using System.Text.Json;

namespace LegendaryPen.Core;

/// <summary>Texte d'annotation enregistré.</summary>
public sealed record SavedText(string Text, double Left, double Top, double FontSize, string FontFamily, string Color);

/// <summary>Contenu enregistré d'une surface : traits au format de l'encre Windows, et textes.</summary>
public sealed record SavedSurface(byte[] Strokes, List<SavedText> Texts);

/// <summary>Annotations gardées sur cet ordinateur, un fichier par écran et par fond.</summary>
public sealed class AnnotationStore(string folder)
{
    public void Save(string monitor, BoardBackground background, SavedSurface surface)
    {
        Directory.CreateDirectory(folder);

        var path = PathOf(monitor, background);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";

        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(surface));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    /// <summary>Renvoie null s'il n'y a rien d'enregistré ou si le fichier est illisible.</summary>
    public SavedSurface? Load(string monitor, BoardBackground background)
    {
        try
        {
            return JsonSerializer.Deserialize<SavedSurface>(File.ReadAllText(PathOf(monitor, background)));
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Delete(string monitor, BoardBackground background)
    {
        // File.Delete accepte un fichier absent, mais pas un dossier absent.
        if (Directory.Exists(folder))
        {
            File.Delete(PathOf(monitor, background));
        }
    }

    /// <summary>Supprime toutes les annotations enregistrées, y compris celles d'un écran débranché.</summary>
    public void DeleteAll()
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // Un nom d'écran Windows contient des caractères interdits dans un nom de fichier.
    private string PathOf(string monitor, BoardBackground background)
    {
        var safeName = new string(monitor.Where(char.IsLetterOrDigit).ToArray());

        return Path.Combine(folder, $"{safeName}-{background}.json");
    }
}
