# Engine Iteration Wave 4: Tick Allocations, Map Sync, Scene Teardown, State Checksum, Payload Gate

Purpose: Record wave 4 of `docs/ledger/plans/2026-10-27-engine-iteration-master-plan.md` and its fix loop.

Status: Integrated on `integrate/engine-iteration` at `2ab3d5b`, plus this record. Verified by automated tests and a perf-harness run. Not pushed and not merged into `master`.

This was the first wave run in continuous mode. The user asked for development to keep going without approval between waves.

## Process

1. **Implementation.** Six implementers ran in parallel from `5b05629`, each owning separate files.
   - **Planned tasks:** MEM-3, GFX-4 and NET-7.
   - **Added from the wave-3 follow-ups:** MEM-9 (Map sync allocations), WIRE-8 (JSON source generation) and WIRE-9 (a hand-written once-gate replacing `Lazy`).
2. **WIRE-8 was not merged.**
   - Source generation in metadata mode left the per-message cost unchanged: 520 B per command encode and 5,040 B per snapshot encode.
   - Decoding through it cost twice as much, so decode stayed on reflection.
   - The only gain was a cheaper one-time contract build (1.55 MB to 383 KB). That did not justify splitting the codec into separate encode and decode option sets, plus a new failure mode.
   - Branch `worktree-agent-a01d840ff2c3dfc44` (`e5240f5`) keeps the work. The follow-up is hand-written converters (WIRE-10).
3. **Integration.** The five merges were clean. One test budget was raised from 744 B to 776 B: GFX-4's four pre-draw fields make the presentation state, which is allocated once per frame, 32 B larger. No allocation was added.
4. **Review and test.**
   - Reviewer: no Must fix, 1 Should fix (checksum coverage) and 7 nits.
   - Tester: 12 cross-task tests. It found one production bug that predates this wave: a session that rejoined was never re-attached.
   - Perf run on the merged build.
5. **Fix loop 1.** Three implementers: the rejoin bug, checksum coverage, and hygiene fixes.

## Changes

| Task | Change |
| --- | --- |
| MEM-3 | Player targets are cached per seat and filled into reused lists. `RemoteSessionPlayerState` is its own target. Queued actions are sorted in place with a total-order comparer. Coalesce and damage scratch is reused. The experience-to-next-level result is cached per actor, keyed on level and experience. `EnemyRegistry` uses loops and removes in place. `EnemyActor.Update` has a `Map` overload. `PlayerSessionRegistry.HasSpawnedRemotePlayer` was added. |
| MEM-9 | `UpdateFracturePiecePositions` gains an overload that takes a caller's set. The piece list is copy-on-write. Rubble and coarse-cube descriptor arrays are rewritten in place; nothing outside `Map` holds them. Arrays move to the new cell on a cell change. The subcell LINQ branch is split out. |
| GFX-4 | `PlayScene.Dispose` disposes `Render`, then `ModelAssets.DisposeAll`, then the feedback systems, runtime and remote client, collecting failures. `Game.OnUnload` disposes the scenes before `base.OnUnload()`, and disposes the local server in its own `finally`. `FramePerformanceSnapshot` gains pre-draw fields and a HUD line. |
| NET-7 | `SimulationStateChecksum` is an order-independent hash with five parts: world, actors (including enemy and combat state), rubble, in-cell cube positions, and mutation sequence. `TwoServerDeterminismIntegrationTests` uses it and names the parts that differ. |
| WIRE-9 | `SharedEncodedPayload` uses a once-gate (`Interlocked` state, a private gate created on demand, monitor wait) instead of `Lazy`. A fault is captured once as an `ExceptionDispatchInfo`. |
| Fix A | `RuntimeAuthoritativeSessionHost.JoinSession` now sends an existing session through `AttachRemoteSession`. On a rejoin, the persisted profile is restored. A secondary that returns gets a new body, a former primary takes the seat back or gets its own seat, and a duplicate join creates nothing. |
| Fix B | The checksum gains in-cell cube positions and the actor fields `InteractionMode`, `CombatStats` and `EnemyBehaviorState`. Non-finite values are pre-mixed. The golden value was re-pinned. |
| Fix C | The payload uses a private gate object and a single stack capture. The perf log line no longer prints `preDraw=` twice. The `OnUnload` steps each collect their failures. |

## Measured Impact

All figures are from `AllocationAssert` (minimum over rounds). Each "before" was measured on the original code.

| Path | Before | After |
| --- | --- | --- |
| Runtime tick, 3 sessions (MEM-3) | 1,806 B | 30 B (in `Player.CheckAndLogCollisions`) |
| Tick plus snapshot export (MEM-3) | 4,584 B | 616 B (584 B is the wire payload) |
| `TryResolveCombatActor` (MEM-3) | 2,824 B | 0 |
| Runtime tick, 2 sessions (`DedicatedServerTickAllocationTests`) | 1,361 B | 55 B |
| `EnemyRegistry` client path (MEM-3) | 352 B | 0 |
| Resting rubble, `UpdateFracturePiecePositions` (MEM-9) | 1,496 B | 0 |
| Whole `PhysicsSyncSystem` sync with rubble (MEM-9) | 1,008 B budget | 0 |
| Client cube-move apply (MEM-9) | 832 B | 400 B (all in `WorldPhysicsBinder`) |
| Shared payload create and read (WIRE-9) | 216 B | 96 B |
| Idle host tick in the long-run churn test | n/a | 0 B at ticks 150, 450, 700 and 1000 |

## Verification

- `dotnet build dream/dream.csproj`: 0 errors.
- `dotnet test dream.Tests/dream.Tests.csproj`: 1,391 of 1,391 passed in two full runs, with 0 skipped. The tester's flakiness runs (4 full runs plus filters) found no intermittent failures.
- **Long-run churn:** two servers run 1,000 ticks with 4 sessions. The run includes leaving, rejoining, the primary seat moving, wolf deaths, 4 fractures and rubble settling. The two servers match on every tick under the stronger checksum, and map invariants stay empty.
- **No aliasing:** over 240 frames of moving rubble, earlier presentation exports were never changed.
- **Perf run** (`logs/perf-run-20261028-004358`):
  - Both clients held 144 FPS, with frame medians of 0.26 and 0.51 ms.
  - The HUD pre-draw line read 0.01 ms.
  - Server tick median was 0.06 ms, with one tick per loop.
  - No errors.
- **Not verified:**
  - Teardown in the running game. The harness kills client processes, so `OnUnload` never runs.
  - Interactive play.

## Known Limits And Follow-ups

- `WorldPhysicsBinder` allocates closures, a boxed `UserObject` and lists on every synced cube move: 200–552 B, all of what is left on the client replication path (MEM-10).
- `Player.CheckAndLogCollisions` allocates every 0.5 s per player. This is all of what is left in the runtime tick (MEM-11).
- The client command encode is still 520 B inside System.Text.Json. Next step: hand-written converters and `[JsonIgnore]` on derived properties (WIRE-10).
- The checksum keys cubes by cell, because `Map` has no public way to look up a cube's replicated id.
- All hosts use the public default seed (NET-8).
- Wolves share one animated model, so they show the same clip (GFX-9).
