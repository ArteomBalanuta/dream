# Wave: living world — plan

Five goals, three workstreams, one integration branch. Planned against master @ `ed0005f3`
(clean, green). Every number here was measured, not guessed; the measurement is named beside it.

## The five goals

1. **Item/weapon proportions** — nothing tiny, nothing giant; sizes that make sense against the figure.
2. **Textures** — flora and props are near-flat colour; they need patterns that mean something.
3. **Ambient animation** — trees and plants move; the world stops being a still life.
4. **Cloud shadows** — real shadows moving across the ground.
5. **Fire is not a bulb** — fire varies moment to moment, and the shadows it casts vary with it.

## What the ground truth turned out to be

### The metre anchor (goal 1)

The repo *does* have an authored metre, in the art pipeline, not the engine:
`tools/ui-assets/gen_char_player.py:47` `U_PER_M = 16.0` and `:56` `UNITS_PER_U = 0.088`, so

> **1 metre = 1.408 model units.**

`dream/animations/players/player-grim.glb` measures **2.6805** model units tall (node transforms
applied, same walk as `dream/Rendering/Models/WorldAssetBounds.cs:15-32`) = **1.904 m** authored.
The comment in `WeaponMeshCatalog.cs:27-28` saying the figure is "about 2 units tall" is wrong by
34 % and gets corrected.

`dream/Rendering/Actors/HeldWeaponRenderer.cs:27-28` then inflates **everything** held by a blanket
`HandScale = 1.7` / `ShieldScale = 1.5`, because at isometric zoom a blade was a few pixels wide.
That blanket is the defect. Measured authored lengths against real-world references:

| visual id | authored | = m | today (×1.7/1.5) | target |
|---|---|---|---|---|
| dagger | 0.915 u | 0.65 | **1.11 m** | 0.35 |
| short-sword | 1.090 | 0.77 | **1.32** | 0.75 |
| sword | 1.480 | 1.05 | **1.79** | 0.95 |
| greatsword | 2.230 | 1.58 | **2.69** | 1.55 |
| axe | 1.280 | 0.91 | **1.55** | 0.90 |
| hand-axe | 0.935 | 0.66 | **1.13** | 0.55 |
| mace | 1.170 | 0.83 | **1.41** | 0.75 |
| hammer | 1.380 | 0.98 | **1.67** | 1.00 |
| spear | 2.660 | 1.89 | **3.21** | 2.10 |
| bow | 1.670 | 1.19 | **2.02** | 1.70 |
| short-bow | 1.260 | 0.90 | **1.52** | 1.10 |
| staff | 2.370 | 1.68 | **2.86** | 1.80 |
| bone-staff | 2.080 | 1.48 | **2.51** | 1.65 |
| iron-blade | 1.260 | 0.90 | **1.52** | 0.90 |
| ward-lantern | 0.500 | 0.36 | **0.60** | 0.35 |
| buckler | 0.400 | 0.28 | 0.43 | 0.40 |
| kite shield | 0.990 | 0.70 | 1.06 | 0.95 |
| tower shield | 1.120 | 0.80 | 1.19 | 1.15 |
| tool.blade | 1.130 | 0.80 | **1.36** | 0.80 |
| tool.maul | 1.420 | 1.01 | **1.71** | 1.00 |
| tool.builder | 0.735 | 0.52 | 0.89 | 0.55 |
| tool.torch | 1.365 | 0.97 | **1.65** | 0.55 |
| tool.lantern | 0.470 | 0.33 | 0.57 | 0.32 |

Reading: **hand items are nearly right *before* the 1.7; shields are nearly right *after* the 1.5.**
So the fix is to drop the blanket for hand items, keep ~1.4-1.5 for shields, and correct four real
outliers in the mesh table itself (dagger, torch, hand-axe, sword).

A trap worth recording: the repo carries **two metre conventions**. `tools/ui-assets/enginelook.py:38-40`
states outright that the lighting side reads one world unit as one metre; the geometry side makes one
world unit 1.587 art-metres. Goal 1 works only in the geometry anchor. Reconciling them is not this wave.

Already anchored, no defect, deliberately left alone: `ArmourMeshCatalog` (gated against the measured
figure by its own test), the dropped-item GLBs and `PropCatalog` (gated by `check_props.py`), and the
first-person view models (an unreachable path since the isometric decision).

### The textures are flat, and nothing measures it (goal 2)

Mean absolute neighbouring-pixel gradient inside used UV area: `tree_gallows_oak` **3.40/255**,
`bush_nettle` **3.30**, `grass_tuft_tall_01` **4.14** — about 1.3 %. `props/cottage.glb` reaches
**7.02** because it has plank detail. So flora atlases are smooth gradients with no bark striation,
no leaf-cluster breakup, no lichen, no grain.

`tools/ui-assets/tests/check_props.py:52-78` already gates a low-frequency *ceiling*
(`rect_lowfreq_max 0.80`) and `check_art.py:113-119` already computes a high-frequency measure.
**There is no gradient floor anywhere** — that is the missing gate, and flora is what slipped through it.

Two facts constrain how detail gets added. These atlases are **albedo**: final pixel is
`(ambient + lights) * albedo`, no tonemap, with a clamp, and the gain reaches ~2.17 beside a brazier,
so a channel over ~0.40 clips to a flat patch. And `groundkit.py:113-129 encode_ground_atlas` folds a
`scale` into `baseColorFactor`, so a texel's effective albedo is `byte/255 * scale` — any clipping gate
that reads the byte alone measures the wrong number. Detail therefore gets added **multiplicatively
downward only**, treating today's flat value as the per-class ceiling.

### Nothing moves, and there are no clouds (goals 3, 4)

No wind or sway exists anywhere. Flora and props draw as instanced static meshes
(`GroundScatterRenderer.cs`, `PropRenderer.cs`). The sky is a gradient plus stars plus two discs
(`skyFragment.glsl`) — no clouds at all. There is no shadow map in the project.

Three findings shaped the design:

- **All three world programs share one vertex shader.** `Render.Shaders.cs:13-23`: `cube`, the terrain
  variant and `enemy` are all `cubeVertex.glsl`. A sway added there also compiles into the terrain and
  into every actor, so it must be gated by a uniform defaulting to zero and **reset after the scatter
  draws**. A leaked uniform means rippling terrain.
- **Per-instance phase needs no new attribute**: `cubeVertex.glsl:11` already has `instanceModel`, and
  `instanceModel[3].xyz` is the instance's world position. Hash it in the shader. No buffer change.
- **`dreamDirect(vec3 n)` must not change signature.** Four shaders call it,
  `FrameLightingContractTests.cs:90` pins the `enemyFragment` call site with a regex character for
  character, and `enginelook.py:22-27` restates it as the engine contract. Clouds get a *new*
  `dreamDirectShaded(n, fragPos)` beside it, and with coverage 0 the two are bit-identical — which is
  what keeps every existing equivalence test and the art instrument honest.

### The fire is a bulb on purpose, and the reason is a byte (goal 5)

`HeldLightCatalog.cs:28-34`: `TorchFlickerAmplitude` is **0.08**, two sines, and the radius is fixed.
It is small because the fire halo packs `light.Color * HeldHaloTint * gain` into **one byte**, and
**two independent flickers** are already inside that product — the light's (peak 1.08) and the halo's
(peak 1.15, `FxSimulation.cs:40-44`). Hence the derived ceiling
`MaxUnclippedTorchIntensity = 1/(0.375 × 1.08 × 1.15) = 2.147`, against which `TorchIntensity 2.1`
sits 2.2 % under. Raising the amplitude lowers the ceiling: the two are coupled.

**Merging the two flickers into one shared value removes one `(1 + A)` from that product and buys the
amplitude back:** at `TorchIntensity 2.1` the merged ceiling allows `A ≤ 0.269`. We ship **A = 0.25**,
a **3.1× increase**, with no intensity change and no new clipping.

Placed fires do not flicker at all today (`PlacedLightIndex.cs:112`, `LampLightIndex.cs:87` are
constant); only their halo wobbles. Braziers are the tight case: `3.0 × 0.30 = 0.900` already reaches
0.959 of the byte at today's halo peak. Cutting `LampHaloTint.X` **0.30 → 0.26** (a 13 % dimmer brazier
halo) buys `A_brazier = 0.25` at peak 0.975. That trade is accepted: goal 5 is the point of the wave.

Shadows are **constant black ground blobs** (`FxShadowList.cs:67-98`), lit by nothing, with no access
to the light list. Props, flora and terrain cast nothing at all.

**REVISED after review (user decision): the blob approach is rejected and real shadow mapping is being
designed instead.** The first draft of this plan proposed rotating and elongating the existing ground
blob away from the dominant fire — cheap, one shader line, and driven by the same live flicker. It was
dropped because it is an ellipse, not a silhouette: no limbs, no held weapon, one light only, nothing
cast onto walls or onto other actors, no flora or prop shadows, capped at 32. The user's instruction is
that quality is the goal and that this must not be compromised, so goal 5's *"consequently casts
different shadows"* is now delivered by a real shadow-map workstream (WS-D, designed separately), in
which a flickering light genuinely moves the shadows it casts. `FxShadowList`, `FxShadowRules`,
`FxInstance` and `fxVertex.glsl` are therefore **left exactly as master** by WS-A.

What WS-A still delivers for goal 5 is the whole light side: the merged single flicker, the 3.1×
amplitude, flickering braziers and lamps, and a `FireFlicker` public surface that the shadow pass can
consume from a `PointLight` and the clock alone.

## Shared contracts, agreed before any parallel work

| # | Contract | Owner file | Workstream |
|---|---|---|---|
| C1 | FrameLighting UBO 144 → **176 B**, 11 × vec4: `CloudParams` @144 (coverage, softness, strength, scroll), `FrameTime` @160 (wrapped clock, gust). Default zero reproduces master exactly. | `Rendering/Lighting/FrameLighting.cs` | C |
| C2 | One cloud function, two consumers: `float dreamCloudCover(vec2)` in a new `shaders/clouds.glsl` (no includes — includes are one level only), used by `skyFragment` for the visible sheet and by `dreamDirectShaded` for the ground mask, so sky and ground always agree. | `dream/shaders/clouds.glsl` | C |
| C3 | `ModelUnitsPerMetre = 1.408f`, `AuthoredFigureHeightMetres = 1.904f`, `Resolve(visualId)`, `TargetLengthMetres(visualId)`. Replaces `HandScale`/`ShieldScale`. | `Rendering/Actors/HeldItemScale.cs` | A |
| C4 | **One** fire flicker value for light, halo, flame and shadow: four octaves summing to exactly 1 plus gusts that may only *reduce*, so the byte ceiling stays exact. Intensity, radius scale and a small position offset. | `Rendering/Effects/FireFlicker.cs` | A |
| C5 | `FxSimulation.TryClassify` must stop matching radius to ±0.01 — a flickering radius would silently delete every halo, flame and ember. Nearest class within ±0.75. | `Rendering/Effects/FxSimulation.cs` | A |
| ~~C6~~ | ~~Ground-aligned blob elongation.~~ **Withdrawn** — blob shadows rejected in favour of real shadow maps (WS-D). `fxVertex.glsl` and the FX shadow files stay as master. | — | — |
| C7 | `uniform vec2 uWind` (amplitude at tip, stiffness exponent), phase from `instanceModel[3].xz`, clock from `uFrameTime.x`, **reset to zero after the scatter and prop draws**. | `Rendering/Ground/GroundWind.cs` | C |

Decisions taken on the architect's open questions, so nothing waits on an answer:

- **Spear stays 2.10 m** — 110 % of the figure, the only held item above its height, which is what a
  real spear does. Everything else caps at 100 %.
- **Night cloud coverage 0.30.** Clouds crossing the stars is the grim look, not a loss of it.
- **Flicker seeded from the position hash, quantised to a 0.5-unit grid** — no change to `PointLight`
  or the pinned 528-byte `LightList` block, and quantising stops a walking torch's phase from drifting.
- **`LampHaloTint.X` 0.30 → 0.26 accepted**: a 13 % dimmer brazier halo for 2.8× the flicker range.

## Workstreams

**WS-A — proportions and fire** (goals 1, 5). Owns `HeldWeaponRenderer`, `WeaponMeshCatalog`,
`HeldLightCatalog`, `PlacedLight(+Index)`, `LampLightIndex`, `FxSimulation`, `FxShadowList/Rules/Instance`,
`fxVertex.glsl`, `PlayScene.Effects/HeldLights`, plus new `HeldItemScale`, `FireFlicker`, `FxFireShadow`.
Touches no lighting UBO and no world shader.

**WS-B — atlas detail** (goal 2). Owns `groundkit.py`, `gen_ground_flora/stone/props.py`, the three
`check_*` gates, and the regenerated flora/stone/prop GLBs. No C#, no GLSL. New helpers go in
`groundkit.py` only — `forge.py`/`isoforge.py`/`charforge.py` are shared with the character and HUD
pipelines and stay untouched.

**WS-C — clouds and wind** (goals 3, 4). Owns `FrameLighting(+Buffer)`, all of `Rendering/Sky`,
`Render.Lighting`, `GroundScatterRenderer`, `PropRenderer`, and `lighting/cubeVertex/cubeFragment/enemyFragment/skyFragment`
shaders, plus new `clouds.glsl`, `GroundWind`, `CloudLayer`.

No file appears in two lists.

**Merge order: WS-B → WS-C → WS-A.** B first because it is binaries plus python and shares nothing, so
a later test shift has an unambiguous cause. C second because it owns the UBO byte layout, the highest
blast radius in the wave. A last because it is the largest C# surface but the most self-contained, and
its shadow fallback is byte-identical to master, which makes it the easiest to verify after a merge.

## Acceptance criteria

| Goal | Measurable |
|---|---|
| 1 | No held item's final world extent exceeds 1.15 × the figure; greatsword 1.55 m ±0.10 (was 2.69), torch 0.55 ±0.06 (was 1.65), tower shield 1.15 ±0.10; every id within ±12 % of its target metre; anchor test ties 1.408 × 1.904 to the measured GLB. |
| 2 | Every flora, stone and prop atlas reaches mean 4-neighbour laplacian **≥ 7.0/255** over used UV (from 3.3-4.1); **zero** texels with effective albedo `byte/255 × baseColorFactor` above 0.42; mean v ≤ 0.28; no new gate overrides; two builds byte-identical. |
| 3 | A tree's canopy displaces 0.06-0.10 world units peak, trunk below 20 % height under 0.005, terrain and actor vertices **exactly** 0; `uWind` proven reset. |
| 4 | UBO 176 B / 11 members / `ViewModelOffset` 256; with `CloudParams = 0` the captured frame is identical to master's at a pinned clock (max channel delta 0); at coverage 0.35 the ground's mean value drops 8-18 % at noon while the night and dusk grim bands do not move. |
| 5 | `TorchFlickerAmplitude == 0.25` at `TorchIntensity == 2.1`, both derived ceilings satisfied, peak halo red ≤ 0.99 for torch **and** brazier; torch radius varies over 7.30-8.70 with `TryClassify` still returning Torch; flicker range > 0.30 (today's test asks > 0.05) and autocorrelation at 0.5 s below 0.5, which a two-sine signal fails. The shadow half of goal 5 moves to WS-D. |

## Risks

1. `cubeVertex.glsl` is shared by terrain, cubes and actors — a leaked `uWind` ripples the world.
   Reset in a `finally` and assert the uniform sequence.
2. `TryClassify` is radius-exact: jittering the radius without C5 silently deletes every fire effect,
   and no existing test catches it. The likeliest silent break in the wave.
3. The brazier halo has 4.3 % byte headroom. The brazier product must be pinned by a test, not just
   the torch's.
4. A position-hashed flicker means a *moving* torch's phase drifts with position — watch it in the
   capture; the 0.5-unit quantisation is the mitigation.
5. 40-plus regenerated GLBs is a large binary diff, and the determinism gate fails on the *second*
   build, so every detail function seeds explicitly from the asset id.
6. `enginelook.py` restates the shader contract character for character. Leaving `dreamDirect` alone is
   what keeps the art instrument honest.
7. Test discipline: never two suites at once (≈35 spurious persistence failures, testhost lock hangs);
   at most two full-suite runs in a row; the two 0-byte HUD allocation tests fail on clean master too.

## Deferred

`ArmourMeshCatalog` and dropped-item/prop scale (measured, gated, no defect) · first-person view models
(unreachable) · ground decal atlases (flat by nature; add once the floor is proven on flora) · prop sway
(a swaying cottage is wrong; gates and shutters want authoring) · cloud effect on the *ambient* term and
overcast sky colour (its own keyframe pass) · reconciling the two metre conventions.

**Not deferred: real shadow maps.** WS-D is being designed now, to land on top of WS-A/B/C — a sun
cascade plus omnidirectional shadows for the fires, with the caster pass applying the same wind
displacement as the visible pass so shadows do not detach from swaying canopies, and the flicker
driving the shadows it casts. It carries the whole shadow half of goal 5.

---

# Outcome

Integrated on `integrate/living-world`, two review rounds, no Must-fix outstanding.
Suite: 8331 passed / 1 failed / 47 skipped, the failure being a known exact-zero-allocation
intermittent (`CubeRestFreezerTests...AllocatesNothing`) that passes alone.

## What the goals actually got

| Goal | Result |
|---|---|
| 1 proportions | Blanket x1.7/x1.5 gone, replaced by a per-item scale off the 1.408 u/m anchor. Greatsword 2.69 m -> 1.55, torch 1.65 -> 0.55, spear 3.21 -> 1.32 world units (under the 1.38 ceiling). Extents now measured from the **rotated** corners, which caught the buckler drawing 0.453 m against its documented 0.400. |
| 2 textures | Flora atlas gradient 3.3-4.9 -> min 7.22/255 across all 98 assets, with bark, leaf-mass, blade and stone vocabularies. Detail is **mean-preserving**; the cap is a per-texel soft knee that scales all three channels equally, so hue and saturation survive. |
| 3 sway | Vertex wind on instanced scatter, phase from the instance matrix (no new attribute), amplitude per asset class, scaled by instance scale, uniform reset in a `finally`. |
| 4 clouds | 4-octave fBm shared by the sky sheet and the ground mask, so clouds sit over the shadows they cast. Lattice wraps at 64 cells, periodic at every octave. |
| 5 fire | One shared flicker for light, halo and flame; amplitude 0.08 -> **0.25** at unchanged intensity. Flame brightness now strictly monotonic (bytes 191/219/247) with area climbing alongside. Braziers and lamps flicker for the first time. |

## What the wave found that it was not looking for

- **~50 of 98 ground and prop assets were already breaking the albedo ceiling on master** (birch 0.717, bramble 0.714 against ~0.41), so they clipped to flat patches near firelight — the very defect goal 2 set out to remove. Bringing them into gamut costs them 14-20 % of their mean value. That darkening is correct, not a regression, and it is why `mean_v_drop_capped_max` is a separate, looser gate derived from `precap_albedo_max > albedo_clip_max` rather than a hand-kept list.
- **The gate set had ceilings but no floors.** Nothing measured a gradient floor (so flat art passed), and nothing measured a value floor (so "darken everything" passed). Both now exist, each with a negative that runs through the real pipeline.
- **The flame sprite had no clipping ceiling at all.** The halo's byte was carefully derived; the flame's second packed byte was not, and saturated for half the cycle. Goal 5 was not achieved until that was fixed.
- **Captures are not byte-deterministic**, even pinned: three independent clocks, of which `RenderFrameClock` is still unpinned. The byte-for-byte A/B gate in the plan above was never achievable as written.
- **Four tests could not fail** and were caught only by mutation or by algebra: the gust bound, the "not a bulb" autocorrelation, the held-item 12 % band (twice), and a sway-continuity check that wrapped both sides through the same function.

## Known limits, deliberately accepted

The ~50 capped assets stay 14-20 % darker until their bake materials are re-authored or the engine
gains a tonemap. `DREAM_FX_CLOCK` does not pin fire. The visible cloud sheet may rarely be in frame,
since the isometric view is enclosed by boundary mist. Shadows are WS-D, not this wave.
