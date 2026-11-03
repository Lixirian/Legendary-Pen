using System.Windows;
using System.Windows.Ink;
using System.Windows.Media;
using LegendaryPen.Core;

namespace LegendaryPen.App;

/// <summary>Cadre dans lequel l'utilisateur trace sa signature.</summary>
public partial class SignaturePadWindow : Window
{
    private const double InkThickness = 2.5;
    private static readonly InkColor InkShade = new(0x1B, 0x2A, 0x6B);

    public SignaturePadWindow()
    {
        InitializeComponent();

        Pad.DefaultDrawingAttributes = new DrawingAttributes
        {
            Color = Color.FromRgb(InkShade.R, InkShade.G, InkShade.B),
            Width = InkThickness,
            Height = InkThickness,
            FitToCurve = true,
            IgnorePressure = true,
        };
        Pad.Strokes.StrokesChanged += (_, _) => UseButton.IsEnabled = Pad.Strokes.Count > 0;
    }

    public SignatureInk? Ink { get; private set; }

    public bool Remember => RememberBox.IsChecked == true;

    private void OnClearClicked(object sender, RoutedEventArgs e) => Pad.Strokes.Clear();

    private void OnUseClicked(object sender, RoutedEventArgs e)
    {
        var strokes = Pad.Strokes
            .Select(stroke => (IReadOnlyList<InkPoint>)stroke.StylusPoints.Select(point => new InkPoint(point.X, point.Y)).ToList())
            .ToList();

        Ink = SignatureInk.FromStrokes(strokes, InkThickness, InkShade);
        DialogResult = Ink is not null;
    }
}
