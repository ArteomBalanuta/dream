#version 330 core

// The post chain's vertex stage: one fullscreen triangle generated from gl_VertexID, with no
// vertex buffer and no attributes (a bound VAO is still required in a core profile). The same
// trick the sky pass uses; a triangle rather than a quad so there is no diagonal seam where two
// triangles meet and no fragment is shaded twice.
out vec2 vUv;

void main()
{
    vec2 ndc = vec2(gl_VertexID == 1 ? 3.0 : -1.0, gl_VertexID == 2 ? 3.0 : -1.0);
    vUv = ndc * 0.5 + 0.5;
    gl_Position = vec4(ndc, 0.0, 1.0);
}
