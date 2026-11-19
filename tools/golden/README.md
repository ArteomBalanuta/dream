# Golden-image compare

Catches visual regressions that unit tests cannot (a sword pointing at the sky, a trail above the head)
by diffing `DREAM_HUD_CAPTURE` PNGs against recorded baselines. numpy + PIL only; run with `python -I`.

## Use

```powershell
# Record baselines (each scene is captured -Runs times, default 4, min 3 - use -Runs 10 or more for committed baselines - to measure its own noise), then commit them.
powershell -ExecutionPolicy Bypass -File tools\golden\run.ps1 -Scene all -Record -Parallel
# Compare the current build against them. Exit 0 only when every scene PASSes.
powershell -ExecutionPolicy Bypass -File tools\golden\run.ps1 -Scene all
powershell -ExecutionPolicy Bypass -File tools\golden\run.ps1 -Scene spawn-noon -FromSource
```

`run.ps1` calls `skills/capturing-dream-screenshots/scripts/capture.ps1` per scene with `-Name golden-<scene>`,
`-DataDir <capture dir>\data` and the scene's `-Seed`, so runs are reproducible and can go `-Parallel`.
Captures land in `artifacts/golden/capture/golden-<scene>/`; diff images in
`artifacts/golden/<scene>/<png>-diff.png` (differing pixels red over a dimmed actual).
Build the capture exe first (see the capture skill), or pass `-Bin`.

`golden.py` can be used directly: `compare <actualDir> <scene>`, `record <scene> <dir> <dir> <dir> [...]`, `selftest`.

## Scene file `scenes/<name>.json`

```json
{"script":"play,keys:g:3,wait:120,snap","timeOfDay":"noon","seed":1,
 "env":["DREAM_FX_CLOCK=10","DREAM_FIRE_CLOCK=10","DREAM_DAY_LENGTH_MINUTES=1440","DREAM_MAX_FPS=60"],
 "pixelThreshold":0.08,"maxFraction":0.002,"ignore":[[0,0,200,40]]}
```

Optional `blockFloor` (default 0.06) and `blockMultiplier` (default 5) tighten the block check of a scene whose noise is
measured small (wolf-staged: 0.04 and 3).

- Both images are box-downscaled x2. `ignore` rects are `[x, y, w, h]` in full-resolution pixels and are excluded from
  every check below.
- The baseline is the per-pixel MEDIAN of the N recorded runs (N >= 8 in the committed baselines: 18 per scene, 36 for wolf-staged).
  `baselines/<scene>/noise.json` + `blocks.npz` hold each step's own run-to-run noise: every run against the median of
  the OTHER runs (leave-one-out), so it is not flattered by a run being its own baseline.
- A step PASSes only when all three checks hold (a failing line ends in `<- failed: pixel|tone|block`):

| Check | Measures | Limit |
|---|---|---|
| pixel | fraction of pixels whose max channel differs by more than `pixelThreshold` | `max(maxFraction, 3 * perStep[step])` (`NOISE_MULTIPLIER`); the scene-wide `selfDiffFraction` only if the step is absent |
| tone | relative shift of mean luma, R/G/B means and luma p10/p25/p50/p75/p95 (outside ignore rects) | per statistic `max(0.04, 3 * noise.json tone[step][stat])` |
| block | gain-normalised mean abs diff per 8x8 block (16x16 capture px) of the half-res frame, vs that block's own noise (3x3 neighbourhood max of the recorded worst) | per block `max(0.06, 5 * blocks.npz[step][block])` |

- **Tone** exists because the pixel threshold (0.2 / 0.3) is blind to a global darkening: x0.8 moves almost no pixel by
  more than 0.2, so 0% differ. Fire flicker moves the mean (night: up to 17%) and the lit percentiles a lot, but the dark
  percentiles (p10/p25) hardly, so a x0.8 darkening shows there (noon p10 22% vs limit 4%; night p25 11-14% vs 6.0-6.5%).
  Statistics that flicker a lot get a wide limit and are simply inert.
- **Block** exists so a small missing figure fails (the pixel fraction of a 28x101 figure, 0.19%, is below the noise at night
  and wolf). Flicker is a smooth multiplicative gain on the lit ground, so `a` is divided by a local gain (median of the
  per-pixel luma ratio per 16x16 cell, then the median over 5x5 cells) before the diff; a vanished figure is a small
  minority of that window and is not absorbed. Without the gain, the blocks next to the torch (where the player
  stands) are as noisy as the figure's signal; with it, noon/wolf blocks there drop from 0.04 to 0.01. The diff PNG tints
  failing blocks yellow and differing pixels red.
- A missing baseline, missing noise files, a different set of step PNGs, or a size mismatch FAILs.

## Gate that cannot go blind

`python -I tools/golden/tests/test_golden.py` includes synthetic and real-baseline negatives: a moved bar, a x0.8 and x0.6
global darkening on every step of every scene, the player figure painted out (noon, wolf), a 100x100 block (bright over
the player, black on lit ground; all scenes incl. night), a 200x300 block, and a 20x60 bar on noon. `python -I
tools/golden/golden.py selftest` is the short form.

## What varies between runs, and what each scene does about it

| Source | Effect | Handled by |
|---|---|---|
| Fire flicker (+-25% intensity, wall clock; torch, braziers, lamps, their shadows) | the dominant noise: brightness of the lit ground and the position of soft shadow edges | pinned by `DREAM_FIRE_CLOCK=10` (particles and embers still run live); `pixelThreshold` (noon/wolf 0.2, night 0.3), noise-derived limits, the gain-normalised block check |
| Fire-shadow slot choice (which 4 fires cast shadows) | a whole shadow wedge appeared in some runs and not others | fixed in the engine on 2026-11-19: slots are ranked by each fire's steady brightness (`PointLight.Flicker` divided back out), so flicker can no longer swap them |
| Sun position: the world clock runs in real time, so a run under load (a 36-way `-Record -Parallel`) snaps later and the sun's shadows have moved | a sun-shadow band over the barrel in wolf-staged 01-play differed between record and compare | `tod:<hours>` as the second script step pins the visual time for every step after it, and `DREAM_DAY_LENGTH_MINUTES=1440` slows the clock 48x for the `play` frame before it |
| Clouds, wind sway, boundary mist | none once pinned | `DREAM_FX_CLOCK=10` |
| Frame timing | none (server rolls) | `DREAM_MAX_FPS=60`, `-Seed 1` |
| Wolf bite: floating damage number (12 or 18) and hit flash | wolf-staged only | the scene no longer captures a live wolf. The wolf bites at a real-time phase the script cannot pin, so the number floats from the player's head (y 430) to the Gate Warden (y 185) at a different height in every run (failed 5 of 16 runs: block 0.14-0.31 vs 0.06 at x 704..736, y 320..430). The script spawns the wolf, aims at it (`aimenemy`, the pointer stays latched so the player keeps the Combat pose and facing), then `dev:clear`s it and waits 120 frames (+30 from the dev step) for every number to expire before the snap. There is no damage-number `ignore` rect any more; only the chat log `[0,840,340,90]` (three dev lines) and the health orb `[180,890,140,134]` are masked, so the whole world and the player are compared |

The frame no longer shows the wolf model; the player's pose, facing, cursor and Combat HUD are what it pins. Chat text and the health orb (76 or 88 of 100) are masked.

## Choosing the multipliers

`golden.py study <scene> <run dirs...>` is the held-out study: for every run h, the noise comes from the other runs and h
is measured against their median; it prints the smallest k that would have passed every held-out run (counting only
items above their floor). 18 runs per scene (two capture batches of 10 and 8, the second taken after the first), 2 steps
each, final floors:

| | pixel (floor = maxFraction) | tone (floor 0.04) | block (floor 0.06) |
|---|---|---|---|
| noon | 0.00 (never above floor) | 1.19-1.25 | 1.03-1.07 |
| night | 1.20-1.37 | 1.29-1.43 | 1.45-2.02 (at floor 0.08) |
| wolf (old live-wolf scene) | 0.00-0.99 | 1.12-1.47 | 1.01-2.95 |

Worst observed: pixel 1.37, tone 1.47, block 2.95. Multipliers chosen with margin: pixel k = 3 (2.2x), tone k = 3 (2.0x),
block k = 5 (1.7x). The old k = 4 sat exactly on 3.96 measured from 5 runs; the old 5-run and a 10-run study had
heavy block tails (6.7, 8.8) because one extreme-flicker run was held out; with 18 runs the tail is covered. Floors:
tone 0.04 (quiet noon p10 deviates 0.2%), block 0.06 (held-out noon/wolf peak 0.07 is covered by noise-derived limits;
a soft shadow edge near (520,650) shifts by 0.054-0.067 in some runs and was missing from a 10-run baseline, which is
why the baseline now holds 18).

Verification after recording (fresh captures, not part of the baseline): `run.ps1 -Scene all` 5 sequential + 3 `-Parallel`,
all PASS. Worst limit usage over the runs: pixel noon 43%, night 25%, wolf 38%; tone 67% (night p25); block
below 50% outside the floor-limited blocks. Doctored negatives on the committed baselines, value vs limit:

| Doctoring | noon | night | wolf |
|---|---|---|---|
| x0.8 everywhere (tone) | p10 0.222 vs 0.040 | p25 0.112 vs 0.065 | p10 0.212 vs 0.040 |
| player figure painted out (block) | 0.124 vs 0.063 (also pixel 0.064% vs 0.04%) | not required (figure is in the dark) | 0.088 vs 0.040 (2.2x, 6 blocks; also pixel 0.060% vs 0.050%) |
| 100x100 bright over the player | fails | block 0.71 vs 0.35, pixel 0.68% vs 0.47% | fails |
| 100x100 black on lit ground | fails | block 0.23 vs 0.06 | fails |

### wolf-staged after fix loop 3 (deterministic scene)

36 baseline runs (12 + 24, all from the same script). With the wolf and its damage number gone the per-block noise is
small, so the scene uses `blockFloor` 0.04 and `blockMultiplier` 3 (the 0.06 floor left the figure-removal negative at
1.4x). Verification, fresh captures not in the baseline: 24 runs, 48 steps, all PASS; worst usage pixel 22% (0.000109 vs
0.0005), tone 40%, block 52% (floor-limited blocks). `run.ps1 -Scene all -Parallel` 3 times, all PASS. Negatives on the
committed baseline: x0.8 everywhere FAILs (tone p10 0.226 vs 0.040), player figure painted out FAILs at 2.2x the block limit
(0.088 vs 0.040). Caveat: lowering the floor to 0.04 with the 5x multiplier is NOT safe: a block at (304,816) shows a rare
0.048 mode that a 12-run baseline had recorded as 0.01 noise; the 36-run baseline covers it.

## Re-recorded 2026-11-19 (graphics quick wins)

The baselines went stale when the torch-flame fix landed and the quick-wins wave added moon shadows. Before re-recording,
the fire-shadow slot choice was made steady and the fire clock and sun were pinned (table above). Run-to-run noise
(`selfDiffFraction`) dropped from 0.0021 to 0.000011 (night), 0.000098 to 0.000008 (noon) and 0.00013 to 0.000017
(wolf-staged). `test_golden.py` (all real-baseline negatives) OK, and `run.ps1 -Scene all -Parallel` 3 times, all PASS.
A re-record without those fixes passed `run.ps1` but failed the negatives (a x0.8 night darkening passed): always run
`test_golden.py` before committing baselines.
