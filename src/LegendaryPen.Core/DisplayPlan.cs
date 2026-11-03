namespace LegendaryPen.Core;

/// <summary>
/// Ce qu'il faut faire des overlays quand la liste des écrans change.
/// L'overlay d'un écran débranché est masqué et non détruit : ses annotations reviennent avec l'écran.
/// </summary>
public sealed record DisplayPlan(
    IReadOnlyList<string> ToCreate,
    IReadOnlyList<string> ToKeep,
    IReadOnlyList<string> ToHide)
{
    public static DisplayPlan Compute(IReadOnlyCollection<string> existing, IReadOnlyCollection<string> connected)
    {
        return new DisplayPlan(
            connected.Except(existing).ToList(),
            connected.Intersect(existing).ToList(),
            existing.Except(connected).ToList());
    }
}
