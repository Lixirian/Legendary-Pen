using System.Globalization;
using System.Text.Json;

namespace LegendaryPen.Core;

public sealed record AppSettings
{
    public int Version { get; init; } = 1;

    public AppLanguage Language { get; init; }

    public int? ToolbarX { get; init; }

    public int? ToolbarY { get; init; }

    public bool ToolbarVertical { get; init; }

    public ToolbarDock ToolbarDock { get; init; }

    public string? ToolbarDockMonitor { get; init; }

    public double? ToolbarCollapseSeconds { get; init; }

    public bool ToolbarSecondRowVisible { get; init; } = true;

    public bool WheelChangesSize { get; init; } = true;

    public bool PressureEnabled { get; init; } = true;

    public bool HighlightSnapEnabled { get; init; } = true;

    public bool ShapeRecognitionEnabled { get; init; }

    public bool BoardOnToolbarScreenOnly { get; init; }

    public CaptureDestination CaptureDestination { get; init; }

    public string? CaptureFolder { get; init; }

    public IReadOnlyDictionary<string, HotkeyBinding>? Hotkeys { get; init; }

    public bool FadingEnabled { get; init; }

    public double? FadeDelaySeconds { get; init; }

    public double? FadeDurationSeconds { get; init; }

    public IReadOnlyList<string>? QuickColors { get; init; }

    public string? PenColor { get; init; }

    public double? PenSize { get; init; }

    public string? PreviousPenColor { get; init; }

    public bool SizePerToolEnabled { get; init; }

    public IReadOnlyDictionary<string, double>? ToolSizes { get; init; }

    public bool KeepAnnotations { get; init; }

    public RecordingAudio RecordingAudio { get; init; }

}

/// <summary>Lit et écrit les réglages sans jamais laisser de fichier à moitié écrit.</summary>
/// <param name="path">Fichier des réglages.</param>
/// <param name="waitBeforeRetry">Attente entre deux lectures, si un antivirus ou une synchronisation tient le fichier.</param>
public sealed class SettingsStore(string path, Action? waitBeforeRetry = null)
{
    private const int ReadAttempts = 4;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <exception cref="IOException">Le fichier existe mais reste inaccessible après plusieurs essais.</exception>
    public AppSettings Load()
    {
        string json;

        try
        {
            json = ReadWithRetries();
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new AppSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<AppSettings>(json, Options) is { } settings ? Normalized(settings) : SetAsideUnreadableFile();
        }
        catch (JsonException)
        {
            return SetAsideUnreadableFile();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";

        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, Options));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private string ReadWithRetries()
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (Exception exception) when (attempt < ReadAttempts && exception is not (FileNotFoundException or DirectoryNotFoundException) && exception is IOException or UnauthorizedAccessException)
            {
                (waitBeforeRetry ?? WaitBriefly)();
            }
        }
    }

    private static void WaitBriefly() => Thread.Sleep(150);

    // Une valeur inconnue, écrite à la main ou par une autre version, ne doit jamais ouvrir un micro : sans choix clair, pas de son.
    private static AppSettings Normalized(AppSettings settings) =>
        Enum.IsDefined(settings.RecordingAudio) ? settings : settings with { RecordingAudio = RecordingAudio.None };

    private AppSettings SetAsideUnreadableFile()
    {
        File.Move(path, $"{path}.illisible-{DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)}");
        return new AppSettings();
    }
}
