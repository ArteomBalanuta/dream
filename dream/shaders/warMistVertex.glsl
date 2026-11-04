#version 330 core

// The war mist: a stack of horizontal quads, one per instance. Each is a four-vertex triangle
// strip whose corners come from gl_VertexID (no mesh VBO); the instance picks the slice height
// from uSliceHeights (absolute world y, bottom first). All slices cover the same XZ rectangle,
// the focus-centred window clipped to the map, which the renderer computes on the CPU.
uniform mat4 uViewProjection;
uniform vec2 uExtentMin;
uniform vec2 uExtentMax;
uniform float uSliceHeights[8];

out vec3 vWorldPos;
flat out float vSlice;

void main()
{
    vec2 corner = vec2(gl_VertexID & 1, gl_VertexID >> 1);
    vec2 xz = mix(uExtentMin, uExtentMax, corner);
    vec3 world = vec3(xz.x, uSliceHeights[gl_InstanceID], xz.y);
    vWorldPos = world;
    vSlice = float(gl_InstanceID);
    gl_Position = uViewProjection * vec4(world, 1.0);
}
