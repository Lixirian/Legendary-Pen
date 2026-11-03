using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using LegendaryPen.Core;

namespace LegendaryPen.App;

/// <summary>Fige les écrans, laisse choisir une zone et renvoie l'image.</summary>
internal sealed class CaptureSession
{
    private readonly List<CaptureWindow> _windows = [];
    private readonly DispatcherFrame _frame = new();
    private BitmapSource? _result;

    /// <summary>Bloque jusqu'à la sélection. Renvoie null si l'utilisateur annule.</summary>
    public BitmapSource? Run(IReadOnlyList<MonitorArea> monitors)
    {
        foreach (var monitor in monitors)
        {
            var window = new CaptureWindow(monitor, Win32.CaptureScreen(monitor.Bounds), Finish);
            _windows.Add(window);
            window.Show();
        }

        var pointer = Win32.GetPointer();
        var active = _windows.FirstOrDefault(window => window.Monitor.Bounds.Contains(pointer.X, pointer.Y)) ?? _windows[0];
        active.Activate();
        active.Focus();

        Dispatcher.PushFrame(_frame);

        foreach (var window in _windows)
        {
            window.Close();
        }

        return _result;
    }

    private void Finish(BitmapSource? result)
    {
        _result = result;
        _frame.Continue = false;
    }
}

internal sealed class CaptureWindow : Window
{
    private static readonly Brush DimBrush = CreateFrozenBrush(Color.FromArgb(110, 0, 0, 0));
    private static readonly Brush HintBrush = CreateFrozenBrush(Color.FromArgb(230, 32, 33, 36));

    private readonly BitmapSource _screen;
    private readonly Action<BitmapSource?> _finish;
    private readonly RectangleGeometry _selectionGeometry = new();
    private readonly Rectangle _outline = new()
    {
        Stroke = Brushes.White,
        StrokeThickness = 1,
        StrokeDashArray = [4, 3],
        Visibility = Visibility.Collapsed,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
        IsHitTestVisible = false,
    };

    private IntPtr _handle;
    private Point _start;
    private bool _dragging;

    public CaptureWindow(MonitorArea monitor, BitmapSource screen, Action<BitmapSource?> finish)
    {
        Monitor = monitor;
        _screen = screen;
        _finish = finish;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Title = "Legendary Pen - Capture";
        Cursor = Cursors.Cross;
        Background = Brushes.Black;

        var dim = new GeometryGroup { FillRule = FillRule.EvenOdd };
        dim.Children.Add(new RectangleGeometry(new Rect(0, 0, 100000, 100000)));
        dim.Children.Add(_selectionGeometry);

        var hint = new Border
        {
            Background = HintBrush,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 8, 14, 8),
            Margin = new Thickness(0, 24, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Foreground = Brushes.White,
                FontSize = 14,
                Text = Loc.T("Glissez pour choisir une zone  ·  Cliquez pour l'écran entier  ·  Échap pour annuler"),
            },
        };

        var root = new Grid();
        root.Children.Add(new Image { Source = screen, Stretch = Stretch.Fill });
        root.Children.Add(new System.Windows.Shapes.Path { Fill = DimBrush, Data = dim, IsHitTestVisible = false });
        root.Children.Add(_outline);
        root.Children.Add(hint);
        Content = root;

        MouseLeftButtonDown += OnPointerDown;
        MouseMove += OnPointerMove;
        MouseLeftButtonUp += OnPointerUp;
        PreviewKeyDown += OnKey;
    }

    public MonitorArea Monitor { get; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _handle = new WindowInteropHelper(this).Handle;
        Win32.SetBounds(_handle, Monitor.Bounds);
        HwndSource.FromHwnd(_handle)!.AddHook(OnWindowMessage);
    }

    private IntPtr OnWindowMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == Win32.WmDpiChanged)
        {
            // WPF applique d'abord sa propre taille : la fenêtre doit ensuite recouvrir l'écran.
            Dispatcher.BeginInvoke(DispatcherPriority.Send, () => Win32.SetBounds(_handle, Monitor.Bounds));
        }

        return IntPtr.Zero;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _finish(null);
        }
    }

    private void OnPointerDown(object sender, MouseButtonEventArgs e)
    {
        _start = e.GetPosition(this);
        _dragging = true;
        CaptureMouse();
        ShowSelection(_start);
    }

    private void OnPointerMove(object sender, MouseEventArgs e)
    {
        if (_dragging)
        {
            ShowSelection(e.GetPosition(this));
        }
    }

    private void OnPointerUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        ReleaseMouseCapture();

        var region = CaptureRegion.Resolve(ToPixels(_start), ToPixels(e.GetPosition(this)), _screen.PixelWidth, _screen.PixelHeight);
        var cropped = new CroppedBitmap(_screen, new Int32Rect(region.Left, region.Top, region.Width, region.Height));
        cropped.Freeze();

        _finish(cropped);
    }

    private PixelPoint ToPixels(Point point)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return new PixelPoint((int)Math.Round(point.X * dpi.DpiScaleX), (int)Math.Round(point.Y * dpi.DpiScaleY));
    }

    private void ShowSelection(Point current)
    {
        var selection = new Rect(_start, current);

        _selectionGeometry.Rect = selection;
        _outline.Margin = new Thickness(selection.Left, selection.Top, 0, 0);
        _outline.Width = selection.Width;
        _outline.Height = selection.Height;
        _outline.Visibility = Visibility.Visible;
    }

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

/// <summary>Envoie une capture vers la destination choisie dans les réglages.</summary>
internal static class CaptureDelivery
{
    public static string DefaultFolder => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        "Legendary Pen");

    /// <summary>Renvoie le fichier écrit, ou null si l'image est partie ailleurs ou si l'utilisateur a annulé.</summary>
    public static string? SaveToFolder(BitmapSource image, string? folder)
    {
        var target = string.IsNullOrWhiteSpace(folder) ? DefaultFolder : folder;
        Directory.CreateDirectory(target);

        var path = CaptureFiles.UniquePath(target, DateTime.Now, File.Exists);
        WritePng(image, path);
        return path;
    }

    public static string? SaveWithDialog(BitmapSource image, string? folder)
    {
        var target = string.IsNullOrWhiteSpace(folder) ? DefaultFolder : folder;
        Directory.CreateDirectory(target);

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = Loc.T("Enregistrer la capture"),
            Filter = Loc.T("Image PNG (*.png)|*.png"),
            DefaultExt = ".png",
            InitialDirectory = target,
            FileName = System.IO.Path.GetFileName(CaptureFiles.UniquePath(target, DateTime.Now, File.Exists)),
        };

        if (dialog.ShowDialog() != true)
        {
            return null;
        }

        WritePng(image, dialog.FileName);
        return dialog.FileName;
    }

    public static void CopyToClipboard(BitmapSource image)
    {
        const int Attempts = 5;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Clipboard.SetImage(image);
                return;
            }
            catch (COMException) when (attempt < Attempts)
            {
                // Une autre application peut tenir le presse-papiers un court instant.
                Thread.Sleep(100);
            }
        }
    }

    public static void ShowInExplorer(string path)
    {
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }

    private static void WritePng(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));

        // Le dialogue a déjà demandé confirmation s'il remplace un fichier.
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        encoder.Save(stream);
    }
}
