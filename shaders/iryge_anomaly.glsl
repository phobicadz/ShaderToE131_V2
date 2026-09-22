// "iryge-Anomaly" — animated orange sine wave on a deep maroon field,
// after the desktop wallpaper: glowing wireframe ribbon with faint mesh.

float lineAA(float v, float w) { return max(0.0, 1.0 - abs(v) / w); }
float gg(float t) { return exp(-t * t); }

// main wave profile: one big crest on the left, a shallow rise on the right.
// Returns (main line, echo line) — both ripple independently.
vec2 waveYS(vec2 p, float t) {
    float xw = p.x - 0.25 * sin(t * 0.12);                 // crest drifts very slowly
    float profile = -0.55
        + 0.95 * gg((xw + 1.05) / 1.45)
        + 0.40 * gg((p.x - 1.50) / 1.55);
    float env = 0.35 + 0.65 * gg((xw + 1.05) / 1.90) + 0.30 * gg((p.x - 1.50) / 2.00);
    float pulse = 0.85 + 0.15 * sin(t * 0.20);
    float y1 = profile + env * pulse * (
                   0.22 * sin(xw * 1.6 - t * 0.50)
                 + 0.06 * sin(xw * 3.4 + t * 0.30));
    float y2 = profile * 0.92 - env * 0.12 * sin(xw * 2.4 + t * 0.40);
    return vec2(y1, y2);
}

void mainImage(out vec4 fragColor, in vec2 fragCoord) {
    float t = iTime;
    vec2 p = (fragCoord - 0.5 * iResolution) / iResolution.y;

    // --- flat maroon background (wallpaper is a solid rgb(45,0,30)) ---
    vec3 bg = vec3(0.290, 0.020, 0.195); // rgb(74,5,50) — wallpaper maroon, brightened
    vec2 q = (p - vec2(-0.7, 0.1)) / vec2(2.2, 1.6);
    bg += vec3(0.030, 0.004, 0.016) * gg(q.x) * gg(q.y); // faint light around wave

    // --- the sine wave, bright core + wide glow ---
    vec2 ys = waveYS(p, t);
    float d = p.y - ys.x;
    float core = lineAA(d, fwidth(d) * 2.0);
    float glow = gg(d / 0.10);

    // faint echo line below, like the ribbon's lower edge — counter-riffing
    float echo = lineAA(p.y - ys.y, fwidth(p.y - ys.y)) * 0.45;

    // faint vertical mesh lines hugging the wave (the ribbon texture), fast scroll
    float env = 0.18 + 0.65 * gg((p.x + 1.05) / 1.9) + 0.30 * gg((p.x - 1.5) / 2.0);
    float s = d / env;
    float mesh = lineAA(sin(s * 31.4159 - t * 0.6), fwidth(s) * 31.4)
                * (1.0 - min(1.0, abs(s) * 1.3)) * 0.30;

    vec3 col = bg
        + vec3(1.0, 0.41, 0.10) * (glow * 0.30 + mesh)      // soft orange halo
        + vec3(1.0, 0.49, 0.15) * (core * 1.1 + echo);      // hot line (wallpaper brightest)
    fragColor = vec4(col, 1.0);
}
