// Sunflower Fields
// By Bitless, Ircss, & Noztol

#define p(t, a, b, c, d) ( a + b*cos( 6.28318*(c*t+d) ) ) 
#define sp(t) p(t,vec3(.26,.76,.77),vec3(1,.3,1),vec3(.8,.4,.7),vec3(0,.12,.54)) 
#define hue(v) ( .6 + .76 * cos(6.3*(v) + vec4(0,23,21,0) ) ) 

#define smoothing      0.006
#define TWO_PI         6.28318530718
#define lineSize       0.01

#define MountainLayerThreecol vec3(26., 65., 74.)/255.
#define MountainLayerFourCol vec3(14., 49., 55.)/255.
#define SunflowerInsideOne    vec3(203., 77., 23.)/255.
#define SunflowerInsideTwo    vec3(134., 71., 48.)/255.
#define SunflowerInsideThree  vec3(158., 159., 33.)/255.
#define SunflowerLeavesOne    vec3(247., 214., 0.)/255.
#define SunflowerHighlight    vec3(247., 218., 63.)/255.
#define SunflowerLeavesTwo    vec3(236., 168., 3.)/255.
#define SunflowerStem         vec3(97., 128., 52.)/255.
#define SunflowerStemBright   vec3(176., 186., 53.)/255.
#define FieldDark             vec3(44., 62., 40.)/255.
#define FieldMid              vec3(94., 121., 62.)/255.

float hash12(vec2 p) {
    vec3 p3  = fract(vec3(p.xyx) * .1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return fract((p3.x + p3.y) * p3.z);
}

vec2 hash22(vec2 p) {
    vec3 p3 = fract(vec3(p.xyx) * vec3(.1031, .1030, .0973));
    p3 += dot(p3, p3.yzx+33.33);
    return fract((p3.xx+p3.yz)*p3.zy);
}

vec2 rotate2D (vec2 st, float a){
    return  mat2(cos(a),-sin(a),sin(a),cos(a))*st;
}

float st(float a, float b, float s) { return smoothstep (a-s, a+s, b); }

float noise( in vec2 p ) {
    vec2 i = floor( p );
    vec2 f = fract( p );
    vec2 u = f*f*(3.-2.*f);
    return mix( mix( dot( hash22( i+vec2(0,0) ), f-vec2(0,0) ), 
                     dot( hash22( i+vec2(1,0) ), f-vec2(1,0) ), u.x),
                mix( dot( hash22( i+vec2(0,1) ), f-vec2(0,1) ), 
                     dot( hash22( i+vec2(1,1) ), f-vec2(1,1) ), u.x), u.y);
}

float s_noise(vec2 p) { return noise(p)*0.5 + 0.5; }

float fbm(vec2 p) {
    float v = 0.0; float a = 0.5; vec2 shift = vec2(100.0);
    mat2 rot = mat2(cos(0.5), sin(0.5), -sin(0.5), cos(0.50));
    for (int i = 0; i < 5; ++i) { v += a * s_noise(p); p = rot * p * 2.0 + shift; a *= 0.5; }
    return v;
}


float randOneD(float x) { return fract(sin(x *52.163)*268.2156); }


void DrawHalfVectorWithLength(vec2 origin, vec2 vector, float len, vec2 uv, float size, vec3 lineColor, inout vec3 sceneColor){
    uv  -= origin;
    float v2   = dot(vector, vector);
    float vUv  = dot(vector, uv);
    vec2  p    = vector * vUv/v2;
    float d    = distance(p, uv);
    float m    = 1. - step(0.,vUv/v2);
          m   += step(len, vUv/v2);
    sceneColor = mix(lineColor, sceneColor, clamp(smoothstep(size, size + 0.01, d)+ m, 0. ,1.)); 
}

void DrawStemLeave(vec2 origin, vec2 vector, float len, vec2 uv, float size, vec3 lineColor, inout vec3 sceneColor){
    uv  -= origin;
    float v2   = dot(vector, vector);
    float vUv  = dot(vector, uv);
    uv.y += pow(vUv/len, 2.)*4.;
    vec2  p    = vector * vUv/v2;
    float d    = distance(p, uv);
    float m    = 1. - step(0.,vUv/v2);
          m   += step(len, vUv/v2);
          size *= smoothstep(0.5, 0.0, abs(vUv - 0.5)/len) *0.5;
    sceneColor = mix(lineColor, sceneColor, clamp(smoothstep(size, size + 0.01, d)+ m, 0. ,1.)); 
}

void DrawPetals(vec2 uv, inout vec3 col, float seed, float offset, vec3 petalColor) {
     float leavesSpread = 0.35;
     vec2  petalSpace = vec2(fract((offset+ uv.x)* TWO_PI *leavesSpread), uv.y);
     float petalSpaceID = floor((uv.x+offset)* TWO_PI * leavesSpread);
     float petalLength = 1.;
     float petalThickness = smoothstep(-0.01, 1., pow( (1.-(petalSpace.y / petalLength)), 0.85)) *0.9 - smoothstep(0.7,0., petalSpace.y / petalLength);
     petalSpace.x += sin((petalSpace.y + randOneD(petalSpaceID + seed) * TWO_PI )*4. )*0.3 * smoothstep(0.5, 1., (petalSpace.y / petalLength));                  
     DrawHalfVectorWithLength(vec2(0.5, 0.), vec2(0.,1.), 1., petalSpace, petalThickness,  petalColor, col);
}

void DrawSunFlower(vec2 uv, float seed, inout vec3 col, float mask) {
    vec2 stroke_warp = vec2(s_noise(uv*20.0), s_noise(uv*20.0 + 10.0)) * 0.08;
    vec2 w_uv = uv + stroke_warp; 
    float paintStroke = fbm(uv * 25.0) * 0.25; 

    vec2 stem_uv = w_uv;
    
    // Calculate the sine wave ripple moving up the stem
    float flowAmount = sin(stem_uv.y * 1.5 - iTime * 2.0 + seed * TWO_PI) * 0.2;
    // Anchor the root (at y = -7.0) so it doesn't move, maximum sway at top (y = 0.0)
    float bendFactor = smoothstep(-7.0, 0.0, stem_uv.y);
    stem_uv.x -= flowAmount * bendFactor;

    // Original Stem & Leaves (now automatically curved by the warped space)
    DrawHalfVectorWithLength(vec2(0.), vec2(0.,-1.), 7., stem_uv, 0.15, SunflowerStem + paintStroke, col);
    DrawStemLeave(vec2(0.,-2.+ randOneD(seed+5.125) *-2.), normalize(vec2(max(0.2,randOneD(seed+712.125)),(randOneD(seed+81.215) -0.3) * 0.3)), 5., stem_uv, 0.3 +randOneD(seed+12.125) *0.4 , SunflowerStemBright + paintStroke, col);
    DrawStemLeave(vec2(0.,-3.+ randOneD(seed+61.125) *-2.), normalize(vec2(-1.0 * max(0.2,randOneD(seed+4.25)),(randOneD(seed+73.25) -0.3) * 0.3)), 5., stem_uv, 0.3 + randOneD(seed+0.125) * 0.4, SunflowerStem + paintStroke, col);
    
    vec2 head_uv = w_uv;
    
    // Calculate exact displacement of the stem tip (at y = 0.0) so head perfectly connects
    float headFlow = sin(0.0 * 1.5 - iTime * 2.0 + seed * TWO_PI) * 0.2;
    float headBend = smoothstep(-7.0, 0.0, 0.0); // equals 1.0
    head_uv.x -= headFlow * headBend;

    // Convert to polar coordinates
    head_uv = vec2(atan(head_uv.y, head_uv.x), length(head_uv) * 0.55);
    
    // Slowly rotate the petals counter-clockwise
    float spinSpeed = 0.15 + (randOneD(seed) * 0.1); 
    head_uv.x -= iTime * spinSpeed; 
    
    vec3 DrawnFlower= col;
    
    // Original Petals
    DrawPetals(head_uv, DrawnFlower, 53.126 + seed, +0.4 + randOneD(seed), SunflowerLeavesTwo + paintStroke);
    DrawPetals(head_uv, DrawnFlower, 0. + seed, randOneD(seed+7.125)*-0.5, SunflowerLeavesOne + paintStroke);

    // Segmented Spiral Center
    float centerMask = smoothstep(lineSize, lineSize + smoothing, 0.45 - head_uv.y);
    
    if (centerMask > 0.0) {
        float yd = 20.0; 
        float r_id = floor((head_uv.y + 0.01) * yd); 
        float xd = max(4.0, floor(r_id * TWO_PI * 0.8)); 
        float spiralAngle = head_uv.x + head_uv.y * 5.0 + seed * 10.0;
        float seg_x = (spiralAngle / TWO_PI) * xd;
        float seg_y = (head_uv.y + 0.01) * yd;
        
        vec2 cell_id = vec2(floor(seg_x), floor(seg_y));
        vec2 lc = vec2(fract(seg_x), fract(seg_y));
        float n = s_noise(cell_id * 5.0 + seed);
        lc += (vec2(s_noise(cell_id*3.0), s_noise(cell_id*7.0)) - 0.5) * 0.4;
        
        float dashMask = st(abs(lc.x - 0.5), 0.4, 0.08) * st(abs(lc.y - 0.5), 0.4, 0.08);
        
        vec3 midRing = mix(SunflowerInsideTwo, SunflowerInsideThree, n);
        vec3 centerBgCol = mix(SunflowerInsideOne, midRing, smoothstep(0.45, 0.20, head_uv.y));
        centerBgCol = mix(centerBgCol, SunflowerInsideThree, smoothstep(0.12, 0.0, head_uv.y));
        
        vec3 strokeCol = centerBgCol * (1.1 + n * 0.4); 
        vec3 gapCol = centerBgCol * 0.4;                
        
        vec3 finalCenterCol = mix(gapCol, strokeCol, dashMask);
        DrawnFlower = mix(DrawnFlower, finalCenterCol, centerMask);
    }
    
    col = mix(col, DrawnFlower, mask);
}

void DrawSunFlowerField(vec2 OG_UV, float seed, vec2 offset, float fieldMask, float totalMovementSpeed, inout vec3 col,float tiling) {
     OG_UV += offset; OG_UV.x += iTime *totalMovementSpeed;
     vec2  flowerRepeatedSpace = vec2(fract(OG_UV.x*tiling), OG_UV.y*tiling);
     vec2  idFlowerCoord = vec2(floor(OG_UV.x*tiling), seed*21.215);
     flowerRepeatedSpace -= vec2(0.5) + vec2(0.15,0.5) *(randOneD (dot(idFlowerCoord , vec2(1.126, 26.6))) - 0.5) ;
     flowerRepeatedSpace *= 4. + 0.2 *(randOneD (dot(idFlowerCoord , vec2(8.136, 5.316))) - 0.5);
     DrawSunFlower(flowerRepeatedSpace, randOneD (dot(idFlowerCoord , vec2(21.126, 8.3216))), col,fieldMask );
}

void mainImage( out vec4 O, in vec2 g) {
    vec2 r = iResolution.xy;
    vec2 uv = (g+g-r)/r.y;
    
    vec2 sun_pos = vec2(r.x/r.y * 0.45, -.45); 
    vec2 tree_pos = vec2(-r.x/r.y * 0.2, -.2); 
    
    vec2 sh, u, id, lc, t;
    vec3 f = vec3(0), c;
    float xd, yd, h, a=0.0, l;
    vec4 C;
    float sm = 3./r.y; 

    sh = rotate2D(sun_pos, noise(uv+iTime*.25)*.3); 
    if (uv.y > -.8) 
    {
        u = uv + sh;
        yd = 60.; 
        id =  vec2((length(u)+.01)*yd,0); 
        xd = floor(id.x)*.09; 
        h = (hash12(floor(id.xx))*.5+.25)*(iTime+10.)*.25; 
        t = rotate2D (u,h); 
    
        id.y = atan(t.y,t.x)*xd;
        lc = fract(id); 
        id -= lc;
    
        t = vec2(cos((id.y+.5)/xd)*(id.x+.5)/yd,sin((id.y+.5)/xd)*(id.x+.5)/yd); 
        t = rotate2D(t,-h) - sh;
    
        h = noise(t*vec2(.5,1)-vec2(iTime*.2,0)) * step(-.25,t.y); 
        h = smoothstep (.052,.055, h);
        
        lc += (noise(lc*vec2(1,4)+id))*vec2(.7,.2); 
        
        f = mix (sp(sin(length(u)-.1))*.35, 
                 mix(sp(sin(length(u)-.1)+(hash12(id)-.5)*.15),vec3(1),h), 
                 st(abs(lc.x-.5),.4,sm*yd)*st(abs(lc.y-.5),.48,sm*xd));
                 
        vec2 sun_uv = uv - sun_pos;
        vec2 spun_uv = rotate2D(sun_uv, -iTime * 0.5); 
        
        float dToSun = length(sun_uv);
        float angle = atan(spun_uv.y, spun_uv.x);
        
        float rays = sin(angle * 12.0 + s_noise(spun_uv*15.0)*2.0) * 0.03;
        float sunCore = smoothstep(0.12, 0.02, dToSun);
        float sunCorona = smoothstep(0.35, 0.1, dToSun + rays) * s_noise(spun_uv * 20.0);
        
        vec3 sunColor = mix(vec3(0.9, 0.4, 0.0), vec3(1.0, 1.0, 0.2), smoothstep(0.15, 0.0, dToSun));
        
        float swirl = smoothstep(0.0, 0.8, sin(dToSun * 40.0 + angle * 4.0));
        sunColor += swirl * 0.3 * sunCore;
        
        f = mix(f, sunColor, clamp(sunCore + sunCorona, 0.0, 1.0));
    };

    // mountains
    if (uv.y < 0.25)
    {
        float cld = noise(-sh*vec2(.5,1)  - vec2(iTime*.2,0)); 
        cld = 1.- smoothstep(.0,.15,cld)*.5;

        u = (uv - vec2(0.0, 0.25)) * vec2(1, 15); 
        id = floor(u);

        for (float i = 1.; i > -1.; i--)
        {
            if (id.y+i < 0.0)
            {
                lc = fract(u)-.5;
                lc.y = (lc.y+(sin(uv.x*8.-iTime*0.8+id.y+i))*.3-i)*4.; 
                h = hash12(vec2(id.y+i,floor(lc.y))); 
                
                xd = 6.+h*4.;
                yd = 30.;
                lc.x = uv.x*xd+sh.x*9.; 
                lc.x += sin(iTime * (.2 + h*1.5))*.5; 
                h = .8*smoothstep(5.,.0,abs(floor(lc.x)))*cld+.1; 
                
                vec3 mtnBase = MountainLayerFourCol;
                vec3 mtnHigh = MountainLayerThreecol;
                f = mix(f,mix(mtnBase,mtnHigh,h),st(lc.y,0.,sm*yd)); 
                lc += noise(lc*vec2(3,.5))*vec2(.1,.6); 
                
                vec3 strokeCol = hue(hash12(floor(lc))*.1+.35).rgb*(1.2+floor(lc.y)*.17);
                f = mix(f, 
                    mix(strokeCol,FieldMid,h),
                    st(lc.y,0.,sm*xd)
                    *st(abs(fract(lc.x)-.5),.48,sm*xd)*st(abs(fract(lc.y)-.5),.3,sm*yd)
                );
            }
        }
    }


    vec2 uv_sun = g/r; uv_sun -= 0.5; uv_sun.x *= r.x/r.y; uv_sun *= 5.; 
    float totalMovementSpeed = 0.05; float movement = iTime * totalMovementSpeed;

    float fieldMask = smoothstep(0.05, 0.15, 0.1 - uv.y); 
    vec3 fieldBaseColor = mix(FieldMid + pow(s_noise((uv_sun + vec2(movement * 4.7,0.))*20.0), 2.)*0.05,
                              SunflowerLeavesOne + pow(s_noise((uv_sun + vec2(movement*1.,0.))*20.0), 2.)*0.05, smoothstep(-0.2, 0.0, uv_sun.y));
    fieldBaseColor = mix(fieldBaseColor, FieldDark + pow(s_noise((uv_sun + vec2(movement*6.2,0.))*20.0), 2.)*0.05, smoothstep(-0.6, -1.4, uv_sun.y));
    f = mix(f, fieldBaseColor, fieldMask);
    
    O = vec4(f, 1.0); 

    // Sunflowers (Background & Midground)
    vec2 OG_UV = g/r.xy; OG_UV.x *= r.x/r.y;
    float fm = smoothstep(0.01, 0.05, 0.1 - uv_sun.y); 

    if (uv.y < 0.1) {
        vec3 tCol = O.rgb;
        DrawSunFlowerField(OG_UV, 0., vec2(0.51,-0.48),    fm, totalMovementSpeed, tCol, 90.);
        totalMovementSpeed *= 1.1; DrawSunFlowerField(OG_UV, 6.621, vec2(0.51,-0.46), fm,  totalMovementSpeed, tCol, 50.);
        totalMovementSpeed *= 1.1; DrawSunFlowerField(OG_UV, 7.23, vec2(0.51,-0.43),  fm,  totalMovementSpeed, tCol, 29.);
        totalMovementSpeed *= 1.1; DrawSunFlowerField(OG_UV, 12.6, vec2(0.51,-0.4),   fm,  totalMovementSpeed, tCol, 22.);
        totalMovementSpeed *= 1.1; DrawSunFlowerField(OG_UV, -7.21, vec2(0.51,-0.35), fm,  totalMovementSpeed, tCol, 15.);
        totalMovementSpeed *= 1.1; DrawSunFlowerField(OG_UV, 2.125, vec2(0.51,-0.3),  fm,  totalMovementSpeed, tCol, 12.);
        O.rgb = tCol; 
    }

    // cypress tree
    float T = sin(iTime*.5); 

    if (abs(uv.x+tree_pos.x-.1-T*.1) < .6) {
        u = uv + tree_pos; u.x -= sin(u.y+1.)*.2*(T+.75); u += noise(u*4.5-7.)*.25; 
        xd = 10., yd = 60.; t = u * vec2(1,yd); h = hash12(floor(t.yy)); t.x += h*.01; t.x *= xd; lc = fract(t); 
        float m = st(abs(t.x-.5),.5,sm*xd)*step(abs(t.y+20.),45.); 
        C = mix(vec4(.07), vec4(.5,.3,0,1)*(.4+h*.4), st(abs(lc.y-.5),.4,sm*yd)*st(abs(lc.x-.5),.45,sm*xd)); C.a = m;
        xd = 30., yd = 15.;
        for (float xs =0.;xs<4.;xs++) {
            u = uv + tree_pos + vec2 (xs/xd*.5 -(T +.75)*.15,-.7); 
            u += noise(u*vec2(2,1)+vec2(-iTime+xs*.05,0))*vec2(-.25,.1)*smoothstep (.5,-1.,u.y+.7)*.75; 
            t = u * vec2(xd,1.); h = hash12(floor(t.xx)+xs*1.4); yd = 5.+ h*7.; t.y *= yd; sh = t; lc = fract(t); h = hash12(t-lc); 
            t = (t-lc)/vec2(xd,yd)+vec2(0,.7);
            m = (step(0.,t.y)*step (length(t),.45) + step (t.y,0.)*step (-0.7+sin((floor(u.x)+xs*.5)*15.)*.2,t.y)) *step (abs(t.x),.5) *st(abs(lc.x-.5),.35,sm*xd*.5); 
            lc += noise((sh)*vec2(1.,3.))*vec2(.3,.3); f = hue((h+(sin(iTime*.2)*.5+.5))*.2).rgb-t.x; 
            C = mix(C, vec4(mix(f*.15,f*.6*(.7+xs*.2), st(abs(lc.y-.5),.47,sm*yd)*st(abs(lc.x-.5),.2,sm*xd)),m), m);
        }
        O = mix (O,C,C.a); 
    }
    
    // Sunflowers (Foreground)
    if (uv.y < 0.1) {
        vec3 tCol = O.rgb;
        totalMovementSpeed *= 1.2; DrawSunFlowerField(OG_UV, 1., vec2(1.126,-0.2),    fm,  totalMovementSpeed, tCol, 8.);
        totalMovementSpeed *= 1.2; DrawSunFlowerField(OG_UV, 5., vec2(0.,-0.05),      fm,  totalMovementSpeed, tCol, 4.);
        totalMovementSpeed *= 1.3; DrawSunFlowerField(OG_UV, 71.612, vec2(0.,0.1),    fm,  totalMovementSpeed, tCol, 3.);
        O.rgb = tCol; 
    }
    
    float global_canvas = fbm(uv * 100.0) * 0.05;
    O.rgb -= global_canvas;
}