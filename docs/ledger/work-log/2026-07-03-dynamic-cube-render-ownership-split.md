Purpose: Record the render-ownership split that keeps dynamic rigid-body cubes out of the static chunk cache.
Status: Completed
Scope: `GeometryRenderExport`, `Map`, `WorldRenderer`, static chunk extraction/signatures, and regression coverage for dynamic cube export behavior.

## Summary

The static chunk renderer was still consuming every compatibility cube exported by the map. That worked for static terrain, but it broke down for gameplay cubes that move under physics: the chunk cache kept presenting a terrain-style copy while collision and authoritative transforms continued to advance elsewhere.

The fix splits `GeometryRenderExport` into two compatibility-cube paths:

- `CompatibilityCubes` remains the static chunk input
- `DynamicCompatibilityCubes` carries live rigid-body cubes for direct world rendering

`Map.CaptureGeometryRenderExport(...)` now classifies coarse solid cells using authoritative static-cell ownership. Startup terrain and other static cells stay chunk-backed, while non-static placed cubes render from their current cube/body transform. `StaticWorldChunkExtractor` and `StaticWorldChunkRenderer.CalculateExportSignature(...)` intentionally ignore the dynamic list so moving cubes no longer invalidate or drift inside the terrain cache.

## Verification

- Focused tests: `dotnet test .\dream.Tests\dream.Tests.csproj --nologo --filter "MapRenderCandidateTests|WorldRenderBatchPlannerTests|StaticWorldChunkRendererTests|PhysicsSyncSystemTests" /p:UseSharedCompilation=false`
- Full test pass: `dotnet test .\dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false`
- Build check: `dotnet build .\dream\dream.csproj --nologo /p:UseSharedCompilation=false`
