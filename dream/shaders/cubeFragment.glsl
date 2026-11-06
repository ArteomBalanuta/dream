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

// Define the Material structure
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

// Declare the uniform variable
uniform Material material;

#include "lighting.glsl"
// The fire shadow atlas lookup: pointLights.glsl calls dreamFireVisibility, so this goes above it.
#include "fireShadows.glsl"
#include "pointLights.glsl"
// The boundary mist wall: the final colour is mixed toward smoke at and near the map edge.
#include "boundaryMist.glsl"
#include "clouds.glsl"

#ifdef DREAM_TERRAIN
// The terrain program (Shader.TerrainDefine): this file compiled a third time. The terrain tile
// repeats once per 1 m cell, so with uTerrainVariation = 1 the tile is sampled stochastically (two
// world-keyed variants blended along their relief height, tile alpha) and a world-space macro
// variation is applied. With 0 it is one plain sample, exactly the shared cube look. On top faces
// within uTerrainBlendBand of a cell edge, different materials (the 8 tiles of uTerrainTiles, looked
// up through the world material map) interlock along their tile heights, and on every face a cheap
// bump normal is derived from the tile height (uTerrainRelief). There is no MASK variant: terrain is
// opaque and writes alpha 1.
#ifdef WORLD_ALPHA_MASK
#error The terrain program has no alpha-mask variant.
#endif

uniform float uTerrainVariation;
// The tile array layer of the bound material (GeometryMaterialId order); -1 = not blendable.
uniform int uTerrainLayer;
// 0/1 per pass: the cross-material height blend (variation, env switch and a ready tile array).
uniform float uTerrainBlend;
uniform float uTerrainBlendBand;  // metres from a cell edge
uniform float uTerrainBlendDepth; // height window in which the lower material shows through
uniform float uTerrainRelief;     // bump strength in metres, 0 = off
uniform sampler2DArray uTerrainTiles;
// World material map: 0 none, 1..8 = materialId + 1 exposed full top cell, 254 covered, 255 other.
uniform usampler3D uTerrainMaterialMap;
uniform vec3 uTerrainMapOrigin; // world cell of texel (0, 0, 0)
uniform vec3 uTerrainMapSize;   // texels; all 0 = no map

// Added to a layer's tile height so the lower materials fill the crevices of the higher ones:
// grass, dirt, snow and path 0; stone and rock +0.1; sand and mud -0.1.
const float kLayerHeightBias[8] = float[8](0.0, 0.0, 0.1, -0.1, -0.1, 0.0, 0.1, 0.0);

// pcg2d: two 32-bit hashes from one integer pair. Inputs are integer ids and lattice corners only.
uvec2 dreamTerrainHash2(uvec2 v)
{
    v = v * 1664525u + 1013904223u;
    v.x += v.y * 1664525u;
    v.y += v.x * 1664525u;
    v ^= v >> 16u;
    v.x += v.y * 1664525u;
    v.y += v.x * 1664525u;
    v ^= v >> 16u;
    return v;
}

float dreamTerrainLattice(vec2 cell)
{
    return float(dreamTerrainHash2(uvec2(ivec2(cell))).x >> 8u) * (1.0 / 16777216.0);
}

// Smooth value noise in 0..1 (mean 0.5): four lattice hashes, smoothstep weights.
float dreamValueNoise(vec2 p)
{
    vec2 c = floor(p);
    vec2 f = p - c;
    vec2 u = f * f * (3.0 - 2.0 * f);
    float a = dreamTerrainLattice(c);
    float b = dreamTerrainLattice(c + vec2(1.0, 0.0));
    float d = dreamTerrainLattice(c + vec2(0.0, 1.0));
    float e = dreamTerrainLattice(c + vec2(1.0, 1.0));
    return mix(mix(a, b, u.x), mix(d, e, u.x), u.y);
}

// The world-space plane the surface lies in: top faces use xz, the two side orientations zy and xy.
// Also selects the matching components of a world-space vector (a derivative of the position).
vec2 dreamTerrainPlane(vec3 p, vec3 n)
{
    if (abs(n.y) > 0.5) {
        return p.xz;
    }
    return abs(n.x) > abs(n.z) ? p.zy : p.xy;
}

// One tile variant's texture coordinate and gradients, keyed by an integer id and independent of
// the material layer, so every layer of a blend samples the same transform. Top faces get an
// offset, a quarter turn and a mirror; side faces an offset and an x mirror only. The uv and its
// derivatives are transformed together so textureGrad keeps the mip level.
struct DreamTerrainXform {
    vec2 t;
    vec2 dx;
    vec2 dy;
};

DreamTerrainXform dreamTerrainXform(float id, vec2 uv, vec2 dx, vec2 dy, bool top)
{
    uvec2 h = dreamTerrainHash2(uvec2(uint(int(id)), 7u));
    vec2 offset = vec2(float(h.y & 1023u), float((h.y >> 10u) & 1023u)) * (1.0 / 1024.0);
    float mirror = ((h.x >> 2u) & 1u) == 1u ? -1.0 : 1.0;
    float c = 1.0;
    float s = 0.0;
    if (top) {
        uint turn = h.x & 3u;
        c = turn == 0u ? 1.0 : (turn == 2u ? -1.0 : 0.0);
        s = turn == 1u ? 1.0 : (turn == 3u ? -1.0 : 0.0);
    }
    mat2 m = mat2(c, s, -s, c) * mat2(mirror, 0.0, 0.0, 1.0);
    return DreamTerrainXform(m * (uv - 0.5) + 0.5 + offset, m * dx, m * dy);
}

// The sample step: layer < 0 is the program's per-material texture, otherwise a layer of the tile array.
vec4 dreamTerrainVariant(DreamTerrainXform x, int layer)
{
    if (layer < 0) {
        return textureGrad(material.baseColorTexture, x.t, x.dx, x.dy);
    }
    return textureGrad(uTerrainTiles, vec3(x.t, float(layer)), x.dx, x.dy);
}

// One material's tile under the two stochastic variants (f = their fractional blend position):
// rgb is colour, a the relief height.
vec4 dreamTerrainLayerColor(DreamTerrainXform xa, DreamTerrainXform xb, float f, int layer)
{
    vec4 ca = dreamTerrainVariant(xa, layer);
    vec4 cb = dreamTerrainVariant(xb, layer);
    // The relief height (tile alpha) decides the seam: the higher crest wins. The bias fades to 0
    // at f = 0 and 1, where the variant pair changes, so the blend stays continuous. Equal alpha
    // (old tiles, 255 everywhere) is a plain smooth blend.
    float w = smoothstep(0.3, 0.7, f + (cb.a - ca.a) * 0.6 * 4.0 * f * (1.0 - f));
    return mix(ca, cb, w);
}

// The material map texel of a world cell (0 outside the map or with no map bound).
int dreamTerrainTexel(ivec3 cell)
{
    ivec3 i = cell - ivec3(floor(uTerrainMapOrigin + 0.5));
    ivec3 size = ivec3(floor(uTerrainMapSize + 0.5));
    if (any(lessThan(i, ivec3(0))) || any(greaterThanEqual(i, size))) {
        return 0;
    }
    return int(texelFetch(uTerrainMaterialMap, i, 0).r);
}

// Where the mass m of a missing corner slot goes: to its two edge neighbours in the 2x2 block, in
// proportion to their raw weights (wp/wq, counted only when okp/okq, i.e. present), or, if neither
// is present, to the diagonal slot. Returns (to p, to q, to diagonal).
vec3 dreamTerrainSplitMissing(float m, float wp, bool okp, float wq, bool okq)
{
    float sp = okp ? wp : 0.0;
    float sq = okq ? wq : 0.0;
    float t = sp + sq;
    if (t > 1e-6) {
        return m * vec3(sp, sq, 0.0) / t;
    }
    return vec3(0.0, 0.0, m);
}

// The four corner weights (own, X neighbour, Z neighbour, diagonal) of a top fragment. a is the
// per-axis weight of the neighbour (0 at the band boundary, 0.5 on the cell edge); px/pz/pd say
// whether the X, Z and diagonal cells are present (own always is). The weights depend only on the
// four cells of the block and the position, not on which of them the fragment is in: a missing
// cell's weight goes to its present edge neighbours in proportion to their weights, so both cells
// sharing an edge compute the same weight per material, and with a = 0 the result is own only.
vec4 dreamTerrainCornerWeights(vec2 a, bool px, bool pz, bool pd)
{
    float r0 = (1.0 - a.x) * (1.0 - a.y);
    float r1 = a.x * (1.0 - a.y);
    float r2 = (1.0 - a.x) * a.y;
    float r3 = a.x * a.y;
    float w0 = r0;
    float w1 = px ? r1 : 0.0;
    float w2 = pz ? r2 : 0.0;
    float w3 = pd ? r3 : 0.0;
    if (!px) {
        // X slot: edge neighbours own and diagonal, diagonal-of-it is the Z slot.
        vec3 g = dreamTerrainSplitMissing(r1, r0, true, r3, pd);
        w0 += g.x;
        w3 += g.y;
        w2 += g.z;
    }
    if (!pz) {
        vec3 g = dreamTerrainSplitMissing(r2, r0, true, r3, pd);
        w0 += g.x;
        w3 += g.y;
        w1 += g.z;
    }
    if (!pd) {
        // Diagonal slot: edge neighbours Z and X, diagonal-of-it is own.
        vec3 g = dreamTerrainSplitMissing(r3, r2, pz, r1, px);
        w2 += g.x;
        w1 += g.y;
        w0 += g.z;
    }
    return vec4(w0, w1, w2, w3);
}

// uv is texCoord0 and dx/dy its screen derivatives, taken by main() before any branch. height is the
// relief height (tile alpha, blended with the neighbouring materials where they blend).
vec3 dreamTerrainAlbedo(vec2 uv, vec2 dx, vec2 dy, vec3 n, vec3 worldPos, out float height)
{
    vec2 plane = dreamTerrainPlane(worldPos, n);
    bool isTop = abs(n.y) > 0.5;

    float k = dreamValueNoise(plane * 0.45) * 8.0;
    float i = floor(k);
    float f = k - i;
    DreamTerrainXform xa = dreamTerrainXform(i, uv, dx, dy, isTop);
    DreamTerrainXform xb = dreamTerrainXform(i + 1.0, uv, dx, dy, isTop);

    vec4 c = vec4(0.0);
    bool blended = false;

    // Cross-material height blend: top faces of a full exposed cell whose map texel is this
    // material (so fallback and dynamic cubes, and a stale or missing map, never blend). The quad
    // origin is an integer cell corner (uv = position - corner). Every static top quad is exactly one
    // unit cell (the mesh builder does not merge faces, and the map holds only unit cells), so the
    // cell is the corner's cell and l is uv clamped into it. The clamp matters: helper invocations
    // of a 2x2 quad extrapolate uv past the face edge, and must evaluate the same cell's edge value
    // (not the neighbour's) or their screen derivatives would spike into a line along the seam.
    if (uTerrainBlend > 0.5 && uTerrainLayer >= 0 && n.y > 0.5 && uTerrainMapSize.x > 0.5) {
        vec2 corner = worldPos.xz - uv;
        vec2 cornerCell = floor(corner + 0.5);
        vec2 l = clamp(uv, 0.0, 1.0);
        ivec3 cell = ivec3(int(cornerCell.x), int(floor(worldPos.y + 0.5)) - 1, int(cornerCell.y));
        // The top height must be an integer too: a carved sub-cell top (or any partial height) sits
        // between map layers and never blends. Plain arithmetic, no derivative in this condition.
        bool integerTop = abs(worldPos.y - floor(worldPos.y + 0.5)) < 0.01;
        if (integerTop && all(lessThan(abs(corner - cornerCell), vec2(0.01))) && dreamTerrainTexel(cell) == uTerrainLayer + 1) {
            float depth = max(uTerrainBlendDepth, 0.01);
            vec2 e = min(l, 1.0 - l);
            vec2 a = 0.5 * (1.0 - clamp(e / max(uTerrainBlendBand, 0.001), 0.0, 1.0));
            ivec2 dir = ivec2(l.x >= 0.5 ? 1 : -1, l.y >= 0.5 ? 1 : -1);

            // Slots: own, X neighbour, Z neighbour, diagonal. A neighbour that is not an exposed
            // full top cell (texel outside 1..8) is missing; its weight is handed on by
            // dreamTerrainCornerWeights, identically from every cell around the same corner.
            int tx = a.x > 0.0 ? dreamTerrainTexel(cell + ivec3(dir.x, 0, 0)) : 0;
            int tz = a.y > 0.0 ? dreamTerrainTexel(cell + ivec3(0, 0, dir.y)) : 0;
            int td = (a.x > 0.0 && a.y > 0.0) ? dreamTerrainTexel(cell + ivec3(dir.x, 0, dir.y)) : 0;
            bool px = tx >= 1 && tx <= 8;
            bool pz = tz >= 1 && tz <= 8;
            bool pd = td >= 1 && td <= 8;
            int lay[4];
            float wt[4];
            lay[0] = uTerrainLayer;
            lay[1] = px ? tx - 1 : uTerrainLayer;
            lay[2] = pz ? tz - 1 : uTerrainLayer;
            lay[3] = pd ? td - 1 : uTerrainLayer;
            vec4 cw = dreamTerrainCornerWeights(a, px, pz, pd);
            wt[0] = cw.x;
            wt[1] = cw.y;
            wt[2] = cw.z;
            wt[3] = cw.w;
            // Equal layers merge into the first slot that has them, so no layer is fetched twice.
            for (int s = 1; s < 4; s++) {
                for (int j = 0; j < s; j++) {
                    if (wt[j] > 0.0 && lay[j] == lay[s]) {
                        wt[j] += wt[s];
                        wt[s] = 0.0;
                        break;
                    }
                }
            }

            vec3 col[4];
            float hh[4];
            float av[4];
            float best = -1000.0;
            for (int s = 0; s < 4; s++) {
                col[s] = vec3(0.0);
                hh[s] = 0.0;
                av[s] = -1000.0;
                if (wt[s] > 0.0) {
                    int layer = clamp(lay[s], 0, 7);
                    vec4 lc = dreamTerrainLayerColor(xa, xb, f, layer);
                    col[s] = lc.rgb;
                    hh[s] = lc.a;
                    // The higher tile (plus its material bias and edge weight) covers the lower one;
                    // only layers within `depth` of the top contribute.
                    av[s] = lc.a + kLayerHeightBias[layer] + (1.0 + depth) * wt[s];
                    best = max(best, av[s]);
                }
            }
            float floorA = best - depth;
            float sum = 0.0;
            vec3 rgb = vec3(0.0);
            float hgt = 0.0;
            for (int s = 0; s < 4; s++) {
                if (wt[s] > 0.0) {
                    float kk = max(av[s] - floorA, 0.0);
                    sum += kk;
                    rgb += col[s] * kk;
                    hgt += hh[s] * kk;
                }
            }
            float inv = 1.0 / max(sum, 1e-4);
            c = vec4(rgb * inv, hgt * inv);
            blended = true;
        }
    }

    if (!blended) {
        c = dreamTerrainLayerColor(xa, xb, f, -1);
    }
    height = c.a;

    // World-space macro variation: two octaves of darker and lighter patches (mean 0.95) plus a
    // slight desaturated cool/warm drift from a third, broader octave. Evaluated on x/z with a
    // small y term on every face, so the brightness does not step at a cliff lip.
    vec2 macroP = worldPos.xz + worldPos.y * vec2(0.31, 0.17);
    float m = 0.6 * dreamValueNoise(macroP * 0.08) + 0.4 * dreamValueNoise(macroP * 0.21);
    float t = dreamValueNoise(macroP * 0.035 + 17.0);
    vec3 tint = mix(vec3(0.97, 0.99, 1.03), vec3(1.03, 1.0, 0.96), smoothstep(0.3, 0.7, t));
    return c.rgb * mix(0.82, 1.08, m) * tint;
}

// Tilts the geometric normal n by the gradient of the relief height. hx/hy are the screen
// derivatives of the height and posDx/posDy those of the world position, all taken by main() in
// uniform control flow. Solves the 2x2 for the height gradient in the surface plane, clamped to
// 64 per metre. Plain (variation 0) and relief 0 return n untouched.
vec3 dreamTerrainBump(vec3 n, float hx, float hy, vec3 posDx, vec3 posDy)
{
    float strength = uTerrainVariation > 0.5 ? uTerrainRelief : 0.0;
    if (strength <= 0.0) {
        return n;
    }
    vec2 pdx = dreamTerrainPlane(posDx, n);
    vec2 pdy = dreamTerrainPlane(posDy, n);
    float det = pdx.x * pdy.y - pdx.y * pdy.x;
    if (abs(det) < 1e-8) {
        return n;
    }
    vec2 g = vec2(hx * pdy.y - hy * pdx.y, pdx.x * hy - pdy.x * hx) / det;
    g *= min(1.0, 64.0 / max(length(g), 1e-4));
    vec3 tangent = vec3(1.0, 0.0, 0.0);
    vec3 bitangent = vec3(0.0, 0.0, 1.0);
    if (abs(n.y) <= 0.5) {
        if (abs(n.x) > abs(n.z)) {
            tangent = vec3(0.0, 0.0, 1.0);
            bitangent = vec3(0.0, 1.0, 0.0);
        } else {
            bitangent = vec3(0.0, 1.0, 0.0);
        }
    }
    return normalize(n - strength * (g.x * tangent + g.y * bitangent));
}
#endif

void main()
{
#ifdef DREAM_TERRAIN
    // Screen derivatives first, in uniform control flow, before any branch.
    vec2 terrainDx = dFdx(texCoord0);
    vec2 terrainDy = dFdy(texCoord0);
    vec3 terrainPosDx = dFdx(fragPos);
    vec3 terrainPosDy = dFdy(fragPos);
    float terrainHeight = 0.5;
#endif
    vec4 baseColor = material.baseColorFactor;

    if (material.hasBaseColorTexture) {
#ifdef DREAM_TERRAIN
        if (uTerrainVariation > 0.5) {
            terrainHeight = 0.5;
            // Blended neighbour tiles are tinted by this material's baseColorFactor too. That is
            // white for every blendable material (layers 0..7); only Lamp is tinted and it never
            // blends (TerrainBlendCornerWeightTests).
            vec3 terrainRgb = dreamTerrainAlbedo(texCoord0, terrainDx, terrainDy, normalize(normal), fragPos, terrainHeight);
            baseColor *= vec4(terrainRgb, terrainHeight);
        } else {
            baseColor *= texture(material.baseColorTexture, texCoord0);
        }
#endif
#ifndef DREAM_TERRAIN
        baseColor *= texture(material.baseColorTexture, texCoord0);
#endif
    }
#ifdef DREAM_TERRAIN
    // Terrain is opaque; the tile alpha is relief height, not coverage.
    baseColor.a = 1.0;
    // The height's screen derivatives, after the material branches (uniform control flow).
    float terrainHx = dFdx(terrainHeight);
    float terrainHy = dFdy(terrainHeight);
#endif

#ifdef WORLD_ALPHA_MASK
    // glTF MASK: fragments below the cutoff are not drawn.
    if (baseColor.a < material.alphaCutoff) {
        discard;
    }
#endif

    // Frame lighting (lighting.glsl): hemisphere ambient plus sun and moon, plus the frame's point
    // lights (pointLights.glsl; none means exactly the T1 look), then exposure and fog.
    vec3 n = normalize(normal);
#ifdef DREAM_TERRAIN
    // The bumped normal feeds the sun, moon and every point light below.
    n = dreamTerrainBump(n, terrainHx, terrainHy, terrainPosDx, terrainPosDy);
#endif
    vec3 lit = (dreamAmbient(n) + dreamDirectShaded(n, fragPos) + dreamPointLights(n, fragPos)) * baseColor.rgb;
    vec3 emissive = material.emissiveFactor;
    if (material.hasEmissiveTexture) {
        emissive *= texture(material.emissiveTexture, texCoord0 + material.emissiveUvScroll * uTime).rgb;
    }
    FragColor = vec4(dreamApplyBoundaryMist(dreamApplyFog(dreamExpose(lit) + emissive, fragPos), fragPos), baseColor.a);
}
