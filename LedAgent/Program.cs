using System.Net.Sockets;
using LedAgent;
using LedAgent.Backends;

int port = Protocol.DefaultPort;
string bind = "*";
string backendName = "auto";
string? msiDllPath = null;
int idleOffSeconds = 120;
bool help = false;

for (int i = 0; i < args.Length; i++)
{
    if (args[i] is "--help" or "-h") { help = true; }
    else if (args[i] == "--port" && i + 1 < args.Length && int.TryParse(args[++i], out int p) && p > 0 && p <= 65535)
        port = p;
    else if (args[i] == "--bind" && i + 1 < args.Length)
        bind = args[++i];
    else if (args[i] == "--backend" && i + 1 < args.Length)
        backendName = args[++i].ToLowerInvariant();
    else if (args[i] == "--msi-dll" && i + 1 < args.Length)
        msiDllPath = args[++i];
    else if (args[i] == "--idle-off" && i + 1 < args.Length && int.TryParse(args[++i], out int idle))
        idleOffSeconds = Math.Max(0, idle);
    else
        Console.WriteLine($"[WARN] Ignoring unknown argument '{args[i]}'.");
}

if (help)
{
    Console.WriteLine($"""
        LedAgent — standalone RGB LED agent for ShaderToE131.

        Runs on the machine whose motherboard LEDs you want to drive. ShaderToE131 connects to
        it over TCP and streams one colour per frame; the agent hands that colour to the LED
        hardware backend installed on this machine.

        Usage:
          LedAgent [--backend auto|msi|mock] [--port {Protocol.DefaultPort}] [--bind *]
                   [--msi-dll C:\\path\\MSLight.dll] [--idle-off 120]

        Options:
          --backend <name>   'auto' (default) picks the first backend that works on this
                             machine, 'msi' forces MSI Mystic Light, 'mock' drives no hardware
                             and logs colours.
          --port <port>       TCP port to listen on (default {Protocol.DefaultPort}).
          --bind <addr>       Bind address: '*' (default, all interfaces), '0.0.0.0', or a
                             specific IP/hostname. Use a specific IP to keep it on one LAN.
          --msi-dll <path>    Path to MSLight.dll when auto-detection fails.
          --idle-off <secs>   Blank the LEDs after this many seconds with no sender
                              (default 120, 0 disables).

        Note: MSI's MSLight.dll is 32-bit. To drive MSI motherboard LEDs, build and publish
        this agent as x86:  dotnet publish LedAgent -a x86

        Protocol (TCP, one line per command, '\n' terminated):
          HELLO <name>     announce a client      -> OK hello <name>
          RGB <r> <g> <b>  set every LED          -> OK
          OFF              blank the LEDs          -> OK
          STATUS           agent state as JSON     -> STATUS <json>
          PING             keep-alive              -> PONG
          QUIT             close                   -> OK bye

        Try it without ShaderToE131:
          curl -s telnet://127.0.0.1:{Protocol.DefaultPort}
          "RGB 255 40 0" | Set-NetSocketToText ...   (or any TCP client)
        """);
    return 0;
}

// Pick a backend. 'auto' tries the real hardware first and falls back to the mock, so the
// service always starts and always says why it chose what it chose.
ILedBackend backend = SelectBackend(backendName, msiDllPath, out bool usable);
if (!usable)
{
    Console.WriteLine("[ERROR] No LED backend is usable on this machine.");
    return 1;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

using var server = new AgentServer(port, bind, backend, idleOffSeconds);
try
{
    server.Start();
}
catch (SocketException ex)
{
    Console.WriteLine($"[ERROR] Cannot listen on {bind}:{port} — {ex.Message} " +
        $"(port in use, or '{bind}' is not a local address).");
    return 1;
}

Console.WriteLine("Press Ctrl+C to stop.");
if (!Console.IsInputRedirected)
{
    // Local convenience: typing "rgb 255 0 0" here sends it through the same command handler
    // the network clients use, so the agent can be tested without a TCP client.
    _ = Task.Run(() =>
    {
        string? line;
        while (!cts.IsCancellationRequested && (line = Console.ReadLine()) != null)
        {
            if (line.Trim().Length == 0) continue;
            string reply = server.HandleCommand(line);
            if (reply.Length > 0) Console.WriteLine(reply);
        }
    });
}

cts.Token.WaitHandle.WaitOne();
Console.WriteLine("Stopping.");
return 0;

static ILedBackend SelectBackend(string name, string? msiDllPath, out bool usable)
{
    usable = true;

    if (name is "mock")
        return new MockLedBackend();

    if (name is "msi" or "auto")
    {
        MsiMysticLightBackend msi = new(msiDllPath);
        if (msi.TryInitialize(out string? error))
            return msi;

        if (name == "msi")
        {
            Console.WriteLine($"[ERROR] MSI backend unavailable: {error}");
            usable = false;
            return new UnavailableBackend(error!);
        }

        Console.WriteLine($"[auto] MSI Mystic Light unavailable ({error}).");
    }
    else
    {
        Console.WriteLine($"[WARN] Unknown --backend '{name}'.");
    }

    MockLedBackend mock = new();
    mock.TryInitialize(out _);
    return mock;
}

/// <summary>Placeholder backend used when a forced backend cannot start.</summary>
sealed class UnavailableBackend : ILedBackend
{
    private readonly string _reason;
    public UnavailableBackend(string reason) => _reason = reason;
    public string Name => "unavailable";
    public int DeviceCount => 0;
    public bool TryInitialize(out string? error) { error = _reason; return false; }
    public void SetColor(int color) => throw new InvalidOperationException(_reason);
    public void TurnOff() { }
    public void Dispose() { }
}
