---
name: generating-dream-assets
description: Adds, changes and regenerates Dream's original procedural art with the Python generators in tools/ui-assets - ground flora, stones, debris and floor decals (groundkit, dream.ground/1 manifests), world props, characters, terrain tiles, item icons and HUD/window/menu atlases - and gets them through the determinism, shipped-bytes, budget, palette and check_sources gates. Use when creating a new asset, editing a gen_*.py generator, fixing a red tests/check_*.py gate, updating a ground manifest, or regenerating committed GLBs or atlases.
---

# Generating Dream Assets

Every Dream asset is original procedural art: Python code with fixed seeds, byte-deterministic, committed together
with its output and gated against it. Follow the steps in order. Long tables live in `references/`.

## 1. Set up

- Python 3.13 with `numpy==2.5.4` and `pillow==13.0.0` (`tools/ui-assets/requirements.txt`). No Blender, scipy or GPU.
- Use the repo venv if present: `.venv/Scripts/python` (in the main checkout). Otherwise
  `python -m venv .venv` and `.venv/Scripts/python -m pip install -r tools/ui-assets/requirements.txt`, or point a
  system Python at existing packages read-only: `PYTHONPATH=<venv>/Lib/site-packages python ...`.
- Check the versions before trusting any byte gate:
  `python -c "import numpy, PIL; print(numpy.__version__, PIL.__version__)"` must print `2.5.4 13.0.0`.
- Run everything from the repo (or worktree) root. Gates write temp renders under `artifacts/tmp` (gitignored).

## 2. Pick the family

| You want | Generator | Lands in | Gate |
|---|---|---|---|
| scattered ground dressing (tree, bush, grass, flower, mushroom) | `gen_ground_flora.py` | `dream/animations/ground/flora/` + `flora.json` | `check_ground_flora.py` |
| rocks, pebbles, stumps, logs, roots, twigs, leaves, bones, dropped gear | `gen_ground_stone.py` | `.../ground/stone/` + `stone.json` | `check_ground_stone.py` |
| flat floor decal (dirt, moss, puddle, crack, path, litter, scorch, stain) | `gen_ground_decals.py` | `.../ground/decals/` + `decals.json` | `check_ground_decals.py` |
| placed scenery / buildings (hamlet) | `gen_props.py` | `dream/animations/props/<name>.glb` | `check_props.py` |
| player, villagers, hound | `gen_char_player.py`, `gen_char_npc.py`, `gen_char_beast.py` | `dream/animations/players|npcs|enemies/` | `check_char.py`, `check_actors.py` |
| voxel surface tile | `gen_terrain.py` | `tools/ui-assets/terrain/`, copied by hand to `dream/animations/library/forge/<n>/<n>.albedo.png` | `check_terrain.py` |
| inventory icon | `gen_items.py` | `dream/ui/items/` | `check_items.py`, `check_loader.py` |
| HUD, stone window, menu, damage digits, particles | `gen_hud.py`, `gen_window.py`, `gen_menu.py`, `gen_fx.py`, `gen_particles.py` | `dream/ui/<hud|window|menu|fx|particles>/` | `check_<same>.py`, `check_manifest.py`, `check_art.py` |

**Worn armour and held weapons are NOT in this table and have no generator here.** They are built at
render time in C# from `WeaponMeshCatalog` / `ArmourMeshCatalog` plus the sculpted primitives in
`EquipmentShapeLibrary`, so a new item costs no asset and no GLB. Do not look for a `gen_equipment.py`
and do not add one - see "Equipment Rendering Path" in `docs/rendering-and-assets.md`. The one Python
file that touches them is the gate `tools/ui-assets/tests/check_char.py`, which parses both catalogs
and rasterises each part as its BOUNDING BOX to measure clipping, contrast and envelope.

Full table with runtime consumers, kits and what each new asset must also touch (C# catalogs, spec manifests,
content footprints): `references/asset-families.md`. Ground schema, budgets and the add-a-ground-asset procedure:
`references/ground-assets.md`. Read `tools/ui-assets/README.md` for the section of the family you are changing.

## 3. Conventions every 3D asset follows

- Authored in u (16 u = 1 m), isoforge axes: x right, y = front, z up, standing on z = 0, centred on the origin.
- Exported at `gen_props.WORLD_UNITS_PER_U` = 0.039 world units per u, glTF Y up, +Z front, base at y = 0. The player
  is 1.2 world units tall; a cover asset stays under 0.4.
- Rigid, one node, embedded PNG, no URI, no skin or animation (characters excepted).
- One baked albedo atlas (`charforge.unwrap_split` + `bake_albedo`). Props encode it with `gen_char_player.encode_atlas`
  (true linear plus a lift). Ground assets use `groundkit.encode_ground_atlas` instead: baked bytes as-is times
  `GROUND_ALBEDO_GAIN` (1.5), because the engine samples bytes as linear, like the terrain tiles. Decoding to true
  linear made the ground assets about 6x too dark, so they read as black cutouts. Thin double-sided sheets (grass,
  leaves, twigs, bushes) go through `groundkit.sheet_normals`: downward normals are flipped and leaned toward +Y. The
  shaders have no back-face handling. Albedo, AO, cavity, wear, soot, dust only - **no directional key light
  and no cast shadow are baked**; the engine lights it and the sun moves.
- Contact shadow = a separate alpha-MASK disc primitive (`gen_props.contact_texture` / `disc_arrays`), never BLEND
  (blending is off in the world pass). Ground: only accent and landmark assets carry one.
- No emissive except the brazier's coals. Every random source is named (`U.rng("ground.flora.<stem>")`,
  `waves3(..., "<name>")`); builders read no clock, environment or file.

## 4. Add or change an asset

1. Read the family's generator top docstring and its gate's docstring (they list exactly what is gated).
2. Write the builder and its spec entry (`ASSETS`, `PROPS`, `ICONS`, `TILES`, `DECALS`) in that generator. Reuse the
   family's materials and helpers; keep shapes in the form bands (primary >= 8 u, secondary 2-6 u, tertiary < 1 u).
3. Iterate into a scratch folder, not the shipped one:
   `python tools/ui-assets/gen_ground_stone.py artifacts/tmp/try --only boulder_04 --preview`.
   Check the printed `tris=` and `size=` against the class budget and look at the preview sheet in
   `tools/ui-assets/previews/`. The sheets are tracked (and `check_ground_flora.py` compares its sheet), so an
   `--only --preview` run leaves a partial sheet that step 5 must overwrite.
4. Update the gate's expected set (counts, names, footprints) and any C# catalog the family needs
   (`references/asset-families.md`, "What a new asset touches").
5. Regenerate the family into its shipped location with a **full** run of that generator (no `--only`, no out dir):
   `python tools/ui-assets/gen_ground_stone.py --preview` (stone and decals write GLBs, manifest and sheet in one
   run; flora needs `gen_ground_flora.py` and then `gen_ground_flora.py --preview`, which writes only the sheet).
   A ground `--only` run writes only the named GLBs and leaves `<family>.json` untouched (it prints a note), so
   a new stem only enters the manifest through a full run. `build_all.py` regenerates every UI atlas plus actors, props
   and ground (not terrain or iso) and rewrites all manifests; use it only when a shared kit changed.
6. Run the family gate in full, then hygiene:
   `python tools/ui-assets/tests/check_ground_stone.py` and `python tools/ui-assets/tests/check_sources.py`.
   After editing a kit, also run its self-test (`python tools/ui-assets/groundkit.py`, `gltfwrite.py`, `charforge.py`).
7. Engine tests touching the family, from PowerShell (the absolute `BaseOutputPath` must be inside this checkout;
   `dotnet build dream\dream.csproj` once first so the test project can copy the native Bullet files, see
   `docs/build-and-run.md`, Testing):
   `dotnet test dream.Tests\dream.Tests.csproj "/p:BaseOutputPath=<abs checkout>\artifacts\verify-bin\" --filter "FullyQualifiedName~Dream.Tests.Rendering.Ground"`
   (props: `~Dream.Tests.Rendering.Props`). Changing the ground asset count means updating
   `Assert.Equal(100, assets.Assets.Count)` in `GroundScatterLayoutTests.cs`.
8. Look at it in the engine with the `capturing-dream-screenshots` skill: noon and night, and an A/B with
   `DREAM_GROUND_SCATTER=0` (ground) or `DREAM_WORLD_PROPS=0` (props). Optionally measure a frame with
   `python tools/ui-assets/tests/check_look.py <frame.png>`.
9. Commit the generator, the gate edits, the regenerated outputs and the manifest together, and nothing else the run
   touched. `git status` must show only files of the families you changed.

## 5. Gates you will meet

- **check_sources**: allowed imports only; any other sibling module via `importlib.import_module("gen_props")`; no
  file reads outside `READ_ALLOW`; no fonts, network, `exec`/`eval`; no short strings matching
  `reference|fonts|.ttf|http|screenshot`; no source-game names.
- **Shipped bytes**: the committed files must equal a fresh render byte for byte. One red gate = stale output,
  regenerate. Many red at once after a toolchain change = encoder drift, do not mass-regenerate.
- **Determinism**: two builds identical (skipped by `--fast`; run the full gate before committing).
- **Budgets**: triangles, bytes, atlas size per class, and a family byte total.
- **Grim palette and no baked key**: value, saturation, forbidden hues, chrome, quadrant, low-frequency and azimuth
  gates from `check_props.GATES`.
- Every gate has a synthetic negative in `self_test()`. Never loosen a constant to pass. If the art is right and a
  sparse asset trips a statistical gate, run `--measure` and add an `OVERRIDES` entry with the measured value and why.

Rules, numbers and a symptom -> fix table: `references/gates-and-failures.md`.

## 6. Art direction

- Dark, grim, gothic: muted ash-greens, umber, bone, blackened iron, dull flowers; rust only where the family allows.
  Judge by mood and material quality, not by brightness.
- Original procedural art only. Never trace, sample, copy or read reference images, screenshots or another game's
  assets, and never use a source game's names for files, sprites, ids or tokens. References are for the eye only.
- Form first (silhouette and primary shapes), then secondary detail, then fine noise; wear follows the form.

## 7. Quick failure guide

- Byte budget overrun: the atlas is the cost; use the class's atlas size, weld/smooth meshes, fewer sides.
- `azimuth` / `seam_share` red on a tiny asset: measure, then a commented `OVERRIDES` entry with the measured value.
- Manifest differs only on Windows: missing `text eol=lf` pin in `.gitattributes` for that JSON path.
- `check_sources` red on a new generator: a direct `import gen_props` / `import groundkit`; use `importlib`.
- New stem missing from the manifest, or a GLB flagged stale: it was only written by an `--only` run; rerun the full family.
- Asset missing in game: unknown class/layer/material in C#; read `[render-fault] ground-assets` lines in stdout.
