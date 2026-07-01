# Startup World Expansion And Terrain Materials Implementation Plan

Status: proposed

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Expand the default startup world into a larger, more varied play space with mixed terrain materials and simple authored landmarks while staying inside the current cube-compatible geometry/render pipeline.

**Architecture:** Keep `Map` as the world façade and concentrate authored-world rules inside `WorldGenerator`, so startup variety comes from deterministic geometry/material decisions rather than scene-local hacks. Extend the existing material and cube-asset seams so intact geometry cells can select different textured cube models without changing the render/collision contract.

**Tech Stack:** C# / .NET 10, `Dream.Simulation.World`, `Dream.Simulation.World.Geometry`, `Dream.Rendering.Core`, `Dream.Rendering.Models`, existing glTF cube asset path, Poly Haven diffuse textures already stored in `dream/animations/library/polyhaven/`, xUnit, repo ledger docs.

---

## Current State

The startup world is still mostly a flat grass floor. `WorldGenerator` currently authors every geometry cell as `GeometryMaterialId.Grass`, and the compatibility/render export path always routes cells through the single `GrassTextureCube.gltf` model. That means the world can be larger, but it still looks uniform and does not express terrain zones or landmarks even though the repo now contains additional legal terrain textures.

## Non-Negotiable Rules

- Keep the current cube-compatible render/collision pipeline working.
- Keep startup world generation deterministic.
- Do not introduce scene-local world painting logic outside `WorldGenerator`.
- Do not break multiplayer snapshot/world-mutation assumptions for intact geometry cells.
- Use repo-owned, already imported legal textures only.

## Target File Map

- Create: `dream/Rendering/Models/DirtCubeAssetDefinition.cs`
- Create: `dream/Rendering/Models/StoneCubeAssetDefinition.cs`
- Create: `dream/animations/DirtTextureCube.gltf`
- Create: `dream/animations/StoneTextureCube.gltf`
- Modify: `dream/Rendering/Core/ModelAssets.cs`
- Modify: `dream/Rendering/Core/Render.cs`
- Modify: `dream/Simulation/World/Geometry/GeometryMaterialId.cs`
- Modify: `dream/Simulation/World/WorldGenerator.cs`
- Modify: `dream/Application/GameSettings.cs`
- Modify tests: `dream.Tests/Application/GameSettingsTests.cs`
- Modify tests: `dream.Tests/Simulation/World/Geometry/MapGeometryFacadeTests.cs`
- Create tests: `dream.Tests/Simulation/World/WorldGeneratorTerrainTests.cs`
- Update docs: `docs/architecture.md`, `docs/rendering-and-assets.md`
- Add work log: `docs/ledger/work-log/2026-07-01-startup-world-expansion-and-terrain-materials.md`

## Task 1: Add Terrain Material Asset Variants

**Files:**
- Create: `dream/Rendering/Models/DirtCubeAssetDefinition.cs`
- Create: `dream/Rendering/Models/StoneCubeAssetDefinition.cs`
- Create: `dream/animations/DirtTextureCube.gltf`
- Create: `dream/animations/StoneTextureCube.gltf`
- Modify: `dream/Rendering/Core/ModelAssets.cs`
- Modify: `dream/Rendering/Core/Render.cs`

- [ ] Add two cube asset-definition classes that mirror the grass cube normalization seam but point at dirt and stone textured cube models.
- [ ] Add dirt and stone glTF cube descriptors by reusing the current cube mesh/bin layout and swapping the image URIs to Poly Haven terrain textures already in the repo.
- [ ] Extend `ModelAssets` with cached `DirtCube` and `StoneCube` accessors.
- [ ] Update `Render` scale resolution so these cube variants use the same normalization path as the grass cube.

## Task 2: Expand Material Identity For Startup Terrain

**Files:**
- Modify: `dream/Simulation/World/Geometry/GeometryMaterialId.cs`
- Modify: `dream/Simulation/World/WorldGenerator.cs`

- [ ] Keep existing materials stable and add any extra terrain material ids only if the generator truly needs them.
- [ ] Add one focused material-to-model resolver inside `WorldGenerator` so compatibility cubes and render blocks select the correct textured cube model from `GeometryMaterialId`.
- [ ] Keep shader usage unchanged (`"cube"`) so the new terrain variants remain compatible with the current world renderer.

## Task 3: Author A Larger Deterministic Startup Layout

**Files:**
- Modify: `dream/Simulation/World/WorldGenerator.cs`
- Modify: `dream/Application/GameSettings.cs`

- [ ] Increase the default startup floor size to a larger but still safe value for the current renderer.
- [ ] Replace the “all grass everywhere” startup rule with deterministic zoning:
  - central playable grassland
  - dirt or path bands to break up the floor
  - stone or rocky outer accents
  - a few low raised landmarks or berms that create silhouette variety without trapping spawn flow
- [ ] Keep the geometry cube-first and avoid introducing incomplete slope/wedge dependencies in this pass.
- [ ] Keep generation logic readable by splitting helpers such as “is path”, “is rocky rim”, or “landmark height” inside `WorldGenerator`.

## Task 4: Add Focused Tests Before And After Implementation

**Files:**
- Modify: `dream.Tests/Application/GameSettingsTests.cs`
- Modify: `dream.Tests/Simulation/World/Geometry/MapGeometryFacadeTests.cs`
- Create: `dream.Tests/Simulation/World/WorldGeneratorTerrainTests.cs`

- [ ] Add a failing test for the new default floor size expectation in `GameSettings`.
- [ ] Add generator tests proving the startup world now yields mixed terrain materials instead of only grass.
- [ ] Add tests proving the authored layout creates at least one raised landmark cell above `y = 0`.
- [ ] Add or adjust geometry-export tests so compatibility cubes/render blocks keep working when a cell material is dirt or stone.

## Task 5: Update Project Docs And Ledger

**Files:**
- Modify: `docs/architecture.md`
- Modify: `docs/rendering-and-assets.md`
- Create: `docs/ledger/work-log/2026-07-01-startup-world-expansion-and-terrain-materials.md`

- [ ] Document that startup-world authorship now comes from deterministic terrain zoning inside `WorldGenerator`.
- [ ] Document the new terrain cube asset variants and which Poly Haven textures they use.
- [ ] Record implementation and verification outcomes in the work log.

## Verification

- `dotnet test .\dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false`
- `dotnet build .\dream\dream.csproj --nologo /p:UseSharedCompilation=false`

## Self-Review

- The plan stays inside one subsystem cluster: startup world generation plus terrain material selection.
- The implementation remains compatible with current cube-based rendering and geometry export.
- Tests, docs, and ledger updates are explicitly included so the change lands as a durable repo decision rather than an invisible art tweak.
