# 05: Open questions and risks

Decide these before or during the first implementation wave. Each says what settles it.

## Blocking

| # | Question | Why it blocks | Recommendation |
|---|---|---|---|
| 0 | **Sprites, glTF, or both?** See [06](06-output-target.md) | It changes what `charforge` *is*. Files 02–04 assume sprites; the engine consumes glTF today | **glTF first, structured so sprites become a second back end.** The reasoning is in [06](06-output-target.md) |

Nothing else here stops work starting on [03](03-animation-model.md)'s shared skeleton and clip model, which is
built the same way under either target.

## Needs your decision

| # | Question | Why it matters | Default if unanswered |
|---|---|---|---|
| 1 | **Is `grim.verdigris` kept?** MEASURED at hue 140.0, S 0.286 — the only token violating the forbidden-hue rule in [04](04-gates-and-verification.md) | The gate is a hard rule, and this is the one exemption it would need | **Drop it.** Rust and old blood cover the accent need; one fewer exemption keeps the hue rule absolute |
| 2 | **4 facings or 8?** (Target A only) | 8 doubles render cost, sheet size and authoring. No mirroring is possible, so the cost is strictly linear | **4.** `ActorSpec.dirs` makes 8 a data change later |
| 3 | **Do actors bake their weapon in, or leave a socket?** | Baked is one less runtime contract; a socket allows per-weapon overlays later | **Baked for the first wave**, but declare a `hand.r` joint anyway so an overlay stays possible |
| 4 | **Is the player part of this?** | It is seen closest and longest, and may deserve a larger canvas and more clips | Yes — actor one, per [06](06-output-target.md) |

## Needs evidence, not a decision

| # | Question | How it gets settled |
|---|---|---|
| 5 | Do the `GRIM_RIG` numbers in [01](01-art-direction.md) land inside the [04](04-gates-and-verification.md) table? | Render the first actor, `--measure`, retune. The rig numbers are starting points; the gates are the contract. If they conflict, the rig moves first |
| 6 | Does an actor read at all under the value ceiling? | **The real risk.** The review sheet decides. `p99_v_min` 0.28 exists to catch mud, and the ceiling is the first number to relax if the sheet fails |
| 7 | Does `isoforge` stay byte-neutral under the `Mesh.rest` change? | The full `check_iso.py` run in the contract commit is the proof. If it fails, the work stops and reports rather than loosening the gate |
| 8 | Does bind-pose noise lookup hold still under large rotations? | `patina_stability` in [04](04-gates-and-verification.md). A limb rotated 90° must show the identical grime pattern |
| 9 | ~~Can `check_iso.py`'s metric helpers be imported without executing its gate run?~~ | **ANSWERED** 2026-11-02: yes, it is `__main__`-guarded. See [04](04-gates-and-verification.md) |
| 10 | Does re-welding normals after skinning cost more than skinning itself? | Profile frame 0 of `walk`. If welding dominates, cache the weld topology from the bind pose — connectivity does not change |
| 11 | One shadow per frame, or one per `(clip, dir)`? (Target A only) | `Scene._shadow_layer` comes free with the render, so per-frame costs nothing but doubles the file count. **Assume per-frame**, matching the iso set, and revisit if the directory becomes unwieldy |

## Risks

| Risk | Severity | Mitigation |
|---|---|---|
| **Built for the wrong target.** The tool produces sprites nothing can render | **High** | Question 0. This is why [06](06-output-target.md) exists and why no code is written yet |
| **The look is too dark to ship.** Every gate passes and the sheet is unreadable | High | Build one actor before building a roster. Do not author against unvalidated rig numbers |
| **Skinning breaks determinism.** Posed-space noise swims between frames | High | `Mesh.rest` and `patina_stability` are part of the first workstream, not a later refinement |
| **The `rest` change regresses the prop set.** `check_iso.py` byte-compares committed sprites | High | Props pass `rest=None` → `Pr = Pw`, which must be a provable no-op. Full `check_iso.py` before committing any `isoforge.py` change |
| **`check_sources.py` blocks the first import.** `import charforge` fails the hygiene gate | Low but immediate | The one-line `ALLOWED_MODULES` edit goes in the shared contract commit |
| **Gate inflation.** Overrides accumulate until the table means nothing | Medium | `check_iso.py`'s existing rule: an override is a claim, with a comment giving the reason and the measured value |
| **Canvas sized from one clip.** `attack` reaches further than `idle` and clips at the edge | Medium | Canvas is the union over every pose, gated by `canvas_fit` |
| **The biped foot gate.** `check_iso.py`'s `foot_min 0.5` cannot pass on two feet with a gap | Medium — **already found** | Replaced, not retuned. See [04](04-gates-and-verification.md) |

## Explicitly out of scope

- Any C# change in the first wave. No loader, no renderer, no csproj.
- Terrain, tilesets, ground planes and light decals — the reference's warm floor pools are an engine feature.
- Runtime fog, bloom and colour grading. That is `integrate/fx-ldr`'s territory, and [06](06-output-target.md)
  argues it is the *reason* to prefer engine-lit geometry.
- Equipment layering and per-slot swaps. The first pass bakes gear into the actor.
- Replacing the CC0 prop and nature packs. Actors first; `gen_iso_*` already covers some props.
- Blender, GPU rendering, or any new dependency. `numpy` and `pillow`, pinned, remain the whole stack.
