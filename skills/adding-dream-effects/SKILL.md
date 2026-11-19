---
name: adding-dream-effects
description: Adds or changes a Dream client visual effect end to end - a new particle atlas cell in tools/ui-assets/gen_particles.py, a spawn path in Dream.Rendering.Effects (FxSimulation, FxParticlePool, FxInstance), and the gameplay event that triggers it through FxEventBridge. Use when adding a cast, hit, death, impact or ambient particle effect, claiming a reserved particle frame, debugging an effect that is invisible or washed out, or fixing a red check_particles.py or FxAllocationTests run.
---

# Adding Dream Effects

Client-side presentation only. FX never feeds the authoritative simulation: all jitter comes from `FxSimulation`'s
private `FxRandom`, and nothing under `Dream.Simulation` reads FX state.

Atlas mechanics (venv, determinism, shipped-bytes, `check_sources.py`, art direction) are owned by the
**`generating-dream-assets`** skill - read its sections 1, 5 and 6 and do not re-derive them here. This skill covers
only what is particular to effects.

## The one fact that changes how you design

**`FxInstance.Frame` is a variant selector chosen once at spawn. It is not a flipbook.** Nothing advances `Frame`
over a particle's life - `StepParticles` ages position, size and fade, never the frame. An effect is therefore a
short **burst of single-frame sprites** with per-particle velocity, gravity, drag, rotation and lifetime. Families
with several variants (`spark` 0-3, `bolt` 0-1) pick one at spawn by adding `0..n-1` to the first frame, purely so a
burst does not look stamped.

If you plan an animation, you will build the wrong thing. Motion comes from many particles, not from one that
changes picture.

## 1. The atlas

`tools/ui-assets/gen_particles.py` -> `dream/ui/particles/particles.png` + `particles.json`. 512x256 RGBA8,
**8 x 4 = 32 cells of 64 px**, frame 0 top-left, rows downward, 4 px fully-zero margin per cell.

Claimed at the time of writing (`integrate/skill-fx` f7205d72): 0-22 ambient (`spark` 0-3, `ember` 4-7, `dust` 8-11,
`ash` 12-15, `halo` 16, `halo.core` 17, `flame` 18-21, `blob` 22), **23 `arc`, 24-25 `bolt.0/1`, 26 `ring`**.

**Do not trust that count - read it.** Another agent may have claimed cells since. The free range is whatever both
of these say, and they must agree:

- `dream/ui/particles/particles.json` -> `"reserved": [first, last]` (inclusive), and `frames` holding `0..first-1`.
- `RESERVED` in `tools/ui-assets/tests/check_particles.py:25`.

A new cell must be claimed in **three** places or a gate goes red:

1. `GROUPS` in `gen_particles.py:27` - `("<name>", <firstIndex>, <count>, "zero"|"coverage")` plus a painter.
2. `check_particles.py`: the index constant, membership of `ADDITIVE` (`:24`) if `zero`-alpha, the narrowed
   `RESERVED` range (`:25`), the expected `frames` length and the expected `reserved` pair.
3. `FxFrame` in `dream/Rendering/Effects/FxInstance.cs:8` (the C# mirror), and the expectations in
   `dream.Tests/Rendering/Effects/FxAtlasAssetTests.cs`.

`particles.json` is generated - never hand-edit it. Regenerate with a **full** run (`python tools/ui-assets/gen_particles.py`),
never `--only`, or the manifest goes stale.

### Alpha classes

RGB is **premultiplied emission and stays meaningful where A = 0**; A is only how much the quad occludes what is
behind it.

- `zero` (additive): `A == 0` everywhere, RGB peak >= 100 (a hot core such as `bolt`: >= 200). Draws as pure light.
  The cell's index **must** be in `check_particles.ADDITIVE`.
- `coverage`: A is real coverage, `rgb*2 >= a` wherever `a > 8`, no emission outside the coverage. The instance
  alpha scales it, which is how `flame`, `arc` and `ring` are additive at night and occlude the bright sky by day
  (`FxSkillCastTests.Ground_Quads_Are_Pure_Light_At_Night_And_Occlude_By_Day`).

Authored RGB peaks stay <= 245 with no clipped plateau, in every class.

## 2. Brightness - two different rules

The "keep catalog channels <= 0.40 because render colours are multiplied up to ~2.17x with no tonemap" rule is about
**world albedo**. It does **not** apply to particles: `FxInstance.PackColor` writes premultiplied emission straight
through and clamps at 255.

The particle rule is clipping, not darkness:

- authored atlas RGB peak <= 245, no plateau (gated by `check_particles.py`);
- at run time `tint * brightness <= 0.9` per channel at peak. See `CastBrightness = 0.9f` and the three tints at
  `FxSimulation.cs:119-124`;
- assert it: `FxSkillCastTests.No_Packed_Channel_Pins_At_255_At_Any_Time_Of_Day` is a theory over night factors
  **0, 0.5 and 1**. Add your effect to it.

No new emissive material anywhere outside the atlas.

## 3. Scale and legibility

The player is about **28 x 101 px** on screen in the isometric view. Subtlety is the main failure mode - a tasteful
effect is simply invisible. Size against world metres the player can see: the Cleave crescent's painted outer edge equals
the skill's own 3.5 m range and is placed by `FxArcGeometry` (`HalfSizeForRange`, `OuterRadiusFraction`, and
`PaintedArcDegrees`, which must equal every MeleeArc skill's `arcDegrees`), never by a hand-tuned multiple; it is emitted
when the swing turns active (`MeleeArcSwing.WindupSeconds`) with steel sparks along the edge, left to right, and a blade trail placed by `FxBladeTrailGeometry` (white and additive, each segment gone within `2 x ActiveSeconds` of its birth). The Mend ring is ~0.9 m at the feet with motes rising past head height, the Bolt lance leaves the player
entirely. Make hue the third discriminator (cold steel / cold violet / bone-green) so two effects never read alike.

Bold low-frequency shapes also **compress better**, which matters against the PNG cap below. Fbm fleck storms cost
bytes and vanish at this scale anyway.

## 4. Zero allocations per frame

`FxAllocationTests` asserts **exactly 0 bytes** via `AllocationAssert.AtMost(0, ...)` around spawn, `Advance` and
`WriteInstances`. In any new FX code path that forbids: LINQ, lambdas or closures, `params` arrays, collection
expressions, `List<T>`, array creation, boxing (including an interface-typed local over a struct), eager string
construction (not even in a log or exception message), and iterator methods.

Use fixed-size fields, plain `for` loops, and `in` / `ref` parameters. Everything new is a struct in the fixed pool.
Add your spawn entry point to `FxAllocationTests` - on a **full** pool, so the drop path is covered too.

Note: any exact-0-byte allocation test flakes under a loaded full suite. Re-run it alone before believing a failure.

## 5. Pool safety

`FxLimits` (`FxInstance.cs:33`) bounds everything: `MaxInstances` 2048 minus `HaloBudget` 128 minus `ShadowBudget` 32
= `ParticleCapacity` 1888.

A burst must not starve ambient dust and torch embers, so it takes a budget and leaves a reserve
(`FxSimulation.cs:95-98`, `MaxCastParticles = 19`, `CastPoolReserve = 128`):

```csharp
int budget = Math.Max(Math.Min(MaxCastParticles, _pool.Capacity - _pool.Count - CastPoolReserve), 0);
```

Every emit decrements that budget and goes through `_pool.TryAdd`, which drops and counts `DroppedSpawns` on a full
pool. Ambient top-up uses `TryAddQuietly` instead, so a full pool does not inflate the counter every frame. Declare a
hard `const` cap per effect and assert it: one trigger adds at most N particles. No new unbounded list, no new halo
or flash slot (`FxFlashList` has 4 slots with tuned `Radius`/`Life` tables - a fifth kind is a separate change).

## 6. Ground-aligned quads and draw order

`FxInstance.Stretch`: 0 or 1 is a camera-facing square, above 1 stretches along local +X, and
`FxInstance.GroundAligned` (-1f) lays the quad flat on XZ. Flat quads also need a small `DepthBias` (0.4f, the
held-light value) so the floor does not cut them - see `WriteParticle`, `FxSimulation.cs:629-640`.

`WriteInstances` (`:278`) writes in two passes. `IsUnderlay(FxParticleKind)` (`:316`) selects the first: `Ash`, `Arc`
and `Ring` draw under everything else, so a flat quad sits beneath its own sparks instead of popping across them. Add
a new flat quad kind there.

Streaked particles (`Spark`, `Bolt`) go through `FxSimulation.Streak` (`:675`), which lays the quad along the
screen-space projection of its velocity and stretches it with speed - that is how a dart looks like a lance without
any stateful tracer.

## 7. The event seam - never call FX from gameplay code

Gameplay publishes an event on the `SceneEventHub`; `FxEventBridge` is the **only** production caller of a spawn
method.

```
PlayScene.Replication.cs:297  ->  ClientSkillCastPublisher.Publish(...)   // maps SkillKind -> SkillFxKind
     -> events.Publish(new SkillCastEvent(kind, origin, direction, range))
          -> FxEventBridge.OnSkillCast (FxEventBridge.cs:53)
               -> FxSimulation.SpawnSkillCast (FxSimulation.cs:208)
```

**Why it matters:** the seam is what keeps remote visibility a purely additive change later. Adding a snapshot
`CastEvents` list only has to publish the same `SkillCastEvent` from a new applier - nothing in the atlas or the
simulation moves. Calling `FxSimulation` directly from the scene would couple presentation to one code path and
forfeit that.

Follow the shape of the existing handlers: resolve the anchor **inside** the handler while the actor is still
resolvable; validate in the spawn method (non-finite origin returns, a zero or non-finite direction means "no
direction" and falls back to a radial burst); and dispose the subscription with the others.

## 8. Current limitation - state it, do not quietly work around it

**A skill cast is visible only to the caster.** `AbilityUsedResult` is an action *result*, unicast to the acting
session; the snapshot's `DamageEventDelta` names the victim, carries no skill identity, origin or direction, and
`skill.mend` produces no damage event at all, so a peer's Mend is invisible on the wire.

Remote visibility needs a snapshot `CastEvents` list, a `CastEventDelta(int ActorId, SkillKind Kind, Vector3 Direction)`,
a **protocol version bump in all five spellings**, and a `ClientSnapshotCastApplier`. Scoped but not built:
`docs/ledger/plans/2026-11-18-skill-effect-visuals.md`. Do not start it inside an effect change.

Also open from that plan: a bolt visually passes through walls (the server stops it, but no impact point is on the
wire), and the effect aims with the client camera forward while the server resolves on its own ray.

## 9. The PNG byte cap

`MAX_PNG_BYTES` in `check_particles.py:28` is currently 40 KB; the shipped sheet is ~38.8 KB. Order of operations:

1. **Fit the art first** - low-frequency shapes, no fleck noise.
2. Only if it genuinely does not fit, raise that **one** constant, with a comment saying what was added.
3. **Never degrade, shrink or re-tune an existing frame to make room**, and never touch another gate constant.

Raising the cap costs nothing at run time: dimensions, grid and GPU cost are unchanged. 512x256 RGBA8 is 512 KB in
VRAM whatever the PNG compresses to. The cap is a budget guard, not a performance limit.

## 10. Verify

Run from the worktree root. Python setup and version check: `generating-dream-assets` section 1.

```powershell
# Iterate into a scratch dir, never the shipped one
python tools/ui-assets/gen_particles.py artifacts/tmp/try --only <group> --preview

# Ship: a FULL run (no --only, no out dir), then the preview sheet
python tools/ui-assets/gen_particles.py

python tools/ui-assets/tests/check_particles.py     # expect "N frames, 0 errors"
python tools/ui-assets/tests/check_sources.py       # expect exactly 2 FAILs, both pre-existing
```

`check_sources.py` reports `FAIL enginelook.py:525` and `FAIL enginelook.py:640` (unclassified reads). **Those two
are pre-existing and are not yours.** Any third failure is.

Determinism: re-run the full generator and byte-compare the output (`git status` clean after a second run). Do not
pass `--fast` for the pre-commit run.

Engine tests - build `dream\dream.csproj` once first so the test project can copy the native Bullet files, and use an
absolute `BaseOutputPath` **inside this checkout** (a running `dream.exe` locks `dream\bin`; never redirect it into
another worktree):

```powershell
dotnet build dream\dream.csproj
dotnet test dream.Tests\dream.Tests.csproj "/p:BaseOutputPath=<abs worktree>\artifacts\verify-bin-tests\" `
  --filter "FullyQualifiedName~Dream.Tests.Rendering.Effects"
```

Visual check is manual: cast or trigger the effect at noon and at night and confirm it reads at isometric distance.
Do not spend the session on the screenshot capture harness - it is broken on current master.

Commit the generator, the gate edits, the regenerated PNG, the manifest, the preview sheet and the C# together;
`git status` must show only effect-family files.

## Quick failure guide

| Symptom | Cause |
|---|---|
| New frame missing from `particles.json` | written by an `--only` run; rerun the full generator |
| `check_particles` "reserved" / frame-count error | narrowed `RESERVED` or the index constant not updated in the gate |
| Additive cell flagged for alpha | index missing from `ADDITIVE`, or the painter wrote a non-zero A |
| Effect invisible at distance | too small or too few particles - size against metres, not pixels |
| Effect washed out / a channel pinned | `tint * brightness` over 0.9, or an authored RGB plateau |
| Flat quad cut by the floor | `Stretch` not `GroundAligned`, or no `DepthBias` |
| Flat quad pops over its own sparks | kind not added to `IsUnderlay` |
| `FxAllocationTests` red | a closure, LINQ, `params`, a collection expression or a string built on a spawn path - but re-run it alone first, it flakes under a loaded suite |
| Burst eats the ambient dust | emits not bounded by the `CastPoolReserve` budget, or bypassing `TryAdd` |
