using System.Globalization;

namespace LegendaryPen.Core;

public enum AppLanguage
{
    System,
    French,
    English,
}

/// <summary>
/// Traduction de l'interface. Le français sert de texte source et de clé : un texte absent du
/// dictionnaire s'affiche en français plutôt que de disparaître.
/// </summary>
public static class Loc
{
    private static bool _english;

    public static bool IsEnglish => _english;

    /// <summary>« Selon Windows » donne le français sur un Windows français, l'anglais partout ailleurs.</summary>
    public static AppLanguage Resolve(AppLanguage language, string systemLanguage) => language switch
    {
        AppLanguage.System => string.Equals(systemLanguage, "fr", StringComparison.OrdinalIgnoreCase)
            ? AppLanguage.French
            : AppLanguage.English,
        _ => language,
    };

    public static void Use(AppLanguage language, string systemLanguage)
    {
        _english = Resolve(language, systemLanguage) == AppLanguage.English;
    }

    public static string T(string french)
    {
        return _english && Translations.English.TryGetValue(french, out var english) ? english : french;
    }

    public static string T(string french, params object[] values)
    {
        return string.Format(CultureInfo.CurrentCulture, T(french), values);
    }
}

/// <summary>Dictionnaire français vers anglais. Un test vérifie qu'aucun texte de l'interface n'y manque.</summary>
public static class Translations
{
    public static IReadOnlyDictionary<string, string> English { get; } = new Dictionary<string, string>
    {
        // Sélecteur de couleur
        ["Couleur du stylo"] = "Pen color",
        ["Aperçu de la couleur"] = "Color preview",
        ["Teinte"] = "Hue",
        ["Saturation"] = "Saturation",
        ["Luminosité"] = "Brightness",
        ["Code"] = "Code",
        ["Code hexadécimal"] = "Hexadecimal code",
        ["Remplacer la couleur rapide sélectionnée"] = "Replace the selected quick color",
        ["Utiliser"] = "Use",
        ["Annuler"] = "Cancel",

        // Signature de PDF
        ["Signer un PDF"] = "Sign a PDF",
        ["Signer un PDF - {0}"] = "Sign a PDF - {0}",
        ["Ouvrir un PDF"] = "Open a PDF",
        ["Page précédente"] = "Previous page",
        ["Page suivante"] = "Next page",
        ["Page {0} / {1}"] = "Page {0} / {1}",
        ["Enregistrer le PDF signé"] = "Save the signed PDF",
        ["Tracer une signature"] = "Draw a signature",
        ["Poser ma signature"] = "Place my signature",
        ["Retirer de la page"] = "Remove from page",
        ["Retire la signature sélectionnée (Suppr)"] = "Removes the selected signature (Delete)",
        ["Oublier ma signature"] = "Forget my signature",
        ["Supprime la signature mémorisée sur cet ordinateur"] = "Deletes the signature saved on this computer",
        ["Ouvrez un PDF pour commencer. Le fichier d'origine n'est jamais modifié."] = "Open a PDF to begin. The original file is never changed.",
        ["Signature visuelle : elle n'a pas la valeur d'une signature électronique certifiée."] = "Visual signature: it does not have the value of a certified electronic signature.",
        ["Signature posée"] = "Placed signature",
        ["Signez dans le cadre, à la souris ou au stylet."] = "Sign inside the frame, with the mouse or a stylus.",
        ["Zone de signature"] = "Signature area",
        ["Mémoriser cette signature sur cet ordinateur"] = "Remember this signature on this computer",
        ["Effacer"] = "Clear",
        ["Poser sur la page"] = "Place on the page",
        ["Document PDF (*.pdf)|*.pdf"] = "PDF document (*.pdf)|*.pdf",
        ["Ce PDF ne peut pas être signé ici : il est protégé, interdit la modification ou est endommagé. Il n'a pas été modifié."] = "This PDF cannot be signed here: it is protected, forbids changes or is damaged. It has not been changed.",
        ["Windows ne parvient pas à afficher ce PDF. Il n'a pas été modifié."] = "Windows cannot display this PDF. It has not been changed.",
        ["Ce PDF porte une signature numérique. Elle restera valable dans l'original, mais plus dans la copie que vous enregistrerez ici."] = "This PDF carries a digital signature. It stays valid in the original, but not in the copy you save here.",
        ["Enregistré : {0}"] = "Saved: {0}",
        ["Impossible d'enregistrer le PDF signé. L'original n'a pas été modifié.\n\n{0}"] = "Could not save the signed PDF. The original has not been changed.\n\n{0}",
        ["La signature mémorisée est illisible. Tracez-la de nouveau."] = "The saved signature cannot be read. Please draw it again.",
        ["La signature mémorisée a été supprimée de cet ordinateur."] = "The saved signature has been deleted from this computer.",
        ["Choisissez un autre nom : le fichier d'origine n'est jamais remplacé."] = "Choose another name: the original file is never replaced.",
        ["La copie signée ne peut pas remplacer le fichier d'origine."] = "The signed copy cannot replace the original file.",
        ["{0} (signé).pdf"] = "{0} (signed).pdf",
        ["{0} (signé {1}).pdf"] = "{0} (signed {1}).pdf",

        // Réglages
        ["Réglages de Legendary Pen"] = "Legendary Pen settings",
        ["Réglages"] = "Settings",
        ["Enregistrer"] = "Save",
        ["Général"] = "General",
        ["Démarrage"] = "Startup",
        ["Lancer Legendary Pen à l'ouverture de session"] = "Start Legendary Pen when I sign in",
        ["Disponible uniquement depuis la version installée."] = "Only available from the installed version.",
        ["Langue"] = "Language",
        ["Selon Windows"] = "Same as Windows",
        ["Français"] = "Français",
        ["English"] = "English",
        ["Le changement s'applique au prochain lancement de l'application."] = "The change applies the next time the application starts.",
        ["Barre d'outils"] = "Toolbar",
        ["Une barre attachée à un bord se replie après :"] = "A toolbar attached to an edge collapses after:",
        ["Délai de repli"] = "Collapse delay",
        ["Dessin"] = "Drawing",
        ["Saisie"] = "Input",
        ["La molette change la taille du trait en mode Dessin"] = "The mouse wheel changes the stroke size in Draw mode",
        ["Tenir compte de la pression du stylet"] = "Use stylus pressure",
        ["Redresser le surligneur et le caler sur le texte"] = "Straighten the highlighter and fit it to the text",
        ["Reconnaître les formes dessinées au stylo"] = "Recognize shapes drawn with the pen",
        ["Mémoriser une taille par outil"] = "Remember a size for each tool",
        ["Annotations"] = "Annotations",
        ["Retrouver les annotations à la réouverture"] = "Restore annotations when reopening",
        ["Elles restent sur cet ordinateur, sans chiffrement. Désactiver cette option les supprime."] = "They stay on this computer, unencrypted. Turning this option off deletes them.",
        ["Encre évanescente"] = "Fading ink",
        ["Encre évanescente (F)"] = "Fading ink (F)",
        ["Délai avant la disparition :"] = "Delay before fading:",
        ["Délai avant la disparition"] = "Delay before fading",
        ["Durée du fondu :"] = "Fade duration:",
        ["Durée du fondu"] = "Fade duration",
        ["Tableaux"] = "Boards",
        ["Afficher le tableau uniquement sur l'écran de la barre d'outils"] = "Show the board only on the toolbar's screen",
        ["Capture"] = "Capture",
        ["Après une capture"] = "After a capture",
        ["Enregistrer dans un dossier"] = "Save to a folder",
        ["Copier dans le presse-papiers"] = "Copy to the clipboard",
        ["Demander où enregistrer à chaque fois"] = "Ask where to save each time",
        ["Dossier"] = "Folder",
        ["Parcourir…"] = "Browse…",
        ["Dossier des captures"] = "Captures folder",
        ["Raccourcis"] = "Shortcuts",
        ["Cliquez sur un champ puis appuyez sur la combinaison souhaitée. Sauf mention contraire, un raccourci n'est actif qu'en mode Dessin."] = "Click a field, then press the combination you want. Unless stated otherwise, a shortcut only works in Draw mode.",
        ["Rétablir les raccourcis par défaut"] = "Restore default shortcuts",
        ["Aucun"] = "None",
        ["Désactiver : {0}"] = "Disable: {0}",
        ["{0} (toujours actif)"] = "{0} (always active)",
        ["Enregistrement impossible : {0}"] = "Could not save: {0}",
        ["Lecture impossible : {0}"] = "Could not read: {0}",
        ["Réglages de Legendary Pen. {0}"] = "Legendary Pen settings. {0}",

        // Thème et barre d'outils
        ["Fermer"] = "Close",
        ["Déplier la barre"] = "Expand the toolbar",
        ["Déplacer la barre"] = "Move the toolbar",
        ["Mode Souris (Échap)"] = "Mouse mode (Esc)",
        ["Mode Souris"] = "Mouse mode",
        ["Stylo (P)"] = "Pen (P)",
        ["Stylo"] = "Pen",
        ["Surligneur (H)"] = "Highlighter (H)",
        ["Surligneur"] = "Highlighter",
        ["Gomme (E)"] = "Eraser (E)",
        ["Gomme"] = "Eraser",
        ["Ligne (L). Maj : par pas de 45°"] = "Line (L). Shift: 45° steps",
        ["Ligne"] = "Line",
        ["Flèche (A). Maj : par pas de 45°"] = "Arrow (A). Shift: 45° steps",
        ["Flèche"] = "Arrow",
        ["Rectangle (R). Maj : carré"] = "Rectangle (R). Shift: square",
        ["Rectangle"] = "Rectangle",
        ["Ellipse (O). Maj : cercle"] = "Ellipse (O). Shift: circle",
        ["Ellipse"] = "Ellipse",
        ["Texte (T). Échap ou clic ailleurs : valider"] = "Text (T). Esc or click elsewhere: confirm",
        ["Texte"] = "Text",
        ["Texte de l'annotation"] = "Annotation text",
        ["Maj : par pas de 45°"] = "Shift: 45° steps",
        ["Maj : carré"] = "Shift: square",
        ["Maj : cercle"] = "Shift: circle",
        ["Échap ou clic ailleurs : valider"] = "Esc or click elsewhere: confirm",
        ["Tableau blanc (W)"] = "Whiteboard (W)",
        ["Tableau blanc"] = "Whiteboard",
        ["Tableau noir (B)"] = "Blackboard (B)",
        ["Tableau noir"] = "Blackboard",
        ["Autre couleur"] = "Other color",
        ["Couleur précédente"] = "Previous color",
        ["Clic : choisir. Clic droit : modifier"] = "Click: select. Right-click: edit",
        ["Couleur rapide {0}"] = "Quick color {0}",
        ["Trait fin"] = "Thin stroke",
        ["Trait moyen"] = "Medium stroke",
        ["Trait épais"] = "Thick stroke",
        ["Taille du trait"] = "Stroke size",
        ["Annuler (Ctrl+Z)"] = "Undo (Ctrl+Z)",
        ["Annuler la dernière action"] = "Undo",
        ["Rétablir (Ctrl+Y)"] = "Redo (Ctrl+Y)",
        ["Rétablir"] = "Redo",
        ["Tout effacer (Suppr)"] = "Clear all (Delete)",
        ["Tout effacer"] = "Clear all",
        ["Masquer les annotations (V)"] = "Hide annotations (V)",
        ["Masquer les annotations"] = "Hide annotations",
        ["Capture d'écran"] = "Screenshot",
        ["Changer l'orientation de la barre"] = "Change the toolbar orientation",
        ["Mettre la barre à l'horizontale"] = "Make the toolbar horizontal",
        ["Mettre la barre à la verticale"] = "Make the toolbar vertical",
        ["Mise à jour disponible"] = "Update available",
        ["Quitter"] = "Quit",
        ["Le raccourci {0} est déjà utilisé par une autre application. Les boutons de la barre restent disponibles, et les réglages permettent d'en choisir un autre."] = "The shortcut {0} is already used by another application. The toolbar buttons remain available, and you can choose another one in the settings.",
        ["Afficher ou masquer la barre"] = "Show or hide the toolbar",

        // Mode présentation
        ["Présentation"] = "Presentation",
        ["Projecteur"] = "Spotlight",
        ["Loupe"] = "Magnifier",
        ["Effets de clic"] = "Click effects",

        // Calques
        ["Calques"] = "Layers",
        ["Calque {0}"] = "Layer {0}",
        ["Ajouter"] = "Add",
        ["Supprimer"] = "Delete",
        ["Ajouter un calque"] = "Add a layer",
        ["Supprimer le calque"] = "Delete the layer",
        ["Afficher ou masquer le calque"] = "Show or hide the layer",
        ["Monter le calque"] = "Move the layer up",
        ["Descendre le calque"] = "Move the layer down",

        // Mises à jour
        ["Mise à jour de Legendary Pen"] = "Legendary Pen update",
        ["L'application va se fermer puis redémarrer. Les annotations à l'écran seront perdues, sauf si l'option pour les retrouver est activée."] = "The application will close and restart. On-screen annotations will be lost, unless the option to restore them is on.",
        ["Téléchargement"] = "Downloading",
        ["Mettre à jour"] = "Update",
        ["Plus tard"] = "Later",
        ["Une nouvelle version est disponible"] = "A new version is available",
        ["Version actuelle : {0}   →   Nouvelle version : {1}"] = "Current version: {0}   →   New version: {1}",
        ["La mise à jour a échoué : {0}"] = "The update failed: {0}",
        ["L'installeur annoncé est anormalement volumineux."] = "The announced installer is abnormally large.",
        ["L'installeur téléchargé est anormalement volumineux."] = "The downloaded installer is abnormally large.",
        ["L'installeur téléchargé ne correspond pas à la version signée."] = "The downloaded installer does not match the signed version.",

        // Capture et notifications
        ["Capture copiée dans le presse-papiers."] = "Capture copied to the clipboard.",
        ["La capture n'a pas pu être enregistrée : {0}"] = "The capture could not be saved: {0}",
        ["Capture enregistrée : {0}"] = "Capture saved: {0}",
        ["Glissez pour choisir une zone  ·  Cliquez pour l'écran entier  ·  Échap pour annuler"] = "Drag to select an area  ·  Click for the whole screen  ·  Esc to cancel",
        ["Enregistrer la capture"] = "Save the capture",
        ["Image PNG (*.png)|*.png"] = "PNG image (*.png)|*.png",
        ["Windows a refusé la capture de l'écran."] = "Windows refused the screen capture.",
        ["Impossible d'enregistrer les annotations."] = "Could not save the annotations.",
        ["Impossible de supprimer les annotations enregistrées.\n\n{0}"] = "Could not delete the saved annotations.\n\n{0}",

        // Enregistrement vidéo
        ["Enregistrer l'écran"] = "Record the screen",
        ["Arrêter l'enregistrement"] = "Stop recording",
        ["Annuler le décompte"] = "Cancel the countdown",
        ["Finalisation de la vidéo…"] = "Finishing the video…",
        ["L'enregistrement va commencer"] = "Recording is about to start",
        ["Vidéo enregistrée : {0}"] = "Video saved: {0}",
        ["Plus assez de place sur le disque : enregistrement arrêté."] = "Not enough disk space: recording stopped.",
        ["L'écran enregistré a été débranché."] = "The recorded screen was disconnected.",
        ["La résolution de l'écran a changé : enregistrement arrêté."] = "The screen resolution changed: recording stopped.",
        ["L'enregistrement s'est arrêté sur une erreur. Vidéo gardée : {0}"] = "Recording stopped on an error. Video kept: {0}",
        ["Enregistrement impossible sur ce PC."] = "Recording is not possible on this PC.",
        ["Le dossier des vidéos n'a pas pu être créé : {0}"] = "The video folder could not be created: {0}",
        ["Enregistrement vidéo"] = "Screen recording",
        ["Son de la vidéo :"] = "Video sound:",
        ["Son de l'ordinateur"] = "Computer sound",
        ["Micro"] = "Microphone",

        // Raccourcis
        ["Basculer entre Souris et Dessin"] = "Switch between Mouse and Draw",
        ["Quitter le mode Dessin"] = "Exit Draw mode",
        ["Maj"] = "Shift",
        ["Retour arrière"] = "Backspace",
        ["Entrée"] = "Enter",
        ["Échap"] = "Esc",
        ["Espace"] = "Space",
        ["Page préc."] = "Page Up",
        ["Page suiv."] = "Page Down",
        ["Fin"] = "End",
        ["Début"] = "Home",
        ["Gauche"] = "Left",
        ["Haut"] = "Up",
        ["Droite"] = "Right",
        ["Bas"] = "Down",
        ["Impr. écran"] = "Print Screen",
        ["Inser"] = "Insert",
        ["Suppr"] = "Delete",
        ["Pavé {0}"] = "Numpad {0}",
        ["Touche {0}"] = "Key {0}",
        ["{0} est attribué à : {1}."] = "{0} is assigned to: {1}.",
        ["« {0} » reste actif en permanence : il lui faut Ctrl, Alt ou Win, sinon cette touche ne fonctionnerait plus ailleurs."] = "\"{0}\" is always active: it needs Ctrl, Alt or Win, otherwise this key would stop working elsewhere.",
        ["« {0} » : Ctrl+Alt équivaut à AltGr sur un clavier français et gênerait la saisie."] = "\"{0}\": Ctrl+Alt is the same as AltGr on some keyboards and would interfere with typing.",
    };
}
