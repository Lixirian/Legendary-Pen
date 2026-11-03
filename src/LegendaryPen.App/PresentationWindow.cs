using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using LegendaryPen.Core;

namespace LegendaryPen.App;

/// <summary>Fenêtre transparente, traversable par les clics, qui porte les effets du mode présentation sur un écran.</summary>
internal sealed class PresentationWindow : Window
{
    private const int MaxRipples = 8;
    private const double RippleThickness = 4;

    private readonly EffectsLayer _layer = new();
    private readonly List<Ripple> _ripples = [];

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

    /// <summary>Retire ou rend la fenêtre aux captures d'écran. Renvoie faux si Windows refuse.</summary>
    public bool SetExcludedFromCapture(bool excluded) => Win32.SetExcludedFromCapture(_handle, excluded);

    /// <summary>Vrai si ce point, en pixels physiques, tombe sur l'écran de cette fenêtre.</summary>
    public bool Contains(PixelPoint point) => Covers(_monitor.Bounds, point);

    /// <summary>Lance un anneau à la position du clic. Au-delà de huit anneaux vivants, le plus ancien disparaît.</summary>
    public void AddRipple(PixelPoint point, Color color)
    {
        if (_ripples.Count >= MaxRipples)
        {
            _ripples.RemoveAt(0);
        }

        _ripples.Add(new Ripple(point, color, Stopwatch.GetTimestamp()));
    }

    /// <summary>Vrai tant qu'un anneau est vivant : l'animation doit alors continuer.</summary>
    public bool HasRipples => _ripples.Count > 0;

    /// <summary>Abandonne les anneaux en cours, par exemple quand les effets de clic s'éteignent.</summary>
    public void ClearRipples()
    {
        _ripples.Clear();
        _layer.DrawRipples([]);
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
        RenderSpotlight(pointer, spotlight);
        RenderRipples();
    }

    /// <summary>Met le projecteur à jour pour la position du pointeur. Rien n'est redessiné si rien n'a changé.</summary>
    public void RenderSpotlight(PixelPoint pointer, bool spotlight)
    {
        var bounds = _monitor.Bounds;
        var dpi = VisualTreeHelper.GetDpi(this);

        // Chaque écran peut avoir sa propre échelle : le centre se calcule avec le DPI de cette fenêtre.
        var center = Covers(bounds, pointer)
            ? new Point((pointer.X - bounds.Left) / dpi.DpiScaleX, (pointer.Y - bounds.Top) / dpi.DpiScaleY)
            : (Point?)null;

        _layer.Draw(spotlight, center, dpi);
    }

    /// <summary>Avance l'animation des anneaux.</summary>
    public void RenderRipples()
    {
        var bounds = _monitor.Bounds;
        var dpi = VisualTreeHelper.GetDpi(this);
        var now = Stopwatch.GetTimestamp();

        _ripples.RemoveAll(ripple => Stopwatch.GetElapsedTime(ripple.Start, now).TotalMilliseconds >= PresentationGeometry.RippleDuration);

        var rings = new List<RippleRing>(_ripples.Count);

        foreach (var ripple in _ripples)
        {
            var shape = PresentationGeometry.RippleAt(Stopwatch.GetElapsedTime(ripple.Start, now).TotalMilliseconds);
            var center = new Point((ripple.Position.X - bounds.Left) / dpi.DpiScaleX, (ripple.Position.Y - bounds.Top) / dpi.DpiScaleY);

            // Le rayon et l'épaisseur sont en DIP (PR-4) : les anneaux suivent l'échelle de l'écran, comme le projecteur.
            rings.Add(new RippleRing(center, shape.Radius, shape.Opacity, ripple.Color, RippleThickness));
        }

        _layer.DrawRipples(rings);
    }

    private static bool Covers(PixelRect bounds, PixelPoint point)
    {
        return point.X >= bounds.Left && point.X < bounds.Right && point.Y >= bounds.Top && point.Y < bounds.Bottom;
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

    private readonly record struct Ripple(PixelPoint Position, Color Color, long Start);

    private readonly record struct RippleRing(Point Center, double Radius, double Opacity, Color Color, double Thickness);

    // Le lissage des bords est coupé pour les cases du voile et le bloc du trou, pas pour les anneaux.
    private sealed class AliasedContainer : ContainerVisual
    {
        public AliasedContainer()
        {
            VisualEdgeMode = EdgeMode.Aliased;
        }
    }

    /// <summary>
    /// Un seul élément dessiné à la main : pas d'éléments de mise en page. Le voile et les anneaux ont chacun leur visuel : animer un anneau ne redessine pas le voile.
    /// Avec AllowsTransparency, le coût suit la surface invalidée : le voile est une grille de cases fixes, et seul le bloc de cases qui contient le trou est redessiné.
    /// </summary>
    private sealed class EffectsLayer : FrameworkElement
    {
        private const double VeilOpacity = 0.6;
        private const double SpotlightRadius = 180;

        // Le trou est franc jusqu'à cette fraction du rayon, puis le voile revient en dégradé.
        private const double SoftEdgeStart = 0.8;

        private static readonly Brush Veil = CreateVeil();

        private readonly AliasedContainer _veilVisual = new();
        private readonly DrawingVisual _holeVisual = new();
        private readonly DrawingVisual _ripplesVisual = new();
        private readonly RadialGradientBrush _hole = CreateHole();

        private DrawingVisual[] _cells = [];
        private int _columns;
        private int _rows;
        private double _cellX;
        private double _cellY;
        private SpotlightCells? _holeCells;

        private bool _spotlightDrawn;
        private bool _ripplesDrawn;
        private Point? _lastCenter;
        private Size _lastSize;
        private DpiScale _lastDpi;

        public EffectsLayer()
        {
            IsHitTestVisible = false;
            AddVisualChild(_veilVisual);
            AddVisualChild(_ripplesVisual);
        }

        protected override int VisualChildrenCount => 2;

        // Les anneaux passent au-dessus du voile.
        protected override Visual GetVisualChild(int index) => index == 0 ? _veilVisual : _ripplesVisual;

        public void Draw(bool spotlight, Point? center, DpiScale dpi)
        {
            var size = new Size(ActualWidth, ActualHeight);
            var layoutChanged = _lastSize != size || _lastDpi.DpiScaleX != dpi.DpiScaleX || _lastDpi.DpiScaleY != dpi.DpiScaleY;

            if (_spotlightDrawn == spotlight && _lastCenter == center && !layoutChanged)
            {
                return;
            }

            _spotlightDrawn = spotlight;
            _lastCenter = center;
            _lastSize = size;
            _lastDpi = dpi;

            if (!spotlight || size.Width <= 0 || size.Height <= 0)
            {
                ClearVeil();
                return;
            }

            if (layoutChanged || _cells.Length == 0)
            {
                BuildVeil(size, dpi);
            }

            MoveHole(center);
        }

        private void ClearVeil()
        {
            _veilVisual.Children.Clear();
            _cells = [];
            _holeCells = null;
        }

        // Tout se recrée ici, mais seulement à l'allumage et aux changements de taille ou d'échelle.
        private void BuildVeil(Size size, DpiScale dpi)
        {
            ClearVeil();

            _cellX = PresentationGeometry.SpotlightCell(dpi.DpiScaleX);
            _cellY = PresentationGeometry.SpotlightCell(dpi.DpiScaleY);
            _columns = (int)Math.Ceiling(size.Width / _cellX);
            _rows = (int)Math.Ceiling(size.Height / _cellY);
            _cells = new DrawingVisual[_columns * _rows];

            for (var row = 0; row < _rows; row++)
            {
                for (var column = 0; column < _columns; column++)
                {
                    var cell = new DrawingVisual();
                    _cells[(row * _columns) + column] = cell;
                    _veilVisual.Children.Add(cell);
                    DrawCell(column, row, visible: true);
                }
            }

            ClearHole();
            _veilVisual.Children.Add(_holeVisual);
        }

        private void MoveHole(Point? center)
        {
            if (center is not { } point)
            {
                if (_holeCells is { } left)
                {
                    RestoreVeil(left, null);
                    ClearHole();
                    _holeCells = null;
                }

                return;
            }

            var cells = PresentationGeometry.CellsAround(point.X, point.Y, SpotlightRadius, _cellX, _cellY, _columns, _rows);

            _hole.Center = point;
            _hole.GradientOrigin = point;

            // Le bloc ne change pas : déplacer le dégradé suffit, le reste de la fenêtre n'est pas touché.
            if (_holeCells == cells)
            {
                return;
            }

            if (_holeCells is { } previous)
            {
                RestoreVeil(previous, cells);
            }

            for (var row = cells.FirstRow; row <= cells.LastRow; row++)
            {
                for (var column = cells.FirstColumn; column <= cells.LastColumn; column++)
                {
                    if (_holeCells is not { } shown || !shown.Contains(column, row))
                    {
                        DrawCell(column, row, visible: false);
                    }
                }
            }

            _holeCells = cells;

            using var context = _holeVisual.RenderOpen();
            context.DrawRectangle(_hole, null, BlockRect(cells));
        }

        // Rend le voile aux cases que le trou quitte.
        private void RestoreVeil(SpotlightCells previous, SpotlightCells? next)
        {
            for (var row = previous.FirstRow; row <= previous.LastRow; row++)
            {
                for (var column = previous.FirstColumn; column <= previous.LastColumn; column++)
                {
                    if (next is not { } kept || !kept.Contains(column, row))
                    {
                        DrawCell(column, row, visible: true);
                    }
                }
            }
        }

        private void DrawCell(int column, int row, bool visible)
        {
            using var context = _cells[(row * _columns) + column].RenderOpen();

            if (visible)
            {
                context.DrawRectangle(Veil, null, BlockRect(new SpotlightCells(column, row, column, row)));
            }
        }

        private void ClearHole()
        {
            _holeVisual.RenderOpen().Close();
        }

        // Les cases voisines partagent exactement la même coordonnée : sans lissage des bords, ni vide ni recouvrement entre elles.
        private Rect BlockRect(SpotlightCells cells)
        {
            var left = cells.FirstColumn * _cellX;
            var top = cells.FirstRow * _cellY;
            return new Rect(left, top, ((cells.LastColumn + 1) * _cellX) - left, ((cells.LastRow + 1) * _cellY) - top);
        }

        /// <summary>Dessine les anneaux. Sans anneau, rien n'est redessiné sauf pour effacer le dernier.</summary>
        public void DrawRipples(IReadOnlyList<RippleRing> rings)
        {
            if (rings.Count == 0 && !_ripplesDrawn)
            {
                return;
            }

            _ripplesDrawn = rings.Count > 0;

            using var context = _ripplesVisual.RenderOpen();

            foreach (var ring in rings)
            {
                var alpha = (byte)Math.Round(255 * ring.Opacity);
                var brush = new SolidColorBrush(Color.FromArgb(alpha, ring.Color.R, ring.Color.G, ring.Color.B));
                brush.Freeze();
                var pen = new Pen(brush, ring.Thickness);
                pen.Freeze();
                context.DrawEllipse(null, pen, ring.Center, ring.Radius, ring.Radius);
            }
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
