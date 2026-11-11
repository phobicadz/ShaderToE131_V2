using ShaderToE131;
using Xunit;

namespace ShaderToE131.Tests;

/// <summary>
/// Matrix 2 has its own shader selection, independent from the main matrix (only the
/// audio capture is shared). These tests pin the WebServer control surface both the
/// HTTP endpoint and the MQTT front-end route through: exact-name matching, the "off"
/// and "mirror" pseudo-shaders, and the separate pending slot the render loop consumes.
/// </summary>
public class Matrix2ShaderSelectTests
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
    public void SelectMatrix2Shader_QueuesItsOwnPendingSlot()
    {
        using var web = NewServer();
        web.RefreshShaderList();
        string name = web.Shaders[0].Name;

        // Queue matrix 2's shader without touching the main matrix's pending slot.
        string? selected = web.SelectMatrix2ShaderByName(name, out string? error);

        Assert.Null(error);
        Assert.Equal(name + ".glsl", selected);
        var pending = web.ConsumePendingMatrix2Shader();
        Assert.NotNull(pending);
        Assert.Equal(name, pending!.FileName);
        Assert.False(string.IsNullOrEmpty(pending.Source));
        Assert.Null(web.ConsumePendingShader()); // the main matrix's slot is untouched
    }

    [Fact]
    public void SelectMatrix2Shader_OffQueuesItsOwnOffSignal()
    {
        using var web = NewServer();

        string? selected = web.SelectMatrix2ShaderByName("off", out string? error);

        Assert.Null(error);
        Assert.Equal("Off (blank)", selected);
        var pending = web.ConsumePendingMatrix2Shader();
        Assert.NotNull(pending);
        Assert.Null(pending!.Source);          // null source is matrix 2's "blank" signal
        Assert.Equal("Off", pending.FileName);
        Assert.Null(web.ConsumePendingShader()); // the main matrix keeps rendering
    }

    [Fact]
    public void SelectMatrix2Shader_MirrorQueuesTheMirrorToken()
    {
        using var web = NewServer();

        string? selected = web.SelectMatrix2ShaderByName("mirror", out string? error);

        Assert.Null(error);
        Assert.Contains("mirror", selected);
        var pending = web.ConsumePendingMatrix2Shader();
        Assert.NotNull(pending);
        Assert.Equal(WebServer.Matrix2MirrorToken, pending!.Source);
        Assert.Equal("mirror", pending.FileName);
    }

    [Fact]
    public void SelectMatrix2Shader_RejectsUnknownName()
    {
        using var web = NewServer();
        web.RefreshShaderList();

        string? selected = web.SelectMatrix2ShaderByName("definitely-not-a-shader", out string? error);

        Assert.Null(selected);
        Assert.Contains("not found", error);
        Assert.Null(web.ConsumePendingMatrix2Shader());
    }

    [Fact]
    public void SelectMatrix2Shader_BuiltInDefaultQueuesBuiltInToken()
    {
        using var web = NewServer();

        string? selected = web.SelectMatrix2ShaderByName("built-in default", out string? error);

        Assert.Null(error);
        Assert.Equal("built-in default", selected);
        Assert.Equal(WebServer.BuiltInShaderToken, web.PendingMatrix2ShaderSource);
        Assert.Equal("built-in default", web.PendingMatrix2ShaderFileName);
    }

    [Fact]
    public void MainAndMatrix2SelectionsAreIndependent()
    {
        using var web = NewServer();
        web.RefreshShaderList();
        string mainName = web.Shaders[0].Name;
        string m2Name = web.Shaders.Count > 1 ? web.Shaders[1].Name : mainName;

        web.SelectShaderByName(mainName, out _);
        web.SelectMatrix2ShaderByName(m2Name, out _);

        var main = web.ConsumePendingShader();
        var m2 = web.ConsumePendingMatrix2Shader();
        Assert.NotNull(main);
        Assert.NotNull(m2);
        Assert.Equal(mainName, main!.FileName);
        Assert.Equal(m2Name, m2!.FileName);
    }
}
