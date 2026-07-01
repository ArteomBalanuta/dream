using OpenTK.Mathematics;

namespace Dream.Rendering.Hud;


public readonly record struct FloatingCombatTextStyle(
    Vector4 Color,
    float ScaleMultiplier,
    float OutlineThickness,
    int LayerPriority);
