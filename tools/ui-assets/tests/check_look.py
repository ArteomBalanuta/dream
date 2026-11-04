"""Look gates for the hybrid bake: measures a captured gameplay frame against the acceptance
criteria in docs/superpowers/specs/2026-11-03-hybrid-bake/00-acceptance.md.

    python tools/ui-assets/tests/check_look.py <frame.png> [--hud-rows N] [--actor x0,y0,x1,y1]
    python tools/ui-assets/tests/check_look.py <frame.png> --actor-from-log <out.log> [--open-ground]
    python tools/ui-assets/tests/check_look.py --selftest

The gate that matters is A1: BOTH value tails must move. A frame can be darkened by any tone curve,
but darkening alone drives the bright tail down, not up. Requiring the near-black share and the bright
share to rise together is what distinguishes real local lighting from a grade. The numbers come from
measuring the target art and our own baseline frame; they are targets, not laws, and the spec records
where each came from.
"""
from __future__ import annotations

import re
import sys
import tempfile
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

# A1's HUD crop is not a judgement call any more. A1's bright share sits just above its floor, and it
# MOVES WITH THE CROP: the hybrid-bake round recorded 0.028 at --hud-rows 96 against 0.018 at
# --hud-rows 190 - on either side of BRIGHT_SHARE_MIN - so a number chosen per run decided whether A1
# passed. MEASURED here on a 1440x1024 night capture: 0.022 at 96 rows, 0.020 at 190, same direction.
#
# The default is therefore the literal bar rect and nothing else: UiLayout.BarHeightUnits (48) x the UI
# scale the captures run at (2.0) = 96 rows. A larger crop also eats the ornaments that rise above the
# bar, which are HUD art rather than world. Pass --hud-rows to declare a different one, as M8 requires.
HUD_ROWS_DEFAULT = 96

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


ACTOR_BOX_RE = re.compile(r"actorbox\s+(\d+),(\d+),(\d+),(\d+)")
ACTOR_BOX_NONE_RE = re.compile(r"actorbox\s+none:\s*(.*)")


def actor_from_log(log: Path, frame: str | None = None) -> tuple[int, int, int, int]:
    """The `actorbox x0,y0,x1,y1` the capture logged (PlayScene.Capture.cs, acceptance M7).

    The point of reading it is that the crop is then the renderer's own projection of the figure
    instead of a number typed by hand from a guess at where the figure is - a hand-typed crop is how a
    gate ends up measuring the wrong pixels. `actorbox none: <reason>` is a refusal to measure, not a
    box, so it raises rather than falling back to anything.

    A script usually has several `status` steps, so which box belongs to which frame matters. The
    capture logs the box of the frame it is about to write immediately above that frame's `wrote
    ...<path>` line, so naming the frame picks the last box before its write - the box of exactly the
    frame being measured, with no heuristic. Without a frame name, the last box in the log wins.

    Naming a frame the log does not contain RAISES. It used to fall back to the last box in the log -
    another step's pose, silently - which is the exact failure this function exists to prevent: a
    renamed PNG, a typo or a run that died before the write would have measured the wrong crop and
    said nothing. A missing frame is a loud failure.
    """
    lines = log.read_text(encoding="utf-8", errors="replace").splitlines()
    found: list[tuple[int, tuple[int, int, int, int] | str]] = []
    wrote = -1
    for n, line in enumerate(lines):
        box = ACTOR_BOX_RE.search(line)
        if box:
            found.append((n, tuple(int(v) for v in box.groups())))
            continue
        refusal = ACTOR_BOX_NONE_RE.search(line)
        if refusal:
            found.append((n, refusal.group(1).strip()))
        elif frame and "wrote " in line and frame in line:
            wrote = n

    if not found:
        raise ValueError(f"{log.name}: no 'actorbox' line - the script needs a `status` step at the pose")
    if frame and wrote < 0:
        raise ValueError(f"{log.name}: no 'wrote ...{frame}' line, so there is no box belonging to that frame "
                         f"(renamed PNG, a typo, or a run that died before the write). Refusing to measure "
                         f"another step's box; drop the frame name to take the last box in the log on purpose")
    before = [e for e in found if e[0] < wrote] if wrote >= 0 else []
    picked = before[-1] if before else found[-1]
    if isinstance(picked[1], str):
        raise ValueError(f"{log.name}:{picked[0] + 1}: the capture logged no actor box: {picked[1]}")
    x0, y0, x1, y1 = picked[1]
    if x1 - x0 < 2 or y1 - y0 < 2:
        raise ValueError(f"{log.name}:{picked[0] + 1}: the logged actorbox {x0},{y0},{x1},{y1} is empty")
    return x0, y0, x1, y1


def check_a3(rgb: np.ndarray, actor: tuple[int, int, int, int], errs: list[str], gated: bool = True) -> dict:
    """The actor against the ground immediately touching it: medians, and either sign counts.

    The collar is deliberately thin. Ground a figure's width away can be lit completely differently
    from the ground at its feet, and that difference belongs to the lighting, not to the figure.

    gated=False is the M2 open-ground report: the same measurement, no floor. DERIVED in the spec, the
    night actor gain is ~0.072, so open ground cannot reach ACTOR_SPREAD_MIN at any albedo; a floor
    there would be a gate the engine cannot pass. The number is still the deliverable.
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

    if gated and spread < ACTOR_SPREAD_MIN:
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

    # A3 needs the same treatment: a crop that must fail it and one that must pass, so the gate cannot
    # silently stop measuring internal spread. Both are drawn on identical ground, which is the point -
    # A3 is about the figure's own range, not about how it differs from what it stands on.
    box = (120, 40, 200, 140)
    flat_actor = np.full((180, 320, 3), 30, dtype=np.uint8)
    flat_actor[box[1]:box[3], box[0]:box[2]] = 70          # one even tone: no internal structure
    errs = []
    a3 = check_a3(flat_actor, box, errs)
    if not errs:
        print(f"  SELFTEST FAIL: a flat actor (spread {a3['spread']:.4f}) passed A3 but must fail it")
        problems += 1
    else:
        print(f"  ok  flat actor fails A3: spread {a3['spread']:.4f} < {ACTOR_SPREAD_MIN}")

    bimodal = np.full((180, 320, 3), 30, dtype=np.uint8)
    bimodal[box[1]:box[3], box[0]:box[2]] = 8              # deep undercuts
    bimodal[box[1]:box[3], box[0] + 20:box[0] + 60] = 230  # broad plates catching the light
    errs = []
    a3 = check_a3(bimodal, box, errs)
    if errs:
        print(f"  SELFTEST FAIL: a bimodal actor (spread {a3['spread']:.4f}) fails A3: {errs}")
        problems += 1
    else:
        print(f"  ok  bimodal actor passes A3: spread {a3['spread']:.4f} >= {ACTOR_SPREAD_MIN}")

    # And the same flat actor must NOT be reported as a failure in the open-ground mode, which has no floor.
    errs = []
    check_a3(flat_actor, box, errs, gated=False)
    if errs:
        print(f"  SELFTEST FAIL: the open-ground report gated A3: {errs}")
        problems += 1
    else:
        print("  ok  the open-ground report measures A3 without a floor")

    # The actorbox reader: a real line, a refusal, and a log with neither.
    with tempfile.TemporaryDirectory() as tmp:
        good_log = Path(tmp) / "out.log"
        good_log.write_text(
            "[hud-capture] actorbox 10,20,30,40 (viewport 1280x720)\n"
            "[hud-capture] actorbox 612,284,668,410 (viewport 1280x720)\n", encoding="utf-8")
        if actor_from_log(good_log) == (612, 284, 668, 410):
            print("  ok  --actor-from-log reads the last actorbox line")
        else:
            print("  SELFTEST FAIL: --actor-from-log read the wrong box")
            problems += 1
        # Named a frame, it takes the box logged for that frame, not the last one in the log.
        framed_log = Path(tmp) / "framed.log"
        framed_log.write_text(
            "[hud-capture] actorbox 10,20,30,40 (viewport 1280x720)\n"
            "[hud-capture] status keysnap begin: pos 5,1,5\n"
            "[hud-capture] actorbox 612,284,668,410 (viewport 1280x720)\n"
            "[hud-capture] wrote shots\\05-keysnap-a-120.png surface=Classic\n"
            "[hud-capture] actorbox 1,1,9,9 (viewport 1280x720)\n"
            "[hud-capture] wrote shots\\07-snap.png surface=Classic\n", encoding="utf-8")
        if actor_from_log(framed_log, "05-keysnap-a-120.png") == (612, 284, 668, 410):
            print("  ok  --actor-from-log picks the box of the named frame's step")
        else:
            print("  SELFTEST FAIL: --actor-from-log picked another step's box")
            problems += 1
        # A frame the log never wrote must RAISE, not quietly measure the last box in the log, which is
        # another step's pose. Same log, a name that is not in it.
        try:
            actor_from_log(framed_log, "05-keysnap-a-121.png")
        except ValueError as e:
            print(f"  ok  --actor-from-log refuses a frame the log never wrote: {e}")
        else:
            print("  SELFTEST FAIL: --actor-from-log fell back to another step's box for an unknown frame")
            problems += 1
        for name, body in (("a refusal", "[hud-capture] actorbox none: the player is behind the camera\n"),
                           ("no actorbox at all", "[hud-capture] status x: pos 1,2,3\n")):
            bad_log = Path(tmp) / "bad.log"
            bad_log.write_text(body, encoding="utf-8")
            try:
                actor_from_log(bad_log)
            except ValueError as e:
                print(f"  ok  --actor-from-log refuses {name}: {e}")
            else:
                print(f"  SELFTEST FAIL: --actor-from-log accepted {name}")
                problems += 1

    print(f"selftest: {problems} problem(s)")
    return 1 if problems else 0


def main(argv: list[str]) -> int:
    if "--selftest" in argv:
        return selftest()
    if not argv:
        print(__doc__)
        return 2

    path = Path(argv[0])
    hud_rows = HUD_ROWS_DEFAULT
    actor = None
    actor_source = "none"
    open_ground = "--open-ground" in argv
    for i, a in enumerate(argv):
        if a == "--hud-rows":
            hud_rows = int(argv[i + 1])
        elif a == "--actor":
            actor = tuple(int(v) for v in argv[i + 1].split(","))
            actor_source = "--actor, typed by hand"
        elif a == "--actor-from-log":
            log = Path(argv[i + 1])
            if not log.exists():
                print(f"check_look: no such capture log: {log}")
                return 2
            try:
                actor = actor_from_log(log, path.name)
            except ValueError as e:
                print(f"check_look: {e}")
                return 2
            actor_source = f"--actor-from-log {log.name}, the renderer's own projection"

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

    # A3 is reported as two labelled lines whatever this frame is, so the difference between the gated
    # criterion and the recorded one is in front of whoever reads the output. One run measures one
    # frame: the crop fills the line this frame belongs to and the other says it was not measured.
    lit_line = "not measured in this frame (pass the lit capture, 22:00 within 2.0 m of the brazier)"
    open_line = "not measured in this frame (pass the open-ground capture with --open-ground)"
    if actor:
        a3 = check_a3(rgb, actor, errs, gated=not open_ground)
        measured = (f"internal spread {a3['spread']:.4f}  crop {actor[0]},{actor[1]},{actor[2]},{actor[3]} "
                    f"({actor[2] - actor[0]}x{actor[3] - actor[1]} px from {actor_source})")
        if open_ground:
            open_line = measured
        else:
            lit_line = measured
    print(f"  A3 M1 lit, GATED (need >= {ACTOR_SPREAD_MIN}; target art 0.50-0.78): {lit_line}")
    print(f"  A3 M2 open ground, REPORTED with no floor (expect 0.06-0.20): {open_line}")
    if actor:
        print(f"  (informational) actor {a3['actor_v']:.4f} vs touching ground "
              f"{a3['ground_v']:.4f} = {a3['signed']:+.4f} - {a3['reads_as']}")

    for e in errs:
        print(f"  FAIL {e}")
    print(f"look: {len(errs)} problem(s)")
    return 1 if errs else 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
