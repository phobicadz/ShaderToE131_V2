// Audio Harmonic Rope — a vibrating string where each band drives one harmonic.
//
// Instead of drawing five bars, the five bands become the amplitudes of the first five
// modes of a string with fixed ends: sin(k*pi*x) for k = 1..5, each oscillating at its own
// (inharmonic) rate so the pattern never repeats exactly. Bass makes the whole string sway
// slowly, lowmid/mid add lobes, highmid/treble add fast ripple. Amplitudes are normalised
// against the total energy, so the string can never leave the panel, and steep sections
// catch more light — that is where the motion reads.
//
// Uses: u_bass, u_lowmid, u_mid, u_highmid, u_treble, u_volume

uniform float u_bass;
uniform float u_lowmid;
uniform float u_mid;
uniform float u_highmid;
uniform float u_treble;
uniform float u_volume;

const float PI = 3.14159265;

// Same response as the analyzers: soft knee plus a contrast curve, so quiet material
// stays small instead of being amplified into noise.
float Shape(float e)
{
    float l = clamp(1.0 - exp(-1.8 * max(e, 0.0)), 0.0, 1.0);
    return l * l * (3.0 - 2.0 * l);
}

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord.xy / iResolution.xy;
    float x = uv.x;
    float y = uv.y;
    float pxY = 1.0 / iResolution.y;

    float l1 = Shape(u_bass);
    float l2 = Shape(u_lowmid);
    float l3 = Shape(u_mid);
    float l4 = Shape(u_highmid);
    float l5 = Shape(u_treble);
    float total = l1 + l2 + l3 + l4 + l5;

    // Bright, energetic material makes the whole string move faster.
    float w = 1.1 + 2.2 * clamp(u_volume, 0.0, 1.0);

    // Sum of amplitudes is capped, so the string always stays inside the panel.
    float amp = 0.42 / max(total, 1.0);
    float t = iTime * w;
    float d = 0.0;
    d += l1 * sin(1.0 * PI * x) * cos(1.00 * t);
    d += l2 * sin(2.0 * PI * x) * cos(1.93 * t);
    d += l3 * sin(3.0 * PI * x) * cos(3.11 * t);
    d += l4 * sin(4.0 * PI * x) * cos(4.63 * t);
    d += l5 * sin(5.0 * PI * x) * cos(6.41 * t);
    d *= amp;

    float swing = clamp(total, 0.0, 1.0);          // 0 = string at rest, 1 = full excursion
    float yc = 0.5 + d;

    float dist  = abs(y - yc);
    float core  = 1.0 - smoothstep(0.4 * pxY, 1.4 * pxY, dist);
    float halo  = exp(-dist / (2.0 * pxY));   // squared below: a tight glow, not a haze
    float steep = clamp(abs(dFdx(d)) * iResolution.x * 0.6, 0.0, 1.0);

    // Warm where the bass lives, cool toward the treble end; steep parts burn hotter.
    // Blue and violet need extra value to read as brightly as red on a small matrix.
    float hue = mix(0.99, 0.62, x);
    float lift = 1.0 + 0.9 * x;
    vec3 c = HSVtoRGB(vec3(fract(hue), 0.90 - 0.35 * steep, (0.42 + 0.40 * steep + 0.28 * swing) * lift));

    vec3 color = c * core;
    color += HSVtoRGB(vec3(fract(hue), 0.85, 1.0)) * halo * halo * (0.30 + 0.55 * swing) * lift;

    // At rest the string stays a faint filament; it comes alive with the signal.
    color *= 0.35 + 0.65 * swing;

    fragColor = vec4(min(color, vec3(1.0)), 1.0);
}
