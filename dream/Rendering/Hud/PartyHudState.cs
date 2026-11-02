namespace Dream.Rendering.Hud;

// One member as the party panel shows it: a cleaned name, the level and the health the server last sent.
public readonly record struct PartyMemberLine(string Name, int Level, int Health, int MaxHealth, bool IsLeader);

// What the party panel shows (M5 workstream A): the members with their health and the pending invite. One
// instance per scene, created with a parameterless constructor (the headless play scene fills fields by it).
// The scene fills it from ServerPartyState after cleaning the names; the panel only reads it.
public sealed class PartyHudState
{
    public const int MaxMembers = 5;
    public const int MaxNameChars = 32;

    private readonly PartyMemberLine[] _members = new PartyMemberLine[MaxMembers];

    public int Count { get; private set; }

    // The player whose invite is waiting for /accept or /decline; null when none.
    public string? PendingInviteFrom { get; private set; }

    public bool IsEmpty => Count == 0 && PendingInviteFrom == null;

    public PartyMemberLine MemberAt(int index)
    {
        if ((uint)index >= (uint)Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        return _members[index];
    }

    // Replaces the members (at most MaxMembers are kept) and the invite.
    public void Set(ReadOnlySpan<PartyMemberLine> members, string? pendingInviteFrom)
    {
        int count = Math.Min(members.Length, MaxMembers);
        members[..count].CopyTo(_members);
        Array.Clear(_members, count, _members.Length - count);
        Count = count;
        PendingInviteFrom = string.IsNullOrEmpty(pendingInviteFrom) ? null : pendingInviteFrom;
    }

    public void Clear() => Set([], null);
}
