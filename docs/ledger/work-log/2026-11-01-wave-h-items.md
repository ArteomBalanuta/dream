# Wave H: Items With Their Own Stats

Purpose: record wave H of `docs/ledger/plans/2026-10-31-characters-items-enemies-roadmap.md`. Plan: `docs/ledger/plans/2026-11-01-wave-h-plan.md`.

Status: integrated on `integrate/engine-iteration`. Not pushed. Not merged into `master`. Tester and reviewer have not run yet, so this log has no Release measurements.

## Process

1. **C0 (`e0a0b07`).** Contracts, migration 0008, protocol v10, `ToolStatModifier` retired.
2. **Merges, no conflicts.** H2 (`8fdd159`), H1 (`de9ed96`), H3 (`bb82722`). Focused tests passed after each.
3. **Glue.** See below.

## Production bug found

**Unequip, destroy and every backpack move were not persisted.** `SqliteWorldPersistenceRepository` is the facade the runtime writes through, and it did not implement `DeleteInventorySlot` or `DestroyInventoryItem`. Those two `IInventoryRepository` methods have do-nothing default bodies so the test fakes compile, so the facade silently used them. H2 tested the repository directly and H1 tested a stand-in, so neither stream could see it.

On the real repository, before the fix, 8 of 11 new cases failed. An unequipped item was back after a restart, a destroyed item stayed, and a moved item kept its old row as well as its new one. The facade now forwards both, and `EquipmentSqliteActionTests` fails if any repository member falls through to a default body.

## Glue

- `EquipmentSqliteActionTests` runs H1's move, swap, unequip, destroy and legacy-rescue cases on the real repository, through a reopened bundle and a new service, as a restart does.
- `RolledItemEndToEndTests`: `--items grant` for all four slots, start, join over TCP, equip with intents, client and server effective stats equal, restart, rejoin, `--worlds copy`, join the copy, `export`, join again. Stored roll text stays byte-equal in all three worlds.
- `RestartMidSessionMeasurementTests`: run 2 gives one character four rolled, equipped items and ends in a mid-session stop, and a new run 3 restarts and rejoins. The invariants now compare that stop with run 3, for every character. They fail on empty sets. Run 1's rows are checked as a subset, because the gear is new in run 2.
- `StoredWorldJoinLoopbackTests` gates the real frame at 4,099 B.
- `docs/configuration.md` and `docs/architecture.md` are updated.

## Measured

`server.join-accepted` for the starter kit is **4,048 B**, read from the real frame before normalization, in the same scenario as the 3,565 B baseline. The gate is 4,099 B, so the plan's wire-shortening was not needed. Margin: 51 B.

## Reviews asked for at glue

- **`ActionGate`.** Every client action reaches the service through `RunInventoryAction`, with the session taken from the action's own context, and `InventoryBudget` is a field, so the struct is spent in place. No wrong-session path was found. The gate was bound lazily on the first action, which made this harder to see, so it is now bound once in the constructor. A new test pins that two interleaved sessions spend their own budgets. A rejoin creates a new `ConnectedPlayer`, so it starts with a full budget; joins have their own throttle.
- **`StoredWorldJoinLoopbackTests` normalization.** The regex removes `modifiers`, `heldLight` and `equipSlots` only directly after an item's `detailLines`, in that order, and `rarity`, `isRolled` and `equipment` stay. The SHA still covers every other byte. A new theory pins this, including a field out of order staying visible.
- **Unequip contract.** H3's two menus send 100..103, which `TryUnequip` takes. Pinned for all four slots. The comment on `InventoryHudAction` claimed a backpack slot also works, and it does not, so the comment is corrected.
- **Inert items.** A retired kind, rolled or not, and an item in a slot it may not occupy, arrive with no modifiers and no light. Pinned on the real repository, with empty client and server loadouts. A damaged roll on a known kind keeps its definition's light on both sides: the plan makes it statless, not lightless.

## The recurring flake

`Wave13PlayerWorldStateOnLeaveTests.LeaveBeforeTheFirstCheckpoint_ThenRejoin_KeepsThePositionAndHpTheyLeftWith` ("1366 ticks played") was not caused by wave H. It is a test harness race.

A join is deferred until its persisted state has loaded, and `Rig.WaitUntilJoinedAsync` steps ticks with no pause meanwhile. So ticks measure load latency, at about 26 ticks per millisecond, and a stall of about 50 ms passes the test's 320-tick limit.

| Write queue stall at join | C0 ticks | Tip ticks |
| --- | --- | --- |
| none (10 runs each) | 68 to 69 | 68 to 69 |
| 100 ms | 675 to 725 | 644 to 709 |
| 500 ms | 13,399 to 13,717 | 13,476 to 13,527 |
| 1,500 ms | about 39,100 | about 39,100 |

C0 and the tip match, so wave H did not slow the join. Outside CPU and disk load alone did not reproduce it: 12 runs each stayed at 68 to 69. With the Integration tests in the same process, C0 reached 150 and the tip stayed at 68, one run each, so the delay comes from contention inside the test process. The exact source of the stall in a full-suite run was not isolated.

The rig now has `awaitPrefetchBeforeTicking`, as `WireLoopbackServer` does, and this test uses it: 68 ticks even with a 1.5 s stall.

## Checks on real processes

Run on copies of the 4 `legacy-e` databases, Debug build, `dream --server`. Clients were a console driver over the real `RemoteClientSession` with `DREAM_IDENTITY_FILE`. The hostile client is hand-written Python on raw sockets. 24 checks on each database, all passed.

- Start migrates v6 to v8 in one step and leaves one `v0006-to-v0008` backup. These files are v6, so the plan's `v0007-to-v0008` name does not apply; `ItemRollsMigrationTests` covers that step.
- Every slot-100 row is unchanged by the migration, and both joined characters hold the tool they held before.
- `--items grant` works offline, and exits 7 with the server running and 2 for an unknown character or a kind with no rolls.
- Rolled blade in the main hand plus a torch in the off hand are held together. A second client saw the torch light arrive and go.
- Unequip survives a kill of the server and a restart, and the blade keeps its rolled stats.
- The hostile client's 44 bad intents were all rejected and answered, with its connection kept. 1,000 moves in a burst: 16 accepted, every one answered. No other player's row changed. Bad JSON, an unknown `$type`, a string or oversized slot and deep nesting close that connection, and the server keeps accepting joins. Extra fields on a valid intent are ignored.
- Two real desktop clients with their own `DREAM_IDENTITY_FILE` joined the server, one holding the rolled blade and a torch. Both ran for 26 s and 14 s with no exception logged.

## Not done

- **No panel screenshot.** A Windows Firewall prompt for the copied `dream.exe` covered both windows, and it was left for a person to answer. The panel, the off-hand pose and the torch light as drawn have not been seen on screen. Loopback was not affected.
- **The UI itself was not driven.** The desktop clients did not click anything, so panel actions are covered by the pure model tests and the equips went through the driver.

## Notes

- The driver stored its token under `127.0.0.1:5057` and desktop clients use `local`, so the first desktop attempt was correctly refused. Documented wave G behaviour.
- The 4,099 B gate covers the starter kit only. A join with rolled gear is larger and has no gate.

## Full suite

Debug, two runs, both on the final glue. `dc85582`: 4,419 passed, 0 failed, 43 skipped. `6537031`: 4,421 passed, 0 failed, 43 skipped; the two extra are the contract tests added between them. The flake did not fail in either run.

## Verification after the fix loops

What the tester measured on the tip after the fix loops:

| Gate | Measured |
| --- | --- |
| Join accepted (starter kit) | 4,048 B, under the 4,099 B gate |
| Join of a fully rolled character | 9,400 B, no gate |
| Checkpoint cost | 1.02x |
| Integrity check | 7.1 ms |
| Migration of 100,003 items | 91.7 ms |
| Rolls from 144 grants | all within their ranges |

Duplication attacks over real TCP (replayed and raced equips, moves and unequips) left every row audit clean: no item in two places, no lost item.

Fix loop 2 made a new player's seed (kit items, slots, main-hand equip, grant record) one queued closure, so a crash leaves either a new player or the complete grant. A seed cut short by the old per-item writes (the first 1 to 3 version-1 kit items in slots 0..k-1, nothing else, no grant row) is read as a new player's, so the next join gives the missing kinds without duplicating what is there. Any other inventory with no grant row is still a pre-kit one, and a player with a grant row is never given an old kind again.

Known limits:

- No one has seen the panel on screen.
- A failed save can leave one item in two slot rows until a later write fixes it. Nothing is lost.
- Inventory actions while dead were not run over real TCP.
- The perf harness does not equip gear, so its numbers cover no gear.
