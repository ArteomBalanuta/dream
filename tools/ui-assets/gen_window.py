"""Window chrome for dream's classic MMO UI: dream/ui/window/window.png + window.json.

All art is original and procedural, rendered through forge.py: an authored relief (height and a material id per
pixel) is baked into maps, aged, lit in linear light with GGX, finished with a filmic curve and downsampled in
linear light. Layout and sprite names follow spec 01 (see 07 section A).

Art direction: dark, heavy, old. Cold blackened iron frame, a mottled and cracked stone slab, recessed iron wells,
a few worn brass accents. Wear sits on edges and grime in recesses; nothing is scattered.

Big sprites are authored in absolute u coordinates and rendered by region, so a band cut from a large canvas
shades exactly like the whole and the bands can be rendered in parallel processes.
"""
from __future__ import annotations

import dataclasses
import math
import os
import sys
import zlib
from concurrent.futures import ProcessPoolExecutor
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw

sys.path.insert(0, str(Path(__file__).resolve().parent))
import forge as fg  # noqa: E402
import uiart as ua  # noqa: E402
from forge import Capsule, Ellipse, Poly, Rect  # noqa: E402
from uiart import PPU, SS, Atlas, Sprite, token  # noqa: E402

OUT_DIR = "window"
SCALE = ua.SCALE
CHAIN_PERIOD = 5.5
TILE_H = 44
TILE_ANCHOR_Y = 41  # window y of a tile boundary, for both windows, so crown and bottom join
SLAB_W = 165
SLAB_H = {"inventory": 387, "combine": 388}


# helpers ------------------------------------------------------------------------------


def smooth(a, lo, hi):
    return fg.smooth(lo, hi, a)


def lerp(a, b, t):
    return a + (b - a) * t


def bez(*p, n=28):
    return ua.bezier(p[0], p[1], p[2], p[3], n)


def mirror_pts(pts, axis_x):
    return [(2 * axis_x - x, y) for x, y in pts]


def ribbon(pts, widths):
    """Closed polygon around a centreline. widths: one per point, or a function of 0..1."""
    n = len(pts)
    left, right = [], []
    for i, (x, y) in enumerate(pts):
        ax, ay = pts[max(i - 1, 0)]
        bx, by = pts[min(i + 1, n - 1)]
        dx, dy = bx - ax, by - ay
        ln = math.hypot(dx, dy) or 1
        w = widths(i / max(n - 1, 1)) if callable(widths) else widths[i]
        nx, ny = -dy / ln * w / 2, dx / ln * w / 2
        left.append((x + nx, y + ny))
        right.append((x - nx, y - ny))
    return left + right[::-1]


def rot(pts, deg, origin=(0, 0), at=(0, 0), flip=False):
    a = math.radians(deg)
    c, s = math.cos(a), math.sin(a)
    out = []
    for x, y in pts:
        x, y = (-x if flip else x) - origin[0], y - origin[1]
        out.append((x * c - y * s + at[0], x * s + y * c + at[1]))
    return out


# materials ----------------------------------------------------------------------------
# Added to forge's table at import, so every process that imports this module (including the pool
# workers) agrees on the ids. All numbers are linear light.


def _register(name: str, m: fg.Material) -> None:
    if name not in fg.MATERIALS:
        fg.MATERIALS[name] = m
        fg.MATERIAL_NAMES.append(name)
        fg.MAT_ID[name] = len(fg.MATERIAL_NAMES) - 1


STEEL_BARE = fg.STEEL_BARE
# the slab: cold slate, nearly matte; edges that wear show paler stone
_register("slab", fg.Material((0.0225, 0.0222, 0.0218), (0.030, 0.030, 0.030), 0.78, 0.0, (0.060, 0.058, 0.055), (0.045, 0.045, 0.045), 0.62, 0.55, 0.9, 0.04))
# a heavier, darker stone for recesses and fracture floors
_register("slab.dark", fg.Material((0.0105, 0.0105, 0.0105), (0.025, 0.025, 0.025), 0.82, 0.0, (0.030, 0.029, 0.028), (0.040, 0.040, 0.040), 0.7, 0.4, 0.8, 0.04))
# well floors and pewter relief: dark blued iron with a soft sheen
_register("well", fg.Material((0.0036, 0.0036, 0.0038), (0.028, 0.028, 0.030), 0.62, 0.15, *STEEL_BARE, 0.9, 0.7, 0.2))
_register("pewter", fg.Material((0.015, 0.015, 0.0165), (0.045, 0.045, 0.048), 0.52, 0.2, (0.024, 0.024, 0.026), (0.10, 0.10, 0.104), 0.42, 0.9, 0.4, 0.08))
_register("pewter.dark", fg.Material((0.008, 0.008, 0.009), (0.040, 0.040, 0.043), 0.56, 0.2, (0.016, 0.016, 0.017), (0.09, 0.09, 0.095), 0.45, 0.8, 0.5, 0.25))
# glyph metal: warm brass that has gone dull, with hot spots only where wear exposes it
# dim brass inlay for the frame rails: a bone-gold that has gone brown, not a polished trim
# the frame's iron wears to a dull grey, not to bright steel
for _frame, _iron in (("frame.iron", "iron"), ("frame.warm", "iron.warm"), ("frame.cold", "iron.cold"), ("frame.black", "iron.black"), ("frame.rivet", "iron.rivet")):
    _register(_frame, dataclasses.replace(fg.MATERIALS[_iron], rough=0.74, aniso=0.08, bare_f0=(0.115, 0.115, 0.118), bare_albedo=(0.012, 0.012, 0.013), bare_rough=0.55))
# bag cells and button faces: dark gunmetal that holds a faint bevel
_register("cell", fg.Material((0.0105, 0.0105, 0.0113), (0.016, 0.016, 0.017), 0.66, 0.0, (0.014, 0.014, 0.015), (0.07, 0.07, 0.073), 0.58, 0.6, 0.5, 0.03))
# the scroll rod: dark steel, its highlight a soft broken streak
_register("rod.steel", fg.Material((0.012, 0.012, 0.013), (0.085, 0.085, 0.09), 0.52, 0.45, (0.02, 0.02, 0.021), (0.14, 0.14, 0.145), 0.46, 0.5, 0.3, 0.30))
_register("rail.dim", fg.Material((0.075, 0.048, 0.019), (0.085, 0.066, 0.036), 0.52, 0.15, (0.14, 0.09, 0.04), (0.20, 0.15, 0.08), 0.42, 0.7, 0.3, 0.6))
_register("glyph.brass", fg.Material((0.034, 0.020, 0.0055), (0.26, 0.18, 0.062), 0.46, 0.2, (0.050, 0.031, 0.010), (0.46, 0.33, 0.14), 0.36, 0.8, 0.3, 0.10))
_register("glyph.ember", fg.Material((0.050, 0.020, 0.0030), (0.24, 0.12, 0.034), 0.48, 0.2, (0.075, 0.030, 0.006), (0.42, 0.24, 0.07), 0.38, 0.8, 0.3, 0.10))


# regions ------------------------------------------------------------------------------


def region_relief(x0: float, y0: float, x1: float, y1: float, margin: float, base: str, alpha: float = 1.0, tile: str = "") -> fg.Relief:
    """A Relief covering [x0,x1] x [y0,y1] u plus margin on every side, in absolute coordinates: xs and ys
    hold absolute u, so authored shapes and noise use the same numbers however the sprite is cut."""
    w, h = (x1 - x0) + 2 * margin, (y1 - y0) + 2 * margin
    r = fg.Relief(w, h, base, alpha, tile)
    r.ox, r.oy = x0 - margin, y0 - margin
    r.xs = r.xs + F32(r.ox)
    r.ys = r.ys + F32(r.oy)
    return r


F32 = np.float32


def trim(sprite: np.ndarray, margin: float) -> np.ndarray:
    m = int(round(margin * SCALE))
    return sprite[m : sprite.shape[0] - m, m : sprite.shape[1] - m] if m else sprite


def wv(r: fg.Relief, name: str, n: int, lo: float, hi: float, stretch=(1.0, 1.0), angle: float = 0.0) -> np.ndarray:
    """Sum of plane waves at the relief's absolute coordinates, optionally on rotated axes (angle in degrees,
    stretch < 1 elongates the pattern along that rotated axis)."""
    X, Y = r.xs[None, :], r.ys[:, None]
    if angle:
        a = math.radians(angle)
        c, s = math.cos(a), math.sin(a)
        X, Y = X * c + Y * s, -X * s + Y * c
    return fg.waves(X, Y, name, n, lo, hi, stretch, r.period)


def render_chain(r: fg.Relief, seed: str, primary: np.ndarray | None = None, value_map=None, wear=0.3, grime=0.5, rust=0.2,
                 dust=0.1, exposure=1.0, rig: fg.Rig = fg.GOTHIC_RIG, debug: dict | None = None) -> np.ndarray:
    """forge's chain with the authored relief taken as final (no generic detail pass): bake, age, light, finish.
    primary is the form without micro relief; wear follows it, so edges wear and dents do not."""
    m = fg.bake(r.h, r.h if primary is None else primary, rig.lights[0].dir, r.wrap)
    surf = r.surface(seed)
    surf.rough = fg.blur(surf.rough, 1.2 * PPU, r.wrap)  # forge's 1 u roughness waves read as a woven grid on smooth metal
    s = fg.age(surf, m, seed, wear, grime, rust, dust, streak=r._drip_mask(), origin=(r.ox, r.oy), period=r.period, wrap=r.wrap)
    if debug is not None:
        debug["maps"], debug["surface"] = m, s
    return fg.finish(fg.light(s, m, rig), s.alpha, exposure, value_map)


def micro(r: fg.Relief, name: str, amp: float = 1.0, facet: float = 0.045, dent: float = 0.03, dent2: float = 0.0) -> None:
    """Beaten facets and shallow dents added to the relief inside its coverage, with no forging flow."""
    X, Y = r.xs[None, :], r.ys[:, None]
    d = facet * fg.facet_plane(X, Y, name + ".f", 6.5, 0.085, 9.0, r.period)
    d = d + dent * fg.cell_dents(X, Y, name + ".d1", 3.8, 0.7, r.period)
    if dent2:
        d = d + dent2 * fg.cell_dents(X, Y, name + ".d2", 1.25, 0.7, r.period)
    r.h += (amp * d * (r.a > 0.5)).astype(F32)


def render_micro(r: fg.Relief, seed: str, amp: float = 1.0, **kw) -> np.ndarray:
    prim = r.h.copy()
    micro(r, seed, amp)
    return render_chain(r, seed, prim, **kw)


def rise(a: np.ndarray, lo: float, hi: float) -> np.ndarray:
    return fg.smooth(lo, hi, a)


def crack_path(name: str, p0, p1, rough: float, depth: int = 6, shrink: float = 0.80):
    """Midpoint displacement: a crack that wanders at every scale rather than zigzagging."""
    g = ua.rng(name)
    pts = [p0, p1]
    amp = rough
    for _ in range(depth):
        out = [pts[0]]
        for a, b in zip(pts, pts[1:]):
            dx, dy = b[0] - a[0], b[1] - a[1]
            k = float(g.normal()) * amp
            out.append(((a[0] + b[0]) / 2 - dy * k, (a[1] + b[1]) / 2 + dx * k))
            out.append(b)
        pts = out
        amp *= shrink
    return pts


def carve(r: fg.Relief, buf: np.ndarray, pts, width, depth, lip: float = 0.0) -> None:
    """Cut a V groove along a polyline into buf (positive = deeper). width and depth are numbers or functions of
    0..1 along the path; the deepest cut wins where segments overlap. lip raises a thin rim beside the cut."""
    n = len(pts) - 1
    for i, ((x0, y0), (x1, y1)) in enumerate(zip(pts[:-1], pts[1:])):
        wa = width(i / n) if callable(width) else width
        wb = width((i + 1) / n) if callable(width) else width
        da = depth(i / n) if callable(depth) else depth
        db = depth((i + 1) / n) if callable(depth) else depth
        reach = max(wa, wb) * (2.6 if lip else 1.0) + 0.1
        s = r._window((min(x0, x1) - reach, min(y0, y1) - reach, max(x0, x1) + reach, max(y0, y1) + reach), 0.0)
        if s is None:
            continue
        X, Y = r.xs[s[1]][None, :], r.ys[s[0]][:, None]
        ex, ey = x1 - x0, y1 - y0
        wx, wy = X - x0, Y - y0
        t = np.clip((wx * ex + wy * ey) / max(ex * ex + ey * ey, 1e-9), 0, 1)
        d = np.hypot(wx - ex * t, wy - ey * t)
        w, dp = lerp(wa, wb, t), lerp(da, db, t)
        cut = dp * np.clip(1 - d / w, 0, 1) ** 1.35
        if lip:
            cut = cut - lip * dp * np.exp(-(((d - w * 1.5) / (w * 0.7)) ** 2))
        buf[s] = np.maximum(buf[s], cut.astype(F32)) if not lip else np.where(np.abs(cut) > np.abs(buf[s]), cut, buf[s]).astype(F32)


def plates(X, Y, name: str, cell_u: float, slope: float, step: float, jitter: float = 0.9, warp: float = 0.0) -> np.ndarray:
    """Hard-edged facets: each jittered cell owns a tilted plane and a height step, so neighbours meet in
    creases and small ledges. Position only, so it matches across regions. Returns heights in u."""
    seed = ua.SEED ^ zlib.crc32(name.encode("utf-8"))
    if warp:
        X, Y = X + warp * fg.waves(X, Y, name + ".wx", 3, 6.0, 18.0), Y + warp * fg.waves(X, Y, name + ".wy", 3, 6.0, 18.0)
    ci, cj = np.floor(X / cell_u).astype(np.int64), np.floor(Y / cell_u).astype(np.int64)
    shape = np.broadcast_shapes(np.shape(X), np.shape(Y))
    best = np.full(shape, 1e9, dtype=F32)
    out = np.zeros(shape, dtype=F32)
    for dj in (-1, 0, 1):
        for di in (-1, 0, 1):
            ni, nj = ci + di, cj + dj
            px = (ni + 0.5 + (fg._hash01(ni, nj, seed, 1) - 0.5) * jitter) * cell_u
            py = (nj + 0.5 + (fg._hash01(ni, nj, seed, 2) - 0.5) * jitter) * cell_u
            d = ((X - px) ** 2 + (Y - py) ** 2).astype(F32)
            sx = (fg._hash01(ni, nj, seed, 3) - 0.5) * 2 * slope
            sy = (fg._hash01(ni, nj, seed, 4) - 0.5) * 2 * slope
            z0 = (fg._hash01(ni, nj, seed, 5) - 0.5) * 2 * step
            val = (z0 + sx * (X - px) + sy * (Y - py)).astype(F32)
            nearer = d < best
            best = np.where(nearer, d, best)
            out = np.where(nearer, val, out)
    return out


# forged slab --------------------------------------------------------------------------

SLAB_MARGIN = 8.0


def slab_fractures(kind: str) -> list[dict]:
    """Crack paths in slab coordinates, per window. Main fractures are deep and chipped; hairlines are not."""
    out = []
    if kind == "combine":
        out.append(dict(name="slab.c.main", a=(52, 268), b=(160, 366), rough=0.15, w0=0.55, d0=0.9, lip=0.30))
        out.append(dict(name="slab.c.second", a=(86, 238), b=(148, 330), rough=0.14, w0=0.30, d0=0.55, lip=0.20))
    else:
        out.append(dict(name="slab.i.main", a=(26, 338), b=(132, 386), rough=0.14, w0=0.46, d0=0.8, lip=0.28))
    out.append(dict(name=f"slab.{kind}.h1", a=(18, 60), b=(6, 150), rough=0.17, w0=0.18, d0=0.38, lip=0.0))
    out.append(dict(name=f"slab.{kind}.h2", a=(150, 140), b=(128, 232), rough=0.16, w0=0.17, d0=0.34, lip=0.0))
    out.append(dict(name=f"slab.{kind}.h3", a=(70, 150), b=(104, 192), rough=0.2, w0=0.16, d0=0.30, lip=0.0))
    return out


def terrace(t: np.ndarray, steps: float, riser: tuple = (0.58, 0.92)) -> np.ndarray:
    """A smooth field to stepped plateaus: flat treads and short risers, like beds of rock. 0..steps in height."""
    x = t * steps
    f = np.floor(x)
    return (f + rise(x - f, riser[0], riser[1])).astype(F32)


def fracture_zone(kind: str, X, Y) -> np.ndarray:
    """0..1 weight around the two main fractures of a slab."""
    zone = np.zeros(np.broadcast_shapes(np.shape(X), np.shape(Y)), dtype=F32)
    for f in slab_fractures(kind)[:2 if kind == "combine" else 1]:
        (ax, ay), (bx, by) = f["a"], f["b"]
        dx, dy = bx - ax, by - ay
        tt = np.clip(((X - ax) * dx + (Y - ay) * dy) / (dx * dx + dy * dy), -0.1, 1.1)
        dist = np.hypot(X - ax - tt * dx, Y - ay - tt * dy)
        zone = np.maximum(zone, np.exp(-((dist / 20.0) ** 2)))
    return zone.astype(F32)


def slab_tone(kind: str, X, Y, period=(None, None)):
    """Log albedo multiplier and the composition value map as functions of absolute position. Soft patches with
    fractal edges (domain-warped pink noise), a pool of torch light, soot in the corners and along the frame."""
    nm = f"slab.{kind}"
    H = SLAB_H[kind]
    Xw = X + 9 * fg.waves(X, Y, nm + ".wx", 5, 30, 90, (1, 1), period)
    Yw = Y + 9 * fg.waves(X, Y, nm + ".wy", 5, 30, 90, (1, 1), period)
    big = fg.waves(Xw, Yw, nm + ".pink", 16, 28, 130, (1, 1), period)
    small = fg.waves(X, Y, nm + ".pink2", 18, 3.0, 28, (1, 1), period)
    mid = fg.waves(X, Y, nm + ".m2", 12, 3.5, 14, (1, 1), period)
    hue = rise(fg.waves(X, Y, nm + ".hue", 8, 25, 80, (1, 1), period), 0.25, 0.95)
    zone = fracture_zone(kind, X, Y)
    lnum = -0.84 + 1.05 * big + 0.30 * small + 0.26 * mid + 0.30 * zone
    # lit stone is held back and soot is let run deep: pale patches read as marble next to black wells
    lnum = np.where(lnum > -0.75, -0.75 + 0.40 * (lnum + 0.75), -0.75 + 2.20 * (lnum + 0.75))
    de = np.minimum(np.minimum(X, SLAB_W - X), np.minimum(Y, H - Y))
    pool = np.exp(-(((X - 62) / 78) ** 2 + ((Y - 0.34 * H) / (0.34 * H)) ** 2))
    vm = (0.10 + 0.90 * rise(de, 0.0, 24.0)) * (0.62 + 0.55 * pool) * (0.74 + 0.26 * rise(Y, 0.0, 40.0))
    return lnum.astype(F32), hue.astype(F32), vm.astype(F32), zone


def slab_author(kind: str, x0: float, y0: float, x1: float, y1: float, margin: float = SLAB_MARGIN):
    """The slab relief for a region, and the composition value map. Everything is a function of absolute u."""
    r = region_relief(x0, y0, x1, y1, margin, "slab", 1.0)
    X, Y = r.xs[None, :], r.ys[:, None]
    H = SLAB_H[kind]
    nm = f"slab.{kind}"
    lnum, hue, vm, zone = slab_tone(kind, X, Y, r.period)
    # hewn rock: broad swells, and ridges whose crests catch the key light while their far sides sink into shade
    h = 0.34 * wv(r, nm + ".und1", 14, 30, 95, (0.85, 1.0), 25)
    h = h + 0.62 * wv(r, nm + ".und2", 16, 9, 34, (0.85, 1.0), -35)
    crest = 1 - np.abs(wv(r, nm + ".crest", 12, 10, 44, (0.7, 1.0), 30) * 0.45)
    h = h + 0.90 * np.clip(crest, 0, 1) ** 4 * (0.35 + 0.65 * rise(wv(r, nm + ".crestz", 6, 30, 90), -0.3, 0.8))
    # a zone of chiselled plates along the main fracture, tapering to nothing
    pl = plates(X, Y, nm + ".plates", 17.0, 0.05, 0.08, 0.92, 1.1)
    h = h + fg.blur(pl, 0.45 * PPU) * (1.0 * zone ** 1.5)
    prim = h.copy()
    # pitting in weathered clusters, not an even sprinkle
    cluster = rise(wv(r, nm + ".cl", 10, 10, 40), 0.35, 1.0)
    h = h - 0.16 * fg.cell_dents(X, Y, nm + ".pit1", 5.5, 0.9, r.period) * cluster
    # beaten facets and shallow dents; stone has no forging flow, so forge's own detail pass is not used
    h = h + 0.10 * fg.facet_plane(X, Y, nm + ".facet", 8.0, 0.085, 7.0, r.period)
    r.h[...] = h.astype(F32)
    # cracks
    buf = np.zeros_like(r.h)
    for f in slab_fractures(kind):
        pts = crack_path(f["name"], f["a"], f["b"], f["rough"])
        g = ua.rng(f["name"] + ".w")
        phase = float(g.random()) * math.tau
        wf = lambda t, f=f, phase=phase: f["w0"] * (0.55 + 0.45 * math.sin(math.pi * t) ** 0.7) * (0.8 + 0.25 * math.sin(t * 23 + phase))
        df = lambda t, f=f, phase=phase: f["d0"] * (0.5 + 0.5 * math.sin(math.pi * t) ** 0.6) * (0.85 + 0.2 * math.sin(t * 17 + phase))
        carve(r, buf, pts, wf, df, f["lip"])
        if f["lip"]:  # short branches off the main cracks
            for k in range(4):
                j = 12 + int(g.integers(0, max(len(pts) - 24, 1)))
                a = pts[j]
                ang = float(g.random()) * math.tau
                ln = 4 + float(g.random()) * 9
                sub = crack_path(f["name"] + f".b{k}", a, (a[0] + math.cos(ang) * ln, a[1] + math.sin(ang) * ln), 0.25, 4)
                carve(r, buf, sub, lambda t, w=f["w0"]: w * 0.40 * (1 - t) + 0.05, lambda t, d=f["d0"]: d * 0.45 * (1 - t) + 0.05)
    r.h -= buf
    prim = prim - buf
    # fine scratches: pale hairlines, mostly along the beds, drawn into the albedo
    gs = ua.rng(nm + ".scrape")
    sbuf = np.zeros_like(r.h)
    for i in range(int(SLAB_W * H / 90)):
        sx, sy = float(gs.random()) * SLAB_W, float(gs.random()) * H
        ang = (33 + float(gs.normal()) * 12) * math.pi / 180 if gs.random() < 0.7 else float(gs.random()) * math.tau
        ln = 2.5 + float(gs.random()) ** 2 * 12
        amp = 0.35 + 0.65 * float(gs.random())
        if not (x0 - margin - 14 < sx < x1 + margin + 14 and y0 - margin - 14 < sy < y1 + margin + 14):
            continue
        pts = [(sx + math.cos(ang) * ln * k / 3, sy + math.sin(ang) * ln * k / 3 + float(gs.normal()) * 0.10) for k in range(4)]
        carve(r, sbuf, pts, lambda t: 0.075, lambda t, a=amp: a * math.sin(math.pi * t) ** 0.5)
    cold, warm = np.array((0.975, 0.99, 1.035), F32), np.array((1.04, 1.0, 0.95), F32)
    m = np.exp(lnum + 0.9 * sbuf)[..., None] * (cold * (1 - hue[..., None]) + warm * hue[..., None])
    r.tint = m.astype(F32)
    # run-off: dark streaks from the crown and from the larger cracks
    g = ua.rng(nm + ".run")
    for i in range(14):
        x = float(g.random()) * SLAB_W
        top = float(g.random()) * (H * 0.55)
        r.drip(x, top, 18 + 55 * float(g.random()), 0.35 + 0.6 * float(g.random()), 0.20 + 0.35 * float(g.random()))
    r.prim = prim.astype(F32)
    return r, vm


SLAB_RENDER = dict(wear=0.12, grime=0.30, rust=0.10, dust=0.10, exposure=1.70)


def render_slab_region(kind: str, x0: float, y0: float, x1: float, y1: float) -> np.ndarray:
    r, vm = slab_author(kind, x0, y0, x1, y1)
    hi = render_chain(r, f"slab.{kind}", r.prim, vm, **SLAB_RENDER)
    return trim(fg.downsample(hi), SLAB_MARGIN)


# parallel rendering -------------------------------------------------------------------


def _job(spec):
    fn, args = spec
    return globals()[fn](*args)


def run_jobs(jobs: list[tuple]) -> list:
    """Runs (function name, args) jobs in order, in worker processes when more than one core is free.
    Every job is a pure function of its arguments, so the result does not depend on the worker count."""
    workers = int(os.environ.get("DREAM_ART_WORKERS", "0")) or min(4, os.cpu_count() or 1)
    if workers <= 1 or len(jobs) == 1:
        return [_job(j) for j in jobs]
    with ProcessPoolExecutor(max_workers=workers) as pool:
        return list(pool.map(_job, jobs))


def render_slab(kind: str, band: float = 24.0) -> np.ndarray:
    H = SLAB_H[kind]
    edges = [0.0]
    while edges[-1] < H:
        edges.append(min(edges[-1] + band, float(H)))
    parts = run_jobs([("render_slab_region", (kind, 0.0, a, float(SLAB_W), b)) for a, b in zip(edges[:-1], edges[1:])])
    return np.concatenate(parts, axis=0)


# side columns -------------------------------------------------------------------------
# 12 u wide, 44 u tall, tiling in y. Left to right (outer edge to slab): a rolled iron arris, a heavy channel with a
# plain chain in it, a bead, a broken brass inlay rail, a dark gutter. Quiet and dark: the frame must not shout.

SIDE_W = 12
RIGHT_ART_END = 10.4  # u, in the right tile: the art stops here so the filter spill never reaches the last 2 u
SIDE_VM = 0.74        # the frame stays darker than its own lights would make it


def rail_segment(r: fg.Relief, pts, w: float, z: float, seed: str, groove: float = 0.28, mat: str = "rail.dim") -> None:
    """Brass inlay: a groove cut round it, then the bar with forged points at straight ends. Top at z."""
    r.pocket(Capsule(pts, w / 2 + groove), depth=0.55, wall=0.28, mat="void", wobble=0.03, seed=seed + ".g")
    shape = Capsule(pts, w / 2)
    if len(pts) == 2:
        (x0, y0), (x1, y1) = pts
        ln = math.hypot(x1 - x0, y1 - y0)
        ux, uy = (x1 - x0) / ln, (y1 - y0) / ln
        nx, ny = -uy, ux
        e = min(w * 1.0, ln / 3)
        h = w / 2
        shape = Poly([(x0, y0), (x0 + ux * e + nx * h, y0 + uy * e + ny * h), (x1 - ux * e + nx * h, y1 - uy * e + ny * h), (x1, y1),
                      (x1 - ux * e - nx * h, y1 - uy * e - ny * h), (x0 + ux * e - nx * h, y0 + uy * e - ny * h)])
    r.plate(shape, z=z - 0.30, bevel=0.30, profile="chamfer", rise=1.0, mat=mat, wobble=0.04, nick=0.01, seed=seed)


def zat(r: fg.Relief, x: float, y: float) -> float:
    """Current height at a point in the relief's own u coordinates (canvases that start at the origin)."""
    return float(r.h[min(max(int(y * PPU), 0), r.H_px - 1), min(max(int(x * PPU), 0), r.W_px - 1)])


def draw_side(r: fg.Relief, shift: float = 0.0, h_u: float = TILE_H, base: bool = True) -> None:
    """Left column in a relief whose y axis is the tile axis moved by shift (window y = tile y + shift). Every shape
    is drawn once per tile period that touches [0, h_u], so it wraps in the tiling sprite and joins in crown and bottom.
    Few broad forms: an arris, a sunk channel with a chain in it, one bar and the broken brass rail."""
    if base:
        r.plate(Rect(0, -TILE_H, SIDE_W, h_u + 2 * TILE_H), z=0.0, bevel=0.4, rise=0.0, mat="frame.black", seed="side.base")
    ks = range(-1, int(math.ceil((h_u - shift) / TILE_H)) + 1)
    for k in ks:
        dy = shift + k * TILE_H
        r.plate(Rect(0, dy - 1, 2.2, TILE_H + 2, 0.8), z=1.0, bevel=1.5, profile="rolled", rise=0.6, mat="frame.warm", wobble=0.05, seed="side.arris")
        r.pocket(Rect(2.8, dy - 1, 5.2, TILE_H + 2), depth=0.7, wall=0.6, mat="frame.black", seed="side.channel")
        r.plate(Rect(8.4, dy - 1, 1.5, TILE_H + 2), z=1.0, bevel=0.75, profile="rolled", rise=0.7, mat="frame.iron", wobble=0.04, seed="side.bar")
        # chain: face-on links alternate with edge-on ones, 5.5 u apart
        y = dy + 1.4
        j = 0
        while y < dy + TILE_H + CHAIN_PERIOD:
            cx = 5.4
            if j % 2 == 0:
                r.plate(Ellipse(cx, y, 1.95, 2.65), z=0.4, bevel=1.1, profile="round", rise=1.0, mat="frame.cold", wobble=0.03, seed=f"side.link.{j}")
                r.pocket(Ellipse(cx, y, 0.7, 1.35), depth=1.4, wall=0.5, mat="void", seed=f"side.hole.{j}")
            else:
                r.plate(Capsule([(cx, y - 1.5), (cx, y + 1.5)], 0.7), z=0.9, bevel=0.65, profile="round", rise=1.0, mat="frame.iron", seed=f"side.edge.{j}")
            y += CHAIN_PERIOD * 0.5
            j += 1
    for k in ks:
        dy = shift + k * TILE_H
        rail_segment(r, [(10.9, dy + 2.0), (10.9, dy + 17.0)], 1.4, 1.1, "side.raila", groove=0.22)
        rail_segment(r, [(10.9, dy + 19.0), (10.9, dy + 41.5)], 1.4, 1.1, "side.railb", groove=0.22)


def side_sprite(right: bool) -> np.ndarray:
    w_u = 13 if right else 12
    base = fg.Relief(SIDE_W, TILE_H, "frame.black", 1.0, "y")
    draw_side(base)
    if not right:
        return fg.downsample(base.render("side.left", wear=0.04, grime=0.5, rust=0.10, dust=0.04, detail=0.40, value_map=side_value_map(base)), sharpen=0.0, wrap="y")
    r = fg.Relief(w_u, TILE_H, "frame.black", 1.0, "y")
    n = 11 * PPU
    r.h[:, :n] = base.h[:, :n][:, ::-1]
    r.mid[:, :n] = base.mid[:, :n][:, ::-1]
    e = int(RIGHT_ART_END * PPU)
    r.a[:, e:] = 0.0
    r.h[:, e:] = 0.0
    return fg.downsample(r.render("side.right", wear=0.04, grime=0.5, rust=0.10, dust=0.04, detail=0.40, value_map=side_value_map(r, True)), sharpen=0.0, wrap="y")


def side_value_map(r: fg.Relief, right: bool = False) -> np.ndarray:
    """Dark towards the slab: the gutter swallows light, the outer arris keeps a little."""
    x = r.xs[None, :] * np.ones((r.H_px, 1), F32)
    d_in = x if right else SIDE_W - x
    return ((0.60 + 0.40 * smooth(d_in, 0.0, 3.5)) * SIDE_VM).astype(F32)


def build_side() -> list[Sprite]:
    return [
        Sprite("win.side.left", side_sprite(False), tile="y", meta={"tileAnchorY": TILE_ANCHOR_Y * SCALE}),
        Sprite("win.side.right", side_sprite(True), tile="y", meta={"tileAnchorY": TILE_ANCHOR_Y * SCALE}),
    ]



# crowns and bottom --------------------------------------------------------------------


def taper(t: float) -> float:
    return math.sin(min(t * 1.25 + 0.12, 1.0) * math.pi / 2) * (1 - t) ** 0.55


def thorn(r: fg.Relief, p, w: float, z: float, seed: str, bevel: float = 1.4, rise_: float = 0.8, mat: str = "frame.iron", tip: float = 0.28, n: int = 20) -> None:
    """A forged hook: a curved bar that swells and tapers to a point, on top of whatever is below."""
    path = bez(*p, n=n)
    r.plate(Poly(ribbon(path, lambda t: tip + w * taper(t))), z=z, bevel=bevel, profile="rolled", rise=rise_, mat=mat, wobble=0.04, nick=0.008, seed=seed)


CROWN_PAD = 3


def mirror_half(r: fg.Relief, axis_u: float = 94.0, art_end_u: float = 177.0 + RIGHT_ART_END) -> None:
    """Copies everything left of the axis onto the right in height and material, and clears the art past art_end_u."""
    n = int(axis_u * PPU)
    for arr in (r.h, r.mid):
        arr[:, n : 2 * n] = arr[:, :n][:, ::-1]
    r.a[:, n : 2 * n] = r.a[:, :n][:, ::-1]
    r.a[:, int(art_end_u * PPU) :] = 0.0
    r.h[:, int(art_end_u * PPU) :] = 0.0
    r.drips = [d for d in r.drips if d[0] < axis_u] + [(2 * axis_u - d[0], d[1], d[2], d[3], d[4]) for d in r.drips if d[0] < axis_u]


def crown_relief(kind: str) -> fg.Relief:
    inv = kind == "inventory"
    h_u = 41 if inv else 40
    vt, lt = (8.5, 33.4) if inv else (6.2, 29.2)  # top and bottom of the title socket
    r = fg.Relief(190, h_u + CROWN_PAD, "frame.black", 1.0)
    draw_side(r, shift=TILE_ANCHOR_Y - TILE_H, h_u=h_u + CROWN_PAD, base=False)
    # the lintel, then the socket cut into it
    r.plate(Rect(11.0, 0.2, 90, h_u + CROWN_PAD, 1.5), z=1.0, bevel=2.6, profile="rolled", rise=0.55, mat="frame.warm", wobble=0.10, nick=0.012, seed=f"crown.{kind}.lintel")
    r.pocket(Rect(14.6, vt, 84.4, lt - vt + 1.4, 3.0), depth=3.4, wall=1.1, mat="void", wobble=0.12, nick=0.02, seed=f"crown.{kind}.socket")
    # lower plate: carries the socket's bottom lip and the arcs under the rod
    r.plate(Poly([(12, lt + 0.4), (28, lt - 0.3), (58, lt + 1.2), (97, lt + 1.0), (97, h_u + CROWN_PAD + 2), (12, h_u + CROWN_PAD + 2)]),
            z=1.5, bevel=2.2, profile="rolled", rise=0.6, mat="frame.cold", wobble=0.10, nick=0.015, seed=f"crown.{kind}.lower")
    # top beam with a broken brass inlay
    r.plate(Rect(12.5, 0.8, 83.5, 5.4, 1.6), z=2.0, bevel=1.5, profile="rolled", rise=0.55, mat="frame.iron", wobble=0.08, nick=0.015, seed=f"crown.{kind}.beam")
    zb = zat(r, 60, 3.6)
    for i, (x0, x1) in enumerate(((40.0, 56.0), (58.4, 76.0), (78.4, 94.0))):
        rail_segment(r, [(x0, 3.5), (x1, 3.5)], 1.25, zb + 0.15, f"crown.{kind}.rail{i}")
    # corner boss, with the brass disc and a stud
    r.plate(Ellipse(6.4, 6.4, 5.6, 5.6), z=2.4, bevel=2.3, profile="round", rise=0.9, mat="frame.iron", wobble=0.05, seed=f"crown.{kind}.boss")
    r.plate(Ellipse(6.4, 6.4, 3.3, 3.3), z=4.3, bevel=1.0, profile="round", rise=0.6, mat="rail.dim", wobble=0.03, seed=f"crown.{kind}.disc")
    r.dome(6.4, 6.4, 1.15, z=zat(r, 6.4, 6.4), mat="frame.rivet", collar=False, seed=f"crown.{kind}.stud")
    # thorns, overlapping mass over mass: the long hook over the socket corner, one down the column, a counter-thorn
    thorn(r, ((8.5, 5.2), (19, 3.2), (28.5, 7.5), (31.6, 18.0)), 5.4, 3.4, f"crown.{kind}.t1", 1.7, 0.85)
    thorn(r, ((4.8, 10.5), (3.8, 18.0), (7.0, 25.5), (14.0, lt - 1.6)), 4.6, 3.1, f"crown.{kind}.t2", 1.5, 0.8)
    thorn(r, ((13.0, 3.0), (23.0, 2.2), (32.0, 3.8), (40.0, 8.8)), 3.2, 3.6, f"crown.{kind}.t3", 1.2, 0.7, "frame.warm")
    thorn(r, ((12.0, 13.0), (16.5, 11.6), (21.0, 13.6), (23.0, 19.2)), 2.4, 3.9, f"crown.{kind}.t4", 1.0, 0.7)
    thorn(r, ((11.5, lt - 10.0), (15.0, lt - 11.6), (19.5, lt - 9.6), (21.6, lt - 4.6)), 2.4, 3.8, f"crown.{kind}.t5", 1.0, 0.7)
    # a brass line riding the long hook
    rail_segment(r, [(12.5, 5.9), (21.0, 5.6), (27.0, 9.4)], 0.9, zat(r, 19, 4.6) + 0.1, f"crown.{kind}.hookrail", mat="rail.dim")
    # shallow arcs under the rod, in brass
    for i, (a0, a1, lift) in enumerate(((36.0, 62.0, 3.0), (60.0, 90.0, 2.6))):
        base = lt + 4.2
        pts = bez((a0, base), (a0 + (a1 - a0) * 0.3, base + lift), (a0 + (a1 - a0) * 0.7, base + lift), (a1, base - 0.5), n=18)
        r.pocket(Capsule(pts, 0.95), depth=0.5, wall=0.3, mat="void", seed=f"crown.{kind}.arcg{i}")
        r.plate(Capsule(pts, 0.55), z=zat(r, 50, base + 1.0) - 0.05, bevel=0.3, profile="chamfer", mat="rail.dim", wobble=0.03, seed=f"crown.{kind}.arc{i}")
    # rivets along the lintel and the lower plate
    for i, (x, y) in enumerate(((13.6, h_u - 3.4), (24.0, h_u - 2.4), (61.0, lt + 4.1), (93.0, lt + 4.2), (45.0, 7.2), (14.0, lt - 2.4))):
        r.dome(x, y, 0.9, z=zat(r, x, y), mat="frame.rivet", seed=f"crown.{kind}.rv{i}", collar=True)
    mirror_half(r)
    # centre crest on the beam: a kite with a brass inlay, in the axis of the window
    r.plate(Poly([(94, 0.6), (101.5, 5.8), (94, 12.0), (86.5, 5.8)]), z=2.6, bevel=1.7, profile="rolled", rise=0.7, mat="frame.warm", wobble=0.06, nick=0.015, seed=f"crown.{kind}.crest")
    r.plate(Poly([(94, 3.0), (97.6, 5.8), (94, 9.0), (90.4, 5.8)]), z=4.0, bevel=0.6, profile="chamfer", mat="rail.dim", seed=f"crown.{kind}.crestinlay")
    return r


def crown_value_map(r: fg.Relief) -> np.ndarray:
    x = np.broadcast_to(r.xs[None, :], (r.H_px, r.W_px))
    y = np.broadcast_to(r.ys[:, None], (r.H_px, r.W_px))
    socket = fg.blur((r.mid == fg.MAT_ID["void"]).astype(F32), 0.5 * PPU)
    body = (0.60 + 0.40 * np.exp(-(((x - 94) / 62) ** 2))) * (0.86 + 0.14 * np.exp(-(((y - 20) / 18) ** 2))) * (1 - 0.6 * socket)
    d_in = np.where(x < SIDE_W, SIDE_W - x, x - 177.0)  # distance from the slab edge, in the two columns
    column = (0.60 + 0.40 * smooth(d_in, 0.0, 3.5)) * SIDE_VM
    return np.where((x < SIDE_W) | (x > 177.0), column, body).astype(F32)


FRAME_RENDER = dict(wear=0.12, grime=0.55, rust=0.14, dust=0.05, detail=0.6)


def render_crown(kind: str) -> np.ndarray:
    r = crown_relief(kind)
    img = fg.downsample(r.render(f"crown.{kind}", value_map=crown_value_map(r), **FRAME_RENDER))
    return img[: (41 if kind == "inventory" else 40) * SCALE]


def bottom_relief() -> fg.Relief:
    r = fg.Relief(190, 16 + 2, "frame.black", 0.0)
    r.plate(Rect(0, -TILE_H, SIDE_W, 16 + 2 * TILE_H), z=0.0, bevel=0.4, rise=0.0, mat="frame.black", seed="bottom.colbase")
    draw_side(r, shift=-23.0, h_u=18, base=False)
    # the closing beam, thick on the inside lip, and the arc that turns up into the column
    r.plate(Rect(11.5, 11.4, 84.5, 9.5, 1.4), z=1.2, bevel=2.4, profile="rolled", rise=0.6, mat="frame.warm", wobble=0.10, nick=0.015, seed="bottom.beam")
    thorn(r, ((1.8, 4.2), (8.5, 4.4), (14.5, 8.6), (17.0, 15.4)), 3.6, 3.0, "bottom.arc", 1.4, 0.8, "frame.iron", n=22)
    rail_segment(r, [(18.0, 13.6), (46.0, 13.6)], 1.2, zat(r, 30, 13.6) + 0.12, "bottom.rail0")
    rail_segment(r, [(48.4, 13.6), (70.0, 13.6)], 1.2, zat(r, 60, 13.6) + 0.12, "bottom.rail1")
    rail_segment(r, [(72.4, 13.6), (93.8, 13.6)], 1.2, zat(r, 80, 13.6) + 0.12, "bottom.rail2")
    for i, x in enumerate((19.0, 24.6, 30.2)):  # three short thorns along the beam edge
        thorn(r, ((x + 5.0, 12.6), (x + 3.2, 11.4), (x + 1.4, 11.6), (x - 0.4, 13.4)), 2.0 - 0.2 * i, 3.3, f"bottom.th{i}", 0.9, 0.7, n=14)
    for i, (x, y) in enumerate(((13.4, 16.2), (36.0, 16.4), (60.0, 16.4), (84.0, 16.4))):
        r.dome(x, y, 0.85, z=zat(r, x, y), mat="frame.rivet", seed=f"bottom.rv{i}", collar=True)
    mirror_half(r)
    return r


def render_bottom() -> np.ndarray:
    r = bottom_relief()
    img = fg.downsample(r.render("bottom", value_map=crown_value_map(r), **FRAME_RENDER))
    return img[: 16 * SCALE]


def blend_columns(sprite: np.ndarray, left: np.ndarray, right: np.ndarray, first_window_y: int, rows: int, at_bottom: bool) -> np.ndarray:
    """Pastes the side tiles' own pixels over the rows of a crown (its last rows) or a bottom (its first rows) in
    both columns, fading in towards the join, so the columns continue the tile exactly."""
    out = sprite.copy()
    H = sprite.shape[0]
    for i in range(rows):
        y_row = i if at_bottom else H - 1 - i
        wy = first_window_y + y_row // SCALE
        t_row = ((wy + (TILE_H - TILE_ANCHOR_Y)) % TILE_H) * SCALE + (y_row % SCALE)
        w = 1.0 - i / rows
        w = w * w * (3 - 2 * w)
        lw, rw, x0 = left.shape[1], right.shape[1], 177 * SCALE
        out[y_row, :lw, :3] = np.round(out[y_row, :lw, :3] * (1 - w) + left[t_row, :, :3] * w).astype(np.uint8)
        a = right[t_row, :, 3] > 0
        seg = out[y_row, x0 : x0 + rw, :3].astype(np.float32)
        seg[a] = seg[a] * (1 - w) + right[t_row, :, :3][a] * w
        out[y_row, x0 : x0 + rw, :3] = np.round(seg).astype(np.uint8)
    return out

# @@PARTS@@

# legacy-shape adapter ------------------------------------------------------------------
# The silhouettes and glyphs were drawn with a small height-field painter (polygons inflated by a blur, ellipsoids,
# grooves). The shapes are kept; this class paints the same way into plain arrays in the old height units, and
# finish_relief() turns them into a forge Relief, scaled to low relief, so they are lit like everything else.

STEEL, GOLD, AMBER, TAN, LEATHER = 1, 2, 3, 4, 5
HEIGHT_K = 0.14  # old units to u: the old art was dressed for a matcap and stood up to 15 high


def gauss(a: np.ndarray, sigma_px: float) -> np.ndarray:
    return fg.blur(a, sigma_px)


class Leg:
    """Height (old units), material id, value multiplier and coverage on a canvas of w_u x h_u."""

    def __init__(self, w_u: float, h_u: float):
        self.w_u, self.h_u = w_u, h_u
        self.h, self.w = int(round(h_u * PPU)), int(round(w_u * PPU))
        self.H = np.zeros((self.h, self.w), dtype=np.float32)
        self.M = np.zeros((self.h, self.w), dtype=np.int8)
        self.T = np.ones((self.h, self.w), dtype=np.float32)
        self.A = np.zeros((self.h, self.w), dtype=np.float32)

    def _window(self, x0, y0, x1, y1):
        px0, py0 = max(int(x0 * PPU) - 2, 0), max(int(y0 * PPU) - 2, 0)
        px1, py1 = min(int(x1 * PPU) + 3, self.w), min(int(y1 * PPU) + 3, self.h)
        return min(px0, self.w), min(py0, self.h), max(px1, 0), max(py1, 0)

    def _write(self, win, sel, height, mat):
        px0, py0, px1, py1 = win
        self.H[py0:py1, px0:px1][sel] = height[sel]
        self.M[py0:py1, px0:px1][sel] = mat
        self.A[py0:py1, px0:px1][sel] = 1

    def poly(self, pts, mat, z=0.0, h=0.8, r=0.8, on_top=False, ridge=0.0, belly=0.0):
        """Filled polygon inflated into a dome; r is edge roundness in u, z its base height, belly a wide curve."""
        if mat == STEEL and not belly and h >= 1.5:
            belly = 0.7
        xs, ys = [p[0] for p in pts], [p[1] for p in pts]
        win = self._window(min(xs), min(ys), max(xs), max(ys))
        px0, py0, px1, py1 = win
        if px0 >= px1 or py0 >= py1:
            return
        pad = int(r * PPU * (5 if belly else 2)) + 2
        lw, lh = px1 - px0 + 2 * pad, py1 - py0 + 2 * pad
        im = Image.new("L", (lw, lh), 0)
        ImageDraw.Draw(im).polygon([(x * PPU - px0 + pad, y * PPU - py0 + pad) for x, y in pts], fill=255)
        m = np.asarray(im, dtype=np.float32) / 255
        b = gauss(m, r * PPU * 0.55)
        t = np.clip(2 * b - 1, 0, 1)
        dome = np.sqrt(1 - (1 - t) ** 2)
        if belly:
            wide = gauss(m, r * PPU * 1.6)
            wide = wide / max(float(wide.max()), 1e-3)
            dome = dome * (1 - belly + belly * np.sqrt(np.clip(1 - (1 - wide) ** 2, 0, 1)))
        if ridge:
            core = gauss(m, r * PPU * 1.3)
            dome = dome + ridge * np.clip(2 * core - 1, 0, 1) ** 2
        height = (z + h * dome)[pad:-pad, pad:-pad]
        sel = m[pad:-pad, pad:-pad] > 0.5
        if on_top:
            height = height + self.H[py0:py1, px0:px1]
        self._write(win, sel, height, mat)

    def rect(self, x, y, w, h_u, mat, z=0.0, h=0.0, r=0.5):
        pts = [(x, y), (x + w, y), (x + w, y + h_u), (x, y + h_u)]
        if h == 0:
            win = self._window(x, y, x + w, y + h_u)
            px0, py0, px1, py1 = win
            sel = np.ones((py1 - py0, px1 - px0), dtype=bool)
            self._write(win, sel, np.full(sel.shape, z, dtype=np.float32), mat)
        else:
            self.poly(pts, mat, z, h, r)

    def _local(self, cx, cy, ext):
        win = self._window(cx - ext, cy - ext, cx + ext, cy + ext)
        px0, py0, px1, py1 = win
        px1, py1 = max(px0, px1), max(py0, py1)
        win = (px0, py0, px1, py1)
        yy, xx = np.mgrid[py0:py1, px0:px1].astype(np.float32)
        return win, (xx + 0.5) / PPU - cx, (yy + 0.5) / PPU - cy

    def ellipsoid(self, cx, cy, rx, ry, angle, mat, z=0.0, h=1.0):
        win, x, y = self._local(cx, cy, max(rx, ry) + 0.2)
        if x.size == 0:
            return
        a = math.radians(angle)
        u = x * math.cos(a) + y * math.sin(a)
        v = -x * math.sin(a) + y * math.cos(a)
        rho = (u / rx) ** 2 + (v / ry) ** 2
        self._write(win, rho < 1, z + h * np.sqrt(np.clip(1 - rho, 0, 1)), mat)

    def ring(self, cx, cy, rx, ry, thick, angle, mat, z=0.0, h=1.0):
        """Elliptical torus; thick is the tube width in u."""
        win, x, y = self._local(cx, cy, max(rx, ry) + thick)
        if x.size == 0:
            return
        a = math.radians(angle)
        u = x * math.cos(a) + y * math.sin(a)
        v = -x * math.sin(a) + y * math.cos(a)
        rho = np.sqrt((u / rx) ** 2 + (v / ry) ** 2)
        d = (rho - 1) * (rx + ry) / 2 / (thick / 2)
        self._write(win, np.abs(d) < 1, z + h * np.sqrt(np.clip(1 - d * d, 0, 1)), mat)

    def groove(self, pts, width, depth):
        xs, ys = [p[0] for p in pts], [p[1] for p in pts]
        win = self._window(min(xs) - width, min(ys) - width, max(xs) + width, max(ys) + width)
        px0, py0, px1, py1 = win
        if px0 >= px1 or py0 >= py1:
            return
        im = Image.new("L", (px1 - px0, py1 - py0), 0)
        ImageDraw.Draw(im).line([(x * PPU - px0, y * PPU - py0) for x, y in pts], fill=255, width=max(1, int(width * PPU)))
        m = gauss(np.asarray(im, dtype=np.float32) / 255, width * PPU * 0.25)
        self.H[py0:py1, px0:px1] -= m * depth

    def mirrored_about(self, axis_u: float) -> None:
        ax = int(round(axis_u * PPU))
        n = min(ax, self.w - ax)
        for arr in (self.H, self.M, self.T, self.A):
            arr[:, ax : ax + n] = arr[:, ax - n : ax][:, ::-1]
        self.A[:, ax + n :] = 0


def darken(R: Leg, pts, width, amount=0.25):
    """Paints a soft dark line into T: seams, slits, engraved lines."""
    xs, ys = [p[0] for p in pts], [p[1] for p in pts]
    px0, py0, px1, py1 = R._window(min(xs) - width, min(ys) - width, max(xs) + width, max(ys) + width)
    if px0 >= px1 or py0 >= py1:
        return
    im = Image.new("L", (px1 - px0, py1 - py0), 0)
    ImageDraw.Draw(im).line([(x * PPU - px0, y * PPU - py0) for x, y in pts], fill=255, width=max(1, int(width * PPU)), joint="curve")
    m = gauss(np.asarray(im, dtype=np.float32) / 255, width * PPU * 0.2)
    R.T[py0:py1, px0:px1] *= 1 - m * (1 - amount)


def seam(R: Leg, pts, width=0.5, depth=0.5, amount=0.2):
    R.groove(pts, width, depth)
    darken(R, pts, width, amount)


def rgba(h, w, colour, alpha=1.0) -> np.ndarray:
    c = token(colour) if isinstance(colour, str) else np.asarray(colour, dtype=np.float32)
    out = np.zeros((h, w, 4), dtype=np.float32)
    out[..., :3] = c
    out[..., 3] = alpha
    return out


def finish_relief(R: Leg, mats: dict, base: str = "pewter") -> fg.Relief:
    """The painted arrays as a forge Relief: heights scaled to low relief, material from the id table."""
    r = fg.Relief(R.w_u, R.h_u, base, 0.0)
    r.h[...] = np.where(R.A > 0.5, R.H * HEIGHT_K, 0.0)
    r.a[...] = (R.A > 0.5).astype(np.float32)
    for k, name in mats.items():
        r.mid[R.M == k] = fg.MAT_ID[name]
    return r



# silhouettes: low relief in the well floor ----------------------------------------------

SIL_RENDER = dict(wear=0.16, grime=0.5, rust=0.05, dust=0.04, exposure=1.0)


def sil_finish(R: Leg, peak: float = 1.0, name: str = "sil", fade: float = 0.4, sharp: float = 1.0, lift: float = 1.0) -> np.ndarray:
    """Worn pewter relief, lit like the rest of the window. peak and lift scale its value, fade darkens it towards
    the edges of the well, and a soft shadow falls to the lower right so the shape sits in the iron."""
    r = finish_relief(R, {STEEL: "pewter"})
    yy, xx = np.mgrid[0 : r.H_px, 0 : r.W_px].astype(np.float32)
    d = np.hypot((xx / r.W_px - 0.42) / 0.62, (yy / r.H_px - 0.36) / 0.72)
    vm = R.T * peak * lift * 0.78 * (1 - fade * smooth(d, 0.25, 1.05))
    hi = render_chain(r, f"sil.{name}", None, vm.astype(F32), **SIL_RENDER)
    return with_shadow(hi, 0.55, 0.9, 0.5, 0.60)


def with_shadow(img: np.ndarray, dx_u: float, dy_u: float, blur_u: float, opacity: float) -> np.ndarray:
    """Hi-res RGBA with a soft black shadow offset to the lower right, then downsampled to the atlas."""
    a = img[..., 3]
    dy, dx = int(dy_u * PPU), int(dx_u * PPU)
    sh = np.zeros_like(a)
    sh[dy:, dx:] = a[: a.shape[0] - dy, : a.shape[1] - dx]
    sh = fg.blur(sh, blur_u * PPU) * opacity
    alpha = a + sh * (1 - a)
    rgb = np.where(alpha[..., None] > 1e-5, (img[..., :3] * a[..., None] + 0.0 * sh[..., None] * (1 - a[..., None])) / np.maximum(alpha[..., None], 1e-5), 0.0)
    return fg.downsample(np.dstack((rgb, alpha)).astype(F32))



# silhouette and glyph shapes (painted with Leg, finished through forge) -------------------


def sil_helm() -> np.ndarray:
    R = Leg(40, 40)
    # mail aventail hanging from the rim, then the shell, then a pivoting visor
    R.poly([(9, 28), (29, 29), (33, 36), (22, 37.5), (7, 36)], STEEL, 0, 2.2, 2.4)
    for y in (31.5, 34.0):
        seam(R, bez((8, y), (15, y + 1.2), (24, y + 1.2), (32, y)), 0.4, 0.3)
    skull = bez((9, 30), (5.5, 15), (12, 3.6), (21.5, 3.4)) + bez((21.5, 3.4), (29, 3.6), (33, 10), (33, 18)) + [(30, 29), (20, 32)]
    R.poly(skull, STEEL, 0.5, 10, 11)
    # keel down the centre of the shell
    keel = bez((12.5, 12), (15, 6), (19, 3.0), (23.5, 3.0)) + bez((23.5, 3.2), (19.5, 4.6), (16.5, 8), (14.2, 13.5))
    R.poly(keel, STEEL, 9.2, 1.1, 1.0, on_top=False)
    # visor: an angular snout in two facets
    upper = [(15.5, 15.2), (22, 13.2), (30, 13.6), (35.6, 19.6), (25.5, 21.2), (16, 20.4)]
    lower = [(16, 20.8), (25.5, 21.6), (35.6, 20), (34, 25.4), (28.5, 31), (20.5, 31.6), (16.5, 26.5)]
    R.poly(lower, STEEL, 9.0, 3.4, 3.2)
    R.poly(upper, STEEL, 9.8, 3.2, 3.0)
    seam(R, [(17, 19.4), (22, 18.3), (28, 18.5), (34.5, 19.6)], 1.15, 1.8, 0.05)  # eye slit
    seam(R, [(26, 21.6), (27.4, 25), (27, 31)], 0.4, 0.4, 0.45)
    for x, y in ((19.5, 24), (22.5, 24.6), (20, 27), (23, 27.6), (21, 29.6), (30.5, 23.7), (29.8, 26.2), (32.5, 23.4)):
        R.ellipsoid(x, y, 0.42, 0.5, 0, STEEL, R.H[int(y * PPU), int(x * PPU)] - 1.0, 0.2)
        darken(R, [(x - 0.3, y), (x + 0.3, y)], 0.7, 0.04)
    R.ellipsoid(14.2, 19.4, 1.6, 1.6, 0, STEEL, 10.0, 1.2)
    return sil_finish(R, 0.96, "sil.helm")


def sil_armor() -> np.ndarray:
    R = Leg(40, 60)

    def both(pts, *a, **k):
        R.poly(pts, STEEL, *a, **k)
        R.poly(mirror_pts(pts, 20)[::-1], STEEL, *a, **k)

    # skirt plates, each in three lames, parting at the centre
    for lame, (y0, y1, x0, x1) in enumerate(((47, 56.6, 6.6, 19.0), (43.5, 50.5, 7.8, 19.2), (40, 46.5, 9.4, 19.4))):
        both([(x0 + 1.7, y0), (x1, y0), (x1 + 0.1, y1 - 2), (x1 - 3, y1), (x0 + 0.3, y1), (x0 - 0.5, y1 - 3)], 0.8 + lame * 1.1, 4.4, 2.6, belly=0.7)
    # one cuirass: a single swelling chest narrowing to the waist
    half = bez((20, 12.6), (15, 12.8), (8.6, 12), (8, 19)) + bez((8, 19), (8.4, 28), (12.2, 31), (12.1, 39.8))
    body = half + mirror_pts(half, 20)[::-1]
    R.poly(body, STEEL, 2.5, 13, 7, belly=0.92)
    seam(R, bez((12.2, 30), (16, 33.6), (24, 33.6), (27.8, 30)), 0.34, 0.5, 0.4)  # plackart edge
    # waist band
    band = [(10.8, 38.2), (20, 39.8), (29.2, 38.2), (29.2, 42), (20, 43.6), (10.8, 42)]
    R.poly(band, STEEL, 9.8, 1.5, 1.1)
    # neck gusset
    both(bez((20, 8.6), (17, 8.8), (14, 9.6), (12.2, 13.6)) + [(15.6, 15.6), (20, 16.2)], 10, 3, 1.7, belly=0.5)
    # pauldron, three stacked lames, the top one domed high
    both(bez((5.6, 26.5), (1.6, 21), (2.4, 14), (10.5, 13.5)) + [(13, 19), (8.5, 26)], 7, 5, 2.4, belly=0.8)
    both(bez((4.4, 22), (1.6, 17), (3.2, 11.5), (10.5, 11)) + [(14.2, 15), (9.5, 21.5)], 10, 5, 2.4, belly=0.8)
    both(bez((3.2, 17), (2.8, 11), (8.5, 6.2), (15.2, 8.8)) + [(15.4, 12.4), (10, 15.8), (6, 18.6)], 13, 6, 3, belly=0.9)
    seam(R, [(20, 16.5), (20, 30)], 0.3, 0.16, 0.6)
    R.ellipsoid(20, 41.7, 1.5, 1.5, 0, STEEL, 11.2, 1.1)
    for x, y in ((6.4, 12.2), (33.6, 12.2), (14.6, 39.9), (25.4, 39.9)):
        R.ellipsoid(x, y, 0.55, 0.55, 0, STEEL, R.H[int(y * PPU), int(x * PPU)] - 0.1, 0.4)
    return sil_finish(R, 1.22, "sil.armor", fade=0.32)


def sil_pants() -> np.ndarray:
    R = Leg(40, 40)
    for side in (0,):
        # greave stub and knee cop
        R.poly([(9.8, 28), (18.6, 28), (18, 37.2), (11.2, 37.2)], STEEL, 0.6, 3, 3)
        # thigh plate
        R.poly(bez((8.2, 8), (7.6, 15), (8, 22), (9.2, 28.8)) + [(19.4, 29.8), (19.8, 18), (20.2, 13)], STEEL, 1.8, 6.8, 6.8)
        R.poly(bez((8.6, 25), (7.2, 28), (9.2, 33), (14.2, 33)) + bez((14.2, 33), (19, 33), (20.4, 28.5), (19, 24.5)) + [(14, 23.4)], STEEL, 7, 3.2, 3)
        R.ellipsoid(14.1, 28.2, 2.3, 2.1, 0, STEEL, 9.2, 1.3)
        # fauld lame over the hip
        R.poly([(7.8, 7.6), (20.2, 8.4), (20.2, 14.6), (15, 16.8), (7.9, 13.8)], STEEL, 6.8, 2.2, 2)
    # belt
    R.poly([(7, 4.4), (20.2, 5), (20.2, 9), (7.1, 8.4)], STEEL, 8.6, 1.5, 1.2)
    R.mirrored_about(20)
    seam(R, [(20.0, 14.5), (20, 37.8)], 1.5, 2.8, 0.05)
    seam(R, [(9, 14.2), (15, 17.2), (20, 22), (25, 17.2), (31, 14.2)], 0.5, 0.5, 0.25)  # the V
    R.ellipsoid(20, 7, 2.1, 1.8, 0, STEEL, 9.8, 1)
    for x in (10.5, 15, 25, 29.5):
        R.ellipsoid(x, 7.1, 0.5, 0.5, 0, STEEL, 9.8, 0.3)
    return sil_finish(R, 0.97, "sil.pants")


def boot(R: Leg, ox, oy, z, s=1.0):
    def P(pts):
        return [(ox + x * s, oy + y * s) for x, y in pts]

    # sole, sabaton, ankle, shaft, cuff; toe to the left
    R.poly(P([(1.2, 28.6), (5, 27), (22.6, 27), (22.8, 30), (2.2, 30)]), STEEL, z, 1.2, 0.8)
    R.poly(P(bez((0.6, 28), (2.5, 23), (6, 21.4), (11.5, 19)) + bez((11.5, 19), (13, 16), (13.6, 12), (13.2, 6)) + [(24, 5)] + bez((24, 5), (23.6, 13), (24.2, 20), (23.4, 28))), STEEL, z + 0.6, 7.5, 5.6, belly=0.85)
    R.poly(P(bez((11.8, 3.6), (15, 1.6), (21, 1), (25.6, 2)) + [(25.2, 6.4)] + bez((25.2, 6.4), (21, 5.6), (16, 6), (12.4, 7.6))), STEEL, z + 7.4, 2.2, 1.6, belly=0.6)
    for a, b in (((5.6, 22.8), (8.2, 27.2)), ((10.8, 20), (13.2, 27.2))):
        seam(R, P(bez(a, (a[0] + 1.6, a[1] + 0.8), (b[0] - 0.6, b[1] - 2), b)), 0.3 * s, 0.22, 0.45)
    seam(R, P(bez((12.6, 17), (15.5, 18.6), (20, 18.6), (23.2, 17.4))), 0.5 * s, 0.5, 0.25)
    R.ellipsoid(ox + 19.3 * s, oy + 21.5 * s, 1.0 * s, 1.0 * s, 0, STEEL, z + 7.4, 0.5)
    R.poly(P([(21.2, 26.4), (25.8, 26.6), (26, 30), (21.4, 30)]), STEEL, z + 0.2, 1.8, 1.0)  # heel


def sil_boots() -> np.ndarray:
    R = Leg(40, 40)
    boot(R, 13.6, 2.2, 0.3, 0.9)
    R.T[:] = 0.66
    boot2 = Leg(40, 40)
    boot(boot2, 3.6, 7.2, 6.5, 0.97)
    sel = boot2.A > 0
    R.H[sel] = boot2.H[sel]
    R.A[sel] = 1
    R.T[sel] = boot2.T[sel]
    return sil_finish(R, 0.96, "sil.boots")


def gauntlet(R: Leg, at, deg, flip, z):
    def P(pts):
        return rot(pts, deg, (0, 0), at, flip)

    # local frame: wrist at the origin, fingers towards +y, thumb towards +x
    R.poly(P(bez((-4.6, -10.5), (-5, -7), (-3.8, -3), (-3.6, 0)) + [(3.6, 0)] + bez((3.6, 0), (3.8, -3), (5, -7), (4.6, -10.5))), STEEL, z, 3.6, 3.6)
    seam(R, P(bez((-4.6, -6.8), (-2, -5.8), (2, -5.8), (4.6, -6.8))), 0.42, 0.4, 0.3)
    R.poly(P([(-4.2, -1.2), (4.2, -1.2), (4.9, 5.4), (-4.9, 5.4)]), STEEL, z + 1.4, 3.6, 3.4)
    seam(R, P(bez((-4.2, 1.8), (-1.5, 2.6), (1.5, 2.6), (4.2, 1.8))), 0.4, 0.4, 0.3)
    for i, off in enumerate((-3.75, -1.25, 1.25, 3.75)):
        ln = (8.0, 9.4, 9.0, 7.4)[i]
        spread = (off * 0.1)
        for j in range(3):
            a = 5.0 + ln * j / 3
            b = 5.0 + ln * (j + 1) / 3 + 0.2
            wid = 2.75 - j * 0.34
            pts = [(off - wid / 2 + spread * a * 0.25, a), (off + wid / 2 + spread * a * 0.25, a), (off + (wid - 0.25) / 2 + spread * b * 0.25, b), (off - (wid - 0.25) / 2 + spread * b * 0.25, b)]
            if j == 2:
                pts = pts[:2] + [(off + spread * b * 0.25, b + 1.4)] + pts[3:] if False else pts[:2] + [(off + wid * 0.3 + spread * b * 0.25, b), (off + spread * b * 0.25, b + 1.5), (off - wid * 0.3 + spread * b * 0.25, b)]
            R.poly(P(pts), STEEL, z + 2.4 - j * 0.3, 1.5, 1.2)
    R.poly(P(bez((4.2, 0.4), (7.5, 2), (8.6, 5.5), (8.2, 9.2), n=14) + [(6.2, 8.6), (5.6, 5.6), (3.6, 3.8)]), STEEL, z + 3, 1.8, 1.5)


def sil_gloves() -> np.ndarray:
    R = Leg(40, 40)
    gauntlet(R, (27.4, 16.6), 36, False, 0.4)
    gauntlet(R, (12.8, 16.6), -36, True, 7.5)
    return sil_finish(R, 0.92, "sil.gloves")


def sil_weapon_left() -> np.ndarray:
    R = Leg(40, 60)
    # a quiver slung behind: tube, collar, stitched strap and a sheaf of fletched arrows
    def Q(a, b):
        return (11 + 0.42 * a + 0.91 * b, 53 - 0.91 * a + 0.42 * b)

    for off, top in ((-2.6, 45), (0, 48), (2.6, 44.5)):
        R.poly([Q(30, off - 0.4), Q(top, off - 0.4), Q(top, off + 0.4), Q(30, off + 0.4)], STEEL, 0.4, 0.9, 0.4)
        R.poly([Q(top - 5.5, off), Q(top - 3.8, off - 1.5), Q(top + 1.6, off - 1.4), Q(top, off), Q(top + 1.6, off + 1.4), Q(top - 3.8, off + 1.5)], STEEL, 0.8, 1.5, 0.8)
        seam(R, [Q(top - 4, off), Q(top + 0.4, off)], 0.22, 0.2, 0.5)
    R.poly([Q(0, -3.6), Q(2.4, -5.2), Q(33, -6), Q(33, 6), Q(2.4, 5.2), Q(0, 3.6)], STEEL, 1.0, 6, 4.4, belly=0.85)
    R.poly([Q(30.5, -6.5), Q(34, -6.5), Q(34, 6.5), Q(30.5, 6.5)], STEEL, 6.4, 1.5, 1.0)
    for a in (8, 20):
        R.poly([Q(a, -5.8), Q(a + 2.2, -5.8), Q(a + 2.2, 5.8), Q(a, 5.8)], STEEL, 6.2, 1.2, 0.7)
    R.T[:] = 0.68
    # the bow crosses in front: strung, belly to the upper right
    B = Leg(40, 60)
    up = bez((28, 27), (31, 16), (23, 7.5), (9.5, 5), n=40)
    low = bez((28, 27), (31.5, 36), (34, 45), (30.5, 55), n=40)
    B.poly(ribbon(up, lambda t: 3.3 - 2.2 * t), STEEL, 6, 2.2, 1.4, ridge=0.2)
    B.poly(ribbon(low, lambda t: 3.3 - 2.2 * t), STEEL, 6, 2.2, 1.4, ridge=0.2)
    B.poly(ribbon(bez((9.5, 5), (7.5, 4.6), (6.4, 6), (4.6, 5.4), n=12), lambda t: 1.2 - 0.8 * t), STEEL, 6, 1, 0.6)
    B.poly(ribbon(bez((30.5, 55), (30, 56.2), (31.2, 56.8), (30.8, 58), n=12), lambda t: 1.2 - 0.8 * t), STEEL, 6, 1, 0.6)
    B.poly(ribbon(bez((27.8, 22), (28.6, 25), (28.6, 29), (28.4, 32), n=12), lambda t: 4.3), STEEL, 7.6, 2.3, 1.3)
    for y in (23.2, 25.2, 27.2, 29.2, 31):
        seam(B, [(26, y + 0.2), (30.8, y)], 0.32, 0.4, 0.35)
    B.poly(ribbon([(9.5, 5), (30.5, 55)], lambda t: 0.36), STEEL, 7, 0.3, 0.2)
    sel = B.A > 0
    R.H[sel], R.T[sel], R.A[sel] = B.H[sel], B.T[sel], 1
    return sil_finish(R, 0.93, "sil.weapon.left")


def sil_weapon_right() -> np.ndarray:
    R = Leg(40, 60)
    # kite shield, dim, standing behind
    shield = bez((6, 13), (12, 8), (22, 7), (31, 12)) + bez((31, 12), (33.5, 26), (27.5, 41), (16.5, 54)) + bez((16.5, 54), (9, 42), (4.5, 27), (6, 13))
    R.poly(shield, STEEL, 0.2, 2.0, 3.0)
    inner = bez((8.2, 14.4), (13, 10.4), (22, 9.4), (29, 13.4)) + bez((29, 13.4), (31, 26), (25.8, 39), (16.6, 51)) + bez((16.6, 51), (10.5, 41), (6.6, 27), (8.2, 14.4))
    R.poly(inner, STEEL, 0.6, 3.4, 8)
    seam(R, [(18.3, 10), (17, 52)], 0.4, 0.4, 0.45)
    seam(R, bez((7.4, 24), (14, 22.5), (22, 22.5), (30.2, 24)), 0.4, 0.4, 0.45)
    R.T[:] = 0.5
    # a bearded axe laid over it: long haft to the lower left, blade towards the shield
    a, b = (28.5, 14), (10.5, 55)
    dx, dy = b[0] - a[0], b[1] - a[1]
    ln = math.hypot(dx, dy)
    ux, uy = dx / ln, dy / ln
    px_, py_ = -uy, ux

    def S(along, across):
        return (a[0] + ux * along + px_ * across, a[1] + uy * along + py_ * across)

    M = Leg(40, 60)
    M.poly(ribbon([S(-3.4, 0), S(ln, 0)], lambda t: 2.1 + 0.3 * t), STEEL, 6, 2.0, 1.2)
    for f, w in ((0.34, 3.0), (0.92, 3.2), (0.99, 3.6)):
        M.poly(ribbon([S(ln * f - 0.7, 0), S(ln * f + 0.7, 0)], lambda t: w), STEEL, 7.2, 1.2, 0.7)
    for f in (0.74, 0.78, 0.82, 0.86):
        seam(M, [S(ln * f, -1.5), S(ln * f, 1.5)], 0.32, 0.3, 0.4)
    blade = bez(S(-0.8, 1), S(-2.6, 5), S(-4.4, 6.4), S(-7, 10.6), n=14) + bez(S(-7, 10.6), S(-1, 13.8), S(7, 14), S(14.6, 10), n=18) + bez(S(14.6, 10), S(11.4, 7.4), S(8.6, 4.4), S(8.4, 1), n=14)
    M.poly(blade, STEEL, 6.4, 4.0, 2.6, belly=0.8)
    edge = bez(S(-5.4, 9.2), S(0, 11.6), S(6.4, 11.8), S(12.4, 8.9), n=20)
    seam(M, edge, 0.3, 0.45, 0.42)
    M.poly([S(0.8, -1), S(3.2, -7.6), S(6.4, -1)], STEEL, 7, 2.6, 1.1)
    M.poly([S(-1.5, -1.7), S(8.4, -1.7), S(8.4, 1.7), S(-1.5, 1.7)], STEEL, 8.6, 1.8, 1)
    for along in (1.2, 5.8):
        M.ellipsoid(*S(along, 0), 0.6, 0.6, 0, STEEL, 10, 0.4)
    sel = M.A > 0
    R.H[sel] = M.H[sel]
    R.T[sel] = M.T[sel]
    R.A[sel] = 1
    return sil_finish(R, 0.95, "sil.weapon.right")


def sil_pet() -> np.ndarray:
    """A coiled familiar: a small drake asleep in its own tail, head lifted over the coil."""
    R = Leg(40, 40)
    cx, cy = 20, 22.5
    turns = 2.1
    pts = []
    n = 150
    for i in range(n + 1):
        t = i / n
        ang = -0.5 + t * turns * math.tau
        rad = 14.6 - t * 11.6
        pts.append((cx + rad * math.cos(ang) * 1.08, cy + rad * math.sin(ang) * 0.94))
    R.poly(ribbon(pts, lambda t: 0.5 + 6.2 * min(t * 2.4, 1) ** 0.8), STEEL, 0.5, 2.7, 1.5, belly=0.7)
    # scutes along the spine
    for i in range(10, n - 2, 4):
        (x0, y0), (x1, y1) = pts[i - 1], pts[i + 1]
        ax, ay = x1 - x0, y1 - y0
        ln = math.hypot(ax, ay) or 1
        w = 0.5 + 2.4 * min(i / n * 2.4, 1) ** 0.8
        darken(R, [(pts[i][0] - ay / ln * w, pts[i][1] + ax / ln * w), (pts[i][0] + ay / ln * w, pts[i][1] - ax / ln * w)], 0.2, 0.62)
    # neck and head rising from the centre towards the upper right
    neck = bez((19.6, 23), (21, 18), (24.6, 14.4), (28.4, 12.6), n=20)
    R.poly(ribbon(neck, lambda t: 5.2 - 2.1 * t), STEEL, 3.6, 2.2, 1.4, belly=0.6)
    R.poly(bez((25.2, 12), (28, 8.6), (31.6, 8.4), (36.4, 11.4), n=12) + [(32.4, 14.2), (27.6, 16)], STEEL, 5.6, 2.2, 1.4, belly=0.6)
    R.poly(ribbon(bez((27.4, 10.4), (25.4, 8), (23.2, 6.4), (20.6, 6.2), n=14), lambda t: 2.0 * (1 - t) + 0.15), STEEL, 6.6, 1, 0.7)
    R.poly(ribbon(bez((29.6, 9.4), (28.4, 6.8), (26.8, 5), (24.6, 3.8), n=14), lambda t: 1.7 * (1 - t) + 0.15), STEEL, 6.2, 1, 0.7)
    seam(R, [(29.4, 12.4), (33.2, 12.2), (35.2, 11.8)], 0.3, 0.4, 0.4)
    R.ellipsoid(29.6, 10.8, 0.6, 0.45, 12, STEEL, 7.8, 0.3)
    img = sil_finish(R, 0.5, "sil.pet", fade=0.2, sharp=0.8, lift=1.5)
    # the faint pool of light behind it
    h, w = img.shape[0], img.shape[1]
    yy, xx = np.mgrid[0:h, 0:w].astype(np.float32)
    pool = np.exp(-(((xx / SCALE - 20) / 12) ** 2 + ((yy / SCALE - 21) / 12) ** 2))
    back = np.zeros((h, w, 4), np.uint8)
    back[..., :3] = (0x4A, 0x4A, 0x4D)
    back[..., 3] = (pool * 0.26 * 255).astype(np.uint8)
    return over_u8(img, back)


def over_u8(top: np.ndarray, bottom: np.ndarray) -> np.ndarray:
    """Straight-alpha uint8 RGBA, top over bottom."""
    ta, ba = top[..., 3:4].astype(np.float32) / 255, bottom[..., 3:4].astype(np.float32) / 255
    oa = ta + ba * (1 - ta)
    rgb = (top[..., :3] * ta + bottom[..., :3] * ba * (1 - ta)) / np.maximum(oa, 1e-6)
    return np.dstack((np.clip(rgb + 0.5, 0, 255), np.clip(oa * 255 + 0.5, 0, 255))).astype(np.uint8)


def feather(R, root, ang, ln, wid, curl, z):
    a = math.radians(ang)
    ux, uy = math.cos(a), math.sin(a)
    nx, ny = -uy, ux
    tip = (root[0] + ux * ln + nx * curl, root[1] + uy * ln + ny * curl)
    p = bez(root, (root[0] + ux * ln * 0.4, root[1] + uy * ln * 0.4), (root[0] + ux * ln * 0.75 + nx * curl * 0.3, root[1] + uy * ln * 0.75 + ny * curl * 0.3), tip, n=26)
    R.poly(ribbon(p, lambda t: 0.6 + wid * math.sin(math.pi * t**1.5) ** 0.55), STEEL, z, 1.7, 1.3, belly=0.5)
    seam(R, p[1:24], 0.22, 0.12, 0.72)


def sil_wings() -> np.ndarray:
    """One spread pair: arms rising from two separate shoulders, primaries sweeping out and down."""
    R = Leg(60, 40)
    arm = bez((24.8, 24), (24, 10), (18, 4.2), (9.6, 3.6), n=40)
    # flight feathers hang from the arm, outermost first so each inner one overlaps it
    for i in range(10):
        root = arm[int((0.99 - i * 0.094) * 40)]
        tip_y = 30 + 7 * math.sin(i / 9 * math.pi / 2)
        feather(R, (root[0], root[1] + 0.6), 99 - i * 1.6, tip_y - root[1], 5.0, 1.8, 0.3 + i * 0.42)
    # a row of coverts along the arm
    for i in range(8):
        root = arm[int((0.95 - i * 0.12) * 40)]
        feather(R, root, 124 - i * 6, 9.6, 4.0, 1.2, 4.8 + i * 0.3)
    R.poly(ribbon(arm, lambda t: 3.8 - 2.7 * t), STEEL, 7, 2.4, 1.3, belly=0.5)
    R.mirrored_about(30)
    return sil_finish(R, 0.5, "sil.wings", fade=0.12, sharp=0.85, lift=1.75)


def sil_pendant() -> np.ndarray:
    R = Leg(20, 20)
    # a teardrop loop of links whose point meets the charm at the lower left
    n = 26
    pts = []
    for i in range(n):
        t = i / n * math.tau
        a = 5.4 * math.sin(t) * math.sin(t / 2) ** 1.3
        b = -6.4 * math.cos(t)
        pts.append((11.6 + (a * -0.707 - b * -0.707), 8.2 + (a * -0.707 + b * -0.707)))
    for i, (x, y) in enumerate(pts):
        nx, ny = pts[(i + 1) % n]
        ang = math.degrees(math.atan2(ny - y, nx - x))
        R.ellipsoid(x, y, 0.9, 0.56, ang, STEEL, 0.2 + (i % 2) * 0.25, 0.65)
    R.poly(bez((6.9, 11.4), (4.4, 11.8), (2.8, 14.2), (3.6, 16.4)) + bez((3.6, 16.4), (6.6, 17.2), (8.8, 15), (8.2, 12.2)), STEEL, 0.4, 2.6, 1.5, belly=0.8)
    return sil_finish(R, 0.5, "sil.pendant", fade=0.0, sharp=0.8)


def sil_ring() -> np.ndarray:
    R = Leg(20, 20)
    yy, xx = np.mgrid[0:R.h, 0:R.w].astype(np.float32)
    x, y = (xx + 0.5) / PPU, (yy + 0.5) / PPU
    outer = ((x - 9.7) / 5.6) ** 2 + ((y - 10) / 6.7) ** 2 < 1
    # the hole sits off to the right: heavy band at the left, a thin one turning away, a C
    inner = ((x - 11.5) / 2.9) ** 2 + ((y - 9.9) / 4.1) ** 2 < 1
    m = (outer & ~inner).astype(np.float32)
    b = gauss(m, 0.8 * PPU)
    dome = np.sqrt(1 - (1 - np.clip(2 * b - 1, 0, 1)) ** 2)
    wide = gauss(m, 1.5 * PPU)
    R.H = (0.1 + 3.0 * dome * (0.4 + 0.6 * wide / float(wide.max()))).astype(np.float32) * m
    R.A = m
    R.M[m > 0.5] = STEEL
    R.T *= 0.5 + 0.5 * smooth(-(x - 15.0) + np.abs(y - 10) * 0.7, -1.0, 1.4)
    return sil_finish(R, 0.9, "sil.ring", fade=0.0)


def glyph_close(hover) -> np.ndarray:
    R = Leg(18, 18)
    w = lambda t: 4.0 + 1.3 * abs(2 * t - 1) ** 1.5  # arms widen slightly to their ends
    first = [(3.2 + 11.6 * i / 16, 3.2 + 11.6 * i / 16) for i in range(17)]
    second = [(14.8 - 11.6 * i / 16, 3.2 + 11.6 * i / 16) for i in range(17)]
    cross = Leg(18, 18)
    for arm in (first, second):
        cross.poly(ribbon(arm, w), AMBER, 0, 1.0, 0.1)
    m = (cross.A > 0.5).astype(np.float32)
    rounded = (gauss(m, 0.7 * PPU) > 0.5).astype(np.float32)  # eases every corner of the cross
    b = gauss(rounded, 0.8 * PPU)
    dome = np.sqrt(1 - (1 - np.clip(2 * b - 1, 0, 1)) ** 2)
    wide = gauss(rounded, 1.7 * PPU)
    R.H = (0.2 + 1.9 * dome * (0.45 + 0.55 * wide / float(wide.max()))).astype(np.float32) * rounded
    R.M[rounded > 0.5] = AMBER
    R.A = rounded
    return glyph_finish(R, hover, name="glyph.close")


def glyph_repair(hover) -> np.ndarray:
    R = Leg(18, 18)
    # anvil: horn left, heel right, waist, wide foot
    R.poly([(3.2, 16), (14.8, 16), (13.6, 14.2), (11.8, 13.4), (11.4, 11.2), (6.8, 11.2), (6.4, 13.4), (4.4, 14.2)], LEATHER, 0.2, 1.2, 0.8)
    R.poly(bez((0.8, 8.2), (2.6, 9.6), (4.2, 9.6), (5.6, 9.6), n=10) + [(5.6, 11.6), (14, 11.6), (15.4, 10.4), (15.6, 8.2)], TAN, 0.6, 1.5, 0.9)
    # hammer striking it: haft to the upper right, head low on the face
    R.poly(ribbon([(9.6, 7), (15.8, 1.2)], lambda t: 1.5), LEATHER, 1.8, 0.9, 0.6)
    R.poly(rot([(-1.7, -3.7), (1.7, -3.7), (2.0, 3.0), (1.4, 3.8), (-1.4, 3.8), (-2.0, 3.0)], 47, (0, 0), (8.6, 6.2)), TAN, 2.2, 1.5, 0.9)
    return glyph_finish(R, hover, name="glyph.repair")


def glyph_store(hover) -> np.ndarray:
    R = Leg(18, 18)
    # chest: lid, body, bands, clasp
    R.poly([(2, 10.4), (15.8, 10.4), (15.4, 16.2), (2.4, 16.2)], LEATHER, 0.2, 1.2, 0.8)
    R.poly(bez((1.6, 10.6), (2, 8), (3, 7.4), (4.4, 7.4), n=8) + [(13.4, 7.4)] + bez((13.4, 7.4), (15, 7.4), (16, 8), (16.2, 10.6), n=8), TAN, 0.3, 1.3, 0.9)
    for x in (4.6, 13.2):
        R.poly([(x - 0.7, 7.4), (x + 0.7, 7.4), (x + 0.7, 16.2), (x - 0.7, 16.2)], TAN, 1.0, 0.7, 0.4)
    R.poly([(7.6, 9.6), (10.2, 9.6), (10.2, 12.6), (8.9, 13.4), (7.6, 12.6)], AMBER, 1.4, 0.8, 0.5)
    # satchel slumped on the lid, flap and strap
    R.poly(bez((6.6, 8.2), (5.4, 5), (6.8, 2.6), (10, 2.4), n=12) + bez((10, 2.4), (13.4, 2.6), (15, 5), (13.8, 8.2), n=12), LEATHER, 1.5, 1.8, 1.4)
    R.poly(bez((6.2, 3.8), (8.5, 2.2), (12, 2.2), (14.2, 3.8), n=12) + [(13, 6), (10.2, 7), (7.4, 6)], TAN, 2.6, 1.0, 0.7)
    R.ellipsoid(10.2, 6.1, 0.7, 0.7, 0, AMBER, 3.0, 0.5)
    R.poly(ribbon(bez((7.2, 3.2), (8, 0.4), (12.4, 0.4), (13.4, 3.2), n=14), lambda t: 0.7), LEATHER, 2.2, 0.5, 0.3)
    return glyph_finish(R, hover, name="glyph.store")


def glyph_combine(hover) -> np.ndarray:
    R = Leg(28, 24)
    cx, cy = 14, 12
    R.ellipsoid(cx, cy, 5, 5, 0, GOLD, 0.2, 1.6)
    R.ring(cx, cy, 3.9, 3.9, 0.5, 0, GOLD, 1.3, 0.2)

    def sweep(a0, a1):
        pts = []
        for i in range(25):
            t = i / 24
            a = math.radians(a0 + (a1 - a0) * t)
            pts.append((cx + 11.2 * math.cos(a), cy + 8.8 * math.sin(a)))
        R.poly(ribbon(pts, lambda t: 0.6 + 3.2 * t**0.7), GOLD, 0.2, 1.3, 0.9)
        ex, ey = pts[-1]
        dx, dy = ex - pts[-3][0], ey - pts[-3][1]
        ln = math.hypot(dx, dy)
        dx, dy = dx / ln, dy / ln
        R.poly([(ex - dy * 3.9, ey + dx * 3.9), (ex + dx * 4.6, ey + dy * 4.6), (ex + dy * 3.9, ey - dx * 3.9)], GOLD, 0.2, 1.3, 0.9)

    sweep(-170, -32)  # over the top, pointing right
    sweep(10, 148)  # under, pointing left
    return glyph_finish(R, hover, shadow=(0.6, 0.7), name="glyph.combine")




def build_silhouettes() -> list[Sprite]:
    return [
        Sprite("sil.pet", sil_pet()), Sprite("sil.helm", sil_helm()), Sprite("sil.gloves", sil_gloves()),
        Sprite("sil.pants", sil_pants()), Sprite("sil.boots", sil_boots()), Sprite("sil.wings", sil_wings()),
        Sprite("sil.weapon.left", sil_weapon_left()), Sprite("sil.armor", sil_armor()), Sprite("sil.weapon.right", sil_weapon_right()),
        Sprite("sil.pendant", sil_pendant()), Sprite("sil.ring", sil_ring()),
    ]


# glyphs: raised brass on the plate ------------------------------------------------------

GLYPH_MATS = {GOLD: "glyph.brass", AMBER: "glyph.ember", TAN: "glyph.brass", LEATHER: "rail.dim"}


def glyph_finish(R: Leg, hover: bool, shadow=(0.55, 0.65), name="glyph") -> np.ndarray:
    r = finish_relief(R, GLYPH_MATS, "glyph.brass")
    r.h[...] = r.h / HEIGHT_K * 0.34  # glyphs are shallower than the silhouettes
    hi = render_chain(r, name, None, None, wear=0.10, grime=0.35, rust=0.0, dust=0.0, exposure=1.15 if hover else 0.55)
    return ua_shadow(hi, shadow, 0.9)


def ua_shadow(hi: np.ndarray, off, opacity: float) -> np.ndarray:
    return with_shadow(hi, off[0], off[1], 0.3, opacity)


def build_glyphs() -> list[Sprite]:
    out = []
    for nm, fn in (("close", glyph_close), ("repair", glyph_repair), ("store", glyph_store), ("combine", glyph_combine)):
        out.append(Sprite(f"glyph.{nm}.normal", fn(False)))
        out.append(Sprite(f"glyph.{nm}.hover", fn(True)))
    return out


# wells --------------------------------------------------------------------------------


def well_relief(w_u: float, h_u: float, name: str, lip: float = 2.7) -> fg.Relief:
    """A recessed iron well: a rolled lip round a deep pocket whose floor is dark blued iron."""
    r = fg.Relief(w_u, h_u, "frame.black", 1.0)
    r.plate(Rect(0, 0, w_u, h_u, 0.9), z=1.0, bevel=2.3, profile="rolled", rise=0.6, mat="frame.warm", wobble=0.05, nick=0.01, seed=name + ".lip")
    r.pocket(Rect(lip, lip, w_u - 2 * lip, h_u - 2 * lip, 0.5), depth=3.5, wall=1.0, mat="well", wobble=0.04, nick=0.008, seed=name + ".pocket")
    if w_u >= 40 or h_u >= 40:
        for i, (x, y) in enumerate(((1.35, 1.35), (w_u - 1.35, 1.35), (1.35, h_u - 1.35), (w_u - 1.35, h_u - 1.35))):
            r.dome(x, y, 0.5, z=zat(r, x, y), mat="frame.rivet", collar=False, seed=f"{name}.rv{i}")
    return r


def well_value_map(r: fg.Relief) -> np.ndarray:
    """The floor falls into shadow towards the top and left, where the lip hangs over it."""
    x = np.broadcast_to(r.xs[None, :], (r.H_px, r.W_px))
    y = np.broadcast_to(r.ys[:, None], (r.H_px, r.W_px))
    floor = fg.blur((r.mid == fg.MAT_ID["well"]).astype(F32), 0.4 * PPU)
    shade_ = 0.40 - 0.18 * np.exp(-((y - 2.7) / 5.0) ** 2) * (y < 9) - 0.08 * np.exp(-((x - 2.7) / 4.0) ** 2) * (x < 8)
    return (1 + floor * (shade_ - 1)).astype(F32)


WELL_RENDER = dict(wear=0.09, grime=0.5, rust=0.06, dust=0.04, detail=0.4)


def render_well(w_u: int, h_u: int) -> np.ndarray:
    r = well_relief(w_u, h_u, f"well.{w_u}x{h_u}")
    return fg.downsample(r.render(f"well.{w_u}x{h_u}", value_map=well_value_map(r), **WELL_RENDER))


def render_field(w_u: int, h_u: int, name: str, lip: float) -> np.ndarray:
    r = well_relief(w_u, h_u, name, lip)
    return fg.downsample(r.render(name, value_map=well_value_map(r), **WELL_RENDER))


def render_plain(w_u: int, h_u: int) -> np.ndarray:
    """The label plate beside the currency field: a small dark iron plate with a dim brass edge line."""
    r = fg.Relief(w_u, h_u, "frame.black", 1.0)
    r.plate(Rect(0, 0, w_u, h_u, 0.7), z=0.6, bevel=1.4, profile="rolled", rise=0.6, mat="frame.iron", wobble=0.03, seed="currency.plate")
    r.dome(w_u - 1.3, 1.3, 0.45, z=zat(r, w_u - 1.3, 1.3), mat="frame.rivet", collar=False, seed="currency.plate.rv")
    return fg.downsample(r.render("currency.plate", wear=0.10, grime=0.5, rust=0.05, dust=0.04, detail=0.5))


def render_tip_panel() -> np.ndarray:
    """Tooltip frame: nine-slice with 4 u corners. A thin forged rim, then black at 80%."""
    r = fg.Relief(12, 12, "frame.black", 1.0)
    r.plate(Rect(0, 0, 12, 12, 1.1), z=0.9, bevel=1.5, profile="rolled", rise=0.55, mat="frame.warm", wobble=0.04, nick=0.008, seed="tip.rim")
    r.pocket(Rect(1.55, 1.55, 8.9, 8.9, 0.6), depth=1.6, wall=0.55, mat="void", wobble=0.02, seed="tip.floor")
    img = r.render("tip.panel", wear=0.14, grime=0.4, rust=0.04, dust=0.02, detail=0.4)
    inner = fg.blur((r.mid == fg.MAT_ID["void"]).astype(F32), 0.12 * PPU)
    rgb = img[..., :3] * (1 - inner[..., None]) * 1.0
    alpha = 1.0 - 0.20 * inner
    out = fg.downsample(np.dstack((rgb, alpha)).astype(F32))
    # the middle of the panel must stay pure black at exactly 204/255 so text keeps its contrast
    n = 4 * SCALE
    out[n:-n, n:-n, :3] = 0
    out[n:-n, n:-n, 3] = 204
    return out


def render_tip_band() -> np.ndarray:
    """Opaque band for tinting, 12 x 8 u, nine-sliced horizontally: a dark iron strip."""
    r = fg.Relief(12, 8, "frame.black", 1.0, "x")
    r.plate(Rect(-2, 0.6, 16, 6.8, 0.0), z=0.8, bevel=1.4, profile="rolled", rise=0.6, mat="frame.cold", seed="tip.band")
    return fg.downsample(r.render("tip.band", wear=0.08, grime=0.4, rust=0.0, dust=0.02, detail=0.4), wrap="x")


def build_wells() -> list[Sprite]:
    s3 = 3 * SCALE
    sizes = (("1x1", 20, 20), ("2x2", 40, 40), ("3x2", 60, 40), ("2x3", 40, 60))
    jobs = [("render_well", (w, h)) for _, w, h in sizes] + [("render_field", (113, 18, "currency.field", 2.3)), ("render_plain", (12, 8))]
    res = run_jobs(jobs)
    out = [Sprite(f"well.{n}", res[i], slice=(s3, s3, s3, s3)) for i, (n, _, _) in enumerate(sizes)]
    s4 = 4 * SCALE
    out += [
        Sprite("currency.field", res[4], slice=(s3, s3, s3, s3)),
        Sprite("currency.plate", res[5]),
        Sprite("tip.panel", render_tip_panel(), slice=(s4, s4, s4, s4)),
        Sprite("tip.band", render_tip_band(), slice=(s4, 0, s4, 0)),
    ]
    return out


# cells --------------------------------------------------------------------------------


def cell_relief(name: str, face: str = "cell") -> fg.Relief:
    r = fg.Relief(20, 20, "frame.black", 1.0)
    r.plate(Rect(0.5, 0.5, 19.0, 19.0, 0.4), z=0.8, bevel=1.6, profile="chamfer", rise=0.85, mat=face, wobble=0.02, seed=name)
    return r


CELL_RENDER = dict(wear=0.06, grime=0.35, rust=0.0, dust=0.02)


def render_cell_empty() -> np.ndarray:
    r = cell_relief("cell.empty")
    return fg.downsample(render_micro(r, "cell.empty", 0.55, **CELL_RENDER))


def lattice_tile(name: str, tone: tuple, line: tuple) -> np.ndarray:
    """A flat 20 u tile with a half line on each edge, so tiling makes a 1 u lattice. Dark iron, faintly beaten."""
    r = fg.Relief(20, 20, "cell", 1.0, "xy")
    for ox in (0.0, 20.0):
        r.pocket(Rect(ox - 0.5, -1.0, 1.0, 22.0), depth=0.35, wall=0.35, mat="frame.black", seed=f"{name}.v{ox}")
    for oy in (0.0, 20.0):
        r.pocket(Rect(-1.0, oy - 0.5, 22.0, 1.0), depth=0.35, wall=0.35, mat="frame.black", seed=f"{name}.h{oy}")
    r.tint = np.broadcast_to(np.array(tone, dtype=F32), (r.H_px, r.W_px, 3)).copy()
    hi = render_micro(r, name, 0.55, wear=0.0, grime=0.3, rust=0.0, dust=0.0)
    return fg.downsample(hi, wrap="xy")


def flat_tile(rgb: str, alpha: float) -> np.ndarray:
    return (rgba(int(20 * SCALE), int(20 * SCALE), rgb, alpha) * 255 + 0.5).astype(np.uint8)


def build_cells() -> list[Sprite]:
    res = run_jobs([("render_cell_empty", ()), ("render_cell_state", ("cell.occupied",)), ("render_cell_state", ("cell.hover",)), ("render_cell_state", ("cell.invalid",))])
    out = [Sprite("cell.empty", res[0]), Sprite("cell.occupied", res[1], tile="xy"), Sprite("cell.hover", res[2], tile="xy"), Sprite("cell.invalid", res[3], tile="xy")]
    out.append(Sprite("cell.overlay.hover", flat_tile("#071451", 64 / 255), tile="xy"))
    out.append(Sprite("cell.overlay.invalid", flat_tile("#51070A", 64 / 255), tile="xy"))
    return out


CELL_STATES = {"cell.occupied": (0.62, 0.62, 0.66), "cell.hover": (0.55, 0.70, 1.15), "cell.invalid": (1.25, 0.55, 0.55)}


def render_cell_state(name: str) -> np.ndarray:
    return lattice_tile(name, CELL_STATES[name], (1, 1, 1))




# button plates ------------------------------------------------------------------------


def plate_relief(w_u: int, h_u: int, state: str, name: str) -> fg.Relief:
    """Square iron plate: a rolled rim, a dished face, a rivet in each corner. pressed sinks it, so the rim reads inverted."""
    r = fg.Relief(w_u, h_u, "frame.black", 1.0)
    pressed = state == "pressed"
    z = 0.4 if pressed else 1.2
    r.plate(Rect(0.3, 0.3, w_u - 0.6, h_u - 0.6, 0.8), z=z, bevel=1.9, profile="rolled", rise=0.55, mat="frame.iron" if state != "disabled" else "frame.cold", wobble=0.03, nick=0.006, seed=name + ".rim")
    face_z = zat(r, w_u / 2, h_u / 2)
    r.pocket(Rect(2.1, 2.1, w_u - 4.2, h_u - 4.2, 0.7), depth=0.6 if not pressed else 0.0, wall=0.7, seed=name + ".face")
    if pressed:  # the whole plate sits lower, with a shadow lip on its top and left
        r.h[...] = np.where(r.a > 0.5, r.h - 0.35, r.h)
    inset = 3.7 if w_u <= 24 else 4.0
    for i, (x, y) in enumerate(((inset, inset), (w_u - inset, inset), (inset, h_u - inset), (w_u - inset, h_u - inset))):
        r.dome(x, y, 0.95, z=zat(r, x, y), mat="frame.rivet", seed=f"{name}.rv{i}", collar=True)
    return r


PLATE_RENDER = dict(wear=0.12, grime=0.5, rust=0.08, dust=0.03, detail=0.5)


def render_plate(w_u: int, h_u: int, state: str) -> np.ndarray:
    name = f"btn{w_u}"
    r = plate_relief(w_u, h_u, "normal" if state == "hover" else state, name)
    exposure = {"normal": 1.0, "hover": 1.7, "pressed": 0.70, "disabled": 0.62}[state]
    img = r.render(name, exposure=exposure, **PLATE_RENDER)
    if state == "disabled":
        lum = img[..., :3].mean(axis=2, keepdims=True)
        img[..., :3] = img[..., :3] * 0.35 + lum * 0.65
    return fg.downsample(img)


def build_plates() -> list[Sprite]:
    jobs = [("render_plate", (24, 24, s)) for s in ("normal", "hover", "pressed", "disabled")] + [("render_plate", (44, 32, s)) for s in ("normal", "hover", "pressed")]
    res = run_jobs(jobs)
    names = [f"btn.plate24.{s}" for s in ("normal", "hover", "pressed", "disabled")] + [f"btn.plate44x32.{s}" for s in ("normal", "hover", "pressed")]
    return [Sprite(n, a) for n, a in zip(names, res)]


# title strip, rod, tab, arrow ---------------------------------------------------------


def render_strip() -> np.ndarray:
    """Nine-slice title plate, 2 u corners: a dark recessed slot with a thin lip. The middle is flat so it stretches."""
    r = fg.Relief(8, 8, "frame.black", 1.0)
    r.plate(Rect(0, 0, 8, 8, 0.0), z=1.0, bevel=1.1, profile="rolled", rise=0.6, mat="frame.warm", seed="strip.lip")
    r.pocket(Rect(1.05, 1.05, 5.9, 5.9, 0.0), depth=1.7, wall=0.45, mat="void", seed="strip.slot")
    img = r.render("win.strip", wear=0.10, grime=0.4, rust=0.0, dust=0.02, detail=0.3)
    inner = fg.blur((r.mid == fg.MAT_ID["void"]).astype(F32), 0.12 * PPU)
    rgb = img[..., :3] * (1 - inner[..., None]) + np.array((0.012, 0.012, 0.013), F32) * inner[..., None]
    out = fg.downsample(np.dstack((rgb, img[..., 3])).astype(F32))
    out[2 * SCALE : -2 * SCALE, 2 * SCALE : -2 * SCALE, :3] = np.round(token("#0A0A0B") * 255).astype(np.uint8)
    return out


def render_rod() -> np.ndarray:
    """The scroll rod: a dark steel bar with a long soft highlight, two forged collars and knobs at both ends."""
    w_u, h_u = 158, 10
    r = fg.Relief(w_u, h_u, "frame.black", 0.0)
    r.plate(Rect(14.0, 2.35, w_u - 28.0, 5.3, 2.6), z=0.0, bevel=2.65, profile="round", rise=1.0, mat="rod.steel", wobble=0.02, seed="rod.bar")
    for side, x0 in (("l", 6.2), ("r", w_u - 17.0)):
        r.plate(Rect(x0, 0.7, 10.8, 8.6, 1.4), z=0.2, bevel=2.5, profile="rolled", rise=1.0, mat="frame.warm", wobble=0.03, nick=0.006, seed=f"rod.collar{side}")
        r.plate(Rect(x0 + 3.7, 0.25, 3.4, 9.5, 0.8), z=0.8, bevel=1.4, profile="round", rise=0.9, mat="rail.dim", wobble=0.02, seed=f"rod.band{side}")
    for side, cx in (("l", 3.6), ("r", w_u - 3.6)):
        r.plate(Ellipse(cx, 5.0, 3.4, 2.6), z=0.0, bevel=2.2, profile="round", rise=0.9, mat="frame.iron", wobble=0.02, seed=f"rod.knob{side}")
    img = r.render("win.scroll.rod", wear=0.07, grime=0.5, rust=0.05, dust=0.02, detail=0.30, exposure=0.8)
    return fg.downsample(img)


def render_tab(hover: bool) -> np.ndarray:
    r = fg.Relief(33, 6, "frame.black", 1.0)
    r.plate(Rect(0.0, 0.0, 33, 6, 0.0), z=0.6, bevel=1.0, profile="rolled", rise=0.7, mat="frame.cold", seed="tab")
    img = r.render("win.setoption.tab", wear=0.08, grime=0.4, rust=0.0, dust=0.02, detail=0.3, exposure=2.0 if hover else 1.0)
    img[..., 3] = 0.86 if not hover else 0.92
    return fg.downsample(img)


def render_arrow(hover: bool) -> np.ndarray:
    r = fg.Relief(13, 7, "frame.black", 0.0)
    r.plate(Poly([(1.0, 0.7), (12.0, 0.7), (6.5, 6.1)]), z=0.0, bevel=1.5, profile="rolled", rise=0.9, mat="steel", wobble=0.02, seed="arrow")
    img = r.render("win.arrow.down", wear=0.12, grime=0.3, rust=0.0, dust=0.0, detail=0.3, exposure=1.7 if hover else 0.95)
    return with_shadow(img, 0.25, 0.8, 0.3, 0.85)


def build_rod() -> list[Sprite]:
    res = run_jobs([("render_rod", ()), ("render_strip", ()), ("render_tab", (False,)), ("render_tab", (True,)), ("render_arrow", (False,)), ("render_arrow", (True,))])
    size = int(8 * SCALE)
    return [
        Sprite("win.scroll.rod", res[0]),
        Sprite("win.strip", res[1], slice=(8, 8, 8, 8)),
        Sprite("win.setoption.tab.normal", res[2]), Sprite("win.setoption.tab.hover", res[3]),
        Sprite("win.arrow.down.normal", res[4]), Sprite("win.arrow.down.hover", res[5]),
        Sprite("win.linebox", (rgba(size, size, "#000000", 46 / 255) * 255 + 0.5).astype(np.uint8), slice=(8, 8, 8, 8)),
    ]


# slabs, crowns and the build ----------------------------------------------------------


def build_slabs() -> list[Sprite]:
    meta = {"radius": 6 * SCALE}
    return [Sprite(f"win.slab.{k}", render_slab(k), meta=meta) for k in ("inventory", "combine")]


def build_crowns(left: np.ndarray, right: np.ndarray) -> list[Sprite]:
    meta = {"tileAnchorY": TILE_ANCHOR_Y * SCALE}
    res = run_jobs([("render_crown", ("inventory",)), ("render_crown", ("combine",)), ("render_bottom", ())])
    inv = blend_columns(res[0], left, right, 0, 6 * SCALE, False)
    com = blend_columns(res[1], left, right, 0, 6 * SCALE, False)
    bot = blend_columns(res[2], left, right, 416, 6 * SCALE, True)
    return [Sprite("win.crown.inventory", inv, meta=meta), Sprite("win.crown.combine", com, meta=meta), Sprite("win.bottom", bot, meta=meta)]


def all_sprites() -> list[Sprite]:
    side = build_side()
    out: list[Sprite] = list(side)
    out += build_crowns(side[0].image, side[1].image)
    for fn in (build_slabs, build_cells, build_rod, build_wells, build_silhouettes, build_glyphs, build_plates):
        out += fn()
    return out


def main(out_root: Path) -> None:
    atlas = Atlas("window", Path(out_root) / OUT_DIR, meta={"source": "tools/ui-assets/gen_window.py"})
    for s in all_sprites():
        atlas.add(s)
    atlas.save()


# previews -----------------------------------------------------------------------------

_WELLS = (
    ("pet", "2x2", 14, 46), ("helm", "2x2", 74, 46), ("wings", "3x2", 114, 46),
    ("weapon.left", "2x3", 14, 89), ("pendant", "1x1", 54, 89), ("armor", "2x3", 74, 89), ("weapon.right", "2x3", 134, 89),
    ("gloves", "2x2", 14, 152), ("ring", "1x1", 54, 152), ("pants", "2x2", 74, 152), ("ring", "1x1", 114, 152), ("boots", "2x2", 134, 152),
)
_BG = (14, 15, 13, 255)


def _frame(get, kind: str) -> Image.Image:
    S = SCALE
    w = Image.new("RGBA", (190 * S, 432 * S), (0, 0, 0, 0))

    def put(name, x, y, size=None):
        im = get(name)
        if size:
            im = im.resize(size, Image.NEAREST)
        w.alpha_composite(im, (int(x * S), int(y * S)))

    crown_h = 41 if kind == "inventory" else 40
    put(f"win.slab.{kind}", 12, crown_h)
    # both side tiles are anchored on window y=41; the crown covers everything above
    for name, x in (("win.side.left", 0), ("win.side.right", 177)):
        y = TILE_ANCHOR_Y - TILE_H
        while y < 416:
            im = get(name)
            top = max(0, crown_h - y)
            if top < TILE_H:
                w.alpha_composite(im.crop((0, top * S, im.width, min(TILE_H, 416 - y) * S)), (x * S, (y + top) * S))
            y += TILE_H
    put(f"win.crown.{kind}", 0, 0)
    put("win.bottom", 0, 416)
    return w


def compose(get, kind: str, items=None) -> Image.Image:
    """The window as the game builds it. items maps (col, row, w, h) cell rects to icon names, if the items atlas is at hand."""
    S = SCALE
    w = _frame(get, kind)

    def put(name, x, y, size=None):
        im = get(name)
        if size:
            im = im.resize((int(size[0] * S), int(size[1] * S)), Image.NEAREST)
        w.alpha_composite(im, (int(x * S), int(y * S)))

    def grid(ox, oy, cols, rows):
        for c in range(cols):
            for r in range(rows):
                put("cell.empty", ox + 20 * c, oy + 20 * r)

    if kind == "inventory":
        put("win.strip", 34, 12, (120, 8))
        put("win.scroll.rod", 16, 22)
        put("win.setoption.tab.normal", 78, 26)
        put("win.arrow.down.normal", 87, 34)
        for sil, well_name, x, y in _WELLS:
            put(f"well.{well_name}", x, y)
            put(f"sil.{sil}", x, y)
        grid(15, 200, 8, 8)
        placed = [(c, r, cw, ch) for (c, r, cw, ch) in (items or {})] if items else [(2, 0, 2, 3), (0, 2, 2, 2), (6, 4, 2, 4)]
        for c, r, cw, ch in placed:
            for i in range(cw):
                for j in range(ch):
                    put("cell.occupied", 15 + 20 * (c + i), 200 + 20 * (r + j))
        for c, r in ((0, 2), (1, 2), (0, 3), (1, 3)):
            put("cell.hover", 15 + 20 * c, 200 + 20 * r)
        for (c, r, cw, ch), name in (items or {}).items():
            try:
                ico = get(name)
            except KeyError:
                continue
            # item sprites carry a 6 u margin round the footprint, pivot at its top-left corner
            w.alpha_composite(ico, (int((15 + 20 * c) * S) - int(6 * S), int((200 + 20 * r) * S) - int(6 * S)))
        put("currency.plate", 19, 370)
        put("currency.field", 49, 368)
        for name, x in (("close", 19), ("repair", 47), ("store", 75)):
            put("btn.plate24.normal", x, 395)
            put(f"glyph.{name}.normal", x + 3, 398)
    else:
        put("win.strip", 35, 8, (120, 8))
        put("win.strip", 35, 20, (119, 8))
        for y in (50, 70, 90):
            put("win.linebox", 19, y, (100, 8))
        grid(15, 110, 8, 4)
        for y in (211, 226):
            put("win.linebox", 19, y, (140, 8))
        put("btn.plate44x32.normal", 74, 301)
        put("glyph.combine.normal", 82, 305)
        put("btn.plate24.normal", 19, 395)
        put("glyph.close.normal", 22, 398)
    return w


def write_previews(atlas_json: Path, out_dir: Path) -> None:
    out_dir.mkdir(parents=True, exist_ok=True)

    def get(name):
        return Image.fromarray(ua.sprite_pixels(atlas_json, name), "RGBA")

    S = SCALE
    items = {(2, 0, 2, 3): "item.gear.iron-blade", (0, 2, 2, 2): "item.helm.gilded", (6, 4, 2, 4): "item.bow.verdant", (4, 0, 1, 1): "item.gear.iron-band",
             (5, 0, 1, 1): "item.gem.amber", (4, 1, 1, 2): "item.gear.ward-lantern", (3, 4, 3, 2): "item.gear.travel-cloak", (0, 4, 2, 2): "item.gear.leather-vest"}
    ij = Path(atlas_json).parent.parent / "items" / "items.json"
    if ij.exists():
        def get_item(name, _g=get):
            try:
                return _g(name)
            except KeyError:
                return Image.fromarray(ua.sprite_pixels(ij, name), "RGBA")
        get = get_item
    else:
        items = None
    inv, com = compose(get, "inventory", items), compose(get, "combine")
    board = Image.new("RGBA", (380 * S + 40, 432 * S + 40), _BG)
    board.alpha_composite(com, (20, 20))
    board.alpha_composite(inv, (20 + 190 * S, 20))
    board.convert("RGB").save(out_dir / "window_composite.png", compress_level=6)
    board.convert("RGB").resize((board.width // 2, board.height // 2), Image.LANCZOS).save(out_dir / "window_composite_half.png", compress_level=6)
    # what a player sees: the inventory window at 1080p (2.25 px/u) and 720p (1.5 px/u)
    for tag, ppu in (("1080", 2.25), ("720", 1.5)):
        im = Image.new("RGBA", inv.size, _BG)
        im.alpha_composite(inv)
        im.convert("RGB").resize((round(190 * ppu), round(432 * ppu)), Image.LANCZOS).save(out_dir / f"window_inventory_{tag}.png", compress_level=6)
    ua.contact_sheet([atlas_json], out_dir / "window_sprites.png", cols=8)


if __name__ == "__main__":
    root = Path(__file__).resolve().parents[2] / "dream" / "ui"
    main(root)
    write_previews(root / OUT_DIR / "window.json", Path(__file__).resolve().parent / "previews")
