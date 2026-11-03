using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LegendaryPen.Core;
using Microsoft.Win32;

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
    private readonly PresentationState _presentation = new();
    private PresentationManager? _presenter;
    private ToolbarWindow? _toolbar;
    private UpdateService? _updates;
    private LiveOptions? _options;
    private TrayIcon? _tray;
    private HotkeyMap? _pendingHotkeys;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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
        _overlays = new OverlayManager(_state, _pen, histories, new LayerStacks(), _options, new AnnotationStore(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Legendary Pen",
            "annotations")));
        _state.Changed += () => _pen.ActivateTool(_state.Tool);
        histories.Changed += () =>
        {
            if (_options.KeepAnnotations)
            {
                _annotationSaveTimer.Stop();
                _annotationSaveTimer.Start();
            }
        };
        _annotationSaveTimer.Tick += (_, _) => SaveAnnotations();
        _overlays.UpdateMonitors(monitors);
        _presenter = new PresentationManager(_presentation, () => _pen.Color);
        _presenter.UpdateMonitors(monitors);

        // Un changement de mode ou d'outil remonte les overlays : les effets doivent repasser au-dessus d'eux, sous la barre.
        _state.Changed += _presenter.Raise;

        // La saisie d'un texte active un overlay, ce qui le remonte : même remède, une fois l'activation faite.
        _overlays.TextEditingChanged += _presenter.Raise;

        // La recherche du texte sous un trait lit l'écran : le voile et la loupe n'en font pas partie.
        _overlays.ExcludeEffectsFromCapture = _presenter.ExcludeFromCapture;

        PixelPoint? savedPosition = _settings is { ToolbarX: { } x, ToolbarY: { } y } ? new PixelPoint(x, y) : null;
        _updates = UpdateService.IsEnabled ? new UpdateService() : null;
        _toolbar = new ToolbarWindow(
            _state,
            _pen,
            histories,
            _overlays,
            _updates,
            monitors,
            savedPosition,
            _settings.ToolbarVertical,
            ToolbarDocking.Sanitize(_settings.ToolbarDock),
            _settings.ToolbarDockMonitor,
            ToolbarDocking.CollapseDelay(_settings.ToolbarCollapseSeconds),
            new HotkeyMap(_settings.Hotkeys),
            _presentation);
        _overlays.BoardMonitor = () => _toolbar.CurrentMonitorName;
        _toolbar.CaptureRequested += RequestCapture;
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
        _toolbar.Show();
        var toolbarHandle = new System.Windows.Interop.WindowInteropHelper(_toolbar).Handle;
        _overlays.KeepBelowToolbar(toolbarHandle);
        _presenter.KeepBelowToolbar(toolbarHandle);

        _tray = new TrayIcon(_toolbar.ToggleBarVisibility, RequestCapture, RequestSignPdf, _presentation, RequestSettings, Shutdown);

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
        _tray?.Dispose();

        if (_saveTimer.IsEnabled)
        {
            SaveSettings();
        }

        if (_options?.KeepAnnotations == true)
        {
            _overlays!.CommitText();
            SaveAnnotations();
        }

        // En dernier : son arrêt peut attendre une seconde le fil de la loupe, l'enregistrement passe avant.
        _presenter?.Dispose();

        _instanceGuard?.Dispose();
        base.OnExit(e);
    }

    // Différé : la demande peut venir d'un raccourci, en plein traitement d'un message Windows.
    private void RequestCapture() => Dispatcher.BeginInvoke(RunCapture);

    private void RequestSettings() => Dispatcher.BeginInvoke(OpenSettings);

    private void RequestSignPdf() => Dispatcher.BeginInvoke(OpenPdfSigning);

    private void RunCapture()
    {
        _overlays!.CommitText();

        var toolbar = _toolbar!;
        BitmapSource? image = null;

        toolbar.RunExclusive(() =>
        {
            var previousOpacity = toolbar.Opacity;
            toolbar.Opacity = 0;

            try
            {
                // Les effets de présentation ne doivent pas figurer dans la capture (PR-8).
                _presenter!.Suspend();

                // La barre doit avoir réellement disparu de l'écran avant la copie.
                WaitFor(TimeSpan.FromMilliseconds(150));
                Win32.WaitForComposition();
                image = new CaptureSession().Run(toolbar.Monitors);
            }
            catch (InvalidOperationException exception)
            {
                MessageWindow.Show(exception.Message);
            }
            finally
            {
                toolbar.Opacity = previousOpacity;
                _presenter!.Resume();
            }

            if (image is not null)
            {
                DeliverCapture(image);
            }
        });
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
                    _tray!.Notify(Loc.T("Capture copiée dans le presse-papiers."));
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
            _tray!.Notify(Loc.T("Capture enregistrée : {0}", Path.GetFileName(path)), () => CaptureDelivery.ShowInExplorer(path));
        }
    }

    private void OpenPdfSigning()
    {
        _overlays!.CommitText();

        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Legendary Pen");

        _toolbar!.RunExclusive(() => new PdfSignWindow(new SignatureStore(Path.Combine(folder, "signature.json"))).ShowDialog());
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

            if (canChangeStartup)
            {
                StartupRegistration.Set(window.StartupEnabled);
            }

            SaveSettings();
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
            _tray?.Notify(Loc.T("Impossible d'enregistrer les annotations."));
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

    private void SaveSettings()
    {
        _saveTimer.Stop();
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

    private static AppSettings LoadSettings(SettingsStore store)
    {
        try
        {
            return store.Load();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ReportSettingsFailure(Loc.T("Lecture impossible : {0}", exception.Message));
            return new AppSettings();
        }
    }

    private static void ReportSettingsFailure(string message)
    {
        MessageWindow.Show(Loc.T("Réglages de Legendary Pen. {0}", message));
    }
}
