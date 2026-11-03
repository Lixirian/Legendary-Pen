using LegendaryPen.Core;

namespace LegendaryPen.App;

/// <summary>Relie les overlays de tous les écrans à l'état et aux historiques communs.</summary>
public sealed class OverlayManager
{
    private readonly Dictionary<string, OverlayWindow> _overlays = [];
    private readonly AppState _state;
    private readonly PenState _pen;
    private readonly SurfaceHistories _histories;
    private readonly LiveOptions _options;
    private readonly AnnotationStore _annotations;

    private OverlayWindow? _keyboardOverlay;
    private IntPtr _toolbarHandle;

    public OverlayManager(AppState state, PenState pen, SurfaceHistories histories, LiveOptions options, AnnotationStore annotations)
    {
        _state = state;
        _pen = pen;
        _histories = histories;
        _options = options;
        _annotations = annotations;

        _state.Changed += ApplyToVisibleOverlays;
        _pen.Changed += ApplyToVisibleOverlays;
        _options.Changed += ApplyToVisibleOverlays;
    }

    /// <summary>Indique l'écran de la barre d'outils, pour un tableau limité à un écran.</summary>
    public Func<string?> BoardMonitor { get; set; } = () => null;

    /// <summary>
    /// Retire des captures, le temps d'une lecture de l'écran, ce qui recouvre l'écran sans être du contenu : les effets de
    /// présentation. Renvoie null si c'est impossible.
    /// </summary>
    public Func<IDisposable?> ExcludeEffectsFromCapture { get; set; } = () => new NothingToRestore();

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

    /// <summary>Garantit que les overlays, présents et futurs, restent sous la barre d'outils.</summary>
    public void KeepBelowToolbar(IntPtr toolbar)
    {
        _toolbarHandle = toolbar;

        foreach (var overlay in _overlays.Values)
        {
            overlay.ToolbarHandle = toolbar;
        }
    }

    public void CommitText()
    {
        foreach (var overlay in _overlays.Values)
        {
            overlay.CommitText();
        }
    }

    public void Refresh() => ApplyToVisibleOverlays();

    /// <summary>Enregistre les annotations, y compris celles d'un écran débranché pendant la session.</summary>
    public void SaveAnnotations()
    {
        foreach (var overlay in _overlays.Values)
        {
            overlay.SaveAnnotations(_annotations);
        }
    }

    public void DeleteSavedAnnotations() => _annotations.DeleteAll();

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

        overlay = new OverlayWindow(monitor, _state, _pen, _histories, _options, () => BoardMonitor(), editing => OnTextEditing(overlay!, editing), () => ExcludeEffectsFromCapture())
        {
            ToolbarHandle = _toolbarHandle,
        };

        if (_options.KeepAnnotations)
        {
            overlay.RestoreAnnotations(_annotations);
        }

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

    private sealed class NothingToRestore : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
