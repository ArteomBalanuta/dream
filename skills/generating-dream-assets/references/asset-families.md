# Asset families: generator, output, gate, consumer

All paths are repo-relative. All generators live in `tools/ui-assets/`, all gates in `tools/ui-assets/tests/`.
"In build_all" means `tools/ui-assets/build_all.py` runs it (see its `UI_GENERATORS` and `ASSET_GENERATORS`).

## Table

| Kind | Generator (kit) | Writes | Gate | Runtime consumer | build_all |
|---|---|---|---|---|---|
| Ground flora (trees, saplings, bushes, grass, flowers, mushrooms) | `gen_ground_flora.py` (`groundkit`, `gen_props`, `gen_iso_nature`) | `dream/animations/ground/flora.json`, `flora/<stem>.glb` | `check_ground_flora.py` | `Dream.Rendering.Ground` (`GroundAssetSet`, `GroundScatterLayout`, `GroundScatterRenderer`) | yes |
| Ground stone (rocks, scree, pebbles, stumps, logs, roots, twigs, leaves, bones, dropped items) | `gen_ground_stone.py` (`groundkit`, `gen_props`) | `.../ground/stone.json`, `stone/<stem>.glb` | `check_ground_stone.py` | same | yes |
| Floor decals | `gen_ground_decals.py` (`groundkit`, own painters) | `.../ground/decals.json`, `decals/<stem>.glb` | `check_ground_decals.py` | same | yes |
| World props (hamlet, scenery) | `gen_props.py` (`charforge`, `gltfwrite`, `isoforge`) | `dream/animations/props/<name>.glb` | `check_props.py` | `dream/Rendering/Props/PropCatalog.cs`, `PropLayout.cs`, `PropRenderer.cs` | yes |
| Player | `gen_char_player.py` (`charforge`, `gltfwrite`) | `char/player_grim.glb` standalone; build_all writes `dream/animations/players/player-grim.glb` | `check_char.py` | `PlayerModelAssetDefinition` | yes |
| Villagers, hound | `gen_char_npc.py`, `gen_char_beast.py` | `dream/animations/npcs/villager-*.glb`, `dream/animations/enemies/hound.glb` | `check_actors.py` | `NpcVisualCatalog` in `dream/Rendering/Actors/NpcModelRenderer.cs` (`npc.vendor/smith/warden`) | yes |
| Risen skeleton (the first forged BIPED enemy) | `gen_char_skeleton.py` (`charforge`, `gltfwrite`, `isoforge`, `gen_char_player`) | `dream/animations/enemies/skeleton.glb` | `check_actors.py` | `SkeletonAssetDefinition`, `EnemyVisualCatalog` key `enemy.skeleton` | yes |
| Terrain tiles (one per `GeometryMaterialId` + grass top/side/bottom) | `gen_terrain.py` (`forge`) | `tools/ui-assets/terrain/<name>.png`, `terrain_manifest.json`, `terrain_sheet.png` | `check_terrain.py` | `StaticTerrainMaterialCatalog` reads `dream/animations/library/forge/<name>/<name>.albedo.png` (manual copy) | no |
| Item icons | `gen_items.py` (`forge`, `uiart`) | `dream/ui/items/` (`items.png`, `items.json`, `items_mask.png`, tier variants, ramps) | `check_items.py`, `check_loader.py` | `UiSpriteTable` page `items/items.json`; sprite name `item.<item key>` (`UiSpriteNames.Item`) | yes |
| HUD bar, orbs, plates | `gen_hud.py` (`forge`, `uiart`) | `dream/ui/hud/` | `check_hud.py`, `check_art.py --art`, `check_manifest.py` | `UiSpriteTable` | yes |
| Stone windows | `gen_window.py` | `dream/ui/window/` | `check_window.py`, `check_manifest.py` | `UiSpriteTable` | yes |
| Front-end menu | `gen_menu.py` (reuses `gen_window`) | `dream/ui/menu/` | `check_menu.py` | `UiSpriteTable`; `DREAM_MENU_CAPTURE=<dir>` walks the title screens | yes |
| Damage-number digits | `gen_fx.py` | `dream/ui/fx/` (`dmg_digits*.png/json`) | `check_fx.py` | `UiSpriteTable` | yes |
| Particles | `gen_particles.py` | `dream/ui/particles/particles.png`, `particles.json` (fixed 8x4 grid of 64 px cells, max 32 KB) | `check_particles.py` | `Dream.Rendering.Effects` (`FxInstance.RelativePath`) | yes |
| Iso sprites (legacy, sprite renderer) | `gen_iso.py` over `gen_iso_props/nature/relics.py` (`isoforge`) | `tools/ui-assets/iso/` only | `check_iso.py` | none in the perspective world; `gen_props` reuses their meshes | no |

## Per-family commands

Run from the repo root (see SKILL.md for the interpreter).

```text
python tools/ui-assets/gen_ground_flora.py  [out_root] [--only a,b] [--preview]
python tools/ui-assets/gen_ground_stone.py  [out_root] [--only a,b] [--preview]
python tools/ui-assets/gen_ground_decals.py [out_root] [--only a,b] [--preview]
python tools/ui-assets/gen_props.py         [out_dir]  [--only a,b]          # about 45 s
python tools/ui-assets/gen_terrain.py       [--only a,b] [out_root]          # --only must come first
python tools/ui-assets/gen_iso.py           [--only a,b DIR]
python tools/ui-assets/build_all.py         [ui_out_root]                    # everything in build_all

python tools/ui-assets/tests/check_ground_<family>.py [--fast] [--measure] [stem ...]
python tools/ui-assets/tests/check_props.py   [--fast] [--measure]           # about 90 s
python tools/ui-assets/tests/check_terrain.py [--fast] [--measure]
python tools/ui-assets/tests/check_iso.py     [--fast] [--partial] [--measure]
python tools/ui-assets/tests/check_art.py     [--fast] [--art --only bar,hp,mp,dragon]
python tools/ui-assets/tests/check_sources.py
```

`--preview` writes a review sheet to `tools/ui-assets/previews/ground_<family>.png`. For flora, `--preview` writes only
the sheet and does not run `main`. Sheets are review aids, not shipped assets.

Self-tests of the kits (run after editing a kit): `python tools/ui-assets/groundkit.py`, `gltfwrite.py`, `charforge.py`,
`enginelook.py`, `forge.py` (writes `previews/forge_sample.png`, gated by `check_art.py`).

## What a new asset touches, per family

- **Ground asset**: see `ground-assets.md`.
- **Prop**: a builder and a `PropSpec` entry in `gen_props.PROPS`; in `PropCatalog.cs` the `PropKind` enum,
  `KindCount`, `BoundsByKind`, `FileStems` and `Footprints` (check_props parses `FileStems` and `Footprints` from the
  C# with a regex, so keep the `new(<x>f, <z>f, <spread>)` shape); placement in `PropLayout.cs`.
- **Item icon**: an `icon_*` painter and an entry in `gen_items.ICONS` or `CATALOGUE_ICONS` (`(cols, rows, fn)`), keyed
  by the content key in `dream/config/content/items.json`; `(cols, rows)` must equal that item's `footprint`. Add the
  name to `EXPECTED` or `NEW_KINDS` in `check_items.py` under its footprint. `gen_items.py` may only import `numpy`,
  `PIL`, `uiart`, `forge` and `__future__ json math pathlib sys` (`check_items.THIRD_PARTY_OK | STDLIB_OK`).
- **HUD, window or menu sprite**: the sprite must also be listed in the Manifest tables of
  `docs/superpowers/specs/2026-11-01-classic-mmo-ui/07-asset-plan.md`; `check_manifest.py` parses those tables and fails
  on missing or extra sprites. Atlases are straight-alpha RGBA at 4 px per u, `yDown` true, rects exclude extrusion.
- **Terrain tile**: a `Tile` in `gen_terrain.TILES`; copy `tools/ui-assets/terrain/<name>.png` to
  `dream/animations/library/forge/<name>/<name>.albedo.png`. Retuning a tile shifts `gen_ground_decals.TILE_LUMA`,
  which `check_ground_decals.py` compares with the tiles on disk: update it and re-run the decal gate.
- **Actor**: rigid part hierarchy with the engine's node names, or a skin under the 32-joint cap; read the README
  section "charforge.py" and the specs it names before starting.

## Toolkit roles

- `uiart.py`: atlas packing, PNG I/O, `rng`, tokens, `shipped_errors`, `determinism_errors`. The only file allowed to
  read files freely.
- `forge.py`: the 2D lit-relief chain for UI art and terrain (`Relief -> bake -> age -> light -> finish -> downsample`).
- `isoforge.py`: `Mesh`, builders (`tube lathe grid box convex torus card`), `IsoMat`, `waves3`, the iso sprite renderer.
- `charforge.py`: rigs, clips, `unwrap_split`, `bake_albedo` (albedo only, no directional light), `GRIM_MATS`.
- `gltfwrite.py`: GLB writer (rigid or skinned), materials with `alpha_mode`, `alpha_cutoff`, `emissive_factor`,
  `double_sided`.
- `gen_props.py`: also the shared prop plumbing: `Part`, `M(token, ...)`, `WORLD_UNITS_PER_U`, `ALBEDO_LIFT`,
  `contact_texture`, `disc_arrays`, `DISC_LIFT`, `_weld`.
- `groundkit.py`: `GroundSpec`, budgets, `build_ground_asset`, `glb_bytes`, `manifest_entry`, `write_family`,
  `validate_manifest`, `glb_errors`, `family_errors`.
- `enginelook.py`: the engine's actor transfer function for gates (night constants, no tonemap). Not art.
