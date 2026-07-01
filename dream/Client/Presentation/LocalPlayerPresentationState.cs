using OpenTK.Mathematics;

namespace Dream.Client.Presentation;


public readonly record struct LocalPlayerPresentationState(
    Vector3 Position,
    Vector3 Facing,
    bool IsMoving);
