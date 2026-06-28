# Render Play Boundary Cleanup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reduce state mutation and gameplay consequence handling inside render-owned code while keeping menu presentation and rendering behavior intact.

**Architecture:** Preserve the current request-return approach for HUD controls, but move consequence handling upward into scene/application code and keep `Render` focused on presentation. Extract small request/result types where needed so renderers present UI, gather user intent, and return data without mutating broader runtime state directly.

**Tech Stack:** C# 13 / .NET 10, OpenTK, ImGui.NET, xUnit, existing `Render`, `HudRenderer`, and `GameSettings` model

---

### Task 1: Add tests around HUD interaction result behavior

**Files:**
- Create: `dream.Tests/HudInteractionRequestTests.cs`
- Test: `dream.Tests/dream.Tests.csproj`

- [ ] **Step 1: Write the failing test for a pure request object**

```csharp
using OpenTK.Mathematics;
using Xunit;

public class HudInteractionRequestTests
{
    [Fact]
    public void Empty_Request_Has_No_Runtime_Changes()
    {
        HudInteractionRequest request = HudInteractionRequest.None;

        Assert.Null(request.FieldOfViewDegrees);
        Assert.Null(request.MouseSensitivity);
        Assert.Null(request.RequestedResolution);
        Assert.Null(request.StagedFloorSize);
        Assert.Null(request.RequestedFloorSize);
        Assert.Null(request.ShowCollisionGeometry);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~HudInteractionRequestTests" --nologo`

Expected: FAIL because `HudInteractionRequest` does not exist yet.

- [ ] **Step 3: Add an equality test for request data transport**

Append this test:

```csharp
[Fact]
public void Request_Can_Carry_Multiple_Changes_Without_Applying_Them()
{
    HudInteractionRequest request = new(
        FieldOfViewDegrees: 80f,
        MouseSensitivity: 4.5f,
        RequestedResolution: new Vector2i(1600, 900),
        StagedFloorSize: 20,
        RequestedFloorSize: 20,
        ShowCollisionGeometry: true);

    Assert.Equal(80f, request.FieldOfViewDegrees);
    Assert.Equal(4.5f, request.MouseSensitivity);
    Assert.Equal(new Vector2i(1600, 900), request.RequestedResolution);
    Assert.Equal(20, request.StagedFloorSize);
    Assert.Equal(20, request.RequestedFloorSize);
    Assert.True(request.ShowCollisionGeometry);
}
```

- [ ] **Step 4: Run the tests again**

Run: `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~HudInteractionRequestTests" --nologo`

Expected: FAIL with missing-type errors only.

### Task 2: Extract a pure HUD interaction request type

**Files:**
- Create: `dream/HudInteractionRequest.cs`
- Modify: `dream/Render.cs`
- Modify: `dream.Tests/HudInteractionRequestTests.cs`
- Test: `dream.Tests/HudInteractionRequestTests.cs`

- [ ] **Step 1: Add the request type**

Create `dream/HudInteractionRequest.cs`:

```csharp
using OpenTK.Mathematics;

public readonly record struct HudInteractionRequest(
    float? FieldOfViewDegrees,
    float? MouseSensitivity,
    Vector2i? RequestedResolution,
    int? StagedFloorSize,
    int? RequestedFloorSize,
    bool? ShowCollisionGeometry)
{
    public static HudInteractionRequest None => new(
        null,
        null,
        null,
        null,
        null,
        null);
}
```

- [ ] **Step 2: Replace the nested `HudInteractionChanges` type in `Render.cs`**

Remove:

```csharp
private readonly record struct HudInteractionChanges(...)
```

And update `DrawHudInterface(...)` to return `HudInteractionRequest`.

- [ ] **Step 3: Stop applying HUD interaction changes inside `Render`**

Remove the call to:

```csharp
ApplyHudInteractionChanges(settings, changes);
```

And remove the `ApplyHudInteractionChanges(...)` helper entirely from `Render.cs`.

- [ ] **Step 4: Run the request tests**

Run: `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~HudInteractionRequestTests" --nologo`

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add dream/HudInteractionRequest.cs dream/Render.cs dream.Tests/HudInteractionRequestTests.cs
git commit -m "refactor: extract pure HUD interaction request"
```

### Task 3: Move request consequence handling into `PlayScene`

**Files:**
- Modify: `dream/Render.cs`
- Modify: `dream/HudRenderer.cs`
- Modify: `dream/PlayScene.cs`
- Modify: `docs/architecture.md`

- [ ] **Step 1: Return the request from render without mutation**

Update the `HudFrameResult` shape in `dream/Render.cs`:

```csharp
public readonly record struct HudFrameResult(
    HudInteractionRequest Request);
```

And return:

```csharp
return new HudFrameResult(changes);
```

- [ ] **Step 2: Add a scene-side request applier**

Add to `dream/PlayScene.cs`:

```csharp
private void ApplyHudRequest(HudInteractionRequest request, Action<Vector2i> applyClientSize)
{
    if (request.FieldOfViewDegrees.HasValue)
    {
        _settings.FieldOfViewDegrees = request.FieldOfViewDegrees.Value;
    }

    if (request.MouseSensitivity.HasValue)
    {
        _settings.MouseSensitivity = request.MouseSensitivity.Value;
    }

    if (request.StagedFloorSize.HasValue)
    {
        _settings.StageFloorSize(request.StagedFloorSize.Value);
    }

    if (request.ShowCollisionGeometry.HasValue)
    {
        _settings.SetShowCollisionGeometry(request.ShowCollisionGeometry.Value);
    }

    if (request.RequestedResolution.HasValue)
    {
        applyClientSize(request.RequestedResolution.Value);
        _settings.ApplyResolution(request.RequestedResolution.Value);
    }

    if (request.RequestedFloorSize.HasValue && request.RequestedFloorSize.Value != _settings.FloorSize)
    {
        _settings.StageFloorSize(request.RequestedFloorSize.Value);
        _settings.ApplyPendingFloorSize();
        RebuildMapForFloorSize(_settings.FloorSize);
    }
}
```

- [ ] **Step 3: Use the new scene-side request handling in `Render(...)`**

Replace the current manual handling block in `PlayScene.Render(...)` with:

```csharp
HudFrameResult hudResult = _render.Hud.DrawFrame(args, windowFocused, _settings, mouseState, _player.Camera, _combatFeedback);
ApplyHudRequest(hudResult.Request, applyClientSize);
_render.Hud.UpdateProjectionForSettings(_settings);
```

- [ ] **Step 4: Update the architecture note**

Add a short note to `docs/architecture.md`:

```md
The HUD now returns interaction requests upward to the scene layer, while `PlayScene` owns the gameplay and runtime consequences of those requests.
```

- [ ] **Step 5: Run build and full tests**

Run: `dotnet build dream\\dream.csproj --nologo`

Expected: PASS with existing warnings only.

Run: `dotnet test dream.Tests\\dream.Tests.csproj --nologo`

Expected: PASS.

### Task 4: Record the boundary cleanup

**Files:**
- Create: `docs/ledger/work-log/2026-06-28-render-play-boundary-cleanup.md`

- [ ] **Step 1: Add the work-log entry**

```md
# Render Play Boundary Cleanup

Purpose: record the shift of runtime consequence handling out of render-owned code.
Status: completed
Scope: HUD request transport and scene-side application

## Summary

Refactored the HUD interaction path so render-owned code returns user requests while `PlayScene` owns the resulting runtime state changes and gameplay consequences.

## Verification

- `dotnet build dream\\dream.csproj --nologo`
- `dotnet test dream.Tests\\dream.Tests.csproj --nologo`
```

- [ ] **Step 2: Commit**

```bash
git add docs/ledger/work-log/2026-06-28-render-play-boundary-cleanup.md
git commit -m "docs: log render play boundary cleanup"
```
