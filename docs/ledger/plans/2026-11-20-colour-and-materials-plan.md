# Plan: the neutral shadows and world material wave

- Branch: `feat/colour-and-materials`, from master `4a43fb12`.
- Method: the swarm workflow in `CLAUDE.md` with `parallel-agent-delivery` — architect, three
  implementers on disjoint files, then integration and measurement.
- Follows `docs/ledger/plans/2026-11-20-render-pipeline-plan.md`, whose work log left these three
  items open and unauthorised.

## What was measured first, before anything was planned

The look gate `tools/ui-assets/tests/check_look.py` has failed one criterion for a long time: mean
saturation 0.544 against a <= 0.38 target on night frames. Previous waves recorded it as "our blacks
are tinted" and moved on. Breaking a fresh night capture down by value band says something sharper:

| band (value = max channel) | share of frame | mean saturation | mean colour, normalised |
| --- | --- | --- | --- |
| 0.00-0.02 | 0.344 | 0.51 | 0.73 / 0.52 / 1.00 |
| 0.02-0.05 | 0.285 | 0.55 | 0.98 / 0.63 / 1.00 |
| 0.05-0.20 | 0.215 | 0.56 | 1.00 / 0.65 / 0.47 |
| 0.20-1.00 | 0.156 | 0.58 | 1.00 / 0.70 / 0.47 |

Saturation is ~0.54 in **every** band. The darks dominate the mean only because 63% of the frame
sits under value 0.05. Below 0.05 the tint is blue (cold ambient and the violet boundary mist);
above it the tint is warm (torchlight). So this was never a "dark pixels" problem: it is a
whole-frame chroma problem whose mean is set by where the pixels are.

That rules the source fix out on arithmetic rather than on taste. Reaching 0.33 by desaturating the
ambient and the mist would mean desaturating torchlight too, and `postTone.glsl` already records why
coloured highlights must not slide toward white.

## The transform, and the property that makes it safe

```
w = (1 - smoothstep(0, 0.08, maxChannel)) * strength
c = c + (vec3(maxChannel) - c) * w
```

Mixing toward the **max channel** — not toward luma, not toward the mean — leaves the max channel
exactly where it was. Written subtractively, the max channel's own term is `(m - m) * w = 0`, so it
survives bit-for-bit rather than approximately.

That matters because `check_look.py` derives near-black share, bright share, mean value and the A3
actor spread from the max channel. Simulated on captured frames across twelve settings, all four
came out identical to four decimal places every time and only saturation moved. The gate's other
criteria are protected by the arithmetic, not by luck: if one of them moves, the implementation is
wrong.

At edge 0.08, strength 0.80: night 0.544 -> 0.327, noon 0.378 -> 0.327. Both inside the 0.311-0.371
that `check_look.py`'s own header records as measured off the target art. One constant, both times
of day, inside the reference band rather than scraping under the ceiling.

Looked at rather than trusted: the boundary mist goes from violet to charcoal with its red ember
veins intact, the dark side of the frame becomes neutral charcoal, the torch pool is untouched and
reads stronger by contrast.

## Tracks

| Branch | Owns | Job |
| --- | --- | --- |
| `ws/world-material` | `cubeFragment.glsl`, `Shader.cs`, `AnimatedModel.cs`, `Render.Terrain.cs`, `StaticTerrainMaterial*.cs` | a material response for the world program through `surface.glsl`, off by default, `Stone` and `Rock` opting in |
| `ws/arcane-bloom` | `WeaponMeshCatalog.cs` and its tests | raise the arcane gem's emissive over the tone knee so it reaches the bloom |
| `ws/neutral-shadows` | `postTone.glsl`, `postResolveFragment.glsl`, `Rendering/Post`, the post tests, `enginelook.py`, `docs/rendering-and-assets.md` | the grade above |

Disjoint by file. Merge order: world material, arcane, grade last — the grade's deliverable is a
measured number on the final frame, so it is measured once the other two are in.

## Frozen contracts

- `surface.glsl` and `enemyFragment.glsl` are frozen. No workstream edits either.
- Every existing `dream*` GLSL function keeps its name, signature and body; `ws/neutral-shadows` may
  add one to `postTone.glsl`.
- `uEquipSurface` (x metallic, y roughness, z grain, w accentMix) and `uEquipEye` are the shared
  material-response ABI, with `uEquipSurface.y > 0` as the gate and zero meaning off. The world
  program reuses them rather than growing a parallel uniform.
- The grade constants have one C# home in `PostSettings`, mirrored into the GLSL and pinned by a
  test that reads the shader. No Python copy.
- Golden baselines are re-recorded once, after integration, never inside a workstream.

## Deliberately not done

- **No source-side desaturation** of the ambient term, the moon tint, the boundary mist palette or
  the war mist albedo. It cannot reach the number without desaturating torchlight, it moves value
  and so reopens A1 and A3, `ComputeNightMoonIntensity` divides by the night ambient's luminance so
  a chroma edit there moves the moon, and it would cost the mist its ember identity.
- **No global material response.** One terrain family opts in, measured. Driving it from the glTF
  `metallicFactor` / `roughnessFactor` already being uploaded would switch the whole world on
  silently, since the terrain pass uploads roughness 1.0 and props carry 0.5.
- **No change to the armour's arcane emissive.** It was lowered to a third for a reason that still
  holds: the Membrane wings are a large area and emissive is added after the gain, so nothing shades
  it. Only the weapon Gem's size makes the larger number acceptable.
- **No gate thresholds touched.** The frame moves, not `check_look.py`.
- Noon's A1 bright share (0.011 against a 0.020 floor) is **not** addressed here. Daylight has no
  local light pools, so moving it is a lighting change rather than a grade, and it belongs to its
  own wave.
