// Cloud cover shared by the world fragment shaders (cloud shadows, via dreamDirectShaded) and the sky
// (the cloud sheet). Pulled in with `#include "clouds.glsl"` AFTER `#include "lighting.glsl"`
// (Shader.ResolveIncludes: one level only, so no #include or #version here); it reads
// uCloudParams and uFrameTime from the FrameLighting block. Mirrored in C# by Rendering/Sky/CloudLayer.cs,
// which a test compares against the constants below.
// uCloudParams: x coverage 0..1, y softness 0..1, z shadow strength 0..1, w scroll speed (world units/s).
// uFrameTime.yz: the drift in noise cells, already reduced modulo CloudPeriodCells on the CPU (CloudLayer.ScrollCells),
// so no clock is wrapped here and the sheet never jumps. That reduction is only seamless because the lattice
// hash below wraps at the same period.
const float CloudPlaneY = 60.0;
const vec2 CloudDirection = vec2(0.8, 0.6);   // unit: the way the clouds drift
const float CloudCellsPerUnit = 0.008;        // base noise frequency, cells per world unit
const float CloudLacunarity = 2.0;
const float CloudGain = 0.5;
const float CloudAmplitudeSum = 1.875;        // 1 + 0.5 + 0.25 + 0.125 (4 octaves)
// NOTE: because of CloudContrast, uCloudParams.x (coverage) is NOT the literal fraction of sky covered:
// 0.35 covers roughly 30-35 % of the plane, but the mapping is a threshold on stretched noise, not a percentage.
const float CloudContrast = 2.0;            // fBm clusters near 0.5; stretch it so coverage is about the fraction covered
const float CloudSoftnessScale = 0.5;         // half-width of the threshold ramp at softness 1
const float CloudPeriodCells = 64.0;          // the lattice repeats every 64 cells at every octave (each octave stretch divides 64)

float dreamCloudHash(vec2 p)
{
    vec3 p3 = fract(vec3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return fract((p3.x + p3.y) * p3.z);
}

float dreamCloudValueNoise(vec2 p)
{
    vec2 cell = floor(p);
    vec2 f = fract(p);
    vec2 u = f * f * (3.0 - 2.0 * f);
    float a = dreamCloudHash(mod(cell, CloudPeriodCells));
    float b = dreamCloudHash(mod(cell + vec2(1.0, 0.0), CloudPeriodCells));
    float c = dreamCloudHash(mod(cell + vec2(0.0, 1.0), CloudPeriodCells));
    float d = dreamCloudHash(mod(cell + vec2(1.0, 1.0), CloudPeriodCells));
    return mix(mix(a, b, u.x), mix(c, d, u.x), u.y);
}

// 0 = clear, 1 = solid, for a point on the cloud plane (world XZ).
float dreamCloudCover(vec2 planeXZ)
{
    if (uCloudParams.x <= 0.0) { return 0.0; }
    vec2 p = planeXZ * CloudCellsPerUnit - uFrameTime.yz;
    float sum = 0.0;
    float amplitude = 1.0;
    for (int i = 0; i < 4; i++)
    {
        sum += amplitude * dreamCloudValueNoise(p);
        p = p * CloudLacunarity + vec2(17.0, 31.0);
        amplitude *= CloudGain;
    }
    float n = clamp((sum / CloudAmplitudeSum - 0.5) * CloudContrast + 0.5, 0.0, 1.0);
    float w = clamp(uCloudParams.y, 0.0, 1.0) * CloudSoftnessScale + 0.0005;
    float center = mix(1.0 + w, -w, clamp(uCloudParams.x, 0.0, 1.0));
    return smoothstep(center - w, center + w, n);
}

// dreamDirect with the sun term dimmed by the cloud shadow over this fragment. The ray from fragPos
// toward the sun is cut with the cloud plane and the cover there multiplies the SUN term only; the
// moon and dreamAmbient are untouched. With coverage 0 the mask is exactly 1.0 and this is
// bit-identical to dreamDirect (lighting.glsl), which stays the engine contract that
// tools/ui-assets/enginelook.py restates and that boundaryMist.glsl and warMistFragment.glsl call.
// (It lives here, not in lighting.glsl, because includes are one level deep: lighting.glsl cannot
// pull in clouds.glsl.)
//
// Why GrimLookHistogramTests' CPU mirror needs no cloud term: cloud shadow multiplies the SUN term
// only, and at that test's four hours (0, 20, 19, 5) sunIntensity is 0 or near 0 (SkyModel.cs), so
// the dimmed term is already zero. If daytime coverage is ever gated there, the mirror must gain it.
vec3 dreamDirectShaded(vec3 n, vec3 fragPos)
{
    // The sun attenuation is one named scalar on purpose: a shadow map can later multiply it in
    // (sunMask * sunShadow) without restructuring. dreamCloudCover itself makes no lighting decisions.
    float sunMask = 1.0;
    if (uCloudParams.x > 0.0 && uSunDirection.y > 0.02)
    {
        vec3 onPlane = fragPos + uSunDirection.xyz * ((CloudPlaneY - fragPos.y) / uSunDirection.y);
        sunMask = 1.0 - uCloudParams.z * dreamCloudCover(onPlane.xz);
    }
    // The map's visibility belongs to the DOMINANT light's term (the one the map was rendered from), the other
    // term gets exactly 1.0, so strength 0 leaves both terms bitwise unchanged. A select, not a mix.
    float v = dreamSunVisibility(fragPos, n);
    bool sunMap = dreamShadowMapIsSun();
    return max(dot(n, uSunDirection.xyz), 0.0) * uSunDirection.w * uSunColor.rgb * sunMask * (sunMap ? v : 1.0)
         + max(dot(n, uMoonDirection.xyz), 0.0) * uMoonDirection.w * uMoonColor.rgb * (sunMap ? 1.0 : v);
}
