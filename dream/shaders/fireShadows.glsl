// fireShadows.glsl - `#include "fireShadows.glsl"` after lighting.glsl and BEFORE pointLights.glsl, which calls it
// (Shader.ResolveIncludes: one level only, so no #include or #version here; a function must be defined above its caller).
// Included only by cubeFragment.glsl and enemyFragment.glsl.
//
// Mirrors Rendering/Shadows/FireShadowList.cs member for member (std140, 25 x vec4 = 400 bytes, binding point
// FireShadowList.BindingPoint = 3; 0 is FrameLighting, 1 is LightList, 2 is BoundaryMistFrame).
// World range: the frame's casting fires in world space. View-model range: count 0 (its fragPos is in eye space, the
// atlas holds world-space depth), which makes dreamFireVisibility return 1.0.
//   uFireShadowCount.x    active slots 0..4
//   uFireShadowLight[s]   xyz flickered world position of the fire, w its index in the LightList block (as a float)
//   uFireShadowRange[s]   x near, y far (the SAME values the caster pass rendered with), z gutter-shrunk tile scale
//                         (TileSide - 2 * TileGutter) / TileSide, w strength (0 = no shadow)
//   uFireShadowTile[s*4+f] xy tile origin in atlas UV, zw tile size in atlas UV (ShadowMapTargets.TileOrigin: slot-major, row-major)
// Faces: 0 +X, 1 -X, 2 +Z, 3 -Z, each a perspective view down its axis with +Y up (Matrix4.LookAt) and the FOV widened so the
// 90 degree cone lands inside the tile minus its 4 texel gutter. The +-Y polar cones are deliberately unshadowed: the SAFE
// failure direction (over-lit, never black).
layout(std140) uniform FireShadowList {
    ivec4 uFireShadowCount;
    vec4 uFireShadowLight[4];
    vec4 uFireShadowRange[4];
    vec4 uFireShadowTile[16];
};

// Unit 6 (ShadowSettings.AtlasTextureUnit), set at link time. COMPARE_REF_TO_TEXTURE + LINEAR: every tap is a hardware 2x2 PCF.
uniform sampler2DShadow uFireShadowAtlas;

// Mirror ShadowSettings.TileGutter, GrazingCosineFloor, SlopeTanMax, NormalOffsetTexels, ConstantBiasWorld and
// FireShadowList.SlopeBiasTexels (pinned equal by FireShadowSamplingMirrorTests).
const float FireShadowGutterTexels = 4.0;
const float FireShadowGrazingFloor = 0.15;
const float FireShadowTanMax = 4.0;
const float FireShadowNormalOffsetTexels = 1.5;
const float FireShadowConstantBias = 0.006;
const float FireShadowSlopeTexels = 2.5;
const float FireShadowTapOffset = 1.5;   // a 3x3 grid at +-1.5 texel; with the hardware 2x2 each tap reaches one texel further

// 1.0 = fully lit, 0.0 = fully shadowed, of the point light `light` (its index in the LightList block) at fragPos with
// shading normal n. A literal 1.0 with no texture fetch when no fire casts (count 0: DREAM_SHADOWS=0, the view model, no fires).
float dreamFireVisibility(int light, vec3 fragPos, vec3 n)
{
    if (uFireShadowCount.x <= 0) { return 1.0; }
    int slot = -1;
    int count = min(uFireShadowCount.x, 4);
    for (int s = 0; s < count; ++s)
    {
        if (int(uFireShadowLight[s].w + 0.5) == light) { slot = s; break; }
    }
    if (slot < 0) { return 1.0; }
    vec4 range = uFireShadowRange[slot];
    if (range.w <= 0.0) { return 1.0; }

    vec3 d0 = fragPos - uFireShadowLight[slot].xyz;
    vec3 a0 = abs(d0);
    if (a0.y >= max(a0.x, a0.z)) { return 1.0; } // polar cone: unshadowed
    float major0 = max(a0.x, a0.z);
    float dist = length(d0);

    // One texel of the face at this distance, in world units: the cone covers 2 * major / scale across the tile.
    vec2 texel = 1.0 / vec2(textureSize(uFireShadowAtlas, 0));
    float tilePixels = uFireShadowTile[0].z / texel.x;
    float texelWorld = 2.0 * major0 / (tilePixels * range.z);

    // Grazing measure against the light: normal offset moves the lookup, the slope bias moves the depth.
    float ndl = abs(dot(n, d0)) / dist;
    float tanL = min(sqrt(max(1.0 - ndl * ndl, 0.0)) / max(ndl, FireShadowGrazingFloor), FireShadowTanMax);
    vec3 d = d0 + n * (FireShadowNormalOffsetTexels * texelWorld * tanL);
    vec3 a = abs(d);

    // Restricted cube-face selection. lat.x is the face's right axis (forward x up), lat.y is up.
    int face;
    float major;
    vec2 lat;
    float axisN;
    if (a.x > a.z)
    {
        face = d.x > 0.0 ? 0 : 1;
        major = a.x;
        lat = vec2(d.x > 0.0 ? d.z : -d.z, d.y);
        axisN = abs(n.x);
    }
    else
    {
        face = d.z > 0.0 ? 2 : 3;
        major = a.z;
        lat = vec2(d.z > 0.0 ? -d.x : d.x, d.y);
        axisN = abs(n.z);
    }

    // The depth the face stores varies by texelWorld * |n_lateral| / (n . ray / major) per texel of a tilted receiver.
    float slope = min(sqrt(max(1.0 - axisN * axisN, 0.0)) / max(ndl * dist / major0, FireShadowGrazingFloor), FireShadowTanMax);
    float bias = FireShadowConstantBias + FireShadowSlopeTexels * texelWorld * slope;

    // The perspective depth the caster pass wrote for a point `m` in front of the light along the face axis,
    // from the SAME near/far (range.xy): z_ndc = (f + n) / (f - n) - 2 f n / ((f - n) m), stored as 0.5 z_ndc + 0.5.
    float mRef = max(major - bias, range.x);
    float zn = (range.y + range.x) / (range.y - range.x) - (2.0 * range.y * range.x) / ((range.y - range.x) * mRef);
    float refDepth = clamp(0.5 * zn + 0.5, 0.0, 1.0);

    vec4 tile = uFireShadowTile[slot * 4 + face];
    vec2 uv = tile.xy + tile.zw * (lat / major * (0.5 * range.z) + 0.5);
    // Every tap stays inside the tile's inner rect, a gutter's width from its edge, so a kernel at a face edge can never
    // read the neighbouring tile.
    vec2 lo = tile.xy + FireShadowGutterTexels * texel;
    vec2 hi = tile.xy + tile.zw - FireShadowGutterTexels * texel;
    vec2 o = FireShadowTapOffset * texel;
    float sum = texture(uFireShadowAtlas, vec3(clamp(uv + vec2(-o.x, -o.y), lo, hi), refDepth))
              + texture(uFireShadowAtlas, vec3(clamp(uv + vec2( 0.0, -o.y), lo, hi), refDepth))
              + texture(uFireShadowAtlas, vec3(clamp(uv + vec2( o.x, -o.y), lo, hi), refDepth))
              + texture(uFireShadowAtlas, vec3(clamp(uv + vec2(-o.x,  0.0), lo, hi), refDepth))
              + texture(uFireShadowAtlas, vec3(clamp(uv + vec2( 0.0,  0.0), lo, hi), refDepth))
              + texture(uFireShadowAtlas, vec3(clamp(uv + vec2( o.x,  0.0), lo, hi), refDepth))
              + texture(uFireShadowAtlas, vec3(clamp(uv + vec2(-o.x,  o.y), lo, hi), refDepth))
              + texture(uFireShadowAtlas, vec3(clamp(uv + vec2( 0.0,  o.y), lo, hi), refDepth))
              + texture(uFireShadowAtlas, vec3(clamp(uv + vec2( o.x,  o.y), lo, hi), refDepth));
    return mix(1.0, (1.0 / 9.0) * sum, clamp(range.w, 0.0, 1.0));
}
