#version 330 core
#include "lighting.glsl"

in vec2 vUv;
in vec4 vColor;
in vec3 vWorldPos;

out vec4 FragColor;

uniform sampler2D uAtlas;

// Premultiplied atlas texel times the premultiplied instance colour. Blended with
// (One, OneMinusSrcAlpha): alpha 0 adds light, alpha above 0 also darkens what is behind it. The
// whole result, alpha included, is scaled by the fog transmittance, so a distant particle fades
// into the fog colour already on screen instead of being mixed towards it. Not exposed (the
// atlas holds light that is meant to read as bright) and no discard (the strip's blend does the
// cut-out).
void main()
{
    FragColor = texture(uAtlas, vUv) * vColor * dreamFogTransmittance(vWorldPos);
}
