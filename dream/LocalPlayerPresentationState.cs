using OpenTK.Mathematics;

public readonly record struct LocalPlayerPresentationState(
    Vector3 Position,
    Vector3 Facing,
    bool IsMoving);
