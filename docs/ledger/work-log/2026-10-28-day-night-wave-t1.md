# Day/Night Wave T1: World Clock, Shared Lighting, Sky

Purpose: record wave T1 of the day/night roadmap (`docs/ledger/plans/2026-10-28-day-night-roadmap.md`) and its fix loop.

Status: integrated on `integrate/engine-iteration` and verified by automated tests and the perf harness. Not pushed. Not merged into `master`. Interactive play has not been checked.

## Process

1. **Plan.**
   - The user approved the day/night roadmap: nights are dark, no time passes offline, no skipping, no weather.
   - The open questions were settled with the recommended answers: 30-minute days, moonlit nights, a debug-only time slider, and one extra wolf at night in T2.
2. **Architect.** It wrote `docs/ledger/plans/2026-10-28-wave-t1-plan.md`. Before any implementer started, the shared contract types were committed as C0 (`43cdfa4`), so all five branches compiled against the same types.
3. **Implementation.** Five implementers ran in parallel: T1a, T1b, T1c, T1d and T1e. All five merges were clean. The tip was `ed81ccc`, and one full run passed 2,941 tests.
4. **Verification.**
   - Reviewer: 0 Must fix, 5 Should fix, 8 nits.
   - Tester: no bugs. It added 12 tests and ran the perf harness against a build of the pre-T1 commit.
5. **Fix loop 1.** Two implementers, one for rendering and one for clock and network. Both merges were clean. The tip was `7c71a50`, and one full run passed 2,966 tests.

## Changes

| Workstream | Change |
| --- | --- |
| T1a World clock | `WorldClock` counts world ticks; `Advance()` runs once per `AdvanceAuthoritativeTick`. It provides `TimeOfDay01`, `DayNumber`, `Phase` (Night [20,4), Dawn [4,7), Day [7,17), Dusk [17,20)) and `PhaseChanged`. The default is a 30-minute day (`DREAM_DAY_LENGTH_MINUTES`, 1 to 1440), and a new world starts at 08:00. World time is saved as `world_metadata.world_time_ticks` by non-null checkpoints and by shutdown, and restored at startup. No time passes while the server is off. |
| T1b Replication, protocol v5 | `JoinAcceptedMessage.WorldClock` carries a `WorldClockAnchor`. Clients derive the time from their server-tick estimate. Snapshots gain 0 bytes; the join grows by 97 bytes. v4 peers are refused in both directions. |
| T1c Shared lighting | A `FrameLighting` std140 uniform buffer (144 B, binding 0) has a world range and a view-model range (eye space, ambient floor 0.08). `lighting.glsl` holds the hemisphere ambient, sun and moon light, exposure and exp2 fog. `Shader.ResolveIncludes` adds one level of `#include`. The cube and enemy shaders no longer use hard-coded point lights. Noon matches the old look (cube within 1e-4). |
| T1d SkyModel | A pure, allocation-free function from time of day to `SkyState` and `FrameLighting`. Its keyframes are night, blue hour, sunrise, morning, day, afternoon, sunset and dusk. The sun moves on a great circle through the old light direction at noon. At night the light is set by `DREAM_NIGHT_BRIGHTNESS` (default 0.15, moonlit). The horizon color is always the fog color. |
| T1e Sky pass | A full-screen triangle drawn at depth 1 after the actors: a gradient, hashed stars, a moon disc, a sun disc and its glow. The clear color is the horizon color. The Settings window has a debug slider for this client's time of day. |
| Fix: rendering | The actor ambient scale is now driven by the sky (`Params.z`): 3.25 by day, 1.5 at night. At midnight actors are 1.18× terrain brightness instead of 2.2×. `SkyRenderer` is created when the scene starts. Docs for the sky and time of day were added. Nits fixed: a BOM in `Shader.cs`, the slider's maximum, clear timing. |
| Fix: clock, network | `ConfigureWorldClock` throws once the world has ticked. `ClientWorldClock.TimeOfDay01Exact` and `Phase` match the server on every tick, including phase boundaries. The anchor's time-of-day math matches the server bit for bit. A reflection hack was removed from a test. The day-length warning goes through the injected log. |

## Measured Impact

| Measurement | Before (`d37a311`) | After (`ed81ccc`) |
| --- | --- | --- |
| Draws per frame, client 1 / client 2 | 28 / 33 | 29 / 34 (+1 sky) |
| Programs per frame | 5 | 6 |
| Uniform uploads per frame | 127 | 136 |
| FPS (capped) | 144 | 144 |
| Actor GPU section | about 0.00 ms | about 0.04 ms (includes the sky) |
| Terrain brightness at midnight vs noon (CPU mirror) | n/a | 13% |
| Join-accepted size | 7,677 B | 7,774 B |
| Snapshot bytes over 200 ticks | 58,107 B | 58,107 B (unchanged) |
| Allocations per tick, over a full day with phase changes | 0 B | 0 B |

The harness runs were in third person, so these frame counts cannot be compared with wave 13's first-person 23 draws.

## Verification

- `dotnet build dream/dream.csproj`: 0 errors.
- `dotnet test` on `7c71a50`: 2,966 passed and 25 skipped, in 1 run of 62 s.
- Tester checks:
  - A persistent server restarts with no gap or jump in time, after a clean shutdown and after the harness killed it (about 3 s lost).
  - With a 1-minute day, the client follows every phase.
  - Two lockstep servers with different clocks produce identical checksums on every tick.
  - The perf harness reported 0 GL errors.
- Not verified:
  - Interactive play and the look on screen at 08:00, 12:00, 18:00 and 00:00.
  - The faint edge where the fog meets the render distance (fog is 0.93 at 48 units).

## Known Limits And Follow-ups

- **Sun height.** The noon sun is only about 25° high (`ReferenceNoon` keeps the old light direction). Days look like late afternoon, and walls facing −Z are lit more strongly than the ground. Retune this in T2d together with night darkness. It changes how noon looks.
- **Nights are moonlit, not dark yet.** `DREAM_NIGHT_BRIGHTNESS` stays at 0.15 until T2 adds torches and lamps; T2d then lowers it.
- **No `server.world-clock` message.** Nothing changes the server's clock while it runs. A reconnect re-anchors.
- **GLSL include errors.** Errors in an included file report shifted line numbers, because no `#line` directives are emitted.
- **Next: wave T2.** Point lights, a torch item, a lamp block, night tuning and a night spawn. Persistence wave A (SQL foundation) runs alongside it, since the two share no files.
