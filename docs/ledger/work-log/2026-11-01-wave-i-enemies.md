# Wave I: Enemies On The Same System

Purpose: record wave I of `docs/ledger/plans/2026-10-31-characters-items-enemies-roadmap.md`. Plan: `docs/ledger/plans/2026-11-01-wave-i-plan.md`.

Status: integrated on `integrate/engine-iteration`. Not pushed. Not merged into `master`. The tester's Release measurements and adversarial tests are below.

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

- **A wolf that chases stops out of its own bite range.** `WolfDefinition.PreferredStandOffDistance` is 2.0 m and `attackRange` is 1.35 m, and `EnemyActor` bites only within the range. So a wolf stops 2 m from a player who stands still and never bites until the player walks in. The Dire Wolf has the same range. These numbers predate wave I. It made the plan's spawn-camping risk smaller and made the driver's "die" step wait forever until it walked to melee range. Changing it changes combat, so the tick goldens would move. It is the user's call.

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
| World10k full checkpoint | C0 x 1.05 | **fails numerically.** Full-every-capture write mean, 12 paired runs: C0 34.6-35.2 ms, tip 37.5-39.0 ms (+9 to +11%); tip max 83-90 ms against 60-66 ms. Bisect: I1 merge 34, I2 merge 35.5-37.9, I3 merge and tip 38. I3 changes only Client, Rendering and a shader, and no checkpoint code changed in the wave, so the cause is not found in server code. Unexplained; not a settled regression |
| Join-window total | C0 x 1.10 | C0 15.0 ms, tip 13.5 ms per round |
| Integrity check, World10k | 15 ms | 7.20 ms (C0 7.15) |
| Migration 0009 | 50 ms | 0008 and 0009 together on 100,003 items: 108-141 ms (v7 to v9, two runs). The two are not split, so the 0009 figure alone is not measured |
| Kill burst: queue drained after the last kill | 100 ms | 0.3 ms (4 runs) |
| Kill burst: failed writes, items lost | 0, 0 | 0, 0 (100 pushes, 100 items, peak pending 2) |
| Kill burst: tick p99 | 15.6 ms, and C0 fight-only p99 x 1.25 | 0.20-0.22 ms in 6 runs; single worst tick 6.9-7.3 ms. C0 fight-only p99 0.16-0.19 ms (4 runs) so the limit is about 0.22 ms: passes with no margin. Tip fight-only 0.18-0.23 ms. Fight-only is `KillBurstFightOnlyBaselineTests`, run on both commits |
| 1,000-write drain | 32.1 ms | 25.9-26.1 ms (C0 26.8) |

Load harness (`ServerLoadTests`): Smoke8, Light8 and Players32 pass with tick-loop p99 0.98, 0.35 and 6.27 ms. World10k tick-loop p99 416 ms, 347 dropped ticks (the old known cannot-keep-real-time scenario; no baseline taken at C0). Soak (30 min) and the stored-world soaks were not run.

`KillBurstMeasurementTests` runs 100 real kills at 5 a second, in real time, across 10 characters. Seating them with `AttachRemoteSession` alone gave 10 failed writes, one each, because no profile row existed. That was the test's mistake, and it now seats them as a join does.

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
- **Notice cap.** A notice past `MaxNotices` is logged as `server.loot.notice-dropped`; a `lost` notice replaces the first ordinary one instead of being dropped. Only the full path allocates (the log string).
- **Nits.** `EnemyFactory.Restore` uses `Math.Clamp(level, 1, MaxEnemyLevel)` and its comment says the first checkpoint with a diff writes the clamped value. Two stale comments repaired (`WorldSessionRuntime.Enemies.cs`, `WorldSessionRuntime.Respawn.cs`). The `EnemyFactoryTests` XP assertion compared a call with itself; it now asserts the reward at the clamped level is positive and lower than the reward at the stored 5000 (it does not run `AuthoritativeCombatResolver`).
- **Unverified, not changed.** Respawn into rubble: fractured cells may have no Cube, so `IsSolidCell` may not see pieces. Queued action results are not purged on leave, so a stale `InventoryChangedResult` could reach a rejoined session.
- **Full suite after merge 1433ec8.** One run (Debug, quiet machine, no stray processes): 4,719 passed, 0 failed, 46 skipped, 4,765 total, 2 m 40 s. `dream/dream.csproj` built with 0 errors and 16 warnings.
