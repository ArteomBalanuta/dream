# Phase 7 Input Commands And Events Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Convert major gameplay actions into clearer commands and introduce targeted event-style decoupling for runtime changes that currently mutate state inline.

**Architecture:** This phase keeps the project lightweight and avoids a large event bus. Instead, it adds focused commands for key gameplay actions and small typed event flows where state changes currently cross subsystem boundaries awkwardly.

**Tech Stack:** C#/.NET 8, OpenTK, current gameplay architecture

---

### Task 1: Identify major gameplay commands

**Files:**
- Modify: `dream/Game.cs` or `dream/PlayScene.cs`
- Modify: player-related files after Phase 3

- [ ] Identify the top-value actions to represent as commands, such as jump, place cube, toggle freeze, toggle settings, and toggle collision debug
- [ ] Avoid converting every single input path at once

### Task 2: Introduce a lightweight command model

**Files:**
- Create: `dream/GameCommands.cs`
- Modify: gameplay orchestration and player-control files

- [ ] Add a lightweight command representation suitable for current project scale
- [ ] Route selected gameplay actions through commands instead of direct inline mutations

### Task 3: Introduce targeted events for runtime change points

**Files:**
- Create: `dream/GameEvents.cs`
- Modify: `dream/GameSettings.cs`
- Modify: world rebuild or debug-toggle call paths

- [ ] Add small typed events or callbacks for high-value change points such as floor-size apply or debug-visibility changes
- [ ] Avoid a generic global bus

### Task 4: Keep input and gameplay responsibilities separated

**Files:**
- Modify: input/control files produced by earlier phases

- [ ] Ensure input capture creates commands
- [ ] Ensure gameplay systems consume commands
- [ ] Prevent UI state and gameplay state from becoming more tangled during the conversion

### Task 5: Verify and document

**Files:**
- Modify: `docs/architecture.md`
- Create: `docs/ledger/work-log/2026-06-24-phase7-input-commands-and-events.md`

- [ ] Run `dotnet test dream.Tests\\dream.Tests.csproj --nologo`
- [ ] Run `dotnet build dream\\dream.csproj --nologo`
- [ ] Record verification notes honestly
