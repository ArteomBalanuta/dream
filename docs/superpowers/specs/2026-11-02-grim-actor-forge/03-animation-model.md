# 03: Skeleton, clips and output

The data model for a posed, animated actor. Everything here is new; `isoforge` renders one static mesh to one
sprite and knows nothing about joints or time.

## Decisions

| # | Topic | Decision |
|---|---|---|
| 1 | Rig kind | **Linear-blend skinning over a joint tree**, two bone influences per vertex. Not free-form deformation, not per-part rigid attachment |
| 2 | Why two influences | Actors are armoured and bony. Two handle an elbow; four would only smooth silhouettes we deliberately keep hard |
| 3 | Rigid parts | A weapon, pauldron or skull weighted to one bone is the same path with the second weight zero, not a special case |
| 4 | Pose space | Joint rotations are **local to the parent**, in degrees, applied x then y then z. No quaternions, no scale, no translation except the root |
| 5 | Root motion | `Pose.root_move` translates the whole figure, for a hop, a lunge or a death slump. In-place clips keep it at zero |
| 6 | Noise space | Material noise is evaluated in **bind-pose (rest) space**, never posed space. Non-negotiable — see [02](02-tool-architecture.md) hook 2 |
| 7 | Directions | **4 facings** for the first wave. `ActorSpec.dirs` makes 8 a data change later |
| 8 | No mirroring | SW is **not** a flip of SE. VERIFIED: `ISO_RIG`'s key is a fixed view-space direction, so a mirrored sprite is lit from the wrong side. This is why the direction count cannot be halved, and it is the dominant cost term |
| 9 | Facing implementation | A final `xform((0, 0, facing_yaw(d, dirs)))` on the whole skinned set — **not** applied to `rest`. One camera, one rig, one shadow direction |
| 10 | Interpolation | Linear on Euler angles with a per-key ease (`linear`, `smooth`, `hold`). Keys are hand-authored ≤ 90° apart, so this is adequate. **Say so in a comment** — "fixing" it with quaternions changes every byte |
| 11 | Canvas | **One canvas size and one pivot per actor**, not per clip, sized to the union of every pose. A clip that overflows fails `canvas_fit` |
| 12 | Output location | `tools/ui-assets/char/` only. Never `dream/ui/` |

## Types

All frozen dataclasses, all plain data, all picklable for the process pool.

```python
@dataclass(frozen=True)
class Joint:
    name: str
    parent: str | None            # parents must appear before children
    offset: tuple                 # bind translation from the parent, world u

@dataclass(frozen=True)
class Skeleton:
    joints: tuple
    def index(self, name) -> int
    def bind_world(self) -> dict      # 4x4 float64 per joint

@dataclass(frozen=True)
class Pose:
    rot: dict = ()                # joint -> (rx, ry, rz) degrees, local to parent
    root_move: tuple = (0.0, 0.0, 0.0)

@dataclass(frozen=True)
class Key:
    t: float                      # 0..1 along the clip
    pose: Pose
    ease: str = "smooth"          # "linear" | "smooth" (smoothstep) | "hold" (step)

@dataclass(frozen=True)
class Clip:
    name: str
    keys: tuple                   # t ascending, t=0 required
    frames: int
    frame_ms: int                 # uniform frame time
    loop: bool = True
    durations: tuple = ()         # optional per-frame ms, else frame_ms repeated

@dataclass(frozen=True)
class ActorSpec:
    name: str                     # ALSO the noise seed. Never includes clip or frame
    size: tuple                   # one canvas for every frame of every clip and direction
    pivot: tuple                  # foot point, one for all frames
    skeleton: Skeleton
    build: callable               # () -> tuple[Part, ...]; Part = (Mesh, IsoMat, seed, bones)
    clips: tuple
    dirs: int = 4
    render: dict = ()             # kwargs for Scene.render (wear, grime, rust, exposure, dome, …)
```

`ActorSpec` is a **new type beside `ObjectSpec`, not an extension of it.** `ObjectSpec` is a flat tuple of
frame callables, and `gen_iso.py` writes `"loop": false` unconditionally with no clip concept, while
`check_iso.manifest_spec_errors` asserts that flat shape. Clips, loops and directions do not fit. `ObjectSpec`
stays untouched and `gen_char.py` flattens `ActorSpec` to frames itself.

## Pose sampling

`sample(clip, i) -> Pose`:

- `t = i / clip.frames` for a loop clip, so frame `frames` would equal frame 0 and is never emitted.
- `t = i / (clip.frames - 1)` for a once clip, so the last frame lands exactly on the final key.
- Find the bracketing keys, blend with the **left** key's ease (`hold` returns the left pose), and interpolate
  rotations and `root_move` componentwise.

Exact arithmetic on the integer frame index. Never accumulate `t += dt`.

## Skinning

Computed once in bind pose, so it is pure data and deterministic.

`bind(mesh, skeleton, bones) -> Skin`, where `bones = ((joint, p0, p1, radius), …)`. Each vertex takes the two
nearest bone segments by point-to-segment distance, with weight `w = smooth(0, 1, (d1 − d0) / blend)` clamped
and normalised, `blend ≈ 0.8 u`. `Skin` holds `(i0, i1, w)` as int32/int32/float32.

`skin(skeleton, pose, parts, skins) -> list[Mesh]` builds world matrices parent→child, applies linear blend
skinning to `v`, sets `rest` to the bind-pose `v`, and **recomputes normals from the posed vertices** —
`_smooth_normals(v_posed, f)` for welded meshes, `None` left as `None` for flat ones.

Do not blend-transform the stored normals: LBS makes them non-orthogonal. Recomputing costs little and keeps
the 35° crease weld meaningful. Skinning does not change connectivity, so the weld topology can be cached from
the bind pose if profiling shows it matters.

## Baseline clip set

| Clip | Frames | Loop | Frame ms | Reads as |
|---|---|---|---|---|
| `idle` | 4 | yes | 220 | Breathing, a slight weapon sway. Deliberately almost still |
| `walk` | 8 | yes | 90 | Four-beat cycle: contact, down, pass, up, mirrored for the second half |
| `attack` | 6 | no | 70, 60, 50, 90, 120, 160 | Wind-up, strike, follow-through. Fastest at the strike |
| `die` | 6 | no | 80, 80, 80, 120, 160, 400 | Collapse, settling into a pose the engine holds |

24 frames × 4 facings = **96 renders per actor**. A `hurt` clip is a likely fifth; it is left out of the first
wave so the baseline stays small.

## Cost

MEASURED on this machine, 2026-11-02, one core:

| Measurement | Result |
|---|---|
| `slate_rocks`, 240x192 final (0.74 Mpx hi-res) | **5.1 s** |
| `rust_barrel`, 128x160 final (0.33 Mpx hi-res), 21,932 tris | **4.61 s = 14.1 µs per hi-res px** |
| Same barrel with the AO dome cut from 20 to 8 directions | **2.86 s = 8.7 µs/px, a 38 % saving** |

The README's 4.4 µs/px is the **forge chain alone**; `isoforge` adds the triangle raster, the key occluder and
the dome occluders on top, which is the 3.2x difference. Plan with the measured figures, not the README's.

Actor sizing: 16 u = 1 m, so a 1.8 m humanoid is ~29 u tall; at pitch 50 its vertical screen extent is ~0.77x
that plus footprint. A **160x192 canvas** (0.49 Mpx hi-res) holds a figure plus a weapon swing, costing about
**6.9 s/frame at dome 20** or **~4.5 s/frame at dome 10**.

| Set | Frames | Serial at 4.5 s | 4 workers |
|---|---|---|---|
| 1 actor, 4 dirs | 96 | 7.2 min | ~1.8 min |
| 1 actor, 8 dirs | 192 | 14.4 min | ~3.6 min |
| 3 actors, 4 dirs | 288 | 21.6 min | ~5.4 min |
| 3 actors, 8 dirs | 576 | 43 min | ~11 min |

What makes it tractable, in order of leverage:

1. **`dirs=4` for the first wave.** With no mirroring possible, direction count is the one linear term nobody
   can cheat.
2. **`dome=10`** — measured −38 %.
3. **Small canvases.** Cost is strictly linear in hi-res area. Never size the canvas to the widest conceivable
   weapon arc.
4. **The existing process pool**, one job per `(actor, clip, dir, frame)` — perfectly parallel.
5. **Cache the build, not the render.** `ActorSpec.build()` and `bind()` run once per worker process and are
   reused for every frame. Memory peaks near 320 bytes per hi-res pixel, ~160 MB per worker at this canvas, so
   4 workers are safe and banding is not needed.
6. **`--only` and `--clip`**, so an art iteration costs one clip, not the set.

Two things that do **not** work, written down so nobody tries them:

- **Caching the G-buffer across frames of a clip.** The geometry moves every frame, so the raster, the key
  shadow map, every dome occluder, `_depth_ao` and every form map must be rebuilt. Only the material LUTs and
  the bound skin are shareable. An earlier draft of this spec suggested this; it is withdrawn.
- **Lowering `SS` for actors.** VERIFIED: `SS` and `PPU` are module globals read at import by both `forge.py`
  and `isoforge.py`, and are baked into blur sigmas (`0.30 * PPU`), occluder resolutions, `_depth_ao` steps and
  `forge.finish`'s bloom radii. Threading a scale through both modules is a far larger change than this wave.
  **Keep `SS=4` and shrink canvases instead.**

## Output files

Per-frame PNGs, matching the shape of the existing iso set rather than inventing a packed atlas:

```
char/sprites/<actor>_<clip>_d<dir>_<frame:02d>.png
char/shadows/<actor>_<clip>_d<dir>_<frame:02d>.png
char/char_manifest.json
char/char_sheet.png
```

Straight-alpha RGBA at the actor's single `size` and `pivot`, RGB dilated under A=0 via `uiart.bleed_rgb`,
`yDown` true so a loader must not flip V.

`char_sheet.png` is a **review sheet, not a runtime atlas**: a strict grid, one row per `(clip, dir)`, one
column per frame, with every frame of an actor sharing one crop so the motion stays in register. It reuses
`gen_iso.py`'s `BG`, `PAD`, `GAP` and shadow-then-sprite stamping so `check_iso.sheet_errors` passes unchanged;
the layout code is new, because `gen_iso`'s justified-row layout cannot hold a 96-cell grid.

## Manifest

One trailing newline, LF, `indent=1`, matching `gen_iso.py`.

```json
{"version": 1, "pitchDeg": 50, "yawDeg": 45, "pxPerUnit": 4, "yDown": true, "straightAlpha": true,
 "sheet": "char_sheet.png",
 "actors": [{"name": "wolf", "size": [160, 192], "pivot": [80, 170], "dirs": 4,
   "clips": [{"name": "walk", "loop": true, "frames": 8,
              "durationMs": [90, 90, 90, 90, 90, 90, 90, 90],
              "directions": [{"dir": 0, "start": 0, "count": 8},
                             {"dir": 1, "start": 8, "count": 8}]}],
   "frames": [{"image": "sprites/wolf_walk_d0_00.png",
               "shadow": "shadows/wolf_walk_d0_00.png", "durationMs": 90}]}]}
```

`start` and `count` index the actor's flat `frames` array. Clip order is fixed by the registry, direction
ascending, frame ascending. `durationMs` appears both per frame and summarised per clip, as `iso_manifest`
does, so a consumer can read either.

## Engine side

**Out of scope, and nothing on the C# side changes in this wave.** VERIFIED:

- The UI loader is a closed set: `UiSpriteTable.PageFiles` lists five JSON/PNG pairs and `PagePngPath` switches
  over the `UiPage` enum.
- Its schema is a single-page atlas — a top-level `size` matching the PNG's dimensions and a `sprites` object
  of `x/y/w/h/pivot/slice/tile/meta.footprint`. **`UiSprite` has no field for a clip, a direction, a frame
  index or a duration**, so the actor manifest cannot be expressed in it.
- `iso_manifest.json` has **no C# consumer today**; a grep for `iso_manifest` across `dream/**/*.cs` finds only
  camera-mode and `IsometricView` code. `char/` ships as review art exactly as `iso/` does.

When the game does consume actors it needs a new `ActorAtlasTable` beside `UiSpriteTable` — clip ranges, loop
flags, per-frame durations, per-direction indices and a separate shadow layer — plus either shelf-packing
through `uiart.Atlas` or per-frame textures. That is a separate wave with its own plan, and it depends on the
unmerged `integrate/isometric` branch. The manifest above is designed so that work is a loader, not a redesign.
