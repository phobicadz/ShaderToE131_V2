// Christmas Tree — animated decorated tree with ornaments and lights
// Tree with twinkling lights and spinning ornaments

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
    vec2 uv = (fragCoord - 0.5 * iResolution.xy) / iResolution.xy;
    uv.y *= iResolution.y / iResolution.x;
    
    // Night sky gradient
    vec3 col = mix(
        vec3(0.01, 0.02, 0.05),
        vec3(0.05, 0.03, 0.08),
        smoothstep(0.5, -0.5, uv.y)
    );
    
    // Tree trunk
    float trunkDist = abs(uv.x) - 0.08;
    float trunk = smoothstep(0.1, 0.0, trunkDist) * smoothstep(-0.2, -0.4, uv.y);
    col = mix(col, vec3(0.3, 0.15, 0.05), trunk * 0.9);
    
    // Tree shape - three triangles getting progressively larger
    for (int layer = 0; layer < 3; layer++)
    {
        float fl = float(layer);
        float yOffset = -0.1 - fl * 0.25;
        float width = 0.15 + fl * 0.25;
        float height = 0.2;
        
        // Distance to tree layer
        float distToLayer = abs(uv.x) - (width * (1.0 - (uv.y - yOffset) / height));
        float treeDist = max(distToLayer, uv.y - yOffset + 0.01);
        treeDist = max(treeDist, -1.0 * (uv.y - yOffset - height));
        
        float tree = smoothstep(0.05, -0.02, treeDist);
        
        // Base tree color - dark green
        vec3 treeColor = vec3(0.05, 0.25, 0.08);
        
        // Add ornaments and lights
        vec2 ornGrid = uv * vec2(8.0, 6.0) + fl;
        vec2 ornGridPos = floor(ornGrid);
        
        for (int i = 0; i < 5; i++)
        {
            float fi = float(i);
            vec2 offset = vec2(
                0.5 + 0.4 * sin(hash12(ornGridPos + vec2(fi, 0.0)) * 6.283),
                0.5 + 0.3 * cos(hash12(ornGridPos + vec2(0.0, fi)) * 6.283)
            );
            
            vec2 ornPos = ornGridPos + offset;
            float distToOrn = distance(fract(ornGrid), offset);
            
            // Different types of decorations
            float ornType = hash12(ornGridPos + vec2(fi * 2.0, 0.0));
            
            if (ornType < 0.6)
            {
                // Ornament balls - rotate and shine
                float ornament = exp(-distToOrn * 40.0);
                vec3 ornColor = vec3(
                    0.5 + 0.5 * sin(hash12(ornGridPos) * 6.283 + iTime),
                    0.3 + 0.3 * cos(hash12(ornGridPos + vec2(1.0, 0.0)) * 6.283 + iTime),
                    0.7 + 0.3 * sin(hash12(ornGridPos + vec2(0.0, 1.0)) * 6.283 + iTime)
                );
                treeColor += ornColor * ornament * tree;
            }
            else
            {
                // Twinkling lights
                float light = exp(-distToOrn * 30.0);
                float twinkle = 0.3 + 0.7 * (0.5 + 0.5 * sin(iTime * 3.0 + hash12(ornGridPos) * 6.283));
                vec3 lightColor = vec3(1.0, 1.0, 0.6);
                treeColor += lightColor * light * twinkle * tree * 1.5;
            }
        }
        
        col = mix(col, treeColor, tree * 0.95);
    }
    
    // Star on top
    float starY = 0.4;
    float starX = abs(uv.x);
    float starDist = max(starX - 0.03, abs(uv.y - starY) - 0.03);
    float starAngle = atan(uv.y - starY, uv.x);
    starDist = min(starDist, length(uv - vec2(0.0, starY)) - 0.05 * sin(starAngle * 5.0 + iTime * 2.0));
    
    float star = smoothstep(0.04, -0.01, starDist);
    col = mix(col, vec3(1.0, 1.0, 0.3), star);
    col += vec3(1.0, 1.0, 0.5) * star * 0.5 * (0.5 + 0.5 * sin(iTime * 2.0));
    
    // Snow on branches
    float snow = 0.1 * sin(uv.x * 20.0) * smoothstep(-0.1, -0.35, uv.y);
    col += vec3(1.0, 1.0, 1.0) * snow * 0.4;
    
    fragColor = vec4(clamp(col, 0.0, 1.0), 1.0);
}
