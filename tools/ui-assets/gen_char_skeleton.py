"""The risen skeleton: a skinned biped enemy, exported as an animated GLB.

    python tools/ui-assets/gen_char_skeleton.py [out_dir]

Writes <out_dir>/skeleton.glb (default: dream/animations/enemies/, beside the hound). It is the hound's sibling and
follows gen_char_beast.py step for step: ONE skin, 18 joints against contract C3's 32 (the engine's hard ceiling is
64, above which LoadSkinning silently returns null and the mesh draws in bind pose), every primitive carrying
JOINTS_0 and WEIGHTS_0, inverseBindMatrices in exported space, bare clip names: idle, walk, attack, die.

Skinned, so it can carry no pose-layer contract (node overrides resolve by renderable node name). That is why the
notched blade is GEOMETRY, fused into the right hand and bound to its joint, exactly as the hound's collar is.

`walk` is also the enemy's sprint clip (one gait), so it is authored to still read at the playback ceiling of 1.6.
`die` runs once and the engine holds its final frame: that pose is the corpse, with the root dropped so it lies ON
the ground.

Authoring lives in charforge's world (u, 16 u = 1 m, iso axes: x right, y forward, z up, origin at the foot point)
and every clip rotation is converted to a glTF quaternion numerically (R_gltf = SWAP . R_iso . SWAP).

Legibility: the actor is about 28x101 px on screen. Detail on a thin axis never reaches it, so the bright reads
are broad up-facing plates (gen_char_player.plate): the brow, the shoulder girdle, the pauldron, the hip crests and
the blade's flat. Colours are albedo with ~2.17x gain and no tonemap, so every material stays dim.
"""
from __future__ import annotations

import hashlib
import math
import sys
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
if str(HERE) not in sys.path:
    sys.path.insert(0, str(HERE))

import charforge as C  # noqa: E402
import gen_char_beast as B  # noqa: E402
import gen_char_player as P  # noqa: E402
import gltfwrite as G  # noqa: E402
import isoforge as I  # noqa: E402

UNITS_PER_U = P.UNITS_PER_U
ATLAS_SIZE = 256
MAX_JOINTS = 32                     # contract C3
DEFAULT_OUT = HERE.parents[1] / "dream" / "animations" / "enemies"
OUT_NAME = "skeleton.glb"
AGE = (0.55, 0.70, 0.20, 0.30)
SWAP = B.SWAP
_quat_from_iso = B._quat_from_iso

PELVIS_U = 14.0
DIE_DROP = -9.0   # root z of the corpse: tuned so the posed mesh lies on z = 0 (check_actors.py measures it)

MATS = {
    "bone": C._mat("#6B6353", 0.82, 0.030, 0.24, wear=0.6),
    "bone.hi": C._mat("#7C725E", 0.80, 0.030, 0.24, wear=0.5),
    "bone.old": C._mat("#443C31", 0.86, 0.030, 0.24, wear=0.7),
    "cavity": C._mat("#1B1816", 0.94, 0.030, 0.18, wear=0.5),
    # warm-neutral (red above blue): a blue-leaning dark iron quantises into a forbidden magenta hue in the atlas
    "iron": C._mat("#463E38", 0.72, 0.030, 0.24, metal=True, wear=0.9),
    "rust": C._mat("#3A3028", 0.80, 0.030, 0.24, wear=0.8),
    "blade": C._mat("#4A4743", 0.55, 0.060, 0.26, metal=True, wear=0.9),
    "leather": C._mat("#2B221B", 0.88, 0.030, 0.22, wear=0.8),
}

# ---------------------------------------------------------------------------- skeleton

JOINTS = (
    C.Joint("root", None, (0.0, 0.0, PELVIS_U)),
    C.Joint("spine1", "root", (0.0, 0.0, 2.6)),
    C.Joint("chest", "spine1", (0.0, 0.2, 3.4)),
    C.Joint("neck", "chest", (0.0, 0.3, 3.2)),
    C.Joint("head", "neck", (0.0, 0.5, 1.7)),
    C.Joint("jaw", "head", (0.0, 0.9, -0.9)),
    C.Joint("arm-l-upper", "chest", (3.3, 0.0, 2.4)),
    C.Joint("arm-l-lower", "arm-l-upper", (0.2, 0.0, -5.0)),
    C.Joint("arm-l-hand", "arm-l-lower", (0.0, 1.0, -4.7)),
    C.Joint("arm-r-upper", "chest", (-3.3, 0.0, 2.4)),
    C.Joint("arm-r-lower", "arm-r-upper", (-0.2, 0.0, -5.0)),
    C.Joint("arm-r-hand", "arm-r-lower", (0.0, 1.0, -4.7)),
    C.Joint("leg-l-upper", "root", (1.8, 0.0, -0.6)),
    C.Joint("leg-l-lower", "leg-l-upper", (0.0, 0.4, -6.6)),
    C.Joint("leg-l-foot", "leg-l-lower", (0.0, -0.4, -6.4)),
    C.Joint("leg-r-upper", "root", (-1.8, 0.0, -0.6)),
    C.Joint("leg-r-lower", "leg-r-upper", (0.0, 0.4, -6.6)),
    C.Joint("leg-r-foot", "leg-r-lower", (0.0, -0.4, -6.4)),
)
SKELETON = C.Skeleton(JOINTS)
JOINT_NAMES = tuple(j.name for j in JOINTS)
INDEX = {n: i for i, n in enumerate(JOINT_NAMES)}

BLADE_TILT = -37.0                  # the blade points forward and down from the fist: a sword held low
BLADE_LEN = 11.0
BLADE_DIR = (0.0, math.cos(math.radians(BLADE_TILT)), math.sin(math.radians(BLADE_TILT)))
# tips that own vertices beyond their last child: joint -> offset of the segment end from the joint, u
TIPS = {"head": (0.0, 0.3, 2.8), "arm-l-hand": (0.0, 0.4, -1.8),
        "arm-r-hand": tuple(d * (BLADE_LEN + 0.5) for d in BLADE_DIR),
        "leg-l-foot": (0.0, 2.6, -0.4), "leg-r-foot": (0.0, 2.6, -0.4), "jaw": (0.0, 1.8, -0.6)}


def bone_segments():
    """((joint, p0, p1, radius), ...) in BIND-POSE WORLD u, one per joint, for charforge.bind."""
    bw = SKELETON.bind_world()
    seg = []
    for j in JOINTS:
        a = bw[j.name][:3, 3]
        kids = [k for k in JOINTS if k.parent == j.name]
        if j.name in TIPS:
            b = a + np.array(TIPS[j.name])
        elif kids:
            b = bw[kids[0].name][:3, 3]
        else:
            b = a + (a - bw[j.parent][:3, 3])
        seg.append((j.name, tuple(a), tuple(b), 1.0))
    return tuple(seg)


# ---------------------------------------------------------------------------- the body

def _tube(path, radii, sides=8):
    return I.tube(path, radii, sides=sides, name="skeleton." + str(len(path)))


def _plate(outline, thick, taper, rot, move, bevel=0.0):
    return P.plate(outline, thick, taper, bevel=bevel).xform(rot_deg=rot, move=tuple(move))


def build_parts():
    """-> list of (mesh in bind-pose world u, material key). Flat plates (mesh.n is None) are the bright reads;
    check_actors.py's M3 negative control removes exactly those."""
    bw = SKELETON.bind_world()
    p = {n: bw[n][:3, 3] for n in JOINT_NAMES}
    out = []
    ch, sp, hd = p["chest"], p["spine1"], p["head"]

    # spine and the dark hollow the ribs stand against
    out.append((_tube([tuple(p["root"] + (0, -0.6, 0.5)), tuple(sp + (0, -0.7, 0)), tuple(ch + (0, -0.7, 0)),
                       tuple(p["neck"] + (0, -0.4, 0))], [0.7, 0.6, 0.6, 0.55], 6), "bone.old"))
    out.append((_tube([tuple(p["root"] + (0, 0, 1.2)), tuple(sp), tuple(ch + (0, 0, 0.8))], [1.5, 1.4, 1.5], 8),
                "cavity"))
    # ribs: arcs from the spine round each side, open at the sternum
    for dz, ax in ((-3.4, 2.1), (-2.3, 2.5), (-1.2, 2.75), (-0.1, 2.7), (1.0, 2.35)):
        for sx in (1.0, -1.0):
            path = []
            for t in np.linspace(0.0, 1.0, 6):
                a = math.radians(160.0 * t)
                path.append((ch[0] + sx * ax * math.sin(a), ch[1] + 0.2 - 1.8 * math.cos(a),
                             ch[2] + dz - 0.9 * t))
            out.append((_tube(path, 0.42, 5), "bone.hi"))
    out.append((_tube([tuple(ch + (0, 1.7, 1.6)), tuple(ch + (0, 2.0, -0.8)), tuple(ch + (0, 1.8, -3.4))],
                      [0.5, 0.45, 0.35], 5), "bone"))
    # shoulder girdle: broad up-facing collar plates, and a tattered pauldron on the left shoulder
    for sx in (1.0, -1.0):
        out.append((_plate(P._oct(2.4, 1.5, 0.4), 0.42, 0.7, (-6.0, 8.0 * sx, 0.0),
                           (ch[0] + sx * 1.9, ch[1] + 0.2, ch[2] + 2.9), 0.12), "bone.hi"))
    s = p["arm-l-upper"]
    out.append((_plate(P._oct(1.9, 1.8, 0.4), 0.5, 0.7, (0.0, 20.0, 0.0), (s[0] + 0.9, s[1], s[2] + 1.0), 0.12),
                "rust"))
    out.append((_plate(P._oct(1.6, 1.3, 0.4), 0.45, 0.7, (0.0, 34.0, 0.0), (s[0] + 2.1, s[1] + 0.2, s[2] - 0.9),
                       0.12), "rust"))
    # pelvis: a crossbar and two broad iliac plates
    r = p["root"]
    out.append((_tube([tuple(r + (-2.4, 0, 0.2)), tuple(r + (0, -0.3, 0.2)), tuple(r + (2.4, 0, 0.2))],
                      [0.9, 0.9, 0.9], 6), "bone.old"))
    for sx in (1.0, -1.0):
        out.append((_plate(P._oct(1.7, 2.1, 0.4, front=0.9), 0.42, 0.7, (-4.0, 24.0 * sx, 0.0),
                           (r[0] + sx * 2.1, r[1] + 0.0, r[2] + 1.1), 0.12), "bone.hi"))
    out.append((_tube([tuple(r + (-2.6, 0.1, 1.0)), tuple(r + (0, 1.3, 1.0)), tuple(r + (2.6, 0.1, 1.0))],
                      0.55, 6), "leather"))
    # a ragged loincloth, front and back: dark cloth hanging from the belt, the dark half of the figure's histogram
    for yy, tilt in ((1.5, 6.0), (-1.4, -4.0)):
        out.append((_plate(P._oct(1.7, 2.4, 0.3), 0.3, 0.8, (90.0 + tilt, 0.0, 0.0),
                           r + np.array((0.0, yy, -2.6))), "cavity"))
    # skull: cranium, a brow plate, dark sockets, an iron cap and band, the jaw
    out.append((_tube([tuple(hd + (0, -0.9, 1.3)), tuple(hd + (0, 0.4, 1.6)), tuple(hd + (0, 1.6, 1.2)),
                       tuple(hd + (0, 2.4, 0.5))], [1.3, 1.75, 1.45, 0.8], 10), "bone"))
    out.append((_plate(P._oct(1.75, 0.9, 0.4), 0.5, 0.7, (-16.0, 0.0, 0.0), hd + np.array((0, 1.6, 2.1)), 0.12),
                "bone.hi"))
    for sx in (1.0, -1.0):
        out.append((_tube([tuple(hd + (sx * 0.75, 1.7, 1.2)), tuple(hd + (sx * 0.75, 2.5, 1.2))], 0.42, 5),
                    "cavity"))
    ring = [tuple(hd + (1.95 * math.sin(a), 0.3 + 2.0 * math.cos(a), 2.2))
            for a in np.linspace(0.0, math.tau, 11)]
    out.append((_tube(ring, 0.38, 5), "iron"))
    out.append((_plate(P._oct(1.35, 1.5, 0.4), 0.45, 0.6, (-8.0, 0.0, 0.0), hd + np.array((0, 0.2, 3.0)), 0.12),
                "iron"))
    j = p["jaw"]
    out.append((_tube([tuple(j + (0, -0.5, 0.2)), tuple(j + (0, 0.9, -0.1)), tuple(j + (0, 2.1, -0.2))],
                      [0.8, 0.7, 0.4], 6), "bone"))
    out.append((_tube([tuple(p["neck"] + (0, -0.2, -1.0)), tuple(p["neck"] + (0, -0.1, 0.6)),
                       tuple(hd + (0, 0.0, 0.0))], 0.5, 5), "bone.old"))
    # arms, legs
    for tag in ("l", "r"):
        u, lo, h = p[f"arm-{tag}-upper"], p[f"arm-{tag}-lower"], p[f"arm-{tag}-hand"]
        out.append((_tube([tuple(u), tuple(lo), tuple(h)], [0.62, 0.48, 0.42], 6), "bone"))
        out.append((_tube([tuple(h + (0, -0.1, 0.3)), tuple(h + (0, 0.4, -1.5))], [0.5, 0.35], 5), "bone.old"))
        u, lo, f = p[f"leg-{tag}-upper"], p[f"leg-{tag}-lower"], p[f"leg-{tag}-foot"]
        out.append((_tube([tuple(u), tuple(lo), tuple(f)], [0.85, 0.62, 0.5], 6), "bone"))
        out.append((_tube([tuple(f + (0, -0.6, 0.1)), tuple(f + (0, 1.0, -0.3)), tuple(f + (0, 2.7, -0.5))],
                          [0.6, 0.6, 0.35], 5), "bone.old"))
        out.append((_tube([tuple(lo + (0, 0.5, 0.4)), tuple(lo + (0, 0.7, -0.5))], [0.9, 0.9], 6), "bone"))
        # a flat knee plate and a flat toe plate: the up-facing reads a thin shin and foot cannot give
        out.append((_plate(P._oct(0.9, 1.0, 0.4), 0.36, 0.7, (-35.0, 0.0, 0.0), lo + np.array((0, 1.2, 0.9)), 0.1),
                    "bone.hi"))
        out.append((_plate(P._oct(0.75, 1.5, 0.4, front=0.8), 0.3, 0.7, (0.0, 0.0, 0.0),
                           f + np.array((0, 1.4, 0.0)), 0.1), "bone.hi"))
    # the blade, fused into the right hand: grip, pommel, guard, and three plates with a notch between each
    h = p["arm-r-hand"] + np.array((0.0, 0.0, -0.3))
    rot = (BLADE_TILT, 0.0, 0.0)
    rm = I._rotation(rot)

    def local(y):   # a point `y` along the blade axis, in the hand's frame, rotated to the blade
        return rm @ np.array((0.0, y, 0.0)) + h

    out.append((_tube([tuple(local(-1.6)), tuple(local(1.8))], 0.46, 5), "leather"))
    out.append((_plate(P._oct(1.9, 0.5, 0.4), 0.5, 0.7, rot, local(2.0)), "iron"))
    out.append((_plate(P._oct(0.7, 0.7, 0.4), 0.55, 0.7, rot, local(-1.9)), "iron"))
    for y0, y1, hx, tip in ((2.5, 5.3, 1.0, 0.92), (5.55, 8.0, 0.86, 0.92), (8.25, 11.0, 0.94, 0.25)):
        hy = (y1 - y0) / 2.0
        out.append((_plate(P._oct(hx, hy, 0.12, front=1.0, taper_x=tip), 0.34, 0.6, rot,
                           local((y0 + y1) / 2.0), 0.10), "blade"))
    return out


def ground_offset(parts) -> float:
    return float(min(float(np.asarray(m.v)[:, 2].min()) for m, _ in parts))


# ---------------------------------------------------------------------------- clips

def _p(rot: dict, move=(0.0, 0.0, 0.0)) -> C.Pose:
    return C.Pose(dict(rot), move)


def _anim(name: str, times, poses, root_offset) -> G.GltfAnimation:
    """charforge Poses -> a glTF animation over this skeleton's joint nodes (gen_char_beast._anim, over OUR index).
    The root channel REPLACES the node's bind translation, so it is built from the GROUNDED skeleton's root."""
    t = np.asarray(times, dtype=np.float32)
    touched = sorted({n for p in poses for n in p.rot})
    rotations = {INDEX[n]: (t, np.array([_quat_from_iso(p.rot.get(n, (0.0, 0.0, 0.0))) for p in poses],
                                        dtype=np.float32)) for n in touched}
    translations = {}
    if any(tuple(p.root_move) != (0.0, 0.0, 0.0) for p in poses):
        base = np.asarray(root_offset, dtype=np.float64)
        translations[INDEX["root"]] = (t, np.array(
            [[(base[0] + m[0]) * UNITS_PER_U, (base[2] + m[2]) * UNITS_PER_U, (base[1] + m[1]) * UNITS_PER_U]
             for m in (p.root_move for p in poses)], dtype=np.float32))
    return G.GltfAnimation(name=name, rotations=rotations, translations=translations)


def _gait(phase: float):
    """One walk key: contralateral, stiff. Opposite arm to leading leg; the sword arm swings short."""
    s, c = math.sin(phase), math.cos(phase)
    A, K = 20.0, 26.0
    rot = {"leg-l-upper": (A * s, 0, 0), "leg-l-lower": (-K * max(0.0, c), 0, 0), "leg-l-foot": (-A * 0.3 * s, 0, 0),
           "leg-r-upper": (-A * s, 0, 0), "leg-r-lower": (-K * max(0.0, -c), 0, 0),
           "leg-r-foot": (A * 0.3 * s, 0, 0),
           "arm-r-upper": (11.0 * s + 6.0, 0, 0), "arm-r-lower": (12.0, 0, 0),
           "arm-l-upper": (-20.0 * s, 0, 0), "arm-l-lower": (10.0 + 6.0 * c, 0, 0),
           "spine1": (4.0, 0, -6.0 * s), "chest": (3.0, 0, -5.0 * s), "neck": (-2.0, 0, 4.0 * s),
           "head": (5.0 + 3.0 * abs(s), 0, 6.0 * s), "root": (0, 0, 5.0 * s)}
    return _p(rot, (0.0, 0.0, -0.55 * abs(s)))


def clips(root_offset):
    idle_a = {"head": (3.0, 0, 5.0), "neck": (2.0, 0, 2.0), "spine1": (2.0, 0, 1.5), "chest": (1.5, 0, -1.0),
              "arm-l-upper": (3.0, 0, 3.0), "arm-r-upper": (10.0, 0, -2.0), "arm-l-lower": (6.0, 0, 0),
              "leg-l-lower": (-2.0, 0, 0), "leg-r-lower": (-1.0, 0, 0)}
    idle_poses = [
        _p(idle_a),
        _p({"head": (6.5, 0, -5.0), "neck": (3.0, 0, -2.0), "spine1": (3.5, 0, -1.5), "chest": (2.5, 0, 1.0),
            "arm-l-upper": (5.0, 0, 5.0), "arm-r-upper": (13.0, 0, -4.0), "arm-l-lower": (9.0, 0, 0),
            "leg-l-lower": (-4.0, 0, 0), "leg-r-lower": (-3.0, 0, 0)}, (0.0, 0.0, -0.18)),
        _p(idle_a)]
    idle = _anim("idle", [0.0, 1.3, 2.6], idle_poses, root_offset)

    walk_poses = [_gait(math.tau * k / 8.0) for k in range(9)]
    walk = _anim("walk", [0.11 * k for k in range(9)], walk_poses, root_offset)

    neutral = _p({})
    attack_poses = [
        neutral,
        # wind up across the body: sword arm up and over the opposite shoulder, torso coiled back
        _p({"arm-r-upper": (138.0, 0, -22.0), "arm-r-lower": (28.0, 0, 0), "arm-r-hand": (-10.0, 0, 0),
            "arm-l-upper": (-28.0, 0, -8.0), "spine1": (-4.0, 0, -14.0), "chest": (-6.0, 0, -12.0),
            "head": (-4.0, 0, 8.0), "leg-l-upper": (-8.0, 0, 0), "leg-r-upper": (12.0, 0, 0)}, (0, -0.4, -0.2)),
        # the cut, halfway: fast
        _p({"arm-r-upper": (84.0, 0, 8.0), "arm-r-lower": (14.0, 0, 0), "arm-l-upper": (-18.0, 0, 6.0),
            "spine1": (8.0, 0, 6.0), "chest": (10.0, 0, 8.0), "head": (6.0, 0, -4.0),
            "leg-l-upper": (14.0, 0, 0), "leg-r-upper": (-6.0, 0, 0)}, (0, 0.4, -0.5)),
        # the end of the cut, blade low and forward
        _p({"arm-r-upper": (28.0, 0, 24.0), "arm-r-lower": (6.0, 0, 0), "arm-l-upper": (-8.0, 0, 10.0),
            "spine1": (14.0, 0, 18.0), "chest": (14.0, 0, 14.0), "head": (10.0, 0, -8.0),
            "leg-l-upper": (26.0, 0, 0), "leg-l-lower": (-8.0, 0, 0), "leg-r-upper": (-16.0, 0, 0),
            "leg-r-lower": (-10.0, 0, 0)}, (0, 0.9, -0.9)),
        _p({"arm-r-upper": (22.0, 0, 14.0), "arm-r-lower": (10.0, 0, 0), "spine1": (8.0, 0, 8.0),
            "chest": (6.0, 0, 6.0), "head": (6.0, 0, -3.0), "leg-l-upper": (10.0, 0, 0),
            "leg-r-upper": (-6.0, 0, 0)}, (0, 0.3, -0.3)),
        neutral]
    attack = _anim("attack", [0.0, 0.30, 0.38, 0.46, 0.80, 1.05], attack_poses, root_offset)

    # the fall: knees buckle, the frame pitches forward about the pelvis, the skull goes last
    die_poses = [
        neutral,
        _p({"leg-l-upper": (30.0, 0, 0), "leg-l-lower": (-52.0, 0, 0), "leg-r-upper": (22.0, 0, 0),
            "leg-r-lower": (-48.0, 0, 0), "spine1": (10.0, 0, 0), "chest": (8.0, 0, 0), "head": (-8.0, 0, 0),
            "arm-l-upper": (-12.0, 0, 10.0), "arm-r-upper": (-4.0, 0, -8.0)}, (0, 0.0, -3.4)),
        _p({"root": (-38.0, 0, 0), "leg-l-upper": (52.0, 0, 0), "leg-l-lower": (-82.0, 0, 0),
            "leg-r-upper": (44.0, 0, 0), "leg-r-lower": (-76.0, 0, 0), "spine1": (-8.0, 0, 0),
            "head": (-14.0, 0, 0), "arm-l-upper": (-34.0, 0, 20.0), "arm-r-upper": (-26.0, 0, -18.0)},
           (0, 2.4, -6.6)),
        _p({"root": (-80.0, 0, 0), "leg-l-upper": (30.0, 0, 6.0), "leg-l-lower": (-28.0, 0, 0),
            "leg-r-upper": (22.0, 0, -6.0), "leg-r-lower": (-24.0, 0, 0), "head": (-22.0, 0, 0),
            "arm-l-upper": (-60.0, 0, 34.0), "arm-r-upper": (-54.0, 0, -30.0)}, (0, 5.4, -11.0)),
        _p({"root": (-86.0, 0, 0), "leg-l-upper": (14.0, 0, 8.0), "leg-l-lower": (-12.0, 0, 0),
            "leg-r-upper": (8.0, 0, -8.0), "leg-r-lower": (-10.0, 0, 0), "head": (-26.0, 0, 6.0),
            "jaw": (22.0, 0, 0), "arm-l-upper": (-70.0, 0, 40.0), "arm-r-upper": (-64.0, 0, -36.0)},
           (0, 6.0, DIE_DROP))]
    die = _anim("die", [0.0, 0.28, 0.62, 1.0, 1.5], die_poses, root_offset)
    return ([idle, walk, attack, die],
            dict(idle=idle_poses, walk=walk_poses, attack=attack_poses, die=die_poses))


# ---------------------------------------------------------------------------- build

def build_asset() -> dict:
    """Pure function of the source: nothing here reads the clock, the environment or the filesystem."""
    if len(JOINTS) > MAX_JOINTS:
        raise ValueError(f"{len(JOINTS)} joints, contract C3 caps a forged actor at {MAX_JOINTS}")
    raw = build_parts()
    drop = ground_offset(raw)
    parts = [(m.xform(move=(0.0, 0.0, -drop)), k) for m, k in raw]
    meshes = [m for m, _ in parts]
    split, uvs, rects = C.unwrap_split(meshes, ATLAS_SIZE, axis=P.unwrap_axes(meshes))
    wear, grime, rust, dust = AGE
    atlas = C.bake_albedo(split, None, None, uvs, rects, ATLAS_SIZE, materials=[MATS[k] for _, k in parts],
                          seed="skeleton", wear=wear, grime=grime, rust=rust, dust=dust)
    png, scale, enc = P.encode_atlas(atlas)

    # the meshes dropped to the ground, so the skeleton's root drops with them
    sk = C.Skeleton(tuple(C.Joint(j.name, j.parent,
                                  (j.offset[0], j.offset[1], j.offset[2] - (drop if j.parent is None else 0.0)))
                          for j in JOINTS))
    bones = tuple((n, (a[0], a[1], a[2] - drop), (b[0], b[1], b[2] - drop), r) for n, a, b, r in bone_segments())
    skins = [C.bind(m, sk, bones) for m in split]

    nodes = [G.GltfNode(name=j.name, parent=None if j.parent is None else INDEX[j.parent],
                        translation=(j.offset[0] * UNITS_PER_U, j.offset[2] * UNITS_PER_U,
                                     j.offset[1] * UNITS_PER_U))
             for j in sk.joints]
    primitives = []
    for m, uv, sknn in zip(split, uvs, skins):
        pos, nrm, uvo, idx = P._render_arrays(m, uv)
        j4, w4 = C.skin_attributes(sknn, len(sk.joints))
        if m.n is None:
            take = np.asarray(m.f, dtype=np.int64).reshape(-1)
            j4, w4 = j4[take], w4[take]
        if len(j4) != len(pos):
            raise ValueError(f"skin attributes ({len(j4)}) do not match the exported vertices ({len(pos)})")
        primitives.append(G.GltfPrimitive(positions=pos, normals=nrm, uvs=uvo, indices=idx, joints=j4,
                                          weights=w4, material=0, node=None))
    materials = [{"name": "skeleton.baked", "base_color_factor": (scale, scale, scale, 1.0), "base_color_png": png,
                  "metallic": 0.0, "roughness": 0.9}]
    anims, poses = clips(sk.joints[0].offset)
    return dict(name="skeleton", nodes=nodes, primitives=primitives, materials=materials, animations=anims,
                skin_joints=list(range(len(sk.joints))),
                inverse_bind=C.inverse_bind_export(sk, UNITS_PER_U),
                atlas=atlas, encoded=enc, scale=scale, rects=rects, parts=parts, emissive=[],
                meshes=split, uvs=uvs, skeleton=sk, skins=skins, clip_poses=poses, drop=drop)


def glb_bytes(asset: dict) -> bytes:
    return G.build_glb(asset["nodes"], asset["primitives"], asset["skin_joints"], asset["inverse_bind"],
                       asset["animations"], asset["materials"])


def main(out_dir: str | None = None) -> Path:
    out = Path(out_dir) if out_dir else DEFAULT_OUT
    out.mkdir(parents=True, exist_ok=True)
    a = build_asset()
    data = glb_bytes(a)
    path = out / OUT_NAME
    path.write_bytes(data)
    verts = sum(len(p.positions) for p in a["primitives"])
    tris = sum(len(p.indices) for p in a["primitives"])
    print(f"gen_char_skeleton: {path}")
    print(f"  joints={len(a['skin_joints'])} parts={len(a['parts'])} verts={verts} tris={tris} "
          f"clips={[c.name for c in a['animations']]}")
    print(f"  size={len(data)} bytes sha1={hashlib.sha1(data).hexdigest()}")
    return path


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else None)
