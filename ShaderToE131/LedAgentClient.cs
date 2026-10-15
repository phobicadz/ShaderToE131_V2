using System.Net.Sockets;
using System.Text;

namespace ShaderToE131;

/// <summary>
/// How the frame colour is reduced to a single colour for the PC's own RGB devices.
/// </summary>
public enum LedAgentMode
{
    /// <summary>Mean colour of the frame, exactly as rendered (dim for mostly-dark frames).</summary>
    Average,

    /// <summary>Mean hue of the frame normalised so its brightest channel is full — a readable
    /// ambient mirror of what the matrix is showing.</summary>
    Dominant,
}

/// <summary>
/// Client for the standalone LED agent service (see the <c>LedAgent</c> project).
///
/// The point of the split: this app renders shaders on whatever machine it likes, while the
/// motherboard RGB LEDs live on a different box. The agent runs on the box that has the LEDs
/// and owns the vendor SDK; this client only speaks the agent's line protocol over TCP.
///
/// Protocol (one ASCII command per '\n'-terminated line, default port 6743):
///   greeting <c>LEDAGENT 1 &lt;backend&gt;</c> on connect, then HELLO, RGB, OFF, PING, QUIT.
///
/// All socket work happens on a background thread: the render loop only stores the latest
/// colour in a single volatile slot (newer frames overwrite older ones), so a slow or
/// unreachable agent can never stall or break the E.1.31 output.
/// </summary>
public sealed class LedAgentClient : IDisposable
{
    /// <summary>Default TCP port of the LED agent (deliberately not OpenRGB's 6742).</summary>
    public const int DefaultPort = 6743;

    private const int ConnectTimeoutMs = 3_000;
    private const int ReadTimeoutMs = 5_000;
    private const int ReconnectDelayMs = 10_000;
    /// <summary>Minimum gap between two colour updates. Motherboard LEDs do not need
    /// (and do not tolerate) per-frame updates; 30 Hz is visually smooth.</summary>
    private const int SendIntervalMs = 33;
    /// <summary>How long the link may stay silent before we send a PING to prove it is alive.</summary>
    private const int KeepAliveIdleMs = 15_000;
    private const int MaxReplyLength = 512;

    private readonly string _host;
    private readonly int _port;
    private readonly LedAgentMode _mode;

    private NetworkStream? _stream;
    private Thread? _thread;
    private volatile bool _disposed;
    private volatile bool _connected;
    private string _backendName = "";

    // Single volatile colour slot handed over by the render thread (0x00RRGGBB).
    // A newer frame simply overwrites an older one; nothing is queued.
    private volatile int _pendingColor;
    private int _lastSentColor = -1;
    private long _lastSendMs;
    private long _lastActivityMs;
    private long _lastLogMs;
    private string _lastLogMessage = "";

    public int FramesSent { get; private set; }
    public int SendErrors { get; private set; }
    public bool Connected => _connected;
    public string BackendName => _backendName;

    public LedAgentClient(string host, int port, LedAgentMode mode)
    {
        _host = host;
        _port = port;
        _mode = mode;
    }

    public void Start()
    {
        _thread = new Thread(RunLoop)
        {
            IsBackground = true,
            Name = "LedAgentClient",
        };
        _thread.Start();
    }

    /// <summary>
    /// Reduce an RGB raster (3 bytes per pixel, as produced by <see cref="PixelMapper"/>) to one
    /// colour and hand it to the sender thread. Never blocks, never allocates.
    /// </summary>
    public void UpdateFromFrame(ReadOnlySpan<byte> rgbRaster)
    {
        _pendingColor = ComputeColor(rgbRaster, _mode);
    }

    /// <summary>
    /// Mean colour of an RGB raster, packed as 0x00RRGGBB. In <see cref="LedAgentMode.Dominant"/>
    /// the mean is scaled so its brightest channel reaches 255, preserving the hue while keeping
    /// the LEDs bright enough to read. A black frame yields 0.
    /// </summary>
    public static int ComputeColor(ReadOnlySpan<byte> rgbRaster, LedAgentMode mode)
    {
        long r = 0, g = 0, b = 0;
        int pixels = 0;
        for (int i = 0; i + 2 < rgbRaster.Length; i += 3)
        {
            r += rgbRaster[i];
            g += rgbRaster[i + 1];
            b += rgbRaster[i + 2];
            pixels++;
        }
        if (pixels == 0) return 0;

        int ar = (int)(r / pixels);
        int ag = (int)(g / pixels);
        int ab = (int)(b / pixels);

        if (mode == LedAgentMode.Dominant)
        {
            int max = Math.Max(ar, Math.Max(ag, ab));
            if (max == 0) return 0;
            ar = (int)Math.Round(ar * 255.0 / max);
            ag = (int)Math.Round(ag * 255.0 / max);
            ab = (int)Math.Round(ab * 255.0 / max);
        }

        return (ar << 16) | (ag << 8) | ab;
    }

    // ─── Background thread: connect, handshake, stream colours, reconnect ───

    private void RunLoop()
    {
        while (!_disposed)
        {
            try
            {
                using var tcp = new TcpClient();
                try
                {
                    if (!tcp.ConnectAsync(_host, _port).Wait(ConnectTimeoutMs))
                    {
                        LogThrottled($"[LED agent] Connect timed out ({_host}:{_port}). " +
                            "Start LedAgent on the machine that has the LEDs.");
                        RetryWait();
                        continue;
                    }
                }
                catch (AggregateException ex) when (ex.Flatten().InnerExceptions.All(e => e is SocketException))
                {
                    LogThrottled($"[LED agent] Nothing listening on {_host}:{_port}. " +
                        "Start LedAgent on the machine that has the LEDs " +
                        $"(dotnet run --project LedAgent -- --port {_port}).");
                    RetryWait();
                    continue;
                }

                using var stream = tcp.GetStream();
                stream.ReadTimeout = ReadTimeoutMs;
                _stream = stream;

                if (!Handshake(stream))
                {
                    LogThrottled($"[LED agent] {HostDescription()} answered with something that is not the " +
                        "LED agent protocol. Check the port, or start LedAgent on a free port.");
                    RetryWait();
                    continue;
                }

                _connected = true;
                _lastSentColor = -1;
                _lastActivityMs = Environment.TickCount64;
                Console.WriteLine($"[LED agent] Connected to {HostDescription()} (backend={_backendName}).");

                while (!_disposed)
                {
                    FlushColor(stream);
                    KeepAlive(stream);
                    Thread.Sleep(SendIntervalMs);
                }
            }
            catch (Exception ex) when (!_disposed)
            {
                LogThrottled($"[LED agent] Link lost ({ex.GetType().Name}: {ex.Message}). Retrying.");
            }
            finally
            {
                _connected = false;
                _stream = null;
            }

            RetryWait();
        }
    }

    private string HostDescription() => $"{_host}:{_port}";

    private void RetryWait()
    {
        if (!_disposed) Thread.Sleep(ReconnectDelayMs);
    }

    /// <summary>
    /// Read the server greeting and announce ourselves. Returns false when the peer is not an
    /// LED agent (wrong port, another service), so the caller can back off instead of sending
    /// commands into a socket that will never understand them.
    /// </summary>
    private bool Handshake(NetworkStream stream)
    {
        string greeting = ReadLine(stream);
        if (greeting.Length == 0 || !greeting.StartsWith("LEDAGENT", StringComparison.OrdinalIgnoreCase))
            return false;

        string[] parts = greeting.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        _backendName = parts.Length >= 3 ? parts[2] : "unknown";

        WriteLine(stream, "HELLO ShaderToE131");
        string reply = ReadLine(stream);
        return reply.StartsWith("OK", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Send the pending colour if it differs from the last one sent and the minimum interval
    /// has passed. Identical consecutive frames produce no traffic at all, which matters on a
    /// network link far more than it does on a localhost one.
    /// </summary>
    private void FlushColor(NetworkStream stream)
    {
        int color = _pendingColor;
        long now = Environment.TickCount64;
        if (color == _lastSentColor || now - _lastSendMs < SendIntervalMs) return;

        int r = (color >> 16) & 0xFF, g = (color >> 8) & 0xFF, b = color & 0xFF;
        try
        {
            WriteLine(stream, $"RGB {r} {g} {b}");
            string reply = ReadLine(stream);
            _lastSendMs = now;
            _lastActivityMs = now;
            if (reply.StartsWith("ERR", StringComparison.OrdinalIgnoreCase))
            {
                SendErrors++;
                LogThrottled($"[LED agent] Rejected a colour: {reply}");
                return;
            }
            _lastSentColor = color;
            FramesSent++;
        }
        catch (Exception ex)
        {
            SendErrors++;
            throw new IOException("Colour update failed.", ex);
        }
    }

    /// <summary>
    /// Prove the link is still alive when nothing has been sent for a while — a static scene
    /// sends no colours, and a half-open TCP connection would otherwise go unnoticed.
    /// </summary>
    private void KeepAlive(NetworkStream stream)
    {
        if (Environment.TickCount64 - _lastActivityMs < KeepAliveIdleMs) return;
        WriteLine(stream, "PING");
        string reply = ReadLine(stream);
        if (!reply.Equals("PONG", StringComparison.OrdinalIgnoreCase))
            throw new IOException($"Unexpected reply to PING: '{reply}'");
        _lastActivityMs = Environment.TickCount64;
    }

    // ─── Line framing ───

    private static string ReadLine(NetworkStream stream)
    {
        StringBuilder sb = new(64);
        int b;
        while ((b = stream.ReadByte()) != -1)
        {
            if (b == '\n') return sb.ToString();
            if (b == '\r') continue;
            sb.Append((char)b);
            if (sb.Length > MaxReplyLength)
                throw new IOException($"Reply longer than {MaxReplyLength} bytes.");
        }
        return sb.Length > 0 ? sb.ToString() : "";
    }

    private static void WriteLine(NetworkStream stream, string text)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(text + "\n");
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush();
    }

    private void LogThrottled(string message)
    {
        long now = Environment.TickCount64;
        if (message == _lastLogMessage && now - _lastLogMs < 60_000) return;
        _lastLogMessage = message;
        _lastLogMs = now;
        Console.WriteLine(message);
    }

    public void Dispose()
    {
        _disposed = true;
        try
        {
            NetworkStream? stream = _stream;
            if (_connected && stream != null) WriteLine(stream, "QUIT");
        }
        catch { }
        try { _stream?.Dispose(); } catch { }
        try { _thread?.Join(1_000); } catch { }
    }
}
