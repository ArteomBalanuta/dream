# Player Controls, Camera, And Settings Analysis

**Purpose:** Analyze the current movement/input, camera projection, and in-game settings behavior in `dream`, and identify the most likely causes behind the reported usability issues.

**Status:** Complete

**Scope:** Player movement, jump responsiveness, camera framing, projection/aspect handling, and settings/UI capabilities

## Summary

The reported issues group into three areas:

1. Player movement and jump feel broken or non-responsive
2. Camera framing feels wrong or overly zoomed
3. There is no usable settings menu for adjusting resolution or related runtime options

From the current code, the settings problem and camera/projection problems are directly confirmed. The movement issue has strong code-level suspects, but it should still be validated during implementation because multiple small issues may be combining into the final symptom.

## Confirmed Findings

### 1. There is no real settings menu

`Render.DrawHUD()` creates a small ImGui window that only displays:

```text
Score: 100
```

The window is explicitly non-interactive:

- `ImGuiWindowFlags.NoInputs`
- `ImGuiWindowFlags.NoResize`
- `ImGuiWindowFlags.NoMove`
- `ImGuiWindowFlags.NoFocusOnAppearing`

`InitializeImGui()` also disables keyboard and mouse interaction:

- `ImGuiConfigFlags.NoKeyboard`
- `ImGuiConfigFlags.NoMouse`

This means there is currently no path for:

- changing resolution
- changing FOV
- changing mouse sensitivity
- toggling fullscreen/windowed behavior
- adjusting runtime graphics preferences

### 2. The projection aspect ratio is hardcoded incorrectly

`Render` currently creates its perspective projection with:

```csharp
Matrix4.CreatePerspectiveFieldOfView(MathHelper.PiOver4, 1.0f, 0.1f, 100.0f);
```

The aspect ratio is fixed at `1.0f`, even though the window starts at `1440 x 1024`. This can distort the scene and make the view feel zoomed or framed incorrectly.

`Game.OnResize()` updates the OpenGL viewport but does not update the camera or render projection.

### 3. The camera position is offset to a corner of the player body

`Player.Update()` sets:

```csharp
Camera.Position = currentPos + new Vector3(-0.5f, -0.5f, -0.5f);
```

This places the camera relative to the lower/back corner of the player box instead of a more natural eye-height position. Even if movement works, this can make the view feel cramped or visually wrong.

### 4. There is no runtime settings state model

There is currently no dedicated structure for:

- current resolution
- FOV
- mouse sensitivity
- graphics/window mode preferences

The relevant values are scattered:

- window size in `Game`
- mouse sensitivity inside `Camera`
- projection setup inside `Render`

This makes settings changes harder to implement cleanly.

## Likely Movement Root Causes

The movement issue is not fully proven from code alone, but these are the strongest suspects.

### 1. Player body activation may be insufficient during movement

`Player` activates the body once in the constructor:

```csharp
PhysicsBody.Activate();
```

Movement then relies on repeated impulses, but the code does not explicitly reactivate the rigid body when movement input is applied each frame. If the rigid body is entering a sleeping state, movement and jump can appear dead or inconsistent.

### 2. Movement is impulse-based without a more stable character-controller model

Current movement works by computing a velocity delta and applying a central impulse. This can be fragile for player control because it depends on:

- current velocity
- body sleep/activation state
- contact/friction behavior
- frame delta

It is workable, but not very robust for first-person controls.

### 3. Jumping has no grounded-state protection or diagnostics

Jump is currently:

```csharp
PhysicsBody.ApplyCentralImpulse(new BulletSharp.Math.Vector3(0, 0.5f, 0));
```

There is:

- no grounded check
- no jump cooldown
- no explicit confirmation that the impulse is being applied while the body is active

This makes jump behavior hard to reason about and hard to tune.

### 4. Camera and movement feel may be compounding each other

Even if impulses are being applied, the player experience can still feel wrong because:

- the projection aspect ratio is incorrect
- the camera is offset unnaturally
- the user has no way to tune sensitivity or FOV

So the “I can’t move” report may be partly pure movement failure and partly severe movement readability problems.

## Confirmed Code Areas To Change

### `Game.cs`

Needs a better window/settings integration layer and proper resize handling for projection updates.

### `Render.cs`

Needs projection ownership that can react to window size and user-configurable FOV.

### `Player.cs`

Needs movement/jump debugging and likely control-model improvements.

### `Camera.cs`

Needs configurable sensitivity and likely a cleaner eye-position model.

### New settings/UI components

The project needs dedicated structures for runtime settings and a real interactive settings UI.

## Recommended Direction

The best way to address all three user-facing problems together is:

### Track A: Restore reliable movement and jump

- validate rigid body activation behavior
- make movement more explicit and easier to reason about
- add guardrails around jump behavior

### Track B: Fix camera framing

- make projection aspect ratio reflect actual window size
- support configurable FOV
- move the camera to a better eye position relative to the player body

### Track C: Add a usable settings menu

- introduce a runtime settings model
- add an interactive in-game settings panel
- include at least:
  - resolution display or selection
  - FOV
  - mouse sensitivity
  - window/display mode if practical

## Scope Guidance

This should be treated as a bounded gameplay/usability modernization, not a full engine rewrite.

Included:

- movement reliability
- jump reliability
- projection and camera framing fixes
- settings menu

Deferred:

- full character controller rewrite unless the current physics approach proves untenable
- complete UI framework overhaul
- complete rendering architecture redesign
