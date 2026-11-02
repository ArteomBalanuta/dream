namespace Dream.Multiplayer.Protocol;

public readonly record struct PartyMemberSnapshot(string Name, int Level, int CurrentHealth, int MaxHealth, bool IsLeader);

// M5 (protocol v18). Owner only, unprompted: the members of the receiver's party (empty when in none)
// and the name of the player whose invite is pending, if any.
public sealed record ServerPartyState(IReadOnlyList<PartyMemberSnapshot> Members, string? PendingInviteFrom) : IServerToClientMessage
{
    public const string Kind = "server.party";
    public const int MaxMembers = 5;
    public string MessageKind => Kind;
}
