# Plan: FX LDR enhancement round

**Goal:** make fire, dust, sparks and deaths read clearly in the dark gothic look at both third person and iso, with one instanced draw, no allocation and no change to gameplay lighting.

**Sequence:** C0 lands on `integrate/fx-ldr` first, then WS1, WS2 and WS3 run in parallel. Implementers run `git reset --hard integrate/fx-ldr` first. No workstream edits `PlayScene.Render.cs`, `Render.cs`, `Render.Lighting.cs` or `LocalPlayerRenderer.cs`.

**Decisions:** flashes skip once 16 lights are collected, even if some are off screen. The point light stays 1.8 m above the feet, so its ground pool doesn't match the flame height. Remote-player fire depends on `HeldLight`, which is None until protocol v6, so it is unit tested only. Cold ash fall is dropped.

## Findings

1. The halo plateau comes from the atlas: `halo_frame(core=True)` sums to 1.3 at the centre and `np.clip` cuts it. Frame 17 reads 255, 255, 249, 209, 164, 123 along its radius. The framebuffer is not saturating. The hard rim is the steep 5.5 px Gaussian after the clipped core.
2. The halo sits 0.55 m above the head. The pose is the capsule centre: feet at -0.5, head top at about +0.75, so `BodyUp` 1.3 is 1.8 m above the feet.
3. Screen scale at 1440x1024: third person 4.8 m, FOV 70, 152 px/m; iso 18 m, FOV 35, 90 px/m. Anything under about 0.02 m half size vanishes in iso.
4. Neon embers are double tinting: atlas (204,46,14) x colour (0.9,0.24,0.07) is about (184,11,1). Additive on navy sky pushes it to magenta.
5. Spark art has forks at arbitrary angles and rotation is random. The core is about 1.5 atlas px wide, so 0.05 m half size gives a sub-pixel line.
6. Ash RGB is about 0.07: dark on dark, invisible at night, which is why kills looked like hits.
7. Dust: atlas RGB peak about 60/255, half size 0.012-0.026 m, halved again at night.
8. `SelectNearest` takes 16 and a flash can push out the farthest lamp. `pointLights.glsl` falloff is `f=(1-d^2/r^2)^2`, which dust can mirror.
9. Default 24-bit depth gives under 1 mm at 30 m, so a 3 cm lift for ground quads is safe. A blob overhanging a ledge by up to 0.3 m is acceptable with soft alpha.

## C0 `fx-contract` (straight onto integrate/fx-ldr, no visual change)

**Files:** `dream/Rendering/Effects/FxInstance.cs`, `FxInstanceBuffer.cs`, `dream/shaders/fxVertex.glsl`, small edits to `FxSimulation.cs`, `dream/Client/Scenes/PlayScene.Effects.cs`, `dream.Tests/Rendering/Effects/FxPassContractTests.cs`.

```csharp
public enum FxFrame : byte { Spark0=0, Ember0=4, Dust0=8, Ash0=12, Halo=16, HaloCore=17, Flame0=18, Blob=22 }
public static class FxLimits { MaxInstances=2048; HaloBudget=128; ShadowBudget=32;
    ParticleCapacity = MaxInstances - HaloBudget - ShadowBudget; } // 1,888
public struct FxInstance { // 36 bytes; Stride=36
    Vector3 Position; float HalfSize; uint Color; float Rotation; float Frame; float DepthBias;
    public float Stretch; // 0 or 1 = square billboard; >1 = local +X length multiplier; <0 = ground aligned
    public const float GroundAligned = -1f;
}
// loc 2 becomes vec4, size 4, same RotationOffset 20.
public readonly record struct FxFrameInputs(float DeltaSeconds, Vector3 CameraPosition, Vector3 CameraFront,
    Vector3 PlayerPosition, float NightFactor, FxViewBasis Basis = default);
public int WriteInstances(FxInstance[] destination, int start = 0); // returns end index, never writes past destination
```

```glsl
layout(location = 2) in vec4 iRotationFrameBiasStretch;
float shape = iRotationFrameBiasStretch.w;
vec2 scaled = local * iPositionHalfSize.w;
scaled.x *= max(shape, 1.0);
vec2 rotated = vec2(scaled.x * c - scaled.y * s, scaled.x * s + scaled.y * c);
vec3 world = shape < 0.0 ? iPositionHalfSize.xyz + vec3(rotated.x, 0.0, rotated.y)
   : iPositionHalfSize.xyz + uCameraRight * rotated.x + uCameraUp * rotated.y + uCameraBack * iRotationFrameBiasStretch.z;
```

`DrawEffectsPass` builds the basis before `Advance` and passes it in the inputs. Update the pins: 36 bytes, vec4, attribute size 4, ParticleCapacity, frame list. Frames 18+ stay empty until WS1 merges. A short-destination test must cover `start > 0`.

## WS1 `fx-art`

**Owns:** `tools/ui-assets/gen_particles.py`, `tools/ui-assets/tests/check_particles.py`, `tools/ui-assets/previews/particles.png`, `dream/ui/particles/particles.png`, `dream/ui/particles/particles.json`, `dream.Tests/Rendering/Effects/FxAtlasAssetTests.cs`, `tools/ui-assets/README.md`.

System `python` has no Pillow; use the venv route in that README. Atlas stays 512x256, PNG at most 32 KB.

| Frames | Change |
|---|---|
| 0-3 Spark | Straight along +X only, head at +u, no forks or angle. Gaussian half-width 5 px, slight kink at most 1 px, hot toward the head, tapered tail, near-neutral, A=0. Variants differ in kink and flecks |
| 4-7 Ember | Near-neutral (max-min at most 26, tint sets colour), peak at least 200, tight nucleus r0 = 5-7.5 px, stretch 1.3-1.9, keep the noise crust, A=0 |
| 8-11 Dust | Additive now: A=0, near-neutral cold, RGB peak about 215, keep shape and loose grains |
| 12-15 Ash | Same A, soot RGB factor 0.30 instead of 0.075 |
| 16 Halo | `v = 0.46*exp(-(s/0.30)^2) + 0.30*exp(-(s/0.62)^2)`, windowed `smoothstep(1, 0.8, s)`, peak about 194 |
| 17 HaloCore | `v = 0.62*exp(-(s/0.22)^2) + 0.32*exp(-(s/0.50)^2)`, same window, peak about 240, no clip |
| 18-21 Flame | Four frames of one tongue, base at the cell bottom, tip up, about 48 px tall and 20 px wide at base, warped by fbm per frame. Baked ramp: core (1,.82,.45), mid (1,.5,.12), edge (.6,.16,.03). Peak at most 245, A=0 |
| 22 Blob | RGB=0, `A = smoothstep(1, 0, s)^1.4 * 0.92`, peak at least 200, zero at the window |
| 23-31 | Reserved, empty |

**Checks (both `check_particles.py` and `FxAtlasAssetTests`):** additive list 0-11, 16-21. Premultiplied check only 12-15. Ash `lum` limit 0.2 to 0.4; `MaxRgb` at most 90 stays. Ember dull-red checks replaced by near-neutral. Blob exempt from "no emission". JSON frames 0-22, reserved [23,31]. New no-plateau rule for 16 and 17: no texel at 255, and at most 16 texels within 3 of the frame max. Monotone falloff stays.

## WS2 `fx-sim`

**Owns:** `dream/Rendering/Effects/FxSimulation.cs`, `FxParticlePool.cs`, `dream.Tests/Rendering/Effects/FxSimulationTests.cs`, `FxAllocationTests.cs`.

### Held fire (torch and lantern)

- `HeldFxDrop = 1.0f`: `fxPos = light.Position - UnitY * HeldFxDrop`. That is shoulder height, keeps forward and right offsets, works for remote players, and the point light does not move.
- If the light is within `HaloMinCameraDistance` of the camera (first person), use `light.Position` for embers and draw no halo or flame.
- A test pins `HeldFxDrop < HeldLightAnchor.BodyUp`.
- `DepthBiasHeld` 0.2 to 0.55, because 0.2 lets the body cut the glow with a hard edge.

| | Halo half size (m) | Frame | Offset |
|---|---|---|---|
| Torch | 0.70 (was 1.1) | HaloCore | up 0.12 |
| Lantern | 0.55 | HaloCore | up 0.12 |
| Lamp | 1.25 (was 1.7) | Halo | none, bias 0.9 |

- Tint is componentwise `light.Color * HaloTint`, then `strength`, then the cull fade, so flicker carries through. Held `HaloTint = (0.375, 0.27, 0.157)`, lamp `HaloTint = (0.30, 0.22, 0.13)`.
- `HaloDayStrength` 0.35 to 0.12, which removes the white noon disc.
- Flicker: private `_clock` (accumulated dt, wrapped at 4096), `phi` = light index x 2.4. Torch: `f = 1 + .07 sin(7.3t+phi) + .05 sin(13.1t+1.7phi) + .03 sin(23.9t+.6phi)`, then `gain *= f`, `size *= 1 + 0.5(f-1)`.
- Lamp slow flicker: `f = 1 + .04 sin(1.7t+phi) + .025 sin(2.9t+1.3phi)`, `phi` from `frac(x*12.9898 + z*78.233) * TwoPi`.
- Flame, torches only, first `MaxEmberTorches` (8): instance at `fxPos + up 0.10`, half size 0.15, rotation 0, frame `Flame0 + ((int)(t*12 + i) & 3)`, white x `f` x `lerp(0.75, 1, night)`, A=0, bias 0.55. Written after the halo, in `_halos`, while `_haloCount < HaloBudget`.

### Embers

All embers: `Color = (b,b,b,0)` multiplied at write time by a heat ramp: `t=0` (1,.62,.20); `t=.35` (.95,.33,.07); `t=1` (.42,.07,.015), piecewise linear. `fade = min(1, t/.08) * (1-t)^.6 * (.85 + .15 sin(age*17 + phase))`.

| | Half size (m) | End | Velocity | Gravity / Drag | Life (s) | Rate |
|---|---|---|---|---|---|---|
| Torch plume, from `fxPos + up 0.2 +- 0.04` | 0.020-0.034 | 0.008 | up 0.7-1.3, lateral +-0.1 | +0.35 / 0.8 | 0.9-1.6 | 5/s |
| Lamp plume, from `pos + up 0.55` | x0.8 | | same | same | same | `0.5/s * night`, max 8 lamps |
| Burst | 0.028-0.050 | 0.010 | as now, cone upward | -0.3 / 1.6 | 0.8-1.6 | |

Plume curl in `StepParticles` via `FxParticle.Sway` (new float), plume embers only: `Velocity.X += sin(age*3 + phase) * 0.6 * dt`, `Velocity.Z += cos(age*2.3 + phase) * 0.6 * dt`, times `Sway`.

### Dust (additive, A=0)

- Half size 0.028-0.05; `DustTarget` stays 96; add `Phase`. Remove `(1 - 0.5*night)`.
- In `ScanLights`, keep the 6 lights nearest `_viewCentre` in a fixed `PointLight[6]` by insertion, unclassified, so flash lights count.
- In `WriteParticle`: `E = sum(light.Color * f * DustLightGain)`, `f=(1-d^2/r^2)^2` clamped, `DustLightGain = 0.45`. `E += (1-night) * (.14,.15,.16)`. `rgb = min(E,1) * edgeFade * nearFade * (.75 + .25 sin(age*1.3 + phase))`.
- Bounded at 576 evaluations per frame. No light at night gives exactly 0.

### Sparks

- `AngularVelocity = 0`; don't randomise `Rotation`. Half size 0.07 x sizeScale, end 0.025. Gravity -7, Drag 0.9, Life 0.3-0.6.
- In `WriteParticle`, with `Basis`: `vx = v.Right`, `vy = v.Up`, `Rotation = atan2(vy, vx)`, `Stretch = clamp(hypot(vx,vy) * 0.03 / halfSize, 1, 6)`. `Stretch` is 1 when the velocity is zero or `Basis` is default.
- Direction: `d = horizontal(anchor - player)`, normalised, or zero if under 0.1 m. `direction = normalize(unit + d*1.0 + up*0.35)`.
- `SpawnHit` signature unchanged; store `_playerPosition` in `Advance`.
- `AnchorDrop = 0.2f` in `SpawnHit` and `SpawnDeath`.
- `rgb *= lerp((1,1,1), (1,.55,.25), t)`, `fade = (1-t)^.7`.

| Kind | Sparks | Speed | Colour | Embers |
|---|---|---|---|---|
| Regular | 8 | 2.5-6 | (1,.72,.36) | 0 |
| Critical | 16 | 3-8 | (1,.88,.58) | 3 |
| Incoming | 6 | 2-5 | (1,.34,.14) | 2 |
| Death | 26 | 3-8, scale 1.2 | (1,.66,.30) | 20 |

### Death extras

- Ash: 14 particles, half size 0.14 to 0.50, speed up 0.5-1.4, Gravity 0.2, Life 1.4-2.4, `Premultiply(1,1,1,.75)`, tint from (1,.62,.38) to (.6,.58,.56) over `t` 0 to 0.35.
- New kind `FxParticleKind.Flash`, death only: `FxFrame.Halo`, half size 0.8 to 1.5, life 0.32, colour (.75,.40,.16,0), `fade (1-t)^2`, DepthBias 0.4.

### Tests

Fire offset and bias. Dust black at night with no light, lit near a torch, zero outside radius. Stretch and rotation from a known velocity and basis, including a velocity straight at the camera (Stretch 1). Sparks biased away from the player. Ember G/R rises at `t=0` and falls with age. Death additive energy at least 4x a regular hit. Allocation with 1,024 lights and a real `Basis`. Halo plus flame count within budget. Tests must pass a real basis.

## WS3 `fx-presence` (flash lights and contact shadows)

**Owns:** new `dream/Rendering/Effects/FxFlashList.cs`, `FxShadowList.cs`, `FxGroundProbe.cs`; `FxEventBridge.cs`; `dream/Client/Scenes/PlayScene.Effects.cs`, `PlayScene.Lights.cs`, `PlayScene.Lifetime.cs`; `dream.Tests/Rendering/Effects/FxPassContractTests.cs`, new `FxFlashListTests.cs`, `FxShadowListTests.cs`; `docs/rendering-and-assets.md`. Add the new files to the no-RNG list at `FxPassContractTests:298`.

```csharp
public enum FxFlashKind : byte { Hit, Critical, Incoming, Death }
public sealed class FxFlashList {
    public const int Capacity = 4;
    public void Trigger(Vector3 position, FxFlashKind kind);
    public void Advance(float deltaSeconds);                   // clamp as MaxDeltaSeconds
    public int AppendTo(LightCollector lights, float night);   // returns lights added
}
```

| Kind | Colour | Radius | Life (s) |
|---|---|---|---|
| Hit | (1,.62,.3)x0.9 | 3.5 | 0.11 |
| Critical | (1,.62,.3)x1.3 | 4.5 | 0.15 |
| Incoming | (1,.28,.12)x0.7 | 3.0 | 0.12 |
| Death | (1,.52,.22)x2.2 | 5.5 | 0.40 |

- Intensity is `peak * (1-t/life)^2 * lerp(0.3, 1, night)`.
- Radii never match 6, 8 or 10, so flashes never classify as halos. A test pins this.
- Displacement rule: `free = LightList.MaxLights - lights.Count`; add at most `free`, strongest first.
- Merge: a trigger within 1.5 m of a live flash restarts it only if its peak is at least the current intensity and `age >= 0.1 s`, except Death, which always restarts.
- Full list: replace the weakest if the new peak is at least it, otherwise drop.

```csharp
public sealed class FxShadowList {
    public const float Lift = 0.03f, PlayerRadius = 0.5f, EnemyRadiusScale = 1.9f, Alpha = 0.5f, MaxAirHeight = 2.5f;
    public void Clear();
    public void Add(Vector3 feet, float groundY, float radius); // ignores past FxLimits.ShadowBudget
    public int Write(FxInstance[] destination, Vector3 camera, Vector3 viewCentre);
}
public static class FxGroundProbe {
    public const int MaxCellsDown = 8;
    public static bool TryFindGroundY(Map map, Vector3 feet, out float groundY);
}
```

- `Write`: `Position = (x, groundY + Lift, z)`, `Frame = Blob`, `Stretch = GroundAligned`, `Color = PackColor(0,0,0, Alpha * k')`. `k = clamp(1 - height/MaxAirHeight)`, alpha x `(.35 + .65k)`, size x `(1 + .3(1-k))`. Skip beyond 30 m of the view centre and within 1.6 m of the camera.
- Probe: cells are `[i, i+1)`, use `TryGetGeometryCellAtCell(floor x, y, floor z)` walking down from `floor(feet.Y - 0.01)`. A non-null record gives `y + 1`. No ground in range means no shadow.
- Only players probe (at most 1 + remote count, capped at budget). Enemies use their pose Y.

**Wiring:** `PlayScene.Lights.cs` gets one line after lamps: `CollectEffectLights(lights, remotePlayers);` and `partial void CollectEffectLights(LightCollector lights, IReadOnlyList<RemotePlayerPresentationState> remotePlayers);`, implemented in `PlayScene.Effects.cs`: store `remotePlayers` in a frame-scoped field and call `_fxFlashes?.AppendTo(lights, _fxLastNight)`.

`DrawEffectsPass` order:
1. `_fxFlashes.Advance(dt)`
2. `simulation.Advance`
3. `_fxShadows.Clear()`, add local player (feet = pose - 0.5, probed)
4. Indexed loop over remote players (pose - 0.5, probed; skip if not `IsAlive`)
5. Indexed loop over `presentation.ActiveEnemies` with `EnemyRenderer.ResolveRenderPose(actor, _scenePresenter, RemotePlayerRenderTick).Position`, radius `actor.CollisionRadius * EnemyRadiusScale`
6. `int n = _fxShadows.Write(_fxInstances, camera, viewCentre)`
7. `int count = simulation.WriteInstances(_fxInstances, n)`

Shadows come first, because a dark quad drawn later would dim the flame in iso.

`FxEventBridge(SceneEventHub events, FxSimulation simulation, FxFlashList flashes)` triggers the flash at `evt.Actor.GetWorldAnchor()` in the handler beside the existing spawn. `Kind` maps Critical, Incoming and other to Hit. Update the contract-test strings at lines 225 and 250.

`PlayScene.Lifetime.cs`: flashes and shadows are created beside the simulation and nulled in `DisposeEffects`, so a missing atlas also means no flashes and no shadows.

Docs: update the Effects section for 36 bytes, 1,888 particles, 0.12 noon strength, additive dust ("alpha above 0 also darkens: ash, blobs"), frames 18-22, the flash rule, and the blob.

## Merge order

1. C0 (already on the branch).
2. WS1.
3. WS2.
4. WS3.

## Testing

Plain `dotnet build dream/dream.csproj`, then `MSYS_NO_PATHCONV=1 dotnet test dream.Tests/dream.Tests.csproj "/p:BaseOutputPath=<absolute G:/ path inside the worktree>/artifacts/verify-bin/"`. Max 2 full-suite runs per step. Run all `check_*.py`.

Allocation: `AllocationAssert.AtMost(0, ...)` around `Advance` + `WriteInstances`, `AppendTo` on a full collector, `FxFlashList.Trigger`/`Advance`, shadow `Add`/`Write`, `TryFindGroundY`.

Capture recipe: scratch folder on G:, `dream/bin/Debug/net10.0/libbulletc*` beside the build, scratch `config/player-profiles.db`, throwaway `DREAM_IDENTITY_FILE`, db deleted between identities, `camera_view=third_person`. Script `play,inventory,drag:2:main,drag:7:off,close,wait:90,snap,fight:600,fight:300`, repeated.

| Capture | What to judge |
|---|---|
| Midnight, snap with torch | Flame at the right shoulder, halo tied to the arm, no disc. On a horizontal row through the halo centre, no run of 5 px within +-1 at gain 1 |
| Midnight, fights, at least 3 kills | Sparks point outward and fall; ember plume rises, orange not magenta; kills obvious against hits; ground flash visible |
| Same, crop at gain 1 and with gain | Note the gain used; `crop24.png` was brightened |
| Midnight with no torch | Dust invisible, burst still reads, black stays crushed |
| Noon, same script | No white disc; flame visible; blob under player and wolves; no quad edges |
| Lamp, only if reachable | Otherwise unit tests only |
| Jump next to a ledge | Blob stays on the ground, no z-fight |
| Degrade: delete `ui/particles/particles.png` from the scratch folder only | One `effects` line in the log, game plays through a fight, no flashes, no exception |
| Perf log | `fx` GPU ms against the `nightB` log; draw count unchanged |
| Iso scratch merge: throwaway worktree and branch from `integrate/fx-ldr`, `git merge integrate/isometric`, `camera_view=isometric`; never touch `.claude/worktrees/iso-int`; delete after | Flame and halo sizes at 18 m, plume height, sparks at least 2 px wide, blob under actors, halo not darkened by blob, sky-less dim clear does not make halos look stuck on |

If iso looks weak, raise sizes in FxSimulation constants only, and re-check third person.
