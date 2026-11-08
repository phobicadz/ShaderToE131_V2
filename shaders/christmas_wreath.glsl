// Christmas Wreath — festive holly wreath with berries and bow
// Circular wreath with animated lights and rotating decoration

float hash12(vec2 p)
{
    vec3 p3 = fract(vec3(p.xyx) * 0.103);
    p3 += dot(p3, p3.yzx + 33.33);
    return fract((p3.x + p3.y) * p3.z);
}

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = (fragCoord - 0.5 * iResolution.xy) / iResolution.y;
    
    // Deep festive background
    vec3 col = mix(
        vec3(0.01, 0.02, 0.05),
        vec3(0.04, 0.03, 0.08),
        sin(uv.y * 3.0 + iTime * 0.2) * 0.5 + 0.5
    );
    
    // Distance from center
    float r = length(uv);
    float angle = atan(uv.y, uv.x);
    
    // Wreath ring
    float wreathInner = 0.3;
    float wreathOuter = 0.5;
    float wreathDist = abs(r - 0.4);
    float wreathWidth = smoothstep(0.15, 0.0, wreathDist);
    
    // Holly leaves in the wreath
    float leaves = 0.0;
    for (int i = 0; i < 12; i++)
    {
        float fi = float(i) / 12.0;
        float leafAngle = fi * 6.283;
        
        // Rotate leaves around the wreath
        float rotated = angle - leafAngle - iTime * 0.3;
        float leaf = abs(sin(rotated * 3.0)) * exp(-abs(r - 0.4) * 15.0);
        leaves += leaf * 0.3;
    }
    
    vec3 wreathColor = mix(vec3(0.05, 0.3, 0.1), vec3(0.08, 0.4, 0.12), leaves);
    
    // Red berries scattered on wreath
    float berries = 0.0;
    for (int i = 0; i < 20; i++)
    {
        float fi = float(i) / 20.0;
        vec2 berryAngle = vec2(cos(fi * 6.283 + iTime * 0.2), sin(fi * 6.283 + iTime * 0.2));
        vec2 berryPos = berryAngle * 0.4;
        
        // Add some radial variation
        berryPos += 0.05 * vec2(sin(fi * 12.0), cos(fi * 8.0));
        
        float dist = distance(uv, berryPos);
        float berry = exp(-dist * 80.0);
        
        // Twinkling/pulsing berries
        float pulse = 0.6 + 0.4 * sin(iTime * 2.5 + fi * 6.283);
        berries += berry * pulse * 0.5;
    }
    
    wreathColor += vec3(0.9, 0.1, 0.1) * berries;
    
    // Gold/yellow lights on wreath
    for (int i = 0; i < 8; i++)
    {
        float fi = float(i) / 8.0;
        vec2 lightPos = vec2(cos(fi * 6.283 + iTime * 0.8), sin(fi * 6.283 + iTime * 0.8)) * 0.42;
        
        float dist = distance(uv, lightPos);
        float light = exp(-dist * 40.0);
        
        // Bright twinkle
        float twinkle = 0.4 + 0.6 * sin(iTime * 3.0 + fi * 6.283);
        wreathColor += vec3(1.0, 1.0, 0.4) * light * twinkle * 1.5;
    }
    
    col = mix(col, wreathColor, wreathWidth);
    
    // Red bow at bottom
    float bowY = -0.35;
    
    // Left bow loop
    float leftBowX = -0.15;
    float leftBowDist = distance(uv, vec2(leftBowX, bowY)) - 0.12;
    float leftBow = smoothstep(0.08, 0.01, abs(leftBowDist));
    
    // Right bow loop
    float rightBowX = 0.15;
    float rightBowDist = distance(uv, vec2(rightBowX, bowY)) - 0.12;
    float rightBow = smoothstep(0.08, 0.01, abs(rightBowDist));
    
    // Bow center knot
    float knotDist = distance(uv, vec2(0.0, bowY)) - 0.04;
    float knot = smoothstep(0.06, 0.0, knotDist);
    
    vec3 bowColor = vec3(0.8, 0.1, 0.1);
    bowColor += vec3(1.0, 0.3, 0.2) * (sin(iTime * 2.0 + length(uv)) * 0.5 + 0.5) * 0.3;
    
    col = mix(col, bowColor, max(max(leftBow, rightBow), knot) * 0.95);
    
    // Holly leaves on bow
    for (int i = 0; i < 4; i++)
    {
        float fi = float(i);
        vec2 leafPos = vec2(leftBowX + fi * 0.1, bowY - 0.08);
        float leafDist = length(uv - leafPos) - 0.03;
        float leaf = exp(-abs(leafDist) * 20.0);
        col += vec3(0.1, 0.6, 0.2) * leaf * 0.6;
    }
    
    fragColor = vec4(clamp(col, 0.0, 1.0), 1.0);
}
