---
name: shadertoe131
description: "Specialized agent for ShaderToE131 project — GLSL shader rendering to E.1.31/sACN LED matrix output with audio reactivity. Use when: modifying shaders, debugging sACN/E.1.31 transmission, audio analysis/FFT, WebGL/GLSL preview, pixel mapping, Silk.NET window management, WebServer controls."
tags: [shader, glsl, e131, sacn, led-matrix, silk-net, audio-reactive]
---

# ShaderToE131 Agent

## Project Overview

**ShaderToE131** is a C# .NET 8.0 application that renders GLSL shaders (ShaderToy-style) to OpenGL via Silk.NET, then maps the pixel output to an E.1.31 (sACN) universe for driving a 53×11 LED matrix. It features:

- **Audio-reactive shaders**: FFT-based spectral analysis (NAudio) with 5 frequency bands exposed as GLSL uniforms
- **E.1.31/sACN protocol**: UDP multicast to configurable IP/universe for DMX output
- **Pixel mapping**: Custom coordinate transformation from shader space to LED matrix layout
- **Web server**: Built-in HTTP server (port 8080) for shader selection and runtime controls
- **Demo mode**: Automatic cycling through all `.glsl` files in the shaders directory

## Key Architecture

```
Program.cs          ← Entry point, render loop, window/GL init, sACN sender lifecycle
AudioCapture.cs     ← NAudio microphone/loopback capture, FFT → 5-band spectrum values
E131Sender.cs       ← sACN packet construction & UDP multicast transmission
PixelMapper.cs      ← Shader pixel coordinate → LED matrix channel mapping (RGB = 3 ch)
WebServer.cs        ← HTTP server: shader listing, selection, status API, controls
shaders/*.glsl      ← ShaderToy-style fragment shaders (mainImage signature)
```

## Development Guidelines

### Shader Development
- Shaders use **ShaderToy-style** `mainImage(out vec4 color, vec2 coord)` signature
- Auto-wrapped with: `#version 330 core`, `u_time`, `u_resolution`, `u_frame` uniforms
- Audio uniforms (when enabled): `u_bass`, `u_lowmid`, `u_mid`, `u_highmid`, `u_treble`, `u_volume`
- Helper function injected: `HSVtoRGB(vec3 c)` and `#define HSVToRGB HSVtoRGB`

### E.1.31/sACN Configuration
- Universe ID: 1 (valid range: 1–63999)
- Target IP: `192.168.2.150` (modify in `Program.cs`)
- Matrix dimensions: 53×11 = 583 pixels × 3 channels = **1749 DMX channels**

### Audio Reactivity
- Sample rate: 44100 Hz, mono, 16-bit
- FFT size: 2048 points (~21.5 Hz resolution)
- Frequency bands: Bass (20–80), LowMid (80–300), Mid (300–1500), HighMid (1500–6000), Treble (6000–20000)

### Dependencies
- **Silk.NET** + GLFW + OpenGL: Windowing & hardware-accelerated rendering
- **NAudio**: Audio capture & FFT analysis
- **.NET 8.0**: Target framework, nullable reference types enabled

## Common Tasks

| Task | Files to Edit | Notes |
|------|---------------|-------|
| Add shader | `shaders/*.glsl` | Use `mainImage(FragColor, fragCoord)` signature |
| Modify pixel mapping | `ShaderToE131/PixelMapper.cs` | Affects coordinate transformation |
| Change E.1.31 target | `ShaderToE131/Program.cs` | Update `TargetIp`, `UniverseId` constants |
| Adjust FFT bands | `ShaderToE131/AudioCapture.cs` | Modify `_bands` array, update shader uniforms |
| Web server routes | `ShaderToE131/WebServer.cs` | Add/modify HTTP endpoints |
| Shader boilerplate | `ShaderToE131/Program.cs::BuildFragmentShader()` | Injects uniforms, helpers, wrapper code |

## Build & Run

```powershell
cd ShaderToE131
dotnet build
dotnet run                          # Normal mode: load shader via web UI or args
dotnet run --no-preview             # E.1.31 only, no OpenGL window
dotnet run --demo                   # Demo mode: cycle all shaders in /shaders
```

## Constraints & Gotchas

- **Thread safety**: `_pendingShaderChange` flag syncs HTTP thread with render loop — use `volatile`
- **GL context**: Silk.NET creates single OpenGL context; all shader compilation happens on main thread
- **UDP multicast**: sACN requires proper network config for multicast (239.255.x.x, port 5568)
- **Audio loopback**: Requires Windows Stereo Mix / Loopback capture permissions
