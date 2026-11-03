using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Media;

namespace LegendaryPen.App;

/// <summary>Encre et textes d'un calque : les textes restent sous l'encre.</summary>
internal sealed class AnnotationLayer : Grid
{
    public AnnotationLayer()
    {
        Ink = new InkCanvas
        {
            Background = Brushes.Transparent,
            EditingMode = InkCanvasEditingMode.None,
            EditingModeInverted = InkCanvasEditingMode.None,
            EraserShape = new EllipseStylusShape(ToolCursors.EraserDiameter, ToolCursors.EraserDiameter),
            UseCustomCursor = true,
        };

        Children.Add(Texts);
        Children.Add(Ink);
    }

    public InkCanvas Ink { get; }

    public Canvas Texts { get; } = new() { IsHitTestVisible = false };
}
