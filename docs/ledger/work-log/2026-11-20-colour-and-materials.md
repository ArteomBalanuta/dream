# Work log: the neutral shadows and world material wave

- Purpose: a neutral dark end, a material response for the world programs, and an arcane gem that
  reaches the bloom — each with gate evidence and a capture.
- Status: on `integrate/colour-and-materials`. Not pushed, not merged.
- Plan: `docs/ledger/plans/2026-11-20-colour-and-materials-plan.md`.
- Builds on the post chain merged into master at `ac91799d`
  (`docs/ledger/work-log/2026-11-20-render-pipeline.md`).

## What landed

**The grade (`ws/neutral-shadows`).** `dreamToneNeutraliseShadows` in `postTone.glsl`, applied as
the last step of the resolve, shipping on at strength 0.80 with `DREAM_SHADOW_NEUTRALISE` as the
A/B. Written subtractively — `c + (vec3(m) - c) * w` rather than `mix(c, vec3(m), w)` — so the max
channel's own term is exactly `(m - m) * w = 0` and survives bit-for-bit instead of approximately.
Every pixel it touches is below 0.08, far under the 0.80 knee where the shoulder is the identity, so
the two transforms commute on every pixel either one changes.

**A material response for the world program (`ws/world-material`).** `cubeFragment.glsl` now calls
the shared `surface.glsl` behind a `uEquipSurface.y > 0` gate, with `StaticTerrainMaterial.Surface`
plumbed through the catalog and `Render.Terrain.cs`. `Stone` and `Rock` opt in at
`(0.05, 0.50, 0, 0)`; every other material is zero. Props needed nothing — `PropRenderer` already
draws with the `enemy` program, which has the response path.

**The arcane gem (`ws/arcane-bloom`).** `WeaponMeshCatalog.Emissive(Arcane)` from
`(0.30, 0.12, 0.55)` to `(0.55, 0.22, 1.00)`: every channel x1.818, so the hue ratio is exact and
the peak clears the 0.80 knee by 0.20 and reaches the bloom. The armour's Arcane entry stays a third
of it, deliberately, because the Membrane wings are a large area and emissive is added after the
gain where nothing shades it.

## Measured

| | before | after |
| --- | --- | --- |
| look gate A1 mean saturation, night (target <= 0.38, art 0.311-0.371) | 0.544 **FAIL** | 0.334 **PASS** |
| look gate A1 mean saturation, noon | 0.378 | 0.327 |
| look gate problems, night | 1 | 0 |

One constant lands both times of day inside the band measured off the reference art, rather than
scraping under the ceiling.

**How the saturation failure was actually diagnosed.** Three previous waves recorded it as "our
blacks are tinted". Breaking a night frame down by value band says something sharper: saturation is
~0.54 in **every** band, and the darks dominate the mean only because 63% of the frame sits under
value 0.05. Below 0.05 the tint is blue (cold ambient, the violet boundary mist); above it, warm
torchlight. So reaching 0.33 by editing the ambient and the mist at source would have meant
desaturating torchlight too — which is why that option was dropped rather than attempted.

## The identity claim, and the experiment that could not prove it

The case for shipping a grade into a calibrated renderer rests on the max channel never moving,
because `check_look.py` derives near-black share, bright share, mean value and the A3 actor spread
from it (`_value_sat` returns `rgb.max(axis=2)`).

**Captures cannot demonstrate it.** The same pinned golden scene captured twice with the grade OFF
both times differs on 120,606 max-channel pixels, by as much as 141 of 255. The on/off pair differs
on 74,007 — fewer. The golden gate's ~1e-5 self-diff comes from its block comparison and per-scene
noise envelope, not from raw per-pixel equality, and conflating the two cost an experiment.

What the experiment does establish is the ratio: saturation moved 0.5457 to 0.3345 against a noise
floor of 0.5457 to 0.5470, about 160x the noise, while every value-derived metric moved within noise
in both directions.

What actually backs the identity: the algebra above; a bit-exact property test over a 14^3 colour
grid by 7 strengths comparing `SingleToInt32Bits`; a synthetic negative in the same test (a
mix-toward-luma helper that must FAIL that assertion, proving it can fail); and a fixed-frame
simulation over twelve settings in which the three value metrics were identical to four decimals.

**Not built, and the honest next step:** `ShaderCompileTests` already stands up a hidden GL context.
Running the resolve shader over a known texture and reading it back would prove the property on the
GPU rather than in a mirror. Nothing in this wave does that.

## The stone sheen is a near-no-op, and that is reported rather than tuned away

With `Stone` and `Rock` opted in, pixels changed by more than 0.02 are **0.27% of the night frame**
and 0.095% of noon; mean luma moves 0.0794 to 0.0795. The A/B crop is not distinguishable by eye.

The likely cause is structural, not a bad roughness value: a specular lobe is view-dependent, and at
a fixed isometric camera over mostly flat ground the only surfaces that can catch one are vertical
stone faces — roughly the fraction measured. The value delivered here is the **path**: the world
program now has a material response, opt-in, proven inert when off. The `Stone`/`Rock` opt-in itself
sits below the visibility threshold at the shipped zoom, which is the same lesson the equipment wave
learned about thin-axis detail.

## Verification

- `dotnet build dream\dream.csproj`: 0 errors.
- Full suite on the integration branch: **Passed 10418 / Failed 0 / Skipped 47**.
- Golden: all six steps failed on **tone only** before the re-record (pixel and block passed
  everywhere), with the shifts in the dark percentiles exactly where a grade below value 0.08 acts.
  Re-recorded over 12 runs; `python -I tools/golden/tests/test_golden.py` 25 tests OK; two fresh
  verification passes, 6/6 both times, self-diffs 1e-6 to 7e-5.
- `python tools/ui-assets/enginelook.py`: `contract: 25 lines match the shaders`, PASS.
- The response-off identity was **measured**, not asserted: with the plumbing in and every material
  at zero, a full golden run passed against the pre-wave baselines.
- Captures at night with everything on: the boundary mist reads as charcoal with its red ember veins
  intact and still animates, the torch pool is unchanged, the HUD is unaffected (it is drawn after
  the resolve).

## Open items

- **`check_char.py` reports 2 problems on master**, and did before this wave:
  `palette: Bone albedo (0.4, 0.36, 0.29) x peak gain 2.59 = 1.03 reaches 1.0`. Master's pre-merge
  tree and the render-pipeline branch each reported 0, so **neither wave caused it alone** — the
  moon term and the new point-light window each add light and together cross Bone's 2.5 threshold.
  The gate's premise has also moved: nothing clips any more, so what survives of that bound is not
  clipping but contrast compression (the shoulder's slope at 1.03 is 0.216, squeezing Bone's local
  contrast to about a fifth). Rewriting it to measure that is the honest fix; relaxing it to
  "1.0 is fine now" and darkening the art to satisfy a dead constraint are both wrong.
- **Golden 01-play pixel noise grew 4.5-5x** on spawn-noon and wolf-staged, and 2.7x on wolf
  10-snap, while spawn-night 01-play *improved* 2x. The grade acts most at night, so that pattern
  argues against the wave and for the recording methodology (12 runs taken 36-way parallel, on the
  first frame after `play`, which the harness README warns is load-sensitive). Everything stays far
  below the floor-bound limits. Not isolated.
- Noon's A1 bright share (0.011 against a 0.020 floor) is untouched and out of scope: daylight has
  no local light pools, so moving it is a lighting change rather than a grade.

## Review corrections (`fix/colour-review`)

- **Correction to "proven inert when off".** The wave's reasoning that `AnimatedModel.BindProgram`
  resets the cube program's surface was wrong: `BindProgram` runs only on a mid-node switch to an
  alpha-mask variant, and `BeginRender` does not call it. The base `cube` program is safe only because
  nothing ever writes a non-zero surface to it. The terrain program's surface is now written in one
  helper (`Render.BindTerrainSurface`), reset at the start of the batched pass, and a structural test
  (`WorldMaterialResponseTests`) fails for any terrain bind or draw path that skips it.
- **The grade's `w <= 1` invariant** is now clamped in `dreamToneNeutraliseShadows`, in the C# mirror and
  in `PostChain`'s constructor, with a test at strengths 1.5 and 100. `PostChain` takes the neutralise
  strength as a parameter like the bloom strength, and `PostChainUniformContractTests` checks the three
  uniform-name arrays against the shader sources in both directions.
- **The Arcane emissive ratio.** The armour Membrane entry is about a sixth of the weapon gem, not a
  third (it was a third of the old value); comments and `ArmourArcaneEmissive_StaysBelowTheKnee_ByDesign`
  now say and pin that.
