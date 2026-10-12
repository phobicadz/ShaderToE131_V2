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

    /// <summary>A frame filled with a flat mid-gray — a stand-in for shader output.</summary>
    private static byte[] TintFrame(byte level)
    {
        var frame = new byte[W * H * 4];
        for (int i = 0; i < frame.Length; i++) frame[i] = level;
        return frame;
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
        // 'A' column 0 is 0x7E: glyph rows 1..6 are lit and row 0 (the apex) is clear by design.
        // Rows map bottom-up, so the clear apex row is the TOP of the glyph (y=8), not the bottom.
        Assert.True(IsLit(frame, 18, 3));
        Assert.True(IsLit(frame, 18, 2));
        Assert.False(IsLit(frame, 18, 8)); // apex row is clear, and it sits at the top
    }

    [Fact]
    public void DrawBanner_Tilde_RendersTildeGlyphNotArrow()
    {
        var frame = WhiteFrame();
        // "~" is 5px wide → centered at x = (53 - 5) / 2 = 24; glyph rows map to y=2..8.
        TextRenderer.DrawBanner(frame.AsSpan(), W, H, "~", elapsedMs: 0);

        // Tilde columns are 0x08, 0x04, 0x08, 0x10, 0x08: glyph rows 3,2,3,4,3. Frame rows are
        // bottom-up, so glyph row 2 is LOWER on the panel (y=6) and row 4 is higher (y=4).
        Assert.True(IsLit(frame, 24, 5));
        Assert.True(IsLit(frame, 25, 6));
        Assert.True(IsLit(frame, 26, 5));
        Assert.True(IsLit(frame, 27, 4));
        Assert.True(IsLit(frame, 28, 5));

        // No other pixel in the glyph box is lit (in particular none of the arrow-shape rows).
        for (int x = 24; x <= 28; x++)
            for (int y = 2; y <= 8; y++)
            {
                bool expected = (x, y) switch
                {
                    (24, 5) or (25, 6) or (26, 5) or (27, 4) or (28, 5) => true,
                    _ => false,
                };
                Assert.Equal(expected, IsLit(frame, x, y));
            }
    }

    [Fact]
    public void DrawBanner_TextIsUprightOnThePanel_NotUpsideDown()
    {
        // Guard for the orientation bug: the framebuffer is bottom-up (row 0 is the BOTTOM of the
        // panel), so glyph row 0 — the glyph's own top row — must land on the HIGHEST frame index.
        // 'A' column 0 is 0x7E: its top row is the only clear one, so exactly one pixel in that
        // column is unlit and it has to be the top of the glyph box. If the mapping is inverted the
        // text renders upside down and this pixel moves to the bottom.
        var frame = WhiteFrame();
        TextRenderer.DrawBanner(frame.AsSpan(), W, H, "A", elapsedMs: 0);

        int glyphLeft = (W - TextRenderer.MeasureWidth("A")) / 2; // 24
        Assert.False(IsLit(frame, glyphLeft, 8));                  // clear apex at the top
        for (int y = 2; y <= 7; y++)
            Assert.True(IsLit(frame, glyphLeft, y), $"expected the stem lit at ({glyphLeft},{y})");

        // '!' is a second, unmistakable check: its dot is the glyph's bottom row, so if the mapping
        // is inverted the dot ends up on top and the test fails.
        frame = WhiteFrame();
        TextRenderer.DrawBanner(frame.AsSpan(), W, H, "!", elapsedMs: 0);
        int bangX = (W - TextRenderer.MeasureWidth("!")) / 2;
        // '!' column 2 is 0x5F = glyph rows 0..4 and 6 lit, row 5 clear → the stroke occupies the
        // top of the glyph box (y=8..4), the gap is y=3, and the dot (row 6) is at the bottom (y=2).
        Assert.True(IsLit(frame, bangX + 2, 2), "the dot of '!' must be at the bottom of the glyph");
        Assert.True(IsLit(frame, bangX + 2, 8), "the stroke of '!' must reach the top");
        Assert.False(IsLit(frame, bangX + 2, 3), "the gap between stroke and dot must be clear");
    }

    [Fact]
    public void DrawBanner_Overlay_KeepsTheShaderBehindTheText()
    {
        // A mid-gray frame stands in for shader output: overlay mode must not blank the band.
        var frame = TintFrame(100);
        TextRenderer.DrawBanner(frame.AsSpan(), W, H, "A", elapsedMs: 0, overlay: true);

        int glyphLeft = (W - TextRenderer.MeasureWidth("A")) / 2; // 24
        Assert.Equal(255, frame[(7 * W + glyphLeft) * 4]); // glyphs are still white

        // Same band row, nowhere near the glyphs or their shadows: untouched, not cleared.
        Assert.Equal(100, frame[(5 * W + 0) * 4]);
        Assert.Equal(100, frame[(5 * W + 52) * 4]);
        Assert.Equal(100, frame[(1 * W + 0) * 4]);
    }

    [Fact]
    public void DrawBanner_Overlay_DrawsABlackHaloAroundTheGlyphs()
    {
        var frame = TintFrame(100);
        TextRenderer.DrawBanner(frame.AsSpan(), W, H, "A", elapsedMs: 0, overlay: true);

        int glyphLeft = (W - TextRenderer.MeasureWidth("A")) / 2; // 24
        Assert.Equal(255, frame[(7 * W + glyphLeft) * 4]); // the stroke itself is white

        // 'A' column 1 is 0x11 → only glyph rows 0 and 4 are lit, so (25,6) is not a glyph pixel:
        // it is the halo around the stroke at (24,7), and it is solid black on both sides.
        Assert.Equal(0, frame[(6 * W + 25) * 4]);
        Assert.Equal(0, frame[(6 * W + 23) * 4]);

        // Away from the glyphs and their halo the shader is untouched.
        Assert.Equal(100, frame[(5 * W + 0) * 4]);
    }

    [Fact]
    public void DrawBanner_Overlay_TextShadow_WidensTheHalo()
    {
        var frame = TintFrame(100);
        TextRenderer.DrawBanner(frame.AsSpan(), W, H, "A", elapsedMs: 0, overlay: true, shadowSpread: 2);

        int glyphLeft = (W - TextRenderer.MeasureWidth("A")) / 2; // 24
        Assert.Equal(255, frame[(7 * W + glyphLeft) * 4]);

        // Two columns left of the stroke: inside a 2 px halo, outside a 1 px one.
        Assert.Equal(0, frame[(7 * W + glyphLeft - 1) * 4]);
        Assert.Equal(0, frame[(7 * W + glyphLeft - 2) * 4]);
        Assert.Equal(100, frame[(7 * W + glyphLeft - 3) * 4]);
    }

    [Fact]
    public void DrawBanner_Overlay_TextShadowZero_DrawsNoHalo()
    {
        var frame = TintFrame(100);
        TextRenderer.DrawBanner(frame.AsSpan(), W, H, "A", elapsedMs: 0, overlay: true, shadowSpread: 0);

        Assert.Equal(255, frame[(7 * W + 24) * 4]);   // glyphs still drawn
        Assert.Equal(100, frame[(6 * W + 25) * 4]);   // nothing dimmed around them
        Assert.Equal(100, frame[(6 * W + 23) * 4]);
    }

    [Fact]
    public void DrawBanner_WithoutOverlay_BlanksTheBandAsBefore()
    {
        var frame = TintFrame(100);
        TextRenderer.DrawBanner(frame.AsSpan(), W, H, "A", elapsedMs: 0);
        Assert.Equal(0, frame[(5 * W + 0) * 4]); // band cleared when not overlaying
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
