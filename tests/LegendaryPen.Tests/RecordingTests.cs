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

    [Fact]
    public void The_record_shortcut_is_global_and_unassigned()
    {
        var definition = HotkeyCatalog.All.Single(candidate => candidate.Action == HotkeyAction.Record);

        Assert.True(definition.IsGlobal);
        Assert.Equal(HotkeyBinding.None, definition.Default);
        Assert.True(new HotkeyMap()[HotkeyAction.Record].IsNone);
        Assert.Empty(new HotkeyMap().Validate());
    }

    [Fact]
    public void The_record_shortcut_comes_last_so_existing_identifiers_do_not_move()
    {
        Assert.Equal(HotkeyAction.Record, Enum.GetValues<HotkeyAction>()[^1]);
        Assert.Equal(HotkeyAction.Record, HotkeyCatalog.All[^1].Action);
    }

    [Fact]
    public void The_end_of_a_highlighter_stroke_does_not_undo_the_exclusion_of_a_recording()
    {
        var exclusions = new CaptureExclusionCount();

        exclusions.Acquire();
        exclusions.Acquire();

        Assert.False(exclusions.Release());
        Assert.True(exclusions.IsExcluded);
        Assert.True(exclusions.Release());
        Assert.False(exclusions.IsExcluded);
    }

    [Fact]
    public void A_release_without_a_request_restores_nothing()
    {
        var exclusions = new CaptureExclusionCount();

        Assert.False(exclusions.Release());
        Assert.False(exclusions.IsExcluded);

        exclusions.Acquire();
        Assert.True(exclusions.IsExcluded);
    }

    [Theory]
    [InlineData(RecordingEndReason.Requested, true, "Vidéo enregistrée : {0}")]
    [InlineData(RecordingEndReason.MonitorClosed, true, "L'écran enregistré a été débranché.|Vidéo enregistrée : {0}")]
    [InlineData(RecordingEndReason.MonitorClosed, false, "L'écran enregistré a été débranché.")]
    [InlineData(RecordingEndReason.ScreenChanged, true, "La résolution de l'écran a changé : enregistrement arrêté.|Vidéo enregistrée : {0}")]
    [InlineData(RecordingEndReason.ScreenChanged, false, "La résolution de l'écran a changé : enregistrement arrêté.")]
    [InlineData(RecordingEndReason.DiskFull, true, "Plus assez de place sur le disque : enregistrement arrêté.|Vidéo enregistrée : {0}")]
    [InlineData(RecordingEndReason.DiskFull, false, "Plus assez de place sur le disque : enregistrement arrêté.")]
    [InlineData(RecordingEndReason.Failed, true, "L'enregistrement s'est arrêté sur une erreur. Vidéo gardée : {0}")]
    [InlineData(RecordingEndReason.Failed, false, "Enregistrement impossible sur ce PC.")]
    public void Each_end_of_recording_has_its_message(RecordingEndReason reason, bool hasFile, string expected)
    {
        Assert.Equal(expected.Split('|'), RecordingRules.EndMessages(reason, hasFile));
    }

    [Fact]
    public void A_stop_that_wrote_nothing_says_nothing()
    {
        Assert.Empty(RecordingRules.EndMessages(RecordingEndReason.Requested, hasFile: false));
    }

    [Fact]
    public void Every_end_message_is_translated_with_the_same_placeholders()
    {
        foreach (var reason in Enum.GetValues<RecordingEndReason>())
        {
            foreach (var hasFile in new[] { true, false })
            {
                foreach (var key in RecordingRules.EndMessages(reason, hasFile))
                {
                    Assert.True(Translations.English.TryGetValue(key, out var english), key);
                    Assert.Equal(key.Contains("{0}", StringComparison.Ordinal), english.Contains("{0}", StringComparison.Ordinal));
                }
            }
        }
    }

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(9, "0:09")]
    [InlineData(754, "12:34")]
    [InlineData(3599, "59:59")]
    [InlineData(3600, "1:00:00")]
    [InlineData(45296, "12:34:56")]
    public void The_elapsed_time_is_short_and_grows_with_the_hours(int seconds, string expected)
    {
        Assert.Equal(expected, RecordingRules.FormatElapsed(TimeSpan.FromSeconds(seconds)));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(RecordingRules.MinFreeBytes, false)]
    [InlineData(RecordingRules.MinFreeBytes - 1, true)]
    [InlineData(0L, true)]
    public void Only_a_real_value_under_the_limit_stops_a_recording(long? freeBytes, bool expected)
    {
        Assert.Equal(expected, RecordingRules.ShouldStopForDisk(freeBytes));
    }

    [Fact]
    public void The_sound_source_defaults_to_none_and_is_saved()
    {
        var path = Path.Combine(Path.GetTempPath(), $"legendary-pen-{Guid.NewGuid():N}", "settings.json");

        try
        {
            Assert.Equal(RecordingAudio.None, new AppSettings().RecordingAudio);

            var store = new SettingsStore(path);
            store.Save(new AppSettings { RecordingAudio = RecordingAudio.Microphone });

            Assert.Equal(RecordingAudio.Microphone, store.Load().RecordingAudio);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }
}
