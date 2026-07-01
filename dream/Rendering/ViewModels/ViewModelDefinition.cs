using OpenTK.Mathematics;

namespace Dream.Rendering.ViewModels;


public readonly record struct ViewModelDefinition(
    string Id,
    ViewModelSlot Slot,
    string ModelPath,
    string? IdleAnimation,
    string? UseAnimation,
    Vector3 PositionOffset,
    Vector3 RotationOffsetDegrees,
    float Scale);
