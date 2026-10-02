// Interference — two ripple sources at the ends of the strip, overlapping wavefronts
// Audio-reactive: mid/treble set the wavelength, lowmid drives the animation speed,
// bass pushes the sources apart and tints the antinodes. Looks like a tank of
// water being shaken from both ends — great on a 53-pixel-wide panel.
// Uses: u_bass, u_lowmid, u_mid, u_highmid, u_treble, u_volume

uniform float u_bass;
uniform float u_lowmid;
uniform float u_mid;
uniform float u_highmid;
uniform float u_treble;
uniform float u_volume;

vec3 pal(float t, vec3 a, vec3 b, vec3 c, vec3 d)
{
    return a + b * cos(6.28318 * (c * t + d));
}

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    // x: -2.41..2.41, y: -0.5..0.5
    vec2 uv = (fragCoord - 0.5 * iResolution.xy) / iResolution.y;
    float t = iTime;

    float level = clamp(u_volume * 1.8, 0.0, 1.0);

    // Sources sit at the ends of the strip; bass spreads them wider
    float sep = 1.6 + u_bass * 0.9;
    vec2 s1 = vec2(-sep, -0.15 + 0.3 * sin(t * 0.4));
    vec2 s2 = vec2( sep,  0.15 + 0.3 * sin(t * 0.4 + 2.1));

    // Wavelength: brighter highs -> tighter fringes (kept coarse enough that
    // fringes stay readable on a 53-pixel-wide grid)
    float k = 4.0 + u_mid * 12.0 + u_highmid * 9.0 + u_treble * 6.0;
    float omega = t * (1.2 + u_lowmid * 3.5);

    float r1 = length(uv - s1);
    float r2 = length(uv - s2);

    // Two wavefields plus a slow bass pulse from the centre
    float w1 = sin(k * r1 - omega);
    float w2 = sin(k * r2 - omega + u_bass * 3.0);
    float w3 = sin(length(uv) * (3.0 + u_bass * 8.0) - t * 1.5) * u_bass;

    float v = (w1 + w2 + w3 * 0.8) / 2.8;
    float m = 0.5 + 0.5 * v;

    // Antinode intensity, damped away from the sources
    float atten = (0.55 + 0.45 * exp(-r1 * 0.55)) * (0.55 + 0.45 * exp(-r2 * 0.55));
    float intensity = clamp(pow(m, 1.4) * atten * (0.55 + 1.0 * level) * 2.6, 0.0, 1.0);

    // Iridescent palette; hue rides the fringe value so bands shift hue
    vec3 col = pal(m * 0.9 + t * 0.02 + u_bass * 0.15,
                   vec3(0.35, 0.40, 0.50),
                   vec3(0.45, 0.45, 0.40),
                   vec3(1.0, 1.0, 1.0),
                   vec3(0.55, 0.35, 0.20)) * intensity;

    // Treble sparkle on the crests only
    float sparkle = fract(sin(dot(floor(fragCoord) + vec2(floor(t * 24.0), 0.0), vec2(12.9898, 78.233))) * 43758.5453);
    col += vec3(0.8, 0.9, 1.0) * step(0.975, sparkle) * step(0.75, m) * u_treble * 0.6;

    // Dark water background with a hint of depth
    col += vec3(0.012, 0.020, 0.035);

    fragColor = vec4(clamp(col, 0.0, 1.0), 1.0);
}
