using System.IO;
using System.Windows;
using System.Windows.Threading;
using LegendaryPen.Core;
using Microsoft.Win32;

namespace LegendaryPen.App;

public partial class App : Application
{
    private readonly DispatcherTimer _displayChangeTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromMinutes(10) };

    private SingleInstanceGuard? _instanceGuard;
    private SettingsStore? _settingsStore;
    private AppSettings _settings = new();
    private PenState? _pen;
    private OverlayManager? _overlays;
    private ToolbarWindow? _toolbar;
    private UpdateService? _updates;

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

        var state = new AppState();
        var history = new ActionHistory();
        var monitors = Win32.GetMonitors();
        _pen = new PenState(_settings);
        _overlays = new OverlayManager(state, _pen, history);
        _overlays.UpdateMonitors(monitors);

        PixelPoint? savedPosition = _settings is { ToolbarX: { } x, ToolbarY: { } y } ? new PixelPoint(x, y) : null;
        _updates = UpdateService.IsEnabled ? new UpdateService() : null;
        _toolbar = new ToolbarWindow(state, _pen, history, _overlays, _updates, monitors, savedPosition);
        _toolbar.PositionChanged += OnToolbarMoved;
        _toolbar.Show();

        _pen.Changed += RestartSaveTimer;
        _saveTimer.Tick += (_, _) => SaveSettings();

        // Windows envoie plusieurs notifications par changement d'écran : seule la dernière compte.
        _displayChangeTimer.Tick += (_, _) => ApplyDisplayChange();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        _instanceGuard.ListenForRevealRequests(() => Dispatcher.BeginInvoke(() => _toolbar.Reveal()));

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

        if (_saveTimer.IsEnabled)
        {
            SaveSettings();
        }

        _instanceGuard?.Dispose();
        base.OnExit(e);
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
            _toolbar!.UpdateMonitors(monitors);
        }
    }

    private void OnToolbarMoved()
    {
        var position = _toolbar!.Position;
        _settings = _settings with { ToolbarX = position.X, ToolbarY = position.Y };
        RestartSaveTimer();
    }

    private void RestartSaveTimer()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveSettings()
    {
        _saveTimer.Stop();
        _settings = _settings with
        {
            QuickColors = _pen!.QuickColors.Select(color => color.ToHex()).ToList(),
            PenColor = _pen.Color.ToHex(),
            PenSize = _pen.Size,
        };

        try
        {
            _settingsStore!.Save(_settings);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ReportSettingsFailure($"Enregistrement impossible : {exception.Message}");
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
            ReportSettingsFailure($"Lecture impossible : {exception.Message}");
            return new AppSettings();
        }
    }

    private static void ReportSettingsFailure(string message)
    {
        MessageBox.Show(
            $"Réglages de Legendary Pen. {message}",
            "Legendary Pen",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }
}
