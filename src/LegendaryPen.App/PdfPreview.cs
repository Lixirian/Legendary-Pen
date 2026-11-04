using System.IO;
using System.Windows.Media.Imaging;
using LegendaryPen.Core;
using Windows.Storage.Streams;
using WinPdf = Windows.Data.Pdf;

namespace LegendaryPen.App;

/// <summary>Affichage des pages avec le moteur PDF intégré à Windows.</summary>
internal sealed class PdfPreview
{
    private readonly WinPdf.PdfDocument _document;

    private PdfPreview(WinPdf.PdfDocument document) => _document = document;

    public int PageCount => (int)_document.PageCount;

    /// <summary>Affiche exactement les octets qui seront signés, jamais une relecture du fichier.</summary>
    public static async Task<PdfPreview> OpenAsync(PdfSnapshot source)
    {
        return new PdfPreview(await WinPdf.PdfDocument.LoadFromStreamAsync(source.OpenRead().AsRandomAccessStream()));
    }

    public async Task<BitmapSource> RenderAsync(int pageIndex)
    {
        using var page = _document.GetPage((uint)pageIndex);
        using var stream = new InMemoryRandomAccessStream();

        var (width, height) = PdfRenderSize.For(page.Size.Width, page.Size.Height);

        await page.RenderToStreamAsync(stream, new WinPdf.PdfPageRenderOptions { DestinationWidth = width, DestinationHeight = height });
        stream.Seek(0);

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream.AsStreamForRead();
        image.EndInit();
        image.Freeze();

        return image;
    }
}
