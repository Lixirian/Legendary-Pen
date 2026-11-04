using System.Diagnostics;
using System.IO;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
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
/// Le son choisi s'ajoute en second flux, sur la même horloge. Il ne fait jamais échouer un enregistrement : s'il manque,
/// la vidéo se fait sans lui. <see cref="SoundLost"/> le dit dès que c'est décidé, et <see cref="AudioProblem"/> le redit à la fin.
///
/// Une instance ne sert qu'une fois. <see cref="StartAsync"/> ne lève que si l'enregistrement ne peut pas être tenté.
/// Toute autre fin, y compris une erreur au démarrage, passe par <see cref="Ended"/>, levé une seule fois, sur une tâche à
/// lui : l'appelant le ramène sur son fil. <see cref="StopAsync"/> se termine avant cet événement, et un fil d'interface
/// qui l'attend ne peut donc pas se bloquer. À ce moment, les fichiers et l'encodeur sont terminés, sauf après un abandon :
/// la capture est alors fermée, et l'encodeur finit seul. S'il rend encore un fichier lisible, celui-ci prend son nom final.
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
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CancelGrace = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan CloseCaptureLimit = TimeSpan.FromSeconds(1);

    // L'abandon finit avant la limite de RecordingController : à 15 s, le fichier gardé doit déjà être annoncé.
    private static readonly TimeSpan AbandonMargin = TimeSpan.FromMilliseconds(200);

    // Attributs Media Foundation, depuis mfapi.h.
    private static readonly Guid MtYuvMatrix = new("3e23d450-2c75-4d25-a00e-b91670d12327");
    private static readonly Guid MtVideoPrimaries = new("dbfbe4d7-0740-4ee0-8192-850ab0e21935");
    private static readonly Guid MtTransferFunction = new("5fb0fce9-be5c-4935-a811-ec838f8eed93");
    private static readonly Guid MtNominalRange = new("c21b8ee5-b956-4071-8daf-325edf5cab11");
    private static readonly Guid LowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    private const uint Bt709Matrix = 1, Bt709Primaries = 2, Bt709Transfer = 5, StudioRange = 2;

    private const uint AudioBitRate = 192_000;

    private readonly object _frameGate = new();
    private readonly object _endGate = new();
    private readonly object _fileGate = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly TaskCompletionSource _running = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ManualResetEventSlim _firstFrame = new(false);

    private Direct3DInterop? _direct3D;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private Direct3D11CaptureFrame? _latest;
    private volatile EncodingAttempt? _attempt;
    private volatile bool _abandoned;
    private volatile IAsyncActionWithProgress<double>? _transcode;
    private volatile string? _savedPath;
    private volatile bool _mayBeIncomplete;
    private volatile bool _temporaryCreated;
    private bool _fileSettled;
    private volatile RecordingAudioProblem _audioProblem;
    private RecordingAudio _audioKind;
    private string _temporaryPath = "";
    private bool _captureClosed;
    private bool _stoppingDisposed;
    private SizeInt32 _contentSize;
    private int _width;
    private int _height;
    private int _startCalled;
    private int _endRaised;
    private RecordingEndReason? _endRequest;

    public event Action<RecordingEndReason>? Ended;

    /// <summary>
    /// Cet essai, ou le suivant, n'a pas de son : lire <see cref="AudioProblem"/>. Peut être levé plusieurs fois, sur une tâche à
    /// lui, et dès avant la première image : le son d'un essai suivant se décide après la fin de <see cref="StartAsync"/>.
    /// </summary>
    public event Action? SoundLost;

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

    /// <summary>
    /// Le fichier où se trouve la vidéo, fixé avant <see cref="Ended"/> ; null s'il n'y en a pas. C'est le « .tmp » si le nom
    /// final est pris, ou si le fichier n'a pas pu être confirmé comme une vidéo finie. Après un abandon, il peut encore changer :
    /// le « .tmp » annoncé prend son nom final si l'encodeur finit plus tard.
    /// </summary>
    public string? SavedPath => _savedPath;

    /// <summary>Vrai si le fichier gardé n'a pas pu être confirmé comme une vidéo finie : le message le dit.</summary>
    public bool MayBeIncomplete => _mayBeIncomplete;

    /// <summary>Ce qui est arrivé au son, fixé avant <see cref="Ended"/> si la vidéo a suivi son cours.</summary>
    public RecordingAudioProblem AudioProblem => _audioProblem;

    /// <summary>Points d'essai du banc. Tous inactifs en usage normal.</summary>
    internal RecorderProbes Probes { get; } = new();

    /// <summary>
    /// Se termine quand l'encodeur demande sa première image, ou quand l'enregistrement a déjà pris fin : en 15 s au plus,
    /// au-delà de quoi l'enregistrement a échoué.
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

        _audioKind = audio;

        // Tout le travail se fait hors du fil de l'appelant : ni la capture ni l'encodeur ne touchent l'interface.
        _ = Task.Run(() => RunAsync(monitor, path));
        _ = Task.Run(WatchStartAsync);
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

        // Une panne sans demande d'arrêt : la fermeture qui suit peut bloquer, et la limite de temps doit donc aussi la couvrir.
        if (failure is not null)
        {
            RequestEnd(RecordingEndReason.Failed);
        }

        try
        {
            CloseEverything();
            var accounted = SettleFile(path, failure is not null && IsDiskFull(failure));

            if (failure is not null)
            {
                reason = IsDiskFull(failure) ? RecordingEndReason.DiskFull : RecordingEndReason.Failed;
            }
            else if (accounted)
            {
                lock (_endGate)
                {
                    reason = _endRequest ?? RecordingEndReason.Requested;
                }
            }
        }
        catch (Exception exception)
        {
            // Quoi qu'il soit arrivé ici, un fichier encore en place reste annoncé.
            Debug.WriteLine(exception);
            PublishTemporary();
        }
        finally
        {
            End(reason);
            DisposeSignals();
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

        _contentSize = _item.Size;
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
        // Une valeur inconnue ne vaut pas un choix : sans demande claire, aucun périphérique de son ne s'ouvre.
        var withSound = _audioKind is RecordingAudio.System or RecordingAudio.Microphone;

        try
        {
            await EncodeAsync(hardware: true, withSound).ConfigureAwait(false);
        }
        catch (Exception exception) when (CanRetry(exception))
        {
            // Une seule reprise, en logiciel : une nouvelle panne remonte et termine l'enregistrement.
            Debug.WriteLine(exception);
            DeleteTemporary();

            try
            {
                await EncodeAsync(hardware: false, withSound).ConfigureAwait(false);
            }
            catch (Exception again) when (withSound && CanRetry(again))
            {
                // Le son ne fait jamais échouer une vidéo : si c'est peut-être lui, un dernier essai se passe de lui.
                Debug.WriteLine(again);
                DeleteTemporary();

                // Avant cet essai, qui peut durer toute la vidéo : découvrir à la fin qu'elle est muette serait trop tard.
                _audioProblem = Unavailable();
                RaiseSoundLost();
                await EncodeAsync(hardware: false, withSound: false).ConfigureAwait(false);
            }
        }
    }

    private RecordingAudioProblem Unavailable() =>
        _audioKind == RecordingAudio.Microphone ? RecordingAudioProblem.MicrophoneUnavailable : RecordingAudioProblem.SystemSoundUnavailable;

    // Mesuré en temps de vidéo, pas en images envoyées : un encodeur lent en saute beaucoup, et la reprise efface le fichier.
    private bool CanRetry(Exception exception) =>
        !_stopping.IsCancellationRequested && !IsDiskFull(exception) && _attempt is not { DeviceLost: true } && (_attempt?.LastIndex ?? -1) + 1 < StartConfirmedSamples;

    private void RaiseSoundLost()
    {
        _ = Task.Run(() =>
        {
            try
            {
                SoundLost?.Invoke();
            }
            catch (Exception exception)
            {
                // Une erreur de l'abonné ne doit pas fermer l'application depuis un fil de fond.
                Debug.WriteLine(exception);
            }
        });
    }

    private AudioSource? OpenAudio()
    {
        var audio = AudioSource.TryStart(_audioKind, Probes);
        _audioProblem = audio is null ? Unavailable() : RecordingAudioProblem.None;

        if (audio is null)
        {
            RaiseSoundLost();
        }

        return audio;
    }

    private async Task EncodeAsync(bool hardware, bool withSound)
    {
        Probes.UsedHardware = hardware;
        Probes.UsedAudio = withSound;
        Probes.EncoderStarting?.Invoke(hardware);

        if (_stopping.IsCancellationRequested)
        {
            return;
        }

        var audio = withSound ? OpenAudio() : null;

        // L'ouverture peut durer 3 s : un arrêt arrivé entre-temps relâche le micro tout de suite.
        if (_stopping.IsCancellationRequested)
        {
            audio?.Finish();
            return;
        }

        var attempt = new EncodingAttempt(new FrameTextures(_direct3D!, _width, _height, Probes), _stopping.Token, audio);
        _attempt = attempt;

        try
        {
            using var output = await OpenTemporaryAsync().ConfigureAwait(false);
            Probes.OutputOpened?.Invoke(output);
            TypedEventHandler<MediaStreamSource, MediaStreamSourceSampleRequestedEventArgs> onSample = (_, args) => OnSampleRequested(attempt, args);
            var source = CreateSource(audio is not null);
            source.SampleRequested += onSample;
            StartServing(attempt);
            var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = hardware };
            var prepared = await transcoder.PrepareMediaStreamSourceTranscodeAsync(source, output, CreateProfile(hardware, audio is not null));

            if (!prepared.CanTranscode)
            {
                throw new InvalidOperationException("The encoder was refused: " + prepared.FailureReason);
            }

            var transcode = prepared.TranscodeAsync();
            _transcode = transcode;

            try
            {
                await transcode;
            }
            finally
            {
                _transcode = null;
                source.SampleRequested -= onSample;
                source.Starting -= OnSourceStarting;
                CloseOperation(transcode);
            }

            await output.FlushAsync();
            Probes.AfterEncoding?.Invoke();

            if (attempt.Fault is { } fault)
            {
                throw new InvalidOperationException("A frame could not be sent to the encoder.", fault);
            }
        }
        finally
        {
            // Une demande d'image peut survivre à la fin de l'encodeur : elle ne doit plus rien toucher une fois l'essai fermé.
            attempt.Close();

            if (audio is { DeviceLost: true } || attempt.AudioFailed)
            {
                _audioProblem = RecordingAudioProblem.Interrupted;
            }
        }
    }

    private static void CloseOperation(IAsyncInfo operation)
    {
        try
        {
            operation.Close();
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
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

    private MediaStreamSource CreateSource(bool withSound)
    {
        var properties = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, (uint)_width, (uint)_height);
        var video = new VideoStreamDescriptor(properties);
        var source = withSound ? new MediaStreamSource(video, new AudioStreamDescriptor(AudioSource.Format)) : new MediaStreamSource(video);
        source.BufferTime = TimeSpan.Zero;
        source.Starting += OnSourceStarting;
        return source;
    }

    private MediaEncodingProfile CreateProfile(bool hardware, bool withSound)
    {
        var profile = new MediaEncodingProfile
        {
            Audio = withSound ? AudioEncodingProperties.CreateAac(AudioTimeline.FramesPerSecond, AudioTimeline.Channels, AudioBitRate) : null,
        };
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

    /// <summary>
    /// La source fait toutes ses demandes l'une après l'autre, sur un même fil, en alternant image et son : attendre ici l'heure
    /// d'une image priverait le son de ses demandes, à raison d'une par image. Chaque demande part donc aussitôt, différée,
    /// vers le fil de son flux, et les deux flux ne s'attendent jamais.
    /// </summary>
    private static void OnSampleRequested(EncodingAttempt attempt, MediaStreamSourceSampleRequestedEventArgs args)
    {
        try
        {
            var request = args.Request;
            var pending = new PendingSample(request, request.GetDeferral());

            if (!attempt.TryPost(pending, request.StreamDescriptor is AudioStreamDescriptor))
            {
                Answer(pending, null);
            }
        }
        catch (Exception exception)
        {
            // La demande est morte avec son encodeur : il n'y a plus personne à prévenir.
            Debug.WriteLine(exception);
        }
    }

    private static void Answer(PendingSample pending, MediaStreamSample? sample)
    {
        try
        {
            pending.Request.Sample = sample;
        }
        catch (Exception exception)
        {
            // La demande est morte avec son encodeur : il n'y a plus personne à prévenir.
            Debug.WriteLine(exception);
        }

        try
        {
            pending.Deferral.Complete();
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
        }
    }

    /// <summary>Un fil par flux, le temps de l'essai. Chacun s'arrête de lui-même quand l'essai est fermé, ou abandonné, et ses demandes servies.</summary>
    private void StartServing(EncodingAttempt attempt)
    {
        attempt.Enter();
        new Thread(() => ServeVideo(attempt)) { IsBackground = true, Name = "Legendary Pen video samples" }.Start();

        if (attempt.Audio is not null)
        {
            attempt.Enter();
            new Thread(() => ServeAudio(attempt)) { IsBackground = true, Name = "Legendary Pen audio samples" }.Start();
        }

        // L'abandon a pu passer juste avant que cet essai soit connu.
        if (_abandoned)
        {
            attempt.StopServing();
        }
    }

    private void ServeVideo(EncodingAttempt attempt)
    {
        try
        {
            foreach (var pending in attempt.VideoRequests.GetConsumingEnumerable())
            {
                MediaStreamSample? sample = null;

                try
                {
                    sample = NextVideoSample(attempt);
                }
                catch (Exception exception)
                {
                    // La vidéo se clôt normalement, sans signaler d'erreur à l'encodeur : ce qui est déjà écrit reste lisible.
                    Debug.WriteLine(exception);
                    attempt.Fault ??= exception;
                }

                // Sans vidéo, l'encodeur attendrait la fin du son pour finir : elle vient avec celle de l'image, quelle qu'en soit la cause.
                if (sample is null)
                {
                    attempt.Audio?.Finish();
                }

                Answer(pending, sample);
                attempt.Leave();
            }
        }
        finally
        {
            Probes.ServerEnded();
            attempt.Leave();
        }
    }

    private void ServeAudio(EncodingAttempt attempt)
    {
        try
        {
            foreach (var pending in attempt.AudioRequests.GetConsumingEnumerable())
            {
                MediaStreamSample? sample = null;

                try
                {
                    sample = NextAudioSample(attempt.Audio!);
                }
                catch (Exception exception)
                {
                    // Le son s'arrête là, et la vidéo continue seule.
                    Debug.WriteLine(exception);
                    attempt.AudioFailed = true;
                    attempt.Audio!.Finish();
                }

                Answer(pending, sample);
                attempt.Leave();
            }
        }
        finally
        {
            Probes.ServerEnded();
            attempt.Leave();
        }
    }

    /// <summary>Renvoie null à la fin du son. N'attend que le son : l'arrêt le ferme en un temps borné, même si la capture est bloquée.</summary>
    private MediaStreamSample? NextAudioSample(AudioSource audio)
    {
        while (true)
        {
            if (audio.TryRead(out var pcm, out var timestamp))
            {
                Probes.BeforeAudioSample?.Invoke(timestamp);
                var sample = MediaStreamSample.CreateFromBuffer(pcm.AsBuffer(), timestamp);
                sample.Duration = AudioTimeline.TimeOf(pcm.Length / AudioTimeline.BytesPerFrame);
                return sample;
            }

            if (audio.IsFinished)
            {
                return null;
            }

            Thread.Sleep(2);
        }
    }

    /// <summary>Renvoie null pour clore la vidéo. Bloque le fil des images jusqu'à l'heure de l'image.</summary>
    private MediaStreamSample? NextVideoSample(EncodingAttempt attempt)
    {
        Probes.BeforeSample?.Invoke(attempt.LastIndex + 1);
        var isFirst = attempt.LastIndex < 0;

        if (isFirst)
        {
            // L'heure zéro est la demande de la première image : la vidéo commence sans le temps de préparation de l'encodeur.
            attempt.Clock.Restart();
            attempt.Audio?.SetOrigin(Stopwatch.GetTimestamp());
            _running.TrySetResult();
        }

        var stop = attempt.Stop;
        var dueTicks = (attempt.LastIndex + 1) * TicksPerFrame;

        while (!stop.IsCancellationRequested)
        {
            var remaining = dueTicks - attempt.Clock.Elapsed.Ticks;

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

        var textures = attempt.Textures;

        if (!textures.WaitForFree(isFirst ? CancellationToken.None : stop, FrameWaitTimeout))
        {
            return null;
        }

        var index = attempt.Clock.Elapsed.Ticks / TicksPerFrame;
        MediaStreamSample sample;

        // Rien de natif hors de ce verrou : la fermeture de l'essai l'attend avant de libérer les textures.
        lock (attempt.Gate)
        {
            if (attempt.IsClosed)
            {
                return null;
            }

            var texture = textures.TakeFree();

            if (!CopyLatestTo(texture.Texture))
            {
                return null;
            }

            var removed = Probes.DeviceRemovedReason?.Invoke(attempt.Samples) ?? _direct3D!.DeviceRemovedReason();

            if (removed != 0)
            {
                // Sans échantillon, un encodeur encore vivant écrit son index : la vidéo jusque-là reste lisible.
                attempt.DeviceLost = true;
                attempt.Fault ??= new InvalidOperationException("The graphics device was removed: 0x" + removed.ToString("X8"));
                return null;
            }

            sample = MediaStreamSample.CreateFromDirect3D11Surface(texture.Surface, TimeSpan.FromTicks(index * TicksPerFrame));
            sample.Duration = TimeSpan.FromTicks(TicksPerFrame);

            // Cet événement rend la texture : le nombre de textures en attente de l'encodeur reste borné.
            // L'échantillon reste référencé jusque-là : si le ramasse-miettes collecte son enveloppe, l'événement est perdu.
            texture.Sample = sample;
            sample.Processed += (_, _) => textures.Return(texture);
        }

        Probes.FramesSkipped += (int)(index - attempt.LastIndex - 1);
        attempt.LastIndex = index;
        attempt.Samples++;
        Probes.Samples++;
        return sample;
    }

    /// <summary>Faux si la capture est déjà fermée : il n'y a plus d'image à copier.</summary>
    private bool CopyLatestTo(nint destination)
    {
        lock (_frameGate)
        {
            if (_latest is null)
            {
                return false;
            }

            var source = Direct3DInterop.TextureOf(_latest.Surface);

            try
            {
                Probes.InsideCopy?.Invoke();
                _direct3D!.CopyTopLeft(destination, source, _width, _height);
                return true;
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
            var changed = false;

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

                var size = Probes.ContentSize?.Invoke(frame.ContentSize) ?? frame.ContentSize;

                if (size.Width != _contentSize.Width || size.Height != _contentSize.Height)
                {
                    // Cette image ne tient plus dans la vidéo : la dernière bonne reste en place jusqu'à la fin.
                    changed = true;
                    frame.Dispose();
                }
                else
                {
                    _latest?.Dispose();
                    _latest = frame;
                }
            }

            if (changed)
            {
                RequestEnd(RecordingEndReason.ScreenChanged);
                return;
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

        CancelStopping();
        _ = Task.Run(WatchEndAsync);
    }

    /// <summary>La fin tient en 15 s : au-delà, l'encodeur est annulé puis abandonné, et l'enregistrement a échoué.</summary>
    private async Task WatchEndAsync()
    {
        if (await Task.WhenAny(_finished.Task, Task.Delay(StopTimeout - CancelGrace - CloseCaptureLimit - AbandonMargin)).ConfigureAwait(false) == _finished.Task)
        {
            return;
        }

        Debug.WriteLine("The encoder did not stop in time.");
        _ = Task.Run(CancelTranscode);

        if (await Task.WhenAny(_finished.Task, Task.Delay(CancelGrace)).ConfigureAwait(false) != _finished.Task)
        {
            await AbandonAsync().ConfigureAwait(false);
        }
    }

    private async Task WatchStartAsync()
    {
        if (await Task.WhenAny(_running.Task, Task.Delay(StartTimeout)).ConfigureAwait(false) == _running.Task)
        {
            return;
        }

        Debug.WriteLine("The recording did not start in time.");
        _ = Task.Run(CancelTranscode);
        await AbandonAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Annonce l'échec quoi qu'il arrive : un pilote bloqué peut retenir le verrou de la copie ou la fermeture de la capture.
    /// Celle-ci continue alors en arrière-plan, et se termine quand le pilote rend la main.
    /// </summary>
    private async Task AbandonAsync()
    {
        // Un encodeur bloqué ne rendra peut-être jamais la main : les fils d'échantillons n'attendent plus ses demandes.
        _abandoned = true;
        _attempt?.StopServing();

        var hasContent = Task.Run(TemporaryHasContent);
        await Task.WhenAny(Task.WhenAll(Task.Run(CloseCapture), hasContent), Task.Delay(CloseCaptureLimit)).ConfigureAwait(false);

        // Le fichier reste sur le disque : le message doit le dire, même si un dossier bloqué empêche de le regarder.
        if (!hasContent.IsCompletedSuccessfully || hasContent.Result)
        {
            PublishTemporary();
        }

        End(RecordingEndReason.Failed);
    }

    private bool TemporaryHasContent()
    {
        if (!_temporaryCreated)
        {
            return false;
        }

        try
        {
            using var file = new FileStream(_temporaryPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return file.Length > 0;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            Debug.WriteLine(exception);
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Illisible n'est pas vide : dans le doute, le fichier est annoncé.
            Debug.WriteLine(exception);
            return true;
        }
    }

    /// <summary>Annonce le « .tmp » tant que son sort n'est pas fixé. La fin de l'encodeur, même plus tard, a le dernier mot.</summary>
    private void PublishTemporary()
    {
        lock (_fileGate)
        {
            if (!_fileSettled && _temporaryCreated)
            {
                _mayBeIncomplete = true;
                _savedPath = _temporaryPath;
            }
        }
    }

    private void CancelTranscode()
    {
        try
        {
            _transcode?.Cancel();
        }
        catch (Exception exception)
        {
            // L'opération vient peut-être de se terminer d'elle-même.
            Debug.WriteLine(exception);
        }
    }

    /// <summary>
    /// Ferme la capture : plus d'image, plus de cadre jaune. Sans effet la seconde fois, sauf sur ce qu'un démarrage encore
    /// en cours aurait ouvert entre-temps. Le périphérique reste : seule la fin de l'encodeur le libère.
    /// </summary>
    private void CloseCapture()
    {
        GraphicsCaptureItem? item;
        Direct3D11CaptureFramePool? framePool;
        GraphicsCaptureSession? session;

        lock (_frameGate)
        {
            _captureClosed = true;
            Try(() => _latest?.Dispose());
            _latest = null;
            (item, _item) = (_item, null);
            (framePool, _framePool) = (_framePool, null);
            (session, _session) = (_session, null);
        }

        Try(() =>
        {
            if (item is not null)
            {
                item.Closed -= OnItemClosed;
            }
        });
        Try(() =>
        {
            if (framePool is not null)
            {
                framePool.FrameArrived -= OnFrameArrived;
            }
        });
        Try(() => session?.Dispose());
        Try(() => framePool?.Dispose());
    }

    private void CloseEverything()
    {
        CloseCapture();
        Try(() => _direct3D?.Dispose());
        _direct3D = null;
        _attempt = null;
    }

    private static void Try(Action action)
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

    /// <summary>
    /// Fixe le sort du fichier, sans jamais perdre une vidéo : voir <see cref="RecordingFile"/>.
    /// Renvoie vrai si la vidéo est quelque part, même sous son nom temporaire, ou s'il n'y avait rien à écrire. Faux si un
    /// fichier écrit a disparu : c'est le seul vrai échec sans exception, et les autres fins gardent leur raison.
    /// </summary>
    private bool SettleFile(string path, bool diskFull)
    {
        if (!_temporaryCreated)
        {
            lock (_fileGate)
            {
                _fileSettled = true;
                _savedPath = null;
            }

            return true;
        }

        Probes.BeforeSettle?.Invoke(_temporaryPath);
        var outcome = RecordingFile.Settle(_temporaryPath, path, diskFull);

        lock (_fileGate)
        {
            _fileSettled = true;
            _mayBeIncomplete = outcome.MayBeIncomplete;
            _savedPath = outcome.SavedPath;
            _temporaryCreated = outcome.SavedPath == _temporaryPath;
        }

        return !outcome.IsLost;
    }

    // Seulement pour une reprise : l'essai abandonné a moins d'une seconde de vidéo, et le suivant réécrit ce même fichier.
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

        CancelStopping();
        _running.TrySetResult();
        _finished.TrySetResult();

        // Sur sa propre tâche, après la fin de l'attente : un abonné lent ou bloqué ne retient ni StopAsync ni la fermeture.
        _ = Task.Run(() =>
        {
            try
            {
                Ended?.Invoke(reason);
            }
            catch (Exception exception)
            {
                // Une erreur de l'abonné ne doit pas fermer l'application depuis un fil de fond.
                Debug.WriteLine(exception);
            }
        });
    }

    private void CancelStopping()
    {
        lock (_endGate)
        {
            if (!_stoppingDisposed)
            {
                _stopping.Cancel();
            }
        }
    }

    private void DisposeSignals()
    {
        lock (_endGate)
        {
            _stoppingDisposed = true;
            _stopping.Dispose();
        }

        _firstFrame.Dispose();
    }
}

/// <summary>
/// Une demande de l'encodeur, en attente de son échantillon. Tenue ici, elle reste vivante jusqu'à la réponse.
/// </summary>
internal sealed record PendingSample(MediaStreamSourceSampleRequest Request, MediaStreamSourceSampleRequestDeferral Deferral);

/// <summary>
/// Un essai d'encodage et tout ce que ses demandes d'image touchent. Une demande peut survivre à son essai : elle écrit ici
/// et jamais dans l'essai suivant, et ne touche plus aux textures une fois l'essai fermé.
/// </summary>
internal sealed class EncodingAttempt
{
    private readonly CancellationTokenSource _closing;

    // Séparé de Gate, que la copie tient tout du long : un pilote bloqué ne doit pas retenir le fil des demandes de l'encodeur.
    private readonly object _requestGate = new();
    private bool _acceptsRequests = true;
    private int _users = 1;

    public EncodingAttempt(FrameTextures textures, CancellationToken stopping, AudioSource? audio)
    {
        Textures = textures;
        Audio = audio;
        _closing = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        Stop = _closing.Token;

        // Dès l'arrêt, le micro est relâché, même si l'encodeur traîne ensuite.
        if (audio is not null)
        {
            Stop.Register(audio.Finish);
        }
    }

    public object Gate { get; } = new();

    public FrameTextures Textures { get; }

    /// <summary>Le son de cet essai ; null sans son. L'essai le ferme, et un nouvel essai en rouvre un.</summary>
    public AudioSource? Audio { get; }

    public BlockingCollection<PendingSample> VideoRequests { get; } = [];

    public BlockingCollection<PendingSample> AudioRequests { get; } = [];

    public Stopwatch Clock { get; } = new();

    /// <summary>L'arrêt de l'enregistrement ou la fermeture de l'essai : aucune attente ne survit à l'un ou à l'autre.</summary>
    public CancellationToken Stop { get; }

    public long LastIndex { get; set; } = -1;

    public int Samples { get; set; }

    public Exception? Fault { get; set; }

    public bool DeviceLost { get; set; }

    public bool AudioFailed { get; set; }

    public bool IsClosed { get; private set; }

    public void Enter()
    {
        lock (_requestGate)
        {
            _users++;
        }
    }

    /// <summary>Faux si l'essai est fermé : personne ne servira cette demande.</summary>
    public bool TryPost(PendingSample pending, bool isAudio)
    {
        lock (_requestGate)
        {
            if (!_acceptsRequests || (isAudio && Audio is null))
            {
                return false;
            }

            _users++;
            (isAudio ? AudioRequests : VideoRequests).Add(pending);
            return true;
        }
    }

    public void Leave()
    {
        lock (_requestGate)
        {
            if (--_users > 0)
            {
                return;
            }
        }

        _closing.Dispose();
        Textures.Dispose();
        VideoRequests.Dispose();
        AudioRequests.Dispose();
    }

    /// <summary>
    /// Les fils d'échantillons s'arrêtent une fois servies les demandes en attente, et une demande tardive reçoit aussitôt une
    /// fin de flux. Réservé à l'abandon : à un arrêt normal, l'encodeur demande encore de quoi finir, et le son doit se vider.
    /// </summary>
    public void StopServing()
    {
        lock (_requestGate)
        {
            if (!_acceptsRequests)
            {
                return;
            }

            _acceptsRequests = false;
            VideoRequests.CompleteAdding();
            AudioRequests.CompleteAdding();
        }
    }

    public void Close()
    {
        StopServing();

        lock (Gate)
        {
            IsClosed = true;
        }

        _closing.Cancel();
        Textures.ReleaseAll();
        Leave();
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

    /// <summary>Attend une place. Faux à l'arrêt. Lève si l'encodeur garde tout trop longtemps : il est bloqué.</summary>
    public bool WaitForFree(CancellationToken stop, TimeSpan timeout)
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
            return false;
        }

        return true;
    }

    /// <summary>Prend la texture de la place obtenue par <see cref="WaitForFree"/>.</summary>
    public FrameTexture TakeFree()
    {
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

    /// <summary>Libère les textures, y compris celles qu'un encodeur mort ne rendra jamais.</summary>
    public void ReleaseAll()
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
    }

    /// <summary>Appelé quand plus aucune demande d'image n'attend une place.</summary>
    public void Dispose()
    {
        ReleaseAll();
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
        // Un échantillon jamais traité et sa texture se retiennent l'un l'autre, avec le périphérique : ce lien doit tomber ici.
        Sample = null;
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

    /// <summary>Remplace la taille lue sur chaque image, pour simuler un changement de résolution.</summary>
    public Func<SizeInt32, SizeInt32>? ContentSize { get; set; }

    /// <summary>Remplace la lecture de l'état du périphérique, selon le nombre d'images écrites, pour simuler sa perte.</summary>
    public Func<int, int>? DeviceRemovedReason { get; set; }

    /// <summary>Donne le flux de sortie, pour simuler sa perte en cours d'enregistrement.</summary>
    public Action<IRandomAccessStream>? OutputOpened { get; set; }

    /// <summary>Appelé pendant la copie, sous son verrou. Une attente simule un pilote bloqué.</summary>
    public Action? InsideCopy { get; set; }

    /// <summary>Appelé quand l'encodeur a fini. Une attente simule une fin qui traîne au-delà de la limite.</summary>
    public Action? AfterEncoding { get; set; }

    /// <summary>Appelé avec le fichier temporaire juste avant de décider de son sort, pour le faire tenir par un autre programme.</summary>
    public Action<string>? BeforeSettle { get; set; }

    /// <summary>Remplace, si non nul, le résultat de la lecture d'un tampon de son, pour simuler ses erreurs sans en prendre aucun.</summary>
    public Func<int>? AudioBufferResult { get; set; }

    /// <summary>Appelé à l'ouverture du son. Une exception simule un micro refusé ou absent, une attente un pilote bloqué.</summary>
    public Action<RecordingAudio>? AudioOpening { get; set; }

    /// <summary>Appelé sur le fil de capture une fois le son ouvert. Une attente simule une capture bloquée.</summary>
    public Action? AudioOpened { get; set; }

    /// <summary>Remplace l'heure de chaque paquet, pour simuler une horloge de périphérique qui dérive.</summary>
    public Func<long, long>? AudioPacketTime { get; set; }

    /// <summary>Remplace les indications de chaque paquet, pour simuler une heure de périphérique déclarée fausse.</summary>
    public Func<int, int>? AudioPacketFlags { get; set; }

    /// <summary>Écarte un paquet selon son heure, pour simuler un périphérique qui se tait. Une exception simule sa disparition.</summary>
    public Func<long, bool>? AudioPacketDropped { get; set; }

    /// <summary>Appelé avant chaque échantillon de son. Une exception simule une panne de ce flux.</summary>
    public Action<TimeSpan>? BeforeAudioSample { get; set; }

    /// <summary>Reçoit chaque paquet capté, pour mesurer la dérive du périphérique.</summary>
    public Action<long, int, bool, bool, long>? AudioPacketSeen { get; set; }

    public void AudioPacket(long elapsedTicks, int frames, bool silent, bool discontinuity, long corrections) =>
        AudioPacketSeen?.Invoke(elapsedTicks, frames, silent, discontinuity, corrections);

    public bool UsedHardware { get; set; }

    public bool UsedAudio { get; set; }

    public int Samples { get; set; }

    public int FramesArrived { get; set; }

    public int FramesSkipped { get; set; }

    public int PeakPending { get; set; }

    public int TexturesCreated { get; set; }

    /// <summary>Fils d'échantillons terminés, pour vérifier qu'aucun ne reste en attente après un abandon.</summary>
    public int ServersEnded => _serversEnded;

    public void ServerEnded() => Interlocked.Increment(ref _serversEnded);

    private int _serversEnded;
}
