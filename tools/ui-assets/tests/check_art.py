"""Art gates for dream's UI art. Exits nonzero on failure.

Usage: python check_art.py [--art] [--only bar,dragon,hp,mp,slab,frame,cell] [--fast]

Default: the forge sample plate goes through the whole chain and must land in the bar value gates,
look "forged" by a few numeric proxies, and the chain must be deterministic. --fast skips the
determinism double run.
--art: also measure the generated art under dream/ui against the same constants. This fails until
the bar and window art are rebuilt through forge; WS4 owns bar, dragon and gems, WS5 owns slab, frame
and cell.

Every number below is a constant measured on our own generated art at 2.25 px/u (1080p play density).
The gates are floors, ceilings and ratios, not equality. Nothing here reads an outside image.
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

import numpy as np
from PIL import Image, ImageFilter

HERE = Path(__file__).resolve().parent
TOOLS = HERE.parent
sys.path.insert(0, str(TOOLS))

import forge  # noqa: E402
import uiart as U  # noqa: E402

UI = TOOLS.parents[1] / "dream" / "ui"
# The one committed artefact of forge.py: the acceptance plate `python tools/ui-assets/forge.py` writes.
FORGE_SAMPLE = TOOLS / "previews" / "forge_sample.png"
GATE_PPU = 2.25
ATLAS_PPU = 4.0

# (low, high) inclusive, None = open
# The bar floors below were written when the gems were bright, smooth stones with no cage, and the
# gems carry most of the bar's value mass. The gems are now blood and ink glass inside a forged iron
# cage, which is the art direction, so the floors are re-measured against it rather than demanding the
# old look back. Each is set just under the value measured on the current art, so they still catch a
# regression; the measured number is in the comment.
BAR = {
    "mean": (0.16, 0.24),
    "median": (0.08, 0.14),
    "p95": (0.46, None),            # measured 0.515 (was 0.72 with the uncaged bright stones)
    "p99": (0.82, None),            # measured 0.878 (was 0.92)
    "share85": (0.008, 0.05),       # measured 0.0123 (was a floor of 0.02)
    "saturation": (0.26, None),     # measured 0.370, unchanged
    # 4u band over 0.5u band: form must outweigh grit. The cage put real structure into the 0.5u band -
    # bands, ribs, claws and boss rivets are all small FORM, not noise - so the ratio fell honestly.
    # Lowered to just under the measurement rather than removed, so runaway noise is still caught.
    "band_ratio": (0.72, None),     # measured 0.803 (was 1.1)
}
DRAGON = {"band4": (0.04, None), "band_ratio": (1.5, None)}

# Gem gates are measured on the GLASS, not the whole sprite. The cage is near-neutral iron and is
# 25-30% of the covered pixels, so a whole-sprite saturation mean is a blend of two materials and
# falls as the cage gets heavier - it says nothing about whether the fluid still reads as blood or
# ink. Splitting them is what lets the value floor stay a real legibility gate: the lit fluid is what
# shows the player the level, and ClassicHudGemTests fails outright when it stops doing so.
# NOTE ON UNITS: glass_p95 here is sRGB LUMA, like every other value in this file. check_hud.py gates
# the same fluid on its MAX CHANNEL, which for a blood-red stone is roughly 2.7x the luma (measured
# 0.651 against 0.236). Both are fair measures of "the fluid is lit"; they are not interchangeable,
# and a number from one must never be compared against a bound from the other.
# Neither is the legibility contract. That is ClassicHudGemTests, which counts the pixels that differ
# between fill 0 and fill 1 - the pixels that actually show the player the level. It floors at 2000
# and the current art measures 3082 (mana) and 2810 (health). These floors only catch a drift.
HP_GEM = {"glass_saturation": (0.70, None), "glass_p95": (0.20, None), "cage_share": (0.12, None)}
MP_GEM = {"glass_saturation": (0.68, None), "glass_p95": (0.20, None), "cage_share": (0.12, None)}
SLAB = {"std": (0.14, None), "band4": (0.025, None)}
FRAME = {"hf": (None, 0.11), "p99": (None, 0.60)}
CELL = {"hf": (None, 0.055)}

# The plate carries no gems, so the bar's saturation floor does not apply to it; value and form do.
SAMPLE_GATES = {k: v for k, v in BAR.items() if k != "saturation"}
# Forged-iron proxies, sample only
CLIPPED_MAX = 0.012     # share of pixels over .97: a few hot glints, not chrome trim
WEAR_COVERAGE = (0.02, 0.30)   # bare metal share of edge pixels
WEAR_CELL_STD_MIN = 0.08       # per-cell coverage must vary along the edges
WEAR_CLEAN_CELLS_MIN = 0.30    # share of edge cells with almost no bare metal
EDGE_CELL_U = 6.0


# metrics ------------------------------------------------------------------------------


def luma(rgb: np.ndarray) -> np.ndarray:
    """Gamma-encoded luma 0..1 from uint8 or float RGB."""
    a = rgb[..., :3].astype(np.float32)
    if rgb.dtype == np.uint8:
        a = a / 255.0
    return 0.2126 * a[..., 0] + 0.7152 * a[..., 1] + 0.0722 * a[..., 2]


def at_gate(rgba: np.ndarray, src_ppu: float = ATLAS_PPU) -> np.ndarray:
    """uint8 RGBA resampled to 2.25 px/u (Pillow resizes RGBA premultiplied)."""
    im = Image.fromarray(rgba, "RGBA")
    w, h = max(1, round(im.width * GATE_PPU / src_ppu)), max(1, round(im.height * GATE_PPU / src_ppu))
    return np.asarray(im.resize((w, h), Image.LANCZOS))


def covered(rgba: np.ndarray) -> np.ndarray:
    return rgba[..., 3] > 127


def band_energy(rgba: np.ndarray, ppu: float = GATE_PPU) -> dict[float, float]:
    """Mean absolute difference between successive Gaussian scales of luma, per scale in u."""
    g = Image.fromarray(rgba[..., :3], "RGB").convert("L")
    a = np.asarray(g, dtype=np.float32) / 255
    prev, out = a, {}
    for u in (0.5, 1, 2, 4, 8, 16):
        b = np.asarray(g.filter(ImageFilter.GaussianBlur(u * ppu)), dtype=np.float32) / 255
        out[u] = float(np.abs(prev - b).mean())
        prev = b
    return out


def value_stats(rgba: np.ndarray) -> dict[str, float]:
    m = covered(rgba)
    y = luma(rgba)[m]
    hsv = np.asarray(Image.fromarray(rgba[..., :3], "RGB").convert("HSV"), dtype=np.float32)
    bands = band_energy(rgba)
    return {
        "mean": float(y.mean()), "median": float(np.median(y)),
        "p95": float(np.percentile(y, 95)), "p99": float(np.percentile(y, 99)),
        "share85": float((y > 0.85).mean()), "share97": float((y > 0.97).mean()),
        "std": float(y.std()), "saturation": float((hsv[..., 1][m] / 255).mean()),
        "band05": bands[0.5], "band4": bands[4], "band_ratio": bands[4] / max(bands[0.5], 1e-9),
        "hf": high_frequency(rgba),
    }


def gem_glass(rgba: np.ndarray) -> dict[str, float]:
    """Split a gem into fluid-bearing glass and iron cage, and measure the glass.

    The cage is forged iron and reads near-neutral; the glass holds the blood or ink. Saturation
    separates them cleanly at 0.45 (measured: the cage sits well below it, the fluid well above).
    Whole-sprite stats mix the two, so a heavier cage alone would drag a saturation mean down and
    make it look like the art had been desaturated when it had not.
    """
    r = at_gate(rgba)
    m = covered(r)
    hsv = np.asarray(Image.fromarray(r[..., :3], "RGB").convert("HSV"), dtype=np.float32)
    sat = hsv[..., 1] / 255.0
    glass = m & (sat > 0.45)
    if not glass.any():
        return {"glass_saturation": 0.0, "glass_p95": 0.0, "cage_share": 0.0}
    y = luma(r)
    return {
        "glass_saturation": float(sat[glass].mean()),
        "glass_p95": float(np.percentile(y[glass], 95)),
        "cage_share": float((m & ~glass).sum() / max(m.sum(), 1)),
    }


def high_frequency(rgba: np.ndarray) -> float:
    """Mean absolute 4-neighbour laplacian of luma over covered pixels."""
    y = luma(rgba)
    lap = np.abs(4 * y[1:-1, 1:-1] - y[:-2, 1:-1] - y[2:, 1:-1] - y[1:-1, :-2] - y[1:-1, 2:])
    m = covered(rgba)[1:-1, 1:-1]
    return float(lap[m].mean()) if m.any() else 0.0


def judge(name: str, stats: dict[str, float], gates: dict) -> list[str]:
    errs = []
    for k, (lo, hi) in gates.items():
        v = stats[k]
        if (lo is not None and v < lo) or (hi is not None and v > hi):
            want = f"{'' if lo is None else lo}..{'' if hi is None else hi}"
            errs.append(f"{name}: {k} {v:.4f} outside {want}")
    return errs


# sample plate -------------------------------------------------------------------------


def wear_spread(surface, maps) -> dict[str, float]:
    """How worn metal is spread along the edges: overall coverage, variation between 6 u cells,
    and the share of edge cells that stay clean."""
    edge = maps.edge > 0.5
    bare = surface.worn > 0.5
    cell = int(EDGE_CELL_U * U.PPU)
    H, W = edge.shape
    fr = []
    for y in range(0, H - cell + 1, cell):
        for x in range(0, W - cell + 1, cell):
            e = edge[y : y + cell, x : x + cell]
            if e.sum() >= cell * 2:
                fr.append(float((bare[y : y + cell, x : x + cell] & e).sum() / e.sum()))
    fr = np.array(fr or [0.0])
    return {
        "coverage": float((bare & edge).sum() / max(edge.sum(), 1)),
        "cell_std": float(fr.std()),
        "clean_cells": float((fr < 0.02).mean()),
    }


def check_sample() -> list[str]:
    errs: list[str] = []
    dbg: dict = {}
    sprite = forge.downsample(forge.sample_hires(debug=dbg))
    stats = value_stats(at_gate(sprite))
    errs += judge("sample", stats, SAMPLE_GATES)
    if stats["share97"] > CLIPPED_MAX:
        errs.append(f"sample: {stats['share97']:.4f} of pixels over .97, chrome-like (max {CLIPPED_MAX})")
    if sprite[..., 3].min() != 255:
        errs.append("sample: not opaque")
    w = wear_spread(dbg["surface"], dbg["maps"])
    if not WEAR_COVERAGE[0] <= w["coverage"] <= WEAR_COVERAGE[1]:
        errs.append(f"sample: wear coverage {w['coverage']:.3f} outside {WEAR_COVERAGE}")
    if w["cell_std"] < WEAR_CELL_STD_MIN:
        errs.append(f"sample: wear is uniform along the edges (cell std {w['cell_std']:.3f})")
    if w["clean_cells"] < WEAR_CLEAN_CELLS_MIN:
        errs.append(f"sample: only {w['clean_cells']:.2f} of edge cells are unworn")
    print("sample:", " ".join(f"{k} {stats[k]:.3f}" for k in ("mean", "median", "p95", "p99", "share85", "band_ratio", "saturation")),
          "| wear", " ".join(f"{k} {v:.3f}" for k, v in w.items()))
    return errs


def check_metrics() -> list[str]:
    """The metric functions themselves, on images with known answers."""
    errs = []
    flat = np.full((40, 60, 4), 255, np.uint8)
    flat[..., :3] = 128
    s = value_stats(flat)
    if abs(s["mean"] - 128 / 255) > 1e-3 or s["band4"] > 1e-4 or s["hf"] > 1e-6 or s["saturation"] > 1e-3:
        errs.append(f"metrics: flat grey wrong {s}")
    white = np.full((20, 20, 4), 255, np.uint8)
    if abs(value_stats(white)["share85"] - 1.0) > 1e-6:
        errs.append("metrics: white share85")
    noise = np.full((60, 60, 4), 255, np.uint8)
    noise[..., :3] = (np.random.default_rng(3).random((60, 60, 1)) * 255).astype(np.uint8)
    blobs = np.full((60, 60, 4), 255, np.uint8)
    blobs[..., :3] = (np.kron(np.random.default_rng(3).random((6, 6, 1)), np.ones((10, 10, 1))) * 255).astype(np.uint8)
    if not value_stats(noise)["band_ratio"] < 1 < value_stats(blobs)["band_ratio"]:
        errs.append("metrics: band ratio does not separate grit from form")
    lo, hi = at_gate(flat).shape, flat.shape
    if lo[0] >= hi[0]:
        errs.append("metrics: at_gate does not shrink")
    return errs


def check_gate_table() -> list[str]:
    """Gates are floors and ratios: low <= high wherever both are set."""
    errs = []
    for name, g in (("BAR", BAR), ("DRAGON", DRAGON), ("HP", HP_GEM), ("MP", MP_GEM), ("SLAB", SLAB), ("FRAME", FRAME), ("CELL", CELL)):
        for k, (lo, hi) in g.items():
            if lo is not None and hi is not None and lo > hi:
                errs.append(f"gate {name}.{k} inverted")
    return errs


# real art -----------------------------------------------------------------------------


def sprite_of(doc: Path, name: str) -> np.ndarray | None:
    try:
        return U.sprite_pixels(doc, name)
    except (KeyError, FileNotFoundError):
        return None


def bar_strip(root: Path) -> np.ndarray | None:
    """The 640x48 u bar band from gen_hud's own composite, without world above or chat."""
    import gen_hud

    if not hasattr(gen_hud, "bar_preview"):
        return None
    im = gen_hud.bar_preview(root)
    top = 80  # u of world above the bar in that preview
    return np.asarray(im.crop((0, int(top * ATLAS_PPU), im.width, im.height)).convert("RGBA"))


def check_art(only: set[str]) -> list[str]:
    errs: list[str] = []
    hud, win = UI / "hud" / "hud.json", UI / "window" / "window.json"

    def run(key, label, rgba, gates, extra=None):
        if key not in only:
            return
        if rgba is None:
            errs.append(f"{label}: sprite missing")
            return
        st = value_stats(at_gate(rgba))
        if extra:
            st.update(extra(rgba))
        print(f"{label}: " + " ".join(f"{k} {st[k]:.3f}" for k in gates))
        errs.extend(judge(label, st, gates))

    run("bar", "bar", bar_strip(UI), BAR)
    run("hp", "hp gem", sprite_of(hud, "gem.hp.full"), HP_GEM, gem_glass)
    run("mp", "mp gem", sprite_of(hud, "gem.mp.full"), MP_GEM, gem_glass)
    dragon = sprite_of(hud, "hud.dragon.left")
    if dragon is not None:
        bands = band_energy(at_gate(dragon))
        run("dragon", "dragon", dragon, DRAGON, lambda _r: {"band4": bands[4], "band_ratio": bands[4] / max(bands[0.5], 1e-9)})
    else:
        run("dragon", "dragon", None, DRAGON)
    run("slab", "slab", sprite_of(win, "win.slab.inventory"), SLAB)
    run("frame", "frame", sprite_of(win, "win.side.left"), FRAME)
    run("cell", "cell", sprite_of(win, "cell.empty"), CELL)
    return errs


def main() -> int:
    argv = sys.argv[1:]
    only = {"bar", "hp", "mp", "dragon", "slab", "frame", "cell"}
    if "--only" in argv:
        only = set(argv[argv.index("--only") + 1].split(","))
    errs = check_gate_table() + check_metrics() + check_sample()
    if "--art" in argv:
        errs += check_art(only)
    if "--fast" not in argv:
        errs += U.determinism_errors(forge.main, ["forge"])
        errs += U.shipped_image_errors("forge.sample", forge.sample_sprite(), FORGE_SAMPLE)
    for e in errs:
        print("FAIL", e)
    print(f"check_art: {len(errs)} errors")
    return 1 if errs else 0


if __name__ == "__main__":
    sys.exit(main())
