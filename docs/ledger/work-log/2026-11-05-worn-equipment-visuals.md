# Work log: worn equipment visuals

- Purpose: make every equippable item visibly change the character, not only the two hands.
- Status: on `integrate/worn-visuals` from master `04a8fbfe`. Not pushed, not merged.
- Method: the swarm workflow in `CLAUDE.md` (architect -> three parallel implementers -> integrator ->
  fix loops), with the discipline in the `software-engineering` skill.

## The gap this closed

Before this wave only the two hands rendered. All twenty armour items shared nine flat per-slot ids
(`worn.head`, `worn.body`, `worn.back`, ...), **nothing in the renderer read `worn.*` at all**, and the
replicated `VisualKey` carried only `"<mainHand>|<offHand>"`, so armour was not even on the wire. Held
items were only differentiated per archetype: three swords, two axes, two bows, two staffs and two
lanterns each shared one mesh.

Inventory **icons were already per-item** (69 sprites in `dream/ui/items/`), so the 2D half was done and
no icon work was needed. The gap was purely on-character 3D.

## What landed

- **Wire** (`PlayerEquipmentVisuals`, `PlayerInventoryService`, `TransportEnvelope`): the player
  `VisualKey` widens from `"main|off"` to nine positional fields
  `main|off|head|body|hands|feet|legs|back|companion`, trailing empties trimmed, so a player wearing
  nothing still formats to exactly `"main|off"` and is byte-identical to v19. Each field is validated
  alone and must carry its slot's prefix, so a boot on the head is unreachable from the wire. An unknown
  worn id draws nothing (unlike a held id, which falls back to a stick: a wrong box on the head is worse
  than no box). Protocol 19 -> 20.
- **Content** (`items.json`): 29 per-item worn ids and 6 new held ids, so no two items share a mesh.
  Also fixed `potion.health` / `potion.mana`, which carried a stale `"visualId": "worn.ring"`.
- **Catalog and renderer**: new `ArmourMeshCatalog` (21 models) and `WornArmourRenderer`, mirroring the
  proven `WeaponMeshCatalog` / `HeldWeaponRenderer` path — boxes drawn as batched unit-cube draws off one
  shared mesh, so a new item costs no GL object and no asset. `PlayerPoseLayers` gained
  `TryGetNodeMatrix`, with `TryGetArmMatrix` reimplemented on top of it.
- **Client**: one `PlayerEquipmentVisuals Equipment` replaces the two per-hand fields; `ReadEquipment`
  now carries worn ids from the inventory snapshot (the local player's appearance has never come from a
  VisualKey); the loopback gallery cycles the whole worn catalog so every mesh can be inspected with no
  content, vendor or starter-kit dependency.

## Decisions, and the evidence behind them

- **Oversize-and-enclose, not a regenerated base figure.** `gen_char_player.py` bakes a full outfit into
  the player mesh — hood, bone mask, kettle helm, left-only pauldron, gorget, **a cloak**, knee cops. So
  worn pieces are authored to fully contain the baked geometry at their node, making it an interior
  surface. The honest cost: an *unequipped* character still reads as armoured. Regenerating an undressed
  base figure is the correct long-term answer and is queued as its own wave; it would have coupled an
  asset-pipeline change, two byte gates and an A3 re-measure into a rendering wave.
- **Neck and rings get ids but no 3D.** Measured from a real capture (`actorbox 706,475,734,576`), the
  whole character is **28 x 101 pixels**. A helm gets ~14x12 px; a ring is under 2. Drawing them would be
  dishonest work.
- **The starter-kit armour grant was added, then reverted.** It was meant to let a new player see the
  feature. But **all 47 gear kinds already drop** (`loot.json`), so armour is reachable in normal play,
  and the gallery covers inspection. The grant silently gave new players armour (an unasked-for balance
  change) and broke ~30 tests re-expecting armor values, stat totals, kit counts and pinned bytes, plus
  pushed `EquipmentSqliteActionTests` into `BackpackFull`. Reverted; it bought nothing.
- **Procedural boxes, not generated GLB attachments.** At 28 px a loft-smooth attachment buys nothing
  visible, and a GLB attachment would need a normalisation and parenting story the loader does not expose
  (`PlayerModelAssetDefinition` normalises by a model's own authored height, meaningless for a helmet).

## Verification

- Measured serialized on a quiet machine: master `04a8fbfe` Failed 35 / Passed 7632 / Total 7676; the
  branch Failed 112 / Total 7790. All 35 master failures also fail on the branch, so exactly **76 were
  this wave's**, all protocol-version fallout. All 76 are fixed and verified green under two filtered
  runs (156 and 45 tests, 0 failed).
- **Those totals are partial runs.** Two `StatSheetTests` grids walked the raised attribute ceiling and
  stalled the suite, so ~290 tests never executed for anyone. A peer session fixed that
  (`fix/suite-hang`) and a complete run puts master at **~40 failures**, not 35. See `master-is-green`.
  The 76 attribution is unaffected, since both sides were truncated identically.
- Final targeted runs on the integration branch, both green: the wave's own areas (ArmourMeshCatalog,
  WeaponMeshCatalog, PlayerEquipmentVisuals, PlayerWornVisualKey, PlayerPoseLayers, Client.Scenes,
  ClientScenePresenter) **393 passed / 0 failed**; the protocol classes the 76 came from
  (Multiplayer.Protocol, ProtocolVersionMismatch, ProtocolV2*, WaveBTesterWorlds, StoredTerrainFixLoop)
  **737 passed / 0 failed**. No full-suite run was taken at the end: three other sessions were running
  suites, and a contended run produces spurious failures. Green under contention is trustworthy; red is
  not.
- Visual: `artifacts/captures/` holds the before, the gallery at noon and midnight, and the cloak
  iterations. Emissive pieces (ember wisp, gossamer wings) verified at midnight.
- After the renumber to 21: **1116 passed / 0 failed** across the protocol, wire, mismatch,
  armour-catalog and client-scene families. The one red test under those filters,
  `StoredWorldJoinLoopbackTests.GeneratedWorldJoin_FrameSequenceAndBytes_EqualTheC0Capture`, was
  measured at master `04a8fbfe` in a clean detached worktree: it yields the **identical** sha256
  `e9dfe27f` and the identical 6,127 bytes there and fails identically, so it is pre-existing and the
  join bytes are untouched by this wave. Its pin dates from `823f6eb9` and is stale on master.
  Corroborated afterwards: `integrate/master-green-v2` re-pinned that gate in `9f54e66b` to exactly
  `e9dfe27f...` / `6_127` - the same bytes this wave measured independently, without having seen that
  branch. The test goes green once the attribute origin is rolled back to 10 (route A, on `integrate/master-ready`): the pin was never wrong, it was correct for an origin of 10 all along, and the ceiling raise invalidated it. The combined tree re-measures to the pin's own 90a7bf84 / 6,117 - the identical hash, not merely the identical count. Note the pin is protocol-insensitive only by maintenance: `StoredWorldJoinLoopbackTests` normalises the version literal to 8 before hashing, and that search pattern names the current version explicitly, so a bump that misses it makes the replace a silent no-op. This wave deliberately does **not**
  cherry-pick the re-pin: it is master-side, that branch owns it, and duplicating it would only
  produce a merge conflict.

## The art took two iterations, both driven by looking at it

The first catalog passed every test and still looked wrong. The enclosure rule ("a worn piece must
strictly exceed the baked geometry") is satisfiable by one big box, and that is what it got: the hooded
cloak was a single 0.84 x 2.00 x 0.06 panel against a 0.62-wide torso. In game it read as **a flat navy
plank** that swallowed the figure. No assertion could have caught it, because the tests encoded coverage,
not shape.

1. **Taper.** Each cloak became a stack of 3-5 boxes narrowing toward the shoulders, at increasing depth
   (z -0.30 at the yoke to -0.48 at the hem) with a small increasing pitch, alternating Cold/Ash so
   neighbouring panels land in different light buckets. That fixed the uniform-mass reading.
2. **Scallop.** The outline was still one straight hem. The long cloaks' hems became 2-3 fold boxes of
   unequal length, the idea already proven by `travel-cloak`'s ragged tabs, which read best of the four
   in the first capture.

Tests now encode the *shape intent* rather than only coverage:
`EveryCloak_TapersFromNarrowShouldersToAWiderHem` and `LongCloaks_HaveAScallopedHem`. Those would have
caught the plank.

**Lesson worth keeping: a geometry test proves containment, not legibility.** Anything authored for the
eye needs a capture in the verification loop, not just a green filter.

## Findings worth keeping

- **The sed that bumped protocol literals missed every escaped form.** `protocolVersion":19` matches raw
  string literals but not `protocolVersion\":19`, which is how they appear in C# source.
- **Three tests had silently stopped testing anything.** `JoinAcceptedWorldDescriptorCodecTests`,
  `WaveBTesterWorldsLoopbackTests` and `StoredWorldJoinLoopbackTests` derive a bad frame with
  `Replace("\"protocolVersion\":19", "...\":6")`. After the bump the *search* pattern went stale, the
  replace became a no-op, the frame was never downgraded, and the tests still passed. Fixed.
- **Never run two suites at once in this repo.** Concurrent runs produce ~35 spurious failures, always in
  the persistence and world-lease families, and frequently leave a `testhost.exe` hung holding a lock on
  `dream.Tests.dll`, after which every later build fails with MSB3027.
- **Protocol v20 was contended, and this wave moved.** The inventory.drop wave also bumped 19 -> 20 and
  claimed it first, so this wave now ships **21** and the version comment says why rather than just
  carrying a new digit. The renumber had to touch five distinct spellings of the same number: the
  constant, raw `"protocolVersion":NN` fixtures, **escaped** `\"protocolVersion\":NN` fixtures, the
  `"Supported version: NN"` / `"this client uses vNN"` message strings, and the downgrade helpers whose
  `Replace()` *searches* for the current version. A sed that handles only the unescaped form leaves 40
  tests red - this wave hit that trap twice, which is why it is written down twice.
  `ProtocolVersionMismatchClientTests`' fake newer server moved 21 -> 22.

## Open

- Nothing blocks the merge. If the drop wave never lands, 20 is simply never used: `Decode` accepts only
  the current version, so an unreachable digit is harmless.
- Cloak sway is deferred: the walk clip deliberately holds the torso steady, and 1-2 px of sway is
  invisible at this zoom.
- An undressed base figure, so "no armour" reads as no armour, is a separate wave.
