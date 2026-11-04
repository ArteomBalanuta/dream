"""Checks tools/ui-assets/terrain/: the baked surface tiles. Exits nonzero on failure.

Usage: python check_terrain.py [--fast] [--measure]

  --fast     skip the determinism renders (every tile rendered twice more and compared with the committed files)
  --measure  print every metric for every tile and exit, to calibrate the constants below

Gates are floors, ceilings and ratios on our own generated tiles; nothing here reads an outside image. Every gate has a
synthetic negative that must fail it (self_test). Do not loosen a constant to pass: fix the art.
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

import numpy as np
from PIL import Image

HERE = Path(__file__).resolve().parent
TOOLS = HERE.parent
sys.path.insert(0, str(TOOLS))
import gen_terrain  # noqa: E402
import uiart  # noqa: E402

TERRAIN = TOOLS / "terrain"
NAMES = tuple(gen_terrain.TILES)
PPU = float(uiart.SCALE)  # final px per u

GATES = {
    "seam_ratio_max": 1.20,      # wrap-edge mean abs difference over that of the interior neighbour pairs beside it, per axis
    "quad_spread_max": 0.20,     # (max - min) of the four quadrant mean lumas over the tile mean: no baked direction
    "lowfreq_max": 0.12,         # share of luma AC energy at wavelengths >= H/2 (cycles per tile <= 2)
    "contrast_min": 0.012,        # mean 5x5 window std of luma, rejects flat fills
    "form_ratio_min": 0.40,      # band energy 4 u over 0.5 u: big shapes read before texture
    "sat_p95_max": 0.35,         # HSV S on pixels with V >= .10
    "chrome_max": 0.002,         # share of pixels over V .85
    "p5_v_max": 0.17,            # the shadows are crushed
    "p99_v_min": 0.25,           # something catches the light
    "mean_v_max": 0.34,          # the photographic tiles these replace measure .37 to .65
}
# Add an entry to hold one tile to its own numbers. An override is a claim that the art needs it: say why, with the
# measured value, and keep it as tight as the measurement allows.
OVERRIDES: dict[str, dict] = {
    # Soil, mud and fractured rock are grain-first by design (fine clods, pebbles, chips), so their 4 u over 0.5 u band
    # ratio sits below the default. Measured 2026-11: grass_side .215, grass_bottom .234, dirt .235, mud .336, rock .280.
    # Their big shapes come from albedo mottling and the 8 u fragments, not from grain-free relief.
    "grass_side": {"form_ratio_min": 0.18},
    "grass_bottom": {"form_ratio_min": 0.18},
    "dirt": {"form_ratio_min": 0.18},
    "mud": {"form_ratio_min": 0.25},
    "rock": {"form_ratio_min": 0.23},
}


def cfg(name: str) -> dict:
    c = dict(GATES)
    c.update(OVERRIDES.get(name, {}))
    return c


# metrics ---------------------------------------------------------------------------------------------


def luma(rgb: np.ndarray) -> np.ndarray:
    a = rgb[..., :3].astype(np.float32) / 255.0
    return 0.2126 * a[..., 0] + 0.7152 * a[..., 1] + 0.0722 * a[..., 2]


def hsv(rgb: np.ndarray) -> np.ndarray:
    return np.asarray(Image.fromarray(np.ascontiguousarray(rgb[..., :3]), "RGB").convert("HSV"), dtype=np.float32) / 255.0


def wrap_blur(a: np.ndarray, sigma: float) -> np.ndarray:
    """Gaussian blur that wraps on both axes (frequency domain), so a tile blurs like an endless field."""
    fy = np.fft.fftfreq(a.shape[0])[:, None]
    fx = np.fft.fftfreq(a.shape[1])[None, :]
    k = np.exp(-2 * np.pi ** 2 * sigma ** 2 * (fx * fx + fy * fy))
    return np.fft.ifft2(np.fft.fft2(a) * k).real.astype(np.float32)


def seam_ratio(y: np.ndarray) -> float:
    """Worst axis: the wrap-edge mean abs difference over the mean abs difference of the two interior neighbour pairs
    that sit against the same edge. Local on purpose: a few big features cross the edge and set the scale of both."""
    out = []
    for ax in (0, 1):
        v = np.moveaxis(y, ax, 0)
        edge = np.abs(v[0] - v[-1]).mean()
        inner = 0.5 * (np.abs(v[1] - v[0]).mean() + np.abs(v[-1] - v[-2]).mean())
        out.append(float(edge / max(inner, 1e-9)))
    return max(out)


def quad_spread(y: np.ndarray) -> float:
    h, w = y.shape[0] // 2, y.shape[1] // 2
    q = [y[:h, :w].mean(), y[:h, w:].mean(), y[h:, :w].mean(), y[h:, w:].mean()]
    return float((max(q) - min(q)) / max(float(y.mean()), 1e-6))


def lowfreq_share(y: np.ndarray) -> float:
    """Share of AC energy at wavelengths of at least half the tile: at most 2 cycles per tile on either axis."""
    f = np.abs(np.fft.fft2(y - y.mean())) ** 2
    ky = np.abs(np.fft.fftfreq(y.shape[0]) * y.shape[0])[:, None]
    kx = np.abs(np.fft.fftfreq(y.shape[1]) * y.shape[1])[None, :]
    low = (np.hypot(kx, ky) <= 2.0)
    return float(f[low].sum() / max(f.sum(), 1e-9))


def local_contrast(y: np.ndarray) -> float:
    return float(np.lib.stride_tricks.sliding_window_view(y, (5, 5)).std(axis=(2, 3)).mean())


def form_ratio(y: np.ndarray) -> float:
    prev, e = y, {}
    for u in (0.5, 1, 2, 4):
        b = wrap_blur(y, u * PPU)
        e[u] = float(np.abs(prev - b).mean())
        prev = b
    return e[4] / max(e[0.5], 1e-9)


def measure(rgba: np.ndarray) -> dict:
    y = luma(rgba)
    h = hsv(rgba)
    s = h[..., 1][h[..., 2] >= 0.10]
    v = h[..., 2]
    return {
        "seam": seam_ratio(y), "quad": quad_spread(y), "lowfreq": lowfreq_share(y),
        "contrast": local_contrast(y), "form_ratio": form_ratio(y),
        "sat_p95": float(np.percentile(s, 95)) if s.size else 0.0, "mean_s": float(h[..., 1].mean()),
        "chrome": float((v > 0.85).mean()), "p5_v": float(np.percentile(v, 5)), "p99_v": float(np.percentile(v, 99)),
        "mean_v": float(v.mean()),
    }


# gates -----------------------------------------------------------------------------------------------


def gate_errors(name: str, m: dict) -> list[str]:
    c = cfg(name)
    errs = []
    if m["seam"] > c["seam_ratio_max"]:
        errs.append(f"{name}: wrap seam {m['seam']:.2f}x an interior pair > {c['seam_ratio_max']} (does not tile)")
    if m["quad"] > c["quad_spread_max"]:
        errs.append(f"{name}: quadrant luma spread {m['quad']:.3f} > {c['quad_spread_max']} (baked direction)")
    if m["lowfreq"] > c["lowfreq_max"]:
        errs.append(f"{name}: low-frequency luma share {m['lowfreq']:.3f} > {c['lowfreq_max']} (baked gradient)")
    if m["contrast"] < c["contrast_min"]:
        errs.append(f"{name}: local contrast {m['contrast']:.4f} < {c['contrast_min']} (flat)")
    if m["form_ratio"] < c["form_ratio_min"]:
        errs.append(f"{name}: form ratio {m['form_ratio']:.2f} < {c['form_ratio_min']} (texture before form)")
    if m["sat_p95"] > c["sat_p95_max"]:
        errs.append(f"{name}: saturation p95 {m['sat_p95']:.3f} > {c['sat_p95_max']}")
    if m["chrome"] > c["chrome_max"]:
        errs.append(f"{name}: chrome share {m['chrome']:.4f} > {c['chrome_max']}")
    if m["p5_v"] > c["p5_v_max"]:
        errs.append(f"{name}: p5 V {m['p5_v']:.3f} > {c['p5_v_max']} (shadows not dark)")
    if m["p99_v"] < c["p99_v_min"]:
        errs.append(f"{name}: p99 V {m['p99_v']:.3f} < {c['p99_v_min']} (nothing catches light)")
    if m["mean_v"] > c["mean_v_max"]:
        errs.append(f"{name}: mean V {m['mean_v']:.3f} > {c['mean_v_max']} (too bright)")
    return errs


def format_errors(name: str, im: np.ndarray, size: list) -> list[str]:
    errs = []
    h, w = im.shape[:2]
    if h & (h - 1) or w & (w - 1):
        errs.append(f"{name}: {w}x{h} is not a power of two")
    if [w, h] != list(size):
        errs.append(f"{name}: {w}x{h} but the manifest says {size}")
    if not (im[..., 3] == 255).all():
        errs.append(f"{name}: alpha is not fully opaque")
    return errs


def png(path: Path) -> np.ndarray | None:
    if not path.exists():
        return None
    im = Image.open(path)
    return np.asarray(im, dtype=np.uint8) if im.mode == "RGBA" else None


# synthetic negatives: each must fail its gate -------------------------------------------------------------


def _wrapped_noise(seed: int, n: int = 256, sigma: float = 3.0) -> np.ndarray:
    r = np.random.default_rng(seed)
    return wrap_blur(r.random((n, n)).astype(np.float32), sigma)


def _as_rgba(y: np.ndarray, lo: float = 0.05, hi: float = 0.25, tint=(1.0, 0.95, 0.85)) -> np.ndarray:
    y = (y - y.min()) / max(float(y.max() - y.min()), 1e-9)
    c = (lo + (hi - lo) * y)[..., None] * np.array(tint, np.float32)
    rgb = np.clip(c * 255 + 0.5, 0, 255).astype(np.uint8)
    return np.dstack((rgb, np.full(y.shape, 255, np.uint8)))


def self_test() -> list[str]:
    errs = []
    n = 256
    good = _as_rgba(_wrapped_noise(1) + 0.6 * _wrapped_noise(2, sigma=10.0))
    ramp = np.tile(np.linspace(0, 1, n, dtype=np.float32), (n, 1))
    xs = np.arange(n, dtype=np.float32)[None, :] / n
    cases = {
        "seam": ("seam", _as_rgba(_wrapped_noise(3) * 0.2 + ramp)),
        "quad": ("quad", _as_rgba(_wrapped_noise(4) * 0.3 + 0.8 * np.sin(2 * np.pi * xs))),
        "lowfreq": ("lowfreq", _as_rgba(_wrapped_noise(5) * 0.15 + 1.0 * np.cos(2 * np.pi * xs) * np.cos(2 * np.pi * xs.T))),
        "contrast": ("contrast", np.dstack((np.full((n, n, 3), 40, np.uint8), np.full((n, n), 255, np.uint8)))),
        "form": ("form_ratio", _as_rgba(np.random.default_rng(6).random((n, n)).astype(np.float32))),
        "sat": ("sat_p95", _as_rgba(_wrapped_noise(7), tint=(1.0, 0.2, 0.1), hi=0.6)),
        "chrome": ("chrome", _as_rgba(_wrapped_noise(8), lo=0.3, hi=1.0)),
        "p5": ("p5_v", _as_rgba(_wrapped_noise(9), lo=0.4, hi=0.6)),
        "p99": ("p99_v", _as_rgba(_wrapped_noise(10), lo=0.0, hi=0.10)),
        "mean": ("mean_v", _as_rgba(_wrapped_noise(11), lo=0.3, hi=0.8)),
    }
    gm = measure(good)
    bad = gate_errors("selftest.good", gm)
    # the good tile is only required to pass the tiling and direction gates; the palette ones are tested by their negatives
    bad = [e for e in bad if "wrap seam" in e or "quadrant" in e or "low-frequency" in e]
    errs += [f"self-test: the good tile fails its own gates: {e}" for e in bad]
    for label, (key, im) in cases.items():
        m = measure(im)
        keyed = {"seam": "wrap seam", "quad": "quadrant", "lowfreq": "low-frequency", "contrast": "local contrast",
                 "form": "form ratio", "sat": "saturation", "chrome": "chrome", "p5": "p5 V", "p99": "p99 V", "mean": "mean V"}[label]
        if not any(keyed in e for e in gate_errors("selftest." + label, m)):
            errs.append(f"self-test: gate {label} did not fail its negative (metric {key} = {m.get(key, m.get('form_ratio'))})")
    # Format negatives, one per branch of format_errors, and each input may trip only the error it is built for:
    # the non power-of-two case was np.zeros, whose alpha 0 ALSO tripped "alpha is not fully opaque", so deleting
    # the power-of-two branch outright would have left this control green. Opaque white isolates the size error.
    for label, im, size, want in (
            ("npot", np.full((100, 100, 4), 255, np.uint8), [100, 100], "is not a power of two"),
            ("alpha", np.zeros((64, 64, 4), np.uint8), [64, 64], "alpha is not fully opaque"),
            ("size", np.full((64, 64, 4), 255, np.uint8), [128, 128], "but the manifest says")):
        got = format_errors("selftest." + label, im, size)
        if len(got) != 1 or want not in got[0]:
            errs.append(f"self-test: the format {label} negative reported {got}, expected exactly one {want!r}")
    if format_errors("selftest.clean", np.full((64, 64, 4), 255, np.uint8), [64, 64]):
        errs.append("self-test: a clean 64x64 opaque tile fails the format gate, so no negative above is specific")
    return errs


# main ------------------------------------------------------------------------------------------------


def main() -> int:
    fast, meas = "--fast" in sys.argv, "--measure" in sys.argv
    errs: list[str] = [] if meas else self_test()
    doc = None
    manifest = TERRAIN / "terrain_manifest.json"
    if manifest.exists():
        doc = json.loads(manifest.read_text(encoding="utf-8"))
    else:
        errs.append("terrain_manifest.json missing")
    sizes = {t["name"]: t["size"] for t in doc["tiles"]} if doc else {}
    if doc and [t["name"] for t in doc["tiles"]] != list(NAMES):
        errs.append(f"manifest lists {[t['name'] for t in doc['tiles']]}, generator has {list(NAMES)}")
    tiles: dict[str, np.ndarray] = {}
    for n in NAMES:
        im = png(TERRAIN / f"{n}.png")
        if im is None:
            errs.append(f"{n}: tile missing or not RGBA")
            continue
        tiles[n] = im
        errs += format_errors(n, im, sizes.get(n, [gen_terrain.TILE_PX] * 2))
    if TERRAIN.exists():
        wanted = {f"{n}.png" for n in NAMES} | {"terrain_manifest.json", "terrain_sheet.png"}
        errs += [f"stale committed file {p.name}: no generator tile produces it" for p in sorted(TERRAIN.iterdir()) if p.name not in wanted]
    stats = {n: measure(im) for n, im in tiles.items()}
    if meas:
        for n, m in stats.items():
            print(n, " ".join(f"{k} {v:.4f}" for k, v in m.items()))
        if stats:
            print("mean over tiles:", " ".join(f"{k} {np.mean([m[k] for m in stats.values()]):.4f}" for k in next(iter(stats.values()))))
        return 0
    for n, m in stats.items():
        errs += gate_errors(n, m)
    if not fast:
        errs += uiart.determinism_errors(gen_terrain.main, ["terrain"])
        # The third render is the committed output: regenerate into a temp dir and compare byte for byte. This
        # comparison was red on EVERY core.autocrlf=true checkout and green on Linux CI: terrain_manifest.json is
        # LF in the index, git handed it back CRLF, and the render writes LF, so the only difference was one CR per
        # line and "regenerate and commit" could not fix it. .gitattributes now pins eol=lf for
        # tools/ui-assets/terrain/*.json; uiart.shipped_errors compares .json with line endings normalised (and
        # PNGs untouched) as the belt behind that brace. See uiart._canon_shipped.
        errs += uiart.shipped_errors(gen_terrain.main, "terrain", TERRAIN)
    for e in errs:
        print("FAIL", e)
    print(f"terrain: {len(errs)} problems, {len(tiles)} tiles checked")
    return 1 if errs else 0


if __name__ == "__main__":
    sys.exit(main())
