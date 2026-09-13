using ShaderToE131;
using Xunit;

namespace ShaderToE131.Tests;

/// <summary>
/// Unit tests for <see cref="PixelMapper.MapRowToString"/> (LED string sampling)
/// and the matrix mapping it builds on.
/// </summary>
public class PixelMapperStringTests
{
    // Deterministic framebuffer: pixel (x, y) = R=x, G=y×4, B=200−x, A=255.
    private static byte[] BuildFrameBuffer()
    {
        var fb = new byte[PixelMapper.Width * PixelMapper.Height * 4];
        for (int y = 0; y < PixelMapper.Height; y++)
        {
            for (int x = 0; x < PixelMapper.Width; x++)
            {
                int i = (y * PixelMapper.Width + x) * 4;
                fb[i] = (byte)x;
                fb[i + 1] = (byte)(y * 4);
                fb[i + 2] = (byte)(200 - x);
                fb[i + 3] = 255;
            }
        }
        return fb;
    }

    private static void AssertRowMapping(byte[] fb, int row, int ledCount)
    {
        var e131 = new byte[ledCount * 3];
        PixelMapper.MapRowToString(fb.AsSpan(), row, ledCount, e131.AsSpan());
        for (int i = 0; i < ledCount; i++)
        {
            int x = i * PixelMapper.Width / ledCount; // nearest-neighbor sample column
            Assert.Equal((byte)x, e131[i * 3]);
            Assert.Equal((byte)(row * 4), e131[i * 3 + 1]);
            Assert.Equal((byte)(200 - x), e131[i * 3 + 2]);
        }
    }

    [Fact]
    public void MapRowToString_50Leds_SamplesNearestNeighborFromGivenRow()
    {
        AssertRowMapping(BuildFrameBuffer(), 5, 50);
    }

    [Fact]
    public void MapRowToString_60Leds_ReusesRowPixelsWhenLongerThanMatrix()
    {
        AssertRowMapping(BuildFrameBuffer(), 3, 60);
    }

    [Fact]
    public void MapRowToString_512Leds_SamplesFullColumnRange()
    {
        var fb = BuildFrameBuffer();
        var e131 = new byte[512 * 3];
        PixelMapper.MapRowToString(fb.AsSpan(), 0, 512, e131.AsSpan());

        // 512×3 = 1536 channels → 4 universes; first/last LEDs hit the column extremes.
        Assert.Equal((byte)0, e131[0]);              // i=0 → x=0
        Assert.Equal((byte)52, e131[511 * 3]);      // i=511 → x=511×53/512=52
        Assert.Equal((byte)(200 - 52), e131[511 * 3 + 2]);
    }

    [Fact]
    public void MapRowToString_InvalidRow_FallsBackToCenterRow()
    {
        var fb = BuildFrameBuffer();
        var center = new byte[50 * 3];
        PixelMapper.MapRowToString(fb.AsSpan(), PixelMapper.Height / 2, 50, center.AsSpan());

        foreach (int badRow in new[] { -1, -100, PixelMapper.Height, 999 })
        {
            var outBuf = new byte[50 * 3];
            PixelMapper.MapRowToString(fb.AsSpan(), badRow, 50, outBuf.AsSpan());
            Assert.Equal(center, outBuf);
        }
    }

    [Fact]
    public void MapRowToString_PreservesRgbChannelOrder()
    {
        var fb = BuildFrameBuffer();
        var e131 = new byte[3];
        PixelMapper.MapRowToString(fb.AsSpan(), 0, 1, e131.AsSpan());

        Assert.Equal((byte)0, e131[0]);     // R of pixel (0,0)
        Assert.Equal((byte)0, e131[1]);     // G of pixel (0,0)
        Assert.Equal((byte)200, e131[2]);   // B of pixel (0,0)
    }

    [Fact]
    public void MatrixMapping_Regression_LedIndexAndChannels()
    {
        Assert.Equal(0, PixelMapper.ToLedIndex(0, 0));
        Assert.Equal(PixelMapper.TotalPixels - 1, PixelMapper.ToLedIndex(PixelMapper.Width - 1, PixelMapper.Height - 1));
        Assert.Equal(-1, PixelMapper.ToLedIndex(-1, 0));
        Assert.Equal(-1, PixelMapper.ToLedIndex(PixelMapper.Width, 0));
        Assert.Equal(-1, PixelMapper.ToLedIndex(0, PixelMapper.Height));

        var (r, g, b) = PixelMapper.ToRgbChannels(10, 4);
        int idx = 4 * PixelMapper.Width + 10;
        Assert.Equal(idx * 3, r);
        Assert.Equal(idx * 3 + 1, g);
        Assert.Equal(idx * 3 + 2, b);
    }

    [Fact]
    public void MatrixMapping_Regression_MapFrameCopiesRgbInChannelOrder()
    {
        var fb = BuildFrameBuffer();
        var e131 = new byte[PixelMapper.TotalChannels];
        PixelMapper.MapFrame(fb.AsSpan(), e131.AsSpan());

        for (int y = 0; y < PixelMapper.Height; y++)
        {
            for (int x = 0; x < PixelMapper.Width; x++)
            {
                var (r, g, b) = PixelMapper.ToRgbChannels(x, y);
                Assert.Equal((byte)x, e131[r]);
                Assert.Equal((byte)(y * 4), e131[g]);
                Assert.Equal((byte)(200 - x), e131[b]);
            }
        }
    }
}
