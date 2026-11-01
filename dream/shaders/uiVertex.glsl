#version 330 core
// Classic HUD batch: positions are screen pixels with a top-left origin. The mode word is passed
// through untouched: its low byte is the fragment path and a gem vertex carries its kind in the bits
// above (see uiFragment.glsl).
uniform vec2 uViewport;
layout(location = 0) in vec2 aPosition;
layout(location = 1) in vec2 aUv;
layout(location = 2) in vec4 aTint;
layout(location = 3) in vec4 aRim;
layout(location = 4) in uint aMode;
out vec2 vUv;
out vec4 vTint;
out vec4 vRim;
flat out uint vMode;
void main() {
    vUv = aUv;
    vTint = aTint;
    vRim = aRim;
    vMode = aMode;
    gl_Position = vec4(aPosition.x * 2.0 / uViewport.x - 1.0, 1.0 - aPosition.y * 2.0 / uViewport.y, 0.0, 1.0);
}
