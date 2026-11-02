"""Particle atlas for dream/ui/particles/: sparks, embers, dust motes, ash, two halos, flame tongues and a contact blob.

512x256 RGBA8, 8 columns by 4 rows of 64 px cells, frame 0 top-left, rows downward, loaded with no flip.
Every cell keeps a 4 px margin that is fully zero. RGB is premultiplied emission and stays meaningful
where A=0 (A=0 draws purely additive); A is coverage (ash, blob, flame; the flame's is scaled by the
instance alpha, so it is additive at night and occludes the sky by day). Frames 23-31 are reserved and empty.
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
    ("dust", 8, 4, "zero"),
    ("ash", 12, 4, "coverage"),
    ("halo", 16, 1, "zero"),
    ("halo.core", 17, 1, "zero"),
    ("flame", 18, 4, "coverage"),
    ("blob", 22, 1, "coverage"),
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

def spark_frame(i: int) -> np.ndarray:
    """One straight streak along +X, hot head at +u, tapered tail. The particle supplies angle and length."""
    r = uiart.rng(f"particles.spark.{i}")
    x, y = grid()
    head, tail = 21.0, 24.0
    t = np.where(x >= 0, x / head, x / tail)  # 0 at the middle, 1 at the head, -1 at the tail
    kink = r.uniform(0.3, 1.0) * np.sin(x * r.uniform(0.12, 0.2) + r.uniform(0, 6.283))
    taper = np.clip(1.0 - np.maximum(-t, 0.0), 0.0, 1.0) ** 0.8
    w = 5.0 * (0.3 + 0.7 * taper)
    d = (y - kink) / w
    along = 0.45 + 0.55 * smoothstep(-0.9, 0.7, t)  # hotter toward the head
    along *= smoothstep(1.0, 0.82, t)  # round off the head
    along *= smoothstep(-1.0, -0.6, t)
    out = np.exp(-d * d) * along
    for _ in range(i + 1):  # variants differ in flecks
        px, py = -r.uniform(0.3, 0.95) * tail, r.uniform(-4.5, 4.5)
        out = np.maximum(out, r.uniform(0.25, 0.5) * np.exp(-((x - px) ** 2 + (y - py) ** 2) / 1.6))
    out = np.clip(out, 0.0, 1.0) * smoothstep(26.5, 22.5, np.abs(x)) * smoothstep(26.0, 20.0, np.abs(y))
    tint = np.array([1.0, 0.97, 0.93], dtype=np.float32)  # near-neutral: the particle tint sets the colour
    return np.dstack((out[..., None] * tint, np.zeros_like(out)))


# embers -------------------------------------------------------------------------------------------

def ember_frame(i: int) -> np.ndarray:
    r = uiart.rng(f"particles.ember.{i}")
    x, y = grid()
    angle = r.uniform(0, 3.1416)
    ca, sa = np.cos(angle), np.sin(angle)
    stretch = r.uniform(1.3, 1.9)
    u, v = (x * ca + y * sa) / stretch, -x * sa + y * ca
    r0 = r.uniform(5.0, 7.5)
    warp = (noise(f"particles.ember.shape.{i}", 1.6) - 0.5) * 2.0
    d = np.hypot(u, v) / r0 + 0.45 * warp
    body = smoothstep(1.0, 0.35, d)
    crust = noise(f"particles.ember.crust.{i}", 0.7, 4)
    heat = smoothstep(0.3, 0.8, crust)
    inten = body * (0.55 + 0.45 * heat)
    glow = 0.07 * np.exp(-((x * x + y * y) / (2.0 * (1.5 * r0) ** 2)))
    v_ = np.clip(inten + glow, 0, None)
    v_ = v_ / max(float(v_.max()), 1e-6) * 0.98  # peak ~250; the particle colour carries the heat
    v_ *= window(x, y)
    tint = np.array([1.0, 0.97, 0.93], dtype=np.float32)
    return np.dstack((v_[..., None] * tint, np.zeros_like(v_)))


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
    cov = np.clip(cov, 0, 1) * window(x, y)
    cov = cov / max(float(cov.max()), 1e-6) * 0.84  # RGB peak about 215
    cold = np.array([0.93, 0.96, 1.0], dtype=np.float32)  # near-neutral, a touch cold
    return np.dstack((cov[..., None] * cold, np.zeros_like(cov)))


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
    soot = np.array([0.30, 0.28, 0.265], dtype=np.float32)  # sooty, a touch warmer than black; readable on dark
    tone = 0.6 + 0.4 * noise(f"particles.ash.tone.{i}", 0.8)
    return np.dstack((alpha[..., None] * soot * tone[..., None], alpha))


# halos --------------------------------------------------------------------------------------------

def halo_frame(core: bool) -> np.ndarray:
    x, y = grid()
    s = np.hypot(x, y) / (HALF - 1.0)
    if core:
        v = 0.62 * np.exp(-((s / 0.22) ** 2)) + 0.32 * np.exp(-((s / 0.50) ** 2))
    else:
        v = 0.46 * np.exp(-((s / 0.30) ** 2)) + 0.30 * np.exp(-((s / 0.62) ** 2))
    v = v * smoothstep(1.0, 0.8, s)  # no clip: the peak stays under 255, so there is no plateau
    return np.dstack((v[..., None] * np.ones(3, dtype=np.float32), np.zeros_like(v)))


# flame --------------------------------------------------------------------------------------------

def flame_frame(i: int) -> np.ndarray:
    """One tongue, base at the cell bottom, tip up. The colour ramp is baked; the particle tint is white."""
    r = uiart.rng(f"particles.flame.{i}")
    x, y = grid()
    h = (24.0 - y) / 48.0  # 0 at the base, 1 at the tip
    warp = (noise(f"particles.flame.warp.{i}", 1.4) - 0.5) * 2.0
    sway = 3.2 * np.sin(h * 3.4 + r.uniform(0, 6.283)) * h + 2.2 * warp * np.clip(h, 0, 1)
    half = 12.5 * np.clip(1.0 - h, 0.0, 1.0) ** 0.8 * (0.85 + 0.3 * np.sin(h * 3.1416)) * (1.0 + 0.2 * warp)
    d = np.abs(x - sway) / np.maximum(half, 0.4)
    body = smoothstep(1.0, -0.2, d) ** 1.1
    body *= smoothstep(-0.02, 0.1, h) * smoothstep(1.02, 0.85, h)
    body *= smoothstep(24.5, 21.0, y)  # exactly dark by the bottom margin ring
    lick = 0.85 + 0.15 * noise(f"particles.flame.lick.{i}", 0.9)
    e = np.clip(body * lick, 0.0, 1.0)
    edge, mid, core = (np.array(c, dtype=np.float32) for c in ((0.6, 0.16, 0.03), (1.0, 0.5, 0.12), (1.0, 0.82, 0.45)))
    k = e[..., None]
    ramp = np.where(k < 0.55, edge + (mid - edge) * (k / 0.55), mid + (core - mid) * np.clip((k - 0.55) / 0.45, 0, 1))
    rgb = ramp * (e ** 0.9)[..., None] * 0.96
    # Coverage follows the tongue and peaks at 0.8 in the core. The instance alpha scales it: 0 at
    # night (pure additive, as before), up to 0.8 by day so the flame occludes the bright sky. RGB
    # stays at or above A times the baked colour, so the occluding flame never reads dark.
    cover = 0.8 * (e / max(float(e.max()), 1e-6)) ** 0.9
    return np.dstack((np.clip(rgb, 0, 0.96), cover))


# blob ---------------------------------------------------------------------------------------------

def blob_frame() -> np.ndarray:
    """Contact shadow: black with soft coverage, so it darkens instead of adding."""
    x, y = grid()
    s = np.hypot(x, y) / (HALF - 1.0)
    a = smoothstep(1.0, 0.0, s) ** 1.4 * 0.92 * smoothstep(1.0, 0.8, s)
    return np.dstack((np.zeros((HI, HI, 3), dtype=np.float32), a))


# atlas --------------------------------------------------------------------------------------------

def frames() -> list[np.ndarray]:
    out = [spark_frame(i) for i in range(4)]
    out += [ember_frame(i) for i in range(4)]
    out += [dust_frame(i) for i in range(4)]
    out += [ash_frame(i) for i in range(4)]
    out += [halo_frame(False), halo_frame(True)]
    out += [flame_frame(i) for i in range(4)]
    out.append(blob_frame())
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
        "columns": COLUMNS, "rows": ROWS, "cell": CELL, "margin": MARGIN, "reserved": [23, 31],
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
