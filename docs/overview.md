# Overview

## What This Project Is

`dream` is a desktop C# project that combines an OpenGL renderer, Bullet physics, and a simple block-based world representation. The current experience is centered on moving a player-controlled camera through a cube grid, rendering textured floor cubes, and placing additional cubes into the world.

## Runtime Flow

The program starts in `Program.Main()`, which creates a `Game` instance and calls `Run()`. `Game` inherits from `GameWindow`, so OpenTK drives the frame lifecycle through update and render callbacks.

On each update frame, the project:

- Advances the Bullet physics world
- Updates player movement and camera state from keyboard and mouse input
- Tries to place a cube when the left mouse button is pressed
- Copies physics body positions back into cube instances for rendering

On each render frame, the project:

- Clears the color and depth buffers
- Iterates over solid cubes in the map
- Draws each cube through the rendering subsystem
- Draws the HUD crosshair and ImGui overlay
- Swaps buffers and updates the FPS counter

## Main Systems

- `Game`: owns the main window, frame loop, and top-level orchestration
- `PhysicsIntegrator`: creates and steps the Bullet physics world
- `Map`: stores the 3D cube grid and populates the floor
- `Player`: converts input into physics impulses and camera movement
- `Render`: manages shaders, OpenGL buffers, cube drawing, HUD rendering, and ImGui draw submission
- `Shader`: compiles and links GLSL shader programs
- `AnimatedModel`: loads and renders the glTF cube model used by solid map cubes

## Current Visual Elements

The current repository includes:

- GLSL shaders for world rendering and HUD rendering
- A glTF cube model and related texture images under `dream/animations/`
- An `Arial.ttf` font file under `dream/fonts/`
- A crosshair rendered from line primitives
- A small ImGui HUD window that currently displays `Score: 100`

## World Layout

The world is represented as a `25 x 25 x 25` cube array. During initialization, the map creates empty cube placeholders for every grid cell, then fills the `y = 0` layer with solid floor cubes. These floor cubes use the textured `GrassTextureCube.gltf` model and are also added to the physics world as rigid bodies.
