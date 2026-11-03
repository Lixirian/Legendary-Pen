using LegendaryPen.Core;
using Xunit;

namespace LegendaryPen.Tests;

public sealed class AnnotationStoreLayersTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"legendary-pen-layers-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void A_file_from_version_0_6_is_still_read()
    {
        var store = new AnnotationStore(_folder);
        store.Save("DISPLAY1", BoardBackground.Transparent, new SavedSurface([1, 2, 3], [Text("a")]));

        var loaded = store.Load("DISPLAY1", BoardBackground.Transparent);

        Assert.NotNull(loaded);
        Assert.Null(loaded.Layers);
        Assert.Equal(new byte[] { 1, 2, 3 }, loaded.Strokes);
        Assert.Equal("a", Assert.Single(loaded.Texts).Text);
    }

    [Fact]
    public void A_file_written_by_0_8_is_read_with_null_layers()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(
            Path.Combine(_folder, "DISPLAY1-Transparent.json"),
            "{\"Strokes\":\"AQID\",\"Texts\":[{\"Text\":\"bonjour\",\"Left\":1.5,\"Top\":2,\"FontSize\":16,\"FontFamily\":\"Segoe UI\",\"Color\":\"#FF0000\"}]}");

        var loaded = new AnnotationStore(_folder).Load("DISPLAY1", BoardBackground.Transparent);

        Assert.NotNull(loaded);
        Assert.Null(loaded.Layers);
        Assert.Null(loaded.ActiveLayerId);
        Assert.Null(loaded.NextNumber);
        Assert.Equal(new byte[] { 1, 2, 3 }, loaded.Strokes);
        Assert.Equal("bonjour", Assert.Single(loaded.Texts).Text);
    }

    [Fact]
    public void Layers_survive_a_round_trip()
    {
        var first = new SavedLayer(Guid.NewGuid(), 1, true, [1], [Text("un")]);
        var second = new SavedLayer(Guid.NewGuid(), 4, false, [], []);
        var third = new SavedLayer(Guid.NewGuid(), 5, true, [3], []);
        var store = new AnnotationStore(_folder);
        store.Save("DISPLAY1", BoardBackground.Whiteboard, new SavedSurface([], [], [first, second, third]));

        var loaded = store.Load("DISPLAY1", BoardBackground.Whiteboard);

        Assert.NotNull(loaded?.Layers);
        Assert.Equal([first.Id, second.Id, third.Id], loaded.Layers.Select(l => l.Id));
        Assert.Equal([1, 4, 5], loaded.Layers.Select(l => l.Number));
        Assert.Equal([true, false, true], loaded.Layers.Select(l => l.IsVisible));
        Assert.Equal(new byte[] { 1 }, loaded.Layers[0].Strokes);
        Assert.Equal("un", Assert.Single(loaded.Layers[0].Texts).Text);
        Assert.Empty(loaded.Layers[1].Strokes);
    }

    [Fact]
    public void An_active_id_and_next_number_survive_a_round_trip()
    {
        var layer = new SavedLayer(Guid.NewGuid(), 2, true, [], []);
        var store = new AnnotationStore(_folder);
        store.Save("DISPLAY1", BoardBackground.Transparent, new SavedSurface([], [], [layer], layer.Id, 3));

        var loaded = store.Load("DISPLAY1", BoardBackground.Transparent);

        Assert.Equal(layer.Id, loaded?.ActiveLayerId);
        Assert.Equal(3, loaded?.NextNumber);
    }

    [Fact]
    public void A_corrupt_file_still_gives_null()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "DISPLAY1-Transparent.json"), "{ pas du json");

        Assert.Null(new AnnotationStore(_folder).Load("DISPLAY1", BoardBackground.Transparent));
    }

    [Fact]
    public void The_same_layers_on_two_monitors_map_onto_themselves()
    {
        var stack = new LayerStack();
        stack.Add();
        var current = stack.Capture();
        var saved = current.Layers.Select(l => new SavedLayer(l.Id, l.Number, l.IsVisible, [], [])).ToList();

        var merge = LayerRestoration.MergeLayers(current, saved);

        AssertSameState(current, merge.State);
        Assert.All(saved, layer => Assert.Equal(layer.Id, merge.Targets[layer.Id]));
    }

    [Fact]
    public void An_extra_layer_on_the_second_monitor_is_appended_on_top()
    {
        var stack = new LayerStack();
        var current = stack.Capture();
        var known = current.Layers[0];
        var extra = new SavedLayer(Guid.NewGuid(), 5, false, [], []);

        var merge = LayerRestoration.MergeLayers(
            current,
            [new SavedLayer(known.Id, known.Number, true, [], []), extra]);

        Assert.Equal([known.Id, extra.Id], merge.State.Layers.Select(l => l.Id));
        Assert.Equal(5, merge.State.Layers[1].Number);
        Assert.False(merge.State.Layers[1].IsVisible);
        Assert.Equal(current.ActiveId, merge.State.ActiveId);
        Assert.Equal(6, merge.State.NextNumber);
        Assert.Equal(extra.Id, merge.Targets[extra.Id]);
        Assert.NotNull(TryRestore(merge.State));
    }

    [Fact]
    public void Layers_beyond_the_limit_merge_into_the_top_layer()
    {
        var stack = new LayerStack();
        while (stack.CanAdd)
        {
            stack.Add();
        }

        var current = stack.Capture();
        var top = current.Layers[^1];
        var extra = new SavedLayer(Guid.NewGuid(), 20, true, [], []);

        var merge = LayerRestoration.MergeLayers(current, [extra]);

        AssertSameState(current, merge.State);
        Assert.Equal(top.Id, merge.Targets[extra.Id]);
    }

    [Fact]
    public void An_invalid_state_falls_back_to_one_layer_without_losing_content()
    {
        var stack = new LayerStack();
        var before = stack.Capture();
        var layerA = new SavedLayer(Guid.NewGuid(), 1, true, [1], []);
        var layerB = new SavedLayer(Guid.NewGuid(), 2, true, [2], []);

        // Le calque actif annoncé n'existe pas.
        var targets = LayerRestoration.RestoreInto(stack, new SavedSurface([], [], [layerA, layerB], Guid.NewGuid(), 3), stackAlreadyRestored: false);

        AssertSameState(before, stack.Capture());
        Assert.Equal(before.ActiveId, targets[layerA.Id]);
        Assert.Equal(before.ActiveId, targets[layerB.Id]);
    }

    [Fact]
    public void The_first_file_restores_the_stack_and_the_next_one_maps_by_id()
    {
        var source = new LayerStack();
        source.Add();
        source.SetVisible(source.Layers[0].Id, false);
        var state = source.Capture();
        var saved = new SavedSurface(
            [],
            [],
            state.Layers.Select(l => new SavedLayer(l.Id, l.Number, l.IsVisible, [], [])).ToList(),
            state.ActiveId,
            state.NextNumber);

        var target = new LayerStack();
        var first = LayerRestoration.RestoreInto(target, saved, stackAlreadyRestored: false);
        var second = LayerRestoration.RestoreInto(target, saved, stackAlreadyRestored: true);

        AssertSameState(state, target.Capture());
        Assert.Equal(first, second);
        Assert.False(target.Layers[0].IsVisible);
        Assert.Equal(state.ActiveId, target.Active.Id);
    }

    // Les états contiennent des listes : on compare leur contenu, pas leurs références.
    private static void AssertSameState(LayerStackState expected, LayerStackState actual)
    {
        Assert.Equal(expected.Layers, actual.Layers);
        Assert.Equal(expected.ActiveId, actual.ActiveId);
        Assert.Equal(expected.NextNumber, actual.NextNumber);
    }

    private static LayerStack? TryRestore(LayerStackState state)
    {
        var stack = new LayerStack();
        stack.Restore(state);
        return stack;
    }

    private static SavedText Text(string text) => new(text, 0, 0, 16, "Segoe UI", "#000000");
}
