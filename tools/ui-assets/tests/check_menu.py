"""Checks for dream/ui/menu: the front-end atlas (title, backdrop, buttons, panel, slider, divider).

Exits nonzero on failure. `--determinism` also regenerates the page twice (about three minutes).
"""
from __future__ import annotations

import json
import re
import sys
from pathlib import Path

import numpy as np
from PIL import Image

HERE = Path(__file__).resolve().parent
TOOLS = HERE.parent
sys.path.insert(0, str(TOOLS))
sys.path.insert(0, str(HERE))

import check_loader as cl  # noqa: E402
import uiart as ua  # noqa: E402

ROOT = TOOLS.parents[1] / "dream" / "ui"
JSON = ROOT / "menu" / "menu.json"
PNG = ROOT / "menu" / "menu.png"
S = ua.SCALE
errors: list[str] = []

# name -> (w_u, h_u)
EXPECTED = {
    "menu.backdrop": (256, 144),
    "menu.title": (None, 56),
    "menu.button.normal": (128, 20),
    "menu.button.hover": (128, 20),
    "menu.button.pressed": (128, 20),
    "menu.button.disabled": (128, 20),
    "menu.panel": (64, 64),
    "menu.slider.track": (64, 6),
    "menu.slider.knob": (8, 14),
    "menu.divider": (120, 6),
}
SLICES = {
    "menu.button.normal": [24, 24, 24, 24],
    "menu.button.hover": [24, 24, 24, 24],
    "menu.button.pressed": [24, 24, 24, 24],
    "menu.button.disabled": [24, 24, 24, 24],
    "menu.panel": [56, 56, 56, 56],
    "menu.slider.track": [48, 8, 48, 8],
}
PNG_BUDGET_BYTES = 2_500_000


def fail(msg: str) -> None:
    errors.append(msg)


def px(name: str) -> np.ndarray:
    return ua.sprite_pixels(JSON, name)


def luma(a: np.ndarray) -> np.ndarray:
    a = a[..., :3].astype(np.float32) / 255
    return 0.2126 * a[..., 0] + 0.7152 * a[..., 1] + 0.0722 * a[..., 2]


def region(img: np.ndarray, x0: float, y0: float, x1: float, y1: float) -> np.ndarray:
    return img[int(y0 * S) : int(y1 * S), int(x0 * S) : int(x1 * S)]


def check_sprites(doc: dict) -> None:
    if set(doc["sprites"]) != set(EXPECTED):
        fail(f"sprite set differs: missing {sorted(set(EXPECTED) - set(doc['sprites']))}, extra {sorted(set(doc['sprites']) - set(EXPECTED))}")
    for name, (w, h) in EXPECTED.items():
        r = doc["sprites"].get(name)
        if not r:
            continue
        if (w is not None and r["w"] != w * S) or r["h"] != h * S:
            fail(f"{name}: size {r['w']}x{r['h']} px, want {'?' if w is None else w * S}x{h * S}")
    if doc["extrude"] != 4:
        fail("extrude must be 4")
    for name, want in SLICES.items():
        r = doc["sprites"].get(name)
        if r and r["slice"] != want:
            fail(f"{name}: slice {r['slice']}, want {want}")
    title = doc["sprites"].get("menu.title")
    if title and not 800 <= title["w"] <= 1000:
        fail(f"menu.title is {title['w']} px wide; the word should fill about 230 u")
    for e in ua.validate_atlas(JSON, {"require4": True}):
        fail(e)
    if PNG.stat().st_size > PNG_BUDGET_BYTES:
        fail(f"menu.png is {PNG.stat().st_size / 1e6:.2f} MB, over the {PNG_BUDGET_BYTES / 1e6:.1f} MB budget")


def check_loader(doc: dict) -> None:
    """The shared loader simulation: no neighbour bleed under bilinear sampling, 9-slices without gaps."""
    sheet = np.asarray(Image.open(PNG).convert("RGBA"))
    before = len(cl.errs)
    cl.check_sampling(doc, sheet, JSON)
    cl.check_slices(doc, JSON)
    for e in cl.errs[before:]:
        fail(e)


def check_mood() -> None:
    """Dark and grim: nothing bright, the ember only on hover, the title cut from dull metal."""
    bd = px("menu.backdrop")
    if bd[..., 3].min() != 255:
        fail("menu.backdrop must be opaque")
    mean = float(luma(bd).mean())
    if not 0.02 <= mean <= 0.16:
        fail(f"menu.backdrop mean luma {mean:.3f} outside .02..0.16")
    # the vignette: corners darker than the middle band
    h, w = bd.shape[:2]
    corner = float(luma(np.concatenate([bd[: h // 6, : w // 6].reshape(-1, 4), bd[-h // 6 :, -w // 6 :].reshape(-1, 4)])).mean())
    centre = float(luma(bd[h // 3 : 2 * h // 3, w // 3 : 2 * w // 3]).mean())
    if corner > centre * 0.8:
        fail(f"menu.backdrop corners {corner:.3f} are not darker than the centre {centre:.3f}")
    # the text sits over the lower middle: that area must stay dark enough for bone-white text
    behind = float(luma(bd[int(h * 0.55) :, w // 4 : 3 * w // 4]).mean())
    if behind > 0.20:
        fail(f"menu.backdrop under the buttons is {behind:.3f}; the status line needs it below .20")

    normal, hover, pressed, disabled = (px(f"menu.button.{s}") for s in ("normal", "hover", "pressed", "disabled"))
    face = lambda im: im[int(5 * S) : int(15 * S), int(14 * S) : int(114 * S)]  # noqa: E731
    ln, lh, lp, ld = (float(luma(face(i)).mean()) for i in (normal, hover, pressed, disabled))
    if ln > 0.09:
        fail(f"menu.button.normal face luma {ln:.3f} above .09: the plate is a dark slot")
    if not (lh > ln * 1.5):
        fail(f"menu.button.hover face {lh:.3f} must be clearly brighter than normal {ln:.3f}")
    rn, gn, bn = (float(face(normal)[..., c].mean()) for c in range(3))
    rh, gh, bh = (float(face(hover)[..., c].mean()) for c in range(3))
    if not (rh - bh > rn - bn + 20):
        fail("menu.button.hover must glow warm (red over blue)")
    if not (ld < ln):
        fail(f"menu.button.disabled {ld:.3f} must be darker than normal {ln:.3f}")
    if not (lp <= ln * 1.05):
        fail(f"menu.button.pressed {lp:.3f} must not be brighter than normal {ln:.3f}")
    for name, im in (("normal", normal), ("hover", hover), ("pressed", pressed), ("disabled", disabled)):
        if im[0, 0, 3] != 0 or im[0, -1, 3] != 0 or im[-1, 0, 3] != 0:
            fail(f"menu.button.{name}: corners must be cut away (chamfer)")

    panel = px("menu.panel")
    interior = float(luma(region(panel, 14, 14, 50, 50)).mean())
    if interior > 0.09:
        fail(f"menu.panel interior luma {interior:.3f} above .09: text sits on it")
    if not (luma(region(panel, 0, 14, 2, 50)).mean() > interior):
        fail("menu.panel rim must be lighter than its floor")

    title = px("menu.title")
    a = title[..., 3]
    if a[0, 0] != 0 or a[-1, -1] != 0:
        fail("menu.title corners must be transparent")
    cover = float((a > 128).mean())
    if not 0.25 <= cover <= 0.65:
        fail(f"menu.title covers {cover:.2f} of its box, want .25..0.65 (five heavy letters)")
    solid = title[a > 200][:, :3].astype(np.float32) / 255
    if float(solid.max(axis=1).mean()) > 0.62:
        fail("menu.title is too bright: it should read as worn iron and dull brass")
    if float(solid[:, 0].mean()) < float(solid[:, 2].mean()):
        fail("menu.title must lean warm (brass inlay)")
    # five letters: five separate runs along the vertical midline of the cap band
    row = a[int(a.shape[0] * 0.47)] > 128
    runs = int(((row[1:] & ~row[:-1]).sum()) + (1 if row[0] else 0))
    if runs < 5:
        fail(f"menu.title mid-height row has {runs} runs; DREAM has at least 5 stems/strokes")

    knob = px("menu.slider.knob")
    if knob[..., 3].max() != 255:
        fail("menu.slider.knob must have a solid body")
    div = px("menu.divider")
    if div[..., 3][:, : int(1.5 * S)].max() > 40 or div[..., 3][:, -int(1.5 * S) :].max() > 40:
        fail("menu.divider must fade to nothing at both ends")


def check_source() -> None:
    src = (TOOLS / "gen_menu.py").read_text(encoding="utf-8")
    for bad in ("ImageFont", "urllib", "requests", "reference/", ".webp", "dream/fonts", "truetype"):
        if bad in src:
            fail(f"generator contains {bad}")
    allowed = ("__future__", "math", "sys", "pathlib", "numpy", "PIL", "uiart", "forge", "gen_window")
    for line in re.findall(r"^\s*(?:import|from)\s+([\w.]+)", src, flags=re.M):
        if line.split(".")[0] not in allowed:
            fail(f"generator imports {line}")
    if re.search(r"zen|chaos|larkan|loch|mu_?online|arcana|soul|bless", src, flags=re.I):
        fail("generator names a forbidden word")


def check_determinism() -> None:
    import gen_menu

    for e in ua.determinism_errors(gen_menu.main, ["menu"]):
        fail(e)


def main() -> int:
    if not JSON.exists() or not PNG.exists():
        print("FAIL dream/ui/menu/menu.json or menu.png missing; run tools/ui-assets/gen_menu.py")
        return 1
    doc = json.loads(JSON.read_text(encoding="utf-8"))
    check_sprites(doc)
    check_loader(doc)
    check_mood()
    check_source()
    if "--determinism" in sys.argv:
        check_determinism()
    for e in errors:
        print("FAIL", e)
    print(f"menu: {len(errors)} problems; menu.png {PNG.stat().st_size / 1e6:.2f} MB, {len(doc['sprites'])} sprites")
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
