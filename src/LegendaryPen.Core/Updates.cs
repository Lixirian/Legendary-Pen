using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LegendaryPen.Core;

/// <summary>Contenu signé de latest.json, le manifeste publié avec chaque release GitHub.</summary>
public sealed record UpdateManifest(
    Version Version,
    string InstallerUrl,
    string Sha256,
    IReadOnlyList<string> Notes)
{
    public const string ManifestUrl = "https://github.com/Lixirian/Legendary-Pen/releases/latest/download/latest.json";

    /// <summary>Un manifeste ne fait que quelques kilo-octets : au-delà, il n'est pas lu.</summary>
    public const int MaxManifestBytes = 1024 * 1024;

    private const string AllowedDownloadPath = "/Lixirian/Legendary-Pen/releases/download/";

    private static readonly byte[] SignaturePrefix = Encoding.UTF8.GetBytes("LegendaryPen-manifest-v2\n");

    /// <summary>
    /// Renvoie le manifeste seulement si la clé de publication l'a signé en entier. Seul le contenu signé est lu : les champs
    /// en clair du fichier ne servent qu'aux versions 0.10.2 et antérieures, et sont donc ignorés ici.
    /// </summary>
    public static UpdateManifest? ParseVerified(string json, string publicKeyXml)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var payload = Convert.FromBase64String(root.GetProperty("signed").GetString() ?? string.Empty);
            var signature = Convert.FromBase64String(root.GetProperty("sig2").GetString() ?? string.Empty);

            using var rsa = RSA.Create();
            rsa.FromXmlString(publicKeyXml);

            return rsa.VerifyData(SignaturePrefix.Concat(payload).ToArray(), signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                ? ParsePayload(Encoding.UTF8.GetString(payload))
                : null;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or CryptographicException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Lit le contenu signé. Renvoie null s'il est invalide ou pointe hors des releases du dépôt.</summary>
    public static UpdateManifest? ParsePayload(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var version = root.GetProperty("version").GetString();
            var url = root.GetProperty("url").GetString();
            var sha256 = root.GetProperty("sha256").GetString();

            if (!Version.TryParse(version, out var parsedVersion) ||
                url is null ||
                !IsReleaseDownload(url) ||
                sha256 is not { Length: 64 } ||
                !sha256.All(Uri.IsHexDigit))
            {
                return null;
            }

            var notes = root.TryGetProperty("notes", out var items) && items.ValueKind == JsonValueKind.Array
                ? items.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToList()
                : [];

            return new UpdateManifest(parsedVersion, url, sha256.ToLowerInvariant(), notes);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    public bool IsNewerThan(Version current)
    {
        // 1.2.3 et 1.2.3.0 désignent la même version, alors que Version les ordonne différemment.
        static (int, int, int) Key(Version version) =>
            (version.Major, version.Minor, Math.Max(version.Build, 0));

        return Key(Version).CompareTo(Key(current)) > 0;
    }

    public bool MatchesInstaller(Stream installer)
    {
        return Convert.ToHexStringLower(SHA256.HashData(installer)) == Sha256;
    }

    // Uri normalise le chemin : un « ../ » ne peut pas faire sortir des releases du dépôt.
    private static bool IsReleaseDownload(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
        uri.IsDefaultPort &&
        uri.UserInfo.Length == 0 &&
        uri.AbsolutePath.StartsWith(AllowedDownloadPath, StringComparison.Ordinal);
}

public static class InstallLocation
{
    /// <summary>Les mises à jour ne concernent que la copie installée, jamais un dossier de développement.</summary>
    public static bool IsInstalledCopy(string? executablePath, string installDirectory)
    {
        if (string.IsNullOrEmpty(executablePath))
        {
            return false;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(executablePath));
        return string.Equals(directory, Path.GetFullPath(installDirectory).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    }
}
