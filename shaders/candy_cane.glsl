// Candy Cane — rotating festive candy cane patterns
// Classic red and white stripes with swirl and motion

float hash12(vec2 p)
{
    vec3 p3 = fract(vec3(p.xyx) * 0.103);
    p3 += dot(p3, p3.yzx + 33.33);
    return fract((p3.x + p3.y) * p3.z);
}

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = (fragCoord - 0.5 * iResolution.xy) / iResolution.y;
    
    // Background - festive dark green
    vec3 col = vec3(0.02, 0.08, 0.04);
    
    // Distance from center
    float d = length(uv);
    
    // Swirl - creates the spiral effect
    float angle = atan(uv.y, uv.x) + iTime * 0.5 + length(uv) * 3.0;
    
    // Distance from a rotating point on a circle
    vec2 circlePos = vec2(cos(angle + d * 15.0), sin(angle + d * 15.0)) * 0.3;
    float circleDist = distance(uv, circlePos);
    
    // Main candy cane structure
    float stripes = mod(angle * 6.0 + iTime * 2.0, 6.283);
    float stripe = smoothstep(1.0, 0.5, abs(mod(angle, 0.628) - 0.314));
    
    // Thickness taper for candy cane hook
    float thickness = smoothstep(0.5, 0.0, d) * 0.3 + 0.1;
    float cane = smoothstep(thickness, 0.0, circleDist);
    
    // Red and white color pattern
    if (mod(angle * 8.0, 1.0) > 0.5)
    {
        col = mix(col, vec3(1.0, 0.2, 0.2), cane); // Red
    }
    else
    {
        col = mix(col, vec3(1.0, 1.0, 1.0), cane * 0.9); // White
    }
    
    // Add some peppermint shine
    float shine = sin(angle * 12.0 - iTime) * 0.5 + 0.5;
    col += shine * cane * vec3(0.3, 0.05, 0.05) * smoothstep(0.15, 0.0, d);
    
    // Holly decoration around the edge
    float holly = smoothstep(0.05, -0.05, abs(d - 0.4)) * sin(angle * 8.0) * 0.5 + 0.5;
    col = mix(col, vec3(0.1, 0.6, 0.2), holly * 0.5 * (1.0 - abs(sin(iTime + angle))));
    
    // Red berries
    float berryPos = mod(angle * 16.0, 6.283);
    float berry = smoothstep(0.08, 0.02, distance(uv, vec2(cos(berryPos), sin(berryPos)) * 0.4));
    col += vec3(0.9, 0.1, 0.1) * berry;
    
    fragColor = vec4(clamp(col, 0.0, 1.0), 1.0);
}
