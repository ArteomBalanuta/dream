# Hybrid Presentation Export FPS Fix

Purpose: Record the FPS regression fix for movement near `Player position: (8.086724, 1.5, 4.500001)`.

Status: Implemented and profiled.

Scope: Runtime presentation export selection, static chunk cache stability, dynamic object render bounds, and performance documentation.

## Summary

Profiling at the persisted player coordinate showed that movement reintroduced camera-local static mesh source churn. The bad run reached `13 FPS` on one client and logged static chunk work such as `Export: 16.47 ms`, static signature `15.53 ms`, static plan `11.76 ms`, and static sync `5.19 ms`.

The renderer itself was not the primary issue. Static chunks were being asked to treat camera movement as source-data changes, so the cache repeatedly extracted, planned, rebuilt, or synchronized chunk meshes even when the world did not materially change.

## Change

- Added `Map.CapturePresentationGeometryRenderExport(position, radius)`.
- Static compatibility cubes and static render blocks now enter presentation from a stable full static-world source.
- Dynamic compatibility cubes and fracture-piece blocks remain radius-filtered by camera position.
- Updated `WorldSessionRuntime.CapturePresentationState(...)` to use the presentation-specific export.
- Added regression coverage that a tiny presentation render distance still includes far static terrain while excluding a far dynamic cube.

## Verification

- Focused tests passed:
  - `dotnet test dream.Tests\dream.Tests.csproj --filter "FullyQualifiedName~WorldSessionRuntimePresentationSmoothingTests|FullyQualifiedName~MapRenderCandidateTests|FullyQualifiedName~StaticWorldChunkRendererTests" --nologo /p:UseSharedCompilation=false`
- Runtime profiling after the fix:
  - `logs/perf-run-20260705-230731`
  - steady render samples showed static plan `0.00 ms` and static sync `0.00 ms`
  - focused client reached `332 FPS`
  - the formerly bad client ended at `90 FPS` instead of `13 FPS`

## Follow-Up

The profile still shows startup/window-focus lows and dead persisted test players. Those are separate from the movement-triggered static chunk cache churn fixed here.
