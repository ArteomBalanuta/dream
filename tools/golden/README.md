# Golden-image compare

Catches visual regressions that unit tests cannot (a sword pointing at the sky, a trail above the head)
by diffing `DREAM_HUD_CAPTURE` PNGs against recorded baselines. numpy + PIL only; run with `python -I`.

## Use

```powershell
# Record baselines (each scene is captured twice to measure its own run-to-run noise), then commit them.
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

`golden.py` can be used directly: `compare <actualDir> <scene>`, `record <dirA> <dirB> <scene>`, `selftest`.

## Scene file `scenes/<name>.json`

```json
{"script":"play,keys:g:3,wait:120,snap","timeOfDay":"noon","seed":1,
 "env":["DREAM_FX_CLOCK=10","DREAM_MAX_FPS=60"],
 "pixelThreshold":0.08,"maxFraction":0.002,"ignore":[[0,0,200,40]]}
```

- Both images are box-downscaled x2. A pixel differs when its max channel difference exceeds `pixelThreshold`
  (0..1). `ignore` rects are `[x, y, w, h]` in full-resolution pixels and are excluded from the count.
- A step FAILs when the differing fraction exceeds `max(maxFraction, 3 * selfDiffFraction)`; `selfDiffFraction`
  is the A-vs-B difference stored in `baselines/<scene>/noise.json` at record time. The relation is derived in
  `golden.effective_limit` and pinned by `tests/test_golden.py`.
- A missing baseline, a different set of step PNGs, or a size mismatch FAILs.

## Gate that cannot go blind

`python -I tools/golden/tests/test_golden.py` includes synthetic negatives: a moved 20x60 bar must FAIL, so a
compare that stopped measuring would be caught. `python -I tools/golden/golden.py selftest` is the short form.
