"""Actor rig layer: skeleton and clips, rigid-part localisation, two-bone skinning, a crude atlas unwrap and a
baked albedo.

Spec: docs/superpowers/specs/2026-11-02-grim-actor-forge/ (01 art direction, 03 animation model).

World is u (16 u = 1 m), x and y on the ground, z up, origin = foot point, the same as isoforge.

Parts:

1. Skeleton, Pose, Key, Clip and sample(): exact arithmetic on the integer frame index.
   rigid_local(): a rigid part's mesh in its joint's local space (what a glTF node holds).
2. bind() and skin(): two bone influences per vertex (secondary path, for skinned actors such as the wolf).
3. unwrap(): one cylindrical or planar projection per part, shelf-packed into one atlas.
4. bake_albedo(): VIEW-INDEPENDENT response only (albedo, AO, cavity, wear, soot, rust, dust). No key light is
   baked: the engine applies its own ambient + N.L, and a baked key would turn with the model.

Everything random goes through uiart.rng (waves3 does that by name), so the same input gives the same bytes.
"""
from __future__ import annotations

import math
from dataclasses import dataclass, field

import numpy as np

import forge
import isoforge
import uiart as U
from forge import smooth
from isoforge import IsoMat, Mesh, _rotation, _smooth_normals, waves3

F32 = np.float32
TAU = math.tau
GUTTER = 4  # px of dilated colour around every atlas rectangle
BLEND_U = 0.8  # skin weight transition length, world u
CHAR_RUST_CAP = 0.34  # saturation ceiling for rust texels (props use 0.44), spec 01 palette table
# Spec 01 decision 10: actors run grime up and rust down against the prop default .3/.5/.2/.1
ACTOR_AGE = dict(wear=0.35, grime=0.65, rust=0.12, dust=0.20)


# 1. skeleton and clips -------------------------------------------------------------------


@dataclass(frozen=True)
class Joint:
    name: str
    parent: str | None  # parents must appear before children
    offset: tuple  # bind translation from the parent, world u


@dataclass(frozen=True)
class Skeleton:
    joints: tuple

    def __post_init__(self):
        seen = set()
        for j in self.joints:
            if j.name in seen:
                raise ValueError(f"duplicate joint {j.name}")
            if j.parent is not None and j.parent not in seen:
                raise ValueError(f"joint {j.name}: parent {j.parent} must appear before its child")
            seen.add(j.name)

    def index(self, name) -> int:
        for i, j in enumerate(self.joints):
            if j.name == name:
                return i
        raise KeyError(name)

    def bind_world(self) -> dict:
        """4x4 float64 per joint. The bind pose has no rotation, so each matrix is a pure translation."""
        out = {}
        for j in self.joints:
            T = np.eye(4)
            T[:3, 3] = j.offset
            out[j.name] = T if j.parent is None else out[j.parent] @ T
        return out


@dataclass(frozen=True)
class Pose:
    rot: dict = field(default_factory=dict)  # joint -> (rx, ry, rz) degrees, local to the parent, x then y then z
    root_move: tuple = (0.0, 0.0, 0.0)


@dataclass(frozen=True)
class Key:
    t: float  # 0..1 along the clip
    pose: Pose
    ease: str = "smooth"  # "linear" | "smooth" (smoothstep) | "hold" (step)


@dataclass(frozen=True)
class Clip:
    name: str
    keys: tuple  # t ascending, t=0 required
    frames: int
    frame_ms: int  # uniform frame time
    loop: bool = True
    durations: tuple = ()  # optional per-frame ms, else frame_ms repeated


def _ease(kind: str, a: float) -> float:
    if kind == "linear":
        return a
    if kind == "smooth":
        return a * a * (3 - 2 * a)
    if kind == "hold":
        return 0.0
    raise ValueError(f"unknown ease {kind!r}")


def _blend(p0: Pose, p1: Pose, u: float) -> Pose:
    # Linear on Euler angles, componentwise. Keys are hand-authored <= 90 degrees apart, so this is adequate.
    # Do NOT "fix" it with quaternions: that changes every byte of every clip (spec 03 decision 10).
    zero = (0.0, 0.0, 0.0)
    rot = {}
    for n in sorted(set(p0.rot) | set(p1.rot)):
        a, b = p0.rot.get(n, zero), p1.rot.get(n, zero)
        rot[n] = tuple(float(x + (y - x) * u) for x, y in zip(a, b))
    rm = tuple(float(x + (y - x) * u) for x, y in zip(p0.root_move, p1.root_move))
    return Pose(rot, rm)


def sample(clip: Clip, i: int) -> Pose:
    """Pose of frame i. t = i / frames for a loop (frame `frames` would equal frame 0 and is never emitted),
    t = i / (frames - 1) for a once clip (the last frame lands on the final key). Integer index in, no
    accumulation. A loop whose last key is before t=1 blends from it back to the first key's pose."""
    t = i / clip.frames if clip.loop else (i / (clip.frames - 1) if clip.frames > 1 else 0.0)
    keys = list(clip.keys)
    if clip.loop and keys[-1].t < 1.0:
        keys.append(Key(1.0, keys[0].pose, keys[-1].ease))
    j = 0
    for k in range(len(keys)):
        if keys[k].t <= t:
            j = k
    if j == len(keys) - 1 or keys[j].t == t:
        return keys[j].pose
    a = (t - keys[j].t) / (keys[j + 1].t - keys[j].t)
    return _blend(keys[j].pose, keys[j + 1].pose, _ease(keys[j].ease, a))


def _mesh(part) -> Mesh:
    return part[0] if isinstance(part, tuple) else part


def rigid_local(part, skeleton: Skeleton, joint: str) -> Mesh:
    """Rigid path (no skin, no weights): the part's mesh in the LOCAL space of `joint`, i.e. vertices minus the
    joint's bind world position, which is what a glTF node holds while its TRS places it. Bind has no rotation,
    so this is a pure translation: normals, uv, tint and topology are untouched. The node's translation is the
    joint's offset from its parent, so world = parent chain of offsets + local v, exactly the bind mesh."""
    m = _mesh(part)
    o = skeleton.bind_world()[joint][:3, 3]
    out = Mesh(m.v.astype(np.float64) - o, m.f.copy(), m.n, m.uv, m.tint)
    out.rest = m.v.copy()
    return out


# 2. skinning (secondary) -----------------------------------------------------------------


@dataclass
class Skin:
    i0: np.ndarray  # int32 joint index, nearest
    i1: np.ndarray  # int32 joint index, second nearest
    w: np.ndarray  # float32 weight of i1; i0 gets 1 - w. 0 for a rigid vertex


def _seg_dist(P: np.ndarray, a, b) -> np.ndarray:
    a, b = np.asarray(a, dtype=np.float64), np.asarray(b, dtype=np.float64)
    ab = b - a
    t = np.clip(((P - a) @ ab) / max(float(ab @ ab), 1e-12), 0.0, 1.0)
    return np.linalg.norm(P - (a + t[:, None] * ab), axis=1)


def bind(mesh: Mesh, skeleton: Skeleton, bones: tuple, blend: float = BLEND_U) -> Skin:
    """bones = ((joint_name, p0, p1, radius), ...) in bind-pose world u. Per vertex the two nearest bone segments
    by point-to-segment distance, taken from two DIFFERENT joints (two segments of one joint would give no
    blend; with one segment per joint this is exactly the spec). Weight on the second joint is
    0.5 * (1 - smoothstep(0, 1, (d1 - d0) / blend)): 0.5 where the two are equidistant, 0 once the nearer one is
    `blend` closer. The spec's literal `w = smooth(...)` is 0 at equidistance, which would jump there; this is
    continuous. radius is carried for authoring but does not enter the distance."""
    P = mesh.v.astype(np.float64)
    J = len(skeleton.joints)
    D = np.full((len(P), J), np.inf)
    for name, p0, p1, _radius in bones:
        j = skeleton.index(name)
        D[:, j] = np.minimum(D[:, j], _seg_dist(P, p0, p1))
    order = np.argsort(D, axis=1, kind="stable")
    r = np.arange(len(P))
    i0 = order[:, 0]
    i1 = order[:, 1] if J > 1 else order[:, 0]
    d0, d1 = D[r, i0], D[r, i1]
    one = ~np.isfinite(d1)  # a single bound joint: rigid
    i1 = np.where(one, i0, i1)
    s = smooth(0.0, 1.0, np.where(one, 1.0, (d1 - d0) / blend))
    return Skin(i0.astype(np.int32), i1.astype(np.int32), (0.5 * (1.0 - s)).astype(F32))


def _t4(t) -> np.ndarray:
    T = np.eye(4)
    T[:3, 3] = t
    return T


def skin_matrices(skeleton: Skeleton, pose: Pose) -> np.ndarray:
    """(J, 4, 4) world-posed times inverse-bind, parent to child. Root joints also take pose.root_move."""
    world, out = {}, np.empty((len(skeleton.joints), 4, 4))
    bw = skeleton.bind_world()
    for k, j in enumerate(skeleton.joints):
        R4 = np.eye(4)
        rot = pose.rot.get(j.name)
        if rot is not None:
            R4[:3, :3] = _rotation(rot)
        off = np.asarray(j.offset, dtype=np.float64)
        if j.parent is None:
            off = off + np.asarray(pose.root_move, dtype=np.float64)
        world[j.name] = _t4(off) @ R4 if j.parent is None else world[j.parent] @ _t4(off) @ R4
        inv = np.eye(4)
        inv[:3, 3] = -bw[j.name][:3, 3]  # bind has no rotation, so the inverse is exact
        out[k] = world[j.name] @ inv
    return out


def skin(skeleton: Skeleton, pose: Pose, parts, skins) -> list[Mesh]:
    """Linear blend skinning of v only. Normals are recomputed from the posed vertices with _smooth_normals
    (flat meshes keep n = None); blending stored normals under LBS makes them non-orthogonal. Each result carries
    `.rest`, the bind-pose v. Written as M0 v + w (M1 v - M0 v) so an identity pose returns v exactly."""
    M = skin_matrices(skeleton, pose)
    out = []
    for part, sk in zip(parts, skins):
        m = _mesh(part)
        v = m.v.astype(np.float64)
        r = np.arange(len(v))
        A = np.einsum("jab,nb->jna", M[:, :3, :3], v) + M[:, None, :3, 3]  # (J, N, 3), every joint's view of v
        p0, p1 = A[sk.i0, r], A[sk.i1, r]
        vp = p0 + sk.w.astype(np.float64)[:, None] * (p1 - p0)
        npos = None if m.n is None else _smooth_normals(vp, m.f)
        res = Mesh(vp, m.f.copy(), npos, m.uv, m.tint)
        res.rest = m.v.copy() if getattr(m, "rest", None) is None else m.rest
        out.append(res)
    return out


# 3. unwrap -------------------------------------------------------------------------------


@dataclass(frozen=True)
class Rect:
    """Atlas rectangle in pixels, EXCLUDING the gutter (the gutter is GUTTER px outside every side).
    cyl: u wraps around the rectangle's width (a cylindrical part). Iterates as (x, y, w, h)."""

    x: int
    y: int
    w: int
    h: int
    cyl: bool = False

    def __iter__(self):
        return iter((self.x, self.y, self.w, self.h))


def _canon(e: np.ndarray) -> np.ndarray:
    return e * (1.0 if e[np.argmax(np.abs(e))] >= 0 else -1.0)


def _project(mesh: Mesh):
    return _project_ex(mesh)[:4]


def _project_ex(mesh: Mesh, axis=None):
    """Flat parts (thinnest axis under 1/4 of the middle one) get a planar projection along it; everything else
    a cylinder around the longest axis (a blob is a short fat cylinder). Returns coords in u as (x, y), the
    size in u, and whether x wraps. _project_ex adds a fifth item: (per-vertex distance from the cylinder axis in u,
    mean radius), which unwrap_split uses to find the poles; (None, 0.0) for a planar part.
    `axis` (a 3-vector) forces a cylinder about that direction instead: PCA picks the longest extent, which for a
    short wide ring such as a belt is a horizontal axis through its side wall, not the loft axis it was built on."""
    v = mesh.v.astype(np.float64)
    d = v - v.mean(axis=0)
    if axis is not None:
        e0 = np.asarray(axis, dtype=np.float64)
        e0 = e0 / np.linalg.norm(e0)
        helper = np.array((1.0, 0.0, 0.0)) if abs(e0[0]) < 0.9 else np.array((0.0, 1.0, 0.0))
        e1 = np.cross(helper, e0)
        e1 /= np.linalg.norm(e1)
        e2 = np.cross(e0, e1)
        ev = np.array((1.0, 1.0, 1.0))
    else:
        ev, E = np.linalg.eigh(d.T @ d / max(len(d), 1))
        e2, e1, e0 = (_canon(E[:, k]) for k in range(3))  # minor, middle, major
        ev = np.maximum(ev, 0.0)
    a0, a1 = d @ e0, d @ e1
    if ev[0] < ev[1] / 16.0:
        x, y = a0 - a0.min(), a1 - a1.min()
        return np.stack((x, y), 1), max(x.max(), 1e-3), max(y.max(), 1e-3), False, (None, 0.0)
    a2 = d @ e2
    ang = np.arctan2(a2, a1)
    rv = np.hypot(a1, a2)
    rad = max(float(rv.mean()), 0.25)
    y = a0 - a0.min()
    w = TAU * rad
    return np.stack(((ang / TAU) % 1.0 * w, y), 1), w, max(y.max(), 1e-3), True, (rv, rad)


def _shelf(sizes, tex: int):
    """Shelf-pack padded sizes, tallest first. Returns top-left positions or None when they do not fit."""
    order = sorted(range(len(sizes)), key=lambda i: (-sizes[i][1], i))
    pos, x, y, rowh = [None] * len(sizes), 0, 0, 0
    for i in order:
        w, h = sizes[i]
        if x + w > tex:
            x, y, rowh = 0, y + rowh, 0
        if y + h > tex or w > tex:
            return None
        pos[i] = (x, y)
        x, rowh = x + w, max(rowh, h)
    return pos


def unwrap(parts, tex_size: int):
    """-> (uvs_per_part, rects). One rectangle per part, GUTTER px around each, non-overlapping. The texel
    density is the largest (<= 16 px/u) at which every part fits. uv is per original vertex, normalised to the
    atlas with v = row / tex_size (rows top down, yDown, so a loader must not flip V).

    Known limits, deliberate for a crude unwrap: a cylinder's seam is not split, so triangles that straddle it
    carry a u jump (bake_albedo handles that; an exporter sampling these uvs directly would smear that one
    column of triangles unless the mesh was pre-split there); the two sides of a planar plate overlap."""
    meshes = [_mesh(p) for p in parts]
    proj = [_project(m) for m in meshes]
    d = 16.0
    while True:
        sizes = [(max(4, math.ceil(w * d)) + 2 * GUTTER, max(4, math.ceil(h * d)) + 2 * GUTTER) for _, w, h, _ in proj]
        pos = _shelf(sizes, tex_size)
        if pos is not None:
            break
        d *= 0.93
        if d < 0.05:
            raise ValueError("parts do not fit the atlas")
    uvs, rects = [], []
    for (xy, wu, hu, cyl), (pw, ph), (px, py) in zip(proj, sizes, pos):
        rw, rh = pw - 2 * GUTTER, ph - 2 * GUTTER
        rect = Rect(px + GUTTER, py + GUTTER, rw, rh, cyl)
        tx = rect.x + xy[:, 0] * (rw / wu)
        ty = rect.y + xy[:, 1] * (rh / hu)
        uvs.append((np.stack((tx, ty), 1) / tex_size).astype(F32))
        rects.append(rect)
    return uvs, rects


def _split_cylinder(mesh: Mesh, xy: np.ndarray, w: float, rv: np.ndarray):
    """Cut a cylinder-projected mesh open along its seam, so no triangle spans the u wrap.

    Two repairs, both leaving the surface itself exactly where it was:
    - a triangle with a corner near the cylinder axis (a cap fan apex has no real angle) gets its own copy of that
      corner whose u is the circular mean of the other two, so it does not smear across the whole cap;
    - a triangle that straddles the seam (u spread over half the width) is clipped against u = w into a left
      and a right piece. The cut vertices sit on the original edge, one copy at u = w and one at u = 0, so the
      texture is continuous across the seam and the geometry is unchanged (the clipped areas sum to the original).
    Returns (Mesh, xy) with the new vertices appended; the original vertices keep their indices."""
    v = mesh.v.astype(np.float64)
    nrm = None if mesh.n is None else mesh.n.astype(np.float64)
    V, X, N = list(v), [tuple(r) for r in xy], (list(nrm) if nrm is not None else None)
    tin = None if mesh.tint is None else mesh.tint.astype(np.float64)
    T = None if tin is None else list(tin)
    cache: dict = {}
    out: list = []
    xc: dict = {}

    def clone(i, x):
        V.append(v[i].copy())
        X.append((x, xy[i][1]))
        if N is not None:
            N.append(nrm[i].copy())
        if T is not None:
            T.append(tin[i].copy())
        return len(V) - 1

    def cut(a, b, side):
        lo, hi = (a, b) if a < b else (b, a)
        key = (lo, hi, side)
        if key in cache:
            return cache[key]
        t = (w - xc[lo]) / (xc[hi] - xc[lo])
        V.append(V[lo] + (V[hi] - V[lo]) * t)
        X.append((w if side == "L" else 0.0, X[lo][1] + (X[hi][1] - X[lo][1]) * t))
        if N is not None:
            q = N[lo] + (N[hi] - N[lo]) * t
            N.append(q / max(float(np.linalg.norm(q)), 1e-12))
        if T is not None:
            T.append(T[lo] + (T[hi] - T[lo]) * t)
        cache[key] = len(V) - 1
        return cache[key]

    def clip(poly, keep_left):
        res = []
        for k in range(len(poly)):
            a, b = poly[k], poly[(k + 1) % len(poly)]
            ina, inb = (xc[a] <= w) == keep_left, (xc[b] <= w) == keep_left
            if ina:
                res.append(a)
            if ina != inb:
                res.append(cut(a, b, "L" if keep_left else "R"))
        return res

    for tri in mesh.f.tolist():
        ids = list(tri)
        # a pole apex: the corner nearest the axis, well inside the other two, in a triangle whose u is spread
        # enough to smear (a cap fan; the axis is the PCA one, so a cap centre is rarely exactly on it)
        k = min(range(3), key=lambda j: rv[ids[j]])
        others = [rv[ids[j]] for j in range(3) if j != k]
        xs0 = [xy[i][0] for i in ids]
        if rv[ids[k]] < 0.5 * min(others) and max(xs0) - min(xs0) > w / 6:
            xa, xb = (xy[ids[j]][0] for j in range(3) if j != k)
            if abs(xa - xb) > w / 2:
                xa, xb = (xa + w, xb) if xa < xb else (xa, xb + w)
            ids[k] = clone(ids[k], ((xa + xb) / 2) % w)
        xs = [X[i][0] for i in ids]
        if max(xs) - min(xs) <= w / 2:
            out.append(ids)
            continue
        top = max(xs)
        for i in ids:
            xc[i] = X[i][0] + w if X[i][0] < top - w / 2 else X[i][0]
        for keep_left in (True, False):
            poly = clip(ids, keep_left)
            for a in range(1, len(poly) - 1):
                out.append([poly[0], poly[a], poly[a + 1]])
    m = Mesh(np.array(V), np.array(out, dtype=np.int32).reshape(-1, 3), None if N is None else np.array(N), None,
             None if T is None else np.array(T))
    return m, np.array(X, dtype=np.float64)


def unwrap_split(parts, tex_size: int, axis=None):
    """unwrap() with the cylinder seams actually cut (see _split_cylinder); texel density, packing and Rect are
    identical to unwrap(). Returns (meshes, uvs, rects): `meshes` are the parts' meshes with the seam and pole
    vertices added (planar parts come back unchanged); uvs[i] is per vertex of meshes[i]. Use THESE meshes, not
    the originals, for bake_albedo and for export: the uvs only mean something on the split topology. Where
    unwrap() leaves one column of triangles whose u jumps across the atlas rectangle, this has none.
    `axis` forces every part to a cylinder about that direction (a 3-vector, or a list with one vector or None
    per part); None keeps unwrap()'s own choice. Use it for parts built as lofts along a known axis."""
    meshes = [_mesh(p) for p in parts]
    if axis is None or np.ndim(axis[0]) == 0:
        axes = [axis] * len(meshes)
    else:
        axes = list(axis)
    proj = [_project_ex(m, a) for m, a in zip(meshes, axes)]
    split, coords = [], []
    for m, (xy, wu, hu, cyl, (rv, _)) in zip(meshes, proj):
        if cyl:
            m2, xy2 = _split_cylinder(m, xy, wu, rv)
            split.append(m2)
            coords.append((xy2, wu, hu, True))
        else:
            split.append(m)
            coords.append((xy, wu, hu, False))
    d = 16.0
    while True:
        sizes = [(max(4, math.ceil(w * d)) + 2 * GUTTER, max(4, math.ceil(h * d)) + 2 * GUTTER) for _, w, h, _ in coords]
        pos = _shelf(sizes, tex_size)
        if pos is not None:
            break
        d *= 0.93
        if d < 0.05:
            raise ValueError("parts do not fit the atlas")
    uvs, rects = [], []
    for (xy, wu, hu, cyl), (pw, ph), (px, py) in zip(coords, sizes, pos):
        rw, rh = pw - 2 * GUTTER, ph - 2 * GUTTER
        rect = Rect(px + GUTTER, py + GUTTER, rw, rh, cyl)
        tx = rect.x + xy[:, 0] * (rw / wu)
        ty = rect.y + xy[:, 1] * (rh / hu)
        uvs.append((np.stack((tx, ty), 1) / tex_size).astype(F32))
        rects.append(rect)
    return split, uvs, rects


# 4. baked albedo -------------------------------------------------------------------------

# Spec 01 palette. These live under `grim.*` in tokens.json once the architect lands them; until then the
# spec's review sRGB values are the fallback, so the generator reads the token first.
GRIM_HEX = {
    "grim.iron.black": "#17181A", "grim.iron.pitted": "#212022", "grim.steel.bare": "#3A3B3E",
    "grim.bone": "#4A4740", "grim.bone.old": "#33302B", "grim.leather": "#241C16",
    "grim.cloth.dark": "#1A1720", "grim.cloth.ash": "#2A2724", "grim.flesh.grey": "#3B332E",
    "grim.horn": "#282018", "grim.stone.wet": "#1E2022", "grim.rust": "#4A2A16", "grim.blood.old": "#3A1416",
}
DUST_HEX = "#3A3631"  # dim warm grey, never lighter than bone


def _lin(name: str) -> np.ndarray:
    try:
        c = U.token(name)
    except KeyError:
        c = U.token(GRIM_HEX[name] if name in GRIM_HEX else name)
    return U.srgb_to_linear(c).astype(np.float64)


def _mat(token: str, rough: float, f0: float, cap: float, metal: bool = False, wear: float = 1.0) -> IsoMat:
    bare = tuple(_lin("grim.steel.bare")) if metal else None
    return IsoMat(tuple(_lin(token)), (f0,) * 3, rough, bare, (0.22,) * 3 if metal else None, wear, None, cap)


GRIM_MATS: dict[str, IsoMat] = {
    "grim.iron.black": _mat("grim.iron.black", 0.62, 0.035, 0.22, True),
    "grim.iron.pitted": _mat("grim.iron.pitted", 0.72, 0.030, 0.24, True),
    "grim.steel.bare": _mat("grim.steel.bare", 0.44, 0.22, 0.22, True),
    "grim.bone": _mat("grim.bone", 0.80, 0.028, 0.24, wear=0.6),
    "grim.bone.old": _mat("grim.bone.old", 0.88, 0.025, 0.26, wear=0.6),
    "grim.leather": _mat("grim.leather", 0.84, 0.035, 0.26, wear=0.8),
    "grim.cloth.dark": _mat("grim.cloth.dark", 0.92, 0.030, 0.24, wear=0.5),
    "grim.cloth.ash": _mat("grim.cloth.ash", 0.94, 0.028, 0.22, wear=0.5),
    "grim.flesh.grey": _mat("grim.flesh.grey", 0.86, 0.032, 0.28, wear=0.4),
    "grim.horn": _mat("grim.horn", 0.70, 0.040, 0.26, wear=0.7),
    "grim.stone.wet": _mat("grim.stone.wet", 0.78, 0.032, 0.22, wear=0.5),
}


MAX_OCCLUDERS = 12000   # triangles that occlude in the AO pass; above this a fixed stride thins them


def _vertex_fields(meshes: list[Mesh]):
    """Per-vertex view-independent fields for every part, in bind pose.
    ao: isotropic occlusion by every triangle of the whole figure within 4 u (solid angle of each triangle seen
        from the vertex, cosine weighted, so parts shade one another at joints). 1 open .. 0 closed.
    kappa: welded-adjacency Laplacian curvature along the vertex normal, + convex (rims, ridges), - concave.
    Neither depends on a light direction."""
    verts = [m.v.astype(np.float64) for m in meshes]
    norms = [(m.n.astype(np.float64) if m.n is not None else _smooth_normals(m.v, m.f).astype(np.float64)) for m in meshes]
    cent, cn, area = [], [], []
    for m, v in zip(meshes, verts):
        tri = v[m.f]
        fn = np.cross(tri[:, 1] - tri[:, 0], tri[:, 2] - tri[:, 0])
        ln = np.linalg.norm(fn, axis=1)
        keep = ln > 1e-9
        cent.append(tri.mean(axis=1)[keep])
        cn.append(fn[keep] / ln[keep, None])
        area.append(0.5 * ln[keep])
    cent, cn, area = np.concatenate(cent), np.concatenate(cn), np.concatenate(area)
    if len(cent) > MAX_OCCLUDERS:
        # A huge prop (a tree of 50k triangles) would cost vertices x triangles. Every k-th triangle stands in for
        # k of them, with k times the area: a fixed stride, so it is deterministic and the player (about 2k) never gets here.
        stride = -(-len(cent) // MAX_OCCLUDERS)
        cent, cn, area = cent[::stride], cn[::stride], area[::stride] * stride
    r2, chunk = 4.0 * 4.0, max(8, 1_500_000 // max(len(cent), 1))
    out = []
    for m, v, nv in zip(meshes, verts, norms):
        ao = np.empty(len(v))
        for a in range(0, len(v), chunk):
            dv = cent[None] - v[a : a + chunk, None]
            d2 = (dv * dv).sum(-1)
            dh = dv / (np.sqrt(d2) + 1e-9)[..., None]
            c1 = np.clip((dh * nv[a : a + chunk, None]).sum(-1), 0, 1)
            c2 = np.clip(-(dh * cn[None]).sum(-1), 0, 1)
            om = c1 * c2 * area[None] / (math.pi * (d2 + area[None])) * np.clip(1 - d2 / r2, 0, 1)
            ao[a : a + chunk] = om.sum(1)
        ao = np.clip(1.0 - 1.5 * ao, 0.0, 1.0)
        # curvature on position-welded groups, so a cap and the tube it closes see each other
        key = np.round(v * 2000).astype(np.int64)
        _, g = np.unique(key, axis=0, return_inverse=True)
        g = g.reshape(-1)
        ng = int(g.max()) + 1
        cnt = np.maximum(np.bincount(g, minlength=ng), 1)
        G = np.stack([np.bincount(g, weights=v[:, c], minlength=ng) / cnt for c in range(3)], 1)
        gn = np.stack([np.bincount(g, weights=nv[:, c], minlength=ng) for c in range(3)], 1)
        gn /= np.maximum(np.linalg.norm(gn, axis=1, keepdims=True), 1e-12)
        e = np.concatenate((m.f[:, [0, 1]], m.f[:, [1, 2]], m.f[:, [2, 0]]))
        e = np.unique(np.sort(g[e], axis=1), axis=0)
        e = e[e[:, 0] != e[:, 1]]
        a_, b_ = np.concatenate((e[:, 0], e[:, 1])), np.concatenate((e[:, 1], e[:, 0]))
        deg = np.bincount(a_, minlength=ng)
        nb = np.stack([np.bincount(a_, weights=G[b_, c], minlength=ng) for c in range(3)], 1) / np.maximum(deg, 1)[:, None]
        h = max(float(np.median(np.linalg.norm(G[a_] - G[b_], axis=1))) if len(a_) else 1.0, 1e-3)
        kappa_g = -2.0 * ((nb - G) * gn).sum(1) / (h * h) * np.where(deg > 0, 1.0, 0.0)
        out.append((ao, kappa_g[g], nv))
    return out


def _sat_cap(rgb: np.ndarray, cap: np.ndarray) -> np.ndarray:
    """Soft-knee HSV saturation ceiling on sRGB [0,1]; value untouched, so S never exceeds cap."""
    mx, mn = rgb.max(1), rgb.min(1)
    s = (mx - mn) / np.maximum(mx, 1e-6)
    k = cap * 0.8
    s2 = np.where(s <= k, s, k + (cap - k) * np.tanh((s - k) / np.maximum(cap - k, 1e-6)))
    f = np.where(s > 1e-6, s2 / np.maximum(s, 1e-6), 1.0)
    return mx[:, None] - (mx[:, None] - rgb) * f[:, None]


def _shade(P, N, ao, kappa, mat: IsoMat, seed: str, wear, grime, rust, dust, tint=None):
    """sRGB colour for texels of one part. P and N are bind-pose object space, so the noise sticks to the
    model. Wavelengths keep to the form bands: secondary 2..6 u, tertiary < 1 u, plus a long 5..14 u field."""
    wv = lambda name, n, lo, hi: waves3(P, f"charforge.{seed}.{name}", n, lo, hi)[0]
    cav = np.maximum(np.clip(-kappa / 0.8, 0, 1), smooth(0.15, 0.55, 1 - ao))
    edge = smooth(0.30, 0.60, kappa)
    col = np.asarray(mat.albedo, dtype=np.float64)[None, :] * (1 + 0.12 * np.tanh(wv("grain", 8, 0.4, 0.9)) + 0.08 * np.tanh(wv("tone", 6, 5.0, 14.0)))[:, None]
    if tint is not None:
        col = col * tint   # the mesh's per-vertex tint (isoforge: albedo x tint), e.g. rust patches or plate-to-plate darkness
    # edge wear: convex x a large-scale use field x a broken-up threshold (forge.age's recipe)
    use = 0.5 + 0.5 * np.tanh(wv("use", 10, 5.0, 14.0) * 1.1)
    thr = 0.98 - wear * 0.8
    gate = smooth(thr - 0.10, thr + 0.22, use)
    grit = 0.5 + 0.5 * np.tanh(wv("grit", 8, 0.4, 0.9))
    bare = smooth(0.40, 0.62, edge * gate * (0.45 + 0.85 * grit) * mat.wear)
    if mat.bare_albedo is not None:
        col = col * (1 - bare[:, None]) + np.asarray(mat.bare_albedo)[None, :] * bare[:, None]
    else:
        col = col * (1 + 0.35 * bare[:, None])  # frayed or scuffed material just lifts
    # soot in cavities, matte and dark
    soot = np.clip(smooth(0.18, 0.70, cav * 1.5 + 0.4 * (1 - ao)) * grime, 0, 1) * (1 - 0.7 * bare)
    col = col * (1 - 0.65 * soot)[:, None]
    # rust pools in cavities, metals only, scaled by the (low) actor rust budget
    rmask = np.zeros(len(P))
    if mat.bare_albedo is not None:
        rn = 0.5 + 0.5 * np.tanh(wv("rust", 10, 2.5, 6.0))
        damp = smooth(0.66, 0.90, 0.5 + 0.5 * np.tanh(wv("damp", 8, 8.0, 20.0) * 1.2))
        cavx = np.maximum(cav, smooth(0.15, 0.5, 1 - ao))
        rthr = 0.62 - rust * 0.8
        rmask = smooth(rthr, rthr + 0.16, rn) * smooth(0.5, 0.92, cavx) * damp * (1 - bare) * min(1.0, rust * 3)
        rcol = _lin("grim.rust")[None, :] * (0.40 + 0.8 * rn[:, None])
        col = col * (1 - rmask[:, None]) + rcol * rmask[:, None]
    # dust on up-facing slopes (object-space up; view independent)
    up = np.clip(N[:, 2] * 1.6 - 0.15, 0, 1)
    dn = 0.5 + 0.5 * np.tanh(wv("dust", 6, 2.5, 5.0))
    dmask = np.clip(up * smooth(0.35, 0.8, dn) * dust * 2.2 * (1 - bare) * ao, 0, 1)
    col = col * (1 - dmask[:, None]) + _lin(DUST_HEX)[None, :] * dmask[:, None]
    # occlusion and cavity darkening, multiplied into the base colour
    col = col * (0.30 + 0.70 * ao)[:, None] * (1 - 0.45 * cav)[:, None]
    cap = 0.92 * mat.sat_cap + (CHAR_RUST_CAP - 0.92 * mat.sat_cap) * np.clip(rmask * 3.0, 0, 1)
    srgb = U.linear_to_srgb(np.clip(col, 0, 1)).astype(np.float64)
    return _sat_cap(srgb, cap)


def _raster(uvpx: np.ndarray, f: np.ndarray, rect: Rect):
    """Texel centres covered by each triangle. Returns atlas (y, x), triangle id and barycentrics (n, 3).
    Cylinder rects wrap u: a triangle with a corner more than half a width away is shifted across the seam."""
    ys, xs, ts, bs = [], [], [], []
    for t in range(len(f)):
        Q = uvpx[f[t]].copy()
        if rect.cyl:
            rel = Q[:, 0] - rect.x
            Q[rel < rel.max() - rect.w / 2, 0] += rect.w
        (x0, y0), (x1, y1), (x2, y2) = Q
        den = (y1 - y2) * (x0 - x2) + (x2 - x1) * (y0 - y2)
        if abs(den) < 1e-9:
            continue
        gx = np.arange(math.floor(Q[:, 0].min()), math.ceil(Q[:, 0].max()) + 1)
        gy = np.arange(math.floor(Q[:, 1].min()), math.ceil(Q[:, 1].max()) + 1)
        X, Y = np.meshgrid(gx + 0.5, gy + 0.5)
        l0 = ((y1 - y2) * (X - x2) + (x2 - x1) * (Y - y2)) / den
        l1 = ((y2 - y0) * (X - x2) + (x0 - x2) * (Y - y2)) / den
        l2 = 1 - l0 - l1
        ok = (l0 >= -1e-6) & (l1 >= -1e-6) & (l2 >= -1e-6)
        if not ok.any():
            continue
        px = np.floor(X[ok]).astype(np.int64)
        if rect.cyl:
            px = rect.x + (px - rect.x) % rect.w
        py = np.floor(Y[ok]).astype(np.int64)
        ys.append(py), xs.append(px), ts.append(np.full(len(py), t)), bs.append(np.stack((l0[ok], l1[ok], l2[ok]), 1))
    if not ys:
        return (np.zeros(0, np.int64),) * 3 + (np.zeros((0, 3)),)
    return np.concatenate(ys), np.concatenate(xs), np.concatenate(ts), np.concatenate(bs)


def _fill(img: np.ndarray, cov: np.ndarray, x0: int, y0: int, x1: int, y1: int, base: np.ndarray) -> None:
    """Dilate colour into every uncovered texel of the padded rectangle (gutter and holes), in place."""
    a, c = img[y0:y1, x0:x1], cov[y0:y1, x0:x1].copy()
    if not c.any():
        a[:] = base
        cov[y0:y1, x0:x1] = True
        return
    for _ in range(max(a.shape)):
        if c.all():
            break
        pa = np.pad(a * c[..., None], ((1, 1), (1, 1), (0, 0)))
        pc = np.pad(c.astype(np.float64), 1)
        h, w = c.shape
        acc, cnt = np.zeros_like(a), np.zeros(c.shape)
        for dy in (0, 1, 2):
            for dx in (0, 1, 2):
                if dy == 1 and dx == 1:
                    continue
                acc += pa[dy : dy + h, dx : dx + w]
                cnt += pc[dy : dy + h, dx : dx + w]
        new = ~c & (cnt > 0)
        a[new] = acc[new] / cnt[new][:, None]
        c |= new
    cov[y0:y1, x0:x1] = True


def bake_albedo(parts, skins, skeleton: Skeleton, uvs, rects, tex_size: int, materials=None, seed: str = "charforge",
                wear: float = ACTOR_AGE["wear"], grime: float = ACTOR_AGE["grime"], rust: float = ACTOR_AGE["rust"],
                dust: float = ACTOR_AGE["dust"]) -> np.ndarray:
    """uint8 RGBA base colour atlas, evaluated in BIND pose. Parts are bind-pose world-space meshes (a posed mesh
    from skin() is mapped back through its `.rest`); skins may be None entries for rigid parts. Material of part
    i: materials[i] if given, else a `(Mesh, material)` part's second item, else grim.iron.black; a material is
    a GRIM_MATS name or an IsoMat. View-independent only: no directional light is baked. Alpha is 255 inside
    every padded rectangle, 0 elsewhere."""
    meshes = [_mesh(p) for p in parts]
    bindm = []
    for m in meshes:
        rest = getattr(m, "rest", None)
        bindm.append(Mesh(m.v if rest is None else rest, m.f, m.n, m.uv, m.tint))
    if skins is not None and all(s is not None for s in skins):
        bindm = skin(skeleton, Pose(), bindm, skins)  # identity pose = bind, exact
    fields = _vertex_fields(bindm)
    out = np.zeros((tex_size, tex_size, 3), dtype=np.float64)
    cov = np.zeros((tex_size, tex_size), dtype=bool)
    mats = []
    for i, p in enumerate(parts):
        m = materials[i] if materials is not None else (p[1] if isinstance(p, tuple) and len(p) > 1 else "grim.iron.black")
        mats.append(GRIM_MATS[m] if isinstance(m, str) else m)
    for i, (m, (ao, kappa, nv), uv, rect, mat) in enumerate(zip(bindm, fields, uvs, rects, mats)):
        uvpx = np.asarray(uv, dtype=np.float64) * tex_size
        ty, tx, tri, bary = _raster(uvpx, m.f, rect)
        if len(ty):
            flat = ty * tex_size + tx  # last triangle wins where two overlap, deterministic
            _, first = np.unique(flat[::-1], return_index=True)
            sel = len(flat) - 1 - first
            ty, tx, tri, bary = ty[sel], tx[sel], tri[sel], bary[sel]
            fi = m.f[tri]
            lerp = lambda a: np.einsum("nk,nkc->nc", bary, a[fi].reshape(len(fi), 3, -1))
            P = lerp(m.v.astype(np.float64))
            N = lerp(nv)
            N /= np.maximum(np.linalg.norm(N, axis=1, keepdims=True), 1e-9)
            tint = None if m.tint is None else np.clip(lerp(m.tint.astype(np.float64)), 0.0, None)
            out[ty, tx] = _shade(P, N, lerp(ao[:, None])[:, 0], lerp(kappa[:, None])[:, 0], mat, f"{seed}.{i}", wear, grime, rust, dust, tint)
            cov[ty, tx] = True
        _fill(out, cov, rect.x - GUTTER, rect.y - GUTTER, rect.x + rect.w + GUTTER, rect.y + rect.h + GUTTER,
              U.linear_to_srgb(np.array(mat.albedo)).astype(np.float64))
    img = np.zeros((tex_size, tex_size, 4), dtype=np.uint8)
    img[..., :3] = np.round(np.clip(out, 0, 1) * 255).astype(np.uint8)
    for r in rects:
        img[r.y - GUTTER : r.y + r.h + GUTTER, r.x - GUTTER : r.x + r.w + GUTTER, 3] = 255
    img[img[..., 3] == 0, :3] = 0
    return img


# demo ------------------------------------------------------------------------------------


def _hsv_report(img: np.ndarray) -> dict:
    px = img[img[..., 3] > 0, :3].astype(np.float64) / 255.0
    mx, mn = px.max(1), px.min(1)
    s = (mx - mn) / np.maximum(mx, 1e-6)
    d = np.maximum(mx - mn, 1e-9)
    r, g, b = px[:, 0], px[:, 1], px[:, 2]
    h = np.where(mx == r, ((g - b) / d) % 6, np.where(mx == g, (b - r) / d + 2, (r - g) / d + 4)) * 60
    bad = (s > 0.12) & (((h > 270) & (h < 345)) | ((h > 85) & (h < 200)))  # magenta/pink, and green through cyan
    return dict(texels=len(px), v_p99=float(np.percentile(mx, 99)), v_max=float(mx.max()), s_p95=float(np.percentile(s, 95)),
                s_max=float(s.max()), frac_s_gt_035=float((s > 0.35).mean()), forbidden_hue_texels=int(bad.sum()))


def _textured(meshes, tex: np.ndarray, uvs, rects, size: int, lift: float) -> np.ndarray:
    """Crude painter's-algorithm side view (looking along +y) sampling the atlas with the per-vertex uvs, with the
    same cylinder seam shift as the baker. Preview only: a soft facing term keeps the shapes readable."""
    img = np.full((size, size, 3), (14, 14, 16), dtype=np.float64)
    sc = size * 0.42 / 17.0
    tris = sorted(((m.v[m.f[t], 1].mean(), mi, t) for mi, m in enumerate(meshes) for t in range(len(m.f))), reverse=True)
    T = tex.shape[0]
    for _, mi, t in tris:
        m, uv, rect = meshes[mi], uvs[mi], rects[mi]
        f = m.f[t]
        v = m.v[f].astype(np.float64)
        sx, sy = size * 0.5 + v[:, 0] * sc, size * 0.88 - v[:, 2] * sc
        u = uv[f].astype(np.float64) * T
        if rect.cyl:
            rel = u[:, 0] - rect.x
            u[rel < rel.max() - rect.w / 2, 0] += rect.w
        den = (sy[1] - sy[2]) * (sx[0] - sx[2]) + (sx[2] - sx[1]) * (sy[0] - sy[2])
        gx = np.arange(max(int(sx.min()), 0), min(int(math.ceil(sx.max())) + 1, size))
        gy = np.arange(max(int(sy.min()), 0), min(int(math.ceil(sy.max())) + 1, size))
        if abs(den) < 1e-9 or not len(gx) or not len(gy):
            continue
        X, Y = np.meshgrid(gx + 0.5, gy + 0.5)
        l0 = ((sy[1] - sy[2]) * (X - sx[2]) + (sx[2] - sx[1]) * (Y - sy[2])) / den
        l1 = ((sy[2] - sy[0]) * (X - sx[2]) + (sx[0] - sx[2]) * (Y - sy[2])) / den
        l2 = 1 - l0 - l1
        ok = (l0 >= 0) & (l1 >= 0) & (l2 >= 0)
        if not ok.any():
            continue
        U_ = l0[ok] * u[0, 0] + l1[ok] * u[1, 0] + l2[ok] * u[2, 0]
        V_ = l0[ok] * u[0, 1] + l1[ok] * u[1, 1] + l2[ok] * u[2, 1]
        if rect.cyl:
            U_ = rect.x + (U_ - rect.x) % rect.w
        px = tex[np.clip(V_.astype(int), 0, T - 1), np.clip(U_.astype(int), 0, T - 1), :3].astype(np.float64)
        fn = np.cross(v[1] - v[0], v[2] - v[0])
        fn /= max(np.linalg.norm(fn), 1e-9)
        img[Y[ok].astype(int), X[ok].astype(int)] = np.clip(px * (0.55 + 0.45 * abs(fn[1])) * lift, 0, 255)
    return img


def main() -> None:
    import hashlib
    from pathlib import Path

    from PIL import Image

    sk = Skeleton((Joint("hip", None, (0, 0, 16)), Joint("knee", "hip", (0, 0, -8)), Joint("ankle", "knee", (4, 0, -6.5))))
    bones = (("hip", (0, 0, 16), (0, 0, 8), 1.6), ("knee", (0, 0, 8), (4, 0, 1.5), 1.2))
    thigh = isoforge.tube([(0, 0, 16), (0, 0, 12), (0, 0, 8)], [1.7, 1.6, 1.3], sides=12, name="demo.thigh")
    shin = isoforge.tube([(0, 0, 8), (2, 0, 4.5), (4, 0, 1.5)], [1.3, 1.0, 0.9], sides=12, name="demo.shin")
    parts = [(thigh, "grim.iron.black"), (shin, "grim.leather")]
    skins = [bind(m, sk, bones) for m, _ in parts]
    print("verts", [len(m.v) for m, _ in parts], "tris", [len(m.f) for m, _ in parts],
          "blended verts", [int(((s.w > 0.01) & (s.i0 != s.i1)).sum()) for s in skins])

    # rigid path: local mesh + the joint's bind position gives back the bind mesh
    for (m, _), jn in zip(parts, ("hip", "knee")):
        loc = rigid_local(m, sk, jn)
        assert np.allclose(loc.v + sk.bind_world()[jn][:3, 3], m.v, atol=1e-5) and loc.n is m.n
    print("rigid_local: local v + joint bind position == bind v")

    # identity pose reproduces the bind mesh exactly
    ident = skin(sk, Pose(), parts, skins)
    for (m, _), res in zip(parts, ident):
        assert np.array_equal(res.v, m.v), "identity skin changed v"
        assert np.array_equal(res.rest, m.v)
        # rim vertices differ: tube() computed them before the caps were merged, the recompute welds the caps in
        key = np.round(m.v * 2000).astype(np.int64)
        _, inv, cnt = np.unique(key, axis=0, return_inverse=True, return_counts=True)
        interior = cnt[inv.reshape(-1)] == 1  # not welded to another vertex (cap rims are)
        assert (np.abs(res.n - m.n).max(1)[interior] < 1e-3).all()
    print("identity pose: v exact, normals match the stored ones on every vertex not welded to a cap rim")

    # clip sampling
    A = Pose({"hip": (0, -10, 0), "knee": (0, 20, 0)})
    B = Pose({"hip": (0, 25, 0), "knee": (0, 70, 0)}, (0, 0, 1.0))
    loop = Clip("demo", (Key(0.0, A), Key(0.5, B, "smooth"), Key(1.0, A, "linear")), 8, 90)
    assert sample(loop, 0) == loop.keys[-1].pose, "loop frame 0 must equal the t=1 key"
    assert sample(loop, 4) == B, "frame 4 of 8 is exactly t=.5"
    once = Clip("once", (Key(0.0, A, "hold"), Key(1.0, B)), 6, 70, loop=False)
    assert sample(once, 5) == B and sample(once, 0) == A and sample(once, 2).rot["hip"] == A.rot["hip"]
    print("clip: loop f0 == t=1 key, loop f4 == key .5, once last == final key, hold ease holds; loop f2 hip ry",
          round(sample(loop, 2).rot["hip"][1], 4))

    poses = [Pose(), Pose({"hip": (0, -25, 0), "knee": (0, 60, 0)}), Pose({"hip": (0, 20, 0), "knee": (0, 100, 0)}, (0, 0, 2.0))]
    posed = [skin(sk, p, parts, skins) for p in poses]
    for res in posed[1:]:
        assert not np.array_equal(res[1].v, parts[1][0].v)
    print("posed shin z min per pose", [round(float(r[1].v[:, 2].min()), 3) for r in posed])

    TEX = 256
    uvs, rects = unwrap(parts, TEX)
    print("rects (x, y, w, h, cyl)", [tuple(r) + (r.cyl,) for r in rects])
    for a in range(len(rects)):
        for b in range(a + 1, len(rects)):
            ra, rb, g = rects[a], rects[b], GUTTER
            assert (ra.x + ra.w + g <= rb.x - g or rb.x + rb.w + g <= ra.x - g or ra.y + ra.h + g <= rb.y - g
                    or rb.y + rb.h + g <= ra.y - g), "padded rectangles overlap"
    # unwrap_split: the old unwrap leaves seam-straddling triangles, the split one has none and moves no surface
    def _straddlers(ms, uv_list, rs):
        n = 0
        for m_, uv_, r_ in zip(ms, uv_list, rs):
            if r_.cyl:
                u_ = uv_[m_.f][:, :, 0] * TEX
                n += int(((u_.max(1) - u_.min(1)) > r_.w / 2 + 1e-6).sum())
        return n

    def _area(m_):
        t_ = m_.v.astype(np.float64)[m_.f]
        return float(0.5 * np.linalg.norm(np.cross(t_[:, 1] - t_[:, 0], t_[:, 2] - t_[:, 0]), axis=1).sum())

    sm, suv, srect = unwrap_split([m for m, _ in parts], TEX)
    before, after = _straddlers([m for m, _ in parts], uvs, rects), _straddlers(sm, suv, srect)
    assert before > 0 and after == 0, (before, after)
    assert all(abs(_area(a_) - _area(b_)) < 1e-4 * _area(b_) for a_, (b_, _) in zip(sm, parts)), "seam cut moved the surface"
    print("unwrap_split: straddling triangles", before, "->", after, "(surface area unchanged)")
    atlas = bake_albedo(parts, skins, sk, uvs, rects, TEX)
    again = bake_albedo(parts, skins, sk, uvs, rects, TEX)
    assert atlas.tobytes() == again.tobytes(), "bake is not deterministic"
    print("bake sha1", hashlib.sha1(atlas.tobytes()).hexdigest(), "(identical on a second run)")
    print("stats", {k: (round(v, 4) if isinstance(v, float) else v) for k, v in _hsv_report(atlas).items()})

    lift, side = 3.0, 330
    flat = atlas[..., :3]
    left = Image.fromarray(flat).resize((TEX * 2, TEX * 2), Image.NEAREST)
    lifted = Image.fromarray(np.clip(flat.astype(np.float64) * lift, 0, 255).astype(np.uint8)).resize((TEX * 2, TEX * 2), Image.NEAREST)
    shots = [Image.fromarray(_textured(res, atlas, uvs, rects, side, lift).astype(np.uint8)) for res in posed]
    sheet = Image.new("RGB", (TEX * 4 + side * 3 + 16 * 6, TEX * 2 + 32), (8, 8, 9))
    x = 16
    for im in (left, lifted, *shots):
        sheet.paste(im, (x, 16))
        x += im.width + 16
    out = Path(__file__).resolve().parent / "previews" / "charforge_bake.png"
    out.parent.mkdir(exist_ok=True)
    sheet.save(out)
    print("wrote", out, sheet.size, "(1: atlas x2 as baked, 2: atlas x2 with x3 value lift, 3-5: three poses textured with x3 lift)")


if __name__ == "__main__":
    main()
