// Audio-reactive scrolling sine wave shader
// Horizontal scrolling sine waves that respond to audio levels
// Uses: u_bass, u_volume for reactivity

// ── Color palette function for the sine wave ───────────────────────────────
vec3 SineWaveColor(float t)
{
    // Rainbow gradient with neon twist
    vec3 base = 0.5 + 0.5 * cos(vec3(0, 2, 4) + t);
    
    // Add bass-enhanced red/orange tint for low frequencies
    float bassBoost = clamp(u_bass * 2.0, 0.0, 1.0);
    vec3 bassColor = mix(base, vec3(1.0, 0.5, 0.0), bassBoost * 0.7);
    
    return bassColor;
}

// ── Main shader ─────────────────────────────────────────────────────────────
void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = (fragCoord.xy - 0.5 * iResolution.xy) / iResolution.y;
    
    // Normalized coordinates for wave calculation
    float x = uv.x;           // Horizontal position (-0.5 to 0.5)
    float y = uv.y;           // Vertical position
    
    // ── Audio reactivity scaling factors ───────────────────────────────
    // Base amplitude scaled by overall volume (0.5x to 2.5x range)
    float ampScale = mix(0.5, 2.5, clamp(u_volume * 1.5, 0.0, 1.0));
    
    // Bass amplifies wave thickness/visibility
    float bassFactor = clamp(u_bass * 3.0, 0.2, 1.5);
    
    // ── Scrolling sine waves ───────────────────────────────────────────
    vec3 color = vec3(0.0);
    
    // Create multiple overlapping sine waves for richer effect
    int numWaves = 4;
    for (int i = 0; i < numWaves; i++) {
        float waveNum = float(i);
        
        // Wave properties
        float frequency = 2.0 + waveNum * 1.5;           // Higher freq for each subsequent wave
        float speed = 2.0 - waveNum * 0.3;               // Slower scrolling for higher waves
        
        // Calculate phase with time offset (scrolling effect)
        float phase = iTime * speed - x * frequency;
        
        // Sine wave calculation: amplitude varies by vertical position
        float sineValue = sin(phase);
        
        // Wave thickness and shaping using smoothstep
        float waveWidth = 0.15 + bassFactor * 0.1;
        float waveIntensity = smoothstep(waveWidth - 0.02, waveWidth, abs(sineValue));
        
        // Make waves fade toward top/bottom edges for vertical gradient effect
        float verticalFade = 1.0 - pow(abs(y) * 1.5, 1.5);
        waveIntensity *= max(verticalFade, 0.0);
        
        // Apply amplitude scaling and bass boost
        waveIntensity *= ampScale;
        waveIntensity = clamp(waveIntensity, 0.0, 1.2);
        
        // Color based on wave number with time-based hue rotation
        vec3 waveColor = SineWaveColor(phase + iTime * 0.5);
        color += waveColor * waveIntensity;
    }
    
    // ── Add bass-responsive background glow ────────────────────────────
    float bassGlow = clamp(u_bass * 1.5, 0.0, 0.8);
    vec3 bgGlow = vec3(0.1, 0.05, 0.2) * bassGlow;
    
    // ── Final color with brightness boost from volume ──────────────────
    float volBoost = mix(1.0, 1.8, clamp(u_volume, 0.0, 1.0));
    vec3 finalColor = (color + bgGlow) * volBoost;
    
    fragColor = vec4(clamp(finalColor, 0.0, 1.0), 1.0);
}
