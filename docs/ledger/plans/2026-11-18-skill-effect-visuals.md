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

---

# Wave 2 (2026-11-18): remote cast visibility (protocol v22)

- **Purpose**: make a peer's `skill.cleave` / `skill.bolt` / `skill.mend` cast visible on every other
  client, by replicating the cast as a presentation-only snapshot delta.
- **Status**: execution-ready plan. Not started. Base: `master` at `2b34af6f`, suite 9276/0.
- **Scope**: one new wire delta, one protocol version bump (21 -> 22), one server queue, one client
  applier. **No new effect, no FX change, no simulation change.** Wave 1's `SkillCastEvent` ->
  `FxEventBridge` -> `SpawnSkillCast` chain is reused unchanged; this wave only adds a second
  publisher of the same event. Nothing under `dream/Rendering/Effects/` is touched.

## Decisions, with evidence

### 1. Coalescing: cast events accumulate, capped in the buffer, and `DropDeltas` clears them

`ServerSnapshotCoalesceBuffer` appends mutations and damage events from every merged tick
(`ServerSnapshotCoalesceBuffer.cs:40-41`, `:61-62`) and `ToEnvelope` hands both lists back (`:88-93`).
Cast events **accumulate the same way**. Dropping them would mean a client that lags by even two
ticks silently misses peers' casts - exactly the client that most needs to see what is being thrown at
it, and an invisible failure no test would catch.

The cap is **not** `MaxCoalescedWorldMutations`-shaped. That cap raises an overflow flag in
`ServerConnection.AppendCoalesced` (`ServerConnection.cs:585-595`) which drops every delta and asks for a
**full world resync** (`DedicatedServerService.ExceedsDeltaCaps`, `:717-721`). Forcing a world resync
because too many sparkles queued up would be a cosmetic feature spending the most expensive operation the
server has. So:

- Add `public const int MaxCoalescedCastEvents = 1024;` **inside `ServerSnapshotCoalesceBuffer`**, and
  truncate on append: once the list holds that many, further cast events are dropped silently. No
  overflow flag, no resync.
- **Do not** add a cast term to `ServerConnection.AppendCoalesced`'s overflow expression
  (`:587-589`) and **do not** add one to `DedicatedServerService.ExceedsDeltaCaps` (`:719-720`). A
  cast event never triggers a resync.
- **Do not** add a `MaxCoalescedCastEvents` option to `ServerConnectionOptions`; a buffer const avoids
  touching the options-plumbing tests for a value no operator needs to tune.

`DropDeltas()` (`ServerSnapshotCoalesceBuffer.cs:69-79`) **must clear the cast list too**, alongside
the mutations and damage events. Reason: `DropDeltas` means "this connection's backlog is being
abandoned and replaced by a full world". The queued casts are by then seconds stale and their actors may
not even exist in the world the client is about to receive. Unlike `WorldChecksum`, nothing needs
invalidating - cast events carry no sequence and are not covered by `WorldStateChecksum` - so the clear is
the whole change. `Reset()` must clear them as well (`:98-105`).

Three sibling sites must stay in step:

- `ServerSnapshotEnvelope.TryCoalesce` (`ServerSnapshotEnvelope.cs:59-64`) concatenates the cast lists,
  because the buffer's contract is "returns exactly what chaining `TryCoalesce` returns" (`:3-8`) and
  `Net2CoalesceChecksumTests.MergedDrop_AgreesWithTheUnmergedWithoutDeltasPath` pins it. The spread
  `[.. a, .. b]` throws on a null operand, so use a null-tolerant concat helper that returns **null**
  when both sides are null or empty.
- `ServerConnection.WithoutDeltas` (`ServerConnection.cs:319-331`) sets `CastEvents = null` (not
  `Array.Empty`, see the empty-is-absent rule below), for the same reason `DropDeltas` clears them.
- `ServerConnection.HasDeltas` (`:334-339`) **does not** gain a cast term. It answers "is a resync owed
  for what `WithoutDeltas` would drop", and a dropped cast owes no resync; adding casts would make a
  cast-only snapshot with a large actor frame trip the oversized-strip path at `:1041`. Note the
  asymmetry deliberately in a comment: `WithoutDeltas` clears casts, `HasDeltas` ignores them.

### 2. The null guard: `CastEvents` does **not** join `RejectMalformedSnapshot`

`RemoteClientSession.RejectMalformedSnapshot` (`dream/Client/Networking/RemoteClientSession.cs:624-630`,
called at `:673`) ends the session as a protocol error when `LastAcceptedCommandSequences`,
`WorldMutations` or `DamageEvents` decoded to null. **`CastEvents` must not be added to that list, and
`RemoteClientSession.cs` must not be edited by this wave at all.**

Why: those three are load-bearing. A missing sequence map stalls command acknowledgement, missing
mutations leave a divergent world, missing damage events leave wrong health - each would otherwise throw a
`NullReferenceException` on the frame thread, and the guard exists to convert that into a clean protocol
error (`:669-672`). A missing cast list costs exactly one sparkle. Turning a cosmetic omission into a
disconnect is a strictly worse failure than the feature it protects.

**Absent and empty mean the same thing: "no casts in this interval."** That is a frozen part of the
contract, not an implementation detail:

- `ClientSnapshotCastApplier` reads `snapshot.CastEvents` through an `is { } casts` pattern and returns
  without doing anything when it is null.
- The **server writes null (the property is omitted) whenever there are no casts** - see the wire
  contract. This is deliberately the common case, so the null path is exercised on essentially every
  tick in production and can never become a surprise, and so every existing snapshot golden fixture
  changes only in its version digit.

### 3. Decoder parity: a trailing optional positional parameter, and the parity test only grows

`ServerSnapshotConverterTests.AssertDecodesLikeReference` (`ServerSnapshotConverterTests.cs:550-563`)
re-serializes both the reference decode and the hand-written decode with `ProtocolWireOptions.V3` and
compares the strings, then asserts null-parity per collection.
`HandWrittenPayloads_ReorderedExtraMissingAndDuplicated_Decode` feeds it the empty object, a snapshot
with an empty body, and an all-null payload. **Do not change either test's intent.** The field is added
so that they pass untouched:

- Declare it as the **last** positional parameter of `ServerSnapshotEnvelope`, after
  `WorldChecksumSequence`, **with a default of null**:
  `IReadOnlyList<CastEventDelta>? CastEvents = null`. Trailing plus defaulted means every existing
  positional `new ServerSnapshotEnvelope(...)` in `dream/` and `dream.Tests/` still compiles unchanged.
- The reflection reference writes properties in declaration order, so `castEvents` lands last, after
  `worldChecksumSequence`. `ServerSnapshotEnvelopeJsonConverter.WriteEnvelope` must write it in exactly
  that position - after the `WorldChecksumSequence` number at `ServerSnapshotJsonConverters.cs:279`,
  before `WriteEndObject`. Byte identity with the reference
  (`RandomSnapshots_AreByteIdenticalToReference`) then holds.
- `ProtocolWireOptions.V2` sets `DefaultIgnoreCondition = WhenWritingNull`
  (`ProtocolWireOptions.cs:110`), so a null list is omitted by the reference and must be omitted by the
  converter: guard with `if (value.CastEvents is IReadOnlyList<CastEventDelta> casts)`, exactly like
  `DamageEvents` at `:262`.
- Read side: a `castEvents` property whose token is Null decodes to null, anything else to a sized
  array - the shape of `:168-172` - and the value is passed as `castEvents` to the constructor. An absent
  property leaves it null, which is what the reference produces, so the three degenerate payloads keep
  decoding.
- `AssertDecodesLikeReference` gains **one added line**,
  `Assert.Equal(expected.Snapshot.CastEvents is null, actual.Snapshot.CastEvents is null);`, which
  strengthens it. Nothing existing is relaxed.
- `ProtocolWireOptions.AddActorFrames` (`:58-93`) needs no change: it only reorders actors/actorFrame.

### 4. Own-cast duplication is suppressed on the **client**, by actor id

The caster already draws its own effect from the unicast `AbilityUsedResult`
(`PlayScene.Replication.cs:290-300`). The server cannot omit the cast from the caster's own snapshot:
snapshot payloads are a **shared broadcast body encoded once for all connections**
(`ServerSnapshotMessage.SharedBody` / `SharedSnapshotEncodeScope.Applies`,
`ServerSnapshotJsonConverters.cs:65-80`; the per-connection part is only the envelope header ack,
`TransportEnvelope.cs:79-82`). Per-recipient filtering of the body would force a per-connection encode of
every snapshot - the exact cost SCALE-2 was built to remove.

So the client skips cast events whose `ActorId` equals its own server actor id. `PlayScene` already
tracks it: `_serverLocalActorId`, set from the snapshot in `ObserveLocalDeath`
(`PlayScene.Replication.cs:317`, field at `PlayScene.Economy.cs:29`). Pass **`_serverLocalActorId`
directly, not the `LocalActorId` property** (`PlayScene.Economy.cs:31`): that property falls back to the
*local* `_runtime.Player.Combatant.Actor.Id`, which could collide with a real server actor id before the
first snapshot names the local player and wrongly swallow a peer's cast. Zero means "unknown - suppress
nothing".

Call the applier **immediately after `ObserveLocalDeath(...)`** in the snapshot branch
(`PlayScene.Replication.cs:244`) so it sees this snapshot's id.

Residual risk, accepted: the server reuses a player actor id once its session leaves, so a reused id could
suppress one peer cast. `ClientSnapshotDamageApplier.IsAttributable` guards against this for damage
(`ClientSnapshotDamageApplier.cs:97-123`) because a misattributed number appears on the wrong player.
A missed cast effect has no wrong place to appear - the local effect is always drawn at the local player
from the unicast path - so the full session-history machinery is not worth its cost here. Do not copy it.

### 5. Hit effects: out of scope, and a real gap was found - route to the user

Checked, not assumed:

- Enemy hits **already replicate**. `DamageEventDelta` goes in every snapshot
  (`WorldSessionRuntime.Replication.cs:325-346`), `ClientSnapshotDamageApplier` publishes
  `DamageAppliedEvent` / `ActorDiedEvent` (`ClientSnapshotDamageApplier.cs:51`, `:61`), and
  `FxEventBridge` turns those into `SpawnHit` / `SpawnDeath` (`FxEventBridge.cs:22-23`, `:34-50`,
  `:58-63`). Every client sees hits on every enemy. **This wave needs no hit work.**
- **But hits on remote *players* are invisible today.** The applier can only draw a hit it can anchor,
  and `ClientWorldSession.TryResolveCombatActor` resolves exactly two things: the local player and the
  enemy registry (`dream/Client/World/ClientWorldSession.cs:92-101`). `RemoteEnemyStateApplier` only
  admits `CreatureKind.Enemy` actors (`RemoteEnemyStateApplier.cs:111`), so a remote *player* is never in
  that registry and every damage event naming one is silently dropped at
  `ClientSnapshotDamageApplier.cs:44-47`. Peers take damage with no splat and no number.

  This is a pre-existing gap, not one this wave creates, and fixing it means giving the client a position
  source for remote player actors - a different change from cast replication. **Open question 1 below;
  not built here.** It is also why this wave puts the cast origin on the wire (next section): there is
  demonstrably no client-side way to resolve a remote player's position today.

### 6. Determinism and cost

- **Nothing added feeds the authoritative simulation.** The pending cast list is an export-only list
  beside `_pendingDamageEvents` (`WorldSessionRuntime.Replication.cs:15-16`), drained and cleared on the
  same export (`:55`, `:62-63`). No simulation code reads it, no cast event is sequenced
  (`QueueWorldMutation`'s `LastWorldMutationSequence` is untouched, `:292-296`) and none is covered by
  `WorldStateChecksum`. Tick goldens and lockstep determinism dumps are unaffected because the
  simulation's own state is unchanged.
- **Per-tick bound on the server list**: one event per accepted `skill.use` action per tick, so at most
  one per connected session per tick (cooldowns are >= 64 ticks, `dream/config/content/skills.json`).
  Make the bound unconditional anyway: `private const int MaxPendingCastEvents = 256;` in
  `WorldSessionRuntime.Replication.cs`; past it, `QueueCastEvent` returns without adding. The list is
  cleared every export, so this is a per-tick cap.
- **Per-snapshot bound on the client**: `ClientSnapshotCastApplier` applies at most
  `MaxCastEventsPerSnapshot = 64` events and ignores the rest, so neither a merged backlog nor a hostile
  server can flood the FX pool from one frame.
- **Zero allocations per frame is unaffected.** This wave adds nothing to `FxSimulation`, `Advance` or
  `WriteInstances`, which `FxAllocationTests` pins at exactly 0 bytes. The new applier runs on the
  snapshot-apply path, not the render loop - the same path as `ClientSnapshotDamageApplier`, which
  `dream.Tests/Client/Replication/ApplierAllocationTests.cs` already bounds; write it in that style
  (plain `for`, no LINQ, no closures, `in`/`ref` for structs) and add it to that test.
- Wire cost: **zero bytes on a tick with no cast**, because an empty list is written as absent.

## The frozen wire contract

Frozen before implementation; all three workstreams code against it and none may change it.

### The delta type

New file `dream/Simulation/World/CastEventDelta.cs`, namespace `Dream.Simulation.World`, beside
`DamageEventDelta.cs`:

    public readonly record struct CastEventDelta(
        int ActorId,
        SkillKind Kind,
        Vector3 Origin,
        Vector3 Direction,
        float Range);

`SkillKind` is `Dream.Simulation.Content.SkillKind` (`MeleeArc`, `RangedBolt`, `SelfHeal`) - the
simulation's own enum, serialized as a number exactly like `CreatureKind` in `DamageEventDelta`. The
server must **not** reference `Dream.Rendering.Effects.SkillFxKind`; the client maps.

**`Origin` is on the wire** and is the server's cast origin (`context.CombatRayOrigin`). It is not
resolved client-side, because there is no client-side way to resolve a remote player's position
(decision 5). `Direction` is the server's resolved cast direction, unnormalised; the client normalises
horizontally through the existing helper. `Range` is `skill.Range` as the definition gives it.

No `PrintMembers` override is needed (unlike `DamageEventDelta`): no golden hashes a cast event's
`ToString`.

### The envelope field

`ServerSnapshotEnvelope` gains, as the **last** positional parameter:
`IReadOnlyList<CastEventDelta>? CastEvents = null`.

### JSON

Property name `"castEvents"`, written **last**, after `"worldChecksumSequence"`, omitted entirely when
null. Each entry: actorId, kind, origin, direction, range - camelCase, `Vector3` through the existing
`Vector3JsonConverter`, which `ProtocolWireOptions.V2` already registers (`ProtocolWireOptions.cs:137`)
so the reflection reference agrees byte for byte. Model the hand-written `CastEventDeltaJsonConverter`
on `DamageEventDeltaJsonConverter` in the same file.

### Empty is absent (one rule, everywhere)

A snapshot with no casts carries **no `castEvents` property**. Enforced at four sites, all of which use
null, never `Array.Empty<CastEventDelta>()`:

1. `WorldSessionRuntime` export: null when the pending list is empty, otherwise its copy. (Note this
   differs from the damage-event line at `WorldSessionRuntime.Replication.cs:55`, which emits
   `Array.Empty`; that line stays as it is.)
2. `ServerSnapshotCoalesceBuffer.ToEnvelope`: null when the list is empty.
3. `ServerSnapshotEnvelope.TryCoalesce`: null when both sides are null or empty.
4. `ServerConnection.WithoutDeltas` and `ServerSnapshotCoalesceBuffer.DropDeltas`: null.

Consequence to rely on: **every existing snapshot golden stays byte-identical except its version digit.**

### The client mapping, in one place

`ClientSkillCastPublisher` gains a shared mapper used by both the own-cast and the remote path, so a
fourth skill never drifts between them:

    public static bool TryMap(SkillKind kind, float definitionRange, out SkillFxKind fxKind, out float range);
    public static bool PublishRemote(SceneEventHub events, in CastEventDelta delta);

`TryMap` is the exact switch already in `Publish` (MeleeArc -> Arc with `skill.Range`,
RangedBolt -> Bolt with `skill.Range`, SelfHeal -> Mend with range 0, anything else false). `Publish`
is refactored to call it; its observable behaviour does not change and `ClientSkillCastPublisherTests`
must keep passing untouched.

`PublishRemote` treats the wire as hostile: it returns false (publishing nothing) for a non-finite
Origin, Direction or Range, for an unmapped Kind, and clamps Range into 0..64. A zero direction stays
legal and means "radial burst", as wave 1 defined.

## The v22 bump: all five spellings, with the actual call sites

Current: `TransportEnvelope.CurrentProtocolVersion = 21` (`TransportEnvelope.cs:77`). New value **22**.
Before claiming it, run the check the file itself prescribes (`:74-75`): list the unmerged branches and
`git show <branch>:dream/Multiplayer/Protocol/TransportEnvelope.cs | grep CurrentProtocolVersion` for
each. 20 and 21 collided once; do not let 22 collide.

1. **The constant and its version-history comment.** `TransportEnvelope.cs:77`, plus a new paragraph
   after the v21 block (`:64-70`) in the same voice: v22 adds the optional trailing `"castEvents"` of a
   server snapshot (`CastEventDelta`: caster actor id, skill kind, origin, direction and range), written
   only when a cast happened in the interval, so a tick without a cast is byte-identical to v21 apart
   from the version digit; a v21 client would skip the unknown property and never show a peer's cast, so
   both sides refuse the other.
2. **Raw `"protocolVersion":NN` fixtures** (4 files):
   `dream.Tests/Multiplayer/Protocol/ProtocolV2SlimmingTests.cs` (`:148`, `:199`, `:207`, `:226`),
   `dream.Tests/Multiplayer/Protocol/TransportJsonCodecDecodeTests.cs`
   (`:101`, `:114`, `:131`, `:158`, `:167`, `:173`, `:174`, `:204`, `:213`, `:221`-`:224`),
   `dream.Tests/Multiplayer/Protocol/WorldClockAnchorProtocolTests.cs:22`,
   `dream.Tests/Integration/StoredWorldJoinLoopbackTests.cs` (`:55` comment, `:216`).
3. **Escaped fixtures** - the same key written with backslash-escaped quotes inside a C# string. A
   pattern anchored on an unescaped quote does **not** match these. 8 files:
   `dream.Tests/Integration/ProtocolV2SharedSnapshotLoopbackIntegrationTests.cs:216`,
   `dream.Tests/Integration/StoredWorldJoinLoopbackTests.cs:216`,
   `dream.Tests/Integration/WaveBTesterWorldsLoopbackTests.cs:337`,
   `dream.Tests/Multiplayer/Protocol/ClientJoinRequestCodecTests.cs:28,65`,
   `dream.Tests/Multiplayer/Protocol/DamageEventHitWireTests.cs:35,58`,
   `dream.Tests/Multiplayer/Protocol/JoinAcceptedWorldDescriptorCodecTests.cs:29,71,101,102`,
   `dream.Tests/Multiplayer/Protocol/M4ContractCodecTests.cs:117`,
   `dream.Tests/Multiplayer/Protocol/ProtocolV2SlimmingTests.cs:134`.
4. **The message strings.** Producers are interpolated from the constant and need no edit:
   `dream/Multiplayer/Protocol/ProtocolVersionMismatchException.cs:28,38` ("this client uses vNN." /
   "this server uses vNN.") and `dream/Multiplayer/Protocol/TransportJsonCodec.cs:175,378`
   ("Supported version: NN."). The **literals that must change**:
   `dream.Tests/Integration/ProtocolVersionMismatchClientTests.cs:34,51,68,87,90,107,110,127,130,139,174,196`,
   `dream.Tests/Integration/ProtocolV2VersionRejectionLoopbackIntegrationTests.cs:53,101`,
   `dream.Tests/Integration/WaveBTesterWorldsLoopbackTests.cs:340`,
   `dream.Tests/Multiplayer/Protocol/JoinAcceptedWorldDescriptorCodecTests.cs:74,78,81`,
   `dream.Tests/Multiplayer/Protocol/ProtocolV2SlimmingTests.cs:253,264`,
   `dream.Tests/Multiplayer/Protocol/TransportJsonCodecDecodeTests.cs:142,151,189`,
   `dream.Tests/Multiplayer/Protocol/TransportJsonCodecEncodeTests.cs:239,247`,
   `dream.Tests/Multiplayer/Protocol/PlayerInventorySnapshotConverterTests.cs:422`,
   `dream.Tests/BugHunt/RaceConcurrencyTests.cs:139` ("Server uses protocol v20; this client uses v21."
   - **both** digits move, to v21 and v22, because it is a one-below/current pair).
5. **`ProtocolVersionMismatchClientTests`' fake newer server must stay exactly one above the current
   version**: `dream.Tests/Integration/ProtocolVersionMismatchClientTests.cs:51` reads "Server uses
   protocol v22; this client uses v21." and becomes "Server uses protocol v23; this client uses v22.";
   the fake server's version number in that test moves 22 -> 23 with it.

**The silent trap.** Four sites derive a downgraded frame with a `.Replace` whose search string pins the
*current* version. If the constant moves and these are left at 21 the replace no-ops, the frame is never
downgraded, and the test passes while verifying nothing:

- `dream.Tests/Integration/StoredWorldJoinLoopbackTests.cs:216` (and its comment at `:55`, which already
  warns about exactly this)
- `dream.Tests/Integration/WaveBTesterWorldsLoopbackTests.cs:337`
- `dream.Tests/Multiplayer/Protocol/JoinAcceptedWorldDescriptorCodecTests.cs:71`
- `dream.Tests/Multiplayer/Protocol/JoinAcceptedWorldDescriptorCodecTests.cs:102` - **reversed**: it
  replaces 6 *with* the current version, so the replacement target moves 21 -> 22.

After the bump, run both of these and fix every hit. Exclude `.claude/worktrees` from any grep: it holds
stale copies of every one of these files.

    grep -rn 'Replace("' --include=*.cs dream dream.Tests | grep protocolVersion
    grep -rn 'protocolVersion.\{0,3\}:21\|uses v21\|version: 21\|version 21' --include=*.cs dream dream.Tests

Both must come back empty of stale 21s (deliberate historical values such as the v1..v12 legacy cases
stay). A green suite is **not** evidence that step 5 or the trap was handled; the grep output is.

## Workstreams

Three, with disjoint owned files. The wire contract above is frozen, so all three can be written in
parallel; only compilation ordering forces the merge order.

### W1 - Wire: the delta, the envelope field, the converters and the v22 bump

Owned files:

- `dream/Simulation/World/CastEventDelta.cs` (new)
- `dream/Simulation/World/ServerSnapshotEnvelope.cs`
- `dream/Multiplayer/Protocol/ServerSnapshotJsonConverters.cs`
- `dream/Multiplayer/Protocol/TransportEnvelope.cs`
- `dream.Tests/Multiplayer/Protocol/ServerSnapshotConverterTests.cs`
- `dream.Tests/Multiplayer/Protocol/ServerSnapshotSamples.cs`
- `dream.Tests/Multiplayer/Protocol/CastEventWireTests.cs` (new)
- every bump fixture listed in spellings 2-5 above, namely in
  `dream.Tests/Multiplayer/Protocol/`: ProtocolV2SlimmingTests, TransportJsonCodecDecodeTests,
  TransportJsonCodecEncodeTests, WorldClockAnchorProtocolTests, ClientJoinRequestCodecTests,
  DamageEventHitWireTests, JoinAcceptedWorldDescriptorCodecTests, M4ContractCodecTests,
  PlayerInventorySnapshotConverterTests; in `dream.Tests/Integration/`:
  ProtocolVersionMismatchClientTests, ProtocolV2VersionRejectionLoopbackIntegrationTests,
  ProtocolV2SharedSnapshotLoopbackIntegrationTests, StoredWorldJoinLoopbackTests,
  WaveBTesterWorldsLoopbackTests; and `dream.Tests/BugHunt/RaceConcurrencyTests.cs`.

Steps:

1. Add `CastEventDelta.cs` exactly as frozen.
2. Add the trailing defaulted `CastEvents` parameter to `ServerSnapshotEnvelope`, and the null-tolerant
   concat in `TryCoalesce` (returning null when both sides are empty).
3. Add `CastEventDeltaJsonConverter` beside `DamageEventDeltaJsonConverter`, the `castEvents`
   `JsonEncodedText`, the read arm and the write block (last, guarded on non-null), and extend the wire
   shape doc comment at `ServerSnapshotJsonConverters.cs:18-32`.
4. Bump to 22 and chase all five spellings plus the four `.Replace` sites; run both greps above.
5. Tests: extend `ServerSnapshotSamples.SnapshotWith` with a casts parameter; add the one null-parity
   line to `AssertDecodesLikeReference`; new `CastEventWireTests` covering byte identity against the
   reference for null / empty / one / many / edge-value cast lists, round-trip, a cast-free snapshot
   containing no `castEvents` substring, and property reordering and duplication.

**Do not touch** `dream/Client/Networking/RemoteClientSession.cs` (decision 2),
`dream/Server/ServerConnection.cs` (W2 owns it), or anything under `dream/Rendering/`.

### W2 - Server: queue the cast, export it, coalesce it

Owned files:

- `dream/Simulation/Runtime/WorldSessionRuntime.Replication.cs`
- `dream/Simulation/Runtime/WorldSessionRuntime.Skills.cs`
- `dream/Simulation/World/ServerSnapshotCoalesceBuffer.cs`
- `dream/Server/ServerConnection.cs` (`WithoutDeltas` only; `HasDeltas` and `AppendCoalesced` stay as
  they are)
- `dream.Tests/BugHunt/Net2CoalesceChecksumTests.cs`
- `dream.Tests/Simulation/Runtime/CastEventExportTests.cs` (new)

Steps:

1. `WorldSessionRuntime.Replication.cs`: add the pending cast list beside `_pendingDamageEvents`
   (`:15-16`), `MaxPendingCastEvents = 256`, an internal `QueueCastEvent(in CastEventDelta)` that
   returns early at the cap, the export line (null when empty) in the snapshot build beside `:55`, and
   the Clear beside `:62-63`.
2. `WorldSessionRuntime.Skills.cs`: after `_combatResolver.ProcessSkillUse(...)` returns (`:78-84`),
   queue one event when the result is accepted - `context.Combatant.Actor.Id`, `skill.Kind`,
   `context.CombatRayOrigin`, the resolved direction, `skill.Range`. Nothing is queued for a
   rejection: mana and cooldown have already been spent by this point, so queue strictly on acceptance,
   which matches the own-cast rule (a rejected cast plays nothing).
3. `ServerSnapshotCoalesceBuffer.cs`: the cast list, `public const int MaxCoalescedCastEvents = 1024`,
   a `CastEventCount` property, truncating append in Begin and Append, inclusion in `ToEnvelope` (null
   when empty), clearing in `DropDeltas` and `Reset`.
4. `ServerConnection.WithoutDeltas`: set `CastEvents` to null. Leave `HasDeltas` alone and comment why.
5. Tests: a cast on a tick appears once in the export and the list is empty on the next tick; a rejected
   cast queues nothing; the per-tick cap holds; a merged pair keeps both ticks' casts in order; the
   truncation cap holds and raises no overflow and no resync; `DropDeltas` clears casts and
   `MergedDrop_AgreesWithTheUnmergedWithoutDeltasPath` still passes (extend it with a cast assertion);
   buffer `ToEnvelope` still equals chained `TryCoalesce` including casts.

### W3 - Client: apply remote casts, suppress the local one

Owned files:

- `dream/Client/Replication/ClientSnapshotCastApplier.cs` (new)
- `dream/Client/Replication/ClientSkillCastPublisher.cs`
- `dream/Client/Scenes/PlayScene.Replication.cs`
- `dream.Tests/Client/Replication/ClientSnapshotCastApplierTests.cs` (new)
- `dream.Tests/Client/Replication/ClientSkillCastPublisherTests.cs`
- `dream.Tests/Client/Replication/ApplierAllocationTests.cs`

Steps:

1. `ClientSkillCastPublisher`: extract `TryMap`, add `PublishRemote` as frozen. `Publish` keeps its
   behaviour exactly; its existing tests must not need edits.
2. New `ClientSnapshotCastApplier` with a single static
   `ApplyToRuntime(SceneEventHub events, in ServerSnapshotEnvelope snapshot, int localActorId)`: it
   returns on a null `CastEvents`, iterates with a plain for loop up to
   `MaxCastEventsPerSnapshot = 64`, skips entries whose `ActorId` equals a non-zero `localActorId`, and
   calls `ClientSkillCastPublisher.PublishRemote`. No LINQ, no closures, no allocation.
3. `PlayScene.Replication.cs`: one call immediately after `ObserveLocalDeath(...)` (`:244`), passing
   `_serverLocalActorId`.
4. Tests: a remote cast publishes exactly one `SkillCastEvent` of the right kind, origin, direction and
   range on a real `SceneEventHub`; the local actor's own cast publishes nothing; a zero `localActorId`
   suppresses nothing; a null and an empty list both publish nothing; non-finite origin or direction, an
   out-of-range Range and an unmapped `SkillKind` publish nothing; 100 events publish at most 64; and an
   `AllocationAssert.AtMost(0, ...)` case in `ApplierAllocationTests`.

**Do not touch** `dream/Client/Networking/RemoteClientSession.cs` or anything under
`dream/Rendering/Effects/`.

## Merge order

**W1 (Wire) -> W2 (Server) -> W3 (Client).**

- W1 first: `CastEventDelta` and `ServerSnapshotEnvelope.CastEvents` are the types the other two compile
  against, and the v22 bump touches around twenty test files that nobody else owns - landing it alone
  keeps the bump's blast radius readable in one diff.
- W2 second: it fills the field W1 declared, and its coalescing tests only mean something once the field
  exists.
- W3 last: it consumes what W2 produces, and its one `PlayScene` line is the smallest, least
  merge-prone edit. The feature is only observable after W3, so verify end to end on the integration
  branch, not on W3's branch alone.

No file is owned twice. `ServerConnection.cs` is W2's alone; `RemoteClientSession.cs` is nobody's.

## How this is tested

Per workstream, before handing back (build `dream\dream.csproj` once first so the test project can copy
the native Bullet files; keep `BaseOutputPath` an absolute path inside the worktree that runs the tests -
never redirected into another worktree):

- W1: filter `FullyQualifiedName~Dream.Tests.Multiplayer.Protocol`, then
  `FullyQualifiedName~Dream.Tests.Integration.Protocol`. Then re-run the two greps from the bump section
  and paste the output into the hand-back.
- W2: filters `FullyQualifiedName~Dream.Tests.BugHunt.Net2Coalesce`,
  `FullyQualifiedName~Dream.Tests.Simulation.Runtime` and `FullyQualifiedName~Dream.Tests.Server`.
- W3: filter `FullyQualifiedName~Dream.Tests.Client.Replication`.

On the integration branch: the full suite, at most two consecutive full runs; isolate any flake rather
than running a third time. Expect the one dependency test that is red on master itself. The exact-zero-byte
allocation tests flake under a loaded suite - re-run them alone and tee suite output to a file. Arm a
stall watchdog beside every run; never run two suites at once. No `git stash` in any worktree: the stash
stack is shared across all of them.

End-to-end check, which no unit test covers: run two clients against one host, cast each of the three
skills on client A, and confirm client B draws the effect at A's figure and that A draws it exactly once.
A v21 client against a v22 server must show the version-mismatch message, not a decode error.

## Open questions

1. **Hits on remote players are invisible today** (decision 5): `TryResolveCombatActor` cannot resolve a
   remote player, so every damage event naming one is dropped at `ClientSnapshotDamageApplier.cs:44-47`.
   Peers take damage with no splat and no number. Pre-existing, not caused by this wave, and a separate
   change (it needs a position source for remote player actors). Schedule it as its own wave?
2. **Origin on the wire vs. the drawn figure.** The replicated origin is the server's `CombatRayOrigin`
   at the cast tick, while the peer's figure is drawn at an interpolated client position a few frames
   behind. At isometric distance the offset should be under a figure's width; accept, or anchor the
   effect to the rendered actor once a position source exists (same prerequisite as question 1)?
3. **Aim source**, carried over from wave 1: the local effect uses the client camera's horizontal forward
   while the replicated one uses the server's resolved direction, so the caster and a peer can see
   slightly different arcs. Acceptable, or should the client start sending its aim direction
   (`ResolveSkillAim()` currently sends null, `WorldSessionRuntime.Skills.cs:33-43`) so both draw the
   same thing?
