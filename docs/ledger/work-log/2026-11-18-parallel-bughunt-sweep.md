# 2026-11-18 - Parallel bughunt sweep (54 fixes)

Branch `integrate/bughunt-w1`, from `ff3d3ed7`. Thirteen workstream branches `fix/bh-*`,
each owning disjoint paths, merged in completion order. New tests live under
`dream.Tests/BugHunt/` with a per-workstream prefix.

## Why

A survey sweep rather than a feature: read-only hunters were pointed at one domain each,
told to produce evidence-based findings with `file:line`, a trigger, the broken invariant
and a disproof condition, and to drop anything they could not prove. Fixers then took one
packet each in an isolated worktree.

## Result

Full suite on the integration branch: **8922 passed, 0 failed, 47 skipped**.
`master` itself ran 5 failed / 8794 passed before this work.

Of those 5 master failures: two were artifacts of the `/p:BaseOutputPath` redirect (the
tests walked a fixed `../../../..` from `AppContext.BaseDirectory`; they now use
`RepoRoot.Find()`), one was a genuine `Client -> Server` dependency violation introduced
by the multiplayer menu (`MainMenuScene` reached for `ServerBootstrap.DefaultPort`; it now
goes through the already-allowlisted `RemoteClientOptions.DefaultPort`, and the ratchet's
allowlist was NOT widened), and two were load-related integration flakes that pass in
isolation.

## Fixes by area

- **Protocol:** a blank `requestedSessionId` threw `ArgumentException` past a catch that
  named only `InvalidOperationException` and killed the dedicated server's tick loop; a
  missing `displayName` bound to null on a non-nullable record parameter; a merged snapshot
  that dropped its deltas kept the `WorldChecksum` covering them, so the client acked
  mutations it never applied and resynced twice (`ServerConnection.WithoutDeltas` already
  dropped it on the unmerged path).
- **Server:** the `MaxPlayers` gate matched a seated player by display name, so it was
  bypassable by typing another online player's name; a queued repeat join double-counted
  its own seat; one transient accept error stopped the server accepting anyone (the process
  kept ticking, so it surfaced only at shutdown); a muted admin could still broadcast via
  the announce verb; an out-of-process `unban` was shadowed by the host's sanction memory;
  `_sanctionMemory` ignored its own cap; an unconsumed close reason could reach a different
  connection reusing the same `ip:port` id.
- **Simulation:** a restored enemy took `max_health` and `movement_speed` unclamped from the
  persisted row while every sibling stat was clamped; a trade could give away an item the
  giver was also wearing; primary melee hit through walls while the wolf's own bite was
  LOS-checked; a cube built on an enemy's cell wedged it and leaked its camp slot; a night
  camp's dawn despawn never completed after one non-fatal hit; a build-protected swing spent
  the attack cooldown.
- **Persistence:** a batch-create throw outside `DrainPending`'s try left the write queue
  `_running` forever, queueing writes that `Flush` then threw on and `Dispose` dropped
  uncounted; checkpoint capture could overwrite a cube's cached persisted owner with null;
  `PlayerMotor`'s freeze stored `InvMass` and restored it as mass.
- **Client:** hosting mispredicted noclip/freeze, so every snapshot rubber-banded the player;
  a Join could not be cancelled, and a cancelled connect could still attach; a cancelled
  auto-reconnect still published its session; `DisconnectReason` raced between two paths;
  a malformed snapshot NREd on the frame thread instead of ending the session.
- **Rendering:** a chunk that lost every cell was never retired, so a replaced map's terrain
  stayed drawn and kept casting shadows; `ReplaceMesh` recorded its signature before the
  upload, so one failed upload hid a chunk permanently; a fire-shadow scissor leak; actor
  tint never reached cut-out parts; enemy tint bled into the next draw; HUD: a quest-log
  push between press and release could send `QuestAbandonIntent` for a button pressed as
  "Turn in"; world labels kept hit boxes under the opaque status bar.
- **Config:** a frame-callback exception skipped `OnUnload`, so the integrated server was
  never stopped and the world never drained to disk; NaN survived `Math.Clamp` in several
  config sanitisers; `MsaaSettings.Sanitize` overflowed on `int.MinValue`.

## Rejected and reverted

One rendering "fix" was **reverted**: charging a throwing GPU upload against the frame's
upload budget is intended, and `AsyncResourceLoaderStressIntegrationTests` asserts every
frame that `PumpUploads` returns the number of upload calls it made. The faulting-decode and
faulting-upload tests were consistent all along - a decode fault makes no upload call.

One client guard caused a deterministic regression and was repaired: withholding stale
actors must preserve entity **existence** but not their **pose**. An all-or-nothing gate kept
a stale interpolation buffer alive, rendering a wolf at its original spawn for ~60 snapshots.
The guard is now per actor; NPCs keep an all-or-nothing skip deliberately, since
`ClientNpcCache.ApplyActors` rebuilds wholesale and they are static props.

One finding was rejected as already fixed (read against stale code) and one refused as
written: `ServerSnapshotConverterTests.AssertDecodesLikeReference` deliberately pins null
collections as a decoder parity contract, so that guard went to the client boundary instead.

## Left open, needing an owner decision

- An in-cell cube move with `syncPhysics: false` leaves a stale collision descriptor and
  marks no cell dirty (`Map.Cubes.cs:163-183`). An in-code comment says the skip is
  deliberate and compensated by `BumpStaticContentVersion`; the only caller is every moving
  cube every authoritative tick, so the packet's fix would add a per-tick collision rebuild
  to a hot path. No consumer reads the stale descriptor today.
- `BH-PERSIST-2`'s offline case is now bounded by one standing-refresh interval (~15 s)
  rather than instant. Instant would need a durable read on the join path, which the host
  deliberately avoids ("the tick never reads the database").
- The `stat_points` column drop at `SqliteSchemaMigrator.cs:105` was investigated read-only
  and found **safe**: `DivergentStatPointsMigrationTests` already proves the drop with a
  dependent row, `PRAGMA integrity_check` and schema equality against a fresh database.

## Process notes

`git stash` is shared across all worktrees of a repo, so two parallel fixers popped each
other's work-in-progress. Both recovered; no work was lost. Parallel-agent briefs for this
repo should ban stash and use `git diff > patch` + `git apply -R` instead.

Not pushed, not merged into `master`.

## Later waves (6-8)

Suite after these: **8946 passed, 0 failed, 47 skipped**, twice in a row.

### Test-suite integrity - the most consequential change here

`dream.Tests` called process-wide `SqliteConnection.ClearAllPools()` in **239 statements across
94 parallel-phase files**, tearing down connection pools belonging to tests running concurrently
in other xunit collections, in breach of the suite's own documented rule. This is the mechanism
behind the long-standing "~35 spurious persistence/integration failures and testhost hangs on
file locks under a loaded suite", and behind `TerrainLockstepRestartTests` failing only under
load. All 239 now call `SqlitePools.Release(path|schema)`, which clears only the connection
string in play; `NoProcessWidePoolClearGuardTests` fails if one is reintroduced. Two call sites
were releasing the wrong database (`StoredWorldTestSupport.cs:61` released the not-yet-assigned
field instead of the source it was copying; two soak teardowns released an already-disposed
database).

Separately: a mid-test `PersistenceWriteQueue.Flush(30 s)` blocks the calling thread and starves
neighbouring test classes (3/3 reproducible). Polling with `UntilAsync` fixes it; other mid-test
`Flush` calls in the loopback family are candidates for the same treatment.

Six tests that asserted nothing now assert something real: `Building_Mode_Is_Available` compared
an enum constant to itself (it now pins the wire values, since the enum crosses the wire as a bare
int); `StartupWorld_ReportsLegacyAndCurrentPlanTimes` asserted nothing; four `MenuTextPreviewTests`
theories rendered a menu and asserted nothing. `Admin_Mute_...AcrossARejoin` was satisfied by the
2-minute in-memory `SanctionMemoryWindow` overlay and never exercised the `muted_until_utc` row it
is named for - it now asserts the row and proves the row does the silencing by restarting the
server. All 47 skipped tests were confirmed to be deliberate `DREAM_LOAD_TEST` opt-in gates;
nothing was silently disabled.

### Shutdown and lifecycle

- **A faulted server tick skipped `_host.Dispose()` entirely**, abandoning the integrated server's
  queued writes, final checkpoint, world lease and listening socket. Teardown now runs from a
  `finally`, with the host disposal nested under the service disposal so a throw out of
  `_service.DisposeAsync()` cannot cost the drain or the checkpoint. Bounded at 10 s; the fault is
  logged before teardown so the trace survives. The server layer still propagates the fault -
  making `TickLoopAsync` swallow it would turn a simulation crash into exit 0, and
  `ServerTickMetricsTests.cs:235` pins that throw.
- The dedicated server handled only Ctrl+C, so **SIGTERM and a closed console window skipped every
  `using`** and lost the whole write queue and final checkpoint. `OrderlyShutdownSignals` now routes
  `CancelKeyPress`, `PosixSignalRegistration` (SIGTERM/SIGINT/SIGQUIT/SIGHUP) and `ProcessExit` into
  one cancellation; only the first signal starts a shutdown, and a handler blocks at most 4 s so it
  stays inside Windows' console-close allowance.
- A start that failed after the runtime host existed wrote a final checkpoint over a world never
  served; `WriteWorldTime` is a plain UPDATE with no monotonic guard, so a clock of 0 overwrote the
  stored world time.
- `RemoteClientSession.DisposeAsync`'s waits are now bounded (5 s), because
  `PlayScene.DisposeRemoteClient` joins it on the render thread. The packet's claimed root cause
  ("cancellation cannot abort the receive") was **rejected empirically**: a probe showed the pending
  `ReadAsync` ends ~0 ms after `Cancel()` on .NET 10/Windows.

### Items as durable value

No duplication or unaccounted loss is reachable: the `ux_player_inventory_slots_item` UNIQUE index
on (world_id, item_instance_id) prevents cross-row aliasing, the `item_instances` insert trigger
plus `LoadNextObjectId` healing prevent id reuse, and no stacks exist. Two Low defects fixed: a
layout repair could strand an overflow row at an index the service treats as inert (now bounded and
counted as `stranded=N`), and `StarterKitVersion` was installed unclamped while its sibling `Gold`
was clamped.

Half of that second finding was **rejected**: clamping does not make the kit arrive, and making it
arrive would require treating the row as unrecorded, which repeats on every load because
`UpsertStarterGrantSql` is raise-only (`WHERE kit_version < excluded.kit_version`). Since starter
items sell for 2-14 gold, that would let a tampered row mint gold per relog. Durably repairing such
a row needs `SqliteInventoryRepository` to accept a lowering write - a deliberate security
trade-off, left for an owner decision.

### Recurring shape worth knowing

Three independent instances this session of **one field of a persisted row used unclamped while its
siblings on the same row are clamped**: `max_health`, `movement_speed`, `StarterKitVersion`. Clamping
is applied per field at the call site rather than by the row's loader, so a new field does not
inherit its neighbours' bounds, and the bad value is only reachable from a corrupt, tampered or
old-scale row.

### Wave 8 - using the skipped load tests as a map

Of the 47 `[LoadFact]` tests, 33 assert a timing budget and **14 assert a correctness invariant** -
so that behaviour has no coverage in a normal run. Reading the production code those 14 cover found:

- `PlayerInventoryService.Evict` leaked one `_inventoryRevisions` and one `_questRevisions` entry per
  departed character, forever - a real leak on a long-lived server. The other eight per-character
  dictionaries in that class were audited and are clean; only these two leaked. The `Retained` path is
  deliberately left alone, since it keeps the inventory loaded on purpose.
- `EnemyFactory.Restore` used the persisted **position** and **facing** unclamped. Position is now
  sanitised in `RuntimePersistenceBridge.RestoreEnemies`, where map and home are in scope - finiteness
  checked before `Math.Clamp`, the same bound as `EnemyRegistry.ClampToMapBounds`, non-finite falling
  back to the resolved home, and clamped rows logged. `FaceDirection` only rejected a near-zero vector,
  so a NaN normalised to a NaN forward; also guarded.
- The `Migration0008` load test asserted `ToVersion == 10` while 16 migrations exist, so its
  row-preservation gate was dead even with `DREAM_LOAD_TEST=1`. It now reads the version from the
  loaded set and covers v7 -> v16 across 100,003 item rows with equal before/after counts. No product
  bug; no migration file touched.

**Field-audit lesson.** Position was the third unclamped field found in `EnemyFactory.Restore` after
`max_health` and `movement_speed`. Auditing *every* field the method reads then found a fourth
(`Facing`) that nobody had reported. On a second instance in one method, audit the whole method rather
than fixing one field per wave. Severity calibration: `Microsoft.Data.Sqlite` cannot bind a non-finite
REAL, so NaN reaches a row only via hand-edit, raw SQL or import - never the game's own writer.

Suite after this wave: **8953 passed, 0 failed, 47 skipped** - a third consecutive clean full run.

## Waves 9-13

Suite after these: **9026 passed, 1 failed, 47 skipped** - the one failure is a load-sensitive
loopback test that passes 3/3 in isolation and is under repair; see "Test reliability" below.

### Trust boundary (wave 9) - mostly a positive result

A systematic sweep of **31 client-reachable command/payload kinds** found **29 held, 2 holes**, with
24 hypotheses disproved and the holding code cited for each. Every player command takes its acting
identity from the connection's joined session, so there is no second instance of the player-cap
display-name shape. Both holes were missing throttles, now fixed: `trade.request` had no cooldown
(a request/cancel loop could flood a named victim; now a 2 s cooldown on an *unanswered* request,
mirroring `PartyRegistry._lastInviteTick`), and a command frame alternating `RequestedMode` every
tick queued one profile write per tick per client (now paced at one per second, last mode of a burst
still written). Note: "only enqueue when the value changes" was already present and is NOT
sufficient - an alternating client changes the value every tick by construction.

### Deferred authorisation (wave 10)

The command sweep missed a real authorisation hole because it lives in a **deferred** action: a party
invite is authorised when SENT, and `PartyRegistry.Respond` never re-checked that the inviter was
still the leader, so a non-leader could admit a member to someone else's party. Re-checked at the
moment of effect now. **The general lesson: "the command checked identity at entry" and "the action
is still legitimate when it lands" are different questions.** Anything created now and applied later
needs the second check.

Also fixed: a trade's only state message could be dropped client-side and was never re-sent, leaving
a player stuck (server now re-sends live trade state on a 32-tick cadence); the detach hook purged a
session's queued messages without decrementing their counts, so the per-session cap ratcheted down
over reconnects; a pending invite survived its invitee joining a party; `trade.request` had no mute
check while `party.invite` did.

### Animation, audio, world mutation (waves 11-12)

- **Every enemy's `die` clip was unreachable** - the renderer dropped the actor on the frame its
  health hit zero. Fixed with a render-only `EnemyDeathLinger`: a frozen matrix plus the enemy's own
  pooled instance, no actor, no visual state, no interpolation buffer, bounded by the clip length and
  a 3 s cap, released immediately on a respawn under a reused id. The packet's suggested fix (keep
  dead enemies in `SelectActiveEnemies`) was REJECTED because `PresentationFramePoolingIntegrationTests`
  pins that a locally-killed enemy leaves `ActiveEnemies`.
- An attack one-shot froze the legs mid-stride, because the attack clips animate no leg node and the
  pose only writes the playing clip's nodes.
- **Cube identity was keyed on the cell**, which is not a durable key: placing a cube in the cell a
  *moved* cube was last persisted at inherited that cube's `ObjectId`, owner, health and actor id
  (so build protection protected the wrong player), and a placement over a restore-skipped orphan row
  inherited its owner and damaged health. **One guard fixed both**: the cell now only finds a candidate
  row, and the row is adopted only if its `ObjectId`-to-actor binding says it describes *this* cube.
  The lost-registry-link repair the lookup exists for still works, and `RepairCubeRecord` still runs
  before `CanDamageCube`.
- A join/resync destroyed a cube built over unmoved rubble, because the snapshot's fallback fracture
  removed whatever cube was in the cell while the same snapshot listed a cube there. Snapshot path
  fixed; the delta path deliberately still fractures, and the asymmetry is documented in the code.

### Quests (wave 13)

18 hypotheses disproved, including valuable negatives: the turn-in already re-validates every
objective, a same-session double turn-in is guarded, the reward is all-or-nothing in one savepoint,
kill counts cannot exceed/negate/double-count, and every `PersistedQuest` field is clamped.

Fixed: **XP could be collected repeatedly** - it was a separate write queued after the turn-in
closure, so a failed turn-in paid the XP durably and left the quest turn-in-able after a restart (the
XP write now runs inside the same closure, asked for only after every guard passes). A dialogue
re-open between press and release fired the action against a **different quest** - the button id now
binds offer state + NPC + quest key, and a mid-press identity change drops the click. A giver with
more than eight quests silently lost the rest **and the shipped content already has 7 of 8 on one
NPC**, so the loader now fails at load. Quest rows the catalog no longer knows counted against the
cap while being invisible and unabandonable. A reward item was resolved through `ContentCatalog.Shipped`
while the quest came from `EnemyContent`.

**Consequence to note:** with the per-giver cap enforced and two givers in `npcs.json`, the authorable
total is 16. `MaxQuests` (32) remains the total bound; reaching it needs more givers or a scrolling
dialogue window. `QuestContentTests` asserted that 32 quests load when all 32 name one giver - exactly
the silently-broken state the validation prevents - so that assertion was replaced by two stronger
ones (the per-giver cap is rejected when exceeded; quests spread across both givers load). The test's
named intent, the total-cap rejection, is unchanged.

**Residual, not fixed:** the repeated-XP-after-restart path is closed, but the XP is still applied to
the in-memory combatant, so a later periodic checkpoint could persist it after a failed turn-in.
Closing that needs the failed-write gate to roll the combatant back.

### Test reliability - four flake root causes found and fixed

This repo's "flakes under a loaded suite" reputation had four distinct causes, none of them a game bug:

1. **239 process-wide `SqliteConnection.ClearAllPools()`** calls in 94 parallel-phase files, tearing
   down connection pools belonging to concurrently running collections. Now `SqlitePools.Release(path)`,
   with a guard test. Two call sites were releasing the wrong database.
2. **Mid-test `PersistenceWriteQueue.Flush(30 s)`** blocks the calling thread and starves neighbouring
   test classes (3/3 reproducible). 62 call sites became an awaited barrier with the same guarantee
   Flush gives; teardown flushes deliberately left blocking.
3. **The server's 10 s idle close** - the dominant loopback cause. A real client keepalives an empty
   frame every second; `WireLoopbackClient` sends nothing while waiting, so any wait outlasting 10 s
   had its own connection closed underneath it. Evidence: `M8.Reconnect_ADroppedConnectionIsRetryable_AKickIsNot`,
   which had resisted every other fix and failed at gate 64 on the base too, is the same cause and now
   passes - first clean gate-64 run of the 69-test loopback filter.
4. **Exact-0-byte allocation gates measured a single window** against a JIT tier-up threshold of 30.
   Fixed by warm-up plus steady-state re-measurement across ~94 sites; **the bound is still exactly
   zero** - no tolerance, no skip, no retry. Isolation was evaluated and rejected on evidence:
   `ClassicHudGemAllocationTests` was already alone in its own collection and still flaked, so
   isolation does not make an invalid measurement valid. No product path was found to allocate.
   Proof: a hostile short harness (CPU hogs + 64 collections) failed 4 of 6 reps before and 0 of 14 after.

Six tests that asserted nothing were also repaired (one compared an enum constant to itself), and
`Admin_Mute_...` was strengthened to assert the `muted_until_utc` row directly and prove the row does
the silencing by restarting the server. All 47 skipped tests were confirmed as deliberate
`DREAM_LOAD_TEST` opt-in gates - nothing is silently disabled.
