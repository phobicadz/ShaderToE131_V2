// Heartbeat — an ECG trace scrolling across the strip, driven by bass
// Audio-reactive: bass sets the beat, volume sets the amplitude, treble adds
// a shimmer on the monitor line. Reads like a hospital monitor on a wide panel.
// Uses: u_bass, u_lowmid, u_mid, u_highmid, u_treble, u_volume

uniform float u_bass;
uniform float u_lowmid;
uniform float u_mid;
uniform float u_highmid;
uniform float u_treble;
uniform float u_volume;

float gauss(float x, float c, float w)
{
    float v = (x - c) / w;
    return exp(-v * v);
}

// One cardiac cycle over u in [0,1): P bump, Q dip, R spike, S dip, T bump
float ecg(float u)
{
    u = fract(u);
    return 0.16 * gauss(u, 0.14, 0.055)
         - 0.14 * gauss(u, 0.40, 0.020)
         + 1.00 * gauss(u, 0.46, 0.016)
         - 0.38 * gauss(u, 0.53, 0.030)
         + 0.30 * gauss(u, 0.72, 0.075);
}

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    // x: -2.41..2.41, y: -0.5..0.5
    vec2 uv = (fragCoord - 0.5 * iResolution.xy) / iResolution.y;
    float t = iTime;

    // Quiet input -> nearly flat line; loud input -> tall, fast beats
    float level = clamp(u_volume * 1.6, 0.0, 1.0);
    float amp = 0.06 + 0.36 * clamp(u_bass * 1.5 + level * 0.5, 0.0, 1.0);
    float rate = 0.35 + u_bass * 1.2 + level * 0.25;   // beats per second
    float scroll = t * rate;

    // Two beats visible across the strip at a time
    float wavelength = 2.6;
    float phase = (uv.x + scroll) / wavelength;

    float trace = ecg(phase);
    float prev = ecg(phase - 0.030 / wavelength);
    float lineY = -0.05 + amp * trace;

    // Distance to the trace, using the segment to the previous sample so the
    // R spike stays connected instead of turning into dots
    float d = abs(uv.y - lineY);
    float slope = (lineY - (-0.05 + amp * prev)) / 0.030;
    d *= 1.0 / sqrt(1.0 + slope * slope);   // true distance to the slanted segment

    float core = exp(-d * d * 2500.0);
    float halo = exp(-d * d * 300.0);

    // Colour shifts from monitor green toward hot cyan on bass hits
    vec3 traceCol = mix(vec3(0.15, 1.0, 0.45), vec3(0.25, 0.95, 1.0), clamp(u_bass * 2.0, 0.0, 1.0));

    vec3 col = vec3(0.010, 0.016, 0.020);

    // Baseline grid: faint dashed rule the trace rides on
    float dash = step(0.5, fract(uv.x * 4.0 + scroll * 4.0));
    col += vec3(0.06, 0.10, 0.09) * exp(-abs(uv.y + 0.05) * 60.0) * dash * 0.8;

    col += traceCol * (core * 1.2 + halo * 0.30);

    // Beat flash: the whole panel breathes with each bass hit
    float beat = fract(phase);
    float thump = gauss(beat, 0.46, 0.09) * clamp(u_bass * 2.0, 0.0, 1.0);
    col += vec3(0.10, 0.22, 0.18) * thump * 0.55;

    // Treble adds a faint shimmer along the top edge (monitor noise)
    float hiss = fract(sin(dot(floor(fragCoord) + vec2(floor(t * 30.0), 0.0), vec2(12.9898, 78.233))) * 43758.5453);
    col += vec3(0.35, 0.65, 0.75) * step(0.97, hiss) * u_treble * 0.5;

    // R-spike bloom in the vertical direction — bright white core
    col += vec3(0.9, 1.0, 0.95) * core * clamp(trace, 0.0, 1.0) * 0.6;

    fragColor = vec4(clamp(col, 0.0, 1.0), 1.0);
}
