# Phase 6 Renderer Layering Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Split the current renderer into clearer world, HUD, and debug layers so visual systems can evolve without one monolithic `Render.cs`.

**Architecture:** The refactor will preserve current rendering behavior while decomposing `Render` into purpose-driven renderer units. Shared math/utilities such as `Shader`, `HudLayout`, and `CubeTransforms` remain as support types.

**Tech Stack:** C#/.NET 8, OpenTK, ImGui.NET

---

### Task 1: Extract world rendering

**Files:**
- Create: `dream/WorldRenderer.cs`
- Modify: `dream/Render.cs`

- [ ] Move cube/world rendering into a world-focused renderer
- [ ] Preserve current model and fallback cube rendering behavior

### Task 2: Extract HUD/settings UI rendering

**Files:**
- Create: `dream/HudRenderer.cs`
- Modify: `dream/Render.cs`

- [ ] Move crosshair, HUD, and settings-window behavior into a HUD-focused renderer
- [ ] Preserve current HUD result flow for runtime settings actions

### Task 3: Extract debug rendering

**Files:**
- Create: `dream/DebugRenderer.cs`
- Modify: `dream/Render.cs`

- [ ] Move collision debug drawing into a dedicated debug renderer
- [ ] Keep Bullet-driven collision overlay behavior identical

### Task 4: Reduce `Render.cs` to composition or remove it

**Files:**
- Modify: `dream/Render.cs`
- Modify: callers in `dream/Game.cs` or `dream/PlayScene.cs`

- [ ] Decide whether `Render` remains as a composition root or is replaced entirely by layered renderer ownership
- [ ] Keep startup/setup and disposal behavior coherent

### Task 5: Verify and document

**Files:**
- Modify: `docs/rendering-and-assets.md`
- Modify: `docs/project-structure.md`
- Create: `docs/ledger/work-log/2026-06-24-phase6-renderer-layering.md`

- [ ] Run `dotnet test dream.Tests\\dream.Tests.csproj --nologo`
- [ ] Run `dotnet build dream\\dream.csproj --nologo`
- [ ] Run a controlled startup and record runtime verification limits honestly
