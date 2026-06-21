// CC0: Smaller Waterfall
//  Trying to minimize the previous waterfall shader a bit.
//  I probably missed something obvious as usual.

// This file is released under CC0 1.0 Universal (Public Domain Dedication).
// To the extent possible under law, mrange has waived all copyright
// and related or neighboring rights to this work.
// See <https://creativecommons.org/publicdomain/zero/1.0/> for details.

// Suggested by: moonlightoctopus
#define L length

void mainImage(out vec4 o, vec2 C) {
  float 
    i
  , z
  , T=.1*iTime+9.
  , d=T
  , j
  ;
  
  vec2
    r=iResolution.xy
  , P=(C+C-r)/r.x
  , Y=vec2(5e-3,1)
  ;
  
  vec4
    U=vec4(0,1,2,4)
  , O
  ;
  
  for(
    ;++i<39.&&d>1e-4
    ;z+=d=1.-sqrt(L(O*O))
  )
    O=z*normalize(vec4(P,2,0))-U.xwyx/4.5
    ;
  
  C=vec2(O.x,atan(O.z,O.y));
  O=vec4(4,16,99,0)/(1e3*dot(P=U.zy*P-r/r.x*U.xy,P)+6.);
  z=5e-4;
  for(
     r=L(fwidth(C))*U.yy
    ;++j<9.
    ;C.x+=Y.x/8.
  )
    i=fract(sin(dot(vec2(j,round(C/Y)),7.+U.xw)*73.))
  , P=C-(T+T*i)*U.xy
  , P-=round(P/Y)*Y
  , o=1.+sin(T+7.*fract(8663.*i)+U)
  , O+=dot(smoothstep(r,-r,vec2(L(max(P,-U.yx)),L(P)-z)-z),vec2(exp(19.*P.y),3))*o*o.w
  ;
  
  o=sqrt(tanh(O-.02*U.zwyy));
}
