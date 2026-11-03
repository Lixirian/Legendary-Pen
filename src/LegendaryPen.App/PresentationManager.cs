using System.Windows.Media;
using LegendaryPen.Core;

namespace LegendaryPen.App;

/// <summary>Une fenêtre par écran pour les effets de présentation, actives seulement quand un effet l'est.</summary>
internal sealed class PresentationManager : IDisposable
{
    private readonly PresentationState _state;
    private readonly Dictionary<string, PresentationWindow> _windows = [];
    private readonly HashSet<string> _connected = [];

    private IReadOnlyList<MonitorArea> _monitors = [];
    private MagnifierWindow? _magnifier;
    private IntPtr _toolbarHandle;
    private bool _suspended;
    private bool _subscribed;
    private bool _disposed;

    public PresentationManager(PresentationState state)
    {
        _state = state;
        _state.Changed += Apply;
    }

    /// <summary>Aligne les fenêtres sur les écrans connectés : celle d'un écran débranché est masquée.</summary>
    public void UpdateMonitors(IReadOnlyList<MonitorArea> monitors)
    {
        var connected = monitors.ToDictionary(monitor => monitor.DeviceName);
        var plan = DisplayPlan.Compute(_windows.Keys.ToList(), connected.Keys.ToList());

        foreach (var name in plan.ToHide)
        {
            _windows[name].Hide();
        }

        foreach (var name in plan.ToKeep)
        {
            _windows[name].UpdateMonitor(connected[name]);
        }

        foreach (var name in plan.ToCreate)
        {
            _windows[name] = new PresentationWindow(connected[name]) { ToolbarHandle = _toolbarHandle };
        }

        _connected.Clear();
        _connected.UnionWith(connected.Keys);
        _monitors = monitors;
        _magnifier?.SetMonitors(monitors);
        Apply();
    }

    /// <summary>Garantit que les fenêtres, présentes et futures, restent sous la barre d'outils.</summary>
    public void KeepBelowToolbar(IntPtr toolbar)
    {
        _toolbarHandle = toolbar;
        _magnifier?.KeepBelow(toolbar);

        foreach (var window in _windows.Values)
        {
            window.ToolbarHandle = toolbar;
        }
    }

    /// <summary>Met les effets en pause, par exemple pendant une capture.</summary>
    public void Suspend()
    {
        _suspended = true;
        Apply();
    }

    public void Resume()
    {
        _suspended = false;
        Apply();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _state.Changed -= Apply;
        SetSubscribed(false);

        foreach (var window in _windows.Values)
        {
            window.Close();
        }

        _windows.Clear();
        _magnifier?.Dispose();
    }

    private void Apply()
    {
        if (_disposed)
        {
            return;
        }

        var run = PresentationState.ShouldRun(_state.AnyActive, _suspended);

        // Magnification.dll ne se charge qu'à partir d'ici. Sans elle, la loupe s'éteint : ce basculement relance Apply.
        if (run && _state.MagnifierOn && (_magnifier ??= MagnifierWindow.TryStart(_monitors, _toolbarHandle, OnMagnifierUnavailable)) is null)
        {
            _state.ToggleMagnifier();
            return;
        }

        foreach (var (name, window) in _windows)
        {
            if (run && _connected.Contains(name))
            {
                if (!window.IsVisible)
                {
                    window.Show();
                }

                window.Raise();
            }
            else if (window.IsVisible)
            {
                window.Hide();
            }
        }

        SetSubscribed(run);

        if (run)
        {
            RenderFrame();
        }

        if (run && _state.MagnifierOn)
        {
            _magnifier!.Show();
        }
        else
        {
            _magnifier?.Hide();
        }
    }

    private void SetSubscribed(bool subscribed)
    {
        if (subscribed == _subscribed)
        {
            return;
        }

        _subscribed = subscribed;

        if (subscribed)
        {
            CompositionTarget.Rendering += OnRendering;
        }
        else
        {
            CompositionTarget.Rendering -= OnRendering;
        }
    }

    private void OnRendering(object? sender, EventArgs e) => RenderFrame();

    private void RenderFrame()
    {
        var pointer = Win32.GetPointer();

        foreach (var window in _windows.Values)
        {
            if (window.IsVisible)
            {
                window.Render(pointer, _state.SpotlightOn);
            }
        }
    }

    // Le fil de la loupe a échoué : aucune nouvelle tentative, TryStart renverra null.
    private void OnMagnifierUnavailable()
    {
        _magnifier?.Dispose();
        _magnifier = null;

        if (!_disposed && _state.MagnifierOn)
        {
            _state.ToggleMagnifier();
        }
    }
}
