# Project Structure

## Top Level

- `dream/`: the main C# project directory
- `dream.Tests/`: xUnit test project for low-harness gameplay and runtime seam coverage
- `docs/`: documentation files for the project
- `Directory.Packages.props`: centrally managed NuGet package versions
- `global.json`: pinned .NET SDK version for consistent local builds
- `.gitignore`: repository ignore rules

## Solution And Project Files

- `dream/dream.sln`: Visual Studio solution file
- `dream/dream.csproj`: .NET project file with package references and build settings
- `dream.Tests/dream.Tests.csproj`: test project for focused regression coverage around gameplay and runtime seams

## Core Source Files

- `dream/Program.cs`: application entry point, explicit startup mode selector, desktop default path, and reserved `--server` seam
- `dream/Game.cs`: OpenTK game window shell, lifecycle delegation, resize callback, and FPS tracking
- `dream/PlayScene.cs`: active gameplay scene and current desktop orchestration root; it still owns player/map/physics access during preflight, composes the current local player body registration, and remains the planned extraction boundary for a future `WorldSessionRuntime` that will take over authoritative simulation ownership while the scene stays client-facing
- `dream/GameSettings.cs`: runtime configuration, current runtime mode, and settings-change events
- `dream/RuntimeState.cs`: compact gameplay-vs-settings runtime state model
- `dream/GameEvents.cs`: targeted configuration/runtime event payloads
- `dream/Player.cs`: gameplay façade for movement, camera-facing state, collision logging, and upward-facing build-use request capture
- `dream/PlayerController.cs`: raw input-to-command translation
- `dream/PlayerMotor.cs`: rigid-body movement, jump, and freeze behavior
- `dream/PlayerCameraRig.cs`: body-to-camera synchronization and mouse-look updates
- `dream/PlayerInteraction.cs`: edge-triggered gameplay interaction capture that turns plain data into `BuildUseRequest`
- `dream/PlayerCommands.cs`: small command records used by the player helpers
- `dream/Map.cs`: world façade that composes grid storage, generation, physics registration, and accepted-placement reporting through `AddCube(...)`
- `dream/WorldGrid.cs`: sparse cube cell storage and bounds checks
- `dream/WorldGenerator.cs`: solid-cube creation and visual asset binding
- `dream/WorldPhysicsBinder.cs`: Bullet rigid-body registration for floor and cube world pieces
- `dream/Cube.cs`: cube data model used by the world
- `dream/PhysicsIntegrator.cs`: Bullet world setup, simulation stepping, explicit player-body registration ownership, body collection ownership, and disposal
- `dream/PhysicsFactory.cs`: factory for gameplay rigid bodies
- `dream/PhysicsSyncSystem.cs`: Bullet-to-world transform synchronization for dynamic cubes
- `dream/Camera.cs`: view direction and mouse-look behavior

## Rendering And Utility Files

- `dream/Render.cs`: OpenGL resource owner and rendering façade
- `dream/WorldRenderer.cs`: scene-facing world draw orchestration
- `dream/DebugRenderer.cs`: scene-facing collision debug draw orchestration
- `dream/HudRenderer.cs`: scene-facing HUD/settings draw orchestration
- `dream/Shader.cs`: shader source loading, OpenGL shader compilation, uniform setters, and uniform-location caching
- `dream/AnimatedModel.cs`: glTF mesh/material/texture loading and model rendering
- `dream/ModelAssets.cs`: shared model asset accessors
- `dream/ResourceCache.cs`: reusable in-memory cache for shared runtime resources
- `dream/HudLayout.cs`: viewport-driven HUD crosshair layout math
- `dream/BulletSharpExtensions.cs`: Bullet-to-OpenTK matrix conversion helper
- `dream/CubeTransforms.cs`: shared centered visual-transform and collision-debug transform helpers
- `dream/VectorConversion.cs`: conversions between OpenTK and BulletSharp vector types
- `dream/MatrixExtensions.cs`: conversion from `System.Numerics.Matrix4x4` to `OpenTK.Matrix4`
- `dream/GLUtils.cs`: OpenGL error helper

## Shader Files

Shader files live under `dream/shaders/`:

- `vertexShader.glsl`
- `fragmentShader.glsl`
- `cubeVertex.glsl`
- `cubeFragment.glsl`
- `hudVertex.glsl`
- `hudFragment.glsl`
- `hudVertexText.glsl`
- `hudFragmentText.glsl`

## Asset Files

Assets currently live under `dream/animations/` and `dream/fonts/`.

### `dream/animations/`

- `GrassTextureCube.gltf`: glTF model used for solid floor cubes
- `GrassTextureCube.bin`: binary data referenced by the glTF asset
- `grass_top.png`
- `Grass_bottom.jpg`
- `grass_block_side_texture.png`
- `me.png`

### `dream/fonts/`

- `Arial.ttf`

## Documentation Files

- `README.md`: quickstart and high-level entry point
- `docs/overview.md`: high-level project summary
- `docs/architecture.md`: subsystem interaction guide
- `docs/project-structure.md`: repository and file guide
- `docs/configuration.md`: framework, packages, build, and runtime assumptions
- `docs/rendering-and-assets.md`: rendering path and asset usage details
