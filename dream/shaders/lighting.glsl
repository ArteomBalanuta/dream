// Shared frame lighting for the world fragment shaders, pulled in with `#include "lighting.glsl"`
// (Shader.ResolveIncludes: one level only, so no #include or #version here). The block mirrors
// Rendering/Lighting/FrameLighting.cs member for member (std140, 16 x vec4 = 256 bytes) and is
// bound to FrameLighting.BindingPoint after link. FrameLightingBuffer uploads it once per frame:
// the world range holds world-space directions and the camera position; the view-model range
// holds eye-space directions with the camera at the origin.
// uCloudParams: x coverage, y softness, z shadow strength, w scroll speed (clouds.glsl).
// uFrameTime: x sway clock seconds (wind in cubeVertex.glsl; wrapped at 20 pi), yz cloud drift in noise cells (clouds.glsl), w 0.
// uSunShadowMatrix0..3: the four ROWS of the C# Matrix4 (world -> shadow texture space), see dreamSunShadowMatrix below.
// uSunShadowParams: x 1 / resolution, y constant bias / depth range, z normal offset in world units, w strength (0 = off).
// uCameraPosition is the FOG origin (the isometric focus), not the eye; the sky takes the real eye as its own uniform.
// uLightingParams: x exposure, y actor brightness floor, z actor ambient scale (enemyFragment.glsl), w 0.
layout(std140) uniform FrameLighting {
    vec4 uSunDirection; vec4 uSunColor; vec4 uMoonDirection; vec4 uMoonColor;
    vec4 uAmbientSky; vec4 uAmbientGround; vec4 uFogColor; vec4 uCameraPosition; vec4 uLightingParams;
    vec4 uCloudParams; vec4 uFrameTime;
    vec4 uSunShadowMatrix0; vec4 uSunShadowMatrix1; vec4 uSunShadowMatrix2; vec4 uSunShadowMatrix3; vec4 uSunShadowParams;
};
vec3 dreamAmbient(vec3 n) { return mix(uAmbientGround.rgb, uAmbientSky.rgb, n.y * 0.5 + 0.5); }
vec3 dreamDirect(vec3 n) {
    return max(dot(n, uSunDirection.xyz), 0.0) * uSunDirection.w * uSunColor.rgb
         + max(dot(n, uMoonDirection.xyz), 0.0) * uMoonDirection.w * uMoonColor.rgb;
}
vec3 dreamExpose(vec3 c) { return c * uLightingParams.x; }
float dreamFogTransmittance(vec3 fragPos) {
    float d = max(distance(fragPos, uCameraPosition.xyz) - uCameraPosition.w, 0.0);
    float k = uFogColor.w * d;
    return clamp(exp2(-k * k), 0.0, 1.0);
}
vec3 dreamApplyFog(vec3 c, vec3 fragPos) { return mix(uFogColor.rgb, c, dreamFogTransmittance(fragPos)); }

// ---- Sun/moon shadow map (docs/ledger/wave-shadow-maps.md). ----
// Unit 5 (ShadowSettings.SunTextureUnit), set at link time. COMPARE_REF_TO_TEXTURE + LINEAR: every tap is a hardware 2x2 PCF.
// The border is 1.0, so outside the volume reads fully lit.
uniform sampler2DShadow uSunShadowMap;

// OpenTK is row-vector (clip = p * M); GLSL is column-major (M * p). The four C# ROWS are uploaded as-is and used as
// the four GLSL COLUMNS, so M * p == p * M with no transpose (pinned by SunShadowLookupMirrorTests).
mat4 dreamSunShadowMatrix() { return mat4(uSunShadowMatrix0, uSunShadowMatrix1, uSunShadowMatrix2, uSunShadowMatrix3); }

// Mirrors ShadowSettings.GrazingCosineFloor, SlopeTanMax, SlopePerInverseResolution, SunTaps (pinned by a test).
const float SunShadowGrazingFloor = 0.15;
const float SunShadowTanMax = 4.0;
const float SunShadowSlopePerInvRes = 6826.667;
const int SunShadowTapsPerSide = 3;
const float SunShadowTapSpacing = 1.5;

// Compile-time switch: 0 until the shadow pass is wired (workstream D3). While it is 0 the function below is a bare
// `return 1.0` with no texture fetch and no use of the shadow members, so the frame is pixel-identical to a build with
// no shadow code at all.
#define DREAM_SUN_SHADOW_LIVE 0

// 1.0 = fully lit, 0.0 = fully shadowed, of the SUN term (the dominant light: sun, else moon, matching the map).
// TODO(D3): this is NOT yet multiplied into any lighting. dreamDirect stays textually unchanged (the engine contract
// enginelook.py restates), and the seam is dreamDirectShaded in clouds.glsl (workstream-D3-owned):
//     return max(dot(n, uSunDirection.xyz), 0.0) * uSunDirection.w * uSunColor.rgb * sunMask      // the sun line
// becomes `... * uSunColor.rgb * sunMask * dreamSunVisibility(fragPos, n)`, and DREAM_SUN_SHADOW_LIVE flips to 1.
float dreamSunVisibility(vec3 fragPos, vec3 n)
{
#if DREAM_SUN_SHADOW_LIVE
    if (uSunShadowParams.w <= 0.0) { return 1.0; }
    vec3 toLight = uSunDirection.w >= uMoonDirection.w ? uSunDirection.xyz : uMoonDirection.xyz;
    float ndl = max(dot(n, toLight), 0.0);
    float tanT = min(sqrt(max(1.0 - ndl * ndl, 0.0)) / max(ndl, SunShadowGrazingFloor), SunShadowTanMax);
    // Normal offset moves the lookup, not the depth. Bias = constant * (1 + slope/constant * tan): constant + slope-scaled,
    // the slope term proportional to the texel (texel = OrthoSide * uSunShadowParams.x), see ShadowSettings.SlopeBiasTexels.
    vec3 lookup = fragPos + n * (uSunShadowParams.z * tanT);
    vec3 tc = (dreamSunShadowMatrix() * vec4(lookup, 1.0)).xyz;
    float refDepth = tc.z - uSunShadowParams.y * (1.0 + SunShadowSlopePerInvRes * uSunShadowParams.x * tanT);
    float sum = 0.0;
    for (int j = 0; j < SunShadowTapsPerSide; j++)
    {
        for (int i = 0; i < SunShadowTapsPerSide; i++)
        {
            vec2 o = (vec2(float(i), float(j)) - 0.5 * float(SunShadowTapsPerSide - 1)) * (SunShadowTapSpacing * uSunShadowParams.x);
            sum += texture(uSunShadowMap, vec3(tc.xy + o, refDepth));
        }
    }
    return mix(1.0, sum / float(SunShadowTapsPerSide * SunShadowTapsPerSide), clamp(uSunShadowParams.w, 0.0, 1.0));
#else
    return 1.0;
#endif
}
