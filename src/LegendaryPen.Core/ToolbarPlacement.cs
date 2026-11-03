namespace LegendaryPen.Core;

/// <summary>Rectangle en pixels physiques ; Right et Bottom sont exclus.</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;

    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
}

public readonly record struct PixelPoint(int X, int Y);

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
