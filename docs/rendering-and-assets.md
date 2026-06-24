# Rendering And Assets

## Rendering Overview

The rendering subsystem is centered in `Render.cs`. It initializes shared OpenGL state, loads shader programs, and creates the cube and HUD buffers used by the lower-level draw paths. Frame orchestration now happens in `PlayScene.Render()`, which drives focused renderer collaborators instead of pushing first-person behavior directly into `Render.cs`.

At construction time, `Render`:

- Enables depth testing and alpha blending
- Loads named shader programs into an internal dictionary
- Creates the cube VAO and VBO used for basic cube rendering
- Initializes HUD geometry
- Initializes ImGui and its supporting OpenGL resources
- Exposes `WorldRenderer`, `DebugRenderer`, `ViewModelRenderer`, and `HudRenderer` as scene-facing collaborators

The current frame order is:

1. world cubes
2. collision/debug overlays
3. first-person viewmodels
4. HUD and ImGui

That ordering keeps the hands and held item visually above the world while still letting the HUD render last.

## Shader Usage

The current shader dictionary is populated with:

- `default`: built from `vertexShader.glsl` and `fragmentShader.glsl`
- `cube`: built from `cubeVertex.glsl` and `cubeFragment.glsl`

Separate shader objects are also created for:

- HUD line rendering through `hudVertex.glsl` and `hudFragment.glsl`
- ImGui text rendering through `hudVertexText.glsl` and `hudFragmentText.glsl`

Shader compilation and linking are handled by `Shader.cs`, which reads vertex and fragment sources from disk and creates an OpenGL program handle.
Runtime shader file paths are provided through `AssetPaths.cs`.
`Shader.cs` now caches uniform locations after first lookup so repeated render-path uniform updates do not keep calling `GL.GetUniformLocation`.

## Cube Rendering Paths

`Render.DrawCube()` builds a model matrix from:

- The cube position
- The cube side length
- A translation offset of `(-0.5, -0.5, -0.5)` to align the geometry

It then chooses one of two paths:

### Model-backed cube rendering

If `Cube.Animation` is set, rendering is delegated to `AnimatedModel.Render()`. This is the path used for the textured floor cubes created by `Map.AddCube()`.

The current floor model is now reused through a shared model asset entry instead of being loaded separately for every floor cube.
The world render loop now iterates the active `Map.SolidCubes` collection rather than scanning every cell in the backing world array each frame.

### Simple cube rendering

If `Cube.Animation` is not set, the renderer:

- Draws a transparent solid cube
- Draws a white wireframe overlay

Both of these use the shared cube VAO and the `default` shader.

## First-Person Viewmodels

The first-person pass is owned by `ViewModelRenderer`, not by inline logic in `Render.cs`. `PlayScene` provides the current `ViewModelFrameState`, and the renderer:

- resolves the hands and held-item models from `ViewModelDefinition.ModelPath`
- applies per-slot local position, rotation, and scale offsets
- uses a dedicated first-person projection built from the live viewport aspect
- clears only the depth buffer before drawing so world geometry does not clip the camera-relative pass

`PlayerViewModelController` owns the runtime presentation state that feeds this pass, while `ViewModelCatalog` and `ViewModelAssets` define the built-in ids and default loadout.

## HUD Rendering

The HUD currently includes:

- A red crosshair drawn with line primitives when gameplay is active
- A lightweight ImGui `HUD` window with status text
- An interactive ImGui `Settings` window toggled with `Esc`

The crosshair layout is viewport-driven: its center and line geometry are rebuilt from the active window size instead of from fixed startup-time HUD pixels.

`Render.DrawHUD()` now:

- Disables depth testing so the HUD appears on top
- Uses an orthographic projection based on the current window size
- Draws the crosshair lines only while the settings menu is hidden
- Keeps the crosshair centered from live viewport metrics and scales it with window size
- Starts a new ImGui frame
- Renders a window at the top-left corner
- Displays the current resolution and settings-menu hint
- Optionally renders a settings panel with FOV, mouse sensitivity, and resolution controls
- Renders the ImGui draw data manually
- Re-enables depth testing

The code also tracks whether the game window has regained focus and resets parts of the ImGui input state when focus returns.

Future HUD positioning work should follow the same pattern and compute placement from current viewport data rather than introducing hardcoded viewport pixel coordinates.

## Projection And Camera Feel

The renderer no longer uses a fixed square aspect ratio. `Render` rebuilds its perspective projection from the live window width, height, and configured field of view so the scene scale stays aligned with the actual viewport.

On the player side, the camera now follows a configurable eye-height offset instead of an embedded diagonal offset, which avoids the previous close-up framing problem and gives the settings model a single place to own camera feel defaults.

## glTF Model Loading And Clips

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

For first-person presentation, `AnimatedModel` now also supports named clip playback with explicit loop and one-shot modes. The current viewmodel milestone expects exact clip names:

- `Idle`: looping baseline presentation
- `Use`: one-shot click or use animation

Viewmodel assets should ship those exact clip names so the controller and renderer can stay data-driven instead of per-asset hardcoded.

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
- First-person hands and held-item blockout assets under `dream/animations/viewmodels/`
- A font file under `dream/fonts/`

These asset directories are copied into the build output and resolved at runtime relative to the application base directory, as documented in [configuration.md](configuration.md).

For this first milestone, the assets under `dream/animations/viewmodels/` are intentionally blockout placeholders rather than final art. They exist to prove:

- the dedicated first-person pass
- the split between world models and first-person viewmodels
- independent hands and held-item slot rendering
- the shared `Idle` and `Use` clip contract for future replacement art

World models remain part of the main world pass and should keep world-space transforms and occlusion behavior. First-person viewmodels are camera-relative presentation assets and should only be authored for the dedicated viewmodel pass.
