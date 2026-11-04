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
    private readonly Func<Task>? _beforeInstall;

    /// <param name="beforeInstall">À attendre avant de lancer l'installateur, s'il y a une vidéo à finir.</param>
    public UpdateWindow(UpdateManifest manifest, UpdateService updates, Func<Task>? beforeInstall = null)
    {
        _manifest = manifest;
        _updates = updates;
        _beforeInstall = beforeInstall;

        InitializeComponent();

        HeadingText.Text = Loc.T("Une nouvelle version est disponible");
        VersionsText.Text = Loc.T("Version actuelle : {0}   →   Nouvelle version : {1}", FormatVersion(UpdateService.CurrentVersion), FormatVersion(manifest.Version));
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

            if (_beforeInstall is not null)
            {
                await _beforeInstall();
                _cancellation.Token.ThrowIfCancellationRequested();
            }

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
            ErrorText.Text = Loc.T("La mise à jour a échoué : {0}", exception.Message);
            ErrorText.Visibility = Visibility.Visible;
            UpdateNowButton.IsEnabled = true;
        }
    }

    private void OnLaterClicked(object sender, RoutedEventArgs e) => Close();

    private static string FormatVersion(Version version) => $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";
}
