using Dream.Simulation.Characters;

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
}
