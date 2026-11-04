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

/// <summary>Bloc de cases de la grille du voile, bornes comprises.</summary>
public readonly record struct SpotlightCells(int FirstColumn, int FirstRow, int LastColumn, int LastRow)
{
    public bool Contains(int column, int row) => column >= FirstColumn && column <= LastColumn && row >= FirstRow && row <= LastRow;
}

/// <summary>
/// Géométrie des effets. La loupe est en pixels physiques (PR-3). Les anneaux (PR-4) et les cases du projecteur sont en
/// DIP : ils suivent l'échelle de l'écran.
/// </summary>
public static class PresentationGeometry
{
    public const double RippleDuration = 400;

    public const double RippleMaxRadius = 48;

    /// <summary>Plafond de mise à jour du projecteur quand le pointeur bouge.</summary>
    public const int SpotlightUpdatesPerSecond = 60;

    private const double SpotlightCellSize = 128;

    /// <summary>Boîte de la loupe : centrée sur le pointeur, ramenée dans l'écran.</summary>
    public static PixelRect MagnifierWindow(PixelPoint pointer, int diameter, PixelRect monitor)
    {
        return CenteredInside(pointer, diameter, monitor);
    }

    /// <summary>
    /// Zone d'écran grossie, calée sur la boîte de la loupe : le point affiché sous le curseur est celui qu'un clic touche,
    /// même quand la boîte est ramenée dans l'écran. Loin des bords, la zone est donc centrée sur le pointeur.
    /// </summary>
    public static PixelRect MagnifierSource(PixelPoint pointer, int diameter, double zoom, PixelRect monitor)
    {
        if (!(zoom > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(zoom), zoom, "The zoom must be above zero.");
        }

        var box = MagnifierWindow(pointer, diameter, monitor);
        var size = (int)Math.Round(diameter / zoom);
        var left = ClampToRange(SourceOrigin(pointer.X, box.Left, zoom), monitor.Left, monitor.Right - size);
        var top = ClampToRange(SourceOrigin(pointer.Y, box.Top, zoom), monitor.Top, monitor.Bottom - size);
        return new PixelRect(left, top, left + size, top + size);
    }

    /// <summary>Un appui ferme le menu de présentation seulement s'il tombe hors du menu et hors de son bouton.</summary>
    public static bool PressClosesMenu(PixelPoint press, PixelRect menu, PixelRect button)
    {
        return !menu.Contains(press.X, press.Y) && !button.Contains(press.X, press.Y);
    }

    /// <summary>L'anneau grandit et s'efface de façon linéaire, puis reste invisible. Un âge inconnu vaut un anneau fini.</summary>
    public static ClickRipple RippleAt(double elapsedMilliseconds)
    {
        var progress = double.IsNaN(elapsedMilliseconds) ? 1 : Math.Clamp(elapsedMilliseconds / RippleDuration, 0, 1);
        return new ClickRipple(RippleMaxRadius * progress, 1 - progress);
    }

    /// <summary>Côté d'une case du voile, en DIP : un nombre entier de pixels physiques, pour que les bords tombent sur la grille de l'écran.</summary>
    public static double SpotlightCell(double scale)
    {
        return Math.Round(SpotlightCellSize * scale) / scale;
    }

    /// <summary>Cases que touche le trou : ce sont les seules à ne pas porter le voile, et la seule zone à redessiner.</summary>
    public static SpotlightCells CellsAround(double centerX, double centerY, double radius, double cellX, double cellY, int columns, int rows)
    {
        return new SpotlightCells(
            CellIndex(centerX - radius, cellX, columns),
            CellIndex(centerY - radius, cellY, rows),
            CellIndex(centerX + radius, cellX, columns),
            CellIndex(centerY + radius, cellY, rows));
    }

    /// <summary>Tranche de temps d'un instant : une seule mise à jour par tranche tient le plafond, même si les réveils sont irréguliers.</summary>
    public static long SpotlightSlot(TimeSpan elapsed)
    {
        return (long)(elapsed.TotalSeconds * SpotlightUpdatesPerSecond);
    }

    private static int CellIndex(double position, double cell, int count)
    {
        return Math.Clamp((int)Math.Floor(position / cell), 0, count - 1);
    }

    private static int SourceOrigin(int pointer, int boxOrigin, double zoom)
    {
        return (int)Math.Round(pointer - ((pointer - boxOrigin) / zoom));
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
