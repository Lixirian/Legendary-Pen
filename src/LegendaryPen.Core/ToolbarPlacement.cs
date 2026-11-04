namespace LegendaryPen.Core;

/// <summary>Rectangle en pixels physiques ; Right et Bottom sont exclus.</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;

    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
}

public readonly record struct PixelPoint(int X, int Y);

/// <summary>Où poser le panneau des calques, en pixels physiques ; <paramref name="MaxHeight"/> est nul si toute sa hauteur tient.</summary>
public readonly record struct LayersPanelPlace(int X, int Y, int? MaxHeight);

public static class LayersPanelPlacement
{
    /// <summary>
    /// Contre la barre, du côté où le panneau tient, sans sortir de la zone ; il est plafonné si la zone est trop basse.
    /// Si la barre ne laisse aucune place au-dessus ni au-dessous, le panneau la chevauche plutôt que de quitter la zone.
    /// </summary>
    public static LayersPanelPlace Beside(PixelRect bar, bool vertical, PixelRect area, int width, int natural)
    {
        int x;
        int y;
        int height;

        if (vertical)
        {
            var right = area.Right - bar.Right;
            var left = bar.Left - area.Left;
            var onRight = right >= width || right >= left;
            height = Math.Min(natural, area.Height);
            x = onRight ? bar.Right : bar.Left - width;
            y = Math.Clamp(bar.Top, area.Top, area.Bottom - height);
        }
        else
        {
            var below = area.Bottom - bar.Bottom;
            var above = bar.Top - area.Top;
            var onBelow = below >= natural || below >= above;
            var room = onBelow ? below : above;

            var inArea = Math.Clamp(natural, 0, Math.Max(area.Height, 0));

            x = bar.Left;
            height = room > 0 ? Math.Min(inArea, room) : inArea;
            y = Math.Clamp(onBelow ? bar.Bottom : bar.Top - height, area.Top, Math.Max(area.Top, area.Bottom - height));
        }

        x = Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - width));
        return new LayersPanelPlace(x, y, height < natural ? height : null);
    }
}

public static class ToolbarPlacement
{
    private const int DefaultTopMargin = 16;

    /// <summary>
    /// Conserve la position enregistrée si la barre reste sur un écran, en la ramenant dans
    /// ses limites si besoin, sinon la place en haut et au centre de l'écran principal.
    /// </summary>
    public static PixelPoint Resolve(
        PixelPoint? saved,
        int width,
        int height,
        IReadOnlyList<PixelRect> screens,
        PixelRect primaryWorkArea)
    {
        if (saved is { } position)
        {
            var centerX = position.X + (width / 2);
            var centerY = position.Y + (height / 2);

            // Après une rotation, le centre peut sortir de l'écran alors que le coin de départ y reste.
            var screen = screens.Cast<PixelRect?>().FirstOrDefault(candidate => candidate!.Value.Contains(centerX, centerY))
                ?? screens.Cast<PixelRect?>().FirstOrDefault(candidate => candidate!.Value.Contains(position.X, position.Y));

            if (screen is { } target)
            {
                return new PixelPoint(
                    ClampToRange(position.X, target.Left, target.Right - width),
                    ClampToRange(position.Y, target.Top, target.Bottom - height));
            }
        }

        return new PixelPoint(
            primaryWorkArea.Left + ((primaryWorkArea.Width - width) / 2),
            primaryWorkArea.Top + DefaultTopMargin);
    }

    // Une barre plus grande que l'écran reste calée sur son bord de départ.
    private static int ClampToRange(int value, int minimum, int maximum)
    {
        return Math.Max(minimum, Math.Min(value, maximum));
    }
}
