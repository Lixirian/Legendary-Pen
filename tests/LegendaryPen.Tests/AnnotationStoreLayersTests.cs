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
    public void A_damaged_text_gets_safe_values_and_keeps_the_rest()
    {
        var repaired = new SavedText(null!, 10, 20, 24, null!, null!).Repaired();

        Assert.Equal(string.Empty, repaired.Text);
        Assert.Equal(10, repaired.Left);
        Assert.Equal(20, repaired.Top);
        Assert.Equal(24, repaired.FontSize);
        Assert.Equal(SavedText.DefaultFontFamily, repaired.FontFamily);
        Assert.Equal(string.Empty, repaired.Color);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(1_000_000)]
    public void A_text_size_the_display_would_refuse_is_replaced(double size)
    {
        Assert.Equal(SavedText.DefaultFontSize, new SavedText("a", 0, 0, size, "Segoe UI", "#000000").Repaired().FontSize);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void A_text_position_the_display_would_refuse_is_replaced(double position)
    {
        var repaired = new SavedText("a", position, position, 16, "Segoe UI", "#000000").Repaired();

        Assert.Equal(0, repaired.Left);
        Assert.Equal(0, repaired.Top);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_font_is_replaced(string font)
    {
        Assert.Equal(SavedText.DefaultFontFamily, new SavedText("a", 0, 0, 16, font, "#000000").Repaired().FontFamily);
    }

    [Fact]
    public void A_healthy_text_comes_back_unchanged()
    {
        var text = new SavedText("Note", 10, 20, 16, "Arial", "#FF0000");

        Assert.Equal(text, text.Repaired());
    }

    [Fact]
    public void A_null_saved_layer_does_not_stop_the_restore()
    {
        var stack = new LayerStack();
        var good = new SavedLayer(Guid.NewGuid(), 2, true, [], []);
        var saved = new SavedSurface([], [], [null!, good], good.Id, 3);

        var targets = LayerRestoration.RestoreInto(stack, saved, stackAlreadyRestored: false);

        Assert.Equal(good.Id, stack.Active.Id);
        Assert.Equal(good.Id, targets[good.Id]);
    }

    [Fact]
    public void A_file_whose_layers_share_a_number_keeps_both_layers_with_their_visibility()
    {
        var stack = new LayerStack();
        var a = new SavedLayer(Guid.NewGuid(), 2, true, [], []);
        var hidden = new SavedLayer(Guid.NewGuid(), 2, false, [], []);

        var targets = LayerRestoration.RestoreInto(stack, new SavedSurface([], [], [a, hidden], a.Id, 3), stackAlreadyRestored: false);

        Assert.Equal(new[] { a.Id, hidden.Id }, stack.Layers.Select(layer => layer.Id));
        Assert.False(stack.Layers[1].IsVisible);
        Assert.NotEqual(stack.Layers[0].Number, stack.Layers[1].Number);
        Assert.Equal(hidden.Id, targets[hidden.Id]);
    }

    [Fact]
    public void A_repeated_layer_id_joins_its_first_layer()
    {
        var stack = new LayerStack();
        var id = Guid.NewGuid();
        var saved = new SavedSurface([], [], [new SavedLayer(id, 1, true, [1], []), new SavedLayer(id, 2, false, [2], [])], id, 3);

        var targets = LayerRestoration.RestoreInto(stack, saved, stackAlreadyRestored: false);

        Assert.Equal(id, Assert.Single(stack.Layers).Id);
        Assert.True(stack.Layers[0].IsVisible);
        Assert.Equal(id, targets[id]);
    }

    [Fact]
    public void Layers_past_the_limit_join_a_kept_layer_of_the_same_visibility()
    {
        var stack = new LayerStack();
        var layers = Enumerable.Range(1, LayerStack.MaxLayers)
            .Select(number => new SavedLayer(Guid.NewGuid(), number, number != 3, [], []))
            .ToList();
        var extraHidden = new SavedLayer(Guid.NewGuid(), 20, false, [], []);
        var extraVisible = new SavedLayer(Guid.NewGuid(), 21, true, [], []);
        layers.AddRange([extraHidden, extraVisible]);

        var targets = LayerRestoration.RestoreInto(stack, new SavedSurface([], [], layers, layers[0].Id, 22), stackAlreadyRestored: false);

        Assert.Equal(LayerStack.MaxLayers, stack.Layers.Count);
        Assert.Equal(layers[2].Id, targets[extraHidden.Id]);
        Assert.Equal(layers[LayerStack.MaxLayers - 1].Id, targets[extraVisible.Id]);
    }

    [Fact]
    public void A_file_with_every_layer_hidden_shows_only_its_active_layer()
    {
        var stack = new LayerStack();
        var a = new SavedLayer(Guid.NewGuid(), 1, false, [], []);
        var b = new SavedLayer(Guid.NewGuid(), 2, false, [], []);

        LayerRestoration.RestoreInto(stack, new SavedSurface([], [], [a, b], a.Id, 3), stackAlreadyRestored: false);

        Assert.Equal(a.Id, stack.Active.Id);
        Assert.True(stack.Layers[0].IsVisible);
        Assert.False(stack.Layers[1].IsVisible);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-4, null)]
    [InlineData(int.MaxValue, int.MinValue)]
    public void Unusable_numbers_are_replaced_and_never_reused(int number, int? next)
    {
        var stack = new LayerStack();
        var a = new SavedLayer(Guid.NewGuid(), number, true, [], []);
        var b = new SavedLayer(Guid.NewGuid(), 5, true, [], []);

        LayerRestoration.RestoreInto(stack, new SavedSurface([], [], [a, b], a.Id, next), stackAlreadyRestored: false);

        var state = stack.Capture();
        Assert.All(state.Layers, layer => Assert.InRange(layer.Number, 1, state.NextNumber - 1));
        Assert.Equal(2, state.Layers.Select(layer => layer.Number).Distinct().Count());
    }

    [Fact]
    public void A_file_without_any_layer_leaves_the_stack_alone()
    {
        var stack = new LayerStack();
        var before = stack.Capture();

        var targets = LayerRestoration.RestoreInto(stack, new SavedSurface([], [], [], null, null), stackAlreadyRestored: false);

        AssertSameState(before, stack.Capture());
        Assert.Empty(targets);
    }

    [Fact]
    public void The_panel_goes_below_a_horizontal_bar_when_it_fits()
    {
        var place = LayersPanelPlacement.Beside(new PixelRect(100, 0, 500, 50), false, new PixelRect(0, 0, 1920, 1040), 300, 400);

        Assert.Equal(new LayersPanelPlace(100, 50, null), place);
    }

    [Fact]
    public void The_panel_goes_above_a_horizontal_bar_at_the_bottom()
    {
        var place = LayersPanelPlacement.Beside(new PixelRect(100, 990, 500, 1040), false, new PixelRect(0, 0, 1920, 1040), 300, 400);

        Assert.Equal(new LayersPanelPlace(100, 590, null), place);
    }

    [Fact]
    public void The_panel_goes_beside_a_horizontal_bar_rather_than_being_cut_short()
    {
        var place = LayersPanelPlacement.Beside(new PixelRect(100, 300, 500, 350), false, new PixelRect(0, 0, 1920, 600), 300, 400);

        Assert.Equal(new LayersPanelPlace(500, 200, null), place);
    }

    [Fact]
    public void The_panel_is_capped_when_the_bar_fills_the_width()
    {
        var place = LayersPanelPlacement.Beside(new PixelRect(0, 300, 1920, 350), false, new PixelRect(0, 0, 1920, 600), 300, 400);

        Assert.Equal(new LayersPanelPlace(0, 0, 300), place);
    }

    [Fact]
    public void The_panel_does_not_cover_a_bar_that_leaves_room_on_a_side()
    {
        var area = new PixelRect(0, 0, 1920, 300);

        var place = LayersPanelPlacement.Beside(new PixelRect(100, 0, 500, 300), false, area, 300, 400);

        Assert.Equal(new LayersPanelPlace(500, 0, 300), place);
    }

    [Fact]
    public void The_panel_goes_on_the_left_when_only_the_left_has_room()
    {
        var place = LayersPanelPlacement.Beside(new PixelRect(1500, 0, 1920, 300), false, new PixelRect(0, 0, 1920, 300), 300, 400);

        Assert.Equal(new LayersPanelPlace(1200, 0, 300), place);
    }

    [Fact]
    public void A_vertical_bar_too_wide_for_a_side_sends_the_panel_above_or_below()
    {
        var place = LayersPanelPlacement.Beside(new PixelRect(0, 100, 1000, 500), true, new PixelRect(0, 0, 1000, 1000), 300, 400);

        Assert.Equal(new LayersPanelPlace(0, 500, null), place);
    }

    [Fact]
    public void The_panel_stays_in_the_area_when_the_bar_fills_it_entirely()
    {
        var area = new PixelRect(0, 0, 1000, 300);

        var place = LayersPanelPlacement.Beside(area, false, area, 300, 400);

        Assert.Equal(new LayersPanelPlace(0, 0, 300), place);
    }

    [Fact]
    public void Notifications_stack_up_from_the_corner_with_the_newest_at_the_bottom()
    {
        var places = NotificationPlacement.Stack(new PixelRect(0, 0, 1920, 1040), [(300, 60), (340, 80)], 10);

        Assert.Equal(new[] { new PixelPoint(1610, 880), new PixelPoint(1570, 950) }, places);
    }

    [Fact]
    public void The_second_row_stays_away_from_the_edge_a_bar_is_attached_to()
    {
        Assert.True(ToolbarDocking.SecondRowLeads(ToolbarDock.Bottom));
        Assert.True(ToolbarDocking.SecondRowLeads(ToolbarDock.Right));
        Assert.False(ToolbarDocking.SecondRowLeads(ToolbarDock.Top));
        Assert.False(ToolbarDocking.SecondRowLeads(ToolbarDock.Left));
        Assert.False(ToolbarDocking.SecondRowLeads(ToolbarDock.None));
    }

    [Fact]
    public void The_second_row_is_shown_unless_the_settings_say_otherwise()
    {
        Assert.True(new AppSettings().ToolbarSecondRowVisible);
        Assert.True(System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{}")!.ToolbarSecondRowVisible);
        Assert.False(System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"ToolbarSecondRowVisible\":false}")!.ToolbarSecondRowVisible);
    }

    [Fact]
    public void The_panel_stays_in_the_area_when_the_bar_is_outside_it()
    {
        var area = new PixelRect(0, 0, 1920, 300);

        var place = LayersPanelPlacement.Beside(new PixelRect(100, 400, 500, 450), false, area, 300, 200);

        Assert.InRange(place.Y, area.Top, area.Bottom - 200);
    }

    [Fact]
    public void An_area_too_low_for_the_panel_beside_a_vertical_bar_caps_it()
    {
        var place = LayersPanelPlacement.Beside(new PixelRect(0, 50, 50, 550), true, new PixelRect(0, 0, 1920, 300), 300, 400);

        Assert.Equal(new LayersPanelPlace(50, 0, 300), place);
    }

    [Fact]
    public void The_panel_goes_left_of_a_vertical_bar_on_the_right_edge()
    {
        var place = LayersPanelPlacement.Beside(new PixelRect(1870, 100, 1920, 600), true, new PixelRect(0, 0, 1920, 1040), 300, 400);

        Assert.Equal(new LayersPanelPlace(1570, 100, null), place);
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
    public void An_unknown_active_layer_keeps_every_layer_and_picks_the_top_visible_one()
    {
        var stack = new LayerStack();
        var layerA = new SavedLayer(Guid.NewGuid(), 1, true, [1], []);
        var layerB = new SavedLayer(Guid.NewGuid(), 2, true, [2], []);
        var hidden = new SavedLayer(Guid.NewGuid(), 3, false, [3], []);

        // Le calque actif annoncé n'existe pas.
        var targets = LayerRestoration.RestoreInto(stack, new SavedSurface([], [], [layerA, layerB, hidden], Guid.NewGuid(), 4), stackAlreadyRestored: false);

        Assert.Equal(3, stack.Layers.Count);
        Assert.Equal(layerB.Id, stack.Active.Id);
        Assert.False(stack.Layers[2].IsVisible);
        Assert.Equal(hidden.Id, targets[hidden.Id]);
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
