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

    // ─── LED string output state ───
    // A pending config change posted by the web UI; consumed (and cleared) by the
    // render loop once it has been applied on the GL thread.
    private volatile StringConfigChange? _pendingStringChange;
    public StringConfigChange? PendingStringChange { get => _pendingStringChange; set => _pendingStringChange = value; }
    public Func<LiveStringState>? GetLiveStringState { get; set; }  // current string config + live stats

    private readonly List<ShaderInfo> _shaderList = new();
    internal IReadOnlyList<ShaderInfo> Shaders => _shaderList;
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
        int StringSendErrors
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
        string html = @"
<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""UTF-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
<title>ShaderToE131 — Control Panel</title>
<style>
  * { margin: 0; padding: 0; box-sizing: border-box; }
  body { font-family: 'Segoe UI', system-ui, sans-serif; background: #0d1117; color: #c9d1d9; min-height: 100vh; display: flex; justify-content: center; align-items: flex-start; padding-top: 40px; }
  .card { background: #161b22; border: 1px solid #30363d; border-radius: 12px; padding: 28px; width: 580px; max-width: 95vw; box-shadow: 0 8px 32px rgba(0,0,0,.4); }
  h1 { font-size: 1.6rem; margin-bottom: 4px; color: #58a6ff; }
  .subtitle { font-size: 0.85rem; color: #8b949e; margin-bottom: 20px; }
  .section { margin-bottom: 20px; padding-bottom: 18px; border-bottom: 1px solid #21262d; }
  .section:last-of-type { border-bottom: none; margin-bottom: 0; }
  h2 { font-size: 0.75rem; text-transform: uppercase; letter-spacing: 1px; color: #8b949e; margin-bottom: 12px; }
  label { display: block; font-weight: 600; font-size: 0.85rem; margin-bottom: 6px; color: #c9d1d9; }
  select, input[type=number], input[type=text], button { width: 100%; padding: 9px 12px; border-radius: 8px; font-size: 0.9rem; border: 1px solid #30363d; background: #21262d; color: #c9d1d9; }
  select:focus, input:focus, button:focus { outline: none; border-color: #58a6ff; box-shadow: 0 0 0 3px rgba(88,166,255,.15); }
  select:hover, button:hover { background: #292e36; }
  select:disabled { opacity: 0.45; cursor: not-allowed; }
  button { cursor: pointer; margin-top: 10px; }
  button.primary { background: #238636; border-color: #2ea043; color: #fff; font-weight: 600; }
  button.primary:hover { background: #2ea043; }
  .grid2 { display: grid; grid-template-columns: 1fr 1fr; gap: 12px; }
  .toggle-row { display: flex; align-items: center; gap: 12px; margin-bottom: 14px; }
  .toggle-row .desc { font-size: 0.85rem; color: #8b949e; }
  .toggle-switch { position: relative; width: 52px; height: 28px; flex-shrink: 0; display: inline-block; }
  .toggle-switch input { opacity: 0; width: 0; height: 0; }
  .toggle-slider { position: absolute; inset: 0; background: #30363d; border-radius: 14px; cursor: pointer; transition: .2s; }
  .toggle-slider::before { content: ''; position: absolute; width: 22px; height: 22px; left: 3px; top: 3px; background: #c9d1d9; border-radius: 50%; transition: .2s; }
  .toggle-switch input:checked + .toggle-slider { background: #238636; }
  .toggle-switch input:checked + .toggle-slider::before { transform: translateX(24px); }
  .banner { border-radius: 8px; padding: 10px 14px; font-size: 0.85rem; margin-bottom: 16px; }
  .banner.ok { background: rgba(35,134,54,.15); border: 1px solid #238636; color: #3fb950; }
  .banner.error { background: rgba(218,54,51,.15); border: 1px solid #da3633; color: #f85149; }
  .hint { font-size: 0.75rem; color: #8b949e; margin-top: 8px; }
  .status-grid { display: grid; grid-template-columns: repeat(3, 1fr); gap: 10px; }
  .status-item { background: #0d1117; border-radius: 8px; padding: 10px 12px; text-align: center; }
  .status-label { font-size: 0.65rem; text-transform: uppercase; letter-spacing: .5px; color: #8b949e; margin-bottom: 2px; }
  .status-value { font-size: 0.9rem; font-weight: 600; color: #58a6ff; word-break: break-all; }
  .notice { font-size: 0.75rem; color: #8b949e; margin-top: 14px; text-align: center; }
</style>
</head>
<body>
<div class=""card"">
  <h1>ShaderToE131</h1>
  <div class=""subtitle"">LED Matrix Shader Control Panel</div>

  <div id=""banner"" class=""banner ok"" style=""display:none""></div>

  <div class=""section"">
    <h2>Shader</h2>
    <label for=""shaderSelect"">Shader</label>
    <select id=""shaderSelect""><option value=""off"">Off (blank)</option><option value="""">— select shader —</option></select>
    <button class=""primary"" id=""applyShaderBtn"">Apply Shader</button>
  </div>

  <div class=""section"">
    <h2>Audio</h2>
    <div class=""grid2"">
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
  </div>

  <div class=""section"">
    <h2>LED String</h2>
    <div class=""toggle-row"">
      <label class=""toggle-switch"">
        <input type=""checkbox"" id=""stringEnabled"" aria-labelledby=""stringEnabledDesc"">
        <span class=""toggle-slider""></span>
      </label>
      <span class=""desc"" id=""stringEnabledDesc"">Stream an LED string in parallel with the matrix (mirrors a matrix row)</span>
    </div>
    <div class=""grid2"">
      <div>
        <label for=""stringSize"">LED Count</label>
        <input type=""number"" id=""stringSize"" min=""1"" max=""2000"" placeholder=""50"">
      </div>
      <div>
        <label for=""stringRow"">Matrix Row</label>
        <input type=""number"" id=""stringRow"" min=""0"" max=""10"" placeholder=""5 (center)"">
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
  </div>

  <div class=""section"">
    <h2>Status</h2>
    <div class=""status-grid"">
      <div class=""status-item""><div class=""status-label"">Shader</div><div class=""status-value"" id=""stShader"">—</div></div>
      <div class=""status-item""><div class=""status-label"">Audio</div><div class=""status-value"" id=""stAudio"">—</div></div>
      <div class=""status-item""><div class=""status-label"">String</div><div class=""status-value"" id=""stString"">—</div></div>
      <div class=""status-item""><div class=""status-label"">Uptime</div><div class=""status-value"" id=""stUptime"">—</div></div>
      <div class=""status-item""><div class=""status-label"">Matrix Frames</div><div class=""status-value"" id=""stFrames"">0</div></div>
      <div class=""status-item""><div class=""status-label"">Send Errors</div><div class=""status-value"" id=""stErrors"">0</div></div>
    </div>
  </div>

  <div class=""notice"">Status auto-refreshes every 2 s. Changes apply immediately.</div>
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

// ── Shader ───────────────────────────────────────────────────────
async function loadShaders() {
  try {
    const r = await fetch(API + 'api/shaders');
    const data = await r.json();
    const sel = $('shaderSelect');
    const prev = sel.value;
    sel.innerHTML = '';
    sel.add(new Option('Off (blank)', 'off'));
    sel.add(new Option('— select shader —', ''));
    for (const s of data.shaders) sel.add(new Option(s.name + (s.isAudioReactive ? ' 🔊' : ''), s.name));
    if (prev !== '' && [...sel.options].some(o => o.value === prev)) sel.value = prev;
  } catch (e) { console.error('Failed to load shaders', e); }
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

// ── Status ───────────────────────────────────────────────────────
async function refreshStatus() {
  try {
    const r = await fetch(API + 'api/status');
    const d = await r.json();
    $('stShader').textContent = d.selectedShader || '—';
    syncAudioUi(d);

    // Keep the shader dropdown in sync with the server without clobbering a
    // shader (or 'off') the user just picked but hasn't applied yet. We only
    // sync when the dropdown still matches what the server reported on the
    // previous poll (or on the first poll); otherwise the user is staging a
    // manual selection and we leave it alone.
    const sel = $('shaderSelect');
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

    const s = Math.max(0, Math.floor(d.uptimeSecs || 0));
    const h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60), sec = s % 60;
    $('stUptime').textContent = h + 'h ' + m.toString().padStart(2, '0') + 'm ' + sec + 's';
  } catch (e) { /* ignore transient errors */ }
}

// ── Wire up ──────────────────────────────────────────────────────
$('applyShaderBtn').addEventListener('click', applyShader);
$('applyStringBtn').addEventListener('click', applyString);
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

        // Search for the shader file by name. Match exactly on the display name
        // (filename without extension) or the full filename — never a substring,
        // so "fire" won't accidentally select "fireworks.glsl".
        string? fullPath = null;
        foreach (var s in _shaderList)
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
        var devicesRaw = GetAvailableDevices?.Invoke() ?? new List<(int Index, string Name)>();

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
        get => _statusSnapshot;
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
        {
            var snap = _statusSnapshot;
            var devicesRaw = GetAvailableDevices?.Invoke() ?? snap!.AvailableDevices;
            var devices = devicesRaw.Select(d => new { Index = d.Index, Name = d.Name }).ToList();
            var loopbackName = GetLoopbackDeviceName?.Invoke() ?? snap!.LoopbackDeviceName;

            SendJson(clientSocket, new
            {
                snap!.SelectedShader,
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
                snap.StringSendErrors
            });
        }
        else
        {
            var devicesRaw = GetAvailableDevices?.Invoke() ?? new List<(int Index, string Name)>();
            var devices = devicesRaw.Select(d => new { Index = d.Index, Name = d.Name }).ToList();
            bool audioOn = GetAudioEnabled != null && GetAudioEnabled()!;
            SendJson(clientSocket,
                new ApiStatus("off", audioOn, audioOn ? (GetCurrentAudioSource?.Invoke() ?? "microphone") : "off", _shaderList.Count,
                    _shaderList.Where(s => s.IsAudioReactive).Select(s => s.Name!).ToArray()!, 0, 0, 0,
                    GetLoopbackDeviceName?.Invoke(), devicesRaw,
                    false, 50, (PixelMapper.Height - 1) / 2, null, 0, 0, 0));
        }
    }

    public void Dispose() => Stop();
}
