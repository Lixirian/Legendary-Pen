namespace LegendaryPen.Core;

/// <summary>Un calque d'un fond : l'identifiant reste stable lors des déplacements.</summary>
public sealed record LayerInfo(Guid Id, string Name, bool IsVisible);

/// <summary>
/// Pile de calques d'un fond. L'indice 0 est le calque du bas.
/// Invariants : au moins un calque, au plus <see cref="MaxLayers"/>, un calque actif toujours visible.
/// </summary>
public sealed class LayerStack
{
    public const int MaxLayers = 8;

    private readonly List<LayerInfo> _layers = [];
    private Guid _activeId;
    // Compteur de noms propre à la pile, jamais décrémenté : un nom n'est pas réutilisé.
    private int _nameCounter;

    public LayerStack()
    {
        var first = NewLayer();
        _layers.Add(first);
        _activeId = first.Id;
    }

    public event Action? Changed;

    public IReadOnlyList<LayerInfo> Layers => _layers;

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

    /// <summary>Remet un calque retiré (annulation) avec son identifiant, son nom, sa visibilité et sa position.</summary>
    public void Reinsert(LayerInfo layer, int index)
    {
        if (!CanAdd)
        {
            throw new InvalidOperationException("The stack already holds the maximum number of layers.");
        }

        if (_layers.Any(l => l.Id == layer.Id))
        {
            throw new InvalidOperationException("This layer is already in the stack.");
        }

        _layers.Insert(Math.Clamp(index, 0, _layers.Count), layer);
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

    private LayerInfo NewLayer() => new(Guid.NewGuid(), Loc.T("Calque {0}", ++_nameCounter), true);

    private int IndexOf(Guid id)
    {
        var index = _layers.FindIndex(l => l.Id == id);
        return index >= 0 ? index : throw new ArgumentException("Unknown layer.", nameof(id));
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
