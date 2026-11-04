# Gates and common failures

## check_sources.py (runs over every `tools/ui-assets/*.py`, not the tests)

- **Imports**: top-level module must be in `ALLOWED_MODULES`: `__future__ json math sys pathlib numpy PIL uiart typing
  dataclasses re hashlib tempfile importlib zlib forge gemtrace concurrent os isoforge gen_window charforge gltfwrite
  gen_char_player struct io enginelook gen_char_npc gen_char_beast`. Any other sibling (`gen_props`, `groundkit`,
  `gen_iso_nature`, ...) is imported by name: `GP = importlib.import_module("gen_props")`, with `sys.path` holding the
  tools directory. A plain `import gen_props` fails the gate. Widening `ALLOWED_MODULES` needs the new name to be an
  original generator in this directory, with a comment saying so.
- **No file reads**: every `.open(` / `.read_bytes(` / `.read_text(` call in a generator must match a `READ_ALLOW`
  entry keyed on `(file, exact call source)` with a reason; a stale entry also fails. `uiart.py` is the only exempt
  file. Generators build from code and fixed seeds; they never read images, fonts or the network.
- **Banned**: `ImageFont`, `truetype`, `load_default`, `urllib`, `requests`, `exec`, `eval`, `__import__`; any string
  constant under 200 chars matching `reference|\.webp|fonts|\.ttf|\.otf|https?:|screenshot` (so a short string or
  comment-like literal saying "reference" fails; long docstrings are not scanned).
- **No source-game names** in sprite names, atlas file names or tokens (`BAD_NAME` regex). Name things by what they
  are (`tree_gallows_oak`), never after another game's places, items or brands.
- `check_items.py` adds its own stricter import list for `gen_items.py` (`numpy PIL uiart forge` + basic stdlib).

## Shipped bytes and determinism

- A gate reads; a generator writes. No check writes into the repository (`artifacts/tmp`, `.iso_tmp/` are gitignored
  scratch).
- `uiart.shipped_errors(main, subdir, committed)` renders the generator into a temp dir and byte-compares every file
  with the committed one (JSON line endings normalised). Ground uses `groundkit.family_errors` (same, plus "committed
  but the generator no longer makes it"). Props, actors, iso and char gates have their own equivalent.
- Determinism: two builds must give identical bytes. Builders are pure: no clock, environment, filesystem, or unseeded
  RNG; dict/set iteration order must not leak into geometry.
- One gate red = stale art: re-run that generator and commit its output. Many gates red at once right after a Pillow or
  numpy change = the encoder moved: do not regenerate everything; compare decoded pixels instead
  (`uiart.shipped_errors` docstring). Pinned toolchain: `numpy==2.5.4`, `pillow==13.0.0`.
- Every gate has a synthetic negative in `self_test()` that must fail it. When you add a gate, add its negative; when a
  negative stops failing, the gate is blind and that is the bug.

## Look gates (constants in `tests/check_props.py` `GATES`, reused by the ground checks)

| Gate | Limit | Meaning |
|---|---|---|
| `rect_quad_max` | 0.45 | quadrant luma spread per atlas part rectangle: a baked key would raise it |
| `rect_lowfreq_max` | 0.80 | low-frequency share of a rectangle's luma: a baked gradient |
| `azimuth_max` | 0.23 | correlation of luma with side-facing direction, over 16 azimuths |
| `sat_p95_max` | 0.35 | HSV S p95 on texels with V >= .10 |
| `forbidden_hue_max` | 0.001 | magenta/pink or green-cyan at S > .12 |
| `chrome_max` | 0.002 | share of texels over V .85 |
| `v_p99_max` / `v_p99_min` | 0.55 / 0.10 | nothing bright, nothing a black hole |
| `mean_v_max` | 0.28 | overall dark |
| `seam_share_max` | 0.015 | triangles straddling a cylinder seam after `unwrap_split` |

Decals instead gate covered-texel luma over the luma of the terrain tiles they may lie on, per class band
(`check_ground_decals.BANDS`, e.g. scorch 0.25-0.50, dirt 0.75-1.15), with moss greener and puddle colder than neutral.
Flora greens sit on the yellow side (hue under about 85 degrees) because green-cyan is forbidden.

Whole-frame look (in engine): `python tools/ui-assets/tests/check_look.py <frame.png> [--actor-from-log <out.log>]`
measures a captured frame: near-black share >= 0.45 and bright share >= 0.02 together, mean V <= 0.18, mean S <= 0.38.

**Never loosen a constant to pass.** Fix the art. If the art is right and a small or sparse asset trips a statistical
gate by chance, run the check with `--measure`, then add an `OVERRIDES` entry with the measured value rounded up only
as far as needed and a comment with the reason, e.g. `"stump_axe": {"azimuth_max": 0.27},  # measured .247`.

## Common failures

| Symptom | Cause | Fix |
|---|---|---|
| `bytes ... over budget` on a GLB | the atlas PNG dominates the size | use the class's atlas size (`budget_for`), `weld=True` / smooth normals (a flat mesh unwelds to 3 vertices per triangle), fewer sides; do not raise the budget |
| triangle budget exceeded | too many sides/rows/subdivisions | lower `sides`, `rows`, `subdiv`; cover assets are tiny on screen |
| `azimuth` or `seam_share` red on a small asset | few side-facing texels correlate by chance, or big flat triangles cross a seam | `--measure`, then an `OVERRIDES` entry with the measured value and reason; never a guess |
| `quad` / `lowfreq` red | a directional key or gradient got baked (tint ramp along an axis, light in the material) | bake albedo only; keep tints as patchy `waves3` mottle, not a ramp |
| shipped-bytes red after editing a generator | committed output is stale | regenerate the family (full run) and commit GLBs and manifest together |
| manifest bytes differ only on Windows | CRLF checkout | `.gitattributes` must pin `text eol=lf` for the manifest path (ground, iso, terrain are pinned); GLB/PNG are `binary`. A new JSON output path needs a new pin |
| new stem missing from the manifest / GLB reported stale | written by an `--only` run, which never touches the manifest | rerun the full family (no `--only`) into the default root |
| `stale: <family>/x.glb is not in the manifest` | removed or renamed a stem, the old GLB is still on disk | delete the old GLB from the family folder and commit the removal |
| `check_sources` red: `import gen_props` | direct sibling import | `importlib.import_module("gen_props")` |
| `check_sources` red: `reads ...` | a generator reads a file | build from code; if it re-reads its own output, add a `READ_ALLOW` entry with the reason |
| `check_sources` red: `string 'reference...'` | a short literal hit `BAD_STR` | reword the literal |
| engine shows none of a new class | class missing in C# `GroundAssetSet.Classes` / `GroundAssetClass` | add both; check `[render-fault] ground-assets` lines in stdout |
| C# test `Assert.Equal(100, ...)` fails | asset count changed | update the count in `GroundScatterLayoutTests` |
| decal gate red after a terrain change | `gen_ground_decals.TILE_LUMA` no longer matches the tiles in `dream/animations/library/forge` | update `TILE_LUMA` to the new measured values |
| `check_props` footprint red | `PropCatalog.cs` `Footprints` no longer matches the mesh | update the C# footprint (it is parsed by regex) or the mesh |
| a full `build_all.py` dirtied many unrelated files | encoder or toolchain drift, or another generator was stale | do not commit the unrelated files; regenerate only the families you changed |
