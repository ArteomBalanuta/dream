using OpenTK.Mathematics;

public readonly record struct FloatingCombatTextRenderState(
    string Text,
    Vector3 WorldAnchor,
    float HorizontalOffset,
    float VerticalOffset,
    float Scale,
    float Alpha,
    FloatingCombatTextStyle Style,
    int LayerPriority);
