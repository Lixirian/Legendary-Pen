using System.Globalization;
using System.Text;
using LegendaryPen.Core;
using PdfSharp.Pdf.IO;
using Xunit;

namespace LegendaryPen.Tests;

public class PdfPageFrameTests
{
    [Theory]
    [InlineData(0, 0, 0, 10, 820)]
    [InlineData(0, 1, 1, 610, 20)]
    [InlineData(90, 0, 0, 10, 20)]
    [InlineData(90, 1, 0, 10, 820)]
    [InlineData(180, 0, 0, 610, 20)]
    [InlineData(180, 1, 1, 10, 820)]
    [InlineData(270, 0, 0, 610, 820)]
    [InlineData(270, 1, 0, 610, 20)]
    [InlineData(-90, 0, 0, 610, 820)]
    public void Display_corners_land_on_the_right_page_corners(int rotation, double u, double v, double x, double y)
    {
        var frame = new PdfPageFrame(10, 20, 600, 800, rotation);

        Assert.Equal(new InkPoint(x, y), frame.ToPage(u, v));
    }

    [Theory]
    [InlineData(0, 600, 800)]
    [InlineData(90, 800, 600)]
    [InlineData(180, 600, 800)]
    [InlineData(270, 800, 600)]
    public void Quarter_turns_swap_the_displayed_size(int rotation, double width, double height)
    {
        var frame = new PdfPageFrame(0, 0, 600, 800, rotation);

        Assert.Equal(width, frame.DisplayWidth);
        Assert.Equal(height, frame.DisplayHeight);
    }
}

public class SignatureInkTests
{
    private static readonly InkColor Blue = new(0, 0, 255);

    [Fact]
    public void An_empty_drawing_is_not_a_signature()
    {
        Assert.Null(SignatureInk.FromStrokes([], 2, Blue));
        Assert.Null(SignatureInk.FromStrokes([[]], 2, Blue));
    }

    [Fact]
    public void Strokes_fit_inside_the_box_with_their_thickness()
    {
        var ink = SignatureInk.FromStrokes([[new(100, 50), new(300, 150)]], 10, Blue)!;

        Assert.Equal(210.0 / 110.0, ink.AspectRatio, 6);
        Assert.Equal(10.0 / 210.0, ink.ThicknessShare, 6);
        Assert.Equal(5.0 / 210.0, ink.Strokes[0][0].X, 6);
        Assert.Equal(105.0 / 110.0, ink.Strokes[0][1].Y, 6);
    }

    [Fact]
    public void A_single_dot_is_still_a_signature()
    {
        var ink = SignatureInk.FromStrokes([[new(5, 5)]], 2, Blue)!;

        Assert.Equal(1, ink.AspectRatio, 6);
        Assert.Equal(new InkPoint(0.5, 0.5), ink.Strokes[0][0]);
    }

    [Fact]
    public void A_saved_signature_comes_back_identical()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lp-signature-{Guid.NewGuid():N}", "signature.json");
        var store = new SignatureStore(path, bytes => bytes, bytes => bytes);
        var ink = SignatureInk.FromStrokes([[new(1, 2), new(30, 40)], [new(5, 6)]], 2, Blue)!;

        try
        {
            Assert.False(store.Exists);
            Assert.Null(store.Load());

            store.Save(ink);
            var loaded = store.Load()!;

            Assert.True(store.Exists);
            Assert.Equal(ink.AspectRatio, loaded.AspectRatio);
            Assert.Equal(ink.Color, loaded.Color);
            Assert.Equal(ink.Strokes[0], loaded.Strokes[0]);

            store.Delete();
            Assert.False(store.Exists);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void An_unreadable_saved_signature_is_ignored()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lp-signature-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ pas du json");

        try
        {
            Assert.Null(new SignatureStore(path, bytes => bytes, bytes => bytes).Load());
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class SignaturePlacementTests
{
    private static readonly SignatureInk Ink = new(2, [[new(0, 0), new(1, 1)]], 0.01, new InkColor(0, 0, 0));
    private const double Aspect = 0.75;

    [Fact]
    public void Height_follows_the_width_without_distortion()
    {
        // 0,4 de 600 = 240 ; à un rapport 2, soit 120 sur 800.
        Assert.Equal(0.15, new SignaturePlacement(Ink, 0, 0, 0, 0.4).HeightShare(Aspect), 6);
    }

    [Fact]
    public void A_move_stays_inside_the_page()
    {
        var placement = new SignaturePlacement(Ink, 0, 0, 0, 0.4);

        var low = placement.MovedTo(5, 5, Aspect);
        Assert.Equal(0.6, low.Left, 6);
        Assert.Equal(0.85, low.Top, 6);

        var high = placement.MovedTo(-5, -5, Aspect);
        Assert.Equal(0, high.Left);
        Assert.Equal(0, high.Top);
    }

    [Fact]
    public void A_resize_stays_inside_the_page()
    {
        var placement = new SignaturePlacement(Ink, 0, 0.5, 0.9, 0.1);

        // Le bas de la page limite avant le bord droit : 0,1 de 800 = 80, soit une largeur de 160 sur 600.
        Assert.Equal(160.0 / 600.0, placement.ResizedTo(0.9, Aspect).Width, 6);
        Assert.Equal(SignaturePlacement.MinWidth, placement.ResizedTo(0, Aspect).Width, 6);
    }
}

public class PdfFilesTests
{
    [Fact]
    public void The_proposed_name_never_targets_an_existing_file()
    {
        var folder = Path.Combine("C:", "docs");
        var first = Path.Combine(folder, "contrat (signé).pdf");
        var second = Path.Combine(folder, "contrat (signé 2).pdf");

        Assert.Equal(first, PdfFiles.SignedPath(Path.Combine(folder, "contrat.pdf"), _ => false));
        Assert.Equal(
            Path.Combine(folder, "contrat (signé 3).pdf"),
            PdfFiles.SignedPath(Path.Combine(folder, "contrat.pdf"), path => path == first || path == second));
    }
}

public sealed class PdfSignerTests : IDisposable
{
    private const string PageText = "Texte selectionnable";
    private static readonly SignatureInk Ink = new(2, [[new(0, 0), new(1, 1)]], 0.01, new InkColor(255, 0, 0));
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"lp-pdf-{Guid.NewGuid():N}");

    public PdfSignerTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string Target => Path.Combine(_folder, "signé.pdf");

    /// <summary>PDF minimal écrit à la main : il ne dépend d'aucune police ni d'aucun document réel.</summary>
    private string WritePdf(string name, string catalogExtra, params string[] pageExtras)
    {
        var objects = new List<string>();
        var kids = string.Join(" ", pageExtras.Select((_, i) => $"{3 + (i * 2)} 0 R"));
        objects.Add($"<< /Type /Catalog /Pages 2 0 R {catalogExtra} >>");
        objects.Add($"<< /Type /Pages /Count {pageExtras.Length} /Kids [{kids}] /MediaBox [0 0 600 800] >>");

        for (var i = 0; i < pageExtras.Length; i++)
        {
            var stream = $"BT /F1 12 Tf 50 700 Td ({PageText} {i + 1}) Tj ET";
            objects.Add($"<< /Type /Page /Parent 2 0 R /Contents {4 + (i * 2)} 0 R {pageExtras[i]} /Resources << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >> >>");
            objects.Add($"<< /Length {stream.Length} >>\nstream\n{stream}\nendstream");
        }

        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();

        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = pdf.Length;
        pdf.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");

        foreach (var offset in offsets)
        {
            pdf.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        }

        pdf.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");

        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(pdf.ToString()));
        return path;
    }

    private static string ContentOf(string path, int pageIndex)
    {
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Modify);
        var content = new StringBuilder();

        foreach (var item in document.Pages[pageIndex].Contents)
        {
            content.Append(Encoding.ASCII.GetString(item.Stream.UnfilteredValue)).Append('\n');
        }

        return content.ToString();
    }

    [Fact]
    public void Every_page_and_its_text_are_kept_and_the_ink_is_added_on_top()
    {
        var source = WritePdf("source.pdf", "", "", "", "");

        PdfSigner.Sign(PdfSnapshot.Read(source), Target, [new SignaturePlacement(Ink, 1, 0.1, 0.1, 0.4)]);

        using (var document = PdfReader.Open(Target, PdfDocumentOpenMode.Modify))
        {
            Assert.Equal(3, document.PageCount);
        }

        Assert.Contains($"({PageText} 1) Tj", ContentOf(Target, 0));
        Assert.DoesNotContain(" RG ", ContentOf(Target, 0));

        var signedPage = ContentOf(Target, 1);
        Assert.Contains($"({PageText} 2) Tj", signedPage);
        Assert.True(signedPage.IndexOf("Tj", StringComparison.Ordinal) < signedPage.IndexOf(" RG ", StringComparison.Ordinal));
        Assert.StartsWith("q\n", signedPage);
    }

    [Fact]
    public void The_original_file_is_left_untouched()
    {
        var source = WritePdf("source.pdf", "", "");
        var before = File.ReadAllBytes(source);

        PdfSigner.Sign(PdfSnapshot.Read(source), Target, [new SignaturePlacement(Ink, 0, 0, 0, 0.5)]);

        Assert.Equal(before, File.ReadAllBytes(source));
    }

    [Fact]
    public void The_original_cannot_be_chosen_as_the_destination()
    {
        var source = WritePdf("source.pdf", "", "");
        var before = File.ReadAllBytes(source);

        Assert.Throws<ArgumentException>(() => PdfSigner.Sign(PdfSnapshot.Read(source), source.ToUpperInvariant(), [new SignaturePlacement(Ink, 0, 0, 0, 0.5)]));
        Assert.Equal(before, File.ReadAllBytes(source));
    }

    [Fact]
    public void No_temporary_file_is_left_behind()
    {
        var source = WritePdf("source.pdf", "", "");

        PdfSigner.Sign(PdfSnapshot.Read(source), Target, [new SignaturePlacement(Ink, 0, 0, 0, 0.5)]);

        Assert.Equal(2, Directory.GetFiles(_folder).Length);
    }

    [Fact]
    public void A_page_without_a_crop_box_keeps_its_format()
    {
        var source = WritePdf("source.pdf", "", "");

        PdfSigner.Sign(PdfSnapshot.Read(source), Target, [new SignaturePlacement(Ink, 0, 0, 0, 0.5)]);

        using var document = PdfReader.Open(Target, PdfDocumentOpenMode.Modify);

        Assert.False(document.Pages[0].Elements.ContainsKey("/CropBox"));
        Assert.Equal(600, document.Pages[0].MediaBox.Width);
        Assert.Equal(800, document.Pages[0].MediaBox.Height);
    }

    [Fact]
    public void A_page_without_a_crop_box_receives_the_ink_from_its_media_box()
    {
        var source = WritePdf("source.pdf", "", "");

        PdfSigner.Sign(PdfSnapshot.Read(source), Target, [new SignaturePlacement(Ink, 0, 0, 0, 0.5)]);

        // Le coin haut gauche affiché, en haut de la page de 800 points.
        Assert.Contains("0 800 m\n", ContentOf(Target, 0));
    }

    [Fact]
    public void A_rotated_cropped_page_receives_the_ink_at_the_displayed_position()
    {
        var source = WritePdf("source.pdf", "", "/Rotate 90 /CropBox [100 100 500 700]");

        PdfSigner.Sign(PdfSnapshot.Read(source), Target, [new SignaturePlacement(Ink, 0, 0, 0, 0.5)]);

        // Le coin haut gauche affiché d'une page tournée de 90° est son coin bas gauche.
        Assert.Contains("100 100 m\n", ContentOf(Target, 0));
    }

    [Fact]
    public void Numbers_use_a_dot_whatever_the_system_language()
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("fr-FR");

        try
        {
            var content = PdfSigner.BuildContent(new PdfPageFrame(0, 0, 600, 800, 0), [new SignaturePlacement(Ink, 0, 0.123, 0.5, 0.333)]);

            Assert.DoesNotContain(",", content);
            Assert.Contains("1 0 0 RG", content);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public void A_single_dot_is_drawn()
    {
        var dot = new SignatureInk(1, [[new(0.5, 0.5)]], 0.5, new InkColor(0, 0, 0));

        var content = PdfSigner.BuildContent(new PdfPageFrame(0, 0, 100, 100, 0), [new SignaturePlacement(dot, 0, 0, 0, 1)]);

        Assert.Contains("50 50 m\n50 50 l\nS\n", content);
    }

    [Fact]
    public void A_readable_pdf_can_be_signed_but_not_a_damaged_one()
    {
        Assert.True(PdfSigner.CanSign(PdfSnapshot.Read(WritePdf("source.pdf", "", ""))));

        var damaged = Path.Combine(_folder, "abime.pdf");
        File.WriteAllText(damaged, "ceci n'est pas un PDF");

        Assert.False(PdfSigner.CanSign(PdfSnapshot.Read(damaged)));
        Assert.Throws<FileNotFoundException>(() => PdfSnapshot.Read(Path.Combine(_folder, "absent.pdf")));
    }

    [Fact]
    public void An_encrypted_pdf_cannot_be_signed()
    {
        Assert.False(PdfSigner.CanSign(PdfSnapshot.Read(WritePdf("chiffre.pdf", "/Encrypt 99 0 R", ""))));
    }

    [Fact]
    public void An_existing_digital_signature_is_detected()
    {
        Assert.False(PdfSigner.HasDigitalSignature(PdfSnapshot.Read(WritePdf("plain.pdf", "", ""))));
        Assert.True(PdfSigner.HasDigitalSignature(PdfSnapshot.Read(WritePdf("digital.pdf", "/AcroForm << /SigFlags 3 /Fields [] >>", ""))));
    }

    [Fact]
    public void The_signature_goes_on_the_version_read_even_if_the_file_is_replaced()
    {
        var source = WritePdf("source.pdf", "", "");
        var snapshot = PdfSnapshot.Read(source);

        Assert.False(snapshot.HasChangedOnDisk());

        WritePdf("source.pdf", "", "", "");

        Assert.True(snapshot.HasChangedOnDisk());

        PdfSigner.Sign(snapshot, Target, [new SignaturePlacement(Ink, 0, 0, 0, 0.5)]);

        using var document = PdfReader.Open(Target, PdfDocumentOpenMode.Modify);
        Assert.Equal(1, document.PageCount);
    }

    [Fact]
    public void A_deleted_original_counts_as_changed()
    {
        var source = WritePdf("source.pdf", "", "");
        var snapshot = PdfSnapshot.Read(source);

        File.Delete(source);

        Assert.True(snapshot.HasChangedOnDisk());
    }

    [Theory]
    [InlineData("0.001 0 0 0.001 0 0 cm q", 1)]
    [InlineData("q q 2 0 0 2 0 0 cm", 1)]
    [InlineData("Q Q 0.5 0 0 0.5 0 0 cm", 3)]
    [InlineData("Q q Q q", 2)]
    [InlineData("q BT (Q) Tj ET Q", 1)]
    public void Unbalanced_page_content_cannot_shift_the_signature(string content, int opening)
    {
        var source = WritePdfWithContent(content);

        PdfSigner.Sign(PdfSnapshot.Read(source), Target, [new SignaturePlacement(Ink, 0, 0, 0, 0.5)]);

        var signed = ContentOf(Target, 0);
        var ink = signed.IndexOf(" RG ", StringComparison.Ordinal);
        var before = Encoding.ASCII.GetBytes(signed[..signed.LastIndexOf("q ", ink, StringComparison.Ordinal)]);

        // Au moment de tracer la signature, l'état graphique est revenu exactement à celui de départ.
        Assert.Equal(new GraphicsStateBalance(0, 0), GraphicsStateBalance.Measure(before));
        Assert.StartsWith(string.Concat(Enumerable.Repeat("q\n", opening)), signed, StringComparison.Ordinal);
    }

    private string WritePdfWithContent(string content)
    {
        var path = Path.Combine(_folder, "contenu.pdf");
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 800] /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
        };
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();

        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = pdf.Length;
        pdf.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");

        foreach (var offset in offsets)
        {
            pdf.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        }

        pdf.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(pdf.ToString()));
        return path;
    }
}
