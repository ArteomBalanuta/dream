"""Look gates for the hybrid bake: measures a captured gameplay frame against the acceptance
criteria in docs/superpowers/specs/2026-11-03-hybrid-bake/00-acceptance.md.

    python tools/ui-assets/tests/check_look.py <frame.png> [--hud-rows N] [--actor x0,y0,x1,y1]
    python tools/ui-assets/tests/check_look.py --selftest

The gate that matters is A1: BOTH value tails must move. A frame can be darkened by any tone curve,
but darkening alone drives the bright tail down, not up. Requiring the near-black share and the bright
share to rise together is what distinguishes real local lighting from a grade. The numbers come from
measuring the target art and our own baseline frame; they are targets, not laws, and the spec records
where each came from.
"""
from __future__ import annotations

import sys
from pathlib import Path

import numpy as np
from PIL import Image

HERE = Path(__file__).resolve().parent
if str(HERE.parent) not in sys.path:
    sys.path.insert(0, str(HERE.parent))

import uiart as U  # noqa: E402

# A1: the bimodal value structure. Target art measured 0.592-0.705 near-black, 0.024-0.038 bright,
# mean value 0.123-0.159, mean saturation 0.311-0.371. Our baseline frame measured 0.043 / 0.014 /
# 0.252 / 0.445. These sit between the two, nearer the target, so passing means real movement.
BLACK_SHARE_MIN = 0.45     # share of world pixels with value < BLACK_V
BLACK_V = 0.10
BRIGHT_SHARE_MIN = 0.020   # share with value > BRIGHT_V; must RISE even as the frame darkens
BRIGHT_V = 0.60
MEAN_V_MAX = 0.18
MEAN_SAT_MAX = 0.38

# A3: the actor must separate from the ground it stands on. Baseline measured a margin of -0.002
# (actor p99 0.2625, ground p99 0.2647), i.e. none at all.
ACTOR_P99_MARGIN_MIN = 0.08

# A2's texture-detail floor lives with the terrain generator, since it measures a texture rather than
# a frame; this file gates the frame only.


def _value_sat(rgb: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    """HSV value and saturation of an (H,W,3) uint8 array, as floats in 0..1."""
    a = rgb.astype(np.float32) / 255.0
    mx = a.max(axis=2)
    mn = a.min(axis=2)
    sat = np.where(mx > 0.0, (mx - mn) / np.maximum(mx, 1e-6), 0.0)
    return mx, sat


def measure_frame(rgb: np.ndarray) -> dict:
    v, s = _value_sat(rgb)
    return {
        "mean_v": float(v.mean()),
        "median_v": float(np.median(v)),
        "p99_v": float(np.percentile(v, 99)),
        "black_share": float((v < BLACK_V).mean()),
        "bright_share": float((v > BRIGHT_V).mean()),
        "mean_sat": float(s.mean()),
    }


def check_a1(stats: dict, errs: list[str]) -> None:
    if stats["black_share"] < BLACK_SHARE_MIN:
        errs.append(f"A1 near-black share {stats['black_share']:.3f} < {BLACK_SHARE_MIN}")
    if stats["bright_share"] < BRIGHT_SHARE_MIN:
        errs.append(f"A1 bright share {stats['bright_share']:.3f} < {BRIGHT_SHARE_MIN}")
    if stats["mean_v"] > MEAN_V_MAX:
        errs.append(f"A1 mean value {stats['mean_v']:.3f} > {MEAN_V_MAX}")
    if stats["mean_sat"] > MEAN_SAT_MAX:
        errs.append(f"A1 mean saturation {stats['mean_sat']:.3f} > {MEAN_SAT_MAX}")


def check_a3(rgb: np.ndarray, actor: tuple[int, int, int, int], errs: list[str]) -> dict:
    """The actor crop against a ring of ground around it, both at p99 so a few lit pixels count."""
    x0, y0, x1, y1 = actor
    h, w = rgb.shape[:2]
    pad_x = max(8, (x1 - x0))
    pad_y = max(8, (y1 - y0) // 2)
    rx0, ry0 = max(0, x0 - pad_x), max(0, y0 - pad_y)
    rx1, ry1 = min(w, x1 + pad_x), min(h, y1 + pad_y)

    av, _ = _value_sat(rgb[y0:y1, x0:x1])
    ring = np.ones((ry1 - ry0, rx1 - rx0), dtype=bool)
    ring[y0 - ry0:y1 - ry0, x0 - rx0:x1 - rx0] = False
    gv, _ = _value_sat(rgb[ry0:ry1, rx0:rx1])

    actor_p99 = float(np.percentile(av, 99))
    ground_p99 = float(np.percentile(gv[ring], 99))
    margin = actor_p99 - ground_p99
    if margin < ACTOR_P99_MARGIN_MIN:
        errs.append(f"A3 actor p99 {actor_p99:.4f} - ground p99 {ground_p99:.4f} "
                    f"= {margin:+.4f} < {ACTOR_P99_MARGIN_MIN}")
    return {"actor_p99": actor_p99, "ground_p99": ground_p99, "margin": margin}


def _synthetic_failures() -> list[tuple[str, np.ndarray]]:
    """Each gate needs an image that must fail it, so a gate cannot silently stop measuring."""
    rng = U.rng("check_look.selftest")
    flat_mid = np.full((180, 320, 3), 64, dtype=np.uint8)                       # fails black+mean
    graded = (np.clip(flat_mid.astype(np.float32) * 0.18, 0, 255)).astype(np.uint8)
    graded[:2, :2] = 0                                                          # dark, no bright tail
    sat = np.zeros((180, 320, 3), dtype=np.uint8)
    sat[..., 0] = 200                                                           # pure red: oversaturated
    noisy = rng.integers(0, 40, (180, 320, 3), dtype=np.int64).astype(np.uint8)  # dark, no highlights
    return [("flat mid grey", flat_mid), ("graded dark, no light sources", graded),
            ("oversaturated", sat), ("dark noise, no highlights", noisy)]


def selftest() -> int:
    problems = 0
    for name, img in _synthetic_failures():
        errs: list[str] = []
        check_a1(measure_frame(img), errs)
        if not errs:
            print(f"  SELFTEST FAIL: '{name}' passed A1 but must fail it")
            problems += 1
        else:
            print(f"  ok  '{name}' fails A1: {errs[0]}")

    # A synthetic frame built to pass: mostly black, a bright warm pool, muted mid tones.
    good = np.zeros((180, 320, 3), dtype=np.uint8)
    good[120:150, 40:280] = (28, 26, 24)
    yy, xx = np.mgrid[0:180, 0:320]
    pool = np.exp(-(((xx - 90) / 46.0) ** 2 + ((yy - 130) / 30.0) ** 2))
    for c, k in enumerate((255, 150, 60)):
        good[..., c] = np.clip(good[..., c] + pool * k, 0, 255).astype(np.uint8)
    errs = []
    stats = measure_frame(good)
    check_a1(stats, errs)
    if errs:
        print(f"  SELFTEST FAIL: the known-good frame fails A1: {errs}")
        problems += 1
    else:
        print(f"  ok  known-good frame passes A1 "
              f"(black {stats['black_share']:.3f}, bright {stats['bright_share']:.3f}, "
              f"mean {stats['mean_v']:.3f}, sat {stats['mean_sat']:.3f})")

    print(f"selftest: {problems} problem(s)")
    return 1 if problems else 0


def main(argv: list[str]) -> int:
    if "--selftest" in argv:
        return selftest()
    if not argv:
        print(__doc__)
        return 2

    path = Path(argv[0])
    hud_rows = 0
    actor = None
    for i, a in enumerate(argv):
        if a == "--hud-rows":
            hud_rows = int(argv[i + 1])
        elif a == "--actor":
            actor = tuple(int(v) for v in argv[i + 1].split(","))

    if not path.exists():
        print(f"check_look: no such frame: {path}")
        return 2
    rgb = np.asarray(Image.open(path).convert("RGB"))
    world = rgb[:rgb.shape[0] - hud_rows] if hud_rows else rgb

    errs: list[str] = []
    stats = measure_frame(world)
    check_a1(stats, errs)
    print(f"frame {path.name}  world {world.shape[1]}x{world.shape[0]} (hud rows excluded: {hud_rows})")
    print(f"  mean v {stats['mean_v']:.3f}  median {stats['median_v']:.3f}  p99 {stats['p99_v']:.3f}")
    print(f"  near-black share {stats['black_share']:.3f} (need >= {BLACK_SHARE_MIN})")
    print(f"  bright share     {stats['bright_share']:.3f} (need >= {BRIGHT_SHARE_MIN})")
    print(f"  mean saturation  {stats['mean_sat']:.3f} (need <= {MEAN_SAT_MAX})")

    if actor:
        a3 = check_a3(rgb, actor, errs)
        print(f"  actor p99 {a3['actor_p99']:.4f} vs ground p99 {a3['ground_p99']:.4f} "
              f"= {a3['margin']:+.4f} (need >= {ACTOR_P99_MARGIN_MIN})")

    for e in errs:
        print(f"  FAIL {e}")
    print(f"look: {len(errs)} problem(s)")
    return 1 if errs else 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
