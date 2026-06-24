# Phase 1 Shell Vs Play Scene Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extract active gameplay orchestration out of `Game` into a dedicated `PlayScene` while preserving current runtime behavior.

**Architecture:** `Game` will remain the OpenTK window shell and lifecycle owner, while a new `PlayScene` will own player, map, physics, renderer, gameplay update/render flow, and scene-specific settings handling. This is a structural extraction, not a gameplay redesign.

**Tech Stack:** C#/.NET 8, OpenTK, BulletSharp, ImGui.NET, current runtime architecture

---

### Task 1: Add a failing seam test or smallest verifiable extraction target

**Files:**
- Optional Create: `dream.Tests/PlaySceneTests.cs`
- Modify: `dream.Tests/dream.Tests.csproj` only if needed by new test file discovery

- [ ] Add a small test only if a pure helper or constructor boundary is introduced that can be validated without booting OpenTK
- [ ] Skip artificial tests for `GameWindow` lifecycle plumbing if they would add low-value harness complexity
- [ ] Treat build plus existing suite as the main regression safety net for this phase

### Task 2: Introduce `PlayScene` as the gameplay owner

**Files:**
- Create: `dream/PlayScene.cs`
- Modify: `dream/Game.cs`

- [ ] Move ownership of `Render`, `Player`, `Map`, and `PhysicsIntegrator` into `PlayScene`
- [ ] Give `PlayScene` a constructor that receives the settings object and any shell-owned context it truly needs
- [ ] Keep `Game` responsible only for window shell concerns plus current-scene delegation

### Task 3: Move gameplay update orchestration into `PlayScene`

**Files:**
- Modify: `dream/PlayScene.cs`
- Modify: `dream/Game.cs`

- [ ] Move physics stepping, player update, cube placement, and dynamic-body sync out of `Game.OnUpdateFrame()`
- [ ] Add a `PlayScene.Update(...)` entry point that accepts the current frame/input state explicitly
- [ ] Keep gameplay behavior identical to the current order of operations

### Task 4: Move gameplay rendering and runtime scene behavior into `PlayScene`

**Files:**
- Modify: `dream/PlayScene.cs`
- Modify: `dream/Game.cs`

- [ ] Move world rendering, collision debug rendering, HUD/settings handling, and floor rebuild behavior into `PlayScene.Render(...)`
- [ ] Keep `SwapBuffers()` and FPS/title updates in `Game`
- [ ] Preserve runtime resolution changes and floor rebuild handling semantics

### Task 5: Move scene-specific resize and dispose behavior

**Files:**
- Modify: `dream/PlayScene.cs`
- Modify: `dream/Game.cs`

- [ ] Add `PlayScene.Resize(...)` for projection/settings updates needed on resize
- [ ] Add `PlayScene.Dispose()` for scene-owned runtime cleanup
- [ ] Keep `Game.OnResize()` and `Game.OnUnload()` as shell callbacks that delegate appropriately

### Task 6: Update docs and ledger

**Files:**
- Modify: `README.md`
- Modify: `docs/architecture.md`
- Modify: `docs/project-structure.md`
- Create: `docs/ledger/work-log/2026-06-24-phase1-shell-vs-play-scene.md`

- [ ] Document the new `Game` vs `PlayScene` ownership split
- [ ] Update the architecture description to explain shell delegation
- [ ] Record verification commands and observed results

### Task 7: Verify build, tests, and controlled startup

**Files:**
- Test: `dream/dream.csproj`
- Test: `dream.Tests/dream.Tests.csproj`

- [ ] Run `dotnet test dream.Tests\\dream.Tests.csproj --nologo`
- [ ] Run `dotnet build dream\\dream.csproj --nologo`
- [ ] Run a controlled startup of the application
- [ ] Record any runtime-verification limits honestly

## Self-Review Notes

- Spec coverage: the plan covers shell ownership, scene ownership, update/render delegation, resize/unload delegation, docs, and verification
- Placeholder scan: no `TBD`, `TODO`, or implied “figure it out later” steps remain
- Type consistency: `PlayScene` is the single extracted type used consistently across all tasks
