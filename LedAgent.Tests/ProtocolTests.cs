using LedAgent;
using Xunit;

namespace LedAgent.Tests;

/// <summary>
/// Unit tests for <see cref="Protocol"/> — the line format spoken between ShaderToE131 (or any
/// other sender) and the LED agent.
/// </summary>
public class ProtocolTests
{
    private static void ParseOk(string line, int expectedColor)
    {
        Assert.True(Protocol.TryParseRgb(line, out int color, out string? error));
        Assert.Null(error);
        Assert.Equal(expectedColor, color);
    }

    private static void ParseFail(string line)
    {
        Assert.False(Protocol.TryParseRgb(line, out int color, out string? error));
        Assert.NotNull(error);
        Assert.Equal(0, color);
    }

    // ─── Parsing ───

    [Fact]
    public void TryParseRgb_ArgumentsOnly_IsAccepted()
        => ParseOk("255 40 0", (255 << 16) | (40 << 8));

    [Fact]
    public void TryParseRgb_WholeLineIsAlsoAccepted()
        => ParseOk("RGB 255 40 0", (255 << 16) | (40 << 8));

    [Fact]
    public void TryParseRgb_PacksChannelsIntoRgb()
        => ParseOk("1 2 3", (1 << 16) | (2 << 8) | 3);

    [Fact]
    public void TryParseRgb_BlackIsZero()
        => ParseOk("0 0 0", 0);

    [Fact]
    public void TryParseRgb_WhiteIsMax()
        => ParseOk("255 255 255", 0x00FFFFFF);

    [Fact]
    public void TryParseRgb_ExtraWhitespaceBetweenChannels_IsTolerated()
        => ParseOk("10   20  30", (10 << 16) | (20 << 8) | 30);

    [Theory]
    [InlineData("")]
    [InlineData("255")]
    [InlineData("255 40")]
    [InlineData("255 40 0 7")]                 // too many channels
    [InlineData("RGB 255 40")]
    [InlineData("255 40 0 extra")]
    [InlineData("r g b")]
    [InlineData("255 -1 0")]
    public void TryParseRgb_MalformedLines_AreRejected(string line)
        => ParseFail(line);

    [Theory]
    [InlineData("256 0 0")]                    // out of byte range
    [InlineData("0 0 999")]
    [InlineData("-5 0 0")]
    [InlineData("255 4o 0")]                   // not a number
    [InlineData("255 40.5 0")]
    public void TryParseRgb_OutOfRangeOrNonNumericChannels_AreRejected(string line)
        => ParseFail(line);

    [Fact]
    public void TryParseRgb_ErrorExplainsWhatWasExpected()
    {
        Assert.False(Protocol.TryParseRgb("255 40", out _, out string? error));
        Assert.Contains("expected 'RGB r g b'", error);
    }

    // ─── Round-tripping ───

    [Theory]
    [InlineData(0)]
    [InlineData(0x00FFFFFF)]
    [InlineData(0x00123456)]
    [InlineData(0x00FF0000)]
    public void Unpack_InvertsTryParseRgb(int color)
    {
        Protocol.Unpack(color, out byte r, out byte g, out byte b);
        Assert.Equal((color >> 16) & 0xFF, r);
        Assert.Equal((color >> 8) & 0xFF, g);
        Assert.Equal(color & 0xFF, b);
        Assert.True(Protocol.TryParseRgb($"{r} {g} {b}", out int reparsed, out _));
        Assert.Equal(color, reparsed);
    }

    // ─── Replies and framing ───

    [Fact]
    public void Greeting_AdvertisesProtocolVersionAndBackend()
        => Assert.Equal("LEDAGENT 1 msi-mystic-light", Protocol.Greeting("msi-mystic-light"));

    [Fact]
    public void DefaultPort_IsNotOpenRgbsSdkPort()
    {
        // The agent must be able to run on the same machine as OpenRGB.
        Assert.Equal(6743, Protocol.DefaultPort);
    }

    [Fact]
    public void ReplyHelpers_HaveTheDocumentedPrefixes()
    {
        Assert.Equal("OK", Protocol.Ok);
        Assert.Equal("PONG", Protocol.Ping);
        Assert.Equal("ERR nope", Protocol.Error("nope"));
        Assert.Equal("OK hello ShaderToE131", Protocol.OkWith("hello ShaderToE131"));
        Assert.Equal("STATUS {}", Protocol.StatusLine("{}"));
    }

    [Fact]
    public void MaxLineLength_LeavesRoomForAStatusReply()
    {
        // A STATUS line carries a JSON body; the frame limit must not clip it.
        Assert.True(Protocol.MaxLineLength >= 256);
    }
}
