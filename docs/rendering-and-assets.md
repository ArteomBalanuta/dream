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
5. the sky (only the pixels nothing covered; see Sky and time of day)
6. the effects pass (particles and light halos; see Effects)
7. collision/debug overlays
8. first-person viewmodels when first-person view is enabled
9. HUD and ImGui

That ordering keeps enemy actors in the same depth-tested world layer as cubes, keeps the hands and held item visually above the world, and still lets the HUD render last.

## Shader Usage

The current shader dictionary is populated with:

- `default`: built from `vertexShader.glsl` and `fragmentShader.glsl`
- `cube`: built from `cubeVertex.glsl` and `cubeFragment.glsl`, with an alpha MASK variant (`Render.LoadWorldShader`)
- `enemy`: built from `cubeVertex.glsl` and `enemyFragment.glsl`, with an alpha MASK variant (`Render.InitializeShaders` / `Render.LoadWorldShader` in `Render.Shaders.cs`)

Separate shader objects are also created for:

- HUD line rendering through `hudVertex.glsl` and `hudFragment.glsl`
- ImGui text rendering through `hudVertexText.glsl` and `hudFragmentText.glsl`

Shader compilation and linking are handled by `Shader.cs`, which reads vertex and fragment sources from disk and creates an OpenGL program handle.
Runtime shader file paths are provided through `AssetPaths.cs`.
`Shader.cs` now caches uniform locations after first lookup so repeated render-path uniform updates do not keep calling `GL.GetUniformLocation`.

### Frame lighting

- **Include mechanism.** `Shader.ResolveIncludes` replaces each line `#include "file.glsl"` with that file from the shader's own folder. It runs on both stages before the MASK define is inserted. It goes one level only: a nested `#include`, a `#version` in an included file, a path outside the folder, or a missing file throws.
- **One lighting model.** `cubeFragment.glsl` and `enemyFragment.glsl` both include `shaders/lighting.glsl`. That file declares the std140 uniform block `FrameLighting` and the helpers `dreamAmbient` (hemisphere ambient), `dreamDirect` (sun plus moon), `dreamExpose` and `dreamApplyFog` (exp2-squared fog starting at `CameraPosition.w`). The hard-coded point lights are gone.
  - Terrain, cubes, models and the view model use `(ambient + direct) * base`.
  - Actors (the `enemy` program) use `ambient × Params.z + direct × 0.75`, floored at `Params.y × base`. `Params.z`, the actor ambient scale, comes from the sky: 3.25 by day (today's 0.65 actor ambient at noon), 1.5 at night, and values in between at twilight, so wolves and players do not look self-lit at night (about 1.2× terrain on up faces at midnight).
- **The C# side.** `Rendering/Lighting/FrameLighting.cs` mirrors the block member for member: 144 bytes, binding point 0. GL 3.3 has no `layout(binding)` on a block, so after link `Shader` calls `FrameLightingBuffer.BindProgramBlock`. That runs `GL.UniformBlockBinding` for every program that declares the block.
- **Uploading.** `FrameLightingBuffer` owns one uniform buffer with two ranges:
  - the world range, at offset 0;
  - the view-model range, at 144 rounded up to `GL_UNIFORM_BUFFER_OFFSET_ALIGNMENT`. It holds eye-space sun and moon directions, the camera at the origin, and ambient floored at 0.05 (`FrameLightingBuffer.ViewModelAmbientFloor`). The view model draws with view = identity.
  - `WorldRenderer.DrawWorld(WorldSessionPresentationState)` calls `Render.UploadFrameLighting(camera)` once per frame. It writes both ranges with `BufferSubData` of a stack struct (0 B allocated) and binds the world range. `ViewModelRenderer.Draw` binds the view-model range and restores the world range afterwards.
  - The scene picks the lighting with `Render.SetFrameLighting`. Until the first call, `Render` holds `FrameLighting.ReferenceNoon`, which reproduces the old cube point light at spawn with fog density 0. In play, `PlayScene` sets the `SkyModel` lighting every frame, so fog is always on (see Sky and time of day).
  - `FrameLightingContractTests` pins the layout, the GLSL member order, the include rules and the noon match against the old formulas.

### Point lights and emissive (wave T2)

- **Contract.** `Rendering/Lighting/PointLight.cs` is one world-space light: position, radius (where it reaches 0) and linear colour already multiplied by intensity. `shaders/pointLights.glsl` declares the std140 block `LightList` (528 B, binding point 1; `FrameLighting` is 0): `ivec4 uPointLightCount` and two `vec4[16]` arrays, positions with radius in `w` and colours. `Rendering/Lighting/LightList.cs` mirrors it (`MaxLights` = 16). It is included after `lighting.glsl`.
- **Falloff.** `dreamPointLights(n, fragPos)` uses a windowed falloff, `f = clamp(1 - d²/r², 0, 1)²`, times `0.25 + 0.75 × lambert`. The 0.25 wrap keeps faces turned away from the light readable.
- **Shading.** Terrain, cubes and models use `(ambient + direct + pointLights) * base`. Actors (`enemy`) add the point lights to the direct term, so they get the same `0.75` actor direct scale.
- **Per frame.** The scene fills `Render.Lights` (`LightCollector`, capacity 1,024, never allocates) through `PlayScene.CollectFrameLights`: held lights first (`PlayScene.HeldLights.cs`), so a world full of lamps can never take their slots, then lamp lights (`PlayScene.LampLights.cs`). `LightSelection.SelectNearest` culls them against the camera frustum (by each light's sphere) and keeps the 16 nearest by squared distance; ties keep input order, so lights do not flicker. `LightListBuffer` uploads a world range and a view-model range (positions in eye space, like `FrameLightingBuffer`). That is one more uniform upload per frame and no extra draw call. With no lights the image is identical to T1.
- **Debug ring.** `DREAM_DEBUG_POINT_LIGHTS` (client, default 0, clamped to 0–64) adds a deterministic ring of synthetic lights around the camera for the perf harness (`docs/configuration.md`).
- **Emissive.** `struct Material` in `cubeFragment.glsl` and `enemyFragment.glsl` has `vec3 emissiveFactor`. The final colour is `dreamApplyFog(dreamExpose(lit) + material.emissiveFactor, fragPos)`: emissive is added after exposure and before fog, so an emissive surface glows at night and still fades into fog. `AnimatedModel` sets it from the glTF material. Static terrain sets it from `StaticTerrainMaterial.Emissive` (default 0, so every existing material and the noon match are unchanged). Both go through `Shader.SetEmissiveFactor`, which caches the value per program like the alpha cutoff. Binding one non-emissive material after another therefore uploads nothing, and only a switch to or from an emissive material (a lamp) costs an upload.
- **Torch.** `tool.torch` ("Torch", view model `held.tool.torch`) is a starter item, appended last so the hammer stays equipped. Players whose backpack predates it get it through a one-time top-up of missing starter items into the lowest free backpack slot; a full backpack is skipped and the equipped slot never changes. An equipped torch casts a held light (`HeldLightKind.Torch`, `HeldLightCatalog`): colour (1.0, 0.62, 0.32) × 1.6, radius 8. The existing `tool.survey-lantern` casts a weaker `Lantern` light: (1.0, 0.80, 0.55) × 1.1, radius 6. The light sits in front of the camera in first person and beside the body in third person and on remote players (`HeldLightAnchor`). Remote players' held lights arrive over protocol v6 (`heldLight`, `docs/architecture.md`).
- **Lamp.** `GeometryMaterialId.Lamp` (8) is a placeable, untextured block: base colour (1.0, 0.85, 0.6), emissive (0.9, 0.75, 0.5). Every lamp cell, static or dynamic, gives one point light: (1.0, 0.78, 0.5) × 1.8, radius 10 (`LampLightIndex`). An intact lamp lights from its centre; a partly mined (subdivided) lamp from the centroid of its remaining solid subcells, and a fully mined one gives none. Static lamps come from the export's static render cells (`GeometryRenderExport.StaticCells`) and are rescanned only when `StaticContentVersion` changes. Only lamps within `LampLightIndex.CollectDistance` of the camera are collected: 58 m, the farthest drawn geometry (`WorldVisibility.MaxRenderDistance`, 48 m) plus the lamp radius, so a farther lamp could not light anything on screen. Lamps are persisted like any other cell material.
- **Build material panel.** In Building mode the HUD shows a "Build" window with Auto, Stone, Dirt and Lamp. The choice is sent as a `build.material` action and kept per session on the server (not persisted, not checksummed). Auto uses the supporting cell's material, as before, but never yields Lamp: on top of a lamp it places Stone.

### Sky and time of day

- **`SkyModel`** (`Rendering/Sky/SkyModel.cs`) maps a time of day in [0, 1) to a `SkyState`: the frame lighting, zenith and horizon colours, sun and moon directions and disc colours, and star visibility. It is pure (no GL) and allocates nothing per call.
- **Keyframes.** `SkyModelKeyframes` holds the authored day: night (00:00, 04:00, 20:00), blue hour (05:00), sunrise (06:00), morning (07:30), day (10:00–14:00, `SkyModelKeyframes.Noon`), afternoon (16:30), sunset (18:00) and dusk (19:00). `SkyModel` blends neighbouring keyframes with a smoothstep. Every value blends, including the actor floor (`Params.y`) and the actor ambient scale (`Params.z`). Until wave T2 noon matched `ReferenceNoon` bit for bit; since T2 `ReferenceNoon` is the frozen legacy look (still used before the first `SetFrameLighting` and by the shader-equivalence test), and the noon keyframe is retuned for the higher sun: up-facing light at noon 0.85–0.95, and a wall facing −Z no brighter than the ground.
- **Sun path.** At 12:00 the sun is about 60° above the horizon, on `ReferenceNoon`'s azimuth (the T1 noon sun was much lower). It still moves on the great circle through the east and west horizon points, rising at 06:00 and setting at 18:00. The moon is always opposite the sun. Direct light fades in over a small elevation band around the horizon.
- **Night brightness.** `DREAM_NIGHT_BRIGHTNESS` (`SkyModelSettings`, default 0.07 since wave T2, was 0.15; clamped to [0, 1]) sets the up-facing light at midnight as a fraction of noon's. It scales the night keyframes' ambient, moonlight and actor floor. Twilight and day keyframes keep their authored values. Midnight terrain is 5–8% of noon, and a torch at 2 m gives at least 40% of noon's up-facing light, so torches and lamps carry the night.
- **Fog.** Each keyframe has its own exp2 fog density and start distance. Fog is at least 90% at 48 units (the render distance) and at most 10% at 16 units at every time of day.
- **Clear colour = horizon = fog.** `PlayScene.Render` evaluates the sky once per frame, before the clear. It passes the lighting to `Render.SetFrameLighting` and clears to `SkyState.HorizonColor`, which equals `FogColor.rgb`. A pixel that nothing covers therefore already matches the fogged distance.
- **The sky pass.** `SkyRenderer` (`skyVertex.glsl` and `skyFragment.glsl`) draws one fullscreen triangle at depth 1 with `DepthFunc(Lequal)` and depth writes off. It shades only the pixels that nothing covered: a zenith-to-horizon gradient, sun and moon discs, a sun glow and hashed stars. Then it restores the world state. `PlayScene` creates it in its constructor, right after `Render`, and disposes it in its own dispose step.
- **Frame order.** The sky (step 5 of the frame order in Rendering Overview) draws after the remote players and before the effects pass, the collision and debug overlays and the view model, inside the `Actors` GPU timer section.
- **Debug override.** The Settings window has a "Time Of Day (debug, this client only)" section. Its checkbox and `Hour` slider (`Render.TimeOfDayOverride`) replace the replicated time of day on this client only. Nothing is sent to the server. The slider stops at 23:59, because 24:00 wraps to midnight.
- **Time source.** Without the override, the time comes from the replicated world clock (`ClientWorldClock`), read at the remote-player render tick.
- `SkyModelTests`, `SkyPassContractTests` and `WaveT1DayCycleLightingTests` pin the noon match, the night floor, the fog bounds, the continuity over the whole day, the pass order and state, and the midnight actor-to-terrain ratio.

### Effects (LDR particles and halos)

- **Scope.** A client-only pass in LDR on the back buffer: hit sparks, death bursts, torch embers, ambient dust and additive halos around lamps, torches and lanterns. No HDR target, bloom, tone mapping or soft particles. Nothing in the server, the protocol or the simulation changes, and the jitter comes from a private xorshift (`FxRandom`), never from simulation randomness.
- **Frame order.** `PlayScene.DrawEffectsPass` runs right after the sky (step 6 of the frame order in Rendering Overview), in its own `Effects` GPU timer section. A timer section cannot reopen within a frame, so the debug overlays and the view model that follow are untimed. The `GPU:` line reads `actors X ms, fx Y ms, hud Z ms`.
- **Simulation.** `FxSimulation` (`Rendering/Effects`) holds up to `FxLimits.ParticleCapacity` (1,888) particles in a swap-remove pool and at most `FxLimits.HaloBudget` (128) halos. `Advance` clamps the frame time to 0.1 s and rebuilds the halos from `Render.Lights`, classifying a light by its radius (`LampLightIndex.LampRadius`, `HeldLightCatalog.TorchRadius`, `LanternRadius`); the tint is the light's colour, so the torch flicker carries through. Halos within 1.5 m of the camera (the torch in hand) or beyond 30 m of the view centre are skipped (the last 5 m before the cull fade out), and their strength is 0.12 at noon up to 1 at night (`SkyState.StarVisibility`). Held torches shed embers. About 96 dust motes wrap around `FxSimulation.ViewCentre`: the point along the view direction at least 6 m out, or as far as the player when the player is farther. With the isometric camera that is the player's surroundings, not the air by the lens. The dust is additive (alpha 0). A full pool drops the spawn and counts it in `DroppedSpawns`.
- **Flash lights.** `FxFlashList` (4 slots) turns each hit and kill into a short point light at the actor's anchor (Hit, Critical, Incoming, Death; radii 3.5, 4.5, 3.0 and 5.5, lives 0.11 to 0.40 s, intensity `peak * (1 - t/life)^2`, scaled from 0.3 by day to 1 at night). `PlayScene.CollectEffectLights` appends them after the held lights and lamps, and only into the slots `LightList.MaxLights` (16) still has free, strongest first, so a flash never displaces a collected light. No flash radius equals the lamp, torch or lantern radius, so a flash is never drawn as a halo. A trigger within 1.5 m of a live flash restarts it only if it is at least as strong as what is left and the flash is 0.1 s old, except a death, which always restarts; a full list replaces its weakest flash for an at least as strong trigger.
- **Contact shadows.** `FxShadowList` (at most `FxLimits.ShadowBudget`, 32) lays a soft black blob (`FxFrame.Blob`, ground-aligned, 3 cm above the ground) under the local player, the living remote players and the active enemies; it grows and fades with height above the ground up to 2.5 m, and is skipped beyond 30 m of the view centre or within 1.6 m of the camera. Players find the ground with `FxGroundProbe.TryFindGroundY` (the first geometry cell up to 8 below the feet; none means no shadow); enemies use their pose height. The blobs are written first, ahead of the particles, so a later additive flame is not dimmed by one.
- **Events.** `FxEventBridge` turns `DamageAppliedEvent` (Amount above 0) into sparks and `ActorDiedEvent` into a burst. Client combatants have no hub, so `ActorDiedEvent` never fired on a client; `ClientSnapshotDamageApplier` now publishes it from the wire's `ActorDied` flag, once per actor per snapshot (the server writes two deltas per death).
- **Drawing.** `FxRenderer` draws every instance in one `DrawArraysInstanced` of a four-vertex strip with no mesh VBO. `FxInstance` is 36 bytes (position and half size, premultiplied RGBA bytes, rotation, atlas frame, depth bias, stretch) uploaded through `FxInstanceBuffer` (orphan, then sub-data, fixed capacity 2,048). The quads face the camera through right, up and back vectors read from the view matrix being drawn with (`FxViewBasis`), never from `uCameraPosition`, which is the fog origin and not the eye in the isometric view. State: depth test on, depth writes off, blend `(One, OneMinusSrcAlpha)`, culling off; all restored afterwards. Alpha 0 on an instance adds light; alpha above 0 also darkens what is behind it (ash, blobs). A halo has a per-instance depth bias toward the camera so a lamp's own cube does not cut it.
- **Fog.** `lighting.glsl` splits the fog into `dreamFogTransmittance` and `dreamApplyFog`, whose result is unchanged. `fxFragment.glsl` multiplies the whole premultiplied colour, alpha included, by the transmittance, so a distant particle fades into the fog colour already on screen.
- **Atlas.** `ui/particles/particles.png`, RGBA8, 512 x 256, 8 columns by 4 rows of 64 px cells, frame 0 top-left: sparks 0-3, embers 4-7, dust 8-11, ash 12-15, halo 16, tight halo 17, flame 18-21, contact blob 22, the rest reserved. Loaded unflipped with two mip levels; the vertex shader flips V. The atlas is not required to start: if it is missing or corrupt, or an effects shader fails to compile, `PlayScene` logs one `effects` line through `RenderFaultLog` and runs without the renderer, simulation, flashes, shadows and bridge (`DrawEffectsPass` returns early).
- **Emissive textures.** The world fragment shaders can multiply `material.emissiveFactor` by an emissive texture (`material.hasEmissiveTexture`, sampled only when set), optionally scrolling with `material.emissiveUvScroll` and `uTime`; terrain resets the flag. No shipped model has an emissive texture yet, so that path is verified by contract tests only.

## Cube Rendering Paths

`Render.DrawCube()` builds a centered visual model matrix (`CubeTransforms.CreateCenteredVisualModelMatrix`) from the cube position, side length and the model's asset scale, then resolves the model from `Cube.MaterialId` through `CubeMaterialModelCatalog.Resolve`. It chooses one of two paths:

### Model-backed cube rendering

If a model resolves (grass, dirt and stone have glTF cube models), rendering is delegated to `AnimatedModel.Render()` with the cube's `ShaderName` program. The model is a shared cached asset, not one instance per cube.

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

### Terrain fallback cube rendering

If no model resolves for the material, `Render.DrawTerrainCubeFallback` draws a textured unit-cube mesh for that material (see the fallback paragraph under World Geometry Direction). The older transparent cube with a white wireframe overlay and the `Cube.Animation` property no longer exist.

### Static terrain pass, culling and MASK variants

- The static chunk renderer draws all chunk meshes in one batched pass (`Render.BeginStaticTerrainPass`, `BindStaticTerrainMaterial` per material slot, `EndStaticTerrainPass`), so the program, view, projection and material constants are set once per pass.
- World geometry is culled against the camera `ViewFrustum` (`Rendering/Core/ViewFrustum.cs`) in `WorldRenderer.IsCubeInView` / `IsGeometryBlockInView` and per chunk in `StaticWorldChunkRenderer`.
- World shader programs are loaded with `Render.LoadWorldShader`, which also compiles a MASK variant (`WORLD_ALPHA_MASK`, `Shader.AlphaMaskDefine`). `AnimatedModel` uses it only for glTF `MASK` materials, with the material's alpha cutoff. The OPAQUE programs contain no `discard`.
- `GpuFrameTimer` (GPU time for the world, actors, effects and HUD sections) and `RenderCounters` (draw calls, program binds, uniform uploads) feed the `GPU:` line of the settings-panel performance counters.

See [Performance And Optimizations](performance-and-optimizations.md) for the guardrails.

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

There are two HUD surfaces. The classic sprite HUD is the default; the ImGui HUD described after it is the automatic fallback and the admin view (F3). Settings, the Build panel and the startup menu stay ImGui on both. The switches are in `docs/configuration.md`.

### Classic HUD

`Dream.Rendering.Ui` is GL-free; only `Dream.Rendering.Ui.Gl` touches OpenGL, and a source test enforces that. One frame, inside `Render.DrawHUD`:

1. Crosshair, then `ObserveClassicHud`: `BeginHudFrame` advances the atlas load and `HudSurfaceSelector` decides the surface.
2. `ClassicHudSurface.Draw`: `HudViewBuilder` refills the reused `HudView`, `UiInputRouter.BeginFrame` takes the pointer, the backend resets `UiBatch` for the `UiLayout`, and `ClassicHud.Build` runs the panels. This is the only caller of `ClassicHud.Build`, and it makes no GL call.
3. `GlClassicHudBackend.Submit` calls `UiGlRenderer.Submit`: one `BufferSubData`, one draw per command, GL state restored.
4. ImGui windows on top.

Layers:

- **Atlas.** `UiSpriteTable` reads the JSON in `dream/ui/`; `UiAtlasStore` decodes off-thread and uploads one page per frame (`LinearMipmapLinear`, max level 2, clamp). Uncommon and Rare icons are whole pages with the same rects as `items.png`, so `ItemTierStyle.IconFor` only swaps the page.
- **Batch.** `UiBatch : IUiCanvas` holds 8192 quads and merges neighbours by page and scissor. Quads keep draw order, so alternating pages cost a draw each. Output is premultiplied (`uiVertex.glsl`, `uiFragment.glsl`; blend `ONE, ONE_MINUS_SRC_ALPHA`). One program draws every mode, and the mode is per vertex (the low byte of `UiVertex.Mode`), so modes never split a draw command: 0 is texture times tint, 1 the two-channel digit mask, 2 a flat colour, 3 the liquid gem and 4 additive (see Gems).
- **Gems.** `IUiCanvas.Gem` draws one liquid stone as a single quad of `gem.<hp|mp>.full` on the Hud page, in vertex mode 3; `IUiCanvas.Additive` draws a sprite added to what is below it, in mode 4 (it writes `(rgb * a, 0)`, valid with the premultiplied blend). Both quads merge with the rest of the bar's Hud art, so the bar stays at 12 draw commands in play and 16 with windows and a tooltip. A gem vertex carries its kind in bit 8 of the mode word and a legacy flag in bit 9, and its rim bytes carry R fill, G surface phase, B low-health pulse and A glint sweep (8 bits each, under half a pixel of level on a 112 px gem).
  - **Sprites per gem.** The fragment stage reads four sprites of one size from the same page: `gem.<k>.full` (the stone filled to the top, what the quad's UVs cover), `gem.<k>.empty` (the same stone as dark glass), `gem.<k>.mask` (the level map) and `gem.<k>.spec` (hot glints only). `UiBatch.SetSpriteTable` turns their rects into six `vec2` UV deltas (`uGemOffset`, kind * 3 + empty, mask, spec) and two `vec4` stone rects (`uGemRect`) that `UiGlRenderer.Submit` uploads each frame. No strip atlas is needed. When a gem's four sprites are not all in the table (same page, same size), `Gem` draws the plain sprite and `StatusBarPanel` keeps the old clipped look instead.
  - **Level map (`gem.<hp|mp>.mask`, 200 x 200 px, same rect size as `full`, `empty` and `spec`).** Stored as raw 8-bit values (the atlas is uploaded linear, not sRGB), sampled bilinearly. Inside the silhouette:
    - **R is the fill threshold, measured from the bottom.** R = 0 at the lowest point of the stone and R = 1 at the highest, bent by the refraction; low values fill first. A pixel shows liquid when the fill level reaches its R: `liquid = smoothstep(R - 0.012, R + 0.012, level)`, with `level = mix(-0.03, 1.03, fill) + ripple`. So a plain stone is `R = 1 - v` (v is 0 at the top), and fill 0.25 covers the pixels with R below about 0.25. Any R in 0..1 gives an exactly empty stone at fill 0 and an exactly full one at fill 1; R should span nearly the whole 0..1 range and rise broadly bottom to top (a surface that is not a straight line is the point, but a non-monotonic map leaves islands).
    - **G is the meniscus weight**, 0..1: the strength of the bright line drawn where the liquid ends, `exp(-((R - level) / 0.018)^2) * G * 1.2 * full.rgb` added to the liquid. 1 is a full-strength line at that surface point, 0 none (keep it 0 where the surface would be hidden, for example on dark facets).
    - **B is the per-facet surface phase**, 0..1 as one full cycle: the surface ripples by `0.006 * sin(2 pi (phase + B))`, so facets with different B ripple out of step. A constant B ripples the whole surface together. B = 0 and B = 1 are the same phase.
    - **Alpha: the shader does not read the mask's alpha.** Coverage comes from `full` and `empty` (`mix(empty.a, full.a, liquid)`), which must have the same silhouette. Set the mask's alpha to that silhouette as well, and dilate R, G and B a few pixels past the edge (do not leave them 0 there), because the bilinear and mip sampling blends neighbours' colour channels at the rim.
    - The two other sprites: `empty` is the same stone as dark glass, `spec` holds the hot glints only (straight alpha, lit by the sweep as `spec.rgb * spec.a`).
    - **Old art:** the masks that ship today are a plain top-down gradient (R = 0 at the top and 1 at the bottom; G and B copies of R), the reverse of the convention above. The batch reads them that way (`threshold = 1 - R`, no ripple, a full-width meniscus line) until `gem.setting.left` and `gem.setting.right` are in the atlas, which is the marker that the masks are baked level maps. A test fails if the shipped masks run the other way from what the batch assumes.
  - **Animation.** `ClassicHudSurface.Draw` sets `HudView.HealthLevel`, `ManaLevel`, `AnimationSeconds` and `GemPulse` before the panels run. The levels ease toward their targets (`GemAnimation.EaseRate`, never overshooting, snapping in the last 1/512), from a frame time capped at 0.1 s so a stall moves the animation by one capped step. The first frame shows the targets as they are. The pulse is non-zero only under 25% health and deepens toward 10%. `AnimationSeconds` wraps at 840 s, a multiple of every period (surface phase 0.4 Hz, glint sweep every 7 s lasting 1.4 s, the mana gem half a period later, pulse 1.25 Hz). All of it is allocation free.
  - **Mana.** The game has no mana system. `StatusBarPanel.ManaGemFill` (1) is one constant for how full the MP gem is drawn, a visual choice so the stone does not read as a dead socket; no number is ever drawn on it, and the side gauges stay empty glass. Setting it to 0 draws the stone empty.
  - **Bar art order.** Per group: the bezel under the gem (`gem.setting.left/right`, 62 x 56 u, centred on the gem), the gem (50 x 50 u at 98, -2; the right side mirrors), the talons (71 x 52 u at 128, -3, so they bite over the stone), then the gem's light (`gem.hp.spill` and `gem.mp.spill`, 128 x 64 u centred on the gem, additive, alpha scaled by the level). The dragons (118 x 58 u at 0, -52, six units over the plate) come last, then the numerals, then all the text. HP and level are drawn as `hudnum.0-9` and `hudnum.slash` sprites on the Hud page when the art has them (shrunk to fit the gem width and the EXP cup, never below half size); a number the art has no glyph for, or art without numerals, draws as text as before. Every sprite named here that is not yet in the atlas is simply skipped, so old art keeps working; `UiSpriteNames.PendingRequired` lists the ones to move into `Required` when the art lands.
  - **Software preview.** `UiSoftwareCompositor` (tests) implements both modes with the shader's own arithmetic, reading the same offsets from the batch, so previews and the gem tests are the shader's picture.
- **Scale.** 1 unit is `UiLayout.Scale` pixels, and the art is 4 texels per unit. Panels place rects in units. `UiBatch.UnitScale` is pixels per texel (`UiLayout.PixelsPerTexel`, Scale / 4), which sizes 9-slice borders and tile steps. `UiBatch.TextScale` is `UiLayout.TextScale`: panels lay text out in baked pixels (13 and 26), so it is 1 below scale 4 and 2 at scale 4.
- **Input.** `UiInputRouter` is the only `IUiInput` in the game, and `IUiInput.Pointer` the only pointer. The hot widget is the last one hit in the previous frame. `BeginDrag` fires once past 4 px, `TryEndDrag` once on release. A panel sets `ModalActive` right before its widgets; widgets built earlier in a frame after a modal frame are ignored, and the frame that opens a modal is guarded by `InventoryUiState.ModalFresh`. A changed inventory snapshot cancels a drag and forgets the press; a status change does not.
- **Combat text.** `Render.ProjectClassicCombatText` projects `WorldAnchor` alone into `ScreenAnchor` and drops states it cannot project. `CombatTextPanel` adds the rise and jitter in pixels. The ImGui path keeps its own offsets.

`RenderFaultLog` gets one `hud-classic` line when the surface latches to ImGui (bad atlas, shader or font failure, an exception in build or submit, or Loading past 5 s).

### ImGui HUD

The ImGui HUD includes:

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
- Classic HUD atlases and their sprite JSON under `dream/ui/` (`window`, `hud`, `items`, `fx`)
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
