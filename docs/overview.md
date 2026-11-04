# Overview

## What This Project Is

`dream` is a desktop C# project that combines an OpenGL renderer, Bullet physics, and a simple block-based world representation. The current experience is centered on moving a player-controlled camera through a cube grid, building or fighting through interaction modes, and now facing a first-pass animated wolf enemy in the world.

## Runtime Flow

The program starts in `Program.Main()`. With no argument it creates a `Game` instance and calls `Run()`; with `--server` it runs the headless dedicated server (`ServerBootstrap.RunServerMode()`, TCP port 5057). `Game` inherits from `GameWindow`, so OpenTK drives the frame lifecycle through update and render callbacks.

The desktop client is a protocol client of an authoritative server. It opens on a connection screen, connects either to an integrated loopback server (started on demand) or to a remote host, and only then creates `PlayScene`. The server owns the world, physics, combat and persistence; it ticks at 64 Hz and sends snapshots at 32 Hz by default.

On each update frame, the client:

- Captures keyboard and mouse input into a `PlayerCommandFrame` and sends it to the server
- Predicts the local player in its `ClientWorldSession` (physics step and rubble settling) and reconciles that prediction with the server's acknowledged position
- Applies authoritative snapshots, world mutations and enemy state received from the server
- Interpolates remote players and enemies for presentation

On each render frame, the client:

- Clears the color and depth buffers
- Draws static terrain through the batched chunk renderer and the live dynamic cubes through the model/shader batch path, both frustum-culled
- Draws enemies, the local player model (third person) and remote players through the animated-model path
- Draws the first-person viewmodel pass, then the HUD crosshair and ImGui overlay
- Swaps buffers and updates the FPS counter

If the session ends (lost connection, server silence, rejected join) the client returns to the connection screen with the reason.

## Main Systems

- `Game`: owns the main window, frame loop, scene switching, and top-level orchestration
- `PlayScene`, `ClientWorldSession`: the desktop gameplay scene and the client's replicated world
- `WorldSessionRuntime`, `DedicatedServerHost`, `DedicatedServerService`: the authoritative server runtime and its host and network loop
- `PhysicsIntegrator`: creates and steps the Bullet physics world (on the server, and for client prediction)
- `Map`: stores the 3D cube grid and populates the floor
- `Player`: converts input into physics impulses and camera movement
- `EnemyActor` and `EnemyCombatant`: own first-pass enemy movement, health, attack cadence, and combat participation
- `Render`: manages shaders, OpenGL buffers, cube drawing, HUD rendering, and ImGui draw submission
- `Shader`: compiles and links GLSL shader programs
- `AnimatedModel`: loads and renders glTF-backed world and first-person models, including first-pass skinning support for the current wolf asset

## Current Visual Elements

The current repository includes:

- GLSL shaders for world rendering and HUD rendering
- A glTF cube model and related texture images under `dream/animations/`
- An animated wolf asset under `dream/animations/enemies/`
- Cinzel and EB Garamond (SIL OFL, third-party) under `dream/fonts/`, with their licences and `README.md` provenance
- A crosshair rendered from line primitives
- A small ImGui HUD and settings overlay that displays HP, mode, and damage range

## World Layout

The world is represented through explicit width, height, and depth bounds. During initialization, the map fills the `y = 0` layer with solid floor cubes. Those floor cubes use the textured `GrassTextureCube.gltf` model while physics uses one merged floor collider. The current gameplay scene also spawns one wolf enemy near the far side of the floor as the first non-player combat actor.
