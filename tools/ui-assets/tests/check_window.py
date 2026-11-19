"""Checks for dream/ui/window: plan 07 sections "Checks" (All, Window). Exits nonzero on failure.

`--fast` skips the one render that checks the committed atlas against the generator's bytes (about 3 minutes).

This gate READS dream/ui/window and never writes it. It used to open with `gen_window.main(ROOT)` - it
regenerated the atlas into the repository and then compared those just-overwritten files with a temp render, so
what it called a committed-bytes check was two renders of the same source compared with each other. A gate that
mutates the thing it measures cannot detect staleness, and the failure is invisible: it reports success BECAUSE
it destroyed the evidence. That is worse than a gate that is always red, because nothing ever looks wrong. A
gate reads; a generator writes; build_all.py is where writing belongs.
"""
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
    for n in ("shop.cell.empty", "shop.cell.occupied", "cell.overlay.ember"):
        m[n] = (20, 20)
    m["shop.frame.ember"] = m["shop.frame.cold"] = (16, 16)
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
        # iron with a cold shadow and a warm highlight, never a tinted material: the average stays neutral
        if abs(float((solid[:, 0].astype(int) - solid[:, 2].astype(int)).mean())) > 8 or np.percentile(np.abs(solid[:, 0].astype(int) - solid[:, 2].astype(int)), 99.5) > 34:
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
    if peaks.get("armor", 0) < float(np.median(list(peaks.values()))) * 1.05:
        fail(f"sil.armor is the centrepiece and must stand above the median peak: {peaks}")


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
        c = np.asarray(px(name), dtype=int)
        # tiled, the half lines on the two edges make one line, and its two halves are lit differently: what must hold is that
        # the step across the join is no bigger than the step between neighbouring columns (and rows) just inside the tile
        join_x = np.abs(c[:, -1] - c[:, 0]).mean()
        join_y = np.abs(c[-1] - c[0]).mean()
        inside_x = max(np.abs(c[:, k] - c[:, k + 1]).mean() for k in range(0, 6))
        inside_y = max(np.abs(c[k] - c[k + 1]).mean() for k in range(0, 6))
        if join_x > inside_x * 1.6 + 2 or join_y > inside_y * 1.6 + 2:
            fail(f"{name}: tiling seam ({join_x:.1f}/{join_y:.1f} against {inside_x:.1f}/{inside_y:.1f})")

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


def check_shipped() -> None:
    """The committed atlas must BE what the generator makes: one render into a temp dir, compared with the files
    on disk that every other check in this file reads.

    This replaces a hand-rolled sha256 loop so window gets the same .json line-ending normalisation as the other
    gates (uiart._canon_shipped): the terrain manifest spent a wave always-red for want of it.

    One render, not two. The old pass rendered twice, and two renders equal to each other is a weaker property
    than a render equal to a fixed committed reference, which is what this asserts: any nondeterminism makes this
    comparison fail against the committed bytes. What one process rendering twice adds is catching per-call
    nondeterminism deterministically within a single run rather than probabilistically across runs - and at about
    400 s a render that is not worth a third pass here. uiart.determinism_errors(gen_window.main, ["window"]) is
    the call to add if that ever stops being true."""
    for e in ua.shipped_errors(gen_window.main, "window", ROOT / "window"):
        fail(e)


def rgb_mean(img: np.ndarray, sel: np.ndarray) -> np.ndarray:
    return img[..., :3][sel].astype(float).mean(axis=0)


def _seam_ok(c: np.ndarray) -> bool:
    c = np.asarray(c, dtype=int)
    jx, jy = np.abs(c[:, -1] - c[:, 0]).mean(), np.abs(c[-1] - c[0]).mean()
    ix = max(np.abs(c[:, k] - c[:, k + 1]).mean() for k in range(0, 6))
    iy = max(np.abs(c[k] - c[k + 1]).mean() for k in range(0, 6))
    return jx <= ix * 1.6 + 2 and jy <= iy * 1.6 + 2


def check_shop() -> None:
    """The five sprites the shop board draws: a grate cell that is measurably not the bag's niche, a stocked tile, a flash
    for the receiving bag cell and two 9-slice frames, ember (warm, heat only in its cavities) and cold (blue-grey)."""
    import json

    doc = json.loads(JSON.read_text(encoding="utf-8"))
    sp = doc["sprites"]
    for n in ("shop.cell.empty", "shop.cell.occupied", "cell.overlay.ember", "shop.frame.ember", "shop.frame.cold"):
        if n not in sp:
            fail(f"{n}: missing from the atlas")
            return
    for n in ("shop.cell.occupied", "cell.overlay.ember"):
        if sp[n]["tile"] != "xy":
            fail(f"{n}: tile {sp[n]['tile']!r}, want 'xy'")
    if sp["shop.cell.empty"]["tile"] != "none" or sp["shop.cell.empty"]["slice"]:
        fail("shop.cell.empty is a single cell: no tile, no slice")
    for n in ("shop.frame.ember", "shop.frame.cold"):
        if sp[n]["slice"] != [16, 16, 16, 16]:
            fail(f"{n}: slice {sp[n]['slice']}, want 4 u (16 px) all round")
    # the shop cell: opaque, quiet, and a different object from the bag cell
    sc, bc = px("shop.cell.empty"), px("cell.empty")
    if sc[..., 3].min() != 255:
        fail("shop.cell.empty must be opaque")
    face = med(sc, 5, 5, 15, 15)
    if not SHOP_FACE[0] <= face <= SHOP_FACE[1]:
        fail(f"shop.cell.empty face luma {face:.3f} outside {SHOP_FACE}: an icon must read over it")
    lit = (med(sc, 6, 0.6, 14, 1.8) + med(sc, 0.6, 6, 1.8, 14)) / 2
    dark = (med(sc, 6, 18.2, 14, 19.4) + med(sc, 18.2, 6, 19.4, 14)) / 2
    if lit < dark + SHOP_KEY:
        fail(f"shop.cell.empty rim {lit:.3f} not brighter than {dark:.3f} by {SHOP_KEY}: the key light comes from the upper left")
    diff = np.abs(sc[..., :3].astype(float) - bc[..., :3].astype(float)).mean()
    if diff < SHOP_DIFF:
        fail(f"shop.cell.empty differs from cell.empty by only {diff:.1f} (want {SHOP_DIFF}): the shop cell must read as another object")
    inner = (slice(int(4 * S), int(16 * S)),) * 2
    grate, niche = luma(sc[inner]).std(), luma(bc[inner]).std()
    if grate < niche * SHOP_TEXTURE:
        fail(f"shop.cell.empty interior contrast {grate:.3f} is not {SHOP_TEXTURE}x the niche's {niche:.3f}: no grate")
    # the stocked tile: a lattice like the bag's, dark, warm against it, and it tiles
    so, bo = px("shop.cell.occupied"), px("cell.occupied")
    v = med(so, 4, 4, 16, 16)
    if not 0.025 <= v <= 0.20:
        fail(f"shop.cell.occupied face luma {v:.3f} outside .025..0.2")
    if med(so, 0, 4, 0.5, 16) >= v * 0.85:
        fail("shop.cell.occupied: edge lattice line not visible")
    wo, wb = rgb_mean(so, so[..., 3] > 0), rgb_mean(bo, bo[..., 3] > 0)
    if (wo[0] - wo[2]) < (wb[0] - wb[2]) + SHOP_WARM:
        fail(f"shop.cell.occupied must lean warmer than cell.occupied by {SHOP_WARM}: R-B {wo[0] - wo[2]:.1f} against {wb[0] - wb[2]:.1f}")
    if not _seam_ok(so):
        fail("shop.cell.occupied: tiling seam")
    # the buy flash: warm, translucent, never solid, tiles
    ov = px("cell.overlay.ember")
    a = ov[..., 3].astype(float) / 255
    if not (FLASH_A[0] <= a.min() and a.max() <= FLASH_A[1]):
        fail(f"cell.overlay.ember alpha {a.min():.2f}..{a.max():.2f} outside {FLASH_A}")
    if a.std() < 0.02:
        fail("cell.overlay.ember is a flat fill: the heat must run in veins")
    wm = rgb_mean(ov, ov[..., 3] > 0)
    if wm[0] < wm[2] + FLASH_WARM:
        fail(f"cell.overlay.ember must lean warm: {wm.round()}")
    if not _seam_ok(ov):
        fail("cell.overlay.ember: tiling seam")
    # the frames: an opaque iron ring round a fully transparent middle, ember warm and cold blue
    n4 = 4 * S
    for name in ("shop.frame.ember", "shop.frame.cold"):
        f = px(name)
        mid = f[n4:-n4, n4:-n4, 3]
        if mid.max() != 0:
            fail(f"{name}: the 9-slice middle must be fully transparent (max alpha {mid.max()})")
        ring = f[int(1.0 * S) : int(2.0 * S), n4:-n4, 3]
        if ring.min() < 200:
            fail(f"{name}: the ring is not solid along its edge (min alpha {ring.min()})")
        for sl in (f[:n4, :n4, 3], f[:n4, -n4:, 3], f[-n4:, :n4, 3], f[-n4:, -n4:, 3]):
            if sl.max() < 250:
                fail(f"{name}: a corner is empty")
    fe, fc = px("shop.frame.ember"), px("shop.frame.cold")
    me, mc = rgb_mean(fe, fe[..., 3] > 200), rgb_mean(fc, fc[..., 3] > 200)
    if me[0] - me[2] < EMBER_RB:
        fail(f"shop.frame.ember must lean warm: R-B {me[0] - me[2]:.1f}, want {EMBER_RB}")
    if mc[2] - mc[0] < COLD_BR:
        fail(f"shop.frame.cold must lean blue: B-R {mc[2] - mc[0]:.1f}, want {COLD_BR}")
    solid_e = fe[..., 3] > 200
    hot = solid_e & (fe[..., 0].astype(int) > 150) & (fe[..., 0].astype(int) > 2 * fe[..., 2].astype(int))
    share = hot.sum() / max(int(solid_e.sum()), 1)
    if not EMBER_HOT[0] <= share <= EMBER_HOT[1]:
        fail(f"shop.frame.ember: glowing share {share:.3f} outside {EMBER_HOT}: heat belongs in the cavities, the metal stays metal")
    if ((fc[..., 3] > 200) & (fc[..., 0].astype(int) > 150) & (fc[..., 0].astype(int) > 2 * fc[..., 2].astype(int))).any():
        fail("shop.frame.cold: has heat in it")
    le = luma(fe)[solid_e].mean()
    if le > EMBER_LUMA:
        fail(f"shop.frame.ember: the ring is too bright overall ({le:.3f} > {EMBER_LUMA})")


# Measured on the shipped sprites; each constant carries what was measured (filled in after the render).
SHOP_FACE = (0.05, 0.24)   # shop.cell.empty face luma: measured 0.115 (cell.empty 0.133)
SHOP_KEY = 0.06            # lit-minus-dark rim luma: measured 0.112 (cell.empty 0.165)
SHOP_DIFF = 12.0           # mean abs RGB difference to cell.empty: measured 17.9
SHOP_TEXTURE = 2.0         # interior luma std against the niche's: measured 2.62 (0.116 against 0.044)
SHOP_WARM = 4.0            # R-B lead of shop.cell.occupied over cell.occupied: measured 5.6 (+3.0 against -2.6)
FLASH_A = (0.15, 0.60)     # cell.overlay.ember alpha range: measured 0.259..0.506, std 0.064
FLASH_WARM = 100.0         # cell.overlay.ember R-B: measured 150 (mean 193, 107, 43)
EMBER_RB = 25.0            # shop.frame.ember R-B over the ring: measured 44.8
COLD_BR = 10.0             # shop.frame.cold B-R over the ring: measured 18.0
EMBER_HOT = (0.03, 0.30)   # share of ring pixels that glow (R > 150 and R > 2B): measured 0.179
EMBER_LUMA = 0.32          # the ring is iron with heat in it, not a glowing band: measured mean luma 0.263


def main() -> int:
    check_sprites()
    check_source()
    check_colours()
    check_silhouettes()
    check_seams()
    check_shop()
    if "--fast" not in sys.argv:
        check_shipped()
    for e in errors:
        print("FAIL", e)
    print(f"check_window: {len(errors)} failure(s)")
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
