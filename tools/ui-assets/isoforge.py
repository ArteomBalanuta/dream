"""Isometric prop renderer: z-buffered triangles in numpy, lit through forge's linear-light chain.

Chain: Mesh -> raster G-buffer (P, N, material, uv, tint) -> key shadow map + dome AO -> forge.Maps ->
forge.Surface -> forge.age -> object-space moss -> forge.light (ISO_RIG) -> forge.finish -> crush and
saturation cap -> FX layer -> forge.downsample -> uiart.bleed_rgb.

World is u (16 u = 1 m), x and y on the ground, z up, origin = foot point. The camera sits at pitch 50 and
yaw 45. With a = sqrt(.5), s = sin 50, c = cos 50 the view basis is

    R = (a, -a, 0)   U = (s a, s a, c)   V = (-c a, -c a, s)
    view x = P.R, view y (down) = -P.U, view z (towards the viewer) = P.V

Normals handed to forge are view-space (forge.light looks along view (0, 0, 1)). A hi-res pixel is
pivot_px * SS + view_xy * PPU; the final sprite is 4 px per u.

Form bands (all authors): primary >= 8 u, secondary 2..6 u, tertiary < 1 u. Keep waves3, displace and
IsoMat.detail wavelengths inside those bands and leave the 1..2 u gap empty.
"""
from __future__ import annotations

import math
from dataclasses import dataclass

import numpy as np

import forge
import uiart as U
from forge import blur, smooth

F32 = np.float32
TAU = math.tau
PPU, SS = U.PPU, U.SS
FINAL_PPU = PPU // SS

PITCH_DEG = 50.0
YAW_DEG = 45.0
_a = math.sqrt(0.5)
_s = math.sin(math.radians(PITCH_DEG))
_c = math.cos(math.radians(PITCH_DEG))
AX_R = np.array((_a, -_a, 0.0))
AX_U = np.array((_s * _a, _s * _a, _c))
AX_V = np.array((-_c * _a, -_c * _a, _s))
KEY_DIR = forge.GOTHIC_RIG.lights[0].dir  # view space, towards the light


def _unit(v) -> np.ndarray:
    v = np.asarray(v, dtype=np.float64)
    return v / np.linalg.norm(v)


def to_view(P: np.ndarray):
    """World points to (view x, view y down, view depth towards the viewer)."""
    return P @ AX_R, -(P @ AX_U), P @ AX_V


def view_to_world(n: np.ndarray) -> np.ndarray:
    return n[..., 0:1] * AX_R + (-n[..., 1:2]) * AX_U + n[..., 2:3] * AX_V


def world_to_view(n: np.ndarray) -> np.ndarray:
    return np.stack((n @ AX_R, -(n @ AX_U), n @ AX_V), axis=-1)


KEY_WORLD = _unit(view_to_world(np.array(KEY_DIR, dtype=np.float64)))

# lighting rig ---------------------------------------------------------------------------

ISO_RIG = forge.Rig(
    lights=(
        forge.Light(KEY_DIR, (1.00, 0.76, 0.52), 18.0, shadow=True),   # torch key, same direction as the gothic key
        forge.Light((0.75, 0.40, 0.52), (0.22, 0.46, 1.00), 6.0),    # cold fill from the lower right
        forge.Light((-0.15, 0.80, 0.30), (0.30, 0.52, 1.00), 1.2),    # cold rim from below
    ),
    panels=(forge.Panel(-0.18, -0.62, 0.60, 0.26, 0.16, (0.88, 0.94, 1.0)),),
    ambient=(0.0050, 0.0130, 0.0460),                                 # cold, under 15 percent of the key
)

# materials ------------------------------------------------------------------------------


@dataclass(frozen=True)
class Waves3:
    """One analytic bump layer: plane waves in world space, height amplitude amp in u."""

    name: str
    n: int
    lo: float
    hi: float
    amp: float
    stretch: tuple = (1.0, 1.0, 1.0)


@dataclass(frozen=True)
class IsoMat:
    """Linear light. rough >= .65 unless marked emissive or glass."""

    albedo: tuple
    f0: tuple
    rough: float
    bare_albedo: tuple | None = None
    bare_f0: tuple | None = None
    wear: float = 1.0
    emit: tuple | None = None
    sat_cap: float = 0.35
    detail: tuple = ()


RUST_CAP = 0.44  # rust is the one accent the barrel may saturate: p99 <= .50 after the soft knee

# Only "moss" is looked up by name (the moss pass, and the tombstone). Every object brings its own IsoMat.
ISO_MATERIALS: dict[str, IsoMat] = {
    "moss": IsoMat((0.020, 0.034, 0.012), (0.020, 0.020, 0.020), 0.95, sat_cap=0.35,
                   detail=(Waves3("moss.tuft", 8, 0.4, 0.9, 0.03), Waves3("moss.mound", 6, 2.5, 5.0, 0.12))),
}

# waves3 ---------------------------------------------------------------------------------


def waves3(P, name: str, n: int, lo: float, hi: float, stretch=(1.0, 1.0, 1.0)):
    """Sum of n plane waves with log-spaced wavelengths lo..hi (u), roughly unit variance, and the analytic
    gradient. P is (..., 3) in world u. Returns (value shaped P[..., 0], gradient shaped P).
    stretch scales the wave vector per axis, so 0.14 on z elongates features along z."""
    r = U.rng("iso." + name)
    i = np.arange(n)
    z = 1 - 2 * (i + r.random(n)) / n
    phi = i * 2.399963 + r.random() * TAU
    rad = np.sqrt(np.maximum(1 - z * z, 0))
    dirs = np.stack((rad * np.cos(phi), rad * np.sin(phi), z), axis=1)
    wl = lo * (hi / lo) ** ((r.permutation(n) + r.random(n)) / n)
    k = dirs * (TAU / wl)[:, None] * np.asarray(stretch, dtype=np.float64)[None, :]
    ph = r.random(n) * TAU
    P = np.asarray(P, dtype=np.float64)
    flat = P.reshape(-1, 3)
    norm = 1.0 / math.sqrt(n / 2)
    val = np.empty(len(flat), dtype=F32)
    grad = np.empty((len(flat), 3), dtype=F32)
    for a0 in range(0, len(flat), 200_000):
        arg = flat[a0 : a0 + 200_000] @ k.T + ph
        val[a0 : a0 + 200_000] = np.cos(arg).sum(1) * norm
        grad[a0 : a0 + 200_000] = (-np.sin(arg)) @ k * norm
    return val.reshape(P.shape[:-1]), grad.reshape(P.shape)


# mesh -----------------------------------------------------------------------------------


def _face_normals(v: np.ndarray, f: np.ndarray) -> np.ndarray:
    """Area-weighted (unnormalised) face normals from the winding."""
    return np.cross(v[f[:, 1]] - v[f[:, 0]], v[f[:, 2]] - v[f[:, 0]])


def _smooth_normals(v: np.ndarray, f: np.ndarray, crease_deg: float = 35.0) -> np.ndarray:
    """Vertex normals welded by position, so uv seams stay smooth, but not across creases sharper than crease_deg."""
    v = v.astype(np.float64)
    fn = _face_normals(v, f)
    own = np.zeros_like(v)
    for k in range(3):
        for c in range(3):
            own[:, c] += np.bincount(f[:, k], weights=fn[:, c], minlength=len(v))
    key = np.round(v * 2000).astype(np.int64)
    _, g = np.unique(key, axis=0, return_inverse=True)
    g = g.reshape(-1)
    ng = g.max() + 1
    gs = np.stack([np.bincount(g, weights=own[:, c], minlength=ng) for c in range(3)], axis=1)[g]
    own_n = own / np.maximum(np.linalg.norm(own, axis=1, keepdims=True), 1e-12)
    gs_n = gs / np.maximum(np.linalg.norm(gs, axis=1, keepdims=True), 1e-12)
    ok = (own_n * gs_n).sum(1) > math.cos(math.radians(crease_deg))
    return np.where(ok[:, None], gs_n, own_n).astype(F32)


def _rotation(rot_deg) -> np.ndarray:
    rx, ry, rz = (math.radians(a) for a in rot_deg)
    cx, sx, cy, sy, cz, sz = math.cos(rx), math.sin(rx), math.cos(ry), math.sin(ry), math.cos(rz), math.sin(rz)
    Rx = np.array(((1, 0, 0), (0, cx, -sx), (0, sx, cx)))
    Ry = np.array(((cy, 0, sy), (0, 1, 0), (-sy, 0, cy)))
    Rz = np.array(((cz, -sz, 0), (sz, cz, 0), (0, 0, 1)))
    return Rz @ Ry @ Rx


@dataclass
class Mesh:
    v: np.ndarray                    # Nx3 float32
    f: np.ndarray                    # Mx3 int32
    n: np.ndarray | None = None      # None = flat facet normals
    uv: np.ndarray | None = None     # Nx2
    tint: np.ndarray | None = None   # Nx3

    def __post_init__(self):
        self.v = np.asarray(self.v, dtype=F32)
        self.f = np.asarray(self.f, dtype=np.int32)
        if self.n is not None:
            self.n = np.asarray(self.n, dtype=F32)

    def xform(self, rot_deg=(0, 0, 0), move=(0, 0, 0), scale=1.0) -> "Mesh":
        """Scale, then rotate about x, y, z (degrees, applied in that order), then move."""
        Rm = _rotation(rot_deg)
        v = (self.v.astype(np.float64) * scale) @ Rm.T + np.asarray(move, dtype=np.float64)
        n = None if self.n is None else (self.n.astype(np.float64) @ Rm.T)
        return Mesh(v, self.f.copy(), n, self.uv, self.tint)

    def _unwelded(self) -> "Mesh":
        """Flat mesh to explicit per-face vertices and normals, so it can merge with smooth meshes."""
        fn = _face_normals(self.v.astype(np.float64), self.f)
        fn = fn / np.maximum(np.linalg.norm(fn, axis=1, keepdims=True), 1e-12)
        f = self.f
        return Mesh(self.v[f].reshape(-1, 3), np.arange(len(f) * 3, dtype=np.int32).reshape(-1, 3), np.repeat(fn, 3, axis=0),
                    None if self.uv is None else self.uv[f].reshape(-1, 2), None if self.tint is None else self.tint[f].reshape(-1, 3))

    def merge(self, other: "Mesh") -> "Mesh":
        a, b = self, other
        if (a.n is None) != (b.n is None):
            a, b = (a._unwelded() if a.n is None else a), (b._unwelded() if b.n is None else b)
        off = len(a.v)
        n = None if a.n is None else np.concatenate((a.n, b.n))
        uv = None if (a.uv is None and b.uv is None) else np.concatenate((_or(a.uv, len(a.v), 2, 0.0), _or(b.uv, len(b.v), 2, 0.0)))
        tint = None if (a.tint is None and b.tint is None) else np.concatenate((_or(a.tint, len(a.v), 3, 1.0), _or(b.tint, len(b.v), 3, 1.0)))
        return Mesh(np.concatenate((a.v, b.v)), np.concatenate((a.f, b.f + off)), n, uv, tint)

    def displace(self, name: str, amp_u: float, wl_lo: float, wl_hi: float, along_normal: bool = True) -> "Mesh":
        """Move vertices by a position-only wave field, so welded seams stay closed. along_normal moves along
        the vertex normal (flat meshes use welded smooth normals for the direction); otherwise each axis gets
        its own field (rattling a shard or a pile)."""
        v = self.v.astype(np.float64)
        if along_normal:
            nrm = self.n if self.n is not None else _smooth_normals(self.v, self.f)
            val, _ = waves3(v, name, 6, wl_lo, wl_hi)
            v = v + nrm * (val * amp_u)[:, None]
        else:
            for ax in range(3):
                val, _ = waves3(v, f"{name}.{ax}", 6, wl_lo, wl_hi)
                v[:, ax] += val * amp_u
        n = None if self.n is None else _smooth_normals(v, self.f)
        return Mesh(v, self.f.copy(), n, self.uv, self.tint)


def _or(a, n, w, fill):
    return np.full((n, w), fill, dtype=F32) if a is None else a


def _orient_outward(v: np.ndarray, f: np.ndarray, centre) -> np.ndarray:
    fn = _face_normals(v.astype(np.float64), f)
    cen = v[f].mean(axis=1) - np.asarray(centre)
    bad = (fn * cen).sum(1) < 0
    f = f.copy()
    f[bad] = f[bad][:, ::-1]
    return f


def grid(P: np.ndarray, uv: np.ndarray | None = None, smooth_normals: bool = True) -> Mesh:
    """Surface from an (R, C, 3) lattice of points; rows run along the profile, columns around it. Duplicate the
    first column at the end for a closed ring (uv seam); welded normals keep the seam smooth."""
    Rr, C = P.shape[:2]
    v = np.asarray(P, dtype=np.float64).reshape(-1, 3)
    idx = np.arange(Rr * C).reshape(Rr, C)
    a, b, c, d = idx[:-1, :-1], idx[:-1, 1:], idx[1:, 1:], idx[1:, :-1]
    f = np.concatenate((np.stack((a, b, c), -1).reshape(-1, 3), np.stack((a, c, d), -1).reshape(-1, 3)))
    n = _smooth_normals(v, f) if smooth_normals else None
    return Mesh(v, f, n, None if uv is None else np.asarray(uv, dtype=F32).reshape(-1, 2))


def lathe(profile, sides: int = 48) -> Mesh:
    """Surface of revolution about z. profile is [(r, z)] ordered so the solid is on the left when r points right
    and z up (a side wall runs bottom to top; a top cap runs from the rim inwards). uv = (theta, z)."""
    pr = np.asarray(profile, dtype=np.float64)
    th = np.linspace(0, TAU, sides + 1)
    P = np.stack((pr[:, 0:1] * np.cos(th)[None, :], pr[:, 0:1] * np.sin(th)[None, :], np.broadcast_to(pr[:, 1:2], (len(pr), sides + 1))), axis=-1)
    uv = np.stack((np.broadcast_to(th[None, :] / TAU, (len(pr), sides + 1)), np.broadcast_to(pr[:, 1:2], (len(pr), sides + 1))), axis=-1)
    return grid(P, uv)


def _resample(path: np.ndarray, step: float) -> np.ndarray:
    """Catmull-Rom through the path, then points about step apart. Returns (K, 3) and arclength fractions."""
    p = np.asarray(path, dtype=np.float64)
    if len(p) < 3:
        n = max(2, int(np.linalg.norm(p[-1] - p[0]) / step) + 1)
        return p[0] + (p[-1] - p[0]) * np.linspace(0, 1, n)[:, None]
    ext = np.vstack((2 * p[0] - p[1], p, 2 * p[-1] - p[-2]))
    pts = []
    for i in range(len(p) - 1):
        p0, p1, p2, p3 = ext[i], ext[i + 1], ext[i + 2], ext[i + 3]
        m = max(2, int(np.linalg.norm(p2 - p1) / (step * 0.5)) + 1)
        t = np.linspace(0, 1, m, endpoint=False)[:, None]
        pts.append(0.5 * ((2 * p1) + (-p0 + p2) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t ** 2 + (-p0 + 3 * p1 - 3 * p2 + p3) * t ** 3))
    pts.append(p[-1:])
    dense = np.vstack(pts)
    seg = np.linalg.norm(np.diff(dense, axis=0), axis=1)
    s = np.concatenate(([0.0], np.cumsum(seg)))
    n = max(2, int(round(s[-1] / step)) + 1)
    q = np.linspace(0, s[-1], n)
    return np.stack([np.interp(q, s, dense[:, c]) for c in range(3)], axis=1)


def tube(path, radii, sides: int = 10, seg_u: float = 1.5, name: str = "") -> Mesh:
    """Tube along a smooth path (Catmull-Rom). radii is a scalar or one value per path point. uv = (theta, length).
    The ends are closed with flat caps (own vertices, so they stay crisp)."""
    p = np.asarray(path, dtype=np.float64)
    q = _resample(p, seg_u)
    K = len(q)
    if np.isscalar(radii):
        rad = np.full(K, float(radii))
    else:
        r0 = np.asarray(radii, dtype=np.float64)
        rad = np.interp(np.linspace(0, 1, K), np.linspace(0, 1, len(r0)), r0)
    t = np.gradient(q, axis=0)
    t /= np.maximum(np.linalg.norm(t, axis=1, keepdims=True), 1e-12)
    ref = np.array((0.0, 0.0, 1.0)) if abs(t[0, 2]) < 0.9 else np.array((1.0, 0.0, 0.0))
    nrm = np.cross(t[0], ref)
    nrm /= np.linalg.norm(nrm)
    frames = []
    for k in range(K):  # parallel transport
        if k:
            b = np.cross(t[k - 1], t[k])
            sb = np.linalg.norm(b)
            if sb > 1e-9:
                ang = math.asin(min(sb, 1.0))
                b /= sb
                nrm = nrm * math.cos(ang) + np.cross(b, nrm) * math.sin(ang) + b * (b @ nrm) * (1 - math.cos(ang))
            nrm = nrm - t[k] * (nrm @ t[k])
            nrm /= np.linalg.norm(nrm)
        frames.append((nrm.copy(), np.cross(t[k], nrm)))
    th = np.linspace(0, TAU, sides + 1)
    Nn = np.array([f_[0] for f_ in frames])
    Bb = np.array([f_[1] for f_ in frames])
    off = np.cos(th)[None, :, None] * Nn[:, None, :] + np.sin(th)[None, :, None] * Bb[:, None, :]
    P = q[:, None, :] + off * rad[:, None, None]
    length = np.concatenate(([0.0], np.cumsum(np.linalg.norm(np.diff(q, axis=0), axis=1))))
    uv = np.stack((np.broadcast_to(th[None, :] / TAU, (K, sides + 1)), np.broadcast_to(length[:, None], (K, sides + 1))), axis=-1)
    body = grid(P, uv)
    out = body
    for end, sgn in ((0, -1.0), (K - 1, 1.0)):
        ring = P[end, :sides]
        cap_v = np.vstack((ring, q[end][None, :]))
        ii = np.arange(sides)
        cf = np.stack((ii, (ii + 1) % sides, np.full(sides, sides)), axis=1)
        cn = np.broadcast_to(t[end] * sgn, cap_v.shape)
        cf = _orient_outward(cap_v, cf, q[end] - t[end] * sgn)
        out = out.merge(Mesh(cap_v, cf, cn, np.zeros((len(cap_v), 2), dtype=F32)))
    return out


def box(w: float, d: float, h: float, chamfer: float = 0.0) -> Mesh:
    """Box centred on x and y, standing on z = 0, flat-faceted. chamfer cuts every edge and corner."""
    hx, hy = w / 2, d / 2
    c = min(chamfer, hx * 0.99, hy * 0.99, h * 0.49)
    ext = np.array((hx, hy, h / 2))
    cen = np.array((0.0, 0.0, h / 2))
    if c <= 0:
        sg = np.array([(x, y, z) for z in (-1, 1) for y in (-1, 1) for x in (-1, 1)], dtype=np.float64)
        v = cen + sg * ext
        quads = ((0, 1, 3, 2), (4, 6, 7, 5), (0, 4, 5, 1), (2, 3, 7, 6), (0, 2, 6, 4), (1, 5, 7, 3))
        f = np.array([t for q in quads for t in ((q[0], q[1], q[2]), (q[0], q[2], q[3]))], dtype=np.int32)
        return Mesh(v, _orient_outward(v, f, cen))
    verts, vid = [], {}
    corners = [(x, y, z) for z in (-1, 1) for y in (-1, 1) for x in (-1, 1)]
    for ci, sg in enumerate(corners):
        for ax in range(3):
            p = cen + np.array(sg) * ext
            for o in range(3):
                if o != ax:
                    p[o] -= sg[o] * c
            vid[(ci, ax)] = len(verts)
            verts.append(p)
    f = []

    def cid(sg):
        return corners.index(tuple(sg))

    for ax in range(3):  # big faces
        for s_ in (-1, 1):
            cs = [cid(sg) for sg in corners if sg[ax] == s_]
            q = [vid[(ci, ax)] for ci in cs]
            f += [(q[0], q[1], q[3]), (q[0], q[3], q[2])]
    for ax in range(3):  # edge strips between the two faces meeting on the edge, along axis ax
        o1, o2 = [o for o in range(3) if o != ax]
        for s1 in (-1, 1):
            for s2 in (-1, 1):
                pair = []
                for sa in (-1, 1):
                    sg = [0, 0, 0]
                    sg[ax], sg[o1], sg[o2] = sa, s1, s2
                    pair.append(cid(sg))
                a, b = pair
                f += [(vid[(a, o1)], vid[(b, o1)], vid[(b, o2)]), (vid[(a, o1)], vid[(b, o2)], vid[(a, o2)])]
    for ci in range(8):
        f.append((vid[(ci, 0)], vid[(ci, 1)], vid[(ci, 2)]))
    v = np.array(verts)
    return Mesh(v, _orient_outward(v, np.array(f, dtype=np.int32), cen))


def convex(name: str, radius: float, cuts: int, squash=(1.0, 1.0, 1.0)) -> Mesh:
    """Convex rock: a box of half-size radius cut by `cuts` random planes; flat-faceted. Centred on the origin."""
    r = U.rng("iso.convex." + name)
    nrm = [np.array(a, dtype=np.float64) for a in ((1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1))]
    dist = [radius * (0.85 + 0.3 * r.random()) for _ in range(6)]
    for _ in range(cuts):
        v = r.normal(size=3)
        v /= np.linalg.norm(v)
        nrm.append(v)
        dist.append(radius * (0.45 + 0.4 * r.random()))
    N, D = np.array(nrm), np.array(dist)
    m = len(N)
    combos = np.array([(i, j, k) for i in range(m) for j in range(i + 1, m) for k in range(j + 1, m)])
    A = N[combos]
    det = np.linalg.det(A)
    good = np.abs(det) > 1e-6
    sol = np.linalg.solve(A[good], D[combos[good]][..., None])[..., 0]
    inside = (sol @ N.T <= D[None, :] + 1e-6).all(1)
    pts = sol[inside]
    key = np.round(pts / 1e-4).astype(np.int64)
    _, first = np.unique(key, axis=0, return_index=True)
    pts = pts[np.sort(first)]
    faces = []
    for i in range(m):
        on = np.nonzero(np.abs(pts @ N[i] - D[i]) < 1e-4)[0]
        if len(on) < 3:
            continue
        cen = pts[on].mean(0)
        t1 = np.cross(N[i], (1, 0, 0) if abs(N[i][0]) < 0.9 else (0, 1, 0))
        t1 /= np.linalg.norm(t1)
        t2 = np.cross(N[i], t1)
        ang = np.arctan2((pts[on] - cen) @ t2, (pts[on] - cen) @ t1)
        order = on[np.argsort(ang)]
        for a in range(1, len(order) - 1):
            faces.append((order[0], order[a], order[a + 1]))
    v = pts * np.asarray(squash, dtype=np.float64)
    f = _orient_outward(v, np.array(faces, dtype=np.int32), np.zeros(3))
    return Mesh(v, f)


def torus(R: float, r: float, sides: int = 16, rings: int = 24) -> Mesh:
    """Torus about z centred on the origin. uv = (around the ring, around the tube)."""
    a = np.linspace(0, TAU, rings + 1)[:, None]
    b = np.linspace(0, TAU, sides + 1)[None, :]
    P = np.stack(((R + r * np.cos(b)) * np.cos(a), (R + r * np.cos(b)) * np.sin(a), r * np.sin(b) + 0 * a), axis=-1)
    uv = np.stack((np.broadcast_to(a / TAU, (rings + 1, sides + 1)), np.broadcast_to(b / TAU, (rings + 1, sides + 1))), axis=-1)
    return grid(P, uv)


def card(outline_xy, bend: float = 0.0) -> Mesh:
    """Fanned polygon (a leaf) in the xy plane, flat-faceted, rib along x. bend lifts the sides: z = bend * y^2."""
    o = np.asarray(outline_xy, dtype=np.float64)
    cen = o.mean(0)
    pts = np.vstack((cen[None, :], o))
    z = bend * pts[:, 1] ** 2
    v = np.column_stack((pts, z))
    k = len(o)
    f = np.array([(0, 1 + i, 1 + (i + 1) % k) for i in range(k)], dtype=np.int32)
    span = np.maximum(np.ptp(pts, axis=0), 1e-6)
    return Mesh(v, f, None, (pts - pts.min(0)) / span)


# raster ---------------------------------------------------------------------------------

_EPS = 1e-7


def _tri_setup(sx, sy, sz, F):
    x0, x1, x2 = sx[F[:, 0]], sx[F[:, 1]], sx[F[:, 2]]
    y0, y1, y2 = sy[F[:, 0]], sy[F[:, 1]], sy[F[:, 2]]
    z0, z1, z2 = sz[F[:, 0]], sz[F[:, 1]], sz[F[:, 2]]
    det = (x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0)
    ok = np.abs(det) > 1e-9
    inv = 1.0 / np.where(ok, det, 1.0)
    a1, b1, c1 = (y2 - y0) * inv, -(x2 - x0) * inv, (-x0 * (y2 - y0) + (x2 - x0) * y0) * inv
    a2, b2, c2 = -(y1 - y0) * inv, (x1 - x0) * inv, (-(x1 - x0) * y0 + x0 * (y1 - y0)) * inv
    dz1, dz2 = z1 - z0, z2 - z0
    coef = np.stack((a1, b1, c1, a2, b2, c2, a1 * dz1 + a2 * dz2, b1 * dz1 + b2 * dz2, z0 + c1 * dz1 + c2 * dz2), axis=1)
    mnx, mxx = np.minimum(np.minimum(x0, x1), x2), np.maximum(np.maximum(x0, x1), x2)
    mny, mxy = np.minimum(np.minimum(y0, y1), y2), np.maximum(np.maximum(y0, y1), y2)
    return coef, ok, mnx, mxx, mny, mxy


def _raster(sx, sy, sz, F, w, h, want_tid=True):
    """Z-buffer (larger z wins) and triangle ids for triangles with screen coordinates sx, sy in px."""
    coef, ok, mnx, mxx, mny, mxy = _tri_setup(sx, sy, sz, F)
    ix0 = np.maximum(np.ceil(mnx - 0.5), 0).astype(np.int64)
    ix1 = np.minimum(np.floor(mxx - 0.5), w - 1).astype(np.int64)
    iy0 = np.maximum(np.ceil(mny - 0.5), 0).astype(np.int64)
    iy1 = np.minimum(np.floor(mxy - 0.5), h - 1).astype(np.int64)
    keep = np.nonzero(ok & (ix0 <= ix1) & (iy0 <= iy1))[0]
    zbuf = np.full((h, w), -np.inf)
    tid = np.full((h, w), -1, dtype=np.int32)
    cx, cy = np.arange(w) + 0.5, np.arange(h) + 0.5
    rows = coef[keep].tolist()
    for t, (a1, b1, c1, a2, b2, c2, zA, zB, zC), xa, xb, ya, yb in zip(
            keep.tolist(), rows, ix0[keep].tolist(), ix1[keep].tolist(), iy0[keep].tolist(), iy1[keep].tolist()):
        xs, ys = cx[xa : xb + 1], cy[ya : yb + 1]
        l1 = (b1 * ys + c1)[:, None] + (a1 * xs)[None, :]
        l2 = (b2 * ys + c2)[:, None] + (a2 * xs)[None, :]
        ins = (l1 >= -_EPS) & (l2 >= -_EPS) & (l1 + l2 <= 1 + _EPS)
        z = (zB * ys + zC)[:, None] + (zA * xs)[None, :]
        zs = zbuf[ya : yb + 1, xa : xb + 1]
        m = ins & (z > zs)
        zs[m] = z[m]
        tid[ya : yb + 1, xa : xb + 1][m] = t
    return zbuf, tid, coef


def _depth_map(sx, sy, sz, F, w, h, small=1.5):
    """Depth only. Triangles under `small` px are splatted (corners, edge midpoints, centroid) with
    np.maximum.at, larger ones are rasterised."""
    x = sx[F]
    y = sy[F]
    zt = sz[F].max(axis=1)
    size = np.maximum(x.max(1) - x.min(1), y.max(1) - y.min(1))
    sm = size <= small
    zbuf = np.full(h * w, -np.inf)
    if sm.any():
        xs, ys = x[sm], y[sm]
        px = np.concatenate([xs[:, 0], xs[:, 1], xs[:, 2], xs.mean(1), (xs[:, 0] + xs[:, 1]) / 2, (xs[:, 1] + xs[:, 2]) / 2, (xs[:, 2] + xs[:, 0]) / 2])
        py = np.concatenate([ys[:, 0], ys[:, 1], ys[:, 2], ys.mean(1), (ys[:, 0] + ys[:, 1]) / 2, (ys[:, 1] + ys[:, 2]) / 2, (ys[:, 2] + ys[:, 0]) / 2])
        pz = np.tile(zt[sm], 7)
        ix, iy = np.floor(px).astype(np.int64), np.floor(py).astype(np.int64)
        okp = (ix >= 0) & (ix < w) & (iy >= 0) & (iy < h)
        np.maximum.at(zbuf, iy[okp] * w + ix[okp], pz[okp])
    zbuf = zbuf.reshape(h, w)
    big = ~sm
    if big.any():
        zb, _, _ = _raster(sx, sy, sz, F[big], w, h)
        zbuf = np.maximum(zbuf, zb)
    return zbuf


class _Occluder:
    """Orthographic depth map seen from direction d (towards the light or sky)."""

    def __init__(self, V, F, d, res: float, small: float, margin: float = 1.0):
        self.e1, self.e2, self.d = _frame(d)
        a, b, z = V @ self.e1, V @ self.e2, V @ self.d
        self.o1, self.o2, self.res = a.min() - margin, b.min() - margin, res
        self.w = int(math.ceil((a.max() - a.min() + 2 * margin) * res)) + 1
        self.h = int(math.ceil((b.max() - b.min() + 2 * margin) * res)) + 1
        self.z = _depth_map((a - self.o1) * res, (b - self.o2) * res, z, F, self.w, self.h, small)

    def occluded(self, P, bias, tap: float = 0.0):
        """Fraction of the 1 (tap 0) or 4 samples that see something nearer to the source than P + bias."""
        a, b, dz = (P @ self.e1 - self.o1) * self.res, (P @ self.e2 - self.o2) * self.res, P @ self.d + bias
        offs = ((0.0, 0.0),) if tap == 0 else ((-tap, -tap), (tap, -tap), (-tap, tap), (tap, tap))
        acc = 0.0
        for oa, ob in offs:
            ix, iy = np.floor(a + oa).astype(np.int64), np.floor(b + ob).astype(np.int64)
            inside = (ix >= 0) & (ix < self.w) & (iy >= 0) & (iy < self.h)
            zm = self.z[np.clip(iy, 0, self.h - 1), np.clip(ix, 0, self.w - 1)]
            acc = acc + (inside & (zm > dz))
        return acc / len(offs)


def _frame(d):
    d = _unit(d)
    t = np.array((0.0, 0.0, 1.0)) if abs(d[2]) < 0.9 else np.array((1.0, 0.0, 0.0))
    e1 = _unit(np.cross(t, d))
    return e1, np.cross(d, e1), d


def _dome_dirs(m: int) -> np.ndarray:
    i = np.arange(m)
    z = 0.10 + 0.90 * (i + 0.5) / m
    phi = i * 2.399963
    r = np.sqrt(1 - z * z)
    return np.stack((r * np.cos(phi), r * np.sin(phi), z), axis=1)


def _depth_ao(z: np.ndarray, valid: np.ndarray) -> np.ndarray:
    """Fine contact occlusion from the view depth (u): 8 directions, horizon slope, range-limited to 5 u of depth."""
    H, W = z.shape
    zz = np.where(valid, z, -1e4).astype(F32)
    steps = (1, 2, 4, 7, 12, 20)
    pad = max(steps)
    P = np.pad(zz, pad, mode="edge")
    occ = np.zeros((H, W), dtype=F32)
    for dx, dy in forge._DIRS8:
        best = np.zeros((H, W), dtype=F32)
        for s_ in steps:
            ox, oy = int(round(dx * s_)), int(round(dy * s_))
            dist = math.hypot(ox, oy) / PPU
            dh = P[pad + oy : pad + oy + H, pad + ox : pad + ox + W] - zz
            t = np.where(dh > 0, dh * np.clip((5.0 - dh) / 2.5, 0, 1), 0) / dist
            np.maximum(best, t, out=best)
        occ += best / np.sqrt(1 + best * best)
    return (1 - occ / 8).astype(F32)


# scene ----------------------------------------------------------------------------------


@dataclass
class Rendered:
    rgba: np.ndarray      # uint8 straight RGBA, final size
    shadow: np.ndarray    # uint8 black RGB + soft alpha, same canvas
    stats: dict


@dataclass(frozen=True)
class ObjectSpec:
    size: tuple
    pivot: tuple
    frames: tuple                # callables, each () -> Rendered; statics have 1, potion has 4
    duration_ms: tuple = ()


def _masked_blur(a, m, sigma):
    """Blur a (H, W[, C]) restricted to the mask m (normalised convolution)."""
    mm = m.astype(F32)
    num = blur(a * (mm[..., None] if a.ndim == 3 else mm), sigma)
    den = np.maximum(blur(mm, sigma), 1e-3)
    return num / (den[..., None] if a.ndim == 3 else den)


CRUSH = 0.008  # sRGB luma at or under this goes to black; the cold fill keeps everything above it readable


def _grade(rgb: np.ndarray, cap: np.ndarray) -> np.ndarray:
    """Crush the blacks, then a per-material soft-knee cap on HSV saturation that keeps hue and max."""
    y = 0.2126 * rgb[..., 0] + 0.7152 * rgb[..., 1] + 0.0722 * rgb[..., 2]
    y2 = np.clip((y - CRUSH) / (1 - CRUSH), 0, 1) ** 1.08
    rgb = np.clip(rgb * (y2 / np.maximum(y, 1e-5))[..., None], 0, 1)
    mx, mn = rgb.max(-1), rgb.min(-1)
    S = (mx - mn) / np.maximum(mx, 1e-5)
    k = 0.8 * cap
    S2 = np.where(S <= k, S, k + (cap - k) * np.tanh((S - k) / np.maximum(cap - k, 1e-4)))
    sc = np.where(mx - mn > 1e-5, (mx * S2) / np.maximum(mx - mn, 1e-5), 1.0)
    return (mx[..., None] - (mx[..., None] - rgb) * sc[..., None]).astype(F32)


class Scene:
    def __init__(self, name: str, w_px: int, h_px: int, pivot_px):
        self.name, self.w, self.h = name, int(w_px), int(h_px)
        self.pivot = (float(pivot_px[0]), float(pivot_px[1]))
        self.W, self.H = self.w * SS, self.h * SS
        self.items: list[tuple] = []
        self.mats: list[IsoMat] = []
        self.fx_layers: list[tuple] = []

    # building ---------------------------------------------------------------------------

    def add(self, mesh: Mesh, mat, seed: str = "", emit=None):
        """mat is a key of ISO_MATERIALS or an IsoMat. emit, if given, is a callable uv(Nx2) -> linear rgb (Nx3)
        evaluated per pixel and added as emission (rune strokes painted in uv)."""
        m = ISO_MATERIALS[mat] if isinstance(mat, str) else mat
        if m not in self.mats:
            self.mats.append(m)
        self.items.append((mesh, self.mats.index(m), seed, emit))

    def project(self, P) -> np.ndarray:
        """World to final px."""
        P = np.asarray(P, dtype=np.float64)
        vx, vy, _ = to_view(P)
        return np.stack((self.pivot[0] + vx * FINAL_PPU, self.pivot[1] + vy * FINAL_PPU), axis=-1)

    def ground_uv(self):
        """Hi-res world x, y on z = 0 for every canvas pixel, for ground effects."""
        vx = ((np.arange(self.W) + 0.5) - self.pivot[0] * SS) / PPU
        vy = ((np.arange(self.H) + 0.5) - self.pivot[1] * SS) / PPU
        sm = -vy[:, None] / (_s * _a)
        df = vx[None, :] / _a
        return (sm + df) / 2, (sm - df) / 2

    def fx(self, rgb_linear, alpha):
        """Soft layer at hi-res (H*SS, W*SS), composited over the finished object before the downsample.
        Only this layer may leave the silhouette."""
        self.fx_layers.append((np.broadcast_to(np.asarray(rgb_linear, dtype=F32), (self.H, self.W, 3)),
                               np.broadcast_to(np.asarray(alpha, dtype=F32), (self.H, self.W))))

    # rendering --------------------------------------------------------------------------

    def _merged(self):
        Vs, Fs, Ns, UVs, TIs, flat, mat, mesh_id = [], [], [], [], [], [], [], []
        off = 0
        for mi, (mesh, mid, _, _) in enumerate(self.items):
            nv, nf = len(mesh.v), len(mesh.f)
            Vs.append(mesh.v.astype(np.float64))
            Fs.append(mesh.f + off)
            Ns.append(np.zeros((nv, 3)) if mesh.n is None else mesh.n.astype(np.float64))
            UVs.append(_or(mesh.uv, nv, 2, 0.0).astype(np.float64))
            TIs.append(_or(mesh.tint, nv, 3, 1.0).astype(np.float64))
            flat.append(np.full(nf, mesh.n is None))
            mat.append(np.full(nf, mid, dtype=np.int32))
            mesh_id.append(np.full(nf, mi, dtype=np.int32))
            off += nv
        return (np.concatenate(Vs), np.concatenate(Fs).astype(np.int64), np.concatenate(Ns), np.concatenate(UVs),
                np.concatenate(TIs), np.concatenate(flat), np.concatenate(mat), np.concatenate(mesh_id))

    def render(self, wear=0.3, grime=0.5, rust=0.2, dust=0.1, moss=0.0, exposure=1.0, hard_alpha=False, detail=1.0) -> Rendered:
        H, W = self.H, self.W
        V, F, Nv, UV, TI, flat, tmat, tmesh = self._merged()
        vx, vy, vz = to_view(V)
        sx, sy = self.pivot[0] * SS + vx * PPU, self.pivot[1] * SS + vy * PPU
        zbuf, tid, coef = _raster(sx, sy, vz, F, W, H)
        ys, xs = np.nonzero(tid >= 0)
        t = tid[ys, xs]
        cf = coef[t]
        X, Y = xs + 0.5, ys + 0.5
        l1 = cf[:, 0] * X + cf[:, 1] * Y + cf[:, 2]
        l2 = cf[:, 3] * X + cf[:, 4] * Y + cf[:, 5]
        l0 = 1 - l1 - l2
        tri = F[t]

        def lerp(A):
            return l0[:, None] * A[tri[:, 0]] + l1[:, None] * A[tri[:, 1]] + l2[:, None] * A[tri[:, 2]]

        Pw = lerp(V)
        uv = lerp(UV)
        tint = lerp(TI)
        # world normals: flat from the winding, smooth from the vertices; always facing the viewer per triangle
        fn = _face_normals(V, F)
        fn /= np.maximum(np.linalg.norm(fn, axis=1, keepdims=True), 1e-12)
        nsm = lerp(Nv)
        nsm /= np.maximum(np.linalg.norm(nsm, axis=1, keepdims=True), 1e-12)
        isflat = flat[t]
        Nw = np.where(isflat[:, None], fn[t], nsm)
        ref = np.where(isflat[:, None], fn[t], Nv[tri[:, 0]] + Nv[tri[:, 1]] + Nv[tri[:, 2]])
        Nw = np.where(((ref @ AX_V) < 0)[:, None], -Nw, Nw)
        mid = tmat[t]
        npx = len(t)

        # bump from the material detail layers
        grad = np.zeros((npx, 3))
        for k, m in enumerate(self.mats):
            if not m.detail:
                continue
            sel = np.nonzero(mid == k)[0]
            if not len(sel):
                continue
            for wv in m.detail:
                _, g = waves3(Pw[sel], wv.name, wv.n, wv.lo, wv.hi, wv.stretch)
                grad[sel] += g * (wv.amp * detail)
        Nb = Nw - (grad - (grad * Nw).sum(1, keepdims=True) * Nw)
        Nb /= np.linalg.norm(Nb, axis=1, keepdims=True)

        # shadow and ambient occlusion
        key = _Occluder(V, F, KEY_WORLD, PPU, 0.0)
        lit = 1 - key.occluded(Pw + Nw * 0.07, 0.035)
        dirs = _dome_dirs(20)
        occs = [_Occluder(V, F, d, PPU / 4, 1.5) for d in dirs]
        Wref = np.maximum(dirs[:, 2], 0).sum()
        ao_s = np.zeros(npx)
        wsum = np.zeros(npx)
        Pq = Pw + Nw * 0.12
        for d, oc in zip(dirs, occs):
            w_ = np.maximum(Nw @ d, 0)
            sin_t = np.sqrt(np.clip(1 - w_ * w_, 0, 1))
            vis = 1 - oc.occluded(Pq, 0.08 + 0.30 * sin_t, tap=0.5)
            ao_s += vis * w_
            wsum += w_
        ao_dome = ao_s / np.maximum(wsum, 0.3 * Wref)
        ao_dome = np.clip(ao_dome * (1 - 0.5 * (1 - smooth(0.2, 3.5, Pw[:, 2]))), 0, 1)

        def plane(a, bg):
            o = np.full((H, W) + a.shape[1:], bg, dtype=F32)
            o[ys, xs] = a
            return o

        cov = tid >= 0
        zimg = plane(lerp(vz[:, None])[:, 0], 0.0)
        ao_fine = _depth_ao(zimg, cov)
        aod = plane(ao_dome, 1.0)
        aod = _masked_blur(aod, cov, 1.2)
        aod = np.where(cov, aod, 1.0)
        ao = np.clip(aod * ao_fine, 0, 1).astype(F32)
        shadow = plane(lit, 1.0)

        nview = world_to_view(Nb)
        nview[:, 2] = np.maximum(nview[:, 2], 0.05)
        nview /= np.linalg.norm(nview, axis=1, keepdims=True)
        ngeo = world_to_view(Nw)
        ngeo[:, 2] = np.maximum(ngeo[:, 2], 0.05)
        ngeo /= np.linalg.norm(ngeo, axis=1, keepdims=True)
        normal = plane(nview, 0.0)
        normal[~cov] = (0, 0, 1)
        ngm = plane(ngeo, 0.0)

        # form maps from the normals, away from silhouette depth jumps
        zp = np.pad(zimg, 1, mode="edge")
        cp = np.pad(cov, 1)
        jump = np.zeros_like(cov)
        for oy, ox in ((0, 1), (2, 1), (1, 0), (1, 2)):
            nz_ = zp[oy : oy + H, ox : ox + W]
            nc_ = cp[oy : oy + H, ox : ox + W]
            jump |= (~nc_) | (np.abs(nz_ - zimg) > 0.8)
        form = cov & ~(blur(jump.astype(F32), 2.0) > 0.12)

        def div(nimg, sigma):
            sm_ = _masked_blur(nimg[..., :2], form, sigma)
            gx = np.gradient(sm_[..., 0], axis=1)
            gy = np.gradient(sm_[..., 1], axis=0)
            return np.where(form, (gx + gy) * PPU, 0.0)

        d_edge = div(ngm, 0.30 * PPU)
        d_fine = div(normal, 0.10 * PPU)
        d_mid = div(ngm, 0.22 * PPU)
        edge = smooth(0.35, 2.4, d_edge).astype(F32)
        curv = np.tanh(d_fine * 0.5).astype(F32)
        cav_geo = np.clip(-d_mid / 1.4, 0, 1)
        cav_fine = np.clip(-d_fine / 2.2, 0, 1)
        cavity = (1 - (1 - cav_geo) * (1 - cav_fine) * (1 - 0.9 * smooth(0.15, 0.7, 1 - ao))).astype(F32)
        maps = forge.Maps(normal.astype(F32), ao, shadow.astype(F32), cavity, curv, edge)

        # surface
        lut = lambda f: np.array([np.broadcast_to(np.array(f(m), dtype=F32), (3,)) for m in self.mats], dtype=F32)
        albedo_p = lut(lambda m: m.albedo)[mid]
        f0_p = lut(lambda m: m.f0)[mid]
        bare_a_p = lut(lambda m: m.bare_albedo if m.bare_albedo is not None else m.albedo)[mid]
        bare_f_p = lut(lambda m: m.bare_f0 if m.bare_f0 is not None else m.f0)[mid]
        rough_p = np.array([m.rough for m in self.mats], dtype=F32)[mid]
        wear_p = np.array([m.wear for m in self.mats], dtype=F32)[mid]
        cap_p = np.array([m.sat_cap for m in self.mats], dtype=F32)[mid]
        emit_p = lut(lambda m: m.emit if m.emit is not None else (0, 0, 0))[mid]
        for mi, (_, _, _, efn) in enumerate(self.items):
            if efn is not None:
                sel = np.nonzero(tmesh[t] == mi)[0]
                if len(sel):
                    emit_p[sel] += np.asarray(efn(uv[sel]), dtype=F32)
        # object-space patina: broad mottling and a finer break-up, so surfaces are never one flat value
        mott = 0.5 + 0.5 * np.tanh(waves3(Pw, self.name + ".mott", 10, 3.0, 14.0)[0] * 0.8)
        pat = 0.5 + 0.5 * np.tanh(waves3(Pw, self.name + ".pat", 8, 2.0, 6.0, (1.0, 1.0, 0.5))[0] * 1.1)
        k = 0.80 + 0.40 * (0.55 * mott + 0.45 * pat)
        albedo_p = albedo_p * k[:, None] * tint
        f0_p = f0_p * (0.6 + 0.4 * k)[:, None]
        rough_p = np.clip(rough_p + 0.10 * (mott - 0.5), 0.12, 1.0)
        surf = forge.Surface(plane(albedo_p, 0.0), plane(f0_p, 0.0), plane(rough_p, 1.0), np.zeros((H, W), F32), np.zeros((H, W), F32),
                             cov.astype(F32), plane(bare_a_p, 0.0), plane(bare_f_p, 0.0), plane(np.clip(rough_p - 0.15, 0.5, 1.0), 1.0),
                             plane(wear_p, 1.0), plane(emit_p, 0.0))
        streak = self._runoff(edge, cov, form, shadow)
        s = forge.age(surf, maps, self.name, wear, grime, 0.0, dust, streak=streak, origin=(0.0, 0.0))
        if rust > 0:
            m_rust = self._rust(s, maps, streak, Pw, xs, ys, rust)
            cap_p = np.maximum(cap_p, cap_p + (RUST_CAP - cap_p) * m_rust)
        if moss > 0:
            self._moss(s, Pw, Nw, ao_dome, xs, ys, moss)
        hdr = forge.light(s, maps, ISO_RIG)
        out = forge.finish(hdr, cov.astype(F32), exposure)
        cap_img = plane(cap_p, 0.35)
        out[..., :3] = _grade(out[..., :3], cap_img)
        out[~cov, :3] = 0

        # fx layer over the finished object
        alpha_hi = cov.astype(F32)
        if self.fx_layers:
            lin = U.srgb_to_linear(out[..., :3]) * alpha_hi[..., None]
            for frgb, fa in self.fx_layers:
                lin = frgb * fa[..., None] + lin * (1 - fa)[..., None]
                alpha_hi = fa + alpha_hi * (1 - fa)
            rgb = U.linear_to_srgb(lin / np.maximum(alpha_hi, 1e-6)[..., None])
            out = np.dstack((rgb, alpha_hi)).astype(F32)
            out[alpha_hi <= 0, :3] = 0
        # downsample: forge for colour, box coverage for a one-pixel crisp edge
        fin = forge.downsample(out)
        a_f = alpha_hi.reshape(self.h, SS, self.w, SS).mean(axis=(1, 3))
        a_u8 = np.floor(a_f * 255 + 0.5).astype(np.uint8)
        if hard_alpha:
            a_u8 = np.where(a_f >= 0.5, 255, 0).astype(np.uint8)
        known = fin.copy()
        known[..., 3] = np.where(fin[..., 3] > 0, 255, 0)
        fin[..., :3] = U.bleed_rgb(known)[..., :3]
        fin[..., 3] = a_u8
        fin[a_u8 == 0, :3] = 0
        fin = U.bleed_rgb(fin)
        return Rendered(fin, self._shadow_layer(key, occs, dirs), {"tris": int(len(F)), "hi_px": int(H * W), "covered": int(cov.sum())})

    # helpers ----------------------------------------------------------------------------

    def _runoff(self, edge, cov, form, shadow):
        """Rain and rust runoff: streaks that fall from convex horizontal edges, broken into vertical runs by
        world-space noise stretched along z. Returned as the 0..1 drip mask forge.age wants."""
        H, W = cov.shape
        src = (edge > 0.35) & cov
        acc = np.zeros((H, W), dtype=F32)
        for step in (3, 6, 10, 16, 24, 34, 46):
            sh = np.zeros((H, W), dtype=F32)
            sh[step:] = src[:-step].astype(F32)
            acc = np.maximum(acc, sh * (1 - step / 60.0))
        ys, xs = np.nonzero(cov)
        # stripes keyed to screen x in u: runs fall straight down the sprite
        stripe = 0.5 + 0.5 * np.tanh(forge.waves(xs[None, :] / PPU, np.zeros((1, len(xs))), self.name + ".runoff", 8, 2.0, 5.0, (1.0, 0.0))[0] * 1.4)
        m = np.zeros((H, W), dtype=F32)
        m[ys, xs] = stripe
        return (acc * smooth(0.45, 0.8, m) * cov).astype(F32)

    def _rust(self, s, maps, streak, Pw, xs, ys, amount):
        """Object-space rust: dark, rich, matte. It pools in cavities and seams (maps.cavity), runs down from
        convex edges (streak) and breaks up through world-space noise, so it never lies as a flat coat."""
        n1 = waves3(Pw, self.name + ".rust1", 8, 2.5, 9.0)[0] * 0.5 + 0.5
        n2 = waves3(Pw, self.name + ".rust2", 6, 0.5, 0.95)[0] * 0.5 + 0.5
        cav = smooth(0.55, 0.95, maps.cavity[ys, xs])
        drip = streak[ys, xs]
        worn = s.worn[ys, xs]
        metal = (s.bare_f0[ys, xs].mean(axis=1) > 0.08).astype(F32)     # pools only on iron; runoff stains anything
        src = np.maximum(cav * metal, drip * 0.9)
        thr = 0.78 - 0.55 * min(amount, 1.0)
        m = smooth(thr, thr + 0.20, n1 * 0.7 + n2 * 0.3 + 0.35 * src) * np.clip(src * 1.6, 0, 1) * (1 - worn)
        col = np.array((0.052, 0.0185, 0.0065), dtype=F32) * (0.45 + 0.9 * n2)[:, None]
        s.albedo[ys, xs] = s.albedo[ys, xs] * (1 - m)[:, None] + col * m[:, None]
        s.f0[ys, xs] = s.f0[ys, xs] * (1 - m)[:, None] + 0.025 * m[:, None]
        s.rough[ys, xs] = s.rough[ys, xs] * (1 - m) + 0.88 * m
        return m

    def _moss(self, s, Pw, Nw, ao_dome, xs, ys, amount):
        """Object-space moss on up-facing and low surfaces, and pale lichen specks, from waves3."""
        m = ISO_MATERIALS["moss"]
        up = smooth(0.15, 0.75, Nw[:, 2])
        low = 1 - 0.55 * smooth(2.0, 14.0, Pw[:, 2])
        n1 = waves3(Pw, self.name + ".moss", 8, 2.5, 9.0)[0] * 0.5 + 0.5
        n2 = waves3(Pw, self.name + ".moss2", 6, 2.0, 4.0)[0] * 0.5 + 0.5
        thr = 1.05 - 0.8 * amount
        mask = smooth(thr - 0.08, thr + 0.18, n1 * (0.45 + 0.55 * up) * low * (0.65 + 0.35 * ao_dome) + 0.2 * n2 * up)
        lich = smooth(0.78, 0.88, waves3(Pw, self.name + ".lichen", 6, 0.5, 0.95)[0] * 0.5 + 0.5) * amount * (1 - mask) * up
        alb = s.albedo[ys, xs]
        f0 = s.f0[ys, xs]
        rough = s.rough[ys, xs]
        malb = np.array(m.albedo, dtype=F32) * (0.7 + 0.8 * n2)[:, None]
        lalb = np.array((0.16, 0.16, 0.115), dtype=F32)
        alb = alb * (1 - mask - lich)[:, None] + malb * mask[:, None] + lalb * lich[:, None]
        f0 = f0 * (1 - mask - lich)[:, None] + 0.02 * (mask + lich)[:, None]
        rough = rough * (1 - mask - lich) + 0.94 * (mask + lich)
        s.albedo[ys, xs], s.f0[ys, xs], s.rough[ys, xs] = alb, f0, rough

    def _shadow_layer(self, key, occs, dirs) -> np.ndarray:
        """Ground shadow: crisp key cast shadow plus dome AO evaluated on z = 0. Black RGB, soft alpha."""
        X, Y = self.ground_uv()
        P = np.stack((X.ravel(), Y.ravel(), np.full(X.size, 0.04)), axis=-1)
        cast = key.occluded(P, 0.03)
        wsum = np.maximum(dirs[:, 2], 0).sum()
        vis = np.zeros(len(P))
        for d, oc in zip(dirs, occs):
            vis += (1 - oc.occluded(P, 0.06, tap=0.5)) * d[2]
        ao = vis / wsum
        a = 1 - (1 - 0.60 * cast) * (1 - 0.75 * np.clip(1 - ao, 0, 1) ** 3.0)
        a = a.reshape(self.H, self.W).astype(F32)
        af = a.reshape(self.h, SS, self.w, SS).mean(axis=(1, 3))
        out = np.zeros((self.h, self.w, 4), dtype=np.uint8)
        out[..., 3] = np.floor(np.clip(af, 0, 1) * 255 + 0.5).astype(np.uint8)
        return out
