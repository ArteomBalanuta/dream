using OpenTK.Mathematics;

public readonly record struct FloatingCombatTextStyle(
    Vector4 Color,
    float ScaleMultiplier,
    float OutlineThickness,
    int LayerPriority);
