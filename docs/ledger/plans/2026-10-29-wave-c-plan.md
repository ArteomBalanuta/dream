# Persistence Wave C Plan: Stable Object Ids

Purpose: give every persisted world object a 64-bit `ObjectId` that is never reused. The objects are placed cubes, rubble pieces, enemies and items. The ids come from a counter stored on each world's row. The int actor id stays the identity on the wire, in checksums and in RNG seeds, and the server maps between the two at runtime. The protocol stays at v7.

Status: approved. The architect analysed `integrate/engine-iteration` at `d1ec7e0` read-only. This plan uses the same format as the wave B plan.

## Decisions

- **Where ids are allocated.** On the tick thread, from one `ObjectIdAllocator` owned by the runtime. `RuntimePersistenceBridge` allocates for cubes, rubble and enemies; `PlayerInventoryService` allocates for items.
  - Lockstep databases are compared row by row (`Wave13PersistentLockstepDeterminismTests.DumpWorld`), so allocation must be deterministic.
- **Never reused across a crash.** An `AFTER INSERT` trigger on each of the 4 object tables raises the stored counter in the same statement as the insert: `UPDATE worlds SET next_object_id = NEW.<id> + 1 WHERE world_id = NEW.world_id AND next_object_id <= NEW.<id>`. Restore seeds the allocator from `worlds.next_object_id`. Rejected alternatives:
  - A high-water mark written by the checkpoint. Checkpoints only UPDATE, so this would not be atomic with the inserts.
  - Reserving blocks of ids. That needs a database write before the tick can allocate.
  - Deriving the counter from MAX. That reuses the id of the highest deleted object.

  An id reissued after a crash belonged to an insert that never committed, so nothing references it.
- **Startup guard.** `LoadNextObjectId()` returns `max(stored, MAX(id)+1)` over the 4 tables and the inventory slots, and logs `server.persistence.object-id-counter-healed` when it has to raise the counter. The bridge also reserves every loaded id.
- **Wire and checksums are unchanged.** `ObjectId` never enters `SimulationStateChecksum`, `WorldStateChecksum`, snapshots or RNG seeds.
- **Rubble needs no wire change.** Piece ids are server-only. The bridge's fracture group holds `ObjectId Origin` and `ObjectId[] PieceIds`, indexed by map piece index. A cell's piece list is fixed once it fractures.
- **Restored actor ids.** Restore no longer uses persisted ids as actor ids. Cubes go through `RegisterCube(actorId: null)` and enemies through `TryAllocateEnemyActorId`, both in `ORDER BY object_id` order, and are then bound in `ObjectIdentityMap`. `ReassignEnemyId` and the reservation of legacy cube ids that fall in the enemy range are deleted.
- **Items (C5) share the allocator.** The column keeps the name `item_instance_id` and holds an ObjectId value; the C# type stays `long`. Without persistence nothing allocates through the bridge, so item ids still start at 1.
- **Owners.** `owner_player_name` becomes nullable with `ON DELETE SET NULL` on cubes and rubble, and `OwnerPlayerName` becomes `string?`.
  - New checkpoint rule: a cube is captured when its actor id has an `ObjectId` binding, whatever its owner.
  - A new placement still needs a valid owner to get an `ObjectId`.
  - Terrain cubes never get one before wave D.
- **Backfill in 0005.** Renumbering is deterministic, per world:
  - `base = MAX(item ids, slot ids) + 1`.
  - Ids are assigned in this order: cubes (by `cube_id`), rubble groups (distinct `source_cube_id`, which become retired origin ids), pieces (by `source_cube_id, piece_index`), then enemies (by `enemy_id`).
  - `next_object_id = base + count`.
  - Items keep their ids.

## C0 Contract Commit

C0 changes no behaviour, and the suite stays green.

```csharp
// dream/Simulation/World/ObjectId.cs
public readonly record struct ObjectId : IComparable<ObjectId>
{
    public static readonly ObjectId None = default;           // Value 0: never allocated
    public ObjectId(long value) { ArgumentOutOfRangeException.ThrowIfLessThan(value, 1L); Value = value; }
    public long Value { get; }
    public bool IsNone => Value == 0;
    public int CompareTo(ObjectId other) => Value.CompareTo(other.Value);
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}
// dream/Simulation/World/ObjectIdAllocator.cs   (fully implemented in C0, tick thread only)
public sealed class ObjectIdAllocator
{
    public ObjectIdAllocator(long next = 1);
    public long Next { get; }                 // value Take() returns next
    public ObjectId Take();                   // InvalidOperationException at long.MaxValue
    public void Restore(long storedNext);     // Next = Math.Max(Next, storedNext)
    public void Reserve(ObjectId taken);      // Next = Math.Max(Next, taken.Value + 1); None ignored
}
// dream/Simulation/World/ObjectIdentityMap.cs   (fully implemented in C0; cubes and enemies share it, actor ranges are disjoint)
public sealed class ObjectIdentityMap
{
    public int Count { get; }
    public void Bind(ObjectId objectId, int actorId);         // drops any earlier binding of either side
    public bool TryGetObjectId(int actorId, out ObjectId objectId);
    public bool TryGetActorId(ObjectId objectId, out int actorId);
    public bool RemoveByActorId(int actorId, out ObjectId objectId);
    public bool RemoveByObjectId(ObjectId objectId);
    public void Clear();
}
// Records (Simulation/Persistence)
public readonly record struct PersistedPlacedCube(ObjectId ObjectId, string? OwnerPlayerName, Vector3 CellPosition,
    GeometryShapeKind ShapeKind, GeometryMaterialId MaterialId, int CurrentHealth, int MaxHealth,
    DateTime CreatedUtc, DateTime UpdatedUtc, Vector3? CenterPosition = null);
public readonly record struct PersistedCubeFracturePiece(ObjectId ObjectId, ObjectId OriginObjectId, int PieceIndex,
    string? OwnerPlayerName, Vector3 ParentCell, Vector3 Center, float SideLength, GeometryMaterialId MaterialId,
    DateTime CreatedUtc, DateTime UpdatedUtc);
public readonly record struct PersistedEnemyInstance(ObjectId ObjectId, string EnemyKind, string DisplayName,
    Vector3 Position, Vector3 Facing, PersistedCreatureStats Stats, DateTime CreatedUtc, DateTime UpdatedUtc);
// PersistedItemInstance / PersistedInventorySlot: unchanged (long ids).
// Interfaces
IPlacedObjectRepository.DeletePlacedCube(ObjectId objectId);
IRubbleRepository.DeleteCubeFracturePieces(ObjectId originObjectId);
IEnemyRepository.DeleteEnemyInstance(ObjectId objectId);
// IEnemyRepository.ReassignEnemyId(int,int) and IItemRepository.GetMaxItemInstanceId(): KEPT, comment "removed by the wave C glue".
public interface IObjectIdCounterRepository { long LoadNextObjectId(); }   // Simulation/Persistence/IObjectIdCounterRepository.cs
public interface IWorldPersistenceRepository : ..., IObjectIdCounterRepository { }
// PersistenceCheckpoint tombstones (renamed and retyped):
public IReadOnlyList<ObjectId> RemovedCubeObjectIds { get; init; } = [];
public IReadOnlyList<ObjectId> RemovedEnemyObjectIds { get; init; } = [];
public IReadOnlyList<ObjectId> RemovedFractureOriginObjectIds { get; init; } = [];
// Wiring (WorldSessionRuntime.cs, C0 only; no workstream edits this file afterwards):
private readonly ObjectIdAllocator _objectIds = new();  internal ObjectIdAllocator ObjectIds => _objectIds;
private readonly ObjectIdentityMap _objectIdentities = new(); internal ObjectIdentityMap ObjectIdentities => _objectIdentities;
new PlayerInventoryService(worldPersistence, _writes, _objectIds)          // C0: stored, not used yet
new RuntimePersistenceBridge(..., _events, _objectIds, _objectIdentities)  // appended ctor params
```

**C0 shims, which later workstreams remove:**

- **Bridge.**
  - `ObjectId` is `new ObjectId(actorId)` everywhere.
  - A rubble origin is `new ObjectId(sourceCubeId)`, and a piece's `ObjectId` is `None`.
  - On restore, an id inside the world-object or enemy actor range keeps today's path. Any other id gets a newly allocated actor id.
  - `RestoreWorld` begins with `_objectIds.Restore(_worldPersistence.LoadNextObjectId())`.
- **SQL.**
  - Repositories bind `.Value` into today's columns and read them with `GetInt64`.
  - Loading rubble returns `ObjectId.None` for each piece.
  - `SqliteWorldPersistenceRepository.LoadNextObjectId` returns MAX+1 over `cube_id`, `enemy_id`, `source_cube_id`, item ids and slot ids.
- **Merge.** `PersistenceCheckpoint.Merge` keeps keying pieces by `(OriginObjectId, PieceIndex)`.
- **Tests.**
  - C0 does the mechanical compile fixes in the affected test files and every fake: it adds `public long LoadNextObjectId() => 1;` and renames `CubeId`/`EnemyId` to `ObjectId`. No assertion changes meaning.
  - C0 adds `dream.Tests/Simulation/World/ObjectIdTests.cs` for `ObjectId`, the allocator and the identity map.

## Migration 0005 (`dream/config/migrations/0005_object_ids.sql`)

Use wave B's rebuild pattern:
- Steps: create `X__v5`, `INSERT … SELECT`, `DROP X`, rename `X__v5` to `X`, then recreate the indexes.
- Never rename the old table first.
- The migrator's foreign-keys OFF, `foreign_key_check` and lock handling already exist. Don't change them.

1. `ALTER TABLE worlds ADD COLUMN next_object_id INTEGER NOT NULL DEFAULT 1;`. Don't rebuild `worlds`: it has 7 child FKs.
2. Create the scratch table `object_id_backfill__v5(world_id, kind, key_a, key_b, object_id, PRIMARY KEY(world_id, kind, key_a, key_b))`. Fill it with one `INSERT … SELECT` over a `UNION ALL` of these kinds:
   - kind 1, cubes: (`cube_id`, 0);
   - kind 2, rubble groups: `SELECT DISTINCT` (`source_cube_id`, 0);
   - kind 3, pieces: (`source_cube_id`, `piece_index`);
   - kind 4, enemies: (`enemy_id`, 0).

   The id is `object_id = base + ROW_NUMBER() OVER (PARTITION BY world_id ORDER BY kind, key_a, key_b) - 1`, where `base = COALESCE(MAX(item ids in item_instances and player_inventory_slots for that world), 0) + 1`.
3. Rebuild these tables. Keep all 0003 CHECKs and DEFAULTs verbatim unless listed below.

   | Table | Columns | Keys and indexes | Owner |
   | --- | --- | --- | --- |
   | `placed_cube_instances` | `world_id, object_id, owner_player_name NULL, …` | `PK(world_id, object_id)` | FK `ON DELETE SET NULL`; `CHECK (owner_player_name IS NULL OR length(trim(owner_player_name)) > 0)` |
   | `cube_fracture_pieces` | `world_id, object_id, origin_object_id, piece_index, owner_player_name NULL, …` | `PK(world_id, object_id)`; `UNIQUE INDEX ux_cube_fracture_pieces_origin (world_id, origin_object_id, piece_index)` | `SET NULL`, with the same nullable CHECK |
   | `enemy_instances` | `object_id` replaces `enemy_id` | `PK(world_id, object_id)` | none |

   All three stay rowid tables. Recreate the owner and `parent_cell` indexes, but NOT the `updated_utc` indexes, which 0004 dropped.
4. Per world, `UPDATE worlds SET next_object_id = MAX(base, COALESCE(MAX(backfilled object_id), 0) + 1)`. Then drop the scratch table.
5. Create the `AFTER INSERT` triggers `trg_<table>_advance_object_id` on `placed_cube_instances`, `cube_fracture_pieces`, `enemy_instances` and `item_instances` (on `item_instance_id`). Any later rebuild of these tables must recreate them.

## Workstreams

Rules for every workstream:
- Touch only the files you own.
- Build and the full suite must pass. A test may be skipped only when it needs another workstream; its reason reads `wave C: needs <X>, unskip at glue`, and the test is listed in the hand-off.
- 0 B per tick.
- These stay unchanged: the `WorldSessionRuntimeTickEquivalenceTests` goldens, `SimulationStateChecksumTests`, and the snapshot-byte tests.
- Persistence measurements stay within C0 × 1.10. Measure in Release with `DREAM_LOAD_TEST=1`, using two alternating back-to-back rounds against a C0 build.

### A: Schema, SQL and the counter (critical path)

**Owns:**
- `config/migrations/0005_object_ids.sql`;
- `Server/Persistence/Repositories/*.cs`, including the `*.Checkpoint.cs` files;
- `SqliteWorldPersistenceRepository.cs`, `SqlitePersistenceCheckpointWriter.cs`, `Worlds/SqliteWorldCatalog.cs`;
- a new `Repositories/SqliteObjectIdCounterRepository.cs`.

**Repository changes:**
- Every statement keys on `object_id`, with `ON CONFLICT(world_id, object_id)`.
- Load order:
  - cubes and enemies: `ORDER BY object_id`;
  - rubble: `ORDER BY origin_object_id, piece_index`.
- Rubble checkpoint: `UPDATE … WHERE world_id = <literal> AND object_id = $objectId`.
- `DeleteCubeFracturePieces` deletes `WHERE origin_object_id = $origin`.
- Owners are nullable.

**Counter and catalog:**
- `LoadNextObjectId` returns the stored counter, through the heal guard.
- `ReassignEnemyId` and `GetMaxItemInstanceId` must still compile. Either rewrite them or make them throw `NotSupportedException`.
- Catalog Copy uses the new columns and also copies `next_object_id`.

**Tests owned:**
- `Migrations/*`;
- `SqlitePersistenceRepositoryTests`, `Repositories/WorldAggregateRepositoryTests`, `Repositories/WorldScopedRepositoryTests`;
- `WorldRoundTripCharacterizationTests`, `PersistenceGroupCommitTests`, `PersistenceThroughputMeasurementTests`, `WorldClockPersistenceTests`;
- `Server/Worlds/SqliteWorldCatalogTests`, `Server/Worlds/WaveBTesterCliTests` (version assertions);
- `Load/WorldSeeder.cs`;
- `Integration/Wave13PersistentLockstepDeterminismTests.cs`, which needs `DumpWorld` columns updated.

**New test, `Migrations/ObjectIdsMigrationTests.cs`**, with the fixture `Fixtures/v4-with-data.sql`:
- A v4 database with data, including a cube and an enemy that share a legacy id, migrates to deterministic ids and the expected counter.
- The inventory slots survive.
- An orphan row fails the migration loudly, and the version stays at 4.
- The triggers raise the counter and never lower it.
- An upsert leaves the counter alone.
- Deleting a profile sets the owner to null on its cubes and rubble, while its slots cascade away.
- The heal guard raises a stale counter and logs that it did.

**Must not touch:** `Simulation/**`, `ServerBootstrap`, protocol, client, docs.

### B: Runtime identity for cubes, rubble and enemies (C2–C4, plus the owner rule)

**Owns:**
- `Simulation/Persistence/RuntimePersistenceBridge.cs` and `.Checkpoints.cs`;
- `PersistenceCheckpoint.cs`, `IRuntimePersistenceHost.cs`;
- `IEnemyRepository.cs`, `IPlacedObjectRepository.cs`, `IRubbleRepository.cs`;
- `RepositoryPersistenceCheckpointWriter.cs`;
- `Simulation/Runtime/WorldSessionRuntime.Persistence.cs` and `WorldSessionRuntime.Enemies.cs`;
- `Simulation/World/WorldObjectRegistry.cs` and `ReplicatedActorIdRanges.cs`, comments only.

**Restore:**
- Seed the allocator: `Restore`, then `Reserve` each loaded id.
- Cubes: `RegisterCube(actorId: null)`, then `Bind`. A skipped cube stays in `_persistedCubes`, which is now keyed by `ObjectId`.
- Rubble: group pieces by `OriginObjectId`. Build `PieceIds` aligned to the indices `RestoreFracturePieces` keeps, compacting the same way it does.
- Enemies: `TryAllocateEnemyActorId`, then `Bind`.
- Delete the reserve and reassign code, and `IRuntimePersistenceHost.ReserveEnemyActorId`.

**Persist:**
- `PersistPlacedCube` reuses the bound id, or calls `Take()` when the owner is valid. Drop the `DeleteCubeFracturePieces(cubeId)` call.
- `DeletePersistedPlacedCube(int actorId)`:
  1. `RemoveByActorId`;
  2. queue the delete;
  3. add the id to the bridge-owned `_removedCubeObjectIds`;
  4. keep the id in a one-slot `_lastRetiredCube`, so the `PersistFracturePieces` call that follows in the same tick can resolve its origin.
- `PersistFracturePieces` calls `Take()` once per piece.
- `PersistEnemy` calls `Take()` for every new enemy; a respawn is a new object.
- `DeletePersistedEnemy` removes the binding and queues the delete. It is a no-op for an unbound enemy.
- Allocate only when `_worldPersistence != null`.

**Repair:** `RepairCubeRecordFromPersistence` compares the bound `ObjectId` and the owner, null-safe. It re-identifies with the actor id bound to that `ObjectId`, falling back to `record.Actor.Id`, then calls `Bind`.

**Capture:**
- A cube is captured when it has a binding and `cell.Y != 0`. A null owner is fine.
- Enemies, including `_checkpointEnemies`, are keyed by `ObjectId`, and unbound ones are skipped.
- A piece resolves to `group.PieceIds[index]`.
- Tombstones go into the new lists.

**Merge:** pieces are keyed by `ObjectId`, and tombstones by `OriginObjectId`.

**Tests owned:**
- `RuntimePersistenceBridgeTests`, `CheckpointPersistenceTests`, `IncrementalCheckpointTests`, `CheckpointLeaveRaceGuardTests`;
- `Wave8PersistenceCrashAndJoinTests`, `Wave9FrozenCubeCheckpointTests`, `WaveT2LampPersistenceTests`;
- `PersistenceOffTickTests`, `JoinPersistencePrefetchTests`;
- `ReplicatedIdIntegrationTests`, `ServerActorIdentityIntegrationTests`, `PersistentWorldPlacedCubeDamageIntegrationTests`;
- `Wave13ExtractedSeamsLoopbackTests`, `AuthoritativeCombatResolverTests`, `RebuildMapCombatCheckpointTests`, `RuntimeAuthoritativeSessionHostTests`.

**Replace `EnemyReassignPersistenceTests` with `EnemyObjectIdPersistenceTests`:**
- a respawn gets a new id;
- restore keeps the `ObjectId` and gives a new actor id;
- a legacy cube/enemy id collision restores both.

**New, `Simulation/Persistence/ObjectIdentityBridgeTests.cs`:**
- restore order fixes the actor ids;
- a cube with no owner is still checkpointed;
- after a fracture, each piece has its own id and the origin is kept;
- tombstones merge by `ObjectId`;
- a map rebuild keeps the bindings;
- a capture with no changes allocates 0 B;
- the delete-then-fracture call order is pinned.

**Must not touch:** `Server/**`, `PlayerInventoryService.cs`, `WorldSessionRuntime.Inventory.cs`, `WorldSessionRuntime.cs`, `AuthoritativeCombatResolver.cs`, protocol.

### C: Items on the shared allocator (C5)

**Owns:** `Simulation/Inventory/PlayerInventoryService.cs`, `Simulation/Runtime/WorldSessionRuntime.Inventory.cs`, and `Simulation/Persistence/IItemRepository.cs` (comment only).

**Changes:**
- `GrantItem` uses `_objectIds.Take().Value`.
- Remove `RestoreItemIdCounter` and its call, since the bridge seeds the allocator.
- `ApplyLoaded` calls `_objectIds.Reserve(...)`.
- `NextItemInstanceId` returns `_objectIds.Next`.

**Tests owned:** `PlayerInventoryServiceTests`, `PerPlayerInventoryPersistenceTests`, `Wave12InventoryLoopbackTests`, and the starter-grant tests.

**New, `Simulation/Inventory/ItemObjectIdTests.cs`:**
- item ids never collide with cube or enemy ids;
- a non-persistent runtime grants ids 1..N;
- ids are unique across a restart.

**Must not touch:** the bridge, `Server/**`, protocol.

### D: Safety net, docs and baseline (merges last)

**New tests:**
- `Integration/ObjectIdRestartLoopbackTests.cs`
- `Integration/ObjectIdCrashSafetyTests.cs`: with `WriteCheckpointOnDispose = false`, every persisted id is below the stored counter, and no persisted id is reissued.
- `Integration/BuildingsOutliveBuilderTests.cs`: delete a profile row and restart. The cube and rubble are restored with no owner, are checkpointed, and can be damaged.
- `Integration/WaveCLockstepDeterminismTests.cs`: two lockstep servers, with a restart in the middle. Checksums are equal on every tick, and the databases are equal, including `object_id` and `next_object_id`.

Skip each one until glue, with the reason naming the workstream it needs.

**Docs:**
- `docs/server-persistence-ddl.md`
- `docs/architecture.md`, the persistence section
- the baseline `docs/ledger/work-log/2026-10-29-persistence-wave-c-baseline.md`, measured at C0

**Must not touch:** production code, and the tests owned by A, B or C.

## Merge Order And Glue

The merge order is C0, A, B, C, D. Build and test after each merge.

The glue commit:
- removes `ReassignEnemyId` and `GetMaxItemInstanceId`, with their SQL, facade forwarders and fake members;
- removes the C0 shims;
- greps for `cube_id`, `enemy_id`, `source_cube_id`, `ReserveEnemyActorId` and the skip reasons, and unskips;
- runs the full suite at most 2 times;
- runs the measurements against C0 in two alternating rounds;
- runs `integrity_check` and `foreign_key_check` on a migrated real v4 database;
- writes the work-log.

## Determinism Notes

- Actor ids remain the only identity in checksums, on the wire and in RNG seeds. `ObjectId` exists only in persistence.
- Without persistence nothing is allocated through the bridge, so goldens, checksum tests and snapshot bytes do not move. Nothing is re-pinned.
- **Deliberate change.** After a restart, restored cubes and enemies get compact actor ids in `object_id` order: cubes from 1000, enemies from 2000. Rolls seeded by actor id after a restart therefore differ from wave B. No golden pins this. Registration order is unchanged, because 0005 keeps `cube_id` order.
- In persistent worlds, item ids now interleave with ids of other kinds.

## Risks

1. **Trigger cost on inserts**, which affects the write burst and the join tick. Fallback: one counter UPDATE per queued closure.
2. **C0 is large but mechanical.** If the agent dies, the coordinator finishes it.
3. **Skips local to a branch** are listed in each hand-off and removed at glue.
4. **`_lastRetiredCube`** depends on the resolver calling delete, then fracture, in the same tick. B pins this with a test.
5. **Any future rebuild of these tables must recreate the 4 triggers.** A migration test checks this.
6. **Orphan rubble rows are still never deleted.** Deferred to wave D.
