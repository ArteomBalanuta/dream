# Architecture

## Startup Flow

The application entry point is `Program.Main()`. It creates a `Game` object in a `using` block and calls `Run()`, which hands control to OpenTK's `GameWindow` lifecycle.

Inside the `Game` constructor:

- `PhysicsIntegrator` is created first
- `Map` is created next and receives the physics integrator plus the initial floor size from `GameSettings`
- `Player` is created with the map, physics integrator, player rigid body, and starting position
- `Render` is created last
- The mouse cursor is grabbed by setting `CursorState = CursorState.Grabbed`

## Main Object Relationships

- `Game` is the coordinator for windowing, updates, rendering, and lifetime management.
- `PhysicsIntegrator` owns the Bullet collision configuration, dispatcher, broadphase, solver, world, player body, all map bodies, and the dynamic-only map-body subset used for transform syncing.
- `Map` owns the world cell storage, active solid cube tracking, floor generation, and cube registration with the physics system.
- `Player` holds the camera and player rigid body, using physics impulses for movement.
- `Render` owns OpenGL state, shader programs, cube geometry buffers, HUD buffers, and ImGui resources.
- `Shader` is a low-level helper used by `Render` and `AnimatedModel`.
- `AnimatedModel` owns glTF mesh, material, texture, and draw buffer data for model-based cube rendering.

## Update Loop

`Game.OnUpdateFrame()` performs the current simulation work in this order:

1. Step the Bullet physics simulation with the current frame delta
2. Update the player using the current keyboard and mouse state
3. Try to place a cube in front of the player
4. Iterate through dynamic map rigid bodies and copy Bullet transform origins back into `Cube.Position`

`Player.Update()` currently handles:

- Reading `W`, `A`, `S`, and `D` for horizontal movement
- Reading `Space` for jumping
- Reading `F` for frozen-state toggling
- Applying impulses to the player rigid body
- Clamping horizontal speed
- Updating the camera orientation from mouse delta
- Running collision logging at a timed interval

## Render Loop

`Game.OnRenderFrame()` clears the framebuffer, iterates over `Map.SolidCubes`, and calls `Render.DrawCube()` for each one. After world drawing, it calls `Render.DrawHUD()` and then swaps buffers.

The HUD result now also carries explicit runtime requests, including resolution changes and floor-size rebuild requests, back to `Game`.

`Render.DrawCube()` chooses between two rendering paths:

- If `Cube.Animation` is present, the cube is rendered through `AnimatedModel.Render()`
- Otherwise, the cube is drawn as a transparent cube plus a wireframe overlay

The HUD path currently includes:

- A crosshair built from simple line geometry
- An ImGui `HUD` window for runtime status
- An ImGui `Settings` window that can request camera, resolution, and floor-size changes

## Physics Model

`PhysicsIntegrator` builds a `DiscreteDynamicsWorld` with gravity pointing down the Y axis. It also creates the player rigid body as a `0.5 x 0.5 x 0.5` box shape with CCD settings enabled.

`Map` now separates floor visuals from floor physics:

- the visible floor is still created as solid cubes in the map
- the floor collision surface is registered once as a merged static floor collider
- later non-floor cubes can still receive individual rigid bodies through `AddPhysicsBody()`

When the floor size changes at runtime:

- `PhysicsIntegrator.ResetMapBodies()` removes the old map rigid bodies
- `Game` creates a rebuilt `Map` with the requested square width/depth
- `Map.CopyPlacedCubesTo()` migrates surviving non-floor cubes into the rebuilt map
- `Player.SetWorldPosition()` resets the player transform and clears stale velocities after the rebuild

The player and dynamic map cubes are linked through Bullet rigid bodies. During updates, Bullet is treated as the authoritative source of dynamic physical positions, and those world-space origins are copied back into the rendering-side `Cube` objects.

## Rendering Model

`Render` creates and reuses:

- A cube VAO/VBO for non-model cube rendering
- A shader dictionary keyed by logical names such as `default` and `cube`
- HUD VAO/VBO resources
- ImGui context, texture, and buffer resources

Projection is rebuilt from the live window size and configured field of view. The HUD crosshair is also rebuilt from live viewport data.

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
