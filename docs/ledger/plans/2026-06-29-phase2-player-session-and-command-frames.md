# Phase 2 Player Session And Command Frames Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the implicit single-local-player input model with explicit player session identity and transport-safe command frames that the authoritative runtime can consume.

**Architecture:** Keep desktop input capture on the client side, but convert it into command DTOs and route those into the simulation runtime. Add narrow session ownership types so many clients can later map cleanly onto many authoritative player actors.

**Tech Stack:** C# / .NET 10, OpenTK client input capture, current player subsystem split, future dedicated server runtime

---

### Task 1: Add player session identity types

**Files:**
- Create: `dream/PlayerSessionId.cs`
- Create: `dream/ConnectedPlayer.cs`
- Create: `dream/PlayerSessionRegistry.cs`
- Modify: `dream/WorldSessionRuntime.cs`

- [ ] Define a narrow session identity shape suitable for one connected client controlling one authoritative player actor
- [ ] Track active sessions, spawn state, and actor ownership in `PlayerSessionRegistry`
- [ ] Preserve the current local player path by giving it a first local session identity
- [ ] Avoid turning the registry into a persistence or profile system

### Task 2: Define command DTOs

**Files:**
- Create: `dream/PlayerCommandFrame.cs`
- Create: `dream/PlayerMoveIntent.cs`
- Create: `dream/PlayerLookIntent.cs`
- Create: `dream/PlayerModeSwitchIntent.cs`
- Modify: `docs/architecture.md`

- [ ] Define command DTOs that are serializable and independent of OpenTK runtime objects
- [ ] Include sequence numbering so later reconciliation can work
- [ ] Cover only the current gameplay loop: movement, look, primary use, and mode selection
- [ ] Keep debug-only or nonessential controls out of the first contract unless they are truly required

### Task 3: Convert local input capture into command frames

**Files:**
- Modify: `dream/PlayerController.cs`
- Modify: `dream/PlayScene.cs`
- Modify: `dream/WorldSessionRuntime.cs`

- [ ] Keep OpenTK keyboard and mouse sampling at the client edge
- [ ] Build one command frame per local input sample or per runtime update as appropriate
- [ ] Feed that command frame into the runtime rather than letting `Player` read desktop input types directly
- [ ] Preserve current single-player playability through the new path

### Task 4: Make the runtime consume command intent

**Files:**
- Modify: `dream/Player.cs`
- Modify: `dream/PlayerMotor.cs`
- Modify: `dream/PlayerInteraction.cs`
- Modify: `dream/WorldSessionRuntime.cs`

- [ ] Update movement and interaction flow so authoritative behavior derives from command intent instead of raw OpenTK types
- [ ] Keep camera-specific behavior out of authoritative runtime consumption
- [ ] Ensure edge-triggered use behavior still works correctly through command-frame sequencing
- [ ] Keep room for later remote sessions without implementing transport yet

### Task 5: Verify and document

**Files:**
- Modify: `docs/project-structure.md`
- Modify: `docs/architecture.md`
- Create: `docs/ledger/work-log/2026-06-29-phase2-player-session-and-command-frames.md`

- [ ] Run `dotnet build dream\\dream.csproj --nologo`
- [ ] Run `dotnet test dream.Tests\\dream.Tests.csproj --nologo`
- [ ] Run a controlled desktop startup and verify the local player still moves and interacts through command frames
- [ ] Record verification notes honestly

## Self-Review Notes

- Spec coverage: this phase covers session identity, command frames, local input translation, and runtime command consumption
- Placeholder scan: the plan stays specific about types, files, and behavioral goals
- Type consistency: `PlayerSessionRegistry` and `PlayerCommandFrame` stay the core seams throughout
