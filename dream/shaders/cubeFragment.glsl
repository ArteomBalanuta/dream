#version 330 core

in vec2 texCoord0;
in vec3 normal;
in vec3 fragPos;

out vec4 FragColor;

void main()
{
    vec3 lightPos = vec3(2.0, 5.0, 2.0);  // Try a new light position.
    vec3 lightColor = vec3(1.0);
    vec3 norm = normalize(normal);
    vec3 lightDir = normalize(lightPos - fragPos);
    
    float diff = max(dot(norm, lightDir), 0.0);
    
    // Output the diffuse factor as grayscale.
    FragColor = vec4(vec3(diff), 1.0);
}
