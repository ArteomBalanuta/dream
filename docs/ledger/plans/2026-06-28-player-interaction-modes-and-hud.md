# Player Interaction Modes And HUD Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Split gameplay primary use into explicit build versus combat modes, and surface player HP plus active mode in the existing top-left HUD.

**Architecture:** Keep `RuntimeMode` responsible for gameplay-versus-menu gating, and add a separate `PlayerInteractionMode` owned by `PlayScene` for gameplay intent. Route left click through one scene-owned branch only, and keep HUD rendering display-only by passing it a small status snapshot instead of gameplay logic.

**Tech Stack:** C# / .NET 10, OpenTK, ImGui.NET, BulletSharp, xUnit, existing `PlayScene`, `Player`, `PlayerController`, `HudRenderer`, and `Render`

---

### Task 1: Add pure mode and HUD status types with tests

**Files:**
- Create: `dream/PlayerInteractionMode.cs`
- Create: `dream/HudStatusSnapshot.cs`
- Create: `dream.Tests/PlayerInteractionModeTests.cs`
- Modify: `dream.Tests/dream.Tests.csproj`

- [ ] **Step 1: Write the failing mode tests**

Create `dream.Tests/PlayerInteractionModeTests.cs`:

```csharp
using Xunit;

public class PlayerInteractionModeTests
{
    [Fact]
    public void Default_Mode_Is_Building()
    {
        Assert.Equal(PlayerInteractionMode.Building, PlayerInteractionMode.Building);
    }

    [Theory]
    [InlineData(PlayerInteractionMode.Building, "Building")]
    [InlineData(PlayerInteractionMode.Combat, "Combat")]
    public void ToHudLabel_Returns_Player_Facing_Name(PlayerInteractionMode mode, string expected)
    {
        Assert.Equal(expected, mode.ToHudLabel());
    }
}

public class HudStatusSnapshotTests
{
    [Fact]
    public void Create_Exposes_Current_Health_And_Mode()
    {
        HudStatusSnapshot snapshot = new(82, 100, PlayerInteractionMode.Combat);

        Assert.Equal(82, snapshot.CurrentHealth);
        Assert.Equal(100, snapshot.MaxHealth);
        Assert.Equal(PlayerInteractionMode.Combat, snapshot.InteractionMode);
        Assert.Equal("HP: 82/100", snapshot.HealthLabel);
        Assert.Equal("Mode: Combat", snapshot.ModeLabel);
    }
}
```

- [ ] **Step 2: Run the focused tests to confirm missing types**

Run: `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~PlayerInteractionModeTests|FullyQualifiedName~HudStatusSnapshotTests" --nologo`

Expected: FAIL with missing-type errors for `PlayerInteractionMode` and `HudStatusSnapshot`.

- [ ] **Step 3: Add the new gameplay-mode enum**

Create `dream/PlayerInteractionMode.cs`:

```csharp
public enum PlayerInteractionMode
{
    Building,
    Combat
}

public static class PlayerInteractionModeExtensions
{
    public static string ToHudLabel(this PlayerInteractionMode mode)
    {
        return mode switch
        {
            PlayerInteractionMode.Building => "Building",
            PlayerInteractionMode.Combat => "Combat",
            _ => mode.ToString()
        };
    }
}
```

- [ ] **Step 4: Add the HUD status snapshot**

Create `dream/HudStatusSnapshot.cs`:

```csharp
public readonly record struct HudStatusSnapshot(
    int CurrentHealth,
    int MaxHealth,
    PlayerInteractionMode InteractionMode)
{
    public string HealthLabel => $"HP: {CurrentHealth}/{MaxHealth}";
    public string ModeLabel => $"Mode: {InteractionMode.ToHudLabel()}";
}
```

- [ ] **Step 5: Re-run the focused tests**

Run: `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~PlayerInteractionModeTests|FullyQualifiedName~HudStatusSnapshotTests" --nologo`

Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add dream/PlayerInteractionMode.cs dream/HudStatusSnapshot.cs dream.Tests/PlayerInteractionModeTests.cs
git commit -m "test: add interaction mode and HUD status model coverage"
```

### Task 2: Route gameplay input through the active interaction mode

**Files:**
- Modify: `dream/PlayerCommands.cs`
- Modify: `dream/PlayerController.cs`
- Modify: `dream/Player.cs`
- Modify: `dream/PlayScene.cs`
- Modify: `dream.Tests/PlayerViewModelControllerTests.cs`

- [ ] **Step 1: Extend the interaction command to carry mode-selection input**

Update `dream/PlayerCommands.cs`:

```csharp
public readonly record struct PlayerInteractionCommand(
    bool PrimaryUsePressed,
    bool SelectBuildingModePressed,
    bool SelectCombatModePressed);
```

- [ ] **Step 2: Add a failing controller test for direct mode hotkeys**

Append to `dream.Tests/PlayerViewModelControllerTests.cs` or move the interaction tests into a dedicated file:

```csharp
[Fact]
public void CaptureInteraction_Maps_Number_Keys_To_Mode_Selection()
{
    PlayerController controller = new();

    PlayerInteractionCommand command = controller.CaptureInteraction(
        new KeyboardState(
            new[]
            {
                OpenTK.Windowing.GraphicsLibraryFramework.Keys.Number1,
                OpenTK.Windowing.GraphicsLibraryFramework.Keys.Number2
            },
            Array.Empty<OpenTK.Windowing.GraphicsLibraryFramework.Keys>()),
        new MouseState(),
        allowGameplayInput: true);

    Assert.True(command.SelectBuildingModePressed || command.SelectCombatModePressed);
}
```

- [ ] **Step 3: Update controller capture to include keyboard mode hotkeys**

Update `dream/PlayerController.cs`:

```csharp
public PlayerInteractionCommand CaptureInteraction(
    KeyboardState keyboardState,
    MouseState mouseState,
    bool allowGameplayInput)
{
    return new PlayerInteractionCommand(
        PrimaryUsePressed: allowGameplayInput && mouseState.IsButtonDown(MouseButton.Left),
        SelectBuildingModePressed: allowGameplayInput && keyboardState.IsKeyDown(Keys.Number1),
        SelectCombatModePressed: allowGameplayInput && keyboardState.IsKeyDown(Keys.Number2));
}
```

- [ ] **Step 4: Expose the richer interaction command from `Player`**

Add to `dream/Player.cs`:

```csharp
public PlayerInteractionCommand CaptureInteractionCommand(
    KeyboardState keyboardState,
    MouseState mouseState,
    bool allowInput)
{
    return _controller.CaptureInteraction(keyboardState, mouseState, allowInput);
}
```

And change `ConsumePrimaryUsePressed(...)` to accept the already-captured command:

```csharp
public bool ConsumePrimaryUsePressed(PlayerInteractionCommand interaction)
{
    _primaryUseTriggered = _interaction.CapturePrimaryUse(interaction);
    return _primaryUseTriggered;
}
```

- [ ] **Step 5: Add scene-owned mode state and single-route action dispatch**

Update `dream/PlayScene.cs` with a field and helpers:

```csharp
private PlayerInteractionMode _interactionMode = PlayerInteractionMode.Building;

private void ApplyInteractionModeSelection(PlayerInteractionCommand command)
{
    if (command.SelectBuildingModePressed)
    {
        _interactionMode = PlayerInteractionMode.Building;
    }
    else if (command.SelectCombatModePressed)
    {
        _interactionMode = PlayerInteractionMode.Combat;
    }
}
```

Then update the top of `Update(...)`:

```csharp
bool allowGameplayInput = _settings.CurrentRuntimeState.AllowGameplayInput;
PlayerInteractionCommand interaction = _player.CaptureInteractionCommand(keyboardState, mouseState, allowGameplayInput);
ApplyInteractionModeSelection(interaction);
bool primaryUseTriggered = _player.ConsumePrimaryUsePressed(interaction);

if (primaryUseTriggered)
{
    _viewModelController.TriggerPrimaryUse();

    if (_interactionMode == PlayerInteractionMode.Combat)
    {
        _events.Publish(new DamageAppliedEvent(
            _player.Combatant.Actor,
            Random.Shared.Next(120, 481),
            FloatingCombatTextKind.Regular));
    }
}
```

And replace the unconditional placement call:

```csharp
if (_interactionMode == PlayerInteractionMode.Building)
{
    _player.PlaceCube(_map);
}
```

- [ ] **Step 6: Add a rising-edge safety test and re-run focused tests**

Keep or extend the existing `CapturePrimaryUse_ReturnsRisingEdgeOnly` test so it uses the new command shape:

```csharp
Assert.True(interaction.CapturePrimaryUse(new PlayerInteractionCommand(true, false, false)));
Assert.False(interaction.CapturePrimaryUse(new PlayerInteractionCommand(true, false, false)));
Assert.False(interaction.CapturePrimaryUse(new PlayerInteractionCommand(false, false, false)));
Assert.True(interaction.CapturePrimaryUse(new PlayerInteractionCommand(true, false, false)));
```

Run: `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~PlayerInteractionTests|FullyQualifiedName~PlayerInteractionModeTests" --nologo`

Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add dream/PlayerCommands.cs dream/PlayerController.cs dream/Player.cs dream/PlayScene.cs dream.Tests/PlayerViewModelControllerTests.cs dream.Tests/PlayerInteractionModeTests.cs
git commit -m "feat: route primary use through interaction mode"
```

### Task 3: Feed player HP and mode into the existing top-left HUD

**Files:**
- Modify: `dream/HudRenderer.cs`
- Modify: `dream/Render.cs`
- Modify: `dream/PlayScene.cs`
- Modify: `dream.Tests/HudLayoutTests.cs`

- [ ] **Step 1: Add a failing HUD snapshot test**

Append to `dream.Tests/HudLayoutTests.cs`:

```csharp
[Fact]
public void HudStatusSnapshot_Formats_Top_Left_Status_Lines()
{
    HudStatusSnapshot snapshot = new(100, 100, PlayerInteractionMode.Building);

    Assert.Equal("HP: 100/100", snapshot.HealthLabel);
    Assert.Equal("Mode: Building", snapshot.ModeLabel);
}
```

- [ ] **Step 2: Thread the HUD snapshot through the render entry point**

Update `dream/HudRenderer.cs`:

```csharp
public HudFrameResult DrawFrame(
    FrameEventArgs args,
    bool windowFocused,
    GameSettings settings,
    MouseState mouseState,
    Camera camera,
    CombatFeedbackSystem combatFeedback,
    HudStatusSnapshot status)
{
    return _render.DrawHUD(args, windowFocused, settings, mouseState, camera, combatFeedback, status);
}
```

Update the `DrawHUD(...)` signature in `dream/Render.cs` the same way.

- [ ] **Step 3: Build the snapshot in `PlayScene.Render(...)`**

Add in `dream/PlayScene.cs` before the HUD draw:

```csharp
HudStatusSnapshot hudStatus = new(
    _player.Combatant.Health.CurrentHealth,
    _player.Combatant.Health.MaxHealth,
    _interactionMode);
```

Then pass it into:

```csharp
HudFrameResult hudResult =
    _render.Hud.DrawFrame(args, windowFocused, _settings, mouseState, _player.Camera, _combatFeedback, hudStatus);
```

- [ ] **Step 4: Replace placeholder top-left lines with player status**

Update the top-left block in `dream/Render.cs`:

```csharp
ImGui.Text(status.HealthLabel);
ImGui.Text(status.ModeLabel);
ImGui.Text($"Resolution: {settings.WindowWidth} x {settings.WindowHeight}");
ImGui.Text("Press Esc to toggle settings");
```

This replaces the current placeholder line:

```csharp
ImGui.Text("Score: 100");
```

- [ ] **Step 5: Re-run HUD-focused tests**

Run: `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~HudLayoutTests|FullyQualifiedName~HudStatusSnapshotTests" --nologo`

Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add dream/HudRenderer.cs dream/Render.cs dream/PlayScene.cs dream.Tests/HudLayoutTests.cs
git commit -m "feat: show HP and interaction mode in gameplay HUD"
```

### Task 4: Verify the slice and record it in the ledger

**Files:**
- Create: `docs/ledger/work-log/2026-06-28-player-interaction-modes-and-hud.md`
- Modify: `README.md`
- Modify: `docs/architecture.md`

- [ ] **Step 1: Update the controls documentation**

Adjust `README.md` controls to reflect mode selection:

```md
- `1`: select building mode
- `2`: select combat mode
- Left mouse button: perform the active mode's primary action
```

- [ ] **Step 2: Update the architecture note**

Add a short note to `docs/architecture.md`:

```md
`PlayScene` now owns gameplay interaction mode selection and routes primary use into either build placement or combat feedback, while the HUD consumes a display-only status snapshot.
```

- [ ] **Step 3: Add the work-log entry**

Create `docs/ledger/work-log/2026-06-28-player-interaction-modes-and-hud.md`:

```md
Purpose: Record the split between build and combat primary-use routing and the gameplay HUD status upgrade.
Status: Completed
Scope: Interaction mode input, primary-use routing, and top-left HUD status

# Summary

Added explicit building/combat interaction modes, removed the shared left-click double action, and upgraded the existing top-left HUD to show player HP plus active mode.

# Verification

- `dotnet build dream\\dream.csproj --nologo`
- `dotnet test dream.Tests\\dream.Tests.csproj --nologo`
```

- [ ] **Step 4: Run full verification**

Run: `dotnet build dream\\dream.csproj --nologo`

Expected: PASS with the existing BulletSharp warning only.

Run: `dotnet test dream.Tests\\dream.Tests.csproj --nologo`

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add README.md docs/architecture.md docs/ledger/work-log/2026-06-28-player-interaction-modes-and-hud.md
git commit -m "docs: record interaction mode HUD update"
```
