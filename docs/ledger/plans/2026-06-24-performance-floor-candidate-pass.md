# Performance Floor Candidate Pass Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reduce large-floor render candidate overhead by querying nearby floor tiles directly and using the spatial index only for placed cubes.

**Architecture:** Keep the floor as a special static world layer inside `Map`, add a dedicated render-candidate API that combines local floor tiles with nearby placed cubes, and route `PlayScene` world rendering through that API.

**Tech Stack:** C#/.NET 10, OpenTK, current world/render architecture, xUnit

---

### Task 1: Add failing render-candidate tests

**Files:**
- Create: `G:\workspace\projects\dream\dream.Tests\MapRenderCandidateTests.cs`

- [ ] Cover local floor inclusion
- [ ] Cover nearby placed-cube inclusion
- [ ] Cover far floor/placed exclusion
- [ ] Cover duplicate avoidance for floor tiles

### Task 2: Add a dedicated render-candidate path in `Map`

**Files:**
- Modify: `G:\workspace\projects\dream\dream\Map.cs`

- [ ] Keep floor tiles out of the placed-cube spatial index
- [ ] Query nearby floor tiles from direct X/Z bounds
- [ ] Combine local floor tiles with nearby placed cubes in a dedicated API

### Task 3: Route rendering through the new candidate API

**Files:**
- Modify: `G:\workspace\projects\dream\dream\PlayScene.cs`

- [ ] Use the new `Map` render-candidate API in the world render path
- [ ] Keep later visibility and batching passes unchanged

### Task 4: Update docs and ledger

**Files:**
- Modify: `G:\workspace\projects\dream\README.md`
- Modify: `G:\workspace\projects\dream\docs\architecture.md`
- Create: `G:\workspace\projects\dream\docs\ledger\work-log\2026-06-24-performance-floor-candidate-pass.md`

- [ ] Document the floor-vs-placed candidate split
- [ ] Record the scope honestly

### Task 5: Verify

**Files:**
- Test: `G:\workspace\projects\dream\dream.Tests\dream.Tests.csproj`
- Test: `G:\workspace\projects\dream\dream\dream.csproj`

- [ ] Run `dotnet test dream.Tests\\dream.Tests.csproj --filter MapRenderCandidateTests --nologo`
- [ ] Run `dotnet build dream\\dream.csproj --nologo`
- [ ] Run `dotnet test dream.Tests\\dream.Tests.csproj --nologo`
