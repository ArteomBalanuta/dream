# Model Size Normalization And Asset Units Implementation Plan

Status: proposed

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make imported world actors use explicit cube-relative sizing so players are `2` cubes tall by `1` cube wide/deep and future glTF assets have predictable scale rules.

**Architecture:** Keep the existing single-project structure and extend the current `Dream.Rendering.Models` asset-definition seam. Add measured-dimension metadata and focused tests first, then replace ad hoc visual scale constants with normalization-derived values used by both local and remote actor render paths.

**Tech Stack:** C# / .NET 10, OpenTK matrices, SharpGLTF bounds inspection, xUnit, existing `Dream.Rendering.Models` and `Dream.Rendering.Actors` namespaces.

---

## Current State

`WolfAssetDefinition` already uses `WorldAssetNormalization` with authored height and target world height. `PlayerModelAssetDefinition` still uses a raw `VisualScale = 0.85f` and a `BodyCenterToFootOffset`, so the player model target size is not encoded in cube-unit terms.

That mismatch makes player size, grounding, and multiplayer presentation easier to regress. Local and remote player rendering share `PlayerRenderState`, which is good. The missing piece is explicit model-size metadata and tests that prove the scale produces the intended cube-relative envelope.

## Target Rules

- `1.0` world unit equals one cube side.
- Player target height is `2.0` world units.
- Player neutral standing width and depth should fit inside `1.0` world unit.
- glTF authored dimensions must be measured or documented in the asset definition.
- World actor scaling must come from asset metadata, not renderer-local constants.
- Grounding must be expressed as asset metadata or derived from normalized bounds.
- First-person viewmodels are excluded from this world-actor size policy because they are camera-relative presentation assets.

## Target File Map

- Modify: `dream/Rendering/Models/WorldAssetNormalization.cs`
- Create: `dream/Rendering/Models/WorldAssetDimensions.cs`
- Create: `dream/Rendering/Models/WorldAssetBounds.cs`
- Modify: `dream/Rendering/Models/PlayerModelAssetDefinition.cs`
- Modify: `dream/Rendering/Models/WolfAssetDefinition.cs`
- Modify: `dream/Rendering/Actors/PlayerRenderState.cs`
- Modify: `dream.Tests/Rendering/Models/WorldAssetNormalizationTests.cs`
- Modify: `dream.Tests/Rendering/Models/PlayerModelAssetDefinitionTests.cs`
- Modify: `dream.Tests/Rendering/Models/WolfAssetDefinitionTests.cs`
- Modify: `dream.Tests/Rendering/Actors/PlayerRenderStateTests.cs`
- Create: `dream.Tests/Rendering/Models/WorldAssetBoundsTests.cs`
- Update docs after implementation: `docs/rendering-and-assets.md`

## Task 1: Add Cube-Relative Dimension Metadata

- [ ] **Step 1: Add a dimension record**

Create `dream/Rendering/Models/WorldAssetDimensions.cs`:

```csharp
namespace Dream.Rendering.Models;

public readonly record struct WorldAssetDimensions(
    float Height,
    float Width,
    float Depth)
{
    public static WorldAssetDimensions UnitCube => new(1f, 1f, 1f);

    public bool HasPositiveVolume =>
        Height > 0f &&
        Width > 0f &&
        Depth > 0f;
}
```

- [ ] **Step 2: Add a reproducible glTF bounds helper**

Create `dream/Rendering/Models/WorldAssetBounds.cs`:

```csharp
using OpenTK.Mathematics;
using SharpGLTF.Schema2;

namespace Dream.Rendering.Models;

public static class WorldAssetBounds
{
    public static WorldAssetDimensions MeasureDimensions(string modelPath)
    {
        ModelRoot model = ModelRoot.Load(modelPath);
        bool hasAnyPosition = false;
        Vector3 min = new(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        Vector3 max = new(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);

        foreach (Node node in SceneNodeTraversal.EnumerateRenderableNodes(model.DefaultScene.VisualChildren))
        {
            Matrix4 nodeWorld = node.WorldMatrix.ToOpenTK();
            foreach (MeshPrimitive primitive in node.Mesh!.Primitives)
            {
                var positions = primitive.GetVertexAccessor("POSITION").AsVector3Array();
                foreach (System.Numerics.Vector3 position in positions)
                {
                    Vector3 transformed = Vector3.TransformPosition(
                        new Vector3(position.X, position.Y, position.Z),
                        nodeWorld);

                    min = Vector3.ComponentMin(min, transformed);
                    max = Vector3.ComponentMax(max, transformed);
                    hasAnyPosition = true;
                }
            }
        }

        if (!hasAnyPosition)
        {
            return new WorldAssetDimensions(0f, 0f, 0f);
        }

        Vector3 size = max - min;
        return new WorldAssetDimensions(size.Y, size.X, size.Z);
    }
}
```

- [ ] **Step 3: Test the bounds helper against known assets**

Create `dream.Tests/Rendering/Models/WorldAssetBoundsTests.cs`:

```csharp
using Xunit;
using Dream.Rendering.Models;

namespace Dream.Tests.Rendering.Models;

public class WorldAssetBoundsTests
{
    [Fact]
    public void MeasureDimensions_Returns_Positive_Player_Bounds()
    {
        WorldAssetDimensions dimensions = WorldAssetBounds.MeasureDimensions(PlayerModelAssetDefinition.ModelPath);

        Assert.True(dimensions.HasPositiveVolume);
    }

    [Fact]
    public void MeasureDimensions_Returns_Positive_Wolf_Bounds()
    {
        WorldAssetDimensions dimensions = WorldAssetBounds.MeasureDimensions(WolfAssetDefinition.ModelPath);

        Assert.True(dimensions.HasPositiveVolume);
    }
}
```

- [ ] **Step 4: Extend normalization metadata**

Modify `dream/Rendering/Models/WorldAssetNormalization.cs` so it can preserve the existing height-only path while supporting full dimensions:

```csharp
namespace Dream.Rendering.Models;

public readonly record struct WorldAssetNormalization(
    float AuthoredHeight,
    float TargetWorldHeight)
{
    public WorldAssetNormalization(
        WorldAssetDimensions authoredDimensions,
        WorldAssetDimensions targetDimensions)
        : this(authoredDimensions.Height, targetDimensions.Height)
    {
        AuthoredDimensions = authoredDimensions;
        TargetDimensions = targetDimensions;
    }

    public WorldAssetDimensions? AuthoredDimensions { get; }

    public WorldAssetDimensions? TargetDimensions { get; }

    public float ResolveUniformScale()
    {
        if (AuthoredHeight <= 0f)
        {
            return 1f;
        }

        return TargetWorldHeight / AuthoredHeight;
    }

    public WorldAssetDimensions ResolveScaledDimensions()
    {
        if (AuthoredDimensions is not WorldAssetDimensions authored)
        {
            return new WorldAssetDimensions(TargetWorldHeight, TargetWorldHeight, TargetWorldHeight);
        }

        float scale = ResolveUniformScale();
        return new WorldAssetDimensions(
            authored.Height * scale,
            authored.Width * scale,
            authored.Depth * scale);
    }
}
```

- [ ] **Step 5: Add tests for dimension normalization**

Extend `dream.Tests/Rendering/Models/WorldAssetNormalizationTests.cs`:

```csharp
[Fact]
public void ResolveScaledDimensions_Preserves_UniformScale_From_TargetHeight()
{
    WorldAssetNormalization normalization = new(
        new WorldAssetDimensions(height: 4f, width: 2f, depth: 1f),
        new WorldAssetDimensions(height: 2f, width: 1f, depth: 1f));

    WorldAssetDimensions scaled = normalization.ResolveScaledDimensions();

    Assert.Equal(2f, scaled.Height, 3);
    Assert.Equal(1f, scaled.Width, 3);
    Assert.Equal(0.5f, scaled.Depth, 3);
}
```

- [ ] **Step 6: Run focused tests**

Run:

```powershell
dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false --filter "FullyQualifiedName~WorldAssetNormalizationTests|FullyQualifiedName~WorldAssetBoundsTests"
```

Expected: pass.

## Task 2: Normalize Player Model To Cube Targets

- [ ] **Step 1: Record player target dimensions**

Modify `dream/Rendering/Models/PlayerModelAssetDefinition.cs`:

```csharp
public static WorldAssetDimensions TargetWorldDimensions => new(
    height: 2.0f,
    width: 1.0f,
    depth: 1.0f);
```

- [ ] **Step 2: Replace raw scale with normalization**

Use the reproducible bounds helper from Task 1 for authored dimensions:

```csharp
public static WorldAssetDimensions AuthoredDimensions =>
    WorldAssetBounds.MeasureDimensions(ModelPath);

public static WorldAssetNormalization Normalization { get; } = new(
    AuthoredDimensions,
    TargetWorldDimensions);

public static float VisualScale => Normalization.ResolveUniformScale();
```

This keeps authored dimensions reproducible. If runtime model loading cost becomes visible later, replace this property with a generated constant only after adding a test that compares the constant to `WorldAssetBounds.MeasureDimensions(ModelPath)`.

- [ ] **Step 3: Update the foot offset**

Replace the current hardcoded `-0.5f` body-center-to-foot offset with a value that matches the normalized target:

```csharp
public static Vector3 BodyCenterToFootOffset => new(0f, -TargetWorldDimensions.Height * 0.5f, 0f);
```

If gameplay actor position is confirmed to represent feet instead of body center, use `Vector3.Zero` and update `PlayerRenderStateTests` accordingly. Do not mix both conventions.

- [ ] **Step 4: Add player scale tests**

Extend `dream.Tests/Rendering/Models/PlayerModelAssetDefinitionTests.cs`:

```csharp
[Fact]
public void PlayerModelAsset_Uses_CubeRelative_TargetDimensions()
{
    Assert.Equal(2.0f, PlayerModelAssetDefinition.TargetWorldDimensions.Height, 3);
    Assert.True(PlayerModelAssetDefinition.TargetWorldDimensions.Width <= 1.0f);
    Assert.True(PlayerModelAssetDefinition.TargetWorldDimensions.Depth <= 1.0f);
    Assert.True(PlayerModelAssetDefinition.VisualScale > 0f);
}

[Fact]
public void PlayerModelAsset_NormalizedDimensions_Fit_PlayerEnvelope()
{
    WorldAssetDimensions scaled = PlayerModelAssetDefinition.Normalization.ResolveScaledDimensions();

    Assert.Equal(2.0f, scaled.Height, 2);
    Assert.True(scaled.Width <= 1.0f, $"Player width should fit inside one cube. Actual: {scaled.Width}");
    Assert.True(scaled.Depth <= 1.0f, $"Player depth should fit inside one cube. Actual: {scaled.Depth}");
}
```

- [ ] **Step 5: Run focused tests**

Run:

```powershell
dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false --filter "FullyQualifiedName~PlayerModelAssetDefinitionTests|FullyQualifiedName~PlayerRenderStateTests"
```

Expected: pass.

## Task 3: Verify Grounding In Player Render Matrices

- [ ] **Step 1: Add a matrix grounding test**

Extend `dream.Tests/Rendering/Actors/PlayerRenderStateTests.cs` with a test that builds a local player render state at a known body-center position and verifies the model translation places the visual base on the intended floor reference.

```csharp
[Fact]
public void CreateLocal_Grounds_PlayerVisual_From_BodyCenter_Position()
{
    Vector3 bodyCenter = new(5f, 1f, 5f);

    PlayerRenderState state = PlayerRenderState.CreateLocal(
        bodyCenter,
        -Vector3.UnitZ,
        isMoving: false);

    Assert.Equal(
        bodyCenter.Y + PlayerModelAssetDefinition.BodyCenterToFootOffset.Y,
        state.ModelMatrix.M42,
        3);
}
```

- [ ] **Step 2: Keep local and remote player rendering shared**

Do not add separate scale or grounding constants to `LocalPlayerRenderer` or `RemotePlayerRenderer`. Both paths must continue to flow through `PlayerRenderState`.

- [ ] **Step 3: Run actor render-state tests**

Run:

```powershell
dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false --filter "FullyQualifiedName~PlayerRenderStateTests|FullyQualifiedName~RemotePlayerRendererTests"
```

Expected: pass.

## Task 4: Keep Wolf Metadata Explicit

- [ ] **Step 1: Preserve wolf height normalization**

Keep `WolfAssetDefinition.Normalization` as the source of `VisualScale`.

- [ ] **Step 2: Add full wolf target dimensions only after measurement**

If wolf authored width/depth are measured during this pass, add:

```csharp
public static WorldAssetDimensions TargetWorldDimensions => new(
    height: 0.60f,
    width: CollisionRadius * 2f,
    depth: PreferredStandOffDistance * 0.5f);
```

If those dimensions are not measured, do not invent them. Leave the current height-only normalization and document that width/depth remain collision-authored follow-up work.

- [ ] **Step 3: Run wolf tests**

Run:

```powershell
dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false --filter "FullyQualifiedName~WolfAssetDefinitionTests|FullyQualifiedName~WolfAnimationResolverTests"
```

Expected: pass.

## Task 5: Document The Asset Authoring Contract

- [ ] **Step 1: Update rendering docs**

Update `docs/rendering-and-assets.md` under `World Asset Normalization` with:

```markdown
Dream world units are cube-relative: `1.0` world unit equals one cube side. Imported glTF world actors must declare authored dimensions and target cube-relative dimensions in their asset definition. Player models target `2.0` cubes tall and at most `1.0` cube wide/deep in neutral stance. Renderer code should consume those definitions rather than introducing local scale constants.
```

- [ ] **Step 2: Link the durable decision**

Add a link to `docs/ledger/decisions/2026-07-01-world-units-and-asset-scale.md`.

- [ ] **Step 3: Run docs/path verification**

Run:

```powershell
Test-Path docs\ledger\decisions\2026-07-01-world-units-and-asset-scale.md
Test-Path docs\ledger\plans\2026-07-01-model-size-normalization-and-asset-units.md
```

Expected: both commands print `True`.

## Final Verification

Run:

```powershell
dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false
dotnet build dream\dream.csproj --nologo /p:UseSharedCompilation=false
```

Expected:

- all tests pass
- build succeeds with only known warnings
- player visual scale is metadata-derived
- player visual target remains `2.0` cubes tall and no more than `1.0` cube wide/deep in neutral stance
- local and remote player rendering use the same grounding and scale path

## Success Criteria

- Future assets have a clear cube-relative sizing contract.
- Player model size is no longer an arbitrary `0.85f` renderer-era constant.
- The wolf retains explicit target-height normalization.
- Tests protect the player model envelope and grounding.
- Documentation points future agents to the unit decision before they tune model scale.
