# Rendering And Assets

## Rendering Overview

The rendering subsystem is centered in `Render.cs`. It initializes OpenGL state, loads shader programs, creates cube and HUD buffers, and submits both world geometry and HUD geometry every frame.

At construction time, `Render`:

- Enables depth testing and alpha blending
- Loads named shader programs into an internal dictionary
- Creates the cube VAO and VBO used for basic cube rendering
- Initializes HUD geometry
- Initializes ImGui and its supporting OpenGL resources

## Shader Usage

The current shader dictionary is populated with:

- `default`: built from `vertexShader.glsl` and `fragmentShader.glsl`
- `cube`: built from `cubeVertex.glsl` and `cubeFragment.glsl`

Separate shader objects are also created for:

- HUD line rendering through `hudVertex.glsl` and `hudFragment.glsl`
- ImGui text rendering through `hudVertexText.glsl` and `hudFragmentText.glsl`

Shader compilation and linking are handled by `Shader.cs`, which reads vertex and fragment sources from disk and creates an OpenGL program handle.
Runtime shader file paths are provided through `AssetPaths.cs`.

## Cube Rendering Paths

`Render.DrawCube()` builds a model matrix from:

- The cube position
- The cube side length
- A translation offset of `(-0.5, -0.5, -0.5)` to align the geometry

It then chooses one of two paths:

### Model-backed cube rendering

If `Cube.Animation` is set, rendering is delegated to `AnimatedModel.Render()`. This is the path used for the textured floor cubes created by `Map.AddCube()`.

The current floor model is now reused through a shared model asset entry instead of being loaded separately for every floor cube.

### Simple cube rendering

If `Cube.Animation` is not set, the renderer:

- Draws a transparent solid cube
- Draws a white wireframe overlay

Both of these use the shared cube VAO and the `default` shader.

## HUD Rendering

The HUD currently includes two pieces:

- A red crosshair drawn with line primitives
- A non-interactive ImGui window labeled `HUD`

`Render.DrawHUD()`:

- Disables depth testing so the HUD appears on top
- Uses an orthographic projection from `(0,0)` to `(800,600)`
- Draws the crosshair lines
- Starts a new ImGui frame
- Renders a window at the top-left corner
- Displays the text `Score: 100`
- Renders the ImGui draw data manually
- Re-enables depth testing

The code also tracks whether the game window has regained focus and resets parts of the ImGui input state when focus returns.

## glTF Model Loading

`AnimatedModel.cs` uses `SharpGLTF.Schema2.ModelRoot.Load()` to load a glTF asset from disk. During initialization, it:

- Reads logical materials
- Extracts material factors such as base color, metallic, roughness, and emissive values
- Loads referenced textures from glTF image content
- Builds VAO, VBO, and EBO resources for mesh primitives
- Reads animations into an internal dictionary

When rendering, it:

- Enables depth testing and back-face culling
- Applies the caller-supplied model, view, and projection transforms
- Iterates the default scene's visual children
- Draws each mesh primitive with its material properties and textures

## Texture Handling

Texture loading currently happens inside `AnimatedModel.LoadTexture()`. The implementation:

- Reads image content from the glTF texture's primary image
- Loads the image with `ImageSharp`
- Flips the image vertically
- Uploads it to OpenGL as a 2D texture
- Generates mipmaps

Texture handles are stored for disposal later.

## Asset Locations

The repository currently includes:

- World and HUD shaders under `dream/shaders/`
- A cube model and related textures under `dream/animations/`
- A font file under `dream/fonts/`

These asset directories are copied into the build output and resolved at runtime relative to the application base directory, as documented in [configuration.md](configuration.md).
