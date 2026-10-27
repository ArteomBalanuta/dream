# Dynamic Fracture Sync Batching

Purpose: Record the performance profiling result and fix for FPS drops when many mini-cube fracture pieces move.

Status: Implemented and verified.

Scope: Physics-to-map synchronization for dynamic fracture pieces, plus performance documentation.

## Findings

- The profiling harness was run with `DREAM_AUTO_CONNECT=1`, `DREAM_RENDER_PERF_LOG=1`, and `DREAM_NET_DIAGNOSTICS=1` through `skills/profiling-dream-multiplayer/scripts/launch-and-summarize.ps1`.
- Baseline multiplayer logs showed healthy snapshot queues and remote-player rendering, so the mini-cube complaint was not explained by network queue buildup.
- Code tracing found the high-risk path in `PhysicsSyncSystem.SyncDynamicMapBodies`: each dynamic fracture body synchronized independently into `Map.UpdateFracturePiecePosition`.
- The old map update path copied the full fracture-piece list and rebuilt the parent cell render/collision descriptors for each moved mini cube. When several pieces in the same parent cell moved together, the work multiplied by piece count.

## Change

- Added `Map.UpdateFracturePiecePositions(...)` to apply many moved piece centers for one geometry cell with one artifact rebuild.
- Changed `PhysicsSyncSystem` to group pending fracture-piece syncs by `GeometryCellKey` before calling the map.
- Kept per-piece acceptance handling so stale or invalid body references still reset to their last accepted centers.

## Verification

- Added `UpdateFracturePiecePositions_AppliesMultipleMovedPiecesForOneCell`.
- Verified focused physics sync tests passed.
- Verified full test suite passed.
- Verified `dotnet build dream\dream.csproj --nologo /p:UseSharedCompilation=false` passed with known warnings.

## Follow-Up

- The general harness still showed static chunk draw/signature spikes and one isolated server `advanceMs` outlier without world mutations. Those are separate from the moved-rubble sync path and should be profiled in their own pass if they remain visible during gameplay.
