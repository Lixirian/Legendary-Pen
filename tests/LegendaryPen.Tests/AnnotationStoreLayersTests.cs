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

    [Fact]
    public void An_appended_layer_with_a_colliding_number_takes_the_next_number_and_keeps_its_id()
    {
        var current = new LayerStack().Capture();
        var extra = new SavedLayer(Guid.NewGuid(), 1, true, [], []);

        var merge = LayerRestoration.MergeLayers(current, [extra]);

        Assert.Equal([current.Layers[0].Id, extra.Id], merge.State.Layers.Select(l => l.Id));
        Assert.Equal([1, 2], merge.State.Layers.Select(l => l.Number));
        Assert.Equal(3, merge.State.NextNumber);
        Assert.Equal(extra.Id, merge.Targets[extra.Id]);
        Assert.NotNull(TryRestore(merge.State));
    }

    [Fact]
    public void Numbers_stay_unique_when_several_appended_layers_collide()
    {
        var stack = new LayerStack();
        stack.Add();
        var current = stack.Capture();
        SavedLayer[] saved =
        [
            new(Guid.NewGuid(), 1, true, [], []),
            new(Guid.NewGuid(), 2, true, [], []),
            new(Guid.NewGuid(), 3, true, [], []),
            new(Guid.NewGuid(), 9, true, [], []),
            new(Guid.NewGuid(), 4, true, [], []),
        ];

        var merge = LayerRestoration.MergeLayers(current, saved);
        var numbers = merge.State.Layers.Select(l => l.Number).ToList();

        Assert.Equal(numbers.Count, numbers.Distinct().Count());
        Assert.True(merge.State.NextNumber > numbers.Max());
        Assert.Contains(9, numbers);
        Assert.All(saved, layer => Assert.Contains(merge.State.Layers, l => l.Id == layer.Id));
        Assert.NotNull(TryRestore(merge.State));
    }

    [Fact]
    public void Merging_never_removes_a_live_layer()
    {
        var stack = new LayerStack();
        stack.Add();
        var live = stack.Layers.Select(l => l.Id).ToList();
        var history = new ActionHistory();
        var ledger = new LayerRestorationLedger();
        ledger.CompleteFirstPass();

        ledger.Place(BoardBackground.Transparent, stack, history, FileWith(new SavedLayer(Guid.NewGuid(), 1, true, [], [Text("b")])));

        Assert.All(live, id => Assert.Contains(stack.Layers, l => l.Id == id));
    }

    [Fact]
    public void A_hot_plugged_file_merges_with_the_live_stack_and_keeps_the_content_drawn_before()
    {
        var board = new Board();
        board.Ledger.CompleteFirstPass();
        var drawnOn = board.Stack.Active.Id;
        board.Draw(drawnOn, "dessin sur A");

        board.Load(FileWith(new SavedLayer(Guid.NewGuid(), 1, true, [], [Text("fichier de B")])));

        Assert.Equal(["dessin sur A", "fichier de B"], board.LiveContent);
        Assert.Equal(drawnOn, board.Stack.Active.Id);
        Assert.Equal([1, 2], board.Stack.Layers.Select(l => l.Number));
        Assert.Equal(3, board.Stack.Capture().NextNumber);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Mixed_files_at_startup_keep_everything_in_both_orders(bool legacyFirst)
    {
        var board = new Board();
        var legacy = new SavedSurface([], [Text("ancien format")]);
        var recent = FileWith(new SavedLayer(Guid.NewGuid(), 1, true, [], [Text("nouveau format")]));

        board.Load(legacyFirst ? legacy : recent);
        board.Load(legacyFirst ? recent : legacy);
        board.Ledger.CompleteFirstPass();

        Assert.Equal(["ancien format", "nouveau format"], board.LiveContent.Order(StringComparer.Ordinal));
        var numbers = board.Stack.Layers.Select(l => l.Number).ToList();
        Assert.Equal(numbers.Count, numbers.Distinct().Count());
    }

    [Fact]
    public void Every_file_marks_its_background_and_only_that_one()
    {
        var ledger = new LayerRestorationLedger();

        ledger.Place(BoardBackground.Whiteboard, new LayerStack(), new ActionHistory(), new SavedSurface([], []));

        Assert.True(ledger.IsRestored(BoardBackground.Whiteboard));
        Assert.False(ledger.IsRestored(BoardBackground.Blackboard));
    }

    [Fact]
    public void The_end_of_the_first_pass_marks_every_background()
    {
        var ledger = new LayerRestorationLedger();

        ledger.CompleteFirstPass();

        Assert.All(Enum.GetValues<BoardBackground>(), background => Assert.True(ledger.IsRestored(background)));
    }

    [Fact]
    public void The_first_file_of_a_pristine_stack_still_restores_it()
    {
        var source = new LayerStack();
        source.Add();
        var state = source.Capture();
        var target = new LayerStack();

        new LayerRestorationLedger().Place(BoardBackground.Transparent, target, new ActionHistory(), FileOf(state));

        AssertSameState(state, target.Capture());
    }

    [Fact]
    public void A_stack_already_changed_is_never_replaced_even_if_no_file_was_read()
    {
        var stack = new LayerStack();
        var drawnOn = stack.Add();
        var file = FileWith(new SavedLayer(Guid.NewGuid(), 1, true, [], []));

        LayerRestoration.RestoreInto(stack, file, stackAlreadyRestored: false);

        Assert.Contains(stack.Layers, l => l.Id == drawnOn.Id);
        Assert.Equal(3, stack.Layers.Count);
    }

    [Fact]
    public void A_merge_that_adds_layers_clears_the_history_of_the_background()
    {
        var board = new Board();
        board.Ledger.CompleteFirstPass();
        board.Draw(board.Stack.Active.Id, "dessin");
        board.Draw(board.Stack.Active.Id, "dessin 2");
        board.History.Undo();
        Assert.True(board.History.CanUndo && board.History.CanRedo);

        board.Load(FileWith(new SavedLayer(Guid.NewGuid(), 1, true, [], [Text("b")])));

        Assert.False(board.History.CanUndo);
        Assert.False(board.History.CanRedo);
    }

    [Fact]
    public void A_merge_that_adds_nothing_keeps_the_history()
    {
        var board = new Board();
        board.Ledger.CompleteFirstPass();
        var known = board.Stack.Active;
        board.Draw(known.Id, "dessin");

        board.Load(FileWith(new SavedLayer(known.Id, known.Number, true, [], [Text("même calque")])));

        Assert.True(board.History.CanUndo);
        Assert.Single(board.Stack.Layers);
        Assert.Equal(["dessin", "même calque"], board.LiveContent);
    }

    [Fact]
    public void An_old_file_on_a_hot_plugged_monitor_keeps_the_stack_and_the_history()
    {
        var board = new Board();
        board.Ledger.CompleteFirstPass();
        var before = board.Stack.Capture();
        board.Draw(before.ActiveId, "dessin");

        board.Load(new SavedSurface([], [Text("ancien")]));

        AssertSameState(before, board.Stack.Capture());
        Assert.True(board.History.CanUndo);
        Assert.Equal(["dessin", "ancien"], board.LiveContent);
    }

    [Fact]
    public void A_layer_beyond_the_limit_on_a_hot_plugged_monitor_joins_the_top_layer()
    {
        var board = new Board();
        board.Ledger.CompleteFirstPass();
        while (board.Stack.CanAdd)
        {
            board.Stack.Add();
        }

        board.Draw(board.Stack.Active.Id, "dessin");

        board.Load(FileWith(new SavedLayer(Guid.NewGuid(), 1, true, [], [Text("de trop")])));

        Assert.Equal(LayerStack.MaxLayers, board.Stack.Layers.Count);
        Assert.Contains("de trop", board.LiveContent);
        Assert.True(board.History.CanUndo);
    }

    [Fact]
    public void A_file_with_missing_layer_fields_is_placed_without_failure()
    {
        Directory.CreateDirectory(_folder);
        var id = Guid.NewGuid();
        File.WriteAllText(
            Path.Combine(_folder, "DISPLAY1-Transparent.json"),
            $"{{\"Strokes\":\"\",\"Texts\":[],\"Layers\":[{{\"Id\":\"{id}\",\"Number\":1,\"IsVisible\":true}}],\"ActiveLayerId\":\"{id}\",\"NextNumber\":2}}");
        var loaded = new AnnotationStore(_folder).Load("DISPLAY1", BoardBackground.Transparent);
        var stack = new LayerStack();

        var targets = new LayerRestorationLedger().Place(BoardBackground.Transparent, stack, new ActionHistory(), loaded!);

        Assert.Equal(id, targets[id]);
        Assert.Equal(id, stack.Active.Id);
    }

    [Fact]
    public void The_original_stack_uses_the_old_format()
    {
        Assert.True(LayerRestoration.UsesLegacyFormat(new LayerStack().Capture()));
    }

    [Fact]
    public void An_added_layer_leaves_the_old_format()
    {
        var stack = new LayerStack();
        stack.Add();

        Assert.False(LayerRestoration.UsesLegacyFormat(stack.Capture()));
    }

    [Fact]
    public void A_single_layer_that_is_not_the_first_leaves_the_old_format()
    {
        var stack = new LayerStack();
        var first = stack.Active;
        stack.Add();
        stack.Remove(first.Id);

        Assert.False(LayerRestoration.UsesLegacyFormat(stack.Capture()));
    }

    [Fact]
    public void Removing_an_added_layer_does_not_go_back_to_the_old_format()
    {
        var stack = new LayerStack();
        stack.Remove(stack.Add().Id);

        // Le numéro suivant garde la trace du calque ajouté : la pile n'est plus exactement celle d'origine.
        Assert.False(LayerRestoration.UsesLegacyFormat(stack.Capture()));
    }

    [Fact]
    public void A_hidden_only_layer_is_not_the_original_stack()
    {
        var hidden = new LayerInfo(Guid.NewGuid(), 1, false);

        Assert.False(LayerRestoration.UsesLegacyFormat(new LayerStackState([hidden], hidden.Id, 2)));
    }

    [Fact]
    public void A_file_in_the_old_format_gives_its_content_to_a_0_8_reader()
    {
        var store = new AnnotationStore(_folder);
        store.Save("DISPLAY1", BoardBackground.Transparent, new SavedSurface([1, 2, 3], [Text("a")]));

        var json = File.ReadAllText(Path.Combine(_folder, "DISPLAY1-Transparent.json"));
        var old = System.Text.Json.JsonSerializer.Deserialize<SurfaceOf08>(json);

        Assert.Equal(new byte[] { 1, 2, 3 }, old?.Strokes);
        Assert.Equal("a", Assert.Single(old!.Texts).Text);
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

    private static SavedSurface FileWith(SavedLayer layer) => new([], [], [layer], layer.Id, layer.Number + 1);

    private static SavedSurface FileOf(LayerStackState state) => new(
        [],
        [],
        state.Layers.Select(l => new SavedLayer(l.Id, l.Number, l.IsVisible, [], [])).ToList(),
        state.ActiveId,
        state.NextNumber);

    // Ce que lisait la version 0.8.
    private sealed record SurfaceOf08(byte[] Strokes, List<SavedText> Texts);

    private sealed class NoAction : IUndoableAction
    {
        public void Undo()
        {
        }

        public void Redo()
        {
        }
    }

    /// <summary>
    /// Un fond et son contenu, comme les surfaces le tiennent : le contenu reste sur son calque, et seul celui des calques
    /// de la pile sera affiché et enregistré. Un contenu hors de la pile serait perdu à la prochaine sauvegarde.
    /// </summary>
    private sealed class Board
    {
        private const BoardBackground Background = BoardBackground.Transparent;

        private readonly Dictionary<Guid, List<string>> _content = [];

        public LayerStack Stack { get; } = new();

        public ActionHistory History { get; } = new();

        public LayerRestorationLedger Ledger { get; } = new();

        public IEnumerable<string> LiveContent => Stack.Layers.SelectMany(layer => _content.GetValueOrDefault(layer.Id, []));

        public void Draw(Guid layer, string stroke)
        {
            Put(layer, stroke);
            History.Record(new NoAction());
        }

        public void Load(SavedSurface file)
        {
            var targets = Ledger.Place(Background, Stack, History, file);

            if (file.Layers is null)
            {
                file.Texts.ForEach(text => Put(Stack.Active.Id, text.Text));

                return;
            }

            foreach (var layer in file.Layers)
            {
                layer.Texts.ForEach(text => Put(targets[layer.Id], text.Text));
            }
        }

        private void Put(Guid layer, string content)
        {
            if (!_content.TryGetValue(layer, out var list))
            {
                list = [];
                _content[layer] = list;
            }

            list.Add(content);
        }
    }
}
