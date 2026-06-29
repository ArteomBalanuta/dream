# Multiplayer Foundation Prerequisites

Purpose: identify the concrete runtime and testing blockers that should be addressed before the dedicated-server, protocol, and transport plans begin implementation.
Status: complete
Scope: startup mode assumptions, simulation ownership assumptions, player/runtime coupling, physics ownership, and low-harness test gaps that affect early multiplayer refactors

## Goal

Define the real "do this first" groundwork for multiplayer so implementation starts from stable seams instead of from the most visible networking features.

## Summary

The ledger now has strong multiplayer architecture and protocol planning, but the current codebase still has several pre-networking blockers:

- `Program` only boots the desktop client path
- `PlayScene` still constructs and owns most of the local gameplay stack directly
- `PhysicsIntegrator` still creates one `PlayerBody` in its constructor
- `Player.Update()` still mixes authoritative movement and client-local camera assumptions
- the test suite has almost no direct coverage over player command/motor/interaction seams

These are the highest-value first steps because they reduce risk for every later multiplayer phase.

## Key Preconditions

### 1. Startup mode must stop being single-path

`Program.Main()` still always creates `Game` and runs the desktop window shell.

That means:

- there is no clean place to introduce server mode later
- headless execution is still a foreign shape to the runtime

This does not mean Phase 4 server bootstrap must be implemented immediately. It means the roadmap should treat "startup mode is single-path" as a known prerequisite constraint for later host work.

## 2. Simulation construction is still scene-owned

`PlayScene` currently constructs:

- `PhysicsIntegrator`
- `Map`
- `Player`
- `Render`
- `CombatFeedbackSystem`

This is the main ownership knot.

Until this is reduced, later transport or protocol work would only add more responsibilities to the same scene root.

## 3. `PhysicsIntegrator` still assumes one local player

The constructor still creates one `PlayerBody` immediately.

That is a multiplayer blocker because:

- session-owned players cannot be introduced cleanly
- authoritative runtime ownership stays tied to a single implicit actor
- server host work would inherit a one-player assumption at the physics boundary

This is one of the strongest reasons the runtime extraction phase should come before transport.

## 4. Player movement and camera assumptions still ride together

The current player stack is improved, but not yet multiplayer-ready:

- `PlayerController` uses `Camera.Front` and `Camera.Right` to create movement direction
- `Player.Update()` still coordinates camera sync, movement, and local input ownership
- `PlayerInteraction` still directly places cubes into `Map`

These are good seams, but they still need a cleaner division between:

- command capture
- authoritative simulation
- client presentation

## 5. Test coverage is weak exactly where the next refactors will land

The current `dream.Tests` suite has good coverage for:

- settings
- HUD layout
- world visibility and render planning
- event hub
- combat feedback
- viewmodel support

But it has little or no direct low-harness coverage for:

- `PlayerController`
- `PlayerMotor`
- `PlayerInteraction`
- runtime extraction boundaries

That means the next foundational refactors would be more fragile than they need to be.

## Most Important First-Phase Risks

### Overeager networking before runtime seams are ready

If transport or protocol code lands before runtime ownership is cleaner, the repo will grow a second coupling problem instead of solving the first one.

### Letting `PlayScene` absorb multiplayer logic

The temptation will be to add:

- snapshot intake
- routing
- server/client state branching

directly to `PlayScene`. That would undermine the architectural work already planned.

### Leaving one-player assumptions inside physics or player construction

That would make later session support expensive and invasive.

## Recommended Foundational Order

### Foundation Step 0

Add low-harness tests around player command capture, interaction edge handling, and player motor behavior where possible.

### Foundation Step 1

Extract `WorldSessionRuntime` so `PlayScene` becomes a client-facing composition shell rather than the owner of simulation truth.

### Foundation Step 2

Change player/session ownership so physics and runtime no longer assume one constructor-owned player actor.

### Foundation Step 3

Only after those are healthy should protocol, transport, and remote-player presentation begin implementation.

## Concrete Areas To Stabilize First

- `dream/Program.cs`
- `dream/PlayScene.cs`
- `dream/Player.cs`
- `dream/PlayerController.cs`
- `dream/PlayerMotor.cs`
- `dream/PlayerInteraction.cs`
- `dream/PhysicsIntegrator.cs`
- `dream/PhysicsSyncSystem.cs`
- `dream.Tests/*` for new focused movement/interaction tests

## Recommendation

The first needed work is not transport or rendering.

It is a preflight multiplayer foundation pass that:

- adds confidence to the player/runtime seams
- extracts authoritative runtime ownership out of `PlayScene`
- removes the strongest one-player assumptions from physics and simulation construction

That is the cleanest and safest foundation for everything already planned in the multiplayer ledger set.
