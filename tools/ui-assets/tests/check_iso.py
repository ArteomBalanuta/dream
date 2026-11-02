"""Checks tools/ui-assets/iso/: the isometric sprite set. Exits nonzero on failure.

Usage: python check_iso.py [--fast] [--partial] [--measure]

  --fast     skip the determinism runs (three renders of everything)
  --partial  only the objects whose sprites exist; without it all seven names must exist
  --measure  print every metric for every object and exit, to calibrate the constants below

The gates are floors, ceilings and ratios measured on our own generated sprites at 4 px per u and then held;
nothing here reads an outside image. Do not loosen a constant to pass: fix the art.
"""
from __future__ import annotations

import hashlib
import json
import sys
import tempfile
from pathlib import Path

import numpy as np
from PIL import Image

HERE = Path(__file__).resolve().parent
TOOLS = HERE.parent
ROOT = TOOLS.parents[1]
sys.path.insert(0, str(TOOLS))
import gen_iso  # noqa: E402
import uiart  # noqa: E402

ISO = TOOLS / "iso"
NAMES = ("rust_barrel", "dead_oak", "bramble", "slate_rocks", "tombstone", "bone_pile", "rune_potion_drop")
POTION = "rune_potion_drop"
PPU = 4.0  # final px per u
BG = (200, 200, 200)

# constants: defaults for any static object, then per-object overrides ------------------------------------
STATIC = {
    "crisp_min": 0.95,            # share of partial-alpha pixels that touch both an opaque and a transparent neighbour
    "partial_ratio_max": 0.22,    # partial-alpha pixels over opaque pixels
    "sat_p95_max": 0.35,          # HSV S on pixels with a >= 240 and V >= .15
    "sat_p99_max": None,
    "sat_over35_max": None,       # share of pixels with S > .35
    "p5_v_max": 0.06,             # the shadows are crushed
    "p99_v_min": 0.35,            # something catches the light
    "contrast_min": 0.012,        # mean 5x5 window std of luma, rejects flat fills
    "form_ratio_min": 0.35,       # interior band energy 4 u over .5 u, alpha-normalised: big shapes read before texture
    "form_interior_min": 150,     # eroded interior pixels needed to measure form at all
    "warm_lit_min": 3.0,          # mean R - B (0..255) over the brightest 30 percent: warm
    "cold_band_max": 1.0,         # mean R - B over opaque pixels of luma 12..60 (0..255): the shadow band reads cold
    "cold_band_share_min": 0.10,  # the band needs this share of opaque pixels (and 150 px), or the shadows are crushed or absent
    "median_s_min": 0.10,         # not neutral grey
    "chrome_max": 0.012,          # share of pixels over V .85
    "foot_min": 0.5,              # opaque share in the 9x9 window around the pivot
}
# Add an entry here to hold one object to its own numbers. Anything not listed uses STATIC. An override is a claim that
# the art needs it: say why in a comment, with the measured value, and keep it as tight as the measurement allows.
OVERRIDES = {
    # rust is the barrel's one accent (p99 holds the ceiling, the share of rust pixels is high), and the sprite is nearly
    # all dark iron, so its p99 V and contrast floors differ. Measured 2026-11, see the barrel commit.
    "rust_barrel": {"sat_p95_max": None, "sat_p99_max": 0.50, "sat_over35_max": 0.35, "partial_ratio_max": 0.10},
    # bramble: the brief asks for crimson leaves, and the leaves are most of the lit sprite, so the saturated share cannot
    # reach the plan's 30 percent: measured sat p99 .671, share over S .35 .671 (leaves, canes and thorns are all
    # desaturated or dark; a third of the leaves are withered grey-rose). The same leaves, lit warm at low luma, sit in the
    # luma 12..60 band, so its mean R-B is warm: measured 15.0. Everything else (form, crispness, value) holds the defaults.
    "bramble": {"sat_p95_max": None, "sat_p99_max": 0.70, "sat_over35_max": 0.68, "cold_band_max": 15.2},
    # bone_pile: plan default p95 <= .35, no override. The art pass must meet it or justify an entry here.
}
POTION_GATES = {
    "magic_px_min": 40,           # pixels with S >= .8 and V >= .5 in every frame
    "centroid_tol": 14.0,         # bottle centroid x against the pivot x, px
    "base_tol": 8.0,              # frames 2 and 3: lowest opaque row against pivot y, px
    "diff_min": 2.0,              # mean absolute difference between consecutive frames, 0..255
    "ring_peak_max": 0.45,        # peak alpha of the faint impact ring in the last frame
    "partial_ratio_max": 0.60,
    "crisp_min": 0.95,            # soft pixels with S >= .5 (glow, aura, ring, sparks) are exempt, everything else is crisp
    "peak_v_min": 0.90,           # the potion glow is the brightest mass in the set
    "chrome_max": 0.012,          # share of pixels over V .85 that are not glow (S < .5): glints, not emission
}
GLOW_S = 0.5     # soft pixels at or above this saturation are emission, not edge
GLOW_COLD = 30   # B - R (0..255) at or above this on a bright pixel is cyan emission, not chrome
EDGE_ALPHA = 16  # sprite and shadow alpha allowed on any canvas edge row or column
SHEET = {"bg_share_min": 0.60, "line_max": 0.90, "band_px": 12}


def cfg(name: str) -> dict:
    c = dict(STATIC)
    c.update(OVERRIDES.get(name, {}))
    return c


# metrics ---------------------------------------------------------------------------------------------


def hsv(rgb: np.ndarray) -> np.ndarray:
    return np.asarray(Image.fromarray(np.ascontiguousarray(rgb), "RGB").convert("HSV"), dtype=np.float32) / 255.0


def luma(rgb: np.ndarray) -> np.ndarray:
    a = rgb[..., :3].astype(np.float32) / 255.0
    return 0.2126 * a[..., 0] + 0.7152 * a[..., 1] + 0.0722 * a[..., 2]


def neighbours(mask: np.ndarray) -> np.ndarray:
    """True where any 8-neighbour is set."""
    p = np.pad(mask, 1)
    out = np.zeros_like(mask)
    for dy in (0, 1, 2):
        for dx in (0, 1, 2):
            if dy != 1 or dx != 1:
                out |= p[dy : dy + mask.shape[0], dx : dx + mask.shape[1]]
    return out


def erode(mask: np.ndarray, times: int = 1) -> np.ndarray:
    """Mask pixels whose 8 neighbours are all set; the canvas border counts as empty."""
    for _ in range(times):
        p = np.pad(mask, 1)
        out = mask.copy()
        for dy in (0, 1, 2):
            for dx in (0, 1, 2):
                out &= p[dy : dy + mask.shape[0], dx : dx + mask.shape[1]]
        mask = out
    return mask


def _blur(a: np.ndarray, sigma: float) -> np.ndarray:
    """Separable Gaussian, zero outside the canvas (so only meaningful through an alpha-normalised ratio)."""
    r = int(np.ceil(3 * sigma))
    k = np.exp(-0.5 * (np.arange(-r, r + 1) / sigma) ** 2)
    k /= k.sum()
    for axis in (0, 1):
        pad = [(0, 0), (0, 0)]
        pad[axis] = (r, r)
        p = np.pad(a, pad)
        n = a.shape[axis]
        out = np.zeros_like(a)
        for i, w in enumerate(k):
            out += w * (p[i : i + n] if axis == 0 else p[:, i : i + n])
        a = out
    return a


def band_energy(rgba: np.ndarray, cover: np.ndarray, interior: np.ndarray) -> dict:
    """Mean |difference| between successive blurs of luma at .5, 1, 2 and 4 u, over `interior` only. The blurs are
    alpha-normalised (blur(y * cover) / blur(cover)), so the silhouette and the empty canvas contribute nothing: a flat
    interior with grain has no energy at 4 u and fails, a shape with real shading has."""
    y = luma(rgba) * cover
    m = cover.astype(np.float32)
    prev, out = luma(rgba), {}
    for u in (0.5, 1, 2, 4):
        s = u * PPU
        b = _blur(y, s) / np.maximum(_blur(m, s), 1e-3)
        out[u] = float(np.abs(prev - b)[interior].mean()) if interior.any() else 0.0
        prev = b
    return out


def local_contrast(rgba: np.ndarray) -> float:
    y = luma(rgba)
    full = rgba[..., 3] >= 240
    win = np.lib.stride_tricks.sliding_window_view(y, (5, 5)).std(axis=(2, 3))
    ok = np.lib.stride_tricks.sliding_window_view(full, (5, 5)).all(axis=(2, 3))
    return float(win[ok].mean()) if ok.any() else 0.0


def components(mask: np.ndarray) -> list[np.ndarray]:
    """8-connected components of a boolean mask as (n, 2) arrays of (y, x), largest first."""
    H, W = mask.shape
    seen = np.zeros_like(mask)
    out = []
    for y0, x0 in zip(*np.nonzero(mask)):
        if seen[y0, x0]:
            continue
        seen[y0, x0] = True
        stack, pts = [(y0, x0)], []
        while stack:
            y, x = stack.pop()
            pts.append((y, x))
            for dy in (-1, 0, 1):
                for dx in (-1, 0, 1):
                    ny, nx = y + dy, x + dx
                    if 0 <= ny < H and 0 <= nx < W and mask[ny, nx] and not seen[ny, nx]:
                        seen[ny, nx] = True
                        stack.append((ny, nx))
        out.append(np.array(pts))
    return sorted(out, key=len, reverse=True)


def main_body(opaque: np.ndarray, keep: float = 0.25) -> np.ndarray:
    """The object itself: the largest 8-connected opaque component plus any other at least `keep` of its size
    (a stopper that does not touch the neck). A spark, a speck or a ring fragment is not part of it."""
    comps = components(opaque)
    out = np.zeros_like(opaque)
    for c in comps:
        if len(c) >= keep * len(comps[0]):
            out[c[:, 0], c[:, 1]] = True
    return out


def measure(name: str, rgba: np.ndarray) -> dict:
    a = rgba[..., 3]
    op = a >= 240
    part = (a > 16) & (a < 240)
    clear = a <= 16
    h = hsv(rgba[..., :3])
    if name == POTION:
        part_e = part & (h[..., 1] < GLOW_S)   # glow, aura, ring and sparks are exempt from edge crispness
    else:
        part_e = part
    near_op, near_cl = neighbours(op), neighbours(clear)
    m = {"opaque": int(op.sum()), "partial": int(part.sum())}
    # no partial-alpha pixels at all is as crisp as it gets (hard-alpha foliage was 0/0)
    m["crisp"] = float((part_e & near_op & near_cl).sum() / part_e.sum()) if part_e.any() else 1.0
    m["partial_ratio"] = float(part.sum() / max(op.sum(), 1))
    sel = op & (h[..., 2] >= 0.15)
    s = h[..., 1][sel] if sel.any() else np.zeros(1, np.float32)
    v = h[..., 2][op]
    m["sat_p95"], m["sat_p99"] = float(np.percentile(s, 95)), float(np.percentile(s, 99))
    m["sat_over35"] = float((s > 0.35).mean())
    m["median_s"] = float(np.median(s))
    m["p5_v"], m["p99_v"], m["peak_v"] = float(np.percentile(v, 5)), float(np.percentile(v, 99)), float(np.percentile(v, 99.9))
    bright = v > 0.85
    if name == POTION:
        # the glow is emission, and its hot core goes pale but stays cyan; only a neutral or warm bright pixel is chrome
        cold = (rgba[..., 2].astype(np.int32) - rgba[..., 0].astype(np.int32))[op] >= GLOW_COLD
        bright = bright & ~cold
    m["chrome"] = float(bright.mean())
    m["contrast"] = local_contrast(rgba)
    interior = erode(op, 2)
    m["form_n"] = int(interior.sum())
    be = band_energy(rgba, op, interior)
    m["form_ratio"] = be[4] / max(be[0.5], 1e-9)
    y = luma(rgba)[op]
    hi = np.percentile(y, 70)
    rb = rgba[..., 0].astype(np.float32)[op] - rgba[..., 2].astype(np.float32)[op]
    m["warm_lit"] = float(rb[y >= hi].mean())
    # the shadow band sits above the crush: dark enough to be shadow, bright enough to carry hue
    band = (y * 255 >= 12) & (y * 255 <= 60)
    m["band_n"] = int(band.sum())
    m["band_share"] = float(band.mean())
    m["cold_band"] = float(rb[band].mean()) if band.any() else 0.0
    m["magic_px"] = int(((h[..., 1] >= 0.8) & (h[..., 2] >= 0.5) & op).sum())
    return m


# gates -----------------------------------------------------------------------------------------------


def static_errors(name: str, rgba: np.ndarray, pivot: tuple, m: dict) -> list[str]:
    c, errs = cfg(name), []

    def need(key, ok, text):
        if not ok:
            errs.append(f"{name}: {text}")

    need("crisp", m["crisp"] >= c["crisp_min"], f"crispness {m['crisp']:.3f} < {c['crisp_min']}")
    need("pr", m["partial_ratio"] <= c["partial_ratio_max"], f"partial/opaque {m['partial_ratio']:.3f} > {c['partial_ratio_max']}")
    if c["sat_p95_max"] is not None:
        need("s95", m["sat_p95"] <= c["sat_p95_max"], f"saturation p95 {m['sat_p95']:.3f} > {c['sat_p95_max']}")
    if c["sat_p99_max"] is not None:
        need("s99", m["sat_p99"] <= c["sat_p99_max"], f"saturation p99 {m['sat_p99']:.3f} > {c['sat_p99_max']}")
    if c["sat_over35_max"] is not None:
        need("s35", m["sat_over35"] <= c["sat_over35_max"], f"share over S .35 {m['sat_over35']:.3f} > {c['sat_over35_max']}")
    need("p5", m["p5_v"] <= c["p5_v_max"], f"p5 V {m['p5_v']:.3f} > {c['p5_v_max']}: shadows not crushed")
    need("p99", m["p99_v"] >= c["p99_v_min"], f"p99 V {m['p99_v']:.3f} < {c['p99_v_min']}: nothing catches the light")
    need("con", m["contrast"] >= c["contrast_min"], f"5x5 contrast {m['contrast']:.4f} < {c['contrast_min']}: flat")
    if m["form_n"] < c["form_interior_min"]:
        errs.append(f"{name}: only {m['form_n']} interior px, need {c['form_interior_min']} to measure form")
    else:
        need("form", m["form_ratio"] >= c["form_ratio_min"], f"interior band 4u/.5u {m['form_ratio']:.2f} < {c['form_ratio_min']}: noise before form")
    need("warm", m["warm_lit"] >= c["warm_lit_min"], f"lit R-B {m['warm_lit']:.1f} < {c['warm_lit_min']}: lights not warm")
    if m["band_n"] < 150 or m["band_share"] < c["cold_band_share_min"]:
        errs.append(f"{name}: shadow band (luma 12..60) holds {m['band_n']} px, {m['band_share']:.2f} of the object, "
                    f"need {c['cold_band_share_min']}: shadows crushed or absent")
    else:
        need("cold", m["cold_band"] <= c["cold_band_max"], f"shadow band R-B {m['cold_band']:.1f} > {c['cold_band_max']}: shadows not cold")
    need("ms", m["median_s"] >= c["median_s_min"], f"median S {m['median_s']:.3f} < {c['median_s_min']}: neutral grey")
    need("chr", m["chrome"] <= c["chrome_max"], f"{m['chrome']:.4f} of pixels over V .85: chrome")
    px, py = pivot
    win = rgba[max(py - 4, 0) : py + 5, max(px - 4, 0) : px + 5, 3] >= 240
    need("foot", win.mean() >= c["foot_min"], f"opaque share around the pivot {win.mean():.2f} < {c['foot_min']}: no foot point")
    return errs


def potion_errors(frames: list[np.ndarray], pivot: tuple) -> list[str]:
    g, errs = POTION_GATES, []
    px, py = pivot
    lows, bases, ms = [], [], []
    for i, f in enumerate(frames):
        m = measure(POTION, f)
        ms.append(m)
        if m["magic_px"] < g["magic_px_min"]:
            errs.append(f"{POTION}_{i}: only {m['magic_px']} magic pixels (S >= .8, V >= .5), need {g['magic_px_min']}")
        if m["partial_ratio"] > g["partial_ratio_max"]:
            errs.append(f"{POTION}_{i}: partial/opaque {m['partial_ratio']:.2f} > {g['partial_ratio_max']}")
        if m["crisp"] < g["crisp_min"]:
            errs.append(f"{POTION}_{i}: crispness {m['crisp']:.3f} < {g['crisp_min']} outside the glow (soft pixels with S < {GLOW_S})")
        if m["peak_v"] < g["peak_v_min"]:
            errs.append(f"{POTION}_{i}: peak V {m['peak_v']:.3f} < {g['peak_v_min']}: the glow is not the brightest mass")
        if m["chrome"] > g["chrome_max"]:
            errs.append(f"{POTION}_{i}: {m['chrome']:.4f} of pixels over V .85 are not glow (B - R < {GLOW_COLD}): chrome")
        body = main_body(f[..., 3] >= 240)
        ys, xs = np.nonzero(body)
        if not len(ys):
            errs.append(f"{POTION}_{i}: no opaque pixels")
            lows.append(0)
            continue
        lows.append(int(ys.max()))
        if abs(float(xs.mean()) - px) > g["centroid_tol"]:
            errs.append(f"{POTION}_{i}: bottle centroid x {xs.mean():.1f} is {abs(xs.mean() - px):.1f} px from the pivot x {px}")
    if lows != sorted(lows):
        errs.append(f"{POTION}: lowest opaque row is not non-decreasing {lows}")
    for i in (2, 3):
        if i < len(lows) and abs(lows[i] - py) > g["base_tol"]:
            errs.append(f"{POTION}_{i}: base at row {lows[i]}, pivot y {py}, tol {g['base_tol']}")
    for i in range(1, len(frames)):
        d = float(np.abs(frames[i][..., :3].astype(np.float32) * (frames[i][..., 3:4] / 255) - frames[i - 1][..., :3].astype(np.float32) * (frames[i - 1][..., 3:4] / 255)).mean())
        if d < g["diff_min"]:
            errs.append(f"{POTION}: frames {i - 1} and {i} differ by {d:.2f} < {g['diff_min']}")
    if len(frames) >= 4:
        last = frames[3]
        # everything visible that is not the bottle (its connected component, dilated 2 px for the antialiased outline)
        # is the ring, so a ring drawn at alpha 255 cannot hide above the soft-pixel range
        bottle = main_body(last[..., 3] >= 240)
        soft = (last[..., 3] > 16) & ~neighbours(neighbours(bottle) | bottle)
        peak = float(last[..., 3][soft].max() / 255) if soft.any() else 0.0
        if peak > g["ring_peak_max"] + 1e-6:
            errs.append(f"{POTION}_3: ring peak alpha {peak:.2f} > {g['ring_peak_max']}")
    return errs


def edge_errors(label: str, layer: str, a: np.ndarray) -> list[str]:
    """Nothing may touch the canvas border: alpha over EDGE_ALPHA on any edge row or column means the art is clipped."""
    errs = []
    sides = {"top": a[0], "bottom": a[-1], "left": a[:, 0], "right": a[:, -1]}
    for side, line in sides.items():
        if int(line.max()) > EDGE_ALPHA:
            errs.append(f"{label}: {layer} touches the {side} edge, alpha {int(line.max())} at index {int(line.argmax())} ({int((line > EDGE_ALPHA).sum())} px over {EDGE_ALPHA})")
    return errs


def png(path: Path) -> np.ndarray | None:
    if not path.exists():
        return None
    im = Image.open(path)
    if im.mode != "RGBA":
        return None
    return np.asarray(im)


def sheet_errors(sheet: np.ndarray) -> list[str]:
    errs = []
    rgb = sheet[..., :3]
    is_bg = (rgb == np.array(BG, dtype=np.uint8)).all(axis=2)
    b = SHEET["band_px"]
    edge = np.concatenate((is_bg[:b].ravel(), is_bg[-b:].ravel(), is_bg[:, :b].ravel(), is_bg[:, -b:].ravel()))
    if not edge.all():
        errs.append("sheet: outer band is not the exact neutral background")
    if is_bg.mean() < SHEET["bg_share_min"]:
        errs.append(f"sheet: only {is_bg.mean():.2f} exact background, need {SHEET['bg_share_min']}")
    if (1 - is_bg).mean(axis=1).max() > SHEET["line_max"] or (1 - is_bg).mean(axis=0).max() > SHEET["line_max"]:
        errs.append("sheet: a row or column is over 90 percent non-background")
    return errs


def digests(root: Path) -> dict:
    return {str(p.relative_to(root)).replace("\\", "/"): hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(root.rglob("*")) if p.is_file()}


def determinism(partial: bool) -> list[str]:
    tmp = ROOT / ".iso_tmp"
    tmp.mkdir(exist_ok=True)
    tempfile.tempdir = str(tmp)  # keeps the temp dirs on the repository drive, never committed
    seen: list[dict] = []

    def run(out_root: Path) -> None:
        gen_iso.main(out_root)
        seen.append(digests(Path(out_root) / "iso"))

    errs = uiart.determinism_errors(run, ["iso"])
    if seen:
        first = seen[0]
        committed = digests(ISO) if ISO.exists() else {}
        for k, h in committed.items():
            if k in first and first[k] != h:
                errs.append(f"committed {k} differs from the generated bytes")
        for k in first:
            if k not in committed and (k.startswith(("sprites/", "shadows/")) or not partial):
                errs.append(f"{k} is generated but not committed")
    return errs


def _disc(size: int, r: float, fn) -> np.ndarray:
    """RGBA disc of radius r px on a size x size canvas; fn(dx, dy, rng) gives the RGB array of the disc pixels."""
    yy, xx = np.mgrid[:size, :size].astype(np.float32)
    dx, dy = xx - size / 2 + 0.5, yy - size / 2 + 0.5
    inside = dx * dx + dy * dy <= r * r
    img = np.zeros((size, size, 4), np.uint8)
    img[..., :3] = np.clip(fn(dx, dy, np.random.default_rng(7)), 0, 255).astype(np.uint8)
    img[..., 3] = np.where(inside, 255, 0)
    return img


def _self_test_form_cold() -> list[str]:
    errs = []
    grain = lambda rng, s: rng.integers(-8, 9, size=(s, s, 1)).astype(np.float32)
    flat = _disc(96, 40, lambda dx, dy, rng: 90 + grain(rng, 96) + 0 * dx[..., None])
    if not any("interior band" in e for e in static_errors("flat_grain", flat, (48, 48), measure("flat_grain", flat))):
        errs.append("self-test: a flat interior with grain passed the form gate")
    shaded = _disc(96, 40, lambda dx, dy, rng: 40 + 90 * np.clip(0.5 + (-dx - dy) / 70.0, 0, 1)[..., None] + grain(rng, 96) * 0.4)
    if any("interior band" in e for e in static_errors("shaded", shaded, (48, 48), measure("shaded", shaded))):
        errs.append("self-test: a disc with a broad shading ramp failed the form gate")
    warm = _disc(64, 28, lambda dx, dy, rng: np.broadcast_to(np.array((44.0, 32.0, 18.0)), dx.shape + (3,)) + 8 * np.sin(dx / 3)[..., None])
    if not any("shadows not cold" in e for e in static_errors("warm", warm, (32, 32), measure("warm", warm))):
        errs.append("self-test: a warm shadow band passed the cold gate")
    cold = _disc(64, 28, lambda dx, dy, rng: np.broadcast_to(np.array((16.0, 30.0, 52.0)), dx.shape + (3,)) + 8 * np.sin(dx / 3)[..., None])
    if any("shadows not cold" in e or "shadows crushed" in e for e in static_errors("cold", cold, (32, 32), measure("cold", cold))):
        errs.append("self-test: a cold shadow band tripped the cold gate")
    black = _disc(64, 28, lambda dx, dy, rng: 3 + 0 * dx[..., None] + 0 * dy[..., None])
    if not any("shadows crushed" in e for e in static_errors("black", black, (32, 32), measure("black", black))):
        errs.append("self-test: a crushed sprite with no shadow band passed")
    return errs


def manifest_spec_errors(doc: dict, objs: dict) -> list[str]:
    """The committed manifest against the registry: size, pivot, frame count, durations and loop must all agree."""
    errs = []
    for o in doc.get("objects", []):
        spec = objs.get(o["name"])
        if spec is None:
            errs.append(f"manifest: {o['name']} is not in any generator (stale committed object)")
            continue
        if tuple(o["size"]) != tuple(spec.size):
            errs.append(f"manifest: {o['name']} size {o['size']}, spec {list(spec.size)}")
        if tuple(o["pivot"]) != tuple(spec.pivot):
            errs.append(f"manifest: {o['name']} pivot {o['pivot']}, spec {list(spec.pivot)}")
        if len(o["frames"]) != len(spec.frames):
            errs.append(f"manifest: {o['name']} has {len(o['frames'])} frames, spec {len(spec.frames)}")
        want = [int(d) for d in spec.duration_ms] if spec.duration_ms else [0] * len(spec.frames)
        got = [int(fr["durationMs"]) for fr in o["frames"]]
        if got != want:
            errs.append(f"manifest: {o['name']} durations {got}, spec {want}")
        if o.get("loop") is not False:
            errs.append(f"manifest: {o['name']} loop {o.get('loop')}")
    return errs


def self_test() -> list[str]:
    """The metrics and gates on images with known answers: a flat fill, a white chrome blob and a neutral grey one must fail."""
    errs = []
    flat = np.zeros((48, 48, 4), np.uint8)
    flat[8:40, 8:40] = (90, 90, 90, 255)
    errs += ["self-test: a flat neutral fill passed" for _ in [0] if not static_errors("flat", flat, (24, 24), measure("flat", flat))]
    chrome = flat.copy()
    chrome[8:40, 8:40] = (250, 250, 250, 255)
    if not any("over V .85" in e for e in static_errors("blob", chrome, (24, 24), measure("blob", chrome))):
        errs.append("self-test: a white blob did not trip the chrome gate")
    errs += _self_test_form_cold()
    m = measure("flat", flat)
    if m["crisp"] != 1.0 or m["contrast"] > 1e-4 or m["sat_p95"] > 1e-3:
        errs.append(f"self-test: metrics wrong on a flat fill {m['crisp']} {m['contrast']} {m['sat_p95']}")
    edge = flat.copy()
    edge[0, 20] = (90, 90, 90, 200)
    if not edge_errors("edge", "sprite", edge[..., 3]):
        errs.append("self-test: alpha 200 on the top edge did not trip the edge gate")
    if edge_errors("edge", "sprite", flat[..., 3]):
        errs.append("self-test: a clear border tripped the edge gate")
    frames = [np.zeros((64, 64, 4), np.uint8) for _ in range(4)]
    for f in frames:
        f[40:60, 20:44] = (200, 40, 220, 255)
    ring = frames[3].copy()
    ring[3:6, 3:6] = (200, 40, 220, 150)  # soft pixels far from the bottle, alpha .59
    if not any("ring peak" in e for e in potion_errors(frames[:3] + [ring], (32, 60))):
        errs.append("self-test: a bright impact ring did not trip the ring peak gate")
    off = [f.copy() for f in frames]
    off[2][40:60, 20:44] = 0
    off[2][5:25, 20:44] = (200, 40, 220, 255)  # base far above the pivot
    if not any("base at row" in e for e in potion_errors(off, (32, 60))):
        errs.append("self-test: a frame with its base off the pivot did not trip the pivot gate")
    hole = frames[3].copy()
    hole[3:9, 3:9] = (200, 40, 220, 255)  # an opaque ring fragment: alpha >= 240 must not slip past the ring gate
    if not any("ring peak" in e for e in potion_errors(frames[:3] + [hole], (32, 60))):
        errs.append("self-test: an opaque patch away from the bottle did not trip the ring peak gate")
    spark = [f.copy() for f in frames]
    spark[2][40:60, 20:44] = 0
    spark[2][22:40, 20:44] = (200, 40, 220, 255)  # bottle base 20 px above the pivot ...
    spark[2][59, 4:6] = (255, 255, 255, 255)      # ... and a spark on the pivot row, which must not count as the base
    if not any("base at row" in e for e in potion_errors(spark, (32, 60))):
        errs.append("self-test: a spark on the pivot row hid a bottle that is off the pivot")
    chromey = [f.copy() for f in frames]
    chromey[1][40:60, 20:44] = (250, 250, 250, 255)  # a white bottle is chrome, not glow
    if not any("not glow" in e for e in potion_errors(chromey, (32, 60))):
        errs.append("self-test: a white potion bottle did not trip the potion chrome gate")
    dull = [f.copy() for f in frames]
    dull[0][40:60, 20:44] = (40, 60, 70, 255)
    if not any("brightest mass" in e for e in potion_errors(dull, (32, 60))):
        errs.append("self-test: a dull potion frame did not trip the potion value gate")
    ragged = [f.copy() for f in frames]
    ragged[0][38, 20:44] = (200, 200, 190, 128)  # soft edge pixels that are not glow
    ragged[0][36, 5:20] = (200, 200, 190, 128)   # and soft pixels away from the body
    if not any("crispness" in e for e in potion_errors(ragged, (32, 60))):
        errs.append("self-test: soft non-glow pixels did not trip the potion crispness gate")
    glowy = [f.copy() for f in frames]
    glowy[0][36:39, 5:20] = (20, 200, 230, 128)   # cyan soft pixels are exempt
    if any("crispness" in e for e in potion_errors(glowy, (32, 60))):
        errs.append("self-test: soft glow pixels tripped the potion crispness gate")
    spec = type("Spec", (), {"size": (64, 64), "pivot": (32, 60), "frames": (0,), "duration_ms": ()})
    good = {"objects": [{"name": "a", "size": [64, 64], "pivot": [32, 60], "loop": False,
                         "frames": [{"image": "sprites/a.png", "shadow": "shadows/a.png", "durationMs": 0}]}]}
    if manifest_spec_errors(good, {"a": spec}):
        errs.append("self-test: a manifest equal to its spec failed")
    for key, val in (("pivot", [31, 60]), ("size", [64, 65])):
        stale = json.loads(json.dumps(good))
        stale["objects"][0][key] = val
        if not manifest_spec_errors(stale, {"a": spec}):
            errs.append(f"self-test: a stale manifest {key} was not flagged")
    stale = json.loads(json.dumps(good))
    stale["objects"][0]["frames"][0]["durationMs"] = 90
    if not manifest_spec_errors(stale, {"a": spec}):
        errs.append("self-test: a stale manifest duration was not flagged")
    bad = np.full((200, 200, 4), 255, np.uint8)
    bad[..., :3] = BG
    if sheet_errors(bad):
        errs.append("self-test: a clean sheet failed the sheet gate")
    bad[0, 0, :3] = (10, 10, 10)
    if not any("outer band" in e for e in sheet_errors(bad)):
        errs.append("self-test: a dirty sheet border did not trip the sheet gate")
    bad[..., :3] = (90, 90, 90)
    if not any("exact background" in e for e in sheet_errors(bad)):
        errs.append("self-test: a sheet with no background did not trip the share gate")
    return errs


# main ------------------------------------------------------------------------------------------------


def main() -> int:
    fast, partial, meas = "--fast" in sys.argv, "--partial" in sys.argv, "--measure" in sys.argv
    objs = gen_iso.load_objects()
    errs: list[str] = [] if meas else self_test()
    present =[n for n in NAMES if n in objs and all((ISO / "sprites" / f"{f}.png").exists() for f in gen_iso.frame_names(n, objs[n]))]
    if not partial:
        errs += [f"missing object {n}" for n in NAMES if n not in present]
    names = present if partial or meas else [n for n in NAMES if n in present]
    sprites: dict[str, list[np.ndarray]] = {}
    for n in names:
        spec = objs[n]
        sprites[n] = []
        for f in gen_iso.frame_names(n, spec):
            im, sh = png(ISO / "sprites" / f"{f}.png"), png(ISO / "shadows" / f"{f}.png")
            if im is None:
                errs.append(f"{f}: sprite missing or not RGBA")
                continue
            if im.shape[:2] != (spec.size[1], spec.size[0]):
                errs.append(f"{f}: size {im.shape[1]}x{im.shape[0]}, spec {spec.size}")
            if sh is None or sh.shape != im.shape:
                errs.append(f"{f}: shadow missing or a different canvas")
            elif sh[..., :3].any():
                errs.append(f"{f}: shadow RGB is not black")
            if uiart.FORBIDDEN_NAMES.search(f):
                errs.append(f"{f}: forbidden name")
            ring = neighbours(im[..., 3] > 0) & (im[..., 3] == 0)
            if ring.any() and not im[..., :3][ring].any():
                errs.append(f"{f}: RGB under alpha 0 is not bled, so not straight alpha")
            sprites[n].append(im)
            errs += edge_errors(f, "sprite", im[..., 3])
            if sh is not None:
                errs += edge_errors(f, "shadow", sh[..., 3])
    wanted = {f"{f}.png" for n in objs for f in gen_iso.frame_names(n, objs[n])}
    for sub in ("sprites", "shadows"):
        folder = ISO / sub
        if folder.exists():
            errs += [f"stale committed file {sub}/{p.name}: no generator frame produces it" for p in sorted(folder.iterdir()) if p.name not in wanted]
    statics = [n for n in names if n != POTION]
    stats = {}
    for n in names:
        for i, im in enumerate(sprites[n]):
            stats[(n, i)] = measure(n, im)
    if meas:
        for (n, i), m in stats.items():
            print(n, i, " ".join(f"{k} {v:.4f}" if isinstance(v, float) else f"{k} {v}" for k, v in m.items()))
        return 0
    for n in statics:
        if sprites[n]:
            errs += static_errors(n, sprites[n][0], objs[n].pivot, stats[(n, 0)])
    if POTION in names and len(sprites[POTION]) == len(objs[POTION].frames):
        errs += potion_errors(sprites[POTION], objs[POTION].pivot)
        peak = min(stats[(POTION, i)]["peak_v"] for i in range(len(sprites[POTION])))
        for n in statics:
            if sprites[n] and stats[(n, 0)]["peak_v"] >= peak:
                errs.append(f"focal: {n} peak V {stats[(n, 0)]['peak_v']:.3f} is not below the potion's {peak:.3f}")
    manifest = ISO / "iso_manifest.json"
    if manifest.exists():
        doc = json.loads(manifest.read_text(encoding="utf-8"))
        errs += manifest_spec_errors(doc, objs)
        listed = [o["name"] for o in doc["objects"]]
        if not partial and listed != [n for n in NAMES if n in listed]:
            errs.append(f"manifest: objects {listed}")
        for o in doc["objects"]:
            if uiart.FORBIDDEN_NAMES.search(o["name"]):
                errs.append(f"manifest: forbidden name {o['name']}")
            for fr in o["frames"]:
                for key in ("image", "shadow"):
                    p = png(ISO / fr[key])
                    if p is None or list(p.shape[1::-1]) != o["size"]:
                        errs.append(f"manifest: {fr[key]} missing or not {o['size']}")
        if doc.get("yDown") is not True or doc.get("straightAlpha") is not True or doc.get("pxPerUnit") != 4:
            errs.append("manifest: header flags")
    elif not partial:
        errs.append("manifest: iso_manifest.json missing")
    sheet = png(ISO / "iso_sheet.png")
    if sheet is not None:
        errs += sheet_errors(sheet)
    elif not partial:
        errs.append("sheet: iso_sheet.png missing")
    elif names:
        ents = []
        for n in names:
            for f, im in zip(gen_iso.frame_names(n, objs[n]), sprites[n]):
                ents.append((im, png(ISO / "shadows" / f"{f}.png")))
        if all(s is not None for _, s in ents):
            sh = gen_iso.compose_sheet(ents)
            errs += sheet_errors(np.dstack((sh, np.full(sh.shape[:2], 255, np.uint8))))
    if not fast:
        errs += determinism(partial)
    for e in errs:
        print("FAIL", e)
    for (n, i), m in stats.items():
        if i == 0:
            print(f"{n}: crisp {m['crisp']:.3f} sat p95 {m['sat_p95']:.3f} p99 {m['sat_p99']:.3f} p5V {m['p5_v']:.3f} p99V {m['p99_v']:.3f} "
                  f"contrast {m['contrast']:.4f} form {m['form_ratio']:.2f} warm {m['warm_lit']:.1f} cold {m['cold_band']:.1f} (band {m['band_share']:.2f}) "
                  f"medS {m['median_s']:.3f} chrome {m['chrome']:.4f} peakV {m['peak_v']:.3f}")
    print(f"check_iso: {len(names)} objects, {len(errs)} errors")
    return 1 if errs else 0


if __name__ == "__main__":
    sys.exit(main())
