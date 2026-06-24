# Architecture

## Startup Flow

The application entry point is `Program.Main()`. It creates a `Game` object in a `using` block and calls `Run()`, which hands control to OpenTK's `GameWindow` lifecycle.

Inside the `Game` constructor:

- `PhysicsIntegrator` is created first
- `Map` is created next and receives the physics integrator
- `Player` is created with the map, physics integrator, player rigid body, and starting position
- `Render` is created last
- The mouse cursor is grabbed by setting `CursorState = CursorState.Grabbed`

## Main Object Relationships

- `Game` is the coordinator for windowing, updates, rendering, and lifetime management.
- `PhysicsIntegrator` owns the Bullet collision configuration, dispatcher, broadphase, solver, world, player body, and map rigid bodies.
- `Map` owns the `Cube[,,]` grid and asks the physics system to register rigid bodies for solid cubes.
- `Player` holds the camera and player rigid body, using physics impulses for movement.
- `Render` owns OpenGL state, shader programs, cube geometry buffers, HUD buffers, and ImGui resources.
- `Shader` is a low-level helper used by `Render` and `AnimatedModel`.
- `AnimatedModel` owns glTF mesh, material, texture, and draw buffer data for model-based cube rendering.

## Update Loop

`Game.OnUpdateFrame()` performs the current simulation work in this order:

1. Step the Bullet physics simulation with the current frame delta
2. Update the player using the current keyboard and mouse state
3. Try to place a cube in front of the player
4. Iterate through map rigid bodies and copy Bullet transform origins back into `Cube.Position`

`Player.Update()` currently handles:

- Reading `W`, `A`, `S`, and `D` for horizontal movement
- Reading `Space` for jumping
- Reading `F` for frozen-state toggling
- Applying impulses to the player rigid body
- Clamping horizontal speed
- Updating the camera orientation from mouse delta
- Running collision logging at a timed interval

## Render Loop

`Game.OnRenderFrame()` clears the framebuffer, iterates over every solid cube in `Map.Cubes`, and calls `Render.DrawCube()` for each one. After world drawing, it calls `Render.DrawHUD()` and then swaps buffers.

`Render.DrawCube()` chooses between two rendering paths:

- If `Cube.Animation` is present, the cube is rendered through `AnimatedModel.Render()`
- Otherwise, the cube is drawn as a transparent cube plus a wireframe overlay

The HUD path currently includes:

- A crosshair built from simple line geometry
- An ImGui frame that renders a non-interactive `HUD` window

## Physics Model

`PhysicsIntegrator` builds a `DiscreteDynamicsWorld` with gravity pointing down the Y axis. It also creates the player rigid body as a `0.5 x 0.5 x 0.5` box shape with CCD settings enabled.

`Map.AddCube()` creates a solid cube at a snapped grid location, assigns it a model-backed visual representation, and registers a `RigidBody` for it through `AddPhysicsBody()`. Static floor cubes are created with zero mass. Dynamic cubes use a mass of `1.0f`.

The player and map cubes are linked through Bullet rigid bodies. During updates, Bullet is treated as the authoritative source of physical positions, and the world-space origins from Bullet transforms are copied back into the rendering-side `Cube` objects.

## Rendering Model

`Render` creates and reuses:

- A cube VAO/VBO for non-model cube rendering
- A shader dictionary keyed by logical names such as `default` and `cube`
- HUD VAO/VBO resources
- ImGui context, texture, and buffer resources

Projection is currently created inside `Render` as a perspective matrix with a fixed aspect ratio of `1.0f`. The HUD uses an orthographic projection sized to `800 x 600`.

## Asset Loading Model

Two parts of the codebase are responsible for runtime asset loading:

- `Shader` reads GLSL files from disk and compiles them into OpenGL programs
- `AnimatedModel` loads a glTF file, loads textures from embedded image content, creates OpenGL textures, and creates mesh buffers for each primitive

The current implementation uses hardcoded absolute Windows paths for several shader and model references in `Render` and `Map`.
