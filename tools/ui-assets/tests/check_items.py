"""Checks dream/ui/items/. Exits nonzero on failure."""
from __future__ import annotations

import ast
import json
import re
import sys
import tempfile
from pathlib import Path

import numpy as np
from PIL import Image

HERE = Path(__file__).resolve().parent
TOOLS = HERE.parent
sys.path.insert(0, str(TOOLS))
import gen_items  # noqa: E402
import uiart  # noqa: E402

REPO = TOOLS.parents[1]
ITEMS = REPO / "dream" / "ui" / "items"
PREVIEWS = TOOLS / "previews"
BANNED = ("ImageFont", "urllib", "requests", "reference/", ".webp", "dream/fonts")
THIRD_PARTY_OK = {"numpy", "PIL", "uiart", "forge"}
STDLIB_OK = {"__future__", "json", "math", "pathlib", "sys"}

EXPECTED = {
    (1, 1): "item.relic.disc item.gear.iron-band item.gem.amber item.gear.ward-pendant item.potion.health item.potion.mana",
    (1, 2): "item.feather.azure item.tool.survey-lantern item.gear.ward-lantern item.tool.torch",
    (2, 2): "item.gear.leather-cap item.gear.leather-vest item.armor.bone item.helm.gilded item.armor.gilded "
            "item.gear.plated-leggings item.gear.iron-gauntlets item.gear.iron-boots item.gear.ember-wisp",
    (2, 3): "item.tool.builder-hammer item.tool.stone-maul item.tool.field-blade item.gear.iron-blade item.axe.twinbit item.shield.kite",
    (2, 4): "item.bow.verdant item.gear.hunting-spear",
    (3, 2): "item.gear.gossamer-wings item.gear.travel-cloak",
}

NEW_KINDS = {
    (1, 1): "gear.bone-amulet gear.iron-torc gear.hollow-locket gear.silver-ring gear.bone-ring gear.blood-ring "
            "consumable.weak-heal consumable.minor-heal consumable.greater-heal consumable.minor-mana consumable.greater-mana",
    (1, 2): "gear.iron-dagger",
    (1, 3): "gear.short-sword",
    (1, 4): "gear.long-sword gear.ash-staff gear.bone-staff",
    (2, 2): "gear.hand-axe gear.wooden-buckler gear.iron-helm gear.greathelm gear.chain-hauberk gear.plate-cuirass gear.leather-gloves "
            "gear.plate-gauntlets gear.leather-boots gear.plate-sabatons gear.leather-leggings gear.chain-chausses gear.plate-greaves",
    (2, 3): "gear.battle-axe gear.flanged-mace gear.war-hammer gear.short-bow gear.kite-shield",
    (2, 4): "gear.greatsword gear.long-bow gear.tower-shield",
    (3, 2): "gear.hooded-cloak gear.grave-mantle",
}
for _fp, _names in NEW_KINDS.items():
    for _n in _names.split():
        EXPECTED[_fp] = EXPECTED.get(_fp, "") + " item." + _n
FOOTPRINT = {n: fp for fp, names in EXPECTED.items() for n in names.split()}


def generator_errors(path: Path) -> list[str]:
    text = path.read_text(encoding="utf-8")
    errs = [f"{path.name}: contains {b}" for b in BANNED if b in text]
    for node in ast.walk(ast.parse(text)):
        mods = []
        if isinstance(node, ast.Import):
            mods = [a.name for a in node.names]
        elif isinstance(node, ast.ImportFrom):
            mods = [node.module or ""]
        for m in mods:
            top = m.split(".")[0]
            if top not in THIRD_PARTY_OK | STDLIB_OK:
                errs.append(f"{path.name}: imports {m}")
    return errs


def check_names(doc: dict) -> list[str]:
    errs = []
    keys = json.loads((REPO / "dream" / "config" / "content" / "items.json").read_text(encoding="utf-8"))
    wanted = set(FOOTPRINT) | {"fx.aura.blob"} | {f"item.{i['key']}" for i in keys["items"]}
    have = set(doc["sprites"])
    errs += [f"missing sprite {n}" for n in sorted(wanted - have)]
    errs += [f"unexpected sprite {n}" for n in sorted(have - wanted)]
    return errs


def art_box(alpha: np.ndarray) -> tuple:
    ys, xs = np.where(alpha > 16)
    return int(xs.min()), int(ys.min()), int(xs.max()) + 1, int(ys.max()) + 1


def check_icons(doc: dict, sheet: np.ndarray) -> list[str]:
    errs = []
    s = uiart.SCALE
    for name, fp in FOOTPRINT.items():
        r = doc["sprites"].get(name)
        if r is None:
            continue
        if r["meta"].get("footprint") != list(fp):
            errs.append(f"{name}: footprint {r['meta'].get('footprint')}")
        w_u, h_u = fp[0] * 20, fp[1] * 20
        if (r["w"], r["h"]) != ((w_u + 12) * s, (h_u + 12) * s):
            errs.append(f"{name}: size {r['w']}x{r['h']}")
        if r["pivot"] != [24, 24]:
            errs.append(f"{name}: pivot {r['pivot']}")
        a = sheet[r["y"] : r["y"] + r["h"], r["x"] : r["x"] + r["w"], 3]
        if a[0].any() or a[-1].any() or a[:, 0].any() or a[:, -1].any():
            errs.append(f"{name}: art clipped by the sprite edge")
        x0, y0, x1, y1 = art_box(a)
        bw, bh = (x1 - x0) / s, (y1 - y0) / s
        if bw > w_u + 6 or bh > h_u + 6:
            errs.append(f"{name}: art {bw}x{bh} u over footprint + 6 u")
        long_axis = max(w_u, h_u)
        got = bw if w_u >= h_u else bh
        if got < 0.45 * long_axis:
            errs.append(f"{name}: art {got} u under 45% of {long_axis} u")
    return errs


def check_variants(doc: dict, sheet: np.ndarray) -> list[str]:
    errs = []
    variants = doc["meta"].get("variants", [])
    names = {v["name"]: v for v in variants}
    if set(names) != {"uncommon", "rare", "mask"}:
        return [f"variants {sorted(names)}"]
    if names["mask"]["kind"] != "mask":
        errs.append("mask variant kind")
    icons = sorted(FOOTPRINT)
    for v in variants:
        other = np.asarray(Image.open(ITEMS / v["image"]).convert("RGBA"))
        if other.shape != sheet.shape:
            errs.append(f"{v['image']}: size {other.shape}")
            continue
        with tempfile.TemporaryDirectory() as td:
            tmp = Path(td)
            alias = dict(doc, image=v["image"], kind=v["kind"])
            Image.open(ITEMS / v["image"]).save(tmp / v["image"])
            (tmp / "variant.json").write_text(json.dumps(alias), encoding="utf-8")
            errs += [f"{v['image']}: {e}" for e in uiart.validate_atlas(tmp / "variant.json", {"require4": True})]
        for name in icons:
            r = doc["sprites"][name]
            sa = sheet[r["y"] : r["y"] + r["h"], r["x"] : r["x"] + r["w"]]
            so = other[r["y"] : r["y"] + r["h"], r["x"] : r["x"] + r["w"]]
            if v["kind"] == "color":
                if (sa[..., 3] != so[..., 3]).any():
                    errs.append(f"{v['image']}:{name}: alpha differs")
                if not (sa[..., :3] != so[..., :3]).any():
                    errs.append(f"{v['image']}:{name}: same as Common")
            else:
                art = sa[..., 3] > 200
                if so[..., 2][art].max() < 64:
                    errs.append(f"{name}: empty rim channel")
                if (so[..., :3][sa[..., 3] == 0] != 0).any():
                    errs.append(f"{name}: mask outside art")
    return errs


def check_tier_look(doc: dict, sheet: np.ndarray) -> list[str]:
    """Tiers must be told apart at a glance."""
    errs = []

    def px(image, name):
        r = doc["sprites"][name]
        s = np.asarray(Image.open(ITEMS / image).convert("RGBA"))[r["y"] : r["y"] + r["h"], r["x"] : r["x"] + r["w"]]
        return s[s[..., 3] > 200][:, :3].astype(float)

    for name in sorted(FOOTPRINT):
        base, unc = px("items.png", name), px("items_uncommon.png", name)
        if np.abs(unc - base).mean() < 10:
            errs.append(f"{name}: Uncommon too close to Common")
        if (unc - base).mean(axis=0)[1] > (unc - base).mean(axis=0)[0]:
            errs.append(f"{name}: Uncommon not shifted towards rose")
    return errs


def check_mask_semantics(doc: dict) -> list[str]:
    errs = []
    mask = np.asarray(Image.open(ITEMS / "items_mask.png").convert("RGBA"))
    for name, metal_high in (("item.axe.twinbit", True), ("item.gear.leather-vest", False)):
        r = doc["sprites"][name]
        m = mask[r["y"] : r["y"] + r["h"], r["x"] : r["x"] + r["w"]]
        art = (m[..., 0] > 0) | (m[..., 1] > 0)
        share = (m[..., 0][art] > 128).mean()
        if metal_high != (share > 0.3):
            errs.append(f"{name}: metal share {share:.2f}")
    for name in ("item.tool.torch", "item.gear.ember-wisp", "item.gear.ward-lantern"):
        r = doc["sprites"][name]
        if mask[r["y"] : r["y"] + r["h"], r["x"] : r["x"] + r["w"], 3].max() < 128:
            errs.append(f"{name}: no emissive")
    return errs


def check_aura(doc: dict) -> list[str]:
    errs = []
    r = doc["sprites"].get("fx.aura.blob")
    if r is None:
        return errs
    if (r["w"], r["h"]) != (120, 120):
        errs.append(f"aura size {r['w']}x{r['h']}")
    img = uiart.sprite_pixels(ITEMS / "items.json", "fx.aura.blob")
    a = img[..., 3].astype(int)
    if a[60, 60] < 240 or a[0, 0] != 0 or a[0, 60] > 3:
        errs.append("aura is not a soft radial")
    if (np.abs(np.diff(a[60])) > 8).any():
        errs.append("aura has a hard step")
    return errs


def check_ramps() -> list[str]:
    errs = uiart.validate_atlas(ITEMS / "tier_ramps.json")
    doc = json.loads((ITEMS / "tier_ramps.json").read_text(encoding="utf-8"))
    if doc["size"] != [256, 48]:
        errs.append(f"ramp size {doc['size']}")
    sheet = np.asarray(Image.open(ITEMS / "tier_ramps.png").convert("RGBA"))
    order = ["ramp.rare", "ramp.enhanced", "ramp.chrome"]
    for i, name in enumerate(order):
        r = doc["sprites"].get(name)
        if not r or (r["y"], r["w"], r["h"]) != (i * 16, 256, 16):
            errs.append(f"{name}: rect {r}")
    keys = {"#DA8939": 0, "#C68F79": 64, "#8A6A7A": 128}
    for hex_col, x in keys.items():
        want = (uiart.token(hex_col) * 255).round()
        got = np.array(uiart.probe(sheet, (x, 0, 1, 16)))
        if np.abs(got - want).max() > 6:
            errs.append(f"rare ramp {hex_col} at {x}: {got}")
    want = (uiart.token("gold.material") * 255).round()
    if np.abs(np.array(uiart.probe(sheet, (0, 16, 1, 16))) - want).max() > 6:
        errs.append("enhanced ramp start")
    if (sheet[..., 3] != 255).any():
        errs.append("ramps not opaque")
    return errs


# The engine multiplies UI albedo up to about 2.17x with no tonemap, so an authored channel above ~0.40 blooms into candy.
# Measured on the shipped icons (non-emissive pixels, alpha > 0.8); the ceilings carry a little room over the worst icon.
# Before the aging pass the same icons peaked at 1.00 with p99 0.95 and sat at saturation 0.9, which is the candy this gate stops.
GOLD_GAP = 12    # gilded helm median (82, 70, 54): g - b = 16, was > 40 when the gold was pure toy gold
BLUE_GAP = 8     # azure feather (74, 80, 89): b - r = 15; travel cloak (29, 32, 40): b - r = 11; was 25
RED_GAP = 8      # hunting spear (41, 27, 25): r - g = 14; was 15
BONE_MIN = 70    # bone cuirass median (100, 98, 94): min channel 94; was 120, which no 0.40-ceiling icon can reach
CEIL_P99 = 0.46          # per icon, p99 of max(R,G,B) over lit (non-emissive) pixels; measured worst 0.435 (consumable.greater-mana)
CEIL_P999 = 0.48         # per icon, p99.9 of the same; measured worst 0.447
CEIL_EMIT_P99 = 0.54     # emissive pixels (flames, glass, wisp) may sit higher; measured worst 0.502 (item.tool.survey-lantern)
SAT_P90 = 0.62           # per icon, p90 of (max-min)/max over pixels whose max channel is above 0.15; measured worst 0.526 (potion.mana)
SAT_CANDY_SHARE = 0.01   # share of pixels that are both saturated (>0.80) and bright (max > 0.30); measured worst 0.000


def lit_pixels(doc: dict, sheet: np.ndarray, mask: np.ndarray, name: str, emissive: bool) -> np.ndarray:
    r = doc["sprites"][name]
    s = sheet[r["y"] : r["y"] + r["h"], r["x"] : r["x"] + r["w"]]
    m = mask[r["y"] : r["y"] + r["h"], r["x"] : r["x"] + r["w"], 3]
    sel = (s[..., 3] > 204) & ((m > 64) if emissive else (m <= 64))
    return s[sel][:, :3].astype(float) / 255


def brightness_stats(doc: dict, sheet: np.ndarray, mask: np.ndarray) -> dict:
    """name -> (p99 of the brightest channel, p99.9, emissive p99, p90 saturation, candy share)."""
    out = {}
    for name in FOOTPRINT:
        if name not in doc["sprites"]:
            continue
        p = lit_pixels(doc, sheet, mask, name, False)
        e = lit_pixels(doc, sheet, mask, name, True)
        if len(p) == 0:
            continue
        mx, mn = p.max(1), p.min(1)
        vis = mx > 0.15
        sat = (mx - mn)[vis] / mx[vis]
        candy = float(((mx - mn) / np.maximum(mx, 1e-6) > 0.80)[mx > 0.30].sum()) / len(p)
        out[name] = (float(np.percentile(mx, 99)), float(np.percentile(mx, 99.9)),
                     float(np.percentile(e.max(1), 99)) if len(e) > 20 else 0.0,
                     float(np.percentile(sat, 90)) if len(sat) else 0.0, candy)
    return out


def check_brightness(doc: dict, sheet: np.ndarray) -> list[str]:
    """No icon may drift back to bright, saturated toy colour."""
    mask = np.asarray(Image.open(ITEMS / "items_mask.png").convert("RGBA"))
    errs = []
    for name, (p99, p999, ep99, sat, candy) in brightness_stats(doc, sheet, mask).items():
        if p99 > CEIL_P99:
            errs.append(f"{name}: p99 channel {p99:.3f} over the {CEIL_P99} ceiling")
        if p999 > CEIL_P999:
            errs.append(f"{name}: p99.9 channel {p999:.3f} over {CEIL_P999}")
        if ep99 > CEIL_EMIT_P99:
            errs.append(f"{name}: emissive p99 {ep99:.3f} over {CEIL_EMIT_P99}")
        if sat > SAT_P90:
            errs.append(f"{name}: p90 saturation {sat:.2f} over {SAT_P90}")
        if candy > SAT_CANDY_SHARE:
            errs.append(f"{name}: {candy:.3f} of pixels are bright and saturated")
    return errs


def check_palette(doc: dict, sheet: np.ndarray) -> list[str]:
    """Hue signals from spec 02 s7 that a recolour could silently lose. The absolute levels were re-tuned for the
    aged, <= 0.40 icons; the hue directions are the ones the spec asks for."""
    errs = []

    def typical_rgb(name):
        r = doc["sprites"][name]
        s = sheet[r["y"] : r["y"] + r["h"], r["x"] : r["x"] + r["w"]]
        return np.median(s[s[..., 3] > 200][:, :3], axis=0)

    for name, test, label in (
        ("item.helm.gilded", lambda c: c[0] > c[1] > c[2] + GOLD_GAP, "gold"),
        ("item.feather.azure", lambda c: c[2] > c[0] + BLUE_GAP, "blue"),
        ("item.gear.travel-cloak", lambda c: c[2] > c[0] + BLUE_GAP, "navy"),
        ("item.gear.hunting-spear", lambda c: c[0] > c[1] + RED_GAP, "red"),
    ):
        c = typical_rgb(name)
        if not test(c):
            errs.append(f"{name}: not {label}: {c.round()}")
    # bone stays the palest of the set: the old absolute floor (min channel > 120) cannot hold under a 0.40 ceiling,
    # so it is now relative to the other hue anchors, plus a floor on its own
    bone = typical_rgb("item.armor.bone")
    if bone.min() <= BONE_MIN:
        errs.append(f"item.armor.bone: not pale: {bone.round()}")
    for other in ("item.helm.gilded", "item.feather.azure", "item.gear.travel-cloak", "item.gear.hunting-spear"):
        if typical_rgb(other).mean() >= bone.mean():
            errs.append(f"item.armor.bone: not paler than {other}")
    return errs


def main() -> int:
    js = ITEMS / "items.json"
    errs = uiart.validate_atlas(js, {"require4": True})
    doc = json.loads(js.read_text(encoding="utf-8"))
    sheet = np.asarray(Image.open(ITEMS / "items.png").convert("RGBA"))
    errs += check_names(doc)
    errs += check_icons(doc, sheet)
    errs += check_variants(doc, sheet)
    errs += check_tier_look(doc, sheet)
    errs += check_mask_semantics(doc)
    errs += check_aura(doc)
    errs += check_ramps()
    errs += check_palette(doc, sheet)
    errs += check_brightness(doc, sheet)
    errs += generator_errors(TOOLS / "gen_items.py")
    for f in ITEMS.iterdir():
        if re.search(uiart.FORBIDDEN_NAMES.pattern, f.name, re.I):
            errs.append(f"forbidden file name {f.name}")
    for p in ("items_icons.png", "items_tiers.png"):
        if not (PREVIEWS / p).exists():
            errs.append(f"missing preview {p}")
    errs += uiart.determinism_errors(gen_items.main, ["items"])
    errs += uiart.shipped_errors(gen_items.main, "items", ITEMS)
    for e in errs:
        print("FAIL", e)
    print(f"check_items: {len(doc['sprites'])} sprites, {len(errs)} errors")
    return 1 if errs else 0


if __name__ == "__main__":
    sys.exit(main())
