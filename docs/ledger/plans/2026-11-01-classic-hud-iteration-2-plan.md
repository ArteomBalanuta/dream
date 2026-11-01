# Plan: classic HUD iteration 2 (gear slots, footprint bag, forged art)

**Goal:** all 12 gear slots as real slots, a true 8x8 footprint bag, and a rebuilt art pipeline that takes the bar, gems, windows and icons from casual to grim forged iron.

**Evidence level.** Facts come from reading code and from read-only scans of the legacy DBs. Art numbers were measured on the images. Two throwaway spikes ran in my scratchpad. I built nothing and changed nothing in the repo, and I did not run the test suite.

**Paths.** Repo paths are relative because each implementer has its own worktree. Base root: `G:\workspace\projects\dream\.claude\worktrees\classic-hud-int`. Save as `docs/ledger/plans/2026-11-01-classic-hud-iteration-2-plan.md`.

**Standing rules for every implementer**
- Base is branch `integrate/classic-hud`, tip e6304a1. Worktrees start from master, so reset to the base commit (C0 commit for WS1-5) first.
- Nothing goes to master and nothing is pushed.
- Full suite: at most 2 runs per verification step. Isolate flakes instead of re-running.
- Use an absolute `BaseOutputPath` inside your worktree. Do not reuse `artifacts/verify-bin/`, because a hung test host from an earlier pass may still hold it.
- Commits end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Nothing bound to all interfaces. Do not click any firewall prompt. Do not stop processes you did not start. Do not write probes that can hang.
- Original art only. No source-game names in code, sprites or UI strings. Reference images are never committed and no generator reads them. Third-party game clients sit at the root of `G:\`; do not open them.
- Out of scope: Wave I known issues, enemy pathing.

---

## 1. What exists and what the screenshots show

### Facts that shape the design
- **Art is ahead of the game.** `items.json` already has `item.boots.plate`, `gloves.plate`, `legs.verdant`, `pendant.ward`, `ring.ice`, `cape.navy`, `wings.gossamer`, `pet.wisp`, `spear.crimson`, all with `meta.footprint`. `window.json` already has `well.1x1/2x2/2x3/3x2` and all `sil.*`. Spec 01 section 3.8 has the 12 well rects and section 3.9 the 8x8 bag.
- **The window needs no resize.** Today's bag is 4x4 of 40 u at (15,200), which is 160x160 u: exactly 8x8 of 20 u. Wells fit y 46..192. Window 432 u + bar 48 u = 480 u, so 720p (scale 1.5, 30 px cell) and 1080p (scale 2.25, 45 px cell) fit by construction.
- `StatSheet.SourceCapacity` is 16, so 12 equipment sources fit. `EquipmentLoadout` and `PlayerCombatant` size off `EquipmentSlots.Count`.
- **The alias conflicts with footprints.** Worn items keep a bag row (`PlayerInventoryService.cs:31-34`). With footprints that would block up to about 40 of 64 cells and show each item twice.
- **The 16-item rule fails on area alone.** 16 x 2x3 blades = 96 cells > 64, and only 8 fit. Migration needs a no-loss path for this.
- **Wire.** Worst case today: join 9,328 B and push 8,402 B for 20 entries, about 410 B each. 76 entries in today's shape would be about 31 KB, far over the 16,000 B gate.
- **Golden hash.** `GoldenCheckpoints` hashes actors, mutations, results and damage, with no inventory snapshot. `GrantItem` draws from the shared ObjectId allocator, so a change in *whether* a drop happens could move goldens. A change in *which item* drops cannot.
- **Migrations are SQL scripts, data-only is fine.** The migrator backs up and runs in one transaction. Legacy-e DBs are schema v6, with 61 rows (db4: 18) at indices {0,1,2,3,4,100} and 5 kinds.
- `gem.*.mask` and `gem.*.spec` exist and nothing draws them, so they can be repurposed with stable names.
- **Tools.** Python 3.13.13 has no numpy or PIL. Blender is absent (not on PATH, not in Program Files). RTX 4090 and 16 threads are present. `numpy 2.5.4` and `pillow 13.0.0` exist in the site-packages below. That venv has no `python.exe`, but system Python works with `PYTHONPATH` set to it (I ran it that way):
  `C:\Users\arteo\AppData\Local\Temp\claude\G--workspace-projects-dream\93e26748-42dc-4eb0-b48a-835aefeb26f2\scratchpad\venv\Lib\site-packages`

### Measured: reference against ours
Luma 0..1. Band energy is mean absolute difference between Gaussian scales, measured at each image's own px/u. Scripts:
- `C:\Users\arteo\AppData\Local\Temp\claude\G--workspace-projects-dream\80febfeb-89be-4722-b728-869c40af46ff\scratchpad\art-plan\measure.py`
- `C:\Users\arteo\AppData\Local\Temp\claude\G--workspace-projects-dream\80febfeb-89be-4722-b728-869c40af46ff\scratchpad\art-plan\crops.py`

Crops are hand-picked, and the reference is a soft upscale, so its finest band reads low.

| Region | Metric | Reference | Ours |
|---|---|---|---|
| Bar strip | mean / median | .199 / .110 | .165 / .087 |
| | p95 / p99 | .763 / .946 | .648 / .846 |
| | share > .85 | 2.9% | 0.9% |
| | mean saturation | .31 | .18 |
| | 0.5u band / 4u band | .0367 / .0437 | .0407 / .0331 |
| Dragon | bands 0.5u, 2u, 4u, 8u | .019, .046, .051, .044 | .043, .023, .019, .017 |
| HP gem | saturation / p95 | .80 / .859 | .60 / .637 |
| MP gem | mean / saturation | .277 / .85 | .091 / .41 |
| Window slab | std / 4u band | .179 / .034 | .072 / .0073 |
| Frame side | high frequency / p99 | .069 / .489 | .221 / .714 |
| Empty cells | high frequency | .036 | .078 |

### Why ours reads cheap
1. **Noise without form.** This is the main finding. Our dragon has 2.3x the reference's energy at 0.5 u and 0.37x at 4 u. Scales about 2 u wide alias to grit at 2 px/u while the big masses are missing.
2. **Lighting happens in sRGB-encoded values.** `uiart.py` says so in its docstring, and `studio()` adds spec to those values. Highlights can't roll off, and blacks sit in one muddy band.
3. **No real light transport.** One softbox, AO from a blur difference, no cast shadow, no roughness or metal model, no Fresnel, no wear driven by geometry.
4. **The gem is a vector sticker.** Flat polygon fills in 10-fold radial symmetry, plus a pale stroked arc (`rim_layers`). It has no refraction or depth, and the claw doesn't touch it. The MP gem is near black.
5. **Clean, uniform lines.** Q/W/E frames are thin even strokes with smooth gradients. The reference's lips are heavy, broken and uneven.
6. **Boxed composition.** Each part sits in its own black recess. The reference overlaps mass over mass: the dragon sits on the plate and the claw bites the gem.
7. **Flat slab.** 4.6x less energy at 4 u, so the window reads as grey concrete.
8. **Neutral palette.** Almost no warm ivory or gold against cold iron.
9. **Thin numerals.** 13 px bitmap text against bold lit numbers.
10. **Box downsample in gamma space** in `uiart.downsample`.

### Spike results (validation, not final art)
- **Gem:** faceted stone with Beer-Lambert absorption, exact Fresnel, total internal reflection and a liquid plane. Deterministic, no RNG in shading. 640k primary rays, 14 bounces, 57 planes, about 1.2 s per frame on one core. HP full reads mean .263, p95 .557, p99 .833, saturation .87. The level line breaks across the facets through real refraction.
- **Iron:** same height field, ours against linear GGX + horizon AO + cast shadow + curvature wear + filmic. New side reads mean .174, median .088, p95 .779, p99 .956, already beside the reference strip's .199/.110/.763/.946.
- **Limits:** the upper-left softbox facet is still too chalky, highlight area is below target, and the first iron pass was badly over-worn, so wear needs restraint. Two tuning passes got here.

Files (read `gem3_hp.png` and `iron2_cmp.png` first):
- `C:\Users\arteo\AppData\Local\Temp\claude\G--workspace-projects-dream\80febfeb-89be-4722-b728-869c40af46ff\scratchpad\art-plan\gem_spike3.py`
- `C:\Users\arteo\AppData\Local\Temp\claude\G--workspace-projects-dream\80febfeb-89be-4722-b728-869c40af46ff\scratchpad\art-plan\iron_spike2.py`
- `C:\Users\arteo\AppData\Local\Temp\claude\G--workspace-projects-dream\80febfeb-89be-4722-b728-869c40af46ff\scratchpad\art-plan\gem3_hp.png`
- `C:\Users\arteo\AppData\Local\Temp\claude\G--workspace-projects-dream\80febfeb-89be-4722-b728-869c40af46ff\scratchpad\art-plan\gem3_mp.png`
- `C:\Users\arteo\AppData\Local\Temp\claude\G--workspace-projects-dream\80febfeb-89be-4722-b728-869c40af46ff\scratchpad\art-plan\iron2_cmp.png`

---

## 2. Decisions

| # | Topic | Decision | Reason |
|---|---|---|---|
| D1 | Slot set | All 12 as real slots, indices 100..111. Enum appended, existing values unchanged. | Art, wells and stat sources exist. Each extra slot is one enum row, one label, one well row. |
| D2 | Bag | True footprint placement, 8x8, `slot_index` = top-left anchor cell (`row*8+col`). No rotation. | The reference and `meta.footprint`. 64 1x1 slots would throw away the look the user asked for. |
| D3 | Alias | **Dropped; reverses H-D1.** A worn item leaves the bag. | See section 1. The reference has no duplicates. |
| D4 | Footprints | `items.json` gets optional `footprint: [w,h]`, default 1x1, range 1..4. A test requires every shipped item to set it and to match the art's `meta.footprint`. In `ContentDigest`. | Server needs the truth, and the optional default keeps inline test fixtures valid. |
| D5 | Migration | Schema v10, data only: delete alias rows and remap 0..15 to the 2x anchors. Load-time repair handles the rest. | Repair is needed anyway for hand edits and future footprint changes, so it doubles as the migration's second half. |
| D6 | Overflow | Unplaceable items become **held-back rows** at 200..215. They drain automatically when room appears, and the HUD shows a count line. Loot never enters them. | Only path that keeps 16 x 2x3 lossless. No new UI widgets. Max 8 for legacy data. |
| D7 | Wire | Protocol **v13**. Hand-written compact snapshot converter; client rehydrates text from `ContentCatalog`. | Only way under the gate with room to spare. Precedent: actor frame converters. |
| D8 | Loot | New kinds added to `wolf` and `dire-wolf` with **the same nothing weight and the same total**. | Keeps drop-or-nothing identical, so the ObjectId sequence and goldens stay put. |
| D9 | Starter kit | Unchanged, version stays 2. | Avoids re-granting and pin churn. Players get gear from loot and `--items grant`. |
| D10 | Swap | Drop on exactly one item: mover takes the spot, other goes to the mover's old anchor if it fits, else first fit, else `NoRoom`. Two or more overlapped items: `NoRoom`. Atomic. | Rejecting every unequal swap would feel broken. |
| D11 | Full bag | Row-major first fit. Nothing fits: existing `Lost` notice. No auto-compaction. | Never rearranges a player's bag silently. Fragmentation accepted. |
| D12 | Drop snap | Ghost centres on the pointer. Target anchor is clamped into the grid while the pointer is inside it. | Forgiving at edges; the server still validates. |
| D13 | Rings | Two wells, one kind, stacking stats. No-target equip fills the first empty ring well. Well-to-well drag swaps. | Details in 3A. |
| D14 | Art stack | Pure Python: `numpy 2.5.4`, `pillow 13.0.0`. No Blender, no scipy, no Mitsuba. | Blender is absent. Byte-deterministic output is a gate. A gem is a convex polyhedron, so a small tracer suffices. |
| D15 | Light | New `forge.py`: linear light, GGX, baked AO, shadow and curvature, aging layers, filmic, bloom, linear Mitchell downsample. | Fixes causes 2, 3, 10. |
| D16 | Gem runtime | Bake full, empty, level map and glints. New vertex mode in the same batch. | Liquid, meniscus and glint with no extra draw command and no frame sequence. |
| D17 | **MP gem** | **Draw full and lit, no number, no pulse.** One constant, `ManaGemFill = 1f`. Side gauges stay empty glass. **This reverses the previous plan's "no faked data" rule for this one sprite.** | User named mana gems. A dead black stone is half of what they called cheap. It states no value. Flip the constant to undo. |
| D18 | Layout | Talon grows to overlap the gem; dragon grows; new under-gem setting. Sprite names stay. See 3C. | Fixes cause 6. |
| D19 | Numerals | New `hudnum.0-9` and `hudnum.slash` sprites on the Hud page for HP and level. | Fixes cause 9. Stays in the bar's art command. |
| D20 | Draw budget | **Stays 12 in play, 16 with windows and tooltip.** | Gem and additive quads share the Hud page, and modes are per vertex. Overlays stay in the window-art group. |
| D21 | Icons | Re-render all through `forge.py`, last and droppable. | They are bright and toy-like against a grim window. |

---

## 3. Designs

### A. Equipment slots

**Enum** (`EquipmentSlot`, values are stat sources and must not change), with wells from spec 3.8. `Wells[]` stays in enum order because the index is the `HudView.Worn` index.

| Value | Slot | Label | Index | Well rect u | Well | Silhouette |
|---|---|---|---|---|---|---|
| 0 | MainHand | Main Hand | 100 | 14,89,40,60 | 2x3 | weapon.left |
| 1 | OffHand | Off Hand | 101 | 134,89,40,60 | 2x3 | weapon.right |
| 2 | Head | Head | 102 | 74,46,40,40 | 2x2 | helm |
| 3 | Body | Body | 103 | 74,89,40,60 | 2x3 | armor |
| 4 | Hands | Hands | 104 | 14,152,40,40 | 2x2 | gloves |
| 5 | Feet | Feet | 105 | 134,152,40,40 | 2x2 | boots |
| 6 | Legs | Legs | 106 | 74,152,40,40 | 2x2 | pants |
| 7 | Neck | Neck | 107 | 54,89,20,20 | 1x1 | pendant |
| 8 | RingLeft | Left Ring | 108 | 54,152,20,20 | 1x1 | ring |
| 9 | RingRight | Right Ring | 109 | 114,152,20,20 | 1x1 | ring |
| 10 | Back | Back | 110 | 114,46,60,40 | 3x2 | wings |
| 11 | Companion | Companion | 111 | 14,46,40,40 | 2x2 | pet |

**New content.** Add to `ParseEquipSlots`. Rename sprites in `gen_items` from the current art so `item.<key>` resolves.

| Key | Name | Slots | Size | From sprite | Rolls common / uncommon / rare |
|---|---|---|---|---|---|
| gear.iron-gauntlets | Iron Gauntlets | Hands | 2x2 | gloves.plate | Armor 1-2/2-4/4-6, AttackSpeed .10-.25/.20-.40/.35-.60 |
| gear.iron-boots | Iron Boots | Feet | 2x2 | boots.plate | Armor 1-3/3-5/5-7, Vitality 0-1/1-2/2-3 |
| gear.plated-leggings | Plated Leggings | Legs | 2x2 | legs.verdant | Armor 2-3/3-6/6-9, Vitality 1/1-3/3-5 |
| gear.ward-pendant | Ward Pendant | Neck | 1x1 | pendant.ward | Vitality 1-2/2-4/4-6 |
| gear.iron-band | Iron Band | RingLeft, RingRight | 1x1 | ring.ice | Strength 1/1-2/2-3 |
| gear.travel-cloak | Travel Cloak | Back | 3x2 | cape.navy | Armor 1-2/2-3/3-5, Vitality 0-1/1-2/2-3 |
| gear.gossamer-wings | Gossamer Wings | Back | 3x2 | wings.gossamer | AttackSpeed .10-.20/.20-.35/.35-.50 |
| gear.ember-wisp | Ember Wisp | Companion | 2x2 | pet.wisp | Strength 1/1-2/2-4 |
| gear.hunting-spear | Hunting Spear | MainHand | 2x4 | spear.crimson | Strength 5-6/6-8/8-10, AttackSpeed .50-.90/.75-1.10/1.00-1.40 |

Existing items get footprints from the art: tools and iron blade 2x3, lanterns and torch 1x2, cap and vest 2x2. Movement speed is not a live stat, so boots cannot give it.

**Loot** (totals unchanged)
- `wolf`, nothing 75, items sum 25: cap 4, vest 3, blade 3, ward lantern 2, gauntlets 3, boots 3, leggings 2, pendant 1, band 2, cloak 1, wisp 1.
- `dire-wolf`, nothing 50, items sum 50: cap 6, vest 6, blade 5, ward lantern 4, gauntlets 6, boots 6, leggings 5, pendant 3, band 4, cloak 2, wings 1, wisp 1, spear 1.

**Ring pairs, traced**
- Two instances of one kind: stats stack through separate sources. `FindEquipmentSlotHolding`, masks and `BackpackMenu` already handle this, and the menu lists both wells.
- Digest includes the slot index, so left and right differ. Fine.
- **Bug to fix:** no-target equip uses the first listed slot and would displace the left ring with the right empty. New rule, applied only when the first listed slot is a ring: first empty ring well, else RingLeft. Other items keep H-D8.
- **Gap to fix:** `TryResolveDrop` calls well-to-well invalid. Allow it when the item's mask permits the target: `InventoryEquipIntent(SlotIndex = well, Target = well)`, swapping when occupied. Also covers a lantern moving between hands.
- Same instance in both wells is only reachable by hand edit: `CopyEquipment` skips an id it already counted. Add a test.

**Path changes:** `ContentCatalog`, `EquipmentLoadout` (sized by Count), `PlayerCombatant` (comment), `HudView.WornSlots = 12`, `CharacterPanelModel.SlotLabel`, `HudCapturePointerPlan.TryParseSlot` (`hands, feet, legs, neck, ring1, ring2, back, pet`), `InventoryWindow.Wells`.

### B. Bag grid

**`BagGrid` contract** is in section 5. `ulong` occupancy with bit = cell. Pure, allocation free, shared by server and client so the drop verdict and server rule cannot diverge. Unknown kind footprint: 1x1.

**Row ranges**

| Range | Meaning |
|---|---|
| 0..63 | Bag anchors |
| 100..111 | Worn |
| 200..215 | Held back |

Anything else stays an unknown row: kept and inert, as today.

**Service rules.** Same check order as today: ranges, load, occupied, plan, then budget last, so a rejection costs no budget and queues no write.

| Action | Rule |
|---|---|
| `TryMove(from, to)` | `from` is an anchor; `to` is 0..63; bounds and overlap via `PlanMove` (D10). New outcome `NoRoom = 10`, "the item does not fit there". |
| `TryEquip(source, target?)` | Remove source row, write well row. Displaced item goes to the source anchor if it fits, else first fit, else `BackpackFull`, nothing changed. |
| `TryUnequip(well, targetCell?)` | `InventoryUnequipIntent` gains optional trailing `int? TargetCell`, omitted when null. Target must be free, else `NoRoom`. Null uses first fit, else `BackpackFull`. |
| `TryDestroy` | Same, by anchor and item id. |
| `TryGrantRolled`, starter grant | First fit, else `BackpackFull`. |

**Atomic writes.** One queued closure: deletes guarded by expected id first, then upserts. The primary key is per `slot_index`, so order matters.

**Helpers that must change with the alias gone**
- `HasBackpackContent` must count worn rows, or a legacy player with everything worn is seeded a second kit.
- `BackpackHoldsKind` must include worn items.
- `IsCutShortSeed` compares kinds over bag and worn, not slots 0..k-1.
- `EquipFirstOccupiedSlotIfNone` moves the row instead of aliasing it.

**Migration, `0010_bag_grid.sql`, in this order**
```sql
-- Hand-edited rows in 16..99 would become legal anchors and could collide: park them as unknown.
UPDATE player_inventory_slots SET slot_index = slot_index + 20000 WHERE slot_index BETWEEN 16 AND 99;
-- A worn item no longer keeps a bag row.
DELETE FROM player_inventory_slots
WHERE slot_index BETWEEN 0 AND 15 AND EXISTS (
  SELECT 1 FROM player_inventory_slots AS e
  WHERE e.world_id = player_inventory_slots.world_id
    AND e.player_name = player_inventory_slots.player_name
    AND e.item_instance_id = player_inventory_slots.item_instance_id
    AND e.slot_index BETWEEN 100 AND 103);
-- Two steps, because slot_index is part of the primary key. Old slot i was a 2x2 block.
UPDATE player_inventory_slots SET slot_index = slot_index + 10000 WHERE slot_index BETWEEN 0 AND 15;
UPDATE player_inventory_slots
SET slot_index = ((slot_index - 10000) / 4) * 16 + ((slot_index - 10000) % 4) * 2
WHERE slot_index BETWEEN 10000 AND 10015;
```

**Load-time repair** (first `Ensure` per loaded inventory, tracked in a set cleared on `Evict`, writes in `Ensure`'s single closure)
1. Walk anchors ascending. Keep an item if it is in bounds and overlaps nothing kept.
2. Re-home the rest by largest area then old index, first fit.
3. What still does not fit becomes held back, 200+n.

Deterministic, so lockstep holds. The direct mapping alone already fits anything up to 2x2. Legacy-e rows need no re-homing.

**Held-back drain:** after any accepted move, destroy, equip or unequip, place held-back rows ascending, same closure. `BuildSnapshot` includes them in `Slots`. The client shows `N items held back: make room` in the free band at u (15,364), hidden at 0.

**Offline and tools**
- `ItemCli.Grant` joins kinds and uses `BagBoard` first fit.
- `WorldIntegrityCheck`: `EquippedWithoutBackpackItem` inverts to a count of worn items that still have a bag row. Simplify `SharedItemReferences`. `UnknownSlotIndexes` allows 0..63, 100..111, 200..215. Add soft counts for overlaps and held-back rows.

**Hostile client.** Overlap, out of bounds, huge ints, `From` on a non-anchor cell, a held-back row or a well, `To` on a well: all rejected before budget. Cost is at most 64 dictionary lookups per message.

**Wire, v13.** `PlayerInventorySnapshot` stays the in-memory model. A new `PlayerInventorySnapshotJsonConverter` writes:
```json
{"playerName":"x","columns":8,"rows":8,
 "bag":[[anchor,id,"kind",flags,[stat,value,...],stack?,name?,visual?],...],
 "worn":[[slot,id,"kind",flags,[...]],...]}
```
- `flags`: bits 0-1 rarity, bit 2 rolled, bit 3 inert.
- The modifier list is empty for an unrolled item, which is rehydrated from the definition. Trailing fields appear only when not default; name and visual only for a kind the catalog does not know.
- Read rehydrates `DisplayName`, `VisualId`, `Description`, `DetailLines`, `HeldLight`, `EquipSlots`, `Modifiers` from `ContentCatalog.Shipped`, so every consumer is unchanged.
- Read caps entries and throws `JsonException` past 8 modifiers or 200 entries.
- `Slots` becomes sparse.

**Worst-case estimate:** 64 rolled 1x1 + 12 worn = 76 entries x about 45 B = about 3.4 KB, plus about 1.7 KB of join overhead = about 5-6 KB. Plain JSON minus text fields would be about 15.8 KB, too close to the gate.

**Client**
- `HudView.BackpackSlots = 64`, array indexed by anchor. New `sbyte[64] BagCells` holds the anchor per cell. Hostile overlap: first wins.
- Hit test: pointer to cell to anchor, with item rect = anchor plus footprint x 20 u. Hover, drag and right-click work from any covered cell, with the same ids at `IdBagSlot + anchor`.
- Ghost at full footprint size centred on the pointer. While dragging, tile `cell.overlay.hover` or `cell.overlay.invalid` over each target cell, clipped to the grid. Swap shows valid.
- `TryResolveDrop` calls `BagGrid.PlanMove`. A well-to-bag drop emits Unequip with `TargetCell`.
- Remove alias tint and overlay code.
- Quads: at most 64 cells + 24 well sprites + items, within the observed 805 of 8192.
- Currency stays hidden: no gold exists in Simulation.

**F3 ImGui panel:** iterate the sparse `Slots` as a flow of buttons. `MoveTargets` returns anchors where the item fits, first 16. Held-back rows show as a line.

**Tick goldens and byte pins**

| Pin | Moves? | Why |
|---|---|---|
| `GoldenCheckpoints`, `PreExtractionGolden`, `MapSourceGoldenTests` | **No** | No inventory in the hash; D8 keeps the ObjectId sequence. If one moves, stop and report. |
| Protocol digit in envelope pins | Yes, 12 to 13 | Bump. |
| Actor and snapshot frames | Digit only | Untouched. |
| `InventoryItemWireTests`, inventory cases of `ProtocolFoundationTests`, `TransportJsonCodecEncodeTests`, `TransportJsonCodecDecodeTests` | Yes | New snapshot shape. |
| `StarterKitInventoryBytesTests` | Rebaseline | Must get smaller. |
| `WorstCaseInventoryWireSizeTests` | Rewrite | 64 + 12 entries, same 16,000 B gate. |
| `HandWrittenConverterTests` | Add | Pin the new converter. |
| `LootRollerTests.GoldenPicks_AreStable` | Kinds only | "nothing" positions and rarities must not change. |
| Migration tests `Assert.Equal(9, ...)` | Yes, 10 | Schema bump. |
| `InventoryUnequipIntent` bytes | No | Field omitted when null. |

**Test blast radius:** 40 test files, 164 call sites of equip, move and unequip, plus dense `Slots[i]`, `Slots.Count == 16` and "fill the bag" loops. WS1 adds one helper, `AnchorOf(kind)`, in test support.

### C. Art

**New pipeline, `tools/ui-assets/forge.py`.** Hi-res stays 16 px/u, atlas 4 px/u.

1. **Author form first.** Height from SDF plates and sweeps, plus per-pixel material id. Rule: primary forms at least 8 u, secondary 2-6 u, tertiary under 1 u and capped by the band gate. Replace hundreds of 2 u scales with 5-6 huge overlapping forged plates.
2. **Bake maps:** multi-scale normal, horizon AO over 8 directions, real cast shadow for the key, cavity (blurred minus height at two scales), curvature, edge mask from primary height only, so wear follows the form and not the noise.
3. **Age:** edge wear to bare steel, soot in cavities, rust in seams and only in cavities, dust on up-facing surfaces, directional file marks, hammer dents at 2 scales.
4. **Light in linear:** GGX with Schlick Fresnel, per-pixel roughness and F0, anisotropy along the forging direction. `GOTHIC_RIG` = cool-neutral key upper left, low amber candle, cold rim from below, plus a broad overhead reflection band so black metal shows big soft shapes.
5. **Finish:** bloom from values over 1, value-map multiply for composition (dark at group ends, brighter toward the gems), warm highlights and cold shadows, ACES-style tonemap, triangular dither for dark gradients.
6. **Downsample:** premultiplied, linear light, Mitchell. Light unsharp tuned for 2.25 px/u.

Starting parameters that worked in the spike are in `iron_spike2.py`: wear threshold, reflection gain, F0 values. Keep wear on edges. The first pass there shows what overdoing it looks like.

**Gem, `tools/ui-assets/gemtrace.py`**
- Original cut, not radial: seven-fold crown and pavilion, about 57 half-space planes, per-facet jitter, stone tilted so the table sits off centre.
- Convex half-space intersection, Fresnel split at every interface, TIR, Beer-Lambert per channel, liquid below a plane, up to 14 bounces. Supersample at least 4.
- **Sparkle comes from about 46 small lights in the environment. Keep emission low.** The spike proved both failure ends: too little gave a grey table, too much gave a flat disc at saturation .97.
- Fix: narrow the upper-left window so its facet stops reading chalky, and raise highlight area to the gate.
- `ProcessPoolExecutor` over tiles in fixed order, seeds per tile. `check_sources` allowlist adds `forge`, `gemtrace`, `concurrent`, `os`.

**Bake outputs (names and 200x200 px sizes unchanged)**

| Sprite | Content |
|---|---|
| `gem.<k>.full` | Stone filled to the top, tonemapped. |
| `gem.<k>.empty` | Same geometry, dark glass with residue. |
| `gem.<k>.mask` | R = refraction-warped level threshold (mean depth reached by the first two segments, normalised). G = meniscus weight. B = facet id for phase. A = silhouette. |
| `gem.<k>.spec` | Hot facet glints only, for the sweep. |

**New sprites**

| Sprite | Size u | Use |
|---|---|---|
| `gem.setting.left/right` | 62x56 | Dark iron bezel under the gem. |
| `gem.hp.spill`, `gem.mp.spill` | 128x64 | Additive gem light on the iron and talons, alpha scales with fill. |
| `hudnum.0-9`, `hudnum.slash` | about 8x12 | Bold ivory bar numerals. |

**Rect changes in `StatusBarPanel` and `meta.layout`**

| Item | Old | New |
|---|---|---|
| Talon | 140,1,59,46 | 128,-3,71,52, three talons biting into the gem |
| Dragon | 0,-46,107,46 | 0,-52,118,58, bottom 6 u over the plate |

Gem stays 98,-2,50,50. Right side mirrors as today. Update `SIZES_U` in `check_hud.py`, `HUD_LAYOUT`, and the `07-asset-plan.md` manifest.

**Runtime.** `UiVertexMode.Gem = 3` and `Additive = 4`, same program, same page, same command.
```glsl
// uGemOffset[k]: UV delta from gem.<k>.full to empty, mask, spec. Rim: r fill, g phase, b pulse, a sweep.
vec4 full = texture(uTexture, vUv), empty = texture(uTexture, vUv + uGemOffset[k].empty);
vec4 d = texture(uTexture, vUv + uGemOffset[k].mask), g = texture(uTexture, vUv + uGemOffset[k].spec);
float lvl = vRim.r + 0.006 * sin(6.2832 * (vRim.g + d.b));
float t = smoothstep(d.r - 0.012, d.r + 0.012, lvl);
vec3 rgb = mix(empty.rgb, full.rgb, t);
rgb += exp(-pow((d.r - lvl) / 0.018, 2.0)) * d.g * full.rgb * 1.2;   // meniscus
rgb += g.rgb * g.a * sweepBand(vUv, vRim.a);
rgb *= 1.0 + 0.35 * vRim.b * t;                                      // low-health pulse
```
- Six vec2 uniforms, computed in `UiBatch.SetSpriteTable` from sprite rects. The Gem mode only needs the page rects, so no strip atlas is required.
- Additive outputs `vec4(rgb*a, 0)`, valid with the premultiplied blend.
- Eased level, phase and pulse: `HudView.HealthLevel`, `ManaLevel`, `AnimationSeconds`, `GemPulse`, set in `ClassicHudSurface.Draw` from a frame dt capped at 0.1 s. Pulse only under 25% HP. No allocation.
- `UiSoftwareCompositor` implements both modes so previews stay valid.

**Coverage**

| Group | Work |
|---|---|
| WS4 bar | `hud.base.left/centre/right`, `hud.gapfill`, plates and slabs, Q/W/E sockets with heavy uneven lips, skill bracket and tile, icon buttons and plinth, EXP trough, frame, fill, cup, gauge caps and glass, gauge fills, dragons, talons, setting, gems |
| WS5 window | Crowns, slabs with large-scale mottling, cracks and pitting, sides quieter and darker, bottom, wells, cells, close button and glyph, tip panel, then icons |

Dragon and claw stay original. Keep the existing original silhouette direction, thicken the mass, and do not model it on the reference's head.

**Keeps working:** sprite JSON schema, `UiSpriteTable`, the `Required` list (additions only), tier pages (same rects).

---

## 4. Scope this round

**In:** 12 slots, 9 new item kinds, loot, 8x8 footprint bag, alias removal, schema v10, protocol v13, held-back rows, drag ghost and footprint highlight, ring swap, F3 parity, full art pipeline, gems with liquid, bar, windows, then icons.

**Not in:** rotation, auto-sort, stacking, equipped model visuals for the new slots, companion behaviour beyond stats, wings or cloak visuals, mana or gauge systems, currency, chat, licensed font, bag scrolling.

**Droppable if time runs out, in order:** WS5 icons, spill sprites, sweep and pulse.

---

## 5. C0 contracts and workstreams

Branches: `classic-hud-2/c0-contracts`, `c0-forge`, `ws1-server`, `ws2-client-inventory`, `ws3-gem-runtime`, `ws4-art-bar`, `ws5-art-window`.

### C0a contracts (one commit, before parallel work; suite must stay green)
**Owns:** `EquipmentSlot.cs` (12 values, Count 12), `ContentCatalog.cs` and `ItemDefinition.cs` (`Footprint`, slot names, digest), new `dream/Simulation/Inventory/BagGrid.cs`, `InventoryEquipOutcome.NoRoom` in `PlayerInventoryService.cs`, `InventoryUnequipIntent.cs`, `HudInteractionRequest.cs` (comments), `CharacterPanelModel.SlotLabel`, `HudView.cs` (`WornSlots = 12`, animation fields), `IUiCanvas.cs` (default methods), `UiSpriteNames.cs` (constants, not yet in `Required`). Plus `BagGridTests`.

Leave `InventoryConstants` and `HudView.BackpackSlots` alone; WS1 and WS2 flip them.

```csharp
namespace Dream.Simulation.Inventory;
public readonly record struct ItemFootprint(byte Cols, byte Rows) { public static ItemFootprint One => new(1, 1); }
public enum BagMoveVerdict : byte { Same, Move, Swap, Blocked }
public readonly record struct BagMovePlan(BagMoveVerdict Verdict, int OtherAnchor, int OtherTo);
public static class BagGrid {
    public const int Columns = 8, Rows = 8, CellCount = 64, MaxFootprint = 4;
    public static ItemFootprint FootprintOf(string? itemKind);                 // unknown kind: One
    public static bool TryMask(int anchor, ItemFootprint f, out ulong mask);   // false out of bounds
    public static int FirstFit(ulong occupied, ItemFootprint f);               // lowest anchor or -1
    // owners: 64 cells, anchor or -1. footprints: 64 entries by anchor.
    public static BagMovePlan PlanMove(ReadOnlySpan<sbyte> owners, ReadOnlySpan<ItemFootprint> footprints, int from, int to);
}
namespace Dream.Rendering.Ui;
public enum UiGemKind : byte { Health, Mana }
// on IUiCanvas; defaults keep every test fake compiling
void Gem(in UiSprite full, UiRect dst, UiGemKind kind, float fill, float phase, float pulse, float sweep, UiColor tint) => Sprite(in full, dst, tint);
void Additive(in UiSprite s, UiRect dst, UiColor tint) => Sprite(in s, dst, tint);
```

### C0b art core (parallel with C0a, before WS1-5)
**Owns:** new `tools/ui-assets/forge.py`, new `tests/check_art.py`, `tests/check_sources.py`, `tokens.json`, `uiart.py`, `tools/ui-assets/README.md`, `docs/superpowers/specs/2026-11-01-classic-mmo-ui/07-asset-plan.md` (final names and sizes). One-off: rename 9 sprites in `gen_items.py` and regenerate `dream/ui/items/*`, so content keys resolve from the start.

```python
@dataclass
class Maps: normal; ao; shadow; cavity; curvature; edge
@dataclass
class Surface: albedo; f0; rough; aniso; tangent; alpha
class Relief: ...                                  # superset of gen_hud.Relief with a material id
def bake(height, primary=None, key=None) -> Maps
def age(s: Surface, m: Maps, seed: str, wear=.3, grime=.5, rust=.2, dust=.1) -> Surface
def light(s: Surface, m: Maps, rig=GOTHIC_RIG) -> np.ndarray      # linear HDR
def finish(hdr, alpha, exposure=1.0, value_map=None) -> np.ndarray # float sRGB RGBA, hi-res
def downsample(rgba, factor=SS) -> np.ndarray                      # uint8, linear Mitchell
```
**Accept:** one sample plate through the whole chain lands in the bar value gates, and `determinism_errors` passes.

### WS1 server, content, persistence, wire
**Owns:** `PlayerInventoryService.cs`, `InventoryConstants.cs`, `PlayerInventorySnapshot.cs`, `WorldSessionRuntime.Inventory.cs`, `ItemCli.cs`, `WorldIntegrityCheck.cs`, `dream/config/migrations/0010_bag_grid.sql`, `dream/config/content/items.json`, `loot.json`, `TransportEnvelope.cs`, `TransportJsonCodec.cs`, new `PlayerInventorySnapshotJsonConverter.cs`, `docs/server-persistence-ddl.md`, protocol digits in `README.md` and `build-and-run.md`, tests in `dream.Tests/Simulation/`, `dream.Tests/Server/`, `dream.Tests/Multiplayer/`, `dream.Tests/Integration/`.

**Steps:** constants and row ranges; alias removal; rewrite move, equip, unequip and grant on `BagGrid`; repair and held-back drain; migration; content and loot; ItemCli and integrity; converter and v13; update tests with `AnchorOf`.

**Tests:** every outcome of move, swap and equip; atomic rows after each; 16 x 2x3 gives 8 placed, 8 held back and all drain; content footprint change and overlapping rows on load; hostile inputs spend no budget; migration from v9 and from a v6 copy, including rows in 16..99; ring swap and double-instance guard; legacy player with everything worn gets no second kit; v12 client rejected; unknown kind round trip; worst-case wire.

**Accept:** section 6 server gates pass, goldens untouched.

### WS2 client inventory
**Owns:** `InventoryWindow.cs`, `ItemOverlays.cs`, `HudViewBuilder.cs`, `HudView.cs`, `HudItemCatalog.cs`, `CharacterPanelModel.cs`, `Render.HudPanels.cs`, `PlayScene.HudRequests.cs`, `HudCapturePointerPlan.cs`, `PlayScene.Capture.cs`, `docs/configuration.md`, tests in `dream.Tests/Rendering/Ui/Panels/` and `dream.Tests/Client/Scenes/`.

**Steps:** 12 wells; 8x8 cells; occupancy; hit test from any cell; ghost and footprint highlight; drop rules with `PlanMove`; well-to-well; held-back line; remove alias drawing; ImGui flow list; capture grammar (anchors 0..63, new names).

**Tests:** every drop verdict, including clamp at edges; hover and right-click from every covered cell; hostile snapshot overlap; snapshot change mid-drag; 0 B frame; icon fit for 3x2 and 2x4 in wells.

**Accept:** works on synthetic snapshots alone, and end to end after WS1.

### WS3 gem runtime and bar panel
**Owns:** `UiBatch.cs`, `dream/shaders/uiVertex.glsl`, `uiFragment.glsl`, `Gl/UiGlRenderer.cs`, `ClassicHudSurface.cs`, `StatusBarPanel.cs`, `UiSpriteNames.cs` (`Required`), `dream.Tests/Support/UiSoftwareCompositor.cs`, `docs/rendering-and-assets.md`, tests in `dream.Tests/Rendering/Ui/`.

**Steps:** modes and uniforms; compositor parity; eased levels; new rects; setting and spill; `hudnum` numerals; `ManaGemFill`.

**Tests:** fill 0, .5, 1 in compositor; gem quads merge into the Hud command; easing bounds; no MP number ever; 0 B.

**Accept:** works with old art too (a v-gradient mask behaves like today's clip), so it does not depend on WS4 to pass.

### WS4 art: bar and gems
**Owns:** `tools/ui-assets/gen_hud.py`, new `gemtrace.py`, `tests/check_hud.py`, `dream/ui/hud/*`, `previews/hud_*`.

**Order:** gems, setting and talons, dragon, plates and gapfill, Q/W/E, EXP, gauges, buttons, numerals.

**Accept:** art gates plus critique pass.

### WS5 art: windows and icons
**Owns:** `tools/ui-assets/gen_window.py`, `gen_items.py`, `tests/check_window.py`, `tests/check_items.py`, `tests/check_loader.py`, `dream/ui/window/*`, `dream/ui/items/*`, `previews/window_*`, `previews/items_*`.

**Order:** slab, sides and crown, wells and silhouettes, cells, close button, tip panel, then icons (droppable).

**Accept:** art gates plus critique pass.

### Shared contracts
`BagGrid`, the snapshot in-memory shape (sparse `Slots`, `EquippedSlot` + `Equipment`), row ranges, `IUiCanvas.Gem/Additive`, `gem.*` channel meanings, final names and sizes in `07-asset-plan.md`. Changing any of these goes back through the coordinator.

### Merge order
C0a and C0b, then **WS1, WS2, WS4, WS5, WS3**.
- WS1 first because the rules and wire are the base.
- WS2 sits on it and needs it for end-to-end.
- Art merges cannot conflict with code.
- WS3 last so it can add the new sprites to `Required` once they exist, and the fallback proof runs on final art.

Then tester and reviewer per `CLAUDE.md`. The integrator writes the work log.

---

## 6. Gates

**Tests**

| Gate | Target |
|---|---|
| Build | 0 errors |
| Suite | Green, at most 2 runs per step |
| Tick goldens | Unchanged, by name |
| Byte pins | Only those listed in 3B |
| Worst wire, 64 + 12 entries | At most 16,000 B in join, push and push with 8 notices |
| Sprite footprint test | All shipped items agree with art |

**Migration, on copies only.** Sources, schema v6:
- `C:\Users\arteo\AppData\Local\Temp\claude\G--workspace-projects-dream\80febfeb-89be-4722-b728-869c40af46ff\scratchpad\wave-h-e2e\orig\db1\persistence.db`
- `C:\Users\arteo\AppData\Local\Temp\claude\G--workspace-projects-dream\80febfeb-89be-4722-b728-869c40af46ff\scratchpad\wave-h-e2e\orig\db2\persistence.db`
- `C:\Users\arteo\AppData\Local\Temp\claude\G--workspace-projects-dream\80febfeb-89be-4722-b728-869c40af46ff\scratchpad\wave-h-e2e\orig\db3\persistence.db`
- `C:\Users\arteo\AppData\Local\Temp\claude\G--workspace-projects-dream\80febfeb-89be-4722-b728-869c40af46ff\scratchpad\wave-h-e2e\orig\db4\persistence.db`

Each must reach v10, take a backup, keep every item id, keep every worn row, leave no worn item with a bag row, no anchor outside 0..63, integrity no findings. Then join on loopback and compare `--items list` before and after. Plus a synthetic v9 DB with 16 2x3 items and one with a hand-edited row in 16..99.

**Hostile:** extend `C:\Users\arteo\AppData\Local\Temp\claude\G--workspace-projects-dream\80febfeb-89be-4722-b728-869c40af46ff\scratchpad\wave-h-e2e\hostile_i.py` on loopback with overlap, out of bounds, non-anchor `From`, wells as `To`, and spam. All rejected, no write, no state change.

**Real GL capture.** Release build copied to a scratch folder with its own `config/player-profiles.db`, `DREAM_AUTO_CONNECT=1`, **no** `DREAM_SERVER_HOST`, own `DREAM_IDENTITY_FILE`, `DREAM_HUD_CAPTURE=<dir>`, `DREAM_HUD_CAPTURE_SCRIPT=...`, `DREAM_RENDER_PERF_LOG=1`. One join to create the character, then `--items grant` for boots, gauntlets, leggings, two bands, pendant, cloak, wisp and spear.

Script at `size:1920:1080`, then repeat at `size:1280:720`:
- `inventory`, snap
- hover each footprint size
- `dragging:<a>:<b>` to a free spot, to a swap and to a blocked spot, snap each
- `drag:<boots>:feet`, `drag:<gauntlets>:hands`, both rings, `drag:ring1:ring2`
- `drag:feet:<cell>`, right-click equip and unequip, destroy with confirm
- `burst`
- `fight:` for a health drop, snapping the gem at about 75, 50, 25 and 10%

Log stat rows before and after each. Every capture must report `dropped=0`.

**Performance**

| Gate | Budget |
|---|---|
| HUD draw commands | At most 12 in play, 16 with windows and tooltip; report the drag-ghost case |
| HUD CPU | Under 1.5 ms |
| GPU HUD | Under 1.0 ms at 1080p |
| Managed allocation | 0 B per frame on the surface test, with the gem animating |
| Quads | Report the maximum, under 8192 |
| FPS | At least 95% of tip e6304a1, same scene |
| `dream/ui` total | At most 10 MB (today 7.4 MB), because noisier art compresses worse; record the actual figure |

**Fallback:** rename `ui/hud/hud.png` in a copied build: ImGui surface, exactly one `[render-fault] [hud-classic]` line. F3 with `DREAM_ADMIN_HUD=1` toggles both surfaces, and the ImGui inventory can equip, move and destroy.

**Art gates, in `check_art.py`.** Constants only, on our generated composite at 2.25 px/u with no reference read. Gates are floors and ratios, not equality.

| Metric | Reference | Ours | Gate |
|---|---|---|---|
| Bar mean | .199 | .165 | .16 to .24 |
| Bar median | .110 | .087 | .08 to .14 |
| Bar p95 | .763 | .648 | at least .72 |
| Bar p99 | .946 | .846 | at least .92 |
| Bar share over .85 | 2.9% | 0.9% | 2 to 5% |
| Bar saturation | .31 | .18 | at least .26 |
| Bar 4u / 0.5u band | 1.19 | .81 | at least 1.1 |
| Dragon 4u band | .051 | .019 | at least .04 |
| Dragon 4u / 0.5u | 2.69 | .44 | at least 1.5 |
| HP gem saturation / p95 | .80 / .859 | .60 / .637 | at least .78 / .75 |
| MP gem mean / saturation | .277 / .85 | .091 / .41 | at least .22 / .78 |
| Slab std / 4u band | .179 / .034 | .072 / .0073 | at least .14 / .025 |
| Frame high frequency / p99 | .069 / .489 | .221 / .714 | at most .11 / .60 |
| Cell high frequency | .036 | .078 | at most .055 |

Also: `determinism_errors` passes, and all existing `check_*.py` pass.

**Mood contact sheet.** An uncommitted script in the scratchpad, reusing `crops.py`, puts ours and the reference side by side at equal px/u. Output stays in the scratchpad and is never committed.

**Human-style critique, before shipping.** An agent reads the PNGs at 1:1 and 4x crops (gem, claw, dragon, Q/W/E, plates, full inventory at 1080p and 720p, in-game GL capture) and answers:
1. Does the bar read as heavy, old, forged?
2. Would a stranger still call it casual?
3. Squint test: with an 8 u blur, do the big shapes still read?
4. At a 25% thumbnail, is the silhouette clear?
5. Can you tell iron, steel, brass and rust apart?
6. Do the gems pop and look like real cut stone?
7. Does the claw visibly grip the gem?
8. Any stroke-outline, sticker, flat-gradient or uniform-noise look?
9. Is wear on edges and grime in recesses, not scattered?
10. Is the design clearly its own, not a copy of the reference?

Any bad answer goes back to the art WS. At most 2 art loops per workstream, then report what still fails. Answers go in the work log.

---

## 7. Risks and what I could not establish

**Risks**
- **Art remains subjective.** Numeric gates are necessary, not sufficient. The spikes prove technique and speed, not finished art: the chalky upper facet, highlight area and restrained wear are still open.
- **Art volume is the schedule risk:** about 3,000 lines of generators are reworked. Hence the droppable list.
- **Alias reversal** is the largest behaviour change: 164 call sites in 40 test files, plus the starter-kit helpers in 3B. Missing one of those gives duplicate kits, so a test is named for it.
- **Fragmentation** can lose loot with free cells showing. Accepted by D11; the `Lost` notice covers it.
- **Rehydration** assumes client and server share content. A same-protocol build with different content shows wrong text. `ContentDigest` is logged, not enforced on the wire.
- **Filmic bright art plus `TextScale`:** text over the gems may need the dark halo retained.
- **Bigger dragons** overlap more of the inventory's bottom edge at small windows. Check at 1100x700.
- **D17 is a call the user may want to reverse.** One constant does it.
- **Legacy DBs** only cover 5 kinds and 5 bag rows, so the worst case rests on the synthetic DBs.

**Could not establish**
- How many kills, if any, reach `GrantLoot` in the golden run, or whether the 16-slot bag ever filled there (no test run). If it did fill, the larger bag could allocate more ObjectIds and move goldens despite D8. The test itself is the proof; stop and report if it moves.
- Real worst-case wire bytes. The 5-6 KB figure is an estimate.
- Whether the shader compiles on the user's driver, and whether the level-map threshold looks right at 112 px. The spike rendered true fill frames but not the baked map.
- Current `build_all.py` runtime, and pip access for a fresh venv. Fall back to the `PYTHONPATH` route above if pip fails.
- `bpy` from pip was not tried; not needed under D14.
- Native reference resolution. Its fine band is understated, which is why gates use ratios.

**Open questions:** none. All points have defaults.
