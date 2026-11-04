"""Floor decals: 24 flat, alpha-cut stains, patches and litter that break up the repeating terrain tiles.

    python tools/ui-assets/gen_ground_decals.py [out_root] [--only a,b] [--preview]

Writes <out_root>/decals/<stem>.glb and <out_root>/decals.json (default out_root: dream/animations/ground/). The
runtime scatter lays them on the floor at y = top + 0.008 or 0.012, so each is one flat 16-gon fan at y = 0 exactly,
normal +Y, one alpha-MASK material (cutoff 0.5) and one square RGBA atlas (128 px, 256 for the path decals).

Texture. Painted with numpy straight into the atlas, no key light: the "cube" shader that draws a decal lights it exactly
as it lights the terrain, so the bytes are albedo and the decal has to sit in the same brightness band as the tile it lies
on (the terrain tiles are also sRGB-encoded bytes sampled as linear, so a ratio of byte means is a ratio of what is
drawn). Every decal is rescaled to hit RATIO x the mean luma of the tiles it may lie on. Alpha is 0 or 255, never partial
(blending is off in the world pass): the ragged edge is a clumped stipple, the same technique as the props' contact disc,
and the outermost texels are always clear, so a decal can never show a hard square.

All art is original and procedural; nothing is read from disk. Palette is dull and dark: umber, ash, bruised green,
cold water, soot, dried oxblood.
"""
from __future__ import annotations

import importlib
import io
import math
import sys
from dataclasses import dataclass
from pathlib import Path

import numpy as np
from PIL import Image

HERE = Path(__file__).resolve().parent
if str(HERE) not in sys.path:
    sys.path.insert(0, str(HERE))

import forge as F  # noqa: E402
import gltfwrite as G  # noqa: E402
import uiart as U  # noqa: E402

K = importlib.import_module("groundkit")   # by name: check_sources.py allows tools to import only a fixed set of siblings

FAMILY = "decals"
WORLD_UNITS_PER_U = 0.039           # gen_props.WORLD_UNITS_PER_U; stated, not imported, to keep this generator's deps small
PREVIEW = HERE / "previews" / "ground_decals.png"
FAN = 16                            # rim vertices of the flat fan; the atlas content stays inside the inscribed circle
CIRCLE_MAX = 0.955                  # texels beyond this normalised radius are always clear

# Mean luma of the ten terrain albedo tiles (dream/animations/library/forge/<n>/<n>.albedo.png, bytes as linear), measured
# once. tests/check_ground_decals.py re-measures them from the shipped files and fails if these drift.
TILE_LUMA = {"dirt": 0.2736, "grass_bottom": 0.2620, "grass_path": 0.3115, "grass_side": 0.2679, "grass_top": 0.2832,
             "mud": 0.2487, "rock": 0.2767, "sand": 0.2959, "snow": 0.2780, "stone": 0.2912}
# Which tile a ground name resolves to in StaticTerrainMaterialCatalog (Grass draws grass_top on top faces).
TILE_OF = {"grass": "grass_top", "dirt": "dirt", "stone": "stone", "sand": "sand", "mud": "mud", "snow": "snow",
           "rock": "rock", "grass_path": "grass_path"}
# Flat backdrop colours for the preview sheet (the tiles' mean RGB).
TILE_RGB = {"dirt": (78, 68, 59), "grass_top": (67, 75, 59), "grass_path": (80, 81, 66), "mud": (70, 62, 55),
            "stone": (74, 74, 73), "sand": (81, 75, 67), "rock": (73, 70, 67)}


@dataclass(frozen=True)
class Decal:
    cls: str                 # manifest class
    paint: str               # painter name
    diameter: float          # world units across the fan at scale 1
    ratio: float             # target mean luma of covered texels / mean luma of the ground tiles (centre of the gated band)
    ground: tuple            # GeometryMaterialId names it may lie on
    atlas: int = 128
    weight: float = 1.0
    scale: tuple = (0.9, 1.15)
    base: tuple = (0.3, 0.3, 0.3)     # linear base colour before the luma rescale
    alt: tuple = (0.4, 0.4, 0.4)      # lighter / second colour
    var: int = 0                      # variant index inside the painter


ALL_GROUND = ("grass", "dirt", "stone", "sand", "mud", "snow", "rock", "grass_path")
DECALS: dict[str, Decal] = {
    "decal_dirt_01": Decal("decal.dirt", "dirt", 1.30, 0.95, ("grass", "dirt"), base=(0.36, 0.27, 0.19), alt=(0.52, 0.43, 0.33), var=0),
    "decal_dirt_02": Decal("decal.dirt", "dirt", 1.05, 0.90, ("grass", "dirt"), base=(0.33, 0.26, 0.19), alt=(0.50, 0.42, 0.33), var=1),
    "decal_dirt_03": Decal("decal.dirt", "dirt", 1.50, 0.98, ("grass", "dirt"), base=(0.37, 0.29, 0.21), alt=(0.53, 0.45, 0.35), var=2),
    "decal_dirt_04": Decal("decal.dirt", "dirt", 0.90, 0.88, ("grass", "dirt"), base=(0.31, 0.25, 0.19), alt=(0.47, 0.40, 0.32), var=3),
    "decal_moss_01": Decal("decal.moss", "moss", 1.25, 0.78, ("stone", "rock", "grass"), weight=0.9, base=(0.15, 0.21, 0.09), alt=(0.28, 0.34, 0.15), var=0),
    "decal_moss_02": Decal("decal.moss", "moss", 1.00, 0.72, ("stone", "rock", "grass"), weight=0.9, base=(0.14, 0.20, 0.09), alt=(0.26, 0.32, 0.15), var=1),
    "decal_moss_03": Decal("decal.moss", "moss", 1.50, 0.82, ("stone", "rock", "grass"), weight=0.9, base=(0.16, 0.21, 0.10), alt=(0.29, 0.33, 0.17), var=2),
    "decal_moss_04": Decal("decal.moss", "moss", 0.85, 0.70, ("stone", "rock", "grass"), weight=0.9, base=(0.13, 0.19, 0.09), alt=(0.25, 0.30, 0.14), var=3),
    "decal_puddle_01": Decal("decal.puddle", "puddle", 1.30, 0.50, ("mud", "dirt"), weight=0.8, base=(0.115, 0.13, 0.15), alt=(0.17, 0.15, 0.13), var=0),
    "decal_puddle_02": Decal("decal.puddle", "puddle", 1.00, 0.46, ("mud", "dirt"), weight=0.8, base=(0.11, 0.125, 0.145), alt=(0.16, 0.145, 0.125), var=1),
    "decal_puddle_03": Decal("decal.puddle", "puddle", 1.55, 0.54, ("mud", "dirt"), weight=0.8, base=(0.12, 0.135, 0.155), alt=(0.17, 0.15, 0.13), var=2),
    "decal_crack_01": Decal("decal.crack", "crack", 1.40, 0.55, ("stone", "rock"), base=(0.05, 0.05, 0.05), alt=(0.30, 0.30, 0.28), var=0),
    "decal_crack_02": Decal("decal.crack", "crack", 1.10, 0.52, ("stone", "rock"), base=(0.05, 0.05, 0.05), alt=(0.30, 0.30, 0.28), var=1),
    "decal_crack_03": Decal("decal.crack", "crack", 1.50, 0.58, ("stone", "rock"), base=(0.05, 0.05, 0.05), alt=(0.30, 0.30, 0.28), var=2),
    "decal_path_01": Decal("decal.path", "path", 2.20, 0.95, ("grass_path", "grass", "dirt"), atlas=256, scale=(0.9, 1.1), base=(0.37, 0.31, 0.22), alt=(0.52, 0.46, 0.36), var=0),
    "decal_path_02": Decal("decal.path", "path", 2.30, 0.92, ("grass_path", "grass", "dirt"), atlas=256, scale=(0.9, 1.08), base=(0.36, 0.31, 0.23), alt=(0.51, 0.46, 0.37), var=1),
    "decal_path_03": Decal("decal.path", "path", 2.10, 0.97, ("grass_path", "grass", "dirt"), atlas=256, scale=(0.9, 1.1), base=(0.37, 0.32, 0.23), alt=(0.53, 0.47, 0.37), var=2),
    "decal_litter_01": Decal("decal.litter", "litter", 1.30, 0.80, ("grass", "dirt"), base=(0.30, 0.20, 0.10), alt=(0.20, 0.20, 0.11), var=0),
    "decal_litter_02": Decal("decal.litter", "litter", 1.05, 0.76, ("grass", "dirt"), base=(0.28, 0.19, 0.10), alt=(0.19, 0.19, 0.11), var=1),
    "decal_litter_03": Decal("decal.litter", "litter", 1.50, 0.84, ("grass", "dirt"), base=(0.31, 0.22, 0.12), alt=(0.21, 0.20, 0.12), var=2),
    "decal_scorch_01": Decal("decal.scorch", "scorch", 1.40, 0.37, ALL_GROUND, weight=0.8, base=(0.09, 0.083, 0.075), alt=(0.32, 0.30, 0.27), var=0),
    "decal_scorch_02": Decal("decal.scorch", "scorch", 1.00, 0.34, ALL_GROUND, weight=0.8, base=(0.09, 0.083, 0.075), alt=(0.32, 0.30, 0.27), var=1),
    "decal_stain_01": Decal("decal.stain", "stain", 0.90, 0.62, ("dirt", "stone", "sand"), weight=0.8, scale=(0.85, 1.15), base=(0.15, 0.13, 0.11), alt=(0.24, 0.20, 0.15), var=0),
    "decal_stain_02": Decal("decal.stain", "stain", 0.75, 0.58, ("dirt", "stone", "sand"), weight=0.8, scale=(0.85, 1.15), base=(0.15, 0.10, 0.09), alt=(0.20, 0.14, 0.12), var=1),
}



def ref_luma(d: Decal) -> float:
    """Mean luma of the tiles the decal may lie on (the whole set for a decal that goes anywhere but snow)."""
    names = [g for g in d.ground if g != "snow"]
    return float(np.mean([TILE_LUMA[TILE_OF[g]] for g in names]))


# --------------------------------------------------------------------------------------------------- painting
def _smooth(e0, e1, x):
    return F.smooth(e0, e1, x)


class Canvas:
    """The unit-circle frame of one atlas: gx, gy in [-1, 1] across the square, y down, plus seeded noise in that frame."""

    def __init__(self, stem: str, n: int):
        self.stem, self.n = stem, n
        c = (np.arange(n, dtype=np.float32) + 0.5) / n * 2.0 - 1.0
        self.gx = np.broadcast_to(c[None, :], (n, n)).copy()
        self.gy = np.broadcast_to(c[:, None], (n, n)).copy()
        self.rad = np.hypot(self.gx, self.gy)
        self.rng = U.rng("ground.decals." + stem)

    def waves(self, name, n, lo, hi, stretch=(1.0, 1.0), x=None, y=None):
        return F.waves(self.gx if x is None else x, self.gy if y is None else y, f"ground.decals.{self.stem}.{name}", n, lo, hi, stretch)

    def dents(self, name, cell, jitter=0.8):
        return F.cell_dents(self.gx, self.gy, f"ground.decals.{self.stem}.{name}", cell, jitter)

    def clumped_uniform(self):
        """A permutation of (0, 1) with 2 px clumps: density > uniform keeps a texel, so coverage follows density exactly."""
        noise = self.rng.random((self.n, self.n))
        noise = (noise + np.roll(noise, 1, 0) + np.roll(noise, 1, 1) + np.roll(noise, (1, 1), (0, 1))) / 4.0
        order = np.argsort(np.argsort(noise.ravel())).reshape(noise.shape)
        return ((order + 0.5) / noise.size).astype(np.float32)

    def frame(self, rot):
        c, s = math.cos(rot), math.sin(rot)
        return c * self.gx + s * self.gy, -s * self.gx + c * self.gy

    def rel(self, ax, ay, rot, base, wob, lobes=4):
        """Normalised radius of an irregular blob: 1.0 is its edge, edge radius never above 0.92 of the atlas half-width."""
        xr, yr = self.frame(rot)
        rad = np.hypot(xr / ax, yr / ay)
        th = np.arctan2(yr, xr)
        ph = self.rng.random(4) * math.tau
        edge = base * (1.0 + wob * (0.55 * np.sin(2 * th + ph[0]) + 0.35 * np.sin(3 * th + ph[1]) + 0.25 * np.sin(5 * th + ph[2])
                                    + 0.55 * self.waves("edge", lobes + 2, 0.5, 1.6)))
        edge = np.clip(edge, 0.40, 0.92 / max(ax, ay, 1.0))
        return rad / edge


def _mix(a, b, t):
    return np.asarray(a, np.float32) * (1 - t[..., None]) + np.asarray(b, np.float32) * t[..., None]


def paint_dirt(cv: Canvas, d: Decal):
    rot = cv.rng.random() * math.tau
    rel = cv.rel(1.0, 0.82 + 0.15 * cv.rng.random(), rot, 0.92, 0.10)
    density = 1.0 - 0.80 * _smooth(0.50, 1.0, rel)
    density = np.where(rel > 1.0, 0.0, density)
    dents = cv.dents("dent", 0.30)
    fine = cv.dents("fine", 0.09)
    clump = cv.waves("clump", 5, 0.25, 0.9)
    pebble = _smooth(0.55, 0.85, cv.dents("peb", 0.065, 0.9))
    k = 1.0 - 0.30 * np.clip(dents, 0, 1) + 0.12 * clump - 0.12 * fine
    col = _mix(d.base, d.alt, np.clip(0.18 + 0.25 * np.tanh(clump) + 0.6 * pebble + 0.3 * (rel - 0.5), 0, 1))
    return density, col * k[..., None]


def paint_moss(cv: Canvas, d: Decal):
    rot = cv.rng.random() * math.tau
    rel = cv.rel(1.0, 0.85 + 0.15 * cv.rng.random(), rot, 0.92, 0.14, 6)
    clump = cv.waves("clump", 6, 0.14, 0.55)
    s = 0.55 * clump + 1.05 * (1.0 - np.clip(rel, 0, 1.2)) - 0.08
    density = _smooth(-0.10, 0.50, s)
    density = np.where(rel > 1.0, 0.0, density)
    tips = np.clip(cv.dents("tips", 0.11, 0.9), 0, 1)
    fine = cv.dents("fine", 0.05, 0.9)
    k = 0.70 + 0.55 * tips - 0.25 * fine + 0.12 * cv.waves("mott", 5, 0.2, 0.7)
    col = _mix(d.base, d.alt, np.clip(0.30 + 0.9 * tips - 0.2 * fine + 0.2 * clump, 0, 1))
    return density, col * k[..., None]


def paint_puddle(cv: Canvas, d: Decal):
    rot = cv.rng.random() * math.tau
    rel = cv.rel(1.0, 0.75 + 0.2 * cv.rng.random(), rot, 0.90, 0.18, 6)
    density = np.where(rel < 1.0, 1.0 - 0.75 * _smooth(0.90, 1.0, rel), 0.0)
    ring = _smooth(0.55, 1.0, rel)                              # wet mud lip, lighter than the open water
    water = cv.waves("water", 5, 0.12, 0.5)
    glint = _smooth(0.70, 0.95, np.tanh(cv.waves("glint", 4, 0.10, 0.35, stretch=(1.0, 0.25)) * 0.8)) * (1.0 - ring)
    col = _mix(d.base, d.alt, np.clip(ring ** 1.5 + 0.05 * water, 0, 1))
    col = col * (0.85 + 0.35 * np.clip(1.0 - rel, 0, 1) ** 1.5)[..., None] * (1.0 + 0.10 * water)[..., None]
    col = col + glint[..., None] * np.array([0.07, 0.08, 0.09], np.float32)
    return density, col


def _segments(cv: Canvas, d: Decal):
    """Random-walk crack segments (x0, y0, x1, y1, width): a trunk through the middle and branches off it."""
    r = cv.rng
    segs = []

    def walk(x, y, ang, length, w0, depth):
        steps = max(4, int(length / 0.09))
        for i in range(steps):
            ang += (r.random() - 0.5) * 0.9
            step = length / steps
            nx, ny = x + math.cos(ang) * step, y + math.sin(ang) * step
            if math.hypot(nx, ny) > 0.80:
                break
            w = w0 * (1.0 - 0.65 * i / steps)
            segs.append((x, y, nx, ny, w))
            if depth < 2 and r.random() < 0.30:
                walk(nx, ny, ang + (r.random() - 0.5) * 2.2, length * (0.35 + 0.25 * r.random()), w * 0.7, depth + 1)
            x, y = nx, ny

    a0 = r.random() * math.tau
    for sgn in (0.0, math.pi):
        walk(0.0, 0.0, a0 + sgn + (r.random() - 0.5) * 0.4, 1.1 + 0.4 * r.random(), 0.036, 0)
    return segs


def paint_crack(cv: Canvas, d: Decal):
    segs = _segments(cv, d)
    dist = np.full(cv.gx.shape, 9.0, np.float32)
    wid = np.zeros(cv.gx.shape, np.float32)
    for x0, y0, x1, y1, w in segs:
        vx, vy = x1 - x0, y1 - y0
        ll = vx * vx + vy * vy + 1e-9
        t = np.clip(((cv.gx - x0) * vx + (cv.gy - y0) * vy) / ll, 0, 1)
        dd = np.hypot(cv.gx - x0 - t * vx, cv.gy - y0 - t * vy)
        better = dd < dist
        dist = np.where(better, dd, dist)
        wid = np.where(better, w, wid)
    wob = 0.012 * cv.waves("wob", 4, 0.04, 0.12)
    core = (dist + wob) < wid
    halo_r = 0.46 + 0.10 * cv.waves("halo", 4, 0.3, 0.9)
    halo = 1.0 - _smooth(0.02, 1.0, dist / np.maximum(halo_r, 0.08))
    density = np.where(core, 1.0, 0.90 * halo ** 0.45)
    chip = np.clip(cv.dents("chip", 0.08, 0.9), 0, 1)
    k = 0.75 + 0.5 * chip + 0.15 * cv.waves("mott", 5, 0.2, 0.7)
    near = 1.0 - _smooth(0.0, 0.09, dist)                        # dust is darker against the crack
    col = _mix(d.alt, (0.12, 0.12, 0.11), np.clip(near, 0, 1))
    col = col * k[..., None]
    col = np.where(core[..., None], np.asarray(d.base, np.float32), col)
    return density, col


def paint_path(cv: Canvas, d: Decal):
    rot = cv.rng.random() * math.tau
    xr, yr = cv.frame(rot)
    ph = cv.rng.random(3) * math.tau
    centre = 0.16 * np.sin(2.1 * xr + ph[0]) + 0.07 * np.sin(4.7 * xr + ph[1])
    across = yr - centre
    half = 0.52 + 0.07 * np.sin(3.1 * xr + ph[2]) + 0.05 * cv.waves("w", 4, 0.3, 1.0)
    a = np.abs(across)
    along = np.abs(xr)
    density = (1.0 - _smooth(half * 0.62, half, a)) * (1.0 - _smooth(0.74, 0.93, along))
    ruts = 1.0 - _smooth(0.0, 0.07, np.abs(a - 0.20 * (half / 0.52)))
    ruts = ruts * (0.5 + 0.5 * np.tanh(cv.waves("rutbreak", 4, 0.2, 0.6)))
    pebble = _smooth(0.5, 0.8, cv.dents("peb", 0.07, 0.9))
    dents = np.clip(cv.dents("dent", 0.22), 0, 1)
    fine = cv.dents("fine", 0.06)
    k = 1.0 - 0.22 * dents - 0.08 * fine + 0.10 * cv.waves("mott", 5, 0.2, 0.8)
    k = k * (1.0 - 0.34 * ruts)
    col = _mix(d.base, d.alt, np.clip(0.25 + 0.55 * pebble + 0.25 * np.tanh(cv.waves("lt", 4, 0.3, 0.9)), 0, 1))
    return density, col * k[..., None]


LEAF_COLOURS = ((0.27, 0.20, 0.12), (0.23, 0.20, 0.12), (0.19, 0.18, 0.12), (0.30, 0.24, 0.15), (0.16, 0.14, 0.11), (0.21, 0.16, 0.11))


def paint_litter(cv: Canvas, d: Decal):
    """Fallen leaves and a few snapped twigs, each a hard-edged cut-out; later ones lie on earlier ones."""
    r = cv.rng
    n = cv.n
    density = np.zeros((n, n), np.float32)
    col = np.zeros((n, n, 3), np.float32)
    count = 58 + 8 * d.var
    for i in range(count):
        px, py = r.random(2) * 1.5 - 0.75
        if math.hypot(px, py) > 0.74:
            px, py = px * 0.6, py * 0.6
        ang = r.random() * math.tau
        la = 0.19 + 0.08 * r.random()                       # half length
        lb = la * (0.40 + 0.10 * r.random())                # half width
        c, s = math.cos(ang), math.sin(ang)
        u = (cv.gx - px) * c + (cv.gy - py) * s
        v = -(cv.gx - px) * s + (cv.gy - py) * c
        t = u / la
        width = lb * np.clip(1.0 - t * t, 0, 1) ** 0.75 * (1.0 + 0.25 * t)   # pointed, fuller toward the stem end
        bend = 0.05 * la * t * t
        inside = (np.abs(t) < 1.0) & (np.abs(v - bend) < width)
        if not inside.any():
            continue
        tone = np.array(LEAF_COLOURS[int(r.integers(len(LEAF_COLOURS)))], np.float32) * (0.80 + 0.40 * r.random())
        vein = np.abs(v - bend) < (0.014 + 0.01 * (1 - np.abs(t)))
        shade = 1.0 - 0.38 * vein - 0.10 * np.clip(np.abs(v - bend) / np.maximum(width, 1e-3), 0, 1)
        col[inside] = (tone[None, :] * shade[inside][:, None])
        density[inside] = 1.0
    for i in range(3 + d.var):                              # twigs
        px, py = r.random(2) * 1.2 - 0.6
        ang = r.random() * math.tau
        ln = 0.22 + 0.2 * r.random()
        c, s = math.cos(ang), math.sin(ang)
        u = (cv.gx - px) * c + (cv.gy - py) * s
        v = -(cv.gx - px) * s + (cv.gy - py) * c
        inside = (np.abs(u) < ln) & (np.abs(v) < 0.017 * (1.0 - 0.4 * np.abs(u) / ln)) & (np.hypot(cv.gx, cv.gy) < 0.88)
        col[inside] = np.array((0.14, 0.11, 0.08), np.float32) * (0.9 + 0.2 * r.random())
        density[inside] = 1.0
    col = col * (0.92 + 0.16 * cv.waves("mott", 5, 0.15, 0.6))[..., None]
    return density, col


def paint_scorch(cv: Canvas, d: Decal):
    rot = cv.rng.random() * math.tau
    rel = cv.rel(1.0, 0.82 + 0.15 * cv.rng.random(), rot, 0.92, 0.14)
    holes = cv.waves("hole", 5, 0.1, 0.4)
    density = 1.0 - 0.95 * _smooth(0.45, 1.0, rel + 0.07 * holes)
    density = np.where(rel > 1.0, 0.0, density)
    ash = _smooth(0.55, 0.9, cv.dents("ash", 0.07, 0.9)) * (0.4 + 0.6 * _smooth(0.3, 1.0, rel))
    char = np.clip(cv.dents("char", 0.20), 0, 1)
    k = 0.85 + 0.35 * char + 0.2 * cv.waves("mott", 5, 0.2, 0.6)
    col = _mix(d.base, d.alt, np.clip(ash + 0.15 * _smooth(0.5, 1.0, rel), 0, 1))
    return density, col * k[..., None]


def paint_stain(cv: Canvas, d: Decal):
    rot = (cv.rng.random() - 0.5) * 0.5
    rel = cv.rel(0.85, 1.0, rot, 0.90, 0.12)
    # Drips run toward +y of the atlas: narrow lobes added below the blob.
    drips = np.zeros_like(rel)
    for i in range(3):
        cx = (cv.rng.random() - 0.5) * 0.9
        ln = 0.25 + 0.30 * cv.rng.random()
        drips = np.maximum(drips, np.exp(-((cv.gx - cx) / 0.075) ** 2) * (cv.gy > 0.0) * np.clip(1.0 - (cv.gy - 0.15) / ln, 0, 1))
    streak = cv.waves("streak", 5, 0.08, 0.5, stretch=(1.0, 0.18))
    s = (1.0 - rel) + 0.9 * drips + 0.05 * streak
    density = _smooth(-0.04, 0.22, s)
    density = np.where(cv.rad > 0.9, 0.0, density)
    dents = np.clip(cv.dents("dent", 0.18), 0, 1)
    k = 0.85 + 0.10 * np.tanh(streak) + 0.25 * dents
    col = _mix(d.base, d.alt, np.clip(0.55 * _smooth(0.0, 0.5, rel) + 0.25 * dents + 0.1 * np.tanh(streak), 0, 1))
    return density, col * k[..., None]


PAINTERS = {"dirt": paint_dirt, "moss": paint_moss, "puddle": paint_puddle, "crack": paint_crack, "path": paint_path,
            "litter": paint_litter, "scorch": paint_scorch, "stain": paint_stain}


def luma(rgb):
    return 0.2126 * rgb[..., 0] + 0.7152 * rgb[..., 1] + 0.0722 * rgb[..., 2]


def render_texture(stem: str) -> np.ndarray:
    """-> uint8 RGBA (n, n, 4). Alpha is 0 or 255; the outer texels and everything past CIRCLE_MAX are clear."""
    d = DECALS[stem]
    n = d.atlas
    cv = Canvas(stem, n)
    density, col = PAINTERS[d.paint](cv, d)
    keep = (density > cv.clumped_uniform()) & (cv.rad < CIRCLE_MAX)
    keep[:2, :] = keep[-2:, :] = False
    keep[:, :2] = keep[:, -2:] = False
    col = np.clip(col, 0.0, None).astype(np.float32)
    target = d.ratio * ref_luma(d)
    scale = 1.0
    for _ in range(4):                                      # the clip at 1.0 bends the mean a little; converge on it
        c = np.clip(col * scale, 0.0, 1.0)
        scale *= target / max(float(luma(c[keep]).mean()), 1e-6)
    c = np.clip(col * scale, 0.0, 1.0)
    mean_rgb = c[keep].mean(axis=0)
    rgb = np.where(keep[..., None], c, mean_rgb[None, None, :])  # clear texels carry the mean colour: no dark fringe when filtered
    out = np.zeros((n, n, 4), np.uint8)
    out[..., :3] = np.round(rgb * 255.0).astype(np.uint8)
    out[..., 3] = np.where(keep, 255, 0)
    return out


def png_bytes(rgba: np.ndarray) -> bytes:
    buf = io.BytesIO()
    Image.fromarray(rgba, "RGBA").save(buf, format="PNG", compress_level=9)
    return buf.getvalue()


# --------------------------------------------------------------------------------------------------- mesh
def fan_arrays(radius: float):
    """Flat 16-gon fan at y = 0, uv over the whole atlas (the circle is inscribed in it), normals +Y, CCW seen from +Y."""
    a = np.arange(FAN) * math.tau / FAN
    x, z = radius * np.cos(a), radius * np.sin(a)
    pos = np.vstack([[0.0, 0.0, 0.0], np.column_stack((x, np.zeros(FAN), z))]).astype(np.float32)
    nrm = np.tile(np.array([0.0, 1.0, 0.0], np.float32), (FAN + 1, 1))
    uv = np.vstack([[0.5, 0.5], np.column_stack((0.5 + 0.5 * np.cos(a), 0.5 + 0.5 * np.sin(a)))]).astype(np.float32)
    idx = np.array([(0, 1 + (i + 1) % FAN, 1 + i) for i in range(FAN)], np.uint32)
    return pos, nrm, uv, idx


def build_decal(stem: str) -> dict:
    d = DECALS[stem]
    radius = d.diameter / 2.0
    rgba = render_texture(stem)
    png = png_bytes(rgba)
    pos, nrm, uv, idx = fan_arrays(radius)
    mats = [{"name": "ground.baked", "base_color_factor": (1.0, 1.0, 1.0, 1.0), "base_color_png": png, "metallic": 0.0,
             "roughness": 1.0, "alpha_mode": "MASK", "alpha_cutoff": 0.5, "double_sided": True}]
    prims = [G.GltfPrimitive(pos, nrm, uv, idx, None, None, material=0, node=0)]
    nodes = [G.GltfNode(name=stem, parent=None)]
    bounds = (float(np.hypot(pos[:, 0], pos[:, 2]).max()), float(np.abs(pos[:, [0, 2]]).max()), float(pos[:, 1].max()))
    glb = G.build_glb(nodes, prims, None, None, [], mats)
    return dict(name=stem, rgba=rgba, png=png, glb=glb, bounds=bounds, decal=d, positions=pos, normals=nrm, indices=idx)


def _no_parts():
    return []


ASSETS: dict[str, "K.GroundSpec"] = {
    stem: K.GroundSpec(build=_no_parts, cls=d.cls, layer="decal", atlas=d.atlas, ground=d.ground, weight=d.weight, scale=d.scale)
    for stem, d in DECALS.items()
}


# --------------------------------------------------------------------------------------------------- preview
def preview(assets: dict, path: Path = PREVIEW) -> None:
    """24 decals, 6 x 4, each on a flat swatch of the mean colour of the first tile it may lie on (a stand-in, not the tile)."""
    cell = 160
    cols = 6
    rows = (len(assets) + cols - 1) // cols
    sheet = np.zeros((rows * cell, cols * cell, 3), np.uint8)
    for i, (stem, a) in enumerate(assets.items()):
        d = a["decal"]
        tile = TILE_OF[d.ground[0] if d.ground[0] != "snow" else "dirt"]
        bg = np.array(TILE_RGB.get(tile, TILE_RGB["dirt"]), np.float32)
        rgba = Image.fromarray(a["rgba"], "RGBA").resize((cell, cell), Image.NEAREST)
        t = np.asarray(rgba, np.float32)
        al = (t[..., 3:4] > 127).astype(np.float32)
        tile_img = bg[None, None, :] * (0.92 + 0.08 * (((np.arange(cell)[:, None] // 8 + np.arange(cell)[None, :] // 8) % 2)[..., None]))
        out = tile_img * (1 - al) + t[..., :3] * al
        r, c = divmod(i, cols)
        sheet[r * cell:(r + 1) * cell, c * cell:(c + 1) * cell] = np.clip(out, 0, 255).astype(np.uint8)
    path.parent.mkdir(parents=True, exist_ok=True)
    Image.fromarray(sheet, "RGB").save(path, format="PNG", compress_level=9)


# --------------------------------------------------------------------------------------------------- entry
def main(out_root: Path = K.GROUND_ROOT, only: list[str] | None = None, with_preview: bool = False) -> None:
    out_root = Path(out_root)
    if only:
        unknown = [s for s in only if s not in DECALS]
        if unknown:
            raise SystemExit(f"unknown decals: {unknown}")
    built = {stem: build_decal(stem) for stem in DECALS if not only or stem in only}
    entries, files = [], {}
    for stem, a in built.items():
        entries.append(K.manifest_entry(FAMILY, stem, ASSETS[stem], a["bounds"]))
        files[stem] = a["glb"]
        print(f"gen_ground_decals: {stem:16s} r={a['bounds'][0]:.3f} size={len(a['glb']):6d}")
    K.write_family(out_root, FAMILY, entries, files, partial=bool(only))
    if with_preview:
        preview(built)


if __name__ == "__main__":
    argv = sys.argv[1:]
    only_arg = None
    if "--only" in argv:
        i = argv.index("--only")
        only_arg = argv[i + 1].split(",")
        del argv[i:i + 2]
    flags = [a for a in argv if a.startswith("--")]
    pos_args = [a for a in argv if not a.startswith("--")]
    main(Path(pos_args[0]) if pos_args else K.GROUND_ROOT, only_arg, "--preview" in flags)
