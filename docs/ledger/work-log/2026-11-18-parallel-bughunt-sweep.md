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
