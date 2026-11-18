using Dream.Simulation.Characters;
using Dream.Simulation.World;

namespace Dream.Simulation.Persistence;

// Accounts and character ownership (global, not bound to a world).
public interface IAccountRepository
{
    // Read-only. Runs on the persistence worker, or synchronously for non-deferred hosts.
    // account null: OwnedByAccount is 0.
    CharacterLookup Lookup(string trimmedName, AccountKey? account);

    // Queued write, one savepoint: upsert the account by token_hash (stamps last_seen_utc); when claim
    // is true, set account_id where player_name matches and account_id IS NULL. False when claim was
    // requested and no row changed (lost a race, or no row).
    bool RecordJoin(AccountKey account, CharacterKey character, bool claim, DateTime utcNow);

    // Read-only, like Lookup. The characters this account owns (most recently saved first), at most max.
    // Only the account's own rows are returned. A repository that cannot list returns none.
    IReadOnlyList<AccountCharacterSummary> ListCharacters(AccountKey account, int max) => [];

    // M8. Read-only. The role and sanctions of the account now; null when it has no row (or the repository
    // cannot read them, as a fake). The server reads connected accounts again on a slow cadence.
    AccountStanding? ReadStanding(AccountKey account) => null;

    // M8. Sets (untilUtc and reason) or clears (untilUtc null) the ban of the account that owns the
    // character displayName. False when no account owns it. The default (a fake) changes nothing.
    bool TrySetBan(string displayName, DateTime? untilUtc, string? reason) => false;

    // M8. Sets or clears the mute, like TrySetBan.
    bool TrySetMute(string displayName, DateTime? untilUtc) => false;

    // M8. Sets the role (0 player, 1 admin) of the account that owns the character displayName.
    bool TrySetRole(string displayName, int role) => false;

    // M8 presence lease (B9). Claims the character for the world until expiresUtc: an insert, or an update
    // that applies only while the row is this world's own or already expired. False when another world
    // holds a live lease. The default (a fake) always claims.
    bool TryClaimPresence(string characterKey, WorldId world, DateTime nowUtc, DateTime expiresUtc) => true;

    // M8. Extends the lease of the characters this world holds.
    void RenewPresence(IReadOnlyList<string> characterKeys, WorldId world, DateTime expiresUtc)
    {
    }

    // BH-XFER-4. The same renewal, reporting into lost the keys whose row it changed nothing for: this
    // world does not hold their lease any more (it ran out while the host was stalled, or another world
    // took it over). The caller has to act on those, because a renewal that fails silently is what lets
    // one character be played in two worlds. The default keeps the plain behaviour and reports nothing.
    void RenewPresence(IReadOnlyList<string> characterKeys, WorldId world, DateTime expiresUtc, List<string> lost)
        => RenewPresence(characterKeys, world, expiresUtc);

    // M8. Drops the lease, only while this world holds it.
    void ReleasePresence(string characterKey, WorldId world)
    {
    }
}

// One character of an account, for the character picker. Characters are global (not per world).
public readonly record struct AccountCharacterSummary(string DisplayName, int Level);
