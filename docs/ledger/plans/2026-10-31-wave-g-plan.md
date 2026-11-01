# Persistence Wave G Plan: Characters

Purpose:
- A person is an account: a hidden token kept on their computer. One account has up to 5 characters.
- Level and base stats travel with the character. Items, position and health stay in each world.
- A character can only be used by the account that owns it.
- `DisplayName` stops being the persistence key.
- Agility, Energy and MovementSpeed are saved as stored, not as constants.

Status: ready to implement. Analysed read-only on `integrate/engine-iteration` at `db07642` (schema v6, protocol v8). U1, U2 and U10 are settled as the recommended defaults. The migration SQL below was run on in-memory SQLite 3.50.4 (via Python), including collision cases and a 10,100-row table (25 ms). No .NET test or measurement was run.

## Decisions

| Id | Decision |
| --- | --- |
| U1, U2, U10 | Settled, as in the roadmap. |
| G-D1 | **Ownership is decided in `JoinRequestHandler`, on the tick, from memory only, before `TryClaimSession`.** The roadmap says "when the prefetch result is applied", but the takeover runs inside `TryClaimSession`, so a check after it would let a stranger kick the owner. |
| G-D2 | **The prefetch does the reading and the tick does the deciding.** The prefetch stays read-only. The account row and the claim are one queued write, made after `JoinSession` succeeds. A join that is never applied writes nothing. `JoinRequest_CreatesTheProfileRowOnlyWhenTheJoinIsApplied` keeps its meaning. |
| G-D3 | **Prefetches are keyed by (name, account), not by name.** Part of the result (how many characters this token owns) is per token. Keyed by name alone, two tokens contesting a name would replace each other's load every tick until the 30 s bound. |
| G-D4 | **No claim overlay.** When a join claims or creates a character, every other pending prefetch for that name or that account is removed. Those joins see `Missing`, restart their load (ordered behind the queued claim) and read the new owner. This reuses the existing `Missing` path. |
| G-D5 | **`AccountKey` is the SHA-256 of the token, in memory and in comparisons.** The tick cannot know a new account's `account_id`, because AUTOINCREMENT runs on the queue. |
| G-D6 | **Character identity is the trimmed name with ASCII case folded, which is exactly SQLite `NOCASE`.** `CharacterNames.Comparer` implements it, and a test pins it against SQLite. `Äb` and `äb` are two different characters. |
| G-D7 | **`CharacterKey` compares ordinally, and every persistence map switches to `StringComparer.Ordinal`.** After 0007, `Bob` and `bob` can both exist as keys with different display names. The current `OrdinalIgnoreCase` maps would merge their profile and inventory. |
| G-D8 | **`player_name` never changes.** `display_name` is what people type and see. A new character whose name equals an existing `player_name` (a retired key) is rejected as taken. `SaveSql` is an upsert on `player_name`, so it would otherwise overwrite that character silently. |
| G-D9 | **A null or malformed token on a host with accounts is rejected before any prefetch.** This includes the integrated server. There is no anonymous mode. |
| G-D10 | **The trusted path is the in-process two-argument `JoinSession`.** It resolves the key but checks no owner and claims nothing. No network message reaches it. |
| G-D11 | **Hosts without an account repository keep today's behaviour exactly**, including the 8 s window and the throttle. That is every host with `persistence: null` and every fake-repository host. They have nothing to protect, and `StaleSessionTakeoverTests` stays as their regression test. |
| G-D12 | Legacy characters and released characters are the same thing: `account_id IS NULL`. The first token to join claims it, subject to the limit of 5. |
| G-D13 | A token that already owns 5 characters cannot create or claim another. Nothing is claimed, and the join is rejected. It can still join the 5 it owns. A limit breach cannot happen within one process; see Risks for two processes. |
| G-D14 | `desktop-client.txt` stat application is **in** (G3). The file's stats never reach the server (no join path sends them). `CaptureRuntime` also writes the current character's stats into a per-machine file, which is wrong with several characters. |
| G-D15 | `release` and `list` run while a server runs. `rename` is offline and needs every world's lease, because a live server's cached profile would write the old `display_name` back. |
| G-D16 | Rejection reasons stay plain strings in `JoinRejectedMessage`, as constants. There is no new wire code. The client already shows them. |
| G-D17 | Once joined, a connection cannot switch character. A second join on it for another key is rejected. Today that path silently renames the seat and switches the persistence key (`AttachRemoteSessionCore` calls `UpdateDisplayName` and restores nothing). Same key stays idempotent. |

## What The Code Does Today

**Confirmed from the roadmap:**
- `PersistPlayerProfile` writes the constants `Agility: 10`, `Energy: 10`, `MovementSpeed: 6f` (`RuntimePersistenceBridge.cs:455-458`).
- The tick never reads the database. `ApplyPendingJoins` defers on `Pending`, restarts on `Missing`, retries `Failed` once, and rejects after `MaxJoinPrefetchDeferralTicks` (30 s).
- `CheckpointUpdateVitalsSql` touches only `interaction_mode`, `current_hp`, `max_hp`.
- `SimulationStateChecksum` excludes `DisplayName`. It hashes the live base stats, level and experience, and not Agility, Energy or MovementSpeed.
- `SqliteWorldCatalog`'s copy lists hold only per-world tables, so accounts and profiles are never copied.

**Corrections to the roadmap:**
1. **"Only 5 sites resolve it for persistence" is too low.** Sites to change:
   - `WorldSessionRuntime.Inventory.cs` lines 28, 34, 42, 63, 95;
   - `.Building.cs:42`;
   - `.Sessions.cs` lines 26, 152, 228-229;
   - `RuntimeAuthoritativeSessionHost.cs:100,140`;
   - bridge lines 446-449, 492, 498, 501, 713 (`IsProfileNameActive`);
   - `RuntimePersistenceBridge.Checkpoints.cs:362-376`.
   `.Replication.cs:193` is a real display use and stays. So does the actual display text in rosters and logs.
2. **A migration comment is not enough for duplicates.** Duplicates must be found on `display_name`, not `player_name`. A cached `Bob` joined as `bob` saves `display_name = 'bob'` on row `Bob`, so two rows can share a display name exactly. Suffixes must also avoid existing names.
3. **`COLLATE NOCASE` is ASCII only, while the in-memory maps are Unicode `OrdinalIgnoreCase`.** They disagree on non-ASCII names. G-D6 and G-D7 resolve this.
4. **More maps than the roadmap implied ignore case:**
   - bridge lines 59, 60, 72, 77, 632, 703;
   - `PlayerInventoryService.cs:37,41,48`;
   - `PersistenceCheckpoint.cs:81` (the coalescing merge);
   - `DedicatedServerHost.cs:663,692`.
5. **G1's file list is wrong.** `SqlitePlayerProfileRepository*.cs` and `PersistedPlayerProfile.cs` need no change. `SaveSql` never names `account_id`, so ordinary saves can't touch ownership. `PersistedPlayerPrefetch.cs` belongs with the seam.
6. **"One token per server" needs a special case.** The integrated server binds port 0 (`IntegratedLocalServerHandle.StartAsync`), so a `host:port` key would mint a new account on every launch.
7. **A blank name falls back to the session id** (`ResolveJoinPlayerName`), which is `client-<guid>`, 39 characters. It breaks the 32 limit. On account hosts a blank name is rejected. The desktop client already refuses it.
8. **`ClientJoinRequestMessage` is a positional record, so the default `ToString` prints every member.** A token field leaks through any interpolation unless it is redacted. The same goes for `RemoteClientOptions`. No code logs them whole today; the three `Describe` sites list fields by hand.
9. **The roadmap's `0007` SQL has no `CHECK` on the hash size and no index on `account_id`.** Both are added.
10. **The roadmap's small join SHA gate can't hold literally.** The version digit is inside the hash. Compare with the digit normalized, and record the new SHA at C0.

**Migrator facts for 0007** (`SqliteSchemaMigrator`, `DatabaseMaintenanceLock`):
- The backup `{db}.v0006-to-v0007.{stamp}.bak` is taken under the write lock, for any non-new database.
- Pending migrations need every world's lease, else exit 7.
- Foreign keys are off during the run. `foreign_key_check` runs before commit, and a violation rolls everything back.
- No table is rebuilt, so the five object-id triggers are untouched.
- The script must not contain transaction control at line start.
- `UPDATE` on a duplicate fires `trg_player_profiles_set_updated_utc`. That is harmless: the vitals guard reads `player_world_state`.
- A v7 file on an older build exits 3.

**Local databases inspected read-only** (3 copies on this machine; v5, v6, v6; 1, 2 and 11 profiles): no case duplicates, `player_name = display_name` everywhere, longest name 25, ASCII only.

## Scope

**In:**
- Migration 0007 and protocol v9.
- Accounts, ownership, claiming, and the limit of 5.
- `CharacterKey` across all persistence paths.
- The name rule for new names.
- Same-account takeover.
- Client token store.
- `--characters list | release | rename`.
- Integrity additions and restart coverage.
- Real stat values on save.
- `desktop-client.txt` stats removal.
- Docs.

**Out:**
- Real authentication and encryption (U10).
- A character picker UI.
- A player-facing delete.
- An account merge or token export.
- A rule stopping one character being live in two worlds at once.
- `current_energy` (stays 0).
- Enemy stat constants (wave I).
- Items (wave H).
- Pruning old token entries from the client file.
- Telling a taken-over client why it was closed (it sees a lost connection).

## Determinism And Security Rules

**Determinism**
1. Account and ownership data never enter the tick's simulation inputs, RNG seeds or checksums. No golden or snapshot byte changes. Any golden change is a defect, not a refresh.
2. Join order stays canonical (`ComparePendingJoinsOrdinal`). G-D4 can defer a contested join by a few ticks. That is the same kind of timing as a pending prefetch.
3. `ToString` on `CharacterKey` is for keys and logs only. Display names shown to clients come from the row. For every existing test name, stored equals typed, so bytes hold.

**Security**
4. **Token format:** 32 bytes from `RandomNumberGenerator`, base64url without padding, 43 characters. The server accepts exactly that form: canonical (re-encoding gives the same text) and nothing else. Failure rejects the join; the frame limit bounds the rest.
5. **Hash:** SHA-256 of the 32 decoded bytes, unsalted. A salt adds nothing against a 256-bit random value, and the lookup needs a deterministic value.
6. **Constant-time compare is not needed.** The server looks up and compares hashes. Timing could at most reveal part of a stored hash, and a stored hash neither logs anyone in nor can be steered toward by choosing tokens. The same reasoning means a database backup holds no usable secret.
7. **The token is never logged, printed or kept longer than needed.** `HandleClientMessage` replaces the message with one whose `AccountToken` is null and whose `ResolvedAccount` is set, before throttling, queueing or routing. So `PendingJoin` and `LastJoinRequest` never hold it. Redact `PrintMembers` on both records. `AccountKey.ToString()` returns the literal `"account"`. `--characters` prints `account_id`, never the hash.
8. **A session id is public and grants nothing.** `client-<guid>` is broadcast in snapshots and rosters. Knowing one allows no command or action: `PlayerCommandHandler` and `PlayerActionHandler` match it against the connection's own join. In wave G:
   - Another account presenting a held session id is rejected, stale or not. It can no longer eject a silent holder.
   - A departed session id reused by another account restores only that account's character.
9. **Hostile names:** rejected or new names are logged by length, not by content. Existing log lines are unchanged.
10. **U10 limit:** anyone capturing traffic gets the token. Say so in the docs.

## C0 Contracts

C0 is one commit on the tip. The suite stays green, and behaviour is unchanged because no host has accounts wired yet. After C0, nobody edits contract types except to add what a workstream section names.

**New `dream/Simulation/Characters/`** (namespace `Dream.Simulation.Characters`):

```csharp
// The immutable persistence key (player_profiles.player_name). Ordinal.
public readonly record struct CharacterKey
{
    public CharacterKey(string value);        // throws on null or whitespace
    public string Value { get; }
    public bool IsNone => Value is null;
    public override string ToString() => Value ?? string.Empty;
}

// SHA-256 of a token. Never holds the token.
public readonly record struct AccountKey
{
    public const int TokenBytes = 32;
    public const int TokenChars = 43;
    public string HashHex { get; }            // 64 lowercase hex
    public byte[] ToHashBytes();
    public static AccountKey FromHash(ReadOnlySpan<byte> sha256);
    // Strict: 43 chars, Base64Url, 32 bytes, canonical. Null, blank or malformed gives false.
    public static bool TryFromToken(string? token, out AccountKey key);
    public override string ToString() => "account";
}

public static class CharacterNames
{
    public const int MaxNewNameLength = 32;        // UTF-16 code units, after trimming
    public const int MaxCharactersPerAccount = 5;
    public static IEqualityComparer<string> Comparer { get; }   // ASCII case fold, same as SQLite NOCASE
    public static string Normalize(string name);                // string.Trim()
    // 1..32 after trimming; no Control, Format, LineSeparator, ParagraphSeparator or
    // PrivateUse category; no unpaired surrogate. Applied only when creating or renaming.
    public static bool IsValidNewName(string trimmedName);
}

public static class JoinRejectionReasons
{
    public const string TokenRequired = "This server needs a player identity. Update your client and try again.";
    public const string TokenInvalid = "Your player identity is not valid. Update your client and try again.";
    public const string CharacterOwned = "That character belongs to someone else. Choose another name.";
    public const string CharacterLimit = "You already have the maximum of 5 characters. Pick one of them, or ask the server admin to release one.";
    public const string NameInvalid = "Character names must be 1 to 32 characters, with no control characters.";
    public const string NameTaken = "That character name is not available. Choose another name.";
    public const string CharacterInUse = "That character is being played right now.";
    public const string AlreadyJoined = "This connection is already playing another character. Reconnect to switch.";
}

public readonly record struct CharacterRecordRef(CharacterKey Key, string DisplayName, AccountKey? Owner);

// What the prefetch read. Existing: the row whose display_name matches NOCASE.
// KeyReserved: no display match, but a row has this exact player_name.
public readonly record struct CharacterLookup(CharacterRecordRef? Existing, bool KeyReserved, int OwnedByAccount);

public enum CharacterAccessOutcome : byte { Unchecked, Owned, ClaimUnowned, CreateNew, Rejected }

public readonly record struct JoinHolderIdentity(CharacterKey Character, AccountKey Account);

public readonly record struct CharacterAccessDecision(
    CharacterAccessOutcome Outcome,
    CharacterKey Key,
    string DisplayName,
    AccountKey? Account,
    string? RejectionReason)
{
    public bool IsRejected => Outcome == CharacterAccessOutcome.Rejected;
    public JoinHolderIdentity? Holder { get; }   // null for Unchecked and Rejected
    public static CharacterAccessDecision Unchecked(string trimmedName);
}

public static class CharacterAccessRules
{
    // Pure; implemented in C0 with a table test. No lookup means no accounts.
    public static CharacterAccessDecision Decide(string trimmedName, AccountKey account, in CharacterLookup lookup);
}
```

**`CharacterAccessRules.Decide` table:**

| Lookup | Owned count | Result |
| --- | --- | --- |
| Existing, owner is this account | any | `Owned` |
| Existing, another owner | any | Rejected `CharacterOwned` |
| Existing, no owner | < 5 | `ClaimUnowned` |
| Existing, no owner | >= 5 | Rejected `CharacterLimit` |
| None, `KeyReserved` | any | Rejected `NameTaken` |
| None, name fails the rule | any | Rejected `NameInvalid` |
| None, valid name | >= 5 | Rejected `CharacterLimit` |
| None, valid name | < 5 | `CreateNew` |

The name rule is never applied to an existing character, so legacy names of any length stay valid. The `NameInvalid` check comes before the limit check so the player fixes one thing at a time.

**Protocol:**

```csharp
// dream/Multiplayer/Protocol/ClientJoinRequestMessage.cs
public sealed record ClientJoinRequestMessage(
    string RequestedSessionId, string DisplayName, string? AccountToken = null) : IClientToServerMessage
{
    public string MessageKind => "client.join-request";
    [JsonIgnore] public AccountKey? ResolvedAccount { get; init; }   // server side, never from the wire
    private bool PrintMembers(StringBuilder builder);                // prints session and name only
}
// TransportEnvelope.CurrentProtocolVersion = 9, with the history comment extended.
```

Wire: `"accountToken":"<43 chars>"`, omitted when null. Server-to-client frames change only in the version digit.

**Persistence ports:**

```csharp
// dream/Simulation/Persistence/IAccountRepository.cs (global, not world-bound)
public interface IAccountRepository
{
    // Read-only. Runs on the persistence worker, or synchronously for non-deferred hosts.
    // account null: OwnedByAccount is 0.
    CharacterLookup Lookup(string trimmedName, AccountKey? account);

    // Queued write, one savepoint: upsert the account by token_hash (stamps last_seen_utc); when
    // claim is true, set account_id where player_name matches and account_id IS NULL.
    // False when claim was requested and no row changed (lost a race, or no row).
    bool RecordJoin(AccountKey account, CharacterKey character, bool claim, DateTime utcNow);
}

// RuntimePersistencePorts:      public IAccountRepository? Accounts { get; init; }
// ServerPersistenceBundle ctor: trailing IAccountRepository? accounts = null; property Accounts.
// WorldSessionRuntime ctor:     trailing IAccountRepository? accounts = null; passed to the bridge.
// RuntimeAuthoritativeSessionHost.CreateDefault passes it through.
// Bridge and runtime both expose: bool HasAccounts => _accounts != null.
```

**Prefetch** (`PersistedPlayerPrefetch.cs`, runtime and bridge):

```csharp
public PersistedPlayerPrefetch(string playerName, AccountKey? account = null);
public AccountKey? Account { get; }
public CharacterLookup? Lookup { get; }                     // null when the runtime has no accounts
internal void Complete(profile, worldState, inventory = null, CharacterLookup? lookup = null);

// Bridge and runtime. Existing one-argument calls mean account null, so tests compile unchanged.
PersistedPlayerPrefetch? BeginPersistedPlayerPrefetch(string playerName, AccountKey? account = null);
PersistedPlayerLoadState GetPersistedPlayerLoadState(string playerName, AccountKey? account = null);
bool IsPersistedPlayerReady(string playerName, bool startIfMissing, AccountKey? account = null);
void DiscardPersistedPlayerPrefetch(string playerName, AccountKey? account = null);
```

The map key is `(CharacterNames.Comparer over name, account)`. `Load()` first calls `accounts.Lookup`, then loads profile, world state and inventory under `Existing?.Key ?? name`.

**Session host and claims:**

```csharp
// IAuthoritativeSessionHost: default methods, so the three test fakes compile.
CharacterAccessDecision AuthorizeJoin(string requestedSessionId, string displayName, AccountKey? account)
    => CharacterAccessDecision.Unchecked(displayName.Trim());
JoinAcceptedMessage JoinSession(string requestedSessionId, string displayName, in CharacterAccessDecision access)
    => JoinSession(requestedSessionId, displayName);

// IServerSessionClaimRegistry: new default forwards to the existing overload.
bool TryClaimSession(string connectionId, string sessionId, string? displayName,
    JoinHolderIdentity? holder, out string? rejectionReason);

// ServerRoutingContext
public JoinHolderIdentity? JoinedIdentity { get; set; }

// ConnectedPlayer: CharacterKey defaults to new CharacterKey(DisplayName) and follows
// UpdateDisplayName until bound.
public CharacterKey CharacterKey { get; }
public void BindCharacter(CharacterKey key, string displayName);
```

**CLI surface** (implemented in G1):

```
dream --characters list                       display_name, key, level, account_id or -, last_seen_utc; tab-separated
dream --characters release <name>             sets account_id NULL; prints released, key, name
dream --characters rename <old> <new>         display_name only; new name must pass the rule and be free
```
- Exit codes: 0 ok, 1 usage, 2 not found or name conflict, 3 schema too new, 7 held.
- `<name>` matches `display_name` NOCASE, then the exact key.
- Control characters print as `?`.
- Releasing an unowned character prints `already-unowned` and exits 0.
- `rename` has no `--yes`, matching `--worlds create`. `release` takes none, because it is reversible by the owner's next join.

**Test support in C0:**
- `dream.Tests/Support/TestAccountTokens.cs`: `string For(string name)`, a stable valid token derived from the name's SHA-256, so restarts keep the account.
- `InMemoryAccountRepository : IAccountRepository`.
- If `SourceLayoutTests` or `NamespaceConventionTests` list folders, add `Simulation/Characters` and `Client/Identity` here.

**C0 tests:**
- Fix protocol pins in the 11 files that hold them: `ProtocolV2SharedSnapshotLoopbackIntegrationTests`, `ProtocolV2VersionRejectionLoopbackIntegrationTests`, `WaveBTesterWorldsLoopbackTests`, `JoinAcceptedWorldDescriptorCodecTests`, `ProtocolFoundationTests`, `ProtocolV2SlimmingTests`, `TerrainChunkMessageCodecTests`, `TransportJsonCodecDecodeTests`, `WorldClockAnchorProtocolTests`, `WorldTransferLimitTests`, `DedicatedServerServiceConnectionErrorTests`.
- Fix schema pins (6 to 7) under `dream.Tests/Server/Persistence`; about 15 files match `Equal(6,` or `ShippedLatestVersion`, and some of those matches may be unrelated.
- `CharacterAccessRulesTests`: every row of the table.
- `AccountKeyTests`: wrong length, padding, `+` and `/`, non-canonical last character, whitespace; a golden hash.
- `CharacterNamesTests`: 1, 32 and 33 lengths; control, zero-width and bidi characters; an unpaired surrogate; `Comparer` agrees with SQLite `NOCASE` on a sample that includes non-ASCII.
- `ClientJoinRequestCodecTests`: omitted when null; round trip; a client-sent `resolvedAccount` is ignored; `ToString()` holds no token.
- `AccountsMigrationTests`, below.

## Migration 0007 (`dream/config/migrations/0007_accounts.sql`, in C0)

```sql
-- Migration 0007: accounts and character ownership (wave G). No table is rebuilt. player_name stays
-- the immutable key; display_name becomes unique ignoring ASCII case.
CREATE TABLE accounts (
    account_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    token_hash BLOB NOT NULL,
    created_utc TEXT NOT NULL,
    last_seen_utc TEXT NOT NULL,
    CHECK (length(token_hash) = 32)
);

CREATE UNIQUE INDEX ux_accounts_token_hash ON accounts (token_hash);

ALTER TABLE player_profiles ADD COLUMN account_id INTEGER NULL
    REFERENCES accounts(account_id) ON DELETE SET NULL;

CREATE INDEX ix_player_profiles_account_id
    ON player_profiles (account_id) WHERE account_id IS NOT NULL;

-- Rows sharing a display name ignoring case: the oldest keeps it (created_utc, then player_name);
-- each other row gets "<its own display name>~<n>", with n the lowest free numbers from 2. A name
-- ending in ~digits cannot collide across groups, and the number range cannot run out: it holds
-- more values than there are renamed rows plus rows containing '~'.
CREATE INDEX ix_player_profiles_display_name__v7 ON player_profiles (display_name COLLATE NOCASE);

CREATE TABLE display_name_fix__v7 (
    player_name TEXT NOT NULL PRIMARY KEY,
    new_display_name TEXT NOT NULL
);

INSERT INTO display_name_fix__v7 (player_name, new_display_name)
WITH RECURSIVE
ranked AS (
    SELECT player_name, display_name,
        ROW_NUMBER() OVER (PARTITION BY display_name COLLATE NOCASE ORDER BY created_utc, player_name) AS duplicate_rank,
        MIN(display_name) OVER (PARTITION BY display_name COLLATE NOCASE) AS group_id
    FROM player_profiles
),
losers AS (
    SELECT player_name, display_name, group_id, duplicate_rank - 1 AS slot
    FROM ranked WHERE duplicate_rank > 1
),
numbers(n) AS (
    SELECT 2
    UNION ALL
    SELECT n + 1 FROM numbers
    WHERE n <= (SELECT COUNT(*) FROM losers)
        + (SELECT COUNT(*) FROM player_profiles WHERE instr(display_name, '~') > 0) + 1
),
free AS (
    SELECT g.group_id, n, ROW_NUMBER() OVER (PARTITION BY g.group_id ORDER BY n) AS slot
    FROM (SELECT DISTINCT group_id FROM losers) AS g
    CROSS JOIN numbers
    WHERE NOT EXISTS (
        SELECT 1 FROM player_profiles AS p
        WHERE p.display_name = g.group_id || '~' || n COLLATE NOCASE)
)
SELECT l.player_name, l.display_name || '~' || f.n
FROM losers AS l JOIN free AS f ON f.group_id = l.group_id AND f.slot = l.slot;

UPDATE player_profiles
SET display_name = (SELECT f.new_display_name FROM display_name_fix__v7 AS f
                    WHERE f.player_name = player_profiles.player_name)
WHERE player_name IN (SELECT player_name FROM display_name_fix__v7);

DROP TABLE display_name_fix__v7;
DROP INDEX ix_player_profiles_display_name__v7;

CREATE UNIQUE INDEX ux_player_profiles_display_name
    ON player_profiles (display_name COLLATE NOCASE);
```

Trial results on in-memory SQLite:
- `Bob, bob, BOB` gives `Bob, bob~2, BOB~3`.
- With literal `bob~2`, `BOB~2` and `bob~3` present, `bob` gives `bob~4` and `BOB~2` gives `BOB~2~2`.
- `Äb` and `äb` both stay as they are.

Real-data rules:
- All rows of a renamed player keep their `player_name`, so their world rows, inventory and cubes are untouched.
- A migrated `bob~2` is a normal existing character. The name rule doesn't apply to it, even past 32.
- The migration cannot log renames. `--characters list` shows them, and the `.bak` holds the originals.
- `foreign_key_check` passes because every `account_id` is NULL.

**`AccountsMigrationTests`:**
- every version 0 to 6 migrates to 7 with `integrity_check` ok and `foreign_key_check` empty;
- all 5 object-id triggers survive;
- each trial case above, and two rows with the same display name exactly;
- rows, world rows and inventory rows are byte-equal apart from `display_name`, `account_id` and `updated_utc`;
- two copies of one database migrate identically;
- a `v0006-to-v0007` backup exists and holds the old names;
- a forced failure rolls back to 6;
- `length(token_hash) != 32` is refused;
- the unique index refuses a NOCASE duplicate.

## Join Rules

**Flow on a deferred host, the production path:**
1. `HandleClientMessage`, under the lock. When `Runtime.HasAccounts`:
   - `AccountKey.TryFromToken` runs first. Null gives `TokenRequired`; malformed gives `TokenInvalid`. Both reply at once, set `JoinRejectionSent`, and start no prefetch.
   - Blank name gives `NameInvalid`.
   - The message becomes `join with { AccountToken = null, ResolvedAccount = key }`.
2. `TryThrottleJoin`. The throttle key gains the account (see below).
3. `QueueJoin` calls `BeginPersistedPlayerPrefetch(name, account)`.
4. **While pending:** the connection has no claim, no seat and no reply. The holder of the character is untouched. Commands from it are dropped, because `LastJoinRequest` is null. A second join on it is ignored (`server.join.duplicate-ignored`). Disconnecting removes the pending join. The existing deferral bound and single retry apply, and failure gives `JoinRetryRejectionReason`, which the client retries.
5. `ApplyPendingJoins` routes only when `Ready`.
6. `JoinRequestHandler`, in this order:
   1. `AuthorizeJoin` reads the prefetch (not consuming it) and calls `Decide`.
   2. Rejected: add `JoinRejectedMessage`. For `CharacterOwned`, also set `context.JoinConflictPlayerName`, so `CompleteJoinAttempt` starts the throttle. Return.
   3. If `context.JoinedIdentity` holds a different key: `AlreadyJoined`.
   4. `TryClaimSession(..., access.Holder, ...)`.
   5. `JoinSession(id, name, access)`.
   6. `context.JoinedIdentity = access.Holder`, and set `LastJoinRequest`.
7. Inside `JoinSession(.., access)`: attach with `BindCharacter`, restore by key, then `Runtime.CommitCharacterAccess(access)`:
   - queues `RecordJoin(account, key, claim: outcome is ClaimUnowned or CreateNew, now)`, after the profile save that creates a new row;
   - removes other prefetches for that name or account (G-D4) when claiming or creating;
   - a `false` result logs `server.character.claim-lost` from the write.

A rejection leaves the connection open and unclaimed. The reply goes through `_pendingConnectionReplies`, `DeliverPendingJoinReplies`, then `JoinSupervisorAsync`. Only `JoinRetryRejectionReason` retries; any other reason ends the session as `Join rejected: <reason>`, and the connection screen shows it. No client code is needed for these reasons.

**Non-deferred hosts** (tests only) have no prefetch. `AuthorizeJoin` flushes the queue and calls `Lookup` synchronously, like the existing `join-sync-read` path. They never run on the server tick.

**Takeover on hosts with accounts:**

| Situation | Result |
| --- | --- |
| Same account, same character, live or silent holder | Immediate takeover. The old connection is released (profile saved) and closed. New helper with no silence check. |
| Same account, session id held | Same immediate takeover. |
| Other account, session id held, live or stale | `SessionInUseRejectionReason`. No stale takeover. The 10 s idle release frees it. |
| Other account, character held | The ownership check rejects first. If the character is unowned (just released), `CharacterInUse`. |
| Rejoin after disconnect | Normal join. `Owned`. Restores from the cache, as today. |
| Crash rejoin | Immediate, instead of up to 8 s. |

`TakeoverSilence` and the name-based path remain only for `holder == null` (hosts without accounts).

**Throttle:** it stays, because an ownership rejection costs a prefetch. `JoinThrottleTable.Key` gains the account hash, so a spammer on a shared address can't lock out the real owner, whose token differs. The owner has no conflict and never reaches it. Rotating tokens evades it, but costs no more than fresh random names do today. One pending join per connection stays the real bound.

**Null token, by host:**

| Host | Null token |
| --- | --- |
| Dedicated or integrated (accounts wired) | Rejected `TokenRequired` before any prefetch. No seat, no claim, no row. |
| Persisted test host through the wire | The same, so harnesses send tokens. |
| Host with no accounts (`persistence: null`, fake repositories) | Ignored. Today's behaviour. |
| Direct two-argument `JoinSession` | Trusted, in process, not reachable from a socket. |

How existing tests keep working:
- 194 direct `new ClientJoinRequestMessage` sites compile unchanged, as `AccountToken` is optional.
- Hosts with `persistence: null` need nothing. That is the great majority.
- 23 files combine persistence with a wire join. Most go through `WireLoopbackClient.JoinAsync` (`WireLoopbackHarness.cs:301`), a one-line fix sending `TestAccountTokens.For(displayName)`.
- Four files have a direct site on a persisted host: `PersistentWorldSeedDeterminismIntegrationTests`, `StoredWorldTestSupport`, `CheckpointLeaveRaceGuardTests`, `PersistenceThroughputMeasurementTests`.
- `Wave12InventoryLoopbackTests`, `Wave13ExtractedSeamsLoopbackTests` and `Wave13PlayerWorldStateOnLeaveTests` matched by helper name only. Check them.
- A persisted test that expects a rejoin to be rejected while the old connection is live now sees a takeover, because the same name gives the same token. Give the second client a different token, or update the expectation. Do not weaken the check.

## Token File

- **Location:** `Environment.SpecialFolder.LocalApplicationData/Dream/account-tokens.json`, not next to the executable (builds wipe it, and it isn't `desktop-client.txt`). `DREAM_IDENTITY_FILE` overrides the path, for tests and for two accounts on one machine.
- **Format:** `{"version":1,"servers":{"local":"<token>","play.example.com:5057":"<token>"}}`.
- **One per server:** the key is lowercase `host:port`. A hostile server therefore can't replay your token elsewhere. A cost: `localhost` and `127.0.0.1` are two servers.
- **Integrated server:** the local player needs a token and is an ordinary loopback client; there is no bypass. The key is the literal `local` when `IsLoopbackHost(host)` and the port equals `_localServer.BoundPort` with the server ready. It survives the ephemeral port, relaunches and `Use Local Server`. The same file and code serve both.
- **Permissions:** on Unix, 0600 through `UnixCreateMode` and a 0700 directory. On Windows, the per-user directory's inherited ACL; nothing extra.
- **Writes:** temp file plus `File.Move(overwrite)`, under a process-wide named lock or a retry, so two windows can't lose an entry.
- **Corrupt file:** never overwritten. It moves aside to `.bad-<UTC stamp>`, a new store starts, and one console line says so. The token isn't logged.
- **Unwritable location:** `BeginConnect` fails with a readable message.

## Workstreams

Rules for every workstream:
- Touch only the files you own.
- The build and full suite pass. Any skip reads `wave G: needs <X>, unskip at glue` and is listed in the hand-off.
- 0 B per tick.
- Goldens and snapshot bytes stay unchanged.
- Reset the worktree to the C0 tip first (worktrees start from master), and build with an absolute `BaseOutputPath` inside the worktree.

### G1: Data and admin

**Owns:**
- new `Server/Persistence/Repositories/SqliteAccountRepository.cs`;
- `Server/Persistence/ServerPersistenceBundle.cs`;
- `Server/ServerBootstrap.cs` (`OpenWorldBundle` builds the repository; ports carry it; `IntegrityOptionsFor` unchanged);
- new `Server/Characters/CharacterCli.cs`;
- `Application/Program.cs` (a `ProcessMode.Characters` for `--characters`);
- `Server/Persistence/WorldIntegrityCheck.cs`;
- `docs/server-persistence-ddl.md`.

**Steps:**
1. Repository SQL:
   - **Lookup:** `SELECT p.player_name, p.display_name, a.token_hash FROM player_profiles AS p LEFT JOIN accounts AS a ON a.account_id = p.account_id WHERE p.display_name = $name COLLATE NOCASE;`. With no row, `SELECT EXISTS(SELECT 1 FROM player_profiles WHERE player_name = $name)`.
   - **Count:** `SELECT COUNT(*) FROM player_profiles AS p JOIN accounts AS a ON a.account_id = p.account_id WHERE a.token_hash = $hash;`.
   - **RecordJoin:** `INSERT INTO accounts (token_hash, created_utc, last_seen_utc) VALUES ($hash, $now, $now) ON CONFLICT(token_hash) DO UPDATE SET last_seen_utc = excluded.last_seen_utc;`, then, when claiming, `UPDATE player_profiles SET account_id = (SELECT account_id FROM accounts WHERE token_hash = $hash) WHERE player_name = $key AND account_id IS NULL;`. Both enlist in the ambient batch.
2. CLI, following `WorldCli`:
   - `DatabaseMaintenanceLock.EnsureSchemaGuarded`, holding the lock until the command ends;
   - `rename` also takes `TryHoldEveryWorld`, exit 7 when held;
   - `list` and `release` take no lease.
3. Integrity: `WorldIntegrityReport` gains `AccountsOverLimit` and `DuplicateDisplayNames` (both soft) and `UnownedCharacters` (information only). The last goes in the head line and **not** in `HasSoftFindings`, or every legacy database would log a finding and `RestartMidSessionMeasurementTests` would fail.
   - `SELECT COUNT(*) FROM (SELECT account_id FROM player_profiles WHERE account_id IS NOT NULL GROUP BY account_id HAVING COUNT(*) > 5);`
   - `SELECT COUNT(*) FROM (SELECT 1 FROM player_profiles GROUP BY display_name COLLATE NOCASE HAVING COUNT(*) > 1);`
   - A bad `account_id` is already fatal through `foreign_key_check` (exit 6).

**Tests:** new `SqliteAccountRepositoryTests`, `CharacterCliTests`, additions to `WorldIntegrityCheckTests`.

**Acceptance:**
- `Lookup` ignores ASCII case and returns the key, not the typed text; `OwnedByAccount` is correct.
- `RecordJoin` keeps `created_utc`, moves `last_seen_utc`, never takes an owned row, and returns false then.
- `SqlitePlayerProfileRepository.Save` leaves `account_id` alone (pinned).
- Release then rejoin works. Rename: conflict exits 2, an invalid name exits 1, a held world exits 7, case-only rename works, the key and world rows are untouched.
- `list` output never contains a hash (asserted against the row's bytes).
- `--worlds copy` and `export` leave `accounts` and `player_profiles` unchanged.
- `--db restore` of a v6 file followed by a start gives v7 with no accounts. Restoring a v7 backup brings back its accounts as they were.
- Integrity cases: 6 characters on one account; a dropped unique index with duplicates; an unowned character gives no soft finding.

**Must not touch:** `Simulation/**` (the C0 interface excepted), `Multiplayer/**`, `Client/**`, `DedicatedServerHost`.

### G3: Client

**Owns:**
- new `Client/Identity/IClientIdentityStore.cs`, `ClientIdentityStore.cs`;
- `Client/Networking/RemoteClientOptions.cs`, `RemoteClientSession.cs`;
- `Client/Startup/ClientConnectionOptions.cs`, `NetworkSessionCoordinator.cs`, `StartupConnectionScene.cs`;
- `Application/Game.cs` only where the coordinator's store is passed in;
- `Client/Scenes/PlayScene.Lifetime.cs`, `PlayScene.HudRequests.cs`, `PlayScene.Update.cs` (the `CaptureRuntime` call sites);
- `Configuration/DesktopClientConfig.cs`, `DesktopClientTextConfig.cs`.

**Contract:**

```csharp
public interface IClientIdentityStore { string GetOrCreateToken(string serverKey); }
// RemoteClientOptions: trailing string? AccountToken = null, PrintMembers redacted.
// ClientConnectionOptions.TryCreateRemoteOptions(out options, out error, string? accountToken = null)
```

**Steps:**
1. Store as specified under Token File.
2. `NetworkSessionCoordinator` takes `IClientIdentityStore? identity = null`, resolves the server key in `BeginConnect`, and passes the token. Production passes `ClientIdentityStore.ForCurrentUser()`. A null store leaves the token null, so a test coordinator never touches the real file.
3. `RemoteClientSession` sends `Options.AccountToken` at both join sites (lines 278 and 358).
4. Remove `ApplyStats` in `PlayScene.Lifetime.cs`, `ToPlayerCombatStats`, the `Strength`, `AttackSpeed` and `Vitality` config members and keys, and the stats parameter of `CaptureRuntime`. Old files still load, because unknown keys are ignored. The next save drops the keys. Update `DesktopClientTextConfigTests` and `FrameRateCapTests`.

**Tests:** new `ClientIdentityStoreTests`, additions to `RemoteClientSessionJoinTests` and coordinator tests.

**Acceptance:**
- The token is 43 valid characters, stable across instances; different servers get different tokens; `local` stays the same across changing ports.
- A corrupt file moves aside and is not overwritten. Unix mode is 0600 (Windows: skipped, and recorded). Two concurrent stores lose no entry.
- The join and its retry carry the token. `ToString` on options and the `client.tx` line hold no token.
- A client with no stored stats joins and shows the server's stats after the first correction (coordinator or applier level, not GL).

**Must not touch:** `Server/**`, `Simulation/**`, protocol files.

### G2: Join and seam (the riskiest, merges last)

**Owns:**
- `Multiplayer/Sessions/ConnectedPlayer.cs`;
- `Simulation/Runtime/WorldSessionRuntime.Sessions.cs`, `.Persistence.cs`, `.Inventory.cs`, `.Building.cs`, `RuntimeAuthoritativeSessionHost.cs`;
- `Simulation/Persistence/RuntimePersistenceBridge.cs`, `.Checkpoints.cs`, `PersistedPlayerPrefetch.cs`, `PersistenceCheckpoint.cs`;
- `Simulation/Inventory/PlayerInventoryService.cs`;
- `Server/DedicatedServerHost.cs`, `Server/JoinThrottleTable.cs`;
- `Multiplayer/Protocol/JoinRequestHandler.cs`, `ServerRoutingContext.cs`;
- `dream.Tests/Integration/WireLoopbackHarness.cs`, `dream.Tests/Load/RestartMidSessionMeasurementTests.cs`, and the persisted test fixes above.

**Steps, one commit each so the review stays readable:**
1. `CharacterKey` on `ConnectedPlayer`, and every site in correction 1 uses it. All maps in correction 4 become `Ordinal`, except the name comparers in the host, which use `CharacterNames.Comparer`. `IsProfileNameActive` compares keys. Rename `TryResolveDisplayName` use in `Building.cs` to the key. No behaviour change on accountless hosts.
2. Real stats. In `PersistPlayerProfile`, use `existing.Stats with { CurrentHealth = ..., MaxHealth = ..., Strength = ..., Vitality = ..., AttackSpeed = ..., Level = ..., Experience = ... }`, so Agility, Energy and MovementSpeed keep their loaded values.
3. Prefetch key and lookup (G-D3), plus `InvalidatePrefetches` (G-D4). `EvictCachedProfile` removes all of a name's entries.
4. `AuthorizeJoin`, `JoinSession(.., access)`, `CommitCharacterAccess`. The trusted two-argument `JoinSession` resolves the key through `Lookup`, so typing `bob` for `Bob` can't hit the unique index, and it records nothing.
5. `HandleClientMessage` pre-step, stripped token, handler order, identity claims, throttle key.

**Tests:** new `Server/CharacterJoinTests.cs`, `Server/CharacterTakeoverTests.cs`, `Integration/CharacterLoopbackTests.cs`, `Simulation/Persistence/CharacterKeySeamTests.cs`, `Server/Persistence/RealProfileStatsTests.cs`, `Multiplayer/Protocol/AccountTokenLogTests.cs`.

**Acceptance:**
- **Ownership.** Two accounts, one name: the second is always rejected, with `CharacterOwned`, in the same batch and across ticks (either session-id order). The holder keeps its seat and connection.
- **Claims.** A legacy row is claimed by the first token. A released row is claimable. With 5 owned, a sixth create or claim is rejected and writes no row. Same-batch creation of two new names by one token at 4 owned accepts exactly one.
- **Prefetch.** A pending join changes nothing for the holder. A rejected join writes nothing (no row, no account). Two tokens on one name do not livelock. The load-failure and timeout paths still give the retry reason.
- **Null and bad tokens.** Over real TCP against a production-built host (`CreateAuthoritativeHost(bundle)`): null gives `TokenRequired`, malformed gives `TokenInvalid`, no prefetch is started (repository call count 0), no seat.
- **Takeover.**
  - Same account takes over a live holder at once, keeps the profile and inventory, and leaves no ghost.
  - Other account with the victim's session id is rejected, live and after 8 s.
  - A departed session id reused by another account restores that account's character only.
  - A joined connection sending a join for another name gets `AlreadyJoined`, and its seat's key doesn't change.
- **Keys.** `Bob` (display `Bob`) and `bob` (display `bob~2`) run at once with separate profile, inventory and world state through leave, eviction and rejoin. Typing `BOB` reaches key `Bob`. No FK-failed write appears. A coalesced checkpoint keeps both rows.
- **Names.** A legacy 40-character name joins. A new 33-character name is rejected. A new name equal to a retired key is rejected and the row is untouched. Blank is rejected on account hosts and falls back on accountless ones.
- **Stats.** Agility 7, Energy 3 and MovementSpeed 4.5 set in a row survive join, save, checkpoint and restart. A full-stack default row stays 10, 10, 6.
- **Logs.** With diagnostics on, a sentinel token through join, rejection and takeover appears in no log line, no `ToString`, no routing context, no `PendingJoin`.
- **Throttle.** A spammer on the owner's address never blocks the owner.
- **Multi-world.** The same character joins world 1 and later world 2: level and base stats follow; items, position and health stay per world; a stranger is rejected in both.
- **Restart.** `RestartMidSessionMeasurementTests`:
  - add `OwnerByCharacter` and `AccountCount` to `RestartObservation`;
  - add an entry to `Invariants`: every session's character has a non-null owner, owners and account count are equal before and after, and `AccountsOverLimit == 0`;
  - in run 2, a client with another token asking for `Name restart-a` is rejected and the owner rejoins.

**Must not touch:** `ServerBootstrap`, the repositories, `Client/**`, `ClientJoinRequestMessage`, the migration.

## Merge Order And Glue

C0, G1, G3, G2 plus glue. Build and test after each merge.
- G1 first: repositories exist, and nothing reads them yet because C0's `AuthorizeJoin` default allows everything.
- G3 second: clients send a token the server still ignores.
- G2 last: it turns enforcement on and carries the largest review surface.

Glue:
1. Unskip anything marked for it.
2. Update `docs/configuration.md` from the hand-offs: Identity limits, the takeover and throttle rows, `--characters`, `DREAM_IDENTITY_FILE`, the removed stat keys, exit codes, and the same-account note for two clients on one machine (the second window with the same name closes the first; use different names).
3. Update `docs/architecture.md` (join flow) and write `docs/ledger/work-log/2026-10-31-wave-g-characters.md`.
4. Real-process flow on copies of real databases (the 3 local copies, plus any others the tester can reach):
   - start, migrate, confirm the backup exists;
   - join and claim, restart, rejoin;
   - a second client with another token is rejected;
   - `--characters release`, then the other token claims;
   - `--worlds copy` and `--db backup`;
   - a hand-written null-token client against `dream --server` is rejected.
5. Perf harness with two clients, with different names.

## Measurements And Gates

Release, `DREAM_LOAD_TEST=1`, two alternating rounds against C0. Max 2 full-suite runs per verification step; isolate flakes rather than rerun. Wave F's numbers were not repeated after its fix loops, so measure a baseline at C0 first.

| Measurement | Gate |
| --- | --- |
| Tick goldens (`WorldSessionRuntimeTickEquivalenceTests`) | unchanged |
| Tick allocation | 0 B |
| Small join | 8,492 B; bytes identical once the version digit is normalized; new SHA recorded at C0 |
| World10k join 0 | 232,677 B, identical apart from the digit |
| World10k full checkpoint | ≤ C0 × 1.05 (wave F: 38.0 ms). No checkpoint SQL changes. |
| Join tick mean | ≤ C0 × 1.10 (roadmap: 6.5 ms) |
| Join request to accepted, loopback | ≤ C0 + 1 tick median |
| Integrity check, World10k | ≤ 15 ms |
| Migration 0007 on a 10,000-profile database with 100 duplicates | ≤ 1 s (SQL alone took 25 ms in the trial) |
| Server start, World10k | no clear difference, ±2× noise |
| Full suite | 0 failed; ≥ 3,870 passed plus the new tests; 43 skips only |
| Two clients, one name, different accounts | second always rejected |
| Real-process flow in Glue | passes on every database copy |

Answers to specific checks:
- **Real Agility, Energy, MovementSpeed:** no checkpoint SQL change, because vitals don't include them. No golden change: they're outside the checksum and the runtime. Behaviour changes only for a row someone edited by hand, whose values now survive instead of resetting. The one-row-per-save volume is the same.
- **World copy and export:** no code change. Copies keep the same `player_name`s, so ownership applies in the new world. `--terrain-only` has no player rows.
- **`--db restore`:** whole file, so accounts and ownership go back with it. A character claimed since the backup is unowned again, and an account created since then is new at its next join. An older file migrates at the next start with the usual backup. `DatabaseCli` needs no change.

## Risks

1. **Two processes, one database.** Two worlds can be hosted at once, and accounts are global. A claim race can leave a session playing a character another account won: the write returns false and logs `claim-lost`, and the next join is rejected. The limit of 5 can overshoot the same way, and `AccountsOverLimit` reports it. The same character live in two worlds still overwrites the global profile, as it does today. This is not new, but U2 makes it more visible.
2. **A crash between the profile save and `RecordJoin`.** The row is unowned, and the first token claims it. Usually that's the same person.
3. **A failed claim write.** The queue drops failed writes. The player keeps playing unowned until their next join claims.
4. **Legacy names are first come, first served** until claimed. No worse than today. `release` repairs a mistake, but after release anyone may claim first, so an admin should time it with the owner.
5. **A released character's former owner, still connected,** keeps playing until they leave. A new token gets `CharacterInUse` until then.
6. **A lost token file** loses access until release. The file lives outside the install, so a reinstall keeps it, but a new computer doesn't. There is no export.
7. **Same machine, same name.** Two windows share the token file, so the second takes over the first. The default name `dream-client` makes this easy to hit. Documented, and the perf harness must set different names.
8. **`localhost` versus `127.0.0.1`** are different accounts, as are a DNS name and its IP. The player sees `CharacterOwned` on a character they think is theirs.
9. **Ordinal maps change a test assumption.** Any test relying on the cache merging `Bob` and `bob` on a fake repository will fail. Fix the test, not the comparer. The merge was the bug.
10. **Seam size.** G2 touches the inventory and persistence paths, and its first commit is mechanical but wide. Keeping it separate from enforcement is the mitigation.
11. **Duplicate renames are silent.** No log line from the migration. Only `list` and the backup show them.
12. **Accounts never get deleted.** Rows grow by one per accepted new token. This is the same growth class as profiles today.

## What Could Not Be Established

- No .NET build, test or measurement was run. The migration was only tried through Python's SQLite 3.50.4, not through `Microsoft.Data.Sqlite` or the migrator. The bundled `e_sqlite3` version wasn't checked, though window functions need only 3.25.
- Only 3 local databases were inspected. Wave F's "9 real database copies" weren't located, so I can't say whether case duplicates exist on them.
- That `foreign_keys` is on at runtime is inferred: no code sets it, and the migrator handles both states. It matters for how a mismatched `bob` write fails today, not for this plan.
- Exactly which of the 15 files matching `Equal(6,` pin the schema version, and which persisted tests expect rejection of a same-name rejoin. The first full run will show them.
- Whether any test relies on case-insensitive caches; that grep output was truncated.
- Whether `Wave12InventoryLoopbackTests` and the two `Wave13*` files join through the harness.
- Whether `SourceLayoutTests` needs entries for the new folders.
- Whether a client shows wrong stats between join and the first correction once the file stats are gone. Read-only tracing suggests the first snapshot fixes it.
- The perf harness's client names.
- Unix file mode and the lease behaviour on Linux and macOS remain unverified, as in wave F.
