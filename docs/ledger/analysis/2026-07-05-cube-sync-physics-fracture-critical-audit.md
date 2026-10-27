# Cube Sync, Physics, Fracture Critical Audit

Purpose: Record concrete implementation risks found in cube movement, destruction, replication, physics synchronization, and render export code.

Status: Current audit follow-up. This analysis feeds the matching implementation plan for the same date.

Scope: Runtime cube mutations, world mutation protocol payloads, Bullet dynamic body synchronization, fracture-piece validation, and client presentation export cost.

## 1. Client-Server Synchronization And Networking

`WorldMutationDelta` only carries a mutation kind and cell position. That is enough for placed and destroyed cells, but it is not enough for moved dynamic cubes or fracture pieces. A cube pushed by physics can change exact center or integer cell ownership without any `CubeMoved` mutation being queued. Clients only learn about that drift when a full world snapshot is included, so intermediate frames can render a stale cube while the server collides with the moved cube.

Fracture replication is also currently non-authoritative. The server emits `CubeFractured` for a cell, while `RemoteWorldMutationApplier` locally calls `Map.FractureCubeIntoPieces`. That method randomizes rubble centers, so each client can generate different child piece positions and collision shapes for the same destruction event.

## 2. Rigid Body Physics And Collision Resolution

`PhysicsSyncSystem.SyncDynamicMapBodies` gathers Bullet transforms but ignores whether `Map.UpdateCubePosition` and `Map.UpdateFracturePiecePosition` actually accept the new position. If the map rejects a transition because the target cell is occupied, the method still updates `RigidBody.UserObject` to the rejected center. That creates an authoritative map/body mismatch.

Dynamic rubble and dynamic full-cube bodies are configured with `ActivationState.DisableDeactivation` and zero contact threshold. That prevents sleeping and keeps fragments active forever, which is a likely contributor to the large sync and broadphase costs seen after rubble collisions.

## 3. Destruction And Fracture Logic

`GeometryFracturePiece` accepts any `Vector3` center and any `float` side length. Restore/load paths can therefore pass NaN, infinity, zero, negative, or excessively large side lengths into render and Bullet `BoxShape` creation. This can produce invisible geometry, invalid transforms, or native physics instability.

## 4. Visual To Collision Alignment

`Map.UpdateCubePosition` mutates `cube.Position` and the placed-cube spatial index before validating whether a cross-cell move is in bounds and unoccupied. A rejected move leaves the cube object and render candidate index ahead of the authoritative cell owner. This is the exact shape of "rendered cube here, collision wire there" bugs.

`Map.UpdateFracturePiecePosition` rebuilds render and collision descriptors but does not mark the updated cell dirty. Chunk/render caches can therefore reuse stale geometry after a dynamic fragment moves.

## 5. Performance, Memory, And Concurrency

`WorldSessionRuntime.CapturePresentationState` calls `Map.CaptureFullGeometryRenderExport()` every frame, ignoring the requested render distance. Hidden-face culling and static chunk hashing then receive the whole world export instead of nearby cells, so movement can trigger expensive full-world signature and chunk work.

No object pool exists for fracture pieces or Bullet bodies yet. That remains a larger design item, but the first priority is to reduce needless active bodies and full-world render exports before adding pooling complexity.

## Implementation Priorities

1. Make map cube movement atomic and make physics sync honor rejected moves.
2. Add authoritative `CubeMoved` and fracture-piece payloads to world mutations.
3. Validate fracture pieces before they reach render or Bullet.
4. Allow dynamic rubble bodies to sleep while preserving CCD for fast pushes.
5. Respect render distance in presentation exports and keep full export only for explicit full-snapshot paths.
