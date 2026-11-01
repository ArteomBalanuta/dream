"""Isometric relics: the cracked tombstone, the bone pile with chains and the falling rune potion.

Form bands (u): primary 8 and up (the slab, the pile, the bottle), secondary 2..6 (cracks, chips, ribs, links),
tertiary under 1 (grain, pitting, scratches). Nothing sits in the 1..2 u gap on purpose.
"""
from __future__ import annotations

import math

import numpy as np

import uiart as U
from isoforge import IsoMat, Mesh, ObjectSpec, Scene, Waves3, box, convex, grid, lathe, waves3

F32 = np.float32


# shared helpers ------------------------------------------------------------------------------


def smoothstep(a, b, x):
    t = np.clip((np.asarray(x, dtype=np.float64) - a) / (b - a), 0, 1)
    return t * t * (3 - 2 * t)


def scaled(m: Mesh, sx: float, sy: float, sz: float) -> Mesh:
    """Non-uniform scale about the origin; smooth normals follow the inverse transpose."""
    s = np.array((sx, sy, sz))
    n = None
    if m.n is not None:
        n = m.n.astype(np.float64) / s
        n /= np.maximum(np.linalg.norm(n, axis=1, keepdims=True), 1e-12)
    return Mesh(m.v.astype(np.float64) * s, m.f.copy(), n, m.uv, m.tint)


def blob(rx: float, ry: float, rz: float, rows: int = 12, sides: int = 20) -> Mesh:
    """Ellipsoid centred on the origin (smooth)."""
    a = np.linspace(-math.pi / 2, math.pi / 2, rows + 1)
    prof = [(max(math.cos(t), 0.0), math.sin(t)) for t in a]
    return scaled(lathe(prof, sides), rx, ry, rz)


def seg_dist(px, pz, poly):
    """Distance and signed side from points to a polyline in a plane (vectorised over points)."""
    best = np.full(px.shape, 1e9)
    side = np.zeros(px.shape)
    for (x0, z0), (x1, z1) in zip(poly[:-1], poly[1:]):
        dx, dz = x1 - x0, z1 - z0
        l2 = dx * dx + dz * dz
        t = np.clip(((px - x0) * dx + (pz - z0) * dz) / l2, 0, 1)
        qx, qz = x0 + t * dx, z0 + t * dz
        d = np.hypot(px - qx, pz - qz)
        sg = np.sign((px - x0) * dz - (pz - z0) * dx)
        upd = d < best
        best = np.where(upd, d, best)
        side = np.where(upd, sg, side)
    return best, side


# tombstone ------------------------------------------------------------------------------------

TS_SIZE = (144, 144)
TS_PIVOT = (46, 118)
TS_W, TS_H, TS_SH, TS_T = 7.4, 28.0, 21.0, 5.4
TS_YAW = -80.0

# stone, a touch warmer and darker than the shared granite so it does not read as concrete
TOMB_STONE = IsoMat((0.054, 0.056, 0.058), (0.040, 0.040, 0.039), 0.80, (0.105, 0.098, 0.090), (0.045, 0.045, 0.044), 0.7, sat_cap=0.28,
                    detail=(Waves3("tomb.chip", 6, 3.0, 6.0, 0.05), Waves3("tomb.grit", 8, 0.35, 0.9, 0.010)))


def _tomb_outline(z):
    """Left and right edge x at height z: a round-shouldered slab, chipped on both shoulders."""
    z = np.asarray(z, dtype=np.float64)
    k = np.clip((z - TS_SH) / (TS_H - TS_SH), 0, 1)
    half = TS_W * np.sqrt(np.maximum(1 - k * k, 0))
    # base flares a hair, the slab is not a perfect rectangle
    half = half + 0.35 * (1 - smoothstep(0, 7, z))
    left = -half
    right = half
    # chipped left shoulder: a bite taken out between z 18.5 and 23
    bite = np.interp(z, (17.6, 19.0, 20.2, 21.4, 22.6, 24.2), (0, 2.6, 1.7, 3.4, 1.0, 0))
    left = left + bite * 0.9
    # broken right edge: jagged steps lower down
    step = np.interp(z, (6.0, 7.2, 8.4, 9.6, 11.8, 12.6, 14.0), (0, 0.8, 0.3, 1.5, 1.1, 0.2, 0))
    right = right - step
    return left, right


CRACK = [(3.6, 24.4), (2.6, 21.0), (3.2, 18.6), (1.0, 16.0), (1.6, 12.8), (-0.9, 10.2), (-0.2, 7.0), (-2.6, 4.2), (-2.0, 0.0)]
CRACK_BRANCH = [(1.3, 15.6), (-2.4, 14.2), (-3.2, 11.4)]
CHIPS = ((-6.4, 20.0, 2.3, 0.9), (6.2, 7.4, 2.0, 1.1), (-5.8, 1.6, 2.6, 1.0), (4.5, 22.2, 1.4, 0.8), (-1.5, 24.0, 1.6, 0.7), (6.8, 13.0, 1.9, 0.8))


def _slab() -> Mesh:
    nz, nx = 150, 64
    zs = np.linspace(0, TS_H, nz)
    zs = zs ** 1.0
    left, right = _tomb_outline(zs)
    ch = 0.62
    # columns: edge (recessed), bevel foot, then the face, bevel foot, edge
    t = np.concatenate(([0.0], np.linspace(0, 1, nx)))
    P = np.zeros((nz, nx + 2, 3))
    for i, z in enumerate(zs):
        xs = np.concatenate(([left[i]], left[i] + ch + (right[i] - left[i] - 2 * ch) * np.linspace(0, 1, nx), [right[i]]))
        xs[1:-1] = np.clip(xs[1:-1], left[i], right[i])
        P[i, :, 0] = xs
        P[i, :, 2] = z
    P[:, :, 1] = -TS_T / 2
    P[:, 0, 1] += ch
    P[:, -1, 1] += ch
    # relief on the face in (x, z): crack groove with a slipped edge, chip craters, a worn shallow cross
    X, Z = P[..., 0], P[..., 2]
    y = np.zeros_like(X)
    interior = np.ones(X.shape)
    interior[:, 0] = interior[:, -1] = 0
    d, sd = seg_dist(X, Z, CRACK)
    taper = 0.55 + 0.45 * smoothstep(0, 24.5, Z)                 # wide at the top, closing towards the ground
    w = 0.80 * taper + 0.22
    groove = np.clip(1 - d / w, 0, 1)
    y += groove * 1.7
    slip = smoothstep(2.2, 0.0, d) * sd * 0.34                    # one side stands proud of the other
    y -= slip * (0.4 + 0.6 * smoothstep(0, 6, Z)) * smoothstep(1.5, 7.0, Z)
    d2, _ = seg_dist(X, Z, CRACK_BRANCH)
    y += np.clip(1 - d2 / 0.40, 0, 1) * 0.62
    for cx, cz, r, dep in CHIPS:
        dd = np.hypot(X - cx, Z - cz) / r
        y += dep * 0.55 * np.clip(1 - dd * dd, 0, 1) ** 0.8
    # shallow weathered cross, cut and then eaten away
    cross = np.minimum(np.where(np.abs(Z - 13.2) < 0.55, np.clip(1 - np.abs(X) / 3.4, 0, 1) * 0 + (np.abs(X) < 3.4), 0),
                       1.0) + np.where(np.abs(X) < 0.55, (Z > 6.2) & (Z < 19.5), 0)
    cross = np.clip(cross, 0, 1) * smoothstep(-0.5, 0.5, waves3(np.stack((X, Z * 0, Z), -1), "relics.tomb.cross", 6, 3.0, 6.0)[0])
    y += cross * 0.30
    # cleavage: the face breaks into 3..5 u planes, each tilted a little, with a crisp step where they meet
    rf = U.rng("relics.tomb.facets")
    seeds = np.column_stack((rf.uniform(-6.5, 6.5, 16), rf.uniform(1.0, 26.0, 16)))
    dd = np.hypot(X[..., None] - seeds[:, 0], (Z[..., None] - seeds[:, 1]) * 0.85)
    near = dd.argmin(-1)
    tilt = rf.uniform(-0.045, 0.045, (16, 2))
    off = rf.uniform(-0.12, 0.08, 16)
    y += off[near] + tilt[near, 0] * (X - seeds[near, 0]) + tilt[near, 1] * (Z - seeds[near, 1])
    P[..., 1] += y * interior
    # stand a little off true: lean back and to the screen-left, base stays on the ground
    P[..., 1] += P[..., 2] * 0.055
    P[..., 0] += P[..., 2] * -0.045
    uv = np.stack((np.broadcast_to(np.linspace(0, 1, nx + 2)[None, :], (nz, nx + 2)), np.broadcast_to((zs / TS_H)[:, None], (nz, nx + 2))), -1)
    front = grid(P, uv, smooth_normals=False)
    # sides: connect the edge column of the front to the back, left and right; back plane is coarse
    back = P[:, [0, -1], :].copy()
    back[..., 1] = TS_T / 2 - ch + back[..., 2] * 0.055
    back[:, 0, 0] = P[:, 0, 0]
    back[:, 1, 0] = P[:, -1, 0]
    out = front
    for c in (0, 1):
        edge = P[:, 0 if c == 0 else -1, :]
        rear = back[:, c, :]
        strip = grid(np.stack((edge, rear), axis=1), None, smooth_normals=False)
        out = out.merge(strip)
    rows = np.linspace(0, nz - 1, 24).astype(int)
    bk = np.stack([np.linspace(back[r, 0], back[r, 1], 4) for r in rows])
    out = out.merge(grid(bk[:, ::-1], None, smooth_normals=False))
    return out.displace("relics.tomb.warp", 0.07, 3.0, 7.0)


def _earth_mound() -> Mesh:
    """A low rim of disturbed earth where the slab is sunk, thin and irregular; the ground itself is not drawn."""
    r = U.rng("relics.tomb.mound")
    nr, na = 6, 72
    ang = np.linspace(0, math.tau, na + 1)
    out_r = 10.5 + 2.2 * waves3(np.stack((np.cos(ang) * 4, np.sin(ang) * 4, 0 * ang), -1), "relics.tomb.mr", 5, 4.0, 8.0)[0]
    P = np.zeros((nr, na + 1, 3))
    for i in range(nr):
        f = i / (nr - 1)
        rr = 6.2 + (out_r - 6.2) * f
        hh = 3.0 * (1 - f) ** 1.6
        P[i, :, 0] = np.cos(ang) * rr * 1.05
        P[i, :, 1] = np.sin(ang) * rr * 0.62
        P[i, :, 2] = hh
    return grid(P[::-1], None, smooth_normals=True)


def _stones(seed: str, spots) -> Mesh:
    out = None
    for i, (x, y, rad) in enumerate(spots):
        m = convex(f"{seed}.{i}", rad, 5, (1.0, 0.8, 0.55)).xform((0, 0, 37 * i), (x, y, rad * 0.30))
        out = m if out is None else out.merge(m)
    return out


def _moss_clump(rx, ry, rz, name, at):
    b = blob(rx, ry, rz, 8, 16).displace(name, 0.35, 2.2, 4.0, along_normal=False)
    return b.xform((0, 0, 0), at)


EARTH = IsoMat((0.024, 0.021, 0.018), (0.020, 0.020, 0.020), 0.95, sat_cap=0.22,
               detail=(Waves3("earth.clod", 6, 3.0, 6.0, 0.10),))
MOSS_SPOTS = ((-4.4, -3.4, 0.8, 2.6, 1.3, 1.3), (1.0, -3.6, 0.5, 2.2, 1.1, 1.0), (5.2, -3.4, 0.6, 2.0, 1.2, 1.1),
              (-3.0, -1.6, 27.0, 3.0, 1.9, 1.0), (-0.6, -1.0, 27.5, 2.4, 1.6, 0.8), (-6.4, -2.4, 17.6, 1.4, 1.2, 0.9),
              (-2.9, -3.1, 3.8, 1.4, 0.9, 1.1))


def tomb_scene() -> Scene:
    sc = Scene("tombstone", TS_SIZE[0], TS_SIZE[1], TS_PIVOT)
    sc.add(_slab().xform((0, 0, TS_YAW)), TOMB_STONE, "tomb.slab")
    sc.add(_earth_mound().xform((0, 0, TS_YAW)), EARTH, "tomb.earth")
    sc.add(_stones("tomb.rub", ((-5.5, -5.2, 1.5), (6.8, -4.6, 1.1), (3.0, -6.4, 0.9), (-9.5, -1.0, 1.0), (9.0, 2.0, 1.2))).xform((0, 0, TS_YAW)), TOMB_STONE, "tomb.rub")
    mo = None
    for i, (x, y, z, rx, ry, rz) in enumerate(MOSS_SPOTS):
        m = _moss_clump(rx, ry, rz, f"relics.tomb.moss{i}", (x, y, z))
        mo = m if mo is None else mo.merge(m)
    sc.add(mo.xform((0, 0, TS_YAW)), "moss", "tomb.moss")
    return sc


def render_tomb():
    return tomb_scene().render(wear=0.3, grime=0.6, rust=0.0, dust=0.1, moss=0.5, exposure=1.0)


# bone pile ------------------------------------------------------------------------------------

BP_SIZE = (208, 144)
BP_PIVOT = (104, 92)
BP_YAW = -45.0

# old bone: dull, stained, never white. The default bone material is brighter than this set wants.
OLD_BONE = IsoMat((0.060, 0.054, 0.046), (0.030, 0.030, 0.028), 0.88, wear=0.25, sat_cap=0.22,
                  detail=(Waves3("obone.grain", 6, 0.5, 1.0, 0.010, (1.0, 1.0, 0.3)), Waves3("obone.dent", 6, 2.5, 5.0, 0.035)))
CHAIN_IRON = IsoMat((0.013, 0.011, 0.0095), (0.040, 0.036, 0.032), 0.78, (0.020, 0.019, 0.018), (0.11, 0.105, 0.10), 0.8, sat_cap=0.32,
                    detail=(Waves3("chain.pit", 6, 0.4, 0.9, 0.012), Waves3("chain.dent", 5, 2.5, 5.0, 0.03)))


def stain(m: Mesh, name: str) -> Mesh:
    """Per-vertex age stain: yellowed and browned in patches (a tint, so the albedo stays dull)."""
    n = waves3(m.v.astype(np.float64), name, 6, 3.0, 9.0)[0] * 0.5 + 0.5
    tint = np.stack((1.0 - 0.12 * n, 1.0 - 0.20 * n, 1.0 - 0.30 * n), -1)
    return Mesh(m.v, m.f, m.n, m.uv, tint)


def catmull(path, step):
    p = np.asarray(path, dtype=np.float64)
    ext = np.vstack((2 * p[0] - p[1], p, 2 * p[-1] - p[-2]))
    pts = []
    for i in range(len(p) - 1):
        p0, p1, p2, p3 = ext[i], ext[i + 1], ext[i + 2], ext[i + 3]
        t = np.linspace(0, 1, 24, endpoint=False)[:, None]
        pts.append(0.5 * ((2 * p1) + (-p0 + p2) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t ** 2 + (-p0 + 3 * p1 - 3 * p2 + p3) * t ** 3))
    pts.append(p[-1:])
    d = np.vstack(pts)
    s = np.concatenate(([0.0], np.cumsum(np.linalg.norm(np.diff(d, axis=0), axis=1))))
    q = np.arange(0.0, s[-1], step)
    return np.stack([np.interp(q, s, d[:, c]) for c in range(3)], axis=1)


def long_bone(a, b, r, knob, bow, name, ends=(1.0, 1.0)):
    """Shaft with a slight bow and swollen ends (a head and a pair of condyles)."""
    a, b = np.asarray(a, dtype=np.float64), np.asarray(b, dtype=np.float64)
    ax = (b - a) / np.linalg.norm(b - a)
    side = np.cross(ax, (0, 0, 1.0))
    side /= np.linalg.norm(side)
    mid = (a + b) / 2 + side * bow
    shaft = tube([a, mid, b], [r * 1.5, r, r, r * 1.5], sides=8, seg_u=1.6)
    parts = [shaft]
    k0, k1 = knob * ends[0], knob * ends[1]
    parts.append(blob(k0 * 1.0, k0 * 0.9, k0 * 0.9, 6, 12).xform((0, 0, 0), a + side * k0 * 0.35))
    parts.append(blob(k1 * 0.8, k1 * 0.8, k1 * 0.8, 6, 12).xform((0, 0, 0), b + side * k1 * 0.5))
    parts.append(blob(k1 * 0.8, k1 * 0.8, k1 * 0.8, 6, 12).xform((0, 0, 0), b - side * k1 * 0.5))
    out = parts[0]
    for q in parts[1:]:
        out = out.merge(q)
    return stain(out.displace(name, 0.10, 2.5, 5.0, along_normal=False), name + ".s")


def tube(path, radii, sides=8, seg_u=1.5):
    from isoforge import tube as _tube
    return _tube(path, radii, sides=sides, seg_u=seg_u)


def _skull(name: str, jaw: bool = True) -> Mesh:
    rows, cols = 30, 44
    lat = np.linspace(-math.pi / 2, math.pi / 2, rows)
    lon = np.linspace(0, math.tau, cols + 1)
    la, lo = np.meshgrid(lat, lon, indexing="ij")
    d = np.stack((np.cos(la) * np.cos(lo), np.cos(la) * np.sin(lo), np.sin(la)), -1)
    s = np.ones(la.shape)

    def well(c, wid, amp):
        c = np.asarray(c, dtype=np.float64)
        c /= np.linalg.norm(c)
        ang = np.arccos(np.clip(d @ c, -1, 1))
        return amp * np.exp(-(ang / wid) ** 2)

    s -= well((0.40, -0.90, 0.02), 0.32, 0.50) + well((-0.40, -0.90, 0.02), 0.32, 0.50)   # eye sockets
    s -= well((0.0, -1.0, -0.34), 0.20, 0.55)                                              # nasal opening
    s += well((0.80, -0.55, -0.28), 0.34, 0.10) + well((-0.80, -0.55, -0.28), 0.34, 0.10)  # cheekbones
    s += well((0.0, -0.96, 0.30), 0.30, 0.07)                                              # brow
    P = d * s[..., None] * np.array((3.5, 4.3, 3.7))
    front = np.clip(-d[..., 1], 0, 1)
    low = smoothstep(0.0, -0.75, d[..., 2])
    P[..., 0] *= 1 - 0.26 * low * front                                                    # the muzzle is narrower than the cranium
    P[..., 2] = np.maximum(P[..., 2], -2.55)                                               # flat underside
    out = grid(P, None, smooth_normals=True)
    parts = [out]
    for i in range(10):                                                                    # teeth, some lost
        if i in (3, 7):
            continue
        x = (i - 4.5) * 0.48
        t = box(0.42, 0.36, 0.7 - 0.08 * (i % 3), 0.06).xform((0, 0, -x * 18), (x, -3.05 + 0.5 * (x / 2.4) ** 2 * 0 - 0.18 * abs(x) / 2.2, -3.25))
        parts.append(t)
    if jaw:
        jaw_m = tube([(-2.7, 0.2, -3.0), (-2.0, -2.4, -3.75), (0.0, -3.5, -4.0), (2.0, -2.4, -3.75), (2.7, 0.2, -3.0)], [0.75, 0.55, 0.50, 0.55, 0.75], sides=8, seg_u=1.0)
        for sg in (-1, 1):
            jaw_m = jaw_m.merge(tube([(sg * 2.7, 0.2, -3.0), (sg * 2.9, 0.9, -1.6), (sg * 2.8, 1.0, -0.6)], [0.75, 0.6, 0.55], sides=8, seg_u=1.0))
        for i in range(9):
            if i in (2, 6):
                continue
            x = (i - 4.0) * 0.52
            jaw_m = jaw_m.merge(box(0.40, 0.34, 0.55, 0.06).xform((0, 0, -x * 16), (x, -3.2 + 0.18 * (x / 2.2) ** 2 * 0 + 0.25 * abs(x) / 2.2 * -1, -3.55)))
        parts.append(jaw_m.xform((6, 0, 0), (0, 0.4, -0.6)))
    m = parts[0]
    for q in parts[1:]:
        m = m.merge(q)
    return stain(m.displace(name, 0.05, 2.5, 5.0, along_normal=False), name + ".s")


def _link(c, t, up, alt, wire=0.44, half_len=1.75, half_w=1.05):
    """One chain link: a stadium loop of wire, in the plane spanned by the tangent t and the side axis."""
    t = np.asarray(t, dtype=np.float64)
    t /= np.linalg.norm(t)
    w1 = np.cross(up, t)
    w1 /= np.linalg.norm(w1)
    w2 = np.cross(t, w1)
    if alt:
        w1, w2 = w2, -w1
    n = 44
    s = np.linspace(0, 1, n + 1)
    straight = half_len - half_w
    per = 4 * straight + 2 * math.pi * half_w
    L = s * per
    px, py = np.zeros(n + 1), np.zeros(n + 1)
    tx, ty = np.zeros(n + 1), np.zeros(n + 1)
    for i, l in enumerate(L):
        if l < 2 * straight:                       # top run, right to left
            px[i], py[i], tx[i], ty[i] = straight - l, half_w, -1, 0
        elif l < 2 * straight + math.pi * half_w:  # left cap
            a = (l - 2 * straight) / half_w
            px[i], py[i] = -straight - half_w * math.sin(a), half_w * math.cos(a)
            tx[i], ty[i] = -math.cos(a), -math.sin(a)
        elif l < 4 * straight + math.pi * half_w:  # bottom run
            q = l - 2 * straight - math.pi * half_w
            px[i], py[i], tx[i], ty[i] = -straight + q, -half_w, 1, 0
        else:                                      # right cap
            a = (l - 4 * straight - math.pi * half_w) / half_w
            px[i], py[i] = straight + half_w * math.sin(a), -half_w * math.cos(a)
            tx[i], ty[i] = math.cos(a), math.sin(a)
    cen = np.stack((px, py), -1)
    nor = np.stack((-ty, tx), -1)                 # in-plane normal
    th = np.linspace(0, math.tau, 9)
    pos2 = cen[:, None, :] + nor[:, None, :] * (wire * np.cos(th))[None, :, None]
    zz = (wire * np.sin(th))[None, :] + 0 * px[:, None]
    P = pos2[..., 0:1] * t + pos2[..., 1:2] * w1 + zz[..., None] * w2 + np.asarray(c)
    return grid(P, None, smooth_normals=True)


def _chain(path, spacing, name, up=(0.0, 0.0, 1.0), start_alt=0):
    pts = catmull(path, spacing)
    out = None
    for i in range(len(pts) - 1):
        t = pts[i + 1] - pts[i]
        c = pts[i]
        m = _link(c, t, np.asarray(up, dtype=np.float64), (i + start_alt) % 2)
        out = m if out is None else out.merge(m)
    return out.displace(name, 0.04, 2.5, 5.0)


def _rib(base, ydir, length, rise, curl, r, name):
    rr = U.rng(name)
    k = length * rr.uniform(0.85, 1.05)
    rise = rise * rr.uniform(0.7, 1.1)
    curl = curl * rr.uniform(0.4, 1.3)
    path = [(0, 0, 0), (0.4 * curl, ydir * 0.28 * k, rise * 0.75), (1.0 * curl, ydir * 0.60 * k, rise),
            (1.8 * curl, ydir * 0.88 * k, rise * 0.55), (2.2 * curl, ydir * k, rise * 0.12)]
    m = tube(path, [r * 1.2, r, r * 0.95, r * 0.8, r * 0.6], sides=6, seg_u=1.4).displace(name, 0.05, 2.5, 5.0, along_normal=False)
    return stain(m.xform((rr.uniform(-14, 14), rr.uniform(-9, 9), rr.uniform(-16, 16)), base), name + ".s")


def bone_scene() -> Scene:
    sc = Scene("bone_pile", BP_SIZE[0], BP_SIZE[1], BP_PIVOT)
    yaw = (0, 0, BP_YAW)
    bones = None

    def add(m):
        nonlocal bones
        bones = m if bones is None else bones.merge(m)

    # long bones first: they make the bed of the pile
    add(long_bone((-14, -6.0, 1.0), (-1.5, -5.5, 1.5), 0.85, 1.55, 0.8, "relics.femur0"))
    add(long_bone((0.0, 7.0, 1.0), (12.0, -6.5, 1.8), 0.85, 1.6, -0.7, "relics.femur1"))
    add(long_bone((6.0, 7.5, 0.9), (17.0, 0.5, 1.4), 0.70, 1.3, 0.6, "relics.tibia"))
    add(long_bone((-15.0, 3.0, 0.9), (-6.0, 8.0, 1.4), 0.62, 1.1, 0.5, "relics.humerus"))
    # spine and a half collapsed ribcage, fanning towards the viewer
    add(stain(tube([(-2.5, 3.0, 2.0), (1.5, 4.2, 2.4), (5.5, 5.0, 2.2), (9.5, 5.0, 1.6)], 0.7, sides=7, seg_u=1.2).displace("relics.spine", 0.1, 2.5, 5.0, along_normal=False), "relics.spine.s"))
    for i in range(7):
        sx = -1.5 + i * 1.8
        sy = 3.3 + 0.15 * i
        add(_rib((sx, sy, 2.0 + 0.05 * i), -1, 10.5 - 0.5 * abs(i - 3), 4.6 + 0.2 * i - 0.5 * abs(i - 4), 0.8, 0.74, f"relics.rib{i}"))
    for i in range(6):                                                      # vertebrae along the spine
        add(stain(blob(1.0, 1.1, 0.9, 6, 10).displace(f"relics.vert{i}", 0.08, 2.5, 4.0, along_normal=False).xform((0, 0, 40 * i), (-2 + i * 2.2, 3.2 + 0.2 * i, 2.5)), f"relics.vs{i}"))
    # skulls: the whole one rests on the long bones at the left, a cracked cap lies behind it
    add(_skull("relics.skull0").xform((-8, 0, -25), (-7.5, -1.5, 5.2), 1.25))
    add(long_bone((-3.5, -3.0, 1.0), (-8.0, -0.5, 4.0), 0.8, 1.5, 0.5, "relics.femur2"))
    add(long_bone((-3.0, 2.5, 6.2), (10.0, -3.5, 6.0), 0.78, 1.5, 0.6, "relics.femur3"))
    add(long_bone((13.0, -3.0, 1.4), (8.0, -2.0, 5.0), 0.7, 1.3, -0.4, "relics.tibia2"))
    add(_skull("relics.skull1", jaw=False).xform((20, 15, 150), (-12.5, 5.0, 2.6), 0.8))
    # small bones: finger and foot bones
    r = U.rng("relics.small")
    for i in range(9):
        a = r.uniform(0, math.tau)
        c = (r.uniform(-8, 12), r.uniform(-6, 3), 0.7)
        add(long_bone((c[0], c[1], c[2]), (c[0] + 2.8 * math.cos(a), c[1] + 2.8 * math.sin(a), c[2] + 0.15), 0.42, 0.7, 0.15, f"relics.small{i}"))
    bed = blob(12.5, 6.5, 2.0, 8, 28).displace("relics.bed", 0.5, 3.0, 6.0, along_normal=False).xform((0, 0, 0), (-1.0, -2.8, 0.0))
    bed = bed.merge(blob(10.0, 5.0, 3.4, 8, 24).displace("relics.bed2", 0.5, 3.0, 6.0, along_normal=False).xform((0, 0, 0), (3.5, 1.5, 0.0)))
    sc.add(bed.xform(yaw, scale=1.15), EARTH, "bone.bed")
    sc.add(bones.xform(yaw, scale=1.15), OLD_BONE, "bone.all")
    # chain: along the front over the long bone, in under the ribs and out the far side over the spine
    ch = _chain([(-17.0, -3.0, 0.9), (-12, -6.5, 1.0), (-7, -8.0, 0.9), (-2, -7.5, 2.9), (3, -6.5, 1.3), (8, -5.0, 2.6), (11, -2.0, 1.5), (9.5, 1.0, 1.7), (5.5, 2.5, 1.6)], 3.15, "relics.chain")
    sc.add(ch.xform(yaw, scale=1.15), CHAIN_IRON, "bone.chain")
    return sc


def render_bones():
    return bone_scene().render(wear=0.3, grime=0.65, rust=0.4, dust=0.15, moss=0.0, exposure=1.0)


# rune potion ----------------------------------------------------------------------------------

PT_SIZE = (160, 224)
PT_PIVOT = (60, 190)
PT_SCALE = 1.15
PT_R = 5.0                                      # reference radius turning angle into lateral u for the strokes
SS_ = U.SS
PPU_ = U.PPU

GLASS = IsoMat((0.0030, 0.0055, 0.0070), (0.055, 0.060, 0.065), 0.30, wear=0.0, sat_cap=1.0)
STOPPER = IsoMat((0.013, 0.010, 0.008), (0.040, 0.036, 0.032), 0.80, (0.020, 0.018, 0.016), (0.10, 0.095, 0.09), 0.8, sat_cap=0.32,
                 detail=(Waves3("stopper.pit", 6, 0.4, 0.9, 0.012), Waves3("stopper.dent", 5, 2.5, 5.0, 0.03)))

# original strokes, (lateral u, height u) polylines around a centre; nothing taken from a real script
RUNES = (
    (11.0, 0.0, ((0, -1.5), (0, 1.5)), ((0, 1.5), (1.1, 0.5)), ((0, 0.4), (1.1, -0.6)), ((0, -0.6), (-0.9, -1.4))),
    (7.0, 0.2, ((-1.2, 0), (0, 1.5), (1.2, 0), (0, -1.5), (-1.2, 0)), ((-0.5, -0.5), (0.6, 0.7))),
    (3.3, -0.1, ((-1.3, 1.4), (0.3, 0.5), (-0.6, -0.3), (1.2, -1.4)), ((-1.3, -0.3), (-0.2, -0.3)), ((0.5, 1.3), (1.3, 1.3))),
)


def rot_matrix(rot_deg) -> np.ndarray:
    m = Mesh(np.array(((0, 0, 0), (1, 0, 0), (0, 1, 0), (0, 0, 1)), dtype=np.float64), np.array(((0, 1, 2),)))
    w = m.xform(rot_deg).v.astype(np.float64)
    return (w[1:] - w[0]).T


class RuneGlow:
    """Emission painted in the bottle's uv: a cyan liquid core that fades towards the silhouette and glowing strokes,
    both centred on the side that faces the camera."""

    def __init__(self, theta_front: float, core: float = 1.0, strokes: float = 1.0):
        self.tf, self.core, self.strokes = theta_front, core, strokes

    def __call__(self, uv):
        th = uv[:, 0] * math.tau
        z = uv[:, 1]
        d = (th - self.tf + math.pi) % math.tau - math.pi
        facing = np.clip(np.cos(d), 0, 1)
        liquid = smoothstep(0.8, 2.4, z) * (1 - smoothstep(11.6, 13.2, z))
        P = np.stack((np.cos(th) * 5.0, np.sin(th) * 5.0, z), -1)
        swirl = 0.5 + 0.5 * np.tanh(waves3(P, "relics.potion.swirl", 6, 3.0, 8.0, (1.0, 1.0, 0.6))[0] * 0.9)
        dens = 0.45 + 0.95 * swirl + 0.30 * (1 - smoothstep(0.0, 8.0, z))
        core = liquid * facing ** 4.2 * dens * 0.85 * self.core
        lat = d * PT_R
        stroke = np.zeros_like(z)
        for z0, s0, *lines in RUNES:
            for pl in lines:
                for (xa, za), (xb, zb) in zip(pl[:-1], pl[1:]):
                    ax, az, bx, bz = xa + s0, za + z0, xb + s0, zb + z0
                    dx, dz = bx - ax, bz - az
                    t = np.clip(((lat - ax) * dx + (z - az) * dz) / (dx * dx + dz * dz), 0, 1)
                    dist = np.hypot(lat - (ax + t * dx), z - (az + t * dz))
                    stroke = np.maximum(stroke, 1 - smoothstep(0.13, 0.28, dist))
        stroke = stroke * facing ** 0.4 * self.strokes
        rgb = core[:, None] * np.array((0.04, 0.90, 1.10)) + stroke[:, None] * np.array((0.55, 2.0, 2.2))
        return rgb


def _bottle():
    prof = [(0, 0.0), (3.4, 0.0), (4.8, 0.5), (5.7, 2.2), (6.0, 5.0), (5.8, 8.5), (4.8, 11.2), (3.1, 12.8), (2.0, 14.0), (1.8, 16.6),
            (2.4, 16.9), (2.5, 17.8), (1.9, 18.2), (1.3, 18.1)]
    glass = lathe(prof, sides=64)
    stop = lathe([(0.0, 17.9), (1.5, 17.9), (1.5, 19.1), (1.95, 19.5), (1.95, 21.0), (1.5, 21.5), (0.0, 21.6)], sides=28).displace("relics.stopper", 0.07, 2.5, 4.0)
    ring = lathe([(2.7, 15.5), (2.75, 16.3), (2.1, 16.3), (2.05, 15.5)], sides=40)                            # iron collar
    metal = stop.merge(ring)
    return glass, metal


def _frame_transform(tilt: float, height: float | None, foot: float = 3.0):
    """Rotation, scale and the move that puts the bottle where the frame wants it; height None means resting on the ground."""
    R = rot_matrix((0, tilt, 0))
    glass, metal = _bottle()
    pts = glass.v.astype(np.float64) @ R.T * PT_SCALE
    move = np.array((0.0, 0.0, height if height is not None else -pts[:, 2].min()))
    ax_r = np.array((math.sqrt(0.5), -math.sqrt(0.5), 0.0))
    ax_u = np.array((math.sqrt(0.5) * math.sin(math.radians(50)), math.sqrt(0.5) * math.sin(math.radians(50)), math.cos(math.radians(50))))
    # centre the bottle on the pivot column so the four frames line up when flipped
    move = move + ax_r * (-((pts + move) @ ax_r).mean())
    if height is None:                                                     # resting: lowest point of the bottle on the pivot row
        bot = (-((pts + move) @ ax_u) * 4.0).max()
        move = move + np.array((1.0, 1.0, 0.0)) * math.sqrt(0.5) * ((bot - foot) / (4.0 * math.sin(math.radians(50))))
    return R, glass, metal, move



def _gauss_dots(sc: Scene, pts, sig_px: float, amp):
    """Hi-res alpha from soft dots; pts are (x, y, strength) in final px."""
    a = np.zeros((sc.H, sc.W), dtype=F32)
    yy = (np.arange(sc.H) + 0.5)[:, None]
    xx = (np.arange(sc.W) + 0.5)[None, :]
    for x, y, k in pts:
        cx, cy = x * SS_, y * SS_
        r = int(sig_px * 3.2) + 2
        x0, x1, y0, y1 = int(max(cx - r, 0)), int(min(cx + r, sc.W)), int(max(cy - r, 0)), int(min(cy + r, sc.H))
        if x1 <= x0 or y1 <= y0:
            continue
        g = np.exp(-((xx[:, x0:x1] - cx) ** 2 + (yy[y0:y1] - cy) ** 2) / (2 * sig_px * sig_px)) * k * amp
        a[y0:y1, x0:x1] = np.maximum(a[y0:y1, x0:x1], g)
    return a


def potion_frame(i: int):
    tilts = (22.0, 50.0, 34.0, 16.0)
    heights = (40.0, 15.5, None, None)
    R, glass, metal, move = _frame_transform(tilts[i], heights[i], (0.0, 0.0, 1.0, 6.0)[i])
    sc = Scene("rune_potion_drop", PT_SIZE[0], PT_SIZE[1], PT_PIVOT)
    dv = R.T @ np.array((-math.sqrt(0.5) * math.cos(math.radians(50)), -math.sqrt(0.5) * math.cos(math.radians(50)), math.sin(math.radians(50))))
    glow = RuneGlow(math.atan2(dv[1], dv[0]))
    g = glass.xform((0, tilts[i], 0), move, PT_SCALE)
    m = metal.xform((0, tilts[i], 0), move, PT_SCALE)
    sc.add(g, GLASS, "potion.glass", emit=glow)
    sc.add(m, STOPPER, "potion.stopper")
    cen = R @ np.array((0.0, 0.0, 9.0)) * PT_SCALE + move                 # body centre, for the aura
    X, Y = sc.ground_uv()
    cx, cy = sc.project(cen)
    yy = (np.arange(sc.H) + 0.5)[:, None] / SS_
    xx = (np.arange(sc.W) + 0.5)[None, :] / SS_
    alpha = np.zeros((sc.H, sc.W), dtype=F32)
    if i < 2:                                                                      # soft aura while it falls
        d2 = ((xx - cx) ** 2 + ((yy - cy) / 1.1) ** 2) / (26.0 + 3.0 * i) ** 2
        alpha = np.maximum(alpha, (0.40 * np.exp(-d2 * 1.6)).astype(F32))
    sparks = []
    if i >= 2:
        rr, wd, peak = (7.4, 0.6, 0.85) if i == 2 else (10.0, 1.1, 0.30)
        dist = np.hypot(X, Y)
        ring = peak * np.exp(-(((dist - rr) / wd) ** 2))
        if i == 3:
            ring = ring * (0.55 + 0.45 * (0.5 + 0.5 * np.tanh(waves3(np.stack((X, Y, 0 * X), -1), "relics.potion.ring", 5, 3.0, 9.0)[0])))
        # the bottle hides the far half of the ring: occlusion from a depth pass over the bottle alone
        from isoforge import _raster, to_view
        allv = np.concatenate((g.v, m.v)).astype(np.float64)
        allf = np.concatenate((g.f, m.f + len(g.v)))
        vx, vy, vz = to_view(allv)
        sx, sy = PT_PIVOT[0] * SS_ + vx * PPU_, PT_PIVOT[1] * SS_ + vy * PPU_
        zb, tid, _ = _raster(sx, sy, vz, allf, sc.W, sc.H)
        ring_depth = X * (-0.643 * math.sqrt(0.5)) + Y * (-0.643 * math.sqrt(0.5))
        ring = np.where((tid >= 0) & (zb > ring_depth - 0.05), 0.0, ring)
        alpha = np.maximum(alpha, ring.astype(F32))
        r = U.rng(f"relics.potion.sparks{i}")
        n = 9 if i == 2 else 3
        for k in range(n):
            a = r.uniform(0, math.tau)
            rad = r.uniform(2.5, 8.0) if i == 2 else r.uniform(4.0, 8.0)
            hz = r.uniform(1.0, 6.5) if i == 2 else r.uniform(1.0, 3.0)
            px, py = sc.project((rad * math.cos(a), rad * math.sin(a), hz))
            sparks.append((px, py, 1.0 if i == 2 else 0.38))
            if i == 2:
                bx, by = sc.project((rad * 0.6 * math.cos(a), rad * 0.6 * math.sin(a), hz * 0.5))
                sparks.append((bx, by, 0.45))
        alpha = np.maximum(alpha, _gauss_dots(sc, sparks, 6.0, 1.0))
    col = np.array((0.04, 0.80, 1.0), dtype=F32) if i < 3 else np.array((0.03, 0.62, 0.85), dtype=F32)
    if i == 2 and sparks:
        col = np.array((0.06, 0.95, 1.0), dtype=F32)
    sc.fx(col, alpha)
    out = sc.render(wear=0.0, grime=0.25, rust=0.45, dust=0.0, moss=0.0, exposure=1.0)
    if i < 2:                                                                      # shadow stays on the ground at the pivot, tightening as it lands
        r_ = (8.0, 5.6)[i]
        a_ = (0.14, 0.34)[i]
        sh = a_ * np.exp(-((X ** 2 + Y ** 2) / (r_ * r_)) ** 1.5)
        af = sh.reshape(sc.h, SS_, sc.w, SS_).mean(axis=(1, 3))
        out.shadow[..., 3] = np.floor(np.clip(af, 0, 1) * 255 + 0.5).astype(np.uint8)
    return out


def render_potion_0():
    return potion_frame(0)


def render_potion_1():
    return potion_frame(1)


def render_potion_2():
    return potion_frame(2)


def render_potion_3():
    return potion_frame(3)


OBJECTS = {"tombstone": ObjectSpec(TS_SIZE, TS_PIVOT, (render_tomb,)), "bone_pile": ObjectSpec(BP_SIZE, BP_PIVOT, (render_bones,)),
           "rune_potion_drop": ObjectSpec(PT_SIZE, PT_PIVOT, (render_potion_0, render_potion_1, render_potion_2, render_potion_3), (90, 90, 110, 160))}
