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

    private readonly List<ShaderInfo> _shaderList = new();
    internal IReadOnlyList<ShaderInfo> Shaders => _shaderList;
    private volatile ApiStatus? _statusSnapshot;

    public record ShaderInfo(string Name, string FileName, bool IsAudioReactive);
    public record ApiStatus(
        string SelectedShader,
        bool AudioEnabled,
        int TotalShaders,
        string[] AudioReactiveNames,
        double UptimeSecs,
        int FramesSent,
        int SendErrors,
        string? LoopbackDeviceName
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

    private void LoadShaderList()
    {
        _shaderList.Clear();
        if (!Directory.Exists(_shaderDirPath)) return;

        foreach (var file in Directory.GetFiles(_shaderDirPath, "*.glsl", SearchOption.TopDirectoryOnly)
                                        .OrderBy(Path.GetFileName))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            bool isAudioReactive = IsAudioReactiveShader(file);
            _shaderList.Add(new ShaderInfo(name, file, isAudioReactive));
        }
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
            try { clientSocket.Shutdown(SocketShutdown.Both); } catch { }
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

        try
        {
            clientSocket.Send(headerBytes, SocketFlags.None);
            if (bodyBytes.Length > 0)
                clientSocket.Send(bodyBytes, SocketFlags.None);
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
        string html = @"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""UTF-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
<title>ShaderToE131 — Control Panel</title>
<style>
  * { margin: 0; padding: 0; box-sizing: border-box; }
  body { font-family: 'Segoe UI', system-ui, sans-serif; background: #0d1117; color: #c9d1d9; min-height: 100vh; display: flex; justify-content: center; align-items: flex-start; padding-top: 40px; }
  .card { background: #161b22; border: 1px solid #30363d; border-radius: 12px; padding: 32px; width: 520px; max-width: 95vw; box-shadow: 0 8px 32px rgba(0,0,0,.4); }
  h1 { font-size: 1.6rem; margin-bottom: 4px; color: #58a6ff; }
  .subtitle { font-size: 0.85rem; color: #8b949e; margin-bottom: 24px; }
  label { display: block; font-weight: 600; font-size: 0.9rem; margin-bottom: 6px; color: #c9d1d9; }
  select, button { width: 100%; padding: 10px 14px; border-radius: 8px; font-size: 0.95rem; border: 1px solid #30363d; background: #21262d; color: #c9d1d9; cursor: pointer; }
  select:focus, button:focus { outline: none; border-color: #58a6ff; box-shadow: 0 0 0 3px rgba(88,166,255,.15); }
  select:hover, button:hover { background: #292e36; }
  button.primary { background: #238636; border-color: #2ea043; color: #fff; font-weight: 600; }
  button.primary:hover { background: #2ea043; }
  .toggle-row { display: flex; align-items: center; gap: 14px; margin-bottom: 20px; }
  .toggle-switch { position: relative; width: 52px; height: 28px; flex-shrink: 0; }
  .toggle-switch input { opacity: 0; width: 0; height: 0; }
  .toggle-slider { position: absolute; inset: 0; background: #30363d; border-radius: 14px; cursor: pointer; transition: .2s; }
  .toggle-slider::before { content: ''; position: absolute; width: 22px; height: 22px; left: 3px; top: 3px; background: #c9d1d9; border-radius: 50%; transition: .2s; }
  .toggle-switch input:checked + .toggle-slider { background: #238636; }
  .toggle-switch input:checked + .toggle-slider::before { transform: translateX(24px); }
  .status-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(120px, 1fr)); gap: 10px; margin-top: 20px; padding-top: 20px; border-top: 1px solid #21262d; }
  .status-item { background: #0d1117; border-radius: 8px; padding: 10px 14px; text-align: center; }
  .status-label { font-size: 0.7rem; text-transform: uppercase; letter-spacing: .5px; color: #8b949e; margin-bottom: 2px; }
  .status-value { font-size: 1.05rem; font-weight: 600; color: #58a6ff; word-break: break-all; }
  .audio-badge { display: inline-block; background: #da3633; color: #fff; font-size: 0.65rem; padding: 2px 7px; border-radius: 4px; margin-left: 8px; vertical-align: middle; }
  .notice { font-size: 0.8rem; color: #8b949e; margin-top: 12px; text-align: center; }
</style>
</head>
<body>
<div class=""card"">
  <h1>ShaderToE131</h1>
  <div class=""subtitle"">LED Matrix Shader Control Panel</div>

  <label for=""shaderSelect"">Shader</label>
  <select id=""shaderSelect""><option value=""off"">Off (blank)</option><option value="""" >Loading shaders…</option></select>

  <button class=""primary"" id=""applyBtn"" style=""margin-top:12px"">Apply &amp; Restart Shader</button>

  <div class=""status-grid"">
    <div class=""status-item""><div class=""status-label"">Shader</div><div class=""status-value"" id=""stShader"">—</div></div>
    <div class=""status-item""><div class=""status-label"">Shaders</div><div class=""status-value"" id=""stCount"">0</div></div>
    <div class=""status-item""><div class=""status-label"">Loopback Device</div><div class=""status-value"" id=""stLoopback"">—</div></div>

    <div class=""status-item""><div class=""status-label"">Uptime</div><div class=""status-value"" id=""stUptime"">—</div></div>
  </div>

  <div class=""notice"">Auto-refreshes status every 2 s. Changes apply immediately.</div>
</div>

<script>
const API = '';

async function loadShaders() {
  try {
    const r = await fetch(API + 'api/shaders');
    const data = await r.json();
    const sel = document.getElementById('shaderSelect');
    sel.innerHTML = '<option value=""off"">Off (blank)</option><option value="""" >— select shader —</option>';
    for (const s of data.shaders) {
      const opt = document.createElement('option');
      opt.value = s.name;
      opt.textContent = s.name + (s.isAudioReactive ? ' 🔊' : '');
      sel.appendChild(opt);
    }
  } catch(e) { console.error('Failed to load shaders', e); }
}

async function apply() {
  const shaderName = document.getElementById('shaderSelect').value;
  if (!shaderName) return alert('Please select a shader.');
  try {
    await fetch(API + 'api/select-shader', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({name:shaderName}) });
    document.getElementById('applyBtn').textContent = '✓ Applied!';
    setTimeout(() => document.getElementById('applyBtn').textContent = 'Apply & Restart Shader', 1500);
  } catch(e) { alert('Failed to apply changes.'); console.error(e); }
}

async function refreshStatus() {
  try {
    const r = await fetch(API + 'api/status');
    const d = await r.json();
    document.getElementById('stShader').textContent = d.selectedShader || '—';
    document.getElementById('stCount').textContent = d.totalShaders;
    document.getElementById('stLoopback').textContent = d.loopbackDeviceName || '—';
    const s = Math.floor(d.uptimeSecs);
    const h = Math.floor(s/3600), m = Math.floor((s%3600)/60), sec = s%60;
    document.getElementById('stUptime').textContent = `${h}h ${m.toString().padStart(2,'0')}m ${sec}s`;
  } catch(e) { /* ignore transient errors */ }
}

document.getElementById('applyBtn').addEventListener('click', apply);
loadShaders();
refreshStatus();
setInterval(refreshStatus, 2000);
</script>
</body>
</html>";
        SendResponse(clientSocket, 200, html, "text/html; charset=utf-8");
    }

    private void ServeShadersList(Socket clientSocket)
    {
        LoadShaderList(); // re-scan in case new files appeared
        var data = _shaderList.Select(s => new { s.Name, s.FileName, s.IsAudioReactive }).ToArray();
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

        // Search for the shader file by name
        string? fullPath = null;
        foreach (var s in _shaderList)
        {
            if (s.Name.Equals(name!, StringComparison.OrdinalIgnoreCase) || s.FileName.Contains(name!))
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
        }

        if (enabled.HasValue)
        {
            SetAudioEnabled?.Invoke(enabled.Value);
            Console.WriteLine($"[WebServer] Audio set to: {enabled}");
        }

        SendJson(clientSocket, new { ok = true, enabled = enabled ?? GetAudioEnabled!() });
    }

    private void ServeShaderFile(Socket clientSocket, string path)
    {
        try
        {
            // Extract filename from /shaders/name.glsl
            string fileName = Uri.UnescapeDataString(path.TrimStart('/').Replace("/shaders/", ""));
            string? fullPath = null;

            foreach (var s in _shaderList)
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
        set { _statusSnapshot = value; }
    }

    /// <summary>
    /// Re-scan shader directory (call when --shader-dir changes).
    /// </summary>
    public void RefreshShaderList()
    {
        LoadShaderList();
    }

    private void ServeStatus(Socket clientSocket)
    {
        if (_statusSnapshot != null)
            SendJson(clientSocket, _statusSnapshot);
        else
            SendJson(clientSocket,
                new ApiStatus("—", (GetAudioEnabled != null ? GetAudioEnabled() : false), _shaderList.Count,
                    _shaderList.Where(s => s.IsAudioReactive).Select(s => s.Name!).ToArray()!, 0, 0, 0,
                    GetLoopbackDeviceName?.Invoke()));
    }

    public void Dispose() => Stop();
}
