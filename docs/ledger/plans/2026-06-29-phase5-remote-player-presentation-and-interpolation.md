# Phase 5 Remote Player Presentation And Interpolation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Render remote players smoothly from authoritative snapshots while keeping local camera, HUD, and first-person presentation systems local to the client.

**Architecture:** Introduce a client-side presenter seam and interpolation buffers for remote players, then expose a dedicated world-space remote-player renderer instead of mixing remote actors into the local first-person or HUD paths.

**Tech Stack:** C# / .NET 10, OpenTK client rendering, authoritative snapshot DTOs from earlier multiplayer phases

---

### Task 1: Lock the remote-player presentation boundary

**Files:**
- Create: `dream/ClientScenePresenter.cs`
- Create: `dream/RemotePlayerPresentationState.cs`
- Modify: `docs/architecture.md`
- Modify: `docs/project-structure.md`

- [ ] Define `ClientScenePresenter` as the owner of replicated remote-player presentation state
- [ ] Keep remote-player visual state separate from local camera and first-person systems
- [ ] Make `PlayScene` consume presenter output instead of hand-managing remote-player state
- [ ] Document that remote players are world-space actors, not first-person actors

### Task 2: Add interpolation primitives

**Files:**
- Create: `dream/RemotePlayerSnapshotSample.cs`
- Create: `dream/RemotePlayerInterpolationBuffer.cs`
- Create: `dream/InterpolatedActorPose.cs`
- Modify: `dream/ClientScenePresenter.cs`

- [ ] Buffer recent authoritative samples per remote actor
- [ ] Interpolate toward a render-time target rather than snapping to the newest sample immediately
- [ ] Keep interpolation strictly client-side and independent from server tick scheduling
- [ ] Add only the minimum pose information the renderer needs

### Task 3: Wire the presenter into the scene flow

**Files:**
- Modify: `dream/PlayScene.cs`
- Modify: `dream/Game.cs`
- Modify: `dream/ServerSnapshotEnvelope.cs` if the DTO needs one more field for render use

- [ ] Feed authoritative snapshots and deltas into `ClientScenePresenter`
- [ ] Keep the local player path unchanged for HUD, camera, and viewmodels
- [ ] Keep remote-player state flow out of local player update methods
- [ ] Preserve controlled local startup while integrating the presenter

### Task 4: Add a dedicated remote-player draw path

**Files:**
- Create: `dream/RemotePlayerRenderer.cs`
- Modify: `dream/Render.cs`
- Modify: `dream/WorldRenderer.cs`
- Modify: `dream/PlayScene.cs`

- [ ] Draw remote players in world space through a dedicated renderer collaborator
- [ ] Keep cube/world geometry rendering responsibilities clear in `WorldRenderer`
- [ ] Keep `HudRenderer` and `ViewModelRenderer` untouched by remote-player ownership
- [ ] Avoid assuming enemy-specific renderer files that do not yet exist in the current repo

### Task 5: Verify and document the remote presentation slice

**Files:**
- Create: `docs/ledger/work-log/2026-06-29-remote-player-presentation-and-interpolation.md`
- Modify: `README.md`
- Modify: `docs/architecture.md`

- [ ] Run `dotnet build dream\\dream.csproj --nologo`
- [ ] Run `dotnet test dream.Tests\\dream.Tests.csproj --nologo`
- [ ] Run a local smoke path where one or more fake or loopback-driven remote players are rendered from snapshot data
- [ ] Record whether render FPS and snapshot cadence mismatches still look smooth enough for the PoC

## Self-Review Notes

- Spec coverage: this phase covers the presenter seam, interpolation ownership, scene wiring, remote-player renderer, and verification
- Placeholder scan: the plan names concrete client-side files and boundaries instead of generic rendering aspirations
- Type consistency: `ClientScenePresenter`, `RemotePlayerPresentationState`, `RemotePlayerInterpolationBuffer`, and `RemotePlayerRenderer` stay stable throughout
