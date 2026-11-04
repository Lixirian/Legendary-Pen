using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using LegendaryPen.Core;

namespace LegendaryPen.App;

/// <summary>
/// Panneau des calques du fond affiché. Il ne prend jamais le focus et reste ouvert pendant le dessin ;
/// le haut de la liste est le calque du dessus.
/// </summary>
public partial class LayersWindow : Window
{
    private const string VisibilityGlyph = "";
    private const string UpGlyph = "";
    private const string DownGlyph = "";

    private readonly AppState _state;
    private readonly LayerStacks _stacks;
    private readonly SurfaceHistories _histories;
    private readonly Action _commitText;

    private IntPtr _handle;
    private BoardBackground _background;
    private LayerStack _stack;
    private bool _placed;

    /// <param name="commitText">Valide le texte en cours de saisie, avant un changement de pile : il doit atterrir sur un calque encore présent.</param>
    public LayersWindow(AppState state, LayerStacks stacks, SurfaceHistories histories, Action commitText)
    {
        _state = state;
        _stacks = stacks;
        _histories = histories;
        _commitText = commitText;
        _background = state.Background;
        _stack = stacks.For(_background);

        InitializeComponent();

        // Invisible jusqu'à son placement, pour ne pas apparaître à une position par défaut.
        Opacity = 0;
        _stack.Changed += Rebuild;
        _state.Changed += OnStateChanged;
        SizeChanged += (_, _) => PlacementRequested?.Invoke();
        Rebuild();
    }

    /// <summary>Remonte le panneau : il se place sous la barre d'outils, au-dessus des overlays.</summary>
    public void Raise()
    {
        if (_handle != IntPtr.Zero)
        {
            Win32.BringToTop(_handle);
        }
    }

    /// <summary>Vrai une fois la fenêtre créée : avant, elle n'a ni identifiant ni taille.</summary>
    public bool IsPlaceable => _handle != IntPtr.Zero;

    public IntPtr Handle => _handle;

    /// <summary>Vrai le temps d'une capture : aucun placement ne doit alors faire réapparaître ni remonter le panneau.</summary>
    public bool IsHiddenForCapture { get; private set; }

    /// <summary>Levé quand la taille du panneau change : il faut le replacer.</summary>
    public event Action? PlacementRequested;

    public bool SetExcludedFromCapture(bool excluded) => Win32.SetExcludedFromCapture(_handle, excluded);

    public void SetHiddenForCapture(bool hidden)
    {
        IsHiddenForCapture = hidden;
        ApplyOpacity();
    }

    // Invisible tant qu'il n'est pas à sa place, et pendant une capture.
    private void ApplyOpacity() => Opacity = _placed && !IsHiddenForCapture ? 1 : 0;

    /// <summary>
    /// Place le panneau contre la barre, du côté où il tient, sans jamais passer dessous ni sortir de la zone ;
    /// la liste défile si la zone est trop basse. Le rend visible, sauf pendant une capture.
    /// </summary>
    public void PlaceBeside(PixelRect bar, bool vertical, PixelRect area)
    {
        var size = Win32.GetBounds(_handle);
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleY;

        // Hauteur naturelle : celle de la liste complète, indépendante du plafond posé plus tôt.
        var natural = (int)Math.Ceiling((ActualHeight - RowScroll.ActualHeight + Rows.DesiredSize.Height) * scale);
        var place = LayersPanelPlacement.Beside(bar, vertical, area, size.Width, natural);

        MaxHeight = place.MaxHeight is { } height ? height / scale : double.PositiveInfinity;
        Win32.MoveTo(_handle, new PixelPoint(place.X, place.Y));
        _placed = true;
        ApplyOpacity();

        if (!IsHiddenForCapture)
        {
            Raise();
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _handle = new WindowInteropHelper(this).Handle;
        Win32.SetNoActivate(_handle, true);
    }

    protected override void OnClosed(EventArgs e)
    {
        _stack.Changed -= Rebuild;
        _state.Changed -= OnStateChanged;
        base.OnClosed(e);
    }

    // Le panneau suit le fond affiché : chaque fond a sa propre pile.
    private void OnStateChanged()
    {
        if (_state.Background == _background)
        {
            return;
        }

        _stack.Changed -= Rebuild;
        _background = _state.Background;
        _stack = _stacks.For(_background);
        _stack.Changed += Rebuild;
        Rebuild();
    }

    private void Rebuild()
    {
        Rows.Children.Clear();

        var layers = _stack.Layers;
        var visibleCount = layers.Count(layer => layer.IsVisible);

        for (var index = layers.Count - 1; index >= 0; index--)
        {
            Rows.Children.Add(CreateRow(layers[index], index, layers.Count, visibleCount));
        }

        AddButton.IsEnabled = _stack.CanAdd;
        RemoveButton.IsEnabled = _stack.CanRemove;
    }

    private UIElement CreateRow(LayerInfo layer, int index, int count, int visibleCount)
    {
        var active = layer.Id == _stack.Active.Id;
        var id = layer.Id;

        var visibility = new ToggleButton
        {
            Style = (Style)FindResource("IconButton"),
            Content = VisibilityGlyph,
            IsChecked = layer.IsVisible,
            // Le dernier calque visible ne se masque pas : le bouton le montre au lieu de ne rien faire.
            IsEnabled = !(layer.IsVisible && visibleCount <= 1),
            ToolTip = Loc.T("Afficher ou masquer le calque"),
        };
        if (!visibility.IsEnabled)
        {
            // Grisé mais encore lisible : il ne doit pas ressembler à un calque masqué.
            visibility.Opacity = 0.6;
        }

        AutomationProperties.SetName(visibility, Loc.T("Afficher ou masquer le calque"));
        visibility.Click += (_, _) => _stack.SetVisible(id, !layer.IsVisible);

        var name = new Button
        {
            Style = (Style)FindResource("LayerNameButton"),
            Content = new TextBlock
            {
                Text = layer.Name,
                FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
                Opacity = layer.IsVisible ? 1 : 0.6,
            },
        };
        AutomationProperties.SetName(name, layer.Name);
        name.Click += (_, _) => _stack.Select(id);

        var up = CreateMoveButton(UpGlyph, Loc.T("Monter le calque"), index < count - 1, () => ChangeStack(() => _stack.MoveUp(id)));
        var down = CreateMoveButton(DownGlyph, Loc.T("Descendre le calque"), index > 0, () => ChangeStack(() => _stack.MoveDown(id)));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(name, 1);
        Grid.SetColumn(up, 2);
        Grid.SetColumn(down, 3);
        grid.Children.Add(visibility);
        grid.Children.Add(name);
        grid.Children.Add(up);
        grid.Children.Add(down);

        // Le calque actif porte un liseré de la couleur d'accent, en plus du texte renforcé : la couleur seule ne suffit pas.
        return new Border
        {
            Height = 38,
            Margin = new Thickness(0, 0, 0, 3),
            Padding = new Thickness(3, 0, 3, 0),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(3, 0, 0, 0),
            BorderBrush = active ? (Brush)FindResource("AccentBrush") : Brushes.Transparent,
            Background = active ? (Brush)FindResource("RaisedBrush") : Brushes.Transparent,
            Child = grid,
        };
    }

    private Button CreateMoveButton(string glyph, string label, bool enabled, Action move)
    {
        var button = new Button
        {
            Style = (Style)FindResource("IconButton"),
            Content = glyph,
            IsEnabled = enabled,
            ToolTip = label,
        };
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) => move();
        return button;
    }

    private void OnAddClicked(object sender, RoutedEventArgs e)
    {
        if (_stack.CanAdd)
        {
            ChangeStack(() => _stack.Add());
        }
    }

    private void OnRemoveClicked(object sender, RoutedEventArgs e)
    {
        if (_stack.CanRemove)
        {
            ChangeStack(() => _stack.Remove(_stack.Active.Id));
        }
    }

    // Ajout, suppression et déplacement s'annulent dans le même historique que le dessin ; sélection et visibilité en restent dehors.
    private void ChangeStack(Action change)
    {
        // Le texte en cours atterrit sur un calque vivant, avant que l'état soit capturé.
        _commitText();

        LayerStackAction.Apply(_stack, _histories.For(_background), change);
    }
}
