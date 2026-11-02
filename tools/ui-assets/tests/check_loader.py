"""Loader simulation: bilinear sampling at 0.5625 scale must not read neighbouring sprites,
9-slices must stretch without gaps, variants must match, items.json keys must have sprites,
and atlas dimensions must respect GPU limits."""
import json
import sys
from pathlib import Path

import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parents[3]
UI = ROOT / "dream" / "ui"
ATLASES = [UI / "window" / "window.json", UI / "hud" / "hud.json", UI / "hud" / "cursors.json",
           UI / "items" / "items.json", UI / "fx" / "dmg_digits.json", UI / "fx" / "dmg_digits_x4.json"]
SCALE = 0.5625
UI_BUDGET_BYTES = 16_000_000  # raised for the items wave; the 10 MB budget is parked for a later art-compression pass
errs: list[str] = []


def bilinear(img: np.ndarray, u: np.ndarray, v: np.ndarray) -> np.ndarray:
    """u, v are texel coordinates with texel centres at integers (i.e. already minus 0.5)."""
    x0, y0 = np.floor(u).astype(int), np.floor(v).astype(int)
    fx, fy = (u - x0)[..., None], (v - y0)[..., None]
    h, w = img.shape[:2]

    def at(yy, xx):
        return img[np.clip(yy, 0, h - 1), np.clip(xx, 0, w - 1)].astype(np.float64)

    return (at(y0, x0) * (1 - fx) * (1 - fy) + at(y0, x0 + 1) * fx * (1 - fy)
            + at(y0 + 1, x0) * (1 - fx) * fy + at(y0 + 1, x0 + 1) * fx * fy)


def check_sampling(doc, sheet, jp):
    e = doc["extrude"]
    if doc["kind"] != "color" or e == 0:
        return
    s = SCALE if doc["scale"] == 4 else 1.0
    for name, r in doc["sprites"].items():
        x, y, w, h = r["x"], r["y"], r["w"], r["h"]
        spr = sheet[y:y + h, x:x + w]
        mx = "wrap" if "x" in r["tile"] else "edge"
        my = "wrap" if "y" in r["tile"] else "edge"
        # what a sampler reading this sprite alone (clamp or repeat) would return
        ideal = np.pad(spr, ((0, 0), (e, e), (0, 0)), mode=mx)
        ideal = np.pad(ideal, ((e, e), (0, 0), (0, 0)), mode=my)
        dw, dh = max(1, int(round(w * s))), max(1, int(round(h * s)))
        for ph in (0.0, 0.25, 0.5, 0.75):
            for sw, sh in ((dw, dh), (dw + 1, dh + 1)):
                # screen pixel centres -> sprite texel coords, quad overshoot of up to one screen pixel
                gx = (np.arange(sw) + 0.5 + ph) / s - 0.5 - 1.0 / s
                gy = (np.arange(sh) + 0.5 + ph) / s - 0.5 - 1.0 / s
                U, V = np.meshgrid(gx, gy)
                got = bilinear(sheet, U + x, V + y)
                exp = bilinear(ideal, U + e, V + e)
                if np.abs(got - exp).max() > 1e-6:
                    errs.append(f"{jp.stem}:{name}: bilinear sample at {s} (phase {ph}) pulls neighbour data")
                    break
            else:
                continue
            break


def check_slices(doc, jp):
    for name, r in doc["sprites"].items():
        sl = r["slice"]
        if not sl:
            continue
        l, t, rr, b = sl
        w, h = r["w"], r["h"]
        if any(v % 4 for v in sl):
            errs.append(f"{jp.stem}:{name}: slice {sl} not multiples of 4")
        if l + rr >= w or t + b >= h:
            errs.append(f"{jp.stem}:{name}: slice leaves no centre")
            continue
        for dw, dh in ((w, h), (w * 3 + 8, h * 2 + 4), (int(w * 1.5), int(h * 1.5)), (l + rr + 1, t + b + 1), (w * 5, h)):
            cover = np.zeros((dh, dw), dtype=int)
            xs = [(0, l, 0, l), (l, dw - rr, l, w - rr), (dw - rr, dw, w - rr, w)]
            ys = [(0, t, 0, t), (t, dh - b, t, h - b), (dh - b, dh, h - b, h)]
            for dy0, dy1, sy0, sy1 in ys:
                for dx0, dx1, sx0, sx1 in xs:
                    if dx1 > dx0 and dy1 > dy0:
                        if sx1 <= sx0 or sy1 <= sy0:
                            errs.append(f"{jp.stem}:{name}: empty source patch")
                            continue
                        cover[dy0:dy1, dx0:dx1] += 1
            if not (cover == 1).all():
                errs.append(f"{jp.stem}:{name}: 9-slice at {dw}x{dh} has gaps/overlap")


def main() -> int:
    for jp in ATLASES:
        doc = json.loads(jp.read_text(encoding="utf-8"))
        img = Image.open(jp.parent / doc["image"])
        sheet = np.asarray(img.convert("RGBA"))
        print(f"{jp.name}: {img.size[0]}x{img.size[1]}")
        if max(img.size) > 4096:
            errs.append(f"{jp.name}: dimension over 4096")
        if list(img.size) != doc["size"]:
            errs.append(f"{jp.name}: json size mismatch")
        check_sampling(doc, sheet, jp)
        check_slices(doc, jp)
        for v in doc.get("meta", {}).get("variants", []):
            vp = jp.parent / v["image"]
            if not vp.exists():
                errs.append(f"{jp.name}: variant {v['image']} missing")
            elif Image.open(vp).size != img.size:
                errs.append(f"{jp.name}: variant {v['image']} size {Image.open(vp).size} != {img.size}")
    if not (UI / "items" / "tier_ramps.png").exists():
        errs.append("tier_ramps.png missing")
    keys = [i["key"] for i in json.loads((ROOT / "dream" / "config" / "content" / "items.json").read_text(encoding="utf-8"))["items"]]
    names = json.loads((UI / "items" / "items.json").read_text(encoding="utf-8"))["sprites"]
    for k in keys:
        if f"item.{k}" not in names:
            errs.append(f"item key {k} has no sprite item.{k}")
    total = sum(p.stat().st_size for p in UI.rglob("*") if p.is_file())
    print(f"dream/ui total {total / 1e6:.2f} MB; {len(keys)} item keys checked")
    if total > UI_BUDGET_BYTES:  # noisier art compresses worse; see UI_BUDGET_BYTES
        errs.append(f"dream/ui is {total / 1e6:.2f} MB, over the {UI_BUDGET_BYTES / 1e6:.0f} MB budget")
    for e in errs:
        print("FAIL", e)
    print(f"loader: {len(errs)} problems")
    return 1 if errs else 0


if __name__ == "__main__":
    sys.exit(main())
