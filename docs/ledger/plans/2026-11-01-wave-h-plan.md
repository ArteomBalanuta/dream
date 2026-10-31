# Persistence Wave H Plan: Items With Their Own Stats

Purpose:
- An item instance can carry rolled stats and a rarity, fixed when it is created.
- A player has four equipment slots: main hand, off hand, head, body. A blade and a torch can be held together.
- Live stats are Strength, Attack Speed, Vitality and Armor. In wave H Armor is resolved and shown, but it changes no combat result.
- Items can be moved, unequipped and destroyed. All of this is validated on the server.
- The client stops using the item catalog for stats and lights, and shows a character panel.

Status: ready to implement. Analysed read-only on `integrate/engine-iteration` at `50cfab7` (schema v7, protocol v9). U3, U5 and U6 are settled. The migration SQL was run on in-memory SQLite 3.50.4 through Python, on a reduced table with the item trigger. No .NET build, test or measurement was run.

## Decisions

| Id | Decision |
| --- | --- |
| U3, U5, U6 | Settled, as in the roadmap. |
| H-D1 | **The alias stays.** Slots 100..103 hold the id of an item that also has a backpack slot. Migrated rows load unchanged, because slot 100 keeps its meaning and is the main hand. One item may be in at most one equipment slot. |
| H-D2 | **Move, unequip and destroy under the alias.** Move touches backpack slots 0..15 only (move or swap). Equipment rows follow by item id, so an equipped item stays equipped when moved. Unequip deletes the 100+n row. If that row is a legacy item with no backpack row, the item first goes to the lowest free backpack slot; a full backpack rejects with `BackpackFull`, so the item is never lost. The same rescue applies to an item displaced by an equip. Destroy is rejected while the item is equipped (`ItemEquipped`). It deletes the item's backpack row or rows and the item row in one queued write. |
| H-D3 | **Auto-equip runs only on first sight of an inventory, when no starter grant is recorded yet.** Today `Ensure` equips the lowest occupied slot whenever slot 100 is empty. It runs on join, on every `TryCaptureInventorySnapshot` and on every `TryEquip`, so an unequip of the main hand would be undone. An empty hand now survives a rejoin. |
| H-D4 | **Rolled modifiers replace the definition's modifiers.** Effective modifiers are `item.Roll?.Modifiers ?? definition.Modifiers`, so a later definition change never alters a rolled item. Unrolled items keep reading the definition, as today. A retired kind is inert even when rolled: no stats, no light, row kept. |
| H-D5 | **Starter kit stays unrolled and identical** (`rolled_modifiers` NULL, kit version 2). `Rarity` is Common and is left off the wire. |
| H-D6 | **Creating a rolled item in wave H:** a new offline admin command, `dream --items grant`, plus four new content-defined items with a `rolls` table. It takes the world lease, as `--worlds` does. It requires a `player_starter_grants` row in that world. Without one, the next join treats the inventory as a pre-kit legacy one and grants only the torch. `PlayerInventoryService.TryGrantRolled` exists for wave I's `GrantLoot` and tests. No client action creates an item. |
| H-D7 | **Slots come from content.** `equipSlots` is required per item. Hammer, blade, maul: `MainHand`. Lantern, torch: `MainHand`, `OffHand`, in that order. New gear as listed in C0. There is no two-handed rule: the maul leaves the off hand free. An unknown kind has no allowed slot. |
| H-D8 | **`InventoryEquipIntent` without a target uses the item's first listed slot**, so a torch click goes to the main hand exactly as today. The wire bytes stay the same (a null target is omitted). The off hand is an explicit target. |
| H-D9 | **Stats apply in any allowed slot.** A lantern's +1 Strength and +0.10 Attack Speed add to a blade in the off hand. This is a small balance gain that exists only after the player chooses it. Effective caps stay at today's values (Strength and Vitality 50, Attack Speed 5). |
| H-D10 | **Held light: one light per player.** The off-hand item's light if it has one, otherwise the main-hand item's. `ActorSnapshot.HeldLight` stays one value, so actor bytes and the light budget don't change. The same pure function runs on server and client. |
| H-D11 | **Armor in wave H:** an effective `StatSheet` value, summed from equipment, base 0, range 0..1000. It is readable through `PlayerCombatant.EffectiveArmor` and shown in the panel with the note "no effect yet". `EnemyActor.cs:145` still calls `ApplyDamage(Combatant.AttackDamage)` unchanged, so no damage number changes. Wave I adds mitigation. A test pins that a player with Armor 40 takes the same damage as one with 0. Agility, Energy and MovementSpeed are rejected in `modifiers` and `rolls` by content validation. |
| H-D12 | **`ToolStatModifier` is retired in C0, as a mechanical second commit.** `ItemDefinition.Modifiers` and `ToolDefinition.Modifiers` become `IReadOnlyList<StatModifier>`. `PlayerCombatant.ApplyEquipment(EquipmentLoadout)` replaces `ApplyEquippedToolModifier`, with the main hand on source slot 0 (today's slot). `Combine` and the old struct move to test-only `LegacyToolStats`, and the bit-for-bit property test stays and compares against them. Doing it in C0 avoids a cross-stream break, since `PlayScene.Replication.cs` and the runtime both use the old type. |
| H-D13 | **`ApplyEquipment` sets all four sources, then derives once.** One derive per slot would call `SetMaxHealthPreservingRatio` several times with rounding between. |
| H-D14 | **`ModifierDigest`:** a `[JsonIgnore]` `ulong` on `ActorSnapshot`, from rolled modifiers of equipped items only, in slot order. It uses slot, stat, op and float bits, never the `ObjectId`. It is cached per player and recomputed on change. `HashActor` mixes it in only when non-zero. Clients decode 0, so server and client hashes differ for a player with rolled gear; `HashActor` has no production caller outside `Simulation/Determinism`. |
| H-D15 | **Two-slot rendering.** First person draws the main-hand model as today, plus a new optional off-hand slot: same definition, X position and Y and Z rotation mirrored, always Idle, no new rig config keys. An empty main hand draws no item (`held.none`), instead of today's fallback hammer. Head and body draw nothing. Remote players show no held model today and still don't; they show only the light. |
| H-D16 | **Unregistered `visualId` falls back to `held.debug` on the client.** `PlayerViewModelController.SetHeldItem` calls `GetDefinition`, which throws. Nothing catches it on the `ApplyInventorySnapshot` path, so a retired kind with an old visual id, or a server-only visual, throws in the client. `ViewModelCatalog.GetDefinition` keeps throwing (pinned by a test); a new `Resolve` does the fallback. |
| H-D17 | **Join health fix.** After inventory and stats are applied, the persisted current health is set again against the final max (`worldState?.CurrentHealth ?? max`). See correction 4. |
| H-D18 | **Inventory actions have a per-session budget:** burst 16, refill 8 per second, counted in server ticks so it is deterministic. Only actions that would write spend it. A no-op (same item, same slot; `From == To`) is accepted and writes nothing. This does not change the existing queue cap of 16 pending. |
| H-D19 | **Destroy names the item:** `InventoryDestroyIntent(SlotIndex, ItemInstanceId)`. A mismatch rejects with `ItemMismatch`, so a queued destroy can't hit whatever moved into the slot. |
| H-D20 | **Rows are immutable where it counts.** `UpsertSql`'s `ON CONFLICT` branch does not name `rolled_modifiers`, so no later save can change a roll. |

## What The Code Does Today

**Confirmed from the roadmap:**
- Only slot 100 exists beyond the backpack; there is no unequip, move or destroy (`PlayerInventoryService.cs`).
- `SaveInventorySlot` is upsert only. `DeleteInventorySlots` removes all of a player's rows. No single-row delete exists.
- `item_instances` has no per-instance stats. `stack_count` is always 1.
- The client calls `ToolCatalog` in exactly one place, `PlayScene.Replication.cs:225`.
- `IsClientSendable` lists client intents; other payloads are rejected.
- Delta actor frames keep `Equipment` optional, so `HeldLight` stays compatible.
- `ResolveEquippedHeldLight` allocates nothing.
- `SqliteWorldCatalog.CopyTables` drives both copy and export (lines 163 and 238), so one column-list edit covers both.
- `StatSheet`: 16 sources, 8 modifiers each. Ascending source order.

**Corrections to the roadmap:**
1. **`HeldLight` is broken for a character whose display name differs from its key.** `WorldSessionRuntime.Replication.cs:212` passes `displayName` to an inventory keyed by `CharacterKey`. A renamed character, or a migrated `bob~2`, casts no light for other clients. Wave G missed it. H1 fixes it with `TryResolveCharacterKey`.
2. **`ResolveEquippedHeldLight` on `Ensure` semantics:** the roadmap treats unequip as a plain action. It needs H-D3 first.
3. **The roadmap's actor byte gate can't hold literally.** v10 is the first two-digit version, and every frame's envelope holds `"protocolVersion":N`, so each frame grows 1 B. `HeldLightSnapshotBytesTests` pins 58,107 B over 200 frames; it becomes 58,307 B. `StoredWorldJoinLoopbackTests.C0TotalBytes` pins 8,492 B and also moves. Compare with the version normalized.
4. **Vitality gear exposes a join bug.** No shipped tool has Vitality, so it never showed. `ApplyPersistedPlayerState` sets health against the base max, before equipment. `SetAuthoritativeState` clamps, then `Refresh` scales by ratio. Saved 120/150 with a vest becomes 100/100, then 150/150. Saved 60/150 becomes 90/150. Every rejoin would heal. H-D17 fixes it.
5. **`StatModifier` is enough, but malformed rolls throw on the tick.** `StatSheet.SetSource` throws on a non-finite value, an unknown stat or more than 8 modifiers. Parsing must therefore reject these, and a row that fails parses to an inert roll. The client filters the same way, since a hostile server can send `1e99`.
6. **The HUD sliders use effective stats.** `ClientWorldSession` passes `Combatant.Stats` to `HudStatusSnapshot`, and `Render.HudPanels.cs:137-152` seeds the sliders from it. Moving a slider with a hammer equipped sends effective+delta as the new base. Gear makes this worse, and the panel needs base values anyway. H3 fixes it.
7. **`SharedItemReferencesSql` and `EquippedWithoutBackpackSql` name slot 100 only.** Left alone, a 101 alias counts as a shared reference, and an item in two equipment slots isn't reported.
8. **`InventoryEquipIntent(int)` can't express the off hand.** The roadmap gave no change; H-D8 adds an optional target.
9. **`EquipSlot` must be plural:** a torch has two. The snapshot carries a mask.
10. **`ReadProperties` requires every allowed property.** `rolls` needs an optional-property form.
11. **Small join and World10k join grow** because inventory travels in the join-accepted message. The G-style "identical apart from the digit" applies only with the new fields removed.
12. **`ReadItem` and `KitVersionOrdinal` are positional.** The new column must go last among the item columns, and `KitVersionOrdinal` moves from 16 to 17.

**Migrator facts for 0008:** backup `{db}.v0007-to-v0008.{stamp}.bak` is taken as usual. No table is rebuilt, so all five object-id triggers stay. `foreign_key_check` passes because no row changes. A v8 file on an older build exits 3.

## Scope

**In:**
- Migration 0008 and protocol v10.
- Four equipment slots, slot rules from content.
- Rolls, rarity, `ItemRoller`, `ModifierDigest`.
- Move, unequip, destroy, equip with target.
- `--items grant | list`.
- Integrity additions, restart coverage, copy and export.
- Character panel and off-hand first-person model.
- Retiring `ToolStatModifier`, and the client's use of `ToolCatalog`.
- The fixes in H-D16, H-D17, correction 1 and correction 6.
- Docs.

**Out:**
- Armor mitigation, enemy stats, loot, `InventoryPushMessage` (wave I).
- Percent modifiers (`Op` is stored, only `Flat` is accepted).
- Ground drops, pickup, trading, stacking.
- Head and body art, and held models on remote players.
- Mirroring the first-person light anchor for the off hand.
- Tool requirements for build or attack: an empty hand can still do both.
- A live admin grant while a server runs.
- Raising stat caps. Agility, Energy, MovementSpeed.
- Removing client startup content loading (enemies still read it until wave I).

## Determinism And Security Rules

**Determinism**
1. Any tick golden change with default items is a defect. Only the version digit may differ.
2. Equipment source slots are fixed: 0 main, 1 off, 2 head, 3 body. Sources resolve in ascending order. An item with several modifiers keeps file or stored order.
3. Rolls use a fresh `DeterministicRandom` per item, from `ItemRollSeed`. No stream is shared with combat, and no shared stream advances.
4. `ObjectId`s never enter the tick's seeds or digests. The offline CLI may use the item id as `DropIndex`, because it isn't lockstep.
5. Effective stats stay out of checksums. `ModifierDigest` zero is not mixed in.
6. The budget (H-D18) counts ticks, never wall time.
7. `ContentDigest` changes with the new items (format tag `items.v3`). This is expected and logged once.

**Security**
8. Nothing from a client carries a stat, rarity, item id to create, or kind. Intents name the sender's own slots. `ItemInstanceId` in destroy is only compared.
9. Client integers are range-checked before use. `TargetSlotIndex` is parsed through `EquipmentSlots.TryFromSlotIndex`; no enum is cast from wire data.
10. A rejected action changes nothing and queues no write.
11. Any row content is hostile on load. `ItemRollCodec.TryParse` never throws; a bad value gives `ItemRoll.Damaged`, which is inert.
12. Client-side, `EquipmentLoadout.Set` skips non-finite or unknown modifiers and ignores anything past 8.
13. Log lines carry counts and slot numbers, not item text.

## C0 Contracts

C0 is two commits on the tip, with the suite green after each and behaviour unchanged. After C0 nobody edits contract types except to add what a workstream section names.

**C0a: contracts, migration, protocol.**

New `dream/Simulation/Inventory/EquipmentSlot.cs`:

```csharp
public enum EquipmentSlot : byte { MainHand = 0, OffHand = 1, Head = 2, Body = 3 }

public static class EquipmentSlots
{
    public const int Count = 4;
    public const int FirstSlotIndex = InventoryConstants.EquippedSlotIndex;   // 100
    public const int LastSlotIndex = FirstSlotIndex + Count - 1;              // 103
    public static int ToSlotIndex(EquipmentSlot slot) => FirstSlotIndex + (int)slot;
    public static bool TryFromSlotIndex(int slotIndex, out EquipmentSlot slot);
    public static int MaskOf(EquipmentSlot slot) => 1 << (int)slot;
    public static HeldLightKind ResolveHeldLight(HeldLightKind mainHand, HeldLightKind offHand)
        => offHand != HeldLightKind.None ? offHand : mainHand;
}
```

New `EquipmentLoadout.cs` (preallocated, no allocation after construction):

```csharp
public sealed class EquipmentLoadout
{
    public void Clear();
    public void Set(EquipmentSlot slot, IReadOnlyList<StatModifier> modifiers); // skips invalid, stops at 8
    public ReadOnlySpan<StatModifier> Get(EquipmentSlot slot);
}
```

New `ItemRarity.cs`, `ItemRoll.cs`, `ItemRollCodec.cs`, `ItemRoller.cs`:

```csharp
public enum ItemRarity : byte { Common = 0, Uncommon = 1, Rare = 2 }

public sealed record ItemRoll(ItemRarity Rarity, IReadOnlyList<StatModifier> Modifiers)
{
    public static ItemRoll Damaged { get; }    // Common, no modifiers
    public bool IsDamaged => ReferenceEquals(this, Damaged);
}

public static class ItemRollCodec
{
    public const int Version = 1;
    public const int MaxTextLength = 1024;
    public static string Serialize(ItemRoll roll);
    // null text gives null. Malformed, wrong version, unknown rarity, stat or op, a non-live stat,
    // a non-finite or out-of-range value, a duplicate stat or more than 8 modifiers gives Damaged.
    public static ItemRoll? Parse(string? text);
}

public readonly record struct ItemRollSeed(
    ulong SimulationSeed, ulong Tick, ulong SourceHash, ulong SourceId, int DropIndex);

public static class ItemRoller
{
    // Pure. Draws one value per range in file order from new DeterministicRandom(mix of seed and
    // Salt "item-rol"). Strength, Vitality, Armor: integers, inclusive. AttackSpeed: min + k * 0.05f.
    public static ItemRoll Roll(ItemDefinition definition, ItemRarity rarity, in ItemRollSeed seed);
}
```

`Seed.Combine` takes up to 5 arguments, so nest calls. The seed for wave I is `(simulation seed, tick, killer session hash, enemy actor id, drop index)`, as in the roadmap.

Stored format, compact and order-preserving, with floats written round-trip and stat names as text so `StatId` can be reordered:

```json
{"v":1,"r":"Rare","m":[["Strength","Flat",6],["AttackSpeed","Flat",1.75],["Armor","Flat",4]]}
```

The largest realistic value is 316 B.

**Content** (`ItemDefinition.cs`, `ContentCatalog.cs`, `items.json`, in C0a so `ItemRoller` and H2's CLI have real input):

```csharp
public readonly record struct ItemRollRange(StatId Stat, float Min, float Max);

public sealed record ItemRollTable(
    IReadOnlyList<ItemRollRange> Common,
    IReadOnlyList<ItemRollRange> Uncommon,
    IReadOnlyList<ItemRollRange> Rare);

public sealed record ItemDefinition(
    string Key, string DisplayName, string Description, string VisualId,
    IReadOnlyList<string> DetailLines, HeldLightKind HeldLight,
    IReadOnlyList<StatModifier> Modifiers,
    int EquipSlotMask,
    IReadOnlyList<EquipmentSlot> EquipSlots,   // file order; [0] is the default target
    ItemRollTable? Rolls);
```

- `items.json` version 2. `equipSlots` is required, `rolls` optional.
- `modifiers` and `rolls` accept Strength, AttackSpeed, Vitality, Armor only, `Flat` only, each stat once, at most 8.
- Ranges: Strength and Vitality ±50, integer. Attack Speed ±5. Armor 0..100, integer. All three rarities are required when `rolls` is present.
- `ContentCatalogTests` item count goes from 5 to 9.

| Key | Slots | visualId | Rolls: Common / Uncommon / Rare |
| --- | --- | --- | --- |
| `gear.iron-blade` | MainHand | `held.tool.blade` | Strength 4-5, AttackSpeed 1.0-1.5 / 5-7, 1.25-1.75 / 7-9, 1.5-2.0 |
| `gear.ward-lantern` | OffHand | `held.tool.lantern` | Armor 1-2 / 2-4 / 4-6; held light Lantern |
| `gear.leather-cap` | Head | `worn.head` | Armor 1-3, Vitality 0-1 / 3-5, 1-2 / 5-8, 2-4 |
| `gear.leather-vest` | Body | `worn.body` | Armor 2-4, Vitality 1-2 / 4-7, 2-4 / 7-11, 4-6 |

These numbers are placeholders for a balance pass. None is granted to anyone by default.

**Protocol:**

```csharp
// TransportEnvelope.CurrentProtocolVersion = 10, history comment extended.
public sealed record InventoryEquipIntent(int SlotIndex, int? TargetSlotIndex = null) : IPlayerActionPayload;
public sealed record InventoryUnequipIntent(int SlotIndex) : IPlayerActionPayload;         // "inventory.unequip"
public sealed record InventoryMoveIntent(int From, int To) : IPlayerActionPayload;         // "inventory.move"
public sealed record InventoryDestroyIntent(int SlotIndex, long ItemInstanceId) : IPlayerActionPayload; // "inventory.destroy"

public readonly record struct InventoryItemSnapshot(
    long ItemInstanceId, string ItemKind, string DisplayName, string VisualId, int StackCount,
    string Description, IReadOnlyList<string> DetailLines,
    [property: JsonIgnore(WhenWritingNull)] IReadOnlyList<StatModifier>? Modifiers = null,
    [property: JsonIgnore(WhenWritingDefault)] HeldLightKind HeldLight = HeldLightKind.None,
    [property: JsonIgnore(WhenWritingDefault)] int EquipSlots = 0,            // EquipmentSlots mask
    [property: JsonIgnore(WhenWritingDefault)] ItemRarity Rarity = ItemRarity.Common,
    [property: JsonIgnore(WhenWritingDefault)] bool IsRolled = false);

public readonly record struct PlayerInventorySnapshot(
    string PlayerName, int Columns, int Rows,
    IReadOnlyList<InventorySlotSnapshot> Slots,
    InventorySlotSnapshot? EquippedSlot = null,              // the main hand, slot 100, as today
    IReadOnlyList<InventorySlotSnapshot>? Equipment = null); // occupied 101..103 only; null when none

// ActorSnapshot, trailing:
[property: JsonIgnore] ulong ModifierDigest = 0
```

Empty `Modifiers` serialize as null, so a torch adds nothing for them. `InventorySlotSnapshot` is unchanged; `IsEquipped` means "in any equipment slot".

Wire examples:
- Equip to off hand: `{"$type":"inventory.equip","slotIndex":4,"targetSlotIndex":101}`.
- Item fields: `"modifiers":[{"stat":0,"op":0,"value":4}]`, `"heldLight":1`, `"equipSlots":3`, `"rarity":2`, `"isRolled":true`.
- `StatId` numbers are now a wire contract: Strength 0, AttackSpeed 4, Vitality 2, Armor 6.
- `PlayerActionHandler.IsClientSendable` gains the three new intents. Until H1 merges, the runtime rejects them as unsupported.

**Persistence ports:**

```csharp
public readonly record struct PersistedItemInstance(..., DateTime UpdatedUtc, ItemRoll? Roll = null);

// IInventoryRepository, default bodies do nothing so the 9 test fakes compile:
void DeleteInventorySlot(string playerName, int slotIndex) { }
// Deletes this player's rows for the item, then the item row if no slot still references it.
void DestroyInventoryItem(string playerName, long itemInstanceId) { }
```

**Stats:**

```csharp
public int EffectiveArmor => (int)_statSheet.Effective[StatId.Armor];   // PlayerCombatant
public void ApplyEquipment(EquipmentLoadout equipment);
```

**Runtime surface** (declared in C0, stubs where H1 fills them):

```csharp
public enum InventoryEquipOutcome
{ Equipped = 0, SlotOutOfRange = 1, StillLoading = 2, SlotEmpty = 3,
  TargetOutOfRange = 4, NotEquippable = 5, BackpackFull = 6, ItemEquipped = 7, ItemMismatch = 8, Throttled = 9 }

InventoryEquipOutcome TryEquip(string player, int slotIndex, int? targetSlotIndex = null);
InventoryEquipOutcome TryUnequip(string player, int slotIndex);
InventoryEquipOutcome TryMove(string player, int from, int to);
InventoryEquipOutcome TryDestroy(string player, int slotIndex, long itemInstanceId);
internal InventoryEquipOutcome TryGrantRolled(string player, string kind, ItemRarity rarity, in ItemRollSeed seed);
void CopyEquipment(string player, EquipmentLoadout into);    // replaces ResolveEquippedToolModifier
ulong ResolveModifierDigest(string player);
```

**C0 tests:**
- Protocol pins (9 to 10) in the 12 files that match: `ProtocolV2SharedSnapshotLoopbackIntegrationTests`, `ProtocolV2VersionRejectionLoopbackIntegrationTests`, `ProtocolVersionMismatchClientTests`, `StoredWorldJoinLoopbackTests`, `WaveBTesterWorldsLoopbackTests`, `ClientJoinRequestCodecTests`, `JoinAcceptedWorldDescriptorCodecTests`, `ProtocolFoundationTests`, `ProtocolV2SlimmingTests`, `TransportJsonCodecDecodeTests`, `WorldClockAnchorProtocolTests`, `DedicatedServerServiceConnectionErrorTests`. Also check `TerrainChunkMessageCodecTests` and `WorldTransferLimitTests`, which wave G listed.
- Byte pins: `HeldLightSnapshotBytesTests` becomes 58,307 B, with a comment on the extra digit.
- Schema pins (7 to 8) under the 15 files that match `Equal(7,` or `ShippedLatestVersion`; some matches may be unrelated.
- `ItemRollCodecTests`: round trip by float bits; every rejection case; a 1,500-deep array; never throws.
- `ItemRollerTests`: same seed gives same roll; different `DropIndex` differs; every value within range across 10,000 seeds; Attack Speed on the 0.05 grid; golden rolls for fixed seeds.
- `EquipmentSlotsTests`: 99, 104, -1, `int.MaxValue` are false.
- `ContentCatalogTests`: stat, slot and roll validation; the 5 shipped kinds equal today's values and order.
- `ItemRollsMigrationTests`, below.

**C0b: retire `ToolStatModifier`.** One mechanical commit, per H-D12:
- Delete `ToolStatModifier.cs`, `ContentStat`, `ItemModifierDefinition`, `ToolDefinition.StatModifier`, `PlayerCombatStats.Combine`.
- `PlayerInventoryService` gets `CopyEquipment` reading slot 100 only.
- `RefreshAuthoritativePlayerCombatStats` and `PlayScene.ApplyInventorySnapshot` fill an `EquipmentLoadout`. The client still reads `ToolCatalog` at this point.
- Update `ContentCatalogTests`, `StatSheetTests`, `PlayerCombatantTests`, `PlayerInventoryServiceTests`, `UnknownItemKindTests`. Add `dream.Tests/Support/LegacyToolStats.cs`.

Acceptance: `StatSheetTests`' property tests over the full base range pass unchanged in meaning, every field bit-equal to `LegacyToolStats.Combine`, and tick goldens are identical.

## Migration 0008 (`dream/config/migrations/0008_item_rolls.sql`, in C0a)

```sql
-- Migration 0008: rolled item stats (wave H). NULL means an unrolled item, whose stats come from its
-- definition. The value is versioned JSON (ItemRollCodec) and never changes after the item is
-- created. No table is rebuilt, so trg_item_instances_advance_object_id stays.
ALTER TABLE item_instances ADD COLUMN rolled_modifiers TEXT NULL
    CHECK (rolled_modifiers IS NULL OR (length(rolled_modifiers) <= 1024 AND json_valid(rolled_modifiers)));
```

Trial results on in-memory SQLite 3.50.4:
- Existing rows read NULL.
- A valid roll inserts. Malformed text, 2,000 characters and 1,500 `[` are refused.
- An `ON CONFLICT DO UPDATE` that doesn't name the column keeps the roll.
- The trigger survives and still raises the counter.

Slot indices 101..103 need no schema change: `CHECK (slot_index >= 0)` already allows them.

**`ItemRollsMigrationTests`:**
- every version 0 to 7 migrates to 8, with `integrity_check` ok and `foreign_key_check` empty;
- all 5 object-id triggers survive;
- every existing row is byte-equal apart from the new NULL column;
- two copies migrate identically;
- a `v0007-to-v0008` backup exists;
- a forced failure rolls back to 7;
- a malformed value and 1,025 characters are refused;
- a v8 file on a v7 build exits 3 (existing test, new pin).

## Workstreams

Rules for every workstream:
- Touch only the files you own.
- The build and full suite pass. Any skip reads `wave H: needs <X>, unskip at glue` and is listed in the hand-off.
- 0 B per tick.
- Goldens and post-join snapshot bytes stay unchanged, apart from the version digit's length.
- Reset the worktree to the C0 tip first (worktrees start from master), and build with an absolute `BaseOutputPath` inside the worktree.
- Max 2 full-suite runs per verification step.

### H2: Persistence and admin

**Owns:**
- `Server/Persistence/Repositories/SqliteItemRepository.cs`, `SqliteInventoryRepository.cs`;
- `Server/Persistence/Worlds/SqliteWorldCatalog.cs`;
- `Server/Persistence/WorldIntegrityCheck.cs`;
- `Server/ServerBootstrap.cs` (`IntegrityOptionsFor`);
- new `Server/Items/ItemCli.cs`;
- `Application/Program.cs` (`ProcessMode.Items` for `--items`);
- `dream.Tests/Load/RestartMidSessionMeasurementTests.cs`;
- `docs/server-persistence-ddl.md`.

**Steps:**
1. `rolled_modifiers` goes last among the item columns in `LoadSql` and `LoadPlayerInventorySql`. Add one `NULL` to the grant branch. `KitVersionOrdinal` becomes 17. `ReadItem` calls `ItemRollCodec.Parse` at `o + 12`, which runs on the persistence worker.
2. `UpsertSql` inserts the column and does not name it in `ON CONFLICT`. Bind with a null-safe text binder. `Serialize` runs in `SaveItemInstance`, on the worker.
3. Add `DeleteInventorySlot`, and `DestroyInventoryItem`:
   - `DELETE FROM player_inventory_slots WHERE world_id = $worldId AND player_name = $playerName AND item_instance_id = $itemInstanceId;`
   - `DELETE FROM item_instances WHERE world_id = $worldId AND item_instance_id = $itemInstanceId AND NOT EXISTS (SELECT 1 FROM player_inventory_slots AS s WHERE s.world_id = $worldId AND s.item_instance_id = $itemInstanceId);`
4. `CopyTables`: add `rolled_modifiers` to the `item_instances` list.
5. Integrity:
   - `EquippedWithoutBackpackSql` covers 100..103.
   - `SharedItemReferencesSql` treats any 100..103 alias of a same-player backpack slot as legitimate.
   - New soft counts: `ItemsInTwoEquipmentSlots`, `UnknownSlotIndexes` (outside 0..15 and 100..103), `EquipmentSlotMismatches` (needs `Func<string, int, bool> CanOccupy` on `WorldIntegrityOptions`, default accept-all, wired in `IntegrityOptionsFor`), `DamagedItemRolls` (reads only rows with a non-NULL roll and parses them).
6. `--items`, following `CharacterCli` and `WorldCli`:

```
dream --items grant <world> <character> <item kind> [--rarity common|uncommon|rare]
dream --items list <world> <character>
```

- `grant` is one owned transaction, under the world's lease:
  - resolve the character by `display_name` NOCASE, then the exact key;
  - require a grant row;
  - pick the lowest free backpack slot;
  - take `LoadNextObjectId`;
  - call `ItemRoller.Roll` with `(worlds.seed, worlds.world_time_ticks, StableHash.Of(key), 0, (int)itemId)`;
  - insert the item row, owner = key, `State = "Inventory"`, then the slot row.
- It prints item id, slot, rarity and modifiers, tab-separated. `list` prints slots, kinds, rarity and modifiers, and takes no lease.
- Exit codes: 0 ok, 1 usage, 2 not found, no grant row, not rollable or backpack full, 3 schema too new, 7 held, 8 invalid content. Default rarity is common.

**Tests:** additions to `PerPlayerInventoryPersistenceTests`, `SqliteWorldCatalogTests`, `WorldIntegrityCheckTests`; new `ItemCliTests`, `ItemRollPersistenceTests`.

**Acceptance:**
- A rolled item round-trips with equal float bits through save, `LoadPlayerInventory` and `LoadItemInstances`.
- A second save with a different `Roll` leaves the stored text alone (pinned).
- An unrolled row reads `Roll == null`. A hand-edited valid-JSON bad roll reads `Damaged`, loads and is counted.
- `--worlds copy` and full `export` keep the roll text byte-equal. `--terrain-only` has no items.
- `DestroyInventoryItem` leaves another player's row and the item when shared (legacy), and removes both otherwise.
- `--items grant` refuses a held world (7), a character with no grant row (2), a full backpack (2), a kind without `rolls` (2). A granted item's id is below the raised `next_object_id`. A later join sees it, with no starter top-up side effects.
- Integrity: a 101 alias is no finding; each new count fires on its own fixture; a clean v7-to-v8 database has no soft finding; World10k stays within budget.
- `RestartMidSessionMeasurementTests`: add `RollsByCharacter` and `EquipmentByCharacter` to `RestartObservation`, and an `Invariants` entry that they match before and after.

**Must not touch:** `Simulation/**`, `Multiplayer/**`, `Client/**`, `Rendering/**`.

### H1: Equipment and actions

**Owns:**
- `Simulation/Inventory/PlayerInventoryService.cs`;
- `Simulation/Runtime/WorldSessionRuntime.Inventory.cs`, `.Commands.cs`, `.Persistence.cs`, `.Replication.cs`;
- `Simulation/Determinism/SimulationStateChecksum.cs`;
- `Multiplayer/Sessions/ConnectedPlayer.cs` (the budget field only).

**Steps:**
1. **H-D3.** In `Ensure`, read `bool firstSight = !_starterKitByPlayer.ContainsKey(player)` before granting, and auto-equip only when true. Auto-equip picks the lowest occupied slot whose item allows `MainHand`.
2. **`TryEquip` with target.** Order of checks: backpack range; target via `TryFromSlotIndex`, else `TargetOutOfRange`; `Ensure`; slot occupied; kind known and slot in the mask, else `NotEquippable`; budget. Default target is `EquipSlots[0]`. If the item already sits in another equipment slot, that row is deleted. A displaced item with no backpack row gets rescued (H-D2). Slot writes that belong together share one queued closure, so the savepoint makes them atomic.
3. **`TryUnequip`, `TryMove`, `TryDestroy`** per H-D2 and H-D19. Destroy deletes all of the player's rows for the item in memory and queues `DestroyInventoryItem`, and calls `NoteWriteQueued`. Move and swap write both rows in one closure, deleting the vacated row.
4. **Budget** (H-D18). `InventoryActionBudget` is a struct on `ConnectedPlayer`, taking `CurrentServerTick`.
5. **`CopyEquipment`** reads all four slots. A retired kind, a slot the item doesn't allow, or a `Damaged` roll gives no modifiers: the row is kept and is inert.
6. **Cached summary.** `Dictionary<string, EquippedSummary>` with `readonly record struct EquippedSummary(HeldLightKind Light, ulong Digest)`. One private `RefreshEquippedSummary` is called from every path that changes slots or loads an inventory (`ApplyLoaded`, `TryRestoreEvicted`, `Ensure`, each action), and removed in `Evict`. `ResolveEquippedHeldLight` reads it.
7. **`BuildSnapshot`** fills `Modifiers`, `HeldLight`, `EquipSlots`, `Rarity`, `IsRolled`, `IsEquipped` for all four slots, and `Equipment`.
8. **Digest in checksum:**

```csharp
ulong hash = /* the existing last step */;
return actor.ModifierDigest == 0 ? hash : Seed.Combine(hash, ModifierDigestSalt, actor.ModifierDigest);
```

9. **`CaptureActorSnapshots`:** use `TryResolveCharacterKey` for the light and digest (correction 1).
10. **`.Commands.cs`:** route the three new intents. `.Inventory.cs`: add `ProcessInventory*Action` with one rejection text per outcome, refresh stats on accepted, return `InventoryChangedResult`.
11. **H-D17** in `RestorePersistedPlayerState`: after the refresh, call a new private `ReapplyPersistedHealth(sessionId, worldState)`, using the same `SetHealth` branch as `ApplyPersistedPlayerState`.

**Tests:** new `Simulation/Inventory/EquipmentSlotTests.cs`, `InventoryActionTests.cs`, `Integration/EquipmentLoopbackTests.cs`, `Server/InventoryHostileIntentTests.cs`, `Simulation/Runtime/RolledItemDigestTests.cs`, `RenamedCharacterHeldLightTests.cs`, `EquipmentHealthRestoreTests.cs`, `ArmorNoEffectTests.cs`, `Simulation/Runtime/WorldSessionRuntimeEquipmentAllocationTests.cs`.

**Acceptance:**
- **Slots.** Blade in main plus torch in off both hold. Stats sum, light is Torch. Torch with no target goes to main, as today. Maul in off, helmet in body, and anything to a bad index are rejected.
- **One slot only.** Torch moved main to off leaves main empty and the item in one equipment row.
- **Alias.** Moving an equipped item keeps it equipped, and the rows after restart match. Swap of two equipped items works. Unequip keeps the backpack row. Unequip of a legacy no-backpack item rescues it, or rejects when full. Destroy equipped is rejected. Destroy of a backpack item removes both rows, and a later kit top-up doesn't return it.
- **H-D3.** Unequip main, then `Ensure`, `TryCaptureInventorySnapshot`, rejoin and restart: still empty. New player: hammer equipped, as today. Legacy unrecorded inventory: as today.
- **Migrated rows.** Slot 100 with a torch, lantern, maul or retired kind loads with no write and the same stats and light as v7.
- **Rolls.** A rolled item's modifiers replace its definition's. Changing the definition in a test catalog leaves it unchanged. A retired rolled kind is inert with its row kept. A damaged roll gives no stats and no throw on any tick path.
- **Digest.** 0 with no rolled item equipped, and with one in the backpack only. Equal across two runs and across a restart. Changes with the item, the slot and the value. `HashActor` for a digest-0 actor equals the pre-wave value.
- **Light.** Off-hand wins. A retired item gives None. Renamed and `bob~2` characters cast a light for other clients.
- **Health.** Vest saved at 120/150 rejoins at 120/150, and 60/150 at 60/150, no row at full, dead at 0. Unequip scales by ratio. Toggling a vest 100 times from any starting value never raises health by more than the first round.
- **Armor.** Same wolf, same tick: Armor 40 and Armor 0 take identical damage. `EffectiveArmor` equals the sum.
- **Hostile.** Each of these is rejected with no state, row or write change: bad slot (-1, 16, 99, 104, `int.MaxValue`), bad target, empty slot, wrong slot type, a destroy naming another player's item id, a destroy naming a stale id, an action on another session. Over a real connection, 1,000 moves in one burst: at most 16 accepted before the budget empties, at most 32 writes, no action outside the queue cap, client stays connected.
- **Allocation.** 0 B per tick with 4 equipped rolled items on 2 sessions, including `CaptureActorSnapshots`.
- **Existing suites.** `PlayerInventoryServiceTests`, `HeldLightLoopbackTests`, `HeldLightSnapshotBytesTests`, `Wave12InventoryLoopbackTests` pass without weakening. A re-equip that expected a write count changes because of H-D18's no-op rule: fix the count and note it.

**Must not touch:** `Server/**`, `Client/**`, `Rendering/**`, `Multiplayer/Protocol/**`, the migration, `ActorFrameJson`, `ActorSnapshotJsonConverter`.

### H3: Client and HUD (merges last)

**Owns:**
- `Client/Scenes/PlayScene.Replication.cs`, `PlayScene.HudRequests.cs`, `PlayScene.Update.cs`;
- `Client/Presentation/WorldSessionPresentationState.cs`;
- `Client/World/ClientWorldSession.cs` (the `Create` call only);
- `Rendering/Core/Render.HudPanels.cs`;
- `Rendering/Hud/HudInteractionRequest.cs`, `HudStatusSnapshot.cs`, new `Rendering/Hud/CharacterPanelModel.cs`;
- `Rendering/ViewModels/PlayerViewModelController.cs`, `ViewModelAssets.cs`, `ViewModelCatalog.cs`, `ViewModelFrameState.cs`, `ViewModelPoseResolver.cs`, `ViewModelRenderer.cs`, `ViewModelSlot.cs`.

**Contract:**

```csharp
public enum InventoryHudActionKind : byte { EquipTo, Unequip, Move, Destroy }
public readonly record struct InventoryHudAction(InventoryHudActionKind Kind, int SlotIndex, int Target = 0, long ItemInstanceId = 0);
// HudInteractionRequest: trailing InventoryHudAction? InventoryAction = null
// HudStatusSnapshot: trailing int BaseStrength, float BaseAttackSpeed, int BaseVitality, int Armor
// ViewModelFrameState: trailing ViewModelSlotState? OffHandItem = null
// PlayerViewModelController.SetOffHandItem(string? heldItemId)
// ViewModelCatalog.Resolve(string id): the definition, or held.debug when unregistered
// ViewModelAssets.EmptyHeldItemId = "held.none"
```

**Steps:**
1. `ApplyInventorySnapshot` reads only the snapshot: `EquipmentLoadout` from `EquippedSlot` and `Equipment`, light from `EquipmentSlots.ResolveHeldLight`, visuals. `using ToolCatalog` goes away from `Client/`. Reuse one loadout instance.
2. `held.none` has an empty `ModelPath`. `TryResolveModel` returns false, and `UpdateSlot` reports finished for it, so the Use state can't stick.
3. Off hand: `ViewModelSlot.OffHandItem`. The state is built from the same definition with `PositionOffset.X`, `RotationOffsetDegrees.Y` and `.Z` negated. `GetBodyAnchor` uses the hands' sign, and tuning is mirrored from `rig.HeldItem`. A third `ViewModelSlotAnimation`. `TriggerPrimaryUse` doesn't animate it. No new rig settings.
4. `CharacterPanelModel` (pure, no ImGui) builds rows per stat: base, bonus, effective. It also builds slot rows, tooltip lines from `Modifiers` for rolled items, and rarity colour keys. Armor's row reads "no effect yet". Agility, Energy and Movement Speed are not shown.
5. `Render.HudPanels.cs`: a Character section with four equipment slots. Left click on a backpack item equips as today. A context menu offers each allowed slot from the `EquipSlots` mask, Unequip, Move To, and Destroy behind a confirm. Rarity colours the label: common default, uncommon green, rare blue.
6. Sliders use the new base fields (correction 6).
7. `PlayScene.HudRequests.cs` maps `InventoryAction` to intents. Effective values come from the local combatant; `DrawInventoryOverlay` keeps its layout.

**Tests:** additions to `PlayerViewModelControllerTests`, `ViewModelPoseResolverTests`, `ViewModelCatalogTests`; new `Rendering/Hud/CharacterPanelModelTests.cs`, `Client/Scenes/PlaySceneEquipmentSnapshotTests.cs` (non-GL, following `PlaySceneHeldLightsTests`).

**Acceptance:**
- The client's effective stats match the server's for every shipped item, in every allowed slot, using only snapshot data, and with `ToolCatalog` definitions absent from the snapshot's decision.
- A snapshot naming an unknown kind and visual doesn't throw. It shows `held.debug` and no light. A hostile snapshot with `1e99`, stat 200 or 40 modifiers doesn't throw.
- Blade plus torch: two item states, off hand mirrored, only main plays Use. Unequip main: no held model, no stuck Use. Default frame state for the default loadout is unchanged.
- Panel: base + bonus = effective for a fixture with all four slots; a capped stat shows the capped effective; each action produces exactly one correct intent; a right-click destroy of a rolled item needs the confirm.
- Sliders with a blade equipped send the base value plus the drag.
- Inventory text from the server goes through `TextUnformatted`, so `%` stays literal.

**Must not touch:** `Server/**`, `Simulation/**`, `Multiplayer/**`.

## Merge Order And Glue

C0a, C0b, H2, H1, H3 plus glue. Build and test after each merge.
- H2 first: it persists the roll and accepts more slots, and nothing produces them yet.
- H1 second: the server fills the snapshot fields. An old H3-free client still works, because it ignores them.
- H3 last: the client depends on what H1 sends, and it's the only stream that can't be proved end to end by unit tests alone.

Glue:
1. Unskip anything marked for it.
2. Add a rolled, fully equipped character to `RestartMidSessionMeasurementTests` run 2, using H1's service with H2's real repository.
3. Combined test `RolledItemEndToEndTests`: `--items grant`, start, join over TCP, equip four slots, check the server's and client's effective stats, restart, rejoin, `--worlds copy`, join the copy, `export`, join again.
4. Update `docs/configuration.md` (`--items`, exit codes, integrity counts, slot range), `docs/architecture.md` (equipment, rolls, digest), and write `docs/ledger/work-log/2026-11-01-wave-h-items.md`.
5. Real-process flow on copies of the 4 local `legacy-e` databases: start migrates to v8 and leaves a backup; existing equipped tools are unchanged; `--items grant`; two real clients (one sees the other's torch light); blade plus torch held; unequip survives a restart; a hand-written hostile client sending the bad intents is rejected.
6. Perf harness with two clients, screenshot of the panel for a person to judge. Interactive feel has to be checked by the user.

## Measurements And Gates

Release, `DREAM_LOAD_TEST=1`, two alternating rounds against C0b. Measure the baseline at C0b, because several wave G numbers weren't repeated after its fix loops. Max 2 full-suite runs per verification step; isolate flakes rather than rerun.

| Measurement | Gate |
| --- | --- |
| Tick goldens (`WorldSessionRuntimeTickEquivalenceTests`) with default items | unchanged |
| Starter kit combat outcome | identical; every tool in slot 100, full base range, bit for bit against `LegacyToolStats` |
| Tick allocation | 0 B, including 4 equipped rolled items on 2 sessions |
| `server.join-accepted`, starter kit | ≤ 3,565 B × 1.15 = 4,099 B. Estimate 4,048 B (+483 B): tight. Re-baseline at C0 first |
| Small join | ≤ 8,492 B + 530 B; identical once the new item fields and the digit are removed |
| World10k join 0 | ≤ 232,677 B + 530 B; same normalization |
| Actor snapshot bytes | identical apart from 1 B per frame for the version: `NoLight_200Ticks` 58,307 B |
| Delta frames with equipment changes | `Equipment` group only, as `OneTorch_GrowthPerFrame` |
| World10k full checkpoint | ≤ C0b × 1.05 (wave F 38.0 ms). No checkpoint SQL changes |
| Join-window total | ≤ C0b × 1.10 (wave G 12.7 to 13.0 ms) |
| Integrity check, World10k | ≤ 15 ms |
| Migration 0008 on 100,000 items | ≤ 1 s |
| Server start, World10k | no clear difference, ±2× noise |
| Rolled item | survives restart, `--worlds copy` and full export, byte-equal text and bit-equal stats |
| Hostile intents | bad slot, other player's item, wrong slot type, flood: all rejected, no state change |
| Flood writes | 1,000 moves: ≤ 32 writes |
| Full suite | 0 failed; ≥ 4,096 passed plus the new tests; 43 skips only |
| Real-process flow in Glue | passes on every database copy |

If `server.join-accepted` fails, in this order: shorten the modifier JSON names on the wire (`s`, `o`, `v`, about 90 B), then omit `equipSlots` when it's main hand only and default on the client (about 60 B). Don't raise the gate without recording why.

## Risks

1. **Join size margin is about 50 B.** The estimate is hand-counted from the JSON shape, not measured.
2. **Client and server stat sums can differ in one case:** a future item whose slot rule the client doesn't apply. The server sends `Modifiers` empty for inert items to prevent this. The client only displays the result, and health is authoritative.
3. **Auto-equip change.** A row set with a recorded grant and an empty slot 100 used to be auto-filled, and now stays empty. It only affects a failed slot write or a hand edit, and the player can click equip.
4. **`ModifierDigest` cache staleness.** Any new slot-changing path must call the refresh. All writes go through private helpers, and a test compares the cache against a recompute after a random action sequence.
5. **Shared legacy items.** `SharedItemReferences` rows mean one item in two players' slots. Destroy leaves the item row for the other holder, but this player's in-memory map drops it. Only corrupted data reaches this.
6. **Lantern in the off hand is a small power gain.** It's a choice the player makes, and starter balance is otherwise unchanged. Change `equipSlots` or the modifiers if it's unwanted.
7. **Placeholder gear numbers.** Effective caps of 50 can waste high rolls.
8. **CLI seed input.** It uses `world_time_ticks` and the item id, so two databases with different histories give different rolls. That's fine: it isn't a lockstep path. Loot in wave I must use the tick seed.
9. **`json_valid` in the bundled SQLite.** It's built in since 3.38, and window functions in 0007 already passed on the real build. The migration test proves it on `e_sqlite3`.
10. **First-person light in the off hand** still sits at the right-hand anchor. Minor, and left for art.
11. **Budget edge.** A test that sends more than 16 real inventory changes without ticks will start seeing `Throttled`. Advance ticks in the test; don't lift the budget.
12. **Destroy is permanent,** and there's no undo. The HUD confirm plus `ItemInstanceId` are the only guards, and the automatic backup doesn't cover it.
13. **Wave I depends on this.** `TryGrantRolled` returns `BackpackFull` for U8. Armor reaches enemies through `EffectiveArmor`, and the `ArmorNoEffectTests` pin is meant to be changed by wave I, on purpose.

## What Could Not Be Established

- No .NET build, test or measurement was run. All byte and millisecond figures are from the ledger or hand estimates. The migration ran only on Python's SQLite 3.50.4, on a reduced table.
- The current `server.join-accepted` size at `50cfab7`. The 3,565 B figure predates waves F and G, and I did not confirm which payload the roadmap measured.
- Whether any existing test expects auto-equip with a recorded grant and an empty slot 100. A search for named grant arguments found nothing, but `PersistedPlayerInventory`'s third positional argument wasn't traced through every fake. The first full run will show it.
- Whether `[JsonIgnore]` on a positional parameter passes the reflection contract test next to `ActorSnapshotJsonConverter`. `StarvedTicks` uses a conditional ignore, not an unconditional one.
- How many tests assert a slot write count on a repeated equip.
- Which of the 15 `Equal(7,` files pin the schema, and whether `TerrainChunkMessageCodecTests` and `WorldTransferLimitTests` still hold a version literal in a form my search missed.
- That an unregistered `visualId` really crashes a live client; this is from reading the code, with no surrounding catch found on that path. No run confirmed it.
- Whether the mirrored off-hand pose looks right. It needs a person looking at it, as does the panel layout.
- The bundled `e_sqlite3` version.
- Whether the perf harness's clients ever equip anything, so whether its numbers exercise this wave at all.
- Unix behaviour of the new CLI's lease path, as with wave F.

No open question blocks the start. H-D6 (offline-only grant), H-D9 (lantern stats in the off hand) and the placeholder gear numbers are the three choices the user may want to review.
