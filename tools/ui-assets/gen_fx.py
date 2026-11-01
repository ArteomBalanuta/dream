"""Combat text masks for dream/ui/fx/: digit bitmaps and the thin Miss label.

Two-channel masks, tinted at runtime: R fill, G rim plus a 1-texel down-right shadow, B 0, A = R+G.
Spec 05, Glyph plan.
"""
from __future__ import annotations

import sys
from pathlib import Path

import numpy as np
from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parent))
import uiart  # noqa: E402
from uiart import Atlas, Sprite, bezier, ellipse_mask, stroke_mask, token  # noqa: E402

CELL_W, CELL_H = 12, 16
FILL_X, FILL_Y = 1, 2  # fill box offset inside the cell: 9x11 fill, rim, then the shadow
ADVANCE = 11
UPSCALE = 4
MISS_CELLS = {"M": 52, "i": 20, "s": 28}
MISS_H = 44
MISS_WORD = "Miss"

# Our own face: 3-texel stems, 2-texel bars, clipped corners, flat-sided counters.
GLYPHS = {
    "0": """
.#######.
#########
###...###
###...###
###...###
###...###
###...###
###...###
###...###
#########
.#######.""",
    "1": """
...####..
.######..
.######..
...####..
...####..
...####..
...####..
...####..
...####..
...####..
...####..""",
    "2": """
.#######.
#########
###...###
......###
.....####
...#####.
.#####...
####.....
###......
#########
#########""",
    "3": """
.#######.
#########
###...###
......###
...#####.
...#####.
......###
###...###
###...###
#########
.#######.""",
    "4": """
.....####
....#####
...######
..###.###
.###..###
###...###
#########
#########
......###
......###
......###""",
    "5": """
#########
#########
###......
###......
########.
#########
......###
###...###
###...###
#########
.#######.""",
    "6": """
.#######.
#########
###...###
###......
########.
#########
###...###
###...###
###...###
#########
.#######.""",
    "7": """
#########
#########
###...###
.....####
....####.
...####..
...###...
..####...
..###....
..###....
..###....""",
    "8": """
.#######.
#########
###...###
###...###
.#######.
.#######.
###...###
###...###
###...###
#########
.#######.""",
    "9": """
.#######.
#########
###...###
###...###
###...###
#########
.########
......###
###...###
#########
.#######.""",
    "plus": """
.........
.........
...###...
...###...
#########
#########
#########
...###...
...###...
.........
.........""",
    "comma": """
.........
.........
.........
.........
.........
.........
.........
...####..
...####..
....###..
...###...""",
}


def fill_of(key: str) -> np.ndarray:
    rows = GLYPHS[key].strip().split("\n")
    if len(rows) != 11 or any(len(r) != 9 for r in rows):
        raise ValueError(f"bad bitmap {key}")
    cell = np.zeros((CELL_H, CELL_W), dtype=bool)
    cell[FILL_Y : FILL_Y + 11, FILL_X : FILL_X + 9] = np.array([[c == "#" for c in r] for r in rows])
    return cell


def shifted(m: np.ndarray, dx: int, dy: int) -> np.ndarray:
    out = np.zeros_like(m)
    h, w = m.shape
    out[max(dy, 0) : h + min(dy, 0), max(dx, 0) : w + min(dx, 0)] = m[max(-dy, 0) : h + min(-dy, 0), max(-dx, 0) : w + min(-dx, 0)]
    return out


def digit_cell(key: str) -> np.ndarray:
    fill = fill_of(key)
    edge = np.zeros_like(fill)
    for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
        edge |= shifted(fill, dx, dy)
    corner = np.zeros_like(fill)
    for dx, dy in ((1, 1), (1, -1), (-1, 1), (-1, -1)):
        corner |= shifted(fill, dx, dy)
    rim = edge | corner
    shadow = shifted(rim | fill, 1, 1)
    g = np.zeros(fill.shape, dtype=np.uint8)
    g[corner & ~edge] = 170  # the 1 soft texel: outer corners only, so bars stay solid
    g[edge] = 255
    g[shadow & ~rim] = 255
    g[fill] = 0
    r = np.where(fill, 255, 0).astype(np.uint8)
    return np.dstack((r, g, np.zeros_like(r), r + g))


def upscale(cell: np.ndarray) -> np.ndarray:
    return cell.repeat(UPSCALE, axis=0).repeat(UPSCALE, axis=1)


def _word_mask(w_u: float, h_u: float, strokes) -> np.ndarray:
    m = np.zeros((int(h_u * uiart.PPU), int(w_u * uiart.PPU)), dtype=np.float32)
    for s in strokes:
        m = np.maximum(m, s)
    return m


def miss_glyph(ch: str) -> np.ndarray:
    """A thin hand-built sans letter, as uint8 mask cell: R coverage, G soft shadow."""
    w_px = MISS_CELLS[ch]
    w, h = w_px / 4, MISS_H / 4
    stem = 1.45
    top, base = 1.6, 9.2
    if ch == "M":
        strokes = [stroke_mask(w, h, [(2, base), (2.2, top), (6.5, base - 1), (10.8, top), (11, base)], stem)]
    elif ch == "i":
        strokes = [stroke_mask(w, h, [(2.5, 3.9), (2.5, base)], stem), ellipse_mask(w, h, 2.5, 1.9, 0.95, 0.95)]
    else:
        top_s = bezier((6.0, 4.4), (5.2, 2.9), (1.6, 2.7), (1.5, 4.5), 16)
        mid = bezier((1.5, 4.5), (1.5, 6.0), (6.1, 5.5), (6.1, 7.4), 16)
        low = bezier((6.1, 7.4), (6.1, 9.6), (1.4, 9.6), (0.9, 7.8), 16)
        strokes = [stroke_mask(w, h, top_s + mid[1:] + low[1:], stem * 0.95)]
    m = _word_mask(w, h, strokes)
    rgba = np.dstack((m, m, m, m)).astype(np.float32)
    cov = uiart.downsample(rgba)[..., 3].astype(np.float32) / 255
    # soft shadow a texel down-right, kept under the fill so R+G never exceeds 1
    sh = np.roll(cov, (2, 2), axis=(0, 1))
    blur = sum(np.roll(sh, (dy, dx), axis=(0, 1)) for dy in (-1, 0, 1) for dx in (-1, 0, 1)) / 9
    g = np.minimum(blur * 0.8, 1 - cov)
    r8, g8 = (cov * 255 + 0.5).astype(np.uint8), (g * 255).astype(np.uint8)
    return np.dstack((r8, g8, np.zeros_like(r8), r8 + g8))


def main(out_root: Path) -> None:
    out = Path(out_root) / "fx"
    meta = {"channels": "R fill, G rim and shadow, B 0, A R+G", "advance": ADVANCE}
    small = Atlas("dmg_digits", out, extrude=1, kind="mask", scale=1, meta=meta)
    large = Atlas("dmg_digits_x4", out, extrude=UPSCALE, kind="mask", scale=UPSCALE, meta=dict(meta, advancePx=ADVANCE * UPSCALE))
    for key in GLYPHS:
        cell = digit_cell(key)
        small.add(Sprite(f"dmg.{key}", cell))
        large.add(Sprite(f"dmg.{key}", upscale(cell)))
    letters = {ch: miss_glyph(ch) for ch in MISS_CELLS}
    for ch, img in letters.items():
        large.add(Sprite(f"dmg.miss.{ch}", img, meta={"advancePx": MISS_CELLS[ch]}))
    large.add(Sprite("dmg.miss", np.hstack([letters[ch] for ch in MISS_WORD])))
    small.save()
    large.save()


# previews -----------------------------------------------------------------------------

STYLES = [
    ("normal", "dmg.gold.fill", "dmg.gold.rim"),
    ("critical", "dmg.blue.fill", "dmg.blue.rim"),
    ("excellent", "dmg.green.fill", "dmg.green.rim"),
    ("ignore", "dmg.ignore", "dmg.ignore.rim"),
    ("orange", "dmg.orange.fill", "dmg.gold.rim"),
    ("incoming", "#FF0000", "#400000"),
    ("reflect", "#800080", "#200020"),
    ("absorbed", "#8B8000", "#232000"),
    ("poison", "#556B2F", "#151B0C"),
]


def tinted(cell: np.ndarray, fill: np.ndarray, rim: np.ndarray) -> Image.Image:
    c = cell.astype(np.float32) / 255
    rgb = c[..., 0:1] * fill + c[..., 1:2] * rim
    a = np.clip(c[..., 0] + c[..., 1], 0, 1)
    straight = rgb / np.maximum(a[..., None], 1e-5)
    return Image.fromarray((np.dstack((np.clip(straight, 0, 1), a)) * 255 + 0.5).astype(np.uint8), "RGBA")


def preview(out_root: Path, previews: Path) -> None:
    """Every type colour at spawn and floor size on stone and on a white-hot blast."""
    js = Path(out_root) / "fx" / "dmg_digits_x4.json"
    keys = ["4", "4", "3", "0", "0", "comma", "1", "2", "5", "6", "7", "8", "9", "plus"]
    adv = ADVANCE * UPSCALE
    row_h, width = 76, adv * len(keys) + 330
    board = Image.new("RGBA", (width, row_h * (len(STYLES) + 1)), (36, 32, 30, 255))
    px = board.load()
    for y in range(board.height):
        for x in range(width // 2, width):
            px[x, y] = (255, 250, 236, 255)
    for i, (_, f, r) in enumerate(STYLES):
        fill, rim = token(f), token(r)
        x = 8
        for key in keys:
            g = tinted(uiart.sprite_pixels(js, f"dmg.{key}"), fill, rim)
            board.alpha_composite(g, (x, i * row_h + 6))
            x += adv
        x += 16
        for key in keys[:10]:
            g = tinted(uiart.sprite_pixels(js, f"dmg.{key}"), fill, rim)
            board.alpha_composite(g.resize((g.width * 3 // 8, g.height * 3 // 8), Image.BILINEAR), (x, i * row_h + 20))
            x += adv * 3 // 8
    miss = tinted(uiart.sprite_pixels(js, "dmg.miss"), token("dmg.miss.fill"), np.array([0, 0, 0], dtype=np.float32))
    y = len(STYLES) * row_h + 14
    board.alpha_composite(miss, (8, y))
    board.alpha_composite(miss, (width // 2 + 8, y))
    Path(previews).mkdir(parents=True, exist_ok=True)
    board.convert("RGB").save(Path(previews) / "fx_digits.png", compress_level=6)


if __name__ == "__main__":
    here = Path(__file__).resolve().parent
    root = Path(sys.argv[1]) if len(sys.argv) > 1 else here.parents[1] / "dream" / "ui"
    main(root)
    preview(root, here / "previews")
