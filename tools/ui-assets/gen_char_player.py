"""Grim player actor: original procedural geometry exported as a rigid-part animated GLB.

    python tools/ui-assets/gen_char_player.py [out_dir]

Writes <out_dir>/player_grim.glb (default: tools/ui-assets/char/). Never writes dream/ anything.

Shape: a rigid part hierarchy, which is how dream's own player model is built - one mesh per part
node, no skin, animation driven by node rotation. Verified against kenney-blocky-character-a.glb,
which reports skins=0 and six part meshes.

Authored in metres, Y up, +Z forward, matching PlayerModelAssetDefinition.AuthoredForwardAxis.
The engine measures authored height at runtime (WorldAssetBounds.MeasureDimensions), so the
figure may be authored at life size and needs no C# constant to change.

Art direction: docs/superpowers/specs/2026-11-02-grim-actor-forge/01-art-direction.md.
All geometry is original. Colours come from the grim.* palette only.
"""
from __future__ import annotations

import math
import sys
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
if str(HERE) not in sys.path:
    sys.path.insert(0, str(HERE))

import isoforge as I  # noqa: E402
import gltfwrite as G  # noqa: E402

# grim palette, sRGB hex from the art-direction spec -> linear base colour factors
PALETTE = {
    "iron.black": "#17181A",
    "iron.pitted": "#212022",
    "bone": "#4A4740",
    "leather": "#241C16",
    "cloth.dark": "#1A1720",
    "cloth.ash": "#2A2724",
    "flesh.grey": "#3B332E",
}


def srgb_to_linear(hex_colour: str) -> tuple:
    h = hex_colour.lstrip("#")
    out = []
    for i in (0, 2, 4):
        c = int(h[i:i + 2], 16) / 255.0
        out.append(c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4)
    return (out[0], out[1], out[2], 1.0)


# ---------------------------------------------------------------------------- mesh plumbing

def _render_arrays(mesh: I.Mesh):
    """isoforge Mesh -> (positions, normals, uvs, indices) with glTF axes (Y up, from iso Z up).

    A mesh with n=None is flat-shaded, so its faces are unwelded and each vertex takes its own
    face normal. Otherwise the authored vertex normals are kept.
    """
    v = np.asarray(mesh.v, dtype=np.float64)
    f = np.asarray(mesh.f, dtype=np.int64)

    if mesh.n is None:
        # unweld: one vertex per face corner, normal = face normal
        tri = v[f]                                            # (T,3,3)
        e0 = tri[:, 1] - tri[:, 0]
        e1 = tri[:, 2] - tri[:, 0]
        fn = np.cross(e0, e1)
        ln = np.linalg.norm(fn, axis=1, keepdims=True)
        fn = np.divide(fn, np.maximum(ln, 1e-12))
        pos = tri.reshape(-1, 3)
        nrm = np.repeat(fn, 3, axis=0)
        idx = np.arange(len(pos), dtype=np.uint32).reshape(-1, 3)
        uv_src = None
    else:
        pos = v
        nrm = np.asarray(mesh.n, dtype=np.float64)
        idx = f.astype(np.uint32)
        uv_src = mesh.uv

    # iso is Z up; glTF is Y up. (x, y, z)_iso -> (x, z, y)_gltf keeps handedness and
    # puts iso +Y (forward) on glTF +Z, matching AuthoredForwardAxis = UnitZ.
    pos = np.stack((pos[:, 0], pos[:, 2], pos[:, 1]), axis=1)
    nrm = np.stack((nrm[:, 0], nrm[:, 2], nrm[:, 1]), axis=1)

    if uv_src is not None and len(uv_src) == len(pos):
        uv = np.asarray(uv_src, dtype=np.float32)
    else:
        # cheap box projection, good enough to carry a baked texture later
        lo, hi = pos.min(0), pos.max(0)
        span = np.maximum(hi - lo, 1e-6)
        uv = np.stack(((pos[:, 0] - lo[0]) / span[0], (pos[:, 1] - lo[1]) / span[1]), axis=1)

    return (pos.astype(np.float32), nrm.astype(np.float32),
            np.ascontiguousarray(uv, dtype=np.float32), np.ascontiguousarray(idx))


def quat_x(deg: float) -> tuple:
    a = math.radians(deg) * 0.5
    return (math.sin(a), 0.0, 0.0, math.cos(a))


def quat_z(deg: float) -> tuple:
    a = math.radians(deg) * 0.5
    return (0.0, 0.0, math.sin(a), math.cos(a))


# ---------------------------------------------------------------------------- the figure
# Proportions in metres. A gaunt, hunched 1.78 m figure: long limbs, narrow shoulders,
# a heavy hooded head. Asymmetry is the identity hook - the left shoulder carries a plate.

PELVIS_Y = 0.92       # hip height; the legs must reach exactly this far to stand on y=0
TORSO_H = 0.60        # pelvis to shoulder
NECK_H = 0.07
HEAD_R = 0.098        # a 0.20 m head on a 1.78 m figure - small, so the body reads as tall
ARM_LEN = 0.60        # shoulder to wrist
LEG_LEN = PELVIS_Y    # hip to sole. Anything less and the figure floats.
BOOT_H = 0.09


def build_parts():
    """Each part is (node_name, parent_name, local_translation, mesh, material_key).

    Geometry is authored in the part's OWN local space (origin at its joint), because a rigid
    glTF node holds local geometry and its TRS places it.
    """
    parts = []

    # torso: a tapered slab, deepest at the chest and drawn in at the waist. Built as a
    # lathe-free stack of two boxes so the waist reads as a break in the silhouette.
    # I.box spans z 0..h with its base on the origin, and is centred in x and y.
    chest = I.box(0.33, 0.19, TORSO_H * 0.52, chamfer=0.030)
    chest = chest.xform(move=(0.0, 0.0, TORSO_H * 0.50))
    waist = I.box(0.24, 0.16, TORSO_H * 0.52, chamfer=0.022)
    parts.append(("torso", "root", (0.0, PELVIS_Y, 0.0), chest.merge(waist), "iron.black"))

    # a single asymmetric shoulder plate, on the left only - the identity hook
    pauldron = I.convex("grim.pauldron", 0.105, 1, squash=(1.0, 0.80, 0.55))
    pauldron = pauldron.xform(move=(0.205, 0.0, TORSO_H * 0.95))
    parts.append(("pauldron-left", "torso", (0.0, 0.0, 0.0), pauldron, "iron.pitted"))

    # head: a small hooded mass on a short neck, carried slightly forward (hunched)
    neck = I.tube([[0, 0, 0], [0, 0.012, NECK_H]], [0.038, 0.034], sides=6)
    head = I.convex("grim.head", HEAD_R, 2, squash=(0.90, 1.0, 1.18))
    head = head.xform(move=(0.0, 0.032, NECK_H + HEAD_R * 0.92))
    parts.append(("head", "torso", (0.0, TORSO_H, 0.0), neck.merge(head), "cloth.dark"))

    # arms: long and thin, hanging. Built downward from the shoulder joint.
    for side, sx in (("left", 1.0), ("right", -1.0)):
        upper = I.tube([[0, 0, 0], [0, 0, -ARM_LEN]], [0.055, 0.038], sides=7)
        hand = I.convex(f"grim.hand.{side}", 0.045, 1, squash=(0.8, 0.7, 1.0))
        hand = hand.xform(move=(0.0, 0.0, -ARM_LEN - 0.025))
        parts.append((f"arm-{side}", "torso", (sx * 0.180, TORSO_H * 0.93, 0.0),
                      upper.merge(hand), "leather"))

    # legs: built downward from the pelvis, each ending in a boot that grounds the silhouette
    for side, sx in (("left", 1.0), ("right", -1.0)):
        leg = I.tube([[0, 0, 0], [0, 0, -(LEG_LEN - BOOT_H)]], [0.077, 0.052], sides=7)
        boot = I.box(0.095, 0.17, BOOT_H, chamfer=0.016)
        boot = boot.xform(move=(0.0, 0.028, -LEG_LEN))   # base lands exactly on the ground
        parts.append((f"leg-{side}", "root", (sx * 0.088, PELVIS_Y, 0.0),
                      leg.merge(boot), "cloth.ash"))

    return parts


# ---------------------------------------------------------------------------- clips

def clip_idle(n: dict) -> G.GltfAnimation:
    """Almost still: a slow breath in the torso and a faint sway in the arms."""
    t = np.array([0.0, 0.9, 1.8], dtype=np.float32)
    rot = {
        n["torso"]: (t, np.array([quat_x(1.2), quat_x(-0.8), quat_x(1.2)], dtype=np.float32)),
        n["head"]: (t, np.array([quat_x(2.0), quat_x(3.4), quat_x(2.0)], dtype=np.float32)),
        n["arm-left"]: (t, np.array([quat_x(2.5), quat_x(-1.5), quat_x(2.5)], dtype=np.float32)),
        n["arm-right"]: (t, np.array([quat_x(-2.0), quat_x(2.0), quat_x(-2.0)], dtype=np.float32)),
    }
    return G.GltfAnimation(name="idle", rotations=rot, translations={})


def clip_walk(n: dict) -> G.GltfAnimation:
    """A four-beat trudge: legs swing, arms counter-swing, the body dips twice per cycle."""
    t = np.array([0.0, 0.2, 0.4, 0.6, 0.8], dtype=np.float32)
    swing = [22.0, 0.0, -22.0, 0.0, 22.0]
    rot = {
        n["leg-left"]: (t, np.array([quat_x(a) for a in swing], dtype=np.float32)),
        n["leg-right"]: (t, np.array([quat_x(-a) for a in swing], dtype=np.float32)),
        n["arm-left"]: (t, np.array([quat_x(-a * 0.6) for a in swing], dtype=np.float32)),
        n["arm-right"]: (t, np.array([quat_x(a * 0.6) for a in swing], dtype=np.float32)),
        n["torso"]: (t, np.array([quat_z(1.5), quat_z(0.0), quat_z(-1.5), quat_z(0.0),
                                  quat_z(1.5)], dtype=np.float32)),
    }
    dip = [0.0, -0.018, 0.0, -0.018, 0.0]
    trs = {n["root"]: (t, np.array([[0.0, d, 0.0] for d in dip], dtype=np.float32))}
    return G.GltfAnimation(name="walk", rotations=rot, translations=trs)


# ---------------------------------------------------------------------------- build

def main(out_dir: str | None = None) -> Path:
    out = Path(out_dir) if out_dir else HERE / "char"
    out.mkdir(parents=True, exist_ok=True)

    parts = build_parts()

    # nodes: root first, then every part node, parented by name
    names = ["root"] + [p[0] for p in parts]
    index = {name: i for i, name in enumerate(names)}
    nodes = [G.GltfNode(name="root", parent=None)]
    for part_name, parent, translation, _, _ in parts:
        nodes.append(G.GltfNode(name=part_name, parent=index[parent],
                                translation=(float(translation[0]), float(translation[1]),
                                             float(translation[2]))))

    # one material per palette key actually used, so each part keeps its own grim colour
    used = []
    for *_, mat_key in parts:
        if mat_key not in used:
            used.append(mat_key)
    materials = [{"name": f"grim.{k}",
                  "base_color_factor": srgb_to_linear(PALETTE[k]),
                  "metallic": 0.0,
                  "roughness": 0.9} for k in used]

    primitives = []
    for part_name, _, _, mesh, mat_key in parts:
        pos, nrm, uv, idx = _render_arrays(mesh)
        primitives.append(G.GltfPrimitive(positions=pos, normals=nrm, uvs=uv, indices=idx,
                                          joints=None, weights=None,
                                          material=used.index(mat_key),
                                          node=index[part_name]))

    animations = [clip_idle(index), clip_walk(index)]

    path = out / "player_grim.glb"
    G.write_glb(str(path), nodes, primitives, None, None, animations, materials)

    tris = sum(len(p.indices) for p in primitives)
    verts = sum(len(p.positions) for p in primitives)
    print(f"gen_char_player: {path}")
    print(f"  parts={len(parts)} nodes={len(nodes)} materials={len(materials)} "
          f"verts={verts} tris={tris} clips={[a.name for a in animations]}")
    print(f"  size={path.stat().st_size} bytes")
    return path


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else None)
