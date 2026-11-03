using System.Windows;

namespace LegendaryPen.App;

/// <summary>Message à l'utilisateur, à l'apparence de l'application.</summary>
public partial class MessageWindow : Window
{
    public MessageWindow(string message)
    {
        InitializeComponent();
        MessageText.Text = message;
    }

    public static void Show(string message) => new MessageWindow(message).ShowDialog();

    private void OnOkClicked(object sender, RoutedEventArgs e) => Close();
}
