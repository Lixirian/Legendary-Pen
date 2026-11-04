using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using LegendaryPen.Core;
using Microsoft.Win32;

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

    // Windows n'attend pas longtemps avant la veille : assez pour finir la vidéo d'ordinaire, sans retenir la mise en veille.
    private static readonly TimeSpan SuspendWait = TimeSpan.FromMilliseconds(1500);

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

    // Une commande encore en file ne doit pas relancer une vidéo dans une application qui se ferme.
    private readonly RecordingCloseLatch _close = new();

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
        IsSupported = SupportedOrFalse(() => ScreenRecorder.IsSupported);

        _state.Changed += () => DisplayChanged?.Invoke();
        _ticker.Tick += OnTick;
        _diskTimer.Tick += OnDiskTimer;
    }

    /// <summary>Point d'essai du banc : reçoit l'enregistreur de chaque essai avant son démarrage. Inactif en usage normal.</summary>
    internal Action<ScreenRecorder>? RecorderCreated { get; set; }

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
    /// Arrête ce qui tourne et attend le fichier final, 15 s au plus. Plus rien ne démarre ensuite, sauf après <see cref="Reopen"/>.
    /// Peut être appelé plusieurs fois : chaque appel attend le même arrêt.
    /// </summary>
    /// <param name="reason">
    /// Fin de session ou sortie : aucun message, et aucune dépendance à <see cref="ScreenRecorder.Ended"/>, qui passe par le fil de l'interface.
    /// Mise à jour : le message de fin part comme d'habitude, car elle peut encore échouer ou être annulée.
    /// </param>
    public async Task StopAndWaitAsync(RecordingClose reason = RecordingClose.Update)
    {
        _close.Enter(reason);

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
        // Le verrou de fermeture ne se lit que sur le fil de l'interface.
        if (stopped && !await _dispatcher.InvokeAsync(() => _close.IsQuiet))
        {
            await Task.WhenAny(run.Finished.Task, Task.Delay(EndedGrace)).ConfigureAwait(false);
        }

        await _dispatcher.InvokeAsync(() => FinishRun(run, _close.IsQuiet ? null : run.ForcedReason ?? RecordingEndReason.Failed));
    }

    /// <summary>La mise à jour ou la fin de session n'aura pas lieu : l'application continue, et on peut de nouveau enregistrer, sauf si autre chose la ferme.</summary>
    public void Reopen(RecordingClose reason) => _close.Leave(reason);

    /// <summary>
    /// La veille arrive : arrête ce qui tourne pour que la vidéo se finisse avant, et attend un peu. Un décompte est annulé, pour
    /// qu'aucun enregistrement ne démarre tout seul au réveil. Appelé par <see cref="OnPowerModeChanged"/>, et par le banc d'essai.
    /// </summary>
    internal void StopForSuspend()
    {
        if (_run is not { } run)
        {
            return;
        }

        if (_state.Status == RecordingStatus.Countdown)
        {
            FinishRun(run, null);
        }
        else if (_state.Status == RecordingStatus.Recording && run.Recorder is { } recorder)
        {
            _ = StopRunAsync(run);

            // L'enregistreur finit sur ses propres fils : cette attente ne dépend pas du fil de l'interface.
            recorder.StopAsync().Wait(SuspendWait);
        }
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Suspend)
        {
            return;
        }

        // Une exception sortirait dans le fil des événements système et fermerait l'application.
        try
        {
            _dispatcher.Invoke(StopForSuspend, DispatcherPriority.Send, CancellationToken.None, SuspendWait + SuspendWait);
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
        }
    }

    internal static bool SupportedOrFalse(Func<bool> probe)
    {
        try
        {
            return probe();
        }
        catch (Exception exception)
        {
            // Une API système absente ou en panne ne doit pas empêcher l'application de démarrer.
            Debug.WriteLine(exception);
            return false;
        }
    }

    private async Task BeginAsync()
    {
        if (_close.RefusesStart)
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
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        run.WatchesPower = true;

        try
        {
            run.HoldsExclusion = true;

            // Sans l'exclusion, la barre serait dans la vidéo (VR-3) : mieux vaut ne pas démarrer.
            if (!_excludeFromCapture())
            {
                FinishRun(run, null);
                _notify(Loc.T("Windows refuse de retirer la barre de la vidéo : enregistrement annulé."), null);
                return;
            }

            var settings = _settings();
            var captureFolder = settings.CaptureFolder;

            // Lu une seule fois : le décompte annonce le son que la vidéo aura vraiment.
            run.Audio = settings.RecordingAudio;
            run.Folder = string.IsNullOrWhiteSpace(captureFolder) ? CaptureDelivery.DefaultFolder : captureFolder;

            // Un dossier réseau peut répondre lentement : l'interface n'attend pas. Une commande peut annuler pendant ce temps.
            var folder = run.Folder;
            var prepared = await Task.Run(() => Prepare(folder));

            if (!ReferenceEquals(_run, run))
            {
                return;
            }

            // La place d'abord : sur un disque plein, le dossier refuse aussi le fichier d'essai, et ce message-ci dit la vraie cause.
            if (RecordingRules.ShouldStopForDisk(prepared.FreeBytes))
            {
                FinishRun(run, null);
                _notify(Loc.T("Plus assez de place sur le disque : enregistrement arrêté."), null);
                return;
            }

            if (prepared.Path is null)
            {
                FinishRun(run, null);
                MessageWindow.Show(Loc.T("Dossier de capture inaccessible."));
                return;
            }

            run.Path = prepared.Path;
            run.Remaining = CountdownSeconds;
            run.Countdown = new RecordingCountdownWindow(run.Monitor, CountdownSeconds, run.Audio);
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
            var free = Win32.FreeBytes(folder);

            return RecordingFile.IsWritableFolder(folder)
                ? new Prepared(RecordingRules.FilePath(folder, DateTime.Now, File.Exists), free)
                : new Prepared(null, free);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Debug.WriteLine(exception);
            return new Prepared(null, null);
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
            RecorderCreated?.Invoke(recorder);
            recorder.Ended += reason => _dispatcher.BeginInvoke(() => OnEnded(run, reason));
            recorder.SoundLost += () => _dispatcher.BeginInvoke(() => AnnounceSound(run));
            _diskTimer.Start();

            // Le réglage de veille de Windows ne doit pas couper une vidéo : levé à la fin de l'essai, sur ce même fil.
            Win32.KeepAwake(true);
            run.KeepsAwake = true;

            await recorder.StartAsync(run.Monitor, run.Path, run.Audio);
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
            AnnounceSound(run);
        }
    }

    // Tout de suite : découvrir à la fin qu'une longue vidéo est muette serait trop tard. La fin le redira.
    // Une seule fois, le son d'une reprise se décidant après le démarrage. Avant les premières images, le démarrage s'en charge ; après l'arrêt, la fin.
    private void AnnounceSound(Run run)
    {
        if (run.SoundAnnounced || !ReferenceEquals(_run, run) || _state.Status != RecordingStatus.Recording || !run.Clock.IsRunning)
        {
            return;
        }

        if (RecordingRules.StartSoundMessage(run.Recorder!.AudioProblem) is { } sound)
        {
            run.SoundAnnounced = true;
            _notify(Loc.T(sound), null);
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

            if (reason is { } ended && !_close.IsQuiet)
            {
                NotifyEnd(run, ended);
            }
        }
        finally
        {
            _run = null;
            run.Finished.TrySetResult();

            if (run.KeepsAwake)
            {
                Win32.KeepAwake(false);
            }

            if (run.WatchesPower)
            {
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            }

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
        var recorder = run.Recorder;
        var path = recorder?.SavedPath;

        if (FormatEnd(reason, path, recorder?.AudioProblem ?? RecordingAudioProblem.None, recorder?.MayBeIncomplete ?? false) is { } message)
        {
            _notify(message, ShowFile(recorder, path));
        }

        // Après un abandon, l'encodeur peut encore finir : le fichier annoncé peut alors changer de nom, ou apparaître.
        if (reason == RecordingEndReason.Failed && recorder is not null && (path is null || recorder.MayBeIncomplete))
        {
            _ = ReportLateFileAsync(recorder, path);
        }
    }

    private async Task ReportLateFileAsync(ScreenRecorder recorder, string? announced)
    {
        await Task.Delay(LateFileCheck);

        var path = recorder.SavedPath;

        // Un fichier annoncé peut aussi avoir disparu : un vrai disque plein, découvert quand l'encodeur a enfin fini, le supprime.
        if (!_close.IsQuiet && path != announced && FormatEnd(RecordingEndReason.Failed, path, recorder.AudioProblem, recorder.MayBeIncomplete) is { } message)
        {
            _notify(message, ShowFile(recorder, path));
        }
    }

    // Le nom seul : le chemin complet déborderait de la notification, et le clic montre le dossier.
    private static string? FormatEnd(RecordingEndReason reason, string? path, RecordingAudioProblem audio, bool mayBeIncomplete)
    {
        var keys = RecordingRules.EndMessages(reason, path is not null, audio, mayBeIncomplete);
        var name = Path.GetFileName(path) ?? string.Empty;

        return keys.Count == 0 ? null : string.Join("\n", keys.Select(key => Loc.T(key, name)));
    }

    // Lu au clic : le « .tmp » annoncé a pu prendre son nom final entre-temps.
    private static Action? ShowFile(ScreenRecorder? recorder, string? path) =>
        path is null ? null : () => CaptureDelivery.ShowInExplorer(recorder?.SavedPath ?? path);

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

    // Sans chemin, le dossier est inaccessible.
    private readonly record struct Prepared(string? Path, long? FreeBytes);

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

        public RecordingAudio Audio { get; set; }

        public bool KeepsAwake { get; set; }

        public bool WatchesPower { get; set; }

        public bool HoldsExclusion { get; set; }

        public bool CheckingDisk { get; set; }

        public bool SoundAnnounced { get; set; }
    }
}
