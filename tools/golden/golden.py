#!/usr/bin/env python
"""Golden-image compare over DREAM_HUD_CAPTURE PNGs (numpy + PIL only).

  golden.py compare <actualDir> <scene>            compare a capture folder with the recorded baseline
  golden.py record <actualDirA> <actualDirB> <scene>   store A (downscaled x2) + noise.json from A-vs-B
  golden.py selftest                               synthetic positives and negatives; exit 0 when all hold

<scene> is a name under tools/golden/scenes/ (or a path to a scene .json). Scene format and the
derived limit are documented in README.md. Run with `python -I`.
"""
from __future__ import annotations

import argparse
import json
import sys
import tempfile
from pathlib import Path

import numpy as np
from PIL import Image

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent
SCENES = HERE / "scenes"
BASELINES = HERE / "baselines"
OUT_ROOT = REPO / "artifacts" / "golden"

# The effective limit is a multiple of the baseline's own run-to-run noise, never below the scene's floor.
NOISE_MULTIPLIER = 3.0


def effective_limit(max_fraction: float, self_diff_fraction: float) -> float:
    """max(maxFraction, 3 * selfDiffFraction): the scene floor, widened by measured noise."""
    return max(max_fraction, NOISE_MULTIPLIER * self_diff_fraction)


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


def compare(actual_dir: Path, scene: dict, baselines: Path = BASELINES, out_root: Path = OUT_ROOT) -> list[tuple[bool, str]]:
    """One (ok, line) per step; a missing baseline or a step-count/size mismatch fails."""
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

    noise_path = base_dir / "noise.json"
    self_diff = float(json.loads(noise_path.read_text(encoding="utf-8"))["selfDiffFraction"]) if noise_path.exists() else 0.0
    limit = effective_limit(float(scene["maxFraction"]), self_diff)
    results = []
    for b_path, a_path in zip(base, actual):
        b_img = read_rgb(b_path)
        a_img = downscale2(read_rgb(a_path))
        if a_img.shape != b_img.shape:
            results.append((False, f"FAIL {name} {a_path.name} - size mismatch: baseline {b_img.shape[1]}x{b_img.shape[0]} "
                                   f"vs actual (downscaled) {a_img.shape[1]}x{a_img.shape[0]}"))
            continue
        ignore = ignore_mask(a_img.shape[:2], scene["ignore"])
        differing = diff_mask(a_img, b_img, float(scene["pixelThreshold"]), ignore)
        counted = int((~ignore).sum())
        fraction = float(differing.sum()) / counted if counted else 0.0
        write_diff(a_img, differing, out_root / name / f"{a_path.stem}-diff.png")
        ok = fraction <= limit
        results.append((ok, f"{'PASS' if ok else 'FAIL'} {name} {a_path.name} {fraction:.6f} {limit:.6f}"))
    return results


def record(dir_a: Path, dir_b: Path, scene: dict, baselines: Path = BASELINES) -> float:
    a, b = list_pngs(dir_a), list_pngs(dir_b)
    if not a:
        raise SystemExit(f"no PNGs in {dir_a}")
    if [p.name for p in a] != [p.name for p in b]:
        raise SystemExit(f"the two runs wrote different steps: {[p.name for p in a]} vs {[p.name for p in b]}")
    target = baselines / scene["name"]
    target.mkdir(parents=True, exist_ok=True)
    for old in target.glob("*.png"):
        old.unlink()
    worst = 0.0
    for a_path, b_path in zip(a, b):
        a_img, b_img = downscale2(read_rgb(a_path)), downscale2(read_rgb(b_path))
        if a_img.shape != b_img.shape:
            raise SystemExit(f"{a_path.name}: the two runs differ in size")
        ignore = ignore_mask(a_img.shape[:2], scene["ignore"])
        worst = max(worst, diff_fraction(a_img, b_img, float(scene["pixelThreshold"]), ignore))
        Image.fromarray((a_img * 255.0 + 0.5).astype(np.uint8)).save(target / a_path.name)
    (target / "noise.json").write_text(json.dumps({"selfDiffFraction": worst}) + "\n", encoding="utf-8")
    return worst


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
        run_a, run_b = root / "a", root / "b"
        _save(run_a / "s1.png", synthetic_frame())
        _save(run_b / "s1.png", synthetic_frame())
        record(run_a, run_b, scene, root / "base")
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
    p.add_argument("actual_dir_a", type=Path)
    p.add_argument("actual_dir_b", type=Path)
    p.add_argument("scene")
    p.add_argument("--baselines", type=Path, default=BASELINES)
    sub.add_parser("selftest")
    args = parser.parse_args(argv)

    if args.command == "selftest":
        return selftest()
    scene = load_scene(args.scene)
    if args.command == "record":
        noise = record(args.actual_dir_a, args.actual_dir_b, scene, args.baselines)
        print(f"recorded {scene['name']} selfDiffFraction={noise:.6f}")
        return 0
    results = compare(args.actual_dir, scene, args.baselines, args.out)
    for _, line in results:
        print(line)
    return 0 if all(ok for ok, _ in results) else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
