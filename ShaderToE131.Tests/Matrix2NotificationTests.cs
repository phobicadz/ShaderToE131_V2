using ShaderToE131;
using Xunit;

namespace ShaderToE131.Tests;

/// <summary>
/// Text banners can name a target: the main matrix (the default, unchanged behaviour),
/// matrix 2 only, or both. These tests pin the parsing of the "target" field and the
/// routing of a posted notification into the main and/or matrix-2 pending slots —
/// the surface shared by the HTTP endpoint, the web UI and the MQTT text entities.
/// </summary>
public class Matrix2NotificationTests
{
    private static WebServer NewServer() => new(0, Directory.GetCurrentDirectory(), "localhost");

    [Fact]
    public void ParseNotifyRequest_TargetAbsent_DefaultsToMainMatrix()
    {
        var req = WebServer.ParseNotifyRequest("""{"text":"Hello"}""", out var error);
        Assert.Null(error);
        Assert.NotNull(req);
        Assert.Null(req.Target); // "no opinion" — TryPostNotification routes it to the main matrix
    }

    [Theory]
    [InlineData("""{"text":"Hi","target":"matrix2"}""", "matrix2")]
    [InlineData("""{"text":"Hi","target":"panel"}""", "matrix2")]
    [InlineData("""{"text":"Hi","target":"Matrix 2"}""", null)]   // not an accepted alias
    [InlineData("""{"text":"Hi","target":"both"}""", "both")]
    [InlineData("""{"text":"Hi","target":"main"}""", "matrix")]
    public void ParseNotifyRequest_Target_IsNormalizedOrRejected(string body, string? expected)
    {
        var req = WebServer.ParseNotifyRequest(body, out var error);
        if (expected == null)
        {
            Assert.Null(req);
            Assert.Contains("target", error);
            return;
        }
        Assert.NotNull(req);
        Assert.Null(error);
        Assert.Equal(expected, req.Target);
    }

    [Theory]
    [InlineData("""{"text":"Hi","target":42}""")]
    [InlineData("""{"text":"Hi","target":"screen"}""")]
    public void ParseNotifyRequest_TargetInvalid_IsRejected(string body)
    {
        Assert.Null(WebServer.ParseNotifyRequest(body, out var error));
        Assert.Contains("target", error);
    }

    [Fact]
    public void TryPostNotification_Default_QueuesOnlyTheMainMatrix()
    {
        using var web = NewServer();

        Assert.True(web.TryPostNotification("""{"text":"Hello"}""", out _));

        Assert.NotNull(web.ConsumePendingNotification());
        Assert.Null(web.ConsumePendingMatrix2Notification());
    }

    [Fact]
    public void TryPostNotification_TargetMatrix2_QueuesOnlyMatrix2()
    {
        using var web = NewServer();

        Assert.True(web.TryPostNotification("""{"text":"Panel hello","target":"matrix2","duration":7,"overlay":true}""", out _));

        Assert.Null(web.ConsumePendingNotification()); // the main matrix is untouched
        var m2 = web.ConsumePendingMatrix2Notification();
        Assert.NotNull(m2);
        Assert.Equal("Panel hello", m2!.Text);
        Assert.Equal(7, m2.DurationSec);
        Assert.True(m2.Overlay);
        Assert.Equal("matrix2", m2.Target);
    }

    [Fact]
    public void TryPostNotification_TargetBoth_QueuesBothSlots()
    {
        using var web = NewServer();

        Assert.True(web.TryPostNotification("""{"text":"Dinner","target":"both"}""", out _));

        var main = web.ConsumePendingNotification();
        var m2 = web.ConsumePendingMatrix2Notification();
        Assert.NotNull(main);
        Assert.NotNull(m2);
        Assert.Equal("Dinner", main!.Text);
        Assert.Equal("Dinner", m2!.Text); // each display gets its own copy and lifetime
    }

    [Fact]
    public void TryPostNotification_ResolvesDefaultOverlayForMatrix2Too()
    {
        using var web = NewServer();
        web.DefaultTextOverlay = true;

        Assert.True(web.TryPostNotification("""{"text":"Hi","target":"matrix2"}""", out _));
        var m2 = web.ConsumePendingMatrix2Notification();
        Assert.NotNull(m2);
        Assert.True(m2!.Overlay); // the server default applies to matrix 2 as well
    }
}
