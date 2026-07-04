# Host Frame And Integrated Server Performance Counters

Purpose: Add host-visible instrumentation that separates render-frame cost from integrated-server cost.
Status: In Progress
Scope: Client frame timing, integrated local server timing, HUD counters, and render-performance logging.

## Goal

Expose total frame time plus:

- server tick, snapshot, and broadcast timing
- enemy, local-player, and remote-player draw timing
- HUD/ImGui draw timing
- `SwapBuffers` timing

so host-side FPS drops can be explained from measured slices instead of inferred missing time.

## Implementation Outline

1. Extend `FramePerformanceSnapshot` with the new client/server timing fields and HUD/log formatting.
2. Add a server-owned latest-performance snapshot seam in the integrated local server path so the client can read background server timing without conflating it with render-thread timing.
3. Measure render slices in `PlayScene.Render()` and surface them through the HUD/logging path.
4. Measure present time in `Game.OnRenderFrame()` and push the sampled totals back into `PlayScene` for next-frame reporting.
5. Verify with focused tests for the snapshot formatter and the dedicated server service timing seam, then run the project build.
