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
RESERVED = range(18, 32)


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
        if not rgb.any():
            errs.append(f"frame {index}: no emission")
        if c[ring].max() > 2:  # no quad edge can show
            errs.append(f"frame {index}: not faded out at the cell edge")
        if index in (*SPARK, *EMBER, HALO, HALO_CORE) and a.any():
            errs.append(f"frame {index}: A must be 0 (additive)")
        if index in DUST and not 30 <= a.max() <= 128:
            errs.append(f"frame {index}: dust A max {a.max()} not low")
        if index in ASH:
            if a.max() < 90:
                errs.append(f"frame {index}: ash A max {a.max()} too faint")
            lum = rgb.sum(axis=2)[a > 0].mean() / (3 * a[a > 0].mean())
            if lum > 0.2:
                errs.append(f"frame {index}: ash RGB not dark ({lum:.2f})")
        if index in (*DUST, *ASH) and (rgb > a[..., None]).any():
            errs.append(f"frame {index}: RGB exceeds A (not premultiplied)")
        if index in SPARK and (rgb.max(axis=2) - rgb.min(axis=2)).max() > 26:
            errs.append(f"frame {index}: spark not near-neutral")
        if index in EMBER:
            tot = rgb.sum(axis=(0, 1))
            if not (tot[0] > 2 * tot[1] and tot[1] > tot[2]):
                errs.append(f"frame {index}: ember not dull red {tot}")
            if rgb[..., 0].max() > 235 or rgb[..., 2].max() > 100:
                errs.append(f"frame {index}: ember too hot or too white")
        if index in DUST:
            tot = rgb.sum(axis=(0, 1))
            if abs(int(tot[0]) - int(tot[2])) > 0.2 * tot.max():
                errs.append(f"frame {index}: dust not grey")
    for group in (SPARK, EMBER, DUST, ASH):
        cells = [cell(sheet, i) for i in group]
        for i, a_c in enumerate(cells):
            for b_c in cells[i + 1 :]:
                if np.array_equal(a_c, b_c):
                    errs.append(f"group starting {group[0]}: duplicate frames")
    halo, core = cell(sheet, HALO)[..., :3].astype(int), cell(sheet, HALO_CORE)[..., :3].astype(int)
    centre = slice(CELL // 2 - 2, CELL // 2 + 2)
    if halo[centre, centre].max() < 100 or core[centre, centre].max() < 230:
        errs.append("halo centres too dim")
    if not (core[..., 0] > 20).sum() < (halo[..., 0] > 20).sum():
        errs.append("halo core is not tighter than the halo")
    for name, h in (("halo", halo), ("halo.core", core)):
        if not (h[..., 0] == h[..., 1]).all() or not (h[..., 1] == h[..., 2]).all():
            errs.append(f"{name}: not neutral")
        row = h[CELL // 2, CELL // 2 :, 0]  # falloff to the right of the centre
        if (np.diff(row) > 1).any():
            errs.append(f"{name}: falloff not monotone")
    return errs


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
    expect = {"size": [512, 256], "columns": COLUMNS, "rows": ROWS, "cell": CELL, "margin": MARGIN, "yDown": True, "alpha": "premultiplied", "image": "particles.png"}
    errs += [f"json {k}: {doc.get(k)!r}" for k, v in expect.items() if doc.get(k) != v]
    indices = [f["index"] for f in doc.get("frames", [])]
    if indices != list(range(18)):
        errs.append(f"json frames {indices}")
    errs += generator_errors(TOOLS / "gen_particles.py")
    if not (TOOLS / "previews" / "particles.png").exists():
        errs.append("missing preview particles.png")
    errs += uiart.determinism_errors(gen_particles.main, ["particles"])
    for e in errs:
        print("FAIL", e)
    print(f"check_particles: {len(indices)} frames, {len(errs)} errors")
    return 1 if errs else 0


if __name__ == "__main__":
    sys.exit(main())
