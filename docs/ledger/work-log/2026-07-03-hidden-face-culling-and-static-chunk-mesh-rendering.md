Purpose: Record the hidden-face culling and static chunk mesh rendering implementation.

Status: Completed

Scope: Static world render mesh generation, exposed-face culling, renderer integration, diagnostics, and verification.

## Summary

Implemented a first static world chunk mesh renderer for `GeometryRenderExport`. The renderer converts compatibility cubes and geometry render blocks into render-only voxel cells, groups them by chunk and material, builds indexed meshes with fully hidden adjacent faces removed, uploads those meshes through `StaticMeshBuffer`, and routes `WorldRenderer.DrawWorld(Camera, GeometryRenderExport)` through the chunk path by default.

The implementation deliberately keeps canonical world state in the simulation layer. Chunk meshes are generated presentation artifacts; collision and multiplayer snapshots still derive from the existing map, geometry, and physics exports.

## Evidence

Focused unit coverage now verifies:

- chunk key floor-division mapping, including negative coordinates
- single-cube mesh output of `6` faces, `24` vertices, and `12` triangles
- adjacent same-material cubes suppress the shared interior face
- adjacent different-material cubes still occlude interior faces
- generated indices remain inside the vertex range
- export extraction includes both compatibility cubes and geometry blocks
- chunk planning groups cells by chunk and material while using all same-chunk cells as occluders

The HUD performance snapshot now includes static chunk counters:

- visible static chunks
- static chunk mesh count
- exposed static faces
- static chunk triangle count

Runtime FPS was not captured in this implementation pass because the reliable verification available here was build/test based rather than an interactive camera-matched performance run. The expected runtime measurement is still the one described in [the implementation plan](../plans/2026-07-03-hidden-face-culling-and-static-chunk-mesh-rendering.md): compare the same dense sculpture camera view with collision debug disabled and performance counters enabled.

A short desktop smoke launch of `dream\bin\Debug\net10.0\dream.exe` stayed alive for `5` seconds and produced no stderr output before being stopped by the verification harness.

## Follow-Up Fixes

After the initial implementation, runtime review exposed two regressions:

- Static chunk meshes used material-color fallback instead of terrain textures.
- Chunk visibility used a center-point/front-facing test, so large chunks could disappear while the camera was moving through or near their bounds.

The fix adds `StaticTerrainMaterialCatalog` and `StaticTerrainTextureCache` so chunk meshes bind terrain textures again. It also changes chunk visibility to keep chunks whose bounds intersect the render sphere, preventing large chunks from vanishing when their center is behind the camera.

The material path was then completed for grass face parity: static grass chunk meshes now split exposed faces into top, side, and bottom role batches, binding `grass_top.png`, `grass_block_side_texture.png`, and `Grass_bottom.jpg` respectively. Dirt and stone continue to use their single Poly Haven diffuse texture across all faces, matching their current glTF cube material setup.

Runtime movement then exposed a performance regression in the first cache policy. `StaticWorldChunkRenderer` used one global visible-export signature, so small camera/player movement at the render-radius boundary could invalidate every static chunk mesh and re-upload all buffers on the render thread. The renderer now plans chunk/material-role build inputs independently, keeps stable signatures per mesh key, reuses existing GL buffers for unchanged chunks, and limits face-occlusion inputs to the current chunk plus neighboring chunks instead of scanning the whole visible export for every face.

Follow-up profiling showed the next bottleneck was the mesh builder itself: a temporary startup-world probe measured first static mesh builds at roughly `15-16 s` because every candidate face scanned very large occluder lists. The builder now indexes occluders by adjacent face plane before testing interval coverage. The same probe measured first static mesh build time at roughly `153-162 ms` after the fix.

The next live auto-connected capture showed steady gameplay frames after warmup between roughly `1.75-5.93 ms`, with static chunk draw at roughly `0.03-0.19 ms`, physics at roughly `0.02-0.11 ms`, and static chunk plan/sync at `0.00 ms` on unchanged frames. The first gameplay log still included a startup/connect warm frame of roughly `1526 ms`, but its static chunk work was already low, indicating the large value is startup transition/warmup time rather than steady rendering.

An additional cache-planning pass now skips already cached non-dirty chunk/material/role keys when the visible export changes. That keeps visibility boundary movement from spending CPU planning work for chunks that already have valid meshes. This is intentionally a render-thread algorithmic fix rather than a premature render/game thread split.

The desktop client now supports `DREAM_AUTO_CONNECT=1` for local profiling and `DREAM_RENDER_PERF_LOG=1` for once-per-second gameplay performance logs.

Threading note: OpenGL buffer creation/upload and draw calls remain render-thread-owned. If the next measured bottleneck is first-build latency, the recommended multithreading path is CPU-only mesh generation on workers, followed by render-thread upload of completed immutable mesh data. A full game/render thread split is not the first fix for the current measured issue.

## Verification

- `dotnet test dream.Tests\dream.Tests.csproj --nologo --filter "StaticWorldChunkRendererTests|StaticWorldChunkMeshBuilderTests|StaticWorldChunkKeyTests|FramePerformanceSnapshotTests" /p:UseSharedCompilation=false`
- `dotnet test dream.Tests\dream.Tests.csproj --nologo --filter "StaticTerrainMaterialCatalogTests|StaticWorldChunkRendererTests|StaticWorldChunkMeshBuilderTests" /p:UseSharedCompilation=false`
- `dotnet test dream.Tests\dream.Tests.csproj --nologo --filter "StaticWorldChunkRendererTests|StaticWorldChunkMeshBuilderTests|StaticTerrainMaterialCatalogTests|FramePerformanceSnapshotTests" /p:UseSharedCompilation=false`
- `dotnet test dream.Tests\dream.Tests.csproj --nologo --filter "NetworkSessionCoordinatorTests|FramePerformanceSnapshotTests|StaticWorldChunkRendererTests|StaticWorldChunkMeshBuilderTests" /p:UseSharedCompilation=false`
- `dotnet test dream.Tests\dream.Tests.csproj --nologo --filter "PlanBuildInputsCanSkipAlreadyCachedChunks|PlanBuildInputsRebuildsDirtyCachedChunks|BuildDenseGridCompletesWithinInteractiveBudget" /p:UseSharedCompilation=false`
- `dotnet test dream.Tests\dream.Tests.csproj --nologo --filter "NetworkSessionCoordinatorTests|FramePerformanceSnapshotTests|StaticWorldChunkRendererTests|StaticWorldChunkMeshBuilderTests|StaticTerrainMaterialCatalogTests|StaticWorldChunkKeyTests" /p:UseSharedCompilation=false`
- `dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false`
- `dotnet build dream\dream.csproj --nologo /p:UseSharedCompilation=false`
- Auto-connect capture with `DREAM_AUTO_CONNECT=1` and `DREAM_RENDER_PERF_LOG=1` against `dream\bin\Debug\net10.0\dream.exe`

Focused result: `16` passed, `0` failed.

Texture/visibility regression result: `18` passed, `0` failed.

Static chunk cache regression result: `22` passed, `0` failed.

Static chunk cache-planning result: `3` passed, `0` failed.

Focused profiling/support result: `38` passed, `0` failed.

Full suite result after the follow-up fixes: `463` passed, `0` failed.

Build result: succeeded with the existing `BulletSharp` compatibility warning.
