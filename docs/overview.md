# Overview

## What This Project Is

`dream` is a desktop C# project that combines an OpenGL renderer, Bullet physics, and a simple block-based world representation. The current experience is centered on moving a player-controlled camera through a cube grid, building or fighting through interaction modes, and now facing a first-pass animated wolf enemy in the world.

## Runtime Flow

The program starts in `Program.Main()`, which creates a `Game` instance and calls `Run()`. `Game` inherits from `GameWindow`, so OpenTK drives the frame lifecycle through update and render callbacks.

On each update frame, the project:

- Advances the Bullet physics world
- Updates player movement and camera state from keyboard and mouse input
- Updates scene-local combat and enemy behavior
- Tries to place a cube or attack a target when the left mouse button is pressed
- Copies physics body positions back into cube instances for rendering

On each render frame, the project:

- Clears the color and depth buffers
- Iterates over solid cubes in the map
- Draws the first enemy actor through the animated world-model path
- Draws each cube through the rendering subsystem
- Draws the HUD crosshair and ImGui overlay
- Swaps buffers and updates the FPS counter

## Main Systems

- `Game`: owns the main window, frame loop, and top-level orchestration
- `PhysicsIntegrator`: creates and steps the Bullet physics world
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
- An `Arial.ttf` font file under `dream/fonts/`
- A crosshair rendered from line primitives
- A small ImGui HUD and settings overlay that displays HP, mode, and damage range

## World Layout

The world is represented through explicit width, height, and depth bounds. During initialization, the map fills the `y = 0` layer with solid floor cubes. Those floor cubes use the textured `GrassTextureCube.gltf` model while physics uses one merged floor collider. The current gameplay scene also spawns one wolf enemy near the far side of the floor as the first non-player combat actor.
