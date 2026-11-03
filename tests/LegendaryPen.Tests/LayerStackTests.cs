using LegendaryPen.Core;
using Xunit;

namespace LegendaryPen.Tests;

public sealed class LayerStackTests : IDisposable
{
    public LayerStackTests() => Loc.Use(AppLanguage.French, "fr");

    public void Dispose() => Loc.Use(AppLanguage.French, "fr");

    [Fact]
    public void A_stack_starts_with_one_visible_active_layer()
    {
        var stack = new LayerStack();

        var only = Assert.Single(stack.Layers);
        Assert.Equal("Calque 1", only.Name);
        Assert.True(only.IsVisible);
        Assert.Equal(only, stack.Active);
        Assert.True(stack.CanAdd);
        Assert.False(stack.CanRemove);
    }

    [Fact]
    public void Adding_selects_the_new_layer_above_the_active_one()
    {
        var stack = new LayerStack();
        var first = stack.Active;
        var second = stack.Add();
        stack.Select(first.Id);

        var inserted = stack.Add();

        Assert.Equal(inserted, stack.Active);
        Assert.Equal([first.Id, inserted.Id, second.Id], stack.Layers.Select(l => l.Id).ToArray());
        Assert.Equal("Calque 3", inserted.Name);
    }

    [Fact]
    public void There_are_at_most_eight_layers()
    {
        var stack = new LayerStack();
        for (var i = 1; i < LayerStack.MaxLayers; i++)
        {
            stack.Add();
        }

        Assert.Equal(8, stack.Layers.Count);
        Assert.False(stack.CanAdd);
        Assert.Throws<InvalidOperationException>(() => stack.Add());
    }

    [Fact]
    public void The_last_layer_cannot_be_removed()
    {
        var stack = new LayerStack();

        Assert.Throws<InvalidOperationException>(() => stack.Remove(stack.Active.Id));
        Assert.Single(stack.Layers);
    }

    [Fact]
    public void Removing_the_active_layer_selects_the_one_below()
    {
        var stack = new LayerStack();
        var bottom = stack.Active;
        var middle = stack.Add();
        stack.Add();
        stack.Select(middle.Id);

        stack.Remove(middle.Id);

        Assert.Equal(bottom, stack.Active);
    }

    [Fact]
    public void Removing_the_bottom_active_layer_selects_the_one_above()
    {
        var stack = new LayerStack();
        var bottom = stack.Active;
        var top = stack.Add();
        stack.Select(bottom.Id);

        stack.Remove(bottom.Id);

        Assert.Equal(top, stack.Active);
    }

    [Fact]
    public void Names_are_not_reused_after_a_removal()
    {
        var stack = new LayerStack();
        stack.Add();
        var third = stack.Add();

        stack.Remove(third.Id);
        var next = stack.Add();

        Assert.Equal("Calque 4", next.Name);
    }

    [Fact]
    public void Hiding_the_active_layer_selects_a_visible_one()
    {
        var stack = new LayerStack();
        var bottom = stack.Active;
        var top = stack.Add();

        stack.SetVisible(top.Id, false);

        Assert.Equal(bottom, stack.Active);
        Assert.False(stack.Layers[1].IsVisible);
    }

    [Fact]
    public void Hiding_the_active_layer_prefers_the_visible_one_below()
    {
        var stack = new LayerStack();
        var bottom = stack.Active;
        var middle = stack.Add();
        var top = stack.Add();
        stack.Select(middle.Id);

        stack.SetVisible(middle.Id, false);

        Assert.Equal(bottom.Id, stack.Active.Id);
        Assert.True(stack.Layers.Single(l => l.Id == top.Id).IsVisible);
    }

    [Fact]
    public void The_last_visible_layer_cannot_be_hidden()
    {
        var stack = new LayerStack();
        var changes = 0;
        stack.Changed += () => changes++;

        stack.SetVisible(stack.Active.Id, false);

        Assert.True(stack.Active.IsVisible);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void Selecting_a_hidden_layer_shows_it()
    {
        var stack = new LayerStack();
        var bottom = stack.Active;
        stack.Add();
        stack.SetVisible(bottom.Id, false);
        var changes = 0;
        stack.Changed += () => changes++;

        stack.Select(bottom.Id);

        Assert.Equal(bottom.Id, stack.Active.Id);
        Assert.True(stack.Active.IsVisible);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void Moving_beyond_the_ends_does_nothing()
    {
        var stack = new LayerStack();
        var bottom = stack.Active;
        var top = stack.Add();
        var changes = 0;
        stack.Changed += () => changes++;

        stack.MoveDown(bottom.Id);
        stack.MoveUp(top.Id);

        Assert.Equal([bottom.Id, top.Id], stack.Layers.Select(l => l.Id).ToArray());
        Assert.Equal(0, changes);
    }

    [Fact]
    public void Moving_swaps_neighbours_and_keeps_ids()
    {
        var stack = new LayerStack();
        var bottom = stack.Active;
        var top = stack.Add();

        stack.MoveUp(bottom.Id);

        Assert.Equal([top.Id, bottom.Id], stack.Layers.Select(l => l.Id).ToArray());
        Assert.Equal(top, stack.Active);
    }

    private static void AssertSameState(LayerStackState expected, LayerStackState actual)
    {
        Assert.Equal(expected.Layers, actual.Layers);
        Assert.Equal(expected.ActiveId, actual.ActiveId);
        Assert.Equal(expected.NextNumber, actual.NextNumber);
    }

    [Fact]
    public void Capture_and_restore_round_trip_with_hidden_layers()
    {
        var stack = new LayerStack();
        var bottom = stack.Active;
        stack.Add();
        stack.Add();
        stack.SetVisible(bottom.Id, false);
        var state = stack.Capture();

        var other = new LayerStack();
        other.Restore(state);

        AssertSameState(state, other.Capture());
        Assert.False(other.Layers[0].IsVisible);
    }

    [Fact]
    public void Capture_returns_a_copy_that_later_changes_do_not_alter()
    {
        var stack = new LayerStack();
        var state = stack.Capture();

        stack.Add();

        Assert.Single(state.Layers);
    }

    [Fact]
    public void The_number_continues_after_a_restore()
    {
        var stack = new LayerStack();
        stack.Add();
        var third = stack.Add();
        stack.Remove(third.Id);
        var state = stack.Capture();

        var other = new LayerStack();
        other.Restore(state);

        Assert.Equal("Calque 4", other.Add().Name);
    }

    [Fact]
    public void A_full_stack_of_eight_can_be_restored()
    {
        var stack = new LayerStack();
        for (var i = 1; i < LayerStack.MaxLayers; i++)
        {
            stack.Add();
        }

        var state = stack.Capture();
        var other = new LayerStack();
        other.Restore(state);

        Assert.Equal(8, other.Layers.Count);
        Assert.False(other.CanAdd);
        AssertSameState(state, other.Capture());
    }

    public static TheoryData<string> InvalidStates() => ["empty", "nine", "duplicate", "unknown-active", "hidden-active"];

    [Theory]
    [MemberData(nameof(InvalidStates))]
    public void Restore_refuses_an_invalid_state(string kind)
    {
        var stack = new LayerStack();
        var before = stack.Capture();
        var a = new LayerInfo(Guid.NewGuid(), 1, true);
        var b = new LayerInfo(Guid.NewGuid(), 2, true);
        var state = kind switch
        {
            "empty" => new LayerStackState([], Guid.NewGuid(), 1),
            "nine" => new LayerStackState(
                Enumerable.Range(1, 9).Select(n => new LayerInfo(Guid.NewGuid(), n, true)).ToList(), a.Id, 10),
            "duplicate" => new LayerStackState([a, a with { Number = 2 }], a.Id, 3),
            "unknown-active" => new LayerStackState([a, b], Guid.NewGuid(), 3),
            _ => new LayerStackState([a with { IsVisible = false }, b], a.Id, 3),
        };

        Assert.Throws<ArgumentException>(() => stack.Restore(state));
        AssertSameState(before, stack.Capture());
    }

    [Fact]
    public void Restore_raises_changed_once()
    {
        var stack = new LayerStack();
        stack.Add();
        var state = stack.Capture();
        var other = new LayerStack();
        var changes = 0;
        other.Changed += () => changes++;

        other.Restore(state);

        Assert.Equal(1, changes);
    }

    [Fact]
    public void Undoing_the_removal_of_the_active_layer_restores_everything()
    {
        var stack = new LayerStack();
        var bottom = stack.Active;
        var middle = stack.Add();
        var top = stack.Add();
        stack.SetVisible(middle.Id, false);
        stack.Select(top.Id);
        var before = stack.Capture();

        stack.Remove(top.Id);
        stack.Restore(before);

        AssertSameState(before, stack.Capture());
        Assert.Equal(top.Id, stack.Active.Id);
        Assert.False(stack.Layers.Single(l => l.Id == middle.Id).IsVisible);
        Assert.Equal(bottom.Id, stack.Layers[0].Id);
    }

    [Fact]
    public void Removing_the_active_layer_shows_a_hidden_neighbor()
    {
        var stack = new LayerStack();
        var bottom = stack.Active;
        var top = stack.Add();
        stack.SetVisible(bottom.Id, false);

        stack.Remove(top.Id);

        Assert.Equal(bottom.Id, stack.Active.Id);
        Assert.True(stack.Active.IsVisible);
    }

    [Fact]
    public void Hiding_the_bottom_active_layer_selects_upward()
    {
        var stack = new LayerStack();
        var bottom = stack.Active;
        var top = stack.Add();
        stack.Select(bottom.Id);

        stack.SetVisible(bottom.Id, false);

        Assert.Equal(top.Id, stack.Active.Id);
    }

    [Fact]
    public void A_name_follows_the_language()
    {
        var stack = new LayerStack();
        Assert.Equal("Calque 1", stack.Active.Name);

        Loc.Use(AppLanguage.English, "en");

        Assert.Equal("Layer 1", stack.Active.Name);
    }

    [Fact]
    public void Each_background_has_its_own_stack()
    {
        var stacks = new LayerStacks();

        stacks.For(BoardBackground.Whiteboard).Add();

        Assert.Equal(2, stacks.For(BoardBackground.Whiteboard).Layers.Count);
        Assert.Single(stacks.For(BoardBackground.Transparent).Layers);
        Assert.Same(stacks.For(BoardBackground.Blackboard), stacks.For(BoardBackground.Blackboard));
    }
}
