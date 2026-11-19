"""HUD art: bottom bar, gems, gauges, dragons, talons, chat chrome, cursors.

Writes dream/ui/hud/hud.{png,json} and cursors.{png,json}. All art is original and procedural.
Run directly to also refresh tools/ui-assets/previews/hud_*.png. Layout constants are bar units
(u), y down, 0 at the bar's top edge; dragons and chat sit at negative y.
"""
from __future__ import annotations

import dataclasses
import math
import os
import sys
from concurrent.futures import ProcessPoolExecutor
from pathlib import Path

import numpy as np
from PIL import Image

import forge
import gemtrace
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
TALON = (128, -3, 71, 52)
DRAGON = (0, -52, 118, 58)
SETTING = (92, -5, 62, 56)
SPILL = (59, -9, 128, 64)       # centred on the gem
DRAGON_W, DRAGON_H = DRAGON[2], DRAGON[3]

def _material(name, base, **changes):
    """A bar-only variant of a forge material (forge itself stays as the art core left it)."""
    forge.MATERIALS[name] = dataclasses.replace(forge.MATERIALS[base], **changes)
    forge.MATERIAL_NAMES.append(name)
    forge.MAT_ID[name] = len(forge.MATERIAL_NAMES) - 1


# gunmetal armour plates: bright sheen on the lit bevels, calm faces (the stock steel patina shows as a lattice)
_material("steel.armour", "steel", f0=(0.15, 0.145, 0.14), rough=0.47, vary=0.14, grit=0.45)

class BarRelief(forge.Relief):
    """forge.Relief with the roughness field smoothed: the stock plane-wave patina shows as a lattice on
    polished faces; here only the broad variation stays, so a plate's sheen is calm and its edges carry it."""

    def surface(self, seed):
        s = super().surface(seed)
        soft = forge.blur(s.rough, 2.4 * PPU, self.wrap)
        s.rough = (0.25 * s.rough + 0.75 * soft).astype(np.float32)
        return s


# The bar's own light: a warm key from the upper left, a low amber candle on the right, cold fill, and
# only faint reflection panels, so black iron shows its form in edges and broken sheen, not as cloudy chrome.
BAR_RIG = forge.Rig(
    lights=(
        forge.Light((-0.60, -0.60, 0.53), (1.00, 0.92, 0.80), 11.0, shadow=True),
        forge.Light((0.75, -0.25, 0.45), (1.00, 0.60, 0.28), 1.5),
        forge.Light((-0.15, 0.80, 0.30), (0.62, 0.74, 1.00), 0.03),
    ),
    panels=(
        forge.Panel(-0.18, -0.62, 0.55, 0.22, 0.26, (1.0, 0.93, 0.85)),
        forge.Panel(0.80, 0.05, 0.20, 0.50, 0.20, (1.0, 0.64, 0.34)),
    ),
    ambient=(0.017, 0.018, 0.022),
)


def _lit(r, seed, emit=None, rig=None, **kw):
    """Relief.render with the bar's rig and an optional emission map (HxWx3 linear, so an eye can burn).
    Returns float sRGB RGBA at hi-res; pass it to forge.downsample."""
    rig = rig or BAR_RIG
    prim = r.h
    h = prim + r._detail(seed, kw.get("detail", 1.0))
    m = forge.bake(h, prim, rig.lights[0].dir, r.wrap)
    s = forge.age(r.surface(seed), m, seed, kw.get("wear", 0.3), kw.get("grime", 0.5), kw.get("rust", 0.2), kw.get("dust", 0.1),
                  streak=r._drip_mask(), origin=(r.ox, r.oy), period=r.period, wrap=r.wrap)
    # inlay of bone and brass catches the candle: a little emission, so it carries the bar's bright mass
    inlay = (r.mid == forge.MAT_ID["rail"]).astype(np.float32)[..., None] * np.array((0.34, 0.25, 0.13), np.float32)
    inlay = inlay + (r.mid == forge.MAT_ID["brass"]).astype(np.float32)[..., None] * np.array((0.07, 0.045, 0.016), np.float32)
    inlay = forge.blur(inlay, 0.25 * PPU, r.wrap) * kw.get("inlay", 1.0)
    s.emit = inlay if emit is None else inlay + emit
    img = forge.finish(forge.light(s, m, rig), s.alpha, kw.get("exposure", 1.0), kw.get("value_map"))
    if kw.get("soft"):  # a hair of focus on the big sprites, so the finest bevel lines do not alias at 2.25 px/u
        img[..., :3] = forge.blur(img[..., :3], kw["soft"], r.wrap)
    return img


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


# gems ---------------------------------------------------------------------------------


GEM_U = 50
SETTING_U = (62, 56)
SPILL_U = (128, 64)


def desat(img, amount, dim):
    out = img.copy()
    l = (img[..., :3] * np.array([0.3, 0.59, 0.11], dtype=np.float32)).sum(axis=2, keepdims=True)
    out[..., :3] = (img[..., :3] * (1 - amount) + l * amount) * dim
    return out


GEM_PX = GEM_U * U.SCALE   # 200
GEM_R_U, GEM_C_U = 24.0, 24.9   # the stone's radius and centre in its 50 u sprite (alpha spans x 3..196, y 8..191 px)

# Per-stone grade. cap is the brightest channel the glass may reach (the engine's gem pass multiplies up from here),
# keep the share of chroma that survives, knee where the highlight compression starts. The traced stone peaks at 1.00
# with p99 0.99 and a ruby saturation near 1.0; what the grade leaves is measured in check_hud.
GEM_GRADE = {
    "hp": dict(cap=0.66, knee=0.28, keep=0.64, tint=(0.95, 0.58, 0.76), gain=1.9, core=(0.46, 0.50), glow=(0.24, 0.005, 0.032), hl=(0.36, 0.19, 0.18)),
    "mp": dict(cap=0.60, knee=0.26, keep=0.48, tint=(0.86, 0.95, 1.00), gain=1.8, core=(0.54, 0.50), glow=(0.065, 0.14, 0.38), hl=(0.24, 0.31, 0.42)),
}
# glow is the candlelight that burns through the liquid (an sRGB add at the lit core, brighter where the fluid is thick
# and along the thin lip of each clot); hl is the pale, wet, broken sheen on the upper-left glass. Neither may reach plastic:
# the highlight stays well under luma 0.80.


def _smoothstep(a, b, x):
    t = np.clip((x - a) / (b - a), 0, 1)
    return t * t * (3 - 2 * t)


def _noise_px(seed: str, scale_u: float, octaves: int = 3) -> np.ndarray:
    return U.fbm(GEM_PX, GEM_PX, seed, scale_u, octaves)


def _stretch(a: np.ndarray, ky: float) -> np.ndarray:
    """Vertical smear of a noise field: slow viscous runs rather than round blobs."""
    h, w = a.shape
    small = np.asarray(Image.fromarray(a.astype(np.float32), "F").resize((w, max(2, int(h * ky))), Image.BICUBIC), dtype=np.float32)
    return np.asarray(Image.fromarray(small, "F").resize((w, h), Image.BICUBIC), dtype=np.float32)


def _cracks(seed: str, kind: str) -> np.ndarray:
    """Hairline cracks in the glass as a 0..1 line mask at stone resolution: a few jagged runs from the rim inwards, one
    fork each at most. Drawn at 4x and averaged, so each is about a quarter of a unit wide."""
    from PIL import ImageDraw
    r = U.rng(f"{seed}.cracks.{kind}")
    S4 = 4
    im = Image.new("L", (GEM_PX * S4, GEM_PX * S4), 0)
    d = ImageDraw.Draw(im)
    cx = cy = GEM_PX * S4 / 2
    rad = GEM_R_U * U.SCALE * S4
    for k in range(4):
        ang = math.radians((200, 292, 24, 118)[k] + (r.random() - 0.5) * 30 + (0 if kind == "hp" else 14))
        x, y = cx + rad * 0.98 * math.cos(ang), cy + rad * 0.98 * math.sin(ang)
        heading = ang + math.pi
        pts = [(x, y)]
        for _ in range(int(10 + r.random() * 8)):
            heading += (r.random() - 0.5) * 1.15
            step = (1.9 + r.random() * 2.4) * U.SCALE * S4
            x, y = x + step * math.cos(heading), y + step * math.sin(heading)
            pts.append((x, y))
            if r.random() < 0.10:   # a fork that dies quickly
                bx, by, ba = x, y, heading + (0.9 if r.random() < 0.5 else -0.9)
                fork = [(bx, by)]
                for _ in range(3):
                    ba += (r.random() - 0.5) * 0.9
                    bx, by = bx + 1.6 * U.SCALE * S4 * math.cos(ba), by + 1.6 * U.SCALE * S4 * math.sin(ba)
                    fork.append((bx, by))
                d.line(fork, fill=170, width=int(S4 * 0.7))
        d.line(pts, fill=255, width=S4)
    a = np.asarray(im, dtype=np.float32) / 255
    return a.reshape(GEM_PX, S4, GEM_PX, S4).mean(axis=(1, 3))


def _swell(n: int, base: float, amp: float, phase: float, ends: float = 0.0) -> list:
    """Radii of a forged bar that was beaten thicker in places: a slow swell plus a taper at both ends."""
    out = []
    for k in range(n):
        t = k / (n - 1)
        out.append(max(0.3, (base + amp * math.sin(t * math.tau * 1.5 + phase)) * (1 - ends * (2 * abs(t - 0.5)) ** 3)))
    return out


def _cage_relief(kind: str) -> BarRelief:
    """A forged iron cage the orb sits inside: a girdle ring hugging the rim, a crown band and a lower band, two bowed
    ribs, all hammered to uneven widths, a boss rivet where they cross and on the girdle, and four heavy claws that bite
    over the glass. The second stone is the first mirrored, so its claws curl the other way."""
    r = BarRelief(GEM_U, GEM_U, "iron.black", 0.0)
    cx, cy, R = GEM_C_U + 0.1, GEM_C_U, GEM_R_U
    m = -1.0 if kind == "mp" else 1.0
    # the girdle: a heavy ring on the rim, so the cage reads as seated in the bar and not floating on the glass
    ring = [(cx + (R - 0.4) * math.cos(t), cy + (R - 0.4) * math.sin(t)) for t in np.linspace(0, math.tau, 96)]
    r.sweep(ring, _swell(96, 1.95, 0.55, 0.6), z=1.0, mat="iron.warm", flatten=0.90, step_px=2.0)
    bands = []
    for lat_deg, w, tag, ph in ((34, 1.95, "upper", 0.4), (-24, 1.75, "lower", 2.1)):
        lat = math.radians(lat_deg)
        y0, rx = cy - R * math.sin(lat), R * math.cos(lat) * 0.985
        pts = [(cx + rx * math.cos(t), y0 + rx * 0.30 * math.sin(t)) for t in np.linspace(math.radians(8), math.radians(172), 44)]
        r.sweep(pts, _swell(44, w, 0.55, ph, 0.25), z=1.3, mat="iron.warm", flatten=0.90, step_px=2.0)
        bands.append(pts)
    ribs = []
    for i, s in enumerate((-0.54, 0.54)):
        pts = [(cx + R * 0.97 * s * math.sin(t), cy - R * 0.97 * math.cos(t)) for t in np.linspace(math.radians(22), math.radians(158), 44)]
        r.sweep(pts, _swell(44, 1.75, 0.50, 1.3 + i, 0.30), z=1.35, mat="iron", flatten=0.90, step_px=2.0)
        ribs.append(pts)
    for i, rib in enumerate(ribs):
        for j, band in enumerate(bands):
            best = min(((ax - bx) ** 2 + (ay - by) ** 2, (ax + bx) / 2, (ay + by) / 2) for ax, ay in rib for bx, by in band)
            r.dome(best[1], best[2], 2.2, z=float(r.h[int(best[2] * PPU), int(best[1] * PPU)]) + 0.35, mat="iron.rivet", seed=f"gem.cage.{kind}.rv{i}{j}", collar=True)
    for i, tdeg in enumerate((-70, -20, 25, 70, 115, 160, 205, 250)):
        t = math.radians(tdeg)
        x, y = cx + (R - 0.4) * math.cos(t), cy + (R - 0.4) * math.sin(t)
        r.dome(x, y, 1.4, z=float(r.h[int(y * PPU), int(x * PPU)]) + 0.3, mat="iron.rivet", seed=f"gem.cage.{kind}.gr{i}", collar=True)
    for i, ang in enumerate((-132, -48, 138, 44)):
        a = math.radians(ang)
        ux, uy = math.cos(a), math.sin(a)
        base = (cx + (R + 0.8) * ux, cy + (R + 0.8) * uy)
        pts = [(base[0] - ux * t * 13.0 - math.sin(t * 2.4) * uy * 2.0 * m, base[1] - uy * t * 13.0 + math.sin(t * 2.4) * ux * 2.0 * m) for t in np.linspace(0, 1, 20)]
        r.sweep(pts, [3.5 - 3.35 * (k / 19) ** 0.75 for k in range(20)], z=1.5, mat="iron.warm", flatten=0.55, step_px=2.0)
    return r


def _cage_layer(kind: str) -> np.ndarray:
    r = _cage_relief(kind)
    img = _lit(r, f"gem.cage.{kind}", wear=0.14, grime=0.85, rust=0.42, dust=0.03, detail=0.8, exposure=0.78)
    return forge.downsample(img).astype(np.float32) / 255


def _grade_glass(kind: str, state: str, px: np.ndarray, cage: np.ndarray) -> np.ndarray:
    """Candy to blood: highlights compressed so no plastic flare survives, chroma pulled in, the liquid mottled with slow
    vertical runs and dark clots, soot settled along the rim and the cage, hairline cracks, and the cage over the lot."""
    g = GEM_GRADE[kind]
    a = px[..., 3].astype(np.float32) / 255
    rgb = px[..., :3].astype(np.float32) / 255
    y = (rgb @ np.array([0.2126, 0.7152, 0.0722], np.float32))[..., None]
    rgb = np.clip(y + (rgb - y) * g["keep"], 0, None) * np.array(g["tint"], np.float32) * g["gain"]
    v = rgb.max(axis=2, keepdims=True)
    span = g["cap"] - g["knee"]
    v2 = np.where(v > g["knee"], g["knee"] + span * (1 - np.exp(-(v - g["knee"]) / span)), v)
    rgb = rgb * (v2 / np.maximum(v, 1e-6))
    # the traced soft boxes are pale and desaturated: that is reflection, and a wet oily sheen reads as plastic, so it sinks into the liquid
    mx0, mn0 = rgb.max(axis=2, keepdims=True), rgb.min(axis=2, keepdims=True)
    pale = _smoothstep(0.10, 0.28, mn0 / np.maximum(mx0, 1e-6)) * _smoothstep(0.14, 0.30, mx0)
    rgb = rgb * (1 - 0.45 * pale)
    seed = f"gem.{kind}"
    run = _stretch(_noise_px(seed + ".run", 3.2), 0.28)
    clot = _noise_px(seed + ".clot", 1.7)
    clots = _smoothstep(0.60, 0.74, clot)
    if state == "empty":
        rgb = rgb * 0.30   # drained glass is soot-dark: the lit fluid is what shows the level (the C# gem test counts the pixels that differ)
    if state == "full":
        body = 1.0 + 0.52 * (run - 0.5) - 0.62 * clots * (0.5 + 0.5 * run)
        rgb = rgb * body[..., None]
        yy, xx = np.mgrid[0:GEM_PX, 0:GEM_PX].astype(np.float32)
        rpx = GEM_R_U * U.SCALE
        d2 = ((xx - GEM_PX * g["core"][0]) ** 2 + (yy - GEM_PX * g["core"][1]) ** 2) / (0.78 * rpx) ** 2
        core = np.exp(-d2) * (0.55 + 0.80 * run)
        lip = clots * (1 - _smoothstep(0.74, 0.80, clot))
        glow = np.array(g["glow"], np.float32)
        rgb = rgb + (core + 0.9 * lip * (0.3 + 0.7 * np.exp(-d2 / 3.0)))[..., None] * glow * a[..., None]
        ang = np.arctan2(yy - GEM_PX / 2, xx - GEM_PX / 2)
        rad = np.hypot(yy - GEM_PX / 2, xx - GEM_PX / 2) / rpx
        dang = np.abs(((ang - math.radians(-135 if kind == "hp" else -45) + math.pi) % math.tau) - math.pi)
        arc = _smoothstep(math.radians(46), math.radians(14), dang) * _smoothstep(0.50, 0.62, rad) * (1 - _smoothstep(0.80, 0.90, rad))
        broken = _smoothstep(0.50, 0.64, _noise_px(seed + ".wet", 1.1, octaves=2))
        rgb = rgb + (arc * broken * 0.85)[..., None] * np.array(g["hl"], np.float32)
        rgb = rgb * (1 - 0.34 * _smoothstep(0.55, 1.0, rad))[..., None]   # the outer glass falls away: the lit part is a core, not the whole stone
    rim = 1 - _smoothstep(0.50, 0.97, forge.blur(a, 4.5 * U.SCALE))
    pit = _noise_px(seed + ".soot", 0.9, octaves=2)
    smudge = _noise_px(seed + ".smudge", 4.0)
    soot = np.clip(0.80 * rim * (0.4 + 0.8 * smudge) + 0.5 * _smoothstep(0.66, 0.78, pit) * (0.35 + 0.65 * rim)
                   + 0.35 * _smoothstep(0.55, 0.85, forge.blur(cage[..., 3], 2.2 * U.SCALE)), 0, 0.85)
    rgb = rgb * (1 - soot[..., None] * 0.80) + soot[..., None] * np.array((0.012, 0.011, 0.010), np.float32)
    crack = _cracks(seed, kind)
    shifted = np.roll(crack, (-1, -1), axis=(0, 1))
    rgb = rgb * (1 - 0.88 * crack[..., None]) + np.array(g["tint"], np.float32) * (np.clip(shifted - crack, 0, 1) * 0.16)[..., None]
    sh = np.roll(forge.blur(cage[..., 3], 1.7 * U.SCALE), (int(2.2 * U.SCALE), int(1.6 * U.SCALE)), axis=(0, 1))   # the cast shadow lands on the fluid beneath
    rgb = rgb * (1 - 0.80 * sh[..., None])
    ca = cage[..., 3:4]
    rgb = rgb * (1 - ca) + cage[..., :3] * ca
    out8 = (np.clip(rgb, 0, 1) * 255 + 0.5).astype(np.uint8)
    out = np.dstack((out8, px[..., 3]))
    out[..., :3][px[..., 3] == 0] = 0
    return out


def gem_set(kind):
    """The four gem layers from the ray tracer, aged. All share the pivot at the stone's centre. The traced stone is a
    clean faceted ruby or sapphire; here it becomes blood or ink in soot-streaked glass held in a forged iron cage. The
    cage and the soot are baked into full and empty alike, so the fill level slides behind them."""
    layers = gemtrace.render_set(kind)
    cage = _cage_layer(kind)
    cage[..., 3] *= (layers["full"][..., 3] > 0)
    for k in ("full", "empty"):
        layers[k] = _grade_glass(kind, k, layers[k], cage)
    sp = layers["spec"].copy()
    keep = (1 - cage[..., 3:4]) * (0.45 + 0.30 * _noise_px(f"gem.{kind}.glint", 2.5)[..., None])   # broken sheens, not a plastic gleam
    sp[..., :3] = (sp[..., :3].astype(np.float32) * keep).astype(np.uint8)
    layers["spec"] = sp
    piv = (GEM_U * 2, GEM_U * 2)
    return [U.Sprite(f"gem.{kind}.{k}", layers[k], pivot=piv) for k in ("full", "empty", "mask", "spec")]


def gem_spill(kind, full_alpha):
    """Additive light the stone throws on the iron and the talons: a bright ring hugging the silhouette and
    a long soft falloff, nothing over the stone itself. Straight alpha; the runtime scales it with the fill."""
    w, h = SPILL_U[0] * U.SCALE, SPILL_U[1] * U.SCALE
    a = np.zeros((h, w), np.float32)
    g = full_alpha.astype(np.float32) / 255
    ox, oy = (w - g.shape[1]) // 2, (h - g.shape[0]) // 2
    a[oy : oy + g.shape[0], ox : ox + g.shape[1]] = g
    glow = np.zeros_like(a)
    for sigma_u, gain in ((1.6, 0.55), (4.5, 0.34), (11.0, 0.20), (24.0, 0.10)):
        glow += forge.blur(a, sigma_u * U.SCALE) * gain
    glow *= 1 - smooth(0.25, 0.9, forge.blur(a, 0.6 * U.SCALE))      # nothing over the stone
    yy, xx = np.mgrid[0:h, 0:w].astype(np.float32)
    edge = np.minimum(np.minimum(xx, w - 1 - xx), np.minimum(yy, h - 1 - yy)) / (8.0 * U.SCALE)
    glow *= smooth(0.0, 1.0, edge)                                  # and gone before the border
    col = np.array({"hp": (1.0, 0.16, 0.07), "mp": (0.12, 0.42, 1.0)}[kind], np.float32)
    alpha = np.clip(glow, 0, 1)
    out = np.dstack((np.broadcast_to(col, (h, w, 3)), alpha))
    return np.clip(out * 255 + 0.5, 0, 255).astype(np.uint8)


def setting():
    """Dark forged collar the stone sits in: a heavy rolled lip, a pocket of black, and six forged
    clasp bosses on the collar. The stone covers the pocket, the talons cover the right side."""
    w, h = SETTING_U
    r = BarRelief(w, h, "iron.black", 0.0)
    cx, cy = w / 2, h / 2 + 0.6
    r.plate(forge.Ellipse(cx, cy, 30.8, 27.6), z=0.0, bevel=3.6, profile="rolled", rise=0.55, mat="iron.warm", wobble=0.10, nick=0.02, wob_scale=9.0, seed="setting.outer")
    r.plate(forge.Ellipse(cx, cy, 27.9, 25.6), z=1.7, bevel=2.2, profile="rolled", rise=0.6, mat="iron", wobble=0.08, nick=0.02, wob_scale=9.0, seed="setting.lip")
    r.pocket(forge.Ellipse(cx, cy, 25.0, 24.6), depth=4.4, wall=1.3, mat="void", wobble=0.06, seed="setting.pocket")
    # a worn brass bead rolled round the outer lip, broken in four places
    ring = [(cx + 29.1 * math.cos(t), cy + 26.1 * math.sin(t)) for t in np.linspace(0, math.tau, 120)]
    for i, (a0, a1) in enumerate(((0.05, 0.19), (0.31, 0.44), (0.60, 0.70), (0.84, 0.95))):
        seg = ring[int(a0 * 120) : int(a1 * 120) + 1]
        r.plate(forge.Capsule(seg, 0.55), z=r.h[int(seg[0][1] * U.PPU), int(seg[0][0] * U.PPU)] + 0.1, bevel=0.4, profile="rolled", rise=0.9, mat="brass", wobble=0.04, nick=0.01, seed=f"setting.bead{i}")
    for i, t in enumerate(np.radians((-100, -45, 8, 62, 122, 190))):
        x, y = cx + 28.3 * math.cos(t), cy + 25.4 * math.sin(t)
        r.dome(x, y, 1.9, z=float(r.h[int(y * U.PPU), int(x * U.PPU)]), mat="iron.rivet", seed=f"setting.boss{i}")
    return forge.downsample(_lit(r, "hud.setting", wear=0.22, grime=0.55, rust=0.16, dust=0.06, value_map=None))


# forged bar plates ---------------------------------------------------------------------

R_ = forge.Rect
P_ = forge.Poly


def _vmap(w_u, h_u, ends, peak, top=0.86, floor=0.58, mottle=0.0, seed="", period=None):
    """Composition: dark at the group ends, open towards the focal point, a little darker at top and bottom,
    and broad mottling (5 to 18 u) like old oil and soot on the plates, so the bar has big soft shapes."""
    H, W = int(h_u * PPU), int(w_u * PPU)
    x = (np.arange(W, dtype=np.float32)[None, :] + 0.5) / PPU
    y = (np.arange(H, dtype=np.float32)[:, None] + 0.5) / PPU
    vx = floor + (1 - floor) * np.exp(-(((x - peak) / ends) ** 2))
    vy = top + (1 - top) * np.exp(-(((y - h_u * 0.5) / (h_u * 0.45)) ** 2))
    vm = vx * vy
    if mottle:
        n = forge.waves(x, y, seed + ".mottle", 12, 7.0, 26.0, (1.0, 1.0), (period, None))
        vm = vm * (1 + mottle * np.tanh(n * 0.9))
    return vm.astype(np.float32)


def _bolt(r, x, y, rad, seed):
    iy, ix = min(max(int(y * PPU), 0), r.H_px - 1), min(max(int(x * PPU), 0), r.W_px - 1)
    r.dome(x, y, rad, z=float(r.h[iy, ix]), mat="iron.rivet", seed=seed)


def _rail(r, x0, x1, y, w, seed, notch=True):
    """A worn brass-ivory inlay, set into a groove, ending in forged points. Never longer than 30 u."""
    zb = float(r.h[int(y * PPU), int(x0 * PPU) + 8])
    r.pocket(forge.Capsule([(x0, y), (x1, y)], w / 2 + 0.34), depth=0.5, wall=0.30, mat="void", wobble=0.05, seed=seed + ".g")
    e = w * 0.9
    shape = P_([(x0, y), (x0 + e, y - w / 2), (x1 - e, y - w / 2), (x1, y), (x1 - e, y + w / 2), (x0 + e, y + w / 2)])
    r.plate(shape, z=zb - 0.30, bevel=0.30, profile="chamfer", rise=1.0, mat="rail", wobble=0.06, nick=0.02, seed=seed)
    if notch:
        rg = U.rng(seed + ".n")
        xx = x0 + 2.5 + 3.0 * float(rg.random())
        while xx < x1 - 2.5:
            r.pocket(R_(xx, y - w * 0.6, 0.42, w * 1.2), depth=0.30, wall=0.20, seed=f"{seed}.n{xx:.1f}")
            xx += 4.0 + 4.5 * float(rg.random())


def _trim(r, rect, inset, radius, seed, width=0.62, seg=(6.0, 15.0), gap=(1.4, 4.2)):
    """A thin worn-brass trim following the outline of a lip, inset from its edge and broken into
    hand-laid pieces of different lengths, so a socket reads as gilt armour, not as a drawn border."""
    x, y, w, h = rect
    pts = forge._round_path(x + inset, y + inset, x + w - inset, y + h - inset, max(radius - inset, 0.6), 7)
    lens = [0.0]
    for (a, b), (c, d) in zip(pts[:-1], pts[1:]):
        lens.append(lens[-1] + math.hypot(c - a, d - b))
    total = lens[-1]
    rg = U.rng(seed + ".trim")
    pos = float(rg.random()) * gap[1]
    k = 0
    while pos < total - 2.0:
        end = min(pos + seg[0] + (seg[1] - seg[0]) * float(rg.random()), total)
        idx = [i for i, l in enumerate(lens) if pos <= l <= end]
        if len(idx) >= 2:
            piece = [pts[i] for i in idx]
            zs = [float(r.h[min(max(int(py * PPU), 0), r.H_px - 1), min(max(int(px * PPU), 0), r.W_px - 1)]) for px, py in piece]
            wd = width * (0.8 + 0.5 * float(rg.random()))
            r.plate(forge.Capsule(piece, wd / 2), z=float(np.median(zs)) - 0.12, bevel=0.32, profile="rolled", rise=1.0, mat="brass", wobble=0.04, nick=0.02, seed=f"{seed}.t{k}")
            k += 1
        pos = end + gap[0] + (gap[1] - gap[0]) * float(rg.random())


def bar_left():
    """Left group, 199 x 48 u: three plate stacks (menu, gauge, gem seat) and the first of the arm bands,
    each overlapping the last, on a nearly black field. Sprites sit in the recesses it leaves."""
    w, h = LEFT_W, BAR_H
    r = BarRelief(w, h, "iron.black", 1.0)
    r.plate(R_(0, 0, w, h), z=0.0, bevel=0.5, rise=0.0, seed="bl.field")
    # the arm bands carry on under the talon and out into the gap between the groups
    r.plate(P_([(146, 29), (210, 29), (210, 52), (146, 52)]), z=0.6, bevel=2.8, profile="rolled", rise=0.5, mat="iron.cold", wobble=0.14, nick=0.015, seed="bl.band.c")
    r.plate(P_([(146, -4), (210, -4), (210, 27), (146, 27)]), z=1.0, bevel=2.8, profile="rolled", rise=0.5, mat="iron.warm", wobble=0.14, nick=0.015, seed="bl.band.a")
    r.plate(P_([(146, 13), (210, 13), (210, 41), (146, 41)]), z=2.2, bevel=3.0, profile="rolled", rise=0.55, mat="iron", wobble=0.14, nick=0.015, seed="bl.band.b")
    # gem seat: a broad plate with a deep recess for the collar
    r.plate(R_(93, -3, 62, 54, 12), z=0.9, bevel=3.2, profile="rolled", rise=0.55, mat="iron.warm", wobble=0.16, nick=0.02, wob_scale=8.0, seed="bl.seat")
    r.pocket(forge.Ellipse(123.0, 23.6, 32.6, 29.6), depth=2.6, wall=1.4, mat="void", wobble=0.08, seed="bl.seat.pocket")
    # gauge pillar, standing proud of both neighbours
    r.plate(R_(62.5, -2.5, 38, 53, 4), z=1.9, bevel=2.6, profile="rolled", rise=0.5, mat="iron.cold", wobble=0.14, nick=0.02, seed="bl.pillar")
    r.pocket(R_(66.4, 2.2, 29.6, 43.8, 5), depth=3.2, wall=1.0, mat="void", wobble=0.06, seed="bl.pillar.pocket")
    # menu plate with two sockets for the slabs
    r.plate(R_(-8, -1.5, 72.5, 51.0, 4.5), z=0.8, bevel=2.8, profile="rolled", rise=0.5, mat="iron.warm", wobble=0.14, nick=0.02, seed="bl.menu")
    r.pocket(R_(3.4, 0.4, 59.2, 23.0, 1.4), depth=2.2, wall=0.9, mat="void", wobble=0.05, seed="bl.sock.a")
    r.pocket(R_(3.4, 23.9, 59.2, 22.8, 1.4), depth=2.2, wall=0.9, mat="void", wobble=0.05, seed="bl.sock.b")
    for i, (x, y, rad) in enumerate(((1.6, 3.0, 1.5), (1.6, 44.8, 1.5), (65.6, 23.6, 1.7), (97.0, 4.6, 1.7), (97.0, 42.6, 1.7), (183.0, 6.0, 1.9), (183.0, 45.0, 1.9))):
        _bolt(r, x, y, rad, f"bl.bolt{i}")
    _rail(r, 8.0, 36.0, 47.0, 1.5, "bl.rail.a")
    _rail(r, 102.0, 120.0, 46.2, 1.6, "bl.rail.b")
    return forge.downsample(_lit(r, "hud.bar.left", wear=0.26, grime=0.55, rust=0.12, dust=0.06, detail=0.5, soft=1.8, value_map=_vmap(w, h, 70, 124, mottle=0.55, seed="bl")))


# numerals and letters ------------------------------------------------------------------

NUM_U = (8, 12)


def _arc(cx, cy, rx, ry, a0, a1, n=16):
    return [(cx + rx * math.cos(math.radians(a0 + (a1 - a0) * k / n)), cy + ry * math.sin(math.radians(a0 + (a1 - a0) * k / n))) for k in range(n + 1)]


def _rot180(strokes, cx=4.0, cy=6.0):
    return [[(2 * cx - x, 2 * cy - y) for x, y in s] for s in strokes]


# Bold monoline skeletons on an 8 x 12 u cell (y down); the stroke is 2.1 u. Our own forms, a little flared.
_G6 = [_arc(4.0, 7.4, 2.45, 2.9, 0, 360, 22), [(1.55, 7.4), (1.9, 4.6), (3.0, 2.9), (5.7, 1.7)]]
_GLYPHS = {
    "0": [_arc(4.0, 6.0, 2.45, 4.1, 0, 360, 26)],
    "1": [[(2.4, 3.6), (4.7, 1.8), (4.7, 10.4)], [(2.3, 10.4), (7.0, 10.4)]],
    "2": [_arc(4.0, 4.5, 2.45, 2.7, -200, 20, 16) + [(1.8, 10.4), (6.8, 10.4)]],
    "3": [_arc(3.9, 3.9, 2.3, 2.2, -160, 100, 14), _arc(3.9, 8.1, 2.7, 2.4, -100, 160, 14)],
    "4": [[(5.3, 10.4), (5.3, 1.6), (1.3, 7.3), (7.0, 7.3)]],
    "5": [[(6.5, 1.8), (2.4, 1.8), (2.0, 5.3)] + _arc(3.9, 7.5, 2.7, 2.9, -125, 150, 16)],
    "6": _G6,
    "7": [[(1.4, 1.8), (6.7, 1.8), (3.0, 10.4)]],
    "8": [_arc(4.0, 3.9, 2.2, 2.2, 0, 360, 20), _arc(4.0, 8.1, 2.65, 2.5, 0, 360, 20)],
    "9": _rot180(_G6),
    "/": [[(6.5, 1.4), (1.7, 10.6)]],
}
_LETTERS = {
    "q": [_arc(3.5, 4.4, 2.5, 3.2, 0, 360, 22), [(4.0, 6.6), (6.4, 9.4)]],
    "w": [[(0.6, 1.0), (1.9, 9.0), (3.5, 3.4), (5.1, 9.0), (6.4, 1.0)]],
    "e": [[(5.9, 1.1), (1.3, 1.1), (1.3, 9.0), (5.9, 9.0)], [(1.3, 5.0), (4.9, 5.0)]],
}


def _glyph(strokes, w, h, seed, half=1.05, outline=0.85, mat="ivory", wear=0.20):
    """Bold embossed glyph: a dark rim under a lit ivory body. Straight RGBA at atlas density."""
    r = BarRelief(w, h, "iron.black", 0.0)
    k, cx, cy = 0.88, w / 2, h / 2          # keep the dark rim clear of the cell edge
    strokes = [[(cx + (x - 4.0) * k, cy + (y - 6.0) * k) for x, y in s_] for s_ in strokes]
    for i, s in enumerate(strokes):
        r.plate(forge.Capsule(s, half + outline), z=0.0, bevel=0.2, profile="rolled", rise=0.0, mat="void", mode="max", seed=f"{seed}.o{i}")
    for i, s in enumerate(strokes):
        r.plate(forge.Capsule(s, half), z=0.6, bevel=0.85, profile="rolled", rise=0.8, mat=mat, mode="max", seed=f"{seed}.b{i}")
    hi = _lit(r, seed, wear=wear, grime=0.2, rust=0.0, dust=0.0, detail=0.35, exposure=1.35)
    return forge.downsample(hi, sharpen=0.25)


def hudnum(ch):
    return _glyph(_GLYPHS[ch], NUM_U[0], NUM_U[1], f"hudnum.{ch}")


def qwe_letter(ch):
    return _glyph(_LETTERS[ch], 7, 10, f"qwe.letter.{ch}", half=0.85, outline=0.7, mat="brass")


# gauges -------------------------------------------------------------------------------


# dragon -------------------------------------------------------------------------------

DRAGON_U = (118, 58)

# The head is drawn level, then lifted about the nape and the jaw dropped about its hinge: alert and open.
HEAD_PIVOT, HEAD_ROT = (54.0, 30.0), -7.0
JAW_PIVOT, JAW_ROT = (60.0, 42.0), 10.0


def _turn(pts, pivot, deg):
    a = math.radians(deg)
    c, s = math.cos(a), math.sin(a)
    return [(pivot[0] + (x - pivot[0]) * c - (y - pivot[1]) * s, pivot[1] + (x - pivot[0]) * s + (y - pivot[1]) * c) for x, y in pts]


def H(pts):
    return _turn(pts, HEAD_PIVOT, HEAD_ROT)


def J(pts):
    return H(_turn(pts, JAW_PIVOT, JAW_ROT))


_EYE = H([(66.4, 21.2)])[0]
EYE_RECT_U = (_EYE[0], _EYE[1], 2.4, 1.2)    # flat amber left of the pupil, in sprite u
_FANG = J([(101.0, 47.6), (110.4, 30.0)])
FANG_RECT_U = (min(p[0] for p in _FANG) - 2, min(p[1] for p in _FANG), 12, abs(_FANG[0][1] - _FANG[1][1]))


def _horn(r, pts, rad, z, seed, tip="ivory", tip_from=0.60, mat="iron.warm"):
    path = U.bezier(*pts, 64)
    n = len(path)
    rr = [rad * (1 - k / (n - 1)) ** 0.72 * (1 + 0.07 * math.sin(k * 0.62) * (1 - k / (n - 1))) + 0.05 for k in range(n)]
    r.sweep(path, rr, z, mat=mat, step_px=2.0)
    t0 = int(n * tip_from)
    r.sweep(path[t0:], rr[t0:], z + 0.03, mat=tip, step_px=2.0)


def _spike(r, bx, by, lean, height, wid, z, seed, mat="iron"):
    pts = [(bx - wid, by + 2.5), (bx + lean, by - height), (bx + wid * 0.7, by + 2.5)]
    r.plate(forge.Poly(pts), z=z, bevel=1.6, profile="rolled", rise=0.55, mat=mat, wobble=0.10, nick=0.02, seed=seed)


def _tooth(r, x, y, length, wid, z, seed, tf, up=False):
    s = -1 if up else 1
    pts = tf([(x - wid, y), (x + wid, y), (x + wid * 0.2, y + s * length)])
    r.plate(forge.Poly(pts), z=z, bevel=0.55, profile="rolled", rise=0.9, mat="ivory", wobble=0.04, nick=0.01, seed=seed)


def dragon_relief():
    w, h = DRAGON_U
    r = BarRelief(w, h, "iron.black", 0.0)
    P = forge.Poly
    kw = dict(profile="rolled", wobble=0.14, nick=0.02, wob_scale=7.0)
    # two horns first, swept back off the skull, so everything else overlaps their roots
    _horn(r, [(56, 14), (43, 1.5), (23, -1.0), (5, 9)], 4.2, 1.2, "dragon.horn1")
    _horn(r, [(52, 21), (38, 14), (20, 14.5), (5, 25)], 3.0, 0.6, "dragon.horn2")
    # the neck: one heavy mass, then five great plates down it, tail to head
    r.plate(P([(-5, 26), (10, 25), (26, 22), (42, 17), (57, 18), (63, 31), (59, 45), (46, 54), (30, 61), (-5, 61)]), z=0.4, bevel=3.2, rise=0.5, mat="iron.black", seed="dragon.neck", **kw)
    for i, (cx, cy, ang, L, Hh) in enumerate(((7, 42, -8, 19, 13), (18, 38, -14, 19, 13.5), (29, 34, -22, 19.5, 14), (39, 30, -34, 18, 14), (46, 29, -46, 15, 13))):
        a = math.radians(ang)
        c, s = math.cos(a), math.sin(a)
        local = [(-L / 2, -Hh * 0.30), (-L * 0.1, -Hh / 2), (L * 0.46, -Hh * 0.40), (L / 2, Hh * 0.08), (L * 0.30, Hh / 2), (-L * 0.42, Hh * 0.46)]
        pts = [(cx + x * c - y * s, cy + x * s + y * c) for x, y in local]
        r.plate(P(pts), z=1.2 + i * 0.55, bevel=2.4, rise=0.72, mat=("iron.warm", "steel.armour", "iron.cold", "steel.armour", "iron")[i], seed=f"dragon.nplate{i}", **kw)
    # dorsal crest: five swept-back spikes along the top of the neck
    for i, (bx, by, lean, ht, wd) in enumerate(((8, 26.5, -7, 11, 4.0), (18, 24.4, -8, 12, 4.4), (28, 22.2, -8, 12.5, 4.6), (38, 19.4, -8, 12, 4.6), (47, 17.4, -7, 10, 4.2))):
        _spike(r, bx, by, lean, ht, wd, 2.6 + i * 0.3, f"dragon.spike{i}", mat=("iron.warm", "iron", "iron.cold", "iron.warm", "iron")[i])
    # cheek spikes raked back from the hinge, over the neck plates
    for i, (p0, p1, p2, p3, rad) in enumerate((((58, 33), (52, 30), (46, 31), (40, 26), 1.9), ((58, 38), (51, 38), (45, 41), (38, 41), 1.7), ((57, 43), (52, 45), (46, 49), (41, 52), 1.5))):
        path = U.bezier(*H([p0, p1, p2, p3]), 36)
        r.sweep(path, [rad * (1 - k / 36) ** 0.7 + 0.04 for k in range(37)], 5.8 - i * 0.2, mat="iron", step_px=2.0)
    # lower jaw, with a heavy chin spike, then the hinge plate
    r.plate(P(J([(58, 40), (78, 41.5), (100, 43.5), (111, 47), (105, 51.5), (86, 53), (66, 52), (57, 48)])), z=3.0, bevel=2.4, rise=0.72, mat="steel.armour", seed="dragon.jaw", **kw)
    r.plate(P(J([(90, 50), (97, 59), (104, 49.5)])), z=3.4, bevel=1.6, rise=0.6, mat="iron", seed="dragon.chin", **kw)
    # the dark of the open mouth, between the upper lip and the dropped jaw
    mouth = H([(62, 35.8), (86, 37.6), (108, 38.4)]) + list(reversed(J([(64, 41.6), (86, 42.4), (108, 43.4)])))
    r.plate(P(mouth), z=1.0, bevel=0.3, rise=0.1, mat="void", seed="dragon.mouth", profile="rolled", wobble=0.05)
    # skull, brow and snout: plates stepping up and forward
    r.plate(P(H([(46, 17), (58, 12), (72, 11.5), (86, 16), (95, 22.5), (93, 28), (78, 32), (60, 36), (47, 30)])), z=3.8, bevel=3.0, rise=0.72, mat="steel.armour", seed="dragon.skull", **kw)
    r.plate(P(H([(80, 21), (100, 24), (114, 31), (117, 34.5), (110, 38.2), (92, 38.4), (80, 35)])), z=4.6, bevel=2.8, rise=0.72, mat="iron", seed="dragon.snout", **kw)
    r.plate(P(H([(55, 12.5), (66, 9.8), (80, 12.5), (91, 19.5), (87, 24), (76, 20.5), (64, 18.8), (56, 19.8)])), z=6.2, bevel=2.2, rise=0.6, mat="steel.armour", seed="dragon.brow", **kw)
    # cheek plate
    r.plate(P(H([(50, 28), (66, 27.5), (74, 33), (70, 41), (56, 46), (48, 40)])), z=5.0, bevel=2.6, rise=0.72, mat="iron.warm", seed="dragon.cheek", **kw)
    # brow horns, swept back over the eye
    _horn(r, H([(72, 11.5), (66, 5), (58, 1.5), (49, 3.5)]), 2.4, 6.6, "dragon.brow.horn", tip_from=0.5, mat="iron")
    # eye socket, then nostril
    r.pocket(P(H([(64.5, 20.6), (73.5, 22.8), (76.5, 26.6), (69.5, 25.8), (64, 23.5)])), depth=2.0, wall=0.7, mat="void", wobble=0.04, seed="dragon.eye")
    nose = H([(112.0, 32.6)])[0]
    r.pocket(forge.Ellipse(nose[0], nose[1], 2.0, 1.1), depth=1.2, wall=0.5, mat="void", seed="dragon.nostril")
    # teeth: a row hanging from the snout, a shorter row rising from the jaw, and the long fang outside the lip
    rg = U.rng("dragon.teeth")
    for i, x in enumerate(np.arange(79, 110, 6.4)):
        _tooth(r, float(x), 37.2 + (x - 78) * 0.03, 5.4 + 2.4 * float(rg.random()), 1.6, 5.0, f"dragon.tu{i}", H)
    for i, x in enumerate(np.arange(80, 104, 8.0)):
        _tooth(r, float(x), 43.0 + (x - 82) * 0.03, 3.8 + 1.6 * float(rg.random()), 1.5, 3.6, f"dragon.tl{i}", J, up=True)
    fang = U.bezier(*J([(101.0, 47.6), (103.0, 42.0), (105.4, 35.5), (110.4, 30.0)]), 40)
    r.sweep(fang, [2.0 * (1 - k / 40) ** 0.65 + 0.04 for k in range(41)], 5.6, mat="ivory", step_px=2.0)
    # bulk: the whole head and neck swell a little, so the light has something round to find
    r.h = r.h + forge.blur(r.a, 4.0 * PPU) * 7.0
    return r


def dragon():
    r = dragon_relief()
    w, h = DRAGON_U
    ex, ey, ew, eh = EYE_RECT_U
    yy, xx = np.mgrid[0 : r.H_px, 0 : r.W_px].astype(np.float32)
    xu, yu = (xx + 0.5) / PPU, (yy + 0.5) / PPU
    # eye: a slanted amber almond with a black slit, burning
    top = np.array(U.bezier(*H([(64.4, 21.4), (68.0, 20.6), (71.6, 21.6), (74.6, 24.6)]), 24))
    bot = np.array(U.bezier(*H([(74.6, 24.6), (70.0, 25.0), (66.5, 24.2), (64.4, 21.4)]), 24))
    almond = U.poly_mask(w, h, [tuple(p) for p in np.vstack((top, bot))])
    sc = H([(71.2, 23.2)])[0]
    slit = U.ellipse_mask(w, h, sc[0], sc[1], 0.42, 1.6, 5)
    fall = np.clip((xu - ex) / 7.0, 0, 1)
    amber = np.array((1.0, 0.30, 0.04), np.float32) * (1.2 - 0.6 * fall[..., None])
    emit = amber * (almond * (1 - slit))[..., None] * 0.9
    vm = ((0.38 + 0.62 * smooth(56, 34, yu)) * (0.55 + 0.45 * smooth(0, 26, xu))).astype(np.float32)
    img = _lit(r, "hud.dragon", emit=emit.astype(np.float32), wear=0.14, grime=0.5, rust=0.1, dust=0.05, detail=0.10, exposure=1.9, value_map=vm)
    # the socket stays black round the eye
    a = r.a
    img[..., 3] = a
    img[..., :3] = forge.blur(img[..., :3], 3.5)
    return forge.downsample(img, sharpen=0.0)


# talons -------------------------------------------------------------------------------

TALON_U = (71, 52)


def _claw(r, pts, rad0, z0, seed, band_at=0.20, band=True):
    """One talon: a dark forged horn tapering to a worn steel point, with a brass collar near the knuckle."""
    path = U.bezier(*pts, 56)
    n = len(path)
    rr = [rad0 * (1 - k / (n - 1)) ** 0.78 + 0.05 for k in range(n)]
    split = int(n * 0.50)
    r.sweep(path, rr, z0, mat="iron.black", flatten=1.0, step_px=2.0)
    r.sweep(path[split:], rr[split:], z0 + 0.04, mat="steel", flatten=1.0, step_px=2.0)    # the point is worn to bare steel
    r.sweep(path[: split + 4], [v * 0.97 for v in rr[: split + 4]], z0 + 0.02, mat="iron", flatten=1.0, step_px=2.0)
    if band:
        k = int(n * band_at)
        (x, y), (x2, y2) = path[k], path[k + 1]
        d = math.hypot(x2 - x, y2 - y) or 1.0
        nx, ny = -(y2 - y) / d, (x2 - x) / d
        e = rr[k]
        gr = 1.05
        r.sweep([(x - nx * e, y - ny * e), (x + nx * e, y + ny * e)], gr, z0 + e - gr + 0.5, mat="brass", flatten=1.0, step_px=2.0)
    return path


def talon():
    """Left version. A forearm of three heavy overlapping plates, and three horns that curve onto the stone."""
    w, h = TALON_U
    r = BarRelief(w, h, "iron.black", 0.0)
    P = forge.Poly
    # forearm: three big overlapping plates, so mass overlaps mass
    r.plate(P([(72, 29), (46, 33), (36, 36), (31, 43), (35, 50.5), (50, 52.5), (72, 52.5)]), z=0.6, bevel=2.8, profile="rolled", rise=0.5, mat="iron.cold", wobble=0.16, nick=0.02, seed="talon.low")
    r.plate(P([(72, -1), (48, -0.8), (38, 2.5), (31, 8), (31, 15), (37, 21), (48, 25), (72, 27)]), z=1.0, bevel=2.8, profile="rolled", rise=0.5, mat="iron.warm", wobble=0.16, nick=0.02, seed="talon.up")
    r.plate(P([(72, 13), (50, 15), (38, 19), (31, 25), (31, 32), (37, 38), (50, 40), (72, 41)]), z=2.2, bevel=3.0, profile="rolled", rise=0.55, mat="iron", wobble=0.16, nick=0.02, seed="talon.mid")
    for i, (x, y, rad) in enumerate(((60.5, 5.0, 2.0), (60.5, 22.0, 2.1), (60.5, 46.0, 2.0), (48.5, 33.5, 1.7))):
        r.dome(x, y, rad, z=float(r.h[int(y * PPU), int(x * PPU)]), mat="iron.rivet", seed=f"talon.rivet{i}")
    # knuckles where the horns root
    for i, (x, y, rad) in enumerate(((35.0, 9.0, 5.2), (34.0, 26.0, 5.6), (35.5, 43.0, 5.2))):
        r.dome(x, y, rad, z=float(r.h[int(y * PPU), int(x * PPU)]) + 0.2, flat=0.9, mat="iron", seed=f"talon.knuckle{i}", collar=False)
    # the three horns: over the top of the stone, straight at its edge, under the bottom
    _claw(r, [(36, 9), (24, 0.6), (13, 3.5), (7.2, 14.5)], 4.8, 4.6, "talon.c1")
    _claw(r, [(35, 26), (24, 24.4), (13, 25.0), (4.8, 27.0)], 5.2, 5.0, "talon.c2")
    _claw(r, [(36, 43), (24, 51.0), (13, 48.0), (7.6, 38.0)], 4.8, 4.6, "talon.c3")
    hi = _lit(r, "hud.talon", wear=0.24, grime=0.55, rust=0.14, dust=0.06, detail=0.8, soft=1.4)
    # a soft contact shadow round the horns and the arm, so they sit on the stone rather than float over it
    a = hi[..., 3]
    sh = np.clip(forge.blur(a, 1.7 * PPU) * 1.5, 0, 1) * 0.55
    out = hi.copy()
    out[..., 3] = np.maximum(a, sh)
    a_safe = np.maximum(out[..., 3], 1e-6)
    out[..., :3] = np.where((a > 0.01)[..., None], hi[..., :3] * (a / a_safe)[..., None], 0.0)
    return forge.downsample(out)


# stone slabs and buttons --------------------------------------------------------------


def stone_rgb(H, W, base, seed, amp=0.16):
    n = U.fbm(H, W, seed + ".c", 6.0, 4)
    g = U.grain(H, W, seed + ".g", 0.05)
    return (rgb(base)[None, None, :] * (1 + (n - 0.5) * amp * 2 + g)[..., None]).astype(np.float32)


def flat(mask, colour):
    return solid(colour, mask)


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


# Q/W/E --------------------------------------------------------------------------------


# skill tile ---------------------------------------------------------------------------


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


# gauges -------------------------------------------------------------------------------


def _vial(seed, liquid, glow, edge_dark=0.80, stripe=0.9, fill=True):
    """A glass tube seen end on its side: dark rolled edges, a sharp reflection right of centre, a soft
    one on the left, liquid that glows from inside, a dark seat under the cap. 15 x 35 u at atlas density."""
    w_u, h_u = GAUGE_FILL[2], GAUGE_FILL[3]
    W, H = int(w_u * U.SCALE), int(h_u * U.SCALE)
    x = ((np.arange(W, dtype=np.float32) + 0.5) / W) * 2 - 1
    y = ((np.arange(H, dtype=np.float32) + 0.5) / H)[:, None]
    nz = np.sqrt(np.clip(1 - x * x, 0, 1))
    fres = (1 - nz) ** 1.6
    lin = lambda c: np.array(U.srgb_to_linear(np.array(c, np.float32)), np.float32)
    body = lin(liquid)[None, None, :] * (0.22 + 0.78 * np.clip(0.55 + 0.45 * x * -0.5 + 0.5 * nz, 0, 1))[None, :, None] if fill else np.zeros((1, W, 3), np.float32) + 0.006
    core = lin(glow)[None, None, :] * (nz ** 3 * (0.30 + 0.70 * (1 - y) ** 0.6))[..., None] if fill else 0.0
    sharp = np.exp(-(((x - 0.30) / 0.075) ** 2)) * stripe
    soft = np.exp(-(((x + 0.46) / 0.20) ** 2)) * 0.22
    spec = ((sharp + soft) * (0.55 + 0.45 * np.clip((1 - np.abs(y - 0.45) * 1.2), 0, 1)))[..., None] * np.array((1.0, 0.95, 0.86), np.float32)
    if not fill:
        spec = spec * 0.20
    img = (body + core) * (1 - edge_dark * fres)[None, :, None] + spec * (0.10 if not fill else 0.55)
    # bubbles and a dark seat under the cap
    rg = U.rng(seed)
    for _ in range(7):
        bx, by = int(rg.integers(5, W - 5)), int(rg.integers(8, H - 6))
        yy, xx = np.mgrid[0:H, 0:W]
        img = img + (np.exp(-(((xx - bx) ** 2 + (yy - by) ** 2) / 2.2)))[..., None] * (0.25 if fill else 0.012)
    seat = (1 - _sm(0.0, 0.10, y)) * 0.8
    img = img * (1 - seat[..., None])
    out = np.dstack((U.linear_to_srgb(np.clip(img, 0, 1)), np.ones((H, W), np.float32)))
    return np.clip(out * 255 + 0.5, 0, 255).astype(np.uint8)


def _sm(a, b, x):
    t = np.clip((x - a) / (b - a), 0, 1)
    return t * t * (3 - 2 * t)


def gauge_fill_left():
    return _vial("gauge.left", (0.95, 0.46, 0.10), (1.0, 0.62, 0.14), stripe=0.85)


def gauge_fill_right():
    return _vial("gauge.right", (0.42, 0.16, 0.62), (0.70, 0.32, 0.95), stripe=0.80)


def gauge_empty():
    return _vial("gauge.empty", (0.02, 0.02, 0.02), (0.0, 0.0, 0.0), edge_dark=0.5, stripe=0.35, fill=False)


def gauge_cap():
    """Forged clamp for the left vial; the hole over the glass stays open. Mirrored for the right."""
    w, h = GAUGE_CAP[2], GAUGE_CAP[3]
    r = BarRelief(w, h, "iron.black", 0.0)
    kw = dict(profile="rolled", wobble=0.12, nick=0.02, wob_scale=5.0)
    # side posts, then the heavy blocks over and under the glass, each overlapping the posts
    r.plate(R_(1.2, 2.0, 6.8, 42.0, 3.0), z=0.5, bevel=2.6, rise=0.5, mat="iron.cold", seed="cap.pl", **kw)
    r.plate(R_(25.0, 2.0, 6.8, 42.0, 3.0), z=0.5, bevel=2.6, rise=0.5, mat="iron.cold", seed="cap.pr", **kw)
    r.plate(P_([(0.6, 0.0), (32.4, 0.0), (32.4, 4.4), (28, 5.6), (5, 5.6), (0.6, 4.4)]), z=1.4, bevel=2.6, rise=0.55, mat="iron.warm", seed="cap.top", **kw)
    r.plate(P_([(0.6, 46.0), (32.4, 46.0), (32.4, 41.4), (28, 40.2), (5, 40.2), (0.6, 41.4)]), z=1.4, bevel=2.6, rise=0.55, mat="iron.warm", seed="cap.bot", **kw)
    for i, (x, y) in enumerate(((4.6, 2.4), (28.4, 2.4), (4.6, 43.6), (28.4, 43.6))):
        r.dome(x, y, 1.3, z=float(r.h[int(y * PPU), int(x * PPU)]), mat="iron.rivet", seed=f"cap.rv{i}", collar=False)
    for i, y in enumerate((8.0, 36.5)):
        for x0 in (1.0, 25.4):
            zb = float(r.h[int(y * PPU), int((x0 + 3.4) * PPU)])
            r.plate(R_(x0, y, 6.6, 1.6, 0.5), z=zb - 0.1, bevel=0.5, profile="rolled", rise=0.9, mat="brass", wobble=0.04, seed=f"cap.band{i}{x0}")
    return forge.downsample(_lit(r, "gauge.cap", wear=0.25, grime=0.5, rust=0.1, dust=0.04))


# slabs and plates ---------------------------------------------------------------------


def slab(state):
    """Menu slab, 56 x 22: a heavy forged plate with a raised face; hover is candle-lit, pressed is pushed in."""
    w, h = 56, 22
    st = _STATE[state]
    r = BarRelief(w, h, "iron.black", 0.0)
    r.plate(R_(0.2, 0.2, w - 0.4, h - 0.4, 2.2), z=0.4, bevel=2.8, profile="rolled", rise=0.5, mat="iron.warm", wobble=0.16, nick=0.03, wob_scale=6.0, seed="slab.lip")
    _trim(r, (0.2, 0.2, w - 0.4, h - 0.4), 1.4, 2.2, "slab.lip")
    face = R_(4.2 + st["shift"], 3.8 + st["shift"], w - 8.4, h - 7.8, 1.6)
    r.plate(face, z=0.9 - st["shift"] * 1.4, bevel=1.5, profile="rolled", rise=0.55, mat="iron.cold", wobble=0.14, nick=0.03, seed="slab.face")
    for i, (x, y) in enumerate(((2.4, 2.4), (53.6, 2.4), (2.4, 19.6), (53.6, 19.6))):
        r.dome(x, y, 1.15, z=float(r.h[int(y * PPU), int(x * PPU)]), mat="iron.rivet", seed=f"slab.rv{i}", collar=False)
    # an inlay on the face: a brass lozenge on a worn line, set into the iron, off its centre like hand work
    zf = float(r.h[int(11.0 * PPU), int(10.0 * PPU)])
    cx0 = 28.5 + st["shift"]
    cy0 = 10.8 + st["shift"]
    r.plate(P_([(cx0 - 5.4, cy0), (cx0, cy0 - 3.4), (cx0 + 5.0, cy0), (cx0, cy0 + 3.4)]), z=zf - 0.3, bevel=0.55, profile="chamfer", mat="brass", wobble=0.05, nick=0.02, seed="slab.loz")
    r.pocket(P_([(cx0 - 2.4, cy0), (cx0, cy0 - 1.4), (cx0 + 2.2, cy0), (cx0, cy0 + 1.4)]), depth=0.5, wall=0.4, mat="void", seed="slab.loz.eye")
    _rail(r, 9.0 + st["shift"], cx0 - 8.0, cy0, 0.9, "slab.rail.a", notch=False)
    _rail(r, cx0 + 8.0, 47.0 + st["shift"], cy0, 0.9, "slab.rail.b", notch=False)
    emit = _glow(r, face, 0.5, st["glow"]) if st["glow"] else None
    return forge.downsample(_lit(r, "hud.slab", emit=emit, wear=0.28, grime=0.55, rust=0.1, dust=0.05, exposure=st["exposure"]))


def coords_plate():
    """Sunken plate for the coordinates: a rolled frame round a black well; the text sits in the well."""
    w, h = 56, 22
    r = BarRelief(w, h, "iron.black", 0.0)
    r.plate(R_(0.2, 0.2, w - 0.4, h - 0.4, 2.2), z=0.4, bevel=2.8, profile="rolled", rise=0.5, mat="iron.warm", wobble=0.16, nick=0.03, wob_scale=6.0, seed="coords.lip")
    _trim(r, (0.2, 0.2, w - 0.4, h - 0.4), 1.4, 2.2, "coords.lip")
    r.pocket(R_(3.6, 3.4, w - 7.2, h - 6.8, 1.4), depth=3.0, wall=1.1, mat="void", wobble=0.08, nick=0.02, seed="coords.well")
    for i, (x, y) in enumerate(((2.0, 2.0), (54.0, 2.0), (2.0, 20.0), (54.0, 20.0))):
        r.dome(x, y, 1.0, z=float(r.h[int(y * PPU), int(x * PPU)]), mat="iron.rivet", seed=f"coords.rv{i}", collar=False)
    return forge.downsample(_lit(r, "hud.coords", wear=0.25, grime=0.55, rust=0.1, dust=0.05))


def skill_bracket():
    """The frame round the skill tile: two posts, a top and a bottom bar, brass pins."""
    w, h = 32, 36
    r = BarRelief(w, h, "iron.black", 0.0)
    kw = dict(profile="rolled", wobble=0.12, nick=0.02, wob_scale=5.0)
    r.plate(R_(0.4, 0.8, 5.6, 34.6, 2.6), z=0.5, bevel=2.4, rise=0.55, mat="iron.cold", seed="sb.pl", **kw)
    r.plate(R_(26.0, 0.8, 5.6, 34.6, 2.6), z=0.5, bevel=2.4, rise=0.55, mat="iron.cold", seed="sb.pr", **kw)
    r.plate(R_(0.2, 0.0, 31.6, 3.6, 1.6), z=1.3, bevel=1.9, rise=0.55, mat="iron.warm", seed="sb.top", **kw)
    r.plate(R_(0.2, 32.4, 31.6, 3.6, 1.6), z=1.3, bevel=1.9, rise=0.55, mat="iron.warm", seed="sb.bot", **kw)
    for i, (x, y) in enumerate(((3.2, 2.0), (28.8, 2.0), (3.2, 34.0), (28.8, 34.0))):
        r.dome(x, y, 1.0, z=float(r.h[int(y * PPU), int(x * PPU)]), mat="brass", seed=f"sb.pin{i}", collar=False)
    for i, y in enumerate((11.0, 24.0)):
        for x0 in (0.7, 26.3):
            zb = float(r.h[int(y * PPU), int((x0 + 2.5) * PPU)])
            r.plate(R_(x0, y, 5.0, 1.2, 0.4), z=zb - 0.1, bevel=0.4, profile="rolled", rise=0.9, mat="brass", wobble=0.03, seed=f"sb.band{i}{x0}")
    return forge.downsample(_lit(r, "skill.bracket", wear=0.25, grime=0.5, rust=0.1, dust=0.04))


def skill_tile(state):
    w, h = 19, 28
    r = BarRelief(w, h, "iron.black", 1.0)
    r.plate(R_(0, 0, w, h, 0.6), z=0.4, bevel=1.4, profile="rolled", rise=0.5, mat="iron.cold", wobble=0.08, nick=0.02, seed="tile.face")
    face = R_(2.2, 2.2, w - 4.4, h - 4.4, 0.8)
    r.pocket(face, depth=0.7, wall=0.6, mat="iron.black", seed="tile.inset")
    emit = _glow(r, R_(0.6, 0.6, w - 1.2, h - 1.2, 0.6), 0.7, 0.45) if state == "h" else None
    img = forge.downsample(_lit(r, "skill.tile", emit=emit, wear=0.2, grime=0.45, rust=0.05, dust=0.04, exposure=1.0 if state != "d" else 0.55))
    return img


def iconbtn_plinth():
    w, h = 101, 30
    r = BarRelief(w, h, "iron.black", 0.0)
    r.plate(R_(0, 0.5, w, h - 1.0, 3.0), z=0.5, bevel=2.6, profile="rolled", rise=0.5, mat="iron.warm", wobble=0.14, nick=0.025, seed="plinth.lip")
    for i, x in enumerate((4.2, 35.2, 66.2)):
        r.pocket(R_(x, 0.8, 30.0, 28.4, 2.4), depth=2.4, wall=0.9, mat="void", wobble=0.06, seed=f"plinth.well{i}")
    return forge.downsample(_lit(r, "iconbtn.plinth", wear=0.25, grime=0.55, rust=0.1, dust=0.05))


def iconbtn_frame(state):
    """Square forged frame for a button, 28 u: rolled lip, four bolts, a black face for the icon."""
    w = h = 28
    st = _STATE[state]
    r = BarRelief(w, h, "iron.black", 0.0)
    r.plate(R_(0.3, 0.3, w - 0.6, h - 0.6, 2.4), z=0.4, bevel=3.0, profile="rolled", rise=0.55, mat="iron.warm", wobble=0.16, nick=0.03, wob_scale=5.0, seed="ibf.lip")
    _trim(r, (0.3, 0.3, w - 0.6, h - 0.6), 1.5, 2.4, "ibf.lip")
    face = R_(3.6 + st["shift"], 3.6 + st["shift"], w - 7.2 - st["shift"], h - 7.2 - st["shift"], 1.4)
    r.pocket(face, depth=3.2 - st["shift"] * 1.4, wall=1.0, mat="void", wobble=0.07, seed="ibf.face")
    for i, (x, y) in enumerate(((2.0, 2.0), (26.0, 2.0), (2.0, 26.0), (26.0, 26.0))):
        r.dome(x, y, 1.0, z=float(r.h[int(y * PPU), int(x * PPU)]), mat="iron.rivet", seed=f"ibf.rv{i}", collar=False)
    emit = _glow(r, face, 0.55, st["glow"]) if st["glow"] else None
    return forge.downsample(_lit(r, "iconbtn.frame", emit=emit, wear=0.26, grime=0.55, rust=0.1, dust=0.05, exposure=st["exposure"]))


# Q/W/E sockets and EXP ----------------------------------------------------------------

AMBER = (1.0, 0.50, 0.14)


def _glow(r, shape, width_u, power, tint=AMBER):
    """Emission hugging the inside edge of a shape (linear HxWx3): a candle-lit rim for the hover states."""
    d = shape.sdf(r.xs[None, :], r.ys[:, None])
    ring = np.exp(-((d - width_u * 0.8) / width_u) ** 2) * (d > -0.2)
    return (ring[..., None] * np.array(tint, np.float32) * power).astype(np.float32)


_STATE = {"n": dict(exposure=1.0, glow=0.0, shift=0.0), "h": dict(exposure=1.18, glow=0.55, shift=0.0), "p": dict(exposure=0.72, glow=0.0, shift=0.6)}


def qwe_slot(w, state, cheek=0.0):
    """Empty socket with a heavy, uneven lip. cheek is the shoulder that ties the first socket to the plate."""
    h = 28
    st = _STATE[state]
    r = BarRelief(w, h, "iron.black", 0.0)
    x0 = cheek
    r.plate(R_(x0 + 0.2, 0.3, w - x0 - 0.5, h - 0.7, 2.6), z=0.4, bevel=3.1, profile="rolled", rise=0.55, mat="iron.warm", wobble=0.26, nick=0.04, wob_scale=5.0, seed=f"qwe.lip{w}")
    _trim(r, (x0 + 0.2, 0.3, w - x0 - 0.5, h - 0.7), 1.5, 2.6, f"qwe.lip{w}")
    if cheek:
        r.plate(R_(0.2, 2.2, cheek + 4.0, h - 4.6, 2.0), z=0.9, bevel=2.2, profile="rolled", rise=0.55, mat="iron", wobble=0.2, nick=0.03, seed="qwe.shoulder")
    pocket = R_(x0 + 3.2 + st["shift"] * 0.5, 3.1 + st["shift"], w - x0 - 6.4, h - 6.6, 1.8)
    r.pocket(pocket, depth=3.8 - st["shift"] * 1.2, wall=1.3, mat="void", wobble=0.14, nick=0.03, seed=f"qwe.pocket{w}")
    # a broken line of worn brass along the bottom lip
    zb = float(r.h[int((h - 1.4) * PPU), int((x0 + 6) * PPU)])
    for i, (a, b) in enumerate(((x0 + 5.0, x0 + 13.0), (x0 + 15.5, w - 7.0))):
        if b - a > 2.5:
            r.plate(P_([(a, h - 1.4), (a + 0.9, h - 2.0), (b - 0.9, h - 2.0), (b, h - 1.4), (b - 0.9, h - 0.8), (a + 0.9, h - 0.8)]), z=zb - 0.2, bevel=0.3, profile="chamfer", mat="brass", wobble=0.04, seed=f"qwe.brass{w}.{i}")
    emit = _glow(r, pocket, 0.55, st["glow"]) if st["glow"] else None
    return forge.downsample(_lit(r, f"qwe.slot.{w}", emit=emit, wear=0.26, grime=0.55, rust=0.12, dust=0.05, exposure=st["exposure"]))


def qwe_notch():
    """The forged ear on top of each socket: a rolled ring with a brass bead round a black eye."""
    w, h = 18, 14
    r = BarRelief(w, h, "iron.black", 0.0)
    c = (9.0, 7.0)
    r.plate(forge.Ellipse(c[0], c[1], 8.8, 6.7), z=0.3, bevel=2.6, profile="rolled", rise=0.6, mat="iron.warm", wobble=0.16, nick=0.03, seed="notch.lip")
    r.pocket(forge.Ellipse(c[0], c[1], 5.6, 3.7), depth=3.4, wall=1.1, mat="void", wobble=0.08, seed="notch.eye")
    ring = [(c[0] + 6.55 * math.cos(t), c[1] + 4.55 * math.sin(t)) for t in np.linspace(math.radians(190), math.radians(350), 40)]
    zb = float(r.h[int(c[1] * PPU), int((c[0] - 7.0) * PPU)])
    r.plate(forge.Capsule(ring, 0.5), z=zb + 0.1, bevel=0.4, profile="rolled", rise=0.9, mat="brass", wobble=0.04, seed="notch.bead")
    return forge.downsample(_lit(r, "qwe.notch", wear=0.25, grime=0.55, rust=0.1, dust=0.04))


def exp_frame():
    """Two thin forged rails and heavy end knobs round the trough; the middle is open."""
    w, h = 207, 10
    r = BarRelief(w, h, "iron.black", 0.0)
    kw = dict(profile="rolled", rise=0.55, wobble=0.10, nick=0.02, wob_scale=5.0)
    r.plate(R_(2.4, 0.0, 202.2, 2.2, 0.9), z=0.5, bevel=1.1, mat="iron.warm", seed="expf.top", **kw)
    r.plate(R_(2.4, 7.8, 202.2, 2.2, 0.9), z=0.5, bevel=1.1, mat="iron.warm", seed="expf.bot", **kw)
    for i, x in enumerate((0.0, 201.6)):
        r.plate(R_(x, 0.0, 5.4, 10.0, 2.4), z=0.8, bevel=2.4, mat="iron", seed=f"expf.end{i}", **kw)
        r.dome(x + 2.7, 5.0, 1.45, z=float(r.h[5 * PPU, int((x + 2.7) * PPU)]), mat="brass", seed=f"expf.knob{i}", collar=False)
    for i, (a, b) in enumerate(((14.0, 38.0), (96.0, 118.0), (170.0, 190.0))):
        _rail(r, a, b, 1.1, 0.8, f"expf.rail{i}", notch=False)
    return forge.downsample(_lit(r, "hud.exp.frame", wear=0.24, grime=0.5, rust=0.1, dust=0.04, exposure=1.1))


def exp_trough():
    w, h = 199, 6
    r = BarRelief(w, h, "iron.black", 1.0)
    r.plate(R_(0, 0, w, h, 0.8), z=2.0, bevel=0.5, profile="rolled", rise=0.2, mat="iron.black", seed="expt.base")
    r.pocket(R_(0.7, 0.7, w - 1.4, h - 1.4, 1.0), depth=2.6, wall=0.9, mat="void", wobble=0.04, seed="expt.pocket")
    return forge.downsample(_lit(r, "hud.exp.trough", wear=0.05, grime=0.2, rust=0.0, dust=0.0, detail=0.4))


def exp_fill():
    """Molten amber, tiling in x: a bright lip, a warm body, a deep underside, one soft seam per tile."""
    w, h = 32, 20
    y = (np.arange(h, dtype=np.float32)[:, None] + 0.5) / h
    x = (np.arange(w, dtype=np.float32)[None, :] + 0.5) / w
    stops = ((0.0, (1.00, 0.93, 0.66)), (0.16, (0.96, 0.78, 0.44)), (0.62, (0.90, 0.64, 0.26)), (1.0, (0.50, 0.30, 0.10)))
    col = np.zeros((h, w, 3), np.float32)
    for c in range(3):
        col[..., c] = np.interp(y[:, 0], [s[0] for s in stops], [s[1][c] for s in stops])[:, None]
    col *= (1 + 0.035 * np.cos(2 * math.pi * x))[..., None]
    out = np.dstack((col, np.ones((h, w), np.float32)))
    return (np.clip(out, 0, 1) * 255 + 0.5).astype(np.uint8)


def exp_cup():
    """Forged socket for the level number: a rolled ring, a worn brass bead, a black well."""
    w = h = 12
    r = BarRelief(w, h, "iron.black", 0.0)
    r.plate(forge.Ellipse(6, 6, 5.9, 5.9), z=0.3, bevel=2.0, profile="rolled", rise=0.6, mat="iron.warm", wobble=0.10, nick=0.02, wob_scale=4.0, seed="cup.lip")
    r.pocket(forge.Ellipse(6, 6, 3.7, 3.7), depth=3.0, wall=0.9, mat="void", wobble=0.05, seed="cup.well")
    ring = [(6 + 4.2 * math.cos(t), 6 + 4.2 * math.sin(t)) for t in np.linspace(math.radians(200), math.radians(520), 40)]
    zb = float(r.h[int(6 * PPU), int(1.6 * PPU)])
    r.plate(forge.Capsule(ring, 0.38), z=zb + 0.1, bevel=0.35, profile="rolled", rise=0.9, mat="brass", wobble=0.03, seed="cup.bead")
    return forge.downsample(_lit(r, "exp.cup", wear=0.22, grime=0.5, rust=0.08, dust=0.03))


# centre group and the gap between the groups ------------------------------------------


def bar_centre():
    """Centre group, 242 x 48 u: a long beam with the EXP channel in it, a cluster plate for Q/W/E, a
    stand for the skill tile and a plate for the three buttons, each overlapping its neighbour."""
    w, h = CENTRE_W, BAR_H
    r = BarRelief(w, h, "iron.black", 1.0)
    r.plate(R_(0, 0, w, h), z=0.0, bevel=0.5, rise=0.0, seed="bc.field")
    # icon plate, then the stand for the skill tile, then the Q/W/E cluster, then the beam over the top
    r.plate(R_(137.0, 14.0, 110.0, 38.0, 4.5), z=0.8, bevel=2.8, profile="rolled", rise=0.5, mat="iron.warm", wobble=0.14, nick=0.02, seed="bc.icons")
    r.pocket(R_(141.6, 16.0, 101.6, 30.4, 2.2), depth=2.2, wall=0.9, mat="void", wobble=0.05, seed="bc.icons.pocket")
    r.plate(R_(99.5, 8.0, 43.0, 44.0, 5.0), z=1.7, bevel=2.8, profile="rolled", rise=0.5, mat="iron.cold", wobble=0.14, nick=0.02, seed="bc.stand")
    r.pocket(R_(104.5, 10.5, 33.0, 37.5, 3.0), depth=2.4, wall=1.0, mat="void", wobble=0.05, seed="bc.stand.pocket")
    r.plate(R_(-8.0, 14.0, 108.5, 38.0, 4.5), z=1.0, bevel=2.8, profile="rolled", rise=0.5, mat="iron", wobble=0.14, nick=0.02, seed="bc.qwe")
    for i, (x0, ww) in enumerate(((-0.9, 34.6), (35.2, 30.8), (66.2, 30.0))):
        r.pocket(R_(x0, 17.0, ww, 30.2, 3.0), depth=2.4, wall=1.0, mat="void", wobble=0.06, seed=f"bc.qwe.pocket{i}")
    r.plate(R_(13.5, -1.5, 223.5, 15.2, 3.2), z=1.6, bevel=2.6, profile="rolled", rise=0.5, mat="iron.warm", wobble=0.14, nick=0.02, seed="bc.beam")
    r.pocket(R_(18.4, 2.6, 213.2, 9.8, 2.6), depth=2.4, wall=0.9, mat="void", wobble=0.05, seed="bc.beam.pocket")
    for i, (x, y, rad) in enumerate(((16.2, 0.9, 1.5), (233.6, 0.9, 1.5), (16.2, 11.8, 1.5), (234.6, 11.8, 1.4), (101.6, 50.0, 1.6), (140.4, 50.0, 1.6), (101.6, 9.4, 1.5), (140.4, 9.4, 1.5))):
        _bolt(r, x, y, rad, f"bc.bolt{i}")
    _rail(r, 44.0, 70.0, 0.8 + 1.0, 1.4, "bc.rail.a")
    _rail(r, 150.0, 176.0, 1.8, 1.4, "bc.rail.b")
    _rail(r, 108.0, 134.0, 46.6, 1.4, "bc.rail.c")
    return forge.downsample(_lit(r, "hud.bar.centre", wear=0.25, grime=0.55, rust=0.12, dust=0.06, detail=0.5, soft=1.8, value_map=_vmap(w, h, 95, 121, floor=0.7, mottle=0.55, seed="bc")))


def bar_gap():
    """Tiling filler, 64 x 48 u: the arm's three bands run straight through, with a strap at the seam."""
    w, h = 64, BAR_H
    r = BarRelief(w, h, "iron.black", 1.0, tile="x")
    r.plate(R_(0, 0, w, h), z=0.0, bevel=0.5, rise=0.0, seed="bg.field")
    kw = dict(profile="rolled", rise=0.5, wobble=0.14, nick=0.015)
    r.plate(R_(-8, 29, 80, 23, 3), z=0.6, bevel=2.8, mat="iron.cold", seed="bg.c", **kw)
    r.plate(R_(-8, -4, 80, 31, 3), z=1.0, bevel=2.8, mat="iron.warm", seed="bg.a", **kw)
    r.plate(R_(-8, 13, 80, 28, 3), z=2.2, bevel=3.0, mat="iron", seed="bg.b", **kw)
    for dx in (0.0, 64.0):
        r.plate(R_(-2.4 + dx, -2, 4.8, 54, 1.2), z=3.2, bevel=1.5, mat="iron.cold", seed=f"bg.strap{dx}", **kw)
        for y in (6.0, 27.0, 44.0):
            _bolt(r, dx, y, 1.3, f"bg.sb{y}{dx}")
    _rail(r, 7.0, 28.0, 8.0, 1.4, "bg.rail.a")
    _rail(r, 36.0, 57.0, 44.4, 1.4, "bg.rail.b")
    for i, (x, y) in enumerate(((20.0, 20.0), (46.0, 34.0))):
        _bolt(r, x, y, 1.5, f"bg.bolt{i}")
    return forge.downsample(_lit(r, "hud.gap", wear=0.25, grime=0.55, rust=0.1, dust=0.06, detail=0.5, soft=1.8, value_map=_vmap(w, h, 1e9, 0, top=0.9, floor=1.0, mottle=0.55, seed="bg", period=64.0)), wrap="x")


# button icons -------------------------------------------------------------------------

LEATHER = (3.6, 2.2, 1.3)
CLOTH = (6.0, 0.9, 0.8)


def _icon_render(r, seed):
    # tarnished, pitted and soot-dark: the brass glyphs were clean flat gold (grime .4, rust .04, exposure 1.15)
    return forge.downsample(_lit(r, seed, wear=0.26, grime=0.85, rust=0.26, dust=0.05, detail=1.1, exposure=0.92))


def icon_banner():
    r = BarRelief(22, 22, "iron.black", 0.0)
    r.plate(P_([(5.2, 5.4), (16.8, 5.4), (16.8, 19.4), (11.0, 15.4), (5.2, 19.4)]), z=0.4, bevel=1.3, profile="rolled", rise=0.6, mat="iron.warm", tint=CLOTH, wobble=0.10, nick=0.03, seed="banner.cloth")
    r.sweep([(2.8, 4.4), (19.2, 4.4)], 0.75, 1.6, mat="brass", step_px=2.0)
    for i, x in enumerate((2.8, 19.2)):
        r.dome(x, 4.4, 1.3, z=1.2, mat="brass", seed=f"banner.knob{i}", collar=False)
    zb = float(r.h[int(11.0 * PPU), int(11.0 * PPU)])
    r.sweep([(7.6, 9.0), (11.0, 12.4), (14.4, 9.0)], 0.55, zb + 0.35, mat="brass", step_px=2.0)
    r.sweep([(10.2, 1.6), (11.0, 1.2), (11.8, 1.6)], 0.5, 1.4, mat="brass", step_px=2.0)
    return _icon_render(r, "icon.banner")


def icon_lens():
    r = BarRelief(22, 22, "iron.black", 0.0)
    c = (9.0, 9.0)
    r.sweep([(13.7, 13.7), (19.2, 19.2)], 1.15, 0.8, mat="iron.warm", tint=LEATHER, step_px=2.0)
    r.sweep([(13.4, 13.4), (14.2, 14.2)], 1.55, 1.0, mat="brass", step_px=2.0)
    r.plate(forge.Ellipse(c[0], c[1], 7.4, 7.4), z=0.3, bevel=1.2, profile="rolled", rise=0.5, mat="brass", wobble=0.04, seed="lens.ring")
    r.pocket(forge.Ellipse(c[0], c[1], 5.6, 5.6), depth=1.8, wall=0.5, mat="void", seed="lens.glass")
    arc = [(c[0] + 4.1 * math.cos(t), c[1] + 4.1 * math.sin(t)) for t in np.linspace(math.radians(200), math.radians(262), 12)]
    zg = float(r.h[int(c[1] * PPU), int(c[0] * PPU)])
    r.plate(forge.Capsule(arc, 0.32), z=zg + 0.1, bevel=0.3, profile="rolled", rise=0.9, mat="ivory", seed="lens.glint")
    return _icon_render(r, "icon.lens")


def icon_satchel():
    r = BarRelief(22, 22, "iron.black", 0.0)
    r.plate(P_([(3.4, 9.6), (18.6, 9.6), (19.8, 18.0), (18.0, 20.4), (4.0, 20.4), (2.2, 18.0)]), z=0.4, bevel=1.7, profile="rolled", rise=0.6, mat="iron.warm", tint=LEATHER, wobble=0.10, nick=0.03, seed="satchel.body")
    r.plate(P_([(3.0, 7.2), (19.0, 7.2), (19.0, 11.4), (13.0, 14.4), (9.0, 14.4), (3.0, 11.4)]), z=1.3, bevel=1.3, profile="rolled", rise=0.6, mat="iron.warm", tint=(4.2, 2.5, 1.4), wobble=0.10, nick=0.03, seed="satchel.flap")
    strap = U.bezier((5.6, 7.6), (6.6, 0.4), (15.4, 0.4), (16.4, 7.6), 24)
    r.sweep(strap, 0.8, 0.9, mat="iron.warm", tint=LEATHER, step_px=2.0)
    r.dome(11.0, 12.8, 1.5, z=float(r.h[int(12.8 * PPU), int(11.0 * PPU)]), mat="brass", seed="satchel.clasp", collar=False)
    return _icon_render(r, "icon.satchel")


# assembly -----------------------------------------------------------------------------


def _heavy(task):
    name, args = task
    return globals()[name](*args)


def _compute(tasks):
    """The forge renders, in parallel and in a fixed order. Each task is a pure function of its arguments,
    so the worker count cannot change a byte."""
    items = list(tasks.items())
    workers = min(os.cpu_count() or 1, 10, len(items))
    if workers <= 1:
        return {k: _heavy(t) for k, t in items}
    with ProcessPoolExecutor(max_workers=workers) as ex:
        return dict(zip((k for k, _ in items), ex.map(_heavy, [t for _, t in items], chunksize=1)))


def render_tasks():
    """Sprite name -> (function, args). Slowest first, so the pool stays busy to the end."""
    t = {
        "hud.base.centre": ("bar_centre", ()),
        "hud.base.left": ("bar_left", ()),
        "hud.dragon.left": ("dragon", ()),
        "hud.talon.left": ("talon", ()),
        "gem.setting.left": ("setting", ()),
        "hud.gapfill": ("bar_gap", ()),
        "gauge.cap.left": ("gauge_cap", ()),
        "gauge.left.fill": ("gauge_fill_left", ()),
        "gauge.right.fill": ("gauge_fill_right", ()),
        "gauge.empty": ("gauge_empty", ()),
        "hud.coordsplate": ("coords_plate", ()),
        "exp.frame": ("exp_frame", ()),
        "exp.trough": ("exp_trough", ()),
        "exp.cup": ("exp_cup", ()),
        "exp.fill": ("exp_fill", ()),
        "qwe.notch": ("qwe_notch", ()),
        "skill.bracket": ("skill_bracket", ()),
        "iconbtn.plinth": ("iconbtn_plinth", ()),
    }
    for st, suf in (("n", "normal"), ("h", "hover"), ("p", "pressed")):
        t[f"hud.slab.{suf}"] = ("slab", (st,))
        t[f"qwe.slot.q.{suf}"] = ("qwe_slot", (33, st, 5.0))
        t[f"qwe.slot.w.{suf}"] = ("qwe_slot", (29, st, 0.0))
        t[f"qwe.slot.e.{suf}"] = ("qwe_slot", (28, st, 0.0))
        t[f"iconbtn.frame.{suf}"] = ("iconbtn_frame", (st,))
    for st, suf in (("n", "normal"), ("h", "hover"), ("d", "disabled")):
        t[f"skill.tile.{suf}"] = ("skill_tile", (st,))
    for ch in "qwe":
        t[f"qwe.letter.{ch}"] = ("qwe_letter", (ch,))
    for ch in "0123456789":
        t[f"hudnum.{ch}"] = ("hudnum", (ch,))
    t["hudnum.slash"] = ("hudnum", ("/",))
    return t


def hud_sprites():
    s = []
    add = lambda name, img, **kw: s.append(U.Sprite(name, img, **kw))
    big = _compute(render_tasks())
    add("hud.base.left", big["hud.base.left"])
    add("hud.base.right", U.mirror_x(big["hud.base.left"]))
    add("hud.base.centre", big["hud.base.centre"])
    add("hud.gapfill", big["hud.gapfill"], tile="x")
    for kind in ("hp", "mp"):
        gs = gem_set(kind)
        s.extend(gs)
        add(f"gem.{kind}.spill", gem_spill(kind, gs[0].image[..., 3]))
    add("gem.setting.left", big["gem.setting.left"])
    add("gem.setting.right", U.mirror_x(big["gem.setting.left"]))
    for name in ("gauge.left.fill", "gauge.right.fill", "gauge.empty"):
        add(name, big[name])
    add("gauge.cap.left", big["gauge.cap.left"])
    add("gauge.cap.right", U.mirror_x(big["gauge.cap.left"]))
    d = big["hud.dragon.left"]
    w, h = DRAGON_W * 4, DRAGON_H * 4
    eye = [int(v * 4) for v in EYE_RECT_U]
    fang = [int(v * 4) for v in FANG_RECT_U]
    add("hud.dragon.left", d, pivot=(0, h), meta={"eye": eye, "fang": fang})
    add("hud.dragon.right", U.mirror_x(d), pivot=(w, h), meta={"eye": [w - eye[0] - eye[2], *eye[1:]], "fang": [w - fang[0] - fang[2], *fang[1:]]})
    add("hud.talon.left", big["hud.talon.left"])
    add("hud.talon.right", U.mirror_x(big["hud.talon.left"]))
    for suf in ("normal", "hover", "pressed"):
        for name in (f"hud.slab.{suf}", f"qwe.slot.q.{suf}", f"qwe.slot.w.{suf}", f"qwe.slot.e.{suf}", f"iconbtn.frame.{suf}"):
            add(name, big[name])
        add(f"chat.btn.{suf}", chat_btn(suf[0]))
        add(f"chat.scroll.up.{suf}", chat_scroll("up", suf[0]))
        add(f"chat.scroll.down.{suf}", chat_scroll("down", suf[0]))
    add("hud.coordsplate", big["hud.coordsplate"])
    add("icon.scroll", icon_scroll())
    add("icon.party", icon_party())
    add("icon.pvp", icon_pvp())
    add("hud.ministat.off", ministat(False))
    add("hud.ministat.on", ministat(True))
    add("exp.frame", big["exp.frame"], slice=(16, 0, 16, 0))
    add("exp.trough", big["exp.trough"], slice=(16, 0, 16, 0))
    add("exp.fill", big["exp.fill"], tile="x")
    add("exp.tick", np.dstack((np.broadcast_to(rgb("#5A4A28"), (24 * 4, 4 * 4, 3)), np.ones((24 * 4, 4 * 4), np.float32))).astype(np.float32))
    add("exp.cup", big["exp.cup"])
    add("qwe.notch", big["qwe.notch"])
    for ch in "qwe":
        add(f"qwe.letter.{ch}", big[f"qwe.letter.{ch}"])
    add("skill.bracket", big["skill.bracket"])
    for suf in ("normal", "hover", "disabled"):
        add(f"skill.tile.{suf}", big[f"skill.tile.{suf}"])
    add("skill.icon.default", skill_icon())
    add("iconbtn.plinth", big["iconbtn.plinth"])
    add("icon.banner", icon_banner())
    add("icon.lens", icon_lens())
    add("icon.satchel", icon_satchel())
    for ch in "0123456789":
        add(f"hudnum.{ch}", big[f"hudnum.{ch}"])
    add("hudnum.slash", big["hudnum.slash"])
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
    "dragon": DRAGON, "setting": SETTING, "spill": SPILL,
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

    def add_put(name, x, y):
        """Additive sprite at full level, as the shader draws it: rgb times alpha, added."""
        s = np.asarray(S(name), dtype=np.float32) / 255
        x0, y0 = px(x), px(y + top)
        base = np.asarray(im, dtype=np.float32) / 255
        h, w = s.shape[:2]
        reg = base[y0 : y0 + h, x0 : x0 + w, :3]
        sub = s[: reg.shape[0], : reg.shape[1]]
        reg += sub[..., :3] * sub[..., 3:4]
        im.paste(Image.fromarray((np.clip(base, 0, 1) * 255 + 0.5).astype(np.uint8), "RGBA"))

    def numerals(text, cx_u, y_u, h_u=12.0):
        """The bar's own digits, bottom-aligned on one advance, shrunk to fit 50 u."""
        names = {"/": "hudnum.slash", **{str(i): f"hudnum.{i}" for i in range(10)}}
        try:
            glyphs = [S(names[c]) for c in text]
        except KeyError:
            return
        adv = glyphs[0].width
        total_w = adv * len(glyphs)
        for i, g_ in enumerate(glyphs):
            im.alpha_composite(g_, (int(px(cx_u) - total_w / 2 + i * adv), px(y_u + top)))

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
    # left group, in the game's draw order: slabs, gauge, collar, stone, talon, spill, then numerals
    put("hud.slab.normal", SLAB[0], SLAB[1])
    put("hud.coordsplate", COORDS[0], COORDS[1])
    put("gauge.empty", GAUGE_FILL[0], GAUGE_FILL[1])
    put("gauge.cap.left", GAUGE_CAP[0], GAUGE_CAP[1])
    put("gem.setting.left", SETTING[0], SETTING[1])
    put("gem.hp.full", GEM[0], GEM[1])
    put("hud.talon.left", TALON[0], TALON[1])
    add_put("gem.hp.spill", SPILL[0], SPILL[1])
    numerals("812/812", GEM[0] + GEM[2] / 2, GEM[1] + GEM[3] - 14)
    # right group
    off = total - BAR_W
    put("hud.slab.normal", BAR_W - SLAB[0] - SLAB[2] + off + gap_u * 0, SLAB[1])
    put("hud.slab.normal", BAR_W - COORDS[0] - COORDS[2] + off, COORDS[1])
    put("gauge.empty", BAR_W - GAUGE_FILL[0] - GAUGE_FILL[2] + off, GAUGE_FILL[1])
    put("gauge.cap.right", BAR_W - GAUGE_CAP[0] - GAUGE_CAP[2] + off, GAUGE_CAP[1])
    put("gem.setting.right", BAR_W - SETTING[0] - SETTING[2] + off, SETTING[1])
    put("gem.mp.full", BAR_W - GEM[0] - GEM[2] + off, GEM[1])
    put("hud.talon.right", BAR_W - TALON[0] - TALON[2] + off, TALON[1])
    add_put("gem.mp.spill", BAR_W - SPILL[0] - SPILL[2] + off, SPILL[1])
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
    put("hud.dragon.left", DRAGON[0], DRAGON[1])
    put("hud.dragon.right", total - DRAGON_W, DRAGON[1])
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
