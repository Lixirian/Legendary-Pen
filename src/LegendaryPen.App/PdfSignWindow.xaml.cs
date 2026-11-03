using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using LegendaryPen.Core;
using Path = System.IO.Path;

namespace LegendaryPen.App;

/// <summary>Pose des signatures sur un PDF et en enregistre une copie.</summary>
public partial class PdfSignWindow : Window
{
    private const double DefaultSignatureWidth = 0.3;
    private const double MaxPageWidth = 1000;
    private const double PageMargin = 24;
    private const double HandleSize = 12;

    private readonly SignatureStore _store;
    private readonly List<PlacedSignature> _placed = [];
    private PdfPreview? _preview;
    private string? _path;
    private int _pageIndex;
    private double _pageAspect = 1;
    private PlacedSignature? _selected;
    private DragState? _drag;

    public PdfSignWindow(SignatureStore store)
    {
        _store = store;
        InitializeComponent();
        UpdateCommands();
    }

    /// <summary>Signatures posées, quelle que soit la page.</summary>
    public IReadOnlyList<SignaturePlacement> Placements => _placed.Select(placed => placed.Placement).ToList();

    /// <summary>Ouvre un document. Renvoie false si Windows ne peut pas le lire.</summary>
    public async Task<bool> OpenAsync(string path)
    {
        PdfPreview preview;

        if (!await Task.Run(() => PdfSigner.CanSign(path)))
        {
            MessageWindow.Show("Ce PDF ne peut pas être signé ici : il est protégé, interdit la modification ou est endommagé. Il n'a pas été modifié.");
            return false;
        }

        try
        {
            preview = await PdfPreview.OpenAsync(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            MessageWindow.Show("Windows ne parvient pas à afficher ce PDF. Il n'a pas été modifié.");
            return false;
        }

        _preview = preview;
        _path = path;
        _placed.Clear();
        Select(null);
        await ShowPageAsync(0);

        Title = $"Signer un PDF - {Path.GetFileName(path)}";

        if (HasDigitalSignature(path))
        {
            MessageWindow.Show("Ce PDF porte une signature numérique. Elle restera valable dans l'original, mais plus dans la copie que tu enregistreras ici.");
        }

        return true;
    }

    /// <summary>Pose une signature au milieu de la partie visible de la page.</summary>
    public void Place(SignatureInk signature)
    {
        var visibleMiddle = (Viewer.VerticalOffset + (Viewer.ViewportHeight / 2) - PageMargin) / Math.Max(1, PageImage.Height);
        var placement = new SignaturePlacement(signature, _pageIndex, 0, 0, DefaultSignatureWidth);

        placement = placement.MovedTo(
            (1 - DefaultSignatureWidth) / 2,
            visibleMiddle - (placement.HeightShare(_pageAspect) / 2),
            _pageAspect);

        var placed = new PlacedSignature(placement, BuildView(signature));
        _placed.Add(placed);
        ShowPlacedOnPage();
        Select(placed);
    }

    /// <summary>Écrit la copie signée. Renvoie false en cas d'échec, après l'avoir signalé.</summary>
    public async Task<bool> SaveAsync(string destination)
    {
        var source = _path!;
        var placements = Placements;

        SaveButton.IsEnabled = false;

        try
        {
            await Task.Run(() => PdfSigner.Sign(source, destination, placements));
            StatusText.Text = $"Enregistré : {destination}";
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // PDFsharp signale les fichiers qu'il ne sait pas relire avec plusieurs types d'exceptions.
            MessageWindow.Show($"Impossible d'enregistrer le PDF signé. L'original n'a pas été modifié.\n\n{exception.Message}");
            return false;
        }
        finally
        {
            UpdateCommands();
        }
    }

    private static bool HasDigitalSignature(string path)
    {
        try
        {
            return PdfSigner.HasDigitalSignature(path);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Sans cette information, l'avertissement ne s'affiche pas ; l'enregistrement dira s'il échoue.
            return false;
        }
    }

    private async Task ShowPageAsync(int pageIndex)
    {
        Select(null);

        var image = await _preview!.RenderAsync(pageIndex);

        _pageIndex = pageIndex;
        _pageAspect = (double)image.PixelWidth / image.PixelHeight;
        PageImage.Source = image;
        PageHost.Visibility = Visibility.Visible;
        EmptyHint.Visibility = Visibility.Collapsed;
        Viewer.ScrollToTop();

        UpdatePageSize();
        ShowPlacedOnPage();
        UpdateCommands();
    }

    private void UpdatePageSize()
    {
        var width = Math.Clamp(Viewer.ViewportWidth - PageMargin, 100, MaxPageWidth);

        PageImage.Width = width;
        PageImage.Height = width / _pageAspect;
        PageHost.Width = PageImage.Width;
        PageHost.Height = PageImage.Height;

        foreach (var placed in _placed)
        {
            Layout(placed);
        }
    }

    private void ShowPlacedOnPage()
    {
        SignatureLayer.Children.Clear();

        foreach (var placed in _placed.Where(placed => placed.Placement.PageIndex == _pageIndex))
        {
            SignatureLayer.Children.Add(placed.View);
            Layout(placed);
        }
    }

    private void Layout(PlacedSignature placed)
    {
        var placement = placed.Placement;

        Canvas.SetLeft(placed.View, placement.Left * PageImage.Width);
        Canvas.SetTop(placed.View, placement.Top * PageImage.Height);
        placed.View.Width = placement.Width * PageImage.Width;
        placed.View.Height = placement.HeightShare(_pageAspect) * PageImage.Height;
    }

    private Grid BuildView(SignatureInk signature)
    {
        var accent = (Brush)FindResource("AccentBrush");
        var view = new Grid { Background = Brushes.Transparent, Cursor = Cursors.SizeAll };

        System.Windows.Automation.AutomationProperties.SetName(view, "Signature posée");
        view.Children.Add(new Image { Source = SignatureImage.Build(signature), Stretch = Stretch.Fill });
        view.Children.Add(new Rectangle
        {
            Stroke = accent,
            StrokeThickness = 1,
            StrokeDashArray = [3, 2],
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        });
        view.Children.Add(new Rectangle
        {
            Width = HandleSize,
            Height = HandleSize,
            Fill = accent,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Cursor = Cursors.SizeNWSE,
            Visibility = Visibility.Collapsed,
            Tag = "Handle",
        });

        view.MouseLeftButtonDown += OnSignaturePressed;
        view.MouseMove += OnSignatureMoved;
        view.MouseLeftButtonUp += OnSignatureReleased;

        return view;
    }

    private void Select(PlacedSignature? placed)
    {
        _selected = placed;

        foreach (var item in _placed)
        {
            var visibility = item == placed ? Visibility.Visible : Visibility.Collapsed;
            item.View.Children[1].Visibility = visibility;
            item.View.Children[2].Visibility = visibility;
        }

        UpdateCommands();
    }

    private void UpdateCommands()
    {
        var hasDocument = _preview is not null;

        PreviousButton.IsEnabled = hasDocument && _pageIndex > 0;
        NextButton.IsEnabled = hasDocument && _pageIndex < _preview!.PageCount - 1;
        PageLabel.Text = hasDocument ? $"Page {_pageIndex + 1} / {_preview!.PageCount}" : "";
        DrawButton.IsEnabled = hasDocument;
        PlaceSavedButton.IsEnabled = hasDocument && _store.Exists;
        ForgetButton.IsEnabled = _store.Exists;
        RemoveButton.IsEnabled = _selected is not null;
        SaveButton.IsEnabled = hasDocument && _placed.Count > 0;
    }

    private void RemoveSelected()
    {
        if (_selected is not { } selected)
        {
            return;
        }

        _placed.Remove(selected);
        SignatureLayer.Children.Remove(selected.View);
        Select(null);
    }

    private void OnSignaturePressed(object sender, MouseButtonEventArgs e)
    {
        var view = (Grid)sender;
        var placed = _placed.First(item => item.View == view);
        var isResize = e.OriginalSource is FrameworkElement { Tag: "Handle" };

        Select(placed);
        _drag = new DragState(placed, isResize, e.GetPosition(SignatureLayer), placed.Placement);
        view.CaptureMouse();
        e.Handled = true;
    }

    private void OnSignatureMoved(object sender, MouseEventArgs e)
    {
        if (_drag is not { } drag)
        {
            return;
        }

        var position = e.GetPosition(SignatureLayer);
        var deltaX = (position.X - drag.Start.X) / PageImage.Width;
        var deltaY = (position.Y - drag.Start.Y) / PageImage.Height;

        drag.Placed.Placement = drag.IsResize
            ? drag.Origin.ResizedTo(drag.Origin.Width + deltaX, _pageAspect)
            : drag.Origin.MovedTo(drag.Origin.Left + deltaX, drag.Origin.Top + deltaY, _pageAspect);

        Layout(drag.Placed);
    }

    private void OnSignatureReleased(object sender, MouseButtonEventArgs e)
    {
        _drag = null;
        ((Grid)sender).ReleaseMouseCapture();
    }

    private void OnLayerPressed(object sender, MouseButtonEventArgs e) => Select(null);

    private void OnViewerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_preview is not null)
        {
            // La largeur utile n'est connue qu'après le calcul de la barre de défilement.
            Dispatcher.BeginInvoke(UpdatePageSize, System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && _selected is not null)
        {
            RemoveSelected();
            e.Handled = true;
        }
    }

    private async void OnOpenClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Ouvrir un PDF",
            Filter = "Document PDF (*.pdf)|*.pdf",
        };

        if (dialog.ShowDialog(this) == true)
        {
            await OpenAsync(dialog.FileName);
        }
    }

    private async void OnPreviousClicked(object sender, RoutedEventArgs e) => await ShowPageAsync(_pageIndex - 1);

    private async void OnNextClicked(object sender, RoutedEventArgs e) => await ShowPageAsync(_pageIndex + 1);

    private void OnDrawClicked(object sender, RoutedEventArgs e)
    {
        var pad = new SignaturePadWindow { Owner = this };

        if (pad.ShowDialog() != true || pad.Ink is not { } ink)
        {
            return;
        }

        if (pad.Remember)
        {
            _store.Save(ink);
        }

        Place(ink);
    }

    private void OnPlaceSavedClicked(object sender, RoutedEventArgs e)
    {
        if (_store.Load() is { } ink)
        {
            Place(ink);
        }
        else
        {
            MessageWindow.Show("La signature mémorisée est illisible. Trace-la de nouveau.");
        }
    }

    private void OnRemoveClicked(object sender, RoutedEventArgs e) => RemoveSelected();

    private void OnForgetClicked(object sender, RoutedEventArgs e)
    {
        _store.Delete();
        UpdateCommands();
        StatusText.Text = "La signature mémorisée a été supprimée de cet ordinateur.";
    }

    private async void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Enregistrer le PDF signé",
            Filter = "Document PDF (*.pdf)|*.pdf",
            DefaultExt = ".pdf",
            InitialDirectory = Path.GetDirectoryName(_path),
            FileName = Path.GetFileName(PdfFiles.SignedPath(_path!, File.Exists)),
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        if (PdfFiles.IsSameFile(_path!, dialog.FileName))
        {
            MessageWindow.Show("Choisis un autre nom : le fichier d'origine n'est jamais remplacé.");
            return;
        }

        await SaveAsync(dialog.FileName);
    }

    private sealed class PlacedSignature(SignaturePlacement placement, Grid view)
    {
        public SignaturePlacement Placement { get; set; } = placement;

        public Grid View { get; } = view;
    }

    private sealed record DragState(PlacedSignature Placed, bool IsResize, Point Start, SignaturePlacement Origin);
}

/// <summary>Image vectorielle d'une signature, pour l'aperçu.</summary>
internal static class SignatureImage
{
    public static DrawingImage Build(SignatureInk ink)
    {
        var geometry = new StreamGeometry();

        using (var context = geometry.Open())
        {
            foreach (var stroke in ink.Strokes)
            {
                context.BeginFigure(ToBox(stroke[0], ink), isFilled: false, isClosed: false);

                if (stroke.Count == 1)
                {
                    // WPF n'affiche pas un trait de longueur nulle.
                    context.LineTo(ToBox(stroke[0], ink) + new Vector(0.001, 0), isStroked: true, isSmoothJoin: true);
                }

                foreach (var point in stroke.Skip(1))
                {
                    context.LineTo(ToBox(point, ink), isStroked: true, isSmoothJoin: true);
                }
            }
        }

        var box = new RectangleGeometry(new Rect(0, 0, ink.AspectRatio, 1));
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(ink.Color.R, ink.Color.G, ink.Color.B)), ink.ThicknessShare * ink.AspectRatio)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };

        // La boîte transparente fixe les limites de l'image : l'aperçu garde les proportions du PDF.
        var drawing = new DrawingGroup { ClipGeometry = box };
        drawing.Children.Add(new GeometryDrawing(Brushes.Transparent, null, box));
        drawing.Children.Add(new GeometryDrawing(null, pen, geometry));

        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }

    private static Point ToBox(InkPoint point, SignatureInk ink) => new(point.X * ink.AspectRatio, point.Y);
}
