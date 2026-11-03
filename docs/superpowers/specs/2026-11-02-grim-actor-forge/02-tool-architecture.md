# 02: Tool architecture

What the forge is, what it reuses, and what is new. The guiding rule: **`forge.py` and `isoforge.py` already
solve lighting, material and downsampling correctly.** This tool adds skeletons, time and a darker rig. It must
not grow a second renderer, and it must not fork `Scene`.

## What exists and is reused unchanged

VERIFIED against source, 2026-11-02.

| Reused | From | Role in the forge |
|---|---|---|
| `Relief`, `bake`, `age`, `light`, `finish`, `downsample`, `MATERIALS` | `forge.py` | The whole 2D material and lighting chain. Untouched |
| `forge.Rig`, `forge.Light`, `forge.Panel` | `forge.py` | `GRIM_RIG` is built from these; no new types |
| `Mesh` (`v f n uv tint`), `xform`, `merge`, `displace`, `_smooth_normals` (35° crease weld), `_face_normals` | `isoforge.py` | Limbs are meshes; `merge` already reconciles flat and smooth meshes |
| Builders `tube`, `lathe`, `grid`, `box`, `convex`, `torus`, `card` | `isoforge.py` | `tube(path, radii, …)` with parallel-transport frames *is* a limb; `card` is cloth and hair; `convex` is a skull. **No new primitive is needed for a humanoid** |
| `waves3`, `IsoMat`, `Waves3`, `sat_cap`, `RUST_CAP`, `_grade`, `CRUSH` | `isoforge.py` | The grim look is reached by moving these numbers, not by new code |
| The whole render chain: raster → `_Occluder` key shadow + dome AO → `_depth_ao` → `forge.Maps` → `forge.age` → `_rust`/`_moss` → `forge.light` → `forge.finish` → `_grade` → `fx` → `forge.downsample` → `U.bleed_rgb` | `isoforge.py` | Nothing about it is static-only. It only lacks a rig/crush/rest-space hook |
| `to_view`, `view_to_world`, `world_to_view`, `project`, `ground_uv` | `isoforge.py` | Camera. Unchanged: pitch 50°, yaw 45° |
| `uiart.rng` (SEED ^ crc32 of name), `save_png`, `bleed_rgb`, `determinism_errors`, `FORBIDDEN_NAMES`, `SS=4`, `PPU=16` | `uiart.py` | Determinism and naming hygiene come free |
| Registry + `ProcessPoolExecutor` job shape, where only plain data crosses the process boundary and `_job` re-resolves the registry | `gen_iso.py` | Copied, not imported — the job tuple differs. `DREAM_ART_WORKERS` is already the convention |
| Multi-frame precedent: `rune_potion_drop`, 4 frames with `duration_ms`, one shared canvas and pivot | `gen_iso_relics.py` | Proves frames-as-callables plus manifest durations already work end to end |
| Gate metrics `hsv`, `luma`, `neighbours`, `erode`, `band_energy`, `local_contrast`, `components`, `main_body`, `edge_errors`, `sheet_errors`, `png`, `digests` | `tests/check_iso.py` | `check_char.py` **imports** these. Values are not shared; see [04](04-gates-and-verification.md) |
| Review-sheet conventions: BG `(200,200,200)`, `PAD` 20, `GAP` 28, shadow-then-sprite stamping | `gen_iso.py` | Reuse the constants so `sheet_errors` passes unchanged. The layout code itself is new |

## What is new

| New | File | Owns |
|---|---|---|
| `Joint`, `Skeleton`, `Pose`, `Key`, `Clip`, `ActorSpec`; `sample`, `bind`, `skin`, `facing_yaw`, `render_frame`; `GRIM_RIG`, `CHAR_CRUSH`, `CHAR_RUST_CAP` | `charforge.py` | The animation model from [03](03-animation-model.md) and the grim constants from [01](01-art-direction.md) |
| Three keyword hooks + `Mesh.rest` | `isoforge.py` (edit) | The only invasive change. Backwards-compatible and provably byte-neutral |
| `ALLOWED_MODULES += {"charforge"}` | `tests/check_sources.py` (edit) | One line. See the trap below |
| `char/` rules | `.gitattributes` (edit) | LF JSON, binary PNG, or the byte compare fails on Windows |
| `ACTORS: dict[str, ActorSpec]` registries | `gen_char_player.py`, `gen_char_wolf.py` | Actor content, mirroring the `gen_iso_*.py` `OBJECTS` pattern |
| Build driver, PNG writing, manifest, review sheet | `gen_char.py` | CLI, worker pool, `char/` output |
| Actor gate table, animation gates, self-tests | `tests/check_char.py` | [04](04-gates-and-verification.md) |

### The trap

`tests/check_sources.py` whitelists imports for every `tools/ui-assets/*.py`. **`import charforge` fails the
hygiene gate until `"charforge"` is added to `ALLOWED_MODULES`.** That one-line edit belongs in the shared
contract commit so no workstream owns the file.

`charforge.py` must itself stay inside that whitelist: `numpy`, `math`, `dataclasses`, `forge`, `isoforge`,
`uiart` only. `requirements.txt` stays at `numpy==2.5.4` and `pillow==13.0.0`.

## Why the grim constants live in `charforge.py`

An earlier draft of this spec put them in a separate `grimrig.py`. **Withdrawn** — it is a file with no
behaviour. They go in `charforge.py`, and the reason they cannot go in `isoforge.py` is concrete:
`ISO_RIG` and `CRUSH` *are* the committed look of the prop set, and `check_iso.py` hashes the committed iso
PNGs against a fresh render. Editing either constant rewrites seven sprites and fails the byte comparison.

The hooks below are what let `charforge` pass its own values without touching the prop defaults.

## The changes to `isoforge.py`

Everything is additive, with the current constants as defaults, so the committed prop PNGs stay byte-identical.

### Hook 1 — rig, crush and dome as arguments

`Scene.render` hard-codes `ISO_RIG`, `_grade` hard-codes `CRUSH`, and the AO dome is a literal 20. Duplicating
the ~150-line `render` inside `charforge` would rot, so parameterise instead:

```python
def _grade(rgb, cap, crush: float = CRUSH): ...

class Scene:
    def render(self, wear=0.3, grime=0.5, rust=0.2, dust=0.1, moss=0.0, exposure=1.0,
               hard_alpha=False, detail=1.0,
               rig: forge.Rig = ISO_RIG, crush: float = CRUSH, dome: int = 20): ...
```

### Hook 2 — `Mesh.rest`, and why it is the most important line in the spec

**All of `isoforge`'s material character is keyed to world position**: `mott` and `pat`, `_rust`, `_moss`, and
`IsoMat.detail` bumps all sample noise at the pixel's world position. Under animation the vertices move, so
**rust, moss, mottling and pitting would swim across the surface frame by frame.**

Fix: carry a second per-vertex position — the bind-pose or *rest* position — interpolate it per pixel as `Pr`,
and feed `Pr` to every material noise call, while `Pw` keeps feeding lighting, AO, cast shadow and any
height-based mask that genuinely wants the posed height.

```python
Mesh.rest: np.ndarray | None = None   # None means "equals v"
```

`None` meaning "equals `v`" is what makes props byte-identical. `rest` threads through `__post_init__`,
`xform`, `merge`, `displace`, `_unwelded` and `_merged`.

Note the interaction with facing: the final yaw is applied to the posed mesh and **not** to `rest`, which is
exactly right — an actor facing four ways must wear the same patina.

### Hook 3 — `Scene.name` is the noise seed

For actors it must be the **actor** name alone, `"wolf"`, never `"wolf_walk_d2_03"`, or the patina changes
per frame. `patina_stability` in [04](04-gates-and-verification.md) enforces it.

### Acceptance for these edits

`python tools/ui-assets/tests/check_iso.py` — the **full** run including determinism — must pass unchanged.
That is the proof the hooks are byte-neutral, and it runs before the edit is committed. If it fails, the work
stops and reports rather than loosening the gate.

## Import graph

```
forge  →  isoforge  →  charforge  →  gen_char_*  →  gen_char
                            ↑
                  tests/check_char.py  →  tests/check_iso.py (metrics only, never edited)
```

Acyclic, and `check_char.py` depending on `check_iso.py` is a one-way import of pure functions.

## Output layout

```
tools/ui-assets/
  charforge.py                      new: skeleton, skin, clips, GRIM_RIG, CHAR_CRUSH, render_frame
  gen_char.py                       new: driver, PNGs, manifest, review sheet
  gen_char_player.py                new: ACTORS registry, the player
  gen_char_wolf.py                  new: ACTORS registry, the wolf (quadruped)
  tests/check_char.py               new: gates
  char/                             new: output, never dream/ui
    sprites/<actor>_<clip>_d<dir>_<frame:02d>.png
    shadows/<same>.png
    char_manifest.json
    char_sheet.png
  previews/charforge_sample.png     new: the acceptance sample for the chain
```

`gen_char.py` is **not** added to `build_all.py`, exactly as `gen_iso.py` is not: these are slow, and nothing in
`dream/` consumes their output yet.

Keeping output under `tools/ui-assets/char/` rather than `dream/ui/` also keeps it outside
`tests/check_loader.py`'s 16 MB `dream/ui` budget — a four-direction actor set is tens of megabytes of PNG.

## CLI

Mirrors `gen_iso.py` so there is one thing to learn.

```bash
.venv/Scripts/python tools/ui-assets/gen_char.py
```

```bash
.venv/Scripts/python tools/ui-assets/gen_char.py --only wolf --clip walk
```

| Flag | Effect |
|---|---|
| *(none)* | Every actor, clip and direction into `tools/ui-assets/char/`, plus manifest and review sheet |
| `--only <name>[,<name>]` | Those actors only. Never writes the manifest or the sheet, matching `gen_iso.py --only` |
| `--clip <name>[,<name>]` | Those clips only. Implies the manifest is not written |
| `--dir <n>` | One facing, for fast iteration |
| `--pose <clip>:<t>` | Render a single frame at normalised time *t*. The authoring loop |
| `<out_root>` | Positional, as `gen_iso.py` takes it. Output goes to `<out_root>/char` |
| `DREAM_ART_WORKERS` | Process count, default `min(4, cpu)` |

`--pose` is what makes the tool usable: a single frame is a few seconds, so an author can iterate on one joint
angle without rendering a 96-frame set.

## Environment

VERIFIED 2026-11-02: there was no `.venv` in the repo. It is created with `uv`, which is installed.

```bash
uv venv .venv --python 3.13
```

```bash
uv pip install --python .venv/Scripts/python.exe -r tools/ui-assets/requirements.txt
```

That installs exactly `numpy==2.5.4` and `pillow==13.0.0`. No Blender, no scipy, no GPU, no new dependency.

VERIFIED: `uv` writes `.venv/.gitignore` containing `*`, so the venv ignores itself and the root `.gitignore`
needs no entry. `.gitignore` already has `__pycache__/` and `.iso_tmp/`; the determinism run **reuses
`.iso_tmp`** rather than adding a second temp folder, so nothing new is needed there either.

## Determinism contract

Restated here because it constrains every design choice above, and measured in
[04](04-gates-and-verification.md):

- All randomness through `uiart.rng("<stable string>")`, built from fixed strings and integer indices only.
- All material noise in rest space.
- `Scene.name` is the actor name alone.
- Worker count must not affect output; jobs sort by `(actor, clip, dir, frame)`, never by hash.
- Output is byte-identical across runs.
