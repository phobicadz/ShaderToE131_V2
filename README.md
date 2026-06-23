# ShaderToE131

Render GLSL fragment shaders to a **53×11 LED matrix** via E.1.31 (sACN) UDP packets, with optional audio reactivity driven by microphone or system playback capture.

## Overview

ShaderToE131 takes [ShaderToy-style](https://www.shadertoy.com/) GLSL shaders (`mainImage` signature), compiles them on GPU via **Silk.NET** (OpenGL 3.3 Core), maps the framebuffer pixels to LED matrix channel order, and streams RGB data as E.1.31/sACN packets over UDP.

### Features

- 🎨 **ShaderToy-compatible GLSL shaders** — auto-wrapped with uniforms (`u_time`, `u_resolution`, `u_frame`)
- 🔊 **Audio reactivity** — FFT spectral analysis (2048-point, ~21.5 Hz resolution) feeds bass/mid/treble bands as shader uniforms
- 🌐 **Web control panel** — select shaders, view status, toggle audio without restarting
- 📡 **E.1.31/sACN output** — UDP multicast/unicast to LED matrix controllers (4 universes: 583 pixels × 3 channels = 1749 DMX slots)
- 🎭 **Demo mode** — auto-cycle through all shaders in directory
- 🔇 **"Off" blank** — dispose shader program to stop GPU rendering entirely

## Architecture

```
shaders/*.glsl  ← ShaderToy-style fragment shaders (mainImage signature)
    │
    ▼
┌─────────────────────────────────────────────┐
│              Program.cs (Main Loop)         │
│  Silk.NET Windowing + OpenGL 3.3 Core       │
│  • Compile & link GLSL fragment shader      │
│  • Render framebuffer at matrix resolution  │
│  • Feed audio spectrum uniforms each frame  │
└──────────────┬──────────────────────────────┘
               │ _frameBuffer[583×4] (RGBA)
               ▼
┌─────────────────────────────────────────────┐
│           PixelMapper.cs                    │
│  Map framebuffer pixels → LED channel order │
│  RGB per pixel, straight raster layout      │
└──────────────┬──────────────────────────────┘
               │ _e131Buffer[1749] (RGB slots)
               ▼
┌─────────────────────────────────────────────┐
│           E131Sender.cs                     │
│  Build E.1.31/sACN UDP packets              │
│  Send to target IP:5568 (UDP Datagram)      │
└─────────────────────────────────────────────┘

┌─────────────────────────────────────────────┐
│           WebServer.cs (HttpListener)       │
│  • HTTP control panel on configurable port  │
│  • API: /api/shaders, /api/select-shader    │
│         /api/status                         │
│  • Shader selection, status display         │
└─────────────────────────────────────────────┘

┌─────────────────────────────────────────────┐
│           AudioCapture.cs (NAudio)          │
│  • Microphone: WaveInEvent                  │
│  • Loopback: WasapiLoopbackCapture          │
│  • FFT: 2048-point → spectral bands         │
│    Bass, LowMid, Mid, HighMid, Treble       │
└─────────────────────────────────────────────┘
```

## Prerequisites

- **.NET 8.0 SDK** — https://dotnet.microsoft.com/download/dotnet/8.0
- **Windows** — tested on Windows 10/11 (NAudio WASAPI, Silk.NET GLFW)
- **LED matrix controller** that accepts E.1.31/sACN UDP multicast on port 5568

## Build & Run

```bash
# Restore dependencies and build
dotnet restore
dotnet build

# Run with default shader (built-in demo)
dotnet run

# Run with a specific shader file
dotnet run --shader shaders/neon.glsl

# Enable audio reactivity (microphone by default)
dotnet run --audio --mic 0

# Use system playback capture (loopback) for audio
dotnet run --audio --loopback 0

# Demo mode: cycle through all shaders in /shaders directory
dotnet run --demo
dotnet run --demo --demo-time 15   # 15 seconds per shader

# Specify custom shader directory
dotnet run --shader-dir ./my-shaders

# Start web control panel on custom port (default: 8080)
dotnet run --web-port 3000

# Combine options
dotnet run --audio --loopback 0 --demo --web-port 8080

# Run headless (no OpenGL preview window — useful for server environments)
dotnet run --no-preview --shader shaders/balatro.glsl

# List available audio devices and exit
dotnet run --list-devices

# Show help
dotnet run --help
```

## Command-Line Options

| Option | Description |
|--------|-------------|
| `--no-preview` | Run headless (no OpenGL window) |
| `--shader <file>` | Path to a `.glsl` shader file |
| `--demo` | Cycle through all shaders in `shaders/` directory |
| `--demo-time <secs>` | Seconds per shader in demo mode (default: 10) |
| `--shader-dir <path>` | Directory containing `.glsl` shaders |
| `--audio` | Enable audio reactivity (microphone by default) |
| `--mic <idx>` | Use microphone at given index (default: 0) |
| `--loopback [idx]` | Capture system playback output (default: 0 = default speakers) |
| `--audio-device <idx>` | Fallback device index for either source |
| `--web-port <port>` | Start web control panel on given port (default: 8080) |
| `--list-devices` | List available audio input devices and exit |
| `--help, -h` | Show usage help |

## Web Control Panel

When started with `--web-port <port>` (or default 8080), a web UI is available at:

```
http://localhost:<port>
```

### Features

- **Shader dropdown** — browse and select from all `.glsl` files in shader directory
- 🔊 **Audio-reactive badges** — shaders with audio uniforms are marked
- **Loopback device name** — displays current loopback capture device friendly name
- **Status grid** — shows selected shader, total shader count, uptime, frames sent, send errors

### API Endpoints

| Endpoint | Method | Description | Response |
|----------|--------|-------------|----------|
| `/api/shaders` | GET | List all available shaders | JSON array of `{name, fileName, isAudioReactive}` |
| `/api/select-shader` | POST | Select a shader by filename | `{"success": true/false}` |
| `/api/status` | GET | Get current status and stats | JSON object (see below) |

#### Status Response

```json
{
  "selectedShader": "neon.glsl",
  "audioEnabled": false,
  "totalShaders": 31,
  "audioReactiveNames": ["audio_reactive_bass_pulse.glsl", ...],
  "uptimeSecs": 123.456,
  "framesSent": 7407,
  "sendErrors": 0,
  "loopbackDeviceName": "Speakers (Realtek Audio)"
}
```

## Shader Development

### Writing Shaders

Shaders should use the **ShaderToy-style** `mainImage` signature:

```glsl
void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    // Your shader code here
    // Use built-in uniforms: u_time, u_resolution, u_frame
}
```

### Auto-Wrapping

ShaderToy-style shaders are **automatically wrapped** with:

- `#version 330 core` (GLSL 3.3 Core profile)
- Uniforms: `u_time`, `u_resolution`, `u_frame`
- Defines: `iTime = u_time`, `iResolution = u_resolution`
- Helper function: `HSVtoRGB(vec3 c)` (also aliased as `HSVToRGB`)

### Audio-Reactive Shaders

To make a shader respond to audio, declare these uniforms:

```glsl
uniform float u_bass;       // ~20–80 Hz — deep kick/bass
uniform float u_lowmid;     // ~80–300 Hz — bass guitars, lower vocals
uniform float u_mid;        // ~300–1500 Hz — mids, snare, lead vocals
uniform float u_highmid;    // ~1500–6000 Hz — upper mids, cymbals attack
uniform float u_treble;     // ~6000–20000 Hz — air, shimmer
uniform float u_volume;     // overall RMS level (0–1)
```

The shader is automatically detected as audio-reactive if it contains any of these uniform declarations. Audio uniforms are only injected when `--audio` flag is used.

### Aspect Ratio Placeholder

Use `{AR}` in your shader source to inject the matrix aspect ratio (`Width/Height = 53/11 ≈ 4.82`):

```glsl
vec2 uv = (fragCoord - 0.5 * iResolution) / iResolution.y * {AR};
```

### Shader Directory

Shaders are loaded from:

1. The `shaders/` directory in the project root
2. Custom path via `--shader-dir <path>`
3. Individual file via `--shader <file>`

## E.1.31/sACN Configuration

### Packet Details

- **Protocol**: E.1.31 (sACN) v1.0 — ACN frame hierarchy with DMX payload
- **Port**: UDP 5568 (standard sACN port)
- **Universe IDs**: Valid range 1–63999 (configured in `Program.cs`)
- **Target IP**: Unicast or multicast address (default: `239.255.x.x` for multicast, configurable per-target)
- **DMX slots**: 1749 total (583 pixels × 3 channels RGB) across 4 universes

### Matrix Configuration

| Parameter | Value |
|-----------|-------|
| Width | 53 pixels |
| Height | 11 pixels |
| Total Pixels | 583 |
| Total Channels | 1749 (RGB per pixel) |
| Channel Order | RGB (R=slot N, G=slot N+1, B=slot N+2) |
| Layout | Straight raster order (row-by-row) |

### Pixel Mapping

Each framebuffer pixel `(x, y)` maps to LED channels:

```csharp
int ledIndex = y * Width + x;           // 0–582
int redChannel   = ledIndex * 3;         // DMX slot N
int greenChannel = ledIndex * 3 + 1;     // DMX slot N+1
int blueChannel  = ledIndex * 3 + 2;     // DMX slot N+2
```

## Audio Capture Details

### FFT Configuration

- **Sample rate**: 44,100 Hz
- **FFT size**: 2048 points (power of 2)
- **Frequency resolution**: ~21.5 Hz per bin (44100 / 2048)
- **Smoothing factor**: 0.35 (lower = faster transient response)
- **Peak decay**: 0.94 (auto-calibration adapts to signal level)

### Spectral Bands

| Band | Frequency Range | Typical Use |
|------|----------------|-------------|
| Bass | 20–80 Hz | Kick drum, bass guitar |
| Low Mid | 80–300 Hz | Bass guitars, lower vocals |
| Mid | 300–1500 Hz | Snare, lead vocals, instruments |
| High Mid | 1500–6000 Hz | Cymbals attack, upper mids |
| Treble | 6000–20000 Hz | Air, shimmer, hi-hats |

### Audio Sources

- **Microphone** (`--mic <idx>`): Captures from input devices via `WaveInEvent`
- **Loopback** (`--loopback [idx]`): Captures system playback output via `WasapiLoopbackCapture` targeting render endpoints (speakers/headphones)

## Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| Silk.NET | 2.23.0 | Cross-platform OpenGL + Windowing (GLFW) |
| NAudio | 2.2.1 | Audio capture and FFT analysis |
| System.Memory | 4.6.3 | Span<T> and memory utilities |

## Project Structure

```
ShaderToE131/
  Program.cs          ← Entry point, render loop, OpenGL init, shader lifecycle
  WebServer.cs        ← HttpListener web UI + REST API endpoints
  E131Sender.cs       ← E.1.31/sACN UDP packet builder and sender
  PixelMapper.cs      ← Framebuffer → LED channel mapping (53×11 matrix)
  AudioCapture.cs     ← NAudio audio capture, FFT, spectral band analysis
  ShaderToE131.csproj ← .NET 8.0 project file with NuGet dependencies

shaders/
  *.glsl              ← ShaderToy-style fragment shaders (mainImage signature)
```

## Troubleshooting

### No preview window appears

Use `--no-preview` to run headless when OpenGL window creation fails or is not needed. The LED matrix output continues to work in headless mode.

### Audio not detected

Run `dotnet run --list-devices` to see all available audio devices and their indices. Then use `--mic <idx>` or `--loopback <idx>` with the correct index.

### LED matrix not receiving data

- Verify target IP is reachable from your machine
- Check firewall allows UDP port 5568 outbound
- Confirm LED controller accepts E.1.31/sACN protocol (not DMX512 over USB/serial)
- Universe ID must be configured correctly in `Program.cs` (default: 1)

### Shader not loading after "Off"

The "Off (blank)" option disposes the shader program to stop GPU rendering. Switching back to a new shader via web UI or command line will reload it correctly. If issues persist, restart the application.

## License

This project is provided as-is for LED matrix visualization and creative coding purposes.
