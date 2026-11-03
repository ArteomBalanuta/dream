# Plan: grim actor forge

**Goal:** add a deterministic procedural forge that generates `dream`'s own actor models, animations and
assets — original art, grimmer and darker than the current prop set — replacing the third-party CC0 glTF packs
the game ships today.

**Spec:** [docs/superpowers/specs/2026-11-02-grim-actor-forge/](../../superpowers/specs/2026-11-02-grim-actor-forge/00-README.md)

**Evidence level:** every claim comes from reading the source named with it, plus measurements run in this repo
on 2026-11-02 (recorded in the spec). **No tool code has been written.**

## Status: blocked on one decision

`06-output-target.md` records a fork that changes the shape of the tool, and it is the user's call:

- **Target A — isometric sprite sheets.** Reuses the whole `forge`/`isoforge` chain, so the grim look is nearly
  free, but nothing in C# consumes actor sprites and nothing will until `integrate/isometric` lands plus a new
  loader wave.
- **Target B — original glTF with skeletal clips.** Drops straight into the existing loader
  (`WolfAssetDefinition` already names `Idle`/`Walk`/`Attack`), but the engine lights it, so the baked
  art-directed light that makes our 2D art good is lost.
- **Target C — both**, sharing the skeleton and clip model.

Recommendation: **B first, structured so A is a second back end.** The look belongs where the grade already
lives (`integrate/fx-ldr`), and baking a second grade into sprite pixels would fight it.

The workstreams below are written for Target A, which is what the spec's files 02–04 describe. Under B, WS-A's
`render_frame` becomes a glTF writer, WS-C's pixel gates become structural gates, and the C0 hooks to
`isoforge.py` are unnecessary. **The shared core — `03`'s `Joint`/`Skeleton`/`Pose`/`Key`/`Clip`/`ActorSpec`,
pose sampling and skinning — is identical under both**, so C0 and the first half of WS-A are safe either way.

## Measured baseline

`check_iso.py --fast`: **7 objects, 0 errors.** The prop set is clean, which is the precondition for touching
`isoforge.py`. Per-object numbers are in the spec's `04`.

Cost, one core: `rust_barrel` 4.61 s for 0.33 Mpx hi-res = **14.1 µs/hi-res px**; the same render at dome 8
takes 2.86 s, a **38 % saving**. The README's 4.4 µs/px is the forge chain alone and must not be used for
planning. A 160x192 actor canvas costs ~4.5 s/frame at dome 10, so one actor at 4 facings and 24 frames is
~1.8 min on 4 workers.

## C0 — shared contracts, one commit before the split

Three files would otherwise be shared between workstreams. Every worktree resets to this tip (worktrees start
from master, so reset them).

**Files:** `tools/ui-assets/isoforge.py`, `tools/ui-assets/charforge.py` (new, data layer only),
`tools/ui-assets/tests/check_sources.py`, `.gitattributes`.

1. `isoforge.py` — three backwards-compatible hooks: `Scene.render(..., rig=ISO_RIG, crush=CRUSH, dome=20)`,
   `_grade(rgb, cap, crush=CRUSH)`, and `Mesh.rest: np.ndarray | None = None` threaded through
   `__post_init__`, `xform`, `merge`, `displace`, `_unwelded`, `_merged` and `Scene.render` (interpolated as
   `Pr` and fed to the `mott`/`pat`/`_rust`/`_moss`/`IsoMat.detail` noise; `None` falls back to `v`).
2. `charforge.py` — the spec's dataclasses, `sample`, `Skeleton.bind_world`, `facing_yaw`, and `GRIM_RIG` /
   `CHAR_CRUSH` / `CHAR_RUST_CAP`. `bind`, `skin` and `render_frame` are declared with full signatures and
   `raise NotImplementedError`.
3. `check_sources.py` — `ALLOWED_MODULES += {"charforge"}`, or `import charforge` fails the hygiene gate.
4. `.gitattributes` — `char/**/*.json text eol=lf`, `char/**/*.png binary`.

**Verification:** the full `check_iso.py`, including determinism, must pass **unchanged** — that is the proof
the hooks are byte-neutral — plus `check_sources.py` and `check_art.py --fast`. If it fails, stop and report;
do not loosen the gate.

## WS-A — `charforge` (the engine)

**Owns:** `tools/ui-assets/charforge.py`, and the generated `tools/ui-assets/previews/charforge_sample.png`.

1. `bind()` — two-bone capsule weights by point-to-segment distance, `blend ≈ 0.8 u`.
2. `skin()` — linear blend skinning, `rest` propagation, normals recomputed from the posed vertices (never
   blend-transformed: LBS makes them non-orthogonal).
3. `render_frame(spec, clip, dir, frame) -> isoforge.Rendered` — build and bind cached per process, skin, yaw,
   `Scene(spec.name, …)`, `render(**spec.render, rig=GRIM_RIG, crush=CHAR_CRUSH, dome=10)`.
4. `python charforge.py` writes `previews/charforge_sample.png`: a minimal three-joint figure in four poses,
   the acceptance sample for the chain, mirroring `forge.py`'s sample-plate convention.

**Published contract** (frozen at C0 so B and C build against it): `bind`, `skin`, `facing_yaw`, `sample`,
`frame_count`, `render_frame`, `GRIM_RIG`, `CHAR_CRUSH`, `CHAR_RUST_CAP`.

**Tests:** the sample renders; one frame rendered twice in-process and across two processes is byte-identical;
`skin()` with an identity pose reproduces the bind mesh exactly; `sample()` on a loop clip closes.

## WS-B — actors (registries, writer, manifest)

**Owns:** `tools/ui-assets/gen_char.py`, `gen_char_player.py`, `gen_char_wolf.py`, and the generated
`tools/ui-assets/char/` tree.

1. `gen_char.py` — `load_actors()` over the registry modules skipping absent ones, a fixed `ORDER`, the
   `_job`/`ProcessPoolExecutor`/`DREAM_ART_WORKERS` shape from `gen_iso.py`, PNG writing, the spec's manifest,
   the grid review sheet, and `--only` / `--clip` / `--dir` / `--pose`.
2. `gen_char_player.py` — a 14-joint humanoid (root, pelvis, spine, chest, neck, head, two of
   clavicle/upper/fore/hand, two of thigh/shin/foot), parts from `tube`/`lathe`/`box`/`card`, grim `IsoMat`s,
   clips idle 4 / walk 8 / attack 6 / die 6.
3. `gen_char_wolf.py` — the quadruped, ~9.6 u tall per `WolfDefinition`, its own skeleton, clips named to match
   `WolfAssetDefinition`'s `Idle` / `Walk` / `Attack`.

**Consumes:** WS-A's contract, plus `ACTORS: dict[str, charforge.ActorSpec]` as the registry name.

**Tests:** `gen_char.py --only <actor>` into a temp dir; `check_char.py --partial --fast` green; the review
sheet judged by eye against the project's art direction — dark, grim, gothic, original art only.

## WS-C — gates and docs

**Owns:** `tools/ui-assets/tests/check_char.py`, `tools/ui-assets/README.md`, this plan, the work-log entry.

1. `check_char.py` with the spec's `04` constants, importing metric helpers from `check_iso.py` and **never
   editing it**. Note the two structural changes from the prop gates: `p99_v` becomes two-sided, and the 9x9
   `foot_min` is replaced because it cannot pass on a biped.
2. The synthetic self-tests — every gate must fail an image built to fail it. All writable before any art
   exists.
3. `--fast`, `--partial`, `--measure` exactly as `check_iso.py`.
4. A README section mirroring the isoforge one: chain, rig numbers, form bands, output paths, cost, the `char/`
   contract, and the no-mirroring rule.

**Note:** `check_iso.py` becomes a de facto shared contract by import. It must not be edited in this wave; if a
metric needs changing, the implementer stops and reports it.

## Merge order

`C0` → **WS-A** → **WS-B** → **WS-C**. All three can be *built* in parallel from the C0 tip: B authors
registries and clip data against C0's real dataclasses (clip evaluation is testable with no renderer), and C's
self-tests need no art at all.

## Verification

- **C0:** full `check_iso.py` (byte-identical props), `check_sources.py`, `check_art.py --fast`.
- **Integration:** `check_char.py` full, `check_iso.py` full, `check_sources.py`, `check_art.py`.
- **No `dotnet build` or `dotnet test`** — this wave touches no `.cs`, `.csproj` or shader file.
- At most two full-suite runs in a row per verification step; isolate flakes instead.
- Temp output goes to the existing `.iso_tmp` in the repo root and is never committed. Output stays on G:.

## Open questions

Carried in the spec's `05`. The blocking one is the output target above. The rest: whether `grim.verdigris`
survives the forbidden-hue rule (MEASURED hue 140.0 — it does not, as authored), 4 versus 8 facings, baked
weapons versus a `hand.r` socket, and whether the value ceiling leaves an actor readable at all.
