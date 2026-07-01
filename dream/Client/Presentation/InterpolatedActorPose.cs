using OpenTK.Mathematics;

namespace Dream.Client.Presentation;


public readonly record struct InterpolatedActorPose(
    Vector3 Position,
    Vector3 Facing);
