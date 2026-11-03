using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LegendaryPen.Core;

/// <summary>Contenu de latest.json, le manifeste publié avec chaque release GitHub.</summary>
public sealed record UpdateManifest(
    Version Version,
    string InstallerUrl,
    string Sha256,
    string Signature,
    IReadOnlyList<string> Notes)
{
    public const string ManifestUrl = "https://github.com/Lixirian/Legendary-Pen/releases/latest/download/latest.json";

    private const string AllowedDownloadPrefix = "https://github.com/Lixirian/Legendary-Pen/releases/download/";

    /// <summary>Renvoie null si le manifeste est invalide ou pointe hors des releases du dépôt.</summary>
    public static UpdateManifest? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var version = root.GetProperty("version").GetString();
            var url = root.GetProperty("url").GetString();
            var sha256 = root.GetProperty("sha256").GetString();
            var signature = root.GetProperty("sig").GetString();

            if (!Version.TryParse(version, out var parsedVersion) ||
                url is null ||
                !url.StartsWith(AllowedDownloadPrefix, StringComparison.Ordinal) ||
                sha256 is not { Length: 64 } ||
                !sha256.All(Uri.IsHexDigit) ||
                string.IsNullOrEmpty(signature))
            {
                return null;
            }

            var notes = root.TryGetProperty("notes", out var items) && items.ValueKind == JsonValueKind.Array
                ? items.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToList()
                : [];

            return new UpdateManifest(parsedVersion, url, sha256.ToLowerInvariant(), signature, notes);
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

    /// <summary>Vérifie que le manifeste a bien été signé par la clé privée de publication.</summary>
    public bool HasValidSignature(string publicKeyXml)
    {
        try
        {
            using var rsa = RSA.Create();
            rsa.FromXmlString(publicKeyXml);

            return rsa.VerifyData(
                Encoding.UTF8.GetBytes(SignedText(Version, Sha256)),
                Convert.FromBase64String(Signature),
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            return false;
        }
    }

    public bool MatchesInstaller(Stream installer)
    {
        return Convert.ToHexStringLower(SHA256.HashData(installer)) == Sha256;
    }

    public static string SignedText(Version version, string sha256) => $"LegendaryPen|{version}|{sha256}";
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
