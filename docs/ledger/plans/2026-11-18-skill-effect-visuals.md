# Skill effect visuals: Cleave, Bolt, Mend

- **Purpose**: give the three shipped combat skills distinct, readable cast effects, built from the repo's own particle generator.
- **Status**: execution-ready plan. Not started.
- **Scope**: client FX only (particle atlas, `Dream.Rendering.Effects`, one client publish site). No protocol change, no simulation change, no tick/pacer change.

## Goal

When the local player casts `skill.cleave`, `skill.bolt` or `skill.mend`, the client plays a distinct, instantly
recognisable particle effect at the player, authored as four new cells in the existing particle atlas.

## Current state (evidence)

- Three skills, `dream/config/content/skills.json`: `skill.cleave` (`MeleeArc`, range 3.5, arc 120, 4 targets),
  `skill.bolt` (`RangedBolt`, range 18), `skill.mend` (`SelfHeal`, 35).
- The FX simulation has exactly two combat entry points: `SpawnHit(Vector3, FloatingCombatTextKind)` at
  `dream/Rendering/Effects/FxSimulation.cs:147` and `SpawnDeath(Vector3)` at `:166`. Nothing else spawns combat FX.
- They are driven only by `DamageAppliedEvent` / `ActorDiedEvent` on the `SceneEventHub`
  (`dream/Rendering/Effects/FxEventBridge.cs:21-22`). Neither event names a skill. So every skill currently looks
  like a plain hit, and `skill.mend` (which deals no damage) looks like nothing at all.
- Atlas: `tools/ui-assets/gen_particles.py` writes `dream/ui/particles/particles.png` and `particles.json`,
  512x256 RGBA8, 8x4 grid of 64 px cells, groups spark(0-3) ember(4-7) dust(8-11) ash(12-15) halo(16) halo.core(17)
  flame(18-21) blob(22); the manifest says `"reserved": [23, 31]` (9 empty cells). C# mirror: `FxFrame` in
  `dream/Rendering/Effects/FxInstance.cs:8`.
- Pool: `FxLimits.ParticleCapacity` = 2048 - 128 - 32 = 1888 (`FxInstance.cs:29-35`). `FxInstance.Stretch` already
  supports `GroundAligned = -1f` (flat on XZ) and the spark path already computes a screen-space streak
  (`FxSimulation.Streak`, `:574`).

## The replication decision

**Verdict: own-cast only for this pass. A skill cast does not reach any other client today, and making it do so
needs a protocol version bump.**

Evidence:

- `AbilityUsedResult(string Key, int TargetsHit, int DamageApplied, int HealthRestored)`
  (`dream/Multiplayer/Protocol/AbilityUsedResult.cs:5`) is the only message that names a skill. It carries no origin
  and no direction.
- It is an action **result**, and action results are unicast to the acting session:
  `dream/Server/DedicatedServerService.cs:419-431` enqueues `ServerActionMessage` only on the connection whose
  `LastJoinRequest.RequestedSessionId` equals `actionResult.SessionId`. A peer never receives it, and
  `ClientAuthoritativeActionApplier.ApplyActionResult` would discard it anyway
  (`dream/Client/Replication/ClientAuthoritativeActionApplier.cs:32-36`).
- What peers do get is `DamageEventDelta(ActorId, Kind, Amount, ActorDied, CurrentHealth, MaxHealth, HitKind)` inside
  the snapshot (`dream/Simulation/Runtime/WorldSessionRuntime.Replication.cs:325-346`, applied by
  `ClientSnapshotDamageApplier`). It names the **victim**, not the caster, and carries no skill identity, origin or
  direction. `skill.mend` produces no damage event at all, so a peer's Mend is literally invisible on the wire.

So replicating casts needs a new wire field, a protocol version bump and a codec/golden pass - most of the work, for
an effect the caster still would not see any sooner. The caster's own effect ships first.

**Minimum addition for a later second pass** (documented, not built now): add
`IReadOnlyList<CastEventDelta> CastEvents` to `ServerSnapshotEnvelope`, with
`readonly record struct CastEventDelta(int ActorId, SkillKind Kind, Vector3 Direction)` queued from
`WorldSessionRuntime.Skills.cs` next to the existing `_pendingDamageEvents` plumbing; bump the protocol version (v21
is taken by worn-visuals, so v22) in all five spellings; apply in a new `ClientSnapshotCastApplier` that publishes
the same `SkillCastEvent` this plan defines. Because workstream C publishes an event rather than calling FX
directly, that second pass is purely additive: nothing in workstreams A or B changes.

## Art direction and the emission rule

- Dark, grim, gothic. These are **particles, not albedo**: the "keep catalog channels <= 0.40 because render colours
  are multiplied up to ~2.17x with no tonemap" rule is about world albedo and does **not** apply here. Particle RGB
  is premultiplied emission written straight through `FxInstance.PackColor`, which clamps at 255.
- **The rule applied**: (1) authored atlas RGB peaks <= 245 with no clipped plateau, exactly as `flame` and `halo`
  are gated today; (2) at run time `tint_channel * brightness <= 0.9` for every channel at peak, so no packed channel
  can pin; (3) no new emissive material anywhere outside the atlas - only new atlas cells.
- **Daylight legibility** reuses the flame's existing trick rather than inventing one: the two ground-aligned frames
  are authored as `coverage` alpha frames (like `flame.0-3`), so the instance alpha is 0 at night (purely additive
  light) and rises by day (occludes the bright sky). The bolt frames stay `zero` alpha (purely additive), like sparks.
- **Scale**: the player is ~28x101 px on screen in the isometric view, so subtlety is invisible. The arc sweeps a
  radius near the skill's own 3.5 m range (about three player widths), the mend ring is ~0.9 m at the feet with motes
  rising above head height, and the bolt is a 12-dart lance that leaves the player entirely. Hue is the third
  discriminator: cold steel (Cleave), cold violet (Bolt), sickly bone-green (Mend).

## Frozen contracts

Frozen before implementation starts; all three workstreams code against them.

### 1. Atlas frames (owned by workstream A, consumed by B)

| Index | Group name | Alpha | Shape |
|---|---|---|---|
| 23 | `arc` | `coverage` | A swept crescent: thick at the leading edge, tapering to nothing at both ends, drawn flat (ground-aligned). |
| 24 | `bolt.0` | `zero` | A lance head: hot narrow core with a tapered tail along +X (the instance supplies angle and stretch). |
| 25 | `bolt.1` | `zero` | As `bolt.0`, a different fleck/kink seed. |
| 26 | `ring` | `coverage` | A thin soft annulus, dark centre, drawn flat (ground-aligned). |

Frames 27-31 stay reserved and empty. `particles.json` gains frames 23-26 (27 entries, indices 0..26) and
`"reserved": [27, 31]`. The grid, cell size, margin, `yDown` and `alpha: premultiplied` are unchanged.

### 2. C# frame mirror (owned by B)

In `dream/Rendering/Effects/FxInstance.cs`, added to `enum FxFrame`: `Arc = 23`, `Bolt0 = 24` (two variants, add
0..1), `Ring = 26`.

### 3. The cast event and the spawn entry point (owned by B)

New file `dream/Rendering/Effects/SkillCastEvent.cs`, namespace `Dream.Rendering.Effects`:

- `public enum SkillFxKind : byte { Arc, Bolt, Mend }`
- `public readonly record struct SkillCastEvent(SkillFxKind Kind, Vector3 Origin, Vector3 Direction, float Range);`

Origin is the caster's world position as `Player.GetWorldPosition()` returns it (capsule centre); `FxSimulation`
applies its own lifts and drops. Direction is a horizontal unit vector; a zero or non-finite direction means "no
direction" and the effect falls back to a radial burst. Range is the skill definition's range in metres (0 for a
self-cast).

On `FxSimulation`, public, beside `SpawnHit`/`SpawnDeath`:

`public void SpawnSkillCast(SkillFxKind kind, Vector3 origin, Vector3 direction, float range);`

`FxEventBridge` subscribes to `SkillCastEvent` and forwards it to `SpawnSkillCast`; it is the only production caller.

### 4. Per-cast particle budget (owned by B, hard `const` caps)

| Effect | Particles | Life |
|---|---|---|
| Arc (Cleave) | 1 `Arc` quad + 14 sparks + 4 embers = 19 | quad 0.25 s, sparks 0.3-0.6 s |
| Bolt | 12 `Bolt` darts + 6 sparks = 18 | darts `range / speed`, capped 0.8 s |
| Mend | 1 `Ring` quad + 14 embers = 15 | ring 0.6 s, embers 0.9-1.6 s |

At most 19 particles per cast against a 1888 pool, and the shortest cooldown is 64 ticks, so the sustained cost is
negligible. Spawns go through `_pool.TryAdd` (the hit/death path), so a full pool drops them and counts
`DroppedSpawns`; nothing is unbounded.

### 5. Tints (owned by B)

- Arc: `new Vector3(0.70f, 0.74f, 0.85f)` cold steel.
- Bolt: `new Vector3(0.52f, 0.40f, 0.90f)` cold violet.
- Mend: `new Vector3(0.46f, 0.68f, 0.44f)` sickly bone-green.

Peak brightness multiplier <= 0.9 for all three, so no packed channel reaches 255.

## Workstreams

### A - Atlas: four new particle cells

Owned files (no other workstream touches these):

- `tools/ui-assets/gen_particles.py`
- `tools/ui-assets/tests/check_particles.py`
- `dream/ui/particles/particles.png`
- `dream/ui/particles/particles.json`
- `tools/ui-assets/previews/particles.png`
- `dream.Tests/Rendering/Effects/FxAtlasAssetTests.cs`

Steps:

1. Read the `gen_particles.py` docstring and `check_particles.py` in full. Use the repo venv (`.venv/Scripts/python`)
   and confirm `numpy 2.5.4`, `PIL 13.0.0` before trusting any byte gate.
2. Add four painters and extend `GROUPS` with `("arc", 23, 1, "coverage")`, `("bolt", 24, 2, "zero")`,
   `("ring", 26, 1, "coverage")`. Keep every shape low-frequency (no fbm fleck storms) - see the byte budget risk
   below. Keep the 4 px margin exactly zero and fade to 0 before the drawn area ends (the existing window() helper).
3. Coverage frames must satisfy the flame rules the gate already enforces: an A peak in a sane band, `rgb*2 >= a`
   wherever `a > 8`, no emission outside the coverage, RGB peak <= 245. Additive frames must have `A == 0` everywhere
   and an RGB peak >= 100 (bolt: >= 200, it is a hot core).
4. Update `check_particles.py`: `RESERVED = range(27, 32)`, `ARC`/`BOLT`/`RING` index constants, the new additive
   indices in `ADDITIVE`, `indices != list(range(27))`, `doc.get("reserved") != [27, 31]`, a duplicate-frame check
   for the `bolt` pair, and per-frame peak/plateau checks for the two coverage frames mirroring the `FLAME` block.
5. Regenerate with a **full** run (no `--only`): `python tools/ui-assets/gen_particles.py`, and the preview sheet.
6. Update `FxAtlasAssetTests.cs`: `frames` length 27, `reserved[0] == 27`, `Reserved_Cells_Are_Empty` from 27, add
   24/25 to the additive theory, add arc/ring cases to a coverage theory mirroring the flame one.
7. Run `check_particles.py` and `check_sources.py` green.

Risk to settle inside this workstream: `particles.png` is **32,241 bytes against a 32 KB cap** in
`check_particles.py` - 527 bytes of headroom. If four low-frequency cells do not fit, raise that one cap to 40 KB
with a comment stating that the sheet dimensions, grid and GPU cost are unchanged and only four cells were added.
Do not shrink or re-tune an existing frame to make room, and do not touch any other gate constant.

### B - FX: the three effects in the simulation

Owned files:

- `dream/Rendering/Effects/FxSimulation.cs`
- `dream/Rendering/Effects/FxParticlePool.cs` (the `FxParticleKind` enum only)
- `dream/Rendering/Effects/FxInstance.cs` (the `FxFrame` enum only)
- `dream/Rendering/Effects/FxEventBridge.cs`
- `dream/Rendering/Effects/SkillCastEvent.cs` (new)
- `dream.Tests/Rendering/Effects/FxSimulationTests.cs`
- `dream.Tests/Rendering/Effects/FxAllocationTests.cs`
- `dream.Tests/Rendering/Effects/FxSkillCastTests.cs` (new)

Steps:

1. Add `Arc`, `Bolt`, `Ring` to `FxParticleKind` and the three `FxFrame` entries from contract 2.
2. Add `SkillCastEvent.cs` exactly as contract 3 spells it.
3. Add `SpawnSkillCast` plus private `EmitArc`, `EmitBolt`, `EmitMend`:
   - **Arc**: one `Arc` particle at the origin dropped to the feet, `Rotation` from the yaw of `direction` as the
     ground quad needs it, half-size ramping from `0.35f * range` to `0.55f * range`, life 0.25 s; then 14 sparks
     thrown into the forward half-arc (reuse the direction logic in `EmitSpark`, with `direction` in place of
     `away`) and 4 embers.
   - **Bolt**: 12 `Bolt` darts at the origin lifted to chest height, velocity `direction * speed` with a per-dart
     speed spread, so they string out into a lance in flight rather than needing a stateful tracer; life
     `min(range / speed, 0.8f)`; plus 6 sparks at the muzzle. Darts take the existing `Streak` path, so they lie
     along their velocity and stretch with screen-space speed.
   - **Mend**: one `Ring` particle at the feet expanding 0.3 -> 0.9 m over 0.6 s, plus 14 embers rising from the
     feet to above head height with a positive `Gravity` and a `Sway`, tinted bone-green.
4. `WriteParticle`: add the three cases. `Arc` and `Ring` set `stretch = FxInstance.GroundAligned` and a small
   `depthBias` (0.4f, the held-light value) so the floor does not cut them; `Bolt` takes the `Spark` streak branch
   with its own fade curve. Keep the existing `nearStart` / `nearSpan` near-lens fade.
5. `WriteInstances`: the first (underlay) pass currently selects `Kind == Ash`. Widen it to one private
   `static bool IsUnderlay(FxParticleKind)` returning true for `Ash`, `Arc` and `Ring`, so the ground quads draw
   under their own sparks instead of popping across them. The second pass takes the complement. Do not change the
   halo section or the return contract.
6. `FxEventBridge`: subscribe to `SkillCastEvent`, forward to `SpawnSkillCast`, dispose the subscription with the
   others. Do **not** trigger an `FxFlashList` light for a cast in this pass: the flash list has 4 slots and
   `Radius` / `Life` tables the hit and death paths tune, so a fifth kind is a separate change.
7. Tests: distinct-frame and distinct-count assertions per kind; determinism (two simulations with the same seed
   produce byte-identical instances after the same cast); a bounded-spawn test (one cast adds at most 19 particles);
   a `WriteInstances` test that the ground quads are written before non-underlay particles; and in
   `FxAllocationTests`, `SpawnSkillCast` for all three kinds on a full pool inside `AllocationAssert.AtMost(0, ...)`.

Hard constraints for this workstream:

- **Zero allocations per frame.** `SpawnSkillCast`, `Advance` and `WriteInstances` must stay allocation-free: no
  LINQ, no `params` arrays, no lambdas or closures, no collection expressions, no `List<T>` or array creation, no
  boxing (including an interface-typed local over a struct), no eager string construction (not even in a log or an
  exception message), no iterator methods. Pass structs by `in` / `ref`. Everything new is a struct in the fixed pool.
- **FX never feeds the authoritative simulation.** `FxSimulation` keeps its private `FxRandom`; nothing here reads
  from or writes to anything under `Dream.Simulation` beyond value types it is handed, and nothing publishes an
  event the simulation subscribes to. No new `SceneEventHub` publish from inside the render loop.
- Do not change the tick rate or the pacer.
- No `git stash`: one stash stack is shared across all worktrees.

### C - Wiring: publish a cast event for the local player

Owned files:

- `dream/Client/Replication/ClientSkillCastPublisher.cs` (new)
- `dream/Client/Scenes/PlayScene.Replication.cs`
- `dream.Tests/Client/Replication/ClientSkillCastPublisherTests.cs` (new)

Steps:

1. New static `ClientSkillCastPublisher.Publish(SceneEventHub events, AbilityUsedResult result, Vector3 origin,
   Vector3 forward)`: resolve the skill through `ContentCatalog.Shipped.TryGetSkill(result.Key, ...)`, map
   `SkillKind` to `SkillFxKind` (`MeleeArc` -> `Arc`, `RangedBolt` -> `Bolt`, `SelfHeal` -> `Mend`), take `Range`
   from the definition, and publish one `SkillCastEvent`. An unknown key, an unmapped kind, or a non-finite origin
   or direction publishes nothing and returns false. Mapping on `SkillKind` rather than on the three keys means a
   fourth skill of an existing kind gets its effect for free.
2. In `PlayScene.Replication.cs`, in the existing `ServerActionMessage` branch (around line 280, beside the
   `InventoryChangedResult` / `VendorOpenedResult` / `NpcDialogueResult` arms), add
   `else if (resultTargetsLocalSession && actionMessage.Result.Payload is AbilityUsedResult used)` and call the
   publisher with `_runtime.Player.GetWorldPosition()` and `_runtime.Player.Camera.HorizontalForward`. Only an
   **accepted** result reaches here, so a rejected cast (cooldown, mana, wrong mode) plays nothing, which is the
   behaviour we want and the reason this is not predicted at key-press time.
3. Tests: a plain unit test per skill key on the publisher with a real `SceneEventHub` (accepted key gives one event
   with the right kind and range; unknown key gives none; zero direction gives none). No GL and no server needed.
   If an end-to-end check is wanted, `dream.Tests/Integration/HeadlessPlayScene.cs` can reach `_events` by
   reflection, but the unit test is the required one.

Nothing in this workstream touches the protocol, the server, or `Dream.Simulation`.

## Merge order

**A, then B, then C.**

- A is pure assets plus its own gate and asset test; it depends on nothing and makes the frames exist on disk, so the
  frame indices in B point at real pixels the moment B lands.
- B depends on the frame indices from A (frozen above, so B is written in parallel) and introduces the event type C
  publishes.
- C is three lines in `PlayScene.Replication.cs` plus a small publisher; it cannot compile before `SkillCastEvent`
  from B exists, so it merges last.

No file is owned by two workstreams. The only cross-cutting artefacts are the frozen contracts above.

## How this is tested

Per workstream, before handing back:

- A: `python tools/ui-assets/tests/check_particles.py` and `python tools/ui-assets/tests/check_sources.py` (both
  green, full run, not `--fast`), then
  `dotnet test dream.Tests\dream.Tests.csproj "/p:BaseOutputPath=<abs checkout>\artifacts\verify-bin\" --filter "FullyQualifiedName~Dream.Tests.Rendering.Effects.FxAtlasAssetTests"`.
- B: `--filter "FullyQualifiedName~Dream.Tests.Rendering.Effects"`.
- C: `--filter "FullyQualifiedName~Dream.Tests.Client"` plus
  `--filter "FullyQualifiedName~Dream.Tests.Integration.SkillsLoopbackTests"`.

Build `dream\dream.csproj` once first so the test project can copy the native Bullet files, and keep
`BaseOutputPath` an absolute path inside the checkout that runs the tests. Never redirect `BaseOutputPath` into
another worktree.

On the integration branch: the full suite, at most two consecutive full runs; isolate any flake rather than running
a third time. The exact-zero-byte allocation tests (HUD pair, CubeRestFreezer) flake under a loaded suite - re-run
them alone and tee suite output to a file. Expect the one known red dependency test that is red on master itself.
Arm a stall watchdog beside every suite run, and never run two suites at once.

Visual check is manual: run the client and cast all three skills at night and at noon, and confirm the three read
apart at isometric distance. Do **not** spend the session on the screenshot capture harness; it is broken on current
master.

## Gates and what each new asset touches

- `tools/ui-assets/tests/check_particles.py`: shipped bytes, determinism, frame alpha and emission rules, manifest
  `frames` and `reserved`, the 32 KB PNG budget, and the existence of the preview sheet.
- `tools/ui-assets/tests/check_sources.py`: allowed imports only, no file reads outside `READ_ALLOW`, no fonts,
  network, `exec` or `eval`, no source-game names.
- `check_manifest.py` and `check_art.py` do **not** cover particles (they cover HUD, window and menu), and
  `references/asset-families.md` lists no spec-footprint obligation for this family. So a new particle frame touches
  exactly three things beyond the PNG: the generator, the generated `particles.json` manifest, and the C# catalog
  `FxFrame` in `dream/Rendering/Effects/FxInstance.cs` - plus the expected set in the gate and `FxAtlasAssetTests`.
- Commit the generator, the gate edits, the regenerated PNG, the manifest and the preview sheet together;
  `git status` must show only particle-family files for workstream A.

## Determinism and cost

- FX is client presentation only. It must never feed the authoritative simulation: no FX state is read by anything
  under `Dream.Simulation`, and all jitter comes from the private `FxRandom` of `FxSimulation`, never from the
  simulation RNG.
- `Advance` and `WriteInstances` run on the render thread once per frame and are asserted to allocate **exactly
  zero** bytes (`FxAllocationTests`). That forbids, in every new code path: LINQ, lambdas and closures, `params`
  arrays, collection expressions, `List<T>`, array creation, boxing, eager string construction and iterator methods.
  Use fixed-size fields, `in` / `ref` parameters and plain `for` loops.
- Particles stay bounded by the existing pool: at most 19 added per cast through `TryAdd`, which drops and counts on
  a full pool. No new unbounded list, no per-cast allocation, no new halo or flash slot.

## Open questions

1. **Atlas byte budget.** `particles.png` is 32,241 of 32,768 allowed bytes. If four cells do not fit, the plan
   raises the cap in `check_particles.py` to 40 KB (dimensions, grid and GPU cost unchanged). Confirm that is
   acceptable rather than re-tuning an existing frame.
2. **The bolt passes through walls visually.** The server stops the bolt at a wall, but `AbilityUsedResult` carries
   no impact point, so the client darts fly the full range. Accept for this pass, or add an impact point later
   (a protocol change, which belongs with the replication pass).
3. **Remote visibility.** Deferred by the decision above. Worth scheduling as its own wave with the v22 bump?
4. **Aim source.** The effect uses the client camera horizontal forward, while the server resolves the cast on its
   own ray (`WorldSessionRuntime.Skills.cs:33`; the aim is a direction only and the client currently sends `null`).
   They can differ by a frame of turning. Acceptable, or should `ResolveSkillAim()` start sending the same direction
   the effect draws?
