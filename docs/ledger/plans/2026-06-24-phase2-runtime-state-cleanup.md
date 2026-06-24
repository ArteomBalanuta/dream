# Phase 2 Runtime State Cleanup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace scattered runtime booleans and implicit state rules with clearer gameplay/UI state handling while preserving current behavior.

**Architecture:** The refactor will keep the current settings and player systems but introduce explicit state grouping for gameplay-vs-settings flow, player frozen mode, and debug-visibility behavior. The focus is clarity of control flow, not feature expansion.

**Tech Stack:** C#/.NET 8, OpenTK, BulletSharp, ImGui.NET

---

### Task 1: Inventory current implicit states

**Files:**
- Modify: `dream/Game.cs`
- Modify: `dream/GameSettings.cs`
- Modify: `dream/Player.cs`

- [ ] Identify the currently implicit runtime states such as settings open, gameplay input enabled, player frozen, and collision debug visibility
- [ ] Group state ownership so UI-oriented state lives outside movement logic where possible
- [ ] Remove duplicated boolean interpretation where the same condition is recomputed in multiple places

### Task 2: Introduce explicit gameplay/UI state boundaries

**Files:**
- Create: `dream/RuntimeState.cs` or `dream/GameplayState.cs`
- Modify: `dream/Game.cs`
- Modify: `dream/GameSettings.cs`

- [ ] Add an explicit type or grouped state model for top-level runtime mode decisions
- [ ] Route `Esc`-driven settings transitions through that model
- [ ] Keep cursor and input-capture behavior consistent with the current game

### Task 3: Isolate player local state handling

**Files:**
- Modify: `dream/Player.cs`

- [ ] Make player freeze behavior read as a coherent local state transition instead of inline branching across `Update()`
- [ ] Keep movement, jump, and camera behavior identical when not frozen
- [ ] Preserve current freeze toggle semantics

### Task 4: Align debug and settings state flow

**Files:**
- Modify: `dream/GameSettings.cs`
- Modify: `dream/Render.cs`
- Modify: `dream/Game.cs` or `dream/PlayScene.cs` after Phase 1

- [ ] Route collision-debug visibility and other UI-owned runtime toggles through the same explicit state boundary
- [ ] Remove scattered “if settings visible then no gameplay input” style checks where a clearer state check can replace them

### Task 5: Verify and document

**Files:**
- Modify: `docs/architecture.md`
- Create: `docs/ledger/work-log/2026-06-24-phase2-runtime-state-cleanup.md`

- [ ] Run `dotnet test dream.Tests\\dream.Tests.csproj --nologo`
- [ ] Run `dotnet build dream\\dream.csproj --nologo`
- [ ] Record behavior-preservation notes honestly
