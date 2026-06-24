# Performance Visibility Pass Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reduce large-floor rendering cost by skipping obviously non-visible cubes before they reach the hot draw path.

**Architecture:** Add a small world-visibility helper that applies cheap render-distance and forward-visibility filtering, then route `WorldRenderer` through it while keeping the rest of the world/render architecture unchanged.

**Tech Stack:** C#/.NET 10, OpenTK, current world/render architecture, xUnit

---

### Task 1: Add focused visibility tests

**Files:**
- Create: `G:\workspace\projects\dream\dream.Tests\WorldVisibilityTests.cs`

- [ ] Cover cubes in front of the camera within range
- [ ] Cover cubes outside the visibility distance
- [ ] Cover cubes clearly behind the camera
- [ ] Cover very near cubes so they remain visible even if slightly behind the camera

### Task 2: Add a world-visibility helper

**Files:**
- Create: `G:\workspace\projects\dream\dream\WorldVisibility.cs`

- [ ] Add a small helper for cheap cube visibility decisions
- [ ] Keep the first pass heuristic and simple
- [ ] Avoid introducing rendering-API or GL dependencies into the helper

### Task 3: Route world drawing through the filter

**Files:**
- Modify: `G:\workspace\projects\dream\dream\WorldRenderer.cs`

- [ ] Filter cube draws through `WorldVisibility`
- [ ] Keep public scene/render entry points unchanged
- [ ] Avoid touching debug rendering in this pass

### Task 4: Update docs and ledger

**Files:**
- Modify: `G:\workspace\projects\dream\README.md`
- Modify: `G:\workspace\projects\dream\docs\architecture.md`
- Create: `G:\workspace\projects\dream\docs\ledger\work-log\2026-06-24-performance-visibility-pass.md`

- [ ] Document that large-floor rendering now uses a lightweight visibility gate
- [ ] Record the intentionally limited scope of the pass

### Task 5: Verify

**Files:**
- Test: `G:\workspace\projects\dream\dream.Tests\dream.Tests.csproj`
- Test: `G:\workspace\projects\dream\dream\dream.csproj`

- [ ] Run `dotnet test dream.Tests\\dream.Tests.csproj --filter WorldVisibilityTests --nologo`
- [ ] Run `dotnet build dream\\dream.csproj --nologo`
- [ ] Run `dotnet test dream.Tests\\dream.Tests.csproj --nologo`
- [ ] Record any remaining warnings honestly
