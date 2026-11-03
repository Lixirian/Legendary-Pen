using LegendaryPen.Core;

namespace LegendaryPen.Tests;

public class ShapeRecognizerTests
{
    private const int PointCount = 80;
    private const double NoiseAmplitude = 2;
    private const int SeedCount = 30;

    [Fact]
    public void A_noisy_line_is_a_line()
    {
        var shape = ShapeRecognizer.Recognize(Segment(new InkPoint(100, 100), new InkPoint(500, 260)));

        Assert.NotNull(shape);
        Assert.Equal(DrawingTool.Line, shape.Tool);
        AssertNear(new InkPoint(100, 100), shape.Start, 8);
        AssertNear(new InkPoint(500, 260), shape.End, 8);
    }

    [Fact]
    public void A_noisy_rectangle_is_a_rectangle()
    {
        var shape = ShapeRecognizer.Recognize(Polygon(
            new InkPoint(100, 100), new InkPoint(500, 100), new InkPoint(500, 300), new InkPoint(100, 300)));

        Assert.NotNull(shape);
        Assert.Equal(DrawingTool.Rectangle, shape.Tool);
        AssertNear(new InkPoint(100, 100), shape.Start, 8);
        AssertNear(new InkPoint(500, 300), shape.End, 8);
    }

    [Fact]
    public void A_noisy_circle_is_an_ellipse()
    {
        var shape = ShapeRecognizer.Recognize(Arc(new InkPoint(300, 300), 120, 120, 1));

        Assert.NotNull(shape);
        Assert.Equal(DrawingTool.Ellipse, shape.Tool);
        AssertNear(new InkPoint(180, 180), shape.Start, 10);
        AssertNear(new InkPoint(420, 420), shape.End, 10);
    }

    [Fact]
    public void A_noisy_ellipse_is_an_ellipse()
    {
        var shape = ShapeRecognizer.Recognize(Arc(new InkPoint(300, 300), 200, 80, 1));

        Assert.NotNull(shape);
        Assert.Equal(DrawingTool.Ellipse, shape.Tool);
        AssertNear(new InkPoint(100, 220), shape.Start, 10);
        AssertNear(new InkPoint(500, 380), shape.End, 10);
    }

    [Fact]
    public void An_overlapping_circle_is_an_ellipse()
    {
        var shape = ShapeRecognizer.Recognize(Arc(new InkPoint(300, 300), 120, 120, 1.15));

        Assert.NotNull(shape);
        Assert.Equal(DrawingTool.Ellipse, shape.Tool);
    }

    [Fact]
    public void A_circle_with_a_small_gap_is_an_ellipse()
    {
        var shape = ShapeRecognizer.Recognize(Arc(new InkPoint(300, 300), 120, 120, 0.92));

        Assert.NotNull(shape);
        Assert.Equal(DrawingTool.Ellipse, shape.Tool);
    }

    [Fact]
    public void A_click_is_not_a_shape()
    {
        InkPoint[] click = [new(100, 100), new(101, 100.5), new(100.5, 101)];

        Assert.Null(ShapeRecognizer.Recognize(click));
    }

    [Fact]
    public void A_tiny_stroke_is_not_a_shape()
    {
        Assert.Null(ShapeRecognizer.Recognize(Segment(new InkPoint(100, 100), new InkPoint(112, 116))));
    }

    [Fact]
    public void A_scribble_is_not_a_shape()
    {
        var points = new List<InkPoint>();

        for (var i = 0; i < PointCount; i++)
        {
            points.Add(new InkPoint(100 + (i * 4), i % 2 == 0 ? 100 : 160));
        }

        Assert.Null(ShapeRecognizer.Recognize(points));
    }

    [Fact]
    public void A_handwritten_word_is_not_a_shape()
    {
        var points = new List<InkPoint>();

        for (var i = 0; i < PointCount; i++)
        {
            var t = i / (double)(PointCount - 1);
            points.Add(new InkPoint(100 + (400 * t), 200 + (30 * Math.Sin(2 * Math.PI * 5 * t))));
        }

        Assert.Null(ShapeRecognizer.Recognize(points));
    }

    [Fact]
    public void A_wavy_underline_is_not_a_shape()
    {
        Assert.Null(ShapeRecognizer.Recognize(Wave(10)));
    }

    [Fact]
    public void A_slightly_wavy_underline_is_not_a_shape()
    {
        Assert.Null(ShapeRecognizer.Recognize(Wave(8)));
    }

    [Fact]
    public void An_open_curve_is_not_a_shape()
    {
        Assert.Null(ShapeRecognizer.Recognize(Arc(new InkPoint(300, 300), 120, 120, 0.5)));
    }

    [Fact]
    public void A_rectangle_tilted_by_30_degrees_is_not_a_shape()
    {
        var angle = Math.PI / 6;
        InkPoint Rotate(double x, double y) => new(
            300 + (x * Math.Cos(angle)) - (y * Math.Sin(angle)),
            300 + (x * Math.Sin(angle)) + (y * Math.Cos(angle)));

        var points = Polygon(Rotate(-200, -100), Rotate(200, -100), Rotate(200, 100), Rotate(-200, 100));

        Assert.Null(ShapeRecognizer.Recognize(points));
    }

    [Fact]
    public void An_empty_list_is_not_a_shape()
    {
        Assert.Null(ShapeRecognizer.Recognize([]));
    }

    [Theory]
    [InlineData('o')]
    [InlineData('a')]
    [InlineData('q')]
    [InlineData('l')]
    [InlineData('1')]
    public void A_letter_shaped_stroke_is_not_a_shape(char letter)
    {
        foreach (var size in new double[] { 20, 28, 40 })
        {
            for (var seed = 0; seed < SeedCount; seed++)
            {
                Assert.Null(ShapeRecognizer.Recognize(Letter(letter, size, 0.5, seed)));
                Assert.Null(ShapeRecognizer.Recognize(Letter(letter, size, 1, seed)));
            }
        }
    }

    [Theory]
    [InlineData(300, 110, true)]
    [InlineData(300, 500, false)]
    [InlineData(1000, 110, false)]
    [InlineData(799, 150, true)]
    [InlineData(800, 150, false)]
    [InlineData(300, 151, false)]
    public void Writing_cadence_needs_a_short_pause_and_a_nearby_stroke(int pauseMilliseconds, double left, bool expected)
    {
        var previous = new StrokeBounds(0, 0, 0, 0);
        var stroke = new StrokeBounds(left, 0, left + 30, 30);

        Assert.Equal(expected, ShapeRecognizer.LooksLikeWriting(TimeSpan.FromMilliseconds(pauseMilliseconds), previous, stroke));
    }

    [Fact]
    public void Writing_cadence_measures_the_gap_diagonally_between_boxes()
    {
        var previous = new StrokeBounds(0, 0, 10, 10);

        // 120 et 120 d'écart sur chaque axe : environ 170 en diagonale.
        Assert.False(ShapeRecognizer.LooksLikeWriting(TimeSpan.FromMilliseconds(300), previous, new StrokeBounds(130, 130, 160, 160)));
    }

    [Fact]
    public void Writing_cadence_sees_overlapping_strokes_as_nearby()
    {
        var previous = new StrokeBounds(0, 0, 100, 100);

        Assert.True(ShapeRecognizer.LooksLikeWriting(TimeSpan.FromMilliseconds(300), previous, new StrokeBounds(50, 50, 400, 400)));
    }

    [Fact]
    public void Stroke_bounds_enclose_every_point()
    {
        Assert.Equal(new StrokeBounds(1, 2, 7, 9), StrokeBounds.Of([new InkPoint(5, 9), new InkPoint(1, 4), new InkPoint(7, 2)]));
    }

    [Theory]
    [InlineData(600, 16, 5)]
    [InlineData(1000, 28, 5)]
    [InlineData(1000, 28, 50)]
    [InlineData(1500, 40, 5)]
    [InlineData(1800, 52, 5)]
    public void A_long_wave_is_not_a_line(double width, double peakToPeak, int periods)
    {
        var count = Math.Max(PointCount, periods * 16);
        var points = new List<InkPoint>();

        for (var i = 0; i < count; i++)
        {
            var t = i / (double)(count - 1);
            points.Add(new InkPoint(100 + (width * t), 400 + (peakToPeak / 2 * Math.Sin(2 * Math.PI * periods * t))));
        }

        Assert.Null(ShapeRecognizer.Recognize(points));
    }

    [Fact]
    public void A_shallow_arc_on_a_long_chord_is_not_a_line()
    {
        const double chord = 800;
        const double bow = 31;
        var radius = ((chord * chord / 4) + (bow * bow)) / (2 * bow);
        var halfAngle = Math.Asin(chord / 2 / radius);
        var points = new List<InkPoint>();

        for (var i = 0; i < PointCount; i++)
        {
            var angle = -halfAngle + (2 * halfAngle * i / (PointCount - 1));
            points.Add(new InkPoint(500 + (radius * Math.Sin(angle)), 400 + (radius * (Math.Cos(angle) - 1))));
        }

        Assert.Null(ShapeRecognizer.Recognize(points));
    }

    [Fact]
    public void An_s_curve_on_a_long_chord_is_not_a_line()
    {
        var points = new List<InkPoint>();

        for (var i = 0; i < PointCount; i++)
        {
            var t = i / (double)(PointCount - 1);
            points.Add(new InkPoint(100 + (800 * t), 400 + (28 * Math.Sin(2 * Math.PI * t))));
        }

        Assert.Null(ShapeRecognizer.Recognize(points));
    }

    [Fact]
    public void A_steady_long_line_stays_a_line()
    {
        for (var seed = 0; seed < SeedCount; seed++)
        {
            var shape = ShapeRecognizer.Recognize(Polyline(0.5, seed, new InkPoint(100, 100), new InkPoint(1600, 400)));

            Assert.NotNull(shape);
            Assert.Equal(DrawingTool.Line, shape.Tool);
        }
    }

    [Theory]
    [InlineData(400, 200, 30, 0.95)]
    [InlineData(300, 300, 40, 0.95)]
    [InlineData(400, 200, 60, 0.92)]
    [InlineData(400, 200, 60, 0.95)]
    [InlineData(400, 200, 60, 1)]
    [InlineData(400, 200, 60, 1.05)]
    [InlineData(300, 300, 60, 0.92)]
    [InlineData(300, 300, 60, 0.95)]
    [InlineData(300, 300, 60, 1)]
    [InlineData(300, 300, 60, 1.05)]
    public void A_rounded_rectangle_is_not_an_ellipse(double width, double height, double radius, double closure)
    {
        foreach (var startPhase in new[] { 0, 0.125, 0.4, 0.6 })
        {
            for (var seed = 0; seed < SeedCount; seed++)
            {
                var shape = ShapeRecognizer.Recognize(RoundedRectangle(width, height, radius, closure, seed, startPhase));

                Assert.NotEqual(DrawingTool.Ellipse, shape?.Tool);
            }
        }
    }

    [Theory]
    [InlineData(1.5, 30)]
    [InlineData(2, 15)]
    [InlineData(1.25, 45)]
    public void A_tilted_ellipse_is_not_a_shape(double aspect, double tiltDegrees)
    {
        for (var seed = 0; seed < SeedCount; seed++)
        {
            Assert.Null(ShapeRecognizer.Recognize(TiltedEllipse(aspect, tiltDegrees, seed)));
        }
    }

    [Theory]
    [InlineData(1.25, 0)]
    [InlineData(2, 5)]
    [InlineData(1.1, 45)]
    public void A_nearly_straight_or_nearly_round_ellipse_stays_an_ellipse(double aspect, double tiltDegrees)
    {
        for (var seed = 0; seed < SeedCount; seed++)
        {
            var shape = ShapeRecognizer.Recognize(TiltedEllipse(aspect, tiltDegrees, seed));

            Assert.NotNull(shape);
            Assert.Equal(DrawingTool.Ellipse, shape.Tool);
        }
    }

    [Theory]
    [InlineData(0.97)]
    [InlineData(1.06)]
    public void A_sharp_rectangle_with_an_imperfect_closure_is_a_rectangle(double closure)
    {
        for (var seed = 0; seed < SeedCount; seed++)
        {
            var shape = ShapeRecognizer.Recognize(RoundedRectangle(400, 200, 0, closure, seed));

            Assert.NotNull(shape);
            Assert.Equal(DrawingTool.Rectangle, shape.Tool);
        }
    }

    [Fact]
    public void A_cross_out_scribble_is_not_a_rectangle()
    {
        InkPoint[] path = [new(100, 100), new(300, 120), new(100, 130), new(300, 110), new(104, 102)];

        Assert.Null(ShapeRecognizer.Recognize(path));
    }

    [Fact]
    public void A_dense_cross_out_scribble_is_not_a_rectangle()
    {
        for (var seed = 0; seed < SeedCount; seed++)
        {
            var points = Polyline(1, seed, new InkPoint(100, 100), new InkPoint(300, 120), new InkPoint(100, 130), new InkPoint(300, 110), new InkPoint(104, 102));

            Assert.Null(ShapeRecognizer.Recognize(points));
        }
    }

    [Fact]
    public void A_rectangle_started_mid_side_is_a_rectangle()
    {
        for (var seed = 0; seed < SeedCount; seed++)
        {
            var points = Polyline(
                1,
                seed,
                new InkPoint(300, 100),
                new InkPoint(500, 100),
                new InkPoint(500, 300),
                new InkPoint(100, 300),
                new InkPoint(100, 100),
                new InkPoint(300, 100));
            var shape = ShapeRecognizer.Recognize(points);

            Assert.NotNull(shape);
            Assert.Equal(DrawingTool.Rectangle, shape.Tool);
            AssertNear(new InkPoint(100, 100), shape.Start, 8);
            AssertNear(new InkPoint(500, 300), shape.End, 8);
        }
    }

    [Theory]
    [InlineData(45)]
    [InlineData(100)]
    [InlineData(200)]
    [InlineData(300)]
    public void A_circle_started_at_a_rotated_angle_is_an_ellipse(double startDegrees)
    {
        for (var seed = 0; seed < SeedCount; seed++)
        {
            var random = new Random(seed);
            var points = new List<InkPoint>();

            for (var i = 0; i < PointCount; i++)
            {
                var angle = (startDegrees * Math.PI / 180) + (2 * Math.PI * i / (PointCount - 1));
                points.Add(Noisy(random, 300 + (120 * Math.Cos(angle)), 300 + (120 * Math.Sin(angle)), 1));
            }

            var shape = ShapeRecognizer.Recognize(points);

            Assert.NotNull(shape);
            Assert.Equal(DrawingTool.Ellipse, shape.Tool);
        }
    }

    [Fact]
    public void An_out_and_back_stroke_is_not_a_shape()
    {
        InkPoint[] exact = [new(100, 100), new(500, 100), new(110, 100)];

        Assert.Null(ShapeRecognizer.Recognize(exact));

        for (var seed = 0; seed < SeedCount; seed++)
        {
            Assert.Null(ShapeRecognizer.Recognize(Polyline(1, seed, new InkPoint(100, 100), new InkPoint(500, 100), new InkPoint(104, 102))));
        }
    }

    [Fact]
    public void A_tight_zigzag_is_rejected_by_its_shape_and_not_by_its_length()
    {
        var corners = new List<InkPoint>();

        for (var i = 0; i <= 8; i++)
        {
            corners.Add(new InkPoint(i % 2 == 0 ? 100 : 400, 100 + (i * 7.5)));
        }

        for (var seed = 0; seed < SeedCount; seed++)
        {
            var points = Polyline(1, seed, corners.ToArray());
            var length = points.Zip(points.Skip(1), (a, b) => Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2))).Sum();
            var width = points.Max(p => p.X) - points.Min(p => p.X);
            var height = points.Max(p => p.Y) - points.Min(p => p.Y);
            var chord = Math.Sqrt(Math.Pow(points[^1].X - points[0].X, 2) + Math.Pow(points[^1].Y - points[0].Y, 2));

            // Le tracé doit franchir le test de longueur et celui de fermeture : seule sa forme peut le refuser.
            Assert.True(length <= 8 * (width + height), $"longueur {length}");
            Assert.True(chord <= 0.15 * length, $"corde {chord}");
            Assert.Null(ShapeRecognizer.Recognize(points));
        }
    }

    [Fact]
    public void A_thin_loop_is_not_a_shape()
    {
        for (var seed = 0; seed < SeedCount; seed++)
        {
            var random = new Random(seed);
            var points = new List<InkPoint>();

            for (var i = 0; i < PointCount; i++)
            {
                var angle = 2 * Math.PI * i / (PointCount - 1);
                points.Add(Noisy(random, 300 + (150 * Math.Cos(angle)), 300 + (6 * Math.Sin(angle)), 1));
            }

            Assert.Null(ShapeRecognizer.Recognize(points));
        }
    }

    private static void AssertNear(InkPoint expected, InkPoint actual, double tolerance)
    {
        Assert.InRange(actual.X, expected.X - tolerance, expected.X + tolerance);
        Assert.InRange(actual.Y, expected.Y - tolerance, expected.Y + tolerance);
    }

    /// <summary>Sinusoïde de 5 périodes sur 400 de large.</summary>
    private static List<InkPoint> Wave(double amplitude)
    {
        var points = new List<InkPoint>();

        for (var i = 0; i < PointCount; i++)
        {
            var t = i / (double)(PointCount - 1);
            points.Add(new InkPoint(100 + (400 * t), 200 + (amplitude * Math.Sin(2 * Math.PI * 5 * t))));
        }

        return points;
    }

    private static InkPoint Noisy(Random random, double x, double y) => new(
        x + (((random.NextDouble() * 2) - 1) * NoiseAmplitude),
        y + (((random.NextDouble() * 2) - 1) * NoiseAmplitude));

    private static List<InkPoint> Segment(InkPoint from, InkPoint to)
    {
        var random = new Random(7);
        var points = new List<InkPoint>();

        for (var i = 0; i < PointCount; i++)
        {
            var t = i / (double)(PointCount - 1);
            points.Add(Noisy(random, from.X + ((to.X - from.X) * t), from.Y + ((to.Y - from.Y) * t)));
        }

        return points;
    }

    /// <summary>Contour fermé, échantillonné à pas régulier le long du périmètre, en repartant du premier sommet.</summary>
    private static List<InkPoint> Polygon(params InkPoint[] corners)
    {
        var random = new Random(7);
        var sides = corners.Length;
        var lengths = new double[sides];
        var perimeter = 0.0;

        for (var s = 0; s < sides; s++)
        {
            var next = corners[(s + 1) % sides];
            lengths[s] = Math.Sqrt(Math.Pow(next.X - corners[s].X, 2) + Math.Pow(next.Y - corners[s].Y, 2));
            perimeter += lengths[s];
        }

        var points = new List<InkPoint>();

        for (var i = 0; i < PointCount; i++)
        {
            var distance = perimeter * i / (PointCount - 1);
            var side = 0;

            while (side < sides - 1 && distance > lengths[side])
            {
                distance -= lengths[side];
                side++;
            }

            var from = corners[side];
            var to = corners[(side + 1) % sides];
            var t = distance / lengths[side];
            points.Add(Noisy(random, from.X + ((to.X - from.X) * t), from.Y + ((to.Y - from.Y) * t)));
        }

        return points;
    }

    private static InkPoint Noisy(Random random, double x, double y, double amplitude) => new(
        x + (((random.NextDouble() * 2) - 1) * amplitude),
        y + (((random.NextDouble() * 2) - 1) * amplitude));

    /// <summary>Ligne brisée échantillonnée tous les 6 DIP environ, avec un bruit choisi.</summary>
    private static List<InkPoint> Polyline(double noise, int seed, params InkPoint[] vertices)
    {
        var random = new Random(seed);
        var points = new List<InkPoint>();

        for (var s = 0; s + 1 < vertices.Length; s++)
        {
            var from = vertices[s];
            var to = vertices[s + 1];
            var length = Math.Sqrt(Math.Pow(to.X - from.X, 2) + Math.Pow(to.Y - from.Y, 2));
            var steps = Math.Max(1, (int)(length / 6));

            for (var i = 0; i < steps; i++)
            {
                var t = i / (double)steps;
                points.Add(Noisy(random, from.X + ((to.X - from.X) * t), from.Y + ((to.Y - from.Y) * t), noise));
            }
        }

        points.Add(Noisy(random, vertices[^1].X, vertices[^1].Y, noise));

        return points;
    }

    /// <summary>Lettre tenant dans un carré de côté <paramref name="size"/>, avec un bruit choisi.</summary>
    private static List<InkPoint> Letter(char letter, double size, double noise, int seed)
    {
        var random = new Random(seed);
        var points = new List<InkPoint>();
        void Add(double x, double y) => points.Add(Noisy(random, 500 + (x * size), 500 + (y * size), noise));
        void Bowl(double centerX, double centerY, double radiusX, double radiusY, double startDegrees)
        {
            for (var i = 0; i < 40; i++)
            {
                var angle = (startDegrees - (360.0 * i / 39)) * Math.PI / 180;
                Add(centerX + (radiusX * Math.Cos(angle)), centerY + (radiusY * Math.Sin(angle)));
            }
        }

        void Stem(double fromX, double fromY, double toX, double toY)
        {
            for (var i = 1; i <= 12; i++)
            {
                Add(fromX + ((toX - fromX) * i / 12), fromY + ((toY - fromY) * i / 12));
            }
        }

        switch (letter)
        {
            case 'o':
                Bowl(0.5, 0.5, 0.5, 0.5, -60);
                break;
            case 'a':
                // Panse puis petite queue : assez courte pour que le tracé paraisse fermé.
                Bowl(0.45, 0.5, 0.45, 0.5, 0);
                Stem(0.9, 0.5, 1, 0.7);
                break;
            case 'q':
                // Panse commencée en bas à droite, puis courte jambe.
                Bowl(0.45, 0.42, 0.45, 0.42, 45);
                Stem(0.77, 0.72, 0.85, 1);
                break;
            case 'l':
                Add(0.05, 0);
                Stem(0.05, 0, 0, 1);
                break;
            default:
                // Le 1 : drapeau à peine marqué, puis la hampe.
                Add(0, 0.04);
                Stem(0, 0.04, 0.03, 0);
                Stem(0.03, 0, 0.03, 1);
                break;
        }

        return points;
    }

    /// <summary>Contour à pas régulier, commençant à une fraction du périmètre ; closure 1 revient au départ, moins laisse un trou, plus déborde.</summary>
    private static List<InkPoint> RoundedRectangle(double width, double height, double radius, double closure, int seed, double startPhase = 0)
    {
        var random = new Random(seed);
        var straightX = width - (2 * radius);
        var straightY = height - (2 * radius);
        var quarter = Math.PI * radius / 2;
        var perimeter = (2 * straightX) + (2 * straightY) + (4 * quarter);
        var points = new List<InkPoint>();
        var count = (int)(perimeter * closure / 6);

        for (var i = 0; i <= count; i++)
        {
            var (x, y) = RoundedOutlineAt(perimeter * (startPhase + (closure * i / count)) % perimeter, straightX, straightY, radius);
            points.Add(Noisy(random, 100 + x, 100 + y, 1));
        }

        return points;
    }

    private static (double X, double Y) RoundedOutlineAt(double distance, double straightX, double straightY, double radius)
    {
        var quarter = Math.PI * radius / 2;
        var right = radius + straightX;
        var bottom = radius + straightY;

        (double, double) Corner(double centerX, double centerY, double fromDegrees, double along)
        {
            var angle = (fromDegrees * Math.PI / 180) + (along / radius);

            return (centerX + (radius * Math.Cos(angle)), centerY + (radius * Math.Sin(angle)));
        }

        if (distance < straightX)
        {
            return (radius + distance, 0);
        }

        distance -= straightX;

        if (distance < quarter)
        {
            return Corner(right, radius, -90, distance);
        }

        distance -= quarter;

        if (distance < straightY)
        {
            return (right + radius, radius + distance);
        }

        distance -= straightY;

        if (distance < quarter)
        {
            return Corner(right, bottom, 0, distance);
        }

        distance -= quarter;

        if (distance < straightX)
        {
            return (right - distance, bottom + radius);
        }

        distance -= straightX;

        if (distance < quarter)
        {
            return Corner(radius, bottom, 90, distance);
        }

        distance -= quarter;

        if (distance < straightY)
        {
            return (0, bottom - distance);
        }

        distance -= straightY;

        return Corner(radius, radius, 180, distance);
    }

    /// <summary>Ellipse fermée de grand rayon 200, inclinée de <paramref name="tiltDegrees"/>, bruitée de 1 DIP au plus.</summary>
    private static List<InkPoint> TiltedEllipse(double aspect, double tiltDegrees, int seed)
    {
        var random = new Random(seed);
        var tilt = tiltDegrees * Math.PI / 180;
        var points = new List<InkPoint>();

        for (var i = 0; i < PointCount; i++)
        {
            var angle = 2 * Math.PI * i / (PointCount - 1);
            var x = 200 * Math.Cos(angle);
            var y = 200 / aspect * Math.Sin(angle);
            points.Add(Noisy(
                random,
                400 + (x * Math.Cos(tilt)) - (y * Math.Sin(tilt)),
                400 + (x * Math.Sin(tilt)) + (y * Math.Cos(tilt)),
                1));
        }

        return points;
    }

    private static List<InkPoint> Arc(InkPoint center, double radiusX, double radiusY, double turns)
    {
        var random = new Random(7);
        var points = new List<InkPoint>();

        for (var i = 0; i < PointCount; i++)
        {
            var angle = 2 * Math.PI * turns * i / (PointCount - 1);
            points.Add(Noisy(random, center.X + (radiusX * Math.Cos(angle)), center.Y + (radiusY * Math.Sin(angle))));
        }

        return points;
    }
}
