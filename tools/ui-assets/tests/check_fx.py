"""Checks dream/ui/fx/. Exits nonzero on failure."""
from __future__ import annotations

import ast
import json
import sys
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
TOOLS = HERE.parent
sys.path.insert(0, str(TOOLS))
import gen_fx  # noqa: E402
import uiart  # noqa: E402

FX = TOOLS.parents[1] / "dream" / "ui" / "fx"
BANNED = ("ImageFont", "urllib", "requests", "reference/", ".webp", "dream/fonts")
DIGITS = [f"dmg.{i}" for i in range(10)]
GLYPHS = DIGITS + ["dmg.plus", "dmg.comma"]
FILLS = ("dmg.gold.fill", "dmg.blue.fill", "dmg.green.fill", "dmg.ignore", "dmg.orange.fill", "#FF0000", "#800080", "#8B8000", "#556B2F")
MID_ROWS = range(5, 10)  # middle third of the 11-row fill: bars cross the exact centre row of 2, 3, 5, 6, 8, 9


def runs(row: np.ndarray) -> list[int]:
    out, n = [], 0
    for v in list(row) + [False]:
        if v:
            n += 1
        elif n:
            out.append(n)
            n = 0
    return out


def check_small(doc: dict, js: Path) -> list[str]:
    errs = []
    if doc["scale"] != 1 or doc["extrude"] != 1:
        errs.append(f"scale {doc['scale']} extrude {doc['extrude']}")
    if doc["meta"].get("advance") != 11:
        errs.append(f"advance {doc['meta'].get('advance')}")
    if sorted(doc["sprites"]) != sorted(GLYPHS):
        errs.append(f"names {sorted(doc['sprites'])}")
    cells = {}
    for name in GLYPHS:
        c = uiart.sprite_pixels(js, name)
        cells[name] = c
        if c.shape[:2] != (16, 12):
            errs.append(f"{name}: size {c.shape}")
            continue
        r, g, b, a = (c[..., i].astype(int) for i in range(4))
        if ((r > 0) & (g > 0)).any():
            errs.append(f"{name}: R and G overlap")
        if b.any():
            errs.append(f"{name}: B not 0")
        if (a != r + g).any():
            errs.append(f"{name}: A != R+G")
        if not set(np.unique(r)) <= {0, 255}:
            errs.append(f"{name}: fill not hard")
        if (r > 0).sum() == 0 or (g > 0).sum() == 0:
            errs.append(f"{name}: empty channel")
        ys, xs = np.where(r > 0)
        wide = xs.max() - xs.min() + 1
        if name in DIGITS and (ys.max() - ys.min() + 1 != 11 or (wide != 9 and name != "dmg.1") or wide > 9):
            errs.append(f"{name}: fill box")  # 1 is a plain stem, narrower than the 9-wide box
    names = list(cells)
    for i, a_n in enumerate(names):
        for b_n in names[i + 1 :]:
            if np.array_equal(cells[a_n], cells[b_n]):
                errs.append(f"{a_n} equals {b_n}")
    for name in DIGITS:
        if name in ("dmg.1", "dmg.7"):
            continue
        fill = cells[name][..., 0] > 0
        if not any(3 in runs(fill[y]) for y in MID_ROWS):
            errs.append(f"{name}: no 3-texel stem at mid-height")
    return errs


def check_large(doc: dict, js: Path, small_js: Path) -> list[str]:
    errs = []
    if doc["meta"].get("advance") != 11 or doc["meta"].get("advancePx") != 44:
        errs.append("x4 advance")
    for name in GLYPHS:
        big = uiart.sprite_pixels(js, name)
        want = uiart.sprite_pixels(small_js, name).repeat(4, axis=0).repeat(4, axis=1)
        if big.shape[:2] != (64, 48):
            errs.append(f"{name}: x4 size {big.shape}")
        elif not np.array_equal(big, want):
            errs.append(f"{name}: x4 is not a box upscale")
    miss = uiart.sprite_pixels(js, "dmg.miss")
    if miss.shape[:2] != (44, 128):
        errs.append(f"dmg.miss size {miss.shape}")
    parts = [uiart.sprite_pixels(js, f"dmg.miss.{c}") for c in "Miss"]
    if not np.array_equal(np.hstack(parts), miss):
        errs.append("dmg.miss is not M, i, s, s")
    if miss[..., 2].any():
        errs.append("dmg.miss: B not 0")
    if (miss[..., 3].astype(int) != miss[..., 0].astype(int) + miss[..., 1]).any():
        errs.append("dmg.miss: A != R+G")
    # thin stroke: about 3 px at 19 px height, so about 5-7 px here
    stem = runs(parts[1][22, :, 0] > 128)
    if not stem or not 4 <= max(stem) <= 8:
        errs.append(f"dmg.miss stem {stem}")
    return errs


def check_tint(js: Path) -> list[str]:
    errs = []
    for fill_name in FILLS:
        fill = (uiart.token(fill_name) * 255 + 0.5).astype(int)
        for name in GLYPHS:
            c = uiart.sprite_pixels(js, name).astype(np.float64) / 255
            rgb = c[..., 0:1] * uiart.token(fill_name) + c[..., 1:2] * uiart.token("dmg.gold.rim")
            px = (rgb[c[..., 0] == 1] * 255 + 0.5).astype(int)
            if (px != fill).any():
                errs.append(f"{name}: tinted fill differs for {fill_name}")
                break
    return errs


def generator_errors(path: Path) -> list[str]:
    text = path.read_text(encoding="utf-8")
    errs = [f"{path.name}: contains {b}" for b in BANNED if b in text]
    ok = {"__future__", "pathlib", "sys", "numpy", "PIL", "uiart"}
    for node in ast.walk(ast.parse(text)):
        mods = [a.name for a in node.names] if isinstance(node, ast.Import) else [node.module or ""] if isinstance(node, ast.ImportFrom) else []
        errs += [f"{path.name}: imports {m}" for m in mods if m.split(".")[0] not in ok]
    return errs


def main() -> int:
    small, large = FX / "dmg_digits.json", FX / "dmg_digits_x4.json"
    errs = uiart.validate_atlas(small) + uiart.validate_atlas(large)
    doc_s = json.loads(small.read_text(encoding="utf-8"))
    doc_l = json.loads(large.read_text(encoding="utf-8"))
    errs += check_small(doc_s, small)
    errs += check_large(doc_l, large, small)
    errs += check_tint(small)
    errs += generator_errors(TOOLS / "gen_fx.py")
    if not (TOOLS / "previews" / "fx_digits.png").exists():
        errs.append("missing preview fx_digits.png")
    errs += uiart.determinism_errors(gen_fx.main, ["fx"])
    for e in errs:
        print("FAIL", e)
    print(f"check_fx: {len(doc_s['sprites'])}+{len(doc_l['sprites'])} sprites, {len(errs)} errors")
    return 1 if errs else 0


if __name__ == "__main__":
    sys.exit(main())
