using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using LegendaryPen.Core;

namespace LegendaryPen.App;

/// <summary>Fenêtre transparente, traversable par les clics, qui porte les effets du mode présentation sur un écran.</summary>
internal sealed class PresentationWindow : Window
{
    private readonly EffectsLayer _layer = new();

    private MonitorArea _monitor;
    private IntPtr _handle;

    public PresentationWindow(MonitorArea monitor)
    {
        _monitor = monitor;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Title = "Legendary Pen - Presentation";
        Content = _layer;
    }

    /// <summary>La barre d'outils, que cette fenêtre ne doit jamais recouvrir.</summary>
    public IntPtr ToolbarHandle { get; set; }

    /// <summary>Remonte la fenêtre juste sous la barre d'outils, donc au-dessus des overlays d'annotation.</summary>
    public void Raise()
    {
        if (_handle != IntPtr.Zero)
        {
            Win32.BringToTop(_handle);
        }
    }

    public void UpdateMonitor(MonitorArea monitor)
    {
        _monitor = monitor;

        if (_handle != IntPtr.Zero)
        {
            Win32.SetBounds(_handle, monitor.Bounds);
        }
    }

    /// <summary>Redessine les effets pour la position du pointeur, en pixels physiques.</summary>
    public void Render(PixelPoint pointer, bool spotlight)
    {
        var bounds = _monitor.Bounds;
        var dpi = VisualTreeHelper.GetDpi(this);
        var onThisScreen = pointer.X >= bounds.Left && pointer.X < bounds.Right
            && pointer.Y >= bounds.Top && pointer.Y < bounds.Bottom;

        // Chaque écran peut avoir sa propre échelle : le centre se calcule avec le DPI de cette fenêtre.
        var center = onThisScreen
            ? new Point((pointer.X - bounds.Left) / dpi.DpiScaleX, (pointer.Y - bounds.Top) / dpi.DpiScaleY)
            : (Point?)null;

        _layer.Draw(spotlight, center);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _handle = new WindowInteropHelper(this).Handle;
        Win32.MakeFloatingToolWindow(_handle);
        Win32.SetClickThrough(_handle, true);
        Win32.SetNoActivate(_handle, true);
        Win32.SetBounds(_handle, _monitor.Bounds);
        HwndSource.FromHwnd(_handle)!.AddHook(OnWindowMessage);
    }

    private IntPtr OnWindowMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Sans cela, un changement d'empilement ferait passer la fenêtre devant la barre d'outils.
        if (message == Win32.WmWindowPosChanging && ToolbarHandle != IntPtr.Zero)
        {
            Win32.KeepBelow(lParam, ToolbarHandle);
        }

        if (message == Win32.WmDpiChanged)
        {
            // WPF applique d'abord sa propre taille : la fenêtre doit ensuite recouvrir l'écran.
            Dispatcher.BeginInvoke(DispatcherPriority.Send, () =>
            {
                Win32.SetBounds(_handle, _monitor.Bounds);
                _layer.Invalidate();
            });
        }

        return IntPtr.Zero;
    }

    /// <summary>Un seul élément dessiné à la main : pas d'éléments de mise en page, d'autres calques viendront s'y ajouter.</summary>
    private sealed class EffectsLayer : FrameworkElement
    {
        private const double VeilOpacity = 0.6;
        private const double SpotlightRadius = 180;

        // Le trou est franc jusqu'à cette fraction du rayon, puis le voile revient en dégradé.
        private const double SoftEdgeStart = 0.8;

        private static readonly Brush Veil = CreateVeil();

        private readonly DrawingVisual _spotlightVisual = new();
        private readonly RadialGradientBrush _hole = CreateHole();

        private bool _spotlightDrawn;
        private Point? _lastCenter;
        private Size _lastSize;

        public EffectsLayer()
        {
            IsHitTestVisible = false;
            AddVisualChild(_spotlightVisual);
        }

        protected override int VisualChildrenCount => 1;

        protected override Visual GetVisualChild(int index) => _spotlightVisual;

        public void Draw(bool spotlight, Point? center)
        {
            var size = new Size(ActualWidth, ActualHeight);

            if (_spotlightDrawn == spotlight && _lastCenter == center && _lastSize == size)
            {
                return;
            }

            _spotlightDrawn = spotlight;
            _lastCenter = center;
            _lastSize = size;

            using var context = _spotlightVisual.RenderOpen();

            if (!spotlight || size.Width <= 0 || size.Height <= 0)
            {
                return;
            }

            var area = new Rect(size);

            if (center is not { } point)
            {
                context.DrawRectangle(Veil, null, area);
                return;
            }

            _hole.Center = point;
            _hole.GradientOrigin = point;
            context.DrawRectangle(_hole, null, area);
        }

        public void Invalidate()
        {
            _lastSize = default;
        }

        private static Brush CreateVeil()
        {
            var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(255 * VeilOpacity), 0, 0, 0));
            brush.Freeze();
            return brush;
        }

        private static RadialGradientBrush CreateHole()
        {
            var clear = Color.FromArgb(0, 0, 0, 0);
            var veil = Color.FromArgb((byte)Math.Round(255 * VeilOpacity), 0, 0, 0);

            // Au-delà du rayon, le mode d'étalement par défaut prolonge la dernière couleur : le voile plein.
            return new RadialGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute,
                RadiusX = SpotlightRadius,
                RadiusY = SpotlightRadius,
                GradientStops =
                [
                    new GradientStop(clear, 0),
                    new GradientStop(clear, SoftEdgeStart),
                    new GradientStop(veil, 1),
                ],
            };
        }
    }
}
