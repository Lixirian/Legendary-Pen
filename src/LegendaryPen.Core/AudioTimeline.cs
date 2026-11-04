namespace LegendaryPen.Core;

/// <summary>
/// Met le son d'un périphérique sur l'horloge de la vidéo, en images audio de 48 kHz depuis le début de la vidéo.
/// Le périphérique a sa propre horloge, qui dérive, et se tait parfois sans rien livrer. L'encodeur, lui, attend un son continu.
/// Chaque paquet est donc placé d'après l'heure à laquelle il a été capté : un écart réel devient du silence, un retard perd
/// ce qui recouvre le déjà écrit, et une dérive se rattrape une image à la fois, ce qui ne s'entend pas.
/// Le fil de capture écrit et celui de l'encodeur lit : toutes les méthodes peuvent se croiser.
/// </summary>
public sealed class AudioTimeline
{
    public const int FramesPerSecond = 48000;
    public const int Channels = 2;
    public const int BitsPerSample = 16;
    public const int BytesPerFrame = Channels * BitsPerSample / 8;

    /// <summary>Un morceau remis à l'encodeur : 20 ms.</summary>
    public const int ChunkFrames = FramesPerSecond / 50;

    /// <summary>Écart toléré avant de corriger : 5 ms, pour ne pas suivre le simple tremblement des paquets.</summary>
    public const int DriftToleranceFrames = FramesPerSecond / 200;

    /// <summary>Au-delà de 40 ms, l'écart n'est plus une dérive : il se comble ou se coupe d'un coup.</summary>
    public const int GapFrames = FramesPerSecond / 25;

    /// <summary>Ce que retient la mémoire si l'encodeur ne lit plus : 10 s. Le plus ancien part d'abord.</summary>
    public const int CapacityFrames = FramesPerSecond * 10;

    private readonly object _gate = new();
    private readonly byte[] _ring = new byte[CapacityFrames * BytesPerFrame];
    private long _read;
    private long _written;
    private bool _finished;

    /// <summary>Images placées depuis le début, silences compris.</summary>
    public long Written
    {
        get
        {
            lock (_gate)
            {
                return _written;
            }
        }
    }

    /// <summary>Corrections de dérive d'une image.</summary>
    public long Corrections { get; private set; }

    public bool IsDrained
    {
        get
        {
            lock (_gate)
            {
                return _finished && _read == _written;
            }
        }
    }

    public static long FrameAt(long ticks) => (long)Math.Round((decimal)ticks * FramesPerSecond / TimeSpan.TicksPerSecond);

    public static TimeSpan TimeOf(long frame) => TimeSpan.FromTicks((long)Math.Round((decimal)frame * TimeSpan.TicksPerSecond / FramesPerSecond));

    /// <summary>Place un paquet dont la première image a été captée à l'image <paramref name="frame"/> de la vidéo.</summary>
    public void Place(ReadOnlySpan<byte> pcm, long frame)
    {
        lock (_gate)
        {
            if (_finished)
            {
                return;
            }

            // Avant le début de la vidéo : coupé à l'image près.
            if (frame < 0)
            {
                var early = Math.Min(pcm.Length / BytesPerFrame, -frame);
                pcm = pcm[(int)(early * BytesPerFrame)..];
                frame += early;
            }

            var offset = frame - _written;

            // Le premier paquet tombe pile à son heure : rien n'a encore été entendu, il n'y a pas de dérive à rattraper.
            if (offset >= GapFrames || (_written == 0 && offset > 0))
            {
                AppendSilence(offset);
            }
            else if (offset <= -GapFrames)
            {
                pcm = pcm[(int)(Math.Min(pcm.Length / BytesPerFrame, -offset) * BytesPerFrame)..];
            }
            else if (pcm.Length >= 2 * BytesPerFrame && Math.Abs(offset) > DriftToleranceFrames)
            {
                // Une seule image par paquet : assez pour suivre toute dérive réelle, et trop peu pour s'entendre.
                Corrections++;

                if (offset > 0)
                {
                    Append(pcm[..BytesPerFrame]);
                }
                else
                {
                    pcm = pcm[BytesPerFrame..];
                }
            }

            Append(pcm);
        }
    }

    /// <summary>Place un paquet à la suite, quand son heure n'est pas fiable : il n'y a rien à corriger.</summary>
    public void PlaceNext(ReadOnlySpan<byte> pcm)
    {
        lock (_gate)
        {
            if (!_finished)
            {
                Append(pcm);
            }
        }
    }

    /// <summary>Le périphérique se tait : le silence avance jusqu'à cette image. Sans effet si le son y est déjà.</summary>
    public void FillSilenceTo(long frame)
    {
        lock (_gate)
        {
            if (!_finished && frame > _written)
            {
                AppendSilence(frame - _written);
            }
        }
    }

    /// <summary>La vidéo finit à cette image : le son la rejoint en silence, et plus rien n'est accepté.</summary>
    public void Finish(long frame)
    {
        lock (_gate)
        {
            if (!_finished)
            {
                if (frame > _written)
                {
                    AppendSilence(frame - _written);
                }

                _finished = true;
            }
        }
    }

    /// <summary>Le morceau suivant, avec son image de départ. Faux s'il n'est pas encore complet ; à la fin, le dernier peut être plus court.</summary>
    public bool TryRead(out byte[] pcm, out long frame)
    {
        lock (_gate)
        {
            var available = _written - _read;

            if (available == 0 || (available < ChunkFrames && !_finished))
            {
                pcm = [];
                frame = 0;
                return false;
            }

            var frames = (int)Math.Min(available, ChunkFrames);
            pcm = new byte[frames * BytesPerFrame];
            frame = _read;
            var start = (int)(_read % CapacityFrames) * BytesPerFrame;
            var first = Math.Min(pcm.Length, _ring.Length - start);
            _ring.AsSpan(start, first).CopyTo(pcm);
            _ring.AsSpan(0, pcm.Length - first).CopyTo(pcm.AsSpan(first));
            _read += frames;
            return true;
        }
    }

    private void Append(ReadOnlySpan<byte> pcm)
    {
        var frames = pcm.Length / BytesPerFrame;

        if (frames > CapacityFrames)
        {
            pcm = pcm[((frames - CapacityFrames) * BytesPerFrame)..];
            _written += frames - CapacityFrames;
        }

        var start = (int)(_written % CapacityFrames) * BytesPerFrame;
        var first = Math.Min(pcm.Length, _ring.Length - start);
        pcm[..first].CopyTo(_ring.AsSpan(start));
        pcm[first..].CopyTo(_ring);
        _written += pcm.Length / BytesPerFrame;
        _read = Math.Max(_read, _written - CapacityFrames);
    }

    private void AppendSilence(long frames)
    {
        // Le silence est du vide : au-delà de la capacité, seule la position avance.
        if (frames > CapacityFrames)
        {
            _written += frames - CapacityFrames;
            frames = CapacityFrames;
        }

        var start = (int)(_written % CapacityFrames) * BytesPerFrame;
        var bytes = (int)frames * BytesPerFrame;
        var first = Math.Min(bytes, _ring.Length - start);
        _ring.AsSpan(start, first).Clear();
        _ring.AsSpan(0, bytes - first).Clear();
        _written += frames;
        _read = Math.Max(_read, _written - CapacityFrames);
    }
}
