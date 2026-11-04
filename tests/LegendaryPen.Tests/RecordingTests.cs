using LegendaryPen.Core;
using Xunit;

namespace LegendaryPen.Tests;

public class RecordingTests
{
    [Fact]
    public void A_recording_goes_through_each_state_in_order()
    {
        var state = new RecordingState();
        var seen = new List<RecordingStatus>();
        state.Changed += () => seen.Add(state.Status);

        Assert.Equal(RecordingStatus.Idle, state.Status);
        Assert.True(state.TryStart());
        state.CountdownElapsed();
        state.RequestStop();
        state.Finished();

        Assert.Equal(
            [RecordingStatus.Countdown, RecordingStatus.Recording, RecordingStatus.Finishing, RecordingStatus.Idle],
            seen);
    }

    [Fact]
    public void A_second_start_is_refused_while_recording()
    {
        var state = new RecordingState();
        state.TryStart();
        state.CountdownElapsed();

        Assert.False(state.TryStart());
        Assert.Equal(RecordingStatus.Recording, state.Status);
    }

    [Fact]
    public void Stopping_during_the_countdown_records_nothing()
    {
        var state = new RecordingState();
        var seen = new List<RecordingStatus>();
        state.Changed += () => seen.Add(state.Status);

        state.TryStart();
        state.RequestStop();

        Assert.Equal(RecordingStatus.Idle, state.Status);
        Assert.DoesNotContain(RecordingStatus.Recording, seen);
    }

    [Fact]
    public void Stopping_twice_does_not_raise_two_changes()
    {
        var state = new RecordingState();
        state.TryStart();
        state.CountdownElapsed();
        var changes = 0;
        state.Changed += () => changes++;

        state.RequestStop();
        state.RequestStop();

        Assert.Equal(1, changes);
        Assert.Equal(RecordingStatus.Finishing, state.Status);
    }

    [Fact]
    public void An_illegal_transition_is_ignored()
    {
        var state = new RecordingState();
        var changes = 0;
        state.Changed += () => changes++;

        state.CountdownElapsed();
        state.RequestStop();
        state.Finished();

        Assert.Equal(0, changes);
        Assert.Equal(RecordingStatus.Idle, state.Status);
    }

    [Fact]
    public void The_file_name_uses_the_date()
    {
        var path = RecordingRules.FilePath(@"C:\Videos", new DateTime(2026, 11, 3, 21, 10, 5), _ => false);

        Assert.Equal(Path.Combine(@"C:\Videos", "Legendary Pen 2026-11-03 21-10-05.mp4"), path);
    }

    [Fact]
    public void An_existing_name_gets_a_number()
    {
        var taken = new HashSet<string>
        {
            Path.Combine(@"C:\Videos", "Legendary Pen 2026-11-03 21-10-05.mp4"),
        };

        var path = RecordingRules.FilePath(@"C:\Videos", new DateTime(2026, 11, 3, 21, 10, 5), taken.Contains);

        Assert.Equal(Path.Combine(@"C:\Videos", "Legendary Pen 2026-11-03 21-10-05 (2).mp4"), path);
    }

    [Theory]
    [InlineData(2560, 1440, 2560, 1440)]
    [InlineData(1921, 1081, 1920, 1080)]
    [InlineData(3841, 2161, 3840, 2160)]
    [InlineData(1, 0, 2, 2)]
    public void The_size_is_even(int width, int height, int expectedWidth, int expectedHeight)
    {
        Assert.Equal((expectedWidth, expectedHeight), RecordingRules.EncodableSize(width, height));
    }

    [Fact]
    public void Recording_stops_under_500_megabytes_free()
    {
        Assert.True(RecordingRules.HasRoomToContinue(RecordingRules.MinFreeBytes));
        Assert.False(RecordingRules.HasRoomToContinue(RecordingRules.MinFreeBytes - 1));
    }

    [Fact]
    public void Audio_is_off_by_default()
    {
        var settings = new AppSettings();

        Assert.Equal(RecordingAudio.None, settings.RecordingAudio);
        Assert.Equal(3, settings.RecordingCountdownSeconds);
    }

    [Fact]
    public void An_old_settings_file_loads_the_recording_defaults()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");

        try
        {
            File.WriteAllText(path, "{ \"Version\": 1, \"PressureEnabled\": false }");

            var settings = new SettingsStore(path).Load();

            Assert.False(settings.PressureEnabled);
            Assert.Equal(RecordingAudio.None, settings.RecordingAudio);
            Assert.Equal(3, settings.RecordingCountdownSeconds);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
