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
#include "pointLights.glsl"
// The boundary mist wall: the final colour is mixed toward smoke at and near the map edge.
#include "boundaryMist.glsl"

#ifdef DREAM_TERRAIN
// The terrain program (Shader.TerrainDefine): this file compiled a third time. The terrain tile
// repeats once per 1 m cell, so with uTerrainVariation = 1 the tile is sampled stochastically (two
// world-keyed variants blended along their relief height, tile alpha) and a world-space macro
// variation is applied. With 0 it is one plain sample, exactly the shared cube look. There is no
// MASK variant: terrain is opaque and writes alpha 1.
#ifdef WORLD_ALPHA_MASK
#error The terrain program has no alpha-mask variant.
#endif

uniform float uTerrainVariation;

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
vec2 dreamTerrainPlane(vec3 p, vec3 n)
{
    if (abs(n.y) > 0.5) {
        return p.xz;
    }
    return abs(n.x) > abs(n.z) ? p.zy : p.xy;
}

// One tile variant, keyed by an integer id. Top faces get an offset, a quarter turn and a mirror;
// side faces an offset and an x mirror only. The uv and its derivatives are transformed together
// so textureGrad keeps the mip level.
vec4 dreamTerrainVariant(float id, vec2 uv, vec2 dx, vec2 dy, bool top)
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
    vec2 t = m * (uv - 0.5) + 0.5 + offset;
    return textureGrad(material.baseColorTexture, t, m * dx, m * dy);
}

// uv is texCoord0 and dx/dy its screen derivatives, taken by main() before any branch.
vec3 dreamTerrainAlbedo(vec2 uv, vec2 dx, vec2 dy, vec3 n, vec3 worldPos, out float alpha)
{
    vec2 plane = dreamTerrainPlane(worldPos, n);
    bool top = abs(n.y) > 0.5;

    float k = dreamValueNoise(plane * 0.45) * 8.0;
    float i = floor(k);
    float f = k - i;
    vec4 ca = dreamTerrainVariant(i, uv, dx, dy, top);
    vec4 cb = dreamTerrainVariant(i + 1.0, uv, dx, dy, top);
    // The relief height (tile alpha) decides the seam: the higher crest wins. The bias fades to 0
    // at f = 0 and 1, where the variant pair changes, so the blend stays continuous. Equal alpha
    // (old tiles, 255 everywhere) is a plain smooth blend.
    float w = smoothstep(0.3, 0.7, f + (cb.a - ca.a) * 0.6 * 4.0 * f * (1.0 - f));
    vec4 c = mix(ca, cb, w);
    alpha = c.a;

    // World-space macro variation: two octaves of darker and lighter patches (mean 0.95) plus a
    // slight desaturated cool/warm drift from a third, broader octave.
    float m = 0.6 * dreamValueNoise(plane * 0.08) + 0.4 * dreamValueNoise(plane * 0.21);
    float t = dreamValueNoise(plane * 0.035 + 17.0);
    vec3 tint = mix(vec3(0.97, 0.99, 1.03), vec3(1.03, 1.0, 0.96), smoothstep(0.3, 0.7, t));
    return c.rgb * mix(0.82, 1.08, m) * tint;
}
#endif

void main()
{
#ifdef DREAM_TERRAIN
    // Screen derivatives first, in uniform control flow, before any branch.
    vec2 terrainDx = dFdx(texCoord0);
    vec2 terrainDy = dFdy(texCoord0);
#endif
    vec4 baseColor = material.baseColorFactor;

    if (material.hasBaseColorTexture) {
#ifdef DREAM_TERRAIN
        if (uTerrainVariation > 0.5) {
            float terrainAlpha;
            vec3 terrainRgb = dreamTerrainAlbedo(texCoord0, terrainDx, terrainDy, normalize(normal), fragPos, terrainAlpha);
            baseColor *= vec4(terrainRgb, terrainAlpha);
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
    vec3 lit = (dreamAmbient(n) + dreamDirect(n) + dreamPointLights(n, fragPos)) * baseColor.rgb;
    vec3 emissive = material.emissiveFactor;
    if (material.hasEmissiveTexture) {
        emissive *= texture(material.emissiveTexture, texCoord0 + material.emissiveUvScroll * uTime).rgb;
    }
    FragColor = vec4(dreamApplyBoundaryMist(dreamApplyFog(dreamExpose(lit) + emissive, fragPos), fragPos), baseColor.a);
}
