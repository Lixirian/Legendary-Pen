using LegendaryPen.Core;
using Xunit;

namespace LegendaryPen.Tests;

public class HotkeyBindingTests
{
    [Theory]
    [InlineData(HotkeyBinding.Control | HotkeyBinding.Shift, 0x20u, "Ctrl+Maj+Espace")]
    [InlineData(0u, 0x1Bu, "Échap")]
    [InlineData(0u, 'P', "P")]
    [InlineData(HotkeyBinding.Control, 'Z', "Ctrl+Z")]
    [InlineData(HotkeyBinding.Alt | HotkeyBinding.Windows, 0x78u, "Alt+Win+F9")]
    [InlineData(0u, 0x2Eu, "Suppr")]
    [InlineData(0u, 0x61u, "Pavé 1")]
    [InlineData(0u, 0xFFu, "Touche FF")]
    public void Formats_for_display(uint modifiers, uint key, string expected)
    {
        Assert.Equal(expected, new HotkeyBinding(modifiers, key).Format());
    }

    [Fact]
    public void An_empty_binding_is_disabled()
    {
        Assert.True(HotkeyBinding.None.IsNone);
        Assert.Equal("Aucun", HotkeyBinding.None.Format());
    }
}

public class HotkeyMapTests
{
    [Fact]
    public void The_default_shortcuts_are_valid_and_match_the_specification()
    {
        var map = new HotkeyMap();

        Assert.Empty(map.Validate());
        Assert.Equal("Ctrl+Maj+Espace", map[HotkeyAction.ToggleMode].Format());
        Assert.Equal("P", map[HotkeyAction.Pen].Format());
        Assert.Equal("C", map[HotkeyAction.Capture].Format());
    }

    [Fact]
    public void Every_action_has_a_catalog_entry_with_a_label()
    {
        Assert.Equal(Enum.GetValues<HotkeyAction>().Length, HotkeyCatalog.All.Count);
        Assert.All(HotkeyCatalog.All, definition => Assert.False(string.IsNullOrWhiteSpace(definition.Label)));
    }

    [Fact]
    public void Only_the_mode_and_toolbar_shortcuts_stay_active_everywhere()
    {
        var global = HotkeyCatalog.All.Where(definition => definition.IsGlobal).Select(definition => definition.Action);

        Assert.Equal([HotkeyAction.ToggleMode, HotkeyAction.ToggleToolbar], global);
    }

    [Fact]
    public void Saved_shortcuts_replace_defaults_and_unknown_entries_are_ignored()
    {
        var saved = new Dictionary<string, HotkeyBinding>
        {
            ["Pen"] = HotkeyBinding.Letter('K'),
            ["ActionDisparue"] = HotkeyBinding.Letter('Q'),
        };

        var map = new HotkeyMap(saved);

        Assert.Equal("K", map[HotkeyAction.Pen].Format());
        Assert.Equal("H", map[HotkeyAction.Highlighter].Format());
    }

    [Fact]
    public void With_returns_a_copy()
    {
        var original = new HotkeyMap();

        var changed = original.With(HotkeyAction.Pen, HotkeyBinding.Letter('K'));

        Assert.Equal("K", changed[HotkeyAction.Pen].Format());
        Assert.Equal("P", original[HotkeyAction.Pen].Format());
    }

    [Fact]
    public void Reports_a_conflict_between_two_actions()
    {
        var issues = new HotkeyMap().With(HotkeyAction.Pen, HotkeyBinding.Letter('H')).Validate();

        var issue = Assert.Single(issues);
        Assert.Contains("Stylo", issue);
        Assert.Contains("Surligneur", issue);
    }

    [Fact]
    public void Several_disabled_shortcuts_are_not_a_conflict()
    {
        var map = new HotkeyMap()
            .With(HotkeyAction.Pen, HotkeyBinding.None)
            .With(HotkeyAction.Line, HotkeyBinding.None);

        Assert.Empty(map.Validate());
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(HotkeyBinding.Shift)]
    public void A_global_shortcut_needs_a_real_modifier(uint modifiers)
    {
        var issues = new HotkeyMap().With(HotkeyAction.ToggleMode, new HotkeyBinding(modifiers, 'J')).Validate();

        Assert.Contains("permanence", Assert.Single(issues));
    }

    [Fact]
    public void Ctrl_alt_is_refused_because_it_equals_altgr()
    {
        var binding = new HotkeyBinding(HotkeyBinding.Control | HotkeyBinding.Alt, 'J');

        var issues = new HotkeyMap().With(HotkeyAction.Pen, binding).Validate();

        Assert.Contains("AltGr", Assert.Single(issues));
    }

    [Fact]
    public void Shortcuts_survive_being_saved_to_disk()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"legendarypen-tests-{Guid.NewGuid():N}");

        try
        {
            var store = new SettingsStore(Path.Combine(directory, "settings.json"));
            var map = new HotkeyMap()
                .With(HotkeyAction.Pen, new HotkeyBinding(HotkeyBinding.Alt, 'K'))
                .With(HotkeyAction.Line, HotkeyBinding.None);

            store.Save(new AppSettings { Hotkeys = map.ToSettings() });
            var loaded = new HotkeyMap(store.Load().Hotkeys);

            Assert.Equal("Alt+K", loaded[HotkeyAction.Pen].Format());
            Assert.True(loaded[HotkeyAction.Line].IsNone);
            Assert.Equal("H", loaded[HotkeyAction.Highlighter].Format());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

public class CaptureFilesTests
{
    private static readonly DateTime Moment = new(2026, 11, 3, 14, 46, 12);

    [Fact]
    public void The_name_is_timestamped()
    {
        var path = CaptureFiles.UniquePath(@"C:\Captures", Moment, _ => false);

        Assert.Equal(Path.Combine(@"C:\Captures", "Legendary Pen 2026-11-03 14-46-12.png"), path);
    }

    [Fact]
    public void An_existing_file_is_never_overwritten()
    {
        var taken = new HashSet<string>
        {
            Path.Combine(@"C:\Captures", "Legendary Pen 2026-11-03 14-46-12.png"),
            Path.Combine(@"C:\Captures", "Legendary Pen 2026-11-03 14-46-12 (2).png"),
        };

        var path = CaptureFiles.UniquePath(@"C:\Captures", Moment, taken.Contains);

        Assert.Equal(Path.Combine(@"C:\Captures", "Legendary Pen 2026-11-03 14-46-12 (3).png"), path);
    }
}

public class CaptureRegionTests
{
    [Theory]
    [InlineData(500, 500, 500, 500)]
    [InlineData(500, 500, 505, 495)]
    public void A_click_takes_the_whole_screen(int x1, int y1, int x2, int y2)
    {
        var region = CaptureRegion.Resolve(new PixelPoint(x1, y1), new PixelPoint(x2, y2), 1920, 1080);

        Assert.Equal(new PixelRect(0, 0, 1920, 1080), region);
    }

    [Fact]
    public void A_drag_takes_the_area_in_any_direction()
    {
        var region = CaptureRegion.Resolve(new PixelPoint(800, 600), new PixelPoint(200, 100), 1920, 1080);

        Assert.Equal(new PixelRect(200, 100, 800, 600), region);
    }

    [Fact]
    public void A_drag_past_the_edges_stays_on_the_screen()
    {
        var region = CaptureRegion.Resolve(new PixelPoint(-50, -50), new PixelPoint(3000, 2000), 1920, 1080);

        Assert.Equal(new PixelRect(0, 0, 1920, 1080), region);
    }

    [Fact]
    public void A_drag_along_a_line_keeps_a_valid_size()
    {
        var region = CaptureRegion.Resolve(new PixelPoint(100, 500), new PixelPoint(900, 500), 1920, 1080);

        Assert.True(region.Width > 0);
        Assert.True(region.Height > 0);
    }

    [Fact]
    public void A_drag_entirely_off_screen_keeps_a_valid_area()
    {
        var region = CaptureRegion.Resolve(new PixelPoint(2500, 1500), new PixelPoint(3000, 2000), 1920, 1080);

        Assert.True(region.Left >= 0 && region.Right <= 1920 && region.Width > 0);
        Assert.True(region.Top >= 0 && region.Bottom <= 1080 && region.Height > 0);
    }
}

public class LiveOptionsTests
{
    [Fact]
    public void New_settings_have_useful_defaults()
    {
        var settings = new AppSettings();

        Assert.True(settings.WheelChangesSize);
        Assert.True(settings.PressureEnabled);
        Assert.False(settings.BoardOnToolbarScreenOnly);
        Assert.Equal(CaptureDestination.Folder, settings.CaptureDestination);
    }

    [Fact]
    public void Settings_are_applied_and_notified()
    {
        var options = new LiveOptions(new AppSettings());
        var notified = 0;
        options.Changed += () => notified++;

        options.Apply(new AppSettings
        {
            FadeDelaySeconds = 10,
            FadeDurationSeconds = 2,
            WheelChangesSize = false,
            PressureEnabled = false,
            BoardOnToolbarScreenOnly = true,
        });

        Assert.Equal(1, notified);
        Assert.Equal(TimeSpan.FromSeconds(10), options.Fade.Delay);
        Assert.Equal(TimeSpan.FromSeconds(2), options.Fade.Duration);
        Assert.False(options.WheelChangesSize);
        Assert.False(options.PressureEnabled);
        Assert.True(options.BoardOnToolbarScreenOnly);
    }

    [Fact]
    public void Older_settings_files_keep_the_new_defaults()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"legendarypen-tests-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, """{ "Version": 1, "PenSize": 12 }""");

            var loaded = new SettingsStore(path).Load();

            Assert.True(loaded.WheelChangesSize);
            Assert.True(loaded.PressureEnabled);
            Assert.Equal(12, loaded.PenSize);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
