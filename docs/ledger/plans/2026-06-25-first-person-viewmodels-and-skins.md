# First-Person Viewmodels And Skins Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a renderer-safe first-person viewmodel foundation with skinnable hands and a separate held-item slot, including click animation support and the renderer decomposition needed to keep `Render.cs` from growing into a larger godclass.

**Architecture:** The work starts by extracting a dedicated first-person renderer seam and threading it through `PlayScene` before any visible feature logic lands. After that, it adds a small data-driven catalog/loadout model, a controller-owned runtime presentation state, deterministic clip playback support in `AnimatedModel`, and a first visible integration using blockout viewmodel assets that can later be replaced without code changes.

**Tech Stack:** C#/.NET 10, OpenTK, OpenGL, BulletSharp, SharpGLTF, SixLabors.ImageSharp, ImGui.NET, xUnit

---

## File Structure

**Create:**

- `dream/ViewModelRenderer.cs`
- `dream/ViewModelSlot.cs`
- `dream/ViewModelDefinition.cs`
- `dream/ViewModelLoadout.cs`
- `dream/ViewModelCatalog.cs`
- `dream/ViewModelAnimationRequest.cs`
- `dream/ViewModelSlotState.cs`
- `dream/ViewModelFrameState.cs`
- `dream/PlayerViewModelController.cs`
- `dream/AnimationPlaybackMode.cs`
- `dream/AnimationPlaybackState.cs`
- `dream/ViewModelAssets.cs`
- `dream.Tests/ViewModelCatalogTests.cs`
- `dream.Tests/PlayerViewModelControllerTests.cs`
- `dream.Tests/AnimationPlaybackStateTests.cs`
- `docs/ledger/work-log/2026-06-25-first-person-viewmodels-and-skins.md`

**Modify:**

- `dream/Render.cs`
- `dream/PlayScene.cs`
- `dream/Player.cs`
- `dream/AnimatedModel.cs`
- `dream/ModelAssets.cs`
- `docs/architecture.md`
- `docs/rendering-and-assets.md`
- `docs/build-and-run.md`

**Asset additions during implementation:**

- `dream/animations/viewmodels/DebugHands.gltf`
- `dream/animations/viewmodels/DebugHands.bin`
- `dream/animations/viewmodels/DebugHeldItem.gltf`
- `dream/animations/viewmodels/DebugHeldItem.bin`

The initial art pass intentionally uses blockout debug assets so the system proves its slot, animation, and renderer boundaries before bespoke hand art is introduced.

### Task 1: Extract the dedicated first-person renderer seam

**Files:**
- Create: `dream/ViewModelRenderer.cs`
- Modify: `dream/Render.cs`
- Modify: `dream/PlayScene.cs`
- Test: `dream/dream.csproj`

- [ ] **Step 1: Create the new renderer seam as a focused collaborator**

Add a first version of `ViewModelRenderer` that owns a dedicated draw entry point but performs no visible work yet.

```csharp
using OpenTK.Mathematics;

public class ViewModelRenderer
{
    private readonly Render _render;

    public ViewModelRenderer(Render render)
    {
        _render = render;
    }

    public void Draw(Camera camera, ViewModelFrameState? frameState)
    {
        if (frameState is null)
        {
            return;
        }

        // Intentionally empty in Task 1. The seam must exist before feature logic lands.
    }
}
```

- [ ] **Step 2: Expose the new seam from `Render` instead of planning to inline feature logic later**

Add the field and property in `dream/Render.cs`:

```csharp
private readonly ViewModelRenderer _viewModelRenderer;

public Render(GameSettings settings)
{
    _worldRenderer = new WorldRenderer(this);
    _debugRenderer = new DebugRenderer(this);
    _hudRenderer = new HudRenderer(this);
    _viewModelRenderer = new ViewModelRenderer(this);
    GL.Enable(EnableCap.DepthTest);
    GL.Enable(EnableCap.Blend);
    GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

    InitializeShaders();
    UpdateProjectionForSettings(settings);
    ...
}

public ViewModelRenderer ViewModels => _viewModelRenderer;
```

- [ ] **Step 3: Thread the new pass through `PlayScene.Render(...)` in the correct order**

Insert the viewmodel pass between debug rendering and HUD rendering in `dream/PlayScene.cs`:

```csharp
_render.World.DrawWorld(
    _player.Camera,
    _map.GetRenderCandidateCubes(_player.Camera.Position, WorldVisibility.MaxRenderDistance));

if (_settings.ShowCollisionGeometry)
{
    _render.Debug.DrawCollisionBodies(_physics.MapCubeBodies, _player.Camera);
    _render.DrawCollisionBody(_physics.PlayerBody, _player.Camera);
}

_render.ViewModels.Draw(_player.Camera, null);

HudFrameResult hudResult = _render.Hud.DrawFrame(args, windowFocused, _settings, mouseState);
```

- [ ] **Step 4: Run a build to verify the renderer extraction compiles cleanly**

Run:

```powershell
dotnet build dream\dream.csproj --nologo
```

Expected:

- build succeeds
- no behavior change yet
- `Render.cs` now exposes a real first-person seam without absorbing new feature logic

- [ ] **Step 5: Commit the renderer-first extraction**

```powershell
git add dream/ViewModelRenderer.cs dream/Render.cs dream/PlayScene.cs
git commit -m "refactor: extract first-person renderer seam"
```

### Task 2: Add the catalog, slot, and loadout foundation

**Files:**
- Create: `dream/ViewModelSlot.cs`
- Create: `dream/ViewModelDefinition.cs`
- Create: `dream/ViewModelLoadout.cs`
- Create: `dream/ViewModelCatalog.cs`
- Create: `dream.Tests/ViewModelCatalogTests.cs`

- [ ] **Step 1: Write the failing catalog tests**

Create `dream.Tests/ViewModelCatalogTests.cs`:

```csharp
using Xunit;

public class ViewModelCatalogTests
{
    [Fact]
    public void DefaultLoadout_UsesRegisteredHandsAndHeldItem()
    {
        ViewModelCatalog catalog = ViewModelCatalog.CreateDefault();
        ViewModelLoadout loadout = catalog.DefaultLoadout;

        ViewModelDefinition hands = catalog.GetDefinition(loadout.HandsId);
        ViewModelDefinition heldItem = catalog.GetDefinition(loadout.HeldItemId);

        Assert.Equal(ViewModelSlot.Hands, hands.Slot);
        Assert.Equal(ViewModelSlot.HeldItem, heldItem.Slot);
    }

    [Fact]
    public void GetDefinition_ThrowsForUnknownId()
    {
        ViewModelCatalog catalog = ViewModelCatalog.CreateDefault();

        Assert.Throws<KeyNotFoundException>(() => catalog.GetDefinition("missing"));
    }

    [Fact]
    public void Catalog_RejectsSlotMismatchInDefaultLoadout()
    {
        var hands = new ViewModelDefinition(
            "hands.debug",
            ViewModelSlot.Hands,
            "hands.gltf",
            "Idle",
            "Use",
            new OpenTK.Mathematics.Vector3(0f, 0f, 0f),
            new OpenTK.Mathematics.Vector3(0f, 0f, 0f),
            1f);

        var held = new ViewModelDefinition(
            "held.debug",
            ViewModelSlot.Hands,
            "held.gltf",
            "Idle",
            "Use",
            new OpenTK.Mathematics.Vector3(0f, 0f, 0f),
            new OpenTK.Mathematics.Vector3(0f, 0f, 0f),
            1f);

        Assert.Throws<ArgumentException>(() =>
            new ViewModelCatalog(
                new[] { hands, held },
                new ViewModelLoadout("hands.debug", "held.debug")));
    }
}
```

- [ ] **Step 2: Run the catalog tests to verify they fail**

Run:

```powershell
dotnet test dream.Tests\dream.Tests.csproj --filter ViewModelCatalogTests --nologo
```

Expected:

- test discovery succeeds
- tests fail because the catalog and data types do not exist yet

- [ ] **Step 3: Implement the slot, definition, and loadout types**

Create `dream/ViewModelSlot.cs`:

```csharp
public enum ViewModelSlot
{
    Hands,
    HeldItem
}
```

Create `dream/ViewModelDefinition.cs`:

```csharp
using OpenTK.Mathematics;

public readonly record struct ViewModelDefinition(
    string Id,
    ViewModelSlot Slot,
    string ModelPath,
    string? IdleAnimation,
    string? UseAnimation,
    Vector3 PositionOffset,
    Vector3 RotationOffsetDegrees,
    float Scale);
```

Create `dream/ViewModelLoadout.cs`:

```csharp
public readonly record struct ViewModelLoadout(
    string HandsId,
    string HeldItemId);
```

- [ ] **Step 4: Implement the catalog with default validation**

Create `dream/ViewModelCatalog.cs`:

```csharp
using System.Collections.ObjectModel;

public class ViewModelCatalog
{
    private readonly IReadOnlyDictionary<string, ViewModelDefinition> _definitions;

    public ViewModelCatalog(IEnumerable<ViewModelDefinition> definitions, ViewModelLoadout defaultLoadout)
    {
        Dictionary<string, ViewModelDefinition> map = definitions.ToDictionary(d => d.Id, d => d);

        ValidateSlot(map, defaultLoadout.HandsId, ViewModelSlot.Hands);
        ValidateSlot(map, defaultLoadout.HeldItemId, ViewModelSlot.HeldItem);

        _definitions = new ReadOnlyDictionary<string, ViewModelDefinition>(map);
        DefaultLoadout = defaultLoadout;
    }

    public ViewModelLoadout DefaultLoadout { get; }

    public ViewModelDefinition GetDefinition(string id)
    {
        if (!_definitions.TryGetValue(id, out ViewModelDefinition definition))
        {
            throw new KeyNotFoundException($"Unknown viewmodel id '{id}'.");
        }

        return definition;
    }

    public static ViewModelCatalog CreateDefault()
    {
        return new ViewModelCatalog(
            new[]
            {
                ViewModelAssets.DebugHandsDefinition,
                ViewModelAssets.DebugHeldItemDefinition
            },
            new ViewModelLoadout(
                ViewModelAssets.DebugHandsDefinition.Id,
                ViewModelAssets.DebugHeldItemDefinition.Id));
    }

    private static void ValidateSlot(
        IReadOnlyDictionary<string, ViewModelDefinition> map,
        string id,
        ViewModelSlot expectedSlot)
    {
        if (!map.TryGetValue(id, out ViewModelDefinition definition))
        {
            throw new ArgumentException($"Default viewmodel id '{id}' is not registered.");
        }

        if (definition.Slot != expectedSlot)
        {
            throw new ArgumentException(
                $"Viewmodel '{id}' must be registered as slot '{expectedSlot}' but was '{definition.Slot}'.");
        }
    }
}
```

- [ ] **Step 5: Add a first asset-definition host for the default catalog**

Create `dream/ViewModelAssets.cs`:

```csharp
using OpenTK.Mathematics;

public static class ViewModelAssets
{
    public static ViewModelDefinition DebugHandsDefinition => new(
        "hands.debug",
        ViewModelSlot.Hands,
        AssetPaths.Animation("viewmodels/DebugHands.gltf"),
        "Idle",
        "Use",
        new Vector3(0f, -0.35f, -0.55f),
        new Vector3(8f, 180f, 0f),
        0.55f);

    public static ViewModelDefinition DebugHeldItemDefinition => new(
        "held.debug",
        ViewModelSlot.HeldItem,
        AssetPaths.Animation("viewmodels/DebugHeldItem.gltf"),
        "Idle",
        "Use",
        new Vector3(0.28f, -0.28f, -0.48f),
        new Vector3(0f, 20f, 0f),
        0.35f);
}
```

- [ ] **Step 6: Run the catalog tests to verify they pass**

Run:

```powershell
dotnet test dream.Tests\dream.Tests.csproj --filter ViewModelCatalogTests --nologo
```

Expected:

- `ViewModelCatalogTests` pass

- [ ] **Step 7: Commit the catalog foundation**

```powershell
git add dream/ViewModelSlot.cs dream/ViewModelDefinition.cs dream/ViewModelLoadout.cs dream/ViewModelCatalog.cs dream/ViewModelAssets.cs dream.Tests/ViewModelCatalogTests.cs
git commit -m "feat: add viewmodel catalog foundation"
```

### Task 3: Add controller-owned runtime viewmodel state

**Files:**
- Create: `dream/ViewModelAnimationRequest.cs`
- Create: `dream/ViewModelSlotState.cs`
- Create: `dream/ViewModelFrameState.cs`
- Create: `dream/PlayerViewModelController.cs`
- Modify: `dream/Player.cs`
- Modify: `dream/PlayScene.cs`
- Create: `dream.Tests/PlayerViewModelControllerTests.cs`

- [ ] **Step 1: Write the failing controller tests**

Create `dream.Tests/PlayerViewModelControllerTests.cs`:

```csharp
using Xunit;

public class PlayerViewModelControllerTests
{
    [Fact]
    public void CreateDefaultFrameState_UsesCatalogLoadout()
    {
        ViewModelCatalog catalog = ViewModelCatalog.CreateDefault();
        PlayerViewModelController controller = new(catalog);

        ViewModelFrameState state = controller.CurrentFrameState;

        Assert.Equal("hands.debug", state.Hands.Definition.Id);
        Assert.Equal("held.debug", state.HeldItem.Definition.Id);
    }

    [Fact]
    public void TriggerPrimaryUse_StartsUseAnimationOnHands()
    {
        ViewModelCatalog catalog = ViewModelCatalog.CreateDefault();
        PlayerViewModelController controller = new(catalog);

        controller.TriggerPrimaryUse();

        Assert.Equal(ViewModelAnimationRequest.Use, controller.CurrentFrameState.Hands.AnimationRequest);
    }

    [Fact]
    public void FinishOneShot_ReturnsHandsToIdle()
    {
        ViewModelCatalog catalog = ViewModelCatalog.CreateDefault();
        PlayerViewModelController controller = new(catalog);

        controller.TriggerPrimaryUse();
        controller.NotifyAnimationFinished(ViewModelSlot.Hands);

        Assert.Equal(ViewModelAnimationRequest.Idle, controller.CurrentFrameState.Hands.AnimationRequest);
    }
}
```

- [ ] **Step 2: Run the controller tests to verify they fail**

Run:

```powershell
dotnet test dream.Tests\dream.Tests.csproj --filter PlayerViewModelControllerTests --nologo
```

Expected:

- tests fail because the runtime controller and state types do not exist yet

- [ ] **Step 3: Implement the frame-state model**

Create `dream/ViewModelAnimationRequest.cs`:

```csharp
public enum ViewModelAnimationRequest
{
    Idle,
    Use
}
```

Create `dream/ViewModelSlotState.cs`:

```csharp
public readonly record struct ViewModelSlotState(
    ViewModelDefinition Definition,
    ViewModelAnimationRequest AnimationRequest);
```

Create `dream/ViewModelFrameState.cs`:

```csharp
public readonly record struct ViewModelFrameState(
    ViewModelSlotState Hands,
    ViewModelSlotState HeldItem);
```

- [ ] **Step 4: Implement the controller with deterministic use-to-idle behavior**

Create `dream/PlayerViewModelController.cs`:

```csharp
public class PlayerViewModelController
{
    private readonly ViewModelCatalog _catalog;
    private ViewModelLoadout _loadout;

    public PlayerViewModelController(ViewModelCatalog catalog)
    {
        _catalog = catalog;
        _loadout = catalog.DefaultLoadout;
        CurrentFrameState = BuildState(ViewModelAnimationRequest.Idle, ViewModelAnimationRequest.Idle);
    }

    public ViewModelFrameState CurrentFrameState { get; private set; }

    public void TriggerPrimaryUse()
    {
        CurrentFrameState = BuildState(ViewModelAnimationRequest.Use, ViewModelAnimationRequest.Use);
    }

    public void NotifyAnimationFinished(ViewModelSlot slot)
    {
        if (slot == ViewModelSlot.Hands)
        {
            CurrentFrameState = BuildState(ViewModelAnimationRequest.Idle, CurrentFrameState.HeldItem.AnimationRequest);
            return;
        }

        CurrentFrameState = BuildState(CurrentFrameState.Hands.AnimationRequest, ViewModelAnimationRequest.Idle);
    }

    private ViewModelFrameState BuildState(ViewModelAnimationRequest handsRequest, ViewModelAnimationRequest heldItemRequest)
    {
        return new ViewModelFrameState(
            new ViewModelSlotState(_catalog.GetDefinition(_loadout.HandsId), handsRequest),
            new ViewModelSlotState(_catalog.GetDefinition(_loadout.HeldItemId), heldItemRequest));
    }
}
```

- [ ] **Step 5: Wire the controller into `PlayScene` and player-facing input flow**

Use a direct edge-trigger for the first release instead of burying animation logic inside the renderer.

Add to `dream/PlayScene.cs`:

```csharp
private readonly PlayerViewModelController _viewModelController;

public PlayScene(GameSettings settings)
{
    _settings = settings;
    _physics = new PhysicsIntegrator();
    _map = new Map(_physics, width: _settings.FloorSize, depth: _settings.FloorSize);
    _viewModelController = new PlayerViewModelController(ViewModelCatalog.CreateDefault());
    ...
}
```

In `Update(...)`:

```csharp
bool primaryUseTriggered = _player.ConsumePrimaryUsePressed(mouseState, allowGameplayInput);
if (primaryUseTriggered)
{
    _viewModelController.TriggerPrimaryUse();
}
```

In `Render(...)` replace the Task 1 placeholder:

```csharp
_render.ViewModels.Draw(_player.Camera, _viewModelController.CurrentFrameState);
```

And add the input edge helper to `dream/Player.cs`:

```csharp
private bool _wasPrimaryUsePressed;

public bool ConsumePrimaryUsePressed(MouseState mouseState, bool allowInput)
{
    bool isPressed = _controller.CaptureInteraction(mouseState, allowInput).PlaceCubePressed;
    bool triggered = isPressed && !_wasPrimaryUsePressed;
    _wasPrimaryUsePressed = isPressed;
    return triggered;
}
```

This preserves the current gameplay click semantics while making the presentation hook explicit.

- [ ] **Step 6: Run the controller tests and full build**

Run:

```powershell
dotnet test dream.Tests\dream.Tests.csproj --filter PlayerViewModelControllerTests --nologo
dotnet build dream\dream.csproj --nologo
```

Expected:

- controller tests pass
- build succeeds

- [ ] **Step 7: Commit the runtime controller layer**

```powershell
git add dream/ViewModelAnimationRequest.cs dream/ViewModelSlotState.cs dream/ViewModelFrameState.cs dream/PlayerViewModelController.cs dream/Player.cs dream/PlayScene.cs dream.Tests/PlayerViewModelControllerTests.cs
git commit -m "feat: add player viewmodel controller"
```

### Task 4: Add deterministic clip playback support to `AnimatedModel`

**Files:**
- Create: `dream/AnimationPlaybackMode.cs`
- Create: `dream/AnimationPlaybackState.cs`
- Modify: `dream/AnimatedModel.cs`
- Create: `dream.Tests/AnimationPlaybackStateTests.cs`

- [ ] **Step 1: Write the failing playback-state tests**

Create `dream.Tests/AnimationPlaybackStateTests.cs`:

```csharp
using Xunit;

public class AnimationPlaybackStateTests
{
    [Fact]
    public void PlayLoop_SetsLoopingClip()
    {
        AnimationPlaybackState state = AnimationPlaybackState.Stopped;

        state = state.Play("Idle", AnimationPlaybackMode.Loop);

        Assert.Equal("Idle", state.ClipName);
        Assert.Equal(AnimationPlaybackMode.Loop, state.Mode);
        Assert.True(state.IsPlaying);
    }

    [Fact]
    public void PlayOneShot_ResetsTime()
    {
        AnimationPlaybackState state = AnimationPlaybackState.Stopped
            .Play("Idle", AnimationPlaybackMode.Loop)
            .Advance(0.75f, 2f, out _);

        state = state.Play("Use", AnimationPlaybackMode.OneShot);

        Assert.Equal("Use", state.ClipName);
        Assert.Equal(0f, state.TimeSeconds);
    }

    [Fact]
    public void Advance_ReportsFinishedForOneShot()
    {
        AnimationPlaybackState state = AnimationPlaybackState.Stopped
            .Play("Use", AnimationPlaybackMode.OneShot);

        state = state.Advance(1.25f, 1f, out bool finished);

        Assert.True(finished);
        Assert.Equal(1f, state.TimeSeconds);
    }
}
```

- [ ] **Step 2: Run the playback-state tests to verify they fail**

Run:

```powershell
dotnet test dream.Tests\dream.Tests.csproj --filter AnimationPlaybackStateTests --nologo
```

Expected:

- tests fail because playback helper types do not exist yet

- [ ] **Step 3: Implement the pure playback helper**

Create `dream/AnimationPlaybackMode.cs`:

```csharp
public enum AnimationPlaybackMode
{
    Loop,
    OneShot
}
```

Create `dream/AnimationPlaybackState.cs`:

```csharp
public readonly record struct AnimationPlaybackState(
    string? ClipName,
    AnimationPlaybackMode Mode,
    float TimeSeconds,
    bool IsPlaying)
{
    public static AnimationPlaybackState Stopped => new(null, AnimationPlaybackMode.Loop, 0f, false);

    public AnimationPlaybackState Play(string clipName, AnimationPlaybackMode mode)
    {
        if (IsPlaying && ClipName == clipName && Mode == mode)
        {
            return this;
        }

        return new AnimationPlaybackState(clipName, mode, 0f, true);
    }

    public AnimationPlaybackState Advance(float deltaTime, float durationSeconds, out bool finished)
    {
        finished = false;

        if (!IsPlaying)
        {
            return this;
        }

        float nextTime = TimeSeconds + deltaTime;
        if (Mode == AnimationPlaybackMode.Loop)
        {
            float wrapped = durationSeconds <= 0f ? 0f : nextTime % durationSeconds;
            return this with { TimeSeconds = wrapped };
        }

        if (nextTime >= durationSeconds)
        {
            finished = true;
            return this with { TimeSeconds = durationSeconds, IsPlaying = true };
        }

        return this with { TimeSeconds = nextTime };
    }
}
```

- [ ] **Step 4: Upgrade `AnimatedModel` to honor named clips**

Refactor the animation fields in `dream/AnimatedModel.cs`:

```csharp
private AnimationPlaybackState _playback = AnimationPlaybackState.Stopped;
private string? _idleClipName;

public void PlayLoop(string clipName)
{
    _idleClipName = clipName;
    _playback = _playback.Play(clipName, AnimationPlaybackMode.Loop);
}

public void PlayOneShot(string clipName)
{
    _playback = _playback.Play(clipName, AnimationPlaybackMode.OneShot);
}
```

Replace `Update(float deltaTime)` with clip-specific advancement:

```csharp
public bool Update(float deltaTime)
{
    if (!_playback.IsPlaying || string.IsNullOrWhiteSpace(_playback.ClipName))
    {
        return false;
    }

    if (!_animations.TryGetValue(_playback.ClipName, out Animation animation))
    {
        return false;
    }

    _playback = _playback.Advance(deltaTime * AnimationSpeed, animation.Duration, out bool finished);
    UpdateNodes(animation, _playback.TimeSeconds);

    if (finished && _playback.Mode == AnimationPlaybackMode.OneShot && !string.IsNullOrWhiteSpace(_idleClipName))
    {
        _playback = _playback.Play(_idleClipName, AnimationPlaybackMode.Loop);
        return true;
    }

    return finished;
}
```

And keep compatibility helpers small and explicit:

```csharp
public void StartAnimation(string? name = null)
{
    if (string.IsNullOrWhiteSpace(name))
    {
        return;
    }

    PlayLoop(name);
}

public void StopAnimation()
{
    _playback = AnimationPlaybackState.Stopped;
}
```

- [ ] **Step 5: Run the playback tests and full build**

Run:

```powershell
dotnet test dream.Tests\dream.Tests.csproj --filter AnimationPlaybackStateTests --nologo
dotnet build dream\dream.csproj --nologo
```

Expected:

- playback tests pass
- build succeeds
- `AnimatedModel` now owns real clip selection semantics

- [ ] **Step 6: Commit the animation playback upgrade**

```powershell
git add dream/AnimationPlaybackMode.cs dream/AnimationPlaybackState.cs dream/AnimatedModel.cs dream.Tests/AnimationPlaybackStateTests.cs
git commit -m "feat: add named animation playback control"
```

### Task 5: Add visible blockout hands and held-item rendering

**Files:**
- Modify: `dream/ViewModelRenderer.cs`
- Modify: `dream/ModelAssets.cs`
- Modify: `dream/PlayScene.cs`
- Modify: `dream/PlayerViewModelController.cs`
- Add assets: `dream/animations/viewmodels/DebugHands.gltf`, `dream/animations/viewmodels/DebugHands.bin`, `dream/animations/viewmodels/DebugHeldItem.gltf`, `dream/animations/viewmodels/DebugHeldItem.bin`
- Test: `dream/dream.csproj`

- [ ] **Step 1: Add runtime-resolved asset access for the new models**

Extend `dream/ModelAssets.cs` with cached accessors:

```csharp
public static AnimatedModel DebugHands =>
    Cache.GetOrAdd(
        AssetPaths.Animation("viewmodels/DebugHands.gltf"),
        path => new AnimatedModel(path));

public static AnimatedModel DebugHeldItem =>
    Cache.GetOrAdd(
        AssetPaths.Animation("viewmodels/DebugHeldItem.gltf"),
        path => new AnimatedModel(path));
```

- [ ] **Step 2: Add the first blockout assets under the copied runtime asset tree**

For the first implementation, create two simple reusable debug assets:

- `DebugHands.gltf`: a lightweight blockout hands proxy model with `Idle` and `Use` clips
- `DebugHeldItem.gltf`: a simple held-item proxy with `Idle` and `Use` clips

Store them under:

```text
dream/animations/viewmodels/
```

The goal here is not final art quality. The goal is proving that:

- the catalog can swap assets by id
- both slots render independently
- both slots can expose named idle/use clips

- [ ] **Step 3: Implement viewmodel-space transforms and dedicated projection in `ViewModelRenderer`**

Update `dream/ViewModelRenderer.cs`:

```csharp
using OpenTK.Mathematics;

public class ViewModelRenderer
{
    private readonly Render _render;
    private readonly Matrix4 _viewModelView = Matrix4.Identity;

    public ViewModelRenderer(Render render)
    {
        _render = render;
    }

    public void Draw(Camera camera, ViewModelFrameState? frameState)
    {
        if (frameState is null)
        {
            return;
        }

        DrawSlot(ModelAssets.DebugHands, frameState.Value.Hands, 60f);
        DrawSlot(ModelAssets.DebugHeldItem, frameState.Value.HeldItem, 60f);
    }

    private void DrawSlot(AnimatedModel model, ViewModelSlotState slotState, float fovDegrees)
    {
        Matrix4 projection = Matrix4.CreatePerspectiveFieldOfView(
            MathHelper.DegreesToRadians(fovDegrees),
            16f / 9f,
            0.01f,
            10f);

        Matrix4 modelMatrix =
            Matrix4.CreateScale(slotState.Definition.Scale) *
            Matrix4.CreateRotationX(MathHelper.DegreesToRadians(slotState.Definition.RotationOffsetDegrees.X)) *
            Matrix4.CreateRotationY(MathHelper.DegreesToRadians(slotState.Definition.RotationOffsetDegrees.Y)) *
            Matrix4.CreateRotationZ(MathHelper.DegreesToRadians(slotState.Definition.RotationOffsetDegrees.Z)) *
            Matrix4.CreateTranslation(slotState.Definition.PositionOffset);

        model.BeginRender(_render.GetShader("cube"), _viewModelView, projection);
        model.RenderPrepared(_render.GetShader("cube"), modelMatrix);
        model.EndRender();
    }
}
```

The first render pass should deliberately use a viewmodel-specific identity view and local offsets so the assets stay camera-relative.

- [ ] **Step 4: Drive idle/use clip selection from the controller during update**

In `dream/PlayScene.cs`, tick the models each frame:

```csharp
if (ModelAssets.DebugHands.Update((float)args.Time))
{
    _viewModelController.NotifyAnimationFinished(ViewModelSlot.Hands);
}

if (ModelAssets.DebugHeldItem.Update((float)args.Time))
{
    _viewModelController.NotifyAnimationFinished(ViewModelSlot.HeldItem);
}
```

In `dream/PlayerViewModelController.cs`, add helpers that expose desired requests by slot:

```csharp
public ViewModelAnimationRequest GetRequest(ViewModelSlot slot)
{
    return slot == ViewModelSlot.Hands
        ? CurrentFrameState.Hands.AnimationRequest
        : CurrentFrameState.HeldItem.AnimationRequest;
}
```

And in `dream/ViewModelRenderer.cs`, trigger clip playback before drawing:

```csharp
private void ApplyAnimation(AnimatedModel model, ViewModelSlotState slotState)
{
    string? clipName = slotState.AnimationRequest == ViewModelAnimationRequest.Use
        ? slotState.Definition.UseAnimation
        : slotState.Definition.IdleAnimation;

    if (string.IsNullOrWhiteSpace(clipName))
    {
        return;
    }

    if (slotState.AnimationRequest == ViewModelAnimationRequest.Use)
    {
        model.PlayOneShot(clipName);
        return;
    }

    model.PlayLoop(clipName);
}
```

Use this once per slot before `BeginRender(...)`. The `AnimationPlaybackState.Play(...)` idempotency added in Task 4 is what prevents the renderer from restarting the same idle or already-active one-shot clip every frame.

- [ ] **Step 5: Run a full test pass and manual runtime verification**

Run:

```powershell
dotnet test dream.Tests\dream.Tests.csproj --nologo
dotnet run --project dream\dream.csproj
```

Manual checks:

- hands are visible in front of the camera
- held item is visible independently of the hands
- left click visibly triggers use motion
- the world still renders behind the viewmodels
- HUD still renders on top

- [ ] **Step 6: Commit the first visible integration**

```powershell
git add dream/ViewModelRenderer.cs dream/ModelAssets.cs dream/PlayScene.cs dream/PlayerViewModelController.cs dream/animations/viewmodels
git commit -m "feat: add first-person viewmodel rendering"
```

### Task 6: Update documentation and record the work

**Files:**
- Modify: `docs/architecture.md`
- Modify: `docs/rendering-and-assets.md`
- Modify: `docs/build-and-run.md`
- Create: `docs/ledger/work-log/2026-06-25-first-person-viewmodels-and-skins.md`

- [ ] **Step 1: Update architecture docs**

Document:

- the new render order with the viewmodel pass between debug and HUD
- the new `ViewModelRenderer`, `ViewModelCatalog`, and `PlayerViewModelController` responsibilities
- the rule that `Render.cs` remains the shared low-level host rather than the home of feature-specific first-person logic

- [ ] **Step 2: Update rendering/assets docs**

Document:

- first-person assets under `dream/animations/viewmodels/`
- the distinction between world models and first-person viewmodels
- clip naming expectations for `Idle` and `Use`
- blockout asset intent for the first milestone

- [ ] **Step 3: Update build/run guidance**

Document:

- how to verify viewmodels at runtime
- how to trigger use animation
- what “success” looks like for the first milestone

- [ ] **Step 4: Record the work log**

Create `docs/ledger/work-log/2026-06-25-first-person-viewmodels-and-skins.md` with:

- summary of the renderer-first implementation strategy
- files added and modified
- verification commands run
- runtime observations
- follow-up notes for real art replacement and future skin-selection UI

- [ ] **Step 5: Commit the documentation pass**

```powershell
git add docs/architecture.md docs/rendering-and-assets.md docs/build-and-run.md docs/ledger/work-log/2026-06-25-first-person-viewmodels-and-skins.md
git commit -m "docs: record first-person viewmodel foundation"
```

## Self-Review

### Spec coverage

- renderer-first extraction is covered by Task 1
- catalog/loadout interchangeability is covered by Task 2
- controller-owned runtime state is covered by Task 3
- named clip selection is covered by Task 4
- visible hands and held item rendering are covered by Task 5
- docs and work logging are covered by Task 6

No spec gaps remain for the agreed first milestone.

### Placeholder scan

This plan intentionally chooses concrete first assets, concrete class names, exact file paths, and exact commands. It does not rely on `TODO`, `TBD`, or deferred “figure it out later” steps.

### Type consistency

The plan consistently uses:

- `ViewModelSlot`
- `ViewModelDefinition`
- `ViewModelLoadout`
- `ViewModelFrameState`
- `ViewModelSlotState`
- `PlayerViewModelController`
- `AnimationPlaybackState`

Those names are introduced before later tasks depend on them.
