# Work log: profiled equipment shells (pass 4)

- Purpose: finish the "blocky and Minecraft-like" brief by attacking what was still producing it.
- Status: on `feat/profiled-equipment` from master `436feb7a`. Not pushed, not merged.
- Plan: `docs/ledger/plans/2026-11-19-profiled-equipment-plan.md`.
- Method: the swarm workflow in `CLAUDE.md` with `parallel-agent-delivery` — the shape library frozen
  and committed first, then three implementers on disjoint files, then two solo passes driven by what
  the pictures showed.

## What the audit found, and why the brief was still open

Pass 3 was taken to mean *remove the plain boxes*. It did: **50 models, 510 parts, zero
`EquipmentShape.Box`**. The obvious reading of the brief was already satisfied.

What was still producing the slab look is the primitives. `Plate` carries most of the armour and it
is a **cylinder** — one radius, one sweep, three rings at 0.96 / 1.00 / 0.96. A constant section
reads as an extruded shape however curved its ends are. `Panel`'s whole surface variation is one
gentle bulge, which across a part twenty pixels wide is one value.

## The technique, and where it came from

The `lowpoly-armor-generator` skill builds every rounded form with `loft(centers, radii, angles)`:
a **ring table** listing centre, two ellipse radii and arc extent per ring. `AppendArcShell` is that
in this project's unit frame, and the new shells are ring tables over it. Everything is fitted to
the unit box afterwards exactly as `Plate` is, so each is a drop-in and a part's authored `Size` is
still its real extent.

| Shape | What it is | What it answers |
| --- | --- | --- |
| `Pauldron` | swells to its widest a third down, then tucks and opens | shoulder caps that read as slabs |
| `Cuirass` | hourglass, elliptical section, hard waist ring | torsos that read as barrels |
| `Greave` | tapers 0.50 → 0.29, centre walking toward +Z | limb shells that read as pipes |
| `Lame` | sawtooth radius: three overlapping steps | layered plate faked with stacked thin parts |
| `Membrane` | ribbed sheet with a **scalloped** trailing edge | `Panel`'s bulge reading as one value |
| `Drape` | continuous cloak, five folds, ragged hem | cloaks built as six to eight loose falls |

## What landed

- **All 27 worn models** reviewed; shells swapped to `Pauldron` / `Cuirass` / `Greave` / `Lame`,
  part counts unchanged except the cloaks, which got shorter.
- **The three shields** rebuilt: the tower door now bows, the kite is one tapering shell instead of
  three stacked tiers, the buckler is three concentric rises. Four hilts traded a `Ring` + `Taper`
  stack for one `Lame` collar.
- **The shader**: the pattern frequencies were re-derived after an audit found `sin()` arguments in
  radians per metre being read as cycles per metre, which had put three patterns' features far wider
  than the part they sat on. A per-material rim replaces the flat Fresnel, derived from metallic and
  roughness so it needs no new uniform, and scaled down by the room left under the clip ceiling.
- **The cloaks** rebuilt around `Drape`: collar, three layers at three depths, trim. 9→7, 10→6,
  10→10, 9→7 parts.

## Three bugs the pictures found that no gate could

1. **`Lame` stepped by 0.01.** The first ring table ramped the radius smoothly with a hard normal
   split at each join. A crease across a 0.01 radius change is a crease nobody can see, and it
   rendered as a plain tapering shell. The radius is now a sawtooth. *Reading the ring table would
   never have shown this.*
2. **`Membrane`'s ribs could not reach the screen.** A sheet's ribs stand out along its thin axis,
   scaled by `Size.Z`, which for a wing is 0.05 and is pinned at or under 0.06 because a thick wing
   is a plank. A rib 0.055 deep in the unit frame is 0.0028 engine units — a third of a pixel. No
   amplitude inside a thin axis can ever be seen. The form moved into the **silhouette**: a scalloped
   trailing edge, 0.22 of the part's height, in an axis that fills its box.
3. **Arcane's emissive was three fifths of the wing.** Emissive is added AFTER the gain, so nothing
   shades it. At 0.55 of blue against a lit blue of about 0.37, most of every wing texel sat at a
   value no light could change, which is why the wings stayed flat whatever the mesh did. Now
   (0.09, 0.035, 0.17): the lit range is back in charge and it still glows at midnight.

## The tool this wave is really worth

`tools/ui-assets/tests/modelsheet.py` composes every model in both catalogues from the dumped unit
meshes and draws it at the shipped camera angle — all 50 by name, in a second, with no engine. Feed
it `EveryShapeDumpsToObjWhenAsked`, which writes OBJs when `DREAM_SHAPE_DUMP` names a directory:

```
DREAM_SHAPE_DUMP=<dir> dotnet test dream.Tests/dream.Tests.csproj --filter EveryShapeDumpsToObj
python tools/ui-assets/tests/modelsheet.py <dir> out.png armour
```

It is not the engine — no tint, no pattern, no point lights, one constant gain — but it answers the
geometry question, which is the one a gate cannot answer and a gallery capture answers only by luck
(the gallery step is chosen from the client clock, so you cannot ask it for a named piece). It is
what found all three bugs above and what showed the back items reading as floating rectangles.

## One test changed, and why

`LongCloaks_HaveAScallopedHem` asserted the hem stepped because the cloak was several side-by-side
boxes cut to different heights. That mechanism is gone; the rule is not. `EveryCloak_HasARaggedHem`
replaces it, asserts the same thing where it now lives — the lowest fall is a `Drape`, and its hem
step clears two screen pixels at the shipped zoom — and covers all four cloaks instead of two. The
old test passed the whole time the engine was showing rectangles.

## Verification

- `dotnet build dream\dream.csproj`: 0 errors.
- Full suite: **Passed 10188 / Failed 0 / Skipped 47 / Total 10235**.
- `check_char` 0 problems; `enginelook` `contract: 20 lines match the shaders`, `PASS`.
- In-engine captures at 1600×1800 with the gallery on, noon and midnight
  (`artifacts/captures/prof3-noon`, `prof3-midnight`): the two floating grey rectangles behind the
  figure are gone and a single cloak with a stepped hem hangs in their place; the pauldrons take a
  highlight gradient over the cap at midnight; the gold trim reads at the shipped size; the wings are
  shaded and notched rather than one flat violet chevron.

## Open items

- The gossamer wings are still the weakest piece in the catalogue. They are better — shaded, notched
  — but at this size and these pinned bounds they still read as a violet arrow rather than a wing.
  Their envelope is pinned by `Wings_SpanTheRoadmapEnvelope_Symmetrically`, so moving them is a
  deliberate decision, not a tuning pass.
- Carried from the sorcerer wave, untouched here: `dream/ui` at 15.60 MB against a 16 MB budget, and
  the loot invariant that lets a level-1 wolf drop the best armour in the game.
- The brushed-metal streak amplitude (0.42) and the rim exponent (3) were chosen by arithmetic
  against the clip ceiling, not by looking. They survive the captures above, but nobody has A/B'd
  them.
