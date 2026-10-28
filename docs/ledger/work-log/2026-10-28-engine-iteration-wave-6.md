# Engine Iteration Wave 6: Enemy Interpolation, Persisted Seed, Snapshot Converter, Id-Keyed Checksum, Test Isolation

Purpose: Record wave 6 of `docs/ledger/plans/2026-10-27-engine-iteration-master-plan.md`, its fix loop, and the start of the second analysis round.

Status: Integrated on `integrate/engine-iteration` and verified by automated tests and a perf-harness run. Not pushed and not merged into `master`. Continuous mode.

## Process

1. Five implementers worked in parallel from `1b7bdae`: NET-4, NET-8, NET-9, WIRE-11 and TEST-1. All five merges were clean.
2. Verification:
   - The reviewer found no Must fix items and 4 Should fix items.
   - The tester added 11 cross-task tests and found **three production bugs**.
   - A perf run was done.
   - The four round-2 analysts started at the same time; their output is in `docs/ledger/plans/2026-10-28-engine-iteration-round-2-plan.md`.
3. Fix loop 1 had three implementers: server bugs, client bugs and labels, and test and doc nits.

## Changes

| Task | Change |
| --- | --- |
| NET-4 | `TickInterpolationBuffer` is now shared by players and enemies. `ClientScenePresenter` keeps a pooled buffer for each enemy, and `IEnemyRenderPoseSource` exposes the poses. `EnemyRenderer.DrawAtRenderTick` draws wolves at the interpolated pose, and `EnemyActor.Position` stays authoritative. |
| NET-8 | Each persistent world stores a random seed in a new `world_metadata` table (additive migration). `DREAM_SIMULATION_SEED` overrides it for one run. The seed is never logged or sent to clients. Hosts without persistence keep `DefaultSimulationSeed`. |
| NET-9 | New `Map.TryGetReplicatedId`. The checksum's cube part is keyed by replicated id, with the cell as fallback. |
| WIRE-11 | Hand-written converters for `ServerSnapshotMessage`, `ServerSnapshotEnvelope`, `WorldMutationDelta` and `DamageEventDelta`. The codec also interns known message kinds. |
| TEST-1 | A non-parallel `TimingSensitive` collection for timing-sensitive tests. The flaky tests stopped failing, but serial time rose to about 43 s; ARCH-1 in round 2 reworks it. |
| Fix A | **Bug:** on persistent worlds, every hit reset a placed cube's damage. The identity repair counted health as identity. It now compares only id and owner, and hits update the cached health. **Bug:** a respawned wolf reused id 2000, so a respawn after a coalesced death slid instead of snapping. Respawns now take a fresh, deterministic id, and the counter persists past restored ids. The server now registers replicated cube ids in `Map` through `WorldObjectRegistry.RegisterCube`. |
| Fix B | **Bug:** a late joiner kept the intact generated cube in a fractured cell, so its checksum never matched and resync looped. The world apply now removes such cubes unless the snapshot places one there. Enemy labels now use the interpolated render pose. |
| Fix C | Converter budgets tightened (snapshot encode 0 B, decode 5,504 B). New drift test for `KnownMessageKinds`. Collision wording fixed. `world_metadata` documented in the DDL doc and the configuration doc. |
| Integration | The MEM-3 golden test's checkpoints 6–10 were re-pinned for the fresh respawn ids. Forcing the old allocator back to 2000 reproduces the old values. |

## Measured Impact

| Path | Before | After |
| --- | --- | --- |
| 24-actor snapshot encode (WIRE-11) | 824 B | 0 B |
| 24-actor snapshot decode (WIRE-11) | 7,016 B | 5,008 B |
| Mutation-heavy payload encode (WIRE-11) | 2,032 B | 0 B |
| Enemy interpolation, per frame (NET-4) | n/a | 0 B |
| Enemy label placement (fix B) | 0 B | 0 B (now interpolated) |

## Perf Harness

Run `logs/perf-run-20261028-025139` (integration worktree), before the fix loop:
- Both clients held 144 FPS. After a few warm-up frames the frame medians were 0.39 and 0.51 ms.
- The server generated and stored a world seed and did not log its value.
- Server tick median was 0.06 ms, with one tick per loop.
- A one-player snapshot was 840 B, identical to wave 3, which confirms the converter is byte-identical.
- No errors.

## Verification

- `dotnet build dream/dream.csproj`: 0 errors.
- `dotnet test dream.Tests/dream.Tests.csproj`: 1,630 of 1,630 passed, 0 skipped. A second run had one failure in `AsyncResourceLoaderTests.PumpUploads_Respects_MaxPerFrame`, the thread-pool starvation case that ARCH-1 targets.
- The suite now takes about 81 s on a quiet machine (serial collection about 53 s).
- The tester's checks:
  - A persistent world restarted from its database keeps its seed and replays all 400 per-tick checksums.
  - A second database diverges at the first damage roll.
  - The seed does not appear in 774 KB of captured wire traffic, in decimal, hex, base64 or byte form.
  - All 1,376 frames of a 3-client, 300-tick session re-encode byte-identically.
  - Wolves never render backwards under gaps of up to 61 ticks.
  - Max speed times max gap is 2.8 m, under the 4 m threshold, so there is no false snap.
- Not verified: interactive play.

## Known Limits And Follow-ups

- A terrain cube that gets hit is registered with an id on the server. Clients learn that id only from a full world snapshot, so the two sides hash that cube differently until a resync.
- Placed-cube ids start at 1000 and enemy ids at 2000, and both grow, so the two ranges can overlap after about 1,000 registrations.
- Fractures of generated terrain are not persisted, so a restart regenerates that terrain intact. This looks like a design choice.
- Test suite duration and isolation are fixed in ARCH-1 (wave 7).
