# Fallback Cube Texture And Static Dirty Split

Purpose: Record the fixes for audit items C1 (untextured fallback cube mesh) and B1 (dynamic cube movement forcing static chunk rebuilds) from `docs/ledger/analysis/2026-10-27-cube-physics-render-deep-audit.md`.

Status: Implemented and verified by automated tests. Not yet verified visually or with in-game frame timing.

## Completed Changes

### Textured fallback cube (C1)

- Added `Rendering/World/TerrainFallbackCubeMesh`. It builds a unit cube centered on the origin through `StaticWorldChunkMeshBuilder`, with positions, normals and 0–1 UVs on six faces.
- `Render` now creates a `StaticMeshBuffer` from that mesh, and `DrawTerrainCubeFallback` draws it instead of the position-only cube VAO. The buffer is disposed in `DisposeHUD`.
- This fixes cubes and rubble without a model asset rendering flat and untextured. That covers Sand, Mud, Snow, Rock and GrassPath, and every material when visual assets are off. It also fixes three of their faces being culled: the old fan data wound the back, right and bottom faces clockwise while the draw enabled back-face culling.
- `StaticMeshBuffer` exposes `PositionAttributeLocation`, `NormalAttributeLocation` and `TexCoordAttributeLocation`, and uses them for its attribute setup.
- The position-only VAO is unchanged and still serves debug, collision and wireframe draws.

### Static dirty split (B1)

- `GeometryRenderExport` gained `StaticDirtyCells`. When it isn't supplied, it defaults to `DirtyCells`, so existing external constructions keep their meaning.
- `Map` routes every render-dirty mark through `MarkRenderDirty`. That helper tracks which cells currently contribute static render content (`_staticRenderCells`) and adds a cell to `StaticDirtyCells` only if it was or is static. Static content means a coarse cube in `_staticPhysicsCells` or the floor, or a subdivided cell that is not fractured.
- `StaticWorldChunkRenderer` uses `StaticDirtyCells` both for the export signature and for forcing dirty chunks.
- Moving dynamic cubes and fracture pieces still appear in `DirtyCells`, but no longer change the static export signature. A push therefore no longer triggers a full static extract, a chunk re-plan and identical chunk rebuilds every frame.

## Verification

- New `TerrainFallbackCubeMeshTests` (4 tests): six faces, 24 vertices and 36 indices with full 0–1 UVs per face; a unit cube centered on the origin; every triangle counter-clockwise around an outward normal; `StaticMeshBuffer` attribute locations match `shaders/cubeVertex.glsl`.
- New `MapStaticRenderDirtyTests` (5 tests):
  - A dynamic cube crossing cells is render-dirty but not static-dirty.
  - Fracture piece movement is not static-dirty.
  - Removing a static cube is static-dirty.
  - Subdividing a placed cube is static-dirty.
  - Pushing a dynamic cube across cells for five steps keeps `StaticWorldChunkRenderer.CalculateExportSignature` unchanged.
- In `StaticWorldChunkRendererTests`, the existing dirty-cells signature test now targets `StaticDirtyCells`. A new test checks that dynamic-only dirty cells don't change the signature.
- Full suite: `dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false`, 605 passed, 0 failed.
- Build: `dotnet build dream\dream.csproj --nologo /p:UseSharedCompilation=false` succeeded with the existing package and analyzer warnings.
- The game was not run. Fallback textures and frame time while pushing have not been checked by eye.

## Follow-Up

- The client still rebuilds a cube's Bullet body for every replicated move (audit B2).
- `CapturePresentationGeometryRenderExport` still walks every solid cube each frame, and the signature still hashes all static content each frame (audit B7).
- The chunk build queue can still drop a superseded request's stale-mesh removal (audit section D).
