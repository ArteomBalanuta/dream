# Phase 3 Player System Decomposition Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Split `Player.cs` into smaller gameplay units so input, movement, camera, and interaction logic can evolve independently.

**Architecture:** The refactor will keep the existing player behavior but divide responsibilities into focused collaborators such as controller, motor, camera rig, and optional interaction logic. The intention is to make future features like sprint, crouch, tools, and alternate movement modes cheaper to add.

**Tech Stack:** C#/.NET 8, OpenTK, BulletSharp

---

### Task 1: Define player responsibility boundaries

**Files:**
- Modify: `dream/Player.cs`
- Create: `dream/PlayerController.cs`
- Create: `dream/PlayerMotor.cs`
- Create: `dream/PlayerCameraRig.cs`

- [ ] Decide which responsibilities remain on `Player` as a composition root
- [ ] Move input interpretation into `PlayerController`
- [ ] Move movement/jump/freeze motor rules into `PlayerMotor`
- [ ] Move camera follow/look responsibilities into `PlayerCameraRig`

### Task 2: Extract interaction logic

**Files:**
- Create: `dream/PlayerInteraction.cs`
- Modify: `dream/Player.cs`

- [ ] Move cube-placement and future interaction hooks into a dedicated interaction-focused class
- [ ] Keep interaction APIs simple and centered on gameplay intent rather than raw mouse branching

### Task 3: Preserve public player seam

**Files:**
- Modify: `dream/Player.cs`
- Modify: dependent callers such as `dream/Game.cs` or `dream/PlayScene.cs`

- [ ] Keep the top-level player API stable enough that callers do not need a broad rewrite
- [ ] Ensure existing features continue to work through the new composed structure

### Task 4: Add or adjust focused tests

**Files:**
- Create: `dream.Tests/PlayerMotorTests.cs`
- Optional Create: `dream.Tests/PlayerInteractionTests.cs`

- [ ] Add tests only for extracted pure or low-harness logic where they provide value
- [ ] Avoid fake-heavy tests that only mirror current implementation detail

### Task 5: Verify and document

**Files:**
- Modify: `docs/project-structure.md`
- Create: `docs/ledger/work-log/2026-06-24-phase3-player-system-decomposition.md`

- [ ] Run `dotnet test dream.Tests\\dream.Tests.csproj --nologo`
- [ ] Run `dotnet build dream\\dream.csproj --nologo`
- [ ] Run a controlled startup and record limitations honestly
