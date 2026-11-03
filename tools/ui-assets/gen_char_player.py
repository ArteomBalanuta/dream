"""Grim player actor: original procedural geometry exported as a rigid-part animated GLB.

    python tools/ui-assets/gen_char_player.py [out_dir]

Writes <out_dir>/player_grim.glb (default: tools/ui-assets/char/). Never writes dream/ anything; the shipped
copy is dream/animations/players/player-grim.glb, which is this file under the name the engine's
PlayerModelAssetDefinition loads.

Shape: a rigid part hierarchy, which is how dream's own player model is built - one mesh per part node, no
skin, animation driven by node rotation. A node may carry several primitives (one per material), so a
pauldron or a cloak rides the torso node and takes the engine's hit flinch with it: PlayerPoseLayers only
overrides the torso, head and arm nodes, so a child node of the torso would be left behind in a flinch.
The node names torso / head / arm-left / arm-right / leg-left / leg-right are the engine's contract.

Authored in u (16 u = 1 m, the isoforge scale), iso axes (x right, y forward, z up), then exported in metres,
Y up, +Z forward, matching PlayerModelAssetDefinition.AuthoredForwardAxis. The engine measures the authored
height at runtime (WorldAssetBounds.MeasureDimensions), so the figure needs no C# constant to change.

Texture: one baked albedo atlas (charforge.unwrap_split + bake_albedo) on one material. How the engine reads
it matters and is stated at encode_atlas().

Art direction: docs/superpowers/specs/2026-11-02-grim-actor-forge/01-art-direction.md.
All geometry is original. Colours come from the grim.* palette plus one dull accent.
"""
from __future__ import annotations

import hashlib
import io
import math
import sys
from pathlib import Path

import numpy as np
from PIL import Image

HERE = Path(__file__).resolve().parent
if str(HERE) not in sys.path:
    sys.path.insert(0, str(HERE))

import charforge as C  # noqa: E402
import gltfwrite as G  # noqa: E402
import isoforge as I  # noqa: E402
import uiart as U  # noqa: E402

U_PER_M = 16.0
TAU = math.tau

# The figure is authored in u (16 u = 1 m) and EXPORTED in the engine's blocky-character units, not metres:
# PlayerModelAssetDefinition normalises by height, so the absolute size is free, but whatever the player holds
# rides in the model's own units - HeldWeaponRenderer puts the fist at (+-0.2, -0.9) in the arm's frame and
# draws the weapon at the blocky's proportions. The blocky is 2.7 tall with a 1.0 arm; a figure exported at
# 0.088 units per u is 2.7 tall with its hand at -0.9, so a held axe lands in the hand. Measured in-game
# before this: authored in metres, the axe floated a hand-width off the fist.
UNITS_PER_U = 0.088
ARM_OFFSET_U = 0.2 / UNITS_PER_U   # the grip sits 0.2 units outboard of the arm node (see arm_node_x below)

# Palette keys -> bake materials. grim.* where the palette has the material; the figure's value structure
# (a dark cold cloak against cool steel and pale bone) needs a few that it does not, so they live here:
# (hex, rough, f0, sat_cap, bare hex for worn-through edges or None).
CUSTOM = {
    "cloth.red": ("#5A1E1A", 0.90, 0.030, 0.40, None),       # the one saturated thing, as in the target
    "steel.cool": ("#5C6475", 0.50, 0.050, 0.26, "#7E869A"),
    "cloth.cold": ("#2A3040", 0.92, 0.030, 0.28, None),
    "cloth.ash.cold": ("#363940", 0.94, 0.028, 0.24, None),
}
MATERIALS = {
    "leather": "grim.leather",
    "bone": "grim.bone",
    "flesh.grey": "grim.flesh.grey",
}


def bake_material(key: str):
    if key in CUSTOM:
        hexv, rough, f0, cap, bare = CUSTOM[key]
        return I.IsoMat(tuple(C._lin(hexv)), (f0,) * 3, rough, tuple(C._lin(bare)) if bare else None,
                        (0.22,) * 3 if bare else None, 1.0 if bare else 0.5, None, cap)
    return C.GRIM_MATS[MATERIALS[key]]


# The palette above is authored for a renderer that bakes light. The engine does not: enemyFragment lights an
# actor as (ambient * scale + direct * 0.75) * baseColor, then multiplies by exposure, with no GGX, no
# shadowing and no tonemap - strictly linear in albedo. Dark albedo in, dark pixels out.
#
# MEASURED in-game on the isometric view: iron.black at linear 0.0085 rendered to a mean sRGB luma of 0.175,
# so the engine's whole gain is about 3.0x. The grass beside it measured 0.218, which the actor has to beat or
# it reads as a hole rather than a figure.
#
# This is a render-target correction, not an art change: the authored hexes stay the art direction and the
# preview sheet keeps using them unlifted, since isoforge does bake its own light. The lift is applied exactly
# once, in encode_atlas(), to the linear albedo of the baked atlas.
ALBEDO_LIFT = 3.0
ATLAS_SIZE = 512
UNWRAP_AXIS = (0.0, 0.0, 1.0)   # every part is a loft along iso z, so project around z, not around the longest extent


# ---------------------------------------------------------------------------- mesh plumbing

def loft(rings, sides: int = 10) -> I.Mesh:
    """A closed solid through elliptical rings, bottom to top. ring = (z, rx, ry, cx, cy) in u. Few sides keep
    the facets (isoforge welds normals only across creases under 35 degrees, so 8 sides stays faceted).
    The two ends are closed with flat caps on their own vertices."""
    R = np.asarray(rings, dtype=np.float64)
    th = np.linspace(0.0, TAU, sides + 1)
    x = R[:, 3:4] + R[:, 1:2] * np.cos(th)[None, :]
    y = R[:, 4:5] + R[:, 2:3] * np.sin(th)[None, :]
    z = np.broadcast_to(R[:, 0:1], x.shape)
    P = np.stack((x, y, z), axis=-1)
    uv = np.stack((np.broadcast_to(th[None, :] / TAU, x.shape), z), axis=-1)
    out = I.grid(P, uv)
    for end, sgn in ((0, -1.0), (len(R) - 1, 1.0)):
        ring = P[end, :sides]
        centre = np.array((R[end, 3], R[end, 4], R[end, 0]))
        cap_v = np.vstack((ring, centre[None, :]))
        ii = np.arange(sides)
        cf = np.stack((ii, (ii + 1) % sides, np.full(sides, sides)), axis=1)
        cf = I._orient_outward(cap_v, cf, centre - np.array((0.0, 0.0, sgn)))
        cn = np.broadcast_to(np.array((0.0, 0.0, sgn)), cap_v.shape)
        out = out.merge(I.Mesh(cap_v, cf, cn, np.zeros((len(cap_v), 2), dtype=np.float32)))
    return out


def _render_arrays(mesh: I.Mesh, uv_in: np.ndarray):
    """isoforge Mesh in u -> glTF (positions, normals, uvs, indices) in metres, Y up (from iso Z up).

    Every mesh from loft() carries vertex normals; a mesh with n=None is flat-shaded, so its faces are
    unwelded and each corner takes its face normal (and its own uv)."""
    v = np.asarray(mesh.v, dtype=np.float64)
    f = np.asarray(mesh.f, dtype=np.int64)
    uv = np.asarray(uv_in, dtype=np.float64)
    if mesh.n is None:
        tri = v[f]
        fn = np.cross(tri[:, 1] - tri[:, 0], tri[:, 2] - tri[:, 0])
        fn = fn / np.maximum(np.linalg.norm(fn, axis=1, keepdims=True), 1e-12)
        pos, nrm = tri.reshape(-1, 3), np.repeat(fn, 3, axis=0)
        uv = uv[f].reshape(-1, 2)
        idx = np.arange(len(pos), dtype=np.uint32).reshape(-1, 3)
    else:
        pos, nrm, idx = v, np.asarray(mesh.n, dtype=np.float64), f.astype(np.uint32)
    # iso (x, y, z) -> glTF (x, z, y): handedness kept, iso +Y (forward) lands on glTF +Z
    pos = np.stack((pos[:, 0], pos[:, 2], pos[:, 1]), axis=1) * UNITS_PER_U
    nrm = np.stack((nrm[:, 0], nrm[:, 2], nrm[:, 1]), axis=1)
    return (pos.astype(np.float32), nrm.astype(np.float32),
            np.ascontiguousarray(uv, dtype=np.float32), np.ascontiguousarray(idx))


def quat_x(deg: float) -> tuple:
    a = math.radians(deg) * 0.5
    return (math.sin(a), 0.0, 0.0, math.cos(a))


def quat_y(deg: float) -> tuple:
    a = math.radians(deg) * 0.5
    return (0.0, math.sin(a), 0.0, math.cos(a))


def quat_z(deg: float) -> tuple:
    a = math.radians(deg) * 0.5
    return (0.0, 0.0, math.sin(a), math.cos(a))


# ---------------------------------------------------------------------------- the figure
# A gaunt 1.9 m hooded figure: narrow shoulders, long limbs, a peaked hood with a pale mask under it, a dark
# cloak behind, one dull red cloth tail in front. Asymmetry is the identity hook: the LEFT shoulder carries a
# big plate and the right has none.

PELVIS_Y = 0.92                      # hip height in m; the legs reach exactly this far down
PELVIS_U = PELVIS_Y * U_PER_M        # 14.72
LEG_U = PELVIS_U

# node name, parent, translation from the parent in u (iso axes: x, y forward, z up)
# The arm nodes sit INBOARD of the arms (ARM_OFFSET_U): HeldWeaponRenderer measures the fist 0.2 units
# outboard of the node in the arm's own frame, and the arm only ever rotates about X, so sliding the arm's
# geometry outboard of its pivot costs nothing and puts the grip on the hand.
ARM_NODE_X = 0.7
JOINTS = (
    ("root", None, (0.0, 0.0, 0.0)),
    ("torso", "root", (0.0, 0.0, PELVIS_U)),
    ("head", "torso", (0.0, 0.4, 9.6)),
    ("arm-left", "torso", (ARM_NODE_X, 0.0, 8.9)),
    ("arm-right", "torso", (-ARM_NODE_X, 0.0, 8.9)),
    ("leg-left", "root", (1.4, 0.0, PELVIS_U)),
    ("leg-right", "root", (-1.4, 0.0, PELVIS_U)),
)


def joint_world_u() -> dict:
    out = {}
    for name, parent, t in JOINTS:
        base = out[parent] if parent else np.zeros(3)
        out[name] = base + np.array(t, dtype=np.float64)
    return out


def _limb_arm(side: float):
    """Hangs from the shoulder, hand centre at -10.2 u (= -0.9 units, where the engine's grip is), and sits
    ARM_OFFSET_U outboard of its node."""
    sleeve = loft([(-9.4, 0.56, 0.56, 0.0, 0.1), (-7.2, 0.68, 0.68, 0.0, 0.25), (-5.0, 0.80, 0.80, 0.0, 0.35),
                   (-2.6, 0.95, 0.95, 0.0, 0.15), (0.4, 1.02, 1.02, 0.0, 0.0), (1.0, 0.70, 0.70, 0.0, 0.0)], 8)
    bracer = loft([(-9.0, 0.66, 0.66, 0.0, 0.12), (-7.8, 0.78, 0.78, 0.0, 0.18), (-5.4, 0.90, 0.90, 0.0, 0.30)], 8)
    hand = loft([(-11.4, 0.40, 0.28, 0.0, 0.15), (-10.7, 0.58, 0.40, 0.0, 0.18), (-9.6, 0.58, 0.46, 0.0, 0.14),
                 (-9.1, 0.50, 0.44, 0.0, 0.12)], 6)
    mv = (side * ARM_OFFSET_U, 0.0, 0.0)
    return [("cloth.cold", sleeve.xform(move=mv)), ("steel.cool", bracer.xform(move=mv)),
            ("flesh.grey", hand.xform(move=mv))]


def _limb_leg():
    trouser = loft([(-11.8, 0.76, 0.76, 0.0, 0.0), (-9.2, 0.92, 0.92, 0.0, -0.12), (-7.0, 0.86, 0.86, 0.0, 0.0),
                    (-3.6, 1.10, 1.10, 0.0, 0.0), (0.0, 1.25, 1.25, 0.0, 0.0), (0.4, 0.95, 0.95, 0.0, 0.0)], 8)
    boot = loft([(-LEG_U, 0.92, 1.45, 0.0, 0.70), (-14.0, 0.98, 1.50, 0.0, 0.70), (-13.0, 0.92, 1.10, 0.0, 0.50),
                 (-11.5, 0.90, 0.96, 0.0, 0.08), (-10.2, 0.94, 0.94, 0.0, 0.0)], 8)
    guard = loft([(-8.2, 0.98, 0.98, 0.0, 0.10), (-7.0, 1.08, 1.08, 0.0, 0.22), (-5.9, 0.99, 0.99, 0.0, 0.10)], 8)
    return [("cloth.ash.cold", trouser), ("leather", boot), ("steel.cool", guard)]


def build_parts():
    """-> list of (node_name, local mesh in u, material key). Geometry is local to its node's joint."""
    parts = []

    # torso, local origin at the pelvis: plate over a waist, belt, hip skirt, red tail, cloak, gorget,
    # backplate, left pauldron
    plate = loft([(0.0, 2.00, 1.35, 0.0, 0.0), (2.6, 1.75, 1.25, 0.0, 0.0), (5.2, 2.45, 1.65, 0.0, 0.1),
                  (7.6, 2.75, 1.70, 0.0, 0.2), (9.0, 2.35, 1.45, 0.0, 0.3), (9.7, 1.10, 0.95, 0.0, 0.35)], 8)
    belt = loft([(1.0, 2.10, 1.50, 0.0, 0.0), (2.3, 2.00, 1.45, 0.0, 0.0)], 10)
    skirt = loft([(-2.0, 2.95, 1.75, 0.0, 0.0), (-0.5, 2.75, 1.70, 0.0, 0.0), (1.3, 2.05, 1.45, 0.0, 0.0)], 10)
    tail = loft([(-4.6, 0.90, 0.30, 0.0, 2.15), (-2.5, 0.96, 0.33, 0.0, 2.00), (0.2, 1.02, 0.36, 0.0, 1.95),
                 (2.3, 1.06, 0.40, 0.0, 1.85)], 8)
    cloak = loft([(-5.4, 3.70, 0.90, 0.0, -3.20), (-1.0, 3.25, 0.95, 0.0, -2.75), (4.0, 2.95, 1.00, 0.0, -2.30),
                  (8.6, 2.90, 1.00, 0.0, -1.55), (9.5, 2.00, 0.80, 0.0, -1.00)], 12)
    backplate = loft([(3.2, 1.55, 0.50, 0.0, -3.35), (5.6, 1.95, 0.55, 0.0, -3.00), (8.2, 2.05, 0.55, 0.0, -2.45),
                      (8.9, 1.20, 0.40, 0.0, -2.20)], 8)
    gorget = loft([(8.6, 2.55, 2.05, 0.0, 0.2), (9.5, 2.45, 1.95, 0.0, 0.3), (10.4, 1.65, 1.45, 0.0, 0.4)], 8)
    pauldron = loft([(-0.5, 2.60, 2.10, 0.0, 0.0), (0.0, 2.90, 2.30, 0.0, 0.0), (0.4, 2.70, 2.10, 0.0, 0.0),
                     (1.0, 1.80, 1.50, 0.0, 0.0), (1.3, 0.80, 0.70, 0.0, 0.0)], 6)
    pauldron = pauldron.xform(rot_deg=(0.0, 22.0, 0.0), move=(4.1, 0.0, 9.3))
    parts += [("torso", plate, "steel.cool"), ("torso", belt, "leather"), ("torso", skirt, "cloth.ash.cold"),
              ("torso", tail, "cloth.red"), ("torso", cloak, "cloth.cold"), ("torso", backplate, "steel.cool"),
              ("torso", gorget, "steel.cool"), ("torso", pauldron, "steel.cool")]

    # head, local origin at the neck base: peaked hood, pale mask, two dark sockets
    hood = loft([(-0.8, 2.70, 2.20, 0.0, -0.2), (0.6, 2.00, 1.90, 0.0, 0.0), (2.2, 1.95, 2.15, 0.0, 0.0),
                 (3.8, 1.70, 2.00, 0.0, -0.2), (5.0, 1.15, 1.50, 0.0, -0.7), (5.7, 0.55, 0.90, 0.0, -1.4),
                 (6.1, 0.12, 0.30, 0.0, -2.1)], 10)
    mask = loft([(0.2, 0.60, 0.50, 0.0, 1.45), (1.2, 1.15, 0.90, 0.0, 1.50), (2.4, 1.25, 1.00, 0.0, 1.50),
                 (3.6, 1.00, 0.80, 0.0, 1.45), (4.3, 0.40, 0.40, 0.0, 1.30)], 8)
    parts += [("head", hood, "cloth.cold"), ("head", mask, "bone")]
    for sx in (1.0, -1.0):
        socket = loft([(-0.25, 0.30, 0.22, 0.0, 0.0), (0.0, 0.34, 0.26, 0.0, 0.0), (0.25, 0.30, 0.22, 0.0, 0.0)], 6)
        parts.append(("head", socket.xform(move=(sx * 0.58, 2.38, 2.95)), "cloth.cold"))

    for node, side in (("arm-left", 1.0), ("arm-right", -1.0)):
        for key, mesh in _limb_arm(side):
            parts.append((node, mesh, key))
    for node in ("leg-left", "leg-right"):
        for key, mesh in _limb_leg():
            parts.append((node, mesh, key))
    return parts


def world_meshes(parts):
    """Each part's mesh moved into bind-pose world space, in u."""
    wp = joint_world_u()
    return [m.xform(move=tuple(wp[node])) for node, m, _ in parts]


# ---------------------------------------------------------------------------- atlas

def encode_atlas(atlas_rgba: np.ndarray, lift: float = ALBEDO_LIFT):
    """Baked sRGB atlas -> (PNG bytes, baseColorFactor scale).

    What the engine does with a texture, VERIFIED in code: TextureDecoder.UploadTexture2D uploads plain
    PixelInternalFormat.Rgba (no sRGB internal format) and enemyFragment multiplies baseColorFactor by the
    raw sample, so the texel bytes ARE linear albedo in the shader - the same space the old flat
    baseColorFactor lived in. So this does not write sRGB bytes:
      1. decode the baked atlas to linear albedo,
      2. apply the engine lift ONCE (the factor path no longer carries it: the single material's factor is the
         texture's scale, not a colour),
      3. store linear / scale in the bytes and put `scale` in baseColorFactor, so the 8 bits span the actual
         range of the figure instead of 0..1 (dark iron would otherwise get 6 levels).
    A glTF viewer that treats the texture as sRGB shows this brighter than the engine does; the engine is the
    target."""
    cov = atlas_rgba[..., 3] > 0
    lin = U.srgb_to_linear(atlas_rgba[..., :3].astype(np.float32) / 255.0).astype(np.float64) * lift
    top = float(lin[cov].max())
    scale = min(1.0, math.ceil(top * 200.0) / 200.0)
    enc = np.zeros(atlas_rgba.shape, dtype=np.uint8)
    enc[..., :3] = np.round(np.clip(lin / scale, 0.0, 1.0) * 255.0).astype(np.uint8)
    enc[..., 3] = 255
    buf = io.BytesIO()
    Image.fromarray(enc, "RGBA").save(buf, format="PNG", compress_level=9)
    return buf.getvalue(), scale, enc


# ---------------------------------------------------------------------------- clips

def _clip(name, t, rot=None, trs=None, index=None) -> G.GltfAnimation:
    t = np.asarray(t, dtype=np.float32)
    rotations = {index[n]: (t, np.array(q, dtype=np.float32)) for n, q in (rot or {}).items()}
    translations = {index[n]: (t, np.array(v, dtype=np.float32)) for n, v in (trs or {}).items()}
    return G.GltfAnimation(name=name, rotations=rotations, translations=translations)


def clip_idle(n: dict) -> G.GltfAnimation:
    """Almost still: a slow breath in the torso and a faint sway in the arms."""
    t = [0.0, 0.9, 1.8]
    return _clip("idle", t, {
        "torso": [quat_x(1.2), quat_x(-0.8), quat_x(1.2)],
        "head": [quat_x(2.0), quat_x(3.4), quat_x(2.0)],
        "arm-left": [quat_x(2.5), quat_x(-1.5), quat_x(2.5)],
        "arm-right": [quat_x(-2.0), quat_x(2.0), quat_x(-2.0)],
    }, index=n)


def _gait(name, t, swing, arm_k, dip, lean, twist, n) -> G.GltfAnimation:
    rot = {
        "leg-left": [quat_x(a) for a in swing],
        "leg-right": [quat_x(-a) for a in swing],
        "arm-left": [quat_x(-a * arm_k) for a in swing],
        "arm-right": [quat_x(a * arm_k) for a in swing],
        "torso": [_mul(quat_x(lean), quat_y(twist * a / max(swing))) for a in swing],
        "head": [quat_x(-lean * 0.6) for _ in swing],
    }
    return _clip(name, t, rot, {"root": [[0.0, d, 0.0] for d in dip]}, index=n)


def _mul(a: tuple, b: tuple) -> tuple:
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return (aw * bx + ax * bw + ay * bz - az * by, aw * by - ax * bz + ay * bw + az * bx,
            aw * bz + ax * by - ay * bx + az * bw, aw * bw - ax * bx - ay * by - az * bz)


def clip_walk(n: dict) -> G.GltfAnimation:
    """A four-beat trudge: legs swing, arms counter-swing, the body dips twice per cycle. The torso holds a fixed
    lean and does not twist: a held arm rides the torso (PlayerPoseLayers), and a held item must not swing with the walk."""
    t = [0.0, 0.2, 0.4, 0.6, 0.8]
    return _gait("walk", t, [22.0, 0.0, -22.0, 0.0, 22.0], 0.75, [0.0, -0.018, 0.0, -0.018, 0.0], 3.0, 0.0, n)


def clip_sprint(n: dict) -> G.GltfAnimation:
    """A run: a half-second cycle, a long stride, a forward lean and a bounce (never below the ground)."""
    t = [0.0, 0.125, 0.25, 0.375, 0.5]
    return _gait("sprint", t, [38.0, 0.0, -38.0, 0.0, 38.0], 0.9, [0.0, 0.035, 0.0, 0.035, 0.0], 10.0, 8.0, n)


def clip_melee(n: dict) -> G.GltfAnimation:
    """0.42 s overhead chop: raise the right arm back and up, slam it forward and down, recover to rest."""
    t = [0.0, 0.09, 0.19, 0.28, 0.42]
    return _clip("attack-melee-right", t, {
        "arm-right": [quat_x(0.0), quat_x(-150.0), quat_x(-35.0), quat_x(-22.0), quat_x(0.0)],
        "arm-left": [quat_x(0.0), quat_x(25.0), quat_x(-10.0), quat_x(-6.0), quat_x(0.0)],
        "torso": [_mul(quat_x(0.0), quat_y(0.0)), _mul(quat_x(-6.0), quat_y(-14.0)), _mul(quat_x(14.0), quat_y(16.0)),
                  _mul(quat_x(10.0), quat_y(10.0)), _mul(quat_x(0.0), quat_y(0.0))],
        "head": [quat_x(0.0), quat_x(6.0), quat_x(-8.0), quat_x(-5.0), quat_x(0.0)],
    }, index=n)


def clip_shoot(n: dict) -> G.GltfAnimation:
    """0.2 s: the main arm is held raised by the engine, so only a small recoil and a lean."""
    t = [0.0, 0.05, 0.12, 0.20]
    return _clip("holding-right-shoot", t, {
        "arm-right": [quat_x(-70.0), quat_x(-62.0), quat_x(-72.0), quat_x(-70.0)],
        "torso": [quat_x(0.0), quat_x(-4.0), quat_x(1.0), quat_x(0.0)],
    }, index=n)


def clip_die(n: dict, lift: float) -> G.GltfAnimation:
    """1.3 s: stagger, then fall onto the back and stay there (the engine holds the last frame). The root pivots
    on the ground, so it is also lifted by the body's back-to-front thickness to rest ON the ground rather than
    in it; `lift` is that thickness in m."""
    t = [0.0, 0.25, 0.55, 0.9, 1.3]
    fall = [0.0, -14.0, -52.0, -86.0, -90.0]
    return _clip("die", t, {
        "root": [quat_x(a) for a in fall],
        "torso": [quat_x(0.0), quat_x(8.0), quat_x(-6.0), quat_x(-10.0), quat_x(-6.0)],
        "head": [quat_x(0.0), quat_x(-12.0), quat_x(-10.0), quat_x(-18.0), quat_x(-10.0)],
        "arm-left": [quat_x(0.0), quat_x(-30.0), quat_x(-60.0), quat_x(-40.0), quat_x(-25.0)],
        "arm-right": [quat_x(0.0), quat_x(-50.0), quat_x(-20.0), quat_x(-40.0), quat_x(-25.0)],
        "leg-left": [quat_x(0.0), quat_x(10.0), quat_x(-20.0), quat_x(-8.0), quat_x(-4.0)],
        "leg-right": [quat_x(0.0), quat_x(-12.0), quat_x(14.0), quat_x(6.0), quat_x(8.0)],
    }, {"root": [[0.0, lift * math.sin(math.radians(-a)), 0.0] for a in fall]}, index=n)


# ---------------------------------------------------------------------------- build

def build_asset():
    """-> dict with nodes, primitives, materials, animations, the atlas and a stats blurb. Pure function of the
    source: nothing here reads the clock, the environment or the filesystem."""
    parts = build_parts()
    wmeshes = world_meshes(parts)
    meshes, uvs, rects = C.unwrap_split(wmeshes, ATLAS_SIZE, axis=UNWRAP_AXIS)
    mats = [bake_material(key) for _, _, key in parts]
    atlas = C.bake_albedo(meshes, None, None, uvs, rects, ATLAS_SIZE, materials=mats, seed="player_grim")
    png, scale, enc = encode_atlas(atlas)

    names = [j[0] for j in JOINTS]
    index = {name: i for i, name in enumerate(names)}
    nodes = []
    for name, parent, t in JOINTS:
        nodes.append(G.GltfNode(name=name, parent=None if parent is None else index[parent],
                                translation=(t[0] * UNITS_PER_U, t[2] * UNITS_PER_U, t[1] * UNITS_PER_U)))

    wp = joint_world_u()
    primitives, back = [], 0.0
    for (node, _, _), wm, uv in zip(parts, meshes, uvs):
        local = wm.xform(move=tuple(-wp[node]))
        pos, nrm, uvo, idx = _render_arrays(local, uv)
        primitives.append(G.GltfPrimitive(positions=pos, normals=nrm, uvs=uvo, indices=idx, joints=None,
                                          weights=None, material=0, node=index[node]))
        back = max(back, float(-(wm.v[:, 1].min()) * UNITS_PER_U))   # how far behind the pelvis axis the body reaches
    materials = [{"name": "grim.actor", "base_color_factor": (scale, scale, scale, 1.0), "base_color_png": png,
                  "metallic": 0.0, "roughness": 0.9}]
    animations = [clip_idle(index), clip_walk(index), clip_sprint(index), clip_die(index, back),
                  clip_melee(index), clip_shoot(index)]
    return dict(nodes=nodes, primitives=primitives, materials=materials, animations=animations,
                atlas=atlas, encoded=enc, scale=scale, rects=rects, parts=parts, meshes=meshes, uvs=uvs)


def main(out_dir: str | None = None) -> Path:
    out = Path(out_dir) if out_dir else HERE / "char"
    out.mkdir(parents=True, exist_ok=True)
    a = build_asset()
    path = out / "player_grim.glb"
    G.write_glb(str(path), a["nodes"], a["primitives"], None, None, a["animations"], a["materials"])

    tris = sum(len(p.indices) for p in a["primitives"])
    verts = sum(len(p.positions) for p in a["primitives"])
    data = path.read_bytes()
    print(f"gen_char_player: {path}")
    print(f"  parts={len(a['parts'])} nodes={len(a['nodes'])} verts={verts} tris={tris} "
          f"clips={[x.name for x in a['animations']]}")
    print(f"  atlas={ATLAS_SIZE}px lift={ALBEDO_LIFT} baseColorFactor scale={a['scale']:.3f}")
    print(f"  size={len(data)} bytes sha1={hashlib.sha1(data).hexdigest()}")
    return path


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else None)
