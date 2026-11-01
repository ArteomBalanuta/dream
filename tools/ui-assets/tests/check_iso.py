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
from PIL import Image, ImageFilter

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
    "form_ratio_min": 0.35,       # band energy 4 u over .5 u: big shapes read before texture
    "warm_lit_min": 3.0,          # mean R - B (0..255) over the brightest 30 percent: warm
    "cold_shadow_max": 6.0,       # mean R - B over the darkest 30 percent: cold or neutral
    "median_s_min": 0.10,         # not neutral grey
    "chrome_max": 0.012,          # share of pixels over V .85
    "foot_min": 0.5,              # opaque share in the 9x9 window around the pivot
}
OVERRIDES = {
    # measured 2026-11: p99 .490, over .35 .180, p5 V 0, p99 V .62, contrast .057, form .54, warm +32, cold -0.6, median S .31
    # form_ratio_min .38: the second pass's rivets and hoops are real secondary form; measured .3885
    "rust_barrel": {"sat_p95_max": None, "sat_p99_max": 0.50, "sat_over35_max": 0.22, "partial_ratio_max": 0.10, "p99_v_min": 0.50,
                    "contrast_min": 0.04, "form_ratio_min": 0.38, "warm_lit_min": 20.0, "cold_shadow_max": 3.0, "median_s_min": 0.20},
    # crimson leaves are the object's body, not an accent, so the share is high and p99 holds the ceiling; measured p95 .655, p99 .690, over .35 .872
    "bramble": {"sat_p95_max": 0.68, "sat_p99_max": 0.70, "sat_over35_max": 0.90},
    # saturation here is a small accent (under 7 percent of pixels), gated at p99 and by share instead of p95; measured p95 .384, p99 .475, over .35 .067
    "bone_pile": {"sat_p95_max": None, "sat_p99_max": 0.50, "sat_over35_max": 0.08},
}
POTION_GATES = {
    "magic_px_min": 40,           # pixels with S >= .8 and V >= .5 in every frame
    "centroid_tol": 14.0,         # bottle centroid x against the pivot x, px
    "base_tol": 8.0,              # frames 2 and 3: lowest opaque row against pivot y, px
    "diff_min": 2.0,              # mean absolute difference between consecutive frames, 0..255
    "ring_peak_max": 0.45,        # peak alpha of the faint impact ring in the last frame
    "partial_ratio_max": 0.60,
}
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


def band_energy(rgba: np.ndarray, cover: np.ndarray) -> dict:
    g = Image.fromarray(np.ascontiguousarray(rgba[..., :3]), "RGB").convert("L")
    a = np.asarray(g, dtype=np.float32) / 255
    prev, out = a, {}
    for u in (0.5, 1, 2, 4):
        b = np.asarray(g.filter(ImageFilter.GaussianBlur(u * PPU)), dtype=np.float32) / 255
        out[u] = float(np.abs(prev - b)[cover].mean())
        prev = b
    return out


def local_contrast(rgba: np.ndarray) -> float:
    y = luma(rgba)
    full = rgba[..., 3] >= 240
    win = np.lib.stride_tricks.sliding_window_view(y, (5, 5)).std(axis=(2, 3))
    ok = np.lib.stride_tricks.sliding_window_view(full, (5, 5)).all(axis=(2, 3))
    return float(win[ok].mean()) if ok.any() else 0.0


def measure(name: str, rgba: np.ndarray) -> dict:
    a = rgba[..., 3]
    op = a >= 240
    part = (a > 16) & (a < 240)
    clear = a <= 16
    near_op, near_cl = neighbours(op), neighbours(clear)
    m = {"opaque": int(op.sum()), "partial": int(part.sum())}
    # no partial-alpha pixels at all is as crisp as it gets (hard-alpha foliage was 0/0)
    m["crisp"] = float((part & near_op & near_cl).sum() / part.sum()) if part.any() else 1.0
    m["partial_ratio"] = float(part.sum() / max(op.sum(), 1))
    h = hsv(rgba[..., :3])
    sel = op & (h[..., 2] >= 0.15)
    s = h[..., 1][sel]
    v = h[..., 2][op]
    m["sat_p95"], m["sat_p99"] = float(np.percentile(s, 95)), float(np.percentile(s, 99))
    m["sat_over35"] = float((s > 0.35).mean())
    m["median_s"] = float(np.median(s))
    m["p5_v"], m["p99_v"], m["peak_v"] = float(np.percentile(v, 5)), float(np.percentile(v, 99)), float(np.percentile(v, 99.9))
    m["chrome"] = float((v > 0.85).mean())
    m["contrast"] = local_contrast(rgba)
    be = band_energy(rgba, a > 127)
    m["form_ratio"] = be[4] / max(be[0.5], 1e-9)
    y = luma(rgba)[op]
    lo, hi = np.percentile(y, 30), np.percentile(y, 70)
    rb = rgba[..., 0].astype(np.float32)[op] - rgba[..., 2].astype(np.float32)[op]
    m["warm_lit"], m["cold_shadow"] = float(rb[y >= hi].mean()), float(rb[y <= lo].mean())
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
    need("form", m["form_ratio"] >= c["form_ratio_min"], f"band 4u/.5u {m['form_ratio']:.2f} < {c['form_ratio_min']}: noise before form")
    need("warm", m["warm_lit"] >= c["warm_lit_min"], f"lit R-B {m['warm_lit']:.1f} < {c['warm_lit_min']}: lights not warm")
    need("cold", m["cold_shadow"] <= c["cold_shadow_max"], f"shadow R-B {m['cold_shadow']:.1f} > {c['cold_shadow_max']}: shadows not cold")
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
        ys, xs = np.nonzero(f[..., 3] >= 240)
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
        # only soft pixels over 2 px from alpha >= 240, so the bottle's antialiased outline is not read as the ring
        soft = (last[..., 3] > 16) & (last[..., 3] < 240) & ~neighbours(neighbours(last[..., 3] >= 240))
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


def self_test() -> list[str]:
    """The metrics and gates on images with known answers: a flat fill, a white chrome blob and a neutral grey one must fail."""
    errs = []
    flat = np.zeros((48, 48, 4), np.uint8)
    flat[8:40, 8:40] = (90, 90, 90, 255)
    errs += ["self-test: a flat neutral fill passed" for _ in [0] if not static_errors("flat", flat, (24, 24), measure("flat", flat))]
    chrome = flat.copy()
    chrome[8:40, 8:40] = (250, 250, 250, 255)
    if not any("chrome" in e for e in static_errors("chrome", chrome, (24, 24), measure("chrome", chrome))):
        errs.append("self-test: a white blob did not trip the chrome gate")
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
                  f"contrast {m['contrast']:.4f} form {m['form_ratio']:.2f} warm {m['warm_lit']:.1f} cold {m['cold_shadow']:.1f} "
                  f"medS {m['median_s']:.3f} chrome {m['chrome']:.4f} peakV {m['peak_v']:.3f}")
    print(f"check_iso: {len(names)} objects, {len(errs)} errors")
    return 1 if errs else 0


if __name__ == "__main__":
    sys.exit(main())
