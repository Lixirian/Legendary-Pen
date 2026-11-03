namespace LegendaryPen.Core;

public enum HotkeyAction
{
    ToggleMode,
    ToggleToolbar,
    ExitDraw,
    Undo,
    Redo,
    ClearAll,
    Pen,
    Highlighter,
    Eraser,
    Line,
    Arrow,
    Rectangle,
    Ellipse,
    Text,
    Whiteboard,
    Blackboard,
    Fading,
    HideAnnotations,
    Capture,
    PreviousColor,
}

/// <summary>Combinaison de touches : modificateurs Windows et code de touche virtuelle. La touche 0 désactive.</summary>
public readonly record struct HotkeyBinding(uint Modifiers, uint Key)
{
    public const uint Alt = 0x0001;
    public const uint Control = 0x0002;
    public const uint Shift = 0x0004;
    public const uint Windows = 0x0008;

    private const uint VkF1 = 0x70;
    private const uint VkF24 = 0x87;

    public static HotkeyBinding None => default;

    public bool IsNone => Key == 0;

    public static HotkeyBinding Letter(char letter, uint modifiers = 0) => new(modifiers, char.ToUpperInvariant(letter));

    public string Format()
    {
        if (IsNone)
        {
            return "Aucun";
        }

        var parts = new List<string>();

        if ((Modifiers & Control) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((Modifiers & Alt) != 0)
        {
            parts.Add("Alt");
        }

        if ((Modifiers & Shift) != 0)
        {
            parts.Add(Loc.T("Maj"));
        }

        if ((Modifiers & Windows) != 0)
        {
            parts.Add("Win");
        }

        parts.Add(FormatKey(Key));

        return string.Join("+", parts);
    }

    private static string FormatKey(uint key)
    {
        return key switch
        {
            0x08 => Loc.T("Retour arrière"),
            0x09 => "Tab",
            0x0D => Loc.T("Entrée"),
            0x1B => Loc.T("Échap"),
            0x20 => Loc.T("Espace"),
            0x21 => Loc.T("Page préc."),
            0x22 => Loc.T("Page suiv."),
            0x23 => Loc.T("Fin"),
            0x24 => Loc.T("Début"),
            0x25 => Loc.T("Gauche"),
            0x26 => Loc.T("Haut"),
            0x27 => Loc.T("Droite"),
            0x28 => Loc.T("Bas"),
            0x2C => Loc.T("Impr. écran"),
            0x2D => Loc.T("Inser"),
            0x2E => Loc.T("Suppr"),
            >= '0' and <= '9' or >= 'A' and <= 'Z' => ((char)key).ToString(),
            >= 0x60 and <= 0x69 => Loc.T("Pavé {0}", key - 0x60),
            >= VkF1 and <= VkF24 => $"F{key - VkF1 + 1}",
            _ => Loc.T("Touche {0}", key.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)),
        };
    }
}

/// <param name="IsGlobal">Actif en permanence ; sinon, seulement en mode Dessin.</param>
public sealed record HotkeyDefinition(HotkeyAction Action, string FrenchLabel, HotkeyBinding Default, bool IsGlobal = false)
{
    public string Label => Loc.T(FrenchLabel);
}

public static class HotkeyCatalog
{
    private const uint Space = 0x20;
    private const uint Escape = 0x1B;
    private const uint Delete = 0x2E;
    private const uint F9 = 0x78;

    public static IReadOnlyList<HotkeyDefinition> All { get; } =
    [
        new(HotkeyAction.ToggleMode, "Basculer entre Souris et Dessin", new(HotkeyBinding.Control | HotkeyBinding.Shift, Space), IsGlobal: true),
        new(HotkeyAction.ToggleToolbar, "Afficher ou masquer la barre", new(HotkeyBinding.Control | HotkeyBinding.Shift, F9), IsGlobal: true),
        new(HotkeyAction.ExitDraw, "Quitter le mode Dessin", new(0, Escape)),
        new(HotkeyAction.Undo, "Annuler la dernière action", HotkeyBinding.Letter('Z', HotkeyBinding.Control)),
        new(HotkeyAction.Redo, "Rétablir", HotkeyBinding.Letter('Y', HotkeyBinding.Control)),
        new(HotkeyAction.ClearAll, "Tout effacer", new(0, Delete)),
        new(HotkeyAction.Pen, "Stylo", HotkeyBinding.Letter('P')),
        new(HotkeyAction.Highlighter, "Surligneur", HotkeyBinding.Letter('H')),
        new(HotkeyAction.Eraser, "Gomme", HotkeyBinding.Letter('E')),
        new(HotkeyAction.Line, "Ligne", HotkeyBinding.Letter('L')),
        new(HotkeyAction.Arrow, "Flèche", HotkeyBinding.Letter('A')),
        new(HotkeyAction.Rectangle, "Rectangle", HotkeyBinding.Letter('R')),
        new(HotkeyAction.Ellipse, "Ellipse", HotkeyBinding.Letter('O')),
        new(HotkeyAction.Text, "Texte", HotkeyBinding.Letter('T')),
        new(HotkeyAction.Whiteboard, "Tableau blanc", HotkeyBinding.Letter('W')),
        new(HotkeyAction.Blackboard, "Tableau noir", HotkeyBinding.Letter('B')),
        new(HotkeyAction.Fading, "Encre évanescente", HotkeyBinding.Letter('F')),
        new(HotkeyAction.HideAnnotations, "Masquer les annotations", HotkeyBinding.Letter('V')),
        new(HotkeyAction.Capture, "Capture d'écran", HotkeyBinding.Letter('C')),
        new(HotkeyAction.PreviousColor, "Couleur précédente", HotkeyBinding.Letter('X')),
    ];
}

/// <summary>Raccourcis effectifs : ceux enregistrés, complétés par les valeurs par défaut.</summary>
public sealed class HotkeyMap
{
    private readonly Dictionary<HotkeyAction, HotkeyBinding> _bindings = [];

    public HotkeyMap(IReadOnlyDictionary<string, HotkeyBinding>? saved = null)
    {
        foreach (var definition in HotkeyCatalog.All)
        {
            _bindings[definition.Action] =
                saved is not null && saved.TryGetValue(definition.Action.ToString(), out var binding)
                    ? binding
                    : definition.Default;
        }
    }

    public HotkeyBinding this[HotkeyAction action] => _bindings[action];

    public HotkeyMap With(HotkeyAction action, HotkeyBinding binding)
    {
        var copy = new HotkeyMap(ToSettings());
        copy._bindings[action] = binding;
        return copy;
    }

    public Dictionary<string, HotkeyBinding> ToSettings()
    {
        return _bindings.ToDictionary(pair => pair.Key.ToString(), pair => pair.Value);
    }

    /// <summary>Renvoie les problèmes à corriger avant d'enregistrer. Une liste vide signifie que tout est valable.</summary>
    public IReadOnlyList<string> Validate()
    {
        var issues = new List<string>();

        var duplicates = HotkeyCatalog.All
            .Where(definition => !this[definition.Action].IsNone)
            .GroupBy(definition => this[definition.Action])
            .Where(group => group.Count() > 1);

        foreach (var group in duplicates)
        {
            issues.Add(Loc.T("{0} est attribué à : {1}.", group.Key.Format(), string.Join(", ", group.Select(definition => definition.Label))));
        }

        foreach (var definition in HotkeyCatalog.All)
        {
            var binding = this[definition.Action];

            if (binding.IsNone)
            {
                continue;
            }

            if (definition.IsGlobal && (binding.Modifiers & ~HotkeyBinding.Shift) == 0)
            {
                issues.Add(Loc.T("« {0} » reste actif en permanence : il lui faut Ctrl, Alt ou Win, sinon cette touche ne fonctionnerait plus ailleurs.", definition.Label));
            }

            if ((binding.Modifiers & HotkeyBinding.Control) != 0 && (binding.Modifiers & HotkeyBinding.Alt) != 0)
            {
                issues.Add(Loc.T("« {0} » : Ctrl+Alt équivaut à AltGr sur un clavier français et gênerait la saisie.", definition.Label));
            }
        }

        return issues;
    }
}
