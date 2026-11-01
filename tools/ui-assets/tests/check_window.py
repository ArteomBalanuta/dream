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
    for line in re.findall(r"^\s*(?:import|from)\s+([\w.]+)", src, flags=re.M):
        if line.split(".")[0] not in ("__future__", "math", "sys", "pathlib", "numpy", "PIL", "uiart"):
            fail(f"generator imports {line}")


def check_colours() -> None:
    e = px("cell.empty")
    near(ua.probe(e, (20, 20, 40, 40)), "cell.tile", 6, "cell.empty face")
    near(e[:4, 16:64, :3].reshape(-1, 3).max(axis=0), "cell.tile.top", 6, "cell.empty top bevel peak")
    right = np.median(e[16:64, 74:80, :3].reshape(-1, 3), axis=0)
    if not (0x8F - 6 <= right.mean() <= 0xA7 + 6):
        fail(f"cell.empty right bevel {right}")
    near(ua.probe(e, (0, 20, 6, 40)), "#212123", 8, "cell.empty left")
    near(ua.probe(e, (20, 74, 40, 6)), "#272727", 8, "cell.empty bottom")
    near(ua.probe(px("cell.occupied"), (20, 20, 40, 40)), "cell.occupied", 6, "cell.occupied")
    near(ua.probe(px("cell.hover"), (20, 20, 40, 40)), "cell.hover", 6, "cell.hover")
    inv = px("cell.invalid")
    near(ua.probe(inv, (20, 20, 40, 40)), "cell.invalid", 6, "cell.invalid")
    near(ua.probe(inv, (0, 20, 2, 40)), "cell.invalid.lattice.v", 6, "cell.invalid lattice v")
    near(ua.probe(inv, (20, 0, 40, 2)), "cell.invalid.lattice.h", 6, "cell.invalid lattice h")
    occ = px("cell.occupied")
    near(ua.probe(occ, (0, 20, 2, 40)), "cell.lattice.v", 6, "cell.occupied lattice v")
    near(ua.probe(occ, (20, 0, 40, 2)), "cell.lattice.h", 6, "cell.occupied lattice h")
    hov = px("cell.hover")
    near(ua.probe(hov, (0, 20, 2, 40)), "cell.hover.lattice.v", 6, "cell.hover lattice v")
    near(ua.probe(hov, (20, 0, 40, 2)), "cell.hover.lattice.h", 6, "cell.hover lattice h")

    # bevels: raised means the right and top sides are brighter than the left and bottom
    lit = (e[2, 40, :3].mean() + e[40, 77, :3].mean()) / 2
    dark = (e[40, 2, :3].mean() + e[77, 40, :3].mean()) / 2
    if lit <= dark + 60:
        fail(f"cell.empty right-top {lit} not brighter than left-bottom {dark}")

    # overlay over occupied lands on hover
    ov = px("cell.overlay.hover")
    a = ov[40, 40, 3] / 255
    if not 0.24 <= a <= 0.26 or ov[..., 3].min() != ov[..., 3].max():
        fail(f"cell.overlay.hover alpha {a} not flat 25%")
    mix = ov[40, 40, :3] * a + (ua.token("cell.occupied") * 255) * (1 - a)
    near(mix, "cell.hover", 4, "overlay over occupied")
    ovi = px("cell.overlay.invalid")
    near(ovi[40, 40, :3], "#51070A", 2, "cell.overlay.invalid rgb")
    if not 0.24 <= ovi[40, 40, 3] / 255 <= 0.26:
        fail("cell.overlay.invalid alpha")

    # slabs
    for name in ("win.slab.inventory", "win.slab.combine"):
        s = px(name)
        h, w = s.shape[:2]
        inner = s[24:-24, 24:-24, :3].reshape(-1, 3).mean(axis=0)
        near(inner, "stone.base", 8, f"{name} mean")
        if inner[2] < inner[0]:
            fail(f"{name}: blue below red {inner}")
        if (s[..., 3] != 255).any():
            fail(f"{name}: must be opaque, no world through the corners")
        near(ua.probe(s, (w // 2 - 40, 0, 80, 2)), "stone.edge", 10, f"{name} edge shadow")

    # wells and currency field
    for name, (w, h) in (("well.1x1", (20, 20)), ("well.2x2", (40, 40)), ("well.3x2", (60, 40)), ("well.2x3", (40, 60))):
        s = px(name)
        near(ua.probe(s, (w * 2 - 10, h * 2 - 10, 20, 20)), "well.fill", 6, f"{name} interior")
        if s[2, w * 2, :3].max() > 12:
            fail(f"{name}: top wall not black")
        near(s[h * S - 5, 3:60, :3].reshape(-1, 3).max(axis=0), "well.bevel.hi", 14, f"{name} hot lip")
        lo, hi = s[h * S - 4, 16, :3].mean(), s[h * S - 4, w * S - 12, :3].mean()
        if lo <= hi:
            fail(f"{name}: bottom lip must fall away to the right")
        if s[20, 2, :3].mean() >= s[h * S - 20, 2, :3].mean():
            fail(f"{name}: left lip must brighten downwards")
    c = px("currency.field")
    near(ua.probe(c, (200, 24, 60, 30)), "currency.fill", 6, "currency.field interior")
    w = c.shape[1]
    if c[-4, int(w * 0.8), :3].mean() < 235 or c[-4, int(w * 0.8), :3].mean() < c[-4, int(w * 0.3), :3].mean() + 20:
        fail("currency.field gloss must sit in the right third")
    near(ua.probe(px("currency.plate"), (8, 8, 32, 16)), "currency.label.plate", 6, "currency.plate")

    # tooltip
    t = px("tip.panel")
    if abs(int(np.median(t[..., 3])) - 204) > 2 or t[..., :3].max() != 0:
        fail("tip.panel alpha or rgb")
    b = px("tip.band")
    if b[..., 3].min() != 255:
        fail("tip.band not opaque")
    near(ua.probe(b, (0, 0, 48, 32)), "tip.band", 0, "tip.band")

    # strip, linebox, plates, rod, arrow
    near(ua.probe(px("win.strip"), (8, 8, 16, 16)), "strip.fill", 3, "win.strip")
    lb = px("win.linebox")
    if abs(int(lb[0, 0, 3]) - 46) > 1 or lb[..., :3].max() != 0:
        fail("win.linebox not black @18%")
    near(ua.probe(px("btn.plate24.normal"), (28, 28, 40, 40)), "btn.face", 6, "btn face")
    p = px("btn.plate24.normal")
    near(ua.probe(p, (30, 0, 36, 2)), "btn.bevel.top", 6, "btn top bevel")
    near(ua.probe(p, (0, 30, 3, 36)), "btn.wall", 6, "btn left wall")
    if px("btn.plate24.hover")[28:68, 28:68, :3].mean() <= p[28:68, 28:68, :3].mean() * 1.05:
        fail("hover plate must be brighter")
    pr = px("btn.plate24.pressed")
    if pr[2, 48, :3].mean() >= pr[-3, 48, :3].mean():
        fail("pressed plate must swap bevels")
    # every state has to read at a glance, not just differ
    for base, other, floor in (
        ("btn.plate24.normal", "btn.plate24.hover", 14), ("btn.plate24.normal", "btn.plate24.pressed", 10), ("btn.plate24.normal", "btn.plate24.disabled", 10),
        ("btn.plate44x32.normal", "btn.plate44x32.hover", 14), ("btn.plate44x32.normal", "btn.plate44x32.pressed", 10),
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

    rod = px("win.scroll.rod")
    mid = rod[:, rod.shape[1] // 2, :3].mean(axis=1)
    if mid.max() < 110 or mid.argmax() > 16:
        fail("rod: streak must sit in the upper third")
    if rod[20, 8 * S, 3] == 0 or rod[1, 30 * S, 3] != 0 or rod[1, 12 * S, 3] == 0:
        fail("rod: collars must stand taller than the rod")


def check_silhouettes() -> None:
    names = ["pet", "helm", "gloves", "pants", "boots", "wings", "weapon.left", "armor", "weapon.right", "pendant", "ring"]
    peaks = {}
    for n in names:
        s = px(f"sil.{n}")
        solid = s[s[..., 3] > 128]
        if solid.size == 0:
            fail(f"sil.{n}: empty")
            continue
        if np.abs(solid[:, 0].astype(int) - solid[:, 2].astype(int)).max() > 12:
            fail(f"sil.{n}: not neutral")
        peaks[n] = np.percentile(solid[:, :3].mean(axis=1), 99.9)
        cover = (s[..., 3] > 128).mean()
        if cover < 0.05:
            fail(f"sil.{n}: covers only {cover:.2f}")
        if n == "wings":
            if cover < 0.3:
                fail(f"sil.wings: covers only {cover:.2f}, want a large spread pair")
            body = np.median(solid[:, :3].mean(axis=1))
            if body < 42:
                fail(f"sil.wings: body {body:.0f} too dim")
            centre = s[:, s.shape[1] // 2 - 8 : s.shape[1] // 2 + 8, 3]
            if (centre > 128).mean() > 0.02:
                fail("sil.wings: no central knot, the pair must stand apart")
    for n in ("pet", "wings", "pendant"):
        if peaks.get(n, 0) > 0x7E + 6:
            fail(f"sil.{n}: dim peak {peaks[n]:.0f} over #7E7E7E")
    if peaks.get("armor", 0) < max(peaks[n] for n in peaks if n != "armor"):
        fail(f"sil.armor must be brightest: {peaks}")
    a = px("sil.armor")
    if a[a[..., 3] > 128][:, :3].max() < 250:
        fail("sil.armor needs a sil.hi peak")


def check_seams() -> None:
    # 3x3 empty tiles: same pixels at every period, an opaque block, and only the intended junction
    e = px("cell.empty")
    if e[..., 3].min() != 255:
        fail("cell.empty must be opaque")
    block = np.tile(e, (3, 3, 1)).astype(int)
    n = e.shape[0]
    face = block[:, :, :3].mean(axis=2)
    for k in (1, 2):
        # along a junction the two neighbouring columns stay uniform, so tiles line up with no step
        for col in (k * n - 1, k * n):
            if np.ptp(face[n + 12 : n + 68, col]) > 40:
                fail(f"cell.empty 3x3: column {col} uneven")
        for row in (k * n - 1, k * n):
            if np.ptp(face[row, n + 12 : n + 68]) > 40:
                fail(f"cell.empty 3x3: row {row} uneven")
    if abs(face[n // 2, n // 2] - face[n + n // 2, n + n // 2]) > 1:
        fail("cell.empty 3x3: faces differ")
    for name in ("cell.occupied", "cell.hover", "cell.invalid"):
        c = px(name)
        if (c[:, 0] != c[:, -1]).any() or (c[0] != c[-1]).any():
            fail(f"{name}: lattice edges do not match")

    # side tiles wrap, chain repeats every 5.5 u, the right tile ends 2 u early
    left, right = px("win.side.left"), px("win.side.right")
    for tag, s in (("left", left), ("right", right)):
        if np.abs(s[0, :, :3].astype(int) - s[-1, :, :3].astype(int)).mean() > 16:
            fail(f"win.side.{tag}: visible wrap seam")
    if right[:, -8:, 3].max() != 0 or right[:, 40, 3].min() != 255:
        fail("win.side.right: last 2 u must be transparent, the art opaque")
    if left[..., 3].min() != 255:
        fail("win.side.left must be opaque")
    col = left[:, 10:28, :3].astype(int)
    if np.abs(col - np.roll(col, 22, axis=0)).mean() > 12:
        fail("chain period is not 5.5 u")
    rim = left[:, 1:4, :3].mean(axis=(1, 2))
    if np.percentile(rim, 95) < 0xC7 or rim.min() < 70:
        fail(f"rim out of range {rim.min()} {np.percentile(rim, 95)}")

    # crowns and bottom join the columns; art ends at x=188
    tile_h = left.shape[0]
    for name, h_u in (("win.crown.inventory", 41), ("win.crown.combine", 40), ("win.bottom", 16)):
        c = px(name)
        if c[:, 188 * S :, 3].max() != 0:
            fail(f"{name}: art past x=188")
        if name == "win.bottom":
            row = (416 - 41) * S % tile_h
            d = np.abs(c[0, :48, :3].astype(int) - left[row - 1 if False else row, :48, :3].astype(int)).mean()
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


def main() -> int:
    gen_window.main(ROOT)
    check_sprites()
    check_source()
    check_colours()
    check_silhouettes()
    check_seams()
    errors.extend(ua.determinism_errors(gen_window.main, ["window"]))
    for e in errors:
        print("FAIL", e)
    print(f"check_window: {len(errors)} failure(s)")
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
