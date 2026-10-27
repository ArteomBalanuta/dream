# Cube Physics, Replication, And Render Deep Audit

Purpose: Record a code-level investigation into three symptoms reported during manual play: frame-rate drops while pushing cubes, cubes duplicating when they are pushed into each other, and cubes that have collision but render without visible texture.

Status: Analysis only. No code was changed. Findings are against the uncommitted working tree that contains the 2026-07-05 hardening pass. Follow-up: A1–A4 were addressed the same day; see `work-log/2026-10-27-cube-duplication-root-cause-fixes.md`. B1 and C1 were addressed in `work-log/2026-10-27-fallback-cube-texture-and-static-dirty-split.md`. B2 was addressed in `work-log/2026-10-27-replicated-cube-move-body-reuse.md`.

Scope: `Map`, `PhysicsSyncSystem`, `WorldPhysicsBinder`, `WorldSessionRuntime` tick/export, `RemoteWorldMutationApplier`, `PlayScene` client tick, `DedicatedServerService` broadcast, `StaticWorldChunkRenderer`, `Render` cube draw paths, and SQLite checkpoint persistence.

Related: `2026-07-05-cube-sync-physics-fracture-critical-audit.md`, `2026-07-05-runtime-map-transaction-boundaries.md`, `work-log/2026-07-05-runtime-map-sync-transaction-first-slice.md`.

## Summary

The earlier passes hardened each piece locally, so a rejected move is now handled consistently inside a single `Map`. The remaining bugs come from three structural problems that those passes did not address:

1. **Two physics simulations own the same cubes.** The desktop client's mirror runtime steps Bullet and runs `SyncDynamicMapBodies` every tick, and it also applies the server's `CubeMoved` deltas. The two worlds diverge, and the client's delta applier creates new cubes when it can't find the one the server means.
2. **World mutations are a replayable log, not state.** `_pendingWorldMutations` is only cleared when a snapshot is broadcast to a joined client. Mutations queued before a join get replayed on top of the join-time world snapshot.
3. **Integer-cell exclusivity is enforced against continuous physics.** One cube per `floor(center)` cell, checked sequentially per body, rejects legitimate chain pushes and teleports bodies back. That causes jitter, keeps bodies awake, and burns CPU.

The texture symptom is an unrelated render bug: the fallback cube mesh has no UV or normal attributes.

## A. Cube Duplication

### A1. Client mirror runs its own cube physics (root cause, high confidence)

`PlayScene.Update` (`dream/Client/Scenes/PlayScene.cs:150-158`) calls `_runtime.AdvanceAuthoritativeTick(..., allowGameplaySimulation: false)`. In `WorldSessionRuntime.UpdateAcceptedCommand` the flag only gates enemies. Both of these run unconditionally on the client:

- `Physics.StepSimulationFixed(delta)` (`WorldSessionRuntime.cs:263`)
- `_physicsSync.SyncDynamicMapBodies(Map, Physics.DynamicMapCubeBodies)` (`WorldSessionRuntime.cs:279`)

The client's predicted player body therefore pushes the client's own dynamic cube bodies, and the client `Map` commits those moves with `syncPhysics: false`. Meanwhile the server simulates the same push with different timing and inputs. The two maps disagree about which cell owns which cube. The crash stack in `roadmap_ideas/issues.txt` (PlayScene → ServerTickRunner → AdvanceAuthoritativeTick → SyncDynamicMapBodies) confirms this path runs on the client.

Side effect: on the client, `_pendingWorldMutations` gets a `CubeMoved` entry for every locally moved cube every tick, and the client never calls `ExportServerSnapshotEnvelope`. That list grows without bound for the whole session, a memory leak proportional to how long cubes are pushed.

### A2. `ApplyCubeMovedMutation` materializes a new cube when lookup misses (direct duplication mechanism, high confidence)

`RemoteWorldMutationApplier.ApplyCubeMovedMutation` (`dream/Client/Replication/RemoteWorldMutationApplier.cs:229-249`):

1. Looks up the cube at the server's **source** cell.
2. If none, looks at the server's **target** cell.
3. If still none, `map.TryAddCube(source)` creates a **new** cube and moves it to the target center.

Because of A1, the client's copy of that cube is often in a third cell (its own physics moved it somewhere else). Step 3 then creates a second cube while the original stays where the client's physics left it. That is a duplicate. The new cube also gets the default `Grass` material, because the mutation carries no material.

Step 1 also has a wrong-cube variant. If the client's physics moved a *different* cube into the server's source cell, that cube gets teleported to the server's target. The visible result is one cube jumping and another remaining: a swap or duplicate.

`Map.UpdateCubePosition` rejections on the client are also silent. When the client's target cell is occupied by a cube the client hasn't moved yet, the server's move is dropped and the client copy is stuck at the old cell. Every later delta for that cube then names a source cell where the client has nothing, which falls through to step 3 again.

There is no periodic resync. The full `WorldSnapshot` is only sent at join (`RuntimeAuthoritativeSessionHost.cs:50,79`), so divergence persists until the player reconnects.

The same weakness affects `CubeDestroyed` (`RemoteWorldMutationApplier.cs:165-171`): it removes whatever cube the client has at the server's cell. If divergence has moved the client copy, the wrong cube or no cube is removed.

### A3. Mutations queued before a join are replayed on top of the join snapshot (high confidence)

`DedicatedServerService.AdvanceOnceAsync` (`dream/Server/DedicatedServerService.cs:74-100`) returns early when there are no connections or no joined connections. In those cases `CreateSnapshotMessage` is never called, so `ExportServerSnapshotEnvelope` never clears `_pendingWorldMutations` or `_pendingDamageEvents`.

- On a dedicated server with nobody connected, both lists grow every tick while any cube or rubble moves. That is a memory leak.
- When the first client joins, it receives `InitialWorld`, which is the current state. The next broadcast snapshot then contains the **entire backlog**: every `CubePlaced`, `CubeMoved` and `CubeFractured` since startup or since the last joined client left. The client replays all of them on top of the current world:
  - Old `CubePlaced` at a cell the cube has since left creates a duplicate at the original cell.
  - Old `CubeMoved` with stale source cells hits the A2 fallback and creates more duplicates.
  - Old `CubeFractured` calls `RestoreFracturePieces`, which resets rubble to its spawn layout.
- The integrated host has a smaller version of this. Ticks that run between transport accept and the local client's join, while restored cubes settle, are replayed after join.

### A4. Snapshot and delta ordering on join for later joiners (medium confidence)

When a second client joins while another is already joined, mutations from the window between the last broadcast and the join are in both `InitialWorld` and the next snapshot. Moves and placements are mostly idempotent through the source/target lookup, but `CubeFractured` resets rubble positions for the new client.

## B. Push Performance

### B1. Dynamic cube movement forces static chunk rebuilds every frame (high confidence)

Every accepted cross-cell move and every fracture-piece center change adds the cell to `_pendingRenderDirtyCells` (`Map.cs:250,266,335`). On the client, every received `CubeMoved` calls `UpdateCubePosition(..., syncPhysics: true)`, which calls `RebuildGeometryCellArtifacts` and marks the cell dirty even for same-cell moves (`Map.cs:219-229`).

`StaticWorldChunkRenderer.Draw`:

- includes `DirtyCells` in the export signature (`StaticWorldChunkRenderer.cs:370-378`), so the signature changes every frame something moves, and `exportUnchanged` is false;
- then runs `StaticWorldChunkExtractor.ExtractCells` over the **entire static world** and `PlanBuildInputs`, which groups all cells by chunk and hashes every chunk together with its 3×3×3 neighbor occluders;
- forces a rebuild of every chunk containing a dirty cell (`dirtyChunks.Contains(chunkKey)` bypasses the signature cache at `StaticWorldChunkRenderer.cs:70-75`). That schedules a worker build plus up to 8 GPU uploads per frame.

Dynamic cubes and rubble are **not** part of static chunk meshes. `ExtractCells` only reads `CompatibilityCubes` (static) and `Blocks` (static). These rebuilds produce identical meshes. This is pure waste, and it scales with world size, not with the number of moving cubes.

Fix direction: only propagate dirty cells that are or were static (`_staticPhysicsCells`, floor, merged sculpture), or split the export into a static dirty set and a dynamic dirty set.

### B2. The client recreates Bullet bodies for every replicated move (high confidence)

`UpdateCubePosition(syncPhysics: true)` goes through `RebuildGeometryCellArtifacts(..., syncPhysics: true)`, then `WorldPhysicsBinder.SyncCellCollisionBodies`. That removes and disposes the cell's bodies and allocates a new `BoxShape`, `DefaultMotionState` and `RigidBody`. The server sends a `CubeMoved` for every moving cube on every tick at 64 Hz, so the client churns native allocations at that rate per moving cube. Each recreated body starts at rest, and A1 then simulates it locally. That adds visible jitter against the server's motion.

### B3. Sequential cell exclusivity rejects chain pushes and keeps bodies awake (high confidence)

`PhysicsSyncSystem.SyncDynamicMapBodies` (`dream/Simulation/Physics/PhysicsSyncSystem.cs:47-62`) commits bodies one at a time in list order. When cube A's new center crosses into the cell still owned by cube B, A is rejected even if B leaves the same cell later in the same pass. A rejected body is `ResetBodyToCenter` (`:116-124`): teleported back to its last accepted center, with velocity zeroed and `Activate(true)` called.

While the player keeps pushing a row of cubes:

- the rear cubes are reset almost every tick, which produces stutter and a "sticky" feel;
- the reset can put a body back into penetration with the player capsule or a neighbor, so Bullet responds with large separation impulses;
- `Activate(true)` on every reset prevents sleeping, so contacts and broadphase stay hot.

Bullet penetration slop under a hard push can also put two 1×1 boxes whose centers share one integer cell, for example x = 3.001 and 3.999. The map rejects that even though it is a transient physical state.

### B4. Fractured cells permanently block neighbors (high confidence)

`Map.FractureCubeIntoPieces` (`Map.cs:372-402`) leaves the original compatibility `Cube` in `Cubes[x,y,z]`, `_solidCubes`, `_cubeCells` and `_placedCubeSpatialIndex`. `CanTransitionCubeToCell` (`Map.cs:1083-1107`) rejects any full cube entering that cell for two reasons: `Cubes[]` is occupied and the record has a `SubdivisionState`. That holds even after all the rubble has rolled away. Pushing a cube toward a former fracture site gives an endless reject/reset loop (B3) at an invisible wall. Placement into that cell is also refused (`TryAddCube` checks `Cubes[]`).

### B5. Fracture piece ownership never leaves the origin cell (medium impact)

Pieces stay keyed by their origin `GeometryCellKey` however far they travel. Any `RebuildGeometryCellArtifacts(origin, syncPhysics: true)` destroys and recreates every piece body at its last accepted center with zero velocity. Piece motion after the initial `CubeFractured` is never replicated, so each client simulates its own rubble, which diverges across clients and from the server. Only the server's 5-second checkpoint persists the true piece positions.

### B6. Checkpoint persistence stalls the tick thread every 5 seconds (high confidence)

`DedicatedServerHost` calls `Runtime.PersistCheckpoint()` every 5 s from inside the tick callback, while holding the authority lock. `PersistPlacedCubeCheckpoint` (`WorldSessionRuntime.cs:1542-1561`) runs `DeleteCubeFracturePieces` and `SavePlacedCube` for **every** cube. `PersistFracturePieceCheckpoint` runs `SaveCubeFracturePiece` for every piece and calls `Map.CaptureFracturePieces()`, a full scan of the geometry cells, once per fractured cell. Every repository call opens a new `SqliteConnection` and autocommits; `SqliteWorldPersistenceRepository` contains no `BeginTransaction`. With N cubes and P pieces that is about 2N + P separate commits, each a potential fsync, on the simulation thread. It produces a periodic hitch that grows with world content.

### B7. Smaller per-tick costs

- `SyncDynamicMapBodies` reads the transform of every dynamic body every tick through `MotionState.GetWorldTransform`, a P/Invoke call, sleeping bodies included, and allocates arrays and `HashSet`s per tick.
- `WorldPhysicsBinder.RemoveCompatibilityCubeBodies` (`WorldPhysicsBinder.cs:280-283`) scans every map body linearly, with tuple pattern matching, on each fracture or removal, then scans every cell list.
- `List<RigidBody>.Remove` on `MapCubeBodies` and `DynamicMapCubeBodies` is O(n) per body removal, which B2 multiplies.
- `CapturePresentationGeometryRenderExport` iterates every `_solidCubes` entry, floor included, plus every geometry cell, every frame, and the static signature then hashes every static cube and block every frame.

## C. Cubes With Collision But No Texture

### C1. The fallback cube mesh has no normals or UVs (high confidence)

`Render.DrawCube` and `Render.DrawGeometryBlock` use the glTF `AnimatedModel` only when `cube.Animation` or `block.Animation` is set. `WorldGenerator.ResolveAnimation` returns a model only for `Grass`, `Dirt` and `Stone`; every other material returns `null`: `Sand`, `Mud`, `Snow`, `Rock`, `GrassPath`. With `loadVisualAssets: false`, every material returns `null`.

Null-animation cubes go to `DrawTerrainCubeFallback` (`Render.cs:1034-1046`). That draws `_vertexArrayObject`, whose only enabled attribute is position at location 0 (`Render.cs:345-353`). The `cube` shader reads `aNormal` at location 1 and `aTexCoord0` at location 3 (`shaders/cubeVertex.glsl:3-5`). Those attributes are disabled, so every fragment samples the same texel at UV (0,0) and lighting uses a zero normal. The cube renders as a flat, untextured, often dark box while its Bullet body collides normally.

This hits every **dynamic** cube or **rubble piece** of a non-Grass/Dirt/Stone material. Static terrain of the same materials looks correct because it goes through `StaticMeshBuffer`, which does carry UVs and normals. So a cube textured while static loses its texture as soon as it is a dynamic placed cube or rubble.

`StaticTerrainMaterialCatalog.Resolve` also returns magenta with an empty texture path for any material id it doesn't know, which is a second "no texture" look.

### C2. Material lost on client fallback creation (medium confidence)

The A2 fallback `TryAddCube(source)` and the legacy fracture fallbacks (`RemoteWorldMutationApplier.cs:89-94, 185-190`) create cubes with the default `Grass` material, so duplicated cubes can also show the wrong material.

## D. Other Correctness Notes

- **Fracture spawn ignores the cube's real position.** `CubeFracturePattern.CreateFirstPassRubble(cell)` places pieces around the integer cell center, at the bottom of the cell. A cube that was pushed off-center or was falling has its rubble teleported. The rubble can spawn overlapping a neighbor cube, which produces an explosive separation.
- **Hit-ray uses cells, not bodies.** `Map.TryGetFirstSolidCubeAlongRay` samples `Cubes[]` by integer cell. For an off-center moved cube, part of the visible volume is not hittable, and an empty part of its cell is.
- **Static chunk build queue can drop work.** `ScheduleBuild` overwrites `_queuedBuildRequest`, and `DrainCompletedBuild` overwrites a partially applied `_activeUploadBatch`. Stale-mesh removal only covers the `DirtyChunks` of the batch that finishes. A chunk that became empty in a superseded request keeps its old mesh: a static ghost with no collision. Low frequency, but this is the inverse of the "invisible cube" symptom.
- **Restore ordering.** `RestorePersistedWorldState` restores placed cubes before fracture pieces. `RestoreFracturePieces` calls `RemoveCube` on any cube in the parent cell without unregistering it from `WorldObjectRegistry` or deleting its persisted row.

## Recommended Fix Order

1. **Stop client-side cube simulation (A1).** On the client mirror, either keep map cube bodies kinematic and driven only by server deltas, or skip `SyncDynamicMapBodies` and stop accumulating `_pendingWorldMutations` when the runtime is not authoritative. This removes most duplicate and divergence cases.
2. **Give cubes stable ids on the wire (A2).** Put the cube's actor id and material in `CubeMoved` and the other mutations, and resolve the client cube by id instead of by cell. Never create a cube from a move delta; request a resync instead.
3. **Drop or bound the mutation backlog when nobody is joined (A3).** Clear `_pendingWorldMutations` and `_pendingDamageEvents` on every tick when there is no joined receiver, or stamp mutations with server ticks and have clients ignore anything older than their `InitialWorld` tick. Add a periodic or on-mismatch world resync.
4. **Fix the fallback cube mesh (C1).** Give `_vertexArrayObject` normals and UVs matching the `cube` shader layout, or route dynamic cubes of all materials through a textured terrain cube mesh. Add models or materials for the remaining terrain ids.
5. **Separate static and dynamic dirty sets (B1).** Only static cell changes should invalidate static chunks. This should remove most of the frame-rate drop when pushing.
6. **Client move application without body recreation (B2).** Update the existing body transform for same-cell and cross-cell moves instead of rebuilding the cell.
7. **Two-phase physics-to-map commit (B3).** Collect all proposals, release cells being vacated, then resolve conflicts. This is the `PhysicsMapSyncTransaction` already proposed in the 2026-07-05 transaction analysis. Don't reset bodies that only transiently share a cell. Prefer keeping the previous owner and letting physics resolve.
8. **Release fractured cells (B4/B5).** Drop the compatibility cube from `Cubes[]` and the indexes once a cell fractures, and track rubble by piece id rather than by origin cell.
9. **Batch checkpoint writes in one transaction on one connection (B6)**, and move the checkpoint off the tick thread, or at least do it outside the authority lock with a captured state copy.

## Suggested Regression Tests

- Client applier: a `CubeMoved` whose source and target cells are empty on the client must not increase `map.SolidCubes.Count`.
- Join replay: advance the server with no joined connection while a cube moves, join, and assert the client cube count equals the server cube count after the first snapshot.
- Chain push: three dynamic cubes in a row pushed in +X for N ticks; assert no `ResetBodyToCenter` on the trailing cubes when the leading cube vacates first.
- Render: a dynamic `Sand` cube must render through a textured path. At minimum, assert that `ResolveAnimation` returns a model, or that the fallback VAO exposes attributes 1 and 3.
- Static chunk: moving a dynamic cube must not schedule a static chunk build.
