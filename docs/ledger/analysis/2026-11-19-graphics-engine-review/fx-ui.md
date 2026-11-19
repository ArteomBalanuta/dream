# Effects, HUD/UI and frame budget: adversarial review

Checkout: `G:\workspace\projects\dream\.claude\worktrees\integrate-torch-flame`. Read-only. Tags: **V** = Verified in code, **D** = Derived from code, **I** = Inferred (judgement).

## 1. Area map

| File / type | Role |
|---|---|
| `dream/Rendering/Effects/FxSimulation.cs` (1344 lines) | CPU particle sim: swap-remove pool of 1,888 particles, 128 halos rebuilt each frame from `Render.Lights`, dust torus around the view centre, skill-cast sweeps. Variable dt (render dt clamped to 0.1 s). **V** `FxSimulation.cs:399-427` |
| `Effects/FxInstance.cs`, `FxInstanceBuffer.cs`, `FxRenderer.cs` | 36-byte instance, orphan + SubData upload into a fixed 2,048-slot VBO, one `DrawArraysInstanced` of a 4-vertex strip built from `gl_VertexID`. **V** `FxRenderer.cs:86-126`, `FxInstanceBuffer.cs:46-50` |
| `Client/Scenes/PlayScene.Effects.cs` | `DrawEffectsPass`: war mist, then flash list, sim advance, blob shadows (only when shadow maps are off), write instances, one draw inside the `Effects` GPU section. **V** `:64-103` |
| `Core/Render.Hud.cs` `DrawHUD` | Crosshair, HUD surface selection, classic HUD build and submit, then ImGui NewFrame, Render and draw, every frame. **V** `:364-455` |
| `Ui/ClassicHudSurface.cs`, `Ui/UiBatch.cs`, `Ui/Gl/UiGlRenderer.cs` | Classic HUD: GL-free immediate-mode panel build into `UiBatch` (8,192 quads), then one `BufferSubData` and one `DrawElements` per (clip, page) command. **V** `UiGlRenderer.cs:77-168`, `UiBatch.cs:643-705` |
| `Core/Render.ImGui.cs`, `Core/Render.HudPanels.cs` | Hand-written ImGui GL3 backend and the ImGui HUD (settings, build panel, fallback/admin inventory). **V** |
| `Ui/UiFontSetup.cs`, `UiText.cs`, `UiScale.cs` | Fonts are baked by ImGui's stb_truetype at 13, 26, 24 and 26 px. The classic HUD reads the glyph table out of ImGui native memory and stretches the bakes by `ui_scale`. **V** |
| `Core/GpuFrameTimer.cs`, `RenderCounters.cs`, `Hud/FramePerformanceSnapshot.cs` | Non-blocking `TIME_ELAPSED` ring over 5 sequential sections, plus `PRIMITIVES_GENERATED`, plus thread-static CPU draw, program-bind and uniform counters. **V** |
| `Application/Game.cs` `OnRenderFrame`, `Configuration/FrameRateCap.cs`, `Core/RenderFrameClock.cs` | A single-threaded GameWindow. VSync is off and the cap comes from `UpdateFrequency` (default 144). **V** `Game.cs:122-125`, `FrameRateCap.cs:12` |

**Per-frame flow (V, `PlayScene.Render.cs:31-260`):** RenderCounters capture and reset. GPU timer BeginFrame. Clear. Shadows section. World section (terrain, props). Actors section (enemies, players, economy, sky, boundary void, war mist). Effects section (fx draw). Untimed: debug overlays and the view model. Hud section (crosshair, classic batch submit, ImGui). Then, outside PlayScene and untimed, the loading overlay and pause menu, `SwapBuffers`, and a `glGetError` every N frames (`Game.cs:519-549`, `:783-795`).

**Interpolation (V):** the local body is interpolated between 64 Hz ticks by `LocalPlayerRenderSmoother`, with alpha = `_commandTicks.RemainingSeconds / FixedDeltaSeconds` (`ClientWorldSession.cs:213-222`, `PlayScene.Render.cs:63`). Remote actors render at `EstimatedTick - delay`, with the delay adaptive between 3 and 12 ticks (default 6) (`ServerTickClock.cs:15-60`). Effects and HUD animation run on the render dt.

## 2. Antipatterns, ranked by consequence

### A1. No scene render target, so soft particles, distortion, bloom and post effects are structurally impossible (effort L)
- Evidence (V): `Render.Lifetime.cs:126-130` says the MSAA default framebuffer "is resolved implicitly at SwapBuffers with no FBO". The only `GenFramebuffer` in the codebase is in the shadow maps (`ShadowMapTargets.cs:245`). `FxRenderer.cs:8-14` notes "no offscreen target". The docs list "No HDR target, bloom, tone mapping or soft particles" (`docs/rendering-and-assets.md:91`).
- Consequence (D): the depth buffer cannot be sampled, so every billboard hard-clips where it crosses geometry. Halos need a per-instance `DepthBias` hack toward the camera (`FxInstance.cs:331-332`, `fxVertex.glsl:505-506`). Blobs float 3 cm above the ground and clip on relief terrain. Flames and ground-aligned arcs and rings show a seam line wherever they meet a wall or uneven floor. There is no heat haze, no refraction for the bolt and arc skills, and no bloom; the additive halos are a fake bloom that cannot respond to bright pixels. The visual ceiling of the effects pass is capped here, not by the particle code.
- Fix sketch: render the world into an FBO (an MSAA colour renderbuffer plus a depth texture). Blit-resolve the depth to a single-sample `DEPTH_COMPONENT24` texture before the effects pass (a depth blit from multisample is legal with `NEAREST`). Soft particles then cost one `texelFetch` and one `smoothstep(0, k, sceneDepth - fragDepth)` in `fxFragment.glsl`. The same target is the prerequisite for the tonemap and bloom the known context lacks. GL 3.3 is enough.

### A2. VSync is forced off and the cap is fixed, so tearing and judder are guaranteed (effort S)
- Evidence (V): `Game.cs:122-125`: "VSync stays off", with `UpdateFrequency = frameCap`. The default cap is 144 (`FrameRateCap.cs:12`) and there is no VSync setting anywhere (`git grep VSync` hits only that comment).
- Consequence (D): on a 60 Hz panel, 144 fps delivers 2.4 frames per refresh, so the image tears every frame and each displayed frame advances by an uneven amount of time (judder). This is most visible exactly where the code works hardest for smoothness: the interpolated local pose and the isometric camera follow. The sleep-based cap (I: GameWindow's timer, granularity depends on the OS timer resolution) adds frame-time jitter of its own. The presentation interpolation is correct, but the presentation itself undoes it.
- Fix sketch: add a VSync setting (Off / On / Adaptive). OpenTK `VSync = VSyncMode.Adaptive` maps to swap interval -1 through `WGL_EXT_swap_control_tear`. Default it to On or Adaptive, and keep the cap only for Off. No GL version requirement.

### A3. Profiling cannot see hitches, has GPU blind spots and no GPU debug annotations (effort S to M)
- Evidence (V):
  - The perf log prints one single frame's numbers once per second (`PlayScene.Diagnostics.cs:27-39`), and `TotalFrameMilliseconds` is the last frame only (`PlayScene.Render.cs:195`). There is no max, percentile or histogram anywhere (git grep for Percentile, p99, Hitch and similar finds nothing), and there are no GC collection or allocation counters.
  - `GpuFrameTimer` has 5 sequential sections that cannot nest or reopen (`GpuFrameTimer.cs:5-12`, `BeginSection` early-returns if `Issued`). The debug overlays, view model, loading overlay and pause menu are untimed (`PlayScene.Render.cs:112-115` comment, `Game.cs:524-526`). The sky and war mist are folded into "Actors". There is no total GPU frame time, so GPU-bound and CPU-bound frames cannot be told apart.
  - `KHR_debug` output is installed only in DEBUG builds with a debug context (`Game.cs:109-111`, `:331-336`). There are zero `glPushDebugGroup` or `glObjectLabel` calls (git grep), so a RenderDoc or Nsight capture shows hundreds of anonymous draws and numbered buffers.
  - There is no shader hot reload (no `FileSystemWatcher`).
- Consequence (D): a 30 ms hitch once per second is invisible both in the log and in the overlay. The docs' "Current Known Gaps" (first-visibility spikes, `docs/performance-and-optimizations.md:271-277`) are exactly the class of problem these tools cannot see. Shader iteration (most of the recent work is shader tuning: mist, gems, equipment) needs a full restart.
- Fix sketch: see L1 to L4 below. All of it is available on GL 3.3.

### A4. Classic HUD text is a stretched bitmap, Latin-1 only, and coupled to ImGui's native memory layout (effort M)
- Evidence (V):
  - There are four fixed bakes (`UiFontSetup.cs:18-21`). `ui_scale` moves in 0.25 steps from 1 to 4 (`UiScale.cs:5-7`). `UiText` picks the nearest bake and scales it (`UiText.cs:62-66`). Glyph quads are rounded per glyph (`UiBatch.cs:564-577`), and the font texture is Linear with no mips (`Render.ImGui.cs:207-210`).
  - Only codepoints 32 to 255 are copied (`UiFontSetup.cs:153`, `UiGlyphTable.FirstCodepoint/LastCodepoint`). Player names and chat (`ChatPanel.cs:89-90`) outside Latin-1 draw as `?`.
  - Glyphs are read by raw pointer from ImGui 1.91.6's native struct, pinned by a version string (`UiFontSetup.cs:25-27`, `:146-169`).
- Consequence (D):
  - At 1.75x or 2.25x and similar scales, Cinzel and Garamond are resampled bilinearly: soft edges, uneven stem widths from independent rounding, and shimmer when numbers change. This undercuts the gothic HUD art direction, where engraved capitals must look crisp.
  - Any ImGui.NET bump makes `BuildGlyphTable` throw. The HUD then latches to the ImGui fallback (see A5), so a package update silently removes vendor, trade and chat.
  - Outline, emboss and glow text effects need extra quads (`UiText.ShadowPx`, `UiText.cs:140`) instead of being free in the shader.
- Fix sketch: generate an MSDF atlas offline with msdf-atlas-gen, covering Latin, Latin Extended and Cyrillic (Cinzel and Garamond support them). Load its JSON with no ImGui dependency, and add one `uiFragment` mode (median(r,g,b), screen-pixel-range AA, optional outline and inner-shadow parameters in the rim bytes). Text is then crisp at every `ui_scale` with one bake per face, and the classic HUD no longer depends on ImGui at all. GL 3.3 is enough. Lower-effort alternative: FontStashSharp, which rasterises at the exact pixel size on demand.

### A5. Two HUDs, and the fallback cannot play the game (effort M)
- Evidence (V):
  - The ImGui HUD is the automatic fallback and the F3 admin view (`docs/rendering-and-assets.md:297`; latch in `Render.Hud.cs:181-190` on atlas, shader or font failure, an exception, or loading past 5 s).
  - The ImGui HUD duplicates the inventory, equipment, tooltip and context menus (`Render.HudPanels.cs:408-677`, about 270 lines, mirroring `Ui/Panels/InventoryWindow.cs` and `ItemOverlays.cs`). It also has its own combat-text and world-label projection with "its own offsets" (`Render.Hud.cs:457-540` against `:317-356`; docs line 327).
  - It has no vendor, trade, chat, quickbar, dialogue, party or quest UI: `grep -iE 'vendor|trade|chat|quickbar|dialogue|party|quest' Render.HudPanels.cs` finds nothing.
  - ImGui `NewFrame` and `Render` run every frame on both surfaces (`Render.Hud.cs:450-451`, `:646`) for Settings and Build.
- Consequence (D): every inventory change has to be made twice or the ImGui copy rots, which it already has for every newer system. A latch from a corrupt atlas or a glyph-layout throw (A4) leaves a player unable to buy, trade or chat, with only a `hud-classic` log line to explain it. The duplication buys a fallback that is not a working game.
- Fix sketch: make ImGui a dev/debug layer only (settings, build, perf overlay, admin inspectors, the debug tooling in L2 and L4). Replace the fallback with a visible error plus a retry of the classic surface, and delete the ImGui inventory and combat text. ImGui per-frame cost is small (I) and is not the reason to do this; the reasons are maintenance and correctness.

### A6. Per-pass glGet state snapshots (effort S to M; measure first)
- Evidence (V): `ImGuiGlStateSnapshot.Capture` makes 15 `glGet*`/`glIsEnabled` calls (`Render.ImGui.cs:105-137`). It runs twice per frame, once for the classic submit (`UiGlRenderer.cs:102`) and once for ImGui (`Render.ImGui.cs:254`). `FxRenderer.Draw` queries `IsEnabled(CullFace)` (`FxRenderer.cs:96`).
- Consequence (I): on drivers with a threaded GL front end (NVIDIA "threaded optimization", some AMD) each `glGet` can force a round trip to the driver thread, adding tens of microseconds each. The counters cannot show this (`RenderCounters` counts no queries). Total cost is probably under 0.5 ms, but it is avoidable and invisible.
- Fix sketch: either add a tiny CPU-side `GlState` cache that `Enable`, `Disable`, `BlendFunc`, `UseProgram` and `BindVertexArray` go through, or adopt the contract the world pass already uses ("each pass sets what it needs", `Render.Lifetime.cs:123-124`) and drop the snapshot.

### A7. The particle atlas is full, flipbooks are hard-cut, and the visual vocabulary is capped (effort S to M)
- Evidence (V): `gen_particles.py:8` "no cell is reserved", and frame 31 is the trail (`FxInstance.cs` `Trail = 31`), so all 8x4 cells of 64 px are used. The flame is a 4-frame cycle at 12 Hz, `((int)(_clock*12f+i)) & 3` (`FxSimulation.cs:657`), and the shader rounds the frame, `floor(frame+0.5)` (`fxVertex.glsl:508`), so there is no inter-frame blend. Every other family picks one random static frame (`FxSimulation.cs:539, 914, 1204, 1300`).
- Consequence (D): "no flipbooks by design" means smoke, explosions, magic swirls and fire cannot evolve over a particle's life. Each effect is a stretched or rotated static sprite, and torch flames step visibly at 12 Hz. Any new effect needs an atlas re-layout plus a shader constant change (`AtlasColumns`/`AtlasRows` are hard-coded in `fxVertex.glsl:490-491`).
- Related ordering issue (D): sorting is unnecessary for the additive particles, which the code handles well. Occluding ash and ground quads are drawn first (`FxSimulation.cs:441-457`) with depth writes off, so an additive spark behind the ash still adds on top of it. This is minor at current densities.
- Fix sketch: move to a `sampler2DArray` (GL 3.0), one layer per family, so cells can grow to 128 px and new effects need no re-layout. Add per-particle flipbook playback (frame = start + life01 * count) with a `fract` crossfade (two taps) in the shader.

### A8. Leftover per-frame allocations on the frame path (effort S)
- Evidence (V): `Stopwatch.StartNew()` twice per frame (`Game.cs:521, 537`). The PlayScene alloc tests already replaced this exact pattern elsewhere (`PlaySceneFramePathAllocationTests.cs:14-15`). The window title string is built every frame and only deduplicated after allocation (`Game.cs:548`, `GameWindowTitleBuilder.cs:9-11`). A `new Camera(...)` is created per frame by design (`ClientWorldSession.cs:229`). The ImGui inventory builds 3 to 4 strings per slot per frame while open (`Render.HudPanels.cs:478, 483, 576, 596, 624`).
- Consequence (D): a steady gen0 churn of a few hundred bytes per frame (several KB with the ImGui inventory open). It stays harmless only as long as nothing else promotes, and it contradicts the zero-allocation gates the rest of the frame path holds.
- Fix sketch: use `Stopwatch.GetTimestamp` deltas, rebuild the title only when FPS changes (once per second), reuse a scratch camera, and leave the ImGui strings alone if A5 deletes that path.

### A9. A clock zoo with different wrap policies (effort S)
- Evidence (V): `RenderFrameClock` is a float wrapped at 4096 s (`RenderFrameClock.cs:7-13`) and drives `emissiveUvScroll * uTime` (`cubeFragment.glsl:432`, `enemyFragment.glsl:373`). The boundary mist wraps at 6400 s, the HUD gem animation at 840 s (docs line 321), and FX uses `_clientPresentationTimeSeconds` (`PlayScene.Effects.cs:83`).
- Consequence (D): a scrolled emissive texture jumps every 68 minutes unless `scroll * 4096` is an integer, and nothing enforces that, unlike the mist, which has tests for its rates. Each new time-driven effect has to choose its own wrap.
- Fix sketch: upload `fract(scroll * t)` computed in double on the CPU per material, or give every material a period divisible into a single documented wrap, and add a test.

### A10. ImGui backend edge cases (effort S, low priority)
- Evidence (V): `RendererHasVtxOffset` is not set and `pcmd.VtxOffset` is ignored (git grep finds neither). `glBufferData` reallocates for each command list (`Render.ImGui.cs:283-292`). The blend is `SrcAlpha` while the classic HUD uses premultiplied (`:256` against `UiGlRenderer.cs:106`).
- Consequence (D): a single ImGui draw list over 65,535 vertices (a large table in an admin inspector) silently corrupts in release builds. Today that is fine.

## 3. Level-ups

| # | Technique | What it unlocks | Cost / risk | GL |
|---|---|---|---|---|
| L1 | **`glQueryCounter(GL_TIMESTAMP)` markers** in place of the 5 `TIME_ELAPSED` sections, kept in the same non-blocking ring | Any number of nested named GPU scopes (sky, war mist, debug, view model, overlays), a true total GPU frame time, and per-pass costs in the overlay | S, low risk; reuses the `IGpuQueryApi` seam and its tests | 3.3 core (ARB_timer_query) |
| L2 | **Frame-time ring buffer** (last 1,000 frames: CPU total, swap, GPU total) with p50, p95, p99 and max, plus `GC.CollectionCount(0/1/2)` deltas and `GC.GetAllocatedBytesForCurrentThread()` per frame, shown as an ImGui `PlotLines` graph and written into the perf log | Hitches become visible and attributable, and allocation regressions show in-game rather than only in tests | S | none |
| L3 | **KHR_debug annotations**: `glPushDebugGroup`/`glPopDebugGroup` around every pass (same names as L1, so one `using var _ = GpuScope("Effects")` drives both) and `glObjectLabel` on programs, VAOs, buffers and textures | RenderDoc and Nsight captures become navigable (named passes, named resources) | S; guard with the existing extension probe (`GLUtils.cs:197`), since macOS GL 4.1 has no KHR_debug; make the calls compile out or no-op when absent | 4.3 core, or the extension on 3.3 (all Windows drivers) |
| L4 | **RenderDoc in-app API** (P/Invoke `RENDERDOC_GetAPI` from `renderdoc.dll` if it is loaded) with a hotkey or `DREAM_RENDERDOC_CAPTURE=frameN` to trigger a capture | Scripted captures from the existing capture harness (`DREAM_AUTO_CONNECT`); captures of the exact hitch frame | S | 3.2+ core, already satisfied |
| L5 | **Tracy** (`Tracy-CSharp` bindings or the native client over P/Invoke) with CPU zones in `PlayScene.Render` and Update, GPU zones through L1 timestamps, and plots for particle count, draw calls and GC | A timeline of CPU and GPU together, including the 64 Hz sim, net receive and background mesh threads; this is what makes A3 class issues trivial | M (native DLL, zone discipline); keep it out of release builds | 3.3 (GPU zones use timestamps) |
| L6 | **Shader hot reload** (DEBUG): `FileSystemWatcher` on `dream/shaders`, recompiled on the render thread, with the old program kept on compile failure. Needs `Shader` to publish a "relinked" event so caches like `FxRenderer._locations` and the `UniformNames` contract re-resolve | Seconds instead of minutes per iteration on mist, gem, equipment and fx tuning, which is most current work | M, because of the `#include` dependency graph (`lighting.glsl` and `boundaryMist.glsl`) and the cached locations | none |
| L7 | **Scene FBO plus resolved depth texture** (A1) and then **soft particles** | Removes the halo depth bias, the blob and ground seams and the flame clipping; prerequisite for bloom, tonemap and heat haze | L across the renderer; one MSAA resolve blit per frame (about 0.1 to 0.3 ms at 1440p, I) | 3.3 |
| L8 | **MSDF text** (msdf-atlas-gen offline plus one `uiFragment` mode) (A4) | Crisp text at every `ui_scale`, free outline and emboss for the gothic look, Cyrillic and extended Latin, no ImGui dependency for the classic HUD | M | 3.3 |
| L9 | **`sampler2DArray` particle atlas with life-driven flipbooks and crossfade** (A7) | Evolving smoke, fire and magic; new effects without re-layout | S to M (art pipeline in `gen_particles.py`) | 3.0 |
| L10 | **VSync / adaptive VSync setting** (A2) | No tearing, even frame delivery | S | WGL/GLX swap control, any version |
| L11 | UI pages as a texture array | Collapses the classic HUD to about one draw per clip rect | Low value: there are already only 12 to 16 draws (docs line 312); skip unless the page count grows | 3.0 |

Not recommended now: persistent-mapped buffers (ARB_buffer_storage, GL 4.4, absent on macOS) or MDI. The fx upload is 73 KB per frame into one draw and the HUD is 12 to 16 draws, so there is nothing for them to win in this area. GPU-simulated particles (transform feedback or compute) are not justified at 1,888 particles; CPU simulation is cheap and keeps it testable.

## 4. Done well
- The effects pass is lean: one instanced draw, corners from `gl_VertexID`, an orphaned fixed-capacity VBO, cached uniform locations, and zero allocations backed by a test (`FxAllocationTests.cs`).
- Premultiplied `(One, OneMinusSrcAlpha)` unifies additive light (A = 0) and occluders without sorting, and fog multiplies alpha so particles fade correctly (`fxFragment.glsl:538`).
- The particle atlas is premultiplied offline with 4 px zero margins, so the mips do not bleed (`gen_particles.py:6`).
- The view basis comes from the actual view matrix, not the fog origin; this is a real isometric pitfall avoided.
- `GpuFrameTimer` is a correct non-stalling query ring with dropped-slot accounting and a test seam. `RenderCounters` is thread-static and cheap.
- The classic HUD has a GL-free core enforced by a source test, one upload per frame, command merging by clip and page, gems and additive spill in the same program, and a zero-allocation build. `HudText` uses a custom interpolated-string handler.
- GL state is restored after every pass, failures latch with one `RenderFaultLog` line instead of crashing, and the glGetError check is throttled to every N frames.
- Local interpolation between ticks and an adaptive remote interpolation delay are the right design for a 64 Hz authoritative sim.
