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

TS_SIZE = (176, 176)
TS_PIVOT = (58, 140)
TS_W, TS_H, TS_SH, TS_T = 6.8, 34.0, 27.6, 5.0
TS_YAW = -52.0
PANEL_R, PANEL_Z0 = 4.9, 5.5

# cold grey granite; bare stone under chips is lighter and a touch warmer
TOMB_STONE = IsoMat((0.066, 0.057, 0.047), (0.040, 0.041, 0.043), 0.82, (0.080, 0.082, 0.085), (0.045, 0.046, 0.048), 0.7, sat_cap=0.28,
                    detail=(Waves3("tomb.chip", 6, 3.0, 6.0, 0.022), Waves3("tomb.grit", 8, 0.35, 0.9, 0.008)))
# moss: clumps of 2 u and up with no sub-unit detail, so no pixel speckle
TOMB_MOSS = IsoMat((0.017, 0.029, 0.011), (0.020, 0.020, 0.020), 0.95, sat_cap=0.35, detail=(Waves3("tomb.moss.mound", 6, 2.5, 5.0, 0.08),))


def _tomb_outline(z):
    """Left and right edge x at height z: a round-shouldered slab, chipped on both shoulders."""
    z = np.asarray(z, dtype=np.float64)
    k = np.clip((z - TS_SH) / (TS_H - TS_SH), 0, 1)
    half = TS_W * np.sqrt(np.maximum(1 - k * k, 0))
    half = half + 0.30 * (1 - smoothstep(0, 6, z))                  # the foot flares a hair
    left, right = -half, half
    left = left + np.interp(z, (28.4, 29.4, 30.2, 31.0, 32.0), (0, 1.5, 1.0, 1.7, 0))        # chipped left shoulder
    right = right - np.interp(z, (30.0, 30.8, 31.6, 32.6), (0, 1.2, 0.6, 0))                # chipped right shoulder
    right = right - np.interp(z, (4.0, 5.0, 6.2, 7.4), (0, 0.8, 0.4, 0))                    # a nibble at the foot
    return left, right


CRACK = [(5.0, 31.4), (4.4, 27.5), (4.7, 24.0), (3.6, 20.0), (3.9, 16.5), (2.4, 12.5), (2.8, 9.0), (1.2, 5.2), (1.6, 1.5), (0.4, 0.0)]
CRACK_BRANCH = [(3.9, 16.5), (5.4, 13.6)]
CHIPS = ((-5.6, 3.4, 1.7, 0.30), (5.6, 8.4, 1.5, 0.26), (-5.0, 12.0, 1.3, 0.22))


def _sd_box(X, Z, cx, cz, hx, hz):
    qx, qz = np.abs(X - cx) - hx, np.abs(Z - cz) - hz
    return np.hypot(np.maximum(qx, 0), np.maximum(qz, 0)) + np.minimum(np.maximum(qx, qz), 0)


def _face_relief(X, Z):
    """Recession of the face (u, positive goes into the stone): one big sunken arched panel with a carved cross in it,
    one crack and a few shallow chips. Everything else is flat."""
    y = np.zeros_like(X)
    arch = np.hypot(X, np.maximum(Z - TS_SH, 0)) - PANEL_R
    sd = np.maximum(np.where(Z > TS_SH, arch, np.abs(X) - PANEL_R), PANEL_Z0 - Z)
    y += 0.26 * smoothstep(0.25, -0.25, sd)
    cross = np.minimum(_sd_box(X, Z, 0, 20.0, 1.0, 9.0), _sd_box(X, Z, 0, 23.8, 3.5, 1.0))
    y += 0.70 * smoothstep(0.22, -0.22, cross)
    d, sd_ = seg_dist(X, Z, CRACK)
    w = 0.30 + 0.34 * smoothstep(0, 30, Z)
    y += 0.80 * np.clip(1 - d / w, 0, 1) ** 0.8
    y -= smoothstep(1.8, 0.0, d) * sd_ * 0.10 * smoothstep(1.5, 8.0, Z)      # one lip stands a hair proud
    d2, _ = seg_dist(X, Z, CRACK_BRANCH)
    y += 0.40 * np.clip(1 - d2 / 0.28, 0, 1)
    for cx, cz, r, dep in CHIPS:
        dd = np.hypot(X - cx, Z - cz) / r
        y += dep * np.clip(1 - dd * dd, 0, 1) ** 0.8
    return y


def _slab() -> Mesh:
    nz, nx = 190, 80
    zs = np.linspace(0, TS_H, nz)
    left, right = _tomb_outline(zs)
    ch = 0.55
    P = np.zeros((nz, nx + 2, 3))
    for i, z in enumerate(zs):
        xs = np.concatenate(([left[i]], left[i] + ch + (right[i] - left[i] - 2 * ch) * np.linspace(0, 1, nx), [right[i]]))
        P[i, :, 0] = xs
        P[i, :, 2] = z
    P[:, :, 1] = -TS_T / 2
    P[:, 0, 1] += ch
    P[:, -1, 1] += ch
    X, Z = P[..., 0], P[..., 2]
    interior = np.ones(X.shape)
    interior[:, 0] = interior[:, -1] = 0
    P[..., 1] += _face_relief(X, Z) * interior
    P[..., 1] += P[..., 2] * 0.040
    P[..., 0] += P[..., 2] * -0.030
    uv = np.stack((np.broadcast_to(np.linspace(0, 1, nx + 2)[None, :], (nz, nx + 2)), np.broadcast_to((zs / TS_H)[:, None], (nz, nx + 2))), -1)
    front = grid(P, uv, smooth_normals=True)
    back = P[:, [0, -1], :].copy()
    back[..., 1] = TS_T / 2 - ch + back[..., 2] * 0.040
    back[:, 0, 0] = P[:, 0, 0]
    back[:, 1, 0] = P[:, -1, 0]
    out = front
    for c in (0, 1):
        edge = P[:, 0 if c == 0 else -1, :]
        rear = back[:, c, :]
        out = out.merge(grid(np.stack((edge, rear), axis=1), None, smooth_normals=False))
    rows = np.linspace(0, nz - 1, 24).astype(int)
    bk = np.stack([np.linspace(back[r, 0], back[r, 1], 4) for r in rows])
    out = out.merge(grid(bk[:, ::-1], None, smooth_normals=False))
    out = out.displace("relics.tomb.warp", 0.05, 8.0, 14.0)
    g = 1.0 - 0.45 * smoothstep(27.0, 33.5, out.v[:, 2])          # the crown is grimed and rained on: darker than the face
    return Mesh(out.v, out.f, out.n, out.uv, np.stack((g, g, g * 0.98), -1))


def _face_pt(x, z, off=0.0):
    """Point on the face at (x, z) in slab space, with the lean applied; off moves it out of the stone."""
    return (x - 0.030 * z, -TS_T / 2 + 0.040 * z - off, z)


def _earth_mound() -> Mesh:
    """A low rim of disturbed earth where the slab is sunk, thin and irregular; the ground itself is not drawn."""
    nr, na = 6, 72
    ang = np.linspace(0, math.tau, na + 1)
    out_r = 9.5 + 1.6 * waves3(np.stack((np.cos(ang) * 4, np.sin(ang) * 4, 0 * ang), -1), "relics.tomb.mr", 5, 4.0, 8.0)[0]
    P = np.zeros((nr, na + 1, 3))
    for i in range(nr):
        f = i / (nr - 1)
        rr = 5.4 + (out_r - 5.4) * f
        hh = 1.9 * (1 - f) ** 1.6
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
    b = blob(rx, ry, rz, 8, 16).displace(name, 0.25, 2.5, 4.0, along_normal=False)
    return b.xform((0, 0, 0), at)


EARTH = IsoMat((0.024, 0.021, 0.018), (0.020, 0.020, 0.020), 0.95, sat_cap=0.22,
               detail=(Waves3("earth.clod", 6, 3.0, 6.0, 0.10),))
# moss sits in the recesses: the foot of the cross, the crack, the base and the chipped shoulder (x, z on the face; radii u)
MOSS_SPOTS = ((0.0, 10.6, 2.0, 0.45, 1.0), (3.0, 4.4, 2.4, 0.5, 1.2), (-4.6, 1.6, 2.8, 0.6, 1.3), (4.8, 1.2, 2.4, 0.5, 1.1),
              (-5.2, 29.2, 1.9, 0.45, 1.0), (4.4, 15.2, 1.5, 0.4, 1.3))
MOSS_CROWN = ((-3.2, 33.0, 2.3, 1.7, 0.9), (1.6, 33.7, 2.6, 1.8, 0.9))        # on the crown, where rain collects


def tomb_scene() -> Scene:
    sc = Scene("tombstone", TS_SIZE[0], TS_SIZE[1], TS_PIVOT)
    sc.add(_slab().xform((0, 0, TS_YAW)), TOMB_STONE, "tomb.slab")
    sc.add(_earth_mound().xform((0, 0, TS_YAW)), EARTH, "tomb.earth")
    sc.add(_stones("tomb.rub", ((-6.4, -4.4, 1.3), (6.6, -3.8, 1.0))).xform((0, 0, TS_YAW)), TOMB_STONE, "tomb.rub")
    mo = None
    for i, (x, z, rx, ry, rz) in enumerate(MOSS_SPOTS):
        m = _moss_clump(rx, ry, rz, f"relics.tomb.moss{i}", _face_pt(x, z, 0.1))
        mo = m if mo is None else mo.merge(m)
    for i, (x, z, rx, ry, rz) in enumerate(MOSS_CROWN):
        mo = mo.merge(_moss_clump(rx, ry, rz, f"relics.tomb.crown{i}", (x - 0.030 * z, 0.040 * z, z - 0.1)))
    sc.add(mo.xform((0, 0, TS_YAW)), TOMB_MOSS, "tomb.moss")
    return sc


def render_tomb():
    return tomb_scene().render(wear=0.15, grime=0.6, rust=0.0, dust=0.1, moss=0.0, exposure=1.0)


# bone pile ------------------------------------------------------------------------------------

BP_SIZE = (208, 144)
BP_PIVOT = (104, 92)
BP_YAW = -45.0

# old bone: dull, stained, never white. The default bone material is brighter than this set wants.
OLD_BONE = IsoMat((0.056, 0.054, 0.051), (0.030, 0.030, 0.028), 0.88, wear=0.25, sat_cap=0.22,
                  detail=(Waves3("obone.grain", 6, 0.5, 1.0, 0.010, (1.0, 1.0, 0.3)), Waves3("obone.dent", 6, 2.5, 5.0, 0.035)))
BONE_DUST = IsoMat((0.034, 0.030, 0.026), (0.020, 0.020, 0.020), 0.95, sat_cap=0.2, detail=(Waves3("bdust.clod", 6, 3.0, 6.0, 0.08),))
CHAIN_IRON = IsoMat((0.013, 0.011, 0.0095), (0.040, 0.036, 0.032), 0.78, (0.020, 0.019, 0.018), (0.11, 0.105, 0.10), 0.8, sat_cap=0.26,
                    detail=(Waves3("chain.pit", 6, 0.4, 0.9, 0.012), Waves3("chain.dent", 5, 2.5, 5.0, 0.03)))


def stain(m: Mesh, name: str) -> Mesh:
    """Per-vertex age stain: yellowed and browned in patches (a tint, so the albedo stays dull)."""
    n = waves3(m.v.astype(np.float64), name, 6, 3.0, 9.0)[0] * 0.5 + 0.5
    tint = np.stack((1.0 - 0.14 * n, 1.0 - 0.18 * n, 1.0 - 0.22 * n), -1)
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
    rows, cols = 48, 72
    lat = np.linspace(-math.pi / 2, math.pi / 2, rows)
    lon = np.linspace(0, math.tau, cols + 1)
    la, lo = np.meshgrid(lat, lon, indexing="ij")
    d = np.stack((np.cos(la) * np.cos(lo), np.cos(la) * np.sin(lo), np.sin(la)), -1)
    s = np.ones(la.shape)

    def ang_to(c):
        c = np.asarray(c, dtype=np.float64)
        c = c / np.linalg.norm(c)
        return np.arccos(np.clip(d @ c, -1, 1))

    def well(c, wid, amp, p=2):
        return amp * np.exp(-(ang_to(c) / wid) ** p)

    # sockets and the nasal opening are deep with a sharp rim (a flat-topped falloff, not a dimple)
    sock = well((0.40, -0.90, 0.02), 0.30, 0.80, 4) + well((-0.40, -0.90, 0.02), 0.30, 0.80, 4)
    nasal = well((0.0, -1.0, -0.34), 0.17, 0.70, 4)
    s -= sock + nasal
    s += well((0.80, -0.55, -0.28), 0.34, 0.10) + well((-0.80, -0.55, -0.28), 0.34, 0.10)  # cheekbones
    s += well((0.0, -0.96, 0.30), 0.30, 0.07)                                              # brow
    s += well((0.0, 0.7, -0.1), 0.50, 0.06)                                                # the back of the cranium bulges
    # sutures: shallow grooves along the sagittal and coronal lines, and one real crack across the crown
    top = smoothstep(0.05, 0.45, d[..., 2])
    sag = np.exp(-(d[..., 0] / 0.055) ** 2) * top * (d[..., 1] > -0.55)
    cor = np.exp(-((d[..., 1] + 0.18 + 0.20 * d[..., 0] ** 2) / 0.055) ** 2) * top
    cr = np.abs(d[..., 0] - 0.62 - 0.9 * (d[..., 1] + 0.1) + 0.12 * np.sin(14 * d[..., 1]))
    crack = np.exp(-(cr / 0.028) ** 2) * smoothstep(0.30, 0.55, d[..., 2]) * (d[..., 1] > -0.7) * (d[..., 1] < 0.5)
    s -= 0.04 * sag + 0.035 * cor + 0.07 * crack
    P = d * s[..., None] * np.array((3.5, 4.3, 3.7))
    front = np.clip(-d[..., 1], 0, 1)
    low = smoothstep(0.0, -0.75, d[..., 2])
    P[..., 0] *= 1 - 0.30 * low * front                                                    # the muzzle is narrower than the cranium
    P[..., 2] = np.maximum(P[..., 2], -2.55)                                               # flat underside
    out = grid(P, None, smooth_normals=True)
    # sockets and the nose are near black: a vertex tint so the cavities stay deep at 4 px per unit
    cav = np.clip((sock + nasal) / 0.55, 0, 1).reshape(-1)
    parts = [out]
    for i in range(10):                                                                    # upper teeth, some lost, some broken, long and square
        if i in (3, 7):
            continue
        x = (i - 4.5) * 0.48
        h = (0.95 - 0.12 * (i % 3)) * (0.55 if i == 8 else 1.0)
        t = box(0.40, 0.34, h, 0.05).xform((0, 0, -x * 18), (x, -3.05 - 0.18 * abs(x) / 2.2, -3.1 - 0.18 * h))
        parts.append(t)
    if jaw:
        jaw_m = tube([(-2.7, 0.2, -3.0), (-2.0, -2.4, -3.8), (0.0, -3.6, -4.15), (2.0, -2.4, -3.8), (2.7, 0.2, -3.0)], [0.9, 0.62, 0.55, 0.62, 0.9], sides=6, seg_u=1.0)
        for sg in (-1, 1):
            jaw_m = jaw_m.merge(tube([(sg * 2.7, 0.2, -3.0), (sg * 3.0, 0.9, -1.6), (sg * 2.9, 1.0, -0.4)], [0.9, 0.62, 0.5], sides=6, seg_u=1.0))
        for i in range(9):
            if i in (2, 6):
                continue
            x = (i - 4.0) * 0.52
            jaw_m = jaw_m.merge(box(0.38, 0.32, 0.70 - 0.10 * (i % 2), 0.05).xform((0, 0, -x * 16), (x, -3.25 - 0.25 * abs(x) / 2.2, -3.55)))
        parts.append(jaw_m.xform((6, 0, 0), (0, 0.5, -0.9)))
    m = parts[0]
    for q in parts[1:]:
        m = m.merge(q)
    m = m.displace(name, 0.05, 2.5, 5.0, along_normal=False)
    m = stain(m, name + ".s")
    # cavity tint on the skull surface only (the first vertices): sockets go near black
    t = np.array(m.tint, dtype=np.float64)
    k = 1.0 - 0.80 * cav
    t[: len(cav)] *= k[:, None]
    return Mesh(m.v, m.f, m.n, m.uv, t)


def _link(c, t, up, alt, wire=0.36, half_len=1.7, half_w=1.0):
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


def _rib(base, ydir, length, rise, curl, r, name, yaw=None, broken=False):
    """One rib. Lengths, arcs and angles all vary; a broken rib stops short with a blunt snapped end."""
    rr = U.rng(name)
    k = length * rr.uniform(0.80, 1.10) * (0.6 if broken else 1.0)
    rise = rise * rr.uniform(0.55, 1.2)
    curl = curl * rr.uniform(0.2, 1.6)
    path = [(0, 0, 0), (0.4 * curl, ydir * 0.28 * k, rise * 0.75), (1.0 * curl, ydir * 0.60 * k, rise),
            (1.8 * curl, ydir * 0.88 * k, rise * 0.55), (2.2 * curl, ydir * k, rise * 0.12)]
    tip = 0.95 if broken else 0.55
    m = tube(path, [r * 1.25, r, r * 0.95, r * 0.85, r * tip], sides=6, seg_u=1.4).displace(name, 0.06, 2.5, 5.0, along_normal=False)
    yw = rr.uniform(-22, 22) if yaw is None else yaw
    return stain(m.xform((rr.uniform(-16, 16), rr.uniform(-10, 10), yw), base), name + ".s")


def _shard(c, a, ln, r, name):
    """A short splintered bone fragment lying flat."""
    d = np.array((math.cos(a), math.sin(a), 0.12))
    p0 = np.asarray(c, dtype=np.float64)
    return stain(tube([p0, p0 + d * ln * 0.5, p0 + d * ln], [r * 0.9, r, r * 0.55], sides=5, seg_u=1.0).displace(name, 0.06, 2.5, 4.0, along_normal=False), name + ".s")


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
    # spine and a collapsed, uneven ribcage: ribs differ in length, arc and heading, one is missing, one is snapped,
    # a few point the other way where the cage has fallen in
    add(stain(tube([(-2.5, 3.0, 2.0), (1.5, 4.2, 2.4), (5.5, 5.0, 2.2), (9.5, 5.0, 1.6)], 0.7, sides=7, seg_u=1.2).displace("relics.spine", 0.1, 2.5, 5.0, along_normal=False), "relics.spine.s"))
    rib_x = (-1.5, 0.1, 2.9, 4.4, 5.6, 7.9, 9.4)
    rib_len = (9.0, 10.8, 7.5, 11.0, 6.2, 9.4, 7.0)
    for i, sx in enumerate(rib_x):
        if i == 2:
            continue                                                         # a gap in the cage
        sy = 3.3 + 0.15 * i
        yd = -1
        add(_rib((sx, sy, 2.0 + 0.05 * i), yd, rib_len[i], 3.4 + 0.5 * (i % 3), 0.8, 0.74, f"relics.rib{i}", yaw=(-20, 5, 28, -12, 18, -26, 8)[i], broken=i in (4, 6)))
    for i in range(6):                                                      # vertebrae along the spine
        add(stain(blob(1.0, 1.1, 0.9, 6, 10).displace(f"relics.vert{i}", 0.08, 2.5, 4.0, along_normal=False).xform((0, 0, 40 * i), (-2 + i * 2.2, 3.2 + 0.2 * i, 2.5)), f"relics.vs{i}"))
    # skulls: the whole one rests on the long bones at the left; a second, cracked one lies behind it, turned to show its face
    add(_skull("relics.skull0").xform((-8, 0, -25), (-7.5, -1.5, 5.2), 1.25))
    add(long_bone((-3.5, -3.0, 1.0), (-8.0, -0.5, 4.0), 0.8, 1.5, 0.5, "relics.femur2"))
    add(long_bone((-3.0, 2.5, 6.2), (10.0, -3.5, 6.0), 0.78, 1.5, 0.6, "relics.femur3"))
    add(long_bone((13.0, -3.0, 1.4), (8.0, -2.0, 5.0), 0.7, 1.3, -0.4, "relics.tibia2"))
    add(_skull("relics.skull1", jaw=False).xform((-18, 12, -65), (-13.0, 5.5, 3.6), 0.95))
    # small bones, finger and foot bones, and bone bits scattered at the rim of the pile
    r = U.rng("relics.small")
    for i in range(15):
        a = r.uniform(0, math.tau)
        c = (r.uniform(-8, 12), r.uniform(-6, 3), 0.7)
        add(long_bone((c[0], c[1], c[2]), (c[0] + 2.8 * math.cos(a), c[1] + 2.8 * math.sin(a), c[2] + 0.15), 0.42, 0.7, 0.15, f"relics.small{i}"))
    rs = U.rng("relics.shards")
    for i in range(10):
        ang = rs.uniform(0, math.tau)
        rad = rs.uniform(0.75, 1.05)
        c = (-1.0 + 15.5 * rad * math.cos(ang), -1.2 + 8.0 * rad * math.sin(ang), 0.45)
        add(_shard(c, rs.uniform(0, math.tau), rs.uniform(1.4, 2.8), rs.uniform(0.5, 0.65), f"relics.shard{i}"))
    bed = blob(10.5, 5.2, 1.5, 8, 28).displace("relics.bed", 0.4, 3.0, 6.0, along_normal=False).xform((0, 0, 0), (-0.5, -1.8, -0.2))
    bed = bed.merge(blob(8.0, 4.0, 2.6, 8, 24).displace("relics.bed2", 0.4, 3.0, 6.0, along_normal=False).xform((0, 0, 0), (4.0, 2.0, -0.2)))
    sc.add(bed.xform(yaw, scale=1.15), BONE_DUST, "bone.bed")
    sc.add(bones.xform(yaw, scale=1.15), OLD_BONE, "bone.all")
    # chain: along the front over the long bone, in under the ribs and out the far side over the spine
    ch = _chain([(-14.5, -4.5, 0.9), (-10.5, -6.6, 1.0), (-6, -7.8, 0.9), (-1.5, -7.2, 2.7), (2.5, -6.3, 1.5)], 3.1, "relics.chain")
    sc.add(ch.xform(yaw, scale=1.15), CHAIN_IRON, "bone.chain")
    return sc


def render_bones():
    return bone_scene().render(wear=0.3, grime=0.65, rust=0.22, dust=0.15, moss=0.0, exposure=1.0)


# rune potion ----------------------------------------------------------------------------------

PT_SIZE = (160, 240)
PT_PIVOT = (60, 190)
PT_SCALE = 1.15
PT_R = 5.0                                      # reference radius turning angle into lateral u for the strokes
SS_ = U.SS
PPU_ = U.PPU

# dark smoky glass: almost no diffuse of its own, so the shoulders and edges go near black and the light lives in the core
GLASS = IsoMat((0.0016, 0.0030, 0.0040), (0.017, 0.019, 0.021), 0.22, wear=0.0, sat_cap=1.0)
# grimy cork: dark, dry and pitted, with the same warm albedo logic as the rest of the set
CORK = IsoMat((0.070, 0.046, 0.027), (0.020, 0.019, 0.018), 0.92, (0.100, 0.070, 0.042), (0.030, 0.028, 0.026), 0.8, sat_cap=0.40,
              detail=(Waves3("cork.pit", 6, 0.4, 0.9, 0.014), Waves3("cork.dent", 6, 2.5, 4.5, 0.05)))
# worn sealing wax over the neck, dark oxblood
WAX = IsoMat((0.030, 0.0085, 0.0060), (0.035, 0.032, 0.030), 0.66, (0.050, 0.020, 0.014), (0.05, 0.045, 0.04), 0.8, sat_cap=0.55,
             detail=(Waves3("wax.dent", 5, 2.5, 4.5, 0.04),))

# original strokes, (height u, lateral u, polylines in (lateral, height) around the centre); nothing taken from a real script.
# Hand-cut: no two glyphs share a stroke width, and the vertices are jittered per build so no line is ruler straight.
RUNES = (
    (8.7, 0.5, ((0, -1.8), (0.2, 1.8)), ((0.2, 1.8), (1.4, 0.6)), ((0.1, 0.5), (1.3, -0.7)), ((0.0, -0.8), (-1.1, -1.7))),
    (4.6, 0.3, ((-1.5, 1.6), (0.4, 0.6), (-0.7, -0.4), (1.4, -1.6)), ((-1.5, -0.4), (-0.2, -0.5)), ((0.6, 1.5), (1.6, 1.4))),
)


def rot_matrix(rot_deg) -> np.ndarray:
    m = Mesh(np.array(((0, 0, 0), (1, 0, 0), (0, 1, 0), (0, 0, 1)), dtype=np.float64), np.array(((0, 1, 2),)))
    w = m.xform(rot_deg).v.astype(np.float64)
    return (w[1:] - w[0]).T


def _rune_segments():
    """Jittered stroke segments (lateral a, height a, lateral b, height b, half width, brightness)."""
    r = U.rng("relics.potion.runes")
    segs = []
    for z0, s0, *lines in RUNES:
        for pl in lines:
            pts = [(1.15 * x + s0 + r.uniform(-0.18, 0.18), 1.15 * z + z0 + r.uniform(-0.18, 0.18)) for x, z in pl]
            for (xa, za), (xb, zb) in zip(pts[:-1], pts[1:]):
                segs.append((xa, za, xb, zb, r.uniform(0.20, 0.32), r.uniform(0.6, 1.0)))
    return segs


class RuneGlow:
    """Emission painted in the bottle's uv: an uneven, swirling cyan core held inside dark glass (veins and sediment break
    it up, the rim goes black) and etched strokes that glow brighter than the core, each in its own width and strength,
    both centred on the side that faces the camera."""

    def __init__(self, theta_front: float, core: float = 1.0, strokes: float = 1.0):
        self.tf, self.core, self.strokes = theta_front, core, strokes
        self.segs = _rune_segments()

    def __call__(self, uv):
        th = uv[:, 0] * math.tau
        z = uv[:, 1]
        d = (th - self.tf + math.pi) % math.tau - math.pi
        facing = np.clip(np.cos(d), 0, 1)
        P = np.stack((np.cos(th) * 5.0, np.sin(th) * 5.0, z), -1)
        liquid = smoothstep(0.9, 2.6, z) * (1 - smoothstep(10.2, 11.6, z))
        v1 = waves3(P, "relics.potion.swirl", 6, 3.0, 6.0, (1.0, 1.0, 0.6))[0]
        v2 = waves3(P, "relics.potion.vein", 6, 2.5, 5.0, (1.0, 1.0, 0.5))[0]
        u1 = 0.5 + 0.5 * np.tanh(v1 * 1.4)
        shape = smoothstep(0.22, 0.80, u1 * 0.8 + facing * 0.3)                       # the core is a drifting cloud, not a disc
        vein = np.exp(-(v2 / 0.11) ** 2)                                              # dark veins through it
        sediment = 0.30 + 0.70 * smoothstep(0.9, 3.8, z) * (0.6 + 0.4 * u1)             # silt settled at the bottom
        core = liquid * smoothstep(0.30, 0.85, facing) * facing ** 1.4 * shape * (1 - 0.88 * vein) * sediment * self.core
        lat = d * PT_R
        stroke = np.zeros_like(z)
        halo = np.zeros_like(z)
        for xa, za, xb, zb, w, b in self.segs:
            dx, dz = xb - xa, zb - za
            t = np.clip(((lat - xa) * dx + (z - za) * dz) / (dx * dx + dz * dz), 0, 1)
            dist = np.hypot(lat - (xa + t * dx), z - (za + t * dz))
            ww = w * (0.55 + 0.45 * np.sin(math.pi * t) ** 0.5)                         # strokes thin at their ends
            stroke = np.maximum(stroke, (1 - smoothstep(ww * 0.55, ww, dist)) * b)
            halo = np.maximum(halo, 1 - smoothstep(ww, ww + 0.45, dist))
        worn = smoothstep(-0.45, -0.05, waves3(P, "relics.potion.gap", 5, 0.6, 1.0)[0])    # the cut is broken here and there
        stroke = stroke * (0.35 + 0.65 * worn) * facing ** 0.4 * self.strokes
        groove = np.clip(halo - stroke, 0, 1) * facing                                  # the cut edge swallows the light around it
        core = core * (1 - 0.8 * groove)
        rgb = core[:, None] * np.array((0.015, 0.40, 0.52)) + stroke[:, None] * np.array((0.28, 2.1, 2.5)) + (halo * facing * 0.05)[:, None] * np.array((0.0, 0.7, 1.0))
        return rgb


def _bottle():
    prof = [(0, 0.0), (3.4, 0.0), (4.8, 0.5), (5.7, 2.2), (6.0, 5.0), (5.8, 8.5), (4.8, 11.2), (3.1, 12.8), (2.0, 14.0), (1.8, 16.6),
            (2.4, 16.9), (2.5, 17.8), (1.9, 18.2), (1.3, 18.1)]
    glass = lathe(prof, sides=64)
    stop = lathe([(0.0, 17.9), (1.5, 17.9), (1.55, 19.1), (2.0, 19.5), (2.05, 21.0), (1.6, 21.6), (0.0, 21.7)], sides=28).displace("relics.stopper", 0.12, 2.5, 4.0)
    wax = lathe([(1.85, 15.2), (2.75, 15.6), (2.85, 16.6), (2.3, 17.5), (1.8, 17.4)], sides=40).displace("relics.wax", 0.14, 2.5, 4.0)
    return glass, stop, wax


def _frame_transform(tilt: float, height: float | None, foot: float = 3.0):
    """Rotation, scale and the move that puts the bottle where the frame wants it; height None means resting on the ground."""
    R = rot_matrix((0, tilt, 0))
    glass, stop, wax = _bottle()
    pts = glass.v.astype(np.float64) @ R.T * PT_SCALE
    move = np.array((0.0, 0.0, height if height is not None else -pts[:, 2].min()))
    ax_r = np.array((math.sqrt(0.5), -math.sqrt(0.5), 0.0))
    ax_u = np.array((math.sqrt(0.5) * math.sin(math.radians(50)), math.sqrt(0.5) * math.sin(math.radians(50)), math.cos(math.radians(50))))
    # centre the bottle on the pivot column so the four frames line up when flipped
    move = move + ax_r * (-((pts + move) @ ax_r).mean())
    if height is None:                                                     # resting: lowest point of the bottle on the pivot row
        bot = (-((pts + move) @ ax_u) * 4.0).max()
        move = move + np.array((1.0, 1.0, 0.0)) * math.sqrt(0.5) * ((bot - foot) / (4.0 * math.sin(math.radians(50))))
    return R, glass, stop, wax, move


def _streaks(sc: Scene, segs, sig_px: float):
    """Hi-res alpha from short streaks; segs are (x0, y0, x1, y1, strength) in final px, bright at the head (x1, y1)."""
    a = np.zeros((sc.H, sc.W), dtype=F32)
    yy = (np.arange(sc.H) + 0.5)[:, None]
    xx = (np.arange(sc.W) + 0.5)[None, :]
    for x0, y0, x1, y1, k in segs:
        ax, ay, bx, by = x0 * SS_, y0 * SS_, x1 * SS_, y1 * SS_
        r = int(sig_px * 3.2) + 2
        xa, xb, ya, yb = int(max(min(ax, bx) - r, 0)), int(min(max(ax, bx) + r, sc.W)), int(max(min(ay, by) - r, 0)), int(min(max(ay, by) + r, sc.H))
        if xb <= xa or yb <= ya:
            continue
        gx, gy = xx[:, xa:xb], yy[ya:yb]
        dx, dy = bx - ax, by - ay
        t = np.clip(((gx - ax) * dx + (gy - ay) * dy) / (dx * dx + dy * dy), 0, 1)
        dist2 = (gx - (ax + t * dx)) ** 2 + (gy - (ay + t * dy)) ** 2
        g = np.exp(-dist2 / (2 * sig_px * sig_px)) * k * (t ** 1.5)
        a[ya:yb, xa:xb] = np.maximum(a[ya:yb, xa:xb], g)
    return a


def _under(rgba: np.ndarray, alpha: np.ndarray, col) -> np.ndarray:
    """Composite a soft glow of colour col and straight alpha (final-res, 0..1) behind a straight-alpha uint8 sprite."""
    af = rgba[..., 3:4].astype(np.float64) / 255
    lin = U.srgb_to_linear(rgba[..., :3].astype(np.float64) / 255)
    aa = alpha[..., None].astype(np.float64) * (1 - af)
    ao = af + aa
    lo = (lin * af + np.asarray(col, dtype=np.float64) * aa) / np.maximum(ao, 1e-6)
    out = np.dstack((U.linear_to_srgb(np.clip(lo, 0, 1)) * 255 + 0.5, ao * 255 + 0.5))
    out = np.clip(np.floor(out), 0, 255).astype(np.uint8)
    out[out[..., 3] == 0, :3] = 0
    return out


def potion_frame(i: int):
    tilts = (22.0, 50.0, 34.0, 16.0)
    heights = (40.0, 15.5, None, None)
    R, glass, stop, wax, move = _frame_transform(tilts[i], heights[i], (0.0, 0.0, 1.0, 6.0)[i])
    sc = Scene("rune_potion_drop", PT_SIZE[0], PT_SIZE[1], PT_PIVOT)
    dv = R.T @ np.array((-math.sqrt(0.5) * math.cos(math.radians(50)), -math.sqrt(0.5) * math.cos(math.radians(50)), math.sin(math.radians(50))))
    glow = RuneGlow(math.atan2(dv[1], dv[0]))
    g = glass.xform((0, tilts[i], 0), move, PT_SCALE)
    st = stop.xform((0, tilts[i], 0), move, PT_SCALE)
    wx = wax.xform((0, tilts[i], 0), move, PT_SCALE)
    sc.add(g, GLASS, "potion.glass", emit=glow)
    sc.add(st, CORK, "potion.cork")
    sc.add(wx, WAX, "potion.wax")
    cen = R @ np.array((0.0, 0.0, 9.0)) * PT_SCALE + move                 # body centre, for the aura
    X, Y = sc.ground_uv()
    cx, cy = sc.project(cen)
    alpha = np.zeros((sc.H, sc.W), dtype=F32)
    if i >= 2:
        rr, wd, peak = (6.8, 0.50, 0.62) if i == 2 else (9.4, 0.75, 0.28)
        dist = np.hypot(X, Y)
        ang = np.arctan2(Y, X)
        pa = np.stack((np.cos(ang) * 3.0, np.sin(ang) * 3.0, 0 * ang), -1)
        n_r = waves3(pa, f"relics.potion.ringr{i}", 5, 2.5, 6.0)[0]
        n_w = waves3(pa, f"relics.potion.ringw{i}", 5, 2.5, 6.0)[0]
        n_b = waves3(pa, f"relics.potion.ringb{i}", 5, 2.5, 6.0)[0]
        rr_eff = rr + 0.55 * n_r
        wd_eff = wd * (0.65 + 0.55 * (0.5 + 0.5 * np.tanh(n_w * 1.5)))
        gap = smoothstep(-0.45 if i == 2 else -0.15, 0.30, n_b)                      # the ring is broken: dim and missing stretches
        side = np.where(dist > rr_eff, 1.0, 0.55)                                       # sharp on the inside, trailing out
        ring = peak * (0.12 + 0.88 * gap) * np.exp(-(((dist - rr_eff) / (wd_eff / side)) ** 2))
        if i == 2:
            ring = np.maximum(ring, 0.10 * np.exp(-(dist / (rr * 0.9)) ** 4))          # a faint scuff of light where it landed
        # the bottle hides the far half of the ring: occlusion from a depth pass over the bottle alone
        from isoforge import _raster, to_view
        allv = np.concatenate((g.v, st.v, wx.v)).astype(np.float64)
        allf = np.concatenate((g.f, st.f + len(g.v), wx.f + len(g.v) + len(st.v)))
        vx, vy, vz = to_view(allv)
        sx, sy = PT_PIVOT[0] * SS_ + vx * PPU_, PT_PIVOT[1] * SS_ + vy * PPU_
        zb, tid, _ = _raster(sx, sy, vz, allf, sc.W, sc.H)
        ring_depth = X * (-0.643 * math.sqrt(0.5)) + Y * (-0.643 * math.sqrt(0.5))
        ring = np.where((tid >= 0) & (zb > ring_depth - 0.05), 0.0, ring)
        alpha = np.maximum(alpha, ring.astype(F32))
        # sparks: short streaks thrown up and out of the impact, no two alike, hidden behind the bottle
        r = U.rng(f"relics.potion.sparks{i}")
        n = 10 if i == 2 else 3
        segs = []
        for k in range(n):
            a = r.uniform(0, math.tau)
            rad = r.uniform(5.0, 10.0) if i == 2 else r.uniform(6.0, 10.0)
            h0 = r.uniform(0.5, 2.5)
            ln = r.uniform(2.5, 7.0) if i == 2 else r.uniform(1.4, 2.6)
            up = r.uniform(0.7, 2.2)
            p0 = np.array((rad * math.cos(a), rad * math.sin(a), h0))
            p1 = p0 + np.array((0.35 * ln * math.cos(a), 0.35 * ln * math.sin(a), ln * up * 0.5))
            q0, q1 = sc.project(p0), sc.project(p1)
            vzs = to_view(p1[None, :])[2][0]
            # hide where the bottle stands in front of the spark's head
            segs.append((q0[0], q0[1], q1[0], q1[1], (r.uniform(0.6, 1.0) if i == 2 else r.uniform(0.25, 0.4)), vzs))
        spk = np.zeros((sc.H, sc.W), dtype=F32)
        for sg in segs:
            one = _streaks(sc, [sg[:5]], 3.0)
            one = np.where((tid >= 0) & (zb > sg[5] - 0.05), 0.0, one)
            spk = np.maximum(spk, one)
        alpha = np.maximum(alpha, spk)
    col = np.array((0.05, 0.78, 1.0), dtype=F32) if i == 2 else np.array((0.03, 0.62, 0.85), dtype=F32)
    sc.fx(col, alpha)
    out = sc.render(wear=0.0, grime=0.25, rust=0.0, dust=0.0, moss=0.0, exposure=1.0)
    if i < 2:                                                                      # soft aura behind the falling bottle, so the glass reads the same in every frame
        yy = (np.arange(sc.h) + 0.5)[:, None]
        xx = (np.arange(sc.w) + 0.5)[None, :]
        d2 = ((xx - cx) ** 2 + ((yy - cy) / 1.1) ** 2) / (24.0 + 3.0 * i) ** 2
        aura = np.clip(0.40 * np.exp(-d2 * 1.6) - 0.07, 0, 1).astype(F32)
        out.rgba = U.bleed_rgb(_under(out.rgba, aura, (0.04, 0.80, 1.0)))
        r_ = (8.0, 5.6)[i]                                                         # shadow stays on the ground at the pivot, tightening as it lands
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
