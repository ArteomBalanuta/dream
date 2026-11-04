"""Shared toolkit for dream's procedural UI art.

Images are float32 HxWx4 straight RGBA in [0,1], sRGB-encoded. Masks are float32 HxW.
Geometry arguments are logical units (u), origin top-left, y down. Working canvases are
hi-res at PPU px/u; `downsample` brings them to SCALE px/u as uint8 for atlases.
Seeds come from crc32(name), so output is deterministic.

Lighting in this module (`shade`, `material`) works on sRGB-encoded values and is the legacy path.
New art goes through forge.py instead: linear-light GGX, baked AO and shadow, aging, filmic
finish and a linear-light Mitchell downsample. Both routes end in the same Atlas, which takes uint8
sprites (forge.downsample) or float hi-res sprites (the legacy box filter below).
"""
from __future__ import annotations

import hashlib
import json
import math
import re
import tempfile
import zlib
from dataclasses import dataclass, field
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFilter

SCALE = 4
SS = 4
PPU = SCALE * SS
SEED = 20261101
LIGHT_TL = (-0.6, -0.6, 0.53)
LIGHT_TR = (0.6, -0.6, 0.53)

_HERE = Path(__file__).resolve().parent
TOKENS: dict[str, tuple[str, float]] = {
    k: (v[0], float(v[1])) for k, v in json.loads((_HERE / "tokens.json").read_text(encoding="utf-8")).items()
}


def _hex(h: str) -> np.ndarray:
    h = h.lstrip("#")
    return np.array([int(h[i : i + 2], 16) / 255 for i in (0, 2, 4)], dtype=np.float32)


def token(name: str) -> np.ndarray:
    """RGB float[3] for a token name or a literal #RRGGBB."""
    return _hex(name) if name.startswith("#") else _hex(TOKENS[name][0])


def rng(name: str) -> np.random.Generator:
    return np.random.default_rng(SEED ^ zlib.crc32(name.encode("utf-8")))


def _px(u: float) -> int:
    return int(round(u * PPU))


def srgb_to_linear(c: np.ndarray) -> np.ndarray:
    """sRGB-encoded [0,1] to linear light; any shape, float32 out."""
    c = np.asarray(c, dtype=np.float32)
    return np.where(c <= 0.04045, c / 12.92, ((np.maximum(c, 0) + 0.055) / 1.055) ** 2.4).astype(np.float32)


def linear_to_srgb(x: np.ndarray) -> np.ndarray:
    """Linear light [0,1] to sRGB-encoded; any shape, float32 out."""
    x = np.clip(np.asarray(x, dtype=np.float32), 0, 1)
    return np.where(x <= 0.0031308, x * 12.92, 1.055 * np.power(x, 1 / 2.4) - 0.055).astype(np.float32)


# canvas and masks ---------------------------------------------------------------------


def canvas(w_u: float, h_u: float, fill: str | None = None) -> np.ndarray:
    img = np.zeros((_px(h_u), _px(w_u), 4), dtype=np.float32)
    if fill:
        img[..., :3] = token(fill)
        img[..., 3] = 1
    return img


def _mask_image(w_u, h_u) -> tuple[Image.Image, ImageDraw.ImageDraw]:
    im = Image.new("L", (_px(w_u), _px(h_u)), 0)
    return im, ImageDraw.Draw(im)


def _to_mask(im: Image.Image) -> np.ndarray:
    return np.asarray(im, dtype=np.float32) / 255


def rect_mask(w_u, h_u, rect: tuple, radius_u: float = 0) -> np.ndarray:
    """rect is (x, y, w, h) in u."""
    im, d = _mask_image(w_u, h_u)
    x, y, w, h = rect
    box = [x * PPU, y * PPU, (x + w) * PPU - 1, (y + h) * PPU - 1]
    if radius_u > 0:
        d.rounded_rectangle(box, radius_u * PPU, fill=255)
    else:
        d.rectangle(box, fill=255)
    return _to_mask(im)


def poly_mask(w_u, h_u, pts: list[tuple[float, float]]) -> np.ndarray:
    im, d = _mask_image(w_u, h_u)
    d.polygon([(x * PPU, y * PPU) for x, y in pts], fill=255)
    return _to_mask(im)


def ellipse_mask(w_u, h_u, cx, cy, rx, ry, angle_deg: float = 0) -> np.ndarray:
    h, w = _px(h_u), _px(w_u)
    yy, xx = np.mgrid[0:h, 0:w].astype(np.float32)
    x = (xx + 0.5) / PPU - cx
    y = (yy + 0.5) / PPU - cy
    a = math.radians(angle_deg)
    u = x * math.cos(a) + y * math.sin(a)
    v = -x * math.sin(a) + y * math.cos(a)
    return ((u / rx) ** 2 + (v / ry) ** 2 <= 1).astype(np.float32)


def stroke_mask(w_u, h_u, pts, width_u: float, closed: bool = False, taper: bool = False) -> np.ndarray:
    """Polyline stroke with round joins. taper narrows linearly from width_u to 0."""
    im, d = _mask_image(w_u, h_u)
    pts = list(pts) + ([pts[0]] if closed else [])
    seg = [math.dist(pts[i], pts[i + 1]) for i in range(len(pts) - 1)]
    total = sum(seg) or 1
    walked = 0.0
    for (a, b), s in zip(zip(pts, pts[1:]), seg):
        steps = max(1, int(s * PPU / 2))
        for i in range(steps + 1):
            t = i / steps
            frac = (walked + s * t) / total
            r = width_u * (1 - frac if taper else 1) * PPU / 2
            x = (a[0] + (b[0] - a[0]) * t) * PPU
            y = (a[1] + (b[1] - a[1]) * t) * PPU
            d.ellipse([x - r, y - r, x + r, y + r], fill=255)
        walked += s
    return _to_mask(im)


def bezier(p0, p1, p2, p3, n: int = 32) -> list[tuple[float, float]]:
    out = []
    for i in range(n + 1):
        t = i / n
        m = 1 - t
        out.append(
            (
                m**3 * p0[0] + 3 * m * m * t * p1[0] + 3 * m * t * t * p2[0] + t**3 * p3[0],
                m**3 * p0[1] + 3 * m * m * t * p1[1] + 3 * m * t * t * p2[1] + t**3 * p3[1],
            )
        )
    return out


# SDF, heightfield, lighting -----------------------------------------------------------


def _jump_flood(seed: np.ndarray) -> np.ndarray:
    """Distance in px from every pixel to the nearest True pixel of seed."""
    h, w = seed.shape
    if not seed.any():
        return np.full((h, w), float(h + w), dtype=np.float32)
    yy, xx = np.mgrid[0:h, 0:w].astype(np.int32)
    ny = np.where(seed, yy, -1)
    nx = np.where(seed, xx, -1)
    step = 1 << int(math.ceil(math.log2(max(h, w))))
    steps = []
    while step >= 1:
        step //= 2
        if step:
            steps.append(step)
    steps += [2, 1]
    for s in steps:
        for dy, dx in ((-s, 0), (s, 0), (0, -s), (0, s), (-s, -s), (-s, s), (s, -s), (s, s)):
            cy = np.clip(yy + dy, 0, h - 1)
            cx = np.clip(xx + dx, 0, w - 1)
            oy, ox = ny[cy, cx], nx[cy, cx]
            cur = np.where(ny < 0, 1 << 60, (ny.astype(np.int64) - yy) ** 2 + (nx.astype(np.int64) - xx) ** 2)
            new = np.where(oy < 0, 1 << 60, (oy.astype(np.int64) - yy) ** 2 + (ox.astype(np.int64) - xx) ** 2)
            better = new < cur
            ny = np.where(better, oy, ny)
            nx = np.where(better, ox, nx)
    return np.sqrt(((ny - yy).astype(np.float32)) ** 2 + ((nx - xx).astype(np.float32)) ** 2)


def sdf(mask: np.ndarray) -> np.ndarray:
    """Signed distance in px, + inside. Run per element, not per full window: it is O(n log n)."""
    inside = mask > 0.5
    return _jump_flood(~inside) - _jump_flood(inside)


def bevel_height(dist: np.ndarray, width_u: float, profile: str = "round") -> np.ndarray:
    """Height in u from an SDF: rises across width_u from the edge, then flat. round|chamfer|pillow."""
    t = np.clip(dist / (width_u * PPU), 0, 1)
    if profile == "chamfer":
        p = t
    elif profile == "pillow":
        p = np.sin(t * math.pi / 2) ** 2
    else:
        p = np.sqrt(1 - (1 - t) ** 2)
    return (p * width_u).astype(np.float32)


def normals(height: np.ndarray, strength: float = 1.0) -> np.ndarray:
    gy, gx = np.gradient(height.astype(np.float32))
    n = np.dstack((-gx * PPU * strength, -gy * PPU * strength, np.ones_like(height)))
    return n / np.linalg.norm(n, axis=2, keepdims=True)


def shade(height, light=LIGHT_TL, ambient=0.35, diffuse=0.75, spec=0.5, shininess=32.0, strength=1.0):
    """Returns (diffuse term, specular term). Diffuse is 1-ish on flat faces facing the viewer."""
    n = normals(height, strength)
    l = np.array(light, dtype=np.float32)
    l = l / np.linalg.norm(l)
    h = l + np.array([0, 0, 1], dtype=np.float32)
    h = h / np.linalg.norm(h)
    d = np.clip(n @ l, 0, 1)
    flat = l[2]
    dif = ambient + diffuse * d / max(flat, 1e-3) * 0.65
    s = spec * np.clip(n @ h, 0, 1) ** shininess
    flat_s = spec * h[2] ** shininess
    return dif.astype(np.float32), np.clip(s - flat_s, 0, None).astype(np.float32)


# noise --------------------------------------------------------------------------------


def _vnoise(x: np.ndarray, y: np.ndarray, grid: np.ndarray) -> np.ndarray:
    gh, gw = grid.shape
    ix, iy = np.floor(x).astype(np.int64), np.floor(y).astype(np.int64)
    fx, fy = x - ix, y - iy
    sx, sy = fx * fx * (3 - 2 * fx), fy * fy * (3 - 2 * fy)
    a = grid[iy % gh, ix % gw]
    b = grid[iy % gh, (ix + 1) % gw]
    c = grid[(iy + 1) % gh, ix % gw]
    d = grid[(iy + 1) % gh, (ix + 1) % gw]
    return (a * (1 - sx) + b * sx) * (1 - sy) + (c * (1 - sx) + d * sx) * sy


def fbm(h: int, w: int, name: str, scale_u: float, octaves: int = 4) -> np.ndarray:
    """Fractal noise 0..1 on an h x w px image; wraps seamlessly on both axes."""
    r = rng(name)
    yy, xx = np.mgrid[0:h, 0:w].astype(np.float32)
    out = np.zeros((h, w), dtype=np.float32)
    amp, total = 1.0, 0.0
    cell = scale_u * PPU
    for _ in range(octaves):
        gw, gh = max(2, int(round(w / cell))), max(2, int(round(h / cell)))
        grid = r.random((gh, gw), dtype=np.float32)
        out += amp * _vnoise(xx * gw / w, yy * gh / h, grid)
        total += amp
        amp *= 0.5
        cell = max(cell / 2, 2.0)
    out /= total
    return (out - out.min()) / max(float(out.max() - out.min()), 1e-6)


def grain(h: int, w: int, name: str, amp: float) -> np.ndarray:
    """Zero-centred per-pixel noise."""
    return ((rng(name).random((h, w), dtype=np.float32) - 0.5) * 2 * amp).astype(np.float32)


def streaks(h: int, w: int, name: str, angle_deg: float, length_u: float) -> np.ndarray:
    """Directional brushed noise 0..1 running along angle_deg."""
    a = math.radians(angle_deg)
    yy, xx = np.mgrid[0:h, 0:w].astype(np.float32)
    along = (xx * math.cos(a) + yy * math.sin(a)) / (length_u * PPU)
    across = (-xx * math.sin(a) + yy * math.cos(a)) / (0.4 * PPU)
    grid = rng(name).random((256, 256), dtype=np.float32)
    n = _vnoise(across, along, grid)
    return (n - n.min()) / max(float(n.max() - n.min()), 1e-6)


def cracks(h: int, w: int, name: str, count: int, width_u: float = 0.5) -> np.ndarray:
    """Branching crack mask 0..1 on an h x w px image."""
    r = rng(name)
    im = Image.new("L", (w, h), 0)
    d = ImageDraw.Draw(im)
    for _ in range(count):
        x, y = r.random() * w, r.random() * h
        ang = r.random() * math.tau
        pts = [(x, y)]
        for _ in range(int(r.integers(8, 22))):
            ang += (r.random() - 0.5) * 1.1
            step = (1 + r.random() * 3) * PPU
            x += math.cos(ang) * step
            y += math.sin(ang) * step
            pts.append((x, y))
        d.line(pts, fill=255, width=max(1, int(width_u * PPU)))
    return _to_mask(im.filter(ImageFilter.GaussianBlur(PPU * 0.08)))


# materials ----------------------------------------------------------------------------

_MATERIALS = {
    # base, ambient, diffuse, spec, shininess, noise amount, bevel u
    "iron": ("#2A2B2C", 0.45, 0.75, 0.30, 24, 0.14, 1.5),
    "steel": ("#6E7074", 0.40, 0.80, 0.70, 40, 0.08, 1.5),
    "chrome": ("#8E9094", 0.30, 0.95, 1.10, 64, 0.04, 1.5),
    "obsidian": ("#0C0C0D", 0.50, 0.70, 1.00, 90, 0.03, 2.0),
    "stone": ("#444347", 0.60, 0.55, 0.05, 8, 0.20, 1.0),
    "gold": ("#B8862E", 0.45, 0.80, 0.90, 48, 0.08, 1.0),
    "bone": ("#C9BC9C", 0.50, 0.70, 0.25, 18, 0.14, 1.5),
    "parchment": ("#B9A77E", 0.65, 0.50, 0.05, 8, 0.22, 0.8),
    "gem": ("#C01808", 0.50, 0.70, 1.20, 90, 0.02, 1.0),
}


def material(name: str, mask, height=None, light=LIGHT_TL, seed: str = "", base: str | None = None, facets=None) -> np.ndarray:
    """Lit RGBA for a mask. height defaults to a bevel from the mask's SDF.

    facets, for name "gem", is passed to facet_gem semantics by callers; here it only selects
    flat shading (no noise) so polygons keep their authored values.
    """
    col, amb, dif, spc, shin, noise, bevel = _MATERIALS[name]
    colour = token(base) if base else _hex(col)
    if height is None:
        height = bevel_height(sdf(mask), bevel)
    d, s = shade(height, light, amb, dif, spc, shin)
    h, w = mask.shape
    tex = np.ones((h, w), dtype=np.float32)
    if noise and facets is None:
        tag = seed or name
        tex += (fbm(h, w, tag + ".f", 6.0) - 0.5) * noise * 1.5 + grain(h, w, tag + ".g", noise * 0.35)
    rgb = colour[None, None, :] * (d * tex)[..., None] + s[..., None]
    return np.dstack((np.clip(rgb, 0, 1), mask)).astype(np.float32)


def facet_gem(w_u, h_u, facets: list[tuple[list, str]], light=LIGHT_TL) -> np.ndarray:
    """Flat-shaded polygons in order. Each is (points, colour) or (points, top colour, bottom colour)."""
    img = canvas(w_u, h_u)
    for f in facets:
        pts, c0 = f[0], f[1]
        m = poly_mask(w_u, h_u, pts)
        col = token(c0)
        if len(f) > 2:
            ys = np.linspace(0, 1, m.shape[0], dtype=np.float32)[:, None, None]
            y0, y1 = min(p[1] for p in pts) / h_u, max(p[1] for p in pts) / h_u
            t = np.clip((ys - y0) / max(y1 - y0, 1e-4), 0, 1)
            rgb = col * (1 - t) + token(f[2]) * t
            rgb = np.broadcast_to(rgb, img.shape[:2] + (3,))
        else:
            rgb = np.broadcast_to(col, img.shape[:2] + (3,))
        src = np.dstack((rgb, m)).astype(np.float32)
        img = over(img, src)
    return img


# compositing --------------------------------------------------------------------------


def over(dst, src, x_u: float = 0, y_u: float = 0) -> np.ndarray:
    """src over dst at (x_u, y_u), clipped; returns a new image."""
    out = dst.copy()
    ox, oy = _px(x_u), _px(y_u)
    h, w = dst.shape[:2]
    sh, sw = src.shape[:2]
    x0, y0, x1, y1 = max(ox, 0), max(oy, 0), min(ox + sw, w), min(oy + sh, h)
    if x0 >= x1 or y0 >= y1:
        return out
    s = src[y0 - oy : y1 - oy, x0 - ox : x1 - ox]
    d = out[y0:y1, x0:x1]
    sa, da = s[..., 3:], d[..., 3:]
    oa = sa + da * (1 - sa)
    rgb = (s[..., :3] * sa + d[..., :3] * da * (1 - sa)) / np.maximum(oa, 1e-6)
    out[y0:y1, x0:x1] = np.dstack((rgb, oa))
    return out


def tint(img, hexcolor: str, amount: float) -> np.ndarray:
    out = img.copy()
    out[..., :3] = img[..., :3] * (1 - amount) + token(hexcolor) * amount
    return out


def gradient(h: int, w: int, stops: list[tuple[float, str]], axis: str = "y") -> np.ndarray:
    """Opaque RGBA gradient; stops are (0..1, colour)."""
    n = h if axis == "y" else w
    t = np.linspace(0, 1, n, dtype=np.float32)
    pos = [p for p, _ in stops]
    cols = np.array([token(c) for _, c in stops])
    line = np.stack([np.interp(t, pos, cols[:, i]) for i in range(3)], axis=1).astype(np.float32)
    rgb = line[:, None, :] if axis == "y" else line[None, :, :]
    rgb = np.broadcast_to(rgb, (h, w, 3))
    return np.dstack((rgb, np.ones((h, w), dtype=np.float32)))


def mirror_x(img) -> np.ndarray:
    return img[:, ::-1].copy()


def _blur(a: np.ndarray, radius_px: float) -> np.ndarray:
    im = Image.fromarray((np.clip(a, 0, 1) * 255).astype(np.uint8), "L")
    return _to_mask(im.filter(ImageFilter.GaussianBlur(radius_px)))


def drop_shadow(img, dx_u, dy_u, blur_u, color="#000000", opacity=0.8) -> np.ndarray:
    """img over its own shifted, blurred shadow, on the same canvas."""
    a = np.roll(img[..., 3], (_px(dy_u), _px(dx_u)), axis=(0, 1))
    if blur_u > 0:
        a = _blur(a, blur_u * PPU)
    sh = np.dstack((np.broadcast_to(token(color), img.shape[:2] + (3,)), a * opacity)).astype(np.float32)
    return over(sh, img)


def halo(img, width_u: float = 0.75, color="#000000", opacity=0.8) -> np.ndarray:
    """img over a uniform dark outline of width_u."""
    d = _jump_flood(img[..., 3] > 0.5)
    a = np.clip(1 - (d - width_u * PPU) / (0.25 * PPU), 0, 1)
    sh = np.dstack((np.broadcast_to(token(color), img.shape[:2] + (3,)), a * opacity)).astype(np.float32)
    return over(sh, img)


def inner_shadow(img, width_u: float, color: str) -> np.ndarray:
    """Darken towards the alpha edge, inside the shape."""
    d = _jump_flood(img[..., 3] <= 0.5)
    t = 1 - np.clip(d / (width_u * PPU), 0, 1)
    out = img.copy()
    out[..., :3] = img[..., :3] * (1 - t[..., None]) + token(color) * t[..., None]
    return out


def downsample(img, factor: int = SS) -> np.ndarray:
    """Legacy premultiplied box filter in sRGB-encoded space (gen_fx, old generators).

    Returns straight-alpha uint8 RGBA at img size / factor. forge.downsample is the linear-light
    Mitchell replacement that new art uses; it hands Atlas.add ready uint8 sprites.
    """
    h, w = img.shape[0] // factor, img.shape[1] // factor
    a = img[: h * factor, : w * factor]
    pm = np.dstack((a[..., :3] * a[..., 3:], a[..., 3]))
    pm = pm.reshape(h, factor, w, factor, 4).mean(axis=(1, 3))
    alpha = pm[..., 3:]
    rgb = pm[..., :3] / np.maximum(alpha, 1e-6)
    out = np.dstack((np.clip(rgb, 0, 1), alpha))
    return (out * 255 + 0.5).astype(np.uint8)


# atlas --------------------------------------------------------------------------------


def bleed_rgb(rgba_u8: np.ndarray) -> np.ndarray:
    """Dilate RGB under A=0 so filtering never pulls in black."""
    out = rgba_u8.copy()
    known = out[..., 3] > 0
    if known.all():
        return out
    if not known.any():
        out[..., :3] = 0
        return out
    # nearest known pixel by jump flood indices
    h, w = known.shape
    yy, xx = np.mgrid[0:h, 0:w].astype(np.int32)
    ny, nx = np.where(known, yy, -1), np.where(known, xx, -1)
    s = 1 << int(math.ceil(math.log2(max(h, w))))
    while s >= 1:
        s = max(s // 2, 0) if s > 1 else 0
        if not s:
            break
        for dy, dx in ((-s, 0), (s, 0), (0, -s), (0, s), (-s, -s), (-s, s), (s, -s), (s, s)):
            cy, cx = np.clip(yy + dy, 0, h - 1), np.clip(xx + dx, 0, w - 1)
            oy, ox = ny[cy, cx], nx[cy, cx]
            cur = np.where(ny < 0, 1 << 60, (ny.astype(np.int64) - yy) ** 2 + (nx.astype(np.int64) - xx) ** 2)
            new = np.where(oy < 0, 1 << 60, (oy.astype(np.int64) - yy) ** 2 + (ox.astype(np.int64) - xx) ** 2)
            b = new < cur
            ny, nx = np.where(b, oy, ny), np.where(b, ox, nx)
    empty = ~known
    out[..., :3][empty] = rgba_u8[..., :3][ny[empty], nx[empty]]
    return out


@dataclass
class Sprite:
    name: str
    image: np.ndarray  # uint8 RGBA at atlas scale, or float hi-res (downsampled on add)
    slice: tuple | None = None  # (l, t, r, b) px
    pivot: tuple = (0, 0)  # px from sprite top-left
    tile: str = "none"  # none|x|y|xy
    meta: dict | None = None


def save_png(rgba_u8: np.ndarray, path: Path) -> None:
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    # Level 9: the forged pages are noisy and dream/ui has a budget (check_loader).
    Image.fromarray(rgba_u8, "RGBA").save(path, format="PNG", compress_level=9)


class Atlas:
    """Shelf-packed atlas: sorted by (-h, name), so output does not depend on add order."""

    def __init__(self, stem: str, out_dir: Path, max_size: int = 4096, extrude: int = 4, kind: str = "color", scale: int = SCALE, meta: dict | None = None):
        self.stem, self.out_dir, self.max_size, self.extrude, self.kind, self.scale = stem, Path(out_dir), max_size, extrude, kind, scale
        self.meta = meta or {}
        self.sprites: dict[str, Sprite] = {}

    def add(self, sprite: Sprite) -> None:
        if sprite.name in self.sprites:
            raise ValueError(f"duplicate sprite {sprite.name}")
        if sprite.image.dtype != np.uint8:
            sprite.image = downsample(sprite.image)
        self.sprites[sprite.name] = sprite

    def _layout(self, items, width):
        x = y = shelf = 0
        rects = {}
        pad = 2 * self.extrude
        for s in items:
            h, w = s.image.shape[:2]
            if x + w + pad > width:
                x, y, shelf = 0, y + shelf, 0
            rects[s.name] = (x + self.extrude, y + self.extrude)
            x += w + pad
            shelf = max(shelf, h + pad)
        return rects, y + shelf

    def save(self) -> tuple[Path, Path]:
        items = sorted(self.sprites.values(), key=lambda s: (-s.image.shape[0], s.name))
        area = sum((s.image.shape[0] + 2 * self.extrude) * (s.image.shape[1] + 2 * self.extrude) for s in items)
        widest = max(s.image.shape[1] for s in items) + 2 * self.extrude
        width = min(self.max_size, max(widest, int(math.sqrt(area) * 1.2)))
        rects, height = self._layout(items, width)
        if height > self.max_size:
            rects, height = self._layout(items, self.max_size)
            width = self.max_size
        if height > self.max_size:
            raise ValueError(f"atlas {self.stem} overflow: {width}x{height}")
        sheet = np.zeros((height, width, 4), dtype=np.uint8)
        table = {}
        e = self.extrude
        for s in sorted(items, key=lambda s: s.name):
            x, y = rects[s.name]
            img = bleed_rgb(s.image) if self.kind == "color" else s.image
            h, w = img.shape[:2]
            modey = "wrap" if "y" in s.tile else "edge"
            modex = "wrap" if "x" in s.tile else "edge"
            pad = np.pad(img, ((e, e), (0, 0), (0, 0)), mode=modey)
            pad = np.pad(pad, ((0, 0), (e, e), (0, 0)), mode=modex)
            sheet[y - e : y + h + e, x - e : x + w + e] = pad
            table[s.name] = {
                "x": x, "y": y, "w": w, "h": h,
                "slice": list(s.slice) if s.slice else None,
                "pivot": list(s.pivot), "tile": s.tile, "meta": s.meta or {},
            }
        self.out_dir.mkdir(parents=True, exist_ok=True)
        png, js = self.out_dir / f"{self.stem}.png", self.out_dir / f"{self.stem}.json"
        save_png(sheet, png)
        doc = {
            "version": 1, "image": png.name, "scale": self.scale, "size": [width, height],
            "origin": "top-left", "yDown": True, "alpha": "straight",
            "extrude": e, "kind": self.kind, "meta": self.meta, "sprites": table,
        }
        js.write_text(json.dumps(doc, indent=1, sort_keys=True) + "\n", encoding="utf-8", newline="\n")
        return png, js


def contact_sheet(json_paths: list[Path], out: Path, cols: int = 8) -> None:
    """Preview of every sprite on a mid-grey checker, for human review."""
    tiles = []
    for jp in json_paths:
        doc = json.loads(Path(jp).read_text(encoding="utf-8"))
        sheet = np.asarray(Image.open(Path(jp).parent / doc["image"]).convert("RGBA"))
        for name in sorted(doc["sprites"]):
            r = doc["sprites"][name]
            tiles.append(sheet[r["y"] : r["y"] + r["h"], r["x"] : r["x"] + r["w"]])
    if not tiles:
        return
    cw = 256
    rows = []
    for i in range(0, len(tiles), cols):
        rows.append(tiles[i : i + cols])
    heights = [min(cw, max(t.shape[0] for t in row)) + 8 for row in rows]
    board = Image.new("RGBA", (cols * (cw + 8), sum(heights)), (64, 64, 64, 255))
    d = ImageDraw.Draw(board)
    for x in range(0, board.width, 16):
        for y in range(0, board.height, 16):
            if (x + y) // 16 % 2:
                d.rectangle([x, y, x + 15, y + 15], fill=(80, 80, 80, 255))
    y = 0
    for row, rh in zip(rows, heights):
        for i, t in enumerate(row):
            im = Image.fromarray(t, "RGBA")
            f = min(1.0, cw / max(im.size))
            if f < 1:
                im = im.resize((max(1, int(im.width * f)), max(1, int(im.height * f))), Image.BILINEAR)
            board.alpha_composite(im, (i * (cw + 8) + 4, y + 4))
        y += rh
    Path(out).parent.mkdir(parents=True, exist_ok=True)
    board.convert("RGB").save(out, compress_level=6)


FORBIDDEN_NAMES = re.compile(r"zen|chaos|larkan|loch|mu_?online|arcana|soul|bless", re.I)


def validate_atlas(json_path: Path, rules: dict | None = None) -> list[str]:
    """Empty list = pass. rules: {"require4": bool} to enforce px multiples of 4 on color atlases."""
    rules = rules or {}
    errs: list[str] = []
    json_path = Path(json_path)
    doc = json.loads(json_path.read_text(encoding="utf-8"))
    for k in ("version", "image", "scale", "size", "origin", "yDown", "alpha", "extrude", "kind", "sprites"):
        if k not in doc:
            errs.append(f"missing key {k}")
    if errs:
        return errs
    im = Image.open(json_path.parent / doc["image"])
    if im.mode != "RGBA":
        errs.append(f"mode {im.mode}")
    if list(im.size) != doc["size"]:
        errs.append(f"size {im.size} vs {doc['size']}")
    if max(im.size) > 4096:
        errs.append("over 4096")
    sheet = np.asarray(im.convert("RGBA"))
    e = doc["extrude"]
    H, W = sheet.shape[:2]
    occ = np.zeros((H, W), dtype=bool)
    if FORBIDDEN_NAMES.search(json_path.name):
        errs.append(f"forbidden filename {json_path.name}")
    for name, r in sorted(doc["sprites"].items()):
        x, y, w, h = r["x"], r["y"], r["w"], r["h"]
        tag = f"{json_path.stem}:{name}"
        if FORBIDDEN_NAMES.search(name):
            errs.append(f"{tag}: forbidden name")
        if x - e < 0 or y - e < 0 or x + w + e > W or y + h + e > H:
            errs.append(f"{tag}: out of bounds")
            continue
        if occ[y - e : y + h + e, x - e : x + w + e].any():
            errs.append(f"{tag}: overlaps")
        occ[y - e : y + h + e, x - e : x + w + e] = True
        if rules.get("require4") and doc["kind"] == "color" and (w % 4 or h % 4):
            errs.append(f"{tag}: size not multiple of 4")
        s = sheet[y : y + h, x : x + w]
        if not (s[..., 3] > 0).any() and doc["kind"] == "color":
            errs.append(f"{tag}: empty")
        sl = r["slice"]
        if sl and (sl[0] + sl[2] >= w or sl[1] + sl[3] >= h):
            errs.append(f"{tag}: bad slice")
        px, py = r["pivot"]
        if not r.get("meta", {}).get("pivotOverflow") and not (0 <= px <= w and 0 <= py <= h):
            errs.append(f"{tag}: pivot out of bounds")
        tile = r["tile"]
        for i in range(1, e + 1):
            top = s[(-i) % h] if "y" in tile else s[0]
            bottom = s[(i - 1) % h] if "y" in tile else s[-1]
            left = s[:, (-i) % w] if "x" in tile else s[:, 0]
            right = s[:, (i - 1) % w] if "x" in tile else s[:, -1]
            if (
                (sheet[y - i, x : x + w] != top).any()
                or (sheet[y + h + i - 1, x : x + w] != bottom).any()
                or (sheet[y : y + h, x - i] != left).any()
                or (sheet[y : y + h, x + w + i - 1] != right).any()
            ):
                errs.append(f"{tag}: bad extrusion")
                break
        if doc["kind"] == "color":
            a0 = s[..., 3] == 0
            near = np.zeros_like(a0)
            near[:-1] |= s[1:, :, 3] > 0
            near[1:] |= s[:-1, :, 3] > 0
            near[:, :-1] |= s[:, 1:, 3] > 0
            near[:, 1:] |= s[:, :-1, 3] > 0
            edge = a0 & near
            if edge.any():
                solid = s[s[..., 3] > 128][:, :3]
                if solid.size:
                    # darkest solid value bounds what a clean bleed can produce
                    if int(s[edge][:, :3].max(axis=1).min()) + 40 < int(solid.max(axis=1).min()):
                        errs.append(f"{tag}: dark fringe")
        if "x" in tile and (np.abs(s[:, 0, :3].astype(int) - s[:, -1, :3].astype(int)).mean() > 24):
            errs.append(f"{tag}: x seam")
        if "y" in tile and (np.abs(s[0, :, :3].astype(int) - s[-1, :, :3].astype(int)).mean() > 24):
            errs.append(f"{tag}: y seam")
    return errs


def probe(img, rect_px) -> tuple[int, int, int]:
    """Median RGB of (x, y, w, h) on a uint8 RGBA image."""
    x, y, w, h = rect_px
    return tuple(int(v) for v in np.median(img[y : y + h, x : x + w, :3].reshape(-1, 3), axis=0))


def sprite_pixels(json_path: Path, name: str) -> np.ndarray:
    """uint8 RGBA crop of one sprite, for checks and composite previews."""
    json_path = Path(json_path)
    doc = json.loads(json_path.read_text(encoding="utf-8"))
    r = doc["sprites"][name]
    sheet = np.asarray(Image.open(json_path.parent / doc["image"]).convert("RGBA"))
    return sheet[r["y"] : r["y"] + r["h"], r["x"] : r["x"] + r["w"]].copy()


def determinism_errors(main, subdirs: list[str]) -> list[str]:
    """Runs a generator main(out_root) twice into temp dirs and compares SHA-256 of every file."""
    def digest(root: Path) -> dict[str, str]:
        return {str(p.relative_to(root)): hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(root.rglob("*")) if p.is_file()}

    with tempfile.TemporaryDirectory() as a, tempfile.TemporaryDirectory() as b:
        main(Path(a))
        main(Path(b))
        da, db = digest(Path(a)), digest(Path(b))
    errs = [f"nondeterministic: {k}" for k in sorted(set(da) | set(db)) if da.get(k) != db.get(k)]
    if not any(k.startswith(tuple(subdirs)) or any(s in k for s in subdirs) for k in da):
        errs.append("generator wrote nothing in its directories")
    return errs


# shipped bytes ----------------------------------------------------------------------------------------
# determinism_errors above renders twice into temp dirs and compares the two renders TO EACH OTHER, which says
# nothing about the files the engine loads: editing a generator without re-running it leaves the shipped asset
# stale with every gate green. That is the hole check_char.py documents in its own gate_shipped, and these two
# helpers close it for the atlas generators (hud, items, menu, particles, fx) and for a committed preview plate.

SHIPPED_TEXT_SUFFIXES = (".json",)


def _canon_shipped(data: bytes, suffix: str) -> bytes:
    """Line endings are a checkout policy (.gitattributes), not asset content.

    A committed .json that came back from git as CRLF on a core.autocrlf=true machine is not stale art, and a
    gate that calls it stale is red on every Windows checkout and green on Linux CI - which is how check_terrain
    spent the wave always-red. The eol=lf pins in .gitattributes are the fix; this is defence behind them, so a
    missing pin shows up as a git-attribute problem and not as a false staleness report. Binary output (PNG,
    GLB) is compared byte for byte, untouched."""
    return data.replace(b"\r\n", b"\n") if suffix.lower() in SHIPPED_TEXT_SUFFIXES else data


def shipped_errors(main, subdir: str, committed: Path) -> list[str]:
    """Renders main(out_root) into a temp dir and compares every file it wrote under subdir/ with `committed`.

    IF SEVERAL OF THESE GATES GO RED AT ONCE AFTER A TOOLCHAIN UPGRADE, READ THIS BEFORE REGENERATING ANY ART.
    This comparison is byte-exact on PNG, so it is sensitive to the PNG encoder, not only to the art: the same
    pixels re-encoded by a different Pillow or zlib are different bytes. previews/forge_sample.png is the proof
    that this happens - it decodes identically to a fresh render of forge.sample_sprite(), 0 of 92160 pixels
    differing, and is still 157005 bytes against the 154931 a fresh save_png writes here, because it was
    committed through an older zlib. Every committed-vs-fresh PNG byte comparison in this repository (terrain,
    iso, hud, items, menu, particles, fx, window, and the GLBs in check_char/check_actors/check_props) passes
    today only because the committed files were made by the current toolchain.

    So: a single gate red means stale art - re-run its generator and commit. Many gates red at once, right after
    a Pillow or numpy bump, means the ENCODER moved, not the art. The fix for that is to compare decoded pixels
    (shipped_image_errors already does, and says why) - NOT to regenerate the art, which would roll a toolchain
    upgrade into every committed asset and lose the ability to tell the two causes apart ever again."""
    errs: list[str] = []
    with tempfile.TemporaryDirectory() as d:
        main(Path(d))
        fresh_root = Path(d) / subdir
        if not fresh_root.is_dir():
            return [f"shipped: the generator wrote no {subdir}/ to compare with {committed}"]
        files = [p for p in sorted(fresh_root.rglob("*")) if p.is_file()]
        if not files:
            return [f"shipped: the generator wrote no files under {subdir}/"]
        for p in files:
            rel = p.relative_to(fresh_root)
            c = Path(committed) / rel
            if not c.exists():
                errs.append(f"{subdir}/{rel.as_posix()} is generated but not committed under {committed}")
                continue
            # the lengths and hashes reported are of what was actually compared, so a .json diagnostic is never
            # one CR per line out from the file on disk
            on_disk = _canon_shipped(c.read_bytes(), p.suffix)
            fresh = _canon_shipped(p.read_bytes(), p.suffix)
            if on_disk != fresh:
                errs.append(
                    f"{subdir}/{rel.as_posix()}: the committed file is {len(on_disk)} bytes, sha256 "
                    f"{hashlib.sha256(on_disk).hexdigest()[:12]}, but the generator makes {len(fresh)} bytes, "
                    f"sha256 {hashlib.sha256(fresh).hexdigest()[:12]} - re-run the generator and commit the asset")
    return errs


def shipped_image_errors(label: str, img, committed: Path) -> list[str]:
    """The same staleness gate for a single PNG a generator writes outside an atlas (a committed preview plate).

    Pixel-exact, not byte-exact, and deliberately so: previews/forge_sample.png decodes identically to a fresh
    render of forge.sample_sprite() (0 of 92160 pixels differ) and is still 157005 bytes against the 154931 a
    fresh save_png writes, because it was committed through an older zlib. A byte gate here would report a
    Pillow upgrade as stale art. The atlas output under dream/ui stays byte-exact (shipped_errors): those are
    the bytes the engine ships, and they are regenerated by the same chain that checks them."""
    committed = Path(committed)
    if not committed.exists():
        return [f"{label}: {committed} is not committed"]
    with tempfile.TemporaryDirectory() as d:
        fresh_path = Path(d) / committed.name
        save_png(img, fresh_path)
        fresh = np.asarray(Image.open(fresh_path).convert("RGBA"))
    on_disk = np.asarray(Image.open(committed).convert("RGBA"))
    if on_disk.shape != fresh.shape:
        return [f"{label}: the committed {committed.name} is {on_disk.shape[1]}x{on_disk.shape[0]} but the "
                f"generator makes {fresh.shape[1]}x{fresh.shape[0]} - re-run the generator and commit the plate"]
    d8 = np.abs(on_disk.astype(np.int32) - fresh.astype(np.int32))
    bad = int((d8.max(axis=2) > 0).sum())
    if bad:
        return [f"{label}: {bad} of {on_disk.shape[0] * on_disk.shape[1]} pixels of the committed "
                f"{committed.name} differ from a fresh render (worst channel delta {int(d8.max())}) - "
                f"re-run the generator and commit the plate"]
    return []
