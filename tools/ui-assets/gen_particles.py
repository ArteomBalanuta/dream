"""Particle atlas for dream/ui/particles/: sparks, embers, dust motes, ash and two lamp halos.

512x256 RGBA8, 8 columns by 4 rows of 64 px cells, frame 0 top-left, rows downward, loaded with no flip.
Every cell keeps a 4 px margin that is fully zero. RGB is premultiplied emission and stays meaningful
where A=0 (A=0 draws purely additive); A is coverage. Frames 18-31 are reserved and empty.
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

import numpy as np
from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parent))
import uiart  # noqa: E402

COLUMNS, ROWS, CELL, MARGIN = 8, 4, 64, 4
INNER = CELL - 2 * MARGIN  # 56 px drawn area per cell
SS = 4  # supersampling per axis
HI = INNER * SS
HALF = INNER / 2

GROUPS = (
    ("spark", 0, 4, "zero"),
    ("ember", 4, 4, "zero"),
    ("dust", 8, 4, "low"),
    ("ash", 12, 4, "coverage"),
    ("halo", 16, 1, "zero"),
    ("halo.core", 17, 1, "zero"),
)


def smoothstep(e0: float, e1: float, x: np.ndarray) -> np.ndarray:
    t = np.clip((x - e0) / (e1 - e0), 0.0, 1.0)
    return t * t * (3 - 2 * t)


def grid() -> tuple[np.ndarray, np.ndarray]:
    """Pixel-centre coordinates of the hi-res cell in final px, origin at the cell centre, y down."""
    c = (np.arange(HI, dtype=np.float32) + 0.5) / SS - HALF
    return np.meshgrid(c, c)


def window(x: np.ndarray, y: np.ndarray, radius: float = HALF - 1.0) -> np.ndarray:
    """Radial fade that is exactly 0 before the drawn area ends, so no quad edge can show."""
    return smoothstep(radius, radius - 6.0, np.hypot(x, y))


def noise(name: str, scale_u: float, octaves: int = 3) -> np.ndarray:
    return uiart.fbm(HI, HI, name, scale_u, octaves)


def downsample(a: np.ndarray) -> np.ndarray:
    return a.reshape(INNER, SS, INNER, SS, *a.shape[2:]).mean(axis=(1, 3), dtype=np.float32)


# sparks -------------------------------------------------------------------------------------------

def streak(x, y, origin, angle, length, width, kink, r: np.random.Generator) -> np.ndarray:
    """One thin jagged streak, bright at the head (+u), pointed at both ends."""
    ca, sa = np.cos(angle), np.sin(angle)
    u = (x - origin[0]) * ca + (y - origin[1]) * sa
    v = -(x - origin[0]) * sa + (y - origin[1]) * ca
    t = u / length  # 0 at the origin, 1 at the head, -1 at the tail
    bend = np.zeros_like(u)
    for freq in (1.3, 2.9, 5.7):
        bend += kink / freq * np.sin(freq * 2.4 * t + r.uniform(0, 6.283)) * r.uniform(0.5, 1.0)
    taper = np.clip(1.0 - np.abs(t), 0.0, 1.0) ** 0.9
    w = np.maximum(width * taper, 0.05)
    d = (v - bend) / w
    along = 0.5 + 0.5 * smoothstep(-1.0, 0.45, t)  # hotter toward the head
    flicker = 0.8 + 0.2 * np.sin(t * r.uniform(7, 13) + r.uniform(0, 6.283))
    core = np.exp(-d * d)
    glow = 0.28 * np.exp(-(d * d) / 14.0)
    return (core + glow) * along * flicker * (np.abs(t) < 1.0)


def spark_frame(i: int) -> np.ndarray:
    r = uiart.rng(f"particles.spark.{i}")
    x, y = grid()
    angles = (-1.30, -0.62, 0.34, -2.20)
    angle = angles[i] + r.uniform(-0.12, 0.12)
    length = r.uniform(21.0, 25.0)
    out = streak(x, y, (0.0, 0.0), angle, length, r.uniform(1.0, 1.5), r.uniform(1.2, 2.2), r)
    # a short broken-off fork from the middle, thinner and dimmer
    side = 1.0 if r.random() < 0.5 else -1.0
    base = r.uniform(-0.1, 0.3) * length
    origin = (np.cos(angle) * base, np.sin(angle) * base)
    fork = streak(x, y, origin, angle + side * r.uniform(0.35, 0.6), r.uniform(8.0, 11.0), 0.8, 1.2, r)
    out = np.maximum(out, 0.7 * fork)
    # a few cooling flecks trailing behind the tail
    for _ in range(3):
        s = r.uniform(0.35, 0.95) * length
        px, py = -np.cos(angle) * s + r.uniform(-2, 2), -np.sin(angle) * s + r.uniform(-2, 2)
        out = np.maximum(out, r.uniform(0.25, 0.5) * np.exp(-((x - px) ** 2 + (y - py) ** 2) / 1.1))
    out = np.clip(out, 0.0, 1.0) * window(x, y)
    tint = np.array([1.0, 0.97, 0.93], dtype=np.float32)  # near-neutral: the particle tint sets the colour
    rgb = out[..., None] * tint
    return np.dstack((rgb, np.zeros_like(out)))


# embers -------------------------------------------------------------------------------------------

def ember_frame(i: int) -> np.ndarray:
    r = uiart.rng(f"particles.ember.{i}")
    x, y = grid()
    angle = r.uniform(0, 3.1416)
    ca, sa = np.cos(angle), np.sin(angle)
    stretch = r.uniform(1.2, 1.7)
    u, v = (x * ca + y * sa) / stretch, -x * sa + y * ca
    r0 = r.uniform(9.0, 12.5)
    warp = (noise(f"particles.ember.shape.{i}", 1.6) - 0.5) * 2.0
    d = np.hypot(u, v) / r0 + 0.55 * warp
    body = smoothstep(1.0, 0.5, d)
    crust = noise(f"particles.ember.crust.{i}", 0.7, 4)
    heat = smoothstep(0.3, 0.8, crust)
    inten = body * (0.3 + 0.7 * heat)
    glow = 0.1 * np.exp(-((x * x + y * y) / (2.0 * r0 * r0)))
    cold = np.array([0.36, 0.05, 0.02], dtype=np.float32)  # dull red crust
    warm = np.array([0.74, 0.17, 0.05], dtype=np.float32)  # glowing cracks, never white
    ramp = cold + (warm - cold) * np.clip(inten, 0, 1)[..., None]
    rgb = ramp * inten[..., None] + glow[..., None] * np.array([0.7, 0.14, 0.04], dtype=np.float32)
    rgb *= window(x, y)[..., None]
    return np.dstack((np.clip(rgb, 0, 1), np.zeros_like(inten)))


# dust ---------------------------------------------------------------------------------------------

def dust_frame(i: int) -> np.ndarray:
    r = uiart.rng(f"particles.dust.{i}")
    x, y = grid()
    angle = r.uniform(0, 3.1416)
    ca, sa = np.cos(angle), np.sin(angle)
    u, v = (x * ca + y * sa) / r.uniform(1.0, 1.5), -x * sa + y * ca
    r0 = r.uniform(5.0, 8.0)
    warp = (noise(f"particles.dust.shape.{i}", 1.0) - 0.5) * 2.0
    d = np.hypot(u, v) / r0 + 0.6 * warp
    cov = smoothstep(1.0, 0.2, d) ** 1.5
    for _ in range(4):  # loose grains drifting off the mote
        ang, dist = r.uniform(0, 6.283), r.uniform(0.9, 2.4) * r0
        gx, gy = np.cos(ang) * dist, np.sin(ang) * dist
        cov = np.maximum(cov, r.uniform(0.2, 0.45) * np.exp(-((x - gx) ** 2 + (y - gy) ** 2) / r.uniform(0.9, 2.2)))
    alpha = np.clip(cov, 0, 1) * r.uniform(0.22, 0.4) * window(x, y)
    cold = np.array([0.62, 0.65, 0.69], dtype=np.float32)  # cold grey
    return np.dstack((alpha[..., None] * cold, alpha))


# ash ----------------------------------------------------------------------------------------------

def ash_frame(i: int) -> np.ndarray:
    r = uiart.rng(f"particles.ash.{i}")
    x, y = grid()
    angle = r.uniform(0, 3.1416)
    ca, sa = np.cos(angle), np.sin(angle)
    u, v = x * ca + y * sa, -x * sa + y * ca
    # ragged flake: a radius that wanders with a few random harmonics, long and narrow, never round
    aspect = r.uniform(1.5, 2.1)
    theta = np.arctan2(v * aspect, u)
    radius = np.ones_like(theta)
    for k in (2, 3, 5, 7):
        radius += r.uniform(0.04, 0.2) / (k * 0.5) * np.cos(k * theta + r.uniform(0, 6.283))
    r0 = r.uniform(15.0, 20.0)
    dist = np.hypot(u, v * aspect) / (r0 * radius)
    erode = noise(f"particles.ash.erode.{i}", 0.55, 4)
    edge = dist + 0.45 * (erode - 0.5)
    cov = smoothstep(1.0, 0.55, edge)
    mottle = 0.55 + 0.45 * noise(f"particles.ash.mottle.{i}", 1.2)
    alpha = np.clip(cov * mottle * 1.25, 0, 0.9) * window(x, y)
    soot = np.array([0.075, 0.07, 0.066], dtype=np.float32)  # sooty, a touch warmer than black
    tone = 0.6 + 0.4 * noise(f"particles.ash.tone.{i}", 0.8)
    return np.dstack((alpha[..., None] * soot * tone[..., None], alpha))


# halos --------------------------------------------------------------------------------------------

def halo_frame(core: bool) -> np.ndarray:
    x, y = grid()
    s = np.hypot(x, y) / (HALF - 1.0)
    fall = np.clip(1.0 - s, 0, 1)
    if core:
        v = 0.9 * np.exp(-((s * HALF / 5.5) ** 2)) + 0.4 * fall**3.5
    else:
        v = 0.55 * fall**2.2 + 0.25 * np.exp(-((s * HALF / 9.0) ** 2))
    v = np.clip(v, 0, 1) * smoothstep(1.0, 0.8, s)
    return np.dstack((v[..., None] * np.ones(3, dtype=np.float32), np.zeros_like(v)))


# atlas --------------------------------------------------------------------------------------------

def frames() -> list[np.ndarray]:
    out = [spark_frame(i) for i in range(4)]
    out += [ember_frame(i) for i in range(4)]
    out += [dust_frame(i) for i in range(4)]
    out += [ash_frame(i) for i in range(4)]
    out += [halo_frame(False), halo_frame(True)]
    return out


def build() -> np.ndarray:
    sheet = np.zeros((ROWS * CELL, COLUMNS * CELL, 4), dtype=np.uint8)
    for index, hi in enumerate(frames()):
        cell = np.clip(downsample(hi.astype(np.float32)) * 255.0 + 0.5, 0, 255).astype(np.uint8)
        col, row = index % COLUMNS, index // COLUMNS
        x, y = col * CELL + MARGIN, row * CELL + MARGIN
        sheet[y : y + INNER, x : x + INNER] = cell
    return sheet


def main(out_root: Path) -> None:
    out = Path(out_root) / "particles"
    uiart.save_png(build(), out / "particles.png")
    frame_table = []
    for name, first, count, alpha in GROUPS:
        for k in range(count):
            frame_table.append({"index": first + k, "name": f"{name}.{k}" if count > 1 else name, "alpha": alpha})
    doc = {
        "version": 1, "image": "particles.png", "size": [COLUMNS * CELL, ROWS * CELL],
        "origin": "top-left", "yDown": True, "alpha": "premultiplied",
        "columns": COLUMNS, "rows": ROWS, "cell": CELL, "margin": MARGIN, "reserved": [18, 31],
        "frames": frame_table,
    }
    (out / "particles.json").write_text(json.dumps(doc, indent=1, sort_keys=True) + "\n", encoding="utf-8", newline="\n")


# previews -----------------------------------------------------------------------------------------

def preview(out_root: Path, previews: Path) -> None:
    """The used frames on dark stone, composited the way the pass blends them (premultiplied over)."""
    sheet = np.asarray(Image.open(Path(out_root) / "particles" / "particles.png").convert("RGBA")).astype(np.float32) / 255
    scale = 2
    base = np.array([0.10, 0.09, 0.085], dtype=np.float32)
    canvas = np.empty((ROWS * CELL, COLUMNS * CELL, 3), dtype=np.float32)
    canvas[:] = base
    canvas = sheet[..., :3] + canvas * (1.0 - sheet[..., 3:4])
    img = Image.fromarray((np.clip(canvas, 0, 1) * 255 + 0.5).astype(np.uint8), "RGB")
    Path(previews).mkdir(parents=True, exist_ok=True)
    img.resize((img.width * scale, img.height * scale), Image.NEAREST).save(Path(previews) / "particles.png", compress_level=6)


if __name__ == "__main__":
    here = Path(__file__).resolve().parent
    root = Path(sys.argv[1]) if len(sys.argv) > 1 else here.parents[1] / "dream" / "ui"
    main(root)
    preview(root, here / "previews")
