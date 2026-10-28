using OpenTK.Mathematics;

namespace Dream.Rendering.Lighting;

// One world-space point light. Color is linear RGB already multiplied by intensity. Radius is the
// distance where the light reaches 0 (windowed falloff in pointLights.glsl).
public readonly record struct PointLight(Vector3 Position, float Radius, Vector3 Color);
