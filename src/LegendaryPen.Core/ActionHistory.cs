namespace LegendaryPen.Core;

public interface IUndoableAction
{
    void Undo();

    void Redo();
}

/// <summary>Historique unique, partagé par tous les écrans.</summary>
public sealed class ActionHistory
{
    private readonly Stack<IUndoableAction> _undo = new();
    private readonly Stack<IUndoableAction> _redo = new();

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public event Action? Changed;

    /// <summary>Enregistre une action déjà appliquée. Toute nouvelle action invalide le rétablissement.</summary>
    public void Record(IUndoableAction action)
    {
        _undo.Push(action);
        _redo.Clear();
        Changed?.Invoke();
    }

    public void Undo()
    {
        if (_undo.TryPop(out var action))
        {
            action.Undo();
            _redo.Push(action);
            Changed?.Invoke();
        }
    }

    public void Redo()
    {
        if (_redo.TryPop(out var action))
        {
            action.Redo();
            _undo.Push(action);
            Changed?.Invoke();
        }
    }
}
