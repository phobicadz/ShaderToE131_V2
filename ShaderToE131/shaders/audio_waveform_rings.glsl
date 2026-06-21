// Audio-reactive shader: expanding rings driven by frequency bands
// Uses: u_bass, u_lowmid, u_mid, u_highmid, u_treble, u_volume

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = (fragCoord - 0.5 * iResolution.xy) / min(iResolution.x, iResolution.y);
    float dist = length(uv);
    float angle = atan(uv.y, uv.x);

    // Bass creates expanding outward rings
    float bassRing = fract(dist - u_time * 0.3 + u_bass * 2.0);
    bassRing = smoothstep(0.0, 0.15, bassRing) * smoothstep(0.3, 0.15, bassRing);

    // Low-mid creates counter-rotating spiral arms
    float lowmidSpiral = sin(angle * 3.0 - dist * 8.0 + u_time * 2.0);
    lowmidSpiral *= u_lowmid;

    // Mid frequencies create a pulsing center glow
    float midPulse = exp(-dist * (4.0 + u_mid * 12.0));
    midPulse *= (0.5 + 0.5 * sin(u_time * (3.0 + u_mid * 7.0)));

    // High-mid creates fine radial lines
    float highmidLines = abs(sin(angle * 16.0 + dist * 20.0));
    highmidLines *= step(0.85, fract(dist * 5.0)) * u_highmid;

    // Treble adds outer glow / atmospheric haze
    float trebleGlow = exp(-dist * 2.0) * u_treble;

    // Color composition per band
    vec3 colBass = vec3(1.0, 0.15, 0.3);     // Red-pink for bass
    vec3 colLowMid = vec3(0.9, 0.6, 0.1);    // Gold for low-mid
    vec3 colMid = vec3(0.2, 0.8, 0.9);       // Cyan for mid
    vec3 colHighMid = vec3(0.7, 0.2, 1.0);   // Purple for high-mid
    vec3 colTreble = vec3(1.0, 1.0, 0.95);   // Warm white for treble

    float total = 0.;
    total += bassRing * 0.7;
    total += abs(lowmidSpiral) * 0.4;
    total += midPulse * 0.8;
    total += highmidLines * 0.3;
    total += trebleGlow * 0.25;

    // Weight colors by their contribution
    float bassW = bassRing / max(total, 0.01);
    float lowmidW = abs(lowmidSpiral) / max(total, 0.01);
    float midW = midPulse / max(total, 0.01);
    float highmidW = highmidLines / max(total, 0.01);
    float trebleW = trebleGlow / max(total, 0.01);

    vec3 color = colBass * bassW + colLowMid * lowmidW + colMid * midW + colHighMid * highmidW + colTreble * trebleW;
    color *= (0.2 + u_volume * 0.8);

    fragColor = vec4(color, 1.0);
}
