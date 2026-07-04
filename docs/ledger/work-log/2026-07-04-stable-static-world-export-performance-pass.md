Purpose: Record the runtime performance investigation and the first camera-movement static chunk rebuild fix.
Status: Completed
Scope: Static world render export ownership, movement-triggered static mesh rebuilds, and performance counter evidence.

## Summary

Runtime profiling with `DREAM_AUTO_CONNECT=1` and `DREAM_RENDER_PERF_LOG=1` showed that the FPS drops after movement were caused by static chunk mesh rebuild and OpenGL upload work running on the render thread. Smooth frames reused cached meshes and stayed cheap, but movement changed the camera-local `GeometryRenderExport`, which forced many static chunk mesh inputs to be rebuilt.

The renderer was already drawing efficiently. The expensive part was not visible triangle draw cost; it was treating camera movement as static mesh source invalidation.

## Evidence

Captured render counters showed:

- 42 render-perf samples.
- 16 samples rebuilt static meshes.
- Average smooth frame: `9.29 ms`.
- Average rebuild frame: `192.10 ms`.
- Average rebuild sync time: `98.59 ms`.
- Maximum sync time: `242.25 ms`.
- Average draw time: `0.079 ms`.
- Rebuild frames averaged `57.12` rebuilt meshes.

The dominant spike was `Static Timing: sync`, which includes CPU mesh creation and render-thread GL buffer upload.

## Root Cause

`WorldSessionRuntime.CapturePresentationState(...)` captured `Map.CaptureGeometryRenderExport(presentationCamera.Position, maxRenderDistance)` every frame. That meant moving or flying changed the static renderer's source export even when the world itself did not change.

After the cache correctness fix, those source changes were now correctly detected, so movement caused expensive rebuild/upload bursts instead of wrongly reusing incomplete meshes.

## Completed Work

- Added `Map.CaptureFullGeometryRenderExport()` for stable full-world presentation exports.
- Preserved the existing radius-based `CaptureGeometryRenderExport(...)` path for local/spatial-query callers.
- Updated `WorldSessionRuntime.CapturePresentationState(...)` to use the full-world export, so camera movement affects draw visibility but not static mesh invalidation.
- Added map tests proving full export includes far cells excluded by radius export.
- Added runtime coverage proving presentation no longer depends on the render-distance bubble for static world export.
- Updated `docs/performance-and-optimizations.md` with the new guardrail.

## Next Optimization Pattern

The durable long-term pattern is chunk-owned static mesh streaming:

- Cache meshes by chunk/material/role plus chunk input signature.
- Keep build and draw radii separate; camera movement changes only visible chunk keys.
- Mark chunk and neighbor chunks dirty when world geometry mutates.
- Build CPU mesh data on worker threads.
- Upload GL buffers on the render thread with a per-frame budget.
- Keep old meshes visible until replacements are uploaded.

With the current world size, the stable full-world export is the smaller fix and should eliminate movement-triggered rebuild spikes. If world size grows enough that full startup export becomes too expensive, the next step should be the streaming pattern above rather than returning to camera-local mesh source data.

## Verification

- `dotnet test dream.Tests\dream.Tests.csproj --filter "FullyQualifiedName~MapRenderCandidateTests|FullyQualifiedName~WorldSessionRuntimePresentationSmoothingTests" --nologo /p:UseSharedCompilation=false`
- `dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false`
- `dotnet build dream\dream.csproj --nologo /p:UseSharedCompilation=false`
- Runtime perf pass with `DREAM_AUTO_CONNECT=1` and `DREAM_RENDER_PERF_LOG=1`.

Runtime verification after the fix showed:

- 17 render-perf samples.
- 1 startup/static-build sample.
- 16 reuse samples after movement.
- Average reuse frame: `2.88 ms`.
- Maximum reuse frame: `5.91 ms`.
- Average reuse sync time: `0.00 ms`.
- Maximum reuse sync time: `0.00 ms`.
- Average reuse draw time: `0.040 ms`.

The remaining spike is the initial one-time static build/upload, not movement-triggered rebuild churn.
