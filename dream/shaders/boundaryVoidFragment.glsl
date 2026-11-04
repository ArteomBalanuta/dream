#version 330 core
#include "lighting.glsl"
#include "boundaryMist.glsl"

// The boundary void pass, drawn with skyVertex.glsl (a fullscreen triangle at depth 1) after the
// world and the actors: it only shades the pixels no surface covered. The view ray from the true
// camera is cut with the floor plane; a hit outside the map is opaque smoke, a hit inside the map
// (a dug hole) or a ray that never reaches the floor is left alone. The smoke comes from the same
// function the world fragments use, so a wall fragment and the void next to it share one noise
// field.
in vec4 vFarPoint;

out vec4 FragColor;

// The true camera position in world space (in isometric view FrameLighting's camera is the focus).
uniform vec3 uEye;

// Premultiplied output for the (One, OneMinusSrcAlpha) blend: alpha 1 replaces the clear colour.
void main()
{
    if (uMistParams.x <= 0.0) {
        discard;
    }

    vec3 dir = normalize(vFarPoint.xyz / vFarPoint.w);
    if (dir.y > -0.0005) {
        discard;
    }

    float t = (uMistParams.z - uEye.y) / dir.y;
    if (t <= 0.0) {
        discard;
    }

    vec3 hit = uEye + dir * t;
    if (dreamMistInset(hit) > 0.0) {
        discard;
    }

    FragColor = vec4(dreamBoundarySmoke(hit), 1.0);
}
