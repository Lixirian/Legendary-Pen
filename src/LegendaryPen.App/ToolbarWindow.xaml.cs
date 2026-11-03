using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using LegendaryPen.Core;

namespace LegendaryPen.App;

public partial class ToolbarWindow : Window
{
    private const int ToggleModeHotKey = 1;
    private const int ExitDrawHotKey = 2;
    private const int UndoHotKey = 3;
    private const int RedoHotKey = 4;

    private readonly AppState _state;
    private readonly PenState _pen;
    private readonly ActionHistory _history;
    private readonly OverlayManager _overlays;
    private readonly UpdateService? _updates;
    private readonly PixelPoint? _savedPosition;
    private readonly List<ToggleButton> _swatches = [];
    private readonly ToggleButton[] _sizeButtons;

    private IReadOnlyList<MonitorArea> _monitors;
    private UpdateManifest? _availableUpdate;
    private IntPtr _handle;
    private bool _drawHotKeysRegistered;
    private bool _hotKeyFailureReported;
    private bool _refreshing;
    private bool _dialogOpen;

    public ToolbarWindow(
        AppState state,
        PenState pen,
        ActionHistory history,
        OverlayManager overlays,
        UpdateService? updates,
        IReadOnlyList<MonitorArea> monitors,
        PixelPoint? savedPosition)
    {
        _state = state;
        _pen = pen;
        _history = history;
        _overlays = overlays;
        _updates = updates;
        _monitors = monitors;
        _savedPosition = savedPosition;

        InitializeComponent();

        _sizeButtons = [SmallSizeButton, MediumSizeButton, LargeSizeButton];
        CreateSwatches();

        _state.Changed += Refresh;
        _pen.Changed += Refresh;
        _history.Changed += Refresh;
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

    public event Action? PositionChanged;

    /// <summary>Remet la barre au premier plan et sur un écran disponible.</summary>
    public void Reveal()
    {
        PlaceOnScreen(Position);
        Win32.BringToTop(_handle);
    }

    public void UpdateMonitors(IReadOnlyList<MonitorArea> monitors)
    {
        _monitors = monitors;
        Reveal();
    }

    /// <summary>Affiche la pastille, sans interrompre le travail en cours.</summary>
    public void ShowUpdateAvailable(UpdateManifest manifest)
    {
        _availableUpdate = manifest;
        UpdateButton.Visibility = Visibility.Visible;
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
        Win32.MakeFloatingToolWindow(_handle);
        HwndSource.FromHwnd(_handle)!.AddHook(OnWindowMessage);

        Register(ToggleModeHotKey, Win32.ModControl | Win32.ModShift, Win32.VkSpace, "Ctrl+Maj+Espace");
        Refresh();
    }

    protected override void OnClosed(EventArgs e)
    {
        Win32.UnregisterGlobalHotKey(_handle, ToggleModeHotKey);
        SetDrawHotKeys(false);
        base.OnClosed(e);
    }

    // La taille finale n'est connue qu'après le premier rendu, car elle dépend du contenu.
    private void OnFirstRender(object? sender, EventArgs e)
    {
        PlaceOnScreen(_savedPosition);
        Opacity = 1;
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
        PenButton.IsChecked = _state.CanDraw;
        HideButton.IsChecked = !_state.AnnotationsVisible;

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

        UndoButton.IsEnabled = _state.AnnotationsVisible && _history.CanUndo;
        RedoButton.IsEnabled = _state.AnnotationsVisible && _history.CanRedo;
        ClearButton.IsEnabled = _state.AnnotationsVisible;

        SetDrawHotKeys(_state.CanDraw && !_dialogOpen);
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

        if (enabled)
        {
            Register(ExitDrawHotKey, 0, Win32.VkEscape, "Échap");
            Register(UndoHotKey, Win32.ModControl, Win32.VkZ, "Ctrl+Z");
            Register(RedoHotKey, Win32.ModControl, Win32.VkY, "Ctrl+Y");
        }
        else
        {
            Win32.UnregisterGlobalHotKey(_handle, ExitDrawHotKey);
            Win32.UnregisterGlobalHotKey(_handle, UndoHotKey);
            Win32.UnregisterGlobalHotKey(_handle, RedoHotKey);
        }
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
        if (message != Win32.WmHotKey)
        {
            return IntPtr.Zero;
        }

        handled = true;

        switch (wParam.ToInt32())
        {
            case ToggleModeHotKey:
                _state.ToggleMode();
                break;
            case ExitDrawHotKey:
                _state.EnterMouse();
                break;
            case UndoHotKey:
                _history.Undo();
                break;
            case RedoHotKey:
                _history.Redo();
                break;
        }

        return IntPtr.Zero;
    }

    private void OnGripPressed(object sender, MouseButtonEventArgs e)
    {
        DragMove();
        PositionChanged?.Invoke();
    }

    // Checked et Unchecked couvrent la souris et l'accessibilité, contrairement à Click.
    // Désélectionner le choix actif ne change rien : Refresh rétablit alors l'affichage.
    private void OnMouseToggled(object sender, RoutedEventArgs e)
    {
        OnExclusiveToggled(MouseButton.IsChecked == true, _state.EnterMouse);
    }

    private void OnPenToggled(object sender, RoutedEventArgs e)
    {
        OnExclusiveToggled(PenButton.IsChecked == true, _state.EnterDraw);
    }

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

    private void OnUndoClicked(object sender, RoutedEventArgs e) => _history.Undo();

    private void OnRedoClicked(object sender, RoutedEventArgs e) => _history.Redo();

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
