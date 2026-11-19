# Golden-image compare

Catches visual regressions that unit tests cannot (a sword pointing at the sky, a trail above the head)
by diffing `DREAM_HUD_CAPTURE` PNGs against recorded baselines. numpy + PIL only; run with `python -I`.

## Use

```powershell
# Record baselines (each scene is captured -Runs times, default 4, min 3, to measure its own noise), then commit them.
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
 "env":["DREAM_FX_CLOCK=10","DREAM_MAX_FPS=60"],
 "pixelThreshold":0.08,"maxFraction":0.002,"ignore":[[0,0,200,40]]}
```

- Both images are box-downscaled x2. A pixel differs when its max channel difference exceeds `pixelThreshold`
  (0..1). `ignore` rects are `[x, y, w, h]` in full-resolution pixels and are excluded from the count.
- The baseline is the per-pixel MEDIAN of the N recorded runs (a flickering pixel settles on its usual value).
  `selfDiffFraction` in `baselines/<scene>/noise.json` is the worst leave-one-out difference: every run against the
  median of the OTHER runs, over every step (so it is not flattered by a run being its own baseline).
- A step FAILs when the differing fraction exceeds `max(maxFraction, k * selfDiffFraction)`, k =
  `golden.NOISE_MULTIPLIER` = 4, derived in `golden.effective_limit` and pinned by `tests/test_golden.py`.
- A missing baseline, a different set of step PNGs, or a size mismatch FAILs.

## Gate that cannot go blind

`python -I tools/golden/tests/test_golden.py` includes synthetic negatives: a moved 20x60 bar must FAIL, so a
compare that stopped measuring would be caught. `python -I tools/golden/golden.py selftest` is the short form.

## What varies between runs, and what each scene does about it

Measured on 5 runs per scene (pairwise and leave-one-out differences, diff PNGs):

| Source | Effect | Handled by |
|---|---|---|
| Fire flicker (+-25% intensity, wall clock; torch, braziers, lamps, their shadows) | the dominant noise: brightness of the lit ground changes by more than a small threshold, large at night where the lit ground is most of the frame | cannot be pinned (`DREAM_FX_CLOCK` pins clouds, sway and mist but explicitly NOT fire); higher `pixelThreshold` (noon 0.2, night 0.3) and a limit derived from the measured noise |
| Clouds, wind sway, boundary mist | none once pinned | `DREAM_FX_CLOCK=10` |
| Frame timing | none (server rolls) | `DREAM_MAX_FPS=60`, `-Seed 1` |
| Wolf fight: floating damage number (random 12/18), health orb, chat log, wolf pose | wolf-staged only | `ignore` rects for the number zone, the chat log and the health orb; the wolf's own pose stays in the compare |

Neither fire flicker nor the damage roll can be pinned without game code, so night and wolf-staged are
gross-change gates: a 20x60 bar is 0.08% of a 1440x1024 frame, below their noise (night 0.28%, wolf 0.09% self
difference), while a 200x300 block (4%) fails. `spawn-noon` is the sprite-level gate: its limit is 0.054%, below the
0.081% a 20x60 bar adds. `tests/test_golden.py` pins both facts against the committed baselines.

### Choosing k

With 5 recorded runs, for every 4-run subset: self = worst leave-one-out difference of the 4, held-out = the 5th
run against their median. Worst held-out/self ratio: noon 1.1, night 1.7, wolf 3.96 (th 0.2..0.3; the wolf's self is
tiny, so its ratio is noisy), hence k = 4 with the per-scene floor `maxFraction` covering a near-zero self.
Verification after recording: 8 sequential compares (noon) / 3 sequential + 1 parallel (all scenes) all PASS;
the worst run used 73% of the limit.
