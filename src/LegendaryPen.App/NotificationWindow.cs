using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using LegendaryPen.Core;

namespace LegendaryPen.App;

/// <summary>
/// Les messages de l'application, dans le coin de l'écran principal. Chaque message est sa propre fenêtre : un clic ne peut
/// lancer que l'action de celui qu'il touche, ce qu'une notification de Windows ne permet pas de savoir.
/// </summary>
internal sealed class NotificationCenter(Func<PixelRect> workArea)
{
    private const int MarginAtNormalScale = 12;

    private readonly List<NotificationWindow> _shown = [];
    private bool _closed;

    /// <summary>Peut être appelé depuis n'importe quel fil.</summary>
    public void Show(string message, Action? clicked = null)
    {
        var dispatcher = Application.Current?.Dispatcher;

        if (dispatcher is null)
        {
            return;
        }

        if (!dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => Show(message, clicked));
            return;
        }

        if (_closed)
        {
            return;
        }

        var window = new NotificationWindow(message, clicked);
        window.Closed += (_, _) =>
        {
            _shown.Remove(window);
            Arrange();
        };
        window.ContentRendered += (_, _) => Arrange();
        _shown.Add(window);
        window.Show();

        // Fermer retire la fenêtre de la liste : on repart de la plus ancienne à chaque tour.
        while (_shown.Count > NotificationPlacement.MaxVisible)
        {
            _shown[0].Close();
        }

        Arrange();
    }

    public void CloseAll()
    {
        _closed = true;

        foreach (var window in _shown.ToList())
        {
            window.Close();
        }
    }

    private void Arrange()
    {
        if (_closed || _shown.Count == 0)
        {
            return;
        }

        var sizes = _shown.Select(window => window.PixelSize).ToList();
        var margin = (int)(MarginAtNormalScale * VisualTreeHelper.GetDpi(_shown[0]).DpiScaleX);
        var places = NotificationPlacement.Stack(workArea(), sizes, margin);

        for (var index = 0; index < places.Count; index++)
        {
            _shown[index].MoveTo(places[index]);
        }
    }
}

/// <summary>Un message : jamais activé, absent des captures et des vidéos, au-dessus des tableaux et de la barre.</summary>
internal sealed class NotificationWindow : Window
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(6);

    private readonly Action? _clicked;
    private readonly DispatcherTimer _keepOnTop = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private DateTime _closesAt = DateTime.UtcNow + Lifetime;
    private IntPtr _handle;

    public NotificationWindow(string message, Action? clicked)
    {
        _clicked = clicked;

        Title = "Legendary Pen";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        MaxWidth = 420;
        Cursor = clicked is null ? Cursors.Arrow : Cursors.Hand;
        AutomationProperties.SetName(this, message);

        Content = new Border
        {
            Background = (Brush)FindResource("SurfaceBrush"),
            BorderBrush = (Brush)FindResource("LineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 10, 14, 10),
            Child = new TextBlock
            {
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 13,
                Foreground = (Brush)FindResource("TextBrush"),
                Text = message,
                TextWrapping = TextWrapping.Wrap,
            },
        };

        MouseLeftButtonUp += OnClicked;
        _keepOnTop.Tick += OnTick;
    }

    public (int Width, int Height) PixelSize
    {
        get
        {
            var bounds = Win32.GetBounds(_handle);
            return (bounds.Width, bounds.Height);
        }
    }

    public void MoveTo(PixelPoint place) => Win32.MoveTo(_handle, place);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _handle = new WindowInteropHelper(this).Handle;
        Win32.MakeFloatingToolWindow(_handle);
        Win32.SetExcludedFromCapture(_handle, true);
        _keepOnTop.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _keepOnTop.Stop();
        base.OnClosed(e);
    }

    // La barre et les overlays remontent souvent ; un message survolé attend qu'on ait fini de le lire.
    private void OnTick(object? sender, EventArgs e)
    {
        if (IsMouseOver)
        {
            _closesAt = DateTime.UtcNow + Lifetime;
        }
        else if (DateTime.UtcNow >= _closesAt)
        {
            Close();
            return;
        }

        Win32.BringToTop(_handle);
    }

    private void OnClicked(object sender, MouseButtonEventArgs e)
    {
        Close();
        _clicked?.Invoke();
    }
}
