// pointLights.glsl - `#include "pointLights.glsl"` after lighting.glsl (one level, no #version).
// Mirrors Rendering/Lighting/LightList.cs (std140, 528 B, binding LightList.BindingPoint = 1).
// World range: world-space positions. View-model range: eye-space positions (view model draws with view = identity).
layout(std140) uniform LightList {
    ivec4 uPointLightCount;               // x = active lights, 0..16
    vec4 uPointLightPositionRadius[16];
    vec4 uPointLightColor[16];
};

vec3 dreamPointLights(vec3 n, vec3 fragPos) {
    vec3 sum = vec3(0.0);
    for (int i = 0; i < uPointLightCount.x; ++i) {
        vec3 toLight = uPointLightPositionRadius[i].xyz - fragPos;
        float r = uPointLightPositionRadius[i].w;
        float d2 = dot(toLight, toLight);
        float f = clamp(1.0 - d2 / (r * r), 0.0, 1.0);
        f *= f;
        float lambert = max(dot(n, toLight * inversesqrt(max(d2, 1e-4))), 0.0);
        sum += uPointLightColor[i].rgb * (f * (0.25 + 0.75 * lambert)); // 0.25 wrap keeps faces readable
    }
    return sum;
}
