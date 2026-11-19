#version 330 core

in vec2 texCoord0;
in vec3 normal;
in vec3 fragPos;

out vec4 FragColor;

// World-pass alpha modes (WorldAlphaMode in Shader.cs). Blending is off in the world pass and
// there is no sorted transparency, so only OPAQUE and MASK are supported; a glTF BLEND material
// is drawn opaque. This file is compiled twice: as is for OPAQUE materials, and with
// WORLD_ALPHA_MASK defined (Shader.AlphaMaskVariant) for MASK materials. Only the MASK program
// contains the discard, so opaque draws keep the GPU's early depth optimizations.

struct Material {
    vec4 baseColorFactor;
    float metallicFactor;
    float roughnessFactor;
    bool hasBaseColorTexture;
    sampler2D baseColorTexture;
    float alphaCutoff;
    vec3 emissiveFactor; // linear RGB added after exposure, before fog (0 = not emissive)
    bool hasEmissiveTexture;
    sampler2D emissiveTexture;
    vec2 emissiveUvScroll; // UV per second (0 = still)
};

uniform float uTime;

uniform Material material;

// Per-draw tint on the base colour (wave I): base * (1 + offset). EnemyRenderer sets it for each
// enemy and resets it to zero after the enemies, because players share this program. An unset
// uniform is zero, which leaves the colour unchanged.
uniform vec3 uActorTintOffset;

// ---- The equipment material response (HeldWeaponRenderer / WornArmourRenderer only). ----
// x metallic, y roughness, z grain, w accentMix (EquipmentSurface.ToUniform). Zero - the default of
// an unset uniform, and what the equipment renderers set when their pass ends - means every line of
// the block in main() is skipped, so an enemy, an NPC, a player body or a prop computes exactly what
// it computed before this uniform existed. The gate is `uEquipSurface.y > 0.0`: roughness is never
// zero for a real material (EquipmentSurface.MinRoughness is 0.18), so no material can be mistaken
// for "off".
uniform vec4 uEquipSurface;
// The real eye in world space. uCameraPosition (lighting.glsl) is the FOG origin, not the eye, so
// the specular half vector needs its own uniform; the equipment renderers set it once per pass.
uniform vec3 uEquipEye;

#include "lighting.glsl"
// The fire shadow atlas lookup: pointLights.glsl calls dreamFireVisibility, so this goes above it.
#include "fireShadows.glsl"
#include "pointLights.glsl"
// The boundary mist wall: the final colour is mixed toward smoke at and near the map edge.
#include "boundaryMist.glsl"
#include "clouds.glsl"

// Actors read brighter than terrain by day: the frame ambient is scaled by FrameLighting.Params.z
// (uLightingParams.z, set per time of day by SkyModel), the direct light down, and the result never
// drops below FrameLighting.Params.y (uLightingParams.y) times the base color. At ReferenceNoon
// (ambient 0.2, scale 3.25) this is today's 0.65 ambient, 0.75 diffuse and 0.35 floor; at night the
// scale drops so actors stay close to terrain brightness instead of looking self-lit.
const float ActorDirectScale = 0.75;

// ---- Equipment surface helpers. Nothing below is reachable unless uEquipSurface.y > 0.0. ----

// How hard a highlight is allowed to be, in one place. The sum is added before dreamExpose, so a
// real highlight is exposed with everything else and is allowed to clip to white - a highlight is
// supposed to be the brightest thing in frame - while a grazing one stays a value, not a flare.
const float EquipSpecularStrength = 0.30;
// The fresnel edge sheen rides much lower: at 28 px across it is there to separate a helm's rim
// from the head behind it, not to outline the piece.
const float EquipFresnelStrength = 0.08;
// Mirrors EquipmentSurface.MinRoughness. A narrower lobe than this aliases at the shipped zoom.
const float EquipMinRoughness = 0.18;
const float EquipMaxShininess = 512.0;

// A cheap 3D value hash, in [0, 1). Deterministic per world position, so the grain is nailed to the
// surface and does not crawl when the figure walks.
float dreamEquipHash(vec3 p) {
    p = fract(p * 0.3183099 + vec3(0.1, 0.2, 0.3));
    p *= 17.0;
    return fract(p.x * p.y * p.z * (p.x + p.y + p.z));
}

// Trilinear value noise on that hash, smoothstepped inside the cell.
float dreamEquipNoise(vec3 p) {
    vec3 i = floor(p);
    vec3 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    float n00 = mix(dreamEquipHash(i + vec3(0.0, 0.0, 0.0)), dreamEquipHash(i + vec3(1.0, 0.0, 0.0)), f.x);
    float n10 = mix(dreamEquipHash(i + vec3(0.0, 1.0, 0.0)), dreamEquipHash(i + vec3(1.0, 1.0, 0.0)), f.x);
    float n01 = mix(dreamEquipHash(i + vec3(0.0, 0.0, 1.0)), dreamEquipHash(i + vec3(1.0, 0.0, 1.0)), f.x);
    float n11 = mix(dreamEquipHash(i + vec3(0.0, 1.0, 1.0)), dreamEquipHash(i + vec3(1.0, 1.0, 1.0)), f.x);
    return mix(mix(n00, n10, f.y), mix(n01, n11, f.y), f.z);
}

// The wear field: three octaves at about 4, 11 and 26 cycles per world metre. The frequencies are
// chosen against the PIXEL RATE, not against the metre. A worn piece is roughly 0.25 m across and
// about 12 px wide at the shipped isometric zoom, so one pixel spans ~0.02 m: an octave at 38 or 72
// cycles per metre puts one or two full cycles INSIDE a pixel and averages away to a flat grey,
// which is exactly what the first in-engine pass of this wave showed. At 4 and 11 cycles the coarse
// octaves read as dents, soot and scuffing across a few pixels, which is the point. Centred near 0.5.
float dreamEquipGrain(vec3 worldPos) {
    float n = dreamEquipNoise(worldPos * 11.0) * 0.52;
    n += dreamEquipNoise(worldPos * 4.0) * 0.33;
    n += dreamEquipNoise(worldPos * 26.0) * 0.15;
    return clamp(n, 0.0, 1.0);
}

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
    vec3 h = normalize(toLight + viewDir);
    float spec = pow(max(dot(n, h), 0.0), shininess) * ((shininess + 8.0) * EquipLobeNormalisation);
    vec3 fresnel = f0 + (vec3(1.0) - f0) * pow(1.0 - max(dot(h, viewDir), 0.0), 5.0);
    return fresnel * (ndl * spec);
}

void main()
{
    vec4 baseColor = material.baseColorFactor;

    if (material.hasBaseColorTexture) {
        baseColor *= texture(material.baseColorTexture, texCoord0);
    }

    baseColor.rgb *= max(vec3(1.0) + uActorTintOffset, vec3(0.0));

#ifdef WORLD_ALPHA_MASK
    // glTF MASK: fragments below the cutoff are not drawn.
    if (baseColor.a < material.alphaCutoff) {
        discard;
    }
#endif

    vec3 n = normalize(normal);
    // Point lights (pointLights.glsl) count as direct light: scaled like the sun, before the floor.
    vec3 lit = (dreamAmbient(n) * uLightingParams.z + (dreamDirectShaded(n, fragPos) + dreamPointLights(n, fragPos)) * ActorDirectScale) * baseColor.rgb;
    lit = max(lit, baseColor.rgb * uLightingParams.y);

    // ---- Equipment only (see uEquipSurface above). The uniform is zero for every other draw and
    // this whole branch is then skipped, so the fragment is provably what it was two lines up. ----
    if (uEquipSurface.y > 0.0) {
        float metallic = clamp(uEquipSurface.x, 0.0, 1.0);
        float rough = clamp(uEquipSurface.y, EquipMinRoughness, 1.0);
        float grain = clamp(uEquipSurface.z, 0.0, 1.0);

        // Surface grain. The modulation is mostly DOWNWARD, so a piece gets pitted, not brighter.
        float wear = dreamEquipGrain(fragPos);
        float wearAlbedo = mix(1.0 - 0.34 * grain, 1.0 + 0.07 * grain, wear);
        // `lit` above is albedo * max(G, floor) and is LINEAR in the albedo, so scaling it here is
        // exactly the same thing as modulating the albedo before the floor clamp - and because it
        // scales both terms, the floor still holds and a dark piece can never go black.
        lit *= wearAlbedo;
        // A worn patch scatters wider: the hollows of the grain (low wear) are the rough ones.
        rough = clamp(rough + grain * 0.30 * (1.0 - wear), EquipMinRoughness, 1.0);

        vec3 v = normalize(uEquipEye - fragPos);
        float a = max(rough, EquipMinRoughness);
        float shin = clamp(2.0 / (a * a * a * a) - 2.0, 2.0, EquipMaxShininess);
        // Metal reflects its own colour (the albedo is bound to 0.42 a channel, so it is scaled back
        // up toward a real metal reflectance); a dielectric reflects a neutral 4%.
        vec3 f0 = mix(vec3(0.04), baseColor.rgb * 2.2, metallic);

        // Sun, moon and every point light. The sun lobe takes the shadow map, so plate inside a
        // doorway does not glint; the point lobe takes the fire shadows inside dreamPointSpecular.
        vec3 spec = dreamEquipLobe(n, v, uSunDirection.xyz, shin, f0)
            * uSunDirection.w * uSunColor.rgb * dreamSunVisibility(fragPos, n);
        spec += dreamEquipLobe(n, v, uMoonDirection.xyz, shin, f0) * uMoonDirection.w * uMoonColor.rgb;
        spec += dreamPointSpecular(n, fragPos, v, shin, f0);

        // Fresnel edge sheen: the sky caught on a rim. Metal takes most of it, leather a little.
        float rim = pow(1.0 - max(dot(n, v), 0.0), 5.0);
        spec += uAmbientSky.rgb * (rim * (0.10 + 0.90 * metallic) * EquipFresnelStrength);

        // Before dreamExpose, so a highlight is exposed with everything else and may clip to white.
        lit += spec * EquipSpecularStrength;
    }

    vec3 emissive = material.emissiveFactor;
    if (material.hasEmissiveTexture) {
        emissive *= texture(material.emissiveTexture, texCoord0 + material.emissiveUvScroll * uTime).rgb;
    }
    FragColor = vec4(dreamApplyBoundaryMist(dreamApplyFog(dreamExpose(lit) + emissive, fragPos), fragPos), baseColor.a);
}
