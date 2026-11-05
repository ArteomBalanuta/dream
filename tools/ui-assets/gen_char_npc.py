"""Forged village NPCs: three rigid, animated, original GLBs, one per silhouette variant.

    python tools/ui-assets/gen_char_npc.py [out_dir] [--only name[,name]]

Writes <out_dir>/villager-<name>.glb (default: dream/animations/npcs/, where the engine's catalog loads them),
one each for vendor, smith and warden. Spec: docs/superpowers/specs/2026-11-04-models-and-rigs/00-acceptance.md
(M5, and contracts C1 and C5).

RIGID, not skinned, and that is a hard requirement rather than a convenience: AnimatedModel.RenderPose resolves
its per-node overrides by RENDERABLE node name, and in a skinned glTF the mesh hangs off one node while the
joints carry no primitives, so a skinned villager would have no addressable parts at all (finding F2).

Exactly the six renderable nodes of contract C1 - torso, head, arm-left, arm-right, leg-left, leg-right - under
a non-renderable root, on every variant. Anything a variant carries rides one of those six: the warden's staff
is part of arm-right, the smith's apron part of torso. A seventh renderable node would be a contract change.

Scale, axes, atlas encoding and palette discipline are the player's (gen_char_player): authored in u, 16 u = 1 m,
iso axes, exported at UNITS_PER_U with Y up, one baked albedo atlas whose bytes ARE linear albedo. The villagers
match the player's height rather than the 1.64 m boxes they replace - the decision is recorded in the spec.

All geometry is original. Colours are the grim palette: these are not the player, so they carry no steel accent
beyond what a villager would own - a smith's pauldron, a warden's helm, a vendor's hat band.
"""
from __future__ import annotations

import hashlib
import math
import sys
from dataclasses import dataclass, field
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
if str(HERE) not in sys.path:
    sys.path.insert(0, str(HERE))

import charforge as C  # noqa: E402
import gen_char_player as P  # noqa: E402
import gltfwrite as G  # noqa: E402
import isoforge as I  # noqa: E402

UNITS_PER_U = P.UNITS_PER_U
ATLAS_SIZE = 256
DEFAULT_OUT = HERE.parents[1] / "dream" / "animations" / "npcs"

# The six renderable nodes of C1 plus the root, with the player's proportions: a villager and the player stand
# on the same grid and the same normalisation, so they must agree on where a hip and a shoulder are.
PELVIS_U = P.PELVIS_U
ARM_NODE_X = 0.72
JOINTS = (
    ("root", None, (0.0, 0.0, 0.0)),
    ("torso", "root", (0.0, 0.0, PELVIS_U)),
    ("head", "torso", (0.0, 0.3, 9.4)),
    ("arm-left", "torso", (ARM_NODE_X, 0.0, 8.6)),
    ("arm-right", "torso", (-ARM_NODE_X, 0.0, 8.6)),
    ("leg-left", "root", (1.35, 0.0, PELVIS_U)),
    ("leg-right", "root", (-1.35, 0.0, PELVIS_U)),
)
RENDERABLE = ("torso", "head", "arm-left", "arm-right", "leg-left", "leg-right")


def _mat(hexv: str, rough: float = 0.92, f0: float = 0.028, cap: float = 0.22, metal: bool = False,
         wear: float = 0.6):
    return C._mat(hexv, rough, f0, cap, metal, wear)


MATS = {
    "robe.cold": _mat("#2B303C", 0.94, cap=0.22, wear=0.5),
    "robe.earth": _mat("#3C3127", 0.94, cap=0.24, wear=0.5),
    "robe.ash": _mat("#373B42", 0.94, cap=0.20, wear=0.5),
    "leather": C.GRIM_MATS["grim.leather"],
    "steel": _mat("#737B8B", 0.50, 0.055, 0.26, metal=True, wear=1.0),
    "iron": C.GRIM_MATS["grim.iron.pitted"],
    "flesh": C.GRIM_MATS["grim.flesh.grey"],
    "wood": _mat("#2A2520", 0.90, 0.030, 0.20, wear=0.9),
    "cloth.band": _mat("#241A12", 0.94, cap=0.26, wear=0.6),
    "canvas": _mat("#776E5C", 0.94, cap=0.24, wear=0.5),
}


@dataclass(frozen=True)
class Variant:
    """One villager's silhouette. `head` picks the headgear, which is what reads at the iso camera's distance."""
    robe: str                      # the body material key
    headgear: str                  # "hat" | "cap" | "hood"
    shoulders: float               # torso half-width in u at the shoulder line
    stoop: float                   # degrees of forward lean baked into the torso's bind pose
    accent: str                    # the material of the hard plates this variant owns
    staff: bool = False
    apron: bool = False
    age: tuple = (0.40, 0.62, 0.14, 0.22)   # wear, grime, rust, dust
    notes: str = ""


VARIANTS: dict[str, Variant] = {
    "vendor": Variant(robe="robe.earth", headgear="hat", shoulders=2.05, stoop=7.0, accent="canvas",
                      notes="a stooped hooded trader under a broad flat hat, a satchel on the torso"),
    "smith": Variant(robe="robe.ash", headgear="cap", shoulders=2.55, stoop=2.0, accent="steel", apron=True,
                     age=(0.55, 0.78, 0.10, 0.18),
                     notes="heavy shoulders, a leather apron with a steel breast plate, a low cap"),
    "warden": Variant(robe="robe.cold", headgear="hood", shoulders=2.25, stoop=0.0, accent="steel", staff=True,
                      notes="upright, a deep hood under a helm brim, a staff carried in the right hand"),
}


# ---------------------------------------------------------------------------- the figures

def _torso(v: Variant):
    """Tunic, belt and the shoulder yoke. The yoke is a broad near-horizontal plate: on the fixed play camera
    an up-facing facet is the only place a bright pixel can live (gen_char_player.plate() states the
    arithmetic), so it is what stops a robed figure rendering as one flat value under a brazier."""
    s = v.shoulders
    tunic = P.loft([(-0.4, 2.30, 1.55, 0.0, 0.0), (1.6, 2.05, 1.45, 0.0, 0.0), (4.4, s * 0.92, 1.40, 0.0, 0.05),
                    (7.4, s, 1.48, 0.0, 0.12), (9.0, s * 0.88, 1.32, 0.0, 0.2), (9.6, 1.05, 0.90, 0.0, 0.25)], 8)
    belt = P.loft([(1.7, 2.12, 1.50, 0.0, 0.0), (3.0, 2.02, 1.44, 0.0, 0.0)], 10)
    skirt = P.loft([(-2.6, 2.70, 1.72, 0.0, 0.0), (-0.8, 2.50, 1.64, 0.0, 0.0), (1.9, 2.08, 1.46, 0.0, 0.0)], 10)
    out = [(tunic, v.robe), (belt, "leather"), (skirt, v.robe)]
    yoke = P.plate(P._oct(s + 0.95, 2.30, 0.38, front=0.95), 0.44, 0.68)
    out.append((yoke.xform(rot_deg=(0.0, 0.0, 0.0), move=(0.0, 0.05, 9.55)), v.accent))
    if v.apron:
        # the apron is PART OF THE TORSO, not a node of its own (C1 is exactly six renderable nodes)
        apron = P.loft([(-3.2, 1.95, 0.55, 0.0, 1.25), (-0.6, 2.10, 0.60, 0.0, 1.35), (3.2, 2.05, 0.60, 0.0, 1.40),
                        (6.2, 1.70, 0.55, 0.0, 1.30)], 8)
        out.append((apron, "leather"))
        out.append((P.plate(P._oct(1.75, 1.05, 0.40), 0.42, 0.66)
                    .xform(rot_deg=(-24.0, 0.0, 0.0), move=(0.0, 1.30, 7.00)), v.accent))
    else:
        satchel = P.loft([(2.2, 1.35, 0.75, 1.95, -1.05), (4.0, 1.25, 0.70, 2.05, -1.15),
                          (5.0, 0.70, 0.45, 2.10, -1.20)], 8)
        out.append((satchel, "leather"))
    return out


def _head(v: Variant):
    """Head, face and headgear. Each variant's brim or crown is a broad plate with clear air above it; that
    combination is the one the albedo bake does not darken (gen_char_player.plate() and PLATE_RINGS state why)."""
    skull = P.loft([(-0.6, 1.35, 1.30, 0.0, 0.0), (0.6, 1.55, 1.50, 0.0, 0.1), (2.2, 1.60, 1.55, 0.0, 0.15),
                    (3.5, 1.35, 1.30, 0.0, 0.1), (4.2, 0.70, 0.70, 0.0, 0.0)], 8)
    out = [(skull, "flesh")]
    if v.headgear == "hat":
        out.append((P.plate(P._oct(2.55, 2.70, 0.36), 0.42, 0.66).xform(rot_deg=(-4.0, 0.0, 0.0),
                                                                       move=(0.0, 0.0, 3.70)), "canvas"))
        out.append((P.loft([(3.6, 1.25, 1.25, 0.0, 0.0), (5.0, 1.15, 1.15, 0.0, 0.0),
                            (5.4, 0.95, 0.95, 0.0, 0.0)], 8), "canvas"))
        out.append((P.plate(P._oct(1.28, 1.32, 0.38), 0.40, 0.62).xform(move=(0.0, 0.0, 5.45)), "cloth.band"))
    elif v.headgear == "cap":
        out.append((P.plate(P._oct(1.78, 1.92, 0.38), 0.44, 0.64).xform(rot_deg=(-7.0, 0.0, 0.0),
                                                                       move=(0.0, 0.05, 3.80)), "leather"))
        out.append((P.plate(P._oct(1.05, 1.15, 0.38), 0.40, 0.62).xform(move=(0.0, -0.1, 4.45)), v.accent))
    else:
        out.append((P.loft([(-1.0, 1.95, 1.85, 0.0, -0.15), (0.8, 1.80, 1.80, 0.0, 0.0),
                            (2.4, 1.85, 1.95, 0.0, 0.0), (3.8, 1.60, 1.80, 0.0, -0.2),
                            (4.8, 1.05, 1.35, 0.0, -0.7), (5.4, 0.45, 0.75, 0.0, -1.3)], 10), v.robe))
        out.append((P.plate(P._oct(2.18, 2.34, 0.36), 0.44, 0.66).xform(rot_deg=(-5.0, 0.0, 0.0),
                                                                       move=(0.0, 0.05, 3.95)), v.accent))
        out.append((P.plate(P._oct(1.30, 0.70, 0.40, front=1.0), 0.40, 0.62)
                    .xform(rot_deg=(-32.0, 0.0, 0.0), move=(0.0, 1.35, 2.70)), v.accent))
    return out


def _arm(v: Variant, side: float):
    sleeve = P.loft([(-8.8, 0.60, 0.60, 0.0, 0.1), (-6.4, 0.74, 0.74, 0.0, 0.2), (-3.4, 0.92, 0.92, 0.0, 0.25),
                     (-0.6, 1.08, 1.08, 0.0, 0.1), (0.9, 0.72, 0.72, 0.0, 0.0)], 8)
    hand = P.loft([(-10.6, 0.40, 0.30, 0.0, 0.15), (-9.9, 0.56, 0.42, 0.0, 0.18),
                   (-9.0, 0.54, 0.44, 0.0, 0.14), (-8.6, 0.46, 0.40, 0.0, 0.12)], 6)
    mv = (side * P.ARM_OFFSET_U, 0.0, 0.0)
    out = [(sleeve.xform(move=mv), v.robe), (hand.xform(move=mv), "flesh")]
    out.append((P.plate(P._oct(0.90, 1.00, 0.38), 0.38, 0.66)
                .xform(rot_deg=(-6.0, 0.0, 0.0), move=(mv[0], 0.2, -4.6)), v.accent))
    if v.staff and side < 0:
        # The staff is geometry of arm-right, not a seventh node. Its foot stops a unit clear of the ground so
        # the walk clip's arm sway cannot drive it under the terrain.
        bottom = 1.0 - (PELVIS_U + JOINTS[4][2][2])     # world z = 1.0 u, expressed in the arm's local frame
        staff = P.loft([(bottom, 0.20, 0.20, 0.0, 0.0), (bottom + 7.0, 0.22, 0.22, 0.0, 0.0),
                        (bottom + 18.0, 0.21, 0.21, 0.0, 0.0), (bottom + 26.0, 0.19, 0.19, 0.0, 0.0)], 6)
        knob = P.loft([(bottom + 25.4, 0.34, 0.34, 0.0, 0.0), (bottom + 26.2, 0.46, 0.46, 0.0, 0.0),
                       (bottom + 27.0, 0.30, 0.30, 0.0, 0.0)], 8)
        out += [(staff.xform(move=(mv[0] - 0.55, 0.85, 0.0)), "wood"),
                (knob.xform(move=(mv[0] - 0.55, 0.85, 0.0)), "iron")]
    return out


def _leg(v: Variant):
    trouser = P.loft([(-11.4, 0.74, 0.74, 0.0, 0.0), (-8.6, 0.90, 0.90, 0.0, -0.10), (-6.4, 0.86, 0.86, 0.0, 0.0),
                      (-3.2, 1.06, 1.06, 0.0, 0.0), (0.0, 1.20, 1.20, 0.0, 0.0), (0.4, 0.92, 0.92, 0.0, 0.0)], 8)
    boot = P.loft([(-PELVIS_U, 0.90, 1.40, 0.0, 0.66), (-13.8, 0.96, 1.46, 0.0, 0.66),
                   (-12.8, 0.90, 1.08, 0.0, 0.48), (-11.3, 0.88, 0.94, 0.0, 0.08),
                   (-10.0, 0.92, 0.92, 0.0, 0.0)], 8)
    sabaton = P.plate(P._oct(1.26, 1.92, 0.34, front=1.05), 0.38, 0.66).xform(rot_deg=(-8.0, 0.0, 0.0),
                                                                             move=(0.0, 0.62, -12.10))
    # A knee plate, low on the figure. Height matters more than size here: MEASURED, a brazier 1.5 units away
    # at a figure's own mid height rakes a horizontal facet at the shin at N.L 0.72 and the same facet on the
    # hat at 0.39, because the light is only half a unit above the head but a whole one above the boot.
    knee = P.plate(P._oct(1.22, 1.44, 0.36), 0.40, 0.68).xform(rot_deg=(-12.0, 0.0, 0.0), move=(0.0, 0.35, -5.40))
    return [(trouser, v.robe), (boot, "leather"), (sabaton, v.accent), (knee, v.accent)]


def build_parts(name: str):
    """-> list of (node_name, local mesh in u, material key), the node geometry of one variant."""
    v = VARIANTS[name]
    parts = [("torso", m, k) for m, k in _torso(v)]
    parts += [("head", m, k) for m, k in _head(v)]
    for node, side in (("arm-left", 1.0), ("arm-right", -1.0)):
        parts += [(node, m, k) for m, k in _arm(v, side)]
    for node in ("leg-left", "leg-right"):
        parts += [(node, m, k) for m, k in _leg(v)]
    return parts


def joint_world_u() -> dict:
    out = {}
    for name, parent, t in JOINTS:
        out[name] = (out[parent] if parent else np.zeros(3)) + np.array(t, dtype=np.float64)
    return out


def world_meshes(parts):
    wp = joint_world_u()
    return [m.xform(move=tuple(wp[node])) for node, m, _ in parts]


# ---------------------------------------------------------------------------- clips

def clip_idle(n: dict, v: Variant) -> G.GltfAnimation:
    """Breathing, and whatever the trade is: the smith rocks on his heels, the vendor shifts his weight, the
    warden barely moves. Two seconds, looping."""
    t = [0.0, 1.0, 2.0]
    sway = 1.0 if v.headgear == "cap" else (2.2 if v.headgear == "hat" else 0.8)
    arm = 0.0 if v.staff else sway
    return P._clip("idle", t, {
        "torso": [P.quat_x(v.stoop + sway * 0.5), P.quat_x(v.stoop - sway * 0.4), P.quat_x(v.stoop + sway * 0.5)],
        "head": [P.quat_x(1.5), P.quat_x(3.0 + sway), P.quat_x(1.5)],
        "arm-left": [P.quat_x(sway * 1.6), P.quat_x(-sway), P.quat_x(sway * 1.6)],
        "arm-right": [P.quat_x(-arm), P.quat_x(arm * 1.4), P.quat_x(-arm)],
    }, index=n)


def clip_walk(n: dict, v: Variant) -> G.GltfAnimation:
    """An unhurried village walk, a beat slower than the player's trudge. The warden's right arm is held: it
    carries the staff, and a swinging staff would scythe through the ground."""
    t = [0.0, 0.26, 0.52, 0.78, 1.04]
    swing = [18.0, 0.0, -18.0, 0.0, 18.0]
    dip = [0.0, -0.016, 0.0, -0.016, 0.0]
    rot = {
        "leg-left": [P.quat_x(a) for a in swing],
        "leg-right": [P.quat_x(-a) for a in swing],
        "arm-left": [P.quat_x(-a * 0.6) for a in swing],
        "arm-right": [P.quat_x(a * (0.04 if v.staff else 0.6)) for a in swing],
        "torso": [P.quat_x(v.stoop + 2.0) for _ in swing],
        "head": [P.quat_x(-1.0) for _ in swing],
    }
    return P._clip("walk", t, rot, {"root": [[0.0, d, 0.0] for d in dip]}, index=n)


# ---------------------------------------------------------------------------- build

def build_asset(name: str) -> dict:
    """Pure function of the source: nothing here reads the clock, the environment or the filesystem."""
    v = VARIANTS[name]
    parts = build_parts(name)
    wmeshes = world_meshes(parts)
    meshes, uvs, rects = C.unwrap_split(wmeshes, ATLAS_SIZE, axis=P.unwrap_axes(wmeshes))
    wear, grime, rust, dust = v.age
    atlas = C.bake_albedo(meshes, None, None, uvs, rects, ATLAS_SIZE, materials=[MATS[k] for _, _, k in parts],
                          seed="villager." + name, wear=wear, grime=grime, rust=rust, dust=dust)
    png, scale, enc = P.encode_atlas(atlas)

    index = {j[0]: i for i, j in enumerate(JOINTS)}
    nodes = [G.GltfNode(name=jn, parent=None if parent is None else index[parent],
                        translation=(t[0] * UNITS_PER_U, t[2] * UNITS_PER_U, t[1] * UNITS_PER_U))
             for jn, parent, t in JOINTS]
    wp = joint_world_u()
    primitives = []
    for (node, _, _), wm, uv in zip(parts, meshes, uvs):
        pos, nrm, uvo, idx = P._render_arrays(wm.xform(move=tuple(-wp[node])), uv)
        primitives.append(G.GltfPrimitive(positions=pos, normals=nrm, uvs=uvo, indices=idx, joints=None,
                                          weights=None, material=0, node=index[node]))
    materials = [{"name": f"villager.{name}", "base_color_factor": (scale, scale, scale, 1.0),
                  "base_color_png": png, "metallic": 0.0, "roughness": 0.9}]
    animations = [clip_idle(index, v), clip_walk(index, v)]
    return dict(name=name, nodes=nodes, primitives=primitives, materials=materials, animations=animations,
                atlas=atlas, encoded=enc, scale=scale, rects=rects, parts=parts, emissive=[],
                meshes=meshes, uvs=uvs, variant=v)


def glb_bytes(asset: dict) -> bytes:
    return G.build_glb(asset["nodes"], asset["primitives"], None, None, asset["animations"], asset["materials"])


def main(out_dir: str | None = None, only: list[str] | None = None) -> Path:
    out = Path(out_dir) if out_dir else DEFAULT_OUT
    out.mkdir(parents=True, exist_ok=True)
    unknown = [n for n in (only or []) if n not in VARIANTS]
    if unknown:
        raise SystemExit(f"unknown villagers: {unknown}")
    for name in [n for n in VARIANTS if not only or n in only]:
        a = build_asset(name)
        data = glb_bytes(a)
        (out / f"villager-{name}.glb").write_bytes(data)
        verts = sum(len(p.positions) for p in a["primitives"])
        tris = sum(len(p.indices) for p in a["primitives"])
        print(f"gen_char_npc: villager-{name:8s} parts={len(a['parts']):3d} verts={verts:5d} tris={tris:5d} "
              f"clips={[c.name for c in a['animations']]} size={len(data):7d} "
              f"sha1={hashlib.sha1(data).hexdigest()[:12]}")
    return out


if __name__ == "__main__":
    args = [x for x in sys.argv[1:] if not x.startswith("--")]
    sel = None
    if "--only" in sys.argv:
        sel = sys.argv[sys.argv.index("--only") + 1].split(",")
        args = [x for x in args if x != sys.argv[sys.argv.index("--only") + 1]]
    main(args[0] if args else None, sel)
