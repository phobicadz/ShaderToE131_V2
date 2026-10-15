namespace LedAgent;

/// <summary>
/// The line protocol spoken between ShaderToE131 (or any other sender) and this agent.
///
/// Transport: TCP, one command per line, '\n' terminated, UTF-8, ASCII only.
/// The server greets every accepted connection with <see cref="GreetingPrefix"/>.
///
/// Commands (case-insensitive):
///   HELLO &lt;name&gt;   announce a client            → OK hello &lt;name&gt;
///   RGB &lt;r&gt; &lt;g&gt; &lt;b&gt;  set every LED to a colour   → OK
///   OFF             turn every LED off            → OK
///   STATUS          ask for the agent state       → STATUS {json}
///   PING            keep-alive                    → PONG
///   QUIT            close the connection          → OK bye
/// Anything else    → ERR unknown command '&lt;x&gt;'
/// A malformed RGB line → ERR bad RGB line: ...
///
/// Text rather than a binary framing because the whole point of this service is that
/// anything on the network can drive the LEDs: curl, PowerShell, a shell one-liner, or a
/// Python script, with no client library and no protocol version negotiation.
/// </summary>
public static class Protocol
{
    /// <summary>Default TCP port. Deliberately not 6742 (OpenRGB) so both can coexist.</summary>
    public const int DefaultPort = 6743;

    /// <summary>Protocol revision advertised in the greeting.</summary>
    public const int Version = 1;

    /// <summary>First token of the greeting line the server sends on accept.</summary>
    public const string GreetingPrefix = "LEDAGENT";

    public const int MaxLineLength = 512;

    public static string Greeting(string backendName) => $"{GreetingPrefix} {Version} {backendName}";

    public const string Ok = "OK";
    public const string Ping = "PONG";

    public static string Error(string message) => $"ERR {message}";
    public static string OkWith(string message) => $"OK {message}";
    public static string StatusLine(string json) => $"STATUS {json}";

    /// <summary>
    /// Parse the arguments of an <c>RGB r g b</c> line into a packed 0x00RRGGBB colour.
    /// Accepts either the three channel values on their own (what the server passes after it
    /// has split the verb off) or the whole line including the leading "RGB".
    /// Returns false and fills <paramref name="error"/> when the line is malformed.
    /// </summary>
    public static bool TryParseRgb(string line, out int color, out string? error)
    {
        color = 0;
        error = null;

        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // The server hands over only the arguments; callers that pass the whole line are
        // tolerated so this stays usable from a test or a script.
        string[] channels = parts.Length == 4 && parts[0].Equals("RGB", StringComparison.OrdinalIgnoreCase)
            ? [parts[1], parts[2], parts[3]]
            : parts;

        if (channels.Length != 3)
        {
            error = $"bad RGB line: expected 'RGB r g b', got '{line}'";
            return false;
        }

        if (!TryByte(channels[0], out byte r) || !TryByte(channels[1], out byte g) || !TryByte(channels[2], out byte b))
        {
            error = $"bad RGB line: channels must be 0-255, got '{line}'";
            return false;
        }

        color = (r << 16) | (g << 8) | b;
        return true;
    }

    private static bool TryByte(string text, out byte value)
        => byte.TryParse(text, out value);

    /// <summary>Split a packed 0x00RRGGBB colour into its channels.</summary>
    public static void Unpack(int color, out byte r, out byte g, out byte b)
    {
        r = (byte)((color >> 16) & 0xFF);
        g = (byte)((color >> 8) & 0xFF);
        b = (byte)(color & 0xFF);
    }
}
