# Engine Iteration Wave 10: Dependency Inversion, Replicated Ids, Protocol v3 Chunked World Transfer, Batched Terrain, Stale-Session Takeover

Purpose: Record wave 10 of the round-2 plan, `docs/ledger/plans/2026-10-28-engine-iteration-round-2-plan.md`, together with its fix loop.

Status: Integrated on `integrate/engine-iteration` and verified by automated tests and Release load runs. Not pushed. Not merged into `master`. This wave ran in continuous mode.

## Plan Revision

Wave 10 took the tasks that had been deferred from wave 9, plus the non-overlapping parts of the "Wave 10 and later" list:
- ARCH-2
- ID-1 (replicated-id fixes)
- SCALE-1
- GPU-3
- NETR-8

Four tasks were held back because they share files with this wave:
- GPU-6 shares `Render`/`StaticMeshBuffer` with GPU-3.
- ARCH-8 and SCALE-7 share `Map` with ID-1.
- ARCH-3 shares `WorldSessionRuntime`.

## Process

1. Five implementers ran in parallel from `5b38d95`. ARCH-2 was merged last, so its namespace moves landed on top of the other four.
2. Integration landed at `5bfd10d`.
   - There were no text conflicts.
   - Four test files needed `Dream.Simulation.Persistence` usings.
   - The client allowlist was unchanged.
3. Review and test:
   - The reviewer found 0 Must fix and 6 Should fix items, and wrote up an identity decision for the user.
   - The tester added 5 test files plus a `-Exe` option for the perf harness, and found 3 issues.
4. Fix loop 1 ran four implementers:
   - transfer hardening
   - takeover policy (option C)
   - ids
   - docs and nits
5. Integration of fix loop 1:
   - one `docs/architecture.md` conflict, resolved by hand;
   - two test-timing fixes, one for the new 3 s takeover window and one for deferred overflow resyncs.
6. The re-review found 0 Must fix and 5 Should fix items:
   - the 3 s same-session window let a hijacker bypass the 8 s name window, because session ids are broadcast;
   - an oversized single-tick broadcast faulted every connection;
   - the 120 s transfer deadline had little margin;
   - the join throttle was reset by reconnecting;
   - a placeholder line was left in the docs.
7. Fix loop 2 ran three implementers: takeover/throttle, broadcast/deadline, and client caches. All three merged cleanly. This was the second and last fix loop.

## Changes

| Task | Change |
| --- | --- |
| ARCH-2 | `Server/Rules` moved to `Simulation/Rules`, and the persistence ports and records moved to `Simulation/Persistence`, 25 files in all (`git mv`). `ITickDrivenAuthoritativeSessionHost` moved to `Simulation/Runtime`. New `IPersistenceWriteBatch` port; `SqliteWriteBatch` installs itself through a `[ModuleInitializer]`. New `RuntimePersistencePorts` and `ServerBootstrap.CreateRuntimeHost`. `DependencyDirectionTests` forbids `Dream.Server` in Simulation and pins an exact client allowlist of 7 files. |
| ID-1 | New `CubeIdentified` world mutation (kind 4) carries a terrain cube's first replicated id. Cubes and enemies now have disjoint id ranges (`ReplicatedActorIdRanges`). Both counters are derived from persisted data on restore. A colliding enemy from an old save gets a new id, and fracture-group ids are reserved. This fixes a latent bug where a new cube could reuse a fractured cube's id and delete its stored pieces. |
| SCALE-1 | Protocol v3 introduces a compact world encoding: delta-coded cells and ids, material runs, quantized offsets, and the first-pass rubble pattern regenerated on decode. That is about 11 B per cube, against 127 B before. The world is captured into pooled flat arrays on the tick (`WorldSnapshotCapture`). Join and resync worlds travel as a carrier message followed by `server.world-chunk` frames interleaved with normal traffic. The client reassembler holds every message until the final chunk. |
| GPU-3 | Static terrain draws as one pass sorted by material slot, with one bind per material. The material catalog uses fixed slots resolved at upload, so the per-frame path has no string or path allocations. |
| NETR-8 | A crashed client can rejoin before the 10 s idle timeout: a join for a session id, or a player name, whose holder has gone silent takes over the seat and saved profile. A join is rejected while the holder is live. Late messages from the old connection are dropped, and the old connection's player prefetch is kept when a join still needs it. |
| Fix: transfer hardening | `WorldTransferLimits` caps a transfer at 3 items per cell (64k minimum, 4M maximum), 4096 held messages and 120 s. World dimensions are validated and delta sums use checked arithmetic. The default server→client frame cap drops from 32 MB to 8 MB, against a worst legitimate frame of 2.7 MB. Chunks drop to 64 KB, so links down to about 12.5 KB/s survive. An overflow resync is deferred while a transfer is pending, so no second world is queued behind it. Taken-over connections are forgotten when their loop ends. Server-only message kinds sent by a client are refused before their payload is decoded. |
| Fix: takeover policy (option C) | The takeover window is 3 s for the same session id and 8 s for a matching name. A connection is throttled after a join that conflicts with a live holder (2 s, doubling up to 10 s). Staleness is measured up to the last inbound sweep, so a tick stall never makes healthy clients look stale. `docs/configuration.md` gains an "Identity limits" note. |
| Fix: ids | Players get their own id range (1–999, then a high overflow block). The lowest released id is reused deterministically, and a returning session reclaims its own id. The client starts a fresh presentation entry when an id reappears under another session. Enemy re-id on restore is a single `UPDATE` (`ReassignEnemyId`). Id reservation saturates instead of throwing. When the enemy range is exhausted, the spawn is refused and logged. |
| Fix 2: takeover and throttle | One 8 s window (`DedicatedServerHost.TakeoverSilence`) now applies to session-id and name takeovers alike. Real clients never reuse a session id, and session ids are broadcast, so a shorter session-id window only helped hijackers. The join throttle is keyed by (source IP, contested session id or name) in a bounded `JoinThrottleTable` (expiry, 1024-entry cap), so reconnecting does not reset it and NAT or loopback clients with different names are never throttled. A throttled fresh connection gets one `JoinRejected` with the remaining time. |
| Fix 2: oversized broadcast and deadline | A broadcast over the delta caps goes out stripped to actors and acks, and every joined connection is flagged for a resync (`OversizedBroadcasts`). The writer also strips any frame over the write cap instead of throwing (`OversizedSnapshotFrames`). World transfers are bounded by progress: 30 s without a chunk, 300 s in total. The deadline is checked after the frame is processed, so a final chunk that arrives on time completes. |
| Fix 2: client caches on id reuse | `RemotePlayerRenderer` drops the animation handle and clip tracker of an id that moves to another session. Player damage events apply only when the same session held the id in the previous and the current snapshot (`IPlayerActorSessionHistory`). Static draw handles are cleared after each pass. |
| Fix: docs and nits | `docs/architecture.md` now covers ARCH-2, NETR-8 and the v3 transfer limits. The static terrain draw uses a single-lookup draw handle. The `SqliteWriteBatch` CA2255 comment is corrected. |

## Measured Impact

| Measurement | Before | After |
| --- | --- | --- |
| World10k bytes per join | 1,755,282 | 234,725 |
| World50k bytes per join | 7.51 MB (one frame, above a 1 MB cap) | 1.10 MB in 64 KB chunks |
| World10k tick-side world export allocation | 1.80 MB (LINQ) | 232 B (pooled capture) |
| World50k tick-side world export | 2.78 ms, 6.5 MB | 1.52 ms, 232 B |
| Compact world size | 127 B per cube | about 11 B per resting cube with an id |
| Program binds / uniforms per frame | 11 / 140 | 6 / 105 |
| Overflow resyncs queued behind a transfer (tester scenario) | up to 2 extra full worlds, 5 redundant in 8 s | 0 queued behind an unfinished transfer |
| Slowest link that survives a resync | about 43.7 KB/s (256 KB chunks) | about 12.5 KB/s (64 KB chunks); 30 KB/s measured clean |
| Persistence group commit through `CreateRuntimeHost` | — | 2000 writes in 78 ms, 8 groups (registration confirmed in a fresh process) |

Release load on `5bfd10d`:
- Smoke8: tick p50 0.55–0.60 ms, p99 1.08 ms, 64 ticks/s, 0 dropped ticks, joins 158–168 ms.
- World10k: joins about 1.1 s, still limited by the simulation.

## Verification

- `dotnet build dream/dream.csproj` has 0 errors. The warnings are the ImageSharp advisories plus three CS860x warnings that predate this wave.
- `dotnet test dream.Tests/dream.Tests.csproj` on the fix-loop-2 tip passed 2,596 tests with 18 skipped (load and measurement tests that are gated), in 3 out of 3 runs, 88–91 s each.
- The tester checked these interactions:
  - Takeover during the old connection's transfer: separate transfers, and the old capture is released.
  - A `CubeIdentified` arriving in the middle of a transfer is applied after reassembly, and the checksums match.
  - Overflow during a transfer converges.
  - A 1 MB resync over a 50 KB/s link takes about 17.5 s. The watchdog is not tripped, and the client shows no backward steps afterwards.
  - Group commit is registered in a fresh process.
- The 2-client perf harness ran from the tester's worktree using the new `-Exe` option: 144 FPS, programs 6, uniforms 105, no errors.
- Not verified: interactive play.

## Known Limits And Follow-ups

- **Decision for the user: identity (NETR-8).** There is no authentication: session ids are broadcast, and profiles are keyed by display name.
  - Option C is in place: one 8 s takeover window, plus a join throttle keyed by source IP and the contested session or name. A Wi-Fi blip no longer gets a player kicked, and reconnecting does not reset the throttle.
  - A determined attacker on another IP can still claim a player who has been silent for 8 s.
  - The stronger fix is option D: a per-profile reconnect token, stored on the client and sent in the join request. It needs a protocol field and a schema column, and adding it now, while v3 is unreleased, avoids a v4.
- **Missed damage numbers on id reuse.** Damage to a player in the same snapshot interval in which the player appears or leaves is not shown. This is the cost of correct attribution when player ids are reused.
- **Frozen actors during a resync.** The client holds all messages until the world transfer finishes, so remote actors freeze for the duration of a resync (about 17 s on a 50 KB/s link). `RemoteClientSession.IsReceivingWorld` exists but is not shown in the HUD.
- **k=0 rubble on the wire.** The meaning of k=0 rubble depends on `CubeFracturePattern`. Any change to that pattern needs a protocol bump; a comment in the code now says so.
- **Test suite time** is now about 90 s, after about 120 new tests in this wave. A scheduler profile pass (in the style of ARCH-1b) is due.
- **Next wave candidates:**
  - GPU-6 (instanced fallback cubes)
  - ARCH-3 (`WorldSessionRuntime` partials)
  - ARCH-7 (`Render`/`PlayScene` split)
  - ARCH-8 (`Map` split)
  - SCALE-2 (delta actors)
  - SCALE-7 (memory hygiene)
  - ARCH-4 (docs drift, now that ARCH-2 has landed)
  - a "receiving world" HUD state
  - option D, if the user wants it
- **Still waiting on user decisions:**
  - the BulletProfilerGuard patch
  - the ImageSharp advisories
  - `maxParallelThreads`
  - the noclip/freeze gate
  - identity option D
