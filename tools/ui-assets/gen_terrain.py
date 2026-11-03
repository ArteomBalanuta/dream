"""Baked surface tiles: writes terrain/<name>.png, terrain_manifest.json and terrain_sheet.png.

  gen_terrain.py [out_root]                       everything under out_root/terrain (default: this directory)
  gen_terrain.py --only name[,name] [out_root]    only those tiles, never the manifest or the sheet

One seamless power-of-two tile per terrain material and per grass role. Each tile is a height field built in the forge
form bands (primary 8 u and up, secondary 2..6 u, tertiary under 1 u, the 1..2 u gap left empty), baked to maps with
wrap on both axes, aged, and lit by a dome only: no directional key and no cast shadow. The engine applies its own
ambient and N.L and the sun moves, so only view-independent response is baked in: albedo, occlusion, cavity, wear,
grime and dust. DREAM_ART_WORKERS sets the process count (default min(4, cpu)).

The generator never writes under dream/. A separate copy step (see README) places <name>.png at
dream/animations/library/forge/<name>/<name>.albedo.png.
"""
from __future__ import annotations

import json
import math
import os
import sys
import zlib
from concurrent.futures import ProcessPoolExecutor
from dataclasses import dataclass
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
if str(HERE) not in sys.path:
    sys.path.insert(0, str(HERE))
import forge as F  # noqa: E402
import uiart as U  # noqa: E402

TILE_PX = 256                    # final tile edge, power of two
PERIOD_U = TILE_PX / U.SCALE     # 64 u: the tile repeat in forge units
DOME = F.Rig(lights=(), panels=(), ambient=(0.125, 0.125, 0.125))  # ambient x 8 is 1.0: albedo x occlusion, nothing else
NO_KEY = (0.0, 0.0, 1.0)         # straight up, so bake() skips the cast shadow entirely
# The tile is baked on a canvas MARGIN hi-res px larger on every side and cropped. The fields are periodic, so the
# margin holds the true neighbours. forge's wrap="xy" is not enough on its own: the wide occlusion pass resizes with a
# clamped edge and np.gradient is one-sided at the border, which seams the tile (measured: AO wrap-edge difference 3 to
# 5x an interior pair). The margin outreaches every filter in the chain (occlusion 104 px, age blur about 50 px).
MARGIN = 128
CHROMA = 0.55                    # palette chroma kept: the grim look is desaturated


@dataclass(frozen=True)
class Tile:
    style: str                   # relief builder: flag, rock, clod, grass, ripple, mud, drift
    base: str                    # sRGB hex, the body of the surface
    bare: str                    # what wear, convex edges and pebbles expose
    accent: str                  # patches and gaps: mortar, moss, ash
    accent_share: float          # 0..1 coverage of the accent patches
    a1: float                    # primary relief amplitude (u)
    a2: float                    # secondary relief (u)
    a3: float                    # tertiary pitting (u), under 1 u
    wear: float = 0.30
    grime: float = 0.50
    rust: float = 0.0
    dust: float = 0.10
    vary: float = 0.35           # albedo mottling strength
    exposure: float = 1.0


# Names are the shipped file stems. Albedo is held low and desaturated: the engine multiplies these straight into
# linear lighting, so a mid-grey photographic tile reads as daylight on every surface. Daytime must stay readable,
# so the tiles sit near V 0.2, not black; the night look comes from the lighting, not from the albedo.
TILES: dict[str, Tile] = {
    "grass_top":    Tile("grass",  "#2f3a24", "#56633e", "#1c2415", 0.15, 0.5, 0.35, 0.25, 0.20, 0.45, 0, 0.05, 0.55, 3.51),
    "grass_side":   Tile("clod",   "#34291f", "#54463a", "#242c1b", 0.20, 0.5, 0.40, 0.18, 0.15, 0.55, 0, 0.08, 0.5, 4.18),
    "grass_bottom": Tile("clod",   "#2a221b", "#443a30", "#1a1612", 0.00, 0.5, 0.35, 0.18, 0.15, 0.55, 0, 0.05, 0.55, 5.02),
    "dirt":         Tile("clod",   "#3d3126", "#64554a", "#271f18", 0.00, 0.5, 0.40, 0.18, 0.15, 0.50, 0.02, 0.10, 0.5, 3.33),
    "stone":        Tile("flag",   "#454543", "#72716b", "#1b1b1a", 0.00, 0.9, 0.50, 0.12, 0.15, 0.55, 0, 0.12, 0.25, 2.33),
    "sand":         Tile("ripple", "#52493e", "#76695a", "#3a342c", 0.08, 0.6, 0.30, 0.15, 0.15, 0.55, 0, 0.18, 0.45, 2.0),
    "mud":          Tile("mud",    "#31281f", "#4d4033", "#1b1612", 0.10, 0.8, 0.40, 0.18, 0.10, 0.60, 0.02, 0.04, 0.55, 5.02),
    "snow":         Tile("drift",  "#5d6369", "#858a90", "#3d434a", 0.25, 0.7, 0.25, 0.04, 0.25, 0.70, 0, 0.20, 0.30, 0.99),
    "rock":         Tile("rock",   "#3a3733", "#67625a", "#1f1d1b", 0.00, 1.0, 0.60, 0.12, 0.25, 0.60, 0.02, 0.10, 0.4, 4.0),
    "grass_path":   Tile("grass",  "#3a3d29", "#5d5440", "#2c2a1d", 0.30, 0.5, 0.35, 0.25, 0.20, 0.50, 0, 0.08, 0.55, 3.67),
}
ORDER = tuple(TILES)


def _field(X, Y, seed: str, per, n: int, lo: float, hi: float, stretch=(1.0, 1.0)) -> np.ndarray:
    return F.waves(X, Y, seed, n, lo, hi, stretch, period=per)


class _Grid:
    """Jittered cell grid with a whole number of cells per tile, so every lookup wraps exactly. Cell values come from
    the wrapped index and point positions from the unwrapped one, which keeps the margin coordinates periodic."""

    def __init__(self, X, Y, name: str, cells: int, jitter: float):
        self.seed = U.SEED ^ zlib.crc32(name.encode("utf-8"))
        self.n, self.c, self.j = cells, PERIOD_U / cells, jitter
        self.ci = np.floor(X / self.c).astype(np.int64)
        self.cj = np.floor(Y / self.c).astype(np.int64)

    def each(self):
        for dj in (-1, 0, 1):
            for di in (-1, 0, 1):
                ni, nj = self.ci + di, self.cj + dj
                wi, wj = ni % self.n, nj % self.n

                def r(salt, wi=wi, wj=wj):
                    return F._hash01(wi, wj, self.seed, salt).astype(np.float32)

                yield (ni + 0.5 + (r(1) - 0.5) * self.j) * self.c, (nj + 0.5 + (r(2) - 0.5) * self.j) * self.c, r


def voronoi(X, Y, name: str, cells: int, jitter: float = 0.8):
    """F1 and F2 distances (u), the offset (dx, dy) from the nearest cell point, and three cell hashes in 0..1."""
    g = _Grid(X, Y, name, cells, jitter)
    shape = np.broadcast_shapes(X.shape, Y.shape)
    f1 = np.full(shape, 1e9, np.float32)
    f2 = np.full(shape, 1e9, np.float32)
    dxn, dyn = np.zeros(shape, np.float32), np.zeros(shape, np.float32)
    h = [np.zeros(shape, np.float32) for _ in range(3)]
    for px, py, r in g.each():
        dx, dy = X - px, Y - py
        d = np.sqrt(dx * dx + dy * dy).astype(np.float32)
        closer = d < f1
        f2 = np.where(closer, f1, np.minimum(f2, d))
        dxn, dyn = np.where(closer, dx, dxn), np.where(closer, dy, dyn)
        for k, salt in enumerate((5, 6, 7)):
            h[k] = np.where(closer, r(salt), h[k])
        f1 = np.where(closer, d, f1)
    return f1, f2, dxn, dyn, h


def pebbles(X, Y, name: str, cells: int, prob: float, r_lo: float, r_hi: float):
    """Sparse round stones: 0..1 dome heights and a 0..1 coverage mask."""
    g = _Grid(X, Y, name, cells, 0.7)
    shape = np.broadcast_shapes(X.shape, Y.shape)
    hgt, mask = np.zeros(shape, np.float32), np.zeros(shape, np.float32)
    for px, py, r in g.each():
        rad = r_lo + (r_hi - r_lo) * r(3)
        d2 = ((X - px) ** 2 + (Y - py) ** 2) / (rad * rad)
        has = r(4) < prob
        hgt = np.maximum(hgt, (np.sqrt(np.clip(1 - d2, 0, 1)) * has * (0.6 + 0.4 * r(5))).astype(np.float32))
        mask = np.maximum(mask, ((d2 < 0.85) & has).astype(np.float32))
    return hgt, mask


def blades(X, Y, name: str, cells: int) -> np.ndarray:
    """Clumped grass: each cell owns a blade direction, ridges 0.8 u apart run along it. 0..1."""
    g = _Grid(X, Y, name, cells, 0.9)
    shape = np.broadcast_shapes(X.shape, Y.shape)
    num, den = np.zeros(shape, np.float32), np.full(shape, 1e-9, np.float32)
    k = 3.0 / (g.c * g.c)
    for px, py, r in g.each():
        dx, dy = X - px, Y - py
        th = r(3) * math.pi
        ct, st = np.cos(th), np.sin(th)
        perp = -dx * st + dy * ct
        along = dx * ct + dy * st
        ridge = 0.5 + 0.5 * np.cos(perp * (math.tau / 0.8) + r(4) * math.tau)
        taper = 0.55 + 0.45 * np.cos(along * (math.tau / 3.2) + r(5) * math.tau)
        w = np.exp(-k * (dx * dx + dy * dy)).astype(np.float32)
        num += w * (ridge ** 1.6 * taper).astype(np.float32)
        den += w
    return (num / den).astype(np.float32)


def relief(style: str, X, Y, seed: str, per, t: Tile) -> dict:
    """Height pieces and masks for a style: prim (forms of 2 u and up), tert (under 1 u), gap (dark seams),
    hi (pale stones) and tint (multiplicative albedo field)."""
    one = np.ones(np.broadcast_shapes(X.shape, Y.shape), np.float32)
    out = {"gap": 0 * one, "hi": 0 * one, "tint": one}
    roll = _field(X, Y, seed + ".p", per, 8, 8.0, 30.0)
    pit = F.cell_dents(X, Y, seed + ".pit", 0.55, 0.9, period=per)
    if style == "flag":
        # discrete slabs, each with its own level and tilt, mortar gaps and chipped edges
        wx = 0.25 * _field(X, Y, seed + ".wx", per, 6, 3.0, 6.0)
        wy = 0.25 * _field(X, Y, seed + ".wy", per, 6, 3.0, 6.0)
        f1, f2, dx, dy, h = voronoi(X + wx, Y + wy, seed + ".v", 6, 0.55)
        e = (f2 - f1) * 0.5 + 0.15 * _field(X, Y, seed + ".chip", per, 8, 2.0, 5.0)
        slab = F.smooth(0.2, 1.3, e)
        level = (h[0] - 0.5) * 0.8 + (h[1] - 0.5) * 0.06 * dx + (h[2] - 0.5) * 0.06 * dy
        sec = 0.35 * F.cell_dents(X, Y, seed + ".dent", 3.5, period=per) + 0.5 * F.facet_plane(X, Y, seed + ".facet", 5.0, 0.12, period=per)
        out["prim"] = t.a1 * (slab * (1 + level) - 1.0) + t.a2 * sec * slab + 0.15 * t.a1 * roll
        out["tert"] = t.a3 * pit * slab
        out["gap"] = 1 - F.smooth(0.15, 0.9, e)
        out["tint"] = 0.75 + 0.5 * h[0]
    elif style == "rock":
        # angular fragments: flat tilted planes with hard edges, cracks between them
        f1, f2, dx, dy, h = voronoi(X, Y, seed + ".v", 8, 0.9)
        e = (f2 - f1) * 0.5
        plane = (h[1] - 0.5) * 0.9 * dx + (h[2] - 0.5) * 0.9 * dy + (h[0] - 0.5) * 1.4
        g1, g2, gx, gy, gh = voronoi(X, Y, seed + ".v2", 16, 0.9)
        e2 = (g2 - g1) * 0.5
        plane2 = (gh[1] - 0.5) * 0.5 * gx + (gh[2] - 0.5) * 0.5 * gy
        crack = 1 - F.smooth(0.0, 0.45, e)
        out["prim"] = t.a1 * (plane + 0.5 * plane2) * 0.8 - t.a1 * 0.8 * crack - 0.3 * (1 - F.smooth(0.0, 0.3, e2)) + 0.1 * t.a1 * roll
        out["tert"] = t.a3 * pit
        out["gap"] = crack
        out["tint"] = 0.8 + 0.4 * h[0]
    elif style == "clod":
        # fine ragged clods and grain, with a few embedded pebbles
        wx = _field(X, Y, seed + ".wx", per, 6, 2.5, 6.0)
        wy = _field(X, Y, seed + ".wy", per, 6, 2.5, 6.0)
        f1, f2, dx, dy, h = voronoi(X + 0.8 * wx, Y + 0.8 * wy, seed + ".v", 22, 0.9)
        bump = F.smooth(1.5, 0.2, f1) * (0.5 + 0.5 * h[0])
        ph, pm = pebbles(X, Y, seed + ".peb", 10, 0.30, 0.5, 1.1)
        out["prim"] = t.a1 * roll * 0.5 + t.a2 * (bump + 1.2 * ph)
        out["tert"] = t.a3 * (pit + 0.5 * F.cell_dents(X, Y, seed + ".grit", 0.8, 0.9, period=per))
        out["hi"] = pm
        out["tint"] = 0.7 + 0.6 * h[1]
    elif style == "grass":
        b = blades(X, Y, seed + ".bl", 20)
        out["prim"] = t.a1 * roll + t.a2 * F.facet_plane(X, Y, seed + ".facet", 5.0, 0.2, period=per)
        out["tert"] = t.a3 * b
        out["tint"] = 0.6 + 0.8 * b
    elif style == "ripple":
        rip = _field(X, Y, seed + ".rip", per, 7, 5.0, 11.0, (0.35, 1.0))
        out["prim"] = t.a1 * (rip * 0.8 + 0.3 * roll) + 0.3 * t.a2 * F.cell_dents(X, Y, seed + ".dent", 3.5, period=per)
        out["tert"] = t.a3 * (pit + 0.5 * F.cell_dents(X, Y, seed + ".grit", 0.8, 0.9, period=per))
    elif style == "mud":
        wx = _field(X, Y, seed + ".wx", per, 6, 4.0, 9.0)
        wy = _field(X, Y, seed + ".wy", per, 6, 4.0, 9.0)
        f1, f2, dx, dy, h = voronoi(X + 1.6 * wx, Y + 1.6 * wy, seed + ".v", 8, 0.9)
        e = (f2 - f1) * 0.5
        crack = 0.6 * (1 - F.smooth(0.0, 0.3, e))
        out["prim"] = t.a1 * (0.8 * roll + 0.5 * F.smooth(0.0, 2.5, e)) - 0.5 * crack + 0.5 * t.a2 * F.cell_dents(X, Y, seed + ".dent", 3.5, period=per)
        out["tert"] = t.a3 * pit
        out["gap"] = crack
    else:  # drift
        sec = F.facet_plane(X, Y, seed + ".facet", 5.0, 0.30, period=per) + 0.8 * F.cell_dents(X, Y, seed + ".dent", 3.5, period=per)
        out["prim"] = t.a1 * roll + t.a2 * sec
        out["tert"] = t.a3 * pit
    return out


def render_hires(name: str) -> np.ndarray:
    """Hi-res float RGBA of one tile: (TILE_PX * SS) square, sRGB-encoded, periodic."""
    t = TILES[name]
    seed = "terrain." + name
    n = TILE_PX * U.SS
    xs = (np.arange(n + 2 * MARGIN, dtype=np.float32) - MARGIN + 0.5) / U.PPU
    X, Y = xs[None, :], xs[:, None]
    per = (PERIOD_U, PERIOD_U)
    r = relief(t.style, X, Y, seed, per, t)
    prim = r["prim"].astype(np.float32)
    h = prim + r["tert"]
    m = F.bake(h, prim, NO_KEY)

    def lin(hx):
        c = U.token(hx)
        g = float(0.2126 * c[0] + 0.7152 * c[1] + 0.0722 * c[2])
        return U.srgb_to_linear(g + (c - g) * CHROMA).astype(np.float32)

    base, bare, accent = lin(t.base), lin(t.bare), lin(t.accent)
    mott = 0.5 + 0.5 * np.tanh(_field(X, Y, seed + ".mott", per, 12, 5.0, 24.0) * 0.9)
    grain = 0.5 + 0.5 * np.tanh(_field(X, Y, seed + ".grain", per, 8, 0.45, 0.95) * 0.8)
    k = (1.0 - t.vary + 2.0 * t.vary * (0.85 * mott + 0.15 * grain)) * r["tint"]
    patch = 0.5 + 0.5 * np.tanh(_field(X, Y, seed + ".patch", per, 8, 9.0, 28.0) * 1.2)
    pm = F.smooth(1.0 - t.accent_share - 0.12, 1.0 - t.accent_share + 0.12, patch)
    pm = np.maximum(pm, r["gap"])
    albedo = (base * (1 - pm[..., None]) + accent * pm[..., None]) * k[..., None]
    albedo = albedo * (1 - r["hi"][..., None]) + bare * (0.9 + 0.2 * mott[..., None]) * r["hi"][..., None]
    ones = np.ones_like(h)
    s = F.Surface(albedo.astype(np.float32), np.full(albedo.shape, 0.03, np.float32), 0.85 * ones, 0.0 * ones, 0.0 * ones, ones)
    s.bare_albedo = np.broadcast_to(bare, albedo.shape).astype(np.float32)
    s.bare_f0 = np.full(albedo.shape, 0.04, np.float32)
    s.bare_rough = 0.8 * ones
    s.wear_gain = ones
    s = F.age(s, m, seed, t.wear, t.grime, t.rust, t.dust, origin=(-MARGIN / U.PPU, -MARGIN / U.PPU), period=per)
    hdr = F.light(s, m, DOME)
    rgba = F.finish(hdr, s.alpha, t.exposure, None, bloom=0.0, grade=False)
    return np.ascontiguousarray(rgba[MARGIN : MARGIN + n, MARGIN : MARGIN + n])


def render_tile(name: str) -> np.ndarray:
    """The wrapped downsample of render_hires: uint8 TILE_PX x TILE_PX x 4."""
    return F.downsample(render_hires(name), wrap="xy")


def _job(name: str) -> np.ndarray:
    return render_tile(name)


def render_all(names: list[str]) -> dict:
    workers = int(os.environ.get("DREAM_ART_WORKERS", "0")) or min(4, os.cpu_count() or 1)
    workers = max(1, min(workers, len(names)))
    if workers == 1:
        res = [_job(n) for n in names]
    else:
        with ProcessPoolExecutor(max_workers=workers) as pool:
            res = list(pool.map(_job, names))
    return dict(zip(names, res))


def compose_sheet(tiles: list[np.ndarray]) -> np.ndarray:
    """Two rows of tiles, each repeated 2x2 so a seam would show as a visible grid line."""
    cols = (len(tiles) + 1) // 2
    pad = 8
    cell = 2 * TILE_PX
    sheet = np.full((2 * cell + 3 * pad, cols * cell + (cols + 1) * pad, 3), 200, dtype=np.uint8)
    for i, t in enumerate(tiles):
        r, c = divmod(i, cols)
        y, x = pad + r * (cell + pad), pad + c * (cell + pad)
        sheet[y : y + cell, x : x + cell] = np.tile(t[..., :3], (2, 2, 1))
    return sheet


def main(out_root: Path = HERE, only: list[str] | None = None) -> None:
    out = Path(out_root) / "terrain"
    if only:
        missing = [n for n in only if n not in TILES]
        if missing:
            raise SystemExit(f"unknown tiles: {missing}")
    names = [n for n in ORDER if not only or n in only]
    res = render_all(names)
    for n in names:
        U.save_png(res[n], out / f"{n}.png")
    if only:
        print("gen_terrain: wrote", ", ".join(names))
        return
    doc = {"version": 1, "tileSizePx": TILE_PX, "periodU": PERIOD_U, "straightAlpha": True, "wrap": "xy",
           "colorSpace": "srgb-encoded-sampled-as-linear", "sheet": "terrain_sheet.png",
           "tiles": [{"name": n, "image": f"{n}.png", "size": [TILE_PX, TILE_PX]} for n in names]}
    (out / "terrain_manifest.json").write_text(json.dumps(doc, indent=1) + "\n", encoding="utf-8", newline="\n")
    sheet = compose_sheet([res[n] for n in names])
    U.save_png(np.dstack((sheet, np.full(sheet.shape[:2], 255, dtype=np.uint8))), out / "terrain_sheet.png")
    print("gen_terrain: wrote", len(names), "tiles")


if __name__ == "__main__":
    args = sys.argv[1:]
    only_names = None
    if args and args[0] == "--only":
        only_names = args[1].split(",")
        args = args[2:]
    main(Path(args[0]) if args else HERE, only_names)
