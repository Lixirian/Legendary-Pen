namespace LegendaryPen.Core;

/// <summary>
/// Un calque d'un fond : l'identifiant reste stable lors des déplacements.
/// Le numéro n'est jamais réutilisé ; le nom suit la langue active.
/// </summary>
public sealed record LayerInfo(Guid Id, int Number, bool IsVisible)
{
    public string Name => Loc.T("Calque {0}", Number);
}

/// <summary>État complet d'une pile, pour l'annulation et la persistance.</summary>
public sealed record LayerStackState(IReadOnlyList<LayerInfo> Layers, Guid ActiveId, int NextNumber);

/// <summary>
/// Pile de calques d'un fond. L'indice 0 est le calque du bas.
/// Invariants : au moins un calque, au plus <see cref="MaxLayers"/>, un calque actif toujours visible.
/// </summary>
public sealed class LayerStack
{
    public const int MaxLayers = 8;

    private readonly List<LayerInfo> _layers = [];
    private Guid _activeId;
    // Prochain numéro de calque, propre à la pile et jamais décrémenté : un numéro n'est pas réutilisé.
    private int _nextNumber = 1;

    public LayerStack()
    {
        var first = NewLayer();
        _layers.Add(first);
        _activeId = first.Id;
    }

    public event Action? Changed;

    public IReadOnlyList<LayerInfo> Layers => _layers.AsReadOnly();

    public LayerInfo Active => _layers[IndexOf(_activeId)];

    public bool CanAdd => _layers.Count < MaxLayers;

    public bool CanRemove => _layers.Count > 1;

    /// <summary>Insère un calque au-dessus du calque actif et le sélectionne.</summary>
    public LayerInfo Add()
    {
        if (!CanAdd)
        {
            throw new InvalidOperationException("The stack already holds the maximum number of layers.");
        }

        var layer = NewLayer();
        _layers.Insert(IndexOf(_activeId) + 1, layer);
        _activeId = layer.Id;
        Changed?.Invoke();
        return layer;
    }

    /// <summary>Retire un calque. S'il était actif, on sélectionne celui du dessous, sinon celui du dessus.</summary>
    public void Remove(Guid id)
    {
        if (!CanRemove)
        {
            throw new InvalidOperationException("The last layer cannot be removed.");
        }

        var index = IndexOf(id);
        var wasActive = id == _activeId;
        _layers.RemoveAt(index);

        if (wasActive)
        {
            var next = _layers[Math.Max(index - 1, 0)];
            _activeId = next.Id;
            if (!next.IsVisible)
            {
                _layers[IndexOf(next.Id)] = next with { IsVisible = true };
            }
        }

        Changed?.Invoke();
    }

    /// <summary>Copie de l'état complet, sûre à conserver.</summary>
    public LayerStackState Capture() => new(_layers.ToList(), _activeId, _nextNumber);

    /// <summary>Remplace l'état complet (annulation, chargement) ; <see cref="Changed"/> est levé une seule fois.</summary>
    public void Restore(LayerStackState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var layers = state.Layers;
        if (layers is null || layers.Any(l => l is null))
        {
            throw new ArgumentException("A stack cannot hold a null layer.", nameof(state));
        }

        if (layers.Count is < 1 or > MaxLayers)
        {
            throw new ArgumentException("A stack holds between 1 and 8 layers.", nameof(state));
        }

        if (layers.Select(l => l.Id).Distinct().Count() != layers.Count)
        {
            throw new ArgumentException("Layer ids must be unique.", nameof(state));
        }

        if (layers.Select(l => l.Number).Distinct().Count() != layers.Count)
        {
            throw new ArgumentException("Layer numbers must be unique.", nameof(state));
        }

        var active = layers.FirstOrDefault(l => l.Id == state.ActiveId)
            ?? throw new ArgumentException("The active layer is not in the stack.", nameof(state));
        if (!active.IsVisible)
        {
            throw new ArgumentException("The active layer must be visible.", nameof(state));
        }

        if (layers.Any(l => l.Number >= state.NextNumber))
        {
            throw new ArgumentException("The next number must follow every layer number.", nameof(state));
        }

        _layers.Clear();
        _layers.AddRange(layers);
        _activeId = state.ActiveId;
        _nextNumber = state.NextNumber;
        Changed?.Invoke();
    }

    /// <summary>Sélectionne un calque ; s'il était masqué, il est affiché.</summary>
    public void Select(Guid id)
    {
        var index = IndexOf(id);
        var layer = _layers[index];
        if (id == _activeId && layer.IsVisible)
        {
            return;
        }

        _activeId = id;
        _layers[index] = layer with { IsVisible = true };
        Changed?.Invoke();
    }

    /// <summary>
    /// Change la visibilité. Masquer le dernier calque visible est sans effet ; masquer le calque actif
    /// sélectionne le calque visible le plus proche, en cherchant d'abord vers le bas.
    /// </summary>
    public void SetVisible(Guid id, bool visible)
    {
        var index = IndexOf(id);
        var layer = _layers[index];
        if (layer.IsVisible == visible)
        {
            return;
        }

        if (!visible)
        {
            if (_layers.Count(l => l.IsVisible) <= 1)
            {
                return;
            }

            if (id == _activeId)
            {
                _activeId = NearestVisible(index).Id;
            }
        }

        _layers[index] = layer with { IsVisible = visible };
        Changed?.Invoke();
    }

    public void MoveUp(Guid id) => Move(id, 1);

    public void MoveDown(Guid id) => Move(id, -1);

    private void Move(Guid id, int delta)
    {
        var index = IndexOf(id);
        var target = index + delta;
        if (target < 0 || target >= _layers.Count)
        {
            return;
        }

        (_layers[index], _layers[target]) = (_layers[target], _layers[index]);
        Changed?.Invoke();
    }

    private LayerInfo NearestVisible(int from)
    {
        for (var i = from - 1; i >= 0; i--)
        {
            if (_layers[i].IsVisible)
            {
                return _layers[i];
            }
        }

        for (var i = from + 1; i < _layers.Count; i++)
        {
            if (_layers[i].IsVisible)
            {
                return _layers[i];
            }
        }

        throw new InvalidOperationException("No visible layer.");
    }

    private LayerInfo NewLayer() => new(Guid.NewGuid(), _nextNumber++, true);

    private int IndexOf(Guid id)
    {
        var index = _layers.FindIndex(l => l.Id == id);
        return index >= 0 ? index : throw new ArgumentException("Unknown layer.", nameof(id));
    }
}

/// <summary>
/// Un changement de structure de la pile : ajout, suppression ou déplacement. L'état de la pile avant et après suffit à
/// défaire ou refaire : le contenu d'un calque reste conservé par les surfaces, donc il revient avec lui. La visibilité et
/// la sélection restent hors de l'historique ; défaire ou refaire peut les ramener à leur état d'alors.
/// </summary>
public sealed class LayerStackAction(LayerStack stack, LayerStackState before, LayerStackState after) : IUndoableAction
{
    public void Undo() => stack.Restore(before);

    public void Redo() => stack.Restore(after);

    /// <summary>Applique un changement de structure et l'inscrit dans l'historique, sauf s'il n'a rien changé.</summary>
    public static void Apply(LayerStack stack, ActionHistory history, Action change)
    {
        var before = stack.Capture();
        change();
        var after = stack.Capture();

        if (!before.Layers.SequenceEqual(after.Layers) || before.ActiveId != after.ActiveId || before.NextNumber != after.NextNumber)
        {
            history.Record(new LayerStackAction(stack, before, after));
        }
    }
}

/// <summary>Une pile de calques par fond, sur le modèle de <see cref="SurfaceHistories"/>.</summary>
public sealed class LayerStacks
{
    private readonly Dictionary<BoardBackground, LayerStack> _stacks = [];

    public LayerStacks()
    {
        foreach (var background in Enum.GetValues<BoardBackground>())
        {
            _stacks[background] = new LayerStack();
        }
    }

    public LayerStack For(BoardBackground background) => _stacks[background];
}
