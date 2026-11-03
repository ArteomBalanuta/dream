"""Review render for the grim actor: a lit turntable sheet, for judging the look by eye.

    python tools/ui-assets/gen_char_preview.py [out_dir]

Writes <out_dir>/previews/char_player.png (default: tools/ui-assets/previews/).

This renders the SAME geometry gen_char_player.py exports, through isoforge's chain with a
darker rig, so the sheet shows the form and the material reads. It is a review aid, not the
shipped asset - the shipped asset is the .glb, which the engine lights itself.
"""
from __future__ import annotations

import sys
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
if str(HERE) not in sys.path:
    sys.path.insert(0, str(HERE))

import forge  # noqa: E402
import isoforge as I  # noqa: E402
import uiart as U  # noqa: E402
import gen_char_player as P  # noqa: E402

M_TO_U = 26.0   # isoforge works in u; this frames a 1.78 m figure at about 135 px tall
BG = (200, 200, 200)
PAD = 18
GAP = 22

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

# Linear albedo straight from the grim palette, with the spec's roughness and sat_cap.
MATS = {
    "iron.black": I.IsoMat((0.0085, 0.0090, 0.0100), (0.035, 0.035, 0.035), 0.62, sat_cap=0.22),
    "iron.pitted": I.IsoMat((0.0150, 0.0145, 0.0155), (0.030, 0.030, 0.030), 0.72, sat_cap=0.24),
    "bone": I.IsoMat((0.0720, 0.0660, 0.0540), (0.028, 0.028, 0.028), 0.80, sat_cap=0.24),
    "leather": I.IsoMat((0.0170, 0.0110, 0.0075), (0.035, 0.035, 0.035), 0.84, sat_cap=0.26),
    "cloth.dark": I.IsoMat((0.0092, 0.0078, 0.0130), (0.030, 0.030, 0.030), 0.92, sat_cap=0.24),
    "cloth.ash": I.IsoMat((0.0235, 0.0215, 0.0195), (0.028, 0.028, 0.028), 0.94, sat_cap=0.22),
    "flesh.grey": I.IsoMat((0.0550, 0.0400, 0.0340), (0.032, 0.032, 0.032), 0.86, sat_cap=0.28),
}


def _world_mesh(part, parts_by_name) -> I.Mesh:
    """Place a part's local geometry into world space by walking its parent chain."""
    name, parent, translation, mesh, _ = part
    offset = np.zeros(3, dtype=np.float64)
    cursor = part
    while cursor is not None:
        t = cursor[2]
        # author space is (x, y_up, z) in metres; isoforge wants z up
        offset += np.array([t[0], t[2], t[1]], dtype=np.float64)
        parent_name = cursor[1]
        cursor = parts_by_name.get(parent_name)
    return mesh.xform(move=tuple(offset * M_TO_U), scale=M_TO_U)


def render_view(yaw_deg: float, seed: str) -> I.Rendered:
    parts = P.build_parts()
    by_name = {p[0]: p for p in parts}

    height_u = 1.70 * M_TO_U
    w_px, h_px = 150, 180
    scene = I.Scene(f"player_grim", w_px, h_px, (w_px * 0.5, h_px - 12))

    for part in parts:
        mesh = _world_mesh(part, by_name)
        if yaw_deg:
            mesh = mesh.xform(rot_deg=(0.0, 0.0, yaw_deg))
        scene.add(mesh, MATS[part[4]], seed=f"{seed}.{part[0]}")

    return scene.render(wear=0.35, grime=0.65, rust=0.10, dust=0.20, exposure=0.90)


def main(out_dir: str | None = None) -> Path:
    root = Path(out_dir) if out_dir else HERE
    out = root / "previews"
    out.mkdir(parents=True, exist_ok=True)

    views = []
    for i, yaw in enumerate((0.0, 90.0, 180.0, 270.0)):
        r = render_view(yaw, f"view{i}")
        views.append((r.rgba, r.shadow))
        print(f"  view {int(yaw):>3}deg rendered")

    h = max(v[0].shape[0] for v in views)
    w = sum(v[0].shape[1] for v in views) + GAP * (len(views) - 1)
    sheet = np.zeros((h + PAD * 2, w + PAD * 2, 3), dtype=np.uint8)
    sheet[:, :] = BG

    x = PAD
    for rgba, shadow in views:
        for layer in (shadow, rgba):
            a = layer[..., 3:4].astype(np.float32) / 255.0
            region = sheet[PAD:PAD + layer.shape[0], x:x + layer.shape[1], :].astype(np.float32)
            sheet[PAD:PAD + layer.shape[0], x:x + layer.shape[1], :] = np.clip(
                layer[..., :3].astype(np.float32) * a + region * (1.0 - a), 0, 255).astype(np.uint8)
        x += layer.shape[1] + GAP

    path = out / "char_player.png"
    U.save_png(np.dstack([sheet, np.full(sheet.shape[:2], 255, np.uint8)]), path)
    print(f"gen_char_preview: {path} ({path.stat().st_size} bytes)")
    return path


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else None)
