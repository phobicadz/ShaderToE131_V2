using ShaderToE131;
using Xunit;

namespace ShaderToE131.Tests;

/// <summary>
/// Multi-line text banners: a '\n' in the text is a line break. A 24-row panel (the
/// default --matrix2-size) shows two lines separated by a pixel gap (default 4 blank rows,
/// --text-line-gap); an 11-row panel has room for one band, so text asking for more lines
/// than fit is rendered as a single scrolling line rather than dropping content. Frame rows
/// are bottom-up (row 0 = BOTTOM of the panel), so the first text line must land on the
/// HIGHEST row indices.
/// </summary>
public class TextRendererMultiLineTests
{
    private const int W = 32;   // default --matrix2-size width
    private const int H = 24;   // default --matrix2-size height

    private static byte[] WhiteFrame(int w, int h)
    {
        var frame = new byte[w * h * 4];
        for (int i = 0; i < frame.Length; i++) frame[i] = 255;
        return frame;
    }

    private static bool IsLit(byte[] frame, int w, int x, int y)
    {
        int idx = (y * w + x) * 4;
        return frame[idx] == 255 && frame[idx + 1] == 255 && frame[idx + 2] == 255;
    }

    private static bool FramesEqual(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    [Fact]
    public void DrawBanner_TwoLines_TallPanel_DrawsBothBandsWithTheFirstLineOnTop()
    {
        // "!" is the unmistakable shape: column 2 (0x5F) is lit on glyph rows 0..4 and 6,
        // with a gap on row 5 — so the stroke reaches the top of the glyph box and the
        // dot sits at the bottom. Two identical lines stack with the first one higher,
        // separated by the default 4-row gap.
        var frame = WhiteFrame(W, H);
        TextRenderer.DrawBanner(frame.AsSpan(), W, H, "!\n!", elapsedMs: 0);

        int col = (W - TextRenderer.MeasureWidth("!")) / 2 + 2; // centered "!" → x=15, its lit column

        // Line 1 (first): glyph box tops out at row 20 (block of 18 rows centered in 24).
        Assert.True(IsLit(frame, W, col, 20), "first line stroke must reach its glyph top");
        Assert.False(IsLit(frame, W, col, 15), "the gap inside '!' must be clear (first line)");
        Assert.True(IsLit(frame, W, col, 14), "the dot of the first line's '!' must be at its glyph bottom");

        // Line 2 (second): glyph box 11 rows lower (GlyphHeight + 4-row gap band pitch).
        Assert.True(IsLit(frame, W, col, 9), "second line stroke must reach its glyph top");
        Assert.False(IsLit(frame, W, col, 4), "the gap inside '!' must be clear (second line)");
        Assert.True(IsLit(frame, W, col, 3), "the dot of the second line's '!' must be at its glyph bottom");

        // Exactly the requested gap: the four rows between the two glyph boxes are blank,
        // and the whole block (gap included) is cleared as one continuous band.
        for (int y = 10; y <= 13; y++)
            Assert.False(IsLit(frame, W, col, y), $"row {y} is inside the line gap and must be blank");

        // Rows outside the block keep the underlying frame.
        for (int x = 0; x < W; x++)
        {
            Assert.True(IsLit(frame, W, x, 0), "row 0 should be untouched");
            Assert.True(IsLit(frame, W, x, 1), "row 1 should be untouched");
            Assert.True(IsLit(frame, W, x, 22), "row 22 should be untouched");
            Assert.True(IsLit(frame, W, x, 23), "row 23 should be untouched");
        }
    }

    [Fact]
    public void DrawBanner_LineGapParameter_ChangesTheSpaceBetweenTheLines()
    {
        // lineGap 0 stacks the glyph boxes directly: first line's dot at row 12, second
        // line's glyph top immediately below at row 11.
        var tight = WhiteFrame(W, H);
        TextRenderer.DrawBanner(tight.AsSpan(), W, H, "!\n!", elapsedMs: 0, lineGap: 0);
        int col = (W - TextRenderer.MeasureWidth("!")) / 2 + 2;
        Assert.True(IsLit(tight, W, col, 12), "lineGap 0: first line's dot");
        Assert.True(IsLit(tight, W, col, 11), "lineGap 0: second line starts immediately below");

        // The default gap puts four blank rows between the glyph boxes (dot at 14, next
        // glyph top at 9 → rows 13..10 blank).
        var gap = WhiteFrame(W, H);
        TextRenderer.DrawBanner(gap.AsSpan(), W, H, "!\n!", elapsedMs: 0);
        Assert.True(IsLit(gap, W, col, 14));
        Assert.True(IsLit(gap, W, col, 9));
        for (int y = 10; y <= 13; y++)
            Assert.False(IsLit(gap, W, col, y), $"default gap: row {y} must be blank");
    }

    [Fact]
    public void DrawBanner_TwoLines_SingleBandPanel_JoinsThemIntoOneLine()
    {
        // The 11-row main matrix has room for one band only: two lines become one
        // scrolling line rather than half the message being dropped.
        int mainW = PixelMapper.Width, mainH = PixelMapper.Height;
        var joined = WhiteFrame(mainW, mainH);
        var split = WhiteFrame(mainW, mainH);

        TextRenderer.DrawBanner(joined.AsSpan(), mainW, mainH, "HI THERE YOU", elapsedMs: 0);
        TextRenderer.DrawBanner(split.AsSpan(), mainW, mainH, "HI THERE\nYOU", elapsedMs: 0);

        Assert.True(FramesEqual(joined, split),
            "text too tall for the panel must render exactly as the same text joined on one line");
    }

    [Fact]
    public void DrawBanner_MoreLinesThanThePanelHasRoomFor_JoinAllOfThem()
    {
        var joined = WhiteFrame(W, H);
        var split = WhiteFrame(W, H);

        TextRenderer.DrawBanner(joined.AsSpan(), W, H, "A B C", elapsedMs: 0);
        TextRenderer.DrawBanner(split.AsSpan(), W, H, "A\nB\nC", elapsedMs: 0); // 3 lines > 2 bands

        Assert.True(FramesEqual(joined, split));
    }

    [Fact]
    public void DrawBanner_TrailingNewline_IsNotABlankSecondLine()
    {
        // "HI\n" must look exactly like "HI": a trailing newline does not add an empty band,
        // so the single line stays centered rather than being pushed up by a phantom line.
        var plain = WhiteFrame(W, H);
        var trailing = WhiteFrame(W, H);

        TextRenderer.DrawBanner(plain.AsSpan(), W, H, "HI", elapsedMs: 0);
        TextRenderer.DrawBanner(trailing.AsSpan(), W, H, "HI\n", elapsedMs: 0);

        Assert.True(FramesEqual(plain, trailing));
    }

    [Fact]
    public void DrawBanner_CrlfAndLongSecondLine_DrawsAndScrolls()
    {
        // CRLF (what browsers and MQTT bridges often send) is treated as a line break, and
        // a line too wide for the panel scrolls as a marquee while the short one stays put.
        var frame = WhiteFrame(W, H);
        TextRenderer.DrawBanner(frame.AsSpan(), W, H, "OK\r\nTHE LONGER SECOND LINE SCROLLS", elapsedMs: 0);

        // At t=0 the marquee line starts just off the right edge: its band (rows 3..9)
        // is cleared but has nothing drawn in it yet.
        for (int y = 3; y <= 9; y++)
            for (int x = 0; x < W; x++)
                Assert.False(IsLit(frame, W, x, y), $"second-line band should be empty at t=0, found lit at ({x},{y})");

        // After 5 s at 12 px/s the marquee has moved into view.
        frame = WhiteFrame(W, H);
        TextRenderer.DrawBanner(frame.AsSpan(), W, H, "OK\r\nTHE LONGER SECOND LINE SCROLLS", elapsedMs: 5000);
        bool anyLit = false;
        for (int y = 3; y <= 9 && !anyLit; y++)
            for (int x = 0; x < W; x++)
                if (IsLit(frame, W, x, y)) { anyLit = true; break; }
        Assert.True(anyLit, "the long second line should be visible after scrolling");
    }

    [Fact]
    public void DrawBanner_TwoLines_OverlayMode_KeepsShaderBetweenTheBands()
    {
        var frame = new byte[W * H * 4];
        for (int i = 0; i < frame.Length; i++) frame[i] = 100;

        TextRenderer.DrawBanner(frame.AsSpan(), W, H, "!\n!", elapsedMs: 0, overlay: true);

        Assert.Equal(255, frame[(20 * W + 15) * 4]);  // first line's glyph
        Assert.Equal(255, frame[(9 * W + 15) * 4]);   // second line's glyph
        Assert.Equal(0, frame[(15 * W + 15) * 4]);    // halo, not the shader, inside the gap row of a glyph
        Assert.Equal(100, frame[(22 * W + 0) * 4]);   // outside the block the shader is untouched
        Assert.Equal(100, frame[(1 * W + 0) * 4]);
    }
}
