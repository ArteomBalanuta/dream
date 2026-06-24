# Phase 5 Physics Wrapper Refinement Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Refine the Bullet integration boundary so low-level physics ownership, body creation, and state sync are clearer and less coupled to current gameplay assumptions.

**Architecture:** The current `PhysicsIntegrator` façade will be decomposed into smaller physics-focused roles such as world ownership, body creation, and transform sync. The public runtime behavior should remain unchanged.

**Tech Stack:** C#/.NET 8, BulletSharp

---

### Task 1: Split Bullet world ownership from gameplay factories

**Files:**
- Create: `dream/PhysicsWorld.cs`
- Modify: `dream/PhysicsIntegrator.cs`

- [ ] Move world lifecycle, gravity, solver, and disposal ownership into a dedicated low-level physics world type
- [ ] Keep current initialization settings identical

### Task 2: Extract body creation rules

**Files:**
- Create: `dream/PhysicsFactory.cs`
- Modify: `dream/PhysicsIntegrator.cs`
- Modify: world/player binding call sites

- [ ] Move standard rigid-body construction rules into a factory-focused type
- [ ] Cover player body, cube bodies, and floor bodies through centralized creation helpers

### Task 3: Extract sync responsibilities

**Files:**
- Create: `dream/PhysicsSyncSystem.cs`
- Modify: `dream/Game.cs` or `dream/PlayScene.cs`
- Modify: `dream/PhysicsIntegrator.cs`

- [ ] Move “copy Bullet transform back to gameplay object” behavior out of high-level scene orchestration where possible
- [ ] Keep dynamic-body sync behavior identical

### Task 4: Preserve cleanup and rebuild semantics

**Files:**
- Modify: runtime rebuild/disposal paths

- [ ] Preserve current `ResetMapBodies()` semantics or replace them with clearer equivalent behavior
- [ ] Ensure floor rebuilds and shutdown cleanup remain correct

### Task 5: Verify and document

**Files:**
- Modify: `docs/architecture.md`
- Create: `docs/ledger/work-log/2026-06-24-phase5-physics-wrapper-refinement.md`

- [ ] Run `dotnet test dream.Tests\\dream.Tests.csproj --nologo`
- [ ] Run `dotnet build dream\\dream.csproj --nologo`
- [ ] Record cleanup/rebuild verification notes honestly
