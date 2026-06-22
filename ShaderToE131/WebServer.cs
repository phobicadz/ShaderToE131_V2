using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShaderToE131;

/// <summary>
/// Lightweight HTTP web server for shader selection and audio control.
/// Uses HttpListener (no external dependencies). Runs on a configurable port.
/// </summary>
public sealed class WebServer : IDisposable
{
    private readonly int _port;
    private readonly string _shaderDirPath;
    private HttpListener? _listener;
    private Thread? _serverThread;
    private volatile bool _running = false;

    // Shared state — set by Program after construction.
    // Render loop polls these each frame (volatile for thread safety).
    private volatile string? _pendingShaderSource;
    private volatile string? _pendingShaderFileName;

    public string? PendingShaderSource { get => _pendingShaderSource; set => _pendingShaderSource = value; }
    public string? PendingShaderFileName { get => _pendingShaderFileName; set => _pendingShaderFileName = value; }
    public Func<bool>? GetAudioEnabled   { get; set; }              // current audio state
    public Action<bool>? SetAudioEnabled { get; set; }              // toggle audio on/off
    public Action<string>? SetAudioSource { get; set; }            // change audio source (microphone/loopback/off)
    public Func<string?>? GetCurrentAudioSource { get; set; }       // current audio source label
    public int CurrentDeviceIndex { get; set; }                     // currently selected audio device index
    public Action<int>? SetDeviceIndex { get; set; }                // change audio device index via web

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
        int SendErrors
    );

    private static readonly JsonSerializerOptions JsonCamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public WebServer(int port, string shaderDirPath)
    {
        _port = port;
        _shaderDirPath = Path.IsPathRooted(shaderDirPath) ? shaderDirPath : Path.Combine(Directory.GetCurrentDirectory(), shaderDirPath);
        LoadShaderList();
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
        var url = $"http://localhost:{_port}/";
        try
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add(url);
            _listener.Start();
            _serverThread = new Thread(ServerLoop) { IsBackground = true };
            _serverThread.Start();
            Console.WriteLine($"[WebServer] Started on {url}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WebServer] Failed to start: {ex.Message}");
            _running = false;
        }
    }

    private void ServerLoop()
    {
        while (_running && _listener?.IsListening == true)
        {
            try
            {
                var ctx = _listener.GetContext(); // blocks until request arrives
                Task.Run(() => HandleRequest(ctx));
            }
            catch (ObjectDisposedException) { break; }
            catch (InvalidOperationException) { break; }
        }
    }

    private void HandleRequest(HttpListenerContext context)
    {
        var req = context.Request;
        string path = req.Url?.AbsolutePath ?? "/";

        // CORS header for all responses
        context.Response.Headers.Add("Access-Control-Allow-Origin", "*");
        context.Response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
        context.Response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

        if (req.HttpMethod == "OPTIONS")
        {
            context.Response.StatusCode = 204;
            context.Response.ContentLength64 = 0;
            context.Response.Close();
            return;
        }

        try
        {
            switch (path)
            {
                case "/":
                    ServeIndex(context);
                    break;
                case "/api/shaders":
                    if (req.HttpMethod == "GET") ServeShadersList(context);
                    else context.Response.StatusCode = 405;
                    break;
                case "/api/select-shader":
                    if (req.HttpMethod == "POST") ServeSelectShader(context);
                    else context.Response.StatusCode = 405;
                    break;
                case "/api/set-audio":
                    if (req.HttpMethod == "POST") ServeSetAudio(context);
                    else context.Response.StatusCode = 405;
                    break;
                case "/api/status":
                    if (req.HttpMethod == "GET") ServeStatus(context);
                    else context.Response.StatusCode = 405;
                    break;
                default:
                    // Try to serve shader files directly for preview
                    if (path.StartsWith("/shaders/") && path.EndsWith(".glsl"))
                        ServeShaderFile(context, path);
                    else
                    {
                        context.Response.StatusCode = 404;
                        context.Response.ContentLength64 = 0;
                        context.Response.Close();
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WebServer] Error handling {path}: {ex.Message}");
            try
            {
                context.Response.StatusCode = 500;
                context.Response.ContentLength64 = 0;
                context.Response.Close();
            } catch { /* already closed */ }
        }
    }

    private void SendJson(HttpListenerResponse response, object data)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(data, JsonCamelCase);
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        response.OutputStream.Write(bytes, 0, bytes.Length);
        response.Close();
    }

    private void ServeIndex(HttpListenerContext ctx)
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

  <div class=""toggle-row"" style=""margin-top:20px;"">
    <div class=""toggle-switch"">
      <input type=""checkbox"" id=""audioToggle"">
      <span class=""toggle-slider""></span>
    </div>
    <label for=""audioToggle"" style=""margin-bottom:0"">Audio Reactive</label>
  </div>

  <button class=""primary"" id=""applyBtn"">Apply &amp; Restart Shader</button>

  <div class=""status-grid"">
    <div class=""status-item""><div class=""status-label"">Shader</div><div class=""status-value"" id=""stShader"">—</div></div>
    <div class=""status-item""><div class=""status-label"">Audio</div><div class=""status-value"" id=""stAudio"">—</div></div>
    <div class=""status-item""><div class=""status-label"">Shaders</div><div class=""status-value"" id=""stCount"">0</div></div>
    <div class=""status-item""><div class=""status-label"">Uptime</div><div class=""status-value"" id=""stUptime"">—</div></div>
  </div>

  <div class=""notice"">Auto-refreshes status every 2 s. Changes apply immediately.</div>
</div>

<script>
const API = '';
let audioReactiveNames = [];

async function loadShaders() {
  try {
    const r = await fetch(API + 'api/shaders');
    const data = await r.json();
    const sel = document.getElementById('shaderSelect');
    sel.innerHTML = '<option value=""off"">Off (blank)</option><option value="""" >— select shader —</option>';
    audioReactiveNames = (data.audioReactive || []).map(s => s.toLowerCase());
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
  const audioOn = document.getElementById('audioToggle').checked;
  if (!shaderName) return alert('Please select a shader.');
  try {
    await fetch(API + 'api/select-shader', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({name:shaderName}) });
    await fetch(API + 'api/set-audio',   { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({enabled:audioOn}) });
    document.getElementById('applyBtn').textContent = '✓ Applied!';
    setTimeout(() => document.getElementById('applyBtn').textContent = 'Apply & Restart Shader', 1500);
  } catch(e) { alert('Failed to apply changes.'); console.error(e); }
}

async function refreshStatus() {
  try {
    const r = await fetch(API + 'api/status');
    const d = await r.json();
    document.getElementById('stShader').textContent = d.selectedShader || '—';
    document.getElementById('stAudio').innerHTML = d.audioEnabled ? '<span style=color:#2ea043>ON</span>' : 'OFF';
    document.getElementById('stCount').textContent = d.totalShaders;
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
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(html);
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.Close();
    }

    private void ServeShadersList(HttpListenerContext ctx)
    {
        LoadShaderList(); // re-scan in case new files appeared
        var data = _shaderList.Select(s => new { s.Name, s.FileName, s.IsAudioReactive }).ToArray();
        SendJson(ctx.Response, new { shaders = data });
    }

    private void ServeSelectShader(HttpListenerContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
        string body = reader.ReadToEnd();
        var json = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(body);
        string? name = json?.GetValueOrDefault("name")?.ToString();

        if (string.IsNullOrEmpty(name))
        {
            SendJson(ctx.Response, new { ok = false, error = "Missing 'name' field." });
            return;
        }

        // Special: "off" renders a blank/black screen
        string lowerName = name?.ToLowerInvariant();
        if (lowerName == "off")
        {
            PendingShaderSource = null;  // signals render loop to skip shader rendering
            PendingShaderFileName = "Off";
            Console.WriteLine("[WebServer] Shader set to Off — blank screen.");
            SendJson(ctx.Response, new { ok = true, shader = "Off (blank)" });
            return;
        }

        // Search for the shader file by name
        string? fullPath = null;
        foreach (var s in _shaderList)
        {
            if (s.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || s.FileName.Contains(name))
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
            SendJson(ctx.Response, new { ok = false, error = $"Shader '{name}' not found." });
            return;
        }

        string source = File.ReadAllText(fullPath);
        string fileName = Path.GetFileNameWithoutExtension(fullPath);
        PendingShaderSource = source;
        PendingShaderFileName = fileName;
        Console.WriteLine($"[WebServer] Shader changed via web: {fileName}");
        SendJson(ctx.Response, new { ok = true, shader = Path.GetFileName(fullPath) });
    }

    private void ServeSetAudio(HttpListenerContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
        string body = reader.ReadToEnd();
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

        SendJson(ctx.Response, new { ok = true, enabled = enabled ?? GetAudioEnabled!() });
    }

    private void ServeShaderFile(HttpListenerContext ctx, string path)
    {
        try
        {
            // Extract filename from /shaders/name.glsl
            string fileName = Uri.UnescapeDataString(path.TrimStart('/').Replace("/shaders/", ""));
            string? fullPath = null;

            foreach (var s in _shaderList)
                if (s.FileName.EndsWith(fileName, StringComparison.OrdinalIgnoreCase)) { fullPath = s.FileName; break; }

            if (fullPath == null || !File.Exists(fullPath))
            { ctx.Response.StatusCode = 404; ctx.Response.ContentLength64 = 0; ctx.Response.Close(); return; }

            byte[] data = File.ReadAllBytes(fullPath);
            ctx.Response.ContentType = "text/plain";
            ctx.Response.ContentLength64 = data.Length;
            ctx.Response.OutputStream.Write(data, 0, data.Length);
            ctx.Response.Close();
        }
        catch { ctx.Response.StatusCode = 500; ctx.Response.ContentLength64 = 0; ctx.Response.Close(); }
    }

    public void Stop()
    {
        _running = false;
        _listener?.Stop();
        _listener?.Close();
        _serverThread?.Join(2000);
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

    private void ServeStatus(HttpListenerContext ctx)
    {
        if (_statusSnapshot != null)
            SendJson(ctx.Response, _statusSnapshot);
        else
            SendJson(ctx.Response,
                new ApiStatus("—", (GetAudioEnabled != null ? GetAudioEnabled() : false), _shaderList.Count,
                    _shaderList.Where(s => s.IsAudioReactive).Select(s=>s.Name!).ToArray()!, 0, 0, 0));
    }

    public void Dispose() => Stop();
}
