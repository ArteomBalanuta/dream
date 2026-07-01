# Rendering And Assets

## Rendering Overview

The rendering subsystem is centered in `Render.cs`. It initializes shared OpenGL state, loads shader programs, and creates the cube and HUD buffers used by the lower-level draw paths. Frame orchestration now happens in `PlayScene`, which drives focused renderer collaborators during both update and render instead of pushing first-person behavior directly into `Render.cs`.

At construction time, `Render`:

- Enables depth testing and alpha blending
- Loads named shader programs into an internal dictionary
- Creates the cube VAO and VBO used for basic cube rendering
- Initializes HUD geometry
- Initializes ImGui and its supporting OpenGL resources
- Exposes `WorldRenderer`, `DebugRenderer`, `ViewModelRenderer`, and `HudRenderer` as scene-facing collaborators

The current frame order is:

1. world cubes
2. world-space animated enemies
3. local player model when third-person view is enabled
4. world-space remote player models
5. collision/debug overlays
6. first-person viewmodels when first-person view is enabled
7. HUD and ImGui

That ordering keeps enemy actors in the same depth-tested world layer as cubes, keeps the hands and held item visually above the world, and still lets the HUD render last.

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

If `Cube.Animation` is set, rendering is delegated to `AnimatedModel.Render()`. In the current code shape, this shared model-backed path is used broadly by solid cubes that reference the cached `GrassCube` model, including but not limited to the textured floor cubes.

The current floor model is now reused through a shared model asset entry instead of being loaded separately for every floor cube.
The world render loop now iterates the active `Map.SolidCubes` collection rather than scanning every cell in the backing world array each frame.
Those cubes are now also explicitly a compatibility export during the geometry refactor: `Map` keeps authoritative geometry-cell records beside the current cube render path so world storage can evolve before the renderer does.

### Simple cube rendering

If `Cube.Animation` is not set, the renderer:

- Draws a transparent solid cube
- Draws a white wireframe overlay

Both of these use the shared cube VAO and the `default` shader.

## Enemy Rendering Path

The first non-cube world actor uses `EnemyRenderer` rather than trying to squeeze through `Cube.Animation`.

For each active enemy actor, the renderer currently:

- builds an `EnemyRenderState` from world position, facing, and current behavior
- resolves the cached wolf `AnimatedModel`
- applies the correct named clip through `EnemyVisualState`
- advances clip playback
- renders the model in world space with the main camera view and world projection

The animated-model path now composes the scene instance transform with the glTF node world transform in the correct order for this renderer. That matters for authored assets such as the wolf, whose mesh node carries its own non-identity local scale.

This is intentionally the first dedicated world-actor render seam so future creatures do not have to masquerade as cubes just to appear in the scene.

## Remote Player Rendering Path

Remote player actors render through `RemotePlayerRenderer` rather than the cube/world renderer. The renderer consumes `RemotePlayerPresentationState`, builds a `PlayerRenderState`, and draws the Kenney blocky character model through the shared animated-model path.

The first imported player asset is `dream/animations/players/kenney-blocky-character-a.glb`, sourced from Kenney Blocky Characters 2.0 under CC0. The GLB references `players/Textures/texture-a.png`, and the copied license is stored beside the asset as `players/LICENSE-Kenney-Blocky-Characters.txt`.

The current player model contract uses:

- `idle` for stationary remote actors
- `walk` for actors whose authoritative snapshot position changed
- `head` as the named node reserved for head/look overrides
- a body-center-to-foot visual offset so the blocky model is grounded on the player collision body's bottom plane rather than floating at body-center height

For this first pass, replicated facing rotates the body/root model matrix. A head-node override seam is already present in `AnimatedModel.RenderPrepared(...)`, but because the protocol currently carries one look direction rather than separate body and head aim, the default head override remains identity to avoid double-rotating the head. Future aim/pitch replication can apply its local transform through that same node override map.

## Local Third-Person Rendering Path

The local client can now switch between first-person and third-person presentation through `GameSettings.CameraViewMode`. The settings menu exposes a `Third Person View` checkbox, and gameplay input also supports `C` as a quick toggle.

Third person is client presentation only. Movement, combat, collision, and replicated facing continue to use the same local player camera/facing state, while `ThirdPersonCameraRig` derives a follow camera behind and above the player for rendering. When third person is active, `LocalPlayerRenderer` draws the same Kenney blocky player model in world space and the first-person viewmodel pass is skipped. When first person is active, the original first-person viewmodel pass remains the visible local-player presentation.

## First-Person Viewmodels

The first-person pass is owned by `ViewModelRenderer`, not by inline logic in `Render.cs`. `PlayScene` provides the current `ViewModelFrameState`, and the renderer:

- participates in `PlayScene.Update()` by resolving slot models, advancing clip playback, and reporting completed one-shots
- resolves the hands and held-item models from `ViewModelDefinition.ModelPath`
- applies per-slot local position, rotation, and scale offsets
- uses a dedicated first-person projection built from the live viewport aspect
- clears only the depth buffer before drawing so world geometry does not clip the camera-relative pass

`PlayerViewModelController` owns the runtime presentation state that feeds this pass, while `ViewModelCatalog` and `ViewModelAssets` define the built-in ids and default loadout.

### Viewmodel Tuning

The first-person rig now resolves viewmodel poses through `ViewModelPoseResolver`, layering:

1. asset defaults
2. shared rig tuning
3. body-anchor illusion offsets
4. per-slot corrections
5. animation state

`ViewModelRenderer` consumes the resolved pose directly, including a viewmodel-specific FOV. This keeps the projection and transform tuning runtime-adjustable without moving pose composition into `Render.cs`.

## HUD Rendering

The HUD currently includes:

- A red crosshair drawn with line primitives when gameplay is active
- A lightweight ImGui `HUD` window with status text
- An interactive ImGui `Settings` window toggled with `Esc`
- A pooled floating-combat-text overlay rendered in screen space from projected world anchors

The crosshair layout is viewport-driven: its center and line geometry are rebuilt from the active window size instead of from fixed startup-time HUD pixels.

`Render.DrawHUD()` now:

- Disables depth testing so the HUD appears on top
- Uses an orthographic projection based on the current window size
- Draws the crosshair lines only while the settings menu is hidden
- Draws floating combat text through the HUD overlay using camera-projected world anchors
- Keeps the crosshair centered from live viewport metrics and scales it with window size
- Starts a new ImGui frame
- Renders a window at the top-left corner
- Displays the current resolution and settings-menu hint
- Optionally renders a settings panel with world FOV, mouse sensitivity, viewmodel rig controls, and resolution controls
- Renders the ImGui draw data manually
- Re-enables depth testing

The code also tracks whether the game window has regained focus and resets parts of the ImGui input state when focus returns.

Future HUD positioning work should follow the same pattern and compute placement from current viewport data rather than introducing hardcoded viewport pixel coordinates.

## Floating Combat Text

The current first pass of floating combat text is HUD-owned rather than world-canvas owned.

Key properties:

- transient entries are pooled through `FloatingCombatTextSystem`
- the motion model follows the burst / ease-out float / fade lifecycle from `DMG_IMPL.md`
- entries are projected from world anchors into screen-space inside `Render.DrawHUD()`
- per-target active text is capped to avoid unreadable overlap

For the current vertical slice, left click triggers a controlled regular-damage demo spawn in front of the camera so the visual timing can be validated before the broader creature support layer is wired in.

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
- Iterates renderable descendants of the default scene rather than only top-level mesh nodes
- Composes the instance model matrix with each glTF node world matrix through a dedicated helper so authored node-local scale/translation remains stable in world space
- Applies a first-pass skinning palette when a primitive exposes `JOINTS_0` and `WEIGHTS_0`
- Draws each mesh primitive with its material properties and textures

For first-person presentation, `AnimatedModel` now also supports named clip playback with explicit loop and one-shot modes. The current viewmodel milestone expects exact clip names:

- `Idle`: looping baseline presentation
- `Use`: one-shot click or use animation

Viewmodel assets should ship those exact clip names so the controller and renderer can stay data-driven instead of per-asset hardcoded.

The first wolf enemy now uses explicit asset metadata through `WolfAssetDefinition` and `WolfAnimationResolver` rather than assuming the viewmodel clip contract. The imported asset currently maps:

- `AnimalArmature|Idle`
- `AnimalArmature|Walk`
- `AnimalArmature|Attack`

## First-Pass Skinning Support

The current milestone now supports the most important missing creature-asset behavior:

- detection of logical skins during model initialization
- joint-palette upload from SharpGLTF joint world matrices, inverse bind matrices, and the rendered mesh node's world transform
- weighted position and normal blending in `cubeVertex.glsl`

This support is intentionally bounded:

- one logical skin per asset is supported cleanly today
- it is sufficient for the current wolf asset
- broader multi-skin or richer animation-system work can be layered later if future assets require it

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
- An animated wolf asset under `dream/animations/enemies/wolf.glb`
- A Kenney blocky player model under `dream/animations/players/`
- First-person hands and held-item blockout assets under `dream/animations/viewmodels/`
- A curated source library under `dream/animations/library/` with:
  - Quaternius animal and character packs in `library/quaternius/`
  - Kenney `nature-kit` and `animated-characters-retro` packs in `library/kenney/`
  - Poly Haven terrain textures in `library/polyhaven/`
- A font file under `dream/fonts/`

These asset directories are copied into the build output and resolved at runtime relative to the application base directory, as documented in [configuration.md](configuration.md).

For this first milestone, the assets under `dream/animations/viewmodels/` are intentionally blockout placeholders rather than final art. They exist to prove:

- the dedicated first-person pass
- the split between world models and first-person viewmodels
- independent hands and held-item slot rendering
- the shared `Idle` and `Use` clip contract for future replacement art

World models remain part of the main world pass and should keep world-space transforms and occlusion behavior. First-person viewmodels are camera-relative presentation assets and should only be authored for the dedicated viewmodel pass.

## World Geometry Direction

The project now has a first-pass geometry subsystem under `Dream.Simulation.World.Geometry`.

Current behavior:

- `GeometryCellRecord` stores authoritative cell-level geometry facts
- `GeometryCellDefinition` describes shape kind, material, and subdivision resolution
- `GeometrySubdivisionState` keeps sparse child overrides and allocates them only when a mutation touches the cell
- `GeometryMutationService` returns dirty-cell facts for later render/collision rebuild work
- `GeometryRenderExport` now packages nearby geometry cells plus compatibility cubes for the current world render path
- `GeometryCollisionExport` now packages merged-floor and per-cell collision descriptors for the current world physics path
- `Map` now caches per-cell render blocks and collision bodies and rebuilds only dirty geometry cells after a subdivision edit

The current renderer still consumes compatibility `Cube` exports from `Map`, now via `Map.CaptureGeometryRenderExport(...)` and `WorldSessionPresentationState.VisibleWorld`, so this is a storage and mutation seam first, not yet a full chunk-mesh renderer. The first preset non-cube occupancy contract also exists now through wedge and slope sampling rules, which gives the world layer a way to describe non-cubic geometry before the world renderer learns to draw it natively.

The current physics path is now one step further along too: `Map.CaptureGeometryCollisionExport()` still describes one merged floor collider plus coarse intact-cell bodies, but subdivided cells now emit geometry-native child-box bodies and `WorldPhysicsBinder` replaces only the touched cell's collider set. The render path mirrors that shape: subdivided cells now emit geometry-native render blocks while intact solid cells may still use the coarse compatibility-cube path.
The first live fracture preset now uses that same export path. When a destructible placed cube dies in combat, the authoritative map replaces the coarse cell with a deterministic four-piece rubble pattern grounded in the cell corners. Remote snapshots and incremental world mutations now preserve that fractured state instead of collapsing it back into a full cube for connected clients or late joiners.

## World Asset Normalization

Imported world assets do not always arrive in gameplay-ready scale. The current project now handles that through explicit normalization metadata instead of through arbitrary render-only scale tweaking.

Dream world units are cube-relative: `1.0` world unit equals one cube side. Imported glTF world actors should declare authored dimensions and target cube-relative dimensions in their asset definition. The durable policy is recorded in [the world-units and asset-scale decision](ledger/decisions/2026-07-01-world-units-and-asset-scale.md), with follow-up implementation work tracked in [the model-size normalization plan](ledger/plans/2026-07-01-model-size-normalization-and-asset-units.md).

The standing player target is `1.2` cubes tall, which is currently `2x` the wolf target height, and no more than `1.0` cube wide/deep in neutral stance. Renderer code should consume asset definitions for scale and grounding rather than introducing local visual constants.

For the first creature slice, `WolfAssetDefinition` declares:

- the asset's authored height
- the target in-game world height
- the asset's authored forward axis
- the first-pass collision footprint used by enemy movement/debug rendering

The final uniform render scale is derived from those values at runtime through `WorldAssetNormalization.ResolveUniformScale()`. This keeps imported-asset sizing decisions in metadata, where future creatures or props can reuse the same seam without hiding authored-size assumptions inside scene or renderer code.

The current wolf asset is authored facing local `+Z`, so its render yaw maps that axis into the enemy's gameplay movement direction. Future creature assets should declare the same kind of presentation metadata rather than assuming every imported model faces the engine's historical `-Z` direction.

Wolf rendering now uses the actor's real world position directly. Close-range player/enemy separation and world-object blocking are handled in gameplay movement state, not by shifting only the rendered model away from the camera.
