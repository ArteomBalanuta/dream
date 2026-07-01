using OpenTK.Mathematics;

namespace Dream.Client.Presentation;


public readonly record struct RemotePlayerSnapshotSample(
    int ActorId,
    string SessionId,
    ulong ServerTick,
    Vector3 Position,
    Vector3 Facing,
    double ReceivedAtSeconds);
