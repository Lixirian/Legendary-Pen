using System.Globalization;
using System.Text;
using System.Text.Json;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace LegendaryPen.Core;

/// <summary>Zone visible d'une page PDF et rotation d'affichage, en unités de la page.</summary>
public readonly record struct PdfPageFrame(double X, double Y, double Width, double Height, int Rotation)
{
    private int QuarterTurns => Rotation % 90 == 0 ? ((Rotation % 360) + 360) % 360 / 90 : 0;

    public double DisplayWidth => QuarterTurns % 2 == 0 ? Width : Height;

    public double DisplayHeight => QuarterTurns % 2 == 0 ? Height : Width;

    /// <summary>
    /// Convertit une position à l'écran, en fractions de la page (origine en haut à gauche), vers
    /// les coordonnées PDF (origine en bas à gauche), que la page soit tournée ou non.
    /// </summary>
    public InkPoint ToPage(double u, double v) => QuarterTurns switch
    {
        1 => new InkPoint(X + (v * Width), Y + (u * Height)),
        2 => new InkPoint(X + ((1 - u) * Width), Y + (v * Height)),
        3 => new InkPoint(X + ((1 - v) * Width), Y + ((1 - u) * Height)),
        _ => new InkPoint(X + (u * Width), Y + ((1 - v) * Height)),
    };
}

/// <summary>Signature vectorielle, indépendante de la taille à laquelle elle sera posée.</summary>
/// <param name="AspectRatio">Largeur divisée par hauteur.</param>
/// <param name="Strokes">Traits, en fractions de la boîte de la signature.</param>
/// <param name="ThicknessShare">Épaisseur du trait, en fraction de la largeur.</param>
/// <param name="Color">Couleur de l'encre.</param>
public sealed record SignatureInk(double AspectRatio, List<List<InkPoint>> Strokes, double ThicknessShare, InkColor Color)
{
    /// <summary>Renvoie null si rien n'a été tracé.</summary>
    public static SignatureInk? FromStrokes(IReadOnlyList<IReadOnlyList<InkPoint>> strokes, double thickness, InkColor color)
    {
        var points = strokes.SelectMany(stroke => stroke).ToList();

        if (points.Count == 0 || thickness <= 0)
        {
            return null;
        }

        // La boîte englobe aussi l'épaisseur du trait : rien ne dépasse une fois la signature posée.
        var left = points.Min(point => point.X) - (thickness / 2);
        var top = points.Min(point => point.Y) - (thickness / 2);
        var width = points.Max(point => point.X) + (thickness / 2) - left;
        var height = points.Max(point => point.Y) + (thickness / 2) - top;

        return new SignatureInk(
            width / height,
            strokes.Where(stroke => stroke.Count > 0)
                .Select(stroke => stroke.Select(point => new InkPoint((point.X - left) / width, (point.Y - top) / height)).ToList())
                .ToList(),
            thickness / width,
            color);
    }
}

/// <summary>Signature posée sur une page. Positions et largeur en fractions de la page affichée.</summary>
public sealed record SignaturePlacement(SignatureInk Signature, int PageIndex, double Left, double Top, double Width)
{
    public const double MinWidth = 0.03;

    /// <param name="pageAspect">Largeur divisée par hauteur de la page affichée.</param>
    public double HeightShare(double pageAspect) => Width * pageAspect / Signature.AspectRatio;

    public SignaturePlacement MovedTo(double left, double top, double pageAspect) => this with
    {
        Left = Math.Clamp(left, 0, Math.Max(0, 1 - Width)),
        Top = Math.Clamp(top, 0, Math.Max(0, 1 - HeightShare(pageAspect))),
    };

    /// <summary>Redimensionne depuis le coin haut gauche, sans déformer ni sortir de la page.</summary>
    public SignaturePlacement ResizedTo(double width, double pageAspect)
    {
        var largest = Math.Min(1 - Left, (1 - Top) * Signature.AspectRatio / pageAspect);

        return this with { Width = Math.Clamp(width, Math.Min(MinWidth, largest), largest) };
    }
}

public static class PdfFiles
{
    /// <summary>Nom proposé pour la copie signée ; ne désigne jamais un fichier existant.</summary>
    public static string SignedPath(string source, Func<string, bool> exists)
    {
        var folder = Path.GetDirectoryName(source) ?? "";
        var stem = Path.GetFileNameWithoutExtension(source);
        var path = Path.Combine(folder, $"{stem} (signé).pdf");

        for (var suffix = 2; exists(path); suffix++)
        {
            path = Path.Combine(folder, $"{stem} (signé {suffix}).pdf");
        }

        return path;
    }

    public static bool IsSameFile(string first, string second) =>
        string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
}

/// <summary>Ajoute des signatures par-dessus les pages d'un PDF, sans toucher au contenu existant.</summary>
public static class PdfSigner
{
    /// <summary>Instructions PDF qui tracent les signatures d'une page.</summary>
    public static string BuildContent(PdfPageFrame frame, IEnumerable<SignaturePlacement> placements)
    {
        var pageAspect = frame.DisplayWidth / frame.DisplayHeight;
        var content = new StringBuilder();

        void Append(FormattableString text) => content.Append(text.ToString(CultureInfo.InvariantCulture));

        foreach (var placement in placements)
        {
            var ink = placement.Signature;
            var height = placement.HeightShare(pageAspect);

            Append($"q {ink.Color.R / 255.0:0.###} {ink.Color.G / 255.0:0.###} {ink.Color.B / 255.0:0.###} RG ");
            Append($"{placement.Width * frame.DisplayWidth * ink.ThicknessShare:0.###} w 1 J 1 j\n");

            foreach (var stroke in ink.Strokes)
            {
                // Un point seul est doublé : avec des extrémités rondes, il s'affiche comme un point.
                List<InkPoint> points = stroke.Count == 1 ? [stroke[0], stroke[0]] : stroke;

                for (var i = 0; i < points.Count; i++)
                {
                    var point = frame.ToPage(placement.Left + (points[i].X * placement.Width), placement.Top + (points[i].Y * height));
                    Append($"{point.X:0.###} {point.Y:0.###} {(i == 0 ? "m" : "l")}\n");
                }

                content.Append("S\n");
            }

            content.Append("Q\n");
        }

        return content.ToString();
    }

    /// <summary>Écrit une copie signée. Le fichier d'origine n'est jamais modifié.</summary>
    public static void Sign(string source, string destination, IReadOnlyCollection<SignaturePlacement> placements)
    {
        if (PdfFiles.IsSameFile(source, destination))
        {
            throw new ArgumentException("La copie signée ne peut pas remplacer le fichier d'origine.", nameof(destination));
        }

        using var document = PdfReader.Open(source, PdfDocumentOpenMode.Modify);

        foreach (var group in placements.GroupBy(placement => placement.PageIndex))
        {
            var page = document.Pages[group.Key];

            // Le contenu d'origine est isolé : un état graphique laissé ouvert ne décale pas la signature.
            page.Contents.PrependContent().CreateStream(Encoding.ASCII.GetBytes("q\n"));
            page.Contents.AppendContent().CreateStream(Encoding.ASCII.GetBytes("Q\n" + BuildContent(FrameOf(page), group)));
        }

        var temporaryPath = $"{destination}.{Guid.NewGuid():N}.tmp";

        try
        {
            document.Save(temporaryPath);
            File.Move(temporaryPath, destination, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    /// <summary>Faux si le PDF est protégé, interdit la modification ou ne peut pas être relu.</summary>
    public static bool CanSign(string path)
    {
        try
        {
            // PDFsharp ouvre certains fichiers chiffrés sans erreur, puis échoue en lisant leur contenu :
            // on regarde directement si le fichier déclare un chiffrement. Cette entrée reste toujours en clair.
            if (File.ReadAllBytes(path).AsSpan().IndexOf("/Encrypt"u8) >= 0)
            {
                return false;
            }

            using var document = PdfReader.Open(path, PdfDocumentOpenMode.Modify);
            return document.PageCount > 0;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // PDFsharp signale ces cas avec des types d'exceptions très variés.
            return false;
        }
    }

    /// <summary>Une signature numérique existante cesse d'être valide dès que le fichier change.</summary>
    public static bool HasDigitalSignature(string path)
    {
        const int SignaturesExist = 1;

        using var document = PdfReader.Open(path, PdfDocumentOpenMode.InformationOnly);

        return document.Internals.Catalog.Elements.GetDictionary("/AcroForm") is { } form
            && (form.Elements.GetInteger("/SigFlags") & SignaturesExist) != 0;
    }

    private static PdfPageFrame FrameOf(PdfPage page)
    {
        // Lire CropBox sur une page qui n'en a pas en créerait une vide, et changerait le format de la page.
        var box = page.Elements.ContainsKey("/CropBox") && !page.CropBox.IsEmpty ? page.CropBox : page.MediaBox;

        return new PdfPageFrame(
            Math.Min(box.X1, box.X2),
            Math.Min(box.Y1, box.Y2),
            Math.Abs(box.X2 - box.X1),
            Math.Abs(box.Y2 - box.Y1),
            page.Rotate);
    }
}

/// <summary>Signature mémorisée sur ce poste uniquement.</summary>
public sealed class SignatureStore(string path)
{
    public bool Exists => File.Exists(path);

    public SignatureInk? Load()
    {
        try
        {
            return JsonSerializer.Deserialize<SignatureInk>(File.ReadAllText(path));
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(SignatureInk signature)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";

        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(signature));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    public void Delete() => File.Delete(path);
}
