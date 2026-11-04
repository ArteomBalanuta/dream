// The boundary mist wall, pulled in with `#include "boundaryMist.glsl"` AFTER `#include "lighting.glsl"`
// (it lights the smoke with dreamAmbient/dreamDirect; includes are one level only, so no #include
// and no #version here). The block mirrors Rendering/Mist/BoundaryMistFrame.cs member for member
// (std140, 3 x vec4 = 48 bytes) and is bound to binding point 2 after link. BoundaryMistBuffer
// uploads it once per frame; the view-model range has strength 0.
//
// Every surface mixes toward smoke by an opacity computed from its OWN world XZ: 1 on and past the
// map edge, falling to 0 over uMistParams.w metres inside it, with the inner border churned by
// noise. No depth is read, so terrain side faces, props and actors past the edge vanish exactly.
// Pixels no surface covers are shaded by the void pass (boundaryVoidFragment.glsl) from the same
// functions, so the two meet with no seam. Mirrors Rendering/Mist/BoundaryMistField.cs.
//
// uMistBounds: minX, minZ, maxX (map width), maxZ (map depth).
// uMistParams: x strength (1 on, 0 off or view model), y time in seconds (wrapped at 600),
//              z floor top y, w ramp start (metres inside the edge where the mist ends).
// uMistView:   xyz unit camera front, w 0.
layout(std140) uniform BoundaryMist {
    vec4 uMistBounds; vec4 uMistParams; vec4 uMistView;
};

const float MistNoiseAmp = 0.35;   // BoundaryMistField.NoiseAmp
const float MistNoiseFade = 0.5;   // BoundaryMistField.NoiseFadeDistance
const float MistGamma = 3.0;       // BoundaryMistField.Gamma

// Hash-based value noise, no texture. The lattice shifts below move by whole cells every 600 s
// (the time wrap), so the wrap is invisible: lattice offset per second times 600 is an integer.
float dreamMistHash(vec2 p)
{
    vec3 p3 = fract(vec3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return fract((p3.x + p3.y) * p3.z);
}

float dreamMistValueNoise(vec2 q)
{
    vec2 cell = floor(q);
    vec2 f = fract(q);
    f = f * f * (3.0 - 2.0 * f);
    float a = dreamMistHash(cell);
    float b = dreamMistHash(cell + vec2(1.0, 0.0));
    float c = dreamMistHash(cell + vec2(0.0, 1.0));
    float d = dreamMistHash(cell + vec2(1.0, 1.0));
    return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}

// Signed distance to the nearest map edge: positive inside, 0 on the edge, negative outside.
float dreamMistInset(vec3 p)
{
    return min(min(p.x - uMistBounds.x, uMistBounds.z - p.x), min(p.z - uMistBounds.y, uMistBounds.w - p.z));
}

// The slow noise that moves the wall's inner border: one octave drifting at 0.25 m/s.
float dreamMistEdgeNoise(vec2 xz)
{
    return dreamMistValueNoise(xz * 0.9 + vec2(0.8, 0.6) * (0.225 * uMistParams.y) + 5.0);
}

// 0 where the mist is absent, uMistParams.x (1) on and past the edge.
float dreamBoundaryOpacity(vec3 p)
{
    if (uMistParams.x <= 0.0) {
        return 0.0;
    }

    float d = dreamMistInset(p);
    if (d <= 0.0) {
        return uMistParams.x;
    }

    if (d >= uMistParams.w + MistNoiseAmp) {
        return 0.0;
    }

    float n = dreamMistEdgeNoise(p.xz);
    float shifted = d + MistNoiseAmp * (2.0 * n - 1.0) * clamp(d / MistNoiseFade, 0.0, 1.0);
    return pow(clamp(1.0 - shifted / uMistParams.w, 0.0, 1.0), MistGamma) * uMistParams.x;
}

// The point where the view ray through p meets the floor plane. Sampling the smoke noise there
// makes a wall fragment and the void pixel next to it read one noise field. A level or upward
// view has no floor to follow and keeps p.
vec3 dreamMistFloorPoint(vec3 p)
{
    float dy = uMistView.y;
    if (dy > -0.05) {
        return p;
    }

    float t = clamp((uMistParams.z - p.y) / dy, -20.0, 20.0);
    return p + uMistView.xyz * t;
}

// Three drifting octaves: two advected along the wind, one rising with time (the boil).
float dreamMistSmokeNoise(vec2 xz)
{
    float t = uMistParams.y;
    float o1 = dreamMistValueNoise(xz * 0.5 + vec2(0.8, 0.6) * (0.125 * t));
    float o2 = dreamMistValueNoise(xz * 1.0 + vec2(-0.6, 0.8) * (0.325 * t) + 17.0);
    float o3 = dreamMistValueNoise(vec2(dot(xz, vec2(0.6, 0.35)), t * 0.4) + 41.0);
    return smoothstep(0.22, 0.78, 0.5 * o1 + 0.32 * o2 + 0.18 * o3);
}

// The smoke's colour at world point p, lit as an up-facing surface: brighter churn in the first
// 2 m past the edge, darker and quieter deeper. No point lights.
vec3 dreamBoundarySmoke(vec3 p)
{
    vec3 f = dreamMistFloorPoint(p);
    float n = dreamMistSmokeNoise(f.xz);
    float beyond = max(-dreamMistInset(f), 0.0);
    float churn = n * mix(1.0, 0.35, smoothstep(2.0, 10.0, beyond));
    vec3 albedo = mix(vec3(0.11, 0.11, 0.13), vec3(0.38, 0.38, 0.42), churn);
    vec3 up = vec3(0.0, 1.0, 0.0);
    return albedo * (dreamAmbient(up) + 0.6 * dreamDirect(up)) * uLightingParams.x;
}

// Mixes a lit, fogged colour toward the smoke by the opacity at p. The noise runs only where the
// opacity is above zero (a dynamic branch).
vec3 dreamApplyBoundaryMist(vec3 c, vec3 p)
{
    float b = dreamBoundaryOpacity(p);
    return b > 0.0 ? mix(c, dreamBoundarySmoke(p), b) : c;
}
