# Runtime Floor Size Setting Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a runtime floor-size setting that rebuilds the map safely while preserving the player and any cubes that still fit.

**Architecture:** The work will make `Map` constructible with a configurable square floor size, add a migration helper for preserving non-floor cubes, route the settings UI through an explicit apply action, and let `Game` rebuild the world in place by clearing old map bodies, creating a new map, migrating cubes, and resetting the player into valid bounds.

**Tech Stack:** C#/.NET 8, OpenTK, BulletSharp, ImGui.NET, xUnit

---

### Task 1: Make map construction and migration rebuild-friendly

**Files:**
- Modify: `dream/Map.cs`
- Modify: `dream.Tests/MapPerformanceStructureTests.cs`

- [ ] Add configurable map dimensions driven by floor size
- [ ] Add a migration helper that copies surviving non-floor cubes into a target map
- [ ] Add tests for custom floor size and migration preservation/drop behavior

### Task 2: Add floor-size runtime state

**Files:**
- Modify: `dream/GameSettings.cs`
- Optional Modify: `dream.Tests/GameSettingsTests.cs`

- [ ] Store current floor size in `GameSettings`
- [ ] Add a bounded staged/requested floor-size state if needed for UI apply behavior
- [ ] Add or update tests for the new floor-size state behavior

### Task 3: Wire floor-size controls into the settings UI

**Files:**
- Modify: `dream/Render.cs`

- [ ] Extend the HUD/settings interaction result to include a requested floor-size apply action
- [ ] Add a floor-size slider or input
- [ ] Add an explicit `Apply Floor Size` button

### Task 4: Rebuild the world in `Game`

**Files:**
- Modify: `dream/Game.cs`
- Modify: `dream/PhysicsIntegrator.cs`
- Optional Modify: `dream/Player.cs`

- [ ] Add a map-body reset/removal path to the physics integrator
- [ ] Detect floor-size apply requests in `Game`
- [ ] Rebuild the map with the new square floor size
- [ ] Migrate surviving cubes into the rebuilt map
- [ ] Preserve or clamp the player position and reset body transform/velocities cleanly
- [ ] Update `Player.map` and `Game._map`

### Task 5: Update docs and ledger

**Files:**
- Modify: `README.md`
- Modify: `docs/build-and-run.md`
- Modify: `docs/configuration.md`
- Modify: `docs/architecture.md`
- Create: `docs/ledger/work-log/2026-06-24-runtime-floor-size-setting.md`

- [ ] Document the runtime floor-size control and rebuild semantics
- [ ] Record verification commands and observed results

### Task 6: Verify build, tests, and startup behavior

**Files:**
- Test: `dream/dream.csproj`
- Test: `dream.Tests/dream.Tests.csproj`

- [ ] Run `dotnet test dream.Tests\\dream.Tests.csproj --nologo`
- [ ] Run `dotnet build dream\\dream.csproj --nologo`
- [ ] Run a controlled startup of the app
- [ ] State honestly whether the settings-driven floor resize itself was visually confirmed
