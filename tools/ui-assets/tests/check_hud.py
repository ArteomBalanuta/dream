"""Checks for dream/ui/hud. Exits nonzero on any failure. Usage: python check_hud.py [--fast]

--fast skips the determinism double run. Sizes and rules come from docs 07, Manifest and Checks.
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
    "hud.dragon.left": (107, 46), "hud.dragon.right": (107, 46),
    "hud.talon.left": (59, 46), "hud.talon.right": (59, 46),
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

# sprite, rect px (x, y, w, h), token
PROBES = [
    ("hud.slab.normal", (40, 30, 144, 26), "hud.stone.face"),
    ("hud.slab.normal", (2, 2, 6, 6), "hud.slab"),
    ("gauge.left.fill", (20, 60, 16, 20), "gauge.left.face"),
    ("gauge.left.fill", (39, 60, 4, 20), "gauge.left.edge"),
    ("gauge.right.fill", (20, 60, 16, 20), "gauge.right.face"),
    ("gauge.right.fill", (39, 60, 4, 20), "gauge.right.edge"),
    ("skill.tile.normal", (20, 30, 36, 50), "hud.stone.tile"),
    ("chat.field.main", (8, 8, 16, 16), "chat.input"),
    ("chat.field.whisper", (8, 8, 16, 16), "chat.whisper"),
    ("chat.postbar", (8, 12, 16, 12), "chat.post.bg"),
    ("exp.fill", (8, 6, 16, 8), "#F0C870"),
    ("exp.trough", (40, 4, 100, 16), "#070300"),
]


def near(a, b, tol):
    return all(abs(int(x) - int(y)) <= tol for x, y in zip(a, b))


def has_colour(px, hexc, tol):
    t = (U.token(hexc) * 255 + 0.5).astype(int)
    solid = px[px[..., 3] > 128][:, :3].astype(int)
    return bool((np.abs(solid - t) <= tol).all(axis=1).any())


def iou(a, b):
    a, b = a[..., 3] > 128, b[..., 3] > 128
    return (a & b).sum() / max((a | b).sum(), 1)


def check_generator(errs):
    src = GEN.read_text(encoding="utf-8")
    for word in ("ImageFont", "urllib", "requests", "reference/", ".webp", "dream/fonts"):
        if word in src:
            errs.append(f"generator contains {word}")
    allowed = {"uiart", "numpy", "PIL", "math", "sys", "pathlib", "__future__"}
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


def check_gems(errs, S):
    for kind, cols in (("hp", ("#EC260C", "#7C1001", "#EEC6AD")), ("mp", ("#1385E7", "#0109A5", "#6FC2E3"))):
        full, empty, mask, spec = (S(f"gem.{kind}.{x}") for x in ("full", "empty", "mask", "spec"))
        for c in cols:
            if not has_colour(full, c, 10):
                errs.append(f"gem.{kind}.full lacks {c}")
        if not (np.array_equal(full[..., 3], empty[..., 3]) and np.array_equal(full[..., 3], mask[..., 3])):
            errs.append(f"gem.{kind}: alpha differs across layers")
        ins = mask[..., 3] > 0
        rows = np.nonzero(ins.any(axis=1))[0]
        vals = []
        for y in rows:
            v = mask[y][ins[y]][:, :3]
            if v.min() != v.max():
                errs.append(f"gem.{kind}.mask row {y} not uniform")
                break
            vals.append(int(v[0, 0]))
        if vals != sorted(vals) or vals[0] != 0 or vals[-1] != 255:
            errs.append(f"gem.{kind}.mask not monotonic 0 at top to 255 at bottom")
        if not (mask[..., 0] == mask[..., 1]).all() or not (mask[..., 1] == mask[..., 2]).all():
            errs.append(f"gem.{kind}.mask not grey")
        lf, le = full[..., :3][ins].mean(), empty[..., :3][ins].mean()
        if not le < lf * 0.5:
            errs.append(f"gem.{kind}.empty not dimmed")
    if iou(S("gem.mp.full"), S("gem.hp.full")[:, ::-1]) < 0.98:
        errs.append("gem.mp silhouette is not gem.hp mirrored")


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
    if not near(got, (0xBD, 0xA8, 0x84), 12):
        errs.append(f"dragon eye {got}")
    f = r["meta"]["fang"]
    keep = left[..., 3] > 128
    keep[92:128, 212:280] = False  # eye area
    keep[f[1] : f[1] + f[3], f[0] : f[0] + f[2]] = False
    px = left[keep][:, :3].astype(int)
    if ((px.max(axis=1) - px.min(axis=1)) > 40).mean() > 0.005:
        errs.append("dragon remainder is not low saturation")
    if left[-1, :160, 3].min() < 255:
        errs.append("dragon is not rooted on the bottom edge at the outer corner")


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
    for name, rect, tok in PROBES:
        got = U.probe(S(name), rect)
        want = (U.token(tok) * 255 + 0.5).astype(int)
        if not near(got, want, 6):
            errs.append(f"{name} probe {got}, want {tok} {tuple(int(v) for v in want)}")


def check_cursors(errs, C, cur):
    for name in CURSORS:
        px, py = cur["sprites"][name]["pivot"]
        img = C(name)
        a = img[max(py - 2, 0) : py + 3, max(px - 2, 0) : px + 3, 3]
        if a.size == 0 or a.max() <= 128:
            errs.append(f"{name}: pivot not on the art")
        if px >= 64 or py >= 64:
            errs.append(f"{name}: pivot not upper-left")


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
        check_gems(errs, S)
        check_gauges(errs, S)
        check_dragon(errs, S, doc)
        check_misc(errs, S, doc)
        check_cursors(errs, C, cur)
    if "--fast" not in sys.argv:
        import gen_hud

        errs += U.determinism_errors(gen_hud.main, ["hud"])
    for e in errs:
        print("FAIL", e)
    print(f"check_hud: {len(doc['sprites'])} + {len(cur['sprites'])} sprites, {len(errs)} errors")
    return 1 if errs else 0


if __name__ == "__main__":
    sys.exit(main())
