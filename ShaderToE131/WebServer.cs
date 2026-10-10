using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShaderToE131;

/// <summary>
/// Lightweight HTTP web server for shader selection and audio control.
/// Uses raw Socket (no HttpListener, no admin privileges required). Runs on a configurable port.
/// </summary>
public sealed class WebServer : IDisposable
{
    private readonly int _port;
    private readonly string _shaderDirPath;
    private readonly IPAddress _bindIp;
    private TcpListener? _listener;
    private Thread? _acceptThread;
    private volatile bool _running = false;

    // Shared state — set by Program after construction.
    // Render loop polls these each frame (volatile for thread safety).
    private volatile string? _pendingShaderSource;
    private volatile string? _pendingShaderFileName;

    public string? PendingShaderSource { get => _pendingShaderSource; set => _pendingShaderSource = value; }
    public string? PendingShaderFileName { get => _pendingShaderFileName; set => _pendingShaderFileName = value; }
    public Func<bool>? GetAudioEnabled { get; set; }              // current audio state
    public Action<bool>? SetAudioEnabled { get; set; }              // toggle audio on/off
    public Action<string>? SetAudioSource { get; set; }            // change audio source (microphone/loopback/off)
    public Func<string?>? GetCurrentAudioSource { get; set; }       // current audio source label
    public int CurrentDeviceIndex { get; set; }                     // currently selected audio device index
    public Action<int>? SetDeviceIndex { get; set; }                // change audio device index via web
    public Func<string?>? GetLoopbackDeviceName { get; set; }       // current loopback device friendly name
    public Action<int>? SetAudioDeviceIndex { get; set; }         // change audio device index via web
    public Func<List<(int Index, string Name)>>? GetAvailableDevices { get; set; }  // list available devices for selection UI
    public Func<List<(int Index, string Name)>>? GetAvailableDevicesFresh { get; set; }  // same, but re-enumerated (used by /api/audio-devices)

    // ─── LED string output state ───
    // A pending config change posted by the web UI; consumed (and cleared) by the
    // render loop once it has been applied on the GL thread.
    private volatile StringConfigChange? _pendingStringChange;
    public StringConfigChange? PendingStringChange { get => _pendingStringChange; set => _pendingStringChange = value; }

    /// <summary>
    /// A text notification posted via /api/notify, waiting for the render loop to pick up.
    /// An empty Text means "clear the active notification".
    /// </summary>
    public record NotificationRequest(string Text, int DurationSec);

    private volatile NotificationRequest? _pendingNotification;
    public NotificationRequest? PendingNotification { get => _pendingNotification; set => _pendingNotification = value; }
    public Func<LiveStringState>? GetLiveStringState { get; set; }  // current string config + live stats

    // Shader list is rebuilt into a NEW array and published with a single
    // reference assignment. Readers (HTTP handlers, render loop) always see a
    // complete, immutable snapshot — never a list that is half-way through being
    // re-scanned by another thread.
    private readonly object _shaderScanLock = new();
    private volatile ShaderInfo[] _shaders = Array.Empty<ShaderInfo>();
    private DateTime _shadersScannedAt = DateTime.MinValue;
    private DateTime _shaderDirStamp = DateTime.MinValue;
    private const int RescanSeconds = 60;   // full re-scan interval; file add/remove is caught by the directory stamp
    internal IReadOnlyList<ShaderInfo> Shaders => _shaders;
    private volatile ApiStatus? _statusSnapshot;

    public record ShaderInfo(string Name, string FileName, bool IsAudioReactive);

    /// <summary>
    /// Pending LED string config change posted from the web UI.
    /// A null field means "leave unchanged". For <see cref="Ip"/> an empty string
    /// means "reset to the matrix IP"; for <see cref="Universe"/> 0 means "auto".
    /// </summary>
    public record StringConfigChange(bool? Enabled, int? Size, int? Row, string? Ip, int? Universe);

    /// <summary>Current LED string configuration plus live send stats.</summary>
    public record LiveStringState(
        bool Enabled,
        int Size,
        int Row,
        string? Ip,          // null = same as matrix
        int Universe,        // 0 = auto
        int FramesSent,
        int SendErrors,
        int MatrixHeight
    );

    public record ApiStatus(
        string SelectedShader,
        bool AudioEnabled,
        string? AudioSource,   // "off" | "microphone" | "loopback"
        int TotalShaders,
        string[] AudioReactiveNames,
        double UptimeSecs,
        int FramesSent,
        int SendErrors,
        string? LoopbackDeviceName,
        List<(int Index, string Name)> AvailableDevices,
        bool StringEnabled,
        int StringSize,
        int StringRow,
        string? StringIp,
        int StringUniverse,
        int StringFramesSent,
        int StringSendErrors,
        string? ActiveNotification,
        double NotificationRemainingSecs
    );

    private static readonly JsonSerializerOptions JsonCamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public WebServer(int port, string shaderDirPath, string bindAddress = "localhost")
    {
        _port = port;
        _shaderDirPath = Path.IsPathRooted(shaderDirPath) ? shaderDirPath : Path.Combine(Directory.GetCurrentDirectory(), shaderDirPath);
        _bindIp = ResolveBindAddress(bindAddress);
        LoadShaderList();
    }

    private static IPAddress ResolveBindAddress(string address)
    {
        if (address == "+" || address == "0.0.0.0") return IPAddress.Any;
        if (address.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return IPAddress.Loopback;
        if (IPAddress.TryParse(address, out var ip)) return ip;
        // Unknown names fall back to loopback for safety.
        return IPAddress.Loopback;
    }

    private DateTime DirectoryStamp()
    {
        try { return Directory.Exists(_shaderDirPath) ? Directory.GetLastWriteTimeUtc(_shaderDirPath) : DateTime.MinValue; }
        catch { return DateTime.MinValue; }
    }

    private void LoadShaderList()
    {
        var list = new List<ShaderInfo>();
        if (Directory.Exists(_shaderDirPath))
        {
            foreach (var file in Directory.GetFiles(_shaderDirPath, "*.glsl", SearchOption.TopDirectoryOnly)
                                            .OrderBy(Path.GetFileName))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                list.Add(new ShaderInfo(name, file, IsAudioReactiveShader(file)));
            }
        }
        _shaders = list.ToArray();
        _shadersScannedAt = DateTime.UtcNow;
        _shaderDirStamp = DirectoryStamp();
    }

    /// <summary>
    /// Return the current shader snapshot, re-scanning the directory only when the
    /// cached snapshot is older than <see cref="RescanSeconds"/>. Safe to call from
    /// every request and from the render loop.
    /// </summary>
    private ShaderInfo[] CurrentShaders()
    {
        // Reading every .glsl file costs hundreds of ms, and /api/status is polled constantly.
        // Re-scan only when the directory itself changes (add/remove/rename) or once a minute.
        if ((DateTime.UtcNow - _shadersScannedAt).TotalSeconds >= RescanSeconds || DirectoryStamp() != _shaderDirStamp)
        {
            lock (_shaderScanLock)
            {
                if ((DateTime.UtcNow - _shadersScannedAt).TotalSeconds >= RescanSeconds || DirectoryStamp() != _shaderDirStamp)
                    LoadShaderList();
            }
        }
        return _shaders;
    }

    private static bool IsAudioReactiveShader(string filePath)
    {
        try
        {
            string src = File.ReadAllText(filePath);
            return src.Contains("u_bass") || src.Contains("u_lowmid") || src.Contains("u_mid")
                || src.Contains("u_highmid") || src.Contains("u_treble") || src.Contains("u_volume");
        }
        catch { return false; }
    }

    public void Start()
    {
        _running = true;
        try
        {
            _listener = new TcpListener(_bindIp, _port);
            _listener.Start();
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true };
            _acceptThread.Start();
            var addrStr = (_bindIp == IPAddress.Any) ? "0.0.0.0" : _bindIp.ToString();
            Console.WriteLine($"[WebServer] Started on http://{addrStr}:{_port}/");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WebServer] Failed to start: {ex.Message}");
            _running = false;
        }
    }

    private void AcceptLoop()
    {
        while (_running && _listener != null)
        {
            try
            {
                var socket = _listener.AcceptSocket();
                Task.Run(() => HandleClient(socket));
            }
            catch (ObjectDisposedException) { break; }
            catch (InvalidOperationException) { break; }
            catch (SocketException ex)
            {
                if (!_running) break;
                Console.WriteLine($"[WebServer] Accept error: {ex.Message}");
            }
        }
    }

    private void HandleClient(Socket clientSocket)
    {
        try
        {
            var buffer = new byte[65536];
            int received = 0;

            // Read until we have the full HTTP headers (double CRLF)
            while (received < buffer.Length)
            {
                var bytesRead = clientSocket.Receive(buffer, received, buffer.Length - received, SocketFlags.None);
                if (bytesRead == 0) break; // connection closed

                received += bytesRead;

                // Check for end of headers: \r\n\r\n
                int headerEnd = FindDoubleCRLF(buffer, received);
                if (headerEnd > 0)
                {
                    break;
                }
            }

            if (received == 0) return;

            string requestLine = System.Text.Encoding.UTF8.GetString(buffer, 0, Math.Min(received, 2048));
            int firstCRLF = requestLine.IndexOf("\r\n");
            string methodAndPath = firstCRLF > 0 ? requestLine.Substring(0, firstCRLF) : requestLine;
            var parts = methodAndPath.Split(' ');
            string method = parts.Length > 0 ? parts[0] : "GET";
            string rawPath = parts.Length > 1 ? parts[1] : "/";

            // Parse query string
            string? queryString = null;
            int qIdx = rawPath.IndexOf('?');
            if (qIdx > 0) queryString = rawPath.Substring(qIdx + 1);
            string path = Uri.UnescapeDataString(qIdx > 0 ? rawPath.Substring(0, qIdx) : rawPath);

            // Read POST body if Content-Length present
            int contentLength = 0;
            string body = "";
            if (method == "POST")
            {
                var clMatch = System.Text.RegularExpressions.Regex.Match(requestLine, @"Content-Length:\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (clMatch.Success)
                {
                    var match = System.Text.RegularExpressions.Regex.Match(requestLine, @"Content-Length:\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (clMatch.Success && int.TryParse(clMatch.Groups[1].Value, out contentLength))
                    {
                        int headerEnd = FindDoubleCRLF(buffer, received);
                        int bodyStart = headerEnd + 4;
                        int availableBody = Math.Max(0, received - bodyStart);

                        if (availableBody < contentLength)
                        {
                            // Read remaining body bytes
                            var bodyBuf = new byte[contentLength];
                            Array.Copy(buffer, bodyStart, bodyBuf, 0, availableBody);
                            int readSoFar = availableBody;
                            while (readSoFar < contentLength)
                            {
                                var bytesRead = clientSocket.Receive(bodyBuf, readSoFar, contentLength - readSoFar, SocketFlags.None);
                                if (bytesRead == 0) break;
                                readSoFar += bytesRead;
                            }
                            body = System.Text.Encoding.UTF8.GetString(bodyBuf, 0, readSoFar);
                        }
                        else
                        {
                            body = System.Text.Encoding.UTF8.GetString(buffer, bodyStart, availableBody);
                        }
                    }
                }
            }

            // Route the request — pass raw method + path + body instead of HttpListenerContext
            HandleRequest(clientSocket, method, path, queryString, body);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WebServer] Client error: {ex.Message}");
        }
        finally
        {
            // Graceful close: half-close after the full response is queued, and keep
            // the socket alive until the OS has actually delivered it (SO_LINGER).
            // An abrupt Close() here drops any still-buffered bytes on the wire.
            try { clientSocket.LingerState = new LingerOption(true, 5); } catch { }
            try { clientSocket.Shutdown(SocketShutdown.Send); } catch { }
            try { clientSocket.Close(); } catch { }
        }
    }

    private static int FindDoubleCRLF(byte[] buffer, int length)
    {
        for (int i = 3; i < length; i++)
        {
            // Match \r\n\r\n with i at the final '\n'.
            if (buffer[i] == '\n' && buffer[i - 1] == '\r' && buffer[i - 2] == '\n' && buffer[i - 3] == '\r')
                return i - 3;
        }
        return -1;
    }

    private void HandleRequest(Socket clientSocket, string method, string path, string? queryString, string body)
    {
        // CORS headers for all responses
        var corsHeaders = "Access-Control-Allow-Origin: *\r\n" +
                          "Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n" +
                          "Access-Control-Allow-Headers: Content-Type\r\n";

        if (method == "OPTIONS")
        {
            SendResponse(clientSocket, 204, "", "", corsHeaders);
            return;
        }

        try
        {
            switch (path)
            {
                case "/":
                    ServeIndex(clientSocket);
                    break;
                case "/api/shaders":
                    if (method == "GET") ServeShadersList(clientSocket);
                    else SendResponse(clientSocket, 405, "", "text/plain; charset=utf-8", corsHeaders);
                    break;
                case "/api/select-shader":
                    if (method == "POST") ServeSelectShader(clientSocket, body);
                    else SendResponse(clientSocket, 405, "", "text/plain; charset=utf-8", corsHeaders);
                    break;
                case "/api/set-audio":
                    if (method == "POST") ServeSetAudio(clientSocket, body);
                    else SendResponse(clientSocket, 405, "", "text/plain; charset=utf-8", corsHeaders);
                    break;
                case "/api/audio-devices":
                    if (method == "GET") ServeGetAudioDevices(clientSocket);
                    else SendResponse(clientSocket, 405, "", "text/plain; charset=utf-8", corsHeaders);
                    break;
                case "/api/set-audio-device":
                    if (method == "POST") ServeSetAudioDevice(clientSocket, body);
                    else SendResponse(clientSocket, 405, "", "text/plain; charset=utf-8", corsHeaders);
                    break;
                case "/api/set-audio-source":
                    if (method == "POST") ServeSetAudioSource(clientSocket, body);
                    else SendResponse(clientSocket, 405, "", "text/plain; charset=utf-8", corsHeaders);
                    break;
                case "/api/set-string":
                    if (method == "POST") ServeSetString(clientSocket, body);
                    else SendResponse(clientSocket, 405, "", "text/plain; charset=utf-8", corsHeaders);
                    break;
                case "/api/string-config":
                    if (method == "GET") ServeStringConfig(clientSocket);
                    else SendResponse(clientSocket, 405, "", "text/plain; charset=utf-8", corsHeaders);
                    break;
                case "/api/notify":
                    if (method == "POST") ServeNotify(clientSocket, body);
                    else SendResponse(clientSocket, 405, "", "text/plain; charset=utf-8", corsHeaders);
                    break;
                case "/api/status":
                    if (method == "GET") ServeStatus(clientSocket);
                    else SendResponse(clientSocket, 405, "", "text/plain; charset=utf-8", corsHeaders);
                    break;
                default:
                    // Try to serve shader files directly for preview
                    if (path.StartsWith("/shaders/") && path.EndsWith(".glsl"))
                        ServeShaderFile(clientSocket, path);
                    else
                    {
                        SendResponse(clientSocket, 404, "", "text/plain; charset=utf-8", corsHeaders);
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WebServer] Error handling {path}: {ex.Message}");
            try { SendResponse(clientSocket, 500, "Internal Server Error", "text/plain; charset=utf-8", corsHeaders); } catch { }
        }
    }

    private void SendResponse(Socket clientSocket, int statusCode, string? body = null, string contentType = "text/plain; charset=utf-8", string extraHeaders = "")
    {
        string reason = statusCode switch
        {
            200 => "OK",
            204 => "No Content",
            404 => "Not Found",
            405 => "Method Not Allowed",
            500 => "Internal Server Error",
            _ => "OK"
        };

        byte[] bodyBytes = string.IsNullOrEmpty(body)
            ? Array.Empty<byte>()
            : System.Text.Encoding.UTF8.GetBytes(body);

        var response = $"HTTP/1.1 {statusCode} {reason}\r\n" +
                       "Connection: close\r\n" +
                       extraHeaders +
                       (string.IsNullOrEmpty(contentType) ? "" : $"Content-Type: {contentType}\r\n") +
                       $"Content-Length: {bodyBytes.Length}\r\n" +
                       "\r\n";

        byte[] headerBytes = System.Text.Encoding.UTF8.GetBytes(response);

        // Headers + body in ONE buffer, sent in full. Sending them separately and
        // then closing the socket can leave the tail of the body in the send buffer,
        // which Close() discards — the client then sees a truncated response
        // (e.g. an empty / partially filled shader dropdown).
        byte[] responseBytes = new byte[headerBytes.Length + bodyBytes.Length];
        Buffer.BlockCopy(headerBytes, 0, responseBytes, 0, headerBytes.Length);
        Buffer.BlockCopy(bodyBytes, 0, responseBytes, headerBytes.Length, bodyBytes.Length);

        try
        {
            int sent = 0;
            while (sent < responseBytes.Length)
            {
                int n = clientSocket.Send(responseBytes, sent, responseBytes.Length - sent, SocketFlags.None);
                if (n <= 0) break;
                sent += n;
            }
        }
        catch (Exception ex) { Console.WriteLine($"[WebServer] Send error: {ex.Message}"); }
    }

    private void SendJson(Socket clientSocket, object data, string extraHeaders = "")
    {
        string json = JsonSerializer.Serialize(data, JsonCamelCase);
        var headers = "Access-Control-Allow-Origin: *\r\n"
                    + "Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n"
                    + "Access-Control-Allow-Headers: Content-Type\r\n"
                    + extraHeaders;
        SendResponse(clientSocket, 200, json, "application/json; charset=utf-8", headers);
    }

    private void ServeIndex(Socket clientSocket)
    {
        string html = @"
<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""UTF-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
<title>ShaderToE131 — Control Panel</title>
<style>
  * { margin: 0; padding: 0; box-sizing: border-box; }
  :root {
    --bg: #0a0e14;
    --panel: #151d29;
    --panel2: #0f151f;
    --line: #24303f;
    --text: #e6edf3;
    --muted: #8b98a9;
    --accent: #4da3ff;
    --violet: #a06bff;
    --green: #22c58a;
    --amber: #d29922;
    --err: #f85149;
  }
  body {
    font-family: 'Segoe UI', system-ui, -apple-system, sans-serif;
    color: var(--text);
    background:
      radial-gradient(1100px 620px at 10% -10%, rgba(77,163,255,.16), transparent 62%),
      radial-gradient(900px 520px at 95% -4%, rgba(160,107,255,.14), transparent 58%),
      var(--bg);
    min-height: 100vh;
    padding: 26px 18px 48px;
    -webkit-font-smoothing: antialiased;
  }
  .wrap { max-width: 1080px; margin: 0 auto; }

  header { display: flex; align-items: center; gap: 14px; flex-wrap: wrap; margin-bottom: 18px; }
  .mark { width: 44px; height: 44px; border-radius: 12px; flex: 0 0 auto;
    background: conic-gradient(from 210deg, #4da3ff, #a06bff, #22c58a, #4da3ff);
    box-shadow: 0 0 26px rgba(77,163,255,.35); }
  h1 { font-size: 1.5rem; letter-spacing: -.02em; }
  .subtitle { color: var(--muted); font-size: .85rem; }
  .pill { margin-left: auto; display: inline-flex; align-items: center; gap: 8px; font-size: .74rem;
    border: 1px solid var(--line); background: var(--panel2); border-radius: 999px; padding: 6px 12px; color: var(--muted); }
  .pill .dot { width: 8px; height: 8px; border-radius: 50%; background: var(--err); }
  .pill.live { color: var(--green); border-color: rgba(34,197,138,.4); }
  .pill.live .dot { background: var(--green); box-shadow: 0 0 8px var(--green); }

  .banner { border-radius: 10px; padding: 11px 14px; font-size: .85rem; margin-bottom: 16px; }
  .banner.ok { background: rgba(34,197,138,.12); border: 1px solid rgba(34,197,138,.45); color: #3fb950; }
  .banner.error { background: rgba(248,81,73,.12); border: 1px solid rgba(248,81,73,.45); color: var(--err); }

  .layout { display: grid; grid-template-columns: minmax(0,1.55fr) minmax(0,1fr); gap: 16px; align-items: start; }
  .card { background: linear-gradient(180deg, var(--panel), var(--panel2));
    border: 1px solid var(--line); border-radius: 14px; padding: 18px;
    box-shadow: 0 18px 40px rgba(0,0,0,.35); }
  .col > .card + .card { margin-top: 16px; }

  h2 { display: flex; align-items: center; gap: 8px; font-size: .72rem; font-weight: 600;
    text-transform: uppercase; letter-spacing: .12em; color: var(--muted); margin-bottom: 14px; }
  h2::before { content: ''; width: 10px; height: 10px; border-radius: 3px; background: var(--accent); }
  .card.audio h2::before { background: var(--violet); }
  .card.string h2::before { background: var(--green); }
  .card.status h2::before { background: var(--amber); }

  label { display: block; font-weight: 600; font-size: .8rem; margin-bottom: 6px; }
  select, input[type=number], input[type=text] {
    width: 100%; padding: 10px 12px; font-size: .9rem; color: var(--text);
    background: #0c1219; border: 1px solid var(--line); border-radius: 10px; transition: border-color .15s, box-shadow .15s; }
  select { appearance: none; -webkit-appearance: none; padding-right: 34px; cursor: pointer;
    background-image: url('data:image/svg+xml;utf8,<svg xmlns=""http://www.w3.org/2000/svg"" width=""12"" height=""8"" viewBox=""0 0 12 8"" fill=""none"" stroke=""%238b98a9"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round""><path d=""M1 1l5 5 5-5""/></svg>');
    background-repeat: no-repeat; background-position: right 12px center; }
  ::placeholder { color: #58657a; }
  select:hover, input:hover { border-color: #354458; }
  select:focus, input:focus { outline: none; border-color: var(--accent); box-shadow: 0 0 0 3px rgba(77,163,255,.18); }
  select:disabled, input:disabled { opacity: .45; cursor: not-allowed; }

  button { width: 100%; padding: 11px 14px; margin-top: 12px; border-radius: 10px; font-size: .9rem;
    cursor: pointer; border: 1px solid var(--line); background: #0c1219; color: var(--text);
    transition: transform .12s, background .15s, border-color .15s; }
  button:hover { transform: translateY(-1px); border-color: #354458; }
  button:active { transform: none; }
  button.primary { background: linear-gradient(180deg, #2ea043, #238636); border-color: #2ea043; color: #fff; font-weight: 600; }
  button.primary:hover { background: linear-gradient(180deg, #35a94c, #2ea043); }
  button:focus-visible { outline: none; box-shadow: 0 0 0 3px rgba(77,163,255,.25); }

  .fields { display: grid; grid-template-columns: repeat(auto-fit, minmax(150px, 1fr)); gap: 12px; }
  .hint { font-size: .75rem; color: var(--muted); margin-top: 8px; }

  .toggle-row { display: flex; align-items: center; gap: 12px; margin-bottom: 14px; }
  .toggle-row .desc { font-size: .82rem; color: var(--muted); }
  .toggle-switch { position: relative; width: 52px; height: 28px; flex-shrink: 0; display: inline-block; }
  .toggle-switch input { opacity: 0; width: 0; height: 0; }
  .toggle-slider { position: absolute; inset: 0; background: #2a3646; border-radius: 14px; cursor: pointer; transition: .2s; }
  .toggle-slider::before { content: ''; position: absolute; width: 22px; height: 22px; left: 3px; top: 3px; background: #cfd8e3; border-radius: 50%; transition: .2s; }
  .toggle-switch input:checked + .toggle-slider { background: var(--green); }
  .toggle-switch input:checked + .toggle-slider::before { transform: translateX(24px); }
  .toggle-switch input:focus-visible + .toggle-slider { box-shadow: 0 0 0 3px rgba(77,163,255,.25); }

  .status-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(132px, 1fr)); gap: 10px; }
  .status-item { background: #0c1219; border: 1px solid var(--line); border-radius: 10px; padding: 10px 12px; }
  .status-label { font-size: .64rem; text-transform: uppercase; letter-spacing: .08em; color: var(--muted); margin-bottom: 3px; }
  .status-value { font-family: ui-monospace, 'Cascadia Code', Consolas, monospace; font-size: .9rem; font-weight: 600;
    color: var(--accent); word-break: break-all; }
  .notice { font-size: .74rem; color: var(--muted); margin-top: 18px; text-align: center; }

  @media (max-width: 960px) { .layout { grid-template-columns: 1fr; } }
  @media (max-width: 520px) {
    body { padding: 16px 12px 32px; }
    .card { padding: 14px; }
    h1 { font-size: 1.25rem; }
    .pill { margin-left: 0; }
  }
  @media (prefers-reduced-motion: reduce) {
    * { transition: none !important; }
    button:hover { transform: none; }
  }
</style>
</head>
<body>
<div class=""wrap"">
  <header>
    <div class=""mark""></div>
    <div>
      <h1>ShaderToE131</h1>
      <div class=""subtitle"">LED matrix shader control panel</div>
    </div>
    <span class=""pill"" id=""connPill""><span class=""dot""></span><span id=""connText"">connecting…</span></span>
  </header>

  <div id=""banner"" class=""banner ok"" style=""display:none""></div>

  <div class=""layout"">
    <div class=""col"">
      <section class=""card"">
        <h2>Shader</h2>
        <label for=""shaderSelect"">Shader</label>
        <select id=""shaderSelect""><option value=""off"">Off (blank)</option><option value="""">— select shader —</option></select>
        <div class=""hint"" id=""shaderCount""></div>
        <button class=""primary"" id=""applyShaderBtn"">Apply Shader</button>
      </section>

      <section class=""card audio"">
        <h2>Audio</h2>
        <div class=""fields"">
          <div>
            <label for=""audioSourceSelect"">Source</label>
            <select id=""audioSourceSelect"">
              <option value=""off"">Off</option>
              <option value=""microphone"">Microphone</option>
              <option value=""loopback"">Loopback (system audio)</option>
            </select>
          </div>
          <div>
            <label for=""audioDeviceSelect"">Loopback Device</label>
            <select id=""audioDeviceSelect""><option value=""-1"">Loading devices…</option></select>
          </div>
        </div>
        <div class=""hint"">Choosing a device switches the audio source to that loopback device.</div>
      </section>

      <section class=""card notify"">
        <h2>Notifications</h2>
        <label for=""notifyText"">Message</label>
        <input type=""text"" id=""notifyText"" maxlength=""200"" placeholder=""Text to show on the LED matrix"">
        <div class=""fields"">
          <div>
            <label for=""notifyDuration"">Duration (s)</label>
            <input type=""number"" id=""notifyDuration"" min=""1"" max=""300"" value=""10"">
          </div>
        </div>
        <button id=""sendNotifyBtn"">Send Notification</button>
        <div class=""hint"" id=""notifyStatus"">No active notification.</div>
      </section>
    </div>

    <div class=""col"">
      <section class=""card status"">
        <h2>Live Status</h2>
        <div class=""status-grid"">
          <div class=""status-item""><div class=""status-label"">Shader</div><div class=""status-value"" id=""stShader"">—</div></div>
          <div class=""status-item""><div class=""status-label"">Audio</div><div class=""status-value"" id=""stAudio"">—</div></div>
          <div class=""status-item""><div class=""status-label"">String</div><div class=""status-value"" id=""stString"">—</div></div>
          <div class=""status-item""><div class=""status-label"">Uptime</div><div class=""status-value"" id=""stUptime"">—</div></div>
          <div class=""status-item""><div class=""status-label"">Matrix Frames</div><div class=""status-value"" id=""stFrames"">0</div></div>
          <div class=""status-item""><div class=""status-label"">Send Errors</div><div class=""status-value"" id=""stErrors"">0</div></div>
        </div>
        <div class=""hint"">Auto-refreshes every 2 s. Changes apply immediately.</div>
      </section>

      <section class=""card string"">
        <h2>LED String</h2>
        <div class=""toggle-row"">
          <label class=""toggle-switch"">
            <input type=""checkbox"" id=""stringEnabled"" aria-labelledby=""stringEnabledDesc"">
            <span class=""toggle-slider""></span>
          </label>
          <span class=""desc"" id=""stringEnabledDesc"">Stream an LED string alongside the matrix (mirrors a matrix row)</span>
        </div>
        <div class=""fields"">
          <div>
            <label for=""stringSize"">LED Count</label>
            <input type=""number"" id=""stringSize"" min=""1"" max=""2000"" placeholder=""50"">
          </div>
          <div>
            <label for=""stringRow"">Matrix Row</label>
            <input type=""number"" id=""stringRow"" min=""0"" max=""10"" placeholder=""center"">
          </div>
          <div>
            <label for=""stringIp"">Target IP / hostname</label>
            <input type=""text"" id=""stringIp"" placeholder=""same as matrix"" title=""IPv4/IPv6 address or hostname (e.g. ledstring.local); blank = matrix IP"">
          </div>
          <div>
            <label for=""stringUniverse"">Universe</label>
            <input type=""number"" id=""stringUniverse"" min=""0"" max=""63999"" placeholder=""auto"">
          </div>
        </div>
        <button id=""applyStringBtn"">Apply String Settings</button>
        <div class=""hint"" id=""stringStatus"">Loading…</div>
      </section>
    </div>
  </div>

  <div class=""notice"">ShaderToE131 · sACN / E1.31 output</div>
</div>

<script>
const API = '';
let bannerTimer = null;

function $(id) { return document.getElementById(id); }

function showBanner(msg, isError) {
  const b = $('banner');
  b.textContent = msg;
  b.className = 'banner ' + (isError ? 'error' : 'ok');
  b.style.display = 'block';
  clearTimeout(bannerTimer);
  bannerTimer = setTimeout(() => { b.style.display = 'none'; }, 5000);
}

function truncate(s, n) { return s && s.length > n ? s.substring(0, n - 2) + '…' : (s || ''); }

function setConn(ok) {
  $('connPill').classList.toggle('live', ok);
  $('connText').textContent = ok ? 'connected' : 'disconnected';
}

// ── Shader ───────────────────────────────────────────────────────
let shadersLoaded = false;

async function loadShaders(retries) {
  retries = (retries === undefined) ? 2 : retries;
  for (let attempt = 0; ; attempt++) {
    try {
      const r = await fetch(API + 'api/shaders', { cache: 'no-store' });
      if (!r.ok) throw new Error('HTTP ' + r.status);
      const data = await r.json();
      if (!Array.isArray(data.shaders)) throw new Error('bad shader list');
      const sel = $('shaderSelect');
      const prev = sel.value;
      sel.innerHTML = '';
      sel.add(new Option('Off (blank)', 'off'));
      sel.add(new Option('— select shader —', ''));
      for (const s of data.shaders) sel.add(new Option(s.name + (s.isAudioReactive ? ' 🔊' : ''), s.name));
      if (prev !== '' && [...sel.options].some(o => o.value === prev)) sel.value = prev;
      shadersLoaded = true;
      $('shaderCount').textContent = data.shaders.length === 0
        ? 'No .glsl shaders in the shader directory.'
        : data.shaders.length + ' shaders available · 🔊 reacts to audio';
      if (data.shaders.length === 0) showBanner('No .glsl shaders found in the shader directory.', true);
      return;
    } catch (e) {
      if (attempt < retries) { await new Promise(r => setTimeout(r, 400 * (attempt + 1))); continue; }
      // Never leave the user staring at a silently empty dropdown.
      showBanner('Could not load the shader list (' + (e && e.message ? e.message : e) + ').', true);
      return;
    }
  }
}

async function applyShader() {
  const name = $('shaderSelect').value;
  if (!name) { showBanner('Pick a shader first — or choose ""Off (blank)"".', true); return; }
  try {
    const r = await fetch(API + 'api/select-shader', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ name }) });
    const d = await r.json();
    if (d.ok) {
      showBanner(name === 'off' ? 'Shader turned off (blank).' : 'Applied ' + (d.shader || name) + '.', false);
      refreshStatus();
    } else {
      showBanner(d.error || 'Failed to apply shader.', true);
    }
  } catch (e) { showBanner('Failed to apply shader: ' + e, true); }
}

// ── Audio ────────────────────────────────────────────────────────
async function loadDevices() {
  try {
    const r = await fetch(API + 'api/audio-devices');
    const data = await r.json();
    const sel = $('audioDeviceSelect');
    sel.innerHTML = '';
    if (!data.devices || data.devices.length === 0) {
      sel.add(new Option('No loopback devices found', '-1'));
      return;
    }
    for (const dev of data.devices) sel.add(new Option(dev.name, dev.index));
  } catch (e) { console.error('Failed to load audio devices', e); }
}

function syncAudioUi(d) {
  const sel = $('audioSourceSelect');
  if (d.audioSource && sel.value !== d.audioSource) sel.value = d.audioSource;
  $('audioDeviceSelect').disabled = d.audioSource !== 'loopback';
  if (d.audioSource === 'loopback' && d.loopbackDeviceName) {
    const devSel = $('audioDeviceSelect');
    for (const opt of devSel.options) {
      if (opt.text === d.loopbackDeviceName) {
        if (devSel.value !== opt.value) devSel.value = opt.value;
        break;
      }
    }
  }
  let label;
  if (!d.audioEnabled) label = 'off';
  else if (d.audioSource === 'loopback') label = 'loopback' + (d.loopbackDeviceName ? ' · ' + truncate(d.loopbackDeviceName, 24) : '');
  else label = 'microphone';
  $('stAudio').textContent = label;
}

$('audioSourceSelect').addEventListener('change', async (e) => {
  const source = e.target.value;
  try {
    const r = await fetch(API + 'api/set-audio-source', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ source }) });
    const d = await r.json();
    if (d.ok) showBanner('Audio source set to ' + source + '.', false);
    else showBanner(d.error || 'Failed to change audio source.', true);
    refreshStatus();
  } catch (err) { showBanner('Failed to change audio source: ' + err, true); }
});

$('audioDeviceSelect').addEventListener('change', async (e) => {
  const idx = parseInt(e.target.value, 10);
  if (isNaN(idx)) return;
  try {
    const r = await fetch(API + 'api/set-audio-device', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ deviceIndex: idx }) });
    const d = await r.json();
    if (d.ok) {
      const deviceName = e.target.options[e.target.selectedIndex] ? e.target.options[e.target.selectedIndex].text : '';
      showBanner('Loopback device: ' + deviceName + '.', false);
      // Changing a device switches the source to loopback on the server.
      $('audioSourceSelect').value = 'loopback';
      refreshStatus();
    } else {
      showBanner(d.error || 'Failed to change loopback device.', true);
    }
  } catch (err) { showBanner('Failed to change loopback device: ' + err, true); }
});

// ── LED string ───────────────────────────────────────────────────
async function loadStringConfig() {
  try {
    const r = await fetch(API + 'api/string-config');
    const c = await r.json();
    $('stringEnabled').checked = !!c.enabled;
    $('stringSize').value = c.size;
    $('stringRow').value = c.row;
    $('stringIp').value = c.ip || '';
    $('stringUniverse').value = c.universe && c.universe > 0 ? c.universe : '';
    if (c.matrixHeight) {
      $('stringRow').max = c.matrixHeight - 1;
      $('stringRow').placeholder = Math.floor((c.matrixHeight - 1) / 2) + ' (center)';
    }
    updateStringHint(c);
  } catch (e) { console.error('Failed to load string config', e); }
}

function updateStringHint(c) {
  const el = $('stringStatus');
  if (!c || !c.enabled) { el.textContent = 'String output is off.'; return; }
  const target = c.ip ? c.ip : 'same as matrix';
  const uni = c.universe && c.universe > 0 ? c.universe : 'auto';
  el.textContent = c.size + ' LEDs · row ' + c.row + ' · universe ' + uni + ' → ' + target;
}

async function applyString() {
  const body = {
    enabled: $('stringEnabled').checked
  };
  const sizeRaw = $('stringSize').value.trim();
  const rowRaw = $('stringRow').value.trim();
  const uniRaw = $('stringUniverse').value.trim();
  body.ip = $('stringIp').value.trim(); // blank = reset to the matrix IP

  const num = (raw) => {
    const n = Number(raw);
    return Number.isInteger(n) ? n : NaN;
  };
  if (sizeRaw !== '') {
    const n = num(sizeRaw);
    if (isNaN(n)) { showBanner('LED count must be a whole number.', true); return; }
    body.size = n;
  }
  if (rowRaw !== '') {
    const n = num(rowRaw);
    if (isNaN(n)) { showBanner('Matrix row must be a whole number.', true); return; }
    body.row = n;
  }
  if (uniRaw !== '') {
    const n = num(uniRaw);
    if (isNaN(n)) { showBanner('Universe must be a whole number (0 = auto).', true); return; }
    body.universe = n;
  }

  try {
    const r = await fetch(API + 'api/set-string', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
    const d = await r.json();
    if (d.ok) {
      showBanner(body.enabled ? 'LED string settings applied.' : 'LED string disabled.', false);
      setTimeout(loadStringConfig, 300);
    } else {
      showBanner(d.error || 'Failed to apply string settings.', true);
    }
  } catch (e) { showBanner('Failed to apply string settings: ' + e, true); }
}

// ── Notifications ────────────────────────────────────────────────
async function sendNotification() {
  const text = $('notifyText').value.trim();
  const durRaw = $('notifyDuration').value.trim() || '10';
  const dur = Number(durRaw);
  if (!Number.isInteger(dur) || dur < 1 || dur > 300) { showBanner('Duration must be a whole number of seconds (1–300).', true); return; }
  try {
    const r = await fetch(API + 'api/notify', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ text, duration: dur }) });
    const d = await r.json();
    if (d.ok) showBanner(text ? 'Notification sent.' : 'Notification cleared.', false);
    else showBanner(d.error || 'Failed to send notification.', true);
  } catch (e) { showBanner('Failed to send notification: ' + e, true); }
}

// ── Status ───────────────────────────────────────────────────────
async function refreshStatus() {
  try {
    const r = await fetch(API + 'api/status');
    const d = await r.json();
    setConn(true);
    $('stShader').textContent = d.selectedShader || '—';
    syncAudioUi(d);

    // Keep the shader dropdown in sync with the server without clobbering a
    // shader (or 'off') the user just picked but hasn't applied yet. We only
    // sync when the dropdown still matches what the server reported on the
    // previous poll (or on the first poll); otherwise the user is staging a
    // manual selection and we leave it alone.
    const sel = $('shaderSelect');
    if (!shadersLoaded) loadShaders(0);   // self-heal a list that failed to load
    const prevServer = window._shaderServerSel;
    if (prevServer === undefined || sel.value === prevServer) {
      if (d.selectedShader === 'off') {
        sel.value = 'off';
      } else {
        const match = [...sel.options].find(o => o.value === d.selectedShader || o.value + '.glsl' === d.selectedShader);
        if (match) sel.value = match.value;
      }
    }
    window._shaderServerSel = d.selectedShader;

    $('stString').textContent = !d.stringEnabled ? 'off'
      : (d.stringSize + ' LEDs · uni ' + (d.stringUniverse > 0 ? d.stringUniverse : 'auto') + ' → ' + (d.stringIp || 'matrix IP'));
    updateStringHint({ enabled: d.stringEnabled, size: d.stringSize, row: d.stringRow, ip: d.stringIp, universe: d.stringUniverse });

    $('stFrames').textContent = (d.framesSent ?? 0).toLocaleString();
    $('stErrors').textContent = (d.sendErrors ?? 0).toLocaleString();

    const notifyEl = $('notifyStatus');
    if (notifyEl) {
      notifyEl.textContent = d.activeNotification
        ? 'Showing: ' + truncate(d.activeNotification, 60) + ' · ' + Math.ceil(d.notificationRemainingSecs || 0) + 's left'
        : 'No active notification.';
    }

    const s = Math.max(0, Math.floor(d.uptimeSecs || 0));
    const h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60), sec = s % 60;
    $('stUptime').textContent = h + 'h ' + m.toString().padStart(2, '0') + 'm ' + sec + 's';
  } catch (e) { setConn(false); }
}

// ── Wire up ──────────────────────────────────────────────────────
$('applyShaderBtn').addEventListener('click', applyShader);
$('applyStringBtn').addEventListener('click', applyString);
$('sendNotifyBtn').addEventListener('click', sendNotification);
$('notifyText').addEventListener('keydown', e => { if (e.key === 'Enter') sendNotification(); });
loadShaders();
loadDevices();
loadStringConfig();
refreshStatus();
setInterval(refreshStatus, 2000);
</script>
</body>
</html>";
        SendResponse(clientSocket, 200, html, "text/html; charset=utf-8");
    }

    private void ServeShadersList(Socket clientSocket)
    {
        var data = CurrentShaders().Select(s => new { s.Name, s.FileName, s.IsAudioReactive }).ToArray();
        SendJson(clientSocket, new { shaders = data });
    }

    private void ServeSelectShader(Socket clientSocket, string body)
    {
        var jsonObj = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(body);
        string? name = jsonObj?.GetValueOrDefault("name")?.ToString();

        if (string.IsNullOrEmpty(name))
        {
            SendJson(clientSocket, new { ok = false, error = "Missing 'name' field." });
            return;
        }

        // Special: "off" renders a blank/black screen
        string lowerName = name.ToLowerInvariant();
        if (lowerName == "off")
        {
            PendingShaderSource = null;  // signals render loop to skip shader rendering
            PendingShaderFileName = "Off";
            Console.WriteLine("[WebServer] Shader set to Off — blank screen.");
            SendJson(clientSocket, new { ok = true, shader = "Off (blank)" });
            return;
        }

        // Search for the shader file by name. Match exactly on the display name
        // (filename without extension) or the full filename — never a substring,
        // so "fire" won't accidentally select "fireworks.glsl".
        string? fullPath = null;
        foreach (var s in CurrentShaders())
        {
            string fileNameWithExt = Path.GetFileName(s.FileName);
            if (s.Name.Equals(name!, StringComparison.OrdinalIgnoreCase)
                || fileNameWithExt.Equals(name!, StringComparison.OrdinalIgnoreCase))
            {
                fullPath = s.FileName;
                break;
            }
        }

        // Fallback: try as a filename directly
        if (fullPath == null && Directory.Exists(_shaderDirPath))
        {
            foreach (var f in Directory.GetFiles(_shaderDirPath, "*.glsl", SearchOption.TopDirectoryOnly))
            {
                if (Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileNameWithoutExtension(f).Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    fullPath = f;
                    break;
                }
            }
        }

        // Also try current directory's shaders/ subfolder
        if (fullPath == null && Directory.Exists(Path.Combine(Directory.GetCurrentDirectory(), "shaders")))
        {
            foreach (var f in Directory.GetFiles(Path.Combine(Directory.GetCurrentDirectory(), "shaders"), "*.glsl", SearchOption.TopDirectoryOnly))
            {
                if (Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileNameWithoutExtension(f).Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    fullPath = f;
                    break;
                }
            }
        }

        if (fullPath == null || !File.Exists(fullPath))
        {
            SendJson(clientSocket, new { ok = false, error = $"Shader '{name}' not found." });
            return;
        }

        string source = File.ReadAllText(fullPath);
        string fileName = Path.GetFileNameWithoutExtension(fullPath);
        PendingShaderSource = source;
        PendingShaderFileName = fileName;
        Console.WriteLine($"[WebServer] Shader changed via web: {fileName}");
        SendJson(clientSocket, new { ok = true, shader = Path.GetFileName(fullPath) });
    }

    private void ServeSetAudio(Socket clientSocket, string body)
    {
        var json = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(body);
        bool? enabled = null;

        if (json?.TryGetValue("enabled", out var val) == true)
        {
            if (val is bool b) enabled = b;
            else if (val is string s && bool.TryParse(s, out bool pb)) enabled = pb;
            else if (val is JsonElement je)
            {
                if (je.ValueKind == JsonValueKind.True || je.ValueKind == JsonValueKind.False)
                    enabled = je.GetBoolean();
                else if (je.ValueKind == JsonValueKind.String && bool.TryParse(je.GetString(), out bool pje))
                    enabled = pje;
                else if (je.ValueKind == JsonValueKind.Number && je.TryGetInt32(out int num))
                    enabled = num != 0;
            }
        }

        if (enabled.HasValue)
        {
            SetAudioEnabled?.Invoke(enabled.Value);
            Console.WriteLine($"[WebServer] Audio set to: {enabled}");
        }

        SendJson(clientSocket, new { ok = true, enabled = enabled ?? GetAudioEnabled!() });
    }

    private void ServeGetAudioDevices(Socket clientSocket)
    {
        // Asking for devices is an explicit user action, so enumerate them for real.
        var devicesRaw = (GetAvailableDevicesFresh ?? GetAvailableDevices)?.Invoke() ?? new List<(int Index, string Name)>();

        var devices = devicesRaw.Select(d => new { Index = d.Index, Name = d.Name }).ToList();
        SendJson(clientSocket, new { devices = devices });
    }
    private void ServeSetAudioDevice(Socket clientSocket, string body)
    {
        var json = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(body);
        int? deviceIndex = null;

        if (json?.TryGetValue("deviceIndex", out var val) == true)
        {
            if (val is int i) deviceIndex = i;
            else if (val is string s && int.TryParse(s, out int pi)) deviceIndex = pi;
            else if (val is JsonElement je)
            {
                if (je.ValueKind == JsonValueKind.Number && je.TryGetInt32(out int pje))
                    deviceIndex = pje;
                else if (je.ValueKind == JsonValueKind.String && int.TryParse(je.GetString(), out int ps))
                    deviceIndex = ps;
            }
        }

        if (deviceIndex.HasValue)
        {
            SetAudioDeviceIndex?.Invoke(deviceIndex.Value);
            Console.WriteLine($"[WebServer] Audio device index set to: {deviceIndex}");
        }

        SendJson(clientSocket, new { ok = true, deviceIndex = deviceIndex ?? CurrentDeviceIndex });
    }

    private void ServeSetAudioSource(Socket clientSocket, string body)
    {
        Dictionary<string, JsonElement>? json;
        try { json = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body); }
        catch { json = null; }

        string? source = json is { Count: > 0 } && json.TryGetValue("source", out var se) && se.ValueKind == JsonValueKind.String
            ? se.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(source))
        {
            SendJson(clientSocket, new { ok = false, error = "Missing 'source' field." });
            return;
        }

        string src = source.Trim().ToLowerInvariant();
        if (src != "off" && src != "microphone" && src != "loopback")
        {
            SendJson(clientSocket, new { ok = false, error = $"Unknown source '{src}'. Use 'off', 'microphone', or 'loopback'." });
            return;
        }

        if (src == "off")
        {
            SetAudioEnabled?.Invoke(false);
            SetAudioSource?.Invoke("off");
        }
        else
        {
            SetAudioSource?.Invoke(src);
            SetAudioEnabled?.Invoke(true);
        }

        Console.WriteLine($"[WebServer] Audio source set to: {src}");
        SendJson(clientSocket, new { ok = true, source = src, enabled = GetAudioEnabled?.Invoke() ?? false });
    }

    /// <summary>
    /// Parse and validate a /api/notify JSON body.
    /// Returns the request on success (null <paramref name="error"/>), or an error message.
    /// An empty "text" is valid and means "clear the active notification".
    /// </summary>
    public static NotificationRequest? ParseNotifyRequest(string body, out string? error)
    {
        error = null;

        Dictionary<string, JsonElement>? json;
        try { json = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body); }
        catch { json = null; }

        if (json == null || !json.TryGetValue("text", out var textEl) || textEl.ValueKind != JsonValueKind.String)
        {
            error = "Body must be JSON with a \"text\" string, e.g. {\"text\":\"Hello\",\"duration\":10}.";
            return null;
        }

        string text = textEl.GetString() ?? "";
        if (text.Length > TextRenderer.MaxTextLength)
        {
            error = $"text is limited to {TextRenderer.MaxTextLength} characters (got {text.Length}).";
            return null;
        }

        int duration = 10;
        if (json.TryGetValue("duration", out var durEl))
        {
            int? d = null;
            if (durEl.ValueKind == JsonValueKind.Number && durEl.TryGetInt32(out int nv)) d = nv;
            else if (durEl.ValueKind == JsonValueKind.String && int.TryParse(durEl.GetString(), out int sv)) d = sv;

            if (d == null) { error = "'duration' must be a whole number of seconds."; return null; }
            if (d is < 1 or > 300) { error = $"'duration' must be 1..300 seconds (got {d})."; return null; }
            duration = d.Value;
        }

        return new NotificationRequest(text, duration);
    }

    private void ServeNotify(Socket clientSocket, string body)
    {
        var request = ParseNotifyRequest(body, out string? error);
        if (request == null)
        {
            SendJson(clientSocket, new { ok = false, error });
            return;
        }

        PendingNotification = request;
        if (request.Text.Length == 0)
            SendJson(clientSocket, new { ok = true, cleared = true });
        else
            SendJson(clientSocket, new { ok = true, text = request.Text, duration = request.DurationSec });
    }

    private void ServeSetString(Socket clientSocket, string body)
    {
        Dictionary<string, JsonElement>? json;
        try { json = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body); }
        catch { json = null; }

        if (json == null || json.Count == 0)
        {
            SendJson(clientSocket, new { ok = false, error = "Invalid or empty JSON body." });
            return;
        }

        static int? GetIntValue(JsonElement e, out string? err)
        {
            err = null;
            if (e.ValueKind == JsonValueKind.Number)
            {
                if (e.TryGetInt32(out int nv)) return nv;
                err = "must be an integer";
                return null;
            }
            if (e.ValueKind == JsonValueKind.String && int.TryParse(e.GetString(), out int v)) return v;
            err = "must be an integer";
            return null;
        }

        const int MaxStringSize = 2000;
        bool? enabled = null;
        int? size = null, row = null, universe = null;
        string? ip = null;
        bool ipProvided = false;
        string? error = null;

        if (json.TryGetValue("enabled", out var e))
        {
            if (e.ValueKind == JsonValueKind.True) enabled = true;
            else if (e.ValueKind == JsonValueKind.False) enabled = false;
            else if (e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out int iv)) enabled = iv != 0;
            else if (e.ValueKind == JsonValueKind.Number) error = "'enabled' must be a boolean.";
            else if (e.ValueKind == JsonValueKind.String && bool.TryParse(e.GetString(), out bool b)) enabled = b;
            else error = "'enabled' must be a boolean.";
        }

        if (error == null && json.TryGetValue("size", out var sz))
        {
            size = GetIntValue(sz, out var err);
            error ??= err != null ? $"'size' {err}." : null;
            if (error == null && size.HasValue && (size.Value < 1 || size.Value > MaxStringSize))
                error = $"size must be 1..{MaxStringSize} (got {size.Value}).";
        }

        if (error == null && json.TryGetValue("row", out var r))
        {
            row = GetIntValue(r, out var err);
            error ??= err != null ? $"'row' {err}." : null;
            if (error == null && row.HasValue && (row.Value < 0 || row.Value >= PixelMapper.Height))
                error = $"row must be 0..{PixelMapper.Height - 1} (got {row.Value}).";
        }

        if (error == null && json.TryGetValue("ip", out var ipe))
        {
            if (ipe.ValueKind == JsonValueKind.String)
            {
                ip = ipe.GetString();
                ipProvided = true;
                if (!string.IsNullOrEmpty(ip) && !IPAddress.TryParse(ip, out _))
                {
                    // Not a literal IP: treat it as a hostname (e.g. mDNS 'ledstring.local').
                    // Verify it resolves now so a typo is reported here, not at sender startup.
                    bool resolved = false;
                    try { resolved = Dns.GetHostAddresses(ip).Length > 0; }
                    catch { resolved = false; }
                    if (!resolved)
                        error = $"'ip' is neither a valid IP address nor a resolvable hostname: '{ip}'. Leave it blank to target the matrix IP.";
                }
            }
            else if (ipe.ValueKind == JsonValueKind.Null)
            {
                ip = null; // null = leave unchanged
            }
            else error = "'ip' must be a string (or null to leave unchanged).";
        }

        if (error == null && json.TryGetValue("universe", out var u))
        {
            universe = GetIntValue(u, out var err);
            error ??= err != null ? $"'universe' {err}." : null;
            if (error == null && universe.HasValue && universe.Value != 0
                && (universe.Value < StringOutput.MinUniverse || universe.Value > StringOutput.MaxUniverse))
                error = $"universe must be 0 (auto) or {StringOutput.MinUniverse}..{StringOutput.MaxUniverse} (got {universe.Value}).";
        }

        if (error != null)
        {
            SendJson(clientSocket, new { ok = false, error });
            return;
        }

        if (!enabled.HasValue && !size.HasValue && !row.HasValue && !universe.HasValue && !ipProvided)
        {
            SendJson(clientSocket, new { ok = false, error = "No fields provided. Send enabled, size, row, ip, and/or universe." });
            return;
        }

        PendingStringChange = new StringConfigChange(enabled, size, row, ip, universe);
        string ipDesc = !ipProvided ? "unchanged" : (string.IsNullOrEmpty(ip) ? "(reset to matrix IP)" : ip);
        Console.WriteLine($"[WebServer] String config queued: enabled={enabled?.ToString() ?? "unchanged"} size={size?.ToString() ?? "unchanged"} row={row?.ToString() ?? "unchanged"} ip={ipDesc} universe={universe?.ToString() ?? "unchanged"}");
        SendJson(clientSocket, new { ok = true, message = "String configuration queued — applied on the next frame." });
    }

    private void ServeStringConfig(Socket clientSocket)
    {
        var live = GetLiveStringState?.Invoke();
        if (live == null)
        {
            live = new LiveStringState(
                false, 50, (PixelMapper.Height - 1) / 2, null, 0, 0, 0, PixelMapper.Height);
        }
        SendJson(clientSocket, live);
    }

    private void ServeShaderFile(Socket clientSocket, string path)
    {
        try
        {
            // Extract filename from /shaders/name.glsl
            string fileName = Uri.UnescapeDataString(path.TrimStart('/').Replace("/shaders/", ""));
            string? fullPath = null;

            foreach (var s in _shaders)
                if (s.FileName.EndsWith(fileName, StringComparison.OrdinalIgnoreCase)) { fullPath = s.FileName; break; }

            if (fullPath == null || !File.Exists(fullPath))
            {
                SendResponse(clientSocket, 404, "", "text/plain; charset=utf-8");
                return;
            }

            string source = File.ReadAllText(fullPath);
            SendResponse(clientSocket, 200, source, "text/plain; charset=utf-8");
        }
        catch
        {
            SendResponse(clientSocket, 500, "Internal Server Error", "text/plain; charset=utf-8");
        }
    }

    public void Stop()
    {
        _running = false;
        _listener?.Stop();
        _listener?.Dispose();
        _acceptThread?.Join(2000);
        Console.WriteLine("[WebServer] Stopped.");
    }

    /// <summary>
    /// Set live status snapshot from the render loop.
    /// </summary>
    public ApiStatus? StatusSnapshot
    {
        get => _statusSnapshot;
        set { _statusSnapshot = value; }
    }

    /// <summary>
    /// Re-scan shader directory (call when --shader-dir changes).
    /// </summary>
    public void RefreshShaderList()
    {
        CurrentShaders();
    }

    private void ServeStatus(Socket clientSocket)
    {
        if (_statusSnapshot != null)
        {
            var snap = _statusSnapshot;
            var devicesRaw = GetAvailableDevices?.Invoke() ?? snap.AvailableDevices;
            var devices = devicesRaw.Select(d => new { Index = d.Index, Name = d.Name }).ToList();
            var loopbackName = GetLoopbackDeviceName?.Invoke() ?? snap.LoopbackDeviceName;

            SendJson(clientSocket, new
            {
                snap.SelectedShader,
                snap.AudioEnabled,
                snap.AudioSource,
                snap.TotalShaders,
                snap.AudioReactiveNames,
                snap.UptimeSecs,
                snap.FramesSent,
                snap.SendErrors,
                LoopbackDeviceName = loopbackName,
                AvailableDevices = devices,
                snap.StringEnabled,
                snap.StringSize,
                snap.StringRow,
                snap.StringIp,
                snap.StringUniverse,
                snap.StringFramesSent,
                snap.StringSendErrors,
                snap.ActiveNotification,
                snap.NotificationRemainingSecs
            });
        }
        else
        {
            var devicesRaw = GetAvailableDevices?.Invoke() ?? new List<(int Index, string Name)>();
            var devices = devicesRaw.Select(d => new { Index = d.Index, Name = d.Name }).ToList();
            bool audioOn = GetAudioEnabled != null && GetAudioEnabled()!;
            var shaders = CurrentShaders();
            SendJson(clientSocket,
                new ApiStatus("off", audioOn, audioOn ? (GetCurrentAudioSource?.Invoke() ?? "microphone") : "off", shaders.Length,
                    shaders.Where(s => s.IsAudioReactive).Select(s => s.Name!).ToArray()!, 0, 0, 0,
                    GetLoopbackDeviceName?.Invoke(), devicesRaw,
                    false, 50, (PixelMapper.Height - 1) / 2, null, 0, 0, 0, null, 0));
        }
    }

    public void Dispose() => Stop();
}
