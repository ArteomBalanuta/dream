using OpenTK.Mathematics;

namespace Dream.Rendering.Hud;


public readonly record struct FloatingCombatTextRenderState(
    string Text,
    Vector3 WorldAnchor,
    float HorizontalOffset,
    float VerticalOffset,
    float DepthOffset,
    float Scale,
    float Alpha,
    FloatingCombatTextStyle Style,
    int LayerPriority);
