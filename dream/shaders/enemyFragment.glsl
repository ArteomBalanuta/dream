#version 330 core

in vec2 texCoord0;
in vec3 normal;
in vec3 fragPos;

out vec4 FragColor;

// World-pass alpha modes (WorldAlphaMode in Shader.cs). Blending is off in the world pass and
// there is no sorted transparency, so only OPAQUE and MASK are supported; a glTF BLEND material
// is drawn opaque. This file is compiled twice: as is for OPAQUE materials, and with
// WORLD_ALPHA_MASK defined (Shader.AlphaMaskVariant) for MASK materials. Only the MASK program
// contains the discard, so opaque draws keep the GPU's early depth optimizations.

struct Material {
    vec4 baseColorFactor;
    float metallicFactor;
    float roughnessFactor;
    bool hasBaseColorTexture;
    sampler2D baseColorTexture;
    float alphaCutoff;
    vec3 emissiveFactor; // linear RGB added after exposure, before fog (0 = not emissive)
};

uniform Material material;

#include "lighting.glsl"

// Actors read brighter than terrain by day: the frame ambient is scaled by FrameLighting.Params.z
// (uLightingParams.z, set per time of day by SkyModel), the direct light down, and the result never
// drops below FrameLighting.Params.y (uLightingParams.y) times the base color. At ReferenceNoon
// (ambient 0.2, scale 3.25) this is today's 0.65 ambient, 0.75 diffuse and 0.35 floor; at night the
// scale drops so actors stay close to terrain brightness instead of looking self-lit.
const float ActorDirectScale = 0.75;

void main()
{
    vec4 baseColor = material.baseColorFactor;

    if (material.hasBaseColorTexture) {
        baseColor *= texture(material.baseColorTexture, texCoord0);
    }

#ifdef WORLD_ALPHA_MASK
    // glTF MASK: fragments below the cutoff are not drawn.
    if (baseColor.a < material.alphaCutoff) {
        discard;
    }
#endif

    vec3 n = normalize(normal);
    vec3 lit = (dreamAmbient(n) * uLightingParams.z + dreamDirect(n) * ActorDirectScale) * baseColor.rgb;
    lit = max(lit, baseColor.rgb * uLightingParams.y);

    FragColor = vec4(dreamApplyFog(dreamExpose(lit) + material.emissiveFactor, fragPos), baseColor.a);
}
