using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LegendaryPen.Core;
using Microsoft.Win32;

// Une DLL homonyme déposée dans le dossier de l'application ou le dossier courant ne doit jamais être chargée.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace LegendaryPen.App;

public partial class App : Application
{
    private readonly DispatcherTimer _displayChangeTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _annotationSaveTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromMinutes(10) };

    private SingleInstanceGuard? _instanceGuard;
    private SettingsStore? _settingsStore;
    private AppSettings _settings = new();
    private AppState? _state;
    private PenState? _pen;
    private OverlayManager? _overlays;
    private readonly LayerStacks _layerStacks = new();
    private readonly PresentationState _presentation = new();
    private PresentationManager? _presenter;
    private readonly RecordingState _recordingState = new();
    private RecordingController? _recording;
    private bool _sessionEndHandled;
    private bool _annotationsSavedOnClose;
    private ToolbarWindow? _toolbar;
    private UpdateService? _updates;
    private LiveOptions? _options;
    private TrayIcon? _tray;
    private NotificationCenter? _notifications;
    private HotkeyMap? _pendingHotkeys;
    private readonly ErrorBurst _errorBurst = new();
    private ErrorJournal? _errorJournal;
    private bool _handlingError;
    private bool _settingsReadFailed;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        InstallErrorHandlers();

        // Un programme lancé sans chemin complet ne doit jamais être cherché dans un dossier choisi par quelqu'un d'autre.
        Environment.CurrentDirectory = Environment.SystemDirectory;

        _instanceGuard = new SingleInstanceGuard();

        if (!_instanceGuard.IsFirstInstance)
        {
            _instanceGuard.RequestRevealInFirstInstance();
            Shutdown();
            return;
        }

        _settingsStore = new SettingsStore(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Legendary Pen",
            "settings.json"));
        _settings = LoadSettings(_settingsStore);
        Loc.Use(_settings.Language, CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);
        WindowLocalizer.Register();

        _state = new AppState();
        _state.RestoreFading(_settings.FadingEnabled);
        var histories = new SurfaceHistories(_state);
        var monitors = Win32.GetMonitors();
        _pen = new PenState(_settings);
        _options = new LiveOptions(_settings);
        _overlays = new OverlayManager(_state, _pen, histories, _layerStacks, _options, new AnnotationStore(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Legendary Pen",
            "annotations")));
        _state.Changed += () => _pen.ActivateTool(_state.Tool);
        void MarkAnnotationsForSave()
        {
            if (_options.KeepAnnotations)
            {
                _annotationSaveTimer.Stop();
                _annotationSaveTimer.Start();
            }
        }

        histories.Changed += MarkAnnotationsForSave;
        _annotationSaveTimer.Tick += (_, _) => SaveAnnotations();
        _overlays.UpdateMonitors(monitors);

        // Les calques font partie des annotations conservées : ajouter, retirer, déplacer, masquer ou choisir un calque compte aussi.
        foreach (var background in Enum.GetValues<BoardBackground>())
        {
            _layerStacks.For(background).Changed += MarkAnnotationsForSave;
        }
        _presenter = new PresentationManager(_presentation, () => _pen.Color);
        _presenter.UpdateMonitors(monitors);

        // Un changement de mode ou d'outil remonte les overlays : les effets doivent repasser au-dessus d'eux, sous la barre.
        _state.Changed += _presenter.Raise;

        // La saisie d'un texte active un overlay, ce qui le remonte : même remède, une fois l'activation faite.
        _overlays.TextEditingChanged += _presenter.Raise;

        // La recherche du texte sous un trait lit l'écran : le voile et la loupe n'en font pas partie.
        _overlays.ExcludeEffectsFromCapture = ExcludeEffectsAndToolbarFromCapture;

        PixelPoint? savedPosition = _settings is { ToolbarX: { } x, ToolbarY: { } y } ? new PixelPoint(x, y) : null;
        _updates = UpdateService.IsEnabled ? new UpdateService() : null;
        _toolbar = new ToolbarWindow(
            _state,
            _pen,
            histories,
            _layerStacks,
            _overlays,
            _updates,
            monitors,
            savedPosition,
            _settings.ToolbarVertical,
            ToolbarDocking.Sanitize(_settings.ToolbarDock),
            _settings.ToolbarDockMonitor,
            ToolbarDocking.CollapseDelay(_settings.ToolbarCollapseSeconds),
            new HotkeyMap(_settings.Hotkeys),
            _presentation,
            _settings.ToolbarSecondRowVisible);
        _notifications = new NotificationCenter(() => (_toolbar.Monitors.FirstOrDefault(monitor => monitor.IsPrimary) ?? _toolbar.Monitors[0]).WorkArea);
        _overlays.BoardMonitor = () => _toolbar.CurrentMonitorName;
        _toolbar.CaptureRequested += RequestCapture;
        _toolbar.OverlayAnchorChanged += anchor =>
        {
            _overlays.KeepBelowToolbar(anchor);
            _presenter.KeepBelowToolbar(anchor);
        };
        _toolbar.SettingsRequested += RequestSettings;
        _toolbar.SignPdfRequested += RequestSignPdf;
        _toolbar.PositionChanged += OnToolbarMoved;
        _toolbar.OrientationChanged += () =>
        {
            _settings = _settings with
            {
                ToolbarVertical = _toolbar.IsVertical,
                ToolbarDock = _toolbar.Dock,
                ToolbarDockMonitor = _toolbar.DockMonitor,
            };
            RestartSaveTimer();
        };
        _toolbar.SecondRowChanged += () =>
        {
            _settings = _settings with { ToolbarSecondRowVisible = _toolbar.SecondRowVisible };
            RestartSaveTimer();
        };
        _toolbar.Show();
        var toolbarHandle = new System.Windows.Interop.WindowInteropHelper(_toolbar).Handle;
        _overlays.KeepBelowToolbar(toolbarHandle);
        _presenter.KeepBelowToolbar(toolbarHandle);

        _recording = new RecordingController(
            _recordingState,
            Dispatcher,
            RecordedMonitor,
            () => _settings,
            _toolbar.AddCaptureExclusion,
            _toolbar.ReleaseCaptureExclusion,
            _notifications.Show);
        _recording.DisplayChanged += ShowRecording;
        _toolbar.RecordRequested += RequestRecord;
        _toolbar.BeforeUpdateInstall = () => _recording.StopAndWaitAsync();
        _toolbar.UpdateAbandoned = () => _recording.Reopen(RecordingClose.Update);
        _toolbar.SessionEndRequested += FinishBeforeSessionEnd;
        _toolbar.SessionEndCancelled += OnSessionEndCancelled;
        ShowRecording();

        _tray = new TrayIcon(_toolbar.ToggleBarVisibility, RequestCapture, RequestRecord, RecordMenuState, RequestSignPdf, _presentation, RequestSettings, Shutdown);

        _pen.Changed += RestartSaveTimer;
        _state.Changed += () =>
        {
            if (_settings.FadingEnabled != _state.FadingEnabled)
            {
                _settings = _settings with { FadingEnabled = _state.FadingEnabled };
                RestartSaveTimer();
            }
        };
        _saveTimer.Tick += (_, _) => SaveSettings();

        // Windows envoie plusieurs notifications par changement d'écran : seule la dernière compte.
        _displayChangeTimer.Tick += (_, _) => ApplyDisplayChange();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        _instanceGuard.ListenForRevealRequests(() => Dispatcher.BeginInvoke(() => _toolbar.ExpandAndReveal()));

        if (_updates is not null)
        {
            UpdateService.DeleteOldDownloads();

            // Au lancement, une mise à jour est proposée ; ensuite, seule la pastille la signale.
            _toolbar.ContentRendered += async (_, _) => await CheckForUpdateAsync(offerImmediately: true);
            _updateTimer.Tick += async (_, _) => await CheckForUpdateAsync(offerImmediately: false);
            _updateTimer.Start();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _displayChangeTimer.Stop();
        _updateTimer.Stop();
        _notifications?.CloseAll();
        _tray?.Dispose();

        SaveOnClose();

        // Après les annotations et les réglages : la vidéo peut demander jusqu'à 15 s.
        var recording = _recording?.StopAndWaitAsync(RecordingClose.Exit) ?? Task.CompletedTask;

        // Son arrêt peut attendre une seconde le fil de la loupe, l'enregistrement passe avant. La vidéo se finit pendant ce temps.
        _presenter?.Dispose();

        WaitForRecording(recording);

        _instanceGuard?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// Fin de session, ou fermeture par le gestionnaire de redémarrage que l'installateur utilise : Windows peut tuer le processus
    /// dès la réponse, avant que OnExit ne tourne. Tout doit donc être fini au retour.
    /// </summary>
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);

        if (!e.Cancel && _recording is not null)
        {
            FinishBeforeSessionEnd();
        }
    }

    // Windows prévient chaque fenêtre de premier niveau : la barre et la fenêtre cachée de WPF passent toutes deux par ici.
    private void FinishBeforeSessionEnd()
    {
        // Déjà en cours plus haut dans la pile, ou déjà fait : cette réponse-ci attend seulement la même vidéo.
        if (_sessionEndHandled)
        {
            WaitForRecording(_recording!.StopAndWaitAsync(RecordingClose.SessionEnd));
            return;
        }

        _sessionEndHandled = true;

        // D'abord : si Windows tue le processus pendant une longue attente, seule la fin de la vidéo est perdue.
        SaveOnClose();

        var recording = _recording!.StopAndWaitAsync(RecordingClose.SessionEnd);
        var toolbar = new System.Windows.Interop.WindowInteropHelper(_toolbar!).Handle;
        var blocked = !recording.IsCompleted && Win32.BlockShutdown(toolbar, Loc.T("Finalisation de la vidéo"));

        try
        {
            WaitForRecording(recording);
        }
        finally
        {
            if (blocked)
            {
                Win32.AllowShutdown(toolbar);
            }
        }
    }

    // Une autre application a refusé la fin de session : celle-ci continue, et une prochaine fin de session repartira de zéro.
    private void OnSessionEndCancelled()
    {
        _sessionEndHandled = false;
        _annotationsSavedOnClose = false;
        Win32.AllowShutdown(new System.Windows.Interop.WindowInteropHelper(_toolbar!).Handle);
        _recording!.Reopen(RecordingClose.SessionEnd);
    }

    // Peut tourner deux fois, à la fin de session puis dans OnExit : une fois enregistrées, les annotations ne sont pas réécrites si près de la coupure.
    private void SaveOnClose()
    {
        if (_saveTimer.IsEnabled)
        {
            SaveSettings();
        }

        if (_options?.KeepAnnotations == true && !_annotationsSavedOnClose)
        {
            _annotationsSavedOnClose = _sessionEndHandled;
            _overlays!.CommitText();
            SaveAnnotations();
        }
    }

    // Différé : la demande peut venir d'un raccourci, en plein traitement d'un message Windows.
    private void RequestCapture() => Dispatcher.BeginInvoke(RunCapture);

    private void RequestSettings() => Dispatcher.BeginInvoke(OpenSettings);

    private void RequestSignPdf() => Dispatcher.BeginInvoke(OpenPdfSigning);

    private void RequestRecord() => Dispatcher.BeginInvoke(() => _recording!.ToggleAsync());

    private MonitorArea RecordedMonitor()
    {
        var monitors = _toolbar!.Monitors;
        var name = _toolbar.CurrentMonitorName;

        return monitors.FirstOrDefault(monitor => monitor.DeviceName == name)
            ?? monitors.FirstOrDefault(monitor => monitor.IsPrimary)
            ?? monitors[0];
    }

    private void ShowRecording()
    {
        var recording = _recording!;
        _toolbar!.ShowRecording(recording.IsFilming, recording.ElapsedText, recording.CommandText, recording.CanCommand, recording.IsActive);
    }

    private (string Text, bool Enabled) RecordMenuState()
    {
        var recording = _recording!;
        var text = recording.Status == RecordingStatus.Recording ? $"{recording.CommandText} ({recording.ElapsedText})" : recording.CommandText;

        return (text, recording.CanCommand);
    }

    // OnExit est synchrone sur le fil de l'interface, et la fin de l'arrêt y repasse : il faut faire tourner les messages pendant l'attente.
    // La marge couvre ce dernier passage, l'arrêt lui-même étant borné à 15 s.
    private static void WaitForRecording(Task recording)
    {
        if (recording.IsCompleted)
        {
            return;
        }

        var frame = new DispatcherFrame();
        _ = Task.WhenAny(recording, Task.Delay(RecordingController.StopLimit + TimeSpan.FromMilliseconds(500)))
            .ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
    }

    private void RunCapture()
    {
        _overlays!.CommitText();

        var toolbar = _toolbar!;
        BitmapSource? image = null;

        toolbar.RunExclusive(() =>
        {
            var previousOpacity = toolbar.Opacity;
            toolbar.Opacity = 0;
            var restorePanel = toolbar.HideLayersForCapture();

            try
            {
                // Les effets de présentation ne doivent pas figurer dans la capture (PR-8).
                _presenter!.Suspend();

                // La barre doit avoir réellement disparu de l'écran avant la copie.
                WaitFor(TimeSpan.FromMilliseconds(150));
                Win32.WaitForComposition();
                image = new CaptureSession().Run(toolbar.Monitors);
            }
            catch (Exception exception) when (exception is InvalidOperationException or ExternalException)
            {
                MessageWindow.Show(exception.Message);
            }
            finally
            {
                toolbar.Opacity = previousOpacity;
                restorePanel();
                _presenter!.Resume();
            }

            if (image is not null)
            {
                DeliverCapture(image);
            }
        });
    }

    // Le panneau des calques n'est pas un effet de présentation, mais il ne doit pas non plus passer pour du contenu de l'écran.
    private IDisposable? ExcludeEffectsAndToolbarFromCapture()
    {
        var effects = _presenter!.ExcludeFromCapture();

        if (effects is null)
        {
            return null;
        }

        if (!_toolbar!.AddCaptureExclusion())
        {
            _toolbar.ReleaseCaptureExclusion();
            effects.Dispose();
            return null;
        }

        return new CaptureRestore(() =>
        {
            _toolbar.ReleaseCaptureExclusion();
            effects.Dispose();
        });
    }

    private sealed class CaptureRestore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    private static void WaitFor(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private void DeliverCapture(BitmapSource image)
    {
        try
        {
            switch (_settings.CaptureDestination)
            {
                case CaptureDestination.Clipboard:
                    CaptureDelivery.CopyToClipboard(image);
                    _notifications!.Show(Loc.T("Capture copiée dans le presse-papiers."));
                    break;

                case CaptureDestination.Ask:
                    NotifySaved(CaptureDelivery.SaveWithDialog(image, _settings.CaptureFolder));
                    break;

                default:
                    NotifySaved(CaptureDelivery.SaveToFolder(image, _settings.CaptureFolder));
                    break;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or COMException)
        {
            MessageWindow.Show(Loc.T("La capture n'a pas pu être enregistrée : {0}", exception.Message));
        }
    }

    private void NotifySaved(string? path)
    {
        if (path is not null)
        {
            _notifications!.Show(Loc.T("Capture enregistrée : {0}", Path.GetFileName(path)), () => CaptureDelivery.ShowInExplorer(path));
        }
    }

    private void OpenPdfSigning()
    {
        _overlays!.CommitText();

        // Hors du profil itinérant et chiffrée pour cet utilisateur : un tracé de signature ne doit pas voyager avec les sauvegardes.
        var store = new SignatureStore(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Legendary Pen", "signature.dat"),
            SignatureProtection.Protect,
            SignatureProtection.Unprotect,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Legendary Pen", "signature.json"));

        _toolbar!.RunExclusive(() => new PdfSignWindow(store).ShowDialog());
    }

    private void OpenSettings()
    {
        _toolbar!.RunExclusive(() =>
        {
            var canChangeStartup = InstallLocation.IsInstalledCopy(Environment.ProcessPath, UpdateService.InstallDirectory);
            var window = new SettingsWindow(_settings, StartupRegistration.IsEnabled(), canChangeStartup);

            if (window.ShowDialog() != true || window.Result is not { } result)
            {
                return;
            }

            _settings = result;
            _options!.Apply(result);
            _toolbar.SetCollapseDelay(ToolbarDocking.CollapseDelay(result.ToolbarCollapseSeconds));
            _pendingHotkeys = new HotkeyMap(result.Hotkeys);
            _pen!.SetRememberSizePerTool(result.SizePerToolEnabled);

            if (result.KeepAnnotations)
            {
                SaveAnnotations();
            }
            else
            {
                _annotationSaveTimer.Stop();
                DeleteSavedAnnotations();
            }

            SaveSettings();

            if (canChangeStartup)
            {
                ApplyStartup(window.StartupEnabled);
            }
        });

        // Les raccourcis se réenregistrent une fois la fenêtre fermée et la suspension levée.
        if (_pendingHotkeys is { } hotkeys)
        {
            _pendingHotkeys = null;
            _toolbar.SetHotkeys(hotkeys);
        }
    }

    private async Task CheckForUpdateAsync(bool offerImmediately)
    {
        var manifest = await _updates!.CheckAsync();

        if (manifest is null)
        {
            return;
        }

        _toolbar!.ShowUpdateAvailable(manifest);

        if (offerImmediately)
        {
            _toolbar.OfferUpdate();
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _displayChangeTimer.Stop();
            _displayChangeTimer.Start();
        });
    }

    private void ApplyDisplayChange()
    {
        _displayChangeTimer.Stop();
        var monitors = Win32.GetMonitors();

        // Windows peut ne signaler aucun écran pendant une transition : le changement suivant corrigera.
        if (monitors.Count > 0)
        {
            _overlays!.UpdateMonitors(monitors);
            _presenter!.UpdateMonitors(monitors);
            _toolbar!.UpdateMonitors(monitors);
        }
    }

    private void OnToolbarMoved()
    {
        var position = _toolbar!.Position;
        _settings = _settings with
        {
            ToolbarX = position.X,
            ToolbarY = position.Y,
            ToolbarDock = _toolbar.Dock,
            ToolbarDockMonitor = _toolbar.DockMonitor,
        };
        _overlays!.Refresh();
        RestartSaveTimer();
    }

    private void RestartSaveTimer()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveAnnotations()
    {
        _annotationSaveTimer.Stop();

        try
        {
            _overlays!.SaveAnnotations();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _notifications?.Show(Loc.T("Impossible d'enregistrer les annotations."));
        }
    }

    private void DeleteSavedAnnotations()
    {
        try
        {
            _overlays!.DeleteSavedAnnotations();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageWindow.Show(Loc.T("Impossible de supprimer les annotations enregistrées.\n\n{0}", exception.Message));
        }
    }

    // Une stratégie du poste peut interdire cette clé : les réglages viennent d'être enregistrés, seul ce choix échoue.
    private static void ApplyStartup(bool enabled)
    {
        try
        {
            StartupRegistration.Set(enabled);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            MessageWindow.Show(Loc.T("Impossible de modifier le démarrage avec Windows.\n\n{0}", exception.Message));
        }
    }

    private void SaveSettings()
    {
        _saveTimer.Stop();

        // Le fichier existe peut-être encore intact : écrire les valeurs par défaut par-dessus le détruirait.
        if (_settingsReadFailed)
        {
            return;
        }

        _settings = _settings with
        {
            QuickColors = _pen!.QuickColors.Select(color => color.ToHex()).ToList(),
            PenColor = _pen.Color.ToHex(),
            PenSize = _pen.Size,
            PreviousPenColor = _pen.PreviousColor?.ToHex(),
            ToolSizes = _pen.ToolSizes,
        };

        try
        {
            _settingsStore!.Save(_settings);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ReportSettingsFailure(Loc.T("Enregistrement impossible : {0}", exception.Message));
        }
    }

    private AppSettings LoadSettings(SettingsStore store)
    {
        try
        {
            return store.Load();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _settingsReadFailed = true;
            ReportSettingsFailure(Loc.T("Lecture impossible : {0}\n\nLes réglages habituels reviendront au prochain démarrage. Les changements faits d'ici là ne seront pas gardés.", exception.Message));
            return new AppSettings();
        }
    }

    private void InstallErrorHandlers()
    {
        _errorJournal = new ErrorJournal(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Legendary Pen",
            "erreurs.log"));

        DispatcherUnhandledException += OnUnhandledInterfaceError;
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            _errorJournal.Write("Task", e.Exception, DateTime.Now);
            e.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledBackgroundError;
    }

    // L'application continue : une vidéo en cours vaut plus qu'un bouton qui n'a pas répondu. Une panne qui se répète ferme proprement, ce qui termine aussi la vidéo.
    private void OnUnhandledInterfaceError(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        _errorJournal?.Write("Interface", e.Exception, DateTime.Now);

        if (_handlingError)
        {
            return;
        }

        _handlingError = true;

        try
        {
            SaveAfterError();

            if (_notifications is null || e.Exception is OutOfMemoryException || _errorBurst.IsTooMany(DateTime.UtcNow))
            {
                MessageWindow.Show(Loc.T("Une erreur empêche Legendary Pen de continuer : l'application va se fermer.\n\nDétails : {0}", _errorJournal?.Path ?? string.Empty));
                Shutdown();
            }
            else
            {
                _notifications.Show(Loc.T("Une erreur inattendue s'est produite. L'application continue."));
            }
        }
        finally
        {
            _handlingError = false;
        }
    }

    // Une erreur hors du fil de l'interface ferme le processus quoi qu'on fasse : il ne reste qu'à en garder la trace et à sauver ce qui peut l'être.
    private void OnUnhandledBackgroundError(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            _errorJournal?.Write("Background", exception, DateTime.Now);
        }

        try
        {
            Dispatcher.Invoke(SaveAfterError, TimeSpan.FromSeconds(2));
        }
        catch (Exception failure) when (failure is TaskCanceledException or InvalidOperationException)
        {
            // Le fil de l'interface est lui-même arrêté ou bloqué.
        }
    }

    // Respecte le choix de ne pas conserver les annotations ; une erreur ici ne doit pas masquer celle qu'on traite.
    private void SaveAfterError()
    {
        try
        {
            if (_pen is not null)
            {
                SaveSettings();
            }

            if (_options?.KeepAnnotations == true)
            {
                _overlays?.CommitText();
                SaveAnnotations();
            }
        }
        catch (Exception failure)
        {
            _errorJournal?.Write("Recovery", failure, DateTime.Now);
        }
    }

    private static void ReportSettingsFailure(string message)
    {
        MessageWindow.Show(Loc.T("Réglages de Legendary Pen. {0}", message));
    }
}
