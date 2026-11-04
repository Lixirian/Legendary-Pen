using LegendaryPen.Core;
using Xunit;

namespace LegendaryPen.Tests;

public class AudioTimelineTests
{
    private const int BytesPerFrame = AudioTimeline.BytesPerFrame;
    private const int Chunk = AudioTimeline.ChunkFrames;
    private const int PacketFrames = 480;

    /// <summary>Chaque image porte son numéro : on retrouve ainsi ce qui a été gardé, décalé ou remplacé par du silence.</summary>
    private static byte[] Numbered(int firstNumber, int frames)
    {
        var bytes = new byte[frames * BytesPerFrame];

        for (var i = 0; i < frames; i++)
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(i * BytesPerFrame), firstNumber + i);
        }

        return bytes;
    }

    private static int Number(byte[] chunk, int frame) => BitConverter.ToInt32(chunk, frame * BytesPerFrame);

    private static List<(byte[] Pcm, long Start)> ReadAll(AudioTimeline timeline)
    {
        var chunks = new List<(byte[], long)>();

        while (timeline.TryRead(out var pcm, out var start))
        {
            chunks.Add((pcm, start));
        }

        return chunks;
    }

    [Fact]
    public void Contiguous_packets_come_out_unchanged_in_chunks_of_20_ms()
    {
        var timeline = new AudioTimeline();
        timeline.Place(Numbered(1, Chunk), 0);
        timeline.Place(Numbered(1 + Chunk, Chunk), Chunk);

        var chunks = ReadAll(timeline);

        Assert.Equal(2, chunks.Count);
        Assert.Equal(0, chunks[0].Start);
        Assert.Equal(Chunk, chunks[1].Start);
        Assert.Equal(Chunk * BytesPerFrame, chunks[0].Pcm.Length);
        Assert.Equal(1, Number(chunks[0].Pcm, 0));
        Assert.Equal(1 + Chunk, Number(chunks[1].Pcm, 0));
        Assert.Equal(0, timeline.Corrections);
    }

    [Fact]
    public void Less_than_a_chunk_stays_waiting_until_the_end()
    {
        var timeline = new AudioTimeline();
        timeline.Place(Numbered(1, 100), 0);

        Assert.False(timeline.TryRead(out _, out _));
        Assert.False(timeline.IsDrained);

        timeline.Finish(100);

        Assert.True(timeline.TryRead(out var pcm, out var start));
        Assert.Equal(100 * BytesPerFrame, pcm.Length);
        Assert.Equal(0, start);
        Assert.True(timeline.IsDrained);
    }

    [Fact]
    public void What_comes_before_the_origin_is_trimmed_to_the_exact_frame()
    {
        var timeline = new AudioTimeline();
        timeline.Place(Numbered(1, PacketFrames), -PacketFrames);
        Assert.Equal(0, timeline.Written);

        timeline.Place(Numbered(1000, PacketFrames), -100);
        Assert.Equal(PacketFrames - 100, timeline.Written);

        timeline.Finish(timeline.Written);
        var chunk = ReadAll(timeline).Single();
        Assert.Equal(1100, Number(chunk.Pcm, 0));
    }

    [Fact]
    public void A_real_gap_becomes_silence_and_the_sound_stays_at_its_time()
    {
        var timeline = new AudioTimeline();
        timeline.Place(Numbered(1, Chunk), 0);
        var later = Chunk + 3 * Chunk;

        timeline.Place(Numbered(5000, Chunk), later);

        var chunks = ReadAll(timeline);
        Assert.Equal(5, chunks.Count);
        Assert.All(chunks[1..4], chunk => Assert.All(chunk.Pcm, value => Assert.Equal(0, value)));
        Assert.Equal(later, chunks[4].Start);
        Assert.Equal(5000, Number(chunks[4].Pcm, 0));
    }

    [Fact]
    public void A_late_packet_loses_what_overlaps_what_was_already_written()
    {
        var timeline = new AudioTimeline();
        timeline.FillSilenceTo(Chunk * 4);

        timeline.Place(Numbered(1, Chunk * 4), Chunk);

        Assert.Equal(Chunk * 5, timeline.Written);
        var chunks = ReadAll(timeline);
        Assert.Equal(1 + Chunk * 3, Number(chunks[4].Pcm, 0));
    }

    [Theory]
    [InlineData(-200)]
    [InlineData(200)]
    public void Packet_jitter_inside_the_tolerance_changes_nothing(int jitter)
    {
        var timeline = new AudioTimeline();
        timeline.Place(Numbered(1, PacketFrames), 0);

        timeline.Place(Numbered(1000, PacketFrames), PacketFrames + jitter);

        Assert.Equal(PacketFrames * 2, timeline.Written);
        Assert.Equal(0, timeline.Corrections);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(-200)]
    [InlineData(1000)]
    [InlineData(-1000)]
    public void A_device_clock_that_drifts_never_moves_the_sound_away_from_the_video_clock(int partsPerMillion)
    {
        var timeline = new AudioTimeline();
        var packets = 3600L * 48000 / PacketFrames;
        var worst = 0L;
        var pcm = new byte[PacketFrames * BytesPerFrame];

        for (long packet = 0; packet < packets; packet++)
        {
            // Le périphérique livre ses images à son rythme ; l'horloge unique dit où chaque paquet tombe vraiment.
            var deviceFrame = packet * PacketFrames;
            var target = deviceFrame + (deviceFrame * partsPerMillion / 1_000_000);

            timeline.Place(pcm, target);
            worst = Math.Max(worst, Math.Abs(timeline.Written - (target + PacketFrames)));
            ReadAll(timeline);
        }

        Assert.InRange(worst, 0, AudioTimeline.DriftToleranceFrames + 1);
        Assert.InRange(timeline.Corrections, 1, Math.Abs(partsPerMillion) * 3600L * 48000 / 1_000_000 + 1);
    }

    [Fact]
    public void The_first_packet_lands_exactly_on_its_time_without_any_correction()
    {
        var timeline = new AudioTimeline();

        timeline.Place(Numbered(1, PacketFrames), 576);

        Assert.Equal(576 + PacketFrames, timeline.Written);
        Assert.Equal(0, timeline.Corrections);
    }

    [Fact]
    public void One_correction_changes_one_frame_only_at_the_head_of_a_packet()
    {
        var late = new AudioTimeline();
        late.Place(Numbered(1, PacketFrames), 0);
        late.Place(Numbered(1000, PacketFrames), PacketFrames + AudioTimeline.DriftToleranceFrames + 1);
        Assert.Equal(PacketFrames * 2 + 1, late.Written);
        Assert.Equal(1, late.Corrections);

        var early = new AudioTimeline();
        early.Place(Numbered(1, PacketFrames), 0);
        early.Place(Numbered(1000, PacketFrames), PacketFrames - AudioTimeline.DriftToleranceFrames - 1);
        Assert.Equal(PacketFrames * 2 - 1, early.Written);
        Assert.Equal(1, early.Corrections);
    }

    [Fact]
    public void A_silent_device_gives_silence_up_to_the_clock_and_the_sound_resumes_on_time()
    {
        var timeline = new AudioTimeline();
        timeline.FillSilenceTo(Chunk * 10);
        timeline.FillSilenceTo(Chunk * 5);
        Assert.Equal(Chunk * 10, timeline.Written);

        timeline.Place(Numbered(7, Chunk), Chunk * 10);

        var chunks = ReadAll(timeline);
        Assert.Equal(11, chunks.Count);
        Assert.Equal(Chunk * 10, chunks[10].Start);
        Assert.Equal(7, Number(chunks[10].Pcm, 0));
    }

    [Fact]
    public void Finish_pads_with_silence_up_to_the_end_of_the_video_and_refuses_what_comes_after()
    {
        var timeline = new AudioTimeline();
        timeline.Place(Numbered(1, Chunk), 0);

        timeline.Finish(Chunk * 3 + 10);
        timeline.Place(Numbered(9, Chunk), Chunk * 4);

        var chunks = ReadAll(timeline);
        Assert.Equal(Chunk * 3 + 10, chunks.Sum(chunk => chunk.Pcm.Length) / BytesPerFrame);
        Assert.True(timeline.IsDrained);
    }

    [Fact]
    public void An_encoder_that_no_longer_reads_cannot_fill_the_memory()
    {
        var timeline = new AudioTimeline();
        var frames = AudioTimeline.CapacityFrames * 3L;

        timeline.FillSilenceTo(frames);

        Assert.True(timeline.TryRead(out _, out var start));
        Assert.Equal(frames - AudioTimeline.CapacityFrames, start);
    }

    [Fact]
    public void A_packet_without_a_reliable_time_follows_the_previous_one_while_the_clock_agrees()
    {
        var timeline = new AudioTimeline();
        timeline.PlaceNext(Numbered(1, Chunk), 0);
        timeline.PlaceNext(Numbered(1 + Chunk, Chunk), Chunk + AudioTimeline.DriftToleranceFrames);

        var chunks = ReadAll(timeline);

        Assert.Equal(2, chunks.Count);
        Assert.Equal(1 + Chunk, Number(chunks[1].Pcm, 0));
        Assert.Equal(0, timeline.Corrections);
    }

    [Fact]
    public void A_packet_without_a_reliable_time_loses_one_frame_when_the_sound_runs_slightly_ahead_of_the_clock()
    {
        var timeline = new AudioTimeline();
        timeline.PlaceNext(Numbered(1, Chunk), 0);

        // Le son est en avance de plus de 5 ms sur l'horloge, mais de moins de 40 ms : une seule image saute.
        timeline.PlaceNext(Numbered(1 + Chunk, Chunk), Chunk - AudioTimeline.DriftToleranceFrames - 1);

        Assert.Equal(1, timeline.Corrections);
        Assert.Equal((2 * Chunk) - 1, timeline.Written);
    }

    [Fact]
    public void A_packet_without_a_reliable_time_is_cut_when_the_sound_runs_far_ahead_of_the_clock()
    {
        var timeline = new AudioTimeline();
        timeline.PlaceNext(Numbered(1, 3 * Chunk), 0);

        // L'horloge n'en est qu'au premier morceau : ce qui recouvre le déjà écrit disparaît, et le son n'avance plus.
        timeline.PlaceNext(Numbered(1000, Chunk), Chunk);

        Assert.Equal(3 * Chunk, timeline.Written);
    }

    [Fact]
    public void A_packet_without_a_reliable_time_adds_no_silence_when_the_sound_is_behind_the_clock()
    {
        var timeline = new AudioTimeline();
        timeline.PlaceNext(Numbered(1, Chunk), 0);
        timeline.PlaceNext(Numbered(1 + Chunk, Chunk), 10 * Chunk);

        Assert.Equal(2 * Chunk, timeline.Written);
        Assert.Equal(0, timeline.Corrections);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(10_000_000, 48000)]
    [InlineData(-10_000_000, -48000)]
    [InlineData(36_000_000_000_000, 172_800_000_000)]
    public void A_time_gives_its_frame(long ticks, long frame)
    {
        Assert.Equal(frame, AudioTimeline.FrameAt(ticks));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(48000, 10_000_000)]
    [InlineData(960, 200_000)]
    public void A_frame_gives_its_time(long frame, long ticks)
    {
        Assert.Equal(TimeSpan.FromTicks(ticks), AudioTimeline.TimeOf(frame));
    }
}
