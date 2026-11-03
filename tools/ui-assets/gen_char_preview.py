"""Review render for the grim actor: a lit turntable sheet, for judging the look by eye.

    python tools/ui-assets/gen_char_preview.py [out_dir]

Writes <out_dir>/previews/char_player.png (default: tools/ui-assets/previews/).

This renders the SAME geometry gen_char_player.py exports, through isoforge's chain with a darker rig, so the
sheet shows the form and the material reads. It is a review aid, not the shipped asset - the shipped asset is
the .glb, which the engine lights itself.

Layout: row 1 is the four-view turntable at review size; row 2 is the same four views at gameplay size (about
a third of the height), which is where the silhouette has to read; the last panel is the baked albedo atlas
before the engine lift.
"""
from __future__ import annotations

import sys
from pathlib import Path

import numpy as np
from PIL import Image

HERE = Path(__file__).resolve().parent
if str(HERE) not in sys.path:
    sys.path.insert(0, str(HERE))

import forge  # noqa: E402
import isoforge as I  # noqa: E402
import uiart as U  # noqa: E402
import gen_char_player as P  # noqa: E402

M_TO_U = 26.0   # isoforge works in u; this frames a 1.9 m figure at about 150 px tall
BG = (200, 200, 200)
PAD = 18
GAP = 22
SMALL = 0.36    # gameplay-size factor for row 2

# A darker sibling of ISO_RIG: ~0.6x the key, a desaturated-but-still-cold fill, dimmer panel.
# Directions match ISO_RIG so shadows stay consistent. Spec: 01-art-direction.md.
GRIM_RIG = forge.Rig(
    lights=(
        forge.Light(I.KEY_DIR, (1.00, 0.72, 0.46), 11.0, shadow=True),
        forge.Light((0.75, 0.40, 0.52), (0.22, 0.46, 1.00), 4.2),
        forge.Light((-0.15, 0.80, 0.30), (0.30, 0.52, 1.00), 0.9),
    ),
    panels=(forge.Panel(-0.18, -0.62, 0.60, 0.26, 0.09, (0.62, 0.68, 0.76)),),
    ambient=(0.0035, 0.0095, 0.0340),
)


def render_view(yaw_deg: float, seed: str) -> I.Rendered:
    """Parts come from the generator in bind-pose world u; the materials are the bake's own (charforge.GRIM_MATS
    plus the one accent), so the sheet and the shipped texture agree on what each part is made of."""
    parts = P.build_parts()
    world = P.world_meshes(parts)

    w_px, h_px = 150, 190
    scene = I.Scene("player_grim", w_px, h_px, (w_px * 0.5, h_px - 12))
    for i, ((node, _, key), mesh) in enumerate(zip(parts, world)):
        mesh = mesh.xform(scale=M_TO_U / P.U_PER_M)
        if yaw_deg:
            mesh = mesh.xform(rot_deg=(0.0, 0.0, yaw_deg))
        scene.add(mesh, P.bake_material(key), seed=f"{seed}.{node}.{i}")
    return scene.render(wear=0.35, grime=0.65, rust=0.10, dust=0.20, exposure=0.90)


def _composite(sheet: np.ndarray, layer: np.ndarray, x: int, y: int) -> None:
    a = layer[..., 3:4].astype(np.float32) / 255.0
    region = sheet[y:y + layer.shape[0], x:x + layer.shape[1], :].astype(np.float32)
    sheet[y:y + layer.shape[0], x:x + layer.shape[1], :] = np.clip(
        layer[..., :3].astype(np.float32) * a + region * (1.0 - a), 0, 255).astype(np.uint8)


def _shrink(layer: np.ndarray, k: float) -> np.ndarray:
    w, h = max(1, round(layer.shape[1] * k)), max(1, round(layer.shape[0] * k))
    img = Image.fromarray(layer, "RGBA").resize((w, h), Image.BOX)
    return np.asarray(img)


def main(out_dir: str | None = None) -> Path:
    root = Path(out_dir) if out_dir else HERE
    out = root / "previews"
    out.mkdir(parents=True, exist_ok=True)

    views = []
    for i, yaw in enumerate((0.0, 90.0, 180.0, 270.0)):
        r = render_view(yaw, f"view{i}")
        views.append((r.rgba, r.shadow))
        print(f"  view {int(yaw):>3}deg rendered")

    asset = P.build_asset()
    atlas = asset["atlas"]
    side = 256
    atlas_img = np.asarray(Image.fromarray(atlas[..., :3], "RGB").resize((side, side), Image.BILINEAR))
    # show the atlas as the engine will: lifted, so it is judged at the brightness it renders at
    lifted = U.linear_to_srgb(np.clip(U.srgb_to_linear(atlas_img.astype(np.float32) / 255.0) * P.ALBEDO_LIFT, 0, 1)) * 255.0

    vh = max(v[0].shape[0] for v in views)
    vw = sum(v[0].shape[1] for v in views) + GAP * (len(views) - 1)
    small_h = round(vh * SMALL) + 2
    width = max(vw, 0) + PAD * 2 + GAP + side
    height = PAD * 2 + vh + GAP + max(small_h, side - vh - GAP if side > vh + GAP else 0)
    height = max(height, PAD * 2 + side)
    sheet = np.zeros((height, width, 3), dtype=np.uint8)
    sheet[:, :] = BG

    x = PAD
    for rgba, shadow in views:
        for layer in (shadow, rgba):
            _composite(sheet, layer, x, PAD)
        x += rgba.shape[1] + GAP
    y2 = PAD + vh + GAP
    x = PAD
    for rgba, shadow in views:
        for layer in (shadow, rgba):
            _composite(sheet, _shrink(layer, SMALL), x, y2)
        x += round(rgba.shape[1] * SMALL) + GAP
    ax = vw + PAD + GAP
    sheet[PAD:PAD + side, ax:ax + side] = np.clip(lifted, 0, 255).astype(np.uint8)

    path = out / "char_player.png"
    U.save_png(np.dstack([sheet, np.full(sheet.shape[:2], 255, np.uint8)]), path)
    print(f"gen_char_preview: {path} ({path.stat().st_size} bytes)")
    return path


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else None)
