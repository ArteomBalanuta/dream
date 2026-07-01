Purpose: record the startup world expansion that introduced deterministic terrain zoning, larger default size, and textured cube material variants.
Status: completed
Scope: startup world generation, terrain material asset routing, default map size, docs, and regression coverage

## Summary

The default world now starts larger and no longer appears as one flat grass sheet. Startup generation authors a deterministic terrain layout with grass meadows, dirt path bands, rocky outer accents, and a few low raised stone landmarks, while keeping the current geometry/render/collision pipeline cube-compatible.

## What Changed

- increased the default runtime floor size from `25` to `40`
- moved startup-world authorship deeper into `WorldGenerator` through a deterministic `CreateStartupGeometry(...)` path
- added mixed `Grass`, `Dirt`, and `Stone` terrain zoning for floor cells
- added first-pass raised startup landmarks as static stone cells above the floor plane
- added a raised adjacent landmass plus dirt bridge near spawn so the larger world reads immediately from the starting platform
- added `DirtCubeAssetDefinition` and `StoneCubeAssetDefinition`
- added `DirtTextureCube.gltf` and `StoneTextureCube.gltf` that reuse the cube mesh layout with repo-owned Poly Haven terrain textures
- updated `ModelAssets` and `Render` so terrain cube variants use the same world-unit normalization seam as the grass cube
- updated tests to cover larger defaults, mixed startup materials, and raised landmark generation

## Verification

- `dotnet test .\dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false`
- `dotnet build .\dream\dream.csproj --nologo /p:UseSharedCompilation=false`

## Notes

- this pass stays intentionally cube-first; the world looks richer without depending on unfinished slope or wedge-native render work
- follow-up bug fix: the integrated authoritative server default still created a `10 x 10` world, so connected clients rebuilt down to the old plain platform and never saw the expanded terrain. Startup floor size now flows through a shared `WorldDefaults.StartupFloorSize`.
- follow-up bug fix: full world snapshot application restored existing raised cells as default grass, flattening authored dirt/stone material identity on clients. Solid-cell restore now preserves the existing geometry cell material.
