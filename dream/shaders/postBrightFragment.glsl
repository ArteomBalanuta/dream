#version 330 core
#include "postTone.glsl"

// The bright pass: reads the resolved HDR scene and writes, at a quarter of its width and height,
// only the light that sits ABOVE the tone knee. Everything under the knee returns exactly zero
// (dreamToneOverflow), so a dark frame produces a black bloom texture and the glow cannot lift a
// shadow - which is the one thing the brief rules out.
//
// Four bilinear taps on a half-texel grid of the source give a 4x4 box over the 16 source texels
// this destination texel covers, so the downsample does not alias a single bright pixel into a
// flickering dot as the camera moves.
in vec2 vUv;
out vec4 FragColor;

uniform sampler2D uScene;
uniform vec2 uSourceTexel;   // 1 / scene resolution

void main()
{
    vec2 o = uSourceTexel;
    vec3 sum = texture(uScene, vUv + vec2(-o.x, -o.y)).rgb
             + texture(uScene, vUv + vec2(o.x, -o.y)).rgb
             + texture(uScene, vUv + vec2(-o.x, o.y)).rgb
             + texture(uScene, vUv + vec2(o.x, o.y)).rgb;
    FragColor = vec4(dreamToneOverflow(sum * 0.25), 1.0);
}
