# Hidden Face Culling And Static Chunk Mesh Rendering Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace high-volume static voxel instance rendering with chunk-level static meshes that emit only externally visible faces.

**Architecture:** Keep canonical world geometry in `Map` and `Dream.Simulation.World.Geometry`; add a rendering-only chunk mesh layer under `Dream.Rendering.World` that consumes existing `GeometryRenderExport` data. The first pass should optimize static floor/startup geometry while leaving dynamic fractured/player-built objects on the current descriptor path until dirty chunk invalidation is wired.

**Tech Stack:** C#/.NET 10, OpenTK/OpenGL, xUnit, existing `GeometryRenderExport`, `WorldRenderer`, `Render`, and `FramePerformanceSnapshot` diagnostics.

---

Purpose: Define the implementation path for real hidden-face culling in `dream`.

Status: Proposed execution plan.

Scope: Static world rendering, chunk mesh generation, exposed-face extraction, render diagnostics, tests, and documentation. This does not change Bullet collision, multiplayer protocol state, player/enemy rendering, or first-person viewmodel rendering.

## Current Baseline

The current engine has partial culling only:

- `AnimatedModel.BeginRender(...)` enables OpenGL back-face culling for glTF/model rendering.
- `WorldVisibility` applies distance and camera-facing gates before world objects are submitted.
- Static startup sculpture render blocks are greedily merged, so some internal microcell surfaces disappear indirectly.

The current engine does not have full voxel hidden-face culling:

- `Render.DrawCubeFaceFans()` draws all six faces for simple cubes.
- Model-backed cube and geometry-block rendering still submits whole cube/cuboid model instances through `AnimatedModel.RenderPreparedInstanced(...)`.
- Adjacent floor cubes and adjacent geometry blocks can still pay visual cost for faces that cannot be seen.

Recent performance evidence from [2026-07-02-next-performance-optimization-options](../analysis/2026-07-02-next-performance-optimization-options.md):

- Enriched startup map export showed about `3,346` compatibility cubes and `6,264` geometry blocks in representative full-radius captures.
- Visibility still allowed roughly `4,687` to `6,264` visible geometry blocks and `1,329` to `2,579` visible compatibility cubes in full-radius views.
- Collision was later reduced separately from `9,269` descriptors to `629` bodies in [2026-07-02-global-static-collision-merge](../work-log/2026-07-02-global-static-collision-merge.md), so the next large win is visual render submission and GPU/driver work.

## Expected Performance Impact

Hidden-face culling only helps if the renderer stops drawing whole cube instances. Expected impact by implementation style:

- Existing OpenGL back-face culling only: `0-5%` overall improvement because it is already mostly present for model rendering.
- Hidden-face bookkeeping while still submitting complete cube/glTF instances: `0-10%` overall improvement and not worth doing as a standalone change.
- Chunk mesh with exposed faces only: expected `20-55%` frame-time reduction in static voxel-heavy views, with a plausible FPS increase of `25-120%` depending on whether the current frame is GPU, driver submission, or debug-overlay bound.

Important measurement rules:

- Measure with collision debug disabled, otherwise red wireframe rendering can dominate the frame and hide the world-rendering gain.
- Compare the HUD performance counters before and after with the same camera view, same floor size, same startup world, and same `Show Performance Counters` state.
- Treat this as successful if visible static voxel draw work drops materially and the dense sculpture view improves by at least `20%` in frame time.

## Design Boundaries

- Canonical geometry remains simulation-owned. Rendering must not invent collision or authoritative occupancy.
- Collision remains driven by `GeometryCollisionExport`, `WorldPhysicsBinder`, and current static/dynamic body ownership.
- Chunk meshes are render artifacts and can be rebuilt from `GeometryRenderExport`.
- Dynamic fractured cells and player-built objects can stay on the current model-backed descriptor path in the first implementation.
- Texture/material seams must preserve `GeometryMaterialId` grouping so grass, dirt, and stone can render with the correct visual material.

## File Structure

Create:

- `dream/Rendering/World/StaticVoxelRenderCell.cs`: Small render-facing record for one axis-aligned occupied visual cell or cuboid, normalized from compatibility cubes and geometry blocks.
- `dream/Rendering/World/StaticWorldChunkKey.cs`: Stable chunk coordinate key such as `(ChunkX, ChunkY, ChunkZ)`.
- `dream/Rendering/World/StaticWorldChunkBounds.cs`: Axis-aligned bounds for visibility and diagnostics.
- `dream/Rendering/World/StaticWorldChunkMesh.cs`: CPU-side mesh result containing vertices, indices, material id, bounds, and exposed-face count.
- `dream/Rendering/World/StaticWorldChunkMeshBuilder.cs`: Pure hidden-face/exposed-face builder with no OpenGL calls.
- `dream/Rendering/Core/StaticMeshBuffer.cs`: OpenGL VAO/VBO/EBO owner for one material chunk mesh.
- `dream/Rendering/World/StaticWorldChunkRenderer.cs`: Renderer-owned cache that uploads chunk meshes and draws visible chunk buffers.
- `dream.Tests/Rendering/World/StaticWorldChunkMeshBuilderTests.cs`: Unit tests for exposed-face extraction and chunk mesh counts.
- `dream.Tests/Rendering/World/StaticWorldChunkKeyTests.cs`: Unit tests for stable chunk coordinate mapping.

Modify:

- `dream/Rendering/World/WorldRenderer.cs`: Route static chunk meshes before falling back to current compatibility cube/block rendering.
- `dream/Rendering/World/WorldRenderStats.cs`: Add static chunk counters and exposed-face/triangle counters.
- `dream/Rendering/Hud/FramePerformanceSnapshot.cs`: Add optional static chunk counters.
- `dream/Rendering/Core/Render.cs`: Expose a low-level static mesh draw seam or delegate that lets `StaticWorldChunkRenderer` use shared shader/camera state without going through cube instances.
- `dream/Simulation/Runtime/WorldSessionPresentationState.cs`: Carry render stats only if needed; do not move renderer-owned buffers into runtime state.
- `docs/rendering-and-assets.md`: Document that static world geometry now uses chunk meshes with exposed faces only.
- `docs/ledger/work-log/2026-07-03-hidden-face-culling-and-static-chunk-mesh-rendering.md`: Record implementation evidence after completion.

## Mesh Contract

`StaticVoxelRenderCell` should represent a render-only occupied cuboid:

```csharp
namespace Dream.Rendering.World;

using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;

public readonly record struct StaticVoxelRenderCell(
    Vector3 Center,
    Vector3 Size,
    GeometryMaterialId MaterialId)
{
    public Vector3 Min => Center - Size / 2f;
    public Vector3 Max => Center + Size / 2f;
}
```

`StaticWorldChunkKey` should map world-space positions into deterministic chunk buckets:

```csharp
namespace Dream.Rendering.World;

using OpenTK.Mathematics;

public readonly record struct StaticWorldChunkKey(int X, int Y, int Z)
{
    public const int DefaultChunkSideInWorldUnits = 16;

    public static StaticWorldChunkKey FromWorldPosition(Vector3 position, int chunkSide = DefaultChunkSideInWorldUnits)
    {
        return new StaticWorldChunkKey(
            FloorDiv(position.X, chunkSide),
            FloorDiv(position.Y, chunkSide),
            FloorDiv(position.Z, chunkSide));
    }

    private static int FloorDiv(float value, int divisor)
    {
        return (int)MathF.Floor(value / divisor);
    }
}
```

`StaticWorldChunkMesh` should expose diagnostics directly:

```csharp
namespace Dream.Rendering.World;

using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;

public sealed record StaticWorldChunkMesh(
    StaticWorldChunkKey Key,
    GeometryMaterialId MaterialId,
    IReadOnlyList<StaticWorldVertex> Vertices,
    IReadOnlyList<uint> Indices,
    StaticWorldChunkBounds Bounds,
    int SourceCellCount,
    int ExposedFaceCount)
{
    public int TriangleCount => Indices.Count / 3;
}

public readonly record struct StaticWorldVertex(
    Vector3 Position,
    Vector3 Normal,
    Vector2 TexCoord);
```

`GeometryRenderExport` currently uses this constructor shape:

```csharp
public readonly record struct GeometryRenderExport(
    IReadOnlyList<GeometryCellKey> DirtyCells,
    IReadOnlyList<GeometryRenderCell> Cells,
    IReadOnlyList<GeometryRenderBlockDescriptor> Blocks,
    IReadOnlyList<Cube> CompatibilityCubes);
```

Compatibility `Cube` construction currently uses `new Cube(isEmpty: false, isSolid: true)` plus property assignment for `Position`, `Animation`, and `ShaderName`.

The first pass originally targeted one mesh per chunk per material to avoid introducing a texture atlas. The completed texture-parity pass refines that rule to one mesh per chunk per material role: grass splits exposed faces into top, side, and bottom role meshes so each face can bind the same terrain texture used by the old glTF cube path, while dirt and stone keep a single texture role.

## Hidden-Face Algorithm

The first implementation should be conservative and exact for axis-aligned cuboids from the current export:

1. Normalize compatibility cubes and geometry render blocks into `StaticVoxelRenderCell`.
2. Group cells by `StaticWorldChunkKey`.
3. Within each chunk, emit candidate faces for each cell's six sides.
4. Suppress a candidate face when another cell has a touching coplanar face that fully covers the candidate face.
5. Keep faces at material boundaries suppressed too because adjacent solid interiors are hidden even when their materials differ.
6. Do not attempt partial-face subdivision in the first slice; if a larger face is partially covered by smaller neighbors, emit the larger face unchanged and record a diagnostic counter for partial adjacency.
7. Add partial-face subdivision in a follow-up task if the first slice leaves visible overdraw in dense mixed-size sculptures.

This intentionally balances correctness and first PoC speed. The current static startup sculptures are already globally greedily merged, so exact full-face adjacency should remove the largest invisible surfaces without requiring a full constructive solid geometry pass.

## Task 1: Pure Chunk Key And Cell Normalization

**Files:**

- Create: `dream/Rendering/World/StaticVoxelRenderCell.cs`
- Create: `dream/Rendering/World/StaticWorldChunkKey.cs`
- Create: `dream.Tests/Rendering/World/StaticWorldChunkKeyTests.cs`

- [ ] **Step 1: Write chunk key tests**

Add tests:

```csharp
using Dream.Rendering.World;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.World;

public class StaticWorldChunkKeyTests
{
    [Theory]
    [InlineData(0f, 0)]
    [InlineData(15.99f, 0)]
    [InlineData(16f, 1)]
    [InlineData(-0.01f, -1)]
    [InlineData(-16f, -1)]
    [InlineData(-16.01f, -2)]
    public void FromWorldPositionUsesFloorDivision(float x, int expectedChunkX)
    {
        StaticWorldChunkKey key = StaticWorldChunkKey.FromWorldPosition(new Vector3(x, 0f, 0f));

        Assert.Equal(expectedChunkX, key.X);
        Assert.Equal(0, key.Y);
        Assert.Equal(0, key.Z);
    }

    [Fact]
    public void FromWorldPositionMapsEachAxisIndependently()
    {
        StaticWorldChunkKey key = StaticWorldChunkKey.FromWorldPosition(new Vector3(17f, 33f, -1f));

        Assert.Equal(new StaticWorldChunkKey(1, 2, -1), key);
    }
}
```

- [ ] **Step 2: Run the focused failing test**

Run:

```powershell
dotnet test dream.Tests\dream.Tests.csproj --nologo --filter StaticWorldChunkKeyTests /p:UseSharedCompilation=false
```

Expected before implementation: build fails because `StaticWorldChunkKey` does not exist.

- [ ] **Step 3: Implement chunk key and render cell records**

Create the files with the contracts shown in the Mesh Contract section.

- [ ] **Step 4: Run the focused passing test**

Run:

```powershell
dotnet test dream.Tests\dream.Tests.csproj --nologo --filter StaticWorldChunkKeyTests /p:UseSharedCompilation=false
```

Expected after implementation: all `StaticWorldChunkKeyTests` pass.

- [ ] **Step 5: Commit**

```powershell
git add dream\Rendering\World\StaticVoxelRenderCell.cs dream\Rendering\World\StaticWorldChunkKey.cs dream.Tests\Rendering\World\StaticWorldChunkKeyTests.cs
git commit -m "feat: add static world chunk coordinates"
```

## Task 2: Exposed Face Mesh Builder

**Files:**

- Create: `dream/Rendering/World/StaticWorldChunkBounds.cs`
- Create: `dream/Rendering/World/StaticWorldChunkMesh.cs`
- Create: `dream/Rendering/World/StaticWorldChunkMeshBuilder.cs`
- Create: `dream.Tests/Rendering/World/StaticWorldChunkMeshBuilderTests.cs`

- [ ] **Step 1: Write single-cube and adjacent-cube tests**

Add tests:

```csharp
using Dream.Rendering.World;
using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.World;

public class StaticWorldChunkMeshBuilderTests
{
    [Fact]
    public void BuildEmitsSixFacesForSingleCube()
    {
        StaticVoxelRenderCell[] cells =
        [
            new(new Vector3(0.5f, 0.5f, 0.5f), Vector3.One, GeometryMaterialId.Grass)
        ];

        StaticWorldChunkMesh mesh = StaticWorldChunkMeshBuilder.Build(
            new StaticWorldChunkKey(0, 0, 0),
            GeometryMaterialId.Grass,
            cells);

        Assert.Equal(1, mesh.SourceCellCount);
        Assert.Equal(6, mesh.ExposedFaceCount);
        Assert.Equal(24, mesh.Vertices.Count);
        Assert.Equal(36, mesh.Indices.Count);
        Assert.Equal(12, mesh.TriangleCount);
    }

    [Fact]
    public void BuildSuppressesSharedFaceForAdjacentCubes()
    {
        StaticVoxelRenderCell[] cells =
        [
            new(new Vector3(0.5f, 0.5f, 0.5f), Vector3.One, GeometryMaterialId.Grass),
            new(new Vector3(1.5f, 0.5f, 0.5f), Vector3.One, GeometryMaterialId.Grass)
        ];

        StaticWorldChunkMesh mesh = StaticWorldChunkMeshBuilder.Build(
            new StaticWorldChunkKey(0, 0, 0),
            GeometryMaterialId.Grass,
            cells);

        Assert.Equal(2, mesh.SourceCellCount);
        Assert.Equal(10, mesh.ExposedFaceCount);
        Assert.Equal(40, mesh.Vertices.Count);
        Assert.Equal(60, mesh.Indices.Count);
        Assert.Equal(20, mesh.TriangleCount);
    }

    [Fact]
    public void BuildSuppressesInteriorFacesAcrossDifferentMaterials()
    {
        StaticVoxelRenderCell[] grassCells =
        [
            new(new Vector3(0.5f, 0.5f, 0.5f), Vector3.One, GeometryMaterialId.Grass)
        ];
        StaticVoxelRenderCell[] allCells =
        [
            grassCells[0],
            new(new Vector3(1.5f, 0.5f, 0.5f), Vector3.One, GeometryMaterialId.Stone)
        ];

        StaticWorldChunkMesh mesh = StaticWorldChunkMeshBuilder.Build(
            new StaticWorldChunkKey(0, 0, 0),
            GeometryMaterialId.Grass,
            grassCells,
            allCells);

        Assert.Equal(5, mesh.ExposedFaceCount);
    }
}
```

- [ ] **Step 2: Run the focused failing test**

Run:

```powershell
dotnet test dream.Tests\dream.Tests.csproj --nologo --filter StaticWorldChunkMeshBuilderTests /p:UseSharedCompilation=false
```

Expected before implementation: build fails because `StaticWorldChunkMeshBuilder` does not exist.

- [ ] **Step 3: Implement mesh records and face builder**

Implement `StaticWorldChunkMeshBuilder.Build(...)` with two overloads:

```csharp
public static StaticWorldChunkMesh Build(
    StaticWorldChunkKey key,
    GeometryMaterialId materialId,
    IReadOnlyList<StaticVoxelRenderCell> materialCells)
{
    return Build(key, materialId, materialCells, materialCells);
}

public static StaticWorldChunkMesh Build(
    StaticWorldChunkKey key,
    GeometryMaterialId materialId,
    IReadOnlyList<StaticVoxelRenderCell> materialCells,
    IReadOnlyList<StaticVoxelRenderCell> occluderCells)
```

For each material cell, emit `+X`, `-X`, `+Y`, `-Y`, `+Z`, and `-Z` faces only when no occluder cell fully covers that face. Use exact or epsilon-based comparisons around `0.0001f` to avoid float cracks from descriptor math.

Each emitted face should append four vertices and six indices. Use UVs `(0,0)`, `(1,0)`, `(1,1)`, `(0,1)` in a stable winding order that works with back-face culling.

- [ ] **Step 4: Run the focused passing test**

Run:

```powershell
dotnet test dream.Tests\dream.Tests.csproj --nologo --filter StaticWorldChunkMeshBuilderTests /p:UseSharedCompilation=false
```

Expected after implementation: all mesh builder tests pass.

- [ ] **Step 5: Commit**

```powershell
git add dream\Rendering\World\StaticWorldChunkBounds.cs dream\Rendering\World\StaticWorldChunkMesh.cs dream\Rendering\World\StaticWorldChunkMeshBuilder.cs dream.Tests\Rendering\World\StaticWorldChunkMeshBuilderTests.cs
git commit -m "feat: build static world meshes with hidden faces culled"
```

## Task 3: Static Chunk Extraction From Geometry Render Export

**Files:**

- Create: `dream/Rendering/World/StaticWorldChunkExtractor.cs`
- Test: `dream.Tests/Rendering/World/StaticWorldChunkMeshBuilderTests.cs`

- [ ] **Step 1: Add extraction tests**

Add tests that build static cells from one compatibility cube and one geometry block:

```csharp
[Fact]
public void ExtractorIncludesCompatibilityCubesAndGeometryBlocks()
{
    Cube cube = new(isEmpty: false, isSolid: true)
    {
        Position = new Vector3(0.5f, 0.5f, 0.5f),
        Animation = null!,
        ShaderName = "default"
    };
    GeometryRenderBlockDescriptor block = new(
        new GeometryCellKey(1, 0, 0),
        new Vector3(1.5f, 0.5f, 0.5f),
        1f,
        GeometryMaterialId.Stone,
        Animation: null,
        ShaderName: "cube");
    GeometryRenderExport export = new(
        DirtyCells: [],
        Cells: [],
        Blocks: [block],
        CompatibilityCubes: [cube]);

    IReadOnlyList<StaticVoxelRenderCell> cells = StaticWorldChunkExtractor.ExtractCells(export);

    Assert.Equal(2, cells.Count);
    Assert.Contains(cells, cell => cell.MaterialId == GeometryMaterialId.Grass);
    Assert.Contains(cells, cell => cell.MaterialId == GeometryMaterialId.Stone);
}
```

- [ ] **Step 2: Run the focused failing test**

Run:

```powershell
dotnet test dream.Tests\dream.Tests.csproj --nologo --filter ExtractorIncludesCompatibilityCubesAndGeometryBlocks /p:UseSharedCompilation=false
```

Expected before implementation: build fails because `StaticWorldChunkExtractor` does not exist.

- [ ] **Step 3: Implement the extractor**

`StaticWorldChunkExtractor.ExtractCells(GeometryRenderExport export)` should:

- Convert each compatibility cube to a `StaticVoxelRenderCell`.
- Convert each `GeometryRenderBlockDescriptor` to a `StaticVoxelRenderCell` using `Center`, `VisualSize`, and `MaterialId`.
- Preserve material ids from geometry blocks.
- Use the grass material for legacy compatibility cubes unless the cube path already exposes material metadata.

- [ ] **Step 4: Run focused tests**

Run:

```powershell
dotnet test dream.Tests\dream.Tests.csproj --nologo --filter "StaticWorldChunkMeshBuilderTests|StaticWorldChunkKeyTests" /p:UseSharedCompilation=false
```

Expected after implementation: all selected tests pass.

- [ ] **Step 5: Commit**

```powershell
git add dream\Rendering\World\StaticWorldChunkExtractor.cs dream.Tests\Rendering\World\StaticWorldChunkMeshBuilderTests.cs
git commit -m "feat: extract static render cells from world export"
```

## Task 4: OpenGL Static Mesh Buffer

**Files:**

- Create: `dream/Rendering/Core/StaticMeshBuffer.cs`
- Modify: `dream/Rendering/Core/Render.cs`
- Test: `dream.Tests/Rendering/World/StaticWorldChunkMeshBuilderTests.cs`

- [ ] **Step 1: Add a non-OpenGL contract test**

Add a test confirming mesh indices are stable and suitable for indexed drawing:

```csharp
[Fact]
public void BuildUsesSequentialIndicesInsideVertexRange()
{
    StaticVoxelRenderCell[] cells =
    [
        new(new Vector3(0.5f, 0.5f, 0.5f), Vector3.One, GeometryMaterialId.Grass)
    ];

    StaticWorldChunkMesh mesh = StaticWorldChunkMeshBuilder.Build(
        new StaticWorldChunkKey(0, 0, 0),
        GeometryMaterialId.Grass,
        cells);

    uint maxIndex = mesh.Indices.Max();

    Assert.True(maxIndex < mesh.Vertices.Count);
    Assert.All(mesh.Indices, index => Assert.True(index < mesh.Vertices.Count));
}
```

- [ ] **Step 2: Run the focused test**

Run:

```powershell
dotnet test dream.Tests\dream.Tests.csproj --nologo --filter BuildUsesSequentialIndicesInsideVertexRange /p:UseSharedCompilation=false
```

Expected: the test passes after Task 2. This protects the buffer upload assumptions before adding OpenGL code.

- [ ] **Step 3: Implement `StaticMeshBuffer`**

`StaticMeshBuffer` should:

- Own `VAO`, `VBO`, and `EBO`.
- Upload interleaved `StaticWorldVertex` data.
- Enable attributes for position, normal, and UV.
- Draw with `GL.DrawElements(PrimitiveType.Triangles, indexCount, DrawElementsType.UnsignedInt, 0)`.
- Implement `IDisposable` and delete GL resources.

- [ ] **Step 4: Add a `Render.DrawStaticMesh(...)` seam**

`Render.DrawStaticMesh(Camera camera, StaticMeshBuffer buffer, GeometryMaterialId materialId, StaticTerrainMaterialRole materialRole)` should:

- Use the world/cube shader selected for terrain.
- Set view and projection from `camera`.
- Set model to `Matrix4.Identity`.
- Bind the correct terrain material texture for the material role, with color fallback only if the expected asset is missing.
- Call `buffer.Draw()`.

Keep this as a narrow seam. Do not move chunk cache ownership into `Render`.

- [ ] **Step 5: Build**

Run:

```powershell
dotnet build dream\dream.csproj --nologo /p:UseSharedCompilation=false
```

Expected: build succeeds with the known package warnings.

- [ ] **Step 6: Commit**

```powershell
git add dream\Rendering\Core\StaticMeshBuffer.cs dream\Rendering\Core\Render.cs dream.Tests\Rendering\World\StaticWorldChunkMeshBuilderTests.cs
git commit -m "feat: add static world mesh draw buffer"
```

## Task 5: Chunk Renderer Cache And WorldRenderer Integration

**Files:**

- Create: `dream/Rendering/World/StaticWorldChunkRenderer.cs`
- Modify: `dream/Rendering/World/WorldRenderer.cs`
- Modify: `dream/Rendering/World/WorldRenderStats.cs`
- Modify: `dream/Rendering/Hud/FramePerformanceSnapshot.cs`
- Test: `dream.Tests/Rendering/World/WorldRenderBatchPlannerTests.cs` or new `dream.Tests/Rendering/World/StaticWorldChunkRendererTests.cs`

- [ ] **Step 1: Add a pure cache-planning test**

Create `StaticWorldChunkRendererTests` with no OpenGL calls:

```csharp
using Dream.Rendering.World;
using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.World;

public class StaticWorldChunkRendererTests
{
    [Fact]
    public void PlanGroupsMeshesByChunkAndMaterial()
    {
        StaticVoxelRenderCell[] cells =
        [
            new(new Vector3(0.5f, 0.5f, 0.5f), Vector3.One, GeometryMaterialId.Grass),
            new(new Vector3(1.5f, 0.5f, 0.5f), Vector3.One, GeometryMaterialId.Stone)
        ];

        IReadOnlyList<StaticWorldChunkMesh> meshes = StaticWorldChunkRenderer.PlanMeshes(cells);

        Assert.Equal(2, meshes.Count);
        Assert.Contains(meshes, mesh => mesh.MaterialId == GeometryMaterialId.Grass);
        Assert.Contains(meshes, mesh => mesh.MaterialId == GeometryMaterialId.Stone);
    }
}
```

- [ ] **Step 2: Run the focused failing test**

Run:

```powershell
dotnet test dream.Tests\dream.Tests.csproj --nologo --filter StaticWorldChunkRendererTests /p:UseSharedCompilation=false
```

Expected before implementation: build fails because `StaticWorldChunkRenderer` does not exist.

- [ ] **Step 3: Implement cache planning and renderer shell**

`StaticWorldChunkRenderer` should:

- Expose pure static `PlanMeshes(...)` for tests.
- Keep instance-owned `Dictionary<(StaticWorldChunkKey, GeometryMaterialId), StaticMeshBuffer>`.
- Rebuild buffers only when the source world export version changes. If no dirty version exists yet, start with a conservative hash/count signature from cell center, size, and material.
- Frustum/radius filter chunk bounds before drawing.
- Return stats for visible chunk count, mesh count, exposed face count, and triangle count.

- [ ] **Step 4: Route `WorldRenderer.DrawWorld(Camera, GeometryRenderExport)` through chunks**

Initial integration rule:

- Extract static cells from the export.
- Draw chunk meshes for those static cells.
- Keep current compatibility cube/block fallback behind a debug flag or temporary setting until visual parity is confirmed.
- Do not draw both chunk meshes and legacy instances by default, or FPS comparisons will be invalid.

- [ ] **Step 5: Extend stats and HUD counters**

Add fields to `WorldRenderStats`:

```csharp
int VisibleStaticChunks
int StaticChunkMeshCount
int StaticChunkExposedFaces
int StaticChunkTriangles
```

Thread those into `FramePerformanceSnapshot` and the HUD performance display.

- [ ] **Step 6: Run tests and build**

Run:

```powershell
dotnet test dream.Tests\dream.Tests.csproj --nologo --filter "StaticWorldChunkRendererTests|WorldRenderBatchPlannerTests|FramePerformanceSnapshotTests" /p:UseSharedCompilation=false
dotnet build dream\dream.csproj --nologo /p:UseSharedCompilation=false
```

Expected: tests pass; build succeeds with known package warnings.

- [ ] **Step 7: Commit**

```powershell
git add dream\Rendering\World\StaticWorldChunkRenderer.cs dream\Rendering\World\WorldRenderer.cs dream\Rendering\World\WorldRenderStats.cs dream\Rendering\Hud\FramePerformanceSnapshot.cs dream.Tests\Rendering\World\StaticWorldChunkRendererTests.cs
git commit -m "feat: render static world chunks with hidden faces culled"
```

## Task 6: Visual Parity And Performance Measurement

**Files:**

- Modify: `docs/rendering-and-assets.md`
- Create: `docs/ledger/work-log/2026-07-03-hidden-face-culling-and-static-chunk-mesh-rendering.md`

- [ ] **Step 1: Run full tests**

Run:

```powershell
dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false
```

Expected: all tests pass.

- [ ] **Step 2: Build the desktop app**

Run:

```powershell
dotnet build dream\dream.csproj --nologo /p:UseSharedCompilation=false
```

Expected: build succeeds with known package warnings. If `MSB3027` or `MSB3021` occurs while copying `dream.exe`, stop the running game process and rerun the command.

- [ ] **Step 3: Capture performance counters**

Run the game and enable `Show Performance Counters`. Capture at least these two camera positions:

- Spawn-facing view toward the large map/sculpture area.
- Dense sculpture close-up view where FPS previously dropped heavily.

Record:

- FPS before and after if a previous run is available.
- render-export capture time
- visible compatibility cube count
- visible geometry block count
- visible static chunk count
- static exposed face count
- static triangle count
- geometry batch count
- collision body count

- [ ] **Step 4: Update rendering docs**

In `docs/rendering-and-assets.md`, update the cube/world rendering section with:

- Static terrain/sculptures now render through chunk meshes.
- Chunk meshes emit exposed faces only.
- Dynamic fractured/player-built pieces still use the legacy descriptor path until dirty chunk invalidation is implemented.
- Collision remains canonical and independent from render mesh generation.

- [ ] **Step 5: Add work-log evidence**

Create the dated work-log file with measured values copied from Step 3. The values below are an example structure, not acceptable final values:

```markdown
Purpose: Record the hidden-face culling and static chunk mesh rendering implementation.

Status: Completed

Scope: Static world render mesh generation, exposed-face culling, renderer integration, diagnostics, and verification.

## Summary

Implemented static world chunk meshes for floor/startup geometry. Static meshes are generated from canonical render export data and suppress hidden faces before reaching OpenGL.

## Evidence

- Before: FPS `50`, visible geometry blocks `6264`, static chunk triangles `0`
- After: FPS `78`, visible geometry blocks `0`, visible static chunks `18`, static chunk triangles `14820`
- Frame-time delta: from `20.00 ms` to `12.82 ms`, `35.9%` faster

## Verification

- `dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false`
- `dotnet build dream\dream.csproj --nologo /p:UseSharedCompilation=false`
```

Replace the example numbers with the measured values from Step 3 before committing. Do not copy the example numbers unless they match the actual implementation run.

- [ ] **Step 6: Commit docs and final integration**

```powershell
git add docs\rendering-and-assets.md docs\ledger\work-log\2026-07-03-hidden-face-culling-and-static-chunk-mesh-rendering.md
git commit -m "docs: record hidden face culling renderer results"
```

## Risks And Guards

- **Texture stretching:** Large generated faces can stretch texture UVs. Use per-face UVs scaled by world dimensions if grass/dirt/stone look smeared.
- **Material seams:** One mesh per material role per chunk avoids texture atlas work but can keep multiple draw calls. This is acceptable for the first pass because it preserves grass top/side/bottom texture parity without atlas packing.
- **Partial adjacency:** If a large cuboid face is partly covered by smaller cuboids, the first pass may keep the large face. This is visually safe but not maximally optimal.
- **Chunk invalidation:** Dynamic edits must not silently leave stale static mesh buffers. Keep dynamic edits on the old path until dirty chunk invalidation is implemented.
- **Debug overlay masking:** Collision wireframes can dominate frame cost and should be disabled for performance comparison.

## Follow-Up Plan After First Slice

Once static chunk meshes are stable:

1. Add chunk dirty-version tracking in `Map` so changed chunks rebuild without hashing the full export.
2. Move dynamic fracture rubble into chunk meshes after edits settle.
3. Add exact partial-face subdivision for mixed-size adjacent cuboids.
4. Add texture-array or atlas support so chunk materials can share fewer draw calls.
5. Add frustum-plane culling for chunk bounds instead of only radius/front visibility.

## Completion Criteria

- Static floor/startup voxel geometry renders through chunk meshes by default.
- Hidden internal faces are not emitted for full-face adjacent cells.
- HUD performance counters expose static chunk and exposed-face counts.
- Collision behavior remains unchanged.
- Full test suite passes.
- Desktop build succeeds with known warnings.
- A work-log records before/after performance counters and percentage impact.
