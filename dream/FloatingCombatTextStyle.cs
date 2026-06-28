using OpenTK.Mathematics;

public readonly record struct FloatingCombatTextStyle(
    Vector4 Color,
    float ScaleMultiplier,
    int LayerPriority);
