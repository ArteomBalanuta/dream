using OpenTK.Mathematics;

namespace Dream.Rendering.Lighting;

// One authored world light: a brazier flame. Position is the flame centre in world space, Color is
// linear RGB already multiplied by intensity (as PointLight expects).
public readonly record struct PlacedLight(Vector3 Position, float Radius, Vector3 Color);
