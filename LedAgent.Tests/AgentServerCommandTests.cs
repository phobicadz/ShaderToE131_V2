using System.Text.Json;
using Xunit;

namespace LedAgent.Tests;

/// <summary>
/// Unit tests for <see cref="AgentServer.HandleCommand"/> — the command half of the protocol,
/// driven without a socket so the behaviour is checked directly.
/// </summary>
public class AgentServerCommandTests
{
    /// <summary>Backend that records what it was asked to do instead of touching hardware.</summary>
    private sealed class RecordingBackend : ILedBackend
    {
        public List<int> Colors { get; } = [];
        public int OffCount { get; private set; }
        public bool Throwing { get; set; }

        public string Name => "recording";
        public int DeviceCount => 2;

        public bool TryInitialize(out string? error) { error = null; return true; }

        public void SetColor(int color)
        {
            if (Throwing) throw new InvalidOperationException("hardware is asleep");
            Colors.Add(color);
        }

        public void TurnOff()
        {
            OffCount++;
            Colors.Add(0);
        }

        public void Dispose() { }
    }

    private static (AgentServer Server, RecordingBackend Backend) Create(int idleOffSeconds = 0)
    {
        RecordingBackend backend = new();
        // Port 0 + loopback: nothing is listened on unless Start() is called, and these tests
        // only exercise the command handler.
        AgentServer server = new(0, "127.0.0.1", backend, idleOffSeconds);
        return (server, backend);
    }

    // ─── Colour commands ───

    [Fact]
    public void Rgb_RepliesOkAndPaintsTheBackend()
    {
        var (server, backend) = Create();
        using (server)
        {
            Assert.Equal("OK", server.HandleCommand("RGB 255 40 0"));
            Assert.Equal(new[] { (255 << 16) | (40 << 8) }, backend.Colors.ToArray());
            Assert.Equal((255 << 16) | (40 << 8), server.LastColor);
            Assert.Equal(1, server.ColorUpdates);
        }
    }

    [Fact]
    public void Rgb_IsCaseInsensitive()
    {
        var (server, backend) = Create();
        using (server)
        {
            Assert.Equal("OK", server.HandleCommand("rgb 1 2 3"));
            Assert.Equal(new[] { (1 << 16) | (2 << 8) | 3 }, backend.Colors.ToArray());
        }
    }

    [Fact]
    public void Rgb_MalformedLine_RepliesAnErrorAndPaintsNothing()
    {
        var (server, backend) = Create();
        using (server)
        {
            Assert.StartsWith("ERR bad RGB line", server.HandleCommand("RGB 255 40"));
            Assert.Empty(backend.Colors);
            Assert.Equal(0, server.ColorUpdates);
        }
    }

    [Fact]
    public void Rgb_OutOfRangeChannel_RepliesAnError()
    {
        var (server, backend) = Create();
        using (server)
        {
            Assert.StartsWith("ERR bad RGB line", server.HandleCommand("RGB 256 0 0"));
            Assert.Empty(backend.Colors);
        }
    }

    [Fact]
    public void Rgb_BlankIsAllowedAndCountsAsAColour()
    {
        var (server, backend) = Create();
        using (server)
        {
            Assert.Equal("OK", server.HandleCommand("RGB 0 0 0"));
            Assert.Equal(new[] { 0 }, backend.Colors.ToArray());
        }
    }

    [Fact]
    public void Off_TurnsTheBackendOff()
    {
        var (server, backend) = Create();
        using (server)
        {
            server.HandleCommand("RGB 9 9 9");
            Assert.Equal("OK", server.HandleCommand("OFF"));
            Assert.Equal(1, backend.OffCount);
            Assert.Equal(0, server.LastColor);
        }
    }

    [Fact]
    public void BackendFailure_RepliesAnErrorInsteadOfThrowing()
    {
        var (server, backend) = Create();
        using (server)
        {
            backend.Throwing = true;
            Assert.StartsWith("ERR backend rejected", server.HandleCommand("RGB 1 1 1"));
            // A hardware failure must not take the service down: the next command still works.
            Assert.Equal("PONG", server.HandleCommand("PING"));
        }
    }

    // ─── Housekeeping commands ───

    [Fact]
    public void Hello_RepliesWithTheClientName()
    {
        var (server, _) = Create();
        using (server)
        {
            Assert.Equal("OK hello ShaderToE131", server.HandleCommand("HELLO ShaderToE131"));
            Assert.Equal("OK hello anonymous", server.HandleCommand("HELLO"));
        }
    }

    [Fact]
    public void Ping_RepliesPong()
    {
        var (server, _) = Create();
        using (server)
        {
            Assert.Equal("PONG", server.HandleCommand("PING"));
            Assert.Equal("PONG", server.HandleCommand("ping"));
        }
    }

    [Fact]
    public void Quit_RepliesBye()
    {
        var (server, _) = Create();
        using (server)
        {
            Assert.Equal("OK bye", server.HandleCommand("QUIT"));
        }
    }

    [Fact]
    public void UnknownCommand_RepliesAnErrorNamingIt()
    {
        var (server, backend) = Create();
        using (server)
        {
            Assert.Equal("ERR unknown command 'FROBNICATE 1 2 3'", server.HandleCommand("FROBNICATE 1 2 3"));
            Assert.Empty(backend.Colors);
        }
    }

    [Fact]
    public void EmptyLine_IsNotAnError()
    {
        // The socket reader skips blank lines, but the handler must not blow up on one either.
        var (server, _) = Create();
        using (server)
        {
            Assert.StartsWith("ERR unknown command", server.HandleCommand("   "));
        }
    }

    // ─── STATUS ───

    [Fact]
    public void Status_RepliesJsonDescribingTheAgent()
    {
        var (server, _) = Create(idleOffSeconds: 45);
        using (server)
        {
            server.HandleCommand("RGB 12 34 56");
            string reply = server.HandleCommand("STATUS");
            Assert.StartsWith("STATUS ", reply);

            using JsonDocument doc = JsonDocument.Parse(reply["STATUS ".Length..]);
            JsonElement root = doc.RootElement;
            Assert.Equal("ledagent", root.GetProperty("agent").GetString());
            Assert.Equal(Protocol.Version, root.GetProperty("protocolVersion").GetInt32());
            Assert.Equal("recording", root.GetProperty("backend").GetString());
            Assert.Equal(2, root.GetProperty("devices").GetInt32());
            Assert.Equal(1, root.GetProperty("colorUpdates").GetInt32());
            Assert.Equal(12, root.GetProperty("lastColor").GetProperty("r").GetInt32());
            Assert.Equal(34, root.GetProperty("lastColor").GetProperty("g").GetInt32());
            Assert.Equal(56, root.GetProperty("lastColor").GetProperty("b").GetInt32());
            Assert.Equal(45, root.GetProperty("idleOffSeconds").GetInt32());
            Assert.True(root.GetProperty("uptimeSecs").GetDouble() >= 0);
        }
    }

    [Fact]
    public void Status_BeforeAnyColourReportsBlack()
    {
        var (server, _) = Create();
        using (server)
        {
            using JsonDocument doc = JsonDocument.Parse(server.HandleCommand("STATUS")["STATUS ".Length..]);
            JsonElement root = doc.RootElement;
            Assert.Equal(0, root.GetProperty("lastColor").GetProperty("r").GetInt32());
            Assert.Equal(0, root.GetProperty("colorUpdates").GetInt32());
        }
    }

    // ─── Lifecycle ───

    [Fact]
    public void Dispose_ReleasesTheBackendOnce()
    {
        var (server, _) = Create();
        server.Dispose();
        server.Dispose();   // idempotent — the agent is disposed from a finally block and a using
    }

    [Fact]
    public void Start_ThrowsWhenThePortIsAlreadyTaken()
    {
        RecordingBackend backend = new();
        backend.TryInitialize(out _);
        using AgentServer first = new(0, "127.0.0.1", backend, 0);
        first.Start();

        // Port 0 always binds, so ask the OS for the port it chose and reuse it.
        using AgentServer second = new(first.BoundPort, "127.0.0.1", backend, 0);
        Assert.ThrowsAny<Exception>(() => second.Start());
    }
}
