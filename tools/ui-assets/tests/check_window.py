"""Checks for dream/ui/window: plan 07 sections "Checks" (All, Window). Exits nonzero on failure."""
from __future__ import annotations

import re
import sys
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
TOOLS = HERE.parent
sys.path.insert(0, str(TOOLS))

import gen_window  # noqa: E402
import uiart as ua  # noqa: E402

ROOT = TOOLS.parents[1] / "dream" / "ui"
JSON = ROOT / "window" / "window.json"
S = ua.SCALE
errors: list[str] = []


def fail(msg: str) -> None:
    errors.append(msg)


def px(name: str) -> np.ndarray:
    return ua.sprite_pixels(JSON, name)


def near(actual, want, tol, tag) -> None:
    w = ua.token(want) * 255 if isinstance(want, str) else np.array(want, dtype=float)
    d = np.abs(np.array(actual, dtype=float) - np.round(w))
    if d.max() > tol:
        fail(f"{tag}: {tuple(int(v) for v in actual)} vs {want} (tol {tol})")


def expected() -> dict[str, tuple[int, int]]:
    m: dict[str, tuple[int, int]] = {
        "win.crown.inventory": (190, 41), "win.crown.combine": (190, 40), "win.side.left": (12, 44),
        "win.side.right": (13, 44), "win.bottom": (190, 16), "win.slab.inventory": (165, 387),
        "win.slab.combine": (165, 388), "win.strip": (8, 8), "win.linebox": (8, 8), "win.scroll.rod": (158, 10),
        "well.1x1": (20, 20), "well.2x2": (40, 40), "well.3x2": (60, 40), "well.2x3": (40, 60),
        "sil.wings": (60, 40), "sil.pendant": (20, 20), "sil.ring": (20, 20),
        "currency.field": (113, 18), "currency.plate": (12, 8), "tip.panel": (12, 12), "tip.band": (12, 8),
    }
    for n in ("pet", "helm", "gloves", "pants", "boots"):
        m[f"sil.{n}"] = (40, 40)
    for n in ("weapon.left", "armor", "weapon.right"):
        m[f"sil.{n}"] = (40, 60)
    for n in ("empty", "occupied", "hover", "invalid", "overlay.hover", "overlay.invalid"):
        m[f"cell.{n}"] = (20, 20)
    for st in ("normal", "hover"):
        m[f"win.setoption.tab.{st}"] = (33, 6)
        m[f"win.arrow.down.{st}"] = (13, 7)
        m[f"glyph.close.{st}"] = m[f"glyph.repair.{st}"] = m[f"glyph.store.{st}"] = (18, 18)
        m[f"glyph.combine.{st}"] = (28, 24)
    for st in ("normal", "hover", "pressed", "disabled"):
        m[f"btn.plate24.{st}"] = (24, 24)
    for st in ("normal", "hover", "pressed"):
        m[f"btn.plate44x32.{st}"] = (44, 32)
    return m


def check_sprites() -> None:
    import json

    doc = json.loads(JSON.read_text(encoding="utf-8"))
    want = expected()
    if set(doc["sprites"]) != set(want):
        fail(f"sprite set differs: missing {sorted(set(want) - set(doc['sprites']))}, extra {sorted(set(doc['sprites']) - set(want))}")
    for name, (w, h) in want.items():
        r = doc["sprites"].get(name)
        if r and (r["w"], r["h"]) != (w * S, h * S):
            fail(f"{name}: size {r['w']}x{r['h']} px, want {w * S}x{h * S}")
    if doc["extrude"] != 4:
        fail("extrude must be 4")
    for name, r in doc["sprites"].items():
        if r["slice"] and any(v % 4 for v in r["slice"]):
            fail(f"{name}: slice not multiples of 4")
    for name in ("well.1x1", "well.2x2", "well.3x2", "well.2x3", "currency.field"):
        if doc["sprites"][name]["slice"] != [12, 12, 12, 12]:
            fail(f"{name}: slice 3 u expected")
    if doc["sprites"]["win.strip"]["slice"] != [8, 8, 8, 8]:
        fail("win.strip slice")
    if doc["sprites"]["tip.panel"]["slice"] != [16, 16, 16, 16]:
        fail("tip.panel slice")
    if doc["sprites"]["tip.band"]["slice"] != [16, 0, 16, 0]:
        fail("tip.band slice")
    for e in ua.validate_atlas(JSON, {"require4": True}):
        fail(e)


def check_source() -> None:
    src = (TOOLS / "gen_window.py").read_text(encoding="utf-8")
    for bad in ("ImageFont", "urllib", "requests", "reference/", ".webp", "dream/fonts"):
        if bad in src:
            fail(f"generator contains {bad}")
    allowed = ("__future__", "math", "sys", "pathlib", "numpy", "PIL", "uiart", "forge", "os", "concurrent", "dataclasses", "zlib")
    for line in re.findall(r"^\s*(?:import|from)\s+([\w.]+)", src, flags=re.M):
        if line.split(".")[0] not in allowed:
            fail(f"generator imports {line}")


def luma(a: np.ndarray) -> np.ndarray:
    a = a[..., :3].astype(np.float32) / 255
    return 0.2126 * a[..., 0] + 0.7152 * a[..., 1] + 0.0722 * a[..., 2]


def med(img: np.ndarray, x0: float, y0: float, x1: float, y1: float) -> float:
    """Median luma of a rectangle given in u."""
    return float(np.median(luma(img[int(y0 * S) : int(y1 * S), int(x0 * S) : int(x1 * S)])))


def check_colours() -> None:
    e = px("cell.empty")
    face = med(e, 5, 5, 15, 15)
    if not 0.05 <= face <= 0.24:
        fail(f"cell.empty face luma {face:.3f} outside .05..0.24: the grid must stay quiet")
    lit = (med(e, 6, 0.6, 14, 1.8) + med(e, 0.6, 6, 1.8, 14)) / 2
    dark = (med(e, 6, 18.2, 14, 19.4) + med(e, 18.2, 6, 19.4, 14)) / 2
    if lit < dark + 0.07:
        fail(f"cell.empty top-left bevel {lit:.3f} not brighter than bottom-right {dark:.3f}: the key light comes from the upper left")
    if e[..., 3].min() != 255:
        fail("cell.empty must be opaque")
    occ, hov, inv = px("cell.occupied"), px("cell.hover"), px("cell.invalid")
    for name, c in (("cell.occupied", occ), ("cell.hover", hov), ("cell.invalid", inv)):
        v = med(c, 4, 4, 16, 16)
        if not 0.025 <= v <= 0.20:
            fail(f"{name}: face luma {v:.3f} outside .025..0.2")
    hm = np.median(hov[16:64, 16:64, :3].reshape(-1, 3), axis=0)
    im_ = np.median(inv[16:64, 16:64, :3].reshape(-1, 3), axis=0)
    if hm[2] < hm[0] + 8:
        fail(f"cell.hover must lean blue: {hm}")
    if im_[0] < im_[2] + 8:
        fail(f"cell.invalid must lean red: {im_}")
    for name, c in (("cell.occupied", occ), ("cell.hover", hov), ("cell.invalid", inv)):
        # the half lines on each edge make a lattice when tiled, darker than the face
        edge = med(c, 0, 4, 0.5, 16)
        if edge >= med(c, 4, 4, 16, 16) * 0.85:
            fail(f"{name}: edge lattice line not visible ({edge:.3f})")

    # overlay over occupied lands near hover: both are flat 25% tints
    ov = px("cell.overlay.hover")
    a = ov[40, 40, 3] / 255
    if not 0.24 <= a <= 0.26 or ov[..., 3].min() != ov[..., 3].max():
        fail(f"cell.overlay.hover alpha {a} not flat 25%")
    ovi = px("cell.overlay.invalid")
    near(ovi[40, 40, :3], "#51070A", 2, "cell.overlay.invalid rgb")
    if not 0.24 <= ovi[40, 40, 3] / 255 <= 0.26:
        fail("cell.overlay.invalid alpha")

    # slabs: dark, cool, opaque, the stone falls into shadow at its edges
    for name in ("win.slab.inventory", "win.slab.combine"):
        s_ = px(name)
        h, w = s_.shape[:2]
        inner = s_[24:-24, 24:-24, :3].reshape(-1, 3).mean(axis=0)
        v = float(luma(s_[24:-24, 24:-24]).mean())
        if not 0.14 <= v <= 0.30:
            fail(f"{name}: mean luma {v:.3f} outside .14..0.30")
        if inner[2] < inner[0] - 2:
            fail(f"{name}: blue below red {inner}")
        if (s_[..., 3] != 255).any():
            fail(f"{name}: must be opaque, no world through the corners")
        if med(s_, 0, 40, 2, 120) > v * 0.8 and med(s_, 40, 0, 120, 2) > v * 0.8:
            fail(f"{name}: edges do not fall into shadow")

    # wells and the currency field: near-black floor, lit lip on the far walls
    for name, (w, h) in (("well.1x1", (20, 20)), ("well.2x2", (40, 40)), ("well.3x2", (60, 40)), ("well.2x3", (40, 60))):
        s_ = px(name)
        if s_[..., 3].min() != 255:
            fail(f"{name}: must be opaque")
        floor = med(s_, w / 2 - 3, h / 2 - 3, w / 2 + 3, h / 2 + 3)
        if floor > 0.10:
            fail(f"{name}: floor luma {floor:.3f}, a recess must stay dark")
        right = med(s_, w - 3.6, 5, w - 2.9, h - 5)
        left = med(s_, 2.8, 5, 3.5, h - 5)
        bottom = med(s_, 5, h - 3.6, w - 5, h - 2.9)
        top = med(s_, 5, 2.8, w - 5, 3.5)
        if right <= left or bottom <= top:
            fail(f"{name}: the pocket walls facing the light must be brighter (right {right:.3f} left {left:.3f} bottom {bottom:.3f} top {top:.3f})")
    c = px("currency.field")
    if med(c, 6, 5, 100, 12) > 0.10:
        fail("currency.field: interior must stay dark")
    if px("currency.plate")[..., 3].min() != 255:
        fail("currency.plate must be opaque")

    # tooltip frame: black 80% in the middle, a forged opaque rim
    t = px("tip.panel")
    mid = t[20:-20, 20:-20]
    if int(np.median(mid[..., 3])) != 204 or mid[..., :3].max() != 0:
        fail("tip.panel: the middle must be rgb 0 at alpha 204")
    if t[0, t.shape[1] // 2, 3] < 250:
        fail("tip.panel: the rim must be opaque")
    b = px("tip.band")
    if b[..., 3].min() != 255:
        fail("tip.band not opaque")

    # strip, linebox, plates, rod, arrow
    near(ua.probe(px("win.strip"), (8, 8, 16, 16)), "#0A0A0B", 4, "win.strip")
    lb = px("win.linebox")
    if abs(int(lb[0, 0, 3]) - 46) > 1 or lb[..., :3].max() != 0:
        fail("win.linebox not black @18%")
    p = px("btn.plate24.normal")
    if not 0.05 <= med(p, 7, 7, 17, 17) <= 0.30:
        fail("btn.plate24: face luma out of range")
    if med(p, 4, 0.3, 20, 1.3) <= med(p, 4, 22.7, 20, 23.7) + 0.02:
        fail("btn.plate24: the rim facing the light must be brighter than the far rim")
    if px("btn.plate24.hover")[28:68, 28:68, :3].mean() <= p[28:68, 28:68, :3].mean() * 1.05:
        fail("hover plate must be brighter")
    pr = px("btn.plate24.pressed")
    if med(pr, 4, 0.3, 20, 1.3) >= med(p, 4, 0.3, 20, 1.3):
        fail("pressed plate must sink: its lit rim is dimmer than the normal one")
    # every state has to read at a glance, not just differ
    for base, other, floor in (
        ("btn.plate24.normal", "btn.plate24.hover", 14), ("btn.plate24.normal", "btn.plate24.pressed", 8), ("btn.plate24.normal", "btn.plate24.disabled", 8),
        ("btn.plate44x32.normal", "btn.plate44x32.hover", 14), ("btn.plate44x32.normal", "btn.plate44x32.pressed", 8),
        ("win.setoption.tab.normal", "win.setoption.tab.hover", 10), ("win.arrow.down.normal", "win.arrow.down.hover", 8),
        ("glyph.close.normal", "glyph.close.hover", 8), ("glyph.repair.normal", "glyph.repair.hover", 8),
        ("glyph.store.normal", "glyph.store.hover", 8), ("glyph.combine.normal", "glyph.combine.hover", 8),
    ):
        x, y = px(base).astype(float), px(other).astype(float)
        mask = (x[..., 3] > 128) & (y[..., 3] > 128)
        d = np.abs(x[..., :3] - y[..., :3]).mean(axis=2)[mask].mean()
        print(f"state {other}: {d:.1f}")
        if d < floor:
            fail(f"{other}: only {d:.1f} from {base}, want {floor}")
    # glyphs are warm metal: red above blue, and not a flat sticker (some spread in value)
    for n in ("close", "repair", "store", "combine"):
        g = px(f"glyph.{n}.normal")
        solid = g[g[..., 3] > 200][:, :3].astype(float)
        if solid.mean(axis=0)[0] < solid.mean(axis=0)[2] + 25:
            fail(f"glyph.{n}: not warm {solid.mean(axis=0)}")
        if solid.mean(axis=1).std() < 14:
            fail(f"glyph.{n}: flat, no relief")
        if np.percentile(solid.mean(axis=1), 99) > 235:
            fail(f"glyph.{n}: blown highlights, brass must read worn")

    rod = px("win.scroll.rod")
    mid_ = rod[:, rod.shape[1] // 2, :3].mean(axis=1)
    if mid_.argmax() > 16:
        fail("rod: streak must sit in the upper third")
    if rod[20, 8 * S, 3] == 0 or rod[1, 30 * S, 3] != 0 or rod[1, 12 * S, 3] == 0:
        fail("rod: collars must stand taller than the rod")
    if np.percentile(luma(rod[rod[..., 3] > 200]), 99) > 0.62:
        fail("rod: highlights too hot, the rod is dark steel")


def check_silhouettes() -> None:
    names = ["pet", "helm", "gloves", "pants", "boots", "wings", "weapon.left", "armor", "weapon.right", "pendant", "ring"]
    peaks = {}
    for n in names:
        s_ = px(f"sil.{n}")
        solid = s_[s_[..., 3] > 128]
        if solid.size == 0:
            fail(f"sil.{n}: empty")
            continue
        if np.abs(solid[:, 0].astype(int) - solid[:, 2].astype(int)).max() > 16:
            fail(f"sil.{n}: not neutral")
        peaks[n] = np.percentile(solid[:, :3].mean(axis=1), 99.5)
        cover = (s_[..., 3] > 128).mean()
        if cover < 0.04:
            fail(f"sil.{n}: covers only {cover:.2f}")
        if peaks[n] > 0.82 * 255:
            fail(f"sil.{n}: peak {peaks[n]:.0f} is chrome, a recessed relief stays dull")
        if n == "wings":
            if cover < 0.25:
                fail(f"sil.wings: covers only {cover:.2f}, want a large spread pair")
            body = np.median(solid[:, :3].mean(axis=1))
            if body < 32:
                fail(f"sil.wings: body {body:.0f} too dim")
            centre = s_[:, s_.shape[1] // 2 - 8 : s_.shape[1] // 2 + 8, 3]
            if (centre > 128).mean() > 0.03:
                fail("sil.wings: no central gap, the pair must stand apart")
    if peaks.get("armor", 0) < sorted(peaks.values())[-3]:
        fail(f"sil.armor must be among the brightest: {peaks}")


def check_seams() -> None:
    # 3x3 empty tiles: same pixels at every period, an opaque block, and only the intended junction
    e = px("cell.empty")
    if e[..., 3].min() != 255:
        fail("cell.empty must be opaque")
    block = np.tile(e, (3, 3, 1)).astype(int)
    n = e.shape[0]
    face = block[:, :, :3].mean(axis=2)
    if abs(face[n // 2, n // 2] - face[n + n // 2, n + n // 2]) > 1:
        fail("cell.empty 3x3: faces differ")
    for name in ("cell.occupied", "cell.hover", "cell.invalid"):
        c = px(name)
        if np.abs(c[:, 0].astype(int) - c[:, -1].astype(int)).mean() > 3 or np.abs(c[0].astype(int) - c[-1].astype(int)).mean() > 3:
            fail(f"{name}: lattice edges do not match")

    # side tiles wrap, the chain repeats every 5.5 u, the right tile ends 2 u early
    left, right = px("win.side.left"), px("win.side.right")
    for tag, s_ in (("left", left), ("right", right)):
        if np.abs(s_[0, :, :3].astype(int) - s_[-1, :, :3].astype(int)).mean() > 16:
            fail(f"win.side.{tag}: visible wrap seam")
    if right[:, -8:, 3].max() != 0 or right[:, 40, 3].min() != 255:
        fail("win.side.right: last 2 u must be transparent, the art opaque")
    if left[..., 3].min() != 255:
        fail("win.side.left must be opaque")
    col = left[:, 10:28, :3].astype(int)
    if np.abs(col - np.roll(col, 22, axis=0)).mean() > 18:
        fail("chain period is not 5.5 u")
    ll = luma(left)
    if np.percentile(ll, 99) > 0.62 or ll.mean() > 0.20:
        fail(f"win.side.left too bright: mean {ll.mean():.3f} p99 {np.percentile(ll, 99):.3f}")

    # crowns and bottom join the columns; art ends at x=188
    tile_h = left.shape[0]
    for name, h_u in (("win.crown.inventory", 41), ("win.crown.combine", 40), ("win.bottom", 16)):
        c = px(name)
        if c[:, 188 * S :, 3].max() != 0:
            fail(f"{name}: art past x=188")
        if name == "win.bottom":
            row = (416 - 41) * S % tile_h
            d = np.abs(c[0, :48, :3].astype(int) - left[row, :48, :3].astype(int)).mean()
        else:
            row = (h_u - 41) * S % tile_h
            d = np.abs(c[-1, 4:44, :3].astype(int) - left[(row - 1) % tile_h, 4:44, :3].astype(int)).mean()
        if d > 12:
            fail(f"{name}: columns do not line up with the side tile ({d:.1f})")
    b = px("win.bottom")
    if b[8, 95 * S, 3] != 0:
        fail("win.bottom: must be open over the slab")
    if b[-8, 95 * S, 3] != 255:
        fail("win.bottom: must close below the slab")


def build_matches_committed() -> list[str]:
    """Reproducibility: a second build into a temp folder must equal the one just written, byte for byte."""
    import hashlib
    import tempfile

    errs = []
    with tempfile.TemporaryDirectory() as tmp:
        gen_window.main(Path(tmp))
        for f in ("window.png", "window.json"):
            a = hashlib.sha256((ROOT / "window" / f).read_bytes()).hexdigest()
            b = hashlib.sha256((Path(tmp) / "window" / f).read_bytes()).hexdigest()
            if a != b:
                errs.append(f"nondeterministic: window/{f}")
    return errs


def main() -> int:
    gen_window.main(ROOT)
    check_sprites()
    check_source()
    check_colours()
    check_silhouettes()
    check_seams()
    errors.extend(build_matches_committed())
    for e in errors:
        print("FAIL", e)
    print(f"check_window: {len(errors)} failure(s)")
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
