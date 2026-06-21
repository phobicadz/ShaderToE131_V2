void mainImage( out vec4 fragColor, in vec2 fragCoord )
{
    // centered, aspect-correct coords (0,0 = middle)
    vec2 uv = (fragCoord - 0.5 * iResolution.xy) / iResolution.y;
    float t = iTime;

    // --- water surface: sum of sines (scroll with + t) ---
    float wave = 0.0;
    wave += sin(uv.x * 3.0  + t * 1.0) * 0.10;   // big slow swell
    wave += sin(uv.x * 7.0  + t * 1.6) * 0.05;   // medium chop
    wave += sin(uv.x * 15.0 + t * 2.4) * 0.025;  // small fast ripples
    wave -= 0.05;                                 // push surface down a touch

    // split screen: 1 = sky (above surface), 0 = water (below)
    float aboveSurface = step(wave, uv.y);

    // --- colors ---
    vec3 sky       = vec3(0.75, 0.90, 1.0);   // pale sky blue
    vec3 waterTop  = vec3(0.30, 0.70, 0.95);  // bright light blue at surface
    vec3 waterDeep = vec3(0.05, 0.25, 0.55);  // deeper blue down low

    // water darkens with depth below the surface
    float depth = clamp((wave - uv.y) * 2.0, 0.0, 1.0);
    vec3 water = mix(waterTop, waterDeep, depth);

    vec3 col = mix(water, sky, aboveSurface);

    // --- CLOUDS: lumpy band, broken into separate puffs, drifting OPPOSITE (- t) ---
    float cloudShape = 0.0;
    cloudShape += sin(uv.x * 2.0 - t * 0.4) * 0.04;
    cloudShape += sin(uv.x * 5.0 - t * 0.6) * 0.02;
    float cloudLine = 0.25 + cloudShape;          // height of clouds in the sky

    // soft fluffy band around the cloud line
    float clouds = 1.0 - smoothstep(0.0, 0.18, abs(uv.y - cloudLine));

    // density mask: slow sine across x, only positive humps survive -> gaps between puffs
    float density = sin(uv.x * 1.5 - t * 0.4);
    density = smoothstep(0.1, 0.6, density);
    clouds *= density;

    clouds *= aboveSurface;                        // sky only, never over water
    col = mix(col, vec3(1.0), clouds * 0.8);       // blend white clouds in

    // --- thin soft-blue crest line where water meets air ---
    float surfaceGlow = 1.0 - smoothstep(0.0, 0.004, abs(uv.y - wave));
    col += surfaceGlow * vec3(0.6, 0.85, 1.0) * 0.3;

    fragColor = vec4(col, 1.0);
}