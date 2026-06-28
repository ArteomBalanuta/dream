# Architecture

## Startup Flow

The application entry point is `Program.Main()`. It creates a `Game` object in a `using` block and calls `Run()`, which hands control to OpenTK's `GameWindow` lifecycle.

Inside the `Game` constructor:

- `GameSettings` is created first
- `PlayScene` is created next and receives the settings object
- The mouse cursor is grabbed by setting `CursorState = CursorState.Grabbed`

## Main Object Relationships

- `Game` is the OpenTK window shell and lifecycle owner.
- `PlayScene` is the active gameplay scene and coordinates per-frame gameplay update/render flow.
- `GameSettings` now owns both raw tuning values and a small runtime state model (`RuntimeState`) plus targeted change events for HUD-driven configuration updates.
- `PhysicsIntegrator` owns the Bullet collision configuration, dispatcher, broadphase, solver, world, player body, and registered map bodies, while helper systems now handle body creation and transform syncing.
- `Map` remains the world façade, but internally delegates sparse-cell storage to `WorldGrid`, cube authoring to `WorldGenerator`, and Bullet registration to `WorldPhysicsBinder`.
- `Player` remains the gameplay façade, but internally delegates input capture to `PlayerController`, movement/body control to `PlayerMotor`, camera synchronization to `PlayerCameraRig`, and placement actions to `PlayerInteraction`.
- `Render` still owns shared low-level OpenGL state, shader programs, cube geometry buffers, HUD buffers, and ImGui resources, while scene-facing rendering is now exposed through `WorldRenderer`, `DebugRenderer`, `ViewModelRenderer`, and `HudRenderer`.
- `HudRenderer` now also owns the transient floating-combat-text runtime used for screen-space combat number presentation.
- `PlayerViewModelController` owns first-person runtime presentation state and translates gameplay input into per-slot animation requests.
- `ViewModelPoseResolver` composes the final first-person slot pose from asset defaults, shared rig settings, body-anchor illusion offsets, and per-slot tuning data.
- `ViewModelCatalog` resolves stable cosmetic ids into runtime `ViewModelDefinition` data and provides the default hands plus held-item loadout.
- `Shader` is a low-level helper used by `Render` and `AnimatedModel`.
- `AnimatedModel` owns glTF mesh, material, texture, and draw buffer data for model-backed rendering, including named clip playback with loop and one-shot semantics.

## Update Loop

`Game.OnUpdateFrame()` now delegates gameplay-frame work to `PlayScene.Update()`.

`PlayScene.Update()` performs the current simulation work in this order:

1. Toggle runtime state when global scene input requests it
2. Translate primary-use input into `PlayerViewModelController` animation requests
3. Advance the first-person viewmodel models and notify the controller when one-shot clips finish
4. Step the Bullet physics simulation with the current frame delta
5. Update the player using the current keyboard and mouse state
6. Try to place a cube in front of the player
7. Use `PhysicsSyncSystem` to copy Bullet transform origins back into dynamic `Cube.Position` values

`Player.Update()` now coordinates smaller subsystems:

- `PlayerController` converts raw keyboard and mouse state into movement and interaction commands
- `PlayerMotor` owns freeze/jump/move application against the Bullet rigid body
- `PlayerCameraRig` synchronizes camera position from the body and applies mouse-look
- `PlayerInteraction` handles edge-triggered cube placement
- `Player` itself retains collision reporting and public façade methods used by `PlayScene`

## Render Loop

`Game.OnRenderFrame()` delegates gameplay rendering to `PlayScene.Render()`, then swaps buffers and updates the shell-owned FPS title.

Inside `PlayScene.Render()`:

- the framebuffer is cleared
- `WorldRenderer` draws active cubes
- `DebugRenderer` draws optional collision overlays
- `ViewModelRenderer` clears only depth and draws the first-person hands and held-item pass
- `HudRenderer` submits HUD/settings UI and returns runtime requests
- runtime floor rebuild behavior is applied when requested

`PlayScene` now assembles world-render candidates through a map-side floor-and-placed split: nearby floor tiles are queried directly from local bounds, while nearby placed cubes come from the spatial index. `WorldRenderer` then applies a lightweight visibility filter and groups surviving cubes into shared model/shader batches when possible. Together, those passes reduce candidate iteration cost, final draw count pressure, and repeated per-cube animated-model setup without requiring a full chunk renderer yet.

The HUD result now also carries explicit runtime requests, including resolution changes and floor-size rebuild requests, back to `Game`.

The floating-combat-text first pass is intentionally integrated through the existing HUD flow rather than through a new world-space UI stack. `PlayScene.Update()` now routes the current demo spawn through the player's `CombatActorHandle`, `HudRenderer` advances pooled entry state, and `Render.DrawHUD()` performs the actual screen-space projection and text draw submission.

The first-person subsystem is intentionally threaded through both `PlayScene.Update()` and `PlayScene.Render()` rather than hidden inside `Render`. `PlayScene` owns when the viewmodel update step runs, when the draw pass runs, and what frame state each receives, while `Render` remains the shared low-level renderer host that exposes focused collaborators instead of absorbing feature-specific first-person rules.

`Render.DrawCube()` chooses between two rendering paths:

- If `Cube.Animation` is present, the cube is rendered through `AnimatedModel.Render()`
- Otherwise, the cube is drawn as a transparent cube plus a wireframe overlay

The HUD path currently includes:

- A crosshair built from simple line geometry
- An ImGui `HUD` window for runtime status
- An ImGui `Settings` window that can request camera, resolution, floor-size, collision-debug, and viewmodel rig changes
- A floating-combat-text overlay projected from world anchors into screen coordinates

## Physics Model

`PhysicsIntegrator` builds a `DiscreteDynamicsWorld` with gravity pointing down the Y axis. It now uses `PhysicsFactory` to create the player rigid body as a `0.5 x 0.5 x 0.5` box shape with CCD settings enabled.

`Map` now separates floor visuals from floor physics and data registration concerns:

- the visible floor is still created as solid cubes in the map
- `WorldPhysicsBinder` registers the floor collision surface once as a merged static floor collider
- later non-floor cubes still receive individual rigid bodies through the same binder

When the floor size changes at runtime:

- `PhysicsIntegrator.ResetMapBodies()` removes the old map rigid bodies
- `Game` creates a rebuilt `Map` with the requested square width/depth
- `Map.CopyPlacedCubesTo()` migrates surviving non-floor cubes into the rebuilt map
- `Player.SetWorldPosition()` resets the player transform and clears stale velocities after the rebuild

The player and dynamic map cubes are linked through Bullet rigid bodies. During updates, Bullet is treated as the authoritative source of dynamic physical positions, and `PhysicsSyncSystem` copies those world-space origins back into the rendering-side `Cube` objects.

## Rendering Model

`Render` creates and reuses:

- A cube VAO/VBO for non-model cube rendering
- A shader dictionary keyed by logical names such as `default` and `cube`
- HUD VAO/VBO resources
- ImGui context, texture, and buffer resources
- A dedicated `ViewModelRenderer` collaborator for camera-relative first-person presentation

Projection is rebuilt from the live window size and configured field of view. The HUD crosshair is also rebuilt from live viewport data.

`ViewModelRenderer` owns the first-person pass behavior:

- resolve slot models from the current frame state during `PlayScene.Update()`
- advance clip playback and report one-shot completion back to `PlayerViewModelController`
- resolve active hands and held-item models from the current `ViewModelFrameState`
- apply a viewmodel-local transform and dedicated first-person projection
- clear only the depth buffer before drawing so world color stays intact while world depth does not clip the first-person pass
- render after world and collision-debug passes, but before HUD and ImGui

`GameSettings` now also owns persisted `ViewModelRigSettings`, which are edited through the runtime menu and consumed by `PlayerViewModelController` plus `ViewModelPoseResolver`. That split keeps `Render` responsible for control presentation while the pose math remains in focused first-person collaborators.

The emerging combat layer is intentionally capability-based:

- `IDamageable` defines shared damage intake behavior
- `ICombatAnchorProvider` defines the reusable world anchor used by combat feedback
- `HealthState` is the first shared combat-state component and can back players, enemies, or destructible objects
- `CombatActorHandle` packages identity, kind, damageability, and anchor access into a lightweight runtime handle
- `PlayerCombatant` is the first concrete adapter and keeps player combat participation out of the heavier movement and physics code

For collision debugging, `Render` now uses Bullet-driven transforms for red wireframe overlays instead of reusing the normal visual cube transform path. `Game` enables those overlays conditionally for:

- cube rigid bodies that correspond to actual cube pieces
- the player rigid body

After the Phase 1 scene extraction, the enable/disable decision is owned by `PlayScene`, while `Game` remains responsible only for shell lifecycle and cursor application.

## Current Performance Shape

The codebase now avoids several of the earlier floor-scaling costs:

- the world uses separate `Width`, `Height`, and `Depth` bounds instead of one cubic side size
- empty cells are left unmaterialized instead of eagerly creating `Cube` objects everywhere
- `Map.SolidCubes` is used as the render source instead of scanning the full world array every frame
- the flat base floor uses one merged static collider instead of one rigid body per floor tile
- `Shader` caches uniform locations instead of looking them up repeatedly in hot render paths

## Asset Loading Model

Two parts of the codebase are responsible for runtime asset loading:

- `Shader` reads GLSL files from disk and compiles them into OpenGL programs
- `AnimatedModel` loads a glTF file, loads textures from embedded image content, creates OpenGL textures, and creates mesh buffers for each primitive

The current implementation resolves shader and model assets through shared runtime path helpers rather than through machine-specific absolute Windows paths.
