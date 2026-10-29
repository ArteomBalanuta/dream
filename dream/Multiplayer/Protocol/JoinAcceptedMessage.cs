using Dream.Multiplayer.Sessions;
using Dream.Simulation.Inventory;
using Dream.Simulation.World;

namespace Dream.Multiplayer.Protocol;

public sealed record JoinAcceptedMessage(
    string SessionId,
    int ActorId,
    ulong CurrentServerTick,
    WorldSnapshot? InitialWorld,
    IReadOnlyList<PlayerRosterEntry>? Roster = null,
    PlayerInventorySnapshot? Inventory = null,
    IReadOnlyList<ActorSnapshot>? InitialActors = null,
    ulong? InitialWorldMutationSequence = null,
    // Protocol v3: set on the wire instead of InitialWorld, whose world follows as the
    // server.world-chunk messages of this transfer (WorldChunkMessage). The client session
    // reassembles it and delivers this message with InitialWorld set and this left null.
    int? WorldTransferId = null,
    // Protocol v5: the server's world clock at CurrentServerTick. Clients derive the time of day
    // from it and their own server tick estimate (ClientWorldClock); it is sent only at join, so
    // snapshots carry no clock bytes. A chunked transfer keeps it on the carrier.
    WorldClockAnchor? WorldClock = null,
    // Persistence wave B: the world the client joins (its map size and generator). Null is
    // omitted from the JSON, so a message without it encodes exactly as before.
    WorldDescriptorWire? World = null) : IServerToClientMessage
{
    public string MessageKind => "server.join-accepted";

    public IReadOnlyList<PlayerRosterEntry> EffectiveRoster => Roster ?? Array.Empty<PlayerRosterEntry>();

    public IReadOnlyList<ActorSnapshot> EffectiveInitialActors => InitialActors ?? Array.Empty<ActorSnapshot>();
}
