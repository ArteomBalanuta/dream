# World Persistence, Object Identity and SQL Abstraction: Roadmap

Purpose: plan the work that makes the whole world persistable, gives every world object a stable id, and puts a SQL abstraction in place that can hold more than one map. Map generators themselves are out of scope; this plan only adds the seam they plug into.

Status: proposal. Analysed on `integrate/engine-iteration` at `f499299`. Nothing has been implemented yet.

## Where Things Stand

### The world is code, not data

- Terrain is generated from code at every startup, on the server and separately on each client:
  - `Map.Initialize` calls `WorldGenerator.CreateStartupGeometry`.
  - The server builds its map in `WorldSessionRuntime.CreateMap`, the client in `ClientWorldSession.CreateMap`. Both pass `generateStartupTerrain: true`.
- The landmarks and sculptures are hard-coded in `WorldGenerator`: the pyramid at (14..23, 1..5, 12..21), the boulder, the rubble cluster, and the mounds.
- The only input to generation is `floorSize`, a client setting (`GameSettings.FloorSize`, 5–100). Height is fixed at 8.
- The database stores only an overlay on top of the generated terrain: placed cubes, rubble pieces, enemies, items and players.
- `world_metadata` holds one key, `simulation_seed`. The map's size, generator and version are not stored anywhere.

### Terrain changes are lost on restart

- Terrain cubes get an id lazily, on their first hit (`WorldObjectRegistry.TryResolveCubeRecord` raises `CubeIdAssigned`).
- The checkpoint skips any record without an owner or at `y == 0` (`RuntimePersistenceBridge.Checkpoints.cs` `TryCaptureCube`). No table stores terrain cells.
- So destroying a landmark cube, or damaging a sculpture, comes back undone after a restart.
- If the floor size changes, persisted cubes outside the new bounds are skipped on restore, but their rows stay.

### Ids are wire ids, and some objects have none

| Object | Key today | Problem |
| --- | --- | --- |
| Placed cube | `cube_id`, the replicated `int` actor id (`ReplicatedActorIdRanges`) | The persistent id and the network id are the same number. Nothing stores the counter; it is derived on load from the highest persisted id. |
| Terrain cell | position only | No id until it is hit, and that id is never saved. |
| Rubble piece | `(source_cube_id, piece_index)` | A piece has no id of its own. It is tied to a cube that no longer exists. |
| Enemy | `enemy_id`, a replicated actor id | Ids get reassigned on restore (`ReassignEnemyId`), so an enemy's id is not stable. |
| Item | `item_instance_id` (`long`) | Already stable. Its counter is derived from the highest persisted id (`GetMaxItemInstanceId`). |
| Player | `player_name` | Natural key. Identity option D is still open. |
| World | none | One database file is one implicit world. |

### The SQL layer

- Repositories are hand-written on `Microsoft.Data.Sqlite`:
  - `SqliteWorldPersistenceRepository` is 791 lines. Its interface, `IWorldPersistenceRepository`, has 21 methods and covers five aggregates.
  - The checkpoint writer is a separate SQL path (`SqlitePersistenceCheckpointWriter`, 280 lines).
- The schema is one script, `config/player-profiles.init.sql`, followed by ad-hoc `EnsureColumnExists` calls. There is no schema version and no migration history.
- Cell coordinates are stored as `REAL`, and timestamps as `TEXT`.
- `placed_cube_instances.owner_player_name` has `ON DELETE CASCADE`, so deleting a player deletes everything they built. Rubble pieces cascade the same way.
- What is already good, and stays:
  - WAL mode;
  - the group-commit write queue (`PersistenceWriteQueue`, `SqliteWriteBatch`);
  - incremental checkpoints;
  - the per-player prefetch;
  - the `Simulation`/`Server` split through repository interfaces.

## Target Model

- **A world is a row.** A `worlds` table holds:
  - `world_id`, `name`;
  - bounds (width, height, depth);
  - `map_source` (`generated` or `stored`), with the generator key, generator version and seed;
  - the next-object-id counter;
  - the schema version it was written with.

  Every world-scoped table gets `world_id`.
- **Every object has a stable `ObjectId`.** It is a 64-bit id allocated from the world's stored counter and never reused. It is separate from the replicated `int` actor id, which stays a wire and session concern. A map from `ObjectId` to actor id is kept at runtime.
  - This covers placed cubes, terrain cells once edited, rubble pieces (each one), enemies (no more reassignment), items, and later spawn points and other world features.
- **Terrain is data.** A map comes from an `IMapSource`:
  - `GeneratedMapSource` wraps today's `WorldGenerator`, keyed by generator name and version.
  - `StoredMapSource` reads terrain from the database.

  Edits are stored either as per-cell overrides on top of a generated base, or directly in stored chunks. Clients get the map's identity, or its terrain, from the server, instead of regenerating it from their own `floorSize` setting.
- **A SQL abstraction with seams.**
  - A versioned migration runner.
  - A small `IWorldStore` / unit-of-work layer over the existing write batch.
  - Per-aggregate repositories: worlds, terrain, objects, enemies, items, players.
  - The SQLite implementation stays the only one for now. Nothing in `Simulation` sees SQL.
  - A generator later implements `IMapGenerator`, which produces the terrain chunks for a new world row.

## Roadmap

Each phase is one or two waves, run with the usual team flow and at least 5 implementers where the work splits that far.

### Phase 0: Decisions (yours, before wave 1)

1. **One database per world, or many worlds in one database?** I recommend many worlds in one file, keyed by `world_id`. It keeps backups to one file and makes "list worlds" a query. One file per world is simpler, but every global table (players) is then duplicated.
2. **Terrain storage: generated base plus overrides, or full stored chunks?**
   - I recommend supporting both: `generated` worlds store only their edits, and `stored` worlds keep every chunk.
   - Generated plus overrides keeps today's worlds small and lets them upgrade in place.
   - Stored chunks are what imported or hand-made maps need.
3. **Do buildings outlive their builder?** Today, deleting a player deletes everything they built and their rubble (`ON DELETE CASCADE`). I recommend `ON DELETE SET NULL` instead, so buildings stay as unowned world objects.
4. **Wire actor id: keep `int` and map it to `ObjectId`, or send `ObjectId` on the wire?** I recommend keeping `int`. It avoids a protocol and bandwidth change, and the mapping is server-only.
5. **Existing databases:** migrate them in place, or start fresh? I recommend migrating, as world 1 with `generated` terrain using the current generator and version 1.

### Phase 1: SQL foundation (wave A, 5 workstreams)

- **A1 Migration runner.**
  - A `schema_version` table and ordered, numbered migration scripts (`config/migrations/0001_baseline.sql`, …).
  - The runner applies pending migrations in a transaction, refuses a database newer than the code, and backs up the file before a migration.
  - It replaces `EnsureColumnExists`. The baseline matches today's schema exactly, so existing databases upgrade with no change.
- **A2 Store and unit of work.**
  - `IWorldStore` opens a session. Inside the write queue's batch it enlists in that batch (as `SqliteWriteBatch.TryEnlist` does today); outside it, it opens its own connection.
  - It has typed parameter and reader helpers that replace the copy-pasted `AddWithValue` and `GetOrdinal` code.
- **A3 Split the world repository.**
  - `IWorldPersistenceRepository` becomes `IPlacedObjectRepository`, `IRubbleRepository`, `IEnemyRepository`, `IItemRepository` and `IInventoryRepository`, behind a facade so callers do not change yet.
  - `SqliteWorldPersistenceRepository` is split along the same lines.
- **A4 Checkpoint writer on the store.**
  - `SqlitePersistenceCheckpointWriter` uses the same repositories instead of its own SQL.
  - It adds the `updated_utc` guard from the wave-13 known limits, so a late checkpoint never overwrites the state a player saved on leave.
- **A5 Tests and fixtures.**
  - A migration test: build each historical schema (the baseline, plus one without the `center_*` and `experience` columns), migrate it, and compare.
  - A database round-trip harness.
  - Docs: `server-persistence-ddl.md`.

### Phase 2: Worlds as rows (wave B, 4–5 workstreams)

- **B1** Migration: a `worlds` table and a `world_id` column, with foreign keys, on placed cubes, rubble, enemies, items and per-player world state. Existing rows become world 1. Cell coordinates move to `INTEGER`.
- **B2** `WorldDescriptor`: bounds, map source, generator key and version, and seed. The server loads it at startup from configuration (`DREAM_WORLD` or `--world`) or creates it. The map is built from the descriptor instead of `floorSize`.
- **B3** Protocol: the server sends the descriptor in the join or world snapshot. A client builds its map from it and no longer from its own `GameSettings.FloorSize`; that setting then applies only when creating a local world. This needs a protocol bump.
- **B4** World lifecycle API: create, list, open, copy and delete a world (`IWorldCatalog`). It has a small server and CLI surface; the menu UI can follow later.
- **B5** Tests: two worlds in one database stay isolated; a restart restores the right world; a size mismatch is handled.

### Phase 3: Stable object ids (wave C, 5 workstreams)

- **C1** `ObjectId` type and allocator. The counter is stored on the world row and advanced in the same transaction as the insert, so ids are never reused or derived from a maximum. It is mapped to and from the actor id in a runtime `ObjectIdentityMap`.
- **C2** Placed cubes keyed by `object_id`. The migration turns `cube_id` into `object_id`. Restore stops reserving actor ids from persisted ids.
- **C3** Rubble pieces get their own `object_id` and row (with `origin_object_id` kept for grouping). The fracture and restore paths are updated.
- **C4** Enemies keyed by `object_id`. `ReassignEnemyId` is removed. Respawns create a new object.
- **C5** Items and inventory use the same allocator (items already have `long` ids). Tests: ids survive restart, fracture, respawn and checkpoint, and are unique across kinds.

### Phase 4: Terrain as data (waves D and E)

- **D1** `IMapSource` and `MapDefinition`. `Map` takes its cells from a source instead of calling `WorldGenerator` itself. `GeneratedMapSource` wraps `WorldGenerator` as generator `"default"`, version 1, and is golden-tested bit-identical to today.
- **D2** Terrain schema: `terrain_chunks` holds `world_id`, the chunk coordinates, the format version and a compact blob of cells, with material, flags and an optional subdivision bitmask. Chunks are 16 × 16 × height. There is also `terrain_cell_overrides`, keyed by world and cell, for edits on generated worlds.
- **D3** Persist terrain edits: destroying a cube, and changes to sculpture cells. Edited terrain cells get an `ObjectId`. The checkpoint captures dirty terrain cells.
- **D4** `StoredMapSource` plus an exporter that writes any map to chunks. With these a map can be generated once, then stored, copied and edited.
- **E1** World transfer of stored terrain: a client joining a stored world receives the chunks over the existing chunked transfer and does not regenerate them.
- **E2** `IMapGenerator` plug point: given a generator key, seed and bounds, it produces chunks for a new world. Today's generator is the only one; new generators are out of scope.
- **E3** Performance and soak: World10k-size checkpoint and restore times, chunk write cost, memory.

### Phase 5: Hardening

- Backup and restore of a world.
- Integrity check on load: foreign keys, objects out of bounds, duplicate ids.
- Docs (`architecture.md`, `configuration.md`, `server-persistence-ddl.md`).
- A load test that restarts the server mid-session.

## Risks

- **Determinism goldens.** `GeneratedMapSource` must reproduce today's cell order exactly, because insertion order drives the checksum and the physics body order. D1 needs a golden test before anything else moves.
- **Protocol.** B3 and E1 change the wire and need a version bump (to v5).
- **Migration of real databases.** Each migration needs a backup and a test against a copy of an existing `dream` database.
- **Checkpoint cost.** Terrain edits add dirty sets. They must stay incremental, so a fracture storm does not rewrite whole chunks every 5 s.

## Suggested Order

Phase 0 decisions → A (foundation) → B (worlds) → C (ids) → D and E (terrain). A to C can each run as one wave of 5 implementers. D and E are two waves. That is about 5–6 waves in total, plus fix loops.
