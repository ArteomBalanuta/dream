# Project Structure

## Top Level

- `dream/`: the main C# project directory
- `docs/`: documentation files for the project
- `.gitignore`: repository ignore rules

## Solution And Project Files

- `dream/dream.sln`: Visual Studio solution file
- `dream/dream.csproj`: .NET project file with package references and build settings

## Core Source Files

- `dream/Program.cs`: application entry point
- `dream/Game.cs`: OpenTK game window, update loop, render loop, resize handling, and FPS tracking
- `dream/Player.cs`: input-driven movement, jump logic, camera coordination, collision logging, and cube placement
- `dream/Map.cs`: 3D cube grid initialization, floor generation, and cube creation
- `dream/Cube.cs`: cube data model used by the world
- `dream/PhysicsIntegrator.cs`: Bullet world setup, player body creation, simulation stepping, and disposal
- `dream/Camera.cs`: view direction and mouse-look behavior

## Rendering And Utility Files

- `dream/Render.cs`: OpenGL state setup, cube rendering, HUD drawing, and ImGui submission
- `dream/Shader.cs`: shader source loading, OpenGL shader compilation, and uniform setters
- `dream/AnimatedModel.cs`: glTF mesh/material/texture loading and model rendering
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
