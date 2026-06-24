# Performance Model Batching Pass Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reduce repeated animated-model setup cost by batching visible cubes that share the same model and shader.

**Architecture:** Add a pure batch-planning helper for world cubes, expose begin/render/end phases on `AnimatedModel`, and route `WorldRenderer` through grouped batches while preserving the existing draw API for non-batched paths.

**Tech Stack:** C#/.NET 10, OpenTK, current model/render architecture, xUnit

---

### Task 1: Add failing batch-planner tests

**Files:**
- Create: `G:\workspace\projects\dream\dream.Tests\WorldRenderBatchPlannerTests.cs`

- [ ] Cover grouping by shared model and shader
- [ ] Cover separate shader groups
- [ ] Cover separate model instances

### Task 2: Add a pure batch planner

**Files:**
- Create: `G:\workspace\projects\dream\dream\WorldRenderBatchPlanner.cs`

- [ ] Group cubes by shared animation reference and shader name
- [ ] Keep the helper free of GL dependencies

### Task 3: Add animated-model batch hooks

**Files:**
- Modify: `G:\workspace\projects\dream\dream\AnimatedModel.cs`
- Modify: `G:\workspace\projects\dream\dream\Render.cs`
- Modify: `G:\workspace\projects\dream\dream\WorldRenderer.cs`

- [ ] Split animated-model rendering into shared setup plus per-instance render
- [ ] Add a render helper for animated batches in `Render`
- [ ] Route world rendering through the batch planner

### Task 4: Update docs and ledger

**Files:**
- Modify: `G:\workspace\projects\dream\README.md`
- Modify: `G:\workspace\projects\dream\docs\architecture.md`
- Create: `G:\workspace\projects\dream\docs\ledger\work-log\2026-06-24-performance-model-batching-pass.md`

- [ ] Document the shared-model batching stage
- [ ] Record the intentionally limited scope

### Task 5: Verify

**Files:**
- Test: `G:\workspace\projects\dream\dream.Tests\dream.Tests.csproj`
- Test: `G:\workspace\projects\dream\dream\dream.csproj`

- [ ] Run `dotnet test dream.Tests\\dream.Tests.csproj --filter WorldRenderBatchPlannerTests --nologo`
- [ ] Run `dotnet build dream\\dream.csproj --nologo`
- [ ] Run `dotnet test dream.Tests\\dream.Tests.csproj --nologo`
