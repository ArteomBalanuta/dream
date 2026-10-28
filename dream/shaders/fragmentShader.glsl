#version 330 core
out vec4 fragColor; // Output color

uniform vec3 color; // Color uniform (set to brown)

void main() {
    // Opaque: with alpha 0 a SrcAlpha/OneMinusSrcAlpha blend would draw nothing.
    fragColor = vec4(color, 1.0);
}