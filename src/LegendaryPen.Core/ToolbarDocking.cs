namespace LegendaryPen.Core;

public enum ToolbarDock
{
    None,
    Top,
    Bottom,
    Left,
    Right,
}

/// <summary>Règles d'attache de la barre aux bords de l'écran et de son repli automatique.</summary>
public static class ToolbarDocking
{
    public const double MinCollapseSeconds = 2;
    public const double MaxCollapseSeconds = 120;
    public const double DefaultCollapseSeconds = 5;

    /// <summary>
    /// Renvoie le bord le plus proche du pointeur au moment du dépôt, s'il est à moins de
    /// <paramref name="threshold"/> pixels. Le pointeur décide car la barre, très longue, peut dépasser de l'écran.
    /// </summary>
    public static ToolbarDock Detect(PixelPoint pointer, PixelRect workArea, int threshold)
    {
        (ToolbarDock Dock, int Distance)[] edges =
        [
            (ToolbarDock.Top, Math.Abs(pointer.Y - workArea.Top)),
            (ToolbarDock.Bottom, Math.Abs(workArea.Bottom - pointer.Y)),
            (ToolbarDock.Left, Math.Abs(pointer.X - workArea.Left)),
            (ToolbarDock.Right, Math.Abs(workArea.Right - pointer.X)),
        ];

        var nearest = edges.MinBy(edge => edge.Distance);

        return nearest.Distance <= threshold ? nearest.Dock : ToolbarDock.None;
    }

    /// <summary>Colle la barre au bord demandé et la centre le long de celui-ci.</summary>
    public static PixelPoint Place(ToolbarDock dock, int width, int height, PixelRect workArea)
    {
        var centeredX = workArea.Left + ((workArea.Width - width) / 2);
        var centeredY = workArea.Top + ((workArea.Height - height) / 2);

        return dock switch
        {
            ToolbarDock.Top => new PixelPoint(centeredX, workArea.Top),
            ToolbarDock.Bottom => new PixelPoint(centeredX, workArea.Bottom - height),
            ToolbarDock.Left => new PixelPoint(workArea.Left, centeredY),
            ToolbarDock.Right => new PixelPoint(workArea.Right - width, centeredY),
            _ => throw new ArgumentOutOfRangeException(nameof(dock), dock, "La barre n'est attachée à aucun bord."),
        };
    }

    /// <summary>Sur les côtés, la barre est verticale ; en haut et en bas, horizontale ; sinon, au choix.</summary>
    public static bool IsVertical(ToolbarDock dock, bool floatingVertical)
    {
        return dock switch
        {
            ToolbarDock.Left or ToolbarDock.Right => true,
            ToolbarDock.Top or ToolbarDock.Bottom => false,
            _ => floatingVertical,
        };
    }

    public static bool ShouldCollapse(
        ToolbarDock dock,
        TimeSpan idle,
        TimeSpan delay,
        bool pointerOver,
        bool dialogOpen,
        bool alreadyCollapsed)
    {
        return dock != ToolbarDock.None && !alreadyCollapsed && !pointerOver && !dialogOpen && idle >= delay;
    }

    public static TimeSpan CollapseDelay(double? seconds)
    {
        return TimeSpan.FromSeconds(Math.Clamp(seconds ?? DefaultCollapseSeconds, MinCollapseSeconds, MaxCollapseSeconds));
    }

    /// <summary>Une valeur inconnue dans les réglages revient à une barre libre.</summary>
    public static ToolbarDock Sanitize(ToolbarDock dock) => Enum.IsDefined(dock) ? dock : ToolbarDock.None;
}
