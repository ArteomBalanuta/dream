# Dream renderer review: world geometry, assets, materials, animation

Scope: `dream/Rendering/{World,Ground,Props,Actors,ViewModels,Models}`, `Core/Render.Terrain.cs`, the static terrain caches,
`TextureDecoder.cs`, `ModelAssets.cs`, glTF loading and skinning, and `cubeVertex`/`cubeFragment`/`enemyFragment`/
`depthFragment`/`vertexShader.glsl`. Read-only review of the `integrate-torch-flame` checkout (master + torch flame).
Tags: **V** = Verified (seen in code), **D** = Derived (follows from code), **I** = Inferred (judgement).

## 1. Area map

| Type | Role |
|---|---|
| `WorldRenderer` (World/WorldRenderer.cs) | Per-frame entry. Static chunks go to `StaticWorldChunkRenderer`. Dynamic cubes and blocks are frustum-culled, then batched by `WorldRenderBatchPlanner` into instanced draws (`Render.DrawModelInstances` / `DrawFallbackInstances`). `DrawCasters` repeats the walk for every shadow view. |
| `StaticWorldChunkRenderer` + `StaticWorldChunkMeshBuilder` | 16^3 chunks, one mesh per (chunk, material, role). Hidden faces are culled against neighbour occluders. Planning and building run on a `Task.Run` worker, and uploads are capped at 2 MiB per frame. The visible pass sorts by material slot. One GL VAO/VBO/EBO per mesh (`StaticMeshBuffer`, 32-byte vertex, uint indices). |
| `Render.Terrain.cs` + `cubeFragment.glsl` (`DREAM_TERRAIN`) | Terrain program: stochastic tiling, macro variation, a cross-material height blend through a `sampler2DArray` of 8 tiles plus a `usampler3D` material map, and derivative relief. |
| `AnimatedModel` / `AnimatedModelData` / `AnimatedModelSkeleton` / `AnimatedModelInstance` | glTF via SharpGLTF. The CPU decodes on the pool (`AsyncResourceLoader`, `ModelAssets`). Upload runs on the render thread, 2 models per frame. Each actor has its own instance with a pose. The CPU samples keyframes and composes joint matrices; the GPU does linear-blend skinning in `cubeVertex.glsl` from `uniform mat4 jointMatrices[64]`. |
| `EnemyRenderer`, `RemotePlayerRenderer`, `LocalPlayerRenderer`, `NpcModelRenderer` | One `BeginRender` + `RenderPrepared` per actor. Each one is also registered in `ShadowActorCasters` and redrawn once per shadow view. |
| `WornArmourRenderer` / `HeldWeaponRenderer` / `EquipmentShapeLibrary` | Procedural equipment shapes (`StaticMeshBuffer` per shape), drawn one part at a time and riding body nodes, with the `enemy` program's equipment branch (Blinn-Phong plus a procedural pattern). |
| `GroundScatterRenderer` / `PropRenderer` | Scatter: spatial cells, then per-asset instanced draws with matrices rebuilt on the CPU every frame. Props: one draw per placement, not instanced. |
| `TextureDecoder`, `StaticTerrainTextureCache`, `StaticTerrainTileArray`, `TerrainMaterialMapTexture` | ImageSharp decode to RGBA8 on the pool. Upload is unsized `GL_RGBA`, CPU box mips built on the render thread, Repeat wrap, trilinear filtering. |
| Asset pipeline `tools/ui-assets/*.py` (`gltfwrite.py`, `charforge.py`, `isoforge.py`, `gen_*`) | Procedural numpy generators write GLB with float32 POSITION/NORMAL/UV, uint16 JOINTS, ≤64 joints and an embedded PNG base colour only (gltfwrite.py:4,17,195). |

Per-frame flow: `PumpUploads(2)` → `UploadFrameLighting` → `ShadowCasterPass.Run` (sun plus up to 4 fires × 4 faces = **17 caster views**, ShadowSettings.cs:24,35; each view re-walks the static chunks, dynamic cubes, scatter, props and actors) → `WorldRenderer.DrawWorld` (static chunks, dynamic batches) → scatter and props → enemies, players, NPCs, then armour and weapons → effects and HUD.

## 2. Antipatterns, ranked by consequence

### A1. Actor draws are fragmented per glTF primitive, and the skinning palette is re-uploaded per primitive and per shadow view (High)
- Evidence:
  - `AnimatedModel.RenderNode` issues one `DrawElements` per primitive with a full `SetMaterialProperties` (about 14 uniform calls plus up to 5 texture binds, AnimatedModel.cs:609-652) (V).
  - `ApplySkinning` caches the joint *composition* per node, but `UploadJointMatrices` runs for every primitive (AnimatedModel.cs:585-592) (V).
  - The shipped hound is **1 mesh with 28 primitives, all material 0, skinned with 22 joints**; villager-smith has 23 primitives with 1 material, and the player 17 primitives with 2 materials (measured from the GLBs) (V).
  - Shadow casters call `BeginDepthRender` + `RenderPrepared` per actor per view (ShadowCasterPass.cs:192-195), which recomputes joints on the CPU and re-uploads them per primitive (D).
- Consequence: one hound costs 28 draws and 28 palette uploads (22 × 64 B) in the main pass. It can cost up to 17 × 28 = 476 draws per frame when it stands inside every fire's range (D). The CPU-side driver cost scales with actor count × primitive count × shadow views, and this is what will cap crowd sizes in an ARPG long before the GPU does (I).
- Fix sketch:
  1. Merge primitives that share a material at import time in `AnimatedModelData.LoadMeshes`, or in `gltfwrite.py`.
  2. Upload the palette once per node per draw (move the upload inside the `!jointsComposed` guard and track the bound program).
  3. Cache joint matrices per actor per frame instead of per pass.
  4. Longer term: put palettes in a UBO or TBO and draw all actors of one model in one instanced call.
- Effort: S for items 1-3, M for item 4.

### A2. The glTF material model is mostly thrown away: PBR factors, normal, metallic-roughness and occlusion maps are decoded, uploaded and bound, then never sampled (High for quality ceiling, Medium for waste)
- Evidence:
  - `AnimatedModelData.LoadMaterials` decodes five channels (AnimatedModelData.cs:254-258), and `AnimatedModel.UploadMaterials` uploads each one with mips (AnimatedModel.cs:165-169) (V).
  - `SetMaterialProperties` binds units 0-4 every primitive (:647-651) (V).
  - `cubeFragment.glsl` and `enemyFragment.glsl` declare no normal, MR or occlusion sampler, and nothing reads `metallicFactor`/`roughnessFactor` (grep over dream/shaders) (V).
  - Tangents are uploaded on location 2 (AnimatedModel.cs:235) but `cubeVertex.glsl` never declares location 2 (V).
  - Lighting is Lambert + hemisphere ambient (lighting.glsl:19-23). Only the hand-authored equipment branch has a specular lobe (enemyFragment.glsl:315-367) (V).
- Consequence:
  - A normal-mapped or PBR asset (the Quaternius and Kenney library under `animations/library`) renders flat, while paying VRAM, decode time and binds for its maps (D).
  - The vertex stride is 76 B, 12 B of which is dead (D).
  - "Material" in this engine means three hardcoded programs (`cube`, `terrain`, `enemy`) × an OPAQUE/MASK `#define`, with per-type uniforms such as `uEquipSurface` and `uActorTintOffset` leaking across draws. That is why every program switch has to re-zero them (AnimatedModel.cs:546-555) (V/I).
- Fix sketch:
  1. Short term: skip decoding the channels no shader reads, and drop the tangent stream.
  2. Proper fix: one world uber-shader with a material UBO (base, MR, emissive, flags, texture-array layer indices), normal mapping via the existing tangents, and a GGX lobe (the equipment branch already approximates one). Per-type features become flag bits, not separate programs.
- Effort: S for the first step, L for the proper fix.

### A3. Textures: unsized non-sRGB RGBA8, no anisotropy, no compression, CPU mip generation on the render thread (Medium-High)
- Evidence:
  - `TextureDecoder.UploadTexture2D` uses `PixelInternalFormat.Rgba` (unsized), never `Srgb8Alpha8` (TextureDecoder.cs:97-106) (V).
  - No `TEXTURE_MAX_ANISOTROPY` anywhere, and no BC, S3TC, KTX or DDS path (repo grep) (V).
  - Power-of-two textures get `UploadBoxMips`, which runs `BoxReduce` (a scalar four-deep nested loop) on the render thread inside the `AnimatedModel` constructor called from `PumpUploads` (TextureDecoder.cs:118-125, 171-194; ModelAssets.cs:97-98) (V).
  - `ModelUploadsPerFrame = 2` caps models, not bytes (PlayScene.cs:35) (V).
  - `dead_oak.glb` carries a 1024² PNG (V).
- Consequence:
  - Lighting maths (N·L, point-light falloff, the terrain height blend, mip averaging) runs on sRGB-encoded values, which gives harsh terminators and over-dark mips (D). This sits under the known "albedo × gain, no tonemap" contract and limits how far lighting quality can go (I).
  - The 50° pitch camera views every vertical face, and the far half of the ground, at grazing angles. Trilinear filtering without anisotropy blurs exactly the cliff and wall faces the relief shader works to detail (I).
  - Uncompressed RGBA8 is 4-8× the VRAM and bandwidth of BC1/BC7 (D).
  - A 1024² box-mip pass (~1.4 M output texels × 4 channels × 4 taps) on the render thread is a load-time hitch every time a new prop or actor streams in (I: not timed).
- Fix sketch:
  1. sRGB internal format for colour textures and linear for data, plus `GL_FRAMEBUFFER_SRGB` or a final encode once the lighting area agrees.
  2. `GL_EXT_texture_filter_anisotropic` (core in 4.6, available as an extension on every 3.3 desktop driver) at 4-8×.
  3. Move `BoxReduce` into `AnimatedModelData.Load` on the pool, so the decoded texture carries its mip chain.
  4. Offline: have the Python pipeline emit KTX2 (BasisU → BC7/BC1, or straight BC7 via `compressonator`/`bc7enc`).
- Effort: S for items 2-3, M for items 1 and 4.

### A4. Terrain shades back faces and overdraw it could skip; the double-sided rationale is obsolete (Medium)
- Evidence:
  - `BeginStaticTerrainPass` disables culling "so clipped/edge cameras do not reveal collision-only blocks" (Render.Terrain.cs:104-107) (V).
  - `GameSettings.SetCameraViewMode` now forces `Isometric` (GameSettings.cs:224-227) (V).
  - The camera has a fixed pitch of 50° and a fixed yaw of 45° (IsometricView.cs:10-11) (V).
  - The draw order is sorted by material slot only (StaticWorldChunkRenderer.cs:272), with no front-to-back order and no depth prepass (V).
  - The terrain fragment is the most expensive in the frame: two to eight `textureGrad`, about 18 hashes, a 9-tap sun PCF (lighting.glsl:69-76) and the point-light loop (V).
- Consequence:
  - From a fixed 45°/50° camera with a 35° FOV, only +X, +Y and +Z faces ever face the viewer (D: the view direction has components 0.45/0.77/0.45, and at the 16:9 screen edge the X/Z components stay above 0).
  - −X, −Y and −Z faces, including every floor-bottom face the builder keeps, are rasterised for nothing. With material-sorted order, a back or covered fragment drawn first is fully shaded before it is overwritten (I).
- Fix sketch:
  1. Re-enable back-face culling for the visible terrain pass. Keep it off for shadow casters, where `BeginDepthRender` already disables it.
  2. Better: emit indices grouped by face direction per mesh and draw only the three camera-facing ranges in the main view, which removes half of terrain vertex work.
  3. Sort meshes front-to-back within a material slot (the camera direction is constant, so the key is a dot product).
  4. Optionally add a depth-only prepass for terrain using the existing shadow-depth program.
- Effort: S for culling and sort, M for face ranges and prepass.

### A5. Props are drawn one placement at a time, with no geometry budget or LOD, into up to 17 shadow views (Medium)
- Evidence:
  - `PropRenderer.Draw` loops placements and calls `model.RenderPrepared(shader, matrix)` (PropRenderer.cs:111-121), and `DrawCasters` does the same per view (V).
  - The comment notes "an oak is 62k triangles"; `dead_oak.glb` measures 62,317 triangles (V).
  - There is no LOD anywhere in Rendering, shaders or the generators (grep) (V).
  - The instanced path already exists: `RenderPreparedInstanced` is used by the scatter (V).
- Consequence:
  - Each visible oak is 62k triangles in the main pass plus up to 62k per shadow view that accepts it (D).
  - At the fixed iso distance (focus distance 18, FOV 35°) the tree covers a few hundred pixels, so most triangles are sub-pixel. 2×2 quad shading then wastes most fragment work (I).
  - Prop count multiplies draws × views linearly (D).
- Fix sketch:
  1. Route props through `RenderPreparedInstanced`.
  2. Set an iso-specific triangle budget in the generators (decimate in `isoforge.py`, for example with `pymeshlab` or `fast-simplification`). With a fixed camera distance, one well-budgeted mesh beats an LOD chain.
  3. Add a separate low-poly shadow proxy, since shadow maps need far less detail.
- Effort: S for instancing, M for budgets and proxies.

### A6. Instanced scatter rebuilds static matrices every frame, per node and per view (Medium-Low)
- Evidence:
  - `GroundScatterRenderer.Draw` builds `CreateScale * CreateRotationY * CreateTranslation` per visible placement every frame (GroundScatterRenderer.cs:162), and again in `DrawCasters` (:276) (V).
  - `RenderPreparedInstanced` then composes every instance with every renderable node and re-uploads one orphaned buffer per node (AnimatedModel.cs:409-417, 487) (V).
- Consequence: placements are immutable, yet CPU cost and bus traffic run at O(instances × nodes × 18 views) per frame (D).
- Fix sketch:
  1. Build a static per-asset instance VBO once, ordered by spatial block.
  2. Cull per block and draw contiguous visible ranges by re-pointing the instance attribute offset (3.3), or with `glDrawElementsInstancedBaseInstance` (4.2).
  3. Pre-bake node transforms into the vertices for single-node scatter assets.
- Effort: M.

### A7. Skinning is capped at 64 joints with a silent bind-pose fallback, and there are no clip transitions (Medium)
- Evidence:
  - `uniform mat4 jointMatrices[64]` (cubeVertex.glsl:18) uses 1024 uniform components, which alone equals the GL 3.3 minimum `MAX_VERTEX_UNIFORM_COMPONENTS` (D).
  - A skin with more than 64 joints logs (only with `DREAM_MODEL_DIAGNOSTICS=1`) and returns null, so the model renders in bind pose (AnimatedModelData.cs:226-231) (V).
  - `AnimatedModelInstance.Play` switches clips instantly (AnimationPlaybackState.cs:11-17; `Pose.Apply` writes one clip only). No crossfade exists in Models or Actors; only the procedural player layers blend (PlayerAnimationStateMachine.cs:385-390) (V).
  - Joints are stored as float (V).
- Consequence:
  - Mixamo, Quaternius and most humanoid rigs (65 or more bones) silently T-pose (D).
  - Every idle → walk → attack switch on enemies pops visibly, which ARPG combat readability suffers from (I).
- Fix sketch:
  1. Put palettes in a UBO (16 KiB guaranteed, so 256 joints) or a `samplerBuffer`/TBO (core 3.1) holding all actors' palettes, indexed per instance. That also enables the instanced actor draw from A1.
  2. Add a two-pose crossfade in `AnimatedModelPose` (sample both clips, slerp TRS per track over 0.1-0.2 s).
  3. Make the joint-cap failure loud, as a `RenderFaultLog` entry.
- Effort: S for the crossfade and loud failure, M for the TBO/UBO.

### A8. Atlas mips bleed past the generator's gutter (Medium-Low)
- Evidence:
  - `charforge.py:34` sets `GUTTER = 4` px of dilated colour (V).
  - Every glTF texture gets a full mip chain to 1×1 with Repeat wrap (TextureDecoder.cs:116-125) (V).
  - Actors are about 28×101 px on screen (project memory) with 256-512² atlases, so they sample around mip 2-3 (I).
- Consequence: at mip 3 the 4 px gutter is half a texel. Neighbouring islands bleed into each other, and Repeat wraps across atlas edges (D). This shows up as seams and colour fringes on characters and props.
- Fix sketch:
  1. Clamp-to-edge for atlases, which needs a texture-kind flag from the loader.
  2. Set `TEXTURE_MAX_LEVEL = log2(gutter)` (2), or raise the gutter to 8-16 px.
  3. Longer term, pack per-material layers into texture arrays instead of atlases.
- Effort: S.

### A9. Terrain is split into one mesh and draw per (chunk × material × role) although a tile array of every blendable material already exists (Low-Medium)
- Evidence:
  - Mesh key = chunk + material + role (StaticWorldChunkRenderer.cs:1222-1235) (V).
  - `BindStaticTerrainMaterial` binds a separate 2D texture per slot (Render.Terrain.cs:113-128) (V).
  - `StaticTerrainTileArray` already holds the same 8 tiles as layers (StaticTerrainTileArray.cs:213-221), so each terrain tile is resident twice (V).
- Consequence: the draw count multiplies by the material count, there are about 9 texture rebinds per pass, and VRAM is duplicated (D). With a world of at most 100×100 (WorldDefaults.cs:9) the absolute cost is small (I).
- Fix sketch:
  1. Add a per-vertex layer byte; the static vertex has room if normals are packed to `GL_INT_2_10_10_10_REV`, which is core in 3.3.
  2. Add grass side, grass bottom and lamp as layers 8-10, and sample only the array.
  3. The result is one mesh and one draw per chunk, and `BindStaticTerrainMaterial` disappears.
- Effort: M.

### A10. The terrain blend shader pins mesh topology, so greedy face merging is impossible (Low now, design constraint)
- Evidence:
  - cubeFragment.glsl:244-257 assumes every static top quad is exactly one unit cell with UV origin at the cell corner ("the mesh builder does not merge faces") (V).
  - The mesh builder emits one quad per exposed face (StaticWorldChunkMeshBuilder.cs:181-203) (V).
- Consequence: today this is fine. Top surfaces of a 100×100 world are about 20k triangles, and fragment cost dominates, not vertex cost. The constraint matters only if worlds grow or if vertex-level baked AO, which wants merged quads split by AO, is added (I). Any future greedy mesher has to move the blend to a world-space cell lookup (`floor(fragPos.xz)`) first.
- Effort: n/a (note in docs).

### A11. Whole-world extract and plan on every static edit (Low-Medium)
- Evidence:
  - `StaticWorldChunkExtractor.ExtractCells` copies every static cube and block into a new `List` and `Dictionary` on the render thread whenever the signature changes (StaticWorldChunkRenderer.cs:224-226; StaticWorldChunkExtractor.cs:9-39) (V).
  - `Plan` then re-groups and re-hashes the whole world on the worker (:1087-1238) (V).
  - Both are a known gap (performance doc, "Current Known Gaps") (V).
- Consequence: the cost is O(world) per destructible-cube death or fracture. It is fine at 100×100 but gives a GC and frame-time spike per combat edit and blocks larger worlds (I).
- Fix sketch: keep a persistent per-chunk cell store on the map side, updated incrementally from `StaticDirtyCells`, and send only dirty chunks plus their neighbours to the worker.
- Effort: M.

### A12. Actors have no frustum cull, and dead code paths remain (Low)
- Evidence:
  - `EnemyRenderer.DrawAtRenderTick` draws every active enemy with no `ViewFrustum` test (EnemyRenderer.cs:71-115) (V); `NpcModelRenderer` behaves the same (V).
  - `Render.DrawCube` and `DrawGeometryBlock` have no callers; a test even asserts they are not called (WorldInstanceBatchDrawerTests.cs:217-218), yet docs/rendering-and-assets.md:104 still describes them as live (V).
  - `ViewModels/**` (15 files) and the first-person branches (PlayScene.Render.cs:233) are unreachable now that the view is forced to isometric (D).
  - `vertexShader.glsl` is debug-only (V).
- Consequence: off-screen enemies still pay full draw and skin cost (enemies are server-interest-bounded, so this is small) (I). Dead code also misleads anyone reading the docs.
- Fix sketch: frustum-test the actor AABB (already computed for casters), and delete or quarantine the dead paths.
- Effort: S.

## 3. Level-ups

| Technique | What it unlocks here | Cost / risk | GL |
|---|---|---|---|
| **TBO (`samplerBuffer`) or UBO joint palettes, one block for all actors** | Lifts the 64-joint cap. Instanced skinned actors (one draw per model per pass instead of primitives × actors). Palettes computed once per frame and shared by all 18 views (fixes A1/A7). | M. Every program touching `cubeVertex.glsl` changes; contract tests pin it. | 3.1 core (TBO), 3.1 UBO; fine on 3.3/macOS |
| **Primitive merge at import** (meshoptimizer via `meshoptimizer` Python bindings or a C# merge in `LoadMeshes`; also `meshopt_optimizeVertexCache`/`simplify`) | 28 → 1 draws per hound, plus vertex-cache-ordered indices and offline decimation for the 62k oak. | S-M, offline only. | none |
| **Material UBO + one world uber-shader (flags), glTF metallic-roughness + normal maps** | Uses the PBR data already being decoded. Kills the per-program uniform-leak resets (`uEquipSurface`, `uActorTintOffset`, `uWind` zeroing). One program means fewer binds. | L. Must keep the albedo ≤ 0.40 contract and the "no discard in OPAQUE" rule. | 3.3 OK |
| **sRGB textures + `GL_FRAMEBUFFER_SRGB`** (or a final encode) | Physically sane N·L, light falloff and mip filtering, and the prerequisite for any future HDR/tonemap. | M. Retunes every colour constant, so coordinate with the lighting area. | 3.0 core |
| **Anisotropic filtering** (`GL_EXT_texture_filter_anisotropic` / ARB 4.6) | Crisp grazing walls and far ground under a fixed 50° pitch, for about one line per texture upload. | S, negligible risk. | ext on 3.3 everywhere incl. macOS |
| **KTX2 + Basis Universal or BC7 offline in the Python pipeline** (`KTX-Software` `toktx`, `basisu`; C# runtime via `BasisUniversal` transcoder or pre-transcoded BC7 `.dds`) | 4-8× less VRAM and bandwidth, no runtime decode or mip generation, faster loads. | M. Needs a transcoder DLL or BC-only builds. BC7 is not exposed on macOS GL 4.1 (no `ARB_texture_compression_bptc` there), so fall back to BC1/BC3 (S3TC) on mac. | BC1-3 via ext on 3.3; BC7 needs 4.2/ARB_bptc |
| **Texture arrays for actors and props** (one `sampler2DArray` per size class, layer in the material UBO) | Removes atlas bleeding (A8), allows all props or actors of a size class in one draw, and is the prerequisite for MDI. | M | 3.0 core |
| **Face-direction index ranges + front-to-back sort + optional terrain Z-prepass** | About 50% fewer terrain vertices in the main view, and no shading of hidden fragments under the costly terrain shader (A4). | S-M | 3.3 |
| **Per-vertex baked AO in the chunk mesher** (classic voxel corner AO, 2 bits per vertex) | The cheapest big visual win for blocky iso terrain: contact darkening at cliff bases and wall seams with no SSAO pass. It multiplies albedo, so it respects the no-tonemap contract. | S-M. Interacts with the blend shader (A10); store AO per vertex, not per quad. | 3.3 |
| **Persistent mapped buffers (`ARB_buffer_storage`) + MDI (`glMultiDrawElementsIndirect`) + one big static terrain/prop VBO** | Collapses chunk and prop draws into one call per pass and makes the 17 shadow views cheap. GPU culling into the indirect buffer is possible later. | L, and it needs a GL 4.3 path (MDI 4.3, buffer storage 4.4, bindless `ARB_bindless_texture` not on macOS/Intel-old). Keep a 3.3 fallback, or move the minimum to 4.3/4.6 and accept dropping macOS. | 4.3+/4.4+ |
| **Shadow caster proxies + cached static shadow maps** | Static terrain and props in the sun map are camera-invariant within a texel-snapped volume: render static casters once, re-blit, and draw only dynamic actors per frame. Low-poly proxies for fire faces. | M | 3.3 |
| **Two-clip crossfade in `AnimatedModelPose`** | Removes animation pops on enemies. | S | none |

Versus a "glTF-PBR + texture arrays + GPU skinning + bindless/MDI" target: GPU skinning already exists. The missing pieces are, in order of payoff: (1) skinned instancing via a palette buffer, (2) using the glTF material data (PBR, normals) under sRGB, (3) arrays instead of atlases or per-material 2D textures, (4) MDI/bindless, which is the only step that needs to leave GL 3.3.

## 4. Done well

- Decode is strictly off-thread and upload strictly on the render thread, with `AssertRenderThread` guards. Async model, terrain-texture and tile-array loaders cache failures so they are never retried per frame (V).
- The static chunk cache has careful signatures, `StaticDirtyCells` keeps dynamic cubes out of the rebuilds, uploads are byte-budgeted, and old buffers stay alive until their replacements land (V).
- Hidden-face culling with a face-plane occluder index runs on pooled arrays and on a worker (V).
- `AnimatedModelInstance` separates the shared GPU model from per-actor pose state, `KeyframeCurve` sampling does not allocate, and there is one palette upload call per draw (V).
- OPAQUE and MASK programs are split so that opaque programs keep early-Z, which is documented and measured (V).
- The terrain shader is strong for GLSL 330: stochastic tiling with `textureGrad` taken before branching, a seam-symmetric corner-weight blend, and derivative relief with no extra fetch (V).
- The Python pipeline dilates atlas gutters, enforces the joint limit, and is reproducible (V).
