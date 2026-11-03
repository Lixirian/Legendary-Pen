using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LegendaryPen.Core;

namespace LegendaryPen.App;

public partial class ToolbarWindow : Window
{
    private const int ToggleModeHotKey = 1;
    private const int FirstDrawHotKey = 2;

    private const double ToolButtonSpan = 44;
    private const double GripThickness = 20;
    private const double SeparatorLength = 24;
    private const double WindowChrome = 8;
    private const double SnapDistance = 48;
    private const double CornerRadius = 12;
    private const double WaveIconShift = 4;

    private static readonly Duration SlideDuration = new(TimeSpan.FromMilliseconds(240));

    private readonly AppState _state;
    private readonly PenState _pen;
    private readonly SurfaceHistories _histories;
    private readonly OverlayManager _overlays;
    private readonly UpdateService? _updates;
    private readonly PixelPoint? _savedPosition;
    private readonly List<ToggleButton> _swatches = [];
    private readonly DispatcherTimer _collapseTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly TimeSpan _collapseDelay;
    private readonly ToggleButton[] _sizeButtons;
    private readonly ToggleButton[] _toolButtons;
    private readonly (uint Modifiers, uint Key, string Label, Action Run)[] _drawHotKeys;

    private IReadOnlyList<MonitorArea> _monitors;
    private UpdateManifest? _availableUpdate;
    private IntPtr _handle;
    private bool _drawHotKeysRegistered;
    private bool _hotKeyFailureReported;
    private bool _refreshing;
    private bool _dialogOpen;
    private bool _collapsed;
    private DateTime _lastInteraction = DateTime.UtcNow;
    private int _slideRequest;
    private BoardBackground _placedBackground;

    public ToolbarWindow(
        AppState state,
        PenState pen,
        SurfaceHistories histories,
        OverlayManager overlays,
        UpdateService? updates,
        IReadOnlyList<MonitorArea> monitors,
        PixelPoint? savedPosition,
        bool vertical,
        ToolbarDock dock,
        string? dockMonitor,
        TimeSpan collapseDelay)
    {
        IsVertical = vertical;
        Dock = dock;
        DockMonitor = dockMonitor;
        _collapseDelay = collapseDelay;
        _state = state;
        _pen = pen;
        _histories = histories;
        _overlays = overlays;
        _updates = updates;
        _monitors = monitors;
        _savedPosition = savedPosition;

        InitializeComponent();

        _sizeButtons = [SmallSizeButton, MediumSizeButton, LargeSizeButton];
        _toolButtons = [PenButton, HighlighterButton, EraserButton, LineButton, ArrowButton, RectangleButton, EllipseButton, TextButton];
        _drawHotKeys = CreateDrawHotKeys();
        CreateSwatches();
        ExpandIcon.Source = LoadApplicationIcon();
        ApplyOrientation();
        _collapseTimer.Tick += OnCollapseTimerTick;

        _state.Changed += Refresh;
        _pen.Changed += Refresh;
        _histories.Changed += Refresh;
        _overlays.TextEditingChanged += Refresh;
        ContentRendered += OnFirstRender;
    }

    /// <summary>Position actuelle de la barre, en pixels physiques.</summary>
    public PixelPoint Position
    {
        get
        {
            var bounds = Win32.GetBounds(_handle);
            return new PixelPoint(bounds.Left, bounds.Top);
        }
    }

    /// <summary>Orientation choisie pour la barre lorsqu'elle est libre.</summary>
    public bool IsVertical { get; private set; }

    public ToolbarDock Dock { get; private set; }

    public string? DockMonitor { get; private set; }

    private bool EffectiveVertical => ToolbarDocking.IsVertical(Dock, IsVertical);

    public event Action? PositionChanged;

    public event Action? OrientationChanged;

    /// <summary>Remet la barre au premier plan et sur un écran disponible.</summary>
    public void Reveal()
    {
        if (Dock == ToolbarDock.None)
        {
            PlaceOnScreen(Position);
        }
        else
        {
            PlaceDocked();
        }

        Win32.BringToTop(_handle);
    }

    /// <summary>Déplie la barre : second lancement de l'application ou clic dans la barre des tâches.</summary>
    public void ExpandAndReveal()
    {
        SetCollapsed(false);
        Reveal();
    }

    public void UpdateMonitors(IReadOnlyList<MonitorArea> monitors)
    {
        _monitors = monitors;
        UpdateSizeLimit();
        PlaceAfterLayout();
    }

    /// <summary>Affiche la pastille, sans interrompre le travail en cours.</summary>
    public void ShowUpdateAvailable(UpdateManifest manifest)
    {
        _availableUpdate = manifest;
        UpdateButton.Visibility = Visibility.Visible;
        ExpandBadge.Visibility = Visibility.Visible;
    }

    public void OfferUpdate()
    {
        if (_availableUpdate is { } manifest && _updates is not null && !_dialogOpen)
        {
            RunDialog(() => new UpdateWindow(manifest, _updates).ShowDialog());
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _handle = new WindowInteropHelper(this).Handle;
        Win32.MakeNonActivatingAppWindow(_handle);
        HwndSource.FromHwnd(_handle)!.AddHook(OnWindowMessage);

        Register(ToggleModeHotKey, Win32.ModControl | Win32.ModShift, Win32.VkSpace, "Ctrl+Maj+Espace");
        UpdateSizeLimit();
        Refresh();
    }

    protected override void OnClosed(EventArgs e)
    {
        _collapseTimer.Stop();
        Win32.UnregisterGlobalHotKey(_handle, ToggleModeHotKey);
        SetDrawHotKeys(false);
        base.OnClosed(e);

        // Fermée depuis la barre des tâches ou par Alt+F4 : sans elle, l'application serait inaccessible.
        Application.Current.Shutdown();
    }

    // La taille finale n'est connue qu'après le premier rendu, car elle dépend du contenu.
    private void OnFirstRender(object? sender, EventArgs e)
    {
        // La limite de taille n'existe qu'après la création de la fenêtre : la première mesure peut être périmée.
        SizeToContent = SizeToContent.Manual;
        SizeToContent = SizeToContent.WidthAndHeight;
        UpdateLayout();

        if (Dock == ToolbarDock.None)
        {
            PlaceOnScreen(_savedPosition);
        }
        else
        {
            PlaceDocked();
        }

        Opacity = 1;
        _lastInteraction = DateTime.UtcNow;
        _collapseTimer.Start();
    }

    private void PlaceOnScreen(PixelPoint? preferred)
    {
        var size = Win32.GetBounds(_handle);
        var primary = _monitors.FirstOrDefault(monitor => monitor.IsPrimary) ?? _monitors[0];

        Win32.MoveTo(_handle, ToolbarPlacement.Resolve(
            preferred,
            size.Width,
            size.Height,
            _monitors.Select(monitor => monitor.Bounds).ToList(),
            primary.WorkArea));
    }

    private void PlaceDocked()
    {
        var size = Win32.GetBounds(_handle);
        Win32.MoveTo(_handle, ToolbarDocking.Place(Dock, size.Width, size.Height, DockArea(FindBarMonitor())));
    }

    private PixelRect DockArea(MonitorArea monitor)
    {
        return ToolbarDocking.DockArea(_state.Background, monitor.Bounds, monitor.WorkArea);
    }

    private MonitorArea FindBarMonitor()
    {
        var bounds = Win32.GetBounds(_handle);
        var centerX = bounds.Left + (bounds.Width / 2);
        var centerY = bounds.Top + (bounds.Height / 2);

        return (Dock != ToolbarDock.None ? _monitors.FirstOrDefault(candidate => candidate.DeviceName == DockMonitor) : null)
            ?? _monitors.FirstOrDefault(candidate => candidate.Bounds.Contains(centerX, centerY))
            ?? _monitors.FirstOrDefault(candidate => candidate.IsPrimary)
            ?? _monitors[0];
    }

    private void SetCollapsed(bool collapsed)
    {
        _lastInteraction = DateTime.UtcNow;

        if (collapsed == _collapsed)
        {
            return;
        }

        _collapsed = collapsed;

        var request = ++_slideRequest;
        var leaving = collapsed ? (FrameworkElement)BarSurface : ExpandButton;
        var arriving = collapsed ? (FrameworkElement)ExpandButton : BarSurface;

        Slide(leaving, show: false, () =>
        {
            if (request != _slideRequest)
            {
                return;
            }

            // La fenêtre change de taille pendant que rien n'est visible : aucun saut à l'écran.
            leaving.Visibility = Visibility.Collapsed;
            arriving.Opacity = 0;
            arriving.Visibility = Visibility.Visible;
            UpdateLayout();

            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
            {
                if (request != _slideRequest)
                {
                    return;
                }

                Reveal();
                Slide(arriving, show: true, null);
                arriving.Opacity = 1;
            });
        });
    }

    /// <summary>Fait monter ou redescendre un élément derrière le bord ; la fenêtre le coupe à l'écran.</summary>
    private void Slide(FrameworkElement element, bool show, Action? completed)
    {
        var (x, y) = ToolbarDocking.HiddenOffset(Dock, element.ActualWidth, element.ActualHeight);
        var distance = x != 0 ? x : y;
        var transform = new TranslateTransform();
        element.RenderTransform = transform;

        var animation = new DoubleAnimation(show ? distance : 0, show ? 0 : distance, SlideDuration)
        {
            EasingFunction = new SineEase { EasingMode = show ? EasingMode.EaseOut : EasingMode.EaseIn },
        };

        if (completed is not null)
        {
            animation.Completed += (_, _) => completed();
        }

        transform.BeginAnimation(x != 0 ? TranslateTransform.XProperty : TranslateTransform.YProperty, animation);
    }

    private void OnCollapseTimerTick(object? sender, EventArgs e)
    {
        var shouldCollapse = ToolbarDocking.ShouldCollapse(
            Dock,
            DateTime.UtcNow - _lastInteraction,
            _collapseDelay,
            IsMouseOver,
            _dialogOpen,
            _collapsed);

        if (shouldCollapse)
        {
            SetCollapsed(true);
        }
    }

    private static BitmapFrame LoadApplicationIcon()
    {
        var decoder = new IconBitmapDecoder(
            new Uri("pack://application:,,,/legendary-pen.ico"),
            BitmapCreateOptions.None,
            BitmapCacheOption.OnLoad);

        return decoder.Frames.Where(frame => frame.PixelWidth <= 64).MaxBy(frame => frame.PixelWidth) ?? decoder.Frames[0];
    }

    private void OnToolbarPressed(object sender, MouseButtonEventArgs e) => _lastInteraction = DateTime.UtcNow;

    private void OnExpandClicked(object sender, RoutedEventArgs e) => SetCollapsed(false);

    // La nouvelle taille n'est connue qu'une fois la mise en page terminée.
    private void PlaceAfterLayout()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, Reveal);
    }

    private void ApplyOrientation()
    {
        var orientation = EffectiveVertical ? Orientation.Vertical : Orientation.Horizontal;

        ToolbarPanel.Orientation = orientation;
        SwatchPanel.Orientation = orientation;
        SizeSlider.Orientation = orientation;

        SizeSlider.Width = EffectiveVertical ? double.NaN : 90;
        SizeSlider.Height = EffectiveVertical ? 90 : double.NaN;
        SizeSlider.Margin = EffectiveVertical ? new Thickness(0, 4, 0, 4) : new Thickness(4, 0, 4, 0);

        Grip.Width = EffectiveVertical ? ToolButtonSpan : GripThickness;
        Grip.Height = EffectiveVertical ? GripThickness : ToolButtonSpan;
        GripGlyph.Text = EffectiveVertical ? "" : "";

        foreach (var separator in ToolbarPanel.Children.OfType<Border>().Where(child => Equals(child.Tag, "Separator")))
        {
            separator.Width = EffectiveVertical ? SeparatorLength : 1;
            separator.Height = EffectiveVertical ? 1 : SeparatorLength;
            separator.Margin = EffectiveVertical ? new Thickness(0, 4, 0, 4) : new Thickness(4, 0, 4, 0);
            separator.HorizontalAlignment = HorizontalAlignment.Center;
            separator.VerticalAlignment = VerticalAlignment.Center;
        }

        foreach (var swatch in _swatches)
        {
            swatch.Width = EffectiveVertical ? ToolButtonSpan - 4 : 30;
            swatch.Height = EffectiveVertical ? 30 : ToolButtonSpan - 4;
        }

        ApplyDockAppearance();
        OrientationButton.ToolTip = EffectiveVertical ? "Mettre la barre à l'horizontale" : "Mettre la barre à la verticale";
        UpdateSizeLimit();
    }

    // Le côté attaché reste droit, et la vague garde sa base contre le bord.
    private void ApplyDockAppearance()
    {
        var radius = CornerRadius;

        BarSurface.RenderTransform = null;
        BarSurface.CornerRadius = Dock switch
        {
            ToolbarDock.Top => new System.Windows.CornerRadius(0, 0, radius, radius),
            ToolbarDock.Bottom => new System.Windows.CornerRadius(radius, radius, 0, 0),
            ToolbarDock.Left => new System.Windows.CornerRadius(0, radius, radius, 0),
            ToolbarDock.Right => new System.Windows.CornerRadius(radius, 0, 0, radius),
            _ => new System.Windows.CornerRadius(radius),
        };

        WavePath.LayoutTransform = new RotateTransform(ToolbarDocking.WaveAngle(Dock));

        // Le creux de la vague est plus épais côté bord : l'icône s'y loge.
        WaveContent.Margin = Dock switch
        {
            ToolbarDock.Top => new Thickness(0, 0, 0, WaveIconShift),
            ToolbarDock.Bottom => new Thickness(0, WaveIconShift, 0, 0),
            ToolbarDock.Left => new Thickness(0, 0, WaveIconShift, 0),
            ToolbarDock.Right => new Thickness(WaveIconShift, 0, 0, 0),
            _ => default,
        };
    }

    // Une barre plus grande que son écran passe sur une colonne ou une ligne supplémentaire.
    private void UpdateSizeLimit()
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        var monitor = FindBarMonitor();
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;

        ToolbarPanel.MaxWidth = EffectiveVertical ? double.PositiveInfinity : (DockArea(monitor).Width / scale) - WindowChrome;
        ToolbarPanel.MaxHeight = EffectiveVertical ? (DockArea(monitor).Height / scale) - WindowChrome : double.PositiveInfinity;
    }

    private void CreateSwatches()
    {
        var style = (Style)FindResource("SwatchStyle");

        for (var i = 0; i < PenState.QuickColorCount; i++)
        {
            var index = i;
            var swatch = new ToggleButton
            {
                Style = style,
                ToolTip = "Clic : choisir. Clic droit : modifier",
            };

            AutomationProperties.SetName(swatch, $"Couleur rapide {index + 1}");
            swatch.Checked += (_, _) => OnExclusiveToggled(true, () => _pen.SelectQuickColor(index));
            swatch.Unchecked += (_, _) => OnExclusiveToggled(false, () => { });
            swatch.MouseRightButtonUp += (_, _) =>
            {
                _pen.SelectQuickColor(index);
                OpenColorPicker(replaceQuickColor: true);
            };

            _swatches.Add(swatch);
            SwatchPanel.Children.Add(swatch);
        }
    }

    private void Refresh()
    {
        _refreshing = true;

        MouseButton.IsChecked = !_state.CanDraw;
        HideButton.IsChecked = !_state.AnnotationsVisible;
        FadeButton.IsChecked = _state.FadingEnabled;
        WhiteboardButton.IsChecked = _state.Background == BoardBackground.Whiteboard;
        BlackboardButton.IsChecked = _state.Background == BoardBackground.Blackboard;

        foreach (var button in _toolButtons)
        {
            button.IsChecked = _state.CanDraw && _state.Tool == ToolOf(button);
        }

        for (var i = 0; i < _swatches.Count; i++)
        {
            var color = _pen.QuickColors[i];
            _swatches[i].Background = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
            _swatches[i].IsChecked = _pen.SelectedQuickColor == i;
        }

        for (var i = 0; i < _sizeButtons.Length; i++)
        {
            _sizeButtons[i].IsChecked = _pen.Size == PenState.SizePresets[i];
        }

        SizeSlider.Value = _pen.Size;

        _refreshing = false;

        var history = _histories.Current;
        UndoButton.IsEnabled = _state.AnnotationsVisible && history.CanUndo;
        RedoButton.IsEnabled = _state.AnnotationsVisible && history.CanRedo;
        ClearButton.IsEnabled = _state.AnnotationsVisible;

        SetDrawHotKeys(_state.CanDraw && !_dialogOpen && !_overlays.IsEditingText);

        // Ouvrir ou fermer un tableau montre ou cache la barre des tâches : la barre attachée suit.
        if (_placedBackground != _state.Background)
        {
            _placedBackground = _state.Background;

            if (Dock != ToolbarDock.None)
            {
                UpdateSizeLimit();
                PlaceAfterLayout();
            }
        }

        Win32.BringToTop(_handle);
    }

    private void OpenColorPicker(bool replaceQuickColor)
    {
        var quickColor = _pen.SelectedQuickColor;

        RunDialog(() =>
        {
            var picker = new ColorPickerWindow(_pen.Color, quickColor is not null, replaceQuickColor) { Owner = this };

            if (picker.ShowDialog() != true)
            {
                return;
            }

            if (picker.ReplaceQuickColor && quickColor is { } index)
            {
                _pen.ReplaceQuickColor(index, picker.SelectedColor);
            }
            else
            {
                _pen.SetColor(picker.SelectedColor);
            }
        });
    }

    // Échap et Ctrl+Z doivent rester utilisables dans les fenêtres de dialogue.
    private void RunDialog(Action showDialog)
    {
        _dialogOpen = true;
        SetDrawHotKeys(false);

        showDialog();

        _dialogOpen = false;
        Refresh();
    }

    // Ces touches ne sont réservées qu'en mode Dessin, pour ne pas gêner les autres applications.
    private void SetDrawHotKeys(bool enabled)
    {
        if (enabled == _drawHotKeysRegistered)
        {
            return;
        }

        _drawHotKeysRegistered = enabled;

        for (var i = 0; i < _drawHotKeys.Length; i++)
        {
            if (enabled)
            {
                var hotKey = _drawHotKeys[i];
                Register(FirstDrawHotKey + i, hotKey.Modifiers, hotKey.Key, hotKey.Label);
            }
            else
            {
                Win32.UnregisterGlobalHotKey(_handle, FirstDrawHotKey + i);
            }
        }
    }

    private (uint Modifiers, uint Key, string Label, Action Run)[] CreateDrawHotKeys()
    {
        (uint, uint, string, Action) Letter(char letter, Action run) => (0, letter, letter.ToString(), run);
        (uint, uint, string, Action) Tool(char letter, DrawingTool tool) => Letter(letter, () => _state.SelectTool(tool));

        return
        [
            (0, Win32.VkEscape, "Échap", _state.EnterMouse),
            (Win32.ModControl, Win32.VkZ, "Ctrl+Z", () => _histories.Current.Undo()),
            (Win32.ModControl, Win32.VkY, "Ctrl+Y", () => _histories.Current.Redo()),
            (0, Win32.VkDelete, "Suppr", _overlays.ClearAll),
            Tool('P', DrawingTool.Pen),
            Tool('H', DrawingTool.Highlighter),
            Tool('E', DrawingTool.Eraser),
            Tool('L', DrawingTool.Line),
            Tool('A', DrawingTool.Arrow),
            Tool('R', DrawingTool.Rectangle),
            Tool('O', DrawingTool.Ellipse),
            Tool('T', DrawingTool.Text),
            Letter('W', () => _state.ToggleBoard(BoardBackground.Whiteboard)),
            Letter('B', () => _state.ToggleBoard(BoardBackground.Blackboard)),
            Letter('F', _state.ToggleFading),
            Letter('V', _state.ToggleAnnotations),
        ];
    }

    private void Register(int id, uint modifiers, uint virtualKey, string label)
    {
        if (Win32.RegisterGlobalHotKey(_handle, id, modifiers, virtualKey) || _hotKeyFailureReported)
        {
            return;
        }

        _hotKeyFailureReported = true;
        MessageBox.Show(
            $"Le raccourci {label} est déjà utilisé par une autre application. Les boutons de la barre restent disponibles.",
            "Legendary Pen",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private IntPtr OnWindowMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Un clic sur le bouton de la barre des tâches ramène la barre devant les overlays.
        if (message == Win32.WmActivate && wParam != IntPtr.Zero)
        {
            SetCollapsed(false);
            Win32.BringToTop(_handle);
            return IntPtr.Zero;
        }

        if (message != Win32.WmHotKey)
        {
            return IntPtr.Zero;
        }

        handled = true;

        var id = wParam.ToInt32();

        if (id == ToggleModeHotKey)
        {
            _state.ToggleMode();
        }
        else if (id >= FirstDrawHotKey && id < FirstDrawHotKey + _drawHotKeys.Length)
        {
            _drawHotKeys[id - FirstDrawHotKey].Run();
        }

        return IntPtr.Zero;
    }

    private void OnGripPressed(object sender, MouseButtonEventArgs e)
    {
        DragMove();
        _lastInteraction = DateTime.UtcNow;

        // Le bord se cherche sur l'écran où le pointeur vient de déposer la barre.
        var previousDock = Dock;
        var pointer = Win32.GetPointer();
        var monitor = _monitors.FirstOrDefault(candidate => candidate.Bounds.Contains(pointer.X, pointer.Y)) ?? FindBarMonitor();
        var threshold = (int)(SnapDistance * VisualTreeHelper.GetDpi(this).DpiScaleX);
        Dock = ToolbarDocking.Detect(pointer, DockArea(monitor), threshold);
        DockMonitor = Dock == ToolbarDock.None ? null : monitor.DeviceName;

        if (Dock != previousDock)
        {
            ApplyOrientation();
        }

        UpdateSizeLimit();
        PlaceAfterLayout();
        PositionChanged?.Invoke();
    }

    private void OnOrientationClicked(object sender, RoutedEventArgs e)
    {
        // Tourner une barre attachée la détache : l'orientation d'un bord est imposée.
        IsVertical = !EffectiveVertical;
        Dock = ToolbarDock.None;
        DockMonitor = null;
        ApplyOrientation();
        PlaceAfterLayout();
        OrientationChanged?.Invoke();
    }

    // Checked et Unchecked couvrent la souris et l'accessibilité, contrairement à Click.
    // Désélectionner le choix actif ne change rien : Refresh rétablit alors l'affichage.
    private void OnMouseToggled(object sender, RoutedEventArgs e)
    {
        OnExclusiveToggled(MouseButton.IsChecked == true, _state.EnterMouse);
    }

    private void OnToolToggled(object sender, RoutedEventArgs e)
    {
        var button = (ToggleButton)sender;
        OnExclusiveToggled(button.IsChecked == true, () => _state.SelectTool(ToolOf(button)));
    }

    private void OnFadeToggled(object sender, RoutedEventArgs e)
    {
        if (!_refreshing)
        {
            _state.ToggleFading();
        }
    }

    private void OnBoardToggled(object sender, RoutedEventArgs e)
    {
        if (!_refreshing)
        {
            _state.ToggleBoard(Enum.Parse<BoardBackground>((string)((ToggleButton)sender).Tag));
        }
    }

    private static DrawingTool ToolOf(ToggleButton button) => Enum.Parse<DrawingTool>((string)button.Tag);

    private void OnSizePresetToggled(object sender, RoutedEventArgs e)
    {
        var button = (ToggleButton)sender;
        var preset = PenState.SizePresets[int.Parse((string)button.Tag)];
        OnExclusiveToggled(button.IsChecked == true, () => _pen.SetSize(preset));
    }

    private void OnExclusiveToggled(bool selected, Action select)
    {
        if (_refreshing)
        {
            return;
        }

        if (selected)
        {
            select();
        }
        else
        {
            Refresh();
        }
    }

    private void OnSizeSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_refreshing && IsInitialized)
        {
            _pen.SetSize(e.NewValue);
        }
    }

    private void OnMoreColorsClicked(object sender, RoutedEventArgs e) => OpenColorPicker(replaceQuickColor: false);

    private void OnUpdateClicked(object sender, RoutedEventArgs e) => OfferUpdate();

    private void OnUndoClicked(object sender, RoutedEventArgs e) => _histories.Current.Undo();

    private void OnRedoClicked(object sender, RoutedEventArgs e) => _histories.Current.Redo();

    private void OnClearClicked(object sender, RoutedEventArgs e) => _overlays.ClearAll();

    private void OnHideToggled(object sender, RoutedEventArgs e)
    {
        if (!_refreshing)
        {
            _state.ToggleAnnotations();
        }
    }

    private void OnQuitClicked(object sender, RoutedEventArgs e) => Application.Current.Shutdown();
}
