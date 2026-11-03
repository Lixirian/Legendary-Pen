using LegendaryPen.Core;
using Xunit;

namespace LegendaryPen.Tests;

public class ToolbarDockingTests
{
    private static readonly PixelRect WorkArea = new(0, 0, 1920, 1040);

    [Theory]
    [InlineData(800, 10, ToolbarDock.Top)]
    [InlineData(800, 1030, ToolbarDock.Bottom)]
    [InlineData(5, 300, ToolbarDock.Left)]
    [InlineData(1912, 300, ToolbarDock.Right)]
    [InlineData(800, 500, ToolbarDock.None)]
    public void Detects_the_edge_nearest_to_the_pointer(int x, int y, ToolbarDock expected)
    {
        Assert.Equal(expected, ToolbarDocking.Detect(new PixelPoint(x, y), WorkArea, 48));
    }

    [Fact]
    public void A_pointer_exactly_at_the_threshold_docks_and_one_pixel_further_does_not()
    {
        Assert.Equal(ToolbarDock.Top, ToolbarDocking.Detect(new PixelPoint(800, 48), WorkArea, 48));
        Assert.Equal(ToolbarDock.None, ToolbarDocking.Detect(new PixelPoint(800, 49), WorkArea, 48));
    }

    [Fact]
    public void A_pointer_released_on_the_taskbar_still_docks_to_the_bottom()
    {
        Assert.Equal(ToolbarDock.Bottom, ToolbarDocking.Detect(new PixelPoint(800, 1070), WorkArea, 48));
    }

    [Fact]
    public void The_closest_edge_wins_in_a_corner()
    {
        Assert.Equal(ToolbarDock.Left, ToolbarDocking.Detect(new PixelPoint(5, 30), WorkArea, 48));
    }

    [Fact]
    public void Works_on_a_secondary_screen_with_negative_coordinates()
    {
        var screen = new PixelRect(-1600, 0, 0, 860);

        Assert.Equal(ToolbarDock.Right, ToolbarDocking.Detect(new PixelPoint(-8, 200), screen, 48));
        Assert.Equal(new PixelPoint(-52, 180), ToolbarDocking.Place(ToolbarDock.Right, 52, 500, screen));
    }

    [Theory]
    [InlineData(ToolbarDock.Top, 760, 0)]
    [InlineData(ToolbarDock.Bottom, 760, 940)]
    [InlineData(ToolbarDock.Left, 0, 470)]
    [InlineData(ToolbarDock.Right, 1520, 470)]
    public void Places_the_bar_against_the_edge_and_centered_along_it(ToolbarDock dock, int x, int y)
    {
        Assert.Equal(new PixelPoint(x, y), ToolbarDocking.Place(dock, 400, 100, WorkArea));
    }

    [Fact]
    public void A_free_bar_has_no_docked_position()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ToolbarDocking.Place(ToolbarDock.None, 10, 10, WorkArea));
    }

    [Theory]
    [InlineData(ToolbarDock.Top, true, false)]
    [InlineData(ToolbarDock.Bottom, true, false)]
    [InlineData(ToolbarDock.Left, false, true)]
    [InlineData(ToolbarDock.Right, false, true)]
    [InlineData(ToolbarDock.None, true, true)]
    [InlineData(ToolbarDock.None, false, false)]
    public void Orientation_follows_the_edge_and_a_free_bar_keeps_its_choice(ToolbarDock dock, bool floatingVertical, bool expected)
    {
        Assert.Equal(expected, ToolbarDocking.IsVertical(dock, floatingVertical));
    }

    [Theory]
    [InlineData(ToolbarDock.Top, 6, false, false, false, true)]
    [InlineData(ToolbarDock.Top, 4, false, false, false, false)]
    [InlineData(ToolbarDock.None, 60, false, false, false, false)]
    [InlineData(ToolbarDock.Left, 6, true, false, false, false)]
    [InlineData(ToolbarDock.Left, 6, false, true, false, false)]
    [InlineData(ToolbarDock.Left, 6, false, false, true, false)]
    public void Collapses_only_when_docked_idle_and_not_in_use(
        ToolbarDock dock,
        int idleSeconds,
        bool pointerOver,
        bool dialogOpen,
        bool alreadyCollapsed,
        bool expected)
    {
        var result = ToolbarDocking.ShouldCollapse(
            dock,
            TimeSpan.FromSeconds(idleSeconds),
            TimeSpan.FromSeconds(5),
            pointerOver,
            dialogOpen,
            alreadyCollapsed);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(null, 5)]
    [InlineData(0.5, 2)]
    [InlineData(5000d, 120)]
    [InlineData(30d, 30)]
    public void The_collapse_delay_stays_within_limits(double? seconds, double expected)
    {
        Assert.Equal(TimeSpan.FromSeconds(expected), ToolbarDocking.CollapseDelay(seconds));
    }

    [Fact]
    public void An_unknown_saved_dock_becomes_a_free_bar()
    {
        Assert.Equal(ToolbarDock.None, ToolbarDocking.Sanitize((ToolbarDock)99));
        Assert.Equal(ToolbarDock.Left, ToolbarDocking.Sanitize(ToolbarDock.Left));
    }

    [Fact]
    public void Dock_settings_survive_being_saved_to_disk()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"legendarypen-tests-{Guid.NewGuid():N}");

        try
        {
            var store = new SettingsStore(Path.Combine(directory, "settings.json"));

            store.Save(new AppSettings { ToolbarDock = ToolbarDock.Right, ToolbarDockMonitor = "ecran-2", ToolbarCollapseSeconds = 9 });
            var loaded = store.Load();

            Assert.Equal(ToolbarDock.Right, loaded.ToolbarDock);
            Assert.Equal("ecran-2", loaded.ToolbarDockMonitor);
            Assert.Equal(9, loaded.ToolbarCollapseSeconds);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
