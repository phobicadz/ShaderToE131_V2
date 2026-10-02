// Fireflies — a few wandering lights blinking over dark grass
// Ambient (non-audio). Motion is slow and asymmetric so the wide strip
// never looks like a repeating tile.

float hash12(vec2 p)
{
    vec3 p3 = fract(vec3(p.xyx) * 0.103);
    p3 += dot(p3, p3.yzx + 33.33);
    return fract((p3.x + p3.y) * p3.z);
}

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    // x: -2.41..2.41, y: -0.5..0.5
    vec2 uv = (fragCoord - 0.5 * iResolution.xy) / iResolution.y;

    // Night base: warm dark at the bottom (ground), cold near-black above
    vec3 col = mix(vec3(0.008, 0.012, 0.020), vec3(0.030, 0.028, 0.014),
                   smoothstep(0.25, -0.5, uv.y));

    // Nine fireflies, each on its own wandering Lissajous path
    for (int i = 0; i < 9; i++)
    {
        float fi = float(i);
        float s1 = hash12(vec2(fi * 3.1, 1.7));
        float s2 = hash12(vec2(fi * 5.7, 9.2));
        float s3 = hash12(vec2(fi * 1.9, 4.4));

        vec2 p = vec2(
            2.0 * sin(iTime * (0.06 + 0.05 * s1) + s2 * 6.283)
          + 0.7  * sin(iTime * 0.21 + s3 * 6.283),
            0.30 * sin(iTime * (0.10 + 0.06 * s3) + s1 * 6.283)
          + 0.10 * sin(iTime * 0.37 + s2 * 6.283)
        );

        // Glow is tighter vertically than horizontally (11 rows vs 53)
        float d = length((uv - p) * vec2(1.0, 1.7));

        // Blink: slow fade in, quicker fade out
        float blink = clamp(0.55 + 0.45 * sin(iTime * (1.1 + 2.2 * s2) + s3 * 6.283), 0.0, 1.0);
        blink = blink * blink;

        float glow = exp(-d * 16.0) + 0.30 * exp(-d * 4.5);
        vec3 warm = mix(vec3(1.0, 0.82, 0.30), vec3(0.65, 1.0, 0.45), s1);
        col += warm * glow * blink * 1.7;
    }

    // Dark grass silhouette along the bottom edge
    float blade = 0.03 * sin(uv.x * 22.0) + 0.02 * sin(uv.x * 41.0);
    col *= smoothstep(-0.42 + blade, -0.20 + blade, uv.y) * 0.85 + 0.15;

    // Keep the whole thing dim — fireflies should read as points of light
    fragColor = vec4(clamp(col, 0.0, 1.0), 1.0);
}
