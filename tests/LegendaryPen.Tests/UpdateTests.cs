using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LegendaryPen.Core;
using Xunit;

namespace LegendaryPen.Tests;

public class UpdateManifestTests
{
    private const string ValidUrl = "https://github.com/Lixirian/Legendary-Pen/releases/download/v1.2.3/LegendaryPen-Setup-1.2.3.exe";
    private static readonly string ValidHash = new('a', 64);

    private static string Json(
        string version = "1.2.3",
        string? url = ValidUrl,
        string? sha256 = null,
        string sig = "c2lnbmF0dXJl")
    {
        return JsonSerializer.Serialize(new { version, url, sha256 = sha256 ?? ValidHash, sig, notes = new[] { "Une note" } });
    }

    [Fact]
    public void Parses_a_valid_manifest()
    {
        var manifest = UpdateManifest.Parse(Json());

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
    public void Rejects_downloads_outside_the_repository_releases(string url)
    {
        Assert.Null(UpdateManifest.Parse(Json(url: url)));
    }

    [Theory]
    [InlineData("pas du json")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("""{"version":3}""")]
    public void Rejects_malformed_manifests(string json)
    {
        Assert.Null(UpdateManifest.Parse(json));
    }

    [Theory]
    [InlineData("pas-une-version", null)]
    [InlineData("1.2.3", "trop-court")]
    public void Rejects_invalid_fields(string version, string? sha256)
    {
        Assert.Null(UpdateManifest.Parse(Json(version: version, sha256: sha256)));
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
        var manifest = new UpdateManifest(Version.Parse(published), ValidUrl, ValidHash, "c2ln", []);

        Assert.Equal(expected, manifest.IsNewerThan(Version.Parse(current)));
    }

    [Fact]
    public void Accepts_a_manifest_signed_by_the_release_key()
    {
        using var key = RSA.Create(3072);

        Assert.True(Signed(key, new Version(1, 2, 3), ValidHash).HasValidSignature(key.ToXmlString(false)));
    }

    [Fact]
    public void Rejects_a_manifest_signed_by_another_key()
    {
        using var attacker = RSA.Create(3072);
        using var release = RSA.Create(3072);

        Assert.False(Signed(attacker, new Version(1, 2, 3), ValidHash).HasValidSignature(release.ToXmlString(false)));
    }

    [Fact]
    public void Rejects_a_signature_when_the_installer_hash_was_swapped()
    {
        using var key = RSA.Create(3072);
        var tampered = Signed(key, new Version(1, 2, 3), ValidHash) with { Sha256 = new string('b', 64) };

        Assert.False(tampered.HasValidSignature(key.ToXmlString(false)));
    }

    [Fact]
    public void Rejects_a_signature_when_the_version_was_changed()
    {
        using var key = RSA.Create(3072);
        var tampered = Signed(key, new Version(1, 2, 3), ValidHash) with { Version = new Version(9, 9, 9) };

        Assert.False(tampered.HasValidSignature(key.ToXmlString(false)));
    }

    [Theory]
    [InlineData("pas du base64 !")]
    [InlineData("AAAA")]
    public void Rejects_unusable_signatures(string signature)
    {
        using var key = RSA.Create(3072);
        var manifest = new UpdateManifest(new Version(1, 2, 3), ValidUrl, ValidHash, signature, []);

        Assert.False(manifest.HasValidSignature(key.ToXmlString(false)));
    }

    [Fact]
    public void Rejects_an_empty_public_key()
    {
        using var key = RSA.Create(3072);

        Assert.False(Signed(key, new Version(1, 2, 3), ValidHash).HasValidSignature(string.Empty));
    }

    [Fact]
    public void Checks_the_installer_against_its_hash()
    {
        var content = Encoding.UTF8.GetBytes("installeur");
        var hash = Convert.ToHexStringLower(SHA256.HashData(content));
        var manifest = new UpdateManifest(new Version(1, 2, 3), ValidUrl, hash, "c2ln", []);

        Assert.True(manifest.MatchesInstaller(new MemoryStream(content)));
        Assert.False(manifest.MatchesInstaller(new MemoryStream(Encoding.UTF8.GetBytes("autre"))));
    }

    private static UpdateManifest Signed(RSA key, Version version, string sha256)
    {
        var signature = key.SignData(
            Encoding.UTF8.GetBytes(UpdateManifest.SignedText(version, sha256)),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        return new UpdateManifest(version, ValidUrl, sha256, Convert.ToBase64String(signature), []);
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
