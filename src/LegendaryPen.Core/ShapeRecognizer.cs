namespace LegendaryPen.Core;

/// <summary>Forme reconnue : l'outil et les deux points à passer à <see cref="ShapeGeometry.Build"/>.</summary>
public sealed record RecognizedShape(DrawingTool Tool, InkPoint Start, InkPoint End);

/// <summary>Reconnaît une ligne, un rectangle ou une ellipse dans un tracé à main levée.</summary>
public static class ShapeRecognizer
{
    private const double MinDiagonal = 24;
    private const double MaxLineDeviation = 0.04;
    private const double LineWobbleDeviation = 0.015;
    private const int MaxLineWobbles = 1;
    private const double MaxClosureGap = 0.15;
    private const double MaxPathOverDiagonal = 4;
    private const double MaxTilt = 20;
    private const double MaxEllipseResidual = 0.12;
    private const double SimplifyTolerance = 0.08;
    private const double MinEllipseSweep = 300;

    public static RecognizedShape? Recognize(IReadOnlyList<InkPoint> points)
    {
        if (points.Count < 2)
        {
            return null;
        }

        var minX = points.Min(p => p.X);
        var minY = points.Min(p => p.Y);
        var maxX = points.Max(p => p.X);
        var maxY = points.Max(p => p.Y);
        var width = maxX - minX;
        var height = maxY - minY;
        var diagonal = Math.Sqrt((width * width) + (height * height));

        if (diagonal < MinDiagonal)
        {
            return null;
        }

        var first = points[0];
        var last = points[^1];
        var chord = Distance(first, last);

        if (chord > 0
            && MaxDeviationFromChord(points, first, last) <= MaxLineDeviation * chord
            && CountWobbles(points, first, last) <= MaxLineWobbles)
        {
            return new RecognizedShape(DrawingTool.Line, first, last);
        }

        var pathLength = PathLength(points);

        if (chord > MaxClosureGap * pathLength || pathLength > MaxPathOverDiagonal * 2 * (width + height))
        {
            return null;
        }

        var box = new RecognizedShape(DrawingTool.Rectangle, new InkPoint(minX, minY), new InkPoint(maxX, maxY));

        if (IsRectangle(points, diagonal * SimplifyTolerance))
        {
            return box;
        }

        if (IsEllipse(points, (minX + maxX) / 2, (minY + maxY) / 2, width / 2, height / 2))
        {
            return box with { Tool = DrawingTool.Ellipse };
        }

        return null;
    }

    private static bool IsRectangle(IReadOnlyList<InkPoint> points, double tolerance)
    {
        var keep = new bool[points.Count];
        keep[0] = true;
        keep[^1] = true;
        Simplify(points, 0, points.Count - 1, tolerance, keep);

        var corners = new List<InkPoint>();

        for (var i = 0; i < points.Count; i++)
        {
            if (keep[i])
            {
                corners.Add(points[i]);
            }
        }

        // Le tracé est fermé : le dernier sommet rejoint le premier.
        if (Distance(corners[0], corners[^1]) <= tolerance)
        {
            corners.RemoveAt(corners.Count - 1);
        }

        // Un tracé commencé au milieu d'un côté laisse un faux sommet de départ, presque plat.
        if (corners.Count == 5 && TurnAngle(corners[^1], corners[0], corners[1]) < MaxTilt)
        {
            corners.RemoveAt(0);
        }

        if (corners.Count != 4)
        {
            return false;
        }

        for (var i = 0; i < 4; i++)
        {
            var from = corners[i];
            var to = corners[(i + 1) % 4];
            var angle = Math.Atan2(Math.Abs(to.Y - from.Y), Math.Abs(to.X - from.X)) * 180 / Math.PI;

            if (Math.Min(angle, 90 - angle) > MaxTilt)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsEllipse(IReadOnlyList<InkPoint> points, double centerX, double centerY, double radiusX, double radiusY)
    {
        if (radiusX <= 0 || radiusY <= 0)
        {
            return false;
        }

        var residual = 0.0;
        var sweep = 0.0;
        var previousAngle = 0.0;

        for (var i = 0; i < points.Count; i++)
        {
            var dx = (points[i].X - centerX) / radiusX;
            var dy = (points[i].Y - centerY) / radiusY;
            residual += Math.Abs(Math.Sqrt((dx * dx) + (dy * dy)) - 1);

            var angle = Math.Atan2(dy, dx);

            if (i > 0)
            {
                var step = angle - previousAngle;
                step -= 2 * Math.PI * Math.Round(step / (2 * Math.PI));
                sweep += step;
            }

            previousAngle = angle;
        }

        return residual / points.Count <= MaxEllipseResidual
            && Math.Abs(sweep) * 180 / Math.PI >= MinEllipseSweep;
    }

    /// <summary>Douglas-Peucker : marque les points à conserver entre deux indices.</summary>
    private static void Simplify(IReadOnlyList<InkPoint> points, int from, int to, double tolerance, bool[] keep)
    {
        var farthest = -1;
        var farthestDistance = tolerance;

        for (var i = from + 1; i < to; i++)
        {
            var distance = DistanceToSegment(points[i], points[from], points[to]);

            if (distance > farthestDistance)
            {
                farthest = i;
                farthestDistance = distance;
            }
        }

        if (farthest < 0)
        {
            return;
        }

        keep[farthest] = true;
        Simplify(points, from, farthest, tolerance, keep);
        Simplify(points, farthest, to, tolerance, keep);
    }

    private static double MaxDeviationFromChord(IReadOnlyList<InkPoint> points, InkPoint first, InkPoint last)
    {
        var deviation = 0.0;

        foreach (var point in points)
        {
            deviation = Math.Max(deviation, DistanceToLine(point, first, last));
        }

        return deviation;
    }

    /// <summary>Nombre de passages d'un côté à l'autre de la corde, hors de la bande de tolérance : une ondulation n'est pas une droite.</summary>
    private static int CountWobbles(IReadOnlyList<InkPoint> points, InkPoint first, InkPoint last)
    {
        var band = LineWobbleDeviation * Distance(first, last);
        var chordX = last.X - first.X;
        var chordY = last.Y - first.Y;
        var chordLength = Distance(first, last);
        var side = 0;
        var reversals = 0;

        foreach (var point in points)
        {
            var signed = ((chordX * (point.Y - first.Y)) - (chordY * (point.X - first.X))) / chordLength;
            var current = signed > band ? 1 : signed < -band ? -1 : 0;

            if (current == 0)
            {
                continue;
            }

            if (side != 0 && current != side)
            {
                reversals++;
            }

            side = current;
        }

        return reversals;
    }

    private static double PathLength(IReadOnlyList<InkPoint> points)
    {
        var length = 0.0;

        for (var i = 1; i < points.Count; i++)
        {
            length += Distance(points[i - 1], points[i]);
        }

        return length;
    }

    private static double TurnAngle(InkPoint before, InkPoint corner, InkPoint after)
    {
        var incoming = Math.Atan2(corner.Y - before.Y, corner.X - before.X);
        var outgoing = Math.Atan2(after.Y - corner.Y, after.X - corner.X);
        var turn = Math.Abs(outgoing - incoming);

        return (turn > Math.PI ? (2 * Math.PI) - turn : turn) * 180 / Math.PI;
    }

    private static double DistanceToLine(InkPoint point, InkPoint a, InkPoint b)
    {
        var length = Distance(a, b);

        return Math.Abs(((b.X - a.X) * (a.Y - point.Y)) - ((a.X - point.X) * (b.Y - a.Y))) / length;
    }

    private static double DistanceToSegment(InkPoint point, InkPoint a, InkPoint b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var lengthSquared = (dx * dx) + (dy * dy);

        if (lengthSquared == 0)
        {
            return Distance(point, a);
        }

        var t = Math.Clamp((((point.X - a.X) * dx) + ((point.Y - a.Y) * dy)) / lengthSquared, 0, 1);

        return Distance(point, new InkPoint(a.X + (t * dx), a.Y + (t * dy)));
    }

    private static double Distance(InkPoint a, InkPoint b) =>
        Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2));
}
