// Falling Snow — gentle Christmas snowfall
// Realistic layered snow falling at different depths with parallax effect

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
    
    // Deep blue night sky
    vec3 col = mix(
        vec3(0.02, 0.04, 0.10),  // Dark blue
        vec3(0.05, 0.05, 0.15),  // Slightly lighter near horizon
        smoothstep(0.0, 1.0, uv.y)
    );
    
    // Multiple layers of snow at different depths (parallax)
    for (int layer = 0; layer < 4; layer++)
    {
        float depth = float(layer) / 4.0;
        float speed = 0.3 + depth * 0.7;  // Further snow falls slower
        float scale = 0.02 + depth * 0.15; // Further snow is smaller
        float opacity = 0.3 + depth * 0.4;
        
        // Time offset for each layer
        float timeOffset = iTime * speed - float(layer) * 0.5;
        
        // Create snowflake grid
        vec2 snowUv = uv / scale;
        snowUv.y -= timeOffset;
        
        vec2 gridPos = floor(snowUv);
        vec2 cellUv = fract(snowUv);
        
        // Snowflake center offset based on grid position
        vec2 flakeCenter = vec2(0.5) + 0.3 * vec2(
            sin(hash12(gridPos) * 6.283) * (0.5 + 0.5 * sin(iTime * 0.3 + hash12(gridPos))),
            sin(hash12(gridPos + vec2(10.0, 0.0)) * 6.283) * 0.2
        );
        
        float d = distance(cellUv, flakeCenter);
        
        // Snowflake shape - simple cross
        float flake = exp(-d * 8.0) * 0.8 + 
                     abs(sin((atan(cellUv.y - flakeCenter.y, cellUv.x - flakeCenter.x) + iTime * 2.0) * 6.0)) * 
                     exp(-d * 5.0) * 0.3;
        
        // Twinkle effect
        float twinkle = 0.6 + 0.4 * sin(iTime * 3.0 + hash12(gridPos) * 6.283);
        
        col += vec3(0.9, 0.95, 1.0) * flake * twinkle * opacity;
    }
    
    // Slight drift effect
    col = mix(col, vec3(0.7, 0.8, 1.0), 0.1 * sin(uv.x * 2.0 + iTime * 0.5) * 0.5 + 0.5);
    
    fragColor = vec4(clamp(col, 0.0, 1.0), 1.0);
}
