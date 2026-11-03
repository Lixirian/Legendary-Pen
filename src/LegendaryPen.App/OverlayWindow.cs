using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
    private readonly LayerStacks _layerStacks;
    private readonly LiveOptions _options;
    private readonly Func<string?> _boardMonitor;
    private readonly Action<bool> _textEditingChanged;
    private readonly Func<IDisposable?> _excludeEffectsFromCapture;
    private readonly Grid _root = new();
    private readonly Dictionary<BoardBackground, AnnotationSurface> _surfaces = [];

    private MonitorArea _monitor;
    private IntPtr _handle;

    public OverlayWindow(
        MonitorArea monitor,
        AppState state,
        PenState pen,
        SurfaceHistories histories,
        LayerStacks layerStacks,
        LiveOptions options,
        Func<string?> boardMonitor,
        Action<bool> textEditingChanged,
        Func<IDisposable?> excludeEffectsFromCapture)
    {
        _monitor = monitor;
        _state = state;
        _pen = pen;
        _histories = histories;
        _layerStacks = layerStacks;
        _options = options;
        _boardMonitor = boardMonitor;
        _textEditingChanged = textEditingChanged;
        _excludeEffectsFromCapture = excludeEffectsFromCapture;

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

    public IReadOnlyList<SurfaceSnapshot> TakeSnapshotOfCurrentSurface()
    {
        return GetSurface(EffectiveBackground).TakeSnapshotAndClearFading();
    }

    public void SaveAnnotations(AnnotationStore store)
    {
        foreach (var (background, surface) in _surfaces)
        {
            if (surface.ToSaved() is { } saved)
            {
                store.Save(_monitor.DeviceName, background, saved);
            }
            else
            {
                store.Delete(_monitor.DeviceName, background);
            }
        }
    }

    /// <summary>
    /// <paramref name="restoredStacks"/> retient les fonds dont la pile a déjà été rétablie par le fichier d'un autre écran :
    /// la pile est commune à tous les écrans.
    /// </summary>
    public void RestoreAnnotations(AnnotationStore store, ISet<BoardBackground> restoredStacks)
    {
        foreach (var background in Enum.GetValues<BoardBackground>())
        {
            if (store.Load(_monitor.DeviceName, background) is { } saved)
            {
                GetSurface(background).Restore(saved, restoredStacks.Contains(background));

                if (saved.Layers is not null)
                {
                    restoredStacks.Add(background);
                }
            }
        }
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

    /// <summary>Cherche le texte de l'écran sous un trait, en ignorant l'overlay et ses annotations.</summary>
    private Rect? FindTextUnder(double left, double right, double y)
    {
        const int HorizontalMargin = 300;
        const int VerticalMargin = 70;

        // Un tableau recouvre l'écran : il n'y a pas de texte à reconnaître dessous.
        if (EffectiveBackground != BoardBackground.Transparent)
        {
            return null;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var bounds = _monitor.Bounds;
        var screenLeft = bounds.Left + (int)(left * dpi.DpiScaleX);
        var screenRight = bounds.Left + (int)(right * dpi.DpiScaleX);
        var screenY = bounds.Top + (int)(y * dpi.DpiScaleY);
        var margin = (int)(VerticalMargin * dpi.DpiScaleY);

        var region = new PixelRect(
            Math.Max(bounds.Left, screenLeft - (int)(HorizontalMargin * dpi.DpiScaleX)),
            Math.Max(bounds.Top, screenY - margin),
            Math.Min(bounds.Right, screenRight + (int)(HorizontalMargin * dpi.DpiScaleX)),
            Math.Min(bounds.Bottom, screenY + margin));

        if (region.Width < 2 || region.Height < 2)
        {
            return null;
        }

        if (CaptureWithoutOwnWindows(region) is not { } screen)
        {
            return null;
        }

        var gray = new FormatConvertedBitmap(screen, PixelFormats.Gray8, null, 0);
        var pixels = new byte[region.Width * region.Height];
        gray.CopyPixels(pixels, region.Width, 0);

        var span = HighlightSnapper.FindText(
            new GrayImage(region.Width, region.Height, pixels),
            screenLeft - region.Left,
            screenRight - region.Left,
            screenY - region.Top);

        if (span is not { } text)
        {
            return null;
        }

        return new Rect(
            (region.Left - bounds.Left + text.Left) / dpi.DpiScaleX,
            (region.Top - bounds.Top + text.Top) / dpi.DpiScaleY,
            (text.Right - text.Left) / dpi.DpiScaleX,
            text.Height / dpi.DpiScaleY);
    }

    // Sans cela, le trait qui vient d'être tracé, les annotations, le voile et la loupe seraient pris pour le contenu de l'écran.
    // Si un effet n'a pas pu être retiré, il n'y a pas de capture : le trait reste droit plutôt que de s'ajuster à tort.
    private BitmapSource? CaptureWithoutOwnWindows(PixelRect region)
    {
        IDisposable? effects = null;

        try
        {
            if (!Win32.SetExcludedFromCapture(_handle, true))
            {
                return null;
            }

            Win32.SetExcludedFromCapture(ToolbarHandle, true);
            effects = _excludeEffectsFromCapture();

            if (effects is null)
            {
                return null;
            }

            Win32.WaitForComposition();

            return Win32.CaptureScreen(region);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        finally
        {
            Win32.SetExcludedFromCapture(_handle, false);
            Win32.SetExcludedFromCapture(ToolbarHandle, false);
            effects?.Dispose();
        }
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
            surface = new AnnotationSurface(_state, _pen, _histories.For(background), _layerStacks.For(background), _options, _textEditingChanged, FindTextUnder);
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
