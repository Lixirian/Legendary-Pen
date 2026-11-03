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
}

/// <summary>Lit et écrit les réglages sans jamais laisser de fichier à moitié écrit.</summary>
public sealed class SettingsStore(string path)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public AppSettings Load()
    {
        string json;

        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new AppSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<AppSettings>(json, Options) ?? SetAsideUnreadableFile();
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

    private AppSettings SetAsideUnreadableFile()
    {
        File.Move(path, $"{path}.illisible-{DateTime.Now:yyyyMMdd-HHmmss-fff}");
        return new AppSettings();
    }
}
