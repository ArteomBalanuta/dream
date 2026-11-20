#version 330 core
#include "postTone.glsl"

// The resolve: the last thing the world sees before the HUD. It adds the blurred overflow back on
// top of the scene, bends the result over the tone shoulder and writes it to the default
// framebuffer, after neutralising the chroma of the very darkest pixels.
//
// The order matters and is not the usual one. Bloom is added BEFORE the shoulder, not after, so
// the glow is subject to the same curve as the light that made it: a spark at 4.0 spreads its
// overflow into its neighbours, and the sum then rolls off together. Added after the curve the
// glow would be the one thing in the frame that could still clip, which is precisely the artefact
// this pass exists to remove.
//
// There is NO gamma or colour-space conversion here, deliberately. This engine's shaders write
// values that are already display-referred - the whole catalogue of albedos, fog colours and HUD
// tints was authored by eye against what the monitor showed - so an encode step here would move
// every pixel in the game and mean nothing but a global brightness change. This pass applies
// two transforms. The shoulder is the identity below its knee. The shadow neutralisation is the
// identity in the max channel everywhere and in chroma above 0.08; it runs LAST, and since it only
// changes pixels far below the knee it commutes with the shoulder. See postTone.glsl.
in vec2 vUv;
out vec4 FragColor;

uniform sampler2D uScene;
uniform sampler2D uBloom;
uniform float uBloomStrength;
uniform float uShadowNeutralise;

void main()
{
    vec3 scene = texture(uScene, vUv).rgb;
    vec3 bloom = texture(uBloom, vUv).rgb * uBloomStrength;
    FragColor = vec4(dreamToneNeutraliseShadows(dreamToneRolloff(scene + bloom), uShadowNeutralise), 1.0);
}
