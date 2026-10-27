# Movement Jitter And Push Freeze

Purpose: Record the investigation into two reported symptoms. First, the local player jitters and stutters at high movement speed. Second, pushing some cubes "freezes", then resolves with the player and cube in a different location.

Status: Analysis only. No code changed. Follow-up: section 1 (checkpoint stall and catch-up cap) was addressed in `work-log/2026-10-27-checkpoint-off-tick-and-catch-up-cap.md`. Section 2 (hard snap) was addressed in `work-log/2026-10-27-client-prediction-reconciliation.md`. The fix also found a second cause: the client predicted with its local `NoclipSpeed` (up to 300) while the server always used 18.

Related: `2026-10-27-cube-physics-render-deep-audit.md` (B3 and B6).

## 1. Server freezes for about 0.5 s on every 5-second checkpoint (confirmed from logs)

`DedicatedServerHost.AdvanceTime` runs `Runtime.PersistCheckpoint()` every 5 s inside the tick callback, while holding `_authoritySync` (audit B6). Every saved player, enemy, placed cube and fracture piece opens its own `SqliteConnection` and autocommits.

The existing perf logs show the stall directly. They are sampled once per second, and every `advanceMs` spike over 50 ms falls on a checkpoint tick (tick % 320 == 0 at 64 Hz):

| Log | Tick | advanceMs |
| --- | --- | --- |
| `perf-run-20260705-222533` | 960 | 534 |
| `perf-run-20260705-225859` | 1600 | 575 |
| `perf-run-20260705-230731` | 1280 | 528 |

In `perf-run-20260705-230731`, the snapshot after the stalled tick 1280 jumps to tick 1330. That is about 34 catch-up ticks run back to back, because `SimulationClock.QueueElapsed` has no cap on catch-up ticks.

What the player sees:

- During the stall the server sends no snapshots. Since the duplication fix, client cubes are kinematic and move only by server deltas, so a pushed cube stops dead and blocks the local player, which looks like a freeze.
- When the server resumes, it runs the backlog of ticks with the latest command frame. The player and the cube advance about 0.5 s at once, and the next snapshot teleports both: "unfreezes somewhere in a different location".
- Whether a given push "moves instantly" or "freezes" mostly depends on whether it overlaps a checkpoint. Audit B3 (reject/reset ping-pong when cubes push into occupied cells) adds a smaller, cube-specific version of the same stall.

## 2. The local player is hard-snapped to a stale server position on every snapshot

`ClientCorrectionApplier.ApplySnapshotToRuntime` calls `runtime.Player.SetWorldPosition(actor.Position)` for every snapshot, which runs at about 60 Hz. `PlayerCameraRig.Teleport` sets the body transform and also zeroes linear and angular velocity and clears forces.

The client predicts movement locally: `PlayScene` keeps `MoveIntent` in its local simulation frame. The server position in a snapshot is always at least one server-loop round trip older than the local prediction. The server loop sleeps with `Task.Delay(1)`, which is about 15.6 ms on Windows, and the client sends one command per render frame. So every snapshot pulls the player back by roughly speed × (1–3 ticks):

- walk speed 6 m/s: about 0.1–0.3 m per snapshot;
- noclip speed 150 m/s: about 2–7 m per snapshot.

Snapshot arrival is not aligned with client ticks or frames, so the size of the pull-back varies from frame to frame. Zeroing velocity also cuts jumps and falls short on every snapshot. Together these produce the high-speed jitter and stutter.

`ServerSnapshotEnvelope.LastAcceptedCommandSequences` already tells the client which command the server position corresponds to, but nothing uses it for reconciliation.

## Recommended Fixes

1. **Make the checkpoint cheap and keep it off the tick.** Write all rows in one connection and one transaction per checkpoint. Better still, capture the state under the lock and write it on a background task. Expected effect: the 0.5 s stall becomes milliseconds.
2. **Cap catch-up ticks.** Clamp `SimulationClock`/`ServerTickRunner` to a maximum number of ticks per advance (for example 4–8) and drop the excess time. That turns any future stall into a slowdown instead of a teleport burst. It applies to both the server and the client mirror.
3. **Reconcile the local player instead of snapping.** Record the predicted position for each sent command sequence. When a snapshot arrives, compare the server position with the prediction recorded for the acknowledged sequence. Ignore errors below a small threshold, blend medium errors into the current position over a few frames, and hard-snap only large errors such as teleports or respawns. Don't zero velocity on soft corrections.
4. **Then address audit B3** (two-phase physics-to-map commit) so cubes pushed into each other stop ping-ponging on the server.
