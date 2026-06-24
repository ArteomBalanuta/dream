# Runtime Upgrade Roadmap Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Upgrade the project to a modern stable .NET/C# baseline, clean up stale dependencies, and replace the legacy BulletSharp dependency through a staged migration.

**Architecture:** Separate safe modernization from risky physics replacement. First centralize and upgrade routine dependencies, then move the target framework to `.NET 10`, and only then migrate physics behind an already-clean facade boundary.

**Tech Stack:** C#/.NET, OpenTK, ImGui.NET, SharpGLTF, SixLabors.ImageSharp, xUnit, BulletSharp to maintained-physics migration

---

### Task 1: Establish package and SDK control

**Files:**
- Create: `G:\workspace\projects\dream\Directory.Packages.props`
- Create: `G:\workspace\projects\dream\global.json`
- Modify: `G:\workspace\projects\dream\dream\dream.csproj`
- Modify: `G:\workspace\projects\dream\dream.Tests\dream.Tests.csproj`

- [ ] Add central package management so dependency versions live in one place
- [ ] Pin the SDK with `global.json` so the repo builds consistently on machines that have multiple SDKs installed
- [ ] Keep the project on `net8.0` during this first step so package-management changes are isolated from framework changes
- [ ] Verify `dotnet build dream\dream.csproj --nologo`
- [ ] Verify `dotnet test dream.Tests\dream.Tests.csproj --nologo`

### Task 2: Take the low-risk dependency upgrades

**Files:**
- Modify: `G:\workspace\projects\dream\Directory.Packages.props`
- Modify: any code files that need small API compatibility updates

- [ ] Upgrade `OpenTK` from `4.9.3` to `4.9.4`
- [ ] Upgrade `OpenTK.Windowing.Desktop` from `4.9.3` to `4.9.4`
- [ ] Upgrade `SharpGLTF.Core` from `1.0.3` to `1.0.6`
- [ ] Upgrade `SixLabors.ImageSharp` from `3.1.6` to `4.0.0` to remove the currently reported vulnerability warnings
- [ ] Upgrade test packages:
  - `Microsoft.NET.Test.Sdk` `17.11.1` -> `18.7.0`
  - `xunit` `2.9.2` -> `2.9.3`
  - `xunit.runner.visualstudio` `2.8.2` -> `3.1.5`
- [ ] Re-run build and tests
- [ ] Document any small source changes required by ImageSharp or test-runner API drift

### Task 3: Remove dead or misleading dependencies

**Files:**
- Modify: `G:\workspace\projects\dream\Directory.Packages.props`
- Modify: `G:\workspace\projects\dream\dream\dream.csproj`
- Modify: docs if runtime assumptions change

- [ ] Confirm `System.Drawing.Common` is truly unused
- [ ] Remove `System.Drawing.Common` instead of upgrading it, unless a hidden runtime path proves it is still required
- [ ] Re-run build and tests after removal
- [ ] Update docs so the repo no longer implies a `System.Drawing` dependency it does not actually use

### Task 4: Move the project to the current stable .NET/C# baseline

**Files:**
- Modify: `G:\workspace\projects\dream\dream\dream.csproj`
- Modify: `G:\workspace\projects\dream\dream.Tests\dream.Tests.csproj`
- Modify: `G:\workspace\projects\dream\README.md`
- Modify: `G:\workspace\projects\dream\docs\configuration.md`

- [ ] Change the target framework from `net8.0` to `net10.0`
- [ ] Do not set preview language versions
- [ ] Allow the compiler to use the stable language version implied by `.NET 10`, which is `C# 14`
- [ ] Verify restore, build, and test with the pinned SDK
- [ ] Update user-facing docs to say the project builds on `.NET 10`

### Task 5: Harden the physics boundary before replacement

**Files:**
- Modify: `G:\workspace\projects\dream\dream\PhysicsIntegrator.cs`
- Modify: `G:\workspace\projects\dream\dream\WorldPhysicsBinder.cs`
- Modify: `G:\workspace\projects\dream\dream\PhysicsFactory.cs`
- Modify: `G:\workspace\projects\dream\dream\PhysicsSyncSystem.cs`
- Modify: `G:\workspace\projects\dream\dream\Player.cs`
- Modify: `G:\workspace\projects\dream\dream\PlayerMotor.cs`
- Modify: `G:\workspace\projects\dream\dream\Render.cs`
- Modify: `G:\workspace\projects\dream\dream\VectorConversion.cs`
- Create: one or more narrow physics-facing interfaces or adapter types if needed

- [ ] Reduce direct `BulletSharp` type usage at gameplay-call sites
- [ ] Concentrate rigid-body creation, stepping, sync, and collision-query behavior behind a tighter adapter boundary
- [ ] Keep public gameplay behavior stable while preparing for a backend swap
- [ ] Add or extend tests around:
  - map rebuild behavior
  - player movement state transitions
  - collision debug toggling
- [ ] Verify gameplay still runs after the boundary tightening

### Task 6: Replace BulletSharp with a maintained physics backend

**Files:**
- Modify: physics-related runtime files under `G:\workspace\projects\dream\dream\`
- Remove: Bullet-specific helpers once the migration is complete
- Modify: `G:\workspace\projects\dream\Directory.Packages.props`
- Modify: docs

- [ ] Preferred path: migrate to a maintained physics engine, with `BepuPhysics v2` as the default recommendation
- [ ] Re-implement the current required capabilities first:
  - gravity and stepping
  - player rigid body
  - static floor collider
  - dynamic cube bodies
  - body-to-render transform sync
  - basic grounded/jump support
- [ ] Keep the initial migration feature-parity focused; do not expand scope into advanced physics features during the swap
- [ ] Remove `BulletSharp` only after all build, test, and runtime checks pass

### Task 7: Final cleanup and verification

**Files:**
- Modify: `G:\workspace\projects\dream\docs\architecture.md`
- Modify: `G:\workspace\projects\dream\docs\configuration.md`
- Modify: `G:\workspace\projects\dream\docs\project-structure.md`
- Create: `G:\workspace\projects\dream\docs\ledger\work-log\2026-06-24-runtime-upgrade-execution.md`

- [ ] Run `dotnet build dream\dream.csproj --nologo`
- [ ] Run `dotnet test dream.Tests\dream.Tests.csproj --nologo`
- [ ] Run the game manually and verify:
  - startup
  - movement
  - jumping
  - floor resizing
  - collision debug rendering
  - settings menu
- [ ] Record exactly which warnings were eliminated and which remain
- [ ] Record final dependency versions and target framework in the ledger
