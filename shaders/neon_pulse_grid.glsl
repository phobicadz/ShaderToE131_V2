// Audio-reactive shader: Neon Pulse Grid
// Uses: u_bass, u_lowmid, u_mid, u_highmid, u_treble, u_volume

// ── Palette function for dynamic color cycling (from Palettes.glsl) ───
vec3 pal( in float t, in vec3 a, in vec3 b, in vec3 c, in vec3 d )
{
    return a + b*cos( 6.28318*(c*t+d) );
}

// Neon palette: Cyan → Magenta → Yellow → White hot
vec3 GetNeonColor(float t)
{
    // Neon palette: Cyan → Magenta → Yellow → White hot
    vec3 c = vec3(0.0);
    
    if (t < 0.25) {
        // Cyan to Blue-Magenta
        c = mix(vec3(0.0, 1.0, 1.0), vec3(0.8, 0.0, 1.0), t * 4.0);
    } else if (t < 0.5) {
        // Magenta to Purple
        c = mix(vec3(0.8, 0.0, 1.0), vec3(0.5, 0.0, 0.8), (t - 0.25) * 4.0);
    } else if (t < 0.75) {
        // Purple to Pink-White
        c = mix(vec3(0.5, 0.0, 0.8), vec3(1.0, 0.5, 1.0), (t - 0.5) * 4.0);
    } else {
        // White hot peaks
        c = mix(vec3(1.0, 0.5, 1.0), vec3(1.0, 1.0, 1.0), (t - 0.75) * 4.0);
    }
    
    return c;
}

// ── Get grid cell value with smooth edges ─────────────────────────────
float GetGridCell(vec2 uv, vec2 position, float size)
{
    // Distance-based soft circle for each grid point
    float dist = distance(position.xy, uv);
    float cell = smoothstep(size * 1.5, size, dist);
    
    // Add some radial falloff for softer edges
    cell *= (1.0 - dist / 2.0);
    
    return cell;
}

// ── Main shader ───────────────────────────────────────────────────────
void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    // Normalized coordinates (-1 to 1 range for better symmetry)
    vec2 uv = (fragCoord - 0.5 * iResolution.xy) / min(iResolution.x, iResolution.y);
    
    // ── Bass-driven grid expansion/contraction ───────────────────────
    float bassPulse = u_bass * 1.8;           // Bass drives scale changes
    
    // Create a dynamic grid pattern that pulses with bass
    vec2 gridUV = uv * (0.5 + bassPulse);
    
    // ── Generate grid points using sine wave interference ────────────
    float gridSize = 6.0 - u_bass * 3.0;      // Bass reduces grid density
    
    float xGrid = sin(gridUV.x * gridSize) * cos(gridUV.y * gridSize);
    float yGrid = cos(gridUV.x * gridSize) * sin(gridUV.y * gridSize);
    
    // Combine for interesting interference pattern
    float gridPattern = (xGrid + yGrid) * 0.5;
    
    // ── Low-mid frequency: adds spiral twist to the grid ─────────────
    float lowmidTwist = u_lowmid * sin(gridUV.x * gridSize - iTime * 0.2);
    gridPattern += lowmidTwist * 0.3;
    
    // ── Mid-frequency color cycling ────────────────────────────────
    float midPhase = u_mid * 2.0 + iTime * (0.1 + u_lowmid * 0.4);  // Low-mid drives animation speed
    
    vec3 gridColor = GetNeonColor(fract(midPhase));
    
    // Add bass and low-mid modulation to color intensity
    gridColor *= (0.5 + u_bass * 0.3 + u_lowmid * 0.2);
    
    // ── High-mid frequency: adds radial lines to the grid ────────────
    float angle = atan(uv.y, uv.x);
    float highmidLines = abs(sin(angle * (8.0 + u_highmid * 12.0))) * u_highmid;
    
    // Add high-mid contribution to pattern
    gridPattern += highmidLines * 0.4;
    
    // ── Treble adds sparkles and noise effects ───────────────────────
    float trebleNoise = fract(sin(dot(uv, vec2(12.9898, 78.233))) * 43758.5453);
    
    // Sparkle effect on high frequencies - increases with treble
    float sparkles = step(0.96 - u_treble * 0.5, trebleNoise) * u_treble;
    
    // ── Volume-based brightness control ──────────────────────────────
    float volumeMod = clamp(u_volume * 2.0, 0.3, 1.0);
    
    // ── Combine all effects ──────────────────────────────────────────
    float pixel = gridPattern;
    
    // Boost the signal with bass for more visible low-frequency response
    pixel *= (0.5 + bassPulse * 0.8);
    
    // Add high-mid and treble contributions to pattern
    pixel += highmidLines * u_highmid * 0.2;
    pixel += sparkles * (0.3 + u_treble * 0.4);
    
    // Apply smooth thresholding for cleaner edges
    pixel = smoothstep(0.2, 0.9, pixel) * volumeMod;
    
    // ── Final output with glow effect and color modulation by all bands ───────────────
    vec3 finalColor = gridColor * pixel;
    
    // Add bass glow to the final color
    float bassGlow = exp(-length(uv) * (1.5 - u_bass)) * u_bass * 0.2;
    finalColor += bassGlow * vec3(0.1, 0.8, 0.9);
    
    // Apply volume-based brightness control
    finalColor *= clamp(volumeMod, 0.3, 1.0);
    
    fragColor = vec4(finalColor, 1.0);
}
