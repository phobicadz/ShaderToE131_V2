// Christmas Lights — classic twinkling holiday bulbs
// Colorful lights strung in a pattern, blinking and twinkling

float hash12(vec2 p)
{
    vec3 p3 = fract(vec3(p.xyx) * 0.103);
    p3 += dot(p3, p3.yzx + 33.33);
    return fract((p3.x + p3.y) * p3.z);
}

float hash13(vec3 p)
{
    p = fract(p * 0.1031);
    p += dot(p, p.yzx + 33.33);
    return fract((p.x + p.y) * p.z);
}

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord / iResolution.xy;
    
    // Night sky background - dark navy blue
    vec3 col = vec3(0.01, 0.02, 0.05);
    
    // Create grid of light positions
    vec2 grid = floor(uv * vec2(12.0, 4.0));
    vec2 gridUv = fract(uv * vec2(12.0, 4.0));
    
    // Generate light properties based on grid position
    float id = hash12(grid);
    
    // Create multiple strings at different heights
    for (int i = 0; i < 4; i++)
    {
        float fi = float(i) * 0.25;
        vec2 lightPos = vec2(gridUv.x, fi + 0.05 * sin(uv.x * 20.0 + iTime * 0.3));
        
        // Distance to nearest light
        float d = distance(gridUv, lightPos);
        
        // Blink pattern - different frequency for each light
        float freq = 2.0 + hash12(grid + vec2(fi, 0.0)) * 4.0;
        float blink = 0.5 + 0.5 * sin(iTime * freq + hash12(grid) * 6.283);
        
        // Color cycle through classic Christmas colors
        vec3 lightColor;
        float hue = mod(hash12(grid + vec2(fi * 2.0, 0.0)), 1.0);
        if (hue < 0.2)
            lightColor = vec3(1.0, 0.1, 0.1); // Red
        else if (hue < 0.4)
            lightColor = vec3(0.1, 1.0, 0.1); // Green
        else if (hue < 0.6)
            lightColor = vec3(1.0, 1.0, 0.1); // Yellow
        else if (hue < 0.8)
            lightColor = vec3(0.1, 0.5, 1.0); // Blue
        else
            lightColor = vec3(1.0, 0.5, 0.1); // Warm white
        
        // Glow effect
        float glow = exp(-d * 30.0) * blink;
        glow += 0.3 * exp(-d * 8.0) * blink * 0.5;
        
        col += lightColor * glow;
    }
    
    // Wire between lights (dim)
    float wire = 0.15 * sin(gridUv.x * 50.0) * smoothstep(0.08, 0.02, abs(gridUv.y - 0.1));
    col += vec3(0.3, 0.2, 0.1) * wire;
    
    fragColor = vec4(clamp(col, 0.0, 1.0), 1.0);
}
