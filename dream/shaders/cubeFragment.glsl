#version 330 core

in vec2 texCoord0;
in vec3 normal;
in vec3 fragPos;

out vec4 FragColor;

// World-pass alpha modes (WorldAlphaMode in Shader.cs). Blending is off in the world pass and
// there is no sorted transparency, so only OPAQUE and MASK are supported: a glTF BLEND material
// is uploaded as OPAQUE and drawn opaque.
const int ALPHA_MODE_OPAQUE = 0;
const int ALPHA_MODE_MASK = 1;

// Define the Material structure
struct Material {
    vec4 baseColorFactor;
    float metallicFactor;
    float roughnessFactor;
    bool hasBaseColorTexture;
    sampler2D baseColorTexture;
    int alphaMode;
    float alphaCutoff;
};

// Declare the uniform variable
uniform Material material;

void main()
{
    vec4 baseColor = material.baseColorFactor;

    if (material.hasBaseColorTexture) {
        baseColor *= texture(material.baseColorTexture, texCoord0);
    }

    // glTF MASK: fragments below the cutoff are not drawn. OPAQUE never takes this path.
    if (material.alphaMode == ALPHA_MODE_MASK && baseColor.a < material.alphaCutoff) {
        discard;
    }

    // Lighting
    vec3 lightPos = vec3(4.0, 2.0, 2.0);
    vec3 lightColor = vec3(1.0);
    vec3 norm = normalize(normal);
    vec3 lightDir = normalize(lightPos - fragPos);
    
    // Ambient lighting
    float ambientStrength = 0.2;  // Adjust for more/less ambient light
    vec3 ambient = ambientStrength * lightColor;

    // Diffuse lighting
    float diff = max(dot(norm, lightDir), 0.0);
    vec3 diffuse = diff * lightColor;
    
    // Final color calculation
    vec3 result = (ambient + diffuse) * baseColor.rgb;
    FragColor = vec4(result, baseColor.a);
}
