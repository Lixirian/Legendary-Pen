using System.Windows.Controls;
using System.Windows.Ink;
using LegendaryPen.Core;

namespace LegendaryPen.App;

internal sealed class AddStrokeAction(InkCanvas canvas, Stroke stroke) : IUndoableAction
{
    public void Undo() => canvas.Strokes.Remove(stroke);

    public void Redo() => canvas.Strokes.Add(stroke);
}

internal sealed class RemoveStrokeAction(InkCanvas canvas, Stroke stroke) : IUndoableAction
{
    public void Undo() => canvas.Strokes.Add(stroke);

    public void Redo() => canvas.Strokes.Remove(stroke);
}

internal sealed class AddTextAction(Canvas layer, TextBlock text) : IUndoableAction
{
    public void Undo() => layer.Children.Remove(text);

    public void Redo() => layer.Children.Add(text);
}

internal sealed class RemoveTextAction(Canvas layer, TextBlock text) : IUndoableAction
{
    public void Undo() => layer.Children.Add(text);

    public void Redo() => layer.Children.Remove(text);
}

/// <summary>Efface les annotations d'un fond sur tous les écrans, en une seule action annulable.</summary>
internal sealed class ClearAllAction(IReadOnlyList<SurfaceSnapshot> cleared) : IUndoableAction
{
    public void Undo()
    {
        foreach (var snapshot in cleared)
        {
            snapshot.Restore();
        }
    }

    public void Redo()
    {
        foreach (var snapshot in cleared)
        {
            snapshot.Remove();
        }
    }
}

internal sealed class SurfaceSnapshot(InkCanvas canvas, StrokeCollection strokes, Canvas layer, IReadOnlyList<TextBlock> texts)
{
    public void Remove()
    {
        canvas.Strokes.Remove(strokes);

        foreach (var text in texts)
        {
            layer.Children.Remove(text);
        }
    }

    public void Restore()
    {
        canvas.Strokes.Add(strokes);

        foreach (var text in texts)
        {
            layer.Children.Add(text);
        }
    }
}
