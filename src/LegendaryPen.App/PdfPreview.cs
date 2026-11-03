using System.IO;
using System.Windows.Media.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using WinPdf = Windows.Data.Pdf;

namespace LegendaryPen.App;

/// <summary>Affichage des pages avec le moteur PDF intégré à Windows.</summary>
internal sealed class PdfPreview
{
    // Assez large pour rester net sur un grand écran, sans coût mémoire excessif pour une page.
    private const uint RenderWidth = 1800;

    private readonly WinPdf.PdfDocument _document;

    private PdfPreview(WinPdf.PdfDocument document) => _document = document;

    public int PageCount => (int)_document.PageCount;

    public static async Task<PdfPreview> OpenAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));

        return new PdfPreview(await WinPdf.PdfDocument.LoadFromFileAsync(file));
    }

    public async Task<BitmapSource> RenderAsync(int pageIndex)
    {
        using var page = _document.GetPage((uint)pageIndex);
        using var stream = new InMemoryRandomAccessStream();

        await page.RenderToStreamAsync(stream, new WinPdf.PdfPageRenderOptions { DestinationWidth = RenderWidth });
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
