using System.Net;
using System.Text.RegularExpressions;
using LegendaryPen.Core;
using Xunit;

// La langue active est un état global de l'application : les tests ne doivent pas se croiser.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace LegendaryPen.Tests;

public sealed partial class LocalizationTests : IDisposable
{
    // Noms propres et textes identiques dans les deux langues.
    private static readonly HashSet<string> NotTranslated = ["Legendary Pen", "OK"];

    // Erreurs de programmation, jamais montrées à l'utilisateur.
    private static readonly HashSet<string> DeveloperMessages =
    [
        "Cet outil n'est pas une forme.",
        "La taille de l'image ne correspond pas aux dimensions.",
        "La barre n'est attachée à aucun bord.",
        "La loupe n'a pas confirmé sa disparition.",
        "La loupe ne s'est pas arrêtée à temps.",
        "Loupe indisponible : la création de la fenêtre ou du contrôle a échoué.",
        "Loupe : la liste de filtrage a été refusée.",
        "Loupe indisponible : {exception.Message}",
        "The click hook thread did not stop in time.",
        "Click effects unavailable: the mouse hook was refused.",
    ];

    public void Dispose() => Loc.Use(AppLanguage.French, "fr");

    [Theory]
    [InlineData(AppLanguage.System, "fr", AppLanguage.French)]
    [InlineData(AppLanguage.System, "FR", AppLanguage.French)]
    [InlineData(AppLanguage.System, "en", AppLanguage.English)]
    [InlineData(AppLanguage.System, "de", AppLanguage.English)]
    [InlineData(AppLanguage.French, "en", AppLanguage.French)]
    [InlineData(AppLanguage.English, "fr", AppLanguage.English)]
    public void The_language_follows_the_choice_then_the_system(AppLanguage choice, string system, AppLanguage expected)
    {
        Assert.Equal(expected, Loc.Resolve(choice, system));
    }

    [Fact]
    public void French_is_returned_as_written()
    {
        Loc.Use(AppLanguage.French, "en");

        Assert.Equal("Annuler", Loc.T("Annuler"));
        Assert.Equal("Enregistré : a.pdf", Loc.T("Enregistré : {0}", "a.pdf"));
    }

    [Fact]
    public void English_translates_and_formats()
    {
        Loc.Use(AppLanguage.English, "fr");

        Assert.Equal("Cancel", Loc.T("Annuler"));
        Assert.Equal("Saved: a.pdf", Loc.T("Enregistré : {0}", "a.pdf"));
    }

    [Fact]
    public void An_unknown_text_stays_visible()
    {
        Loc.Use(AppLanguage.English, "fr");

        Assert.Equal("Texte inconnu", Loc.T("Texte inconnu"));
    }

    [Fact]
    public void Shortcuts_and_labels_follow_the_language()
    {
        var binding = new HotkeyBinding(HotkeyBinding.Control | HotkeyBinding.Shift, 0x20);

        Assert.Equal("Ctrl+Maj+Espace", binding.Format());

        Loc.Use(AppLanguage.English, "fr");

        Assert.Equal("Ctrl+Shift+Space", binding.Format());
        Assert.Equal("Pen", HotkeyCatalog.All.Single(definition => definition.Action == HotkeyAction.Pen).Label);
    }

    [Fact]
    public void Undoing_and_cancelling_a_dialog_are_two_different_words()
    {
        Loc.Use(AppLanguage.English, "fr");

        Assert.Equal("Cancel", Loc.T("Annuler"));
        Assert.Equal("Undo", HotkeyCatalog.All.Single(definition => definition.Action == HotkeyAction.Undo).Label);
    }

    [Fact]
    public void The_signed_file_name_follows_the_language()
    {
        Loc.Use(AppLanguage.English, "fr");

        Assert.Equal(Path.Combine("d", "a (signed).pdf"), PdfFiles.SignedPath(Path.Combine("d", "a.pdf"), _ => false));
    }

    [Fact]
    public void A_setting_defaults_to_the_system_language()
    {
        Assert.Equal(AppLanguage.System, new AppSettings().Language);
    }

    [Fact]
    public void Every_translation_keeps_its_placeholders()
    {
        foreach (var (french, english) in Translations.English)
        {
            Assert.False(string.IsNullOrWhiteSpace(english), french);
            Assert.Equal(Placeholders(french), Placeholders(english));
        }
    }

    [Fact]
    public void Every_hotkey_label_is_translated()
    {
        Assert.All(HotkeyCatalog.All, definition => Assert.True(Translations.English.ContainsKey(definition.FrenchLabel), definition.FrenchLabel));
    }

    [Fact]
    public void The_word_pattern_detects_french_without_accents()
    {
        // Un motif cassé ne doit plus jamais passer inaperçu : une majuscule initiale ou une lettre accentuée ne l'aveuglent pas.
        Assert.Matches(Words(), "Tableau blanc");
        Assert.Matches(Words(), "sans accent ici");
        Assert.Matches(Words(), "Loupe indisponible");
        Assert.DoesNotMatch(Words(), "OK");
        Assert.DoesNotMatch(Words(), "LegendaryPen.App");
    }

    [Fact]
    public void Every_text_of_the_windows_is_translated()
    {
        var missing = new List<string>();

        foreach (var path in Directory.GetFiles(SourceFolder, "*.xaml", SearchOption.AllDirectories).Where(IsSource))
        {
            foreach (Match match in XamlText().Matches(File.ReadAllText(path)))
            {
                var value = WebUtility.HtmlDecode(match.Groups[1].Value);

                if (TwoLetters().IsMatch(value) && !NotTranslated.Contains(value) && !Translations.English.ContainsKey(value))
                {
                    missing.Add($"{Path.GetFileName(path)} : {value}");
                }
            }
        }

        Assert.True(missing.Count == 0, string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void Every_text_of_the_code_is_translated()
    {
        var missing = new List<string>();

        foreach (var path in Directory.GetFiles(SourceFolder, "*.cs", SearchOption.AllDirectories).Where(IsSource))
        {
            if (Path.GetFileName(path) == "Localization.cs")
            {
                continue;
            }

            foreach (var (line, number) in File.ReadLines(path).Select((line, index) => (line.Trim(), index + 1)))
            {
                if (line.StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (Match match in Literal().Matches(line))
                {
                    var value = match.Groups[2].Value.Replace(@"\n", "\n", StringComparison.Ordinal);
                    var isWrapped = line[..match.Index].TrimEnd().EndsWith("Loc.T(", StringComparison.Ordinal) || line.StartsWith("Tip(", StringComparison.Ordinal);
                    var isCatalog = line.StartsWith("new(HotkeyAction.", StringComparison.Ordinal);

                    if (isWrapped)
                    {
                        if (!Translations.English.ContainsKey(value))
                        {
                            missing.Add($"{Path.GetFileName(path)}:{number} : pas de traduction : {value}");
                        }
                    }
                    else if (!isCatalog && !DeveloperMessages.Contains(value) && (Accent().IsMatch(value) || Words().IsMatch(value)))
                    {
                        missing.Add($"{Path.GetFileName(path)}:{number} : sans Loc.T : {value}");
                    }
                }
            }
        }

        Assert.True(missing.Count == 0, string.Join(Environment.NewLine, missing));
    }

    private static string SourceFolder
    {
        get
        {
            var folder = new DirectoryInfo(AppContext.BaseDirectory);

            while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "LegendaryPen.sln")))
            {
                folder = folder.Parent;
            }

            return Path.Combine(folder!.FullName, "src");
        }
    }

    private static bool IsSource(string path)
    {
        var separator = Path.DirectorySeparatorChar;

        return !path.Contains($"{separator}bin{separator}", StringComparison.Ordinal)
            && !path.Contains($"{separator}obj{separator}", StringComparison.Ordinal);
    }

    private static List<string> Placeholders(string text) =>
        Placeholder().Matches(text).Select(match => match.Value).Order().ToList();

    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"\b(?:Title|Content|Text|ToolTip|Header|AutomationProperties\.Name)=""([^""{]*)""")]
    private static partial Regex XamlText();

    [GeneratedRegex(@"[A-Za-zÀ-ÿ]{2}")]
    private static partial Regex TwoLetters();

    [GeneratedRegex(@"(\$?)""((?:[^""\\]|\\.)*)""")]
    private static partial Regex Literal();

    [GeneratedRegex(@"[À-ÿ]")]
    private static partial Regex Accent();

    [GeneratedRegex(@"\b[A-Za-zÀ-ÿ]{2,} [a-zà-ÿ]{2,}\b")]
    private static partial Regex Words();
}
