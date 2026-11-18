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

## Wave 14 and the remaining flake work

Suite: **9031 passed, 0 failed, 47 skipped**.

### Two more flake causes, both real

5. **A fixture flag missed on one test.** `TerrainLockstepRestartTests` constructed
   `PersistentScriptedServer` without `allowOccupiedBuildCell`, so an occupied cell in a later phase
   fired `Assert.Null` from the fixture's constructor and read as a determinism divergence on the
   repo's determinism guarantee test. The script builds a cube at tick 3 and can only hit it on ticks
   130/158/186/214 (28-tick cooldown), so at most 3 hits of 25-50 damage land on a 100 hp cube inside
   a 200-tick phase - for some seeds it survives into the next phase. **The product is deterministic:
   phases 1 and 2 produced identical hit sequences from the same seed.** The identical twin test
   `WaveCTesterLockstepTwoRestartsTests` was given this flag in 2de0a96c; this one was missed. The
   guard now says "fixture:" so a setup failure can never again be read as a divergence.
   Method note: 20 reps could not catch it. The real rate was measured at 1 occupied phase start in
   75, and **seed** discriminates, not load - forcing the bad seed reproduced it on every run.
6. **Two genuine races, found by the deadline diagnostic rather than by any load harness.**
   - The unmute line was sent as soon as the ADMIN saw "Unmuted Bee", while whether it is routed or
     swallowed depends on BEE's standing; the reply and the `AccountUpdate` are enqueued by the same
     barrier read but travel different paths. Lose the race and the line reaches nobody.
   - The mute case is the same shape: `/mute`'s barrier read runs on the persistence worker,
     `_pendingAccountUpdates` drains at the START of a tick (`DedicatedServerHost.cs:1315`) while
     `_pendingConnectionReplies` drains AFTER `AdvanceTime` (`DedicatedServerService.cs:611`), and
     chat is routed on the receive path. A read landing between them sends the admin's reply on tick
     T and applies the standing on T+1. The test asserted an ordering the product does not promise;
     it now waits for the mute to be in force on the muted player's own connection, which is an ADDED
     assertion rather than a relaxed one.

Along the way the deadline harness itself was rebuilt: `UntilAsync` now spends a budget only once
EVERY side it depends on has done the work (server ticks plus the client frames / applied messages /
settled writes that the condition actually reads from), so a starved side holds the wait open while a
healthy-but-stuck one fails fast. The diagnostic names every side and its progress; the earlier
message could claim "not a loaded machine" without having measured the side that starves, and that
claim cost a round of investigation. Budget values were not raised (1024 write-round-trip, 512
applied); only the counters they are spent on changed. Worst-case failure cost for the worst
offender: 180 s -> 8 s.

**Where the load harnesses could not discriminate, semantics were pinned directly:**
`LoopbackDeadlineTests` asserts the budget is not spent while any side has made no progress, is spent
once every side has, and that the message names every side. Flipping back to one-counter semantics
fails exactly 2 of those 4 - catching a bug three separate load harnesses could not.

### Presence lease (wave 14)

The packet corrected the premise: `WorldTransferProtocol` is the chunked world-STATE stream to a
client; there is no player-between-worlds flow. The real cross-world handover is the character
presence lease. **Items cannot cross worlds** - every inventory table is keyed `(world_id, ...)` and
every statement binds `$worldId` - but **`player_profiles` is global**, which is what makes the lease
findings durable-value findings. 15 hypotheses disproved.

Findings taken: the lease is renewed on a tick counter but expires on wall-clock, and the renewal's
result is discarded, so a stalled server silently loses its lease and one character can be played in
two worlds, both writing the one global profile row; a join whose connection drops before the tick
applies it leaves the lease claimed, locking the character out of every world for 45 s; a join
refused for a ban set after its prefetch started leaks the same lease; a prefetch-timeout rejection
discards the prefetch while its load is still in flight, so the lease it later claims is unreachable
by any release path.

### Product detail reported, not papered over

The admin's "Muted X." reply can precede enforcement by up to one tick (~15-31 ms at 32-64 Hz), and a
message sent inside that window is still published. Nothing is lost - the row is written, the standing
applies on the next tick, the mute survives rejoin and restart - so the test was the thing that was
wrong. But for a moderation feature the confirmation is the product: an admin told "muted" expects the
next message to be refused. Making the reply follow enforcement is a change in
`QueueSanction`/`StepAccountStandings`.

## Waves 15-16, and the last of the test-reliability work

Suite: **9070 passed, 0 failed, 47 skipped**.

### Moderation confirmation now follows enforcement

`/mute`'s barrier read runs on the persistence worker and enqueued the `AccountUpdate` and the admin's
"Muted X." reply on two different queues: account updates drain in `StepAccountStandings`, the first
statement of the tick (`DedicatedServerHost.cs:1314`), while connection replies drain after
`AdvanceTime` (`:690`). Chat is decided on the receive path via `IsMuted`. A barrier read landing
between the two drain points told the admin the mute was in place a tick before it reached the target,
and **a message sent inside that window was still published**. Reverting the fix demonstrates it: the
griefer's line "one more word out of me" reaches the admin AFTER the admin has been told "Muted."

`AccountUpdate` now carries the confirmation, and `ApplyAccountUpdate` enqueues it last and
unconditionally, so the reply is emitted from `StepAccountStandings` once the standing is in force.
Failure cases (no such account, guard refused, write failed, queue closed) still reply on the direct
path, so the admin can never be left uninformed. `ban`, `unban`, `mute` and `unmute` all share
`QueueSanction` and are all fixed by it; `kick` does not have the shape (it replies inline on the
receive path) and was left alone. No tick-thread wait and no database read on the tick.

### Economy: a corrected premise and a hardened subsystem

The premise this wave started from was wrong, and the packet says so: **gold is not on
`player_profiles`.** It lives in `player_wallets (world_id, player_name, gold, updated_utc)`
(migration 0012), per world, and the only durable writer in the repo is the relative statement
`ApplyWalletDeltaSql` (`gold = player_wallets.gold + $delta`). The stale-absolute-write and
cross-world angles do not reach gold at all.

20 hypotheses disproved, including the buy-sell money pump (blocked at content load), double
quest-reward payment (`CompleteQuest` is `WHERE state = 1` and throws unless exactly one row changed),
vendor stock (there is none; supply is definitional) and overflow (every check is
`amount > MaxGold - current`). Two Low findings remain unfixed and are information divergence rather
than value loss: a wallet row above `MaxGold` is capped in memory forever with no diagnostic, and
`TryAddGold` is the one wallet mutator without a `HasSeenFailedWrite` gate.

### Death credit is exactly-once - with one hole, now closed

**`WasFatal` cannot fire twice for one death.** It is an EDGE
(`previousHealth > 0 && CurrentHealth == 0`, `HealthState.cs:31`), every enemy damage path funnels
through one `EnemyCombatant.ApplyDamage` on the single-threaded tick, and enemy actor ids are never
reused. Kill gold, XP and loot are therefore exactly-once; 16 hypotheses were disproved with the
specific guard that kills each.

The hole was next door: `RecordEnemyKill` **deduped by session id while crediting by character key**
(`WorldSessionRuntime.Quests.cs:185` vs `:207`), so one death advanced a kill objective twice when two
live seats shared a `CharacterKey`. The credit loop now dedupes on the resolved character key.

**Two live seats CAN share a `CharacterKey`** - proved by a test driving the real
`RuntimeAuthoritativeSessionHost.JoinSession`, not by reading: a reconnect arriving after a second
session took the name leaves both players spawned with equal keys, because the duplicate-name gate
(`:224`) sits below the existing-session branch (`:166`). Closing THAT is a policy decision and is
left open: every shape of it either refuses a legitimate reconnect or evicts a player already
playing, and the existing-session branch IS the reconnect path. The presence lease now prevents the
cross-world case; this is the same-world case it does not cover.

Also fixed: a persisted enemy row with `current_health = 0` restored dead, was pruned with no credit
and no row delete, and leaked one enemy actor id on every restart (checkpoints skip `!IsActive`, so it
never self-healed). The restore now clamps current health to at least 1, so the anomalous row becomes
a live enemy that the next checkpoint rewrites and a kill deletes - self-healing, and without putting
a database write on a per-tick registry call. The schema permits it: `enemy_instances`' CHECK is only
`current_hp >= 0`.

### The last allocation flake was a drifting player, not a flip

`CubeRestFreezerTests.Tick_WithAFrozenCarpetAndAWalkingPlayer_AllocatesNothing` had been classified as
"already correct" because it already used `AllocationAssert.AtMost(0, ...)`. It was not. The measured
body advanced simulation state and flipped direction on a tick counter, while the helper runs a
load-dependent number of iterations - so machine speed decided which ticks were measured.

The actual mechanism: over the helper's ~12,000-tick span the player **drifted off the carpet**
(the gait ramp is not symmetric between directions), reached the edge at tick ~4268, woke 38 cubes,
fell to the floor, and from there every tick allocated on the AWAKE-cube path. Only the early clean
rounds held the minimum at zero. The frozen path - what the test is named for - allocates **0 B**,
verified across ticks 64..4267, identical idle and under 16-core load.

The walk is now turned by the player's own position rather than a tick counter, verified stable over
60,000 ticks with 0 cubes ever woken, and the test additionally asserts it turned, stayed in band,
stayed on the carpet and woke nothing - so a future drift fails loudly instead of silently changing
what is measured.

**Product observation, not a defect:** once cubes are awake, `PhysicsSyncSystem` allocates 160-680 B
per tick (peak 2976 B) - boxing `(Cube, position)` into `Body.UserObject` per moved body
(`PhysicsSyncSystem.cs:177`) plus `movedCubes.ToArray()` (`:222`). That is the documented wake path,
but it is a per-tick allocation on the authoritative loop that nothing asserts against.

## Waves 17-19

Suite: **9135 passed, 0 failed, 47 skipped** - and that run happened while a sibling agent was
saturating all 16 cores with a hostile load harness, which is exactly the condition that used to
produce ~35 spurious failures and testhost hangs.

### PvP and spawn protection (wave 17)

**The PvP gate itself holds**: all 4 client-reachable player-damage paths go through
`WorldSessionRuntime.FillPvpTargets` (`Combat.cs:147-172`), enemies through `FillEnemyTargets`
(`Respawn.cs:126-142`), both applying `IsSpawnProtected` (`Respawn.cs:79-82`), and outgoing damage
while protected is blocked (`Commands.cs:228-234`, `Skills.cs:59-63`). 14 hypotheses disproved.

Two exploitable holes found and closed:

- **Spawn protection could be re-armed at will by re-joining, with zero downtime, mid-fight.** It is
  now keyed on the CHARACTER (`_characterSpawnProtection`, tick-based, reusing
  `JoinHoldReattachCooldownTicks`): a character that spent its window by ATTACKING gets nothing back
  for rejoining inside the cooldown, and otherwise a join may start a new window only once the
  cooldown has run since the last join grant - in between it resumes the remainder. Attack ->
  reconnect -> attack is dead, and a non-attacker cannot re-arm faster than the window expires.
  Because the record is the character's, the known two-live-seats case hits the same record and
  resumes the same window rather than defeating it.
  **An existing test contradicted the packet's simpler "always resume" shape** -
  `WolfBiteAdversarialTests` pins that a rejoin gets its own full window - so the rate limit was
  scoped to join-granted windows and attacks, and a respawn grant clears the join brake. The test was
  not edited and still passes.
- **One player could permanently seal the single respawn point with build-protected cubes** - the
  victim could not clear them, because the cubes were protected from them. `ResolveRespawnPosition`
  now runs a bounded, map-only BFS (radius 8 cells, +/-3 of climb) and steps to the nearest free cell
  outside the sealed region. No wall is damaged, no cube ownership is read, and build protection is
  untouched. The "no-build zone around spawn" alternative was rejected on evidence: players start at
  the world spawn, so any radius large enough to matter refuses nearly every build in ~35 existing
  test files.
  **Honest residual:** a griefer can still enclose someone with a much larger structure outside the
  search radius. That is the generic "box anyone anywhere" problem, not the spawn-point invariant;
  no bounded deterministic search closes it, and the fixes that would (letting players break
  protected cubes, or a teleport/unstick command) each trade one griefing vector for another. Left
  for whoever owns the build spec.

### Content validation generalised (wave 18)

The per-giver quest cap fixed earlier was an instance of a class, so this wave built a
**validation-vs-runtime table** for every authored collection. **7 gaps, 6 findings** - and the first
is the same bug in vendors: `MaxNpcStock = 32` at `ContentCatalog.cs:935`, but `VendorWindow.cs:57`
draws `BuyRows = 8` with no pager and `VendorHudState.cs:64` caps at 16, so a vendor's 9th entry
loads cleanly and is **unbuyable**. Shipped content sits at **6 of 8** - the same near-miss margin the
quests had.

Fixed by validating at load against the tightest runtime bound, matching the quest-cap precedent.
New authorable maxima, all now failing loudly at load instead of silently dropping: vendor stock 8
per NPC; `use.cooldownGroup` 64 chars via `ReadKey` (past 64, `QuickbarHudState.cs:87` silently
dropped the cooldown so the tile showed ready and every click was refused); quest and item
`description` 400; item `detailLines` 8. Also: `ContentDigest` now includes the cooldown group (it
gates consumable use, and two catalogs with different cooldown pools hashed identically - the digest
is logged at startup and compared run-to-run in tests only, never persisted or sent on the wire), and
NPC key dedupe moved from `Ordinal` to `OrdinalIgnoreCase`, since `_questsByGiver` and the giver match
already folded case and two case-variant NPCs each offered the other's quests.

Shipped content is unchanged and still loads: stock 6/8, longest description 202/400, detail lines
3/8.

**Worldgen came back clean** - there is no RNG in the generation path at all,
`NaturalTerrainMaterialPolicy` is explicitly seed-independent with fixed sub-seeds, and there is no
`string.GetHashCode` / `DateTime` / `Guid` / parallelism anywhere in it.

**A deferral worth recording:** the camp-restore Shipped-vs-EnemyContent finding was implemented, and
doing so turned `CampBandTests` red - which exposed the real root cause. `RestoreWorld` runs inside
the `WorldSessionRuntime` constructor (`WorldSessionRuntime.cs:187`), so `EnemyContent` is still the
default at restore time; the mechanical fix would have made restore read a camp-less catalog, which
is worse than the bug. Reverted cleanly. A real fix resolves the camp key at the first camp fill, or
takes the catalog as a constructor argument.

### Skills (wave 19)

**4 of the 5 cast paths already re-validate the target at effect time**; only SelfHeal did not.

- **A reconnect wiped every running cooldown** while mana, health and items persisted - rejoin as a
  free reset, the same family as the spawn-protection exploit above. Cooldowns are now recorded per
  `CharacterKey` on start and resumed on attach instead of cleared, so a second live seat on the same
  character inherits the cooldowns the first seat is serving and a new session id cannot fetch a clean
  bar. Expired entries are dropped at restore, so a bad connection owes only what it already owed.
- **A SelfHeal at full health spent mana and the full cooldown for zero effect.** Now refused before
  the spend. The boundary matters and is tested: a heal that mostly overheals (damaged by 1 against a
  35-point heal) is still ACCEPTED, still heals 1, still spends 15 mana and still starts the cooldown
  - only a strictly zero-effect cast is refused.
  This needed an existing test changed: `SkillsLoopbackTests.AttackSkills_EndSpawnProtection_ButMendDoesNot`
  cast mend at FULL health and asserted acceptance. Its named intent is that a NON-ATTACK skill does
  not end spawn protection, and the full-health cast was incidental setup - so the caster is now
  damaged first, which makes it a real mend cast and strengthens the test rather than weakening it.
  Its spawn-protection assertions are untouched.
- Skill keys and item cooldown groups shared one unvalidated namespace, so a collision let an item's
  cooldown gate a skill. Now a load failure.

**One finding rejected with evidence**: a skill cast and a command-synthesized primary attack in the
same tick drawing the identical damage roll is **intended determinism**, not a defect. The seed is a
pure function of (sim seed, tick, session, action sequence, target), pinned from both sides by
`SkillResolutionTests.cs:197-201` and `:292-295`, and the two sequence spaces are kept disjoint on the
client. No seeding change was made.
