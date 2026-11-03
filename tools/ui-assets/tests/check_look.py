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

# A3: the actor must separate from the ground it stands on.
#
# REWRITTEN 2026-11-03, because the first version measured the wrong thing. It compared the actor's
# p99 against the p99 of a ring of ground as wide as the actor, which is fine on evenly lit ground
# and useless once local light pools exist: the ring reaches into a brazier's core, so the gate reads
# the light gradient rather than the figure. MEASURED on the integrated night frame, the same actor
# scored -0.10, -0.24 and -0.60 as the ring grew from 20 to 60 to 140 px. A number that depends that
# strongly on how far away you look is not measuring legibility.
#
# The second attempt was ALSO wrong, and measuring the target art is what showed it. Using a thin
# collar and medians, the target's own figures score separations of 0.000 to 0.086, median 0.000 -
# most of them do not differ from the ground they touch at all. That is not quantisation: the frame
# has 82 distinct value levels with a median gap of 0.008, so a zero is a real zero. A gate the
# reference art fails is a bad gate, whichever way round the sign goes.
#
# MEASURED, and this is the real answer: the target's figures read through INTERNAL CONTRAST, not
# through their average differing from the ground's. A figure there spans p90-p10 of 0.50 to 0.78 -
# bright bone and steel against near-black recesses, inside the silhouette. Our character spans
# 0.278, about half. That is why ours reads as a flat shape and theirs reads as an armoured figure,
# and it is the thing worth gating.
#
# So A3 is now internal spread, with the collar separation kept as a reported number only. The floor
# sits below the target's observed figure range, which leaves room for a figure in shadow.
ACTOR_SPREAD_MIN = 0.45
ACTOR_COLLAR_PX = 8

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
    """The actor against the ground immediately touching it: medians, and either sign counts.

    The collar is deliberately thin. Ground a figure's width away can be lit completely differently
    from the ground at its feet, and that difference belongs to the lighting, not to the figure.
    """
    x0, y0, x1, y1 = actor
    h, w = rgb.shape[:2]
    c = ACTOR_COLLAR_PX
    rx0, ry0 = max(0, x0 - c), max(0, y0 - c)
    rx1, ry1 = min(w, x1 + c), min(h, y1 + c)

    av, _ = _value_sat(rgb[y0:y1, x0:x1])
    gv, _ = _value_sat(rgb[ry0:ry1, rx0:rx1])
    collar = np.ones(gv.shape, dtype=bool)
    collar[y0 - ry0:y1 - ry0, x0 - rx0:x1 - rx0] = False

    actor_v = float(np.median(av))
    ground_v = float(np.median(gv[collar]))
    signed = actor_v - ground_v
    spread = float(np.percentile(av, 90) - np.percentile(av, 10))

    if spread < ACTOR_SPREAD_MIN:
        errs.append(f"A3 actor internal spread {spread:.4f} < {ACTOR_SPREAD_MIN} "
                    f"(target art's figures: 0.50-0.78) - the figure is too flat to read as a figure")
    return {"actor_v": actor_v, "ground_v": ground_v, "signed": signed, "spread": spread,
            "separation": abs(signed),
            "reads_as": "lit figure on darker ground" if signed > 0 else "silhouette on lit ground"}


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
        print(f"  actor internal spread {a3['spread']:.4f} (need >= {ACTOR_SPREAD_MIN}; "
              f"target art 0.50-0.78)")
        print(f"  (informational) actor {a3['actor_v']:.4f} vs touching ground "
              f"{a3['ground_v']:.4f} = {a3['signed']:+.4f} - {a3['reads_as']}")

    for e in errs:
        print(f"  FAIL {e}")
    print(f"look: {len(errs)} problem(s)")
    return 1 if errs else 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
