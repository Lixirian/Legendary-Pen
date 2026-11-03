namespace LegendaryPen.Core;

/// <summary>Image en niveaux de gris, ligne par ligne.</summary>
public sealed record GrayImage(int Width, int Height, byte[] Pixels)
{
    public byte this[int x, int y] => Pixels[(y * Width) + x];
}

/// <summary>Zone de texte trouvée, en pixels de l'image ; Right et Bottom sont exclus.</summary>
public readonly record struct TextSpan(int Left, int Top, int Right, int Bottom)
{
    public int Height => Bottom - Top;
}

public static class HighlightLine
{
    public const double HorizontalSnapDegrees = 12;

    /// <summary>Droite qui résume un tracé à main levée. Presque horizontale, elle devient parfaitement horizontale.</summary>
    public static (InkPoint Start, InkPoint End, bool IsHorizontal) Straighten(IReadOnlyList<InkPoint> points)
    {
        var meanX = points.Average(point => point.X);
        var meanY = points.Average(point => point.Y);

        double xx = 0, yy = 0, xy = 0;

        foreach (var point in points)
        {
            xx += (point.X - meanX) * (point.X - meanX);
            yy += (point.Y - meanY) * (point.Y - meanY);
            xy += (point.X - meanX) * (point.Y - meanY);
        }

        var angle = 0.5 * Math.Atan2(2 * xy, xx - yy);
        var degrees = Math.Abs(angle * 180 / Math.PI);

        if (degrees <= HorizontalSnapDegrees)
        {
            return (new InkPoint(points.Min(point => point.X), meanY), new InkPoint(points.Max(point => point.X), meanY), true);
        }

        var (cos, sin) = (Math.Cos(angle), Math.Sin(angle));
        var positions = points.Select(point => ((point.X - meanX) * cos) + ((point.Y - meanY) * sin)).ToList();
        var (low, high) = (positions.Min(), positions.Max());

        // Le trait garde le sens dans lequel il a été tracé.
        if (positions[0] > positions[^1])
        {
            (low, high) = (high, low);
        }

        return (
            new InkPoint(meanX + (low * cos), meanY + (low * sin)),
            new InkPoint(meanX + (high * cos), meanY + (high * sin)),
            false);
    }
}

/// <summary>Repère la ligne de texte visée par un surlignage, à partir des pixels de l'écran.</summary>
public static class HighlightSnapper
{
    private const int BucketCount = 32;
    private const int InkContrast = 50;
    private const double MinBackgroundShare = 0.5;
    private const double MaxInkDensity = 0.85;
    private const int MinLineHeight = 6;
    private const int MaxLineHeight = 120;
    private const double MinRowInkShare = 0.01;
    private const double MinWordGapPerHeight = 0.2;
    private const double MaxWordGapPerHeight = 0.5;
    private const double DefaultWordGapPerHeight = 0.35;
    private const int MinGapJump = 2;
    private const double MaxStrokeDistancePerHeight = 0.75;
    private const double MinEdgeWordOverlap = 0.25;

    /// <summary>Renvoie null s'il n'y a pas de texte reconnaissable : fond chargé, photo ou aplat.</summary>
    public static TextSpan? FindText(GrayImage image, int strokeLeft, int strokeRight, int strokeY)
    {
        var left = Math.Clamp(Math.Min(strokeLeft, strokeRight), 0, image.Width - 1);
        var right = Math.Clamp(Math.Max(strokeLeft, strokeRight), left + 1, image.Width);

        if (FindBackground(image) is not { } background)
        {
            return null;
        }

        bool IsInk(int x, int y) => Math.Abs(image[x, y] - background) > InkContrast;

        var rowThreshold = Math.Max(2, (int)((right - left) * MinRowInkShare));
        var rowHasInk = new bool[image.Height];

        for (var y = 0; y < image.Height; y++)
        {
            var count = 0;

            for (var x = left; x < right; x++)
            {
                count += IsInk(x, y) ? 1 : 0;
            }

            rowHasInk[y] = count >= rowThreshold;
        }

        if (FindNearestRow(rowHasInk, Math.Clamp(strokeY, 0, image.Height - 1)) is not { } row)
        {
            return null;
        }

        var top = row;
        var bottom = row + 1;

        while (top > 0 && rowHasInk[top - 1])
        {
            top--;
        }

        while (bottom < image.Height && rowHasInk[bottom])
        {
            bottom++;
        }

        var height = bottom - top;

        if (height < MinLineHeight || height > MaxLineHeight)
        {
            return null;
        }

        // Un trait tracé à côté d'une ligne ne la vise pas.
        var clampedY = Math.Clamp(strokeY, 0, image.Height - 1);
        var distance = clampedY < top ? top - clampedY : clampedY >= bottom ? clampedY - bottom + 1 : 0;

        if (strokeY != clampedY || distance > height * MaxStrokeDistancePerHeight)
        {
            return null;
        }

        var columnHasInk = new bool[image.Width];
        var inkPixels = 0;

        for (var x = 0; x < image.Width; x++)
        {
            for (var y = top; y < bottom; y++)
            {
                if (IsInk(x, y))
                {
                    columnHasInk[x] = true;

                    if (x >= left && x < right)
                    {
                        inkPixels++;
                    }
                }
            }
        }

        if (inkPixels > (right - left) * height * MaxInkDensity)
        {
            return null;
        }

        var words = FindWords(columnHasInk, FindWordGap(columnHasInk, height));
        var touched = words.Where(word => word.Right > left && word.Left < right).ToList();

        if (touched.Count == 0)
        {
            return null;
        }

        // Un mot à peine effleuré au bout du trait ne fait pas partie de la sélection.
        bool WellCovered((int Left, int Right) word)
        {
            var overlap = Math.Min(word.Right, right) - Math.Max(word.Left, left);
            return overlap >= (word.Right - word.Left) * MinEdgeWordOverlap;
        }

        var covered = touched.Where(WellCovered).ToList();
        var selected = covered.Count > 0 ? covered : touched;

        return new TextSpan(selected[0].Left, top, selected[^1].Right, bottom);
    }

    private static int? FindBackground(GrayImage image)
    {
        var counts = new int[BucketCount];
        var sums = new long[BucketCount];

        foreach (var pixel in image.Pixels)
        {
            var bucket = pixel * BucketCount / 256;
            counts[bucket]++;
            sums[bucket] += pixel;
        }

        var mode = Array.IndexOf(counts, counts.Max());

        return counts[mode] < image.Pixels.Length * MinBackgroundShare ? null : (int)(sums[mode] / counts[mode]);
    }

    private static int? FindNearestRow(bool[] rowHasInk, int y)
    {
        for (var distance = 0; distance < rowHasInk.Length; distance++)
        {
            if (y - distance >= 0 && rowHasInk[y - distance])
            {
                return y - distance;
            }

            if (y + distance < rowHasInk.Length && rowHasInk[y + distance])
            {
                return y + distance;
            }
        }

        return null;
    }

    /// <summary>
    /// Longueur à partir de laquelle un blanc sépare deux mots. Elle dépend de la police : on cherche,
    /// sur la ligne, le saut entre les écarts de lettres et les espaces.
    /// </summary>
    private static double FindWordGap(bool[] columnHasInk, int height)
    {
        var minimum = height * MinWordGapPerHeight;
        var maximum = height * MaxWordGapPerHeight;
        var gaps = new SortedSet<int>();
        int? lastInk = null;

        for (var x = 0; x < columnHasInk.Length; x++)
        {
            if (!columnHasInk[x])
            {
                continue;
            }

            // Un très grand blanc sépare des colonnes de texte, pas des mots.
            if (lastInk is { } previous && x - previous - 1 is > 0 and var gap && gap <= height)
            {
                gaps.Add(gap);
            }

            lastInk = x;
        }

        var ordered = gaps.ToList();
        var bestJump = 0;
        var threshold = height * DefaultWordGapPerHeight;

        for (var i = 1; i < ordered.Count; i++)
        {
            var jump = ordered[i] - ordered[i - 1];

            if (jump >= MinGapJump && jump > bestJump)
            {
                bestJump = jump;
                threshold = (ordered[i] + ordered[i - 1]) / 2.0;
            }
        }

        return Math.Clamp(threshold, minimum, maximum);
    }

    private static List<(int Left, int Right)> FindWords(bool[] columnHasInk, double wordGap)
    {
        var words = new List<(int Left, int Right)>();
        int? start = null;
        var lastInk = 0;

        for (var x = 0; x < columnHasInk.Length; x++)
        {
            if (!columnHasInk[x])
            {
                continue;
            }

            if (start is { } wordStart && x - lastInk - 1 > wordGap)
            {
                words.Add((wordStart, lastInk + 1));
                start = x;
            }

            start ??= x;
            lastInk = x;
        }

        if (start is { } lastStart)
        {
            words.Add((lastStart, lastInk + 1));
        }

        return words;
    }
}
