// Ember — a campfire flame front along the bottom edge with rising sparks
// Ambient (non-audio). The flame line is an fbm height field scrolling downward,
// so it flickers like fire instead of scrolling like a conveyor belt.

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
        p = p * 2.11 + vec2(3.1, 1.9);
        a *= 0.5;
    }
    return v;
}

// Blackbody-ish flame ramp: white-hot -> yellow -> orange -> deep red -> dark
vec3 flameRamp(float h)
{
    vec3 c = mix(vec3(0.12, 0.01, 0.00), vec3(0.85, 0.12, 0.01), smoothstep(0.0, 0.35, h));
    c = mix(c, vec3(1.0, 0.55, 0.05), smoothstep(0.35, 0.65, h));
    c = mix(c, vec3(1.0, 0.92, 0.55), smoothstep(0.65, 0.90, h));
    c = mix(c, vec3(1.0, 1.0, 0.95), smoothstep(0.90, 1.0, h));
    return c;
}

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    // x: -2.41..2.41, y: -0.5..0.5
    vec2 uv = (fragCoord - 0.5 * iResolution.xy) / iResolution.y;
    float t = iTime;

    // Flame height along the strip: taller in the middle, licks upward
    float shape = 1.0 - 0.35 * smoothstep(0.4, 2.2, abs(uv.x));
    float n = fbm(vec2(uv.x * 1.6 + 0.15 * t, -t * 1.15));
    float h = (0.02 + 0.42 * n) * shape;

    // Heat = how far below the flame line this pixel sits (soft falloff, so the
    // flame never blows out to pure white across a whole row)
    float heat = 1.0 - exp(-max(h - uv.y, 0.0) * 2.6);

    // Fine flicker so the flame breaks up into tongues instead of a solid block
    float flicker = 0.75 + 0.25 * vnoise(vec2(uv.x * 9.0 - t * 2.0, uv.y * 6.0 - t * 4.0));
    vec3 col = flameRamp(heat * flicker);

    // Warm spill onto the "ground" below the flame
    col += vec3(0.12, 0.035, 0.008) * exp(-max(uv.y + 0.5, 0.0) * 0.8) * (0.35 + 0.65 * heat);

    // Embers: a handful of sparks rising and drifting, wrapping back to the base
    for (int i = 0; i < 12; i++)
    {
        float fi = float(i);
        float s1 = hash12(vec2(fi * 2.7, 5.3));
        float s2 = hash12(vec2(fi * 6.1, 8.9));
        float speed = 0.25 + 0.45 * s1;
        float phase = fract(t * speed + s2);

        vec2 p = vec2(
            mix(-1.9, 1.9, s1) + 0.35 * sin(t * (1.0 + 2.0 * s2) + s1 * 6.283) * phase,
            -0.45 + phase * (0.75 + 0.35 * s2)
        );

        float d = length((uv - p) * vec2(1.0, 1.5));
        float fade = (1.0 - phase) * (0.4 + 0.6 * s2);
        col += mix(vec3(1.0, 0.55, 0.12), vec3(1.0, 0.85, 0.35), s2) * exp(-d * 22.0) * fade;
    }

    fragColor = vec4(clamp(col, 0.0, 1.0), 1.0);
}
