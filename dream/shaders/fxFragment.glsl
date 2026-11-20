#version 330 core
#include "lighting.glsl"
#include "boundaryMist.glsl"

in vec2 vUv;
in vec4 vColor;
in vec3 vWorldPos;

out vec4 FragColor;

uniform sampler2D uAtlas;

// ---- HDR intensity. ----
// The pass draws into the RGBA16F world target (postTone.glsl), where nothing clips and only light
// above the 0.80 knee blooms. An atlas texel times a packed instance colour is at most 1.0 in every
// channel (both are 8-bit normalised), so without this a spark's core could never exceed the knee by
// more than 0.2 and could not glow at all. The gain is applied to the LIGHT a particle emits, chosen
// by how bright that light already is:
//   gain = 1 + (FxHdrPeak - 1) * smoothstep(FxHdrFloor, FxHdrFull, max channel) * (1 - alpha)
// - Below FxHdrFloor (0.55) the gain is exactly 1.0 and its slope is zero there, so smoke, dust, ash
//   and the dim edge of any glow are bit-for-bit what they were: nothing dark is dragged up, and no
//   seam appears where the boost begins. 0.55 sits under the knee, so the boost only starts working
//   on texels that were already most of the way to glowing.
// - At FxHdrFull (0.95) or above, the texel's core, the gain is FxHdrPeak. 1.0 * 2.5 = 2.5, which is
//   1.7 over the knee: the shoulder (postTone.glsl) shows it at 0.8 + 0.2 * 1.7 / 1.9 = 0.98 and the
//   bloom takes the 1.7. A white-hot spark reads as a glowing point, not a flat 1.0 dot.
// - A mid-bright halo is lifted in proportion: the kill flash colour (0.75, 0.40, 0.16) peaks at 0.75
//   and gets smoothstep(0.55, 0.95, 0.75) = 0.5 -> gain 1.75 -> 1.31, 0.51 over the knee: a soft glow,
//   not a flare.
// - (1 - alpha) keeps occluding particles from glowing. Alpha is premultiplied coverage: a particle
//   with alpha 0 is pure added light and gets the whole boost; one with alpha 1 is an opaque body.
//   The ash puff (white 0.75 alpha) therefore stays a dull grey whatever its texel holds.
// The colour channels move together (a scalar gain), so a hot orange spark stays orange.
const float FxHdrPeak = 2.5;
const float FxHdrFloor = 0.55;
const float FxHdrFull = 0.95;

float dreamFxHdrGain(vec4 premultiplied)
{
    float m = max(max(premultiplied.r, premultiplied.g), premultiplied.b);
    float glow = smoothstep(FxHdrFloor, FxHdrFull, m) * (1.0 - clamp(premultiplied.a, 0.0, 1.0));
    return 1.0 + ((FxHdrPeak - 1.0) * glow);
}

// Premultiplied atlas texel times the premultiplied instance colour, its light lifted by
// dreamFxHdrGain. Blended with (One, OneMinusSrcAlpha): alpha 0 adds light, alpha above 0 also
// darkens what is behind it (alpha itself is NOT scaled). The whole result, alpha included, is then
// scaled by the fog transmittance, so a distant particle fades into the fog colour already on screen
// instead of being mixed towards it. Not exposed (the atlas holds light that is meant to read as
// bright) and no discard (the strip's blend does the cut-out). Particles past the map edge fade out
// with the boundary mist (boundaryMist.glsl), so a spark or a flash beyond the wall does not show
// through it.
void main()
{
    vec4 emitted = texture(uAtlas, vUv) * vColor;
    emitted.rgb *= dreamFxHdrGain(emitted);
    FragColor = emitted * dreamFogTransmittance(vWorldPos) * (1.0 - dreamBoundaryOpacity(vWorldPos));
}
