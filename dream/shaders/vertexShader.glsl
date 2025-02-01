#version 330 core

layout(location = 0) in vec3 aPosition; // Vertex position

uniform mat4 model;      // Model matrix
uniform mat4 view;       // View matrix
uniform mat4 projection; // Projection matrix

void main() {
    // Transform the vertex position
    gl_Position = projection * view * model * vec4(aPosition, 1.0);
}