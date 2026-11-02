"""Front-end art for dream: dream/ui/menu/menu.png + menu.json.

Original and procedural. Buttons, panel, slider and the title are forged through forge.py exactly like the
window chrome (relief -> bake -> age -> light -> finish -> downsample); the backdrop is painted directly with
numpy at atlas density: a moonlit, fogged valley with a ruined cathedral, a dead tree and a heavy vignette.

Mood: dark, grim, gothic. Blackened iron with a chamfered outline, a sunk face that glows ember when hovered,
a few dull brass accents. The title is cut from iron with a worn brass inlay.

Sprites (atlas scale 4 px/u):
  menu.backdrop               256 x 144 u   stretched / cropped to the window
  menu.title                  228 x 56 u    "DREAM"
  menu.button.{normal,hover,pressed,disabled}   128 x 20 u, nine-slice (6 u)
  menu.panel                  64 x 64 u     nine-slice (14 u)
  menu.slider.track           64 x 6 u      nine-slice (horizontal)
  menu.slider.knob            8 x 14 u
  menu.divider                120 x 6 u
"""
from __future__ import annotations

import math
import sys
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw

sys.path.insert(0, str(Path(__file__).resolve().parent))
import forge as fg  # noqa: E402
import gen_window as gw  # noqa: E402,F401  (registers the frame.* and rail.dim materials)
import uiart as ua  # noqa: E402
from forge import Ellipse, Poly, Rect  # noqa: E402
from uiart import PPU, SCALE, Atlas, Sprite  # noqa: E402

OUT_DIR = "menu"
F32 = np.float32
BTN_W, BTN_H = 128, 20
BTN_SLICE = 6  # u


# helpers ------------------------------------------------------------------------------


def chamfer_outline(w: float, h: float, c: float, inset: float = 0.0) -> list[tuple[float, float]]:
    x0, y0, x1, y1 = inset, inset, w - inset, h - inset
    return [(x0 + c, y0), (x1 - c, y0), (x1, y0 + c), (x1, y1 - c), (x1 - c, y1), (x0 + c, y1), (x0, y1 - c), (x0, y0 + c)]


def zat(r: fg.Relief, x: float, y: float) -> float:
    return float(r.h[min(max(int(y * PPU), 0), r.H_px - 1), min(max(int(x * PPU), 0), r.W_px - 1)])


# buttons ------------------------------------------------------------------------------


def render_button(state: str) -> np.ndarray:
    w, h = BTN_W, BTN_H
    name = "menu.button"
    r = fg.Relief(w, h, "frame.black", 0.0)
    pressed = state == "pressed"
    z = 0.35 if pressed else 1.2
    rim = "frame.cold" if state == "disabled" else "frame.iron"
    r.plate(Poly(chamfer_outline(w, h, 3.2, 0.25)), z=z, bevel=1.8, profile="rolled", rise=0.55, mat=rim,
            wobble=0.03, nick=0.006, seed=name + ".rim")
    face = Poly(chamfer_outline(w, h, 2.2, 2.2))
    r.pocket(face, depth=0.0 if pressed else 0.7, wall=0.7, mat="frame.black", seed=name + ".face")
    if pressed:
        r.h[...] = np.where(r.a > 0.5, r.h - 0.3, r.h)
    # a thin dull-brass line inlaid along the lower edge of the face, broken in the middle
    if state != "disabled":
        for x0, x1 in ((7.0, 52.0), (76.0, 121.0)):
            r.plate(Rect(x0, h - 4.6, x1 - x0, 0.7, 0.2), z=zat(r, w / 2, h / 2) + 0.05, bevel=0.3, profile="round",
                    mat="rail.dim", wobble=0.02, seed=f"{name}.inlay.{x0}", mode="max")
    for i, (x, y) in enumerate(((4.4, 4.4), (w - 4.4, 4.4), (4.4, h - 4.4), (w - 4.4, h - 4.4))):
        r.dome(x, y, 1.0, z=zat(r, x, y), mat="frame.rivet", seed=f"{name}.rv{i}", collar=True)
    exposure = {"normal": 1.0, "hover": 1.45, "pressed": 0.72, "disabled": 0.6}[state]
    img = r.render(name + "." + state, exposure=exposure, wear=0.07, grime=0.5, rust=0.08, dust=0.03, detail=0.5)
    # the sunk face: darker than the rim, glowing ember when hovered
    face_mask = (Poly(chamfer_outline(w, h, 2.2, 2.6)).sdf(r.xs[None, :], r.ys[:, None]) > 0).astype(F32)
    face_mask = fg.blur(face_mask, 0.5 * PPU)
    base = np.array((0.012, 0.011, 0.012), F32)
    glow = np.array((0.42, 0.17, 0.045), F32) if state == "hover" else np.array((0.0, 0.0, 0.0), F32)
    k = (0.62 if state != "disabled" else 0.8) * face_mask[..., None]
    rgb = img[..., :3] * (1 - k) + (base + glow * face_mask[..., None] * _vert(h_px=img.shape[0])) * k
    out = np.dstack((rgb, img[..., 3])).astype(F32)
    if state == "disabled":
        lum = out[..., :3].mean(axis=2, keepdims=True)
        out[..., :3] = out[..., :3] * 0.3 + lum * 0.7
    return fg.downsample(out)


def _vert(h_px: int) -> np.ndarray:
    """Ember glow rises from the bottom of the face: 1 at the bottom, 0.15 at the top."""
    t = np.linspace(0.15, 1.0, h_px, dtype=F32) ** 1.6
    return t[:, None, None]


# panel --------------------------------------------------------------------------------


def render_panel() -> np.ndarray:
    n = 64
    r = fg.Relief(n, n, "frame.black", 0.0)
    r.plate(Poly(chamfer_outline(n, n, 5.0, 0.3)), z=1.4, bevel=2.4, profile="rolled", rise=0.5, mat="frame.iron",
            wobble=0.05, nick=0.008, seed="panel.rim")
    inner = Poly(chamfer_outline(n, n, 3.0, 4.2))
    r.pocket(inner, depth=1.1, wall=0.9, mat="slab.dark", seed="panel.floor")
    # dull brass line, inset
    line = chamfer_outline(n, n, 2.2, 6.4)
    for i in range(len(line)):
        (xa, ya), (xb, yb) = line[i], line[(i + 1) % len(line)]
        r.plate(fg.Capsule([(xa, ya), (xb, yb)], 0.32), z=zat(r, n / 2, n / 2) + 0.02, bevel=0.3, profile="round",
                mat="rail.dim", wobble=0.02, seed=f"panel.line{i}", mode="max")
    # corner bosses and mid-edge studs
    spots = [(7.6, 7.6), (n - 7.6, 7.6), (7.6, n - 7.6), (n - 7.6, n - 7.6)]
    for i, (x, y) in enumerate(spots):
        r.dome(x, y, 1.5, z=zat(r, x, y), mat="frame.rivet", seed=f"panel.boss{i}", collar=True)
    img = r.render("menu.panel", exposure=1.0, wear=0.09, grime=0.6, rust=0.10, dust=0.04, detail=0.6)
    floor = (inner.sdf(r.xs[None, :], r.ys[:, None]) > 0.5).astype(F32)
    floor = fg.blur(floor, 0.3 * PPU)
    img[..., :3] = img[..., :3] * (1 - 0.68 * floor[..., None])
    return fg.downsample(img)


# slider ---------------------------------------------------------------------------------


def render_track() -> np.ndarray:
    w, h = 64, 6
    r = fg.Relief(w, h, "frame.black", 0.0)
    r.plate(Rect(0.2, 0.2, w - 0.4, h - 0.4, 1.4), z=0.9, bevel=1.2, profile="rolled", rise=0.6, mat="frame.iron",
            wobble=0.02, seed="track.rim")
    r.pocket(Rect(1.4, 1.4, w - 2.8, h - 2.8, 0.7), depth=0.8, wall=0.5, mat="void", seed="track.groove")
    img = r.render("menu.slider.track", exposure=0.9, wear=0.1, grime=0.5, rust=0.04, dust=0.02, detail=0.4)
    return fg.downsample(img)


def render_knob() -> np.ndarray:
    w, h = 8, 14
    r = fg.Relief(w, h, "frame.black", 0.0)
    r.plate(Poly([(4, 0.2), (7.8, 3.2), (7.8, 10.8), (4, 13.8), (0.2, 10.8), (0.2, 3.2)]), z=0.8, bevel=1.8,
            profile="rolled", rise=0.7, mat="frame.warm", wobble=0.02, nick=0.005, seed="knob.body")
    r.plate(Rect(1.6, 6.2, 4.8, 1.6, 0.4), z=zat(r, 4, 7) + 0.1, bevel=0.5, profile="round", mat="rail.dim",
            wobble=0.02, seed="knob.band", mode="max")
    img = r.render("menu.slider.knob", exposure=1.35, wear=0.14, grime=0.4, rust=0.05, dust=0.02, detail=0.4)
    return fg.downsample(img)


# divider --------------------------------------------------------------------------------


def render_divider() -> np.ndarray:
    w, h = 120, 6
    r = fg.Relief(w, h, "frame.black", 0.0)
    for side, (x0, x1) in enumerate(((2.0, 54.0), (66.0, 118.0))):
        r.plate(Rect(x0, 2.45, x1 - x0, 1.1, 0.5), z=0.5, bevel=0.55, profile="round", mat="rail.dim",
                wobble=0.02, seed=f"divider.line{side}")
    r.plate(Poly([(60, 0.2), (63.6, 3), (60, 5.8), (56.4, 3)]), z=0.7, bevel=1.4, profile="rolled", rise=0.7,
            mat="frame.warm", wobble=0.01, seed="divider.gem")
    r.plate(Poly([(60, 1.6), (61.8, 3), (60, 4.4), (58.2, 3)]), z=zat(r, 60, 3) + 0.05, bevel=0.5, profile="round",
            mat="glyph.ember", seed="divider.core", mode="max")
    img = r.render("menu.divider", exposure=1.1, wear=0.12, grime=0.4, rust=0.0, dust=0.02, detail=0.3)
    x = (r.xs[None, :] / w).astype(F32)
    img[..., 3] = img[..., 3] * np.clip(np.minimum(x, 1 - x) * 7.0, 0, 1)  # the lines fade out toward the ends
    return fg.downsample(img)


# title ------------------------------------------------------------------------------------

CAP = 36.0


def _spurred_stem(x: float, t: float = 7.0, spur: float = 3.0) -> list[tuple[float, float]]:
    """A vertical stem with wedge feet at top and bottom, left edge at x."""
    return [(x - spur, 0), (x + t, 0), (x + t, CAP), (x - spur, CAP), (x - spur, CAP - 3), (x, CAP - 6), (x, 6), (x - spur, 3)]


def letter_d(x: float):
    solid = [_spurred_stem(x)]
    solid.append([(x, 0), (x + 18, 0), (x + 28, 4), (x + 32, 12), (x + 32, 24), (x + 28, 32), (x + 18, CAP), (x, CAP)])
    holes = [[(x + 7, 7), (x + 15, 7), (x + 21, 10.5), (x + 24.5, 15), (x + 24.5, 21), (x + 21, 25.5), (x + 15, 29), (x + 7, 29)]]
    return solid, holes, 32


def letter_r(x: float):
    solid = [_spurred_stem(x)]
    solid.append([(x, 0), (x + 19, 0), (x + 28, 4), (x + 31, 10), (x + 31, 16), (x + 28, 22), (x + 19, 26), (x, 26)])
    solid.append([(x + 13, 24), (x + 22.5, 24), (x + 33, CAP), (x + 23.5, CAP)])
    solid.append([(x + 21, 33), (x + 36, 33), (x + 36, CAP), (x + 21, CAP)])
    holes = [[(x + 7, 7), (x + 17, 7), (x + 22, 10), (x + 24, 13), (x + 22, 16), (x + 17, 19), (x + 7, 19)]]
    return solid, holes, 36


def letter_e(x: float):
    solid = [_spurred_stem(x)]
    solid.append([(x, 0), (x + 28, 0), (x + 26, 6), (x + 22, 9), (x, 9)])
    solid.append([(x, 14.5), (x + 22, 14.5), (x + 24.5, 18), (x + 22, 21.5), (x, 21.5)])
    solid.append([(x, CAP), (x + 28, CAP), (x + 26, 30), (x + 22, 27), (x, 27)])
    return solid, [], 28


def letter_a(x: float):
    solid = [
        [(x + 14, 0), (x + 22, 0), (x + 10, CAP), (x, CAP)],
        [(x + 14, 0), (x + 22, 0), (x + 36, CAP), (x + 26, CAP)],
        [(x + 10.5, 23), (x + 25.5, 23), (x + 27, 28.5), (x + 9, 28.5)],
        [(x - 3, 33), (x + 10, 33), (x + 10, CAP), (x - 3, CAP)],
        [(x + 26, 33), (x + 39, 33), (x + 39, CAP), (x + 26, CAP)],
    ]
    return solid, [], 36


def letter_m(x: float):
    solid = [
        _spurred_stem(x),
        _spurred_stem(x + 37),
        [(x + 2, 0), (x + 11, 0), (x + 22, 22), (x + 33, 0), (x + 42, 0), (x + 26, 34), (x + 18, 34)],
    ]
    return solid, [], 44


def title_masks() -> tuple[np.ndarray, int, int]:
    """Boolean hi-res mask of the word and the canvas size in u."""
    gap = 8.0
    pad_x, pad_y = 9.0, 10.0
    letters = (letter_d, letter_r, letter_e, letter_a, letter_m)
    x = pad_x + 3.0
    placed = []
    for fn in letters:
        solid, holes, adv = fn(x)
        placed.append((solid, holes))
        x += adv + gap
    w_u = int(math.ceil(x - gap + 3.0 + pad_x - 0.0))
    h_u = int(CAP + 2 * pad_y)
    im = Image.new("L", (int(w_u * PPU), int(h_u * PPU)), 0)
    d = ImageDraw.Draw(im)
    for solid, holes in placed:
        for poly in solid:
            d.polygon([(px * PPU, (py + pad_y) * PPU) for px, py in poly], fill=255)
        for poly in holes:
            d.polygon([(px * PPU, (py + pad_y) * PPU) for px, py in poly], fill=0)
    return np.asarray(im) > 127, w_u, h_u


def render_title() -> np.ndarray:
    mask, w_u, h_u = title_masks()
    r = fg.Relief(w_u, h_u, "frame.black", 0.0)
    r.mask_plate(mask, z=1.0, bevel=2.6, profile="rolled", mat="frame.iron", rise=0.6)
    dist = np.maximum(ua.sdf(mask.astype(F32)), 0) / PPU
    inlay = dist > 2.5
    r.mask_plate(inlay, z=1.9, bevel=0.9, profile="round", mat="rail.dim", mode="set", rise=0.8)
    img = r.render("menu.title", exposure=0.95, wear=0.07, grime=0.8, rust=0.10, dust=0.04, detail=0.8)
    # ember heat low in the letters: the inlay glows faintly from underneath
    heat = fg.blur(inlay.astype(F32), 0.4 * PPU)
    ramp = np.linspace(0.0, 1.0, img.shape[0], dtype=F32)[:, None] ** 2.2
    img[..., :3] = np.clip(img[..., :3] + (heat * ramp)[..., None] * np.array((0.20, 0.07, 0.015), F32), 0, 1)
    img[..., :3] = np.where(inlay[..., None], img[..., :3] * 0.62, img[..., :3])
    return fg.downsample(img)


# backdrop ---------------------------------------------------------------------------------

BW, BH = 256 * SCALE, 144 * SCALE


def _noise1d(n: int, seed: str, octaves: int = 5, base: int = 4) -> np.ndarray:
    rg = ua.rng(seed)
    out = np.zeros(n, F32)
    amp, total = 1.0, 0.0
    for o in range(octaves):
        k = base * 2**o + 1
        pts = rg.random(k).astype(F32)
        out += amp * np.interp(np.linspace(0, k - 1, n), np.arange(k), pts)
        total += amp
        amp *= 0.52
    return out / total


def fbm_px(name: str, cell_px: float, octaves: int) -> np.ndarray:
    return ua.fbm(BH, BW, name, cell_px / PPU, octaves).astype(F32)


def _layer(draw_fn) -> np.ndarray:
    im = Image.new("L", (BW, BH), 0)
    draw_fn(ImageDraw.Draw(im))
    return np.asarray(im, dtype=F32) / 255.0


def _soft(a: np.ndarray, px: float) -> np.ndarray:
    return fg.blur(a, px)


def render_backdrop() -> np.ndarray:
    rg = ua.rng("menu.backdrop")
    ys = np.linspace(0, 1, BH, dtype=F32)[:, None]
    xs = np.linspace(0, 1, BW, dtype=F32)[None, :]

    # sky: near-black blue-green overhead, a dim sickly glow at the horizon
    top = np.array((0.014, 0.020, 0.030), F32)
    mid = np.array((0.045, 0.060, 0.072), F32)
    hor = np.array((0.15, 0.125, 0.110), F32)
    t = ys ** 1.2
    k1 = np.clip(t / 0.55, 0, 1)[..., None]
    k2 = np.clip((t - 0.55) / 0.45, 0, 1)[..., None]
    col = top + (mid - top) * k1 + (hor - mid) * k2
    img = np.repeat(col, BW, axis=1).astype(F32)

    # clouds: slow streaks that catch a little light
    cl2 = fbm_px("menu.cloud", 150, 5)
    band = np.exp(-(((ys - 0.30) / 0.22) ** 2))
    cloud = np.clip((cl2 - 0.45) * 2.4, 0, 1) * band
    img += cloud[..., None] * np.array((0.050, 0.060, 0.070), F32)

    # stars: sparse, mostly dim
    star = (rg.random((BH, BW)) > 0.9985).astype(F32) * (rg.random((BH, BW)).astype(F32) * 0.6 + 0.2)
    star = _soft(star, 0.6) * 5.0 * np.clip(1 - ys * 1.6, 0, 1)
    img += star[..., None] * np.array((0.55, 0.62, 0.72), F32)

    # moon: a pale disc behind thin cloud with a long halo
    mx, my = 0.20 * BW, 0.20 * BH
    X = np.arange(BW, dtype=F32)[None, :]
    Y = np.arange(BH, dtype=F32)[:, None]
    dm = np.sqrt((X - mx) ** 2 + (Y - my) ** 2)
    disc = np.clip((18.0 - dm) / 1.5, 0, 1)
    halo = np.exp(-dm / 55.0) * 0.42 + np.exp(-dm / 14.0) * 0.30
    surf = 0.80 + 0.20 * fbm_px("menu.moon", 8, 4)
    img += halo[..., None] * np.array((0.060, 0.082, 0.105), F32)
    img = img * (1 - disc[..., None]) + disc[..., None] * (surf[..., None] * np.array((0.62, 0.68, 0.72), F32))

    # distant mountain ridge, lit from behind by the horizon glow
    ridge_y = BH * (0.56 - 0.20 * (_noise1d(BW, "menu.ridge.far", 6, 3) - 0.4))
    far = _layer(lambda d: d.polygon([(0, BH)] + [(x, float(ridge_y[x])) for x in range(BW)] + [(BW, BH)], fill=255))
    far = _soft(far, 0.8)
    img = img * (1 - far[..., None]) + far[..., None] * np.array((0.040, 0.050, 0.060), F32)

    # fog behind the ruin
    fog_y = np.exp(-(((ys - 0.60) / 0.12) ** 2)).astype(F32)
    fogn = 0.55 + 0.45 * fbm_px("menu.fog.a", 220, 4)
    img += (fog_y * fogn)[..., None] * np.array((0.085, 0.092, 0.100), F32)

    # the ruined cathedral, off to the right, near-black with a faint ember in a few windows
    cx = 0.745 * BW
    s = BH / 576.0 * 0.80
    ground = 0.69 * BH

    def P(px, py):
        return (cx + px * s, ground + py * s)

    def spire(d, x, base, top, half, pin=True):
        d.polygon([P(x - half, base), P(x - half * 0.82, base - (base - top) * 0.28), P(x, top), P(x + half * 0.82, base - (base - top) * 0.28), P(x + half, base)], fill=255)
        if pin:
            for dx in (-half, half):
                d.polygon([P(x + dx - 4, base), P(x + dx, base - 34), P(x + dx + 4, base)], fill=255)

    def ruin(d):
        # west front, a narrow stepped mass
        d.polygon([P(-150, 0), P(-150, -200), P(-104, -224), P(-104, -330), P(-70, -330), P(-70, -226), P(70, -226),
                   P(70, -330), P(104, -330), P(104, -270), P(112, -200), P(154, -160), P(154, 0)], fill=255)
        # twin towers: tall, thin spires; the right one is snapped
        spire(d, -87, -330, -520, 22)
        d.polygon([P(70, -330), P(74, -392), P(86, -372), P(94, -410), P(104, -330)], fill=255)
        # central gable with a pinnacle
        d.polygon([P(-70, -226), P(0, -300), P(70, -226)], fill=255)
        d.polygon([P(-5, -296), P(0, -352), P(5, -296)], fill=255)
        # nave to the left, a broken roofline with a pointed clerestory
        d.polygon([P(-150, -160), P(-150, -188), P(-206, -194), P(-238, -168), P(-262, -214), P(-300, -166),
                   P(-352, -190), P(-392, -130), P(-410, -90), P(-410, 0), P(-150, 0)], fill=255)
        for k in range(4):
            x0 = -190 - k * 62
            d.polygon([P(x0, -160 + k * 8), P(x0 - 16, -160 + k * 8), P(x0 - 56, 0), P(x0 - 40, 0)], fill=255)
            spire(d, x0 - 8, -150 + k * 8, -226 + k * 8, 6, pin=False)
        # broken right wing
        d.polygon([P(154, 0), P(154, -156), P(182, -122), P(204, -148), P(228, -92), P(256, -108), P(286, -44), P(330, 0)], fill=255)

    body = _layer(ruin)
    body = _soft(body, 0.6)

    def lights(d):
        for lx, top in ((-136, -150), (-40, -150), (40, -150)):
            d.polygon([P(lx - 7, -112), P(lx - 7, top + 18), P(lx, top), P(lx + 7, top + 18), P(lx + 7, -112)], fill=255)
        d.polygon([P(-22, 0), P(-22, -56), P(0, -90), P(22, -56), P(22, 0)], fill=255)
        for lx in (-310, -248):
            d.polygon([P(lx - 6, -38), P(lx - 6, -70), P(lx, -84), P(lx + 6, -70), P(lx + 6, -38)], fill=255)

    lit = _layer(lights) * body
    vgrad = np.clip(0.35 + 0.65 * (ys - 0.45) / 0.25, 0.2, 1.0).astype(F32)
    flicker = 0.55 + 0.45 * fbm_px("menu.glow", 14, 3)
    ruin_col = np.array((0.010, 0.012, 0.016), F32)
    img = img * (1 - body[..., None]) + body[..., None] * ruin_col
    # cool moon light on the edges that face it
    shifted = np.roll(body, (3, 3), axis=(0, 1))
    rim = np.clip(body - shifted, 0, 1)
    img += _soft(rim, 0.5)[..., None] * np.array((0.050, 0.068, 0.088), F32)
    lit_k = (lit * vgrad * flicker)
    img += _soft(lit, 7.0)[..., None] * np.array((0.040, 0.014, 0.003), F32)
    img = img * (1 - lit[..., None] * 0.9) + (lit_k * 0.75)[..., None] * np.array((0.36, 0.13, 0.034), F32)

    # a dead tree, left foreground
    def tree(d):
        tx, ty = 0.10 * BW, 0.90 * BH
        rt = ua.rng("menu.tree")

        def branch(x, y, ang, ln, wd, depth):
            if depth == 0 or wd < 0.35:
                return
            nx, ny = x + math.cos(ang) * ln, y - math.sin(ang) * ln
            d.line([(x, y), (nx, ny)], fill=255, width=max(1, int(round(wd))))
            for _ in range(2 if depth > 2 else 1):
                branch(nx, ny, ang + (rt.random() - 0.5) * 1.5, ln * (0.66 + rt.random() * 0.14), wd * 0.68, depth - 1)

        branch(tx, ty, math.pi / 2 + 0.08, 0.22 * BH, 6.5, 7)
        d.polygon([(tx - 18, BH), (tx - 7, ty - 4), (tx + 8, ty - 6), (tx + 20, BH)], fill=255)

    tr = _soft(_layer(tree), 0.6)
    img = img * (1 - tr[..., None]) + tr[..., None] * np.array((0.006, 0.007, 0.009), F32)

    # leaning gravestones in the mid-ground, half sunk in fog
    def stones(d):
        rs = ua.rng("menu.stones")
        for i in range(15):
            sx = rs.random() * BW * 0.62 + BW * 0.06
            sy = ground + (6 + rs.random() * 42) * (BH / 576.0) * 2.2
            sz = 6 + (sy - ground) * 0.22
            lean = (rs.random() - 0.5) * 0.5
            pts = [(sx - sz * 0.55, sy), (sx - sz * 0.55 + lean * sz, sy - sz * 1.5), (sx + lean * sz, sy - sz * 1.85),
                   (sx + sz * 0.55 + lean * sz, sy - sz * 1.5), (sx + sz * 0.55, sy)]
            d.polygon(pts, fill=255)
            if i % 4 == 0:
                d.rectangle([sx + lean * sz - 1.2, sy - sz * 3.1, sx + lean * sz + 1.2, sy - sz * 1.6], fill=255)
                d.rectangle([sx + lean * sz - sz * 0.6, sy - sz * 2.6, sx + lean * sz + sz * 0.6, sy - sz * 2.3], fill=255)

    st = _soft(_layer(stones), 0.7)
    img = img * (1 - st[..., None]) + st[..., None] * np.array((0.007, 0.008, 0.010), F32)

    # ground: black earth under a bank of fog
    gy = np.clip((ys - 0.69) / 0.31, 0, 1)
    earth = np.array((0.008, 0.009, 0.011), F32)
    gmask = (ys > 0.69).astype(F32) * np.ones((1, BW), F32)
    gmask = _soft(gmask, 1.2)
    fog2 = (0.4 + 0.6 * fbm_px("menu.fog.b", 300, 4)) * np.exp(-(((ys - 0.72) / 0.10) ** 2))
    img = img * (1 - gmask[..., None]) + gmask[..., None] * (earth + gy[..., None] * 0.0)
    img += (fog2 * (0.5 + 0.5 * (1 - gy)))[..., None] * np.array((0.080, 0.086, 0.094), F32)

    # vignette, strong, slightly warm at the centre of the room
    vx = (xs - 0.5) * 2.0
    vy = (ys - 0.52) * 2.0
    vig = np.clip(1.0 - (vx ** 2 * 0.62 + vy ** 2 * 0.95) * 0.82, 0, 1) ** 1.35
    img = img * (0.10 + 0.90 * vig)[..., None]
    img *= np.array((1.02, 1.0, 0.985), F32)

    # film grain
    img += (rg.standard_normal((BH, BW, 1)).astype(F32)) * 0.006
    rgb = np.clip(ua.linear_to_srgb(np.clip(img * 1.0, 0, 1)) * 255.0 + 0.5, 0, 255).astype(np.uint8)
    out = np.dstack((rgb, np.full((BH, BW), 255, np.uint8)))
    return out


# build -------------------------------------------------------------------------------------


def all_sprites() -> list[Sprite]:
    out: list[Sprite] = []
    out.append(Sprite("menu.backdrop", render_backdrop()))
    out.append(Sprite("menu.title", render_title()))
    sl = BTN_SLICE * SCALE
    for state in ("normal", "hover", "pressed", "disabled"):
        out.append(Sprite(f"menu.button.{state}", render_button(state), slice=(sl, sl, sl, sl)))
    p = 14 * SCALE
    out.append(Sprite("menu.panel", render_panel(), slice=(p, p, p, p)))
    t = 12 * SCALE
    out.append(Sprite("menu.slider.track", render_track(), slice=(t, 8, t, 8)))
    out.append(Sprite("menu.slider.knob", render_knob()))
    out.append(Sprite("menu.divider", render_divider()))
    return out


def main(out_root: Path) -> None:
    atlas = Atlas("menu", Path(out_root) / OUT_DIR, meta={"source": "tools/ui-assets/gen_menu.py"})
    for s in all_sprites():
        atlas.add(s)
    atlas.save()


if __name__ == "__main__":
    main(Path(sys.argv[1]) if len(sys.argv) > 1 else Path(__file__).resolve().parents[2] / "dream" / "ui")
