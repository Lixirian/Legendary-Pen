using System.Globalization;

namespace LegendaryPen.Core;

/// <summary>Garde la trace locale des erreurs imprévues. Rien n'est envoyé hors de l'ordinateur.</summary>
public sealed class ErrorJournal(string path, long maxBytes = 256 * 1024)
{
    private readonly Lock _gate = new();

    public string Path => path;

    /// <summary>Ne lève jamais : le journal sert pendant le traitement d'une autre erreur.</summary>
    public void Write(string origin, Exception exception, DateTime now)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);

                // On repart de zéro plutôt que de laisser le fichier grossir sans limite.
                if (File.Exists(path) && new FileInfo(path).Length > maxBytes)
                {
                    File.Delete(path);
                }

                var stamp = now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                File.AppendAllText(path, $"[{stamp}] {origin}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                // Sans journal, l'erreur d'origine reste traitée normalement.
            }
        }
    }
}

/// <summary>Reconnaît une panne qui se répète : continuer n'aurait alors plus de sens.</summary>
public sealed class ErrorBurst(int limit = 3, TimeSpan? window = null)
{
    private readonly TimeSpan _window = window ?? TimeSpan.FromSeconds(30);
    private readonly Queue<DateTime> _recent = new();

    /// <summary>Compte une erreur et indique si elles sont trop nombreuses dans la période.</summary>
    public bool IsTooMany(DateTime now)
    {
        _recent.Enqueue(now);

        while (now - _recent.Peek() > _window)
        {
            _recent.Dequeue();
        }

        return _recent.Count >= limit;
    }
}
