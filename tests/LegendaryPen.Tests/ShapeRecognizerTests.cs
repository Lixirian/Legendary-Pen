using LegendaryPen.Core;

namespace LegendaryPen.Tests;

public class ShapeRecognizerTests
{
    private const int PointCount = 80;
    private const double NoiseAmplitude = 2;

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
