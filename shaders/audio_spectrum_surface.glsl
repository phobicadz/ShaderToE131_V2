// Audio Spectrum Surface — the spectrum as a liquid silhouette, not bars.
//
// The continuous band envelope (same cross-fading kernel as audio_spectrum_pro) becomes
// the top edge of a filled body: dark and saturated at the bottom, brightening toward the
// surface, with a near-white highlight line on the surface itself and a faint mist above it.
// A slow travelling wave makes it slosh and the treble band adds small ripples on top, so
// it reads as liquid rather than as a chart. No bars, no gaps — the whole panel is used.
//
// Uses: u_bass, u_lowmid, u_mid, u_highmid, u_treble, u_volume

uniform float u_bass;
uniform float u_lowmid;
uniform float u_mid;
uniform float u_highmid;
uniform float u_treble;
uniform float u_volume;

float Shape(float e)
{
    float l = clamp(1.0 - exp(-1.8 * max(e, 0.0)), 0.0, 1.0);
    return l * l * (3.0 - 2.0 * l);
}

float BandWeight(float f, float center)
{
    return clamp(1.0 - abs(f - center), 0.0, 1.0);
}

// Continuous spectrum: cross-fade the five bands instead of assigning them to regions.
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

// Loudest shaped band in this frame — used only to keep the idle panel dim.
float Loudness()
{
    return max(max(Shape(u_bass), Shape(u_lowmid)), max(Shape(u_mid), max(Shape(u_highmid), Shape(u_treble))));
}

// Magenta at the bass end through pink into amber at the treble end.
vec3 Palette(float p, float sat, float val)
{
    float hue = fract(mix(0.88, 1.13, pow(clamp(p, 0.0, 1.0), 0.9)));
    return HSVtoRGB(vec3(hue, sat, val));
}

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord.xy / iResolution.xy;
    float x = uv.x;   // frequency axis
    float y = uv.y;   // 0 = bottom row
    float pxY = 1.0 / iResolution.y;

    // ── Envelope ───────────────────────────────────────────────────────
    float f = pow(x, 1.12) * 4.0;
    float e = SpectrumAt(f);

    // Sloshing body + small ripples where there is high-frequency material
    e *= 1.0 + 0.10 * sin(iTime * 1.25 - x * 7.0);
    e += 0.03 * u_treble * sin(iTime * 6.5 + x * 26.0);
    e = max(e, 0.0);

    // Same response as the analyzers: soft knee, then a contrast curve so quiet stays low
    float level = clamp(1.0 - exp(-1.8 * e), 0.0, 1.0);
    level = level * level * (3.0 - 2.0 * level);

    float surf = 0.012 + level * 0.92;

    // ── Body: dark deep, bright toward the surface ─────────────────────
    float depth = clamp(y / max(surf, 1e-4), 0.0, 1.0);
    float sat = mix(0.85, 0.52, depth * depth);       // pale and hot just under the surface
    float val = 0.42 + 0.50 * depth;                  // the body stays visibly lit all the way down
    vec3 color = Palette(x, sat, val)
               * (1.0 - smoothstep(surf, surf + 0.9 * pxY, y));

    // ── Surface highlight ──────────────────────────────────────────────
    float line = 1.0 - smoothstep(0.0, 1.6 * pxY, abs(y - surf));
    color += vec3(1.0, 0.93, 0.85) * line * (0.07 + 0.93 * level);

    // ── Mist above the surface, so the edge is not a hard cut ─────────
    float mist = exp(-max(y - surf, 0.0) / (2.2 * pxY)) * smoothstep(surf, surf + 1.5 * pxY, y);
    color += Palette(x, 0.85, 1.0) * mist * 0.12 * level;

    // With nothing playing, the surface is a faint warm line instead of a lit panel.
    color *= 0.22 + 0.78 * Loudness();

    fragColor = vec4(min(color, vec3(1.0)), 1.0);
}
