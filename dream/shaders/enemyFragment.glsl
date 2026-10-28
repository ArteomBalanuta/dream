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
};

uniform Material material;

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

    vec3 lightPos = vec3(4.0, 4.0, 2.0);
    vec3 lightColor = vec3(1.0);
    vec3 norm = normalize(normal);
    vec3 lightDir = normalize(lightPos - fragPos);

    float ambientStrength = 0.65;
    vec3 ambient = ambientStrength * lightColor;

    float diff = max(dot(norm, lightDir), 0.0);
    vec3 diffuse = diff * lightColor * 0.75;

    float brightnessFloor = 0.35;
    vec3 litColor = (ambient + diffuse) * baseColor.rgb;
    vec3 boostedColor = max(litColor, baseColor.rgb * brightnessFloor);

    FragColor = vec4(boostedColor, baseColor.a);
}
