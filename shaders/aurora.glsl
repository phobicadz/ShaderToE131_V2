// Aurora — slow green/violet curtains drifting over a dark horizon
// Ambient (non-audio). Tuned for 53x11: curtains run vertically, motion travels
// along the wide axis so the pattern reads as one continuous sweep.

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
    for (int i = 0; i < 5; i++)
    {
        v += a * vnoise(p);
        p = p * 2.03 + vec2(1.7, 9.7);
        a *= 0.5;
    }
    return v;
}

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    // x: -2.41..2.41 across the strip, y: -0.5..0.5
    vec2 uv = (fragCoord - 0.5 * iResolution.xy) / iResolution.y;
    float t = iTime * 0.12;

    // Curtain spine: a slowly flowing height field along the wide axis
    float drift = fbm(vec2(uv.x * 0.55 - t, t * 0.6));
    float spine = -0.30 + drift * 0.85;

    // Vertical ray structure — the striations of a real aurora
    float rays = fbm(vec2(uv.x * 2.6 + t * 0.4, uv.y * 1.3 - t * 1.4));
    float rayMask = 0.40 + 0.60 * rays;

    // Soft band around the spine, brighter just below its top edge
    float d = uv.y - spine;
    float band = exp(-abs(d) * 5.0) * exp(-max(-d, 0.0) * 2.5);

    // Hue: green core, violet tips, drifting along x and time
    float hue = 0.33 + 0.15 * sin(uv.x * 0.45 + t * 1.7) + 0.12 * drift;
    float val = band * rayMask;

    vec3 col = HSVtoRGB(vec3(fract(hue), clamp(0.85 - 0.3 * band, 0.35, 1.0), 1.0)) * val;

    // Faint green wash along the lower edge, like light reflecting off snow
    col += vec3(0.04, 0.14, 0.08) * exp(-abs(uv.y + 0.48) * 3.0) * (0.4 + 0.6 * drift);

    // Pin-prick stars hashed on the pixel grid so they never crawl
    float star = hash12(floor(fragCoord) * 0.7 + 13.0);
    float twinkle = 0.6 + 0.4 * sin(iTime * 2.0 + star * 40.0);
    col += vec3(0.8, 0.85, 1.0) * step(0.985, star) * twinkle * 0.5 * (1.0 - step(0.4, val));

    fragColor = vec4(clamp(col, 0.0, 1.0), 1.0);
}
