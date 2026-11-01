#version 330 core
// Classic HUD batch. Output is premultiplied alpha (blend ONE, ONE_MINUS_SRC_ALPHA).
// The low byte of the vertex mode picks the path. Mode 0: texture colour times tint. Mode 1: two-channel
// mask, R is the fill and G the rim. Mode 2: flat tint colour. The tint and rim alphas act as fades.
// Mode 3: a liquid gem. Mode 4: texture times tint added to what is below (alpha out is 0).
//
// Gem vertices carry the gem kind in bit 8 (0 health, 1 mana) and in bit 9 whether the mask sprite is
// the old top-down gradient. The quad's UV covers gem.<k>.full; the empty glass, the level map and the
// glints are on the same page at the fixed UV offsets in uGemOffset. Rim bytes: r fill, g surface phase,
// b low-health pulse, a glint sweep. Level map: R is the fill at which the pixel turns to liquid (0 at the
// bottom of the stone, 1 at the top, bent by the refraction), G the meniscus weight, B a per-facet phase.
// A legacy map has R = 0 at the top and 1 at the bottom, and no G or B: it gives today's straight clip.
uniform sampler2D uTexture;
uniform vec2 uGemOffset[6];  // kind * 3 + 0 empty glass, + 1 level map, + 2 glints
uniform vec4 uGemRect[2];    // per kind: UV origin and size of the full stone
in vec2 vUv;
in vec4 vTint;
in vec4 vRim;
flat in uint vMode;
out vec4 outColor;

const float TAU = 6.2831853;

void main() {
    uint mode = vMode & 255u;
    if (mode == 2u) {
        outColor = vec4(vTint.rgb * vTint.a, vTint.a);
        return;
    }
    vec4 t = texture(uTexture, vUv);
    if (mode == 1u) {
        float fill = t.r * vTint.a;
        float rim = t.g * vRim.a;
        outColor = vec4(fill * vTint.rgb + rim * vRim.rgb, fill + rim);
        return;
    }
    if (mode == 3u) {
        int k = int((vMode >> 8) & 1u);
        bool legacy = (vMode & 512u) != 0u;
        vec4 rect = uGemRect[k];
        vec2 local = (vUv - rect.xy) / rect.zw;
        vec4 glass = texture(uTexture, vUv + uGemOffset[3 * k]);
        vec4 d = texture(uTexture, vUv + uGemOffset[3 * k + 1]);
        vec4 g = texture(uTexture, vUv + uGemOffset[3 * k + 2]);

        // The level is stretched a little past 0..1, so fill 0 is all glass and fill 1 all liquid
        // whatever the surface wobble does.
        float thr = legacy ? 1.0 - d.r : d.r;
        float wobble = legacy ? 0.0 : 0.006 * sin(TAU * (vRim.g + d.b));
        float lvl = mix(-0.03, 1.03, vRim.r) + wobble;
        float liquid = smoothstep(thr - 0.012, thr + 0.012, lvl);
        vec3 rgb = mix(glass.rgb, t.rgb, liquid);

        // Meniscus: a bright line where the liquid ends, fading out at an empty or a full stone.
        float ends = smoothstep(0.0, 0.03, vRim.r) * (1.0 - smoothstep(0.97, 1.0, vRim.r));
        float e = (thr - lvl) / 0.018;
        rgb += exp(-e * e) * (legacy ? 1.0 : d.g) * ends * t.rgb * 1.2;

        // Glint sweep: a diagonal band that lights the baked glints as it passes.
        float s = (0.5 * (local.x + local.y) - mix(-0.25, 1.25, vRim.a)) / 0.09;
        rgb += g.rgb * g.a * exp(-s * s);

        // Low-health pulse brightens the liquid.
        rgb *= 1.0 + 0.35 * vRim.b * liquid;
        rgb = min(rgb * vTint.rgb, vec3(1.0));
        float a = mix(glass.a, t.a, liquid) * vTint.a;
        outColor = vec4(rgb * a, a);
        return;
    }
    if (mode == 4u) {
        float a = t.a * vTint.a;
        outColor = vec4(t.rgb * vTint.rgb * a, 0.0);
        return;
    }
    float a = t.a * vTint.a;
    outColor = vec4(t.rgb * vTint.rgb * a, a);
}
