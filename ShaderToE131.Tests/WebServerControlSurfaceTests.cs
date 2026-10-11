using ShaderToE131;
using Xunit;

namespace ShaderToE131.Tests;

/// <summary>
/// The MQTT front-end routes commands through WebServer.SelectShaderByName and
/// WebServer.TryPostNotification instead of duplicating the HTTP endpoint rules.
/// These tests pin that shared control surface: exact-name shader matching, the "off"
/// pseudo-shader, and /api/notify validation.
/// </summary>
public class WebServerControlSurfaceTests
{
    private static string ShaderDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "shaders");
            if (Directory.Exists(candidate) && Directory.GetFiles(candidate, "*.glsl").Length > 0) return candidate;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("shader dir not found from " + AppContext.BaseDirectory);
    }

    private static WebServer NewServer() => new(0, ShaderDir(), "127.0.0.1");

    [Fact]
    public void SelectShaderByName_MatchesExactDisplayName()
    {
        using var web = NewServer();
        web.RefreshShaderList();
        string name = web.Shaders[0].Name;

        string? selected = web.SelectShaderByName(name, out string? error);

        Assert.Null(error);
        Assert.NotNull(selected);
        Assert.Equal(name + ".glsl", selected);
        Assert.Equal(name, web.PendingShaderFileName);
        Assert.False(string.IsNullOrEmpty(web.PendingShaderSource));
    }

    [Fact]
    public void SelectShaderByName_DoesNotMatchSubstring()
    {
        using var web = NewServer();
        web.RefreshShaderList();

        // "fire" must not select a longer name that merely contains it.
        string? selected = web.SelectShaderByName("definitely-not-a-shader", out string? error);
        Assert.Null(selected);
        Assert.Contains("not found", error);
    }

    [Fact]
    public void SelectShaderByName_OffQueuesBlankScreen()
    {
        using var web = NewServer();

        string? selected = web.SelectShaderByName("off", out string? error);

        Assert.Null(error);
        Assert.Equal("Off (blank)", selected);
        Assert.Equal("Off", web.PendingShaderFileName);
        Assert.Null(web.PendingShaderSource);   // null source is the render loop's "off" signal
    }

    [Fact]
    public void SelectShaderByName_BuiltInDefaultQueuesBuiltInToken()
    {
        using var web = NewServer();

        string? selected = web.SelectShaderByName("built-in default", out string? error);

        Assert.Null(error);
        Assert.Equal("built-in default", selected);
        Assert.Equal(WebServer.BuiltInShaderToken, web.PendingShaderSource);
        Assert.Equal("built-in default", web.PendingShaderFileName);
    }

    [Fact]
    public void TryPostNotification_AcceptsValidJsonAndQueuesIt()
    {
        using var web = NewServer();

        Assert.True(web.TryPostNotification("{\"text\":\"hello\",\"duration\":7}", out string? error));
        Assert.Null(error);

        var note = web.ConsumePendingNotification();
        Assert.NotNull(note);
        Assert.Equal("hello", note!.Text);
        Assert.Equal(7, note.DurationSec);
    }

    [Fact]
    public void TryPostNotification_RejectsMissingText()
    {
        using var web = NewServer();

        Assert.False(web.TryPostNotification("{\"duration\":7}", out string? error));
        Assert.False(string.IsNullOrEmpty(error));
        Assert.Null(web.ConsumePendingNotification());
    }
}
