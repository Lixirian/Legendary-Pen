using System.Globalization;

namespace LegendaryPen.Core;

public enum CaptureDestination
{
    Folder,
    Clipboard,
    Ask,
}

public static class CaptureFiles
{
    /// <summary>Nom horodaté qui n'écrase jamais un fichier existant.</summary>
    public static string UniquePath(string folder, DateTime time, Func<string, bool> exists)
    {
        var stem = "Legendary Pen " + time.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture);
        var path = Path.Combine(folder, stem + ".png");

        for (var suffix = 2; exists(path); suffix++)
        {
            path = Path.Combine(folder, $"{stem} ({suffix}).png");
        }

        return path;
    }
}

public static class CaptureRegion
{
    public const int MinDragPixels = 6;

    /// <summary>Un simple clic prend l'écran entier ; un glissé prend la zone, limitée à l'écran.</summary>
    public static PixelRect Resolve(PixelPoint start, PixelPoint end, int screenWidth, int screenHeight)
    {
        if (Math.Abs(end.X - start.X) < MinDragPixels && Math.Abs(end.Y - start.Y) < MinDragPixels)
        {
            return new PixelRect(0, 0, screenWidth, screenHeight);
        }

        var left = Math.Clamp(Math.Min(start.X, end.X), 0, screenWidth - 1);
        var top = Math.Clamp(Math.Min(start.Y, end.Y), 0, screenHeight - 1);
        var right = Math.Clamp(Math.Max(start.X, end.X), left + 1, screenWidth);
        var bottom = Math.Clamp(Math.Max(start.Y, end.Y), top + 1, screenHeight);

        return new PixelRect(left, top, right, bottom);
    }
}

/// <summary>Réglages appliqués en direct, sans redémarrer l'application.</summary>
public sealed class LiveOptions
{
    public LiveOptions(AppSettings settings)
    {
        Fade = new FadeTiming();
        Apply(settings);
    }

    public FadeTiming Fade { get; private set; }

    public bool WheelChangesSize { get; private set; }

    public bool PressureEnabled { get; private set; }

    public bool BoardOnToolbarScreenOnly { get; private set; }

    public event Action? Changed;

    public void Apply(AppSettings settings)
    {
        Fade = new FadeTiming(settings.FadeDelaySeconds, settings.FadeDurationSeconds);
        WheelChangesSize = settings.WheelChangesSize;
        PressureEnabled = settings.PressureEnabled;
        BoardOnToolbarScreenOnly = settings.BoardOnToolbarScreenOnly;
        Changed?.Invoke();
    }
}
