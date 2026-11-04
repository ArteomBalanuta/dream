# Ground assets: schema, budgets, adding one

Source of truth: `tools/ui-assets/groundkit.py` (Python side) and `dream/Rendering/Ground/GroundAssetSet.cs` (C# side).
Shipped set at db41fba1: flora 40 + stone 36 + decals 24 = 100 assets.

## Manifest `dream.ground/1`

One file per family, `dream/animations/ground/<family>.json`, written only by `groundkit.write_family`
(`json.dumps(indent=2, ensure_ascii=False)` + `"\n"`, LF bytes, floats rounded to 3 decimals):

```json
{
  "schema": "dream.ground/1",
  "family": "stone",
  "generator": "tools/ui-assets/gen_ground_stone.py",
  "assets": [
    {"id": "stone.boulder_01", "file": "stone/boulder_01.glb", "class": "rock", "layer": "accent",
     "radius": 1.148, "footprint": 1.05, "height": 0.818,
     "ground": ["grass", "dirt", "stone", "sand", "mud", "snow", "rock"], "weight": 0.8, "scale": [0.8, 1.3]}
  ]
}
```

| Field | Rule |
|---|---|
| `id` | `<family>.<stem>`, stem `[a-z0-9_]+`, unique across all manifests |
| `file` | exactly `<family>/<stem>.glb`, must exist; any GLB in the folder not listed is "stale" and fails |
| `class` / `layer` | class must allow the layer (table below); only `decal.*` classes use `decal`, and always |
| `radius` | max horizontal vertex distance from the origin, world units, > 0 (measured, never typed) |
| `footprint` | max abs x/z of vertices below y 0.15, world units (measured) |
| `height` | max y, world units (measured) |
| `ground` | non-empty subset of `grass dirt stone sand mud snow rock grass_path` (lower-case `GeometryMaterialId`) |
| `weight` | > 0, relative pick weight within its layer |
| `scale` | `[min, max]`, `0 < min <= max` |

The engine (`GroundAssetSet.Parse`) skips and logs (`[render-fault] ground-assets`) any entry with an unknown class,
layer or material, a bad number or a repeated id; a missing directory is an empty set. It never throws, so a broken
entry shows up as a missing asset in game, not a crash. It loads every `*.json` in the folder, sorted ordinally.

## Classes, layers and budgets (`groundkit.BUDGETS`, `budget_for`)

| Class | Layers | Triangles | Bytes per GLB | Atlas px |
|---|---|---|---|---|
| grass, pebbles, twigs, leaves, bones | cover | 400 | 40 000 | 128 |
| flower, item | cover | 600 | 40 000 | 128 |
| mushroom | cover | 1 200 | 40 000 | 128 |
| root | cover | 3 000 | 40 000 | 128 |
| rock | accent / cover | 2 500 | 140 000 / 40 000 | 256 / 128 |
| bush, sapling | accent | 4 000 | 140 000 | 256 |
| stump, log | accent | 3 000 | 140 000 | 256 |
| tree | landmark | 12 000 | 450 000 | 512 |
| decal.dirt moss puddle crack litter scorch stain | decal | 64 | 48 000 | 128 |
| decal.path | decal | 64 | 96 000 | 256 |

Family totals (`FAMILY_BYTES_MAX`): flora 5 000 000, stone 2 500 000, decals 1 200 000 bytes.

Runtime behaviour by layer (`GroundScatterLayout`, `GroundScatterRenderer`): landmarks one per 7x7 bucket, accents
one per 3x3 bucket, both off roads, the authored zone and props; cover up to two per cell and decals one candidate per
cell on level ground, both clumped by a 12-cell noise field. Decals and cover draw with the `cube` program, accents and
landmarks with the `enemy` program, culled per 8x8-cell block at 24 / 24 / 34 / 42 units. Placement is a fixed integer
hash of cell coordinates: no seed, no RNG, same on every client. Nothing collides.

## GLB container (`groundkit.glb_errors`)

- One node named exactly the stem; no skin, no animation; images embedded PNG, no URIs.
- Materials: `ground.baked` (atlas, double sided, opaque except on decals) and optionally `ground.contact`
  (alpha-MASK disc, only for accent and landmark). No `BLEND` (the world pass has blending off), no `emissiveFactor`.
- Decals (`gen_ground_decals.py`): one flat 16-gon fan at y = 0 exactly, normals +Y, one alpha-MASK `ground.baked`
  with cutoff 0.5, alpha only 0 or 255, outer texels clear, coverage 25-85 %, 0.6-1.8 wu across (path up to 2.5).

## Adding a ground asset to an existing family

1. Write a builder in `gen_ground_<family>.py` named exactly like the stem: `() -> list[gen_props.Part]`, in u
   (16 u = 1 m), z up, front +y, standing on z = 0, centred. Stone wraps builders in `ground()`; flora builders centre
   themselves. Name every random source by a fixed string (`U.rng("ground.flora.<stem>")`, `I.waves3(..., "<name>")`).
2. Add the `ASSETS` entry through the family helper: flora `_spec` / `_tree` / `_cover`, stone `_spec` (alias `s`),
   decals a `Decal(...)` in `DECALS` (class, painter, diameter, luma `ratio`, ground, atlas, weight, scale).
   Pick the class first; the class fixes the layer choices, the atlas size and the budgets above.
   `disc=(hx, hy)` in u only on accent or landmark. `weld=True` shares vertices (thin or many-piece meshes).
   Ages are `(wear, grime, rust, dust)`; rust only where the family's docstring allows it.
3. Iterate into a scratch root, never the shipped one:
   `python tools/ui-assets/gen_ground_<family>.py <scratch> --only <stem> --preview` (flora: run without `--preview`
   for the GLB, with it for the sheet). Read the printed `tris=` and `size=` against the budget.
4. Update the gate's expected set in `tools/ui-assets/tests/check_ground_<family>.py`:
   flora `EXPECTED_COUNTS`; stone `EXPECTED_COUNT`, `EXPECTED_CLASSES`, `EXPECTED_LAYERS` (and the ground-list rules in
   its docstring/spec gates); decals `COUNTS` (and `BANDS` for a new class).
5. Regenerate the whole family into the shipped root (no `--only`):
   `python tools/ui-assets/gen_ground_<family>.py`. This rewrites `<family>.json` and every GLB of the family.
6. Gate: `python tools/ui-assets/tests/check_ground_<family>.py` (full, not `--fast`, before committing), then
   `python tools/ui-assets/tests/check_sources.py`. If a no-key gate fails on a small asset, run `--measure`, fix the
   art first; only then add an `OVERRIDES` entry with the measured value and the reason (see gates-and-failures.md).
7. C# count: `dream.Tests/Rendering/Ground/GroundScatterLayoutTests.cs`,
   `TheShippedSet_OnTheHundredWorld_StandsOnAllowedMaterials_InMeasuredBands` asserts
   `Assert.Equal(100, assets.Assets.Count)` and per-layer bands (decal 400-1100, cover 1500-6000, accent 120-500,
   landmark 25-120). Update the count; re-measure the bands from the test output line `shipped: decal ...` if they move.
8. Run the ground tests: see SKILL.md step "Engine tests".
9. Capture in engine (skill `capturing-dream-screenshots`) with and without `DREAM_GROUND_SCATTER=0`, noon and night.

## Adding a new class

Python: `groundkit._CLASS_LAYERS` (or the decal list `for _d in (...)`) and `BUDGETS`. C#: `GroundAssetClass` in
`dream/Rendering/Ground/GroundAsset.cs` (decal classes must stay after `DecalDirt`: `IsDecalClass` is
`>= GroundAssetClass.DecalDirt`) and the `Classes` dictionary in `GroundAssetSet.cs`. Without the C# side the engine
skips every entry of that class. Add a negative in `groundkit.self_test` if the rule is new.

## Adding a new family

`groundkit.FAMILIES` and `FAMILY_BYTES_MAX`; a `gen_ground_<family>.py` exposing `ASSETS` and
`main(out_root=K.GROUND_ROOT, only=None)` that calls `build_ground_asset` / `glb_bytes` / `manifest_entry` per stem and
`write_family` once; an entry in `build_all.ASSET_GENERATORS`; a `tests/check_ground_<family>.py` modelled on
`check_ground_stone.py` (manifest, `family_errors`, determinism, container, budgets, grounding, `check_props` palette
and no-key gates, each with a synthetic negative). `.gitattributes` already pins `dream/animations/ground/*.json`
to `eol=lf`. The engine picks up the new manifest with no C# change if the classes exist.
