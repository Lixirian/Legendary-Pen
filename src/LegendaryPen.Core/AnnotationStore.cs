using System.Text.Json;

namespace LegendaryPen.Core;

/// <summary>Texte d'annotation enregistré.</summary>
public sealed record SavedText(string Text, double Left, double Top, double FontSize, string FontFamily, string Color)
{
    public const string DefaultFontFamily = "Segoe UI";

    public const double DefaultFontSize = 16;

    // Au-delà, WPF refuse la taille du texte en levant une exception.
    private const double MaxFontSize = 35_000;

    /// <summary>
    /// Ce texte, prêt à afficher. Un fichier retouché à la main peut avoir perdu un champ : il prend une valeur sûre, et le reste
    /// est gardé. Une couleur vide reste vide, car elle prend la couleur du stylo à l'affichage.
    /// </summary>
    public SavedText Repaired() => this with
    {
        Text = Text ?? string.Empty,
        Left = double.IsFinite(Left) ? Left : 0,
        Top = double.IsFinite(Top) ? Top : 0,
        FontSize = FontSize is > 0 and <= MaxFontSize ? FontSize : DefaultFontSize,
        FontFamily = string.IsNullOrWhiteSpace(FontFamily) ? DefaultFontFamily : FontFamily,
        Color = Color ?? string.Empty,
    };
}

/// <summary>Un calque enregistré : le nom se déduit du numéro, il n'est pas gardé.</summary>
public sealed record SavedLayer(Guid Id, int Number, bool IsVisible, byte[] Strokes, List<SavedText> Texts);

/// <summary>
/// Contenu enregistré d'une surface. Les fichiers des versions 0.6 à 0.8 n'ont que <paramref name="Strokes"/> et
/// <paramref name="Texts"/> (<paramref name="Layers"/> est alors nul) ; ce format reste écrit tant que la pile est celle
/// d'origine (<see cref="LayerRestoration.UsesLegacyFormat"/>). Une pile avec des calques remplit <paramref name="Layers"/>,
/// <paramref name="ActiveLayerId"/> et <paramref name="NextNumber"/>, et laisse les deux premiers champs vides.
/// </summary>
public sealed record SavedSurface(
    byte[] Strokes,
    List<SavedText> Texts,
    List<SavedLayer>? Layers = null,
    Guid? ActiveLayerId = null,
    int? NextNumber = null);

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
