#version 330 core

// The effects pass: one camera-facing quad per instance, a four-vertex triangle strip whose
// corners come from gl_VertexID (no mesh VBO). FxInstance is 32 bytes, three attributes with
// divisor 1 (FxInstanceBuffer.BindAttributes). The camera axes arrive as uniforms taken from the
// view matrix being drawn with, so the quads face the camera in every view mode and nothing here
// reads the fog origin (uCameraPosition is the fog origin, not the eye, in the isometric view).
layout(location = 0) in vec4 iPositionHalfSize;  // xyz world position, w half size in metres
layout(location = 1) in vec4 iColor;             // premultiplied RGBA, unpacked from 4 normalized bytes
layout(location = 2) in vec3 iRotationFrameBias; // x rotation (radians), y atlas frame, z depth bias toward the camera

uniform mat4 uViewProjection;
uniform vec3 uCameraRight;
uniform vec3 uCameraUp;
uniform vec3 uCameraBack;

out vec2 vUv;
out vec4 vColor;
out vec3 vWorldPos;

// Atlas layout (FxAtlas): 8 columns by 4 rows of cells, frame 0 top-left, rows running downward.
const float AtlasColumns = 8.0;
const float AtlasRows = 4.0;

void main()
{
    vec2 corner = vec2(gl_VertexID & 1, gl_VertexID >> 1);
    vec2 local = corner * 2.0 - 1.0;
    float s = sin(iRotationFrameBias.x);
    float c = cos(iRotationFrameBias.x);
    vec2 rotated = vec2(local.x * c - local.y * s, local.x * s + local.y * c) * iPositionHalfSize.w;
    vec3 world = iPositionHalfSize.xyz + uCameraRight * rotated.x + uCameraUp * rotated.y
               + uCameraBack * iRotationFrameBias.z;

    float frame = floor(iRotationFrameBias.y + 0.5);
    float row = floor(frame / AtlasColumns);
    float column = frame - row * AtlasColumns;
    // Flip V: the atlas is y-down and is uploaded unflipped, so the top of a cell is its lower v.
    vUv = vec2((column + corner.x) / AtlasColumns, (row + 1.0 - corner.y) / AtlasRows);
    vColor = iColor;
    vWorldPos = world;
    gl_Position = uViewProjection * vec4(world, 1.0);
}
