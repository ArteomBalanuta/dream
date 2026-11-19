"""Checks for dream/ui/hud. Exits nonzero on any failure. Usage: python check_hud.py [--fast]

--fast skips the determinism double run. Sizes and rules come from docs 07, Manifest, Checks and the
iteration 2 section; the numeric art gates live in check_art.py.
"""
import ast
import json
import re
import sys
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
TOOLS = HERE.parent
sys.path.insert(0, str(TOOLS))

import uiart as U  # noqa: E402

ROOT = TOOLS.parents[1] / "dream" / "ui" / "hud"
HUD = ROOT / "hud.json"
CUR = ROOT / "cursors.json"
GEN = TOOLS / "gen_hud.py"
NHP = ("normal", "hover", "pressed")

SIZES_U = {
    "hud.dragon.left": (118, 58), "hud.dragon.right": (118, 58),
    "hud.talon.left": (71, 52), "hud.talon.right": (71, 52),
    "gem.setting.left": (62, 56), "gem.setting.right": (62, 56),
    "gem.hp.spill": (128, 64), "gem.mp.spill": (128, 64),
    "hud.base.left": (199, 48), "hud.base.right": (199, 48), "hud.base.centre": (242, 48), "hud.gapfill": (64, 48),
    "gauge.left.fill": (15, 35), "gauge.right.fill": (15, 35), "gauge.empty": (15, 35),
    "gauge.cap.left": (33, 46), "gauge.cap.right": (33, 46),
    "hud.coordsplate": (56, 22),
    "icon.scroll": (26, 16), "icon.party": (26, 16), "icon.pvp": (26, 16),
    "hud.ministat.off": (16, 11), "hud.ministat.on": (16, 11),
    "exp.frame": (207, 10), "exp.trough": (199, 6), "exp.fill": (8, 5), "exp.tick": (1, 6), "exp.cup": (12, 12),
    "qwe.notch": (18, 14), "qwe.letter.q": (7, 10), "qwe.letter.w": (7, 10), "qwe.letter.e": (7, 10),
    "skill.bracket": (32, 36), "skill.icon.default": (17, 26),
    "skill.tile.normal": (19, 28), "skill.tile.hover": (19, 28), "skill.tile.disabled": (19, 28),
    "iconbtn.plinth": (101, 30), "icon.banner": (22, 22), "icon.lens": (22, 22), "icon.satchel": (22, 22),
    "chat.rock": (64, 64), "chat.post.left": (5, 44), "chat.post.right": (5, 44), "chat.rail": (64, 4),
    "chat.input.frame": (253, 17), "chat.field.main": (8, 12), "chat.field.whisper": (8, 12),
    "chat.scroll.thumb": (14, 10), "chat.scroll.track": (14, 8), "chat.bracket": (3, 3), "chat.postbar": (8, 8),
}
for kind in ("hp", "mp"):
    for layer in ("full", "empty", "mask", "spec"):
        SIZES_U[f"gem.{kind}.{layer}"] = (50, 50)
for ch in "0123456789":
    SIZES_U[f"hudnum.{ch}"] = (8, 12)
SIZES_U["hudnum.slash"] = (8, 12)
for st in NHP:
    SIZES_U[f"hud.slab.{st}"] = (56, 22)
    SIZES_U[f"qwe.slot.q.{st}"] = (33, 28)
    SIZES_U[f"qwe.slot.w.{st}"] = (29, 28)
    SIZES_U[f"qwe.slot.e.{st}"] = (28, 28)
    SIZES_U[f"iconbtn.frame.{st}"] = (28, 28)
    SIZES_U[f"chat.btn.{st}"] = (14, 14)
    SIZES_U[f"chat.scroll.up.{st}"] = (14, 9)
    SIZES_U[f"chat.scroll.down.{st}"] = (14, 9)
CURSORS = ("cursor.pointer", "cursor.pressed", "cursor.grab", "cursor.attack", "cursor.forbid")

# sprite, rect px (x, y, w, h), token. Only the sprites whose colour is a design rule remain probes.
PROBES = [
    ("chat.field.main", (8, 8, 16, 16), "chat.input"),
    ("chat.field.whisper", (8, 8, 16, 16), "chat.whisper"),
    ("chat.postbar", (8, 12, 16, 12), "chat.post.bg"),
]


def near(a, b, tol):
    return all(abs(int(x) - int(y)) <= tol for x, y in zip(a, b))


def iou(a, b):
    a, b = a[..., 3] > 128, b[..., 3] > 128
    return (a & b).sum() / max((a | b).sum(), 1)


def luma(px):
    p = px[..., :3].astype(np.float32) / 255
    return 0.2126 * p[..., 0] + 0.7152 * p[..., 1] + 0.0722 * p[..., 2]


def check_generator(errs):
    src = GEN.read_text(encoding="utf-8")
    for word in ("ImageFont", "urllib", "requests", "reference/", ".webp", "dream/fonts"):
        if word in src:
            errs.append(f"generator contains {word}")
    allowed = {"uiart", "numpy", "PIL", "math", "sys", "pathlib", "__future__", "forge", "gemtrace", "dataclasses", "concurrent", "os"}
    for node in ast.walk(ast.parse(src)):
        mods = []
        if isinstance(node, ast.Import):
            mods = [a.name.split(".")[0] for a in node.names]
        elif isinstance(node, ast.ImportFrom):
            mods = [(node.module or "").split(".")[0]]
        for m in mods:
            if m not in allowed:
                errs.append(f"generator imports {m}")


def check_sizes(errs, doc, cur):
    for name, (w, h) in SIZES_U.items():
        r = doc["sprites"].get(name)
        if r is None:
            errs.append(f"missing {name}")
        elif (r["w"], r["h"]) != (w * 4, h * 4):
            errs.append(f"{name}: {r['w']}x{r['h']} px, want {w * 4}x{h * 4}")
    extra = set(doc["sprites"]) - set(SIZES_U)
    if extra:
        errs.append(f"unexpected sprites {sorted(extra)}")
    for name in CURSORS:
        r = cur["sprites"].get(name)
        if r is None:
            errs.append(f"missing {name}")
        elif (r["w"], r["h"]) != (128, 128):
            errs.append(f"{name}: not 128x128")
    for doc_ in (doc, cur):
        for name, r in doc_["sprites"].items():
            if r["slice"] and any(v % 4 for v in r["slice"]):
                errs.append(f"{name}: slice not multiple of 4")


def mirrored(a, b):
    """Equal under x flip wherever visible; RGB under A=0 is bleed and need not match."""
    b = b[:, ::-1]
    vis = a[..., 3] > 0
    return np.array_equal(a[..., 3], b[..., 3]) and np.array_equal(a[vis], b[vis])


def components(filled):
    """Number of 4-connected regions of a boolean image."""
    seen = np.zeros_like(filled, dtype=bool)
    h, w = filled.shape
    count = 0
    for y0, x0 in zip(*np.nonzero(filled)):
        if seen[y0, x0]:
            continue
        count += 1
        stack = [(y0, x0)]
        seen[y0, x0] = True
        while stack:
            y, x = stack.pop()
            for ny, nx in ((y - 1, x), (y + 1, x), (y, x - 1), (y, x + 1)):
                if 0 <= ny < h and 0 <= nx < w and filled[ny, nx] and not seen[ny, nx]:
                    seen[ny, nx] = True
                    stack.append((ny, nx))
    return count


def check_gems(errs, S):
    """Contract: full filled stone, empty dark glass, mask R/G/B/A channels, spec glints (docs 07 iteration 2)."""
    for kind, hue in (("hp", "red"), ("mp", "blue")):
        full, empty, mask, spec = (S(f"gem.{kind}.{x}") for x in ("full", "empty", "mask", "spec"))
        ins = full[..., 3] > 127
        rgb = full[..., :3].astype(int)
        # Blood and ink in a cage, not candy. These were a saturated body (R > 190 with G, B < 80, share >= 0.06), deep
        # facets at R 40..130 and hot highlights over luma 0.80 (share >= 0.01): the toy ruby. They are re-tuned to the
        # aged stone; every number is measured on the shipped sprites (hp / mp).
        if hue == "red":
            body = (rgb[..., 0] > 70) & (rgb[..., 0] > 1.8 * rgb[..., 1]) & (rgb[..., 0] > 1.8 * rgb[..., 2])
            deep = (rgb[..., 0] > 20) & (rgb[..., 0] < 130) & (rgb[..., 1] < 30) & (rgb[..., 2] < 40)
        else:
            body = (rgb[..., 2] > 70) & (rgb[..., 2] > 1.5 * rgb[..., 0]) & (rgb[..., 2] > 1.5 * rgb[..., 1])   # symmetric with the red test: no teal or cyan stone
            deep = (rgb[..., 2] > 20) & (rgb[..., 2] < 140) & (rgb[..., 0] < 30)
        fl = rgb / 255.0
        sat = (fl.max(axis=2) - fl.min(axis=2)) / np.maximum(fl.max(axis=2), 1e-6)
        iron = (sat < 0.30) & (luma(full) > 0.06) & (luma(full) < 0.6)
        for label, m, need in (("oxidised body", body, 0.35), ("deep glass", deep, 0.30), ("iron cage", iron, 0.06)):   # SHARE of the stone, measured hp / mp: body 0.467 / 0.396, deep 0.328 / 0.338, iron 0.108 / 0.096
            if (m & ins).sum() < need * ins.sum():
                errs.append(f"gem.{kind}.full: {label} cover {(m & ins).sum() / ins.sum():.3f} of the stone, want {need}")
        if (luma(full)[ins] > 0.80).mean() > 0.004:   # LUMA share over 0.80, measured 0.0017 / 0.0005 after the iteration-3 relight (0.0023 / 0.0003 before): no plastic hot spot survives
            errs.append(f"gem.{kind}.full: plastic highlights cover {(luma(full)[ins] > 0.80).mean():.4f} of the stone")
        if np.percentile(fl.max(axis=2)[ins], 99) > 0.86:   # MAX-CHANNEL (not luma) p99, measured 0.835 / 0.682
            errs.append(f"gem.{kind}.full: p99 channel {np.percentile(fl.max(axis=2)[ins], 99):.3f} over the 0.86 ceiling")
        # the glass alone (HSV S > 0.45; the near-neutral iron cage drags a whole-sprite mean down): lit through, still oxidised
        glass = ins & (sat > 0.45)
        gv = fl.max(axis=2)[glass]
        if np.percentile(gv, 95) < 0.40:   # measured glass p95 of the MAX CHANNEL 0.784 / 0.608 (luma is ~0.35 of it on the red stone); the C# ClassicHudGemTests counts the pixels that differ between fill 0 and 1
            errs.append(f"gem.{kind}.full: glass p95 {np.percentile(gv, 95):.3f} under 0.40: the fluid is not lit")
        if sat[glass].mean() < 0.70:   # measured 0.802 / 0.722
            errs.append(f"gem.{kind}.full: glass saturation {sat[glass].mean():.3f} under 0.70")
        if np.percentile(sat[ins], 50) > 0.85:   # measured median saturation 0.76 / 0.70
            errs.append(f"gem.{kind}.full: median saturation {np.percentile(sat[ins], 50):.2f} is candy")
        if not (np.array_equal(full[..., 3], empty[..., 3]) and np.array_equal(full[..., 3], mask[..., 3]) and np.array_equal(full[..., 3], spec[..., 3])):
            errs.append(f"gem.{kind}: alpha differs across layers")
        # mask: R rises upward and spans the stone, with no stray islands at 25, 50 and 75 percent
        R = mask[..., 0].astype(np.float32) / 255
        rows = np.nonzero(ins.any(axis=1))[0]
        top, bot = rows.min(), rows.max()
        band = max(8, (bot - top) // 6)
        r_top = R[top : top + band][ins[top : top + band]].mean()
        r_bot = R[bot - band : bot + 1][ins[bot - band : bot + 1]].mean()
        if not (r_top > 0.78 and r_bot < 0.22):
            errs.append(f"gem.{kind}.mask R does not run 0 at the bottom to 1 at the top (top {r_top:.2f}, bottom {r_bot:.2f})")
        if R[ins].min() > 0.08 or R[ins].max() < 0.92:
            errs.append(f"gem.{kind}.mask R spans only {R[ins].min():.2f}..{R[ins].max():.2f}")
        for level in (0.25, 0.5, 0.75):
            filled = ins & (R < level)
            share = filled.sum() / ins.sum()
            if abs(share - level) > 0.2:
                errs.append(f"gem.{kind}.mask: level {level} fills {share:.2f} of the stone")
            n = components(filled)
            if n > 5:
                errs.append(f"gem.{kind}.mask: level {level} leaves {n} separate pools")
        if mask[..., 1][ins].mean() < 40:
            errs.append(f"gem.{kind}.mask G (meniscus weight) is empty")
        if len(np.unique(mask[..., 2][ins])) < 8:
            errs.append(f"gem.{kind}.mask B (facet phase) has fewer than 8 values")
        # R, G, B are dilated past the rim, not left at 0
        outside = ~ins
        near_rim = outside.copy()
        near_rim[3:-3, 3:-3] &= ~(ins[:-6, 3:-3] | ins[6:, 3:-3] | ins[3:-3, :-6] | ins[3:-3, 6:])
        ring = outside & ~near_rim
        if ring.any() and (mask[..., :3][ring].max(axis=1) == 0).mean() > 0.2:
            errs.append(f"gem.{kind}.mask: RGB is not dilated past the rim")
        # empty glass is dark, spec is sparse glints
        lf, le = luma(full)[ins].mean(), luma(empty)[ins].mean()
        if not le < lf * 0.6:
            errs.append(f"gem.{kind}.empty not dimmed ({le:.3f} against {lf:.3f})")
        sp = luma(spec)[ins]
        if not 0.002 <= (sp > 0.35).mean() <= 0.08:
            errs.append(f"gem.{kind}.spec: glints cover {(sp > 0.35).mean():.3f} of the stone, want 0.002..0.08")
    if iou(S("gem.mp.full"), S("gem.hp.full")[:, ::-1]) < 0.98:
        errs.append("gem.mp silhouette is not gem.hp mirrored")


def check_setting_numbers_spill(errs, S):
    if not mirrored(S("gem.setting.left"), S("gem.setting.right")):
        errs.append("gem.setting.right does not mirror left")
    st = S("gem.setting.left")
    if st[..., 3].mean() < 100:
        errs.append("gem.setting is mostly empty")
    for kind in ("hp", "mp"):
        sp = S(f"gem.{kind}.spill").astype(np.float32) / 255
        a = sp[..., 3]
        h, w = a.shape
        centre = a[h // 2 - 40 : h // 2 + 40, w // 2 - 40 : w // 2 + 40]
        if centre.max() > 0.3:
            errs.append(f"gem.{kind}.spill covers the stone itself (alpha {centre.max():.2f})")
        if a.max() < 0.4 or a.max() > 1.0:
            errs.append(f"gem.{kind}.spill peaks at {a.max():.2f}")
        if a[0].max() > 0.02 or a[-1].max() > 0.02 or a[:, 0].max() > 0.02 or a[:, -1].max() > 0.02:
            errs.append(f"gem.{kind}.spill does not fade to zero at its border")
        c = sp[..., :3][a > 0.1].mean(axis=0)
        if kind == "hp" and not (c[0] > 2 * c[2] and c[0] > c[1]):
            errs.append(f"gem.hp.spill is not red ({c})")
        if kind == "mp" and not (c[2] > 2 * c[0] and c[2] > c[1]):
            errs.append(f"gem.mp.spill is not blue ({c})")
    digs = {c: S(f"hudnum.{c}") for c in "0123456789"}
    digs["/"] = S("hudnum.slash")
    names = list(digs)
    for a_, b_ in ((x, y) for i, x in enumerate(names) for y in names[i + 1 :]):
        d = np.abs(digs[a_][..., :3].astype(float) - digs[b_][..., :3].astype(float)).mean()
        if d < 4.0:
            errs.append(f"hudnum {a_} and {b_} differ by only {d:.1f}")
    for c, px in digs.items():
        opaque = px[..., 3] > 200
        if opaque.sum() < 0.08 * opaque.size:
            errs.append(f"hudnum.{c} is almost empty")
        lit = luma(px)[opaque]
        if np.percentile(lit, 80) < 0.55:
            errs.append(f"hudnum.{c} body is not bright ivory (p80 {np.percentile(lit, 80):.2f})")
        if px[0, :, 3].max() > 200 or px[-1, :, 3].max() > 200:
            errs.append(f"hudnum.{c} touches the top or bottom of its cell")
    heights = [(np.nonzero((px[..., 3] > 128).any(axis=1))[0].max() - np.nonzero((px[..., 3] > 128).any(axis=1))[0].min()) for c, px in digs.items() if c != "/"]
    if max(heights) - min(heights) > 8:
        errs.append(f"hudnum digits differ in height by {max(heights) - min(heights)} px")


def check_gauges(errs, S):
    for name in ("gauge.left.fill", "gauge.right.fill"):
        lum = S(name)[..., :3].astype(float).sum(axis=2).mean(axis=0)
        if lum.argmax() <= len(lum) / 2:
            errs.append(f"{name}: stripe not right of centre")
    if S("gauge.empty")[..., :3].max() > 60:
        errs.append("gauge.empty is not near black")
    cap = S("gauge.cap.left")
    if cap[32:150, 52:80, 3].max() != 0:
        errs.append("gauge.cap: hole not transparent")
    if not mirrored(cap, S("gauge.cap.right")):
        errs.append("gauge caps do not mirror")


def check_dragon(errs, S, doc):
    left, right = S("hud.dragon.left"), S("hud.dragon.right")
    if iou(left, right[:, ::-1]) < 0.98:
        errs.append("hud.dragon.right does not mirror left")
    r = doc["sprites"]["hud.dragon.left"]
    if r["pivot"] != [0, r["h"]] or doc["sprites"]["hud.dragon.right"]["pivot"] != [r["w"], r["h"]]:
        errs.append("dragon pivots are not the bottom outer corners")
    e = r["meta"]["eye"]
    got = U.probe(left, e)
    if not (got[0] > 190 and got[0] > got[1] + 25 and got[2] < 150):
        errs.append(f"dragon eye {got} is not amber")
    keep = left[..., 3] > 128
    keep[e[1] - 12 : e[1] + e[3] + 12, e[0] - 12 : e[0] + e[2] + 40] = False
    px = left[keep][:, :3].astype(int)
    if ((px.max(axis=1) - px.min(axis=1)) > 70).mean() > 0.03:
        errs.append("dragon remainder is not low saturation")
    if left[-1, :128, 3].min() < 255:
        errs.append("dragon is not rooted on the bottom edge at the outer corner")
    # the head must stand clear of the plate: some opaque pixels in the top third
    if (left[: left.shape[0] // 3, :, 3] > 128).sum() < 600:
        errs.append("dragon has no mass in its top third")


def check_misc(errs, S, doc):
    for name in ("hud.base.left", "hud.base.right", "hud.base.centre", "hud.gapfill", "chat.rock"):
        if S(name)[..., 3].min() != 255:
            errs.append(f"{name}: not opaque")
    sp = doc["sprites"]
    if sp["hud.base.left"]["w"] + sp["hud.base.centre"]["w"] + sp["hud.base.right"]["w"] != 640 * 4:
        errs.append("bar widths do not sum to 640 u")
    mean = S("chat.rock")[..., :3].reshape(-1, 3).mean(axis=0)
    if not near(mean, (0x15, 0x12, 0x0B), 4):
        errs.append(f"chat.rock mean {mean}")
    if not mirrored(S("hud.talon.left"), S("hud.talon.right")):
        errs.append("hud.talon does not mirror")
    if not mirrored(S("hud.base.left"), S("hud.base.right")):
        errs.append("hud.base does not mirror")
    for name, rect, tok in PROBES:
        got = U.probe(S(name), rect)
        want = (U.token(tok) * 255 + 0.5).astype(int)
        if not near(got, want, 6):
            errs.append(f"{name} probe {got}, want {tok} {tuple(int(v) for v in want)}")
    # the EXP pieces: a molten body, a dark well
    f = S("exp.fill")[..., :3].reshape(-1, 3).mean(axis=0)
    if not (f[0] > 200 and f[0] > f[1] > f[2]):
        errs.append(f"exp.fill is not warm gold {f}")
    if luma(S("exp.trough")).mean() > 0.08:
        errs.append("exp.trough is not a dark well")
    # the gap tile must wrap: left and right columns agree
    g = S("hud.gapfill")[..., :3].astype(float)
    if np.abs(g[:, 0] - g[:, -1]).mean() > 14:
        errs.append("hud.gapfill does not tile in x")
    # the arm bands run on into the gap: the rows where bands start must match across the group edge
    left_edge = S("hud.base.left")[:, -4:, :3].astype(float).mean(axis=(1, 2))
    gap_edge = S("hud.gapfill")[:, :4, :3].astype(float).mean(axis=(1, 2))
    if np.abs(left_edge - gap_edge).mean() > 28:
        errs.append("hud.base.left and hud.gapfill do not meet")


def check_states(errs, S, C):
    """Every interactive state must differ visibly from normal, not just exist."""
    groups = ["hud.slab", "qwe.slot.q", "qwe.slot.w", "qwe.slot.e", "iconbtn.frame", "chat.btn", "chat.scroll.up", "chat.scroll.down"]
    for g in groups:
        n = S(f"{g}.normal")[..., :3].astype(float)
        for st, need in (("hover", 3.0), ("pressed", 3.0)):
            d = np.abs(S(f"{g}.{st}")[..., :3].astype(float) - n).mean()
            if d < need:
                errs.append(f"{g}.{st} differs by only {d:.1f}")
    n = S("skill.tile.normal")[..., :3].astype(float)
    for st in ("hover", "disabled"):
        d = np.abs(S(f"skill.tile.{st}")[..., :3].astype(float) - n).mean()
        if d < 3.0:
            errs.append(f"skill.tile.{st} differs by only {d:.1f}")
    a, b = C("cursor.pointer"), C("cursor.pressed")
    changed = (np.abs(a.astype(int) - b.astype(int)).max(axis=2) > 24).mean()
    if changed < 0.05:
        errs.append(f"cursor.pressed differs on only {changed:.1%} of pixels")


def check_cursors(errs, C, cur):
    for name in CURSORS:
        px, py = cur["sprites"][name]["pivot"]
        img = C(name)
        a = img[max(py - 2, 0) : py + 3, max(px - 2, 0) : px + 3, 3]
        if a.size == 0 or a.max() <= 128:
            errs.append(f"{name}: pivot not on the art")
        if px >= 64 or py >= 64:
            errs.append(f"{name}: pivot not upper-left")


def check_layout(errs, doc):
    lay = doc["meta"]["layout"]
    want = {"talon": [128, -3, 71, 52], "dragon": [0, -52, 118, 58], "setting": [92, -5, 62, 56], "gem": [98, -2, 50, 50]}
    for k, v in want.items():
        if lay.get(k) != v:
            errs.append(f"meta.layout.{k} is {lay.get(k)}, want {v}")


def main():
    errs = []
    check_generator(errs)
    for path in (HUD, CUR):
        errs += U.validate_atlas(path, {"require4": True})
    doc = json.loads(HUD.read_text(encoding="utf-8"))
    cur = json.loads(CUR.read_text(encoding="utf-8"))
    S = lambda n: U.sprite_pixels(HUD, n)
    C = lambda n: U.sprite_pixels(CUR, n)
    check_sizes(errs, doc, cur)
    if not any(re.match("missing", e) for e in errs):
        check_layout(errs, doc)
        check_gems(errs, S)
        check_setting_numbers_spill(errs, S)
        check_gauges(errs, S)
        check_dragon(errs, S, doc)
        check_misc(errs, S, doc)
        check_cursors(errs, C, cur)
        check_states(errs, S, C)
    if "--fast" not in sys.argv:
        import gen_hud

        errs += U.determinism_errors(gen_hud.main, ["hud"])
        errs += U.shipped_errors(gen_hud.main, "hud", ROOT)
    for e in errs:
        print("FAIL", e)
    print(f"check_hud: {len(doc['sprites'])} + {len(cur['sprites'])} sprites, {len(errs)} errors")
    return 1 if errs else 0


if __name__ == "__main__":
    sys.exit(main())
