#version 330 core

in vec3 vNormal;
in vec2 vTexCoord;

out vec4 FragColor;

uniform sampler2D uTexture;

void main()
{
    // Simple directional light
    vec3 lightDir = normalize(vec3(0.5, 1.0, 0.5));
    float diff = max(dot(normalize(vNormal), lightDir), 0.2);
    FragColor = texture(uTexture, vTexCoord) * vec4(vec3(diff), 1.0);
}