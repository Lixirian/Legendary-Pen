using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using LegendaryPen.Core;

namespace LegendaryPen.App;

public partial class UpdateWindow : Window
{
    private readonly UpdateManifest _manifest;
    private readonly UpdateService _updates;
    private readonly CancellationTokenSource _cancellation = new();

    public UpdateWindow(UpdateManifest manifest, UpdateService updates)
    {
        _manifest = manifest;
        _updates = updates;

        InitializeComponent();

        HeadingText.Text = "Une nouvelle version est disponible";
        VersionsText.Text = $"Version actuelle : {FormatVersion(UpdateService.CurrentVersion)}   →   Nouvelle version : {FormatVersion(manifest.Version)}";
        NotesList.ItemsSource = manifest.Notes;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _cancellation.Cancel();
        base.OnClosing(e);
    }

    private async void OnUpdateClicked(object sender, RoutedEventArgs e)
    {
        UpdateNowButton.IsEnabled = false;
        ErrorText.Visibility = Visibility.Collapsed;
        DownloadProgress.Value = 0;
        DownloadProgress.Visibility = Visibility.Visible;

        try
        {
            var progress = new Progress<double>(value => DownloadProgress.Value = value);
            var installer = await _updates.DownloadAsync(_manifest, progress, _cancellation.Token);

            UpdateService.StartInstaller(installer);
            Application.Current.Shutdown();
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            // Fermeture demandée pendant le téléchargement : rien à signaler.
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException
                                              or InvalidDataException or OperationCanceledException
                                              or System.ComponentModel.Win32Exception)
        {
            DownloadProgress.Visibility = Visibility.Collapsed;
            ErrorText.Text = $"La mise à jour a échoué : {exception.Message}";
            ErrorText.Visibility = Visibility.Visible;
            UpdateNowButton.IsEnabled = true;
        }
    }

    private void OnLaterClicked(object sender, RoutedEventArgs e) => Close();

    private static string FormatVersion(Version version) => $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";
}
