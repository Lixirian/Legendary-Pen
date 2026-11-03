using System.Windows;

namespace LegendaryPen.App;

/// <summary>Apparence commune des fenêtres, reprise de la barre d'outils.</summary>
public partial class Theme : ResourceDictionary
{
    public Theme()
    {
        InitializeComponent();
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        Window.GetWindow((DependencyObject)sender)?.Close();
    }
}
