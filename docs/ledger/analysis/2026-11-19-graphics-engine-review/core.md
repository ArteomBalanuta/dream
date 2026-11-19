# GL core, state and draw submission: review

Scope: `dream/Rendering/Core/**`, `dream/Rendering/Models/**`, `dream/Application/Game.cs`, `dream/Client/Scenes/PlayScene.Render.cs`.
Checkout: `integrate-torch-flame` (master content). Read-only review. Tags: **V** = Verified in code, **D** = Derived from code, **I** = Inferred (judgement).

## 1. Area map

| Type | Role |
|---|---|
| `Game` (`Application/Game.cs`) | OpenTK `GameWindow`, single thread. Update and render run off `UpdateFrequency` (frame cap default 144, VSync off by user choice) (Game.cs:122-125, V). MSAA on the default framebuffer (Game.cs:103). Debug context and KHR_debug in DEBUG builds only (Game.cs:109-112, 330-337). `OnRenderFrame` does scene render, `SwapBuffers`, then a `glGetError` drain every 60 frames (every frame in DEBUG) (Game.cs:518-549, 75-77, 783-801). |
| `PlayScene.Render` | Drives a frame: counters capture/reset, `GpuFrameTimer.BeginFrame`, `ModelAssets.PumpUploads(2)`, sky evaluate, clear, presentation capture, light collection, **shadow pass** (`PrepareShadows`), world (`DrawWorld`: static chunks + dynamic batches), props, enemies, local and remote players, economy entities, sky, boundary mist, effects, debug, viewmodel, HUD/ImGui (PlayScene.Render.cs:31-270, V). |
| `Render` (partials) | Owns shaders (`DisposableRegistry<string, Shader>`), the 4 UBOs (FrameLighting, LightList, BoundaryMist, FireShadowList), shadow targets, fallback cube mesh, HUD and ImGui GL objects, and child renderers. Terrain/static pass entry points are in `Render.Terrain.cs`, the shadow pass glue is in `Render.Shadows.cs`. |
| `Shader` | Compile/link, a one-level `#include`, `#define` insertion for the MASK and TERRAIN variants, location cache, value caches for 9 hot uniforms, UBO binding-point setup at link (Shader.cs:79-173). |
| `AnimatedModel` | Every glTF draw: one VAO/VBO/EBO per primitive, a per-model instance VBO, per-primitive material upload, skinning, and the OPAQUE↔MASK program switch (AnimatedModel.cs:267-668). |
| `StaticMeshBuffer` | One VAO/VBO/EBO per static chunk mesh (chunk × material slot), plus the fallback cube (StaticMeshBuffer.cs:180-276). |
| `InstanceMatrixBuffer` | Per-instance mat4 VBO. Grows geometrically and is orphaned before each `BufferSubData` (InstanceMatrixBuffer.cs:58-87). |
| `AsyncResourceLoader` / `ModelAssets` | Thread-pool decode, render-thread upload bounded to 2 per frame, polled `TryGet` (ModelAssets.cs:115-125, AsyncModelLoader.cs:196-279). |
| `GpuFrameTimer`, `RenderCounters`, `GLUtils` | Non-stalling timer-query ring, thread-static draw/program/uniform counters, error drain with fatal latch, KHR_debug filter. |

**Per-frame flow (D):** `PumpUploads` → `PrepareShadows` (1 sun view + up to 4 fires × 4 faces = **17 caster views**, each resubmitting every accepted caster) → `DrawWorld` (one `UploadFrameLighting`, static chunk pass, then instanced dynamic batches) → props (one draw per placement and primitive) → enemies (per enemy: `BeginRender`, `RenderPrepared`) → players and equipment (one draw per equipment part) → sky, mist, FX → HUD → ImGui → `SwapBuffers`.

### Counts the brief asked for
- **Draw calls (V, measured in the ledger):** camp at night, 4 casting fires, 1440×1024: **301 draws with shadows off, ~1,510 with shadows on** (docs/ledger/wave-shadow-maps.md:631). The shadow pass is about 80% of all submissions.
- **Program binds and uniform uploads:** the last recorded figures (6 / 105, wave 10; 155 uploads after T2) are older than props, scatter, sculpted equipment and shadows (docs/ledger/work-log/2026-10-28-engine-iteration-wave-10.md:73, …-day-night-wave-t2.md:47). No current figure is recorded, so the structural cost is derived instead (D):
  - every `BeginRender`: 1 `glUseProgram` plus 4 uploads (view, projection, `useSkinning`, `useInstancing`) (AnimatedModel.cs:300-304);
  - every non-instanced node: 2 uploads (model mat4 plus normal mat3) (Shader.cs:357-364);
  - every primitive: `useSkinning`, `baseColorFactor` and `hasBaseColorTexture` are uploaded every time. Samplers are re-set with `SetInt` every time. The joint palette (JointCount mat4) goes up every time for a skinned primitive. The 5 emissive/cutoff/scroll setters are value-cached. Up to 5 `ActiveTexture`+`BindTexture` pairs follow (AnimatedModel.cs:571-668);
  - every switch to the MASK program: about 12 uploads (AnimatedModel.cs:538-560).
- **Why so many:** the actor, prop, equipment and shadow paths are not instanced. View and projection live in each program's default block. The material is re-applied per primitive from scratch. Every caster is resubmitted for each of the 17 shadow views.

## 2. Antipatterns, ranked by consequence

### A1. The shadow pass resubmits the whole caster set 17× per frame and caches nothing (draws ×5)
- **Evidence (V):** The sun view is drawn, then for every fire `for face < FaceCount` (4) `target.DrawCasters(view)` (ShadowCasterPass.cs, `Run`; FacesPerLight = 4, ShadowSettings.cs:35; default 4 fires, :24). `DrawShadowCasters` redraws static chunks, dynamic cubes and blocks, props, ground scatter, enemies, players, NPCs and ground items per view (Render.Shadows.cs:162-171). Measured result: 301 → ~1,510 draws (ledger :631). The ledger names this as the reason the fire count is capped at 4 (ledger :594, :603).
- **Consequence:** CPU submission is the frame's dominant cost. The renderer was already "CPU-bound on submission by ~14×" (ledger :47). More casting lights scale linearly in draws, so the cap blocks scenes with several shadowing braziers.
- **Fix sketch:** Braziers are placed and static (`PlacedLightIndex`). Cache each static fire tile's **static-caster** depth and re-render it only when the slot's light changes. Each frame, copy the cached tile and draw only the dynamic casters (actors, dynamic cubes) on top, using `glBlitFramebuffer` depth (GL 3.0) or `glCopyImageSubData` (4.3, not macOS). Today the fire shadow origin uses the *flickered* position (Render.Lighting.cs:52-58 comment, "flickered positions"), so the origin must be decoupled from the flicker first: flicker the intensity, not the shadow eye. Treat the sun map the same way: snap the focus to texels and redraw statics only when the snapped focus or the sun angle changes. A cheaper first step is the ledger's own note: skip the cover/scatter layer in fire faces, which is about 425 of about 490 scatter draws (ledger, "Measured" section).
- **Effort:** M (cover skip: S). Overlaps the shadow reviewer's area; recorded here because it is the largest submission cost.

### A2. Each primitive re-applies its material through string-keyed lookups, and constant samplers and unused texture units are re-bound every draw
- **Evidence (V):** `SetMaterialProperties` makes about 17 calls of the form `shader.SetX("material.…")` per primitive. Each one does a `Dictionary<string,int>` lookup that hashes a 20-30 char string (Shader.cs:291-301, 484-554; AnimatedModel.cs:627-651). `SetTexture` calls `shader.SetInt(name, unit)` on every draw (AnimatedModel.cs:661-668), but the sampler→unit mapping is constant for the program's life. `BindShadowSamplers` already does this the right way, once at load (Render.Shaders.cs:95-100). Normal, metallic-roughness and occlusion textures are bound to units 1, 2 and 4 per draw, but no shader declares those samplers (grep of `dream/shaders`: only `baseColorTexture` and `emissiveTexture`). Props call `RenderPrepared` once per placement, so the full material set goes up again for every prop instance (PropRenderer.cs:107-121).
- **Consequence (D):** CPU time per draw grows with string hashing and redundant GL calls on the path that is already the bottleneck (A1 multiplies it in the depth path only for the matrices, but the visible path pays it all). `UniformUploads` under-reports the cost, because lookups that return -1 and texture binds are not counted.
- **Fix sketch:** Resolve a per-program material location table once, using the existing `GetUniformLocations(string[])` (Shader.cs:306-322). Set the sampler uniforms once after link. Drop the three unused channels. Skip the whole material block when the previous primitive in the same program used the same `MaterialData`. Instance the props per kind the way `GroundScatterRenderer` already does (GroundScatterRenderer.cs:167-177).
- **Effort:** S.

### A3. Unused glTF texture channels go to VRAM, and their mip chains are built on the render thread in scalar C#
- **Evidence (V):** `UploadMaterials` uploads Normal, MetallicRoughness and Occlusion textures with mips (AnimatedModel.cs:165-169). The decoder fills them (AnimatedModelData.cs:241-244), but no shader samples them (see A2). For a power-of-two texture, `UploadTexture2D` computes every mip level with `BoxReduce`, a per-pixel per-channel C# loop that allocates a `byte[]` per level, **on the render thread** inside `PumpUploads` (TextureDecoder.cs ~118-125, `BoxReduce`, `UploadBoxMips`). Up to 2 models upload per frame (PlayScene.cs:35).
- **Consequence:** (D) A model with a 2k normal map costs about 21 MB of VRAM per unused channel, plus a multi-ms hitch on the frame it uploads. The hitch is about 16M byte-ops for a 2k RGBA chain. (I) This is part of the "first-visibility spikes" listed under Known Gaps (performance doc).
- **Fix sketch:** Do not decode or upload channels no program reads. Move `BoxReduce` into the worker decode (`AnimatedModelData.Load`) so the render thread only issues `TexImage2D` per level. Later, consider pre-baked KTX2 mips.
- **Effort:** S.

### A4. Hidden per-program uniform state forces hand-written "reset" choreography in many places
- **Evidence (V):** Each world program is two GL programs (OPAQUE and MASK). Any per-draw value must therefore be written to both, or reset after use. `BindProgram` re-writes view, projection, instancing, tint, the 3 equipment vectors, wind and model on every switch (AnimatedModel.cs:538-560). EnemyRenderer tracks `_maskTintDirty` to clean the MASK variant (EnemyRenderer.cs ~233-260). The terrain pass carries the comment "The cube program is shared with AnimatedModel, which leaves its last emissive flag set" (Render.Terrain.cs:83-84). Ground scatter resets wind in a `finally`, shadows keep their own `WindUniform` tracker (Render.Shadows.cs:38-41), WornArmour re-zeroes `uActorTintOffset` (WornArmourRenderer.cs ~268-270), and a test exists only to police this (`Wave9AlphaMaskUniformCoverageTests`, cited at AnimatedModel.cs:551).
- **Consequence:** Every new per-draw uniform adds reset code in N renderers plus a test, and forgetting one causes a stale-uniform visual bug in an unrelated pass. Each MASK switch costs about 12 uploads.
- **Fix sketch:** Move per-draw state into a std140 "DrawParams" UBO (tint, equipment surface, eye, detail, wind, flags) at a fixed binding point, like `FrameLightingBuffer`. Both variants then read the same bytes, and the reset becomes "write the struct". Use a small ring of 256-byte-aligned slots with `BindBufferRange` per draw, which is GL 3.1 core.
- **Effort:** M.

### A5. View and projection are re-uploaded per program, per `BeginRender`, and again on a MASK switch
- **Evidence (V):** AnimatedModel.cs:300-302 and 541-542; Render.Terrain.cs:79-80 and 229-230; Render.Shadows.cs:153-154; HeldWeaponRenderer.cs:231-232; WornArmourRenderer.cs:260-261. Callers recompute `camera.GetViewMatrix()` per enemy, player and model (EnemyRenderer.cs:111, 220, etc.). `Shader.Use()` calls `glUseProgram` with no current-program check (Shader.cs:284-288).
- **Consequence:** Each enemy, player, prop kind and scatter kind costs 1 bind plus 4 uploads, and the shadow views repeat that per caster model. The `ProgramBinds` counter counts calls, not changes.
- **Fix sketch:** Add a Camera UBO (view, proj, viewProj, eye) at binding point N, uploaded once per view, using the same pattern and code as `FrameLightingBuffer`. The 17 shadow views become 17 small `BufferSubData`s, or one buffer with 17 ranges and a `BindBufferRange` each. Keep the current program in `Shader` (or a GlState) and skip a redundant `glUseProgram`.
- **Effort:** S.

### A6. The joint palette is uploaded per primitive, skinned actors are never instanced, and the palette sits at the GL 3.3 uniform floor
- **Evidence (V):** `ApplySkinning` caches the joint *computation* per node (`jointsComposed`), but it calls `UploadJointMatrices` for every skinned primitive (AnimatedModel.cs:585-593). `uniform mat4 jointMatrices[64]` lives in the vertex default block (cubeVertex.glsl:18). That is 1,024 components, exactly GL 3.3's guaranteed `MAX_VERTEX_UNIFORM_COMPONENTS` minimum, before `model`, `view`, `projection`, `normalMatrix` and `uWind`.
- **Consequence:** (D) A pre-dressed multi-primitive player (memory: helm, hood, pauldron and cloak are baked into the model) pays one full palette upload per primitive. (I) This works on desktop drivers (4096+ components), but more joints or more vertex uniforms move further outside spec. Wolves and hounds cannot be instanced, because the palette is a per-draw uniform.
- **Fix sketch:** Upload the palette once per instance into a UBO range (64 × 64 B = 4 KB, under the 16 KB minimum block). For crowds, upload all palettes to a TBO (GL 3.1) indexed by `gl_InstanceID * jointCount`. That enables one instanced draw per enemy type and primitive.
- **Effort:** M.

### A7. Mixed OPAQUE/MASK models ping-pong programs per node
- **Evidence (V):** Inside a node, the first MASK primitive triggers `BindProgram` (about 12 uploads). At node end, `RestoreProgram` rebinds the base program (AnimatedModel.cs:451-456, 471, 563-569), and the next node's MASK primitive switches again.
- **Consequence:** (D) Foliage, props and characters with cut-out parts across nodes pay one or two program switches plus about 12 uploads per node, multiplied by placements and shadow views. Depth-only drawing skips MASK primitives, so the depth path is not affected.
- **Fix sketch:** At load, split each model's primitive list into an opaque list and a mask list. Draw every node's opaque primitives, then switch once and draw every node's mask primitives.
- **Effort:** S.

### A8. No GL state cache, so the code saves and restores state with per-frame `glGet*`/`glIsEnabled`
- **Evidence (V):** `ImGuiGlStateSnapshot.Capture` makes about 15 `glGet`/`glIsEnabled` calls per frame (Render.ImGui.cs:105-137). `ShadowMapTargets` saves framebuffer, viewport, depth, cull, blend and scissor with `glGet` every frame (ShadowMapTargets.cs:163-172). Sky, war mist, boundary mist and FX query `IsEnabled(CullFace)` (SkyRenderer.cs:75, WarMistRenderer.cs:92, BoundaryMistVoidRenderer.cs:61, FxRenderer.cs:54, 96). `BeginRender` sets depth and cull on every call (AnimatedModel.cs:289-298).
- **Consequence:** (I) With NVIDIA's "threaded optimization" (the default), a `glGet` forces the app thread to synchronise with the driver's worker thread, so each one is a real stall, not a cheap read. (D) Redundant enables and binds inflate CPU per draw.
- **Fix sketch:** Add a tiny `GlState` shadow (current program, VAO, array buffer, texture per unit and target, enable bits, blend func, depth func and mask, viewport, framebuffer) that every pass goes through. Then saving and restoring is a struct copy with no `glGet`.
- **Effort:** S-M.

### A9. Static terrain: one VAO/VBO/EBO per chunk × material, drawn one by one in every view
- **Evidence (V):** `GlStaticWorldChunkMeshStore` keeps a `StaticMeshBuffer` per key (StaticWorldChunkRenderer.cs ~963-985). `DrawBatched` binds the VAO and issues `DrawElements` per mesh (StaticMeshBuffer.cs:100-110). The same happens per shadow view (`DrawDepth`, StaticWorldChunkRenderer.cs ~1040-1046).
- **Consequence:** (D) Draws scale with chunks × materials × 18 views (1 main + 17 shadow), and each draw pays a VAO switch.
- **Fix sketch:** Use one arena VBO/IBO for all static chunks with sub-allocated ranges, then `glMultiDrawElementsBaseVertex` (GL 3.2 core) once per material slot in the main pass. Material slot does not matter for depth, so the depth views can use a single call. The texture-array material path that already exists (`StaticTerrainTileArray`) could later remove the per-material split altogether.
- **Effort:** M.

### A10. Every glTF model uses a fat 76-byte vertex
- **Evidence (V):** `Vertex` = pos3 + normal3 + tangent3 + uv2 + joints4(float) + weights4(float) = 76 B (AnimatedModelData.cs:29-38). The tangent is bound to location 2, which `cubeVertex.glsl` never declares (cubeVertex.glsl:3-8). Static props and scatter carry 32 B of zero skin data.
- **Consequence:** (D) A static mesh needs 32 B, so vertex fetch is about 2.4× larger, and the cost repeats across 18 views. An oak is 62k triangles (ledger :417).
- **Fix sketch:** Use two layouts. Static: pos3f, normal as `INT_2_10_10_10_REV`, uv2f, about 24 B. Skinned: add `UNSIGNED_BYTE ×4` joints via `glVertexAttribIPointer` and `UNSIGNED_SHORT ×4` normalized weights. Drop the tangent until a normal map exists. All of this is GL 3.3 core.
- **Effort:** S-M.

### A11. Sculpted equipment issues one draw plus 3 uploads per part
- **Evidence (V):** `WornArmourRenderer.DrawModel` loops parts with `SetEquipmentDetail`, `SetModelMatrix` (2 uploads) and `DrawBatched` (WornArmourRenderer.cs ~222-250). The same loop exists in HeldWeaponRenderer (:198-219). The catalogs hold 236 armour and 211 weapon `Box(...)` parts.
- **Consequence:** (D) A fully dressed player costs tens of draws, repeated for every remote player and for the shadow views that accept them.
- **Fix sketch:** Parts are rigid relative to their attach node, so bake each item into one mesh at load. Put the material, detail and pattern params in vertex attributes or a per-item UBO array indexed by a per-vertex part id. The result is 1 draw per item, or per item and attach point.
- **Effort:** M.

### A12. Shader management: no `#line`, repeated compiles, no hot reload
- **Evidence (V):** `ResolveIncludes` inlines without `#line` directives (Shader.cs:213-243), so a compile error reports merged line numbers (D). `cubeVertex.glsl` is compiled once per program: cube ×2, terrain, enemy ×2, shadowDepth ×2, so 7 times (Render.Shaders.cs:10-32) (D). There is no hot reload (no FileSystemWatcher or reload entry point, V), even though `DisposableRegistry.Replace` already gives build-then-swap semantics (DisposableRegistry.cs:39-49).
- **Consequence:** Slower shader iteration, error lines that are hard to map, and startup compile cost. Variant growth is combinatorial, since every `#define` doubles the program count (MASK already does).
- **Fix sketch:** Emit `#line 1 <n>` / `#line <k> 0` around each include. Compile each stage once and attach the shared vertex shader object to several programs. Add a DEBUG-only watcher on `dream/shaders` that calls `Load*Shader`; uniform blocks and samplers are already re-bound at load. Optionally cache program binaries (`ARB_get_program_binary`, core 4.1, which macOS also has).
- **Effort:** S.

### A13. Small upload and sync inconsistencies
- **Evidence (V):** `UiGlRenderer` writes `BufferSubData` into a stream VBO without orphaning (UiGlRenderer.cs:130). Every other stream path orphans (InstanceMatrixBuffer.cs:84, FxInstanceBuffer.cs:46). FrameLighting is uploaded twice per frame into the same UBO range, the second time while the 17 shadow views that read it are still queued (Render.Shadows.cs:79, Render.Lighting.cs:54).
- **Consequence:** (I) The driver must either version (copy) the buffer or wait. At these sizes it usually copies, so the risk is low, but it is an avoidable implicit sync point.
- **Fix sketch:** Orphan the UI VBO. Give the shadow pass its own UBO range, or a second slot in a ring.
- **Effort:** S.

### A14. Per-frame managed allocations (small)
- **Evidence (V):** `GameWindowTitleBuilder.Build` formats a string every frame, before `ChangedTextSink` de-dupes it (Game.cs:548). Two `Stopwatch.StartNew()` objects per frame (Game.cs:521, 537). HUD panel interpolated strings allocate while the inventory or equipment windows are open (Render.HudPanels.cs:424-667). The render core itself is allocation-free in steady state (V): instance buffers, `ReusableMatrixBuffer`, UBO stack structs, the joint upload through a span, and the precomputed animation tracks (AnimatedModelSkeleton.cs:174-202).
- **Consequence:** About 3 small objects per frame at 144 FPS: GC noise only.
- **Fix sketch:** Use `Stopwatch.GetTimestamp()`. Rebuild the title only when the FPS value changes, once per second.
- **Effort:** S.

## 3. Level-ups

| Technique | Unlocks | Cost / risk | GL version |
|---|---|---|---|
| **Camera UBO and per-draw UBO ring** (A4, A5) | Removes view/proj from ~every bind and the whole MASK-reset choreography, and makes the 17 shadow views cheap to set up | Low risk; infra exists (`FrameLightingBuffer`, alignment query) | 3.1 core |
| **Joint palettes in a TBO, instanced skinning** (A6) | One draw per enemy type and primitive; crowds of wolves | Medium: shader indexing via `texelFetch`; palette ring upload | 3.1 core (TBO) |
| **Arena buffers + `glMultiDrawElementsBaseVertex`** (A9) | Static chunks and static props in a few calls per view; the base for later GPU-driven culling | Medium: sub-allocator, rebuild compaction | 3.2 core |
| **Static shadow caching** (A1) | Static fire tiles and the sun map cost a blit plus dynamic casters only; more casting fires become affordable | Medium: decouple flicker from the shadow origin, invalidation rules | Blit is 3.0; `glCopyImageSubData` is 4.3 (not macOS) |
| **Layered caster rendering** | The 4 faces of a fire in one submission: `gl_Layer`/`gl_ViewportIndex` with instancing ×4 | GS route is slow on some GPUs; the VS-layer route needs `ARB_shader_viewport_layer_array` | GS 3.2; VS layer is an extension (not macOS) |
| **`GlState` shadow cache** (A8) | No `glGet` stalls, no redundant binds, honest "changes" counters | Low; all GL calls must go through it | any |
| **Compact vertex formats** (A10) | ~2.4× less vertex bandwidth for statics, multiplied by 18 views | Low; loader change plus 2 VAO layouts | 3.3 core (`2_10_10_10_REV`, `VertexAttribIPointer`) |
| **Persistent mapped streaming buffers** (`ARB_buffer_storage` + fences) | Zero-copy instance, UBO and UI streams | Only worth it after A1/A9 raise per-frame streaming volume; adds fence management | 4.4 (not macOS) |
| **MDI + compute culling** | GPU-driven submission | Large; a whole rewrite of the draw path | 4.3 (not macOS) |
| **DSA** | Cleaner object setup, no bind-to-edit | Ergonomics only; little perf | 4.5 (not macOS) |
| **Offscreen HDR target and resolve** | Post-processing, tonemap, bloom for the additive FX. Today MSAA resolves implicitly at swap with no FBO (Render.Lifetime.cs:126-130) | Medium; the current albedo×gain look must be re-matched | 3.0 FBO, `RGBA16F` 3.0 |
| **sRGB textures/framebuffer, anisotropic filtering** | Correct lighting maths, and sharper oblique terrain in iso. Today it is `PixelInternalFormat.Rgba` with no aniso (TextureDecoder.cs) | sRGB changes the look and needs the HDR/tonemap step first; aniso is cheap | sRGB 3.0; aniso `EXT_texture_filter_anisotropic` (universal, core 4.6) |
| **Shader hot reload, `#line`, program binary cache** (A12) | Fast iteration, readable errors, faster startup | Low | binary cache 4.1 (macOS OK) |
| **RenderDoc-friendly labels** (`glObjectLabel`, `glPushDebugGroup` per pass) | Captures readable by pass and object | Low; DEBUG-only, KHR_debug is already probed (GLUtils.cs:184-204) | 4.3 / KHR_debug |
| **Adaptive VSync** (swap interval -1, `WGL_EXT_swap_control_tear`) | Smooth pacing without the tearing of VSync-off. VSync-off was a user decision (docs/configuration.md:77), so offer it as an option only | Low | WGL/GLX extension |

## 4. Done well
- `GpuFrameTimer` reads results back ≥2 frames late with `QUERY_RESULT_AVAILABLE`, uses a 4-slot ring and drops a slot rather than stall (GpuFrameTimer.cs:211-260). There is no CPU-GPU sync anywhere in the frame apart from `SwapBuffers` and the capture harness's `ReadPixels`.
- Uniform locations are resolved at link, and the 9 hot setters cache values (Shader.cs:154-163, 369-482). Lookups that return -1 neither upload nor count.
- The lighting, light list, mist and fire-shadow data are std140 UBOs with alignment-correct world and view-model ranges and stack-struct uploads (Render.Lighting.cs:49-80).
- Instance buffers use geometric growth plus orphaning with no managed allocation (InstanceMatrixBuffer.cs:44-87).
- Threading is clear. GL runs only on the render thread, asserted under `[Conditional("DEBUG")]` (RenderThreadAffinity.cs). Models, terrain tiles and chunk meshes decode or build on workers, and uploads are bounded per frame.
- Error handling is solid: the error drain has a fatal latch (OOM, context lost → close), KHR_debug with per-id de-dupe runs in DEBUG, and the delegate is rooted (GLUtils.cs:44, 86-114, 274-295).
- Lifetime discipline is good. Disposal is ordered and aggregated (Render.Lifetime.cs:18-58), `Replace` only swaps after a successful build, and models are owned once by the loader. I found no GL object leak in this area.
- The OPAQUE/MASK program split keeps `discard` out of opaque programs. Its measured benefit is that early-Z is kept (Shader.cs:38-42).
- The static vertex layout is verified against the struct at type init (StaticMeshBuffer.cs:150-166). Animation tracks are precomputed and sampling allocates nothing.
