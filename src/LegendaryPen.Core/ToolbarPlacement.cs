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
    /// Conserve la position enregistrée si le centre de la barre reste sur un écran,
    /// sinon la place en haut et au centre de l'écran principal.
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

            if (screens.Any(screen => screen.Contains(centerX, centerY)))
            {
                return position;
            }
        }

        return new PixelPoint(
            primaryWorkArea.Left + ((primaryWorkArea.Width - width) / 2),
            primaryWorkArea.Top + DefaultTopMargin);
    }
}
