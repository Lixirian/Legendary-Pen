using LegendaryPen.Core;
using Xunit;

namespace LegendaryPen.Tests;

public class PreviousColorTests
{
    private static readonly InkColor Red = new(255, 0, 0);
    private static readonly InkColor Blue = new(0, 0, 255);

    [Fact]
    public void There_is_no_previous_color_at_first()
    {
        var pen = new PenState();

        Assert.Null(pen.PreviousColor);

        var color = pen.Color;
        pen.RestorePreviousColor();
        Assert.Equal(color, pen.Color);
    }

    [Fact]
    public void Restoring_swaps_the_two_last_colors()
    {
        var pen = new PenState();
        pen.SetColor(Red);
        pen.SetColor(Blue);

        pen.RestorePreviousColor();
        Assert.Equal(Red, pen.Color);
        Assert.Equal(Blue, pen.PreviousColor);

        pen.RestorePreviousColor();
        Assert.Equal(Blue, pen.Color);
        Assert.Equal(Red, pen.PreviousColor);
    }

    [Fact]
    public void Selecting_the_same_color_keeps_the_previous_one()
    {
        var pen = new PenState();
        pen.SetColor(Red);
        pen.SetColor(Blue);
        pen.SetColor(Blue);

        Assert.Equal(Red, pen.PreviousColor);
    }

    [Fact]
    public void A_quick_color_counts_as_a_color_change()
    {
        var pen = new PenState();
        var first = pen.Color;

        pen.SelectQuickColor(3);

        Assert.Equal(first, pen.PreviousColor);
    }

    [Fact]
    public void The_previous_color_survives_a_restart()
    {
        var pen = new PenState(new AppSettings { PenColor = Red.ToHex(), PreviousPenColor = Blue.ToHex() });

        Assert.Equal(Blue, pen.PreviousColor);
    }

    [Fact]
    public void An_invalid_saved_previous_color_is_ignored()
    {
        Assert.Null(new PenState(new AppSettings { PreviousPenColor = "pas une couleur" }).PreviousColor);
    }

    [Fact]
    public void It_has_a_shortcut_that_conflicts_with_nothing()
    {
        Assert.Contains(HotkeyCatalog.All, definition => definition.Action == HotkeyAction.PreviousColor);

        var bindings = HotkeyCatalog.All.Select(definition => definition.Default).ToList();
        Assert.Equal(bindings.Count, bindings.Distinct().Count());
    }
}

public class SizePerToolTests
{
    [Fact]
    public void By_default_every_tool_shares_the_same_size()
    {
        var pen = new PenState();
        pen.SetSize(12);

        pen.ActivateTool(DrawingTool.Highlighter);

        Assert.Equal(12, pen.Size);
    }

    [Fact]
    public void With_the_option_each_tool_comes_back_with_its_own_size()
    {
        var pen = new PenState();
        pen.SetRememberSizePerTool(true);
        pen.SetSize(3);

        pen.ActivateTool(DrawingTool.Highlighter);
        pen.SetSize(20);

        pen.ActivateTool(DrawingTool.Pen);
        Assert.Equal(3, pen.Size);

        pen.ActivateTool(DrawingTool.Highlighter);
        Assert.Equal(20, pen.Size);
    }

    [Fact]
    public void A_tool_used_for_the_first_time_starts_from_the_current_size()
    {
        var pen = new PenState();
        pen.SetRememberSizePerTool(true);
        pen.SetSize(9);

        pen.ActivateTool(DrawingTool.Line);

        Assert.Equal(9, pen.Size);
    }

    [Fact]
    public void Changing_tool_notifies_only_when_the_size_changes()
    {
        var pen = new PenState();
        pen.SetRememberSizePerTool(true);
        pen.SetSize(3);
        pen.ActivateTool(DrawingTool.Line);
        pen.SetSize(8);

        var notified = 0;
        pen.Changed += () => notified++;

        pen.ActivateTool(DrawingTool.Line);
        Assert.Equal(0, notified);

        pen.ActivateTool(DrawingTool.Pen);
        Assert.Equal(1, notified);
    }

    [Fact]
    public void Sizes_survive_a_restart()
    {
        var first = new PenState();
        first.SetRememberSizePerTool(true);
        first.SetSize(4);
        first.ActivateTool(DrawingTool.Highlighter);
        first.SetSize(25);

        var second = new PenState(new AppSettings
        {
            SizePerToolEnabled = true,
            PenSize = first.Size,
            ToolSizes = first.ToolSizes,
        });

        Assert.Equal(4, second.Size);

        second.ActivateTool(DrawingTool.Highlighter);
        Assert.Equal(25, second.Size);
    }

    [Fact]
    public void Invalid_saved_sizes_are_ignored_or_limited()
    {
        var pen = new PenState(new AppSettings
        {
            SizePerToolEnabled = true,
            PenSize = 6,
            ToolSizes = new Dictionary<string, double> { ["OutilInconnu"] = 5, ["Line"] = 5000 },
        });

        Assert.Equal(6, pen.Size);

        pen.ActivateTool(DrawingTool.Line);
        Assert.Equal(PenState.MaxSize, pen.Size);
    }

    [Fact]
    public void Turning_the_option_off_keeps_the_current_size()
    {
        var pen = new PenState();
        pen.SetRememberSizePerTool(true);
        pen.SetSize(7);
        pen.SetRememberSizePerTool(false);

        pen.ActivateTool(DrawingTool.Arrow);

        Assert.Equal(7, pen.Size);
    }
}

public sealed class AnnotationStoreTests : IDisposable
{
    private const string Monitor = @"\\.\DISPLAY1";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"lp-annotations-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static SavedSurface Sample => new([1, 2, 3], [new SavedText("Note", 10, 20, 16, "Segoe UI", "#FF0000")]);

    [Fact]
    public void A_saved_surface_comes_back_identical()
    {
        var store = new AnnotationStore(_folder);

        store.Save(Monitor, BoardBackground.Whiteboard, Sample);
        var loaded = store.Load(Monitor, BoardBackground.Whiteboard)!;

        Assert.Equal(Sample.Strokes, loaded.Strokes);
        Assert.Equal(Sample.Texts, loaded.Texts);
    }

    [Fact]
    public void Screens_and_backgrounds_are_kept_apart()
    {
        var store = new AnnotationStore(_folder);

        store.Save(Monitor, BoardBackground.Whiteboard, Sample);

        Assert.Null(store.Load(Monitor, BoardBackground.Blackboard));
        Assert.Null(store.Load(@"\\.\DISPLAY2", BoardBackground.Whiteboard));
    }

    [Fact]
    public void Nothing_saved_means_nothing_loaded()
    {
        Assert.Null(new AnnotationStore(_folder).Load(Monitor, BoardBackground.Transparent));
    }

    [Fact]
    public void An_unreadable_file_is_ignored()
    {
        var store = new AnnotationStore(_folder);
        store.Save(Monitor, BoardBackground.Transparent, Sample);
        File.WriteAllText(Directory.GetFiles(_folder).Single(), "{ abîmé");

        Assert.Null(store.Load(Monitor, BoardBackground.Transparent));
    }

    [Fact]
    public void Delete_removes_one_surface()
    {
        var store = new AnnotationStore(_folder);
        store.Save(Monitor, BoardBackground.Whiteboard, Sample);
        store.Save(Monitor, BoardBackground.Blackboard, Sample);

        store.Delete(Monitor, BoardBackground.Whiteboard);

        Assert.Null(store.Load(Monitor, BoardBackground.Whiteboard));
        Assert.NotNull(store.Load(Monitor, BoardBackground.Blackboard));
    }

    [Fact]
    public void Deleting_before_anything_was_saved_is_harmless()
    {
        new AnnotationStore(_folder).Delete(Monitor, BoardBackground.Whiteboard);

        Assert.False(Directory.Exists(_folder));
    }

    [Fact]
    public void Delete_all_removes_everything_and_tolerates_an_empty_store()
    {
        var store = new AnnotationStore(_folder);
        store.DeleteAll();
        store.Save(Monitor, BoardBackground.Whiteboard, Sample);

        store.DeleteAll();

        Assert.False(Directory.Exists(_folder));
    }

    [Fact]
    public void No_temporary_file_is_left_behind()
    {
        new AnnotationStore(_folder).Save(Monitor, BoardBackground.Whiteboard, Sample);

        Assert.Single(Directory.GetFiles(_folder));
    }

    [Fact]
    public void The_option_is_off_by_default_and_applied_live()
    {
        var options = new LiveOptions(new AppSettings());
        Assert.False(options.KeepAnnotations);

        options.Apply(new AppSettings { KeepAnnotations = true });
        Assert.True(options.KeepAnnotations);
    }
}
