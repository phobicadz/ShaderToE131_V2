using ShaderToE131;
using Xunit;

namespace ShaderToE131.Tests;

/// <summary>
/// Unit tests for the second matrix output (--matrix2): the generic framebuffer
/// mapping used by outputs that render natively at their own resolution, and the
/// sACN validation applied to --matrix2-size / --matrix2-universe before any
/// buffer is allocated or sent.
/// </summary>
public class Matrix2OutputTests
{
    // Deterministic framebuffer: pixel (x, y) = R=x, G=y, B=200−x, A=255.
    private static byte[] BuildFrameBuffer(int width, int height)
    {
        var fb = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                fb[i] = (byte)x;
                fb[i + 1] = (byte)y;
                fb[i + 2] = (byte)(200 - x);
                fb[i + 3] = 255;
            }
        }
        return fb;
    }

    [Fact]
    public void MapFrame_GenericSize_CopiesRgbInRasterOrder()
    {
        // 32×24 — the default second matrix.
        const int w = 32, h = 24;
        var fb = BuildFrameBuffer(w, h);
        var e131 = new byte[w * h * 3];
        PixelMapper.MapFrame(fb.AsSpan(), w, h, e131.AsSpan());

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 3;
                Assert.Equal((byte)x, e131[i]);
                Assert.Equal((byte)y, e131[i + 1]);
                Assert.Equal((byte)(200 - x), e131[i + 2]);
            }
        }
    }

    [Fact]
    public void MapFrame_ExistingOverload_StillMapsMainMatrix()
    {
        // The 53×11 overload now delegates to the generic one; the layout must not move.
        var fb = BuildFrameBuffer(PixelMapper.Width, PixelMapper.Height);
        var viaGeneric = new byte[PixelMapper.TotalChannels];
        var viaLegacy = new byte[PixelMapper.TotalChannels];
        PixelMapper.MapFrame(fb.AsSpan(), PixelMapper.Width, PixelMapper.Height, viaGeneric.AsSpan());
        PixelMapper.MapFrame(fb.AsSpan(), viaLegacy.AsSpan());
        Assert.Equal(viaLegacy, viaGeneric);
    }

    [Fact]
    public void MapFrame_TruncatedBuffer_MapsExactlyWhatFitsAndNoFurther()
    {
        const int w = 32, h = 24;
        var fb = BuildFrameBuffer(w, h);
        var e131 = new byte[(w * h - 5) * 3]; // room for fewer pixels than the framebuffer has
        PixelMapper.MapFrame(fb.AsSpan(), w, h, e131.AsSpan());

        int mapped = e131.Length / 3;
        // First mapped pixel…
        Assert.Equal(0, e131[0]);
        Assert.Equal(0, e131[1]);
        Assert.Equal(200, e131[2]);
        // …and the last one is exactly the framebuffer's (mapped−1)-th pixel,
        // proving the loop stopped at the buffer's capacity, not the framebuffer's.
        int last = (mapped - 1) * 3;
        Assert.Equal((byte)((mapped - 1) % w), e131[last]);
        Assert.Equal((byte)((mapped - 1) / w), e131[last + 1]);
        Assert.Equal((byte)(200 - (mapped - 1) % w), e131[last + 2]);
    }

    [Fact]
    public void Validate_DefaultMatrix2Layout_AfterMatrixAndString_IsValid()
    {
        // 32×24 = 768 LEDs → 2304 channels → 5 universes.
        // Auto base: matrix (universe 1 + 4 universes) + string (50 LEDs = 1 universe) = 6.
        int matrixUniverses = (PixelMapper.TotalChannels + 509) / 510; // 4
        int baseUniverse = 1 + matrixUniverses + (int)StringOutput.UniversesRequired(50);
        Assert.Equal(6, baseUniverse);
        Assert.Null(StringOutput.Validate(32 * 24, baseUniverse, "--matrix2-size", "--matrix2-universe"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_InvalidMatrix2Size_ReportsMatrix2Flag(int ledCount)
    {
        string? error = StringOutput.Validate(ledCount, 5, "--matrix2-size", "--matrix2-universe");
        Assert.Contains("--matrix2-size", error);
        Assert.DoesNotContain("--string-size", error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_InvalidMatrix2Universe_ReportsMatrix2Flag(int baseUniverse)
    {
        string? error = StringOutput.Validate(32 * 24, baseUniverse, "--matrix2-size", "--matrix2-universe");
        Assert.Contains("--matrix2-universe", error);
        Assert.DoesNotContain("--string-universe", error);
    }

    [Fact]
    public void Validate_Matrix2BeyondUniverseCapacity_Fails()
    {
        // 768 LEDs needs 5 universes; starting at 63998 the last one overflows ushort.
        string? error = StringOutput.Validate(32 * 24, 63998, "--matrix2-size", "--matrix2-universe");
        Assert.NotNull(error);
        Assert.Contains("--matrix2-size", error);
    }

    [Fact]
    public void Validate_StringErrors_StillReportStringFlags()
    {
        // The generalized overload must not change what the string path reports.
        Assert.Contains("--string-size", StringOutput.Validate(0, 5));
        Assert.Contains("--string-universe", StringOutput.Validate(50, 0));
    }
}
