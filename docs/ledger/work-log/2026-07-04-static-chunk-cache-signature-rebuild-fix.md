Purpose: Record the renderer cache fix for terrain holes that appeared after leaving and re-entering the visible world.
Status: Completed
Scope: Static chunk mesh planning, input signatures, cache reuse rules, and focused renderer regression coverage.

## Summary

The invisible-but-walkable terrain issue was caused by static chunk meshes being reused too aggressively. The renderer cached meshes by chunk/material/role key, but the old planner skipped cached keys without proving that the cached mesh had been built from the same visible cell set.

That became visible after flying away from the map and coming back. A chunk could first be cached from a partial camera-local export, then later be treated as complete when more cells from the same chunk entered the export. Collision and authoritative map data were still present, but the cached mesh did not contain those faces.

## Root Cause

`StaticWorldChunkRenderer.CalculateInputSignature(...)` tried to include material cells and occluder cells, but passed `HashCode` into `AddCellsToSignature(...)` by value. Because `HashCode` is a mutable struct, the helper mutated a copy and the cell data never affected the signature.

As a result, chunk input signatures effectively depended only on `StaticWorldChunkMeshKey`, so partial and fuller exports of the same chunk looked identical to the cache.

## Completed Work

- Changed static chunk planning to accept cached input signatures instead of cached keys.
- Reuse now happens only when the chunk is not dirty and the newly calculated input signature matches the cached signature.
- Fixed `AddCellsToSignature(...)` to pass `HashCode` by `ref`, so material cells and occluder cells actually contribute to the signature.
- Added dirty cell keys to the static export signature, so dirty chunk notifications cannot be dropped when the visible cell list otherwise looks unchanged.
- Added a regression test proving a cached chunk is rebuilt when newly visible cells enter the same chunk.
- Added a regression test proving dirty cells change the static export signature.
- Updated the optimization docs to make the cache guardrail explicit.

## Verification

- `dotnet test dream.Tests\dream.Tests.csproj --filter "FullyQualifiedName~StaticWorldChunkRendererTests|FullyQualifiedName~MapRenderCandidateTests|FullyQualifiedName~StaticWorldChunkMeshBuilderTests" --nologo /p:UseSharedCompilation=false`
- `dotnet test dream.Tests\dream.Tests.csproj --filter "FullyQualifiedName~StaticWorldChunkRendererTests" --nologo /p:UseSharedCompilation=false`
- `dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false`
- `dotnet build dream\dream.csproj --nologo /p:UseSharedCompilation=false`
