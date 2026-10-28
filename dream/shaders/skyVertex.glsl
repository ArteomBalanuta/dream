#version 330 core

// The sky pass: one fullscreen triangle generated from gl_VertexID, with no vertex buffer.
// The inverse of the translation-free view-projection is computed on the CPU (SkyRenderer), so
// this shader inverts nothing.
uniform mat4 uInverseViewProjection;

// The far-plane point under this vertex, in homogeneous world-direction space. It is linear in
// NDC, so interpolating it and dividing per fragment gives the exact view ray.
out vec4 vFarPoint;

void main()
{
    vec2 ndc = vec2(gl_VertexID == 1 ? 3.0 : -1.0, gl_VertexID == 2 ? 3.0 : -1.0);
    // z == w: every sky fragment lands at depth 1, behind everything drawn (DepthFunc Lequal).
    gl_Position = vec4(ndc, 1.0, 1.0);
    vFarPoint = uInverseViewProjection * vec4(ndc, 1.0, 1.0);
}
