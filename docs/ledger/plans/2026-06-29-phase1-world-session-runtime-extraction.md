# Phase 1 World Session Runtime Extraction Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extract a host-agnostic authoritative simulation root from the current `PlayScene` flow so the same runtime can later back both a desktop client and a dedicated server.

**Architecture:** `PlayScene` should stop owning simulation truth directly. Instead, it should compose a new `WorldSessionRuntime` that owns world, physics, player actors, and authoritative update order, while the scene remains responsible for client presentation and host wiring.

**Tech Stack:** C# / .NET 10, OpenTK client shell, BulletSharp, current world/player/render split

---

### Task 1: Define the `WorldSessionRuntime` seam

**Files:**
- Create: `dream/WorldSessionRuntime.cs`
- Modify: `dream/PlayScene.cs`
- Modify: `docs/architecture.md`

- [ ] Write down the specific responsibilities leaving `PlayScene` and entering `WorldSessionRuntime`
- [ ] Keep `PlayScene` as a composition root for the client path
- [ ] Ensure the new runtime owns authoritative update order rather than just wrapping existing scene code
- [ ] Avoid moving render, HUD, or viewmodel ownership into the runtime

### Task 2: Move world and physics ownership into the runtime

**Files:**
- Modify: `dream/WorldSessionRuntime.cs`
- Modify: `dream/Map.cs`
- Modify: `dream/PhysicsIntegrator.cs`
- Modify: `dream/PlayScene.cs`

- [ ] Move authoritative creation and lifetime ownership of `Map` and `PhysicsIntegrator` behind the runtime
- [ ] Keep current floor generation, floor rebuild, and map-body reset semantics intact
- [ ] Keep current player-body setup behavior intact while changing ownership boundaries
- [ ] Preserve disposal rules cleanly

### Task 3: Move authoritative player update orchestration into the runtime

**Files:**
- Modify: `dream/WorldSessionRuntime.cs`
- Modify: `dream/Player.cs`
- Modify: `dream/PlayScene.cs`

- [ ] Move movement, interaction, combat, and physics-step ordering into the runtime
- [ ] Leave local desktop input capture outside the runtime for now, but pass only the minimum intent the runtime needs
- [ ] Keep current single-player behavior stable through the seam
- [ ] Avoid forcing remote-player support into this phase

### Task 4: Keep the scene as a presentation consumer

**Files:**
- Modify: `dream/PlayScene.cs`
- Modify: `dream/Render.cs`
- Modify: `dream/WorldRenderer.cs`
- Modify: `dream/HudRenderer.cs`

- [ ] Make the scene read authoritative state from the runtime instead of owning that state directly
- [ ] Keep HUD, debug rendering, and viewmodel behavior client-local
- [ ] Keep current render order unchanged
- [ ] Ensure the client can still perform controlled local startup after the extraction

### Task 5: Verify and document

**Files:**
- Modify: `README.md`
- Modify: `docs/architecture.md`
- Modify: `docs/project-structure.md`
- Create: `docs/ledger/work-log/2026-06-29-phase1-world-session-runtime-extraction.md`

- [ ] Run `dotnet build dream\\dream.csproj --nologo`
- [ ] Run `dotnet test dream.Tests\\dream.Tests.csproj --nologo`
- [ ] Run a controlled desktop startup and record what was verified
- [ ] Record any limitations honestly if the runtime is not yet server-capable after this phase

## Self-Review Notes

- Spec coverage: this phase covers the required simulation root extraction and the presentation/runtime split
- Placeholder scan: each task names concrete ownership moves and verification expectations
- Type consistency: `WorldSessionRuntime` is the single extracted runtime seam used throughout the phase
