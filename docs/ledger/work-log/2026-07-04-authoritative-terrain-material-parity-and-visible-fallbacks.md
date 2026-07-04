Purpose: Record the terrain-material parity fix for authoritative world rendering after the `issues.txt` audit highlighted transparent and mismatched blocks.
Status: Completed
Scope: Static chunk extraction/signatures, live compatibility cube material ownership, imported-terrain fallback rendering, renderer-visible fallback behavior, and focused regression coverage.

## Summary

The parity issue came from two renderer-local assumptions:

- static chunk extraction still defaulted unmapped compatibility cubes back to `Grass`
- the live compatibility cube path still depended on prebound model assets instead of on the authoritative terrain material id

That combination meant imported terrain materials such as `Sand`, `Mud`, `Rock`, and `GrassPath` could drift away from the server/map material matrix or fall back into non-authoritative visuals.

## Completed Work

- Added `Cube.MaterialId` so live compatibility cubes keep their authoritative `GeometryMaterialId` directly.
- Updated `WorldGenerator.CreateCompatibilityCube(...)` to stamp the cube material id from the authoritative geometry cell.
- Narrowed model-backed compatibility cube rendering to the explicit glTF-backed materials only:
  - `Grass`
  - `Dirt`
  - `Stone`
- Removed the static chunk extractor grass default. When a render-cell mapping is absent, chunk extraction now falls back to the compatibility cube's own authoritative material id.
- Updated static chunk export signatures so compatibility cubes always contribute a material id, even when the render-cell mapping is absent.
- Replaced the old transparent live-cube fallback with a terrain-material fallback draw path that binds the same material catalog used by static chunks.
- Changed unknown material fallback visuals to explicit magenta instead of an invisible or neutral-looking path, so future parity gaps are obvious during testing.

## Verification

- `dotnet test dream.Tests\dream.Tests.csproj --filter "FullyQualifiedName~StaticWorldChunkRendererTests|FullyQualifiedName~StaticTerrainMaterialCatalogTests|FullyQualifiedName~WorldGeneratorTerrainTests" --nologo /p:UseSharedCompilation=false`
- `dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false`
- `dotnet build dream\dream.csproj --nologo /p:UseSharedCompilation=false`

## Notes

- This change preserves the static-vs-dynamic ownership split. Static chunk meshes remain the primary terrain path, while moving/live compatibility cubes now render through the same authoritative material contract instead of through renderer-local defaults.
- The visible fallback is a guardrail, not a new content path. Known terrain material ids should continue to resolve to real textures.
