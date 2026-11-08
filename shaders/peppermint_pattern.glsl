// Festive Peppermint Pattern — animated peppermint tiles
// Rotating peppermint patterns with metallic shine

float hash12(vec2 p)
{
    vec3 p3 = fract(vec3(p.xyx) * 0.103);
    p3 += dot(p3, p3.yzx + 33.33);
    return fract((p3.x + p3.y) * p3.z);
}

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = (fragCoord - 0.5 * iResolution.xy) / iResolution.xy;
    uv.x *= iResolution.x / iResolution.y;
    
    // Festive background gradient
    vec3 col = mix(
        vec3(0.1, 0.02, 0.05),
        vec3(0.2, 0.05, 0.1),
        sin(iTime * 0.5) * 0.5 + 0.5
    );
    
    // Create grid of peppermint tiles
    vec2 grid = floor(uv * 4.0);
    vec2 gridUv = fract(uv * 4.0) - 0.5;
    
    float id = hash12(grid);
    
    // Rotation angle for each tile
    float tileRotation = id * 6.283 + iTime * (0.3 + 0.5 * sin(id * 6.283));
    
    // Rotate the uv
    float c = cos(tileRotation);
    float s = sin(tileRotation);
    vec2 rotated = vec2(
        gridUv.x * c - gridUv.y * s,
        gridUv.x * s + gridUv.y * c
    );
    
    // Peppermint stripe pattern
    float stripes = abs(sin((rotated.x + rotated.y) * 8.0));
    
    // Peppermint swirl effect
    float angle = atan(rotated.y, rotated.x);
    float dist = length(rotated);
    float swirl = sin(angle * 6.0 - dist * 8.0 + iTime * 2.0) * 0.5 + 0.5;
    
    // Combine patterns
    float pattern = mix(stripes, swirl, 0.5 + 0.5 * sin(iTime + id * 6.283));
    pattern = smoothstep(0.3, 0.7, pattern);
    
    // Red and white colors
    vec3 tileColor;
    if (mod(grid.x + grid.y, 2.0) < 1.0)
    {
        tileColor = mix(vec3(1.0, 0.1, 0.1), vec3(1.0, 1.0, 1.0), pattern);
    }
    else
    {
        tileColor = mix(vec3(1.0, 1.0, 1.0), vec3(1.0, 0.1, 0.1), pattern);
    }
    
    // Metallic shine effect
    float shine = pow(cos((rotated.x + iTime * 0.5) * 8.0) * 0.5 + 0.5, 2.0);
    tileColor += vec3(0.5, 0.5, 0.5) * shine * 0.4;
    
    // Soft glow between tiles
    float edge = min(abs(gridUv.x), abs(gridUv.y));
    edge = smoothstep(-0.5, -0.45, edge);
    tileColor = mix(vec3(0.05, 0.01, 0.02), tileColor, 1.0 - edge * 0.3);
    
    // Pulse effect
    float pulse = sin(iTime * 2.0 + id * 6.283) * 0.5 + 0.5;
    tileColor += vec3(0.1, 0.05, 0.1) * pulse * edge;
    
    col = mix(col, tileColor, 0.9);
    
    // Add some sparkles
    float sparkle = hash12(grid) > 0.95 ? 1.0 : 0.0;
    sparkle *= 0.5 + 0.5 * sin(iTime * 5.0);
    float sparklePos = distance(gridUv, vec2(0.0));
    sparkle *= exp(-sparklePos * 10.0);
    col += vec3(1.0, 1.0, 0.8) * sparkle;
    
    fragColor = vec4(clamp(col, 0.0, 1.0), 1.0);
}
