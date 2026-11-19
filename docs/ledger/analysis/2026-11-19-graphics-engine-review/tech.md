# Dream renderer review: technology and API strategy

Reviewer area: how tightly the code is tied to raw OpenGL, what moving to another API or adding one would cost, and what to adopt.
Checkout: `G:\workspace\projects\dream\.claude\worktrees\integrate-torch-flame` @ a0248ce6 (master).
Labels: **V** = Verified (seen in code), **D** = Derived (follows from the code), **I** = Inferred (judgement).
For library versions, "unsure" means I could not confirm the exact current version from here. Check NuGet before you pin anything.

---

## 1. Area map: the measured coupling

| Measure | Value | Label |
|---|---|---|
| Files importing `OpenTK.Graphics.OpenGL4` | 42 (40 product + 2 test) | V (`git grep -l`) |
| ...outside `dream/Rendering/Core` | 28 files: Mist 5, Ui/Gl 4, Actors 3, Shadows 2, Lighting 2, Effects 2, Client/Startup 2, Sky, Models, ViewModels, Scenes, Menu, `Application/Game.cs` | V |
| `GL.*` call sites in `dream/` | **707** total: 289 in Core, **418 outside Core** | V |
| Heaviest files outside Core | `Shadows/ShadowMapTargets.cs` 66, `Client/Startup/StartupMenuRenderer.cs` 45, `Ui/Gl/UiGlRenderer.cs` 43, `Models/AnimatedModel.cs` 37, `Mist/WarMistRenderer.cs` 26, `Effects/FxRenderer.cs` 24, `Effects/FxInstanceBuffer.cs` 19 | V |
| Distinct GL entry points used | 78. Top: BindBuffer 52, TexParameter 48, BindVertexArray 48, BindTexture 37, Enable 35, GetInteger 32, Uniform* 30, ActiveTexture 29 | V |
| GL 4.x features used | none. The only version probe is `GLUtils.SupportsDebugOutput` (`GLUtils.cs:184-203`), which looks for KHR_debug | V |
| Requested context | `Game.cs:101-113` sets samples, size, title and `ContextFlags.Debug` (DEBUG builds only). It never sets `APIVersion` or `Profile`, so it gets OpenTK's default 3.3 core | V / I (default) |
| Shaders | 24 files, 1,897 lines in `dream/shaders`. 19 have `#version 330 core`; 5 are include-only. 15 programs, MASK and TERRAIN variants included (`ShaderCompileTests.cs:17-34`) | V |
| GLSL shape | 64 loose `uniform` declarations; 4 `layout(std140)` blocks; `layout(location=N)` on attributes; `uniform mat4 jointMatrices[64]` (`cubeVertex.glsl:18`). Nothing needs more than 3.3, and nothing is hard to port: no extensions, no subroutines, no `layout(binding)` | V |
| Shader preprocessor | Custom one-level `#include` and define insertion (`Shader.cs:79-90, 192, 213`) | V |
| Render-hardware abstraction | **None.** There are three narrow seams, each built for one purpose: `IWorldInstanceDrawTarget` (`World/WorldRenderer.cs:235`, used to swap in the shadow depth pass and a test recorder), `IClassicHudBackend` / `IUiTextureUploader` (UI: `UiBatch` → `UiGlRenderer`), and `IGpuQueryApi` (`Core/GpuFrameTimer.cs:17`) | V |
| Tests that need a GPU | 2 `[GlFact]` tests, both in `ShaderCompileTests.cs` (compile and link every program, plus a negative test), through `HiddenGlContext`. `DREAM_GL_TESTS=0` skips them (`Support/GlFactAttribute.cs`) | V |
| Tests that need no GPU | about 2,267 Fact/Theory under `dream.Tests/Rendering` (pure maths: SkyModel, LightSelection, FxSimulation, WarMistField and others). **43 test files read `.glsl` text** to pin constants and layout to their C# twins (for example `FrameLightingContractTests`, `WarMistStructuralTests`) | V |
| Non-GL coupling to OpenTK | `OpenTK.Mathematics` in 320 files, 114 of them in Simulation/Server/Multiplayer. `OpenTK.Windowing` in 20 files. ImGui.NET in 9 files, with a hand-written GL backend (`Render.ImGui.cs`, 66 GL calls) | V |
| Size of the rendering code | 274 `.cs` files, about 44k lines under `dream/Rendering` | V |
| Platform | Windows only in practice: `NAudio.WinMM` (Windows-only audio) and `BulletSharp` 0.11.1 with a bundled native `libbulletc` (`dream.csproj`, `Directory.Packages.props`). There is no macOS native for Bullet, and macOS caps GL at 4.1 (deprecated) | V / known |
| Textures | All RGBA8 / R8 / RG8; 6 sites use the unsized `InternalFormat.Rgba`; no compressed formats; mips from `GL.GenerateMipmap` (`Core/TextureDecoder.cs:84,128`); no anisotropic filtering anywhere. `dream/animations` is 475 MB on disk | V |
| GPU tooling hooks | `GpuFrameTimer` timer queries and `RenderCounters` (draws, program binds, uniform uploads). There is **no** `ObjectLabel` / `PushDebugGroup`, no RenderDoc in-app API, and no Tracy | V |

**Per-frame flow, as far as it bears on API strategy.** `PlayScene.Render` clears (`PlayScene.Render.cs:54-59`). Then `WorldRenderer` draws the batched instances through `IWorldInstanceDrawTarget` → `Render.Terrain.cs` → `AnimatedModel.RenderInstanced` (`DrawElementsInstanced`, one per primitive). Then come actors (per-draw `UniformMatrix4` for the joints, `AnimatedModel.cs:605`), the sky, war mist, fx, overlays, the view model, and finally the classic HUD and ImGui. Every feature renderer issues its own raw GL, then saves and restores the state it changed by querying it (`GL.IsEnabled(CullFace)` in `FxRenderer.cs:96`, `BoundaryMistVoidRenderer.cs:61`, `WarMistRenderer.cs:92`; `GetInteger(TextureBinding2D/UnpackAlignment)` in `WarMistNoiseTexture.cs:39-40`, `ExplorationTexture.cs:57`). Four UBO wrappers repeat the same post-link `GL.UniformBlockBinding` dance (`FrameLightingBuffer.cs:53`, `LightListBuffer.cs:45`, `BoundaryMistBuffer.cs:44`, `FireShadowListBuffer.cs:45`).

**Bottom line on coupling (D).** The coupling is wide but shallow. 707 call sites over 78 entry points means GL is spread across about 45 files, but each file uses it in the plain GL 3.3 bind-and-draw style, with no exotic features. The shaders are small (1.9k lines) and portable. The real costs of switching APIs lie elsewhere:
- **Shaders as test fixtures.** 43 test files pin GLSL text.
- **Two hand-written UI back ends.** ImGui (`Render.ImGui.cs`) and the classic HUD (`UiGlRenderer`).
- **No device or state layer.** Every renderer would have to be rewritten, not re-pointed.

---

## 2. Antipatterns, ranked by consequence

### A1. GL 4.6 is available on every Windows target, but the context is 3.3 and the code uses only 3.3
- **Evidence.** `Game.cs:101-113` has no `APIVersion` or `Profile`, so OpenTK's default 3.3 applies (V/I). There are zero 4.x entry points (V, histogram above). The docs record what this costs: "GL 3.3 has no `layout(binding)` on a block, so after link `Shader` calls `FrameLightingBuffer.BindProgramBlock`" (`docs/rendering-and-assets.md:53`).
- **Consequence.**
  - **Compute shaders and SSBOs.** Without them, point lights stay capped at 16 in a UBO (`LightList.cs:18`, `MaxLights = 16`). In a night-driven game where torches and lamps "carry the night" (docs:78), that cap decides which lights pop in and out. Particle simulation stays on the CPU (`FxSimulation`, 1,888 particles).
  - **Draw submission.** Without MDI, base-instance, or persistent mapping, every draw remains a CPU-side bind-and-draw.
  - **Debug labels.** KHR_debug only arrives as an extension, and nothing uses labels anyway.
  - All of this is available today on the only platform the game ships on (D).
- **Fix sketch.**
  - Request `APIVersion = new Version(4,5)` and `Profile = ContextProfile.Core`.
  - On failure, show one clear error. Optionally fall back to 3.3 with features gated.
  - Leave the shaders at `#version 330`. GL allows programs of different versions in one context, so they can move to 450 one at a time.
- **Effort.** S.

### A2. No device or state layer: every renderer owns raw GL and saves and restores state by hand
- **Evidence.** 418 call sites in 28 files outside Core (V). Save and restore happen through `IsEnabled` and `GetInteger` queries in Fx, Mist and Sky (V, cited above). GL.Enable/Disable appear 60 times and GetInteger 32 times (V).
- **Consequence.**
  - **State-leak bugs.** Each new pass must remember to restore every piece of state it touched. The war-mist, fx and void renderers each carry the same culling and blend restore code (D).
  - **Cost of any second API.** A migration has to touch all of these files. There is no seam to put a second backend behind (D).
  - **Query cost.** `glGet*` on state usually costs little on Windows drivers, but it is not free on all drivers. It is also redundant when the engine already knows the state (I).
- **Fix sketch.** Do not build an RHI. Build a small `GpuState` cache in Core:
  - blend, depth and cull state, the bound program, VAO, and texture units;
  - the `Set*` calls skip work when nothing changed;
  - each pass gets a `using var _ = state.Push(PassState.AdditiveNoDepthWrite)`.
  This removes the restore-by-query code and becomes the natural home for DSA helpers. Later it is also the seam a second backend would sit behind.
- **Effort.** M. It can be done incrementally, one renderer at a time.

### A3. Per-primitive VAOs and buffers, loose uniforms, and joints uploaded per draw block MDI and batching
- **Evidence.** `AnimatedModel.CreateBuffers` gives every glTF primitive its own VAO, VBO and EBO (`AnimatedModel.cs:212-249`, V). There are 64 loose uniforms set through `Shader.Set*` (`Shader.cs:327-552`, V). The skinned joint palette is uploaded per draw through `glUniformMatrix4fv` into `jointMatrices[64]` (`AnimatedModel.cs:605`, `cubeVertex.glsl:18`, V).
- **Consequence.**
  - The draw count scales with primitives × materials. Each draw needs a VAO switch plus uniform uploads (D).
  - MDI and `DrawElementsBaseVertex` batching are impossible without a shared vertex and index arena (D).
  - The joint limit is fixed at 64 by the uniform array (V).
  - At the current scene scale this is probably not the frame-time bottleneck. `RenderCounters` would show it (I).
- **Fix sketch.** Do these when the draw count becomes a problem:
  - one static vertex/index arena per vertex format, with an offset table;
  - per-draw data (model matrix, material index) in an SSBO, read through `gl_BaseInstance` / `gl_DrawID` (4.6, or `ARB_shader_draw_parameters`);
  - joint palettes in one SSBO per frame;
  - `glMultiDrawElementsIndirect` for the static world and props.
- **Effort.** L. It needs A1.

### A4. Uncompressed RGBA8 textures with unsized formats, and no anisotropic filtering
- **Evidence.** There are no `CompressedTex*` calls; 6 sites use the unsized `InternalFormat.Rgba`; textures are decoded through ImageSharp with mips from `GenerateMipmap` (`TextureDecoder.cs:84,128`, V). The animations folder is 475 MB (V). `TextureMaxAnisotropy` appears nowhere (V).
- **Consequence.**
  - **Memory and load time.** VRAM, load time and the async model loader's upload time scale with RGBA8, which is 4× BC7 and 8× BC1 (D).
  - **Precision.** An unsized `Rgba` lets the driver pick a precision, and it will not work with `glTextureStorage2D` under DSA (D).
  - **Ground blur.** Under the oblique isometric camera, ground textures blur at distance with trilinear filtering alone (I).
- **Fix sketch.**
  - Bake BC7 (colour) and BC5 (normals) offline into KTX2 or DDS, and load them with `glCompressedTexImage2D`. BPTC is core in 4.2 and in every Windows driver.
  - Use sized formats everywhere (`Rgba8`, `Srgb8Alpha8` if sRGB is ever adopted).
  - Set `GL_TEXTURE_MAX_ANISOTROPY` to 4–8 on world textures. It is core in 4.6 and has been a universal extension on 3.3 for years.
- **Effort.** Anisotropic filtering: S. Compression pipeline: M.

### A5. GPU captures cannot be read: no object labels, debug groups or capture hook
- **Evidence.** There is no `ObjectLabel` or `PushDebugGroup`, and no RenderDoc API (V). The capture harness exists (`tools/dev`, the `capture.ps1` mentioned in project memory), but it only produces PNGs.
- **Consequence.**
  - In RenderDoc or Nsight, a frame is an unlabelled list of about 100+ draws and anonymous buffer and texture handles (I).
  - The AI-agent workflow cannot get from "this pixel is wrong" to the draw that produced it.
  - Visual regressions (the recurring albedo and contrast gates in memory) are diagnosed by image statistics instead of by inspecting the draw that caused them (I).
- **Fix sketch.**
  - Put `GL.PushDebugGroup` and `PopDebugGroup` around each pass. They can share the `GpuFrameTimer` section names.
  - Call `GL.ObjectLabel` on every program, buffer, texture and FBO at creation.
  - Add an optional RenderDoc in-app API hook: `RENDERDOC_GetAPI` from `renderdoc.dll`, about 40 lines of P/Invoke. A `DREAM_RENDERDOC_CAPTURE=N` setting would let the harness save a `.rdc` next to the PNG.
- **Effort.** S. KHR_debug is already probed in `GLUtils.cs:184`.

### A6. Shader validity depends on one GPU and one vendor
- **Evidence.** The only compile gate is the `[GlFact]` test, which runs against whatever driver the test machine has, and `DREAM_GL_TESTS=0` skips it (`GlFactAttribute.cs`, V).
- **Consequence.**
  - NVIDIA's GLSL compiler accepts code that AMD and Intel reject: implicit int→float conversion, `texture2D`, missing precision rules. The game ships on Windows with unknown customer GPUs, so a shader can pass every gate and still fail at a player's first launch (I, a well-known class of bug).
  - A machine with no GPU checks nothing.
- **Fix sketch.** Add a test or CI step that runs `glslangValidator` (Khronos reference compiler) on each program's *expanded* source: run `Shader.ResolveIncludes` and `InsertDefine` and dump the result. This is deterministic, needs no GPU, and checks against the spec rather than one vendor's lenient reading. The same step could later emit SPIR-V for an `ARB_gl_spirv` path or a cross-compile.
- **Effort.** S.

**Done well (this area):**
- The pure-maths split. CPU twins (`WarMistField`, `BoundaryMistField`, `FrameLighting` mirrors) are pinned to the GLSL by structural tests, so about 2.3k rendering tests run without a GPU (V).
- `IGpuQueryApi` and `IWorldInstanceDrawTarget` show the team already cuts a seam where a test needs one (V).

---

## 3. API options compared for this codebase

The baseline cost of leaving GL entirely, measured above:
- rewrite about 45 files of GL calls (707 sites);
- port 1.9k lines of GLSL;
- rewrite the 43 GLSL-pinning test files;
- rewrite two UI back ends (ImGui, classic HUD);
- keep OpenTK.Windowing and OpenTK.Mathematics, which work with any API (GLFW exposes the HWND);
- re-validate every visual gate (albedo × 2.17 with no tonemap: the look is tuned to GL's exact blend and sampling behaviour).

For a team of one human plus agents, I estimate that at **4–8 weeks** of focused work before reaching parity, with no new visual capability on day one (I).

| Option | What it gives this game | Migration cost | Risk | Platform reach | Tooling | C# maturity (2026) |
|---|---|---|---|---|---|---|
| **(a) Stay on GL, move to 4.5/4.6 core** | DSA (removes the bind-to-edit state leaks of A2); `layout(binding)` (removes the 4 `UniformBlockBinding` wrappers); SSBO lights + compute light culling (removes the 16-light cap); compute particles and mist; persistent-mapped ring buffers for `FxInstanceBuffer` (replaces orphan + SubData, `FxInstanceBuffer.cs:46-47`); MDI + base-instance for the static world; `glClipControl` reverse-Z for shadow and depth precision; KHR_debug in core; optional `ARB_bindless_texture` for material arrays | **S** to switch the context; **M** to migrate to DSA as files are touched; **L** for the MDI arena. Fully incremental: every step ships on its own and the GLSL stays GLSL | Low. Main risk: old Intel iGPUs. Before Skylake, Windows drivers stopped at 4.3/4.4 (I, unsure of the exact cut-off). 4.5 is a safe floor for any GPU from 2015 on. | Windows (all vendors 4.6) and Linux (Mesa 4.6). macOS no, but it is blocked anyway (Bullet native, WinMM audio, GL 4.1) | RenderDoc full; Nsight Graphics supports GL; PIX no (D3D only) | OpenTK 4.9.4 (in use) binds GL 4.6 completely. OpenTK 5 has been in preview for a long time (unsure of its status) |
| **(b) Silk.NET** | As bindings: the same GL, plus Vulkan, D3D11/12 and WebGPU bindings in one family, and its own windowing and input. It is **not an abstraction**: switching from OpenTK GL to Silk.NET GL renames 707 call sites for no visible gain | M (mechanical) for GL-for-GL. It only makes sense as the binding layer for (d) or (f) | Medium: Silk.NET 3.0 has been in development for a long time; 2.x is stable but slow-moving (I) | Same as the target API | Same as the target API | `Silk.NET.OpenGL`, `Silk.NET.Vulkan`, `Silk.NET.WebGPU`, `Silk.NET.Direct3D11` at 2.22.x (fairly sure about 2.22; unsure whether 3.0 is stable yet) |
| **(c) Veldrid / NeoDemo-style RHI over D3D11/Vulkan/Metal/GL** | One API over D3D11 (better Windows driver robustness than GL on Intel and AMD); Metal for a Mac that cannot ship anyway; compute and indirect draws. No bindless; resource sets replace loose uniforms | **L**: every renderer, all 64 loose uniforms become resource sets, shaders rewritten to Veldrid.SPIRV GLSL 450 conventions, both UI back ends (Veldrid has an ImGui renderer) | **High.** Upstream `Veldrid` 4.9.0 dates from about 2022–23 and is effectively unmaintained (I; forks exist, unsure which are active). The project would be inheriting an abandoned RHI | Win/Linux/Mac (Mac moot) | RenderDoc through the backend; PIX through D3D11 | `Veldrid` 4.9.0, `Veldrid.SPIRV` (stale, I) |
| **(d) WebGPU via wgpu-native** | A modern explicit API with excellent validation and compute. A browser build is **not** realistic: BulletSharp native, NAudio, sockets, SQLite | **L**: shaders to WGSL (all 1.9k lines plus 43 pinned test files), every renderer, UI back ends, new binding model | High: the `webgpu.h` native header churned through 2024–25; C# bindings lag wgpu releases (I) | Win/Linux/Mac (moot) | RenderDoc via the Vulkan or D3D12 backend; captures show wgpu's translation, not your calls | `Silk.NET.WebGPU` 2.x (tracks an older header, I); `Evergine.Bindings.WebGPU` (unsure of the version) |
| **(e) D3D11 / D3D12 via Vortice.Windows** | D3D11: the most robust Windows driver path; PIX; **a DXGI flip-model swapchain** (lower latency, proper VRR, and an HDR10/scRGB swapchain if the game ever adds HDR *output*); HLSL with DXC and good tooling. D3D12 adds nothing this game needs | D3D11: **L** (as above: HLSL rewrite and all renderers; ImGui needs a hand-written D3D11 back end of about 300 lines, matching `Render.ImGui.cs`). Windowing can stay on GLFW with a no-API window and `GLFW.GetWin32Window`. D3D12: **XL** | D3D11: medium (well-known API, but one person plus agents re-tuning every visual gate). D3D12: high (manual barriers, residency, descriptor heaps) | **Windows only, which matches what ships** | PIX, RenderDoc, Nsight: the best tooling of any option | `Vortice.Direct3D11`, `Vortice.DXGI`, `Vortice.D3DCompiler` / `Vortice.Dxc` 3.x (mature and maintained; unsure of the exact 2026 minor version) |
| **(f) Vulkan direct** | Explicit control, bindless by default, async compute. For a small isometric ARPG drawing hundreds of draws, none of this is needed | **XL**: thousands of lines of boilerplate (swapchain, sync, memory allocation without VMA-for-C# maturity, pipelines for every state combination) | **Very high** for this team: synchronisation bugs are non-deterministic and hard for agents to diagnose from PNG captures | Win/Linux (Mac via MoltenVK, moot) | RenderDoc, Nsight, validation layers (excellent) | `Silk.NET.Vulkan` 2.22.x, `Vortice.Vulkan` (maintained, unsure of the version) |
| **(g) bgfx / Diligent via bindings** | A battle-tested C++ RHI over D3D11/12, Vulkan, Metal and GL; Diligent has a modern PSO model. In exchange: a large native dependency, and debugging goes through C++ | **L**: bgfx needs its own shader dialect and `shaderc` toolchain (GLSL-like with `$input` macros), so all shaders and tests are rewritten. Diligent uses HLSL | High: C# is a second-class citizen. bgfx's C# bindings are a generated `bgfx.cs` in its repo, with no official NuGet I am sure of. Diligent has published .NET packages (unsure of the name, version and API coverage) | Wide (moot) | RenderDoc/PIX through the backend | Low as of my knowledge (I) |
| **(h) Full engine, for comparison only** | Godot 4 (C#/.NET 8+, Forward+ clustered lights, tonemap/HDR, GPU particles, editor); Stride 4.2 (C#, D3D11/Vulkan, PBR, post-processing); MonoGame 3.8.x / FNA (XNA model; *lower* feature ceiling than GL 4.6; FNA3D now has an SDL3 GPU backend) | **Rewrite.** 44k lines of rendering, scenes, the custom UI and its tests; the sim/server/Bullet/protocol could survive as a library, but the client could not. Godot and Stride also bring their own physics and scene model, which clash with server-authoritative Bullet | Extreme. You lose about 2.3k rendering tests and the agent workflow built around them | Godot/Stride: wide | Engine profilers plus RenderDoc | Godot .NET mature; Stride small community; MonoGame mature but dated |

**What the comparison shows (I).** Every capability on the "unlocks" list that matters to this game is reachable with option (a). That includes the HDR target and tonemap that the known context says is missing (float render targets have been core since 3.0), clustered lights, GPU particles, MDI, debug labels and texture compression. The only things GL 4.6 cannot give are PIX and a DXGI flip-model, HDR-output swapchain. Both are D3D11 features, and they only matter if HDR displays or Windows driver robustness become product requirements.

---

## 4. Complementary libraries and tools (no API change)

| Item | What it does here | GL needed | How to adopt | Maturity / naming |
|---|---|---|---|---|
| **glslangValidator** (Khronos) | Checks the expanded GLSL against the spec with no GPU (A6); can also emit SPIR-V for a later `ARB_gl_spirv` path or a cross-compile | none | CI or test step over each `ShaderCompileTests.Programs` row after `ResolveIncludes`/`InsertDefine` | Ships with the Vulkan SDK and the KhronosGroup/glslang releases. No NuGet I am sure of; call the binary |
| **KTX2 or DDS + BC7/BC5** (KTX-Software `toktx`, Basis Universal `basisu`, or `texconv`) | 4–8× less texture VRAM and upload; faster async model loads (A4) | 3.3 + `EXT_texture_compression_s3tc`/BPTC (core 4.2) | Bake offline in the asset pipeline. A runtime KTX2 reader for non-supercompressed BCn is about 200 lines of C#. Basis transcoding at runtime is only worth it for on-disk size | Offline tools are mature. C#: no KTX2 reader on NuGet that I am sure of. SharpGLTF can read the `KHR_texture_basisu` extension reference (unsure) |
| **meshoptimizer / gltfpack** | Vertex-cache and fetch reordering, LOD simplification for the 475 MB glTF set, and quantisation | none | Run `gltfpack` offline. Avoid `EXT_meshopt_compression` unless the loader decodes it (SharpGLTF 1.0.6 support is unsure) | Native lib mature; C# wrappers unsure; use the CLI |
| **RenderDoc in-app API** | Captures triggered by the harness; `.rdc` saved beside PNGs (A5) | any | P/Invoke `RENDERDOC_GetAPI`; load `renderdoc.dll` only when an env var is set | API stable for years; no NuGet needed |
| **KHR_debug labels and groups** | Readable captures and Nsight timelines | 4.3 core (extension on 3.3, already probed) | `GL.ObjectLabel` at creation; `Push/PopDebugGroup` per `GpuFrameTimer` section | In OpenTK already |
| **Tracy** | One CPU and GPU timeline: frame, render export, physics, upload stalls, server tick. Complements the `GPU:` line, which only gives section totals | any (GPU zones use GL timer queries) | P/Invoke the Tracy C API (`___tracy_emit_zone_begin` and related) from `TracyClient.dll`; zones in `PlayScene.Render` and the server tick | Tracy is mature; C# wrappers exist but I am unsure which is maintained, so a thin own P/Invoke is safest |
| **MSDF text** (`msdf-atlas-gen` offline) | Crisp HUD and label text at every `UiScale`, and cheap outlines and glows for the gothic HUD | 3.3 | Bake the atlas and JSON offline; a ~20-line fragment function in `uiFragment.glsl`; glyph table into `UiGlyphTable` | Tool mature; no runtime library needed |
| **Anisotropic filtering** | Sharper ground under the oblique isometric camera (A4) | ext on 3.3, core 4.6 | One `TexParameter` per world texture | n/a |
| **SPIRV-Cross** (later, optional) | Turn the validated GLSL into HLSL or MSL if a D3D11 backend is ever wanted, so shader source never has to fork | none | Only together with a future (e) | Mature native; C# bindings exist (for example `Vortice.SpirvCross`, unsure of the version) |

---

## 5. Recommendation

### Do now (small, keeps GL, each item independent)
1. **Request a 4.5 core context with an explicit floor and a clear error message** (A1). It costs nothing and unlocks everything in the "later" list. Leave the shaders at 330 and move each one to 450 when it is next edited.
2. **Debug labels, debug groups and the RenderDoc in-app hook** (A5). This is the biggest gain in how fast the agent swarm can diagnose visual bugs, and it costs about a day.
3. **A `glslangValidator` gate on the expanded shader sources** (A6). It gives vendor-neutral shader correctness without a GPU.
4. **Anisotropic filtering, plus sized internal formats in place of the unsized `Rgba`** (A4, the S part).
5. **Start a `GpuState` cache in Core and route new passes through it** (A2). Convert existing renderers when they are next touched, not in one big change.

### Do later (when a feature or a measurement asks for it)
- **SSBO light list and compute (tiled or clustered) light culling**, when 16 lights stops being enough at night. Lamps are placeable, so it will (I). Needs 4.3.
- **Persistent-mapped ring buffers** for fx instances, the instance matrices and the UBO ranges. Do it when the per-frame upload shows up in Tracy. Needs 4.4 (`ARB_buffer_storage`).
- **A shared vertex arena with MDI and base-instance for the static world and props** (A3). Do it only when `RenderCounters` draw and uniform counts are the bottleneck. Needs 4.3 indirect, plus 4.6 or the extension for `gl_DrawID`.
- **The texture compression pipeline** (KTX2/DDS BC7). Do it before the asset set grows further.
- **The HDR float target and tonemap.** This is a technique question, not an API one. It works on GL 3.3 and needs no API move.
- **A D3D11 backend through Vortice behind the `GpuState` seam.** Reconsider this only if one of these becomes a requirement: HDR10 display output, PIX-level profiling, or repeated GL driver failures reported by players on Intel or AMD. Even then, keep GLSL as the source language and cross-compile it with SPIRV-Cross, so the 43 GLSL-pinning tests survive.

### Never (for this game and this team)
- **Vulkan direct or D3D12.** XL cost, non-deterministic synchronisation bugs that agents cannot diagnose from PNGs, and no capability this game needs that GL 4.6 lacks.
- **An engine migration (Godot, Stride, MonoGame/FNA).** It throws away 44k lines of rendering code, about 2.3k tests and the agent workflow, and the engines' scene and physics models conflict with the server-authoritative Bullet sim. MonoGame and FNA would also lower the feature ceiling.
- **Veldrid, bgfx or Diligent.** Each adds a dependency that is unmaintained or second-class in C#, plus a shader-dialect rewrite, to reach platforms the game cannot ship on.
- **WebGPU now.** A WGSL rewrite and churning C# bindings, and the browser target it promises is blocked by Bullet, NAudio and SQLite.
- **Swapping OpenTK for Silk.NET GL for its own sake.** It renames 707 call sites with zero visible gain. Keep OpenTK.Mathematics (320 files, 114 in the sim) whatever happens to the GL bindings.
- **Any macOS-driven graphics decision.** macOS is blocked by the BulletSharp native library, WinMM audio and GL 4.1, so it should not constrain the Windows renderer to 4.1.
