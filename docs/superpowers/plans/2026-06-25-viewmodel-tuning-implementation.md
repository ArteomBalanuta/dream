# Viewmodel Tuning Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a runtime-tunable viewmodel rig with shared FOV/transform controls, per-slot overrides for `hands` and `held item`, and a body-anchor illusion layer that makes the arms feel attached to the player body.

**Architecture:** Persist a structured `ViewModelRigSettings` model inside `GameSettings`, resolve final poses in a dedicated `ViewModelPoseResolver`, and keep `Render.cs` limited to menu controls plus reset wiring. The resolved pose should layer asset defaults, shared rig tuning, body-anchor offsets, per-slot corrections, and animation state before `ViewModelRenderer` draws the viewmodel pass with its own FOV.

**Tech Stack:** C# 13 / .NET 10, OpenTK.Mathematics, xUnit, existing ImGui runtime settings UI, existing viewmodel pipeline (`PlayerViewModelController`, `ViewModelRenderer`, `ViewModelCatalog`).

---

## File Structure

### Existing files to modify

- `dream/GameSettings.cs`
  - Add persisted viewmodel rig settings and reset helpers.
- `dream/Render.cs`
  - Add runtime sliders for shared rig, body anchor, and per-slot tuning.
- `dream/PlayerViewModelController.cs`
  - Resolve tuned poses into the frame state instead of returning asset defaults only.
- `dream/ViewModelFrameState.cs`
  - Store the resolved transform data the renderer needs.
- `dream/ViewModelRenderer.cs`
  - Consume resolved per-slot FOV and transforms instead of hardcoded viewmodel framing.
- `dream/ViewModelAssets.cs`
  - Move current hardcoded framing into better defaults that fit the new rig model.
- `dream.Tests/GameSettingsTests.cs`
  - Cover default rig values, reset behavior, and persistence-oriented mutations.
- `dream.Tests/PlayerViewModelControllerTests.cs`
  - Cover layered pose resolution and body-anchor behavior.

### New files to create

- `dream/ViewModelRigSettings.cs`
  - Shared runtime rig data and resettable defaults.
- `dream/ViewModelSlotTuning.cs`
  - Per-slot local offsets and rotations.
- `dream/ViewModelBodyAnchorSettings.cs`
  - Torso-origin illusion controls.
- `dream/ViewModelResolvedPose.cs`
  - Immutable resolved pose data per slot.
- `dream/ViewModelPoseResolver.cs`
  - Single transform composition point.
- `dream.Tests/ViewModelPoseResolverTests.cs`
  - Focused unit tests for layered transform composition.

## Task 1: Add the structured viewmodel rig settings model

**Files:**
- Create: `dream/ViewModelRigSettings.cs`
- Create: `dream/ViewModelSlotTuning.cs`
- Create: `dream/ViewModelBodyAnchorSettings.cs`
- Modify: `dream/GameSettings.cs`
- Test: `dream.Tests/GameSettingsTests.cs`

- [ ] **Step 1: Write the failing settings tests**

```csharp
using OpenTK.Mathematics;
using Xunit;

public partial class GameSettingsTests
{
    [Fact]
    public void Constructor_Seeds_Default_ViewModel_Rig_Settings()
    {
        var settings = new GameSettings(new Vector2i(1440, 1024));

        Assert.Equal(60f, settings.ViewModelRig.FieldOfViewDegrees);
        Assert.Equal(Vector3.Zero, settings.ViewModelRig.PositionOffset);
        Assert.Equal(Vector3.Zero, settings.ViewModelRig.RotationOffsetDegrees);
        Assert.Equal(Vector3.Zero, settings.ViewModelRig.Hands.PositionOffset);
        Assert.Equal(Vector3.Zero, settings.ViewModelRig.HeldItem.PositionOffset);
        Assert.Equal(0.45f, settings.ViewModelRig.BodyAnchor.HorizontalSpread);
        Assert.Equal(0.28f, settings.ViewModelRig.BodyAnchor.VerticalDrop);
        Assert.Equal(0.18f, settings.ViewModelRig.BodyAnchor.ForwardPull);
    }

    [Fact]
    public void ResetViewModelRig_Restores_Defaults()
    {
        var settings = new GameSettings(new Vector2i(1440, 1024));

        settings.ViewModelRig.FieldOfViewDegrees = 78f;
        settings.ViewModelRig.PositionOffset = new Vector3(0.2f, -0.3f, 0.1f);
        settings.ViewModelRig.Hands.RotationOffsetDegrees = new Vector3(2f, 14f, -9f);
        settings.ViewModelRig.BodyAnchor.HorizontalSpread = 0.7f;

        settings.ResetViewModelRig();

        Assert.Equal(60f, settings.ViewModelRig.FieldOfViewDegrees);
        Assert.Equal(Vector3.Zero, settings.ViewModelRig.PositionOffset);
        Assert.Equal(Vector3.Zero, settings.ViewModelRig.RotationOffsetDegrees);
        Assert.Equal(Vector3.Zero, settings.ViewModelRig.Hands.RotationOffsetDegrees);
        Assert.Equal(0.45f, settings.ViewModelRig.BodyAnchor.HorizontalSpread);
    }
}
```

- [ ] **Step 2: Run the targeted tests to verify they fail**

Run: `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~GameSettingsTests" --nologo`
Expected: FAIL with missing `ViewModelRig` and `ResetViewModelRig` members.

- [ ] **Step 3: Add the structured settings types**

```csharp
using OpenTK.Mathematics;

public sealed class ViewModelRigSettings
{
    public float FieldOfViewDegrees { get; set; } = 60f;
    public Vector3 PositionOffset { get; set; } = Vector3.Zero;
    public Vector3 RotationOffsetDegrees { get; set; } = Vector3.Zero;
    public ViewModelSlotTuning Hands { get; set; } = new();
    public ViewModelSlotTuning HeldItem { get; set; } = new();
    public ViewModelBodyAnchorSettings BodyAnchor { get; set; } = new();

    public static ViewModelRigSettings CreateDefault() => new();
}
```

```csharp
using OpenTK.Mathematics;

public sealed class ViewModelSlotTuning
{
    public Vector3 PositionOffset { get; set; } = Vector3.Zero;
    public Vector3 RotationOffsetDegrees { get; set; } = Vector3.Zero;
}
```

```csharp
public sealed class ViewModelBodyAnchorSettings
{
    public float HorizontalSpread { get; set; } = 0.45f;
    public float VerticalDrop { get; set; } = 0.28f;
    public float ForwardPull { get; set; } = 0.18f;
}
```

- [ ] **Step 4: Wire the rig model into `GameSettings`**

```csharp
using OpenTK.Mathematics;

public class GameSettings
{
    public ViewModelRigSettings ViewModelRig { get; private set; } = ViewModelRigSettings.CreateDefault();

    public void ResetViewModelRig()
    {
        ViewModelRig = ViewModelRigSettings.CreateDefault();
    }
}
```

- [ ] **Step 5: Run the targeted tests to verify they pass**

Run: `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~GameSettingsTests" --nologo`
Expected: PASS for the new viewmodel rig settings tests and existing `GameSettingsTests`.

- [ ] **Step 6: Commit**

```bash
git add dream/GameSettings.cs dream/ViewModelRigSettings.cs dream/ViewModelSlotTuning.cs dream/ViewModelBodyAnchorSettings.cs dream.Tests/GameSettingsTests.cs
git commit -m "feat: add structured viewmodel rig settings"
```

## Task 2: Add a dedicated pose resolver for shared rig, body anchor, and slot overrides

**Files:**
- Create: `dream/ViewModelResolvedPose.cs`
- Create: `dream/ViewModelPoseResolver.cs`
- Test: `dream.Tests/ViewModelPoseResolverTests.cs`

- [ ] **Step 1: Write the failing pose resolver tests**

```csharp
using OpenTK.Mathematics;
using Xunit;

public class ViewModelPoseResolverTests
{
    [Fact]
    public void ResolveHands_Composes_Shared_BodyAnchor_And_Local_Offsets()
    {
        var definition = new ViewModelDefinition(
            "hands.debug",
            "debug-hands",
            "animations/viewmodels/debug-hands.glb",
            ViewModelSlot.Hands,
            new Vector3(0f, -0.35f, -0.55f),
            new Vector3(8f, 180f, 0f),
            0.55f);

        var rig = new ViewModelRigSettings
        {
            PositionOffset = new Vector3(0.10f, 0.05f, 0f),
            RotationOffsetDegrees = new Vector3(1f, 2f, 3f),
            Hands = new ViewModelSlotTuning
            {
                PositionOffset = new Vector3(0.02f, -0.04f, 0.01f),
                RotationOffsetDegrees = new Vector3(3f, 4f, 5f)
            },
            BodyAnchor = new ViewModelBodyAnchorSettings
            {
                HorizontalSpread = 0.45f,
                VerticalDrop = 0.28f,
                ForwardPull = 0.18f
            }
        };

        var resolved = ViewModelPoseResolver.Resolve(definition, rig);

        Assert.Equal(new Vector3(-0.33f, -0.62f, -0.72f), resolved.PositionOffset, new Vector3Comparer(0.001f));
        Assert.Equal(new Vector3(12f, 186f, 8f), resolved.RotationOffsetDegrees, new Vector3Comparer(0.001f));
        Assert.Equal(60f, resolved.FieldOfViewDegrees);
    }

    [Fact]
    public void ResolveHeldItem_Uses_RightSide_BodyAnchor_Bias()
    {
        var definition = new ViewModelDefinition(
            "held.debug",
            "debug-held",
            "animations/viewmodels/debug-held.glb",
            ViewModelSlot.HeldItem,
            new Vector3(0.28f, -0.28f, -0.48f),
            new Vector3(0f, 20f, 0f),
            0.35f);

        var resolved = ViewModelPoseResolver.Resolve(definition, ViewModelRigSettings.CreateDefault());

        Assert.True(resolved.PositionOffset.X > 0.28f);
        Assert.True(resolved.PositionOffset.Y < -0.28f);
        Assert.True(resolved.PositionOffset.Z < -0.48f);
    }
}
```

- [ ] **Step 2: Run the resolver tests to verify they fail**

Run: `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~ViewModelPoseResolverTests" --nologo`
Expected: FAIL with missing resolver and resolved pose types.

- [ ] **Step 3: Add the resolved pose data type**

```csharp
using OpenTK.Mathematics;

public readonly record struct ViewModelResolvedPose(
    float FieldOfViewDegrees,
    Vector3 PositionOffset,
    Vector3 RotationOffsetDegrees,
    float Scale);
```

- [ ] **Step 4: Implement the resolver**

```csharp
using OpenTK.Mathematics;

public static class ViewModelPoseResolver
{
    public static ViewModelResolvedPose Resolve(ViewModelDefinition definition, ViewModelRigSettings rig)
    {
        Vector3 bodyAnchor = GetBodyAnchor(definition.Slot, rig.BodyAnchor);
        ViewModelSlotTuning slotTuning = definition.Slot == ViewModelSlot.Hands
            ? rig.Hands
            : rig.HeldItem;

        Vector3 position =
            definition.PositionOffset +
            rig.PositionOffset +
            bodyAnchor +
            slotTuning.PositionOffset;

        Vector3 rotation =
            definition.RotationOffsetDegrees +
            rig.RotationOffsetDegrees +
            slotTuning.RotationOffsetDegrees;

        return new ViewModelResolvedPose(
            rig.FieldOfViewDegrees,
            position,
            rotation,
            definition.Scale);
    }

    private static Vector3 GetBodyAnchor(ViewModelSlot slot, ViewModelBodyAnchorSettings anchor)
    {
        float horizontal = slot == ViewModelSlot.Hands ? -anchor.HorizontalSpread : anchor.HorizontalSpread;
        return new Vector3(horizontal, -anchor.VerticalDrop, -anchor.ForwardPull);
    }
}
```

- [ ] **Step 5: Run the resolver tests to verify they pass**

Run: `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~ViewModelPoseResolverTests" --nologo`
Expected: PASS for both shared layering and side-bias tests.

- [ ] **Step 6: Commit**

```bash
git add dream/ViewModelResolvedPose.cs dream/ViewModelPoseResolver.cs dream.Tests/ViewModelPoseResolverTests.cs
git commit -m "feat: add viewmodel pose resolver"
```

## Task 3: Thread resolved poses through the viewmodel frame state and controller

**Files:**
- Modify: `dream/ViewModelFrameState.cs`
- Modify: `dream/PlayerViewModelController.cs`
- Test: `dream.Tests/PlayerViewModelControllerTests.cs`

- [ ] **Step 1: Write the failing controller tests**

```csharp
using Xunit;

public partial class PlayerViewModelControllerTests
{
    [Fact]
    public void CurrentFrameState_Contains_Resolved_Hands_Pose()
    {
        ViewModelCatalog catalog = ViewModelCatalog.CreateDefault();
        GameSettings settings = new(new OpenTK.Mathematics.Vector2i(1440, 1024));
        settings.ViewModelRig.BodyAnchor.HorizontalSpread = 0.60f;

        PlayerViewModelController controller = new(catalog, settings);

        Assert.True(controller.CurrentFrameState.Hands.ResolvedPose.PositionOffset.X < -0.35f);
        Assert.Equal(settings.ViewModelRig.FieldOfViewDegrees, controller.CurrentFrameState.Hands.ResolvedPose.FieldOfViewDegrees);
    }

    [Fact]
    public void Resetting_Settings_Changes_Resolved_Pose_On_Rebuild()
    {
        ViewModelCatalog catalog = ViewModelCatalog.CreateDefault();
        GameSettings settings = new(new OpenTK.Mathematics.Vector2i(1440, 1024));
        settings.ViewModelRig.Hands.PositionOffset = new OpenTK.Mathematics.Vector3(-0.25f, 0f, 0f);

        PlayerViewModelController controller = new(catalog, settings);
        float tunedX = controller.CurrentFrameState.Hands.ResolvedPose.PositionOffset.X;

        settings.ResetViewModelRig();
        controller.RefreshPose();

        Assert.NotEqual(tunedX, controller.CurrentFrameState.Hands.ResolvedPose.PositionOffset.X);
    }
}
```

- [ ] **Step 2: Run the controller tests to verify they fail**

Run: `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~PlayerViewModelControllerTests" --nologo`
Expected: FAIL with constructor/signature mismatches or missing resolved pose members.

- [ ] **Step 3: Add resolved pose storage to the frame state**

```csharp
public readonly record struct ViewModelSlotState(
    ViewModelDefinition Definition,
    ViewModelAnimationRequest AnimationRequest,
    ViewModelResolvedPose ResolvedPose);
```

```csharp
public readonly record struct ViewModelFrameState(
    ViewModelSlotState Hands,
    ViewModelSlotState HeldItem);
```

- [ ] **Step 4: Update the controller to resolve poses from settings**

```csharp
public sealed class PlayerViewModelController
{
    private readonly ViewModelCatalog catalog;
    private readonly GameSettings settings;

    public PlayerViewModelController(ViewModelCatalog catalog, GameSettings settings)
    {
        this.catalog = catalog;
        this.settings = settings;
        CurrentFrameState = CreateDefaultFrameState();
    }

    public void RefreshPose()
    {
        CurrentFrameState = new ViewModelFrameState(
            CurrentFrameState.Hands with
            {
                ResolvedPose = ViewModelPoseResolver.Resolve(CurrentFrameState.Hands.Definition, settings.ViewModelRig)
            },
            CurrentFrameState.HeldItem with
            {
                ResolvedPose = ViewModelPoseResolver.Resolve(CurrentFrameState.HeldItem.Definition, settings.ViewModelRig)
            });
    }
}
```

- [ ] **Step 5: Run the controller tests to verify they pass**

Run: `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~PlayerViewModelControllerTests" --nologo`
Expected: PASS for existing animation tests plus the new resolved-pose coverage.

- [ ] **Step 6: Commit**

```bash
git add dream/ViewModelFrameState.cs dream/PlayerViewModelController.cs dream.Tests/PlayerViewModelControllerTests.cs
git commit -m "feat: resolve tuned poses in viewmodel controller"
```

## Task 4: Make the renderer consume resolved FOV and transforms

**Files:**
- Modify: `dream/ViewModelRenderer.cs`
- Modify: `dream/ViewModelAssets.cs`
- Test: `dream.Tests/ViewModelPoseResolverTests.cs`

- [ ] **Step 1: Extend the resolver tests with default framing expectations**

```csharp
[Fact]
public void DefaultRig_Lowers_And_Widens_Hands_From_Asset_Center()
{
    ViewModelDefinition definition = ViewModelAssets.DebugHandsDefinition;

    ViewModelResolvedPose resolved = ViewModelPoseResolver.Resolve(definition, ViewModelRigSettings.CreateDefault());

    Assert.True(resolved.PositionOffset.X < definition.PositionOffset.X);
    Assert.True(resolved.PositionOffset.Y < definition.PositionOffset.Y);
    Assert.Equal(60f, resolved.FieldOfViewDegrees);
}
```

- [ ] **Step 2: Run the resolver tests to verify the new expectation fails if defaults are not yet tuned**

Run: `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~ViewModelPoseResolverTests" --nologo`
Expected: FAIL if the default asset framing still looks centered or the renderer still hardcodes FOV independently.

- [ ] **Step 3: Update renderer draw calls to use resolved poses**

```csharp
private void DrawSlot(in ViewModelSlotState slotState)
{
    ViewModelResolvedPose pose = slotState.ResolvedPose;
    Matrix4 projection = Matrix4.CreatePerspectiveFieldOfView(
        MathHelper.DegreesToRadians(pose.FieldOfViewDegrees),
        render.ViewportSize.X / (float)render.ViewportSize.Y,
        0.01f,
        64f);

    DrawModel(
        slotState.Definition,
        pose.PositionOffset,
        pose.RotationOffsetDegrees,
        pose.Scale,
        projection);
}
```

- [ ] **Step 4: Tune defaults in `ViewModelAssets` to align with the new layered rig**

```csharp
public static ViewModelDefinition DebugHandsDefinition { get; } = new(
    "hands.debug",
    "debug-hands",
    "animations/viewmodels/debug-hands.glb",
    ViewModelSlot.Hands,
    new Vector3(0f, -0.18f, -0.42f),
    new Vector3(12f, 180f, -4f),
    0.55f);

public static ViewModelDefinition DebugHeldItemDefinition { get; } = new(
    "held.debug",
    "debug-held",
    "animations/viewmodels/debug-held.glb",
    ViewModelSlot.HeldItem,
    new Vector3(0.16f, -0.12f, -0.36f),
    new Vector3(-2f, 16f, -6f),
    0.35f);
```

- [ ] **Step 5: Run the resolver tests to verify they pass**

Run: `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~ViewModelPoseResolverTests" --nologo`
Expected: PASS with default hands lower and wider plus preserved FOV behavior.

- [ ] **Step 6: Commit**

```bash
git add dream/ViewModelRenderer.cs dream/ViewModelAssets.cs dream.Tests/ViewModelPoseResolverTests.cs
git commit -m "feat: render viewmodels from resolved rig poses"
```

## Task 5: Add runtime menu sliders and reset wiring without growing `Render.cs` behavior

**Files:**
- Modify: `dream/Render.cs`
- Modify: `dream/GameSettings.cs`
- Test: `dream.Tests/GameSettingsTests.cs`

- [ ] **Step 1: Write the failing settings mutation tests**

```csharp
using OpenTK.Mathematics;
using Xunit;

public partial class GameSettingsTests
{
    [Fact]
    public void ViewModelRig_Allows_Live_Shared_Adjustments()
    {
        var settings = new GameSettings(new Vector2i(1440, 1024));

        settings.ViewModelRig.FieldOfViewDegrees = 72f;
        settings.ViewModelRig.PositionOffset = new Vector3(0.05f, -0.10f, 0.02f);

        Assert.Equal(72f, settings.ViewModelRig.FieldOfViewDegrees);
        Assert.Equal(new Vector3(0.05f, -0.10f, 0.02f), settings.ViewModelRig.PositionOffset);
    }
}
```

- [ ] **Step 2: Run the settings tests to verify they fail only if setter/reset behavior is still incomplete**

Run: `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~GameSettingsTests" --nologo`
Expected: FAIL only if the rig settings were not left mutable for live menu updates.

- [ ] **Step 3: Add grouped viewmodel UI controls**

```csharp
if (ImGui.CollapsingHeader("Viewmodel", ImGuiTreeNodeFlags.DefaultOpen))
{
    ViewModelRigSettings rig = game.Settings.ViewModelRig;

    ImGui.SliderFloat("Viewmodel FOV", ref rig.FieldOfViewDegrees, 35f, 100f);
    SliderVector3("Shared Offset", ref rig.PositionOffset, -1.5f, 1.5f);
    SliderVector3("Shared Rotation", ref rig.RotationOffsetDegrees, -45f, 45f);

    ImGui.SeparatorText("Body Anchor");
    ImGui.SliderFloat("Spread", ref rig.BodyAnchor.HorizontalSpread, 0f, 1.25f);
    ImGui.SliderFloat("Drop", ref rig.BodyAnchor.VerticalDrop, 0f, 1f);
    ImGui.SliderFloat("Forward Pull", ref rig.BodyAnchor.ForwardPull, 0f, 1f);

    DrawSlotTuningControls("Hands", rig.Hands);
    DrawSlotTuningControls("Held Item", rig.HeldItem);

    if (ImGui.Button("Reset Viewmodel Tuning"))
    {
        game.Settings.ResetViewModelRig();
    }

    game.PlayerViewModelController.RefreshPose();
}
```

- [ ] **Step 4: Add small UI helpers instead of embedding repeated slider blocks inline**

```csharp
private static void DrawSlotTuningControls(string label, ViewModelSlotTuning tuning)
{
    if (!ImGui.TreeNode(label))
    {
        return;
    }

    SliderVector3("Offset", ref tuning.PositionOffset, -1.5f, 1.5f);
    SliderVector3("Rotation", ref tuning.RotationOffsetDegrees, -45f, 45f);
    ImGui.TreePop();
}
```

- [ ] **Step 5: Run the settings tests to verify they pass**

Run: `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~GameSettingsTests" --nologo`
Expected: PASS for existing settings behavior and live viewmodel rig mutation coverage.

- [ ] **Step 6: Commit**

```bash
git add dream/Render.cs dream/GameSettings.cs dream.Tests/GameSettingsTests.cs
git commit -m "feat: add runtime viewmodel tuning controls"
```

## Task 6: Run full verification and record the docs update

**Files:**
- Modify: `docs/rendering-and-assets.md`
- Modify: `docs/architecture.md`
- Modify: `docs/ledger/work-log/2026-06-25-first-person-viewmodels-and-skins.md`

- [ ] **Step 1: Update the rendering docs with the new pose resolution flow**

```md
## Viewmodel Tuning

The first-person rig now resolves viewmodel poses through `ViewModelPoseResolver`, layering asset defaults, shared rig tuning, body-anchor illusion offsets, per-slot corrections, and animation state before `ViewModelRenderer` draws the pass.
```

- [ ] **Step 2: Update the architecture docs with the new settings and resolver responsibilities**

```md
## Viewmodel Rig

`GameSettings` owns persisted `ViewModelRigSettings`. `Render` exposes runtime controls. `PlayerViewModelController` refreshes resolved poses, and `ViewModelRenderer` remains responsible only for drawing the resolved output.
```

- [ ] **Step 3: Add a work-log entry for the tuning layer**

```md
## Viewmodel tuning rig

- Added shared viewmodel FOV and transform settings.
- Added body-anchor illusion controls for torso-connected arm framing.
- Added per-slot `hands` and `held item` corrections.
```

- [ ] **Step 4: Run the full verification suite**

Run: `dotnet build dream\\dream.csproj --nologo`
Expected: `Build succeeded.`

Run: `dotnet test dream.Tests\\dream.Tests.csproj --nologo`
Expected: PASS for the full test suite.

- [ ] **Step 5: Commit**

```bash
git add docs/rendering-and-assets.md docs/architecture.md docs/ledger/work-log/2026-06-25-first-person-viewmodels-and-skins.md
git commit -m "docs: record viewmodel tuning rig"
```
