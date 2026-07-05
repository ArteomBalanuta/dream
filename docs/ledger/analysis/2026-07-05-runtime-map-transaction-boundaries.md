# Runtime Map Transaction Boundaries

Purpose: Analyze the recurring invisible-cube and stale-collision-frame bugs around moving placed cubes, fractured rubble, renderer exports, physics bodies, persistence, and replication.

Status: Analysis. This is not yet an implementation plan.

Scope: Runtime map mutation, Bullet physics synchronization, geometry render/collision exports, SQLite persistence handoff, and server snapshot/mutation replication.

## Symptom

When multiple cubes collide, especially a mix of full placed cubes and fractured rubble pieces, some cubes can stop rendering while red debug collision frames remain visible. The objects can still exist physically: the player can collide with, push against, or jump on invisible bodies.

That symptom means the physics world still owns one or more Bullet bodies, while the render-facing map indexes no longer expose a matching cube or geometry block.

## Transaction Hypothesis

The transaction concern is directionally correct, but there are two different transaction layers:

- SQL transactions matter for restart/rejoin correctness.
- Runtime mutation transactions matter for the live invisible-cube bug.

The reported in-frame invisibility is more likely caused by missing runtime transactions than by missing database transactions. SQL can persist an inconsistent result, but it does not explain a red Bullet frame remaining visible while the render export omits the cube during the same play session.

## Current Runtime Split

One cube or geometry cell is represented across several mutable structures:

- `Map.Cubes[x,y,z]`
- `_geometryCells`
- `_renderBlocksByCell`
- `_collisionBodiesByCell`
- `_staticPhysicsCells`
- `_pendingRenderDirtyCells`
- `_pendingCollisionDirtyCells`
- `_solidCubes`
- `_cubeCells`
- `_placedCubeSpatialIndex`
- `WorldPhysicsBinder` body indexes
- `PhysicsIntegrator.MapCubeBodies`
- `PhysicsIntegrator.DynamicMapCubeBodies`
- render snapshots from `CaptureGeometryRenderExport`
- collision snapshots from `CaptureGeometryCollisionExport`

These collections must agree after every map mutation. Today they are updated directly inside methods such as `UpdateCubePosition`, `RemoveCube`, and `FractureCubeIntoPieces`.

## Concrete Risk Found

`PhysicsSyncSystem.SyncDynamicMapBodies` snapshots the dynamic Bullet body list, then applies each transform immediately:

- full cube bodies call `map.UpdateCubePosition(cube, position, syncPhysics: false)`
- fracture piece bodies call `map.UpdateFracturePiecePosition(cell, pieceIndex, center)`

This avoids mutating the body list while iterating, but it does not make the map mutation itself atomic.

`Map.UpdateCubePosition` computes a new integer cell, removes the old cell record, then writes the moved record into the new key and assigns `Cubes[newKey] = cube`. It does not validate whether the target cell is already owned by another full cube, fractured cell, authored subcell cell, or moving object being processed later in the same physics sync.

If two dynamic cubes resolve into the same cell, or a full cube crosses into a fractured cell, the occupancy grid can be overwritten while both Bullet bodies continue to exist. The renderer depends on map indexes and may skip the overwritten or orphaned object. Debug collision rendering can still draw the Bullet body. This matches the invisible-render plus visible-red-frame symptom.

## Where Transactions Would Help Most

### 1. Physics-To-Map Sync Transaction

After a Bullet step, collect all dynamic body transforms into a batch before mutating the map.

The batch should:

- ignore disposed or stale bodies
- capture cube identity or fracture-piece identity plus new center
- compute old and proposed keys
- detect target-cell conflicts
- choose a conflict policy before touching map indexes
- commit all accepted changes in one ordered phase
- rebuild render/collision artifacts once per dirty cell after the map state is coherent

This is the highest-value runtime transaction. It directly addresses colliding moving cubes.

### 2. Geometry Mutation Transaction

Map operations should stop directly updating many indexes one method at a time.

A `MapMutationTransaction` or `GeometryMutationBatch` should stage:

- removed full cube cells
- added full cube cells
- updated exact centers
- fracture replacements
- dynamic fracture-piece center changes
- render dirty cells
- collision dirty cells
- physics body additions and removals

Then validation should run before commit. If validation fails, the map should remain unchanged or fall back to a safe conflict policy.

### 3. Render/Physics Presentation Snapshot

The renderer should consume an immutable post-commit geometry snapshot for the frame. It should never observe a map half-way through physics sync.

The project already has geometry export objects, but the state feeding them can still be updated object-by-object. The missing piece is a stable commit boundary before export capture.

### 4. Persistence Transaction

When a cube fractures, the database should delete the original placed cube row and insert all fracture-piece rows inside one SQL transaction.

Without that, a crash or exception between `DeletePlacedCube` and `SaveCubeFracturePiece` calls can create restart-only inconsistencies:

- original cube removed but pieces missing
- pieces inserted but original cube still present
- partial piece set persisted

This does not explain the live red-frame/render desync by itself, but it will prevent rejoin/restart variants of the same family.

### 5. Network Mutation Transaction

Current incremental fracture replication sends only a cell-level `CubeFractured` mutation. Clients reconstruct local fracture pieces instead of receiving the authoritative piece centers and source-cube removal as one payload.

For multiplayer parity, fracture replication should become an atomic mutation payload:

- source cube id or cell
- removed source cube center
- generated piece centers and sizes
- material id
- mutation sequence or tick

Clients should apply the whole mutation or none of it.

## Suggested Conflict Policy

Short term, moving full cubes should not be allowed to overwrite occupied geometry cells during `UpdateCubePosition`.

If the proposed target cell is occupied by another cube or fractured geometry, the sync transaction should keep the cube's logical cell ownership unchanged and update only its exact center for rendering/physics parity, or reject the cell transition until the target cell is clear.

Longer term, dynamic placed cubes should become entity-like objects with exact transforms and a broadphase/spatial index, while static terrain remains grid-cell geometry. Treating dynamic physics objects as owners of `Cubes[x,y,z]` grid slots is the design pressure behind these bugs.

## Invariants To Add

Focused tests should assert these invariants after every dynamic physics sync:

- every dynamic full cube body has exactly one renderable cube identity
- no two full cubes own the same `GeometryCellKey`
- no full cube overwrites a fractured cell
- every `Cubes[x,y,z]` owner also has `_cubeCells` and `_geometryCells` entries
- every non-floor `_geometryCells` full cube has a matching `Cubes[x,y,z]` owner
- every dynamic fracture body reference maps to an existing fracture piece
- render export and collision export agree on piece centers and sizes
- debug Bullet bodies have corresponding map identities unless explicitly marked diagnostic-only

## Recommended Next Implementation Slice

Implement a `PhysicsMapSyncTransaction` first.

That slice should:

- collect dynamic body transforms without mutating `Map`
- validate full-cube target-cell conflicts
- commit safe full-cube exact-center updates and cell transitions
- commit fracture-piece center updates
- leave static chunk dirty state untouched for moving dynamic blocks
- expose diagnostics when a body is skipped because its identity is stale or its target cell is occupied
- cover two-cube same-cell collision and full-cube-versus-fractured-cell collision with tests

This should reduce the chance of invisible render bodies without requiring a full entity-system rewrite.
