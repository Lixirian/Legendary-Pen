using LegendaryPen.Core;
using Xunit;

namespace LegendaryPen.Tests;

public class EmptyHotkeyLanguageTests : IDisposable
{
    public void Dispose() => Loc.Use(AppLanguage.French, "fr");

    [Fact]
    public void An_empty_shortcut_follows_the_interface_language()
    {
        Loc.Use(AppLanguage.English, "fr");
        Assert.Equal("None", HotkeyBinding.None.Format());

        Loc.Use(AppLanguage.French, "fr");
        Assert.Equal("Aucun", HotkeyBinding.None.Format());
    }
}

public class LayerMergeNumberTests
{
    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(1_000_000)]
    [InlineData(0)]
    [InlineData(-4)]
    public void A_damaged_number_cannot_break_a_merge(int number)
    {
        var stack = new LayerStack();
        var added = new SavedLayer(Guid.NewGuid(), number, true, [], []);

        stack.Restore(LayerRestoration.MergeLayers(stack.Capture(), [added]).State);

        var state = stack.Capture();
        var kept = state.Layers.Single(layer => layer.Id == added.Id);

        Assert.InRange(kept.Number, 1, 999_999);
        Assert.True(state.NextNumber > kept.Number);
    }
}

public class SettingsReadTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"lp-settings-{Guid.NewGuid():N}");

    public SettingsReadTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string SavedFile()
    {
        var path = Path.Combine(_folder, "settings.json");
        new SettingsStore(path).Save(new AppSettings { KeepAnnotations = true });
        return path;
    }

    [Fact]
    public void A_file_held_for_a_moment_is_read_on_a_later_attempt()
    {
        var path = SavedFile();
        var holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        var waits = 0;

        var store = new SettingsStore(path, () =>
        {
            waits++;
            holder.Dispose();
        });

        Assert.True(store.Load().KeepAnnotations);
        Assert.Equal(1, waits);
    }

    [Fact]
    public void A_file_that_stays_held_is_reported_and_stays_intact()
    {
        var path = SavedFile();
        var before = File.ReadAllBytes(path);

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Throws<IOException>(() => new SettingsStore(path, () => { }).Load());
        }

        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void A_missing_file_gives_the_defaults_without_waiting()
    {
        var store = new SettingsStore(Path.Combine(_folder, "absent.json"), () => throw new InvalidOperationException());

        Assert.False(store.Load().KeepAnnotations);
    }
}

public class ErrorJournalTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"lp-errors-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void An_error_is_written_with_its_origin()
    {
        var path = Path.Combine(_folder, "sous-dossier", "erreurs.log");

        new ErrorJournal(path).Write("Interface", new InvalidOperationException("panne"), new DateTime(2026, 11, 4, 8, 0, 0));

        var text = File.ReadAllText(path);
        Assert.Contains("[2026-11-04 08:00:00] Interface", text);
        Assert.Contains("panne", text);
    }

    [Fact]
    public void The_journal_does_not_grow_without_limit()
    {
        var path = Path.Combine(_folder, "erreurs.log");
        var journal = new ErrorJournal(path, maxBytes: 200);

        for (var i = 0; i < 20; i++)
        {
            journal.Write("Interface", new InvalidOperationException(new string('x', 100)), DateTime.Now);
        }

        Assert.True(new FileInfo(path).Length < 1000);
    }

    [Fact]
    public void An_unwritable_journal_stays_silent()
    {
        Directory.CreateDirectory(_folder);

        // Le chemin désigne un dossier : l'écriture échoue forcément.
        new ErrorJournal(_folder).Write("Interface", new InvalidOperationException(), DateTime.Now);
    }

    [Fact]
    public void Only_errors_close_together_count_as_a_burst()
    {
        var burst = new ErrorBurst(limit: 3, window: TimeSpan.FromSeconds(30));
        var start = new DateTime(2026, 1, 1);

        Assert.False(burst.IsTooMany(start));
        Assert.False(burst.IsTooMany(start.AddSeconds(40)));
        Assert.False(burst.IsTooMany(start.AddSeconds(50)));
        Assert.True(burst.IsTooMany(start.AddSeconds(60)));
    }
}

public class GraphicsStateBalanceTests
{
    [Theory]
    [InlineData("", 0, 0)]
    [InlineData("q Q", 0, 0)]
    [InlineData("q q", 2, 0)]
    [InlineData("Q", -1, -1)]
    [InlineData("Q q", 0, -1)]
    [InlineData("q 1 0 0 1 0 0 cm Q", 0, 0)]
    [InlineData("(q q q) Tj", 0, 0)]
    [InlineData(@"(a\) q (b) q) Tj", 0, 0)]
    [InlineData("<71> Tj /q gs", 0, 0)]
    [InlineData("% q q\nq", 1, 0)]
    [InlineData("[(q) 3 (Q)] TJ", 0, 0)]
    [InlineData("BI /W 1 /H 1 ID q q Q \xff EI q", 1, 0)]
    [InlineData("qq Qx", 0, 0)]
    public void Only_real_instructions_count(string content, int final, int lowest)
    {
        var bytes = System.Text.Encoding.Latin1.GetBytes(content);

        Assert.Equal(new GraphicsStateBalance(final, lowest), GraphicsStateBalance.Measure(bytes));
    }

    [Fact]
    public void Deep_nesting_cannot_exhaust_the_stack()
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(new string('[', 2_000_000) + new string('(', 2_000_000));

        Assert.Equal(new GraphicsStateBalance(0, 0), GraphicsStateBalance.Measure(bytes));
    }
}

public class PdfRenderSizeTests
{
    [Fact]
    public void A_normal_page_keeps_the_usual_width()
    {
        Assert.Equal((1800u, 2400u), PdfRenderSize.For(600, 800));
    }

    [Theory]
    [InlineData(1, 100_000)]
    [InlineData(100_000, 1)]
    [InlineData(14_400, 14_400)]
    [InlineData(0.001, 14_400)]
    public void An_extreme_page_stays_within_the_limits(double width, double height)
    {
        var (pixelWidth, pixelHeight) = PdfRenderSize.For(width, height);

        Assert.InRange(pixelWidth, 1u, (uint)PdfRenderSize.MaxSide);
        Assert.InRange(pixelHeight, 1u, (uint)PdfRenderSize.MaxSide);
        Assert.True((double)pixelWidth * pixelHeight <= PdfRenderSize.MaxPixels * 1.01);
    }

    [Theory]
    [InlineData(0, 800)]
    [InlineData(600, 0)]
    [InlineData(double.NaN, 800)]
    [InlineData(double.PositiveInfinity, 800)]
    public void An_invalid_size_falls_back_to_a_usable_one(double width, double height)
    {
        Assert.Equal((1800u, 1800u), PdfRenderSize.For(width, height));
    }
}

public class SignatureProtectionStoreTests : IDisposable
{
    private static readonly SignatureInk Ink = SignatureInk.FromStrokes([[new(1, 2), new(30, 40)]], 2, new InkColor(0, 0, 255))!;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"lp-signature-{Guid.NewGuid():N}");

    public SignatureProtectionStoreTests() => Directory.CreateDirectory(_folder);

    private string Encrypted => Path.Combine(_folder, "local", "signature.dat");

    private string Legacy => Path.Combine(_folder, "signature.json");

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static byte[] Flip(byte[] bytes) => bytes.Select(value => (byte)(value ^ 0x5A)).ToArray();

    private SignatureStore Store() => new(Encrypted, Flip, Flip, Legacy);

    [Fact]
    public void The_stored_file_never_contains_the_strokes_in_clear()
    {
        Store().Save(Ink);

        var stored = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Encrypted));

        Assert.DoesNotContain("Strokes", stored);
        Assert.Equal(Ink.AspectRatio, Store().Load()!.AspectRatio);
    }

    [Fact]
    public void A_file_of_another_user_is_ignored()
    {
        Store().Save(Ink);

        var other = new SignatureStore(Encrypted, Flip, _ => throw new System.Security.Cryptography.CryptographicException());

        Assert.Null(other.Load());
    }

    [Fact]
    public void An_old_signature_moves_to_the_encrypted_file_and_the_clear_copy_disappears()
    {
        File.WriteAllText(Legacy, System.Text.Json.JsonSerializer.Serialize(Ink));

        Assert.True(Store().Exists);
        Assert.Equal(Ink.AspectRatio, Store().Load()!.AspectRatio);
        Assert.False(File.Exists(Legacy));
        Assert.True(File.Exists(Encrypted));
    }

    [Fact]
    public void Forgetting_removes_both_files()
    {
        Store().Save(Ink);
        File.WriteAllText(Legacy, "{}");

        Store().Delete();

        Assert.False(Store().Exists);
    }

    [Theory]
    [InlineData("""{"AspectRatio":0,"Strokes":[[{"X":0,"Y":0}]],"ThicknessShare":0.1,"Color":{"R":0,"G":0,"B":0}}""")]
    [InlineData("""{"AspectRatio":1,"Strokes":[],"ThicknessShare":0.1,"Color":{"R":0,"G":0,"B":0}}""")]
    [InlineData("""{"AspectRatio":1,"Strokes":[[]],"ThicknessShare":0.1,"Color":{"R":0,"G":0,"B":0}}""")]
    [InlineData("""{"AspectRatio":1,"Strokes":null,"ThicknessShare":0.1,"Color":{"R":0,"G":0,"B":0}}""")]
    public void A_damaged_signature_is_refused(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Encrypted)!);
        File.WriteAllBytes(Encrypted, Flip(System.Text.Encoding.UTF8.GetBytes(json)));

        Assert.Null(Store().Load());
    }
}
