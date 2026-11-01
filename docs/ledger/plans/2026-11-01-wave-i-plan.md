# Persistence Wave I Plan: Enemies On The Same System

Purpose:
- Armor reduces damage taken. Armor 0 stays identical, bit for bit.
- Enemies run on `StatSheet` and formulas and persist real stats. A second template, the level 3 Dire Wolf, proves it.
- A killed enemy rolls a loot table and the item goes into the killer's backpack, or is lost with a notice.
- A dead player stops acting and respawns at the world spawn after 5 s, with full health and all items.
- Enemy and player level show beside health. The client stops reading `ContentCatalog`.

Status: ready to implement. Analysed read-only on `integrate/engine-iteration` at `260d39e` (schema v8, protocol v10). U3, U7, U8 and U9 are settled. The migration SQL ran on in-memory SQLite 3.50.4 through Python, twice, with one hand-edited row. Float bits were computed in Python under IEEE rules. No .NET build, test or measurement was run.

## Decisions

| Id | Decision |
| --- | --- |
| U3, U7, U8, U9 | Settled, as in the roadmap. |
| I-D1 | **Armor formula.** New formula `player.damage_taken_factor` = `100 / (100 + armor)`, allowed variable `armor` only. Damage taken: Armor 0 or less returns the amount unchanged and evaluates nothing. Otherwise `Math.Max(1, (int)Math.Round(amount * factor, MidpointRounding.AwayFromZero))`. The factor is clamped to 0..1 in code (`max` floors stay in code; the engine has no function calls). Examples, wolf 12 / Dire 18: Armor 11 gives 11 / 16, 25 (best shipped gear) gives 10 / 14, 40 gives 9 / 13, 100 gives 6 / 9. |
| I-D2 | **The factor is evaluated on change, never per hit.** `PlayerCombatant` caches it and recomputes only when `EffectiveArmor` differs from the cached armor. The formula service allocates a dictionary per evaluation, and the client calls `ApplyBaseStats` on every snapshot. A hit stays 0 B. |
| I-D3 | **Mitigation lives in `PlayerCombatant.ApplyDamage`.** `EnemyActor.cs:145` doesn't change, and it's the only non-test caller. `Health.ApplyDamage` stays raw. `AppliedAmount` and the floating text show the mitigated number. |
| I-D4 | **Enemy stats are instance stats, like rolled items.** A spawn takes Strength, Vitality, Attack Speed and Level from its template into the row. A restore reads them from the row and derives damage and interval through the formulas. Max health and current health stay the row's. Aggro, attack range, body, visual and loot come from the template. A balance edit reaches new spawns only. |
| I-D5 | **Wolf numbers.** `strength: 10`, `vitality: 10`, `attackSpeed: 0.952381`, `level: 1`. `strength + 2` = 12. `50 + vitality * 5` = 100. `1.0 / attack_speed` with that float gives `1.05f` exactly: the JSON parses to `0x3F73CF3E`, today's stored `1f / 1.05f`, and the double division cast to float gives `0x3F866666`, today's interval. So no explicit interval is needed. `attackIntervalSeconds` stays as an optional template field. If the C0b bit test fails on .NET, add `"attackIntervalSeconds": 1.05` to Wolf. Never refresh a golden. |
| I-D6 | **No enemy row migration.** Every row is a wolf, and its Strength 10, Vitality 10 and attack speed already equal the real values. Agility 10 and Energy 0 stay as written. Only `CreatePersistedEnemy` changes. |
| I-D7 | **Respawn state lives on the session (`ConnectedPlayer`), not the player.** The primary `Player` is reused by successive sessions and a secondary is disposed on detach. `ConnectedPlayer` is the one object that means "this person's current seat" for both, and it already holds the inventory budget. |
| I-D8 | **One scan, no event.** Once per gameplay tick, after the enemy step, walk the primary seat then the ordered secondaries. A remote spawned session at 0 HP with no timer gets `RespawnAtTick = tick + 320`. One past due respawns. `ActorDiedEvent` is not used, see correction 2. The local seat is excluded, so an empty server's parked seat behaves as today. |
| I-D9 | **The timer isn't persisted.** `current_hp = 0` is the persisted state. A character that joins dead restores at 0 HP at its death position, and the scan gives it a fresh 5 s. Leaving can't shorten the wait, only lengthen it. A crash mid-wait gives the same result. No schema column. Existing v8 dead rows heal this way with no migration. |
| I-D10 | **A character at 0 HP in world A that first visits world B keeps arriving at full health there** (wave G's known limit stays). Health is per world (U2), and dying now costs at most 5 s and nothing else, so hopping wins nothing. Back in A it waits its 5 s. Close the known limit in the work-log as harmless. |
| I-D11 | **World spawn** is the runtime's `playerStartPosition`, stored as `_worldSpawn`. The position used is `(x, y, z)`, moving up one cell at a time while the cell or the one above holds a solid cube, capped at `Map.Height`, X and Z clamped to the map. Pure and deterministic. A map rebuild re-clamps because the clamp runs at respawn. |
| I-D12 | **While dead.** Commands: the server simulates `CreateStarvedCommandFrame` for that seat: no movement, no use, facing and mode kept, gravity still applies. Actions: `BuildUseIntent` and `CombatUseIntent` are rejected ("Action rejected because the player is dead."), after the sequence step. Inventory, stats, material select and mode change stay allowed. They can't heal (`SetMaxHealthPreservingRatio` keeps the dead dead), and respawn uses the max at that moment. Enemies already ignore dead targets. |
| I-D13 | **Respawn needs nothing new on the wire.** `IsAlive`, health and position already travel. The client counts down from the first dead snapshot's tick plus `PlayerRespawnRules.DelayTicks`. It's display only, so it may be off by a snapshot. |
| I-D14 | **The client keeps sending commands while dead,** with gameplay input off. The reconciler only re-compares on a new ack (`_lastComparedSequence`), so going silent would miss the respawn snap. Local camera stays in place. |
| I-D15 | **Killer is the session whose hit was `WasFatal`,** the same rule as kill experience. Damage is not shared and no per-enemy damage table is added. Same-tick hits resolve in ordinal session order, and the first fatal hit prunes the enemy. |
| I-D16 | **Loot is one call site:** `ResolveEnemyHit`, after `DeletePersistedEnemy`. A dawn despawn drops nothing. Delete before grant means a crash can lose a drop, never duplicate one by restoring the enemy. |
| I-D17 | **`GrantLoot` never blocks the tick.** It checks `_inventory.IsLoaded(key)` first and otherwise logs and skips, because `TryGrantRolled` → `Ensure` → `TryLoadSynchronously` can flush for 5 s. It passes the character key, never the display name. A kill by a dead player or after leaving can't reach it: actions are rejected or purged first. |
| I-D18 | **`InventoryPushMessage` is a new server message, not an action result.** Kills happen inside the tick golden, which hashes every `AuthoritativeActionResult`, so a push as a result would change it. It travels `_pendingConnectionReplies`, resolved through `_sessionClaims` (session to connection). |
| I-D19 | **Pushes coalesce per session per tick.** The kill records pending notices. The snapshot is built once at drain. Pending state is cleared on detach and capped at 8 notices per session, so a host that never drains can't grow it. |
| I-D20 | **Loss sends a notice with `Inventory = null`.** A full backpack costs about 150 B per kill, not a full snapshot. |
| I-D21 | **Dire Wolf.** Key `DireWolf`, level 3, Strength 16 (damage 18), Vitality 30 (200 HP), Attack Speed 1.0, aggro 12, attack range 1.35, speed 3.2, `wolf.glb` at 1.35× scale and tinted darker, same capsule as the wolf. |
| I-D22 | **Spawn rule.** New template field `spawnRole`: `Primary` (Wolf) or `NightExtra` (Dire). Night target stays 2, day stays 1. With no active enemy: the first `Primary` at the day spawn, as today. With one active by night: the role that's missing, at the night spawn. Dawn removes the highest owned `NightExtra`, else the highest owned id as today. A catalog with no `NightExtra` behaves exactly as today. |
| I-D23 | **Enemy wire group is omitted when default.** Group bit `EnemyPresentation = 512`, not implied by `Full`. Sent only when level isn't 1 or the visual isn't the default. A wolf-only world's actor bytes are unchanged, stricter than the roadmap's gate. An absent group means level 1, default visual. |
| I-D24 | **Level display.** World labels show `Lv 3  80/100` in the existing health line, for enemies and remote players. The local HUD already shows level beside health (`Render.HudPanels.cs:54-57`). |
| I-D25 | **Client stops reading `ContentCatalog`.** `RemoteEnemyStateApplier` drops it, since its only remaining uses are numbers the client never simulates. `NetworkSessionCoordinator` blocks only Use Local Server on a content error, not a remote connect. |
| I-D26 | **Join size with rolled gear: no gate on rolled-gear wire size, no trimming in wave I.** The worst real case is 16 rolled backpack items with 4 equipped, about 14.7 KB (estimate), against an 8 MB frame cap. Each full push needs a free slot, so one backpack produces at most 16 of them, and a full backpack sends notices only. Add a measuring gate at 16,000 B. Trim only if it fails: send `description` and `detailLines` once per kind. |
| I-D27 | **Dead remote players are hidden** (model and label), and their interpolation buffer is cleared on dead to alive, so a respawn doesn't slide across the map. |

## What The Code Does Today

**Confirmed from the roadmap:**
- `enemy_instances` holds all the needed columns. Checkpoint SQL writes position, facing, HP and level only (`SqliteEnemyRepository.Checkpoint.cs`), so stats don't touch checkpoints.
- `CombatFormulaSet` requires a key list and compiles every required key at start.
- `TryGrantRolled` returns `BackpackFull` and `NotEquippable`, and writes the item and slot in one closure.
- HP 0 restores at 0 (`ApplyPersistedHealth`, `SetAuthoritativeState`).
- `RemoteEnemyStateApplier.cs:58` reads `ContentCatalog.Shipped.Wolf`. It's the only client read besides `ClientContentCheck`.
- The `Equipment` group pattern works as described.
- Unknown enemy kinds are logged and skipped, row kept.
- `ToolCatalog` isn't used from `Client/` or `Rendering/`.
- A new world starts at 08:00, Day, so the 200-tick golden stays in Day.

**Corrections to the roadmap:**
1. **Unprompted per-session messages already have two routes.** `_pendingActionResults` is keyed by session id; `DedicatedServerService` matches it against each connection's `LastJoinRequest`. `_pendingConnectionReplies` is keyed by connection id and delivered by `DeliverPendingJoinReplies` (line 281), before the tick's snapshot and never held back by the snapshot interval. `_sessionClaims` maps session to connection. The second route fits; I-D18 says why the first doesn't.
2. **`ActorDiedEvent` fires only for the primary seat.** `RemoteSessionPlayerState` builds its `PlayerCombatant` with no event hub, and its inner `Player` gets `eventHub: null`. A secondary's death is visible only through `DamageAppliedEvent` (`ActorDied: !IsAlive`). No code subscribes for gameplay. Respawn can't hang off it.
3. **A dead player can act today.** `Player.Update` ignores health, and `PlayerSimulationInput.AllowGameplayInput` isn't read by it. `PlayerCombatant.TryStartAttack` has no alive check (the enemy's does). `ProcessCombatUse` and `ProcessBuildUseAction` have none. So the dead walk, build and kill.
4. **29.2 ms isn't a tick p99.** It's the wall time to drain the 1,000-write mix (`PersistenceThroughputMeasurementTests.WriteBurst_Throughput`, wave E). Gates below split the two.
5. **Attack damage on restore comes from the template, not `Strength + 2`.** Wave F changed this. The bridge comment explains why. I-D4 replaces it.
6. **The interval question has an answer,** I-D5. Restored rows already give `1.05f` through `1f / row`.
7. **The migration needs no enemy data change,** I-D6.
8. **There is no world spawn concept.** `(5, 1.5, 5)` is a literal at `RuntimeAuthoritativeSessionHost.cs:410`. Secondary join lanes are relative to the primary's current position. The wolf day spawn `(5.5, 1, 2.5)` is 2.5 m from it, well inside aggro 10.
9. **`EnemyRenderState.CreateWolf` already takes `float? scale`.** Scaling `wolf.glb` needs no asset or model change. `enemyFragment.glsl` has no tint, but one uniform adds it. See I3.
10. **Dead remote players are drawn standing.** `SelectVisiblePlayers` checks distance only, and `AppendRemotePlayerLabels` shows `0/100`.
11. **The tick golden has wolf kills in it,** so loot runs inside it. It stays unchanged only if the push stays out of results and actor data (I-D18). `ModifierDigest` covers equipped items only, so backpack loot doesn't touch it.
12. **`ContentCatalog.Load` has 2 arguments,** used by test fixtures. It gains a third, with the 2-argument form passing an empty table list.
13. **Merge order changes** from I1, I3, I2 to I1, I2, I3, because the wire goes into C0 and the client is the stream unit tests can't fully prove.

**Migrator facts for 0009:** backup `{db}.v0008-to-v0009.{stamp}.bak` as usual. No table is rebuilt, so the five object-id triggers stay. `foreign_key_check` passes because no foreign key is involved. A v9 file on an older build exits 3.

## Scope

**In:**
- Migration 0009, protocol v11.
- Armor mitigation; `ArmorNoEffectTests` replaced on purpose.
- `StatSheet` on enemies, four new formulas, real persisted stats, `EnemyFactory`.
- Dire Wolf, `spawnRole`, `loot.json`, `LootRoller`, `GrantLoot`, `InventoryPushMessage`.
- Respawn, dead-player rules, death overlay, loot notices.
- Enemy level and visual on the wire, level labels, visual catalog with scale and tint.
- Removing the client's `ContentCatalog` read.
- Docs.

**Out:**
- Ground drops, shared loot, damage tables, spawn protection, enemy respawn delays.
- Armor on enemies (base 0, no column, players' hits unmitigated). Percent modifiers.
- New enemy bodies or animations. Death animations, spectator camera, third-person dead pose.
- Text trimming of inventory snapshots (I-D26).
- Stat points, level effects, Agility, Energy, Movement Speed.
- Raising stat caps. Enemy content is limited to the same 1..50 range.

## Determinism And Security Rules

**Determinism**
1. Any tick golden change is a defect. Don't refresh one.
2. Loot uses counter seeds only: `ItemRollSeed(SimulationSeed, tick, StableHash.Of(session), Seed.FromInt(enemyActorId), dropIndex)`. `LootRoller` uses salt `"loot-rol"`, `ItemRoller` keeps `"item-rol"`, so picking a drop and rolling its stats share no stream. No `ObjectId` enters a seed.
3. A roll happens even when the backpack is full. Nothing downstream depends on free space.
4. Respawn counts ticks. The delay is `5 * SimulationClock.AuthoritativeTicksPerSecond` = 320.
5. The scan order is fixed: primary, then ordinal session id. It runs in the gameplay block after `_tickPlayerTargets.Clear()`.
6. `HashActor` mixes `EnemyLevel` and `VisualKey` only when non-default, so `GoldenChecksum` and pre-wave hashes hold.
7. Effective enemy stats stay out of checksums, as for players.
8. `ContentDigest` changes (format `enemies.v3`, new `loot.v1`). Expected, logged once.

**Security**
9. No client field carries a kill, loot, level, visual or respawn value. Clients can't request any of them.
10. The dead gate runs on the server for every build and combat action, queued or synthesized.
11. A rejected action changes nothing and queues no write.
12. A row is hostile on load: `StatSheet.SetBase` clamps it. A retired kind stays skipped.
13. The client treats wire data as hostile: enemy level is clamped to 1..9999 for display, an unknown or oversized `visualKey` draws the default wolf, nothing throws. Notice text goes through `TextUnformatted`, at most 8 per push.
14. A bad formula must fail at start, not on a spawn tick: `CombatExpressionCompiler` throws on a non-finite result. See I1 step 5.
15. Log lines carry counts and kinds, not item text.

## C0 Contracts

C0 is two commits on the tip, suite green after each, behaviour unchanged. After C0 nobody edits contract types except to add what a workstream section names.

**C0a: migration, formulas, protocol, content.**

```csharp
// CombatFormulaKeys, appended to RequiredPlayerKeys:
public const string EnemyAttackDamage = "enemy.attack_damage";
public const string EnemyMaxHealth = "enemy.max_health";
public const string EnemyAttackIntervalSeconds = "enemy.attack_interval_seconds";
public const string PlayerDamageTakenFactor = "player.damage_taken_factor";

// CombatFormulaSet: enemy.* use BaseContextVariables; damage_taken_factor uses ["armor"].

public readonly record struct EnemyCombatDerivedStats(int AttackDamage, int MaxHealth, float AttackIntervalSeconds);

// CombatFormulaService. Floors in code: damage >= 1, health >= 1, interval >= 0.05. intervalOverride skips its formula.
public EnemyCombatDerivedStats EvaluateEnemyDerivedStats(
    int strength, float attackSpeed, int vitality, int level, float? intervalOverride);
// armor <= 0 returns 1.0 without evaluating. Otherwise clamped to 0..1.
public double EvaluateDamageTakenFactor(int armor);
```

`CreateDefaultDefinitions` gains the four, with the same expressions as the migration.

**Respawn rule:**

```csharp
public static class PlayerRespawnRules   // Simulation/Players
{
    public const int DelayTicks = 5 * SimulationClock.AuthoritativeTicksPerSecond;
}
```

**Wire:**

```csharp
// TransportEnvelope.CurrentProtocolVersion = 11, history comment extended.

// ActorFieldGroups
EnemyPresentation = 512,            // not implied by Full
Valid = All | Equipment | EnemyPresentation

// ActorSnapshot, trailing, after ModifierDigest:
[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int EnemyLevel = 0,  // 0 means level 1
[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] string? VisualKey = null // null means the default

// ActorWireRecord, trailing: int EnemyLevel = 0, string? VisualKey = null

public readonly record struct LootNotice(string ItemDisplayName, ItemRarity Rarity, bool Lost);

public sealed record InventoryPushMessage(
    PlayerInventorySnapshot? Inventory,          // null when nothing changed (all lost)
    IReadOnlyList<LootNotice> Notices) : IServerToClientMessage
{
    public const string Kind = "server.inventory-push";
    public const int MaxNotices = 8;
    public string MessageKind => Kind;
}
```

Frame record, after Equipment: `EnemyPresentation  enemyLevel, visualKey|null`. A Full wolf stays mask 255, a Full lit player 511, a Full Dire 767, with `,3,"enemy.dire-wolf"` added. `ActorWireRecord.Full` sets the bit when either value is non-default. `Diff` sets it when either differs. Reader: a level outside 0..1,000,000 is a `JsonException`, like the held light check. `ActorSnapshotJsonConverter` writes `enemyLevel` and `visualKey` after `heldLight`, left out when default. `ActorStateCache.Entry.Apply` and `ToActorSnapshot` carry both.

`TransportJsonCodec`: add the kind to `IsServerOnlyKind`, the decode switch and `KnownMessageKinds`. `ServerConnection.EncodeServerFrame`: add `case InventoryPushMessage push:`; without it the writer throws and faults the connection. `ClientProtocolSession.CreateDefault`: add `InventoryPushMessageHandler`, which sets `context.LastInventoryPush`.

`SimulationStateChecksum.HashActor`:

```csharp
hash = (actor.EnemyLevel == 0 && actor.VisualKey is null) ? hash
    : Seed.Combine(hash, EnemyPresentationSalt, Seed.FromInt(actor.EnemyLevel),
        actor.VisualKey is null ? 0UL : StableHash.Of(actor.VisualKey));
```

Applied before the `ModifierDigest` step.

**Content:**

```csharp
public readonly record struct LootEntry(string ItemKind, int Weight);

public sealed record LootTable(
    string Key, int Drops, int NothingWeight, IReadOnlyList<LootEntry> Items,
    int CommonWeight, int UncommonWeight, int RareWeight);

public readonly record struct LootDrop(string ItemKind, ItemRarity Rarity);

public static class LootRoller
{
    // Pure. A fresh DeterministicRandom per drop from the seed and salt "loot-rol". Draws the item
    // (or nothing), then the rarity. False means nothing dropped.
    public static bool TryRoll(LootTable table, in ItemRollSeed seed, out LootDrop drop);
}

// ContentCatalog
public const string LootFileName = "loot.json";
public const string DefaultEnemyVisualKey = "enemy.wolf";
public IReadOnlyList<LootTable> LootTables { get; }
public bool TryGetLootTable(string key, out LootTable table);
public static ContentCatalog Load(string itemsJson, string enemiesJson, string lootJson);
public static ContentCatalog Load(string itemsJson, string enemiesJson);  // empty loot
```

`config/content/loot.json`, version 1, strict like the others. `drops` 1..4. Weights 0..1,000,000 with an int total, at least one positive. An entry's item must exist and have `rolls`. Duplicates and unknown properties fail at load. First-pass numbers, placeholders for a balance pass:

```json
{"version":1,"tables":[
 {"key":"wolf","drops":1,"nothingWeight":75,
  "items":[{"item":"gear.leather-cap","weight":10},{"item":"gear.leather-vest","weight":5},
           {"item":"gear.iron-blade","weight":5},{"item":"gear.ward-lantern","weight":5}],
  "rarity":{"common":85,"uncommon":13,"rare":2}},
 {"key":"dire-wolf","drops":2,"nothingWeight":50,
  "items":[{"item":"gear.leather-cap","weight":15},{"item":"gear.leather-vest","weight":15},
           {"item":"gear.iron-blade","weight":10},{"item":"gear.ward-lantern","weight":10}],
  "rarity":{"common":60,"uncommon":32,"rare":8}}]}
```

**C0a tests:**
- `EnemyFormulasMigrationTests`, below.
- Protocol pins 10 to 11, in the same files wave H listed. Schema pins 8 to 9: `SchemaMigrationTests` (also check line 81), `ItemRollsMigrationTests`, `ItemRollsMigrationMeasurementTests` and every `ShippedLatestVersion` literal.
- The 6 files that build formula lists get a shared helper with the 4 keys: `SqliteStoreTests`, `WorldRoundTripCharacterizationTests`, `CombatFormulaServiceTests`, `AuthoritativeCombatResolverTests`, `PlayerCombatStatTests`, `RuntimeAuthoritativeSessionHostTests`.
- `LootRollerTests`: same seed same drop, drop index differs, weights hold over 100,000 seeds within 1%, nothing-weight 0 always drops, golden picks.
- `ContentCatalogTests`: loot validation, digest changes with a weight.
- `InventoryPushCodecTests`: round trip, refused from a client without decoding, reaches the wire through a real `ServerConnection`.
- `DeltaActorSnapshotTests`: wolf bytes unchanged, group round trip, keyframe and delta, a hostile level and an oversized key.
- Bytes stay put: v11 has the same digit count, so `HeldLightSnapshotBytesTests` stays 58,307 B and join sizes don't move.

**C0b: enemy stat core, mechanical, behaviour unchanged.**

```csharp
public enum EnemySpawnRole : byte { Primary = 0, NightExtra = 1 }

public sealed record EnemyTemplate(
    string Key, string DisplayName, int Level,
    int Strength, int Vitality, float AttackSpeed,
    float? AttackIntervalSeconds,
    float AggroRange, float AttackRange, float MoveSpeed,
    string VisualKey, string? LootTable, EnemySpawnRole SpawnRole);

// EnemyCombatant: new trailing StatBlock? baseStats = null, and
public StatSheet StatSheet { get; }
// EnemyActor: new trailing string? visualKey = null, and
public string? VisualKey { get; }   // null is the default visual

internal static class EnemyFactory   // Simulation/Enemies
{
    public const int DefaultAgility = 10;   // so rows stay byte-equal
    public const int DefaultEnergy = 0;
    public static EnemyActor Spawn(EnemyTemplate template, Vector3 position, int actorId,
        SceneEventHub events, CombatFormulaService formulas);
    public static EnemyActor Restore(in PersistedEnemyInstance row, EnemyTemplate template, int actorId,
        SceneEventHub events, CombatFormulaService formulas);
}
```

- `enemies.json` version 2: `maxHealth` and `attackDamage` go. `strength`, `vitality`, `attackSpeed`, `visualKey`, `spawnRole` are required. `attackIntervalSeconds`, `lootTable` are optional. Strength and Vitality 1..50, Attack Speed 0.5..5, a level of 1 or more. Wolf: `"visualKey":"enemy.wolf"`, `"spawnRole":"Primary"`, `"lootTable":"wolf"`, numbers from I-D5. A reference to a missing table fails at load. At least one `Primary` is required (Wolf already is).
- `SpawnWolf` and the bridge's `CreatePersistedEnemy` call `EnemyFactory`, removing the duplicate. `IRuntimePersistenceHost` gains `CombatFormulaService CombatFormulas { get; }`.
- `CreatePersistedEnemy(EnemyActor, ...)` reads Strength, Agility, Vitality, Energy and Attack Speed from `StatSheet.Base`.
- `CaptureActorSnapshots` sets `EnemyLevel: actor.Level == 1 ? 0 : actor.Level`, `VisualKey: actor.VisualKey`. The factory maps `DefaultEnemyVisualKey` to null.

**C0b acceptance:**
- Tick goldens unchanged.
- A wolf's row equals today's in every column, by bits (`EnemyObjectIdPersistenceTests`, `IncrementalCheckpointTests` unweakened).
- `WolfIntervalBitsTest`: `0x3F866666` from a fresh spawn and from a restored row. Attack damage 12, max health 100.
- `UnknownEnemyKindRestoreTests` pass.

## Migration 0009 (`dream/config/migrations/0009_enemy_formulas.sql`, in C0a)

```sql
-- Migration 0009: enemy formulas and armor (wave I). Formulas seeded once; a row already present
-- (edited by hand) is kept. No table is rebuilt and no enemy row changes: every stored wolf's
-- strength, vitality and attack speed are already its real values.
INSERT INTO combat_formulas (formula_key, expression_text, description, enabled)
VALUES
    ('enemy.attack_damage', 'strength + 2', 'Enemy attack damage.', 1),
    ('enemy.max_health', '50 + vitality * 5', 'Enemy maximum health.', 1),
    ('enemy.attack_interval_seconds', '1.0 / attack_speed', 'Seconds between enemy attacks.', 1),
    ('player.damage_taken_factor', '100 / (100 + armor)', 'Share of incoming damage a player takes.', 1)
ON CONFLICT(formula_key) DO NOTHING;
```

Trial on in-memory SQLite 3.50.4: new keys inserted, a second run changed nothing, a hand-edited `enemy.max_health` kept its value, `enemy.experience_reward` untouched.

**`EnemyFormulasMigrationTests`:**
- every version 0 to 8 migrates to 9, `integrity_check` ok, `foreign_key_check` empty;
- all 5 object-id triggers survive;
- every `enemy_instances` row is byte-equal before and after;
- a hand-edited formula stays;
- 12 formula rows on a fresh database, and they compile in `CombatFormulaSet`;
- a `v0008-to-v0009` backup exists; a forced failure rolls back to 8; a v9 file on a v8 build exits 3.

## Workstreams

Rules for every workstream:
- Touch only the files you own.
- The build and full suite pass. Any skip reads `wave I: needs <X>, unskip at glue` and is listed in the hand-off.
- 0 B per tick except spawn and kill ticks.
- Goldens and actor bytes stay unchanged.
- Reset the worktree to the C0 tip first (worktrees start from master), and build with an absolute `BaseOutputPath` inside the worktree.
- Max 2 full-suite runs per verification step.

### I1: Armor and the Dire Wolf

**Owns:**
- `Simulation/Players/PlayerCombatant.cs`;
- `Simulation/Runtime/WorldSessionRuntime.Enemies.cs`;
- `dream/config/content/enemies.json`;
- `Server/ServerBootstrap.cs`;
- `dream.Tests/Simulation/Runtime/ArmorNoEffectTests.cs` (renamed `ArmorMitigationTests.cs`);
- `docs/server-persistence-ddl.md`.

**Contract:**

```csharp
public double DamageTakenFactor { get; }     // PlayerCombatant; 1.0 at Armor 0
public int MitigateDamage(int amount);       // I-D1, pure
```

**Steps:**
1. Add a `_factorArmor` cache to `PlayerCombatant`. In `DeriveFromSheet`, recompute only when `EffectiveArmor` changed.
2. `ApplyDamage` calls `Health.ApplyDamage(MitigateDamage(amount))`. Amounts of 0 or less pass through.
3. Add the Dire Wolf to `enemies.json` per I-D21: `"visualKey":"enemy.dire-wolf"`, `"spawnRole":"NightExtra"`, `"lootTable":"dire-wolf"`.
4. Change `SpawnWolf(Vector3)` to `SpawnEnemy(EnemyTemplate, Vector3)`. Implement I-D22 in `EnsureWolfExists`, keeping its name, its one change per tick and its allocation rule. Count roles in the existing scan, reading the role from a template lookup only on spawn or despawn ticks; store a `bool IsNightExtra` on nothing new if the kind comparison already suffices.
5. Startup probe in `ServerBootstrap`: for every template evaluate `EvaluateEnemyDerivedStats`, and evaluate `EvaluateDamageTakenFactor` for armor 1..1000. A failure exits 8 with the formula's key, before the world opens.
6. Replace `ArmorNoEffectTests` deliberately, keeping its helpers. Update the DDL doc: formulas, I-D4, I-D6, schema 9.

**Tests:** `ArmorMitigationTests`, additions to `WaveT2NightSpawnTests`, new `DireWolfPersistenceTests`, `StartupFormulaProbeTests`.

**Acceptance:**
- **Armor 0 is identical:** over every wolf and Dire damage from 1 to 1000, `ApplyDamage` equals raw.
- **Armor:** the table in I-D1 holds. Damage 1 at Armor 1000 still does 1. The wolf-bite run from the old test gives Armor 40 strictly more health left than Armor 0.
- **No allocation:** 0 B on a hit with Armor 40; the factor evaluates once per armor change. 100 `ApplyBaseStats` calls do not evaluate it.
- **`EnemyActor.cs:145` unchanged.**
- **Population:** day one wolf. Night adds one Dire at the night spawn. Kill the wolf at night: next spawn is a wolf. Kill the Dire: next is a Dire. Dawn removes the Dire even with a higher-id wolf. Two lockstep runs across nightfall and dawn match every tick. With a test catalog with no `NightExtra`, the same run spawns only wolves and matches pre-wave counts.
- **Dire stats:** 200 HP, 18 damage, interval 1 s, level 3, XP 42.
- **Dire persistence:** a Dire survives restart, `--worlds copy` and export with its row's stats. Editing the template leaves a restored Dire's stats unchanged and changes the next spawn. A retired `DireWolf` key is skipped with its row kept and counted by the integrity check; a wolf fills the slot.
- **Probe:** a formula dividing by zero exits 8 at start, never on a tick.

**Must not touch:** `Client/**`, `Rendering/**`, `Multiplayer/**`, `Server/DedicatedServerHost.cs`, `AuthoritativeCombatResolver.cs`.

### I2: Respawn and loot

**Owns:**
- `Simulation/Runtime/WorldSessionRuntime.Commands.cs`, `.Combat.cs`, `.Sessions.cs`, `WorldSessionRuntime.cs`;
- new `Simulation/Runtime/WorldSessionRuntime.Respawn.cs`, `WorldSessionRuntime.Loot.cs`;
- `Simulation/Runtime/RemoteSessionPlayerState.cs`;
- `Simulation/Combat/AuthoritativeCombatResolver.cs`;
- `Multiplayer/Sessions/ConnectedPlayer.cs`;
- `Server/DedicatedServerHost.cs`;
- `dream.Tests/Load/RestartMidSessionMeasurementTests.cs`.

**Contract:**

```csharp
// ConnectedPlayer
internal ulong RespawnAtTick;           // 0: no timer

// IAuthoritativeCombatHost, resolver calls it after DeletePersistedEnemy:
void GrantLoot(PlayerSessionId killer, string enemyKind, int enemyActorId, ulong tick);

// WorldSessionRuntime
internal readonly record struct SessionInventoryPush(PlayerSessionId SessionId, InventoryPushMessage Message);
internal bool HasPendingInventoryPushes { get; }
internal void DrainInventoryPushesInto(List<SessionInventoryPush> destination);
```

**Steps:**
1. **Spawn.** The constructor stores `_worldSpawn = playerStartPosition`.
2. **Scan.** `.Respawn.cs`: `AdvanceRespawns()`, called in the gameplay block right after `_tickPlayerTargets.Clear()`, per I-D8. It checks `IsAlive` first, so a live player costs one property read. `_primaryPlayerTarget` and the `_orderedSecondaryRemotePlayers` array, indexed, no list.
3. **Respawn.** `Combatant.Health.SetAuthoritativeState(max, max)`, `target.SetWorldPosition(ResolveRespawnPosition())`, clear the timer. Facing stays. No extra write: the next checkpoint or leave saves it.
4. **Dead commands.** In `UpdateAcceptedCommand`, a dead primary simulates `CreateStarvedCommandFrame(commandFrame)`. In `RemoteSessionPlayerState.Simulate`, the same for a dead seat.
5. **Dead actions.** In `ProcessAuthoritativeAction`, after the sequence step, reject build and combat per I-D12.
6. **Detach.** `DetachRemoteSession` clears `RespawnAtTick` and pending loot.
7. **Loot.** `GrantLoot` in `.Loot.cs`: get the character key, return with a log line unless `IsLoaded`. Look up the template then table; none means no loot. For `dropIndex` in 0..Drops-1: build the seed, `LootRoller.TryRoll`, `TryGrantRolled(key, kind, rarity, seed)`. `Equipped` adds a gained notice and marks the session changed. `BackpackFull` adds a lost notice. Other outcomes log.
8. **Resolver.** Calls `_host.GrantLoot` per I-D16. No other change.
9. **Host.** At the end of `RunTick`'s tick work: if `runtime.HasPendingInventoryPushes`, drain into a reused list and enqueue `PendingConnectionReply` via `_sessionClaims`. A session with no claim drops it.

**Tests:** new `Simulation/Runtime/PlayerRespawnTests.cs`, `DeadPlayerActionTests.cs`, `LootGrantTests.cs`, `LootDeterminismTests.cs`, `Integration/LootPushLoopbackTests.cs`, `Integration/RespawnLoopbackTests.cs`, `Simulation/Runtime/RespawnAllocationTests.cs`.

**Acceptance:**
- **Timer:** death at tick T respawns at exactly T + 320, on primary and secondary seats alike, with both in two lockstep runs. Position is the spawn, health the max at that moment, all slots unchanged.
- **Equal across restart:** a row at 0 HP restores dead at its position and respawns 320 ticks after the join; a leave at tick T + 100 and rejoin gives a fresh 320. Kill the process mid-wait: same.
- **Worlds:** a character dead in A arrives full in B, and in A again waits 320 ticks (test on two worlds in one database).
- **Blocked spawn:** a cube at the spawn cell lifts the player to the first free cell. A column full to the top doesn't throw.
- **Dead:** movement, jump, attack and build do nothing. A queued build and combat action reject with no state or write change. Same tick: killed by the wolf, then its own use rejects. Equip works; a vest equipped while dead stays at 0 and respawn gives the new max. No enemy targets a dead player.
- **Local seat:** a dead local seat is never respawned.
- **Loot:** on a real repository, a kill grants a rolled item with owner = character key, including a renamed `bob~2`. Survives restart and stays byte-equal. Two lockstep runs give the same kinds, rarities and modifiers. A second hitter gets no item or XP. Drop index 0 and 1 of a Dire differ. Dawn despawn drops nothing. A full backpack adds no row, sends a lost notice with `Inventory` null, and loses no existing item. Inventory not loaded: logged, no flush on the tick.
- **Push:** over a real connection only the killer receives it, before that tick's snapshot. Two kills in one tick give one push with two notices. It's absent from `AdvanceTick`'s results. Detach clears pending. Unit runs with no drain stay bounded.
- **Allocation:** 0 B per tick with a dead session waiting; 0 B on non-kill ticks with loot enabled.
- **Goldens:** unchanged, kills included.
- **`RestartMidSessionMeasurementTests`:** add `LootByCharacter` and `HealthAfterRespawn` to the invariants: a character dead at the mid-session stop is alive with full health after its wait in the next run.

**Must not touch:** `Client/**`, `Rendering/**`, `Multiplayer/Protocol/**`, `Simulation/Content/**`, `PlayerInventoryService.cs`, `PlayerCombatant.cs`, the migration.

### I3: Client (merges last)

**Owns:**
- `Client/Replication/RemoteEnemyStateApplier.cs`;
- `Client/Presentation/ClientScenePresenter.cs`, `RemotePlayerPresentationState.cs`, `RemotePlayerInterpolationBuffer.cs`, `WorldSessionPresentationState.cs`;
- new `Client/Presentation/ClientDeathTracker.cs`;
- `Client/Scenes/PlayScene.Replication.cs`, `PlayScene.Update.cs`, `PlayScene.Render.cs`, `PlayScene.cs`;
- `Client/World/ClientWorldSession.cs`;
- `Client/Startup/NetworkSessionCoordinator.cs`, `ClientContentCheck.cs`;
- `Application/Game.cs`;
- `Rendering/Actors/ActorWorldLabelBuilder.cs`, `EnemyRenderer.cs`, `EnemyRenderState.cs`, `RemotePlayerRenderer.cs`;
- new `Rendering/Actors/EnemyVisualCatalog.cs`;
- `Rendering/Core/Render.HudPanels.cs`;
- `Rendering/Hud/HudStatusSnapshot.cs`, `CharacterPanelModel.cs`;
- new `Rendering/Hud/LootFeedbackSystem.cs`;
- `shaders/enemyFragment.glsl`.

**Contract:**

```csharp
public readonly record struct EnemyVisual(string ModelPath, float ScaleMultiplier, Vector3 TintOffset);
public static class EnemyVisualCatalog
{
    public static EnemyVisual Resolve(string? visualKey);   // unknown or null: the wolf
}
// "enemy.wolf": 1.0, offset 0. "enemy.dire-wolf": 1.35, offset (-0.45, -0.45, -0.35).

public sealed class ClientDeathTracker
{
    public void Observe(bool isAlive, ulong serverTick);
    public void Reset();
    public bool IsDead { get; }
    public int SecondsRemaining(double estimatedServerTick);   // whole seconds, rounded up, >= 0
}

// RemotePlayerPresentationState: trailing int Level = 1
// HudStatusSnapshot: trailing bool IsDead = false, int RespawnSeconds = 0
// ActorHealthLabelCache.Get(int level, int current, int max); key includes level
// ActorWorldLabelBuilder.FormatHealthLabel(int level, int current, int max) gives "Lv 3  80/100"
```

**Steps:**
1. `RemoteEnemyStateApplier.CreateEnemy`: drop `ContentCatalog`, keep the two placeholders, add local constants for the unused range and speed numbers, pass `level: Math.Clamp(actor.EnemyLevel, 1, 9999)` and `visualKey`.
2. Remote connect no longer checks content. `Game` still runs `ClientContentCheck`, and the coordinator applies the error only to Use Local Server (`BeginConnect` and `RefreshIdleState` stop blocking on it). The in-process server reads the catalog through `ServerBootstrap`.
3. Enemy visuals: `EnemyRenderer` resolves `EnemyVisualCatalog` per enemy, passes `WolfAssetDefinition.VisualScale * ScaleMultiplier` to `CreateWolf`, and sets `uActorTintOffset`. It sets it for every enemy and back to zero at the end of the draw, because the player renderers share this shader. The shader multiplies base colour by `vec3(1.0) + uActorTintOffset`, so an unset uniform changes nothing.
4. Labels: `RemotePlayerEntry` keeps `Level` from `actor.Progression`, `AppendEnemyLabels` uses `enemy.Level`.
5. Dead remote: hide in `SelectVisiblePlayers` and `AppendRemotePlayerLabels`. In `ApplySnapshot`, clear the buffer on dead to alive. `RemotePlayerInterpolationBuffer` gains `Clear`.
6. Local dead: in `Update`, `allowGameplayInput &&= _runtime.Player.Combatant.Health.IsAlive`. Commands keep flowing (I-D14). `PlayScene.Replication` calls `ClientDeathTracker.Observe` after each snapshot and `Reset` on join. The join's own actor list is observed too, so a join while dead shows the overlay at once.
7. Overlay in `Render.HudPanels`: centred "You died", then "Respawning in N s", or "Respawning" at 0. `TextUnformatted`, cached strings, no allocation per frame.
8. Push: `PlayScene.ApplyReplicatedMessage` gains an `InventoryPushMessage` branch: `ApplyInventorySnapshot` when non-null, then notices to `LootFeedbackSystem`. That holds 4 lines for 4 s: "Looted: Leather Cap (Rare)", "Backpack full: Leather Cap was lost".
9. `CharacterPanelModel.ArmorNote` becomes "reduces damage taken".

**Tests:** additions to `RemoteEnemyStateApplierTests`, `ClientScenePresenterTests`, `ActorWorldLabelAllocationTests`, `EnemyRenderStateTests`, `CharacterPanelModelTests`; new `Client/Presentation/ClientDeathTrackerTests.cs`, `Client/Scenes/PlaySceneInventoryPushTests.cs`, `Client/Startup/NetworkSessionCoordinatorContentTests.cs`, `Rendering/Actors/EnemyVisualCatalogTests.cs`, `Rendering/Hud/LootFeedbackSystemTests.cs`.

**Acceptance:**
- `using Dream.Simulation.Content` is gone from `RemoteEnemyStateApplier`. With a bad content directory, a remote connect succeeds, Use Local Server shows the error, and enemies appear.
- A wire enemy with level 3 and `enemy.dire-wolf` reaches `EnemyActor`, label and render state. An unknown key, a 10,000-character key and level `int.MaxValue` draw the default wolf with a clamped level and no throw.
- Labels: wolf `Lv 1  100/100`, Dire `Lv 3  200/200`, remote player with their level. Allocation pins hold (0 B on a repeat frame).
- The default wolf's render state equals today's matrix and clip.
- Dead: a held key sends no movement or use, commands keep their sequence, and respawn snaps through the reconciler across any distance, with a test where the ack arrives after the teleport. On respawn the remote avatar doesn't slide.
- Tracker: 5, 4, ... 1 then 0 from a fixed tick; a join while dead; a respawn snapshot clears it; a stale old-tick snapshot doesn't reopen it.
- A push never changes health or position. A hostile push with `1e99` modifiers and 40 notices doesn't throw and shows at most 8.
- Inventory text keeps `%` literal.

**Must not touch:** `Server/**`, `Simulation/**` (all of it), `Multiplayer/**`.

## Merge Order And Glue

C0a, C0b, I1, I2, I3 plus glue. Build and test after each merge.
- I1 first: it changes numbers and the population, and nothing needs the rest.
- I2 second: the server drives the wire C0 defined. A client without I3 still receives and ignores pushes.
- I3 last: it depends on what I2 sends and is the only stream that needs a real client to prove.

Glue:
1. Unskip anything marked for it.
2. `EnemyLootEndToEndTests`: start, join over TCP, kill a Dire at night, receive a rolled item, equip it, take reduced damage, die, wait, respawn at spawn with the item, restart, rejoin, `--worlds copy`, join the copy.
3. `KillBurstMeasurementTests`, below, Release, `DREAM_LOAD_TEST=1`.
4. Worst-case push and join size test: 16 rolled, 4 equipped.
5. Update `docs/configuration.md` (loot.json, exit 8 for formulas), `docs/architecture.md` (armor, respawn, loot route, enemy wire group). Write `docs/ledger/work-log/2026-11-01-wave-i-enemies.md`, closing wave G's hop limit and wave H's "inventory actions while dead were not run over real TCP".
6. Real-process flow on copies of the 4 local `legacy-e` databases: start migrates to v9 with a backup; a stored dead character respawns on join; two clients, one sees the other vanish and return without sliding; real loot arrives; a full backpack is told; a hand-written hostile client sends build and combat while dead and is rejected.
7. Perf harness with two clients, screenshots of the Dire Wolf, labels and death overlay for a person to judge. Interactive feel has to be checked by the user.

## Measurements And Gates

Release, `DREAM_LOAD_TEST=1`, two alternating rounds against C0b. Measure the baseline at C0b. Max 2 full-suite runs per verification step; isolate flakes rather than rerun.

| Measurement | Gate |
| --- | --- |
| Tick goldens (`WorldSessionRuntimeTickEquivalenceTests`) | unchanged; no refresh |
| `SimulationStateChecksumTests` goldens | unchanged |
| Wolf interval bits | `0x3F866666`, spawned and restored |
| Armor 0 combat | identical for every amount 1..1000 |
| Tick allocation | 0 B, including a dead session waiting and 4 equipped rolled items; only spawn and kill ticks allocate |
| Hit with Armor > 0 | 0 B |
| Actor snapshot bytes, wolf-only | identical: `NoLight_200Ticks` 58,307 B |
| Actor bytes with a Dire | growth measured; at most 24 B on each Full record, none on a delta |
| `server.join-accepted`, starter kit | 4,048 B, gate 4,099 B |
| Small join | identical once the digit is normalized |
| World10k join 0 | identical to C0b |
| Join with 9 items, fully rolled | 9,400 B, unchanged |
| Worst join and push, 16 rolled and 4 equipped | ≤ 16,000 B. Estimate about 14.7 KB. If it fails, I-D26 |
| Lost-only push | ≤ 256 B |
| World10k full checkpoint | ≤ C0b × 1.05 (wave F 38.0 ms, wave H tester 1.02×). No checkpoint SQL changes |
| Join-window total | ≤ C0b × 1.10 |
| Integrity check, World10k | ≤ 15 ms (wave H 7.1 ms) |
| Migration 0009, any database | ≤ 50 ms |
| Kill burst: 100 kills with a 100% drop test table, 4 writes each | queue back to 0 pending within 100 ms of the last kill; 0 failed writes; no item lost |
| 1,000-write drain | ≤ 29.2 ms × 1.10 = 32.1 ms, no new mix |
| Tick p99 during that burst | ≤ 15.6 ms (one tick), and ≤ C0b fight-only p99 × 1.25 |
| Loot reproducibility | same kinds, rarities and modifiers across two lockstep runs |
| Respawn tick | exactly death + 320 across two lockstep runs |
| Hostile intents while dead | rejected, no state change, over real TCP |
| Full suite | 0 failed; ≥ 4,421 passed plus the new tests; 43 skips only |
| Real-process flow in Glue | passes on every database copy |

If the kill burst fails the tick gate, first check `Ensure`'s per-kill list, then coalesce the profile save. Don't raise a gate without recording why.

## Risks

1. **Spawn camping.** The wolf day spawn is 2.5 m from the world spawn, and the population step replaces a dead wolf one tick later. A weak player can die repeatedly. See review item 3.
2. **Loot farming.** A strong player can kill a wolf per attack (5 per second at the cap) at the spawn point, since there is no enemy respawn delay. The backpack bounds it at 16 items.
3. **Respawn overlap.** Two players dying together land on one spot and rely on Bullet to separate them, deterministically but maybe roughly.
4. **`enemyFragment.glsl` is shared with players and compiled twice.** The offset form keeps an unset uniform harmless. The MASK variant needs the same path. Forgetting the reset darkens players.
5. **The factor cache goes stale** if a new path changes armor without `DeriveFromSheet`. `ApplyEquipment` is the only source today.
6. **Fixed 5 s on a stalled client.** Its ack may lag, so the snap arrives with the next accepted command, not the tick of respawn. The server position is already right.
7. **Tests that keep a dead remote player for more than 320 ticks** will now see it return. Advance fewer ticks; don't add a test bypass.
8. **The Dire changes night balance:** 18 damage per second against 100 HP. Combat stays identical at Armor 0 for wolves, not for the night slot.
9. **`EnemyLevel` 0 meaning level 1** is a wire sentinel. Read through one clamp on the client.
10. **A push before the client finishes a world transfer.** It would need a kill by a session still loading. The client applies messages in order after the join, but this wasn't traced through chunk handling.
11. **Editing a formula changes live numbers on the next start,** and a hostile `combat_formulas` row can make damage odd. The floors and the probe bound it.
12. **Placeholder numbers** for loot weights and the Dire.

## What Could Not Be Established

- No .NET build, test or measurement was run. Byte and millisecond figures are from the ledger or hand estimates. The 14.7 KB worst case is derived from 4,048 B and 9,400 B: about 765 B per extra entry.
- The interval bits were computed in Python. The C0b test proves them on .NET.
- The migration ran only on Python's SQLite 3.50.4, on a reduced `combat_formulas` table.
- The tip's passing test count after wave H's fix loops; 4,421 is the last logged figure, before them.
- `SchemaMigrationTests.cs:81`, whether that 8 counts formulas, and which `Equal(8,` matches pin the schema.
- Which fixtures in `ContentCatalogTests` carry their own enemy JSON, and how many enemy-template tests need new fields.
- How many tests expect a dead player to move, or stay dead for more than 320 ticks.
- `ClientJoinAcceptedApplier`'s handling of a local player joining at 0 HP; I-D14's overlay relies on the first snapshot at worst.
- How the third-person `LocalPlayerRenderer` draws a dead local player.
- Whether a kill-burst test can reach 5 kills per second through real attack intervals, or needs server actions (`TryEnqueueServerAction`).
- The bundled `e_sqlite3` version.
- How the Dire Wolf's scale and tint look, the overlay, and the mirrored off-hand pose and panel from wave H, which nobody has seen on screen.
- Whether the perf harness's clients die, so whether its numbers exercise respawn.

## Choices For The User To Review

No open question blocks the start. Defaults are in the decisions above.

1. **Killing blow takes all (I-D15).** A player who did 95% of the damage gets nothing. Sharing needs a per-enemy damage table.
2. **The Dire Wolf takes the night slot (I-D22).** Night gets harder for everyone. The alternative is a third night enemy, or a rarer chance roll, which needs a spawn seed.
3. **No spawn protection (Risk 1).** A few seconds of invulnerability, or moving the world spawn away from the wolf, would stop death loops. Moving the spawn changes new-player positions.
4. **No enemy respawn delay (Risk 2).** The existing one-tick replacement now feeds loot.
5. **Rejoining while dead restarts the 5 s (I-D9).** Persisting the remaining ticks would need a column and buys little.
6. **Inventory stays usable while dead (I-D12).**
7. **Wolf ships without an explicit interval (I-D5).** It reads the formula, so an edit to `enemy.attack_interval_seconds` also moves wolves. Add the explicit 1.05 if wolves should stay fixed.
8. **Enemy stats stay as stored until the enemy dies (I-D4).** A balance edit reaches new spawns only.
9. **First-pass loot and Dire numbers** are placeholders.
10. **No inventory text trimming yet (I-D26).**
