using System.Diagnostics;

namespace LegendaryPen.Core;

/// <summary>Ce qu'est devenue une vidéo à la fin de l'enregistrement.</summary>
/// <param name="SavedPath">Où elle se trouve ; null s'il n'y a pas de vidéo.</param>
/// <param name="IsInPlace">Vrai si elle porte son nom final.</param>
/// <param name="MayBeIncomplete">Vrai si le fichier gardé n'a pas pu être confirmé comme une vidéo finie.</param>
public readonly record struct RecordingFileOutcome(string? SavedPath, bool IsInPlace, bool MayBeIncomplete);

/// <summary>
/// Décide du sort du fichier temporaire. Une vidéo finie n'est jamais supprimée ni cachée : ni la vérification, ni une erreur de
/// lecture ne suppriment rien. Seule exception : le disque plein, quand le fichier a été lu et refusé, car le supprimer rend la place.
/// Un fichier vide ne contient aucune vidéo, et ne reste donc pas.
/// </summary>
public static class RecordingFile
{
    /// <summary>Attente maximale d'un fichier qu'on ne peut pas lire : un antivirus ou un client de synchronisation le tient peut-être.</summary>
    public static readonly TimeSpan ReadRetryLimit = TimeSpan.FromSeconds(2);

    /// <summary>Attente maximale d'un fichier qu'on ne peut pas renommer, pour la même raison.</summary>
    public static readonly TimeSpan MoveRetryLimit = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan RetryPause = TimeSpan.FromMilliseconds(100);

    private const int AccessDenied = unchecked((int)0x80070005);
    private const int SharingViolation = unchecked((int)0x80070020);
    private const int LockViolation = unchecked((int)0x80070021);

    private enum Check { Complete, Refused, Empty, Unreadable, Missing }

    /// <param name="diskFull">L'encodeur a échoué faute de place sur le disque.</param>
    public static RecordingFileOutcome Settle(string temporaryPath, string finalPath, bool diskFull, TimeSpan? readLimit = null, TimeSpan? moveLimit = null)
    {
        return Inspect(temporaryPath, readLimit ?? ReadRetryLimit) switch
        {
            Check.Complete => Move(temporaryPath, finalPath, moveLimit ?? MoveRetryLimit),
            Check.Refused when diskFull => Delete(temporaryPath) ? default : new RecordingFileOutcome(temporaryPath, false, true),
            Check.Empty => DiscardEmpty(temporaryPath),
            Check.Missing => default,
            _ => new RecordingFileOutcome(temporaryPath, false, true),
        };
    }

    /// <summary>Vrai si le dossier accepte vraiment un fichier : y créer un petit fichier temporaire, puis le retirer. Peut être lent sur un réseau.</summary>
    public static bool IsWritableFolder(string folder)
    {
        try
        {
            var probe = Path.Combine(folder, $".legendary-pen-{Guid.NewGuid():N}.tmp");
            using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            stream.WriteByte(0);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Debug.WriteLine(exception);
            return false;
        }
    }

    private static Check Inspect(string path, TimeSpan limit)
    {
        var clock = Stopwatch.StartNew();

        while (true)
        {
            try
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

                if (file.Length == 0)
                {
                    return Check.Empty;
                }

                return RecordingRules.IsCompleteMp4(file) ? Check.Complete : Check.Refused;
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                Debug.WriteLine(exception);
                return Check.Missing;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine(exception);

                if (clock.Elapsed >= limit)
                {
                    return Check.Unreadable;
                }
            }

            Thread.Sleep(RetryPause);
        }
    }

    private static RecordingFileOutcome Move(string temporaryPath, string finalPath, TimeSpan limit)
    {
        var clock = Stopwatch.StartNew();

        while (true)
        {
            try
            {
                File.Move(temporaryPath, finalPath, overwrite: false);
                return new RecordingFileOutcome(Path.GetFullPath(finalPath), true, false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Le nom final pris n'est pas un conflit passager : la vidéo lisible reste en .tmp, tout de suite.
                Debug.WriteLine(exception);

                if (exception.HResult is not (SharingViolation or LockViolation or AccessDenied) || clock.Elapsed >= limit)
                {
                    return new RecordingFileOutcome(temporaryPath, false, false);
                }
            }

            Thread.Sleep(RetryPause);
        }
    }

    private static RecordingFileOutcome DiscardEmpty(string path)
    {
        // Un fichier vide qui résiste n'est pas une vidéo à annoncer pour autant.
        Delete(path);
        return default;
    }

    private static bool Delete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine(exception);
            return false;
        }
    }
}
