"""Checks dream/ui/particles/. Exits nonzero on failure."""
from __future__ import annotations

import ast
import json
import sys
from pathlib import Path

import numpy as np
from PIL import Image

HERE = Path(__file__).resolve().parent
TOOLS = HERE.parent
sys.path.insert(0, str(TOOLS))
import gen_particles  # noqa: E402
import uiart  # noqa: E402

PARTICLES = TOOLS.parents[1] / "dream" / "ui" / "particles"
BANNED = ("ImageFont", "urllib", "requests", "reference/", ".webp", "dream/fonts")
COLUMNS, ROWS, CELL, MARGIN = 8, 4, 64, 4
SPARK, EMBER, DUST, ASH, HALO, HALO_CORE = range(0, 4), range(4, 8), range(8, 12), range(12, 16), 16, 17
FLAME, BLOB = range(18, 22), 22
ARC, BOLT, RING = 23, range(24, 26), 26
HIT = range(27, 31)  # flash, star, slash, splat: additive hit marks
TRAIL = 31  # the Cleave blade trail band
ADDITIVE = (*SPARK, *EMBER, *DUST, HALO, HALO_CORE, *BOLT, *HIT, TRAIL)
RESERVED = range(0)  # no cell is reserved
# Cap on the sheet size. Raised from 32 KB when the arc, bolt and ring cells were added: the sheet's
# dimensions, grid and GPU cost are unchanged, only four more cells carry pixels. The four hit marks fit
# under it by being coarsely levelled (HIT_STEP in gen_particles.py), so the cap did not move.
MAX_PNG_BYTES = 40 * 1024


def cell(sheet: np.ndarray, index: int) -> np.ndarray:
    x, y = (index % COLUMNS) * CELL, (index // COLUMNS) * CELL
    return sheet[y : y + CELL, x : x + CELL]


def check_frames(sheet: np.ndarray) -> list[str]:
    errs = []
    interior = np.zeros((CELL, CELL), dtype=bool)
    interior[MARGIN : CELL - MARGIN, MARGIN : CELL - MARGIN] = True
    ring = interior.copy()  # the outer 3 px of the drawn area must already be dark
    ring[MARGIN + 3 : CELL - MARGIN - 3, MARGIN + 3 : CELL - MARGIN - 3] = False
    for index in range(COLUMNS * ROWS):
        c = cell(sheet, index)
        if c[~interior].any():
            errs.append(f"frame {index}: margin not zero")
        if index in RESERVED:
            if c.any():
                errs.append(f"frame {index}: reserved cell not empty")
            continue
        rgb, a = c[..., :3].astype(int), c[..., 3].astype(int)
        if index != BLOB and not rgb.any():
            errs.append(f"frame {index}: no emission")
        if c[ring].max() > 2:  # no quad edge can show
            errs.append(f"frame {index}: not faded out at the cell edge")
        if index in ADDITIVE and a.any():
            errs.append(f"frame {index}: A must be 0 (additive)")
        if index in FLAME and rgb.max() > 245:
            errs.append(f"frame {index}: flame peak {rgb.max()} too hot")
        if index in FLAME:
            # coverage, not additive: peaks about 0.8 (204) in the core and RGB stays meaningful under it
            if not 190 <= a.max() <= 215:
                errs.append(f"frame {index}: flame A peak {a.max()} outside 190-215")
            if rgb.max() < 100:
                errs.append(f"frame {index}: flame emission peaks at {rgb.max()}")
            covered = a > 8
            if (rgb.max(axis=2)[covered] * 2 < a[covered]).any():
                errs.append(f"frame {index}: flame RGB under half its A (would read dark)")
            if (rgb.max(axis=2)[a == 0] > 8).any():
                errs.append(f"frame {index}: flame emission outside its coverage")
        if index in (ARC, RING):
            # coverage like the flame: A peaks about 0.8 (204), RGB stays meaningful under it, no peak above 245
            if not 190 <= a.max() <= 215:
                errs.append(f"frame {index}: coverage A peak {a.max()} outside 190-215")
            if not 100 <= rgb.max() <= 245:
                errs.append(f"frame {index}: coverage RGB peak {rgb.max()} outside 100-245")
            covered = a > 8
            if (rgb.max(axis=2)[covered] * 2 < a[covered]).any():
                errs.append(f"frame {index}: coverage RGB under half its A (would read dark)")
            if (rgb.max(axis=2)[a == 0] > 8).any():
                errs.append(f"frame {index}: emission outside its coverage")
        if index in HIT:
            if not 200 <= rgb.max() <= 245:
                errs.append(f"frame {index}: hit mark peak {rgb.max()} outside 200-245")
            if (rgb.max(axis=2) != rgb.min(axis=2)).any():
                errs.append(f"frame {index}: hit mark not neutral (the particle tint sets the colour)")
        if index == TRAIL:
            if not 230 <= rgb.max() <= 245:
                errs.append(f"frame {index}: trail peak {rgb.max()} outside 230-245")
            if (rgb.max(axis=2) != rgb.min(axis=2)).any():
                errs.append(f"frame {index}: trail not neutral")
        if index in BOLT and not 200 <= rgb.max() <= 245:
            errs.append(f"frame {index}: bolt core peak {rgb.max()} outside 200-245")
        if index == RING and rgb[CELL // 2, CELL // 2].max() > 8:
            errs.append(f"frame {index}: ring centre not dark")
        if index in DUST and not 190 <= rgb.max() <= 240:
            errs.append(f"frame {index}: dust RGB peak {rgb.max()} off target")
        if index == BLOB and (rgb.any() or a.max() < 200):
            errs.append(f"frame {index}: blob must be black with A peak >= 200")
        if index in ASH:
            if a.max() < 90:
                errs.append(f"frame {index}: ash A max {a.max()} too faint")
            lum = rgb.sum(axis=2)[a > 0].mean() / (3 * a[a > 0].mean())
            if not 0.2 <= lum <= 0.4:
                errs.append(f"frame {index}: ash RGB luminance {lum:.2f} outside 0.2-0.4")
        if index in ASH and (rgb > a[..., None]).any():
            errs.append(f"frame {index}: RGB exceeds A (not premultiplied)")
        if index in SPARK and (rgb.max(axis=2) - rgb.min(axis=2)).max() > 26:
            errs.append(f"frame {index}: spark not near-neutral")
        if index in EMBER:
            if (rgb.max(axis=2) - rgb.min(axis=2)).max() > 26:
                errs.append(f"frame {index}: ember not near-neutral")
            if rgb.max() < 200:
                errs.append(f"frame {index}: ember peak {rgb.max()} below 200")
        if index in DUST:
            tot = rgb.sum(axis=(0, 1))
            if abs(int(tot[0]) - int(tot[2])) > 0.2 * tot.max():
                errs.append(f"frame {index}: dust not grey")
    for group in (SPARK, EMBER, DUST, ASH, BOLT, HIT):
        cells = [cell(sheet, i) for i in group]
        for i, a_c in enumerate(cells):
            for b_c in cells[i + 1 :]:
                if np.array_equal(a_c, b_c):
                    errs.append(f"group starting {group[0]}: duplicate frames")
    halo, core = cell(sheet, HALO)[..., :3].astype(int), cell(sheet, HALO_CORE)[..., :3].astype(int)
    centre = slice(CELL // 2 - 2, CELL // 2 + 2)
    if halo[centre, centre].max() < 180 or core[centre, centre].max() < 230:
        errs.append("halo centres too dim")
    if not (core[..., 0] > 20).sum() < (halo[..., 0] > 20).sum():
        errs.append("halo core is not tighter than the halo")
    for name, h in (("halo", halo), ("halo.core", core)):
        if not (h[..., 0] == h[..., 1]).all() or not (h[..., 1] == h[..., 2]).all():
            errs.append(f"{name}: not neutral")
        peak = int(h.max())
        if peak >= 255 or (h[..., 0] >= peak - 3).sum() > 16:
            errs.append(f"{name}: plateau or clipped (peak {peak}, {(h[..., 0] >= peak - 3).sum()} texels near it)")
        row = h[CELL // 2, CELL // 2 :, 0]  # falloff to the right of the centre
        if (np.diff(row) > 1).any():
            errs.append(f"{name}: falloff not monotone")
    return errs


ARC_OUTER_PX, ARC_SPAN_DEG = 24.0, 120.0  # restated from FxArcGeometry (OuterRadiusFraction = 24 / 32)


def arc_measure(c: np.ndarray) -> tuple[float, float, float, float]:
    """(outer radius px, left tip deg, right tip deg, brightness ratio left/right) of the crescent cell.
    Angles are + toward the caster's right, which is the top of the image."""
    a = c[..., 3].astype(float)
    ys, xs = np.mgrid[0:CELL, 0:CELL]
    x, y = xs + 0.5 - CELL / 2, ys + 0.5 - CELL / 2
    r, theta = np.hypot(x, y), np.degrees(np.arctan2(-y, x))
    # outer radius: along the forward axis, where the alpha falls to half its value just inside the edge
    ahead = np.abs(theta) < 8
    radii = np.arange(14.0, 27.0, 0.05)
    prof = np.array([a[ahead & (np.abs(r - k) < 0.5)].mean() if (ahead & (np.abs(r - k) < 0.5)).any() else 0.0 for k in radii])
    peak = prof.max()
    outer = float(radii[np.nonzero(prof >= peak / 2)[0].max()])
    band = (r > ARC_OUTER_PX - 5) & (r < ARC_OUTER_PX - 0.5) & (a >= 0.15 * a.max())
    left, right = float(theta[band].min()), float(theta[band].max())
    lo = (r > ARC_OUTER_PX - 5) & (r < ARC_OUTER_PX - 0.5) & (theta < -ARC_SPAN_DEG / 2 + 12) & (theta > -ARC_SPAN_DEG / 2 + 2)
    hi = (r > ARC_OUTER_PX - 5) & (r < ARC_OUTER_PX - 0.5) & (theta > ARC_SPAN_DEG / 2 - 12) & (theta < ARC_SPAN_DEG / 2 - 2)
    ratio = float(a[lo].mean() / max(a[hi].mean(), 1e-6))
    return outer, left, right, ratio


def arc_geometry_errors(c: np.ndarray) -> list[str]:
    outer, left, right, ratio = arc_measure(c)
    errs = []
    if abs(outer - ARC_OUTER_PX) > 1.0:
        errs.append(f"arc outer radius {outer:.2f} px, want {ARC_OUTER_PX} +-1")
    if abs(right - ARC_SPAN_DEG / 2) > 6 or abs(left + ARC_SPAN_DEG / 2) > 6:
        errs.append(f"arc spans {left:.1f}..{right:.1f} deg, want +-{ARC_SPAN_DEG / 2} +-6")
    if not 0.1 <= ratio <= 0.7:
        errs.append(f"arc left tip is {ratio:.2f} of the right tip's brightness, want a dim-to-bright ramp (0.1-0.7)")
    return errs


def arc_self_test(sheet: np.ndarray) -> list[str]:
    """Synthetic negatives: the gate must fail on a shrunk, a narrowed, a mirrored and a flat crescent."""
    good = cell(sheet, ARC).copy()
    errs = []
    if arc_geometry_errors(good):
        return []  # the real cell is reported by check_frames' caller
    ys, xs = np.mgrid[0:CELL, 0:CELL]
    r = np.hypot(xs + 0.5 - CELL / 2, ys + 0.5 - CELL / 2)
    shrunk = good.copy()
    shrunk[r > ARC_OUTER_PX - 3] = 0
    narrow = good.copy()
    theta = np.degrees(np.arctan2(-(ys + 0.5 - CELL / 2), xs + 0.5 - CELL / 2))
    narrow[np.abs(theta) > 40] = 0
    mirrored = good[::-1].copy()
    flat = good.copy()
    flat[..., 3] = np.where(good[..., 3] > 0, 200, 0)
    for name, bad in (("shrunk", shrunk), ("narrowed", narrow), ("mirrored", mirrored), ("flat", flat)):
        if not arc_geometry_errors(bad):
            errs.append(f"arc gate self-test: a {name} crescent passed")
    return errs


TRAIL_PLATEAU_PX = 20.0  # restated from FxBladeTrailGeometry (CellPlateauFraction = 20 / 32)


def trail_errors(c: np.ndarray) -> list[str]:
    """The trail band: a flat peak along the centre row out to the plateau, nothing at 25 px, core row brightest."""
    errs = []
    lum = c[..., :3].max(axis=2).astype(float)
    centre = lum[CELL // 2 - 1 : CELL // 2 + 1].max(axis=0)  # the two rows straddling the cell centre
    peak = centre.max()
    xs = np.abs(np.arange(CELL) + 0.5 - CELL / 2)
    flat = centre[xs <= TRAIL_PLATEAU_PX - 1.0]
    if peak <= 0 or (np.abs(flat - peak) > 0.03 * peak).any():
        errs.append(f"trail plateau not flat within 3% of {peak:.0f} for |x| <= {TRAIL_PLATEAU_PX - 1.0} px")
    if (centre[xs >= 25.0] > 0.02 * max(lum.max(), 1.0)).any():
        errs.append("trail not faded to <= 2% of peak at |x| >= 25 px")
    core = lum[:, CELL // 2].max()
    if lum[:, CELL // 2].argmax() not in (CELL // 2 - 1, CELL // 2) or core < lum.max() - 0.5:
        errs.append("trail core row is not the brightest")
    return errs


def trail_self_test(sheet: np.ndarray) -> list[str]:
    """Synthetic negative: a band shortened to 14 px must fail the plateau gate."""
    good = cell(sheet, TRAIL).copy()
    if trail_errors(good):
        return []  # the real cell is reported by the caller
    ys, xs = np.mgrid[0:CELL, 0:CELL]
    short = good.copy()
    short[np.abs(xs + 0.5 - CELL / 2) > 14] = 0
    return [] if trail_errors(short) else ["trail gate self-test: a band shortened to 14 px passed"]


def generator_errors(path: Path) -> list[str]:
    text = path.read_text(encoding="utf-8")
    errs = [f"{path.name}: contains {b}" for b in BANNED if b in text]
    ok = {"__future__", "json", "pathlib", "sys", "numpy", "PIL", "uiart"}
    for node in ast.walk(ast.parse(text)):
        mods = [a.name for a in node.names] if isinstance(node, ast.Import) else [node.module or ""] if isinstance(node, ast.ImportFrom) else []
        errs += [f"{path.name}: imports {m}" for m in mods if m.split(".")[0] not in ok]
    return errs


def main() -> int:
    png, js = PARTICLES / "particles.png", PARTICLES / "particles.json"
    errs = []
    doc = json.loads(js.read_text(encoding="utf-8"))
    img = Image.open(png)
    if img.mode != "RGBA" or img.size != (COLUMNS * CELL, ROWS * CELL):
        errs.append(f"png is {img.mode} {img.size}")
    else:
        errs += check_frames(np.asarray(img))
        errs += arc_geometry_errors(cell(np.asarray(img), ARC))
        errs += arc_self_test(np.asarray(img))
        errs += trail_errors(cell(np.asarray(img), TRAIL))
        errs += trail_self_test(np.asarray(img))
    expect = {"size": [512, 256], "columns": COLUMNS, "rows": ROWS, "cell": CELL, "margin": MARGIN, "yDown": True, "alpha": "premultiplied", "image": "particles.png"}
    errs += [f"json {k}: {doc.get(k)!r}" for k, v in expect.items() if doc.get(k) != v]
    indices = [f["index"] for f in doc.get("frames", [])]
    if indices != list(range(32)):
        errs.append(f"json frames {indices}")
    if doc.get("reserved") != []:
        errs.append(f"json reserved {doc.get('reserved')}")
    if png.stat().st_size > MAX_PNG_BYTES:
        errs.append(f"png is {png.stat().st_size} bytes, over {MAX_PNG_BYTES}")
    errs += generator_errors(TOOLS / "gen_particles.py")
    if not (TOOLS / "previews" / "particles.png").exists():
        errs.append("missing preview particles.png")
    errs += uiart.determinism_errors(gen_particles.main, ["particles"])
    errs += uiart.shipped_errors(gen_particles.main, "particles", PARTICLES)
    for e in errs:
        print("FAIL", e)
    print(f"check_particles: {len(indices)} frames, {len(errs)} errors")
    return 1 if errs else 0


if __name__ == "__main__":
    sys.exit(main())
