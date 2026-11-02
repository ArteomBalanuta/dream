"""Item icons, tier variants, masks, ramps and the aura blob for dream/ui/items/.

Every icon is a small 2.5D scene: parts are polygons or strokes with a height profile, written
into per-pixel normal, albedo and material buffers. A studio environment then shades the
buffers, which is what gives metal its sharp speculars and dark horizon lines.
Design coordinates are u with the origin at the footprint's top-left; a 6 u margin surrounds it.
"""
from __future__ import annotations

import json
import math
import sys
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw

sys.path.insert(0, str(Path(__file__).resolve().parent))
import forge as fg  # noqa: E402
import uiart  # noqa: E402
from uiart import PPU, SCALE, SS, Atlas, Sprite, fbm, rng, sdf, token  # noqa: E402

MARGIN = 6.0
CELL = 20
UP = np.array([-0.34, -0.78, 0.52], dtype=np.float32)
UP = UP / np.linalg.norm(UP)
KEY = np.array(uiart.LIGHT_TL, dtype=np.float32)
KEY = KEY / np.linalg.norm(KEY)
FILL = np.array([0.72, 0.42, 0.55], dtype=np.float32)
FILL = FILL / np.linalg.norm(FILL)


# helpers ------------------------------------------------------------------------------


def col(c) -> np.ndarray:
    return np.asarray(token(c) if isinstance(c, str) else c, dtype=np.float32)


def M(base: dict, **kw) -> dict:
    out = dict(base)
    out.update(kw)
    return out


def smooth(a, b, x):
    t = np.clip((x - a) / (b - a), 0, 1)
    return t * t * (3 - 2 * t)


def box(a: np.ndarray, r: int) -> np.ndarray:
    """Separable box blur with edge clamp."""
    if r < 1:
        return a
    for axis in (0, 1):
        pad = [(0, 0), (0, 0)]
        pad[axis] = (r + 1, r)
        c = np.cumsum(np.pad(a, pad, mode="edge"), axis=axis, dtype=np.float64)
        if axis == 0:
            a = (c[2 * r + 1 :, :] - c[: -(2 * r + 1), :]) / (2 * r + 1)
        else:
            a = (c[:, 2 * r + 1 :] - c[:, : -(2 * r + 1)]) / (2 * r + 1)
    return a.astype(np.float32)


def soft(a: np.ndarray, r: float) -> np.ndarray:
    r = max(1, int(round(r)))
    return box(box(a, r), r)


def resize(a: np.ndarray, w: int, h: int) -> np.ndarray:
    return np.asarray(Image.fromarray(a.astype(np.float32), "F").resize((w, h), Image.BICUBIC), dtype=np.float32)


def quick_sdf(mask: np.ndarray) -> np.ndarray:
    """Signed distance in hi-res px, + inside, computed on a reduced grid."""
    h, w = mask.shape
    area = h * w
    f = 4 if area > 120000 else 2 if area > 12000 else 1
    if f == 1:
        return sdf(mask.astype(np.float32))
    pad_h, pad_w = (-h) % f, (-w) % f
    m = np.pad(mask.astype(np.float32), ((0, pad_h), (0, pad_w)))
    sh, sw = m.shape[0] // f, m.shape[1] // f
    small = m.reshape(sh, f, sw, f).mean(axis=(1, 3))
    d = soft(sdf(small) * f + (np.clip(small, 0, 1) - 0.5) * f * 0.8, 1)
    return resize(d, sw * f, sh * f)[:h, :w]


def edge_dist(chains, w: int, h: int, radii=None) -> np.ndarray:
    """Exact distance in hi-res px from a coarse grid over a w x h crop to polylines, upsampled.

    Analytic distances keep slanted edges free of the stair steps a rasterised SDF would print
    into the normals. radii, one (r0, r1) per chain, turns the result into r - distance.
    """
    f = 4 if w * h > 40000 else 2
    gw, gh = w // f + 2, h // f + 2
    yy, xx = np.mgrid[0:gh, 0:gw].astype(np.float32)
    xx, yy = (xx + 0.5) * f, (yy + 0.5) * f
    best = np.full((gh, gw), 1e9, dtype=np.float32)
    for ci, ch in enumerate(chains):
        a, b = np.asarray(ch[:-1], dtype=np.float32), np.asarray(ch[1:], dtype=np.float32)
        ln = np.hypot(*(b - a).T)
        total = float(ln.sum()) or 1.0
        walked = 0.0
        for (ax, ay), (bx, by), sl in zip(a, b, ln):
            dx, dy = bx - ax, by - ay
            ll = float(dx * dx + dy * dy) or 1e-6
            u = np.clip(((xx - ax) * dx + (yy - ay) * dy) / ll, 0, 1)
            d = np.hypot(xx - (ax + u * dx), yy - (ay + u * dy))
            if radii is not None:
                r0, r1 = radii[ci]
                d = d - (r0 + (r1 - r0) * (walked + u * sl) / total)
            best = np.minimum(best, d)
            walked += float(sl)
    if radii is not None:
        best = -best
    return resize(best, gw * f, gh * f)[:h, :w]


def cr(pts, n: int = 8, closed: bool = False) -> list:
    """Catmull-Rom through pts."""
    p = [np.array(q, dtype=float) for q in pts]
    if closed:
        p = [p[-1]] + p + [p[0], p[1]]
    else:
        p = [p[0]] + p + [p[-1]]
    out = []
    for i in range(1, len(p) - 2):
        for k in range(n):
            t = k / n
            a = 2 * p[i] + (p[i + 1] - p[i - 1]) * t
            a = a + (2 * p[i - 1] - 5 * p[i] + 4 * p[i + 1] - p[i + 2]) * t * t
            a = a + (-p[i - 1] + 3 * p[i] - 3 * p[i + 1] + p[i + 2]) * t ** 3
            out.append(tuple(0.5 * a))
    if not closed:
        out.append(tuple(p[-2]))
    return out


def ell(cx, cy, rx, ry, deg: float = 0, n: int = 48) -> list:
    a = math.radians(deg)
    pts = []
    for i in range(n):
        t = math.tau * i / n
        x, y = rx * math.cos(t), ry * math.sin(t)
        pts.append((cx + x * math.cos(a) - y * math.sin(a), cy + x * math.sin(a) + y * math.cos(a)))
    return pts


def sym(half: list, cx: float) -> list:
    """Closes a right-hand outline with its mirror about x=cx."""
    return list(half) + [(2 * cx - x, y) for x, y in reversed(half)]


def mirror(pts: list, cx: float) -> list:
    return [(2 * cx - x, y) for x, y in pts]


def shift(pts: list, dx: float, dy: float) -> list:
    return [(x + dx, y + dy) for x, y in pts]


def rot(pts: list, deg: float, c: tuple) -> list:
    a = math.radians(deg)
    ca, sa = math.cos(a), math.sin(a)
    return [(c[0] + (x - c[0]) * ca - (y - c[1]) * sa, c[1] + (x - c[0]) * sa + (y - c[1]) * ca) for x, y in pts]


# materials: metal 0 dielectric .. 1 mirror; gloss = phong exponent; spec = highlight gain; mott = paint noise ----

STEEL = dict(c="#AEB5BF", metal=1.0, gloss=120, spec=1.6, mott=0.10, tint=0.25)
DARK_STEEL = dict(c="#6F757E", metal=1.0, gloss=90, spec=1.2, mott=0.16, tint=0.3)
IRON = dict(c="#4D4F53", metal=0.9, gloss=50, spec=0.8, mott=0.28, tint=0.2)
GOLD = dict(c="#FFB62A", metal=1.0, gloss=90, spec=1.5, mott=0.10, tint=0.85)
PALE_GOLD = dict(c="#EBCB7A", metal=1.0, gloss=100, spec=1.5, mott=0.10, tint=0.7)
BRONZE = dict(c="#B9763A", metal=1.0, gloss=60, spec=1.0, mott=0.20, tint=0.7)
BRASS = dict(c="#D9A441", metal=1.0, gloss=70, spec=1.2, mott=0.16, tint=0.7)
WOOD = dict(c="#7A4E2A", metal=0.0, gloss=30, spec=0.25, mott=0.35, tint=0.3)
DARK_WOOD = dict(c="#4A2D18", metal=0.0, gloss=40, spec=0.35, mott=0.3, tint=0.3)
LEATHER = dict(c="#7B4A24", metal=0.0, gloss=24, spec=0.35, mott=0.32, tint=0.3)
DARK_LEATHER = dict(c="#3F2614", metal=0.0, gloss=30, spec=0.4, mott=0.25, tint=0.3)
BONE = dict(c="#E9DFC4", metal=0.05, gloss=40, spec=0.45, mott=0.18, tint=0.1)
CLOTH = dict(c="#203E6E", metal=0.0, gloss=12, spec=0.15, mott=0.2, tint=0.3)
STONE = dict(c="#7D7A76", metal=0.0, gloss=14, spec=0.15, mott=0.5, tint=0.2)
CORD = dict(c="#B79868", metal=0.0, gloss=16, spec=0.2, mott=0.25, tint=0.2)
BLACK = dict(c="#141416", metal=0.5, gloss=80, spec=0.8, mott=0.05, tint=0.1)


def gemstone(c: str, emis: float = 0.0) -> dict:
    return dict(c=c, metal=0.75, gloss=200, spec=2.2, mott=0.02, tint=0.15, emis=emis)


# scene --------------------------------------------------------------------------------


class Scene:
    def __init__(self, name: str, cols: int, rows: int):
        self.name, self.cols, self.rows = name, cols, rows
        self.fw, self.fh = cols * CELL, rows * CELL
        self.W, self.H = int((self.fw + 2 * MARGIN) * PPU), int((self.fh + 2 * MARGIN) * PPU)
        H, W = self.H, self.W
        self.alb = np.zeros((H, W, 3), dtype=np.float32)
        self.a = np.zeros((H, W), dtype=np.float32)
        self.nrm = np.zeros((H, W, 3), dtype=np.float32)
        self.nrm[..., 2] = 1
        self.metal = np.zeros((H, W), dtype=np.float32)
        self.gloss = np.full((H, W), 30, dtype=np.float32)
        self.spec = np.zeros((H, W), dtype=np.float32)
        self.tintk = np.zeros((H, W), dtype=np.float32)
        self.emis = np.zeros((H, W), dtype=np.float32)
        self.ao = np.ones((H, W), dtype=np.float32)
        self.deg, self.c, self.k = 0.0, (self.fw / 2, self.fh / 2), 1.0
        self.last = None
        self._noise: dict = {}

    def place(self, deg: float = 0.0, k: float = 1.0, c: tuple | None = None) -> None:
        """Rotate and scale the design about c (default the footprint centre)."""
        self.deg, self.k = deg, k
        self.c = c or (self.fw / 2, self.fh / 2)

    def px(self, pts) -> np.ndarray:
        p = np.array(pts, dtype=np.float64)
        a = math.radians(self.deg)
        c = np.array(self.c)
        d = (p - c) * self.k
        rx = d[:, 0] * math.cos(a) - d[:, 1] * math.sin(a)
        ry = d[:, 0] * math.sin(a) + d[:, 1] * math.cos(a)
        return np.stack((rx + c[0] + MARGIN, ry + c[1] + MARGIN), axis=1) * PPU

    def design_xy(self, x0, y0, w, h):
        """Design coordinates of a crop's pixels."""
        yy, xx = np.mgrid[0:h, 0:w].astype(np.float32)
        px = (xx + x0 + 0.5) / PPU - MARGIN - self.c[0]
        py = (yy + y0 + 0.5) / PPU - MARGIN - self.c[1]
        a = math.radians(-self.deg)
        rx = px * math.cos(a) - py * math.sin(a)
        ry = px * math.sin(a) + py * math.cos(a)
        return rx / self.k + self.c[0], ry / self.k + self.c[1]

    def noise(self, scale: float) -> np.ndarray:
        if scale not in self._noise:
            h, w = self.H // 4, self.W // 4
            n = fbm(h, w, f"{self.name}.{scale}", scale * 4, 3)
            self._noise[scale] = resize(n, self.W, self.H)
        return self._noise[scale]

    # core write ---------------------------------------------------------------------

    def _crop(self, p: np.ndarray, pad: float):
        pad_px = int(pad * PPU)
        x0 = max(int(p[:, 0].min()) - pad_px, 0)
        y0 = max(int(p[:, 1].min()) - pad_px, 0)
        x1 = min(int(p[:, 0].max()) + pad_px + 1, self.W)
        y1 = min(int(p[:, 1].max()) + pad_px + 1, self.H)
        return x0, y0, max(x1, x0 + 1), max(y1, y0 + 1)

    def _height(self, m, bevel, prof, slope, dist=None):
        d = quick_sdf(m) if dist is None else dist
        d = np.maximum(d / PPU, 0)
        b = max(bevel * self.k, 1e-3)
        t = np.clip(d / b, 0, 1)
        if callable(prof):
            h = prof(d / self.k)
        elif prof == "chamfer":
            h = t * b
        elif prof == "pillow":
            h = np.sin(t * math.pi / 2) ** 2 * b
        elif prof == "flat":
            h = np.zeros_like(t)
        else:
            h = np.sqrt(np.clip(1 - (1 - t) ** 2, 0, 1)) * b
        return h * slope

    def part(self, m, x0, y0, mat, bevel=1.0, prof="round", slope=1.0, cast=0.5, paint=None, alpha=None,
             bump=None, normal=None, thick=None, emis=None, blur=1.0, dist=None):
        h_c, w_c = m.shape
        if not m.any():
            return
        if thick:
            self._side(m, x0, y0, mat, thick)
            # the wall may have reached beyond; contact shadow below still uses m
        sel = m
        ys = (y0, y0 + h_c)
        xs = (x0, x0 + w_c)
        view = (slice(*ys), slice(*xs))
        if cast > 0:
            r = int(0.45 * PPU)
            sh = np.roll(m.astype(np.float32), (int(0.5 * PPU * self.k), int(0.4 * PPU * self.k)), axis=(0, 1))
            sh = soft(sh, r) * cast
            self.ao[view] *= 1 - sh
        c = np.broadcast_to(col(mat["c"]), (h_c, w_c, 3)).copy()
        X, Y = self.design_xy(x0, y0, w_c, h_c)
        if paint is not None:
            p = paint(X, Y)
            c = p if p.ndim == 3 else c * p[..., None]
        if mat.get("mott", 0):
            n = self.noise(4.0)[view]
            c = c * (1 + (n - 0.5) * mat["mott"] * 1.5)[..., None]
        if normal is not None:
            n3 = np.array(normal, dtype=np.float32)
            n3 = np.broadcast_to(n3 / np.linalg.norm(n3), (h_c, w_c, 3))
        else:
            h = self._height(m, bevel, prof, slope, dist)
            if blur:
                h = soft(h, max(1.0, blur * 2))
            if bump:
                sc, amp = bump
                h = h + (self.noise(sc)[view] - 0.5) * amp
            gy, gx = np.gradient(h)
            n3 = np.dstack((-gx * PPU, -gy * PPU, np.ones_like(h)))
            n3 = n3 / np.linalg.norm(n3, axis=2, keepdims=True)
        al = 1.0 if alpha is None else alpha(X, Y) if callable(alpha) else alpha
        al = np.broadcast_to(np.asarray(al, dtype=np.float32), (h_c, w_c))
        old_a = self.a[view]
        new_a = al + old_a * (1 - al)
        w_new = al / np.maximum(new_a, 1e-5)
        alb = self.alb[view]
        alb[sel] = (c * w_new[..., None] + alb * (1 - w_new[..., None]))[sel]
        self.a[view] = np.where(sel, new_a, old_a)
        self.nrm[view][sel] = n3[sel]
        self.metal[view][sel] = mat["metal"]
        self.gloss[view][sel] = mat["gloss"]
        self.spec[view][sel] = mat["spec"]
        self.tintk[view][sel] = mat.get("tint", 0.3)
        e = mat.get("emis", 0.0) if emis is None else emis(X, Y) if callable(emis) else emis
        e = np.broadcast_to(np.asarray(e, dtype=np.float32), (h_c, w_c))
        self.emis[view][sel] = e[sel]
        self.ao[view][sel] = 1
        self.last = (x0, y0, m)

    def _side(self, m, x0, y0, mat, thick):
        dx, dy = thick[0] * PPU * self.k, thick[1] * PPU * self.k
        steps = max(2, int(max(abs(dx), abs(dy)) / 2))
        h, w = m.shape
        out = m.copy()
        for i in range(1, steps + 1):
            sx, sy = int(dx * i / steps), int(dy * i / steps)
            out |= np.roll(m, (sy, sx), axis=(0, 1))
        wall = M(mat, c=col(mat["c"]) * 0.72)
        self.part(out & ~m, x0, y0, wall, normal=(0.42, 0.48, 0.77), cast=0.0)

    def _mask(self, x0, y0, x1, y1):
        im = Image.new("L", (x1 - x0, y1 - y0), 0)
        return im, ImageDraw.Draw(im)

    # primitives ---------------------------------------------------------------------

    def poly(self, pts, mat, holes=None, pad: float = 3.0, **kw):
        p = self.px(pts)
        thick = kw.get("thick")
        pad = max(pad, (max(abs(thick[0]), abs(thick[1])) + 2) if thick else 0)
        x0, y0, x1, y1 = self._crop(p, pad)
        im, d = self._mask(x0, y0, x1, y1)
        origin = np.array([x0, y0])
        chains = [np.vstack((p, p[:1])) - origin]
        d.polygon([(a - x0, b - y0) for a, b in p], fill=255)
        for hole in holes or []:
            q = self.px(hole)
            d.polygon([(a - x0, b - y0) for a, b in q], fill=0)
            chains.append(np.vstack((q, q[:1])) - origin)
        dist = None if kw.get("normal") is not None else edge_dist(chains, x1 - x0, y1 - y0)
        self.part(np.asarray(im) > 127, x0, y0, mat, dist=dist, **kw)

    def ellipse(self, cx, cy, rx, ry, mat, deg: float = 0, **kw):
        self.poly(ell(cx, cy, rx, ry, deg), mat, **kw)

    def ring(self, cx, cy, rx, ry, w, mat, deg: float = 0, **kw):
        self.poly(ell(cx, cy, rx, ry, deg), mat, holes=[ell(cx, cy, rx - w, ry - w, deg)], **kw)

    def stroke(self, pts, width, mat, width_end=None, bevel=None, prof="round", dash=None, **kw):
        """Polyline of circles; a capsule with a round profile reads as a cylinder."""
        pts = list(pts)
        p = self.px(pts)
        k = self.k * PPU
        x0, y0, x1, y1 = self._crop(p, max(width, width_end or 0) * self.k + 3)
        im, d = self._mask(x0, y0, x1, y1)
        seg = np.hypot(*(p[1:] - p[:-1]).T)
        total = float(seg.sum()) or 1.0
        walked = 0.0
        for i, s in enumerate(seg):
            n = max(1, int(s / 2))
            for j in range(n + 1):
                t = j / n
                f = (walked + s * t) / total
                if dash and ((walked + s * t) / k) % (dash[0] + dash[1]) > dash[0]:
                    continue
                w = width if width_end is None else width + (width_end - width) * f
                r = w * k / 2
                x = p[i, 0] + (p[i + 1, 0] - p[i, 0]) * t - x0
                y = p[i, 1] + (p[i + 1, 1] - p[i, 1]) * t - y0
                d.ellipse([x - r, y - r, x + r, y + r], fill=255)
            walked += s
        bevel = max(width, width_end or 0) / 2 if bevel is None else bevel
        dist = None
        if not dash:
            r1 = width if width_end is None else width_end
            dist = edge_dist([p - np.array([x0, y0])], x1 - x0, y1 - y0, [(width * k / 2, r1 * k / 2)])
        self.part(np.asarray(im) > 127, x0, y0, mat, bevel=bevel, prof=prof, dist=dist, **kw)

    def groove(self, pts, w=0.5, depth=0.5, closed=False, dark=0.55, clip=True, dash=None):
        """Engrave (depth>0) or raise (depth<0) a line into what is already drawn."""
        pts = list(pts) + ([pts[0]] if closed else [])
        p = self.px(pts)
        x0, y0, x1, y1 = self._crop(p, w + 1)
        im, d = self._mask(x0, y0, x1, y1)
        k = self.k * PPU
        seg = np.hypot(*(p[1:] - p[:-1]).T)
        walked = 0.0
        r = w * k / 2
        for i, sl in enumerate(seg):
            n = max(1, int(sl / 2))
            for j in range(n + 1):
                t = j / n
                if dash and ((walked + sl * t) / k) % (dash[0] + dash[1]) > dash[0]:
                    continue
                x, y = p[i] + (p[i + 1] - p[i]) * t
                d.ellipse([x - x0 - r, y - y0 - r, x - x0 + r, y - y0 + r], fill=255)
            walked += sl
        m = np.asarray(im, dtype=np.float32) / 255
        h = -soft(m, max(1, r * 0.5)) * depth
        gy, gx = np.gradient(h)
        view = (slice(y0, y1), slice(x0, x1))
        n3 = self.nrm[view].copy()
        n3[..., 0] -= gx * PPU
        n3[..., 1] -= gy * PPU
        n3 /= np.linalg.norm(n3, axis=2, keepdims=True)
        keep = self.a[view] > 0
        if clip and self.last is not None:
            lx, ly, lm = self.last
            lmask = np.zeros(self.a.shape, dtype=bool)
            lmask[ly : ly + lm.shape[0], lx : lx + lm.shape[1]] = lm
            keep = keep & lmask[view]
        self.nrm[view][keep] = n3[keep]
        if depth > 0 and dark < 1:
            f = 1 - (1 - dark) * m
            self.ao[view] = np.where(keep, self.ao[view] * f, self.ao[view])

    def cut(self, cx, cy, rx, ry, mat, n=8, table=0.5, deg=0.0, glow=None, seed=""):
        """Faceted stone: a table, a star ring and a girdle, each facet one flat normal."""
        r = rng(f"{self.name}.cut.{seed}.{cx:.1f}.{cy:.1f}")
        a0 = math.radians(deg) - math.pi / 2 - math.pi / n
        ang = [a0 + math.tau * i / n for i in range(n)]
        out = [(cx + rx * math.cos(t), cy + ry * math.sin(t)) for t in ang]
        tb = [(cx + rx * table * math.cos(t + math.pi / n), cy + ry * table * math.sin(t + math.pi / n)) for t in ang]
        self.poly(out, mat, prof="flat", cast=0.6, emis=glow)
        c0 = col(mat["c"])
        for i in range(n):
            j, t = (i + 1) % n, ang[i] + math.pi / n
            for tri, lift in (([out[i], out[j], tb[i]], 0.95), ([tb[i], out[j], tb[j]], 0.55)):
                tt = t + (0.45 if lift < 0.9 else 0)
                nv = (math.cos(tt) * lift, math.sin(tt) * lift, 0.8)
                sh = float(0.82 + r.random() * 0.4)
                self.poly(tri, M(mat, c=c0 * sh), normal=nv, cast=0, pad=0.5, emis=glow)
        self.poly(tb, M(mat, c=np.clip(c0 * 1.18, 0, 1)), normal=(-0.12, -0.14, 1), cast=0, pad=0.5, emis=glow)

    def rivets(self, pts, r, mat):
        for x, y in pts:
            self.ellipse(x, y, r, r, mat, bevel=r, cast=0.5, pad=1.0)

    # output -------------------------------------------------------------------------

    def center(self, top_overflow: float | None = None) -> None:
        """Moves the art so its bbox is centred on the footprint (or sits top_overflow above it)."""
        ys, xs = np.where(self.a > 0)
        cx, cy = (xs.min() + xs.max() + 1) / 2, (ys.min() + ys.max() + 1) / 2
        dx = int(round((self.W / 2 - cx) / SS)) * SS
        dy = int(round((self.H / 2 - cy) / SS)) * SS
        if top_overflow is not None:
            dy = int(round(((MARGIN - top_overflow) * PPU - ys.min()) / SS)) * SS
        for name in ("alb", "a", "nrm", "metal", "gloss", "spec", "tintk", "emis", "ao"):
            setattr(self, name, np.roll(getattr(self, name), (dy, dx), axis=(0, 1)))

    def render(self):
        """Returns float RGBA hi-res and float hi-res mask channels (R metal, G reflect, B rim, A emissive)."""
        alb = self.alb
        # a slight lens bend: flat faces pick up a gradient instead of one uniform reflection
        yy, xx = np.mgrid[0 : self.H, 0 : self.W].astype(np.float32)
        n = self.nrm.copy()
        n[..., 0] += (xx / self.W - 0.5) * 0.30
        n[..., 1] += (yy / self.H - 0.5) * 0.30
        n /= np.linalg.norm(n, axis=2, keepdims=True)
        nz = n[..., 2:]
        r = 2 * nz * n - np.array([0, 0, 1], dtype=np.float32)
        t = r @ UP
        # chrome-style studio: bright sky with a hard dark horizon, soft bounce from the ground
        sky = 0.56 + 0.95 * np.exp(-np.maximum(t, 0) * 2.6)
        ground = 0.05 + 0.32 * smooth(0.0, 0.8, -t)
        horizon = smooth(-0.05, 0.05, t)
        env = ground * (1 - horizon) + sky * horizon
        rk = r @ KEY
        rf = r @ FILL
        box_light = smooth(0.80, 0.90, rk)
        env = env + box_light * 1.0 + 0.22 * smooth(0.55, 1.0, rf)
        ndl = np.clip(n @ KEY, 0, 1)
        ndf = np.clip(n @ FILL, 0, 1)
        lit = 0.30 + 1.05 * ndl / 0.65 * 0.75 + 0.10 * ndf
        phong = np.clip(rk, 0, 1) ** self.gloss
        broad = np.clip(rk, 0, 1) ** np.maximum(self.gloss / 6, 2)
        spec = (phong * 1.3 + broad * 0.3) * self.spec
        fres = (1 - np.clip(nz[..., 0], 0, 1)) ** 3
        m3 = self.metal[..., None]
        rgb = alb * (1 - m3) * lit[..., None] + alb * m3 * env[..., None]
        white = np.array([1, 1, 1], dtype=np.float32)
        spec_col = white * (1 - self.tintk[..., None]) + np.clip(alb * 1.3, 0, 1) * self.tintk[..., None]
        rgb = rgb + spec[..., None] * spec_col
        rgb = rgb + (fres * (1 - self.metal) * 0.18)[..., None] * env[..., None]
        # macro falloff: the key is at the upper left of the model
        ys, xs = np.where(self.a > 0)
        x0, x1, y0, y1 = xs.min(), xs.max() + 1, ys.min(), ys.max() + 1
        fall = ((xx - x0) / max(x1 - x0, 1) + (yy - y0) / max(y1 - y0, 1)) / 2
        rgb = rgb * (1.14 - 0.34 * fall)[..., None]
        rgb = rgb * self.ao[..., None]
        rgb = rgb + alb * self.emis[..., None] * 1.1
        # contrast curve with a soft shoulder
        x = np.clip(rgb, 0, None)
        x = x * x * (3.0 - 2.0 * np.clip(x, 0, 1)) * 0.45 + x * 0.55
        x = np.where(x > 0.8, 0.8 + 0.2 * (1 - np.exp(-(x - 0.8) / 0.2)), x)
        img = np.dstack((np.clip(x, 0, 1), self.a)).astype(np.float32)

        solid = self.a > 0.5
        d = quick_sdf(solid) / PPU
        rim = 1 - smooth(0.0, 1.4, np.maximum(d, 0))
        rim = np.clip(rim * 0.8 + fres * 0.6, 0, 1)
        refl = np.clip(0.5 + 0.35 * n[..., 0] + 0.35 * n[..., 1], 0, 1)
        mask = np.dstack((self.metal, refl, rim, np.clip(self.emis, 0, 1))).astype(np.float32)
        return img, mask


def mask_down(mask: np.ndarray, alpha: np.ndarray) -> np.ndarray:
    """Coverage-weighted box reduction of the mask channels to uint8."""
    f = SS
    h, w = mask.shape[0] // f, mask.shape[1] // f
    a = alpha[: h * f, : w * f].reshape(h, f, w, f, 1)
    m = mask[: h * f, : w * f].reshape(h, f, w, f, 4)
    wt = a.sum(axis=(1, 3))
    avg = (m * a).sum(axis=(1, 3)) / np.maximum(wt, 1e-5)
    avg[..., 3] *= (wt[..., 0] / (f * f))
    avg = np.where(wt > 0, avg, 0)
    return (np.clip(avg, 0, 1) * 255 + 0.5).astype(np.uint8)


# shared item parts --------------------------------------------------------------------


def haft(s, p0, p1, w, mat, wraps=None, wrap_mat=None):
    """Round shaft from p0 to p1 with optional wrapped span (t0, t1, bands) along it."""
    s.stroke([p0, p1], w, mat, bump=(1.5, 0.25))
    if wraps:
        t0, t1, bands = wraps
        for i in range(bands):
            a = t0 + (t1 - t0) * i / bands
            b = t0 + (t1 - t0) * (i + 0.72) / bands
            q0 = (p0[0] + (p1[0] - p0[0]) * a, p0[1] + (p1[1] - p0[1]) * a)
            q1 = (p0[0] + (p1[0] - p0[0]) * b, p0[1] + (p1[1] - p0[1]) * b)
            s.stroke([q0, q1], w + 0.5, wrap_mat or DARK_LEATHER, cast=0.7)


def blade(s, cx, outline, mat, thick=(0.5, 0.6), slope=0.42):
    """Symmetric blade: outline is [(y, half width)] from tip to base; a mitred ridge runs down the centre."""
    half = [(cx + hw, y) for y, hw in outline]
    bevel = max(hw for _, hw in outline) * 1.05
    s.poly(sym(half, cx), mat, bevel=bevel, prof="chamfer", slope=slope, thick=thick)


def stud(s, x, y, r, mat=GOLD):
    s.ellipse(x, y, r, r, mat, bevel=r, pad=1.0, cast=0.6)


# 2x3 ----------------------------------------------------------------------------------


def icon_builder_hammer(s):
    s.place(-24, 0.98)
    haft(s, (20, 7), (20, 60), 3.3, WOOD, wraps=(0.56, 0.96, 9), wrap_mat=DARK_LEATHER)
    s.stroke([(20, 60), (20, 61.2)], 4.4, DARK_STEEL)
    # steel head: flat striking face to the left, curved claw to the right
    pts = [(9.5, 3), (12, 2), (22, 2), (26, 3.2), (33, 5.2), (38, 12), (35.5, 9.8), (30, 8.6), (25.5, 9.6), (22, 11.2),
           (12, 11.2), (9.5, 10.2)]
    s.poly(pts, STEEL, bevel=2.2, prof="chamfer", slope=0.8, thick=(0.8, 1.0))
    s.poly([(4.5, 1.2), (9.6, 1.6), (9.6, 11.6), (4.5, 12)], DARK_STEEL, bevel=1.4, prof="chamfer", thick=(0.5, 0.8))
    s.poly([(2.8, 1.8), (4.6, 1.2), (4.6, 12), (2.8, 11.4)], STEEL, bevel=0.8, prof="chamfer", thick=(0.3, 0.5))
    s.poly([(17, 0.6), (23, 0.6), (23, 13), (17, 13)], BRASS, bevel=1.1, thick=(0.4, 0.6))
    s.rivets([(20, 3.2), (20, 10.4)], 1.0, STEEL)


def icon_stone_maul(s):
    s.place(-22, 0.98)
    haft(s, (20, 12), (20, 60), 4.2, DARK_WOOD, wraps=(0.6, 0.97, 8), wrap_mat=M(LEATHER, c="#8A6236"))
    s.stroke([(20, 59.5), (20, 61.5)], 5.6, IRON)
    block = [(3, 7), (7, 1.5), (19, -1), (32, 0.5), (37.5, 6), (38.5, 16), (34, 22.5), (20, 25), (8, 23), (2, 17)]
    s.poly(block, M(STONE, c="#8C8880"), bevel=3.2, prof="chamfer", slope=0.8, thick=(1.0, 1.2), bump=(2.0, 1.1))
    s.groove([(9, 2), (12, 9), (8, 14)], w=0.5, depth=0.7)
    s.groove([(30, 3), (27, 10), (33, 15), (30, 22)], w=0.5, depth=0.7)
    s.groove([(5, 17), (13, 15), (16, 22)], w=0.4, depth=0.6)
    # iron bands round the block
    for x, w in ((13.5, 3.6), (26.5, 3.6)):
        pts = [(x - w / 2, -1.2), (x + w / 2, -1.4), (x + w / 2, 25.5), (x - w / 2, 25.3)]
        s.poly(pts, M(DARK_STEEL, c="#6D7178"), bevel=1.5, thick=(0.3, 0.5))
        s.rivets([(x, 1.5), (x, 12), (x, 22.5)], 0.9, STEEL)


def icon_field_blade(s):
    """Short curved falchion with a big belly: reads as a work blade, not a sword."""
    s.place(-26, 1.0)
    edge = cr([(16.5, 39), (15.2, 28), (12.5, 16), (13, 8), (17, 1)], 6)
    back = cr([(17, 1), (24, 6), (29, 15), (28, 27), (24.5, 39)], 6)
    pts = edge + back[1:]
    s.poly(pts, STEEL, bevel=3.4, prof="chamfer", slope=0.55, thick=(0.6, 0.7))
    s.groove(cr([(22.4, 37), (23.8, 27), (23.5, 16), (19, 7)], 6), w=0.7, depth=0.6, dark=0.7)
    s.poly([(12, 38.5), (29, 38.5), (30.5, 40.5), (29, 43), (12, 43), (10.5, 40.5)], BRASS, bevel=1.5, thick=(0.5, 0.7))
    haft(s, (20.5, 43), (20.5, 56.5), 4.2, DARK_LEATHER, wraps=(0.0, 1.0, 6), wrap_mat=LEATHER)
    s.ellipse(20.5, 58.2, 3.2, 2.6, BRASS, bevel=2.2)


def icon_iron_blade(s):
    """Long straight broadsword: fuller, wide cross guard, round pommel."""
    s.place(-22, 1.0)
    iron = M(STEEL, c="#9AA2AC")
    blade(s, 20, [(-5, 0.2), (-1, 3.2), (2, 4.2), (30, 4.0), (43, 4.9)], iron, slope=0.5)
    s.groove([(20, 4), (20, 41)], w=1.5, depth=0.9, dark=0.62)
    s.poly([(7.5, 42.2), (32.5, 42.2), (34.5, 40.6), (34.5, 45.8), (32.5, 44.8), (7.5, 44.8), (5.5, 45.8), (5.5, 40.6)],
           DARK_STEEL, bevel=1.3, thick=(0.5, 0.7))
    s.ellipse(20, 43.5, 2.6, 2.6, GOLD, bevel=2)
    haft(s, (20, 45), (20, 58), 3.6, DARK_LEATHER, wraps=(0.05, 1.0, 7), wrap_mat=M(LEATHER, c="#6C3D1E"))
    s.ellipse(20, 60, 3.2, 3.2, DARK_STEEL, bevel=3, thick=(0.3, 0.4))
    s.ellipse(20, 60, 1.3, 1.3, GOLD, bevel=1.2, cast=0.3)


def icon_axe_twinbit(s):
    s.place(-22, 0.9)
    haft(s, (20, -1), (20, 62), 2.9, M(WOOD, c="#64402A"), wraps=(0.72, 0.95, 6), wrap_mat=DARK_LEATHER)
    s.stroke([(20, 61.5), (20, 63.5)], 3.8, GOLD)
    s.ellipse(20, -2.4, 2.1, 2.1, GOLD, bevel=2)
    outer = cr([(39, -3), (41.2, 7), (41.2, 17), (38.5, 27)], 5)
    bit = cr([(22, 9.5), (29, 6.5), (35, 1.5), (39, -3)], 5) + outer[1:]
    bit += cr([(38.5, 27), (34, 22), (28, 18.5), (22, 16.5)], 5)[1:]
    inner = cr([(38.5, 27), (37.4, 17.5), (37.4, 7), (39, -3)], 5)
    for flip in (False, True):
        f = (lambda p: mirror(p, 20)) if flip else (lambda p: p)
        s.poly(f(bit), M(STEEL, c="#8A929E"), bevel=8, prof="chamfer", slope=0.3, thick=(0.7, 0.8))
        s.poly(f(outer + inner[1:]), M(STEEL, c="#F2EEE2"), bevel=1.6, prof="chamfer", slope=0.8, cast=0.25)
        s.last = None
        s.groove(f(cr([(25, 11), (29, 10), (33, 8.5)], 4)), w=0.5, depth=0.5)
        s.groove(f(cr([(25, 14.5), (29, 15.5), (33, 17.2)], 4)), w=0.5, depth=0.5)
    boss = [(20, 2.5), (23.6, 6), (24.4, 10), (24.4, 16), (23.6, 20), (20, 23.5), (16.4, 20), (15.6, 16), (15.6, 10), (16.4, 6)]
    s.poly(boss, GOLD, bevel=3.2, prof="chamfer", slope=0.8, thick=(0.4, 0.5))
    s.cut(20, 13, 2.5, 3.0, gemstone("#C8391E", 0.25), n=6)


def icon_shield_kite(s):
    s.place(0, 0.98)
    half = cr([(20, 0), (28, 0.6), (35, 3), (36, 12), (34.2, 26), (29, 40), (23, 52), (20, 58)], 5)
    outline = sym(half, 20)

    def field(x, y):
        t = np.clip((y - 2) / 55, 0, 1)[..., None]
        base = col("#9A1E1E") * (1 - t) + col("#4A0C10") * t
        chev = np.abs(y - 26 + np.abs(x - 20) * 0.75) < 3.0
        return np.where(chev[..., None], col("#E9D8A8"), base)

    s.poly(outline, STEEL, bevel=2.5, thick=(1.1, 1.3))
    inner = [(20 + (x - 20) * 0.84, 3 + y * 0.86) for x, y in outline]
    s.poly(inner, M(CLOTH, metal=0.3, gloss=50, spec=0.6, mott=0.1), bevel=15, prof="pillow", slope=0.4, paint=field, cast=0.7)
    s.poly([(20, 7), (24.5, 11.5), (20, 16), (15.5, 11.5)], GOLD, bevel=2.6, prof="chamfer", slope=0.8, thick=(0.4, 0.5))
    s.rivets([(9, 6.8), (31, 6.8), (7.4, 17), (32.6, 17), (10, 31), (30, 31), (14.5, 43), (25.5, 43), (20, 53)], 0.95, GOLD)


# 1x1 ----------------------------------------------------------------------------------


def icon_relic_disc(s):
    s.ellipse(10, 10, 8.3, 8.1, GOLD, bevel=1.4, thick=(0.7, 0.8))
    s.groove(ell(10, 10, 6.6, 6.4), w=0.3, depth=0.3, closed=True)
    s.ellipse(10, 10, 5.9, 5.7, M(BLACK, c="#3A2410", metal=0.6, gloss=60), bevel=0.5, cast=0.0)
    n = 18
    for i in range(n):
        a = math.tau * i / n
        stud(s, 10 + 7.15 * math.cos(a), 10 + 7.0 * math.sin(a), 0.62, PALE_GOLD)
    arm = [(10, 3.8), (11.6, 5.3), (10.9, 8.4), (11.6, 8.9), (14.8, 8.3), (16.3, 10)]
    q = arm + [(14.8, 11.7), (11.6, 11.1), (10.9, 11.6), (11.6, 14.7), (10, 16.2)]
    s.poly(sym(q, 10), GOLD, bevel=1.5, prof="chamfer", slope=0.8, thick=(0.3, 0.4))
    s.cut(10, 10, 1.7, 1.7, gemstone("#A81818", 0.2), n=6)


def icon_ring_ice(s):
    s.place(10)
    # back of the hoop, then the front, with an interior wall showing through the opening
    s.ring(10, 12.2, 6.4, 5.8, 1.7, M(GOLD, c="#C48C22"), bevel=0.9, thick=(0.3, 0.4))
    s.poly(cr([(3.8, 12.5), (6, 16.8), (10, 18.3), (14, 16.8), (16.2, 12.5), (14.2, 15.4), (10, 16.6), (5.8, 15.4)], 5, True), GOLD, bevel=1.0, cast=0.4)
    s.poly([(5.8, 7.4), (14.2, 7.4), (12.3, 10.2), (7.7, 10.2)], GOLD, bevel=1.0, prof="chamfer", slope=0.9, thick=(0.3, 0.4))
    s.cut(10, 4.8, 4.4, 4.0, gemstone("#A7D2EE", 0.15), n=8, table=0.55)
    s.rivets([(6.3, 7.4), (13.7, 7.4), (10, 8.9)], 0.62, PALE_GOLD)


def icon_gem_amber(s):
    def glow(x, y):
        return 0.55 * np.exp(-(((x - 10) / 3.4) ** 2 + ((y - 10.5) / 5.0) ** 2))

    s.ellipse(10, 10, 5.5, 8.1, gemstone("#3A2410"), prof="flat", cast=0.5, thick=(0.3, 0.4))
    s.cut(10, 10, 5.2, 7.8, gemstone("#C8701C"), n=12, table=0.56, glow=glow)


def icon_pendant_ward(s):
    chain = cr([(2.5, -0.5), (5.2, 3.6), (10, 7.4), (14.8, 3.6), (17.5, -0.5)], 8)
    s.stroke(chain, 0.95, PALE_GOLD, dash=(0.8, 0.45), cast=0.4)
    s.ring(10, 7.6, 1.5, 1.5, 0.6, GOLD, bevel=0.3)
    half = cr([(10, 8.6), (13.6, 9.8), (15, 13), (13.2, 17), (10, 20.4)], 5)
    s.poly(sym(half, 10), GOLD, bevel=1.1, thick=(0.45, 0.55))
    s.cut(10, 13.9, 3.5, 4.5, gemstone("#1FB28E", 0.15), n=8, table=0.5)
    s.rivets([(10, 9.5)], 0.6, PALE_GOLD)


# 1x2 ----------------------------------------------------------------------------------


def flame(s, cx, base, w, h, c_out="#E8480C", c_mid="#FFA61E", c_core="#FFF6C8", glow=1.0):
    def tongue(k, c, emis):
        ww, hh = w * k, h * k
        pts = cr([(cx - ww * 0.5, base - hh * 0.12), (cx - ww * 0.55, base - hh * 0.42), (cx - ww * 0.18, base - hh * 0.68),
                  (cx - ww * 0.26, base - hh * 0.86), (cx + ww * 0.06, base - hh), (cx + ww * 0.1, base - hh * 0.72),
                  (cx + ww * 0.38, base - hh * 0.6), (cx + ww * 0.52, base - hh * 0.32), (cx + ww * 0.42, base - hh * 0.04),
                  (cx, base + hh * 0.03)], 6, True)
        s.poly(pts, dict(c=c, metal=0, gloss=20, spec=0.1, mott=0.05, tint=0.5, emis=emis), bevel=3, prof="pillow", slope=0.3, cast=0)

    tongue(1.0, c_out, 0.65 * glow)
    tongue(0.72, c_mid, 0.85 * glow)
    tongue(0.42, c_core, glow)


def icon_feather_azure(s):
    s.place(7)
    bend = lambda y: 1.6 * np.sin(np.clip(y, 0, 40) / 40 * math.pi)  # noqa: E731
    r_side = [(10.2, 0.2), (13.8, 4), (15.2, 10), (15.4, 13.2), (12.6, 14), (15.6, 15.6), (15.5, 22), (14.8, 26.2), (11.8, 27), (14, 28.6), (12, 32.4), (10.4, 33.6)]
    l_side = [(9.8, 33.2), (7.2, 29), (6, 25), (8.6, 24.4), (5.4, 22.6), (4.8, 15), (5.2, 9.2), (8.4, 9.6), (5.8, 7), (7.6, 3)]
    outline = [(x + float(bend(y)), y) for x, y in cr(r_side, 3) + cr(l_side, 3)]

    def paint(x, y):
        c = 10 + bend(y)
        v = np.abs(x - c)
        u = y + v * 1.25
        band = 0.5 + 0.5 * np.sin(u * 2.0)
        barbs = 0.5 + 0.5 * np.sin(u * 9.0)
        t = np.clip(0.28 + band * 0.55 + (barbs - 0.5) * 0.3 - v * 0.05, 0, 1)[..., None]
        deep, mid, light = col("#1E3A5E"), col("#4F7FB0"), col("#CFE4F6")
        return np.where(t < 0.5, deep + (mid - deep) * t * 2, mid + (light - mid) * (t - 0.5) * 2)

    s.poly(outline, M(BONE, c="#95B9DE", metal=0.15, gloss=60, spec=0.5, mott=0.05), bevel=5.5, prof="pillow", slope=0.32, paint=paint, thick=(0.3, 0.4))
    spine = [(10 + float(bend(y)), y) for y in np.linspace(1.5, 40, 24)]
    s.stroke(spine, 0.5, M(BONE, c="#F4F6F4"), width_end=1.5, bevel=0.75, cast=0.45)


def glass(c: str, emis: float) -> dict:
    return dict(c=c, metal=0.2, gloss=160, spec=1.6, mott=0.02, tint=0.2, emis=emis)


def icon_survey_lantern(s):
    """Bullseye lantern: brass drum, chimney, carry ring and one big round lens."""
    s.ring(10, 2.8, 3.1, 3.1, 1.0, BRASS, bevel=0.5)
    s.poly([(7.6, 5), (12.4, 5), (13.2, 9.2), (6.8, 9.2)], BRASS, bevel=1.6, prof="chamfer", slope=0.8, thick=(0.3, 0.4))
    for x in (8.6, 10, 11.4):
        s.groove([(x, 6), (x, 8.6)], w=0.5, depth=0.6, dark=0.3)
    s.poly([(4, 10.8), (6, 8.8), (14, 8.8), (16, 10.8)], M(BRASS, c="#E8BE5C"), bevel=1.2, prof="chamfer", slope=0.8, thick=(0.3, 0.3))
    body = lambda x, y: col("#D9A441") * (1.1 - 0.06 * np.abs(x - 8))[..., None]  # noqa: E731
    s.poly([(3.2, 10.8), (16.8, 10.8), (16.8, 29), (15, 31), (5, 31), (3.2, 29)], BRASS, bevel=6.8, prof="round", slope=0.6, paint=body, thick=(0.5, 0.6))
    s.ellipse(10, 20, 6.2, 6.2, DARK_STEEL, bevel=1.2, thick=(0.3, 0.4))

    def lens(x, y):
        r = np.hypot(x - 9.2, y - 19.2)
        rings = 0.86 + 0.14 * np.sin(r * 6)
        return (col("#FFF0B0") * (1 - r[..., None] / 7) + col("#F09A18") * (r[..., None] / 7)) * rings[..., None]

    s.ellipse(10, 20, 4.8, 4.8, glass("#FFD870", 0.5), bevel=4.6, slope=0.4, paint=lens, cast=0.6)
    s.poly(ell(8.2, 17.8, 1.4, 0.9, -38), dict(c="#FFFFFF", metal=0, gloss=20, spec=0, mott=0, tint=0, emis=1.0), prof="flat", cast=0)
    s.rivets([(4.6, 12.4), (15.4, 12.4), (4.6, 28.4), (15.4, 28.4)], 0.6, PALE_GOLD)
    s.poly([(2.4, 31), (17.6, 31), (18.6, 33.6), (17.4, 36.4), (2.6, 36.4), (1.4, 33.6)], M(BRASS, c="#A87830"), bevel=2.5, slope=0.5, thick=(0.5, 0.5))
    s.groove([(1.8, 33.4), (18.2, 33.4)], w=0.5, depth=0.5)


def icon_ward_lantern(s):
    """Pointed iron cage round a cold blue ward flame."""
    iron = M(DARK_STEEL, c="#7B828D")
    s.ring(10, 1.6, 1.8, 1.8, 0.7, iron, bevel=0.35)
    s.poly([(10, 2.6), (10.8, 5.4), (9.2, 5.4)], STEEL, bevel=0.7, prof="chamfer")
    s.poly([(10, 4.6), (16.6, 12.2), (3.4, 12.2)], iron, bevel=3.5, prof="chamfer", slope=0.6, thick=(0.4, 0.5))
    s.poly([(2, 12), (18, 12), (16.6, 14), (3.4, 14)], STEEL, bevel=1.0, prof="chamfer", slope=0.9, thick=(0.3, 0.4))

    def pane(x, y):
        r = np.hypot((x - 10) / 6, (y - 23) / 9)
        t = np.clip(r, 0, 1)[..., None]
        return col("#BDF4FF") * (1 - t) + col("#123E78") * t

    s.poly([(4.2, 14), (15.8, 14), (15.4, 32), (4.6, 32)], glass("#3C9CE0", 0.55), bevel=1.0, prof="flat", paint=pane)
    flame(s, 10, 31, 8.6, 15, "#1C74D8", "#58D0F4", "#EFFFFF")
    for x0, x1 in ((4.2, 4.6), (10, 10), (15.8, 15.4)):
        s.stroke([(x0, 13.6), (x1, 32.4)], 1.5, iron, cast=0.7)
    s.stroke([(4.2, 23), (15.8, 23)], 1.0, iron, cast=0.6)
    s.poly([(10, 19.8), (12.3, 23), (10, 26.2), (7.7, 23)], STEEL, bevel=1.5, prof="chamfer", slope=0.9, thick=(0.2, 0.3))
    s.poly([(2.8, 32), (17.2, 32), (15.6, 34.6), (4.4, 34.6)], STEEL, bevel=1.0, prof="chamfer", slope=0.9, thick=(0.3, 0.4))
    s.poly([(5, 34.6), (15, 34.6), (16.4, 38), (3.6, 38)], iron, bevel=1.4, prof="chamfer", slope=0.8, thick=(0.3, 0.4))


def icon_torch(s):
    s.place(-10, 1.0, (10, 20))
    s.stroke([(10, 15), (10, 40)], 3.6, WOOD, width_end=2.2, bump=(1.2, 0.3))
    s.stroke([(10, 37.6), (10, 39.8)], 3.3, IRON)
    # tarred cloth wrapped round the head
    s.poly(cr([(5.8, 12.6), (10, 11), (14.2, 12.6), (14.6, 16), (12.6, 21), (7.4, 21), (5.4, 16)], 5, True), M(LEATHER, c="#5A4632", mott=0.5), bevel=4, prof="pillow", slope=0.7)
    for y0, y1 in ((13, 15.8), (15.2, 18), (17.6, 20.2)):
        s.groove([(5.6, y0), (14.6, y1)], w=0.8, depth=0.7, dark=0.45)
    s.poly([(7.6, 20), (12.4, 20), (12, 23.4), (8, 23.4)], IRON, bevel=1.2, thick=(0.2, 0.3))
    s.rivets([(10, 21.6)], 0.5, STEEL)
    flame(s, 10, 15, 12.2, 15.5)


# 2x2 ----------------------------------------------------------------------------------


def icon_leather_cap(s):
    s.ellipse(20, 26, 12.5, 5.2, M(DARK_LEATHER, c="#1A0F07"), bevel=1)
    dome = cr([(7, 25.5), (7.4, 15), (12, 7.6), (20, 4.8), (28, 7.6), (32.6, 15), (33, 25.5), (28, 29), (20, 30), (12, 29)], 6, True)
    s.poly(dome, LEATHER, bevel=13, prof="round", slope=0.72, bump=(0.8, 0.35), thick=(0.5, 0.7))
    # panel seams with saddle stitching
    for pts in (cr([(20, 5), (15, 12), (13.6, 20), (14.4, 29)], 5), cr([(20, 5), (25, 12), (26.4, 20), (25.6, 29)], 5), [(20, 4.9), (20, 30)]):
        s.groove(pts, w=0.6, depth=0.5)
        s.groove(shift(pts, 0.9, 0), w=0.35, depth=0.45, dark=0.55, dash=(0.7, 0.55))
    # turned-up band
    band = cr([(5.8, 23), (12, 25.6), (20, 26.6), (28, 25.6), (34.2, 23)], 6) + cr([(35, 29.4), (28, 33), (20, 34.2), (12, 33), (5, 29.4)], 6)
    s.poly(band, M(LEATHER, c="#9C6631"), bevel=1.8, thick=(0.4, 0.6), bump=(0.8, 0.3), cast=0.75)
    s.groove(cr([(6.4, 24.8), (12, 27.3), (20, 28.2), (28, 27.3), (33.8, 24.8)], 6), w=0.32, depth=0.4, dark=0.6, dash=(0.8, 0.55))
    s.groove(cr([(6, 28.4), (12, 31.5), (20, 32.6), (28, 31.5), (34.2, 28.4)], 6), w=0.32, depth=0.4, dark=0.6, dash=(0.8, 0.55))
    # brass stud at the crown and a buckle tab on the band
    s.ellipse(20, 5.6, 1.5, 1.2, BRASS, bevel=1.1)
    s.poly([(17, 27.4), (23, 27.4), (23, 32.8), (17, 32.8)], BRASS, bevel=0.9, thick=(0.3, 0.4))
    s.poly([(18.3, 28.7), (21.7, 28.7), (21.7, 31.5), (18.3, 31.5)], M(DARK_LEATHER, c="#2A180B"), prof="flat", cast=0.5)


def torso(cx=20.0, top=3.0, bottom=37.0, sh=13.0, waist=9.5):
    """Right half of a chest outline, neck to hem."""
    return [(cx + 4.2, top), (cx + sh - 1.2, top + 2.4), (cx + sh, top + 6.5), (cx + waist + 0.8, top + 12), (cx + waist, top + 19),
            (cx + waist + 1.2, bottom - 4), (cx + waist + 2.2, bottom - 0.4), (cx + 5, bottom + 0.2), (cx, bottom + 0.2)]


def icon_leather_vest(s):
    half = cr(torso(top=3.4, bottom=36.6, sh=14, waist=10.5), 4)
    outline = half + mirror(half, 20)[::-1] + [(15.8, 3.4), (20, 16.5), (24.2, 3.4)]
    s.poly(outline, LEATHER, bevel=9.5, prof="pillow", slope=0.58, bump=(0.8, 0.35), thick=(0.6, 0.7))
    # opening down the chest, dark shirt behind
    s.poly([(20, 15.5), (22, 37), (18, 37)], M(DARK_LEATHER, c="#20130A"), prof="flat", cast=0.3)
    # collar facings
    for side in (1, -1):
        c = [(20 + side * 4.3, 3.2), (20 + side * 8, 4.6), (20 + side * 2.6, 19), (20 + side * 0.6, 16.4)]
        s.poly(c, M(LEATHER, c="#A06A36"), bevel=1.2, thick=(0.3, 0.5), bump=(0.8, 0.2), cast=0.7)
    # lacing
    ys = [19.5, 23, 26.5, 30, 33.5]
    for a, b in zip(ys, ys[1:]):
        s.stroke([(17.4, a), (22.6, b)], 0.75, CORD, cast=0.5)
        s.stroke([(22.6, a), (17.4, b)], 0.75, CORD, cast=0.5)
    s.rivets([(x, y) for y in ys for x in (17.1, 22.9)], 0.55, BRASS)
    # hem and arm stitching, chest plates
    s.groove(cr([(30.4, 8.6), (29, 15), (28.8, 22)], 5), w=0.3, depth=0.4, dark=0.55, dash=(0.8, 0.55), clip=False)
    s.groove(cr([(9.6, 8.6), (11, 15), (11.2, 22)], 5), w=0.3, depth=0.4, dark=0.55, dash=(0.8, 0.55), clip=False)
    s.groove([(9.4, 33.2), (15.8, 34), (24.2, 34), (30.6, 33.2)], w=0.3, depth=0.4, dark=0.55, dash=(0.8, 0.55), clip=False)
    for side in (1, -1):
        s.groove([(20 + side * 5, 23), (20 + side * 10, 23)], w=0.5, depth=0.5, clip=False)
        s.poly([(20 + side * 5.2, 26), (20 + side * 9.8, 26), (20 + side * 9.8, 31), (20 + side * 5.2, 31)], M(LEATHER, c="#8B5A2C"), bevel=0.9, thick=(0.2, 0.3), cast=0.6)


def icon_armor_bone(s):
    under = sym(cr(torso(top=5, bottom=36.5, sh=12.5, waist=9.5), 4), 20)
    s.poly(under, M(BONE, c="#B5A98F", mott=0.3), bevel=8, prof="pillow", slope=0.5, thick=(0.5, 0.7))
    # sternum and ribs
    for i, y in enumerate((11.5, 15.2, 18.9, 22.6, 26.2)):
        w = 10.2 - i * 0.55
        for side in (1, -1):
            rib = cr([(20 + side * 1, y), (20 + side * w * 0.55, y - 0.6), (20 + side * w, y + 2.4), (20 + side * w * 0.96, y + 4)], 5)
            s.stroke(rib, 2.5, BONE, width_end=1.2, cast=0.8)
    s.stroke([(20, 8.5), (20, 31)], 3.0, M(BONE, c="#F4ECD6"), width_end=2.0, cast=0.8)
    for y in (12, 16, 20, 24, 28):
        s.groove([(18.5, y), (21.5, y)], w=0.35, depth=0.4, dark=0.6, clip=False)
    # pelvis plate
    s.poly([(12.8, 31), (27.2, 31), (29, 37), (24, 36.2), (20, 38.2), (16, 36.2), (11, 37)], BONE, bevel=2.2, thick=(0.4, 0.6))
    # shoulder horns: the left one overshoots the footprint a little, like a model's bounding box would
    for flip, reach in ((False, 39.4), (True, 39.4)):
        f = (lambda p: mirror(p, 20)) if flip else (lambda p: p)
        s.poly(f(cr([(25, 7), (29, 3.2), (34, 4.6), (35.6, 9.6), (33, 13), (28, 12.4)], 5, True)), BONE, bevel=4.8, slope=0.65, thick=(0.4, 0.6))
        s.poly(f([(32, 4.4), (reach, -0.8), (35.4, 8.4)]), M(BONE, c="#FBF6E6"), bevel=2.2, prof="chamfer", slope=0.8, thick=(0.3, 0.4))
    # collar bones tie the shoulders to the sternum
    for side in (1, -1):
        s.stroke(cr([(20, 8.6), (20 + side * 4.6, 7.2), (20 + side * 9, 8.4)], 5), 2.3, M(BONE, c="#F6EFDC"), width_end=1.6, cast=0.8)


def gilded(**kw) -> dict:
    return M(GOLD, c="#FFB81C", spec=1.4, **kw)


def icon_helm_gilded(s):
    s.place(-6, 0.98, (20, 20))
    # tall thin crest sweeping up and back
    crest = cr([(21.5, 9), (23.5, 3), (29, -0.6), (36.6, -3.4), (33, -0.2), (29.2, 4), (26.6, 9.6)], 6)
    s.poly(crest, gilded(), bevel=1.8, prof="chamfer", slope=0.9, thick=(0.3, 0.4))
    # bowl
    bowl = cr([(8, 24), (8.2, 14), (13, 7), (20, 5), (27, 7), (31.8, 14), (32, 24), (28, 30), (20, 31.6), (12, 30)], 6, True)
    s.poly(bowl, gilded(), bevel=12, prof="round", slope=0.7, thick=(0.5, 0.7))
    # brow band and nasal
    s.poly(cr([(7.6, 15.6), (13, 13.4), (20, 12.8), (27, 13.4), (32.4, 15.6)], 6) + cr([(32.6, 19), (27, 17), (20, 16.4), (13, 17), (7.4, 19)], 6), M(GOLD, c="#FFD052"), bevel=1.4, thick=(0.3, 0.5))
    # visor: eye slits and a breath plate
    s.poly([(10, 18.4), (17.8, 17.6), (18.6, 20.6), (11, 21.4)], M(BLACK, c="#1C0E04"), prof="flat", cast=0.3)
    s.poly([(30, 18.4), (22.2, 17.6), (21.4, 20.6), (29, 21.4)], M(BLACK, c="#1C0E04"), prof="flat", cast=0.3)
    s.poly([(18.2, 15.8), (21.8, 15.8), (22.4, 26), (20, 28.4), (17.6, 26)], M(GOLD, c="#FFD052"), bevel=1.8, prof="chamfer", slope=0.9, thick=(0.3, 0.5))
    for side in (1, -1):
        cheek = [(20 + side * 4, 22), (20 + side * 12.2, 21), (20 + side * 11.4, 30), (20 + side * 6.6, 35.5), (20 + side * 3.4, 30)]
        s.poly(cheek, gilded(), bevel=3.2, prof="chamfer", slope=0.7, thick=(0.4, 0.6), cast=0.7)
        s.groove([(20 + side * 6, 25), (20 + side * 9.6, 24.6)], w=0.5, depth=0.6, dark=0.4)
        s.groove([(20 + side * 6, 27.6), (20 + side * 9.2, 27.2)], w=0.5, depth=0.6, dark=0.4)
    s.groove(cr([(20, 5.2), (20, 12.8)], 2), w=0.8, depth=-0.7, clip=False)
    s.rivets([(9.6, 16.8), (30.4, 16.8)], 0.8, PALE_GOLD)


def icon_armor_gilded(s):
    def lower(x, y):
        t = smooth(20, 36, y)[..., None]
        return col("#FFB81C") * (1 - t) + col("#D0761C") * t

    half = cr(torso(top=5.4, bottom=33, sh=11.5, waist=8.6), 4)
    # tassets first, under the cuirass
    for x, w, b in ((12.4, 5, 38), (17.2, 5.6, 39.2), (22.8, 5.6, 39.2), (27.6, 5, 38)):
        s.poly([(x - w / 2, 30), (x + w / 2, 30), (x + w / 2 - 0.5, b - 1.8), (x, b), (x - w / 2 + 0.5, b - 1.8)], M(GOLD, c="#D88A20"), bevel=1.8, prof="chamfer", slope=0.7, thick=(0.3, 0.5))
    s.poly(sym(half, 20), gilded(), bevel=9.5, prof="round", slope=0.62, paint=lower, thick=(0.5, 0.7))
    s.groove([(20, 8.4), (20, 28)], w=0.9, depth=-0.7)
    s.groove(cr([(20, 19), (14, 18.6), (11, 14.4)], 5), w=0.7, depth=0.55, dark=0.55)
    s.groove(cr([(20, 19), (26, 18.6), (29, 14.4)], 5), w=0.7, depth=0.55, dark=0.55)
    s.groove(cr([(11.6, 23.2), (20, 25), (28.4, 23.2)], 5), w=0.6, depth=0.5, dark=0.55)
    s.groove(cr([(11.6, 27), (20, 28.8), (28.4, 27)], 5), w=0.6, depth=0.5, dark=0.55)
    s.poly([(10.6, 30.4), (29.4, 30.4), (29.2, 33), (10.8, 33)], M(GOLD, c="#FFD052"), bevel=1.3, thick=(0.3, 0.5), cast=0.6)
    s.poly([(17.4, 29.8), (22.6, 29.8), (22.6, 33.6), (17.4, 33.6)], PALE_GOLD, bevel=1.4, prof="chamfer", thick=(0.2, 0.3))
    s.cut(20, 31.7, 1.5, 1.5, gemstone("#B41818", 0.15), n=6)
    # gorget
    s.poly(cr([(13, 7.2), (20, 10.6), (27, 7.2), (25.4, 4), (20, 5.6), (14.6, 4)], 5, True), M(GOLD, c="#FFD052"), bevel=2, thick=(0.3, 0.5), cast=0.7)
    s.poly(ell(20, 5.2, 4.8, 1.7), M(BLACK, c="#2A1404"), prof="flat", cast=0.3)
    # pauldrons
    for side in (1, -1):
        cx = 20 + side * 12.6
        for k, dy in ((1.0, 4.4), (0.9, 1.8)):
            s.ellipse(cx, 8.8 + dy, 7.0 * k, 4.8 * k, gilded(), bevel=4.4, deg=side * 18, thick=(0.4, 0.5), cast=0.7)
        s.ellipse(cx, 7.8, 7.4, 5.2, M(GOLD, c="#FFCB40"), bevel=5, deg=side * 12, thick=(0.4, 0.6), cast=0.75)
        s.rivets([(cx - 3.4, 7.2), (cx, 5.4), (cx + 3.4, 7.2)], 0.65, PALE_GOLD)


def icon_legs_verdant(s):
    def cloth(x, y):
        n = s.noise(2.5)
        xi = np.clip(((x + MARGIN) * PPU).astype(int), 0, n.shape[1] - 1)
        yi = np.clip(((y + MARGIN) * PPU).astype(int), 0, n.shape[0] - 1)
        v = smooth(0.46, 0.72, n[yi, xi])[..., None] * 0.8
        return col("#17331C") * (1 - v) + col("#A9C09A") * v

    mat = M(CLOTH, c="#1E3A22", metal=0.15, gloss=60, spec=0.9, mott=0.1)
    leg = cr([(20.8, 9), (30.4, 7), (32, 15), (31, 24), (29.2, 34), (23.6, 34.4), (22.8, 25), (20.9, 17)], 5, True)
    for flip in (False, True):
        pts = rot(leg, -7, (22, 8))
        s.poly(mirror(pts, 20) if flip else pts, mat, bevel=5.0, prof="round", slope=0.85, paint=cloth, thick=(0.4, 0.6))
    s.poly(cr([(9.6, 5), (20, 5.6), (30.4, 5), (31, 11), (25, 14), (20, 17), (15, 14), (9, 11)], 5, True), mat, bevel=6, slope=0.6, paint=cloth, thick=(0.3, 0.5))
    s.groove(cr([(20, 8), (20, 17)], 2), w=0.6, depth=0.6, dark=0.5)
    s.poly([(9, 3.2), (31, 3.2), (31.6, 7.6), (8.4, 7.6)], DARK_LEATHER, bevel=1.5, thick=(0.3, 0.5), cast=0.7)
    s.poly([(17, 2.4), (23, 2.4), (23.4, 8.4), (16.6, 8.4)], PALE_GOLD, bevel=1.5, prof="chamfer", slope=0.8, thick=(0.3, 0.4))
    s.cut(20, 5.4, 1.5, 1.5, gemstone("#2FA25C", 0.2), n=6)
    pale = M(STEEL, c="#DCE4CC", mott=0.2)
    for side in (1, -1):
        s.ellipse(20 + side * 7.8, 21.6, 3.4, 3.0, pale, bevel=2.8, thick=(0.3, 0.4), cast=0.7)
        cx = 20 + side * 10.4
        q = [(cx - 3.6, 30.4), (cx + 3.6, 30.4), (cx + 3.9, 36), (cx + 4.6, 38.8), (cx - 4.6, 38.8), (cx - 3.9, 36)]
        s.poly(q, pale, bevel=2.4, slope=0.7, thick=(0.4, 0.5), cast=0.7)
        s.groove([(cx - 3.7, 33.4), (cx + 3.7, 33.4)], w=0.5, depth=0.5)


def gauntlet(s, c_deg, dx, dy):
    s.place(c_deg, 0.92, (20, 20))
    o = lambda p: shift(p, dx, dy)  # noqa: E731
    # fingers
    for i, (x, tip) in enumerate(((13.2, 11.6), (17.4, 8.2), (21.6, 8.8), (25.6, 12.2))):
        pts = o([(x, 21), (x, tip)])
        s.stroke(pts, 3.5, STEEL, cast=0.7)
        for y in np.linspace(tip + 2.6, 19, 3):
            s.groove(o([(x - 1.8, y), (x + 1.8, y)]), w=0.45, depth=0.6, dark=0.45)
    # thumb
    s.stroke(o(cr([(10.8, 27), (7, 23), (6, 17.5)], 4)), 4.0, STEEL, width_end=2.8, cast=0.7)
    s.groove(o([(5, 20.6), (9, 21.6)]), w=0.45, depth=0.6, dark=0.45)
    # knuckle guard, back plate, wrist lames, flared cuff
    s.poly(o(cr([(11.2, 19.4), (20, 18), (28, 19.6), (28.6, 23), (20, 22.4), (11, 23.8)], 4, True)), DARK_STEEL, bevel=1.8, thick=(0.3, 0.5), cast=0.7)
    s.rivets(o([(14.2, 20.8), (19.6, 20.2), (25, 20.8)]), 0.72, GOLD)
    s.poly(o([(11, 23.8), (28.4, 23), (27.4, 31), (12.6, 31.6)]), STEEL, bevel=7, prof="chamfer", slope=0.42, thick=(0.4, 0.6), cast=0.7)
    s.poly(o([(12, 31), (27.8, 30.4), (27.2, 33), (12.6, 33.6)]), DARK_STEEL, bevel=1.2, thick=(0.3, 0.4), cast=0.7)
    s.poly(o([(11.6, 33.4), (28.2, 32.8), (31, 41), (9.4, 41.6)]), STEEL, bevel=2.2, prof="round", slope=0.6, thick=(0.4, 0.6), cast=0.7)
    s.poly(o([(9.2, 40.6), (31.2, 40), (31.4, 41.8), (9, 42.4)]), GOLD, bevel=0.8, cast=0.5)


def icon_gloves_plate(s):
    gauntlet(s, 24, 7.6, -1.2)
    gauntlet(s, -14, -6.4, 0.4)


def sabaton(s, dx, dy, shade=1.0):
    """One plate boot in side profile, toe to the left."""
    o = lambda p: shift(p, dx, dy)  # noqa: E731
    st = M(STEEL, c=col("#98A0AB") * shade)
    dk = M(DARK_STEEL, c=col(DARK_STEEL["c"]) * shade)
    sole = M(BLACK, c=col("#2C2824") * shade, metal=0.2)
    # heel block and sole under everything
    s.poly(o([(18, 34.6), (26.4, 34.6), (26.6, 39), (19, 39)]), sole, bevel=0.8, thick=(0.2, 0.4))
    s.poly(o([(-2.6, 35), (19, 35), (19, 37.4), (-1, 37.2)]), sole, bevel=0.7, thick=(0.2, 0.4))
    # greave: narrow at the ankle, swelling over the calf
    shaft = cr([(11.4, 3.4), (23.4, 2.6), (25, 12), (23.2, 23), (25.8, 31), (25.8, 35.6), (13, 35.6), (12.6, 23), (12.4, 12)], 5, True)
    s.poly(o(shaft), st, bevel=6, slope=0.55, thick=(0.4, 0.5))
    # one smooth foot shell ending in a pointed toe; lame lines are cut into it, not stacked on it
    foot = cr([(15.6, 21), (9.4, 25.6), (3.6, 29.8), (-1.2, 32.8), (-3.2, 34.6), (-1, 36), (15.8, 36)], 6, True)
    s.poly(o(foot), st, bevel=5.4, slope=0.6, thick=(0.3, 0.4), cast=0.75)
    for pts in ([(10.8, 25), (12.4, 30), (11.6, 36)], [(6.2, 28), (7.8, 32), (7.2, 36)], [(2.2, 30.8), (3.4, 33.4), (3, 36)]):
        s.groove(o(cr(pts, 4)), w=0.55, depth=0.55, dark=0.45)
    # ankle lames, cuff and pivot
    s.groove(o(cr([(12.6, 19.4), (18, 20.4), (23.6, 19.4)], 4)), w=0.6, depth=0.55, dark=0.45, clip=False)
    s.groove(o(cr([(12.4, 11.6), (18, 12.8), (24.6, 11.6)], 4)), w=0.6, depth=0.55, dark=0.45, clip=False)
    cuff = [(10.2, 1.8), (24.6, 0.4), (25.2, 5.6), (11, 7)]
    s.poly(o(cuff), dk, bevel=1.5, thick=(0.3, 0.4), cast=0.7)
    s.poly(o([(10.1, 1.1), (24.6, -0.3), (24.7, 1.1), (10.2, 2.5)]), GOLD, bevel=0.6, cast=0.4)
    s.ellipse(*o([(18.6, 26.4)])[0], 2.2, 2.2, GOLD, bevel=1.8, thick=(0.2, 0.3), cast=0.7)


def icon_boots_plate(s):
    s.place(0, 0.9, (20, 20))
    sabaton(s, 11.5, -2.8, 0.68)
    sabaton(s, -1.5, 1.6)


def icon_pet_wisp(s):
    """A dim captive flame: pale core, dark streaming tail, no face."""
    # curves meet at hard tips, so the tail streams up and right in three tongues
    outline = cr([(5, 28), (6, 19), (12, 12), (19, 8), (30, 0)], 5) + cr([(30, 0), (27, 8), (26, 12), (38, 9)], 5)[1:]
    outline += cr([(38, 9), (32, 15), (30, 19), (37, 23)], 5)[1:] + cr([(37, 23), (29, 27), (25, 33), (16, 37), (8, 35), (5, 28)], 5)[1:-1]
    head = (14.0, 26.0)

    def layer(k, c0, c1, emis):
        def paint(x, y):
            t = np.clip(np.hypot(x - head[0], y - head[1]) / (30 * k), 0, 1)[..., None]
            return col(c0) * (1 - t) + col(c1) * t

        pts = [(head[0] + (x - head[0]) * k, head[1] + (y - head[1]) * k) for x, y in outline]
        mat = dict(c=c0, metal=0.1, gloss=60, spec=0.35, mott=0.08, tint=0.5)
        s.poly(pts, mat, bevel=9 * k, prof="pillow", slope=0.45, paint=paint, emis=emis, cast=0.15)

    layer(1.0, "#155C86", "#06162C", 0.35)
    layer(0.66, "#3BA6BE", "#0E4468", 0.5)
    layer(0.34, "#C8F0EC", "#3F9CB0", 0.62)


# 2x4 ----------------------------------------------------------------------------------


def icon_bow_verdant(s):
    s.place(-13, 1.0, (20, 40))
    rust = M(BRONZE, c="#A23A1C", gloss=50)
    green = M(BONE, c="#4E8A3E", metal=0.4, gloss=70, spec=0.9, mott=0.25, tint=0.5)
    upper = cr([(11.5, 33), (10, 22), (14, 11), (22, 4), (29, -2)], 7)
    lower = cr([(11.5, 47), (10, 58), (14, 69), (22, 76), (29, 82)], 7)
    # string first: it sits behind the limbs
    s.stroke([(29, -2), (29, 82)], 0.55, M(CORD, c="#E4DCC6"), cast=0, blur=0.5)
    for limb in (upper, lower):
        s.stroke(limb, 5.6, green, width_end=2.0)
        n = len(limb)
        s.stroke(limb[int(n * 0.68) :], 3.0, rust, width_end=1.4, cast=0.7)
        for j in range(3):
            i = int(n * (0.15 + j * 0.2))
            x, y = limb[i]
            q = [(x - 1.6, y - 2.2), (x - 6.2, y + (1 if y < 40 else -1) * 1.4), (x - 1.6, y + 2.2)]
            s.poly(q, rust, bevel=1.6, prof="chamfer", slope=0.8, thick=(0.2, 0.3))
        s.stroke(limb[: int(n * 0.66)], 1.1, M(BONE, c="#A8D07A", spec=0.8), cast=0.3)
        for t in (0.22, 0.42, 0.6):
            x, y = limb[int(n * t)]
            stud(s, x, y, 1.05, DARK_STEEL)
    # riser with glowing core
    s.stroke([(11.5, 31), (11.5, 49)], 5.4, DARK_WOOD, cast=0.7)
    for y in (33, 47):
        s.stroke([(8.5, y), (14.5, y)], 1.3, PALE_GOLD, cast=0.6)

    def glow(x, y):
        return 0.8 * np.exp(-(np.hypot(x - 11.5, y - 40) / 4) ** 2)

    s.cut(11.5, 40, 3.2, 5.2, gemstone("#58E048"), n=8, glow=glow)


def icon_spear_crimson(s):
    s.place(-14, 0.96, (20, 40))
    shaft = M(DARK_WOOD, c="#6E1414", gloss=70, spec=0.7)
    s.stroke([(20, 16), (20, 79)], 2.0, shaft, width_end=1.5)
    for y in (30, 46, 62):
        s.stroke([(20, y), (20, y + 1.6)], 2.8, M(GOLD, c="#E09A30"))
    s.poly([(18.9, 78), (21.1, 78), (20, 83.5)], STEEL, bevel=1.1, prof="chamfer", slope=0.9)
    crim = M(STEEL, c="#561017", gloss=110, spec=1.1, tint=0.6)
    half = cr([(20, -6), (23, -1), (27.2, 8), (27.4, 12.5), (23, 18), (21.2, 22)], 6)
    s.poly(sym(half, 20), crim, bevel=7.2, prof="chamfer", slope=0.6, thick=(0.5, 0.6))
    # gold-red filigree along the blade
    fil = M(GOLD, c="#E8903A")
    for side in (1, -1):
        s.groove(cr([(20 + side * 2.2, 15), (20 + side * 4.6, 9), (20 + side * 2.4, 3.4)], 5), w=0.7, depth=-0.55, clip=False)
        s.groove(cr([(20 + side * 3.8, 13), (20 + side * 5.2, 11.4), (20 + side * 3.6, 10)], 4), w=0.5, depth=-0.5, clip=False)
    for side in (1, -1):
        wing = [(20 + side * 1, 20), (20 + side * 8, 17), (20 + side * 10.4, 12.2), (20 + side * 6.6, 23), (20 + side * 1, 25.6)]
        s.poly(wing, fil, bevel=2.2, prof="chamfer", slope=0.8, thick=(0.3, 0.4))
    s.poly([(17.8, 18.4), (22.2, 18.4), (23, 22), (21.6, 27), (18.4, 27), (17, 22)], M(GOLD, c="#F0B040"), bevel=2, prof="chamfer", slope=0.9, thick=(0.3, 0.4))
    s.cut(20, 21.8, 1.7, 2.3, gemstone("#E01818", 0.3), n=6)


# 3x2 ----------------------------------------------------------------------------------


def icon_wings_gossamer(s):
    root = (30.0, 19.0)

    def membrane(x, y):
        r = np.hypot(x - root[0], y - root[1]) / 30
        t = np.clip(r, 0, 1)[..., None]
        return col("#AEF6FF") * (1 - t) ** 2 + col("#6C909F") * 2 * t * (1 - t) + col("#3E4F58") * t * t

    def clarity(x, y):
        r = np.hypot(x - root[0], y - root[1]) / 30
        a = np.arctan2(y - root[1], np.abs(x - root[0]))
        cells = 0.5 + 0.5 * np.cos(a * 13) * np.cos(r * 16)
        return np.clip(0.80 - 0.42 * r + 0.14 * (cells - 0.5), 0.3, 0.95)

    mem = M(BONE, c="#6C909F", metal=0.55, gloss=120, spec=0.9, mott=0.06, tint=0.5)
    vein = M(BONE, c="#D8F4F8", spec=0.6, metal=0.2, gloss=60)
    upper = cr([(31, 19), (35, 9), (44, 1.6), (55, 0.4), (59, 4.4), (56.6, 12), (48.6, 19), (39, 22.4)], 6, True)
    lower = cr([(31.4, 21), (40, 23), (48.4, 24), (51.4, 30), (47, 37.4), (39.4, 38.4), (33.8, 31)], 6, True)
    for flip in (False, True):
        f = (lambda p: mirror(p, 30)) if flip else (lambda p: p)
        s.poly(f(lower), mem, bevel=4, prof="pillow", slope=0.35, paint=membrane, alpha=clarity, cast=0, emis=lambda x, y: 0.5 * np.exp(-(np.hypot(x - root[0], y - root[1]) / 12) ** 2))
        s.poly(f(upper), mem, bevel=4, prof="pillow", slope=0.35, paint=membrane, alpha=clarity, cast=0, emis=lambda x, y: 0.5 * np.exp(-(np.hypot(x - root[0], y - root[1]) / 12) ** 2))
        ribs_u = [[(31, 19), (38, 10), (50, 1.4)], [(31, 19), (43, 10), (57.6, 3)], [(31, 19.5), (46, 12), (58, 8)], [(31, 20), (44, 17), (53, 14.4)]]
        ribs_l = [[(32, 21), (42, 27), (49.6, 25.6)], [(32, 21.5), (41, 30), (49, 34)], [(32, 22), (37.6, 31), (41, 38)]]
        for rib in ribs_u + ribs_l:
            s.stroke(f(cr(rib, 6)), 0.75, vein, width_end=0.3, cast=0)
        s.stroke(f(upper + upper[:1]), 0.55, vein, cast=0)
        s.stroke(f(lower + lower[:1]), 0.5, vein, cast=0)
    s.stroke([(30, 12.6), (30, 29)], 2.6, M(STEEL, c="#D9F6FA"), width_end=1.0, cast=0.2)
    s.ellipse(30, 18.8, 2.2, 3.2, gemstone("#7CE8F4", 0.5), bevel=2, cast=0.1)


def icon_cape_navy(s):
    s.place(0, 1.0, (30, 20))
    navy = M(CLOTH, c="#0A233F", metal=0.25, gloss=30, spec=0.3, mott=0.2)
    half = [(30, 2), (43, 3.4), (46, 12), (47.2, 24), (47.6, 36), (44, 39.4), (40, 37.4), (35.4, 40), (30, 37.6)]
    half = cr(half, 3)
    outline = sym(half, 30)

    def fold(x, y):
        v = 0.5 + 0.5 * np.sin((x - 30) * 0.92 + np.sin(y * 0.14) * 1.2)
        v = (v ** 1.8)[..., None]
        return col("#0A1F52") * (1 - v) + col("#2860B0") * v

    s.poly(outline, navy, bevel=4.5, prof="pillow", slope=0.8, paint=fold, thick=(0.6, 0.8))
    for x, y0 in ((21.4, 12), (26, 10), (34, 10), (38.6, 12)):
        s.groove(cr([(x, y0), (x + 0.8, 24), (x - 0.8, 37)], 5), w=1.8, depth=0.6, dark=0.72)
    trim = M(GOLD, c="#D9A638", mott=0.14)
    s.stroke(half[6:], 1.5, trim, cast=0.4)
    s.stroke(mirror(half[6:], 30), 1.5, trim, cast=0.4)
    # yoke, clasp, crest
    yoke = cr([(14.4, 3.6), (20, 0.6), (30, -0.6), (40, 0.6), (45.6, 3.6), (43, 8.6), (30, 11.4), (17, 8.6)], 5, True)
    s.poly(yoke, M(CLOTH, c="#14365C", metal=0.3, gloss=40, spec=0.4), bevel=4, slope=0.55, thick=(0.3, 0.6), cast=0.8)
    s.stroke(cr([(16.2, 8.2), (30, 11.6), (43.8, 8.2)], 6), 1.3, trim, cast=0.5)
    s.stroke(cr([(14.8, 3.4), (20, 0.8), (30, -0.4), (40, 0.8), (45.2, 3.4)], 6), 1.1, trim, cast=0.3)
    for x in (18, 42):
        s.ellipse(x, 4.2, 2.3, 2.3, GOLD, bevel=2, thick=(0.3, 0.4))
        s.cut(x, 4.2, 1.2, 1.2, gemstone("#B41818", 0.2), n=6)
    s.poly([(30, 13.6), (35, 17), (33.8, 23), (30, 27), (26.2, 23), (25, 17)], GOLD, bevel=2.8, prof="chamfer", slope=0.7, thick=(0.3, 0.5))
    s.poly([(30, 16.4), (32.2, 18.2), (31.6, 21.8), (30, 23.6), (28.4, 21.8), (27.8, 18.2)], M(BLACK, c="#0A233F"), prof="flat", cast=0.4)
    # small gold feet at the hem
    for x, y in ((13.4, 38.4), (20.4, 37.2), (39.6, 37.2), (46.6, 38.4)):
        s.poly([(x - 1.5, y - 1.2), (x + 1.5, y - 1.2), (x, y + 1.8)], GOLD, bevel=1, prof="chamfer", slope=0.8, cast=0.2)


icon_cape_navy.top = 3.0


def potion_flask(s, deep: str, mid: str, glow: str):
    """Round-bellied vial of dark glass and tinted liquid, held by black-iron straps, a seated cork under a wax seal."""
    liquid = lambda x, y: col(deep) + (col(mid) - col(deep)) * np.clip(1 - np.hypot(x - 8.6, (y - 12.6) * 0.9) / 6.5, 0, 1)[..., None]  # noqa: E731
    vessel = dict(c=deep, metal=0.15, gloss=170, spec=1.8, mott=0.03, tint=0.25, emis=0.22)
    # neck and lip first, the bulb overlaps the neck's foot
    s.poly([(8.1, 3.6), (11.9, 3.6), (12.2, 9.0), (7.8, 9.0)], vessel, bevel=1.7, prof="round", slope=0.6, paint=liquid, thick=(0.3, 0.4))
    s.ellipse(10, 13.6, 6.8, 6.4, vessel, bevel=5.6, prof="round", slope=0.75, paint=liquid, thick=(0.4, 0.5))
    # a cold glint on the upper left shoulder of the glass
    s.poly(ell(7.2, 11.4, 1.0, 2.1, 28), dict(c=glow, metal=0, gloss=20, spec=0, mott=0, tint=0, emis=0.9), prof="flat", cast=0, alpha=0.8)
    # iron straps round the belly, riveted
    s.stroke(cr([(3.5, 14.6), (10, 16.6), (16.5, 14.6)], 6), 1.2, IRON, cast=0.6)
    s.rivets([(4.6, 14.9), (10, 16.4), (15.4, 14.9)], 0.45, DARK_STEEL)
    # collar
    s.poly([(7.2, 3.4), (12.8, 3.4), (13.0, 5.4), (7.0, 5.4)], IRON, bevel=0.8, prof="chamfer", slope=0.9, thick=(0.25, 0.3))
    s.groove([(7.2, 4.4), (12.8, 4.4)], w=0.3, depth=0.4)
    # cork and wax seal
    s.poly([(8.0, 0.8), (12.0, 0.8), (12.3, 3.6), (7.7, 3.6)], M(DARK_WOOD, c="#5A3A22", mott=0.5), bevel=1.2, prof="pillow", slope=0.7, thick=(0.25, 0.3))
    s.ellipse(10, 2.1, 1.5, 1.2, M(BLACK, c="#4A0C0C", metal=0.2, gloss=120, spec=1.0), bevel=0.6, cast=0.3)


def icon_potion_health(s):
    potion_flask(s, "#40080E", "#8E1C28", "#E0B8B8")


def icon_potion_mana(s):
    potion_flask(s, "#081640", "#224A94", "#B8CCE8")


# registry -----------------------------------------------------------------------------


# Sprites that carry a content key name today were drawn under another name. Scene seeds come from the name,
# so each keeps its original seed and the art is unchanged by the rename.
SEED_NAME = {
    "item.gear.iron-gauntlets": "item.gloves.plate",
    "item.gear.iron-boots": "item.boots.plate",
    "item.gear.plated-leggings": "item.legs.verdant",
    "item.gear.ward-pendant": "item.pendant.ward",
    "item.gear.iron-band": "item.ring.ice",
    "item.gear.travel-cloak": "item.cape.navy",
    "item.gear.gossamer-wings": "item.wings.gossamer",
    "item.gear.ember-wisp": "item.pet.wisp",
    "item.gear.hunting-spear": "item.spear.crimson",
}

ICONS: dict = {
    "item.relic.disc": (1, 1, icon_relic_disc),
    "item.gear.iron-band": (1, 1, icon_ring_ice),
    "item.gem.amber": (1, 1, icon_gem_amber),
    "item.gear.ward-pendant": (1, 1, icon_pendant_ward),
    "item.feather.azure": (1, 2, icon_feather_azure),
    "item.tool.survey-lantern": (1, 2, icon_survey_lantern),
    "item.gear.ward-lantern": (1, 2, icon_ward_lantern),
    "item.tool.torch": (1, 2, icon_torch),
    "item.potion.health": (1, 1, icon_potion_health),
    "item.potion.mana": (1, 1, icon_potion_mana),
    "item.gear.leather-cap": (2, 2, icon_leather_cap),
    "item.gear.leather-vest": (2, 2, icon_leather_vest),
    "item.armor.bone": (2, 2, icon_armor_bone),
    "item.helm.gilded": (2, 2, icon_helm_gilded),
    "item.armor.gilded": (2, 2, icon_armor_gilded),
    "item.gear.plated-leggings": (2, 2, icon_legs_verdant),
    "item.gear.iron-gauntlets": (2, 2, icon_gloves_plate),
    "item.gear.iron-boots": (2, 2, icon_boots_plate),
    "item.gear.ember-wisp": (2, 2, icon_pet_wisp),
    "item.bow.verdant": (2, 4, icon_bow_verdant),
    "item.gear.hunting-spear": (2, 4, icon_spear_crimson),
    "item.gear.gossamer-wings": (3, 2, icon_wings_gossamer),
    "item.gear.travel-cloak": (3, 2, icon_cape_navy),
    "item.tool.builder-hammer": (2, 3, icon_builder_hammer),
    "item.tool.stone-maul": (2, 3, icon_stone_maul),
    "item.tool.field-blade": (2, 3, icon_field_blade),
    "item.gear.iron-blade": (2, 3, icon_iron_blade),
    "item.axe.twinbit": (2, 3, icon_axe_twinbit),
    "item.shield.kite": (2, 3, icon_shield_kite),
}


# tiers, ramps, aura -------------------------------------------------------------------

RAMP_KEYS = {
    "ramp.rare": ["#DA8939", "#C68F79", "#8A6A7A", "#C68F79"],
    "ramp.enhanced": ["#FAB81A", "#FFE08A", "#FFF6DC", "#FFE08A"],
    "ramp.chrome": ["#3A3E46", "#C8D0DC", "#FFFFFF", "#8A90A0", "#22252B", "#8A90A0"],
}
UNCOMMON_TINT = "#F08FB0"
RARE_KEY, RARE_RIM = "axe.cycle", "#F1C385"
ENHANCED_CAP = 0.55


def luma(rgb: np.ndarray) -> np.ndarray:
    return rgb @ np.array([0.3, 0.59, 0.11], dtype=np.float32)


def tier_variants(rgba: np.ndarray, mask: np.ndarray) -> dict:
    """Bakes the static tier looks from a Common sprite and its masks; alpha never changes."""
    rgb = rgba[..., :3].astype(np.float32) / 255
    m = mask.astype(np.float32) / 255
    metal, rim = m[..., 0:1], m[..., 2:3]
    lum = luma(rgb)[..., None]

    def pack(c):
        return np.dstack(((np.clip(c, 0, 1) * 255 + 0.5).astype(np.uint8), rgba[..., 3]))

    pink = col(UNCOMMON_TINT)
    uncommon = rgb * (0.55 + 0.45 * pink / pink.max()) * 1.1 + rim * 0.35 * pink + 0.09 * pink  # the lift reaches dark cloth, which a multiply cannot
    orange = col(RARE_KEY) * np.clip(lum * 1.75, 0, 1.25)
    amount = 0.3 + 0.6 * metal
    rare = rgb * (1 - amount) + orange * amount + rim * 0.34 * col(RARE_RIM)
    # base hue survives: only metal goes gold, cloth and paint stay themselves but brighter
    gold = col("gold.material") * np.clip(lum * 1.7, 0, 1.2)
    gilt = 0.12 + 0.55 * metal
    warm = np.minimum((rgb * (1 - gilt) + gold * gilt) * 1.22, 0.92)
    white = np.clip(0.05 + 0.5 * lum, 0, ENHANCED_CAP)
    enhanced = warm * (1 - white) + white
    return {"uncommon": pack(uncommon), "rare": pack(rare), "enhanced": pack(enhanced)}


def aura_blob() -> np.ndarray:
    size = 30 * SCALE
    yy, xx = np.mgrid[0:size, 0:size].astype(np.float32)
    r = np.hypot(xx + 0.5 - size / 2, yy + 0.5 - size / 2) / (size / 2)
    t = np.clip(1 - r, 0, 1)
    out = np.full((size, size, 4), 255, dtype=np.uint8)
    out[..., 3] = (t * t * (3 - 2 * t) * 255 + 0.5).astype(np.uint8)
    return out


def write_ramps(out: Path) -> None:
    rows, table = [], {}
    for i, (name, keys) in enumerate(RAMP_KEYS.items()):
        pts = [token(k) for k in keys]
        line = np.zeros((256, 3), dtype=np.float32)
        for x in range(256):
            f = x / 256 * len(pts)
            j = int(f) % len(pts)
            line[x] = pts[j] + (pts[(j + 1) % len(pts)] - pts[j]) * (f - int(f))
        row = np.full((16, 256, 4), 255, dtype=np.uint8)
        row[..., :3] = (np.clip(line, 0, 1) * 255 + 0.5).astype(np.uint8)
        rows.append(row)
        table[name] = {"x": 0, "y": i * 16, "w": 256, "h": 16, "slice": None, "pivot": [0, 0], "tile": "x", "meta": {"row": i}}
    table["ramp.rare"]["meta"]["rim"] = ["#F1C385", "#E4C2BC", "#B19FC9"]
    uiart.save_png(np.vstack(rows), out / "tier_ramps.png")
    doc = {"version": 1, "image": "tier_ramps.png", "scale": SCALE, "size": [256, 48], "origin": "top-left", "yDown": True,
           "alpha": "straight", "extrude": 0, "kind": "ramp", "meta": {}, "sprites": table}
    (out / "tier_ramps.json").write_text(json.dumps(doc, indent=1, sort_keys=True) + "\n", encoding="utf-8", newline="\n")


ICON_EXPOSURE = 0.50   # the icons sit on dark cells in a dark window: they must not be the brightest thing on screen
ICON_ALBEDO = 0.72    # and a little darker still, so aged gold and paint stop shouting
ICON_DESAT = 0.75      # linear-light saturation kept; pure toy colours read cheap next to forged iron


def forge_render(s: Scene) -> tuple:
    """The painted scene buffers, lit through forge: linear-light GGX with Schlick Fresnel and baked AO, bloom and a
    filmic curve in finish(). The buffers already carry normals, albedo, metal, gloss and emission, so the shapes and
    colours stay the designed ones; only the light transport changes. Returns hi-res float RGBA and the mask channels."""
    H, W = s.H, s.W
    yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
    n = s.nrm.copy()
    n[..., 0] += (xx / W - 0.5) * 0.30   # the slight lens bend, so flat faces take a gradient
    n[..., 1] += (yy / H - 0.5) * 0.30
    n /= np.linalg.norm(n, axis=2, keepdims=True)
    nz = n[..., 2:]
    lin = uiart.srgb_to_linear(np.clip(s.alb, 0, 1))
    lum = (lin @ np.array([0.2126, 0.7152, 0.0722], dtype=np.float32))[..., None]
    lin = (lum + (lin - lum) * ICON_DESAT) * ICON_ALBEDO
    metal = s.metal[..., None]
    spec_col = (1 - s.tintk[..., None]) + np.clip(lin * 2.2, 0, 1) * s.tintk[..., None]
    f0 = (0.022 + 0.05 * np.clip(s.spec, 0, 2)[..., None]) * spec_col * (1 - metal) + np.clip(lin * 1.25, 0, 0.8) * metal
    rough = np.clip(np.sqrt(2.0 / (s.gloss + 2.0)) * 1.6, 0.12, 0.85)
    surf = fg.Surface((lin * (1 - metal)).astype(np.float32), f0.astype(np.float32), rough.astype(np.float32),
                      np.zeros((H, W), np.float32), np.zeros((H, W), np.float32), s.a.copy())
    # forge's rig has no sky, so polished metal would turn black: give it a dim fill from above (faces that look up take more)
    sky = 0.55 + 0.45 * np.clip(-n[..., 1:2], 0, 1)
    surf.emit = (lin * s.emis[..., None] * 2.6 + np.clip(lin * 1.25, 0, 0.8) * metal * sky * 0.075).astype(np.float32)
    maps = fg.Maps(n.astype(np.float32), s.ao.astype(np.float32), np.ones((H, W), np.float32), np.zeros((H, W), np.float32),
                   np.zeros((H, W), np.float32), np.zeros((H, W), np.float32))
    img = fg.finish(fg.light(surf, maps), s.a, exposure=ICON_EXPOSURE)
    fres = (1 - np.clip(nz[..., 0], 0, 1)) ** 3
    solid = s.a > 0.5
    d = quick_sdf(solid) / PPU
    rim = 1 - smooth(0.0, 1.4, np.maximum(d, 0))
    rim = np.clip(rim * 0.8 + fres * 0.6, 0, 1)
    refl = np.clip(0.5 + 0.35 * n[..., 0] + 0.35 * n[..., 1], 0, 1)
    mask = np.dstack((s.metal, refl, rim, np.clip(s.emis, 0, 1))).astype(np.float32)
    return img, mask


def render_icon(name: str) -> tuple:
    cols, rows, fn = ICONS[name]
    s = Scene(SEED_NAME.get(name, name), cols, rows)
    fn(s)
    s.center(getattr(fn, "top", None))
    img, mask = forge_render(s)
    return defringe(fg.downsample(img)), mask_down(mask, s.a)


def defringe(rgba: np.ndarray, floor: int = 48) -> np.ndarray:
    """Faint edge pixels take the average colour of the solid pixels around them: un-premultiplying a sliver of
    coverage is noisy, and the noise would travel into every tier variant and the atlas bleed."""
    out = rgba.copy()
    w = (rgba[..., 3] >= floor).astype(np.float32)
    num = np.stack([fg.blur(rgba[..., c].astype(np.float32) * w, 1.4) for c in range(3)], axis=2)
    den = fg.blur(w, 1.4)[..., None]
    faint = (rgba[..., 3] > 0) & (rgba[..., 3] < floor) & (den[..., 0] > 0.02)
    out[..., :3][faint] = np.clip(num / np.maximum(den, 0.02) + 0.5, 0, 255).astype(np.uint8)[faint]
    return out


def main(out_root: Path) -> None:
    out = Path(out_root) / "items"
    out.mkdir(parents=True, exist_ok=True)
    variants = [
        {"name": "uncommon", "image": "items_uncommon.png", "kind": "color"},
        {"name": "rare", "image": "items_rare.png", "kind": "color"},
        {"name": "mask", "image": "items_mask.png", "kind": "mask"},
    ]
    meta = {"variants": variants, "channels": {"mask": "R metal, G reflect, B rim, A emissive"}}
    sheets = {"base": Atlas("items", out, meta=meta)}
    for v in variants:
        sheets[v["name"]] = Atlas(v["image"][:-4], out, kind=v["kind"])
    pivot = (int(MARGIN * SCALE), int(MARGIN * SCALE))
    for name in sorted(ICONS):
        cols, rows, _ = ICONS[name]
        rgba, mask = render_icon(name)
        info = {"footprint": [cols, rows]}
        sheets["base"].add(Sprite(name, rgba, pivot=pivot, meta=info))
        sheets["mask"].add(Sprite(name, mask, pivot=pivot, meta=info))
        for tier, img in tier_variants(rgba, mask).items():
            # No page for the enhanced look until something draws it: dream/ui has a size budget.
            if tier in sheets:
                sheets[tier].add(Sprite(name, img, pivot=pivot, meta=info))
    blob = aura_blob()
    centre = (blob.shape[1] // 2, blob.shape[0] // 2)
    for key, atlas in sheets.items():
        img = np.zeros_like(blob) if key == "mask" else blob
        atlas.add(Sprite("fx.aura.blob", img.copy(), pivot=centre, meta={"blend": "additive"}))
    for key, atlas in sheets.items():
        _, js = atlas.save()
        if key != "base":
            js.unlink()
    write_ramps(out)


# previews -----------------------------------------------------------------------------


def rgb8(name: str) -> tuple:
    return tuple(int(v * 255 + 0.5) for v in token(name)) + (255,)


def cell_board(cols: int, rows: int, z: float) -> Image.Image:
    c = int(CELL * z)
    im = Image.new("RGBA", (cols * c, rows * c), rgb8("cell.occupied"))
    d = ImageDraw.Draw(im)
    for i in range(cols + 1):
        d.line([(i * c, 0), (i * c, rows * c)], fill=rgb8("cell.occupied.line.v"))
    for j in range(rows + 1):
        d.line([(0, j * c), (cols * c, j * c)], fill=rgb8("cell.occupied.line.h"))
    return im


def preview(out_root: Path, previews: Path) -> None:
    """Icons on occupied cells at the 1080p scale of 2.25x, plus the tier sheet."""
    z = 2.25
    items = Path(out_root) / "items"
    doc = json.loads((items / "items.json").read_text(encoding="utf-8"))
    names = [n for n in doc["sprites"] if n.startswith("item.")]
    names.sort(key=lambda n: (doc["sprites"][n]["meta"]["footprint"][1], doc["sprites"][n]["meta"]["footprint"][0], n))
    cw = int(CELL * z)

    def sheet_for(image: str, subset, cols: int = 17, rows: int = 13):
        sheet = Image.open(items / image).convert("RGBA")
        board = cell_board(cols, rows, z)
        x, y, shelf = 1, 1, 0
        for name in subset:
            r = doc["sprites"][name]
            fc, fr = r["meta"]["footprint"]
            if x + fc > cols:
                x, y, shelf = 1, y + shelf + (1 if rows > 3 else 0), 0
            tile = sheet.crop((r["x"], r["y"], r["x"] + r["w"], r["y"] + r["h"]))
            tile = tile.resize((int(tile.width * z / SCALE), int(tile.height * z / SCALE)), Image.BOX)
            layer = Image.new("RGBA", board.size, (0, 0, 0, 0))
            layer.paste(tile, (x * cw - int(MARGIN * z), y * cw - int(MARGIN * z)), tile)
            board.alpha_composite(layer)
            x += fc
            shelf = max(shelf, fr)
        return board.crop((0, 0, cols * cw, (y + shelf + 1) * cw))

    previews = Path(previews)
    previews.mkdir(parents=True, exist_ok=True)
    sheet_for("items.png", names, 17, 17).convert("RGB").save(previews / "items_icons.png", compress_level=6)
    wanted = {"item.axe.twinbit", "item.armor.gilded", "item.helm.gilded", "item.gear.iron-blade", "item.shield.kite", "item.gem.amber", "item.gear.iron-band"}
    picks = [n for n in names if n in wanted]
    boards = [sheet_for(f"items{suf}.png", picks, cols=15, rows=5) for suf in ("", "_uncommon", "_rare")]
    full = Image.new("RGB", (boards[0].width, sum(b.height for b in boards)))
    y = 0
    for b in boards:
        full.paste(b.convert("RGB"), (0, y))
        y += b.height
    full.save(previews / "items_tiers.png", compress_level=6)


if __name__ == "__main__":
    here = Path(__file__).resolve().parent
    root = Path(sys.argv[1]) if len(sys.argv) > 1 else here.parents[1] / "dream" / "ui"
    main(root)
    preview(root, here / "previews")
