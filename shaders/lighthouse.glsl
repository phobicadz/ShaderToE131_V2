// Lighthouse — a fog-bound beam sweeping across the strip
// Ambient (non-audio). The beam crosses the wide axis, so the panel breathes
// bright-left / bright-right instead of showing a static gradient.

float hash12(vec2 p)
{
    vec3 p3 = fract(vec3(p.xyx) * 0.103);
    p3 += dot(p3, p3.yzx + 33.33);
    return fract((p3.x + p3.y) * p3.z);
}

float vnoise(vec2 p)
{
    vec2 i = floor(p);
    vec2 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    float a = hash12(i);
    float b = hash12(i + vec2(1.0, 0.0));
    float c = hash12(i + vec2(0.0, 1.0));
    float d = hash12(i + vec2(1.0, 1.0));
    return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}

float fbm(vec2 p)
{
    float v = 0.0;
    float a = 0.5;
    for (int i = 0; i < 4; i++)
    {
        v += a * vnoise(p);
        p = p * 2.07 + vec2(2.3, 7.1);
        a *= 0.5;
    }
    return v;
}

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    // x: -2.41..2.41, y: -0.5..0.5
    vec2 uv = (fragCoord - 0.5 * iResolution.xy) / iResolution.y;
    float t = iTime;

    // Lamp sits just below the bottom edge, near the centre
    vec2 lamp = vec2(0.0, -0.62);

    // Sweep: a beam that swings left and right, pausing at the ends
    float sweep = sin(t * 0.55) * 1.15 + 0.25 * sin(t * 0.21);
    vec2 dir = normalize(uv - lamp);
    vec2 dirSweep = normalize(vec2(sweep, 1.0));
    float align = dot(dir, dirSweep);
    float beam = pow(max(align, 0.0), 26.0);

    // Beam widens with distance, and is brighter near the source
    float dist = length(uv - lamp);
    float wedge = beam * exp(-dist * 0.35);

    // Fog: drifting fbm density the beam lights up
    float fog = fbm(vec2(uv.x * 1.3 - t * 0.08, uv.y * 2.0 + t * 0.15));
    float density = 0.35 + 0.65 * fog;

    vec3 beamCol = mix(vec3(1.0, 0.88, 0.62), vec3(1.0, 0.98, 0.88), wedge);
    vec3 col = beamCol * wedge * density * 1.5;

    // Ambient haze the beam leaks into
    col += vec3(0.05, 0.06, 0.08) * density * 0.35;

    // Lamp housing glow + a hard hot core
    float lampGlow = exp(-dist * 7.0);
    col += vec3(1.0, 0.95, 0.80) * lampGlow * (0.7 + 0.3 * sin(t * 6.0));

    // Faint horizon line and slow drifting fog bands below it
    float horizon = exp(-abs(uv.y - 0.30) * 12.0);
    col += vec3(0.03, 0.05, 0.07) * horizon;

    fragColor = vec4(clamp(col, 0.0, 1.0), 1.0);
}
