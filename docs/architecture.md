# Architecture

## Startup Flow

The application entry point is `Program.Main(string[] args)`. It now performs an explicit process-mode selection and argument validation step before startup work begins:

- no mode flag selects the current desktop path
- `--server` is recognized, but it exits honestly as not implemented in this phase
- any other startup argument is rejected instead of silently falling through to desktop

Today, the desktop branch still creates a `Game` object and calls `Run()`, which hands control to OpenTK's `GameWindow` lifecycle. This keeps the default local client startup unchanged while giving later phases one obvious seam for host branching.

The next planned follow-on work for this startup seam is captured in:

- Phase 1: `docs/ledger/plans/2026-06-29-phase1-world-session-runtime-extraction.md` extracts `WorldSessionRuntime` so the authoritative runtime stops living directly under the desktop scene
- Phase 4: `docs/ledger/plans/2026-06-29-phase4-protocol-contract-and-routing.md` defines the protocol seam, message routing, and host/client wiring boundaries that a later server path should plug into
- Phase 4: `docs/ledger/plans/2026-06-29-phase4-dedicated-server-host-and-transport-poc.md` adds `ServerBootstrap`, `DedicatedServerHost`, and the first headless transport-backed server path once that host work begins

Inside the `Game` constructor:

- `GameSettings` is created first
- `PlayScene` is created next and receives the settings object
- The mouse cursor is grabbed by setting `CursorState = CursorState.Grabbed`

## Main Object Relationships

- `Game` is the OpenTK window shell and lifecycle owner.
- `PlayScene` is the active gameplay scene and current desktop orchestration root for per-frame update/render flow.
- `PlayScene` still temporarily constructs and owns authoritative world state access points such as `Player`, `Map`, `PhysicsIntegrator`, and `PhysicsSyncSystem`; the planned `WorldSessionRuntime` extraction is expected to take over that ownership while `PlayScene` stays client-facing.
- `PlayScene` now owns the local `SceneEventHub` seam that future scene-scoped event publication and subscription lifetime management should flow through, rather than introducing a global engine bus.
- `GameSettings` now owns both raw tuning values and a small runtime state model (`RuntimeState`) plus targeted change events for HUD-driven configuration updates.
- `PhysicsIntegrator` owns the Bullet collision configuration, dispatcher, broadphase, solver, world, the currently registered player body slot, and registered map bodies, while helper systems now handle body creation, registration, and transform syncing.
- `Map` remains the world façade, but internally delegates sparse-cell storage to `WorldGrid`, cube authoring to `WorldGenerator`, and Bullet registration to `WorldPhysicsBinder`.
- `Player` remains the gameplay façade, but internally delegates input capture to `PlayerController`, movement/body control to `PlayerMotor`, camera synchronization to `PlayerCameraRig`, and request-style placement capture to `PlayerInteraction`.
- `Render` still owns shared low-level OpenGL state, shader programs, cube geometry buffers, HUD buffers, and ImGui resources, while scene-facing rendering is now exposed through `WorldRenderer`, `DebugRenderer`, `ViewModelRenderer`, and `HudRenderer`.
- `EnemyRegistry` now owns the active enemy collection and scene-local hit resolution for non-player combat actors.
- `EnemyActor` owns world-space chase behavior and melee state transitions, while `EnemyCombatant` owns health, attack cadence, combat identity, and damage intake for the same actor.
- `EnemyRenderer` now owns the first world-space non-cube animated-actor draw path and keeps wolf presentation logic out of `PlayScene`.
- `PlayScene` now owns scene-local combat feedback wiring, while `HudRenderer` and `Render` consume `CombatFeedbackSystem` state as render input rather than constructing combat feedback runtime themselves.
- `PlayerViewModelController` owns first-person runtime presentation state and translates gameplay input into per-slot animation requests.
- `ViewModelPoseResolver` composes the final first-person slot pose from asset defaults, shared rig settings, body-anchor illusion offsets, and per-slot tuning data.
- `ViewModelCatalog` resolves stable cosmetic ids into runtime `ViewModelDefinition` data and provides the default hands plus held-item loadout.
- `Shader` is a low-level helper used by `Render` and `AnimatedModel`.
- `AnimatedModel` owns glTF mesh, material, texture, and draw buffer data for model-backed rendering, including named clip playback with loop and one-shot semantics plus a bounded first-pass skinning path for skinned assets.

## Update Loop

`Game.OnUpdateFrame()` now delegates gameplay-frame work to `PlayScene.Update()`.

`PlayScene.Update()` performs the current simulation work in this order:

1. Toggle runtime state when global scene input requests it
2. Capture gameplay interaction input, including direct interaction-mode selection
3. Translate primary-use input into either build placement or combat interaction based on the active `PlayerInteractionMode`
4. Advance the first-person viewmodel models and notify the controller when one-shot clips finish
5. Step the Bullet physics simulation with the current frame delta
6. Update the player using the current keyboard and mouse state
7. Apply build placement only when the active mode is `Building`
8. Update active enemies, including chase-state transitions and melee damage against the player when gameplay input is live
9. Use `PhysicsSyncSystem` to copy Bullet transform origins back into dynamic `Cube.Position` values

Those last four steps are the current authoritative runtime seam. The planned `WorldSessionRuntime` should eventually own physics stepping, player/mutation update order, and Bullet-to-world synchronization, while `PlayScene` remains responsible for local input capture, viewmodel presentation, and other client-only reactions.

Phase 0 intentionally leaves some one-player assumptions in place: the desktop path still has one locally composed `Player`, one registered player body, and one scene-owned update order. `WorldSessionRuntime`, transport, a session registry, and any remote-player path do not exist yet.

`Player.Update()` now coordinates smaller subsystems:

- `PlayerController` converts raw keyboard and mouse state into movement and interaction commands
- `PlayerMotor` owns freeze/jump/move application against the Bullet rigid body and now also owns noclip toggle state plus camera-relative fly movement bypass
- `PlayerCameraRig` synchronizes camera position from the body and applies mouse-look
- `PlayerInteraction` handles edge-triggered build-use capture and returns plain placement requests
- `Player` itself retains collision reporting and public façade methods used by `PlayScene`

## Render Loop

`Game.OnRenderFrame()` delegates gameplay rendering to `PlayScene.Render()`, then swaps buffers and updates the shell-owned FPS title.

Inside `PlayScene.Render()`:

- the framebuffer is cleared
- `WorldRenderer` draws active cubes
- `EnemyRenderer` draws active world-space wolf actors through the animated-model path
- `DebugRenderer` draws optional collision overlays
- `ViewModelRenderer` clears only depth and draws the first-person hands and held-item pass
- `HudRenderer` submits HUD/settings UI and returns runtime requests
- runtime floor rebuild behavior is applied when requested

`PlayScene` now assembles world-render candidates through a map-side floor-and-placed split: nearby floor tiles are queried directly from local bounds, while nearby placed cubes come from the spatial index. `WorldRenderer` then applies a lightweight visibility filter and groups surviving cubes into shared model/shader batches when possible. Together, those passes reduce candidate iteration cost, final draw count pressure, and repeated per-cube animated-model setup without requiring a full chunk renderer yet.

The HUD now returns interaction requests upward to the scene layer, while `PlayScene` owns the gameplay and runtime consequences of those requests.

The floating-combat-text path is now event-driven inside the scene boundary. `PlayScene` constructs the scene-local `CombatFeedbackSystem`, `PlayScene.Update()` publishes `DamageAppliedEvent` facts through the scene-local hub, `CombatFeedbackSystem` subscribes and translates them into transient feedback state, and the HUD/render layer only consumes that feedback state for camera-local billboard-style projection and drawing.

Placed cubes now opt into combat capability through a broader `WorldObjectRegistry` rather than by storing permanent HP directly on `Cube`. `PlayScene` now resolves combat hits against enemies first and then world cubes, while the registered runtime record still models a generic world-object lifecycle that future props or interactables can reuse.

Scene-local events are intended for cross-system facts and reactions. Ordered simulation work such as movement, physics stepping, transform sync, and render pass order should remain direct. In the current reactive slice, the scene now publishes world-mutation facts such as `CubePlacedEvent` and `CubeDestroyedEvent` alongside combat facts such as `DamageAppliedEvent` and `ActorDiedEvent`, while debug tracing remains an opt-in scene-local subscriber through the bounded `EventTraceSystem` rather than default always-on scene wiring.

That keeps `PlayScene.Render()` presentation-oriented even though the scene still performs one authoritative consequence today: floor-size rebuild requests currently flow back into scene-owned map rebuild logic. That rebuild path is another future `WorldSessionRuntime` ownership point rather than a place to add more multiplayer-aware logic to `PlayScene`.

The first-person subsystem is intentionally threaded through both `PlayScene.Update()` and `PlayScene.Render()` rather than hidden inside `Render`. `PlayScene` owns when the viewmodel update step runs, when the draw pass runs, and what frame state each receives, while `Render` remains the shared low-level renderer host that exposes focused collaborators instead of absorbing feature-specific first-person rules.

`Render.DrawCube()` chooses between two rendering paths:

- If `Cube.Animation` is present, the cube is rendered through `AnimatedModel.Render()`
- Otherwise, the cube is drawn as a transparent cube plus a wireframe overlay

The HUD path currently includes:

- A crosshair built from simple line geometry
- An ImGui `HUD` window for runtime status, including player HP, active interaction mode, and current derived damage range
- An ImGui `Settings` window that can request camera, resolution, floor-size, collision-debug, viewmodel rig, and live player combat-stat changes
- A floating-combat-text overlay projected from world anchors into screen coordinates

## Physics Model

`PhysicsIntegrator` builds a `DiscreteDynamicsWorld` with gravity pointing down the Y axis. `PlayScene` now uses `PhysicsFactory` to create the player rigid body as a `0.5 x 0.5 x 0.5` box shape with CCD settings enabled, then registers that body explicitly with the integrator.

That shift removes the older implicit "construct the local player body inside `PhysicsIntegrator`" assumption. The integrator still owns the registered-body slot and its cleanup rules, but the desktop scene currently remains the composition root that decides which player body gets registered.

`Map` now separates floor visuals from floor physics and data registration concerns:

- the visible floor is still created as solid cubes in the map
- `WorldPhysicsBinder` registers the floor collision surface once as a merged static floor collider
- later non-floor cubes still receive individual rigid bodies through the same binder

When the floor size changes at runtime:

- `PhysicsIntegrator.ResetMapBodies()` removes the old map rigid bodies
- `PlayScene` currently creates a rebuilt `Map` with the requested square width/depth
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
- `EnemyCombatant` mirrors that adapter approach for the first wolf enemy so player and enemy damage flow through the same event and floating-text seams
- `PlayerCombatant` now also owns a small player-only stat model that derives outgoing damage range, crit damage, attack cadence, and vitality-driven max HP, while `PlayScene` keeps hit resolution and combat-event publication scene-owned

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

For skinned world assets, the current renderer now also uploads a first-pass joint palette from SharpGLTF joint world matrices plus inverse bind matrices. That support is intentionally bounded to the current project need: one logical skin per asset is supported cleanly, which covers the wolf milestone without overbuilding a broader animation stack yet.
