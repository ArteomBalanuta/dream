# Characters, Items and Enemies: Roadmap

Purpose: give multiple players different names, stats and items, let individual items differ, and put enemies on the same system. Also place the remaining persistence Phase 5 hardening.

Status: accepted. Wave F is done. On 2026-10-31 the user accepted the recommended default for all ten decisions in section 3 (U1-U10), so they are settled as written there. Analysed read-only on `integrate/engine-iteration` at `d504d4d` (schema v6, protocol v8). Nothing has been implemented. No test was run for this analysis.

## 1. Where Things Stand

### Tables

| Table | Scope | Key | Notes |
| --- | --- | --- | --- |
| `player_profiles` | global | `player_name` | `display_name`, `interaction_mode`, `strength`, `agility`, `vitality`, `energy`, `movement_speed`, `attack_speed`, `level`, `experience`, `current_hp`, `max_hp` |
| `player_world_state` | per world | `(world_id, player_name)` | Position, facing, `current_hp`, `current_energy` |
| `item_instances` | per world | `(world_id, item_instance_id)` | `item_kind`, `display_name`, `visual_id`, nullable owner, `world_position_*`, `state`, `stack_count`. No per-instance stats. |
| `player_inventory_slots` | per world | `(world_id, player_name, slot_index)` | Backpack 0..15, equipped = 100 |
| `player_starter_grants` | per world | `(world_id, player_name)` | Kit version |
| `enemy_instances` | per world | `(world_id, object_id)` | `enemy_kind`, `display_name`, copied stat columns, `level` |
| `combat_formulas` | global | `formula_key` | 8 expressions, seeded once |
| `player_profile_change_log` | global | | Nothing writes it |

Seven tables reference `player_profiles.player_name`: `player_world_state`, `placed_cube_instances`, `cube_fracture_pieces`, `item_instances`, `player_inventory_slots`, `player_starter_grants`, `player_profile_change_log`.

### What is code and what is data

| Thing | Where | Hard-coded |
| --- | --- | --- |
| Item definitions | `Simulation/Inventory/ToolCatalog.cs` | 5 tools with `ToolStatModifier(StrengthBonus, AttackSpeedBonus, VitalityBonus)`; the starter kit (version 2); `HeldLightKind` |
| Held light values | `Rendering/Lighting/HeldLightCatalog.cs` | Client only, keyed by enum |
| Slot layout | `Simulation/Inventory/InventoryConstants.cs` | 4x4 grid, one equipped slot (100) |
| Player stat limits | `Simulation/Players/PlayerCombatStats.cs` | Strength and Vitality 1..50, AttackSpeed 0.5..5, defaults 10 / 2.0 / 10 |
| Formulas | `combat_formulas` plus the copy in `CombatFormulaService.CreateDefaultDefinitions` | Data. Min damage `5 + strength * 2`, max `10 + strength * 4`, HP `50 + vitality * 5`, crit 15%, crit damage `max_damage * 1.5` |
| Wolf numbers | `WorldSessionRuntime.Enemies.cs` `SpawnWolf` | HP 100, damage 12, interval 1.05 s, aggro 10, attack range 1.35, speed 2.8, level 1 |
| Wolf numbers again | `RuntimePersistenceBridge.cs` `CreatePersistedEnemy` | Damage is `Strength + 2`, aggro and range repeated; written rows always say Strength 10 |
| Wolf numbers a third time | `Client/Replication/RemoteEnemyStateApplier.cs` | Client copy; damage 10 and interval 1 s are placeholders there |
| Wolf body | `Simulation/Enemies/WolfDefinition.cs` | Capsule, anchor, stand-off |
| Population | `WorldSessionRuntime.Enemies.cs`, `WolfSpawnPolicy.cs` | 1 by day, 2 at night |

### Problems this work must fix or respect

- **Only Strength, AttackSpeed and Vitality are real.** `RuntimePersistenceBridge.PersistPlayerProfile` writes `Agility: 10`, `Energy: 10`, `MovementSpeed: 6f` on every save, so edits to those columns are lost. `current_energy` is always written as 0. Player speed comes from `PlayerCommands.cs` (`MoveSpeed: 6f`).
- **Level gives nothing.** No default formula uses `level`, though the variable is passed. XP and levels only count up.
- **Base stats are free sliders.** Any client can set any value in range (`PlayerCombatStats.TryValidateBaseStats`, `Render.HudPanels.cs`). There is no point budget.
- **Equipped is an alias.** Slot 100 points at an item that is still in its backpack slot. There is no unequip, move, drop, pickup or delete. `stack_count` is always 1, `state` is always `"Inventory"`, and the world-drop columns are unused.
- **Unknown item kinds throw.**
  - `ToolCatalog.Resolve` throws `KeyNotFoundException`.
  - `ResolveEquippedHeldLight` calls it from `CaptureActorSnapshots`, so one bad or retired `item_kind` on an equipped item can throw in the server's snapshot path.
  - `PlayScene.Replication.cs` `ApplyInventorySnapshot` has the same call on the client.
- **The client recomputes stats from its own catalog** (same method). Definitions that exist only on the server would break it.
- **Enemies have no template reference.** A row copies its stats, and the client draws every enemy as a wolf. The enemy snapshot carries no level or kind beyond `CreatureKind.Enemy`.
- **Enemy damage is flat and ignores the target.** There is no armor or mitigation anywhere.
- **Inventory reaches clients in only two ways:** `JoinAccepted.Inventory` and `InventoryChangedResult` as an action result. Nothing pushes an inventory change the player did not request, which loot needs.
- **No respawn code exists** under `Simulation/`; the only matches concern wolves. HP 0 is saved and restored.
- **Names.**
  - There is no length or character rule; a client name is limited only by the 64 KB frame limit.
  - In-memory maps ignore case, but SQLite `player_name = $playerName` is exact, so `Bob` and `bob` can become two profiles after a cache eviction.
  - `DisplayName` is used directly as the persistence key (`TryResolveDisplayName`).

### Identity today, and option D

- A session id is a fresh `client-<guid>` per connect and is broadcast in snapshots.
- A profile is keyed by the chosen name, and nothing proves ownership.
- Option C is in place: an 8 s takeover window and a join throttle by source and contested name. Anyone can still claim a name after its holder has left or been silent for 8 s.
- **Option D** (NETR-8, wave 10 work-log; `docs/configuration.md` Identity limits) is a per-profile reconnect token, stored on the client, sent in the join request and required to take over a profile. It needs a protocol field and a schema column.
- It was deferred waiting for the user. The wave 10 argument for doing it early ("avoids a v4") no longer applies, since the protocol is at v8.
- It bears directly on this work: "several characters per person" needs some way to say which person this is.

### Already reusable

- Migration runner with backup, `foreign_key_check` and rebuild rules.
- `ObjectId` and `ObjectIdAllocator`; item ids already come from them.
- Per-world tables, `SqliteWorldCatalog` explicit column lists, repositories and `IWorldStore`.
- `PersistenceWriteQueue`, and the join prefetch (`PersistedPlayerPrefetch`), which reads slots, items and grant in one statement.
- `InventoryWriteTracker` failed-write retention.
- `DeterministicRandom` with counter-based seeds.
- `CombatFormulaService` with startup validation; delta actor frames with optional groups (the `Equipment` pattern: not implied by `Full`, omitted when default, so bytes stay identical).
- Slot indices 100 and up in the existing table: more equipment slots need no schema change.

## 2. Target Model

### Entities

```
Account (new, global)        1..n  Character
Character = player_profiles (global): name, level, xp, base stats
  per world: player_world_state, player_inventory_slots, player_starter_grants
ItemDefinition (file)        1..n  ItemInstance (per world): rolled modifiers, stack count
EnemyTemplate (file)         1..n  EnemyInstance (per world): stats, level
LootTable (file)             referenced by EnemyTemplate
```

### Keep `player_name` as the character key

`player_profiles` becomes the character table. `player_name` stays as the immutable key and `display_name` becomes the shown, renameable name.

- This avoids rebuilding seven tables, including the two busiest, and their object-id triggers. Migration 0005 shows how much that costs.
- The tables stay `(world_id, player_name)`, so checkpoint SQL does not change.
- In code, a typed `CharacterKey` on `ConnectedPlayer` replaces `DisplayName` in every persistence path. That mix-up is the real bug class here.
- An integer id remains possible later, behind the same type.

### One stat system

New namespace `Dream.Simulation.Stats`, shared by players, enemies and items:

```csharp
public enum StatId : byte { Strength, Agility, Vitality, Energy, AttackSpeed, MovementSpeed, Armor, Count }
public enum StatOp : byte { Flat, Percent }
public readonly record struct StatModifier(StatId Stat, StatOp Op, float Value);

[InlineArray((int)StatId.Count)] internal struct StatValues { private float _first; }

public struct StatBlock { public float this[StatId stat] { get; set; } }

public sealed class StatSheet
{
    public const int SourceCapacity = 16;   // fixed slots: equipment first, then effects
    public ref readonly StatBlock Base { get; }
    public ref readonly StatBlock Effective { get; }
    public void SetBase(in StatBlock block);
    public void SetSource(int slot, ReadOnlySpan<StatModifier> modifiers);
    public void ClearSource(int slot);
}
```

**Resolution:**

```
effective = clamp((base + sum(flat)) * (1 + sum(percent)))
```

- Sources are applied in ascending slot order and modifiers in definition order, so float results are reproducible.
- With one flat source this is exactly today's `PlayerCombatStats.Combine`, including its clamps. `x * 1f` keeps the bits.
- Limits live in a static `StatRules` table, with separate base and effective ranges. Both equal today's values at launch.

**Allocation.** The sheet uses preallocated source storage. Resolution runs only when an input changes (equip, level, restore, effect start or end), never per tick, and it allocates nothing. Derived values still go through `CombatFormulaService`, which allocates a dictionary per evaluation; that is acceptable because it runs on change only. If effects later change often, its variables become a struct.

**Effects** are a later use of the same slots: a fixed source slot plus an expiry tick, checked only for sheets with an active effect.

**Checksums.**
- Effective stats stay out. They are derived, and their results already show up in health and damage.
- Base stats, level and health remain hashed as they are.
- From wave H, `ActorSnapshot` gains a `[JsonIgnore]` `ulong ModifierDigest`, computed on change from rolled instance modifiers only. Zero is not mixed in, so goldens hold for worlds without rolled items.
- Definitions are covered by a `ContentDigest`, logged at server start and treated as a lockstep input, like `TicksPerDay`.

**Rolls** use their own counter-based seed: `(simulation seed, tick, killer session hash, enemy actor id, drop index, salt)`. No stream is shared with combat, and no `ObjectId` is used, since RNG seeds use actor ids by convention. The rolled result is stored on the instance, so a later definition change never alters an existing item.

### Definitions: JSON files

Recommendation: `dream/config/content/items.json`, `enemies.json`, later `loot.json`. They ship through the existing `config\**\*` glob.

- They are authored content like the migrations and assets: reviewed in git, with no migration per balance change, the same for integrated and dedicated servers.
- They are loaded once at server start and validated strictly: unknown stat, duplicate key, bad range or missing reference stops startup.
- `combat_formulas` stays in SQL, because it already works and is editable per database.
- Instances reference definitions by string key, with no foreign key. `item_kind` and `enemy_kind` already do this, so `"Wolf"` needs no migration.
- A retired key must never crash: the item loads as an inert "Unknown item", and an enemy with an unknown kind is logged and skipped, its row kept.
- Clients do not read these files after wave H. The server sends what they need.

## 3. Decisions For The User

| Id | Question | Recommended default |
| --- | --- | --- |
| U1 | Is a person separate from a character, and may one person have several? | Yes. A person is a hidden secret kept on their computer, with no password. This is option D, widened from one token per profile to one per person. Up to 5 characters; typing a new name at connect makes a new one. |
| U2 | What follows a character between worlds? | Keep today's split: level and base stats travel with the character; items, position and health stay in each world. Making items global would mean ids shared across worlds, a much larger change. |
| U3 | Which stats exist at launch? | Strength, Attack Speed, Vitality (as today), plus Armor, which reduces damage taken. Agility, Energy and Movement Speed stay stored but do nothing yet. Movement Speed needs client prediction work. |
| U4 | Can players still pick their stats freely with sliders? | Keep them for now, as a development tool. Remove them when levels start giving stat points, which is outside this roadmap. |
| U5 | Which equipment slots? | Four: main hand, off hand, head, body. This lets a player hold a blade and a torch together, which is impossible today. Head and body have stats but no visible art. |
| U6 | Rarity and random rolls? | Yes: common, uncommon, rare. Stats are rolled once when the item is created and never change. |
| U7 | Do enemies carry items or drop loot? | Enemies do not carry real items. On death they roll a loot table, and the item goes straight into the killer's backpack. Ground drops come later. |
| U8 | What happens with a full backpack? | The loot is lost and the player is told. |
| U9 | What happens when a player dies? | Today nothing brings them back. Respawn at the world spawn after 5 s with full health, keeping all items. |
| U10 | Is real authentication out of scope? | Yes. No logins. Traffic stays unencrypted, so the secret stops casual name-stealing, not someone capturing network traffic. An admin command can release a character whose owner lost the secret. |

## 4. Roadmap

Each wave uses the team flow: at most 3 workstreams, then integrate, test and review, with up to 2 fix loops. Full-suite runs are limited to 2 per verification step. Numbers below come from the wave E work-log unless marked otherwise. Wave E baseline: 3,672 tests passing.

### Phase 5 placement

Phase 5 goes **in the first wave**, as an independent workstream, because its files do not overlap the stat work and it should land before any migration touches player data. Docs are already updated each wave, so that item is closed. The stored soak already restarts six times; the missing piece is a restart with clients connected.

### Wave F: Stats, definitions, hardening (schema 6, protocol 8)

Behaviour is unchanged. The first wave needs no user decision and ships on its own.

**F1 Stat core.** Owns: new `Simulation/Stats/*`, `Simulation/Players/PlayerCombatant.cs`, `Simulation/Players/PlayerCombatStats.cs`.
1. Add the types above.
2. Back `PlayerCombatant` with a `StatSheet`. Keep `BaseStats`, `Stats`, `DerivedStats` and `ApplyEquippedToolModifier(ToolStatModifier)` as they are, so about 20 test files are untouched.
3. Add property tests: new resolution equals old `Combine` for every tool over the whole base range, bit for bit.

**F2 Content catalog.** Owns: new `dream/config/content/*.json`, new `Simulation/Content/*`, `Simulation/Inventory/ToolCatalog.cs`, `ToolDefinition.cs`, `PlayerInventoryService.cs`, `Simulation/Runtime/WorldSessionRuntime.Enemies.cs`, `Simulation/Persistence/RuntimePersistenceBridge.cs`, `Client/Scenes/PlayScene.Replication.cs`, `Client/Replication/RemoteEnemyStateApplier.cs`.
1. `ContentCatalog` loads and validates the JSON, and computes `ContentDigest`.
2. `ToolCatalog` becomes a thin facade with `TryResolve`. All tick and snapshot paths use it, so an unknown kind no longer throws.
3. `EnemyTemplate` supplies every wolf number in the three places listed in section 1.
4. Add a test that the JSON equals today's values, including starter kit order and versions.

**F3 Hardening.** Owns: new `Server/Hosting/WorldHostLease.cs`, new `Server/Persistence/WorldIntegrityCheck.cs`, `Server/ServerBootstrap.cs`, `Server/Worlds/WorldCli.cs`, new `dream.Tests/Load/RestartMidSessionMeasurementTests.cs`.
1. Lease: a per-world OS-locked file, `{db}.world-{id}.lock`. It needs no schema, and a crash frees it. The server refuses a held world. Offline `--worlds` commands refuse too, instead of only printing a warning.
2. Backup: `--db backup <path>`, using the existing `SqliteConnection.BackupDatabase`, whole file, safe while running. Restore is an offline file swap behind `--db restore <path> --yes`.
3. Per-world restore stays out, because profiles are global and it would create orphans.
4. Integrity check on load: `foreign_key_check`, objects out of bounds, slots pointing at missing items, an equipped slot not matching a backpack item, unknown kinds. Foreign-key or corruption failures exit with new code 6; the rest are logged with counts. It takes known-kind predicates as delegates.
5. Restart test: clients connected, stop through `DisposeWithoutFinalCheckpoint`, restart, rejoin, assert invariants. Each later wave adds its own data to this test.

**Contracts.** F1 exposes `StatSheet`. F2 exposes `ContentCatalog.Shipped`, `TryGetItem(string, out ItemDefinition)`, `TryGetEnemy(string, out EnemyTemplate)`. F3 takes `Func<string, bool>` predicates; a glue commit connects them to F2.

**Merge order:** F1, F2, F3 plus glue.

**Gates:**
- Tick goldens in `WorldSessionRuntimeTickEquivalenceTests` unchanged.
- Small join SHA `40391109...b42a42` and 8,492 B unchanged; join 0 of 232,677 B identical.
- `WorldSessionRuntimeTickAllocationTests` at zero.
- World10k full checkpoint at most 36.8 ms × 1.05.
- The integrity check's cost on World10k measured, with a budget set from that measurement. Use `quick_check` only if the cost is acceptable.

### Wave G: Characters (schema 7, protocol 9)

Needs U1, U2, U10.

Migration `0007_accounts.sql`:

```sql
CREATE TABLE accounts (
    account_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    token_hash BLOB NOT NULL UNIQUE,
    created_utc TEXT NOT NULL,
    last_seen_utc TEXT NOT NULL
);
ALTER TABLE player_profiles ADD COLUMN account_id INTEGER NULL
    REFERENCES accounts(account_id) ON DELETE SET NULL;
-- Case-variant duplicates: every row but the oldest gets a suffixed display_name.
CREATE UNIQUE INDEX ux_player_profiles_display_name
    ON player_profiles (display_name COLLATE NOCASE);
```

**G1 Schema and repositories.** Owns: the migration, new `IAccountRepository` and `SqliteAccountRepository`, `SqlitePlayerProfileRepository*.cs`, `PersistedPlayerProfile.cs`, `PersistedPlayerPrefetch.cs`, `ServerPersistenceBundle.cs`, new `Server/Characters/CharacterCli.cs`, `docs/server-persistence-ddl.md`.
- Lookup by display name ignoring case returns the key.
- The prefetch also returns the character's account.
- CLI: `--characters list | release <name> | rename <old> <new>`.

**G2 Join and seam.** Owns: `Multiplayer/Sessions/ConnectedPlayer.cs`, `WorldSessionRuntime.Sessions.cs`, `.Persistence.cs`, `.Inventory.cs`, `.Building.cs`, `.Replication.cs`, `RuntimePersistenceBridge*.cs`, `PlayerInventoryService.cs`, `RuntimeAuthoritativeSessionHost.cs`, `DedicatedServerHost.cs`, `JoinRequestHandler.cs`.
1. Add `CharacterKey` to `ConnectedPlayer`, and use it in every persistence path.
2. Enforce a new-name rule: 1 to 32 characters, no control characters, not blank. Existing names stay valid.
3. Check ownership when the prefetch result is applied, since the tick never reads the database. A character owned by another account is rejected with a clear reason. A legacy character with no account is claimed by the first token that joins.
4. The same account may take over at once. Replace the name-based takeover rule for other accounts.
5. Save real values for Agility, Energy and MovementSpeed from the cached profile, not constants.

**G3 Client and protocol.** Owns: `ClientJoinRequestMessage.cs`, `TransportEnvelope.cs`, `RemoteClientOptions.cs`, `ClientConnectionOptions.cs`, `StartupConnectionScene.cs`, new `Client/Identity/ClientIdentityStore.cs`, `RemoteClientSession.cs`.
1. The client creates a 256-bit random token, one per server, and stores it in its own file, not in `desktop-client.txt`.
2. Add `AccountToken` to the join request. The server stores only its SHA-256.
3. Test that no log line or diagnostic contains the token.
4. Stop applying `desktop-client.txt` stats in `PlayScene.Lifetime.cs` if that file is free in this wave.

**Contract:** `ClientJoinRequestMessage(string RequestedSessionId, string DisplayName, string? AccountToken = null)`.

**Merge order:** G1, G3, G2 plus glue. The seam is the riskiest piece, so it merges last.

**Gates:**
- Tick goldens unchanged; checkpoint unchanged (no column touched in checkpoint SQL).
- Server-to-client join bytes differ only in the version digit.
- Join tick mean at most 6.5 ms × 1.10.
- Migration test on copies of a v6 database, with case-variant duplicates.
- Two clients sharing a name on different accounts: the second is always rejected.

### Wave H: Items with their own stats (schema 8, protocol 10)

Needs U3, U5, U6.

Migration `0008_item_rolls.sql`: `ALTER TABLE item_instances ADD COLUMN rolled_modifiers TEXT NULL`. It adds a column rather than rebuilding, so `trg_item_instances_advance_object_id` is untouched, and it avoids an extra join in `LoadPlayerInventorySql`. The value is compact versioned JSON, parsed on the persistence worker, never on the tick.

**H1 Simulation.** Owns: `PlayerInventoryService.cs`, `ToolStatModifier.cs` (removed), `InventoryConstants.cs`, new `EquipmentSlot.cs` and `ItemRoller.cs`, `PlayerCombatant.cs`, `WorldSessionRuntime.Inventory.cs`, `WorldSessionRuntime.Commands.cs`, `SimulationStateChecksum.cs`, `dream/config/content/items.json`.
1. Each equipment slot maps to index 100 + n. Slot 100 keeps its meaning.
2. Each slot feeds one `StatSheet` source.
3. An item equipped in a slot stops being an alias of a backpack slot, or stays one; settle this at the start of the wave. Whichever is chosen, migrated rows must load unchanged. The alias keeps migration free, so it is the default.
4. Move, unequip and destroy actions, validated on the server.
5. Rolls and `ModifierDigest`.

**H2 Persistence.** Owns: migration, `PersistedItemInstance.cs`, `SqliteItemRepository.cs`, `SqliteInventoryRepository.cs`, `SqliteWorldCatalog.cs`, `WorldIntegrityCheck.cs`, `docs/server-persistence-ddl.md`. The catalog's copy list needs the new column.

**H3 Protocol and HUD.** Owns: `InventoryItemSnapshot.cs`, `InventorySlotSnapshot.cs`, `PlayerInventorySnapshot.cs`, new intents, `IPlayerActionPayload.cs`, `PlayerActionHandler.cs`, `TransportEnvelope.cs`, `PlayScene.Replication.cs`, `PlayScene.HudRequests.cs`, `Render.HudPanels.cs`, `HudInteractionRequest.cs`, `HudStatusSnapshot.cs`.
1. The item snapshot carries its modifiers, held light and equip slot, so the client no longer calls `ToolCatalog`.
2. Add a character panel showing base, bonus and effective stats, and equipment slots.

**Contract:** `InventoryItemSnapshot` gains `IReadOnlyList<StatModifier> Modifiers`, `HeldLightKind HeldLight`, `EquipmentSlot? EquipSlot`. `InventoryMoveIntent(int From, int To)`, `InventoryUnequipIntent(int EquipmentSlot)`.

**Merge order:** H2, H1, H3.

**Gates:**
- Tick goldens unchanged with default items.
- Combat outcome for the starter kit identical.
- Tick allocation at zero with 4 equipped items.
- `server.join-accepted` at most 3,565 B × 1.15 for the starter kit.
- Actor snapshot bytes unchanged.
- Rolled item survives restart, world copy and export.
- Hostile intents: bad slot, another player's item, wrong slot type, flood. All rejected without state change.

### Wave I: Enemies on the same system (schema 9, protocol 11)

Needs U3, U7, U8, U9.

Migration `0009_enemy_formulas.sql`: seed `enemy.attack_damage` = `strength + 2`, `enemy.max_health`, `enemy.attack_interval_seconds`, plus a mitigation formula. Use `ON CONFLICT DO NOTHING`. Update `CombatFormulaKeys.RequiredPlayerKeys`, the allowed variables, and `CombatFormulaService.Default`. The expression engine has no function calls, so `max` floors stay in code: armor 0 skips mitigation entirely.

**I1 Enemy stats.** Owns: `EnemyCombatant.cs`, `EnemyActor.cs`, `WorldSessionRuntime.Enemies.cs`, `CombatFormulaKeys.cs`, `CombatFormulaService.cs`, `CombatFormulaSet.cs`, `RuntimePersistenceBridge.cs`, `enemies.json`, the migration, plus respawn per U9.
- Enemies gain a `StatSheet`, and player armor applies to enemy damage.
- Real stats are persisted instead of constant 10.
- Add a second template, for example a level 3 Dire Wolf using `wolf.glb`, which proves the system.

**I2 Loot.** Owns: new `loot.json`, new `Simulation/Content/LootRoller.cs`, `AuthoritativeCombatResolver.cs`, new `InventoryPushMessage.cs`, and the server routing for that message. `IAuthoritativeCombatHost` gains `GrantLoot`, called next to `AwardEnemyKillExperience`.

**I3 Wire and presentation.** Owns: `ActorSnapshot.cs`, `ActorReplicationFrame.cs`, `ActorFrameJson.cs`, `ActorSnapshotJsonConverter.cs`, `TransportEnvelope.cs`, `RemoteEnemyStateApplier.cs`, `ActorWorldLabelBuilder.cs`, `EnemyRenderer.cs`, `EnemyRenderState.cs`.
- A new optional group bit for enemy level and visual key, following the `Equipment` pattern, not implied by `Full`.
- Level beside the health bar for enemies and players, which is the `roadmap_ideas/npcs_enemies_players.txt` request.

**Merge order:** I1, I3, I2.

**Gates:**
- Wolf-only world: tick goldens unchanged. If `1.0 / attack_speed` moves the interval by one bit, keep an explicit interval in the template rather than refreshing goldens.
- Snapshot bytes unchanged for an unmodified wolf other than this group, and the size growth measured.
- Tick allocation at zero except on spawn and kill ticks.
- Kill burst of 100: write queue and tick p99 within the 1,000-write burst budget (29.2 ms).
- Loot rolls reproducible across two lockstep runs.

### Later, not committed

Ground drops and pickup, level-up stat points, timed effects, a character picker UI, stacking consumables, Movement Speed as a real stat.

**Enemy pathing.** Wolves walk in straight lines and stop at the first wall. Fix loop 5 of wave I (blocked wolf teleports home, legacy wolf migration, target shielding, rejoin protection limit) was stopped unfinished and is not in `master`. The user will address pathing later.

**Classic MMO UI.** Not scheduled, and not yet planned by the architect. Specs: `docs/superpowers/specs/2026-11-01-classic-mmo-ui/` (uncommitted); start with `00-README.md`, and `04-dream-gap-map.md` has the gap table, effort estimates and a suggested order. `06-review-findings.md` overrides the specs where they differ. Art: branch `integrate/ui-assets` at `5c2f193`, 198 sprites, not merged and not integrated with the game. That branch's base must be checked against `master` before any merge.

- **Foundation:** atlas loader, sprite and 9-slice layer, UI scale in 0.25 steps, anchored layout. ImGui is the whole UI today.
- **Blockers:** the mouse is grabbed in play; no keyboard reaches ImGui; bag items are 1x1 but icons are drawn for multi-cell footprints; equipped items alias a bag slot; crit and damage type don't reach client combat text (needs a protocol bump); per-item tint or glow needs a renderer change.
- **Missing systems:** mana, second gauge, skills, hotbar, chat, gold, combine. A licensed UI font is also needed.
- **Open decisions (user):** camera and click mode, paper-doll slot count, equipped-item rule, multi-cell bag, hide or show UI for missing systems, tier mapping, base branch.

### Order

Wave F now, then G, H, I. Each user decision set blocks only its wave. H depends on F. G is independent of H and could swap with it, but a character seam before item ownership work avoids redoing `PlayerInventoryService`.

## 5. Risks

- **Determinism goldens.** Float addition order is the main hazard, which fixed source order handles. Any golden change in F, G or H is a defect, not a refresh. Only the digit of the protocol version may change.
- **Combat balance.** Every default stays identical: same clamps (Strength 50 with a blade stays 50), same formulas, armor 0. Equipment slots with stats change balance only when new items are added.
- **Clamps limiting gear.** The cap of 50 applies to effective stats today. `StatRules` separates the two ranges, but raising the cap is a balance choice.
- **Real databases.** 0007 changes player-visible names for case duplicates. Every migration uses the existing automatic backup, and each needs a test on a v6 copy. No migration in this plan rebuilds a table.
- **First claim of a legacy name.** Until its owner connects, anyone can claim it. This is no worse than today, and `--characters release` repairs mistakes.
- **Lost secret.** A player who loses the file loses their characters until an admin releases them.
- **Checkpoint cost.** No new data goes through checkpoints: items are write-through on change, and enemy stats do not change after spawn. It should stay flat, and the gates will confirm.
- **Wire size.** Inventory snapshots are full copies, with text per item, and rolled items grow them. The H gate limits this. If it fails, send text once per definition instead.
- **Untrusted input.** Names, tokens and all inventory intents are hostile. Nothing from a client ever carries a stat value, except the slider intent kept under U4. Tokens must be compared by hash, and never logged.
- **Content as a lockstep input.** Two servers with different files diverge. `ContentDigest` makes that visible.
- **Unknown definitions.** Retiring a key must leave rows intact. F2 fixes the throw before anything can remove a key.
- **Seam size.** `DisplayName` appears in about 30 files, though only 5 sites resolve it for persistence. 53 test files reference player names, so G2 has the largest review surface.

## What Could Not Be Established

- No test or measurement was run. All numbers come from the ledger, and the gates reuse them.
- I inspected no real user database, so I do not know whether case-variant names exist there.
- I did not confirm at runtime what a dead player can do or see; I only found no respawn code.
- The inventory's exact share of the 3,565 B join message.
- Whether enemy attack interval bits change when computed by formula.
- The existing route for an unprompted per-session server message. I saw action results and snapshots but did not trace per-connection unicast, so I2 must confirm this first.
- `quick_check` cost on World10k.
- Whether `wolf.glb` can be scaled or tinted cheaply for a variant.
- Whether exit code 6 is free outside `Server/ServerBootstrap.cs`; only 3, 4 and 5 are defined there.
