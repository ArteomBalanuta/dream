# Multiplayer Foundation Roadmap Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Refactor `dream` toward a dedicated authoritative server that can host one shared world session for many clients, while keeping the first multiplayer proof of concept practical.

**Architecture:** Extract a host-agnostic simulation runtime from the current local scene flow, introduce player-session and command boundaries, run that runtime on a 64 Hz server tick, and feed clients through explicit replication categories while leaving presentation-heavy systems client-local.

**Tech Stack:** C# / .NET 10, OpenTK client shell, BulletSharp physics, current scene event system, future transport edge chosen for fastest working PoC

---

## File Structure Direction

Before implementation starts, the codebase should converge toward these responsibility boundaries:

- `dream/Game.cs`
  - client desktop shell only
- `dream/PlayScene.cs`
  - client scene presentation and runtime wiring only
- `dream/WorldSessionRuntime.cs`
  - authoritative simulation root
- `dream/PlayerSessionRegistry.cs`
  - connected-player and runtime actor ownership
- `dream/PlayerCommandFrame.cs`
  - transport-safe player command payload
- `dream/ServerTickRunner.cs`
  - fixed 64 Hz simulation scheduling
- `dream/Replication/*.cs` or focused flat files if the repo keeps the current style
  - snapshots, deltas, and wire-neutral export shapes
- `dream/DedicatedServerHost.cs`
  - server process/runtime host

This plan does not require all of those files to appear in the first task, but it defines the intended decomposition so future steps do not drift.

### Task 1: Extract a headless-ready simulation root

**Files:**
- Modify: `dream/PlayScene.cs`
- Modify: `dream/Game.cs`
- Create: `dream/WorldSessionRuntime.cs`
- Modify: `docs/architecture.md`

- [ ] Document the exact responsibilities that move from `PlayScene` into `WorldSessionRuntime`
- [ ] Move authoritative world, physics, and actor update ownership behind the new runtime boundary
- [ ] Keep `PlayScene` responsible for client presentation orchestration instead of simulation truth
- [ ] Verify the desktop client still builds and still runs the local runtime through the new seam

### Task 2: Add explicit player session ownership

**Files:**
- Create: `dream/PlayerSessionId.cs`
- Create: `dream/ConnectedPlayer.cs`
- Create: `dream/PlayerSessionRegistry.cs`
- Modify: `dream/WorldSessionRuntime.cs`
- Modify: `dream/Player.cs`

- [ ] Replace the implicit single-player assumption with explicit connected-player identity
- [ ] Define how a connected player maps to an authoritative runtime actor
- [ ] Preserve the existing local player path as the first client host consumer of the new session model
- [ ] Avoid leaking desktop-only input or camera types into session ownership contracts

### Task 3: Introduce transport-safe command frames

**Files:**
- Create: `dream/PlayerCommandFrame.cs`
- Create: `dream/PlayerLookIntent.cs`
- Create: `dream/PlayerMoveIntent.cs`
- Modify: `dream/PlayerController.cs`
- Modify: `dream/WorldSessionRuntime.cs`

- [ ] Define the per-tick command payloads needed for movement, look, primary use, and interaction mode
- [ ] Convert local desktop input into command frames on the client edge
- [ ] Update authoritative simulation to consume command frames instead of raw keyboard/mouse objects
- [ ] Add sequencing fields so later reconciliation can track what the server has accepted

### Task 4: Establish the 64 Hz server tick contract

**Files:**
- Create: `dream/SimulationClock.cs`
- Create: `dream/ServerTickRunner.cs`
- Modify: `dream/WorldSessionRuntime.cs`
- Modify: `dream/PhysicsIntegrator.cs`

- [ ] Define a fixed server tick schedule at 64 Hz
- [ ] Move simulation ordering into a stable tick contract
- [ ] Ensure physics stepping and world/combat resolution use server tick cadence rather than client frame cadence
- [ ] Keep client render cadence separate from authoritative tick cadence

### Task 5: Add replication export models

**Files:**
- Create: `dream/ActorSnapshot.cs`
- Create: `dream/WorldSnapshot.cs`
- Create: `dream/WorldMutationDelta.cs`
- Create: `dream/ServerSnapshotEnvelope.cs`
- Modify: `dream/WorldSessionRuntime.cs`

- [ ] Define wire-neutral replication shapes for actor state, world state, and mutation events
- [ ] Export authoritative player, enemy, and world-object state from the simulation runtime
- [ ] Include server tick metadata and player identity mapping
- [ ] Keep replication DTOs separate from renderer-only or Bullet-only implementation details

### Task 6: Add the first dedicated server host path

**Files:**
- Create: `dream/DedicatedServerHost.cs`
- Create: `dream/ServerBootstrap.cs`
- Modify: `dream/Program.cs`
- Modify: `dream/dream.csproj`

- [ ] Add a headless startup path for the authoritative runtime
- [ ] Keep desktop and server host entry paths clearly separated
- [ ] Choose the easiest working transport edge for the PoC without locking transport details into gameplay code
- [ ] Make startup and shutdown behavior explicit and testable

### Task 7: Build remote-player presentation support

**Files:**
- Create: `dream/RemotePlayerPresentationState.cs`
- Create: `dream/ClientScenePresenter.cs`
- Modify: `dream/PlayScene.cs`
- Modify: `dream/WorldRenderer.cs`
- Modify: `dream/EnemyRenderer.cs` if shared actor draw paths are reused

- [ ] Add client-side state for rendering remote players in the shared world
- [ ] Distinguish local player first-person presentation from remote player world-space presentation
- [ ] Keep HUD and viewmodel logic local to the client player only
- [ ] Ensure remote actors can be rendered from authoritative or interpolated snapshot data

### Task 8: Route build and combat through authority

**Files:**
- Modify: `dream/PlayerInteraction.cs`
- Modify: `dream/Map.cs`
- Modify: `dream/PlayerCombatant.cs`
- Modify: `dream/CombatUseResolver.cs`
- Modify: `dream/WorldSessionRuntime.cs`

- [ ] Move building and combat acceptance fully behind server validation
- [ ] Publish authoritative world mutations and damage outcomes
- [ ] Reject or correct invalid client assumptions cleanly
- [ ] Keep the authoritative mutation order explicit and observable

### Task 9: Add client correction and multiplayer diagnostics

**Files:**
- Create: `dream/ClientPredictionState.cs`
- Create: `dream/ClientCorrectionApplier.cs`
- Modify: `dream/EventTraceSystem.cs`
- Modify: `dream/PlayScene.cs`
- Modify: `docs/architecture.md`

- [ ] Add the first correction path for movement and authoritative state reconciliation
- [ ] Add tick, command, and snapshot diagnostics to help debug divergence
- [ ] Keep prediction narrow and practical for the PoC
- [ ] Document the rules for what is corrected versus what stays purely cosmetic

### Task 10: Record the multiplayer foundation in docs and ledger

**Files:**
- Modify: `README.md`
- Modify: `docs/architecture.md`
- Modify: `docs/project-structure.md`
- Create: `docs/ledger/work-log/2026-06-29-multiplayer-foundation-execution.md`

- [ ] Update high-level docs once the first multiplayer foundation slices land
- [ ] Record which systems became server-owned, client-owned, or hybrid
- [ ] Document verification commands and any remaining multiplayer limitations honestly
- [ ] Keep the roadmap and the resulting implementation history aligned

## Dependency Notes

- Task 1 unblocks all later work
- Task 2 and Task 3 should land before transport-specific implementation
- Task 4 should land before any serious replication testing
- Task 5 and Task 6 are the minimum path to the first shared-world PoC
- Task 7 through Task 9 should build on a working authoritative core rather than precede it

## Risks To Watch

- allowing `PlayScene` to remain both the client presenter and the authoritative world owner
- serializing current engine types directly instead of defining clean replication DTOs
- leaving camera/viewmodel assumptions inside authoritative player code
- trying to solve multi-room hosting before the single-session PoC works
- overbuilding the transport before the simulation boundary is healthy

## Verification Strategy

For implementation slices created from this roadmap, verify with:

- `dotnet build dream\\dream.csproj`
- `dotnet test dream.Tests\\dream.Tests.csproj`
- controlled desktop startup for the client host path
- controlled headless startup for the server host path once introduced
- focused multiplayer smoke tests that confirm two or more clients can observe the same authoritative world session

## Self-Review Notes

- Spec coverage: the plan covers simulation extraction, session identity, commands, server tick, replication, server host, remote presentation, authoritative interaction, correction, and docs
- Placeholder scan: all tasks name concrete file directions and explicit ownership goals instead of generic future work labels
- Type consistency: the plan uses one stable set of core seams such as `WorldSessionRuntime`, `PlayerSessionRegistry`, `PlayerCommandFrame`, and `ServerTickRunner`
