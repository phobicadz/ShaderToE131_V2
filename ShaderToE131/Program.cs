using System.Runtime.InteropServices;
using System.Linq;
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
    private bool _demoMode = false;
    private double _demoTimePerShaderSec = 10.0;
    private string? _shaderDirPath = null;
    private bool _audioEnabled = false;
    private AudioCapture.AudioSource _audioSource = AudioCapture.AudioSource.Microphone;
    private int _audioDeviceIndex = 0;
    private AudioCapture? _audioCapture;

    private IWindow? _window;
    private GL? _gl;
    private E131Sender? _sender;
    private ShaderProgram? _shaderProgram;
    private byte[] _frameBuffer = new byte[MatW * MatH * 4];
    private byte[] _e131Buffer = new byte[PixelMapper.TotalChannels];
    private double _startTime;
    private int _frameCount = 0;
    private int _audioDebugCount = 0;
    private int _demoIndex = -1;            // current shader index in demo mode
    private long _demoShaderStartTimeMs;     // tick when current shader started
    private string[]? _demoShaders;          // resolved paths for all shaders
    private long _lastStatusLogMs;
    private int _framesSent;
    private int _sendErrors;

    /// <summary>
    /// Build the final GLSL fragment shader source.
    /// Wraps raw ShaderToy-style mainImage code with required boilerplate,
    /// or returns the built-in default if no custom shader was provided.
    /// </summary>
    private string BuildFragmentShader(string rawSource)
    {
        // Check if the source already has #version and void main() — treat as complete
        bool isComplete = rawSource.Contains("#version") && (rawSource.Contains("void main()") || rawSource.Contains("out vec4 FragColor"));
        if (!isComplete)
        {
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

            // Wrap ShaderToy-style source: add defines, uniforms, helpers, and void main()
            string audioUniforms = _audioEnabled
                ? "\nuniform float u_bass;\nuniform float u_lowmid;\nuniform float u_mid;\nuniform float u_highmid;\nuniform float u_treble;\nuniform float u_volume;"
                : "";

            string wrapped = @"#version 330 core
#define iTime u_time
#define iResolution u_resolution
uniform float u_time;
uniform vec2  u_resolution;
uniform int   u_frame;
out vec4 FragColor;" + audioUniforms + @"

" + shaderToyHelpers + rawSource + @"
void main()
{{
    vec2 fragCoord = gl_FragCoord.xy;
    mainImage(FragColor, fragCoord);
}};";
            return wrapped;
        }
        // Already a complete GLSL fragment shader — inject audio uniforms if needed, then replace {AR} placeholder
        string result = rawSource;
        if (_audioEnabled)
        {
            const string uBass = "uniform float u_bass;";
            const string uLowmid = "uniform float u_lowmid;";
            const string uMid = "uniform float u_mid;";
            const string uHighmid = "uniform float u_highmid;";
            const string uTreble = "uniform float u_treble;";
            const string uVolume = "uniform float u_volume;";

            bool hasAudioUniforms = result.Contains(uBass) && result.Contains(uLowmid)
                && result.Contains(uMid) && result.Contains(uHighmid)
                && result.Contains(uTreble) && result.Contains(uVolume);

            if (!hasAudioUniforms)
            {
                // Inject after the 'out vec4 FragColor' line (with semicolon)
                string injectPoint = "out vec4 FragColor;";
                int idx = result.IndexOf(injectPoint);
                if (idx >= 0)
                {
                    int insertPos = idx + injectPoint.Length;
                    result = result.Insert(insertPos,
                        "\nuniform float u_bass;\nuniform float u_lowmid;\n"
                      + "uniform float u_mid;\nuniform float u_highmid;\n"
                      + "uniform float u_treble;\nuniform float u_volume;");
                    Console.WriteLine("[BuildFragmentShader] Injected audio uniforms into complete shader.");
                }
            }
        }
        return result.Replace("{AR}", PixelMapper.AspectRatio.ToString());
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
        var prog = new Program();
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
            else if ((args[i] == "--loopback" || args[i] == "--playback") && i + 1 < args.Length && int.TryParse(args[++i], out var lbIdx))
                { prog._audioSource = AudioCapture.AudioSource.Loopback; prog._audioDeviceIndex = lbIdx; }
            else if (args[i] == "--audio-device" && i + 1 < args.Length && int.TryParse(args[++i], out var devIdx))
                prog._audioDeviceIndex = devIdx;
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
            Console.WriteLine("  --list-devices       List available audio input devices and exit");
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

        _sender = new E131Sender(TargetIp, 5568);
        Console.WriteLine("E.1.31 sender initialized.");

        // Initialize audio capture if requested
        if (_audioEnabled)
        {
            string sourceLabel = _audioSource == AudioCapture.AudioSource.Loopback ? "loopback" : "microphone";
            _audioCapture = AudioCapture.Create(_audioSource, _audioDeviceIndex);

            if (_audioCapture != null)
            {
                _audioCapture.Start();
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

        // Reset time so the animation starts fresh for each shader
        _startTime = Environment.TickCount64 / 1000.0;
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
        _shaderProgram = new ShaderProgram(_gl, BuildFragmentShader(_shaderSource!), MatW, MatH, _window, _audioEnabled);
        Console.WriteLine($"[Reload] Shader program created.");
    }

    private unsafe void OnLoad()
    {
        Console.WriteLine("GL loaded — initializing shaders...");
        _gl = _window!.CreateOpenGL();

        // In demo mode, load the first shader and set start time
        if (_demoMode && _demoShaders != null && _demoShaders.Length > 0)
        {
            LoadDemoShader(0);
        }

        string fragShader = BuildFragmentShader(_shaderSource!);
        _shaderProgram = new ShaderProgram(_gl!, fragShader, MatW, MatH, _window!);
        Console.WriteLine("Shader program created.");
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

        _startTime = Environment.TickCount64 / 1000.0;
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
        // Larger window prevents Silk.NET from throttling to ~1fps.
        // We don't actually display it — just need it big enough for GL context to run fast.
        opts.Size = new Vector2D<int>(512, 512);
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

        // In demo mode, load the first shader and set start time
        if (_demoMode && _demoShaders != null && _demoShaders.Length > 0)
        {
            LoadDemoShader(0);
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

        string fragShader = BuildFragmentShader(_shaderSource);
        _shaderProgram = new ShaderProgram(_gl!, fragShader, MatW, MatH, _window!);
        Console.WriteLine("Shader program created.");
    }

    private unsafe void OnRender(double deltaTime)
    {
        if (_shaderProgram == null || _sender == null) return;

        // Feed audio spectrum into shader uniforms each frame
        if (_audioCapture != null)
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
        _shaderProgram.Render(_gl!, MatW, MatH, _frameBuffer);

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

        _frameCount++;
        long nowMs = Environment.TickCount64;
        if (nowMs - _lastStatusLogMs >= 1000)
        {
            int r = _e131Buffer.Length > 0 ? _e131Buffer[0] : 0;
            int g = _e131Buffer.Length > 1 ? _e131Buffer[1] : 0;
            int b = _e131Buffer.Length > 2 ? _e131Buffer[2] : 0;
            Console.WriteLine($"[E1.31] Sending to {TargetIp}:5568 uni={UniverseId} | fps~{_frameCount}/s | sent={_framesSent} | errors={_sendErrors} | firstRGB={r},{g},{b}");
            _frameCount = 0;
            _lastStatusLogMs = nowMs;
        }

        // Preview in window (windowed mode only)
        DrawPreview();
    }



    private unsafe void OnRenderHeadless(double deltaTime)
    {
        if (_shaderProgram == null || _sender == null) return;

        // Feed audio spectrum into shader uniforms each frame
        if (_audioCapture != null)
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
        _shaderProgram.Render(_gl!, MatW, MatH, _frameBuffer);

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

        _frameCount++;
        long nowMs = Environment.TickCount64;
        if (nowMs - _lastStatusLogMs >= 2000)
        {
            int r = _e131Buffer.Length > 0 ? _e131Buffer[0] : 0;
            int g = _e131Buffer.Length > 1 ? _e131Buffer[1] : 0;
            int b = _e131Buffer.Length > 2 ? _e131Buffer[2] : 0;
            Console.WriteLine($"[E1.31] Sending to {TargetIp}:5568 uni={UniverseId} | fps~{_frameCount}/2s | sent={_framesSent} | errors={_sendErrors} | firstRGB={r},{g},{b}");
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
        _audioCapture?.Dispose();
        _sender?.Dispose();
        _window?.Dispose();
        _shaderProgram?.Dispose();
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
    private readonly int _matW, _matH;
    private IWindow? _window;

    private readonly bool _audioEnabled;

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
        int loc = _gl.GetUniformLocation(_program, name);
        if (loc >= 0) _gl.Uniform1(loc, value);
    }

    public void SetUniform(string name, int value)
    {
        int loc = _gl.GetUniformLocation(_program, name);
        if (loc >= 0) _gl.Uniform1(loc, value);
    }

    public void SetUniform(string name, int w, int h)
    {
        float[] v = { (float)w, (float)h };
        int loc = _gl.GetUniformLocation(_program, name);
        if (loc >= 0) _gl.Uniform2(loc, v[0], v[1]);
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
            else
                Console.WriteLine($"[Audio] Uniform '{_audioUniformLocations[i]}' not found in shader (likely optimized out — shader doesn't reference it).");
        }
    }

    private string[]? _audioUniformLocations;
    private int[]? _audioUniformLocationCache;

    /// <summary>
    /// Render directly to default framebuffer at small resolution, then ReadPixels.
    /// </summary>
    public unsafe void Render(GL gl, int width, int height, byte[] framebuffer)
    {
        // Drain any stale GL errors from prior calls so diagnostics reflect this frame.
        while (gl.GetError() != GLEnum.NoError) { }

        // Render into offscreen FBO at matrix resolution.
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, _renderFbo);
        gl.Viewport(0, 0, (uint)width, (uint)height);

        // Clear with a bright color so we can verify rendering works
        gl.ClearColor(1.0f, 0.0f, 0.0f, 1.0f);  // Red
        gl.Clear(ClearBufferMask.ColorBufferBit);

        // Use shader program and set uniforms while it is bound.
        gl.UseProgram(_program);
        SetUniform("u_time", (float)(Environment.TickCount64 / 1000.0));
        SetUniform("u_frame", 0);
        SetUniform("u_resolution", width, height);

        DrawQuad(gl);

        // Read back from the offscreen color attachment.
        byte[] readPixels = new byte[width * height * 4];
        gl.PixelStore(PixelStoreParameter.PackAlignment, 1);
        gl.ReadBuffer(ReadBufferMode.ColorAttachment0);
        gl.Finish();
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
    /// Preview: draw texture scaled to window.
    /// </summary>
    public unsafe void DrawPreview(GL gl, int winWidth, int winHeight, byte[] frameBuffer)
    {
        uint tex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, tex);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
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

        const string vs = @"#version 330 core
layout(location=0) in vec2 a_pos;
out vec2 UV;
void main(){ UV = a_pos * 0.5 + 0.5; gl_Position = vec4(a_pos, 0, 1); }";

        const string fs = @"#version 330 core
in vec2 UV;
uniform sampler2D tex;
out vec4 fragColor;
void main(){ fragColor = texture(tex, UV); }";

        uint prog = gl.CreateProgram();
        uint vsObj = Compile(gl, GLEnum.VertexShader, vs, false);
        uint fsObj = Compile(gl, GLEnum.FragmentShader, fs, false);
        gl.AttachShader(prog, vsObj);
        gl.AttachShader(prog, fsObj);
        gl.LinkProgram(prog);

        int texLoc = gl.GetUniformLocation(prog, "tex");

        // Clip-space quad; UV in vertex shader handles texture mapping.
        float[] previewQuad = { -1f, -1f, 1f, -1f, -1f, 1f, -1f, 1f, 1f, -1f, 1f, 1f };

        uint pbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, pbo);
        fixed (float* buf = previewQuad)
            gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(previewQuad.Length * sizeof(float)), buf, BufferUsageARB.StaticDraw);

        uint pvao = gl.GenVertexArray();
        gl.BindVertexArray(pvao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, pbo);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), (void*)0);

        gl.UseProgram(prog);
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindTexture(TextureTarget.Texture2D, tex);
        gl.Uniform1(texLoc, 0);
        gl.DrawArrays(PrimitiveType.Triangles, 0, 6);

        // Cleanup preview resources
        gl.DeleteVertexArray(pvao);
        gl.DeleteBuffer(pbo);
        gl.DeleteTexture(tex);
        gl.DetachShader(prog, vsObj);
        gl.DetachShader(prog, fsObj);
        gl.DeleteProgram(prog);
        gl.DeleteShader(vsObj);
        gl.DeleteShader(fsObj);

        // Restore quad VAO for next render pass
        gl.BindVertexArray(_quadVao);
    }

    public void Dispose()
    {
        _gl?.DeleteFramebuffer(_renderFbo);
        _gl?.DeleteTexture(_renderTex);
        _gl?.DeleteProgram(_program);
        _gl?.DeleteVertexArray(_quadVao);
        _gl?.DeleteBuffer(_quadVbo);
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
