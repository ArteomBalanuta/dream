# Game Design Pattern Roadmap

> **For agentic workers:** This roadmap is architectural guidance, not an implementation plan. Use it to derive future specs and implementation plans phase-by-phase.

**Goal:** Guide the next structural evolution of `dream` from a successful prototype into a cleaner gameplay architecture using incremental game-design patterns.

**Architecture:** The roadmap keeps the current game loop and current engine choices, but restructures the code around scenes, explicit runtime state, behavior-focused player systems, world/physics boundaries, and layered renderers. The intention is to preserve momentum while reducing coupling and future feature friction.

**Tech Stack:** C#/.NET 8, OpenTK, BulletSharp, ImGui.NET

---

## Phase 1: Shell vs Play Scene

**Priority:** Highest

**Primary Pattern:** Scene Pattern

**Problem being solved:**

`dream/Game.cs` is currently the runtime shell and the active gameplay scene at the same time.

**Target structure:**

- `Game`
  - owns the window lifecycle only
- `PlayScene`
  - owns player, map/world, physics, renderer coordination

**Likely file impact:**

- Split: `dream/Game.cs`
- Create: `dream/PlayScene.cs`

**Expected benefits:**

- easier future menus/pause/loading scenes
- less pressure on `Game.cs`
- clearer runtime ownership

---

## Phase 2: Runtime State Cleanup

**Priority:** High

**Primary Pattern:** State Pattern

**Problem being solved:**

Runtime booleans currently model several implicit states:

- settings open vs gameplay active
- player frozen vs normal
- collision debug hidden vs visible

**Target structure:**

- introduce explicit gameplay/UI state grouping
- reduce cross-file boolean branching

**Likely file impact:**

- Modify: `dream/Game.cs`
- Modify: `dream/GameSettings.cs`
- Modify: `dream/Player.cs`

**Expected benefits:**

- clearer flow control
- safer feature growth
- easier reasoning about input capture and debug modes

---

## Phase 3: Player System Decomposition

**Priority:** High

**Primary Patterns:** Strategy Pattern, Command Pattern

**Problem being solved:**

`dream/Player.cs` currently handles too many behaviors in one place.

**Target structure:**

- `PlayerController`
  - interprets input into actions/commands
- `PlayerMotor`
  - movement and jump rules
- `PlayerCameraRig`
  - camera sync and look behavior
- optional `PlayerInteraction`
  - cube placement and interaction logic

**Likely file impact:**

- Split: `dream/Player.cs`
- Create: `dream/PlayerController.cs`
- Create: `dream/PlayerMotor.cs`
- Create: `dream/PlayerCameraRig.cs`
- Optional Create: `dream/PlayerInteraction.cs`

**Expected benefits:**

- easier addition of sprint/crouch/tools/inventory later
- improved testability
- simpler per-class responsibility

---

## Phase 4: World Data vs Physics Binding

**Priority:** High

**Primary Patterns:** Factory Pattern, Façade refinement

**Problem being solved:**

`dream/Map.cs` currently mixes world data, floor generation, and physics body creation.

**Target structure:**

- `WorldGrid` or `VoxelWorld`
  - authoritative world cells and cube data
- `WorldGenerator`
  - creates the initial floor/world content
- `WorldPhysicsBinder`
  - creates/removes rigid bodies from world content

**Likely file impact:**

- Split: `dream/Map.cs`
- Create: `dream/WorldGrid.cs` or `dream/VoxelWorld.cs`
- Create: `dream/WorldGenerator.cs`
- Create: `dream/WorldPhysicsBinder.cs`

**Expected benefits:**

- easier save/load later
- cleaner runtime rebuilds
- less coupling between world data and Bullet specifics

---

## Phase 5: Physics Wrapper Refinement

**Priority:** Medium

**Primary Patterns:** Façade, Factory Pattern

**Problem being solved:**

`dream/PhysicsIntegrator.cs` is both Bullet-world owner and gameplay object factory.

**Target structure:**

- `PhysicsWorld`
  - owns Bullet world lifecycle
- `PhysicsFactory`
  - standard body creation helpers
- `PhysicsSyncSystem`
  - syncs Bullet transforms back into gameplay objects

**Likely file impact:**

- Split: `dream/PhysicsIntegrator.cs`
- Create: `dream/PhysicsWorld.cs`
- Create: `dream/PhysicsFactory.cs`
- Create: `dream/PhysicsSyncSystem.cs`

**Expected benefits:**

- cleaner engine boundary
- easier future entity/body creation consistency
- reduced gameplay-specific assumptions in low-level code

---

## Phase 6: Renderer Layering

**Priority:** Medium

**Primary Patterns:** Renderer layering, Single Responsibility

**Problem being solved:**

`dream/Render.cs` currently contains world, HUD, settings UI, and debug rendering together.

**Target structure:**

- `WorldRenderer`
- `HudRenderer`
- `DebugRenderer`
- keep `Shader`, `HudLayout`, and `CubeTransforms` as shared support utilities

**Likely file impact:**

- Split: `dream/Render.cs`
- Create: `dream/WorldRenderer.cs`
- Create: `dream/HudRenderer.cs`
- Create: `dream/DebugRenderer.cs`

**Expected benefits:**

- better isolation between visual systems
- easier evolution of debug tools
- smaller rendering files with clearer intent

---

## Phase 7: Input Commands and Targeted Events

**Priority:** Medium-Low

**Primary Patterns:** Command Pattern, Observer/Event Pattern

**Problem being solved:**

Gameplay actions are still mostly immediate inline mutations.

**Target structure:**

- input-to-command conversion for major actions
- targeted events for settings changes, rebuild requests, and debug toggles

**Likely file impact:**

- Modify: `dream/Game.cs`
- Modify: `dream/Player.cs` or its decomposed replacements
- Modify: `dream/GameSettings.cs`
- Optional Create: `dream/GameCommands.cs`
- Optional Create: `dream/GameEvents.cs`

**Expected benefits:**

- cleaner decoupling
- easier rebinds/replay/testing later
- better orchestration once system count grows

---

## Recommended Execution Order

1. Phase 1: Shell vs Play Scene
2. Phase 2: Runtime State Cleanup
3. Phase 3: Player System Decomposition
4. Phase 4: World Data vs Physics Binding
5. Phase 6: Renderer Layering
6. Phase 5: Physics Wrapper Refinement
7. Phase 7: Input Commands and Targeted Events

## Guidance Notes

- Keep the current game loop.
- Prefer incremental extraction over broad rewrites.
- Preserve the current working runtime behavior between phases.
- Write a dedicated spec and implementation plan per phase before code changes.
