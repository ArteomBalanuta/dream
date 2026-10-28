# Engine Iteration Round 2 Master Plan: Robustness, GPU, Scalability, Architecture

Purpose: Second analysis round after waves 1–6 of `2026-10-27-engine-iteration-master-plan.md`. The user asked for continuous development: keep iterating, and run a new analysis when the plan is finished.
Status: The wave-6 fix loop is done (see `docs/ledger/work-log/2026-10-28-engine-iteration-wave-6.md`). Wave 7 is in progress.

## How This Plan Was Made

Four analysts each audited `integrate/engine-iteration` at `9c8b221` (waves 1–6 merged) in one area not covered before:
- netcode robustness (NETR);
- the GPU and render pipeline (GPU);
- architecture and maintainability (ARCH);
- scalability and persistence (SCALE).

Their task lists, with evidence, are summarized below. The wave-6 reviewer's findings are included too.

Rules are unchanged from the first plan:
- one owner per file per wave;
- every implementer starts from the integration branch;
- allocation claims are measured with `AllocationAssert`;
- the wire stays byte-identical unless a task bumps the protocol;
- never push or merge into master.

Decision taken in continuous mode: NETR-6's "noclip/freeze for every client" gate is user-visible, so it is **deferred** for a user decision. The rest of NETR-6 goes ahead.

## Waves

**Wave 6 fix loop:**
- NET-9: server-side replicated ids in `Map`.
- NET-4: interpolated enemy labels and a gap-scaled respawn threshold.
- Converter/codec test nits.

**Wave 7:**
- **SCALE-6:** metrics and the load harness. It goes first because it measures everything after it.
- **ARCH-1:** test suite under 30 s and free of flakes.
- **R-HYG = ARCH-5 + GPU-8:** warnings, package advisories, ImGui scissor/texture-unit/state fixes. These share `Render.cs` and `AnimatedModel.cs`.
- **ARCH-6:** dead APIs.
- **GPU-1:** static chunk planning off the render thread.
- **GPU-5:** GPU timing, counters, GL debug.
- **NETR-3:** use cadence for every seat (bug: secondaries could build 64 cubes/s) and starvation decay.
- **NETR-6:** input validation, without the noclip gate.

**Wave 8:**
- **SCALE-3:** WAL, group commit, no DB work on the tick at join.
- **NETR-1:** server liveness and bounded coalescing.
- **NETR-4:** per-tick client command stream.
- **GPU-4:** per-actor animation instances (GFX-9).
- **SCALE-5:** physics proportional to active bodies.
- **GPU-2:** frustum culling.

**Wave 9:**
- **ARCH-2:** invert the Simulation→Server dependency.
- **NETR-2:** client disconnect detection, keepalive, and returning to the menu.
- **NETR-5:** server jitter buffer.
- **NETR-7:** directional frame limits and snapshot interval.
- **GPU-7:** terrain shader.
- **SCALE-4:** incremental checkpoints.

**Also open, from the wave-6 fix loop:**
- The server assigns ids to terrain cubes that get hit, but only full snapshots send those ids to clients.
- The placed-cube and enemy id ranges can overlap.

**Wave 10 and later:**
- SCALE-1 (protocol v3: compact and chunked world transfer).
- SCALE-2 (v3: delta actors, per-connection ack).
- ARCH-3 (`WorldSessionRuntime` split).
- ARCH-7 (`Render.cs`/`PlayScene` split).
- ARCH-8 (`Map` split).
- GPU-3 (batched terrain pass).
- GPU-6 (instanced fallback cubes).
- NETR-8 (stale-session takeover).
- SCALE-7 (memory hygiene).
- ARCH-4 (docs drift, after ARCH-2).

Each later wave is re-checked against what has landed before it starts.

## Analyst Findings (summaries; evidence is in the agent reports)

# Round 2 analysis notes (for the wave-7+ master plan)

## Wave-6 reviewer: Should-fix items, which go into the wave-6 fix loop

1. **NET-9 id key unused on the server.**
   - The server never calls `Map.AssignReplicatedCubeId`. Ids are registered only in `_worldObjects.RegisterCube` (`WorldSessionRuntime.cs:1384`).
   - Fix: assign ids in Map on register and restore, and add a test where two runtimes assign ids differently.
2. **Enemy labels use the authoritative position.**
   - `ActorWorldLabelBuilder.cs:59` should use `EnemyRenderer.ResolveRenderPose` with the pose source and render tick, passed through `Render.cs` (`DrawWorldActorLabels`/`DrawHUD`) and `HudRenderer.DrawFrame`.
3. **NET-4 uses a fixed 4 m teleport threshold** (`ClientScenePresenter.cs:20-21, 349-353`).
   - Scale it by the tick gap and the max speed, or have the server send a respawn/teleport generation.
4. **TEST-1 serializes about 43 s of tests.**
   - Keep only `TickPacerTests` and the single 250 ms mesh test (`StaticWorldChunkMeshBuilderTests.cs:189-201`), moved to its own class.
   - Return the loopback, stress and smoke tests to the parallel pool, using progress-based waits.
   - `ClientWorldStateBoundaryIntegrationTests.cs:148` has a fixed 3 s floor.
   - Target: under 30 s.
5. **Nits:**
   - The "cannot collide" comments in `SimulationStateChecksum`.
   - The `KnownMessageKinds` drift test in `TransportJsonCodec`.
   - Docs: the seed override still stores a random seed on a fresh database.
   - The `DDL` doc lacks `world_metadata`.
   - `HandWrittenConverterTests` budgets should tighten to 0 / about 5.5 KB.

## GPU analyst (GPU-1 to GPU-8)

| Task | What | Size |
| --- | --- | --- |
| GPU-1 | Move chunk plan off the render thread (21–24 ms hitches). Neighbour lookup instead of 27-chunk copies. Drop CPU mesh lists after upload. Reuse VBO handles. Byte-based upload budget. Files: `StaticWorldChunkRenderer`, `StaticWorldChunkBuildOutput`/`Input`/`Mesh`, `StaticMeshBuffer`. | L |
| GPU-2 | `ViewFrustum` (Gribb–Hartmann, row-vector). Cull static chunks and dynamic cubes. Fix the `VisibleChunks` stat. Files: new `ViewFrustum`, `StaticWorldChunkRenderer`, `WorldRenderer`. | S–M |
| GPU-3 | Batched static terrain pass sorted by material/role. `StaticTerrainMaterialCatalog.Resolve` allocates path strings per mesh per frame; `ResolveRoles` allocates. Files: `Render`, `StaticWorldChunkRenderer`, `StaticMeshBuffer`, `StaticTerrainMaterialCatalog`. | M |
| GPU-4 | GFX-9 per-actor instances: `AnimatedModelSkeleton` (immutable) + `AnimatedModelInstance`. Joint upload as one `UniformMatrix4` array. `EnemyVisualState` owns the instance. Files: `AnimatedModel`, `AnimatedModelData`, new Instance/Skeleton, `EnemyRenderer`, `EnemyVisualState`(+Cache). Today N wolves advance the clip N×dt. | L |
| GPU-5 | `GpuFrameTimer` (time-elapsed queries), `RenderCounters`, KHR_debug in DEBUG, a per-frame `CheckGLError` in DEBUG, `FramePerformanceSnapshot` fields. Files: new files, `GLUtils`, `Shader`, `FramePerformanceSnapshot`, `PlayScene`, `Game`. | M |
| GPU-6 | Instanced fallback cubes/rubble, `InstanceMatrixBuffer`, `AnimatedModel` instance upload once per node. Files: `WorldRenderer`, `Render`, `StaticMeshBuffer`, `AnimatedModel`. | M |
| GPU-7 | Terrain shader with no per-vertex `inverse()`, a CPU `normalMatrix`, alpha MASK discard. Files: shaders, `Render`, `AnimatedModel`. | M |
| GPU-8 | ImGui: enable `SCISSOR_TEST`, `ActiveTexture(0)` before the font, restore blend/cull state after the HUD, cache uniform locations, RGBA font format. Files: `Render`, `StartupMenuRenderer`. | S |

- **Proposed waves:**
  - A: GPU-1, GPU-4, GPU-5, GPU-8.
  - B: GPU-2, GPU-7.
  - C: GPU-3.
  - D: GPU-6.
- **Shared files:**
  - `StaticWorldChunkRenderer`: GPU-1, GPU-2, GPU-3.
  - `StaticMeshBuffer`: GPU-1, GPU-3, GPU-6.
  - `Render`: GPU-3, GPU-6, GPU-7, GPU-8.
  - `AnimatedModel`: GPU-4, GPU-6, GPU-7.
  - `WorldRenderer`: GPU-2, GPU-6.

## SCALE analyst (SCALE-1 to SCALE-7)

| Task | What | Size |
| --- | --- | --- |
| SCALE-1 | **Critical.** The world snapshot (join/resync) is one frame capped at 1 MiB, so join and resync fail above about 8.2k placed cubes (127 B/cube, 620 B/rubble cell). The export runs on the tick with LINQ. Fix: a compact `WorldSnapshotJsonConverter` (flat int arrays, quantized offsets, about 25 B/cube) plus chunked `server.world-chunk` transfer and client reassembly. Protocol v3. Files: runtime `ExportWorldSnapshot`, `WorldSnapshot`, protocol converters/codec/envelope, `JoinAccepted`, `ServerConnection`, `RuntimeAuthoritativeSessionHost`, `RemoteClientSession`, `ClientJoinAcceptedApplier`. | L |
| SCALE-2 | Delta actor snapshots (WIRE-7): change bitmask per field group, quantized transforms, keyframe every 64 ticks. The per-connection ack moves into the envelope header (removes O(P²)). 32p+30e is about 20 KB/tick, 1.3 MB/s per client. Protocol v3. The client `ActorStateCache` reconstitutes full actors. | L |
| SCALE-3 | SQLite runs in DELETE journal mode with FULL sync and one autocommit per write (about 10 fsyncs per fracture). Fix: WAL + `synchronous=NORMAL` + `busy_timeout`, group commit in `PersistenceWriteQueue` (savepoint per write), and no `Flush(5s)` or DB reads on the tick at join (deferred join now runs on the tick, so the comment is stale). Files: `SqliteSchemaBootstrapper`, `PersistenceWriteQueue`, repositories, `SqlitePersistenceCheckpointWriter`, runtime `RestorePersistedPlayerState`, `DedicatedServerHost` (prefetch). | M |
| SCALE-4 | Incremental checkpoints. Today a full capture every 5 s on the tick: about 1.7 MB garbage at 10k cubes, and every row is updated. Fix: dirty capture plus merge in `BackgroundCheckpointWriter`, with a periodic full sweep. Files: runtime capture, `WorldObjectRegistry`, `Map` (`CaptureFracturePiecesInto`), checkpoint types and writers. Depends on SCALE-3. | M |
| SCALE-5 | Physics is O(all bodies): sync walks sleeping bodies, body lists use O(N) `Remove`, `_solidCubes.Remove` is O(N), and `RemoveCompatibilityCubeBodies` does a full scan per fracture. Fix: skip inactive bodies, indexed sets with swap-remove (preserving determinism order), reverse maps. Files: `PhysicsSyncSystem`, `PhysicsIntegrator`, `WorldPhysicsBinder`, `Map`. | M |
| SCALE-6 | **First.** Always-on `ServerTickMetrics` (histogram p50/p95/p99, dropped ticks, checkpoint ms, queue depth), `ServerConnection` byte counters, and a headless load harness `dream.Tests/Load` (BotClient over `RemoteClientSession`, `WorldSeeder`, `LoadRunner`). Scenarios Smoke8, Players32, World10k and Soak; `[LoadFact]` gated by `DREAM_LOAD_TEST=1`. | M |
| SCALE-7 | Memory hygiene: `PlayerSessionRegistry` never shrinks (a new GUID per launch, walked twice per tick), rate-limiter keys are never removed, the server keeps render-dirty sets and descriptors it never uses (headless map flag), incremental checksum. | M |

- **Suggested waves:**
  - S1: SCALE-6, SCALE-3, SCALE-5.
  - S2: SCALE-4, SCALE-1.
  - S3: SCALE-2.
  - S4: SCALE-7.
- **Hotspot:** `WorldSessionRuntime.cs` is touched by SCALE-1/2/3/4/7. `ServerConnection` is touched by SCALE-1/2/6/7.

## NETR analyst (NETR-1 to NETR-8)

No wire change is needed for any of these tasks.

| Task | What | Files | Size |
| --- | --- | --- | --- |
| NETR-1 | Server liveness: idle-inbound timeout (10 s) and send-stall timeout (5 s), with connection options. Coalescing becomes O(n²) on the tick thread, so append into per-connection lists with an overflow cap. | `ServerConnection`, `DedicatedServerService`, `ServerSnapshotEnvelope` | M |
| NETR-2 | Client: server-silence watchdog (8 s), a 1 s keepalive resend in the send loop, `PlayScene.SessionEndReason` (disconnect or `JoinRejected`), and `Game` returns to the startup scene with the reason. | `RemoteClientSession`, `PlayScene`, `Game`, `StartupConnectionScene`, `NetworkSessionCoordinator` | M |
| NETR-3 | **Bug:** secondary seats build once per received command with no interval gate (up to 64 cubes/s). Evaluate `ConsumePrimaryUsePressed` per seat per tick. Starvation decays to idle after 16 repeated ticks. | `RemoteSessionPlayerState`, `WorldSessionRuntime`, `RuntimeAuthoritativeSessionHost` | M |
| NETR-4 | Command sequence per prediction tick (not per frame). Exactly one command per tick. The client outbound becomes a bounded FIFO (16) with merge on overflow. A snap zeroes velocity. | `PlayScene`, `RemoteClientSession`, `ClientCorrectionApplier`, (`ClientPredictionReconciler`) | M |
| NETR-5 | `BufferedPlayerCommandInbox`: a ring of 8 per session, pops one per tick, elastic shed when depth stays above 3 for 32 ticks, `Remove` on release. | new file, `DedicatedServerHost`, `LatestPlayerCommandInbox` (internal Merge) | M |
| NETR-6 | Validate input. Reject `BuildUseIntent`/`CombatUseIntent` over `player.action`. Clamp axes and sanitize NaN. Cap the action queue at 16 per session, with `RemoveSession`. Defer and throttle resync to 1 per 2 s and serve it after the tick. **Noclip/freeze cheat gate: DEFERRED; a user-visible decision, not taken in continuous mode.** | `PlayerActionHandler`, `PlayerCommandHandler`, `PlayerActionQueue`, `WorldResyncRequestHandler`, `ServerRoutingContext`, `DedicatedServerHost` | M |
| NETR-7 | Directional frame limits (server reads 64 KB and writes 32 MB; the client the reverse), plus `SnapshotIntervalTicks` (default 2 = 32 Hz) with action results after the snapshot. | Framer, `ServerBootstrap`, `IntegratedLocalServerHandle`, `RemoteSessionConnector`, `DedicatedServerService` | S–M |
| NETR-8 | Stale-session takeover on rejoin by display name, when the old connection is silent for 2 s or more. | `JoinRequestHandler`, `ServerRoutingContext`, claim registry, `DedicatedServerHost`, `DedicatedServerService`, `RuntimeAuthoritativeSessionHost` | M |

- **Suggested waves:**
  - A: NETR-1, NETR-3, NETR-4, NETR-6.
  - B: NETR-2, NETR-5, NETR-7.
  - C: NETR-8.
- **Overlaps with SCALE:**
  - NETR-7's frame limits versus SCALE-1 chunking: SCALE-1 is the real fix; NETR-7's frame limit is a stopgap.
  - NETR-7's snapshot interval versus SCALE-2 deltas: complementary.

## ARCH analyst (ARCH-1 to ARCH-8)

| Task | What | Size |
| --- | --- | --- |
| ARCH-1 | Test suite under 30 s with zero flakes. Applies the wave-6 test-reliability follow-up. See the details below. | L |
| ARCH-2 | Invert Simulation→Server. Covers the rules, the persistence ports, `ITickDrivenAuthoritativeSessionHost` and the host factory. See the details below. | L |
| ARCH-3 | Step A: split `WorldSessionRuntime` (2,464 lines) into partial files. Step B (later waves): extract a persistence bridge, inventory and a combat resolver. | M |
| ARCH-4 | Docs drift. See the details below. | S–M |
| ARCH-5 | Clear the 29 warnings. See the details below. | S |
| ARCH-6 | Remove dead APIs. See the details below. | S |
| ARCH-7 | Split `Render.cs` (1,421 lines) and `PlayScene` (958 lines). See the details below. | M |
| ARCH-8 | Split `Map.cs` (2,202 lines): `MicrocellGreedyMerger` plus partials (RenderExport, Rubble, Queries). | M |

**ARCH-1 (test suite).**
- Fix starvation: `[ModuleInitializer]` sets `SetMinThreads`; add `xunit.runner.json` with parallelAlgorithm aggressive.
- Fix the `BackgroundWork` flake test (TCS without `RunContinuationsAsynchronously`).
- Remove the 3 s floor in `ClientWorldStateBoundary`.
- Shrink the TimingSensitive set to TickPacer, MultiClientLoopbackSmoke and the 250 ms mesh perf test, moved to its own class.
- Fixtures: a startup map for `WorldGeneratorTerrainTests` (13.6 s); glTF models for the parity and data tests (12.7 s, 5.3 s).
- Split `PresentationExportCacheTests` (16.5 s) into classes.
- Cut the ticks in the ProtocolV2 three-client test.
- Test files only.

**ARCH-2 (dependency inversion).**
- Move `Server/Rules/*` (7 files) → `Simulation/Rules`.
- Move the persistence ports, `Persisted*` records, `PersistenceCheckpoint`, `RepositoryPersistenceCheckpointWriter` and `PersistenceWriteQueue` → `Simulation/Persistence`.
- Move `ITickDrivenAuthoritativeSessionHost` → `Simulation/Runtime`.
- `RuntimeAuthoritativeSessionHost.CreateDefault(RuntimePersistencePorts?)`, and a new `ServerBootstrap.CreateRuntimeHost(bundle)`.
- Add `Dream.Server` to the Simulation forbidden tokens, plus Client allowlist ratchets.
- `WorldSessionRuntime` gets using-line changes only.

**ARCH-4 (docs drift).**
- The docs say .NET 8; the project targets 10.
- The absolute-path claim in AGENTS.md is stale.
- The DDL doc lacks `world_metadata`; add it to the init SQL too.
- Stale material in `project-structure`, `README` (60-bullet change log), `architecture` and `rendering-and-assets` (`Cube.Animation`).
- `build-and-run`: add a testing section.
- The `.sln` lacks the tests project.
- `.gitignore`: `artifacts-perf-console*.txt`.

**ARCH-5 (warnings).**
- `GL.PolygonMode`/`CullFace` → TriangleFace.
- `MemberNotNull` on the shader fields.
- Inventory nullable pattern matching.
- Bump ImageSharp (7 advisories) and `SQLitePCLRaw` (≥2.1.11).
- `NoWarn` NU1701 on BulletSharp.
- Remove the dead `Render.DrawSolidCube`.

**ARCH-6 (dead APIs).**
- `PlayerController` becomes a static class with only `CaptureCommandFrame`/`CreateCommandFrame`. The forwarders and instance methods go.
- Remove `Map.CaptureGeometryChunkSnapshot` and `GetNearbySolidCubes`.
- Add `InternalsVisibleTo` (new `AssemblyInfo`).

**ARCH-7 (`Render.cs` and `PlayScene` splits).**
- `HudPanels`, `ImGuiGlBackend` and `HudWorldText` out of `Render.cs`.
- `PlaySceneReplicationRouter`, `PlaySceneDiagnostics` and `HudRequestApplier` out of `PlayScene`.

**Proposed waves.**
- Wave 7: ARCH-1, ARCH-2, ARCH-5, ARCH-6.
- Wave 8: ARCH-3A, ARCH-4, ARCH-7, ARCH-8.
