using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using LegendaryPen.Core;

namespace LegendaryPen.App;

/// <summary>Fenêtre transparente couvrant exactement un écran.</summary>
internal sealed class OverlayWindow : Window
{
    // WPF ne transmet les clics d'une fenêtre transparente que sur les pixels d'alpha non nul.
    private static readonly Brush InputBackground = CreateFrozenBrush(Color.FromArgb(1, 0, 0, 0));
    private static readonly Brush WhiteboardBackground = CreateFrozenBrush(Colors.White);
    private static readonly Brush BlackboardBackground = CreateFrozenBrush(Color.FromRgb(0x1C, 0x1C, 0x1E));

    private readonly AppState _state;
    private readonly PenState _pen;
    private readonly SurfaceHistories _histories;
    private readonly LiveOptions _options;
    private readonly Func<string?> _boardMonitor;
    private readonly Action<bool> _textEditingChanged;
    private readonly Grid _root = new();
    private readonly Dictionary<BoardBackground, AnnotationSurface> _surfaces = [];

    private MonitorArea _monitor;
    private IntPtr _handle;

    public OverlayWindow(
        MonitorArea monitor,
        AppState state,
        PenState pen,
        SurfaceHistories histories,
        LiveOptions options,
        Func<string?> boardMonitor,
        Action<bool> textEditingChanged)
    {
        _monitor = monitor;
        _state = state;
        _pen = pen;
        _histories = histories;
        _options = options;
        _boardMonitor = boardMonitor;
        _textEditingChanged = textEditingChanged;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Title = "Legendary Pen - Overlay";
        Content = _root;
    }

    /// <summary>La barre d'outils, que l'overlay ne doit jamais recouvrir.</summary>
    public IntPtr ToolbarHandle { get; set; }

    public void UpdateMonitor(MonitorArea monitor)
    {
        _monitor = monitor;

        if (_handle != IntPtr.Zero)
        {
            Win32.SetBounds(_handle, monitor.Bounds);
        }
    }

    /// <summary>Applique l'état commun : fond, clic traversant, surface visible et outil.</summary>
    public void Apply()
    {
        foreach (var surface in _surfaces.Values)
        {
            surface.CommitText();
        }

        var background = EffectiveBackground;
        var acceptsInput = OverlayInputRules.AcceptsInput(_state.AnnotationsVisible, _state.Mode, background);

        Win32.SetClickThrough(_handle, !acceptsInput);

        Background = background switch
        {
            BoardBackground.Whiteboard => WhiteboardBackground,
            BoardBackground.Blackboard => BlackboardBackground,
            _ => acceptsInput ? InputBackground : Brushes.Transparent,
        };

        var current = GetSurface(background);

        foreach (var surface in _surfaces.Values)
        {
            surface.Visibility = surface == current && _state.AnnotationsVisible
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        current.ApplyTool(VisualTreeHelper.GetDpi(this).DpiScaleX);
    }

    public SurfaceSnapshot? TakeSnapshotOfCurrentSurface()
    {
        return GetSurface(EffectiveBackground).TakeSnapshotAndClearFading();
    }

    public void CommitText()
    {
        foreach (var surface in _surfaces.Values)
        {
            surface.CommitText();
        }
    }

    /// <summary>La saisie de texte a besoin du clavier, donc d'une fenêtre activable.</summary>
    public void SetKeyboardInput(bool enabled)
    {
        Win32.SetNoActivate(_handle, !enabled);

        if (enabled)
        {
            Activate();
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _handle = new WindowInteropHelper(this).Handle;
        Win32.MakeFloatingToolWindow(_handle);
        Win32.SetClickThrough(_handle, true);
        Win32.SetBounds(_handle, _monitor.Bounds);
        HwndSource.FromHwnd(_handle)!.AddHook(OnWindowMessage);
    }

    // Limité à un écran, un tableau laisse les autres écrans se comporter normalement.
    private BoardBackground EffectiveBackground =>
        _options.BoardOnToolbarScreenOnly && _boardMonitor() != _monitor.DeviceName
            ? BoardBackground.Transparent
            : _state.Background;

    private AnnotationSurface GetSurface(BoardBackground background)
    {
        if (!_surfaces.TryGetValue(background, out var surface))
        {
            surface = new AnnotationSurface(_state, _pen, _histories.For(background), _options, _textEditingChanged);
            _surfaces[background] = surface;
            _root.Children.Add(surface);
        }

        return surface;
    }

    private IntPtr OnWindowMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Un clic peut faire remonter l'overlay : transparent, il volerait les clics de la barre sans la cacher.
        if (message == Win32.WmWindowPosChanging && ToolbarHandle != IntPtr.Zero)
        {
            Win32.KeepBelow(lParam, ToolbarHandle);
        }

        if (message == Win32.WmDpiChanged)
        {
            // WPF applique d'abord sa propre taille : l'overlay doit ensuite recouvrir l'écran.
            Dispatcher.BeginInvoke(DispatcherPriority.Send, () =>
            {
                Win32.SetBounds(_handle, _monitor.Bounds);

                if (IsVisible)
                {
                    Apply();
                }
            });
        }

        return IntPtr.Zero;
    }

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
