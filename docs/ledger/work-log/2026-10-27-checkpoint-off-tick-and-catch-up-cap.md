# Checkpoint Off The Tick And Catch-Up Cap

Purpose: Record the fixes for the server freeze found in `docs/ledger/analysis/2026-10-27-movement-jitter-and-push-freeze.md` section 1: a roughly 0.5 s stall on every 5-second persistence checkpoint, followed by a burst of uncapped catch-up ticks.

Status: Implemented and verified by automated tests. Not yet re-measured with a perf run or in manual play.

## Completed Changes

### Checkpoint

- **New types.** `PersistenceCheckpoint` and `PlayerCheckpointVitals` hold an in-memory copy of the checkpointed state. `IPersistenceCheckpointWriter` writes one.
- **SQLite writer.** `SqlitePersistenceCheckpointWriter` writes a checkpoint on one connection, in one transaction, with one prepared command per table. It:
  - upserts `player_world_state`, but only for existing profiles;
  - updates only the `interaction_mode`, `current_hp` and `max_hp` profile columns;
  - updates only existing `enemy_instances`, `placed_cube_instances` and `cube_fracture_pieces` rows.
- **Fallback writer.** `RepositoryPersistenceCheckpointWriter` follows the same rules through the repository interfaces. It is the default when no SQLite writer is supplied, for example with in-memory test repositories.
- **Runtime.**
  - `WorldSessionRuntime.CapturePersistenceCheckpoint()` builds the checkpoint with no database access. Placed cubes come from registry identity, using records that have a persistence owner. Fracture pieces come from one map scan instead of one scan per fractured cell.
  - `PersistCheckpoint()` remains as a synchronous capture-and-write.
  - The old per-cube checkpoint path is gone. It called `RepairCubeRecordFromPersistence`, which reloaded the whole placed-cube table and queried profiles once per cube.
  - The runtime takes an optional `checkpointWriter`. `RuntimeAuthoritativeSessionHost.CreateDefault` supplies the SQLite writer.
- **Server host.**
  - New `BackgroundCheckpointWriter`: one background worker that writes checkpoints in order. If a newer checkpoint arrives while one is still waiting, the older one is dropped. Failures are counted and logged as `server.checkpoint.error`. `Flush(timeout)` waits for pending writes.
  - `DedicatedServerHost` now captures on the checkpoint tick and enqueues the checkpoint instead of writing it. It flushes pending writes before disposing the authority.

### Catch-up cap

- `SimulationClock` caps ticks per `QueueElapsed` at `DefaultMaxTicksPerAdvance = 8`, drops the excess time, and counts it in `DroppedTicks`.
- `ServerTickRunner` takes the cap and exposes `DroppedTicks`. The server host and the desktop client mirror both use the default.

## Behavior Notes

- A cube record without a persistence owner name is no longer checkpointed. The placement and combat paths still repair identity and save immediately.
- The checkpoint no longer deletes a placed cube's fracture-piece rows on every pass. That deletion still happens when a placed cube is saved through `PersistPlacedCube`.
- After a stall longer than 8 ticks (125 ms), world time now falls behind wall-clock time instead of jumping forward.

## Verification

- New `dream.Tests/Server/Persistence/CheckpointPersistenceTests.cs` (7 tests, against the real `config/player-profiles.init.sql` schema):
  - the SQLite writer updates every table in one checkpoint;
  - a late checkpoint does not resurrect a deleted cube, enemy or fracture piece;
  - a late checkpoint keeps level and experience saved after the capture;
  - a failing row rolls back the whole checkpoint;
  - the background writer returns immediately while a write blocks, and keeps only the latest pending checkpoint;
  - a failed background write is counted and later writes still run;
  - the `DedicatedServerHost` checkpoint tick completes while the database write is blocked.
- New clock tests: a 0.6 s stall runs 8 ticks and drops 30; the cap works within and across advances; `ServerTickRunner` keeps tick numbers contiguous under the cap.
- Full suite: `dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false`, 619 passed, 0 failed.
- Build: `dotnet build dream\dream.csproj --nologo /p:UseSharedCompilation=false` succeeded with the existing package and analyzer warnings.
- Not done: a new perf run to confirm the 500 ms `advanceMs` spikes are gone.

## Follow-Up

- Section 2 of the analysis (hard snap of the local player on every snapshot) is still open and is the remaining cause of high-speed jitter.
