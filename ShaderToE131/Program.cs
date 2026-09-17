using System.Runtime.InteropServices;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace ShaderToE131;

/// <summary>
/// ShaderToy → OpenGL renderer with E.1.31 output to a 53×11 LED matrix.
/// </summary>
class Program : IDisposable
{
    private const string TargetIp = "192.168.2.150";
    private const int MatW = PixelMapper.Width;     // 53
    private const int MatH = PixelMapper.Height;    // 11
    private const ushort UniverseId = 1;            // sACN universe (valid range: 1..63999)

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void SwapIntervalFn(int interval);

    private bool _noPreview = false;
    private string? _shaderSource = null;
    private string? _currentShaderFileName;
    private bool _demoMode = false;
    private double _demoTimePerShaderSec = 10.0;
    private string? _shaderDirPath = null;
    private bool _audioEnabled = false;
    private AudioCapture.AudioSource _audioSource = AudioCapture.AudioSource.Microphone;
    private int _audioDeviceIndex = 0;
    private AudioCapture? _audioCapture;
    private string? _selectedLoopbackDeviceName;
    private int _webPort = 8080;
    private string _webBindAddress = "0.0.0.0";
    private WebServer? _webServer;

    private IWindow? _window;
    private GL? _gl;
    private E131Sender? _sender;
    private ShaderProgram? _shaderProgram;
    private byte[] _frameBuffer = new byte[MatW * MatH * 4];
    private byte[] _e131Buffer = new byte[PixelMapper.TotalChannels];
    private long _webStartMs;
    private int _frameCount = 0;
    private int _audioDebugCount = 0;
    private int _demoIndex = -1;            // current shader index in demo mode
    private long _demoShaderStartTimeMs;     // tick when current shader started
    private string[]? _demoShaders;          // resolved paths for all shaders
    private long _lastStatusLogMs;
    private int _framesSent;
    private int _sendErrors;

    // ─── LED string output (in parallel with the matrix) ───
    private bool _stringEnabled = false;
    private int _stringSize = 50;          // LED count
    private int _stringRow = -1;           // -1 = center row of the matrix
    private string? _stringIp = null;      // null = same IP as the matrix
    private int _stringUniverse = 0;       // 0 = auto (first universe after the matrix's)
    private E131Sender? _stringSender;
    private byte[] _stringBuffer = Array.Empty<byte>();
    private int _stringUniverseResolved;
    private int _stringRowResolved;
    private int _stringFramesSent;
    private int _stringSendErrors;
    // Thread-safe hand-off from the web server's single volatile slot to the render
    // thread, so a change arriving between a read and clear is never lost.
    private readonly System.Collections.Concurrent.ConcurrentQueue<WebServer.StringConfigChange> _pendingStringChanges = new();

    private static readonly string[] AudioUniformNames =
        { "u_bass", "u_lowmid", "u_mid", "u_highmid", "u_treble", "u_volume" };

    /// <summary>
    /// Returns the audio uniform declarations missing from <paramref name="source"/>,
    /// joined by newlines (empty string if none are missing).
    /// </summary>
    private static string GetMissingAudioUniforms(string source)
    {
        var missing = new List<string>();
        foreach (string name in AudioUniformNames)
        {
            if (!source.Contains($"uniform float {name};"))
                missing.Add($"uniform float {name};");
        }
        return string.Join("\n", missing);
    }

    /// <summary>
    /// Build the final GLSL fragment shader source.
    /// Wraps raw ShaderToy-style mainImage code with required boilerplate,
    /// or returns the built-in default if no custom shader was provided.
    /// </summary>
    private string BuildFragmentShader(string rawSource)
    {
        string arValue = PixelMapper.AspectRatio.ToString();

        // A shader is "complete" only if it provides its own entry point (void main()).
        // ShaderToy-style shaders define mainImage(...) and rely on us adding void main().
        // Keying on void main() (rather than 'out vec4 FragColor') avoids misclassifying
        // ShaderToy shaders that declare their own output but have no entry point.
        bool hasMain = rawSource.Contains("void main()");

        if (hasMain)
        {
            // Complete GLSL fragment shader — inject audio uniforms if needed, then replace {AR}
            string result = rawSource;
            if (_audioEnabled)
            {
                // Inject only the audio uniforms the shader doesn't already declare,
                // after the 'out vec4 FragColor' line (with semicolon)
                string missing = GetMissingAudioUniforms(result);
                if (missing.Length > 0)
                {
                    string injectPoint = "out vec4 FragColor;";
                    int idx = result.IndexOf(injectPoint);
                    if (idx >= 0)
                    {
                        int insertPos = idx + injectPoint.Length;
                        result = result.Insert(insertPos, "\n" + missing);
                        Console.WriteLine("[BuildFragmentShader] Injected missing audio uniforms into complete shader.");
                    }
                }
            }
            return result.Replace("{AR}", arValue);
        }

        // ShaderToy-style source (mainImage, no void main()) — wrap with required boilerplate.
        // Common ShaderToy built-in helpers that aren't in standard GLSL.
        // Injected once at the top so all wrapped shaders can use them.
        string shaderToyHelpers = @"
vec3 HSVtoRGB(vec3 c)
{{
    vec4 K = vec4(1.0, 2.0 / 3.0, 1.0 / 3.0, 3.0);
    vec3 p = abs(fract(c.xxx + K.xyz) * 6.0 - K.www);
    return c.z * mix(K.xxx, clamp(p - K.xxx, 0.0, 1.0), c.y);
}}
// Alias for shaders that call it PascalCase
#define HSVToRGB HSVtoRGB
";

        // GLSL requires #version to be the first line of the shader. If the source
        // already declares a version, hoist that line to the top of the header and
        // remove it from the body so it isn't duplicated; otherwise default to
        // #version 330 core.
        string body = rawSource;
        string versionDirective = "#version 330 core";
        int versionIdx = rawSource.IndexOf("#version", StringComparison.Ordinal);
        if (versionIdx >= 0)
        {
            int lineStart = versionIdx;
            while (lineStart > 0 && rawSource[lineStart - 1] != '\n' && rawSource[lineStart - 1] != '\r')
                lineStart--;
            int lineEnd = rawSource.IndexOfAny(new[] { '\n', '\r' }, versionIdx);
            int lineLength = lineEnd >= 0 ? lineEnd - lineStart : rawSource.Length - lineStart;
            versionDirective = rawSource.Substring(lineStart, lineLength).TrimEnd();
            body = rawSource.Remove(lineStart, lineLength);
        }

        // Only add directives the source doesn't already declare, to avoid duplicate definitions.
        string header = versionDirective + "\n"
            + (rawSource.Contains("#define iTime") ? "" : "#define iTime u_time\n")
            + (rawSource.Contains("#define iResolution") ? "" : "#define iResolution u_resolution\n")
            + (rawSource.Contains("uniform float u_time") ? "" : "uniform float u_time;\n")
            + (rawSource.Contains("uniform vec2  u_resolution") || rawSource.Contains("uniform vec2 u_resolution") ? "" : "uniform vec2  u_resolution;\n")
            + (rawSource.Contains("uniform int   u_frame") || rawSource.Contains("uniform int u_frame") ? "" : "uniform int   u_frame;\n")
            + (rawSource.Contains("out vec4 FragColor") ? "" : "out vec4 FragColor;\n")
            + (_audioEnabled ? GetMissingAudioUniforms(rawSource) : "");

        string wrapped = header + shaderToyHelpers + body + @"
void main()
{{
    vec2 fragCoord = gl_FragCoord.xy;
    mainImage(FragColor, fragCoord);
}};";
        return wrapped.Replace("{AR}", arValue);
    }

    /// <summary>
    /// Built-in default ShaderToy fragment shader (used when no --shader file is specified).
    /// </summary>
    private const string DefaultFragmentShader = @"#version 330 core
#define iTime u_time
#define iResolution u_resolution
uniform float u_time;
uniform vec2  u_resolution;
uniform int   u_frame;
out vec4 FragColor;

float GetCircle(vec2 uv, vec2 position, float radius)
{{
    float dist = distance(position, uv);
    dist =  smoothstep(dist - 1.2, dist, radius);
    return dist * dist * dist;
}}

void main()
{{
    vec2 fragCoord = gl_FragCoord.xy;
    vec2 uv = vec2(fragCoord.xy - 0.5 * iResolution.xy) / iResolution.y;

    float pixel = 0.;

    vec3 positions[8];
    float Time = iTime / 2.;
    positions[0] = vec3(tan(Time * 1.4) * 1.3, cos(iTime * 2.3) * 0.4, 1.22);
    positions[1] = vec3(tan(Time * 3.0) * 1.0, cos(iTime * 1.3) * 0.6, 0.12);
    positions[2] = vec3(tan(Time * 2.1) * 1.5, cos(iTime * 1.9) * 0.8, 0.4);
    positions[3] = vec3(tan(Time * 1.1) * 1.1, cos(iTime * 2.6) * 0.7, 0.15);
    positions[4] = vec3(tan(Time * 1.8) * 1.1, cos(iTime * 2.1) * 0.5, 0.25);
    positions[5] = vec3(tan(Time * 1.1) * 1.2, cos(iTime * 1.3) * 0.2, 0.15);
    positions[6] = vec3(tan(Time * 1.7) * 1.4, cos(iTime * 2.4) * 0.3, 0.11);
    positions[7] = vec3(tan(Time * 2.8) * 1.5, cos(iTime * 1.1) * 0.4, 0.21);

    for	(int i = 0; i < 8; i++)
        pixel += GetCircle(uv, positions[i].xy, positions[i].z);

    pixel = smoothstep(.8, 1., pixel) * smoothstep(1.5, .9, pixel);

    vec3 col = 0.5 + 0.5*cos(iTime+uv.xyx+vec3(0,2,4));
    FragColor = vec4(vec3(pixel) * col, 1.0);
}};";
    static void Main(string[] args)
    {
        using var prog = new Program();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--no-preview") { prog._noPreview = true; }
            else if (args[i] == "--shader" && i + 1 < args.Length)
                prog._shaderSource = args[++i];
            else if (args[i] == "--demo") { prog._demoMode = true; }
            else if (args[i] == "--demo-time" && i + 1 < args.Length && double.TryParse(args[++i], out var secs))
                prog._demoTimePerShaderSec = secs;
            else if (args[i] == "--shader-dir" && i + 1 < args.Length)
                prog._shaderDirPath = args[++i];
            else if (args[i] == "--audio") { prog._audioEnabled = true; }
            else if ((args[i] == "--mic" || args[i] == "--microphone") && i + 1 < args.Length && int.TryParse(args[++i], out var micIdx))
            { prog._audioSource = AudioCapture.AudioSource.Microphone; prog._audioDeviceIndex = micIdx; }
            else if (args[i] == "--loopback" || args[i] == "--playback")
            {
                prog._audioSource = AudioCapture.AudioSource.Loopback;
                prog._audioDeviceIndex = 0;
                if (i + 1 < args.Length && int.TryParse(args[i + 1], out var lbIdx))
                { prog._audioDeviceIndex = lbIdx; i++; }
            }
            else if (args[i] == "--audio-device" && i + 1 < args.Length && int.TryParse(args[++i], out var devIdx))
                prog._audioDeviceIndex = devIdx;
            else if (args[i] == "--web-port" && i + 1 < args.Length && int.TryParse(args[++i], out var wp))
                prog._webPort = wp;
            else if (args[i] == "--bind-address" && i + 1 < args.Length)
                prog._webBindAddress = args[++i];
            else if (args[i] == "--string") { prog._stringEnabled = true; }
            else if (args[i] == "--string-size" && i + 1 < args.Length && int.TryParse(args[++i], out var ss))
                prog._stringSize = ss;
            else if (args[i] == "--string-row" && i + 1 < args.Length && int.TryParse(args[++i], out var sr))
                prog._stringRow = sr;
            else if (args[i] == "--string-ip" && i + 1 < args.Length)
                prog._stringIp = args[++i];
            else if (args[i] == "--string-universe" && i + 1 < args.Length && int.TryParse(args[++i], out var su))
                prog._stringUniverse = su;
        }

        // Show available audio devices when --help, -h, or --list-devices is passed
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine("Usage: ShaderToE131 [options]");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  --no-preview         Run headless (no OpenGL window)");
            Console.WriteLine("  --shader <file>      Path to a .glsl shader file");
            Console.WriteLine("  --demo               Cycle through all shaders in shaders/");
            Console.WriteLine("  --demo-time <secs>   Seconds per shader in demo mode (default: 10)");
            Console.WriteLine("  --shader-dir <path>  Directory containing .glsl shaders");
            Console.WriteLine("  --audio              Enable audio reactivity (microphone by default)");
            Console.WriteLine("  --mic <idx>          Use microphone at given index (default: 0)");
            Console.WriteLine("  --loopback [idx]     Capture system playback output (default: 0 = default speakers)");
            Console.WriteLine("  --audio-device <idx> Fallback device index for either source");
            Console.WriteLine("  --web-port <port>      Start web control panel on given port (default: 8080)");
            Console.WriteLine("  --bind-address <addr>  Bind address for web server (default: 0.0.0.0; use localhost for local-only)");
            Console.WriteLine("  --string               Also stream an LED string in parallel with the matrix");
            Console.WriteLine("  --string-size <n>      LED string length (default: 50)");
            Console.WriteLine("  --string-row <y>       Matrix row to sample the string from (default: center row)");
            Console.WriteLine("  --string-ip <ip>       Target IP for the string (default: same as matrix)");
            Console.WriteLine("  --string-universe <n>  Universe for the string (default: first after the matrix's universes)");
            Console.WriteLine("  --list-devices         List available audio input devices and exit");
            Console.WriteLine();
            return;
        }

        if (args.Contains("--list-devices"))
        {
            AudioCapture.ListAllDevices();
            return;
        }

        prog.Run();
    }

    /// <summary>
    /// Check if a GLSL file contains audio-reactive uniforms.
    /// </summary>
    private static bool IsAudioReactiveFile(string filePath)
    {
        try
        {
            string src = File.ReadAllText(filePath);
            return src.Contains("u_bass") || src.Contains("u_lowmid") || src.Contains("u_mid")
                || src.Contains("u_highmid") || src.Contains("u_treble") || src.Contains("u_volume");
        }
        catch { return false; }
    }

    /// <summary>
    /// Resolve a shader filename to an absolute path.
    /// Searches: current directory, shaders/ subfolder, and app base directory.
    /// </summary>
    private static string? ResolveShaderPath(string name)
    {
        // If already an absolute path, just check it
        if (Path.IsPathRooted(name) && File.Exists(name))
            return name;

        var candidates = new[]
        {
            Path.Combine(Directory.GetCurrentDirectory(), name),
            Path.Combine(Directory.GetCurrentDirectory(), "shaders", name),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, name),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "shaders", name),
        };

        foreach (var candidate in candidates)
            if (File.Exists(candidate)) return candidate;

        return null;
    }

    private unsafe void Run()
    {
        Console.WriteLine("ShaderToE131 — ShaderToy → E.1.31 LED Matrix");
        Console.WriteLine($"  Matrix: {MatW}×{MatH} ({PixelMapper.TotalPixels} pixels, {PixelMapper.TotalChannels} channels)");
        Console.WriteLine($"  Target: {TargetIp}:5568 (unicast, universe={UniverseId})");
        Console.WriteLine($"  Aspect ratio: {PixelMapper.AspectRatio:F3}");

        // Resolve LED string settings (if enabled)
        if (_stringEnabled)
        {
            int matrixUniverses = (PixelMapper.TotalChannels + 509) / 510; // 4 for the 53×11 matrix
            int baseUniverse = _stringUniverse > 0 ? _stringUniverse : UniverseId + matrixUniverses;
            // Bounds --string-size by the channel capacity from the base universe and
            // validates the base/last universes against the 1..63999 sACN contract
            // before the string buffer is allocated or anything is sent.
            string? error = StringOutput.Validate(_stringSize, baseUniverse);
            if (error != null)
            {
                Console.WriteLine($"[ERROR] {error}");
                return;
            }
            _stringUniverseResolved = baseUniverse;
            _stringRowResolved = _stringRow >= 0 ? _stringRow : (MatH - 1) / 2;
            if (_stringRowResolved < 0 || _stringRowResolved >= MatH)
            {
                Console.WriteLine($"[ERROR] --string-row must be 0..{MatH - 1} (got {_stringRow}).");
                return;
            }
            Console.WriteLine($"  String: {_stringSize} LEDs, row={_stringRowResolved}, universe={_stringUniverseResolved}, target={_stringIp ?? TargetIp}");
        }
        Console.WriteLine();

        // Resolve shader source(s) — file override, demo mode, or built-in default
        if (_demoMode)
        {
            // Resolve shader directory — explicit path, fallback to base/shaders/
            string shaderDir;
            if (!string.IsNullOrEmpty(_shaderDirPath))
            {
                shaderDir = Path.IsPathRooted(_shaderDirPath) ? _shaderDirPath : Path.Combine(Directory.GetCurrentDirectory(), _shaderDirPath);
            }
            else
            {
                shaderDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "shaders");
            }
            if (Directory.Exists(shaderDir))
                _demoShaders = Directory.GetFiles(shaderDir, "*.glsl", SearchOption.TopDirectoryOnly)
                                         .OrderBy(Path.GetFileName).ToArray();
            else
                _demoShaders = Array.Empty<string>();

            if (_demoShaders.Length == 0)
            {
                Console.WriteLine("[ERROR] No .glsl files found in shaders/ directory for demo mode.");
                return;
            }

            Console.WriteLine($"Demo mode: {_demoShaders.Length} shader(s), {_demoTimePerShaderSec}s each");
            Console.WriteLine();
        }
        else if (!string.IsNullOrEmpty(_shaderSource))
        {
            var fullPath = ResolveShaderPath(_shaderSource);
            if (fullPath != null && File.Exists(fullPath))
            {
                Console.WriteLine($"  Shader: {fullPath}");
                _shaderSource = File.ReadAllText(fullPath);
            }
            else
            {
                Console.WriteLine($"[ERROR] Shader file not found: {_shaderSource}");
                Console.WriteLine("      Available shaders in ./shaders/: " + string.Join(", ", Directory.Exists("shaders") ? Directory.GetFiles("shaders", "*.glsl").Select(Path.GetFileName) : Enumerable.Empty<string>()));
                return;
            }
        }
        else
        {
            _shaderSource = DefaultFragmentShader;
        }

        // Resolve shader directory for web server
        string resolvedShaderDir;
        if (!string.IsNullOrEmpty(_shaderDirPath))
            resolvedShaderDir = Path.IsPathRooted(_shaderDirPath) ? _shaderDirPath : Path.Combine(Directory.GetCurrentDirectory(), _shaderDirPath);
        else
            resolvedShaderDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "shaders");

        // Start web control panel (use --web-port 0 to disable)
        if (_webPort > 0)
        {
            _webServer = new WebServer(_webPort, resolvedShaderDir, _webBindAddress);
            _webServer.GetAudioEnabled = () => _audioEnabled;
            _webServer.SetAudioEnabled = enabled =>
            {
                _audioEnabled = enabled;
                if (enabled && _audioCapture == null)
                {
                    string sourceLabel = _audioSource == AudioCapture.AudioSource.Loopback ? "loopback" : "microphone";
                    _audioCapture = AudioCapture.Create(_audioSource, _audioDeviceIndex);
                    if (_audioCapture != null) { _audioCapture.Start(); Console.WriteLine($"Audio capture started via web ({sourceLabel}, device={_audioDeviceIndex})."); }
                }
                else if (!enabled && _audioCapture != null)
                {
                    _audioCapture.Dispose();
                    _audioCapture = null;
                    Console.WriteLine("Audio capture stopped via web.");
                }
            };
            _webServer.SetAudioSource = sourceStr =>
            {
                bool wasRunning = _audioEnabled && _audioCapture != null;
                if (wasRunning) { _audioCapture.Dispose(); _audioCapture = null; }
                string lower = sourceStr?.ToLowerInvariant();
                if (lower == "off")
                {
                    _audioSource = AudioCapture.AudioSource.Microphone;  // won't matter, capture is stopped
                }
                else if (lower == "loopback")
                {
                    _audioSource = AudioCapture.AudioSource.Loopback;
                    var devices = AudioCapture.ListLoopbackDevices().ToList();
                    if (_audioDeviceIndex >= 0 && _audioDeviceIndex < devices.Count)
                        _selectedLoopbackDeviceName = devices[_audioDeviceIndex].Name;
                }
                else
                {
                    _audioSource = AudioCapture.AudioSource.Microphone;
                }
                if (wasRunning)
                {
                    string sourceLabel = _audioSource == AudioCapture.AudioSource.Loopback ? "loopback" : "microphone";
                    _audioCapture = AudioCapture.Create(_audioSource, _audioDeviceIndex);
                    if (_audioCapture != null) { _audioCapture.Start(); Console.WriteLine($"Audio source changed via web to {sourceLabel} (device={_audioDeviceIndex})."); }
                }
            };
            _webServer.SetDeviceIndex = idx =>
            {
                _audioDeviceIndex = idx;
                bool wasRunning = _audioEnabled && _audioCapture != null;
                if (wasRunning)
                {
                    string sourceLabel = _audioSource == AudioCapture.AudioSource.Loopback ? "loopback" : "microphone";
                    _audioCapture.Dispose();
                    _audioCapture = AudioCapture.Create(_audioSource, idx);
                    if (_audioCapture != null) { _audioCapture.Start(); Console.WriteLine($"Audio device index changed via web to {idx} ({sourceLabel})."); }
                }
            };
            _webServer.CurrentDeviceIndex = _audioDeviceIndex;
            _webServer.GetCurrentAudioSource = () =>
    _audioSource == AudioCapture.AudioSource.Loopback ? "loopback" : "microphone";
            _webServer.GetLoopbackDeviceName = () =>
    (_audioSource == AudioCapture.AudioSource.Loopback)
        ? (_audioCapture?.GetCurrentLoopbackDeviceName() ?? _selectedLoopbackDeviceName)
        : null;
            _webServer.GetAvailableDevices = () => AudioCapture.ListLoopbackDevices().ToList();
            _webServer.SetAudioDeviceIndex = idx =>
            {
                _audioSource = AudioCapture.AudioSource.Loopback;
                _audioDeviceIndex = idx;
                _webServer.CurrentDeviceIndex = idx;

                var listedDevices = AudioCapture.ListLoopbackDevices().ToList();
                if (idx >= 0 && idx < listedDevices.Count)
                    _selectedLoopbackDeviceName = listedDevices[idx].Name;

                bool wasRunning = _audioEnabled && _audioCapture != null;
                if (wasRunning) { _audioCapture.Dispose(); _audioCapture = null; }

                if (_audioEnabled)
                {
                    // Always create a fresh capture with the requested device index.
                    AudioCapture? newCapture = null;
                    if (!string.IsNullOrWhiteSpace(_selectedLoopbackDeviceName))
                        newCapture = AudioCapture.CreateLoopbackByName(_selectedLoopbackDeviceName);
                    if (newCapture == null)
                        newCapture = AudioCapture.Create(AudioCapture.AudioSource.Loopback, idx);

                    _audioCapture = newCapture;
                    if (_audioCapture != null)
                    {
                        _audioCapture.Start();
                        var actualName = _audioCapture.GetCurrentLoopbackDeviceName() ?? _selectedLoopbackDeviceName;
                        Console.WriteLine($"Loopback device index changed via web to {idx} ({actualName}).");
                        // Persist the actual name in case CreateLoopbackByName picked a different one.
                        if (!string.IsNullOrWhiteSpace(actualName))
                            _selectedLoopbackDeviceName = actualName;
                    }
                }
            };
            _webServer.GetLiveStringState = () => new WebServer.LiveStringState(
                _stringSender != null,
                _stringSize,
                _stringRow >= 0 ? _stringRow : (MatH - 1) / 2,
                _stringIp,
                _stringUniverse,
                _stringFramesSent,
                _stringSendErrors,
                MatH
            );
            _webServer.Start();
        }

        // Record web server start time for uptime tracking
        _webStartMs = Environment.TickCount64;

        _sender = new E131Sender(TargetIp, 5568);
        Console.WriteLine("E.1.31 sender initialized.");

        if (_stringEnabled)
        {
            string strIp = _stringIp ?? TargetIp;
            _stringSender = new E131Sender(strIp, 5568);
            _stringBuffer = new byte[_stringSize * 3];
            Console.WriteLine($"LED string sender initialized ({_stringSize} LEDs → {strIp}:5568, universe={_stringUniverseResolved}).");
        }

        // Initialize audio capture if requested
        if (_audioEnabled)
        {
            string sourceLabel = _audioSource == AudioCapture.AudioSource.Loopback ? "loopback" : "microphone";
            _audioCapture = AudioCapture.Create(_audioSource, _audioDeviceIndex);

            if (_audioCapture != null)
            {
                _audioCapture.Start();
                if (_audioSource == AudioCapture.AudioSource.Loopback)
                    _selectedLoopbackDeviceName = _audioCapture.GetCurrentLoopbackDeviceName();
                Console.WriteLine($"Audio capture active ({sourceLabel}, device={_audioDeviceIndex}). Shader uniforms: u_bass, u_lowmid, u_mid, u_highmid, u_treble, u_volume.");
            }
            else
            {
                Console.WriteLine($"[WARN] No {sourceLabel} device found — audio reactive features disabled.");
            }
        }

        if (_noPreview)
        {
            Console.WriteLine("Running headless (no preview window). Press Ctrl+C to stop.");
            RunHeadless();
            return;
        }
        Console.WriteLine($"  Source adapter IP: {_sender.BoundLocalAddress?.ToString() ?? "auto-route"}");

        var opts = WindowOptions.Default;
        int scale = 10;
        opts.Size = new Vector2D<int>(MatW * scale, MatH * scale);
        opts.Title = "ShaderToE131 Preview";
        // FramesPerSecond/UpdatesPerSecond set to high values — VSync disabled in OnLoad
        opts.FramesPerSecond = 0;

        _window = Window.Create(opts);
        _window.Load += OnLoad;
        _window.Render += OnRender;
        _window.Closing += () => { };

        System.Console.Error.WriteLine($"  [Init] Window created: size={_window.Size.X}x{_window.Size.Y}");
        Console.WriteLine("Starting render loop...");
        _window.Run();
    }

    /// <summary>
    /// Load a shader from demo mode index and reset the GL time uniform.
    /// </summary>
    private unsafe void LoadDemoShader(int index)
    {
        if (_demoShaders == null || index < 0 || index >= _demoShaders.Length) return;

        _demoIndex = index;
        var fullPath = _demoShaders[index];
        Console.WriteLine($"[Demo] Loading shader #{index + 1}/{_demoShaders.Length}: {Path.GetFileName(fullPath)}");
        _shaderSource = File.ReadAllText(fullPath);

        // Reload the shader program with the new source
        ReloadShader();

        // Time resets per shader automatically (ShaderProgram tracks its own start).
        _demoShaderStartTimeMs = Environment.TickCount64;
    }

    /// <summary>
    /// Reload/recompile the shader program with a new source.
    /// Used in demo mode to swap shaders mid-flight.
    /// </summary>
    private unsafe void ReloadShader()
    {
        if (_gl == null || _window == null) return;

        // Dispose old program (ShaderProgram.Dispose cleans up GL resources)
        _shaderProgram?.Dispose();

        string fragShader = BuildFragmentShader(_shaderSource!);
        _shaderProgram = new ShaderProgram(_gl, fragShader, MatW, MatH, _window, _audioEnabled);
        Console.WriteLine($"[Reload] Shader program created.");
    }

    private unsafe void OnLoad()
    {
        Console.WriteLine("GL loaded — initializing shaders...");
        _gl = _window!.CreateOpenGL();

        // In demo mode, load the first shader (which creates the program via ReloadShader).
        // Otherwise, create the program for the current source. The two are mutually
        // exclusive to avoid creating (and leaking) a second ShaderProgram.
        if (_demoMode && _demoShaders != null && _demoShaders.Length > 0)
        {
            LoadDemoShader(0);
        }
        else
        {
            string fragShader = BuildFragmentShader(_shaderSource!);
            _shaderProgram = new ShaderProgram(_gl!, fragShader, MatW, MatH, _window!, _audioEnabled);
            Console.WriteLine("Shader program created.");
        }
        // Disable VSync via wglSwapIntervalEXT
        try
        {
            var hDC = UnsafeNativeMethods.GetDC(_window!.Handle);
            if (hDC != IntPtr.Zero)
            {
                var proc = UnsafeNativeMethods.wglGetProcAddress("wglSwapIntervalEXT");
                if (proc != IntPtr.Zero)
                {
                    var swapFn = Marshal.GetDelegateForFunctionPointer<UnsafeNativeMethods.WglSwapIntervalEXT>(proc);
                    swapFn(0);
                }
                UnsafeNativeMethods.ReleaseDC(_window.Handle, hDC);
            }
        }
        catch { /* VSync already off or extension not available */ }

        _lastStatusLogMs = Environment.TickCount64;
    }

    /// <summary>
    /// Headless render loop — uses a tiny visible window so Silk.NET doesn't throttle to ~1fps.
    /// Skips preview drawing for max performance.
    /// </summary>
    private unsafe void RunHeadless()
    {
        Console.WriteLine("  [Headless] Creating minimal GL context...");

        // Small but VISIBLE window — Silk.NET throttles hidden/minimized windows to ~1fps
        var opts = WindowOptions.Default;
        // Tiny window prevents Silk.NET from throttling to ~1fps.
        // We don't actually display it — just need a minimal GL context.
        opts.Size = new Vector2D<int>(64, 64);
        opts.Title = "ShaderToE131 Headless";
        opts.FramesPerSecond = 0; // unlimited

        _window = Window.Create(opts);
        _window.Load += OnLoadHeadless;
        _window.Render += OnRenderHeadless;
        _window.Closing += () => { };

        Console.WriteLine("Starting headless render loop...");
        _window.Run();
    }

    private unsafe void OnLoadHeadless()
    {
        Console.WriteLine("GL loaded — initializing shaders...");
        _gl = _window!.CreateOpenGL();

        // In demo mode, load the first shader (which creates the program via ReloadShader).
        // Otherwise, create the program for the current source. Mutually exclusive to
        // avoid creating (and leaking) a second ShaderProgram.
        bool demoLoaded = false;
        if (_demoMode && _demoShaders != null && _demoShaders.Length > 0)
        {
            LoadDemoShader(0);
            demoLoaded = true;
        }

        // Disable VSync via wglSwapIntervalEXT
        try
        {
            var hDC = UnsafeNativeMethods.GetDC(_window!.Handle);
            if (hDC != IntPtr.Zero)
            {
                var proc = UnsafeNativeMethods.wglGetProcAddress("wglSwapIntervalEXT");
                if (proc != IntPtr.Zero)
                {
                    var swapFn = Marshal.GetDelegateForFunctionPointer<UnsafeNativeMethods.WglSwapIntervalEXT>(proc);
                    swapFn(0);
                }
                UnsafeNativeMethods.ReleaseDC(_window.Handle, hDC);
            }
        }
        catch { /* VSync already off or extension not available */ }

        if (!demoLoaded)
        {
            string fragShader = BuildFragmentShader(_shaderSource!);
            _shaderProgram = new ShaderProgram(_gl!, fragShader, MatW, MatH, _window!, _audioEnabled);
            Console.WriteLine("Shader program created.");
        }
    }

    /// <summary>
    /// Apply a runtime LED string configuration change (from the web UI) on the render
    /// thread. Fields that are null keep their current value. Ip="" resets to the
    /// matrix IP; universe 0 means auto (first universe after the matrix's).
    /// </summary>
    private void ApplyStringChange(WebServer.StringConfigChange change)
    {
        bool enabled = change.Enabled ?? _stringSender != null;
        int size = change.Size ?? _stringSize;
        int row = change.Row ?? _stringRow;
        string? ip = change.Ip;              // null = keep; "" = reset to matrix IP
        int universe = change.Universe ?? _stringUniverse;

        string? effectiveIp = ip == null ? _stringIp : (ip.Length == 0 ? null : ip);
        int effectiveUniverse = universe;
        int effectiveRow = row >= 0 ? row : (MatH - 1) / 2;

        int matrixUniverses = (PixelMapper.TotalChannels + 509) / 510;
        int baseUniverse = effectiveUniverse > 0 ? effectiveUniverse : UniverseId + matrixUniverses;

        // Validate the requested configuration before touching anything, so an
        // invalid request is rejected wholesale and a disabled string can still
        // be preconfigured (its configured fields are committed below).
        string? error = StringOutput.Validate(size, baseUniverse);
        if (error != null)
        {
            Console.WriteLine($"[Web] LED string change rejected: {error}");
            return;
        }
        if (effectiveRow < 0 || effectiveRow >= MatH)
        {
            Console.WriteLine($"[Web] LED string change rejected: row must be 0..{MatH - 1} (got {row}).");
            return;
        }

        // Commit the configured (and resolved) values regardless of enabled state,
        // so a request that only preconfigures a disabled string is not discarded.
        _stringSize = size;
        _stringRow = row;
        _stringIp = effectiveIp;
        _stringUniverse = effectiveUniverse;
        _stringUniverseResolved = baseUniverse;
        _stringRowResolved = effectiveRow;

        if (!enabled)
        {
            _stringSender?.Dispose();
            _stringSender = null;
            _stringBuffer = Array.Empty<byte>();
            _stringFramesSent = 0;
            _stringSendErrors = 0;
            Console.WriteLine("[Web] LED string disabled.");
            return;
        }

        _stringSender?.Dispose();
        string strIp = effectiveIp ?? TargetIp;
        _stringSender = new E131Sender(strIp, 5568);
        _stringBuffer = new byte[size * 3];
        _stringFramesSent = 0;
        _stringSendErrors = 0;
        Console.WriteLine($"[Web] LED string configured: {size} LEDs, row={effectiveRow}, universe={baseUniverse}, target={strIp}");
    }

    private unsafe void OnRender(double deltaTime)
    {
        // Apply pending LED string configuration changes from the web UI (render thread).
        // Pull everything out of the web server's single volatile slot into a
        // thread-safe queue so no change is lost if requests arrive between read and clear.
        if (_webServer != null)
        {
            while (_webServer.PendingStringChange is { } change)
            {
                _webServer.PendingStringChange = null;
                _pendingStringChanges.Enqueue(change);
            }
        }
        // Apply all queued changes in order.
        while (_pendingStringChanges.TryDequeue(out var queued))
        {
            ApplyStringChange(queued);
        }

        // Check for pending shader change BEFORE null guard — when coming from "Off",
        // _shaderProgram is null and we need to reload it before the guard would bail out.
        if (_webServer != null && !string.IsNullOrEmpty(_webServer.PendingShaderSource))
        {
            string newSource = _webServer.PendingShaderSource!;
            string? newName = _webServer.PendingShaderFileName;
            _webServer.PendingShaderSource = null;
            _webServer.PendingShaderFileName = null;

            _shaderSource = newSource;
            if (!string.IsNullOrEmpty(newName))
                _currentShaderFileName = newName;

            string fragShader = BuildFragmentShader(_shaderSource!);
            _shaderProgram?.Dispose();
            _shaderProgram = new ShaderProgram(_gl!, fragShader, MatW, MatH, _window!, _audioEnabled);
            Console.WriteLine($"[Web] Shader changed: {newName ?? "unknown"}");

            // Reinitialize audio capture with current loopback device index if enabled
            if (_audioEnabled && _audioSource == AudioCapture.AudioSource.Loopback)
            {
                try
                {
                    Console.WriteLine($"[Web] Shader reload: disposing old audio, reinitializing with deviceIndex={_audioDeviceIndex}");
                    if (_audioCapture != null)
                    {
                        _audioCapture.Dispose();
                        Console.WriteLine("[Web] Old audio capture disposed.");
                    }
                    var newAudio = AudioCapture.Create(AudioCapture.AudioSource.Loopback, _audioDeviceIndex);
                    if (newAudio != null)
                    {
                        newAudio.Start();
                        _audioCapture = newAudio;
                        Console.WriteLine($"[Web] Reinitialized audio capture with device index={_audioDeviceIndex}, name='{newAudio.GetCurrentLoopbackDeviceName() ?? "unknown"}'");
                    }
                    else
                    {
                        Console.WriteLine($"[WARN] AudioCapture.Create returned null for deviceIndex={_audioDeviceIndex}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] Failed to reinitialize audio: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        if (_shaderProgram == null || _sender == null) return;

        // ─── Pending shader swap from web server (main-thread GL work) ───
        bool _isOff = false;
        if (_webServer != null && !string.IsNullOrEmpty(_webServer.PendingShaderFileName) && _webServer.PendingShaderFileName == "Off")
        {
            // "Off" — blank screen, dispose shader to stop GPU rendering
            Console.WriteLine("[Web] Shader stopped — Off (blank).");
            _shaderProgram?.Dispose();
            _shaderProgram = null;
            _shaderSource = null;
            _currentShaderFileName = "Off";
            _webServer.PendingShaderSource = null;  // consume
            _webServer.PendingShaderFileName = null;
            _isOff = true;

            // Publish an explicit "off" in the snapshot so the web UI never sees a
            // stale shader name while no shader is loaded (the snapshot update below
            // is skipped once the shader is null).
            var currentSnapshot = _webServer.StatusSnapshot;
            if (currentSnapshot != null)
                _webServer.StatusSnapshot = currentSnapshot with { SelectedShader = "off" };
        }

        // Update web server status (with live stats)
        if (_webServer != null && _shaderSource != null)
        {
            string selectedName = "built-in default";
            if (_demoShaders != null && _demoIndex >= 0)
                selectedName = Path.GetFileName(_demoShaders[_demoIndex]);
            else if (!string.IsNullOrEmpty(_currentShaderFileName))
                selectedName = _currentShaderFileName + ".glsl";

            // Re-scan shader dir for accurate counts (handles --shader-dir changes)
            _webServer.RefreshShaderList();
            var audioNames = _webServer.Shaders.Where(s => s.IsAudioReactive).Select(s => s.Name!).ToArray()!;
            _webServer.StatusSnapshot = new WebServer.ApiStatus(
                selectedName,
                _audioEnabled,
                _audioEnabled ? (_audioSource == AudioCapture.AudioSource.Loopback ? "loopback" : "microphone") : "off",
                _webServer.Shaders.Count,
                audioNames,
                (Environment.TickCount64 - _webStartMs) / 1000.0,
                _framesSent,
                _sendErrors,
                (_audioCapture?.GetCurrentLoopbackDeviceName() ?? (_audioSource == AudioCapture.AudioSource.Loopback ? _selectedLoopbackDeviceName : null)),
                [],
                _stringSender != null,
                _stringSize,
                _stringRow >= 0 ? _stringRow : (MatH - 1) / 2,
                _stringIp,
                _stringUniverse,
                _stringFramesSent,
                _stringSendErrors
            );
        }

        // Feed audio spectrum into shader uniforms each frame
        if (_audioCapture != null && !_isOff)
        {
            var spectrum = _audioCapture.ReadSpectrum();
            _shaderProgram?.SetAudioValues(spectrum);
            if (++_audioDebugCount % 60 == 0)
                Console.WriteLine($"[Audio] bass={spectrum.Bass:F3} lowmid={spectrum.LowMid:F3} mid={spectrum.Mid:F3} highmid={spectrum.HighMid:F3} treble={spectrum.Treble:F3} vol={spectrum.Volume:F3}");
        }

        // Demo mode: check if it's time to swap shaders
        if (_demoMode && _demoShaders != null && _demoIndex >= 0)
        {
            long elapsed = Environment.TickCount64 - _demoShaderStartTimeMs;
            if (elapsed >= (long)(_demoTimePerShaderSec * 1000))
            {
                int next = (_demoIndex + 1) % _demoShaders.Length;
                LoadDemoShader(next);
            }
        }

        // Render shader output into the matrix-sized framebuffer.
        if (_isOff)
        {
            Array.Clear(_frameBuffer);
        }
        else
        {
            _shaderProgram.Render(_gl!, MatW, MatH, _frameBuffer);
        }

        // Map to E.1.31 buffer (straight raster layout)
        PixelMapper.MapFrame(_frameBuffer.AsSpan(), _e131Buffer.AsSpan());

        // Send to LED matrix — 583 pixels × 3 channels = 1749 slots → needs 4 universes
        try
        {
            _sender.SendFrameMultiUniverse(_e131Buffer, UniverseId);
            _framesSent++;
        }
        catch (Exception ex)
        {
            _sendErrors++;
            Console.WriteLine($"[E1.31] Send failed: {ex.Message}");
        }

        // Send to LED string — mirrors a matrix row (default: center row)
        if (_stringSender != null)
        {
            PixelMapper.MapRowToString(_frameBuffer.AsSpan(), _stringRowResolved, _stringSize, _stringBuffer.AsSpan());
            try
            {
                _stringSender.SendFrameMultiUniverse(_stringBuffer, (ushort)_stringUniverseResolved);
                _stringFramesSent++;
            }
            catch (Exception ex)
            {
                _stringSendErrors++;
                Console.WriteLine($"[String] Send failed: {ex.Message}");
            }
        }

        _frameCount++;
        long nowMs = Environment.TickCount64;
        if (nowMs - _lastStatusLogMs >= 1000)
        {
            int r = _e131Buffer.Length > 0 ? _e131Buffer[0] : 0;
            int g = _e131Buffer.Length > 1 ? _e131Buffer[1] : 0;
            int b = _e131Buffer.Length > 2 ? _e131Buffer[2] : 0;
            string strStats = _stringSender != null ? $" | stringSent={_stringFramesSent} | stringErr={_stringSendErrors}" : "";
            Console.WriteLine($"[E1.31] Sending to {TargetIp}:5568 uni={UniverseId} | fps~{_frameCount}/s | sent={_framesSent} | errors={_sendErrors} | firstRGB={r},{g},{b}{strStats}");
            _frameCount = 0;
            _lastStatusLogMs = nowMs;
        }

        // Preview in window (windowed mode only)
        DrawPreview();
    }

    private unsafe void OnRenderHeadless(double deltaTime)
    {
        // Apply pending LED string configuration changes from the web UI (render thread).
        // Pull everything out of the web server's single volatile slot into a
        // thread-safe queue so no change is lost if requests arrive between read and clear.
        if (_webServer != null)
        {
            while (_webServer.PendingStringChange is { } change)
            {
                _webServer.PendingStringChange = null;
                _pendingStringChanges.Enqueue(change);
            }
        }
        // Apply all queued changes in order.
        while (_pendingStringChanges.TryDequeue(out var queued))
        {
            ApplyStringChange(queued);
        }

        // Check for pending shader change BEFORE null guard — when coming from "Off",
        // _shaderProgram is null and we need to reload it before the guard would bail out.
        if (_webServer != null && !string.IsNullOrEmpty(_webServer.PendingShaderSource))
        {
            string newSource = _webServer.PendingShaderSource!;
            string? newName = _webServer.PendingShaderFileName;
            _webServer.PendingShaderSource = null;
            _webServer.PendingShaderFileName = null;

            _shaderSource = newSource;
            if (!string.IsNullOrEmpty(newName))
                _currentShaderFileName = newName;

            string fragShader = BuildFragmentShader(_shaderSource!);
            _shaderProgram?.Dispose();
            _shaderProgram = new ShaderProgram(_gl!, fragShader, MatW, MatH, _window!, _audioEnabled);
            Console.WriteLine($"[Web] Shader changed: {newName ?? "unknown"}");
        }

        if (_shaderProgram == null || _sender == null) return;

        // Check for pending shader change from web server (thread-safe polling)
        bool _isOff = false;
        if (_webServer != null && !string.IsNullOrEmpty(_webServer.PendingShaderFileName) && _webServer.PendingShaderFileName == "Off")
        {
            // "Off" — blank screen, dispose shader to stop GPU rendering
            Console.WriteLine("[Web] Shader stopped — Off (blank).");
            _shaderProgram?.Dispose();
            _shaderProgram = null;
            _shaderSource = null;
            _currentShaderFileName = "Off";
            _webServer.PendingShaderSource = null;  // consume
            _webServer.PendingShaderFileName = null;
            _isOff = true;

            // Publish an explicit "off" in the snapshot so the web UI never sees a
            // stale shader name while no shader is loaded (the snapshot update below
            // is skipped once the shader is null).
            var currentSnapshot = _webServer.StatusSnapshot;
            if (currentSnapshot != null)
                _webServer.StatusSnapshot = currentSnapshot with { SelectedShader = "off" };
        }

        // Update web server status (with live stats)
        if (_webServer != null && _shaderSource != null)
        {
            string selectedName = "built-in default";
            if (_demoShaders != null && _demoIndex >= 0)
                selectedName = Path.GetFileName(_demoShaders[_demoIndex]);
            else if (!string.IsNullOrEmpty(_currentShaderFileName))
                selectedName = _currentShaderFileName + ".glsl";

            // Re-scan shader dir for accurate counts (handles --shader-dir changes)
            _webServer.RefreshShaderList();
            var audioNames = _webServer.Shaders.Where(s => s.IsAudioReactive).Select(s => s.Name!).ToArray()!;
            _webServer.StatusSnapshot = new WebServer.ApiStatus(
                selectedName,
                _audioEnabled,
                _audioEnabled ? (_audioSource == AudioCapture.AudioSource.Loopback ? "loopback" : "microphone") : "off",
                _webServer.Shaders.Count,
                audioNames,
                (Environment.TickCount64 - _webStartMs) / 1000.0,
                _framesSent,
                _sendErrors,
                (_audioCapture?.GetCurrentLoopbackDeviceName() ?? (_audioSource == AudioCapture.AudioSource.Loopback ? _selectedLoopbackDeviceName : null)),
                [],
                _stringSender != null,
                _stringSize,
                _stringRow >= 0 ? _stringRow : (MatH - 1) / 2,
                _stringIp,
                _stringUniverse,
                _stringFramesSent,
                _stringSendErrors
            );
        }

        // Feed audio spectrum into shader uniforms each frame
        if (_audioCapture != null && !_isOff)
        {
            var spectrum = _audioCapture.ReadSpectrum();
            _shaderProgram?.SetAudioValues(spectrum);
            if (++_audioDebugCount % 60 == 0)
                Console.WriteLine($"[Audio] bass={spectrum.Bass:F3} lowmid={spectrum.LowMid:F3} mid={spectrum.Mid:F3} highmid={spectrum.HighMid:F3} treble={spectrum.Treble:F3} vol={spectrum.Volume:F3}");
        }

        // Demo mode: check if it's time to swap shaders
        if (_demoMode && _demoShaders != null && _demoIndex >= 0)
        {
            long elapsed = Environment.TickCount64 - _demoShaderStartTimeMs;
            if (elapsed >= (long)(_demoTimePerShaderSec * 1000))
            {
                int next = (_demoIndex + 1) % _demoShaders.Length;
                LoadDemoShader(next);
            }
        }

        // Render shader output into the matrix-sized framebuffer.
        if (_isOff)
        {
            Array.Clear(_frameBuffer);
        }
        else
        {
            _shaderProgram.Render(_gl!, MatW, MatH, _frameBuffer);
        }

        // Map to E.1.31 buffer (straight raster layout)
        PixelMapper.MapFrame(_frameBuffer.AsSpan(), _e131Buffer.AsSpan());

        // Send to LED matrix — 583 pixels × 3 channels = 1749 slots → needs 4 universes
        try
        {
            _sender.SendFrameMultiUniverse(_e131Buffer, UniverseId);
            _framesSent++;
        }
        catch (Exception ex)
        {
            _sendErrors++;
            Console.WriteLine($"[E1.31] Send failed: {ex.Message}");
        }

        // Send to LED string — mirrors a matrix row (default: center row)
        if (_stringSender != null)
        {
            PixelMapper.MapRowToString(_frameBuffer.AsSpan(), _stringRowResolved, _stringSize, _stringBuffer.AsSpan());
            try
            {
                _stringSender.SendFrameMultiUniverse(_stringBuffer, (ushort)_stringUniverseResolved);
                _stringFramesSent++;
            }
            catch (Exception ex)
            {
                _stringSendErrors++;
                Console.WriteLine($"[String] Send failed: {ex.Message}");
            }
        }

        _frameCount++;
        long nowMs = Environment.TickCount64;
        if (nowMs - _lastStatusLogMs >= 2000)
        {
            int r = _e131Buffer.Length > 0 ? _e131Buffer[0] : 0;
            int g = _e131Buffer.Length > 1 ? _e131Buffer[1] : 0;
            int b = _e131Buffer.Length > 2 ? _e131Buffer[2] : 0;
            string strStats = _stringSender != null ? $" | stringSent={_stringFramesSent} | stringErr={_stringSendErrors}" : "";
            Console.WriteLine($"[E1.31] Sending to {TargetIp}:5568 uni={UniverseId} | fps~{_frameCount}/2s | sent={_framesSent} | errors={_sendErrors} | firstRGB={r},{g},{b}{strStats}");
            _frameCount = 0;
            _lastStatusLogMs = nowMs;
        }
    }

    private unsafe void DrawPreview()
    {
        if (_gl == null || _shaderProgram == null) return;
        _gl.Clear(ClearBufferMask.ColorBufferBit);
        _gl.ClearColor(0.05f, 0.05f, 0.1f, 1.0f);
        _shaderProgram.DrawPreview(_gl, PixelMapper.Width * 10, PixelMapper.Height * 10, _frameBuffer);
    }

    public void Dispose()
    {
        _webServer?.Stop();
        _audioCapture?.Dispose();
        _sender?.Dispose();
        _stringSender?.Dispose();
        // Dispose the shader program before the window: its GL deletion calls
        // (DeleteProgram, DeleteTexture, DeleteBuffer, DeleteVertexArray, ...)
        // require a live GL context, which the window owns.
        _shaderProgram?.Dispose();
        _window?.Dispose();
    }
}

// ─── Shader Program (Silk.NET 2.x API) ──────────────────────────

class ShaderProgram : IDisposable
{
    public uint Program => _program;
    private readonly GL _gl;
    private readonly uint _program;
    private readonly uint _quadVao, _quadVbo;
    private readonly uint _renderFbo, _renderTex;
    private readonly uint _previewProgram, _previewVao, _previewVbo, _previewTex;
    private readonly int _previewTexLoc;
    private readonly int _matW, _matH;
    private byte[] _readPixels = Array.Empty<byte>();
    private IWindow? _window;

    private readonly bool _audioEnabled;

    // Time base so u_time starts at 0 when this program is created (fresh per shader).
    private readonly long _startMs = Environment.TickCount64;

    public unsafe ShaderProgram(GL gl, string fragmentSource, int width, int height, IWindow window, bool audioEnabled = false)
    {
        Console.WriteLine("  [ShaderProg] Starting constructor...");
        _gl = gl;
        _matW = width;
        _matH = height;
        _window = window;
        _audioEnabled = audioEnabled;

        // Vertex shader — outputs pixel coordinates via FragCoord
        string vertSrc = @"#version 330 core
layout(location = 0) in vec2 a_position;
out vec2 FragCoord;
void main()
{{
    FragCoord = a_position.xy * vec2({0}, {1});
    gl_Position = vec4(a_position, 0.0, 1.0);
}}";
        vertSrc = string.Format(vertSrc, width, height);

        uint fragShader = Compile(gl, GLEnum.FragmentShader, fragmentSource, _audioEnabled);
        if (_audioEnabled) Console.WriteLine("  [ShaderProg] Fragment compiled (with audio uniforms).");
        else Console.WriteLine("  [ShaderProg] Fragment compiled.");
        uint vertShader = Compile(gl, GLEnum.VertexShader, vertSrc, false);
        Console.WriteLine("  [ShaderProg] Vertex compiled.");

        _program = gl.CreateProgram();
        gl.AttachShader(_program, vertShader);
        gl.AttachShader(_program, fragShader);
        gl.LinkProgram(_program);
        Console.WriteLine("  [ShaderProg] Linked.");

        int status;
        gl.GetProgram(_program, ProgramPropertyARB.LinkStatus, out status);
        if (status != (int)GLEnum.True)
            Console.WriteLine($"Shader link failed: {gl.GetProgramInfoLog(_program)}");
        else
            Console.WriteLine("  [ShaderProg] Link OK.");

        // Full-screen quad (-1..1)
        float[] quadVerts = { -1f, -1f, 3f, -1f, -1f, 3f };
        _quadVbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _quadVbo);
        fixed (float* buf = quadVerts)
            gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(quadVerts.Length * sizeof(float)), buf, BufferUsageARB.StaticDraw);

        _quadVao = gl.GenVertexArray();
        gl.BindVertexArray(_quadVao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _quadVbo);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), (void*)0);
        gl.BindVertexArray(0);

        // Offscreen render target for deterministic readback.
        _renderTex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, _renderTex);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba8, (uint)_matW, (uint)_matH, 0, PixelFormat.Rgba, PixelType.UnsignedByte, null);

        _renderFbo = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, _renderFbo);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _renderTex, 0);
        var fboStatus = gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (fboStatus != GLEnum.FramebufferComplete)
            Console.WriteLine($"  [ShaderProg] FBO not complete: {fboStatus}");
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

        // Preview resources — built once here and reused every frame by DrawPreview
        // (previously the texture, program, VBO and VAO were rebuilt per frame).
        _previewTex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, _previewTex);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba, (uint)_matW, (uint)_matH, 0, PixelFormat.Rgba, PixelType.UnsignedByte, null);

        const string previewVs = @"#version 330 core
layout(location=0) in vec2 a_pos;
out vec2 UV;
void main(){ UV = a_pos * 0.5 + 0.5; gl_Position = vec4(a_pos, 0, 1); }";

        const string previewFs = @"#version 330 core
in vec2 UV;
uniform sampler2D tex;
out vec4 fragColor;
void main(){ fragColor = texture(tex, UV); }";

        _previewProgram = gl.CreateProgram();
        uint previewVsObj = Compile(gl, GLEnum.VertexShader, previewVs, false);
        uint previewFsObj = Compile(gl, GLEnum.FragmentShader, previewFs, false);
        gl.AttachShader(_previewProgram, previewVsObj);
        gl.AttachShader(_previewProgram, previewFsObj);
        gl.LinkProgram(_previewProgram);
        _previewTexLoc = gl.GetUniformLocation(_previewProgram, "tex");
        gl.DetachShader(_previewProgram, previewVsObj);
        gl.DetachShader(_previewProgram, previewFsObj);
        gl.DeleteShader(previewVsObj);
        gl.DeleteShader(previewFsObj);

        // Clip-space quad; UV in the vertex shader handles texture mapping.
        float[] previewQuad = { -1f, -1f, 1f, -1f, -1f, 1f, -1f, 1f, 1f, -1f, 1f, 1f };
        _previewVbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _previewVbo);
        fixed (float* buf = previewQuad)
            gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(previewQuad.Length * sizeof(float)), buf, BufferUsageARB.StaticDraw);

        _previewVao = gl.GenVertexArray();
        gl.BindVertexArray(_previewVao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _previewVbo);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), (void*)0);
        gl.BindVertexArray(0);

        gl.DetachShader(_program, vertShader);
        gl.DetachShader(_program, fragShader);
        gl.DeleteShader(vertShader);
        gl.DeleteShader(fragShader);
        Console.WriteLine("  [ShaderProg] Constructor done.");
    }

    private static uint Compile(GL gl, GLEnum type, string source, bool audioEnabled)
    {
        var shader = gl.CreateShader(type);
        gl.ShaderSource(shader, source);
        gl.CompileShader(shader);
        int success;
        gl.GetShader(shader, ShaderParameterName.CompileStatus, out success);
        if (success != (int)GLEnum.True)
            Console.WriteLine($"Compile failed ({type}): {gl.GetShaderInfoLog(shader)}");
        return shader;
    }

    public void SetUniform(string name, float value)
    {
        int loc = GetUniformLocation(name);
        if (loc >= 0) _gl.Uniform1(loc, value);
    }

    public void SetUniform(string name, int value)
    {
        int loc = GetUniformLocation(name);
        if (loc >= 0) _gl.Uniform1(loc, value);
    }

    public void SetUniform(string name, int w, int h)
    {
        int loc = GetUniformLocation(name);
        if (loc >= 0) _gl.Uniform2(loc, (float)w, (float)h);
    }

    /// <summary>
    /// Set all audio-reactive shader uniforms from captured spectrum data.
    /// No-op for shaders compiled without the --audio flag (uniforms won't exist).
    /// </summary>
    public void SetAudioValues(AudioCapture.SpectrumValues spectrum)
    {
        // Check uniform locations on first call to diagnose missing uniforms
        if (_audioUniformLocations == null)
        {
            _audioUniformLocations = new string[] { "u_bass", "u_lowmid", "u_mid", "u_highmid", "u_treble", "u_volume" };
            _audioUniformLocationCache = new int[6];
            for (int i = 0; i < 6; i++)
                _audioUniformLocationCache[i] = _gl.GetUniformLocation(_program, _audioUniformLocations[i]);

            bool anyMissing = false;
            foreach (var loc in _audioUniformLocationCache)
                if (loc == -1) { anyMissing = true; break; }

            if (anyMissing)
            {
                Console.WriteLine("[Audio] WARNING: Some audio uniforms not found in shader program:");
                for (int i = 0; i < 6; i++)
                    Console.WriteLine($"    {_audioUniformLocations[i]} = loc{_audioUniformLocationCache[i]}");
            }
        }

        float[] vals = new float[6]
        {
            spectrum.Bass, spectrum.LowMid, spectrum.Mid,
            spectrum.HighMid, spectrum.Treble, spectrum.Volume
        };
        for (int i = 0; i < 6; i++)
        {
            if (_audioUniformLocationCache[i] >= 0)
                _gl.Uniform1(_audioUniformLocationCache[i], vals[i]);
            // Silently skip uniforms that don't exist — the GLSL optimizer removes
            // unreferenced uniforms, and printing every frame would spam the console.
        }
    }

    private string[]? _audioUniformLocations;
    private int[]? _audioUniformLocationCache;
    private Dictionary<string, int>? _uniformLocationCache;

    /// <summary>
    /// Cached uniform locations. GetUniformLocation is a driver round-trip, so
    /// locations are resolved once per uniform name and reused every frame.
    /// </summary>
    private int GetUniformLocation(string name)
    {
        if (_uniformLocationCache == null)
            _uniformLocationCache = new Dictionary<string, int>();
        if (!_uniformLocationCache.TryGetValue(name, out int loc))
        {
            loc = _gl.GetUniformLocation(_program, name);
            _uniformLocationCache[name] = loc;
        }
        return loc;
    }

    /// <summary>
    /// Render directly to default framebuffer at small resolution, then ReadPixels.
    /// </summary>
    public unsafe void Render(GL gl, int width, int height, byte[] framebuffer)
    {
        // Render into offscreen FBO at matrix resolution.
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, _renderFbo);
        gl.Viewport(0, 0, (uint)width, (uint)height);

        // Clear with a bright color so we can verify rendering works
        gl.ClearColor(1.0f, 0.0f, 0.0f, 1.0f);  // Red
        gl.Clear(ClearBufferMask.ColorBufferBit);

        // Use shader program and set uniforms while it is bound.
        gl.UseProgram(_program);
        // u_time is relative to when this program was created, so each shader starts at t=0.
        SetUniform("u_time", (float)((Environment.TickCount64 - _startMs) / 1000.0));
        SetUniform("u_frame", 0);
        SetUniform("u_resolution", width, height);

        DrawQuad(gl);

        // Read back from the offscreen color attachment.
        int readSize = width * height * 4;
        if (_readPixels.Length < readSize)
            _readPixels = new byte[readSize];
        byte[] readPixels = _readPixels;
        gl.PixelStore(PixelStoreParameter.PackAlignment, 1);
        gl.ReadBuffer(ReadBufferMode.ColorAttachment0);
        // ReadPixels synchronizes with the GPU for the data it returns, so a
        // blocking gl.Finish() here is unnecessary; gl.Flush() is sufficient.
        gl.Flush();
        fixed (byte* readPtr = readPixels)
        {
            gl.ReadPixels(0, 0, (uint)width, (uint)height, PixelFormat.Rgba, PixelType.UnsignedByte, readPtr);
        }

        // Copy directly to framebuffer (no downsample needed)
        int rowBytes = width * 4;
        for (int y = 0; y < height; y++)
        {
            int srcY = height - 1 - y;  // Flip Y: OpenGL bottom-to-top
            for (int i = 0; i < rowBytes; i++)
                framebuffer[y * rowBytes + i] = readPixels[srcY * rowBytes + i];
        }

        // Restore viewport to window size for preview drawing
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        if (_window != null)
            gl.Viewport(0, 0, (uint)_window.Size.X, (uint)_window.Size.Y);
    }

    private void DrawQuad(GL gl)
    {
        gl.BindVertexArray(_quadVao);
        gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        gl.BindVertexArray(0);
    }

    /// <summary>
    /// Preview: draw the latest framebuffer scaled to the window.
    /// The texture, program, VBO and VAO are created once in the constructor;
    /// only the texture data is re-uploaded each frame.
    /// </summary>
    public unsafe void DrawPreview(GL gl, int winWidth, int winHeight, byte[] frameBuffer)
    {
        // Upload the latest framebuffer into the cached preview texture.
        gl.BindTexture(TextureTarget.Texture2D, _previewTex);
        GCHandle fh = GCHandle.Alloc(frameBuffer, GCHandleType.Pinned);
        try
        {
            gl.TexImage2D(
                (GLEnum)TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba,
                (uint)_matW, (uint)_matH, 0,
                (GLEnum)PixelFormat.Rgba, (GLEnum)PixelType.UnsignedByte, (void*)fh.AddrOfPinnedObject());
        }
        finally
        {
            fh.Free();
        }

        gl.UseProgram(_previewProgram);
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindTexture(TextureTarget.Texture2D, _previewTex);
        gl.Uniform1(_previewTexLoc, 0);

        gl.BindVertexArray(_previewVao);
        gl.DrawArrays(PrimitiveType.Triangles, 0, 6);

        // Restore quad VAO and the main program for the next render pass.
        // Without restoring the program, the next frame's SetAudioValues
        // (called before Render binds _program) would target _previewProgram.
        gl.BindVertexArray(_quadVao);
        gl.UseProgram(_program);
    }

    public void Dispose()
    {
        _gl?.DeleteFramebuffer(_renderFbo);
        _gl?.DeleteTexture(_renderTex);
        _gl?.DeleteProgram(_program);
        _gl?.DeleteVertexArray(_quadVao);
        _gl?.DeleteBuffer(_quadVbo);
        _gl?.DeleteTexture(_previewTex);
        _gl?.DeleteProgram(_previewProgram);
        _gl?.DeleteVertexArray(_previewVao);
        _gl?.DeleteBuffer(_previewVbo);
    }
}

internal static class UnsafeNativeMethods
{
    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("opengl32.dll", SetLastError = true)]
    public static extern IntPtr wglGetProcAddress(string procName);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void WglSwapIntervalEXT(int interval);
}
