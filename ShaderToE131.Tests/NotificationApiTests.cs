using ShaderToE131;
using Xunit;

namespace ShaderToE131.Tests;

/// <summary>
/// Unit tests for <see cref="WebServer.ParseNotifyRequest"/> — validation of
/// POST /api/notify request bodies.
/// </summary>
public class NotificationApiTests
{
    [Fact]
    public void ParseNotifyRequest_TextOnly_DefaultsToTenSeconds()
    {
        var req = WebServer.ParseNotifyRequest("""{"text":"Hello"}""", out var error);
        Assert.Null(error);
        Assert.NotNull(req);
        Assert.Equal("Hello", req.Text);
        Assert.Equal(10, req.DurationSec);
    }

    [Fact]
    public void ParseNotifyRequest_ExplicitDuration_IsHonored()
    {
        var req = WebServer.ParseNotifyRequest("""{"text":"Hi","duration":30}""", out var error);
        Assert.Null(error);
        Assert.NotNull(req);
        Assert.Equal(30, req.DurationSec);
    }

    [Fact]
    public void ParseNotifyRequest_DurationAsNumericString_IsAccepted()
    {
        var req = WebServer.ParseNotifyRequest("""{"text":"Hi","duration":"5"}""", out var error);
        Assert.Null(error);
        Assert.NotNull(req);
        Assert.Equal(5, req.DurationSec);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(300)]
    public void ParseNotifyRequest_DurationAtBounds_IsAccepted(int duration)
    {
        var req = WebServer.ParseNotifyRequest($$"""{"text":"Hi","duration":{{duration}}}""", out var error);
        Assert.Null(error);
        Assert.NotNull(req);
        Assert.Equal(duration, req.DurationSec);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(301)]
    public void ParseNotifyRequest_DurationOutOfRange_IsRejected(int duration)
    {
        var req = WebServer.ParseNotifyRequest($$"""{"text":"Hi","duration":{{duration}}}""", out var error);
        Assert.Null(req);
        Assert.Contains("duration", error);
    }

    [Fact]
    public void ParseNotifyRequest_DurationNotNumeric_IsRejected()
    {
        var req = WebServer.ParseNotifyRequest("""{"text":"Hi","duration":"soon"}""", out var error);
        Assert.Null(req);
        Assert.Contains("duration", error);
    }

    [Fact]
    public void ParseNotifyRequest_EmptyText_IsValidClearRequest()
    {
        var req = WebServer.ParseNotifyRequest("""{"text":""}""", out var error);
        Assert.Null(error);
        Assert.NotNull(req);
        Assert.Equal("", req.Text);
    }

    [Fact]
    public void ParseNotifyRequest_UnicodeText_IsAccepted()
    {
        var req = WebServer.ParseNotifyRequest("""{"text":"Ünïcödé ✓"}""", out var error);
        Assert.Null(error);
        Assert.NotNull(req);
        Assert.Equal("Ünïcödé ✓", req.Text);
    }

    [Fact]
    public void ParseNotifyRequest_TextTooLong_IsRejected()
    {
        string longText = new string('x', TextRenderer.MaxTextLength + 1);
        var req = WebServer.ParseNotifyRequest($$"""{"text":"{{longText}}"}""", out var error);
        Assert.Null(req);
        Assert.Contains(TextRenderer.MaxTextLength.ToString(), error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"duration":10}""")]
    [InlineData("""{"text":42}""")]
    public void ParseNotifyRequest_MissingOrInvalidBody_IsRejected(string body)
    {
        var req = WebServer.ParseNotifyRequest(body, out var error);
        Assert.Null(req);
        Assert.NotNull(error);
    }

    [Fact]
    public void ConsumePendingNotification_ReturnsPendingOnce_ThenNull()
    {
        using var ws = new WebServer(0, Directory.GetCurrentDirectory(), "localhost");

        // Nothing pending yet.
        Assert.Null(ws.ConsumePendingNotification());

        // Simulate the /api/notify handler posting a request.
        var req = WebServer.ParseNotifyRequest("""{"text":"Hello"}""", out _);
        Assert.NotNull(req);
        ws.SetPendingNotificationForTest(req);

        // First consume returns it; the slot is cleared atomically.
        Assert.Same(req, ws.ConsumePendingNotification());
        Assert.Null(ws.ConsumePendingNotification());
    }

    [Fact]
    public void ParseNotifyRequest_OverlayFlag_IsHonored()
    {
        var on = WebServer.ParseNotifyRequest("""{"text":"Hi","overlay":true}""", out var error);
        Assert.NotNull(on);
        Assert.Null(error);
        Assert.True(on.Overlay);

        var off = WebServer.ParseNotifyRequest("""{"text":"Hi","overlay":false}""", out _);
        Assert.NotNull(off);
        Assert.False(off.Overlay);
    }

    [Fact]
    public void ParseNotifyRequest_OverlayAbsent_LeavesTheDecisionToTheServer()
    {
        var req = WebServer.ParseNotifyRequest("""{"text":"Hi"}""", out _);
        Assert.NotNull(req);
        Assert.Null(req.Overlay); // "no opinion", resolved by WebServer.DefaultTextOverlay
    }

    [Theory]
    [InlineData("""{"text":"Hi","overlay":"true"}""", true)]   // templates sometimes stringify it
    [InlineData("""{"text":"Hi","overlay":"false"}""", false)]
    public void ParseNotifyRequest_OverlayAsString_IsAccepted(string body, bool expected)
    {
        var req = WebServer.ParseNotifyRequest(body, out var error);
        Assert.NotNull(req);
        Assert.Null(error);
        Assert.Equal(expected, req.Overlay);
    }

    [Theory]
    [InlineData("""{"text":"Hi","overlay":1}""")]
    [InlineData("""{"text":"Hi","overlay":"yes"}""")]
    [InlineData("""{"text":"Hi","overlay":null}""")]
    public void ParseNotifyRequest_OverlayNotBoolean_IsRejected(string body)
    {
        Assert.Null(WebServer.ParseNotifyRequest(body, out var error));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TryPostNotification_ResolvesDefaultOverlayWhenRequestIsSilent(bool serverDefault)
    {
        using var ws = new WebServer(0, Directory.GetCurrentDirectory(), "localhost")
        {
            DefaultTextOverlay = serverDefault
        };

        Assert.True(ws.TryPostNotification("""{"text":"Hi"}""", out _));
        Assert.Equal(serverDefault, ws.ConsumePendingNotification()!.Overlay);

        // An explicit value in the message always beats the server default.
        Assert.True(ws.TryPostNotification("""{"text":"Hi","overlay":false}""", out _));
        Assert.False(ws.ConsumePendingNotification()!.Overlay);
    }
}
