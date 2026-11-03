using System.Windows.Ink;
using LegendaryPen.Core;

namespace LegendaryPen.App;

/// <summary>Relie les overlays de tous les écrans à l'état et à l'historique communs.</summary>
public sealed class OverlayManager
{
    private readonly Dictionary<string, OverlayWindow> _overlays = [];
    private readonly AppState _state;
    private readonly PenState _pen;
    private readonly ActionHistory _history;

    public OverlayManager(AppState state, PenState pen, ActionHistory history)
    {
        _state = state;
        _pen = pen;
        _history = history;

        _state.Changed += ApplyState;
        _pen.Changed += ApplyPen;
    }

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
            overlay.Apply(_state);
        }

        foreach (var name in plan.ToCreate)
        {
            _overlays[name] = CreateOverlay(connected[name]);
        }
    }

    public void ClearAll()
    {
        var cleared = _overlays.Values
            .Where(overlay => overlay.Canvas.Strokes.Count > 0)
            .Select(overlay => (overlay.Canvas, new StrokeCollection(overlay.Canvas.Strokes)))
            .ToList();

        if (cleared.Count == 0)
        {
            return;
        }

        var action = new ClearAllAction(cleared);
        action.Redo();
        _history.Record(action);
    }

    private OverlayWindow CreateOverlay(MonitorArea monitor)
    {
        var overlay = new OverlayWindow(monitor);
        var canvas = overlay.Canvas;

        canvas.StrokeCollected += (_, args) => _history.Record(new AddStrokeAction(canvas, args.Stroke));
        overlay.ApplyPen(_pen);
        overlay.Show();
        overlay.Apply(_state);

        return overlay;
    }

    private void ApplyState()
    {
        foreach (var overlay in _overlays.Values)
        {
            if (overlay.IsVisible)
            {
                overlay.Apply(_state);
            }
        }
    }

    private void ApplyPen()
    {
        foreach (var overlay in _overlays.Values)
        {
            overlay.ApplyPen(_pen);
        }
    }
}
