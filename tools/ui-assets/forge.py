"""Forged-iron renderer for dream's UI art: linear light, GGX, baked wear, filmic finish.

Chain: Relief (authored form + material ids) -> bake (maps) -> age -> light -> finish -> downsample.

  relief = Relief(w_u, h_u); relief.plate(Rect(...), ...); rgba = relief.render(seed)   # hi-res, float sRGB
  sprite = downsample(rgba)                                                              # uint8, atlas density

Heights are in u, canvases are hi-res at PPU px/u, geometry is u with y down. Colours are linear until
finish() returns sRGB-encoded float RGBA (straight alpha). Everything is deterministic: seeds come from
uiart.rng(name) and the shading has no random terms.

Art rules this module enforces by construction:
  * form first: primary shapes are authored 2..40 u wide; noise only adds hammer dents and fine pitting;
  * wear is driven by the convexity of the primary form, gated by a large-scale "use" field, so some
    edges stay black and scaled while others are worn, and wear is patchy within a worn edge;
  * rust lives in cavities and in drip streaks under rivets; soot in cavities; dust on up-facing slopes;
  * metal is dull: black scale has F0 near 0.08, bare steel about 0.3, rough 0.4, so highlights are
    broken sheens and a few hot glints, not mirror lines.
"""
from __future__ import annotations

import math
import sys
import zlib
from dataclasses import dataclass
from pathlib import Path

import numpy as np
from PIL import Image

import uiart as U
from uiart import PPU, SS

F32 = np.float32
TAU = math.tau
HERE = Path(__file__).resolve().parent
SHARPEN = 0.20  # unsharp amount in downsample, tuned for display at 2.25 px/u from a 4 px/u atlas


def smooth(e0, e1, x):
    t = np.clip((x - e0) / (e1 - e0), 0, 1)
    return t * t * (3 - 2 * t)


# materials ----------------------------------------------------------------------------


@dataclass(frozen=True)
class Material:
    """Linear-light surface constants. bare_* is what edge wear exposes."""

    albedo: tuple
    f0: tuple
    rough: float
    aniso: float = 0.0
    bare_albedo: tuple | None = None
    bare_f0: tuple | None = None
    bare_rough: float = 0.4
    wear: float = 1.0  # how readily edges wear through
    grit: float = 1.0  # hammer dent and pitting weight
    vary: float = 0.12  # albedo and specular patina variation, along the forging direction


def _tok(name: str) -> tuple:
    """Linear RGB of a tokens.json colour."""
    return tuple(float(v) for v in U.srgb_to_linear(U.token(name)))


STEEL_BARE = ((0.020, 0.020, 0.021), _tok("forge.steel.bare"), 0.40)

MATERIALS: dict[str, Material] = {
    # blackened forged iron, three heats so assembled plates differ a little
    "iron": Material((0.0062, 0.0058, 0.0055), (0.028, 0.026, 0.025), 0.56, 0.30, *STEEL_BARE, 1.0, 1.0, 0.32),
    "iron.warm": Material((0.0088, 0.0074, 0.0060), (0.033, 0.030, 0.027), 0.58, 0.30, *STEEL_BARE, 1.0, 1.0, 0.32),
    "iron.cold": Material((0.0064, 0.0066, 0.0070), (0.029, 0.029, 0.029), 0.54, 0.30, *STEEL_BARE, 1.0, 1.0, 0.32),
    # the field the plates sit on: nearly black, so the plates read as big shapes when squinted at
    "iron.black": Material((0.0034, 0.0032, 0.0031), (0.016, 0.015, 0.015), 0.64, 0.30, *STEEL_BARE, 0.8, 1.0, 0.25),
    "iron.rivet": Material((0.012, 0.0102, 0.0085), (0.033, 0.030, 0.027), 0.64, 0.0, *STEEL_BARE, 0.45, 0.6, 0.20),
    "steel": Material((0.020, 0.020, 0.021), (0.30, 0.295, 0.285), 0.40, 0.35, None, None, 0.40, 0.4, 0.8),
    # aged brass and ivory-gold rails; wear polishes towards a brighter brass
    "brass": Material((0.012, 0.009, 0.004), _tok("forge.gold"), 0.46, 0.25, (0.012, 0.009, 0.004), (0.50, 0.40, 0.22), 0.36, 0.8, 0.35),
    # ivory-gold rail: bone inlay gone amber with age, the warm note against cold iron
    "rail": Material(_tok("forge.ivory"), (0.30, 0.245, 0.150), 0.37, 0.2, (0.58, 0.46, 0.27), (0.40, 0.33, 0.21), 0.34, 0.9, 0.3, 0.80),
    "ivory": Material(tuple(0.8 * v for v in _tok("forge.ivory")), (0.040, 0.040, 0.038), 0.55, 0.0, None, None, 0.5, 0.3, 0.2),
    "stone": Material((0.058, 0.055, 0.052), (0.040, 0.040, 0.040), 0.72, 0.0, (0.11, 0.105, 0.10), (0.045, 0.045, 0.045), 0.65, 0.7, 0.7),
    "void": Material((0.003, 0.003, 0.003), (0.030, 0.030, 0.030), 0.80, 0.0, None, None, 0.8, 0.0, 0.3),
    "rust": Material((0.17, 0.055, 0.014), (0.030, 0.028, 0.026), 0.85, 0.0, None, None, 0.85, 0.0, 0.5),
}
MATERIAL_NAMES = list(MATERIALS)
MAT_ID = {n: i for i, n in enumerate(MATERIAL_NAMES)}


def _mat_lut(field_name: str, width: int) -> np.ndarray:
    rows = []
    for n in MATERIAL_NAMES:
        m = MATERIALS[n]
        v = getattr(m, field_name)
        if v is None:
            fallback = {"bare_albedo": m.albedo, "bare_f0": m.f0}[field_name]
            v = fallback
        rows.append(np.broadcast_to(np.array(v, dtype=F32), (width,)))
    return np.array(rows, dtype=F32)


# rig ----------------------------------------------------------------------------------


@dataclass(frozen=True)
class Light:
    dir: tuple  # towards the light, +z towards the viewer, y down
    color: tuple
    intensity: float
    shadow: bool = False  # uses the baked cast shadow


@dataclass(frozen=True)
class Panel:
    """Soft reflection panel in the reflection vector's xy plane; widens with roughness."""

    cx: float
    cy: float
    wx: float
    wy: float
    intensity: float
    color: tuple = (1.0, 1.0, 1.0)


@dataclass(frozen=True)
class Rig:
    lights: tuple
    panels: tuple
    ambient: tuple


GOTHIC_RIG = Rig(
    lights=(
        Light((-0.60, -0.60, 0.53), (0.97, 0.98, 0.99), 12.5, shadow=True),  # cool-neutral key, upper left
        Light((0.75, -0.25, 0.45), (1.00, 0.66, 0.34), 1.2),                # low amber candle, right
        Light((-0.15, 0.80, 0.30), (0.70, 0.80, 1.00), 0.03),               # cold rim from below
    ),
    panels=(
        Panel(-0.18, -0.62, 0.55, 0.22, 0.55, (0.96, 0.98, 1.0)),  # broad overhead band so black metal shows form
        Panel(0.80, 0.05, 0.20, 0.50, 0.25, (1.0, 0.70, 0.40)),    # amber strip low right
    ),
    ambient=(0.018, 0.019, 0.022),
)


# maps and surface ---------------------------------------------------------------------


@dataclass
class Maps:
    normal: np.ndarray     # HxWx3 unit, z towards the viewer
    ao: np.ndarray         # 1 open .. 0 closed
    shadow: np.ndarray     # 1 lit .. 0 in the key's cast shadow
    cavity: np.ndarray     # 0..1, concave
    curvature: np.ndarray  # -1..1, positive convex
    edge: np.ndarray       # 0..1, convex edges of the primary form only


@dataclass
class Surface:
    albedo: np.ndarray       # HxWx3 linear diffuse
    f0: np.ndarray           # HxWx3 linear specular colour
    rough: np.ndarray        # HxW perceptual roughness
    aniso: np.ndarray        # HxW 0..1
    tangent: np.ndarray      # HxW radians, direction of the forging or file marks
    alpha: np.ndarray        # HxW coverage
    bare_albedo: np.ndarray | None = None
    bare_f0: np.ndarray | None = None
    bare_rough: np.ndarray | None = None
    wear_gain: np.ndarray | None = None  # HxW per-material wear affinity
    emit: np.ndarray | None = None       # HxWx3 linear emission
    worn: np.ndarray | None = None       # HxW diagnostics written by age(): bare metal share


# filters ------------------------------------------------------------------------------


def _wrap(wrap) -> str:
    """Wrap spec as a string: "" none, "x", "y" or "xy". True means both."""
    return "xy" if wrap is True else ("" if not wrap else str(wrap))


def _sl(ndim: int, axis: int, start, stop) -> tuple:
    idx = [slice(None)] * ndim
    idx[axis] = slice(start, stop)
    return tuple(idx)


def _pad(a: np.ndarray, n: int, wrap) -> np.ndarray:
    """Pad rows and columns by n, wrapping on the tiling axes and clamping on the others."""
    w = _wrap(wrap)
    for axis, key in ((0, "y"), (1, "x")):
        pad = [(0, 0)] * a.ndim
        pad[axis] = (n, n)
        a = np.pad(a, pad, mode="wrap" if key in w else "edge")
    return a


def blur(a: np.ndarray, sigma_px: float, wrap=False) -> np.ndarray:
    """Gaussian blur. Small sigma uses an exact kernel, large sigma three box passes. Edge-clamped."""
    a = np.asarray(a, dtype=F32)
    if sigma_px < 0.3:
        return a
    w = _wrap(wrap)
    out = a
    if sigma_px <= 3.0:
        r = int(math.ceil(sigma_px * 3))
        k = np.exp(-0.5 * (np.arange(-r, r + 1) / sigma_px) ** 2)
        k = (k / k.sum()).astype(F32)
        for axis, key in ((0, "y"), (1, "x")):
            pad = [(0, 0)] * out.ndim
            pad[axis] = (r, r)
            p = np.pad(out, pad, mode="wrap" if key in w else "edge")
            n = out.shape[axis]
            acc = np.zeros_like(out)
            for i, wt in enumerate(k):
                acc += wt * p[_sl(out.ndim, axis, i, i + n)]
            out = acc
        return out
    r = max(1, int(round(sigma_px * 0.87)))
    for axis, key in ((0, "y"), (1, "x")):
        pad = [(0, 0)] * out.ndim
        pad[axis] = (3 * r, 3 * r)
        p = np.pad(out, pad, mode="wrap" if key in w else "edge")
        for _ in range(3):  # each pass: window of 2r+1 centred on the output index, so no net shift
            c = np.cumsum(p, axis=axis, dtype=np.float64)
            zero_shape = list(c.shape)
            zero_shape[axis] = 1
            c = np.concatenate((np.zeros(zero_shape), c), axis=axis)
            p = (c[_sl(c.ndim, axis, 2 * r + 1, None)] - c[_sl(c.ndim, axis, 0, -(2 * r + 1))]) / (2 * r + 1)
        out = p.astype(F32)
    return out


def _resize(a: np.ndarray, w: int, h: int) -> np.ndarray:
    return np.asarray(Image.fromarray(np.ascontiguousarray(a, dtype=F32), "F").resize((w, h), Image.BILINEAR), dtype=F32)


def _block_mean(a: np.ndarray, f: int) -> np.ndarray:
    h, w = a.shape[0] // f * f, a.shape[1] // f * f
    return a[:h, :w].reshape(h // f, f, w // f, f).mean(axis=(1, 3), dtype=F32)


_DIRS8 = ((1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1), (0, -1), (1, -1))


def _horizon_ao(h: np.ndarray, f: int, steps: tuple, wrap) -> np.ndarray:
    """Horizon occlusion on h (u) at 1/f resolution, 8 directions. Returns 1 open .. 0 closed, full size."""
    H, W = h.shape
    hs = _block_mean(h, f) if f > 1 else h
    hh, ww = hs.shape
    ppu = PPU / f
    pad = max(steps)
    P = _pad(hs, pad, wrap)
    occ = np.zeros_like(hs)
    for dx, dy in _DIRS8:
        best = np.zeros_like(hs)
        for s in steps:
            ox, oy = int(round(dx * s)), int(round(dy * s))
            dist = math.hypot(ox, oy) / ppu
            t = (P[pad + oy : pad + oy + hh, pad + ox : pad + ox + ww] - hs) / dist
            np.maximum(best, t, out=best)
        occ += best / np.sqrt(1 + best * best)
    ao = 1 - occ / len(_DIRS8)
    if f > 1:
        ao = _resize(ao, ww * f, hh * f)
        ao = np.pad(ao, ((0, H - ao.shape[0]), (0, W - ao.shape[1])), mode="edge")
    return ao


def _cast_shadow(h: np.ndarray, light: tuple, f: int, wrap) -> np.ndarray:
    """Soft cast shadow of the height field for a directional light. 1 lit .. 0 shadowed."""
    H, W = h.shape
    lx, ly, lz = light
    hl = math.hypot(lx, ly)
    if hl < 1e-4:
        return np.ones_like(h)
    dx, dy = lx / hl, ly / hl
    tan_e = lz / hl
    hs = _block_mean(h, f) if f > 1 else h
    hh, ww = hs.shape
    ppu = PPU / f
    steps = (1, 2, 3, 4, 6, 8, 11, 15, 20, 26, 34, 44, 58, 76)
    pad = max(steps)
    P = _pad(hs, pad, wrap)
    blocked = np.zeros_like(hs)
    for s in steps:
        ox, oy = int(round(dx * s)), int(round(dy * s))
        d_u = math.hypot(ox, oy) / ppu
        excess = P[pad + oy : pad + oy + hh, pad + ox : pad + ox + ww] - hs - d_u * tan_e
        soft = 0.10 + 0.14 * d_u  # penumbra widens with distance
        np.maximum(blocked, np.clip(excess / soft, 0, 1), out=blocked)
    sh = 1 - blocked
    if f > 1:
        sh = _resize(sh, ww * f, hh * f)
        sh = np.pad(sh, ((0, H - sh.shape[0]), (0, W - sh.shape[1])), mode="edge")
    return sh


def bake(height: np.ndarray, primary: np.ndarray | None = None, key: tuple | None = None, wrap=False, soften_u: float = 0.14) -> Maps:
    """Maps from a height field in u. primary is the authored form without dents; edge uses it alone.
    wrap is "", "x", "y", "xy" or a bool for tiling sprites."""
    h = np.asarray(height, dtype=F32)
    p = h if primary is None else np.asarray(primary, dtype=F32)
    key = key or GOTHIC_RIG.lights[0].dir
    hs = blur(h, soften_u * PPU, wrap)
    gy, gx = np.gradient(hs)
    n = np.dstack((-gx * PPU, -gy * PPU, np.ones_like(hs)))
    n /= np.linalg.norm(n, axis=2, keepdims=True)
    ao_fine = _horizon_ao(h, 1, (1, 2, 4, 7), wrap)
    ao_wide = _horizon_ao(h, 4, (1, 2, 3, 5, 8, 12, 18, 26), wrap)
    ao = np.clip((ao_fine * ao_wide) ** 1.5, 0, 1)
    shadow = _cast_shadow(h, key, 2, wrap)
    c1 = np.clip((blur(h, 0.30 * PPU, wrap) - h) / 0.30, 0, 1)
    c2 = np.clip((blur(h, 1.30 * PPU, wrap) - h) / 1.30, 0, 1)
    cavity = 1 - (1 - c1) * (1 - 0.8 * c2)
    curvature = np.tanh((blur(h, 0.12 * PPU, wrap) - blur(h, 0.5 * PPU, wrap)) / 0.12)
    e1 = np.clip((blur(p, 0.12 * PPU, wrap) - blur(p, 0.40 * PPU, wrap)) / 0.12, 0, 1)
    e2 = np.clip((blur(p, 0.35 * PPU, wrap) - blur(p, 1.20 * PPU, wrap)) / 0.34, 0, 1)
    edge = np.maximum(e1, e2)
    return Maps(n.astype(F32), ao.astype(F32), shadow.astype(F32), cavity.astype(F32), curvature.astype(F32), edge.astype(F32))


# noise --------------------------------------------------------------------------------
# All noise is a function of absolute position in u, never of the canvas size, so a band cut out of a
# big sprite shades exactly like the whole. period=(px, py) in u makes it tile on that axis.


def _snap(k: float, period) -> float:
    return k if not period else round(k * period / TAU) * TAU / period


def waves(X: np.ndarray, Y: np.ndarray, name: str, n: int, wl_lo_u: float, wl_hi_u: float, stretch=(1.0, 1.0), period=(None, None)) -> np.ndarray:
    """Sum of n plane waves with log-spaced wavelengths; roughly unit variance, smooth, no grid.
    stretch scales the wave vector per axis (0.3 elongates features along that axis)."""
    r = U.rng(name)
    ang = (np.arange(n) + r.random(n) * 0.8) / n * math.pi + r.random() * math.pi  # stratified, so no direction clumps
    wl = wl_lo_u * (wl_hi_u / wl_lo_u) ** ((np.arange(n)[r.permutation(n)] + r.random(n)) / n)
    ph = r.random(n) * TAU
    out = None
    for a_, w_, p_ in zip(ang, wl, ph):
        kx = _snap(math.cos(a_) * TAU / w_ * stretch[0], period[0])
        ky = _snap(math.sin(a_) * TAU / w_ * stretch[1], period[1])
        t = np.cos(kx * X + ky * Y + p_).astype(F32)
        out = t if out is None else out + t
    return out / math.sqrt(n / 2)


_M64 = (1 << 64) - 1


def _hash01(i: np.ndarray, j: np.ndarray, seed: int, salt: int) -> np.ndarray:
    """Uniform [0,1) from integer cell coordinates; the same cell gives the same value on any canvas."""
    x = (i.astype(np.uint64) * np.uint64(0x9E3779B97F4A7C15)) ^ (j.astype(np.uint64) * np.uint64(0xC2B2AE3D27D4EB4F))
    x = x ^ np.uint64((seed * 0x165667B19E3779F9 + salt * 0x27D4EB2F165667C5) & _M64)
    x ^= x >> np.uint64(33)
    x *= np.uint64(0xFF51AFD7ED558CCD)
    x ^= x >> np.uint64(33)
    x *= np.uint64(0xC4CEB9FE1A85EC53)
    x ^= x >> np.uint64(33)
    return (x >> np.uint64(11)).astype(np.float64) / float(1 << 53)


class _Cells:
    """Jittered grid lookup shared by the dent and facet fields."""

    def __init__(self, X, Y, name: str, cell_u: float, period):
        self.seed = U.SEED ^ zlib.crc32(name.encode("utf-8"))
        px, py = period
        self.cx_u = px / max(1, round(px / cell_u)) if px else cell_u
        self.cy_u = py / max(1, round(py / cell_u)) if py else cell_u
        self.nx = int(round(px / self.cx_u)) if px else None
        self.ny = int(round(py / self.cy_u)) if py else None
        self.px, self.py = px, py
        self.ci = np.floor(X / self.cx_u).astype(np.int64)
        self.cj = np.floor(Y / self.cy_u).astype(np.int64)

    def neighbours(self):
        for dj in (-1, 0, 1):
            for di in (-1, 0, 1):
                ni, nj = self.ci + di, self.cj + dj
                shx = shy = 0.0
                if self.nx:
                    shx = (ni // self.nx) * self.px
                    ni = ni % self.nx
                if self.ny:
                    shy = (nj // self.ny) * self.py
                    nj = nj % self.ny
                yield ni, nj, shx, shy

    def rand(self, i, j, salt: int) -> np.ndarray:
        return _hash01(i, j, self.seed, salt)


def cell_dents(X: np.ndarray, Y: np.ndarray, name: str, cell_u: float, jitter: float = 0.7, period=(None, None)) -> np.ndarray:
    """Overlapping shallow bowls on a jittered grid, 0..~1 deep. Hammered metal at one scale."""
    c = _Cells(X, Y, name, cell_u, period)
    out = np.zeros(np.broadcast_shapes(X.shape, Y.shape), dtype=F32)
    for ni, nj, shx, shy in c.neighbours():
        cx = (ni + 0.5 + (c.rand(ni, nj, 1) - 0.5) * jitter) * c.cx_u + shx
        cy = (nj + 0.5 + (c.rand(ni, nj, 2) - 0.5) * jitter) * c.cy_u + shy
        rad = (0.65 + 0.40 * c.rand(ni, nj, 3)) * cell_u
        amp = 0.35 + 0.65 * c.rand(ni, nj, 4)
        t = np.clip(1 - ((X - cx) ** 2 + (Y - cy) ** 2) / (rad * rad), 0, 1)
        out += (t * t * amp).astype(F32)
    return out


def facet_plane(X: np.ndarray, Y: np.ndarray, name: str, cell_u: float, slope: float, sharp: float = 9.0, period=(None, None)) -> np.ndarray:
    """Beaten-plate facets: each jittered cell owns a random tilted plane and neighbouring planes blend
    with exp(-sharp * d^2 / cell^2) weights, so transitions are soft rolls, never creases or steps."""
    c = _Cells(X, Y, name, cell_u, period)
    shape = np.broadcast_shapes(X.shape, Y.shape)
    num = np.zeros(shape, dtype=F32)
    den = np.full(shape, 1e-9, dtype=F32)
    k = sharp / (cell_u * cell_u)
    for ni, nj, shx, shy in c.neighbours():
        cx = (ni + 0.5 + (c.rand(ni, nj, 1) - 0.5) * 0.9) * c.cx_u + shx
        cy = (nj + 0.5 + (c.rand(ni, nj, 2) - 0.5) * 0.9) * c.cy_u + shy
        sx, sy = (c.rand(ni, nj, 3) - 0.5) * 2 * slope, (c.rand(ni, nj, 4) - 0.5) * 2 * slope
        dx, dy = X - cx, Y - cy
        w = np.exp(-k * (dx * dx + dy * dy)).astype(F32)
        num += w * (sx * dx + sy * dy).astype(F32)
        den += w
    return (num / den).astype(F32)


def _flow(X, Y, name: str, period=(None, None)) -> np.ndarray:
    """File-mark noise 0..1, long along x."""
    return 0.5 + 0.5 * np.tanh(waves(X, Y, name, 8, 0.5, 1.8, (0.25, 1.0), period) * 1.0)


# shapes (signed distance in u, positive inside) ---------------------------------------


class Rect:
    def __init__(self, x, y, w, h, r=0.0):
        self.x, self.y, self.w, self.h, self.r = x, y, w, h, min(r, w / 2, h / 2)

    def bounds(self):
        return self.x, self.y, self.x + self.w, self.y + self.h

    def sdf(self, X, Y):
        cx, cy = self.x + self.w / 2, self.y + self.h / 2
        qx = np.abs(X - cx) - (self.w / 2 - self.r)
        qy = np.abs(Y - cy) - (self.h / 2 - self.r)
        out = np.hypot(np.maximum(qx, 0), np.maximum(qy, 0)) + np.minimum(np.maximum(qx, qy), 0) - self.r
        return -out


class Ellipse:
    def __init__(self, cx, cy, rx, ry):
        self.cx, self.cy, self.rx, self.ry = cx, cy, rx, ry

    def bounds(self):
        return self.cx - self.rx, self.cy - self.ry, self.cx + self.rx, self.cy + self.ry

    def sdf(self, X, Y):
        k = np.sqrt(((X - self.cx) / self.rx) ** 2 + ((Y - self.cy) / self.ry) ** 2)
        return (1 - k) * min(self.rx, self.ry)


class Poly:
    def __init__(self, pts):
        self.pts = [(float(x), float(y)) for x, y in pts]

    def bounds(self):
        xs, ys = zip(*self.pts)
        return min(xs), min(ys), max(xs), max(ys)

    def sdf(self, X, Y):
        shape = np.broadcast_shapes(X.shape, Y.shape)
        d = np.full(shape, 1e9, dtype=F32)
        sgn = np.ones(shape, dtype=F32)
        n = len(self.pts)
        for i in range(n):
            (x0, y0), (x1, y1) = self.pts[i], self.pts[(i + 1) % n]
            ex, ey = x1 - x0, y1 - y0
            wx, wy = X - x0, Y - y0
            t = np.clip((wx * ex + wy * ey) / max(ex * ex + ey * ey, 1e-9), 0, 1)
            bx, by = wx - ex * t, wy - ey * t
            d = np.minimum(d, bx * bx + by * by)
            c1, c2, c3 = Y >= y0, Y < y1, ex * wy > ey * wx
            flip = (c1 & c2 & c3) | (~c1 & ~c2 & ~c3)
            sgn = np.where(flip, -sgn, sgn)
        return (-sgn * np.sqrt(d)).astype(F32)


class Capsule:
    """Round-ended stroke along a polyline."""

    def __init__(self, pts, r):
        self.pts, self.r = [(float(x), float(y)) for x, y in pts], r

    def bounds(self):
        xs, ys = zip(*self.pts)
        return min(xs) - self.r, min(ys) - self.r, max(xs) + self.r, max(ys) + self.r

    def sdf(self, X, Y):
        shape = np.broadcast_shapes(X.shape, Y.shape)
        d = np.full(shape, 1e9, dtype=F32)
        for (x0, y0), (x1, y1) in zip(self.pts[:-1], self.pts[1:]):
            ex, ey = x1 - x0, y1 - y0
            wx, wy = X - x0, Y - y0
            t = np.clip((wx * ex + wy * ey) / max(ex * ex + ey * ey, 1e-9), 0, 1)
            d = np.minimum(d, (wx - ex * t) ** 2 + (wy - ey * t) ** 2)
        return self.r - np.sqrt(d)


def _profile(t: np.ndarray, kind: str) -> np.ndarray:
    if kind == "chamfer":
        return t
    if kind == "pillow":
        return np.sin(t * math.pi / 2) ** 2
    if kind == "rolled":  # steep at the rim, long soft roll into the face: forged, not machined
        return 1 - (1 - t) ** 2.4
    if kind == "flat":
        return (t > 0).astype(F32)
    return np.sqrt(1 - (1 - t) ** 2)


# relief -------------------------------------------------------------------------------


class Relief:
    """Painter's height field with a material id per pixel. Superset of gen_hud.Relief.

    plate/mask_plate/sweep keep gen_hud's signatures (alb and gloss become mat and tint). A plate's
    top is z + bevel * rise, as there. Shapes are analytic, so no jump-flood SDF is needed.
    """

    def __init__(self, w_u, h_u, base: str = "iron", alpha: float = 0.0, tile: str = ""):
        """tile is "", "x", "y" or "xy": the noise, blurs, AO and shadow then wrap on those axes, so the
        sprite tiles if the authored plates are also drawn across the border (draw them twice)."""
        self.w_u, self.h_u = w_u, h_u
        self.H_px, self.W_px = int(round(h_u * PPU)), int(round(w_u * PPU))
        self.h = np.zeros((self.H_px, self.W_px), dtype=F32)
        self.a = np.full((self.H_px, self.W_px), alpha, dtype=F32)
        self.mid = np.full((self.H_px, self.W_px), MAT_ID[base], dtype=np.uint8)
        self.tint: np.ndarray | None = None
        self.ox = self.oy = 0.0  # absolute position of this canvas, nonzero only for bands
        self.xs = ((np.arange(self.W_px) + 0.5) / PPU).astype(F32)
        self.ys = ((np.arange(self.H_px) + 0.5) / PPU).astype(F32)
        self.drips: list[tuple] = []
        self.tile = _wrap(tile)
        self.wrap = self.tile
        self.period = (w_u if "x" in self.tile else None, h_u if "y" in self.tile else None)

    def _waves(self, name: str, n: int, lo: float, hi: float, stretch=(1.0, 1.0)) -> np.ndarray:
        return waves(self.xs[None, :], self.ys[:, None], name, n, lo, hi, stretch, self.period)

    @property
    def xu(self):
        return np.broadcast_to(self.xs[None, :], (self.H_px, self.W_px))

    @property
    def yu(self):
        return np.broadcast_to(self.ys[:, None], (self.H_px, self.W_px))

    def _window(self, bounds, margin_u: float):
        x0, y0, x1, y1 = bounds
        x0, x1, y0, y1 = x0 - self.ox, x1 - self.ox, y0 - self.oy, y1 - self.oy
        a = max(int(math.floor((x0 - margin_u) * PPU)), 0)
        b = max(int(math.floor((y0 - margin_u) * PPU)), 0)
        c = min(int(math.ceil((x1 + margin_u) * PPU)), self.W_px)
        d = min(int(math.ceil((y1 + margin_u) * PPU)), self.H_px)
        if a >= c or b >= d:
            return None
        return slice(b, d), slice(a, c)

    def _store(self, s, m, h, mat, tint):
        self.h[s] = np.where(m, h, self.h[s])
        self.a[s] = np.where(m, 1.0, self.a[s])
        if mat is not None:
            self.mid[s] = np.where(m, MAT_ID[mat], self.mid[s])
        if tint is not None:
            if self.tint is None:
                self.tint = np.ones((self.H_px, self.W_px, 3), dtype=F32)
            t = U.token(tint) if isinstance(tint, str) else np.asarray(tint, dtype=F32)
            self.tint[s] = np.where(m[..., None], t, self.tint[s])

    def _distance(self, shape, s, seed: str, wobble: float, nick: float, wob_scale: float) -> np.ndarray:
        X, Y = self.xs[s[1]][None, :], self.ys[s[0]][:, None]
        if wobble > 0:
            X = X + wobble * waves(X, Y, seed + ".wx", 4, wob_scale, wob_scale * 2.4)
            Y = Y + wobble * waves(X, Y, seed + ".wy", 4, wob_scale, wob_scale * 2.4)
        d = shape.sdf(X, Y)
        if nick > 0:
            d = d + nick * waves(X, Y, seed + ".nick", 5, 0.5, 1.8)
        return d

    def plate(self, shape, z=0.0, bevel=0.6, profile="round", tilt=(0.0, 0.0), mat=None, tint=None, mode="set",
              rise=1.0, wobble=0.0, nick=0.0, wob_scale=6.0, seed=""):
        """Plate from a shape (Rect, Ellipse, Poly, Capsule) or a point list. wobble warps the outline."""
        if not hasattr(shape, "sdf"):
            shape = Poly(shape)
        s = self._window(shape.bounds(), wobble * 2 + nick * 2 + 0.5)
        if s is None:
            return
        d = self._distance(shape, s, seed or f"plate.{shape.bounds()}", wobble, nick, wob_scale)
        m = d > 0
        h = z + _profile(np.clip(d / max(bevel, 1e-4), 0, 1), profile) * bevel * rise
        if tilt != (0.0, 0.0) and m.any():
            xs, ys = self.xs[s[1]][None, :], self.ys[s[0]][:, None]
            h = h + (xs - xs.mean()) * tilt[0] + (ys - ys.mean()) * tilt[1]
        if mode == "max":
            m = m & ((h > self.h[s]) | (self.a[s] < 0.5))
        self._store(s, m, h, mat, tint)

    def mask_plate(self, m, z=0.0, bevel=0.6, profile="round", mat=None, tint=None, mode="set", rise=1.0):
        """Plate from a boolean mask; costs a jump-flood SDF, so prefer shapes for big areas."""
        ys, xs = np.nonzero(m)
        if not len(ys):
            return
        s = (slice(max(ys.min() - 2, 0), min(ys.max() + 3, self.H_px)), slice(max(xs.min() - 2, 0), min(xs.max() + 3, self.W_px)))
        cm = m[s]
        d = np.maximum(U.sdf(cm.astype(F32)), 0) / PPU
        h = z + _profile(np.clip(d / max(bevel, 1e-4), 0, 1), profile) * bevel * rise
        if mode == "max":
            cm = cm & ((h > self.h[s]) | (self.a[s] < 0.5))
        self._store(s, cm, h, mat, tint)

    def sweep(self, pts, radii, z=0.0, mat=None, tint=None, flatten=1.0, step_px=3.0, mode="max"):
        """Sphere sweep along a polyline: tubes, horns, cords, ridges."""
        pts = np.array(pts, dtype=F32)
        rad = np.full(len(pts), radii, dtype=F32) if np.isscalar(radii) else np.array(radii, dtype=F32)
        zs = np.full(len(pts), z, dtype=F32) if np.isscalar(z) else np.array(z, dtype=F32)
        seg = np.hypot(*(pts[1:] - pts[:-1]).T)
        t = np.concatenate(([0], np.cumsum(seg)))
        n = max(2, int(t[-1] * PPU / step_px))
        ts = np.linspace(0, t[-1], n)
        for x, y, r, z0 in zip(np.interp(ts, t, pts[:, 0]), np.interp(ts, t, pts[:, 1]), np.interp(ts, t, rad), np.interp(ts, t, zs)):
            if r <= 0.02:
                continue
            s = self._window((x - r, y - r, x + r, y + r), 0.05)
            if s is None:
                continue
            d2 = (self.xs[s[1]][None, :] - x) ** 2 + (self.ys[s[0]][:, None] - y) ** 2
            ins = d2 < r * r
            h = z0 + np.sqrt(np.maximum(r * r - d2, 0)) * flatten
            if mode == "max":
                ins = ins & ((h > self.h[s]) | (self.a[s] < 0.5))
            self._store(s, ins, h, mat, tint)

    def pocket(self, shape, depth, wall=0.9, mat=None, wobble=0.0, nick=0.0, wob_scale=6.0, seed="", floor_rough=None):
        """Sink a shape below the surrounding height, with a sloped wall of the given width."""
        s = self._window(shape.bounds(), wobble * 2 + nick * 2 + 0.5)
        if s is None:
            return
        d = self._distance(shape, s, seed or f"pocket.{shape.bounds()}", wobble, nick, wob_scale)
        m = d > 0
        k = smooth(0.0, 1.0, np.clip(d / max(wall, 1e-4), 0, 1))
        self.h[s] = np.where(m, self.h[s] - depth * k, self.h[s])
        if mat is not None:
            self.mid[s] = np.where(m & (k > 0.55), MAT_ID[mat], self.mid[s])

    def dome(self, cx, cy, r, z=0.0, flat=0.78, mat=None, seed="", facets=3, collar=True):
        """Forged rivet or boss: a flattened dome with hammer facets, an irregular rim and a low collar."""
        rr = r * 1.45 if collar else r * 1.15
        s = self._window((cx - rr, cy - rr, cx + rr, cy + rr), 0.1)
        if s is None:
            return
        rg = U.rng(seed or f"dome.{cx:.2f}.{cy:.2f}")
        X, Y = self.xs[s[1]][None, :] - cx, self.ys[s[0]][:, None] - cy
        th = np.arctan2(Y, X)
        wob = 1 + 0.07 * np.cos(th * 2 + rg.random() * TAU) + 0.05 * np.cos(th * 3 + rg.random() * TAU) + 0.03 * np.cos(th * 5 + rg.random() * TAU)
        d = np.sqrt(X * X + Y * Y) / wob
        if collar:
            cm = d < r * 1.40
            ch = z + 0.22 * (1 - smooth(r * 1.0, r * 1.40, d)) * 1.0 + 0.0
            self._store(s, cm & (d >= r * 0.92), ch, mat, None)
        ins = d < r
        top = z + (0.22 if collar else 0.0)
        hh = top + np.sqrt(np.maximum(r * r - d * d, 0)) * flat
        cap = top + r * flat * 0.86  # hammered flat on top
        hh = np.minimum(hh, cap) - 0.04 * np.clip(d / r, 0, 1) ** 4
        for _ in range(facets):  # a few shallow planar hammer strikes
            a = rg.random() * TAU
            off = (0.35 + 0.5 * rg.random()) * r
            side = (X * math.cos(a) + Y * math.sin(a) - off) / r
            hh = hh - np.clip(side, 0, 1) * 0.10 * r * flat
        self._store(s, ins, hh, mat, None)
        self.drips.append((cx, cy + r * 0.9, 4.5 + 5.5 * rg.random(), 0.55 + 0.35 * rg.random(), 0.7 + 0.3 * rg.random()))

    def drip(self, x, y, length, width=0.6, strength=1.0):
        self.drips.append((x, y, length, width, strength))

    # rendering ------------------------------------------------------------------------

    def _drip_mask(self) -> np.ndarray | None:
        if not self.drips:
            return None
        out = np.zeros((self.H_px, self.W_px), dtype=F32)
        for i, (x, y, length, width, strength) in enumerate(self.drips):
            s = self._window((x - width * 2.5, y, x + width * 2.5, y + length), 0.0)
            if s is None:
                continue
            X, Y = self.xs[s[1]][None, :], self.ys[s[0]][:, None]
            v = np.clip((Y - y) / length, 0, 1)
            sway = 0.25 * np.sin(Y * 1.7 + i * 2.1)
            w = width * (0.5 + 0.9 * v)
            m = np.exp(-(((X - x + sway) / w) ** 2)) * (1 - v) ** 1.4 * (Y >= y) * strength
            out[s] = np.maximum(out[s], m.astype(F32))
        return out

    def _detail(self, seed: str, detail: float) -> np.ndarray:
        """Secondary and tertiary relief: broad warp, flow, beaten facets, hammer dents, light pitting."""
        X, Y, per = self.xs[None, :], self.ys[:, None], self.period
        grit = _mat_lut("grit", 1)[:, 0][self.mid]
        grit = blur(grit, 0.3 * PPU, self.wrap)
        d = 0.15 * self._waves(seed + ".warp", 6, 6.0, 18.0) + 0.10 * self._waves(seed + ".flow", 6, 3.0, 9.0, (0.28, 1.0))
        d = d + facet_plane(X, Y, seed + ".facet", 6.5, 0.085, period=per) * grit
        d = d + 0.050 * cell_dents(X, Y, seed + ".dent1", 3.8, period=per) * grit
        d = d + 0.010 * cell_dents(X, Y, seed + ".dent2", 1.25, period=per) * grit
        d = d + 0.003 * cell_dents(X, Y, seed + ".pit", 0.45, 0.9, period=per) * grit
        return (d * detail).astype(F32)

    def surface(self, seed: str) -> Surface:
        mid = self.mid
        albedo = _mat_lut("albedo", 3)[mid]
        f0 = _mat_lut("f0", 3)[mid]
        lut_r = np.array([MATERIALS[n].rough for n in MATERIAL_NAMES], dtype=F32)
        lut_a = np.array([MATERIALS[n].aniso for n in MATERIAL_NAMES], dtype=F32)
        lut_br = np.array([MATERIALS[n].bare_rough for n in MATERIAL_NAMES], dtype=F32)
        lut_w = np.array([MATERIALS[n].wear for n in MATERIAL_NAMES], dtype=F32)
        # blur the property maps a hair so material borders do not alias
        albedo = blur(albedo, 0.35 * PPU * 0.3, self.wrap)
        f0 = blur(f0, 0.35 * PPU * 0.3, self.wrap)
        rough = blur(lut_r[mid], 0.35 * PPU * 0.3, self.wrap)
        mott = 0.5 + 0.5 * np.tanh(self._waves(seed + ".mott", 12, 2.5, 12.0) * 0.8)
        vary = blur(np.array([MATERIALS[n].vary for n in MATERIAL_NAMES], dtype=F32)[mid], 1.0, self.wrap)
        pat = 0.5 + 0.5 * np.tanh(self._waves(seed + ".patina", 9, 1.6, 7.0, (0.35, 1.0)) * 1.1)
        k = 1.0 - vary + 2.0 * vary * (0.5 * mott + 0.5 * pat)
        albedo = albedo * k[..., None]
        f0 = f0 * (0.5 + 0.5 * k)[..., None]
        rough = np.clip(rough + 0.12 * (mott - 0.5) + 0.04 * self._waves(seed + ".rgh", 5, 0.8, 3.0), 0.12, 1.0)
        if self.tint is not None:
            albedo = albedo * self.tint
        s = Surface(albedo.astype(F32), f0.astype(F32), rough.astype(F32), blur(lut_a[mid], 1.0, self.wrap), np.zeros_like(rough), self.a.copy())
        s.bare_albedo = _mat_lut("bare_albedo", 3)[mid]
        s.bare_f0 = _mat_lut("bare_f0", 3)[mid]
        s.bare_rough = lut_br[mid]
        s.wear_gain = blur(lut_w[mid], 1.0, self.wrap)
        return s

    def render(self, seed: str, wear=0.3, grime=0.5, rust=0.2, dust=0.1, exposure=1.0, value_map=None, rig: Rig = GOTHIC_RIG,
               detail=1.0, debug: dict | None = None) -> np.ndarray:
        """The whole chain at hi-res. Returns float32 HxWx4 sRGB-encoded straight RGBA.
        debug, if given, receives the baked Maps and the aged Surface under "maps" and "surface"."""
        prim = self.h
        h = prim + self._detail(seed, detail)
        m = bake(h, prim, rig.lights[0].dir, self.wrap)
        s = age(self.surface(seed), m, seed, wear, grime, rust, dust, streak=self._drip_mask(),
                origin=(self.ox, self.oy), period=self.period, wrap=self.wrap)
        if debug is not None:
            debug["maps"], debug["surface"] = m, s
        return finish(light(s, m, rig), s.alpha, exposure, value_map)

    def sprite(self, seed: str, **kw) -> np.ndarray:
        """render() then downsample(): uint8 RGBA at atlas density."""
        return downsample(self.render(seed, **kw), wrap=self.tile)

    def _band(self, a: int, b: int) -> "Relief":
        v = object.__new__(Relief)
        v.__dict__.update(self.__dict__)
        v.H_px, v.h, v.a, v.mid = b - a, self.h[a:b], self.a[a:b], self.mid[a:b]
        v.tint = None if self.tint is None else self.tint[a:b]
        v.ys, v.oy = self.ys[a:b], self.oy + a / PPU
        return v

    def sprite_banded(self, seed: str, band_u: float = 40.0, overlap_u: float = 10.0, **kw) -> np.ndarray:
        """sprite() in horizontal bands, for canvases too big to hold ~35 float planes at once.

        All noise is positional and every band carries overlap_u of context, so the bands match the
        whole render except where a shadow is longer than the overlap. Not for y-tiling sprites."""
        if "y" in self.tile:
            raise ValueError("sprite_banded cannot wrap on y")
        vm = kw.pop("value_map", None)
        step, ov = int(round(band_u * PPU)) // SS * SS, int(round(overlap_u * PPU)) // SS * SS
        out, y = [], 0
        while y < self.H_px:
            y1 = min(y + step, self.H_px)
            a, b = max(y - ov, 0), min(y1 + ov, self.H_px)
            hi = self._band(a, b).render(seed, value_map=None if vm is None else vm[a:b], **kw)
            out.append(downsample(hi, wrap=self.tile)[(y - a) // SS : (y1 - a) // SS])
            y = y1
        return np.concatenate(out, axis=0)


# aging --------------------------------------------------------------------------------


def age(s: Surface, m: Maps, seed: str, wear=0.3, grime=0.5, rust=0.2, dust=0.1, streak: np.ndarray | None = None,
        origin=(0.0, 0.0), period=(None, None), wrap=False) -> Surface:
    """Edge wear to bare metal, soot, rust, dust and file marks, all driven by the baked maps.

    Wear = convex edge of the primary form x a large-scale use field x a broken-up threshold, so it
    sits on some stretches of an edge and not others. Rust needs a cavity, soot a cavity or a drip.
    streak is an optional 0..1 mask of drips; origin is the canvas position in u and period the tile
    size in u (see Relief), so noise matches across bands and tiles.
    """
    H, W = s.alpha.shape
    out = Surface(s.albedo.copy(), s.f0.copy(), s.rough.copy(), s.aniso.copy(), s.tangent.copy(), s.alpha,
                  s.bare_albedo, s.bare_f0, s.bare_rough, s.wear_gain, s.emit)
    X = origin[0] + (np.arange(W, dtype=F32)[None, :] + 0.5) / PPU
    Y = origin[1] + (np.arange(H, dtype=F32)[:, None] + 0.5) / PPU
    wv = lambda name, n, lo, hi, st=(1.0, 1.0): waves(X, Y, seed + name, n, lo, hi, st, period)
    # forging direction follows the form: along the contour of the smoothed normal
    nxb, nyb = blur(m.normal[..., 0], 1.0 * PPU, wrap), blur(m.normal[..., 1], 1.0 * PPU, wrap)
    mag = np.hypot(nxb, nyb)
    tx = np.where(mag > 0.02, -nyb / np.maximum(mag, 1e-6), 1.0)
    ty = np.where(mag > 0.02, nxb / np.maximum(mag, 1e-6), 0.0)
    out.tangent = np.arctan2(ty, tx).astype(F32)
    gain = np.ones((H, W), dtype=F32) if s.wear_gain is None else s.wear_gain
    # wear: gate x edge x breakup
    use = 0.5 + 0.5 * np.tanh(wv(".use", 10, 4.0, 16.0) * 1.1)
    thr = 0.98 - wear * 0.8
    gate = smooth(thr - 0.10, thr + 0.22, use)
    grit = 0.5 + 0.5 * np.tanh(wv(".grit", 8, 0.9, 3.2) * 1.0)
    core = m.edge * gate * gain * (0.45 + 0.85 * grit) * (0.75 + 0.5 * np.clip(m.curvature, 0, 1))
    bare = smooth(0.40, 0.62, core)
    burnish = smooth(0.12, 0.45, core) * (1 - bare)
    streaks = _flow(X, Y, seed + ".file", period)
    if s.bare_albedo is not None:
        out.albedo = out.albedo * (1 - bare[..., None]) + s.bare_albedo * bare[..., None]
    out.f0 = out.f0 + (s.bare_f0 - out.f0) * (bare * 1.0 + burnish * 0.45)[..., None] if s.bare_f0 is not None else out.f0
    br = 0.40 if s.bare_rough is None else s.bare_rough
    out.rough = out.rough * (1 - bare) + (br + 0.12 * (streaks - 0.5)) * bare
    out.rough = out.rough * (1 - 0.34 * burnish)
    out.aniso = np.clip(out.aniso + 0.55 * (bare + burnish), 0, 1)
    # soot in cavities and drips, matte and dark
    drip = np.zeros((H, W), dtype=F32) if streak is None else streak
    soot = np.clip(smooth(0.18, 0.70, m.cavity * 1.5 + 0.4 * (1 - m.ao)) * grime + drip * 0.65 * grime, 0, 1)
    soot *= 1 - 0.7 * bare
    out.albedo *= (1 - 0.65 * soot)[..., None]
    out.f0 *= (1 - 0.55 * soot)[..., None]
    out.rough = out.rough * (1 - soot) + np.maximum(out.rough, 0.7) * soot
    # rust: cavities and under-rivet drips only
    rn = 0.5 + 0.5 * np.tanh(wv(".rust", 10, 2.0, 8.0) * 1.0)
    damp = smooth(0.66, 0.90, 0.5 + 0.5 * np.tanh(wv(".damp", 8, 8.0, 26.0) * 1.2))
    cav = smooth(0.50, 0.92, m.cavity)
    rthr = 0.62 - rust * 0.8
    rmask = smooth(rthr, rthr + 0.16, rn) * np.maximum(cav * damp, drip) * (1 - bare) * min(1.0, rust * 3)
    rcol = np.array(_tok("forge.rust"), dtype=F32) * (0.40 + 0.8 * rn[..., None])
    out.albedo = out.albedo * (1 - rmask[..., None]) + rcol * rmask[..., None]
    out.f0 = out.f0 * (1 - rmask[..., None]) + 0.03 * rmask[..., None]
    out.rough = out.rough * (1 - rmask) + 0.85 * rmask
    # dust on up-facing slopes
    up = np.clip(-m.normal[..., 1] * 1.6 - 0.15, 0, 1)
    dn = 0.5 + 0.5 * np.tanh(wv(".dust", 6, 1.0, 4.0) * 1.0)
    dmask = up * smooth(0.35, 0.8, dn) * dust * 2.2 * (1 - bare) * m.ao
    dcol = np.array(_tok("forge.dust"), dtype=F32)
    out.albedo = out.albedo * (1 - dmask[..., None]) + dcol * dmask[..., None]
    out.f0 = out.f0 * (1 - dmask[..., None]) + 0.04 * dmask[..., None]
    out.rough = out.rough * (1 - dmask) + 0.9 * dmask
    out.worn = np.clip(bare, 0, 1).astype(F32)
    out.albedo, out.f0 = out.albedo.astype(F32), out.f0.astype(F32)
    out.rough = np.clip(out.rough, 0.1, 1.0).astype(F32)
    return out


# light --------------------------------------------------------------------------------


def light(s: Surface, m: Maps, rig: Rig = GOTHIC_RIG) -> np.ndarray:
    """Linear HDR radiance: anisotropic GGX with Schlick Fresnel for every light, baked AO and key
    shadow, plus soft reflection panels so black metal still shows big shapes. HxWx3 float32."""
    n = m.normal
    nx, ny, nz = n[..., 0], n[..., 1], n[..., 2]
    nv = np.clip(nz, 1e-3, 1)
    rough = np.clip(s.rough, 0.06, 1.0)
    a = rough * rough
    td = np.cos(s.tangent) * nx + np.sin(s.tangent) * ny
    Tx, Ty, Tz = np.cos(s.tangent) - nx * td, np.sin(s.tangent) - ny * td, -nz * td
    tl = np.maximum(np.sqrt(Tx * Tx + Ty * Ty + Tz * Tz), 1e-6)
    Tx, Ty, Tz = Tx / tl, Ty / tl, Tz / tl
    Bx, By, Bz = ny * Tz - nz * Ty, nz * Tx - nx * Tz, nx * Ty - ny * Tx
    aspect = np.sqrt(1 - 0.9 * np.clip(s.aniso, 0, 1))
    ax, ay = a * aspect, a / aspect
    k = a / 2
    spec_occ = m.ao ** 0.8
    f0 = s.f0
    out = np.zeros(nx.shape + (3,), dtype=F32)
    for lt in rig.lights:
        L = np.array(lt.dir, dtype=F32)
        L /= np.linalg.norm(L)
        Hv = L + np.array((0, 0, 1), dtype=F32)
        Hv /= np.linalg.norm(Hv)
        nl = np.clip(nx * L[0] + ny * L[1] + nz * L[2], 0, 1)
        nh = np.clip(nx * Hv[0] + ny * Hv[1] + nz * Hv[2], 1e-3, 1)
        hx = Tx * Hv[0] + Ty * Hv[1] + Tz * Hv[2]
        hy = Bx * Hv[0] + By * Hv[1] + Bz * Hv[2]
        D = 1.0 / (math.pi * ax * ay * ((hx / ax) ** 2 + (hy / ay) ** 2 + nh * nh) ** 2)
        vis = 1.0 / (4 * (nl * (1 - k) + k) * (nv * (1 - k) + k))
        F = f0 + (1 - f0) * ((1 - float(Hv[2])) ** 5)
        spec = (D * vis * nl)[..., None] * F
        diff = (s.albedo / math.pi) * nl[..., None] * (1 - F)
        e = np.array(lt.color, dtype=F32) * lt.intensity
        shd = m.shadow if lt.shadow else np.ones_like(nl)
        out += (spec * (spec_occ * shd)[..., None] + diff * (shd * m.ao ** 0.5)[..., None]) * e
    # reflection panels (environment), blurred by roughness
    rx, ry = 2 * nz * nx, 2 * nz * ny
    fenv = f0 + (1 - f0) * ((1 - nv) ** 5)[..., None] * (1 - rough)[..., None]
    for p in rig.panels:
        wx, wy = p.wx + 0.5 * rough, p.wy + 0.5 * rough
        band = np.exp(-(((rx - p.cx) / wx) ** 2)) * np.exp(-(((ry - p.cy) / wy) ** 2))
        out += fenv * (band * p.intensity * spec_occ * (1 - 0.5 * rough))[..., None] * np.array(p.color, dtype=F32)
    amb = (s.albedo + 0.25 * f0) * np.array(rig.ambient, dtype=F32) * (m.ao ** 1.2)[..., None]
    out += amb * 8.0
    if s.emit is not None:
        out += s.emit
    return out.astype(F32)


# finish -------------------------------------------------------------------------------


def tonemap(x: np.ndarray) -> np.ndarray:
    """ACES-style filmic curve (Narkowicz fit), linear in, linear display out."""
    a, b, c, d, e = 2.51, 0.03, 2.43, 0.59, 0.14
    return np.clip((x * (a * x + b)) / (x * (c * x + d) + e), 0, 1)


def finish(hdr: np.ndarray, alpha: np.ndarray, exposure: float = 1.0, value_map: np.ndarray | None = None,
           bloom: float = 1.0, grade: bool = True) -> np.ndarray:
    """Bloom from values over 1, composition value map, warm highlights over cold shadows, filmic curve.
    Returns float32 HxWx4: sRGB-encoded straight RGBA at hi-res."""
    x = np.maximum(hdr, 0) * exposure
    if value_map is not None:
        x = x * np.asarray(value_map, dtype=F32)[..., None]
    if bloom > 0:
        ex = np.maximum(x - 1.0, 0) * alpha[..., None]
        if float(ex.max()) > 0:
            glow = np.zeros_like(x)
            for sigma_u, gain in ((0.20, 0.30), (0.70, 0.18), (2.00, 0.08)):
                for c in range(3):
                    glow[..., c] += blur(ex[..., c], sigma_u * PPU) * gain
            x = x + glow * bloom * (alpha > 0)[..., None]
    if grade:
        y = (0.2126 * x[..., 0] + 0.7152 * x[..., 1] + 0.0722 * x[..., 2])[..., None]
        w = smooth(0.01, 0.9, y)
        cold, warm = np.array((0.99, 1.00, 1.02), dtype=F32), np.array((1.06, 1.00, 0.88), dtype=F32)
        x = x * (cold * (1 - w) + warm * w)
    rgb = U.linear_to_srgb(tonemap(x))
    return np.dstack((rgb, alpha.astype(F32))).astype(F32)


# downsample ---------------------------------------------------------------------------


def _mitchell(x: np.ndarray, B: float = 1 / 3, C: float = 1 / 3) -> np.ndarray:
    x = np.abs(x)
    p1 = ((12 - 9 * B - 6 * C) * x ** 3 + (-18 + 12 * B + 6 * C) * x ** 2 + (6 - 2 * B)) / 6
    p2 = ((-B - 6 * C) * x ** 3 + (6 * B + 30 * C) * x ** 2 + (-12 * B - 48 * C) * x + (8 * B + 24 * C)) / 6
    return np.where(x < 1, p1, np.where(x < 2, p2, 0.0))


def downsample(rgba: np.ndarray, factor: int = SS, sharpen: float = SHARPEN, wrap: str = "", dither: bool = True) -> np.ndarray:
    """Premultiplied, linear-light Mitchell downsample to straight-alpha uint8 RGBA.

    wrap is "", "x", "y" or "xy" for tiling sprites. A light unsharp (sharpen) in linear light offsets the
    runtime minification from 4 to 2.25 px/u. Triangular dither at the 8-bit rounding breaks banding in dark
    gradients; it is deterministic."""
    H, W = rgba.shape[0] // factor, rgba.shape[1] // factor
    a = rgba[: H * factor, : W * factor]
    al = a[..., 3:4].astype(F32)
    pm = np.concatenate((U.srgb_to_linear(a[..., :3]) * al, al), axis=2).astype(F32)
    lo, hi = -2 * factor + factor // 2, 2 * factor + factor // 2 - 1
    offs = np.arange(lo, hi + 1)
    wts = _mitchell((offs + 0.5 - factor / 2) / factor)
    wts = (wts / wts.sum()).astype(F32)
    for axis, n, mode in ((0, H, "wrap" if "y" in wrap else "edge"), (1, W, "wrap" if "x" in wrap else "edge")):
        pad = [(0, 0)] * 3
        pad[axis] = (-lo, hi - factor + 1)
        p = np.pad(pm, pad, mode=mode)
        acc = None
        for o, w in zip(offs, wts):
            start = -lo + int(o)
            sl = [slice(None)] * 3
            sl[axis] = slice(start, start + n * factor, factor)
            t = p[tuple(sl)] * w
            acc = t if acc is None else acc + t
        pm = acc
    if sharpen > 0:
        sm = blur(pm[..., :3], 0.75, "x" in wrap and "y" in wrap)
        pm[..., :3] = pm[..., :3] + sharpen * (pm[..., :3] - sm)
    alpha = np.clip(pm[..., 3], 0, 1)
    rgb = np.clip(pm[..., :3], 0, None) / np.maximum(alpha, 1e-6)[..., None]
    rgb = U.linear_to_srgb(np.minimum(rgb, 1.0)) * 255.0
    if dither:
        r = U.rng("forge.dither")
        rgb = rgb + (r.random(rgb.shape, dtype=F32) + r.random(rgb.shape, dtype=F32) - 1.0)
    out = np.dstack((np.clip(rgb + 0.5, 0, 255), np.clip(alpha * 255 + 0.5, 0, 255))).astype(np.uint8)
    out[..., :3][alpha <= 0] = 0
    return out


# sample plate -------------------------------------------------------------------------

def _round_path(x0, y0, x1, y1, r, n=5):
    """Closed rounded-rectangle polyline starting at the top-left, clockwise."""
    pts = []
    for cx, cy, a0 in ((x1 - r, y0 + r, -90), (x1 - r, y1 - r, 0), (x0 + r, y1 - r, 90), (x0 + r, y0 + r, 180)):
        for k in range(n + 1):
            a = math.radians(a0 + 90 * k / n)
            pts.append((cx + r * math.cos(a), cy + r * math.sin(a)))
    pts.append(pts[0])
    return pts


SAMPLE_W, SAMPLE_H = 120.0, 48.0


def sample_plate() -> Relief:
    """One bar-like piece: a base slab, three overlapping forged plates, a socket with a heavy uneven lip,
    a raised spine, forged rivets and a worn brass inlay. Primary forms are 8..110 u."""
    r = Relief(SAMPLE_W, SAMPLE_H, "iron.black", 1.0)
    r.plate(Rect(0, 0, SAMPLE_W, SAMPLE_H), z=0.0, bevel=0.5, rise=0.0, seed="sample.base")
    r.plate(Rect(3, 4, 46, 40, 5), z=1.3, bevel=2.6, profile="rolled", rise=0.5, mat="iron.warm", wobble=0.14, nick=0.015, seed="sample.a")
    r.plate(Rect(41, 6, 76, 36, 4), z=2.5, bevel=2.4, profile="rolled", rise=0.5, mat="iron.cold", wobble=0.16, nick=0.015, seed="sample.b")
    r.plate(Rect(31, 18, 40, 12, 1.5), z=3.5, bevel=1.6, profile="rolled", rise=0.55, mat="iron", wobble=0.10, nick=0.015, seed="sample.c")
    # socket: a heavy lip, then a pocket into the plate
    r.plate(Rect(7, 8, 34, 32, 6), z=1.5, bevel=2.4, profile="rolled", rise=0.7, mat="iron", wobble=0.22, nick=0.03, seed="sample.lip")
    r.pocket(Rect(12.5, 13.5, 23, 21, 3.4), depth=3.4, wall=1.1, mat="void", wobble=0.16, nick=0.02, seed="sample.pocket")
    # raised forged bar along plate B
    r.plate(Rect(76, 20.2, 37, 7.6, 3.2), z=3.9, bevel=2.0, profile="rolled", rise=0.45, mat="iron.warm", wobble=0.10, nick=0.015, seed="sample.bar")
    for i, (x, y, rad) in enumerate(((7.2, 8.0, 1.8), (7.2, 40.0, 1.6), (44.5, 8.2, 1.7), (44.5, 39.8, 1.9), (46.5, 10.5, 1.7), (113.0, 10.0, 1.8), (113.0, 38.0, 1.6), (34.5, 24.0, 1.5), (67.5, 24.0, 1.7))):
        r.dome(x, y, rad, z=float(r.h[int(y * PPU), int(x * PPU)]), mat="iron.rivet", seed=f"sample.rivet{i}")
    # ivory-gold rails inlaid along plate B, broken into forged segments, and a bead round the socket
    def rail(pts, w, seed, notch=False):
        zb = float(r.h[int(pts[0][1] * PPU), int(pts[0][0] * PPU) + 6])
        r.pocket(Capsule(pts, w / 2 + 0.32), depth=0.55, wall=0.30, mat="void", wobble=0.04, seed=seed + ".g")
        shape = Capsule(pts, w / 2)
        if len(pts) == 2:  # straight segments end in forged points, not round caps
            (x0, y), (x1, _) = pts
            e = w * 0.9
            shape = Poly([(x0, y), (x0 + e, y - w / 2), (x1 - e, y - w / 2), (x1, y), (x1 - e, y + w / 2), (x0 + e, y + w / 2)])
        r.plate(shape, z=zb - 0.35, bevel=0.30, profile="chamfer", rise=1.0, mat="rail", wobble=0.05, nick=0.01, seed=seed)
        if notch:  # a few hand-cut cross notches and hairline cracks
            rg = U.rng(seed + ".n")
            (x0, y), (x1, _) = pts[0], pts[-1]
            xx = x0 + 3.0 + 3.0 * rg.random()
            while xx < x1 - 3.0:
                r.pocket(Rect(xx, y - w * 0.6, 0.40, w * 1.2), depth=0.30, wall=0.20, seed=f"{seed}.n{xx:.1f}")
                xx += 4.5 + 4.5 * rg.random()
            for _ in range(2):
                cx = x0 + 3.0 + (x1 - x0 - 6.0) * rg.random()
                tilt = (rg.random() - 0.5) * 1.2
                r.pocket(Capsule([(cx - tilt, y - w * 0.55), (cx + tilt * 0.4, y + w * 0.55)], 0.07), depth=0.22, wall=0.07, seed=f"{seed}.c{cx:.1f}")

    for i, (x0, x1, y) in enumerate(((50.0, 66.0, 11.4), (67.6, 88.0, 11.4), (89.4, 109.0, 11.4), (50.0, 71.0, 36.6), (72.6, 109.0, 36.6))):
        rail([(x0, y), (x1, y)], 3.0, f"sample.rail{i}", notch=True)
    rail([(11.0, 6.4), (37.0, 6.4)], 2.2, "sample.railA", notch=True)
    rail([(12.0, 41.7), (40.0, 41.7)], 2.2, "sample.railB", notch=True)
    ring = _round_path(10.9, 11.9, 37.1, 36.1, 4.5)
    n = len(ring)
    for i, (a0, a1) in enumerate(((0.00, 0.30), (0.33, 0.62), (0.65, 0.97))):
        rail(ring[int(a0 * n) : int(a1 * n) + 1], 2.0, f"sample.ring{i}")
    return r


def sample_value_map() -> np.ndarray:
    """Composition: dark at both ends and the top and bottom, brighter towards the middle."""
    H, W = int(SAMPLE_H * PPU), int(SAMPLE_W * PPU)
    x = (np.arange(W)[None, :] + 0.5) / PPU
    y = (np.arange(H)[:, None] + 0.5) / PPU
    vx = 0.58 + 0.42 * np.exp(-(((x - 62) / 46) ** 2))
    vy = 0.84 + 0.16 * np.exp(-(((y - 22) / 20) ** 2))
    return (vx * vy).astype(F32)


def sample_hires(seed: str = "forge.sample", **kw) -> np.ndarray:
    kw.setdefault("wear", 0.26)
    kw.setdefault("exposure", 1.0)
    kw.setdefault("rust", 0.28)
    return sample_plate().render(seed, value_map=sample_value_map(), **kw)


def sample_sprite(seed: str = "forge.sample", **kw) -> np.ndarray:
    return downsample(sample_hires(seed, **kw))


def main(out_root: Path) -> None:
    """Writes the sample plate as an atlas under out_root/forge/. Not part of build_all."""
    a = U.Atlas("forge", Path(out_root) / "forge")
    a.add(U.Sprite("forge.sample", sample_sprite()))
    a.save()


if __name__ == "__main__":
    out = Path(sys.argv[1]) if len(sys.argv) > 1 else HERE / "previews"
    U.save_png(sample_sprite(), out / "forge_sample.png")
    print("forge: wrote", out / "forge_sample.png")
