"""Baked surface tiles: writes terrain/<name>.png, terrain_manifest.json and terrain_sheet.png.

  gen_terrain.py [out_root]                       everything under out_root/terrain (default: this directory)
  gen_terrain.py --only name[,name] [out_root]    only those tiles, never the manifest or the sheet

One seamless power-of-two tile per terrain material and per grass role. Each tile is a height field built in the forge
form bands (primary 8 u and up, secondary 2..6 u, tertiary under 1 u, the 1..2 u gap left empty), baked to maps with
wrap on both axes, aged, and lit by a dome only: no directional key and no cast shadow. The engine applies its own
ambient and N.L and the sun moves, so only view-independent response is baked in: albedo, occlusion, cavity, wear,
grime and dust. DREAM_ART_WORKERS sets the process count (default min(4, cpu)).

The generator writes under dream/ only when main() gets ship_root (regen_terrain.py does), placing <name>.png at
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
    style: str                   # relief builder: cobble, rock, clod, sod, gravel, path, mud, drift
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
    extra: str = ""              # sRGB hex of the overlay the style paints (leaves, puddle, moss, ash, straw); "" = none


# Names are the shipped file stems. Albedo is held low and desaturated: the engine multiplies these straight into
# linear lighting, so a mid-grey photographic tile reads as daylight on every surface. Daytime must stay readable,
# so the tiles sit near V 0.2, not black; the night look comes from the lighting, not from the albedo.
TILES: dict[str, Tile] = {
    "grass_top":    Tile("sod",    "#232d1a", "#46512f", "#151b10", 0.15, 0.6, 0.45, 0.25, 0.20, 0.50, 0, 0.04, 0.60, 3.3, "#4a3220"),
    "grass_side":   Tile("clod",   "#2b2118", "#40352a", "#1c2315", 0.20, 0.5, 0.40, 0.18, 0.15, 0.55, 0, 0.06, 0.50, 3.2),
    "grass_bottom": Tile("clod",   "#221b15", "#352d25", "#14110e", 0.00, 0.5, 0.35, 0.18, 0.15, 0.55, 0, 0.04, 0.55, 3.8),
    "dirt":         Tile("clod",   "#352a20", "#54483c", "#1f1812", 0.00, 0.8, 0.50, 0.18, 0.15, 0.50, 0.02, 0.08, 0.55, 2.5, "#6b675f"),
    "stone":        Tile("cobble", "#3a3a38", "#5c5b56", "#120f0c", 0.00, 1.0, 0.50, 0.12, 0.15, 0.55, 0, 0.10, 0.40, 2.0, "#2c3a1f"),
    "sand":         Tile("gravel", "#3a342c", "#625a4e", "#262019", 0.00, 0.7, 0.35, 0.20, 0.15, 0.55, 0, 0.12, 0.50, 1.7, "#77746c"),
    "mud":          Tile("mud",    "#2a2119", "#3a2f25", "#17120e", 0.10, 0.8, 0.20, 0.12, 0.08, 0.60, 0.02, 0.03, 0.45, 3.8, "#0c0e10"),
    "snow":         Tile("drift",  "#555b61", "#7a8088", "#383e45", 0.25, 0.7, 0.25, 0.04, 0.25, 0.70, 0, 0.20, 0.30, 0.85),
    "rock":         Tile("rock",   "#2f2c29", "#524e47", "#171513", 0.00, 1.0, 0.60, 0.12, 0.25, 0.60, 0.02, 0.10, 0.40, 3.2),
    "grass_path":   Tile("path",   "#3a3125", "#5a4e3c", "#241d15", 0.30, 0.6, 0.35, 0.22, 0.20, 0.50, 0, 0.08, 0.50, 2.4, "#6a5c3a"),
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


def leaves(X, Y, name: str, cells: int, prob: float, l_lo: float, l_hi: float):
    """Scattered flat elliptical leaves, orientation hashed per leaf: 0..1 coverage, 0..1 height with a midrib, 0..1 hue."""
    g = _Grid(X, Y, name, cells, 0.85)
    shape = np.broadcast_shapes(X.shape, Y.shape)
    mask, hgt, hue = (np.zeros(shape, np.float32) for _ in range(3))
    for px, py, r in g.each():
        L = l_lo + (l_hi - l_lo) * r(3)
        W = L * (0.36 + 0.2 * r(4))
        th = r(5) * math.pi
        ct, st = np.cos(th), np.sin(th)
        dx, dy = X - px, Y - py
        a, b = dx * ct + dy * st, -dx * st + dy * ct
        d2 = (a / L) ** 2 + (b / W) ** 2
        has = r(6) < prob
        m = has & (d2 < 1.0)
        cov = F.smooth(1.0, 0.75, d2).astype(np.float32)
        h = (0.35 + 0.35 * (1 - d2) - 0.18 * np.exp(-(b / (0.14 * W)) ** 2)).astype(np.float32)
        mask = np.where(m, cov, mask)
        hgt = np.where(m, h, hgt)
        hue = np.where(m, r(7), hue)
    return mask, hgt, hue


def relief(style: str, X, Y, seed: str, per, t: Tile) -> dict:
    """Height pieces and masks for a style: prim (forms of 2 u and up), tert (under 1 u), gap (dark seams),
    hi (pale stones) and tint (multiplicative albedo field)."""
    one = np.ones(np.broadcast_shapes(X.shape, Y.shape), np.float32)
    out = {"gap": 0 * one, "hi": 0 * one, "tint": one, "ex": 0 * one, "ex_k": one}
    roll = _field(X, Y, seed + ".p", per, 8, 8.0, 30.0)
    pit = F.cell_dents(X, Y, seed + ".pit", 0.55, 0.9, period=per)
    if style == "cobble":
        # rounded setts (fine jittered Voronoi) mixed with a few larger flagstones, deep joints filled with dark mud
        wx = 0.3 * _field(X, Y, seed + ".wx", per, 6, 3.0, 6.0)
        wy = 0.3 * _field(X, Y, seed + ".wy", per, 6, 3.0, 6.0)
        f1, f2, dx, dy, h = voronoi(X + wx, Y + wy, seed + ".v", 12, 0.55)
        e = (f2 - f1) * 0.5
        B1, B2, bdx, bdy, bh = voronoi(X + wx, Y + wy, seed + ".big", 5, 0.7)
        eb = (B2 - B1) * 0.5
        flagged = bh[0] < 0.26
        dome = np.sqrt(F.smooth(0.0, 1.5, e))
        slab = F.smooth(0.0, 1.0, eb)
        lvl_s = (h[0] - 0.5) * 0.5 + (h[1] - 0.5) * 0.05 * dx + (h[2] - 0.5) * 0.05 * dy
        lvl_f = (bh[1] - 0.5) * 0.4 + (bh[2] - 0.5) * 0.02 * bdx
        sett_h = dome * (0.8 + lvl_s) - 1.0
        flag_h = slab * (0.9 + lvl_f) - 1.0 + 0.15 * dome
        hh = np.where(flagged, flag_h, sett_h)
        hh = np.minimum(hh, slab * 1.2 - 0.3)
        joint = np.where(flagged, 1 - F.smooth(0.12, 0.7, eb), 1 - F.smooth(0.10, 0.6, e))
        joint = np.maximum(joint, 1 - F.smooth(0.1, 0.55, eb))
        sec = 0.4 * F.cell_dents(X, Y, seed + ".dent", 3.0, period=per) + 0.4 * F.facet_plane(X, Y, seed + ".facet", 5.0, 0.12, period=per)
        out["prim"] = t.a1 * hh + t.a2 * sec * (1 - joint) + 0.25 * t.a1 * roll
        out["tert"] = t.a3 * pit * (1 - joint)
        out["gap"] = joint
        out["tint"] = 0.7 + 0.6 * np.where(flagged, bh[0] * 2.0, h[0])
        mossf = 0.5 + 0.5 * np.tanh(_field(X, Y, seed + ".moss", per, 8, 4.0, 14.0) * 1.3)
        out["ex"] = (joint * F.smooth(0.45, 0.7, mossf) + 0.5 * (1 - F.smooth(0.0, 0.5, e)) * F.smooth(0.65, 0.85, mossf)).clip(0, 1).astype(np.float32)
        out["ex_k"] = 0.8 + 0.5 * mossf
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
        if t.extra:
            zone = 0.5 + 0.5 * np.tanh(_field(X, Y, seed + ".az", per, 8, 4.0, 14.0) * 1.4)
            fl = _field(X, Y, seed + ".ash", per, 8, 0.4, 1.3)
            out["ex"] = (F.smooth(0.95, 1.5, fl) * (0.3 + 0.7 * zone) * (1 - pm)).astype(np.float32)
            out["ex_k"] = 0.8 + 0.5 * zone
            out["tint"] = out["tint"] * (0.75 + 0.5 * zone)
    elif style == "sod":
        # moss cushions with blade ridges, and scattered dead leaves as the accent
        f1, f2, dx, dy, h = voronoi(X, Y, seed + ".cu", 12, 0.9)
        cush = F.smooth(3.0, 0.5, f1) * (0.45 + 0.55 * h[0])
        b = blades(X, Y, seed + ".bl", 20)
        lm, lh, lhue = leaves(X, Y, seed + ".lf", 9, 0.55, 1.3, 2.4)
        out["prim"] = t.a1 * (0.5 * roll + 1.1 * cush) + t.a2 * F.facet_plane(X, Y, seed + ".facet", 5.0, 0.2, period=per) + 0.5 * t.a2 * lh * lm
        out["tert"] = t.a3 * b * (1 - 0.7 * lm)
        out["gap"] = F.smooth(0.35, 0.0, cush) * 0.5
        out["tint"] = (0.45 + 0.9 * b) * (0.65 + 0.7 * cush)
        out["ex"] = lm
        out["ex_k"] = 0.6 + 0.8 * lhue
    elif style == "gravel":
        # dense pebbles of mixed radii over grit, with ash-grey flecks
        h1, m1 = pebbles(X, Y, seed + ".p1", 44, 0.85, 0.30, 0.62)
        h2, m2 = pebbles(X, Y, seed + ".p2", 22, 0.75, 0.55, 1.0)
        h3, m3 = pebbles(X, Y, seed + ".p3", 11, 0.55, 0.95, 1.7)
        cl = 0.5 + 0.5 * np.tanh(_field(X, Y, seed + ".cl", per, 8, 5.0, 16.0) * 1.2)   # coarse and fine zones
        peb = np.maximum(np.maximum(h1 * (1.0 - 0.6 * cl), h2), h3)
        out["prim"] = t.a1 * (0.35 * roll + 0.9 * peb) + 0.5 * t.a2 * F.cell_dents(X, Y, seed + ".dent", 3.5, period=per)
        out["tert"] = t.a3 * (pit + 0.6 * F.cell_dents(X, Y, seed + ".grit", 0.8, 0.9, period=per))
        out["hi"] = np.maximum(np.maximum(m1 * (1.0 - 0.6 * cl), m2), m3) * 0.9
        fl = _field(X, Y, seed + ".ash", per, 8, 0.4, 1.4)
        zone = 0.5 + 0.5 * np.tanh(_field(X, Y, seed + ".az", per, 8, 6.0, 20.0) * 1.3)
        out["ex"] = (F.smooth(0.9, 1.4, fl) * (0.4 + 0.6 * zone) * (1 - out["hi"])).astype(np.float32)
        out["ex_k"] = 0.9 + 0.4 * zone
        out["tint"] = 0.65 + 0.7 * zone
    elif style == "path":
        # packed compacted dirt: low swells, trampled smooth, small embedded stones, sparse dead grass; isotropic
        ph, pm = pebbles(X, Y, seed + ".peb", 14, 0.4, 0.4, 0.95)
        swell = _field(X, Y, seed + ".sw", per, 8, 6.0, 20.0)
        b = blades(X, Y, seed + ".bl", 28)
        zone = 0.5 + 0.5 * np.tanh(_field(X, Y, seed + ".gz", per, 8, 5.0, 18.0) * 1.6)
        ex = (F.smooth(0.55, 0.85, b) * F.smooth(0.62, 0.9, zone) * (1 - pm)).astype(np.float32)
        out["prim"] = t.a1 * (0.5 * roll + 0.35 * swell) + t.a2 * (0.8 * ph + 0.3 * F.cell_dents(X, Y, seed + ".dent", 3.5, period=per))
        out["tert"] = t.a3 * (pit + 0.5 * F.cell_dents(X, Y, seed + ".grit", 0.8, 0.9, period=per)) + 0.4 * t.a3 * b * ex
        out["hi"] = pm
        out["ex"] = ex
        out["ex_k"] = 0.7 + 0.6 * b
        out["tint"] = 0.6 + 0.8 * (0.5 + 0.5 * np.tanh(swell * 0.9))
    elif style == "mud":
        # wet mud: hollows below a height threshold are a flat near-black puddle, hairline cracks at the rims
        wx = _field(X, Y, seed + ".wx", per, 6, 4.0, 9.0)
        wy = _field(X, Y, seed + ".wy", per, 6, 4.0, 9.0)
        f1, f2, dx, dy, h = voronoi(X + 1.6 * wx, Y + 1.6 * wy, seed + ".v", 10, 0.9)
        e = (f2 - f1) * 0.5
        body = 0.8 * roll + 0.45 * _field(X, Y, seed + ".mid", per, 8, 5.0, 14.0)
        thr = float(np.quantile(body[MARGIN:-MARGIN, MARGIN:-MARGIN], 0.24))
        floor_ = np.maximum(body, thr)
        pud = F.smooth(thr + 0.25, thr - 0.03, body)
        rim = F.smooth(thr - 0.05, thr + 0.1, body) * F.smooth(thr + 0.7, thr + 0.2, body)
        crack = rim * (1 - F.smooth(0.0, 0.14, e)) * 0.7
        out["prim"] = t.a1 * floor_ - 0.5 * crack + 0.5 * t.a2 * F.cell_dents(X, Y, seed + ".dent", 3.5, period=per) * (1 - pud)
        out["tert"] = t.a3 * pit * (1 - pud)
        out["gap"] = crack
        out["ex"] = pud.astype(np.float32)
        out["ex_k"] = one
    else:  # drift
        sec = F.facet_plane(X, Y, seed + ".facet", 5.0, 0.30, period=per) + 0.8 * F.cell_dents(X, Y, seed + ".dent", 3.5, period=per)
        out["prim"] = t.a1 * roll + t.a2 * sec
        out["tert"] = t.a3 * pit
    return out


def render_hires(name: str):
    """Hi-res float RGBA of one tile (TILE_PX * SS square, sRGB-encoded, periodic) and its relief h (prim + tert)."""
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
    if t.extra:
        ex = r["ex"][..., None]
        albedo = albedo * (1 - ex) + lin(t.extra) * (r["ex_k"][..., None] * (0.8 + 0.4 * grain[..., None])) * ex
    ones = np.ones_like(h)
    s = F.Surface(albedo.astype(np.float32), np.full(albedo.shape, 0.03, np.float32), 0.85 * ones, 0.0 * ones, 0.0 * ones, ones)
    s.bare_albedo = np.broadcast_to(bare, albedo.shape).astype(np.float32)
    s.bare_f0 = np.full(albedo.shape, 0.04, np.float32)
    s.bare_rough = 0.8 * ones
    s.wear_gain = ones
    s = F.age(s, m, seed, t.wear, t.grime, t.rust, t.dust, origin=(-MARGIN / U.PPU, -MARGIN / U.PPU), period=per)
    hdr = F.light(s, m, DOME)
    rgba = F.finish(hdr, s.alpha, t.exposure, None, bloom=0.0, grade=False)
    sl = slice(MARGIN, MARGIN + n)
    return np.ascontiguousarray(rgba[sl, sl]), np.ascontiguousarray(h[sl, sl])


def render_tile(name: str) -> np.ndarray:
    """The wrapped downsample of render_hires: uint8 TILE_PX x TILE_PX x 4."""
    hi, h = render_hires(name)
    out = F.downsample(hi, wrap="xy")      # hi alpha is 1 here, so the colour is not premultiplied by height
    n = TILE_PX
    hb = h.astype(np.float64).reshape(n, U.SS, n, U.SS).mean(axis=(1, 3))
    lo, up = np.percentile(hb, 1), np.percentile(hb, 99)
    out[..., 3] = np.clip((hb - lo) / max(up - lo, 1e-9) * 255.0 + 0.5, 0, 255).astype(np.uint8)
    return out


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


def main(out_root: Path = HERE, only: list[str] | None = None, ship_root: Path | None = None, results: dict | None = None) -> None:
    """ship_root, when set, also writes each tile to ship_root/<n>/<n>.albedo.png (the engine's copy). results, when a
    dict, receives the in-memory tiles."""
    out = Path(out_root) / "terrain"
    if only:
        missing = [n for n in only if n not in TILES]
        if missing:
            raise SystemExit(f"unknown tiles: {missing}")
    names = [n for n in ORDER if not only or n in only]
    res = render_all(names)
    for n in names:
        U.save_png(res[n], out / f"{n}.png")
        if ship_root is not None:
            U.save_png(res[n], Path(ship_root) / n / f"{n}.albedo.png")
    if results is not None:
        results.update(res)
    if only:
        print("gen_terrain: wrote", ", ".join(names))
        return
    doc = {"version": 1, "tileSizePx": TILE_PX, "periodU": PERIOD_U, "straightAlpha": True, "alpha": "height", "wrap": "xy",
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
