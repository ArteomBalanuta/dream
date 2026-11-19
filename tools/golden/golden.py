#!/usr/bin/env python
"""Golden-image compare over DREAM_HUD_CAPTURE PNGs (numpy + PIL only).

  golden.py compare <actualDir> <scene>            compare a capture folder with the recorded baseline
  golden.py record <scene> <dir> <dir> <dir> [...]  store the per-pixel median (downscaled x2) + noise.json
  golden.py study <scene> <dir> <dir> [...]         held-out study: smallest k per check that passes every held-out run
  golden.py selftest                               synthetic positives and negatives; exit 0 when all hold

<scene> is a name under tools/golden/scenes/ (or a path to a scene .json). Scene format and the
derived limit are documented in README.md. Run with `python -I`.
"""
from __future__ import annotations

import argparse
import json
import sys
import tempfile
import warnings
from pathlib import Path

import numpy as np
from PIL import Image

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent
SCENES = HERE / "scenes"
BASELINES = HERE / "baselines"
OUT_ROOT = REPO / "artifacts" / "golden"

# Three checks per step, each with a limit derived from the baseline's own run-to-run noise (leave-one-out over the
# recorded runs: every run against the median of the OTHER runs). See README, "Choosing the multipliers".
#   pixel : fraction of pixels whose max channel differs by > pixelThreshold      max(maxFraction, K_PIXEL * perStep)
#   tone  : relative shift of the frame's mean luma, channel means and luma percentiles   max(TONE_FLOOR, K_TONE * dev)
#   block : gain-normalised mean abs diff per 8x8 block (half res)                max(BLOCK_FLOOR, K_BLOCK * noise)
NOISE_MULTIPLIER = 3.0  # pixel
TONE_MULTIPLIER = 3.0
TONE_FLOOR = 0.04
BLOCK_MULTIPLIER = 5.0
BLOCK_FLOOR = 0.06
BLOCK = 8        # block edge in downscaled pixels (16 px of the capture)
GAIN_BLOCK = 16  # cell of the local-gain estimate, downscaled pixels
GAIN_WIN = 5     # median window, in gain cells
TONE_EPS = 0.01  # luma below this does not inflate a relative deviation
TONE_NAMES = ["meanLuma", "meanR", "meanG", "meanB", "p10", "p25", "p50", "p75", "p95"]
_LUMA = np.array([0.2126, 0.7152, 0.0722], dtype=np.float32)


def effective_limit(max_fraction: float, self_diff_fraction: float, k: float | None = None) -> float:
    """max(maxFraction, k * selfDiffFraction): the scene floor, widened by measured noise."""
    return max(max_fraction, (NOISE_MULTIPLIER if k is None else k) * self_diff_fraction)


def tone_limits(noise_dev: np.ndarray) -> np.ndarray:
    """Per-statistic relative limit: max(TONE_FLOOR, TONE_MULTIPLIER * recorded leave-one-out deviation)."""
    return np.maximum(TONE_FLOOR, TONE_MULTIPLIER * np.asarray(noise_dev, dtype=np.float64))


def block_limits(noise_blocks: np.ndarray) -> np.ndarray:
    return np.maximum(BLOCK_FLOOR, BLOCK_MULTIPLIER * noise_blocks)


def load_scene(scene: str) -> dict:
    path = Path(scene)
    if not path.suffix:
        path = SCENES / f"{scene}.json"
    data = json.loads(path.read_text(encoding="utf-8"))
    data.setdefault("pixelThreshold", 0.08)
    data.setdefault("maxFraction", 0.002)
    data.setdefault("ignore", [])
    data["name"] = path.stem
    return data


def read_rgb(path: Path) -> np.ndarray:
    """float32 HxWx3 in 0..1."""
    with Image.open(path) as image:
        return np.asarray(image.convert("RGB"), dtype=np.float32) / 255.0


def downscale2(pixels: np.ndarray) -> np.ndarray:
    """2x2 box filter; an odd trailing row/column is dropped."""
    h, w = pixels.shape[0] // 2 * 2, pixels.shape[1] // 2 * 2
    p = pixels[:h, :w]
    return (p[0::2, 0::2] + p[1::2, 0::2] + p[0::2, 1::2] + p[1::2, 1::2]) / 4.0


def ignore_mask(shape: tuple[int, int], rects: list, scale: int = 2) -> np.ndarray:
    """True where a pixel is ignored. Rects are [x, y, w, h] in FULL-resolution pixels; the mask is at
    the downscaled size, covering every downscaled pixel the rect touches."""
    mask = np.zeros(shape, dtype=bool)
    for x, y, w, h in rects:
        x0, y0 = int(x) // scale, int(y) // scale
        x1, y1 = -(-int(x + w) // scale), -(-int(y + h) // scale)
        mask[max(y0, 0):max(y1, 0), max(x0, 0):max(x1, 0)] = True
    return mask


def diff_mask(a: np.ndarray, b: np.ndarray, threshold: float, ignore: np.ndarray) -> np.ndarray:
    """Pixels whose max channel abs diff exceeds threshold, outside the ignore mask."""
    return (np.abs(a - b).max(axis=2) > threshold) & ~ignore


def diff_fraction(a: np.ndarray, b: np.ndarray, threshold: float, ignore: np.ndarray) -> float:
    counted = int((~ignore).sum())
    if counted == 0:
        return 0.0
    return float(diff_mask(a, b, threshold, ignore).sum()) / counted


def write_diff(actual: np.ndarray, differing: np.ndarray, path: Path) -> None:
    out = actual * 0.35
    out[differing] = (1.0, 0.0, 0.0)
    path.parent.mkdir(parents=True, exist_ok=True)
    Image.fromarray((out * 255.0 + 0.5).astype(np.uint8)).save(path)


def list_pngs(folder: Path) -> list[Path]:
    return sorted(folder.glob("*.png"), key=lambda p: p.name) if folder.is_dir() else []


def luma_of(pixels: np.ndarray) -> np.ndarray:
    return pixels @ _LUMA


def tone_stats(frame: np.ndarray, ignore: np.ndarray) -> np.ndarray:
    """Global tone outside the ignore rects: mean luma, per-channel means, luma percentiles 10/25/50/75/95."""
    keep = ~ignore
    if not keep.any():
        return np.zeros(len(TONE_NAMES))
    luma = luma_of(frame)[keep]
    means = [float(frame[..., c][keep].mean()) for c in range(3)]
    return np.array([float(luma.mean()), *means, *np.percentile(luma, [10, 25, 50, 75, 95])], dtype=np.float64)


def tone_deviation(actual: np.ndarray, base: np.ndarray) -> np.ndarray:
    """|actual - base| / max(base, TONE_EPS), per statistic: a relative shift that tiny dark values cannot inflate."""
    return np.abs(actual - base) / np.maximum(base, TONE_EPS)


def gain_field(a: np.ndarray, b: np.ndarray, ignore: np.ndarray) -> np.ndarray:
    """Local brightness gain of a over b, one value per GAIN_BLOCK cell: the median per-pixel luma ratio of the
    cell, then the median over a GAIN_WIN x GAIN_WIN window of cells. Fire flicker is a smooth multiplicative
    change of the lit ground; this estimates it, and a figure that vanished is a small minority inside the
    window, so the median does not absorb it."""
    gb = GAIN_BLOCK
    h, w = a.shape[0] // gb * gb, a.shape[1] // gb * gb
    if h == 0 or w == 0:
        return np.ones((0, 0), dtype=np.float32)
    la, lb = luma_of(a)[:h, :w], luma_of(b)[:h, :w]
    valid = (lb > 0.03) & ~ignore[:h, :w]
    ratio = np.where(valid, la / np.maximum(lb, 1e-6), np.nan)
    cells = ratio.reshape(h // gb, gb, w // gb, gb).transpose(0, 2, 1, 3).reshape(h // gb, w // gb, gb * gb)
    with np.errstate(all="ignore"), warnings.catch_warnings():
        warnings.simplefilter("ignore")
        cell = np.nanmedian(cells, axis=2)
        cell[np.isnan(cells).sum(axis=2) > gb * gb * 3 // 4] = np.nan  # fewer than a quarter of the pixels usable
        p = GAIN_WIN // 2
        padded = np.pad(cell, p, constant_values=np.nan)
        stack = np.stack([padded[i:i + cell.shape[0], j:j + cell.shape[1]] for i in range(GAIN_WIN) for j in range(GAIN_WIN)])
        gain = np.nanmedian(stack, axis=0)
    return np.nan_to_num(gain, nan=1.0).astype(np.float32)


def block_diff(a: np.ndarray, b: np.ndarray, ignore: np.ndarray) -> np.ndarray:
    """Mean abs channel diff per BLOCK x BLOCK block after dividing a by the local gain (see gain_field);
    ignored pixels count as 0 diff."""
    gain = gain_field(a, b, ignore)
    norm = a.copy()
    if gain.size:
        full = np.repeat(np.repeat(gain, GAIN_BLOCK, axis=0), GAIN_BLOCK, axis=1)
        norm[:full.shape[0], :full.shape[1]] /= np.maximum(full, 0.05)[..., None]
    d = np.abs(norm - b).mean(axis=2)
    d[ignore] = 0.0
    bs = BLOCK
    h, w = d.shape[0] // bs * bs, d.shape[1] // bs * bs
    return d[:h, :w].reshape(h // bs, bs, w // bs, bs).mean(axis=(1, 3))


def dilate3(blocks: np.ndarray) -> np.ndarray:
    """3x3 neighbourhood maximum: a noisy block also covers its neighbours (flame sway shifts the noise by a block)."""
    p = np.pad(blocks, 1, mode="edge")
    h, w = blocks.shape
    return np.max([p[i:i + h, j:j + w] for i in range(3) for j in range(3)], axis=0)


def step_noise(frames: list[np.ndarray], ignore: np.ndarray, threshold: float) -> dict:
    """Leave-one-out noise of one step: each run against the median of the OTHER runs (what a later compare sees)."""
    pixel, tone, blocks = 0.0, np.zeros(len(TONE_NAMES)), None
    for k, frame in enumerate(frames):
        others = median_frame(frames[:k] + frames[k + 1:])
        pixel = max(pixel, diff_fraction(frame, others, threshold, ignore))
        tone = np.maximum(tone, tone_deviation(tone_stats(frame, ignore), tone_stats(others, ignore)))
        bd = block_diff(frame, others, ignore)
        blocks = bd if blocks is None else np.maximum(blocks, bd)
    return {"pixel": pixel, "tone": tone, "blocks": dilate3(blocks)}


def compare(actual_dir: Path, scene: dict, baselines: Path = BASELINES, out_root: Path = OUT_ROOT) -> list[tuple[bool, str]]:
    """One (ok, line) per step; a missing baseline or a step-count/size mismatch fails.

    A step passes only when all three checks hold: pixel fraction, global tone, block structure."""
    name = scene["name"]
    base_dir = baselines / name
    base = list_pngs(base_dir)
    actual = list_pngs(actual_dir)
    if not base:
        return [(False, f"FAIL {name} - no baseline PNGs in {base_dir} (record them first)")]
    if not actual:
        return [(False, f"FAIL {name} - no PNGs in {actual_dir}")]
    if [p.name for p in base] != [p.name for p in actual]:
        return [(False, f"FAIL {name} - step mismatch: baseline {[p.name for p in base]} vs actual {[p.name for p in actual]}")]

    noise_path, blocks_path = base_dir / "noise.json", base_dir / "blocks.npz"
    if not noise_path.exists() or not blocks_path.exists():
        return [(False, f"FAIL {name} - noise.json/blocks.npz missing in {base_dir} (re-record the baseline)")]
    noise = json.loads(noise_path.read_text(encoding="utf-8"))
    block_noise = np.load(blocks_path)
    results = []
    for b_path, a_path in zip(base, actual):
        b_img = read_rgb(b_path)
        a_img = downscale2(read_rgb(a_path))
        if a_img.shape != b_img.shape:
            results.append((False, f"FAIL {name} {a_path.name} - size mismatch: baseline {b_img.shape[1]}x{b_img.shape[0]} "
                                   f"vs actual (downscaled) {a_img.shape[1]}x{a_img.shape[0]}"))
            continue
        step = a_path.name
        # per-step noise; the scene-wide worst only when the step is not recorded
        self_diff = float(noise.get("perStep", {}).get(step, noise.get("selfDiffFraction", 0.0)))
        limit = effective_limit(float(scene["maxFraction"]), self_diff)
        ignore = ignore_mask(a_img.shape[:2], scene["ignore"])
        differing = diff_mask(a_img, b_img, float(scene["pixelThreshold"]), ignore)
        counted = int((~ignore).sum())
        fraction = float(differing.sum()) / counted if counted else 0.0

        tone_limit = tone_limits(noise["tone"][step])
        tone_dev = tone_deviation(tone_stats(a_img, ignore), tone_stats(b_img, ignore))
        t_i = int(np.argmax(tone_dev / tone_limit))

        b_limit = block_limits(block_noise[step])
        bd = block_diff(a_img, b_img, ignore)
        if bd.shape != b_limit.shape:
            results.append((False, f"FAIL {name} {step} - block grid mismatch {bd.shape} vs {b_limit.shape} (re-record)"))
            continue
        over = bd > b_limit
        ratio = bd / b_limit
        bi = np.unravel_index(int(np.argmax(ratio)), ratio.shape)

        out = a_img * 0.35
        for y, x in zip(*np.nonzero(over)):  # blocks over their limit tinted yellow
            blk = out[y * BLOCK:(y + 1) * BLOCK, x * BLOCK:(x + 1) * BLOCK]
            blk[...] = blk * 0.5 + np.array([0.5, 0.5, 0.0], dtype=np.float32)
        out[differing] = (1.0, 0.0, 0.0)
        (out_root / name).mkdir(parents=True, exist_ok=True)
        Image.fromarray((np.clip(out, 0, 1) * 255.0 + 0.5).astype(np.uint8)).save(out_root / name / f"{a_path.stem}-diff.png")

        pixel_ok = fraction <= limit
        tone_ok = bool((tone_dev <= tone_limit).all())
        block_ok = not bool(over.any())
        ok = pixel_ok and tone_ok and block_ok
        why = "".join(f" {n}" for n, good in (("pixel", pixel_ok), ("tone", tone_ok), ("block", block_ok)) if not good)
        results.append((ok, f"{'PASS' if ok else 'FAIL'} {name} {step} {fraction:.6f} {limit:.6f}"
                            f" | tone {TONE_NAMES[t_i]} {tone_dev[t_i]:.4f} {tone_limit[t_i]:.4f}"
                            f" | block {float(bd[bi]):.4f} {float(b_limit[bi]):.4f} @{bi[1] * BLOCK * 2},{bi[0] * BLOCK * 2}"
                            f" ({int(over.sum())} over)" + (f" <- failed:{why}" if why else "")))
    return results


MIN_RECORD_RUNS = 3


def median_frame(frames: list[np.ndarray]) -> np.ndarray:
    """Per-pixel median of the runs: a flickering pixel settles on its usual value, not on one run's."""
    return np.median(np.stack(frames), axis=0).astype(np.float32)


def load_runs(dirs: list[Path]) -> tuple[list[str], list[list[Path]]]:
    if len(dirs) < MIN_RECORD_RUNS:
        raise SystemExit(f"record needs at least {MIN_RECORD_RUNS} runs, got {len(dirs)}")
    listings = [list_pngs(d) for d in dirs]
    if not listings[0]:
        raise SystemExit(f"no PNGs in {dirs[0]}")
    names = [p.name for p in listings[0]]
    for d, l in zip(dirs, listings):
        if [p.name for p in l] != names:
            raise SystemExit(f"{d} wrote different steps: {[p.name for p in l]} vs {names}")
    return names, listings


def record(dirs: list[Path], scene: dict, baselines: Path = BASELINES) -> float:
    """Store the per-pixel median of >= 3 runs (downscaled x2), noise.json and blocks.npz.

    noise.json: perStep (worst leave-one-out pixel fraction), selfDiffFraction (their max) and tone (per step, per
    statistic: worst leave-one-out relative deviation). blocks.npz: per step, the worst leave-one-out block diff.
    Leave-one-out = each run against the median of the OTHER runs, which is what a later compare sees."""
    names, listings = load_runs(dirs)
    target = baselines / scene["name"]
    target.mkdir(parents=True, exist_ok=True)
    for old in target.glob("*.png"):
        old.unlink()
    threshold = float(scene["pixelThreshold"])
    worst = 0.0
    per_step: dict[str, float] = {}
    tone: dict[str, list[float]] = {}
    blocks: dict[str, np.ndarray] = {}
    for i, name in enumerate(names):
        frames = [downscale2(read_rgb(l[i])) for l in listings]
        if any(f.shape != frames[0].shape for f in frames):
            raise SystemExit(f"{name}: the runs differ in size")
        ignore = ignore_mask(frames[0].shape[:2], scene["ignore"])
        n = step_noise(frames, ignore, threshold)
        per_step[name] = n["pixel"]
        tone[name] = [round(float(v), 6) for v in n["tone"]]
        blocks[name] = n["blocks"].astype(np.float32)
        worst = max(worst, n["pixel"])
        base = median_frame(frames)
        Image.fromarray((base * 255.0 + 0.5).astype(np.uint8)).save(target / name)
    noise = {"selfDiffFraction": worst, "runs": len(dirs), "perStep": per_step, "toneStats": TONE_NAMES, "tone": tone}
    (target / "noise.json").write_text(json.dumps(noise, indent=1) + "\n", encoding="utf-8")
    np.savez_compressed(target / "blocks.npz", **blocks)
    return worst


def study(dirs: list[Path], scene: dict) -> None:
    """Held-out study behind the multipliers: for every run h, take the noise of the OTHER runs, then measure
    run h against their median. Prints, per step, the smallest multiplier k that would have let every held-out
    run pass each check (max over items of dev/noise, counting only items above their floor)."""
    names, listings = load_runs(dirs)
    threshold = float(scene["pixelThreshold"])
    for i, name in enumerate(names):
        frames = [downscale2(read_rgb(l[i])) for l in listings]
        ignore = ignore_mask(frames[0].shape[:2], scene["ignore"])
        need = {"pixel": 0.0, "tone": 0.0, "block": 0.0}
        peak = {"tone": 0.0, "block": 0.0}
        for h in range(len(frames)):
            rest = frames[:h] + frames[h + 1:]
            n = step_noise(rest, ignore, threshold)
            med = median_frame(rest)
            frac = diff_fraction(frames[h], med, threshold, ignore)
            if frac > scene["maxFraction"] and n["pixel"] > 0:
                need["pixel"] = max(need["pixel"], frac / n["pixel"])
            dev = tone_deviation(tone_stats(frames[h], ignore), tone_stats(med, ignore))
            peak["tone"] = max(peak["tone"], float(dev.max()))
            m = dev > TONE_FLOOR
            if m.any():
                need["tone"] = max(need["tone"], float((dev[m] / np.maximum(n["tone"][m], 1e-9)).max()))
            bd = block_diff(frames[h], med, ignore)
            m = bd > BLOCK_FLOOR
            if m.any():
                need["block"] = max(need["block"], float((bd[m] / np.maximum(n["blocks"][m], 1e-9)).max()))
            peak["block"] = max(peak["block"], float(bd.max()))
        print(f"{scene['name']} {name}: runs={len(frames)} min k to pass every held-out run: "
              f"pixel {need['pixel']:.2f} tone {need['tone']:.2f} block {need['block']:.2f} "
              f"(worst held-out tone dev {peak['tone']:.4f}, block diff {peak['block']:.4f})")


# ---------------------------------------------------------------- selftest

def _save(path: Path, pixels: np.ndarray) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    Image.fromarray(pixels).save(path)


def synthetic_frame(bar_x: int | None = 100, size: tuple[int, int] = (320, 200)) -> np.ndarray:
    """A gradient with a bright 20x60 bar at bar_x (None = no bar)."""
    w, h = size
    frame = np.zeros((h, w, 3), dtype=np.uint8)
    frame[..., 0] = np.linspace(20, 90, w, dtype=np.uint8)[None, :]
    frame[..., 1] = np.linspace(30, 70, h, dtype=np.uint8)[:, None]
    frame[..., 2] = 40
    if bar_x is not None:
        frame[60:120, bar_x:bar_x + 20] = (240, 240, 240)
    return frame


def selftest() -> int:
    failures: list[str] = []

    def check(condition: bool, what: str) -> None:
        if not condition:
            failures.append(what)

    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        scene = {"name": "t", "pixelThreshold": 0.08, "maxFraction": 0.002, "ignore": []}
        runs = [root / f"r{i}" for i in range(MIN_RECORD_RUNS)]
        for run in runs:
            _save(run / "s1.png", synthetic_frame())
        run_a = runs[0]
        record(runs, scene, root / "base")
        out = root / "out"

        def verdict(actual: Path, sc: dict = scene) -> bool:
            return all(ok for ok, _ in compare(actual, sc, root / "base", out))

        check(verdict(run_a), "identical frame must PASS")
        moved = root / "moved"
        _save(moved / "s1.png", synthetic_frame(bar_x=180))
        check(not verdict(moved), "a moved bar must FAIL")
        missing = root / "missing"
        check(not verdict(missing), "no actual PNGs must FAIL")
    print("selftest: " + ("ok" if not failures else "FAILED: " + "; ".join(failures)))
    return 0 if not failures else 1


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)
    p = sub.add_parser("compare")
    p.add_argument("actual_dir", type=Path)
    p.add_argument("scene")
    p.add_argument("--baselines", type=Path, default=BASELINES)
    p.add_argument("--out", type=Path, default=OUT_ROOT)
    p = sub.add_parser("record")
    p.add_argument("scene")
    p.add_argument("run_dirs", type=Path, nargs="+")
    p.add_argument("--baselines", type=Path, default=BASELINES)
    p = sub.add_parser("study")
    p.add_argument("scene")
    p.add_argument("run_dirs", type=Path, nargs="+")
    sub.add_parser("selftest")
    args = parser.parse_args(argv)

    if args.command == "selftest":
        return selftest()
    scene = load_scene(args.scene)
    if args.command == "study":
        study(args.run_dirs, scene)
        return 0
    if args.command == "record":
        noise = record(args.run_dirs, scene, args.baselines)
        print(f"recorded {scene['name']} selfDiffFraction={noise:.6f}")
        return 0
    results = compare(args.actual_dir, scene, args.baselines, args.out)
    for _, line in results:
        print(line)
    return 0 if all(ok for ok, _ in results) else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
