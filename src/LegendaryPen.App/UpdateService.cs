using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using LegendaryPen.Core;

namespace LegendaryPen.App;

/// <summary>Cherche, télécharge et lance les mises à jour publiées sur GitHub.</summary>
public sealed class UpdateService
{
    private const long MaxInstallerBytes = 500L * 1024 * 1024;

    private static readonly HttpClient Http = CreateClient();

    private static readonly string DownloadDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Legendary Pen",
        "updates");

    public static string InstallDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs",
        "Legendary Pen");

    public static Version CurrentVersion { get; } =
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0);

    /// <summary>Faux dans un dossier de développement ou si aucune clé publique n'est inscrite.</summary>
    public static bool IsEnabled =>
        UpdateConfig.ReleasePublicKey.Length > 0 &&
        InstallLocation.IsInstalledCopy(Environment.ProcessPath, InstallDirectory);

    /// <summary>Renvoie la mise à jour disponible, ou null. Une erreur réseau est retentée au contrôle suivant.</summary>
    public async Task<UpdateManifest?> CheckAsync()
    {
        try
        {
            var manifest = UpdateManifest.Parse(await Http.GetStringAsync(UpdateManifest.ManifestUrl));

            return manifest is not null &&
                   manifest.IsNewerThan(CurrentVersion) &&
                   manifest.HasValidSignature(UpdateConfig.ReleasePublicKey)
                ? manifest
                : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    /// <summary>Télécharge l'installeur et ne renvoie son chemin que s'il correspond au manifeste signé.</summary>
    public async Task<string> DownloadAsync(UpdateManifest manifest, IProgress<double> progress, CancellationToken cancellation)
    {
        Directory.CreateDirectory(DownloadDirectory);
        var path = Path.Combine(DownloadDirectory, $"LegendaryPen-Setup-{manifest.Version}.exe");

        using var response = await Http.GetAsync(manifest.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, cancellation);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;

        if (total > MaxInstallerBytes)
        {
            throw new InvalidDataException("L'installeur annoncé est anormalement volumineux.");
        }

        await using (var source = await response.Content.ReadAsStreamAsync(cancellation))
        await using (var target = File.Create(path))
        {
            var buffer = new byte[81920];
            long received = 0;
            int read;

            while ((read = await source.ReadAsync(buffer, cancellation)) > 0)
            {
                received += read;

                if (received > MaxInstallerBytes)
                {
                    throw new InvalidDataException("L'installeur téléchargé est anormalement volumineux.");
                }

                await target.WriteAsync(buffer.AsMemory(0, read), cancellation);

                if (total > 0)
                {
                    progress.Report((double)received / total.Value);
                }
            }
        }

        bool authentic;

        await using (var downloaded = File.OpenRead(path))
        {
            authentic = manifest.MatchesInstaller(downloaded);
        }

        if (!authentic)
        {
            File.Delete(path);
            throw new InvalidDataException("L'installeur téléchargé ne correspond pas à la version signée.");
        }

        return path;
    }

    public static void StartInstaller(string path)
    {
        Process.Start(new ProcessStartInfo(path, "/SILENT /SUPPRESSMSGBOXES /NORESTART") { UseShellExecute = true });
    }

    public static void DeleteOldDownloads()
    {
        try
        {
            if (Directory.Exists(DownloadDirectory))
            {
                Directory.Delete(DownloadDirectory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // L'installeur peut être encore en cours de fermeture : le prochain lancement réessaiera.
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LegendaryPen");
        return client;
    }
}
