using System.Globalization;

namespace LegendaryPen.Core;

public readonly record struct InkColor(byte R, byte G, byte B)
{
    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";

    public static bool TryParse(string? text, out InkColor color)
    {
        color = default;
        var digits = text?.Trim().TrimStart('#');

        if (digits is not { Length: 6 } ||
            !int.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        color = new InkColor((byte)(value >> 16), (byte)(value >> 8), (byte)value);
        return true;
    }

    /// <param name="hue">Teinte, de 0 à 360.</param>
    /// <param name="saturation">Saturation, de 0 à 1.</param>
    /// <param name="brightness">Luminosité, de 0 à 1.</param>
    public static InkColor FromHsv(double hue, double saturation, double brightness)
    {
        var sector = (((hue % 360) + 360) % 360) / 60;
        var chroma = brightness * saturation;
        var secondary = chroma * (1 - Math.Abs((sector % 2) - 1));
        var match = brightness - chroma;

        var (r, g, b) = (int)sector switch
        {
            0 => (chroma, secondary, 0d),
            1 => (secondary, chroma, 0d),
            2 => (0d, chroma, secondary),
            3 => (0d, secondary, chroma),
            4 => (secondary, 0d, chroma),
            _ => (chroma, 0d, secondary),
        };

        return new InkColor(ToByte(r + match), ToByte(g + match), ToByte(b + match));
    }

    public (double Hue, double Saturation, double Brightness) ToHsv()
    {
        var (r, g, b) = (R / 255d, G / 255d, B / 255d);
        var max = Math.Max(r, Math.Max(g, b));
        var chroma = max - Math.Min(r, Math.Min(g, b));

        double hue = 0;

        if (chroma > 0)
        {
            hue = max == r ? ((g - b) / chroma) + 6
                : max == g ? ((b - r) / chroma) + 2
                : ((r - g) / chroma) + 4;
            hue = hue * 60 % 360;
        }

        return (hue, max == 0 ? 0 : chroma / max, max);
    }

    private static byte ToByte(double value) => (byte)Math.Round(value * 255);
}

/// <summary>Couleur et taille du stylo, partagées par tous les écrans.</summary>
public sealed class PenState
{
    public const int QuickColorCount = 6;
    public const double MinSize = 1;
    public const double MaxSize = 40;
    public const double DefaultSize = 5;

    public static readonly IReadOnlyList<double> SizePresets = [2, DefaultSize, 10];

    public static readonly IReadOnlyList<InkColor> DefaultQuickColors =
    [
        new(0xE5, 0x39, 0x35),
        new(0xFB, 0x8C, 0x00),
        new(0xFD, 0xD8, 0x35),
        new(0x43, 0xA0, 0x47),
        new(0x1E, 0x88, 0xE5),
        new(0x21, 0x21, 0x21),
    ];

    private readonly InkColor[] _quickColors;

    public PenState(AppSettings? settings = null)
    {
        _quickColors = ReadQuickColors(settings?.QuickColors);
        Color = InkColor.TryParse(settings?.PenColor, out var color) ? color : _quickColors[0];
        Size = Math.Clamp(settings?.PenSize ?? DefaultSize, MinSize, MaxSize);
    }

    public IReadOnlyList<InkColor> QuickColors => _quickColors;

    public InkColor Color { get; private set; }

    public double Size { get; private set; }

    /// <summary>Position de la couleur actuelle parmi les couleurs rapides, si elle en fait partie.</summary>
    public int? SelectedQuickColor
    {
        get
        {
            var index = Array.IndexOf(_quickColors, Color);
            return index >= 0 ? index : null;
        }
    }

    public event Action? Changed;

    public void SelectQuickColor(int index) => SetColor(_quickColors[index]);

    public void SetColor(InkColor color)
    {
        Color = color;
        Changed?.Invoke();
    }

    public void ReplaceQuickColor(int index, InkColor color)
    {
        _quickColors[index] = color;
        SetColor(color);
    }

    public void SetSize(double size)
    {
        Size = Math.Clamp(size, MinSize, MaxSize);
        Changed?.Invoke();
    }

    private static InkColor[] ReadQuickColors(IReadOnlyList<string>? saved)
    {
        if (saved?.Count == QuickColorCount)
        {
            var colors = new InkColor[QuickColorCount];

            for (var i = 0; i < QuickColorCount; i++)
            {
                if (!InkColor.TryParse(saved[i], out colors[i]))
                {
                    return [.. DefaultQuickColors];
                }
            }

            return colors;
        }

        return [.. DefaultQuickColors];
    }
}
