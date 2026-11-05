"""Grim player actor: original procedural geometry exported as a rigid-part animated GLB.

    python tools/ui-assets/gen_char_player.py [out_dir]

Writes <out_dir>/player_grim.glb (default: tools/ui-assets/char/). Never writes dream/ anything; the shipped
copy is dream/animations/players/player-grim.glb, which is this file under the name the engine's
PlayerModelAssetDefinition loads.

Shape: a rigid part hierarchy, which is how dream's own player model is built - one mesh per part node, no
skin, animation driven by node rotation. A node may carry several primitives (one per material), so a
tunic skirt or a tail rides the torso node and takes the engine's hit flinch with it: PlayerPoseLayers only
overrides the torso, head and arm nodes, so a child node of the torso would be left behind in a flinch.
The node names torso / head / arm-left / arm-right / leg-left / leg-right are the engine's contract.

Authored in u (16 u = 1 m, the isoforge scale), iso axes (x right, y forward, z up), then exported in metres,
Y up, +Z forward, matching PlayerModelAssetDefinition.AuthoredForwardAxis. The engine measures the authored
height at runtime (WorldAssetBounds.MeasureDimensions), so the figure needs no C# constant to change.

Texture: one baked albedo atlas (charforge.unwrap_split + bake_albedo) on material 0. The figure ships TWO
materials: that textured atlas, and one untextured emissive accent (material 1) carrying only an
emissiveFactor - emissive is added after dreamExpose, so it bypasses the actor gain entirely and has to stay
dim. How the engine reads the atlas matters and is stated at encode_atlas().

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
    "cloth.cold": ("#161A28", 0.92, 0.030, 0.28, None),
    # The breeches (was #1E2024): near-black, so the pale tunic over them is the figure's value contrast (see
    # cloth.tunic). Warm on purpose: a cool near-black such as #0D0F12 quantises into the palette gate's
    # forbidden cyan band (1367 texels) where this one makes none.
    "cloth.ash.cold": ("#101010", 0.94, 0.028, 0.24, None),
    # The traveller's tunic, sleeves and collar: dirty oatmeal, a soiled greige, the pale element of a figure that
    # no longer has steel to carry its value range. It is NOT a bright linen. It was #9E9A8C (value 0.62) and
    # that read as a lab coat: the atlas is lifted 3x (ALBEDO_LIFT) and the shader then multiplies by a gain of up
    # to ~2.2 near a brazier with no tonemap, so a texel above linear 0.153 (sRGB #6E) before the lift is
    # fully clipped at the peak, and #9E9A8C clipped 12% of the tunic at azimuth 35 and 31% at 145 (it is the 1.0000
    # p90 at 145). The hex is now chosen so the lit tunic stays under 1.0 at the strongest light, which is what
    # check_char.py's clipping gate enforces; the value contrast against the near-black breeches is what keeps the
    # figure legible, and it is a mid value, not a white one. Muted and warm (saturation ~0.17), never a clean white.
    "cloth.tunic": ("#645E4F", 0.92, 0.030, 0.22, None),
    # Boot leather and belt, darkened from grim.leather #241C16: the dark end of the histogram is as much a
    # part of the spread as the light end, and the boots are the figure's largest dark area.
    "leather.dark": ("#0F0F0E", 0.84, 0.035, 0.26, None),
    # The eye sockets' own surface. It is NOT baked into the atlas (it rides its own emissive material), but
    # bake_material has to answer for it anyway: the preview sheet renders every part build_parts returns.
    "ember.eye": ("#160A06", 0.95, 0.025, 0.30, None),
}
MATERIALS = {
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
UNWRAP_AXIS = (0.0, 0.0, 1.0)   # a loft is built along iso z, so project around z, not around the longest extent

# One dull ember behind the mask. Emissive is added AFTER dreamExpose, so it bypasses the gain entirely and a
# generous factor would be the brightest thing in a night frame; this is the dimmest glow that still reads, and
# the two sockets together cover about half a percent of the silhouette. Linear, as gen_props' coals are.
EMBER_EYE = (0.22, 0.055, 0.020)
EMBER_BASE = (0.012, 0.008, 0.006)      # the flat baseColorFactor under the glow: all but black
EMISSIVE_KEYS = ("ember.eye",)


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


# Radial schedule for a plate's broad faces, outermost first (1.0 = the outline, 0.0 = the centre, implicit).
# Not uniform, and that is the point. bake_albedo evaluates occlusion and curvature PER VERTEX and interpolates
# them across the triangle. A plate's outline vertices are shared with its rim and its underside, so their
# welded normal tilts outward and the plate's own rim occludes them: those vertices are legitimately dark. With
# evenly spaced rings that darkness interpolates across most of the face - at two rings it covers three quarters
# of the AREA. Crowding the first ring in to 0.9 of the outline confines it to the outer 19% and leaves the rest
# of the face at the occlusion it actually has, which for an up-facing plate in clear air is about 0.9.
# MEASURED: evenly spaced, the pauldron's top baked at a linear albedo of 0.27; on this schedule, 0.44.
PLATE_RINGS = (1.0, 0.50)


def _disc(o: np.ndarray, c: np.ndarray, z: float, rings: int):
    """One face of a plate: concentric copies of the outline shrinking to a centre vertex, triangulated.
    `rings` is how many of PLATE_RINGS to use (the centre vertex is always added)."""
    cols = [c + (o - c) * k for k in PLATE_RINGS[:rings]]
    pts = np.vstack([np.column_stack((v, np.full(len(v), z))) for v in cols]
                    + [np.array([[c[0], c[1], z]])])
    n = len(o)
    f = []
    for r in range(rings - 1):
        a0, b0 = r * n, (r + 1) * n
        for k in range(n):
            k2 = (k + 1) % n
            f += [(a0 + k, a0 + k2, b0 + k2), (a0 + k, b0 + k2, b0 + k)]
    last, centre = (rings - 1) * n, rings * n
    f += [(last + k, last + (k + 1) % n, centre) for k in range(n)]
    return pts, f


def plate(outline, thick: float, taper: float = 0.70, rings: int | None = None,
          bevel: float = 0.0, bevel_drop: float = 1.0) -> I.Mesh:
    """A flat slab: the convex polygon `outline` ((x, y) in u, in order) at z = +thick/2, and the same polygon
    shrunk about its centroid by `taper` at z = -thick/2, closed by a rim.

    Flat-faceted, and that is the entire point. The top is ONE plane however many triangles it takes, so every
    pixel of it falls in the same N.L bucket: a plate puts a SPIKE in the figure's lambert histogram where a
    6-12 sided elliptical loft puts a smooth ramp, and p90-p10 of the rendered value is a histogram statistic.
    MEASURED: the figure's lambert histogram before the plates was 64% at N.L < 0.1 and a flat 4-6% smear
    across every bucket above it; the spread that produced was 0.2901 against M3's floor of 0.50.

    `taper` under 1 tucks the underside in, so the plate overhangs what it is mounted on. The engine has no
    shadowing, so an undercut is not a shadow - it is a surface whose normal points down and inward, which
    takes the point light's 0.25 wrap term alone and nothing else. That is the dark half of the histogram.

    `bevel` (a fraction of the half-extent) puts a narrow secondary facet around the outer rim, dropped by
    `bevel_drop` of the thickness, so the broad face stops short of the outline and a chamfer carries the edge
    down and outward. That is a SILHOUETTE fix, not a lighting one: a broad pale slab with square corners reads
    as a crate at this camera distance rather than as plate. It deliberately does NOT round the face - the
    coplanar face is what earns M3 - it only takes a strip off its edge and gives that strip its own N.L bucket,
    which lands in the dark half. Applied to the big shoulder plates only, where the box-read showed.

    `bevel_drop` is how far down the chamfer ring sits, as a fraction of the thickness from the broad face. It
    has to be STEEP and the measurement says so: a shallow chamfer (0.45) faces down-and-outward at about 39
    degrees, which still catches enough light to render mid-grey, and mid-grey pixels dilute both tails - that
    setting cost 0.038 of M3. At 1.0 the chamfer runs the full thickness, reads at about 61 degrees, and falls
    into the wrap term with the rest of the dark half; the measured cost is 0.016."""
    o = np.asarray(outline, dtype=np.float64)
    if len(o) < 3:
        raise ValueError("a plate needs at least three outline points")
    n = len(o)
    c = o.mean(axis=0)
    rings = len(PLATE_RINGS) if rings is None else rings
    face = c + (o - c) * (1.0 - bevel) if bevel > 0.0 else o
    tv, tf = _disc(face, c, thick * 0.5, rings)
    bv, bf = _disc(c + (o - c) * taper, c, -thick * 0.5, rings)
    v = np.vstack((tv, bv))
    off = len(tv)
    chamfer = []
    if bevel > 0.0:
        # one ring on the outline itself, between the broad face and the rim
        ring = len(v)
        v = np.vstack((v, np.column_stack((o, np.full(n, thick * (0.5 - bevel_drop))))))
        for k in range(n):
            a, b = k, (k + 1) % n
            chamfer += [(a, ring + a, ring + b), (a, ring + b, b)]
        off_rim = ring   # the rim now starts from the chamfer ring, not from the broad face
    else:
        off_rim = 0
    # Triangle ORDER matters and is load-bearing. A slab's thin axis makes charforge project it planar, and a
    # planar projection maps the two broad faces onto the SAME atlas rectangle; bake_albedo resolves an overlap
    # by letting the last triangle win. So the underside goes first, then the rim, then the top: the face the
    # camera actually sees is the one that owns the texels. MEASURED with the order the other way round: the
    # pauldron baked at a linear albedo of 0.08 (its own shaded underside) instead of 0.45.
    f = [(a + off, b + off, d + off) for a, b, d in bf]
    for k in range(n):                              # the rim, between the outermost top ring and the underside
        a, b = off_rim + k, off_rim + (k + 1) % n
        f += [(a, off + k, off + (k + 1) % n), (a, off + (k + 1) % n, b)]
    f += chamfer
    f += list(tf)
    f = np.array(f, dtype=np.int32)
    return I.Mesh(v, I._orient_outward(v, f, np.array((c[0], c[1], 0.0))))


def _oct(hx: float, hy: float, cut: float = 0.34, front: float = 1.0, taper_x: float = 1.0):
    """A chamfered rectangle 2hx by 2hy with its corners cut by `cut` of the half-extent: eight points, convex,
    in order. `front` scales the +y half (a trapezoid), `taper_x` the +y end's width."""
    cx, cy = hx * cut, hy * cut
    fy, fx = hy * front, hx * taper_x
    return [(-hx + cx, -hy), (hx - cx, -hy), (hx, -hy + cy), (fx, fy - cy),
            (fx - cx, fy), (-fx + cx, fy), (-fx, fy - cy), (-hx, -hy + cy)]


def unwrap_axes(meshes):
    """One projection spec per part for charforge.unwrap_split: iso z for a loft, which is the axis it was
    built along, and an explicit planar unwrap for a flat-faceted plate. Forcing a cylinder about z on a
    horizontal plate would collapse its rectangle to the slab's thickness, a couple of texels tall; letting PCA
    choose leaves it one authoring tweak away from flipping to a cylinder about its own long axis, which is the
    one projection the seam splitter cannot fully open."""
    return ["planar" if m.n is None else UNWRAP_AXIS for m in meshes]


def split_parts(parts):
    """(baked, emissive): the parts that go in the albedo atlas, and the ones that ride their own flat emissive
    material. Keeps build_parts a single list, so the preview sheet still sees the whole figure."""
    baked = [p for p in parts if p[2] not in EMISSIVE_KEYS]
    emissive = [p for p in parts if p[2] in EMISSIVE_KEYS]
    return baked, emissive


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
# A gaunt 1.9 m unarmoured traveller: narrow shoulders, long limbs, a bare grey head with two dim ember eyes, an
# undyed wool tunic, dark breeches and boots, one dull red cloth tail in front. Every piece of armour is a worn
# model in the equipment layer (ArmourMeshCatalog); nothing here is baked plate, hood or cloak.

PELVIS_Y = 0.92                      # hip height in m; the legs reach exactly this far down
PELVIS_U = PELVIS_Y * U_PER_M        # 14.72
LEG_U = PELVIS_U
SKIRT_HEM_U = -5.0                   # the tunic skirt hangs this far below the pelvis, u (was -3.0): more pale area below the cuirass

# node name, parent, translation from the parent in u (iso axes: x, y forward, z up)
# The arm nodes sit INBOARD of the arms (ARM_OFFSET_U): HeldWeaponRenderer measures the fist 0.2 units
# outboard of the node in the arm's own frame, and the arm only ever rotates about X, so sliding the arm's
# geometry outboard of its pivot costs nothing and puts the grip on the hand.
ARM_NODE_X = 0.7
JOINTS = (
    ("root", None, (0.0, 0.0, 0.0)),
    ("torso", "root", (0.0, 0.0, PELVIS_U)),
    ("head", "torso", (0.0, 0.4, 10.97)),
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
    ARM_OFFSET_U outboard of its node. A loose tunic sleeve over a bare hand: no bracer, no couter."""
    sleeve = loft([(-9.4, 0.58, 0.58, 0.0, 0.1), (-7.0, 0.95, 0.95, 0.0, 0.15), (-4.6, 1.05, 1.05, 0.0, 0.15),
                   (-2.6, 1.05, 1.05, 0.0, 0.15), (0.4, 1.05, 1.05, 0.0, 0.0), (1.0, 0.70, 0.70, 0.0, 0.0)], 8)
    hand = loft([(-11.4, 0.40, 0.28, 0.0, 0.15), (-10.7, 0.58, 0.40, 0.0, 0.18), (-9.6, 0.58, 0.46, 0.0, 0.14),
                 (-9.1, 0.50, 0.44, 0.0, 0.12)], 6)
    mv = (side * ARM_OFFSET_U, 0.0, 0.0)
    return [("cloth.tunic", sleeve.xform(move=mv)), ("flesh.grey", hand.xform(move=mv))]


def _limb_leg():
    """Breeches into a turned boot: no shin guard, knee cop, sabaton or cuisse. The boot's cuff is the widest
    thing in the foot row (1.4 u = 0.123 units) and its footprint is held inside the contract's z -0.05..+0.15."""
    trouser = loft([(-11.8, 0.76, 0.76, 0.0, 0.25), (-10.0, 0.84, 0.84, 0.0, 0.25), (-9.2, 0.92, 0.92, 0.0, -0.12), (-7.0, 0.86, 0.86, 0.0, 0.0),
                    (-3.6, 1.10, 1.10, 0.0, 0.0), (-1.2, 1.23, 1.23, 0.0, 0.0), (0.0, 1.25, 1.25, 0.0, 0.0), (0.4, 0.95, 0.95, 0.0, 0.0)], 8)
    boot = loft([(-LEG_U, 1.05, 1.12, 0.0, 0.56), (-14.1, 1.10, 1.15, 0.0, 0.56), (-13.0, 1.00, 1.05, 0.0, 0.50),
                 (-11.5, 1.05, 1.00, 0.0, 0.45), (-10.5, 1.40, 1.00, 0.0, 0.45), (-10.0, 1.10, 0.85, 0.0, 0.25)], 8)
    return [("cloth.ash.cold", trouser), ("leather.dark", boot)]


def build_parts():
    """-> list of (node_name, local mesh in u, material key). Geometry is local to its node's joint.

    THE UNARMOURED TRAVELLER. This is the underlayer the worn armour (ArmourMeshCatalog) goes over: a tunic,
    breeches, boots, a bare head and bare hands. Nothing here reads as armour - no plate, no helm, no hood, no
    pauldron, no cloak - so an unequipped character is visibly unarmoured and equipping a piece changes the
    figure from bare to armoured rather than from one shape to another. Unarmoured is not nude.

    ENVELOPE TABLE (engine units = u * UNITS_PER_U, each node's own frame; the contract the worn models are
    authored against. The figure may stand SHORT of a row by up to 0.015 but never past it
    (check_char.py's envelope gate is one-sided outward, so a re-tuned tunic cannot poke out of a worn piece
    that covers the table value); the measured values are read back from the built GLB by
    tools/ui-assets/tests/check_char.py's envelope gate and recorded in
    docs/ledger/work-log/2026-11-06-undressed-base-figure.md):

        mount   region                                       base half-width
        Head    y 0.00..0.42, |x|<=0.12, |z|<=0.135          0.12
        Torso   y 0.00..0.86, |x|<=0.26 (hip skirt; chest    0.26
                0.195), z -0.145..+0.15
        Arm     (one arm, own axis) y -0.95..-0.55,          0.09
                |x|<=0.09, z -0.075..+0.10
        Leg     (one leg) y -0.75..-0.10, |x|<=0.11,         0.11
                |z|<=0.11
        Foot    (one leg) y -1.30..-0.88, |x|<=0.132,        0.132
                z -0.055..+0.155
        Back    torso back surface at z >= -0.145             0.26

    The head node sits 1.37 u higher than the dressed figure's (10.97 u over the pelvis; it was 9.6 u under a
    hood): with a bare 0.42 skull the authored top is held at 2.68 only by lengthening the neck, and doing it
    in the node keeps the contract's head region (y 0.00 at the chin) true. The neck and a short cowl collar
    bridge the gap. Two rules from the scale: the actor is 28x101 px, 1 px = 0.0265 units, so nothing here is
    thinner than ~0.05 units and there is no face, hair, lace or stitching - the visible payoff is the head's,
    the torso's and the cloak's width.
    """
    parts = []

    # torso, local origin at the pelvis: cloth tunic body (belt to collar), wide belt, tunic skirt, red tail.
    tunic = loft([(1.0, 2.35, 1.48, 0.0, 0.0), (2.6, 2.15, 1.42, 0.0, 0.0), (5.2, 2.05, 1.40, 0.0, 0.05),
                  (7.6, 2.16, 1.45, 0.0, 0.10), (9.0, 2.00, 1.35, 0.0, 0.10), (9.75, 1.20, 1.00, 0.0, 0.10)], 8)
    belt = loft([(0.9, 2.50, 1.58, 0.0, 0.0), (2.4, 2.45, 1.57, 0.0, 0.0)], 10)
    skirt = loft([(SKIRT_HEM_U, 2.90, 1.62, 0.0, 0.0), (-1.0, 3.00, 1.68, 0.0, 0.0), (0.0, 2.95, 1.68, 0.0, 0.0),
                  (1.2, 2.45, 1.55, 0.0, 0.0)], 10)
    tail = loft([(-3.4, 0.86, 0.30, 0.0, 1.40), (-1.8, 0.92, 0.33, 0.0, 1.38), (0.2, 1.00, 0.36, 0.0, 1.34),
                 (2.3, 1.04, 0.40, 0.0, 1.30)], 8)
    parts += [("torso", tunic, "cloth.tunic"), ("torso", belt, "leather.dark"), ("torso", skirt, "cloth.tunic"),
              ("torso", tail, "cloth.red")]

    # head, local origin at the chin: a bare skull-and-jaw loft in grey flesh, a neck, a short cowl collar and
    # the two ember sockets. y 0.00..4.77 u (0.42 units), |x| 1.36 u (0.12), |z| 1.48 u (0.13).
    skull = loft([(0.0, 0.75, 0.85, 0.0, 0.35), (0.9, 1.15, 1.20, 0.0, 0.15), (2.0, 1.34, 1.42, 0.0, 0.0),
                  (3.2, 1.36, 1.46, 0.0, -0.02), (4.1, 1.18, 1.28, 0.0, -0.08), (4.45, 0.95, 1.05, 0.0, -0.10),
                  (4.77, 0.45, 0.55, 0.0, -0.12)], 8)
    neck = loft([(-2.0, 0.66, 0.62, 0.0, 0.05), (0.4, 0.64, 0.60, 0.0, 0.15)], 8)
    collar = loft([(-1.7, 1.40, 1.20, 0.0, 0.05), (-0.7, 1.15, 1.02, 0.0, 0.10), (0.1, 0.90, 0.84, 0.0, 0.15)], 8)
    parts += [("head", neck, "flesh.grey"), ("head", collar, "cloth.tunic"), ("head", skull, "flesh.grey")]
    for sx in (1.0, -1.0):
        socket = loft([(-0.25, 0.30, 0.22, 0.0, 0.0), (0.0, 0.34, 0.26, 0.0, 0.0), (0.25, 0.30, 0.22, 0.0, 0.0)], 6)
        parts.append(("head", socket.xform(move=(sx * 0.50, 1.30, 2.70)), "ember.eye"))

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


def _gait(name, t, swing, arm_k, dip, lean, twist, n, leg_yaw: float = 0.0) -> G.GltfAnimation:
    """`leg_yaw` turns the whole stride about the model's vertical axis before the swing, which is how the
    directional clips carry their share of the travel-minus-facing angle (C2). quat_y(0) is exactly identity
    and _mul by identity is bit-exact, so the existing clips' bytes do not move."""
    y = quat_y(leg_yaw)
    rot = {
        "leg-left": [_mul(y, quat_x(a)) for a in swing],
        "leg-right": [_mul(y, quat_x(-a)) for a in swing],
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


def clip_walk_back(n: dict) -> G.GltfAnimation:
    """Backpedalling, C2's `walk-back`. The clip carries the WHOLE half turn of the stride and
    PlayerAnimationFrame.LegTwistRadians carries only the residual (delta -+ 180 degrees, inside the new
    pi/4 clamp). The clip and the state machine must not both apply it or the legs double-count: that is the
    one way this clip can be wrong and still look plausible from the front.

    Shorter stride than the forward walk, a slight backward lean, and the arms stay in the FACING frame - the
    pose layers twist the legs only, so an arm that swung with the travel direction would fight a held item."""
    t = [0.0, 0.22, 0.44, 0.66, 0.88]
    return _gait("walk-back", t, [15.0, 0.0, -15.0, 0.0, 15.0], 0.45, [0.0, -0.014, 0.0, -0.014, 0.0],
                 -3.5, 0.0, n, leg_yaw=180.0)


def clip_strafe(n: dict, side: float) -> G.GltfAnimation:
    """A sidestep, C2's `walk-strafe-right` (side > 0) and `walk-strafe-left` (side < 0). The clip carries
    exactly the +-90 degrees the selection table subtracts, in the same sense as PlayerPoseLayers' leg twist
    (a yaw about the model's vertical axis, +Z turning towards +X), so clip plus residual equals the raw delta.

    The legs scissor sideways because the swing axis is yawed with them; the arms barely move and the torso
    leans a couple of degrees into the step."""
    name = "walk-strafe-right" if side > 0 else "walk-strafe-left"
    t = [0.0, 0.21, 0.42, 0.63, 0.84]
    return _gait(name, t, [17.0, 0.0, -17.0, 0.0, 17.0], 0.22, [0.0, -0.015, 0.0, -0.015, 0.0],
                 1.5, 0.0, n, leg_yaw=90.0 * side)


def clip_run(n: dict) -> G.GltfAnimation:
    """The cruise gait, between the trudge and the sprint: a 0.62 s cycle, a long-but-controlled stride, the arms
    driven harder than the walk's and a modest lean. This is the clip the player spends almost all of its time in
    (the default ground speed is the run tier), so it is authored as a gait that can be watched for minutes, not
    as a flat-out effort."""
    t = [0.0, 0.155, 0.31, 0.465, 0.62]
    return _gait("run", t, [30.0, 0.0, -30.0, 0.0, 30.0], 0.85, [0.0, 0.026, 0.0, 0.026, 0.0], 6.5, 4.0, n)


def clip_sprint(n: dict) -> G.GltfAnimation:
    """A true sprint, now that `run` holds the cruise: a 0.44 s cycle, a very long stride, the arms pumping almost
    one-for-one with the legs, a hard forward lean and a real bounce (never below the ground)."""
    t = [0.0, 0.11, 0.22, 0.33, 0.44]
    return _gait("sprint", t, [42.0, 0.0, -42.0, 0.0, 42.0], 0.95, [0.0, 0.040, 0.0, 0.040, 0.0], 12.0, 9.0, n)


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


def clip_die(n: dict, lift) -> G.GltfAnimation:
    """1.3 s: stagger, then fall onto the back and stay there (the engine holds the last frame). The root pivots
    on the ground, so it is also lifted by the body's back-to-front thickness to rest ON the ground rather than
    in it; `lift` is that thickness in m, one number for every key or a per-key list (see _die_lift)."""
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
    }, {"root": [[0.0, (lift[k] if isinstance(lift, (list, tuple)) else lift) * math.sin(math.radians(-a)), 0.0]
                 for k, a in enumerate(fall)]}, index=n)


# ---------------------------------------------------------------------------- build

def _die_lift(nodes, primitives, index: dict, back: float) -> list:
    """Per key, how far the die clip must lift the root so no vertex of the falling body goes under the ground.

    `back` (how far behind the pelvis axis the body reaches) is the floor of every key. Above it each key needs
    its own number: the root's lift is linear (lift * sin of the fall angle), so the lowest vertex of a key at
    zero lift gives the lift THAT key needs. This used to return the maximum over the keys, and clip_die spent
    that one number on every key, so only the binding key landed on the ground and the held last frame hovered
    (measured 0.0414 units, 1.5 px, with the shipped 0.3218). What binds a key differs: at mid-fall (52 degrees)
    it is the swinging right leg, not the head; the head only sets the final key, where the fall tips it back a
    further 10 to 18 degrees on a long neck. A reader tuning the head cannot move the mid-fall number."""
    import enginelook as L
    base = clip_die(index, 0.0)
    unit = clip_die(index, 1.0)
    out = []
    for k, v in enumerate(unit.translations[index["root"]][1]):
        s = float(v[1])
        need = back
        if s >= 0.05:
            world = L.node_worlds(nodes, base, k)
            low = min(float((np.asarray(p.positions, dtype=np.float64) @ world[p.node][:3, :3].T
                             + world[p.node][:3, 3])[:, 1].min()) for p in primitives)
            if low < 0.0:
                need = max(need, -low / s)
        out.append(need)
    return out


def build_asset():
    """-> dict with nodes, primitives, materials, animations, the atlas and a stats blurb. Pure function of the
    source: nothing here reads the clock, the environment or the filesystem."""
    baked, emissive = split_parts(build_parts())
    wmeshes = world_meshes(baked)
    meshes, uvs, rects = C.unwrap_split(wmeshes, ATLAS_SIZE, axis=unwrap_axes(wmeshes))
    mats = [bake_material(key) for _, _, key in baked]
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
    for (node, _, _), wm, uv in zip(baked, meshes, uvs):
        local = wm.xform(move=tuple(-wp[node]))
        pos, nrm, uvo, idx = _render_arrays(local, uv)
        primitives.append(G.GltfPrimitive(positions=pos, normals=nrm, uvs=uvo, indices=idx, joints=None,
                                          weights=None, material=0, node=index[node]))
        back = max(back, float(-(wm.v[:, 1].min()) * UNITS_PER_U))   # how far behind the pelvis axis the body reaches
    materials = [{"name": "grim.actor", "base_color_factor": (scale, scale, scale, 1.0), "base_color_png": png,
                  "metallic": 0.0, "roughness": 0.9}]
    if emissive:
        # One extra material, flat and untextured, carrying the emissive factor: the pattern gen_props uses for
        # the brazier's coals. No emissive TEXTURE - the factor path is already wired in both the writer and the
        # shader, and a texture would only let the glow vary where it has no business varying.
        materials.append({"name": "grim.actor.ember", "base_color_factor": EMBER_BASE + (1.0,),
                          "metallic": 0.0, "roughness": 0.9, "emissive_factor": EMBER_EYE})
        for node, m, _ in emissive:
            local = m.xform(move=tuple(-wp[node]))
            pos, nrm, uvo, idx = _render_arrays(local, np.zeros((len(local.v), 2), dtype=np.float32))
            primitives.append(G.GltfPrimitive(positions=pos, normals=nrm, uvs=uvo, indices=idx, joints=None,
                                              weights=None, material=len(materials) - 1, node=index[node]))
    lift = _die_lift(nodes, primitives, index, back)
    animations = [clip_idle(index), clip_walk(index), clip_run(index), clip_sprint(index), clip_die(index, lift),
                  clip_melee(index), clip_shoot(index), clip_walk_back(index), clip_strafe(index, 1.0),
                  clip_strafe(index, -1.0)]
    return dict(nodes=nodes, primitives=primitives, materials=materials, animations=animations,
                atlas=atlas, encoded=enc, scale=scale, rects=rects, parts=baked, emissive=emissive,
                meshes=meshes, uvs=uvs)


def main(out_dir: str | None = None, filename: str | None = None) -> Path:
    out = Path(out_dir) if out_dir else HERE / "char"
    out.mkdir(parents=True, exist_ok=True)
    a = build_asset()
    # build_all.py passes the shipped name, which the engine's PlayerModelAssetDefinition loads; run by hand the
    # generator keeps writing its own working copy under char/.
    path = out / (filename or "player_grim.glb")
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
