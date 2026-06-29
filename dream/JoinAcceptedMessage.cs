public sealed record JoinAcceptedMessage(
    string SessionId,
    int ActorId,
    ulong CurrentServerTick,
    WorldSnapshot? InitialWorld,
    IReadOnlyList<PlayerRosterEntry>? Roster = null) : IServerToClientMessage
{
    public string MessageKind => "server.join-accepted";

    public IReadOnlyList<PlayerRosterEntry> EffectiveRoster => Roster ?? Array.Empty<PlayerRosterEntry>();
}
