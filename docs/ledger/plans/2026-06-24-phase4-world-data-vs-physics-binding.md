# Phase 4 World Data Vs Physics Binding Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Separate authoritative world/cell data from generation rules and Bullet body binding so the world model becomes easier to extend, rebuild, and save.

**Architecture:** The refactor will split the current `Map` responsibilities into a world-data layer, a generation layer, and a physics-binding layer. The existing sparse-storage and floor-rebuild improvements remain in place; this phase is about cleaner ownership boundaries.

**Tech Stack:** C#/.NET 8, BulletSharp, current voxel/world model

---

### Task 1: Extract authoritative world data model

**Files:**
- Create: `dream/WorldGrid.cs` or `dream/VoxelWorld.cs`
- Modify: `dream/Map.cs`

- [ ] Move cell storage, bounds, and solid-content tracking into an authoritative world-data type
- [ ] Keep world coordinates and cube-data semantics unchanged

### Task 2: Extract generation responsibilities

**Files:**
- Create: `dream/WorldGenerator.cs`
- Modify: `dream/Map.cs` or replacement composition root

- [ ] Move floor/bootstrap generation into a generator-focused type
- [ ] Keep current floor layout and startup behavior identical

### Task 3: Extract physics binding responsibilities

**Files:**
- Create: `dream/WorldPhysicsBinder.cs`
- Modify: `dream/Map.cs`
- Modify: `dream/PhysicsIntegrator.cs`

- [ ] Move rigid-body creation/removal for world content into a physics-binding type
- [ ] Preserve merged floor physics and dynamic-cube registration behavior

### Task 4: Preserve world rebuild behavior

**Files:**
- Modify: runtime world rebuild caller path in `dream/Game.cs` or `dream/PlayScene.cs`

- [ ] Keep floor-size rebuild, cube migration, and player preservation behavior working through the new ownership boundaries

### Task 5: Verify and document

**Files:**
- Modify: `docs/architecture.md`
- Modify: `docs/project-structure.md`
- Create: `docs/ledger/work-log/2026-06-24-phase4-world-data-vs-physics-binding.md`

- [ ] Run `dotnet test dream.Tests\\dream.Tests.csproj --nologo`
- [ ] Run `dotnet build dream\\dream.csproj --nologo`
- [ ] Record world-rebuild verification notes honestly
