namespace LegendaryPen.Core;

/// <summary>Résultat d'une fusion : la pile à adopter, et le calque de la pile qui reçoit chaque calque enregistré.</summary>
public sealed record LayerMerge(LayerStackState State, IReadOnlyDictionary<Guid, Guid> Targets);

/// <summary>
/// Remise en place des calques enregistrés. La pile d'un fond est commune à tous les écrans, mais chaque écran a son
/// fichier : le premier fichier relu rétablit la pile, les suivants rattachent leur contenu aux calques par identifiant.
/// </summary>
public static class LayerRestoration
{
    /// <summary>L'état de pile décrit par un fichier. Peut être refusé par <see cref="LayerStack.Restore"/> s'il est incohérent.</summary>
    public static LayerStackState StateOf(IReadOnlyList<SavedLayer> layers, Guid? activeLayerId, int? nextNumber)
    {
        var infos = layers.Select(layer => new LayerInfo(layer.Id, layer.Number, layer.IsVisible)).ToList();
        var active = activeLayerId ?? infos.FirstOrDefault(info => info.IsVisible)?.Id ?? Guid.Empty;
        var next = nextNumber ?? (infos.Count == 0 ? 1 : infos.Max(info => info.Number) + 1);

        return new LayerStackState(infos, active, next);
    }

    /// <summary>
    /// Rattache des calques enregistrés à une pile existante. Un calque connu garde son identifiant ; un calque inconnu
    /// est ajouté au-dessus, avec son numéro, tant que la limite de <see cref="LayerStack.MaxLayers"/> le permet ;
    /// au-delà, son contenu rejoint le calque du dessus, pour ne rien perdre.
    /// </summary>
    public static LayerMerge MergeLayers(LayerStackState current, IReadOnlyList<SavedLayer> saved)
    {
        var layers = current.Layers.ToList();
        var nextNumber = current.NextNumber;
        var targets = new Dictionary<Guid, Guid>();

        foreach (var layer in saved)
        {
            if (layers.Any(info => info.Id == layer.Id))
            {
                targets[layer.Id] = layer.Id;
            }
            else if (layers.Count < LayerStack.MaxLayers)
            {
                layers.Add(new LayerInfo(layer.Id, layer.Number, layer.IsVisible));
                nextNumber = Math.Max(nextNumber, layer.Number + 1);
                targets[layer.Id] = layer.Id;
            }
            else
            {
                targets[layer.Id] = layers[^1].Id;
            }
        }

        var state = layers.Count == current.Layers.Count ? current : new LayerStackState(layers, current.ActiveId, nextNumber);

        return new LayerMerge(state, targets);
    }

    /// <summary>
    /// Applique les calques d'un fichier à la pile et renvoie, pour chaque calque enregistré, le calque qui reçoit son
    /// contenu. Si l'état enregistré est incohérent, la pile reste telle quelle et tout va dans son calque actif.
    /// </summary>
    public static IReadOnlyDictionary<Guid, Guid> RestoreInto(LayerStack stack, SavedSurface saved, bool stackAlreadyRestored)
    {
        var layers = saved.Layers ?? [];

        try
        {
            if (!stackAlreadyRestored)
            {
                stack.Restore(StateOf(layers, saved.ActiveLayerId, saved.NextNumber));

                return layers.ToDictionary(layer => layer.Id, layer => layer.Id);
            }

            var merge = MergeLayers(stack.Capture(), layers);

            if (merge.State.Layers.Count != stack.Layers.Count)
            {
                stack.Restore(merge.State);
            }

            return merge.Targets;
        }
        catch (ArgumentException exception)
        {
            System.Diagnostics.Debug.WriteLine($"Saved layers are inconsistent, keeping the current stack: {exception.Message}");
            var active = stack.Active.Id;

            return layers.GroupBy(layer => layer.Id).ToDictionary(group => group.Key, _ => active);
        }
    }
}
