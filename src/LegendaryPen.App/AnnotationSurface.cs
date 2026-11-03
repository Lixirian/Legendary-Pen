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
    private readonly Canvas _textLayer = new() { IsHitTestVisible = false };
    private readonly Canvas _editorLayer = new();
    private readonly FadeLayer _fadeLayer = new();
    private readonly HashSet<TextBlock> _fadingTexts = [];

    private TextBox? _editor;
    private Stroke? _shapePreview;
    private InkPoint _shapeStart;
    private bool _erasingText;

    public AnnotationSurface(
        AppState state,
        PenState pen,
        ActionHistory history,
        LiveOptions options,
        Action<bool> textEditingChanged,
        Func<double, double, double, Rect?> findTextUnder)
    {
        _findTextUnder = findTextUnder;
        _state = state;
        _pen = pen;
        _history = history;
        _options = options;
        _textEditingChanged = textEditingChanged;

        Background = Brushes.Transparent;

        Ink = new InkCanvas
        {
            Background = Brushes.Transparent,
            EditingMode = InkCanvasEditingMode.None,
            EditingModeInverted = InkCanvasEditingMode.None,
            EraserShape = new EllipseStylusShape(ToolCursors.EraserDiameter, ToolCursors.EraserDiameter),
            UseCustomCursor = true,
        };
        Ink.StrokeCollected += OnStrokeCollected;
        Ink.StrokeErasing += OnStrokeErasing;

        Children.Add(_textLayer);
        Children.Add(Ink);
        Children.Add(_fadeLayer);
        Children.Add(_editorLayer);

        PreviewMouseLeftButtonDown += OnPointerDown;
        PreviewMouseMove += OnPointerMove;
        PreviewMouseLeftButtonUp += OnPointerUp;
        PreviewMouseWheel += OnPointerWheel;
    }

    public InkCanvas Ink { get; }

    /// <summary>Reflète l'outil, la couleur et la taille sur cette surface.</summary>
    public void ApplyTool(double scale)
    {
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

    public SurfaceSnapshot? TakeSnapshotAndClearFading()
    {
        CommitText();
        _fadeLayer.Clear();

        foreach (var text in _fadingTexts)
        {
            _textLayer.Children.Remove(text);
        }

        _fadingTexts.Clear();

        var texts = _textLayer.Children.OfType<TextBlock>().ToList();

        return Ink.Strokes.Count == 0 && texts.Count == 0
            ? null
            : new SurfaceSnapshot(Ink, new StrokeCollection(Ink.Strokes), _textLayer, texts);
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
        _textLayer.Children.Add(text);

        if (_state.FadingEnabled)
        {
            FadeOut(text);
        }
        else
        {
            _history.Record(new AddTextAction(_textLayer, text));
        }
    }

    private const double TextEditorOffset = 3;

    private void OnStrokeCollected(object sender, InkCanvasStrokeCollectedEventArgs e)
    {
        if (e.Stroke.DrawingAttributes.IsHighlighter && _options.HighlightSnapEnabled && CleanHighlight(e.Stroke) is { } clean)
        {
            Ink.Strokes.Remove(e.Stroke);
            Ink.Strokes.Add(clean);
            FinishStroke(clean);
        }
        else
        {
            FinishStroke(e.Stroke);
        }
    }

    /// <summary>Remplace un surlignage à main levée par un trait droit, calé sur le texte s'il y en a.</summary>
    private Stroke? CleanHighlight(Stroke raw)
    {
        var points = raw.StylusPoints.Select(point => new InkPoint(point.X, point.Y)).ToList();
        var (start, end, isHorizontal) = HighlightLine.Straighten(points);
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

    private void FinishStroke(Stroke stroke)
    {
        if (_state.FadingEnabled)
        {
            // Une annotation évanescente n'entre pas dans l'historique : Annuler ne peut pas la ramener.
            Ink.Strokes.Remove(stroke);
            _fadeLayer.Add(stroke, _options.Fade);
        }
        else
        {
            _history.Record(new AddStrokeAction(Ink, stroke));
        }
    }

    private void OnStrokeErasing(object sender, InkCanvasStrokeErasingEventArgs e)
    {
        _history.Record(new RemoveStrokeAction(Ink, e.Stroke));
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
        _shapePreview = null;
        ReleaseMouseCapture();

        var first = preview.StylusPoints[0];
        var isEmpty = preview.StylusPoints.All(point => point.X == first.X && point.Y == first.Y);

        if (isEmpty)
        {
            Ink.Strokes.Remove(preview);
        }
        else
        {
            FinishStroke(preview);
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
        Ink.Strokes.Add(_shapePreview);
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

        AutomationPropertiesHelper.Name(editor, "Texte de l'annotation");
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
        foreach (var text in _textLayer.Children.OfType<TextBlock>().Except(_fadingTexts).ToList())
        {
            var bounds = new Rect(Canvas.GetLeft(text), Canvas.GetTop(text), text.ActualWidth, text.ActualHeight);

            if (bounds.Contains(position))
            {
                _textLayer.Children.Remove(text);
                _history.Record(new RemoveTextAction(_textLayer, text));
            }
        }
    }

    private void FadeOut(TextBlock text)
    {
        _fadingTexts.Add(text);

        var animation = new DoubleAnimation(1, 0, new Duration(_options.Fade.Duration)) { BeginTime = _options.Fade.Delay };
        animation.Completed += (_, _) =>
        {
            _fadingTexts.Remove(text);
            _textLayer.Children.Remove(text);
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
