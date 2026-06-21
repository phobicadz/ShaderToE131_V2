// Audio-reactive shader: concentric spectrum rings
// Best for music playback — uses all bands to drive ring geometry
// Uses: u_bass, u_lowmid, u_mid, u_highmid, u_treble, u_volume

float GetCircle(vec2 uv, vec2 position, float radius)
{
    float dist = distance(position, uv);
    return smoothstep(dist - 1.0, dist, radius * radius);
}

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = (fragCoord - 0.5 * iResolution.xy) / min(iResolution.x, iResolution.y);
    float dist = length(uv);
    float angle = atan(uv.y, uv.x);

    // Each band drives a ring at different radius + thickness
    float bassR   = 0.25 + u_bass * 0.3;
    float lowmidR = 0.45 + u_lowmid * 0.2;
    float midR    = 0.65 + u_mid * 0.15;
    float highmidR= 0.80 + u_highmid * 0.1;
    float trebleR = 0.95 + u_treble * 0.05;

    // Ring thickness driven by band energy
    float bassW   = 0.04 + u_bass * 0.12;
    float lowmidW = 0.03 + u_lowmid * 0.08;
    float midW    = 0.03 + u_mid * 0.06;
    float highmidW= 0.02 + u_highmid * 0.05;
    float trebleW = 0.01 + u_treble * 0.04;

    // Bass ring — thick, pulsing red-orange
    float bassRing = smoothstep(bassR - bassW, bassR, dist) - smoothstep(bassR, bassR + bassW * 0.5, dist);
    vec3 colBass   = mix(vec3(1.0, 0.2, 0.1), vec3(1.0, 0.6, 0.1), u_bass);

    // Low-mid ring — gold with spiral twist
    float lowmidRing = smoothstep(lowmidR - lowmidW, lowmidR, dist) - smoothstep(lowmidR, lowmidR + lowmidW * 0.5, dist);
    vec3 colLowMid   = mix(vec3(1.0, 0.8, 0.2), vec3(1.0, 0.4, 0.6), sin(angle * 4.0) * u_lowmid);

    // Mid ring — cyan with radial lines
    float midRing   = smoothstep(midR - midW, midR, dist) - smoothstep(midR, midR + midW * 0.5, dist);
    float midLines  = abs(sin(angle * 12.0)) * u_mid;

    // High-mid ring — purple with noise texture
    float highmidRing   = smoothstep(highmidR - highmidW, highmidR, dist) - smoothstep(highmidR, highmidR + highmidW * 0.5, dist);
    float highmidNoise  = fract(sin(dot(uv * iResolution.xy, vec2(43.1, 87.3))) * 43758.5453) * u_highmid;

    // Treble ring — thin white sparkle at the edge
    float trebleRing   = smoothstep(trebleR - trebleW, trebleR, dist) - smoothstep(trebleR, trebleR + trebleW * 0.5, dist);
    vec3 colTreble     = mix(vec3(0.9, 0.9, 1.0), vec3(1.0, 1.0, 0.8), u_treble);

    // Compose: weight each ring by its energy contribution
    float total = bassRing + lowmidRing + midRing + highmidRing + trebleRing;
    if (total < 0.001) { fragColor = vec4(0.03, 0.02, 0.05, 1.0); return; }

    float bw   = bassRing / total;
    float lmW  = lowmidRing / total;
    float mW   = midRing / total;
    float hmW  = highmidRing / total;
    float tW   = trebleRing / total;

    vec3 color = colBass * bw + colLowMid * lmW;
    color += mix(vec3(0.2, 0.8, 0.9), vec3(0.5, 1.0, 0.6), midLines) * mW;
    color += (vec3(0.7, 0.2, 1.0) + vec3(highmidNoise)) * hmW;
    color += colTreble * tW;

    // Overall brightness driven by volume
    color *= (0.15 + u_volume * 0.85);

    // Background glow from bass
    float bgGlow = exp(-dist * 3.0) * u_bass * 0.3;
    color += vec3(0.4, 0.1, 0.2) * bgGlow;

    fragColor = vec4(color, 1.0);
}
