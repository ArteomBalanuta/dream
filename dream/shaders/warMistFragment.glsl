#version 330 core
#include "lighting.glsl"

in vec3 vWorldPos;
flat in float vSlice;

out vec4 FragColor;

uniform sampler2D uNoise;        // tileable RG8 fbm (WarMistNoiseTexture), repeat
uniform sampler2D uExploration;  // R8, one texel per map cell (ExplorationTexture), clamp
uniform vec2 uFocus;             // XZ of the camera focus (the player)
uniform vec2 uMapSize;           // map width and depth in metres
uniform float uTime;             // seconds since the scene started
uniform float uSliceCount;       // slices in the stack

// These repeat WarMistField.cs; WarMistStructuralTests pins both.
const float ClearRadius = 6.5;
const float FullRadius = 12.0;
const float UnexploredDensity = 0.72;
const float ExploredDensity = 0.30;
const float MaxTargetDensity = 0.95;
const float NoiseModMin = 0.6;
const float NoiseModMax = 1.3;

const vec2 WindDirection = vec2(0.8, 0.6);
const float WindSpeed = 0.35;          // metres per second
const float BigScale = 1.0 / 30.0;     // noise tiles per metre, the large billows
const float DetailScale = 1.0 / 11.0;  // the small curls
const vec3 MistAlbedo = vec3(0.30, 0.31, 0.34);

// Premultiplied, blended with (One, OneMinusSrcAlpha). Nothing at all inside the clear radius
// (the early out writes zero, which the blend leaves untouched; there is no discard). Beyond it
// the density rises to its full value, is thinner over ground the player has seen, and is
// modulated by two moving noise fetches so the veil drifts and breathes. Each slice carries the
// alpha that makes the whole stack compose to the target. Lit as an up-facing surface and scaled
// by the fog transmittance like every other world pass; no exposure curve beyond the frame's.
void main()
{
    float distanceToFocus = length(vWorldPos.xz - uFocus);
    float veil = smoothstep(ClearRadius, FullRadius, distanceToFocus);
    if (veil <= 0.0)
    {
        FragColor = vec4(0.0);
        return;
    }

    // Layers drift at slightly different speeds (1.0x at the bottom to 1.3x at the top) and start
    // from different places, so the stack parallaxes instead of moving as one sheet.
    float layer01 = vSlice / max(uSliceCount - 1.0, 1.0);
    float speed = mix(1.0, 1.3, layer01);
    vec2 phase = vec2(vSlice * 0.37, vSlice * 0.61);
    vec2 drift = WindDirection * (WindSpeed * speed * uTime);
    vec2 bigUv = (vWorldPos.xz + drift) * BigScale + phase;
    vec2 detailUv = (vWorldPos.xz + vec2(-drift.y, drift.x) * 0.6) * DetailScale - phase;
    float big = texture(uNoise, bigUv).r;
    float detail = texture(uNoise, detailUv).g;

    float n = clamp((big * 0.65 + detail * 0.35 - 0.5) * 1.7 + 0.5, 0.0, 1.0);
    float noiseMod = mix(NoiseModMin, NoiseModMax, n);
    float explored = texture(uExploration, vWorldPos.xz / uMapSize).r;
    float target = clamp(veil * mix(UnexploredDensity, ExploredDensity, explored) * noiseMod, 0.0, MaxTargetDensity);
    float sliceVariation = mix(0.85, 1.15, detail);
    float alpha = (1.0 - pow(1.0 - target, 1.0 / uSliceCount)) * sliceVariation;

    vec3 up = vec3(0.0, 1.0, 0.0);
    vec3 lit = MistAlbedo * (dreamAmbient(up) + 0.6 * dreamDirect(up)) * uLightingParams.x;
    lit *= mix(0.85, 1.05, n);
    FragColor = vec4(lit * alpha, alpha) * dreamFogTransmittance(vWorldPos);
}
