# Graphics engine review: antipatterns and level-ups

Date: 2026-11-19. Snapshot: master `a0248ce6`. This is a read-only adversarial review split across five areas. The top
claims were re-checked against the code. The detail reports, with `file:line` evidence, are next to this file:

- [core](core.md): GL state and draw submission.
- [lighting](lighting.md): passes, colour, lights, shadows, sky and fog.
- [world](world.md): terrain, actors, props, textures and skinning.
- [fx-ui](fx-ui.md): effects, HUD, frame pacing and profiling.
- [tech](tech.md): graphics API options and libraries.

## Verdict

Where the renderer has been worked on, it is careful and well tested. Effects allocate nothing per frame, nothing stalls the GPU, there are no GL leaks, every pass restores GL state, and the interpolation between simulation ticks is sound. Its limits are structural, not bugs:

1. **There is no scene colour target.** Everything is drawn forward, in gamma space, straight into the MSAA backbuffer,
   and hard-clipped at 1.0. That single decision sets the quality ceiling. It is why lit colours must stay at or below
   0.40 and why nights band. It also makes bloom, soft particles, tonemapping, colour grading and screen-space AO
   impossible to add.
2. **The scene is submitted by brute force.**
   - Shadows submit every caster up to 17 times per frame (the sun, plus 4 fires × 4 faces), and nothing is cached.
   - Actors draw one glTF primitive at a time and re-upload the skinning palette for each primitive.
   - Per-draw uniforms are looked up by string, and props are not instanced.

   The ledger measured 301 draws with shadows off and about 1,510 with them on (`docs/ledger/wave-shadow-maps.md`).
   The frame is limited by CPU and draw calls, which is why the fire count is capped at 4.
3. **The renderer cannot be inspected.** There are no debug labels or groups, no RenderDoc hook and no shader hot
   reload. GPU timers cover only 5 sections, the perf log samples one frame per second, and shader compilation is only
   checked on the test machine's own driver.

The graphics API is not the problem. Nothing in the code needs more than GL 3.3. Every level-up below is reachable on
OpenGL: most on 3.3, and the rest on 4.3–4.5, which every Windows driver offers.

## Antipatterns, ranked by consequence

| # | Antipattern | Evidence (verified) | Consequence | Fix | Effort |
|---|---|---|---|---|---|
| 1 | The pipeline is LDR and gamma-space, drawn forward with no scene target: no sRGB, no HDR, no tonemap, no dither | `Render.Lifetime.cs:126-130`; textures use unsized `PixelInternalFormat.Rgba` (`TextureDecoder.cs:66,100,186`) | The 0.40 colour ceiling. Nights band because they use only 8-bit values 2–12. No post effects are possible. Lighting maths is wrong when lights mix. | An RGBA16F scene FBO with a resolved depth texture, sRGB textures, and tonemap plus dither on output | M, plus retuning colours and re-recording golden baselines |
| 2 | Shadows re-submit the whole scene up to 17 times per frame, uncached | `Render.Shadows.cs:54-97`, `ShadowCasterPass.cs:81-114`, `:192-195` | About 5× the draw calls. Fires are capped at 4. One hound can cost about 476 draws. | Cache the static layer of the sun map and of the brazier tiles, and redraw only dynamic casters. First decouple the fire shadow origin from the flicker. | M |
| 3 | Night frames render a shadow map nobody reads | `Render.Shadows.cs:103` picks the moon, but `clouds.glsl:81-82` applies shadow visibility only to the sun term, which is 0 at night | Every night frame wastes a full caster pass and 9 taps per pixel | Shadow the moon term, or skip the pass when the sun is down | S |
| 4 | Actors draw per primitive, with the joint palette uploaded per primitive; the 64-joint cap falls back silently to the bind pose | `AnimatedModel.cs:585-592`, `AnimatedModelData.cs:226-231` | A hound costs 28 draws and 28 palette uploads per view, and skinned actors can never be instanced | Merge primitives that share a material at import, and upload the palette once per actor per view. Later, a texture-buffer palette for instanced skinning. | S–M |
| 5 | Per-draw uniform traffic. There is no camera UBO; about 17 material uniforms per primitive go through string-keyed lookups; sampler units are re-set every draw. Each world shader is two GL programs, which need hand-written uniform resets. | `core.md` A2, A4, A5 | CPU cost on every draw, and a recurring bug class that tests have to police | A camera UBO plus a per-draw and material UBO ring that both program variants read | M |
| 6 | glTF PBR data is loaded and thrown away. Normal, metallic-roughness and occlusion maps and tangents are uploaded but never sampled, and box mips are built in scalar C# on the render thread. | No `normalTexture` in `dream/shaders` (searched); `TextureDecoder.cs:97-125` | Wasted VRAM and load time, and hitches the first time a model appears | Either sample them (normal maps are the cheapest visual upgrade for the sculpted gear) or stop loading them. Build mips with `glGenerateMipmap` or offline. | S to stop loading, M to use them |
| 7 | Textures are uncompressed, use unsized formats, and have no anisotropic filtering | `TextureDecoder.cs`; `MaxAnisotropy` appears nowhere | The ground blurs at the 45–50° isometric angle; the `animations` folder is 475 MB | Anisotropic filtering now (S); BC7 in KTX2 later (M) | S / M |
| 8 | VSync is hard off, with a 144 fps sleep cap | `Game.cs:122-125`, `FrameRateCap.cs` | Tearing and judder on 60 Hz displays, which undoes the correct interpolation | An Off / On / Adaptive setting, defaulting to Adaptive | S |
| 9 | A hard 16-light cap, chosen nearest-first with no fade; fire-shadow slots switch on and off | `LightList.cs:18` | Light pools and shadows pop as the player moves | Fade lights by rank (S); later, clustered lights in an SSBO with compute culling on GL 4.3 (M) | S / M |
| 10 | Terrain is drawn double-sided with no depth prepass, for a reason (first-person cameras) that no longer exists | `Render.Terrain.cs:107`; `GameSettings.cs:224` forces isometric | Overdraw on the most expensive shader | Back-face culling. With the fixed camera, only the +X, +Y and +Z faces are ever visible. | S |
| 11 | Dead or invisible work. The sky pass is skipped in isometric, which is the only view. Distance fog starts beyond the visible ground, yet every fragment pays for it. FrameLighting is uploaded twice. | `lighting.md` A7–A10 | Wasted cost and confusion; the frame order in the docs is out of date | Remove or retune them, and update `docs/rendering-and-assets.md` | S |
| 12 | Text is a stretched bitmap covering Latin-1 only, read from ImGui's native memory pinned to 1.91.6. The ImGui fallback HUD duplicates panels but cannot play the game. | `fx-ui.md` 4–5 | Text blurs at any non-native scale, non-Latin names show as `?`, and an ImGui upgrade silently breaks the HUD | An MSDF text atlas (msdf-atlas-gen) with one shader mode; keep ImGui as a dev tool only | M |
| 13 | No graphics debugging. There is no `glObjectLabel` or `glPushDebugGroup`, no RenderDoc hook, no shader hot reload and no `#line` mapping, and debug output exists only in DEBUG builds. | `tech.md` A5, `fx-ui.md` 3 | Every visual bug is found from screenshots and guesswork | KHR_debug labels and groups, the RenderDoc in-app API, and a shader file watcher | S |

## Technology: stay on OpenGL, raise the version, add libraries

| Option | Verdict | Why |
|---|---|---|
| OpenGL 4.5/4.6 core. `Game.cs` never asks for a version today, so it gets 3.3. | **Do now** | It unlocks SSBOs, compute shaders (light culling, particles), multi-draw indirect, persistent mapped buffers, direct state access and `layout(binding)`, with the same code and tests. It ends any macOS hope (macOS stops at 4.1), but macOS is already blocked by BulletSharp and the audio code. |
| D3D11 through Vortice.Windows, behind a narrow backend seam | **Later, only if needed** | It would give PIX, a DXGI swapchain for HDR displays and more robust drivers on players' machines. There are 707 `GL.*` call sites in 78 entry points, 418 of them outside `Rendering/Core`, so the seam has to be built first. |
| Vulkan, D3D12, WebGPU, Veldrid, bgfx, Diligent | **No** | Each is 4–8 weeks of work to get back to today's picture, with no new capability on day one. An explicit graphics API is the wrong complexity for one human plus agents. |
| Engine migration (Godot C#, Stride, MonoGame/FNA) | **No** | It throws away the server-authoritative simulation integration and about 9,800 tests for features the game does not need. |
| Silk.NET instead of OpenTK | **No, not for its own sake** | It is the same OpenGL underneath. |

Libraries that slot in without changing the API:

- **RenderDoc** (its in-app API) and **Tracy** (CPU and GPU zones), for capture and profiling.
- **glslangValidator** as a test step. A shader that one vendor's driver accepts but another's rejects then fails in CI.
- **KTX2 / Basis Universal** (or `toktx`/`basisu` in the Python pipeline), for BC7 textures.
- **meshoptimizer**, to merge primitives and to reorder and quantise vertices. Every glTF vertex is 76 bytes today.
- **msdf-atlas-gen**, for sharp text at any scale.

## Recommended order

1. **Quick wins (days, all on GL 3.3):**
   - Skip or fix the night shadow pass.
   - Add the VSync setting.
   - Turn on anisotropic filtering and use sized texture formats.
   - Back-face cull the terrain.
   - Stop uploading the unused maps.
   - Add debug labels and groups, and a RenderDoc hook.
   - Add a glslangValidator step.
   - Request a 4.5 context, with a 3.3 fallback.
2. **HDR scene target, the level-up that changes how the game looks:**
   - Core: an RGBA16F target, sRGB textures, a tonemap and dithering.
   - Then: bloom (fire, spells, the Cleave trail), a colour-grading lookup table for the gothic mood, soft particles,
     and rim light for actor legibility.
   - This needs the albedo contract, `enginelook.py` and the golden baselines retuned.
3. **Cut the draw-call load:**
   - Changes: camera and material UBOs, primitive merging at import, one palette upload per actor, instanced props, and
     cached static shadows.
   - Target: shadows on should cost well under 2× shadows off, and allow more than 4 fires.
4. **Lighting depth:** faded or clustered lights (SSBO on 4.3+), normal maps on equipment, vertex AO baked into the
   chunk meshes, and a two-clip animation crossfade.
5. **Text and UI:** MSDF text, with ImGui demoted to dev tooling.

The existing tools can measure each step:

- draw counts from `RenderCounters`;
- GPU section timings from `GpuFrameTimer`;
- pictures from the capture harness and `tools/golden`.
