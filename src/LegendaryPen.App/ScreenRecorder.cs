using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using LegendaryPen.Core;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.Storage.Streams;

namespace LegendaryPen.App;

/// <summary>
/// Enregistre un écran en MP4 H.264 à 30 images par seconde constantes : la capture ne garde que sa dernière image,
/// et l'encodeur la copie au rythme d'une horloge, car Windows n'envoie une image que si l'écran change.
///
/// Une instance ne sert qu'une fois. <see cref="StartAsync"/> ne lève que si l'enregistrement ne peut pas être tenté.
/// Toute autre fin, y compris une erreur au démarrage, passe par <see cref="Ended"/>, levé une seule fois, sur n'importe
/// quel fil : l'appelant le ramène sur son fil. À ce moment, les fichiers et l'encodeur sont terminés.
/// </summary>
internal sealed class ScreenRecorder : IAsyncDisposable
{
    private const int FramesPerSecond = 30;
    private const long TicksPerFrame = TimeSpan.TicksPerSecond / FramesPerSecond;

    // La capture garde sa dernière image, Windows écrit dans une autre.
    private const int CaptureBuffers = 2;

    // Une panne de l'encodeur avant cette seconde de vidéo reste un échec de démarrage : l'essai en logiciel ne perd presque rien.
    private const int StartConfirmedSamples = FramesPerSecond;

    private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FrameWaitTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CancelGrace = TimeSpan.FromSeconds(2);

    // Attributs Media Foundation, depuis mfapi.h.
    private static readonly Guid MtYuvMatrix = new("3e23d450-2c75-4d25-a00e-b91670d12327");
    private static readonly Guid MtVideoPrimaries = new("dbfbe4d7-0740-4ee0-8192-850ab0e21935");
    private static readonly Guid MtTransferFunction = new("5fb0fce9-be5c-4935-a811-ec838f8eed93");
    private static readonly Guid MtNominalRange = new("c21b8ee5-b956-4071-8daf-325edf5cab11");
    private static readonly Guid LowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    private const uint Bt709Matrix = 1, Bt709Primaries = 2, Bt709Transfer = 5, StudioRange = 2;

    private readonly object _frameGate = new();
    private readonly object _endGate = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly TaskCompletionSource _running = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ManualResetEventSlim _firstFrame = new(false);
    private readonly Stopwatch _clock = new();

    private Direct3DInterop? _direct3D;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private Direct3D11CaptureFrame? _latest;
    private FrameTextures? _textures;
    private IAsyncActionWithProgress<double>? _transcode;
    private Exception? _sampleFault;
    private string _temporaryPath = "";
    private bool _temporaryCreated;
    private bool _captureClosed;
    private int _width;
    private int _height;
    private long _lastIndex;
    private int _samples;
    private int _startCalled;
    private int _endRaised;
    private RecordingEndReason? _endRequest;

    public event Action<RecordingEndReason>? Ended;

    public static bool IsSupported
    {
        get
        {
            // IsCursorCaptureEnabled demande Windows 10 2004 ; les éditions N n'ont pas Media Foundation.
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) || !NativeLibrary.TryLoad("mfplat.dll", out var mediaFoundation))
            {
                return false;
            }

            NativeLibrary.Free(mediaFoundation);
            return GraphicsCaptureSession.IsSupported();
        }
    }

    /// <summary>Points d'essai du banc. Null en usage normal.</summary>
    internal RecorderProbes Probes { get; } = new();

    /// <summary>
    /// Se termine quand l'encodeur demande sa première image, ou quand l'enregistrement a déjà pris fin.
    /// Le son n'existe pas encore : toute source est traitée comme <see cref="RecordingAudio.None"/>.
    /// </summary>
    public Task StartAsync(MonitorArea monitor, string path, RecordingAudio audio)
    {
        if (!IsSupported)
        {
            throw new InvalidOperationException("Screen recording is not supported on this system.");
        }

        if (Interlocked.Exchange(ref _startCalled, 1) != 0)
        {
            throw new InvalidOperationException("A recorder runs only once.");
        }

        // Vidéo seule pour l'instant : le son arrivera avec un second flux de la source.
        _ = audio;

        // Tout le travail se fait hors du fil de l'appelant : ni la capture ni l'encodeur ne touchent l'interface.
        _ = Task.Run(() => RunAsync(monitor, path));
        return _running.Task;
    }

    /// <summary>Se termine quand l'enregistrement a pris fin, en 15 s au plus. Sans effet si rien ne tourne.</summary>
    public Task StopAsync()
    {
        if (Volatile.Read(ref _startCalled) == 0)
        {
            return Task.CompletedTask;
        }

        RequestEnd(RecordingEndReason.Requested);
        return _finished.Task;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task RunAsync(MonitorArea monitor, string path)
    {
        // StorageFile refuse un chemin relatif ou écrit avec des barres obliques.
        _temporaryPath = Path.GetFullPath(path) + ".tmp";
        Exception? failure = null;

        try
        {
            OpenCapture(monitor);

            if (WaitForFirstFrame())
            {
                await EncodeWithRetryAsync().ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            failure = exception;
            Debug.WriteLine(exception);
        }

        var reason = RecordingEndReason.Failed;

        try
        {
            CloseEverything();
            var settled = SettleFile(path);

            if (failure is not null)
            {
                reason = IsDiskFull(failure) ? RecordingEndReason.DiskFull : RecordingEndReason.Failed;
            }
            else if (settled)
            {
                lock (_endGate)
                {
                    reason = _endRequest ?? RecordingEndReason.Requested;
                }
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
        }
        finally
        {
            End(reason);
        }
    }

    private void OpenCapture(MonitorArea monitor)
    {
        var handle = Win32.MonitorHandle(monitor);

        if (handle == 0)
        {
            throw new InvalidOperationException("The monitor to record is gone.");
        }

        _direct3D = new Direct3DInterop();
        _item = Direct3DInterop.CreateItemForMonitor(handle);
        _item.Closed += OnItemClosed;

        var size = Probes.CaptureSize ?? _item.Size;
        (_width, _height) = RecordingRules.EncodableSize(size.Width, size.Height);

        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(_direct3D.WinRtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, CaptureBuffers, size);
        _framePool.FrameArrived += OnFrameArrived;
        _session = _framePool.CreateCaptureSession(_item);
        _session.IsCursorCaptureEnabled = true;
        _session.StartCapture();
    }

    /// <summary>Faux si l'arrêt est arrivé avant toute image : rien à écrire.</summary>
    private bool WaitForFirstFrame()
    {
        try
        {
            if (_firstFrame.Wait(FirstFrameTimeout, _stopping.Token))
            {
                return true;
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        throw new InvalidOperationException("The capture sent no frame.");
    }

    private async Task EncodeWithRetryAsync()
    {
        try
        {
            await EncodeAsync(hardware: true).ConfigureAwait(false);
        }
        catch (Exception exception) when (Volatile.Read(ref _samples) < StartConfirmedSamples && !_stopping.IsCancellationRequested && !IsDiskFull(exception))
        {
            // Une seule reprise, en logiciel : une nouvelle panne remonte et termine l'enregistrement.
            Debug.WriteLine(exception);
            DeleteTemporary();
            await EncodeAsync(hardware: false).ConfigureAwait(false);
        }
    }

    private async Task EncodeAsync(bool hardware)
    {
        _sampleFault = null;
        _lastIndex = -1;
        Volatile.Write(ref _samples, 0);
        Probes.UsedHardware = hardware;
        Probes.EncoderStarting?.Invoke(hardware);

        using var textures = new FrameTextures(_direct3D!, _width, _height, Probes);
        _textures = textures;

        try
        {
            using var output = await OpenTemporaryAsync().ConfigureAwait(false);
            var source = CreateSource();
            var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = hardware };
            var prepared = await transcoder.PrepareMediaStreamSourceTranscodeAsync(source, output, CreateProfile(hardware));

            if (!prepared.CanTranscode)
            {
                throw new InvalidOperationException("The encoder was refused: " + prepared.FailureReason);
            }

            _transcode = prepared.TranscodeAsync();

            try
            {
                await _transcode;
            }
            finally
            {
                _transcode = null;
                source.SampleRequested -= OnSampleRequested;
                source.Starting -= OnSourceStarting;
            }

            await output.FlushAsync();

            if (_sampleFault is { } fault)
            {
                throw new InvalidOperationException("A frame could not be sent to the encoder.", fault);
            }
        }
        finally
        {
            _textures = null;
        }
    }

    private async Task<IRandomAccessStream> OpenTemporaryAsync()
    {
        // CreateNew : un fichier déjà là n'est jamais écrasé, et jamais supprimé ensuite.
        new FileStream(_temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite).Dispose();
        _temporaryCreated = true;
        var file = await StorageFile.GetFileFromPathAsync(_temporaryPath);
        return await file.OpenAsync(FileAccessMode.ReadWrite);
    }

    /// <summary>Une seule piste pour l'instant. Le son s'ajoutera en second descripteur, et sera servi selon args.Request.StreamDescriptor.</summary>
    private MediaStreamSource CreateSource()
    {
        var properties = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, (uint)_width, (uint)_height);
        var source = new MediaStreamSource(new VideoStreamDescriptor(properties)) { BufferTime = TimeSpan.Zero };
        source.Starting += OnSourceStarting;
        source.SampleRequested += OnSampleRequested;
        return source;
    }

    private MediaEncodingProfile CreateProfile(bool hardware)
    {
        var profile = new MediaEncodingProfile { Audio = null };
        profile.Container.Subtype = MediaEncodingSubtypes.Mpeg4;
        profile.Video.Subtype = MediaEncodingSubtypes.H264;
        profile.Video.Width = (uint)_width;
        profile.Video.Height = (uint)_height;
        profile.Video.Bitrate = (uint)RecordingRules.VideoBitRate(_width, _height);
        profile.Video.FrameRate.Numerator = FramesPerSecond;
        profile.Video.FrameRate.Denominator = 1;
        profile.Video.PixelAspectRatio.Numerator = 1;
        profile.Video.PixelAspectRatio.Denominator = 1;
        profile.Video.Properties[MtYuvMatrix] = Bt709Matrix;
        profile.Video.Properties[MtVideoPrimaries] = Bt709Primaries;
        profile.Video.Properties[MtTransferFunction] = Bt709Transfer;
        profile.Video.Properties[MtNominalRange] = StudioRange;

        // En logiciel, l'encodeur réserve 1,2 Go d'avance en 1440p pour regarder les images à venir ; ce mode le ramène à 300 Mo.
        // Sans gain en matériel : l'encodeur garde là ses réglages habituels.
        if (!hardware)
        {
            profile.Video.Properties[LowLatency] = 1u;
        }

        return profile;
    }

    private void OnSourceStarting(MediaStreamSource sender, MediaStreamSourceStartingEventArgs args) =>
        args.Request.SetActualStartPosition(TimeSpan.Zero);

    private void OnSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
    {
        try
        {
            args.Request.Sample = NextVideoSample();
        }
        catch (Exception exception)
        {
            // La vidéo se clôt normalement, sans signaler d'erreur à l'encodeur : ce qui est déjà écrit reste lisible.
            Debug.WriteLine(exception);
            _sampleFault = exception;
            args.Request.Sample = null;
        }
    }

    /// <summary>Renvoie null pour clore la vidéo. Bloque le fil de l'encodeur jusqu'à l'heure de l'image.</summary>
    private MediaStreamSample? NextVideoSample()
    {
        Probes.BeforeSample?.Invoke(_lastIndex + 1);
        var isFirst = _lastIndex < 0;

        if (isFirst)
        {
            // L'heure zéro est la demande de la première image : la vidéo commence sans le temps de préparation de l'encodeur.
            _clock.Restart();
            _running.TrySetResult();
        }

        var stop = _stopping.Token;
        var dueTicks = (_lastIndex + 1) * TicksPerFrame;

        while (!stop.IsCancellationRequested)
        {
            var remaining = dueTicks - _clock.Elapsed.Ticks;

            if (remaining <= 0)
            {
                break;
            }

            stop.WaitHandle.WaitOne(Math.Max(1, (int)(remaining / TimeSpan.TicksPerMillisecond)));
        }

        // Une vidéo arrêtée tout de suite garde une image : sans échantillon, l'encodeur échoue.
        if (stop.IsCancellationRequested && !isFirst)
        {
            return null;
        }

        var index = _clock.Elapsed.Ticks / TicksPerFrame;
        var textures = _textures!;
        var texture = isFirst ? textures.Take(CancellationToken.None, FrameWaitTimeout) : textures.Take(stop, FrameWaitTimeout);

        if (texture is null)
        {
            return null;
        }

        CopyLatestTo(texture.Texture);

        var sample = MediaStreamSample.CreateFromDirect3D11Surface(texture.Surface, TimeSpan.FromTicks(index * TicksPerFrame));
        sample.Duration = TimeSpan.FromTicks(TicksPerFrame);

        // Cet événement rend la texture : le nombre de textures en attente de l'encodeur reste borné.
        // L'échantillon reste référencé jusque-là : si le ramasse-miettes collecte son enveloppe, l'événement est perdu.
        texture.Sample = sample;
        sample.Processed += (_, _) => textures.Return(texture);

        Probes.FramesSkipped += (int)(index - _lastIndex - 1);
        _lastIndex = index;
        Interlocked.Increment(ref _samples);
        Probes.Samples++;
        return sample;
    }

    private void CopyLatestTo(nint destination)
    {
        lock (_frameGate)
        {
            if (_latest is null)
            {
                return;
            }

            var source = Direct3DInterop.TextureOf(_latest.Surface);

            try
            {
                _direct3D!.CopyTopLeft(destination, source, _width, _height);
            }
            finally
            {
                Direct3DInterop.Release(source);
            }
        }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        try
        {
            lock (_frameGate)
            {
                // Une image peut encore arriver pendant la fermeture : le verrou évite de la garder après la fin.
                if (_captureClosed)
                {
                    return;
                }

                var frame = sender.TryGetNextFrame();

                if (frame is null)
                {
                    return;
                }

                _latest?.Dispose();
                _latest = frame;
            }

            Probes.FramesArrived++;
            _firstFrame.Set();
        }
        catch (Exception exception)
        {
            // Rien ne doit remonter dans le fil de la capture : l'image suivante remplacera celle-ci.
            Debug.WriteLine(exception);
        }
    }

    private void OnItemClosed(GraphicsCaptureItem sender, object args) => RequestEnd(RecordingEndReason.MonitorClosed);

    private void RequestEnd(RecordingEndReason reason)
    {
        lock (_endGate)
        {
            if (_endRequest is not null)
            {
                return;
            }

            _endRequest = reason;
        }

        _stopping.Cancel();
        _ = Task.Run(WatchEndAsync);
    }

    /// <summary>La fin tient en 15 s : au-delà, l'encodeur est annulé puis abandonné, et l'enregistrement a échoué.</summary>
    private async Task WatchEndAsync()
    {
        if (await Task.WhenAny(_finished.Task, Task.Delay(StopTimeout - CancelGrace)).ConfigureAwait(false) == _finished.Task)
        {
            return;
        }

        Debug.WriteLine("The encoder did not stop in time.");

        try
        {
            _transcode?.Cancel();
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
        }

        if (await Task.WhenAny(_finished.Task, Task.Delay(CancelGrace)).ConfigureAwait(false) != _finished.Task)
        {
            End(RecordingEndReason.Failed);
        }
    }

    private void CloseEverything()
    {
        Try(() =>
        {
            if (_item is not null)
            {
                _item.Closed -= OnItemClosed;
            }
        });
        Try(() =>
        {
            if (_framePool is not null)
            {
                _framePool.FrameArrived -= OnFrameArrived;
            }
        });
        lock (_frameGate)
        {
            _captureClosed = true;
            Try(() => _latest?.Dispose());
            _latest = null;
        }

        Try(() => _session?.Dispose());
        Try(() => _framePool?.Dispose());

        Try(() => _direct3D?.Dispose());
        _session = null;
        _framePool = null;
        _item = null;
        _direct3D = null;

        static void Try(Action action)
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                Debug.WriteLine(exception);
            }
        }
    }

    /// <summary>
    /// Un fichier lisible prend son nom final ; un fichier illisible est supprimé.
    /// Renvoie vrai si la vidéo est à sa place, ou s'il n'y avait rien à écrire.
    /// </summary>
    private bool SettleFile(string path)
    {
        if (!_temporaryCreated)
        {
            return true;
        }

        bool complete;

        try
        {
            using var file = new FileStream(_temporaryPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            complete = RecordingRules.IsCompleteMp4(file);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine(exception);
            complete = false;
        }

        if (!complete)
        {
            DeleteTemporary();
            return false;
        }

        // Le fichier reste en .tmp si son nom final est pris : une vidéo lisible n'est jamais perdue.
        File.Move(_temporaryPath, path, overwrite: false);
        _temporaryCreated = false;
        return true;
    }

    private void DeleteTemporary()
    {
        if (!_temporaryCreated)
        {
            return;
        }

        try
        {
            File.Delete(_temporaryPath);
            _temporaryCreated = false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine(exception);
        }
    }

    private static bool IsDiskFull(Exception exception)
    {
        const int DiskFull = unchecked((int)0x80070070), HandleDiskFull = unchecked((int)0x80070027);

        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current.HResult is DiskFull or HandleDiskFull)
            {
                return true;
            }
        }

        return false;
    }

    private void End(RecordingEndReason reason)
    {
        if (Interlocked.Exchange(ref _endRaised, 1) != 0)
        {
            return;
        }

        lock (_endGate)
        {
            _endRequest ??= reason;
        }

        _stopping.Cancel();

        try
        {
            Ended?.Invoke(reason);
        }
        catch (Exception exception)
        {
            // Une erreur de l'abonné ne doit ni laisser l'appelant attendre, ni fermer l'application depuis un fil de fond.
            Debug.WriteLine(exception);
        }
        finally
        {
            _running.TrySetResult();
            _finished.TrySetResult();
        }
    }
}

/// <summary>
/// Textures de copie, réutilisées et en nombre borné : si l'encodeur prend du retard, des images sont sautées
/// au lieu de remplir la mémoire. Une par image alloue et libère sans cesse 15 Mo en 1440p.
/// </summary>
internal sealed class FrameTextures : IDisposable
{
    // L'encodeur en garde 2 ou 3 d'ordinaire, et n'en rend une qu'en recevant la suivante : une seule le bloquerait.
    // Le reste est la marge d'un encodeur en retard, avant de sauter des images.
    private const int Capacity = 8;

    private readonly object _gate = new();
    private readonly Direct3DInterop _direct3D;
    private readonly int _width;
    private readonly int _height;
    private readonly RecorderProbes _probes;
    private readonly SemaphoreSlim _available;
    private readonly List<FrameTexture> _all = [];
    private readonly Stack<FrameTexture> _free = new();
    private int _pending;
    private bool _disposed;

    public FrameTextures(Direct3DInterop direct3D, int width, int height, RecorderProbes probes)
    {
        _direct3D = direct3D;
        _width = width;
        _height = height;
        _probes = probes;
        _available = new SemaphoreSlim(Capacity, Capacity);
    }

    /// <summary>Renvoie null à l'arrêt. Lève si l'encodeur garde tout trop longtemps : il est bloqué.</summary>
    public FrameTexture? Take(CancellationToken stop, TimeSpan timeout)
    {
        try
        {
            if (!_available.Wait(timeout, stop))
            {
                throw new TimeoutException("The encoder keeps every frame.");
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _pending++;
            _probes.PeakPending = Math.Max(_probes.PeakPending, _pending);

            if (!_free.TryPop(out var texture))
            {
                texture = new FrameTexture(_direct3D.CreateTexture(_width, _height));
                _all.Add(texture);
                _probes.TexturesCreated = _all.Count;
            }

            texture.IsPending = true;
            return texture;
        }
    }

    public void Return(FrameTexture texture)
    {
        lock (_gate)
        {
            if (_disposed || !texture.IsPending)
            {
                return;
            }

            texture.IsPending = false;
            texture.Sample = null;
            _pending--;
            _free.Push(texture);
            _available.Release();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;

            foreach (var texture in _all)
            {
                texture.Dispose();
            }

            _all.Clear();
            _free.Clear();
        }

        _available.Dispose();
    }
}

internal sealed class FrameTexture : IDisposable
{
    public FrameTexture(nint texture)
    {
        Texture = texture;
        Surface = Direct3DInterop.SurfaceOf(texture);
    }

    public nint Texture { get; }

    public IDirect3DSurface Surface { get; }

    public bool IsPending { get; set; }

    public MediaStreamSample? Sample { get; set; }

    public void Dispose()
    {
        Surface.Dispose();
        Direct3DInterop.Release(Texture);
    }
}

/// <summary>Ce que le banc d'essai peut forcer et mesurer. Rien de tout cela n'est utilisé par l'application.</summary>
internal sealed class RecorderProbes
{
    /// <summary>Appelé avant chaque essai d'encodeur, avec vrai pour le matériel. Une exception simule son refus.</summary>
    public Action<bool>? EncoderStarting { get; set; }

    /// <summary>Appelé sur le fil de l'encodeur avant chaque image. Une exception simule une panne, une attente un encodeur bloqué.</summary>
    public Action<long>? BeforeSample { get; set; }

    /// <summary>Taille de capture imposée, pour essayer un écran de taille impaire sans toucher aux réglages.</summary>
    public SizeInt32? CaptureSize { get; set; }

    public bool UsedHardware { get; set; }

    public int Samples { get; set; }

    public int FramesArrived { get; set; }

    public int FramesSkipped { get; set; }

    public int PeakPending { get; set; }

    public int TexturesCreated { get; set; }
}
