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

    [Theory]
    [InlineData(2560, 1440, 8_000_000)]
    [InlineData(1920, 1080, 4_500_000)]
    [InlineData(3840, 2160, 18_000_000)]
    [InlineData(1280, 720, 2_000_000)]
    [InlineData(640, 480, 2_000_000)]
    [InlineData(7680, 4320, 40_000_000)]
    public void The_bit_rate_follows_the_area_within_its_limits(int width, int height, int expected)
    {
        Assert.Equal(expected, RecordingRules.VideoBitRate(width, height));
    }

    private static byte[] Box(string name, int payload) =>
        [.. BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(payload + 8)), .. System.Text.Encoding.ASCII.GetBytes(name), .. new byte[payload]];

    private static bool IsComplete(params byte[][] boxes) => RecordingRules.IsCompleteMp4(new MemoryStream(boxes.SelectMany(box => box).ToArray()));

    [Fact]
    public void A_file_with_its_index_is_a_complete_video()
    {
        Assert.True(IsComplete(Box("ftyp", 16), Box("mdat", 100), Box("moov", 40)));
    }

    [Fact]
    public void A_file_without_index_is_not_a_complete_video()
    {
        Assert.False(IsComplete(Box("ftyp", 16), Box("mdat", 100)));
        Assert.False(IsComplete([]));
    }

    [Fact]
    public void A_cut_file_is_not_a_complete_video()
    {
        var cut = Box("moov", 40)[..30];

        Assert.False(IsComplete(Box("ftyp", 16), cut));
        Assert.False(IsComplete(Box("moov", 40), [0, 0, 1]));
    }

    [Fact]
    public void A_box_with_a_wrong_size_is_not_a_complete_video()
    {
        Assert.False(IsComplete(Box("moov", 40), [0, 0, 0, 4, 0x66, 0x72, 0x65, 0x65]));
    }

    [Fact]
    public void An_open_ended_data_box_hides_the_index()
    {
        byte[] openEnded = [0, 0, 0, 0, .. System.Text.Encoding.ASCII.GetBytes("mdat"), .. Box("moov", 40)];

        Assert.False(IsComplete(Box("ftyp", 16), openEnded));
    }

    [Fact]
    public void A_large_data_box_is_read_with_its_long_size()
    {
        byte[] large = [0, 0, 0, 1, .. System.Text.Encoding.ASCII.GetBytes("mdat"), 0, 0, 0, 0, 0, 0, 0, 26, .. new byte[10]];

        Assert.True(IsComplete(large, Box("moov", 40)));
    }
}
