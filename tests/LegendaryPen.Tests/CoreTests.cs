using LegendaryPen.Core;
using Xunit;

namespace LegendaryPen.Tests;

public class OverlayInputRulesTests
{
    [Theory]
    [InlineData(true, AppMode.Mouse, BoardBackground.Transparent, false)]
    [InlineData(true, AppMode.Draw, BoardBackground.Transparent, true)]
    [InlineData(true, AppMode.Mouse, BoardBackground.Whiteboard, true)]
    [InlineData(true, AppMode.Mouse, BoardBackground.Blackboard, true)]
    [InlineData(true, AppMode.Draw, BoardBackground.Whiteboard, true)]
    [InlineData(true, AppMode.Draw, BoardBackground.Blackboard, true)]
    [InlineData(false, AppMode.Mouse, BoardBackground.Transparent, false)]
    [InlineData(false, AppMode.Draw, BoardBackground.Transparent, false)]
    public void Matches_the_specification_table(bool visible, AppMode mode, BoardBackground background, bool expected)
    {
        Assert.Equal(expected, OverlayInputRules.AcceptsInput(visible, mode, background));
    }
}

public class AppStateTests
{
    [Fact]
    public void Starts_in_mouse_mode_and_lets_clicks_through()
    {
        var state = new AppState();

        Assert.Equal(AppMode.Mouse, state.Mode);
        Assert.False(state.OverlayAcceptsInput);
    }

    [Fact]
    public void Toggle_switches_between_mouse_and_draw()
    {
        var state = new AppState();

        state.ToggleMode();
        Assert.True(state.CanDraw);

        state.ToggleMode();
        Assert.Equal(AppMode.Mouse, state.Mode);
    }

    [Fact]
    public void Hiding_returns_to_mouse_mode_with_a_transparent_background()
    {
        var state = new AppState();
        state.EnterDraw();
        state.SetBackground(BoardBackground.Whiteboard);

        state.ToggleAnnotations();

        Assert.False(state.AnnotationsVisible);
        Assert.Equal(AppMode.Mouse, state.Mode);
        Assert.Equal(BoardBackground.Transparent, state.Background);
        Assert.False(state.OverlayAcceptsInput);
    }

    [Fact]
    public void Entering_draw_mode_shows_hidden_annotations()
    {
        var state = new AppState();
        state.ToggleAnnotations();

        state.EnterDraw();

        Assert.True(state.AnnotationsVisible);
        Assert.True(state.CanDraw);
    }

    [Fact]
    public void Whiteboard_in_mouse_mode_blocks_clicks_without_drawing()
    {
        var state = new AppState();

        state.SetBackground(BoardBackground.Whiteboard);

        Assert.True(state.OverlayAcceptsInput);
        Assert.False(state.CanDraw);
    }

    [Fact]
    public void Every_change_is_notified()
    {
        var state = new AppState();
        var notifications = 0;
        state.Changed += () => notifications++;

        state.EnterDraw();
        state.EnterMouse();
        state.ToggleAnnotations();
        state.SetBackground(BoardBackground.Blackboard);

        Assert.Equal(4, notifications);
    }
}

public class ActionHistoryTests
{
    private sealed class CountingAction : IUndoableAction
    {
        public int Value { get; private set; } = 1;

        public void Undo() => Value--;

        public void Redo() => Value++;
    }

    [Fact]
    public void Undo_then_redo_restores_the_action()
    {
        var history = new ActionHistory();
        var action = new CountingAction();
        history.Record(action);

        history.Undo();
        Assert.Equal(0, action.Value);
        Assert.False(history.CanUndo);
        Assert.True(history.CanRedo);

        history.Redo();
        Assert.Equal(1, action.Value);
        Assert.True(history.CanUndo);
        Assert.False(history.CanRedo);
    }

    [Fact]
    public void Actions_are_undone_in_reverse_order()
    {
        var history = new ActionHistory();
        var first = new CountingAction();
        var second = new CountingAction();
        history.Record(first);
        history.Record(second);

        history.Undo();

        Assert.Equal(1, first.Value);
        Assert.Equal(0, second.Value);
    }

    [Fact]
    public void A_new_action_discards_the_redo_stack()
    {
        var history = new ActionHistory();
        history.Record(new CountingAction());
        history.Undo();

        history.Record(new CountingAction());

        Assert.False(history.CanRedo);
    }

    [Fact]
    public void Undo_and_redo_do_nothing_on_an_empty_history()
    {
        var history = new ActionHistory();

        history.Undo();
        history.Redo();

        Assert.False(history.CanUndo);
        Assert.False(history.CanRedo);
    }
}

public class ToolbarPlacementTests
{
    private static readonly PixelRect Primary = new(0, 0, 1920, 1080);
    private static readonly PixelRect PrimaryWork = new(0, 0, 1920, 1040);
    private static readonly PixelRect LeftScreen = new(-1600, 0, 0, 900);

    [Fact]
    public void Keeps_a_saved_position_on_a_secondary_screen_with_negative_coordinates()
    {
        var saved = new PixelPoint(-800, 100);

        var result = ToolbarPlacement.Resolve(saved, 300, 50, [Primary, LeftScreen], PrimaryWork);

        Assert.Equal(saved, result);
    }

    [Fact]
    public void Recenters_when_the_saved_screen_has_disappeared()
    {
        var result = ToolbarPlacement.Resolve(new PixelPoint(-800, 100), 300, 50, [Primary], PrimaryWork);

        Assert.Equal(new PixelPoint(810, 16), result);
    }

    [Fact]
    public void Uses_the_default_position_without_a_saved_one()
    {
        var result = ToolbarPlacement.Resolve(null, 300, 50, [Primary], PrimaryWork);

        Assert.Equal(new PixelPoint(810, 16), result);
    }
}

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"legendarypen-tests-{Guid.NewGuid():N}");

    private string SettingsPath => Path.Combine(_directory, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Missing_file_gives_defaults()
    {
        Assert.Equal(new AppSettings(), new SettingsStore(SettingsPath).Load());
    }

    [Fact]
    public void Saved_settings_are_read_back_without_leaving_temporary_files()
    {
        var store = new SettingsStore(SettingsPath);
        var settings = new AppSettings { ToolbarX = -800, ToolbarY = 42 };

        store.Save(settings);

        Assert.Equal(settings, store.Load());
        Assert.Equal([SettingsPath], Directory.GetFiles(_directory));
    }

    [Theory]
    [InlineData("{ pas du json")]
    [InlineData("null")]
    public void Unreadable_file_is_kept_aside_and_replaced_by_defaults(string content)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, content);

        var result = new SettingsStore(SettingsPath).Load();

        Assert.Equal(new AppSettings(), result);
        Assert.False(File.Exists(SettingsPath));
        var kept = Assert.Single(Directory.GetFiles(_directory, "settings.json.illisible-*"));
        Assert.Equal(content, File.ReadAllText(kept));
    }
}
