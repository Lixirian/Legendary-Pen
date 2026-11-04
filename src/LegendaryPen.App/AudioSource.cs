using System.Diagnostics;
using System.Runtime.InteropServices;
using LegendaryPen.Core;
using Windows.Media.MediaProperties;

namespace LegendaryPen.App;

/// <summary>
/// Le son d'une vidéo : ce que joue le périphérique de sortie par défaut, ou le micro par défaut, jamais les deux.
/// La capture ne vit qu'entre <see cref="TryStart"/> et <see cref="Finish"/> : le micro n'est ouvert que pendant un
/// enregistrement où l'utilisateur l'a choisi, et il est relâché dès l'arrêt, même si l'encodeur traîne ensuite.
/// Windows convertit lui-même le format de mélange du périphérique en 48 kHz stéréo sur 16 bits, le seul que reçoit l'encodeur.
/// Tout passe par un fil à part : un pilote bloqué ne retient ni la vidéo ni l'arrêt.
/// </summary>
internal sealed class AudioSource : IDisposable
{
    private static readonly TimeSpan OpenLimit = TimeSpan.FromSeconds(3);

    // Au-delà, un fil de capture bloqué ne retient plus la fin de la vidéo.
    private static readonly TimeSpan FinishLimit = TimeSpan.FromMilliseconds(500);

    private const int PollMilliseconds = 5;

    // Un périphérique qui se tait ne livre rien : le silence comble, avec ce retard pour laisser passer un paquet encore en route.
    // Lu du côté de l'encodeur, il tient aussi quand le fil de capture est bloqué : le son ne manque jamais à la vidéo.
    private const long SilenceLagFrames = AudioTimeline.FramesPerSecond / 5;

    private const int WavePcm = 1;
    private const int BufferFlagDataDiscontinuity = 1, BufferFlagSilent = 2, BufferFlagTimestampError = 4;
    private const int LoopbackFlag = 0x00020000, AutoConvertFlag = unchecked((int)0x80000000), DefaultQualityFlag = 0x08000000;
    private const uint AllContexts = 23;
    private const int BufferEmpty = 0x08890001;
    private const long CaptureBufferTicks = TimeSpan.TicksPerSecond;

    private readonly AudioTimeline _timeline = new();
    private readonly AudioBufferErrorWatch _bufferErrors = new();
    private readonly RecordingAudio _kind;
    private readonly RecorderProbes _probes;
    private readonly ManualResetEventSlim _opened = new(false);
    private readonly ManualResetEventSlim _finishing = new(false);
    private long _originTicks = -1;
    private long _finishRequestedAt = -1;
    private volatile bool _openFailed;
    private volatile bool _deviceLost;
    private volatile bool _captureEnded;
    private IAudioClient? _client;
    private IAudioCaptureClient? _capture;
    private byte[] _packet = new byte[AudioTimeline.ChunkFrames * AudioTimeline.BytesPerFrame];

    private AudioSource(RecordingAudio kind, RecorderProbes probes)
    {
        _kind = kind;
        _probes = probes;
    }

    /// <summary>Le format remis à l'encodeur.</summary>
    public static AudioEncodingProperties Format =>
        AudioEncodingProperties.CreatePcm(AudioTimeline.FramesPerSecond, AudioTimeline.Channels, AudioTimeline.BitsPerSample);

    /// <summary>Vrai si le périphérique a disparu ou a échoué en route : la suite est du silence.</summary>
    public bool DeviceLost => _deviceLost;

    /// <summary>Vrai quand tout le son a été lu et qu'il n'en viendra plus.</summary>
    public bool IsFinished => _timeline.IsDrained;

    /// <summary>Ouvre le périphérique et commence à capter. Null s'il est absent, refusé ou bloqué : la vidéo se fera sans son.</summary>
    public static AudioSource? TryStart(RecordingAudio kind, RecorderProbes probes)
    {
        if (kind is not (RecordingAudio.System or RecordingAudio.Microphone))
        {
            return null;
        }

        var source = new AudioSource(kind, probes);
        var thread = new Thread(source.Run) { IsBackground = true, Name = "Legendary Pen audio capture" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();

        if (source._opened.Wait(OpenLimit) && !source._openFailed)
        {
            return source;
        }

        // Le fil finira par sortir de son blocage, et refermera alors ce qu'il a ouvert.
        source.Finish();
        return null;
    }

    /// <summary>Fixe l'heure zéro de la vidéo, en temps de <see cref="Stopwatch"/> : l'unique horloge du son et de l'image.</summary>
    public void SetOrigin(long stopwatchTimestamp) => Interlocked.CompareExchange(ref _originTicks, ToTicks(stopwatchTimestamp), -1);

    /// <summary>Le morceau de son suivant, sans jamais attendre. Faux s'il n'est pas prêt.</summary>
    public bool TryRead(out byte[] pcm, out TimeSpan timestamp)
    {
        var requested = Volatile.Read(ref _finishRequestedAt);

        if (requested >= 0)
        {
            if (Stopwatch.GetElapsedTime(requested) > FinishLimit && !_timeline.IsDrained && !_captureEnded)
            {
                // Le fil de capture ne répond plus : tout ce son n'était peut-être que du silence, et le message de fin le dira.
                _deviceLost = true;
                _timeline.Finish(EndFrame());
            }
        }
        else if (Volatile.Read(ref _originTicks) >= 0)
        {
            _timeline.FillSilenceTo(FrameOf(ToTicks(Stopwatch.GetTimestamp())) - SilenceLagFrames);
        }

        var read = _timeline.TryRead(out pcm, out var frame);
        timestamp = AudioTimeline.TimeOf(frame);
        return read;
    }

    /// <summary>Arrête de capter tout de suite, et ferme le son à l'heure actuelle. Ne bloque pas ; sans effet la seconde fois.</summary>
    public void Finish()
    {
        Interlocked.CompareExchange(ref _finishRequestedAt, Stopwatch.GetTimestamp(), -1);
        _finishing.Set();
    }

    public void Dispose() => Finish();

    private static long ToTicks(long stopwatchTimestamp)
    {
        var frequency = Stopwatch.Frequency;
        return stopwatchTimestamp / frequency * TimeSpan.TicksPerSecond + stopwatchTimestamp % frequency * TimeSpan.TicksPerSecond / frequency;
    }

    private long FrameOf(long ticks) => AudioTimeline.FrameAt(ticks - Volatile.Read(ref _originTicks));

    /// <summary>La fin du son : l'heure de l'arrêt, avec au moins un morceau, car un flux sans échantillon fait échouer l'encodeur.</summary>
    private long EndFrame()
    {
        var requested = Volatile.Read(ref _finishRequestedAt);
        var end = Volatile.Read(ref _originTicks) < 0 || requested < 0 ? 0 : FrameOf(ToTicks(requested));
        return Math.Max(end, AudioTimeline.ChunkFrames);
    }

    private void Run()
    {
        try
        {
            Capture();
        }
        catch (Exception exception)
        {
            // Rien ne doit remonter de ce fil : l'application se fermerait, et la vidéo avec elle.
            Debug.WriteLine(exception);
            _deviceLost = true;
        }
    }

    private void Capture()
    {
        try
        {
            _probes.AudioOpening?.Invoke(_kind);
            Open();
        }
        catch (Exception exception)
        {
            // Absent, ou permission refusée : l'appelant enregistre sans son et le dit à la fin.
            Debug.WriteLine(exception);
            _openFailed = true;
            CloseDevice();
            _opened.Set();
            _timeline.Finish(0);
            return;
        }

        _opened.Set();
        _probes.AudioOpened?.Invoke();

        while (!_finishing.Wait(PollMilliseconds))
        {
            Poll();
        }

        Poll();
        CloseDevice();
        _timeline.Finish(EndFrame());
        _captureEnded = true;
    }

    private void Open()
    {
        // Le micro ne s'ouvre que sur un choix explicite : jamais par défaut, quelle que soit la valeur reçue.
        var flow = _kind switch
        {
            RecordingAudio.System => DataFlow.Render,
            RecordingAudio.Microphone => DataFlow.Capture,
            _ => throw new InvalidOperationException("Unknown sound source."),
        };
        var isLoopback = flow == DataFlow.Render;
        var enumerator = (IDeviceEnumerator)new DeviceEnumerator();

        try
        {
            Check(enumerator.GetDefaultAudioEndpoint(flow, Role.Console, out var device));

            try
            {
                var clientId = typeof(IAudioClient).GUID;
                Check(device.Activate(ref clientId, AllContexts, 0, out var client));
                _client = (IAudioClient)client;
                var format = new WaveFormat
                {
                    Tag = WavePcm,
                    Channels = AudioTimeline.Channels,
                    SamplesPerSecond = AudioTimeline.FramesPerSecond,
                    AverageBytesPerSecond = AudioTimeline.FramesPerSecond * AudioTimeline.BytesPerFrame,
                    BlockAlign = AudioTimeline.BytesPerFrame,
                    BitsPerSample = AudioTimeline.BitsPerSample,
                };
                var flags = AutoConvertFlag | DefaultQualityFlag | (isLoopback ? LoopbackFlag : 0);
                Check(_client.Initialize(0, flags, CaptureBufferTicks, 0, ref format, 0));
                var captureId = typeof(IAudioCaptureClient).GUID;
                Check(_client.GetService(ref captureId, out var capture));
                _capture = (IAudioCaptureClient)capture;
                Check(_client.Start());
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    /// <summary>Lit tout ce que le périphérique a livré. S'il échoue, il est refermé et la suite sera du silence.</summary>
    private void Poll()
    {
        if (_capture is null)
        {
            return;
        }

        try
        {
            Drain(_capture);
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
            _deviceLost = true;
            CloseDevice();
        }
    }

    private void Drain(IAudioCaptureClient capture)
    {
        while (true)
        {
            Check(capture.GetNextPacketSize(out var size));

            if (size == 0)
            {
                _bufferErrors.Clear();
                return;
            }

            nint data = 0;
            uint frames = 0;
            var flags = 0;
            ulong time = 0;
            var forced = _probes.AudioBufferResult?.Invoke() ?? 0;
            var result = forced != 0 ? forced : capture.GetBuffer(out data, out frames, out flags, out _, out time);

            // Tampon momentanément occupé : aucun tampon n'a été remis, la prochaine lecture le reprendra. Si cela dure, le son est perdu.
            if (RecordingRules.IsTemporaryAudioBufferError(result))
            {
                if (_bufferErrors.IsStuck(TimeSpan.FromTicks(ToTicks(Stopwatch.GetTimestamp()))))
                {
                    throw new InvalidOperationException("The sound buffer stays in error.");
                }

                return;
            }

            Check(result);

            if (result == BufferEmpty)
            {
                _bufferErrors.Clear();
                return;
            }

            _bufferErrors.Clear();

            try
            {
                Take(data, (int)frames, flags, (long)time);
            }
            finally
            {
                Check(capture.ReleaseBuffer(frames));
            }
        }
    }

    private void Take(nint data, int frames, int flags, long time)
    {
        var origin = Volatile.Read(ref _originTicks);

        // Avant la première image, la vidéo n'a pas encore d'heure zéro : ce son-là n'y figurera pas.
        if (origin < 0 || frames == 0)
        {
            return;
        }

        var elapsed = time - origin;
        elapsed = _probes.AudioPacketTime?.Invoke(elapsed) ?? elapsed;

        if (_probes.AudioPacketDropped?.Invoke(elapsed) == true)
        {
            return;
        }

        flags = _probes.AudioPacketFlags?.Invoke(flags) ?? flags;

        var bytes = frames * AudioTimeline.BytesPerFrame;

        if (_packet.Length < bytes)
        {
            _packet = new byte[bytes];
        }

        var pcm = _packet.AsSpan(0, bytes);

        if ((flags & BufferFlagSilent) != 0)
        {
            pcm.Clear();
        }
        else
        {
            Marshal.Copy(data, _packet, 0, bytes);
        }

        if ((flags & BufferFlagTimestampError) != 0)
        {
            // Le paquet vient de finir au plus tard maintenant : son début n'est pas plus tard que cette heure estimée.
            _timeline.PlaceNext(pcm, FrameOf(ToTicks(Stopwatch.GetTimestamp())) - frames);
        }
        else
        {
            _timeline.Place(pcm, AudioTimeline.FrameAt(elapsed));
        }

        _probes.AudioPacket(elapsed, frames, (flags & BufferFlagSilent) != 0, (flags & BufferFlagDataDiscontinuity) != 0, _timeline.Corrections);
    }

    private void CloseDevice()
    {
        try
        {
            _client?.Stop();
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
        }

        if (_capture is not null)
        {
            Marshal.ReleaseComObject(_capture);
            _capture = null;
        }

        if (_client is not null)
        {
            Marshal.ReleaseComObject(_client);
            _client = null;
        }
    }

    private static void Check(int result)
    {
        if (result < 0)
        {
            Marshal.ThrowExceptionForHR(result);
        }
    }

    private enum DataFlow { Render, Capture }

    private enum Role { Console }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormat
    {
        public ushort Tag;
        public ushort Channels;
        public uint SamplesPerSecond;
        public uint AverageBytesPerSecond;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort ExtraSize;
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class DeviceEnumerator;

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(DataFlow flow, int mask, out nint devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(DataFlow flow, Role role, out IAudioDevice device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioDevice
    {
        [PreserveSig]
        int Activate(ref Guid id, uint context, nint parameters, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport]
    [Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig]
        int Initialize(int shareMode, int flags, long bufferTicks, long periodTicks, ref WaveFormat format, nint session);

        [PreserveSig]
        int GetBufferSize(out uint frames);

        [PreserveSig]
        int GetStreamLatency(out long ticks);

        [PreserveSig]
        int GetCurrentPadding(out uint frames);

        [PreserveSig]
        int IsFormatSupported(int shareMode, nint format, out nint closest);

        [PreserveSig]
        int GetMixFormat(out nint format);

        [PreserveSig]
        int GetDevicePeriod(out long defaultTicks, out long minimumTicks);

        [PreserveSig]
        int Start();

        [PreserveSig]
        int Stop();

        [PreserveSig]
        int Reset();

        [PreserveSig]
        int SetEventHandle(nint handle);

        [PreserveSig]
        int GetService(ref Guid id, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport]
    [Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig]
        int GetBuffer(out nint data, out uint frames, out int flags, out ulong devicePosition, out ulong time);

        [PreserveSig]
        int ReleaseBuffer(uint frames);

        [PreserveSig]
        int GetNextPacketSize(out uint frames);
    }
}
