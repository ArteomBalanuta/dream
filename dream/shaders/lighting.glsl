// Shared frame lighting for the world fragment shaders, pulled in with `#include "lighting.glsl"`
// (Shader.ResolveIncludes: one level only, so no #include or #version here). The block mirrors
// Rendering/Lighting/FrameLighting.cs member for member (std140, 11 x vec4 = 176 bytes) and is
// bound to FrameLighting.BindingPoint after link. FrameLightingBuffer uploads it once per frame:
// the world range holds world-space directions and the camera position; the view-model range
// holds eye-space directions with the camera at the origin.
// uCloudParams: x coverage, y softness, z shadow strength, w scroll speed (clouds.glsl).
// uFrameTime: x wrapped clock seconds, y global gust (wind in cubeVertex.glsl), zw 0.
// uLightingParams: x exposure, y actor brightness floor, z actor ambient scale (enemyFragment.glsl), w 0.
layout(std140) uniform FrameLighting {
    vec4 uSunDirection; vec4 uSunColor; vec4 uMoonDirection; vec4 uMoonColor;
    vec4 uAmbientSky; vec4 uAmbientGround; vec4 uFogColor; vec4 uCameraPosition; vec4 uLightingParams;
    vec4 uCloudParams; vec4 uFrameTime;
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
