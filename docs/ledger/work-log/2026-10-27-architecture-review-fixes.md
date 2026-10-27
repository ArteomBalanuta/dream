# Architecture Review Fixes (F1–F13)

Purpose: Record the fixes for all 13 findings in `docs/ledger/analysis/2026-10-27-engine-architecture-review.md`.

Status: Implemented and verified by automated tests. Not yet verified in manual multiplayer play or with a new perf run.

## Changes By Finding

- **F1 – every session gets a real server-side player.**
  - `RemoteSessionPlayerState` now wraps a physics `Player`. Its body is registered through the new `PhysicsIntegrator.AddSessionPlayerBody`, and `Player` accepts any registered player body.
  - Secondary commands are recorded before the tick (`RuntimeAuthoritativeSessionHost` reordered). All sessions are simulated before `Physics.StepSimulationFixed` and synced after it. The last command is reused when none arrives.
  - Secondary players now fall, jump, collide, use noclip and push cubes, at a rate independent of how often their commands arrive.
- **F2 – seat lifecycle.**
  - When the primary leaves, its body is parked out of the world (`SetPlayerBodyParked`) and the primary `Player` is not simulated. It is unparked when the seat is filled again.
  - A secondary's body is disposed when it leaves and recreated when it rejoins.
  - A former primary who returns after the seat was taken gets its own secondary seat.
- **F3 – slow clients no longer stall the world.**
  - `ServerConnection` has an ordered outbound queue with its own writer task. Replies, snapshots and action results all go through it.
  - Consecutive unsent snapshots merge (`ServerSnapshotEnvelope.TryCoalesce`) and keep every mutation and damage event. A backlog over 512 messages faults the connection, and the service drops faulted connections.
  - `DedicatedServerService.AdvanceOnceAsync` only enqueues.
- **F4 – Nagle disabled.** `TcpClientTransport` sets `NoDelay` for both accepted and connected sockets.
- **F5 – one non-blocking command per client tick.**
  - `PlayScene` queues the merged command from inside the client tick.
  - `RemoteClientSession` sends through a background writer. Commands collapse into one merged pending frame with one-shot inputs OR-ed, and actions keep their order.
  - Stats and equip actions are queued instead of sent with a blocking call.
- **F6 – no SQLite on the tick during gameplay.**
  - The placed-cube identity repair and the owner-name check use in-memory indexes: `_persistedCubes`, loaded at startup and kept current by saves, deletes and checkpoints, and `_knownProfileNames`. Profile saves use a profile cache filled at join.
  - All immediate writes go through `PersistenceWriteQueue`. It runs in the background on the dedicated server (`CreateDefault`) and inline by default.
  - Join flushes the queue before reading. Runtime dispose flushes it.
- **F7 – client world model extracted.**
  - New `ClientWorldSession` and `IClientWorldState`. `PlayScene` and every client applier use them.
  - The `isReplicatedMirror` flag and its guards are gone from `WorldSessionRuntime`.
  - A structural test keeps client code off the authoritative runtime.
  - Found and fixed while porting: the connected-play use/attack animation never played because the simulation frame strips primary use. It is now driven from the held input.
- **F8 – replication identity, coalescing and resync.**
  - Cube deltas and placed-cube snapshots carry the server `ActorId`, and clients resolve moves and destroys by id first. `CubeDestroyedEvent` carries the id.
  - Same-cell moves are dropped when a later mutation for the same cube follows in the batch.
  - About once a second a `WorldStateChecksum` rides in the snapshot. On a mismatch the client sends `ClientWorldResyncRequestMessage`, and `WorldResyncRequestHandler` replies to that connection with a full world carrying its `MutationSequence`.
  - Rubble is client-cosmetic for players: Bullet `DebrisFilter` versus `CharacterFilter`, with the player contact-test callback filtered the same way.
- **F9 – map consistency.**
  - Rubble lives in its own store (`Map.RubbleCells`, `TryGetRubbleAtCell`), with its own render and collision descriptors and binder bodies (`SyncRubbleBodies`). A fracture removes the cube and releases its cell, so cubes and placement can enter it while old rubble stays.
  - `RestoreFracturePieces` no longer deletes a cube in the origin cell. Previously persistence restore removed legitimately placed cubes this way.
  - `PhysicsSyncSystem` commits in multiple passes, so chain pushes move in one tick. A rejected body keeps its physics position and is reset only after 8 consecutive rejections.
  - Hit rays test real cube bounds through the spatial index.
  - The static chunk queue keeps the dirty chunks of superseded requests and batches, and no longer overwrites a partially applied upload batch.
  - `Map.FindInvariantViolations` checks the map's parallel indexes. Debug builds report violations after any physics sync that moved a cube.
- **F10 – tick pacing.** The server tick loop runs on a dedicated long-running thread and waits with `TickPacer`: 1 ms Windows timer resolution plus a final spin.
- **F11 – titles.** `ChangedTextSink` applies console and window titles only when the text changes. The console position is rounded to 0.1.
- **F12 – integrated server on demand.** `OnDemandLocalServerHandle` starts it on Use Local Server, a loopback initial configuration, or local auto-connect.
  - An explicit connect is never redirected. The first version also rewrote explicit loopback connects in `BeginConnect`. The perf run caught that it hijacked connections to a separately running dedicated server on `127.0.0.1:5057`; that was fixed and is covered by `ExplicitLoopbackConnect_ReachesTheRequestedPortWithoutStartingTheLocalServer`.
- **F13 – receive buffer.** `FrameReceiveBuffer` replaces the `List<byte>` + `ToArray()`-per-frame deframing on both ends.

## Behavior Changes To Watch

- Pushing is resolved by every session's own physics body. A pushed cube's map position can lag its body by up to 8 ticks while a transition is rejected.
- Players no longer collide with or stand on rubble.
- A cube can now be placed into a cell whose cube fractured earlier. The old rubble stays and is persisted under the old cube id.
- The server raises the Windows timer resolution to 1 ms while its tick loop runs.
- Players are told the desktop's integrated server is "not started" until local hosting is chosen.

## Verification

- Final full suite, run after the last code change (the F12 connect fix): 679 passed, 0 failed (619 before this pass). It used the normal build output. Earlier runs, while a `dream.exe` locked `dream\bin`, used `/p:BaseOutputPath=artifacts/verify-bin/` (ignored, and inside the repo, because repo-relative tests locate the root from their output folder).
- Build: `dotnet build dream\dream.csproj` with the same output path succeeded. The remaining warnings are the existing package, nullable and obsolete-GL warnings; none come from this change.
- End-to-end smoke test: `MultiClientLoopbackSmokeTests` runs the real server service with its tick loop, outbound queues and pacing over loopback TCP, with two real client sessions applying snapshots into `ClientWorldSession`. It verifies:
  - both clients join and receive more than 60 snapshots;
  - both players move;
  - the secondary player physically jumps and lands, which the old ghost seat could not;
  - every periodic world checksum matches.
  - Passed 3 of 3 consecutive runs, and as part of the full suite.
  - A first version failed because it triggered the jump by loop count, and the loop ran slower than assumed (about 35 ms per iteration). Separate probes over the runtime and over real TCP confirmed both primary and secondary seats jump correctly, so the jump is now triggered by elapsed time.
- New test files (plus `MultiClientLoopbackSmokeTests`):
  - `OnDemandLocalServerTests`
  - `ChangedTextSinkTests`
  - `RemoteClientSessionOutboundTests`
  - `ServerOutboundQueueTests`
  - `FrameReceiveBufferTests`
  - `TickPacerTests`
  - `PersistenceOffTickTests`
  - `RemoteSessionPlayerStateTests` (rewritten)
  - `ClientWorldBoundaryTests`
  - `WorldReplicationIdentityTests`
  - `MapTransactionTests`
- The NoDelay test was added to `TcpServerTransportTests`.
- Existing tests updated for intended contract changes:
  - rubble is looked up with `TryGetRubbleAtCell`, and a fractured cell is released;
  - a rejected sync resets only after the streak;
  - outbound sends are flushed before assertions;
  - the mirror tests use `ClientWorldSession`.
- **Perf run with real desktop clients.** Harness: `skills/profiling-dream-multiplayer`, one dedicated server plus two auto-connecting desktop clients for 30 s, logs in `logs/perf-run-20261027-201558`. Run with `-NoStopExisting`, so no user process was killed.
  - **First attempt** (`logs/perf-run-20261027-201427`): both clients joined their own on-demand integrated servers (`players=1` each) instead of the dedicated server. That is the F12 regression above; after the fix, 679 tests pass and the run was repeated.
  - **Sessions:** both clients connected to `127.0.0.1:5057`. The server reports `players=2` (98 lines), and each client rendered the other player (`client.render.remote visible>0`: 32 and 27 lines).
  - **Server tick pacing (F10):** 37 of 37 sampled loops ran exactly one tick. Before, loops alternated between 0 and 2 ticks.
  - **Server tick cost (F6/checkpoint):** `advanceMs` was 0.2–0.5 ms, with one 17.8 ms sample right after the first client joined. Before, ticks took 528–575 ms on every 5 s checkpoint.
  - **Network (F3/F4/F5):** client command `sendMs` stayed at or below 0.26 ms, and server `broadcastMs` at or below 0.18 ms. No connection faults.
  - **World sync (F8/F9):** zero resync requests, map-invariant reports, move misses, persistence or checkpoint errors.
  - **Client frames:** steady-state median 5.4 ms on both clients (worst 11.6 ms and 8.3 ms); physics about 0.12 ms.
  - **Load transient:** while a client loads its scene, snapshots queue (up to 266) and drain back to 1 within about 3 s of the scene starting. This is a load-time effect, not a backlog.
  - **Pre-existing, not from this change:** players show `hp=0/100 alive=False` because the dedicated server's database holds dead test players from earlier runs. The July run before this session shows the same.
- Not run: interactive play by a person (pushing cubes, combat, building between two players). The perf run's clients connect and idle without input. Cube pushing, fracture, combat and building between sessions are covered by the runtime and smoke tests.

## Known Limits

- Swaps (two cubes exchanging cells in one tick) are still rejected conservatively.
- Rubble positions are not replicated. Each client settles rubble locally, and players ignore it.
- The primary-seat model remains, with a per-session `Player` behind every seat. The runtime still has one `Player` property, which is the primary seat.
