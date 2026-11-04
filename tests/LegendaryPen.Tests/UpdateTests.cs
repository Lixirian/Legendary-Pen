using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LegendaryPen.Core;
using Xunit;

namespace LegendaryPen.Tests;

public class UpdateManifestTests
{
    private const string ValidUrl = "https://github.com/Lixirian/Legendary-Pen/releases/download/v1.2.3/LegendaryPen-Setup-1.2.3.exe";
    private static readonly string ValidHash = new('a', 64);

    private static string Payload(string version = "1.2.3", string? url = ValidUrl, string? sha256 = null)
    {
        return JsonSerializer.Serialize(new { version, url, sha256 = sha256 ?? ValidHash, notes = new[] { "Une note" } });
    }

    private static string SignedFile(RSA key, string payload, object? clearFields = null)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        var signature = key.SignData(
            [.. Encoding.UTF8.GetBytes("LegendaryPen-manifest-v2\n"), .. bytes],
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        return JsonSerializer.Serialize(new
        {
            signed = Convert.ToBase64String(bytes),
            sig2 = Convert.ToBase64String(signature),
            clear = clearFields,
        });
    }

    [Fact]
    public void Reads_a_valid_payload()
    {
        var manifest = UpdateManifest.ParsePayload(Payload());

        Assert.NotNull(manifest);
        Assert.Equal(new Version(1, 2, 3), manifest.Version);
        Assert.Equal(ValidUrl, manifest.InstallerUrl);
        Assert.Equal(["Une note"], manifest.Notes);
    }

    [Theory]
    [InlineData("https://example.com/LegendaryPen-Setup.exe")]
    [InlineData("http://github.com/Lixirian/Legendary-Pen/releases/download/v1/a.exe")]
    [InlineData("https://github.com/Autre/Legendary-Pen/releases/download/v1/a.exe")]
    [InlineData("https://github.com/Lixirian/Legendary-Pen.evil.com/releases/download/v1/a.exe")]
    [InlineData("https://github.com/Lixirian/Legendary-Pen/releases/download/../../../../Autre/a.exe")]
    [InlineData("https://github.com.evil.com/Lixirian/Legendary-Pen/releases/download/v1/a.exe")]
    [InlineData("https://user@github.com/Lixirian/Legendary-Pen/releases/download/v1/a.exe")]
    [InlineData("https://github.com:8443/Lixirian/Legendary-Pen/releases/download/v1/a.exe")]
    public void Rejects_downloads_outside_the_repository_releases(string url)
    {
        Assert.Null(UpdateManifest.ParsePayload(Payload(url: url)));
    }

    [Theory]
    [InlineData("pas du json")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("""{"version":3}""")]
    public void Rejects_malformed_payloads(string json)
    {
        Assert.Null(UpdateManifest.ParsePayload(json));
    }

    [Theory]
    [InlineData("pas-une-version", null)]
    [InlineData("1.2.3", "trop-court")]
    public void Rejects_invalid_fields(string version, string? sha256)
    {
        Assert.Null(UpdateManifest.ParsePayload(Payload(version: version, sha256: sha256)));
    }

    [Theory]
    [InlineData("1.2.4", "1.2.3", true)]
    [InlineData("1.10.0", "1.9.9", true)]
    [InlineData("2.0.0", "1.99.99", true)]
    [InlineData("1.2.3", "1.2.3", false)]
    [InlineData("1.2.2", "1.2.3", false)]
    [InlineData("1.2.3", "1.2.3.0", false)]
    [InlineData("1.2.3.0", "1.2.3", false)]
    [InlineData("1.2", "1.2.0", false)]
    public void Compares_versions_numerically(string published, string current, bool expected)
    {
        var manifest = new UpdateManifest(Version.Parse(published), ValidUrl, ValidHash, []);

        Assert.Equal(expected, manifest.IsNewerThan(Version.Parse(current)));
    }

    [Fact]
    public void Accepts_a_manifest_signed_by_the_release_key()
    {
        using var key = RSA.Create(3072);

        var manifest = UpdateManifest.ParseVerified(SignedFile(key, Payload()), key.ToXmlString(false));

        Assert.NotNull(manifest);
        Assert.Equal(["Une note"], manifest.Notes);
    }

    [Fact]
    public void Rejects_a_manifest_signed_by_another_key()
    {
        using var release = RSA.Create(3072);
        using var attacker = RSA.Create(3072);

        Assert.Null(UpdateManifest.ParseVerified(SignedFile(attacker, Payload()), release.ToXmlString(false)));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("url")]
    [InlineData("sha256")]
    [InlineData("notes")]
    public void Rejects_any_change_to_the_signed_content(string field)
    {
        using var key = RSA.Create(3072);
        var file = JsonNode.Parse(SignedFile(key, Payload()))!;
        var payload = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(file["signed"]!.GetValue<string>())))!;

        payload[field] = field == "notes" ? new JsonArray("Installez vite cette version") : field == "version" ? "9.9.9" : field == "sha256" ? new string('b', 64) : ValidUrl + "x";
        file["signed"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload.ToJsonString()));

        Assert.Null(UpdateManifest.ParseVerified(file.ToJsonString(), key.ToXmlString(false)));
    }

    [Fact]
    public void Clear_fields_never_replace_the_signed_ones()
    {
        using var key = RSA.Create(3072);
        var file = JsonNode.Parse(SignedFile(key, Payload()))!;

        file["version"] = "9.9.9";
        file["url"] = "https://example.com/piege.exe";
        file["notes"] = new JsonArray("Fausse note");

        var manifest = UpdateManifest.ParseVerified(file.ToJsonString(), key.ToXmlString(false));

        Assert.Equal(new Version(1, 2, 3), manifest!.Version);
        Assert.Equal(ValidUrl, manifest.InstallerUrl);
        Assert.Equal(["Une note"], manifest.Notes);
    }

    [Fact]
    public void A_signature_made_without_the_purpose_prefix_is_refused()
    {
        using var key = RSA.Create(3072);
        var bytes = Encoding.UTF8.GetBytes(Payload());
        var signature = key.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var file = JsonSerializer.Serialize(new { signed = Convert.ToBase64String(bytes), sig2 = Convert.ToBase64String(signature) });

        Assert.Null(UpdateManifest.ParseVerified(file, key.ToXmlString(false)));
    }

    [Theory]
    [InlineData("pas du json")]
    [InlineData("{}")]
    [InlineData("""{"version":"9.9.9","url":"https://example.com/a.exe","sha256":"aa","sig":"c2ln"}""")]
    [InlineData("""{"signed":"pas du base64 !","sig2":"AAAA"}""")]
    [InlineData("""{"signed":"AAAA","sig2":"pas du base64 !"}""")]
    [InlineData("""{"signed":"AAAA","sig2":"AAAA"}""")]
    public void Rejects_files_without_a_usable_signature(string json)
    {
        using var key = RSA.Create(3072);

        Assert.Null(UpdateManifest.ParseVerified(json, key.ToXmlString(false)));
    }

    [Fact]
    public void Rejects_an_empty_public_key()
    {
        using var key = RSA.Create(3072);

        Assert.Null(UpdateManifest.ParseVerified(SignedFile(key, Payload()), string.Empty));
    }

    [Fact]
    public void Checks_the_installer_hash()
    {
        var content = Encoding.UTF8.GetBytes("installeur");
        var manifest = new UpdateManifest(new Version(1, 2, 3), ValidUrl, Convert.ToHexStringLower(SHA256.HashData(content)), []);

        Assert.True(manifest.MatchesInstaller(new MemoryStream(content)));
        Assert.False(manifest.MatchesInstaller(new MemoryStream(Encoding.UTF8.GetBytes("autre"))));
    }

    // La publication écrit le manifeste en PowerShell et l'application le lit en C# : une différence de signature couperait les mises à jour de tous les utilisateurs.
    [Fact]
    public void The_publishing_script_writes_a_manifest_every_version_can_read()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"lp-manifest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);

        try
        {
            var root = AppContext.BaseDirectory;

            while (!File.Exists(Path.Combine(root, "release-manifest.ps1")))
            {
                root = Path.GetDirectoryName(root) ?? throw new FileNotFoundException("release-manifest.ps1");
            }

            var runner = Path.Combine(folder, "run.ps1");
            File.WriteAllText(runner, """
                param($Script, $Out)
                $ErrorActionPreference = 'Stop'
                . $Script
                $parameters = New-Object System.Security.Cryptography.CspParameters 24
                $rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider(3072, $parameters)
                [IO.File]::WriteAllText("$Out\key.xml", $rsa.ToXmlString($false))
                $note = 'Mises ' + [char]0xE0 + ' jour & ' + [char]0x201C + 'notes' + [char]0x201D
                $json = New-ReleaseManifest -Rsa $rsa -Version '1.2.3' -Sha256 ('a' * 64) -Date '2026-11-04' -Notes $note `
                    -Url 'https://github.com/Lixirian/Legendary-Pen/releases/download/v1.2.3/LegendaryPen-Setup-1.2.3.exe'
                [IO.File]::WriteAllText("$Out\latest.json", $json, (New-Object Text.UTF8Encoding $false))
                """, new UTF8Encoding(true));

            using var process = Process.Start(new ProcessStartInfo(
                Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
                $"-NoProfile -ExecutionPolicy Bypass -File \"{runner}\" -Script \"{Path.Combine(root, "release-manifest.ps1")}\" -Out \"{folder}\"")
            {
                RedirectStandardError = true,
                UseShellExecute = false,
            })!;

            var errors = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, errors);

            var json = File.ReadAllText(Path.Combine(folder, "latest.json"));
            var publicKey = File.ReadAllText(Path.Combine(folder, "key.xml"));

            // Les versions à venir lisent le contenu signé en entier.
            var manifest = UpdateManifest.ParseVerified(json, publicKey);
            Assert.NotNull(manifest);
            Assert.Equal(new Version(1, 2, 3), manifest.Version);
            Assert.Equal(new string('a', 64), manifest.Sha256);
            Assert.Equal(["Mises à jour & “notes”"], manifest.Notes);

            // La 0.10.2 lit les champs en clair et la signature d'origine : c'est ce qui lui permet de se mettre à jour.
            using var document = JsonDocument.Parse(json);
            var file = document.RootElement;
            using var rsa = RSA.Create();
            rsa.FromXmlString(publicKey);

            Assert.Equal("1.2.3", file.GetProperty("version").GetString());
            Assert.Equal(ValidUrl, file.GetProperty("url").GetString());
            Assert.Equal(new string('a', 64), file.GetProperty("sha256").GetString());
            Assert.Equal(JsonValueKind.Array, file.GetProperty("notes").ValueKind);
            Assert.True(rsa.VerifyData(
                Encoding.UTF8.GetBytes($"LegendaryPen|1.2.3|{new string('a', 64)}"),
                Convert.FromBase64String(file.GetProperty("sig").GetString()!),
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}

public class InstallLocationTests
{
    private const string InstallDirectory = @"C:\Users\Moi\AppData\Local\Programs\Legendary Pen";

    [Theory]
    [InlineData(@"C:\Users\Moi\AppData\Local\Programs\Legendary Pen\LegendaryPen.exe", true)]
    [InlineData(@"c:\users\moi\appdata\local\programs\legendary pen\LegendaryPen.exe", true)]
    [InlineData(@"C:\Projets\Legendary Pen\bin\Debug\LegendaryPen.exe", false)]
    [InlineData(@"C:\Users\Moi\AppData\Local\Programs\Legendary Pen\sous-dossier\LegendaryPen.exe", false)]
    [InlineData(@"C:\Users\Moi\AppData\Local\Programs\Legendary Pen 2\LegendaryPen.exe", false)]
    [InlineData(null, false)]
    public void Only_the_installed_copy_receives_updates(string? executable, bool expected)
    {
        Assert.Equal(expected, InstallLocation.IsInstalledCopy(executable, InstallDirectory));
    }
}
