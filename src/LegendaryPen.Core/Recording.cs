using System.Buffers.Binary;
using System.Globalization;

namespace LegendaryPen.Core;

public enum RecordingStatus { Idle, Countdown, Recording, Finishing }

public enum RecordingAudio { None, System, Microphone }

/// <summary>Pourquoi un enregistrement s'est terminé.</summary>
public enum RecordingEndReason { Requested, MonitorClosed, DiskFull, Failed, ScreenChanged }

/// <summary>Ce qui est arrivé au son. Il ne fait jamais échouer un enregistrement : la vidéo continue sans lui, et le message le dit.</summary>
public enum RecordingAudioProblem { None, MicrophoneUnavailable, SystemSoundUnavailable, Interrupted }

/// <summary>
/// Cycle de vie d'un enregistrement : Idle, Countdown, Recording, Finishing, puis Idle.
/// Une transition illégale est ignorée sans rien lever ni notifier : l'arrêt peut croiser la fin de la capture.
/// </summary>
public sealed class RecordingState
{
    public RecordingStatus Status { get; private set; }

    public event Action? Changed;

    public bool TryStart()
    {
        if (Status != RecordingStatus.Idle)
        {
            return false;
        }

        Move(RecordingStatus.Countdown);
        return true;
    }

    public void CountdownElapsed()
    {
        if (Status == RecordingStatus.Countdown)
        {
            Move(RecordingStatus.Recording);
        }
    }

    /// <summary>Pendant le compte à rebours, rien n'a été enregistré : retour direct à Idle.</summary>
    public void RequestStop()
    {
        switch (Status)
        {
            case RecordingStatus.Countdown:
                Move(RecordingStatus.Idle);
                break;
            case RecordingStatus.Recording:
                Move(RecordingStatus.Finishing);
                break;
        }
    }

    public void Finished()
    {
        if (Status == RecordingStatus.Finishing)
        {
            Move(RecordingStatus.Idle);
        }
    }

    private void Move(RecordingStatus status)
    {
        Status = status;
        Changed?.Invoke();
    }
}

/// <summary>
/// Demandes en cours de retrait des captures. L'enregistrement et un trait de surligneur retirent les mêmes fenêtres :
/// seule la dernière levée les rétablit, sinon la fin d'un trait ferait apparaître la barre dans la vidéo.
/// </summary>
public sealed class CaptureExclusionCount
{
    private int _count;

    public bool IsExcluded => _count > 0;

    public void Acquire() => _count++;

    /// <summary>Vrai si plus personne ne demande l'exclusion : les fenêtres sont à rétablir.</summary>
    public bool Release()
    {
        if (_count == 0)
        {
            return false;
        }

        _count--;
        return _count == 0;
    }
}

/// <summary>Ce qui ferme l'application, ou peut la fermer, pendant un enregistrement.</summary>
public enum RecordingClose { Update, SessionEnd, Exit }

/// <summary>
/// Dit si un enregistrement peut encore démarrer et si les messages de fin ont encore un sens. Une mise à jour ou une fin de session
/// peuvent être abandonnées et rendre la main ; chacune ne lève que sa propre demande, et rien ne rouvre après une vraie sortie.
/// </summary>
public sealed class RecordingCloseLatch
{
    private readonly HashSet<RecordingClose> _reasons = [];

    public bool RefusesStart => _reasons.Count > 0;

    /// <summary>Une mise à jour garde les messages : elle peut encore échouer, et l'icône de notification reste.</summary>
    public bool IsQuiet => _reasons.Contains(RecordingClose.SessionEnd) || _reasons.Contains(RecordingClose.Exit);

    public void Enter(RecordingClose reason) => _reasons.Add(reason);

    public void Leave(RecordingClose reason) => _reasons.Remove(reason);
}

/// <summary>
/// Compte le temps passé sur des tampons de son en erreur. Un tampon occupé se retrouve à la lecture suivante, mais une erreur
/// qui dure donnerait une vidéo muette sans message. Toute lecture sans erreur remet le compte à zéro.
/// </summary>
public sealed class AudioBufferErrorWatch
{
    /// <summary>Au-delà de 5 s d'erreurs consécutives, le son est considéré comme interrompu.</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    private TimeSpan? _since;

    /// <summary>Note une erreur à cet instant. Vrai si les erreurs durent depuis <see cref="Limit"/>.</summary>
    public bool IsStuck(TimeSpan now)
    {
        _since ??= now;
        return now - _since >= Limit;
    }

    public void Clear() => _since = null;
}

public static class RecordingRules
{
    /// <summary>Espace libre minimal pour continuer à enregistrer : 500 Mo.</summary>
    public const long MinFreeBytes = 500L * 1024 * 1024;

    /// <summary>Nom fixe « Legendary Pen 2026-11-03 21-10-05.mp4 », suffixé « (2) », « (3) »… s'il existe déjà.</summary>
    public static string FilePath(string folder, DateTime now, Func<string, bool> exists)
    {
        var stem = "Legendary Pen " + now.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture);
        var path = Path.Combine(folder, stem + ".mp4");

        for (var suffix = 2; exists(path); suffix++)
        {
            path = Path.Combine(folder, string.Create(CultureInfo.InvariantCulture, $"{stem} ({suffix}).mp4"));
        }

        return path;
    }

    /// <summary>L'encodeur H.264 exige des dimensions paires : arrondi à l'inférieur, au moins 2.</summary>
    public static (int Width, int Height) EncodableSize(int width, int height) =>
        (Math.Max(2, width & ~1), Math.Max(2, height & ~1));

    public static bool HasRoomToContinue(long freeBytes) => freeBytes >= MinFreeBytes;

    /// <summary>Seule une valeur réelle arrête la vidéo : une requête en échec (null) ne dit rien de la place restante.</summary>
    public static bool ShouldStopForDisk(long? freeBytes) => freeBytes is { } free && !HasRoomToContinue(free);

    /// <summary>Durée affichée : « 12:34 », puis « 1:02:03 » à partir d'une heure.</summary>
    public static string FormatElapsed(TimeSpan elapsed)
    {
        var whole = (long)elapsed.TotalSeconds;
        var hours = whole / 3600;
        var minutes = (whole % 3600) / 60;
        var seconds = whole % 60;

        return hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:00}:{seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes}:{seconds:00}");
    }

    /// <summary>
    /// Textes français, clés de <see cref="Loc"/>, de la notification de fin, ligne par ligne. {0} est le fichier.
    /// Un arrêt demandé qui n'a rien écrit ne dit rien : il n'y a rien à annoncer.
    /// </summary>
    /// <param name="mayBeIncomplete">Le fichier gardé n'a pas pu être confirmé comme une vidéo finie.</param>
    public static IReadOnlyList<string> EndMessages(RecordingEndReason reason, bool hasFile, RecordingAudioProblem audio = RecordingAudioProblem.None, bool mayBeIncomplete = false)
    {
        var lines = EndOfVideoMessages(reason, hasFile, mayBeIncomplete);

        // Sans vidéo, le son n'a plus d'importance.
        var soundLine = audio switch
        {
            RecordingAudioProblem.MicrophoneUnavailable => "Micro indisponible : vidéo enregistrée sans son.",
            RecordingAudioProblem.SystemSoundUnavailable => "Son de l'ordinateur indisponible : vidéo enregistrée sans son.",
            RecordingAudioProblem.Interrupted => "Le son s'est interrompu pendant l'enregistrement : la suite de la vidéo est muette.",
            _ => null,
        };

        return hasFile && soundLine is not null ? [.. lines, soundLine] : lines;
    }

    /// <summary>Annonce immédiate, clé de <see cref="Loc"/>, d'une source de son absente au démarrage ; null s'il n'y a rien à dire.</summary>
    public static string? StartSoundMessage(RecordingAudioProblem audio)
    {
        return audio switch
        {
            RecordingAudioProblem.MicrophoneUnavailable => "Micro indisponible : cette vidéo se fait sans son.",
            RecordingAudioProblem.SystemSoundUnavailable => "Son de l'ordinateur indisponible : cette vidéo se fait sans son.",
            _ => null,
        };
    }

    /// <summary>
    /// Vrai pour AUDCLNT_E_BUFFER_ERROR et AUDCLNT_E_BUFFER_OPERATION_PENDING : le tampon de capture est momentanément occupé,
    /// et la lecture suivante le retrouvera. Toute autre erreur reste une perte du périphérique.
    /// </summary>
    public static bool IsTemporaryAudioBufferError(int result) => result is unchecked((int)0x88890018) or unchecked((int)0x8889000B);

    private static IReadOnlyList<string> EndOfVideoMessages(RecordingEndReason reason, bool hasFile, bool mayBeIncomplete)
    {
        return (reason, hasFile) switch
        {
            (RecordingEndReason.Requested or RecordingEndReason.Failed, true) when mayBeIncomplete => ["La vidéo est peut-être incomplète. Fichier gardé : {0}"],
            (RecordingEndReason.MonitorClosed, true) when mayBeIncomplete => ["L'écran enregistré a été débranché.", "La vidéo est peut-être incomplète. Fichier gardé : {0}"],
            (RecordingEndReason.ScreenChanged, true) when mayBeIncomplete => ["La résolution de l'écran a changé : enregistrement arrêté.", "La vidéo est peut-être incomplète. Fichier gardé : {0}"],
            (RecordingEndReason.DiskFull, true) when mayBeIncomplete => ["Plus assez de place sur le disque : enregistrement arrêté.", "La vidéo est peut-être incomplète. Fichier gardé : {0}"],
            (RecordingEndReason.Requested, true) => ["Vidéo enregistrée : {0}"],
            (RecordingEndReason.MonitorClosed, true) => ["L'écran enregistré a été débranché.", "Vidéo enregistrée : {0}"],
            (RecordingEndReason.MonitorClosed, false) => ["L'écran enregistré a été débranché."],
            (RecordingEndReason.ScreenChanged, true) => ["La résolution de l'écran a changé : enregistrement arrêté.", "Vidéo enregistrée : {0}"],
            (RecordingEndReason.ScreenChanged, false) => ["La résolution de l'écran a changé : enregistrement arrêté."],
            (RecordingEndReason.DiskFull, true) => ["Plus assez de place sur le disque : enregistrement arrêté.", "Vidéo enregistrée : {0}"],
            (RecordingEndReason.DiskFull, false) => ["Plus assez de place sur le disque : enregistrement arrêté."],
            (RecordingEndReason.Failed, true) => ["L'enregistrement s'est arrêté sur une erreur. Vidéo gardée : {0}"],
            (RecordingEndReason.Failed, false) => ["L'enregistrement a échoué : aucune vidéo gardée."],
            _ => [],
        };
    }

    /// <summary>Débit vidéo : 8 Mb/s en 2560 x 1440, proportionnel à la surface, entre 2 et 40 Mb/s.</summary>
    public static int VideoBitRate(int width, int height)
    {
        const long ReferenceBitRate = 8_000_000;
        const long ReferenceArea = 2560L * 1440L;

        return (int)Math.Clamp(ReferenceBitRate * width * height / ReferenceArea, 2_000_000, 40_000_000);
    }

    /// <summary>
    /// Vrai si le flux est un MP4 fini : des boîtes qui tiennent toutes dans le fichier, dont l'index « moov ».
    /// L'encodeur n'écrit cet index qu'à la fin : sans lui, le fichier est illisible.
    /// </summary>
    public static bool IsCompleteMp4(Stream stream)
    {
        const int HeaderSize = 8;
        Span<byte> header = stackalloc byte[HeaderSize];
        var hasIndex = false;
        var position = 0L;
        stream.Seek(0, SeekOrigin.Begin);

        while (position < stream.Length)
        {
            if (stream.Length - position < HeaderSize)
            {
                return false;
            }

            stream.ReadExactly(header);
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var isIndex = header[4..].SequenceEqual("moov"u8);

            if (size == 1)
            {
                if (stream.Length - position < 2 * HeaderSize)
                {
                    return false;
                }

                stream.ReadExactly(header);
                size = BinaryPrimitives.ReadInt64BigEndian(header);
            }
            else if (size == 0)
            {
                size = stream.Length - position;
            }

            if (size < HeaderSize || size > stream.Length - position)
            {
                return false;
            }

            hasIndex |= isIndex;
            position += size;
            stream.Seek(position, SeekOrigin.Begin);
        }

        return hasIndex;
    }
}
