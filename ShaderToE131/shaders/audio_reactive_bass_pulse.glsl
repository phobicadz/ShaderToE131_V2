// Audio-reactive shader: bass-driven pulse explosion
// Uses: u_bass, u_mid, u_treble, u_volume, u_time

float GetCircle(vec2 uv, vec2 position, float radius)
{
    float dist = distance(position, uv);
    return smoothstep(dist - 1.0, dist, radius * radius);
}

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = (fragCoord - 0.5 * iResolution.xy) / iResolution.y;

    // Bass drives the number and size of circles
    float numCircles = floor(3.0 + u_bass * 12.0);
    float baseRadius = 0.08 + u_bass * 0.15;

    float pixel = 0.;
    for (int i = 0; i < 16; i++)
    {
        if (float(i) > numCircles) break;

        float angle = float(i) * 2.399 + u_time * 0.5;
        vec2 pos = vec2(cos(angle), sin(angle)) * (0.4 + u_volume * 0.6);

        // Mid frequencies shift the color hue
        float hue = fract(u_mid * 3.0 + iTime / 10.0);
        vec3 col = HSVtoRGB(vec3(hue, 0.8, 0.9));

        pixel += GetCircle(uv, pos, baseRadius) * (0.5 + u_treble * 0.5);
    }

    // Treble adds sparkle / noise overlay
    float sparkle = fract(sin(dot(uv * iResolution.xy, vec2(12.9898, 78.233))) * 43758.5453);
    pixel += step(0.95 - u_treble, sparkle) * u_treble;

    // Volume controls overall brightness
    fragColor = vec4(pixel * col * (0.3 + u_volume * 0.7), 1.0);
}
