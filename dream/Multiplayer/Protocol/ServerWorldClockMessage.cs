using Dream.Simulation.World;

namespace Dream.Multiplayer.Protocol;

// Protocol v23. The world clock's anchor, sent to every joined connection when the server moves its
// world clock other than by the one-tick-per-tick advance (the /dev tod jump, always forward). The
// client re-anchors on it exactly as on the anchor of JoinAcceptedMessage.
public sealed record ServerWorldClockMessage(WorldClockAnchor Anchor) : IServerToClientMessage
{
    public const string Kind = "server.world-clock";

    public string MessageKind => Kind;
}
