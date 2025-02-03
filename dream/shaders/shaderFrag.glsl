#version 330 core
out vec4 FragColor;

in vec2 TexCoord;
in vec3 Normal;
in vec3 FragPos;

uniform sampler2D texture0;
uniform vec3 color;

void main()
{
    vec4 texColor = texture(texture0, TexCoord);
    if (texColor.a < 0.1) discard;
    
    // Simple directional lighting
    vec3 lightDir = normalize(vec3(1.0, 1.0, 1.0));
    float diff = max(dot(normalize(Normal), lightDir), 0.2);
    
    FragColor = texColor * vec4(color, 1.0) * diff;
}