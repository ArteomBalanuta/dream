# Plan: FX wave (LDR particles, halos, emissive)

**Goal:** add an instanced particle and halo pass, emissive textures and a GPU timer section in LDR on the back buffer, client only, compatible with `integrate/isometric`.

**Out of scope:** HDR, FBOs, bloom, tone mapping, FXAA, MSAA, soft particles, aura shells, weapon trails, spell systems, gear on the character model, sRGB changes, render-queue refactors.

Implementer worktrees must first run `git reset --hard integrate/fx-ldr`.

## Findings that shape the work

1. `ActorDiedEvent` never fires on the client. Client combatants have no hub, and health arrives through `SetAuthoritativeState`. The wire already carries `DamageEventDelta.ActorDied`, and `ClientSnapshotDamageApplier` runs before `RemoveAbsentEnemies`, so the target still resolves. Publish `ActorDiedEvent` from that applier (client only, no protocol change). The server emits two deltas per death (Amount 0 + died, and Amount > 0 + died), so publish at most once per actor per snapshot.
2. No emissive content exists: 0 of 531 `.glb` files have `emissiveTexture`. This feature is verified by contract tests only (decided).
3. Once `material.hasEmissiveTexture` is declared, terrain on the `cube` program would inherit what `AnimatedModel` left there, so `Render.Terrain.cs` must reset it. `AnimatedModel` line 592 currently goes to location -1; after this change it uploads per material, so it needs a cached setter.
4. A timer section cannot reopen: `BeginSection` ignores a section already issued this frame. Debug and view model become untimed. Accepted. Update the comment at `PlayScene.Render.cs:68`.
5. Lamps are player-built only and the capture script cannot place one. Lamps are unit tested; torches are checked visually.
6. In iso, `uCameraPosition` is the fog origin (focus), not the camera. The FX shader must never derive view direction from it, and dust must not centre on the camera (see `ViewCentre`).
7. A lamp halo at the cube centre would be cut by the cube's own depth, hence per-instance `DepthBias`.
8. FrameLightingContractTests:90 and WaveT2PointLightTests:374 pin the exact `FragColor` line. Only their `material.emissiveFactor` to `emissive` text changes; the CPU mirrors stay valid.
9. No csproj change: `shaders\**\*` and `ui\**\*` already copy to output.

## WS1 `fx-runtime`

**New:** `dream/Rendering/Effects/` (`FxInstance.cs`, `FxParticlePool.cs`, `FxRandom.cs`, `FxSimulation.cs`, `FxViewBasis.cs`, `FxEventBridge.cs`, `FxInstanceBuffer.cs`, `FxRenderer.cs`), `dream/shaders/fxVertex.glsl`, `dream/shaders/fxFragment.glsl`, `dream/Client/Scenes/PlayScene.Effects.cs`. Tests in `dream.Tests/Rendering/Effects/`: `FxParticlePoolTests`, `FxSimulationTests`, `FxViewBasisTests`, `FxAllocationTests`, `FxPassContractTests`. Also `dream.Tests/Rendering/Lighting/FogTransmittanceContractTests.cs`, `dream.Tests/Client/Replication/ClientSnapshotDeathEventTests.cs`.

**Modified:** `dream/shaders/lighting.glsl`, `dream/Rendering/Core/GpuFrameTimer.cs`, `dream/Rendering/Hud/FramePerformanceSnapshot.cs`, `dream/Client/Scenes/PlayScene.Render.cs`, `dream/Client/Scenes/PlayScene.Lifetime.cs`, `dream/Client/Replication/ClientSnapshotDamageApplier.cs`, `dream/Application/Game.cs` (one line), `docs/rendering-and-assets.md`, `docs/configuration.md`, `dream.Tests/Rendering/Core/GpuFrameTimerTests.cs` (`3 * 4` to `3 * 5`, Effects cases), `dream.Tests/Rendering/Hud/FramePerformanceSnapshotGpuTests.cs` (format strings).

1. Split the fog helper; `dreamApplyFog`'s result stays identical:
```glsl
float dreamFogTransmittance(vec3 fragPos) {
    float d = max(distance(fragPos, uCameraPosition.xyz) - uCameraPosition.w, 0.0);
    float k = uFogColor.w * d;
    return clamp(exp2(-k * k), 0.0, 1.0);
}
vec3 dreamApplyFog(vec3 c, vec3 fragPos) { return mix(uFogColor.rgb, c, dreamFogTransmittance(fragPos)); }
```
2. Timer: append `Effects = 3` to `GpuTimerSection`, `SectionCount = 4`, append `double EffectsMilliseconds = 0d` as the last `GpuFrameTimings` parameter, read it in `TryRead`.
3. Perf: append `double GpuEffectsMilliseconds = 0d` as the last `FramePerformanceSnapshot` parameter, add it to `HasCounters`. Line 14 of both formatters (`FormatHudLine`, `TryFormatHudLine`) becomes `actors X ms, fx Y ms, hud Z ms`.
4. Pure simulation, no GL (contracts below).
5. `FxInstanceBuffer` follows `InstanceMatrixBuffer`: lazy handle, orphan with `BufferData(null)` then `BufferSubData`. Capacity fixed at `FxLimits.MaxInstances`.
6. `FxRenderer` has the `SkyRenderer` shape: `UniformNames` table with cached locations, render-thread asserts in constructor and `Dispose`, early return when count is 0 before any state change. Atlas: `TextureDecoder.DecodeRgba` + `UploadTexture2DClamped(tex, maxMipLevel: 2)`.
7. Scene wiring: `PlayScene.Effects.cs` holds fields and `DrawEffectsPass`. Construct after `_sky`; add `RunDisposeStep(DisposeEffects, ref failures)` after `DisposeSky`; dispose the bridge's subscriptions too.
8. `Game.cs`: `StartFocused = Environment.GetEnvironmentVariable("DREAM_HUD_CAPTURE") is null`. Document in `docs/configuration.md` near line 194, not lines 76, 185-188 or 457-475, which iso edits.
9. Docs: describe the frame-order step and emissive in `docs/rendering-and-assets.md`.

**The only `PlayScene.Render.cs` edits (3):**
- Insert after the blank line 111, not directly after line 110, with a new blank line after it: `DrawEffectsPass(in sky, in presentation, gpuTimer, (float)args.Time);`. This placement merges cleanly with iso and lands outside its `if (!isometric)`.
- Insert before line 147: `GpuEffectsMilliseconds = gpuTimer.Latest.EffectsMilliseconds,`
- Update the comment at line 68.

**Damage applier shape** (indexed loop; `DamageEvents` is an `IReadOnlyList`):
```csharp
if (damageEvent.Amount <= 0 && !damageEvent.ActorDied) continue;
if (!IsAttributable(...)) continue;
if (!TryResolveCombatActor(...)) continue;
if (damageEvent.Amount > 0) events.Publish(new DamageAppliedEvent(...));
if (damageEvent.ActorDied && !DiedEarlierInSnapshot(snapshot, i, actorId)) events.Publish(new ActorDiedEvent(actor));
```

## WS2 `emissive`

**New:** `dream/Rendering/Core/RenderFrameClock.cs`, `dream.Tests/Rendering/Core/EmissiveMaterialContractTests.cs`.

**Modified:** `dream/shaders/cubeFragment.glsl` (keep the BOM), `dream/shaders/enemyFragment.glsl`, `dream/Rendering/Core/Shader.cs`, `dream/Rendering/Core/Render.Terrain.cs`, `dream/Rendering/Models/AnimatedModel.cs`, `dream/Rendering/Models/AnimatedModelData.cs`, `dream/Client/Scenes/PlayScene.Update.cs` (one line). Tests: `FrameLightingContractTests.cs`, `WaveT2PointLightTests.cs`, `Wave9AlphaMaskUniformCoverageTests.cs` (new names in `indirect`), `WorldShaderContractTests.cs`, `AnimatedModelDataTests.cs`.

Both fragment shaders:
```glsl
// Material members, after emissiveFactor:
bool hasEmissiveTexture;
sampler2D emissiveTexture;
vec2 emissiveUvScroll; // UV per second (0 = still)
// file scope:
uniform float uTime;
// main, replacing the final term:
vec3 emissive = material.emissiveFactor;
if (material.hasEmissiveTexture) {
    emissive *= texture(material.emissiveTexture, texCoord0 + material.emissiveUvScroll * uTime).rgb;
}
FragColor = vec4(dreamApplyFog(dreamExpose(lit) + emissive, fragPos), baseColor.a);
```
No `discard`; no sampling unless the flag is set.

`Shader.cs` (locations resolved at link, last value cached with a NaN or unknown start, reset in `Dispose`, same as `SetEmissiveFactor`):
```csharp
public const string HasEmissiveTextureUniformName = "material.hasEmissiveTexture";
public const string EmissiveUvScrollUniformName = "material.emissiveUvScroll";
public const string TimeUniformName = "uTime";
public void SetHasEmissiveTexture(bool hasEmissiveTexture);
public void SetEmissiveUvScroll(Vector2 uvScroll);
public void SetTime(float seconds);
```

`RenderFrameClock` (static, like `RenderCounters`):
```csharp
public static class RenderFrameClock {
    public const double WrapSeconds = 4096d;
    public static float Seconds { get; private set; }
    public static void Advance(double seconds); // Seconds = (float)(seconds % WrapSeconds)
}
```
`PlayScene.Update.cs`, immediately after line 20: `RenderFrameClock.Advance(_clientPresentationTimeSeconds);`

`AnimatedModel.SetMaterialProperties`: replace the string `SetBool` at line 592 with `SetHasEmissiveTexture`; add `SetEmissiveUvScroll`; add `if (material.EmissiveUvScroll != Vector2.Zero) shader.SetTime(RenderFrameClock.Seconds);` so non-scrolling materials cost no upload.

`Render.Terrain.cs`: call `SetHasEmissiveTexture(false)` in `BeginStaticTerrainPass` and in the fallback-cube setup near line 188.

Scroll source: `AnimatedModelData` reads glTF material extras key `dreamEmissiveScroll: [u, v]` through a pure static helper. Absent or malformed gives zero. Add `EmissiveUvScroll` to both material types and copy it at `AnimatedModel.cs:156`. SharpGLTF is 1.0.6; confirm the extras API there.

`uTime` goes in the Wave9 `indirect` table as `switchPaths.Contains("SetTime(")`.

## WS3 `fx-art`

**New:** `tools/ui-assets/gen_particles.py`, `tools/ui-assets/tests/check_particles.py`, `dream/ui/particles/particles.png`, `dream/ui/particles/particles.json`, `dream.Tests/Rendering/Effects/FxAtlasAssetTests.cs` (literal sizes, no WS1 types).

**Modified:** `tools/ui-assets/build_all.py` (add to `GENERATORS`), `tools/ui-assets/README.md`.

1. Generator with `main(out_root)`, writing only its own directory, seeded and byte-deterministic.
2. Reuse pinned numpy/pillow and `uiart` or `forge`.
3. Determinism check as in `check_fx.py`, plus checks of layout, A=0 for frames 16-17, and empty margins.
4. Run all the `check_*.py` scripts.

Mood: hot, thin, irregular sparks; dull red embers; cold grey motes; sooty ash. No stars, no round cartoon puffs. Procedural and original only.

## Shared contracts

**Atlas (WS1 and WS3):** `AssetPaths.Ui("particles/particles.png")`. RGBA8, 512x256, 8 columns by 4 rows, 64 px cells. Frame 0 is top-left; rows run downward. Loaded with no flip. Cell margin 4 px, fully zero. RGB is premultiplied emission and stays meaningful where A=0. A is coverage.

| Frames | Content | A |
|---|---|---|
| 0-3 | Spark, near-neutral so tint sets the colour | 0 |
| 4-7 | Ember | 0 |
| 8-11 | Dust mote | > 0, low |
| 12-15 | Ash puff, dark RGB | > 0 |
| 16 | Soft halo | 0 |
| 17 | Halo variant, tighter core | 0 |
| 18-31 | Reserved, empty | |

```csharp
public enum FxFrame : byte { Spark0 = 0, Ember0 = 4, Dust0 = 8, Ash0 = 12, Halo = 16, HaloCore = 17 }
public static class FxAtlas { public const int Columns = 8, Rows = 4, CellPixels = 64, MaxMipLevel = 2; public const string RelativePath = "particles/particles.png"; }
```

**WS1 internal:**
```csharp
public static class FxLimits { public const int MaxInstances = 2048, HaloBudget = 128, ParticleCapacity = MaxInstances - HaloBudget; }

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct FxInstance // 32 bytes, test with Unsafe.SizeOf
{
    public Vector3 Position; public float HalfSize;  // loc 0: vec4, float, offset 0
    public uint Color;                               // loc 1: vec4, UnsignedByte, normalized, offset 16
    public float Rotation; public float Frame; public float DepthBias; // loc 2: vec3, float, offset 20
}
```
All three attributes have divisor 1 and stride 32. `Color` is premultiplied RGBA with R in the low byte; A = occlusion, A = 0 is pure additive. Lifetime fade scales all four channels.

```csharp
public readonly record struct FxViewBasis(Vector3 Right, Vector3 Up, Vector3 Back)
{
    // OpenTK row-vector LookAt: Right=(M11,M21,M31), Up=(M12,M22,M32), Back=(M13,M23,M33).
    public static FxViewBasis FromView(in Matrix4 view);
}

public readonly record struct FxFrameInputs(float DeltaSeconds, Vector3 CameraPosition, Vector3 CameraFront, Vector3 PlayerPosition, float NightFactor);

public sealed class FxSimulation
{
    // cam + front * max(MinViewDistance (6), dot(player - cam, front)); ~18 m in iso, 6 m ahead in first person.
    public static Vector3 ViewCentre(in FxFrameInputs inputs);
    public void SpawnHit(Vector3 anchor, FloatingCombatTextKind kind);
    public void SpawnDeath(Vector3 anchor);
    public void Advance(in FxFrameInputs inputs, ReadOnlySpan<PointLight> lights); // clamps dt to 0.1 s
    public int WriteInstances(FxInstance[] destination); // particles, then at most HaloBudget halos
    public int ActiveParticles { get; }
    public long DroppedSpawns { get; }
}

public sealed class FxEventBridge : IDisposable { public FxEventBridge(SceneEventHub events, FxSimulation simulation); }
// Same shape as CombatFeedbackSystem; Amount <= 0 ignored; anchor read inside the handler.

public sealed class FxRenderer : IDisposable
{
    public const string VertexShaderFile = "fxVertex.glsl", FragmentShaderFile = "fxFragment.glsl";
    public static readonly string[] UniformNames = ["uViewProjection", "uCameraRight", "uCameraUp", "uCameraBack", "uAtlas"];
    public void Draw(FxInstance[] instances, int count, in FxViewBasis basis, Matrix4 viewProjection);
}

// PlayScene.Effects.cs
private void DrawEffectsPass(in SkyState sky, in WorldSessionPresentationState presentation, GpuFrameTimer gpuTimer, float deltaSeconds);
```

`DrawEffectsPass` order: `Advance` with `_render.Lights.Lights` and `sky.StarVisibility` as `NightFactor`; `WriteInstances`; `gpuTimer.BeginSection(GpuTimerSection.Effects)`; `Draw(..., view * _render.ProjectionMatrix)`; `gpuTimer.EndSection()`.

**Lights:** lamp, torch and lantern data all come from `LightCollector`, so `LampLightIndex` and `HeldLightCatalog` need no edits. Classify by `Radius` against `LampLightIndex.LampRadius`, `HeldLightCatalog.TorchRadius` and `LanternRadius`. Tint comes from `light.Color`, so torch flicker carries through. Skip halos within 1.5 m of the camera (first-person torch) and beyond 30 m of `ViewCentre`. The collector lists held lights first, so a straight scan favours them. Halo strength: `lerp(0.35, 1, NightFactor)`. `DepthBias`: 0.9 for lamps, 0.2 for held lights.

**Other rules:** dust keeps about 96 motes in a box around `ViewCentre`. `FxRandom` is a private xorshift32 struct; never `System.Random` or anything from simulation. Pool uses swap-remove; a full pool increments `DroppedSpawns` and returns false. No sorting.

**Shaders:**
```glsl
// fxVertex.glsl
layout(location = 0) in vec4 iPositionHalfSize;
layout(location = 1) in vec4 iColor;
layout(location = 2) in vec3 iRotationFrameBias;
uniform mat4 uViewProjection; uniform vec3 uCameraRight; uniform vec3 uCameraUp; uniform vec3 uCameraBack;
out vec2 vUv; out vec4 vColor; out vec3 vWorldPos;
// Strip corner from gl_VertexID, no mesh VBO: vec2(gl_VertexID & 1, gl_VertexID >> 1).
// Rotate the corner, then world = pos + right*x + up*y + back*bias. Flip V: the atlas is y-down.
```
```glsl
// fxFragment.glsl
#include "lighting.glsl"
uniform sampler2D uAtlas;
// FragColor = texture(uAtlas, vUv) * vColor * dreamFogTransmittance(vWorldPos);
```
Scaling alpha too means dust fades into fog with no mix towards fog colour. No exposure, no discard.

**GL state, `FxRenderer.Draw`:** save `GL.IsEnabled(CullFace)`. Set depth test on, `DepthMask(false)`, blend on, `GL.BlendFunc(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha)`, cull off. Bind the atlas on `Texture0`, then `GL.DrawArraysInstanced(PrimitiveType.TriangleStrip, 0, 4, count)` and `RenderCounters.CountDraw()`. Restore `DepthMask(true)`, `GL.Disable(Blend)`, cull as saved, VAO 0.

## Merge order

1. WS3 (the atlas is needed at runtime).
2. WS2 (self-contained).
3. WS1 (adds the pass that loads the atlas).

The game cannot start in the WS1 worktree alone, because the atlas is missing, but tests build and pass.

## `integrate/isometric` overlap

Don't edit `Render.cs`, `Render.Lighting.cs` or `LocalPlayerRenderer.cs`. Nothing depends on `CameraViewMode.Isometric`, `FogOrigin` or `IsometricView`.

## Testing

Plain `dotnet build dream/dream.csproj` first, then `MSYS_NO_PATHCONV=1 dotnet test dream.Tests/dream.Tests.csproj "/p:BaseOutputPath=<ABSOLUTE G:/... path inside the worktree>/artifacts/verify-bin/"`. At most 2 full-suite runs per verification step.

- `FxAllocationTests`: `AllocationAssert.AtMost(0, ...)` around `Advance` + `WriteInstances` with a full pool, 1,024 lights, and `SpawnHit`/`SpawnDeath` on a full pool. Don't go through `SceneEventHub.Publish`: its `ToArray` allocates per event and it is not on the render loop.
- `FxPassContractTests`, `SkyPassContractTests` style: uniform table matches shaders; state restored; `Draw` has no `new`, lambda or `foreach`; the call follows `skyRenderer.Draw` at exactly 8-space indent and precedes the debug pass; attribute locations match `FxInstance`, which is 32 bytes; no `Random` or `Dream.Simulation` RNG.
- `FxViewBasisTests`: a `Camera` in first-person, third-person and iso-like poses matches `Camera.Right`/`Up`/`-Front`.
- `FxSimulationTests`: frame maths, pool drop, determinism with a fixed seed, `ViewCentre` both ways, light classification, halo budget.
- `FogTransmittanceContractTests`: `dreamApplyFog` is the mix over the helper.
- `ClientSnapshotDeathEventTests`: one publish per actor despite two deltas; unattributable or unresolved publishes nothing; Amount 0 publishes no damage event.
- `EmissiveMaterialContractTests`: flag-gated sampling, no new `discard`, cached setters, terrain resets the flag in both paths.

**Visual check** (needs all three merged, never the profiling harness, never click a firewall prompt): Release build copied to a scratch folder on G:, with its own `config/player-profiles.db` and a throwaway `DREAM_IDENTITY_FILE`. Run in the background:
```
DREAM_AUTO_CONNECT=1 DREAM_MAX_FPS=60 DREAM_TIME_OF_DAY=midnight
DREAM_HUD_CAPTURE=<dir>
DREAM_HUD_CAPTURE_SCRIPT=play,inventory,log,drag:<torch anchor>:main,v,wait:60,snap,fight:400,fight:400,fight:400,fight:400
```
Take the torch anchor from the first `log` output. Repeat with `DREAM_TIME_OF_DAY=noon`.

Expect a torch halo and embers at night, sparks at hits, a burst on kills, dust around the view, weaker halos at noon, no square quad edges, HUD unchanged, and `fx` in the GPU line. Confirm `StartFocused` is honoured and `fight:` works unfocused; if not, drop the `Game.cs` line. Re-run after iso merges.
