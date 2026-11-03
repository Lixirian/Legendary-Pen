using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LegendaryPen.Core;

namespace LegendaryPen.App;

/// <summary>Curseurs reflétant la taille et la couleur de l'outil actif.</summary>
internal static class ToolCursors
{
    public const double EraserDiameter = 18;
    public const double HighlighterScale = 3;

    private const int MinSide = 16;
    private const int MaxSide = 128;

    private static readonly Pen LightOutline = CreatePen(Colors.White);
    private static readonly Pen DarkOutline = CreatePen(Color.FromArgb(200, 0, 0, 0));

    public static Cursor Create(DrawingTool tool, PenState pen, double scale)
    {
        return tool switch
        {
            DrawingTool.Text => Cursors.IBeam,
            _ when tool.IsShape() => Cursors.Cross,
            DrawingTool.Eraser => Render(EraserDiameter * scale, scale, DrawEraser),
            DrawingTool.Highlighter => Render(pen.Size * HighlighterScale * scale, scale, (context, center, size) =>
            {
                var color = Color.FromArgb(120, pen.Color.R, pen.Color.G, pen.Color.B);
                var bounds = new Rect(center.X - (size / 2), center.Y - (size / 2), size, size);
                context.DrawRectangle(new SolidColorBrush(color), DarkOutline, bounds);
            }),
            _ => Render(pen.Size * scale, scale, (context, center, size) =>
            {
                var color = Color.FromRgb(pen.Color.R, pen.Color.G, pen.Color.B);
                context.DrawEllipse(new SolidColorBrush(color), null, center, size / 2, size / 2);
                context.DrawEllipse(null, LightOutline, center, (size / 2) + 1, (size / 2) + 1);
                context.DrawEllipse(null, DarkOutline, center, (size / 2) + 2, (size / 2) + 2);
            }),
        };
    }

    private static void DrawEraser(DrawingContext context, Point center, double size)
    {
        context.DrawEllipse(Brushes.Transparent, DarkOutline, center, (size / 2) + 1, (size / 2) + 1);
        context.DrawEllipse(null, LightOutline, center, size / 2, size / 2);
    }

    private static Cursor Render(double size, double scale, Action<DrawingContext, Point, double> draw)
    {
        var side = Math.Clamp((int)Math.Ceiling(size) + 8, MinSide, MaxSide);
        var limited = Math.Min(size, side - 8);
        var center = new Point(side / 2.0, side / 2.0);

        var visual = new DrawingVisual();

        using (var context = visual.RenderOpen())
        {
            draw(context, center, limited);
        }

        var bitmap = new RenderTargetBitmap(side, side, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var pixels = new byte[side * side * 4];
        bitmap.CopyPixels(pixels, side * 4, 0);
        RemovePremultiplication(pixels);

        return new Cursor(new MemoryStream(CursorFile.Build(side, side, pixels, side / 2, side / 2)));
    }

    private static void RemovePremultiplication(byte[] pixels)
    {
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var alpha = pixels[i + 3];

            if (alpha is > 0 and < 255)
            {
                pixels[i] = (byte)Math.Min(255, pixels[i] * 255 / alpha);
                pixels[i + 1] = (byte)Math.Min(255, pixels[i + 1] * 255 / alpha);
                pixels[i + 2] = (byte)Math.Min(255, pixels[i + 2] * 255 / alpha);
            }
        }
    }

    private static Pen CreatePen(Color color)
    {
        var pen = new Pen(new SolidColorBrush(color), 1);
        pen.Freeze();
        return pen;
    }
}
