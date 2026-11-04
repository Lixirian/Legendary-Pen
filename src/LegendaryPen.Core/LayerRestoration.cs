namespace LegendaryPen.Core;

/// <summary>Résultat d'une relecture : la pile à adopter, et le calque de la pile qui reçoit chaque calque enregistré.</summary>
public sealed record LayerMerge(LayerStackState State, IReadOnlyDictionary<Guid, Guid> Targets);

/// <summary>
/// Remise en place des calques enregistrés. La pile d'un fond est commune à tous les écrans, mais chaque écran a son
/// fichier : le premier fichier relu rétablit une pile encore vierge, tous les autres s'y ajoutent sans rien lui retirer.
/// </summary>
public static class LayerRestoration
{
    // Au-delà, un fichier retouché ferait déborder la numérotation des calques suivants.
    private const int MaxNumber = 1_000_000;

    /// <summary>
    /// Vrai si la pile est exactement celle d'origine : un seul calque, le premier, sans autre numéro déjà pris. Ses
    /// annotations s'écrivent alors à l'ancien format, le seul qu'une version antérieure à 0.9 sache relire : elle ne verrait
    /// rien dans un fichier à calques, et l'effacerait à sa sortie.
    /// </summary>
    public static bool UsesLegacyFormat(LayerStackState state) =>
        state.NextNumber == 2 && state.Layers is [{ Number: 1, IsVisible: true }];

    /// <summary>
    /// La pile décrite par un fichier, toujours acceptée par <see cref="LayerStack.Restore"/>, et le calque qui reçoit chaque
    /// calque enregistré. Un fichier retouché à la main est réparé au plus près : les calques gardent leur place et leur
    /// visibilité. Un identifiant répété rejoint son premier calque ; au-delà de <see cref="LayerStack.MaxLayers"/>, un calque
    /// rejoint le dernier gardé de même visibilité, pour qu'un contenu masqué le reste. Un numéro inutilisable en reçoit un
    /// nouveau. Si aucun calque n'est visible, seul le calque actif se montre. Nul si le fichier ne décrit aucun calque.
    /// </summary>
    public static LayerMerge? StateOf(IReadOnlyList<SavedLayer> layers, Guid? activeLayerId, int? nextNumber)
    {
        var kept = new List<LayerInfo>();
        var targets = new Dictionary<Guid, Guid>();

        foreach (var layer in layers)
        {
            if (targets.ContainsKey(layer.Id))
            {
                continue;
            }

            if (kept.Count < LayerStack.MaxLayers)
            {
                kept.Add(new LayerInfo(layer.Id, layer.Number, layer.IsVisible));
                targets[layer.Id] = layer.Id;
            }
            else
            {
                targets[layer.Id] = (kept.LastOrDefault(info => info.IsVisible == layer.IsVisible) ?? kept[^1]).Id;
            }
        }

        if (kept.Count == 0)
        {
            return null;
        }

        // Le plus grand numéro peut valoir int.MaxValue dans un fichier retouché : il est alors remplacé, sans déborder.
        var usable = kept.Where(info => info.Number is >= 1 and < MaxNumber).Select(info => info.Number).DefaultIfEmpty(0).Max();
        var next = Math.Max(nextNumber is >= 1 and <= MaxNumber ? nextNumber.Value : 1, usable + 1);
        var numbers = new HashSet<int>();

        for (var index = 0; index < kept.Count; index++)
        {
            if (kept[index].Number is < 1 or >= MaxNumber || !numbers.Add(kept[index].Number))
            {
                kept[index] = kept[index] with { Number = next };
                numbers.Add(next++);
            }
        }

        // Le calque annoncé s'il est utilisable, sinon le calque visible le plus haut.
        var active = kept.FirstOrDefault(info => info.Id == activeLayerId && info.IsVisible)
            ?? kept.LastOrDefault(info => info.IsVisible);

        if (active is null)
        {
            var chosen = kept.FindIndex(info => info.Id == activeLayerId);
            chosen = chosen >= 0 ? chosen : kept.Count - 1;
            kept[chosen] = kept[chosen] with { IsVisible = true };
            active = kept[chosen];
        }

        return new LayerMerge(new LayerStackState(kept, active.Id, next), targets);
    }

    /// <summary>
    /// Rattache des calques enregistrés à une pile existante, sans jamais lui retirer un calque. Un calque connu garde son
    /// identifiant ; un calque inconnu est ajouté au-dessus tant que la limite de <see cref="LayerStack.MaxLayers"/> le
    /// permet, avec son numéro, ou le prochain numéro de la pile si le sien est déjà pris ; au-delà de la limite, son contenu
    /// rejoint le calque du dessus, pour ne rien perdre.
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
                var number = layer.Number is < 1 or >= MaxNumber || layers.Any(info => info.Number == layer.Number) ? nextNumber : layer.Number;
                layers.Add(new LayerInfo(layer.Id, number, layer.IsVisible));
                nextNumber = Math.Max(nextNumber, number + 1);
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
    /// contenu. Seule une pile encore d'origine peut être remplacée ; sinon le fichier fusionne. Un état abîmé est d'abord
    /// réparé par <see cref="StateOf"/>. Un fichier ancien ne touche pas à la pile.
    /// </summary>
    public static IReadOnlyDictionary<Guid, Guid> RestoreInto(LayerStack stack, SavedSurface saved, bool stackAlreadyRestored)
    {
        if (saved.Layers is not { } allLayers)
        {
            return new Dictionary<Guid, Guid>();
        }

        // Un calque nul ne vient que d'un fichier retouché à la main : le reste se relit.
        var layers = allLayers.Where(layer => layer is not null).ToList();

        if (!stackAlreadyRestored && UsesLegacyFormat(stack.Capture()))
        {
            if (StateOf(layers, saved.ActiveLayerId, saved.NextNumber) is not { } described)
            {
                return new Dictionary<Guid, Guid>();
            }

            stack.Restore(described.State);

            return described.Targets;
        }

        var merge = MergeLayers(stack.Capture(), layers);

        if (merge.State.Layers.Count != stack.Layers.Count)
        {
            stack.Restore(merge.State);
        }

        return merge.Targets;
    }
}

/// <summary>
/// Décide, fond par fond, si le prochain fichier relu peut remplacer la pile ou doit fusionner avec elle. Une pile n'est
/// remplacée que si elle est vierge : premier passage sur les écrans, et aucun fichier déjà relu pour ce fond, à quelque
/// format que ce soit, puisqu'un fichier relu a déjà placé son contenu sur les calques de la pile.
/// </summary>
public sealed class LayerRestorationLedger
{
    private readonly HashSet<BoardBackground> _restored = [];

    public bool IsRestored(BoardBackground background) => _restored.Contains(background);

    /// <summary>À la fin du premier passage : le fichier d'un écran branché ensuite trouve une pile vivante, et fusionne.</summary>
    public void CompleteFirstPass() => _restored.UnionWith(Enum.GetValues<BoardBackground>());

    /// <summary>
    /// Applique un fichier relu pour ce fond et renvoie les calques qui reçoivent son contenu. Si la pile y gagne des
    /// calques, l'historique du fond est vidé : ses états de pile d'avant ne la décrivent plus.
    /// </summary>
    public IReadOnlyDictionary<Guid, Guid> Place(BoardBackground background, LayerStack stack, ActionHistory history, SavedSurface saved)
    {
        var known = stack.Layers.Select(layer => layer.Id).ToHashSet();
        var targets = LayerRestoration.RestoreInto(stack, saved, IsRestored(background));
        _restored.Add(background);

        if (stack.Layers.Any(layer => !known.Contains(layer.Id)))
        {
            history.Clear();
        }

        return targets;
    }
}
