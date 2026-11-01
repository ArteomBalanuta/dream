"""Window chrome for dream's classic MMO UI: dream/ui/window/window.png + window.json.

All art is original and procedural. Layout and colours follow spec 01 (see 07 section A).
Ornaments are built as a relief: a height map with a material id per pixel, shaded once
at the end, so mirrored halves are lit consistently and contact shadows come for free.
Chrome silhouettes use the same relief with a reflection matcap.
"""
from __future__ import annotations

import math
import sys
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw

sys.path.insert(0, str(Path(__file__).resolve().parent))
import uiart as ua  # noqa: E402
from uiart import PPU, Atlas, LIGHT_TL, Sprite, token  # noqa: E402

OUT_DIR = "window"
# Frame ornaments are mirrored in height, not in pixels; this keeps one light for both halves.
LIGHT_FRAME = (-0.3, -0.72, 0.62)
CHAIN_PERIOD = 5.5
TILE_H = 44
TILE_ANCHOR_Y = 41  # window y of a tile boundary, for both windows, so crown and bottom join


# helpers ------------------------------------------------------------------------------


def _box(a: np.ndarray, r: int, axis: int) -> np.ndarray:
    if r < 1:
        return a
    n = a.shape[axis]
    pad = r + 1
    widths = [(pad, pad) if i == axis else (0, 0) for i in range(2)]
    c = np.cumsum(np.pad(a, widths, mode="edge"), axis=axis, dtype=np.float64)
    hi = np.take(c, np.arange(pad + r, pad + r + n), axis=axis)
    lo = np.take(c, np.arange(pad - r - 1, pad - r - 1 + n), axis=axis)
    return ((hi - lo) / (2 * r + 1)).astype(np.float32)


def gblur(a: np.ndarray, sigma_px: float) -> np.ndarray:
    """Float gaussian approximation from three box passes; O(n) at any radius."""
    r = int(round((math.sqrt(4 * sigma_px * sigma_px + 1) - 1) / 2))
    out = a.astype(np.float32)
    for _ in range(3):
        out = _box(_box(out, r, 0), r, 1)
    return out


def smooth(a, lo, hi):
    t = np.clip((a - lo) / (hi - lo), 0, 1)
    return t * t * (3 - 2 * t)


def ramp(v: np.ndarray, stops: list[tuple[float, str]]) -> np.ndarray:
    """Maps a scalar field through colour stops; returns HxWx3."""
    pos = [p for p, _ in stops]
    cols = np.array([token(c) for _, c in stops], dtype=np.float32)
    return np.dstack([np.interp(v, pos, cols[:, i]) for i in range(3)]).astype(np.float32)


def rgba(h, w, colour, alpha=1.0) -> np.ndarray:
    c = token(colour) if isinstance(colour, str) else np.asarray(colour, dtype=np.float32)
    out = np.zeros((h, w, 4), dtype=np.float32)
    out[..., :3] = c
    out[..., 3] = alpha
    return out


def ribbon(pts, widths) -> list[tuple[float, float]]:
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


def bez(*p, n=28):
    return ua.bezier(p[0], p[1], p[2], p[3], n)


def mirror_pts(pts, axis_x):
    return [(2 * axis_x - x, y) for x, y in pts]


# relief -------------------------------------------------------------------------------

VOID, IRON, CHAIN, BONE, BONE_DIM, RIM, BRONZE, GUT_A, GUT_B, STEEL, CREAM, DARKBONE = range(12)

MATS = {
    # stops over lit value, ambient, diffuse, spec, shininess
    VOID: ([(0, "#060606"), (2, "#060606")], 1.0, 0.0, 0.0, 8),
    IRON: ([(0, "#040405"), (0.45, "#111113"), (0.85, "#232327"), (1.2, "#47474D"), (1.8, "#77777F")], 0.34, 0.66, 0.22, 26),
    CHAIN: ([(0, "#060608"), (0.5, "#1A191C"), (0.92, "#34343A"), (1.25, "#55555C"), (1.8, "#9A9694")], 0.28, 0.80, 0.42, 26),
    BONE: ([(0, "#0D0C09"), (0.4, "#3E3C33"), (0.72, "#8D8A67"), (1.0, "#C7BA9C"), (1.3, "#D8D2B5"), (1.7, "#F6F1DC")], 0.20, 0.82, 0.42, 36),
    BONE_DIM: ([(0, "#0E0D0A"), (0.6, "#3B3932"), (1.0, "#57544A"), (1.6, "#8C897A")], 0.32, 0.72, 0.18, 22),
    RIM: ([(0, "#3A382E"), (0.55, "#928C78"), (0.9, "#C7BA9C"), (1.15, "#E4E3BF"), (1.5, "#F6F1DC")], 0.42, 0.66, 0.30, 30),
    BRONZE: ([(0, "#14120E"), (0.7, "#3E3630"), (1.1, "#514E44"), (1.7, "#7C7666")], 0.36, 0.70, 0.16, 20),
    GUT_A: ([(0, "#0E0E0C"), (2, "#0E0E0C")], 1.0, 0.0, 0.0, 8),
    GUT_B: ([(0, "#1A170F"), (2, "#1A170F")], 1.0, 0.0, 0.0, 8),
    STEEL: ([(0, "#070708"), (0.45, "#2A2A2E"), (0.85, "#626368"), (1.25, "#A5A5A8"), (1.7, "#E8E8EA")], 0.26, 0.78, 0.70, 44),
    CREAM: ([(0, "#141208"), (0.45, "#6A6042"), (0.8, "#8D8A67"), (1.15, "#CDC7A5"), (1.6, "#EFEAD0")], 0.30, 0.74, 0.40, 34),
    DARKBONE: ([(0, "#060607"), (0.5, "#151515"), (1.0, "#3F4044"), (1.4, "#626368"), (1.9, "#A0A1A6")], 0.28, 0.76, 0.36, 30),
}


class Relief:
    """Height (u), material id and texture multiplier on a canvas of w_u x h_u."""

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
        """Filled polygon inflated into a dome; r is edge roundness in u. z is its base height.

        belly mixes in a much wider profile, so large plates keep curving instead of going flat
        behind a piped edge; that curve is what a chrome reflection needs."""
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
        b = gblur(m, r * PPU * 0.55)
        t = np.clip(2 * b - 1, 0, 1)
        dome = np.sqrt(1 - (1 - t) ** 2)
        if belly:
            wide = gblur(m, r * PPU * 1.6)
            wide = wide / max(float(wide.max()), 1e-3)
            dome = dome * (1 - belly + belly * np.sqrt(np.clip(1 - (1 - wide) ** 2, 0, 1)))
        if ridge:
            core = gblur(m, r * PPU * 1.3)
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
        sel = rho < 1
        self._write(win, sel, z + h * np.sqrt(np.clip(1 - rho, 0, 1)), mat)

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
        sel = np.abs(d) < 1
        self._write(win, sel, z + h * np.sqrt(np.clip(1 - d * d, 0, 1)), mat)

    def groove(self, pts, width, depth):
        """Cuts a soft line into the current height."""
        xs, ys = [p[0] for p in pts], [p[1] for p in pts]
        win = self._window(min(xs) - width, min(ys) - width, max(xs) + width, max(ys) + width)
        px0, py0, px1, py1 = win
        if px0 >= px1 or py0 >= py1:
            return
        im = Image.new("L", (px1 - px0, py1 - py0), 0)
        ImageDraw.Draw(im).line([(x * PPU - px0, y * PPU - py0) for x, y in pts], fill=255, width=max(1, int(width * PPU)))
        m = gblur(np.asarray(im, dtype=np.float32) / 255, width * PPU * 0.25)
        self.H[py0:py1, px0:px1] -= m * depth

    def mirrored_about(self, axis_u: float) -> None:
        """Copies everything left of axis_u onto the right, in height, not in pixels."""
        ax = int(round(axis_u * PPU))
        n = min(ax, self.w - ax)
        for arr in (self.H, self.M, self.T, self.A):
            arr[:, ax : ax + n] = arr[:, ax - n : ax][:, ::-1]
        self.A[:, ax + n :] = 0


def shade(R: Relief, light=LIGHT_FRAME, ao=1.0, ao_radius=1.0, bump=None) -> np.ndarray:
    """Relief to float RGBA at PPU."""
    H = R.H if bump is None else R.H + bump
    n = ua.normals(H)
    l = np.array(light, dtype=np.float32)
    l /= np.linalg.norm(l)
    half = l + np.array([0, 0, 1], dtype=np.float32)
    half /= np.linalg.norm(half)
    ndl = np.clip(n @ l, 0, 1) / l[2]
    ndh = np.clip(n @ half, 0, 1)
    occ = 1 - np.clip((gblur(R.H, ao_radius * PPU) - R.H) * 1.5 * ao, 0, 0.92)
    rgb = np.zeros((R.h, R.w, 3), dtype=np.float32)
    for mat, (stops, amb, dif, spc, shin) in MATS.items():
        sel = R.M == mat
        if not sel.any():
            continue
        v = (amb + dif * ndl[sel]) * R.T[sel] * occ[sel]
        col = ramp(v[None, :], stops)[0]
        s = spc * np.clip(ndh[sel] ** shin - half[2] ** shin, 0, None) * occ[sel]
        rgb[sel] = np.clip(col + s[:, None], 0, 1)
    return np.dstack((rgb, R.A)).astype(np.float32)


# stone --------------------------------------------------------------------------------


def _scratches(h, w, r, count, length_px, width=1) -> np.ndarray:
    im = Image.new("L", (w, h), 0)
    d = ImageDraw.Draw(im)
    for _ in range(count):
        x, y = float(r.random() * w), float(r.random() * h)
        ang = float(r.random() * math.tau)
        pts = [(x, y)]
        for _ in range(int(r.integers(3, 9))):
            ang += float(r.random() - 0.5) * 0.5
            step = float(r.random()) * length_px
            x += math.cos(ang) * step
            y += math.sin(ang) * step
            pts.append((x, y))
        d.line(pts, fill=int(r.integers(90, 255)), width=width)
    return np.asarray(im, dtype=np.float32) / 255


def _crack_line(p0, p1, r, rough, depth=5):
    """Midpoint displacement, so a crack wanders at every scale instead of zigzagging."""
    pts = [p0, p1]
    amp = rough
    for _ in range(depth):
        out = [pts[0]]
        for a, b in zip(pts, pts[1:]):
            dx, dy = b[0] - a[0], b[1] - a[1]
            k = float(r.normal()) * amp
            out.append(((a[0] + b[0]) / 2 - dy * k, (a[1] + b[1]) / 2 + dx * k))
            out.append(b)
        pts = out
        amp *= 0.82
    return pts


def _draw_cracks(lines, h, w, r) -> np.ndarray:
    """Antialiased crack mask with a width and strength that swell and fade along each line."""
    k = 3
    im = Image.new("L", (w * k, h * k), 0)
    d = ImageDraw.Draw(im)
    for pts in lines:
        n = len(pts) - 1
        for i, (a, b) in enumerate(zip(pts, pts[1:])):
            t = i / n
            env = math.sin(math.pi * t) ** 0.6
            wid = max(1, int(round(1 + 3.2 * env * (0.6 + 0.4 * math.sin(t * 19)))))
            d.line([(a[0] * ua.SCALE * k, a[1] * ua.SCALE * k), (b[0] * ua.SCALE * k, b[1] * ua.SCALE * k)], fill=int(90 + 165 * env), width=wid)
        for _ in range(int(r.integers(1, 3))):
            i = int(r.integers(n // 5, 4 * n // 5))
            a = pts[i]
            ang = float(r.random()) * math.tau
            ln = 5 + float(r.random()) * 12
            for sub in [_crack_line(a, (a[0] + math.cos(ang) * ln, a[1] + math.sin(ang) * ln), r, 0.3, 4)]:
                d.line([(x * ua.SCALE * k, y * ua.SCALE * k) for x, y in sub], fill=110, width=1)
    return np.asarray(im.resize((w, h), Image.BOX), dtype=np.float32) / 255


def stone_luma(h: int, w: int, name: str, band=None) -> np.ndarray:
    """Worn slate luma at atlas scale (SCALE px/u). band: (x0,y0,x1,y1) in u, a fracture zone."""
    s = ua.SCALE / PPU  # fbm scales assume PPU; this converts them to atlas px
    cloud = ua.fbm(h, w, name + ".cloud", 44 * s, 5)
    blot = ua.fbm(h, w, name + ".blot", 11 * s, 5)
    fine = ua.fbm(h, w, name + ".fine", 2.2 * s, 3)
    wx = ((ua.fbm(h, w, name + ".wx", 30 * s, 2) - 0.5) * 60).astype(np.int32)
    wy = ((ua.fbm(h, w, name + ".wy", 30 * s, 2) - 0.5) * 60).astype(np.int32)
    yy, xx = np.mgrid[0:h, 0:w]
    blot = gblur(blot[(yy + wy) % h, (xx + wx) % w], 1.0)
    height = cloud * 0.6 + blot * 0.7 + fine * 0.25
    r = ua.rng(name + ".marks")
    weight = np.zeros((h, w), dtype=np.float32)
    lines = []
    if band:
        x0, y0, x1, y1 = band
        dx, dy = x1 - x0, y1 - y0
        ln = math.hypot(dx, dy)
        px, py = (xx / ua.SCALE - x0), (yy / ua.SCALE - y0)
        along = np.clip((px * dx + py * dy) / (ln * ln), -0.15, 1.15)
        dist = np.hypot(px - along * dx, py - along * dy)
        weight = np.exp(-((dist / 24.0) ** 2)).astype(np.float32)
        ridge = 1 - np.abs(2 * ua.fbm(h, w, name + ".ridge", 26 * s, 5) - 1)
        ridge = gblur(ridge[(yy + wy) % h, (xx + wx) % w], 1.6)
        height = height + weight * (ridge**3) * 1.2
        lines.append(_crack_line((x0 + 8, y0 + 4), (x1 - 2, y1 - 6), r, 0.17))
        lines.append(_crack_line((x0 + 30, y0 + 20), (x1 - 30, y1 + 22), r, 0.15))
    # long diagonal hairlines across the sheet
    for _ in range(3):
        a = (float(r.random()) * w / ua.SCALE, float(r.random()) * h / ua.SCALE)
        b = (a[0] - 30 - float(r.random()) * 40, a[1] + 40 + float(r.random()) * 50)
        lines.append(_crack_line(a, b, r, 0.13))
    crack = _draw_cracks(lines, h, w, r)
    # lit from top right: the emboss is what makes the noise read as rock, not as fog
    emboss = np.roll(height, (1, -1), axis=(0, 1)) - np.roll(height, (-1, 1), axis=(0, 1))
    luma = 66.0 + (cloud - 0.5) * 20 + (blot - 0.5) * 20 + emboss * (84 + 200 * weight)
    luma = luma - gblur(crack, 0.7) * 24 + np.roll(gblur(crack, 0.5), (1, -1), axis=(0, 1)) * 6
    luma += ua.grain(h, w, name + ".g", 4.6)
    flecks = (r.random((h, w), dtype=np.float32) > 0.985) * r.random((h, w), dtype=np.float32) * 24 * smooth(blot, 0.35, 0.65)
    luma += gblur(flecks, 0.5) * 2.2
    luma += gblur(_scratches(h, w, r, 420, 12), 0.5) * 8
    return luma.astype(np.float32)


def slab(w_u, h_u, name, band=None) -> np.ndarray:
    """Opaque sheet: the corners outside the 6 u radius hold the dark halo, so no world shows."""
    h, w = int(h_u * ua.SCALE), int(w_u * ua.SCALE)
    luma = stone_luma(h, w, name, band)
    yy, xx = np.mgrid[0:h, 0:w].astype(np.float32)
    x, y = (xx + 0.5) / ua.SCALE, (yy + 0.5) / ua.SCALE
    rad = 6.0
    qx = np.abs(x - w_u / 2) - (w_u / 2 - rad)
    qy = np.abs(y - h_u / 2) - (h_u / 2 - rad)
    d = rad - (np.hypot(np.maximum(qx, 0), np.maximum(qy, 0)) + np.minimum(np.maximum(qx, qy), 0))
    shadow = smooth(d, 0.0, 5.0)
    edge = 42.0
    luma = edge + (luma - edge) * shadow
    # mean sits on stone.base; the shift keeps the cloud shape and the check honest
    inside = d > 0
    luma += 67.5 - float(luma[inside].mean())
    rgb = np.dstack((luma - 0.4, luma - 1.2, luma + 2.6))
    halo = np.array([8, 8, 9], dtype=np.float32)
    k = smooth(d, -0.5, 0.5)[..., None]
    rgb = rgb * k + halo * (1 - k)
    a = np.full((h, w, 1), 255, dtype=np.float32)
    return (np.dstack((np.clip(rgb, 0, 255), a)) + 0.5).astype(np.uint8)


def build_slabs() -> list[Sprite]:
    meta = {"radius": 6 * ua.SCALE}
    return [
        Sprite("win.slab.inventory", slab(165, 387, "slab.inventory"), meta=meta),
        Sprite("win.slab.combine", slab(165, 388, "slab.combine", band=(58, 262, 160, 352)), meta=meta),
    ]


# cells --------------------------------------------------------------------------------


def _mitre(w_u, h_u, top, right, bottom, left):
    """Per-pixel edge id (0 face, 1 top, 2 right, 3 bottom, 4 left) and depth 0..1 into the bevel."""
    h, w = int(h_u * PPU), int(w_u * PPU)
    yy, xx = np.mgrid[0:h, 0:w].astype(np.float32)
    x, y = (xx + 0.5) / PPU, (yy + 0.5) / PPU
    d = np.dstack((y / top, (w_u - x) / right, (h_u - y) / bottom, x / left))
    which = d.argmin(axis=2)
    depth = d.min(axis=2)
    return np.where(depth < 1, which + 1, 0), depth, x, y


def _lerp(a, b, t):
    return token(a)[None, None, :] * (1 - t[..., None]) + token(b)[None, None, :] * t[..., None]


def tile_face(name, face="#404143") -> np.ndarray:
    size = int(20 * PPU)
    n = (ua.fbm(size, size, name + ".f", 3.0, 3) - 0.5) * 0.022 + ua.grain(size, size, name + ".g", 0.006)
    return np.clip(token(face)[None, None, :] + n[..., None], 0, 1)


def cell_empty() -> np.ndarray:
    edge, depth, _, _ = _mitre(20, 20, 1.5, 1.5, 2, 2)
    rgb = tile_face("cell.empty").astype(np.float32)
    for eid, a, b in ((1, "#A5A5A5", "#797979"), (2, "#8F8F91", "#A7A6A9"), (3, "#212121", "#2C2C2C"), (4, "#1D1D1F", "#262628")):
        sel = edge == eid
        rgb[sel] = _lerp(a, b, depth)[sel]
    # a one texel lit crown where the top and right bevels meet the face
    near = (edge == 0) & (depth < 1.12)
    rgb[near] = rgb[near] * 0.93
    return np.dstack((rgb, np.ones(depth.shape, dtype=np.float32))).astype(np.float32)


def cell_flat(fill, line_v, line_h) -> np.ndarray:
    """Half lines on each edge, so tiling makes a 1 u lattice that breaks at the crossings."""
    size = int(20 * PPU)
    out = rgba(size, size, fill)
    half = int(0.5 * PPU)
    gap = int(1.25 * PPU)  # horizontal segments stop short of the verticals
    out[:, :half, :3] = token(line_v)
    out[:, -half:, :3] = token(line_v)
    out[:half, gap:-gap, :3] = token(line_h)
    out[-half:, gap:-gap, :3] = token(line_h)
    return out


def build_cells() -> list[Sprite]:
    out = [
        Sprite("cell.empty", cell_empty()),
        Sprite("cell.occupied", cell_flat("cell.occupied", "#47494B", "cell.lattice.h"), tile="xy"),
        Sprite("cell.hover", cell_flat("cell.hover", "cell.hover.lattice.v", "cell.hover.lattice.h"), tile="xy"),
        Sprite("cell.invalid", cell_flat("cell.invalid", "cell.invalid.lattice.v", "cell.invalid.lattice.h"), tile="xy"),
    ]
    size = int(20 * PPU)
    out.append(Sprite("cell.overlay.hover", rgba(size, size, "#071451", 64 / 255), tile="xy"))
    out.append(Sprite("cell.overlay.invalid", rgba(size, size, "#51070A", 64 / 255), tile="xy"))
    return out


# side columns -------------------------------------------------------------------------

_PAD = 10  # u of overdraw above and below a tile, so shading wraps exactly


def side_relief() -> Relief:
    """12 u left column over a 44 u tile, with _PAD u of overdraw on both ends."""
    H = TILE_H + 2 * _PAD
    R = Relief(12, H)
    R.rect(0, 0, 12, H, VOID)
    R.rect(7.9, 0, 1.6, H, GUT_A)
    R.rect(11, 0, 1, H, GUT_B)
    wear = ua.fbm(TILE_H * PPU, 12 * PPU, "side.wear", 5.0, 4)
    pit = ua.fbm(TILE_H * PPU, 12 * PPU, "side.pit", 0.8, 3)
    R.T = np.take(0.88 + 0.24 * wear, np.arange(R.h) - _PAD * PPU, axis=0, mode="wrap").astype(np.float32)
    pitting = np.take(pit, np.arange(R.h) - _PAD * PPU, axis=0, mode="wrap")
    # rim, with a darker worn band now and then
    R.rect(0, 0, 1.5, H, RIM, 0.1, 0.75, 0.75)
    rimwear = smooth(ua.fbm(TILE_H * PPU, 8, "side.rimwear", 3.0, 3), 0.62, 0.8)
    rim_t = np.take(rimwear[:, :1], np.arange(R.h) - _PAD * PPU, axis=0, mode="wrap")
    R.T[:, : int(1.5 * PPU)] *= (1 - 0.38 * rim_t).astype(np.float32)
    R.rect(7, 0, 1, H, BRONZE, 0.1, 0.4, 0.4)
    # chain: open links with a narrow edge-on link locking each pair
    y = _PAD - CHAIN_PERIOD
    k = 0
    cx = 4.75
    while y < H + CHAIN_PERIOD:
        R.ring(cx, y, 1.5, 2.05, 1.15, 16, CHAIN, 0.15, 0.85)
        y += CHAIN_PERIOD
        k += 1
    y = _PAD - CHAIN_PERIOD * 0.5
    y -= CHAIN_PERIOD
    while y < H + CHAIN_PERIOD:
        R.ellipsoid(cx, y, 0.78, 1.72, -18, CHAIN, 0.75, 0.6)
        R.groove([(cx - 0.1, y - 1.2), (cx + 0.1, y + 1.2)], 0.14, 0.14)
        y += CHAIN_PERIOD
    # bone column: three beads, then the long dim bar
    bx = 10.25
    for base in range(-1, 3):
        o = _PAD + base * TILE_H
        for by in (3.0, 9.0, 15.0):
            R.ellipsoid(bx - 0.25, o + by + 1.9, 0.5, 0.7, 0, BONE_DIM, 0.1, 0.25)
            R.ellipsoid(bx, o + by, 0.92, 1.55, 0, BONE, 0.1, 0.72)
            R.groove([(bx - 0.9, o + by - 0.5), (bx + 0.9, o + by + 0.3)], 0.12, 0.1)
        R.ellipsoid(bx, o + 30.5, 0.95, 11.6, 0, BONE_DIM, 0.1, 0.8)
        for gy in (22.0, 30.5, 39.0):
            R.groove([(bx - 0.9, o + gy), (bx + 0.9, o + gy + 0.2)], 0.2, 0.22)
    R.H[R.M != VOID] += ((pitting - 0.5) * 0.05)[R.M != VOID]
    return R


_side_cache: dict[str, np.ndarray] = {}


def side_layers() -> dict[str, np.ndarray]:
    if not _side_cache:
        R = side_relief()
        _side_cache.update(H=R.H, M=R.M, T=R.T, A=R.A)
    return _side_cache


def side_sprite(right: bool) -> np.ndarray:
    side = side_layers()
    w_u = 13 if right else 12
    R = Relief(w_u, TILE_H + 2 * _PAD)
    for key in "HMTA":
        src = side[key]
        if right:
            art = src[:, : 11 * PPU][:, ::-1]
            dst = getattr(R, key)
            dst[:, : 11 * PPU] = art
        else:
            setattr(R, key, src.copy())
    img = shade(R, ao=1.3, ao_radius=0.6)
    return img[_PAD * PPU : (_PAD + TILE_H) * PPU]


def build_side() -> list[Sprite]:
    return [
        Sprite("win.side.left", side_sprite(False), tile="y", meta={"tileAnchorY": TILE_ANCHOR_Y * ua.SCALE}),
        Sprite("win.side.right", side_sprite(True), tile="y", meta={"tileAnchorY": TILE_ANCHOR_Y * ua.SCALE}),
    ]


# crowns and bottom --------------------------------------------------------------------


def paste_columns(R: Relief, window_y0: float) -> None:
    """Writes the left column so its tile phase matches the side sprites at this window y."""
    side = side_layers()
    tile = TILE_H * PPU
    rows = (np.arange(R.h) + int(round((window_y0 - TILE_ANCHOR_Y) * PPU))) % tile + _PAD * PPU
    cols = 12 * PPU
    R.H[:, :cols] = side["H"][rows, :cols]
    R.M[:, :cols] = side["M"][rows, :cols]
    R.T[:, :cols] = side["T"][rows, :cols]
    R.A[:, :cols] = 1


def taper(t):
    return math.sin(min(t * 1.25 + 0.12, 1.0) * math.pi / 2) * (1 - t) ** 0.55


def leaf(t):
    return math.sin(math.pi * t) ** 0.7


def moulding(R: Relief, xs, base_y, swell, mat=CREAM, z=0.2):
    """Cream wave along the top or bottom edge: a run of leaf-shaped swells on a thin stem."""
    pts = bez((xs[0], base_y + 0.6), (xs[0] + 6, base_y + 1.4), (xs[0] + 10, base_y - 0.2), (xs[0] + 16, base_y + 0.3), n=30)
    for a, b, lift, wid in swell:
        mid = (a + b) / 2
        p = bez((a, base_y + 0.9), (a + (mid - a) * 0.5, base_y - lift), (mid + (b - mid) * 0.4, base_y - lift * 0.7), (b, base_y + 0.9), n=30)
        R.poly(ribbon(p, lambda t, w=wid: 0.5 + w * leaf(t)), mat, z, 0.8, 0.8, ridge=0.2)
    R.poly(ribbon(pts, lambda t: 0.8 * leaf(t) + 0.3), mat, z, 0.5, 0.5)


def claw(R, p, w, mat=BONE, z=0.4, h=1.2, ridge=0.25):
    R.poly(ribbon(bez(*p, n=36), lambda t, w0=w: 0.12 + w0 * taper(t)), mat, z, h, min(w, 1.5) * 0.8, ridge=ridge)


def knuckles(R, pts, size=1.0, mat=STEEL):
    for i, (x, y, a) in enumerate(pts):
        s = size * (0.9 + 0.25 * ((i * 7) % 3) / 2)
        R.ellipsoid(x + 0.3, y + 0.5, 1.15 * s, 0.8 * s, a, DARKBONE, 0.05, 0.3)
        R.ellipsoid(x, y, 1.1 * s, 0.72 * s, a, mat, 0.25, 0.75)


def rope(R, pts, step_angle=38):
    for i, (x, y) in enumerate(pts):
        R.ellipsoid(x, y, 1.02, 0.56, step_angle, DARKBONE, 0.2, 0.55)


def crown_field(R: Relief, low: float) -> None:
    """Forged backplates under the ornaments: most of the crown's mass, kept dark."""
    R.poly([(11.5, 0.5), (46, 0.5), (41, 6.6), (24, 10.4), (17, 21), (16.4, 33), (11.5, 36)], IRON, 0.05, 0.5, 1.2)
    R.groove(bez((13, 3), (24, 4), (30, 3.6), (40, 1.6)), 0.3, 0.12)
    R.groove(bez((14.8, 30), (15, 21), (20, 11), (33, 7.4)), 0.26, 0.14)
    # lower plate that carries the teeth and arcs
    R.poly([(12, low + 1.4), (24, low + 0.8), (33, low + 2.8), (60, low + 1.8), (88, low + 3.2), (97, low + 3.6), (97, 46), (12, 46)], IRON, 0.05, 0.42, 1.0)
    for x, y in ((33, 2.4), (39.4, 2.2), (13, 13), (12.9, 24), (22, 39.4), (34, 39.6), (48, 39.5), (72, 39.5)):
        R.ellipsoid(x + 0.2, y + 0.3, 0.72, 0.72, 0, IRON, 0.3, 0.2)
        R.ellipsoid(x, y, 0.5, 0.5, 0, BRONZE, 0.45, 0.32)
    # twisted cord under the top moulding
    rope(R, [(15 + i * 1.62, 5.0 + 0.35 * math.sin(i * 0.33)) for i in range(16)] + [(41 + i * 1.62, 5.1 + 0.45 * math.sin(i * 0.3)) for i in range(35)])


def title_finial(R: Relief, y: float) -> None:
    claw(R, ((21.5, y + 1.8), (25, y - 0.8), (29, y - 0.9), (33.4, y + 0.3)), 2.5, BONE, 0.5, 0.9)
    knuckles(R, [(21, y - 0.4, 40)], 0.85)


def crown_corner(R: Relief) -> None:
    # the main hook, rooted in a bone collar in the corner and sweeping in and down
    claw(R, ((3.5, 3.4), (12, 3.5), (21, 6.5), (24.5, 17.5)), 5.0, BONE, 0.5, 1.5, 0.4)
    # a counter-thorn along the top and one dropping down the column
    claw(R, ((11, 1.8), (17, 1.6), (25, 1.8), (32, 6.2)), 2.7, BONE, 0.4, 1.0)
    claw(R, ((4.2, 8), (4.5, 15), (7, 21), (13.8, 28)), 3.2, BONE, 0.45, 1.1)
    # short spurs growing off the hook
    claw(R, ((15.5, 6), (18.5, 4.5), (22, 4.5), (24.5, 5.2)), 1.5, BONE, 0.9, 0.8)
    claw(R, ((8, 8.4), (9.5, 12.5), (12, 15), (16, 16.4)), 2.1, BONE, 0.8, 0.9)
    R.groove(bez((5, 3.7), (12, 3.8), (19, 6.8), (22.5, 13)), 0.18, 0.28)
    R.groove(bez((4.7, 9), (5, 15), (7.5, 20.5), (12, 26)), 0.15, 0.2)
    knuckles(R, [(11.5, 8.7, 30), (15.3, 10.4, 62), (17.8, 13.8, 76), (19, 18.5, 95), (15.3, 19.8, 120), (10.3, 18.9, 62), (12.4, 22.3, 62), (21.5, 2.9, 12), (27, 8.2, 35), (31.5, 7.6, 18), (35.6, 6.9, 8), (15.8, 24.6, 100), (15.6, 29, 85)], 1.0)
    for a, b in (((13, 4.8), (11.5, 7.2)), ((17.8, 6.8), (15.8, 9.4)), ((21.4, 10.4), (18.8, 11)), ((5.2, 13), (7, 13.6)), ((6.3, 18), (8.2, 17.2))):
        claw(R, (a, (a[0] * 0.7 + b[0] * 0.3, a[1] * 0.7 + b[1] * 0.3), (a[0] * 0.3 + b[0] * 0.7, a[1] * 0.3 + b[1] * 0.7), b), 1.2, BONE, 1.0, 0.5)
    # collar and the white stud
    R.ellipsoid(4.2, 4.4, 3.1, 3.1, 0, DARKBONE, 0.3, 1.0)
    R.ring(4.2, 4.4, 2.5, 2.5, 0.9, 0, BONE, 0.9, 0.5)
    R.ellipsoid(5, 5, 1.0, 1.0, 0, RIM, 1.6, 0.9)


def crown_lower(R: Relief, top: float, raised: float, rod_end: bool) -> None:
    """Teeth stepping in from the column, then two shallow arcs towards the centre."""
    y = top
    for i in range(4):
        x = 17 + i * 5.2
        yy = y + i * 0.75
        claw(R, ((x + 3.2, yy - 2.4), (x + 2.2, yy - 1), (x + 0.8, yy + 0.6), (x, yy + 2.4)), 2.1, DARKBONE if i else BONE, 0.3, 0.9)
    for a, b, lift, wid, mat in ((36, 62, 3.6, 1.9, DARKBONE), (58, 87, 3.0, 1.7, DARKBONE)):
        base = y + 3.8 - raised
        p = bez((a, base), (a + (b - a) * 0.3, base + lift), (a + (b - a) * 0.7, base + lift), (b, base - 0.6), n=40)
        R.poly(ribbon(p, lambda t, w=wid: 0.3 + w * leaf(t)), mat, 0.25, 0.9, 0.8, ridge=0.25)
        q = bez((a + 4, base + 0.8), (a + (b - a) * 0.35, base + lift - 1.5), (a + (b - a) * 0.65, base + lift - 1.5), (b - 5, base), n=30)
        R.poly(ribbon(q, lambda t: 0.2 + 0.7 * leaf(t)), BONE_DIM, 0.1, 0.4, 0.4)
    # rod bracket and the twisted beads climbing the slab shoulder
    if rod_end:
        claw(R, ((12.4, 30.2), (14, 28.2), (15.5, 27.5), (18.2, 27.3)), 2.4, BONE, 0.45, 0.9)
    for i in range(4):
        by = top - 1.4 + i * 3.2
        R.ellipsoid(13.6 + 0.35 * (i % 2), by, 1.0, 1.5, 24 - 48 * (i % 2), DARKBONE if i % 2 else BONE, 0.3, 0.8)
        R.groove([(12.8, by - 0.4), (14.6, by + 0.5)], 0.14, 0.14)


def wear_bump(R: Relief, name: str) -> np.ndarray:
    n = ua.fbm(R.h, R.w, name, 0.9, 3) - 0.5
    return n * 0.075 * (R.M != VOID)


def worn(R: Relief, name: str) -> None:
    """Grime on the ornaments: wide blotches, deepest on the bone, none on the shared columns."""
    n = ua.fbm(R.h, R.w, name + ".t", 3.0, 4)
    k = 0.62 + 0.62 * smooth(n, 0.2, 0.78)
    orn = (R.M == BONE) | (R.M == CREAM)
    t = np.where(orn, k, 0.9 + 0.2 * n)
    t[:, : 12 * PPU] = np.where(orn[:, : 12 * PPU], t[:, : 12 * PPU], 1)
    R.T *= t.astype(np.float32)


def crown(name: str, h_u: int, with_rod: bool) -> np.ndarray:
    pad = 4
    R = Relief(190, h_u + pad)
    R.rect(0, 0, 188, h_u + pad, VOID)
    paste_columns(R, 0)
    lower_top = 33.4 if with_rod else 29.0
    crown_field(R, lower_top + (0 if with_rod else 0.5))
    moulding(R, (13, 94), 2.1, [(36, 53, 1.1, 1.3), (50, 69, 1.4, 1.7), (66, 79, 1.6, 2.0), (78, 95, 1.5, 1.2)])
    crown_corner(R)
    title_finial(R, 15.4 if with_rod else 17.4)
    crown_lower(R, lower_top, 0.0, with_rod)
    worn(R, name)
    R.mirrored_about(94)
    img = shade(R, ao=1.4, ao_radius=0.8, bump=wear_bump(R, name + ".b"))
    return img[: h_u * PPU]


def bottom() -> np.ndarray:
    R = Relief(190, 20)
    R.rect(0, 12, 188, 8, VOID)
    paste_columns(R, 416)
    R.rect(12, 12, 82, 8, VOID)
    R.poly([(12, 13), (40, 12.2), (97, 12), (97, 22), (12, 22)], IRON, 0.05, 0.36, 0.8)
    # the polished arc that closes the corner
    R.poly(ribbon(bez((1.8, 3.4), (8.5, 3.6), (14.5, 8.5), (16.2, 15.8), n=40), lambda t: 0.5 + 1.8 * leaf(t)), STEEL, 0.5, 1.0, 0.9, ridge=0.25)
    # twisted beads up the slab edge
    for i in range(3):
        by = 7.6 - i * 3.3
        R.ellipsoid(13.6 + 0.3 * (i % 2), by, 1.0, 1.45, 22 - 44 * (i % 2), BONE if i != 1 else DARKBONE, 0.3, 0.8)
        R.groove([(12.8, by - 0.4), (14.6, by + 0.5)], 0.14, 0.14)
    # three teardrops running along the edge
    for i, x in enumerate((19, 25, 31)):
        claw(R, ((x + 5.2 - i * 0.2, 13.1), (x + 3.4, 12.1), (x + 1.4, 12.6), (x - 0.4, 14.6)), 2.2 - 0.2 * i, BONE, 0.3, 0.9)
    moulding(R, (36, 94), 13.0, [(38, 56, 1.0, 1.2), (53, 70, 1.3, 1.5), (67, 82, 1.4, 1.8), (80, 96, 1.2, 1.4)], CREAM)
    R.ellipsoid(36.8, 14.2, 0.7, 0.7, 0, RIM, 0.6, 0.6)
    worn(R, "bottom")
    R.mirrored_about(94)
    img = shade(R, ao=1.4, ao_radius=0.8, bump=wear_bump(R, "bottom.b"))
    return img[: 16 * PPU]


def build_crowns() -> list[Sprite]:
    meta = {"tileAnchorY": TILE_ANCHOR_Y * ua.SCALE}
    return [
        Sprite("win.crown.inventory", crown("crown.inventory", 41, True), meta=meta),
        Sprite("win.crown.combine", crown("crown.combine", 40, False), meta=meta),
        Sprite("win.bottom", bottom(), meta=meta),
    ]


# scroll rod, tab, arrow, strips --------------------------------------------------------


def rod() -> np.ndarray:
    w_u, h_u = 158, 10
    h, w = h_u * PPU, w_u * PPU
    x = (np.arange(w, dtype=np.float32) + 0.5) / PPU
    ex = np.minimum(x, w_u - x)  # distance from the nearer end, the rod is symmetric
    radius = np.select(
        [ex < 1.2, ex < 6.0, ex < 6.8, ex < 16.4, ex < 17.0],
        [0.7 + ex * 0.25, 1.0 + (ex - 1.2) * 0.55, 3.2 + (ex - 6.0) * 2.1, 4.9 + 0.1 * np.sin((ex - 6.8) / 9.6 * np.pi), 4.0 + 0 * ex],
        default=4.0,
    ).astype(np.float32)
    yy = (np.arange(h, dtype=np.float32) + 0.5) / PPU - 5
    s = yy[:, None] / radius[None, :]
    alpha = (np.abs(s) < 1).astype(np.float32)
    # cylinder: dark top lip, the streak in the upper third, belly, underside, core shadow
    stops = [(-1.0, "#26272A"), (-0.82, "#626368"), (-0.52, "#BFC0C6"), (-0.28, "#7E7F85"), (0.12, "#626368"), (0.62, "#353639"), (0.88, "#151515"), (1.0, "#0A0A0B")]
    v = ramp(s, stops)
    noise = ua.fbm(h, w, "rod.n", 1.0, 3)
    streak = _box(_box(noise, 90, 1), 90, 1)
    streak = (streak - streak.min()) / (streak.max() - streak.min())
    fine = _box(ua.fbm(h, w, "rod.f", 0.4, 2), 40, 1)
    fine = (fine - fine.min()) / (fine.max() - fine.min())
    tex = 0.76 + 0.45 * streak + 0.22 * (fine - 0.5)
    # the specular band breaks up along the length
    brk = _box(ua.fbm(16, w, "rod.brk", 5.0, 4), 14, 1)[:1, :]
    brk = smooth((brk - brk.min()) / (brk.max() - brk.min()), 0.3, 0.7)
    band = np.exp(-(((s + 0.5) / 0.17) ** 2))
    tex = tex * (1 - 0.24 * band * (1 - brk))
    is_collar = (ex >= 6.0) & (ex < 17.0)
    gain = np.where(is_collar, 1.34, 1.0)[None, :]
    rgb = np.clip(v * (tex * gain)[..., None], 0, 1)
    rgb += (band * (0.3 + 0.7 * brk) * 0.11 * (fine * 0.8 + 0.4))[..., None]
    # hard dark seams between cone, collar and rod
    for a in (6.0, 7.1, 15.2, 16.9):
        d = np.abs(ex - a)[None, :]
        rgb *= np.clip(d / 0.22, 0.09, 1)[..., None]
    rgb[:, ex < 6.0] *= 0.74
    return np.dstack((np.clip(rgb, 0, 1), alpha)).astype(np.float32)


def tab(fill, alpha, lift) -> np.ndarray:
    h, w = 6 * PPU, 33 * PPU
    out = rgba(h, w, fill, alpha)
    out[: int(0.5 * PPU), :, :3] = token(lift)
    out[-int(0.5 * PPU) :, :, :3] = token("#050505")
    return out


def arrow(hover: bool) -> np.ndarray:
    m = ua.poly_mask(13, 7, [(1.0, 0.7), (12.0, 0.7), (6.5, 5.9)])
    h, w = m.shape
    v = np.linspace(0, 1, h, dtype=np.float32)[:, None]
    x = np.abs(np.linspace(-1, 1, w, dtype=np.float32))[None, :]
    base = ramp(np.clip(v * 1.2, 0, 1) * np.ones((h, w), dtype=np.float32), [(0, "#D8D8DA" if hover else "#BDBDBD"), (1, "#8A8A8A" if hover else "#6A6A6A")])
    facet = (1.06 - 0.34 * x * smooth(v, 0.0, 0.3))[..., None]
    rgb = np.clip(base * facet, 0, 1)
    img = np.dstack((rgb, m)).astype(np.float32)
    return ua.drop_shadow(img, 0.25, 0.8, 0.3, "#000000", 0.85)


def build_rod() -> list[Sprite]:
    size = 8 * PPU
    strip = rgba(size, size, "strip.fill")
    strip[: int(0.25 * PPU), :, :3] = token("#1C1C1D")
    return [
        Sprite("win.scroll.rod", rod()),
        Sprite("win.setoption.tab.normal", tab("#151515", 0.86, "#26262A")),
        Sprite("win.setoption.tab.hover", tab("#232327", 0.92, "#3A3A40")),
        Sprite("win.arrow.down.normal", arrow(False)),
        Sprite("win.arrow.down.hover", arrow(True)),
        Sprite("win.strip", strip, slice=(8, 8, 8, 8)),
        Sprite("win.linebox", rgba(size, size, "#000000", 46 / 255), slice=(8, 8, 8, 8)),
    ]


# wells --------------------------------------------------------------------------------


def _lip_shading(depth: np.ndarray) -> np.ndarray:
    """Hard bright lip: a darker outer arris and a thin shadow where it meets the wall."""
    return 0.86 + 0.14 * smooth(depth, 0.0, 0.25) - 0.22 * smooth(depth, 0.72, 1.0)


def well(w_u: int, h_u: int) -> np.ndarray:
    edge, depth, x, y = _mitre(w_u, h_u, 2.0, 1.75, 2.0, 1.5)
    h, w = edge.shape
    rgb = np.broadcast_to(token("well.fill"), (h, w, 3)).astype(np.float32).copy()
    # interior: top and right walls fade softly into the pit
    top_fade = smooth(y, 2.0, 5.5)
    right_fade = smooth(w_u - x, 1.75, 4.0)
    rgb *= (0.45 + 0.55 * top_fade * right_fade)[..., None]
    rgb[:] = np.maximum(rgb, 0.0)
    rgb += 0.012 * (ua.fbm(h, w, "well.n", 6.0, 3) - 0.5)[..., None]
    sel = edge == 1
    rgb[sel] = token("well.wall")
    sel = edge == 2
    rgb[sel] = _lerp("#151515", "#020202", depth)[sel]
    # left lip: vertical gradient that heats up towards the corner
    v = y / h_u
    lip = ramp(v, [(0, "well.bevel.top"), (0.5, "#84888D"), (1, "#C7C9CA")]) * _lip_shading(depth)[..., None]
    sel = edge == 4
    rgb[sel] = lip[sel]
    # bottom lip: hot at the left, a plateau, falling away to the right
    u = x / w_u
    low = ramp(u, [(0, "well.bevel.hi"), (0.16, "#CCCDCC"), (0.62, "well.bevel.mid"), (1, "well.bevel.lo")]) * _lip_shading(depth)[..., None]
    sel = edge == 3
    rgb[sel] = low[sel]
    # the mitred corner is the peak of the whole lip
    corner = np.exp(-(((x - 1.1) ** 2 + (h_u - 1.1 - y) ** 2) / 3.2))
    rgb += ((edge == 3) | (edge == 4))[..., None] * corner[..., None] * 0.015
    return np.dstack((np.clip(rgb, 0, 1), np.ones((h, w), dtype=np.float32))).astype(np.float32)


def currency_field() -> np.ndarray:
    edge, depth, x, y = _mitre(113, 18, 1.5, 1.5, 2.0, 1.5)
    h, w = edge.shape
    rgb = np.broadcast_to(token("currency.fill"), (h, w, 3)).astype(np.float32).copy()
    rgb *= (0.8 + 0.2 * smooth(y, 1.5, 3.5))[..., None]
    rgb[edge == 0] = np.where(y[edge == 0, None] > 3.5, token("currency.fill"), rgb[edge == 0])
    rgb[edge == 1] = token("#000000")
    rgb[edge == 2] = token("#060606")
    left = np.broadcast_to(token("#B9B9B9"), (h, w, 3)) * _lip_shading(depth)[..., None]
    rgb[edge == 4] = left[edge == 4]
    u = x / 113
    low = ramp(u, [(0, "#B4B4B4"), (0.1, "#9E9E9E"), (0.6, "currency.bevel"), (0.72, "#FAFAFA"), (0.9, "#F4F4F2"), (1, "#A8A8A8")]) * _lip_shading(depth)[..., None]
    rgb[edge == 3] = low[edge == 3]
    return np.dstack((np.clip(rgb, 0, 1), np.ones((h, w), dtype=np.float32))).astype(np.float32)


def build_wells() -> list[Sprite]:
    s3 = 3 * ua.SCALE
    out = [Sprite("well.%s" % n, well(w, h), slice=(s3, s3, s3, s3)) for n, w, h in (("1x1", 20, 20), ("2x2", 40, 40), ("3x2", 60, 40), ("2x3", 40, 60))]
    label = rgba(8 * PPU, 12 * PPU, "currency.label.plate")
    label[: int(0.25 * PPU), :, :3] = token("#1E1B15")
    label[-int(0.25 * PPU) :, :, :3] = token("#39342A")
    s4 = 4 * ua.SCALE
    out += [
        Sprite("currency.field", currency_field(), slice=(s3, s3, s3, s3)),
        Sprite("currency.plate", label),
        Sprite("tip.panel", rgba(12 * PPU, 12 * PPU, "#000000", 204 / 255), slice=(s4, s4, s4, s4)),
        Sprite("tip.band", rgba(8 * PPU, 12 * PPU, "tip.band"), slice=(s4, 0, s4, 0)),
    ]
    return out


# silhouettes --------------------------------------------------------------------------


def chrome(R: Relief, peak: float = 1.0, name: str = "sil", fade=0.4, sharp=1.0) -> np.ndarray:
    """Photographed pewter: an environment lookup rather than a Phong lobe, so curved plates
    pick up a soft window, a dark horizon and a thin floor bounce."""
    H = R.H + (ua.fbm(R.h, R.w, name + ".b", 1.6, 3) - 0.5) * 0.06 * (R.A > 0)
    H = gblur(H, 0.26 * PPU)
    n = ua.normals(H)
    rx, ry, rz = 2 * n[..., 2] * n[..., 0], 2 * n[..., 2] * n[..., 1], 2 * n[..., 2] ** 2 - 1
    k = np.array([-0.5, -0.62, 0.6], dtype=np.float32)
    k /= np.linalg.norm(k)
    s = rx * k[0] + ry * k[1] + rz * k[2]
    sky = 0.16 + 0.32 * smooth(-ry, -0.5, 0.75)
    window = 0.46 * smooth(s, 0.62, 0.93) + 0.62 * sharp * smooth(s, 0.9, 0.965)
    horizon = 0.3 * np.exp(-(((rz + 0.05) / 0.3) ** 2))
    bounce = 0.26 * smooth(rx + ry, 0.75, 1.15)
    v = (sky + window - horizon + bounce) * R.T
    occ = 1 - np.clip((gblur(R.H, 1.1 * PPU) - R.H) * 0.55, 0, 0.85)
    v = v * occ
    yy, xx = np.mgrid[0:R.h, 0:R.w].astype(np.float32)
    d = np.hypot((xx / R.w - 0.42) / 0.62, (yy / R.h - 0.36) / 0.72)
    v = v * (1 - fade * smooth(d, 0.25, 1.05))
    rgb = ramp(v, [(0, "#0B0B0C"), (0.14, "sil.lo"), (0.46, "sil.mid"), (0.78, "#C4C4C6"), (1.0, "sil.hi")])
    alpha = np.clip(gblur(R.A, 0.14 * PPU) * 1.6 - 0.3, 0, 1)
    rgb = rgb * peak
    return np.dstack((np.clip(rgb, 0, 1), alpha)).astype(np.float32)


def rot(pts, deg, origin=(0, 0), at=(0, 0), flip=False):
    a = math.radians(deg)
    c, s = math.cos(a), math.sin(a)
    out = []
    for x, y in pts:
        x, y = (-x if flip else x) - origin[0], y - origin[1]
        out.append((x * c - y * s + at[0], x * s + y * c + at[1]))
    return out


def darken(R: Relief, pts, width, amount=0.25):
    """Paints a soft dark line into T: seams, slits, engraved lines."""
    xs, ys = [p[0] for p in pts], [p[1] for p in pts]
    win = R._window(min(xs) - width, min(ys) - width, max(xs) + width, max(ys) + width)
    px0, py0, px1, py1 = win
    if px0 >= px1 or py0 >= py1:
        return
    im = Image.new("L", (px1 - px0, py1 - py0), 0)
    ImageDraw.Draw(im).line([(x * PPU - px0, y * PPU - py0) for x, y in pts], fill=255, width=max(1, int(width * PPU)), joint="curve")
    m = gblur(np.asarray(im, dtype=np.float32) / 255, width * PPU * 0.2)
    R.T[py0:py1, px0:px1] *= 1 - m * (1 - amount)


def seam(R, pts, width=0.5, depth=0.5, amount=0.2):
    R.groove(pts, width, depth)
    darken(R, pts, width, amount)


def sil_helm() -> np.ndarray:
    R = Relief(40, 40)
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
    return chrome(R, 0.96, "sil.helm")


def sil_armor() -> np.ndarray:
    R = Relief(40, 60)

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
    return chrome(R, 1.0, "sil.armor", fade=0.32)


def sil_pants() -> np.ndarray:
    R = Relief(40, 40)
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
    return chrome(R, 0.97, "sil.pants")


def boot(R: Relief, ox, oy, z, s=1.0):
    def P(pts):
        return [(ox + x * s, oy + y * s) for x, y in pts]

    # sole, sabaton, ankle, shaft, cuff; toe to the left
    R.poly(P([(1.2, 28.6), (5, 27), (22.6, 27), (22.8, 30), (2.2, 30)]), STEEL, z, 1.2, 0.8)
    R.poly(P(bez((0.6, 28), (2.5, 23), (6, 21.4), (11.5, 19)) + bez((11.5, 19), (13, 16), (13.6, 12), (13.2, 6)) + [(24, 5)] + bez((24, 5), (23.6, 13), (24.2, 20), (23.4, 28))), STEEL, z + 0.6, 7.5, 5.6, belly=0.85)
    R.poly(P(bez((11.8, 3.6), (15, 1.6), (21, 1), (25.6, 2)) + [(25.2, 6.4)] + bez((25.2, 6.4), (21, 5.6), (16, 6), (12.4, 7.6))), STEEL, z + 7.4, 2.2, 1.6, belly=0.6)
    for a, b in (((3.8, 23.2), (9.2, 26.6)), ((7, 21.6), (12.6, 26.2)), ((10.6, 19.8), (15.6, 25.4))):
        seam(R, P(bez(a, (a[0] + 3, a[1] + 0.5), (b[0] - 1, b[1] - 3), b)), 0.45 * s, 0.5, 0.25)
    seam(R, P(bez((12.6, 17), (15.5, 18.6), (20, 18.6), (23.2, 17.4))), 0.5 * s, 0.5, 0.25)
    R.ellipsoid(ox + 19.3 * s, oy + 21.5 * s, 1.5 * s, 1.5 * s, 0, STEEL, z + 6.2, 0.8)
    R.poly(P([(21.2, 26.4), (25.8, 26.6), (26, 30), (21.4, 30)]), STEEL, z + 0.2, 1.8, 1.0)  # heel


def sil_boots() -> np.ndarray:
    R = Relief(40, 40)
    boot(R, 12.2, 3.2, 0.3, 0.9)
    R.T[:] = 0.66
    boot2 = Relief(40, 40)
    boot(boot2, 5.2, 7.4, 6.5, 0.97)
    sel = boot2.A > 0
    R.H[sel] = boot2.H[sel]
    R.A[sel] = 1
    R.T[sel] = boot2.T[sel]
    return chrome(R, 0.96, "sil.boots")


def gauntlet(R: Relief, at, deg, flip, z):
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
    R = Relief(40, 40)
    gauntlet(R, (27.4, 16.6), 36, False, 0.4)
    gauntlet(R, (12.8, 16.6), -36, True, 7.5)
    return chrome(R, 0.92, "sil.gloves")


def sil_weapon_left() -> np.ndarray:
    R = Relief(40, 60)
    # the bow behind: two recurve limbs, a leather grip and a string
    up = bez((12, 28), (6.5, 20), (11.5, 9.5), (26, 5), n=40)
    low = bez((12, 28), (9, 35), (9.5, 42), (17, 50), n=40)
    R.poly(ribbon(up, lambda t: 3.0 - 1.9 * t), STEEL, 0.5, 2.0, 1.4, ridge=0.2)
    R.poly(ribbon(low, lambda t: 3.0 - 1.9 * t), STEEL, 0.5, 2.0, 1.4, ridge=0.2)
    R.poly(ribbon(bez((26, 5), (28, 4.6), (30, 5), (31, 3.2), n=12), lambda t: 1.1 - 0.7 * t), STEEL, 0.5, 1, 0.6)
    R.poly(ribbon(bez((17, 50), (18, 51), (17.8, 53), (19.6, 54), n=12), lambda t: 1.1 - 0.7 * t), STEEL, 0.5, 1, 0.6)
    R.poly(ribbon(bez((11.4, 24), (10.8, 26), (10.9, 29), (11.8, 32), n=12), lambda t: 3.9), STEEL, 1.4, 2.2, 1.3)
    for y in (25.2, 27, 28.8, 30.6):
        seam(R, [(9, y), (13.8, y + 0.4)], 0.32, 0.4, 0.35)
    R.poly(ribbon([(31, 3.4), (19.4, 53.6)], lambda t: 0.3), STEEL, 0.3, 0.3, 0.2)
    # the sword in front, point up and right, guard low and left
    tip, base = (33.6, 7.6), (11.6, 48)
    dx, dy = base[0] - tip[0], base[1] - tip[1]
    ln = math.hypot(dx, dy)
    ux, uy = dx / ln, dy / ln
    px_, py_ = -uy, ux

    def S(a, b):
        return (tip[0] + ux * a + px_ * b, tip[1] + uy * a + py_ * b)

    R.poly([S(0, 0), S(5, 2.4), S(ln - 1, 2.0), S(ln - 1, 0)], STEEL, 4, 2.4, 2.0)
    R.poly([S(0, 0), S(5, -2.4), S(ln - 1, -2.0), S(ln - 1, 0)], STEEL, 4.6, 2.6, 2.0)
    R.groove([S(7, 0), S(ln - 2, 0)], 0.5, 0.5)
    R.poly([S(ln - 2.0, -7), S(ln - 0.2, -7.6), S(ln + 0.8, -3), S(ln + 0.8, 3), S(ln - 0.2, 7.6), S(ln - 2.0, 7)], STEEL, 7.2, 1.9, 1.3)
    R.poly([S(ln + 0.5, -1.1), S(ln + 7, -1.1), S(ln + 7, 1.1), S(ln + 0.5, 1.1)], STEEL, 6, 1.6, 1.0)
    for a in (2.4, 4.2, 6):
        seam(R, [S(ln + a, -1.3), S(ln + a, 1.3)], 0.3, 0.3, 0.4)
    c = S(ln + 8.2, 0)
    R.ellipsoid(c[0], c[1], 1.9, 1.9, 0, STEEL, 6.5, 1.5)
    return chrome(R, 0.93, "sil.weapon.left")


def sil_weapon_right() -> np.ndarray:
    R = Relief(40, 60)
    # kite shield, dim, standing behind
    shield = bez((6, 13), (12, 8), (22, 7), (31, 12)) + bez((31, 12), (33.5, 26), (27.5, 41), (16.5, 54)) + bez((16.5, 54), (9, 42), (4.5, 27), (6, 13))
    R.poly(shield, STEEL, 0.2, 2.0, 3.0)
    inner = bez((8.2, 14.4), (13, 10.4), (22, 9.4), (29, 13.4)) + bez((29, 13.4), (31, 26), (25.8, 39), (16.6, 51)) + bez((16.6, 51), (10.5, 41), (6.6, 27), (8.2, 14.4))
    R.poly(inner, STEEL, 0.6, 3.4, 8)
    seam(R, [(18.3, 10), (17, 52)], 0.4, 0.4, 0.45)
    seam(R, bez((7.4, 24), (14, 22.5), (22, 22.5), (30.2, 24)), 0.4, 0.4, 0.45)
    R.T[:] = 0.5
    # the mace, handle to the lower left, flanged star head to the upper right
    a, b = (29.5, 13), (9.6, 53)
    M = Relief(40, 60)
    M.poly(ribbon([a, b], lambda t: 2.0 + 0.2 * t), STEEL, 6, 2.0, 1.2)
    dx, dy = b[0] - a[0], b[1] - a[1]
    ln = math.hypot(dx, dy)
    ux, uy = dx / ln, dy / ln
    for f, w in ((0.97, 3.2), (0.91, 2.4), (0.78, 2.6), (0.74, 3.0), (0.24, 3.4)):
        cx, cy = a[0] + dx * f, a[1] + dy * f
        M.poly(ribbon([(cx - ux * 0.6, cy - uy * 0.6), (cx + ux * 0.6, cy + uy * 0.6)], lambda t: w), STEEL, 7.2, 1.2, 0.7)
    for f in (0.79, 0.83, 0.87):
        cx, cy = a[0] + dx * f, a[1] + dy * f
        seam(M, [(cx + uy * 1.4, cy - ux * 1.4), (cx - uy * 1.4, cy + ux * 1.4)], 0.32, 0.3, 0.4)
    cx, cy = a
    for i in range(8):
        ang = math.tau * i / 8 + 0.35
        tipx, tipy = cx + math.cos(ang) * 10.4, cy + math.sin(ang) * 10.4
        bx, by = -math.sin(ang) * 2.5, math.cos(ang) * 2.5
        M.poly([(cx + bx, cy + by), (tipx, tipy), (cx - bx, cy - by)], STEEL, 7, 3.2, 1.4)
    M.ellipsoid(cx, cy, 5.8, 5.8, 0, STEEL, 7, 5.4)
    sel = M.A > 0
    R.H[sel] = M.H[sel]
    R.T[sel] = M.T[sel]
    R.A[sel] = 1
    return chrome(R, 0.95, "sil.weapon.right")


def blade_wing(R, root, tip, wid, z):
    mx, my = (root[0] + tip[0]) / 2, (root[1] + tip[1]) / 2
    dx, dy = tip[0] - root[0], tip[1] - root[1]
    p = bez(root, (mx - dy * 0.1, my + dx * 0.1), (mx + dx * 0.25 - dy * 0.14, my + dy * 0.25 + dx * 0.14), tip, n=30)
    R.poly(ribbon(p, lambda t: 0.3 + wid * math.sin(math.pi * t**0.7)), STEEL, z, 1.1, 1.3, ridge=0.2)
    seam(R, p[:27], 0.28, 0.25, 0.5)


def sil_pet() -> np.ndarray:
    R = Relief(40, 40)
    # four long blade wings in an X around a thin floating figure
    cx = 20
    blade_wing(R, (cx - 1, 15.5), (5, 4.4), 5.0, 0.5)
    blade_wing(R, (cx - 1, 18.5), (4.4, 26.2), 3.9, 0.3)
    R.mirrored_about(20)
    R.poly(bez((20, 15), (18.2, 18), (18, 22), (18.8, 25)) + bez((18.8, 25), (19.2, 29), (19.4, 33), (20, 37)) + [(20.1, 15)], STEEL, 2, 1.3, 1.1)
    R.poly(bez((20, 15), (21.8, 18), (22, 22), (21.2, 25)) + bez((21.2, 25), (20.8, 29), (20.6, 33), (20, 37)) + [(19.9, 15)], STEEL, 2, 1.3, 1.1)
    R.ellipsoid(20, 12.8, 1.9, 2.3, 0, STEEL, 2.4, 1.8)
    R.poly(ribbon(bez((18.8, 16), (15, 19), (15.4, 24), (16.8, 26.4), n=14), lambda t: 0.9 - 0.5 * t), STEEL, 2, 0.7, 0.5)
    R.poly(ribbon(bez((21.2, 16), (25, 19), (24.6, 24), (23.2, 26.4), n=14), lambda t: 0.9 - 0.5 * t), STEEL, 2, 0.7, 0.5)
    img = chrome(R, 0.5, "sil.pet", fade=0.2, sharp=0.75)
    # the faint pool of light behind it
    h, w = R.h, R.w
    yy, xx = np.mgrid[0:h, 0:w].astype(np.float32)
    pool = np.exp(-(((xx / PPU - 20) / 11) ** 2 + ((yy / PPU - 19) / 13) ** 2))
    back = rgba(h, w, "#6C6C6E")
    back[..., 3] = pool * 0.3
    return ua.over(back, img)


def feather(R, root, ang, ln, wid, curl, z):
    a = math.radians(ang)
    ux, uy = math.cos(a), math.sin(a)
    nx, ny = -uy, ux
    tip = (root[0] + ux * ln + nx * curl, root[1] + uy * ln + ny * curl)
    p = bez(root, (root[0] + ux * ln * 0.4, root[1] + uy * ln * 0.4), (root[0] + ux * ln * 0.75 + nx * curl * 0.3, root[1] + uy * ln * 0.75 + ny * curl * 0.3), tip, n=26)
    R.poly(ribbon(p, lambda t: 0.6 + wid * math.sin(math.pi * t**1.5) ** 0.55), STEEL, z, 1.7, 1.3, belly=0.5)
    seam(R, p[1:24], 0.22, 0.12, 0.72)


def sil_wings() -> np.ndarray:
    R = Relief(60, 40)
    # low fanned wings, outer primaries lowest so each one overlaps the next
    for i in range(7):
        feather(R, (26 - i * 1.7, 16 + i * 0.9), 183 - i * 13.5, 23.5 - i * 1.6, 5.2, 4, 0.3 + i * 0.5)
    for i in range(5):
        feather(R, (26.5 - i * 2.0, 15.4 + i * 0.7), 179 - i * 22, 14.5 - i * 0.9, 4.8, 2.5, 4 + i * 0.4)
    R.poly(bez((29, 16.2), (24, 11.6), (15, 11.2), (9.5, 15)) + [(17, 20.6), (28.8, 21)], STEEL, 6.4, 3, 1.8, belly=0.7)
    R.mirrored_about(30)
    R.ellipsoid(30, 17.4, 1.8, 3.4, 0, STEEL, 6.4, 2.4)
    return chrome(R, 0.5, "sil.wings", fade=0.3, sharp=0.75)


def sil_pendant() -> np.ndarray:
    R = Relief(20, 20)
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
    return chrome(R, 0.5, "sil.pendant", fade=0.0, sharp=0.8)


def sil_ring() -> np.ndarray:
    R = Relief(20, 20)
    yy, xx = np.mgrid[0:R.h, 0:R.w].astype(np.float32)
    x, y = (xx + 0.5) / PPU, (yy + 0.5) / PPU
    outer = ((x - 9.7) / 5.6) ** 2 + ((y - 10) / 6.7) ** 2 < 1
    # the hole sits off to the right: heavy band at the left, a thin one turning away, a C
    inner = ((x - 11.5) / 2.9) ** 2 + ((y - 9.9) / 4.1) ** 2 < 1
    m = (outer & ~inner).astype(np.float32)
    b = gblur(m, 0.8 * PPU)
    dome = np.sqrt(1 - (1 - np.clip(2 * b - 1, 0, 1)) ** 2)
    wide = gblur(m, 1.5 * PPU)
    R.H = (0.1 + 3.0 * dome * (0.4 + 0.6 * wide / float(wide.max()))).astype(np.float32) * m
    R.A = m
    R.M[m > 0.5] = STEEL
    R.T *= 0.5 + 0.5 * smooth(-(x - 15.0) + np.abs(y - 10) * 0.7, -1.0, 1.4)
    return chrome(R, 0.9, "sil.ring", fade=0.0)


def build_silhouettes() -> list[Sprite]:
    return [
        Sprite("sil.pet", sil_pet()), Sprite("sil.helm", sil_helm()), Sprite("sil.gloves", sil_gloves()),
        Sprite("sil.pants", sil_pants()), Sprite("sil.boots", sil_boots()), Sprite("sil.wings", sil_wings()),
        Sprite("sil.weapon.left", sil_weapon_left()), Sprite("sil.armor", sil_armor()), Sprite("sil.weapon.right", sil_weapon_right()),
        Sprite("sil.pendant", sil_pendant()), Sprite("sil.ring", sil_ring()),
    ]


# plates and glyphs --------------------------------------------------------------------


def rivet_layer(img: np.ndarray, cx: float, cy: float) -> None:
    """3 u rivet: dark ring, pale dome, a thin bright crescent low on the outer side."""
    h, w = img.shape[:2]
    r = 1.5
    x0, y0 = int((cx - r - 0.4) * PPU), int((cy - r - 0.4) * PPU)
    x1, y1 = int((cx + r + 0.4) * PPU), int((cy + r + 0.4) * PPU)
    yy, xx = np.mgrid[y0:y1, x0:x1].astype(np.float32)
    x, y = (xx + 0.5) / PPU - cx, (yy + 0.5) / PPU - cy
    d = np.hypot(x, y)
    a = (d < r).astype(np.float32)
    col = np.broadcast_to(token("btn.rivet.dark"), x.shape + (3,)).astype(np.float32).copy()
    dome = smooth(1.0 - d, 0.0, 0.7)
    col += (token("btn.rivet.dot") - token("btn.rivet.dark")) * dome[..., None]
    outer = np.sign(w / 2 / PPU - cx) * -1, np.sign(h / 2 / PPU - cy) * -1
    lit = (x * outer[0] * 0.5 + y * 0.87) / np.maximum(d, 0.01)
    cres = smooth(lit, 0.45, 0.95) * smooth(d, 1.0, 1.25) * (1 - smooth(d, 1.3, 1.5))
    col += cres[..., None] * 0.55
    # small shadow ring cut into the plate
    win = img[y0:y1, x0:x1]
    ring = (1 - smooth(d, r, r + 0.4))[..., None] * 0.5
    win[..., :3] *= 1 - ring
    win[..., :3] = win[..., :3] * (1 - a[..., None]) + np.clip(col, 0, 1) * a[..., None]


def plate(w_u, h_u, name, edges, face_gain=1.0, swap=False, desat=False, faces=("#39383C", "btn.face", "#555458"), rivet_in=4.0) -> np.ndarray:
    top, right, bottom, left = edges
    if swap:
        top, bottom = bottom, top
        right, left = left, right
    thick = (1.5, 1.5, 2.0, 2.0) if not swap else (2.0, 2.0, 1.5, 1.5)
    edge, depth, x, y = _mitre(w_u, h_u, *thick)
    h, w = edge.shape
    cloud = ua.fbm(h, w, name + ".c", 5.0, 4)
    brushed = _box(_box(ua.fbm(h, w, name + ".s", 0.5, 2), 30, 1), 30, 1)
    brushed = (brushed - brushed.min()) / (brushed.max() - brushed.min())
    v = np.clip(0.5 + (cloud - 0.5) * 0.9 + (brushed - 0.5) * 0.55, 0, 1)
    rgb = ramp(v, [(0, faces[0]), (0.5, faces[1]), (1, faces[2])]) * face_gain
    rgb += ua.grain(h, w, name + ".g", 0.012)[..., None]
    # soft pillow towards the bevels
    rgb *= (0.88 + 0.12 * smooth(depth, 1.0, 1.9))[..., None]
    for eid, (a, b) in ((1, top), (2, right), (3, bottom), (4, left)):
        sel = edge == eid
        rgb[sel] = _lerp(a, b, depth)[sel]
    out = np.dstack((np.clip(rgb, 0, 1), np.ones((h, w), dtype=np.float32))).astype(np.float32)
    for cx in (rivet_in, w_u - rivet_in):
        for cy in (rivet_in, h_u - rivet_in):
            rivet_layer(out, cx, cy)
    if desat:
        lum = out[..., :3].mean(axis=2, keepdims=True)
        out[..., :3] = (out[..., :3] * 0.5 + lum * 0.5) * 0.72
    return out


GLYPH_MATS = {
    "amber": ([(0, "#3E2A08"), (0.5, "#BB761B"), (0.92, "#F9AA0C"), (1.3, "#F4DC77"), (1.8, "#FFF4C4")], 0.30, 0.74, 0.5, 30),
    "tan": ([(0, "#2B1C06"), (0.45, "#735C3F"), (0.85, "#BEB289"), (1.2, "#E5DAB0"), (1.7, "#FBF4DC")], 0.30, 0.74, 0.3, 26),
    "leather": ([(0, "#1E1305"), (0.5, "#4D3A22"), (0.95, "#8B7350"), (1.4, "#C6AE80"), (1.8, "#E5DAB0")], 0.32, 0.72, 0.18, 18),
    "gold": ([(0, "#403628"), (0.5, "#B4780F"), (0.95, "#F0C532"), (1.25, "#FFD546"), (1.8, "#FFF4B0")], 0.32, 0.72, 0.6, 34),
}
AMBER, TAN, LEATHER, GOLD = 20, 21, 22, 23
MATS.update({AMBER: GLYPH_MATS["amber"], TAN: GLYPH_MATS["tan"], LEATHER: GLYPH_MATS["leather"], GOLD: GLYPH_MATS["gold"]})


def glyph_finish(R: Relief, hover: bool, shadow=(0.55, 0.65), name="glyph") -> np.ndarray:
    if hover:
        R.T = R.T * 1.3
    H = R.H + (ua.fbm(R.h, R.w, name + ".b", 0.8, 3) - 0.5) * 0.05 * (R.A > 0)
    R2 = Relief(R.w_u, R.h_u)
    R2.H, R2.M, R2.T, R2.A = H, R.M, R.T, R.A
    img = shade(R2, LIGHT_TL, ao=1.2, ao_radius=0.5)
    return ua.drop_shadow(img, shadow[0], shadow[1], 0.2, "#201505", 0.85)


def glyph_close(hover) -> np.ndarray:
    R = Relief(18, 18)
    w = lambda t: 4.0 + 1.3 * abs(2 * t - 1) ** 1.5  # arms widen slightly to their ends
    first = [(3.2 + 11.6 * i / 16, 3.2 + 11.6 * i / 16) for i in range(17)]
    second = [(14.8 - 11.6 * i / 16, 3.2 + 11.6 * i / 16) for i in range(17)]
    cross = Relief(18, 18)
    for arm in (first, second):
        cross.poly(ribbon(arm, w), AMBER, 0, 1.0, 0.1)
    m = (cross.A > 0.5).astype(np.float32)
    rounded = (gblur(m, 0.7 * PPU) > 0.5).astype(np.float32)  # eases every corner of the cross
    b = gblur(rounded, 0.8 * PPU)
    dome = np.sqrt(1 - (1 - np.clip(2 * b - 1, 0, 1)) ** 2)
    wide = gblur(rounded, 1.7 * PPU)
    R.H = (0.2 + 1.9 * dome * (0.45 + 0.55 * wide / float(wide.max()))).astype(np.float32) * rounded
    R.M[rounded > 0.5] = AMBER
    R.A = rounded
    return glyph_finish(R, hover, name="glyph.close")


def glyph_repair(hover) -> np.ndarray:
    R = Relief(18, 18)
    # anvil: horn left, heel right, waist, wide foot
    R.poly([(3.2, 16), (14.8, 16), (13.6, 14.2), (11.8, 13.4), (11.4, 11.2), (6.8, 11.2), (6.4, 13.4), (4.4, 14.2)], LEATHER, 0.2, 1.2, 0.8)
    R.poly(bez((0.8, 8.2), (2.6, 9.6), (4.2, 9.6), (5.6, 9.6), n=10) + [(5.6, 11.6), (14, 11.6), (15.4, 10.4), (15.6, 8.2)], TAN, 0.6, 1.5, 0.9)
    # hammer striking it: haft to the upper right, head low on the face
    R.poly(ribbon([(9.6, 7), (15.8, 1.2)], lambda t: 1.5), LEATHER, 1.8, 0.9, 0.6)
    R.poly(rot([(-1.7, -3.7), (1.7, -3.7), (2.0, 3.0), (1.4, 3.8), (-1.4, 3.8), (-2.0, 3.0)], 47, (0, 0), (8.6, 6.2)), TAN, 2.2, 1.5, 0.9)
    return glyph_finish(R, hover, name="glyph.repair")


def glyph_store(hover) -> np.ndarray:
    R = Relief(18, 18)
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
    R = Relief(28, 24)
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


def build_plates() -> list[Sprite]:
    e24 = (("#BDBFBE", "#96979A"), ("#7E8084", "btn.bevel.right"), ("btn.wall", "#0A0A0A"), ("#000001", "#0A0A0B"))
    e44 = (("#BFBEC3", "#99989C"), ("#A2A1A6", "#8A898E"), ("#1D1F20", "#141516"), ("#212327", "#000000"))
    f44 = ("#3F3B41", "#4C4A51", "#5A5660")
    out = [
        Sprite("btn.plate24.normal", plate(24, 24, "btn24", e24)),
        Sprite("btn.plate24.hover", plate(24, 24, "btn24", e24, 1.1)),
        Sprite("btn.plate24.pressed", plate(24, 24, "btn24", e24, swap=True)),
        Sprite("btn.plate24.disabled", plate(24, 24, "btn24", e24, desat=True)),
        Sprite("btn.plate44x32.normal", plate(44, 32, "btn44", e44, faces=f44)),
        Sprite("btn.plate44x32.hover", plate(44, 32, "btn44", e44, 1.1, faces=f44)),
        Sprite("btn.plate44x32.pressed", plate(44, 32, "btn44", e44, swap=True, faces=f44)),
    ]
    for nm, fn in (("close", glyph_close), ("repair", glyph_repair), ("store", glyph_store), ("combine", glyph_combine)):
        out.append(Sprite(f"glyph.{nm}.normal", fn(False)))
        out.append(Sprite(f"glyph.{nm}.hover", fn(True)))
    return out


# assembly -----------------------------------------------------------------------------


def all_sprites() -> list[Sprite]:
    out: list[Sprite] = []
    for fn in (build_slabs, build_cells, build_side, build_crowns, build_rod, build_wells, build_silhouettes, build_plates):
        out += fn()
    return out


def main(out_root: Path) -> None:
    atlas = Atlas("window", Path(out_root) / OUT_DIR, meta={"source": "tools/ui-assets/gen_window.py"})
    for s in all_sprites():
        atlas.add(s)
    atlas.save()


_WELLS = (
    ("pet", "2x2", 14, 46), ("helm", "2x2", 74, 46), ("wings", "3x2", 114, 46),
    ("weapon.left", "2x3", 14, 89), ("pendant", "1x1", 54, 89), ("armor", "2x3", 74, 89), ("weapon.right", "2x3", 134, 89),
    ("gloves", "2x2", 14, 152), ("ring", "1x1", 54, 152), ("pants", "2x2", 74, 152), ("ring", "1x1", 114, 152), ("boots", "2x2", 134, 152),
)
_BG = (24, 26, 22, 255)


def _frame(get, kind: str) -> Image.Image:
    S = ua.SCALE
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


def compose(get, kind: str) -> Image.Image:
    S = ua.SCALE
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
        occupied = {(2, 0), (3, 0), (2, 1), (3, 1), (2, 2), (3, 2), (0, 2), (1, 2), (0, 3), (1, 3), (6, 4), (7, 4), (6, 5), (7, 5), (6, 6), (7, 6), (6, 7), (7, 7)}
        for c, r in occupied:
            put("cell.occupied", 15 + 20 * c, 200 + 20 * r)
        for c, r in ((0, 2), (1, 2), (0, 3), (1, 3)):
            put("cell.hover", 15 + 20 * c, 200 + 20 * r)
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

    S = ua.SCALE
    inv, com = compose(get, "inventory"), compose(get, "combine")
    board = Image.new("RGBA", (380 * S + 40, 432 * S + 40), _BG)
    board.alpha_composite(com, (20, 20))
    board.alpha_composite(inv, (20 + 190 * S, 20))
    board.convert("RGB").save(out_dir / "window_composite.png", compress_level=6)
    board.convert("RGB").resize((board.width // 2, board.height // 2), Image.LANCZOS).save(out_dir / "window_composite_half.png", compress_level=6)
    ua.contact_sheet([atlas_json], out_dir / "window_sprites.png", cols=8)


if __name__ == "__main__":
    root = Path(__file__).resolve().parents[2] / "dream" / "ui"
    main(root)
    write_previews(root / OUT_DIR / "window.json", Path(__file__).resolve().parent / "previews")
