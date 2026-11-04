using System.Globalization;
using System.Security.Cryptography;
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
        var path = Path.Combine(folder, Loc.T("{0} (signé).pdf", stem));

        for (var suffix = 2; exists(path); suffix++)
        {
            path = Path.Combine(folder, Loc.T("{0} (signé {1}).pdf", stem, suffix));
        }

        return path;
    }

    public static bool IsSameFile(string first, string second) =>
        string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Le document tel qu'il a été lu une fois pour toutes : contrôle, aperçu et signature travaillent sur ces mêmes octets.
/// Un fichier remplacé sur le disque pendant qu'on signe ne peut donc pas recevoir la signature à la place de celui affiché.
/// </summary>
public sealed class PdfSnapshot
{
    public const long MaxBytes = 500L * 1024 * 1024;

    private readonly byte[] _bytes;
    private readonly byte[] _hash;

    public PdfSnapshot(string path, byte[] bytes)
    {
        Path = path;
        _bytes = bytes;
        _hash = SHA256.HashData(bytes);
    }

    public string Path { get; }

    public ReadOnlyMemory<byte> Bytes => _bytes;

    /// <exception cref="InvalidDataException">Le fichier dépasse la taille acceptée.</exception>
    public static PdfSnapshot Read(string path)
    {
        if (new FileInfo(path).Length > MaxBytes)
        {
            throw new InvalidDataException(Loc.T("Ce PDF est trop volumineux pour être signé ici (500 Mo au plus)."));
        }

        return new PdfSnapshot(path, File.ReadAllBytes(path));
    }

    public MemoryStream OpenRead() => new(_bytes, writable: false);

    /// <summary>Vrai si le fichier n'est plus celui qui a été lu, ou s'il est devenu illisible.</summary>
    public bool HasChangedOnDisk()
    {
        try
        {
            using var file = File.OpenRead(Path);
            return !SHA256.HashData(file).AsSpan().SequenceEqual(_hash);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }
}

/// <summary>Taille de rendu d'une page : une page démesurée ne doit pas réclamer une image de plusieurs gigaoctets.</summary>
public static class PdfRenderSize
{
    public const int PreferredWidth = 1800;
    public const int MaxSide = 8192;
    public const double MaxPixels = 40_000_000;

    public static (uint Width, uint Height) For(double pageWidth, double pageHeight)
    {
        if (!(pageWidth > 0) || !(pageHeight > 0) || double.IsInfinity(pageWidth) || double.IsInfinity(pageHeight))
        {
            return (PreferredWidth, PreferredWidth);
        }

        var aspect = pageWidth / pageHeight;
        var width = Math.Min(PreferredWidth, Math.Min(MaxSide * aspect, Math.Sqrt(MaxPixels * aspect)));
        var height = width / aspect;

        return ((uint)Math.Clamp(Math.Round(width), 1, MaxSide), (uint)Math.Clamp(Math.Round(height), 1, MaxSide));
    }
}

/// <summary>Empilement des états graphiques d'un flux de contenu PDF.</summary>
/// <param name="Final">Niveau atteint à la fin : positif si des <c>q</c> restent ouverts.</param>
/// <param name="Lowest">Niveau le plus bas : négatif si des <c>Q</c> sont en trop.</param>
public readonly record struct GraphicsStateBalance(int Final, int Lowest)
{
    /// <summary>Lecture sans récursion : un contenu hostile ne peut pas épuiser la pile.</summary>
    public static GraphicsStateBalance Measure(ReadOnlySpan<byte> content)
    {
        var level = 0;
        var lowest = 0;
        var position = 0;

        while (position < content.Length)
        {
            var current = content[position];

            if (IsWhitespace(current))
            {
                position++;
            }
            else if (current == (byte)'%')
            {
                while (position < content.Length && content[position] is not ((byte)'\r' or (byte)'\n'))
                {
                    position++;
                }
            }
            else if (current == (byte)'(')
            {
                position = AfterLiteralString(content, position);
            }
            else if (current == (byte)'<' && position + 1 < content.Length && content[position + 1] != (byte)'<')
            {
                var end = content[position..].IndexOf((byte)'>');
                position = end < 0 ? content.Length : position + end + 1;
            }
            else if (current == (byte)'/')
            {
                // Un nom, jamais une instruction : /q désigne une ressource.
                position++;

                while (position < content.Length && !IsWhitespace(content[position]) && !IsDelimiter(content[position]))
                {
                    position++;
                }
            }
            else if (IsDelimiter(current))
            {
                position++;
            }
            else
            {
                var start = position;

                while (position < content.Length && !IsWhitespace(content[position]) && !IsDelimiter(content[position]))
                {
                    position++;
                }

                var token = content[start..position];

                if (token.SequenceEqual("q"u8))
                {
                    level++;
                }
                else if (token.SequenceEqual("Q"u8))
                {
                    level--;
                    lowest = Math.Min(lowest, level);
                }
                else if (token.SequenceEqual("ID"u8))
                {
                    position = AfterInlineImage(content, position);
                }
            }
        }

        return new GraphicsStateBalance(level, lowest);
    }

    private static int AfterLiteralString(ReadOnlySpan<byte> content, int position)
    {
        var depth = 0;

        for (; position < content.Length; position++)
        {
            switch (content[position])
            {
                case (byte)'\\':
                    position++;
                    break;
                case (byte)'(':
                    depth++;
                    break;
                case (byte)')':
                    if (--depth == 0)
                    {
                        return position + 1;
                    }

                    break;
            }
        }

        return content.Length;
    }

    // Les octets d'une image en ligne peuvent ressembler à n'importe quelle instruction.
    private static int AfterInlineImage(ReadOnlySpan<byte> content, int position)
    {
        for (var i = position; i + 1 < content.Length; i++)
        {
            if (content[i] == (byte)'E' && content[i + 1] == (byte)'I' &&
                i > 0 && IsWhitespace(content[i - 1]) &&
                (i + 2 == content.Length || IsWhitespace(content[i + 2])))
            {
                return i + 2;
            }
        }

        return content.Length;
    }

    private static bool IsWhitespace(byte value) => value is 0 or 9 or 10 or 12 or 13 or 32;

    private static bool IsDelimiter(byte value) => value is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';
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

    /// <summary>Écrit une copie signée à partir du document lu. Le fichier d'origine n'est jamais modifié.</summary>
    public static void Sign(PdfSnapshot source, string destination, IReadOnlyCollection<SignaturePlacement> placements)
    {
        if (PdfFiles.IsSameFile(source.Path, destination))
        {
            throw new ArgumentException(Loc.T("La copie signée ne peut pas remplacer le fichier d'origine."), nameof(destination));
        }

        using var input = source.OpenRead();
        using var document = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
        var pageCount = document.PageCount;
        var pages = placements.Select(placement => placement.PageIndex).Distinct().ToList();

        foreach (var group in placements.GroupBy(placement => placement.PageIndex))
        {
            var page = document.Pages[group.Key];
            var balance = GraphicsStateBalance.Measure(ContentOf(page));

            // Le contenu d'origine est isolé, même déséquilibré : des Q en trop ne dépilent que nos q, et aucun q laissé ouvert ne survit.
            var opening = 1 - balance.Lowest;
            var closing = opening + balance.Final;

            page.Contents.PrependContent().CreateStream(Encoding.ASCII.GetBytes(Repeat("q\n", opening)));
            page.Contents.AppendContent().CreateStream(Encoding.ASCII.GetBytes(Repeat("Q\n", closing) + BuildContent(FrameOf(page), group)));
        }

        var temporaryPath = $"{destination}.{Guid.NewGuid():N}.tmp";

        try
        {
            document.Save(temporaryPath);
            VerifyCopy(temporaryPath, pageCount, pages);
            File.Move(temporaryPath, destination, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    /// <summary>Faux si le PDF est protégé, interdit la modification ou ne peut pas être relu.</summary>
    public static bool CanSign(PdfSnapshot source)
    {
        try
        {
            // PDFsharp ouvre certains fichiers chiffrés sans erreur, puis échoue en lisant leur contenu :
            // on regarde directement si le fichier déclare un chiffrement. Cette entrée reste toujours en clair.
            if (source.Bytes.Span.IndexOf("/Encrypt"u8) >= 0)
            {
                return false;
            }

            using var input = source.OpenRead();
            using var document = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
            return document.PageCount > 0;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // PDFsharp signale ces cas avec des types d'exceptions très variés.
            return false;
        }
    }

    /// <summary>Une signature numérique existante cesse d'être valide dès que le fichier change.</summary>
    public static bool HasDigitalSignature(PdfSnapshot source)
    {
        const int SignaturesExist = 1;

        using var input = source.OpenRead();
        using var document = PdfReader.Open(input, PdfDocumentOpenMode.Import);

        return document.Internals.Catalog.Elements.GetDictionary("/AcroForm") is { } form
            && (form.Elements.GetInteger("/SigFlags") & SignaturesExist) != 0;
    }

    private static string Repeat(string text, int count) => string.Concat(Enumerable.Repeat(text, count));

    private static byte[] ContentOf(PdfPage page)
    {
        using var content = new MemoryStream();

        foreach (var part in page.Contents)
        {
            // Un flux qu'on ne sait pas décoder rendrait la mesure fausse : mieux vaut refuser que de déplacer la signature.
            var bytes = part.Stream?.UnfilteredValue ?? [];

            if (bytes.Length == 0 && part.Stream?.Value is { Length: > 0 })
            {
                throw new InvalidDataException(Loc.T("Le contenu de cette page ne peut pas être lu."));
            }

            content.Write(bytes);
            content.WriteByte((byte)'\n');
        }

        return content.ToArray();
    }

    // Dernier contrôle avant de remplacer quoi que ce soit : la copie doit se relire, garder toutes ses pages et finir à l'état graphique d'origine.
    private static void VerifyCopy(string path, int pageCount, IReadOnlyList<int> signedPages)
    {
        using var copy = PdfReader.Open(path, PdfDocumentOpenMode.Modify);

        var isSound = copy.PageCount == pageCount && signedPages.All(index =>
        {
            var balance = GraphicsStateBalance.Measure(ContentOf(copy.Pages[index]));
            return balance is { Final: 0, Lowest: 0 };
        });

        if (!isSound)
        {
            throw new InvalidDataException(Loc.T("La copie signée n'a pas passé le contrôle de relecture."));
        }
    }

    private static PdfPageFrame FrameOf(PdfPage page)
    {
        // Lire CropBox sur une page qui n'en a pas en créerait une vide, et changerait le format de la page.
        var box = page.Elements.ContainsKey("/CropBox") && !page.CropBox.IsZero ? page.CropBox : page.MediaBox;

        return new PdfPageFrame(
            Math.Min(box.X1, box.X2),
            Math.Min(box.Y1, box.Y2),
            Math.Abs(box.X2 - box.X1),
            Math.Abs(box.Y2 - box.Y1),
            page.Rotate);
    }
}

/// <summary>Signature mémorisée sur ce poste uniquement, et chiffrée avant d'atteindre le disque.</summary>
/// <param name="path">Fichier chiffré.</param>
/// <param name="protect">Chiffrement pour l'utilisateur courant.</param>
/// <param name="unprotect">Déchiffrement ; lève <see cref="CryptographicException"/> si le fichier n'est pas lisible.</param>
/// <param name="legacyPath">Fichier en clair des versions précédentes, repris puis supprimé.</param>
public sealed class SignatureStore(string path, Func<byte[], byte[]> protect, Func<byte[], byte[]> unprotect, string? legacyPath = null)
{
    public bool Exists => File.Exists(path) || (legacyPath is not null && File.Exists(legacyPath));

    public SignatureInk? Load()
    {
        if (!File.Exists(path) && legacyPath is not null && File.Exists(legacyPath))
        {
            return ImportLegacyFile(legacyPath);
        }

        try
        {
            return Usable(JsonSerializer.Deserialize<SignatureInk>(unprotect(File.ReadAllBytes(path))));
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException or CryptographicException)
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
            File.WriteAllBytes(temporaryPath, protect(JsonSerializer.SerializeToUtf8Bytes(signature)));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    public void Delete()
    {
        File.Delete(path);

        if (legacyPath is not null)
        {
            File.Delete(legacyPath);
        }
    }

    // Un fichier modifié à la main ne doit pas faire tomber la fenêtre en traçant la signature.
    private static SignatureInk? Usable(SignatureInk? ink)
    {
        static bool IsPositive(double value) => value > 0 && !double.IsInfinity(value);

        return ink is { Strokes.Count: > 0 } &&
               IsPositive(ink.AspectRatio) &&
               IsPositive(ink.ThicknessShare) &&
               ink.Strokes.All(stroke => stroke is { Count: > 0 })
            ? ink
            : null;
    }

    // Le fichier en clair ne disparaît qu'une fois la copie chiffrée écrite.
    private SignatureInk? ImportLegacyFile(string legacy)
    {
        try
        {
            var ink = Usable(JsonSerializer.Deserialize<SignatureInk>(File.ReadAllText(legacy)));

            if (ink is not null)
            {
                Save(ink);
            }

            File.Delete(legacy);
            return ink;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException or CryptographicException)
        {
            return null;
        }
    }
}
