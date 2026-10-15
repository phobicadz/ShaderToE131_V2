using ShaderToE131;
using Xunit;

namespace ShaderToE131.Tests;

/// <summary>
/// Unit tests for <see cref="LedAgentClient.ComputeColor"/> — the reduction of a rendered
/// frame to the single colour streamed to the LED agent.
/// </summary>
public class LedAgentColorTests
{
    // ─── Helpers ───

    /// <summary>Build a 3-byte-per-pixel RGB raster of <paramref name="pixels"/> identical pixels.</summary>
    private static byte[] Raster(int count, byte r, byte g, byte b)
    {
        var buffer = new byte[count * 3];
        for (int i = 0; i < count; i++)
        {
            buffer[i * 3] = r;
            buffer[i * 3 + 1] = g;
            buffer[i * 3 + 2] = b;
        }
        return buffer;
    }

    private static int Color(int r, int g, int b) => (r << 16) | (g << 8) | b;

    private static void AssertColor(int expected, ReadOnlySpan<byte> raster, LedAgentMode mode)
        => Assert.Equal(expected, LedAgentClient.ComputeColor(raster, mode));

    // ─── Average mode ───

    [Fact]
    public void Average_SinglePixel_IsThatPixel()
        => AssertColor(Color(12, 34, 56), Raster(1, 12, 34, 56), LedAgentMode.Average);

    [Fact]
    public void Average_MixedPixels_IsTheExactMean()
    {
        // Mean of (200,0,0) and (0,100,0) is (100,50,0) — no scaling applied.
        byte[] raster = [200, 0, 0, 0, 100, 0];
        AssertColor(Color(100, 50, 0), raster, LedAgentMode.Average);
    }

    [Fact]
    public void Average_BlackFrame_IsZero()
        => AssertColor(0, Raster(64, 0, 0, 0), LedAgentMode.Average);

    [Fact]
    public void Average_WhiteFrame_IsWhite()
        => AssertColor(Color(255, 255, 255), Raster(16, 255, 255, 255), LedAgentMode.Average);

    [Fact]
    public void Average_DimFrame_StaysDim()
    {
        // The point of 'average': a dark frame must not be brightened.
        AssertColor(Color(4, 8, 12), Raster(8, 4, 8, 12), LedAgentMode.Average);
    }

    [Fact]
    public void Average_IgnoresTrailingPartialPixel()
    {
        // A buffer that ends mid-pixel must not read past its bounds or count a partial pixel.
        byte[] raster = [10, 20, 30, 99, 99];
        AssertColor(Color(10, 20, 30), raster, LedAgentMode.Average);
    }

    [Fact]
    public void ComputeColor_EmptyRaster_IsZero()
    {
        AssertColor(0, ReadOnlySpan<byte>.Empty, LedAgentMode.Average);
        AssertColor(0, ReadOnlySpan<byte>.Empty, LedAgentMode.Dominant);
    }

    // ─── Dominant mode ───

    [Fact]
    public void Dominant_ScalesBrightestChannelTo255()
    {
        // (100, 50, 0) scaled by 255/100 → (255, 128, 0): same hue, full brightness.
        AssertColor(Color(255, 128, 0), Raster(4, 100, 50, 0), LedAgentMode.Dominant);
    }

    [Fact]
    public void Dominant_PreservesHueOfAMixedFrame()
    {
        // Mean of (200,0,0) and (0,100,0) is (100,50,0) → scaled to (255,128,0).
        byte[] raster = [200, 0, 0, 0, 100, 0];
        AssertColor(Color(255, 128, 0), raster, LedAgentMode.Dominant);
    }

    [Fact]
    public void Dominant_DimFrame_IsBrightenedButKeepsItsHue()
    {
        // (4, 8, 12) scaled by 255/12 → (85, 170, 255).
        AssertColor(Color(85, 170, 255), Raster(8, 4, 8, 12), LedAgentMode.Dominant);
    }

    [Fact]
    public void Dominant_BlackFrame_IsStillZero()
        => AssertColor(0, Raster(64, 0, 0, 0), LedAgentMode.Dominant);

    [Fact]
    public void Dominant_AlreadyFullBrightness_IsUnchanged()
        => AssertColor(Color(255, 0, 128), Raster(4, 255, 0, 128), LedAgentMode.Dominant);

    [Fact]
    public void Dominant_NeverExceeds255OnAnyChannel()
    {
        // Rounding must not push a channel past 255.
        byte[] raster = Raster(3, 1, 1, 3);
        int color = LedAgentClient.ComputeColor(raster, LedAgentMode.Dominant);
        Assert.True(((color >> 16) & 0xFF) <= 255);
        Assert.True(((color >> 8) & 0xFF) <= 255);
        Assert.True((color & 0xFF) <= 255);
        Assert.Equal(Color(85, 85, 255), color);
    }

    // ─── Live client behaviour ───

    [Fact]
    public void UpdateFromFrame_DoesNotThrowWithoutAConnection()
    {
        // The render loop calls this every frame whether or not the agent is reachable.
        using var client = new LedAgentClient("127.0.0.1", 1, LedAgentMode.Dominant);
        client.UpdateFromFrame(Raster(256, 10, 20, 30));
        Assert.False(client.Connected);
        Assert.Equal(0, client.FramesSent);
    }

    [Fact]
    public void DefaultPort_IsNotOpenRgbsSdkPort()
    {
        // 6742 is OpenRGB's; the agent must be able to run alongside it.
        Assert.Equal(6743, LedAgentClient.DefaultPort);
    }
}
