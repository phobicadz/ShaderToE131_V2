// Audio Spectrum Pro — a smoother, punchier spectrum analyzer for the 53x11 panel.
//
// What it does differently from audio_spectrum_analyzer_2:
//   * Continuous spectrum curve — the 5 bands are cross-faded with a triangular
//     kernel instead of being assigned to bar groups, so there are no visible
//     steps or plateaus between bass/mid/treble regions.
//   * Auto-gain + soft knee — quiet passages are boosted and loud ones compress
//     instead of saturating into a solid block, so detail survives at any volume.
//   * Stable per-bar character — each bar keeps its own slight offset (static hash)
//     plus a slow shimmer, so it looks like a real analyzer, not 5 flat plateaus.
//   * Magenta -> pink -> amber height ramp (HSV), bright peak caps, and a dim ambient
//     floor glow so the panel is never fully black.
//   * Declares its own audio uniforms, so it still compiles (and idles quietly)
//     when the app is run without --audio.
//
// Uses: u_bass, u_lowmid, u_mid, u_highmid, u_treble, u_volume

uniform float u_bass;
uniform float u_lowmid;
uniform float u_mid;
uniform float u_highmid;
uniform float u_treble;
uniform float u_volume;

// ── Continuous band interpolation ─────────────────────────────────────
// f runs 0..4 across the five bands; the bands are already log-spaced
// (60-300, 300-900, 900-2400, 2400-6000, 6000-20000 Hz), so a linear axis
// here is effectively a log-frequency axis.
float BandWeight(float f, float center)
{
    return clamp(1.0 - abs(f - center), 0.0, 1.0);
}

float SpectrumAt(float f)
{
    float e = 0.0;
    e += u_bass    * BandWeight(f, 0.0);
    e += u_lowmid  * BandWeight(f, 1.0);
    e += u_mid     * BandWeight(f, 2.0);
    e += u_highmid * BandWeight(f, 3.0);
    e += u_treble  * BandWeight(f, 4.0);
    return e;
}

// Stable per-bar offset: gives every bar its own personality without per-frame flicker.
float BarHash(float i)
{
    return fract(sin(i * 12.9898 + 78.233) * 43758.5453);
}

// Height ramp: magenta at the floor through pink/red into amber at the top.
// (Avoids blue and cyan, which read dim on a small LED matrix.)
vec3 RampColor(float h, float sat, float val)
{
    float hue = fract(mix(0.88, 1.13, pow(clamp(h, 0.0, 1.0), 0.9)));
    return HSVtoRGB(vec3(hue, sat, val));
}

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord.xy / iResolution.xy;
    float x = uv.x;   // frequency axis
    float y = uv.y;   // amplitude axis, 0 = bottom row

    float pxY = 1.0 / iResolution.y;   // one pixel tall, for crisp edges

    // ── Bar grid, derived from the actual panel size (3px pitch: 2px bar, 1px gap) ──
    float numBars  = floor(iResolution.x / 3.0);
    float barPitch = 1.0 / max(numBars, 4.0);
    float barIdx   = floor(x / barPitch);
    float barMid   = (barIdx + 0.5) * barPitch;
    float dBar     = abs(x - barMid) / barPitch;      // 0 at bar centre, 0.5 at the gap

    // ── Energy for this bar ────────────────────────────────────────────
    float nx = clamp(barMid, 0.0, 1.0);
    float f  = pow(nx, 1.12) * 4.0;                   // slight spread toward the treble end
    float e  = SpectrumAt(f);

    // Character + slow shimmer (small on purpose: the audio must stay in charge)
    e *= 0.88 + 0.12 * BarHash(barIdx);
    e *= 1.0 + 0.05 * sin(iTime * 1.7 + barIdx * 0.9);

    // Soft knee: fast attack, compresses instead of clipping. No auto-gain — the
    // audio side already peak-normalises every band, so boosting here just inflated
    // quiet passages.
    float level = clamp(1.0 - exp(-1.8 * e), 0.0, 1.0);

    // Contrast curve: quiet material stays near the floor, peaks still reach the top.
    level = level * level * (3.0 - 2.0 * level);

    float barTop = 0.015 + level * 0.96;

    // ── Bar body ───────────────────────────────────────────────────────
    float barMask = (1.0 - smoothstep(0.28, 0.36, dBar))
                  * (1.0 - smoothstep(barTop, barTop + pxY, y));

    float sat = mix(0.88, 0.45, smoothstep(0.70, 1.0, y));   // white-hot near the top
    float val = 0.62 + 0.38 * smoothstep(0.0, 1.0, level);
    vec3 color = RampColor(y, sat, val) * barMask;

    // ── Peak cap: one bright row on top of every bar ───────────────────
    float cap = (1.0 - smoothstep(0.0, 1.7 * pxY, abs(y - barTop)))
              * (1.0 - smoothstep(0.28, 0.36, dBar));
    color += vec3(1.0, 0.92, 0.80) * cap * 0.85 * level;   // caps only appear when there is real signal

    // ── Ambient floor: a faint idle glow, driven by signal, not by noise floor ──
    float floorGlow = exp(-y / (1.5 * pxY));
    float amb = 0.025 + 0.22 * level;
    color += RampColor(nx, 0.9, 1.0) * floorGlow * amb;

    // Faint bleed up the panel so a loud frame reads as a glow, not just bars.
    color += RampColor(nx, 0.85, 1.0) * 0.04 * level * (1.0 - y);

    fragColor = vec4(min(color, vec3(1.0)), 1.0);
}
