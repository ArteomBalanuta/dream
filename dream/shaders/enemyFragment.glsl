#version 330 core

in vec2 texCoord0;
in vec3 normal;
in vec3 fragPos;

out vec4 FragColor;

struct Material {
    vec4 baseColorFactor;
    float metallicFactor;
    float roughnessFactor;
    bool hasBaseColorTexture;
    sampler2D baseColorTexture;
};

uniform Material material;

void main()
{
    vec4 baseColor = material.baseColorFactor;

    if (material.hasBaseColorTexture) {
        baseColor *= texture(material.baseColorTexture, texCoord0);
    }

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
