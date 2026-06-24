# Performance Spatial Query Pass Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reduce large-floor candidate iteration cost by querying only nearby world sectors before visibility filtering and draw submission.

**Architecture:** Add a lightweight sector-based spatial index owned by `Map`, expose a nearby-solid-cube query, and route `PlayScene` world rendering through that query before the existing world-visibility gate runs.

**Tech Stack:** C#/.NET 10, OpenTK, current world/render architecture, xUnit

---

### Task 1: Add failing spatial-query tests

**Files:**
- Create: `G:\workspace\projects\dream\dream.Tests\MapSpatialQueryTests.cs`

- [ ] Cover local floor-sector queries
- [ ] Cover added-cube inclusion
- [ ] Cover multi-sector radius behavior

### Task 2: Add a map-side spatial index

**Files:**
- Create: `G:\workspace\projects\dream\dream\WorldSpatialIndex.cs`
- Modify: `G:\workspace\projects\dream\dream\Map.cs`

- [ ] Index cubes by coarse X/Z sectors
- [ ] Keep the index synchronized when cubes are added
- [ ] Expose a nearby-solid-cube query from `Map`

### Task 3: Route rendering through the nearby query

**Files:**
- Modify: `G:\workspace\projects\dream\dream\PlayScene.cs`
- Modify: `G:\workspace\projects\dream\dream\WorldVisibility.cs`

- [ ] Reuse the render-distance constant from `WorldVisibility`
- [ ] Feed `WorldRenderer` only nearby candidates
- [ ] Keep the existing visibility gate in place on top of the reduced candidate set

### Task 4: Update docs and ledger

**Files:**
- Modify: `G:\workspace\projects\dream\README.md`
- Modify: `G:\workspace\projects\dream\docs\architecture.md`
- Create: `G:\workspace\projects\dream\docs\ledger\work-log\2026-06-24-performance-spatial-query-pass.md`

- [ ] Document the new sector-query stage in the world render path
- [ ] Record the limited scope honestly

### Task 5: Verify

**Files:**
- Test: `G:\workspace\projects\dream\dream.Tests\dream.Tests.csproj`
- Test: `G:\workspace\projects\dream\dream\dream.csproj`

- [ ] Run `dotnet test dream.Tests\\dream.Tests.csproj --filter MapSpatialQueryTests --nologo`
- [ ] Run `dotnet build dream\\dream.csproj --nologo`
- [ ] Run `dotnet test dream.Tests\\dream.Tests.csproj --nologo`
