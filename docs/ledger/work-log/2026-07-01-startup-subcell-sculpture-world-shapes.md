Purpose: record the first startup-world pass that authors visible shapes from smaller geometry subcells.
Status: completed
Scope: startup terrain generation, default world size, geometry render/collision export, multiplayer snapshot preservation, and tests

## Summary

The startup world now uses geometry-native mini-cube blocks for authored landmarks instead of relying only on full cube stacks. The default world size increased to `56 x 56`, leaving room for the existing platform, nearby terrain, and new shape landmarks.

## What Changed

- increased `WorldDefaults.StartupFloorSize` from `40` to `56`
- added deterministic startup sculpture generation inside `WorldGenerator`
- added a stone mini-cube pyramid near the spawn approach
- added a rounded dirt boulder assembled from smaller subcell blocks
- increased authored sculpture resolution from `4 x 4 x 4` to `6 x 6 x 6` so each visible sculpture block is smaller
- changed startup generation to let sculpted cells replace overlapping coarse raised cells, preventing coarse cells from punching visual gaps into authored shapes
- added a low stone rubble cluster between the larger shapes to reduce empty space in the sculpted area
- added a deterministic fill pass for strongly supported empty sculpture subcells so visible gaps are closed with real geometry
- kept render-only visual fill scaling for authored non-fractured sculpture subcells as a smaller seam polish while collision remains exact
- batched model-backed geometry render blocks through instanced `AnimatedModel` draws so subcell landmarks do not issue one model render per mini-cube
- greedily merged static subcell collision bodies into larger cuboids, reducing the current startup sculpture collision export from roughly `44k` bodies to about `1.4k`
- preserved authored non-fractured subdivision during full world snapshot application so clients do not flatten generated mini-cube shapes back to coarse cubes
- added regression coverage for default size, subcell shape generation, render export, and client join-time preservation

## Verification

- `dotnet test .\dream.Tests\dream.Tests.csproj --nologo --filter "FullyQualifiedName~WorldGeneratorTerrainTests.StartupWorld_Uses_Finer_Denser_Subcell_Sculpture_Clusters" /p:UseSharedCompilation=false`
- `dotnet test .\dream.Tests\dream.Tests.csproj --nologo --filter "FullyQualifiedName~WorldGeneratorTerrainTests.StartupWorld_Fills_StronglySupported_SubcellVoids_In_AuthoredSculptures" /p:UseSharedCompilation=false`
- `dotnet test .\dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false`
- `dotnet build .\dream\dream.csproj --nologo /p:UseSharedCompilation=false`

## Notes

- this is intentionally still voxel/block authored geometry, not a mesh/smooth-surface system
- later work can add more authored shape predicates and material palettes without changing the render path
- future subcell-heavy content should preserve the boundary rule: semantic render blocks may be numerous, but OpenGL and Bullet consumers should batch or merge before submitting work
