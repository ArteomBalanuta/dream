# Roadmap — the royal sorcerer regalia set, from a handed-over glTF builder

## Where this came from

Another session handed over `build_sorcerer.py`, a self-contained procedural builder (numpy + Pillow,
no Blender) for a "Dark Fantasy Royal Sorcerer Armor Set", with the request to run it and incorporate
the result. It was run, in isolation, and it does exactly what it claims:

- `royal_sorcerer_armor.glb`, 749 KB, glTF 2.0, header and length self-consistent
- 1,669 vertices / 1,620 triangles, one mesh, one material, one draw call
- a 26-bone armature (Hips > Spine > Chest > Neck > Head, limbs, plus Cape1-3, Tabard1-2, Pauldron.L/R)
- `POSITION NORMAL TEXCOORD_0 COLOR_0 JOINTS_0 WEIGHTS_0`, AO baked into COLOR_0
- 1024² albedo / metal-rough / emissive atlas, embedded
- an `Idle_ClothSway` animation, `doubleSided` material
- bounding box (-0.724, -0.001, -0.205) .. (0.724, 1.956, 0.215) — 1.96 m tall, A-pose

It is good work. It also cannot be dropped into this game, for four reasons that are worth writing
down because they are properties of THIS engine, not faults in the asset.

## Why the asset cannot be used directly

1. **It is skinned to its own skeleton.** Equipment here is not skinned at all: `WornArmourRenderer`
   draws a handful of procedural parts on the OVERRIDDEN node matrices of `PlayerBodyNode.{Head,
   Torso, LeftArm, RightArm, LeftLeg, RightLeg}`, and the player model itself
   (`dream/animations/players/player-grim.glb`) is a rigid part hierarchy with no skin. The handoff's
   26 bones have no counterpart to retarget onto.

2. **It is one suit; this game has seven slots.** Head, body, hands, feet, legs, back and companion
   are independent and mix freely across 21 existing pieces. A single merged mesh cannot be equipped
   a helm at a time.

3. **Scale and pose are baked in.** It is 1.96 m in metres with the arms frozen at 45 degrees. The
   figure here is 1.2 world units (about 1.904 m) and parts are authored in model units where 1 m =
   1.408 of them; an arm pose baked into vertex positions would not follow our arm nodes.

4. **The palette is the decisive one.** Measured over its albedo atlas: **62% of texels exceed 0.42
   in some channel and 24% exceed 0.80**. The world pass here has no tonemap and multiplies albedo by
   a gain that reaches about 2.2 beside a brazier, so the gold trim and most of the indigo would clip
   to flat white. Its metallic-roughness texture would also go unread: the actor shader takes scalar
   factors, and `uEquipSurface` is per material, not per texel.

On top of that the asset pipeline here requires original procedural art whose generator lives in
`tools/ui-assets`, passes `check_sources.py` and is gated byte-for-byte against its committed output.
A standalone script writing a 750 KB GLB into the repo root satisfies none of that.

## What we take instead

The **design**, which is excellent and maps almost one-to-one onto the shape library built in the
sculpted-equipment wave. That is the whole point of having built it:

| the handoff's module | our shapes |
| --- | --- |
| hood/mask, brow band, nasal guard, 5 radiating crown spikes | `Dome` + `Ring` + `Taper` + five `Horn` |
| tapered cuirass, high flared collar, swept pauldrons, chest gem | `Plate` + `Flare` + `Dome`/`Horn` + `Gem` |
| gauntlets, gold elbow and wrist cuffs, forearm spikes | `Plate` + `Flare` + three `Horn` |
| greave, gold knee cap, pointed toe cap | `Plate` + `Plate`/`Spike` + `Taper` |
| diamond tabard, narrow gold-bordered cape | `Panel` |

Indigo and gold is also exactly our `Gilt` + a cold field, and the glowing gem and eye slits are what
`Arcane` is for.

## The one thing the palette could not give

There was no indigo in the palette that was not nearly black: `ClothCold` is (0.07, 0.078, 0.10) and
reads black at the shipped zoom. The set is indigo plate with gold trim, not black plate with gold
trim, so the frozen contract adds **`ArmourMaterial.Indigo`**, enamelled plate at (0.095, 0.080,
0.185) — about (0.21, 0.17, 0.40) at the peak gain, so a clear deep indigo that never clips —
metallic 0.85 with the plate pattern and a low grain, because lacquer does not pit like bare iron.

This is a deliberate, single addition to a palette the project otherwise treats as frozen, and
`check_char.py` guards it: a palette material that nothing draws is a failure, so the material and
the set have to land together.

## Workstreams (frozen contract at `feat/sorcerer-set`, 6446a0d8)

| id | owns | builds |
| --- | --- | --- |
| WS-A | `ArmourMeshCatalog.cs`, its tests, `check_char.py` | the six armour pieces |
| WS-B | `items.json`, `loot.json`, `EquipmentTintCatalog.cs` + its tests | the content, balance and Epic tier |
| WS-C | `gen_items.py`, `dream/ui/items/`, `check_items.py`, `check_loader.py` | the six inventory icons |

No two workstreams own a file. The six visual ids and six item keys are frozen across all three:
`worn.{head.sorcerer-crown, body.sorcerer-regalia, hands.sorcerer-gauntlets, legs.sorcerer-greaves,
feet.sorcerer-sabatons, back.sorcerer-mantle}` and `gear.sorcerer-*`.

Known cross-workstream gap, to be resolved at integration rather than by either side guessing: a gate
that cross-checks icon keys against `items.json` cannot pass on WS-C's branch alone, and WS-B's
coverage test cannot see WS-A's ids on its branch alone.

## Verification

- `dotnet build`, filtered `dotnet test` per workstream, then the full suite on the integration branch.
- `check_char.py` back to 0 problems (it is red on the contract commit by design), plus
  `check_items.py`, `check_loader.py`, `check_manifest.py`, `check_art.py`, `check_sources.py`,
  `enginelook.py`, and the content validator CLI.
- In-engine capture through `skills/capturing-dream-screenshots` with the loopback gallery, noon and
  midnight, judged against the handoff's reference renders for silhouette — not for colour, which is
  deliberately darker here.

## Kept for reference, not shipped

The handoff script and its output stay out of the repo. They live in this session's scratch directory
and are reference material for the eye only, exactly as the asset skill's art-direction rule requires.
