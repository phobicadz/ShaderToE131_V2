// Rainstorm — rain streaks on a wide window with occasional lightning
// Ambient (non-audio). Each pixel column gets its own drop speed and phase,
// so the rain never forms a visible repeating pattern across 53 pixels.

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
    float t = iTime;

    // Storm sky: deep slate blue, slightly brighter toward the top
    vec3 col = mix(vec3(0.015, 0.020, 0.035), vec3(0.045, 0.055, 0.085),
                   smoothstep(-0.5, 0.5, uv.y));

    // ── Lightning: random flashes on a ~1.4 s gate, fast decay ─────────
    float tick = floor(t * 0.7);
    float gate = hash12(vec2(tick, 7.0));
    float local = fract(t * 0.7);
    float flash = step(0.72, gate) * exp(-local * 7.0);
    float flash2 = step(0.9, hash12(vec2(tick + 0.5, 3.0))) * exp(-local * 13.0); // double-strike
    flash += flash2;

    // A bolt: jagged vertical seam that only exists while flashing
    float boltX = mix(-1.6, 1.6, hash12(vec2(tick, 2.3)));
    float jag = 0.22 * sin(uv.y * 12.0 + gate * 6.283) + 0.10 * sin(uv.y * 31.0);
    float bolt = exp(-abs(uv.x - boltX - jag) * 9.0) * (0.4 + 0.6 * abs(uv.y + 0.2));
    col += vec3(0.75, 0.80, 1.0) * bolt * flash * 1.4;

    // Flash lights up the whole sheet of rain
    col += vec3(0.10, 0.12, 0.18) * flash;

    // ── Rain: one streak per pixel column ──────────────────────────────
    float cell = floor(fragCoord.x);
    float s1 = hash12(vec2(cell, 3.7));
    float s2 = hash12(vec2(cell, 9.1));

    float speed = 0.9 + 1.9 * s1;
    float dropY = 0.65 - fract(t * speed * 0.55 + s2) * 1.5;   // falls downward, wraps
    float dy = uv.y - dropY;

    // Bright leading head at the BOTTOM, tail fading upward above it.
    // dy > 0 is above the head (gentle tail), dy < 0 is below it (hard cut),
    // so the streak reads as falling even though the head already moves down.
    float dx = abs(fragCoord.x - (cell + 0.5));
    float streak = exp(-max(dy, 0.0) * 6.0) * exp(-max(-dy, 0.0) * 45.0);
    streak *= exp(-dx * dx * 1.2);
    col += vec3(0.55, 0.68, 0.95) * streak * (0.5 + 0.5 * s2) * 1.3;

    // Fine drizzle haze so the strip never goes fully dark between drops
    float drizzle = hash12(floor(fragCoord * 0.5) + vec2(t * 3.0, 0.0));
    col += vec3(0.18, 0.24, 0.36) * step(0.94, drizzle) * 0.12;

    // Splash glow along the bottom edge where the rain lands
    float splash = exp(-max(uv.y + 0.5, 0.0) * 9.0);
    col += vec3(0.16, 0.22, 0.34) * splash * (0.25 + 0.35 * sin(t * 6.0 + cell * 1.7)) * 0.35;

    fragColor = vec4(clamp(col, 0.0, 1.0), 1.0);
}
