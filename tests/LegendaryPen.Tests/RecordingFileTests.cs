using System.Diagnostics;
using LegendaryPen.Core;

namespace LegendaryPen.Tests;

/// <summary>Le sort du fichier à la fin d'un enregistrement : une vidéo finie n'est jamais supprimée ni cachée.</summary>
public sealed class RecordingFileTests : IDisposable
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(300);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"legendary-pen-{Guid.NewGuid():N}");

    public RecordingFileTests() => Directory.CreateDirectory(_folder);

    private string Temporary => Path.Combine(_folder, "video.mp4.tmp");

    private string Final => Path.Combine(_folder, "video.mp4");

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static byte[] Box(string name, int payload) =>
        [.. BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(payload + 8)), .. System.Text.Encoding.ASCII.GetBytes(name), .. new byte[payload]];

    private void WriteComplete() => File.WriteAllBytes(Temporary, [.. Box("ftyp", 16), .. Box("mdat", 100), .. Box("moov", 40)]);

    private void WriteCut() => File.WriteAllBytes(Temporary, [.. Box("ftyp", 16), .. Box("mdat", 100)]);

    /// <summary>Tient le fichier ouvert, puis le rend après le délai, comme un client de synchronisation ou un antivirus.</summary>
    private static Task Hold(string path, FileShare share, TimeSpan duration)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, share);
        return Task.Delay(duration).ContinueWith(_ => stream.Dispose(), TaskScheduler.Default);
    }

    [Fact]
    public void The_limits_are_those_of_the_ruling()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), RecordingFile.ReadRetryLimit);
        Assert.Equal(TimeSpan.FromSeconds(3), RecordingFile.MoveRetryLimit);
    }

    [Fact]
    public void A_complete_video_takes_its_final_name()
    {
        WriteComplete();

        var outcome = RecordingFile.Settle(Temporary, Final, diskFull: false);

        Assert.Equal(new RecordingFileOutcome(Final, IsInPlace: true, MayBeIncomplete: false), outcome);
        Assert.True(File.Exists(Final));
        Assert.False(File.Exists(Temporary));
    }

    [Fact]
    public void A_complete_video_is_kept_even_after_a_disk_full_failure()
    {
        WriteComplete();

        var outcome = RecordingFile.Settle(Temporary, Final, diskFull: true);

        Assert.Equal(new RecordingFileOutcome(Final, IsInPlace: true, MayBeIncomplete: false), outcome);
        Assert.True(File.Exists(Final));
    }

    [Fact]
    public void A_refused_file_is_kept_and_said_to_be_maybe_incomplete()
    {
        WriteCut();

        var outcome = RecordingFile.Settle(Temporary, Final, diskFull: false);

        Assert.Equal(new RecordingFileOutcome(Temporary, IsInPlace: false, MayBeIncomplete: true), outcome);
        Assert.True(File.Exists(Temporary));
        Assert.False(File.Exists(Final));
    }

    [Fact]
    public void Only_a_disk_full_failure_deletes_a_file_read_and_refused()
    {
        WriteCut();

        var outcome = RecordingFile.Settle(Temporary, Final, diskFull: true);

        Assert.Equal(new RecordingFileOutcome(null, IsInPlace: false, MayBeIncomplete: false), outcome);
        Assert.False(File.Exists(Temporary));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_file_that_stays_unreadable_is_kept_and_named(bool diskFull)
    {
        WriteComplete();
        using var holder = new FileStream(Temporary, FileMode.Open, FileAccess.Read, FileShare.None);

        var outcome = RecordingFile.Settle(Temporary, Final, diskFull, readLimit: Short);

        Assert.Equal(new RecordingFileOutcome(Temporary, IsInPlace: false, MayBeIncomplete: true), outcome);
        Assert.True(File.Exists(Temporary));
    }

    [Fact]
    public async Task A_read_error_is_retried_until_the_file_is_free()
    {
        WriteComplete();
        var released = Hold(Temporary, FileShare.None, TimeSpan.FromMilliseconds(500));

        var outcome = RecordingFile.Settle(Temporary, Final, diskFull: false);
        await released;

        Assert.Equal(new RecordingFileOutcome(Final, IsInPlace: true, MayBeIncomplete: false), outcome);
    }

    [Fact]
    public async Task A_rename_waits_for_a_file_held_for_one_second()
    {
        WriteComplete();
        var clock = Stopwatch.StartNew();
        var released = Hold(Temporary, FileShare.Read, TimeSpan.FromSeconds(1));

        var outcome = RecordingFile.Settle(Temporary, Final, diskFull: false);
        await released;

        Assert.Equal(new RecordingFileOutcome(Final, IsInPlace: true, MayBeIncomplete: false), outcome);
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(900));
    }

    [Fact]
    public void A_rename_that_stays_refused_keeps_a_readable_video_under_its_temporary_name()
    {
        WriteComplete();
        using var holder = new FileStream(Temporary, FileMode.Open, FileAccess.Read, FileShare.Read);

        var outcome = RecordingFile.Settle(Temporary, Final, diskFull: false, moveLimit: Short);

        Assert.Equal(new RecordingFileOutcome(Temporary, IsInPlace: false, MayBeIncomplete: false), outcome);
        Assert.True(File.Exists(Temporary));
    }

    [Fact]
    public void A_taken_final_name_is_not_retried_and_overwrites_nothing()
    {
        WriteComplete();
        File.WriteAllText(Final, "not ours");
        var clock = Stopwatch.StartNew();

        var outcome = RecordingFile.Settle(Temporary, Final, diskFull: false);

        Assert.Equal(new RecordingFileOutcome(Temporary, IsInPlace: false, MayBeIncomplete: false), outcome);
        Assert.True(clock.Elapsed < RecordingFile.MoveRetryLimit / 2);
        Assert.Equal("not ours", File.ReadAllText(Final));
    }

    [Fact]
    public void An_empty_file_is_not_a_video_and_does_not_stay()
    {
        File.WriteAllBytes(Temporary, []);

        var outcome = RecordingFile.Settle(Temporary, Final, diskFull: false);

        Assert.Equal(new RecordingFileOutcome(null, IsInPlace: false, MayBeIncomplete: false), outcome);
        Assert.False(File.Exists(Temporary));
    }

    [Fact]
    public void A_file_gone_from_the_disk_is_not_announced()
    {
        var outcome = RecordingFile.Settle(Temporary, Final, diskFull: false);

        Assert.Equal(new RecordingFileOutcome(null, IsInPlace: false, MayBeIncomplete: false), outcome);
    }

    [Fact]
    public void A_folder_that_accepts_a_file_is_writable_and_stays_clean()
    {
        Assert.True(RecordingFile.IsWritableFolder(_folder));
        Assert.Empty(Directory.GetFileSystemEntries(_folder));
    }

    [Fact]
    public void A_folder_that_cannot_hold_a_file_is_not_writable()
    {
        Assert.False(RecordingFile.IsWritableFolder(Path.Combine(_folder, "absent")));

        File.WriteAllText(Final, "a file, not a folder");
        Assert.False(RecordingFile.IsWritableFolder(Final));
    }
}
