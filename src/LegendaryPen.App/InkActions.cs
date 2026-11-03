using System.Windows.Controls;
using System.Windows.Ink;
using LegendaryPen.Core;

namespace LegendaryPen.App;

internal sealed class AddStrokeAction(InkCanvas canvas, Stroke stroke) : IUndoableAction
{
    public void Undo() => canvas.Strokes.Remove(stroke);

    public void Redo() => canvas.Strokes.Add(stroke);
}

internal sealed class ClearAllAction(IReadOnlyList<(InkCanvas Canvas, StrokeCollection Strokes)> cleared) : IUndoableAction
{
    public void Undo()
    {
        foreach (var (canvas, strokes) in cleared)
        {
            canvas.Strokes.Add(strokes);
        }
    }

    public void Redo()
    {
        foreach (var (canvas, strokes) in cleared)
        {
            canvas.Strokes.Remove(strokes);
        }
    }
}
