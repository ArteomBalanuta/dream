# Persistence Wave B Plan: Worlds As Rows

Purpose: split persistence wave B (Phase 2 of `2026-10-28-world-persistence-roadmap.md`) into one contract commit (C0) and five workstreams that touch disjoint files.

Status: approved for implementation. Based on a read-only analysis of `integrate/engine-iteration` at `6e88112`, in the same format as the wave A plan.

## Decisions

| Question | Decision | Why |
| --- | --- | --- |
| How repositories know their world | One repository instance per world: the world id is given at construction and bound as `$worldId` in the SQL. | The six aggregate interfaces stay unchanged, and so do `IWorldPersistenceRepository`, `IPersistenceCheckpointWriter`, `RepositoryPersistenceCheckpointWriter`, `RuntimePersistenceBridge`, every fake in the 14 test files and the `nameof` uses. One server process runs one world. The existing `(schema)` constructors mean world 1. |
| Which tables belong to a world | **Per world:** `player_world_state`, `placed_cube_instances`, `cube_fracture_pieces`, `enemy_instances`, `item_instances`, `player_inventory_slots`, `player_starter_grants`. **Global:** `player_profiles` (including vitals), `player_profile_change_log`, `combat_formulas`, `schema_version`, `world_metadata` (kept as a generic key/value table). | Inventory belongs to a world because items exist in it. The starter grant is measured against that world's inventory. |
| Primary keys | `(world_id, local id)` on every per-world table, including items. Slots reference items by `(world_id, item_instance_id)`. | Actor and item ids restart per world. This also makes copying a world a plain `INSERT … SELECT`. `GetMaxItemInstanceId` becomes per world. |
| Storage of the busy tables | `WITHOUT ROWID` for `placed_cube_instances`, `cube_fracture_pieces` and `enemy_instances`. | With a composite key, a checkpoint `UPDATE` then needs one B-tree lookup instead of two. This protects the World10k +10% gate. |
| Cell coordinates as `INTEGER` | Done in wave B, since these tables are rebuilt anyway. Rows are copied as they are, so `INTEGER` affinity converts whole `REAL` values without loss. | The bind and read code does not change. |
| `ON DELETE SET NULL` for owners | Deferred to wave C. Wave B keeps `CASCADE`. | It changes Simulation behaviour: a non-null `OwnerPlayerName`, and the checkpoint skips cubes without an owner. |
| Seed and world time | Moved onto the `worlds` row. `seed` is TEXT (the same decimal text; NULL means "generate on first load"). `world_time_ticks` is INTEGER; the reader rejects bad values with `InvalidDataException`. Both keys are removed from `world_metadata`. | Rolls do not change, and corrupt values still fail at load. |
| `TicksPerDay` | Not stored. `DREAM_DAY_LENGTH_MINUTES` keeps control. | It can be added later with `ADD COLUMN`. |
| What goes on the wire | `WorldDescriptor` holds only the public identity. The seed and time live on the server-only `WorldRecord`. | The seed never reaches clients. |
| Allowed sizes | Width = depth in 5–100, height 8, generator `default` version 1 only. | |
| Next-object-id counter | Wave C. | |

## C0 Contract Commit

```csharp
// dream/Simulation/World/WorldId.cs
public readonly record struct WorldId
{
    public static readonly WorldId Default = new(1);
    public WorldId(long value) { ArgumentOutOfRangeException.ThrowIfLessThan(value, 1L); Value = value; }
    public long Value { get; }
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}
// dream/Simulation/World/WorldMapSource.cs
public enum WorldMapSource { Generated, Stored }
public static class WorldMapSourceText { public static string ToStorage(WorldMapSource s); public static WorldMapSource Parse(string text); } // "generated"/"stored", else InvalidDataException
// dream/Simulation/World/WorldGeneratorCatalog.cs
public static class WorldGeneratorCatalog { public const string DefaultKey = "default"; public const int DefaultVersion = 1;
    public static bool IsSupported(string key, int version) => key == DefaultKey && version == DefaultVersion; }
// dream/Simulation/World/WorldDescriptor.cs
public sealed record WorldDescriptor(WorldId Id, string Name, int Width, int Height, int Depth,
    WorldMapSource MapSource, string GeneratorKey, int GeneratorVersion)
{
    public const int MinHorizontalSize = 5, MaxHorizontalSize = 100, DefaultHeight = 8, MaxNameLength = 64;
    public static WorldDescriptor Default { get; } = ForFloorSize(WorldDefaults.StartupFloorSize); // id 1, "default", 56x8x56
    public static WorldDescriptor ForFloorSize(int floorSize, WorldId? id = null, string name = "default");
    public void Validate(); // name 1..64 trimmed, Width==Depth in [5,100], Height==8, MapSource Generated, IsSupported(...); else ArgumentException
}
// dream/Multiplayer/Protocol/WorldDescriptorWire.cs   (JSON: {"id":1,"name":"default","width":56,"height":8,"depth":56,"mapSource":"generated","generatorKey":"default","generatorVersion":1})
public sealed record WorldDescriptorWire(long Id, string Name, int Width, int Height, int Depth,
    string MapSource, string GeneratorKey, int GeneratorVersion)
{ public static WorldDescriptorWire From(WorldDescriptor d); public WorldDescriptor ToDescriptor(); } // ToDescriptor does not Validate; callers do
// JoinAcceptedMessage.cs: append the parameter (no version bump in C0)
    WorldDescriptorWire? World = null
// dream/Server/Worlds/  (namespace Dream.Server.Worlds; outside Server.Persistence so the client host may use it later)
public sealed record WorldRecord(WorldDescriptor Descriptor, ulong? Seed, long WorldTimeTicks,
    int CreatedSchemaVersion, DateTime CreatedUtc, DateTime UpdatedUtc);
public sealed record WorldCreateOptions(string Name, int Size = WorldDefaults.StartupFloorSize, ulong? Seed = null);
public readonly record struct WorldSelector(WorldId? Id, string? Name) {   // implemented in C0
    public static WorldSelector Parse(string? text); /* null/blank => default; all digits => Id; else trimmed Name */
    public bool IsDefault => Id is null && Name is null; }
public sealed class WorldNotFoundException(string selector) : InvalidOperationException(...);
public sealed class WorldNameTakenException(string name) : InvalidOperationException(...);
public interface IWorldCatalog
{
    IReadOnlyList<WorldRecord> List();                       // ORDER BY world_id
    WorldRecord? TryGet(WorldId id);
    WorldRecord? TryGetByName(string name);                  // NOCASE
    WorldRecord Create(WorldCreateOptions options);          // validates; seed generated when null; created_schema_version = current
    WorldRecord Copy(WorldId source, string newName);        // one owned tx; every world-scoped row; same local ids; seed+time copied
    bool Delete(WorldId id);                                 // one owned tx; explicit DELETE per table (children first), then the world row
    // Default: world 1, else the lowest id, else create "default" (newWorldSize). Id: must exist (WorldNotFoundException).
    // Name: open it, or create it with newWorldSize. created = true when this call created the world.
    WorldRecord Resolve(WorldSelector selector, int newWorldSize, out bool created);
}
// dream/Server/Persistence/Worlds/SqliteWorldCatalog.cs: skeleton, ctor (SqliteSchemaBootstrapper schema); bodies throw NotImplementedException("B4")
// dream/Server/ServerStartOptions.cs
public sealed record ServerStartOptions(string? WorldSelector = null, int? NewWorldSize = null);
// ServerBootstrap (signatures only, forwarding to today's behaviour):
public static string DefaultDatabasePath => AssetPaths.Config("player-profiles.db");
public static string DefaultSchemaScriptPath => AssetPaths.Config("player-profiles.init.sql");
public const string WorldEnvironmentVariable = "DREAM_WORLD";
public static int RunServerMode(ServerStartOptions options);
public static DedicatedServerHost CreateAuthoritativeHost(ServerStartOptions options);
// ServerPersistenceBundle: optional trailing ctor params + properties
    WorldId? worldId = null, WorldDescriptor? descriptor = null  ->  WorldId WorldId (default 1), WorldDescriptor Descriptor (default Default)
// RuntimePersistencePorts: public WorldDescriptor? World { get; init; }
// World-bound constructor overloads; in C0 they throw NotSupportedException unless worldId == WorldId.Default:
SqliteWorldPersistenceRepository(SqliteSchemaBootstrapper schema, WorldId worldId)
SqlitePersistenceCheckpointWriter(SqliteSchemaBootstrapper schema, WorldId worldId)
SqliteWorldMetadataRepository(SqliteSchemaBootstrapper schema, WorldId worldId)
```

Adapt member names to the real code where needed. Any existing method signatures named above (`RunServerModeAsync`, `CreateAuthoritativeHost`) keep their current overloads. After B1, the aggregate repositories take `(IWorldStore store, WorldId worldId)`, where `(IWorldStore store)` means world 1. The static checkpoint partials gain a `WorldId worldId` parameter.

## Migration 0003 (`dream/config/migrations/0003_worlds.sql`)

### Changes to `SqliteSchemaMigrator.Migrate` (B1)

- **Foreign keys off during migrations.** `PRAGMA foreign_keys` is a no-op inside a transaction, and with foreign keys on, `DROP TABLE` runs an implicit `DELETE` that fires `ON DELETE CASCADE`. Dropping `item_instances` would then wipe the inventory slots. So, outside the transaction:
  - read `PRAGMA foreign_keys`;
  - if it is on, set it to OFF;
  - in a `finally`, restore the original value, because pooled connections keep the pragma.
- **Check after the last migration.** Inside the transaction, after the last migration, run `PRAGMA foreign_key_check;`. If it returns any row, throw `InvalidOperationException` naming the table, rowid and parent (up to 10 rows). That rolls everything back, and the backup already exists.
- Leave `legacy_alter_table` at OFF.

### Table rebuild pattern

Follow SQLite's 12-step procedure:

1. Create `X__v3`.
2. `INSERT … SELECT` the rows into it.
3. `DROP TABLE X`.
4. `ALTER TABLE X__v3 RENAME TO X`.
5. Recreate the indexes.

Never rename the old table out of the way first. SQLite 3.26 and later would rewrite child foreign keys to point at the renamed table. The only trigger is on `player_profiles`, which this migration does not touch. There are no views.

### The `worlds` table

```sql
CREATE TABLE worlds (
    world_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL,
    width INTEGER NOT NULL, height INTEGER NOT NULL, depth INTEGER NOT NULL,
    map_source TEXT NOT NULL,
    generator_key TEXT NOT NULL,
    generator_version INTEGER NOT NULL,
    seed TEXT NULL,
    world_time_ticks INTEGER NOT NULL DEFAULT 0,
    created_schema_version INTEGER NOT NULL,
    created_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    updated_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    CHECK (length(trim(name)) > 0),
    CHECK (width >= 1 AND height >= 1 AND depth >= 1),
    CHECK (map_source IN ('generated', 'stored')),
    CHECK (length(trim(generator_key)) > 0),
    CHECK (generator_version >= 1),
    CHECK (created_schema_version >= 1)
);
CREATE UNIQUE INDEX ux_worlds_name ON worlds (name COLLATE NOCASE);
INSERT INTO worlds (world_id, name, width, height, depth, map_source, generator_key, generator_version,
                    seed, world_time_ticks, created_schema_version)
VALUES (1, 'default', 56, 8, 56, 'generated', 'default', 1,
        (SELECT value FROM world_metadata WHERE key = 'simulation_seed'),
        COALESCE((SELECT value FROM world_metadata WHERE key = 'world_time_ticks'), 0), 3);
DELETE FROM world_metadata WHERE key IN ('simulation_seed', 'world_time_ticks');
```

### Per-world table template

- Columns keep their 0001 order, with `world_id` first.
- All 0001 CHECKs and DEFAULTs are kept verbatim.
- Each table gets `FOREIGN KEY (world_id) REFERENCES worlds(world_id) ON DELETE CASCADE`.
- Rows are copied with `INSERT INTO X__v3 (world_id, <old columns>) SELECT 1, <old columns> FROM X;`.

| Table | Primary key and storage | Column type changes | Other foreign keys | Indexes after the rename (same names) |
| --- | --- | --- | --- | --- |
| `player_world_state` | `(world_id, player_name)`, rowid table | none | player → profiles, CASCADE | `ix_player_world_state_updated_utc (updated_utc)` |
| `placed_cube_instances` | `(world_id, cube_id)`, `WITHOUT ROWID` | `cell_x/y/z INTEGER NOT NULL` | owner → profiles, CASCADE | `…_owner_player_name`, `…_updated_utc` |
| `cube_fracture_pieces` | `(world_id, source_cube_id, piece_index)`, `WITHOUT ROWID` | `parent_cell_x/y/z INTEGER NOT NULL` | owner → profiles, CASCADE | `…_owner_player_name`, `ix_cube_fracture_pieces_parent_cell (world_id, parent_cell_x, parent_cell_y, parent_cell_z)`, `…_updated_utc` |
| `enemy_instances` | `(world_id, enemy_id)`, `WITHOUT ROWID` | none | none | `…_updated_utc` |
| `item_instances` | `(world_id, item_instance_id)`, rowid table | none | owner → profiles, SET NULL | `…_owner_player_name`, `…_updated_utc` |
| `player_inventory_slots` | `(world_id, player_name, slot_index)` | none | player → profiles, CASCADE; `(world_id, item_instance_id)` → `item_instances(world_id, item_instance_id)`, CASCADE | `ix_player_inventory_slots_item_instance_id (world_id, item_instance_id)` |
| `player_starter_grants` | `(world_id, player_name)` | none | player → profiles, CASCADE | none |

## Workstreams

Rules for every workstream:
- `dotnet build` passes and the full suite stays green.
- Touch only the files you own.
- 0 B per tick still holds.
- Snapshot payload bytes do not change.

### B1: Schema and world-bound repositories (critical path)

**Owns:**
- `config/migrations/0003_worlds.sql`
- `Migrations/SqliteSchemaMigrator.cs`
- `Repositories/*.cs`, both the main files and the `*.Checkpoint.cs` files
- `SqliteWorldPersistenceRepository.cs`
- `SqlitePersistenceCheckpointWriter.cs`
- `SqliteWorldMetadataRepository*.cs`, which reads and writes the `worlds` row with its public API unchanged:
  - `GetOrCreate` becomes `UPDATE … SET seed=$s WHERE world_id=$w AND seed IS NULL`, then a re-read.
  - `WriteWorldTime` becomes `UPDATE worlds SET world_time_ticks=$t WHERE world_id=$w`.
- `SqlitePlayerProfileRepository.Checkpoint.cs`: the guard's `NOT EXISTS` gains `AND player_world_state.world_id = $worldId`.

**SQL rules:**
- Every statement filters or keys on `world_id = $worldId`.
- The checkpoint binds `$worldId` once per prepared command.
- `ON CONFLICT` targets become the new primary keys.
- Every `ORDER BY` is kept.
- `ReassignEnemyId` stays a single `UPDATE`.

**Existing tests B1 updates.** Only where data is stored and version numbers change; no behaviour assertion changes:
- `SchemaMigrationTests` (counts and backup regex)
- `LegacySchemaMigrationTests` and `LegacyDatabaseServerStartupTests` (backup regex)
- `WorldSimulationSeedTests` (seed on the `worlds` row)
- `WorldClockPersistenceTests` (`world_time_ticks` on the `worlds` row)
- `WorldRoundTripCharacterizationTests`
- `PersistentWorldSeedDeterminismIntegrationTests` (the wipe keeps `worlds`)
- the `PerPlayerInventoryPersistenceTests` query-plan test (binds `$worldId`)
- `SqlitePersistenceRepositoryTests` (use the shipped script and migrations)
- `dream.Tests/Load/WorldSeeder.cs` (adds `world_id`)

**New test files:**
- `Migrations/WorldsMigrationTests.cs`:
  - a fresh v3 database matches the pinned snapshot;
  - a v2 database with data migrates and every row survives, including the slots;
  - an orphan row fails the migration loudly and the version stays at 2;
  - the foreign-keys pragma is restored;
  - WAL is kept.
- `Repositories/WorldScopedRepositoryTests.cs`:
  - two worlds with the same local ids stay isolated;
  - the world 2 checkpoint writer does not touch world 1;
  - writes still enlist in the batch.

**Must not touch:** Simulation, `Server/Worlds`, `ServerBootstrap`, protocol, client, docs.

### B2: Server world selection and building the runtime from the descriptor

**Owns:**
- `ServerBootstrap.cs`:
  - `CreatePersistenceBundle(db, schema, seedOverride, log, ServerStartOptions? start = null)`. The selector is `start.WorldSelector`, otherwise `DREAM_WORLD`.
  - `catalog.Resolve(…, start.NewWorldSize ?? 56)`. Repositories are bound to that world id.
  - Log the world's name and id, never the seed.
  - `CreateRuntimePersistencePorts` passes the world id and sets `World`.
  - `WorldNotFoundException` exits with code 4.
- `ServerPersistenceBundle.cs`, `ServerStartOptions.cs`, `Simulation/Runtime/RuntimePersistencePorts.cs`.
- `Simulation/Runtime/WorldSessionRuntime.cs`:
  - a new constructor taking a `WorldDescriptor`; the `int floorSize` constructor chains to `ForFloorSize`;
  - `public WorldDescriptor World { get; private set; }`;
  - `CreateMap(WorldDescriptor)`;
  - `RebuildMapForFloorSize` updates `World`.
- `Simulation/Runtime/RuntimeAuthoritativeSessionHost.cs`:
  - `CreateDefault(ports)` uses `ports?.World ?? WorldDescriptor.Default`;
  - both join-accepted sites set `World: WorldDescriptorWire.From(Runtime.World)`, cached.

**Tests:**
- `Server/WorldSelectionBootstrapTests.cs`
- `Simulation/Runtime/WorldDescriptorRuntimeTests.cs`:
  - golden: a map built from `WorldDescriptor.Default` has the same cell order as `CreateMap(56)`;
  - a 20×8×20 world builds a 20-wide map;
  - the join message carries the descriptor.

B2 tests against a fake `IWorldCatalog` until B4 lands.

**Must not touch:** repositories, the catalog body, the protocol version, client, `Program.cs`.

### B3: Protocol v7 and a client map built from the descriptor

**Owns:**
- `TransportEnvelope.cs` (`CurrentProtocolVersion = 7`).
- The bodies in `WorldDescriptorWire.cs`.
- `Client/Replication/ClientJoinAcceptedApplier.cs`:
  - It applies the descriptor before the early return.
  - An unsupported generator throws `UnsupportedWorldException`, and the session ends with a readable reason.
  - It rebuilds the map if the size differs.
  - An `InitialWorld` whose size differs from the descriptor is a protocol error.
- `IClientWorldState.cs` (`RebuildMapForWorld(WorldDescriptor)`).
- `RemoteWorldMutationApplier.cs` (on a resync, a size change goes through the current descriptor).
- `Client/World/ClientWorldSession.cs`.
- `Client/Scenes/PlayScene.HudRequests.cs`: the floor-size request only stages and persists the setting; it no longer rebuilds the live map.
- `Rendering/Core/Render.HudPanels.cs` (label "New local world size").
- `Application/Game.cs` (the integrated host factory passes `new ServerStartOptions(NewWorldSize: _settings.FloorSize)`).
- Tests that pin v6.

**Tests:**
- `Multiplayer/Protocol/JoinAcceptedWorldDescriptorCodecTests.cs`:
  - a byte-exact golden for the descriptor;
  - it survives the chunked world transfer;
  - a v6 peer is refused.
- A golden showing snapshot payload bytes are identical apart from the version digit.
- `Client/Replication/ClientWorldDescriptorApplyTests.cs`:
  - a FloorSize-20 client joining a 56 world gets a 56 map before the snapshot;
  - an unsupported generator;
  - a size mismatch.

**Must not touch:** server files, Simulation runtime, persistence.

### B4: World catalog and command line

**Owns:**
- `Server/Persistence/Worlds/SqliteWorldCatalog.cs`:
  - Copy and Delete each run in one owned transaction, with explicit column lists.
  - Create generates the seed with `SqliteWorldMetadataRepository.GenerateSimulationSeed`.
  - Names are trimmed, 1–64 characters, and unique ignoring case.
- `Server/Worlds/*` (the bodies).
- A new `Server/Worlds/WorldCli.cs`:
  - Commands: `--worlds list | create <name> [--size N] | copy <src> <name> | delete <id|name> --yes`.
  - Output is tab-separated: id, name, size, generator, time. It never prints the seed.
  - Exit codes: 0 success, 1 usage error, 2 not found or name conflict, 3 schema too new.
- `Application/Program.cs`: parses `--server [--world <id|name>]` and `--worlds …`.

**Tests:** `Server/Worlds/SqliteWorldCatalogTests.cs` and `WorldCliTests.cs`:
- create, list, copy and delete round trips;
- a deep copy covers all 7 tables, keeps the same ids, and stays independent afterwards;
- deleting a world leaves the others untouched;
- the `Resolve` rules;
- name conflicts;
- a copy of world 1 restores the same cubes.

**Must not touch:** repositories, `ServerBootstrap`, client.

### B5: Safety net, docs and measurements

B5 records the baseline at C0 and merges last.

**Owns these new tests:**
- `Integration/MultiWorldIsolationTests.cs`
- `Integration/WorldRestartRestoreLoopbackTests.cs`
- `Integration/WorldSizeMismatchLoopbackTests.cs`
- `Migrations/V2DatabaseWithDataStartupTests.cs`, with the fixture `Fixtures/v2-with-data.sql`
- `Integration/WorldsLockstepDeterminismTests.cs`:
  - the same script on world 1 and on a copy gives equal checksums on every tick;
  - a migrated world keeps the checksum from its pre-migration seed and time.

**Owns these docs:**
- `server-persistence-ddl.md`
- `architecture.md`: the persistence and worlds sections
- `configuration.md`: `DREAM_WORLD`, `--world`, `--worlds`, exit code 4, and what floor size now means
- the baseline draft `docs/ledger/work-log/2026-10-29-persistence-wave-b-baseline.md`

**Must not touch:** production code.

## Acceptance Criteria

- Persistence measurements, in Release with `DREAM_LOAD_TEST=1`, are re-baselined by B5 at C0. The gate is C0 × 1.10. For reference, wave A measured:
  - World10k full 45.3 ms, incremental 1.27 ms;
  - Smoke8 full 11.7 ms, incremental 1.01 ms;
  - join tick 1.61 ms;
  - burst 31.0 ms.
- Lockstep and determinism goldens are unchanged.
- Snapshot payload bytes are unchanged. The join-accepted message grows by about 130 B, at join only.
- No `NotImplementedException` is left in `Server/Worlds` or `Server/Persistence`.

## Merge Order And Glue

The merge order is C0, B1, B4, B2, B3, B5. Build and run the suite after each merge.

The glue commit then:
- greps for leftover skeletons;
- runs the full suite and the measurements;
- runs `PRAGMA integrity_check` and `foreign_key_check` on a migrated copy of a real database;
- writes the work-log.

## Determinism Notes

- `WorldDescriptor.Default` builds exactly today's `CreateMap(56)`. B2 pins this with a golden.
- The world id never enters simulation state, checksums or snapshots.
- The seed and the world time are copied verbatim.
- Restore order is unchanged, because primary-key order is kept inside a world.
- The client now rebuilds its map at join instead of on the first full snapshot. The resulting map is the same.

## Risks

1. **Foreign keys during table rebuilds.** Mitigated by the migrator change, a test that the inventory slots survive, and restoring the pragma.
2. **Orphan rows make 0003 fail.** The failure is loud, the migration rolls back, and the backup exists.
3. **Cost of the composite keys.** Mitigated by `WITHOUT ROWID` and the measurement gate.
4. **About 11 existing test files need edits.** B1 owns them; only where data is stored and version numbers change.
5. **No lock between processes.** Stop the server before running `--worlds copy` or `--worlds delete`.
6. **Documented behaviour changes:**
   - item ids and the max item id are per world;
   - inventory does not travel between worlds;
   - the HUD floor size no longer resizes a live session;
   - v6 clients are refused.
7. **World ids are never reused (`AUTOINCREMENT`).**
