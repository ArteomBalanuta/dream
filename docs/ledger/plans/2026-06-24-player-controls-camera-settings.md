# Player Controls, Camera, And Settings Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix non-responsive player movement, correct the camera/projection feel, and add a usable in-game settings UI for core runtime controls.

**Architecture:** The work will improve the existing physics-driven player controller rather than replacing it outright, introduce clearer runtime settings state for camera and window behavior, update rendering to use actual window dimensions and configurable FOV, and add an interactive settings panel layered on top of the current HUD flow.

**Tech Stack:** C#/.NET 8, OpenTK, BulletSharp, ImGui.NET, existing game/render architecture

---

### Task 1: Reproduce and stabilize player movement with testable rules

**Files:**
- Modify: `dream/Player.cs`
- Modify: `dream/PhysicsIntegrator.cs`
- Optional Create: helper file if movement state needs extraction

- [ ] **Step 1: Add temporary diagnostics to confirm movement input and body activation**

Capture:

- whether `KeyboardState` sees `W`, `A`, `S`, `D`, and `Space`
- whether the player body is active
- whether impulses are being applied

- [ ] **Step 2: Make movement input explicitly activate the rigid body**

Ensure movement and jump input wake the player body before applying impulses.

- [ ] **Step 3: Add a grounded or near-grounded jump rule**

Prevent jump from being an unbounded raw impulse and make it easier to reason about.

- [ ] **Step 4: Re-tune movement/jump constants if needed after activation fixes**

Keep the control model bounded; do not switch to a full new controller unless the current approach still fails after the above fixes.

### Task 2: Fix camera framing and projection behavior

**Files:**
- Modify: `dream/Camera.cs`
- Modify: `dream/Render.cs`
- Modify: `dream/Game.cs`

- [ ] **Step 1: Replace the hardcoded projection aspect ratio**

Make projection depend on the actual window aspect ratio instead of `1.0f`.

- [ ] **Step 2: Update projection on resize**

Wire `Game.OnResize()` into render projection updates.

- [ ] **Step 3: Introduce configurable FOV**

Move FOV from a hardcoded constructor value into a runtime setting or explicit render field.

- [ ] **Step 4: Move the camera to a more natural eye offset**

Replace the current `(-0.5f, -0.5f, -0.5f)` offset with a clearer eye-height-oriented model.

### Task 3: Add runtime settings state and a usable settings menu

**Files:**
- Create: `dream/GameSettings.cs`
- Modify: `dream/Game.cs`
- Modify: `dream/Render.cs`
- Modify: `dream/Camera.cs`

- [ ] **Step 1: Add a small runtime settings state object**

Include at least:

- FOV
- mouse sensitivity
- current resolution values

- [ ] **Step 2: Connect camera and render behavior to settings state**

Use settings for:

- FOV
- mouse sensitivity
- displayed window dimensions

- [ ] **Step 3: Add a settings panel in the HUD/UI layer**

Support:

- opening the panel
- editing FOV
- editing mouse sensitivity
- showing current resolution

- [ ] **Step 4: Make the UI interaction model workable**

Ensure gameplay input and settings input do not permanently fight each other.

### Task 4: Update documentation and ledger

**Files:**
- Modify: `README.md`
- Modify: `docs/configuration.md`
- Modify: `docs/rendering-and-assets.md`
- Create: `docs/ledger/work-log/YYYY-MM-DD-player-controls-camera-settings.md`

- [ ] **Step 1: Update docs for the new settings and projection behavior**

- [ ] **Step 2: Record the implementation and verification in the ledger**

### Task 5: Verify build and runtime behavior

**Files:**
- Test: `dream/dream.csproj`
- Test: `dream/Player.cs`
- Test: `dream/Render.cs`
- Test: `dream/Game.cs`
- Test: `dream/Camera.cs`

- [ ] **Step 1: Run build verification**

```powershell
dotnet build dream\dream.csproj
```

- [ ] **Step 2: Run the project and verify controls manually**

Verify:

- movement works
- jump works
- camera framing feels corrected
- settings UI opens and updates values

- [ ] **Step 3: Re-check scope**

Confirm the work solved the usability issues without turning into a broad engine rewrite
