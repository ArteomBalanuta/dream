# Architecture

## Startup Flow

The application entry point is `Program.Main(string[] args)`. It now performs an explicit process-mode selection and argument validation step before startup work begins:

- no mode flag selects the current desktop path
- `--server` is recognized and now boots the first long-running `ServerBootstrap` dedicated-server path
- any other startup argument is rejected instead of silently falling through to desktop

Today, the desktop branch still creates a `Game` object and calls `Run()`, which hands control to OpenTK's `GameWindow` lifecycle. The server branch now routes through `ServerBootstrap.RunServerMode()`, which constructs the authoritative host, starts the TCP accept loop, and keeps the process alive until cancellation.

The next planned follow-on work for this startup seam is captured in:

- Phase 1: `docs/ledger/plans/2026-06-29-phase1-world-session-runtime-extraction.md` extracts `WorldSessionRuntime` so the authoritative runtime stops living directly under the desktop scene
- Phase 4: `docs/ledger/plans/2026-06-29-phase4-protocol-contract-and-routing.md` defines the protocol seam, message routing, and host/client wiring boundaries that a later server path should plug into

Inside the `Game` constructor:

- `GameSettings` is created first
- an on-demand handle for the integrated loopback authoritative server is created (`OnDemandLocalServerHandle`); the server itself starts only when the startup scene first uses it (Use Local Server, or `DREAM_AUTO_CONNECT`)
- a startup connection scene is created next and becomes the primary desktop entry path
- `PlayScene` is created only after a successful client connection
- `PlayScene` now requires that connected `RemoteClientSession`, so normal desktop gameplay is also a protocol client of the authoritative server instead of a direct local-authority runtime path

When the play session ends on its own (NETR-2: a lost connection, the 8 s server-silence watchdog, a rejected or timed-out join), `PlayScene.SessionEndReason` becomes non-null and `Game` returns to the connection screen instead of leaving a dead scene running. After that frame's update it clears the play scene, swaps in a fresh on-demand local server handle, and calls `PlaySessionEndTransition.TearDown`. That disposes the play scene (GL resources first, on the render thread) and the old local server handle (stopping the integrated server if it was started), each exactly once. Teardown failures are logged, not thrown. The new connection scene keeps the endpoint and player name, shows "Session ended: <reason>", and does not auto-connect again. The liveness timeouts are listed in `docs/configuration.md`, Connection liveness.

### Client session lifecycle (protocol version 12)

`RemoteClientSession` (`dream/Client/Networking/RemoteClientSession.cs`) speaks protocol version `TransportEnvelope.CurrentProtocolVersion` = 12 (`TransportEnvelope.cs`; the versions are described below, v12 last); a peer on another version is refused with `ProtocolVersionMismatchException`. Its timers, all listed with the server-side ones in `docs/configuration.md`, Connection liveness:

- Keepalive: when no frame has gone out for `KeepaliveInterval` (1 s, `RemoteClientSession.cs:98`), the send loop writes one empty frame, so a frozen game loop does not look dead to the server (server idle timeout 10 s, `ServerConnectionOptions.cs:12`).
- Server-silence watchdog: `ServerSilenceTimeout` (8 s, line 117) ends a joined session that receives no inbound bytes, with `ServerSilenceDisconnectReason`.
- Join: a join the server could not load in time is answered with a retryable `JoinRejected` and the client resends on the same connection after `JoinRetryBaseDelay` (1 s) doubling, up to `MaxJoinRetries` (3, line 107). Any other rejection, a rejected last retry, or no `JoinAccepted` within `JoinTimeout` (45 s, line 109) ends the session with a reason.
- A world transfer is bounded by `WorldTransferLimits` (30 s without a chunk, 300 s in total).

Any of these ends the session with a `DisconnectReason`, which `PlayScene.SessionEndReason` surfaces. `Game` then returns to the connection screen (see Startup Flow above).

### Persistence write path

Persistence is hidden behind the ports in `Dream.Simulation.Persistence`; the SQLite implementation is `Dream.Server.Persistence`. The database runs in WAL mode (`SqliteSchemaBootstrapper.EnsureSchema`). Immediate writes go through `PersistenceWriteQueue`, whose worker commits them in groups of up to 256 per transaction (`PersistenceWriteQueue.MaxWritesPerGroup`). Every 5 s (`DedicatedServerHost.cs:931-933`) the host captures a `PersistenceCheckpoint` under the authority lock, mostly incremental (a full sweep every 12th capture, `WorldSessionRuntime.DefaultFullCheckpointInterval`), and `BackgroundCheckpointWriter` writes it off the tick thread. Shutdown drains the queue and writes a final full checkpoint under one 30 s deadline (`PersistenceWriteQueue.DefaultDisposeDrainDeadline`). The schema and details are in `docs/server-persistence-ddl.md` and `docs/performance-and-optimizations.md`.

### Worlds as rows (persistence wave B)

Persistence wave B (plan `docs/ledger/plans/2026-10-29-wave-b-plan.md`) turned the one implicit world into rows of a `worlds` table (migration `0003_worlds.sql`, schema version 3). The schema is in `docs/server-persistence-ddl.md`, Worlds.

- **One process, one world.** A database can hold many worlds, but a server process opens exactly one. The world is chosen by `--world <id|name>` on the `--server` command line, else by `DREAM_WORLD`, else it is the default (world 1, else the lowest id, else a new world named `default`). An id must exist; a name is opened, or created with the new-world size. `WorldNotFoundException` makes the dedicated server exit with code 4. `docs/configuration.md` lists the options and exit codes.
- **Catalog.** `IWorldCatalog` (`Dream.Server.Worlds`; `SqliteWorldCatalog` in `Dream.Server.Persistence.Worlds`) lists, creates, copies, deletes and resolves worlds. `WorldSelector.Parse` turns the argument into an id or a name. `WorldCli` implements the `--worlds list | create | copy | delete` commands, which run against the database and exit without starting a server.
- **World-bound repositories.** One repository instance serves one world: the world id is given at construction and bound as `$worldId` in every statement. The aggregate interfaces (`IPlacedObjectRepository` and the rest), `IWorldPersistenceRepository`, `IPersistenceCheckpointWriter` and `RuntimePersistenceBridge` are unchanged; the SQLite implementations take `(IWorldStore store, WorldId worldId)`, and the constructors without a world id mean world 1. `ServerPersistenceBundle` carries the `WorldId` and the `WorldDescriptor`, and `RuntimePersistencePorts.World` passes the descriptor to the runtime.
- **Descriptor and runtime.** `WorldDescriptor` (`Dream.Simulation.World`) is the public identity of a world: id, name, width, height, depth, map source and generator key and version. `WorldSessionRuntime` builds its map from the descriptor (`CreateMap(WorldDescriptor)`) and exposes it as `World`; `WorldDescriptor.Default` builds exactly the map `CreateMap(56)` always built, pinned by a golden test. `RuntimeAuthoritativeSessionHost` puts the descriptor in every `JoinAccepted` (protocol v7, below).
- **Keys.** All seven per-world tables are rowid tables with a composite primary key starting with `world_id`, including the busy ones (cubes, rubble, enemies). They are not `WITHOUT ROWID`: B1 measured that as slower (about 10% on a World10k full checkpoint, against about 4% for a rowid table with the composite key index).
- **Concurrent first start.** `SqliteSchemaMigrator.Migrate` reads the schema version once without a lock and again inside the migration transaction, which is IMMEDIATE (it waits for the write lock through `busy_timeout`). Two processes starting on an unmigrated file therefore migrate it once: the second waits, sees the new version and does nothing. The pre-migration backup is taken under that lock, through a separate read-only connection, because SQLite's backup API reports `database is locked` when its source connection holds the write transaction.
- **Seed and time.** The simulation seed and the world clock moved from `world_metadata` onto the world's row (`worlds.seed`, `worlds.world_time_ticks`). `SqliteWorldMetadataRepository` keeps its public API and reads and writes that row. Rolls do not change: a migrated world keeps the checksums of its pre-migration seed and time (`WorldsLockstepDeterminismTests`). The server logs a world's name and id, never its seed.
- **Floor size is only the size of a new local world.** The HUD floor size no longer resizes a live session: the setting is staged and persisted, and is used as the size of a world the integrated server creates (`ServerStartOptions.NewWorldSize`). The map size of a session comes from the world descriptor, on the server and on every client.
- **Documented behaviour changes.** Item ids and the maximum item id are per world. Inventory does not travel between worlds. v6 clients are refused. There is no lock between processes: stop the server before `--worlds copy` or `--worlds delete`.
- **Safety net.** `MultiWorldIsolationTests`, `WorldRestartRestoreLoopbackTests`, `WorldSizeMismatchLoopbackTests`, `V2DatabaseWithDataStartupTests` and `WorldsLockstepDeterminismTests` cover isolation of two worlds in one file, the restart of world 2 over loopback, a client floor size that differs from the server's world, the startup of a played-on v2 database and lockstep checksums of a world and its copy.

### Object ids and actor ids (persistence wave C)

Persistence wave C (plan `docs/ledger/plans/2026-10-29-wave-c-plan.md`; schema in `docs/server-persistence-ddl.md`, Stable object ids) gave every persisted world object a 64-bit `ObjectId` that is never reused. It sits beside the int actor id and does not replace it.

- **`ObjectId` versus actor id.** The actor id is the runtime identity: it is on the wire, in `SimulationStateChecksum` and `WorldStateChecksum`, in snapshots and in RNG seeds, and the protocol stays at v7. The `ObjectId` (`Dream.Simulation.World.ObjectId`, a `readonly record struct` over a `long`, `None` = 0) is the persistence identity of a placed cube, a rubble piece, an enemy or an item. It never enters a checksum, a snapshot or a seed. Rubble piece ids are server-only.
- **One allocator.** `ObjectIdAllocator` is owned by `WorldSessionRuntime` and used on the tick thread only. `RuntimePersistenceBridge` takes ids for new cubes, rubble pieces and enemies (a respawned wolf is a new object with a new id); `PlayerInventoryService` takes ids for items from the same allocator, so an id is unique across kinds. The counter is stored in `worlds.next_object_id` and is kept above every inserted id by `AFTER INSERT` triggers, so no id is reissued after a crash. Without a persistence repository nothing is allocated through the bridge, and item ids start at 1.
- **Identity map.** `ObjectIdentityMap` (one per runtime) binds an `ObjectId` to the actor id of a live cube or enemy; their actor ranges are disjoint, so one map serves both. The bridge binds on placement, spawn and restore, removes the binding on delete, and checkpoint capture resolves a cube or enemy through it: a cube is captured when its actor id has a binding, whatever its owner, and an enemy without a binding is skipped. A fractured cube's id is kept as the rubble's origin id, and each piece has its own id (the bridge's fracture group holds the origin and the piece ids by piece index).
- **Restore uses compact actor ids (a deliberate change).** Restore no longer reuses the persisted integer as the actor id. Cubes are registered in `ORDER BY object_id` order with a new actor id (from 1000) and bound; enemies take the next enemy actor id (from 2000) in the same order and are bound. After a restart, restored cubes and enemies therefore have different actor ids than before it, and rolls seeded by actor id differ from wave B. No golden pins this, and registration order is unchanged because migration 0005 keeps the `cube_id` order. The old workarounds (reserving legacy cube ids that fall in the enemy range, and re-identifying an enemy that shared an id with a cube) are gone.
- **Items on the shared allocator.** `item_instance_id` keeps its column name and holds an ObjectId value (the C# type stays `long`). `GrantItem` takes an id from the allocator, loading an inventory reserves its ids, and the startup `GetMaxItemInstanceId` seed is replaced by the allocator seeded from `worlds.next_object_id`. In a persistent world, item ids now interleave with the ids of other kinds.
- **Owners are optional.** Deleting a player profile sets `owner_player_name` to NULL on that player's cubes and rubble instead of deleting them; the buildings stay, are restored without an owner, are checkpointed and can be damaged.
- **Safety net.** `ObjectIdRestartLoopbackTests`, `ObjectIdCrashSafetyTests`, `BuildingsOutliveBuilderTests` and `WaveCLockstepDeterminismTests` (in `dream.Tests/Integration`) cover ids over a restart, a fracture, a respawn and a checkpoint, a crash, buildings that outlive their builder, and lockstep servers with a restart in the middle.

### Map sources, terrain overrides and chunks (persistence wave D)

Persistence wave D (plan `docs/ledger/plans/2026-10-29-wave-d-plan.md`; schema in `docs/server-persistence-ddl.md`, Terrain as data) makes terrain data that comes from a source and can be edited and exported. The protocol stays at v7.

- **Map sources.** `Map` is built from an `IMapSource` (`Dream.Simulation.World.MapSources`): `MapDefinition` (width, height, depth, `WorldMapSource`, generator key and version) plus `CreateCells()`, which returns NEW `GeometryCellRecord`s in a canonical insertion order each call. `GeneratedMapSource` delegates to `WorldGenerator.CreateStartupGeometry` unchanged, so the old `Map` constructor and every golden keep their behaviour. `MapSources.ForWorld(world, seed)` picks the source for a world: generated worlds go through the `WorldGeneratorCatalog` (`IMapGenerator`, key `default`, version 1); `stored` takes the world's chunks as a third argument (wave E). `WorldSessionRuntime.CreateMap` uses it; on a generated world the client's `ClientWorldSession` still generates its own base terrain, and on a stored world it builds the map from the chunks the server sends (persistence wave E, below).
- **Source cells.** `Map.IsSourceCell(cell)` is true while a cell (Y > 0) holds the record it got from the source. `Map.RemoveSourceCell(cell)` removes a source cell through the live removal path (`RemoveCube`) without leaving rubble, and `Map` remembers which source cells were removed (`CopyPlacedCubesTo` replays them on a rebuilt map).
- **Overrides.** A terrain cube has no owner, so it used to come back after a restart. When combat destroys a source cell, `AuthoritativeCombatResolver` calls `IAuthoritativeCombatHost.PersistTerrainCellRemoved` (after the persisted-cube delete and before `PersistFracturePieces`); the bridge takes an `ObjectId`, enqueues a `terrain_cell_overrides` row and uses that id as the rubble's origin. `RestoreWorld` applies the overrides with `RemoveSourceCell` in `object_id` order before restoring cubes, rubble and enemies. Terrain health is not stored (a damaged standing cube heals on restart). Two servers restarted from the same database therefore build identical maps; a server that fractured a cell live and one that restarted with the override differ in physics body order (Bullet swap-removes), which is accepted.
- **Chunks.** `TerrainChunkExporter.Export(IMapSource)` (or `Export(Map)`, which writes only source cells, so removed cells are baked out and placed cubes and rubble are left out) encodes the terrain into 16 x 16 column chunks (`TerrainChunkCodec`, blob format v1, documented in `docs/server-persistence-ddl.md`). `ITerrainChunkRepository.ReplaceTerrainChunks` stores them in `terrain_chunks` in one transaction. `StoredMapSource(MapDefinition, chunks)` validates coverage and format version and decodes them in canonical (x, z, y) order: a map built from it has the same cell set, `WorldChecksum` and collision-body multiset as the generated map it was exported from, but a different insertion order; two maps built from the same chunks are identical. Hosting stored worlds and sending terrain to clients is wave E (below).
- **Safety net.** `TerrainPersistenceLoopbackTests` (destroy terrain and a sculpture cell, restart, a joining client matches), `TerrainLockstepRestartTests` (two lockstep servers with terrain fractures and two restarts each) and `StoredTerrainRoundTripTests` (export, SQLite, load, `StoredMapSource`, map equality) in `dream.Tests/Integration`; `TerrainMeasurementTests` in `dream.Tests/Load`; the map-source goldens in `MapSourceGoldenTests`.

### Stored worlds and the terrain transfer (persistence wave E)

Persistence wave E (plan `docs/ledger/plans/2026-10-29-wave-e-plan.md`; schema notes in `docs/server-persistence-ddl.md`, Terrain as data; commands in `docs/configuration.md`, Managing worlds) lets a server host a world whose terrain is stored chunks, sends that terrain to clients at join, and adds offline export and reset. The protocol goes to v8 for every world. There is no schema change (still version 6).

- **Hosting.** `WorldDescriptor.Validate` accepts `MapSource.Stored` (provenance: a non-blank generator key and a version of at least 1; on a stored world they record where the terrain came from and nothing checks them against the catalog). Stored worlds keep the hosting limits: square, 5 to 100, height 8. `ServerBootstrap` loads the world's chunks once through `SqliteTerrainChunkRepository.LoadTerrainChunks()`, checks them with `TerrainTransferPayload.Create`, logs `World '<name>' (id n) stored terrain: chunks= bytes= sha256=`, and hands them to the runtime (`ServerPersistenceBundle.StoredTerrain`, `RuntimePersistencePorts.StoredTerrain`, `WorldSessionRuntime(... storedTerrain)`). Missing, partial, corrupt or over-limit chunks exit with code 5. `WorldSessionRuntime.CreateMap` calls `MapSources.ForWorld(world, seed, StoredTerrain)`, which builds a `StoredMapSource`. `RebuildMapForFloorSize` throws on a stored world: stored worlds cannot be resized.
- **Same bytes, same map.** Server and client build the map from the same blobs through `StoredMapSource`, so cell order and physics-body order are identical, which is stronger than on generated worlds (where only the cell set and checksum are equal). `TerrainChunkCodec.Decode` has a decode budget for untrusted input: it subtracts each subcell override set's size before allocating and throws `InvalidDataException` when the budget (`WorldTransferLimits.MaxTerrainSubcellOverrides`, 1,048,576, about 4.3x World10k generated terrain's 246,382 overrides; a blob just inside it costs the client about 210 MB of transient allocation over a baseline join, where the former 4,194,304 cost about 965 MB) would run out. A second budget, `WorldTransferLimits.MaxTerrainSubcells` (4,194,304), charges resolution^3 for every decoded record that is not a plain subdivision-free `SolidCube` (those take `Map`'s coarse path; everything else makes `Map` walk every implied subcell for render blocks and collision, however few overrides the blob carries) before the record is added. The default generated World10k terrain totals 1,667,072 (pinned in `TerrainChunkCodecBudgetTests`), so the limit is about 2.5x that; Slope cells at resolution 40 just inside it cost about 94 MB over a baseline join. Wire resolutions stay 1 to 40 (tests and tools use others); the budget, not a resolution whitelist, bounds the cost.
- **The payload.** `RuntimeAuthoritativeSessionHost` builds one immutable `TerrainTransferPayload` per host, not per join and never on a tick, from the chunks as loaded (the base chunks byte for byte, not re-exported). Chunks are sorted by (chunk_z, chunk_x) and grouped greedily into fragments of whole blobs up to 48 KiB raw (at least one blob each); World10k is 49 chunks, 215,996 B raw and 5 fragments. `TerrainTransferManifest` (`chunks`, `fragments`, `bytes`, `sha256`) travels in `server.join-accepted` as `terrain`. The digest is SHA-256 over, for each chunk in (z, x) order, `i32 x, i32 z, u16 formatVersion, i32 cellCount, i32 dataLength, data`, little-endian, as lowercase hex. A generated world sends no manifest, so its join stays byte-identical to C0's frames (pinned by `StoredWorldJoinLoopbackTests`).
- **v8 order on one connection** (a stored-world join):

```
server.join-accepted   carrier: "worldTransferId":n, "world":{...,"mapSource":"stored",...},
                       "terrain":{"chunks":49,"fragments":5,"bytes":215996,"sha256":"..."}
server.terrain-chunk   {"transferId":n,"index":0,"chunks":[{"x":0,"z":0,"v":1,"n":312,"data":"<base64>"},...]}
  ...                  (queued messages interleave as for world chunks and are held by the client)
server.terrain-chunk   {"transferId":n,"index":4,"chunks":[...],"final":true}
server.world-chunk     index 0 .. final, unchanged
```

  Terrain frames come after the carrier and before world chunk 0; there is no separate channel and no change to the join handshake. `data` is standard base64 written with `Utf8JsonWriter.WriteBase64String` (no `+` escaping), about 64 KB per frame, about 288 KB for World10k (the 4/3 overhead is inside the 300,000 B gate). Resyncs (`server.snapshot` carriers) never carry terrain. Removals of source cells (overrides) reach the client through the join snapshot, as on generated worlds.
- **Client side.** `WorldTransferReassembler` validates every terrain frame (shape, order, grid bounds, per-blob and total limits, the running counts, the `final` flag and the incremental digest against the manifest) and delivers `JoinAcceptedMessage.ReceivedTerrain` with the completed join; a violation ends the session as a protocol error. `ClientJoinAcceptedApplier` requires `ReceivedTerrain` for a stored world, builds `StoredMapSource(MapDefinition.ForWorld(d), terrain, MaxTerrainSubcellOverrides)` decodes the source once, inside its try and before touching the runtime, so a corrupt or hostile body ends the join as a `Protocol error: stored terrain ...` and leaves the existing map and physics bodies intact, and then calls `IClientWorldState.RebuildMapForWorld(d, source)` (a generated join also rebuilds when the current map is stored, even at the same size); the limits are the session's configured `WorldTransferLimits`; `ClientWorldSession` resets the map bodies and builds a new `Map` from the source without `CopyPlacedCubesTo` (the join snapshot follows immediately and would otherwise replay the old map's removals onto another source). A resync whose dimensions differ on a stored world throws `InvalidDataException` instead of rebuilding a generated map; `PlayScene` applies snapshots through `RemoteWorldMutationApplier.TryApplySnapshotToRuntime`, which turns that (and the non-square-world check, also an `InvalidDataException`) into a clean session end with a protocol-error reason instead of an exception escaping the frame. Clients do not cache terrain: a reconnect downloads it again.
- **Edits behave as on generated worlds.** Overrides are keyed by cell and use `Map.IsSourceCell` / `RemoveSourceCell`, which do not depend on the source, so wave D's persistence path applies unchanged. Terrain cubes have no persisted placed-cube row, so wave E adds no new path into `RepairCubeRecordFromPersistence`. Terrain health is still not stored.
- **Export and reset (offline).** `--worlds export <src> <new name> [--terrain-only]` always creates a new world: `WorldTerrainBaker` builds the source's map (generated, or its own chunks for a stored world), applies the `Removed` overrides, runs `TerrainChunkExporter.Export(map)` and validates the result with `TerrainTransferPayload.Create`; `IWorldCatalog.CreateStored` writes the world and the chunks in one transaction. `--worlds reset-terrain <id|name> --yes` (`IWorldCatalog.ResetTerrain`) restores the world's own base: it deletes every override and every rubble row of that cell, except cells that hold a saved placed cube, which keep their override and rubble and are reported. Both commands are offline: there is no lease that detects a running server (a hosting lease belongs to Phase 5), so a later checkpoint or the restore heal of a running server can silently undo them. The rule is documented and printed.
- **Not in wave E.** New generators, offline time, persisting terrain health (decision U2), cell-matching repair in `RepairCubeRecordFromPersistence`, `Replaced` overrides (still logged and skipped), a client terrain cache, a live reset, a hosting lease, resizing stored worlds.
- **Safety net.** In `dream.Tests/Integration`: `StoredWorldEditsRestartTests` (a fracture on a stored runtime writes one override plus the pieces; restart; a placed cube in a removed cell; two lockstep servers with two restarts), `StoredWorldSoakTests` (3 x 3,000 ticks on a 30 x 8 x 30 stored world; the `DREAM_LOAD_TEST` variant is World10k, 115,200 ticks and 6 restarts), `StoredWorldJoinLoopbackTests` (a client joins a stored World10k over TCP; a generated join's frames and bytes equal the C0 capture) and `TerrainResetLoopbackTests`. In `dream.Tests/Load`: `StoredWorldMeasurementTests`. The baseline is `docs/ledger/work-log/2026-10-29-persistence-wave-e-baseline.md`.

### Persistence schema and repositories (wave A)

Persistence wave A (plan `docs/ledger/plans/2026-10-28-wave-a-plan.md`) put the SQL layer on versioned migrations and per-aggregate repositories. Its schema semantics and runtime behaviour are unchanged, apart from the two points marked below.

- **Migrations are the source of truth.** The schema is the ordered scripts in `dream/config/migrations/NNNN_snake_case.sql`; `0001_baseline.sql` is the schema every existing database already has (the old `player-profiles.init.sql` plus `world_metadata`). `SqliteSchemaBootstrapper.EnsureSchema` sets WAL, then `SqliteSchemaMigrator` (`Dream.Server.Persistence.Migrations`) applies the pending migrations in one transaction and records each in the `schema_version` table (`version`, `name`, `applied_utc`). A database without that table is at version 0. Before it migrates a database that already holds data, it backs the file up next to it as `{db}.v{from:D4}-to-v{to:D4}.{yyyyMMddTHHmmssZ}.bak`. A database at a version newer than the build throws `SchemaTooNewException` and is left untouched (the dedicated server logs it and exits with code 3). A missing `migrations` directory fails startup, and a migration that ends the transaction itself fails it too. `player-profiles.init.sql` is frozen as the legacy test fixture.
- **Behaviour change: the formula seed runs once.** The baseline runs once instead of on every startup, so a deleted `combat_formulas` seed row is no longer restored on restart. Edited formulas were already kept.
- **Store.** `IWorldStore` / `SqliteWorldStore` (`Dream.Server.Persistence.Store`) opens a `SqliteSession`. `OpenSession()` enlists in the thread's active `SqliteWriteBatch`, so queued writes still group-commit, and otherwise owns an autocommit connection. `BeginOwnedTransaction()` never enlists; the checkpoint writer uses it. `SqliteRepositoryBase` and `SqliteCommandExtensions` hold the shared binding and reading helpers, with state-passing overloads so hot paths allocate no closures.
- **Per-aggregate repositories.** `IWorldPersistenceRepository` inherits `IPlayerWorldStateRepository`, `IPlacedObjectRepository`, `IRubbleRepository`, `IEnemyRepository`, `IItemRepository` and `IInventoryRepository` (all in `Dream.Simulation.Persistence`). Each has a `Sqlite*Repository` in `Dream.Server.Persistence.Repositories`. `SqliteWorldPersistenceRepository` remains, as a facade that forwards to them; a new repository method must be forwarded there too, because the wave H methods have do-nothing default bodies for test fakes (`EquipmentSqliteActionTests` fails when one is missing). The profile, combat-formula and world-metadata repositories sit on the same store.
- **Checkpoint writer.** `SqlitePersistenceCheckpointWriter` holds no SQL: it opens one owned transaction and calls each repository's `Checkpoint*` method, which prepares one command per table and reuses its parameters.
- **Behaviour change: the checkpoint `updated_utc` guard.** Both player row kinds anchor on the leave's `player_world_state` row. A checkpoint's world-state row carries its capture time and replaces only a strictly older row. Its profile vitals (`interaction_mode`, `current_hp`, `max_hp`, captured at `PlayerCheckpointVitals.CapturedUtc`) are skipped when `NOT EXISTS (... player_world_state.updated_utc > $capturedUtc)` fails, that is when the player left after the capture. The profile's own `updated_utc` is not compared: queued profile saves stamp the worker's time, which would make the result depend on queue lag rather than on the script. The vitals update stamps the profile with the write time; `player_world_state` carries a capture time (the checkpoint's, or the leave's tick-thread time). The guard needs `DetachRemoteSession` to queue the world state before the profile, so a checkpoint committing between the two skips. A clock stepping backwards leaves a future-stamped row that makes checkpoints skip that player until real time catches up; the next leave repairs it. `RepositoryPersistenceCheckpointWriter` applies the same rules. Enemies, cubes, rubble and `world_time_ticks` are unguarded. Details are in `docs/server-persistence-ddl.md`.
- **Safety net.** `WorldRoundTripCharacterizationTests` writes through every facade, profile and metadata path, reopens the file through a fresh bootstrapper and compares every field, including the stored timestamp formats. `DependencyDirectionTests` forbids `Microsoft.Data.Sqlite` under `dream/Simulation`, so the ports stay storage-agnostic.

### Equipment and rolled items (wave H)

Plan: `docs/ledger/plans/2026-11-01-wave-h-plan.md`.

- **Slots.** A player has four equipment slots: rows 100..103 of the slot map (`EquipmentSlots`), for main hand, off hand, head and body. A row holds the id of an item that also has a backpack row, so moving or swapping backpack slots never touches equipment rows. Allowed slots come from `equipSlots` in `items.json`. An unknown kind, or an item in a slot it may not occupy, is inert: no stats, no light, row kept.
- **Actions.** `PlayerInventoryService` validates equip, unequip, move and destroy on the server, and a rejection changes nothing and queues nothing. Writes that belong together share one queued closure, so the queue's savepoint makes them atomic. Destroy names the item, so a queued destroy cannot hit what moved into the slot, and it is rejected while the item is equipped. Auto-equip runs only on first sight of an inventory, so an empty hand stays empty.
- **Budget.** Each session has an `InventoryActionBudget` on `ConnectedPlayer`: burst 16, 8 a second, in server ticks. Only an action that would write spends it. The service calls `ActionGate` after validation; `WorldSessionRuntime` binds the gate once and `RunInventoryAction` names the session around each call.
- **Rolls.** `item_instances.rolled_modifiers` (migration 0008) is versioned JSON (`ItemRollCodec`) written when the item is created and never changed after: the upsert does not name the column. NULL is an unrolled item, which reads its definition. A roll replaces the definition's modifiers, so a later content change does not alter a rolled item. Row content is treated as hostile: a bad value parses to `ItemRoll.Damaged`, which is inert and counted by the integrity check. `ItemRoller` uses a fresh `DeterministicRandom` per item, so no shared stream advances.
- **Effective stats.** `PlayerCombatant.ApplyEquipment` sets all four sources (0 main, 1 off, 2 head, 3 body), then derives once. Actors on the wire carry base stats. The client computes its effective stats from the inventory snapshot alone (`PlayScene.ReadEquipment`) and reads no item catalog for stats or lights. The server sends inert items with no modifiers and no light. Armor is an effective stat that changes no combat result yet.
- **Digest.** `ActorSnapshot.ModifierDigest` is a `[JsonIgnore]` hash of the rolled modifiers of equipped items, in slot order, cached per player with the held light (`RefreshEquippedSummary`). `HashActor` mixes it in only when non-zero, so default items leave checksums unchanged. Clients decode 0, so server and client hashes differ for a player with rolled gear; `HashActor` has no production caller outside `Simulation/Determinism`.
- **Held light.** One light per player: the off hand's, otherwise the main hand's (`EquipmentSlots.ResolveHeldLight`), on server and client.
- **Protocol v10** adds `InventoryUnequipIntent`, `InventoryMoveIntent`, `InventoryDestroyIntent`, a target on `InventoryEquipIntent`, and the item fields of `InventoryItemSnapshot`. Nothing from a client carries a stat, rarity, kind or new item id. The starter kit's `server.join-accepted` frame is 4,048 B (gate 4,099 B; `StoredWorldJoinLoopbackTests`).

### Enemies, armor, respawn and loot (wave I)

Plan: `docs/ledger/plans/2026-11-01-wave-i-plan.md`.

- **Armor.** `PlayerCombatant.ApplyDamage` mitigates: damage taken is `max(1, round(amount * factor))` with `factor` from the formula `player.damage_taken_factor`, clamped to 0..1. Armor 0 or less returns the amount unchanged and evaluates nothing. The factor is cached and recomputed only when `EffectiveArmor` changes, because the formula service allocates and the client derives on every snapshot. Enemies have no armor.
- **Enemy stats.** `EnemyFactory` is the one place an enemy is built. A spawn takes Strength, Vitality, Attack Speed and Level from its template into a `StatSheet` and its row. A restore reads them from the row. Damage and interval come from the `enemy.*` formulas. Aggro, attack range, body, visual and loot always come from the template.
- **Population.** Day target 1, night target 2. With no active enemy, the first `Primary` template spawns at the day spawn. With one active at night, the role that is missing spawns at the night spawn. Dawn removes the highest owned `NightExtra`, otherwise the highest owned id. The shipped `NightExtra` is the Dire Wolf.
- **Dead players.** A dead seat simulates the starved frame: no movement or use, gravity still applies, facing and mode stay. Build and combat actions are rejected after the sequence step ("Action rejected because the player is dead."), for synthesized and server-queued use alike. A client cannot send them at all: `PlayerActionHandler` refuses them before the queue. A held button on a dead seat never reaches the gate, because the starved frame has no use. Inventory, stats and mode stay allowed. Enemies ignore dead targets.
- **Respawn.** `current_hp = 0` is the persisted state and the timer is not stored. `ConnectedPlayer.RespawnAtTick` holds it for the seat, because the primary `Player` outlives its sessions. `AdvanceRespawns` scans once per gameplay tick after the enemy step, primary then ordinal session order. A remote seat first seen dead at tick T returns at T + `PlayerRespawnRules.DelayTicks` (320) at the world spawn, moved up out of any solid cell and any rubble piece of a fractured cube, with full health at the maximum of that moment. Joining dead, or leaving and rejoining, starts a fresh wait. The local seat is never respawned. Health is per world, so a character dead in world A arrives full in world B and waits its 5 s back in A. Dying costs at most 5 s, so hopping wins nothing.
- **Wolf bite and spawn protection.** A chasing enemy may stop no farther than `WolfDefinition.PreferredStandOffDistance` (1.20 m, at most 90% of its attack range) from the player, but in practice it stops on the first tick within the 1.35 m range (a step is about 0.044 m, so about 1.3 m), where it bites; a player who stands still is bitten. A bite needs the player within the horizontal range, within vertical reach (`EnemyActor.BiteVerticalReach`, 0.5 m between the two bodies' vertical extents: a player on a 1-cube step is in reach, one on a 2-cube step, a pillar or above a floor is not) and no cube on the horizontal line between them (cubes below the player's feet do not count). Wolves cannot climb, so a 2-cube step is a known safe spot. A wolf with no target, or none in aggro range, walks back to its home (its spawn point) and idles there. Day wolf spawn is the far corner of the map (3.5 m in from both far edges), the night spawn 2.5 m in, so a new player at the world spawn (5, 5) is outside aggro range on maps of about 16 m and up. A first spawn, every join and every respawn set `ConnectedPlayer.ProtectedThroughTick` (tick + `PlayerRespawnRules.SpawnProtectionTicks`, 192), which makes the enemy step skip that player, so enemies neither target nor damage it; its own combat use ends it early. Counted in ticks, not persisted, no protocol field. The content catalog rejects an `attackRange` that would put the stand-off inside `PlayerSeparationDistance`.
- **Loot.** `ResolveEnemyHit` calls `GrantLoot` after `DeletePersistedEnemy`, so a crash can lose a drop and never duplicates one by restoring the enemy. It skips a character whose inventory is not loaded, because `Ensure` could flush for 5 s on the tick. `LootRoller` is pure, with the salt `loot-rol`, and its seed is counters only: `ItemRollSeed(SimulationSeed, tick, StableHash.Of(session), enemy actor id, dropIndex)`. A roll happens even with a full backpack.
- **Inventory push.** `InventoryPushMessage` (`server.inventory-push`) is not an action result, because kills happen inside the tick golden, which hashes every result. Notices coalesce per session, capped at 8, and the host drains them at the end of `RunTick` into `_pendingConnectionReplies`, which `DeliverPendingJoinReplies` sends before the snapshot and never holds back. A loss sends `Inventory = null`.
- **Push ordering.** An action result waits for the next snapshot and carries the inventory of its own tick, while a push goes out at once, and the client keeps the inventory it applies last. So `ServePendingInventoryPushes` leaves a push pending while an `InventoryChangedResult` is queued, and builds the snapshot when it sends, so the push is always the newest. A world resync is a snapshot with no inventory, so it does not interact with a push in either order. During a world transfer the client holds every message behind the carrier and releases them in order, so a push always follows the join it belongs to.
- **Enemy wire group.** `ActorFieldGroups.EnemyPresentation = 512`, not implied by `Full`, carries `enemyLevel` and `visualKey`. It is sent only when the level is not 1 or the visual is not the default, so a wolf-only world has the same actor bytes as before. `HashActor` mixes them only when non-default. `EnemyLevel` 0 on the wire means level 1. The client clamps it to 1..9999 and draws an unknown or oversized `visualKey` as the wolf.
- **Client.** `ClientDeathTracker` counts down from the first dead snapshot, for display only. Commands keep flowing while dead, with gameplay input off, because the reconciler compares only on a new ack. Dead remote players are hidden, and their interpolation buffer is cleared on dead to alive, so a respawn does not slide. The client no longer reads `ContentCatalog` for a remote session.
- **Tint.** `enemyFragment.glsl` multiplies base colour by `max(1 + uActorTintOffset, 0)`. The player renderers share this program and its MASK variant. `EnemyRenderer` writes the base program, `AnimatedModel.ActorTintOffset` carries the value to the MASK variant on each program switch (`BindProgram`), and `ResetTint` zeroes all of it after the enemies. A frame draws enemies first, then players.
- **Protocol v11, schema 9.** Migration 0009 seeds four formulas and rebuilds nothing.

### Combat text and protocol v12

`DamageEventDelta` gains a trailing `HitKind` (`FloatingCombatTextKind`), copied from `DamageAppliedEvent.Kind` in `WorldSessionRuntime.Replication.cs`. The JSON key is `hit`, written only when the value is not Regular, so a non-critical snapshot differs from v11 only in the version digit, and the client reads an undefined value as Regular. `CombatResolutionResult` does not change, so the tick goldens do not move. There is no schema change. The attacker's client now publishes one `DamageAppliedEvent` per hit (the snapshot applier; the action applier no longer does), and `CombatFeedbackSystem` turns a hit on the local actor into Incoming on the client.

## Namespace And Source Boundaries

Production code uses the `Dream` root namespace with folder-matched file-scoped namespaces. The current migration keeps one executable project first, while making ownership readable from source paths:

- `Dream.Application.*` owns process startup, window shell, settings, and app-level runtime state.
- `Dream.Client.*` owns desktop startup, input, presentation, replication intake, gameplay scene composition, and remote client networking.
- `Dream.Server.*` owns headless server bootstrap, the dedicated host/service loop, and the SQLite persistence implementations (`Dream.Server.Persistence`).
- `Dream.Multiplayer.*` owns shared protocol, transport, session, and diagnostics seams.
- `Dream.Simulation.*` owns authoritative world, player, enemy, combat, physics, and runtime state. It also owns the combat-formula rules (`Dream.Simulation.Rules`) and the persistence ports and records (`Dream.Simulation.Persistence`: repository interfaces, `Persisted*` records, `PersistenceWriteQueue`, `IPersistenceWriteBatch`).
- `Dream.Rendering.*` owns OpenGL, model loading, world/actor/HUD/viewmodel rendering, and visual asset definitions.
- `Dream.Assets.*`, `Dream.Configuration.*`, and `Dream.Utilities.*` hold narrow cross-cutting helpers.

Dependency-direction tests keep server sources from referencing client/rendering namespaces and keep protocol sources free from presentation/rendering dependencies. A future multi-project split should happen only after these folder and namespace boundaries stay stable.

### Simulation does not reference Server (ARCH-2)

Simulation is the lower layer; Server implements and composes it. `DependencyDirectionTests` enforces the rule: no source under `dream/Simulation` may reference `Dream.Server`, `Dream.Client`, `Dream.Rendering`, `Dream.Application`, ImGui, `Microsoft.Data.Sqlite`, or OpenTK windowing/graphics (or `Dream.Configuration`/`GameSettings`). The same tests keep client sources off persistence and server rules, and `ClientSources_ReferenceServerOnlyFromAllowlistedFiles` pins the exact client files that may reference `Dream.Server`: `Networking/RemoteClientOptions.cs`, `Networking/RemoteClientSession.cs`, `Scenes/PlayScene.cs`, `Startup/ILocalServerHandle.cs`, `Startup/IntegratedLocalServerHandle.cs`, `Startup/NetworkSessionCoordinator.cs` and `Startup/OnDemandLocalServerHandle.cs`. The list is a ratchet: a file that stops referencing the server must leave it, and none may be added.

How the seam is wired:

- The combat-formula rules (`CombatFormulaService`, `CombatFormulaSet`, the expression engine) live in `Dream.Simulation.Rules`.
- The persistence ports and records live in `Dream.Simulation.Persistence`. `RuntimePersistencePorts` (in `Dream.Simulation.Runtime`) bundles the repositories, formula service, checkpoint writer, `PersistenceWriteQueue`, simulation seed and an owner to dispose. `RuntimeAuthoritativeSessionHost.CreateDefault(RuntimePersistencePorts?)` takes it, and a host created without one persists nothing.
- `ServerBootstrap.CreateRuntimeHost(ServerPersistenceBundle?)` builds the ports from the server's SQLite bundle (`CreateRuntimePersistencePorts`) and creates the host.
- `PersistenceWriteQueue` runs its writes inside an `IPersistenceWriteBatch` from `WriteBatchFactory`. The default is a pass-through batch. `Dream.Server.Persistence.SqliteWriteBatch` replaces that default through a `[ModuleInitializer]` when the assembly loads, before any queue can start a worker. The initializer only sets a factory default inside the same assembly.
- The SQLite repositories, the store, the schema bootstrap and the migrations stay in `Dream.Server.Persistence` (with the `.Store`, `.Repositories` and `.Migrations` sub-namespaces).

### Join flow with accounts (wave G)

Plan: `docs/ledger/plans/2026-10-31-wave-g-plan.md`. Every dedicated and integrated host has an `IAccountRepository` (`SqliteAccountRepository`, global, not bound to a world).

1. `DedicatedServerHost.HandleClientMessage` checks the token (`AccountKey.TryFromToken`). A missing or malformed token and a blank name are refused at once, with no prefetch. The message goes on with `AccountToken = null` and `ResolvedAccount` set, so the token is held nowhere past this point.
2. The throttle runs, then the join is queued and its prefetch started, keyed by (name, account). The prefetch reads only: `Lookup` first, then the profile, world state and inventory under the character's key.
3. When the prefetch is ready, `JoinRequestHandler` runs on the tick and reads only memory: `AuthorizeJoin` (`CharacterAccessRules.Decide`), the `AlreadyJoined` check, `TryClaimSession` with the holder's identity, then `JoinSession`. Ownership comes before the claim, so a stranger can never kick the owner.
4. `JoinSession` attaches the player, restores by key and calls `CommitCharacterAccess`, which queues `RecordJoin` behind the profile save that creates a new row. The write queue runs them in order on one connection and transaction, so a claim always finds its row. A claim or a creation drops every other prefetch for that character or account: those joins see `Missing`, reload behind the queued claim and read the new owner.

A failed claim write is dropped like any other queued write: the player plays on unowned and the next join claims. `DisplayName` is what people type and see; `CharacterKey` (`player_profiles.player_name`, ordinal, never changed) keys all persistence. Hosts with no repository, used by tests and tools, check nothing and behave as before, and the in-process two-argument `JoinSession` is trusted.

### Stale-session takeover (NETR-8)

This applies to hosts without accounts. On hosts with accounts, a takeover is immediate for the owning account and never happens for another one (see `docs/configuration.md`, Connection liveness).

A client that lost its connection (crash, network drop) often rejoins before the server has noticed the old socket is dead. `DedicatedServerHost` handles the rejoin by measuring the silence of the connection that holds the seat.

- Liveness: each `ServerConnection` records when its last complete inbound frame arrived, on the connection options' clock. Empty keepalive frames count. `CheckLiveness` also closes a peer that exceeds the idle timeout or stalls a write (see `docs/configuration.md`, Connection liveness).
- Takeover: a join for a session id, or a player name, that another connection holds takes the seat over when that holder has been silent for the takeover window, 8 s for either match (`DedicatedServerHost.TakeoverSilence`). There is one window because session ids are broadcast in snapshots and the desktop client picks a fresh `client-<guid>` on every connect: a shorter session-id window would only let a hijacker skip the name window. The old connection is released with its profile saved, the new one gets the seat and the saved profile, and `DedicatedServerService` closes the old transport.
- Live holder: a holder heard from inside the window keeps the seat. The join is rejected with `SessionInUseRejectionReason` (same session id) or the duplicate-display-name reason (same name), so a second client cannot hijack a live player.
- Join throttle: a join rejected because a live connection holds the session id or name throttles further joins for that contested session id or name from the same remote IP address, across reconnects: 2 s, doubling on each further rejection to 10 s. Throttled joins are never claimed or queued, so they cost no persisted-player prefetch. See the Join throttle row in `docs/configuration.md`.

## Main Object Relationships

- `Game` is the OpenTK window shell and lifecycle owner.
- `Game` now also owns top-level scene switching between the startup connection scene and gameplay.
- `StartupConnectionScene` is the primary multiplayer boot scene. It owns the host/port/player-name form state, uses `StartupMenuRenderer` for UI, and hands successful `RemoteClientSession` creation back to `Game`.
- `NetworkSessionCoordinator` owns startup connection-state transitions and keeps connect-task lifecycle out of both `Game` and `PlayScene`.
- `IntegratedLocalServerHandle` owns the desktop-local authoritative server lifecycle and exposes the loopback port used by the startup scene defaults.
- `PlayScene` is the active gameplay scene and current desktop composition root for per-frame update/render flow.
- `PlayScene` now composes `WorldSessionRuntime` for the client path, but keeps client-local ownership of render, HUD, scene events, viewmodel presentation, settings-driven input gating, and other desktop-only reactions.
- `PlayScene` now also owns `ClientProtocolSession` so future replicated-state intake stays behind a focused client protocol seam instead of turning the scene into a message router.
- `ClientScenePresenter` now owns remote-player presentation state and interpolation buffers on the client side, which keeps remote player world-space rendering separate from local camera, HUD, and first-person systems.
- `RemotePlayerRenderer` now owns world-space remote-player drawing and stays a sibling of `EnemyRenderer` rather than stretching `WorldRenderer` into an all-actor renderer prematurely.
- `WorldSessionRuntime` is the first extracted authoritative runtime seam. It owns simulation-facing state and order for the current desktop session: `Map`, `PhysicsIntegrator`, `Player`, `PhysicsSyncSystem`, `WorldObjectRegistry`, `EnemyRegistry`, `PlayerSessionRegistry`, accepted interaction mode, combat/build resolution, wolf lifecycle upkeep, and floor rebuild/player re-clamp behavior.
- Per-session and per-name state on a long-running server is bounded (SCALE-7): `PlayerSessionRegistry` lists only the active sessions and remembers the `MaxDepartedSessions` (1024) most recently departed ones for a reattach; the runtime's per-session interaction modes follow the registry (dropped through `PlayerSessionRegistry.SessionForgotten` when it forgets a session), and its profile cache keeps every active player's profile plus an LRU of the 1024 most recently departed player names (`RuntimePersistenceBridge.MaxDepartedProfiles`, forwarded by `WorldSessionRuntime`; an evicted profile was saved on detach and is read back on the next join). The in-memory inventory (`PlayerInventoryService._slotsByPlayer`, `_itemInstances`) holds only loaded inventories: startup loads none, and a player's inventory is read with its profile and world state by the join prefetch (`LoadPlayerInventory`, one player's rows through indexes, queued behind that player's pending writes), or by a synchronous read after a queue flush for a direct `JoinSession` caller. It is evicted together with its profile and `RuntimePersistenceBridge._knownProfileNames` entry when the name falls out of the departed-profile LRU, so the active players' and the 1024 most recently departed players' inventories are loaded. Inventory is written through as it changes; because the write queue drops a failed write (counting it in `FailedWrites`), an inventory with a write failure since its first queued write stays loaded instead of being evicted, and one evicted while its writes are still queued is held until a read queued behind them confirms none failed, and restored on the next load if one did. Item ids come from the shared `ObjectIdAllocator`, which the persistence bridge seeds at startup from the world's `worlds.next_object_id`, so new ids never collide with an inventory that is not loaded; loading an inventory reserves its ids (an id of 0 or below in a hand-edited row is loaded but not reserved, and logged). Cube owner names live in `RuntimePersistenceBridge._cubeOwnerNames`, which `IsValidPersistenceOwnerName` accepts without a repository read; it grows only with cube placements, not with joins.
- ARCH-3 step B moved three responsibilities out of `WorldSessionRuntime` into collaborators, each behind a narrow host interface. `PlayerInventoryService` (`Dream.Simulation.Inventory`) owns the loaded inventories and item instances. `AuthoritativeCombatResolver` (`Dream.Simulation.Combat`) resolves authoritative combat through `IAuthoritativeCombatHost`; it reads the map through a `Func<Map>`, so it follows `RebuildMapForFloorSize`. `RuntimePersistenceBridge` (with its `.Checkpoints` partial; `Dream.Simulation.Persistence`) owns world restore, the join prefetch, the profile LRU, write-through, checkpoints and the final checkpoint, through `IRuntimePersistenceHost`. `WorldSessionRuntime` keeps its public API and delegates through private forwarders.
- `RemoteSessionPlayerState` now provides a lightweight authoritative actor seam for additional joined remote sessions that do not yet own the full Bullet-backed `Player` path.
- `ServerTickRunner` and `SimulationClock` (`SimulationClock.AuthoritativeTicksPerSecond` = 64) provide the explicit 64 Hz authoritative scheduling seam around that runtime. The dedicated and integrated servers use it; the desktop client keeps a clock only for its prediction ticks.
- `DedicatedServerHost` is the headless composition root for protocol-side server work. It owns protocol routing, the per-session command inboxes, fixed-tick advancement of the runtime-backed host, the stale-session takeover and join throttle, and the 5 s persistence checkpoint cadence.
- `DedicatedServerHost` now drains a batched set of joined-session command frames per authoritative tick instead of only one primary-session command.
- `DedicatedServerService` now owns the long-running server loop: transport startup, client acceptance, connection lifetime tasks, 64 Hz tick advancement, and joined-session snapshot broadcast.
- `ServerConnection` now owns framed protocol intake and reply encoding for one transport connection, which keeps socket read/write details out of `DedicatedServerHost`.
- `RemoteClientSession` now owns the first desktop-client transport edge: connect, hello/join bootstrap, command submission, and background inbound receive on behalf of the desktop scene.
- `AuthoritativeActionFeedbackSystem` now owns short-lived client-local rejection-message state for surfaced authoritative action failures, while `MultiplayerDiagnostics` owns a bounded scene-local action-flow trace for snapshot/action intake facts.
- `ServerBootstrap` owns the server-mode startup branch, while `IServerTransport`, `IClientTransport`, `TcpServerTransport`, `TcpClientTransport`, and `TcpClientConnector` reserve the first concrete TCP transport edge behind `ITransportConnection`.
- `ProtocolRouter`, `IProtocolCodec`, and `IMessageFramer` now separate message dispatch, JSON serialization, and stream framing so transport code does not collapse into one large host class.
- `PlayScene` now owns the local `SceneEventHub` seam that future scene-scoped event publication and subscription lifetime management should flow through, rather than introducing a global engine bus.
- `GameSettings` now owns both raw tuning values and a small runtime state model (`RuntimeState`) plus targeted change events for HUD-driven configuration updates.
- `PhysicsIntegrator` owns the Bullet collision configuration, dispatcher, broadphase, solver, world, the currently registered player body slot, and registered map bodies, while helper systems now handle body creation, registration, and transform syncing.
- `Map` remains the world façade, but internally delegates sparse-cell storage to `WorldGrid`, cube authoring to `WorldGenerator`, and Bullet registration to `WorldPhysicsBinder`.
- `Player` remains the gameplay facade, but internally delegates movement/body control to `PlayerMotor`, camera synchronization to `PlayerCameraRig`, and request-style placement capture to `PlayerInteraction`, while desktop input translation now stays outside the authoritative update path.
- `Render` still owns shared low-level OpenGL state, shader programs, cube geometry buffers, HUD buffers, and ImGui resources, while scene-facing rendering is now exposed through `WorldRenderer`, `DebugRenderer`, `ViewModelRenderer`, and `HudRenderer`.
- `EnemyRegistry` now owns the active enemy collection and scene-local hit resolution for non-player combat actors.
- `EnemyActor` owns world-space chase behavior and melee state transitions, while `EnemyCombatant` owns health, attack cadence, combat identity, and damage intake for the same actor.
- `EnemyRenderer` now owns the first world-space non-cube animated-actor draw path and keeps wolf presentation logic out of `PlayScene`.
- `PlayScene` now owns scene-local combat feedback wiring, while `HudRenderer` and `Render` consume `CombatFeedbackSystem` state as render input rather than constructing combat feedback runtime themselves.
- `PlayerViewModelController` owns first-person runtime presentation state and translates gameplay input into per-slot animation requests.
- `ViewModelPoseResolver` composes the final first-person slot pose from asset defaults, shared rig settings, body-anchor illusion offsets, and per-slot tuning data.
- `ViewModelCatalog` resolves stable cosmetic ids into runtime `ViewModelDefinition` data and provides the default hands plus held-item loadout.
- `Shader` is a low-level helper used by `Render` and `AnimatedModel`.
- `AnimatedModel` owns glTF mesh, material, texture, and draw buffer data for model-backed rendering, including named clip playback with loop and one-shot semantics plus a bounded first-pass skinning path for skinned assets.

## World Geometry Foundation

The runtime now has a first geometry-storage subsystem under `Dream.Simulation.World.Geometry`.

Current roles:

- `GeometryCellRecord` is the authoritative cell-level storage unit
- `GeometryCellDefinition` carries shape, material, subdivision-resolution, and floor metadata
- `GeometrySubdivisionState` keeps sparse child overrides unallocated until a mutation touches the cell
- `GeometryMutationService` returns dirty-cell facts for future render/collision rebuild work
- `GeometryRenderExport` packages local render-facing geometry snapshots plus compatibility cubes for the current renderer
- `GeometryCollisionExport` packages merged-floor and per-cell collision descriptors for the current physics binder
- `Map` now caches per-cell render blocks and collision bodies and rebuilds only dirty geometry cells after subdivision mutations

`Map` still exports compatibility `Cube` instances for rendering, spatial queries, and current non-floor collider realization, but the canonical storage direction has started moving away from direct cube-only ownership.

`WorldGenerator` now also owns deterministic startup-world authorship instead of only single-cell cube defaults. The initial map is seeded as a larger terrain layout with mixed `Grass`, `Dirt`, and `Stone` material zones plus a few low raised landmark mounds and authored subcell sculptures such as a mini-cube pyramid, rounded boulder, and low rubble clusters, while later player-built cubes still use the simple direct authored-cell path.

## Update Loop

`Game.OnUpdateFrame()` now delegates either startup-scene or gameplay-scene work depending on whether the desktop client has connected yet.

When gameplay is active, `PlayScene.Update()` splits client capture from authoritative runtime advancement:

1. Toggle runtime state when global scene input requests it
2. Capture desktop gameplay input and translate it into a `PlayerCommandFrame` for the local `PlayerSessionId`
3. Pass that command frame into `WorldSessionRuntime.Update(...)`
4. Let `ServerTickRunner` advance fixed 64 Hz authoritative ticks while `WorldSessionRuntime` validates session identity and monotonic command sequence, accepts the active mode, resolves combat primary use before movement, steps Bullet, updates the player, updates wolves and runs the day/night wolf population step (below), applies build placement, and syncs Bullet-owned cube transforms back into world state
5. Trigger first-person presentation reactions from the runtime result, then advance the local viewmodel pass and notify the controller when one-shot clips finish
6. Tick scene-local combat feedback state for HUD rendering

### The world clock is a gameplay input (wave T2)

`WorldSessionRuntime.AdvanceAuthoritativeTick` advances `WorldClock` first, before any gameplay. Inside the gameplay block (`allowGameplaySimulation`), after the enemies act and the dead are pruned, the wolf population step (`EnsureWolfExists` in `WorldSessionRuntime.Enemies.cs`) runs once per tick:

- the target is 2 active wolves when `WorldClock.Phase` is `Night` (20:00 to 04:00), otherwise 1;
- with fewer than the target, it spawns one wolf: at the day spawn (`WolfSpawnPolicy.ResolveInitialSpawn`, (width − 3.5, 1, depth − 3.5), clamped into the map) when none is active, otherwise at the night spawn (`ResolveNightSpawn`, (width − 2.5, 1, depth − 2.5), clamped, and stepped away if it lands on the day spawn). Both are in the far corner from the world spawn (5, 5), outside every template's aggro range on maps of about 16 m and up;
- outside the night with more than one active, it removes the active wolf with the highest actor id and queues its row delete, as a kill does. Checkpoints never delete rows, so the step does not wait for one. Only wolves whose ids the runtime handed out or reserved (every enemy on a real server) are removed; an enemy a test adds with a foreign id still counts as active but is never despawned.

The night wolf is an ordinary persisted enemy with the next enemy id (ids are never reused). A restart in the middle of the night restores both wolves and spawns no third; a restart after dawn that still finds two rows (a crash before the delete committed) removes the night wolf on the first tick. The client never runs the step: `ClientWorldSession` has no clock and no spawn path, and only shows replicated wolves.

Because the phase now changes gameplay, the clock's inputs are lockstep inputs: the persisted world ticks, `TicksPerDay` and the number of ticks run. They are pure integers, so two servers with the same three values spawn and remove the night wolf on the same ticks. The clock itself stays out of `SimulationStateChecksum`; the extra wolf is already in the hashed actor list. A Day server and a Night server diverge on their first tick, by exactly that wolf (`WaveT2NightSpawnTests`). The step allocates nothing except on its spawn and despawn ticks.

That extracted runtime order is the first authoritative session boundary for later host splitting. `PlayScene` still captures local desktop input and still owns client-only presentation consequences, but it no longer owns the simulation sequence directly.

History note (Phase 2 and 3): the local player flows through an explicit `PlayerSessionRegistry`, `PlayerSessionId` and `PlayerCommandFrame` path, and authoritative advancement runs through `ServerTickRunner` at a fixed 64 Hz instead of render-frame delta. That runtime now lives on the server side only. The desktop client has a `ClientWorldSession` (see below) and reaches the world over the protocol, so the "one local Player, no transport" shape of those phases no longer applies.

Phase 4 adds the first explicit protocol contract layer on top of that: versioned transport envelopes, typed client/server message families, focused routing handlers, and explicit action/result payload contracts for one-shot primary-use style gameplay interactions.

The next host-side foundation now lets the headless server keep moving even when no fresh command arrives on a given network frame. `PlayerCommandHandler` records the latest inbound `PlayerCommandFrame` into a focused inbox, `DedicatedServerHost.AdvanceTime(...)` drains that inbox into the 64 Hz tick runner, and `WorldSessionRuntime` now continues simulation from its last accepted command frame instead of stalling authoritative updates outright when the network edge is quiet for one tick.

That host-side foundation now also has its first live transport loop:

1. `TcpServerTransport` accepts a socket connection
2. `DedicatedServerService` wraps it in `ServerConnection` and starts a per-connection receive loop
3. `ServerConnection` deframes and decodes client protocol messages, routes them through `DedicatedServerHost`, and frames outbound replies
4. `DedicatedServerService` advances the authoritative host independently on the fixed tick loop and broadcasts `ServerSnapshotMessage` frames only to connections that have completed join, once every `SnapshotIntervalTicks` ticks (default 2, so 32 Hz; see `docs/configuration.md`)

The current protocol behavior is intentionally conservative: a raw TCP connection may handshake with `client.hello`, but snapshot broadcast waits until the connection has joined a session. `server.join-accepted` now also carries initial actor snapshots for the already-joined shared world, so a late-joining client can render existing players immediately instead of waiting for the next broadcast tick.

The host-side tick seam is now slightly broader too: `LatestPlayerCommandInbox` keeps the latest pending command per joined session, `DedicatedServerHost` drains those commands as a batch for each fixed tick, and `RuntimeAuthoritativeSessionHost` can now apply the primary controlled session plus additional lightweight remote-session actors from the same authoritative tick window.

On the client side, the first transport attachment is equally narrow:

1. `Game` resolves optional remote-client settings from environment
2. `RemoteClientSession` connects to the server, sends `client.hello`, then sends `client.join-request`
3. the session keeps a background receive loop running so socket reads stay off the render thread
4. `PlayScene.Update()` drains queued server messages into `ClientProtocolSession` and mirrors local command frames onto the transport when the remote session is connected

That means the desktop client is now connected to the live transport edge without turning `PlayScene` into a socket pump.

The next client-authority step now also applies a first slice of authoritative local state back into the runtime:

- `ClientCorrectionApplier` mirrors the authoritative local-player actor snapshot onto the client runtime player position, facing, and health
- in connected play, the local player's position is reconciled rather than hard-snapped. `ClientPredictionReconciler` records the predicted position after each local tick, keyed by command sequence. Each snapshot compares the server position with the prediction for `LastAcceptedCommandSequences[localSession]`:
  - errors up to 5 cm are ignored;
  - errors up to 3 m are blended in over 0.1 s through `Player.NudgeWorldPosition`, which keeps velocity, and the remaining history is shifted so the error is not applied twice;
  - larger errors, or snapshots with no acknowledged command, snap the player.
  The join bootstrap and rejected-action corrections still snap, and they reset the reconciler.
- client prediction uses `PlayerSimulationTuning.WithAuthoritativeMovement()`: the server's move, noclip and jump values (`PlayerSimulationTuning.AuthoritativeDefault`, which the server host also uses), with local eye height and mouse sensitivity. The local `NoclipSpeed` setting therefore has no effect in connected play until the server accepts per-player tuning.
- `ClientCorrectionApplier` now also mirrors the authoritative interaction mode from `ServerSnapshotEnvelope` onto the client runtime so HUD and future use-routing logic can converge on server-owned mode state
- `RemoteWorldMutationApplier` reconciles full world snapshots plus incremental cube mutation deltas into the client runtime map
- `RemoteEnemyStateApplier` replaces client-local enemy runtime state from authoritative enemy actor snapshots

Connected desktop play also changes what the local runtime is allowed to do:

- local command frames are still captured and mirrored onto the transport
- local interaction-mode selection may still advance immediately for HUD/viewmodel continuity
- local movement and look simulation are suppressed while connected
- local primary-use authority is suppressed while connected
- local enemy simulation is suppressed while connected
- the desktop client's world is a `ClientWorldSession` (`Client/World`), not a `WorldSessionRuntime`. It owns the replicated `Map` (built with `replicatedCubeBodies`, so placed cubes are kinematic and move only through server `CubeMoved` deltas), the locally predicted `Player`, and the replicated `EnemyRegistry`.
  - `AdvancePredictionTick` predicts the local player, steps physics and settles rubble. It has no server paths: no authoritative actions, replication export, session registration, persistence or wolf spawn.
  - Client appliers target `IClientWorldState`.
  - `ClientWorldBoundaryTests` fails if client code references `WorldSessionRuntime` again.
  - The local use/attack animation is driven from the held primary-use input at the attack cadence. Before, the stripped simulation frame meant it never played in connected play.

World mutation deltas carry a server-assigned `Sequence`, and `JoinAcceptedMessage.InitialWorldMutationSequence` names the last delta already folded into `InitialWorld`.
- `RemoteWorldMutationApplier.ApplySnapshotToRuntime` skips sequenced deltas at or below the client's acknowledged sequence.
- Cube deltas and `PlacedCubeSnapshot` carry the cube's server `ActorId`. Clients resolve moves and destroys through `Map.TryGetCubeByReplicatedId` before falling back to the cell.
- A cube that gets a replicated id outside a placement (most often a generated terrain cube on its first hit) is announced with a sequenced `CubeIdentified` delta (cell plus `ActorId`), raised from `WorldObjectRegistry.CubeIdAssigned`. The client keys its cube in that cell to the id, so both sides hash it by id in `SimulationStateChecksum` and resolve later deltas by id without waiting for a full world snapshot.
- Cube and enemy actor ids come from disjoint ranges (`ReplicatedActorIdRanges`): cubes from 1000 to 1999 and then from 2^30, enemies from 2000 below 2^30. Since persistence wave C these are actor ids only; persisted objects have a separate `ObjectId` with a stored counter, and a restore gives cubes and enemies fresh compact actor ids (see Object ids and actor ids). Player actor ids are allocated separately (from 2) and are not covered.
- A `CubeMoved` delta whose cube the client cannot find is dropped and logged as `client.world.move-miss`; it never creates a cube.
- The server drops a same-cell `CubeMoved` from a snapshot batch when a later mutation for the same cube follows (`WorldSessionRuntime.CoalesceSameCellMoves`). Cross-cell moves stay in order.
- About once a second a snapshot carries `WorldChecksum` (`WorldStateChecksum`: intact raised cube cells and rubble origins with materials; `Map` keeps it as a running sum updated on every geometry and rubble store write, so reading it is O(1) on server and client, and `WorldStateChecksum.ComputeFull` is the full-walk reference). The server runtime's `Map` is headless (`WorldSessionRuntime(headlessMap: true)`, the default): it keeps collision descriptors, physics bodies and the checksum but builds no render block descriptors, merged render blocks or render-dirty sets, and its render exports throw. A client whose world no longer matches sends `ClientWorldResyncRequestMessage` (rate-limited to one per 2 s). `WorldResyncRequestHandler` answers that connection only with `ExportWorldResync`: a full world whose `MutationSequence` the client acknowledges. Deltas pending for other clients are not drained.
- `DedicatedServerService` discards pending deltas on ticks with no joined connection instead of keeping them as a backlog.
- Join and resync worlds travel as chunked transfers (protocol v3, SCALE-1).
  - The tick captures the world into pooled flat arrays (`WorldSessionRuntime.CaptureWorldSnapshot`, `WorldSnapshotCapture`); `ExportWorldSnapshot` returns a `WorldSnapshot` whose lists are views over that capture.
  - `ServerConnection` sends a queued `JoinAccepted` or world snapshot as its carrier (`WorldTransferId` set, no world), then `server.world-chunk` messages (`WorldChunkMessage`) of about 64 KB each (sized so one chunk fits the send-stall allowance on links down to about 12.5 KB/s), alternating with the queued snapshots and actions. It encodes the chunks on its writer task and releases the capture after the last one.
  - Worlds use the compact format of `WorldSnapshotJsonConverter`: delta-coded integer cells, material runs, delta-coded ids, centers off the cell center and moved rubble quantized to 1/4096 m, unmoved rubble as its first-pass pattern. That is about 11 B per resting cube against 127 B in the older protocol v2 format.
  - `RemoteClientSession` passes messages through `WorldTransferReassembler`. It holds the carrier and everything after it until the final chunk, then delivers the carrier with the assembled world followed by the held messages, so consumers see the carrier, then the held messages, in their original order and the held deltas apply on top of the world. Each transfer is bounded (`WorldTransferLimits`): at most 3 items per cell of chunk 0's dimensions (at least 64k items and at most 4M), 4096 held messages, 30 s without a new chunk (counted from the carrier until the first one), and 300 s in total. A breach ends the session with "World transfer failed: ...".
  - While a connection still has a world transfer queued or going out, the service defers a coalesce-overflow resync instead of queueing a second full world behind it (metrics: `CoalesceOverflowResyncsDeferred`, `BackToBackOverflowResyncs`, `BackToBackWorldTransfers`).
  - A peer on an older protocol version is refused with a `ProtocolVersionMismatchException` that names both versions.
- Snapshot actors travel as delta frames (SCALE-2, protocol v4). In memory every `ServerSnapshotEnvelope` keeps the full `Actors` list; only the wire changes. A v3 build would find no `"actors"` list and fail with a `NullReferenceException`, so SCALE-2 bumped the version: a v3 peer is refused in either direction with a `ProtocolVersionMismatchException` ("Client uses protocol v3; this server uses v4." on the server, which answers with a `JoinRejectedMessage` carrying that reason; "Server uses protocol v4; this client uses v3." on a v3 client, and the mirror on a v4 client facing a v3 server).
  - The runtime chains its broadcast exports (`ActorDeltaChain`, `Simulation/World/ActorReplicationFrame.cs`). Each export is frame `n` and is diffed against frame `n - 1` (the previous broadcast), with a keyframe every 64 server ticks (`ActorDeltaContext.KeyframeIntervalTicks`). World resync envelopes and `JoinAccepted.InitialActors` stay off the chain and keep the full actor list.
  - On the wire the snapshot's `"actors"` becomes `"actorFrame":{"seq":n,"base":n-1,"actors":[...]}` (`ActorFrameJson`). A keyframe has no `base`. Every actor appears once, in order, so a missing one has left. An unchanged actor is its bare id, and a changed one is `[id, groups, ...]`. `groups` is an `ActorFieldGroups` mask (full identity, position, facing, health, mode, combat, progression, starved ticks, and since v6 equipment) followed by the fields of the groups present. A full record carries every group with absolute values. The encoder sends one for an actor that is new against the base, an actor whose identity changed, and every actor in a keyframe. Other records carry differences against the base.
  - Transforms are quantized at 1/4096 m, the step the compact world already uses for moved rubble (`ActorQuantization`). Facing is quantized per component at the same step, about 0.014°. A changed transform is sent as the difference of the quantized values, so error never accumulates. An idle actor costs a few bytes (`17,`), a walking player about 20 (`[18,2,410,0,-205],`), and about 30 while it also turns. The bound `ActorQuantization.MaxError` (half a step) holds while |value|·4096 < 2^24, that is within about 4096 m of the origin.
  - The broadcast is still encoded once. `SharedEncodedPayload` encodes the shared body. The per-connection accepted command sequence moved out of the payload into the transport envelope header, as `"ack":{"<session>":n}` (`TransportEnvelope.Ack`), written by `ServerConnection` from the snapshot's in-memory map. Each connection now gets its own sequence only, so a broadcast to P players writes O(P) sequences, not O(P²). The decoder turns the header back into a one-entry `LastAcceptedCommandSequences` and marks the message `SharedBody`, so a re-encode reproduces the payload.
  - Gap recovery is decided on the server and needs no request from the client. TCP delivers in order, so `ServerConnection` knows exactly which frame its client holds: `_lastActorFrameSequence`, the frame of the last snapshot it encoded, and `_lastActorFrameActors`, that frame's actor list (export lists are immutable, so a reference is enough). A snapshot whose delta base is any other frame is handled by where it is encoded:
    - A shared broadcast goes out as its keyframe twin (`SharedEncodedPayload.KeyframeBytes`), which is encoded at most once per broadcast and only when some connection needs it. This only happens when the connection holds no frame: its first snapshot after a join between keyframes, or the first after a world resync (off the chain).
    - A snapshot encoded for this connection alone, in practice merged snapshots on a slow connection, is re-diffed against the frame the client holds (`ActorDeltaContext.CreateDelta(seq, _lastActorFrameSequence, _lastActorFrameActors)`, counted in `ServerConnection.ActorDeltasRebased`), so its base is not `seq - 1`. It goes out as a keyframe only when the client holds no frame. Before this, every merged snapshot was a keyframe: in the slow-link loopback test (`Wave12DeltaActorSlowLinkTests`) 61 forced keyframes became 2 (B's join and its resync), and in `DeltaActorSnapshotTests` merged 8-player snapshots cost 44% of their keyframe size.
    - A delta snapshot too large for the frame cap goes out without its world deltas (see the oversized fallback). Its actors keep the encoding chosen for the full frame (same frame, same base), so the client's chain is not broken. The resync that the fallback flags then puts the connection off the chain, and the next broadcast is the keyframe twin.
    - `ServerConnection.ActorKeyframesForced` counts the keyframes sent in place of a delta, once per frame actually written.
  - On the client, `RemoteClientSession` runs every decoded snapshot through `ActorStateCache` (`Client/Replication`) in wire order, before any world-transfer holding. The cache rebuilds full actors, so the presenter, interpolation, reconciler and appliers are unchanged. As a second line of defense, a delta whose base is not the cache's last frame marks every cached actor stale. A stale actor, or one a delta names that the cache never saw whole, is left out of the snapshot (never shown with old values as if current) until a full record arrives, at the latest with the next keyframe.
- The world clock is replicated once, at join (T1, protocol v5). `JoinAcceptedMessage.WorldClock` is a `WorldClockAnchor` (`Simulation/World/WorldClockAnchor.cs`) captured by `RuntimeAuthoritativeSessionHost.JoinSession` at the join tick: `"worldClock":{"serverTick":N,"worldTicks":N,"ticksPerDay":N,"startOffsetTicks":N}`. A chunked join keeps it on the carrier. The server's `WorldClock` advances once per authoritative tick, so `WorldTicks - CurrentServerTick` is constant and the anchor is just that offset plus the day length; snapshots carry no clock bytes.
  - On the client, `PlayScene` applies the anchor to `ClientWorldClock` on every join (a join without one clears it, and an anchor with `TicksPerDay <= 0` is ignored). The time of day is read at the monotonic `RemotePlayerRenderTick`, `(worldTicks + (tick - serverTick) + startOffsetTicks) mod ticksPerDay / ticksPerDay`, so every client derives the same time from its own tick estimate and the sky never steps back. A world resync leaves the anchor alone, and a reconnect re-anchors through its new join.
  - The anchor is why the version went from 4 to 5: a v4 client would join without a clock and show a fixed time of day. A v4 peer is refused in either direction with a `ProtocolVersionMismatchException` ("Client uses protocol v4; this server uses v5." on the server; "Server uses protocol v4; this client uses v5." on a v5 client).
- Protocol v6 (wave T2) adds two things clients must understand, so a v5 peer is refused in either direction with a `ProtocolVersionMismatchException`, as for earlier bumps:
  - **Held light.** `ActorSnapshot.HeldLight` (`HeldLightKind`: None, Torch, Lantern) is the light a player's equipped item casts, so other players see a remote torch. It is written as `"heldLight"` and is left off when None. In actor frames it is the `ActorFieldGroups.Equipment` group (256), which `Full` does not imply: the bit is set when the held light differs from the base, or on a full record whose held light is not None, and a full record without the bit means None. With no light equipped the snapshot bytes are unchanged. The server derives it from the inventory (`ResolveEquippedHeldLight`), so it stays out of `SimulationStateChecksum`.
  - **Build material.** A `build.material` player action (`BuildMaterialSelectIntent`, material id or null for Auto) selects what build placement places. The selection is per-session server state, not persisted and not checksummed. Placeable materials are Stone, Dirt and the new `GeometryMaterialId.Lamp` (id 8), an emissive block whose cells also emit point lights on clients (`docs/rendering-and-assets.md`, Point lights and emissive).
- Protocol v7 (persistence wave B) adds the world descriptor to `JoinAcceptedMessage`, so a v6 peer is refused in either direction with a `ProtocolVersionMismatchException`:
  - **World descriptor.** `JoinAcceptedMessage.World` is a `WorldDescriptorWire` (`Multiplayer/Protocol/WorldDescriptorWire.cs`), the public identity of the world the server runs: `"world":{"id":1,"name":"default","width":56,"height":8,"depth":56,"mapSource":"generated","generatorKey":"default","generatorVersion":1}`. It adds about 130 B to the join, once. A chunked join keeps it on the carrier. Snapshot payload bytes are unchanged apart from the version digit in the envelope.
  - The descriptor holds no seed and no world time; those live on the server-only `WorldRecord`. The world id never enters simulation state, checksums or snapshots.
  - **The client builds its map from the descriptor.** `ClientJoinAcceptedApplier` applies the descriptor before it looks at the initial world: an unsupported generator (anything but `default` version 1) ends the session with a readable reason naming the generator (`UnsupportedWorldException`; `PlayScene.ApplyReplicatedMessage` catches it, and an `InvalidDataException` from a size mismatch, and calls `RemoteClientSession.EndSessionWithReason`), a size that differs from the client's map rebuilds the map through `IClientWorldState.RebuildMapForWorld`, and an `InitialWorld` whose size differs from the descriptor is a protocol error that ends the session the same way. The client therefore has the right map at join instead of at the first full snapshot; the resulting map is the same. A join without a descriptor (`World` null) leaves the client's map to the initial world, as before.
- Rubble is client-cosmetic for players. Fracture pieces use the Bullet `DebrisFilter` group and players use `CharacterFilter`, and neither collides with the other on server or client, so locally simulated rubble cannot make prediction diverge.

Every joined session gets the same server-side player.
- The first session takes the runtime's primary `Player` seat. Later sessions get a `RemoteSessionPlayerState` that wraps its own physics `Player`, with a body registered through `PhysicsIntegrator.AddSessionPlayerBody`. Previously later sessions were kinematic ghosts with no gravity, jump or collision.
- All sessions are simulated before the physics step and reuse their last command when none arrives, so movement no longer depends on command arrival rate.
- When the primary leaves, its body is parked out of the world (`SetPlayerBodyParked`) instead of staying as an invisible collider. A secondary's body is removed when it leaves.
- A former primary that returns after the seat was taken gets its own secondary seat instead of a dead shared actor id.

Networking and timing:
- Both TCP ends set `NoDelay`.
- The client sends at most one merged command per client tick through a background writer (`RemoteClientSession.QueueCommandFrame`), and queues actions in order.
- Each `ServerConnection` has an ordered outbound queue drained by its own writer. The tick loop only enqueues; unsent consecutive snapshots are merged (`ServerSnapshotEnvelope.TryCoalesce`), and a backlog over 512 messages faults the connection and drops it.
- Receive paths deframe through `FrameReceiveBuffer` instead of copying the whole backlog per frame.
- The server tick loop runs on a dedicated thread paced by `TickPacer` (1 ms timer resolution plus a short spin).
- The desktop starts the integrated server only when local hosting is used (`OnDemandLocalServerHandle`).

`PlayScene` now requires a protocol client session, so this connected-client behavior is the normal desktop path for the first local player as well as later joined clients. The current integrated host still uses TCP loopback as the concrete local transport; the planned next step is an in-memory duplex transport that preserves the same protocol, command, action, and snapshot flow without socket overhead.

That keeps the client usable while reducing the biggest "local runtime fights the server" problems. Later work must still reconcile rollback, combat feedback ownership, gameplay-affecting settings such as floor-size edits, and the remaining local-only presentation/runtime assumptions more aggressively.

Player authority on the server (see "Every joined session gets the same server-side player" above):

- the first session holds the runtime's primary `Player` seat; later sessions hold a `RemoteSessionPlayerState` that wraps its own physics `Player` and body, so all of them have gravity, jump and collision
- authoritative one-shot action handling first resolves a per-session action context so build placement and first-pass combat can validate against the acting session's own replicated mode state
- joined remote-session combat now also normalizes aim from authoritative actor state instead of trusting arbitrary client-provided combat rays directly
- remote client action results now also flow through a focused `ClientAuthoritativeActionApplier` seam so accepted authoritative world mutation/combat outcomes can trigger immediate local correction and combat feedback before the next snapshot arrives
- that same applier now handles rejected authoritative action results too: correction deltas can immediately reposition/reface the local replicated player, rejection facts surface as explicit scene events, and the scene now consumes them through a short-lived HUD feedback seam plus bounded multiplayer diagnostics recording

The first Phase 5 groundwork now sits above that protocol seam: `ClientScenePresenter` buffers authoritative remote-player samples and produces interpolated remote presentation state without changing local authoritative simulation ownership.

`Player.Update()` now coordinates smaller subsystems from distilled simulation input rather than raw desktop device state:

- `PlayScene` converts raw desktop keyboard and mouse state into a session-bound `PlayerCommandFrame`
- `WorldSessionRuntime` validates that frame and translates its transport-safe move/look/mode/use intent into runtime-facing player input
- `PlayerMotor` owns freeze/jump/move application against the Bullet rigid body and now also owns noclip toggle state plus camera-relative fly movement bypass
- `PlayerCameraRig` synchronizes camera position from the body and applies look deltas
- `PlayerInteraction` handles edge-triggered build-use capture and returns plain placement requests
- `Player` itself retains collision reporting and public facade methods used by `WorldSessionRuntime`

## Render Loop

`Game.OnRenderFrame()` now delegates either startup-menu rendering or gameplay rendering, then swaps buffers and updates the shell-owned FPS title.

While the startup scene is active:

- the framebuffer is cleared to a neutral background
- `StartupMenuRenderer` renders the primary multiplayer connect UI
- the player can use the integrated local server defaults or enter a remote host/port manually

After a successful connection, the desktop shell transitions into `PlayScene.Render()`:

Inside `PlayScene.Render()`:

- the framebuffer is cleared
- `WorldRenderer` draws active cubes
- `EnemyRenderer` draws active world-space wolf actors through the animated-model path
- `DebugRenderer` draws optional collision overlays
- `ViewModelRenderer` clears only depth and draws the first-person hands and held-item pass
- `HudRenderer` submits HUD/settings UI and returns runtime requests
- runtime floor rebuild behavior is applied when requested

Static terrain goes through `StaticWorldChunkRenderer`: chunk meshes with hidden faces removed, built on a worker thread and uploaded on the render thread, drawn in one batched pass (`Render.BeginStaticTerrainPass`, `BindStaticTerrainMaterial`, `EndStaticTerrainPass`: one program bind and one set of transforms per pass, only the material changes per slot). Chunks are culled against the camera `ViewFrustum` (`StaticWorldChunkRenderer.cs:144`). The live path (dynamic cubes, fracture pieces) is filtered by `WorldRenderer.IsCubeInView` / `IsGeometryBlockInView`, which apply the distance and direction gate of `WorldVisibility` and then the frustum, and then groups survivors into shared model/shader batches. `PlayScene` assembles candidates through a map-side floor-and-placed split: nearby floor tiles from local bounds, nearby placed cubes from the spatial index.

The HUD now returns interaction requests upward to the scene layer, while `PlayScene` owns the client-side settings consequences of those requests and forwards authoritative floor-rebuild work into `WorldSessionRuntime`.

`WorldSessionRuntime` now also exports a `WorldSessionPresentationState` snapshot for the desktop client path. `PlayScene.Render()` consumes that snapshot for world draw candidates, active enemies, collision-debug bodies, player collision body access, and HUD status, which keeps the scene in a presenter role instead of reaching through several runtime-owned objects independently.

Alongside presentation snapshots, `WorldSessionRuntime` now also exports a transport-neutral `ServerSnapshotEnvelope` for future server/client use. That envelope carries the current authoritative tick, accepted per-session command sequence metadata, current authoritative interaction mode, authoritative actor snapshots, optional full world snapshot data for join/resync use, and explicit world/combat delta collections. Full world snapshots distinguish intact authored subcell startup geometry from fractured rubble cells, so the client restores deterministic generated sculpture cells instead of treating every subdivided cell as damage rubble.

Player actor snapshots are now slightly richer than the first transport cut as well: `ActorSnapshot` can now carry an optional per-actor interaction mode so multi-session player actors do not need to rely forever on one runtime-global mode fact.

The new protocol seam keeps those runtime DTOs transport-neutral. `TransportJsonCodec` owns JSON serialization policy, `LengthPrefixedMessageFramer` owns stream-safe payload boundaries, and `ProtocolRouter` plus role-specific handlers translate between typed protocol messages and focused client/server contexts.

The current client path now consumes remote-player snapshots in two stages:

- `PlayScene.ApplyReplicatedMessage(...)` feeds `ServerSnapshotMessage` data into `ClientScenePresenter`
- `PlayScene.Render(...)` asks the presenter for interpolated remote-player presentation state at a slightly delayed render target and forwards those results into `RemotePlayerRenderer`

That keeps remote players firmly in the world-space pass and leaves local camera, HUD, and viewmodel logic unchanged.

The desktop render path now also receives world geometry through `WorldSessionPresentationState.VisibleWorld`. `WorldSessionRuntime` captures a local `GeometryRenderExport` from the map, and `WorldRenderer` consumes its compatibility cubes through that seam instead of reaching back into runtime-owned map storage directly.

Subdivided cells now enrich that export further: intact solid cells can still ride through the coarse compatibility-cube path, while subdivided cells rebuild only their own geometry-native render blocks and report dirty-cell metadata without forcing a whole-world render artifact refresh.

That render export now also splits ownership explicitly: static compatibility cubes feed the cached chunk renderer, while dynamic rigid-body cubes stay on the live direct-render path so moving gameplay cubes do not drift away from their collision bodies.
Startup world sculptures now use that same geometry-native subcell path proactively rather than only after damage. Authored sculpture cells are now sampled from a global micro-grid where one normal world cube contains `16 x 16 x 16` absolute microcells. `WorldGenerator` authors the sculpture in world microcell coordinates first, then projects those coordinates back into `GeometryCellRecord` subdivision state as a compatibility bridge. This avoids parent-cell-local gaps at sculpture borders while preserving the current storage, snapshot, render, and collision seams.
Because those authored forms can produce many mini-cube descriptors, `Map` now keeps a cached global greedy merge for static startup sculpture render blocks. Contiguous authored microcells become cuboid render descriptors, and those cuboids may cross the old parent-cell boundary when the material is the same. The canonical occupancy remains the full global micro-grid shell, so collision and physics are not derived from render-only visual fill. `WorldRenderer` groups visible geometry blocks by model and shader and renders model-backed batches through instanced `AnimatedModel` draws. `Map` also greedily merges solid subcell runs into larger static collision cuboids before exporting them to Bullet, keeping collision exact enough for the current voxel world without creating one rigid body per visible mini-cube.
For dynamic cubes, though, the map treats every occupied cell as full (`Map.CanTransitionCubeToCell` rejects any other cube there), so an occupied partial static cell (a sculpture cell or a carved static cell) also gets a cell support: a static full-cell box in the static collision group whose mask admits only the default group, so only dynamic map cubes collide with it (`WorldPhysicsBinder.SyncCellSupportBody`). Players, fracture rubble and kinematic replicated cubes never touch it. Without it, a cube resting above a partial cell fell into the cell, had the transition rejected, and was reset by `PhysicsSyncSystem` every `RejectionsBeforeReset` ticks forever, which kept its whole contact island awake.
The first gameplay-facing use of that path is cube fracture: lethal combat hits against destructible placed cubes now convert the cell into a deterministic rubble pattern instead of removing the whole cell immediately, and multiplayer world snapshots/mutations now carry a dedicated fractured-cell state so late joiners and connected clients reconstruct the same pieces.

The next authoritative-interaction slice now routes one-shot gameplay actions more explicitly as well:

- `PlayerActionHandler` always answers `player.action` messages with a `ServerActionMessage`
- `WorldSessionRuntime.ProcessAuthoritativeAction(...)` is now the single authority seam for build/combat action acceptance
- invalid assumptions such as missing joins, session mismatches, missing runtime attachment, or wrong authoritative mode now produce explicit rejected action results instead of failing silently

The floating-combat-text path is now event-driven inside the scene boundary. `PlayScene` constructs the scene-local `CombatFeedbackSystem`, `PlayScene.Update()` publishes `DamageAppliedEvent` facts through the scene-local hub, `CombatFeedbackSystem` subscribes and translates them into transient feedback state, and the HUD/render layer only consumes that feedback state for camera-local billboard-style projection and drawing.

Placed cubes now opt into combat capability through a broader `WorldObjectRegistry` rather than by storing permanent HP directly on `Cube`. `WorldSessionRuntime` now resolves combat hits against enemies first and then world cubes, while the registered runtime record still models a generic world-object lifecycle that future props or interactables can reuse. On fatal cube hits, the runtime now unregisters the destructible record and fractures the authoritative geometry cell into static rubble instead of always deleting the cell outright.

Scene-local events are intended for cross-system facts and reactions. Ordered simulation work such as movement, physics stepping, transform sync, and render pass order should remain direct. In the current reactive slice, the scene now publishes world-mutation facts such as `CubePlacedEvent` and `CubeDestroyedEvent` alongside combat facts such as `DamageAppliedEvent` and `ActorDiedEvent`, while debug tracing remains an opt-in scene-local subscriber through the bounded `EventTraceSystem` rather than default always-on scene wiring.

That keeps `PlayScene.Render()` presentation-oriented. Floor-size rebuild requests still originate from client UI, but the authoritative map rebuild, object restoration, enemy clamping, and player re-clamp now execute inside `WorldSessionRuntime` instead of directly in the scene.

The first-person subsystem is intentionally threaded through both `PlayScene.Update()` and `PlayScene.Render()` rather than hidden inside `Render`. `PlayScene` owns when the viewmodel update step runs, when the draw pass runs, and what frame state each receives, while `Render` remains the shared low-level renderer host that exposes focused collaborators instead of absorbing feature-specific first-person rules.

`Render.DrawCube()` resolves the cube's model from its `MaterialId` through `CubeMaterialModelCatalog.Resolve` (grass, dirt and stone have glTF cube models):

- If a model resolves, the cube is rendered through `AnimatedModel.Render()` with the cube's `ShaderName` program
- Otherwise it goes through `Render.DrawTerrainCubeFallback`, a textured unit-cube mesh for the material (`TerrainFallbackCubeMesh`)

There is no `Cube.Animation` property and no transparent-cube-plus-wireframe path any more.

The HUD path currently includes:
- A crosshair built from simple line geometry
- An ImGui `HUD` window for runtime status, including player HP, active interaction mode, and current derived damage range
- A short-lived rejection message path for surfaced authoritative action failures
- An ImGui `Settings` window that can request camera, resolution, floor-size, collision-debug, viewmodel rig, and live player combat-stat changes
- A floating-combat-text overlay projected from world anchors into screen coordinates

## Physics Model

`PhysicsIntegrator` builds a `DiscreteDynamicsWorld` with gravity pointing down the Y axis. `WorldSessionRuntime` now acts as the authoritative world-stack composition root for the current desktop session: it uses `PhysicsFactory` to create the player rigid body as a `0.5 x 0.5 x 0.5` box shape with CCD settings enabled, registers that body with the integrator, and keeps ownership of that simulation stack behind the runtime seam.

That shift removes the older implicit "construct the local player body inside `PhysicsIntegrator`" assumption. The integrator still owns the registered-body slot and its cleanup rules, but `PlayScene` no longer constructs or owns the player body directly.

`Map` now separates floor visuals from floor physics and data registration concerns:

- the visible floor is still created as solid cubes in the map
- `Map.CaptureGeometryCollisionExport()` now describes the merged floor surface plus geometry-backed non-floor cell bodies
- `WorldPhysicsBinder` consumes those geometry collision descriptors instead of assuming every body starts as a direct cube-only physics request
- later non-floor cubes still receive individual rigid bodies through the same binder as a compatibility realization of geometry cells
- subdivided geometry cells now replace only their own collider set through `WorldPhysicsBinder.SyncCellCollisionBodies(...)` instead of forcing a whole-map collider rebuild

Since persistence wave B the live map size comes from the world descriptor (see Worlds as rows). The HUD floor size now only stages and persists the size of the next new local world; it no longer rebuilds a live session's map. `WorldSessionRuntime.RebuildMapForFloorSize` is still the rebuild path, and updates `World`, with these steps:

- `PhysicsIntegrator.ResetMapBodies()` removes the old map rigid bodies
- `WorldSessionRuntime` creates a rebuilt `Map` with the requested square width/depth
- `Map.CopyPlacedCubesTo()` migrates surviving non-floor cubes into the rebuilt map
- `WorldObjectRegistry` restores destructible cube records against the rebuilt map
- `EnemyRegistry.ClampToMapBounds(...)` revalidates active enemy positions against the rebuilt bounds
- `Player.SetWorldPosition()` resets the player transform and clears stale velocities after the rebuild

The player and dynamic map cubes are linked through Bullet rigid bodies. During updates, Bullet is treated as the authoritative source of dynamic physical positions, and `PhysicsSyncSystem` copies those world-space origins back into the rendering-side `Cube` objects.

### Resting cubes are frozen (`CubeRestFreezer`, PHYS-2)

Bullet puts a simulation island to sleep only when none of its bodies is active. Touching placed cubes overlap in the broadphase, so a floor of placed cubes is one island. Player bodies never deactivate, so a player touching that island kept every cube in it solving every tick (wave 8: about 2,250 of 2,400 bodies in Smoke8, about 9.7 ms per tick).

`PhysicsIntegrator` runs `CubeRestFreezer` before each step:

- **Freeze.** A placed unit cube whose own velocity has stayed under Bullet's sleeping thresholds for `FreezeAfterRestSeconds` (0.5 s) gets mass 0 (a static object) and its velocities zeroed. Static objects never join an island, so a frozen cube costs nothing.
- **Wake on push.** A frozen cube wakes when a mover (an awake cube, or a player moving faster than `WakeSpeed`, 0.1 m/s) moves into its side. The push passes on, so a pushed row or stack wakes in the same tick.
- **Wake on ride.** A frozen cube resting on top of a mover wakes. Cubes a mover stands on stay frozen, so walking on or landing on a floor of cubes wakes nothing.
- **Wake on support removal.** `WakeRidersOf` wakes every frozen cube resting on a body that stops supporting it, and the stacks on those cubes. Side neighbours and the cubes below stay frozen. It runs when:
  - a map body is removed from the world (a broken or moved cube, a rebuilt cell; `OnBodyRemoving`);
  - a player body is removed (a session disconnects), parked (its seat is emptied), detached or teleported (`Player.SetWorldPosition`). A player body is a 1 m box, so cubes can freeze on its top. A teleport zeroes its velocity, so the mover rule would never see it carry them away;
  - a support is moved in place (`WorldPhysicsBinder`'s compatibility cube move), before the move. This also covers a support that is not frozen.
- **Wake on `Activate`.** A frozen body that other code activates (`Activate(true)` after a teleport or reset) wakes, so existing callers keep their meaning. A frozen body never moves on its own, so if its box differs from the one it froze with, it was moved, and the frozen cubes resting on its old box are woken too.

Fracture rubble is never frozen. `DREAM_CUBE_REST_FREEZE=0` turns freezing off (`docs/configuration.md`). The measured effect is in `docs/performance-and-optimizations.md`.

### libbulletc profiler guard (`BulletProfilerGuard`)

The native `libbulletc` that BulletSharp 0.11.1 bundles is built with Bullet's quickprof profiler (`BT_PROFILE`). Every profiled Bullet call picks a per-thread profile tree with `btGetCurrentThreadIndex()`. That index comes from a process-wide counter that wraps after 63 threads. In a long-running server or a parallel test run, more than 63 threads call into Bullet, so two live threads end up sharing one index and one tree. Their interleaved start and stop calls walk the tree to a null node, and the next call faults with `0xC0000005`, which managed code cannot catch. The two threads can be using unrelated worlds. The wave 9 crash dump showed `DedicatedServerHost.Dispose` and a client test faulting at the same instruction.

The library exports no switch for the profiler. So before the first `PhysicsIntegrator` creates a world, `BulletProfilerGuard.EnsureApplied()` patches `btGetCurrentThreadIndex` in memory to return 64 (`BT_MAX_THREAD_COUNT`). For that value quickprof returns at once, so profiling is off. Bullet's multithreaded pools, which dream does not use, fall back to their no-slot path. The function is found by an exact byte pattern, and the patch is applied only on a single match.

Scope: Windows x64 with the bundled `libbulletc` only. Any other platform, a missing library, or a different build is reported as `Unsupported` and left alone. `DREAM_BULLET_PROFILER_GUARD=0` finds the function but skips the patch, to reproduce the crash. The cleaner alternative is a `libbulletc` built without `BT_PROFILE` (or with a thread-safe index), shipped in place of the bundled one. That would remove the patch, but dream would then build and ship its own native Bullet for every platform.

## Rendering Model

`Render` creates and reuses:

- A cube VAO/VBO for non-model cube rendering
- A shader dictionary keyed by logical names such as `default` and `cube`
- HUD VAO/VBO resources
- ImGui context, texture, and buffer resources
- A dedicated `ViewModelRenderer` collaborator for camera-relative first-person presentation

Projection is rebuilt from the live window size and configured field of view. The HUD crosshair is also rebuilt from live viewport data.

World programs are loaded with `Render.LoadWorldShader`, which compiles each program twice: the OPAQUE program and a MASK variant with `WORLD_ALPHA_MASK` defined (`Shader.AlphaMaskDefine`). `AnimatedModel` switches to the variant only for glTF `MASK` materials (`AnimatedModel.UsesAlphaMaskProgram`). The discard lives only in the MASK variant because a fragment shader that can discard loses early depth testing even when it never discards.

Frame diagnostics: `GpuFrameTimer` brackets the world, actors and HUD sections of a frame with GL timer queries (plus a primitives-generated query) in a ring of 4 slots and reads them back at least 2 frames later without stalling, so GPU times describe an older frame than the CPU times beside them. `RenderCounters` counts draw calls, program binds and uniform uploads per frame on the render thread. Both feed `FramePerformanceSnapshot` (`PlayScene.Render` in `PlayScene.Render.cs`, where the GPU section times and render counters are copied into the snapshot), whose `GPU:` line appears in the settings-panel performance counters (`FramePerformanceSnapshot.cs:128`).

`ViewModelRenderer` owns the first-person pass behavior:

- resolve slot models from the current frame state during `PlayScene.Update()`
- advance clip playback and report one-shot completion back to `PlayerViewModelController`
- resolve active hands and held-item models from the current `ViewModelFrameState`
- apply a viewmodel-local transform and dedicated first-person projection
- clear only the depth buffer before drawing so world color stays intact while world depth does not clip the first-person pass
- render after world and collision-debug passes, but before HUD and ImGui

`GameSettings` now also owns persisted `ViewModelRigSettings`, which are edited through the runtime menu and consumed by `PlayerViewModelController` plus `ViewModelPoseResolver`. That split keeps `Render` responsible for control presentation while the pose math remains in focused first-person collaborators.

The emerging combat layer is intentionally capability-based:

- `IDamageable` defines shared damage intake behavior
- `ICombatAnchorProvider` defines the reusable world anchor used by combat feedback
- `HealthState` is the first shared combat-state component and can back players, enemies, or destructible objects
- `CombatActorHandle` packages identity, kind, damageability, and anchor access into a lightweight runtime handle
- `PlayerCombatant` is the first concrete adapter and keeps player combat participation out of the heavier movement and physics code
- `EnemyCombatant` mirrors that adapter approach for the first wolf enemy so player and enemy damage flow through the same event and floating-text seams
- `PlayerCombatant` now also owns a small player-only stat model that derives outgoing damage range, crit damage, attack cadence, and vitality-driven max HP, while `WorldSessionRuntime` keeps hit resolution and combat-event publication on the authoritative path

For collision debugging, `Render` now uses Bullet-driven transforms for red wireframe overlays instead of reusing the normal visual cube transform path. `Game` enables those overlays conditionally for:

- cube rigid bodies that correspond to actual cube pieces
- the player rigid body

After the Phase 1 scene extraction, the enable/disable decision is owned by `PlayScene`, while `Game` remains responsible only for shell lifecycle and cursor application.

## Current Performance Shape

The codebase now avoids several of the earlier floor-scaling costs:

- the world uses separate `Width`, `Height`, and `Depth` bounds instead of one cubic side size
- empty cells are left unmaterialized instead of eagerly creating `Cube` objects everywhere
- `Map.SolidCubes` is used as the render source instead of scanning the full world array every frame
- the flat base floor uses one merged static collider instead of one rigid body per floor tile
- `Shader` caches uniform locations instead of looking them up repeatedly in hot render paths

## Asset Loading Model

Two parts of the codebase are responsible for runtime asset loading:

- `Shader` reads GLSL files from disk and compiles them into OpenGL programs
- `AnimatedModel` loads a glTF file, loads textures from embedded image content, creates OpenGL textures, and creates mesh buffers for each primitive

The current implementation resolves shader and model assets through shared runtime path helpers rather than through machine-specific absolute Windows paths.

For skinned world assets, the current renderer now also uploads a first-pass joint palette from SharpGLTF joint world matrices plus inverse bind matrices. That support is intentionally bounded to the current project need: one logical skin per asset is supported cleanly, which covers the wolf milestone without overbuilding a broader animation stack yet.
