# Engine Iteration Wave 1: Export Cache, Hot Paths, GL Ownership, Determinism, Decoupling, Wire

Purpose: Record the first team iteration of the engine refactoring loop, from the gap selection through wave 1, one fix loop and verification.

Status: Integrated on `integrate/engine-iteration`, verified by automated tests and two perf-harness runs. The perf runs predate fix loop 2, which only touched the render-tick slew, a fault path and the fracture fallback. Not pushed and not merged into `master`.

Plans: `docs/ledger/plans/2026-10-27-static-render-export-caching.md` (task A) and `docs/ledger/plans/2026-10-27-engine-iteration-master-plan.md` (all waves).

## Process

1. **Specialist audits.** Memory, graphics/threading and netcode audits selected one gap. The presentation render export was rebuilt and re-hashed on every client frame.
2. **Task A.** One implementer added the static content cache and the version-based signature.
3. **Analysts.** At the user's request ("split by task, more analysts"), five analysts (MEM, GFX, NET, DEC, WIRE) produced 39 tasks. These were merged into a six-wave master plan, and no two tasks in the same wave touch the same file.
4. **Wave 1.** Fifteen implementers ran in parallel, each in its own worktree, from the shared base `iteration/engine-base`: task A plus the plan and `AllocationAssert`.
5. **Integration and verification.**
   - The integrator merged the 15 branches in plan order. Only one compile fix was needed: a fixture still set the removed `Cube.Animation`.
   - The tester added 19 cross-task integration tests.
   - The reviewer reported 1 Must fix, 4 Should fix and 6 Nit findings.
6. **Fix loop 1.** Three implementers (FIX-A, FIX-B, FIX-C) fixed the Must fix, all four Should fix findings and the actionable nits. A focused re-review found no Must fix and two Should fix findings.
7. **Fix loop 2** (the last one allowed). One implementer fixed both Should fix findings and one nit. Every fix-loop regression test was shown to fail before its fix.

## Changes

| Task | Change |
| --- | --- |
| A | Map caches the static part of the presentation export, keyed by a process-unique `StaticContentVersion`. Each frame only builds the dynamic part. The static chunk signature hashes the version and the static dirty cells instead of every cube and block. |
| GFX-1 | `Shader` is a sealed `IDisposable` with no finalizer, so GL is no longer called from the finalizer thread. Failed compiles and links no longer leak. `RenderThreadAffinity` is bound in `Game` and asserted in Debug. |
| GFX-2 | `AnimatedModel` and `StartupMenuRenderer` disposal is idempotent. `ResourceCache.Clear`/`DisposeAll` and `ModelAssets.DisposeAll` exist, but nothing calls them yet (GFX-4). |
| GFX-5/6 | `BackgroundWork.TryTakeCompleted` and `RenderFaultLog`. A faulted static chunk build is reported and retried with exponential backoff, and it no longer crashes `Draw`. |
| WIRE-1 | `Vector3JsonConverter.Read` reads directly from `Utf8JsonReader`. |
| WIRE-2 | `IMessageFramer.TryReadFrameBounds` and `FrameReceiveBuffer.TryReadPayload`. Both receive loops decode straight from the buffer span. |
| WIRE-3 | `TransportJsonCodec.Decode` is a single `Utf8JsonReader` pass with a payload checkpoint. Validation order and messages are unchanged. |
| NET-2 | `DeterministicRandom` (SplitMix64), `Seed` and `StableHash`. Fracture rubble is a function of the cell, so the server and the client fallback match. The fallback keeps the cell material. |
| NET-3 | `ServerTickClock`, plus remote-player interpolation keyed on server ticks rather than render-frame receive time. Several snapshots drained in one frame no longer collapse to a zero span. |
| DEC-1 | `FloatingCombatTextKind` moved to `Simulation.Combat`. `DependencyDirectionTests` asserts Simulation Combat, Inventory and Physics. |
| DEC-2 | `Player`, `Camera` and `RemoteSessionPlayerState` no longer use windowing or client input. Movement maths moved to `PlayerMovementResolver` and is bit-identical (31,500-case test). |
| DEC-4 | The authoritative runtime no longer builds presentation state. `WorldSessionPresentationState` moved to `Client.Presentation`. |
| DEC-5 | The simulation holds no glTF models. `CubeMaterialModelCatalog` resolves them by material in the renderer, and `Simulation/World` has no Rendering usings. |
| MEM-2 | `PhysicsSyncSystem` uses reusable scratch collections and skips `UserObject` rewrites for resting bodies. |
| MEM-4 | The server host tick pipeline (`ServerTickRunner`, host, service, inbox, action queue, runtime host) avoids LINQ, closures and copies per tick. |
| MEM-8 | Chunk meshing is two-pass and allocates exactly to size, using stackalloc corners and a pooled occluder index. Upload is zero-copy through `CollectionsMarshal`. |
| FIX-A | Static dirty chunks seen during a build backoff are kept for the retry. Model scale is computed once per batch. Mesh-builder pool returns are exception-safe. `ResourceCache.Clear` releases every item. |
| FIX-B | The remote render tick is monotonic, with a rate-slewed `ServerTickClock.RenderTick`. The fallback fracture re-materializes the cube before fracturing, so rubble is built once. |
| FIX-C | The runtime-host allocation test compares the same command path on both sides. |
| Fix loop 2 | `RenderTick` slows rather than freezes while less than 4 ticks ahead of its target, and catches up after hitch frames. A build that returns no batch keeps its dirty chunks for the retry. The fallback re-adds the cube without a physics body before fracturing it. |

## Measured Impact

All figures are bytes per call after warm-up, from `AllocationAssert` or equivalent tests on the startup world (56x56). "Before" was measured on the original code by the same test.

| Path | Before | After |
| --- | --- | --- |
| Presentation export plus static signature, per frame (task A) | 1,939,427 B, 2,027 µs | 672 B, 1.3 µs |
| Physics sync, 49 resting cubes, per tick (MEM-2) | 9,448 B | 0 B |
| Physics sync with rubble, per tick (MEM-2) | 16,872 B | 1,496 B (all inside `Map`) |
| Server host `AdvanceTime`, 2 sessions (MEM-4) | 1,256 B | 0 B |
| `PlayerActionQueue` drain (MEM-4) | 776 B | 0 B |
| `DedicatedServerService.AdvanceOnceAsync`, 2 joined (MEM-4) | 2,976 B | 856 B Release (the remainder is the per-connection snapshot queue) |
| Chunk mesh build, 1,141 faces (MEM-8) | 1,690,863 B | 173,632 B (final storage plus 200 B) |
| Deframe, 100 frames (WIRE-2) | 5,600 B | 0 B |
| Vector3 read, per 1,000 (WIRE-1) | 72,000 B | 0 B |
| Decode of a 24-actor snapshot (WIRE-3; measured end to end by the tester after WIRE-1) | 40,792 B | 16,192 B |

## Perf Harness

Setup: one dedicated server and two auto-connecting desktop clients for 30 s, run with `-NoStopExisting`.

- **Integration branch, machine idle:** `logs/perf-run-20261027-212719` in the integration worktree.
  - Server `advanceMs` median 0.05 ms, max 9.4 ms. Every sampled loop ran exactly one tick.
  - Snapshot about 1,010 B, `sendMs` at most 0.14 ms.
  - Both clients rendered the remote player.
  - No exceptions, render faults, resyncs or invariant reports.
  - The static chunk upload through the new zero-copy path drew 68 to 72 chunks (about 85k triangles) on both clients.
- **Control run on the pre-iteration commit `dfb68db`:** `logs/perf-run-20261027-212829` in the main checkout.

| Metric | Control | Integration |
| --- | --- | --- |
| Focused client median frame | 2.42 ms | 0.78 ms |
| Focused client FPS | 412 | 1,293 |
| Export plus signature, focused client | 0.84 + 0.61 ms | 0.00 + 0.00 ms |
| Unfocused client median frame | 24 ms | 262 ms |
| Unfocused client FPS | 53 | 2 |

**Finding: an uncapped render loop.**
- The game set no frame cap. The focused client ran at about 1,300 FPS and the unfocused client dropped to 2 FPS.
- The user confirmed the unfocused client recovers as soon as its window is focused, so most of its slowdown is unfocused-window behaviour. The uncapped loop next to it made that worse and wasted GPU time.

**Frame cap, added after wave 1 at the user's request.**
- Setting: `max_fps` in `desktop-client.txt`. Default 144; 0 means uncapped; any other value is clamped to 30–500. `DREAM_MAX_FPS` overrides it without persisting.
- It is applied through `GameWindow.UpdateFrequency`, with VSync left off.
- In the single-threaded window, input and the per-frame update also run at most at the cap. Client prediction keeps its own fixed 64 Hz tick.
- Re-run with two clients (`logs/perf-run-20261027-214349` in the integration worktree):
  - Both clients held 144 FPS, including the unfocused one.
  - Median frame work was 0.60 ms (unfocused) and 0.54 ms (focused).
  - Server `advanceMs` median was 0.06 ms, with one tick per loop.
  - Both clients rendered each other, with no exceptions, render faults or resyncs.

An earlier run (`logs/perf-run-20261027-210629`) overlapped with the tester's suite runs and showed the same pattern, plus CPU contention.

## Verification

- `dotnet build dream/dream.csproj` succeeded with 29 warnings, all pre-existing.
- `dotnet test dream.Tests/dream.Tests.csproj` passed 942 of 942 after the frame cap, and 904 of 904 in two full runs after fix loop 2. Before fix loop 2 it passed 901 of 901, three times in a row. The base was 694 and task A's base was 679.
- `MultiClientLoopbackSmokeTests` and `TickPacerTests` passed 5 of 5 isolated runs.
  - Each failed once under full-suite load during implementer runs: a timing `InRange` check and a timing overshoot.
  - Neither failed in the three final full runs. Both are timing-sensitive and remain a known flake risk under heavy parallel load.
- **Acceptance greps:**
  - `Simulation/World` has no `Dream.Rendering.`.
  - `Player.cs`, `Camera.cs` and `RemoteSessionPlayerState.cs` have no `Dream.Client.` or `OpenTK.Windowing`.
  - No type declares a finalizer.
  - `CubeFracturePattern` has no `Random.Shared`.
- **Not verified:**
  - Interactive play by a person.
  - The GL upload path and shader disposal, apart from the harness run.
  - Long sessions.

## Known Limits And Follow-ups

- `Render`'s shaders, the HUD and the chunk renderer are still never disposed (GFX-3/GFX-4). Without the finalizer they now leak until process exit instead of being deleted on the wrong thread.
- `Map.UpdateFracturePiecePositions` and `UpdateCubePosition` still allocate on every physics sync.
- Snapshot encode is still done once per connection, with two serializations (WIRE-4/5).
- Rubble is identical every time the same cell fractures. A per-world seed in the mutation delta is deferred.
- The `loadVisualAssets` parameter is now dead plumbing (DEC-8).
- `PlayScene` still has a write-only `_clientPresentationTimeSeconds` path for loopback orbit and resync cooldown.
