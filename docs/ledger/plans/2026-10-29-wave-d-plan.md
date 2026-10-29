# Persistence Wave D Plan: Terrain As Data

Purpose:
- Terrain comes from an `IMapSource`.
- Terrain edits on generated worlds survive a restart.
- A map can be exported to versioned chunks and loaded back.
- Generators get a plug point.

Nothing changes on the wire: the protocol stays at v7. Hosting stored worlds and sending terrain to clients move to wave E.

Status: approved. Analysed read-only on `integrate/engine-iteration` at `9f699b9`. The user decisions below use the recommended answers, which match the user's goal of persisting the world itself.

## Decisions

| Id | Decision |
| --- | --- |
| U1 | Destroyed terrain (raised cubes and sculpture cells) stays destroyed after a restart, and its rubble is persisted. |
| U2 | Damaged terrain that is still standing heals on restart, as today. Whether to persist terrain health is decided in wave E. |
| U3 | An admin "reset terrain" command comes in wave E. |
| U5 | Orphan rubble rows already in a database are pruned on the first restart, and the prune is logged. |
| U6 | Migration 0006 adds empty tables only. Terrain destroyed before wave D has already come back. |

## What The Code Does Today

- **Startup terrain.** `Map.Initialize` adds the cells from `WorldGenerator.CreateStartupGeometry` in order, through `TryAddGeometryCell(isStatic: true)`.
  - The order is the generator's `Dictionary` insertion order: x, then z, then y. Sculpture cells replace raised cells in place. New sculpture cells are appended in `HashSet<WorldMicroCellKey>` enumeration order.
  - Physics then adds floor bodies, then merged static sculpture bodies, then per-cell bodies, then supports in `_geometryCells.Keys` order.
- **No terrain mining at runtime.** `Map.ApplyGeometrySubcellSolid` has no production caller.
  - A terrain cell only changes when it is hit to death. `AuthoritativeCombatResolver` then calls `FractureCubeIntoPieces`, which removes the cell and leaves rubble.
  - Terrain never moves. The floor (`y == 0`) cannot be targeted.
- **Why terrain comes back after a restart.** A terrain cube has no `ObjectId` and no owner, so `PersistFracturePieces` returns early. Neither the removal nor the rubble is stored.
- **The join snapshot already carries terrain removals.**
  - `CaptureWorldSnapshot` sends every raised cell: intact cubes, and sculpture cells as `AuthoredSubcellCells`. It also sends the rubble.
  - On the client, `ApplyWorldSnapshot` removes every raised cube that is not listed.
  - So a generated world with removal overrides needs no protocol change.
- **`WorldStateChecksum`** is an order-independent sum over raised intact cubes and rubble.

## Scope

**Wave D, this wave** (C0 plus 4 workstreams):
- D1: `IMapSource`, `MapDefinition` and `GeneratedMapSource`, pinned by goldens before anything moves.
- E2: `IMapGenerator` and a catalog registry.
- D2: migration 0006.
- D3: terrain destruction is persisted as an override that has an `ObjectId`, and its rubble is persisted with that id as the origin. This includes the fix for wave C's orphan-rubble rows.
- D4 core: chunk codec v1, `StoredMapSource` and an exporter, round-tripped through SQLite. Stored worlds cannot be hosted yet: `WorldDescriptor.Validate` still rejects `stored`.

**Wave E, a later wave:**
- E1: hosting stored worlds and sending terrain to clients (protocol v8).
- An export CLI.
- Edits on stored worlds.
- E3: soak tests.
- The cube repair that matches by cell (a known limit from wave C).
- U2 (persisting terrain health) and U3 (reset terrain), if wanted.

## Determinism Rules

1. **Goldens come first, as C0a: a separate commit on untouched code.** The file is `dream.Tests/Simulation/World/MapSourceGoldenTests.cs`.
   - Cases:
     - (56,8,56), (100,8,100), (48,8,48) and (20,8,20) with enriched terrain;
     - (19,8,19), which falls back to flat terrain;
     - (25,8,25) with `generateStartupTerrain: false`.
   - For each case, pin these as `ulong` constants, with counts:
     - **(a) CellOrderHash.** A SplitMix or FNV hash over `Map.GeometryCells` in enumeration order. It covers X, Y, Z, ShapeKind, MaterialId, SubdivisionResolution, IsSolid, IsFloor, whether a subdivision exists, AllChildrenEmpty, and the in-range overrides ascending by linear index. Also pin the same hash over the output of `CreateStartupGeometry`.
     - **(b) `Map.WorldChecksum`.**
     - **(c) PhysicsBodyOrderHash.** Computed with a `PhysicsIntegrator` over the `MapCubeBodies` order and the Bullet collision-object array order. It covers origin float bits, shape type, AABB extents, mass and flags. Also pin the merged static sculpture body count and the cell support body count.
   - The existing tick-equivalence, `SimulationStateChecksum` and snapshot-byte tests stay unchanged. Nothing is re-pinned.
2. **`GeneratedMapSource` delegates to `WorldGenerator.CreateStartupGeometry` unchanged.** The C0a goldens must still pass after C0b.
3. **Overrides are applied after the map is built, through the live removal path.**
   - `Map.RemoveSourceCell` calls `RemoveCube`. It runs in `RestoreWorld`, in `object_id` order, before placed cubes, rubble and enemies, and before the `CubeIdAssigned` hook is set.
   - Two lockstep servers restarted from the same database apply identical removals.
   - A server that fractured a cell live and a server that restarted with the override are not identical in body order, because Bullet swap-removes. This is accepted.
   - Clients generate the base terrain and reconcile it from the join snapshot.
4. **`StoredMapSource` order is canonical: (x, z, y) ascending.** Compared with the generated map it came from, a stored copy has the same cell set, the same checksum and the same collision-body multiset, but a different insertion order. Two maps built from the same chunks are identical in body order.

## C0 Contracts

C0a holds the goldens only. C0b holds everything below. The suite stays green, and behaviour is unchanged apart from `Map` gaining a source.

```csharp
// dream/Simulation/World/MapSources/MapDefinition.cs
public sealed record MapDefinition(int Width, int Height, int Depth, WorldMapSource Source,
    string GeneratorKey, int GeneratorVersion)
{
    public static MapDefinition ForWorld(WorldDescriptor world);   // copies the 6 fields
}
// dream/Simulation/World/MapSources/IMapSource.cs
public interface IMapSource
{
    MapDefinition Definition { get; }
    // The base terrain in canonical insertion order. Every call returns NEW records the caller
    // owns (Map mutates records in place). Deterministic: same source, same sequence. Records
    // have FracturePieces == null and keys inside Definition's bounds.
    IReadOnlyList<GeometryCellRecord> CreateCells();
}
// dream/Simulation/World/MapSources/GeneratedMapSource.cs
public sealed class GeneratedMapSource(MapDefinition definition, bool enrichTerrain = true) : IMapSource
{   // CreateCells => new WorldGenerator().CreateStartupGeometry(W, H, D, enrichTerrain)
}
// dream/Simulation/World/MapSources/IMapGenerator.cs
public readonly record struct MapGenerationRequest(int Width, int Height, int Depth, ulong Seed);
public interface IMapGenerator
{
    string Key { get; }
    int Version { get; }
    IMapSource Create(MapGenerationRequest request);   // generated base; the exporter turns any source into chunks
}
// DefaultMapGenerator: Key "default", Version 1, ignores Seed, returns GeneratedMapSource(enrichTerrain: true).
// dream/Simulation/World/WorldGeneratorCatalog.cs (extended; DefaultKey/DefaultVersion unchanged)
public static IReadOnlyList<IMapGenerator> All { get; }
public static bool TryGet(string key, int version, [NotNullWhen(true)] out IMapGenerator? generator);
public static bool IsSupported(string key, int version) => TryGet(key, version, out _);
// dream/Simulation/World/MapSources/MapSources.cs
public static IMapSource ForWorld(WorldDescriptor world, ulong seed);  // Generated via catalog; Stored => NotSupportedException("stored worlds are hosted from wave E")
```

**`Map` changes.** C0 owns `Map.cs` and `Map.Cubes.cs`. No workstream edits them after C0.

```csharp
public Map(IMapSource source, PhysicsIntegrator? physics, bool replicatedCubeBodies = false, bool headless = false);
// The existing ctor delegates: new GeneratedMapSource(new MapDefinition(w, h, d, Generated, "default", 1), generateStartupTerrain).
public IMapSource Source { get; }
// True while the cell (Y > 0) holds the record it got from Source (or a RestoreGeneratedStartupCells re-add).
public bool IsSourceCell(GeometryCellKey cell);
// Removes a source cell as a live destruction does (RemoveCube), without rubble. False if not a source cell.
public bool RemoveSourceCell(GeometryCellKey cell);
```

- `Initialize` and `RestoreGeneratedStartupCells` call `Source.CreateCells()`.
- `CopyPlacedCubesTo` first calls `RemoveSourceCell` on the target for every source cell this map has removed. These are tracked in an insertion-ordered set that `RemoveCube` fills.
- `WorldSessionRuntime.CreateMap` uses `new Map(MapSources.ForWorld(world, SimulationSeed), Physics, headless: _headlessMap)`.
- The client's `ClientWorldSession` is untouched.

**Persistence contracts** (`Simulation/Persistence/`):

```csharp
public enum TerrainCellOverrideKind { Removed = 0, Replaced = 1 }   // Replaced: reserved, never produced in wave D
public readonly record struct PersistedTerrainCellOverride(ObjectId ObjectId, GeometryCellKey Cell,
    TerrainCellOverrideKind Kind, byte[]? CellData, DateTime CreatedUtc, DateTime UpdatedUtc);
public interface ITerrainOverrideRepository
{
    IReadOnlyList<PersistedTerrainCellOverride> LoadTerrainCellOverrides();   // ORDER BY object_id
    void SaveTerrainCellOverride(PersistedTerrainCellOverride cellOverride);  // insert; an existing row for the cell wins (DO NOTHING)
}
public interface IWorldPersistenceRepository : ..., ITerrainOverrideRepository { }   // appended
public readonly record struct PersistedTerrainChunk(int ChunkX, int ChunkZ, int FormatVersion, int CellCount, byte[] Data);
public interface ITerrainChunkRepository   // NOT on the world facade; admin/export path, not the write queue
{
    IReadOnlyList<PersistedTerrainChunk> LoadTerrainChunks();                  // ORDER BY chunk_z, chunk_x
    void ReplaceTerrainChunks(IReadOnlyList<PersistedTerrainChunk> chunks);    // one transaction: delete the world's chunks, insert all
}
// dream/Simulation/World/Terrain/TerrainChunkFormat.cs
public static class TerrainChunkFormat { public const int CurrentVersion = 1; public const int ChunkSize = 16; }
```

**Combat hook:**
- C0 adds `void PersistTerrainCellRemoved(WorldObjectRecord record, GeometryCellKey cell);` to `IAuthoritativeCombatHost`, with forwarders in `WorldSessionRuntime.Combat.cs` and `.Persistence.cs`, and a no-op in the bridge.
- In the fracture branch of `AuthoritativeCombatResolver`:
  - compute `bool sourceCell = map.IsSourceCell(cell)` BEFORE `FractureCubeIntoPieces`;
  - after `DeletePersistedPlacedCube` and immediately before `PersistFracturePieces`, add `if (sourceCell) _host.PersistTerrainCellRemoved(record, cell);`.

**C0 shims:**
- The facade's `LoadTerrainCellOverrides` returns `[]`, and `Save` is a no-op.
- Every fake that implements `IWorldPersistenceRepository` gets the same two members.

**C0 tests:**
- The C0a goldens.
- `GeneratedMapSourceTests`: fresh records on every call, same order across calls.
- `MapSourceSeamTests`:
  - `RemoveSourceCell` on a raised cell and on a pyramid sculpture cell keeps `FindInvariantViolations` empty and keeps `WorldChecksum == ComputeFull`.
  - After a removal, the collision-export multiset equals that of a map built from a source filtered without that cell.
  - The same removal sequence gives an equal PhysicsBodyOrderHash.
- `WorldGeneratorCatalogTests`.

## Chunk Blob Format v1

Workstream C owns this. The blob is little-endian, uncompressed and deterministic. The decoder throws `InvalidDataException` on any violation.

```
off 0   u8[4]  magic "DTCK"
    4   u16    formatVersion = 1
    6   u16    reserved = 0
    8   i32    chunkX            12  i32 chunkZ          (must equal the row's columns)
   16   u8     sizeX = 16        17  u8 sizeZ = 16       18  u16 height (1..1024)
   20   u16    cellCount (<= sizeX*sizeZ*height)
   22   u8     paletteCount (0 iff cellCount == 0; <= 127)
   23   palette[paletteCount], 4 B each: u8 shapeKind, u8 materialId, u8 subdivisionResolution,
        u8 flags (bit0 IsSolid, bit1 IsFloor; other bits 0). Order: first appearance in ascending i.
        occupancy bitmap: ceil(sizeX*sizeZ*height/8) B, LSB-first; bit i = local cell
        i = ((lx * sizeZ) + lz) * height + y. Bits for cells outside the world bounds must be 0.
        cell bytes[cellCount], one per set bit in ascending i: bit7 = has subdivision state,
        bits0-6 = palette index.
        subdivision records, one per cell with bit7, ascending i:
          u8 resolution (1..40), u8 flags (bit0 AllChildrenEmpty, bit1 has false overrides; others 0)
          set(true overrides); if bit1: set(false overrides)
          set := u8 mode; mode 0: bitmask ceil(res^3/8) B, LSB-first, child c = (sx*res + sy)*res + sz;
                 mode 1: u16 count, then count x u16 ascending c. Encoder picks the shorter; tie -> mode 0.
        no trailing bytes.
```

The encoder throws on overrides outside `[0, res)`, on fracture pieces, and on more than 127 palette entries. Encode and decode are exact inverses at the level of effective records.

## Migration 0006 (`dream/config/migrations/0006_terrain.sql`)

```sql
-- Migration 0006: terrain as data (persistence wave D). New tables only; no existing row changes.
CREATE TABLE terrain_cell_overrides (
    world_id INTEGER NOT NULL,
    object_id INTEGER NOT NULL,
    cell_x INTEGER NOT NULL,
    cell_y INTEGER NOT NULL,
    cell_z INTEGER NOT NULL,
    kind INTEGER NOT NULL,
    cell_data BLOB NULL,
    created_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    updated_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    PRIMARY KEY (world_id, object_id),
    FOREIGN KEY (world_id) REFERENCES worlds(world_id) ON DELETE CASCADE,
    CHECK (object_id >= 1),
    CHECK (cell_x >= 0 AND cell_y >= 1 AND cell_z >= 0),
    CHECK (kind IN (0, 1)),
    CHECK ((kind = 0 AND cell_data IS NULL) OR (kind = 1 AND cell_data IS NOT NULL))
);
CREATE UNIQUE INDEX ux_terrain_cell_overrides_cell
    ON terrain_cell_overrides (world_id, cell_x, cell_y, cell_z);
CREATE TRIGGER trg_terrain_cell_overrides_advance_object_id AFTER INSERT ON terrain_cell_overrides BEGIN
    UPDATE worlds SET next_object_id = NEW.object_id + 1
    WHERE world_id = NEW.world_id AND next_object_id <= NEW.object_id;
END;
CREATE TABLE terrain_chunks (
    world_id INTEGER NOT NULL,
    chunk_x INTEGER NOT NULL,
    chunk_z INTEGER NOT NULL,
    format_version INTEGER NOT NULL,
    cell_count INTEGER NOT NULL,
    data BLOB NOT NULL,
    updated_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    PRIMARY KEY (world_id, chunk_x, chunk_z),
    FOREIGN KEY (world_id) REFERENCES worlds(world_id) ON DELETE CASCADE,
    CHECK (chunk_x >= 0 AND chunk_z >= 0),
    CHECK (format_version >= 1),
    CHECK (cell_count >= 0),
    CHECK (length(data) >= 23)
) WITHOUT ROWID;
```

Overrides are saved with `INSERT … ON CONFLICT(world_id, cell_x, cell_y, cell_z) DO NOTHING`.

## Workstreams

Rules for every workstream:
- Touch only the files you own.
- The build and the full suite pass. Any skip reads `wave D: needs <X>, unskip at glue` and is listed in the hand-off.
- 0 B per tick.
- Goldens, checksum and snapshot-byte tests stay unchanged.
- Measure in Release with `DREAM_LOAD_TEST=1`, two alternating back-to-back rounds against a C0 build.

### A: Schema and SQL (D2)

**Owns:**
- `config/migrations/0006_terrain.sql`;
- the new `Server/Persistence/Repositories/SqliteTerrainOverrideRepository.cs` and `SqliteTerrainChunkRepository.cs`;
- `SqliteObjectIdCounterRepository.cs` (the heal guard adds `terrain_cell_overrides`);
- `SqliteWorldPersistenceRepository.cs` (forward to the new repositories, drop the shim);
- `Worlds/SqliteWorldCatalog.cs` (Copy copies overrides with the same ids, and the chunks);
- `ServerPersistenceBundle.cs` (exposes `ITerrainChunkRepository` per world).

**Tests owned:**
- the new `Migrations/TerrainMigrationTests.cs` and `Repositories/TerrainRepositoryTests.cs`;
- the version assertions in `Migrations/*`, `SqliteWorldCatalogTests` and `WaveBTesterCliTests`;
- `ObjectIdTestSupport.DumpWorld`, which adds both tables, ordered;
- `Load/WorldSeeder.cs` and `LoadScenario.cs`: add `WorldSeeder.SeedTerrainOverrides(databasePath, world, count)`. It takes the first `count` raised, non-floor cells of `GeneratedMapSource`, with ids from the counter and no rubble.

**Acceptance:**
- Fixtures v0 to v5 migrate to v6 with `integrity_check` ok, `foreign_key_check` empty, and 5 triggers.
- Every CHECK rejects bad rows.
- A duplicate cell insert is ignored and leaves the counter alone.
- The heal guard covers overrides.
- Deleting a world cascades.
- Copy preserves overrides, chunks and the counter.
- `ReplaceTerrainChunks` is atomic.

**Must not touch:** `Simulation/**`, protocol, client, docs.

### B: Runtime terrain edits (D3 and the orphan-rubble fix)

**Owns:** `Simulation/Persistence/RuntimePersistenceBridge.cs` and `.Checkpoints.cs`, a new `Simulation/Persistence/TerrainEditPersistenceTests.cs`, and existing bridge tests as needed.

**Behaviour:**
1. **`PersistTerrainCellRemoved`.**
   - Return if there is no persistence, or if `_persistedTerrainOverrides` already holds the cell.
   - Otherwise:
     - take an id with `_objectIds.Take()`;
     - enqueue `SaveTerrainCellOverride(Removed)`;
     - record the cell in `_persistedTerrainOverrides`;
     - set `_lastRetiredCube = new(record.Actor.Id, id)`.

     `PersistFracturePieces` then uses that id as the origin, and the pieces get their own ids with a null owner.
2. **Orphan-rubble fix in `PersistFracturePieces`.** If a group already exists for the cell, enqueue `DeleteCubeFracturePieces(old.Origin)` and add a fracture tombstone before writing the new group.
3. **`RestoreWorld`.** After `_objectIds.Restore` and before restoring cubes:
   - load the overrides and reserve their ids;
   - for each `Removed` override, call `map.RemoveSourceCell(cell)`. If that returns false, log `server.persistence.terrain-override-skipped count= reason=not-a-source-cell` and keep the row;
   - for each `Replaced` override, log `terrain-override-unsupported` and skip it.
4. **Rubble restore.** When several groups map to one cell, keep the highest origin and enqueue deletes for the others. Log `server.persistence.orphan-rubble-pruned count=` (U5).
5. **Checkpoints.** Terrain rubble rides the existing incremental capture of dirty rubble. Overrides are written once and are never checkpointed. Ownerless groups are captured.

**Tests:**
- A terrain cube fracture writes 1 override plus N pieces, with the override as their origin.
- After a restart:
  - the cell is absent;
  - the rubble is restored;
  - the ids are reserved and unique across kinds.
- A sculpture cell behaves the same, and the invariants still hold.
- A cube placed in a removed terrain cell persists and restores.
- The wave C repro: a cube placed over terrain rubble and then broken leaves 0 orphan rows.
- An out-of-bounds override is skipped and logged, and its row is kept.
- An incremental checkpoint after a terrain fracture holds only piece rows.
- The resolver call order (delete, terrain-removed, fracture-persist) is pinned.

**Must not touch:** `Map*.cs`, the resolver, SQL, protocol, client.

### C: Chunk codec, StoredMapSource and exporter (D4 core)

**Owns** these new files under `Simulation/World/Terrain/`:
- `TerrainChunkCodec.cs`.
- `StoredMapSource.cs`: `StoredMapSource(MapDefinition, IReadOnlyList<PersistedTerrainChunk>)`. It validates coverage and format version, and every `CreateCells` call returns freshly decoded records in canonical (x, z, y) order.
- `TerrainChunkExporter.cs`, with two entry points:
  - `Export(IMapSource)`;
  - `Export(Map)`, which exports only cells where `IsSourceCell` holds, so removed cells are baked out and placed cubes and rubble are excluded.

  Output is ordered by `chunk_z`, then `chunk_x`, and includes edge chunks.

Tests go in `dream.Tests/Simulation/World/Terrain/*`.

**Tests:**
- Golden blob bytes for a hand-built chunk, as hex.
- Both set modes, and the tie rule.
- Rejection cases: bad magic, version, flags, trailing bytes, out-of-bounds bits, palette overflow.
- Encode, decode, then encode again gives identical bytes.
- For (56,8,56) and (100,8,100), a map built from a stored source exported from the generated source has:
  - an equal `WorldChecksum`;
  - an equal cell set with equal effective records;
  - an equal collision-body multiset and body count;
  - the same PhysicsBodyOrderHash in two such maps.
- `Export(map)` after `RemoveSourceCell` omits the removed cell.

**Report:** blob bytes, and encode and decode time, for World10k.

**Must not touch:** `Map*.cs`, persistence, server, protocol.

### D: Safety net, measurements and docs

**Owns:**
- the new `Integration/TerrainPersistenceLoopbackTests.cs`:
  - destroy terrain and a sculpture cell;
  - restart, and let a client join;
  - the client's checksum equals the server's, the raised key sets are equal, and the rubble cells are equal.
- the new `Integration/TerrainLockstepRestartTests.cs`: two servers with terrain fractures and two restarts each. Checksums and `DumpWorld` are equal on every tick.
- the new `Integration/StoredTerrainRoundTripTests.cs`: export, then replace, load, `StoredMapSource`, and check map equality.
- the new `Load/TerrainMeasurementTests.cs`.
- docs: `docs/server-persistence-ddl.md` and `docs/architecture.md` (map sources, overrides, chunk format).
- `docs/ledger/work-log/2026-10-29-persistence-wave-d-baseline.md`, measured first, at C0.

**Measurements on World10k (100×8×100), C0 against the merged build:**
- full and incremental checkpoint, the 1,000-write burst, and the join tick: each at most C0 × 1.10;
- transfer bytes for an unedited join: byte-identical to C0;
- with 1,000 seeded overrides: restore time, join tick, transfer bytes, and that the client checksum matches;
- the stored map:
  - chunk count;
  - blob bytes, with a target of at most 256 KB;
  - encode time, and the time for `ReplaceTerrainChunks`;
  - load plus build time, with a target of at most 1.5 × the generated build.

The join and transfer for stored worlds cannot be measured until E1. Record the blob bytes, and the same × 4/3 for base64, as E1's budget.

**Must not touch:** any production code.

## Merge Order And Glue

Merge in this order: C0a, C0b, A, C, B, D. Build and test after each merge.

Glue then:
- removes the remaining shims;
- unskips D's tests;
- migrates the real legacy databases to v6;
- runs D's measurements back to back against C0;
- runs the perf harness.

## Risks

1. **The goldens depend on `HashSet<WorldMicroCellKey>` enumeration order.** The goldens are there to catch a change in it.
2. **`RemoveSourceCell` on sculpture cells, against the merged static collision.** If C0's multiset test fails, that is a real bug in the live fracture path too. Stop and report it.
3. **Counter safety.** A's migration test asserts the 5 triggers.
4. **The `_lastRetiredCube` coupling.** B pins the resolver call order.
5. **Orphan pruning deletes rows on the first restart.** It is logged, and only removes groups the map already shadowed.
6. **The test-only `RebuildMapForFloorSize`.** The `CopyPlacedCubesTo` change keeps terrain removals across the rebuild.
7. **Stored worlds can exist in the database but cannot be hosted until wave E.** This is intentional.
