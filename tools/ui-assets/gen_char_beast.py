"""The grave hound: the first original SKINNED creature, exported as an animated GLB.

    python tools/ui-assets/gen_char_beast.py [out_dir]

Writes <out_dir>/hound.glb (default: dream/animations/enemies/, beside the wolf, which this wave does not
touch). Spec: docs/superpowers/specs/2026-11-04-models-and-rigs/00-acceptance.md (M4 and contract C3).

Skinned, where the player and the villagers are rigid, and the division is deliberate (finding F2): node
overrides resolve by renderable node name, so only an actor with no pose-layer contract may be skinned. A hound
has none - nothing holds a weapon in its jaws - so it gets a real skeleton and linear blend skinning.

Contract C3, held here: ONE skin; 22 joints against the limit of 32 (the engine's hard ceiling is 64, above
which LoadSkinning silently returns null and the mesh draws in bind pose); every primitive carries JOINTS_0 and
WEIGHTS_0 with rows summing to 1; inverseBindMatrices in exported space. Clips are bare names - idle, walk,
gallop, attack, die - not the wolf pack's `AnimalArmature|Idle`.

The hound carries TWO gaits: `walk` is the trot it patrols at and `gallop` the bound it chases at, picked on
the client from the speed its drawn pose is actually moving at (EnemyClipSet), because a client never knows an
enemy's authored speed.

Authoring. The skeleton and the meshes live in charforge's world: u, 16 u = 1 m, iso axes (x right, y forward,
z up), origin at the foot point. The poses are charforge Poses - Euler degrees per joint, in ISO axes - and
every one is converted to a glTF quaternion numerically, by building the iso rotation matrix and conjugating it
through the export's axis permutation. Authoring the quaternions by hand in glTF space instead would mean every
clip silently disagreeing with the CPU skinning that check_actors.py validates them against.

All geometry is original: a gaunt, long-jawed hound with bone ridges down its spine and a pitted iron collar.
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
import gen_char_player as P  # noqa: E402
import gltfwrite as G  # noqa: E402
import isoforge as I  # noqa: E402

UNITS_PER_U = P.UNITS_PER_U
ATLAS_SIZE = 256
MAX_JOINTS = 32                     # contract C3, well under the engine's 64
DEFAULT_OUT = HERE.parents[1] / "dream" / "animations" / "enemies"
OUT_NAME = "hound.glb"
AGE = (0.50, 0.72, 0.18, 0.24)      # wear, grime, rust, dust: a thing that has been outdoors a long time

# iso -> glTF axis permutation, as a matrix. The same one _render_arrays applies to positions; stated here as a
# matrix because the clip conversion has to conjugate rotations through it, not just permute points.
SWAP = np.array(((1.0, 0.0, 0.0), (0.0, 0.0, 1.0), (0.0, 1.0, 0.0)))

SHOULDER_U = 13.4                   # withers height; a hound a little under a metre at the shoulder

MATS = {
    "hide": C._mat("#23201D", 0.93, 0.030, 0.20, wear=0.5),
    "hide.dark": C._mat("#191714", 0.94, 0.030, 0.18, wear=0.5),
    "bone": C._mat("#6B6353", 0.82, 0.030, 0.24, wear=0.6),
    # Warm-neutral rather than the palette's grim.iron.pitted (#212022). That hex has blue a level above red,
    # and on a collar this dark the 8-bit atlas quantises the pair into a hue of exactly 300 degrees - magenta -
    # on 70 texels, which check_char's palette gate counts as a forbidden hue. Nudging red above blue removes
    # the artefact without changing what the material looks like.
    "iron": C._mat("#242120", 0.72, 0.030, 0.24, metal=True, wear=0.9),
    "claw": C._mat("#2A241C", 0.74, 0.040, 0.24, wear=0.7),
}


# ---------------------------------------------------------------------------- skeleton

def _legs():
    """(joint tuples, bone segments) for the four legs. Front legs hang off the chest, hind legs off the root."""
    joints, bones = [], []
    for tag, parent, x, y, top, mid, low in (
            ("fl", "chest", 2.25, 0.4, -1.2, -5.4, -4.6),
            ("fr", "chest", -2.25, 0.4, -1.2, -5.4, -4.6),
            ("hl", "root", 2.35, -0.6, -0.9, -5.0, -5.1),
            ("hr", "root", -2.35, -0.6, -0.9, -5.0, -5.1)):
        joints += [C.Joint(f"{tag}-upper", parent, (x, y, top)),
                   C.Joint(f"{tag}-lower", f"{tag}-upper", (0.0, 0.3 if tag[0] == "f" else -0.9, mid)),
                   C.Joint(f"{tag}-foot", f"{tag}-lower", (0.0, 0.9, low))]
    return joints, bones


JOINTS = (
    C.Joint("root", None, (0.0, 0.0, SHOULDER_U - 1.6)),     # the pelvis
    C.Joint("spine1", "root", (0.0, 3.2, 0.5)),
    C.Joint("spine2", "spine1", (0.0, 3.6, 0.5)),
    C.Joint("chest", "spine2", (0.0, 3.4, 0.6)),
    C.Joint("neck", "chest", (0.0, 2.3, 1.5)),
    C.Joint("head", "neck", (0.0, 2.1, 0.7)),
    C.Joint("jaw", "head", (0.0, 1.1, -0.7)),
    C.Joint("tail1", "root", (0.0, -2.4, 0.9)),
    C.Joint("tail2", "tail1", (0.0, -2.8, -0.3)),
    C.Joint("tail3", "tail2", (0.0, -2.8, -0.8)),
) + tuple(_legs()[0])

SKELETON = C.Skeleton(JOINTS)
JOINT_NAMES = tuple(j.name for j in JOINTS)
INDEX = {n: i for i, n in enumerate(JOINT_NAMES)}


def bone_segments():
    """((joint, p0, p1, radius), ...) in BIND-POSE WORLD u, one segment per joint: what charforge.bind measures
    point-to-segment distance against. A joint with no segment takes no vertices, which is right for a parent
    that only carries its children (there is no such joint here; every one has a limb or a body section)."""
    bw = SKELETON.bind_world()
    seg = []
    for j in JOINTS:
        a = bw[j.name][:3, 3]
        kids = [k for k in JOINTS if k.parent == j.name]
        if kids:
            b = bw[kids[0].name][:3, 3]
        else:   # a tip: extend along the direction it came from, so the end of a limb still owns its vertices
            par = bw[j.parent][:3, 3] if j.parent else a - np.array((0.0, 0.0, 1.0))
            b = a + (a - par)
        r = 1.6 if j.name in ("root", "spine1", "spine2", "chest") else 1.0
        seg.append((j.name, tuple(a), tuple(b), r))
    return tuple(seg)


# ---------------------------------------------------------------------------- the body

def _tube(path, radii, sides=8):
    return I.tube(path, radii, sides=sides, name="hound." + str(len(path)))


def build_parts():
    """-> list of (mesh in bind-pose world u, material key). A skinned actor has no node contract, so there is
    no part-to-node mapping here: every vertex is bound to the skeleton by charforge.bind instead."""
    bw = SKELETON.bind_world()
    p = {n: bw[n][:3, 3] for n in JOINT_NAMES}
    out = []

    # body: a tube from the haunch to the chest, then the neck and the skull
    body = _tube([tuple(p["root"] + (0, -2.2, 0.2)), tuple(p["root"]), tuple(p["spine1"]), tuple(p["spine2"]),
                  tuple(p["chest"]), tuple(p["neck"])],
                 [1.70, 2.05, 2.00, 1.85, 1.95, 1.35], sides=10)
    out.append((body, "hide"))
    # HAUNCH AND SHOULDER MASS. Without these the legs are sticks pinned to a tube and the whole thing reads as
    # a mechanical insect rather than an animal: a quadruped's leg has to emerge FROM a body. Each is a short
    # fat loft centred between the hip (or shoulder) joint and the body's axis, so charforge.bind splits it
    # between the limb's upper joint and the spine section behind it and it deforms with the stride.
    for tag, anchor, fwd, rad in (("hl", "root", -0.4, (1.95, 2.35, 1.80)), ("hr", "root", -0.4, (1.95, 2.35, 1.80)),
                                  ("fl", "chest", 0.3, (1.55, 1.95, 1.45)), ("fr", "chest", 0.3, (1.55, 1.95, 1.45))):
        j, b = p[f"{tag}-upper"], p[anchor]
        cx = (j[0] + b[0]) * 0.5
        mass = _tube([(cx, j[1] + fwd - 1.5, j[2] + 1.9), (cx, j[1] + fwd, j[2] + 0.5),
                      (cx, j[1] + fwd + 1.2, j[2] - 1.3)], list(rad), sides=8)
        out.append((mass, "hide"))
    # SKULL. Brought up in size against the body - a small head on a long tube is the other half of the
    # insect read - and given a brow mass behind the muzzle so it narrows towards the nose like a hound.
    skull = _tube([tuple(p["head"] + (0, -1.2, 0.0)), tuple(p["head"] + (0, 0.2, 0.35)),
                   tuple(p["head"] + (0, 1.6, 0.25)), tuple(p["head"] + (0, 3.2, -0.35)),
                   tuple(p["head"] + (0, 4.6, -0.70))],
                  [1.45, 1.85, 1.60, 0.92, 0.52], sides=10)
    out.append((skull, "hide.dark"))
    jaw = _tube([tuple(p["jaw"] + (0, -0.4, 0.1)), tuple(p["jaw"] + (0, 1.4, -0.05)),
                 tuple(p["jaw"] + (0, 3.1, -0.25)), tuple(p["jaw"] + (0, 3.9, -0.35))],
                [0.95, 0.80, 0.52, 0.34], sides=8)
    out.append((jaw, "bone"))
    tail = _tube([tuple(p["tail1"]), tuple(p["tail2"]), tuple(p["tail3"]), tuple(p["tail3"] + (0, -2.6, -0.9))],
                 [0.70, 0.52, 0.36, 0.18], sides=6)
    out.append((tail, "hide"))

    # the legs, each a two-segment tube plus a paw
    for tag in ("fl", "fr", "hl", "hr"):
        u, l, f = p[f"{tag}-upper"], p[f"{tag}-lower"], p[f"{tag}-foot"]
        out.append((_tube([tuple(u), tuple(l), tuple(f)], [1.35, 0.78, 0.55], sides=8), "hide"))
        paw = _tube([tuple(f + (0, -0.3, 0.1)), tuple(f + (0, 0.9, -0.25)), tuple(f + (0, 1.6, -0.3))],
                    [0.58, 0.62, 0.40], sides=6)
        out.append((paw, "claw"))

    # Bone ridges and a collar: broad, flat, UP-FACING plates. They are the hound's whole answer to M3 - a
    # smooth-lofted animal shades as one soft ramp and renders as a single flat value under a brazier. The
    # arithmetic is in gen_char_player._torso_plates; the short version is that on a camera looking down at 50
    # degrees, a horizontal facet is the only place a bright pixel can live.
    # Four of them, not three equal rectangles: they TAPER and overlap down the spine, each one narrower,
    # shorter and more steeply raked than the one behind it, so the ridge follows the animal's back instead of
    # sitting on it like a pair of saddlebags. The taper_x argument narrows each plate's front end as well.
    for along, hx, hy, tilt, tap, lift in ((0.02, 1.95, 1.75, 2.0, 0.92, 1.80), (0.30, 1.78, 1.60, -1.0, 0.90, 1.82),
                                           (0.57, 1.56, 1.45, -5.0, 0.88, 1.80), (0.82, 1.30, 1.25, -10.0, 0.86, 1.72)):
        a = p["root"] + (p["chest"] - p["root"]) * along
        out.append((P.plate(P._oct(hx, hy, 0.40, front=0.96, taper_x=tap), 0.44, 0.64, bevel=0.12)
                    .xform(rot_deg=(tilt, 0.0, 0.0), move=(a[0], a[1], a[2] + lift)), "bone"))
    out.append((P.plate(P._oct(1.46, 1.62, 0.40, front=0.92, taper_x=0.80), 0.44, 0.62, bevel=0.12)
                .xform(rot_deg=(-14.0, 0.0, 0.0), move=(p["head"][0], p["head"][1] + 0.7, p["head"][2] + 1.45)),
               "bone"))
    out.append((P.plate(P._oct(1.52, 1.00, 0.40), 0.44, 0.66)
                .xform(rot_deg=(-28.0, 0.0, 0.0), move=(p["neck"][0], p["neck"][1] + 0.1, p["neck"][2] + 1.05)),
               "iron"))
    for tag, anchor, fwd in (("fl", "chest", 0.3), ("fr", "chest", 0.3)):
        # a scapula plate over each new shoulder mass: bright bone where the foreleg meets the body, which is
        # both what a gaunt grave-hound would show and where the mass change needs reading
        j = p[f"{tag}-upper"]
        sx = 1.0 if tag.endswith("l") else -1.0
        out.append((P.plate(P._oct(1.00, 1.55, 0.44, front=0.94), 0.40, 0.64, bevel=0.12)
                    .xform(rot_deg=(-4.0, 26.0 * sx, 0.0),
                           move=(j[0] * 0.70, j[1] + fwd - 0.2, j[2] + 1.55)), "bone"))
    for sx in (1.0, -1.0):   # haunch plates, low and broad, where a brazier at the hound's own height rakes hardest
        out.append((P.plate(P._oct(1.30, 1.90, 0.44, front=0.94), 0.42, 0.64, bevel=0.12)
                    .xform(rot_deg=(-2.0, 26.0 * sx, 0.0),
                           move=(p["root"][0] + sx * 1.70, p["root"][1] - 0.4, p["root"][2] + 1.35)), "bone"))
    for sx in (1.0, -1.0):   # ears, flat and swept back
        out.append((P.plate(P._oct(0.62, 1.10, 0.40), 0.30, 0.70)
                    .xform(rot_deg=(-62.0, 0.0, 26.0 * sx),
                           move=(p["head"][0] + sx * 1.05, p["head"][1] - 0.70, p["head"][2] + 1.60)), "hide.dark"))
    return out


def ground_offset(parts) -> float:
    """How far to drop every mesh so the lowest vertex sits on z = 0. The skeleton is authored from the withers
    down, so the paws land where the loft radii put them rather than where a constant says they should."""
    return float(min(float(np.asarray(m.v)[:, 2].min()) for m, _ in parts))


# ---------------------------------------------------------------------------- clips

def _pose(**rot) -> C.Pose:
    """A charforge Pose from keyword joint names (dashes written as underscores)."""
    return _p(rot)


def _p(rot: dict, move=(0.0, 0.0, 0.0)) -> C.Pose:
    """The same, from a dict, with an optional root translation in u."""
    return C.Pose({k.replace("_", "-"): v for k, v in rot.items()}, move)


def _quat_from_iso(rot_deg) -> tuple:
    """An ISO-axis Euler triple (degrees, x then y then z, isoforge._rotation's order) -> the glTF quaternion of
    the SAME rotation in exported axes. R_gltf = SWAP . R_iso . SWAP, because SWAP is its own inverse; done
    numerically so a sign cannot be reasoned about wrongly, and check_actors.py proves it against CPU skinning."""
    R = SWAP @ I._rotation(rot_deg) @ SWAP
    t = R[0, 0] + R[1, 1] + R[2, 2]
    if t > 0.0:
        s = math.sqrt(t + 1.0) * 2.0
        q = ((R[2, 1] - R[1, 2]) / s, (R[0, 2] - R[2, 0]) / s, (R[1, 0] - R[0, 1]) / s, 0.25 * s)
    elif R[0, 0] > R[1, 1] and R[0, 0] > R[2, 2]:
        s = math.sqrt(1.0 + R[0, 0] - R[1, 1] - R[2, 2]) * 2.0
        q = (0.25 * s, (R[0, 1] + R[1, 0]) / s, (R[0, 2] + R[2, 0]) / s, (R[2, 1] - R[1, 2]) / s)
    elif R[1, 1] > R[2, 2]:
        s = math.sqrt(1.0 + R[1, 1] - R[0, 0] - R[2, 2]) * 2.0
        q = ((R[0, 1] + R[1, 0]) / s, 0.25 * s, (R[1, 2] + R[2, 1]) / s, (R[0, 2] - R[2, 0]) / s)
    else:
        s = math.sqrt(1.0 + R[2, 2] - R[0, 0] - R[1, 1]) * 2.0
        q = ((R[0, 2] + R[2, 0]) / s, (R[1, 2] + R[2, 1]) / s, 0.25 * s, (R[1, 0] - R[0, 1]) / s)
    n = math.sqrt(sum(x * x for x in q))
    return tuple(x / n for x in q)


def _anim(name: str, times, poses, root_offset) -> G.GltfAnimation:
    """charforge Poses -> a glTF animation over the joint nodes. Only the joints a clip actually touches get a
    channel, so a still joint costs nothing; the root additionally takes pose.root_move as a translation on top
    of its bind offset."""
    t = np.asarray(times, dtype=np.float32)
    touched = sorted({n for p in poses for n in p.rot})
    rotations = {INDEX[n]: (t, np.array([_quat_from_iso(p.rot.get(n, (0.0, 0.0, 0.0))) for p in poses],
                                        dtype=np.float32)) for n in touched}
    translations = {}
    if any(tuple(p.root_move) != (0.0, 0.0, 0.0) for p in poses):
        # The root channel REPLACES the node's bind translation, so it must be built from the skeleton that was
        # actually exported - the grounded one, whose root sits `drop` lower than the authored JOINTS. Using the
        # authored offset here put the whole hound 1.04 units out on every frame of `walk` and nothing threw;
        # check_actors.py's CPU-skinning cross-check is what caught it.
        base = np.asarray(root_offset, dtype=np.float64)
        translations[INDEX["root"]] = (t, np.array(
            [[(base[0] + m[0]) * UNITS_PER_U, (base[2] + m[2]) * UNITS_PER_U, (base[1] + m[1]) * UNITS_PER_U]
             for m in (p.root_move for p in poses)], dtype=np.float32))
    return G.GltfAnimation(name=name, rotations=rotations, translations=translations)


def _gait(a: float):
    """One leg phase: the fore and hind legs of a trot, diagonal pairs together."""
    return dict(fl_upper=(a, 0.0, 0.0), fl_lower=(-a * 0.7, 0.0, 0.0), fr_upper=(-a, 0.0, 0.0),
                fr_lower=(a * 0.7, 0.0, 0.0), hl_upper=(-a * 0.9, 0.0, 0.0), hl_lower=(a * 0.8, 0.0, 0.0),
                hr_upper=(a * 0.9, 0.0, 0.0), hr_lower=(-a * 0.8, 0.0, 0.0))


def _bound(a: float):
    """One leg phase of a BOUNDING gait: the fore pair swings TOGETHER and the hind pair together, which is
    what separates a gallop from _gait's diagonal trot - the pairing reads as the gait, not the amplitude.
    The two sides are a few percent apart because a galloping animal runs on a lead leg; dead symmetry here
    is what makes a four-legged run look mechanical."""
    return dict(fl_upper=(a, 0.0, 0.0), fl_lower=(-a * 0.80, 0.0, 0.0), fr_upper=(a * 0.92, 0.0, 0.0),
                fr_lower=(-a * 0.74, 0.0, 0.0), hl_upper=(-a, 0.0, 0.0), hl_lower=(a * 0.90, 0.0, 0.0),
                hr_upper=(-a * 0.92, 0.0, 0.0), hr_lower=(a * 0.84, 0.0, 0.0))


def clips(root_offset) -> list:
    """idle, walk, gallop, attack, die - the bare names contract C5 asks for. `die` runs once in the engine,
    which holds the last frame, so its final pose is the one the corpse keeps."""
    breath = [_pose(spine1=(1.2, 0, 0), chest=(-0.8, 0, 0), head=(2.0, 0, 0), tail1=(-6.0, 0, 0),
                    tail2=(-4.0, 0, 8.0)),
              _pose(spine1=(-0.9, 0, 0), chest=(1.0, 0, 0), head=(-1.5, 0, 0), jaw=(4.0, 0, 0),
                    tail1=(-3.0, 0, 0), tail2=(-2.0, 0, -9.0)),
              _pose(spine1=(1.2, 0, 0), chest=(-0.8, 0, 0), head=(2.0, 0, 0), tail1=(-6.0, 0, 0),
                    tail2=(-4.0, 0, 8.0))]
    idle = _anim("idle", [0.0, 1.1, 2.2], breath, root_offset)

    walk_poses = []
    for a, dip in ((26.0, 0.0), (0.0, -0.45), (-26.0, 0.0), (0.0, -0.45), (26.0, 0.0)):
        walk_poses.append(_p(dict(_gait(a), spine1=(1.5, 0, 0), neck=(-3.0, 0, 0), head=(2.0, 0, 0),
                                  tail1=(-10.0, 0, a * 0.25)), (0.0, 0.0, dip)))
    walk = _anim("walk", [0.0, 0.17, 0.34, 0.51, 0.68], walk_poses, root_offset)

    # The chase gait. A 0.42 s cycle against the walk's 0.68, the legs paired fore-and-hind instead of
    # diagonally, the spine flexing and extending through the stride (`flex`, carried down spine2 and
    # countered at the neck so the head stays level enough to read) and the root rising into the one
    # suspended frame and dropping into the gather. The spine is what makes this a bound rather than a
    # fast trot, so it moves three times as far as the walk's constant 1.5 degrees.
    gallop_poses = []
    for a, flex, dip in ((34.0, -10.0, -0.25), (10.0, 8.0, 0.85), (-34.0, 10.0, -0.25),
                         (-10.0, -8.0, -0.80), (34.0, -10.0, -0.25)):
        gallop_poses.append(_p(dict(_bound(a), spine1=(flex, 0, 0), spine2=(flex * 0.55, 0, 0),
                                    neck=(-flex * 0.7, 0, 0), head=(3.0 - flex * 0.3, 0, 0),
                                    jaw=(6.0, 0, 0), tail1=(-16.0, 0, a * 0.18),
                                    tail2=(-8.0, 0, a * 0.12)), (0.0, 0.0, dip)))
    gallop = _anim("gallop", [0.0, 0.11, 0.21, 0.32, 0.42], gallop_poses, root_offset)

    attack_poses = [
        _pose(neck=(0, 0, 0), head=(0, 0, 0), jaw=(0, 0, 0), spine1=(0, 0, 0)),
        _pose(neck=(-22.0, 0, 0), head=(-14.0, 0, 0), jaw=(34.0, 0, 0), spine1=(-8.0, 0, 0),
              fl_upper=(-26.0, 0, 0), fr_upper=(-26.0, 0, 0)),
        _pose(neck=(16.0, 0, 0), head=(20.0, 0, 0), jaw=(6.0, 0, 0), spine1=(10.0, 0, 0),
              fl_upper=(24.0, 0, 0), fr_upper=(24.0, 0, 0)),
        _pose(neck=(6.0, 0, 0), head=(8.0, 0, 0), jaw=(16.0, 0, 0), spine1=(4.0, 0, 0)),
        _pose(neck=(0, 0, 0), head=(0, 0, 0), jaw=(0, 0, 0), spine1=(0, 0, 0))]
    attack = _anim("attack", [0.0, 0.14, 0.26, 0.40, 0.58], attack_poses, root_offset)

    # the fall: the legs buckle, the body rolls onto its side (a roll about the spine's own forward axis) and
    # the head goes last. The root drops by the body's own half-thickness so the carcass rests ON the ground.
    die_poses = [
        C.Pose({}),
        _p(dict(fl_upper=(34.0, 0, 0), fr_upper=(30.0, 0, 0), hl_upper=(-26.0, 0, 0), hr_upper=(-22.0, 0, 0),
                    neck=(14.0, 0, 0), head=(-10.0, 0, 0), spine1=(6.0, 0, 0)), (0.0, 0.0, -1.6)),
        _p(dict(fl_upper=(58.0, 0, 0), fr_upper=(52.0, 0, 0), hl_upper=(-48.0, 0, 0), hr_upper=(-42.0, 0, 0),
                    neck=(26.0, 0, 0), head=(-24.0, 0, 0), spine1=(10.0, 0, 34.0), tail1=(0, 0, 26.0)),
           (0.0, 0.0, -5.6)),
        _p(dict(fl_upper=(72.0, 0, 0), fr_upper=(66.0, 0, 0), hl_upper=(-62.0, 0, 0), hr_upper=(-56.0, 0, 0),
                    neck=(30.0, 0, 0), head=(-30.0, 0, 0), spine1=(12.0, 0, 62.0), tail1=(0, 0, 40.0),
                    jaw=(18.0, 0, 0)), (0.0, 0.0, -8.4)),
        _p(dict(fl_upper=(74.0, 0, 0), fr_upper=(68.0, 0, 0), hl_upper=(-64.0, 0, 0), hr_upper=(-58.0, 0, 0),
                    neck=(31.0, 0, 0), head=(-31.0, 0, 0), spine1=(12.0, 0, 70.0), tail1=(0, 0, 44.0),
                    jaw=(14.0, 0, 0)), (0.0, 0.0, -9.0))]
    die = _anim("die", [0.0, 0.22, 0.55, 0.95, 1.40], die_poses, root_offset)
    return ([idle, walk, gallop, attack, die],
            dict(idle=breath, walk=walk_poses, gallop=gallop_poses, attack=attack_poses, die=die_poses))


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
                          seed="hound", wear=wear, grime=grime, rust=rust, dust=dust)
    png, scale, enc = P.encode_atlas(atlas)

    # The skeleton is authored from the withers down; the meshes were dropped to the ground, so the skeleton
    # drops with them or the bind pose and the vertices disagree.
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
            # _render_arrays unwelds a flat-shaded mesh into one vertex per triangle corner, in m.f order, so
            # the skin attributes have to follow the same expansion or every plate is bound to the wrong joint.
            take = np.asarray(m.f, dtype=np.int64).reshape(-1)
            j4, w4 = j4[take], w4[take]
        if len(j4) != len(pos):
            raise ValueError(f"skin attributes ({len(j4)}) do not match the exported vertices ({len(pos)})")
        primitives.append(G.GltfPrimitive(positions=pos, normals=nrm, uvs=uvo, indices=idx, joints=j4,
                                          weights=w4, material=0, node=None))
    materials = [{"name": "hound.baked", "base_color_factor": (scale, scale, scale, 1.0), "base_color_png": png,
                  "metallic": 0.0, "roughness": 0.9}]
    anims, poses = clips(sk.joints[0].offset)
    return dict(name="hound", nodes=nodes, primitives=primitives, materials=materials, animations=anims,
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
    print(f"gen_char_beast: {path}")
    print(f"  joints={len(a['skin_joints'])} parts={len(a['parts'])} verts={verts} tris={tris} "
          f"clips={[c.name for c in a['animations']]}")
    print(f"  size={len(data)} bytes sha1={hashlib.sha1(data).hexdigest()}")
    return path


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else None)
