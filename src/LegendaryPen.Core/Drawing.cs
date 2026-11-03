namespace LegendaryPen.Core;

public enum DrawingTool
{
    Pen,
    Highlighter,
    Eraser,
    Line,
    Arrow,
    Rectangle,
    Ellipse,
    Text,
}

public static class DrawingTools
{
    public static bool IsShape(this DrawingTool tool) =>
        tool is DrawingTool.Line or DrawingTool.Arrow or DrawingTool.Rectangle or DrawingTool.Ellipse;
}

public readonly record struct InkPoint(double X, double Y);

/// <summary>Calcule les points d'une forme, en coordonnées d'écran.</summary>
public static class ShapeGeometry
{
    private const int EllipseSegments = 96;
    private const double ArrowHeadAngle = Math.PI / 7;
    private const double MinArrowHeadLength = 14;

    public static IReadOnlyList<InkPoint> Build(
        DrawingTool tool,
        InkPoint start,
        InkPoint end,
        bool constrained,
        double strokeSize)
    {
        var target = constrained ? Constrain(tool, start, end) : end;

        return tool switch
        {
            DrawingTool.Line => [start, target],
            DrawingTool.Arrow => BuildArrow(start, target, strokeSize),
            DrawingTool.Rectangle => BuildRectangle(start, target),
            DrawingTool.Ellipse => BuildEllipse(start, target),
            _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, "Cet outil n'est pas une forme."),
        };
    }

    /// <summary>Maj : ligne par pas de 45°, carré ou cercle.</summary>
    public static InkPoint Constrain(DrawingTool tool, InkPoint start, InkPoint end)
    {
        var deltaX = end.X - start.X;
        var deltaY = end.Y - start.Y;

        if (tool is DrawingTool.Rectangle or DrawingTool.Ellipse)
        {
            var side = Math.Max(Math.Abs(deltaX), Math.Abs(deltaY));
            return new InkPoint(start.X + (Sign(deltaX) * side), start.Y + (Sign(deltaY) * side));
        }

        var length = Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
        var step = Math.PI / 4;
        var angle = Math.Round(Math.Atan2(deltaY, deltaX) / step) * step;

        return new InkPoint(start.X + (Math.Cos(angle) * length), start.Y + (Math.Sin(angle) * length));
    }

    private static InkPoint[] BuildArrow(InkPoint start, InkPoint tip, double strokeSize)
    {
        var deltaX = tip.X - start.X;
        var deltaY = tip.Y - start.Y;
        var length = Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));

        if (length == 0)
        {
            return [start, tip];
        }

        var angle = Math.Atan2(deltaY, deltaX);
        var head = Math.Min(length * 0.6, Math.Max(MinArrowHeadLength, strokeSize * 4));

        InkPoint Wing(double direction) => new(
            tip.X - (head * Math.Cos(angle + direction)),
            tip.Y - (head * Math.Sin(angle + direction)));

        // Un seul tracé : la flèche s'efface et s'annule d'un bloc.
        return [start, tip, Wing(ArrowHeadAngle), tip, Wing(-ArrowHeadAngle)];
    }

    private static InkPoint[] BuildRectangle(InkPoint first, InkPoint second) =>
    [
        first,
        new(second.X, first.Y),
        second,
        new(first.X, second.Y),
        first,
    ];

    private static InkPoint[] BuildEllipse(InkPoint first, InkPoint second)
    {
        var centerX = (first.X + second.X) / 2;
        var centerY = (first.Y + second.Y) / 2;
        var radiusX = Math.Abs(second.X - first.X) / 2;
        var radiusY = Math.Abs(second.Y - first.Y) / 2;
        var points = new InkPoint[EllipseSegments + 1];

        for (var i = 0; i <= EllipseSegments; i++)
        {
            var angle = 2 * Math.PI * (i % EllipseSegments) / EllipseSegments;
            points[i] = new InkPoint(centerX + (radiusX * Math.Cos(angle)), centerY + (radiusY * Math.Sin(angle)));
        }

        return points;
    }

    private static int Sign(double value) => value < 0 ? -1 : 1;
}

/// <summary>Délai avant disparition et durée du fondu de l'encre évanescente.</summary>
public sealed record FadeTiming
{
    public const double MinDelay = 1;
    public const double MaxDelay = 30;
    public const double DefaultDelay = 3;
    public const double MinDuration = 0.2;
    public const double MaxDuration = 5;
    public const double DefaultDuration = 1;

    public FadeTiming(double? delaySeconds = null, double? durationSeconds = null)
    {
        Delay = TimeSpan.FromSeconds(Math.Clamp(delaySeconds ?? DefaultDelay, MinDelay, MaxDelay));
        Duration = TimeSpan.FromSeconds(Math.Clamp(durationSeconds ?? DefaultDuration, MinDuration, MaxDuration));
    }

    public TimeSpan Delay { get; }

    public TimeSpan Duration { get; }
}

/// <summary>Un historique par fond : Annuler n'agit que sur ce qui est affiché.</summary>
public sealed class SurfaceHistories
{
    private readonly AppState _state;
    private readonly Dictionary<BoardBackground, ActionHistory> _histories = [];

    public SurfaceHistories(AppState state)
    {
        _state = state;

        foreach (var background in Enum.GetValues<BoardBackground>())
        {
            var history = new ActionHistory();
            history.Changed += () => Changed?.Invoke();
            _histories[background] = history;
        }
    }

    public ActionHistory Current => _histories[_state.Background];

    public event Action? Changed;

    public ActionHistory For(BoardBackground background) => _histories[background];
}

/// <summary>Assemble un fichier .cur à partir d'une image BGRA de haut en bas, sans prémultiplication.</summary>
public static class CursorFile
{
    public static byte[] Build(int width, int height, byte[] bgra, int hotspotX, int hotspotY)
    {
        if (bgra.Length != width * height * 4)
        {
            throw new ArgumentException("La taille de l'image ne correspond pas aux dimensions.", nameof(bgra));
        }

        const int FileHeaderSize = 22;
        const int BitmapHeaderSize = 40;

        var maskStride = ((width + 31) / 32) * 4;
        var imageSize = BitmapHeaderSize + bgra.Length + (maskStride * height);

        using var stream = new MemoryStream(FileHeaderSize + imageSize);
        using var writer = new BinaryWriter(stream);

        writer.Write((ushort)0);
        writer.Write((ushort)2);
        writer.Write((ushort)1);
        writer.Write((byte)(width >= 256 ? 0 : width));
        writer.Write((byte)(height >= 256 ? 0 : height));
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((ushort)hotspotX);
        writer.Write((ushort)hotspotY);
        writer.Write(imageSize);
        writer.Write(FileHeaderSize);

        writer.Write(BitmapHeaderSize);
        writer.Write(width);
        writer.Write(height * 2);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(0);
        writer.Write(bgra.Length + (maskStride * height));
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);

        // Le format attend les lignes de bas en haut.
        for (var row = height - 1; row >= 0; row--)
        {
            writer.Write(bgra, row * width * 4, width * 4);
        }

        // La transparence vient du canal alpha : le masque reste vide.
        writer.Write(new byte[maskStride * height]);
        writer.Flush();

        return stream.ToArray();
    }
}
