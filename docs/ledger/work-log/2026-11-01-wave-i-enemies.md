# Wave I: Enemies On The Same System

Purpose: record wave I of `docs/ledger/plans/2026-10-31-characters-items-enemies-roadmap.md`. Plan: `docs/ledger/plans/2026-11-01-wave-i-plan.md`.

Status: integrated on `integrate/engine-iteration`. Not pushed. Not merged into `master`. Tester and reviewer have not run yet, so this log has no Release measurements. The table below has rows for them.

## Process

1. **C0 (`de4c374`).** Migration 0009, formulas, protocol v11, loot content, enemy stat core.
2. **Merges, no conflicts.** I1 (`df2bbd7`), I2 (`7275d21`), I3 (`fc0bc71`). Focused tests passed after each.
3. **Glue.** See below.

## Production bug found

**A push could be undone by an older inventory.** An action result waits for the next snapshot and carries the inventory of its own tick. A push goes out at once. At the default snapshot interval of 2 ticks, an equip on a tick with no snapshot and a kill on the next tick delivered the push first and the older result second. The client keeps what it applies last, so the looted item vanished from the client until its next inventory change. The server was right all along, and nothing was lost.

`ServePendingInventoryPushes` now leaves a push pending while an `InventoryChangedResult` is queued, and builds the snapshot when it sends. `EnemyLootEndToEndTests.AnInventoryResultStillWaitingForItsSnapshot_DoesNotUndoTheLootOfTheNextTick` fails without the change (1 rolled item shown, 2 held) and passes with it. In that rare case a push now arrives after the snapshot of its tick, by at most one interval. The scan runs only on a tick with a push pending.

## Glue

- `ClientDeathTracker.RespawnDelayTicks` is `PlayerRespawnRules.DelayTicks`.
- `HeadlessPlayScene` is a real `PlayScene` with no GL, fed by a real `RemoteClientSession` over TCP. So join, snapshot, action result and push all run through `ApplyReplicatedMessage` in production order, behind the session's real world-transfer hold. `Update` does not run, so prediction and the reconciler's respawn snap stay covered by I3's tests only.
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

- **A wolf that chases stops out of its own bite range.** `WolfDefinition.PreferredStandOffDistance` is 2.0 m and `attackRange` is 1.35 m, and `EnemyActor` bites only within the range. So a wolf stops 2 m from a player who stands still and never bites until the player walks in. The Dire Wolf has the same range. These numbers predate wave I. It made the plan's spawn-camping risk smaller and made the driver's "die" step wait forever until it walked to melee range. Changing it changes combat, so the tick goldens would move. It is the user's call.

## Measured

To be filled in by the tester, Release, `DREAM_LOAD_TEST=1`, against C0b.

| Measurement | Gate | Result |
| --- | --- | --- |
| Tick allocation, including a dead session and 4 rolled items | 0 B | |
| Hit with Armor > 0 | 0 B | |
| `NoLight_200Ticks` | 58,307 B | |
| Actor bytes with a Dire | at most 24 B per Full record, none on a delta | |
| `server.join-accepted`, starter kit | 4,099 B | |
| World10k join 0 | identical to C0b | |
| Join with 9 items, fully rolled | 9,400 B | |
| Lost-only push | 256 B | |
| World10k full checkpoint | C0b × 1.05 | |
| Join-window total | C0b × 1.10 | |
| Integrity check, World10k | 15 ms | |
| Migration 0009 | 50 ms | |
| Kill burst: queue drained after the last kill | 100 ms | |
| Kill burst: failed writes, items lost | 0, 0 | |
| Kill burst: tick p99 | 15.6 ms, and C0b fight-only p99 × 1.25 | |
| 1,000-write drain | 32.1 ms | |

`KillBurstMeasurementTests` runs 100 real kills at 5 a second, in real time, across 10 characters. Seating them with `AttachRemoteSession` alone gave 10 failed writes, one each, because no profile row existed. That was the test's mistake, and it now seats them as a join does.

## Not done

- **Perf harness and screenshots.** Plan glue step 7 is left to the tester. Nobody has seen the Dire Wolf, labels, death overlay or loot feed on screen.
- **Reconciler's respawn snap with a real `Update`.** Covered by I3's unit tests, not by `HeadlessPlayScene`.
