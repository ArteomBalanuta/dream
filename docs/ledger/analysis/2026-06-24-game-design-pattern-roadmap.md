# Game Design Pattern Roadmap Analysis

**Purpose:** Assess the current `dream` project structure from a gameplay and game-architecture pattern perspective, then identify the highest-value structural improvements.

**Status:** Complete

**Scope:** Core runtime architecture, gameplay orchestration, world structure, player systems, rendering boundaries, and pattern-oriented refactor opportunities

## Current Architectural Phase

The project is in a healthy prototype-to-product transition stage.

It already has:

- a clear main loop
- a functioning runtime settings model
- a stronger documentation and ledger discipline
- early extraction helpers such as `HudLayout` and `CubeTransforms`

At the same time, several runtime classes are beginning to accumulate too many responsibilities. That is the normal signal that the project is ready for pattern-driven restructuring.

## Primary Structural Findings

### 1. `Game` is acting as a god object

`dream/Game.cs` currently coordinates:

- window lifecycle
- update ordering
- render ordering
- settings application
- debug rendering decisions
- runtime floor rebuilds
- resize handling
- cursor-mode switching

This is workable for a prototype, but it means every new gameplay feature will want to enter through the same file.

### 2. `Player` mixes multiple gameplay concerns

`dream/Player.cs` currently owns:

- input interpretation
- movement motor behavior
- jump rules
- freeze mode
- camera sync
- cube placement
- debug collision logging

This is a strong indicator that the project needs behavior-oriented decomposition.

### 3. `Map` mixes data, generation, and physics binding

`dream/Map.cs` currently combines:

- world cell storage
- floor generation
- visual cube creation
- physics rigid-body registration
- migration/rebuild behavior

This prevents the world model from becoming a reusable gameplay abstraction.

### 4. `Render` is handling too many rendering layers

`dream/Render.cs` currently owns:

- world rendering
- HUD rendering
- settings UI rendering
- collision debug drawing
- OpenGL resource setup

The class is still understandable, but it is already becoming a rendering subsystem bundle rather than a single renderer.

### 5. `PhysicsIntegrator` is both engine wrapper and gameplay factory

`dream/PhysicsIntegrator.cs` owns:

- Bullet world lifecycle
- player body creation
- map-body registration and cleanup
- dynamic-body tracking

This creates unnecessary coupling between low-level physics integration and current gameplay assumptions.

## Pattern Assessment

## Patterns already present or emerging

### Game Loop

The project already follows the classic game loop pattern correctly enough for its current scale:

- update in `OnUpdateFrame()`
- render in `OnRenderFrame()`
- physics stepping before gameplay sync

This should remain the foundation.

### Façade / Subsystem Wrapper

`PhysicsIntegrator` and `Render` already act as broad façade layers over Bullet and OpenGL. That is useful, but each façade now wants internal decomposition.

### Value/Object Utility Extraction

Recent additions such as `HudLayout` and `CubeTransforms` are the right move. They show that the codebase benefits from extracting math/policy from large orchestration files.

## Patterns that should be introduced next

### 1. Scene Pattern

Highest priority.

Introduce a `Scene` or `GameMode` boundary so `Game` becomes the window shell while a `PlayScene` owns the active gameplay world.

This allows future support for:

- main menu
- pause mode
- loading state
- test/debug scenes

### 2. State Pattern

High priority.

Several runtime states are currently represented by booleans and implicit conditions:

- settings visible
- gameplay input allowed
- player frozen
- collision debug visible

These should gradually move toward explicit state handling or at least state-grouped controller logic.

### 3. Command Pattern

High priority for controls and actions.

Inputs such as:

- move
- jump
- place cube
- toggle freeze
- toggle collision debug

should be modeled as gameplay commands rather than direct inline mutation in multiple places.

### 4. Strategy Pattern

Medium priority.

Useful future fit areas:

- player movement models
- world generation policies
- debug visualization modes

This becomes valuable once there is more than one behavior variant.

### 5. Observer / Event Pattern

Medium priority.

Runtime changes such as:

- floor-size apply
- settings changes
- map rebuild completion
- debug-toggle changes

would benefit from event-style decoupling once more systems depend on them.

### 6. Factory Pattern

Medium priority.

Good target domains:

- player-body creation
- cube rigid-body creation
- generated world content
- future entity spawning

This would reduce repeated creation assumptions scattered across runtime classes.

## Patterns to avoid right now

### Full ECS

Not yet recommended.

The project is still better served by clear object decomposition and subsystem boundaries than by a full entity-component-system rewrite.

### Large global event bus

Also not recommended yet.

The codebase is not large enough to justify a generic global messaging architecture. Focused typed events are enough.

## Recommended Target Architecture

Near-term desired structure:

- `Game`
  - window shell only
- `PlayScene`
  - active gameplay orchestration
- `WorldGrid` or `VoxelWorld`
  - authoritative world data
- `WorldPhysics` or `PhysicsWorld`
  - Bullet ownership and sync
- `PlayerController`
  - interprets commands/input
- `PlayerMotor`
  - movement and jump rules
- `PlayerCameraRig`
  - camera follow/look behavior
- `WorldRenderer`
  - cube/world rendering
- `HudRenderer`
  - HUD and settings UI
- `DebugRenderer`
  - collision and diagnostic overlays

## Refactor Heuristic

The right next moves are not “more abstraction everywhere.”

They are:

1. separate orchestration from domain logic
2. separate simulation state from presentation state
3. separate runtime systems by responsibility
4. keep creation logic centralized

That produces most of the pattern benefits without overengineering the project.
