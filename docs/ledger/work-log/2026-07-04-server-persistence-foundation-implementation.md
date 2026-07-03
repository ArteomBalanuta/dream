Summary: Implemented the first server-bound persistence foundation for player profiles, player reconnect state, active enemies, and player-owned placed cubes.
Date: 2026-07-04
Related:
- [Analysis: Server Persistence Entity Hierarchy And Denormalized Schema](/G:/workspace/projects/dream/docs/ledger/analysis/2026-07-04-server-persistence-entity-hierarchy-and-denormalized-schema.md)
- [Spec: Server Persistence Entity Hierarchy And Denormalized Schema](/G:/workspace/projects/dream/docs/ledger/specs/2026-07-04-server-persistence-entity-hierarchy-and-denormalized-schema.md)
- [Plan: Server Persistence Entity Hierarchy And Denormalized Schema](/G:/workspace/projects/dream/docs/ledger/plans/2026-07-04-server-persistence-entity-hierarchy-and-denormalized-schema.md)

## Completed

- Expanded `dream/config/player-profiles.init.sql` into a denormalized server schema covering:
  - `player_profiles`
  - `player_world_state`
  - `placed_cube_instances`
  - `enemy_instances`
  - `item_instances`
  - `player_inventory_slots`
- Added central package versioning for `Microsoft.Data.Sqlite` so the new persistence code restores and builds cleanly.
- Wired authoritative server startup through a SQLite persistence bundle in `ServerBootstrap`.
- Extended `RuntimeAuthoritativeSessionHost` and `DedicatedServerHost` so runtime-backed hosts:
  - bootstrap persistence automatically
  - reject duplicate active display names
  - restore persisted player state on join
  - checkpoint authoritative state every 5 seconds
- Refactored `WorldSessionRuntime` away from one shared interaction mode into per-session authoritative mode tracking.
- Persisted accepted player-facing authority state:
  - mode changes
  - stat changes
  - reconnect world position/facing/health
- Persisted active enemies:
  - restore on startup
  - save on spawn
  - delete on death
  - checkpoint position/facing/health
- Persisted player-owned placed cubes:
  - save on placement
  - delete on destruction/fracture
  - restore on startup with owner linkage and material/shape metadata
- Added repository round-trip tests plus multiplayer authority/regression coverage for:
  - duplicate display-name rejection
  - per-session interaction-mode isolation
  - reconnect restore of authoritative player state

## Current Limits

- Item/inventory tables and repository save/load methods now exist, but gameplay runtime ownership, pickup/drop/equip flows, and replication are not implemented yet.
- Persisted cube restore currently rehydrates solid cube cells with stored material metadata; non-cube shape runtime reconstruction is still future work even though `shape_kind` is stored.
- The new SQLite dependency currently surfaces an upstream vulnerability warning for `SQLitePCLRaw.lib.e_sqlite3` during restore/build and should be reviewed in a later dependency pass.

## Verification

- `dotnet build dream\dream.csproj --nologo /p:UseSharedCompilation=false`
- `dotnet test dream.Tests\dream.Tests.csproj --filter "SqlitePersistenceRepositoryTests|RuntimeAuthoritativeSessionHostTests" --nologo /p:UseSharedCompilation=false`
- `dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false`
