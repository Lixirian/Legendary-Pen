using LegendaryPen.Core;
using Xunit;

namespace LegendaryPen.Tests;

public class ShapeGeometryTests
{
    private static readonly InkPoint Origin = new(100, 100);

    [Fact]
    public void A_line_has_two_points()
    {
        var points = ShapeGeometry.Build(DrawingTool.Line, Origin, new InkPoint(200, 130), false, 5);

        Assert.Equal([Origin, new InkPoint(200, 130)], points);
    }

    [Fact]
    public void A_rectangle_is_closed_and_passes_through_both_corners()
    {
        var end = new InkPoint(300, 200);

        var points = ShapeGeometry.Build(DrawingTool.Rectangle, Origin, end, false, 5);

        Assert.Equal(5, points.Count);
        Assert.Equal(points[0], points[^1]);
        Assert.Contains(end, points);
    }

    [Fact]
    public void An_ellipse_is_closed_and_stays_inside_its_box()
    {
        var points = ShapeGeometry.Build(DrawingTool.Ellipse, Origin, new InkPoint(300, 200), false, 5);

        Assert.Equal(points[0], points[^1]);
        Assert.All(points, point =>
        {
            Assert.InRange(point.X, 100, 300);
            Assert.InRange(point.Y, 100, 200);
        });
        Assert.Equal(300, points.Max(point => point.X), 6);
        Assert.Equal(100, points.Min(point => point.X), 6);
    }

    [Fact]
    public void An_arrow_ends_on_its_tip_with_two_symmetrical_wings()
    {
        var tip = new InkPoint(300, 100);

        var points = ShapeGeometry.Build(DrawingTool.Arrow, Origin, tip, false, 5);

        Assert.Equal(5, points.Count);
        Assert.Equal(tip, points[1]);
        Assert.Equal(tip, points[3]);
        Assert.Equal(points[2].X, points[4].X, 6);
        Assert.Equal(100 - points[2].Y, points[4].Y - 100, 6);
        Assert.True(points[2].X < tip.X);
    }

    [Fact]
    public void A_zero_length_arrow_does_not_produce_invalid_points()
    {
        var points = ShapeGeometry.Build(DrawingTool.Arrow, Origin, Origin, false, 5);

        Assert.All(points, point => Assert.False(double.IsNaN(point.X) || double.IsNaN(point.Y)));
    }

    [Theory]
    [InlineData(200, 110, 0)]
    [InlineData(200, 190, 45)]
    [InlineData(110, 200, 90)]
    [InlineData(10, 110, 180)]
    [InlineData(10, 10, -135)]
    public void Constrained_lines_snap_to_45_degree_steps(double x, double y, double expectedAngle)
    {
        var end = ShapeGeometry.Constrain(DrawingTool.Line, Origin, new InkPoint(x, y));

        var angle = Math.Atan2(end.Y - Origin.Y, end.X - Origin.X) * 180 / Math.PI;
        Assert.Equal(expectedAngle, angle, 6);
    }

    [Theory]
    [InlineData(DrawingTool.Rectangle, 300, 150, 300, 300)]
    [InlineData(DrawingTool.Ellipse, 150, 300, 300, 300)]
    [InlineData(DrawingTool.Rectangle, 0, 50, 0, 0)]
    [InlineData(DrawingTool.Ellipse, 50, 300, -100, 300)]
    public void Constrained_boxes_become_square_in_the_drag_direction(DrawingTool tool, double x, double y, double expectedX, double expectedY)
    {
        Assert.Equal(new InkPoint(expectedX, expectedY), ShapeGeometry.Constrain(tool, Origin, new InkPoint(x, y)));
    }

    [Theory]
    [InlineData(DrawingTool.Pen)]
    [InlineData(DrawingTool.Text)]
    public void Non_shape_tools_are_rejected(DrawingTool tool)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ShapeGeometry.Build(tool, Origin, Origin, false, 5));
    }

    [Theory]
    [InlineData(DrawingTool.Line, true)]
    [InlineData(DrawingTool.Arrow, true)]
    [InlineData(DrawingTool.Rectangle, true)]
    [InlineData(DrawingTool.Ellipse, true)]
    [InlineData(DrawingTool.Pen, false)]
    [InlineData(DrawingTool.Highlighter, false)]
    [InlineData(DrawingTool.Eraser, false)]
    [InlineData(DrawingTool.Text, false)]
    public void Identifies_shapes(DrawingTool tool, bool expected)
    {
        Assert.Equal(expected, tool.IsShape());
    }
}

public class FadeTimingTests
{
    [Fact]
    public void Uses_the_specified_defaults()
    {
        var timing = new FadeTiming();

        Assert.Equal(TimeSpan.FromSeconds(3), timing.Delay);
        Assert.Equal(TimeSpan.FromSeconds(1), timing.Duration);
    }

    [Theory]
    [InlineData(0, 0, 1, 0.2)]
    [InlineData(500, 500, 30, 5)]
    [InlineData(10, 2, 10, 2)]
    public void Stays_within_limits(double delay, double duration, double expectedDelay, double expectedDuration)
    {
        var timing = new FadeTiming(delay, duration);

        Assert.Equal(TimeSpan.FromSeconds(expectedDelay), timing.Delay);
        Assert.Equal(TimeSpan.FromSeconds(expectedDuration), timing.Duration);
    }
}

public class ToolStateTests
{
    [Fact]
    public void Selecting_a_tool_enters_draw_mode()
    {
        var state = new AppState();

        state.SelectTool(DrawingTool.Arrow);

        Assert.Equal(DrawingTool.Arrow, state.Tool);
        Assert.True(state.CanDraw);
    }

    [Fact]
    public void Toggle_returns_to_the_last_tool()
    {
        var state = new AppState();
        state.SelectTool(DrawingTool.Ellipse);
        state.EnterMouse();

        state.ToggleMode();

        Assert.Equal(DrawingTool.Ellipse, state.Tool);
        Assert.True(state.CanDraw);
    }

    [Fact]
    public void Selecting_a_tool_shows_hidden_annotations()
    {
        var state = new AppState();
        state.ToggleAnnotations();

        state.SelectTool(DrawingTool.Text);

        Assert.True(state.AnnotationsVisible);
    }

    [Fact]
    public void A_board_toggles_and_can_be_replaced_by_the_other()
    {
        var state = new AppState();

        state.ToggleBoard(BoardBackground.Whiteboard);
        Assert.Equal(BoardBackground.Whiteboard, state.Background);

        state.ToggleBoard(BoardBackground.Blackboard);
        Assert.Equal(BoardBackground.Blackboard, state.Background);

        state.ToggleBoard(BoardBackground.Blackboard);
        Assert.Equal(BoardBackground.Transparent, state.Background);
    }

    [Fact]
    public void Showing_a_board_makes_annotations_visible_again()
    {
        var state = new AppState();
        state.ToggleAnnotations();

        state.ToggleBoard(BoardBackground.Whiteboard);

        Assert.True(state.AnnotationsVisible);
        Assert.True(state.OverlayAcceptsInput);
    }

    [Fact]
    public void Fading_toggles_without_changing_the_mode()
    {
        var state = new AppState();

        state.ToggleFading();

        Assert.True(state.FadingEnabled);
        Assert.Equal(AppMode.Mouse, state.Mode);
    }
}

public class SurfaceHistoriesTests
{
    private sealed class NoAction : IUndoableAction
    {
        public void Undo()
        {
        }

        public void Redo()
        {
        }
    }

    [Fact]
    public void Each_background_has_its_own_history()
    {
        var state = new AppState();
        var histories = new SurfaceHistories(state);

        histories.Current.Record(new NoAction());
        Assert.True(histories.Current.CanUndo);

        state.ToggleBoard(BoardBackground.Whiteboard);
        Assert.False(histories.Current.CanUndo);

        state.ToggleBoard(BoardBackground.Whiteboard);
        Assert.True(histories.Current.CanUndo);
    }

    [Fact]
    public void Changes_on_any_surface_are_notified()
    {
        var histories = new SurfaceHistories(new AppState());
        var notifications = 0;
        histories.Changed += () => notifications++;

        histories.For(BoardBackground.Blackboard).Record(new NoAction());

        Assert.Equal(1, notifications);
    }
}

public class CursorFileTests
{
    [Fact]
    public void Writes_a_valid_header_and_flips_the_image()
    {
        var bgra = new byte[2 * 2 * 4];
        bgra[0] = 0xAA;
        bgra[8] = 0xBB;

        var file = CursorFile.Build(2, 2, bgra, 1, 0);

        Assert.Equal(2, BitConverter.ToUInt16(file, 2));
        Assert.Equal(1, BitConverter.ToUInt16(file, 4));
        Assert.Equal(1, BitConverter.ToUInt16(file, 10));
        Assert.Equal(0, BitConverter.ToUInt16(file, 12));
        Assert.Equal(22, BitConverter.ToInt32(file, 18));
        Assert.Equal(4, BitConverter.ToInt32(file, 22 + 8));
        Assert.Equal(file.Length - 22, BitConverter.ToInt32(file, 14));
        Assert.Equal(0xBB, file[22 + 40]);
        Assert.Equal(0xAA, file[22 + 40 + 8]);
    }

    [Fact]
    public void Rejects_an_image_of_the_wrong_size()
    {
        Assert.Throws<ArgumentException>(() => CursorFile.Build(2, 2, new byte[3], 0, 0));
    }
}
