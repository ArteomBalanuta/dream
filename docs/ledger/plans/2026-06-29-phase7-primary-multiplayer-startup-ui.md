# Phase 7 Primary Multiplayer Startup UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make multiplayer connection the primary desktop boot flow through a dedicated startup scene and validated host/connect UI.

**Architecture:** Add a small startup connection scene plus a menu-only ImGui renderer instead of stretching `PlayScene` into a pre-game menu. Keep gameplay scene construction deferred until a successful network connection is available.

**Tech Stack:** C# / .NET 10, OpenTK, ImGui.NET, existing desktop shell and remote client transport seams

---

## File Structure Direction

- `dream/Game.cs`
  - top-level scene switching between startup and gameplay
- `dream/StartupConnectionScene.cs`
  - startup scene update/render flow
- `dream/StartupMenuRenderer.cs`
  - startup ImGui renderer and UI request creation
- `dream/ClientConnectionOptions.cs`
  - validated host/port/player-name input model
- `dream/ConnectionUiRequest.cs`
  - small UI action result contract

### Task 1: Add validated connection option model

**Files:**
- Create: `dream/ClientConnectionOptions.cs`
- Create: `dream.Tests/ClientConnectionOptionsTests.cs`

- [ ] validate host, port, and player name
- [ ] support conversion into `RemoteClientOptions`
- [ ] keep validation independent from UI widgets and sockets

### Task 2: Add startup scene UI renderer

**Files:**
- Create: `dream/StartupMenuRenderer.cs`
- Create: `dream/ConnectionUiRequest.cs`

- [ ] create a small ImGui-only renderer for the startup screen
- [ ] expose host, port, player-name editing plus connect/local-server actions
- [ ] show visible status and error text

### Task 3: Add startup connection scene

**Files:**
- Create: `dream/StartupConnectionScene.cs`
- Modify: `dream/Game.cs`

- [ ] add startup-scene update and render flow
- [ ] keep cursor visible while startup scene is active
- [ ] switch into `PlayScene` only after a successful connection handoff

## Self-Review Notes

- Spec coverage: covers boot UI, host/port editing, visible status, and gameplay handoff
- Placeholder scan: tasks name concrete files and bounded responsibilities
- Type consistency: `ClientConnectionOptions`, `ConnectionUiRequest`, and `StartupConnectionScene` stay stable throughout
