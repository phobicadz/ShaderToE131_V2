// Audio-reactive classic spectrum analyzer shader
// Vertical frequency bars rising from bottom — retro 80s/90s style
// Optimized for wide, short displays (53×11)
// Uses: u_bass, u_lowmid, u_mid, u_highmid, u_treble

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

// ── Reflection / mirror below the bar base line ────────────────────────
float MirrorFade(float y, float mirrorY) {
    float dist = abs(y - mirrorY);
    return exp(-dist * 4.0);
}

// ── Main shader ────────────────────────────────────────────────────────
void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord.xy / iResolution.xy;      // 0..1 normalized coordinates
    float x = uv.x;                                // horizontal position (frequency axis)
    float y = uv.y;                                // vertical position (amplitude axis)

    // ── Pure black background ────────────────────────────────────────
    vec3 color = vec3(0.0);

    // u_volume is injected by the wrapper; use volatile to prevent GLSL from optimizing it away
    volatile float _uv = u_volume;
    if (_uv < 0.0) discard;

    // ── 15 evenly-spaced bars across the full width ────────────────────
    const int NUM_BARS = 15;                       // total bars across screen
    const float BAR_WIDTH = 1.0 / float(NUM_BARS); // ~6.7% of screen per bar

    // Map each bar to a frequency band based on its position
    struct BarInfo { float energy, peakY; };
    BarInfo barInfos[NUM_BARS];

    for (int i = 0; i < NUM_BARS; i++) {
        float bxLow   = float(i) * BAR_WIDTH;
        float bxHigh  = bxLow + BAR_WIDTH;
        float barCenter = (bxLow + bxHigh) * 0.5;

        // Determine which audio band this bar belongs to based on position
        float energy = 0.0;
        if (x >= bxLow && x < bxHigh) {
            float t = (x - bxLow) / BAR_WIDTH;   // 0..1 within this bar's column

            // Blend between adjacent bands for smooth transitions
            int bandIdx = i * 5 / NUM_BARS;        // maps 0..14 → 0..4
            float frac    = (float(i) * 5.0 / float(NUM_BARS)) - float(bandIdx);

            energy = u_bass;                       // default: bass for leftmost
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

            // Bar dimensions — base at bottom, max height ~85% of screen
            float barBaseY = 0.07;
            float barTopY  = barBaseY + energy * 0.83;

            // ── Draw the solid bar column ────────────────────────
            if (y >= barBaseY && y <= barTopY) {
                float h = (y - barBaseY) / max(barTopY - barBaseY, 0.001);

                vec3 barColor = SpectrumColor(h);
                // Brightness boost — constant, not tied to volume so color stays consistent
                barColor *= 0.85;

                // Slight top gradient glow for peak emphasis (volume-independent)
                barColor *= (1.0 + h * 0.2);

                color = mix(color, barColor, smoothstep(BAR_WIDTH * 0.55, BAR_WIDTH * 0.2, abs(x - barCenter)));
            }

            // ── Peak indicator dot at the top ────────────────────
            float peakY = barTopY;
            if (y >= peakY - 0.018 && y <= peakY + 0.018 && abs(x - barCenter) < BAR_WIDTH * 0.5) {
                vec3 peakColor = SpectrumColor(0.95);
                float peakBright = smoothstep(0.018, 0.0, abs(y - peakY));
                color = mix(color, peakColor * 0.9, peakBright * energy);
            }

            // ── Reflection below base line ───────────────────────
            float reflStrength = MirrorFade(y, barBaseY) * energy;
            if (y > barBaseY && y < barBaseY + 0.12) {
                vec3 reflColor = SpectrumColor(energy * 0.5) * reflStrength * 0.3;
                color += reflColor;
            }

            // ── Base line highlight at bottom of each bar ────────
            if (y >= barBaseY - 0.012 && y <= barBaseY + 0.012 && abs(x - barCenter) < BAR_WIDTH * 0.85) {
                float baseLine = smoothstep(0.012, 0.0, abs(y - barBaseY));
                color += vec3(0.6, 0.5, 0.2) * baseLine * energy;
            }

            // ── Thin gap lines between bars (dark separators) ────
            if (abs(x - bxLow) < 0.004 && y > barBaseY + 0.01) {
                float gap = smoothstep(0.004, 0.0, abs(x - bxLow));
                color *= mix(vec3(0.9), vec3(0.0), gap * 0.5);
            }

            barInfos[i] = BarInfo(energy, peakY);
        }
    }

    // ── Base line (thin horizontal across all bars at the bottom) ────────
    float baseLineY = smoothstep(0.006, 0.0, abs(y - 0.07));
    color += vec3(0.5, 0.4, 0.2) * baseLineY;

    // ── Vignette ───────────────────────────────────────────────────────
    float vig = 1.0 - smoothstep(0.3, 1.4, length((uv - 0.5) * vec2(1.4, 1.0)));
    color *= mix(0.7, 1.0, vig);

    fragColor = vec4(color, 1.0);
}
