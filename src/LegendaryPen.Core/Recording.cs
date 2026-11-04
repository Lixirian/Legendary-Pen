using System.Globalization;

namespace LegendaryPen.Core;

public enum RecordingStatus { Idle, Countdown, Recording, Finishing }

public enum RecordingAudio { None, System, Microphone }

/// <summary>Pourquoi un enregistrement s'est terminé.</summary>
public enum RecordingEndReason { Requested, MonitorClosed, DiskFull, Failed }

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
}
