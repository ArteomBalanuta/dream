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

Startup terrain no longer relies only on coarse full cubes. `WorldGenerator` can seed authored subdivision into selected geometry cells, and `Map.CaptureGeometryRenderExport(...)` emits those cells as geometry-native render blocks. The first authored shapes are a stone mini-cube pyramid, a rounded dirt boulder, and low filler rubble clusters in the expanded startup world. These authored sculpture cells are generated from the global `WorldMicroGrid`: one world cube maps to `16 x 16 x 16` absolute microcells, and those coordinates are projected back into the current cell/subcell storage bridge. Render export now keeps those authored microcells canonical but caches a global greedy merge for static startup sculptures, so contiguous runs become cuboid render descriptors that can cross parent-cell boundaries. Collision and debug wire boxes still come from canonical micro-grid occupancy rather than from render-only visual fill.

Static startup sculpture collision is intentionally coarser than render geometry for responsive bootstrap: render keeps high-detail global micro-grid descriptors, while static collision uses one AABB per occupied parent cell. The physics binder keeps that merged static collision group separate from dynamic/fractured per-cell bodies, so startup collision is cheaper while runtime edits still use the existing cell-level synchronization path.

The renderer must batch this geometry before reaching OpenGL. Compatibility cubes and geometry-native render blocks are grouped by model and shader, then model-backed groups are drawn through instanced `AnimatedModel` rendering. This prevents authored subcell landmarks from becoming tens of thousands of individual model render calls per frame.

The instanced cube/block paths reuse matrix buffers for the per-instance model transforms, and `AnimatedModel` uploads the active matrix range directly to the instance buffer. This avoids the previous per-batch matrix-array allocation and the extra `ToArray()` copy during `GL.BufferData(...)`.

Static world export rendering now has a first hidden-face culling path. `WorldRenderer` routes `GeometryRenderExport` through `StaticWorldChunkRenderer`, which normalizes compatibility cubes and geometry render blocks into render-only voxel cells, groups them by chunk and material role, emits only exposed faces, uploads chunk meshes, and draws those meshes directly through OpenGL. This path is render-only: authoritative occupancy and collision still come from the simulation geometry and physics exports, not from the generated mesh. Chunk visibility is bounds-aware instead of center-point-only, so moving through or beside a large chunk does not make the whole chunk disappear just because its center falls behind the camera. The chunk renderer caches GL buffers by chunk/material/role signature, so camera movement at the visible-world boundary does not force a full static-world mesh rebuild. The remaining exterior chunk faces are drawn double-sided, because hidden-face culling already removes interior surfaces and first-person/edge cameras should not reveal collision-only transparent blocks.

The static chunk renderer binds terrain textures by material role, not only by broad `GeometryMaterialId`. Grass chunk meshes are split into top, side, and bottom mesh batches so they can bind `grass_top.png`, `grass_block_side_texture.png`, and `Grass_bottom.jpg` respectively, matching the old multi-material glTF cube contract. Dirt, stone, sand, mud, snow, rock, and grass-path materials bind one Poly Haven diffuse texture each through the static terrain material catalog. Intact compatibility cubes must keep the material id from their matching `GeometryRenderCell`, because the authoritative map/server matrix owns the terrain material even when the renderer still receives a coarse `Cube` view.

Live compatibility cubes now also carry their authoritative `GeometryMaterialId` directly on `Cube.MaterialId`. If a live or dynamic compatibility cube does not use one of the explicit glTF-backed cube models, the renderer falls back to a terrain-textured cube draw path resolved from that material id instead of drawing a transparent placeholder or inventing a grass default. Unknown material ids still render visibly through an explicit magenta fallback so parity gaps are obvious during testing.

Generated chunk UVs are face-direction aware. Top and bottom faces tile across `X/Z`, while vertical faces tile horizontally across `X` or `Z` and vertically across `Y`, so long merged cuboids do not stretch or rotate terrain edge textures incorrectly.

For the broader optimization inventory and guardrails around this path, see [Performance And Optimizations](performance-and-optimizations.md).

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

Remote enemy replication preserves existing `EnemyActor` instances when authoritative snapshots arrive. This is important for animation continuity: recreating the actor every snapshot also recreates `EnemyVisualState`, which repeatedly restarts walk/attack clip playback before the animation can visibly advance.

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
- Optional frame performance counters for render export, physics, visible/exported world descriptors, render batches, and collision-body count

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
- Displays performance counters when `Show Performance Counters` is enabled in the settings panel
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
  - Quaternius `animals` and `characters` source packs in `library/quaternius/`
  - Quaternius `fantasy-props-megakit` for medieval props, tools, weapons, vegetables, furniture, and stalls
  - Quaternius `stylized-nature-megakit` for stylized trees, plants, flowers, grass, and rock sets
  - Quaternius `survival-pack` for utility items, camp gear, tools, firearms, and general-purpose props
  - Kenney `animated-characters-retro`, `food-kit`, and `nature-kit` packs in `library/kenney/`
  - Poly Haven terrain textures in `library/polyhaven/`
  - OpenGameArt `skyboxes/cloudy-skyboxes` with CC0 cubemap and panorama sky backgrounds
- A font file under `dream/fonts/`

The current source library is intentionally broader than the live runtime set. The repo now has reusable inventory for:

- character and creature reference assets
- food, fruit, and kitchen props
- fantasy weapons, tools, market props, and furniture
- stylized trees, foliage, flowers, and rock formations
- survival tools, camp objects, and general-purpose item meshes
- cubemap and panorama skies for sunny, sunset, cloudy, and night background work
- terrain textures for the world material pipeline

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

The current desktop world renderer consumes `Map.CaptureGeometryRenderExport(...)` through `WorldSessionPresentationState.VisibleWorld` and now routes the static export through chunk meshes with hidden faces removed. The compatibility `Cube` and geometry-block descriptors still exist as the export contract and as fallback/dynamic building blocks, while the static presentation path can evolve independently from simulation storage. The first preset non-cube occupancy contract also exists now through wedge and slope sampling rules, which gives the world layer a way to describe non-cubic geometry before every shape has a custom renderer.

The current physics path is now one step further along too: `Map.CaptureGeometryCollisionExport()` still describes one merged floor collider plus coarse intact-cell bodies, but subdivided cells now emit geometry-native collision cuboids and `WorldPhysicsBinder` replaces only the touched cell's collider set. Solid subcell runs are greedily merged before export, so the current startup sculptures keep collision derived from canonical micro-grid occupancy instead of one body per rendered mini-cube. The render path mirrors that shape at the API boundary: subdivided cells emit geometry-native render blocks, and static startup sculpture cells are cached as globally merged cuboid descriptors before `WorldRenderer` batches visible model-backed blocks. Render-only `VisualFillScale` values must not be fed back into collision descriptors.
Static chunk mesh rendering now sits on top of that same export seam. The generated chunk meshes are presentation artifacts that can be rebuilt from `GeometryRenderExport`; they must not become canonical world state and must not drive collision or multiplayer snapshots.
The first live fracture preset now uses that same export path. When a destructible placed cube dies in combat, the authoritative map replaces the coarse cell with a deterministic four-piece rubble pattern grounded in the cell corners. Remote snapshots and incremental world mutations now preserve that fractured state instead of collapsing it back into a full cube for connected clients or late joiners.

The startup world now uses multiple textured terrain variants for intact geometry cells:

- `GrassTextureCube.gltf` for meadow cells
- `DirtTextureCube.gltf`, which reuses the cube mesh layout with `library/polyhaven/park_dirt/park_dirt.diffuse.jpg`
- `StoneTextureCube.gltf`, which reuses the cube mesh layout with `library/polyhaven/ground_grey/ground_grey.diffuse.jpg`
- static chunk terrain textures for `sand_01`, `brown_mud`, `snow_04`, `rocks_ground_08`, and `grass_path_3`

`WorldGenerator` selects between those materials deterministically for the initial world layout. The static chunk path resolves the matching texture through `GeometryMaterialId`; the older compatibility model path still has explicit glTF cube variants only for grass, dirt, and stone.

Imported terrain materials that do not have explicit compatibility glTF cube variants now render through the live terrain-material fallback path when they appear in dynamic compatibility cubes. This keeps dynamic/live world visuals aligned with the authoritative material matrix without requiring a separate glTF cube asset per terrain texture.

The fallback path (`Render.DrawTerrainCubeFallback`) draws a `StaticMeshBuffer` built by `TerrainFallbackCubeMesh`: a unit cube from `StaticWorldChunkMeshBuilder` with positions, normals and 0–1 UVs on all six faces, wound counter-clockwise for back-face culling. Until 2026-10-27 it drew the position-only debug cube VAO. The `cube` shader then read disabled normal and UV attributes, so fallback cubes and rubble rendered flat and untextured, and three faces were culled away because of inconsistent winding. The position-only VAO remains for debug, collision and wireframe draws. `StaticMeshBuffer` exposes its attribute locations, which a test checks against `shaders/cubeVertex.glsl`.

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
