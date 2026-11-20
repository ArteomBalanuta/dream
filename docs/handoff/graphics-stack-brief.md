# Graphics stack brief — `dream`

Written 2026-11-19 against master `ccb2068e`. Everything below was read out of the code, not
remembered.

## The one-line answer

**There is no game engine.** `dream` is a custom renderer written directly against **OpenGL 3.3
core** (GLSL `#version 330 core`) through **OpenTK 4.9.4**, in **C# / .NET 10**. No Unity, no
Unreal, no Godot, no MonoGame, no Veldrid, no intermediate render framework. About 24 hand-written
GLSL shaders and ~40k lines of C# rendering code are the whole graphics layer.

Anything an engine would normally hand you — the render graph, batching, the material model, the
lighting rig, the HUD compositor, the asset pipeline — exists here as project code that can be
changed. There is nothing to work around and no vendor to wait on.

## Stack at a glance

| Layer | What it is |
|---|---|
| Language / runtime | C# , `net10.0` |
| Window + GL binding | OpenTK 4.9.4 (`GameWindow`), single-threaded: update and render on one loop |
| Graphics API | OpenGL 3.3 core profile, GLSL 330 |
| Context | 1440x1024 default, **MSAA 4x** requested at context creation, VSync **off**, frame cap via `UpdateFrequency` (144 default). `ContextFlags.Debug` + KHR_debug in Debug builds |
| Physics | BulletSharp 0.11.1 (simulation only, not graphics) |
| Model format | glTF via SharpGLTF 1.0.6 — skinned meshes, skeletons, animation clips |
| Image I/O | SixLabors.ImageSharp 3.1.12 |
| Debug UI | ImGui.NET 1.91.6.1 — **dev overlay only**, not the shipping HUD |
| Shipping HUD | custom immediate-mode UI, own shaders and sprite atlases (see "HUD") |
| Audio | NAudio (not graphics, listed for completeness) |

Entry point: `dream/Application/Game.cs` (`OnRenderFrame`) then `dream/Client/Scenes/PlayScene.Render.cs`.

## Frame structure

One pass over the default framebuffer. **The only FBO in the entire codebase is the shadow map**
(`dream/Rendering/Shadows/ShadowMapTargets.cs`). There is no HDR target, no G-buffer, no
post-processing chain, no bloom, no SSAO, no TAA, no colour grading.

Per frame, in order (from `PlayScene.Render.cs`):

1. **Sky model evaluated** — time of day drives sun/moon direction and colour, ambient sky/ground,
   fog colour. Clear colour is the horizon colour, so uncovered pixels already match the distance.
2. **Shadows** — sun (single 2048 ortho map, 9-tap PCF) plus up to 4 casting fires into a
   4x4x2048 atlas, 4 perspective faces each. `dream/Rendering/Shadows/`.
3. **World** — static voxel/cube terrain, chunked and frustum-culled, instanced.
   `dream/Rendering/World/StaticWorldChunkRenderer.cs`. Props on top.
4. **Actors** — skinned glTF models: enemies, local player, remote players.
5. **Economy entities, sky dome, boundary mist, war mist.**
6. **Effects pass** — skill FX, particles. `dream/Rendering/Effects/`.
7. **HUD** — the classic HUD batch, then optionally the ImGui dev overlay.
8. `SwapBuffers`, then `CheckGLErrorsAfterFrame`.

GPU timing is already instrumented per section — `GpuTimerSection { World, Actors, Hud, Effects,
Shadows }` in `dream/Rendering/Core/GpuFrameTimer.cs` — plus CPU-side `RenderCounters` for draw
calls, program binds and uniform uploads. **Real per-section numbers are available before
proposing anything.** Use them rather than guessing.

## The lighting model — read this before touching any colour

This is the most important constraint in the project and the most common source of wasted work.
The pipeline is **strictly linear in albedo, with no tonemap and no gamma encode.** Final output
is a flat multiply followed by a hardware clamp:

```glsl
// lighting.glsl
vec3 dreamExpose(vec3 c) { return c * uLightingParams.x; }   // that is the entire "tonemap"
```

The full actor transfer function, verified line-by-line against the shaders:

```
G    = ambient(n)*ambientScale + (directShaded(n,p) + points(n,p)) * 0.75
out  = clamp(max(albedo*G, albedo*floor) * exposure + emissive, 0, 1)
ambient(n) = mix(ambientGround, ambientSky, n.y*0.5 + 0.5)
direct(n)  = max(dot(n,sunDir),0)*sunI*sunCol + max(dot(n,moonDir),0)*moonI*moonCol
points: s = d^2/r^2 ; f = clamp(1-s,0,1)^2 / (1 + 1.5*s)
        contribution = colour * f * (0.25 + 0.75*max(dot(n,l),0)) * lightV
albedo = texel/255 * baseColorFactor      # the texture bytes ARE linear albedo
```

Consequences to design around:

- **Texture bytes are linear albedo, not sRGB.** There is no sRGB framebuffer and no
  `GL_FRAMEBUFFER_SRGB` anywhere in the codebase.
- **Gain reaches ~2.17x with no roll-off.** An authored channel much above **~0.42** clips to flat
  white on screen. That is a hard ceiling on authored art, not a style preference.
- **It is a *channel* that clips, not a luma.** A saturated colour blows out one channel while its
  luma still looks safe. Any brightness bound must be on the max channel.
- Lighting is Lambert plus hemispherical ambient. **No specular, no normal maps, no PBR BRDF** in
  the world path, despite the art being authored through a PBR-ish offline forge.
- Point lights: **hard cap of 16** per frame (`LightList.MaxLights`), of which at most 4 cast
  shadows.
- Fog is exponential-squared on distance; its origin is the isometric focus, not the eye.

`tools/ui-assets/enginelook.py` restates this contract offline **and fails if the shaders stop
matching it** — it re-reads the GLSL and diffs. C# mirrors (`FireShadowSamplingMirrorTests`,
`ShadowDisabledIsIdentityTests`, `SunShadowLookupMirrorTests`) pin the same thing from the other
side. Changing the lighting math will trip those, and they are the right place to express whatever
the new contract becomes.

## Camera

Isometric is the only reachable view. It is a **perspective** projection at a fixed rig, not a true
orthographic iso (`dream/Client/Presentation/IsometricView.cs`):

- pitch 50 deg, yaw 45 deg, distance 18 units, FOV 35 deg, near 1, far 98
- vertical smoothing 0.18 s, snap at 4 units

The HUD and startup menu use `CreateOrthographicOffCenter`; the world does not.

## HUD

The shipping HUD is **not ImGui** — ImGui is a dev overlay only. The real one is a custom
immediate-mode system in `dream/Rendering/Ui/` (~11.4k lines, the largest rendering subsystem) with
its own shaders (`uiVertex.glsl` / `uiFragment.glsl`), its own sprite atlases and its own batcher.

`uiFragment.glsl` is a five-mode uber-shader on premultiplied alpha: textured+tint, two-channel
fill/rim mask, flat tint, **liquid gem** (level map with meniscus, refraction wobble, glint sweep,
low-health pulse) and additive. Text has a separate program pair.

A missing required sprite makes `UiSpriteTable.Validate` throw and silently drops the **entire**
classic HUD back to the ImGui HUD at runtime. If the HUD looks wrong in a capture, check for
`surface=Classic` in the capture log first — that is the usual cause.

## Asset pipeline — unusual, and the project's main strength

Art is **procedurally generated from Python**, not drawn by hand and not imported from DCC tools.
`tools/ui-assets/` holds the generators (`forge.py`, the PBR-ish material forge; `gen_window.py`,
`gen_items.py`, `gen_hud.py`, `gen_char_*.py`, `gen_fx.py` and others) which write the committed
atlases under `dream/ui/`. `tools/audio/` does the same for sound.

Beside them sit **19 numeric art gates** (`tools/ui-assets/tests/check_*.py`) that measure the
committed art — brightness ceilings, saturation, contrast, band energy, silhouette legibility,
determinism — and fail on drift. A gate reads; a generator writes; no gate mutates what it
measures.

Two cautions about the gates, both learned the hard way and both recent:

- **Units matter and have been wrong before.** Some gates measure sRGB luma, others max channel;
  for a red stone those differ by ~2.7x. A number from one is never comparable to a bound from the
  other.
- **A floor on *absolute* contrast (std, band energy) is not a texture gate.** Absolute contrast
  scales with how bright a surface is, so such a floor silently forbids ever darkening that
  surface — it gates brightness by accident, in the wrong direction. Fixed on 2026-11-19 for the
  window slab, where it had been protecting the very defect it appeared to guard against. Contrast
  floors should be relative to the mean; brightness ceilings should be on the max channel.

## Visual verification

There is a scripted capture harness: `DREAM_HUD_CAPTURE=1` **plus `DREAM_AUTO_CONNECT=1`** (both,
or the run waits forever on the connection screen), plus `DREAM_HUD_CAPTURE_SCRIPT` with steps such
as `walkto:x:y:ms`, `vendor`, `aim:x:y`, `snap`, `escape`. It drives the real client against a real
local server and writes PNGs. This is how the HUD is actually checked.

## Where the headroom is

Not a work plan — these are the gaps worth knowing exist. Choose with measurements from
`GpuFrameTimer`, not from this list.

**Lighting / material**

- No specular and no normal mapping anywhere in the world path. Everything is Lambert over flat
  albedo, which is the deepest reason surfaces read as painted rather than lit.
- No tonemap at all. A filmic or even Reinhard curve would reclaim the whole top end of the range
  and remove the ~0.42 authoring ceiling that currently constrains every art decision in the
  project. **Probably the highest-leverage single change available** — it would let a lot of
  existing art breathe without being redrawn. But it invalidates every brightness gate and every
  authored albedo at once, so it is a project, not a patch, and it needs the HDR target below.
- No ambient occlusion of any kind.
- A 16-light cap with 4 shadow casters is low for a torchlit gothic setting.

**Pipeline**

- No post chain at all: no bloom for the fires and gems, no colour grading, no vignette, no
  depth of field. An HDR render target is the prerequisite for all of it and for the tonemap above.
- MSAA 4x is the only antialiasing.
- Single-threaded render loop.

**Already good — do not "fix"**

- The shadow implementation is careful and well-pinned by mirror tests.
- Terrain is chunked, frustum-culled and instanced.
- Per-section GPU timing and draw-call counters already exist.
- The art gates, now that the units are right, are a genuinely strong regression net.

## Where to look first

| Topic | Path |
|---|---|
| Frame order | `dream/Client/Scenes/PlayScene.Render.cs` |
| GL setup, window, frame cap | `dream/Application/Game.cs` |
| Shaders | `dream/shaders/*.glsl` (1559 lines total) |
| Lighting uniforms and helpers | `dream/shaders/lighting.glsl`, `dream/Rendering/Lighting/` |
| Lighting contract, offline and self-verifying | `tools/ui-assets/enginelook.py` |
| Shadows | `dream/Rendering/Shadows/`, `dream/shaders/fireShadows.glsl` |
| Terrain | `dream/Rendering/World/`, `dream/shaders/cubeVertex.glsl`, `cubeFragment.glsl` |
| Actors | `dream/Rendering/Actors/`, `dream/Rendering/Models/`, `enemyFragment.glsl` |
| HUD | `dream/Rendering/Ui/`, `dream/shaders/ui*.glsl`, `hud*.glsl` |
| Art generators | `tools/ui-assets/*.py` |
| Art gates | `tools/ui-assets/tests/check_*.py` |
| Perf instrumentation | `dream/Rendering/Core/GpuFrameTimer.cs`, `RenderCounters.cs` |
