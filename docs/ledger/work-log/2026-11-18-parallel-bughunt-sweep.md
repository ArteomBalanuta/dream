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
