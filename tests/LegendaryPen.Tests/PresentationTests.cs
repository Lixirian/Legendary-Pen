using LegendaryPen.Core;
using Xunit;

namespace LegendaryPen.Tests;

public class PresentationTests
{
    private static readonly PixelRect Screen = new(0, 0, 1920, 1080);
    private static readonly PixelRect NegativeScreen = new(-1920, 0, 0, 1080);

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    public void Presentation_runs_only_when_active_and_not_suspended(bool anyActive, bool suspended, bool expected)
    {
        Assert.Equal(expected, PresentationState.ShouldRun(anyActive, suspended));
    }

    [Fact]
    public void Nothing_is_active_at_first()
    {
        var state = new PresentationState();

        Assert.False(state.SpotlightOn);
        Assert.False(state.MagnifierOn);
        Assert.False(state.ClickEffectsOn);
        Assert.False(state.AnyActive);
    }

    [Fact]
    public void Toggling_raises_changed_once()
    {
        var state = new PresentationState();
        var changes = 0;
        state.Changed += () => changes++;

        state.ToggleSpotlight();
        Assert.Equal(1, changes);
        Assert.True(state.SpotlightOn);
        Assert.False(state.MagnifierOn);
        Assert.False(state.ClickEffectsOn);
        Assert.True(state.AnyActive);

        state.ToggleMagnifier();
        Assert.Equal(2, changes);
        Assert.True(state.MagnifierOn);

        state.ToggleClickEffects();
        Assert.Equal(3, changes);
        Assert.True(state.ClickEffectsOn);

        state.ToggleSpotlight();
        state.ToggleMagnifier();
        state.ToggleClickEffects();
        Assert.Equal(6, changes);
        Assert.False(state.AnyActive);
    }

    [Fact]
    public void The_magnifier_stays_inside_the_screen()
    {
        var topLeft = PresentationGeometry.MagnifierWindow(new PixelPoint(5, 5), 320, Screen);
        var bottomRight = PresentationGeometry.MagnifierWindow(new PixelPoint(1919, 1079), 320, Screen);

        Assert.Equal(new PixelRect(0, 0, 320, 320), topLeft);
        Assert.Equal(new PixelRect(1600, 760, 1920, 1080), bottomRight);
    }

    [Fact]
    public void The_magnifier_is_centered_on_the_pointer_away_from_the_edges()
    {
        var box = PresentationGeometry.MagnifierWindow(new PixelPoint(960, 540), 320, Screen);

        Assert.Equal(new PixelRect(800, 380, 1120, 700), box);
    }

    [Fact]
    public void The_magnifier_works_on_a_negative_screen()
    {
        var centered = PresentationGeometry.MagnifierWindow(new PixelPoint(-960, 540), 320, NegativeScreen);
        var atTheEdge = PresentationGeometry.MagnifierWindow(new PixelPoint(-1915, 5), 320, NegativeScreen);

        Assert.Equal(new PixelRect(-1120, 380, -800, 700), centered);
        Assert.Equal(new PixelRect(-1920, 0, -1600, 320), atTheEdge);
    }

    [Fact]
    public void The_magnifier_source_is_centered_on_the_pointer()
    {
        var source = PresentationGeometry.MagnifierSource(new PixelPoint(960, 540), 320, 2, Screen);

        Assert.Equal(new PixelRect(880, 460, 1040, 620), source);
    }

    [Fact]
    public void The_source_stays_inside_the_screen_at_a_corner()
    {
        var topLeft = PresentationGeometry.MagnifierSource(new PixelPoint(5, 5), 320, 2, Screen);
        var bottomRight = PresentationGeometry.MagnifierSource(new PixelPoint(1919, 1079), 320, 2, Screen);
        var negative = PresentationGeometry.MagnifierSource(new PixelPoint(-1, 1079), 320, 2, NegativeScreen);

        Assert.Equal(new PixelRect(0, 0, 160, 160), topLeft);
        Assert.Equal(new PixelRect(1760, 920, 1920, 1080), bottomRight);
        Assert.Equal(new PixelRect(-160, 920, 0, 1080), negative);
    }

    [Fact]
    public void A_ripple_grows_then_vanishes()
    {
        Assert.Equal(new ClickRipple(0, 1), PresentationGeometry.RippleAt(0));
        Assert.Equal(new ClickRipple(24, 0.5), PresentationGeometry.RippleAt(200));
        Assert.Equal(new ClickRipple(48, 0), PresentationGeometry.RippleAt(400));
    }

    [Fact]
    public void A_finished_ripple_stays_invisible()
    {
        Assert.Equal(0, PresentationGeometry.RippleAt(900).Opacity);
    }
}
