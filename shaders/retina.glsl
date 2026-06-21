
float gaussian(float delta, float width, float x){
    return exp(-pow(x+delta,2.)/(2.*pow(width,2.)));
}
vec3 wavelengthToRGB(float wavelength){
    float x = (wavelength - 380.)/(700.-380.);
    return vec3(gaussian(-.8,.25,x)+gaussian(-.1,.1,x)*0.15,gaussian(-.6,.20,x),gaussian(-.3,.25,x));
}

void mainImage( out vec4 col, in vec2 fragCoord )
{
    
    vec2 uv = fragCoord/iResolution.xy;

    
    float wl = mix(370., 710., cos(iTime/5.+uv.x*3.)/2.+0.5);
    col = vec4(wavelengthToRGB(wl),1.0);
    
}