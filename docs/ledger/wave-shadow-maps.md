# WS-D — REAL SHADOW MAPPING: design and implementation plan

**Goal (one sentence):** Replace the constant black ground blobs with a real shadow-map system — one tight, texel-snapped orthographic map for the sun/moon and a 4-face-per-light omnidirectional atlas for the fires — composed through a single GLSL source of truth, with the fires' live flicker driving the depth maps themselves so a flickering fire genuinely casts moving shadows.

---

## 0. Verified ground truth (with anchors)

Everything below was read, not assumed.

**There is no shadow map.** Shadows today are `dream/Rendering/Effects/FxShadowList.cs:67-98`: one ground-aligned quad per actor at the probed ground height, `Color = PackColor(0,0,0, alpha*(0.35+0.65k))`, atlas cell `FxFrame.Blob`, capped at `FxLimits.ShadowBudget = 32` (`dream/Rendering/Effects/FxInstance.cs:33`). Lit by nothing. Props, flora, terrain cast nothing.

**Recommendation on the blobs: DEMOTE to a fallback, do not delete.** Keep `FxShadowList`/`FxShadowRules`/`FxInstance`/`fxVertex.glsl` exactly as master (WS-A already commits to that, `docs/ledger/wave-living-world.md`), and make the blob emission conditional on `!ShadowSettings.Enabled`. Reasons: (a) `DREAM_SHADOWS=0` must reproduce the pre-change frame **byte for byte** for the A/B capture gate, which is only possible if the blobs come back when shadows are off; (b) the blob is the only shadow on an actor beyond the sun map's 40-unit range; (c) deleting it would churn WS-A's files on the integration branch for no gain. One `if` in `PlayScene.Effects.cs:78 CollectShadows`.

**Lighting UBO.** `dream/shaders/lighting.glsl:8-11` ↔ `dream/Rendering/Lighting/FrameLighting.cs:14-22`, 9 × vec4 = 144 B on master, binding point 0, uploaded in two ranges by `FrameLightingBuffer.cs:91-102` with `ViewModelOffset = RoundUp(SizeInBytes, GL_UNIFORM_BUFFER_OFFSET_ALIGNMENT)` (`:30`). WS-C is taking it to 176 B / 11 members (`CloudParams` @144, `FrameTime` @160). `FrameLightingContractTests.cs:30-42` pins the size, the member count, **and that every member is a `vec4`**, and `:45-63` re-parses the GLSL block with a regex expecting only `vec4 uName;` lines.

**Point lights.** `LightList.MaxLights = 16`, `SizeInBytes = 528`, binding point 1 (`dream/Rendering/Lighting/LightList.cs:18-26`), mirrored in `dream/shaders/pointLights.glsl:4-8`. `PointLight` is `(Position, Radius, Color)` only (`PointLight.cs:7`). Selection: `LightSelection.SelectNearest` (`LightSelection.cs:16-35`) keeps the 16 lights **nearest the camera whose radius-sphere touches the view frustum**, stable on ties, allocation-free; driven from `Render.SelectPointLights` (`Render.PointLights.cs:73-82`) over `LightCollector` (capacity 1024). Braziers: `PlacedLightIndex` — `Spacing = 16`, `CollectDistance = 30`, "about 11 braziers (13 at most)" within range, `BrazierRadius = LampLightIndex.LampRadius = 10`, colour `(1,0.72,0.46) × 3.0`. Torch: `HeldLightCatalog.TorchRadius = 8`, intensity 2.1.
`BoundaryMistFrame.BindingPoint = 2`. **Binding point 3 is free.**

**Programs.** `Render.Shaders.cs:13-23`: `cube`, the terrain variant (`Shader.TerrainDefine`) and `enemy` **all use `cubeVertex.glsl`**. Each of `cube`/`enemy` also carries an auto-built `AlphaMaskVariant` (`Render.Shaders.cs:43-57`, `Shader.cs:157`), selected per material by `AnimatedModel.SelectProgram` (`AnimatedModel.cs:491-495`).
`#include` resolution is **one level only** (`Shader.ResolveIncludes`, enforced by `FrameLightingContractTests.cs:139-147`). Current graph: `lighting.glsl` is included by `cubeFragment`, `enemyFragment`, `fxFragment`, `warMistFragment`, `boundaryVoidFragment`; `pointLights.glsl` only by `cubeFragment` and `enemyFragment`.

| program | samples the shadow maps? |
|---|---|
| `cube` + its MASK variant | **yes** (world cubes, props' neighbours, scatter decal/cover) |
| terrain variant (`DREAM_TERRAIN`) | **yes** — the main receiver |
| `enemy` + its MASK variant | **yes** (actors, props, scatter accent/landmark) |
| new `shadowDepth` + its MASK variant | no (it *writes* the maps) |
| `fxFragment` | **no** — additive flames/particles must not be shadowed |
| `skyFragment`, `boundaryMist.glsl`, `boundaryVoidFragment`, `warMistFragment` | **no** — sky and volumetric smoke |
| `uiFragment`, `hudFragment*`, `fragmentShader.glsl`, view models | **no** |

**Camera (the key exploit).** `dream/Client/Presentation/IsometricView.cs:10-15`: pitch **50°**, yaw **45°**, distance **18**, FOV **35°**, near 1, far 98 — fixed, no rotation, no zoom; first person is gated out at `PlayScene.Render.cs:118,145,215`. `FocusToCamera = (8.18, 13.79, 8.18)`.
Derived footprint at 1440×1024 (aspect 1.406 → horizontal half-angle 23.9°): the top screen ray is pitched 32.5° down, the bottom 67.5°, so the visible ground runs **-5.9 → +10.1 units along the view azimuth from the focus** and **±11.4 units laterally** — a ground footprint of roughly **16 × 23 world units**. Draw distances: terrain `WorldVisibility.MaxRenderDistance = 48`, props `PropRenderer.DrawDistance = 42`, scatter 24/24/34/42, chunks are 16³ (`StaticWorldChunkKey.DefaultChunkSideInWorldUnits = 16`).
**Quantified consequence: no cascades are needed.** See §1.

**Sun and moon are never both lit.** `SkyModel.cs:36` `moon = -sun`, and both intensities pass through `SmoothStep(LightFadeBelow = -0.04, LightFadeAbove = 0.06, body.Y)` (`:44-45`). One directional light is significant at any time; one directional map serves both.

**GL context.** `Game.cs:95-106` passes no `APIVersion`, so OpenTK 4.9.4 defaults to **GL 3.3 Core** — geometry shaders exist (core since 3.2) but `Shader` only compiles vertex+fragment, and **`GL_TEXTURE_CUBE_MAP_ARRAY` is GL 4.0, not available**. That single fact decides the fire design (§2). MSAA: `NumberOfSamples = MsaaSettings.ResolveFromEnvironment(...)`, **default 4**, allowed {0,2,4}, a window-creation hint on the *default* framebuffer only.

**Texture units in use:** `AnimatedModel.cs:601-605` uses 0-4 for material textures; terrain uses 2 (`TerrainTilesUnit`) and 3 (`TerrainMaterialMapUnit`); war mist uses 1. **Units 5 and 6 are free.**

**Flora is not alpha-planed.** `groundkit.py:166-174`: a flora asset is **one OPAQUE primitive** with `double_sided: True`, plus an optional **alpha-MASK contact disc** (`CONTACT_MATERIAL`, cutoff 0.5) — a fake-AO disc. Decal-layer assets are alpha-MASK quads lying on the ground. **So the caster pass needs no alpha testing at all, provided MASK primitives are skipped** (§4).

**Instrumentation.** `GpuFrameTimer.cs:7-13` (`GpuTimerSection`, **`private const int SectionCount = 4;` at `:61`**), `RenderCounters.cs` (`CountDraw`, per-thread), `WorldRenderStats.cs`, HUD row 14 in `FramePerformanceSnapshot.cs:131,157`, perf harness `skills/profiling-dream-multiplayer/scripts/launch-and-summarize.ps1` (`docs/build-and-run.md:135`), captures via `skills/capturing-dream-screenshots`.

**Measured baseline** (`docs/ledger/plans/2026-11-01-isometric-feasibility.md:134-143`, before props and scatter landed): iso perspective **556 FPS (1.80 ms/frame), 42 draw calls, 25,498 triangles, 6 chunks, GPU world 0.13 ms**. The frame is **CPU-bound on draw submission by ~14×**. That is the budget headroom this design spends.

---

## 1. The sun/moon shadow

### Technique: ONE orthographic map. No cascades.

Justification, quantitatively. Because pitch, yaw, FOV and distance are all constants, the receiver region is a **constant-size box that only translates**. Define:

```
ReceiverSide   = 40f   // covers the measured 16x23 ground footprint plus relief and tall props
ReceiverHeight = 26f   // map height 8 + tallest landmark ~12 + 6 margin
```

A caster whose shadow lands inside the receiver box necessarily shares the receiver's **light-space XY** footprint (its shadow ray terminates inside the box). So the lateral extent of the ortho needs to bound the receiver box only; casters are admitted by pulling the **near plane** back, which costs no texels. The light-space footprint of a `S × S × H` box at sun elevation θ is `S × (S·cosθ + H·sinθ)`, maximised over θ at `sqrt(S² + H²)`. Therefore:

```
// DERIVED, with a test pinning the relation (house style, no number in a comment):
OrthoSide = MathF.Sqrt(ReceiverSide*ReceiverSide + ReceiverHeight*ReceiverHeight)  // 47.707
```

A **constant** ortho side means a **constant world-space texel size**, which is what makes texel snapping exact and shimmer-free all day. At `SunResolution = 2048`:

```
TexelWorld = OrthoSide / SunResolution = 0.023295f   // 2.33 cm
```

For scale: the player figure is ~1.9 m tall (`wave-living-world.md`, 1.408 model units per metre) and only ~101 px tall on screen, i.e. **1 screen pixel ≈ 1.9 cm**. A 2.33 cm shadow texel is therefore roughly *one screen pixel*. A second cascade would buy nothing visible. **This is the design's single biggest win and it comes entirely from the fixed camera.**

- Texture: `GL_DEPTH_COMPONENT32F`, 2048², `GL_TEXTURE_COMPARE_MODE = COMPARE_REF_TO_TEXTURE`, `COMPARE_FUNC = LEQUAL`, `MIN/MAG_FILTER = LINEAR` (free hardware 2×2 PCF per tap), `WRAP_S/T = CLAMP_TO_BORDER` with `TEXTURE_BORDER_COLOR = (1,1,1,1)` so **outside the volume reads as fully lit**. 16.8 MB.
- Knob: `DREAM_SHADOW_RESOLUTION ∈ {512, 1024, 2048, 4096}`, default 2048 (sanitised to the nearest allowed value, exactly like `MsaaSettings.Sanitize`).

### Light-space matrix construction

```
dominant   = SunDirection.W >= MoonDirection.W ? SunDirection : MoonDirection   // they are antipodal; never both lit
L          = normalize(dominant.xyz)                                            // points TOWARD the light
centre     = focus + ViewAzimuthUnit * ReceiverCentreOffset                     // ReceiverCentreOffset = 3f, along (cos45, 0, sin45)
up         = abs(L.y) > 0.99 ? UnitZ : UnitY
lightView  = Matrix4.LookAt(centre + L * BackOff, centre, up)                   // BackOff = OrthoSide (arbitrary; the ortho fixes the planes)
// texel snap, in light space, BEFORE the projection:
c          = Vector3.TransformPosition(centre, lightView)
snapped    = new Vector3(Snap(c.X, TexelWorld), Snap(c.Y, TexelWorld), c.Z)     // Snap(v,t) = MathF.Round(v / t) * t
depthRange = OrthoSide + CasterExtension(elevation)
lightProj  = Matrix4.CreateOrthographicOffCenter(
                 snapped.X - OrthoSide*0.5f, snapped.X + OrthoSide*0.5f,
                 snapped.Y - OrthoSide*0.5f, snapped.Y + OrthoSide*0.5f,
                 -(OrthoSide*0.5f) - CasterExtension(elevation),    // near, pulled back toward the light
                  OrthoSide*0.5f);                                  // far
lightViewProjection = lightView * lightProj                                     // row-vector order, as everywhere in this repo
```

`CasterExtension` is derived, not literal, and clamped:

```
// A caster h above the receiver casts along the light from h / sin(elev) away.
// Clamped to twice the visible draw distance: a caster the visible pass never draws must not
// cast either, or the shadow and its object disagree.
MaxCasterExtension = 2f * WorldVisibility.MaxRenderDistance;                    // 96
CasterExtension(elev) = MathF.Min(ReceiverHeight / MathF.Max(MathF.Sin(elev), SkyModel.LightFadeAbove),
                                  MaxCasterExtension);
```

`CasterExtension` saturates below sin(elev) ≈ 0.27 (≈ 15.7°), so very long dawn/dusk shadows from far-off tall geometry are missing — an honest limit, and it bites only while the sun intensity is already fading through the `LightFade` band.

**Shimmer.** Two sources, both handled: (a) camera translation — killed by the light-space XY snap above; (b) a changing texel size — impossible, because `OrthoSide` is constant. The near plane moves with the sun elevation, which changes the depth *encode* but not the XY sampling grid; the depth bias is therefore expressed in world units and divided by the live `depthRange` (see below), which keeps the bias constant in metres.

### Bias strategy — all three terms, with values

Because the ortho depth is linear, a world-unit bias converts exactly: `biasDepth = biasWorld / depthRange`. CPU-mirrorable in one line.

```
NormalOffsetTexels  = 1.5f   -> normalOffsetWorld = NormalOffsetTexels * TexelWorld = 0.0349  (3.49 cm)
ConstantBiasWorld   = 0.006f (6 mm)
SlopeBiasWorld      = 0.020f (2 cm) per unit of tan(acos(NdotL)), clamped to 0.080f (8 cm)
```

In the shader:

```glsl
float ndl    = max(dot(n, L), 0.0);
float tanT   = sqrt(max(1.0 - ndl*ndl, 0.0)) / max(ndl, 0.15);          // 0.15 caps the grazing blow-up
vec3  offset = n * (uSunShadowParams.z * clamp(tanT, 0.0, 4.0));         // .z = normalOffsetWorld
vec4  lp     = uSunShadowMatrix * vec4(fragPos + offset, 1.0);
float bias   = uSunShadowParams.y + uSunShadowParams.w2 * min(tanT, 4.0); // constant + slope, both already /depthRange
```

- **Peter-panning:** the normal offset moves the *lookup*, not the depth, so the contact point stays dark — that is the whole point of choosing normal-offset over a large constant bias. The only peter-panning source left is the 6 mm constant bias, which at 1.9 cm/pixel is **0.3 px**. Invisible.
- **Acne:** handled by the slope term, because the caster pass **cannot** use front-face culling (§4) — static chunk meshes are open shells of exposed faces only, so culling front faces would delete the ground's top faces from the depth map entirely. State this in the code comment; it is the non-obvious constraint.
- **Double-sided flora:** culling is off in the caster pass anyway, so a double-sided leaf sheet writes the nearer of its two faces — correct. Its silhouette is real geometry, not an alpha plane (verified in `groundkit.py:166-168`), so no alpha-to-coverage trickery is needed.
- **`glPolygonOffset` is deliberately NOT used**: its units are implementation-defined against a float depth buffer, so it cannot be CPU-mirrored or pinned by a test. Everything above can.

### Filtering

**9-tap PCF, 3×3 at 1.5-texel spacing, over `sampler2DShadow` with `GL_LINEAR`** — so each tap is itself a hardware 2×2 compare, giving an effective 5.33-texel kernel = **12.4 cm penumbra**. That reads as a soft-edged, heavy shadow at 1.9 cm/pixel (a ~6 px gradient), which is the grim, overcast register the art direction wants, and it needs no noise.

**PCSS is rejected.** It needs a blocker search (16+ taps), produces a variable kernel that makes the CPU mirror impractical to pin, and is noisy without a temporal or screen-space filter — and this project has neither. MSAA 4× antialiases *geometry* edges but **not** a shadow boundary inside a triangle, so a dithered/rotated kernel would show as grain with nothing to resolve it. Fixed kernel, no noise.

Knob: `DREAM_SHADOW_TAPS ∈ {1, 4, 9}`, default 9.

---

## 2. The fire/point-light shadows — the hard half

### Options evaluated

| option | verdict |
|---|---|
| **Cube shadow map per light** | **Not expressible in GLSL 330.** `GL_TEXTURE_CUBE_MAP_ARRAY` is GL 4.0; with per-light cubemaps you need N `samplerCube` uniforms and a *dynamic* sampler index, which GLSL 330 forbids. Only a fully unrolled constant-index loop works — N programs' worth of code for a variable N. Rejected on language grounds, not cost. |
| **Single-pass cube via a geometry shader** | GL 3.3 *does* support layered rendering and `gl_Layer`, so it is technically available — but `Shader` compiles vertex+fragment only (`Shader.cs`), so it would mean a new pipeline stage in a contract-tested class, and GS amplification is slower than N passes on current drivers. Rejected. |
| **Dual paraboloid** | 2 passes/light, but the paraboloid warp is non-linear and its seam sits **exactly at the fire's horizon plane — the ground**, the one place quality matters. Terrain quads are 1×1 units and subtend large angles near a light at height 1.0, so the warp error lands on the brazier pool. Rejected. |
| **Restricted volume: a 4-face horizontal ring in a shared 2D atlas** | **Recommended.** |

### Recommendation: a 4-face horizontal ring per light, packed into one shared 2D depth atlas

The geometry justifies the restriction. `PlacedLightIndex.FlameHeight = 1.0` above the column top, radius 10. The directions from a fire to everything it visibly lights span roughly **-6° to +25°** of elevation (ground out to 10 units: down to 5.7° below horizontal; standing geometry 2-3 units tall within 10 units: up to ~11°; a near actor: up to ~25°). The ±Y cube faces (elevations beyond ±45°) are nearly empty. So:

- **4 faces per light**: `+X, -X, +Z, -Z`, each a 90°×90° perspective frustum. The two 45°-apex polar cones are left **unshadowed**, which is the *safe* failure direction (over-lit, never black). What it costs: a fire has no shadow on the ground within ~1.0 unit horizontally of itself (inside the brazier bowl) and nothing directly overhead is shadowed.
- **One shared atlas** `2048 × 2048`, `GL_DEPTH_COMPONENT24`, a 4×4 grid of **512²** tiles = 4 slots × 4 faces. 16.8 MB. One texture, one sampler (unit 6), one FBO, one clear. **This is the only structure that lets a variable number of shadowing lights be sampled from one sampler in GLSL 330** — the decisive reason over cubemaps.
- Resolution check: a 90° face at 512² covers `2d` world units across 512 texels, so **1.2 cm/texel at d = 3** (a typical occluder distance from a brazier) and 3.9 cm/texel at d = 10. Finer than the sun map where it counts.
- **No matrices in the UBO.** Because the four axes are fixed world axes, the shader selects a face and computes the tile UV arithmetically from the light position alone — the restricted cube-face selection, with `|d.y|` major meaning "outside the ring, visibility 1":

```glsl
vec3  d = fragPos - lightPos;
vec3  a = abs(d);
if (a.y >= max(a.x, a.z)) return 1.0;                       // polar cone: unshadowed
int   face; vec2 uv; float major;
if (a.x > a.z) { face = d.x > 0.0 ? 0 : 1; major = a.x; uv = vec2(d.x > 0.0 ?  d.z : -d.z, d.y) / major; }
else           { face = d.z > 0.0 ? 2 : 3; major = a.z; uv = vec2(d.z > 0.0 ? -d.x :  d.x, d.y) / major; }
uv = uv * (0.5 * uFireShadowTile.z) + 0.5;                  // .z = the gutter-shrunk half-scale
float zn    = ((f + n) / (f - n)) + (2.0*f*n / (f - n)) / (-major);   // the same perspective depth the pass wrote
float depth = 0.5 * zn + 0.5;
```

> **Correction (integration of WS-D2/D3).** The sign-free form this snippet originally carried — `uv = vec2(d.z, d.y) / major` for *both* x faces and `vec2(d.x, d.y) / major` for *both* z faces — **was wrong for `-X` (face 1) and `+Z` (face 2)**; `+X` and `-Z` were already right. It only works if the `-X` and `+Z` face cameras are built with right axes of `+Z` and `+X`, which makes those two view matrices **mirrored (determinant -1)** and silently inverts triangle winding in two of the sixteen tiles. The convention is therefore **plain `Matrix4.LookAt(lightPos, lightPos + FaceForward(face), +Y)` for all four faces** (`FireShadowList.FaceForward` is the one definition, `FireShadowAtlas.FaceView` is the LookAt), with the per-face lateral sign carried in the GLSL as corrected above. The reason to keep every face right-handed: front-face culling in the caster pass is the standard peter-panning fix and would otherwise cull the wrong faces in exactly those two tiles.

- `near = 0.10f`, `far = light.Radius * 1.15f`. Past the radius the windowed falloff in `pointLights.glsl:18` is exactly 0, so far-plane clipping is invisible. The pass and the lookup read `near`/`far` from the **same UBO value**, so the encode round-trips exactly.
- **Gutter**, derived: `TileGutter = 4` texels, rendered FOV widened so the 90° cone lands inside the inner 504², and the gutter cleared to 1.0, so a 4-tap PCF kernel at a face edge can never read the neighbouring tile:

```
FaceFovRadians = 2f * MathF.Atan(MathF.Tan(MathF.PI/4f) * TileSide / (TileSide - 2f*TileGutter));  // 90.92 deg
```

- PCF: **4 taps** at ±0.5 texel over `sampler2DShadow`/`GL_LINEAR`, i.e. an effective 3×3-texel kernel = 3.6 cm at d = 3, 11.7 cm at d = 10 — the penumbra naturally widens with distance, which is physically right for a small source. Clamp each tap's UV into the tile's inner rect.

### How many fires cast, and how they are chosen

`LightSelection` already gives the frame's 16 **nearest-to-camera, frustum-touching** lights, stable on ties. The shadow slots pick from *that* list — never from `LightCollector` — so they can never disagree with what is lit:

```
FireShadowAtlas.SelectSlots(in LightList list, Vector3 focus, int maxSlots, Span<int> slots)
// score = luminance(Color) * windowedFalloff(|Position - focus|, Radius)   // the same window as pointLights.glsl:16-19
// keep the maxSlots highest scores; ties keep the lower LightList index (stable, like LightSelection)
```

Picking by *contribution at the focus* rather than by distance means the slots are the fires the player is actually standing in the light of, and the ordering is stable as the player walks (the score is smooth). Default `CastingFireCount = 2`, max 4 (`DREAM_SHADOW_FIRES`, 0..4).

### Update cadence: **every frame, no amortisation**

Amortising across frames is rejected outright, because it defeats goal 5: a light whose depth map is 2 frames stale casts shadows that *lag* the flame, which reads as a rubber-band wobble — worse than no flicker. Re-render all active slots every frame.

### How the flicker drives the result — literally

WS-A's `FireFlicker.Sample(kind, position, clockSeconds, seed) -> (Intensity, RadiusScale, Offset)` is applied by WS-A when building the `PointLight`s into `LightCollector`, so **the selected `LightList` entries already carry the flicker**: `PositionRadius.xyz` is the flickered position (base + `Offset`), `PositionRadius.w` the flickered radius (base × `RadiusScale`), `Color` the flickered intensity. The shadow pass reads *those* values. Therefore, with no extra code:

- the depth map's **origin moves** with `Offset` every frame → every shadow the fire casts **translates**, and the translation is amplified for far receivers exactly as real parallax demands (a receiver at distance `R` behind an occluder at `O` moves by `Offset × (R/O − 1)`);
- `far = Radius × 1.15` **breathes** with `RadiusScale` → the depth encode and the shadow's reach change with the flame;
- `Color` scales the lit term as before.

The only requirement this imposes is **ordering**: the shadow pass must run after `SelectPointLights`. See §4.

**Goal-5 acceptance:** `FireShadowFlickerTests` (§6) asserts that two pinned `DREAM_FX_CLOCK` times give *different* face origins and far planes, and that a fixed occluder/receiver pair's computed shadow boundary moves by a non-zero, bounded amount. That is the literal "casts DIFFERENT shadows" gate.

### Memory and fill

Atlas 16.8 MB + sun 16.8 MB = **~34 MB**. Fill: 2048² (sun) + 8 × 512² (two fires) = **6.3 M depth-only samples**, of which only the actually-covered fraction rasterises. Well under 1 ms on any GPU that currently spends 0.13 ms on the world pass.

---

## 3. Composition — one source of truth

### The constraint and how it is honoured

`dreamDirect(vec3 n)` **must not change signature**: four shaders call it, `FrameLightingContractTests.cs:90` pins the `enemyFragment` call site character for character, and `tools/ui-assets/enginelook.py:22-27` restates it. WS-C is already adding `dreamDirectShaded(vec3 n, vec3 fragPos)` beside it for the cloud `sunMask`. **WS-D extends that function — same signature, one new multiplicand — and leaves `dreamDirect` untouched.**

### Include order and placement

Includes are one level, resolved textually, so a function must be defined *above* its caller. The layout that makes everything work with no forward declarations:

```
cubeFragment.glsl / enemyFragment.glsl:
    #include "lighting.glsl"       // FrameLighting block, uSunShadowMap, dreamSunVisibility, dreamDirectShaded
    #include "fireShadows.glsl"    // NEW: FireShadowList block (binding 3), uFireShadowAtlas, dreamFireVisibility
    #include "pointLights.glsl"    // LightList block, dreamPointLights -- now multiplies by dreamFireVisibility
    #include "boundaryMist.glsl"   // unchanged; keeps calling dreamDirect (unshadowed: it is a volumetric wall)
```

`fireShadows.glsl` is included **only** by `cubeFragment` and `enemyFragment`, so `fxFragment`, `warMistFragment` and `boundaryVoidFragment` never declare the fire block or sample the atlas. They do inherit the (unused) `uSunShadowMap` declaration from `lighting.glsl`; harmless, but the unit must still hold a valid texture — see "always bind" below.

### The single lighting expression

```glsl
// lighting.glsl  (WS-D adds dreamSunVisibility and ONE multiplicand inside dreamDirectShaded)
vec3 dreamDirectShaded(vec3 n, vec3 fragPos) {
    return dreamDirect(n) * dreamCloudSunMask(fragPos) * dreamSunVisibility(fragPos, n);
}

// pointLights.glsl  (signature unchanged -- the pinned enemyFragment call site is untouched)
vec3 dreamPointLights(vec3 n, vec3 fragPos) {
    ...
    sum += uPointLightColor[i].rgb * (f * (0.25 + 0.75 * lambert)) * dreamFireVisibility(i, fragPos, n);
}
```

so the final world expression is **unchanged text** in both fragment shaders:

```glsl
// cubeFragment.glsl
lit = (dreamAmbient(n) + dreamDirectShaded(n, fragPos) + dreamPointLights(n, fragPos)) * baseColor.rgb;
// enemyFragment.glsl -- FrameLightingContractTests.cs:90's regex still matches verbatim
lit = (dreamAmbient(n) * uLightingParams.z + (dreamDirectShaded(n, fragPos) + dreamPointLights(n, fragPos)) * ActorDirectScale) * baseColor.rgb;
lit = max(lit, baseColor.rgb * uLightingParams.y);
```

`dreamSunVisibility` and `dreamFireVisibility` return **exactly 1.0** when `uSunShadowParams.w == 0` / `uFireShadowCount.x == 0`, with no texture fetch on that path. **That is what makes `DREAM_SHADOWS=0` bit-identical to the pre-change frame.**

- **Ambient is NOT shadowed.** There is no AO in this design; a fully shadowed surface falls to ambient only. Honest limit (§8).
- **Nothing can violate a minimum-brightness assertion.** Shadowing only multiplies light *down*, and `enemyFragment.glsl:69`'s floor `lit = max(lit, baseColor.rgb * uLightingParams.y)` already guards the actor worst case (0.35 × albedo by day). Terrain has no floor, so shadowed terrain drops to ambient — which at night is `(0.016, 0.024, 0.048)`.

### Exactly what the two art instruments must be told

**`dream.Tests/Rendering/Lighting/GrimLookHistogramTests.cs`** — the CPU mirror gating hours 0, 20, 19, 5 against `BlackShareMin 0.45`, `ModelBrightShareMin 0.002`, `MeanVMax 0.18`, `MeanSatMax 0.38`:
- At hours **0 and 20 the sun intensity is 0** (sun below horizon, `SkyModel.cs:44`), and at 19:00/05:00 it is in the `LightFade` band — so the **sun** map barely moves this gate. The **fire** shadows are what move it.
- Direction of each band: `BlackShare` can only **rise** (assertion is `>=`, safe). `MeanV` can only **fall** (assertion is `<=`, safe). `BrightShare` **falls** — the gate at risk is `ModelBrightShareMin 0.002`, though the brazier pool immediately around a fire is unoccluded so most of the bright tail survives. `MeanSat` **rises**, because a shadowed night pixel falls to the blue-ish ambient — **`MeanSatMax 0.38` is the band most likely to break.**
- Instruction: the test's ray-march already builds a column heightfield (`:82-91`), so a CPU mirror of `dreamFireVisibility` against that heightfield is genuinely feasible. Add shadows to the mirror behind a flag, keep the four existing rows at visibility 1 (they pin the unoccluded contract), and add four new shadowed rows with their own measured bands. Do **not** silently widen `MeanSatMax`; measure it, print it with `output.WriteLine` as the test already does, and if it exceeds 0.38, report the number and ask.

**`tools/ui-assets/enginelook.py`** — restates the shader contract character for character (`:22-27`) and is the instrument behind the A3/M3 art gates:
- The restated formula becomes incomplete without a visibility term. Add a documented `visibility: float = 1.0` field to `Directional` and `PointLight`, multiply it into `direct(n)` and into each point contribution, and update the docstring block so it still matches the shaders character for character:
  `direct(n) = max(dot(n,sunDir),0)*sunI*sunCol*sunV + max(dot(n,moonDir),0)*moonI*moonCol*sunV`
  `contribution = colour * f * (0.25 + 0.75*max(dot(n,l),0)) * lightV`
- Add a docstring paragraph stating plainly: **enginelook has no scene geometry, so it measures the unoccluded case (V = 1), and the engine still reproduces it exactly when nothing occludes. With real fire shadows the figure self-shadows (an arm across the torso), which can only *widen* the measured value spread — so enginelook's A3 figure is now a LOWER BOUND on the shipped spread.** That is good news for A3, which asks for ≥ 0.45 against a measured 0.262-0.338.
- Add a self-test row for a non-unit visibility, so the new multiplicand is covered by `python tools/ui-assets/enginelook.py`.

### Byte layout at offset 176 — and the free lunch

WS-C leaves the struct at 176 B with `ViewModelOffset = RoundUp(176, 256) = 256`, i.e. **80 bytes of alignment padding are already paid for**. WS-D's additions fit exactly inside it:

| offset | member | contents |
|---|---|---|
| 0-143 | (master, 9 × vec4) | unchanged |
| 144 | `CloudParams` | WS-C |
| 160 | `FrameTime` | WS-C |
| **176** | `SunShadowMatrix0` | row 0 of the C# `Matrix4` |
| **192** | `SunShadowMatrix1` | row 1 |
| **208** | `SunShadowMatrix2` | row 2 |
| **224** | `SunShadowMatrix3` | row 3 |
| **240** | `SunShadowParams` | x = 1/resolution, y = constant bias (already ÷ depthRange), z = normal-offset world scale, w = strength (0 = off) |
| | **SizeInBytes = 256** | 16 × vec4 |

**Consequences, stated as asked:** `ViewModelOffset = RoundUp(256, 256) = 256` — **unchanged from WS-C's 256**. Total buffer `256 + 256 = 512 B` — **unchanged**. No extra GPU memory, no new rounding behaviour. (If `GL_UNIFORM_BUFFER_OFFSET_ALIGNMENT` were 64 on some driver, the offset would move 192 → 256; `FrameLightingContractTests.cs:203-212` already theory-tests `RoundUp`, so add rows for 256.)

**The matrix is stored as four `vec4` members, not a `mat4`**, deliberately: `FrameLightingContractTests.cs:37-40,57` asserts every member is a `vec4` and its GLSL regex only matches `vec4 uName;`. Four vec4s preserve that invariant; only the counts 11 → 16 and 176 → 256 change. In GLSL:

```glsl
// The four ROWS of the C# Matrix4 read as the COLUMNS of the GLSL mat4, so GLSL's M * v equals
// C#'s v * M -- the same row-vector convention FrameLightingBuffer.cs:126-128 already documents.
mat4 uSunShadowMatrix = mat4(uSunShadowMatrix0, uSunShadowMatrix1, uSunShadowMatrix2, uSunShadowMatrix3);
```

Fire shadow data goes in a **new std140 block at binding point 3** (2 is `BoundaryMistFrame`), declared in `fireShadows.glsl`:

```
FireShadowList : ivec4 uFireShadowCount;            // x = active slots 0..4
                 vec4  uFireShadowLight[4];         // xyz flickered world position, w = LightList index (as float)
                 vec4  uFireShadowRange[4];         // x near, y far, z tile half-scale (gutter-shrunk), w strength
                 vec4  uFireShadowTile[4*4];        // per (slot, face): xy tile origin in UV, zw tile size in UV
   // 16 + 64 + 64 + 256 = 400 B
```

### Samplers

`uSunShadowMap` → unit **5**, `uFireShadowAtlas` → unit **6**. Set **once at link time**, as `Render.LoadTerrainShader` already does for units 2 and 3 (`Render.Shaders.cs:67-69`) — because `AnimatedModel.SetTexture` re-sets material sampler uniforms per draw and would otherwise fight. **Set them on the MASK variants too** (they are separate programs). **Always bind a valid depth texture to units 5 and 6**, even when shadows are off (a 1×1 `DEPTH_COMPONENT16` dummy), so a driver that keeps an unused `sampler2DShadow` alive cannot sample unit 0's colour texture as depth. Restore `GL.ActiveTexture(TextureUnit.Texture0)` after binding, per house pattern.

---

## 4. The depth/caster pass

### ONE shared depth program, plus its MASK variant

```
Render.Shaders.cs:
    LoadWorldShader("shadowDepth", AssetPaths.Shader("cubeVertex.glsl"), AssetPaths.Shader("depthFragment.glsl"));
```

`cubeVertex.glsl` **verbatim** — the same file `cube`, the terrain variant and `enemy` already use. This is the structural answer to the wind requirement and it is not a coincidence:

- it already handles **skinning** (`useSkinning` + `jointMatrices[64]`, `:16-18,47-56`) → skinned actors cast correctly;
- it already handles **instancing** (`useInstancing` + `instanceModel`, `:8,58`) → chunks, props, scatter, cubes;
- **WS-C's wind displacement lives in this same file**, so the caster pass applies *the same code*, not a copy. Shadows cannot detach from a swaying canopy because there is only one displacement function in the repo.
- Static chunk VAOs bind locations 0/1/3 only (`StaticMeshBuffer.cs:42-47`); the unbound joint/instance attributes read 0 with the flags false, exactly as they already do when terrain draws with this shader today.

`depthFragment.glsl` is empty (`void main() {}`) plus:

```glsl
#ifdef WORLD_ALPHA_MASK
void main() { discard; }     // every MASK primitive is excluded from the depth map
#else
void main() {}
#endif
```

**Why a blanket discard is correct, not lazy:** verified in `groundkit.py:166-174`, the only MASK primitives in the world are (a) the flora/landmark **contact discs** — fake-AO discs that would otherwise cast a dark disc shadow, and (b) the **decal-layer** ground quads. Both must cast nothing. And `AnimatedModel.SelectProgram` already routes MASK materials to `shader.AlphaMaskVariant` with zero new code. If a genuine alpha-cutout caster is ever authored, this variant becomes a real alpha test — a one-file change.

**Caster-pass GL state** (set on entry, restored in a `finally`):

```
bind FBO; glDrawBuffer(None); glViewport(tile or full map); glClear(DepthBufferBit)
GL.Enable(DepthTest); GL.DepthMask(true); GL.DepthFunc(Less)
GL.Disable(Blend)
GL.Disable(CullFace)        // MANDATORY, see below
...
GL.BindFramebuffer(0); GL.Viewport(0, 0, ViewportSize.X, ViewportSize.Y)
```

**Culling: front-face vs back-face — and the consequence.** Neither. `GL.Disable(CullFace)`. Front-face culling (the usual acne cure) is **impossible here**: `StaticWorldChunkMeshBuilder` emits only *exposed* faces, so a chunk mesh is an **open shell** — culling front faces would delete the ground's top faces from the depth map and the terrain would shadow itself inside out. Flora is `double_sided: True` for the same reason. Consequence: acne must be beaten by bias alone (§1), which is why the normal-offset + slope terms carry real values rather than token ones. The visible pass already draws props and scatter with `enableCulling: false` (`PropRenderer.cs:105`, `GroundScatterRenderer.cs:141`), so this is the house default anyway.

**The viewport restore is the highest-risk line in the pass.** `PlayScene.Render` clears and sets the viewport long before (and `Resize` sets it at `PlayScene.Render.cs:256`); a shadow pass that leaks a 512×512 viewport renders the whole game into the corner of the window. Put it in a `finally` and pin it with a source regex test.

### Culling: reuse, do not replace

`ViewFrustum.FromViewAndProjection(lightView, lightProj)` works unchanged for an **orthographic** matrix — the Gribb-Hartmann column extraction at `ViewFrustum.cs:56-74` is projection-agnostic (an ortho gives `c3 = (0,0,0,1)` and the near/far planes still come from `±c2`). **Zero new culling code.** Same for each of the four 90° fire faces.

What must **not** be reused is the camera-direction half of the existing cull: `WorldVisibility.IsCubeVisible(cameraPosition, cameraFront, ...)` and `StaticWorldChunkRenderer.IsChunkVisible(...)` reject geometry *behind the camera* — but a caster behind the camera can still cast into view. So caster selection is:

```
ShadowCasterSelection.Accepts(bounds, focus, in lightFrustum)
  = (horizontal distance from focus to bounds <= ShadowCasterDistance)   // ShadowCasterDistance = WorldVisibility.MaxRenderDistance = 48
 && lightFrustum.IntersectsAabb(bounds.Min, bounds.Max);
```

The distance term keeps the two passes consistent: a caster the visible pass never draws must not cast either, or the shadow and its object disagree.

`WorldRenderBatchPlanner` is reused **unchanged** for dynamic cubes/blocks (it groups by material → model, which is also a valid depth grouping). The terrain caster path **bypasses** `DrawSortedByMaterial` entirely: depth needs no material, no texture and no material map, so it is the visible-mesh walk (`StaticWorldChunkRenderer.cs:227-262`) followed by one `DrawBatched()` per mesh with **zero material binds** — strictly cheaper than the visible pass.

### Per-face draw amortisation (the cost control for the fires)

Gather each light's caster set **once**, then for each of its 4 faces submit only the casters whose bounds hit that face's frustum. Most casters land in 1-2 faces, so the per-light draw count is `≈ casters × 1.3`, not `× 4`.

### Ordering in the frame

The flicker-driven design forces the shadow pass to run **after** `SelectPointLights` and **before** `UploadFrameLighting` (which must carry the sun matrix). `FrameLightingContractTests.cs:180-186` pins `UploadFrameLighting` to exactly one call site inside `WorldRenderer.DrawWorld(WorldSessionPresentationState)`, so do **not** move it. Instead:

```
PlayScene.Render, between CollectFrameLights(...) and _render.World.DrawWorld(presentation):
    gpuTimer.BeginSection(GpuTimerSection.Shadows);
    _render.PrepareShadows(presentation.Camera, _render.FogOrigin ?? presentation.Camera.Position, presentation.VisibleWorld, ...);
    gpuTimer.BeginSection(GpuTimerSection.World);
```

`Render.PrepareShadows` calls `SelectPointLights` (pure for a given camera/view, so calling it again inside `UploadFrameLighting` is idempotent and costs microseconds over a 16-slot insertion), builds the `ShadowFrame`, writes the sun matrix rows + params into `_frameLighting`, renders both maps, uploads `FireShadowList`, and binds units 5/6. `UploadFrameLighting` then uploads the already-populated struct. **Zero churn on the pinned regex.**

`GpuTimerSection.Shadows = 4` requires `GpuFrameTimer.SectionCount` 4 → 5 (`:61`), a `ShadowMilliseconds` field on `GpuFrameTimings`, a `GpuShadowMilliseconds` on `FramePerformanceSnapshot` and the HUD row 14 string. Sections cannot nest or reopen, and `BeginSection` ends the open one first, so placing Shadows before World is legal.

---

## 5. Performance budget

**Instrumentation that exists:** `GpuFrameTimer` (`GpuTimerSection`, read back ≥2 frames late, no stall), `RenderCounters` (`DrawCalls`, `ProgramBinds`, `UniformUploads`), `WorldRenderStats` (visible chunks, meshes, exposed faces, triangles, per-phase ms), the perf panel row 14, and `skills/profiling-dream-multiplayer/scripts/launch-and-summarize.ps1`.

**Measured baseline** (`docs/ledger/plans/2026-11-01-isometric-feasibility.md:136-143`, before props/scatter): iso **556 FPS = 1.80 ms/frame, 42 draws, 25.5k tris, GPU world 0.13 ms**. The frame is **CPU-bound on submission by ~14×**. Today, with props and ground scatter landed (an oak is 62k triangles), estimate 90-140 draws and 150-400k triangles — **the implementer must record the real number from the HUD before touching anything**, because it is the only honest denominator.

**Target:** ≤ **+1.5 ms CPU** and ≤ **+1.0 ms GPU** at 1440×1024, `DREAM_SHADOW_FIRES=2`, 2048² sun, 9 sun taps.

**What I actually expect, stated plainly:**

| | extra draw calls | notes |
|---|---|---|
| sun pass | +80 to +130 | ~1 extra full geometry submission; no material binds, MASK primitives skipped |
| 2 fires × 4 faces | +40 to +90 | per-face culling over a ≤10-unit radius; small triangle counts |
| **total** | **+120 to +220** | roughly **2-2.5× today's draw-call count** |

- **CPU: +1.5 to 3.0 ms**, because draw submission is the bottleneck and this roughly doubles it. The upper end exceeds the 1.5 ms target; I am stating that rather than hiding it.
- **GPU: +0.4 to 1.2 ms.** 6.3 M depth-only samples against a world pass that currently costs 0.13 ms.
- **Net:** the iso frame goes from ~1.8 ms to **~3.5-5.0 ms, i.e. 200-285 FPS** — still 3-4× the 60 FPS floor. This is affordable *because* the renderer was CPU-bound at 556 FPS with a near-idle GPU. It would not be affordable in a 60 FPS-limited engine, and the plan should not pretend otherwise.
- `RenderCounters.CountDraw()` fires in the caster pass too (it reuses the same draw helpers), so the HUD `draws` number will visibly jump. Expected, not a bug.

**Quality knobs** (all `MsaaSettings`-style: sanitise to the nearest allowed value, a `Describe`, and one `Console.WriteLine("[startup] shadows: ...")`):

| variable | values | default |
|---|---|---|
| `DREAM_SHADOWS` | 0 / 1 | **1** |
| `DREAM_SHADOW_RESOLUTION` | 512 / 1024 / 2048 / 4096 | 2048 |
| `DREAM_SHADOW_FIRES` | 0..4 | 2 |
| `DREAM_SHADOW_TAPS` | 1 / 4 / 9 | 9 |

`DREAM_SHADOWS=0` is the **kill switch**: `SunShadowParams.W = 0`, `uFireShadowCount.x = 0`, both GLSL visibilities return a literal 1.0 with no fetch, no FBO is created, no caster pass runs, and the FX blobs come back — so the frame is bit-identical to master. Follows `DREAM_GROUND_SCATTER` / `DREAM_WORLD_PROPS` / `DREAM_TIME_OF_DAY` / `DREAM_NIGHT_BRIGHTNESS` / `DREAM_FX_CLOCK`.

---

## 6. Testability

Designed to be testable in this repo's own idiom: CPU mirrors of shaders, GLSL-parsing contract tests, source-regex pass-order tests, allocation assertions, deterministic-clock captures, python art gates.

**New — `dream.Tests/Rendering/Shadows/`**

1. **`ShadowSettingsTests`** — env parsing/sanitising/`Describe` for all four variables; the off default reproduces the disabled `ShadowFrame`.
2. **`ShadowFrameContractTests`** — `FrameLighting.SizeInBytes == 256`; 16 members, all `Vector4`, at 16-byte strides; the GLSL block in `lighting.glsl` parses to the same 16 `vec4`s with matching names (reuse the `FrameLightingContractTests.cs:45-63` parser); `FireShadowList` is 400 B and mirrors `fireShadows.glsl` member for member (the `LightListContractSmokeTests` pattern); `FrameLightingBuffer.RoundUp(256, 256) == 256` and the buffer is 512 B; binding point 3 collides with nothing.
3. **`SunShadowProjectionTests`** — pure, no GL. The derived relations pinned, not the literals: `OrthoSide == sqrt(ReceiverSide² + ReceiverHeight²)`, `TexelWorld == OrthoSide / resolution`, `CasterExtension` clamp at `2 × MaxRenderDistance`, `normalOffsetWorld == NormalOffsetTexels × TexelWorld`, `biasDepth == biasWorld / depthRange`. Behaviour: every corner of the receiver box maps into `[0,1]³`; **texel snapping** — moving the focus by < 1 texel leaves the matrix bit-identical, moving by exactly 1 texel shifts it by exactly 1 texel; the matrix is a pure function of (focus, light direction) and is therefore deterministic from a pinned clock; the ortho side is invariant over all 24 hours of `SkyModel.Evaluate`.
4. **`SunShadowLookupMirrorTests`** — a CPU mirror of `dreamSunVisibility` (9-tap PCF, hardware-bilinear emulated) over a synthetic depth buffer, against hand-worked values; a surface with nothing above it returns exactly 1.0; outside the volume returns exactly 1.0 (the `CLAMP_TO_BORDER` 1.0 contract); `strength == 0` returns exactly 1.0.
5. **`FireShadowAtlasLayoutTests`** — face selection (a direction maps to exactly one of the 4 faces; `|y|` major returns 1.0); tile rects tile the atlas without overlap and never cross a gutter; the perspective depth encode/decode round-trips to float tolerance; `far == Radius × 1.15` and `FaceFovRadians` derived from `TileGutter`; `SelectSlots` is stable on ties and picks by contribution at the focus; **zero allocations** (`AllocationAssert.MeasureBytesPerIteration`, as `FrameLightingContractTests.cs:331-346` does).
6. **`FireShadowFlickerTests`** — **the goal-5 gate.** With `FireFlicker` sampled at two pinned clock times for the same brazier: the two `ShadowFrame`s differ in both the face origin and `far`; and for a fixed occluder/receiver pair the CPU-mirrored shadow boundary moves by a non-zero amount bounded by the flicker's offset amplitude × the parallax ratio. Also: with the flicker frozen, two consecutive frames produce bit-identical frames (no crawl).
7. **`ShadowCasterPassContractTests`** — source-regex, in the `FxPassContractTests` idiom. `Render.Shaders.cs` loads `shadowDepth` from **the same `cubeVertex.glsl` string** as `cube`/the terrain variant/`enemy` (assert the literal appears four times); `depthFragment.glsl` has a MASK variant containing `discard`; the pass disables `CullFace` and restores the framebuffer **and viewport** in a `finally`; `PlayScene.Render` calls `PrepareShadows` after `CollectFrameLights` and before `DrawWorld`, outside every branch (8-space indent, as `FxPassContractTests.cs:202` does); the shadow samplers are set to units 5 and 6 at link time on **both** the base and MASK programs; the dispose step is registered in `Render.Lifetime.cs` (`DisposeRobustnessStructuralTests` will demand it).
8. **`ShadowWindParityTests`** — the hard requirement. Assert `cubeVertex.glsl`'s wind block is reachable from the depth program (no `#ifdef` excluding it), and that the caster pass sets `uWind` from **the same `GroundWind` value** the visible scatter draw uses, and resets it to zero afterwards — a recorded-uniform test, matching WS-C's "assert the uniform sequence" discipline.
9. **`ShadowCasterSelectionTests`** — distance + light-frustum acceptance; a caster behind the camera is accepted (the direction cull is deliberately not applied); a caster beyond `ShadowCasterDistance` is rejected.
10. **`ShadowDisabledIsIdentityTests`** — with shadows off, the CPU mirror of the full lighting expression equals master's expression exactly over the `FrameLightingContractTests.BaseColors()` × `FaceNormals` grid; `SunShadowParams.W == 0`; `uFireShadowCount == 0`; `FxShadowList` emission is restored.

**Modified**

- `FrameLightingContractTests.cs` — 176/11 → **256/16**; new `RoundUp` rows; assert `cubeFragment`/`enemyFragment` include `fireShadows.glsl` exactly once, in the order lighting → fireShadows → pointLights → boundaryMist; the `:90` `enemyFragment` regex must still match **unchanged**.
- `GrimLookHistogramTests.cs` — four new shadowed rows (§3), existing rows unchanged at visibility 1.
- `GpuFrameTimerTests.cs`, `FramePerformanceSnapshotGpuTests.cs` — the new section and the row-14 string.
- `PresentationCameraScratchTests.cs:131`, `WorldSessionPresentationStateActiveEnemiesTests.cs:101` — these pin `FramePerformanceSnapshot`'s **allocated size**; `GpuShadowMilliseconds` adds 8 B. Update both.
- `FxPassContractTests.cs` — the new `GpuTimerSection.Shadows` line sits before the `World` section; check its ordering assertions still hold.
- `tools/ui-assets/enginelook.py` — the `visibility` term and the lower-bound docstring (§3).

**Capture-based A/B** (via `skills/capturing-dream-screenshots`, one `dream.exe` at a time, no colons in paths, shared identity file per bin):

```
capture.ps1 -Name shadow-off-night -TimeOfDay night -Env DREAM_SHADOWS=0,DREAM_FX_CLOCK=<pinned>
capture.ps1 -Name shadow-on-night  -TimeOfDay night -Env DREAM_SHADOWS=1,DREAM_FX_CLOCK=<pinned>
capture.ps1 -Name shadow-off-noon  -TimeOfDay noon  -Env DREAM_SHADOWS=0,DREAM_FX_CLOCK=<pinned>
capture.ps1 -Name shadow-on-noon   -TimeOfDay noon  -Env DREAM_SHADOWS=1,DREAM_FX_CLOCK=<pinned>
```

**Gate: `shadow-off-*` must be byte-identical (max channel delta 0) to the same capture taken from the integration branch before WS-D.** Then `python tools/ui-assets/tests/check_look.py shadow-on-night.png --actor-from-log out.log` must still pass A1 (black share, 0.020 bright share) and A3, and the on/off pair gets looked at by eye for the art judgement (dark, grim, gothic).

**Test discipline:** `dotnet test dream.Tests\dream.Tests.csproj` only, **never two suites at once** (~35 spurious persistence failures, testhost lock hangs), **at most two full-suite runs in a row** per verification step — isolate flakes with a filter instead. The two 0-byte HUD allocation tests fail on clean master.

---

## 7. Workstream split

All of WS-A, WS-B and WS-C are assumed **already landed** on the integration branch. Below, every file is listed by path and appears in exactly one workstream.

### Inherited files: what WS-D changes, and whether it risks undoing their work

| inherited file | owner | WS-D change | risk of undoing |
|---|---|---|---|
| `dream/Rendering/Lighting/FrameLighting.cs` | C | append 5 `Vector4`s @176-255; 176 → 256 | **None.** Purely additive; C's `CloudParams`/`FrameTime` offsets are untouched and `ViewModelOffset` stays 256. |
| `dream/Rendering/Lighting/FrameLightingBuffer.cs` | C | comment + `RoundUp` doc only; **no logic change** | None. |
| `dream/shaders/lighting.glsl` | C | 5 new `vec4` members; `uniform sampler2DShadow uSunShadowMap`; new `dreamSunVisibility`; **one new multiplicand inside `dreamDirectShaded`** | **Low but real.** The only semantic edit is one factor in `dreamDirectShaded`. `dreamDirect` and `dreamCloudSunMask` are untouched, and with `strength = 0` the function is bit-identical to C's. Guard: `ShadowDisabledIsIdentityTests` + the `DREAM_SHADOWS=0`/`CloudParams=0` capture must *still* match master, which is C's own acceptance criterion re-run. |
| `dream/shaders/cubeVertex.glsl` | C | **no edit at all** — reused verbatim by the depth program | None. This is why the wind cannot detach. |
| `dream/shaders/cubeFragment.glsl`, `enemyFragment.glsl` | C | **one `#include` line each** | **Very low.** The `enemyFragment` call site that `FrameLightingContractTests.cs:90` pins character for character is **not touched**; the test must still pass unmodified, which is the guard. |
| `dream/shaders/pointLights.glsl` | master | one `* dreamFireVisibility(i, fragPos, n)` factor; signature unchanged | None to C. Guard: the same pinned regex. |
| `dream/Rendering/Ground/GroundScatterRenderer.cs` | C | **add** a `DrawCasters(...)` overload (skips `GroundLayer.Decal`, applies the same `GroundWind`); `Draw` untouched | **Medium — the one to watch.** C's `uWind` reset-in-a-`finally` discipline must be replicated in the new method, and `ShadowWindParityTests` exists specifically to prove the caster uses C's value and resets it. |
| `dream/Rendering/Props/PropRenderer.cs` | C | **add** a `DrawCasters(...)` overload; `Draw` untouched | Low. |
| `dream/Rendering/Core/Render.Lighting.cs` | C | `PrepareShadows` writes the shadow members into `_frameLighting` before `UploadFrameLighting` uploads it; `UploadFrameLighting`'s body unchanged | **Low.** The pinned `DrawWorld` call site and the single-call-site assertion both still hold. |
| `dream/Rendering/Effects/FxShadowList.cs` etc. | A | **no edit** — only the *call site* in `PlayScene.Effects.cs` becomes conditional | None. A's files stay byte-identical to master, as A promised. |
| `dream/Rendering/Effects/FireFlicker.cs` | A | **read only** | None. |
| `dream/Rendering/Effects/FxFireShadow.cs` *(if A ships it)* | A | **ignore it.** It is a leftover of the withdrawn blob-elongation design (C6, struck out in the ledger). WS-D neither owns nor edits it; if it exists and is unused, flag it for deletion in a follow-up, not here. | None. |

### WS-D1 — Shadow core and the sun map  *(merge first)*

**Owns, exclusively:**
- `dream/Rendering/Shadows/ShadowSettings.cs` *(new)* — env parsing, resolutions, all derived constants
- `dream/Rendering/Shadows/SunShadowVolume.cs` *(new)* — pure, no GL: receiver box, derived ortho side, light-space matrix, texel snapping, bias derivation
- `dream/Rendering/Shadows/ShadowFrame.cs` *(new)* — the per-frame struct D2 fills and D3 reads
- `dream/Rendering/Shadows/ShadowMapTargets.cs` *(new)* — the two FBOs, textures, samplers, the 1×1 dummy, unit binding
- `dream/Rendering/Lighting/FrameLighting.cs` *(modified)*
- `dream/Rendering/Lighting/FrameLightingBuffer.cs` *(modified, comment only)*
- `dream/shaders/lighting.glsl` *(modified)*
- `dream/shaders/depthFragment.glsl` *(new)*
- `dream/Rendering/Core/Render.Shaders.cs` *(modified)* — load `shadowDepth`; set units 5/6 at link time on every world program and its MASK variant
- `dream.Tests/Rendering/Shadows/ShadowSettingsTests.cs`, `ShadowFrameContractTests.cs`, `SunShadowProjectionTests.cs`, `SunShadowLookupMirrorTests.cs` *(new)*
- `dream.Tests/Rendering/Lighting/FrameLightingContractTests.cs` *(modified)*

**Ordered steps:** (1) `ShadowSettings` + derived constants + tests. (2) `SunShadowVolume` pure math + tests (this is the mathematical heart; get it green before any GL). (3) `FrameLighting` 176 → 256 + `lighting.glsl` members; run the contract tests. (4) `dreamSunVisibility` + the one `dreamDirectShaded` factor, **with `strength = 0` hard-wired** so the branch is pixel-identical. (5) `ShadowMapTargets` + `depthFragment.glsl` + `shadowDepth` program load. (6) CPU mirror test of the lookup.

**D1 ships pixel-identical to its base** (`strength = 0`, nothing rendered into the map). That is what makes the merge order verifiable.

### WS-D2 — The caster pass  *(merge second)*

**Owns, exclusively:**
- `dream/Rendering/Shadows/ShadowCasterPass.cs` *(new)* — FBO bind, per-target viewport, the per-face loop, draw submission, `finally` restore
- `dream/Rendering/Shadows/ShadowCasterSelection.cs` *(new)* — pure acceptance predicate
- `dream/Rendering/Core/Render.Shadows.cs` *(new partial)* — `PrepareShadows`, `SetShadowFrame`, init/dispose, kill switch
- `dream/Rendering/Core/Render.Lifetime.cs` *(modified)* — `InitializeShadows` / `RunDisposeStep(DisposeShadows, ...)`
- `dream/Rendering/World/StaticWorldChunkRenderer.cs` *(modified)* — `DrawDepth(Render, in Matrix4 lightView, in Matrix4 lightProjection, in ViewFrustum, Vector3 focus)`: the visible-mesh walk with no material binds
- `dream/Rendering/World/WorldRenderer.cs` *(modified)* — a `DrawCasters` entry for dynamic cubes/blocks, reusing `WorldRenderBatchPlanner`
- `dream/Rendering/Props/PropRenderer.cs` *(modified)*
- `dream/Rendering/Ground/GroundScatterRenderer.cs` *(modified)*
- `dream/Rendering/Actors/EnemyRenderer.cs`, `LocalPlayerRenderer.cs`, `RemotePlayerRenderer.cs`, `NpcModelRenderer.cs` *(modified)* — skinned caster entries
- `dream/Rendering/Core/Render.GroundItems.cs` *(modified)*
- `dream/Client/Scenes/PlayScene.Effects.cs` *(modified)* — the blob fallback condition
- `dream.Tests/Rendering/Shadows/ShadowCasterPassContractTests.cs`, `ShadowCasterSelectionTests.cs`, `ShadowWindParityTests.cs` *(new)*

**Ordered steps:** (1) `ShadowCasterSelection` + tests. (2) `ShadowCasterPass` skeleton with GL state and the `finally` restore; prove the viewport/FBO restore with a capture that is unchanged. (3) Terrain depth path (biggest win, simplest: no materials). (4) Props and scatter caster overloads, **with the `GroundWind` parity test**. (5) Skinned actors. (6) Dynamic cubes/blocks and ground items. (7) Wire `PrepareShadows` to render the **sun** map only; the fire loop is parameterised by D3's atlas helpers and stays dormant.

**D2 also ships pixel-identical** (the maps are written but `strength` is still 0).

### WS-D3 — Fire shadows, composition, instrumentation  *(merge last)*

**Owns, exclusively:**
- `dream/Rendering/Shadows/FireShadowAtlas.cs` *(new)* — pure: tile layout, face selection, depth encode, `SelectSlots`, `FaceView`, `FaceProjection`
- `dream/Rendering/Shadows/FireShadowList.cs`, `FireShadowListBuffer.cs` *(new)* — the std140 block at binding 3
- `dream/shaders/fireShadows.glsl` *(new)*
- `dream/shaders/pointLights.glsl` *(modified)*
- `dream/shaders/cubeFragment.glsl`, `dream/shaders/enemyFragment.glsl` *(modified — include line only)*
- `dream/Rendering/Core/GpuFrameTimer.cs` *(modified)* — `SectionCount` 5, `Shadows` section, `ShadowMilliseconds`
- `dream/Rendering/Hud/FramePerformanceSnapshot.cs` *(modified)* — `GpuShadowMilliseconds`, row 14
- `dream/Client/Scenes/PlayScene.Render.cs` *(modified)* — the `PrepareShadows` call and the section
- `tools/ui-assets/enginelook.py` *(modified)*
- `dream.Tests/Rendering/Shadows/FireShadowAtlasLayoutTests.cs`, `FireShadowFlickerTests.cs`, `ShadowDisabledIsIdentityTests.cs` *(new)*
- `dream.Tests/Rendering/Lighting/GrimLookHistogramTests.cs`, `dream.Tests/Rendering/Core/GpuFrameTimerTests.cs`, `dream.Tests/Rendering/Hud/FramePerformanceSnapshotGpuTests.cs`, `dream.Tests/Client/Presentation/PresentationCameraScratchTests.cs`, `dream.Tests/Client/Presentation/WorldSessionPresentationStateActiveEnemiesTests.cs`, `dream.Tests/Rendering/Effects/FxPassContractTests.cs` *(modified)*

**Ordered steps:** (1) `FireShadowAtlas` pure math + layout tests. (2) `FireShadowList` block + `fireShadows.glsl` + the contract test. (3) The `pointLights.glsl` factor and the two include lines; confirm `FrameLightingContractTests.cs:90` still passes **unmodified**. (4) Instrumentation (timer section, HUD row, the two size-pinning tests). (5) `FireShadowFlickerTests` — the goal-5 gate. (6) **Turn `strength` on** and run `ShadowDisabledIsIdentityTests` + the four captures + `check_look.py`. (7) `enginelook.py` + its self-test. (8) `GrimLookHistogramTests` shadowed rows; report any band that moves.

### Shared contracts, agreed before any parallel work starts

| # | contract | owner |
|---|---|---|
| S1 | `FrameLighting` 176 → **256 B, 16 × vec4**; `SunShadowMatrix0..3` @176-239 (the four C# rows → the four GLSL columns), `SunShadowParams` @240 (x 1/res, y constant bias ÷ depthRange, z normal-offset world, w strength). `ViewModelOffset` stays 256; buffer stays 512 B. | D1 |
| S2 | `ShadowSettings`: `Enabled`, `SunResolution`, `AtlasResolution`, `TileSide`, `TileGutter`, `CastingFireCount`, `SunTaps`, `FireTaps`, `SunTextureUnit = 5`, `AtlasTextureUnit = 6`; derived `OrthoSide`, `TexelWorld`, `CasterExtension`, `FaceFovRadians`. | D1 |
| S3 | `SunShadowVolume.Build(Vector3 focus, Vector3 lightDirection, float elevationSin, int resolution) -> (Matrix4 LightViewProjection, float TexelWorld, float DepthRange)` — pure, no GL. | D1 |
| S4 | `ShadowMapTargets`: `BeginSun()`, `BeginFireTile(int slot, int face)`, `End()`, `BindForSampling()`, `SunHandle`, `AtlasHandle`. | D1 |
| S5 | `ShadowFrame` struct: the sun matrix + params + up to 4 fire slots (light index, flickered position, near, far, strength) — D1 defines, D2 fills, D3 uploads. | D1 |
| S6 | `ShadowCasterPass.Render(Render render, in ShadowFrame frame, in GeometryRenderExport export, Camera camera, Vector3 focus)` — the single entry point D3 calls from `PlayScene`. | D2 |
| S7 | `FireShadowAtlas.SelectSlots(in LightList, Vector3 focus, int maxSlots, Span<int> slots) -> int`; `FaceView(Vector3 lightPos, int face) -> Matrix4`; `FaceProjection(float near, float far) -> Matrix4`; `TileRect(int slot, int face) -> Vector4` (UV origin + size). D2 calls all four. | D3 |
| S8 | GLSL: `float dreamSunVisibility(vec3 fragPos, vec3 n)` in `lighting.glsl` (D1); `float dreamFireVisibility(int light, vec3 fragPos, vec3 n)` in `fireShadows.glsl` (D3). Both return a literal `1.0` when disabled, with no texture fetch. **`dreamDirect(vec3 n)` and the `enemyFragment` call site are not touched by anyone.** | D1/D3 |
| S9 | The caster pass sets `uWind` on the `shadowDepth` program from **the same `GroundWind` value** the visible scatter draw uses, and resets it to zero in a `finally`. | D2 (proved by D2's test) |

### Merge order: **D1 → D2 → D3**

- **D1 first** because it owns the UBO byte layout — the highest blast radius in the workstream, the same reason WS-C merged before WS-A — and because D2 and D3 compile against its types. It ships pixel-identical.
- **D2 second** because it can be verified alone: the maps are rendered and can be read back or dumped, while nothing samples them, so the frame is still pixel-identical and a D2 regression (a leaked viewport, a leaked FBO, a leaked `uWind`) has an **unambiguous cause**.
- **D3 last** because it is the only stream that changes a visible pixel. Every look shift, every art-gate movement and every histogram band drift after D3 has exactly one cause.

If a reviewer prefers fewer streams, D1+D2 can be merged into one; D3 must stay separate, because "the only stream that changes pixels is last" is the property that makes the gates diagnostic.

---

## 8. Honest limits

What this design **cannot** do, and what fixing it would cost.

1. **No ambient occlusion.** The ambient term is never shadowed, so a shadowed surface falls to pure ambient and there is no contact darkening in indirect light. The flora contact discs already fake it locally. *Fix later:* SSAO (a depth prepass + a separable blur + a new full-screen pass, ~+0.5-1.0 ms GPU and a new framebuffer), or baked AO in the atlases.
2. **Only 2 (max 4) fires cast.** With ~11-13 braziers within `CollectDistance`, the 3rd-16th fires light but do not shadow — a scene with several visible braziers shows inconsistent shadowing, and the selection switching as the player walks can pop. Mitigated by scoring on contribution at the focus (smooth and stable), not eliminated. *Fix later:* more atlas tiles and more passes — linear in cost, so 8 fires is roughly +80-180 draw calls.
3. **The ±Y cones of the fire ring are unshadowed.** No shadow on the ground within ~1 unit horizontally of a flame, and nothing directly overhead is shadowed. *Fix later:* +2 faces per light = +50% fire cost for a near-invisible gain. Not worth it.
4. **The sun map covers 40 units around the focus; beyond that, shadows fade out.** The fade boundary is faintly visible on a long open vista at noon. *Fix later:* one more cascade = one more full geometry submission (+80-130 draws).
5. **Long dawn/dusk shadows are clipped.** `CasterExtension` saturates below ~15.7° sun elevation, so a tall object far from the receiver stops casting right when the sun is lowest. Visible only while the sun intensity is already fading through the `LightFade` band. *Fix later:* a shadow-only extended draw distance, which means drawing geometry the visible pass culled.
6. **Foliage casts fully opaque shadows.** Flora bodies are opaque meshes with no leaf masks, so a canopy reads as a solid black mass rather than dappled light — arguably on-register for "grim", but it is a limitation, not a choice. *Fix later:* authored leaf alpha masks (WS-B territory) plus a real alpha test in the depth program's MASK variant, or a two-layer transmittance shadow.
7. **The FX are outside the system entirely.** Flames and particles neither cast nor receive; smoke casts nothing. Deliberate — shadowing an additive flame is wrong — but it does mean a torch's own flame does not light-shaft through anything.
8. **No temporal filtering.** At high flicker amplitude the 9-tap PCF is the only smoother, so a fast flicker can read as jitter rather than flutter. If the capture shows crawl, **lower `FireFlicker`'s position-offset amplitude** rather than adding TAA — this engine has no history buffer and adding one is a wave of its own.
9. **MSAA does not help shadow edges.** 4× MSAA antialiases geometry silhouettes, not a shadow boundary inside a triangle. At 1440×1024 with a 2.33 cm texel ≈ 1.2 px this is fine; at 4K the sun map would want 4096² or more taps. The knobs exist for exactly that.
10. **MASK primitives neither cast nor receive correctly.** They are excluded from the depth map by design (contact discs, decals) and they still *receive* shadows, which is right — but a future genuine cutout caster needs step 6's fix first.
11. **CPU cost is real and the upper estimate exceeds the stated target.** +120 to +220 draw calls roughly doubles submission on a frame that is CPU-bound by ~14×. Expected +1.5-3.0 ms CPU against a +1.5 ms target. It is affordable here only because the GPU is nearly idle (0.13 ms world); on a 60 FPS-capped or GPU-bound configuration it would not be, and `DREAM_SHADOW_FIRES=0` / `DREAM_SHADOWS=0` exist for that.

---

## 9. Open questions for the user

1. **Default on or off for the first merge?** The plan assumes `DREAM_SHADOWS=1` by default (it is the wave's goal). Say so if you want it opt-in for one wave.
2. **`CastingFireCount` default 2 or 4?** 4 roughly doubles the fire-shadow cost (+40-90 more draws) and makes a crowded brazier field consistent. 2 is the safe default.
3. **Is the shadow fade-out at the 40-unit boundary acceptable**, or do you want the second cascade now (limit 4, ~+80-130 draws)?
4. **"Shadowed → pure ambient" with no AO** — is that the grim look you want, or should SSAO be in scope?
5. **If the measured shadowed night saturation exceeds `GrimLookHistogramTests.MeanSatMax = 0.38`**, may the band move, or should the night ambient be desaturated to compensate? I would not widen an art gate without your word.
