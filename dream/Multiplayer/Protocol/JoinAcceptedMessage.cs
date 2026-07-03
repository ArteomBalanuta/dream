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
    PlayerInventorySnapshot? Inventory = null) : IServerToClientMessage
{
    public string MessageKind => "server.join-accepted";

    public IReadOnlyList<PlayerRosterEntry> EffectiveRoster => Roster ?? Array.Empty<PlayerRosterEntry>();
}
