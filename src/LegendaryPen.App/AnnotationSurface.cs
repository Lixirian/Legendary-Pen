using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using LegendaryPen.Core;

namespace LegendaryPen.App;

/// <summary>Annotations d'un écran pour un fond donné, et saisie de tous les outils.</summary>
internal sealed class AnnotationSurface : Grid
{
    private const double MinFontSize = 16;
    private const double FontSizePerPenSize = 4;
    private const double MinHighlightLength = 4;
    private const double MinHighlightPadding = 2;
    private const double HighlightPaddingPerHeight = 0.12;
    private const double HighlightTipWidth = 1;

    private readonly AppState _state;
    private readonly PenState _pen;
    private readonly ActionHistory _history;
    private readonly LiveOptions _options;
    private readonly Action<bool> _textEditingChanged;
    private readonly Func<double, double, double, Rect?> _findTextUnder;
    private readonly LayerStack _stack;

    // Tous les calques déjà vus par cette surface. Un calque retiré de la pile reste ici, hors de l'arbre visuel, pour qu'Annuler le retrouve.
    private readonly Dictionary<Guid, AnnotationLayer> _layers = [];
    private readonly Grid _layerHost = new();
    private readonly Canvas _editorLayer = new();
    private readonly FadeLayer _fadeLayer = new();
    private readonly HashSet<TextBlock> _fadingTexts = [];

    private AnnotationLayer _activeLayer;
    private double? _toolScale;
    private TextBox? _editor;
    private Stroke? _shapePreview;
    private InkCanvas? _shapeInk;
    private InkPoint _shapeStart;
    private bool _erasingText;
    private long _strokeDownTime;
    private (long EndTime, StrokeBounds Bounds)? _lastPenStroke;

    public AnnotationSurface(
        AppState state,
        PenState pen,
        ActionHistory history,
        LayerStack stack,
        LiveOptions options,
        Action<bool> textEditingChanged,
        Func<double, double, double, Rect?> findTextUnder)
    {
        _findTextUnder = findTextUnder;
        _state = state;
        _pen = pen;
        _history = history;
        _stack = stack;
        _options = options;
        _textEditingChanged = textEditingChanged;

        Background = Brushes.Transparent;

        _activeLayer = LayerFor(stack.Active.Id);
        _activeLayer.IsHitTestVisible = true;

        // L'encre évanescente et le champ de texte restent communs, au-dessus de tous les calques.
        Children.Add(_layerHost);
        Children.Add(_fadeLayer);
        Children.Add(_editorLayer);

        SyncLayers();
        stack.Changed += SyncLayers;

        // Le stylet et la souris passent tous les deux ici, avant que l'encre ne démarre.
        PreviewStylusDown += (_, _) => _strokeDownTime = Environment.TickCount64;
        PreviewMouseLeftButtonDown += (_, _) => _strokeDownTime = Environment.TickCount64;
        PreviewMouseLeftButtonDown += OnPointerDown;
        PreviewMouseMove += OnPointerMove;
        PreviewMouseLeftButtonUp += OnPointerUp;
        PreviewMouseWheel += OnPointerWheel;
    }

    /// <summary>L'encre du calque actif.</summary>
    public InkCanvas Ink => _activeLayer.Ink;

    /// <summary>Reflète l'outil, la couleur et la taille sur cette surface.</summary>
    public void ApplyTool(double scale)
    {
        _toolScale = scale;
        Ink.DefaultDrawingAttributes = CreateAttributes(forPenInput: true);

        var drawing = _state.CanDraw;
        Ink.EditingMode = !drawing ? InkCanvasEditingMode.None : _state.Tool switch
        {
            DrawingTool.Pen or DrawingTool.Highlighter => InkCanvasEditingMode.Ink,
            DrawingTool.Eraser => InkCanvasEditingMode.EraseByStroke,
            _ => InkCanvasEditingMode.None,
        };

        // La pointe gomme du stylet efface quel que soit l'outil de tracé.
        Ink.EditingModeInverted = drawing ? InkCanvasEditingMode.EraseByStroke : InkCanvasEditingMode.None;

        var cursor = drawing ? ToolCursors.Create(_state.Tool, _pen, scale) : Cursors.Arrow;
        Ink.Cursor = cursor;
        Cursor = cursor;
    }

    /// <summary>
    /// Instantané de chaque calque non vide de la pile, masqué ou non. Un calque retiré n'en fait pas partie :
    /// annuler son retrait doit rendre son contenu tel quel.
    /// </summary>
    public IReadOnlyList<SurfaceSnapshot> TakeSnapshotAndClearFading()
    {
        CommitText();
        _fadeLayer.Clear();

        // Un texte évanescent peut appartenir à un autre calque que le calque actif.
        foreach (var text in _fadingTexts)
        {
            (text.Parent as Panel)?.Children.Remove(text);
        }

        _fadingTexts.Clear();

        var snapshots = new List<SurfaceSnapshot>();

        foreach (var layer in StackedLayers())
        {
            var texts = layer.Texts.Children.OfType<TextBlock>().ToList();

            if (layer.Ink.Strokes.Count > 0 || texts.Count > 0)
            {
                snapshots.Add(new SurfaceSnapshot(layer.Ink, new StrokeCollection(layer.Ink.Strokes), layer.Texts, texts));
            }
        }

        return snapshots;
    }

    /// <summary>
    /// Contenu à conserver, sans l'encre évanescente : tous les calques de la pile, masqués ou vides compris, pour que la
    /// pile revienne telle quelle. Un calque retiré n'en fait pas partie. Renvoie null si la surface est vide et que la
    /// pile est celle d'origine.
    /// </summary>
    public SavedSurface? ToSaved()
    {
        var layers = new List<SavedLayer>();
        var hasContent = false;

        foreach (var info in _stack.Layers)
        {
            var layer = LayerFor(info.Id);
            var texts = layer.Texts.Children.OfType<TextBlock>().Except(_fadingTexts).ToList();
            hasContent |= layer.Ink.Strokes.Count > 0 || texts.Count > 0;

            using var strokes = new MemoryStream();

            if (layer.Ink.Strokes.Count > 0)
            {
                layer.Ink.Strokes.Save(strokes, compress: true);
            }

            layers.Add(new SavedLayer(info.Id, info.Number, info.IsVisible, strokes.ToArray(), texts.Select(ToSavedText).ToList()));
        }

        var state = _stack.Capture();
        var isOriginalStack = state.Layers.Count == 1 && state.Layers[0].Number == 1 && state.NextNumber == 2;

        if (!hasContent && isOriginalStack)
        {
            return null;
        }

        return new SavedSurface([], [], layers, state.ActiveId, state.NextNumber);
    }

    private static SavedText ToSavedText(TextBlock text)
    {
        var color = ((SolidColorBrush)text.Foreground).Color;

        return new SavedText(
            text.Text,
            Canvas.GetLeft(text),
            Canvas.GetTop(text),
            text.FontSize,
            text.FontFamily.Source,
            new InkColor(color.R, color.G, color.B).ToHex());
    }

    /// <summary>
    /// Remet un contenu enregistré. Un fichier sans calques (versions 0.6 à 0.8) va dans le calque actif, la pile
    /// restant telle quelle. Sinon la pile est rétablie par le premier fichier relu pour ce fond, puis les fichiers des
    /// autres écrans rattachent leur contenu par identifiant de calque. Des traits illisibles sont ignorés sans gêner le reste.
    /// </summary>
    public void Restore(SavedSurface saved, bool stackAlreadyRestored)
    {
        if (saved.Layers is not { } layers)
        {
            RestoreContent(_activeLayer, saved.Strokes, saved.Texts);

            return;
        }

        var targets = LayerRestoration.RestoreInto(_stack, saved, stackAlreadyRestored);

        foreach (var layer in layers)
        {
            RestoreContent(LayerFor(targets[layer.Id]), layer.Strokes, layer.Texts);
        }
    }

    private void RestoreContent(AnnotationLayer target, byte[] savedStrokes, List<SavedText> savedTexts)
    {
        if (savedStrokes.Length > 0)
        {
            try
            {
                using var strokes = new MemoryStream(savedStrokes);
                target.Ink.Strokes.Add(new StrokeCollection(strokes));
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or InvalidOperationException)
            {
                System.Diagnostics.Debug.WriteLine(exception.Message);
            }
        }

        foreach (var item in savedTexts)
        {
            var color = InkColor.TryParse(item.Color, out var parsed) ? parsed : _pen.Color;
            var text = new TextBlock
            {
                Text = item.Text,
                FontSize = item.FontSize,
                FontFamily = new FontFamily(item.FontFamily),
                Foreground = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B)),
            };

            Canvas.SetLeft(text, item.Left);
            Canvas.SetTop(text, item.Top);
            target.Texts.Children.Add(text);
        }
    }

    /// <summary>Valide le texte en cours de saisie. Sans effet s'il n'y en a pas.</summary>
    public void CommitText()
    {
        if (_editor is not { } editor)
        {
            return;
        }

        _editor = null;
        _editorLayer.Children.Clear();
        _textEditingChanged(false);

        if (string.IsNullOrWhiteSpace(editor.Text))
        {
            return;
        }

        var text = new TextBlock
        {
            Text = editor.Text,
            FontSize = editor.FontSize,
            Foreground = editor.Foreground,
            FontFamily = editor.FontFamily,
        };

        // Le contenu du champ a une marge interne : le texte final reste au même endroit.
        Canvas.SetLeft(text, Canvas.GetLeft(editor) + TextEditorOffset);
        Canvas.SetTop(text, Canvas.GetTop(editor) + TextEditorOffset);
        _activeLayer.Texts.Children.Add(text);

        if (_state.FadingEnabled)
        {
            FadeOut(text, _activeLayer.Texts);
        }
        else
        {
            _history.Record(new AddTextAction(_activeLayer.Texts, text));
        }
    }

    private const double TextEditorOffset = 3;

    /// <summary>Le calque d'un identifiant, créé à la première demande avec ses abonnements, une fois pour toutes.</summary>
    private AnnotationLayer LayerFor(Guid id)
    {
        if (!_layers.TryGetValue(id, out var layer))
        {
            layer = new AnnotationLayer { IsHitTestVisible = false };
            layer.Ink.StrokeCollected += OnStrokeCollected;
            layer.Ink.StrokeErasing += OnStrokeErasing;
            _layers[id] = layer;
        }

        return layer;
    }

    /// <summary>Les calques de la pile, de bas en haut.</summary>
    private List<AnnotationLayer> StackedLayers() => _stack.Layers.Select(info => LayerFor(info.Id)).ToList();

    /// <summary>Aligne les enfants sur la pile : présence, ordre, visibilité et calque actif.</summary>
    private void SyncLayers()
    {
        // Le texte en cours appartient au calque qui était actif : il est validé avant tout changement.
        CommitText();

        var children = _layerHost.Children;
        var stacked = StackedLayers();

        foreach (var removed in children.Cast<AnnotationLayer>().Except(stacked).ToList())
        {
            children.Remove(removed);
        }

        // Seuls les calques mal placés sont déplacés : les autres gardent leur place dans l'arbre visuel.
        for (var index = 0; index < stacked.Count; index++)
        {
            if (index >= children.Count || !ReferenceEquals(children[index], stacked[index]))
            {
                children.Remove(stacked[index]);
                children.Insert(index, stacked[index]);
            }

            stacked[index].Visibility = _stack.Layers[index].IsVisible ? Visibility.Visible : Visibility.Hidden;
        }

        var active = LayerFor(_stack.Active.Id);

        if (ReferenceEquals(active, _activeLayer))
        {
            return;
        }

        _activeLayer.Ink.EditingMode = InkCanvasEditingMode.None;
        _activeLayer.Ink.EditingModeInverted = InkCanvasEditingMode.None;
        _activeLayer.IsHitTestVisible = false;

        _activeLayer = active;
        _activeLayer.IsHitTestVisible = true;

        // Sans échelle connue, l'outil n'a encore jamais été appliqué : OverlayWindow.Apply s'en chargera.
        if (_toolScale is { } scale)
        {
            ApplyTool(scale);
        }
    }

    // Le calque actif a pu changer pendant le tracé : l'encre à modifier est celle qui a recueilli le trait.
    private void OnStrokeCollected(object sender, InkCanvasStrokeCollectedEventArgs e)
    {
        var ink = (InkCanvas)sender;
        var isPen = !e.Stroke.DrawingAttributes.IsHighlighter;
        var followsWriting = isPen && FollowsWriting(e.Stroke);

        if (!isPen && _options.HighlightSnapEnabled && CleanHighlight(e.Stroke) is { } clean)
        {
            ink.Strokes.Remove(e.Stroke);
            ink.Strokes.Add(clean);
            FinishStroke(ink, clean);
        }
        else if (isPen && !followsWriting && _options.ShapeRecognitionEnabled && CleanShape(e.Stroke) is { } shape)
        {
            if (_state.FadingEnabled)
            {
                // Évanescent : FinishStroke ne touche pas à l'historique, seule la forme nette s'efface.
                ink.Strokes.Remove(e.Stroke);
                ink.Strokes.Add(shape);
                FinishStroke(ink, shape);
            }
            else
            {
                FinishStroke(ink, e.Stroke);
                ink.Strokes.Remove(e.Stroke);
                ink.Strokes.Add(shape);
                _history.Record(new ReplaceStrokeAction(ink, e.Stroke, shape));
            }
        }
        else
        {
            FinishStroke(ink, e.Stroke);
        }
    }

    /// <summary>Compare ce tracé au tracé au stylo précédent, reconnu ou non, puis le retient à sa place.</summary>
    private bool FollowsWriting(Stroke stroke)
    {
        var bounds = StrokeBounds.Of(PointsOf(stroke));

        // La pause se mesure jusqu'à la pose de la pointe : la durée du tracé lui-même n'en fait pas partie.
        var followsWriting = _lastPenStroke is { } last
            && ShapeRecognizer.LooksLikeWriting(TimeSpan.FromMilliseconds(_strokeDownTime - last.EndTime), last.Bounds, bounds);

        _lastPenStroke = (Environment.TickCount64, bounds);

        return followsWriting;
    }

    /// <summary>Remplace un tracé au stylo par la forme nette qu'il dessine, ou null si le doute subsiste.</summary>
    private Stroke? CleanShape(Stroke raw)
    {
        if (ShapeRecognizer.Recognize(PointsOf(raw)) is not { } recognized)
        {
            return null;
        }

        var attributes = raw.DrawingAttributes.Clone();
        attributes.FitToCurve = false;
        attributes.IgnorePressure = true;

        var outline = ShapeGeometry.Build(recognized.Tool, recognized.Start, recognized.End, false, raw.DrawingAttributes.Width);

        return new Stroke(ToStylusPoints(outline), attributes);
    }

    private static List<InkPoint> PointsOf(Stroke stroke) =>
        stroke.StylusPoints.Select(point => new InkPoint(point.X, point.Y)).ToList();

    /// <summary>Remplace un surlignage à main levée par un trait droit, calé sur le texte s'il y en a.</summary>
    private Stroke? CleanHighlight(Stroke raw)
    {
        var (start, end, isHorizontal) = HighlightLine.Straighten(PointsOf(raw));
        var length = Math.Sqrt(((end.X - start.X) * (end.X - start.X)) + ((end.Y - start.Y) * (end.Y - start.Y)));

        // Un simple clic n'a pas de direction à redresser.
        if (length < MinHighlightLength)
        {
            return null;
        }

        var attributes = raw.DrawingAttributes.Clone();
        attributes.FitToCurve = false;
        attributes.IgnorePressure = true;

        if (isHorizontal && _findTextUnder(start.X, end.X, start.Y) is { } text)
        {
            var padding = Math.Max(MinHighlightPadding, text.Height * HighlightPaddingPerHeight);
            var centerY = text.Top + (text.Height / 2);

            // La pointe rectangulaire s'ajoute aux extrémités : une pointe étroite garde les bords exacts.
            attributes.Width = HighlightTipWidth;
            attributes.Height = text.Height + (padding * 2);

            return new Stroke(
                ToStylusPoints([new InkPoint(text.Left - padding, centerY), new InkPoint(text.Right + padding, centerY)]),
                attributes);
        }

        return new Stroke(ToStylusPoints([start, end]), attributes);
    }

    private void FinishStroke(InkCanvas ink, Stroke stroke)
    {
        if (_state.FadingEnabled)
        {
            // Une annotation évanescente n'entre pas dans l'historique : Annuler ne peut pas la ramener.
            ink.Strokes.Remove(stroke);
            _fadeLayer.Add(stroke, _options.Fade);
        }
        else
        {
            _history.Record(new AddStrokeAction(ink, stroke));
        }
    }

    private void OnStrokeErasing(object sender, InkCanvasStrokeErasingEventArgs e)
    {
        _history.Record(new RemoveStrokeAction((InkCanvas)sender, e.Stroke));
    }

    private void OnPointerDown(object sender, MouseButtonEventArgs e)
    {
        if (!_state.CanDraw)
        {
            return;
        }

        var position = e.GetPosition(this);

        if (_editor is { } editor)
        {
            if (editor.IsMouseOver)
            {
                return;
            }

            CommitText();
            e.Handled = _state.Tool == DrawingTool.Text;

            if (e.Handled)
            {
                return;
            }
        }

        if (_state.Tool.IsShape())
        {
            BeginShape(position);
            e.Handled = true;
        }
        else if (_state.Tool == DrawingTool.Text)
        {
            BeginText(position);
            e.Handled = true;
        }
        else if (_state.Tool == DrawingTool.Eraser)
        {
            _erasingText = true;
            EraseTextAt(position);
        }
    }

    private void OnPointerMove(object sender, MouseEventArgs e)
    {
        if (_shapePreview is not null)
        {
            UpdateShape(e.GetPosition(this));
        }
        else if (_erasingText)
        {
            EraseTextAt(e.GetPosition(this));
        }
    }

    private void OnPointerUp(object sender, MouseButtonEventArgs e)
    {
        _erasingText = false;

        if (_shapePreview is not { } preview)
        {
            return;
        }

        UpdateShape(e.GetPosition(this));
        var ink = _shapeInk!;
        _shapePreview = null;
        _shapeInk = null;
        ReleaseMouseCapture();

        var first = preview.StylusPoints[0];
        var isEmpty = preview.StylusPoints.All(point => point.X == first.X && point.Y == first.Y);

        if (isEmpty)
        {
            ink.Strokes.Remove(preview);
        }
        else
        {
            FinishStroke(ink, preview);
        }

        e.Handled = true;
    }

    private void OnPointerWheel(object sender, MouseWheelEventArgs e)
    {
        if (_options.WheelChangesSize && _state.CanDraw && _editor is null)
        {
            _pen.SetSize(_pen.Size + Math.Sign(e.Delta));
            e.Handled = true;
        }
    }

    private void BeginShape(Point position)
    {
        _shapeStart = new InkPoint(position.X, position.Y);
        _shapePreview = new Stroke(ToStylusPoints(ShapePoints(position)), CreateAttributes(forPenInput: false));

        // La forme reste sur le calque où elle a commencé, même si le calque actif change pendant le geste.
        _shapeInk = Ink;
        _shapeInk.Strokes.Add(_shapePreview);
        CaptureMouse();
    }

    private void UpdateShape(Point position)
    {
        _shapePreview!.StylusPoints = ToStylusPoints(ShapePoints(position));
    }

    private IReadOnlyList<InkPoint> ShapePoints(Point position)
    {
        return ShapeGeometry.Build(
            _state.Tool,
            _shapeStart,
            new InkPoint(position.X, position.Y),
            Win32.IsShiftDown(),
            _pen.Size);
    }

    private static StylusPointCollection ToStylusPoints(IReadOnlyList<InkPoint> points)
    {
        return new StylusPointCollection(points.Select(point => new StylusPoint(point.X, point.Y)));
    }

    private void BeginText(Point position)
    {
        var color = Color.FromRgb(_pen.Color.R, _pen.Color.G, _pen.Color.B);
        var editor = new TextBox
        {
            FontSize = Math.Max(MinFontSize, _pen.Size * FontSizePerPenSize),
            Foreground = new SolidColorBrush(color),
            CaretBrush = new SolidColorBrush(color),
            Background = Brushes.Transparent,
            BorderBrush = new SolidColorBrush(color),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(0),
            AcceptsReturn = true,
            MinWidth = 60,
        };

        AutomationPropertiesHelper.Name(editor, Loc.T("Texte de l'annotation"));
        Canvas.SetLeft(editor, position.X - TextEditorOffset);
        Canvas.SetTop(editor, position.Y - TextEditorOffset);
        editor.PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape)
            {
                CommitText();
                args.Handled = true;
            }
        };
        editor.LostKeyboardFocus += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, CommitIfStill(editor));

        _editor = editor;
        _editorLayer.Children.Add(editor);
        _textEditingChanged(true);
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => editor.Focus());
    }

    private Action CommitIfStill(TextBox editor)
    {
        return () =>
        {
            if (ReferenceEquals(_editor, editor))
            {
                CommitText();
            }
        };
    }

    private void EraseTextAt(Point position)
    {
        foreach (var text in _activeLayer.Texts.Children.OfType<TextBlock>().Except(_fadingTexts).ToList())
        {
            var bounds = new Rect(Canvas.GetLeft(text), Canvas.GetTop(text), text.ActualWidth, text.ActualHeight);

            if (bounds.Contains(position))
            {
                _activeLayer.Texts.Children.Remove(text);
                _history.Record(new RemoveTextAction(_activeLayer.Texts, text));
            }
        }
    }

    // Le calque actif peut changer pendant l'animation : le texte quitte le calque qui le porte.
    private void FadeOut(TextBlock text, Canvas owner)
    {
        _fadingTexts.Add(text);

        var animation = new DoubleAnimation(1, 0, new Duration(_options.Fade.Duration)) { BeginTime = _options.Fade.Delay };
        animation.Completed += (_, _) =>
        {
            _fadingTexts.Remove(text);
            owner.Children.Remove(text);
        };
        text.BeginAnimation(OpacityProperty, animation);
    }

    private DrawingAttributes CreateAttributes(bool forPenInput)
    {
        var highlighter = forPenInput && _state.Tool == DrawingTool.Highlighter;
        var thickness = highlighter ? _pen.Size * ToolCursors.HighlighterScale : _pen.Size;

        // Les formes gardent des angles nets ; seul le tracé libre est lissé et sensible à la pression.
        return new DrawingAttributes
        {
            Color = Color.FromRgb(_pen.Color.R, _pen.Color.G, _pen.Color.B),
            Width = thickness,
            Height = thickness,
            IsHighlighter = highlighter,
            StylusTip = highlighter ? StylusTip.Rectangle : StylusTip.Ellipse,
            IgnorePressure = !forPenInput || highlighter || !_options.PressureEnabled,
            FitToCurve = forPenInput && !highlighter,
        };
    }
}

internal static class AutomationPropertiesHelper
{
    public static void Name(DependencyObject element, string name)
    {
        System.Windows.Automation.AutomationProperties.SetName(element, name);
    }
}
