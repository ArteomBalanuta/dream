# Phase 0 Multiplayer Preflight And Runtime Audit Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stabilize the runtime seams and low-harness test surface that later multiplayer phases depend on, so the first simulation extraction and session refactors land against a safer foundation.

**Architecture:** This phase does not add networking. It strengthens the current single-player runtime where multiplayer will cut deepest: startup assumptions, player command/motor seams, scene-owned simulation construction, and the one-player assumptions inside `PhysicsIntegrator`.

**Tech Stack:** C# / .NET 10, OpenTK client shell, BulletSharp, existing player/world/runtime decomposition

---

## File Structure Direction

This phase should prepare the codebase for later multiplayer work by making the following future ownership split easier:

- `dream/Program.cs`
  - mode selection shell, not desktop-only forever
- `dream/PlayScene.cs`
  - client scene composition, not permanent simulation truth owner
- `dream/WorldSessionRuntime.cs`
  - future authoritative runtime root
- `dream/PlayerController.cs`
  - client input-to-command capture
- `dream/PlayerMotor.cs`
  - authoritative movement rule application
- `dream/PlayerInteraction.cs`
  - local intent capture, not permanent world mutation owner
- `dream/PhysicsIntegrator.cs`
  - authoritative physics subsystem, not one implicit local player constructor

### Task 1: Add focused low-harness tests around player seams

**Files:**
- Create: `dream.Tests/PlayerControllerTests.cs`
- Create: `dream.Tests/PlayerInteractionTests.cs`
- Optional Create: `dream.Tests/PlayerMotorTests.cs`
- Modify: `dream.Tests/dream.Tests.csproj` only if discovery needs it

- [ ] Add focused tests for movement intent capture, interaction edge capture, and any player motor behavior that can be validated without a high-cost window harness
- [ ] Prefer testing command/intent translation and state transitions over fake-heavy OpenTK lifecycle tests
- [ ] Cover the current local primary-use edge behavior so later command-frame refactors have a regression net
- [ ] Keep tests small and stable enough to survive runtime extraction

### Task 2: Inventory and mark the `PlayScene` to `WorldSessionRuntime` seam explicitly

**Files:**
- Modify: `dream/PlayScene.cs`
- Modify: `docs/architecture.md`
- Modify: `docs/project-structure.md`

- [ ] Make the exact responsibilities that will leave `PlayScene` more explicit in code comments or architecture docs where that improves refactor safety
- [ ] Keep `PlayScene` from gaining new multiplayer responsibilities during preflight
- [ ] Identify authoritative state access points that later `WorldSessionRuntime` will need to own
- [ ] Preserve current desktop behavior unchanged

### Task 3: Reduce one-player assumptions at the physics boundary

**Files:**
- Modify: `dream/PhysicsIntegrator.cs`
- Modify: `dream/Player.cs`
- Modify: `dream/PhysicsFactory.cs`
- Optional Create: `dream.Tests/PhysicsIntegratorOwnershipTests.cs`

- [ ] Identify and isolate the constructor-owned `PlayerBody` assumption so later session-owned player creation is possible
- [ ] Keep current runtime behavior intact while reducing hidden one-player coupling
- [ ] Avoid pulling session or transport concepts into this preflight phase
- [ ] Preserve disposal and cleanup behavior honestly

### Task 4: Narrow `PlayerInteraction` toward intent ownership

**Files:**
- Modify: `dream/PlayerInteraction.cs`
- Modify: `dream/Player.cs`
- Modify: `dream/PlayScene.cs`

- [ ] Keep the current build interaction working, but move the code toward "capture and express intent" instead of "permanent authoritative mutation owner"
- [ ] Reduce direct world mutation assumptions where that can be done without premature networking work
- [ ] Keep room for later `BuildUseIntent` and `CombatUseIntent` types
- [ ] Preserve current gameplay feel for local single-player use

### Task 5: Prepare startup mode for later host branching

**Files:**
- Modify: `dream/Program.cs`
- Modify: `docs/architecture.md`

- [ ] Make `Program` easier to extend with later desktop-versus-server branching without fully implementing the server host yet
- [ ] Keep the current default desktop path intact
- [ ] Avoid starting transport or server bootstrap work in this phase
- [ ] Record the exact next handoff into Phase 1 and Phase 4 plans

### Task 6: Verify and document the preflight phase

**Files:**
- Create: `docs/ledger/work-log/2026-06-29-phase0-multiplayer-preflight-and-runtime-audit.md`
- Modify: `README.md`
- Modify: `docs/architecture.md`
- Modify: `docs/project-structure.md`

- [ ] Run `dotnet build dream\\dream.csproj --nologo`
- [ ] Run `dotnet test dream.Tests\\dream.Tests.csproj --nologo`
- [ ] Run a controlled desktop startup and record what behavior was rechecked after the seam-hardening work
- [ ] Record any remaining one-player assumptions honestly before Phase 1 begins

## Dependency Notes

- This phase exists to reduce risk for:
  - `docs/ledger/plans/2026-06-29-phase1-world-session-runtime-extraction.md`
  - `docs/ledger/plans/2026-06-29-phase2-player-session-and-command-frames.md`
  - `docs/ledger/plans/2026-06-29-phase4-protocol-contract-and-routing.md`
  - `docs/ledger/plans/2026-06-29-phase4-dedicated-server-host-and-transport-poc.md`

- If this phase reveals that some planned seams are still too entangled, adjust the later plans before coding the server path.

## Risks To Watch

- adding partial networking concepts too early
- turning preflight into a broad rewrite
- leaving `PlayScene` as the owner of new multiplayer-specific logic during the audit
- weakening current single-player behavior while trying to prepare future seams

## Self-Review Notes

- Spec coverage: this phase covers tests, runtime seam inventory, physics ownership assumptions, interaction ownership direction, startup preparation, and verification
- Placeholder scan: each task names concrete files and bounded preflight outcomes
- Type consistency: the phase consistently prepares `WorldSessionRuntime`, command-intent work, and later host branching without implementing them prematurely
