using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace LedAgent;

/// <summary>
/// The standalone service. Accepts the line protocol on a TCP port and forwards every
/// colour to the configured <see cref="ILedBackend"/>.
///
/// Several senders may connect at once (ShaderToE131, a script, a manual test); the last
/// colour received wins, which is what you want when one controller is driving the machine.
/// Nothing here knows what a motherboard is — that is the backend's job.
/// </summary>
public sealed class AgentServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly ILedBackend _backend;
    private readonly int _idleOffSeconds;
    private readonly DateTime _startedAt = DateTime.UtcNow;

    private long _lastCommandMs = Environment.TickCount64;
    private volatile int _lastColor = -1;
    private volatile int _colorUpdates;
    private volatile int _connections;
    private volatile int _activeClients;
    private volatile bool _disposed;

    public int Port { get; }
    public ILedBackend Backend => _backend;
    public int ColorUpdates => _colorUpdates;
    public int Connections => _connections;
    public int ActiveClients => _activeClients;
    public int LastColor => _lastColor;

    public AgentServer(int port, string bindAddress, ILedBackend backend, int idleOffSeconds)
    {
        Port = port;
        _backend = backend;
        _idleOffSeconds = idleOffSeconds;

        IPAddress bind = bindAddress.Equals("*", StringComparison.OrdinalIgnoreCase)
            || bindAddress.Equals("0.0.0.0", StringComparison.OrdinalIgnoreCase)
            ? IPAddress.Any
            : ResolveBind(bindAddress);
        _listener = new TcpListener(bind, port);
    }

    private static IPAddress ResolveBind(string bindAddress)
    {
        if (IPAddress.TryParse(bindAddress, out IPAddress? parsed)) return parsed;
        // A hostname is a convenience: the agent usually runs on a fixed box people address by name.
        IPAddress[] addresses = Dns.GetHostAddresses(bindAddress);
        return Array.Find(addresses, a => a.AddressFamily == AddressFamily.InterNetwork)
            ?? throw new InvalidOperationException($"Cannot resolve bind address '{bindAddress}'.");
    }

    /// <summary>Start accepting. Throws if the port is taken — the agent is a single-owner service.</summary>
    public void Start()
    {
        _listener.Start();
        Console.WriteLine($"LED agent listening on {((IPEndPoint)_listener.LocalEndpoint).Address}:{Port} " +
            $"(backend={_backend.Name}, devices={_backend.DeviceCount}" +
            (_idleOffSeconds > 0 ? $", idle-off={_idleOffSeconds}s" : ", idle-off=off") + ").");

        Thread acceptThread = new(AcceptLoop) { IsBackground = true, Name = "LedAgent.Accept" };
        acceptThread.Start();

        if (_idleOffSeconds > 0)
        {
            Thread idleThread = new(IdleLoop) { IsBackground = true, Name = "LedAgent.Idle" };
            idleThread.Start();
        }
    }

    /// <summary>The port actually bound (useful when 0 was requested).</summary>
    public int BoundPort => ((IPEndPoint)_listener.LocalEndpoint).Port;

    private void AcceptLoop()
    {
        while (!_disposed)
        {
            TcpClient client;
            try
            {
                client = _listener.AcceptTcpClient();
            }
            catch (SocketException) when (_disposed)
            {
                return;   // listener closed during shutdown
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            _connections++;
            _activeClients++;
            Thread handler = new(() => HandleClient(client)) { IsBackground = true, Name = "LedAgent.Client" };
            handler.Start();
        }
    }

    private void HandleClient(TcpClient client)
    {
        string remote = client.Client?.RemoteEndPoint?.ToString() ?? "?";
        try
        {
            using (client)
            {
                client.NoDelay = true;
                NetworkStream stream = client.GetStream();
                Console.WriteLine($"[net] client connected: {remote}");

                WriteLine(stream, Protocol.Greeting(_backend.Name));

                while (!_disposed)
                {
                    string? line = ReadLine(stream);
                    if (line == null) break;          // peer closed
                    if (line.Length == 0) continue;

                    string reply = HandleCommand(line);
                    if (reply.Length > 0) WriteLine(stream, reply);
                    if (reply == "OK bye") break;
                }
            }
        }
        catch (IOException)
        {
            // Client vanished mid-line; nothing to report.
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[net] client {remote} failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _activeClients--;
            Console.WriteLine($"[net] client disconnected: {remote}");
        }
    }

    /// <summary>
    /// Execute one protocol line and return the reply to send back ("" for no reply).
    /// Public so the tests can drive the protocol without a socket.
    /// </summary>
    public string HandleCommand(string line)
    {
        _lastCommandMs = Environment.TickCount64;

        string trimmed = line.Trim();
        string[] parts = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        string verb = parts.Length > 0 ? parts[0].ToUpperInvariant() : "";
        string rest = parts.Length > 1 ? parts[1] : "";

        switch (verb)
        {
            case "HELLO":
                return Protocol.OkWith("hello " + (rest.Length > 0 ? rest : "anonymous"));

            case "RGB":
                if (!Protocol.TryParseRgb(rest, out int color, out string? error))
                    return Protocol.Error(error!);
                return ApplyColor(color);

            case "OFF":
                _lastColor = 0;
                _colorUpdates++;
                try { _backend.TurnOff(); }
                catch (Exception ex) { return Protocol.Error($"backend rejected OFF: {ex.Message}"); }
                return Protocol.Ok;

            case "STATUS":
                return Protocol.StatusLine(BuildStatusJson());

            case "PING":
                return Protocol.Ping;

            case "QUIT":
                return Protocol.OkWith("bye");

            default:
                return Protocol.Error($"unknown command '{trimmed}'");
        }
    }

    private long _lastLogMs;
    private string _lastLogMessage = "";

    /// <summary>
    /// Paint the backend and report whether it accepted the colour. A hardware failure is
    /// returned as an error line rather than thrown: one bad frame must not take the service
    /// down, but the sender is entitled to know the colour never landed.
    /// </summary>
    private string ApplyColor(int color)
    {
        _lastColor = color;
        _colorUpdates++;
        try
        {
            _backend.SetColor(color);
            return Protocol.Ok;
        }
        catch (Exception ex)
        {
            LogThrottled($"[backend] {_backend.Name} rejected a colour: {ex.Message}");
            return Protocol.Error($"backend rejected the colour: {ex.Message}");
        }
    }

    private void LogThrottled(string message)
    {
        long now = Environment.TickCount64;
        if (message == _lastLogMessage && now - _lastLogMs < 60_000) return;
        _lastLogMessage = message;
        _lastLogMs = now;
        Console.WriteLine(message);
    }

    /// <summary>
    /// If no sender has been heard from for <c>--idle-off</c> seconds, blank the LEDs.
    /// Without this the machine stays lit at whatever the last frame was after ShaderToE131 stops.
    /// </summary>
    private void IdleLoop()
    {
        while (!_disposed)
        {
            Thread.Sleep(1000);
            if (_lastColor == 0) continue;
            if (Environment.TickCount64 - _lastCommandMs < _idleOffSeconds * 1000L) continue;

            _lastColor = 0;
            try { _backend.TurnOff(); } catch { }
            Console.WriteLine($"[idle] No sender for {_idleOffSeconds}s — LEDs turned off.");
        }
    }

    private string BuildStatusJson()
    {
        Protocol.Unpack(Math.Max(_lastColor, 0), out byte r, out byte g, out byte b);        var status = new
        {
            agent = "ledagent",
            protocolVersion = Protocol.Version,
            backend = _backend.Name,
            devices = _backend.DeviceCount,
            port = BoundPort,
            uptimeSecs = Math.Round((DateTime.UtcNow - _startedAt).TotalSeconds, 3),
            connections = _connections,
            activeClients = _activeClients,
            colorUpdates = _colorUpdates,
            lastColor = new { r, g, b },
            idleOffSeconds = _idleOffSeconds,
        };
        return JsonSerializer.Serialize(status, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    // ─── Line framing ───

    private static string? ReadLine(NetworkStream stream)
    {
        StringBuilder sb = new(64);
        int b;
        while ((b = stream.ReadByte()) != -1)
        {
            if (b == '\n') return sb.ToString();
            if (b == '\r') continue;
            sb.Append((char)b);
            if (sb.Length > Protocol.MaxLineLength)
            {
                // A runaway line means the peer is not speaking our protocol; drop the connection
                // rather than growing a buffer forever.
                throw new IOException($"Line longer than {Protocol.MaxLineLength} bytes.");
            }
        }
        return sb.Length > 0 ? sb.ToString() : null;
    }

    private static void WriteLine(NetworkStream stream, string text)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(text + "\n");
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _listener.Stop(); } catch { }
        try { _backend.Dispose(); } catch { }
    }
}
