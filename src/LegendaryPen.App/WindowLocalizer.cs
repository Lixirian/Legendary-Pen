using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using LegendaryPen.Core;

namespace LegendaryPen.App;

/// <summary>
/// Traduit les textes du XAML au chargement de chaque fenêtre. Ils restent écrits en français dans
/// les fichiers ; les textes posés par le code passent directement par <see cref="Loc"/>.
/// </summary>
public static class WindowLocalizer
{
    public static void Register()
    {
        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => Translate((Window)sender)));
    }

    private static void Translate(Window window)
    {
        if (Loc.IsEnglish)
        {
            Visit(window, []);
        }
    }

    private static void Visit(DependencyObject node, HashSet<DependencyObject> visited)
    {
        if (!visited.Add(node))
        {
            return;
        }

        TranslateProperties(node);

        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
        {
            Visit(child, visited);
        }

        // Le bas du modèle d'une fenêtre, comme le bouton Fermer du thème, n'est pas dans l'arbre logique.
        if (node is Visual)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                Visit(VisualTreeHelper.GetChild(node, i), visited);
            }
        }
    }

    private static void TranslateProperties(DependencyObject node)
    {
        var name = AutomationProperties.GetName(node);

        if (!string.IsNullOrEmpty(name) && Loc.T(name) != name)
        {
            AutomationProperties.SetName(node, Loc.T(name));
        }

        if (node is FrameworkElement { ToolTip: string tip } element && Loc.T(tip) != tip)
        {
            element.ToolTip = Loc.T(tip);
        }

        switch (node)
        {
            case Window window when Loc.T(window.Title) != window.Title:
                window.Title = Loc.T(window.Title);
                break;
            case TextBlock text when Loc.T(text.Text) != text.Text:
                text.Text = Loc.T(text.Text);
                break;
            case HeaderedContentControl { Header: string header } headered when Loc.T(header) != header:
                headered.Header = Loc.T(header);
                break;
        }

        if (node is ContentControl { Content: string content } control && Loc.T(content) != content)
        {
            control.Content = Loc.T(content);
        }
    }
}
