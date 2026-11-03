"""Baked surface tiles: writes terrain/<name>.png, terrain_manifest.json and terrain_sheet.png.

  gen_terrain.py [out_root]                       everything under out_root/terrain (default: this directory)
  gen_terrain.py --only name[,name] [out_root]    only those tiles, never the manifest or the sheet

One seamless power-of-two tile per terrain material and per grass role. Each tile is a height field built in the forge
form bands (primary 8 u and up, secondary 2..6 u, tertiary under 1 u, the 1..2 u gap left empty), baked to maps with
wrap on both axes, aged, and lit by a dome only: no directional key and no cast shadow. The engine applies its own
ambient and N.L and the sun moves, so only view-independent response is baked in: albedo, occlusion, cavity, wear,
grime and dust. DREAM_ART_WORKERS sets the process count (default min(4, cpu)).

The generator never writes under dream/. A separate copy step (see README) places <name>.png at
dream/animations/library/forge/<name>/<name>.albedo.png.
"""
from __future__ import annotations

import json
import os
import sys
from concurrent.futures import ProcessPoolExecutor
from dataclasses import dataclass
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
if str(HERE) not in sys.path:
    sys.path.insert(0, str(HERE))
import forge as F  # noqa: E402
import uiart as U  # noqa: E402

TILE_PX = 256                    # final tile edge, power of two
PERIOD_U = TILE_PX / U.SCALE     # 64 u: the tile repeat in forge units
DOME = F.Rig(lights=(), panels=(), ambient=(0.125, 0.125, 0.125))  # ambient x 8 is 1.0: albedo x occlusion, nothing else
NO_KEY = (0.0, 0.0, 1.0)         # straight up, so bake() skips the cast shadow entirely
# The tile is baked on a canvas MARGIN hi-res px larger on every side and cropped. The fields are periodic, so the
# margin holds the true neighbours. forge's wrap="xy" is not enough on its own: the wide occlusion pass resizes with a
# clamped edge and np.gradient is one-sided at the border, which seams the tile (measured: AO wrap-edge difference 3 to
# 5x an interior pair). The margin outreaches every filter in the chain (occlusion 104 px, age blur about 50 px).
MARGIN = 128
CHROMA = 0.55                    # palette chroma kept: the grim look is desaturated


@dataclass(frozen=True)
class Tile:
    base: str                    # sRGB hex, the body of the surface
    bare: str                    # what wear and convex edges expose
    accent: str                  # patches: moss, ash, lichen, frost
    accent_share: float          # 0..1 coverage of the accent patches
    a1: float                    # primary relief amplitude (u), wavelengths 8..30 u
    a2: float                    # secondary relief (u), facets 5 u and dents 3.5 u
    a3: float                    # tertiary pitting (u), under 1 u
    wear: float = 0.30
    grime: float = 0.50
    rust: float = 0.0
    dust: float = 0.10
    ridge: float = 0.0           # 0..1 how much of the primary relief is folded into cracks and ridges
    vary: float = 0.35           # albedo mottling strength
    exposure: float = 1.0


# Names are the shipped file stems. Albedo is held low and desaturated: the engine multiplies these straight into
# linear lighting, so a mid-grey photographic tile reads as daylight on every surface.
TILES: dict[str, Tile] = {
    "grass_top":    Tile("#2b3322", "#4b5438", "#1a2016", 0.35, 0.9, 0.35, 0.05, 0.25, 0.55, 0, 0.05, 0.0, 0.45),
    "grass_side":   Tile("#32291f", "#52463a", "#232b1b", 0.30, 0.8, 0.40, 0.06, 0.30, 0.60, 0, 0.08, 0.1, 0.40),
    "grass_bottom": Tile("#2a221b", "#443a30", "#1a1612", 0.25, 0.8, 0.40, 0.07, 0.25, 0.60, 0, 0.05, 0.0, 0.50),
    "dirt":         Tile("#3a2e25", "#5e5043", "#251d17", 0.30, 0.9, 0.40, 0.06, 0.30, 0.55, 0.03, 0.10, 0.1, 0.40),
    "stone":        Tile("#3b3b3a", "#66655f", "#262826", 0.30, 0.9, 0.55, 0.05, 0.45, 0.55, 0, 0.12, 0.6, 0.30),
    "sand":         Tile("#4a4238", "#6a5f50", "#35302a", 0.25, 0.7, 0.25, 0.05, 0.25, 0.65, 0, 0.18, 0.0, 0.30),
    "mud":          Tile("#2c241d", "#463b30", "#181410", 0.35, 0.9, 0.45, 0.07, 0.25, 0.65, 0.02, 0.04, 0.0, 0.50),
    "snow":         Tile("#5d6369", "#858a90", "#3d434a", 0.25, 0.7, 0.25, 0.04, 0.25, 0.70, 0, 0.20, 0.0, 0.30, 0.7),
    "rock":         Tile("#35322f", "#5f5a53", "#22201e", 0.30, 1.0, 0.60, 0.05, 0.50, 0.60, 0.02, 0.10, 0.8, 0.35),
    "grass_path":   Tile("#363826", "#554e3b", "#212a1a", 0.40, 0.8, 0.35, 0.05, 0.30, 0.55, 0, 0.08, 0.0, 0.45),
}
ORDER = tuple(TILES)


def _field(X, Y, seed: str, per, n: int, lo: float, hi: float) -> np.ndarray:
    return F.waves(X, Y, seed, n, lo, hi, period=per)


def render_hires(name: str) -> np.ndarray:
    """Hi-res float RGBA of one tile: (TILE_PX * SS) square, sRGB-encoded, periodic."""
    t = TILES[name]
    seed = "terrain." + name
    n = TILE_PX * U.SS
    xs = (np.arange(n + 2 * MARGIN, dtype=np.float32) - MARGIN + 0.5) / U.PPU
    X, Y = xs[None, :], xs[:, None]
    per = (PERIOD_U, PERIOD_U)
    # form: primary 8..30 u, secondary facets and dents 3.5..5 u, tertiary pitting 0.5 u. Nothing between 1 and 2 u.
    w1 = _field(X, Y, seed + ".p", per, 8, 8.0, 30.0)
    w1 = w1 * (1 - t.ridge) + (-np.abs(w1) * 1.4 + 0.4) * t.ridge
    sec = F.facet_plane(X, Y, seed + ".facet", 5.0, 0.30, period=per) + 0.8 * F.cell_dents(X, Y, seed + ".dent", 3.5, period=per)
    prim = (t.a1 * w1 + t.a2 * sec).astype(np.float32)
    h = prim + t.a3 * F.cell_dents(X, Y, seed + ".pit", 0.55, 0.9, period=per)
    m = F.bake(h, prim, NO_KEY)
    # albedo: body colour mottled at 8 u and up and grained under 1 u, accent patches, bare colour where wear takes it
    def lin(hx):
        c = U.token(hx)
        g = float(0.2126 * c[0] + 0.7152 * c[1] + 0.0722 * c[2])
        return U.srgb_to_linear(g + (c - g) * CHROMA).astype(np.float32)

    base, bare, accent = lin(t.base), lin(t.bare), lin(t.accent)
    mott = 0.5 + 0.5 * np.tanh(_field(X, Y, seed + ".mott", per, 10, 8.0, 24.0) * 0.9)
    grain = 0.5 + 0.5 * np.tanh(_field(X, Y, seed + ".grain", per, 8, 0.45, 0.95) * 0.8)
    k = 1.0 - t.vary + 2.0 * t.vary * (0.7 * mott + 0.3 * grain)
    patch = 0.5 + 0.5 * np.tanh(_field(X, Y, seed + ".patch", per, 8, 9.0, 28.0) * 1.2)
    pm = F.smooth(1.0 - t.accent_share - 0.12, 1.0 - t.accent_share + 0.12, patch)
    albedo = (base * (1 - pm[..., None]) + accent * pm[..., None]) * k[..., None]
    ones = np.ones_like(h)
    s = F.Surface(albedo.astype(np.float32), np.full(albedo.shape, 0.03, np.float32), 0.85 * ones, 0.0 * ones, 0.0 * ones, ones)
    s.bare_albedo = np.broadcast_to(bare, albedo.shape).astype(np.float32)
    s.bare_f0 = np.full(albedo.shape, 0.04, np.float32)
    s.bare_rough = 0.8 * ones
    s.wear_gain = ones
    s = F.age(s, m, seed, t.wear, t.grime, t.rust, t.dust, origin=(-MARGIN / U.PPU, -MARGIN / U.PPU), period=per)
    hdr = F.light(s, m, DOME)
    rgba = F.finish(hdr, s.alpha, t.exposure, None, bloom=0.0, grade=False)
    return np.ascontiguousarray(rgba[MARGIN : MARGIN + n, MARGIN : MARGIN + n])


def render_tile(name: str) -> np.ndarray:
    """The wrapped downsample of render_hires: uint8 TILE_PX x TILE_PX x 4."""
    return F.downsample(render_hires(name), wrap="xy")


def _job(name: str) -> np.ndarray:
    return render_tile(name)


def render_all(names: list[str]) -> dict:
    workers = int(os.environ.get("DREAM_ART_WORKERS", "0")) or min(4, os.cpu_count() or 1)
    workers = max(1, min(workers, len(names)))
    if workers == 1:
        res = [_job(n) for n in names]
    else:
        with ProcessPoolExecutor(max_workers=workers) as pool:
            res = list(pool.map(_job, names))
    return dict(zip(names, res))


def compose_sheet(tiles: list[np.ndarray]) -> np.ndarray:
    """Two rows of tiles, each repeated 2x2 so a seam would show as a visible grid line."""
    cols = (len(tiles) + 1) // 2
    pad = 8
    cell = 2 * TILE_PX
    sheet = np.full((2 * cell + 3 * pad, cols * cell + (cols + 1) * pad, 3), 200, dtype=np.uint8)
    for i, t in enumerate(tiles):
        r, c = divmod(i, cols)
        y, x = pad + r * (cell + pad), pad + c * (cell + pad)
        sheet[y : y + cell, x : x + cell] = np.tile(t[..., :3], (2, 2, 1))
    return sheet


def main(out_root: Path = HERE, only: list[str] | None = None) -> None:
    out = Path(out_root) / "terrain"
    if only:
        missing = [n for n in only if n not in TILES]
        if missing:
            raise SystemExit(f"unknown tiles: {missing}")
    names = [n for n in ORDER if not only or n in only]
    res = render_all(names)
    for n in names:
        U.save_png(res[n], out / f"{n}.png")
    if only:
        print("gen_terrain: wrote", ", ".join(names))
        return
    doc = {"version": 1, "tileSizePx": TILE_PX, "periodU": PERIOD_U, "straightAlpha": True, "wrap": "xy",
           "colorSpace": "srgb-encoded-sampled-as-linear", "sheet": "terrain_sheet.png",
           "tiles": [{"name": n, "image": f"{n}.png", "size": [TILE_PX, TILE_PX]} for n in names]}
    (out / "terrain_manifest.json").write_text(json.dumps(doc, indent=1) + "\n", encoding="utf-8", newline="\n")
    sheet = compose_sheet([res[n] for n in names])
    U.save_png(np.dstack((sheet, np.full(sheet.shape[:2], 255, dtype=np.uint8))), out / "terrain_sheet.png")
    print("gen_terrain: wrote", len(names), "tiles")


if __name__ == "__main__":
    args = sys.argv[1:]
    only_names = None
    if args and args[0] == "--only":
        only_names = args[1].split(",")
        args = args[2:]
    main(Path(args[0]) if args else HERE, only_names)
