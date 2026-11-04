using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using LegendaryPen.Core;

namespace LegendaryPen.App;

/// <summary>
/// Conduit un enregistrement, de la commande au message de fin : décompte, exclusion de la barre, place disque, arrêt.
/// Tous les appels à <see cref="RecordingState"/> se font sur le fil de l'interface.
/// </summary>
internal sealed class RecordingController
{
    /// <summary>Attente maximale d'un arrêt à la fermeture ou avant une mise à jour (VR-7).</summary>
    public static readonly TimeSpan StopLimit = TimeSpan.FromSeconds(15);

    private const int CountdownSeconds = 3;

    private static readonly TimeSpan DiskCheckInterval = TimeSpan.FromSeconds(10);

    // Après un abandon, l'encodeur peut encore finir et rendre une vidéo : l'enregistreur le dit alors dans SavedPath.
    private static readonly TimeSpan LateFileCheck = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan EndedGrace = TimeSpan.FromSeconds(1);

    private readonly RecordingState _state;
    private readonly Dispatcher _dispatcher;
    private readonly Func<MonitorArea> _recordedMonitor;
    private readonly Func<AppSettings> _settings;
    private readonly Func<bool> _excludeFromCapture;
    private readonly Action _restoreCapture;
    private readonly Action<string, Action?> _notify;
    private readonly DispatcherTimer _ticker = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _diskTimer = new() { Interval = DiskCheckInterval };

    private Run? _run;

    // À la fermeture, l'icône de notification n'existe plus : plus aucun message.
    private bool _quiet;

    // L'application se ferme ou va s'installer : une commande encore en file ne doit pas relancer une vidéo.
    private bool _closing;

    /// <param name="recordedMonitor">L'écran à enregistrer, lu au moment de la commande.</param>
    /// <param name="excludeFromCapture">Retire la barre et son panneau des captures ; faux si Windows refuse. À équilibrer par <paramref name="restoreCapture"/>, même après un refus.</param>
    /// <param name="notify">Affiche un message, avec l'action à lancer si on clique dessus.</param>
    public RecordingController(
        RecordingState state,
        Dispatcher dispatcher,
        Func<MonitorArea> recordedMonitor,
        Func<AppSettings> settings,
        Func<bool> excludeFromCapture,
        Action restoreCapture,
        Action<string, Action?> notify)
    {
        _state = state;
        _dispatcher = dispatcher;
        _recordedMonitor = recordedMonitor;
        _settings = settings;
        _excludeFromCapture = excludeFromCapture;
        _restoreCapture = restoreCapture;
        _notify = notify;
        IsSupported = ScreenRecorder.IsSupported;

        _state.Changed += () => DisplayChanged?.Invoke();
        _ticker.Tick += OnTick;
        _diskTimer.Tick += OnDiskTimer;
    }

    /// <summary>L'état ou la durée affichée ont changé.</summary>
    public event Action? DisplayChanged;

    public bool IsSupported { get; }

    public bool IsActive => _state.Status != RecordingStatus.Idle;

    /// <summary>Vrai dès que la capture filme vraiment, et jusqu'à la fin de la vidéo ; le démarrage peut durer 15 s.</summary>
    public bool IsFilming => _run?.Clock is { } clock && (clock.IsRunning || clock.Elapsed > TimeSpan.Zero);

    public RecordingStatus Status => _state.Status;

    /// <summary>Durée filmée, affichable ; elle ne compte qu'une fois la capture réellement partie.</summary>
    public string ElapsedText => RecordingRules.FormatElapsed(_run?.Clock.Elapsed ?? TimeSpan.Zero);

    /// <summary>Vrai si une commande peut être donnée maintenant.</summary>
    public bool CanCommand => IsSupported && _state.Status != RecordingStatus.Finishing;

    /// <summary>Ce que fait la commande maintenant, ou pourquoi elle est indisponible.</summary>
    public string CommandText
    {
        get
        {
            if (!IsSupported)
            {
                return Loc.T("Enregistrement impossible sur ce PC.");
            }

            return _state.Status switch
            {
                RecordingStatus.Countdown => Loc.T("Annuler le décompte"),
                RecordingStatus.Recording => Loc.T("Arrêter l'enregistrement"),
                RecordingStatus.Finishing => Loc.T("Finalisation de la vidéo…"),
                _ => Loc.T("Enregistrer l'écran"),
            };
        }
    }

    /// <summary>Démarre le décompte, l'annule ou arrête l'enregistrement, selon l'état.</summary>
    public Task ToggleAsync()
    {
        switch (_state.Status)
        {
            case RecordingStatus.Idle:
                return BeginAsync();

            case RecordingStatus.Countdown when _run is { } counting:
                FinishRun(counting, null);
                return Task.CompletedTask;

            case RecordingStatus.Recording when _run is { } recording:
                return StopRunAsync(recording);

            default:
                return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Arrête ce qui tourne et attend le fichier final, 15 s au plus. Plus rien ne démarre ensuite, sauf après <see cref="ReopenAfterAbandonedUpdate"/>.
    /// </summary>
    /// <param name="quiet">
    /// Fermeture de l'application : aucun message, et aucune dépendance à <see cref="ScreenRecorder.Ended"/>, qui passe par le fil de l'interface.
    /// Sinon, le message de fin part comme d'habitude : une mise à jour peut encore échouer ou être annulée.
    /// </param>
    public async Task StopAndWaitAsync(bool quiet = false)
    {
        _closing = true;
        _quiet |= quiet;

        if (_run is not { } run)
        {
            return;
        }

        if (_state.Status == RecordingStatus.Countdown)
        {
            FinishRun(run, null);
            return;
        }

        StopTimers();
        run.Clock.Stop();
        _state.RequestStop();

        // Hors du fil de l'interface : la fermeture de l'application attend ce résultat en faisant tourner ses messages.
        var stopped = false;

        if (run.Recorder is { } recorder)
        {
            var stopping = recorder.StopAsync();
            stopped = await Task.WhenAny(stopping, Task.Delay(StopLimit)).ConfigureAwait(false) == stopping;
        }

        // Ended donne la vraie raison, juste après la fin de l'arrêt. S'il ne vient pas, l'échec est la seule réponse honnête.
        if (!_quiet && stopped)
        {
            await Task.WhenAny(run.Finished.Task, Task.Delay(EndedGrace)).ConfigureAwait(false);
        }

        await _dispatcher.InvokeAsync(() => FinishRun(run, _quiet ? null : run.ForcedReason ?? RecordingEndReason.Failed));
    }

    /// <summary>La mise à jour est annulée ou n'a pas démarré : l'application continue, on peut de nouveau enregistrer.</summary>
    public void ReopenAfterAbandonedUpdate()
    {
        // Si l'application se ferme entre-temps, le verrou reste.
        _closing = _quiet;
    }

    private async Task BeginAsync()
    {
        if (_closing)
        {
            return;
        }

        if (!IsSupported)
        {
            _notify(Loc.T("Enregistrement impossible sur ce PC."), null);
            return;
        }

        if (!_state.TryStart())
        {
            return;
        }

        var run = new Run(_recordedMonitor());
        _run = run;
        var counting = false;

        try
        {
            run.HoldsExclusion = true;

            // Sans l'exclusion, la barre serait dans la vidéo (VR-3) : mieux vaut ne pas démarrer.
            if (!_excludeFromCapture())
            {
                FinishRun(run, null);
                _notify(Loc.T("Enregistrement impossible sur ce PC."), null);
                return;
            }

            var captureFolder = _settings().CaptureFolder;
            run.Folder = string.IsNullOrWhiteSpace(captureFolder) ? CaptureDelivery.DefaultFolder : captureFolder;

            // Un dossier réseau peut répondre lentement : l'interface n'attend pas. Une commande peut annuler pendant ce temps.
            var folder = run.Folder;
            var prepared = await Task.Run(() => Prepare(folder));

            if (!ReferenceEquals(_run, run))
            {
                return;
            }

            if (prepared.Error is { } error)
            {
                FinishRun(run, null);
                MessageWindow.Show(Loc.T("Le dossier des vidéos n'a pas pu être créé : {0}", error));
                return;
            }

            if (RecordingRules.ShouldStopForDisk(prepared.FreeBytes))
            {
                FinishRun(run, null);
                _notify(Loc.T("Plus assez de place sur le disque : enregistrement arrêté."), null);
                return;
            }

            run.Path = prepared.Path!;
            run.Remaining = CountdownSeconds;
            run.Countdown = new RecordingCountdownWindow(run.Monitor, CountdownSeconds);
            run.Countdown.Show();
            _ticker.Start();
            counting = true;
        }
        finally
        {
            // Une erreur imprévue ne doit laisser ni l'état bloqué ni la barre exclue.
            if (!counting)
            {
                FinishRun(run, null);
            }
        }
    }

    private static Prepared Prepare(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            return new Prepared(RecordingRules.FilePath(folder, DateTime.Now, File.Exists), Win32.FreeBytes(folder), null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new Prepared(null, null, exception.Message);
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_run is not { } run)
        {
            return;
        }

        if (_state.Status != RecordingStatus.Countdown)
        {
            DisplayChanged?.Invoke();
        }
        else if (--run.Remaining > 0)
        {
            run.Countdown?.ShowRemaining(run.Remaining);
        }
        else
        {
            _ = StartRecordingAsync(run);
        }
    }

    private async Task StartRecordingAsync(Run run)
    {
        _ticker.Stop();

        try
        {
            CloseCountdown(run);
            _state.CountdownElapsed();

            // Dès maintenant, l'état est « en cours » : une commande arrive à StopAsync, sans effet de bord pendant le démarrage.
            var recorder = new ScreenRecorder();
            run.Recorder = recorder;
            recorder.Ended += reason => _dispatcher.BeginInvoke(() => OnEnded(run, reason));
            _diskTimer.Start();

            await recorder.StartAsync(run.Monitor, run.Path, _settings().RecordingAudio);
        }
        catch (Exception exception)
        {
            // L'enregistreur n'a pas démarré et ne préviendra pas : personne d'autre ne débloquerait l'état.
            Debug.WriteLine(exception);
            OnEnded(run, RecordingEndReason.Failed);
            return;
        }

        // Le démarrage peut durer jusqu'à 15 s : la fin, ou un arrêt, ont pu passer avant.
        if (ReferenceEquals(_run, run) && _state.Status == RecordingStatus.Recording)
        {
            run.Clock.Start();
            _ticker.Start();
            DisplayChanged?.Invoke();
        }
    }

    private async void OnDiskTimer(object? sender, EventArgs e)
    {
        if (_run is not { CheckingDisk: false } run)
        {
            return;
        }

        // Un dossier réseau perdu peut bloquer la requête : jamais sur le fil de l'interface, et une seule à la fois.
        run.CheckingDisk = true;

        // Méthode async void : une exception qui en sortirait fermerait l'application.
        try
        {
            var folder = run.Folder;
            var free = await Task.Run(() => Win32.FreeBytes(folder));
            run.CheckingDisk = false;

            // Une requête en échec ne dit rien de la place : on continue, et on réessaiera.
            if (ReferenceEquals(_run, run) && _state.Status == RecordingStatus.Recording && RecordingRules.ShouldStopForDisk(free))
            {
                run.ForcedReason = RecordingEndReason.DiskFull;
                await StopRunAsync(run);
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
            run.CheckingDisk = false;
        }
    }

    // La fin arrive ensuite par Ended, qui donne la raison.
    private async Task StopRunAsync(Run run)
    {
        StopTimers();
        run.Clock.Stop();
        _state.RequestStop();

        try
        {
            await run.Recorder!.StopAsync();
        }
        catch (Exception exception)
        {
            // Ended ne viendrait peut-être jamais : l'état ne doit pas rester bloqué.
            Debug.WriteLine(exception);
            OnEnded(run, RecordingEndReason.Failed);
        }
    }

    private void OnEnded(Run run, RecordingEndReason reason) => FinishRun(run, run.ForcedReason ?? reason);

    // Seule sortie de chaque essai. Sans raison, l'essai se ferme sans message.
    private void FinishRun(Run run, RecordingEndReason? reason)
    {
        if (!ReferenceEquals(_run, run))
        {
            return;
        }

        run.Clock.Stop();
        StopTimers();

        try
        {
            CloseCountdown(run);

            if (reason is { } ended && !_quiet)
            {
                NotifyEnd(run, ended);
            }
        }
        finally
        {
            _run = null;
            run.Finished.TrySetResult();

            try
            {
                if (run.HoldsExclusion)
                {
                    _restoreCapture();
                }
            }
            finally
            {
                // Une capture peut finir d'elle-même : il faut alors passer par Finishing pour revenir à Idle.
                _state.RequestStop();
                _state.Finished();
            }
        }
    }

    private void NotifyEnd(Run run, RecordingEndReason reason)
    {
        var path = run.Recorder?.SavedPath;

        if (FormatEnd(reason, path) is { } message)
        {
            _notify(message, ShowFile(path));
        }

        if (reason == RecordingEndReason.Failed && path is null && run.Recorder is { } recorder)
        {
            _ = ReportLateFileAsync(recorder);
        }
    }

    private async Task ReportLateFileAsync(ScreenRecorder recorder)
    {
        await Task.Delay(LateFileCheck);

        if (!_quiet && recorder.SavedPath is { } path && FormatEnd(RecordingEndReason.Failed, path) is { } message)
        {
            _notify(message, ShowFile(path));
        }
    }

    // Le nom seul : le chemin complet déborderait de la notification, et le clic montre le dossier.
    private static string? FormatEnd(RecordingEndReason reason, string? path)
    {
        var keys = RecordingRules.EndMessages(reason, path is not null);
        var name = Path.GetFileName(path) ?? string.Empty;

        return keys.Count == 0 ? null : string.Join("\n", keys.Select(key => Loc.T(key, name)));
    }

    private static Action? ShowFile(string? path) => path is null ? null : () => CaptureDelivery.ShowInExplorer(path);

    private static void CloseCountdown(Run run)
    {
        run.Countdown?.Close();
        run.Countdown = null;
    }

    private void StopTimers()
    {
        _ticker.Stop();
        _diskTimer.Stop();
    }

    private readonly record struct Prepared(string? Path, long? FreeBytes, string? Error);

    /// <summary>Un essai : du lancement du décompte à la fin de la vidéo.</summary>
    private sealed class Run(MonitorArea monitor)
    {
        public MonitorArea Monitor { get; } = monitor;

        public Stopwatch Clock { get; } = new();

        /// <summary>L'essai est clos, message envoyé.</summary>
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Folder { get; set; } = string.Empty;

        public string Path { get; set; } = string.Empty;

        public int Remaining { get; set; }

        public RecordingCountdownWindow? Countdown { get; set; }

        public ScreenRecorder? Recorder { get; set; }

        public RecordingEndReason? ForcedReason { get; set; }

        public bool HoldsExclusion { get; set; }

        public bool CheckingDisk { get; set; }
    }
}
