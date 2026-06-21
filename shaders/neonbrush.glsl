// Neon Brush
// By Noztol

const float pi = 3.14159;
const float pi2 = pi * 2.;

mat2 rot2D(float r) {
    float c = cos(r), s = sin(r);
    return mat2(c, s, -s, c);
}

float nsin(float a){return .5+.5*sin(a);}
float ncos(float a){return .5+.5*cos(a);}
vec3 saturate(vec3 a){return clamp(a,0.,1.);}

float rand(vec2 co){
    return fract(sin(dot(co.xy ,vec2(12.9898,78.233))) * 43758.5453);
}

// hash & simplex noise from https://www.shadertoy.com/view/Msf3WH
vec2 hash( vec2 p ) {
	p = vec2( dot(p,vec2(127.1,311.7)), dot(p,vec2(269.5,183.3)) );
	return -1.0 + 2.0*fract(sin(p)*43758.5453123);
}

float noise( in vec2 p ) {
    const float K1 = 0.366025404; 
    const float K2 = 0.211324865; 

	vec2 i = floor( p + (p.x+p.y)*K1 );
    vec2 a = p - i + (i.x+i.y)*K2;
    vec2 o = (a.x>a.y) ? vec2(1.0,0.0) : vec2(0.0,1.0); 
    vec2 b = a - o + K2;
	vec2 c = a - 1.0 + 2.0*K2;

    vec3 h = max( 0.5-vec3(dot(a,a), dot(b,b), dot(c,c) ), 0.0 );
	vec3 n = h*h*h*h*vec3( dot(a,hash(i+0.0)), dot(b,hash(i+o)), dot(c,hash(i+1.0)));

    return dot( n, vec3(70.0) );	
}

float noise01(vec2 p) {
    return clamp((noise(p)+.5)*.5, 0.,1.);
}


vec3 getPalette(float t) {
    vec3 a = vec3(0.7, 0.7, 0.7); 
    vec3 b = vec3(0.5, 0.5, 0.5);
    vec3 c = vec3(1.0, 1.0, 1.0);
    vec3 d = vec3(0.0, 0.33, 0.67);
    return a + b * cos(pi2 * (c * t + d));
}


vec3 colorBrushStrokeHelix(vec2 uv, vec3 inpColor, float phase, float brushX)
{
    float freq = 2.0;
    float amp = 0.6;
    float waveY = sin(uv.x * freq + phase) * amp;
    
    float dist = abs(uv.y - waveY);
    
    // Do not render anything ahead of the 'brush'
    if (uv.x > brushX) return inpColor;
    
    // Taper the stroke sharply right at the brush tip
    float paintReveal = smoothstep(brushX - 0.25, brushX, uv.x);
    float lineWidth = 0.4 * (1.0 - paintReveal); // Tapers to 0 at the leading edge
    
    if(dist > lineWidth + 0.1) return inpColor;

    // Map UVs for texture
    vec2 uvLine = vec2(dist, uv.x * 1.5);
    
    // add Wobble
    uvLine.x += (noise01(uvLine * 1.) - 0.5) * 0.02;
    uvLine.x += cos(uvLine.y * 3.) * 0.009;
    uvLine.x += (noise01(uvLine * 5.) - 0.5) * 0.005;

    // Brush stroke fibers
    float strokeTexture = 0.0
        + noise01(uvLine * vec2(min(iResolution.y, iResolution.x) * 0.2, 1.)) 
        + noise01(uvLine * vec2(79., 1.)) 
        + noise01(uvLine * vec2(14., 1.)); 
        
    strokeTexture *= 0.333; 
    strokeTexture = max(0.008, strokeTexture);
    
    float edgeFade = smoothstep(lineWidth, lineWidth * 0.05, dist);
    

    float strokeAlpha = pow(strokeTexture, 1.1) * edgeFade * 6.0; 
    
    vec3 brushColor = getPalette(uv.x * 0.25 - iTime * 0.1 + phase * 0.1);

    // Glowing tip right where the brush is painting
    float tipGlow = smoothstep(brushX - 0.15, brushX, uv.x) * edgeFade * 2.5;

    // Additive Blending
    return inpColor + (brushColor * strokeAlpha) + (vec3(1.0) * tipGlow);
}

vec2 getuv_centerX(vec2 fragCoord, vec2 newTL, vec2 newSize)
{
    vec2 ret = vec2(fragCoord.x / iResolution.x, (iResolution.y - fragCoord.y) / iResolution.y);
    ret *= newSize;
    float aspect = iResolution.x / iResolution.y;
    ret.x *= aspect;
    float newWidth = newSize.x * aspect;
    return ret + vec2(newTL.x - (newWidth - newSize.x) / 2.0, newTL.y);
}

void mainImage( out vec4 fragColor, in vec2 fragCoord )
{
    vec2 uv = getuv_centerX(fragCoord, vec2(-1,-1), vec2(2,2));
    
    // Control how fast the brush moves
    float speed = 1.0; 
    float brushX = iTime * speed;
    
    uv.x += brushX - 1.2; 
    
    vec3 col = vec3(0.0);
    
    col = colorBrushStrokeHelix(uv, col, 0.0, brushX);
    col = colorBrushStrokeHelix(uv, col, pi, brushX);

    col.rgb += (rand(uv + iTime) - 0.5) * 0.04;
    col.rgb = saturate(col.rgb);

    vec2 uvScreen = (fragCoord / iResolution.xy * 2.) - 1.;
    float vignetteAmt = 1.0 - dot(uvScreen * 0.4, uvScreen * 0.4);
    col *= vignetteAmt;
    
    fragColor = vec4(col, 1.);
}