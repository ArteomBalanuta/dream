using OpenTK.Mathematics;

public readonly record struct ViewModelDefinition(
    string Id,
    ViewModelSlot Slot,
    string ModelPath,
    string? IdleAnimation,
    string? UseAnimation,
    Vector3 PositionOffset,
    Vector3 RotationOffsetDegrees,
    float Scale);
