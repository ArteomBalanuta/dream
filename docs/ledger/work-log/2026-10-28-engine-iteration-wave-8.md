# Engine Iteration Wave 8: Persistence Throughput, Liveness, Per-Tick Commands, Jitter Buffer, Animation Instances, Active-Body Physics, Frustum Culling

Purpose: Record wave 8 of `docs/ledger/plans/2026-10-28-engine-iteration-round-2-plan.md`, together with its fix loop. NETR-5 was moved up from wave 9 to sit with NETR-4.

Status: Integrated on `integrate/engine-iteration`. Verified by automated tests, Release load runs and a 2-client perf-harness run. Not pushed and not merged into `master`. Continuous mode.

## Process

1. Seven implementers worked in parallel from `4957632`: SCALE-3, NETR-1, NETR-4, NETR-5, GPU-4, SCALE-5 and GPU-2. All seven merges were clean, ending at `e4c6930`.
2. Verification:
   - The reviewer reported 1 Must fix and 11 Should fix items.
   - The tester added 8 test files and found 7 issues. The most important was the root cause of the intermittent native test-host crashes, which predated wave 8.
   - A perf run was done.
3. Fix loop 1 used five implementers in parallel: the command stream, the host/join/persistence path, the service and keepalive, partial-terrain physics, and viewmodel nits.
   - Integration produced `0a4bf47`, with one test-file conflict.
   - The overflow-resync re-arm wiring was added during integration.
4. The re-review found 1 Must fix (the client hung on a join-retry rejection) and 5 Should fix items.
5. Fix loop 2 used three implementers: the client join flow, the inbox and stall path, and persistence lock retry.
   - The merges were clean and gave `490a709`.
   - That is the second and last fix loop the workflow allows.

## Changes

| Task | Change |
| --- | --- |
| SCALE-3 | WAL mode with `synchronous=NORMAL` and `busy_timeout`. Writes are grouped into one transaction with a savepoint each (up to 256 per group). The join prefetch runs on the write worker, behind any pending writes. No database reads happen on the tick at join. The flaky deferred-join test is fixed: the host now has its own `ConnectionCount`. |
| NETR-1 | `ServerConnectionOptions`: idle timeout 10 s; send-stall timeout 5 s plus frame bytes at 256 KB/s. `ServerSnapshotCoalesceBuffer` makes merging linear and gives byte-identical output. Coalescing is capped at 4096 mutations, after which the server forces a resync. Close counters were added to the metrics. |
| NETR-4 | `ClientCommandTickStream`: exactly one command per prediction tick, with the sequence equal to the tick and a catch-up cap of 8. `ClientOutboundCommandQueue`: a 16-slot FIFO that merges on overflow while keeping presses. The reconciler replays unacknowledged ticks on a snap and ignores a repeated ack. |
| NETR-5 | `BufferedPlayerCommandInbox`: a per-session ring of 8, popping one frame per tick. Frames merged on overflow keep every tick they cover. It skips the ticks the runtime already repeated, sheds load elastically, and coalesces fast senders. |
| GPU-4 | `AnimatedModelSkeleton` holds the shared, immutable data; `AnimatedModelInstance` holds per-actor animation state. Each wolf now has its own pose and clip time; before, N wolves played the clip N× too fast. All players share one decode and upload. Joints are uploaded in one array call. Enemy instances are pooled. |
| SCALE-5 | Physics sync now visits only bodies that are active, newly added, or on a rejection streak. `InsertionOrderedSet` gives O(1) removal while keeping list order. The binder gets reverse maps, so fracture removal is O(k). Determinism goldens are unchanged. |
| GPU-2 | `ViewFrustum` (Gribb–Hartmann on OpenTK row vectors) culls static chunk meshes and dynamic cubes. The `VisibleChunks` stat now counts chunks; before, it counted meshes. `DrawCubeTransparent` was deleted. |
| Fix: command stream | The client predicts and sends idle once a send stall is 16 ticks old, matching the server's NETR-3 rule. The inbox counts a sequence jump as N ticks, and its skip debt covers every starved tick. Fast senders are now detected by how far their sequence runs ahead, so 28–40 fps clients are no longer misclassified. A snap target of 6 m or more, or one inside a solid cell, snaps to the server position. Inbox `Remove` resets all state. |
| Fix: host, join, persistence | Shutdown race: the host's `Dispose` takes the authority lock and sets `_disposed`. This was the root cause of the intermittent `removeRigidBody` access violation, the `0x80131506` heap corruption, and World10k's dispose exception. Further changes: messages from a stale connection context are dropped and the command inbox is reset on claim (M1). The join prefetch no longer blocks and uses `TryGet`; a join is deferred for up to 30 s, retried once, then rejected with a retry reason. Reads end a group, so they see only committed rows. `RequestWorldResyncIfConnected` creates no ghost contexts. Persistence shutdown is bounded. A closed queue refuses new work. `DefaultTimeout=5`. The WAL file is capped at 64 MB. |
| Fix: service, keepalive | The service tracks its connection loops and waits for them on stop or dispose. The client sends a keepalive (an empty frame) when nothing has been sent for 1 s. `DisconnectReason` is now set on server close. The integrated local server has liveness checks turned off. A refused overflow resync is re-armed, with 0 B allocated. |
| Fix: partial terrain | An occupied partial static cell gets a static full-cell support box that collides only with dynamic map cubes. Before, a cube resting on a sculpture or mound sliver was rejected by the map and reset every 8 ticks forever, which kept its whole island awake. |
| Fix 2: client join | The client handles the join-retry rejection: it resends after 1, 2 and 4 s, up to 3 times. Any other rejection, running out of retries, or no join within 45 s ends the session with a reason (`DisconnectReason`, `PlayScene.SessionEndReason`). `_unrestoredPlayerNames` is now keyed by session id, and keepalive send signals are bounded. |
| Fix 2: inbox and stalls | Merges never fold a press or mode frame into an older one, so a press pops at its own tick. Stale skip debt is cleared when a frame does not match, or after 8 pops with no starvation. The 4-tick tolerance applies only after a sequence-jump frame. The client also predicts idle from the ack lag: its baseline plus 16 ticks. This catches network stalls that the socket buffer hides from the send queue. Merged slots are capped, and the inbox counts fast-sender entries. |
| Fix 2: persistence | When `BEGIN` fails because the database is locked, the group's unrun writes go back to the head of the queue, in order, and the queue backs off (100 ms doubling to 2 s). Writes are dropped and counted only after the dispose deadline. |
| Fix: viewmodel and nits | The viewmodel shows the clip pose on the first frame after a swap. Other changes: the `RemoveAll` predicate is cached, `InsertionOrderedSet` gets a version stamp, the binder removal test was made robust, and the coalesce-overflow test was made deterministic. |

## Measured Impact

| Measurement | Before | After |
| --- | --- | --- |
| Persistence write burst (1000 writes) | 424–630 writes/s | 26,675–45,322 writes/s, in 4 transactions |
| Join tick with a 150-write backlog | 312–793 ms mean, up to 1,007 ms | 15–16 ms mean, 29–31 ms max |
| Players32 join mean | 1,022–1,153 ms | 339–406 ms |
| 1 s client send stall: correction | 4.53 m snap | 0.000 m |
| 1 s client frame stall: correction | 0.83 m | 0.43–0.83 m, no snap |
| 1 s network stall (socket buffer hides it): correction | 4.41 m snap (model) | 0–0.19 m, no snap |
| Spawn camera: static meshes drawn | 76 (86,780 tris) | 6 (1,222 tris) |
| GPU world time (perf run) | 0.15 ms | 0.05 ms |
| Uniform uploads per frame | 1,198 | 258 |
| 2,400-body seed layout, no players: active bodies and per-tick cost | 1,996 bodies, 8–20 ms | 0 bodies, 0.40–0.48 ms |
| Native test-host crashes | Intermittent; about 1 in 7 load runs | 0 in 4 full suites, 3 Smoke8 runs and 3 World10k runs |

## Perf Harness

Run `logs/perf-run-20261028-094417`, on `0a4bf47`. A repeat on `490a709` (`logs/perf-run-20261028-102942`) gave the same picture: frames 6.95 ms, total CPU 0.5 ms, `MaxAdvanceMs` 16.9.
- Both clients held 144 FPS: frames took 6.94–6.95 ms after startup. `MaxAdvanceMs` was 14.93.
- There were no disconnects other than the harness's own cleanup, and no idle timeouts.
- Each client logged one keepalive after joining.

Release load runs, on `0a4bf47`:
- Smoke8, 3 runs:
  - Loop: p50 18.3–19.2 ms, p99 22.3–23.4 ms.
  - Advance per tick: p50 9.3–9.7 ms.
  - 2 ticks per loop iteration, 0 dropped ticks, joins 235–243 ms.
- World10k, 3 runs:
  - Advance per tick 83 ms, 12.3 ticks/s.
  - No crashes.
  - Unchanged from wave 7; see Known Limits.

## Verification

- `dotnet build dream/dream.csproj` finishes with 0 errors and 13 warnings.
- `dotnet test dream.Tests/dream.Tests.csproj` on `490a709` passed 2,109 tests with 6 skipped (load and measurement tests that are gated), in 3 out of 3 runs, about 77 s each.
- The tester's checks covered:
  - a combined NETR-4 × NETR-5 stall model;
  - the frustum near plane in 6 directions;
  - pooled wolf instances across a model reload;
  - crash consistency when a write group is killed midway (integrity "ok", committed rows only);
  - a prefetch queued behind 1,000 pending writes (41 ms).
- Not verified: interactive play, and the look of cubes resting on partial cells.

## Known Limits And Follow-ups

- **Server CPU with players present.** Player bodies never deactivate. In Bullet, any player touching a cube carpet keeps that whole island awake, so Smoke8 still costs about 9.7 ms per tick. Without players the same layout sleeps at 0.4 ms. The fix belongs in `Player.cs`: stop player bodies from merging islands, or make them kinematic. This is the next scale lever.
- **World10k cannot keep real time.** It runs at about 83 ms per tick and 12 ticks/s, because it never stays still long enough to sleep. Joins depend on SCALE-1 (chunked world transfer).
- **Visual change.** A cube on a partial terrain cell now rests on top of the cell. It can appear up to 0.85 m above a thin sculpture sliver. This matches the map's one-cube-per-cell rule; changing it is a design decision.
- **Test suite time** rose from about 35 s to about 77 s with about 290 new tests, including loopback stall tests. ARCH-1 should profile the suite again.
- **Session end.** A session that ends (disconnect, or a failed or timed-out join) only shows its reason; `Game` has no path back to the menu yet (NETR-2, wave 9).
- **The fix loop limit is reached.** No further review ran after fix loop 2, so the next wave's reviewer should also look at `490a709`'s fix-loop-2 changes.
- **GL debug output** reports the driver's "shader recompiled" performance notices as render faults. Filter out performance-type messages.
- **Still open from earlier waves:**
  - ImageSharp advisories and `maxParallelThreads` (user decisions).
  - The noclip/freeze gate (user decision).
  - Terrain-cube ids reach clients only in full snapshots.
  - The id ranges for placed cubes and enemies can overlap.
