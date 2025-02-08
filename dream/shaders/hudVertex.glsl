#version 330 core
layout (location = 0) in vec2 aPos;
uniform mat4 projection;
void main()
{
    // Convert the 2D position into a clip-space position.
    gl_Position = projection * vec4(aPos, 0.0, 1.0);
}
