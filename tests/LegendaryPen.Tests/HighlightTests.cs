using LegendaryPen.Core;
using Xunit;

namespace LegendaryPen.Tests;

public class HighlightLineTests
{
    [Fact]
    public void A_shaky_nearly_horizontal_stroke_becomes_perfectly_horizontal()
    {
        InkPoint[] points = [new(100, 200), new(200, 206), new(300, 195), new(400, 204), new(500, 197)];

        var (start, end, isHorizontal) = HighlightLine.Straighten(points);

        Assert.True(isHorizontal);
        Assert.Equal(start.Y, end.Y);
        Assert.Equal(100, start.X);
        Assert.Equal(500, end.X);
        Assert.InRange(start.Y, 195, 206);
    }

    [Fact]
    public void A_right_to_left_stroke_still_covers_the_same_span()
    {
        InkPoint[] points = [new(500, 200), new(300, 203), new(100, 199)];

        var (start, end, isHorizontal) = HighlightLine.Straighten(points);

        Assert.True(isHorizontal);
        Assert.Equal(100, start.X);
        Assert.Equal(500, end.X);
    }

    [Fact]
    public void A_diagonal_stroke_keeps_its_angle_and_becomes_straight()
    {
        InkPoint[] points = [new(100, 100), new(202, 198), new(298, 302), new(400, 400)];

        var (start, end, isHorizontal) = HighlightLine.Straighten(points);

        Assert.False(isHorizontal);
        Assert.Equal(45, Math.Atan2(end.Y - start.Y, end.X - start.X) * 180 / Math.PI, 0);
        Assert.InRange(start.X, 95, 105);
        Assert.InRange(end.X, 395, 405);
    }

    [Fact]
    public void A_diagonal_stroke_keeps_the_direction_it_was_drawn_in()
    {
        InkPoint[] points = [new(400, 400), new(298, 302), new(202, 198), new(100, 100)];

        var (start, end, _) = HighlightLine.Straighten(points);

        Assert.True(start.X > end.X);
    }
}

public class HighlightSnapperTests
{
    private const byte White = 255;
    private const byte Black = 20;

    private static byte[] Blank(int width, int height, byte value)
    {
        var pixels = new byte[width * height];
        Array.Fill(pixels, value);
        return pixels;
    }

    /// <summary>Dessine un « mot » : des lettres verticales séparées d'un pixel de fond.</summary>
    private static void Word(byte[] pixels, int imageWidth, int left, int right, int top, int bottom, byte ink)
    {
        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x += 3)
            {
                pixels[(y * imageWidth) + x] = ink;
                pixels[(y * imageWidth) + x + 1] = ink;
            }
        }
    }

    private static GrayImage Line(byte background = White, byte ink = Black)
    {
        var pixels = Blank(800, 120, background);
        Word(pixels, 800, 100, 220, 50, 70, ink);
        Word(pixels, 800, 240, 400, 50, 70, ink);
        Word(pixels, 800, 420, 560, 50, 70, ink);
        return new GrayImage(800, 120, pixels);
    }

    [Fact]
    public void Finds_the_exact_height_of_the_line_under_the_stroke()
    {
        var span = HighlightSnapper.FindText(Line(), 110, 390, 58);

        Assert.NotNull(span);
        Assert.Equal(50, span.Value.Top);
        Assert.Equal(70, span.Value.Bottom);
    }

    [Fact]
    public void Snaps_to_the_start_and_end_of_the_touched_words()
    {
        var span = HighlightSnapper.FindText(Line(), 130, 380, 60);

        Assert.Equal(100, span!.Value.Left);
        Assert.InRange(span.Value.Right, 398, 401);
    }

    [Fact]
    public void Only_the_words_between_the_start_and_end_of_the_stroke_are_selected()
    {
        // Commence dans le deuxième mot et s'y termine : ni le premier ni le troisième.
        var span = HighlightSnapper.FindText(Line(), 280, 360, 60);

        Assert.Equal(240, span!.Value.Left);
        Assert.InRange(span.Value.Right, 398, 401);
    }

    [Fact]
    public void Separates_words_with_a_narrow_space()
    {
        var pixels = Blank(800, 120, White);
        Word(pixels, 800, 100, 200, 50, 70, Black);
        Word(pixels, 800, 206, 300, 50, 70, Black);
        Word(pixels, 800, 306, 400, 50, 70, Black);

        var span = HighlightSnapper.FindText(new GrayImage(800, 120, pixels), 220, 280, 60);

        Assert.Equal(206, span!.Value.Left);
        Assert.InRange(span.Value.Right, 298, 301);
    }

    [Fact]
    public void A_stroke_slightly_above_or_below_still_finds_the_line()
    {
        Assert.Equal(50, HighlightSnapper.FindText(Line(), 110, 390, 40)!.Value.Top);
        Assert.Equal(50, HighlightSnapper.FindText(Line(), 110, 390, 80)!.Value.Top);
    }

    [Fact]
    public void A_word_barely_touched_at_the_end_is_left_out()
    {
        var span = HighlightSnapper.FindText(Line(), 110, 250, 60);

        Assert.InRange(span!.Value.Right, 218, 221);
    }

    [Fact]
    public void A_single_word_barely_touched_is_still_selected()
    {
        var span = HighlightSnapper.FindText(Line(), 105, 112, 60);

        Assert.Equal(100, span!.Value.Left);
        Assert.InRange(span.Value.Right, 218, 221);
    }

    [Fact]
    public void Works_on_a_dark_background()
    {
        var span = HighlightSnapper.FindText(Line(Black, White), 130, 380, 60);

        Assert.Equal(new TextSpan(100, 50, span!.Value.Right, 70), span.Value);
    }

    [Fact]
    public void Chooses_the_line_closest_to_the_stroke()
    {
        var pixels = Blank(800, 200, White);
        Word(pixels, 800, 100, 400, 40, 60, Black);
        Word(pixels, 800, 100, 400, 120, 140, Black);
        var image = new GrayImage(800, 200, pixels);

        Assert.Equal(40, HighlightSnapper.FindText(image, 110, 390, 70)!.Value.Top);
        Assert.Equal(120, HighlightSnapper.FindText(image, 110, 390, 105)!.Value.Top);
    }

    [Fact]
    public void A_blank_area_has_no_text()
    {
        Assert.Null(HighlightSnapper.FindText(new GrayImage(800, 120, Blank(800, 120, White)), 100, 500, 60));
    }

    [Fact]
    public void A_noisy_image_is_not_taken_for_text()
    {
        var random = new Random(42);
        var pixels = new byte[800 * 120];
        random.NextBytes(pixels);

        Assert.Null(HighlightSnapper.FindText(new GrayImage(800, 120, pixels), 100, 500, 60));
    }

    [Fact]
    public void A_solid_block_is_not_taken_for_text()
    {
        var pixels = Blank(800, 120, White);

        for (var y = 40; y < 80; y++)
        {
            Array.Fill(pixels, Black, (y * 800) + 100, 400);
        }

        Assert.Null(HighlightSnapper.FindText(new GrayImage(800, 120, pixels), 150, 450, 60));
    }

    [Fact]
    public void A_shape_much_taller_than_a_line_is_not_taken_for_text()
    {
        var pixels = Blank(800, 300, White);
        Word(pixels, 800, 100, 400, 10, 290, Black);

        Assert.Null(HighlightSnapper.FindText(new GrayImage(800, 300, pixels), 110, 390, 150));
    }

    [Fact]
    public void A_stroke_outside_the_image_finds_nothing()
    {
        Assert.Null(HighlightSnapper.FindText(Line(), -50, 2000, 500));
    }

    [Fact]
    public void Horizontal_coordinates_outside_the_image_are_limited_to_it()
    {
        Assert.NotNull(HighlightSnapper.FindText(Line(), -50, 2000, 60));
    }

    [Theory]
    [InlineData(84, true)]
    [InlineData(85, false)]
    [InlineData(35, true)]
    [InlineData(34, false)]
    public void A_stroke_beside_a_line_does_not_target_it(int strokeY, bool expected)
    {
        Assert.Equal(expected, HighlightSnapper.FindText(Line(), 110, 390, strokeY) is not null);
    }

    /// <summary>Un mot dont les lettres, de largeur 4, sont séparées par des écarts irréguliers.</summary>
    private static int LooseWord(byte[] pixels, int imageWidth, int left, int top, int bottom, int[] letterGaps)
    {
        var x = left;

        foreach (var gap in letterGaps)
        {
            for (var y = top; y < bottom; y++)
            {
                Array.Fill(pixels, Black, (y * imageWidth) + x, 4);
            }

            x += 4 + gap;
        }

        for (var y = top; y < bottom; y++)
        {
            Array.Fill(pixels, Black, (y * imageWidth) + x, 4);
        }

        return x + 4;
    }

    [Fact]
    public void A_wide_gap_between_letters_does_not_cut_a_word()
    {
        var pixels = Blank(800, 120, White);
        var firstEnd = LooseWord(pixels, 800, 100, 50, 70, [1, 2, 4, 1, 3]);
        var secondStart = firstEnd + 8;
        var secondEnd = LooseWord(pixels, 800, secondStart, 50, 70, [2, 4, 1, 4, 2]);

        // Le trait commence sur une lettre située après le plus grand écart du deuxième mot.
        var span = HighlightSnapper.FindText(new GrayImage(800, 120, pixels), secondEnd - 6, secondEnd, 60);

        Assert.Equal(secondStart, span!.Value.Left);
        Assert.Equal(secondEnd, span.Value.Right);
    }

    [Fact]
    public void A_single_word_with_regular_letter_gaps_stays_whole()
    {
        var pixels = Blank(800, 120, White);
        var end = LooseWord(pixels, 800, 100, 50, 70, [1, 2, 3, 2, 1, 3]);

        var span = HighlightSnapper.FindText(new GrayImage(800, 120, pixels), end - 6, end, 60);

        Assert.Equal(100, span!.Value.Left);
        Assert.Equal(end, span.Value.Right);
    }

    [Fact]
    public void A_distant_text_column_does_not_change_the_word_spacing()
    {
        var pixels = Blank(800, 120, White);
        var firstEnd = LooseWord(pixels, 800, 100, 50, 70, [1, 3, 1]);
        var secondStart = firstEnd + 8;
        var secondEnd = LooseWord(pixels, 800, secondStart, 50, 70, [1, 3, 1]);
        LooseWord(pixels, 800, 600, 50, 70, [1, 3, 1]);

        var span = HighlightSnapper.FindText(new GrayImage(800, 120, pixels), secondStart + 2, secondEnd - 2, 60);

        Assert.Equal(secondStart, span!.Value.Left);
        Assert.Equal(secondEnd, span.Value.Right);
    }

    private static void Stem(byte[] pixels, int imageWidth, int left, int right, int top, int bottom)
    {
        for (var y = top; y < bottom; y++)
        {
            Array.Fill(pixels, Black, (y * imageWidth) + left, right - left);
        }
    }

    [Fact]
    public void Letters_of_a_selected_word_outside_the_stroke_are_covered()
    {
        var pixels = Blank(800, 120, White);
        Word(pixels, 800, 240, 400, 50, 70, Black);
        Stem(pixels, 800, 390, 394, 70, 76);
        Stem(pixels, 800, 245, 249, 42, 50);

        var span = HighlightSnapper.FindText(new GrayImage(800, 120, pixels), 260, 360, 60);

        Assert.Equal(42, span!.Value.Top);
        Assert.Equal(76, span.Value.Bottom);
    }

    [Fact]
    public void A_single_thin_descender_under_a_long_stroke_is_covered()
    {
        var pixels = Blank(800, 120, White);
        Word(pixels, 800, 100, 700, 50, 70, Black);
        Stem(pixels, 800, 300, 302, 70, 76);

        var span = HighlightSnapper.FindText(new GrayImage(800, 120, pixels), 110, 690, 60);

        Assert.Equal(76, span!.Value.Bottom);
    }

    [Fact]
    public void The_line_below_stays_out_when_a_blank_row_separates_it()
    {
        var pixels = Blank(800, 120, White);
        Word(pixels, 800, 100, 400, 50, 70, Black);
        Stem(pixels, 800, 200, 204, 70, 74);
        Word(pixels, 800, 100, 400, 75, 95, Black);

        var span = HighlightSnapper.FindText(new GrayImage(800, 120, pixels), 110, 390, 60);

        Assert.Equal(50, span!.Value.Top);
        Assert.Equal(74, span.Value.Bottom);
    }

    [Fact]
    public void A_long_vertical_rule_touching_a_word_does_not_stretch_the_line()
    {
        var pixels = Blank(800, 120, White);
        Word(pixels, 800, 100, 400, 50, 70, Black);
        Stem(pixels, 800, 200, 202, 70, 118);

        var span = HighlightSnapper.FindText(new GrayImage(800, 120, pixels), 110, 390, 60);

        Assert.Equal(70, span!.Value.Bottom);
    }

    [Fact]
    public void Right_to_left_coordinates_are_accepted()
    {
        var span = HighlightSnapper.FindText(Line(), 380, 130, 60);

        Assert.Equal(100, span!.Value.Left);
    }

    [Fact]
    public void The_setting_is_on_by_default_and_applied_live()
    {
        var options = new LiveOptions(new AppSettings());
        Assert.True(options.HighlightSnapEnabled);

        options.Apply(new AppSettings { HighlightSnapEnabled = false });
        Assert.False(options.HighlightSnapEnabled);
    }
}
