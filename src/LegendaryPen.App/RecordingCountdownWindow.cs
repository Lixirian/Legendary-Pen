using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using LegendaryPen.Core;

namespace LegendaryPen.App;

/// <summary>Décompte centré sur l'écran à enregistrer : traversable par les clics, jamais activé, absent des captures.</summary>
internal sealed class RecordingCountdownWindow : Window
{
    private readonly MonitorArea _monitor;
    private readonly TextBlock _digit;

    public RecordingCountdownWindow(MonitorArea monitor, int seconds)
    {
        _monitor = monitor;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Title = "Legendary Pen - Countdown";

        _digit = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 96,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var panel = new StackPanel();
        panel.Children.Add(_digit);
        panel.Children.Add(new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 16,
            Foreground = (Brush)FindResource("MutedBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Text = Loc.T("L'enregistrement va commencer"),
        });

        Content = new Border
        {
            Background = (Brush)FindResource("SurfaceBrush"),
            BorderBrush = (Brush)FindResource("AccentBrush"),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(32, 12, 32, 24),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = panel,
        };

        ShowRemaining(seconds);
    }

    public void ShowRemaining(int seconds)
    {
        _digit.Text = seconds.ToString(CultureInfo.CurrentCulture);

        // Le mode Dessin ou un tableau ont pu remonter les overlays pendant le décompte.
        var handle = new WindowInteropHelper(this).Handle;

        if (handle != IntPtr.Zero)
        {
            Win32.BringToTop(handle);
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var handle = new WindowInteropHelper(this).Handle;
        Win32.MakeFloatingToolWindow(handle);
        Win32.SetClickThrough(handle, true);
        Win32.SetNoActivate(handle, true);

        // Avant le premier affichage : le décompte ne fait pas partie de la vidéo.
        Win32.SetExcludedFromCapture(handle, true);
        Win32.SetBounds(handle, _monitor.Bounds);
    }
}
