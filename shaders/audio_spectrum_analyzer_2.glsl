// ── Palette: iconic green → yellow → orange → red gradient ──────────────
vec3 SpectrumColor(float t)
{
    vec3 c = vec3(0.0);
    if (t < 0.25) {
        // Green → Yellow
        c = mix(vec3(0.0, 0.8, 0.0), vec3(1.0, 1.0, 0.0), t * 4.0);
    } else if (t < 0.5) {
        // Yellow → Orange
        c = mix(vec3(1.0, 1.0, 0.0), vec3(1.0, 0.6, 0.0), (t - 0.25) * 4.0);
    } else if (t < 0.75) {
        // Orange → Red
        c = mix(vec3(1.0, 0.6, 0.0), vec3(1.0, 0.1, 0.0), (t - 0.5) * 4.0);
    } else {
        // Red → White-hot peak
        c = mix(vec3(1.0, 0.1, 0.0), vec3(1.0, 1.0, 1.0), (t - 0.75) * 4.0);
    }
    return c;
}

// ── Main shader ────────────────────────────────────────────────────────
void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord.xy / iResolution.xy;      // 0..1 normalized coordinates
    float x = uv.x;                                // horizontal position (frequency axis)
    float y = 1.0 - uv.y;                          // vertical position (amplitude axis) — inverted for LED matrix

    // ── Pure black background ────────────────────────────────────────
    vec3 color = vec3(0.0);

    // u_volume is injected by the wrapper; use volatile to prevent GLSL from optimizing it away
    volatile float _uv = u_volume;
    if (_uv < 0.0) discard;

    // ── 53 evenly-spaced bars across the full width (matches 53 pixel display width)
    const int NUM_BARS = 26;                       // one bar per horizontal pixel
    const float BAR_WIDTH = 1.0 / float(NUM_BARS); // ~1.9% of screen per bar

    for (int i = 0; i < NUM_BARS; i++) {
        float bxLow   = float(i) * BAR_WIDTH;
        float bxHigh  = bxLow + BAR_WIDTH;
        float barCenter = (bxLow + bxHigh) * 0.5;

        // Determine which audio band this bar belongs to based on position
        if (x >= bxLow && x < bxHigh) {
            float t = (x - bxLow) / BAR_WIDTH;   // 0..1 within this bar's column

            // Blend between adjacent bands for smooth transitions
            int bandIdx = i * 5 / NUM_BARS;        // maps 0..14 → 0..4
            float frac    = (float(i) * 5.0 / float(NUM_BARS)) - float(bandIdx);

            float energy = u_bass;                       // default: bass for leftmost
            if (bandIdx == 0 && frac < 0.5) {
                energy = mix(u_bass, u_lowmid, frac * 2.0);
            } else if (bandIdx == 1 && frac < 0.5) {
                energy = mix(u_bass, u_lowmid, (frac - 0.5) * 2.0);
            } else if (bandIdx == 1) {
                energy = mix(u_lowmid, u_mid, (frac - 0.5) * 2.0);
            } else if (bandIdx == 2 && frac < 0.5) {
                energy = mix(u_lowmid, u_mid, (frac - 0.5) * 2.0);
            } else if (bandIdx == 2) {
                energy = mix(u_mid, u_highmid, (frac - 0.5) * 2.0);
            } else if (bandIdx == 3 && frac < 0.5) {
                energy = mix(u_mid, u_highmid, (frac - 0.5) * 2.0);
            } else if (bandIdx == 3) {
                energy = mix(u_highmid, u_treble, (frac - 0.5) * 2.0);
            } else if (bandIdx == 4 && frac < 0.5) {
                energy = mix(u_highmid, u_treble, (frac - 0.5) * 2.0);
            } else {
                energy = u_treble;                   // default: treble for rightmost
            }

            // Add per-bar variation so each bar has unique character
            float variation = fract(sin(dot(fragCoord.xy, vec2(12.9898 + float(i), 78.233))) * 43758.5453);
            energy *= mix(0.75 + variation * 0.25, 1.0, smoothstep(0.0, 0.5, t));

            // Clamp to [0..1]
            energy = clamp(energy, 0.0, 1.0);

            // Boost energy for more dramatic response — apply a mild power curve to exaggerate peaks
            energy = pow(energy, 0.75);
            energy = clamp(energy, 0.0, 1.0);

            // Bar dimensions — base at bottom of screen (Y=0 in inverted coords), bars grow upward
            // For 11-pixel height display: use full height from Y≈0 to Y≈1 (inverted)
            float barBaseY = 0.0;                      // bottom of screen (inverted Y)
            float barTopY  = energy * 1.0;             // bars grow up to full height

            // ── Draw the solid bar column ────────────────────────
            if (y >= barBaseY && y <= barTopY) {
                float h = y / max(barTopY, 0.001);   // normalized height within bar

                vec3 barColor = SpectrumColor(h);
                // Brightness boost — constant so color stays consistent regardless of volume
                barColor *= 0.85;

                // Slight top gradient glow for peak emphasis (volume-independent)
                barColor *= (1.0 + h * 0.2);

                // Use pixel-perfect width matching the display resolution
                float gap = BAR_WIDTH;           // thin gaps between bars
                color = mix(color, barColor, smoothstep(gap * 1.5, gap, abs(x - barCenter)));
            }

            // ── Peak indicator with decay at the top (white bits fall slower) ────────────────────
            float peakY = barTopY;
            
            // Use iTime to create a smooth fading effect for white portion
            // The higher y values fade out more slowly, creating "peak hold" effect
            
            if (y >= peakY - BAR_WIDTH * 0.5 && y <= peakY + BAR_WIDTH * 0.3 && abs(x - barCenter) < BAR_WIDTH * 0.5) {
                vec3 peakColor = SpectrumColor(1.0);
                
                // Distance from top of bar (0 at top, increases downward)
                float distFromPeak = peakY - y;
                
                // White portion decays slower using exponential falloff
                // Higher bars (more energy) have longer decay tail
                float fadeAmount = exp(-distFromPeak * 8.0);
                
                color = mix(color, peakColor * 0.95 * fadeAmount, smoothstep(BAR_WIDTH * 0.3, 0.0, abs(y - peakY)));
            }

            // ── Base line highlight at bottom of each bar (thin bright line) ────────
            if (y >= 0.0 && y < BAR_WIDTH * 0.3 && abs(x - barCenter) < BAR_WIDTH * 0.85) {
                float baseLine = smoothstep(BAR_WIDTH * 0.15, 0.0, y);
                color += vec3(0.6, 0.5, 0.2) * baseLine * energy;
            }
        }
    }

    fragColor = vec4(color, 1.0);
}