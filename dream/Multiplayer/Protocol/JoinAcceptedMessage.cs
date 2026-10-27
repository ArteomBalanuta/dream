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
    ulong? InitialWorldMutationSequence = null) : IServerToClientMessage
{
    public string MessageKind => "server.join-accepted";

    public IReadOnlyList<PlayerRosterEntry> EffectiveRoster => Roster ?? Array.Empty<PlayerRosterEntry>();

    public IReadOnlyList<ActorSnapshot> EffectiveInitialActors => InitialActors ?? Array.Empty<ActorSnapshot>();
}
