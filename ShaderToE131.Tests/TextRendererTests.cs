using ShaderToE131;
using Xunit;

namespace ShaderToE131.Tests;

/// <summary>
/// Unit tests for <see cref="TextRenderer"/> — the 5×7 bitmap font used to draw
/// text notification banners onto the matrix framebuffer.
/// </summary>
public class TextRendererTests
{
    private const int W = PixelMapper.Width;   // 53
    private const int H = PixelMapper.Height;  // 11

    private static byte[] WhiteFrame()
    {
        var frame = new byte[W * H * 4];
        for (int i = 0; i < frame.Length; i++) frame[i] = 255;
        return frame;
    }

    private static bool IsLit(byte[] frame, int x, int y)
    {
        int idx = (y * W + x) * 4;
        return frame[idx] == 255 && frame[idx + 1] == 255 && frame[idx + 2] == 255;
    }

    [Fact]
    public void MeasureWidth_Empty_IsZero()
        => Assert.Equal(0, TextRenderer.MeasureWidth(""));

    [Theory]
    [InlineData("A", 5)]
    [InlineData("AB", 11)]
    [InlineData("ABC", 17)]
    public void MeasureWidth_AdvancesPerCharacter(string text, int expected)
        => Assert.Equal(expected, TextRenderer.MeasureWidth(text));

    [Fact]
    public void DrawBanner_FittingText_CentersItAndDrawsWhiteOnBlackBand()
    {
        var frame = WhiteFrame();
        // "HI" is 11px wide → centered at x = (53 - 11) / 2 = 21.
        TextRenderer.DrawBanner(frame.AsSpan(), W, H, "HI", elapsedMs: 0);

        // 'H' column 0 is 0x7F → the full left stroke is lit at x=21, rows 2..8.
        for (int y = 2; y <= 8; y++)
            Assert.True(IsLit(frame, 21, y), $"expected lit glyph pixel at (21,{y})");

        // 'H' column 1 is 0x08 → only the middle row (text row 3 → y=5) is lit at x=22.
        Assert.True(IsLit(frame, 22, 5));
        Assert.False(IsLit(frame, 22, 2));

        // The band (rows 1..9) is black where no glyph is drawn.
        Assert.False(IsLit(frame, 0, 5));

        // Rows outside the band keep the underlying frame (banner overlays the shader).
        for (int x = 0; x < W; x++)
        {
            Assert.True(IsLit(frame, x, 0), "row 0 should be untouched");
            Assert.True(IsLit(frame, x, 10), "row 10 should be untouched");
        }
    }

    [Fact]
    public void DrawBanner_LongText_ScrollsAsMarquee()
    {
        // 11 chars → 65px > 53px matrix. At t=0 the text starts just off the right edge.
        var frame = WhiteFrame();
        TextRenderer.DrawBanner(frame.AsSpan(), W, H, "ABCDEFGHIJK", elapsedMs: 0);
        for (int y = 1; y <= 9; y++)
            for (int x = 0; x < W; x++)
                Assert.False(IsLit(frame, x, y), $"band should be empty at t=0, found lit pixel at ({x},{y})");

        // After 5 s at 12 px/s the text has moved 60px left and is partially visible.
        frame = WhiteFrame();
        TextRenderer.DrawBanner(frame.AsSpan(), W, H, "ABCDEFGHIJK", elapsedMs: 5000);
        bool anyLit = false;
        for (int y = 2; y <= 8 && !anyLit; y++)
            for (int x = 0; x < W; x++)
                if (IsLit(frame, x, y)) { anyLit = true; break; }
        Assert.True(anyLit, "marquee text should be visible after scrolling");
    }

    [Fact]
    public void DrawBanner_UnsupportedCharacters_RenderAsGaps()
    {
        var frame = WhiteFrame();
        // 'é' is outside the font range; it must not throw and must not draw anything.
        TextRenderer.DrawBanner(frame.AsSpan(), W, H, "AéB", elapsedMs: 0);

        // 'A' is still drawn at the centered position for "AéB" (17px → x=18).
        // 'A' column 0 is 0x7E → rows 1..6 of the glyph → y=3..8 at x=18.
        Assert.True(IsLit(frame, 18, 3));
        Assert.False(IsLit(frame, 18, 2)); // apex row of column 0 is clear by design
    }

    [Fact]
    public void DrawBanner_EmptyText_DoesNothing()
    {
        var frame = WhiteFrame();
        TextRenderer.DrawBanner(frame.AsSpan(), W, H, "", elapsedMs: 0);
        Assert.True(IsLit(frame, 0, 5)); // frame untouched
    }

    [Fact]
    public void DrawBanner_SmallFrame_Throws()
    {
        var frame = new byte[10];
        Assert.Throws<ArgumentException>(() => TextRenderer.DrawBanner(frame.AsSpan(), W, H, "HI", 0));
    }
}
