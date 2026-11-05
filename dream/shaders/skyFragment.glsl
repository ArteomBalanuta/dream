#version 330 core

in vec4 vFarPoint;

out vec4 FragColor;

// The FrameLighting block (world range) and the shared cloud field; the cloud sheet below reads
// uCloudParams and uFrameTime from it (the view ray origin is the separate uEyePosition uniform). The disc uniforms below are named
// uSunDiscColor / uMoonDiscColor because the block already owns uSunColor / uMoonColor.
#include "lighting.glsl"
#include "clouds.glsl"

// SkyState, uploaded once per frame by SkyRenderer.
uniform vec3 uZenithColor;
uniform vec3 uHorizonColor;   // == FrameLighting.FogColor.rgb, so the sky meets the fog with no seam
uniform vec4 uSun;            // xyz unit direction toward the sun, w disc angular radius (radians)
uniform vec3 uSunDiscColor;  // disc colour, 0 when the sun is below the horizon
uniform vec4 uMoon;           // xyz unit direction toward the moon, w disc angular radius (radians)
uniform vec3 uMoonDiscColor;
uniform float uStarVisibility; // 0..1
uniform vec3 uEyePosition;     // the camera's world position (the view ray's origin for the cloud sheet)

// Everything added on top of the gradient (stars, sun, glow, moon) fades in over this much
// elevation, so at and below the horizon the sky is exactly uHorizonColor.
const float HorizonFade = 0.06;
const float SunGlowExponent = 48.0;
const float SunGlowStrength = 0.35;
const float StarCellsPerUnit = 360.0;
const float StarThreshold = 0.9975;
const float DiscEdgeSoftness = 0.15;

// A cheap hash of a cell coordinate into [0, 1). No texture.
float hash13(vec3 p)
{
    p = fract(p * 0.1031);
    p += dot(p, p.zyx + 31.32);
    return fract((p.x + p.y) * p.z);
}

// 1 inside a body's disc, 0 outside, with a slightly soft edge.
float disc(vec3 dir, vec4 body)
{
    float angle = acos(clamp(dot(dir, body.xyz), -1.0, 1.0));
    return 1.0 - smoothstep(body.w * (1.0 - DiscEdgeSoftness), body.w, angle);
}

float stars(vec3 dir)
{
    vec3 scaled = dir * StarCellsPerUnit;
    vec3 cell = floor(scaled);
    float h = hash13(cell);
    float present = step(StarThreshold, h);
    // Round-ish points inside the cell, with a hashed brightness.
    float shape = 1.0 - smoothstep(0.15, 0.45, length(fract(scaled) - 0.5));
    float brightness = 0.45 + 0.55 * fract(h * 113.0);
    return present * shape * brightness;
}

void main()
{
    vec3 dir = normalize(vFarPoint.xyz / vFarPoint.w);
    float elevation = clamp(dir.y, 0.0, 1.0);
    float above = smoothstep(0.0, HorizonFade, dir.y);

    vec3 color = mix(uHorizonColor, uZenithColor, sqrt(elevation));

    color += vec3(stars(dir)) * (uStarVisibility * above);

    // The moon is opaque over the stars; the sun and its glow add light.
    color = mix(color, uMoonDiscColor, disc(dir, uMoon) * above * step(0.0001, dot(uMoonDiscColor, uMoonDiscColor)));

    float sunCos = max(dot(dir, uSun.xyz), 0.0);
    float sunLight = pow(sunCos, SunGlowExponent) * SunGlowStrength + disc(dir, uSun);
    color += uSunDiscColor * (sunLight * above);

    // The cloud sheet: the view ray cut with the cloud plane (the same plane the ground shadows use),
    // drawn over the discs so clouds cross sun, moon and stars, lit by the frame's ambient and sun, and
    // faded out with `above` so the sky still meets the fog with no seam. Coverage 0 leaves the sky untouched.
    if (uCloudParams.x > 0.0 && dir.y > 0.001)
    {
        // The REAL eye (uEyePosition), not uCameraPosition: that is the fog origin, which in the isometric scene is the
        // camera focus tens of units away, and the ground shadows use true world positions.
        vec3 eye = uEyePosition;
        vec3 onPlane = eye + dir * ((CloudPlaneY - eye.y) / dir.y);
        float cloud = dreamCloudCover(onPlane.xz) * 0.8;
        vec3 cloudColor = uHorizonColor * 0.55 + dreamAmbient(vec3(0.0, 1.0, 0.0)) + uSunColor.rgb * (uSunDirection.w * 0.30);
        color = mix(color, cloudColor, cloud * above);
    }

    FragColor = vec4(color, 1.0);
}
