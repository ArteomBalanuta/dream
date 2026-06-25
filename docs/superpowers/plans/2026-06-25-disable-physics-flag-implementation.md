# Disable Physics Flag Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a manual `--disable-physics` startup flag that skips Bullet initialization and keeps the game runnable on macOS in a visual-only gameplay mode.

**Architecture:** Parse startup options once in `Program`, thread `PhysicsEnabled` into `Game` and `PlayScene`, and branch at the scene/player seams rather than scattering platform-specific checks. Keep the no-physics path simple by reusing the existing nullable `Map` physics support, adding a direct-movement helper for the player, and suppressing Bullet-only update/render work when physics is disabled.

**Tech Stack:** C# / .NET 10, OpenTK, BulletSharp, xUnit, existing `Game` / `PlayScene` / `Player` gameplay stack.

---

## File Structure

### Existing files to modify

- `dream/Program.cs`
  - Parse CLI args and pass startup options into `Game`.
- `dream/Game.cs`
  - Accept startup options and forward them into `PlayScene`.
- `dream/PlayScene.cs`
  - Make physics optional for startup, update, render, and disposal.
- `dream/Player.cs`
  - Support both Bullet-backed and direct-movement runtime paths.
- `dream/PlayerCameraRig.cs`
  - Support syncing and teleporting from direct world positions.
- `dream/PlayerInteraction.cs`
  - Add a no-physics placement path that uses a direct world position.
- `README.md`
  - Document the new manual startup flag.
- `docs/build-and-run.md`
  - Document the macOS-friendly no-physics run command.

### New files to create

- `dream/StartupOptions.cs`
  - Parse and expose `PhysicsEnabled`.
- `dream/DirectPlayerMotion.cs`
  - Handle simple no-physics movement and freeze toggling.
- `dream.Tests/StartupOptionsTests.cs`
  - Verify flag parsing behavior.
- `dream.Tests/DirectPlayerMotionTests.cs`
  - Verify direct movement and freeze behavior.
- `dream.Tests/PlayerInteractionTests.cs`
  - Verify cube placement works from a direct world position.

## Task 1: Add startup option parsing for `--disable-physics`

**Files:**
- Create: `dream/StartupOptions.cs`
- Modify: `dream/Program.cs`
- Modify: `dream/Game.cs`
- Test: `dream.Tests/StartupOptionsTests.cs`

- [ ] **Step 1: Write the failing startup option tests**

```csharp
using Xunit;

public class StartupOptionsTests
{
    [Fact]
    public void Parse_Defaults_Physics_To_Enabled()
    {
        StartupOptions options = StartupOptions.Parse([]);

        Assert.True(options.PhysicsEnabled);
    }

    [Fact]
    public void Parse_DisablePhysicsFlag_Turns_Physics_Off()
    {
        StartupOptions options = StartupOptions.Parse(["--disable-physics"]);

        Assert.False(options.PhysicsEnabled);
    }
}
```

- [ ] **Step 2: Run the targeted tests to verify they fail**

Run: `dotnet test dream.Tests/dream.Tests.csproj --filter "FullyQualifiedName~StartupOptionsTests" --nologo`
Expected: FAIL with missing `StartupOptions`.

- [ ] **Step 3: Add the startup options model**

```csharp
public readonly record struct StartupOptions(bool PhysicsEnabled)
{
    public static StartupOptions Parse(string[] args)
    {
        bool physicsEnabled = !args.Contains("--disable-physics");
        return new StartupOptions(physicsEnabled);
    }
}
```

- [ ] **Step 4: Thread startup options into runtime startup**

```csharp
public static class Program
{
    public static void Main(string[] args)
    {
        StartupOptions options = StartupOptions.Parse(args);

        using var game = new Game(options);
        game.Run();
    }
}
```

```csharp
public class Game : GameWindow
{
    public Game(StartupOptions startupOptions) : base(...)
    {
        _settings = new GameSettings(new Vector2i(1440, 1024));
        _playScene = new PlayScene(_settings, startupOptions);
        CursorState = _playScene.DesiredCursorState;
    }
}
```

- [ ] **Step 5: Run the targeted tests to verify they pass**

Run: `dotnet test dream.Tests/dream.Tests.csproj --filter "FullyQualifiedName~StartupOptionsTests" --nologo`
Expected: PASS with `2` tests.

## Task 2: Add a no-physics direct movement and placement path

**Files:**
- Create: `dream/DirectPlayerMotion.cs`
- Modify: `dream/Player.cs`
- Modify: `dream/PlayerCameraRig.cs`
- Modify: `dream/PlayerInteraction.cs`
- Test: `dream.Tests/DirectPlayerMotionTests.cs`
- Test: `dream.Tests/PlayerInteractionTests.cs`

- [ ] **Step 1: Write the failing direct-movement and placement tests**

```csharp
using OpenTK.Mathematics;
using Xunit;

public class DirectPlayerMotionTests
{
    [Fact]
    public void Update_WhenGameplayInputAllowed_Moves_In_WorldDirection()
    {
        var motion = new DirectPlayerMotion();
        var settings = new GameSettings(new Vector2i(1440, 1024));

        Vector3 next = motion.Update(
            currentPosition: new Vector3(5f, 1.5f, 5f),
            command: new MovementCommand(Vector3.UnitX, false, false),
            settings,
            deltaTime: 0.5f,
            allowGameplayInput: true);

        Assert.Equal(new Vector3(8f, 1.5f, 5f), next);
    }

    [Fact]
    public void Update_WhenFrozen_Does_Not_Move()
    {
        var motion = new DirectPlayerMotion();
        var settings = new GameSettings(new Vector2i(1440, 1024));

        _ = motion.Update(new Vector3(5f, 1.5f, 5f), new MovementCommand(Vector3.Zero, false, true), settings, 0.016f, true);
        Vector3 next = motion.Update(new Vector3(5f, 1.5f, 5f), new MovementCommand(Vector3.UnitZ, false, false), settings, 1f, true);

        Assert.Equal(new Vector3(5f, 1.5f, 5f), next);
        Assert.True(motion.IsFrozen);
    }
}
```

```csharp
using OpenTK.Mathematics;
using Xunit;

public class PlayerInteractionTests
{
    [Fact]
    public void TryPlaceCube_Uses_DirectWorldPosition_When_Physics_Is_Disabled()
    {
        var interaction = new PlayerInteraction();
        var map = new Map(loadVisualAssets: false);
        var camera = new Camera(new Vector3(5f, 1.85f, 5f));

        interaction.TryPlaceCube(map, primaryUseTriggered: true, camera, new Vector3(5f, 1.5f, 5f));

        Assert.NotNull(map.Cubes[5, 1, 3]);
    }
}
```

- [ ] **Step 2: Run the targeted tests to verify they fail**

Run: `dotnet test dream.Tests/dream.Tests.csproj --filter "FullyQualifiedName~DirectPlayerMotionTests|FullyQualifiedName~PlayerInteractionTests" --nologo`
Expected: FAIL with missing direct movement type and placement overload.

- [ ] **Step 3: Add the direct movement helper and no-physics placement overload**

```csharp
using OpenTK.Mathematics;

public class DirectPlayerMotion
{
    private bool _wasFreezePressed;

    public bool IsFrozen { get; private set; }

    public Vector3 Update(Vector3 currentPosition, MovementCommand command, GameSettings settings, float deltaTime, bool allowGameplayInput)
    {
        if (command.FreezePressed && !_wasFreezePressed)
        {
            IsFrozen = !IsFrozen;
        }

        _wasFreezePressed = command.FreezePressed;

        if (!allowGameplayInput || IsFrozen)
        {
            return currentPosition;
        }

        return currentPosition + (command.WorldDirection * settings.MoveSpeed * deltaTime);
    }
}
```

```csharp
public void TryPlaceCube(Map map, bool primaryUseTriggered, Camera camera, Vector3 worldPosition)
{
    if (!primaryUseTriggered)
    {
        return;
    }

    Vector3 position = worldPosition + camera.Front * 2.0f;
    map.AddCube(position, withPhysics: false);
}
```

- [ ] **Step 4: Update `Player` and `PlayerCameraRig` to use optional physics**

```csharp
public Vector3 SyncFromPosition(Vector3 position, GameSettings settings)
{
    Camera.Position = position + new Vector3(0f, settings.EyeHeightOffset, 0f);
    Camera.MouseSensitivity = settings.MouseSensitivity;
    return position;
}
```

```csharp
public void Teleport(Vector3 position, float eyeHeightOffset)
{
    Camera.Position = position + new Vector3(0f, eyeHeightOffset, 0f);
}
```

```csharp
if (PhysicsBody != null)
{
    // Existing Bullet-backed path
}
else
{
    Vector3 currentPosition = _cameraRig.SyncFromPosition(_worldPosition, settings);
    MovementCommand movement = _controller.CaptureMovement(Camera, keyboardState, allowInput);
    _worldPosition = _directMotion.Update(currentPosition, movement, settings, deltaTime, allowInput);
    _cameraRig.SyncFromPosition(_worldPosition, settings);
    _cameraRig.UpdateLook(deltaTime, mouseState, allowInput, _directMotion.IsFrozen);
    LastReportedPosition = _worldPosition;
}
```

- [ ] **Step 5: Run the targeted tests to verify they pass**

Run: `dotnet test dream.Tests/dream.Tests.csproj --filter "FullyQualifiedName~DirectPlayerMotionTests|FullyQualifiedName~PlayerInteractionTests" --nologo`
Expected: PASS with the new no-physics unit tests.

## Task 3: Make `PlayScene` startup and render/update flow physics-optional

**Files:**
- Modify: `dream/PlayScene.cs`
- Modify: `README.md`
- Modify: `docs/build-and-run.md`

- [ ] **Step 1: Write the scene changes to branch physics once at startup**

```csharp
public PlayScene(GameSettings settings, StartupOptions startupOptions)
{
    _settings = settings;
    _physics = startupOptions.PhysicsEnabled ? new PhysicsIntegrator() : null;
    _map = new Map(_physics, width: _settings.FloorSize, depth: _settings.FloorSize);
    _viewModelController = new PlayerViewModelController(ViewModelCatalog.CreateDefault(), _settings);

    Vector3 playerStartPosition = new Vector3(5, 1.5f, 5);
    _player = new Player(_map, _physics, _physics?.PlayerBody, playerStartPosition);
    _render = new Render(_settings);
}
```

- [ ] **Step 2: Guard Bullet-only update and render behavior**

```csharp
if (_physics != null)
{
    _physics.StepSimulation(delta);
}

_player.Update(delta, keyboardState, mouseState, _settings, allowGameplayInput);
_player.PlaceCube(_map);

if (_physics != null)
{
    _physicsSync.SyncDynamicMapBodies(_physics.DynamicMapCubeBodies);
}
```

```csharp
if (_settings.ShowCollisionGeometry && _physics != null)
{
    _render.Debug.DrawCollisionBodies(_physics.MapCubeBodies, _player.Camera);
    _render.DrawCollisionBody(_physics.PlayerBody, _player.Camera);
}
```

- [ ] **Step 3: Document the manual flag**

```md
dotnet run --project dream/dream.csproj -- --disable-physics
```

Add a short note that this mode is intended for macOS or other environments where Bullet native libraries are unavailable, and that collisions/gravity/debug physics rendering are intentionally disabled.

- [ ] **Step 4: Run full verification**

Run: `dotnet build dream/dream.csproj --nologo`
Expected: PASS with existing warnings.

Run: `dotnet test dream.Tests/dream.Tests.csproj --nologo`
Expected: PASS with all tests green.

Run: `dotnet run --project dream/dream.csproj -- --disable-physics`
Expected: app starts successfully instead of throwing `DllNotFoundException`.

Run: `dotnet run --project dream/dream.csproj`
Expected: current default startup path is unchanged on machines where Bullet is available.
