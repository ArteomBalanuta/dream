using Dream.Simulation.Characters;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Simulation.Persistence;

// The character presence lease (M8, B9). A character is played in one world at a time: the join prefetch
// claims a lease for a character the joining account owns, the runtime renews it while the player is
// here and drops it when the player leaves. Every call is a no-op without accounts.
internal sealed partial class RuntimePersistenceBridge
{
    // A lease that is not renewed (a crash) frees itself after this; the same world reclaims at once.
    public static readonly TimeSpan PresenceTimeToLive = TimeSpan.FromSeconds(45);

    // The account repository, for the server commands; null on a runtime without accounts.
    public IAccountRepository? Accounts => _accounts;

    private WorldId PresenceWorld => (_host as WorldSessionRuntime)?.World.Id ?? WorldId.Default;

    // Called by the prefetch's load on the persistence worker, right after the account lookup. A lease is
    // taken only for an existing character the account owns; another world's live lease marks the lookup
    // HeldElsewhere, which AuthorizeJoin refuses.
    private CharacterLookup? ClaimPresenceForJoin(IAccountRepository accounts, CharacterLookup? lookup, AccountKey? account)
    {
        if (lookup is not { } read
            || account is not { } joining
            || read.Existing is not { Owner: { } owner } existing
            || !owner.Equals(joining))
        {
            return lookup;
        }

        DateTime now = DateTime.UtcNow;
        return accounts.TryClaimPresence(existing.Key.Value, PresenceWorld, now, now + PresenceTimeToLive)
            ? lookup
            : read with { HeldElsewhere = true };
    }

    // Queues a renewal of the leases of these characters (the players now in this world).
    public void RenewPresence(IReadOnlyList<string> characterKeys)
    {
        if (_accounts is not { } accounts || _writes == null || characterKeys.Count == 0)
        {
            return;
        }

        WorldId world = PresenceWorld;
        string[] keys = [.. characterKeys];
        _writes.Enqueue(() => accounts.RenewPresence(keys, world, DateTime.UtcNow + PresenceTimeToLive));
    }

    // Queues taking (or retaking) the lease of a character that just joined, behind any release a
    // takeover of its old session queued.
    public void ClaimPresenceAfterJoin(string characterKey)
    {
        if (_accounts is not { } accounts || _writes == null)
        {
            return;
        }

        WorldId world = PresenceWorld;
        _writes.Enqueue(() =>
        {
            DateTime now = DateTime.UtcNow;
            accounts.TryClaimPresence(characterKey, world, now, now + PresenceTimeToLive);
        });
    }

    // A join that was refused after its prefetch took the lease (a session in use, say): the lease goes
    // unless the character is already being played here.
    public void ReleasePresenceOfRejectedJoin(string typedName, AccountKey? account)
    {
        if (_accounts == null
            || account is not { } joining
            || !_playerPrefetches.TryGetValue(new PrefetchKey(typedName, joining), out PersistedPlayerPrefetch? prefetch)
            || prefetch.Lookup is not { HeldElsewhere: false, Existing: { Owner: { } owner } existing }
            || !owner.Equals(joining))
        {
            return;
        }

        foreach (Dream.Multiplayer.Sessions.ConnectedPlayer player in _host.Sessions.Players)
        {
            if (player.IsSpawned && !player.IsLocal && player.CharacterKey.Equals(existing.Key))
            {
                return;
            }
        }

        ReleasePresence(existing.Key.Value);
    }

    // Queues dropping the lease; only this world's own is dropped.
    public void ReleasePresence(string characterKey)
    {
        if (_accounts is not { } accounts || _writes == null)
        {
            return;
        }

        WorldId world = PresenceWorld;
        _writes.Enqueue(() => accounts.ReleasePresence(characterKey, world));
    }
}
