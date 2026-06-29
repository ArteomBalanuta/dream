# Client Server Refactor Seams

Purpose: identify the current runtime seams that can support a dedicated-server and multiplayer refactor without rewriting the engine from scratch.
Status: complete
Scope: `Game`, `PlayScene`, player/world/physics ownership, rendering boundaries, and the first networking-oriented runtime splits

## Summary

`dream` already has several useful boundaries for multiplayer work:

- `Game` is mostly the desktop shell
- `PlayScene` is the active gameplay root
- `Player` is already decomposed into controller, motor, camera, and interaction helpers
- `Map` already delegates storage and physics binding
- `Render` already exposes focused scene-facing collaborators
- `SceneEventHub` already exists for scene-local facts and reactions

The main networking obstacle is not a total lack of structure. The obstacle is that the current runtime still assumes one in-process desktop host where simulation, local input, Bullet ownership, HUD, and presentation all live in the same scene object graph.

## Strong Existing Seams

### 1. `Game` is already a shell candidate

`Game` mostly owns:

- OpenTK window lifetime
- frame callbacks
- cursor state application
- title/FPS updates

That makes it a strong future client-only shell. The dedicated server should not depend on this type at all.

### 2. `PlayScene` is already the orchestration root

`PlayScene` already owns:

- world creation
- physics creation
- player creation
- update ordering
- render ordering
- scene-local events

That is the natural source for extracting a host-agnostic simulation runtime. The important next move is to stop treating `PlayScene` as both the simulation root and the client presentation root.

### 3. Player responsibilities are already partly decomposed

The split between:

- `PlayerController`
- `PlayerMotor`
- `PlayerCameraRig`
- `PlayerInteraction`
- `PlayerCombatant`

is valuable because multiplayer does not need one giant `Player` rewrite. It needs a clearer split between:

- server-owned player state
- client-owned local input and camera presentation
- shared command and replication shapes

### 4. World storage is closer to reusable than the scene layer is

`Map`, `WorldGrid`, and `WorldPhysicsBinder` already separate:

- world cell storage
- content creation
- physics registration

That is a good base for making world mutation flow through server authority rather than through direct local scene calls.

### 5. Render collaborators are client-only by nature

`WorldRenderer`, `HudRenderer`, `ViewModelRenderer`, and `DebugRenderer` are already presentation-facing types. They should remain on the client side and should not leak into the server runtime.

## Current Coupling That Blocks Multiplayer

### 1. `PlayScene` constructs the entire local world stack directly

Today `PlayScene` creates:

- `PhysicsIntegrator`
- `Map`
- `Player`
- `Render`
- `CombatFeedbackSystem`

That makes host selection impossible. A dedicated server should be able to build a simulation stack without rendering, while a client should be able to build presentation state around either a local predicted mirror or replicated state.

### 2. `Player` still assumes direct local device input

`Player.Update(...)` currently depends on `KeyboardState` and `MouseState` flow through local capture helpers. That is correct for desktop play, but a server-owned player actor cannot depend on desktop input objects.

The server needs command input such as:

- movement intent
- look intent
- primary use intent
- mode switch intent

The client may still use the current controller helpers, but only to translate local device state into serialized player commands.

### 3. Camera and player state still ride together

`PlayerCameraRig` is inherently client-local. The server needs player aim and orientation facts, but not a render camera with desktop mouse-look behavior.

This means the current `Player` façade is still too mixed for multiplayer:

- body/position/combat state belongs to simulation
- view/camera behavior belongs to the client

### 4. Physics is owned as a local scene implementation detail

`PhysicsIntegrator` is currently created and stepped inside the same scene flow that also drives rendering and HUD. For multiplayer, Bullet becomes part of the authoritative simulation host, not part of a desktop scene convenience layer.

### 5. World mutation is still local-method driven

Placement and destruction flow through direct scene/player/world calls. That is fine for single-player, but multiplayer needs:

- commands entering the server
- validation on the server tick
- authoritative mutation results
- replicated state or events returning to clients

### 6. Scene events are local but not yet replication-aware

`SceneEventHub` is a good seam for local reactions, but the current event model is not yet separated into:

- simulation facts
- local client reactions
- network replication payloads

The same event names do not have to become wire messages automatically.

## Recommended Runtime Split

The best next shape is a three-layer split:

### Host Layer

Host-specific entry points:

- `DedicatedServerHost`
- `ClientGameHost`

Responsibilities:

- process lifetime
- transport startup/shutdown
- fixed tick scheduling on the server
- desktop frame/render loop on the client

### Simulation Layer

Host-agnostic core runtime:

- `WorldSessionRuntime`
- `ServerSimulationTick`
- `PlayerSessionRegistry`
- `WorldCommandProcessor`
- `ReplicationSnapshotBuilder`

Responsibilities:

- authoritative world state
- authoritative player state
- combat and build resolution
- physics stepping
- event publication for simulation facts

### Presentation Layer

Client-only runtime:

- `ClientScenePresenter`
- `RemotePlayerPresentationStore`
- existing render collaborators
- HUD and first-person viewmodel systems

Responsibilities:

- rendering
- input capture and command generation
- local interpolation and optional prediction
- transient feedback such as floating text and viewmodel animation

## First New Boundaries To Introduce

### 1. Player command boundary

Add a transport-safe command model such as:

- `PlayerCommandFrame`
- `MoveIntent`
- `LookIntent`
- `PrimaryUseIntent`

This becomes the bridge between client input and server simulation.

### 2. Player session boundary

Add explicit player/session ownership such as:

- `PlayerSessionId`
- `ConnectedPlayer`
- `PlayerSessionRegistry`

The scene currently assumes one local player. The server runtime must assume many.

### 3. Simulation tick boundary

Introduce a dedicated fixed-step loop model such as:

- `SimulationClock`
- `ServerTickRunner`
- `TickContext`

The server should advance at 64 Hz regardless of client render cadence.

### 4. Replication state boundary

Introduce explicit outbound state models such as:

- `WorldSnapshot`
- `ActorSnapshot`
- `WorldMutationDelta`
- `ServerEventEnvelope`

The exact transport can change later, but these replication categories should not be implicit in scene types.

## What Should Not Be Forced Server-Side Yet

For the first multiplayer roadmap, some systems should stay client-local:

- first-person viewmodel animation
- HUD layout and ImGui menu rendering
- floating combat text presentation
- debug overlays
- camera smoothing/interpolation

Those systems may consume authoritative state, but they do not need to be part of the server simulation.

## Refactor Recommendation

Do not begin with transport details.

Begin with extracting an authoritative simulation core from `PlayScene`, then route local input through command frames and replicated state through explicit snapshot/delta models. That gives the repo a path to a working dedicated-server PoC without forcing a total engine rewrite.
