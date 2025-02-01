#version 330 core
out vec4 fragColor; // Output color

uniform vec3 color; // Color uniform (set to brown)

void main() {
    fragColor = vec4(color, 0.0); // Set the fragment color
}