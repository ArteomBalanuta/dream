// pointLights.glsl - `#include "pointLights.glsl"` after lighting.glsl and fireShadows.glsl (one level, no #version).
// Mirrors Rendering/Lighting/LightList.cs (std140, 528 B, binding LightList.BindingPoint = 1).
// World range: world-space positions. View-model range: eye-space positions (view model draws with view = identity).
layout(std140) uniform LightList {
    ivec4 uPointLightCount;               // x = active lights, 0..16
    vec4 uPointLightPositionRadius[16];
    vec4 uPointLightColor[16];
};

vec3 dreamPointLights(vec3 n, vec3 fragPos) {
    vec3 sum = vec3(0.0);
    int count = min(uPointLightCount.x, 16); // never index past the array, whatever the buffer says
    for (int i = 0; i < count; ++i) {
        vec3 toLight = uPointLightPositionRadius[i].xyz - fragPos;
        float r = uPointLightPositionRadius[i].w;
        float d2 = dot(toLight, toLight);
        float s = d2 / (r * r);
        float f = clamp(1.0 - s, 0.0, 1.0);
        f = f * f / (1.0 + 1.5 * s); // windowed to 0 at r; the 1/(1+1.5s) term keeps the core and thins the middle, so a pool punches
        float lambert = max(dot(n, toLight * inversesqrt(max(d2, 1e-4))), 0.0);
        // The fire shadow multiplies the lit term of a casting light (fireShadows.glsl); 1.0, with no fetch, for every other
        // light and whenever no fire casts, so a build with shadows off sums exactly what it summed before.
        float visibility = f > 0.0 ? dreamFireVisibility(i, fragPos, n) : 1.0;
        sum += uPointLightColor[i].rgb * (f * (0.25 + 0.75 * lambert)) * visibility; // 0.25 wrap keeps faces readable
    }
    return sum;
}

// The same lights, the same window and the same fire shadow as dreamPointLights above, with a
// Blinn-Phong lobe instead of the lambert term: a torch or a brazier glinting off a pauldron.
// Only the equipment path in enemyFragment.glsl calls it (gated on uEquipSurface, zero everywhere
// else); it is dead code in every other program that includes this file, and it reads nothing but
// the LightList block and dreamFireVisibility, which both of them already have.
//   n         unit surface normal
//   viewDir   unit vector from the fragment TOWARD the eye
//   shininess Blinn-Phong exponent, from roughness
//   f0        normal-incidence reflectance (4% for a dielectric, the albedo for metal)
vec3 dreamPointSpecular(vec3 n, vec3 fragPos, vec3 viewDir, float shininess, vec3 f0) {
    vec3 sum = vec3(0.0);
    int count = min(uPointLightCount.x, 16); // never index past the array, whatever the buffer says
    for (int i = 0; i < count; ++i) {
        vec3 toLight = uPointLightPositionRadius[i].xyz - fragPos;
        float r = uPointLightPositionRadius[i].w;
        float d2 = dot(toLight, toLight);
        float sd = d2 / (r * r);
        float f = clamp(1.0 - sd, 0.0, 1.0);
        f = f * f / (1.0 + 1.5 * sd); // the same window as the diffuse loop, so a highlight dies where its pool does
        if (f <= 0.0) { continue; }
        vec3 l = toLight * inversesqrt(max(d2, 1e-4));
        float ndl = max(dot(n, l), 0.0);
        if (ndl <= 0.0) { continue; } // a light behind the surface makes no highlight
        vec3 h = normalize(l + viewDir);
        float spec = pow(max(dot(n, h), 0.0), shininess);
        vec3 fresnel = f0 + (vec3(1.0) - f0) * pow(1.0 - max(dot(h, viewDir), 0.0), 5.0);
        sum += uPointLightColor[i].rgb * fresnel * (f * ndl * spec) * dreamFireVisibility(i, fragPos, n);
    }
    return sum;
}
