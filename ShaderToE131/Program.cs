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

    private IWindow? _window;
    private GL? _gl;
    private E131Sender? _sender;
    private ShaderProgram? _shaderProgram;
    private byte[] _frameBuffer = new byte[MatW * MatH * 4];
    private byte[] _e131Buffer = new byte[PixelMapper.TotalChannels];
    private double _startTime;
    private int _frameCount = 0;
    private long _lastStatusLogMs;
    private int _framesSent;
    private int _sendErrors;

    // ShaderToy fragment shader — paste your mainImage body here.
    // Use {{ and }} for literal curly braces in GLSL; use {AR} for the aspect ratio value.
    private const string DefaultFragmentShader = @"#version 330 core
uniform float u_time;
uniform vec2  u_resolution;
uniform int   u_frame;
out vec4 FragColor;

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{{
    vec2 uv = fragCoord / u_resolution.xy;
    float aspect = {AR};
    uv.x /= aspect;

    // Simple pattern — replace with your ShaderToy code!
    vec3 col = 0.5 + 0.5 * cos(u_time + uv.xyx + vec3(0, 2, 4));
    fragColor = vec4(col, 1.0);
}}
void main() {{
    vec4 color;
    mainImage(color, gl_FragCoord.xy);
    FragColor = color;
}}";

    static void Main() => new Program().Run();

    private unsafe void Run()
    {
        Console.WriteLine("ShaderToE131 — ShaderToy → E.1.31 LED Matrix");
        Console.WriteLine($"  Matrix: {MatW}×{MatH} ({PixelMapper.TotalPixels} pixels, {PixelMapper.TotalChannels} channels)");
        Console.WriteLine($"  Target: {TargetIp}:5568 (unicast, universe={UniverseId})");
        Console.WriteLine($"  Aspect ratio: {PixelMapper.AspectRatio:F3}");
        Console.WriteLine();

        _sender = new E131Sender(TargetIp, 5568);
        Console.WriteLine("E.1.31 sender initialized.");
        Console.WriteLine($"  Source adapter IP: {_sender.BoundLocalAddress?.ToString() ?? "auto-route"}");

        var opts = WindowOptions.Default;
        int scale = 10;
        opts.Size = new Vector2D<int>(MatW * scale, MatH * scale);
        opts.Title = "ShaderToE131 Preview";
        opts.FramesPerSecond = 60;
        opts.UpdatesPerSecond = 60;

        _window = Window.Create(opts);
        _window.Load += OnLoad;
        _window.Render += OnRender;
        _window.Closing += () => { };

        System.Console.Error.WriteLine($"  [Init] Window created: size={_window.Size.X}x{_window.Size.Y}");
        Console.WriteLine("Starting render loop...");
        _window.Run();
    }

    private unsafe void OnLoad()
    {
        Console.WriteLine("GL loaded — initializing shaders...");
        _gl = _window!.CreateOpenGL();

        string fragShader = DefaultFragmentShader.Replace("{AR}", PixelMapper.AspectRatio.ToString());
        _shaderProgram = new ShaderProgram(_gl!, fragShader, MatW, MatH, _window!);
        Console.WriteLine("Shader program created.");
        _startTime = Environment.TickCount64 / 1000.0;
        _lastStatusLogMs = Environment.TickCount64;
    }

    private unsafe void OnRender(double deltaTime)
    {
        if (_shaderProgram == null || _sender == null || _gl == null) return;

        // Render shader output into the matrix-sized framebuffer.
        _shaderProgram.Render(_gl, MatW, MatH, _frameBuffer);

        // Map to E.1.31 buffer (serpentine layout)
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
            Console.WriteLine($"[E1.31] Sending to {TargetIp}:5568 uni={UniverseId} | fps~{_frameCount} | sent={_framesSent} | errors={_sendErrors} | firstRGB={r},{g},{b}");
            _frameCount = 0;
            _lastStatusLogMs = nowMs;
        }

        // Preview in window
        DrawPreview();
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

    public unsafe ShaderProgram(GL gl, string fragmentSource, int width, int height, IWindow window)
    {
        Console.WriteLine("  [ShaderProg] Starting constructor...");
        _gl = gl;
        _matW = width;
        _matH = height;
        _window = window;

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

        uint fragShader = Compile(gl, ShaderType.FragmentShader, fragmentSource);
        Console.WriteLine("  [ShaderProg] Fragment compiled.");
        uint vertShader = Compile(gl, ShaderType.VertexShader, vertSrc);
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

    private static uint Compile(GL gl, ShaderType type, string source)
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
        uint vsObj = Compile(gl, ShaderType.VertexShader, vs);
        uint fsObj = Compile(gl, ShaderType.FragmentShader, fs);
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
