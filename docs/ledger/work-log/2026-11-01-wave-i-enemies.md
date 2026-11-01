# Wave I: Enemies On The Same System

Purpose: record wave I of `docs/ledger/plans/2026-10-31-characters-items-enemies-roadmap.md`. Plan: `docs/ledger/plans/2026-11-01-wave-i-plan.md`.

Status: integrated on `integrate/engine-iteration`, two fix loops done. Not pushed. Not merged into `master`. Interactive play has not been checked. The tester's Release measurements and adversarial tests are below.

## Process

1. **C0 (`de4c374`).** Migration 0009, formulas, protocol v11, loot content, enemy stat core.
2. **Merges, no conflicts.** I1 (`df2bbd7`), I2 (`7275d21`), I3 (`fc0bc71`). Focused tests passed after each.
3. **Glue.** See below.

## Production bug found

**A push could be undone by an older inventory.** An action result waits for the next snapshot and carries the inventory of its own tick. A push goes out at once. At the default snapshot interval of 2 ticks, an equip on a tick with no snapshot and a kill on the next tick delivered the push first and the older result second. The client keeps what it applies last, so the looted item vanished from the client until its next inventory change. The server was right all along, and nothing was lost.

`ServePendingInventoryPushes` now leaves a push pending while an `InventoryChangedResult` is queued, and builds the snapshot when it sends. `EnemyLootEndToEndTests.AnInventoryResultStillWaitingForItsSnapshot_DoesNotUndoTheLootOfTheNextTick` fails without the change (1 rolled item shown, 2 held) and passes with it. In that rare case a push now arrives after the snapshot of its tick. The scan runs only on a tick with a push pending.

**Fix loop 1: the hold is per session.** The first version held every session's push while any session had an inventory result queued, with no upper limit on the wait, so one player equipping on the blocking tick delayed everybody's loot ("at most one interval" was only true for one session). `ServePendingInventoryPushes` now collects the sessions that have a queued `InventoryChangedResult` and drains pushes only for the others; a held session's loot stays pending in the runtime and goes out on the first post-snapshot tick with no new result of that session (a session's own result on the first tick after a snapshot re-holds it; there is no cap, because releasing early would break the order guarantee). The order guarantee is per connection, so it is unchanged. This is a decision against plan I-D18, which only said pushes ride the pending-reply route and said nothing about a hold. `WaveIAdversarialLoopbackTests.AnotherPlayersEquipOnTheExactTickOfTheKill_DoesNotDelayAKillersPushByEvenOneStep` fails with the global hold (push at step 31 against 30) and passes with the per-session one; the older `AnotherPlayersInventoryActionOnEveryTick_DoesNotHoldBackAKillersPush` only caught a delay over 4 steps.

## Glue

- `ClientDeathTracker.RespawnDelayTicks` is `PlayerRespawnRules.DelayTicks`.
- `HeadlessPlayScene` is a real `PlayScene` with no GL, fed by a real `RemoteClientSession` over TCP. So join, snapshot, action result and push all run through `ApplyReplicatedMessage` in production order, behind the session's real world-transfer hold. `Update` does not run, so prediction and the reconciler's respawn snap are not run through it. (An earlier line here said I3's unit tests cover the snap; they did not cover a dead period of idle commands followed by the teleport. Fix loop 1 added `DeadInputPolicyTests` and `ClientPredictionReconcilerTests.Reconcile_DeadPeriodOfIdleCommands_ThenTheRespawnTeleport_SnapsOnceAndAcceptsALaterAck`; a real `Update` is still not run.)
- `EnemyLootEndToEndTests`: join, kill a Dire at night, a rolled item arrives by push, equip it, take a reduced bite, die, wait, respawn on the exact tick at the spawn with the item, restart, rejoin, `--worlds copy`, join the copy. It uses a sure-drop table through the `LootContent` seam. The real-process flow uses the shipped table.
- `KillBurstMeasurementTests` and `WorstCaseInventoryWireSizeTests` are written.
- `WaveBTesterWorldsLoopbackTests`: see Flakes.
- `docs/configuration.md` and `docs/architecture.md` are updated.

## Reviews asked for at glue

- **`ContentCatalog.Shipped` on restore, and `LootContent`.** Neither breaks a real server. `ServerBootstrap` reads `Shipped` once at start and stops with exit 8 if it fails, before any runtime exists. `Shipped` is one lazy value, so the bridge, `EnemyContent` and `GrantLoot` read the same object, with no file read on a tick. `EnemyContent` and `LootContent` are `internal` and no production code writes them. A test that sets `EnemyContent` still restores from `Shipped`, which only matters to tests.
- **Client without content.** `WorldSessionRuntime.EnemyContent` reads `Shipped` in its initializer, but the client has no `WorldSessionRuntime` (`ClientWorldSession` only), so a remote connect does not reach it.
- **Dead gate.** Client-sent build and combat intents are refused by `PlayerActionHandler` before the queue, so the hostile client gets "cannot be sent by clients". The gate in `ProcessAuthoritativeAction` runs for synthesized and server-queued use. A held button on a dead seat never gets there: `UpdateAcceptedCommand` and `RemoteSessionPlayerState.Simulate` swap in the starved frame before `ConsumePrimaryUsePressed`. A seat killed earlier in the same tick is the case the gate catches.
- **Push and resync.** A resync reply is a `ServerSnapshotMessage`. In one tick it is queued ahead of the push, and a push from an earlier tick of the same advance goes ahead of it. Either order is safe: a snapshot carries no inventory, and the feed is cleared only by a join. Pinned in both orders over TCP.
- **Push during a world transfer (plan risk 10).** The session holds every message behind the carrier and releases them in order after the final chunk, so the join applies first, and its reset of the feed comes before the notice. Pinned. A push counts toward `MaxHeldMessages` (4,096) like any other message.
- **Tint.** The only draw order is enemies, then local player, then remote players, and `ResetTint` runs at the end of the enemy draw, before anything else uses that program. Dire then wolf: the wolf's `ApplyTint` writes zero to the base program and the model, and `ResetTint` is a no-op, which is right because the MASK variant takes its value from the drawing model on every `BindProgram`. Wolf then Dire: `ResetTint` clears all three. Players drawn before enemies would see zero too, since the previous frame reset. `Wave9AlphaMaskUniformCoverageTests` passes. Only one model is tinted today: with a second tinted model, `_tintModel` tracks the last one, so an earlier model would keep its offset. How it looks has not been seen on screen.

## Flakes

**`TwoHostsOnTheSameDatabaseFile_AWriteLockHeldPastBusyTimeout_LosesNoWrites`: load, and no write was lost.** With 16 CPU spinners it passed 8 of 8. With 64 it failed 2 of 6, both on "the held write lock should have forced at least one retry", with `lock retries 0; failed writes 0`, and with 96 + 78 and 99 + 75 writes done. The test held the lock for 6.5 s and needed a write to start within the first 1.5 s, so that its 5 s `busy_timeout` ran out inside the hold. Under load the first write started later and simply waited. That assertion stood in front of the rest of the test. The lock now stays held until a write has been refused, at most 30 s more. 2 of 2 passed at 64 spinners before that loop hit its time limit.

Reading the code, a lost write needs more than this. `RunGroup` requeues a write whose transaction could not start, a failed commit replays each write, and writes happen inside an immediate transaction, so a busy error in the middle of a group is not expected. `FailedWrites` was 0 in every run.

**`ChunkEncode_OfRestingCubes_AllocatesNothingPerCube`: not reproduced.** 0 B in 8 runs alone, 6 with 64 spinners, 3 with all 621 `Dream.Tests.Multiplayer` tests at 32 spinners, and 2 full suites. No log of the C0 failure survives, so its size is unknown. No cause is established. If it recurs, the assertion prints the bytes.

## Worst-case wire size

`WorstCaseInventoryWireSizeTests`, 16 rare rolled items with 4 worn, real frames, Debug:

| Frame | Bytes | Gate |
| --- | --- | --- |
| Join | 9,328 | 16,000 |
| Push | 8,402 | 16,000 |
| Push with 8 notices | 8,834 | 16,000 |

The plan's estimate was 14.7 KB. No trimming needed (I-D26).

## Checks on real processes

Run on copies of the 4 `legacy-e` databases, Debug build, `dream --server` on loopback, from the path wave H used. Clients were a console driver over the real `RemoteClientSession` with `DREAM_IDENTITY_FILE`, using the real `ClientScenePresenter`. The hostile client is hand-written Python on raw sockets. 28 checks on each database, all passed.

- Start migrates v6 to v9 in one step with one `v0006-to-v0009` backup, and the formulas are seeded.
- A stored character at 0 HP joins dead at its stored place and is back at the spawn with full health 321 ticks after the join snapshot, about 4.9 s.
- Two clients: one is killed by a wolf. The other stops drawing it, then draws it at the spawn. Over the next 40 samples it moved at most 0.345 m, with no slide from where it fell.
- A real kill paid real loot from the shipped table on every database (for example Iron Blade, Uncommon), and the rolled row is owned by the character key.
- A backpack filled with `--items grant`: the next drop arrives as a lost notice in a push with no inventory, and no row is added.
- The hostile client, joined dead, sent 16 build and combat intents and held forward, jump and attack for 2 s. All were refused and answered, the connection stayed, no cube was placed, no other player's row changed, and it respawned at the spawn. An inventory move while dead was accepted.
- No unhandled exception, and `integrity_check` is ok.

No Firewall prompt window was detected, by window title, and none was clicked.

## Limits closed

- **Wave G, "hopping between worlds works as a revive".** Closed as harmless (I-D10). A character dead in world A still arrives full in world B, because health is per world, but dying now costs at most 5 s and nothing else, so hopping wins nothing. Back in A it waits its 5 s. `PlayerRespawnTests` pins it on two worlds in one database.
- **Wave H, "inventory actions while dead were not run over real TCP".** Closed. `AnEquipWhileDead_WorksOverRealTcp_StaysDead_AndTheRespawnUsesTheNewMaximum` runs an equip through a real session: the health stays 0, the maximum rises, the respawn fills it. The real-process flow runs an inventory move while dead on each database.

## Found, not changed

- **N4: a dead remote player's body stays solid.** Other players and enemies still collide with it until it respawns. Left alone on purpose (limit).
- **N1: a template's `attackIntervalSeconds` is lost on restart.** Kept. A restore derives damage and interval from the row's stats (plan I-D4: a balance edit reaches new spawns only), and `EnemyFactoryTests.AnExplicitTemplateInterval_ReplacesTheFormula_OnSpawnOnly` pins that. No shipped template pins an interval.

- **A wolf that chases stops out of its own bite range.** Fixed afterwards: see "Wolf bite and spawn protection" below. `WolfDefinition.PreferredStandOffDistance` is 2.0 m and `attackRange` is 1.35 m, and `EnemyActor` bites only within the range. So a wolf stops 2 m from a player who stands still and never bites until the player walks in. The Dire Wolf has the same range. These numbers predate wave I. It made the plan's spawn-camping risk smaller and made the driver's "die" step wait forever until it walked to melee range. Changing it changes combat, so the tick goldens would move. It is the user's call.

## Measured

Tester, Release, `DREAM_LOAD_TEST=1`, tip `f635442`-based (tests only; no production code changed by the tester). C0 is `de4c374`. C0 and tip binaries were built into separate output folders and alternated.

| Measurement | Gate | Result |
| --- | --- | --- |
| Tick allocation, with a dead session and 4 rolled items | 0 B | pass: `RespawnAllocationTests` (2), `WorldSessionRuntimeEquipmentAllocationTests`, `WorldSessionRuntimeTickAllocationTests` |
| Hit with Armor > 0 | 0 B | pass: `AHitWithArmor40_AllocatesNothing` |
| Tick goldens, checksum goldens, wolf interval bits | unchanged | pass (`TickEquivalence` 2, `SimulationStateChecksumTests` golden 2, `WolfIntervalBitsTest` 5) |
| `NoLight_200Ticks` | 58,307 B | 58,307 B |
| Actor bytes with a Dire | at most 24 B per Full record, none on a delta | 20 B per Full record by the pinned wire text (`,3,"enemy.dire-wolf"`); a delta carries nothing for an unchanged presentation (asserted) |
| `server.join-accepted`, starter kit | 4,099 B | 4,048 B |
| World10k join 0 (bytes per join) | identical to C0 | 233,182 B at C0 and at tip |
| Join with 9 items, fully rolled | 9,400 B | not measured: no test in the tree builds that case. Worst case (16 rolled, 4 worn) join is 9,351 B, push 8,423 B, push with 8 notices 8,857 B, gate 16,000 B |
| Lost-only push | 256 B | `ALostOnlyPush_HasNoInventory_AndStaysSmall` passes (asserts at most 256 B; the value is not printed) |
| World10k full checkpoint | C0 x 1.05 | **harness artifact, not a checkpoint cost** (see "World10k checkpoint gate settled" below). Full-every-capture write mean, median of 12 alternating fresh-process runs: C0 34.97 ms, tip 38.0 ms (+8.7%), but the same C0 source with 400 empty test facts added to the test assembly also reads 38.0 ms |
| Join-window total | C0 x 1.10 | C0 15.0 ms, tip 13.5 ms per round |
| Integrity check, World10k | 15 ms | 7.20 ms (C0 7.15) |
| Migration 0009 | 50 ms | 0008 and 0009 together on 100,003 items: 108-141 ms (v7 to v9, two runs). The two are not split, so the 0009 figure alone is not measured |
| Kill burst: queue drained after the last kill | 100 ms | 0.3 ms (4 runs) |
| Kill burst: failed writes, items lost | 0, 0 | 0, 0 (100 pushes, 100 items, peak pending 2) |
| Kill burst: tick p99 | 15.6 ms, and C0 fight-only p99 x 1.25 | 0.20-0.22 ms in 6 runs; single worst tick 6.9-7.3 ms. C0 fight-only p99 0.16-0.19 ms (4 runs) so the limit is about 0.22 ms: passes with no margin. Tip fight-only 0.18-0.23 ms. Fight-only is `KillBurstFightOnlyBaselineTests`, run on both commits |
| 1,000-write drain | 32.1 ms | 25.9-26.1 ms (C0 26.8) |

Load harness (`ServerLoadTests`): Smoke8, Light8 and Players32 pass with tick-loop p99 0.98, 0.35 and 6.27 ms. World10k tick-loop p99 416 ms, 347 dropped ticks (the old known cannot-keep-real-time scenario; no baseline taken at C0). Soak (30 min) and the stored-world soaks were not run.

`KillBurstMeasurementTests` runs 100 real kills at 5 a second, in real time, across 10 characters. Seating them with `AttachRemoteSession` alone gave 10 failed writes, one each, because no profile row existed. That was the test's mistake, and it now seats them as a join does.

## World10k checkpoint gate settled

Tester, Release, `DREAM_LOAD_TEST=1`, `IncrementalCheckpointMeasurementTests.World10k_FullVersusIncrementalCheckpoints`, one fresh `dotnet test <dll>` process per sample, builds in detached worktrees under `artifacts/gate` (removed afterwards). Full-every-capture write mean per run (6 writes of 14,001 rows each), 12 rounds with the order rotated every round:

| Build | Median | Min - max |
| --- | --- | --- |
| C0 `de4c374` (prebuilt `verify-bin-c0`) | 34.97 ms | 34.38 - 37.40 |
| C0 second build (control) | 35.34 ms | 34.32 - 38.43 (noisier: 3 runs at 37.5 - 38.4) |
| I2 merge `7275d21` (fc0bc71's parent) | 34.94 ms | 34.47 - 36.62 |
| I3 merge `fc0bc71` | 37.75 ms | 37.04 - 38.25 |
| Tip `66399a6`, build A | 38.00 ms | 37.73 - 38.26 |
| Tip `66399a6`, build B (control) | 37.98 ms | 37.13 - 38.80 |

- **Step is real in this test binary and sits at the I3 merge.** `7275d21` to `fc0bc71` is +8%; `fc0bc71` to tip is flat. Two tip builds agree to 0.02 ms, so build-to-build code layout does not explain it. The two C0 builds agree in median (0.4 ms apart, one build noisier). Tip max write 80 - 85 ms against about 60 ms at C0 / I2.
- **Same work is written.** Rows per write are 14,001 at C0, I2, I3 and tip; the seeder, scenario and checkpoint code (`dream/Server/Persistence`, `dream/Simulation/Persistence`, `dream/config`) are unchanged from C0 to tip except `enemies.json` (+14 lines). Migrations 0008 and 0009 and the item_rolls/enemy columns are in C0 already. Bytes and statement counts were not instrumented.
- **Cause: the size of the test assembly.** The I2 source was rebuilt with one extra file of 400 trivial `[Fact]` methods (20 classes), nothing else, no production change, and the same filter. Six alternating runs: I2 34.5 - 35.4 ms (median 34.8), I2 + 400 empty facts 37.3 - 38.8 (median 38.2), tip 37.5 - 39.1 (median 38.1). The empty facts reproduce the whole step. I3 added about 60 tests. The mechanism inside the process is not pinned: with `DOTNET_TieredCompilation=0` I2 reads 32.0 and tip 34.9 (gap stays); with `DOTNET_TieredPGO=0` 34.3 against 34.9 (gap mostly gone, noisy); with `DOTNET_GCgen0size=0x10000000` 38.2 against 38.7 (gap gone, both slower); `DOTNET_gcConcurrent=0` 35.2 against 38.1 (gap stays); 5 alternating runs each. That points at heap and GC or PGO state left by xunit discovery of a larger assembly, not at the write path, but this was not confirmed with a profile.
- **Verdict.** No checkpoint code or data changed, the same rows are written, and an empty-test padding of the C0-era source gives the same number, so this is not a wave I checkpoint regression. The 1.05 gate cannot be judged by comparing binaries with different test counts; to measure it cleanly, run the C0 and tip comparisons from test assemblies with the same test set. Production code was not changed.

### Kill burst and allocation, rerun at tip (Release, 8 alternating rounds)

- Fight-only p99 (`Kills100_WithNoLoot_TickTimes`): C0 0.17 - 0.19 ms (median 0.18), tip 0.19 - 0.22 (median 0.19). `KillBurstFightOnlyBaselineTests` was copied into a C0 worktree to build it there; `KillBurstMeasurementTests` does not exist at C0.
- Kill burst p99 (`Kills100_WithASureDrop...`) at tip: 0.20, 0.20, 0.21, 0.20, 0.22, 0.23, 0.20, 0.20 ms (median 0.20); single worst tick 7.0 - 7.3 ms. The limit is 1.25 x C0 fight-only p99, 0.21 - 0.24 ms depending on the C0 run (0.225 at the median). Passes in most runs with no margin; the 0.23 run (and the 0.22) sit at the limit. Loot notices from loop 2 do not show: the fight-only run without loot is within 0.01 ms of the loot run, and it also drifts +0.01 ms against C0, which may be the same assembly-size effect (not tested).
- Tick allocation gate: `RespawnAllocationTests`, `WorldSessionRuntimeEquipmentAllocationTests`, `WorldSessionRuntimeTickAllocationTests`, `AHitWithArmor40_AllocatesNothing` filter: tip 11 of 11 pass, C0 8 of 8 (the respawn tests do not exist at C0), 3 rounds each, 0 failures.


## Tester findings

- **The load bots were rejected on every join since wave G.** `BotClient` sent no account token, so `Players32` failed with "needs a player identity" and every scenario built on it measured nothing. Fixed in the test (`BotClient` sends `TestAccountTokens.For`). Not a wave I defect.
- **Adversarial tests** (`WaveIAdversarialTests`, `WaveIAdversarialLockstepTests`, `WaveIAdversarialLoopbackTests`, plus `OldClient...` cases for protocol v8 to v10): kill and leave in one tick, death and leave in one tick, a stranger taking a dead player's primary seat, two kills filling a pack in one tick, equip and unequip around death and respawn, rename and release with loot or a respawn pending, restart with two dead seats, extreme armor modifiers (overflow to infinity, NaN, negatives), the Dire at the dawn and nightfall ticks, two lockstep servers over 1,500 ticks of kills, deaths and respawns, a death during the join's world transfer over TCP, and a neighbour's inventory actions against a push. All pass; no production defect found by them.
- **Flakes.** `TwoHostsOnTheSameDatabaseFile_AWriteLockHeldPastBusyTimeout_LosesNoWrites`: 0 failures in 13 runs (7 under 64 CPU spinners, 6 unloaded). `ChunkEncode_OfRestingCubes_AllocatesNothingPerCube`: 0 failures in 50 runs (20 under 64 spinners, 30 unloaded). `APushAndAWorldResyncOnOneConnection_LoseNothing_InEitherOrder(resyncFirst: False)` failed once in a clean full run (resync index -1, so the resync message was not in the applied list when the test counted a completed transfer) and passed 15 of 15 isolated, 10 of 10 under 48 spinners and 3 of 3 in a class run. Likely a race in the test's wait condition; not fixed.
- **Full suite.** Run 1 (Debug, clean): 4,699 passed, 1 failed (the test above), 46 skipped. Run 2 was invalid: 48 of my CPU spinners were still running, and 7 failed (the same test and six timing tests); those six passed 3 of 3 afterwards on a quiet machine.

## Fix loop 1

- **S1** per-session push hold (above). **S2** `ResolveRespawnPosition` checks every cell the body overlaps (`Map.TryGetCubeAtCell` is the general lookup `EnemyMovementCollision.CanOccupy` uses too). **S3** `EnemyFactory.Restore` clamps a stored level to `ContentCatalog.MaxEnemyLevel` (1000); the row is not rewritten by the restore, but a later checkpoint writes the clamped value. **S4** `DeadInputPolicy`.
- **Flake `APushAndAWorldResyncOnOneConnection_LoseNothing_InEitherOrder(resyncFirst: False)`: a race in the test.** The wait used `Session.WorldTransfersCompleted`, which the receive thread bumps right after it queues the world. A pump that drained just before the queue and checked just after the bump saw the counter ahead of `Applied` (resync index -1). The wait now looks for the resync snapshot in `Applied`.
- **N2** a lone NightExtra at dawn is removed and the next tick spawns the day's Primary. **N3** the startup probe always evaluates the interval formula (a restore reads it even when a template pins its interval). **N5** `RemoteEnemyStateApplier.MaxDisplayLevel` is `ClientScenePresenter.MaxDisplayLevel`; the 'built once' test now checks the one push's snapshot holds both items. S5 (wolf stand-off) untouched.

## Not done

- **Perf harness and screenshots.** Tried once. The harness starts the server from a new exe path and binds all interfaces, so Windows Firewall showed its prompt. I did not click it, stopped my three processes by PID, and left the prompt on screen for the user to dismiss. Nobody has seen the Dire Wolf, labels, death overlay or loot feed. A world with a Dire next to the spawn at night was prepared in `artifacts/perf-release` (not committed).
- **Reconciler's respawn snap with a real `Update`.** Not run by `HeadlessPlayScene`. The reconciler's behaviour across a dead period is covered by a unit test and the dead-input decision by `DeadInputPolicyTests`, but no test runs `PlayScene.Update` itself.

## Fix loop 2

- **Flake `AnotherPlayersEquipOnTheExactTickOfTheKill_DoesNotDelayAKillersPushByEvenOneStep` (expected 31, actual 30): a race in the test, not a production ordering issue.** `StepsUntilThePushAsync` ran `FlushAsync` and then `Pump()`. `FlushAsync` only says the server wrote its bytes to the socket; the client's reader may not have queued them yet, and `Pump()` drains only what has arrived. So under load the push of the kill tick was recorded a step late. When the control arm was late (31), the griefing arm, which equips on `step == control`, equipped after the real push tick, so that run did not test the exact tick and then correctly reported 30. Production sends the push before the tick's snapshot on the same connection (the pending-reply route), so the loop now pumps both clients until the snapshot of the current server tick is applied; the step's push is then applied if it was sent. No production code changed for this.
- **New proof.** With a temporary global hold in `ServePendingInventoryPushes` (return when any inventory result is queued), the fixed test failed 6 of 6 runs, every time with `push after 30 steps alone, 31 steps with a neighbour equipping on step 30` (expected 30, actual 31). The temporary code was removed and the tree shows no production change from it. The fixed test then passed 20 of 20 class runs alone and 10 of 10 class runs with 16 CPU spinners (spinners stopped by PID, none left). The sibling `AnotherPlayersInventoryActionOnEveryTick_DoesNotHoldBackAKillersPush` shared the loop and so the weakness (its 4-step tolerance hid it); the same change fixes it.
- **Same race, other direction, in `APushAndAWorldResyncOnOneConnection_LoseNothing_InEitherOrder`.** The loop-1 fix still checked `WorldTransfersCompleted` with no wait after the pump; the counter is bumped after the world is queued. The counter check is now inside the `PumpUntilAsync` predicate. (The earlier note that this flake was "not fixed" is superseded.)
- **Hold comment corrected.** A session is held until the first post-snapshot tick with no new result of that session, not for at most the ticks until the next snapshot. A session that produces an inventory result on every tick after each snapshot can hold only its own notices this way; its items still arrive through its inventory results. `LootGrantTests.TwoKillers_OneHeld_DrainsExactlyTheOtherPush_AndKeepsTheHeldOneForTheNextDrain` pins the selective removal: it fails if the removal is replaced by clearing `_pendingLoot`; reverted.
- **Notice cap.** A notice past `MaxNotices` is logged as `server.loot.notice-dropped`; a `lost` notice replaces the last ordinary one (the client shows the tail; changed in fix loop 3) instead of being dropped. Only the full path allocates (the log string).
- **Nits.** `EnemyFactory.Restore` uses `Math.Clamp(level, 1, MaxEnemyLevel)` and its comment says the first checkpoint with a diff writes the clamped value. Two stale comments repaired (`WorldSessionRuntime.Enemies.cs`, `WorldSessionRuntime.Respawn.cs`). The `EnemyFactoryTests` XP assertion compared a call with itself; it now asserts the reward at the clamped level is positive and lower than the reward at the stored 5000 (it does not run `AuthoritativeCombatResolver`).
- **Unverified items:** settled in fix loop 3 (below).
- **Full suite after merge 1433ec8.** One run (Debug, quiet machine, no stray processes): 4,719 passed, 0 failed, 46 skipped, 4,765 total, 2 m 40 s. `dream/dream.csproj` built with 0 errors and 16 warnings.

## Review of fix loop 2, not fixed

No Must fix. The two fix loops were used up, so the reviewer's items were left for the user to decide. They were then fixed in fix loop 3 (the `lost` notice, the nits) and, at the user's request, the wolf stand-off (S5, below).

- Fixed in fix loop 3: the `lost` notice placement, the missing replacement test and the nits below (log line, rate limit, push-wait comment, XP test).
- **Wolf stand-off (S5).** Left for the user here, then fixed at the user's request: see "Wolf bite and spawn protection" below.

## Wolf bite and spawn protection

Done after fix loop 2, at the user's request (S5). It changes gameplay, so some tick results were re-pinned.

- **The bite.** `WolfDefinition.PreferredStandOffDistance` is now 1.20 m (was 2.00 m), between `PlayerSeparationDistance` (0.95 m) and the 1.35 m bite range. `EnemyActor` clamps the stand-off to at most 90% of its attack range instead of raising it to the attack range, so a stand-off cannot sit at or past the range. A chasing wolf stops on the first tick it is within 1.35 m (a step is about 0.044 m, so about 1.3 m in practice) and bites; the stand-off only limits the last step. Dire Wolves share this. A wolf at its stop point does not move, jitter or push the player.
- **Spawn protection.** `PlayerRespawnRules.SpawnProtectionTicks` is 192 (3 s). A respawn at tick T sets `ConnectedPlayer.ProtectedThroughTick = T + 192`; the enemy step of ticks T+1 to T+192 is given only the unprotected players (`FillEnemyTargets`, a reused list), so enemies neither target nor bite a protected player, and the 193rd tick is the first that can. A combat use in Combat mode sets it to 0. Join and first spawn never set it, a respawn resets it, and it is cleared on detach. It is counted in ticks, not persisted, and has no protocol field or client visual. A protected player is still pushed out of overlapping enemies (separation uses the full list).
- **Tests.** `WolfBiteAndSpawnProtectionTests` (10 cases). Without the stand-off change the wolf, Dire Wolf, lockstep, stand-off and distance-4 protection cases fail; with the protection assignment disabled the exact-192 (both distances), attack-ends and second-respawn cases fail.
- **Re-pinned.** `WorldSessionRuntimeTickEquivalenceTests.GoldenCheckpoints` 2-10 (checkpoint 1 is unchanged): the wolf walks to about 1.3 m instead of 2.0 m and bites. The 58,307 B pins (`HeldLightSnapshotBytesTests`, `WaveBTesterWorldsLoopbackTests`) are now 58,827 B (+520 B): the wolf stops 0.7 m closer, goes to Attacking and bites player A (health 88, 76, 64 over the 200 ticks). `SimulationStateChecksumTests` did not move.
- **Tests that relied on wolves not biting.** `WaveIAdversarialTests` (a new character at the spawn expected full health after 340 ticks; the wolf is now parked far away), `EnemyActorTests.Update_Stops_At_StandOff_Distance_Before_Reaching_Player` (uses attack range 3 so a 2 m stand-off is still valid), and a note in `EnemyLootEndToEndTests` (its penning is what now keeps the day wolf off the player). Scripts that attack cubes or walk now park the day wolf far away, because it stood in their ray or blocked the walk: `IncrementalCheckpointTests` (`ParkWolves`), `CheckpointLeaveRaceLoopbackTests`, `SoakServer` and `PersistentScriptedServer`. The `DeadPlayerActionTests` comment blamed terrain for the wolf not reaching the player; the stand-off was the cause.

## Fix loop 3

- **`lost` notice replaces the last ordinary notice.** `AddLootNotice` scans from `Count - 1` down, so the replacement lands in the 4 lines `LootFeedbackSystem` keeps (`MaxLines` 4, tail). Several lost notices fill the end of the push one after another (3 lost: indices 5 to 7), all inside the tail. With 4 or more lost already at the end and 8 notices, the fifth lost one replaces index 3 and falls out of the tail; that is the limit of the approach. All-lost stays at 8 and replaces nothing. The earlier work-log sentence saying "first" is corrected above.
- **Log line.** `server.loot.notice-dropped` now names the displaced notice in `replaced=` (`none` when the notice was only dropped). It goes through `WriteEvery` with a constant key, a 200 ms interval and the static-lambda overload, so nothing is built when diagnostics are off.
- **Tests.** `LootGrantTests.ALostNotice_PastTheCap_ReplacesTheLastOrdinaryOne_StaysInTheClientsTail_AndIsLogged` (asserts the captured line) and `SeveralLostNotices_PastTheCap_FillTheTailFromTheEnd_AndAllLostStaysAtTheCap`. Both fail with the first-notice scan (checked by reverting the scan).
- **Push wait in `WaveIAdversarialLoopbackTests`.** The wait target is `CurrentServerTick + 1 - SnapshotIntervalTicks` like the neighbouring waits, and the comment says it holds for an unheld session.
- **Level clamp.** `EnemyFactoryTests` has a theory for stored levels 0 and -5 (actor level 1), and a theory that kills a restored row of level 5000, 0 and -5 through `ProcessAuthoritativeAction` and `AuthoritativeCombatResolver` and compares the killer's level and experience with a kill of a row at the clamped level (1000, 1, 1); it also asserts the two ends differ, so the comparison can fail.
- **Respawn into rubble: a bug, fixed.** A fractured cube leaves no `Cube` in its cell, so `ResolveRespawnPosition` saw a free cell and put the player at the spawn inside the rubble pieces (tests failed with `respawned at (5, 1.5, 5)` before the fix). Whether the physics would then have pushed the player out was not measured. `ResolveRespawnPosition` now also lifts while a piece's box overlaps the body box (one cell wide, two cells high from the body cell); pieces are read with `Map.CaptureFracturePiecesInto`, so a piece that moved out of its origin cell still counts. Tests: `PlayerRespawnTests.ResolveRespawnPosition_LiftsPastRubbleThePiecesOfAFracturedCube_WhichLeftNoCubeInItsCell` (3 cases) and `ResolveRespawnPosition_IgnoresRubbleFarFromTheSpawn`.
- **Stale queued result after a rejoin: real, fixed.** With a snapshot interval of 40 ticks, an inventory result queued before a leave reached the rejoined connection of the same session id after `JoinAccepted` (join at message 1, stale result at 3). In that run the client's inventory still equalled the server's, because nothing can change a left character's inventory in between, so no wrong inventory was shown; but the old action sequence went through `ObserveActionResult` and the applier of the new session. `ReleaseConnection` now purges the session's queued results (`PurgePendingActionResults`), and `DrainPendingActionResultsInto` takes the tick lock so a purge never reorders a drain. Test: `WaveIAdversarialLoopbackTests.AnInventoryResultQueuedBeforeALeave_NeverReachesTheRejoinedSession_AndItsInventoryIsRight` (failed before the purge).
- **Not touched.** Tick goldens, checksum goldens and byte pins (no simulation path changed; respawn position changes only when rubble is at the spawn). The wolf stand-off (S5) was a separate change: see "Wolf bite and spawn protection" above.

## Tester round 4: tip `69e68ff` (wolf bite, spawn protection, notice placement, rubble lift, purge on leave)

Tester, 2026-11-01. No production code changed (the comparison builds carry compile-only edits in scratch worktrees, see below). Tests added and committed: `WolfBiteAdversarialTests` (13 methods, 14 cases) and `WolfBiteLoopbackTests` (1).

### Release measurements (`DREAM_LOAD_TEST=1`)

Base `5e66291` against tip `69e68ff`, **same test set**: the tip's `dream.Tests` copied into a detached worktree at `5e66291`, with two compile-only edits in that scratch copy (`PlayerRespawnRules.SpawnProtectionTicks = 192` added, `AddLootNotice` made `internal`), so both test assemblies hold the same tests. One fresh `dotnet test <dll>` process per sample (Players32, Smoke8, Light8, both kill-burst tests), 4 rounds, order alternated every round, nothing else running.

| Measurement | Gate | Base `5e66291` (4 runs) | Tip `69e68ff` (4 runs) | Result |
| --- | --- | --- | --- | --- |
| Tick allocation (`RespawnAllocationTests`, `WorldSessionRuntimeEquipmentAllocationTests`, `WorldSessionRuntimeTickAllocationTests`, `AHitWithArmor40_AllocatesNothing`, `WolfBiteAndSpawnProtectionTests` incl. the protected-tick allocation case; tick goldens, checksum goldens, wolf interval bits) | 0 B | n/a | 66 of 66 passed, 3 rounds | pass |
| Kill burst p99, with a sure drop | 1.25 x C0 fight-only p99 (about 0.21 - 0.24 ms) | 0.23, 0.26, 0.25, 0.21 ms | 0.26, 0.37, 0.21, 0.21 ms | test passed all 8 runs; tip median 0.235 vs 0.24; one tip run at 0.37 ms |
| Kill burst, worst single tick | none | 4.74, 4.75, 4.73, 4.77 ms | 4.99, 4.87, 4.91, 4.89 ms | tip about +0.15 ms (+3%), stable |
| Fight-only p99 | n/a | 0.21, 0.20, 0.21, 0.18 ms | 0.19, 0.20, 0.18, 0.18 ms | equal |
| Kill burst queue drain / failed writes | 100 ms / 0 | 0.2 - 0.4 ms / 0 | 0.3 ms / 0 | pass |
| Smoke8 tick-loop p99 | test guard | 1.60, 1.19, 1.03, 1.52 ms (median 1.36) | 1.94, 1.08, 1.68, 1.60 ms (median 1.64) | pass, within noise |
| Light8 tick-loop p99 | test guard | 0.37, 0.37, 0.39, 0.60 ms (median 0.38) | 0.70, 0.55, 0.60, 0.57 ms (median 0.59) | pass; tip about 0.2 ms higher here |
| Players32 tick-loop p99 | test guard | 10.21, 11.25, 10.72, 11.82 ms (median 11.0) | 8.00, 11.25, 9.72, 10.72 ms (median 10.2) | pass, 0 dropped ticks |

Smoke8, Light8, Players32 and both kill-burst tests passed in all 8 runs.

- **Does taking `_authoritySync` in the drain hurt? No.** Tip against the same tip with that lock removed (scratch worktree, dll swapped into a copy of the tip test folder), Smoke8 and Light8, 5 alternating rounds. Light8 p99: tip 0.35, 0.63, 0.41, 0.50, 0.39 ms (median 0.41); no lock 0.26, 0.57, 0.55, 0.52, 0.55 ms (median 0.55). Smoke8 p99: tip 0.98, 1.76, 1.45, 1.08, 1.19; no lock 0.89, 4.68, 1.76, 1.94, 1.38. The lock is not what moved Light8.
- **Do chasing, biting wolves move tick cost? Slightly at most.** The Light8 gap to the base (0.2 ms at p99, p50 equal at about 0.24 ms) is inside the spread between two sessions of the same tip (0.41 median in the lock runs, 0.59 here), so it is not established. Nothing was profiled.
- Worst tick in the kill burst: +0.15 ms, consistent across 4 and 4 runs; cause not looked into.

### Soak

`ServerLoadTests.Soak`, Release, 1,500 s instead of 1,800 s (`DREAM_LOAD_DURATION_SECONDS=1500`), the tip test assembly, 8 bots joining and leaving every few seconds, 4 enemies (the Wolf bites now). Passed, 25 m 1 s. 307 of 307 joins, 0 connect failures, 0 rejected, 0 server connection errors. 64.0 ticks/s, 0 dropped ticks. Tick-loop p50 / p95 / p99 / max 1.315 / 2.604 / 5.683 / 41.144 ms (the 41 ms max and a worst one-second p99 of 51 ms fall in the time I ran the real-process checks next to it). Checkpoints 300 written, 0 failed; persistence writes 12,002, 0 failed, max queue 8. Managed heap after full GC 57.7 MB after warm-up, 55.1 MB at the end (guard +5%). Peak working set 299.8 MB. GC gen0 / gen1 / gen2 125 / 71 / 48. **Not confirmed:** whether the bots were bitten at all; the report holds no health or death counts, so "with biting wolves" only means the tip's wolves were in the world.

### Real processes (loopback only)

`dream.exe --server` listens on **all interfaces** (`ServerBootstrap.RunServerModeAsync`: `new TcpServerTransport(IPAddress.Any, DefaultPort)`, banner "listening on 0.0.0.0:5057"). Earlier waves ran it that way. This round did not: `lbhost` (scratch, `scratchpad/wave-h-e2e/lbhost`) is the same host, service, codec, framer and options with `IPAddress.Loopback`, started as `dotnet app/lbhost.dll` from the same `app` folder (rebuilt in Release with `dotnet build dream/dream.csproj -c Release -o app`, so `dream.exe` is at the same path). It is not the `dream.exe --server` entry point. The clients are the console driver over the real `RemoteClientSession` with `DREAM_IDENTITY_FILE`, and the hostile client on raw sockets.

`flow_i.py` on the 4 `legacy-e` copies: **33 of 33 on each, 132 of 132**. The 28 earlier checks plus 5 new ones:

- A player that joins dead and respawns loses no health in the first 185 ticks after the respawn and is then bitten: first bite 194 ticks after the respawn snapshot on every database, 3,016 to 3,047 ms later. Snapshots come every 2 ticks, so 193 is the true first possible bite.
- A player that stands still at (7, 5) is bitten 36 to 38 ticks after the idle starts.
- The loot, full-backpack, watcher and hostile-dead checks were unchanged and pass with wolves that bite.

### Idle new player on a fresh world

Fresh database, new identity, 100 health, joins at the spawn (5, 5) and stands still; the day Wolf spawns 2.5 m away. `lbhost` Release, loopback, 100 s each, day (08:00 start) and night (`DREAM_TIME_OF_DAY=22:00`; the Dire Wolf is listed at level 3 but did not bite in the 100 s).

- **Day and night are the same:** first bite about 9 ticks after the join (hp 88), then 12 damage every 68 ticks (1.06 s). **Dead at server tick 573, about 8.6 s after the join snapshot (tick 22).**
- **It is a repeating loop.** Respawn after 320 ticks (5.0 s), 3 s protected, first bite 194 ticks after the respawn, dead again 8.5 s later. Deaths at ticks 573, 1631, 2687, 3744, 4802, 5858: **4 deaths in the first minute, 6 in 100 s, one every 1,057 ticks (16.5 s)**: 5 s dead, 3 s protected, 8.5 s dying. An idle player never gets out of it. Balance not changed.

### Adversarial tests (all pass)

- `RubbleAtTheSpawn_AWolfBesideIt_...`: respawn lifted clear of rubble, 192 protected ticks with a wolf 1.3 m away, bitten afterwards.
- `ProtectionDoesNotSurviveALeaveAndRejoin_...` and `AProtectedPlayerWhoLeavesAndRejoins_IsNotProtectedOnReturn_OverRealTcp`: the rejoined player is a normal target within 1 step; a later death gets a full new window. `LeavingWhileDeadAndRejoining_...`: fresh 321-tick wait.
- `TwoSeats_HaveTheirOwnWindows_...`: A and B respawn 200 ticks apart, one wolf: A is bitten after its own window while B stays untouched through its own.
- `AnotherKillWhileDeadAndWaiting_...`, `KilledInsideTheWindow_...`: a repeated kill neither shortens nor extends the wait; a kill inside the window gives a full wait and a new window.
- `AWolfAtOrInsideTheSeparationDistance_BesideACube_...` (gaps 0.95, 0.8, 0.5, 0): the wolf ends 0.95 m from the player, never inside the wall, no NaN, the player is not pushed into the wall, and it bites. `APlayerOnAStep_...`: a player on a 2-cube step at (22.5, 3.5, 20.5) is **not reached**: the wolf stays Chasing at its foot (x 20.69), no bite, no NaN. That is the pre-existing "wolves cannot climb" rule, now a safe spot.
- `ACampedPlayerAtNight_...` (Wolf and Dire Wolf camped at the spawn, 4,000 ticks of a 5-minute day): 4 cycles, deaths at 219, 924, 2018, 2765, each respawn exactly 320 ticks after the death, health full at each respawn and unchanged for the 192 ticks, no death inside a window; a bystander killed at 1,212 is back at 1,533 (321 ticks) with full health.
- `FivePlayersAroundOneWolf_...`: 15 bite ticks in 1,000 ticks against a limit of 16 (interval 1.05 s), one player took them all, positions finite.
- `TwoServersInLockstep_AgreeThroughTwoSeatsDyingRespawningAndRejoining`: checksums equal over 1,800 ticks.

### Full suite

One run, Debug, quiet machine, 3 m 5 s: **4,756 passed, 0 failed, 46 skipped, 4,802 total.**

### Not verified

`dream.exe --server` itself (it binds all interfaces), a real `PlayScene.Update`, the picture on screen, a kill while a resync (not a join) transfer is in flight, bots being bitten in the soak, the 30-minute soak length (25 minutes run), the stored-world soaks.

## Fix loop 4

The last fix loop of wave I, on `ee50e67` (tip of `integrate/engine-iteration`). Items 1 to 6 and the nits of the brief.

### 1. Bite reach (Must fix)

- **The bug.** `EnemyActor` zeroed `toPlayer.Y` for the bite range and wolves never change Y, so a wolf at the foot of a pillar, under a floor or under a sealed room bit the player above it, through the cubes. The separation step also shoved a player standing above a wolf sideways (`ResolvePlayerEnemySeparation` was horizontal only).
- **The rule.** A bite needs (a) the player within the horizontal range, (b) within vertical reach: the gap between the wolf's vertical extent (0 to 0.6 m) and the player's box (1 m centred on its position) is at most `EnemyActor.BiteVerticalReach` (0.5 m), and (c) no cube on the horizontal line between them at the wolf's body height, sampled every 0.25 m (`EnemyMovementCollision.HasClearBiteLine`; cubes below the player's feet cell do not count, so a wolf at the foot of a 1-cube step still reaches). A wolf out of reach keeps closing in horizontally to its stop point and then stands in `Idle`, not `Attacking` and not `Chasing` in place. Separation skips a player whose body does not overlap the wolf's vertically. No allocation (`ABitingTick_AllocatesNothing`).
- **Design call.** A player on a 1-cube step can be bitten (gap 0.4 m); a player on a 2-cube step (gap 1.4 m), on a pillar or above a floor cannot.
- **Known limit, left alone as asked.** Wolves cannot climb, so a 2-cube step is a safe spot. Unit cubes are 1 m wide, so the cube line check can only matter for positions the physics does not produce (the actor-level test uses one); the vertical rule does the work in play.
- **Tests (`WolfReachTests`).** Tower, under-floor, sealed upper room (overhanging a pillar), the 1-cube step that is still bitten, the 2-cube step that is not, a control on the floor that is bitten, the vertical reach at its boundary (5 cases), a cube on the line, the leash (2) and allocation. **They fail without the fix**: with the vertical test and the line check disabled, 7 of the 13 cases that existed then failed (two-cube step, under-floor, tower, sealed room, both out-of-reach boundary cases, cube on the line); the fix was restored.

### Idle spawn loop (2)

- **a. Join protection.** `AttachRemoteSession` (every join, first or returning, but not a repeated join of an already attached seat) and the local seat's construction set `ProtectedThroughTick` to tick + 192, like a respawn; attacking ends it, a detach clears it. A rejoin now has its own full window, so `ProtectionDoesNotSurviveALeaveAndRejoin_...` became `ARejoin_IsProtectedByItsOwnWindow_NotTheOldOne_AndALaterDeathGetsAFullFreshWindow`, `TheFirstSpawn_HasNoProtection` became `TheFirstSpawn_IsProtectedFor192Ticks_ThenBitten`, and `AProtectedPlayerWhoLeavesAndRejoins_IsNotProtectedOnReturn_OverRealTcp` became `..._GetsItsOwnWindowOnReturn_OverRealTcp`.
- **b. Spawn outside aggro range.** The day wolf spawns at (width - 3.5, 1, depth - 3.5) clamped into the map (was (5.5, 1, 2.5)); the night's second wolf at (width - 2.5, 1, depth - 2.5), stepping away if it lands on the day spawn. On the 56 m map: (52.5, 52.5) and (53.5, 53.5), 67 m from the world spawn (5, 5); on 30 m, 30 m; on 100 m, 129 m. Maps below about 16 m are too small to stay outside the aggro range (10 m wolf, 12 m Dire): 16 m puts the day wolf 10.6 m and the Dire 12.0 m away. As before, terrain edits at a spawn are not looked at.
- **c. Leash.** `EnemyActor.HomePosition` (the spawn point; a restored wolf gets its role's spawn from `WolfSpawnPolicy`, since nothing is persisted). A wolf with no target, or none in aggro range, walks home at its speed, stops within 0.05 m and idles (`Chasing` while it walks, so the client shows the run clip). Deterministic, no allocation, in no checksum.
- **Measured** (`IdleNewPlayerTests`, 6,400 ticks = 100 s, a new player joined at (5, 1.5, 5) standing still; day, and night from 22:00 of the second day): maps 30, 56 and 100, day and night: **0 attacking ticks, 0 chasing ticks, health 100/100 throughout, in all six runs.** Closest wolf 30.4 m (map 30), 67.2 m (56), 129.4 m (100); one wolf by day, two at night. Before: first bite at tick 9, death at tick 573 (8.6 s), in a loop of 4 deaths a minute. Not measured: a real process over TCP (the earlier tester's `lbhost`); this is the runtime with its clock, join and tick path, not the socket.
- **What still happens.** A player who walks toward the far corner is attacked as before. The first 3 s after every join and respawn are safe, and the protection ends on the first attack.

### Re-pins (old value, new value, cause)

| What | Old | New | Cause |
| --- | --- | --- | --- |
| `StoredWorldJoinLoopbackTests` C0 capture, total bytes | 8,492 | 8,494 | the join's actor list holds the day wolf at (52.5, 1, 52.5) instead of (5.5, 1, 2.5): 2 bytes longer |
| same, SHA-256 | `40391109031e35d0...` | `e98ef020127c415a...` | the same |
| `HeldLightSnapshotBytesTests` (200 ticks, two sessions) and `WaveBTesterWorldsLoopbackTests` | 58,827 B | 58,140 B (-687) | the wolf stays at its far spawn and nobody is bitten (join protection and out-of-range spawn), where it chased and bit before |
| `WorldSessionRuntimeTickEquivalenceTests.GoldenCheckpoints` 6-10 | `0x8118838F28D2A8DB`, `0x896E506A472FAAF5`, `0x7FB267DD0A1ED1E7`, `0xA399D97471F2108A`, `0x626AC8A4F7C77C1E` | `0x47B5F355035AF99E`, `0x1A65C2CE9810486A`, `0xB2A5C4A1D0C2DEE1`, `0xA0CDA305A4CCCCD4`, `0x55BC8DA8232AA9FB` | the respawned wolf spawns at the far spawn ((12.5, 12.5) on the 16 m map) instead of beside the players. Checkpoints 1-5 are unchanged: the test now places the first wolf at (5.5, 1, 2.5) and ends the join windows, which reproduces the old run up to the wolf's death |
| `WolfSpawnPolicyTests`, `WaveT2NightSpawnTests` | (5.5, 1, 2.5); (50.5, 1, 53.5) on 56 m | (21.5, 1, 21.5) on 25 m; (52.5, 1, 52.5) and (53.5, 1, 53.5) on 56 m | the new spawn positions |

`SimulationStateChecksumTests` did not move. `NoLight_200Ticks_IsStill58307Bytes` is now `NoLight_200Ticks_Is58140Bytes` and `WaveT1SnapshotBytes` is `NoLightSnapshotBytes`.

### 3 to 6 and the nits

- **3. Attack range.** The catalog rejects an `attackRange` at or below `PlayerSeparationDistance / StandOffRangeFraction` = 1.0556 m (stricter than the 0.95 m of the brief: the stand-off is at most 90% of the range, so a range of 1.0 m puts it at 0.90 m, inside the separation, and the player is shoved). Tests: `Load_RejectsAnAttackRangeTheWolfCouldNeverBiteFrom` (0.1, 0.95, 1.0, 1.05) and `Load_AcceptsAnAttackRangeAboveTheSeparationOverTheStandOffFraction`. `TheStandOffLiesInsideTheAttackRange_ForAnyTemplate` now uses ranges the catalog accepts and asserts the stand-off stays at or above the separation.
- **4. Tests.** `AProtectedSecondarySeat_NextToTheWolf_IsSkipped_AndTheWolfBitesTheUnprotectedPlayerFartherAway`, `AWolfOverlappingAProtectedPlayer_DoesNotBiteIt_ShovesItClear_AndStaysFinite`, `ARepeatedJoinOfAnAttachedSeat_DoesNotStartANewWindow`.
- **5. Lockstep across a restart.** `WolfRestartLockstepTests.AWolfChasingAndBiting_StaysInLockstepAcrossARestartOfBothServers_OnEditedTerrain`: two servers on their own databases, a pocket of generated terrain cleared on both, a real wolf placed 6 m from the player; equal checksums every tick of 140 ticks (first bite at tick 108); both restarted on the same databases; the wolf restored where the final checkpoint left it with the day spawn as home, the cleared pocket still cleared, the player rejoined with a new 192-tick window; equal checksums for 452 ticks and the first bite at tick 270 on both. Cubes added straight to the map are not persisted, so only the terrain removal is the edit.
- **6. Parks.** With the far spawn and the leash, the parks in `SoakServer` (`StoredWorldTestSupport`), `PersistentScriptedServer`, `IncrementalCheckpointTests` (`ParkWolves`), `CheckpointLeaveRaceLoopbackTests` and `WaveIAdversarialTests` are gone and those tests pass without them (the (50, 1, 50) park was outside the 30 m map). `Wave13ExtractedSeamsLoopbackTests` no longer parks the wolf; it asserts the wolf stays idle at its home. Parks remain only where a test needs a wolf to stand still near a player: it places the player beside the wolf's home (`WaveIRuntimeKit.PlaceBeside`).
- **Verification.** Filtered runs while working, then two full runs (Debug, quiet machine). Run 1: 4,790 passed, 4 failed (three tests that assumed the wolf beside the spawn and one that parked it), 46 skipped, 4,840 total, 3 m 33 s. After fixing them, run 2: **4,794 passed, 0 failed, 46 skipped, 4,840 total**, 3 m 19 s. `dream/dream.csproj` builds with 0 errors and 17 warnings (all existing).
- **Nits.** `ProtectedThroughTick != 0` is checked in `FillEnemyTargets`. `GameplayInputResumeFilter` suppresses a use held through the pause until it has been released once (`Filter_KeepsSuppressingAnAttackHeldThroughTheBlockedPeriod_UntilItIsReleased`); a real attack still ends protection. `docs/architecture.md` (stop point, rubble, spawns, reach, leash), the `WolfDefinition` margin comment and the `ResolveRespawnPosition` comment are corrected. `ResolveRespawnPosition` takes a scratch list (a runtime field) instead of allocating one per respawn. `LootGrantTests` no longer sleeps: `NetworkDiagnosticsLog.ForgetRateLimitKey` clears the key. The tests that assumed the wolf beside the spawn (`WolfBite*`, the interpolation replication tests, the host, combat-queue and action-flow tests, the tick-equivalence script) now place the wolf or the player explicitly.
