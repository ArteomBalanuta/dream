# 04: Numeric gates and verification

`tests/check_char.py` is to actors what `tests/check_iso.py` is to props: constants measured on **our own
generated art**, never on an outside image.

A gate is a claim about the art direction in [01](01-art-direction.md). If a gate and the art disagree, one of
them is wrong — decide which, in writing, before touching the number.

## Why a new file and not new constants in `check_iso.py`

Two reasons, both hard:

1. **The actor targets invert three prop gates.** `check_iso.py` demands `p99_v >= 0.35`, `median_s >= 0.10`
   and `sat_p95 <= 0.35`. Actors want a value **ceiling**, a saturation ceiling two thirds that, and a
   `median_s` target rather than a floor. Parameterising one table to express both would make neither legible.
2. **`check_iso.py` is also the byte-compare for the committed `iso/` tree.** It hashes committed sprites
   against a fresh render, so it must not be edited in this wave at all. If a metric genuinely needs changing,
   the implementer stops and reports it, per `AGENTS.md`.

VERIFIED 2026-11-02: `check_iso.py` guards its run behind `if __name__ == "__main__"`, so importing it executes
no gates, and these helpers are directly reusable — `hsv`, `luma`, `neighbours`, `erode`, `band_energy`,
`local_contrast`, `components`, `main_body`, `edge_errors`, `sheet_errors`, `png`, `digests`.
**`measure(name, rgba)` is not reusable as-is**: it resolves gate config through `cfg(name)`, which reads
`STATIC`/`OVERRIDES`. `check_char.py` brings its own `measure` built on the imported helpers. No shared helper
module is needed.

`check_sources.py` scans the gate files to keep them free of file reads; `check_char.py` must be added to that
scan, and `"charforge"` must be added to its `ALLOWED_MODULES` whitelist or `import charforge` fails hygiene.

## Measured baseline: what the props actually look like

MEASURED 2026-11-02, `check_iso.py --fast`, **7 objects, 0 errors** — the prop set is clean before any change,
which is the precondition for touching `isoforge.py` at all.

| Object | sat p95 | sat p99 | p5 V | p99 V | peak V | contrast | form | warm | cold (band) | med S | chrome |
|---|---|---|---|---|---|---|---|---|---|---|---|
| `rust_barrel` | 0.424 | 0.486 | 0.004 | 0.624 | 0.686 | 0.068 | 0.37 | 22.6 | −0.7 (0.58) | 0.247 | 0.0000 |
| `dead_oak` | 0.318 | 0.341 | 0.008 | 0.616 | 0.671 | 0.058 | 0.45 | 27.9 | −4.5 (0.55) | 0.294 | 0.0000 |
| `bramble` | 0.624 | 0.671 | 0.000 | 0.576 | 0.667 | 0.056 | 0.45 | 33.4 | 15.0 (0.47) | 0.510 | 0.0000 |
| `slate_rocks` | 0.310 | 0.325 | 0.004 | 0.404 | 0.486 | 0.037 | 0.73 | 4.6 | −4.9 (0.65) | 0.141 | 0.0000 |
| `tombstone` | 0.337 | 0.357 | 0.012 | 0.576 | 0.645 | 0.040 | 0.56 | 4.2 | −8.6 (0.73) | 0.275 | 0.0000 |
| `bone_pile` | 0.325 | 0.443 | 0.000 | 0.659 | 0.688 | 0.088 | 0.55 | 24.7 | −1.2 (0.32) | 0.212 | 0.0000 |
| `rune_potion_drop` | 0.973 | 1.000 | 0.004 | 0.958 | 0.998 | 0.100 | 0.81 | −101.8 | −27.4 (0.29) | 0.867 | 0.0084 |

Read against [01](01-art-direction.md), this is the whole argument in one place. `slate_rocks` is the nearest
thing we already have to the grim target — sat p95 0.310, med S 0.141, warm 4.6, p99 V 0.404 — and it is still
at or above every actor ceiling. Treat it as the anchor: *that, and darker still.* `rune_potion_drop` is the
opposite pole and exactly the look [01](01-art-direction.md) decision 8 forbids.

The `contrast` column sets expectations too: props run 0.037–0.100, so an actor floor of 0.020 leaves room
while still rejecting a flat, muddy figure.

## `ACTOR` gate table

Starting values. Every one is confirmed by the first full render, then frozen with the measured number in a
comment, exactly as `check_iso.OVERRIDES` does today.

| Gate | Value | Prop value | Means |
|---|---|---|---|
| `crisp_min` | **0.93** | 0.95 | Share of partial-alpha pixels touching both an opaque and a transparent neighbour. **Relaxed**, not tightened: thin limbs, weapons and hair add partial-alpha pixels a prop silhouette never has |
| `partial_ratio_max` | **0.30** | 0.22 | Partial-alpha over opaque. Relaxed for the same reason |
| `sat_p95_max` | **0.26** | 0.35 | HSV S on pixels with a ≥ 240 and V ≥ .15 |
| `sat_p99_max` | **0.34** | — | The accent material's ceiling |
| `sat_over35_max` | **0.03** | — | Accent budget, [01](01-art-direction.md) decision 4 |
| `p5_v_max` | 0.06 | 0.06 | Shadows crushed. Unchanged — unaffected by the target, or easier |
| `p99_v_max` | **0.50** | *none* | **A ceiling where props have only a floor.** The inversion that forces the new file |
| `p99_v_min` | **0.28** | 0.35 | Something still catches the key. Below this the sprite is mud |
| `peak_v_max` | **0.62** | — | Props peak 0.49–0.69. No actor may approach the potion's 0.998 |
| `chrome_max` | **0.004** | 0.012 | Share over V .85. Tightened: dimming the reflection panel makes this easy |
| `contrast_min` | **0.020** | 0.012 | Mean 5x5 luma std. Tightened: a darker actor must still show form |
| `form_ratio_min` | 0.35 | 0.35 | Interior 4 u over .5 u band energy. Unchanged — it is alpha-normalised, so darkness does not move it |
| `form_interior_min` | 120 | 150 | Eroded interior pixels needed to measure form. Lowered: actor canvases are smaller |
| `warm_lit_min` | **3.0** | 3.0 | Mean R − B over the brightest 30 %. **Held, not lowered.** The amber shift in `GRIM_RIG` exists to hold it. If it fails, raise the key *hue*, never the intensity |
| `cold_band_max` | 1.0 | 1.0 | Mean R − B over opaque pixels in the luma band. The shadow side still reads cold |
| `cold_band` window | **8..48** | 12..60 | Widened and shifted down, because the whole actor sits lower in value |
| `cold_band_share_min` | **0.12** | 0.10 | That band needs this share of opaque pixels, or the shadows are crushed away entirely |
| `median_s_min` | 0.10 | 0.10 | Kept as a floor, with a **target of 0.13–0.18** reported by `--measure` so the art is judged, not merely passed |
| `foot` | **replaced** | `foot_min` 0.5 | See below |
| `hue_forbidden` | see below | — | [01](01-art-direction.md) decision 5, enforced numerically |

### The foot gate must be replaced, not retuned

`check_iso.py`'s `foot_min 0.5` is the opaque share of a 9x9 window centred on the pivot. **It breaks on every
biped**: two small feet with a gap between them cannot fill half of a 9x9 window, however correctly the actor
stands. This is a structural mismatch, not a threshold to nudge.

Replacement, two parts:

| Gate | Value | Means |
|---|---|---|
| `foot_row_tol` | 2 px | The lowest opaque row is within this of pivot y, in **every frame of every grounded clip** |
| `foot_share_min` | 0.18 | Opaque share of the 9x9 window at the pivot — enough to prove something is there |
| `centroid_tol` | 14 px | Opaque centroid x against pivot x, the same tolerance the potion gate already uses |

Clips that deliberately leave the ground — a jump, a death slump — are exempted from `foot_row_tol` by a
per-clip flag, not by a per-actor override.

### `hue_forbidden`

Checked on **rendered pixels**, not on palette tokens. A material's albedo may be more saturated than any pixel
it produces, because `_grade`'s `sat_cap` is applied per pixel after lighting. No opaque pixel with S ≥ 0.15 and
V ≥ 0.10 may fall in these HSV hue ranges (degrees):

| Range | Excludes |
|---|---|
| 270–345 | Pink, magenta, violet-pink |
| 75–165 | Acid and leaf green |
| 165–200 | Cyan |

Permitted: 0–75 (ember through bone-yellow), 200–270 (cold blue-grey through blue-violet), 345–360 (old blood).

MEASURED 2026-11-02, every token in [01](01-art-direction.md) scored against these bands: **`grim.verdigris`
(`#1E2A22`, hue 140.0, S 0.286, V 0.165) is the only violation.** Every other token is clear, and the closest
near-miss is `grim.iron.pitted` at hue 270.0 with S 0.059 — below the S 0.15 floor, so it does not trip.
Verdigris must be re-authored or dropped; see question 1 in [05](05-open-questions.md).

## Animation gates

A static-sprite gate table says nothing about motion, and `check_iso.py` gates **only frame 0**. Every actor
frame, of every clip, in every direction, is gated — plus:

| Gate | Value | Means |
|---|---|---|
| `frame_diff_min` | 1.5 | Mean absolute premultiplied difference between consecutive frames, 0..255. Proves the clip animates. The potion uses 2.0; actors idle more subtly |
| `frame_diff_max` | 28.0 | Proves no frame pops or teleports |
| `loop_closure` | exact | For `loop=True`, assert in **data, not pixels**, that `sample(clip, 0)` equals the clip's `t=1` key pose. Frame 0 and frame n−1 are distinct samples and *should* differ as pixels |
| `pivot_drift_max` | 1.0 px | For in-place clips, opaque centroid x may not drift from pivot x over the clip. A walk cycle must not slide |
| `canvas_fit` | alpha ≤ 16 | No frame puts sprite or shadow alpha above `EDGE_ALPHA` on any border row or column. A swing that leaves the canvas is a sizing bug |
| `direction_coverage` | all | All `dirs` frames exist and differ pairwise by ≥ 1.5/255 |
| `no_mirror` | 2.0/255 | **No two directions may be x-mirrors of each other.** Catches an accidental mirror optimisation, which would light the actor from the wrong side |
| `patina_stability` | 2.0/255 | The swimming test. Render one pose at two `root_move` offsets; after shifting by the projected offset the images must match. This is the direct proof that material noise is rest-space |

`patina_stability` is the most important gate in the file. Material noise in `isoforge` is keyed to **world**
position — `mott`, `pat`, `_rust`, `_moss` and `IsoMat.detail` all sample it — so under animation rust, moss
and pitting would swim across the surface frame by frame unless the noise reads bind-pose position instead.
See [02](02-tool-architecture.md).

Every gate gets a self-test on a synthetic image or clip that **must fail it**, matching `check_iso.py`'s rule:
a flat grained disc for form, a warm shadow band for the cold gate, a white blob for chrome, two identical
frames for the motion gate, a mirrored pair for `no_mirror`, a stale manifest for each manifest field.

## Determinism

Five rules, all measured:

1. **No time, no clock, no global RNG.** Everything seeds through `uiart.rng(name)` / `waves3(P, name, …)`,
   where `name` is built from fixed strings and integer indices only. Anything derived from a float time is a bug.
2. **Pose sampling is exact arithmetic on the frame index** — `t = i / frames`. Never accumulate `t += dt`.
3. **Material noise in rest space**, per [02](02-tool-architecture.md).
4. **`Scene.name` is the actor name alone** — `"wolf"`, never `"wolf_walk_d2_03"` — or the patina changes
   per frame. `patina_stability` catches a violation.
5. **Worker count must not affect output.** Jobs are sorted by `(actor, clip, dir, frame)`, never by hash, and
   results are reassembled by key as `gen_iso.render_all` already does.

The determinism run renders the set twice and compares bytes. It reuses the existing **`.iso_tmp`** folder in
the repo root rather than adding a second one, so temp output stays off the system drive; the folder is not
committed. `.gitattributes` gains the `char/` equivalents of the existing iso rules
(`char/**/*.json text eol=lf`, `char/**/*.png binary`) or the byte compare fails on Windows.

## Commands

```bash
.venv/Scripts/python tools/ui-assets/gen_char.py
```

```bash
.venv/Scripts/python tools/ui-assets/tests/check_char.py --fast
```

```bash
.venv/Scripts/python tools/ui-assets/tests/check_char.py --measure
```

```bash
.venv/Scripts/python tools/ui-assets/tests/check_char.py
```

`--fast`, `--partial` and `--measure` behave exactly as `check_iso.py`'s do. **No C# build or `dotnet test` is
needed**: this work touches no `.cs`, `.csproj` or shader file.

## What "verified" means here

An unavailable environment is unverified, not passing. A green `--fast` run is not a determinism result. A
passing gate table is not a look: the review sheet is part of the deliverable, and a human decides whether it is
grim enough.
