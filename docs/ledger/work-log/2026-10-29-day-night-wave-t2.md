# Day/Night Wave T2: Light In The Dark

Purpose: this log records wave T2 of the day/night roadmap and the fix loop shared with persistence wave A.

Status: integrated on `integrate/engine-iteration` and verified by automated tests, the perf harness and loopback tests across both waves. Not pushed. Not merged into `master`. Interactive play has not been checked.

## Process

1. **Planning.**
   - The architect wrote `docs/ledger/plans/2026-10-28-wave-t2-plan.md`.
   - It corrected the roadmap in three places:
     - Remote players' equipped items were never replicated, so T2f was added for that (protocol v6).
     - There was no building menu, so T2c gained one.
     - Persistence needed no change.
2. **Contracts.** C0 added point lights, the light collector seam, `HeldLightKind`, emissive output and its contract test (`b9e14bd`).
3. **Implementation.** Six implementers ran in parallel with persistence wave A: T2a, T2b, T2c, T2d, T2e and T2f. All merges were clean.
4. **Verification.**
   - A tester checked T2 and persistence wave A together.
   - A T2 reviewer found Must-fix items in the docs only, plus 4 Should-fix items.
5. **Fix loop 1.** It was shared with wave A:
   - F2 fixed lighting.
   - F3 records starter kit grants.
   - F1 also covered one T2 item, the build-material reset.

## Changes

| Workstream | Change |
| --- | --- |
| T2a Point lights | A `LightList` uniform buffer (528 B std140, binding 1) holds the 16 nearest lights. They are culled against the frustum and kept in a stable order. `pointLights.glsl` uses windowed falloff with a 0.25 wrap term. The view model gets its own range in eye space. `DREAM_DEBUG_POINT_LIGHTS` adds a deterministic ring of lights for the perf harness. |
| T2b Torch | `tool.torch` is a starter item and `HeldLight = Torch` (the lantern counts as `Lantern`). `HeldLightCatalog`: torch (1.0, 0.62, 0.32)×1.6, radius 8, with flicker; lantern ×1.1, radius 6. `HeldLightAnchor` places the light for first-person, body and remote views. |
| T2c Lamp and build menu | `GeometryMaterialId.Lamp = 8` is untextured and emissive. `build.material` is a session-level selection; Auto never picks Lamp. A "Build" window lists Auto, Stone, Dirt and Lamp. Each lamp gives one point light (×1.8, radius 10). Lamps persist through the existing placed-cube and rubble paths. |
| T2d Sun and night | The noon sun is at 60°. Up-facing light at noon is 0.92, and walls are no brighter than the ground. The night is dark blue: midnight is 6.9% of noon, `DREAM_NIGHT_BRIGHTNESS` defaults to 0.07, and moonlight is about 63%. The view-model floor is 0.05. The crosshair has more contrast. |
| T2e Night wolf | The population step targets 2 wolves at night and 1 by day. The despawn at dawn deletes the row directly, the same way a kill does, and only removes wolves whose ids the runtime handed out. The clock is now a gameplay and lockstep input. Docs updated. |
| T2f Replication, protocol v6 | `ActorSnapshot.HeldLight` with the group bit `Equipment = 256`, which is not implied by Full. It is carried through deltas, rebased deltas, keyframes and `ActorStateCache` to `RemotePlayerPresentationState`. v5 is refused in both directions. |
| Fix: lighting (F2) | Held lights are collected before lamps. Lamps beyond 58 m (render distance + lamp radius) are skipped. A partly mined lamp lights from the centroid of its remaining solid subcells, via the new `GeometryRenderExport.StaticCells`. A shader loop bound was added. The emissive value is cached per program, which removes 23 redundant uniform uploads per frame. |
| Fix: starter kit (F3) | Migration 0002 adds `player_starter_grants`, and `ToolCatalog.StarterKit` versions the kit (v1 tools, v2 torch). Only kinds introduced after the recorded version are granted, so a removed item is never granted again. The grant is loaded in the same single inventory statement. |
| Fix: build material (F1) | The selection is cleared when a session detaches or is forgotten, so a reconnect starts from Auto, matching the HUD. |

## Measured Impact

| Measurement | Before | After |
| --- | --- | --- |
| Snapshot bytes over 200 ticks, no light equipped | 58,107 B | 58,107 B |
| Snapshot bytes over 200 ticks, one torch | n/a | 58,115 B (+2 B per keyframe, 0 B per delta) |
| GPU World section, 2 clients, 0 → 16 lights | 0.07 / 0.08 ms | 0.25 / 0.33 ms (budget +0.5 ms) |
| FPS with 16 lights | 144 | 144 |
| Uniform uploads per frame, same scene | 180 (after T2, before F2) | 155 (T1 set + 1 light-list upload) |
| Midnight terrain brightness vs noon | 13% (T1) | 6.9% |
| Torch at 2 m at midnight vs noon up-facing | n/a | 77% |

## Verification

- The full suite on `1a92684` passed: 3,180 passed, 25 skipped, 0 failed. It took 2 runs, because the first run exposed 4 test conflicts between F1 and F3, which were fixed.
- Tester loopback coverage over SQLite:
  - A lamp, a torch received through the starter top-up, and the night wolves all survive a restart at night.
  - A restart at dawn leaves 1 wolf and 1 database row.
  - Remote torch light crosses the wire.
  - There are no chain breaks.
- Perf harness: 0 GL errors, and 16 debug lights stay within budget.
- Not verified:
  - interactive play;
  - screenshots at 12:00 and 00:00 with a torch and a lamp;
  - the instanced lamp path on screen.

## Known Limits And Follow-ups

- **No crosshair outline.** T2d only raised contrast.
- **The Build window only takes clicks while an overlay frees the mouse.** It has no number-key shortcuts.
- **A checkpoint can bring back a despawned wolf's row.** This happens when a checkpoint captured before the dawn despawn lands after its delete; kills have the same race. It heals on the first daytime tick after a restart.
- **`ToolCatalog.Resolve` throws for unknown persisted kinds.** `ResolveEquippedHeldLight` now calls it on every snapshot tick, so a `TryResolve` would be safer.
- **Leftover processes.** Three `dream.exe` processes from a fix-loop perf run are still running and hold port 5057: PIDs 19032, 27408 and 36448, all from `.claude\worktrees\agent-a401a7d2c68a6ad6e\artifacts\before-bin\dream.exe`. The agent's permission to stop them was denied, so the user must close them.
- **Next on the day/night roadmap: T3.** Performance with many lamps, and optional sun shadows.
