# Roadmap — visible equipment on the player (worn armour), wave "worn-visuals"

## Goal
Every equippable item changes the character's on-screen silhouette for both the local and replicated
remote players, by extending the proven procedural held-item path to seven worn slots and giving every
item its own visual id.

## Decisions already taken by the coordinator (do not re-litigate)
1. **Strategy (a) OVERSIZE-AND-ENCLOSE.** The base figure stays as it is. An unequipped character still
   reads as armoured; that is accepted for this wave. Regenerating an undressed base figure is queued
   as a separate later wave.
2. **Neck and the rings are DROPPED from 3D.** They get per-item visual ids in items.json and are never
   replicated and never drawn. At 28x101 px a ring is under 2 px.
3. **Starter kit grants one leather set**, including to existing characters on top-up (into the bag,
   not auto-equipped).
4. **Cloak sway is deferred.** Rigid mantles this wave.
5. **Procedural C# boxes, not generated GLB.** No asset pipeline run in this wave.

## 0. The two facts that shape every decision

**Scale.** Measured `actorbox 706,475,734,576` = the figure is **28 x 101 px** at the shipped isometric
zoom. A helm gets ~14 x 12 px, a boot ~6 x 5 px, a ring under 2 px. Only **outline, mass and value**
survive. Greebling, bevels, studs and sway are wasted work.

**The base figure is already dressed.** `tools/ui-assets/gen_char_player.py` bakes a hood + bone mask +
kettle helm (brim `_oct(2.32, 2.52, 0.34)` at z 4.15 u, crown at 5.25 u, hood apex 6.1 u) on the **head**
node, and a chest lame + gorget bib + left-only pauldron lamellae + **a cloak** (hem z -5.4 u, half-width
3.70 u, back offset -3.20 +/- 0.90 u) + backplate on the **torso** node, plus knee cops and sabaton caps
on the legs.

## Unit frame (the single most likely way this ships floating helmets)
The armour catalog authors in the SAME frame as `WeaponMeshCatalog`: **blocky engine units in the
attachment node's own local frame, +Y up, +Z forward, X lateral.** 1 authored u in `gen_char_player.py`
= **0.088** engine units. Derive every offset as `u * 0.088`, exactly as the fist offset
`-0.9 = -10.2 u * 0.088` was derived. Do NOT author in metres and do NOT author in the 16 u = 1 m /
0.039-per-u asset frame.

**Armour draws at scale 1.0.** `HandScale = 1.7f` / `ShieldScale = 1.5f` exist because a blade was a few
pixels wide; armour must match the baked body exactly, so any scale factor breaks enclosure.

## Slots that ship
Head, Body, Hands, Feet, Legs, Back, Companion ship 3D. Neck (4 items) and Ring (4 items) get ids only.

## Icons: already done — DO NOT TOUCH
`dream/ui/items/` is a 69-sprite atlas; every gear item already has its own icon. Icons key off the
**item kind**, never off `visualId`. No workstream may touch `dream/ui/items/`,
`tools/ui-assets/gen_items.py` or `check_items.py`. Each item's existing icon IS the art reference: the
3D piece must read as the same object as its icon, at silhouette level.

---

## 1. Wire design

Positional, pipe-separated, trailing-trimmed **9-field key**, ascending `EquipmentSlot` order with
Neck/RingLeft/RingRight skipped:

```
mainHand | offHand | head | body | hands | feet | legs | back | companion
```

- `FieldCount = 9`, `MaxVisualIdLength = 64`, `MaxKeyLength = 64*9 + 8 = 584`.
- **Trailing empties trimmed on format.** A player wearing nothing emits exactly `"main|off"`,
  byte-identical to v19. `Parse` accepts 1..9 fields; missing trailing fields are empty. Strict superset.
- `null` when every field is empty.
- Hostile input, per field independently (a bad field is dropped alone, never the key):
  1. More than `FieldCount - 1` separators, or total length > `MaxKeyLength` -> `Empty`.
  2. Field empty or > 64 chars -> that field null.
  3. Any char outside `[a-z0-9._-]` -> that field null.
  4. **The field's prefix must match its slot.** Hands require `held.` (not `held.none`); head
     `worn.head.`; body `worn.body.`; hands `worn.hands.`; feet `worn.feet.`; legs `worn.legs.`;
     back `worn.back.`; companion `worn.companion.`. A boot on the head is unreachable from the wire.
  5. A valid-but-unknown worn id draws **nothing** (unlike held items, where unknown -> generic stick):
     a wrong box on the head is worse than no box.
- `SimulationStateChecksum.cs:142` already excludes a player's VisualKey. **No change there.**

**Protocol version: bump 19 -> 20.** Append this comment, keeping `// Decode rejects any other version.`
last:

```
// Version 20 (worn equipment visuals) widens the player VisualKey from "main|off" to nine
// positional fields "main|off|head|body|hands|feet|legs|back|companion", with trailing empty
// fields trimmed (a player wearing nothing still sends exactly "main|off") and Neck and the two
// ring slots deliberately absent - at the shipped isometric zoom the whole figure is 28x101
// pixels and a ring is under 2; a v19 peer reads a worn key's extra separators as a malformed
// key and draws an armoured player bare-handed, so both sides refuse it. Every other payload is
// unchanged apart from the version digit.
```

---

## 2. Per-item visual ids in items.json

The catalog keys on the **`visualId` string**, not the item kind (the client deliberately reads no item
catalog). One visual id per item kind, named after the icon sprite's suffix.

**Worn — replace the 9 flat per-slot ids with 29 per-item ids:**
- head: `worn.head.leather-cap`, `worn.head.iron-helm`, `worn.head.greathelm`
- body: `worn.body.leather-vest`, `worn.body.chain-hauberk`, `worn.body.plate-cuirass`
- hands: `worn.hands.leather-gloves`, `worn.hands.iron-gauntlets`, `worn.hands.plate-gauntlets`
- feet: `worn.feet.leather-boots`, `worn.feet.iron-boots`, `worn.feet.plate-sabatons`
- legs: `worn.legs.leather-leggings`, `worn.legs.plated-leggings`, `worn.legs.chain-chausses`,
  `worn.legs.plate-greaves`
- back: `worn.back.travel-cloak`, `worn.back.hooded-cloak`, `worn.back.grave-mantle`,
  `worn.back.gossamer-wings`
- companion: `worn.companion.ember-wisp`
- neck (ids only, never drawn): `worn.neck.ward-pendant`, `worn.neck.bone-amulet`, `worn.neck.iron-torc`,
  `worn.neck.hollow-locket`
- ring (ids only, never drawn): `worn.ring.iron-band`, `worn.ring.silver-ring`, `worn.ring.bone-ring`,
  `worn.ring.blood-ring`

**Held — 6 new ids + 6 new meshes.** Keep every existing id bound to one item (no renames):
- `held.weapon.sword` stays `gear.long-sword`; add `held.weapon.short-sword` (`gear.short-sword`) and
  `held.weapon.iron-blade` (`gear.iron-blade`)
- `held.weapon.axe` stays `gear.battle-axe`; add `held.weapon.hand-axe`
- `held.weapon.bow` stays `gear.long-bow`; add `held.weapon.short-bow` (`IsRanged: true`)
- `held.weapon.staff` stays `gear.ash-staff`; add `held.weapon.bone-staff`
- `held.tool.lantern` stays `tool.survey-lantern`; add `held.gear.ward-lantern` (`gear.ward-lantern`,
  `HeldLight` unchanged)

**One content bug to fix:** `potion.health` and `potion.mana` carry `"visualId": "worn.ring"` with empty
`equipSlots` — a stale placeholder. Set them to `consumable.potion.heal` / `consumable.potion.mana`.

**Starter kit: bump `starterKitVersion` 4 -> 5.** Add six entries at `"version": 5` with an `equip` well
(precedent: the torch at `OffHand`): `gear.leather-cap`->Head, `gear.leather-vest`->Body,
`gear.leather-gloves`->Hands, `gear.leather-boots`->Feet, `gear.leather-leggings`->Legs,
`gear.travel-cloak`->Back.
**Risk to VERIFY, not assume:** `GrantStarterKit` places every entry into a bag free slot by `FirstFit`
*before* `EquipStarterKitWells` moves the wells, so the kit must still pack: 26 occupied cells today +
5x(2x2) + 1x(3x2) = **52 of 64**. An entry that does not fit silently caps `grantedVersion`.

---

## 3. Armour mesh catalog and renderer design

Mirrors `WeaponMeshCatalog` / `HeldWeaponRenderer` exactly: boxes, one shared `TerrainFallbackCubeMesh`
`StaticMeshBuffer`, batched unit-cube draws, `material.baseColorFactor` + emissive per material change,
the identical `PrepareState`. **Duplicate `PrepareState` rather than refactoring `HeldWeaponRenderer`** —
keeps the files disjoint and the proven path untouched.

### Attachment frames (blocky engine units, node-local, derived as u * 0.088)

| Slot | Rides | Attach | Authored about | Enclosure envelope the piece must cover |
| --- | --- | --- | --- | --- |
| Head | `head` node world matrix (with pose override) | identity | (0, 0.30, 0.06) | y 0.00..0.56, abs(x) <= 0.23, z -0.20..+0.20 (covers brim hw 2.32 u = 0.204 and hood apex 6.1 u = 0.537) |
| Body | `torso` | identity | (0, 0.55, 0.00) | y 0.18..0.80, abs(x) <= 0.26 chest (<= 0.60 with shoulders), z -0.20..+0.17 (covers chest lame at y 0.74, hw 0.24) |
| Hands | **both** arm nodes, mirrored in X (`side = offHand ? +1 : -1`) | translate (side*0.20, 0, 0) | (0, -0.80, 0) in the translated frame | y -0.95..-0.55, abs(x) <= 0.11 (covers the bracer, -9.0..-5.4 u = -0.79..-0.475) |
| Legs | **both** leg nodes, mirrored in X | identity | (0, -0.42, 0) | y -0.10..-0.75, abs(x) <= 0.14 (covers cuisse -5.6 u = -0.49 and knee cop -6.95 u = -0.61) |
| Feet | **both** leg nodes, mirrored in X | identity | (0, -1.10, 0.05) | y -1.30..-0.88, abs(x) <= 0.13, z -0.05..+0.22 (covers the baked boot -14.72..-10.2 u and sabaton at -12.25 u, forward 0.70 u) |
| Back | `torso` | identity | (0, 0.40, -0.30) | **hw >= 0.36, back z <= -0.38, hem y <= -0.50, top y >= 0.84** — strictly outside the baked cloak (hw 3.70 u = 0.326, back -4.1 u = -0.36, hem -5.4 u = -0.475, top 9.5 u = 0.836) |
| Companion | `torso` | identity | (0.42, 0.95, -0.10) | none — a free-floating emissive orb |

Mirrored slots (hands/legs/feet) draw the **same model twice**, once per node, with
`Matrix4.CreateScale(side, 1, 1)` folded into the attach. All ride the node's **overridden** world matrix
(`O * nodeWorld`), so a flinch carries the cuirass and cloak, the arm hold carries the gauntlet, and the
air tuck carries the boots — free, because `PlayerPoseLayers` already computes those overrides.

### The cloak, specifically
No sway. Differentiate by **hem length, width, shoulder mass and hem silhouette**: `travel-cloak` short
and ragged; `hooded-cloak` long with a raised collar (**not** a hood — a hood over a hood reads as
nothing); `grave-mantle` a broad flat shoulder mantle with a short hem; `gossamer-wings` **not cloth at
all** — two thin pale/arcane planes at x +/-0.18..0.52, y 0.45..0.95, the biggest silhouette change in
the catalog and the cheapest.

### Materials: sample the figure's own palette, do not reuse WeaponMaterial
`WeaponMaterial.Steel` (0.46, 0.48, 0.52) is brighter than the baked figure's steel. Introduce
`ArmourMaterial` with colours from `gen_char_player.py`'s CUSTOM palette (output is albedo x gain with no
tonemap): `steel.cool` #5C6475 -> (0.36, 0.39, 0.46); `cloth.cold` #161A28 -> (0.086, 0.102, 0.157);
`cloth.ash.cold` #1E2024 -> (0.118, 0.125, 0.141); `cloth.red` #5A1E1A -> (0.353, 0.118, 0.102); plus
Leather / Bone / DarkIron / Gilt reused from the weapon values, and Ember/Arcane for the wisp and wings.

**Internal-contrast rule for every piece (the A3 lever):** procedural boxes get no bake and no AO, so
contrast must come from the author. Each piece must carry at least one **near-horizontal top facet** in a
light material (steel/bone) and at least one **deep dark mass** (`cloth.cold`/`DarkIron`).

**Part budget:** 3..8 boxes per worn piece, gated by a test.

### Silhouette pass
`LocalPlayerRenderer` draws held items **twice** — the silhouette branch (`:116`) and the normal pass
(`:133`). The armour renderer must be called in **both**, or armour vanishes while the body shows through
world geometry.

---

## 4. Workstreams (3, strictly disjoint). Merge order A -> B -> C.

### WS-A — "Wire and content"
Files:
- `dream/Rendering/Actors/PlayerEquipmentVisuals.cs`
- `dream/Simulation/Inventory/PlayerInventoryService.cs`
- `dream/Multiplayer/Protocol/TransportEnvelope.cs`
- `dream/config/content/items.json`
- `dream.Tests/Rendering/Actors/PlayerEquipmentVisualsTests.cs`
- `dream.Tests/Server/Persistence/StarterKitGrantPersistenceTests.cs`
- new `dream.Tests/Simulation/Inventory/PlayerWornVisualKeyTests.cs`

Steps: (1) widen `PlayerEquipmentVisuals` to Contract 1; (2) widen `RefreshEquippedSummary` to record a
visual id for each of the seven replicated slots (it already walks all twelve slots — capture
`definition.VisualId` per slot instead of only the two hands) and rewrite `BuildVisualKey` to the 9-field
trimmed format with `MaxPlayerVisualKeyLength = 584`; (3) `CurrentProtocolVersion = 20` + the comment;
(4) items.json: 29 worn ids, 6 new held ids, the two potion fixes, six starter-kit entries at version 5,
`starterKitVersion: 5`.

### WS-B — "Armour catalog and renderer"
Files:
- new `dream/Rendering/Actors/ArmourMeshCatalog.cs`
- new `dream/Rendering/Actors/WornArmourRenderer.cs`
- `dream/Rendering/Actors/WeaponMeshCatalog.cs` (the 6 new held meshes)
- `dream/Rendering/Actors/HeldWeaponRenderer.cs` (owner of record; expected to stay untouched)
- `dream/Rendering/Actors/PlayerPoseLayers.cs`
- `dream/Rendering/Actors/LocalPlayerRenderer.cs`
- `dream/Rendering/Actors/RemotePlayerRenderer.cs`
- `dream.Tests/Rendering/Actors/**` (all of it)

Steps: (1) `PlayerPoseLayers.TryGetNodeMatrix` + `PlayerBodyNode`, with `TryGetArmMatrix` reimplemented on
top (identical behaviour, existing test must still pass); (2) `ArmourMeshCatalog` — 21 drawn models to the
envelopes and material rules above; (3) `WornArmourRenderer`; (4) call it in both `LocalPlayerRenderer`
branches and in `RemotePlayerRenderer`, reading `remotePlayer.Equipment`; (5) the 6 new held meshes.

### WS-C — "Client presentation and the gallery"
Files:
- `dream/Client/Presentation/ClientScenePresenter.cs`
- `dream/Client/Presentation/RemotePlayerPresentationState.cs`
- `dream/Client/Scenes/PlayScene.Replication.cs`
- `dream/Client/Scenes/PlayScene.HeldLights.cs`
- `dream/Client/Scenes/PlayScene.Audio.cs`
- `dream.Tests/Client/Scenes/**`
- `dream.Tests/Integration/ClientScenePresenterLongSessionIntegrationTests.cs`

Steps: (1) collapse `RemotePlayerEntry.MainHandVisual/OffHandVisual` and the two
`RemotePlayerPresentationState` fields into one `PlayerEquipmentVisuals Equipment`, parsed once in
`ApplyEquipmentAndMotion` (the "keep the last value when a delta omits the key" rule is unchanged);
(2) the local path: `ReadEquipment` already loops all equipment slots — capture each slot's `VisualId` and
return it in `EquipmentPresentation`, so `_localEquipmentVisuals` carries worn ids. The local player's
appearance comes from the **inventory snapshot**, never from a VisualKey; (3) **the gallery deliverable** —
extend `LoopbackGalleryVisualKey` to cycle `ArmourMeshCatalog.KnownVisualIds` as well, one id per slot
advancing every 3 s.

### Shared contracts (verbatim)

**Contract 1 — WS-A owns.** Positional defaults keep every existing 2-arg call site compiling.
```csharp
public readonly record struct PlayerEquipmentVisuals(
    string? MainHand = null, string? OffHand = null, string? Head = null,
    string? Body = null, string? Hands = null, string? Feet = null,
    string? Legs = null, string? Back = null, string? Companion = null)
{
    public const char Separator = '|';
    public const int MaxVisualIdLength = 64;
    public const int FieldCount = 9;
    public const int MaxKeyLength = (MaxVisualIdLength * FieldCount) + (FieldCount - 1); // 584

    public static readonly EquipmentSlot[] KeyFields;

    public static PlayerEquipmentVisuals Empty { get; }
    public bool IsEmpty { get; }
    public bool HasHeld { get; }
    public bool HasWorn { get; }
    public string? Get(EquipmentSlot slot);
    public static PlayerEquipmentVisuals Parse(string? key);
    public static string? Format(in PlayerEquipmentVisuals visuals);
    public static string? Format(string? mainHand, string? offHand);
    public static string? Normalize(EquipmentSlot slot, string? visualId);
    public static string? Normalize(string? visualId);
    public static string PrefixFor(EquipmentSlot slot);
}
```

**Contract 2 — WS-B owns.**
```csharp
public enum ArmourMaterial { Steel, DarkIron, Leather, ClothCold, ClothAsh, ClothRed, Bone, Gilt, Arcane, Ember }
public enum ArmourMount { Head, Torso, Arms, Legs, Feet, Back, Companion }

public readonly record struct ArmourPart(
    Vector3 Center, Vector3 Size, ArmourMaterial Material,
    float PitchDegrees = 0f, float RollDegrees = 0f) { public Matrix4 ToMatrix(); }

public sealed class ArmourModel
{
    public string VisualId { get; }
    public ArmourMount Mount { get; }
    public IReadOnlyList<ArmourPart> Parts { get; }
}

public static class ArmourMeshCatalog
{
    public static IEnumerable<string> KnownVisualIds { get; }
    public static bool IsKnown(string? visualId);
    public static ArmourModel? Resolve(string? visualId);   // NULL for unknown
    public static Vector4 BaseColor(ArmourMaterial material);
    public static Vector3 Emissive(ArmourMaterial material);
}

public sealed class WornArmourRenderer : IDisposable
{
    public void Draw(Shader shader, Camera camera, Matrix4 projection, AnimatedModelInstance instance,
                     PlayerPoseLayers layers, in PlayerEquipmentVisuals equipment, Matrix4 modelMatrix);
    public void Dispose();
}

public enum PlayerBodyNode { Head, Torso, LeftArm, RightArm, LeftLeg, RightLeg }
public bool TryGetNodeMatrix(AnimatedModelInstance instance, PlayerBodyNode node, out Matrix4 matrix);
```

**Contract 3 — WS-C owns.** In `RemotePlayerPresentationState`, positions 11 and 12
(`string? MainHandVisual`, `string? OffHandVisual`) are replaced by one
`PlayerEquipmentVisuals Equipment = default`.

---

## 5. Verification

### Existing tests that touch these files (extend, do not rewrite)
- `dream.Tests/Rendering/Actors/PlayerEquipmentVisualsTests.cs` — the whole key format.
- `dream.Tests/Rendering/Actors/WeaponMeshCatalogTests.cs` — **`NoTwoItemsShareAShape` is the acceptance
  gate**; extend its id lists with the 6 new held ids.
- `dream.Tests/Rendering/Actors/PlayerPoseLayersTests.cs` — `TryGetArmMatrix` unchanged after refactor.
- `dream.Tests/Client/Scenes/PlaySceneEquipmentSnapshotTests.cs`, `PlaySceneInventoryPushTests.cs`.
- `dream.Tests/Integration/MasterMpGapTests.cs:204` — asserts a stranger receives a `held.`-prefixed main
  hand; must still pass unchanged.
- `dream.Tests/Integration/RolledItemEndToEndTests.cs`,
  `dream.Tests/Server/Persistence/EquipmentSqliteActionTests.cs`.
- Starter-kit consumers: `StarterKitGrantPersistenceTests.cs`, `ConsumableLoopbackTests.cs`,
  `PerPlayerInventoryPersistenceTests.cs`, `ItemCliTests.cs`, `PlayerInventorySnapshotConverterTests.cs`.
- No pinned `ContentDigest` literal exists.

### New tests worth writing (relevant tests only, speed over optimisation)
1. `ArmourMeshCatalogTests` — every expected id is known; 3..8 parts; **no two worn pieces share a
   shape**; per-slot **enclosure envelope** assertions (every part's AABB inside the slot envelope, and
   for Back/Head that the union AABB strictly *exceeds* the baked cloak/brim extents); every piece
   contains at least one light and one dark material; unknown id -> `Resolve` returns null.
2. `PlayerWornVisualKeyTests` — round-trip all nine fields; trailing trim (nothing worn => exactly
   `"main|off"`); a boot id in the head field is dropped and only that field; > 584 chars => `Empty`;
   10 separators => `Empty`; one bad char kills one field only;
   **`PlayerInventoryService.MaxPlayerVisualKeyLength == PlayerEquipmentVisuals.MaxKeyLength`**.
3. A content-consistency test: every `worn.*`/`held.*` `visualId` in the shipped catalog is unique per
   item kind, matches its slot's prefix, and (for the seven drawn slots) is known to the catalogs. This
   is the single test that makes "every equippable item visibly differs" true by construction.
4. One loopback test that a kitted player's key reaches a stranger with all seven worn fields intact.
5. `PlayerPoseLayersTests` — `TryGetNodeMatrix` returns the override-composed world matrix for each of
   the six nodes and false before the first `Update`.

### Gates
`check_sources.py` must stay at 0 problems. `check_char.py`, `check_actors.py`, `check_items.py` and
`gen_items.py` must be **untouched and unrun** — if any changes, a workstream has strayed out of scope.
