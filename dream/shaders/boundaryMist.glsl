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
// uMistParams: x strength (1 on, 0 off or view model), y time in seconds (wrapped at 6400),
//              z floor top y, w ramp start (metres inside the edge where the mist ends).
// uMistEye:    xyz the true camera position in world space (uCameraPosition is the focus in
//              isometric view), w 0.
layout(std140) uniform BoundaryMist {
    vec4 uMistBounds; vec4 uMistParams; vec4 uMistEye;
};

const float MistNoiseAmp = 0.35;   // BoundaryMistField.NoiseAmp
const float MistNoiseFade = 0.5;   // BoundaryMistField.NoiseFadeDistance
const float MistGamma = 3.0;       // BoundaryMistField.Gamma

// Hash-based value noise, no texture. The hash is periodic over 64 cells (BoundaryMistField.HashPeriod)
// and every lattice shift below moves by a whole multiple of 64 cells per time wrap (6400 s), so
// the wrap is invisible. BoundaryMistField holds the rates; a test checks rate x wrap.
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
    float a = dreamMistHash(mod(cell, 64.0));
    float b = dreamMistHash(mod(cell + vec2(1.0, 0.0), 64.0));
    float c = dreamMistHash(mod(cell + vec2(0.0, 1.0), 64.0));
    float d = dreamMistHash(mod(cell + vec2(1.0, 1.0), 64.0));
    return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}

// Signed distance to the nearest map edge: positive inside, 0 on the edge, negative outside.
float dreamMistInset(vec3 p)
{
    return min(min(p.x - uMistBounds.x, uMistBounds.z - p.x), min(p.z - uMistBounds.y, uMistBounds.w - p.z));
}

// The slow noise that moves the wall's inner border: one octave drifting at about 0.07 m/s.
float dreamMistEdgeNoise(vec2 xz)
{
    return dreamMistValueNoise(xz * 0.9 + vec2(0.06, 0.04) * uMistParams.y + 5.0);
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

// The point where the ray from the eye through p meets the floor plane. Sampling the smoke noise
// there makes a wall fragment and the void pixel next to it (whose ray is the same ray) read one
// noise field. A level or upward ray has no floor to follow and keeps p.
vec3 dreamMistFloorPoint(vec3 p)
{
    vec3 v = p - uMistEye.xyz;
    if (v.y > -0.02 * length(v)) {
        return p;
    }

    return p + v * clamp((uMistParams.z - p.y) / v.y, -1.0, 1.0);
}

// The smoke's palette: a near-black base, a body that wanders between cold indigo and bruised
// violet, a slightly lighter crest on the tongues, and a dim ember that smoulders in the crevices.
const vec3 MistBase = vec3(0.016, 0.012, 0.026);
const vec3 MistIndigo = vec3(0.042, 0.045, 0.105);
const vec3 MistBruise = vec3(0.090, 0.040, 0.105);
const vec3 MistCrest = vec3(0.165, 0.115, 0.215);
const vec3 MistEmber = vec3(0.40, 0.080, 0.030);
const float MistEmberGain = 0.36;   // the ember is self-lit, so the veins still glow at night
const float MistSelfLight = 0.16;   // a faint self-lit floor so the tongues never go flat black

// Ridged value noise: 1 on the noise's mid-level contour, 0 at its extremes, so warped contours
// read as thin licking tongues and filaments.
float dreamMistRidge(vec2 q)
{
    return 1.0 - abs(2.0 * dreamMistValueNoise(q) - 1.0);
}

// The flame structure at floor point xz: x the tongue body (0..1), y the ember veins in the
// crevices between tongues (0..1, pulsing slowly), z the indigo-to-violet tint (0..1).
// The lattice is stretched along the wind (0.8, 0.6) so tongues read as long flowing wisps, then
// warped twice (a large slow swirl and a smaller curl) before three ridged octaves drift through
// it at their own slow speeds (about 0.03 to 0.07 m/s). Every time term is a whole multiple of 64
// cells per 6400 s (BoundaryMistField.FlameRates), so the wrap is invisible.
vec3 dreamMistFlame(vec2 xz)
{
    float t = uMistParams.y;
    vec2 s = vec2(dot(xz, vec2(0.8, 0.6)) * 0.32, dot(xz, vec2(-0.6, 0.8)) * 0.80);

    vec2 swirl = vec2(
        dreamMistValueNoise(s * 0.45 + vec2(0.01, 0.01) * t + 3.0),
        dreamMistValueNoise(s * 0.45 + vec2(-0.01, 0.01) * t + 11.0)) - 0.5;
    vec2 q = s + 2.0 * swirl;
    vec2 curl = vec2(
        dreamMistValueNoise(q * 1.3 + vec2(0.03, -0.01) * t + 23.0),
        dreamMistValueNoise(q * 1.3 + vec2(-0.01, -0.03) * t + 31.0)) - 0.5;
    q += 0.9 * curl;

    // Cubed ridges: thin bright crests (the tongues) with dark gaps between them; a coarse mask
    // gathers the tongues into licking clusters instead of an even marbling.
    float r1 = dreamMistRidge(q + vec2(0.02, 0.01) * t + 17.0);
    float r2 = dreamMistRidge(q * 2.1 + vec2(-0.03, 0.05) * t + 29.0);
    float r3 = dreamMistRidge(q * 4.3 + vec2(0.06, -0.04) * t + 43.0);
    float mask = 0.45 + 1.1 * (swirl.y + 0.5);
    float body = clamp((0.52 * r1 * r1 * r1 + 0.32 * r2 * r2 * r2 + 0.16 * r3 * r3) * mask, 0.0, 1.0);

    float vein = pow(dreamMistRidge(q * 1.7 + vec2(-0.02, 0.03) * t + 53.0), 7.0);
    vein *= 1.0 - smoothstep(0.10, 0.40, body);
    float pulse = 0.65 + 0.35 * dreamMistValueNoise(vec2(t * 0.05, s.x * 0.3) + 61.0);

    return vec3(body, vein * pulse, smoothstep(0.30, 0.70, swirl.x + 0.5));
}

// The smoke's colour at world point p: lit as an up-facing surface from the frame lighting (no
// point lights) plus a small self-lit term, so it stays dark by day and alive at night. The
// tongues are livelier in the first 2 m past the edge and calmer deeper.
vec3 dreamBoundarySmoke(vec3 p)
{
    vec3 f = dreamMistFloorPoint(p);
    vec3 flame = dreamMistFlame(f.xz);
    float beyond = max(-dreamMistInset(f), 0.0);
    float life = mix(1.0, 0.5, smoothstep(2.0, 12.0, beyond));
    float body = smoothstep(0.05, 0.85, flame.x) * life;

    vec3 albedo = mix(MistBase, mix(MistIndigo, MistBruise, flame.z), smoothstep(0.0, 0.55, body));
    albedo = mix(albedo, MistCrest, smoothstep(0.55, 1.0, body));
    vec3 up = vec3(0.0, 1.0, 0.0);
    vec3 lit = albedo * (dreamAmbient(up) + 0.6 * dreamDirect(up)) * uLightingParams.x;
    return lit + albedo * MistSelfLight + MistEmber * (flame.y * MistEmberGain * life);
}

// Mixes a lit, fogged colour toward the smoke by the opacity at p. The noise runs only where the
// opacity is above zero (a dynamic branch).
vec3 dreamApplyBoundaryMist(vec3 c, vec3 p)
{
    float b = dreamBoundaryOpacity(p);
    return b > 0.0 ? mix(c, dreamBoundarySmoke(p), b) : c;
}
