#version 330 core
// Classic HUD batch. Output is premultiplied alpha (blend ONE, ONE_MINUS_SRC_ALPHA).
// Mode 0: texture colour times tint. Mode 1: two-channel mask, R is the fill and G the rim.
// Mode 2: flat tint colour. The tint and rim alphas act as fades.
uniform sampler2D uTexture;
in vec2 vUv;
in vec4 vTint;
in vec4 vRim;
flat in uint vMode;
out vec4 outColor;
void main() {
    if (vMode == 2u) {
        outColor = vec4(vTint.rgb * vTint.a, vTint.a);
        return;
    }
    vec4 t = texture(uTexture, vUv);
    if (vMode == 1u) {
        float fill = t.r * vTint.a;
        float rim = t.g * vRim.a;
        outColor = vec4(fill * vTint.rgb + rim * vRim.rgb, fill + rim);
        return;
    }
    float a = t.a * vTint.a;
    outColor = vec4(t.rgb * vTint.rgb * a, a);
}
