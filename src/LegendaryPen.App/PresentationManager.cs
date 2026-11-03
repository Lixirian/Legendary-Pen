using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Threading;
using LegendaryPen.Core;

namespace LegendaryPen.App;

/// <summary>Une fenêtre par écran pour les effets de présentation, actives seulement quand un effet l'est.</summary>
internal sealed class PresentationManager : IDisposable
{
    private static readonly Color RightClickColor = Color.FromRgb(0xD0, 0xD0, 0xD0);

    // Plus court que le pas de la minuterie de Windows : le réveil tombe sur chaque pas, le plafond se règle ensuite par tranche.
    private static readonly TimeSpan PointerPollInterval = TimeSpan.FromMilliseconds(10);

    private readonly PresentationState _state;
    private readonly Func<InkColor> _leftClickColor;
    private readonly Func<PixelPoint> _readPointer;
    private readonly Dictionary<string, PresentationWindow> _windows = [];
    private readonly HashSet<string> _connected = [];
    private readonly Stopwatch _pointerClock = Stopwatch.StartNew();

    private IReadOnlyList<MonitorArea> _monitors = [];
    private MagnifierWindow? _magnifier;
    private MouseClickHook? _clickHook;
    private DispatcherTimer? _pointerTimer;
    private long _pointerSlot = -1;
    private IntPtr _toolbarHandle;
    private bool _suspended;
    private bool _subscribed;
    private bool _disposed;

    /// <param name="leftClickColor">Couleur de l'anneau du clic gauche, lue à chaque clic : celle du stylo.</param>
    /// <param name="readPointer">Source de la position du pointeur ; par défaut, la vraie souris.</param>
    public PresentationManager(PresentationState state, Func<InkColor> leftClickColor, Func<PixelPoint>? readPointer = null)
    {
        _state = state;
        _leftClickColor = leftClickColor;
        _readPointer = readPointer ?? Win32.GetPointer;
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

    /// <summary>Remonte les fenêtres juste sous la barre d'outils, après un changement d'empilement des overlays.</summary>
    public void Raise()
    {
        if (_disposed)
        {
            return;
        }

        foreach (var window in _windows.Values)
        {
            if (window.IsVisible)
            {
                window.Raise();
            }
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
        SetPointerTracking(false);
        SetClickHook(false);

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

        SetClickHook(run && _state.ClickEffectsOn);

        if (run)
        {
            RenderFrame();
        }

        SetSubscribed(run && NeedsFrames());
        SetPointerTracking(run && _state.SpotlightOn);

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

    // S'abonner à Rendering fait travailler le rendu à chaque image : seul un anneau vivant le justifie.
    private bool NeedsFrames() => _windows.Values.Any(window => window.IsVisible && window.HasRipples);

    private void OnRendering(object? sender, EventArgs e)
    {
        foreach (var window in _windows.Values)
        {
            if (window.IsVisible)
            {
                window.RenderRipples();
            }
        }

        if (!NeedsFrames())
        {
            SetSubscribed(false);
        }
    }

    // Le projecteur suit le pointeur par une minuterie : un pointeur immobile ne provoque aucun rendu.
    private void SetPointerTracking(bool tracking)
    {
        if (tracking)
        {
            _pointerTimer ??= CreatePointerTimer();
            _pointerTimer.Start();
        }
        else
        {
            _pointerTimer?.Stop();
        }
    }

    private DispatcherTimer CreatePointerTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = PointerPollInterval };
        timer.Tick += OnPointerTick;
        return timer;
    }

    private void OnPointerTick(object? sender, EventArgs e)
    {
        var slot = PresentationGeometry.SpotlightSlot(_pointerClock.Elapsed);

        if (slot == _pointerSlot)
        {
            return;
        }

        _pointerSlot = slot;
        var pointer = _readPointer();

        foreach (var window in _windows.Values)
        {
            if (window.IsVisible)
            {
                window.RenderSpotlight(pointer, _state.SpotlightOn);
            }
        }
    }

    private void RenderFrame()
    {
        var pointer = _readPointer();

        foreach (var window in _windows.Values)
        {
            if (window.IsVisible)
            {
                window.Render(pointer, _state.SpotlightOn);
            }
        }
    }

    // Le crochet n'existe que pendant que les effets de clic sont actifs.
    private void SetClickHook(bool installed)
    {
        if (installed == (_clickHook is not null))
        {
            return;
        }

        if (installed)
        {
            _clickHook = new MouseClickHook(OnClickHookUnavailable);
            _clickHook.Clicked += OnClicked;
            return;
        }

        _clickHook!.Clicked -= OnClicked;
        _clickHook.Dispose();
        _clickHook = null;

        foreach (var window in _windows.Values)
        {
            window.ClearRipples();
        }
    }

    private void OnClicked(PixelPoint point, bool isRight)
    {
        var window = _windows.Where(pair => _connected.Contains(pair.Key)).Select(pair => pair.Value).FirstOrDefault(candidate => candidate.IsVisible && candidate.Contains(point));

        if (window is null)
        {
            return;
        }

        window.AddRipple(point, isRight ? RightClickColor : ToColor(_leftClickColor()));
        window.Render(_readPointer(), _state.SpotlightOn);
        SetSubscribed(true);
    }

    private static Color ToColor(InkColor color) => Color.FromRgb(color.R, color.G, color.B);

    // Windows a refusé le crochet : les effets de clic s'éteignent, une seule fois.
    private void OnClickHookUnavailable()
    {
        _clickHook?.Dispose();
        _clickHook = null;

        if (!_disposed && _state.ClickEffectsOn)
        {
            _state.ToggleClickEffects();
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
