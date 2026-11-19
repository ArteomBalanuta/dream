// surface.glsl - `#include "surface.glsl"` AFTER lighting.glsl, fireShadows.glsl, pointLights.glsl and
// clouds.glsl (one level, no #version). The shared material response of the world fragment shaders:
// the normalised Blinn-Phong lobe, the roughness-to-shininess map, the reflectance a metal and a
// dielectric start from, and the fresnel rim. It reads only the frame's uAmbientSky (lighting.glsl);
// nothing here has a uniform of its own, and nothing runs unless a caller calls it.
//
// WHY IT IS A FILE. enemyFragment.glsl grew this inline and cubeFragment.glsl has none of it (no
// specular, no fresnel, no rim: the glTF metallic/roughness factors are uploaded and read by neither
// shader). Terrain and props stay diffuse-only on purpose, because switching a response on for them
// moves every pixel of the world and cannot be judged without the screen; what changes here is that
// when they DO get one, it is this one - one lobe, one normalisation, one fresnel - and not a third
// copy. (pointLights.glsl's dreamPointSpecular repeats the lobe maths with its own literal of the
// same normalisation; it is outside this workstream.)
//
// The world target is HDR (postTone.glsl): a highlight above the 0.80 knee is rolled off and blooms
// instead of clipping. Every function below is unchanged arithmetic from the version that lived in
// enemyFragment.glsl, so a pixel under the knee is what it was.

// Mirrors EquipmentSurface.MinRoughness. A narrower lobe than this aliases at the shipped zoom.
const float EquipMinRoughness = 0.18;
const float EquipMaxShininess = 512.0;

// The RIM: the sky caught on a plate edge, the cue that most separates armour from a flat render.
// Its strength is per material and derived from the two numbers that already describe one:
//   rimK = EquipRimBase + EquipRimMetal * metallic * (1 - roughness)
// so polished steel (1.0, 0.26) rims at 0.06 + 0.70 * 0.74 = 0.58, blackened iron (0.85, 0.54) at
// 0.34, leather (0.05, 0.66) at 0.07 and cloth (0, 0.96) at 0.06: plate outlines, hide barely does.
// It is added in exposed units straight into `lit` (not through EquipSpecularStrength) and is then
// capped to the headroom left under 1.0 / exposure, so it can brighten an edge but never clip one.
// Worst case before the cap: sky (0.26 by day) * rim (<= 1) * 0.58 = 0.15 against a 0.92 lit peak
// (albedo 0.42 * gain 2.2) - the cap, not the constant, is what guarantees no flat patch.
const float EquipRimBase = 0.06;
const float EquipRimMetal = 0.70;
// A fresnel exponent of 3, not 5: pow 5 is under 1/10 of its peak until the last 25 percent of the
// silhouette, a band under 1 px wide at 28 px across, which MSAA erases. Cubed it is about 3 px.
const float EquipRimPower = 3.0;

// The NORMALISED Blinn-Phong lobe: (shininess + 8) / (8 pi) keeps the energy under the lobe constant
// as it widens. Without it a rough material (leather at roughness 0.8 resolves to a shininess of
// about 2.5) gets a lobe nearly as wide as the diffuse term AT FULL AMPLITUDE, which is not a
// highlight at all - it is a second diffuse light, and it washed the whole figure to cream on the
// first in-engine pass of this wave. Normalised, a rough surface gets a dim broad sheen and a
// polished one a tight bright glint, which is the distinction the material table is for.
const float EquipLobeNormalisation = 1.0 / (8.0 * 3.14159265);

// One lobe against a directional light. toLight points TOWARD the light, viewDir TOWARD the eye;
// the result still has to be multiplied by the light's colour and intensity.
vec3 dreamEquipLobe(vec3 n, vec3 viewDir, vec3 toLight, float shininess, vec3 f0) {
    float ndl = max(dot(n, toLight), 0.0);
    if (ndl <= 0.0) { return vec3(0.0); }
    // The world pass draws with culling off, so a back face arrives with the FRONT face's normal.
    // Exactly antiparallel light and view would then make toLight + viewDir the zero vector and
    // normalize() a NaN, which spreads to the whole fragment. Fall out before that.
    // (`half` is a reserved word in GLSL, hence `hv`.)
    vec3 hv = toLight + viewDir;
    if (dot(hv, hv) < 1e-8) { return vec3(0.0); }
    vec3 h = normalize(hv);
    float spec = pow(max(dot(n, h), 0.0), shininess) * ((shininess + 8.0) * EquipLobeNormalisation);
    vec3 fresnel = f0 + (vec3(1.0) - f0) * pow(1.0 - max(dot(h, viewDir), 0.0), 5.0);
    return fresnel * (ndl * spec);
}

// Roughness (0..1, clamped away from a mirror) to a Blinn-Phong exponent: 2 / a^4 - 2, a = roughness.
// 0.26 (polished steel) resolves to about 430, 0.66 (leather) to about 8, 0.96 (cloth) to about 0.4
// and is then held at 2.
float dreamSurfaceShininess(float rough)
{
    float a = max(rough, EquipMinRoughness);
    return clamp(2.0 / (a * a * a * a) - 2.0, 2.0, EquipMaxShininess);
}

// Normal-incidence reflectance. Metal reflects its own colour (the albedo is bound to 0.42 a channel,
// so it is scaled back up toward a real metal reflectance); a dielectric reflects a neutral 4%.
vec3 dreamSurfaceF0(vec3 albedo, float metallic)
{
    return mix(vec3(0.04), albedo * 2.2, metallic);
}

// The sky caught on an edge (see EquipRimBase). Back faces carry the front normal with culling off,
// so dot(n, v) is negative there and the term is zero instead of a full sheen. Metal rims in its own
// colour. The CALLER still caps it to the headroom it wants to keep.
vec3 dreamSurfaceRim(vec3 n, vec3 v, vec3 albedo, float metallic, float rough)
{
    float ndv = dot(n, v);
    float rim = ndv <= 0.0 ? 0.0 : pow(1.0 - ndv, EquipRimPower);
    float rimK = EquipRimBase + (EquipRimMetal * metallic * (1.0 - rough));
    return uAmbientSky.rgb * mix(vec3(1.0), albedo * 2.4, metallic) * (rim * rimK);
}
