# Performance First-Pass Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove the most obvious CPU and memory waste behind the large-floor slowdown without introducing a major renderer rewrite.

**Architecture:** The work will keep the current gameplay model but replace cubic eager world allocation with dimension-aware sparse storage, replace per-floor-tile static physics with one merged floor collider, render from direct solid-content lists, and reduce hot draw-loop CPU overhead by caching shader uniform locations.

**Tech Stack:** C#/.NET 8, OpenTK, BulletSharp, current game/render architecture

---

### Task 1: Reshape world storage around active content

**Files:**
- Modify: `dream/Map.cs`
- Optional Modify: `dream/Cube.cs`

- [ ] Replace `SideSize` with explicit dimensions such as `Width`, `Height`, and `Depth`
- [ ] Stop eagerly creating `Cube` instances for every empty cell
- [ ] Store world cells sparsely using nullable entries for empty space
- [ ] Add a direct `SolidCubes` collection for active renderable cubes
- [ ] Update `AddCube()` so it inserts only solid cubes and keeps `SolidCubes` synchronized

### Task 2: Replace per-tile static floor physics

**Files:**
- Modify: `dream/Map.cs`
- Modify: `dream/PhysicsIntegrator.cs`

- [ ] Add support for a merged static floor collider sized to the generated floor footprint
- [ ] Stop creating one rigid body per floor tile
- [ ] Keep dynamic cube body creation available for non-floor cubes
- [ ] Ensure disposal still cleans up the merged floor body correctly

### Task 3: Remove full-volume frame iteration

**Files:**
- Modify: `dream/Game.cs`
- Modify: `dream/PhysicsIntegrator.cs`
- Modify: `dream/Map.cs`

- [ ] Render from `Map.SolidCubes` instead of iterating the full 3D array
- [ ] Track dynamic map bodies separately from static bodies
- [ ] Update per-frame transform syncing to touch only dynamic map bodies

### Task 4: Cache uniform locations in the hot path

**Files:**
- Modify: `dream/Shader.cs`

- [ ] Add a uniform-location cache keyed by uniform name
- [ ] Route all uniform setters through the cache
- [ ] Preserve the existing `Shader` API

### Task 5: Update docs and ledger

**Files:**
- Modify: `README.md`
- Modify: `docs/architecture.md`
- Modify: `docs/project-structure.md`
- Modify: `docs/rendering-and-assets.md`
- Create: `docs/ledger/work-log/2026-06-24-performance-first-pass.md`

- [ ] Document the new world-storage and floor-physics model
- [ ] Record verification commands and observed results

### Task 6: Verify build, tests, and startup behavior

**Files:**
- Test: `dream/dream.csproj`
- Test: `dream.Tests/dream.Tests.csproj`

- [ ] Run `dotnet build dream\\dream.csproj --nologo`
- [ ] Run `dotnet test dream.Tests\\dream.Tests.csproj --nologo`
- [ ] Run a controlled startup of the built application
- [ ] Record verification limits honestly if no live FPS comparison is captured
