using OpenTK.Mathematics;

public readonly record struct CollisionDebugVisibilityChangedEvent(bool IsVisible);

public readonly record struct ResolutionAppliedEvent(Vector2i Resolution);

public readonly record struct FloorSizeAppliedEvent(int FloorSize);

public readonly record struct RuntimeStateChangedEvent(RuntimeState State);
