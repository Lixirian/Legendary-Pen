using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using LegendaryPen.Core;

namespace LegendaryPen.App;

/// <summary>Fenêtre transparente couvrant exactement un écran.</summary>
public sealed class OverlayWindow : Window
{
    // WPF ne transmet les clics d'une fenêtre transparente que sur les pixels d'alpha non nul.
    private static readonly Brush InputBackground = CreateInputBackground();

    private MonitorArea _monitor;
    private IntPtr _handle;

    public OverlayWindow(MonitorArea monitor)
    {
        _monitor = monitor;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Title = "Legendary Pen - Overlay";

        Canvas = new InkCanvas
        {
            Background = Brushes.Transparent,
            EditingMode = InkCanvasEditingMode.None,

            // La gomme du stylet arrive avec DR-4 : elle doit d'abord passer par l'historique.
            EditingModeInverted = InkCanvasEditingMode.None,
        };

        Content = Canvas;
    }

    public InkCanvas Canvas { get; }

    public void UpdateMonitor(MonitorArea monitor)
    {
        _monitor = monitor;

        if (_handle != IntPtr.Zero)
        {
            Win32.SetBounds(_handle, monitor.Bounds);
        }
    }

    public void ApplyPen(PenState pen)
    {
        // Un nouvel objet à chaque changement : les traits déjà dessinés gardent leur apparence.
        Canvas.DefaultDrawingAttributes = new DrawingAttributes
        {
            Color = Color.FromRgb(pen.Color.R, pen.Color.G, pen.Color.B),
            Width = pen.Size,
            Height = pen.Size,
            FitToCurve = true,
        };
    }

    public void Apply(AppState state)
    {
        Win32.SetClickThrough(_handle, !state.OverlayAcceptsInput);
        Background = state.OverlayAcceptsInput ? InputBackground : Brushes.Transparent;
        Canvas.EditingMode = state.CanDraw ? InkCanvasEditingMode.Ink : InkCanvasEditingMode.None;
        Canvas.Visibility = state.AnnotationsVisible ? Visibility.Visible : Visibility.Hidden;
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

    private IntPtr OnWindowMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == Win32.WmDpiChanged)
        {
            // WPF applique d'abord sa propre taille : l'overlay doit ensuite recouvrir l'écran.
            Dispatcher.BeginInvoke(DispatcherPriority.Send, () => Win32.SetBounds(_handle, _monitor.Bounds));
        }

        return IntPtr.Zero;
    }

    private static SolidColorBrush CreateInputBackground()
    {
        var brush = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        brush.Freeze();
        return brush;
    }
}
