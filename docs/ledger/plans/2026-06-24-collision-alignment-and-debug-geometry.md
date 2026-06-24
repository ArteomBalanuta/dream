# Collision Alignment And Debug Geometry Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Align visible cubes with their authoritative Bullet collision boxes and add a runtime setting that shows red collision wireframes for cubes and the player.

**Architecture:** The fix keeps Bullet as the source of truth, removes the extra visual centering offset from centered cube geometry, and adds a debug overlay rendered directly from Bullet transforms. The settings menu will own a simple boolean runtime toggle for collision geometry visibility.

**Tech Stack:** C#/.NET 8, OpenTK, BulletSharp, ImGui.NET, xUnit

---

### Task 1: Add failing regression tests for transform and debug-setting behavior

**Files:**
- Create: `dream.Tests/CubeTransformTests.cs`
- Modify: `dream.Tests/GameSettingsTests.cs`

- [ ] Add a failing test that expects centered cube geometry to keep local origin at cube center
- [ ] Add a failing test that expects collision debug geometry to default to disabled
- [ ] Add a failing test that expects collision debug geometry to be toggleable

### Task 2: Extract authoritative cube/debug transform helpers

**Files:**
- Create: `dream/CubeTransforms.cs`
- Modify: `dream/Render.cs`

- [ ] Add a helper for centered visual cube model matrices
- [ ] Add a helper for Bullet-driven collision debug model matrices
- [ ] Switch `Render.DrawCube()` to the centered visual helper

### Task 3: Add collision debug runtime state and HUD control

**Files:**
- Modify: `dream/GameSettings.cs`
- Modify: `dream/Render.cs`

- [ ] Add `ShowCollisionGeometry` state and a toggle method or setter path in `GameSettings`
- [ ] Extend the settings window with a collision-geometry checkbox
- [ ] Ensure the HUD interaction flow updates the runtime setting without affecting gameplay input behavior

### Task 4: Draw authoritative collision overlays in-game

**Files:**
- Modify: `dream/Game.cs`
- Modify: `dream/Render.cs`

- [ ] Render red wireframes for map bodies when `ShowCollisionGeometry` is enabled
- [ ] Render a red wireframe for the player body when `ShowCollisionGeometry` is enabled
- [ ] Source all debug transforms from Bullet motion-state/world transforms

### Task 5: Update docs and ledger

**Files:**
- Modify: `README.md`
- Modify: `docs/architecture.md`
- Modify: `docs/configuration.md`
- Create: `docs/ledger/work-log/2026-06-24-collision-alignment-and-debug-geometry.md`

- [ ] Document the new collision debug toggle
- [ ] Document that cube visuals now follow centered unit-cube assumptions
- [ ] Record commands and observed verification results
