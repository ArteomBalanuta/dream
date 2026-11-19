#version 330 core

in vec2 texCoord0;
in vec3 normal;
in vec3 fragPos;
// The unskinned model-space position (cubeVertex.glsl): the space the equipment surface detail
// lives in, because it is nailed to the mesh and does not crawl when the figure walks.
in vec3 localPos;

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
    float n = dreamEquipNoise(worldPos * 6.0) * 0.52;
    n += dreamEquipNoise(worldPos * 2.5) * 0.33;
    n += dreamEquipNoise(worldPos * 14.0) * 0.15;
    return clamp(n, 0.0, 1.0);
}

// ---- Equipment surface DETAIL. Nothing below is reachable unless uEquipSurface.y > 0.0. ----
//
// x pattern id (EquipmentPattern), y model-to-metre scale for this part, z pattern depth,
// w a per-item seed so two pieces of the same material do not share a wear map.
//
// Keyed on `localPos`, the UNSKINNED model-space position (cubeVertex.glsl), and on texCoord0 -
// never on fragPos. fragPos is a world position, so a pattern keyed on it crawls across the piece
// as the figure walks, which is what the first pass of this did. localPos is nailed to the mesh,
// and uEquipDetail.y converts it to metres so a cuff and a cuirass get the same real grain size
// instead of the same number of cycles across very different objects.
//
// EVERY FREQUENCY HERE IS CHOSEN AGAINST THE PIXEL, NOT AGAINST THE METRE. A worn part is about
// 20 px across at the shipped isometric zoom, so a feature narrower than about 1/12th of a face is
// sub-pixel and MSAA averages it into a flat tone - the first two passes of this block were tuned
// by amplitude and showed nothing at all for exactly that reason. The rule: a border band is 0.10
// of the UV or wider, a rivet is 0.09 of the UV or bigger, and a noise octave is under about 20
// cycles per metre. Fewer, bigger, higher-contrast features. Anything finer is instructions spent
// on something the player cannot see.
//
// Every pattern may only take light AWAY (the albedo multiplier it returns is at most 1.0): the
// world pass has no tonemap and the albedo ceiling is the one thing holding the figure out of a
// flat clipped patch. Features that have to read BRIGHT - a rivet head, a polished boss - do it by
// lowering local roughness instead, so they answer with a specular glint rather than a lighter
// albedo. That is also what they physically are.
uniform vec4 uEquipDetail;

const int EquipPatternNone = 0;
const int EquipPatternPolishedPlate = 1;
const int EquipPatternWornPlate = 2;
const int EquipPatternHide = 3;
const int EquipPatternWeave = 4;
const int EquipPatternBone = 5;
const int EquipPatternWood = 6;
const int EquipPatternRune = 7;

// Distance to the nearest edge of a face's 0..1 UV square, in UV units. Every shape gives each face
// (or each band of a lathe) its own 0..1 patch, so this is "how far into the panel am I" - the
// coordinate a border bevel, a rivet row and a stitch line all live in.
float dreamEquipBorder(vec2 uv)
{
    vec2 d = min(uv, vec2(1.0) - uv);
    return min(d.x, d.y);
}

// A row of dots following the panel border at `inset`, spaced `period` apart along it. Returns the
// coverage of the nearest dot, 0..1.
float dreamEquipRivets(vec2 uv, float inset, float period, float radius)
{
    float border = dreamEquipBorder(uv);
    // Position ALONG the border: whichever axis is the near one supplies the other as the run.
    vec2 d = min(uv, vec2(1.0) - uv);
    float run = d.x < d.y ? uv.y : uv.x;
    float along = fract(run / period) - 0.5;
    float across = (border - inset) / radius;
    float r = sqrt((along * period / radius) * (along * period / radius) + (across * across));
    return 1.0 - smoothstep(0.6, 1.0, r);
}

// Value noise stretched along one axis: fibres, scratches, wood grain.
float dreamEquipStreak(vec3 p, vec3 stretch, float seed)
{
    return dreamEquipNoise((p * stretch) + vec3(seed * 7.3, seed * 3.1, seed * 11.7));
}

// The surface pattern. Returns the albedo multiplier (<= 1.0) and writes a roughness delta.
float dreamEquipPattern(vec3 lp, vec2 uv, int pattern, float depth, float seed, out float roughDelta)
{
    roughDelta = 0.0;
    if (pattern == EquipPatternNone || depth <= 0.0) {
        return 1.0;
    }

    float border = dreamEquipBorder(uv);
    float albedo = 1.0;

    if (pattern == EquipPatternPolishedPlate || pattern == EquipPatternWornPlate) {
        // A plate is a PANEL: a bevelled border, a groove where two plates meet, a row of rivets
        // just inside the edge, and hammer dimples across the field. The groove is what makes a
        // flat face read as one plate among several rather than as a side of a box.
        float groove = 1.0 - smoothstep(0.0, 0.105, border);
        float bevel = smoothstep(0.08, 0.26, border);
        albedo *= 1.0 - (0.46 * depth * groove);
        // The bevel inside the groove catches light: rougher in the groove, smoother on the field.
        roughDelta += depth * ((0.22 * groove) - (0.05 * bevel));

        float rivets = dreamEquipRivets(uv, 0.165, 0.34, 0.092);
        // A rivet head is a small polished dome: it reads by GLINTING, not by being pale.
        roughDelta -= depth * 0.40 * rivets;
        albedo *= 1.0 - (0.14 * depth * rivets);

        float dimples = dreamEquipNoise((lp * 9.0) + vec3(seed * 5.0));
        albedo *= 1.0 - (depth * 0.16 * (1.0 - dimples));
        roughDelta += depth * 0.16 * (1.0 - dimples);

        if (pattern == EquipPatternWornPlate) {
            // Blackened iron: deeper pitting, and long scratches running down the piece.
            float pits = dreamEquipNoise((lp * 16.0) + vec3(seed * 13.0));
            albedo *= 1.0 - (depth * 0.22 * smoothstep(0.52, 1.0, 1.0 - pits));
            float scratch = dreamEquipStreak(lp, vec3(14.0, 2.0, 14.0), seed);
            roughDelta += depth * 0.26 * smoothstep(0.62, 1.0, scratch);
        }
    } else if (pattern == EquipPatternHide) {
        // Leather: a fibrous grain running the length of the piece, a soft mottle, and a stitch
        // line following the border - the one detail that says "sewn" at this size.
        float fibre = dreamEquipStreak(lp, vec3(17.0, 4.0, 17.0), seed);
        albedo *= 1.0 - (depth * 0.20 * (1.0 - fibre));
        float mottle = dreamEquipNoise((lp * 5.0) + vec3(seed * 3.0));
        albedo *= 1.0 - (depth * 0.16 * (1.0 - mottle));
        float stitch = dreamEquipRivets(uv, 0.125, 0.20, 0.055);
        albedo *= 1.0 - (depth * 0.34 * stitch);
        roughDelta += depth * (0.18 - (0.10 * fibre));
    } else if (pattern == EquipPatternWeave) {
        // Cloth: a warp and weft lattice far too fine to resolve on its own, so what actually reads
        // is the low-frequency fold field it rides on. Both are here; the lattice survives as a
        // texture when the figure is close and averages into the fold value when it is not.
        float warp = 0.5 + (0.5 * sin(uv.x * 26.0));
        float weft = 0.5 + (0.5 * sin(uv.y * 26.0));
        float lattice = max(warp, weft);
        albedo *= 1.0 - (depth * 0.08 * (1.0 - lattice));
        float folds = dreamEquipNoise((lp * vec3(4.5, 3.0, 4.5)) + vec3(seed * 2.0));
        albedo *= 1.0 - (depth * 0.30 * (1.0 - folds));
        roughDelta += depth * 0.10;
    } else if (pattern == EquipPatternBone) {
        // Bone: striations down the length, and pitting at the ends where it is worked.
        float striae = 0.5 + (0.5 * sin((lp.y * 19.0) + (dreamEquipNoise(lp * 6.0) * 6.0)));
        albedo *= 1.0 - (depth * 0.20 * (1.0 - striae));
        float pits = dreamEquipNoise((lp * 13.0) + vec3(seed * 9.0));
        albedo *= 1.0 - (depth * 0.22 * smoothstep(0.58, 1.0, 1.0 - pits));
        roughDelta += depth * 0.14;
    } else if (pattern == EquipPatternWood) {
        // Wood: long grain along the haft with the odd knot, and a darker line where the grain runs
        // out. A haft is often the longest thing on screen, so its grain is worth the instructions.
        float grain = dreamEquipStreak(lp, vec3(20.0, 2.5, 20.0), seed);
        albedo *= 1.0 - (depth * 0.24 * (1.0 - grain));
        float knots = dreamEquipNoise((lp * vec3(3.5, 1.6, 3.5)) + vec3(seed * 4.0));
        albedo *= 1.0 - (depth * 0.26 * smoothstep(0.74, 1.0, knots));
        roughDelta += depth * 0.16;
    } else if (pattern == EquipPatternRune) {
        // The glowing materials: banded filigree across the piece, dark between the bands. The glow
        // itself is the material emissive, added after exposure; this only carves the dark away
        // from it so the light reads as lines rather than as a solid block of colour.
        float bands = 0.5 + (0.5 * sin((lp.y * 9.0) + (lp.x * 4.0) + (seed * 3.0)));
        albedo *= 1.0 - (depth * 0.34 * smoothstep(0.35, 0.95, 1.0 - bands));
        roughDelta -= depth * 0.10;
    }

    // No extra border term here: the plate groove and the leather stitch already own the border,
    // and a second one stacked on top of them is what turned the first pass muddy rather than
    // detailed. The floor is deliberately high - a pattern is a MODULATION, and anything that can
    // halve a piece's albedo is competing with the material palette instead of decorating it.
    return clamp(albedo, 0.58, 1.0);
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

        // Surface grain, the base layer every material gets: broad dents and soot. Keyed on the
        // model-space position scaled to metres, so it is attached to the piece.
        vec3 detailPos = localPos * max(uEquipDetail.y, 0.001);
        float wear = dreamEquipGrain(detailPos + vec3(uEquipDetail.w * 17.0));
        // ONE-SIDED on purpose. `lit` is linear in the albedo, so an upward lobe here is exactly an
        // albedo multiplier, and it would lift a piece past EquipmentTint.MaxAlbedoChannel - the
        // ceiling the CPU clamp is documented to guarantee - straight back into a blown-out patch.
        // Wear takes light away; it never adds any.
        float wearAlbedo = mix(1.0 - 0.20 * grain, 1.0, wear);
        // `lit` above is albedo * max(G, floor) and is LINEAR in the albedo, so scaling it here is
        // exactly the same thing as modulating the albedo before the floor clamp - and because it
        // scales both terms, the floor still holds and a dark piece can never go black.
        lit *= wearAlbedo;
        // A worn patch scatters wider: the hollows of the grain (low wear) are the rough ones.
        rough = clamp(rough + grain * 0.30 * (1.0 - wear), EquipMinRoughness, 1.0);

        // The material's own surface ON TOP of the grain: panel seams and rivets on plate, fibre
        // and stitching on leather, weave and folds on cloth, striations on bone, grain on wood,
        // filigree on the glowing materials. This is the layer that stops a part reading as one
        // flat value, which is the whole complaint the sculpting alone did not answer.
        float patternRough;
        float patternAlbedo = dreamEquipPattern(
            detailPos, texCoord0, int(uEquipDetail.x + 0.5), uEquipDetail.z, uEquipDetail.w, patternRough);
        lit *= patternAlbedo;
        rough = clamp(rough + patternRough, EquipMinRoughness, 1.0);

        vec3 v = normalize(uEquipEye - fragPos);
        float a = max(rough, EquipMinRoughness);
        float shin = clamp(2.0 / (a * a * a * a) - 2.0, 2.0, EquipMaxShininess);
        // Metal reflects its own colour (the albedo is bound to 0.42 a channel, so it is scaled back
        // up toward a real metal reflectance); a dielectric reflects a neutral 4%.
        vec3 f0 = mix(vec3(0.04), baseColor.rgb * 2.2, metallic);

        // Sun, moon and every point light. The dominant light's lobe (the one the map was rendered
        // from: the sun by day, the moon by night) takes the shadow map, so plate inside a doorway does
        // not glint; the other lobe gets exactly 1.0. The point lobe takes the fire shadows inside dreamPointSpecular.
        float mapV = dreamSunVisibility(fragPos, n);
        bool sunMap = dreamShadowMapIsSun();
        vec3 spec = dreamEquipLobe(n, v, uSunDirection.xyz, shin, f0)
            * uSunDirection.w * uSunColor.rgb * (sunMap ? mapV : 1.0);
        spec += dreamEquipLobe(n, v, uMoonDirection.xyz, shin, f0) * uMoonDirection.w * uMoonColor.rgb * (sunMap ? 1.0 : mapV);
        spec += dreamPointSpecular(n, fragPos, v, shin, f0);

        // Fresnel edge sheen: the sky caught on a rim. Metal takes most of it, leather a little.
        // Back faces again: with culling off they carry the front normal, so dot(n, v) is negative
        // and the rim term would saturate to a full sheen over the whole hidden face. Depth hides
        // them while every shape is closed, but an open shell would light up. Clamp it to zero.
        float rim = dot(n, v) <= 0.0 ? 0.0 : pow(1.0 - dot(n, v), 5.0);
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
