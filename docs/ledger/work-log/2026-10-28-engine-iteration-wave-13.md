# Engine Iteration Wave 13: WorldSessionRuntime Extraction, Rubble Memory, Protocol v4, Player State on Leave

Purpose: This log records wave 13, the last planned wave of the round-2 plan, and its fix loop.

Status: Integrated on `integrate/engine-iteration` and verified by automated tests and Release load runs. Not pushed. Not merged into `master`. The full suite was run at most 2 times in a row, per the user's rule.

## Process

1. Five implementers ran in parallel from `91449a7`:
   - inventory, combat and persistence-bridge extractions (ARCH-3 step B);
   - rubble subdivision memory;
   - protocol v4 bump and the churn-memory investigation.

   The coordinator relayed two seams between the extractors while they worked:
   - the inventory dictionary access now goes through a callback;
   - combat's host methods are kept as forwarders.
2. Integration had one conflict, in the `WorldSessionRuntime` constructor; both the combat resolver and the persistence bridge were kept. The bridge's inventory check was pointed at `PlayerInventoryService.IsLoaded`. The merged tip was `a729368`.
3. Verification:
   - The reviewer found no Must fix items. Comparing old and new code side by side, it confirmed that the order of operations is unchanged for combat, inventory, persistence and checkpoints. It raised 2 Should fix items and some nits.
   - The tester found no regressions. It found one exploitable bug that predates this wave: on leave, position and HP were lost.
4. Fix loop 1 ran two implementers: player state on leave plus the nits, and the docs and a flaky test. Their merges were clean and gave `d32ba6f`.

## Changes

| Task | Change |
| --- | --- |
| ARCH-3 step B, inventory | New `PlayerInventoryService` (`Dream.Simulation.Inventory`, internal). It holds slots, items, item ids, equip, starter seeding, per-player load and eviction, and failed-write retention. `WorldSessionRuntime.Inventory.cs` went from 478 lines to 97. |
| ARCH-3 step B, combat | New `AuthoritativeCombatResolver` with `IAuthoritativeCombatHost` (internal). It reads the map through `Func<Map>`, so it follows `RebuildMapForFloorSize`. Seeded rolls are pinned bit-identical by a golden test. Fracture capture now reads a single cell: about 370 µs and 656 KB down to about 1 µs and 104 B at 1,000 rubble cells. `.Combat.cs` went from 265 lines to 103. |
| ARCH-3 step B, persistence | New `RuntimePersistenceBridge` (plus `.Checkpoints`) with `IRuntimePersistenceHost`. It holds world restore, the join prefetch, the profile LRU, write-through, checkpoints and the final checkpoint (`PendingFinalCheckpoint`). The two runtime persistence files went from 1,319 lines to 243. `PersistFracturePieces` now reads a single cell. |
| Rubble memory | A rubble cell's subdivision state is now an "all children empty" flag instead of 1,000 dictionary overrides, and gives the same answers. Each fracture allocates 5.5 KB instead of 108 KB, and World10k managed memory is about 51 MB lower. |
| Protocol v4 | `CurrentProtocolVersion` is now 4. A v3 peer is refused in both directions with "uses protocol v3; this … uses v4" instead of crashing. |
| Churn memory | The growth of about 1.4 KB per departed player was the test rig keeping every snapshot it had verified. With that fixed, managed memory is flat from 1,250 to 2,000 players. No server leak was found. |
| Fix: player state on leave | **Bug (exploitable):** leaving and rejoining restored HP and rolled back position, and a shutdown lost everything since the last checkpoint. The fix: `PersistPlayerWorldState` saves position, facing and HP when a player detaches. A bounded departed-state cache covers a rejoin that arrives before that write lands. |
| Fix: tests, docs, nits | A map-rebuild regression test for combat and checkpoints. The extracted classes are now internal; the equip switch is exhaustive; renames. `docs/architecture.md` describes the extracted classes. The two flaky tests were fixed at their root cause (protocol mismatch hello timing; a tick-seeded hit breaking the "damaged" cube). |

## Measured Impact

| Measurement | Before | After |
| --- | --- | --- |
| World10k managed memory (Release load) | about 128 MB | 69–75 MB |
| Bytes allocated per fracture | 107.7 KB | 5.5 KB |
| Fracture-piece capture, 1,000 rubble cells | about 370 µs, 656 KB | about 1.1 µs, 104 B |
| `WorldSessionRuntime` inventory + combat + persistence partials | 2,062 lines | 443 lines (logic in 3 classes with their own unit tests) |
| Smoke8 tick p50/p99, per-client bandwidth | 0.55 / 1.3 ms, 21–25 KB/s | 0.55 / 1.14 ms, 21.2 KB/s |
| Players32 tick p50/p99 | 2.9 / 7.3 ms | 2.73 / 5.97 ms |

## Verification

- `dotnet build dream/dream.csproj`: 0 errors.
- `dotnet test dream.Tests/dream.Tests.csproj` on `d32ba6f`: 2,783 passed and 25 skipped (gated load and measurement tests), in 2 out of 2 runs, about 60 s each.
- New tester tests:
  - A loopback round trip through all three extracted classes against SQLite: join, equip, kill, fracture, leave, eviction, rejoin, then restart. It restores identical state, now including position.
  - Two persistent servers in lockstep for 600 ticks, past a checkpoint: identical checksums on every tick, and identical database dumps afterwards.
- 2-client perf harness: 144 FPS, 23 draws, 6 programs, no errors.
- Not verified: interactive play.

## Known Limits And Follow-ups

- **Command-received ordering.** `PlayerCommandHandler.cs:13` sets `LastCommandMessage` before it queues the frame (`:27`), so tests that treat that field as "the server has the command" can advance a tick one frame early. The fix is to swap the two lines.
- **Combat target order.** `AuthoritativeCombatResolver.cs:140-151` checks enemies before cubes and does not compare distances. An enemy behind a nearer cube can take the hit. This may be intentional; it needs a design decision.
- **Late checkpoint write.** A periodic checkpoint captured before a leave can land after the leave's world-state write and overwrite it with data up to 5 s old. The same race exists for the profile vitals. The fix is an `updated_utc` guard in `SqlitePersistenceCheckpointWriter`, whose comment saying "only the checkpoint writes player_world_state" is now stale.
- **GPU-6 on screen.** Still to be checked visually by placing or fracturing cubes.
- **Round-2 plan is complete.** Waves 7–13 delivered it. Further work needs a round-3 analysis.
- **Still waiting on user decisions:**
  - identity option D;
  - the BulletProfilerGuard patch;
  - the ImageSharp advisories;
  - `maxParallelThreads`;
  - the noclip/freeze gate.
