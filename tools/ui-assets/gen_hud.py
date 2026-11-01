"""HUD art: bottom bar, gems, gauges, dragons, talons, chat chrome, cursors.

Writes dream/ui/hud/hud.{png,json} and cursors.{png,json}. All art is original and procedural.
Run directly to also refresh tools/ui-assets/previews/hud_*.png. Layout constants are bar units
(u), y down, 0 at the bar's top edge; dragons and chat sit at negative y.
"""
from __future__ import annotations

import math
import sys
from pathlib import Path

import numpy as np
from PIL import Image

import uiart as U
from uiart import PPU, LIGHT_TL

HERE = Path(__file__).resolve().parent

# Bar layout, u. Everything mirrors about x=320 except the centre cluster and the gauge stripe.
BAR_W, BAR_H = 640, 48
LEFT_W, CENTRE_W = 199, 242
SLAB = (5, 1, 56, 22)
COORDS = (5, 24, 56, 22)
GAUGE_FILL = (73, 5, 15, 35)
GAUGE_CAP = (64, 1, 33, 46)
GEM = (98, -2, 50, 50)
TALON = (140, 1, 59, 46)
DRAGON_H = 46


# helpers ------------------------------------------------------------------------------


def smooth(e0, e1, x):
    t = np.clip((x - e0) / (e1 - e0), 0, 1)
    return t * t * (3 - 2 * t)


def fblur(a, sigma_px, wrap=False):
    """Float gaussian approximation: three box passes per axis."""
    r = max(1, int(round(sigma_px * 0.87)))
    out = a.astype(np.float32)
    for axis, wrapped in ((0, wrap in (True, "y")), (1, wrap in (True, "x"))):
        pad = [(0, 0)] * out.ndim
        pad[axis] = (3 * r + 1, 3 * r + 1)
        p = np.pad(out, pad, mode="wrap" if wrapped else "edge")
        for _ in range(3):
            c = np.cumsum(p, axis=axis, dtype=np.float64)
            hi = np.take(c, np.arange(2 * r + 1, c.shape[axis]), axis=axis)
            lo = np.take(c, np.arange(0, c.shape[axis] - 2 * r - 1), axis=axis)
            p = (hi - lo) / (2 * r + 1)
        cut = (p.shape[axis] - out.shape[axis]) // 2
        out = np.take(p, np.arange(cut, cut + out.shape[axis]), axis=axis).astype(np.float32)
    return out


def bbox(mask, pad=0):
    ys, xs = np.nonzero(mask > 0.5)
    if not len(ys):
        return None
    h, w = mask.shape
    return (max(ys.min() - pad, 0), min(ys.max() + pad + 1, h), max(xs.min() - pad, 0), min(xs.max() + pad + 1, w))


def rgb(c):
    return U.token(c)


class Relief:
    """Painter's heightfield with per-pixel albedo and gloss. Heights are in u."""

    def __init__(self, w_u, h_u, base="#000000", alpha=0.0, gloss=1.0):
        self.w_u, self.h_u = w_u, h_u
        self.H_px, self.W_px = int(round(h_u * PPU)), int(round(w_u * PPU))
        self.h = np.zeros((self.H_px, self.W_px), dtype=np.float32)
        self.a = np.full((self.H_px, self.W_px), alpha, dtype=np.float32)
        self.alb = np.broadcast_to(rgb(base), (self.H_px, self.W_px, 3)).copy()
        self.gl = np.full((self.H_px, self.W_px), gloss, dtype=np.float32)
        yy, xx = np.mgrid[0 : self.H_px, 0 : self.W_px].astype(np.float32)
        self.xu, self.yu = (xx + 0.5) / PPU, (yy + 0.5) / PPU

    def _store(self, s, m, h, alb, gloss):
        self.h[s] = np.where(m, h, self.h[s])
        self.a[s] = np.where(m, 1.0, self.a[s])
        if alb is not None:
            self.alb[s] = np.where(m[..., None], rgb(alb) if isinstance(alb, str) else alb, self.alb[s])
        if gloss is not None:
            self.gl[s] = np.where(m, gloss, self.gl[s])

    def plate(self, pts, z=0.0, bevel=0.6, profile="chamfer", tilt=(0.0, 0.0), alb=None, gloss=None, mode="set", rise=1.0):
        """Polygon plate. tilt is slope in u/u along x and y about the plate's centroid."""
        m = U.poly_mask(self.w_u, self.h_u, pts) > 0.5
        self.mask_plate(m, z, bevel, profile, tilt, alb, gloss, mode, rise)

    def mask_plate(self, m, z=0.0, bevel=0.6, profile="chamfer", tilt=(0.0, 0.0), alb=None, gloss=None, mode="set", rise=1.0):
        b = bbox(m, 2)
        if b is None:
            return
        s = (slice(b[0], b[1]), slice(b[2], b[3]))
        cm = m[s]
        d = U.sdf(cm.astype(np.float32))
        h = U.bevel_height(np.maximum(d, 0), bevel, profile) * rise + z
        if tilt != (0.0, 0.0):
            xu, yu = self.xu[s], self.yu[s]
            h = h + (xu - xu[cm].mean()) * tilt[0] + (yu - yu[cm].mean()) * tilt[1]
        if mode == "max":
            cm = cm & ((h > self.h[s]) | (self.a[s] < 0.5))
        self._store(s, cm, h, alb, gloss)

    def sweep(self, pts, radii, z=0.0, alb=None, gloss=None, flatten=1.0, step_px=3.0, mode="max"):
        """Sphere sweep along a polyline; radii is one value or a list per point. Tubes, horns, cords."""
        pts = np.array(pts, dtype=np.float32)
        rad = np.full(len(pts), radii, dtype=np.float32) if np.isscalar(radii) else np.array(radii, dtype=np.float32)
        zs = np.full(len(pts), z, dtype=np.float32) if np.isscalar(z) else np.array(z, dtype=np.float32)
        seg = np.hypot(*(pts[1:] - pts[:-1]).T)
        t = np.concatenate(([0], np.cumsum(seg)))
        n = max(2, int(t[-1] * PPU / step_px))
        ts = np.linspace(0, t[-1], n)
        xs, ys = np.interp(ts, t, pts[:, 0]), np.interp(ts, t, pts[:, 1])
        rs, zz = np.interp(ts, t, rad), np.interp(ts, t, zs)
        for x, y, r, z0 in zip(xs, ys, rs, zz):
            if r <= 0.02:
                continue
            x0, x1 = int((x - r) * PPU) - 1, int((x + r) * PPU) + 2
            y0, y1 = int((y - r) * PPU) - 1, int((y + r) * PPU) + 2
            x0, y0, x1, y1 = max(x0, 0), max(y0, 0), min(x1, self.W_px), min(y1, self.H_px)
            if x0 >= x1 or y0 >= y1:
                continue
            s = (slice(y0, y1), slice(x0, x1))
            d2 = (self.xu[s] - x) ** 2 + (self.yu[s] - y) ** 2
            ins = d2 < r * r
            h = z0 + np.sqrt(np.maximum(r * r - d2, 0)) * flatten
            ins = ins & ((h > self.h[s]) | (self.a[s] < 0.5)) if mode == "max" else ins
            self._store(s, ins, h, alb, gloss)

    def render(self, **kw):
        return studio(self.h, self.alb, self.gl, self.a, **kw)


def studio(h, alb, gloss, alpha, soften=0.14, strength=1.0, amb=0.55, dif=0.7, key=1.0, fill=0.35, ao=0.9, ao_r=1.6, wrap=False):
    """Gloss studio shading: a hard overhead softbox and a low side strip, reflected in the normal.

    Flat faces reflect the dark room, so black lacquer stays black and highlights run along ridges.
    Crevices darken by comparing height with its blurred neighbourhood.
    """
    hs = fblur(h, max(soften * PPU, 1), wrap) if soften else h
    gy, gx = np.gradient(hs)
    nx, ny = -gx * PPU * strength, -gy * PPU * strength
    nl = np.sqrt(nx * nx + ny * ny + 1)
    nx, ny, nz = nx / nl, ny / nl, 1 / nl
    rx, ry, rz = 2 * nz * nx, 2 * nz * ny, 2 * nz * nz - 1
    # softbox above and slightly left; a narrow bright core inside a dimmer sheet
    across = rx + 0.32
    box = smooth(-0.18, -0.46, ry) * (1 - smooth(-0.86, -0.98, ry)) * (1 - smooth(0.55, 0.95, np.abs(across)))
    core = smooth(-0.40, -0.52, ry) * (1 - smooth(-0.66, -0.76, ry)) * (1 - smooth(0.30, 0.70, np.abs(across)))
    low = smooth(0.35, 0.62, -rx) * (1 - smooth(0.1, 0.55, np.abs(ry - 0.12))) * 0.28
    env = key * (0.38 * box + 0.95 * core) + fill * low + 0.035 * np.clip(rz, 0, 1)
    light = np.array(LIGHT_TL, dtype=np.float32)
    light /= np.linalg.norm(light)
    d = np.clip(nx * light[0] + ny * light[1] + nz * light[2], 0, 2) / light[2]
    occ = 1.0
    if ao:
        occ = 1 - ao * np.clip((fblur(hs, ao_r * PPU, wrap) - hs) * 0.9, 0, 0.92)
    col = alb * ((amb + dif * (d - 1) * 0.8) * occ)[..., None] + (gloss * env * occ)[..., None]
    return np.dstack((np.clip(col, 0, 1), alpha)).astype(np.float32)


def solid(colour, mask):
    return np.dstack((np.broadcast_to(rgb(colour), mask.shape + (3,)), mask)).astype(np.float32)


# bar base -----------------------------------------------------------------------------


def iron_field(w_u, h_u, seed):
    """Black iron with low relief: cast pitting and hammered dents."""
    r = Relief(w_u, h_u, "hud.iron.base", 1.0, 0.45)
    h, w = r.H_px, r.W_px
    r.h = (U.fbm(h, w, seed + ".dent", 7.0, 3) * 1.1 + U.fbm(h, w, seed + ".pit", 1.2, 2) * 0.22).astype(np.float32)
    return r


def groove(r, pts, depth=0.5, width=0.5):
    m = U.stroke_mask(r.w_u, r.h_u, pts, width) > 0.5
    r.h[m] -= depth


def rail_strip(img, x, y, w, seed, th=0.8, wear=None):
    """Ivory-gold rail, worn along its length; thin dark under-line."""
    H, W = img.shape[:2]
    h_u, w_u = H / PPU, W / PPU
    img = U.over(img, solid("#000000", U.rect_mask(w_u, h_u, (x, y + th, w, 0.5)) * 0.9))
    m = U.rect_mask(w_u, h_u, (x, y, w, th))
    n = U.fbm(H, W, seed, 10.0, 3) if wear is None else wear
    t = smooth(0.2, 0.7, n)[..., None]
    across = np.clip(1 - np.abs((np.arange(H, dtype=np.float32) / PPU - y - th * 0.45) / (th * 0.6)), 0, 1)[:, None, None]
    c = rgb("hud.rail") * (0.42 + 0.58 * t) + (rgb("#EDECD6") - rgb("hud.rail")) * across * t
    return U.over(img, np.dstack((np.clip(c, 0, 1).astype(np.float32), m)))


def recess(r, rect, radius=1.0, z=-1.2, alb="hud.iron.black"):
    m = U.rect_mask(r.w_u, r.h_u, rect, radius) > 0.5
    r.h[m] = z
    r.alb[m] = rgb(alb)
    r.gl[m] = 0.15


IRON_LIGHT = dict(amb=0.9, dif=0.9, key=0.32, fill=0.3, ao=0.8, ao_r=2.0)


def base_left():
    r = iron_field(LEFT_W, BAR_H, "hud.base.left")
    # cast plate seams, so the iron reads as assembled segments
    for pts in ([(63, 0), (63, 48)], [(97, 2), (97, 48)], [(149, 2), (149, 48)], [(167, 2), (172, 26), (167, 48)]):
        groove(r, pts, 0.7, 0.6)
    recess(r, (3.0, 0.0, 60, 23.6), 1.5)
    recess(r, (3.0, 23.4, 60, 24.2), 1.5)
    recess(r, (66.5, 2.0, 28, 44.5), 1.0, -1.5)
    recess(r, (96, 0, 56, 48), 6.0, -1.5)
    for x in (66, 96, 152, 196):
        r.sweep([(x, 45.6), (x, 45.7)], 0.9, 0.3, alb="#3A3A38", gloss=0.8)
    img = r.render(**IRON_LIGHT)
    img = rail_strip(img, 62, 0.8, 137, "hud.base.left.rail")
    img = rail_strip(img, 0, 46.9, 199, "hud.base.left.rail2", 0.45)
    img[..., 3] = 1
    return img


def chain_band(w_u, h_u, y, x0=0.0, x1=None, period=4.0, key=0.55):
    """Slanted-link chain as a relief on black, periodic in `period`."""
    x1 = w_u if x1 is None else x1
    r = Relief(w_u, h_u, "#000000", 0.0, 0.9)
    n = int((x1 - x0) / period) + 3
    for i in range(-1, n):
        cx = x0 + (i + 0.5) * period
        r.sweep([(cx - 1.3, y + 0.7), (cx + 1.3, y - 0.7)], 0.75, 0.2, alb="frame.chain", gloss=1.0, step_px=4)
    img = r.render(amb=0.9, dif=0.8, key=key, fill=0.3, ao=0, soften=0.08)
    img[..., 3] *= U.rect_mask(w_u, h_u, (x0, 0, x1 - x0, h_u))
    return img


def base_centre():
    r = iron_field(CENTRE_W, BAR_H, "hud.base.centre")
    recess(r, (10, 16, 96, 31), 1.5, -1.0, alb="#080808")
    recess(r, (103, 11.5, 34, 36), 1.0, -1.2)
    recess(r, (140, 16, 100, 31), 1.5, -1.0, alb="#080808")
    recess(r, (10, 11.6, 222, 3.8), 0.5, -1.4)
    for x in (13.0, 229.0):
        r.sweep([(x, 13.5), (x, 13.6)], 1.6, 0.2, alb="#6E7074", gloss=1.0)
    img = r.render(**IRON_LIGHT)
    img = U.over(img, chain_band(CENTRE_W, BAR_H, 13.5, 15, 227))
    img = rail_strip(img, 0, 0.8, 242, "hud.base.centre.rail")
    img = rail_strip(img, 0, 46.9, 242, "hud.base.centre.rail2", 0.45)
    img[..., 3] = 1
    return img


def rope(r, y, period, w_u, band=7.0, rad=2.0, z=0.6, alb="#1C1C1D", slant=1.0, x0=0.0):
    """Twisted cord: slanted S lobes side by side, periodic in x."""
    n = int(w_u / period) + 4
    for i in range(-2, n):
        x = x0 + i * period
        pts = U.bezier((x - band * 0.55 * slant, y + band / 2), (x - band * 0.1 * slant, y + band * 0.1),
                       (x + band * 0.1 * slant, y - band * 0.1), (x + band * 0.55 * slant, y - band / 2), 14)
        rs = [rad * (0.45 + 0.55 * math.sin(math.pi * k / 14)) for k in range(15)]
        zs = [z + rad * 0.35 * math.sin(math.pi * k / 14) + i * 0.0 for k in range(15)]
        r.sweep(pts, rs, zs, alb=alb, gloss=1.0, step_px=4)


def gapfill():
    """Three tile widths are rendered and the middle cropped, so the wrap is exact."""
    w = 64
    tw = w * 3
    r = Relief(tw, BAR_H, "hud.iron.base", 1.0, 0.45)
    h0, w0 = int(BAR_H * PPU), int(w * PPU)
    dents = U.fbm(h0, w0, "hud.gapfill.dent", 7.0, 3) * 1.1 + U.fbm(h0, w0, "hud.gapfill.pit", 1.2, 2) * 0.22
    r.h = np.tile(dents, (1, 3)).astype(np.float32)
    recess(r, (0, 11.6, tw, 3.8), 0, -1.4)
    recess(r, (0, 17.0, tw, 29.0), 0, -1.6)
    rope(r, 24.0, 16 / 3, tw, 11.0, 2.3, 0.4)
    rope(r, 38.0, 16 / 3, tw, 11.0, 2.3, 0.4, slant=-1.0)
    img = r.render(amb=0.9, dif=0.9, key=0.42, fill=0.25, ao=0.8, ao_r=2.0)
    img = U.over(img, chain_band(tw, BAR_H, 13.5, period=4.0))
    H, W = img.shape[:2]
    for y, th, tag in ((0.8, 0.8, "a"), (46.9, 0.45, "b")):
        wear = np.tile(U.fbm(H, w0, "hud.gapfill.rail" + tag, 10.0, 3), (1, 3))
        img = rail_strip(img, 0, y, tw, "", th, wear)
    out = img[:, w0 : w0 * 2].copy()
    out[..., 3] = 1
    return out


# gems ---------------------------------------------------------------------------------


GEM_U = 50


# Original cut: a ten-sided stone narrowing to a short keel, drawn in sprite u. Three rings of
# vertices give the classic table, star, bezel and girdle planes.
GIRDLE = [(14, 2.5), (34, 2.5), (45, 11), (48.5, 24), (43, 36), (30, 47), (20, 47), (7, 36), (1.5, 24), (5, 11)]
N_SIDES = len(GIRDLE)
TABLE_C = (24.0, 23.0)  # the table sits a touch up and left of the stone's centre
STONE_C = (25.0, 24.0)
TABLE = [(TABLE_C[0] + (x - STONE_C[0]) * 0.60, TABLE_C[1] + (y - STONE_C[1]) * 0.60) for x, y in GIRDLE]
BEZEL = []
for _i in range(N_SIDES):
    _a, _b = GIRDLE[_i], GIRDLE[(_i + 1) % N_SIDES]
    BEZEL.append((STONE_C[0] + ((_a[0] + _b[0]) / 2 - STONE_C[0]) * 0.80, STONE_C[1] + ((_a[1] + _b[1]) / 2 - STONE_C[1]) * 0.80))


def gem_palette(kind):
    if kind == "hp":
        return dict(
            bright="hud.hp.bright", mid="hud.hp.mid", deep="hud.hp.deep", void="hud.hp.void", rim="hud.hp.rim",
            lit="#FF5A3A", pale="#F08A72", glow="#D45E45", wash="#98210B", core="#A61302", top="#FBE7DA",
        )
    return dict(
        bright="hud.mp.bright", mid="hud.mp.mid", deep="hud.mp.deep", void="hud.mp.void", rim="hud.mp.rim",
        lit="#2FB4F2", pale="#7FD8F4", glow="#2F7FEA", wash="#0222DF", core="#0F1771", top="#40C8F0",
    )


def facet_tone(pts):
    """0..1 from which way a plane leans: upper left catches the light, the keel falls dark."""
    x = sum(p[0] for p in pts) / len(pts) - STONE_C[0]
    y = sum(p[1] for p in pts) / len(pts) - STONE_C[1]
    d = math.hypot(x, y) or 1
    return 0.5 + 0.5 * (x * -0.5 + y * -0.87) / d


def tone_colour(c, t):
    ramp = ("void", "deep", "wash", "mid", "bright", "lit", "pale")
    return c[ramp[min(int(max(t, 0) * len(ramp)), len(ramp) - 1)]]


def gem_facets(c):
    """Painted flat: girdle planes, then bezel kites and stars, then the table's shallow star."""
    f = [(GIRDLE, c["void"])]
    n = N_SIDES
    for i in range(n):
        g0, g1 = GIRDLE[i], GIRDLE[(i + 1) % n]
        m, m0 = BEZEL[i], BEZEL[i - 1]
        tri = [m, g0, g1]
        f.append((tri, tone_colour(c, facet_tone(tri) * 0.8 + (0.0 if i % 2 else 0.1))))
        kite = [m0, TABLE[i], m, g0]
        f.append((kite, tone_colour(c, facet_tone(kite) * 0.95 + (0.12 if i % 2 else 0.0)), tone_colour(c, facet_tone(kite) * 0.72)))
    for i in range(n):
        star = [TABLE[i], TABLE[(i + 1) % n], BEZEL[i]]
        f.append((star, tone_colour(c, facet_tone(star) * 0.7 + 0.26)))
    apex = (TABLE_C[0] + 2.5, TABLE_C[1] - 1.5)
    for i in range(n):
        tri = [TABLE[i], TABLE[(i + 1) % n], apex]
        f.append((tri, c["bright"] if i % 2 else c["mid"]))
    # three long refraction shards, off the apex
    f.append(([TABLE[9], TABLE[0], apex], c["lit"]))
    f.append(([TABLE[2], TABLE[3], apex], c["lit"]))
    f.append(([apex, TABLE[6], TABLE[5]], c["bright"]))
    return f


def catmull(pts, n=10):
    out = []
    p = [pts[0]] + list(pts) + [pts[-1]]
    for i in range(1, len(p) - 2):
        p0, p1, p2, p3 = (np.array(q, dtype=np.float64) for q in p[i - 1 : i + 3])
        for t in np.linspace(0, 1, n, endpoint=False):
            out.append(tuple(0.5 * (2 * p1 + (p2 - p0) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t * t + (3 * p1 - p0 - 3 * p2 + p3) * t**3)))
    out.append(tuple(pts[-1]))
    return out


def rim_path():
    """Follows the table from top left, down the left, along the keel, up to the right; open top right."""
    pts = [TABLE[0], TABLE[9], TABLE[8], TABLE[7], TABLE[6], TABLE[5], TABLE[4], ((TABLE[4][0] + TABLE[3][0]) / 2, (TABLE[4][1] + TABLE[3][1]) / 2)]
    return catmull(pts, 12)


def rim_layers(kind):
    """Rim arc as colour and alpha: a swelling hot core in a tight falloff, tapered at both ends."""
    c = gem_palette(kind)
    path = rim_path()
    n = len(path)
    w = [0.2 + 0.8 * math.sin(math.pi * i / (n - 1)) ** 0.6 for i in range(n)]
    r = Relief(GEM_U, GEM_U)
    r.sweep(path, [0.5 * v for v in w])
    core = r.a.copy()
    r = Relief(GEM_U, GEM_U)
    r.sweep(path, [0.85 * v for v in w])
    a = np.clip(np.maximum(fblur(r.a, 0.3 * PPU) * 1.3, core), 0, 1)
    # the arc dissolves as it climbs, so it never forms a bright bar along the top
    a = a * smooth(TABLE[0][1] - 0.5, TABLE[0][1] + 7.0, r.yu)
    return np.dstack((np.broadcast_to(rgb(c["rim"]), a.shape + (3,)), a)).astype(np.float32)


def gem_layers(kind):
    c = gem_palette(kind)
    img = U.facet_gem(GEM_U, GEM_U, gem_facets(c))
    sil = U.poly_mask(GEM_U, GEM_U, GIRDLE)
    h, w = sil.shape
    # thin ridge lines where planes meet, strongest on the lit side
    for i in range(N_SIDES):
        m0 = BEZEL[i - 1]
        for a, b in ((m0, GIRDLE[i]), (TABLE[i], m0), (TABLE[i], BEZEL[i])):
            k = 0.16 + 0.34 * facet_tone([a, b])
            img = U.over(img, solid(c["pale"], U.stroke_mask(GEM_U, GEM_U, [a, b], 0.28) * k))
    # the two soft shapes the spec allows: depth core and pink glint
    yy, xx = np.mgrid[0:h, 0:w].astype(np.float32)
    xu, yu = (xx + 0.5) / PPU, (yy + 0.5) / PPU
    cx, cy = TABLE_C[0] - 6.0, TABLE_C[1] + 2.0
    core = np.exp(-(((xu - cx) / 5.2) ** 2 + ((yu - cy) / 6.2) ** 2))
    gx, gy = TABLE_C[0] + 7.0, TABLE_C[1] - 3.0
    glint = np.exp(-(((xu - gx) / 3.8) ** 2 + ((yu - gy) / 4.6) ** 2))
    img[..., :3] = img[..., :3] * (1 - 0.85 * core[..., None]) + rgb(c["deep"]) * 0.85 * core[..., None]
    img[..., :3] = img[..., :3] + (rgb(c["pale"]) - img[..., :3]) * 0.30 * glint[..., None]
    # small pale glint on the crown's lower edge
    g = U.ellipse_mask(GEM_U, GEM_U, 8.2, 13.0, 2.0, 0.65, -52)
    img = U.over(img, solid(c["rim"], fblur(g, 0.2 * PPU)))
    # girdle darkening, so the outline turns away rather than stopping
    e = 1 - smooth(0.0, 0.9 * PPU, U.sdf(sil))
    img[..., :3] *= (1 - 0.55 * e)[..., None]
    img[..., 3] = sil
    spec = rim_layers(kind)
    spec[..., 3] *= sil
    full = U.over(img, spec)
    full[..., 3] = sil
    return full, spec


def desat(img, amount, dim):
    out = img.copy()
    l = (img[..., :3] * np.array([0.3, 0.59, 0.11], dtype=np.float32)).sum(axis=2, keepdims=True)
    out[..., :3] = (img[..., :3] * (1 - amount) + l * amount) * dim
    return out


def gem_set(kind):
    full_f, spec_f = gem_layers(kind)
    if kind == "mp":
        full_f, spec_f = U.mirror_x(full_f), U.mirror_x(spec_f)
    full = U.downsample(full_f)
    empty = U.downsample(desat(full_f, 0.6, 0.30))
    spec = U.downsample(spec_f)
    a = full[..., 3]
    empty[..., 3] = a
    ys = np.nonzero(a.max(axis=1))[0]
    top, bot = ys.min(), ys.max()
    v = np.clip((np.arange(a.shape[0]) - top) / max(bot - top, 1), 0, 1)
    g = (v * 255 + 0.5).astype(np.uint8)
    mask = np.zeros_like(full)
    mask[..., :3] = g[:, None, None]
    mask[..., 3] = a
    piv = (GEM_U * 2, GEM_U * 2)
    return [
        U.Sprite(f"gem.{kind}.full", full, pivot=piv),
        U.Sprite(f"gem.{kind}.empty", empty, pivot=piv),
        U.Sprite(f"gem.{kind}.mask", mask, pivot=piv),
        U.Sprite(f"gem.{kind}.spec", spec, pivot=piv),
    ]


# gauges -------------------------------------------------------------------------------


def gauge_prism(seed, face, side, edge, top_dark, outer, dim=1.0, mottle=0.10):
    """Square crystal seen at a slight angle: broad face, sharp bright stripe right of centre, narrow face."""
    w_u, h_u = GAUGE_FILL[2], GAUGE_FILL[3]
    H, W = int(h_u * PPU), int(w_u * PPU)
    xu = (np.arange(W, dtype=np.float32) + 0.5) / PPU
    yu = (np.arange(H, dtype=np.float32) + 0.5) / PPU
    f, s, e = rgb(face), rgb(side), rgb(edge)
    x0, x1 = 9.1, 11.2
    stripe = smooth(x0 - 0.12, x0 + 0.12, xu) * (1 - smooth(x1 - 0.12, x1 + 0.12, xu))
    right = smooth(x1 - 0.1, x1 + 0.1, xu)
    img = np.zeros((H, W, 3), dtype=np.float32) + f
    img = img * (1 - right[None, :, None]) + s * right[None, :, None]
    # cloudy mottling inside the crystal, streaked along the prism
    n = U.fbm(H, W, seed + ".cloud", 5.0, 4)
    st = U.streaks(H, W, seed + ".streak", 90, 12.0)
    tex = 1 + (n - 0.5) * mottle * 2 + (st - 0.5) * mottle * 0.7
    img = img * tex[..., None]
    ramp = (0.92 + 0.14 * np.interp(xu, [0, 9.2], [0.2, 1.0]))[None, :, None]
    img = img * (1 - stripe[None, :, None]) * ramp + (e * (0.92 + 0.08 * tex[..., None])) * stripe[None, :, None]
    # shoulder glow beside the stripe
    glow = np.exp(-(((xu - x0) / 0.8) ** 2))[None, :, None] * 0.35
    img = img * (1 - glow) + e * 0.8 * glow
    # rounded outer sides fall to near black
    ol = smooth(0.0, 1.3, xu) * smooth(0.0, 1.3, w_u - xu)
    o = rgb(outer)
    t = ol[None, :, None]
    img = img * t + (o * (0.1 + 0.9 * t)) * (1 - t)
    # dark seat where the crystal enters the cap
    td = (1 - smooth(0.0, 1.0, yu))[:, None, None]
    img = img * (1 - td) + rgb(top_dark) * td
    img = np.clip(img * dim, 0, 1)
    return np.dstack((img, np.ones((H, W), dtype=np.float32))).astype(np.float32)


def gauge_cap():
    """Steel-and-gold clamp, left version. The hole over the fill stays transparent."""
    w, h = GAUGE_CAP[2], GAUGE_CAP[3]
    r = Relief(w, h, "#000000", 0.0, 0.8)
    fx0, fx1 = GAUGE_FILL[0] - GAUGE_CAP[0], GAUGE_FILL[0] - GAUGE_CAP[0] + GAUGE_FILL[2]
    cx = (fx0 + fx1) / 2
    # gold collars, top
    for x0 in (2.5, fx1 + 0.6):
        x1 = x0 + 6.5
        mid = (x0 + x1) / 2
        spike = [(mid, 3.0), (mid, 8), (mid, 13)]
        r.sweep(spike, [2.9, 2.4, 0.15], 0.3, alb="#7E8084", gloss=1.0)
        r.plate([(x0, 0.6), (x1, 0.6), (x1 - 0.6, 3.9), (x0 + 0.6, 3.9)], 1.2, 1.2, "round", alb="hud.gold.mid", gloss=0.55)
        r.plate([(x0 + 0.7, 3.9), (x1 - 0.7, 3.9), (x1 - 1.1, 5.0), (x0 + 1.1, 5.0)], 0.9, 0.5, "round", alb="#8A6F36", gloss=0.5)
    # silver half dome sitting on the prism
    dome = [(cx + 4.4 * math.cos(a), 0.6 + 3.6 * math.sin(a)) for a in np.linspace(0, math.pi, 24)]
    r.plate(dome, 0.8, 2.6, "round", alb="#ADAEAB", gloss=0.9)
    # lower collars and dome, smaller
    for x0 in (3.5, fx1 + 0.6):
        x1 = x0 + 5.0
        mid = (x0 + x1) / 2
        r.sweep([(mid, 44), (mid, 38), (mid, 32.5)], [2.2, 1.8, 0.15], 0.3, alb="#6E7074", gloss=0.9)
        r.plate([(x0, 42), (x1, 42), (x1, 44.8), (x0, 44.8)], 1.0, 0.9, "round", alb="hud.gold.mid", gloss=0.5)
    dome = [(cx + 3.6 * math.cos(a), 44.6 - 3.2 * math.sin(a)) for a in np.linspace(0, math.pi, 24)]
    r.plate(dome, 0.8, 2.0, "round", alb="#8A8B88", gloss=0.8)
    img = r.render(amb=0.85, dif=0.9, key=0.9, fill=0.4, ao=0.5, ao_r=1.0)
    img = rail_strip(img, 0, 0.0, w, "gauge.cap.rail", 0.8)
    img = rail_strip(img, 0, 45.0, w, "gauge.cap.rail2", 0.7)
    return img


# dragon -------------------------------------------------------------------------------

DRAGON_W = 107
EYE_RECT_U = (56.6, 26.9, 1.8, 1.0)  # flat amber left of the pupil
FANG_RECT_U = (84, 28, 14, 18)
IVORY = "#C8B890"
NECK_LINE = U.bezier((50, 33), (34, 30), (14, 33), (-14, 38), 40)
EYE_TOP = U.bezier((55.0, 26.2), (58.4, 25.6), (62, 26.6), (65.8, 29.4), 16)
EYE_BOT = U.bezier((65.8, 29.4), (61.5, 29.4), (58, 29.2), (55.0, 26.2), 16)


def horn(r, p0, p1, p2, p3, rad, z, rings=26.0):
    """Crescent horn: tapered sphere sweep with shallow growth rings near the root."""
    path = U.bezier(p0, p1, p2, p3, 64)
    n = len(path)
    rr, zz = [], []
    for i in range(n):
        t = i / (n - 1)
        ring = 1 + 0.07 * math.sin(t * rings) * (1 - t) ** 1.5
        rr.append(rad * (1 - t) ** 0.72 * ring + 0.03)
        zz.append(z * (1 - 0.5 * t))
    r.sweep(path, rr, zz, step_px=2.0)


def dorsal_fin(r):
    """Serrated fin down the back of the neck: thin web between swept spines."""
    bases = [(42, 21.0), (35, 22.2), (28, 23.2), (21, 24.0), (14, 24.8), (7, 25.4), (0, 25.9), (-7, 26.4)]
    tips = [(34, 14.0), (26.5, 15.0), (19.0, 15.8), (11.5, 16.6), (4.0, 17.4), (-3.5, 18.0), (-10, 18.6)]
    pts = [bases[0]]
    for i, tip in enumerate(tips):
        b = bases[i + 1]
        mid = ((tip[0] + b[0]) / 2 + 2.2, (tip[1] + b[1]) / 2 + 1.6)
        pts.append(tip)
        pts.extend(U.bezier(tip, (tip[0] + 2, tip[1] + 3), mid, (b[0] + 1.0, b[1] - 1.5), 6)[1:])
    pts += [(-8, 30), (42, 30)]
    r.plate(pts, 0.0, 0.5, "chamfer", gloss=0.8)
    for i, tip in enumerate(tips):
        a = bases[i]
        c = ((a[0] + tip[0]) / 2 - 0.2, (a[1] + tip[1]) / 2 + 0.8)
        r.sweep(U.bezier(a, c, c, tip, 16), [0.95 * (1 - k / 16) ** 0.6 + 0.05 for k in range(17)], 0.6, step_px=2.0)


def neck_scales(r):
    """Shingled angular plates that follow the neck; painted tail to head so nearer plates overlap."""
    g = U.rng("hud.dragon.scales")
    line = np.array(NECK_LINE, dtype=np.float32)
    lens = np.concatenate(([0], np.cumsum(np.hypot(*(line[1:] - line[:-1]).T))))
    L, Hh = 5.2, 2.5
    offsets = np.arange(-11.5, 17, 3.25)
    for i, s0 in enumerate(np.arange(lens[-1], 4, -4.1)):
        cx, cy = np.interp(s0, lens, line[:, 0]), np.interp(s0, lens, line[:, 1])
        ex, ey = np.interp(s0 - 0.5, lens, line[:, 0]), np.interp(s0 - 0.5, lens, line[:, 1])
        tx, ty = ex - cx, ey - cy  # toward the head
        tl = math.hypot(tx, ty)
        tx, ty = tx / tl, ty / tl
        nx, ny = -ty, tx
        for o in offsets:
            o = o + (i % 2) * 1.6
            size = 0.86 + 0.28 * (1 - abs(o + 4) / 16)
            x, y = cx + nx * o, cy + ny * o
            if y < 27.6 - x * 0.09:  # keep the crest clear so the fin stays visible
                continue
            jit = g.random(3) - 0.5
            le, ha = L * size, Hh * size
            pts = [(-le, 0.0), (-le * 0.25, -ha), (le * 0.52, -ha * 0.82), (le * 0.72, ha * 0.2), (-le * 0.2, ha)]
            q = [(x + tx * a + nx * b + jit[0] * 0.4, y + ty * a + ny * b + jit[1] * 0.4) for a, b in pts]
            r.plate(q, 1.0 + i * 0.03, 1.1, "round", tilt=(float(tx * -0.05 + jit[2] * 0.05), float(jit[0] * 0.06)), gloss=0.8)


def cheek_scales(r, poly):
    """Small shingles across the cheek, clipped to it."""
    clip = U.poly_mask(r.w_u, r.h_u, poly) > 0.5
    g = U.rng("hud.dragon.cheek")
    for i, x in enumerate(np.arange(36, 64, 2.7)):
        for y in np.arange(24.5 + (i % 2) * 1.1, 42, 2.2):
            j = g.random(2) - 0.5
            q = [(x - 2.0, y), (x - 0.6, y - 1.25), (x + 1.3, y - 1.05), (x + 1.5, y + 0.2), (x - 0.4, y + 1.25)]
            m = U.poly_mask(r.w_u, r.h_u, q) > 0.5
            b = bbox(m)
            if b is None or not (m & clip).any():
                continue
            r.mask_plate(m & clip, 4.3 + i * 0.02, 0.7, "round", tilt=(float(j[0] * 0.1), float(0.04 + j[1] * 0.08)), gloss=0.55)


def dragon_relief():
    r = Relief(DRAGON_W, DRAGON_H, "hud.dragon.black", 0.0, 1.0)
    # horns behind everything: two crescents swept back off the skull
    horn(r, (57, 20), (48, 11.5), (38, 8.5), (32, 0.9), 2.8, 0.4, 22)
    horn(r, (50, 20.5), (34, 17.5), (14, 13.5), (3.2, 0.6), 3.9, 1.6, 30)
    dorsal_fin(r)
    # neck mass, then scales
    neck = [(-1, 26.5), (12, 25.2), (26, 23.6), (40, 21), (58, 30), (72, 47), (-1, 47)]
    r.plate(neck, 0.4, 1.5, "round", gloss=0.5)
    neck_scales(r)
    # jaw hinge spikes, backward from the cheek
    for (a, c, b, rad) in (((44, 30), (37, 29.5), (30, 25.5), 1.5), ((42, 36), (36, 37), (28.5, 33), 1.3), ((46, 41), (41, 43), (34.5, 41.5), 1.1)):
        r.sweep(U.bezier(a, c, c, b, 16), [rad * (1 - k / 16) ** 0.7 + 0.03 for k in range(17)], 3.4, step_px=2.0)
    # lower jaw: three plates stepping to the chin
    r.plate([(44, 40), (63, 41.2), (64, 46.5), (48, 46.5)], 3.0, 2.2, "round", (0.0, 0.05))
    r.plate([(63, 41.2), (81, 42.3), (80, 46.2), (64, 46.5)], 3.4, 2.0, "round", (0.0, 0.05))
    r.plate([(81, 42.3), (96.5, 42.8), (99.0, 44.0), (93, 45.6), (80, 46.2)], 3.8, 1.6, "round", (0.0, 0.05))
    # gullet: the jaw hangs slightly open behind the fang
    r.plate([(50, 37.0), (70, 38.5), (98, 40.8), (98.5, 43.0), (82, 42.6), (64, 41.6), (52, 41.0)], 1.0, 0.2, alb="#020202", gloss=0.0)
    # teeth: upper row hanging from the lip, shorter lower row
    g = U.rng("hud.dragon.teeth")
    for x in np.arange(60, 96, 3.3):
        y = 38.6 + (x - 60) * 0.062
        L = 1.9 + float(g.random()) * 0.9
        r.plate([(x - 0.75, y), (x + 0.75, y + 0.1), (x + 0.75, y + L)], 2.2, 0.4, alb="#B5B1A6", gloss=0.5)
    for x in np.arange(63.5, 88, 4.1):
        y = 41.6 + (x - 63.5) * 0.056
        r.plate([(x - 0.6, y + 0.2), (x + 0.6, y + 0.2), (x - 0.5, y - 1.3)], 2.2, 0.3, alb="#98958C", gloss=0.4)
    # cheek
    cheek = [(44, 25.5), (61, 25.5), (58, 38.2), (47, 40.5), (39, 33)]
    r.plate(cheek, 3.4, 3.2, "round", (0.0, 0.06))
    cheek_scales(r, cheek)
    r.plate([(42, 33), (54, 36), (54, 39), (47, 40.8), (41, 37)], 4.6, 1.4, "round")
    # upper lip plates, stepping toward the nose
    r.plate([(57, 30), (74, 32), (75, 39.2), (56, 38.2)], 3.8, 2.4, "round", (0.0, 0.05))
    r.plate([(74, 32), (88, 35), (87, 40.4), (75, 39.2)], 4.2, 2.1, "round", (0.0, 0.05))
    r.plate([(88, 35), (97, 37), (101.5, 38.8), (99.5, 41.0), (87, 40.4)], 4.6, 1.7, "round", (0.0, 0.05))
    # snout top, three angular plates, each ending in a raised scute
    r.plate([(58, 23), (70, 24.5), (76, 27.5), (74.5, 32.5), (57, 30.5)], 4.4, 2.6, "round", (0.02, 0.10))
    r.plate([(74.5, 27), (86, 30.0), (88.5, 35.5), (74.5, 32.5)], 5.0, 2.3, "round", (0.02, 0.11))
    r.plate([(86, 29.6), (92, 31.0), (98, 35.0), (101.5, 38.8), (88, 35.5)], 5.6, 2.0, "round", (0.02, 0.12))
    r.plate([(72.5, 27.4), (77, 25.0), (79.0, 28.4)], 6.6, 1.2, "round")
    r.plate([(83.5, 30.2), (88.5, 27.6), (90.5, 31.4)], 6.8, 1.2, "round")
    # nostril
    r.plate([(92.4, 34.9), (96.2, 36.3), (96.6, 37.5), (93.2, 36.9)], 3.0, 0.3, alb="#010101", gloss=0.0)
    # skull crown
    r.plate([(38, 18.5), (50, 15.4), (66, 22), (62, 26), (44, 26)], 4.6, 2.8, "round", (0.0, 0.08))
    # eye socket, narrow and slanted down toward the nose
    r.plate([(53.5, 24.8), (63, 25.6), (67.0, 29.8), (61.5, 30.0), (54.0, 28.0)], 2.0, 0.3, alb="#030303", gloss=0.3)
    # heavy angular brow, overhanging the eye; hard ridge sweeps carry the long highlights
    r.plate([(40, 15.2), (53, 14.0), (72.0, 24.6), (73.0, 28.8), (64, 26.6), (54.5, 24.8), (43, 22.0)], 7.0, 2.4, "round", (0.02, 0.09))
    r.sweep(U.bezier((41, 16.5), (52, 15.2), (62, 19), (72.2, 26), 30), [0.75 - 0.005 * k for k in range(31)], 8.6, step_px=2.0)
    r.sweep(U.bezier((44, 21), (52, 22.6), (60, 24.4), (68, 26.3), 30), 0.42, 8.3, step_px=2.0)
    # ivory fang from the lower jaw, curved up outside the lip
    f = U.bezier((88.2, 44.8), (88.6, 39.5), (90.0, 34.5), (94.6, 30.4), 40)
    r.sweep(f, [1.75 * (1 - k / 40) ** 0.6 + 0.03 for k in range(41)], 8.0, alb=IVORY, gloss=0.4, step_px=2.0)
    return r


def dragon_eye(img):
    """Amber almond with a black slit. Colour stays flat left of the pupil, so it can be probed."""
    w, h = DRAGON_W, DRAGON_H
    m = U.poly_mask(w, h, EYE_TOP + EYE_BOT)
    H, W = m.shape
    xu = (np.arange(W, dtype=np.float32) + 0.5) / PPU
    t = smooth(60.8, 65.0, xu)[None, :, None]
    c = rgb("hud.dragon.eye") * (1 - t) + rgb("#DCC49F") * t
    img = U.over(img, np.dstack((np.broadcast_to(c, (H, W, 3)), m)).astype(np.float32))
    img = U.over(img, solid("#000000", U.ellipse_mask(w, h, 60.6, 27.8, 0.34, 1.6, 10)))
    lid = U.stroke_mask(w, h, EYE_TOP, 0.45)
    return U.over(img, solid("#000000", lid * m * 0.95))


def dragon():
    r = dragon_relief()
    alpha = r.a.copy()
    ivory = np.abs(r.alb - rgb(IVORY)).sum(axis=2) < 0.02
    # overall volume: a neck cylinder plus the head rounding off toward its silhouette
    yc = 36.0 - np.clip(r.xu, 0, 50) * 0.04
    neck = np.sqrt(np.clip(1 - ((r.yu - yc) / 17.5) ** 2, 0, 1)) * 7.0
    fade = 1 - smooth(34, 56, r.xu)
    head = fblur(alpha, 2.2 * PPU) * 3.5
    r.h = r.h + neck * fade * alpha + head
    # the underside of the neck falls into shadow
    r.gl = r.gl * (1 - 0.72 * smooth(35, 46, r.yu) * fade) * (0.55 + 0.45 * smooth(-6, 14, r.xu))
    img = r.render(amb=1.0, dif=0.9, key=1.25, fill=0.5, ao=0.75, ao_r=1.8, soften=0.1)
    root = (1 - smooth(37.0, 45.0, r.yu)) * 0 + smooth(37.0, 45.0, r.yu)
    img[..., :3] = np.where(ivory[..., None], img[..., :3] * (1 - 0.62 * root[..., None]), img[..., :3])
    img = dragon_eye(img)
    img[..., 3] = alpha
    return img


# talons -------------------------------------------------------------------------------


def talon():
    """Left version. A scaled forearm of twisted cords grips the gem with three ringed claws."""
    w, h = TALON[2], TALON[3]
    r = Relief(w, h, "hud.dragon.black", 0.0, 1.0)
    arm = [(61, 1.5), (34, 2.5), (24, 6), (21, 15), (27, 23), (24, 34), (31, 45), (61, 46)]
    r.plate(arm, 0.0, 2.0, "round", gloss=0.4)
    for y, band, per, sl in ((8.0, 8.5, 4.6, 1.0), (17.5, 9.5, 5.0, -1.0), (27.5, 9.5, 5.0, 1.0), (38.0, 9.0, 4.6, -1.0)):
        for i in range(-1, 10):
            x = 30 + i * per
            pts = U.bezier((x - band * 0.5 * sl, y + band / 2), (x - band * 0.1 * sl, y + band * 0.1),
                           (x + band * 0.1 * sl, y - band * 0.1), (x + band * 0.5 * sl, y - band / 2), 14)
            r.sweep(pts, [2.1 * (0.5 + 0.5 * math.sin(math.pi * k / 14)) for k in range(15)], 1.2, gloss=0.8, step_px=3)
    # claws: a tapered cone with a long highlight, from knuckle to tip
    claws = (
        ((26, 4.2), (17, 3.6), (9, 2.2), (0.6, 1.4), 2.9),
        ((32, 13.5), (21, 13.0), (11, 15.0), (0.8, 20.6), 5.2),
        ((34, 31.5), (24, 30.5), (16, 30.5), (5.0, 33.6), 4.2),
    )
    rings = ((16.0, 3.4, 2.8), (23.8, 13.2, 4.9), (26.8, 30.8, 3.9))
    for (a, b, c, d, rad) in claws:
        path = U.bezier(a, b, c, d, 40)
        r.sweep(path, [rad * (1 - k / 40) ** 0.75 + 0.03 for k in range(41)], 4.5, gloss=1.0, step_px=2.0)
    for (x, y, rr) in rings:
        p = [(x - 0.2, y - rr), (x + 0.5, y), (x - 0.2, y + rr)]
        r.sweep(p, [0.8, 1.15, 0.8], 12.0, alb="hud.gold.mid", gloss=0.5, step_px=2.0)
        r.sweep([(x + 1.3, y - rr), (x + 1.8, y), (x + 1.3, y + rr)], [0.5, 0.75, 0.5], 11.8, alb="#8A6F36", gloss=0.4, step_px=2.0)
    r.h = r.h + fblur(r.a, 2.0 * PPU) * 2.0
    return r.render(amb=1.0, dif=0.9, key=1.15, fill=0.45, ao=0.75, ao_r=1.5, soften=0.1)


# stone slabs and buttons --------------------------------------------------------------


def stone_rgb(H, W, base, seed, amp=0.16):
    n = U.fbm(H, W, seed + ".c", 6.0, 4)
    g = U.grain(H, W, seed + ".g", 0.05)
    return (rgb(base)[None, None, :] * (1 + (n - 0.5) * amp * 2 + g)[..., None]).astype(np.float32)


def flat(mask, colour):
    return solid(colour, mask)


def raised(w, h, face, state, seed, base, top, side, surround, drop=2.2):
    """Raised stone slab in a recess. state n|h|p; pressed swaps bevels and shifts the face."""
    img = U.canvas(w, h, surround)
    H, W = img.shape[:2]
    x, y, fw, fh = face
    press = state == "p"
    img = U.over(img, solid("#000000", U.rect_mask(w, h, (x - 1, y - 1, fw + 2.2, fh + 1 + drop))))
    if press:
        x, y = x + 0.6, y + 0.6
    tex = stone_rgb(H, W, base, seed)
    if state == "h":
        tex = np.clip(tex * 1.30, 0, 1)
        top, side = "#E0DBD6", "#B4B5B0"
    if press:
        tex = tex * 0.7
    m = U.rect_mask(w, h, (x, y, fw, fh))
    img = U.over(img, np.dstack((tex, m)))
    lip = 1.5
    if press:
        img = U.over(img, flat(U.rect_mask(w, h, (x, y, fw, lip)), "#000000"))
        img = U.over(img, flat(U.rect_mask(w, h, (x, y, 1.0, fh)), "#050505"))
        img = U.over(img, flat(U.rect_mask(w, h, (x, y + fh - 1.0, fw, 1.0)), top))
        img = U.over(img, flat(U.rect_mask(w, h, (x + fw - 0.8, y, 0.8, fh)), side))
    else:
        grad = np.linspace(1.0, 0.72, H, dtype=np.float32)[:, None, None]
        tp = np.dstack((np.broadcast_to(rgb(top), (H, W, 3)) * grad / grad[int(y * PPU)], U.rect_mask(w, h, (x, y, fw, lip))))
        img = U.over(img, tp.astype(np.float32))
        img = U.over(img, flat(U.poly_mask(w, h, [(x, y), (x + 1.0, y + lip), (x + 1.0, y + fh), (x, y + fh)]), side))
        img = U.over(img, flat(U.rect_mask(w, h, (x + fw - 0.6, y + 0.4, 0.6, fh - 0.4)), "#000000"))
    return img


def slab(state):
    return raised(56, 22, (3, 3, 50, 14.2), state, "hud.slab", "hud.stone.face", "hud.steel.high", "hud.steel.lip", "hud.slab")


def coords_plate():
    w, h = 56, 22
    img = U.canvas(w, h, "hud.slab")
    H, W = img.shape[:2]
    g = U.gradient(H, W, [(0, "#000000"), (0.14, "#000000"), (0.34, "#282B2A"), (0.72, "#282B2A"), (0.80, "#878883"), (0.86, "#6A6B67"), (0.90, "#101010"), (1, "#101010")])
    g[..., :3] *= (1 + U.grain(H, W, "hud.coords.g", 0.03))[..., None]
    g[..., :3] = np.clip(g[..., :3], 0, 1)
    g[..., 3] = U.rect_mask(w, h, (3, 3, 50, 16.5))
    img = U.over(img, g)
    img = U.over(img, flat(U.rect_mask(w, h, (3, 8, 1.0, 11)), "#61615F"))
    return img


def mat(img, name, mask, seed, base=None, light=LIGHT_TL):
    return U.over(img, U.material(name, mask, light=light, seed=seed, base=base))


def poly(w, h, pts):
    return U.poly_mask(w, h, pts)


def icon_scroll():
    w, h = 26, 16
    img = U.canvas(w, h)
    body = poly(w, h, [(6, 3.4), (20, 3.4), (20, 12.8), (6, 12.8)])
    img = U.over(img, U.material("parchment", body, seed="icon.scroll", base="hud.parchment"))
    for y in (6.0, 8.0, 10.2):
        ln = U.stroke_mask(w, h, [(8.5, y), (11, y - 0.3), (13, y + 0.3), (15.5, y - 0.2), (17.6, y)], 0.45)
        img = U.over(img, solid("#6A5436", ln * 0.7))
    for x in (5.4, 20.6):
        r = Relief(w, h, "#000000", 0.0, 0.7)
        r.sweep([(x, 2.2), (x, 14.0)], 1.25, 0.5, alb="#B58F4A")
        for y in (1.6, 14.6):
            r.sweep([(x, y), (x, y + 0.1)], 1.15, 1.2, alb="hud.gold.bright", gloss=0.7)
        img = U.over(img, r.render(amb=1.0, dif=0.9, key=0.9, ao=0))
    return U.halo(img, 0.75)


def icon_party():
    """Two interlocked gold rings: the left passes over the right at the top, under at the bottom."""
    w, h = 26, 16
    a = [(9.5 + 5.6 * math.cos(t), 8 + 5.6 * math.sin(t)) for t in np.linspace(0, math.tau, 90)]
    b = [(16.5 + 5.6 * math.cos(t), 8 + 5.6 * math.sin(t)) for t in np.linspace(0, math.tau, 90)]
    r = Relief(w, h, "#000000", 0.0, 0.7)
    r.sweep(a, 1.25, 1.0, alb="#C8A860", step_px=2.0)
    # z dips and rises so each ring crosses over the other once
    r.sweep(b, 1.25, [2.2 if p[1] > 8 else 0.0 for p in b], alb="#D8C490", step_px=2.0)
    return U.halo(r.render(amb=1.0, dif=0.9, key=0.95, ao=0.3, ao_r=0.5), 0.75)


def sword(img, w, h, base, tip, seed):
    b, t = np.array(base, np.float32), np.array(tip, np.float32)
    d = (t - b) / np.linalg.norm(t - b)
    n = np.array([-d[1], d[0]], np.float32)
    guard = b + d * 3.4
    blade = [guard + n * 0.95, t, guard - n * 0.95]
    m = poly(w, h, [tuple(p) for p in blade])
    img = U.over(img, U.material("gold", m, seed=seed, base="#D8C490"))
    half = U.poly_mask(w, h, [tuple(guard), tuple(t), tuple(guard - n * 0.95)])
    img = U.over(img, solid("#8C7A50", half * 0.6))
    gm = U.stroke_mask(w, h, [tuple(guard + n * 2.7), tuple(guard - n * 2.7)], 0.95)
    img = U.over(img, U.material("gold", gm, seed=seed + "g", base="#C8A860"))
    hm = U.stroke_mask(w, h, [tuple(b), tuple(guard)], 0.9)
    img = U.over(img, U.material("bone", hm, seed=seed + "h", base="#8A6F36"))
    return U.over(img, U.material("gold", U.ellipse_mask(w, h, b[0], b[1], 0.95, 0.95), seed=seed + "p", base="#D8C490"))


def icon_pvp():
    w, h = 26, 16
    img = U.canvas(w, h)
    img = sword(img, w, h, (7.4, 14.6), (19.2, 1.2), "icon.pvp.a")
    img = sword(img, w, h, (18.6, 14.6), (6.8, 1.2), "icon.pvp.b")
    return U.halo(img, 0.75)


def ministat(on):
    w, h = 16, 11
    img = U.canvas(w, h, "#050505")
    img = U.over(img, flat(U.rect_mask(w, h, (0, 0, w, 0.6)), "#E3DDD8"))
    img = U.over(img, flat(U.rect_mask(w, h, (0, 0, 0.6, h)), "#8D8E89"))
    img = U.over(img, flat(U.rect_mask(w, h, (0, h - 0.6, w, 0.6)), "#8D8E89"))
    img = U.over(img, flat(U.rect_mask(w, h, (w - 0.6, 0, 0.6, h)), "#E3DDD8"))
    c = "#F0C870" if on else "#3A3A3A"
    bar = U.rect_mask(w, h, (3, 4, 10, 3))
    img = U.over(img, flat(bar, c))
    img = U.over(img, flat(U.rect_mask(w, h, (3, 4, 10, 0.8)), "#FFF0C0" if on else "#505050"))
    return img


# EXP ----------------------------------------------------------------------------------


def worn_h(w, h, rect, colour, seed, lo=0.55):
    """Horizontal inlay, brightness wandering along x and uniform across y."""
    H, W = int(h * PPU), int(w * PPU)
    n = U.fbm(1 * 8, W, seed, 6.0, 3)[0]
    t = lo + (1 - lo) * smooth(0.25, 0.6, n)
    c = rgb(colour)[None, None, :] * t[None, :, None]
    return np.dstack((np.broadcast_to(c, (H, W, 3)), U.rect_mask(w, h, rect))).astype(np.float32)


def exp_frame():
    w, h = 207, 10
    img = U.canvas(w, h)
    img = U.over(img, worn_h(w, h, (2, 0.2, 203, 1.2), "hud.ivory.line", "exp.top", 0.8))
    img = U.over(img, worn_h(w, h, (2, 8.2, 203, 1.3), "#D0BEA0", "exp.bot", 0.35))
    for x in (2.2, 203.4):
        img = U.over(img, flat(U.rect_mask(w, h, (x, 0.2, 1.4, 9.3)), "#C0B9A7"))
    return img


def exp_trough():
    w, h = 199, 6
    img = U.canvas(w, h, "#070300")
    return img


def exp_fill():
    w, h = 8, 5
    H, W = int(h * PPU), int(w * PPU)
    img = U.gradient(H, W, [(0, "#FBE4A0"), (0.18, "#F0C870"), (0.8, "#F0C870"), (1, "#B98F42")])
    return img


def exp_cup():
    w, h = 12, 12
    pts = U.bezier((1.2, 2.5), (1.0, 12.8), (11.0, 12.8), (10.8, 2.5), 32)
    r = Relief(w, h, "#000000", 0.0, 0.7)
    r.sweep(pts, 0.8, 0.3, alb="hud.gold.mid", step_px=2.0)
    r.sweep([(1.2, 2.5), (1.2, 2.6)], 1.15, 0.5, alb="hud.gold.bright", gloss=0.8)
    r.sweep([(10.8, 2.5), (10.8, 2.6)], 1.15, 0.5, alb="hud.gold.bright", gloss=0.8)
    return r.render(amb=1.0, dif=0.9, key=0.9, ao=0)


# Q/W/E --------------------------------------------------------------------------------


def ivory_lines(w, h, rects, seed):
    """Polished cream lines with gold flecks; rects are (x, y, w, h) in u."""
    H, W = int(h * PPU), int(w * PPU)
    m = np.zeros((H, W), np.float32)
    for r in rects:
        m = np.maximum(m, U.rect_mask(w, h, r))
    n = U.fbm(H, W, seed, 2.2, 3)
    fleck = smooth(0.70, 0.84, n)[..., None]
    c = rgb("#B8B2A4")[None, None, :] * (1 - fleck) + rgb("#B79A58") * fleck
    c = np.clip(c * (0.82 + 0.18 * smooth(0.3, 0.6, n))[..., None], 0, 1)
    shadow = U.over(U.canvas(w, h), solid("#000000", np.clip(fblur(m, 0.3 * PPU) * 1.6, 0, 0.8)))
    return U.over(shadow, np.dstack((c, m)).astype(np.float32))


def qwe_slot(w, state, cheek=0.0):
    h = 28
    img = U.canvas(w, h)
    H, W = img.shape[:2]
    x0 = cheek
    fw = w - x0 - 0.2
    if cheek:
        stone = stone_rgb(H, W, "#4A4A47", "qwe.cheek")
        ramp = (0.45 + 0.75 * np.linspace(0, 1, W, dtype=np.float32) / (cheek * PPU / W))[None, :, None]
        stone = np.clip(stone * np.minimum(ramp, 1.3), 0, 1)
        img = U.over(img, np.dstack((stone, U.rect_mask(w, h, (0, 1.6, cheek + 1, 25)))))
    stops = [(0, "#1B1B19"), (0.45, "#222220"), (0.92, "#4C4C4A"), (1, "#4C4C4A")]
    if state == "h":
        stops = [(0, "#32323A"), (0.45, "#3C3C40"), (0.92, "#6E6E6A"), (1, "#6E6E6A")]
    if state == "p":
        stops = [(0, "#0A0A09"), (0.45, "#141413"), (0.92, "#2C2C2B"), (1, "#2C2C2B")]
    g = U.gradient(H, W, stops)
    g[..., 3] = U.rect_mask(w, h, (x0 + 0.8, 0.8, fw - 1, h - 1.8))
    img = U.over(img, g)
    t = 1.2
    rects = [(x0, 0, fw, t), (x0, h - t - 0.4, fw, t), (x0, 0, t, h - 0.4), (w - t - 0.2, 12, t, h - 12.4)]
    if state == "p":
        img = U.over(img, solid("#000000", U.rect_mask(w, h, (x0 + t, t, fw - t, 1.6))))
        img = U.over(img, solid("#000000", U.rect_mask(w, h, (x0 + t, t, 1.6, h - t))))
    lines = ivory_lines(w, h, rects, f"qwe.frame{w}")
    if state == "h":
        lines[..., :3] = np.clip(lines[..., :3] * 1.2, 0, 1)
    return U.over(img, lines)


def qwe_notch():
    w, h = 18, 14
    H, W = int(h * PPU), int(w * PPU)
    yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
    x = ((xx + 0.5) / PPU - w / 2) / (w / 2 - 0.2)
    y = ((yy + 0.5) / PPU - h / 2) / (h / 2 - 0.2)
    sup = np.abs(x) ** 3.2 + np.abs(y) ** 3.2
    outer = (sup <= 1).astype(np.float32)
    x2, y2 = x * 1.14, y * 1.19
    inner = (np.abs(x2) ** 3.2 + np.abs(y2) ** 3.2 <= 1).astype(np.float32)
    r = Relief(w, h, "#000000", 0.0, 0.6)
    r.mask_plate(outer > 0.5, 0.0, 0.9, "round", alb="#8D7B5A")
    img = r.render(amb=1.0, dif=0.9, key=0.7, ao=0)
    img = U.over(img, solid("#030000", inner))
    return img


GLYPHS = {
    "q": [U.bezier((3.5, 0.8), (-0.6, 0.8), (-0.6, 8.2), (3.5, 8.2), 16) + U.bezier((3.5, 8.2), (7.6, 8.2), (7.6, 0.8), (3.5, 0.8), 16), [(3.8, 6.2), (6.4, 9.4)]],
    "w": [[(0.5, 0.8), (1.9, 9.0), (3.5, 3.0), (5.1, 9.0), (6.5, 0.8)]],
    "e": [[(5.9, 0.9), (1.2, 0.9), (1.2, 9.0), (5.9, 9.0)], [(1.2, 4.9), (4.8, 4.9)]],
}


def qwe_letter(ch):
    w, h = 7, 10
    img = U.canvas(w, h)
    for pts in GLYPHS[ch]:
        img = U.over(img, solid("#000000", U.stroke_mask(w, h, pts, 2.1) * 0.85))
    for pts in GLYPHS[ch]:
        m = U.stroke_mask(w, h, pts, 1.35)
        img = U.over(img, solid("#D6C69C", m))
    for pts in GLYPHS[ch]:
        img = U.over(img, solid("#100C04", U.stroke_mask(w, h, pts, 0.5)))
    return img


# skill tile ---------------------------------------------------------------------------


def skill_bracket():
    w, h = 32, 36
    r = Relief(w, h, "#000000", 0.0, 0.6)
    gold = "#786149"
    for y in (0.6, 34.2):
        r.plate([(2, y), (30, y), (30, y + 1.4), (2, y + 1.4)], 0.2, 0.6, "round", alb=gold)
    for side in (0, 1):
        # column bulging at the waist, beaded
        path, rad = [], []
        for t in np.linspace(0, 1, 60):
            y = 1.0 + t * 34
            b = math.exp(-(((t - 0.58) / 0.16) ** 2))
            x = 1.9 + 1.0 * b * math.sin(t * 34)
            path.append((x if side == 0 else w - x, y))
            rad.append(1.15 + 0.5 * b * (0.5 + 0.5 * math.cos(t * 58)))
        r.sweep(path, rad, 0.3, alb="#8E7458", gloss=0.8, step_px=2.0)
    img = r.render(amb=1.0, dif=0.9, key=0.75, fill=0.3, ao=0.5, ao_r=0.6)
    return img


def skill_tile(state):
    w, h = 19, 28
    H, W = int(h * PPU), int(w * PPU)
    base = "hud.stone.tile" if state != "h" else "#5A534A"
    tex = stone_rgb(H, W, base, "skill.tile", 0.10)
    img = np.dstack((tex, np.ones((H, W), np.float32))).astype(np.float32)
    img = U.over(img, flat(U.rect_mask(w, h, (0, 0, w, 1.0)), "#A59D89"))
    img = U.over(img, flat(U.rect_mask(w, h, (w - 0.7, 0, 0.7, h)), "#7D776E"))
    img = U.over(img, flat(U.rect_mask(w, h, (0, h - 0.6, w, 0.6)), "#1A1814"))
    img = U.over(img, flat(U.rect_mask(w, h, (0, 0, 0.5, h)), "#2E2A24"))
    if state == "d":
        img = desat(img, 0.5, 0.55)
    return img


def skill_icon():
    """Engraved arrow through a ring: dark cut with a thin light lower-right lip."""
    w, h = 17, 26
    m = np.zeros((int(h * PPU), int(w * PPU)), np.float32)
    head = [(16.2, 13), (9.4, 8.4), (10.8, 12.2), (3.2, 12.2), (1.0, 10.4), (2.4, 13), (1.0, 15.6), (3.2, 13.8), (10.8, 13.8), (9.4, 17.6)]
    m = np.maximum(m, poly(w, h, head))
    for a, b, c, d in (((3.0, 7.5), (5.0, 1.0), (12.0, 1.0), (14.0, 7.5)), ((3.0, 18.5), (5.0, 25.0), (12.0, 25.0), (14.0, 18.5))):
        m = np.maximum(m, U.stroke_mask(w, h, U.bezier(a, b, c, d, 24), 1.2))
    H, W = m.shape
    img = U.canvas(w, h)
    lip = np.roll(m, (int(0.4 * PPU), int(0.4 * PPU)), axis=(0, 1))
    img = U.over(img, solid("#8C8474", lip * 0.75))
    return U.over(img, solid("#1E1B16", m))


# icon buttons -------------------------------------------------------------------------


def iconbtn_plinth():
    w, h = 101, 30
    H, W = int(h * PPU), int(w * PPU)
    img = U.canvas(w, h)
    tex = stone_rgb(H, W, "#5A5A58", "iconbtn.plinth", 0.12)
    xu = (np.arange(W, dtype=np.float32) + 0.5) / PPU
    edge = np.minimum(xu, w - xu)
    t = 0.45 + 0.75 * np.exp(-(((edge - 2.5) / 2.2) ** 2))
    t = np.where(edge > 6, 0.14, t)[None, :, None]
    yy = np.linspace(1.0, 0.75, H, dtype=np.float32)[:, None, None]
    img = U.over(img, np.dstack((np.clip(tex * t * yy, 0, 1), U.rect_mask(w, h, (0, 0.5, w, h - 1), 1.5))).astype(np.float32))
    return img


def iconbtn_frame(state):
    w, h = 28, 28
    H, W = int(h * PPU), int(w * PPU)
    img = U.canvas(w, h, "#000000")
    yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
    x = (xx + 0.5) / PPU - w / 2
    y = (yy + 0.5) / PPU - h / 2
    # inner stone: lit centre falling off to a dark vignette
    v = np.clip(1 - (np.maximum(np.abs(x), np.abs(y)) - 4) / 8.5, 0, 1)
    tex = stone_rgb(H, W, "#646155", "iconbtn.face", 0.14)
    dark = rgb("#0D0500")
    t = (v**1.3)[..., None]
    col = dark * (1 - t) + tex * t
    if state == "h":
        col = np.clip(col * 1.4, 0, 1)
    if state == "p":
        col = col * 0.8
    off = 0.7 if state == "p" else 0.0
    m = U.rect_mask(w, h, (3.2 + off, 3.2 + off, 21.6 - off, 21.6 - off))
    img = U.over(img, np.dstack((col, m)).astype(np.float32))
    if state == "p":
        img = U.over(img, solid("#000000", U.rect_mask(w, h, (3.2, 3.2, 21.6, 2.2))))
        img = U.over(img, solid("#000000", U.rect_mask(w, h, (3.2, 3.2, 2.2, 21.6))))
    t = 1.2
    return U.over(img, ivory_lines(w, h, [(0, 0, w, t), (0, h - t, w, t), (0, 0, t, h), (w - t, 0, t, h)], "iconbtn.frame"))


def icon_banner():
    w, h = 22, 22
    img = U.canvas(w, h)
    cloth = [(5.5, 4.4), (16.5, 4.4), (16.5, 19.5), (11, 15.8), (5.5, 19.5)]
    img = U.over(img, U.material("parchment", poly(w, h, cloth), seed="icon.banner", base="#CFB68F"))
    # chevron charge, stitched on
    ch = U.stroke_mask(w, h, [(7.4, 8.0), (11, 11.2), (14.6, 8.0)], 1.2)
    img = U.over(img, solid("#7A5B33", ch))
    img = U.over(img, solid("#6E5A3E", U.stroke_mask(w, h, [(7.0, 5.6), (15.0, 5.6)], 0.4)))
    r = Relief(w, h, "#000000", 0.0, 0.7)
    r.sweep([(3.2, 4.0), (18.8, 4.0)], 0.75, 0.3, alb="#B58F4A")
    r.sweep([(3.2, 4.0), (3.2, 4.1)], 1.0, 0.5, alb="hud.gold.bright")
    r.sweep([(18.8, 4.0), (18.8, 4.1)], 1.0, 0.5, alb="hud.gold.bright")
    img = U.over(img, r.render(amb=1.0, dif=0.9, key=0.9, ao=0))
    ring = U.ellipse_mask(w, h, 11, 2.1, 1.4, 1.4) * (1 - U.ellipse_mask(w, h, 11, 2.1, 0.7, 0.7))
    img = U.over(img, U.material("gold", ring, seed="icon.banner.ring", base="#C8A860"))
    return U.halo(img, 0.75)


def icon_lens():
    w, h = 22, 22
    r = Relief(w, h, "#000000", 0.0, 0.7)
    r.sweep(U.bezier((13.2, 13.2), (15, 15), (17, 17), (19.2, 19.2), 12), [0.9] * 6 + [1.25] * 7, 0.5, alb="#CFB68F", gloss=0.4)
    ring = [(9 + 6.6 * math.cos(a), 9 + 6.6 * math.sin(a)) for a in np.linspace(0, math.tau, 80)]
    r.sweep(ring, 1.15, 0.8, alb="#C8A860", gloss=0.7, step_px=2.0)
    img = r.render(amb=1.0, dif=0.9, key=0.95, ao=0.3, ao_r=0.5)
    gl = U.ellipse_mask(w, h, 9, 9, 5.7, 5.7)
    glass = U.canvas(w, h)
    glass = U.over(glass, solid("#1A2428", gl * 0.55))
    shine = U.stroke_mask(w, h, [(5.2, 8.0), (6.0, 6.0), (8.0, 5.0)], 0.7)
    glass = U.over(glass, solid("#E8E4D4", shine * 0.75))
    return U.halo(U.over(glass, img), 0.7)


def icon_satchel():
    w, h = 22, 22
    img = U.canvas(w, h)
    bag = [(3.4, 9), (18.6, 9), (19.6, 18), (17.8, 20), (4.2, 20), (2.4, 18)]
    img = U.over(img, U.material("parchment", poly(w, h, bag), seed="icon.satchel", base="#C8A468"))
    flap = [(3.2, 7), (18.8, 7), (18.8, 11), (13, 13.8), (9, 13.8), (3.2, 11)]
    img = U.over(img, solid("#000000", poly(w, h, [(p[0], p[1] + 0.5) for p in flap]) * 0.5))
    img = U.over(img, U.material("parchment", poly(w, h, flap), seed="icon.satchel.f", base="#E1CB98"))
    strap = U.stroke_mask(w, h, U.bezier((5.5, 7.4), (6.5, 0.8), (15.5, 0.8), (16.5, 7.4), 24), 1.1)
    img = U.over(img, U.material("parchment", strap, seed="icon.satchel.s", base="#95846F"))
    clasp = U.ellipse_mask(w, h, 11, 12.8, 1.3, 1.3)
    img = U.over(img, U.material("gold", clasp, seed="icon.satchel.c", base="#E0B860"))
    return U.halo(img, 0.75)


# chat ---------------------------------------------------------------------------------


def worley_wrap(H, W, n, name):
    """Tileable cellular distance: 0 at feature points, growing to the cell borders."""
    g = U.rng(name)
    pts = g.random((n, 2), dtype=np.float32) * np.array([H, W], np.float32)
    # coarse evaluation, then box resize, to stay cheap
    h2, w2 = H // 4, W // 4
    yy, xx = np.mgrid[0:h2, 0:w2].astype(np.float32)
    yy, xx = yy * 4, xx * 4
    d = np.full((h2, w2), 1e9, np.float32)
    e = np.full((h2, w2), 1e9, np.float32)
    for p in pts:
        dy = np.abs(yy - p[0])
        dx = np.abs(xx - p[1])
        dy, dx = np.minimum(dy, H - dy), np.minimum(dx, W - dx)
        q = np.sqrt(dy * dy + dx * dx)
        e = np.where(q < d, d, np.minimum(e, q))
        d = np.minimum(d, q)
    f1 = np.asarray(Image.fromarray(d).resize((W, H), Image.BILINEAR))
    f2 = np.asarray(Image.fromarray(e).resize((W, H), Image.BILINEAR))
    return f1.astype(np.float32), f2.astype(np.float32)


def chat_rock():
    w = 64
    N = int(w * PPU)
    f1, f2 = worley_wrap(N, N, 42, "chat.rock.cells")
    border = smooth(0.0, 18.0, f2 - f1)
    dome = (1 - np.clip(f1 / (9.0 * PPU), 0, 1)) ** 1.5
    n = U.fbm(N, N, "chat.rock.n", 5.0, 5)
    m = U.fbm(N, N, "chat.rock.m", 1.4, 3)
    h = dome * 2.6 + border * 1.5 + n * 2.2 + m * 0.55
    hb = fblur(h, 3, True)
    gy, gx = np.gradient(hb)
    nx, ny = -gx * PPU * 0.32, -gy * PPU * 0.32
    nl = np.sqrt(nx * nx + ny * ny + 1)
    light = np.array(LIGHT_TL, np.float32)
    light /= np.linalg.norm(light)
    d = np.clip((nx * light[0] + ny * light[1] + light[2]) / nl, 0, 1) / light[2]
    occ = np.clip(0.68 + 0.32 * smooth(0.0, 0.8, border) * (0.45 + 0.55 * n * 1.4), 0, 1.2)
    lum = d**2.0 * occ
    lum = lum.mean() + (lum - lum.mean()) * 0.5
    lo, mid, hi = rgb("#0A0906"), rgb("#15120B"), rgb("chat.bg.high")
    t = lum[..., None]
    a = np.clip(t / 0.75, 0, 1)
    b = np.clip((t - 0.75) / 1.1, 0, 1)
    col = lo * (1 - a) + mid * a
    col = col * (1 - b) + hi * b
    col = np.clip(col, 0, 1)
    # centre the mean on the token
    col = np.clip(col * (rgb("chat.bg") / np.maximum(col.reshape(-1, 3).mean(axis=0), 1e-3)), 0, 1)
    return np.dstack((col, np.ones((N, N), np.float32))).astype(np.float32)


def chat_post(left=True):
    """Pale gold strip beside black ropework, periodic in 4 u."""
    w, h = 5, 44
    tw_h = h * 3
    r = Relief(w, tw_h, "#050505", 1.0, 0.8)
    for i in range(-1, 36):
        y = i * 4.0
        r.sweep([(0.8, y + 3.2), (3.0, y + 0.8)], 1.1, 0.3, alb="#1C1C1D", gloss=0.6, step_px=3)
    img = r.render(amb=1.0, dif=0.9, key=0.45, fill=0.2, ao=0.4, ao_r=0.5)
    H = int(h * PPU)
    img = img[H : H * 2]
    n = U.fbm(H, 1, "chat.post", 8.0, 3)
    c = rgb("#948468")[None, None, :] * (0.7 + 0.5 * smooth(0.3, 0.6, n))[:, :, None]
    strip = np.broadcast_to(c, (H, int(w * PPU), 3))
    img = U.over(img, np.dstack((np.clip(strip, 0, 1), U.rect_mask(w, h, (3.7, 0, 1.3, h)))).astype(np.float32))
    img = U.over(img, flat(U.rect_mask(w, h, (4.1, 0, 0.45, h)), "#DCC9A6"))
    return img if left else U.mirror_x(img)


def chat_rail():
    w, h = 64, 4
    tw = w * 3
    img = U.canvas(tw, h, "#050505")
    img = U.over(img, chain_band(tw, h, 2.6, x0=-7.0, period=4.0))  # phase keeps the wrap column calm
    img = U.over(img, flat(U.rect_mask(tw, h, (0, 0, tw, 0.8)), "#CDCDC4"))
    W = int(w * PPU)
    return img[:, W : W * 2]


def chat_input_frame():
    w, h = 253, 17
    img = U.canvas(w, h)
    img = U.over(img, worn_h(w, h, (3, h - 1.0, w - 6, 0.7), "hud.ivory.line", "chat.in.a", 0.75))
    img = U.over(img, worn_h(w, h, (3, 0.3, w - 6, 0.7), "hud.ivory.line", "chat.in.b", 0.75))
    for x in (0.0, w - 1.0):
        img = U.over(img, flat(U.rect_mask(w, h, (x, 0.3, 1.0, h - 1.3)), "#C0B9A7"))
    return img


def chat_btn(state):
    w, h = 14, 14
    H, W = int(h * PPU), int(w * PPU)
    base = {"n": "#937953", "h": "#A58A62", "p": "#7A6444"}[state]
    tex = stone_rgb(H, W, base, "chat.btn", 0.12)
    img = np.dstack((tex, np.ones((H, W), np.float32))).astype(np.float32)
    top, bot = ("#F3E8C7", "#3A2E1A") if state != "p" else ("#3A2E1A", "#F3E8C7")
    img = U.over(img, flat(U.rect_mask(w, h, (0, 0, w, 1.0)), top))
    img = U.over(img, flat(U.rect_mask(w, h, (0, 0, 1.0, h)), top))
    img = U.over(img, flat(U.rect_mask(w, h, (0, h - 0.8, w, 0.8)), bot))
    img = U.over(img, flat(U.rect_mask(w, h, (w - 0.8, 0, 0.8, h)), bot))
    o = 0.5 if state == "p" else 0.0
    curl = U.bezier((4.2 + o, 9.8 + o), (2.6 + o, 5.2 + o), (6.6 + o, 2.2 + o), (9.8 + o, 4.4 + o), 24) + U.bezier((9.8 + o, 4.4 + o), (12.2 + o, 7.0 + o), (8.4 + o, 10.0 + o), (6.6 + o, 7.4 + o), 24)[1:]
    rr = Relief(w, h, "#000000", 0.0, 0.7)
    rr.sweep(curl, [1.1 * (1 - 0.6 * k / len(curl)) for k in range(len(curl))], 0.3, alb="#D69836", step_px=2.0)
    return U.over(img, U.drop_shadow(rr.render(amb=1.0, dif=0.9, key=1.0, ao=0), 0.3, 0.3, 0.1, opacity=0.7))


def stone_cell(w, h, state, seed):
    H, W = int(h * PPU), int(w * PPU)
    base = {"n": "#443E36", "h": "#524B41", "p": "#332E28"}[state]
    tex = stone_rgb(H, W, base, seed, 0.12)
    img = np.dstack((tex, np.ones((H, W), np.float32))).astype(np.float32)
    top, bot = ("#6B6455", "#14110D") if state != "p" else ("#14110D", "#6B6455")
    img = U.over(img, flat(U.rect_mask(w, h, (0, 0, w, 0.7)), top))
    return U.over(img, flat(U.rect_mask(w, h, (0, h - 0.7, w, 0.7)), bot))


def chat_scroll(direction, state):
    w, h = 14, 9
    img = stone_cell(w, h, state, "chat.scroll")
    o = 0.4 if state == "p" else 0.0
    if direction == "up":
        pts = [(7, 1.4), (11.4, 5.2), (9, 5.2), (9, 7.6), (5, 7.6), (5, 5.2), (2.6, 5.2)]
    else:
        pts = [(7, 7.6), (11.4, 3.8), (9, 3.8), (9, 1.4), (5, 1.4), (5, 3.8), (2.6, 3.8)]
    pts = [(x + o, y + o) for x, y in pts]
    img = U.over(img, solid("#0C0A07", U.stroke_mask(w, h, [(x + 0.3, y + 0.3) for x, y in pts], 0.7, closed=True)))
    return U.over(img, solid("#897B60", U.stroke_mask(w, h, pts, 0.6, closed=True)))


def chat_thumb():
    w, h = 14, 10
    img = stone_cell(w, h, "n", "chat.thumb")
    return U.over(img, flat(U.rect_mask(w, h, (2, 4.2, 10, 1.4)), "#C8BDA0"))


def chat_track():
    w, h = 14, 8
    H, W = int(h * PPU), int(w * PPU)
    return U.gradient(H, W, [(0, "#3A3430"), (1, "#3A3430")])


def chat_bracket():
    w, h = 3, 3
    img = U.canvas(w, h)
    img = U.over(img, flat(U.rect_mask(w, h, (0, 0, w, 1.0)), "#F3EFE6"))
    return U.over(img, flat(U.rect_mask(w, h, (0, 0, 1.0, h)), "#F3EFE6"))


def chat_postbar():
    H, W = int(8 * PPU), int(8 * PPU)
    return U.gradient(H, W, [(0, "#A28134"), (0.15, "chat.post.bg"), (1, "chat.post.bg")])


# cursors ------------------------------------------------------------------------------


CUR = 32
TIP = (2.2, 2.2)
AXIS = math.radians(47)


def cp(a, b, tip=TIP, ang=AXIS):
    """(a along the finger from the tip toward the wrist, b across) to sprite u."""
    d = (math.cos(ang), math.sin(ang))
    n = (-d[1], d[0])
    return (tip[0] + d[0] * a + n[0] * b, tip[1] + d[1] * a + n[1] * b)


def gauntlet(pose):
    """pose: point, press (tip bent short), fist (grab), grip (holding a blade)."""
    r = Relief(CUR, CUR, "#6E7074", 0.0, 0.9)
    steel = "#7A7C80"
    dark = "#3C3D40"
    gold = "hud.gold.mid"
    # flared cuff
    r.plate([cp(21.0, -5.8), cp(21.0, 5.8), cp(29.5, 8.6), cp(31.5, 4), cp(31.5, -4), cp(29.5, -8.6)], 0.2, 2.2, "round", alb=dark)
    r.plate([cp(20.2, -6.2), cp(20.2, 6.2), cp(22.0, 6.6), cp(22.0, -6.6)], 3.0, 0.6, "round", alb=gold, gloss=0.6)
    # back of the hand: lames
    for a0, a1, wd, z in ((16.0, 21.0, 6.0, 2.0), (12.6, 16.8, 6.2, 2.6)):
        r.plate([cp(a0, -wd * 0.95), cp(a0, wd), cp(a1, wd), cp(a1, -wd)], z, 1.3, "round", alb=steel)
    # folded fingers on the b>0 side, thumb on b<0
    if pose in ("point", "press", "grip"):
        for i, b in enumerate((0.6, 3.0, 5.2)):
            r.sweep([cp(13.2, b), cp(9.4 + i * 0.5, b + 0.1), cp(11.4 + i * 0.3, b + 0.2)], [1.25, 1.15, 1.0], 4.0, alb=steel, step_px=2.0)
    r.sweep([cp(18, -5.6), cp(13.0, -7.4), cp(9.0, -6.2)], [1.9, 1.4, 0.9], 4.2, alb=steel, step_px=2.0)
    if pose == "point":
        segs = [(0.0, 5.0, 1.0, 1.35), (5.0, 9.6, 1.35, 1.5), (9.6, 13.6, 1.5, 1.6)]
    elif pose == "press":
        segs = []
        r.sweep([cp(13.6, -1.3), cp(9.0, -1.5), cp(6.4, -0.6), cp(6.6, 1.4)], [1.6, 1.5, 1.35, 1.0], 5.0, alb=steel, step_px=2.0)
    else:
        segs = []
    for i, (a0, a1, r0, r1) in enumerate(segs):
        o = -1.3
        r.sweep([cp(a0 + 0.2, o), cp(a1, o)], [r0, r1], 4.8 - i * 0.2, alb=steel, step_px=2.0)
    if pose == "point":
        r.plate([cp(segs[0][0] - 0.0, -1.3), cp(segs[0][0] + 2.4, 0.0), cp(segs[0][0] + 2.4, -2.6)], 6.2, 0.7, "round", alb="#9A9CA0")
    if pose in ("fist", "grip"):
        for i, b in enumerate((-4.2, -1.4, 1.4, 4.2)):
            r.sweep([cp(13.5, b), cp(9.0, b), cp(10.2, b + 0.1)], [1.6, 1.5, 1.3], 4.0, alb=steel, step_px=2.0)
        r.sweep([cp(16, -6.2), cp(11.5, -4.4), cp(9.0, -1.6)], [1.7, 1.3, 1.0], 6.0, alb=steel, step_px=2.0)
    return r


def cursor_render(r):
    img = r.render(amb=0.95, dif=0.85, key=1.1, fill=0.45, ao=0.6, ao_r=0.9)
    return U.halo(img, 0.55, opacity=0.95)


def cursor_pointer():
    return cursor_render(gauntlet("point"))


def cursor_pressed():
    """Index curled in, and the whole hand pushed 1 u toward the wrist as it clicks."""
    return np.roll(cursor_render(gauntlet("press")), (PPU, PPU), axis=(0, 1))


def cursor_grab():
    return cursor_render(gauntlet("fist"))


def cursor_attack():
    r = gauntlet("grip")
    # short blade running to the hotspot
    tip = TIP
    a = cp(9.5, -1.6)
    b = cp(9.5, 1.6)
    m = U.poly_mask(CUR, CUR, [cp(0, -1.2), a, b])
    r.mask_plate(m > 0.5, 6.5, 1.0, "chamfer", alb="#C8CAD0", gloss=1.0)
    r.sweep([cp(9.7, -3.2), cp(9.7, 3.2)], 0.65, 7.8, alb="hud.gold.mid", gloss=0.6)
    img = cursor_render(r)
    return img


def cursor_forbid():
    img = cursor_render(gauntlet("point"))
    cx, cy, rad = 22.5, 22.5, 7.2
    ring = U.ellipse_mask(CUR, CUR, cx, cy, rad, rad) * (1 - U.ellipse_mask(CUR, CUR, cx, cy, rad - 1.6, rad - 1.6))
    bar = U.stroke_mask(CUR, CUR, [(cx - rad * 0.68, cy - rad * 0.68), (cx + rad * 0.68, cy + rad * 0.68)], 1.6)
    sign = np.maximum(ring, bar)
    img = U.over(img, solid("#000000", U.stroke_mask(CUR, CUR, [(cx - rad * 0.7, cy - rad * 0.7), (cx + rad * 0.7, cy + rad * 0.7)], 2.8) * 0.8))
    outer = U.ellipse_mask(CUR, CUR, cx, cy, rad + 0.7, rad + 0.7) * (1 - U.ellipse_mask(CUR, CUR, cx, cy, rad - 2.3, rad - 2.3))
    img = U.over(img, solid("#000000", outer * 0.85))
    return U.over(img, solid("#C21A0A", sign))


# assembly -----------------------------------------------------------------------------


def hud_sprites():
    s = []
    add = lambda name, img, **kw: s.append(U.Sprite(name, img, **kw))
    bl = base_left()
    add("hud.base.left", bl)
    add("hud.base.right", U.mirror_x(bl))
    add("hud.base.centre", base_centre())
    add("hud.gapfill", gapfill(), tile="x")
    s.extend(gem_set("hp"))
    s.extend(gem_set("mp"))
    add("gauge.left.fill", gauge_prism("gauge.left", "gauge.left.face", "gauge.left.side", "gauge.left.edge", "#924C0F", "#A84D0C"))
    add("gauge.right.fill", gauge_prism("gauge.right", "gauge.right.face", "gauge.right.side", "gauge.right.edge", "#2A1430", "#301838", mottle=0.08))
    add("gauge.empty", gauge_prism("gauge.empty", "#0E0D0B", "#0A0908", "#181612", "#040403", "#040403", mottle=0.04))
    cap = gauge_cap()
    add("gauge.cap.left", cap)
    add("gauge.cap.right", U.mirror_x(cap))
    d = dragon()
    w, h = DRAGON_W * 4, DRAGON_H * 4
    eye = [int(v * 4) for v in EYE_RECT_U]
    fang = [int(v * 4) for v in FANG_RECT_U]
    add("hud.dragon.left", d, pivot=(0, h), meta={"eye": eye, "fang": fang})
    add("hud.dragon.right", U.mirror_x(d), pivot=(w, h), meta={"eye": [w - eye[0] - eye[2], *eye[1:]], "fang": [w - fang[0] - fang[2], *fang[1:]]})
    t = talon()
    add("hud.talon.left", t)
    add("hud.talon.right", U.mirror_x(t))
    for st, suf in (("n", "normal"), ("h", "hover"), ("p", "pressed")):
        add(f"hud.slab.{suf}", slab(st))
        add(f"qwe.slot.q.{suf}", qwe_slot(33, st, 5.0))
        add(f"qwe.slot.w.{suf}", qwe_slot(29, st))
        add(f"qwe.slot.e.{suf}", qwe_slot(28, st))
        add(f"iconbtn.frame.{suf}", iconbtn_frame(st))
        add(f"chat.btn.{suf}", chat_btn(st))
        add(f"chat.scroll.up.{suf}", chat_scroll("up", st))
        add(f"chat.scroll.down.{suf}", chat_scroll("down", st))
    add("hud.coordsplate", coords_plate())
    add("icon.scroll", icon_scroll())
    add("icon.party", icon_party())
    add("icon.pvp", icon_pvp())
    add("hud.ministat.off", ministat(False))
    add("hud.ministat.on", ministat(True))
    add("exp.frame", exp_frame(), slice=(16, 0, 16, 0))
    add("exp.trough", exp_trough(), slice=(16, 0, 16, 0))
    add("exp.fill", exp_fill(), tile="x")
    add("exp.tick", np.dstack((np.broadcast_to(rgb("#5A4A28"), (24 * 4, 4 * 4, 3)), np.ones((24 * 4, 4 * 4), np.float32))).astype(np.float32))
    add("exp.cup", exp_cup())
    add("qwe.notch", qwe_notch())
    for ch in "qwe":
        add(f"qwe.letter.{ch}", qwe_letter(ch))
    add("skill.bracket", skill_bracket())
    add("skill.tile.normal", skill_tile("n"))
    add("skill.tile.hover", skill_tile("h"))
    add("skill.tile.disabled", skill_tile("d"))
    add("skill.icon.default", skill_icon())
    add("iconbtn.plinth", iconbtn_plinth())
    add("icon.banner", icon_banner())
    add("icon.lens", icon_lens())
    add("icon.satchel", icon_satchel())
    add("chat.rock", chat_rock(), tile="xy")
    add("chat.post.left", chat_post(True), tile="y")
    add("chat.post.right", chat_post(False), tile="y")
    add("chat.rail", chat_rail(), tile="x")
    add("chat.input.frame", chat_input_frame(), slice=(16, 0, 16, 0))
    add("chat.field.main", U.canvas(8, 12, "chat.input"), slice=(8, 0, 8, 0))
    add("chat.field.whisper", U.canvas(8, 12, "chat.whisper"), slice=(8, 0, 8, 0))
    add("chat.scroll.thumb", chat_thumb())
    add("chat.scroll.track", chat_track(), tile="y")
    add("chat.bracket", chat_bracket())
    add("chat.postbar", chat_postbar(), slice=(8, 0, 8, 0))
    return s


HUD_LAYOUT = {
    "bar": [BAR_W, BAR_H], "split": [0, LEFT_W, LEFT_W + CENTRE_W, BAR_W],
    "slab": SLAB, "coords": COORDS, "gaugeFill": GAUGE_FILL, "gaugeCap": GAUGE_CAP, "gem": GEM, "talon": TALON,
}


def cursor_sprites():
    piv = (int(TIP[0] * 4) + 2, int(TIP[1] * 4) + 2)
    return [
        U.Sprite("cursor.pointer", cursor_pointer(), pivot=piv),
        U.Sprite("cursor.pressed", cursor_pressed(), pivot=tuple(int((v + 1) * 4) for v in cp(6.0, -0.6))),
        U.Sprite("cursor.grab", cursor_grab(), pivot=(44, 44)),
        U.Sprite("cursor.attack", cursor_attack(), pivot=piv),
        U.Sprite("cursor.forbid", cursor_forbid(), pivot=piv),
    ]


def main(out_root: Path) -> None:
    out = Path(out_root) / "hud"
    a = U.Atlas("hud", out, meta={"layout": {k: list(v) for k, v in HUD_LAYOUT.items()}})
    for s in hud_sprites():
        a.add(s)
    a.save()
    c = U.Atlas("cursors", out)
    for s in cursor_sprites():
        c.add(s)
    c.save()


# previews -----------------------------------------------------------------------------


def _world(W, H):
    """Plain backdrop for previews: a dim mossy ground, nothing more."""
    n = U.fbm(H, W, "preview.world", 40.0, 4)
    g = np.dstack((60 + n * 80, 72 + n * 90, 30 + n * 30, np.full((H, W), 255.0))).astype(np.uint8)
    return Image.fromarray(g, "RGBA")


def bar_preview(out_root, gap_u=0):
    """Composite at atlas density. gap_u is the filler width on each side of the centre group."""
    doc = Path(out_root) / "hud" / "hud.json"
    S = lambda n: Image.fromarray(U.sprite_pixels(doc, n), "RGBA")
    top = 80  # world height above the bar, u
    total = BAR_W + gap_u * 2
    px = lambda u: int(round(u * 4))
    im = _world(px(total), px(top + BAR_H))

    def put(name, x, y, flip=False):
        s = S(name)
        im.alpha_composite(s, (px(x), px(y + top))) if x >= 0 and y + top >= 0 else None

    rx = total - LEFT_W
    cx = LEFT_W + gap_u
    put("hud.base.left", 0, 0)
    put("hud.base.right", rx, 0)
    put("hud.base.centre", cx, 0)
    x = LEFT_W
    while gap_u and x < cx:
        for gx in (x, cx + CENTRE_W + (x - LEFT_W)):
            g = S("hud.gapfill")
            wd = min(64, cx - x)
            im.alpha_composite(g.crop((0, 0, px(wd), g.height)), (px(gx), px(top)))
        x += 64
    # left group
    put("hud.slab.normal", SLAB[0], SLAB[1])
    put("icon.scroll", SLAB[0] + 15, SLAB[1] + 3)
    put("hud.coordsplate", COORDS[0], COORDS[1])
    put("gauge.left.fill", GAUGE_FILL[0], GAUGE_FILL[1])
    put("gauge.cap.left", GAUGE_CAP[0], GAUGE_CAP[1])
    put("gem.hp.full", GEM[0], GEM[1])
    put("hud.talon.left", TALON[0], TALON[1])
    # right group
    off = total - BAR_W
    put("hud.slab.normal", BAR_W - SLAB[0] - SLAB[2] + off + gap_u * 0, SLAB[1])
    put("hud.slab.normal", BAR_W - COORDS[0] - COORDS[2] + off, COORDS[1])
    put("icon.party", BAR_W - SLAB[0] - SLAB[2] + off + 4, SLAB[1] + 3)
    put("hud.ministat.off", BAR_W - SLAB[0] - SLAB[2] + off + 34, SLAB[1] + 5)
    put("icon.pvp", BAR_W - SLAB[0] - SLAB[2] + off + 15, COORDS[1] + 2)
    put("gauge.right.fill", BAR_W - GAUGE_FILL[0] - GAUGE_FILL[2] + off, GAUGE_FILL[1])
    put("gauge.cap.right", BAR_W - GAUGE_CAP[0] - GAUGE_CAP[2] + off, GAUGE_CAP[1])
    put("gem.mp.full", BAR_W - GEM[0] - GEM[2] + off, GEM[1])
    put("hud.talon.right", BAR_W - TALON[0] - TALON[2] + off, TALON[1])
    # chat cluster, in the centre group
    c = cx - LEFT_W
    log_h, log_y = 66, -13 - 66 - 4
    chat_x = 186 + c
    for ty in range(0, log_h, 64):
        for tx in range(0, 256, 64):
            r = S("chat.rock")
            hh = min(64, log_h - ty)
            ww = min(64, 244 - tx)
            if ww > 0:
                im.alpha_composite(r.crop((0, 0, px(ww), px(hh))), (px(chat_x + 5 + tx), px(log_y + top + ty)))
    for ty in range(0, log_h, 44):
        p = S("chat.post.left")
        q = S("chat.post.right")
        hh = min(44, log_h - ty)
        im.alpha_composite(p.crop((0, 0, p.width, px(hh))), (px(chat_x), px(log_y + top + ty)))
        im.alpha_composite(q.crop((0, 0, q.width, px(hh))), (px(chat_x + 263), px(log_y + top + ty)))
    for ty in range(9, 57, 8):
        put("chat.scroll.track", chat_x + 249, log_y + ty)
    put("chat.scroll.up.normal", chat_x + 249, log_y)
    put("chat.scroll.thumb", chat_x + 249, log_y + 22)
    put("chat.scroll.down.normal", chat_x + 249, log_y + 57)
    for tx in range(0, 256, 64):
        r = S("chat.rail")
        ww = min(64, 258 - tx)
        im.alpha_composite(r.crop((0, 0, px(ww), r.height)), (px(chat_x + 5 + tx), px(log_y + log_h + top)))
    # input row: black and whisper fields stretched from their 3-slice sprites
    fy = -13 + 1
    for name, fx, fw in (("chat.field.main", chat_x + 9, 179), ("chat.field.whisper", chat_x + 188, 56)):
        f = S(name).resize((px(fw), px(15)))
        im.alpha_composite(f, (px(fx), px(fy + top)))
    put("chat.input.frame", chat_x + 8, -13)
    put("chat.bracket", chat_x + 9, -12)
    put("chat.btn.normal", chat_x + 245, -12)
    # EXP
    put("exp.trough", 221 + c, 5)
    fill = S("exp.fill")
    for x in range(0, 80, 8):
        im.alpha_composite(fill, (px(221 + c + x), px(5.5 + top)))
    for i in range(1, 10):
        put("exp.tick", 221 + c + i * 19.9, 5)
    put("exp.frame", 217 + c, 3)
    put("exp.cup", 420 + c, 3)
    # Q/W/E
    q0 = c
    put("qwe.slot.q.normal", 199 + q0, 18)
    put("qwe.slot.w.normal", 235 + q0, 18)
    put("qwe.slot.e.normal", 266 + q0, 18)
    for x in (216, 247, 279):
        put("qwe.notch", x + q0, 14)
    put("qwe.letter.q", 200 + q0, 37)
    put("qwe.letter.w", 233 + q0, 38)
    put("qwe.letter.e", 264 + q0, 38)
    # skill and icon buttons
    put("skill.bracket", 304 + c, 11)
    put("skill.tile.normal", 310.5 + c, 14.5)
    put("skill.icon.default", 311.5 + c, 15.5)
    put("iconbtn.plinth", 341 + c, 16.5)
    for i, x in enumerate((346, 377, 408)):
        put("iconbtn.frame.normal" if i < 2 else "iconbtn.frame.pressed", x + c, 18)
        put(("icon.banner", "icon.lens", "icon.satchel")[i], x + c + 3, 21)
    # dragons last: they draw above everything else in the bar
    put("hud.dragon.left", 0, -DRAGON_H)
    put("hud.dragon.right", total - DRAGON_W, -DRAGON_H)
    return im


def previews(out_root, out_dir):
    out_dir = Path(out_dir)
    out_dir.mkdir(parents=True, exist_ok=True)
    doc = Path(out_root) / "hud" / "hud.json"
    bar = bar_preview(out_root)
    bar.convert("RGB").save(out_dir / "hud_bar_640.png", compress_level=9)
    gap = (1920 / 2.25 - BAR_W) / 2
    wide = bar_preview(out_root, gap)
    f = 1920 / wide.width
    wide = wide.resize((1920, int(wide.height * f)), Image.LANCZOS)
    wide.convert("RGB").save(out_dir / "hud_bar_1920.png", compress_level=9)
    U.contact_sheet([doc, doc.parent / "cursors.json"], out_dir / "hud_sheet.png", cols=9)


if __name__ == "__main__":
    root = Path(sys.argv[1]) if len(sys.argv) > 1 else HERE.parents[1] / "dream" / "ui"
    main(root)
    previews(root, HERE / "previews")
