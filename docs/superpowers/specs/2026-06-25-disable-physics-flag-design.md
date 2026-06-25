# Manual Disable-Physics Startup Flag Design

Date: 2026-06-25
Status: Drafted and reviewed
Owner: Codex

## Summary

Add a manual startup flag, `--disable-physics`, that lets `dream` run without initializing `BulletSharp`.

The flag should:

- keep default behavior unchanged when omitted
- bypass `PhysicsIntegrator` construction when present
- preserve a playable first-person visual sandbox loop on macOS
- keep the no-physics behavior explicit rather than auto-detected by platform

This is a startup feature flag and a runtime fallback path, not a full physics-backend replacement.

## Goals

- Allow `dotnet run --project dream/dream.csproj -- --disable-physics` to start on macOS without `libbulletc`.
- Preserve the current Bullet-backed behavior as the default path.
- Keep the no-physics mode localized to a small set of startup and gameplay seams.
- Preserve camera look, movement, floor resizing, viewmodels, HUD, and cube placement in no-physics mode.

## Non-Goals

- Replacing `BulletSharp` in this pass.
- Automatically disabling physics on macOS.
- Preserving collision, gravity, jump arcs, or collision debug when physics is off.
- Introducing a full `IPhysicsEngine` abstraction layer.

## User Decisions Captured

- Physics disablement is manual only.
- The entry mechanism is a feature flag, not automatic platform detection.

## Design Overview

The existing startup crash happens during `PlayScene` construction because `PhysicsIntegrator` immediately constructs Bullet objects, which triggers native library loading.

The design should therefore split runtime startup into two explicit modes:

1. Physics enabled
2. Physics disabled

The mode should be chosen once at startup from parsed CLI arguments and then carried through the runtime as configuration, not re-inferred ad hoc.

## Architecture

### Startup Options

Add a small startup-options model near the entrypoint, for example `StartupOptions`.

Responsibilities:

- parse CLI args
- expose `PhysicsEnabled`
- default to `true`
- treat `--disable-physics` as `PhysicsEnabled = false`

`Program.Main()` should parse args first and pass the options into `Game`, which should then pass them into `PlayScene`.

### Scene Ownership Boundary

`PlayScene` should own the decision of whether physics exists for this run.

When physics is enabled:

- construct `PhysicsIntegrator`
- create `Map` with physics
- create `Player` with the Bullet body path
- keep the current update and render flow

When physics is disabled:

- do not construct `PhysicsIntegrator`
- create `Map` in visual-only mode
- create `Player` without a Bullet body
- skip physics stepping and Bullet-body synchronization

This keeps the mode boundary close to the existing scene orchestration instead of spreading flag checks throughout unrelated rendering code.

### Player Runtime Split

`Player` should support two internally distinct movement modes:

- Bullet-backed movement
- direct transform movement

The public scene-facing API should stay as stable as possible:

- update the player
- read world position
- set world position
- place cubes
- expose camera state

When physics is disabled, `Player` should own a world-position field and use `PlayerCameraRig` plus direct movement math instead of `RigidBody` state. The no-physics path should still:

- respond to `W`, `A`, `S`, `D`
- preserve mouse-look
- keep existing movement gating from the settings menu
- place cubes based on camera-facing world position

Jump and freeze behavior should remain coherent, but they do not need to mimic Bullet exactly in this pass. The key requirement is that the no-physics mode is playable and stable, not physically identical.

### Map And Placement Boundary

`Map` already accepts nullable physics and should remain the visual-world owner in both modes.

The no-physics mode should continue to:

- generate the visual floor
- maintain placed-cube grid occupancy
- rebuild floor visuals on runtime floor-size changes
- preserve in-bounds placed cubes during rebuild

Cube placement should continue to call `Map.AddCube()`, but the position source should come from the player's direct world position when physics is off rather than from a Bullet transform.

### Rendering Boundary

Rendering should remain mostly unchanged.

No-physics mode should:

- draw the world normally
- draw viewmodels normally
- draw HUD/settings normally
- suppress collision debug rendering because there are no Bullet bodies to visualize

The collision-debug toggle can remain in settings state, but it should have no visible effect when physics is disabled.

## Runtime Behavior

### Enabled Path

No change from today's default behavior:

- Bullet initializes
- player uses rigid-body movement
- dynamic body sync runs
- collision debug can render

### Disabled Path

Expected behavior:

- app starts without loading Bullet native libraries
- player can move through the world directly
- cube placement still works
- floor resize still rebuilds visual state and clamps player position
- collision debug does not render

Expected limitations:

- no collisions
- no Bullet-driven gravity
- no Bullet contact reporting
- no physics-backed cube simulation

## Risks And Mitigations

### Player logic becomes split-brain

Risk:
Adding a second movement mode can make `Player` harder to reason about.

Mitigation:
Keep the split behind a narrow internal mode boundary and preserve one public façade used by `PlayScene`.

### Null checks spread too widely

Risk:
Optional physics can create scattered defensive checks.

Mitigation:
Contain most branching inside startup, `PlayScene`, and `Player`, and keep render-only code agnostic where possible.

### Tests accidentally load Bullet

Risk:
New tests for the flag could touch types that trigger native loading on macOS.

Mitigation:
Focus tests on startup option parsing and no-physics-safe gameplay helpers that do not construct `PhysicsIntegrator`.

## Verification

Code verification:

- `dotnet build dream/dream.csproj --nologo`
- `dotnet test dream.Tests/dream.Tests.csproj --nologo`

Runtime verification:

- `dotnet run --project dream/dream.csproj -- --disable-physics` starts successfully on macOS
- camera look still works
- movement still works with `W`, `A`, `S`, `D`
- left click still places cubes
- settings menu still opens and closes
- runtime floor resize still applies and clamps player position if needed
- collision debug renders nothing when physics is disabled

Regression verification:

- `dotnet run --project dream/dream.csproj` still follows the current default physics-enabled path

## Implementation Notes

- Prefer a small `StartupOptions` record over passing raw arg arrays through the runtime.
- Reuse the current nullable-physics `Map` seams rather than refactoring world ownership broadly.
- Keep the no-physics movement path simple and deterministic instead of chasing feature parity with Bullet.
- Do not use platform checks to infer mode in this pass; all mode changes should come from explicit startup options.

## Out Of Scope Follow-Ups

- automatic platform-aware defaults
- richer free-fly or noclip controls for no-physics mode
- a formal physics abstraction layer
- Bullet replacement with a maintained backend
