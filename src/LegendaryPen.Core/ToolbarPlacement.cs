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
    /// Contre la barre, sans la recouvrir et sans sortir de la zone. Un côté où tout le panneau tient passe d'abord : à côté
    /// d'une barre verticale, sous ou sur une barre horizontale, puis les deux autres côtés. Sinon le panneau est plafonné du
    /// côté qui a le plus de place. Il ne chevauche la barre que si elle ne laisse rien autour d'elle, dans aucun sens.
    /// </summary>
    public static LayersPanelPlace Beside(PixelRect bar, bool vertical, PixelRect area, int width, int natural)
    {
        var below = area.Bottom - bar.Bottom;
        var above = bar.Top - area.Top;
        var fitsSideways = area.Right - bar.Right >= width || bar.Left - area.Left >= width;
        var fitsLengthways = below >= natural || above >= natural;

        if (fitsSideways && (vertical || !fitsLengthways))
        {
            return Sideways(bar, area, width, natural);
        }

        if (fitsLengthways || Math.Max(below, above) > 0)
        {
            return Lengthways(bar, area, width, natural, below >= natural || (above < natural && below >= above));
        }

        return Place(bar.Left, bar.Top, area, width, HeightIn(area, natural), natural);
    }

    private static LayersPanelPlace Sideways(PixelRect bar, PixelRect area, int width, int natural)
    {
        var x = area.Right - bar.Right >= width ? bar.Right : bar.Left - width;

        return Place(x, bar.Top, area, width, HeightIn(area, natural), natural);
    }

    private static LayersPanelPlace Lengthways(PixelRect bar, PixelRect area, int width, int natural, bool onBelow)
    {
        var room = onBelow ? area.Bottom - bar.Bottom : bar.Top - area.Top;
        var height = Math.Min(HeightIn(area, natural), room);

        return Place(bar.Left, onBelow ? bar.Bottom : bar.Top - height, area, width, height, natural);
    }

    private static int HeightIn(PixelRect area, int natural) => Math.Clamp(natural, 0, Math.Max(area.Height, 0));

    private static LayersPanelPlace Place(int x, int y, PixelRect area, int width, int height, int natural)
    {
        return new LayersPanelPlace(
            Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - width)),
            Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - height)),
            height < natural ? height : null);
    }
}

/// <summary>Empile les notifications dans le coin bas droit d'une zone : la plus récente en bas, chacune au-dessus de la suivante.</summary>
public static class NotificationPlacement
{
    /// <summary>Au-delà, la plus ancienne se ferme : la pile ne doit pas sortir de l'écran.</summary>
    public const int MaxVisible = 4;

    /// <param name="area">Zone de travail de l'écran.</param>
    /// <param name="sizes">Largeur et hauteur de chaque notification, de la plus ancienne à la plus récente.</param>
    /// <param name="margin">Écart avec le bord et entre deux notifications.</param>
    public static IReadOnlyList<PixelPoint> Stack(PixelRect area, IReadOnlyList<(int Width, int Height)> sizes, int margin)
    {
        var places = new PixelPoint[sizes.Count];
        var bottom = area.Bottom;

        for (var index = sizes.Count - 1; index >= 0; index--)
        {
            var (width, height) = sizes[index];
            bottom -= margin + height;
            places[index] = new PixelPoint(area.Right - margin - width, bottom);
        }

        return places;
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
