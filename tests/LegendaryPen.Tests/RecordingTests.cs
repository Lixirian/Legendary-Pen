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
    public void A_stop_during_finishing_changes_nothing()
    {
        var state = new RecordingState();
        state.TryStart();
        state.CountdownElapsed();
        state.RequestStop();
        var changes = 0;
        state.Changed += () => changes++;

        state.RequestStop();

        Assert.Equal(RecordingStatus.Finishing, state.Status);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void A_second_start_is_refused_during_finishing()
    {
        var state = new RecordingState();
        state.TryStart();
        state.CountdownElapsed();
        state.RequestStop();

        Assert.False(state.TryStart());
        Assert.Equal(RecordingStatus.Finishing, state.Status);
    }

    [Fact]
    public void A_start_works_again_after_returning_to_idle()
    {
        var state = new RecordingState();
        state.TryStart();
        state.CountdownElapsed();
        state.RequestStop();
        state.Finished();

        Assert.True(state.TryStart());
        Assert.Equal(RecordingStatus.Countdown, state.Status);
    }

    [Fact]
    public void A_buffer_error_is_only_stuck_after_five_consecutive_seconds()
    {
        var watch = new AudioBufferErrorWatch();

        Assert.False(watch.IsStuck(TimeSpan.FromSeconds(100)));
        Assert.False(watch.IsStuck(TimeSpan.FromSeconds(104.9)));
        Assert.True(watch.IsStuck(TimeSpan.FromSeconds(105)));
    }

    [Fact]
    public void A_good_read_restarts_the_count_of_buffer_errors()
    {
        var watch = new AudioBufferErrorWatch();
        watch.IsStuck(TimeSpan.FromSeconds(100));

        watch.Clear();

        Assert.False(watch.IsStuck(TimeSpan.FromSeconds(106)));
        Assert.False(watch.IsStuck(TimeSpan.FromSeconds(110.9)));
        Assert.True(watch.IsStuck(TimeSpan.FromSeconds(111)));
    }

    [Fact]
    public void A_second_message_without_an_action_keeps_the_action_of_the_first()
    {
        var clicks = new NotificationClicks();
        Action saved = () => { };

        clicks.Shown(saved, TimeSpan.FromSeconds(100));
        clicks.Shown(null, TimeSpan.FromSeconds(102));

        Assert.Same(saved, clicks.Action);
    }

    [Fact]
    public void A_second_message_with_an_action_takes_over()
    {
        var clicks = new NotificationClicks();
        Action first = () => { };
        Action second = () => { };

        clicks.Shown(first, TimeSpan.FromSeconds(100));
        clicks.Shown(second, TimeSpan.FromSeconds(102));

        Assert.Same(second, clicks.Action);
    }

    [Fact]
    public void An_old_action_does_not_answer_a_click_on_a_later_message()
    {
        var clicks = new NotificationClicks();

        clicks.Shown(() => { }, TimeSpan.FromSeconds(100));
        clicks.Shown(null, TimeSpan.FromSeconds(100) + NotificationClicks.Lifetime);

        Assert.Null(clicks.Action);
    }

    [Fact]
    public void A_kept_action_still_ends_with_its_own_message_not_the_later_one()
    {
        var clicks = new NotificationClicks();

        clicks.Shown(() => { }, TimeSpan.FromSeconds(100));
        clicks.Shown(null, TimeSpan.FromSeconds(105));
        clicks.Shown(null, TimeSpan.FromSeconds(100) + NotificationClicks.Lifetime);

        Assert.Null(clicks.Action);
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

    [Fact]
    public void Numbers_go_on_while_the_names_are_taken()
    {
        var stem = Path.Combine(@"C:\Videos", "Legendary Pen 2026-11-03 21-10-05");
        var taken = new HashSet<string> { stem + ".mp4", stem + " (2).mp4" };

        var path = RecordingRules.FilePath(@"C:\Videos", new DateTime(2026, 11, 3, 21, 10, 5), taken.Contains);

        Assert.Equal(stem + " (3).mp4", path);
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
    }

    [Fact]
    public void An_old_settings_file_loads_the_recording_defaults()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");

        try
        {
            File.WriteAllText(path, "{ \"Version\": 1, \"PressureEnabled\": false, \"RecordingCountdownSeconds\": 5 }");

            var settings = new SettingsStore(path).Load();

            Assert.False(settings.PressureEnabled);
            Assert.Equal(RecordingAudio.None, settings.RecordingAudio);
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
    [InlineData(RecordingEndReason.Failed, false, "L'enregistrement a échoué : aucune vidéo gardée.")]
    public void Each_end_of_recording_has_its_message(RecordingEndReason reason, bool hasFile, string expected)
    {
        Assert.Equal(expected.Split('|'), RecordingRules.EndMessages(reason, hasFile));
    }

    [Fact]
    public void A_stop_that_wrote_nothing_says_nothing()
    {
        Assert.Empty(RecordingRules.EndMessages(RecordingEndReason.Requested, hasFile: false));
    }

    [Theory]
    [InlineData(RecordingAudioProblem.MicrophoneUnavailable, "Micro indisponible : vidéo enregistrée sans son.")]
    [InlineData(RecordingAudioProblem.SystemSoundUnavailable, "Son de l'ordinateur indisponible : vidéo enregistrée sans son.")]
    [InlineData(RecordingAudioProblem.Interrupted, "Le son s'est interrompu pendant l'enregistrement : la suite de la vidéo est muette.")]
    public void A_sound_problem_adds_its_line_after_the_saved_file(RecordingAudioProblem problem, string line)
    {
        Assert.Equal(["Vidéo enregistrée : {0}", line], RecordingRules.EndMessages(RecordingEndReason.Requested, true, problem));
        Assert.Equal(["L'écran enregistré a été débranché.", "Vidéo enregistrée : {0}", line], RecordingRules.EndMessages(RecordingEndReason.MonitorClosed, true, problem));
        Assert.Equal(["L'enregistrement s'est arrêté sur une erreur. Vidéo gardée : {0}", line], RecordingRules.EndMessages(RecordingEndReason.Failed, true, problem));
    }

    [Theory]
    [InlineData(RecordingEndReason.Requested)]
    [InlineData(RecordingEndReason.MonitorClosed)]
    [InlineData(RecordingEndReason.Failed)]
    public void Without_a_video_a_sound_problem_says_nothing_more(RecordingEndReason reason)
    {
        Assert.Equal(RecordingRules.EndMessages(reason, false), RecordingRules.EndMessages(reason, false, RecordingAudioProblem.MicrophoneUnavailable));
    }

    [Fact]
    public void Every_sound_problem_is_translated()
    {
        Assert.Equal("Microphone unavailable: video saved without sound.", Translations.English["Micro indisponible : vidéo enregistrée sans son."]);
        Assert.Equal("Computer sound unavailable: video saved without sound.", Translations.English["Son de l'ordinateur indisponible : vidéo enregistrée sans son."]);

        foreach (var problem in Enum.GetValues<RecordingAudioProblem>())
        {
            foreach (var key in RecordingRules.EndMessages(RecordingEndReason.Requested, true, problem))
            {
                Assert.True(Translations.English.ContainsKey(key), key);
            }
        }
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

    [Fact]
    public void Nothing_is_refused_or_silenced_at_first()
    {
        var latch = new RecordingCloseLatch();

        Assert.False(latch.RefusesStart);
        Assert.False(latch.IsQuiet);
    }

    [Fact]
    public void An_update_refuses_a_start_but_keeps_the_message_and_reopens_if_abandoned()
    {
        var latch = new RecordingCloseLatch();

        latch.Enter(RecordingClose.Update);
        Assert.True(latch.RefusesStart);
        Assert.False(latch.IsQuiet);

        latch.Leave(RecordingClose.Update);
        Assert.False(latch.RefusesStart);
    }

    [Fact]
    public void A_cancelled_logoff_reopens_recording_and_the_messages()
    {
        var latch = new RecordingCloseLatch();

        latch.Enter(RecordingClose.SessionEnd);
        Assert.True(latch.RefusesStart);
        Assert.True(latch.IsQuiet);

        latch.Leave(RecordingClose.SessionEnd);
        Assert.False(latch.RefusesStart);
        Assert.False(latch.IsQuiet);
    }

    [Theory]
    [InlineData(RecordingClose.Update)]
    [InlineData(RecordingClose.SessionEnd)]
    public void An_exit_stays_closed_whatever_is_abandoned_meanwhile(RecordingClose other)
    {
        var latch = new RecordingCloseLatch();

        latch.Enter(other);
        latch.Enter(RecordingClose.Exit);
        latch.Leave(other);

        Assert.True(latch.RefusesStart);
        Assert.True(latch.IsQuiet);
    }

    [Fact]
    public void An_abandoned_update_does_not_reopen_during_a_session_end_nor_the_reverse()
    {
        var latch = new RecordingCloseLatch();

        latch.Enter(RecordingClose.Update);
        latch.Enter(RecordingClose.SessionEnd);

        latch.Leave(RecordingClose.Update);
        Assert.True(latch.RefusesStart);

        latch.Enter(RecordingClose.Update);
        latch.Leave(RecordingClose.SessionEnd);
        Assert.True(latch.RefusesStart);
        Assert.False(latch.IsQuiet);
    }

    [Theory]
    [InlineData(RecordingEndReason.Requested, "La vidéo est peut-être incomplète. Fichier gardé : {0}")]
    [InlineData(RecordingEndReason.Failed, "La vidéo est peut-être incomplète. Fichier gardé : {0}")]
    [InlineData(RecordingEndReason.MonitorClosed, "L'écran enregistré a été débranché.|La vidéo est peut-être incomplète. Fichier gardé : {0}")]
    [InlineData(RecordingEndReason.ScreenChanged, "La résolution de l'écran a changé : enregistrement arrêté.|La vidéo est peut-être incomplète. Fichier gardé : {0}")]
    [InlineData(RecordingEndReason.DiskFull, "Plus assez de place sur le disque : enregistrement arrêté.|La vidéo est peut-être incomplète. Fichier gardé : {0}")]
    public void A_kept_file_that_may_be_incomplete_says_so(RecordingEndReason reason, string expected)
    {
        Assert.Equal(expected.Split('|'), RecordingRules.EndMessages(reason, true, mayBeIncomplete: true));
    }

    [Fact]
    public void Without_a_file_there_is_nothing_to_call_incomplete()
    {
        Assert.Equal(["L'enregistrement a échoué : aucune vidéo gardée."], RecordingRules.EndMessages(RecordingEndReason.Failed, false, mayBeIncomplete: true));
    }

    [Fact]
    public void The_new_end_messages_are_translated()
    {
        Assert.Equal("The recording failed: no video was kept.", Translations.English["L'enregistrement a échoué : aucune vidéo gardée."]);
        Assert.Equal("The video may be incomplete. File kept: {0}", Translations.English["La vidéo est peut-être incomplète. Fichier gardé : {0}"]);
    }

    [Theory]
    [InlineData(RecordingAudioProblem.MicrophoneUnavailable, "Micro indisponible : cette vidéo se fait sans son.")]
    [InlineData(RecordingAudioProblem.SystemSoundUnavailable, "Son de l'ordinateur indisponible : cette vidéo se fait sans son.")]
    public void A_missing_sound_source_is_announced_at_the_start(RecordingAudioProblem problem, string expected)
    {
        Assert.Equal(expected, RecordingRules.StartSoundMessage(problem));
        Assert.True(Translations.English.ContainsKey(expected));
    }

    [Theory]
    [InlineData(RecordingAudioProblem.None)]
    [InlineData(RecordingAudioProblem.Interrupted)]
    public void Nothing_else_about_sound_is_announced_at_the_start(RecordingAudioProblem problem)
    {
        Assert.Null(RecordingRules.StartSoundMessage(problem));
    }

    [Theory]
    [InlineData(unchecked((int)0x88890018), true)]
    [InlineData(unchecked((int)0x8889000B), true)]
    [InlineData(unchecked((int)0x88890004), false)]
    [InlineData(unchecked((int)0x80004005), false)]
    [InlineData(0, false)]
    public void Only_a_busy_sound_buffer_is_skipped_until_the_next_poll(int result, bool expected)
    {
        Assert.Equal(expected, RecordingRules.IsTemporaryAudioBufferError(result));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(-1)]
    public void An_unknown_sound_setting_loads_as_no_sound(int value)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");

        try
        {
            File.WriteAllText(path, $"{{ \"Version\": 1, \"PressureEnabled\": false, \"RecordingAudio\": {value} }}");

            var settings = new SettingsStore(path).Load();

            Assert.Equal(RecordingAudio.None, settings.RecordingAudio);
            Assert.False(settings.PressureEnabled);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(1, RecordingAudio.System)]
    [InlineData(2, RecordingAudio.Microphone)]
    public void A_known_sound_setting_loads_unchanged(int value, RecordingAudio expected)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");

        try
        {
            File.WriteAllText(path, $"{{ \"RecordingAudio\": {value} }}");

            Assert.Equal(expected, new SettingsStore(path).Load().RecordingAudio);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
