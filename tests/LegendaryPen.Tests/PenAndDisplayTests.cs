using LegendaryPen.Core;
using Xunit;

namespace LegendaryPen.Tests;

public class InkColorTests
{
    [Theory]
    [InlineData("#E53935", 0xE5, 0x39, 0x35)]
    [InlineData("e53935", 0xE5, 0x39, 0x35)]
    [InlineData("  #000000 ", 0, 0, 0)]
    public void Parses_valid_codes(string text, byte r, byte g, byte b)
    {
        Assert.True(InkColor.TryParse(text, out var color));
        Assert.Equal(new InkColor(r, g, b), color);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#12345")]
    [InlineData("#1234567")]
    [InlineData("#GGGGGG")]
    public void Rejects_invalid_codes(string? text)
    {
        Assert.False(InkColor.TryParse(text, out _));
    }

    [Fact]
    public void Formats_as_uppercase_hexadecimal()
    {
        Assert.Equal("#0A0BFF", new InkColor(0x0A, 0x0B, 0xFF).ToHex());
    }

    [Theory]
    [InlineData(0, 1, 1, 255, 0, 0)]
    [InlineData(120, 1, 1, 0, 255, 0)]
    [InlineData(240, 1, 1, 0, 0, 255)]
    [InlineData(360, 1, 1, 255, 0, 0)]
    [InlineData(0, 0, 1, 255, 255, 255)]
    [InlineData(200, 0.5, 0, 0, 0, 0)]
    public void Converts_from_hsv(double h, double s, double v, byte r, byte g, byte b)
    {
        Assert.Equal(new InkColor(r, g, b), InkColor.FromHsv(h, s, v));
    }

    [Fact]
    public void Every_sampled_color_survives_a_round_trip_through_hsv()
    {
        for (var r = 0; r <= 255; r += 15)
        {
            for (var g = 0; g <= 255; g += 15)
            {
                for (var b = 0; b <= 255; b += 15)
                {
                    var color = new InkColor((byte)r, (byte)g, (byte)b);
                    var (hue, saturation, brightness) = color.ToHsv();

                    Assert.Equal(color, InkColor.FromHsv(hue, saturation, brightness));
                }
            }
        }
    }
}

public class PenStateTests
{
    [Fact]
    public void Starts_with_the_default_palette()
    {
        var pen = new PenState();

        Assert.Equal(PenState.DefaultQuickColors, pen.QuickColors);
        Assert.Equal(0, pen.SelectedQuickColor);
        Assert.Equal(PenState.DefaultSize, pen.Size);
    }

    [Fact]
    public void A_custom_color_is_not_a_quick_color()
    {
        var pen = new PenState();

        pen.SetColor(new InkColor(1, 2, 3));

        Assert.Null(pen.SelectedQuickColor);
    }

    [Fact]
    public void Replacing_a_quick_color_selects_it_without_changing_the_defaults()
    {
        var pen = new PenState();
        var color = new InkColor(1, 2, 3);

        pen.ReplaceQuickColor(4, color);

        Assert.Equal(color, pen.QuickColors[4]);
        Assert.Equal(4, pen.SelectedQuickColor);
        Assert.NotEqual(color, PenState.DefaultQuickColors[4]);
    }

    [Theory]
    [InlineData(0, PenState.MinSize)]
    [InlineData(1000, PenState.MaxSize)]
    [InlineData(12, 12)]
    public void Size_stays_within_limits(double requested, double expected)
    {
        var pen = new PenState();

        pen.SetSize(requested);

        Assert.Equal(expected, pen.Size);
    }

    [Fact]
    public void Restores_saved_values()
    {
        var quickColors = new[] { "#000001", "#000002", "#000003", "#000004", "#000005", "#000006" };

        var pen = new PenState(new AppSettings { QuickColors = quickColors, PenColor = "#000003", PenSize = 12 });

        Assert.Equal(new InkColor(0, 0, 6), pen.QuickColors[5]);
        Assert.Equal(2, pen.SelectedQuickColor);
        Assert.Equal(12, pen.Size);
    }

    [Theory]
    [InlineData("pas une couleur", "#000002", "#000003", "#000004", "#000005", "#000006")]
    [InlineData("#000001", "#000002")]
    public void Invalid_saved_palettes_fall_back_to_the_defaults(params string[] quickColors)
    {
        var pen = new PenState(new AppSettings { QuickColors = quickColors });

        Assert.Equal(PenState.DefaultQuickColors, pen.QuickColors);
    }

    [Fact]
    public void Invalid_saved_color_and_size_are_corrected()
    {
        var pen = new PenState(new AppSettings { PenColor = "rouge", PenSize = 5000 });

        Assert.Equal(PenState.DefaultQuickColors[0], pen.Color);
        Assert.Equal(PenState.MaxSize, pen.Size);
    }

    [Fact]
    public void Pen_settings_survive_being_saved_to_disk()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"legendarypen-tests-{Guid.NewGuid():N}");

        try
        {
            var store = new SettingsStore(Path.Combine(directory, "settings.json"));
            var quickColors = new[] { "#000001", "#000002", "#000003", "#000004", "#000005", "#000006" };

            store.Save(new AppSettings { QuickColors = quickColors, PenColor = "#123456", PenSize = 7 });
            var pen = new PenState(store.Load());

            Assert.Equal(new InkColor(0, 0, 1), pen.QuickColors[0]);
            Assert.Equal(new InkColor(0x12, 0x34, 0x56), pen.Color);
            Assert.Equal(7, pen.Size);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

public class DisplayPlanTests
{
    [Fact]
    public void First_start_creates_an_overlay_per_screen()
    {
        var plan = DisplayPlan.Compute([], ["A", "B"]);

        Assert.Equal(["A", "B"], plan.ToCreate);
        Assert.Empty(plan.ToKeep);
        Assert.Empty(plan.ToHide);
    }

    [Fact]
    public void Unplugged_screen_is_hidden_and_not_destroyed()
    {
        var plan = DisplayPlan.Compute(["A", "B"], ["A"]);

        Assert.Empty(plan.ToCreate);
        Assert.Equal(["A"], plan.ToKeep);
        Assert.Equal(["B"], plan.ToHide);
    }

    [Fact]
    public void Screen_plugged_back_in_reuses_its_overlay()
    {
        var plan = DisplayPlan.Compute(["A", "B"], ["A", "B"]);

        Assert.Empty(plan.ToCreate);
        Assert.Equal(["A", "B"], plan.ToKeep);
        Assert.Empty(plan.ToHide);
    }

    [Fact]
    public void New_screen_is_added_while_another_is_removed()
    {
        var plan = DisplayPlan.Compute(["A", "B"], ["A", "C"]);

        Assert.Equal(["C"], plan.ToCreate);
        Assert.Equal(["A"], plan.ToKeep);
        Assert.Equal(["B"], plan.ToHide);
    }
}
