namespace LegendaryPen.Core;

/// <summary>Résultat d'une fusion : la pile à adopter, et le calque de la pile qui reçoit chaque calque enregistré.</summary>
public sealed record LayerMerge(LayerStackState State, IReadOnlyDictionary<Guid, Guid> Targets);

/// <summary>
/// Remise en place des calques enregistrés. La pile d'un fond est commune à tous les écrans, mais chaque écran a son
/// fichier : le premier fichier relu rétablit une pile encore vierge, tous les autres s'y ajoutent sans rien lui retirer.
/// </summary>
public static class LayerRestoration
{
    /// <summary>
    /// Vrai si la pile est exactement celle d'origine : un seul calque, le premier, sans autre numéro déjà pris. Ses
    /// annotations s'écrivent alors à l'ancien format, le seul qu'une version antérieure à 0.9 sache relire : elle ne verrait
    /// rien dans un fichier à calques, et l'effacerait à sa sortie.
    /// </summary>
    public static bool UsesLegacyFormat(LayerStackState state) =>
        state.NextNumber == 2 && state.Layers is [{ Number: 1, IsVisible: true }];

    /// <summary>L'état de pile décrit par un fichier. Peut être refusé par <see cref="LayerStack.Restore"/> s'il est incohérent.</summary>
    public static LayerStackState StateOf(IReadOnlyList<SavedLayer> layers, Guid? activeLayerId, int? nextNumber)
    {
        var infos = layers.Select(layer => new LayerInfo(layer.Id, layer.Number, layer.IsVisible)).ToList();
        var active = activeLayerId ?? infos.FirstOrDefault(info => info.IsVisible)?.Id ?? Guid.Empty;
        var next = nextNumber ?? (infos.Count == 0 ? 1 : infos.Max(info => info.Number) + 1);

        return new LayerStackState(infos, active, next);
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
                var number = layers.Any(info => info.Number == layer.Number) ? nextNumber : layer.Number;
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
    /// contenu. Seule une pile encore d'origine peut être remplacée ; sinon le fichier fusionne. Si l'état enregistré est
    /// incohérent, la pile reste telle quelle et tout va dans son calque actif. Un fichier ancien ne touche pas à la pile.
    /// </summary>
    public static IReadOnlyDictionary<Guid, Guid> RestoreInto(LayerStack stack, SavedSurface saved, bool stackAlreadyRestored)
    {
        if (saved.Layers is not { } layers)
        {
            return new Dictionary<Guid, Guid>();
        }

        try
        {
            if (!stackAlreadyRestored && UsesLegacyFormat(stack.Capture()))
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
