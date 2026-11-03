namespace LegendaryPen.Core;

/// <summary>État des trois effets du mode présentation, indépendants les uns des autres (PR-1).</summary>
public sealed class PresentationState
{
    public bool SpotlightOn { get; private set; }

    public bool MagnifierOn { get; private set; }

    public bool ClickEffectsOn { get; private set; }

    public bool AnyActive => SpotlightOn || MagnifierOn || ClickEffectsOn;

    public event Action? Changed;

    /// <summary>Les fenêtres et le suivi du pointeur ne tournent que si un effet est actif et que rien ne les suspend.</summary>
    public static bool ShouldRun(bool anyActive, bool suspended) => anyActive && !suspended;

    public void ToggleSpotlight()
    {
        SpotlightOn = !SpotlightOn;
        Changed?.Invoke();
    }

    public void ToggleMagnifier()
    {
        MagnifierOn = !MagnifierOn;
        Changed?.Invoke();
    }

    public void ToggleClickEffects()
    {
        ClickEffectsOn = !ClickEffectsOn;
        Changed?.Invoke();
    }
}

/// <summary>Anneau d'un effet de clic à un instant donné.</summary>
public readonly record struct ClickRipple(double Radius, double Opacity);

/// <summary>Géométrie des effets, en pixels physiques (PR-3, PR-4).</summary>
public static class PresentationGeometry
{
    public const double RippleDuration = 400;

    public const double RippleMaxRadius = 48;

    /// <summary>Boîte de la loupe : centrée sur le pointeur, ramenée dans l'écran.</summary>
    public static PixelRect MagnifierWindow(PixelPoint pointer, int diameter, PixelRect monitor)
    {
        return CenteredInside(pointer, diameter, monitor);
    }

    /// <summary>Zone d'écran grossie : centrée sur le pointeur, ramenée dans l'écran.</summary>
    public static PixelRect MagnifierSource(PixelPoint pointer, int diameter, double zoom, PixelRect monitor)
    {
        return CenteredInside(pointer, (int)Math.Round(diameter / zoom), monitor);
    }

    /// <summary>Délai à laisser après un rafraîchissement de la loupe : le coût mesuré reste au plus un tiers du temps.</summary>
    public static TimeSpan MagnifierGap(TimeSpan minimum, TimeSpan lastCost)
    {
        return TimeSpan.FromTicks(Math.Max(minimum.Ticks, lastCost.Ticks * 2));
    }

    /// <summary>L'anneau grandit et s'efface de façon linéaire, puis reste invisible.</summary>
    public static ClickRipple RippleAt(double elapsedMilliseconds)
    {
        var progress = Math.Clamp(elapsedMilliseconds / RippleDuration, 0, 1);
        return new ClickRipple(RippleMaxRadius * progress, 1 - progress);
    }

    private static PixelRect CenteredInside(PixelPoint center, int size, PixelRect monitor)
    {
        var left = ClampToRange(center.X - (size / 2), monitor.Left, monitor.Right - size);
        var top = ClampToRange(center.Y - (size / 2), monitor.Top, monitor.Bottom - size);
        return new PixelRect(left, top, left + size, top + size);
    }

    // Une boîte plus grande que l'écran reste calée sur son bord de départ.
    private static int ClampToRange(int value, int minimum, int maximum)
    {
        return Math.Max(minimum, Math.Min(value, maximum));
    }
}
