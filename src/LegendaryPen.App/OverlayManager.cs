using LegendaryPen.Core;

namespace LegendaryPen.App;

/// <summary>Relie les overlays de tous les écrans à l'état et aux historiques communs.</summary>
public sealed class OverlayManager
{
    private readonly Dictionary<string, OverlayWindow> _overlays = [];
    private readonly AppState _state;
    private readonly PenState _pen;
    private readonly SurfaceHistories _histories;
    private readonly FadeTiming _fadeTiming;

    private OverlayWindow? _keyboardOverlay;

    public OverlayManager(AppState state, PenState pen, SurfaceHistories histories, FadeTiming fadeTiming)
    {
        _state = state;
        _pen = pen;
        _histories = histories;
        _fadeTiming = fadeTiming;

        _state.Changed += ApplyToVisibleOverlays;
        _pen.Changed += ApplyToVisibleOverlays;
    }

    /// <summary>Vrai pendant la saisie d'un texte : le clavier doit rester libre.</summary>
    public bool IsEditingText { get; private set; }

    public event Action? TextEditingChanged;

    /// <summary>Aligne les overlays sur les écrans connectés, sans perdre aucune annotation.</summary>
    public void UpdateMonitors(IReadOnlyList<MonitorArea> monitors)
    {
        var connected = monitors.ToDictionary(monitor => monitor.DeviceName);
        var plan = DisplayPlan.Compute(_overlays.Keys.ToList(), connected.Keys.ToList());

        foreach (var name in plan.ToHide)
        {
            _overlays[name].Hide();
        }

        foreach (var name in plan.ToKeep)
        {
            var overlay = _overlays[name];
            overlay.UpdateMonitor(connected[name]);
            overlay.Show();
            overlay.Apply();
        }

        foreach (var name in plan.ToCreate)
        {
            _overlays[name] = CreateOverlay(connected[name]);
        }
    }

    public void ClearAll()
    {
        var cleared = _overlays.Values
            .Select(overlay => overlay.TakeSnapshotOfCurrentSurface())
            .OfType<SurfaceSnapshot>()
            .ToList();

        if (cleared.Count == 0)
        {
            return;
        }

        var action = new ClearAllAction(cleared);
        action.Redo();
        _histories.Current.Record(action);
    }

    private OverlayWindow CreateOverlay(MonitorArea monitor)
    {
        OverlayWindow? overlay = null;

        overlay = new OverlayWindow(monitor, _state, _pen, _histories, _fadeTiming, editing => OnTextEditing(overlay!, editing));
        overlay.Show();
        overlay.Apply();

        return overlay;
    }

    private void OnTextEditing(OverlayWindow overlay, bool editing)
    {
        if (editing)
        {
            _keyboardOverlay = overlay;
            overlay.SetKeyboardInput(true);
        }
        else if (_keyboardOverlay == overlay)
        {
            _keyboardOverlay = null;
            overlay.SetKeyboardInput(false);
        }

        IsEditingText = _keyboardOverlay is not null;
        TextEditingChanged?.Invoke();
    }

    private void ApplyToVisibleOverlays()
    {
        foreach (var overlay in _overlays.Values)
        {
            if (overlay.IsVisible)
            {
                overlay.Apply();
            }
        }
    }
}
