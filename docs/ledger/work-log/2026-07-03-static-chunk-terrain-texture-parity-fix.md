Purpose: Record the terrain texture parity fix after hidden-face static chunk rendering became the primary world path.
Status: Completed
Scope: Static chunk terrain material catalog, startup terrain material usage, docs, and regression tests.

## Summary

After hidden-face culling moved world terrain onto static chunk meshes, terrain presentation only had explicit texture mappings for grass, dirt, and stone. The repo already contained additional legal Poly Haven terrain textures, but the static terrain material catalog treated their material ids as unknown, which made those chunks lose texture parity with the intended asset pack.

The fix expands `GeometryMaterialId` with named imported terrain materials and maps each one through `StaticTerrainMaterialCatalog`:

- `Sand` -> `library/polyhaven/sand_01/sand_01.diffuse.jpg`
- `Mud` -> `library/polyhaven/brown_mud/brown_mud.diffuse.jpg`
- `Snow` -> `library/polyhaven/snow_04/snow_04.diffuse.jpg`
- `Rock` -> `library/polyhaven/rocks_ground_08/rocks_ground_08.diffuse.jpg`
- `GrassPath` -> `library/polyhaven/grass_path_3/grass_path_3.diffuse.jpg`

Startup terrain now deterministically uses imported terrain materials on the floor through rock edges, grass-path bands, and sparse sand/mud patches, while preserving existing spawn path and corner-stone behavior.

Follow-up visual review showed two more static chunk terrain regressions:

- Merged cuboid side textures used one fixed UV square for every face, so long edges stretched and vertical faces could rotate the texture axis through `Y`.
- Static chunk terrain was drawn with back-face culling even though hidden-face culling already removed interior surfaces, making some walkable support surfaces appear transparent from clipped first-person or underside edge views.
- Compatibility cubes entered the static chunk extractor with their authoritative `GeometryRenderCell` metadata, but the extractor hardcoded every compatibility cube back to `Grass`, hiding server/map floor materials such as sand, mud, rock, and grass-path.

The mesh builder now emits face-direction-aware, world-unit tiled UVs. Static terrain chunk rendering now draws the remaining exterior faces double-sided. Compatibility cube extraction now preserves the material id from the matching geometry cell, so coarse intact floor cells render the same material that the map/server matrix owns.

## Root Cause

The hidden-face/static chunk path became the primary terrain renderer before the terrain material catalog was expanded beyond the first grass/dirt/stone parity pass. Missing catalog mappings meant imported terrain material ids could not bind real texture paths in the static mesh renderer.

A second parity issue existed one layer later: the map export included `GeometryRenderCell.Cell.Definition.MaterialId`, but `StaticWorldChunkExtractor` ignored that metadata for compatibility cubes and emitted `GeometryMaterialId.Grass` for every intact cube. This made generated sand/stone/mud floor cells appear as grass even when the authoritative geometry matrix contained the correct material ids.

## Verification

- Red test first: `dotnet test .\dream.Tests\dream.Tests.csproj --nologo --filter "StaticTerrainMaterialCatalogTests|StartupWorld_Uses_Imported_Terrain_Texture_Materials" /p:UseSharedCompilation=false` failed because material ids `3..7` had no texture and startup terrain only contained grass/dirt/stone.
- Green focused test: `dotnet test .\dream.Tests\dream.Tests.csproj --nologo --filter "StaticTerrainMaterialCatalogTests|StartupWorld_Uses_Imported_Terrain_Texture_Materials" /p:UseSharedCompilation=false`
- Broader focused test: `dotnet test .\dream.Tests\dream.Tests.csproj --nologo --filter "WorldGeneratorTerrainTests|MapGeometryFacadeTests|StaticTerrainMaterialCatalogTests|StaticWorldChunkRendererTests|StaticWorldChunkMeshBuilderTests" /p:UseSharedCompilation=false`
- Red UV test first: `dotnet test .\dream.Tests\dream.Tests.csproj --nologo --filter BuildTilesSideFaceUvsHorizontallyAndVerticallyInWorldUnits /p:UseSharedCompilation=false` failed because side-face `V` still followed the fixed square assignment.
- Green UV/static-render test: `dotnet test .\dream.Tests\dream.Tests.csproj --nologo --filter "StaticWorldChunkMeshBuilderTests|StaticWorldChunkRendererTests|StaticTerrainMaterialCatalogTests" /p:UseSharedCompilation=false`
- Red extractor test first: `dotnet test .\dream.Tests\dream.Tests.csproj --nologo --filter "FullyQualifiedName~StaticWorldChunkMeshBuilderTests" /p:UseSharedCompilation=false` failed with expected `Sand`, actual `Grass` for `ExtractorPreservesCompatibilityCubeMaterialFromGeometryCell`.
- Green export/extractor test: `dotnet test .\dream.Tests\dream.Tests.csproj --nologo --filter "FullyQualifiedName~WorldGeneratorTerrainTests|FullyQualifiedName~StaticWorldChunkMeshBuilderTests" /p:UseSharedCompilation=false`
