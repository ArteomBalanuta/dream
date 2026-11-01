"""Isometric nature: a gnarled dead oak, a thorn bramble with crimson leaves, a cluster of slate shards.

Form bands (u): primary 8 and up (trunk, crown, canes, clumps, shards), secondary 2..6 (bark ridges, limbs,
leaf groups, cleavage steps), tertiary under 1 (grain, grit, thorn tips). The 1..2 u gap stays empty.
"""
from __future__ import annotations

import math

import numpy as np

import uiart as U
from isoforge import IsoMat, Mesh, ObjectSpec, Scene, Waves3, _resample, grid

TAU = math.tau


def _unit(v):
    v = np.asarray(v, dtype=np.float64)
    return v / max(np.linalg.norm(v), 1e-9)


def _perp(t, r):
    """A unit vector perpendicular to t, picked deterministically from the generator."""
    a = r.normal(size=3)
    a -= t * (a @ t)
    return _unit(a)


def _rot(v, axis, ang):
    axis = _unit(axis)
    return v * math.cos(ang) + np.cross(axis, v) * math.sin(ang) + axis * (axis @ v) * (1 - math.cos(ang))


# ---------------------------------------------------------------------------------------------------
# dead oak
# ---------------------------------------------------------------------------------------------------

OAK_SIZE = (320, 400)
OAK_PIVOT = (150, 372)
SC = 1.15

BARK = IsoMat((0.036, 0.027, 0.020), (0.018, 0.017, 0.016), 0.88, wear=0.3, sat_cap=0.30,
              detail=(Waves3("oak.crack", 8, 2.4, 5.0, 0.06), Waves3("oak.grit", 6, 0.4, 0.9, 0.018), Waves3("oak.roll", 6, 5.0, 8.0, 0.14)))
DEADWOOD = IsoMat((0.055, 0.046, 0.036), (0.034, 0.032, 0.030), 0.84, wear=0.3, sat_cap=0.28,
                  detail=(Waves3("oak.grain", 7, 0.4, 0.9, 0.016, (1.0, 1.0, 0.2)), Waves3("oak.split", 6, 2.5, 5.0, 0.07)))


def _frames(q):
    t = np.gradient(q, axis=0)
    t /= np.maximum(np.linalg.norm(t, axis=1, keepdims=True), 1e-12)
    ref = np.array((0.0, 0.0, 1.0)) if abs(t[0, 2]) < 0.9 else np.array((1.0, 0.0, 0.0))
    nrm = np.cross(t[0], ref)
    nrm /= np.linalg.norm(nrm)
    Ns, Bs = [], []
    for k in range(len(q)):
        if k:
            b = np.cross(t[k - 1], t[k])
            sb = np.linalg.norm(b)
            if sb > 1e-9:
                ang = math.asin(min(sb, 1.0))
                nrm = _rot(nrm, b, ang)
            nrm = nrm - t[k] * (nrm @ t[k])
            nrm /= np.linalg.norm(nrm)
        Ns.append(nrm.copy())
        Bs.append(np.cross(t[k], nrm))
    return t, np.array(Ns), np.array(Bs)


def limb(path, r0, r1, name, ridges=0, amp=0.2, burl=0.0, flare=0.0, flare_len=6.0, twist=0.035, step=1.6, cut=0.0):
    """Ridged, twisting tube along a path. Ridges are V grooves with rounded crests, wandering with length.
    cut > 0 leaves the end blunt (a broken limb): the end radius stays r1 instead of tapering to a point."""
    r = U.rng("iso.oak." + name)
    q = _resample(np.asarray(path, dtype=np.float64), step)
    K = len(q)
    t, Nn, Bb = _frames(q)
    s = np.concatenate(([0.0], np.cumsum(np.linalg.norm(np.diff(q, axis=0), axis=1))))
    tt = s / max(s[-1], 1e-9)
    base = r0 + (r1 - r0) * tt ** 0.85
    if not cut:
        e = np.clip((tt - 0.78) / 0.22, 0, 1)
        base = base * (1 - 0.8 * e * e * (3 - 2 * e))
    if flare:
        base = base + flare * np.exp(-s / flare_len)
    ph = r.random(4) * TAU
    base = base * (1 + burl * (np.sin(s / 6.0 + ph[0]) * 0.6 + np.sin(s / 10.5 + ph[1]) * 0.4))
    sides = max(5, 7 * ridges) if ridges else 6
    th = np.linspace(0, TAU, sides + 1)
    if ridges:
        wob = 1.0 * np.sin(s[:, None] * 0.29 + 2 * th[None, :] + ph[2]) + 0.6 * np.sin(s[:, None] * 0.61 - th[None, :] + ph[3])
        tw = twist * s + 0.012 * s * np.sin(s * 0.05 + ph[0])
        x = ridges * (th[None, :] + tw[:, None]) + wob
        crest = np.abs(np.cos(x / 2)) ** 0.55
        fac = 1 - amp * (1 - crest)
        tint = 0.55 + 0.45 * crest
    else:
        fac = 1 + 0.07 * np.sin(s[:, None] * 0.9 + 3 * th[None, :] + ph[2])
        tint = np.ones_like(fac)
    rad = base[:, None] * fac
    off = np.cos(th)[None, :, None] * Nn[:, None, :] + np.sin(th)[None, :, None] * Bb[:, None, :]
    P = q[:, None, :] + off * rad[:, :, None]
    uv = np.stack((np.broadcast_to(th[None, :] / TAU, (K, sides + 1)), np.broadcast_to(s[:, None], (K, sides + 1))), -1)
    m = grid(P, uv)
    m.tint = np.repeat(tint.reshape(-1, 1), 3, axis=1).astype(np.float32)
    # end caps: flat, closing the tube
    for end, sg in ((0, -1.0), (K - 1, 1.0)):
        if end == 0 or cut > 0 or r1 > 0.4:
            ring = P[end, :sides]
            cv = np.vstack((ring, q[end][None, :] + t[end] * sg * 0.0))
            ii = np.arange(sides)
            cf = np.stack((ii, (ii + 1) % sides, np.full(sides, sides)), axis=1)
            cen = q[end] - t[end] * sg
            fnv = np.cross(cv[cf[:, 1]] - cv[cf[:, 0]], cv[cf[:, 2]] - cv[cf[:, 0]])
            bad = (fnv * (cv[cf].mean(1) - cen)).sum(1) < 0
            cf[bad] = cf[bad][:, ::-1]
            cap = Mesh(cv, cf, np.broadcast_to(t[end] * sg, cv.shape), np.zeros((len(cv), 2)))
            cap.tint = np.full((len(cv), 3), 0.8, dtype=np.float32)
            m = m.merge(cap)
    return m


def _splinters(tip, direction, r_end, name, count):
    """Jagged spikes of splintered wood rising from a broken end."""
    r = U.rng("iso.oak.spl." + name)
    out = None
    d0 = _unit(direction)
    for i in range(count):
        a = TAU * (i + r.random() * 0.6) / count
        side = _perp(d0, r)
        side = _rot(side, d0, a)
        ln = (2.0 + 3.0 * r.random())
        lean = 0.35 + 0.5 * r.random()
        dirv = _unit(d0 + side * lean)
        base = tip + side * r_end * (0.2 + 0.55 * r.random()) - d0 * 0.4
        pts = np.array((base, base + dirv * ln * 0.5, base + dirv * ln + side * 0.3))
        rr = max(0.5, r_end * 0.30) * (0.6 + 0.6 * r.random())
        m = limb(pts, rr, 0.06, f"{name}.{i}", step=1.2)
        out = m if out is None else out.merge(m)
    return out


class _Tree:
    def __init__(self):
        self.bark = None
        self.dead = None
        self.n = 0

    def add(self, m, wood=False):
        if wood:
            self.dead = m if self.dead is None else self.dead.merge(m)
        else:
            self.bark = m if self.bark is None else self.bark.merge(m)


def _grow(tree, r, start, direction, length, rad, order, tag, droop, maxorder=4):
    """One limb: a wandering path, a ridged tube, and child limbs spawned along it."""
    d = _unit(direction)
    start = np.asarray(start, dtype=np.float64) - d * 1.5 * rad ** 0.5
    n = max(3, int(length / 7.0) + 2)
    pts = [np.array(start, dtype=np.float64)]
    step = length / (n - 1)
    bend = _perp(d, r)
    gn = 0.30 + 0.10 * order
    for i in range(1, n):
        d = _unit(d + bend * gn * (0.6 + 0.8 * r.random()) + np.array((0, 0, -droop * step / 20.0 + 0.04 * order)))
        bend = _rot(bend, d, 0.9 + 0.8 * r.random())
        bend = _unit(bend - d * (bend @ d))
        pts.append(pts[-1] + d * step)
    pts = np.array(pts)
    broken = order >= 1 and r.random() < (0.50 if order < 3 else 0.12)
    r_end = rad * (0.45 if broken else 0.30)
    ridges = 0 if rad < 1.9 else int(max(5, round(rad * TAU / 3.4)))
    name = f"{tag}"
    m = limb(pts, rad, max(r_end, 0.15), name, ridges=ridges, amp=0.30 if rad > 3 else 0.2, burl=0.16 if order < 2 else 0.06,
             twist=0.03 * (1 if r.random() < 0.5 else -1), step=1.7 if rad > 1.5 else 2.0, cut=1.0 if broken else 0.0)
    tree.add(m)
    tree.n += 1
    if broken:
        tdir = pts[-1] - pts[-2]
        sp = _splinters(pts[-1], tdir, r_end, name, 3 if rad > 1.5 else 2)
        tree.add(sp, wood=True)
        cap = limb(np.array((pts[-1] - _unit(tdir) * 0.6, pts[-1] + _unit(tdir) * 0.5)), r_end * 0.95, r_end * 0.9, name + ".cap", step=1.0)
        tree.add(cap, wood=True)
    if order >= maxorder:
        return
    kids = {1: 3, 2: 2, 3: 2}.get(order, 0) + (1 if rad > 3 else 0)
    if broken:
        kids = max(0, kids - 1)
    fr = np.sort(0.28 + 0.62 * r.random(kids)) if kids else []
    ang0 = r.random() * TAU
    for j, f in enumerate(fr):
        k = min(int(f * (n - 1)), n - 2)
        pos = pts[k] + (pts[k + 1] - pts[k]) * (f * (n - 1) - k)
        t = _unit(pts[k + 1] - pts[k])
        side = _perp(t, r)
        side = _rot(side, t, ang0 + j * 2.4)
        spread = math.radians(32 + 38 * r.random())
        cd = _unit(t * math.cos(spread) + side * math.sin(spread) + np.array((0, 0, 0.12)))
        crad = rad * (0.50 - 0.05 * order) * (0.7 + 0.5 * r.random()) * (1 - 0.35 * f)
        clen = length * (0.62 + 0.3 * r.random()) * (1 - 0.35 * f)
        if crad < 0.28 or clen < 5:
            continue
        _grow(tree, r, pos - t * 0.0, cd, clen, crad, order + 1, f"{tag}.{j}", droop + 0.6, maxorder)


def oak_meshes():
    r = U.rng("iso.oak.skeleton")
    tree = _Tree()
    # trunk: a heavy twisting column, flared at the collar
    tp = np.array(((0, 0, -3), (1.6, 0.9, 14), (-0.9, 2.8, 32), (-3.2, 0.7, 50), (-2.0, -2.2, 66), (0.3, -2.8, 78)), dtype=np.float64)
    tree.add(limb(tp, 7.8, 4.4, "trunk", ridges=12, amp=0.24, burl=0.09, flare=5.0, flare_len=5.5, twist=0.032, step=1.5, cut=1.0))
    top = tp[-1]
    tree.add(_splinters(top, (0.1, -0.1, 1.0), 4.0, "trunktop", 5), wood=True)
    tree.add(limb(np.array((top - (0, 0, 0.5), top + (0, 0, 0.6))), 4.5, 4.4, "trunkcap", step=1.0), wood=True)
    mains = (
        (tp[3], (1.0, 0.3, 0.7), 32, 4.2),
        (tp[4], (-0.8, -0.4, 0.8), 30, 3.9),
        (tp[2] + (0, 0, 2), (-0.4, 1.0, 0.5), 22, 3.8),
        (tp[4] + (0, 0, 2), (0.6, -0.8, 1.0), 28, 3.4),
        (tp[1] + (0, 0, 3), (0.3, -1.0, 0.3), 22, 3.8),
    )
    for i, (st, dr, ln, rd) in enumerate(mains):
        _grow(tree, r, st, dr, ln, rd, 1, f"m{i}", droop=0.5)
    # root collar: buttress roots arching into the ground
    for i in range(7):
        a = TAU * (i + 0.3 * r.random()) / 7
        c, s = math.cos(a), math.sin(a)
        reach = 10 + 4 * r.random()
        pts = np.array(((c * 3.0, s * 3.0, 11), (c * 8.0, s * 8.0, 5.5), (c * reach * 0.7, s * reach * 0.7, 1.6), (c * reach, s * reach, -0.6)))
        rp = np.cross((c, s, 0), (0, 0, 1)) * (r.random() - 0.5) * 6
        pts[1:, :] += rp[None, :] * np.array((0.5, 1.0, 1.0))[:, None]
        tree.add(limb(pts, 3.4, 0.7, f"root{i}", ridges=7, amp=0.2, burl=0.06, twist=0.02, step=1.6))
    return tree


def oak_scene() -> Scene:
    sc = Scene("dead_oak", OAK_SIZE[0], OAK_SIZE[1], OAK_PIVOT)
    tree = oak_meshes()
    sc.add(tree.bark.xform(scale=SC), BARK, "oak.bark")
    if tree.dead is not None:
        sc.add(tree.dead.xform(scale=SC), DEADWOOD, "oak.dead")
    return sc


def render_oak():
    return oak_scene().render(wear=0.3, grime=0.6, rust=0.0, dust=0.08, exposure=0.95, hard_alpha=True)


OBJECTS = {"dead_oak": ObjectSpec(OAK_SIZE, OAK_PIVOT, (render_oak,))}


# ---------------------------------------------------------------------------------------------------
# bramble
# ---------------------------------------------------------------------------------------------------

BR_SIZE = (256, 192)
BR_PIVOT = (112, 134)

CANE = IsoMat((0.0055, 0.0046, 0.0042), (0.020, 0.019, 0.018), 0.82, wear=0.2, sat_cap=0.25,
              detail=(Waves3("bram.bark", 6, 2.4, 5.0, 0.05), Waves3("bram.grit", 5, 0.4, 0.9, 0.012)))
LEAF = IsoMat((0.062, 0.0055, 0.014), (0.040, 0.030, 0.030), 0.66, wear=0.0, sat_cap=0.60)

_LEAF_OUTLINE = np.array(((0, 0), (0.15, 0.45), (0.28, 0.33), (0.42, 0.80), (0.55, 0.52), (0.72, 0.62), (0.88, 0.22), (1.0, 0.0),
                          (0.86, -0.26), (0.70, -0.62), (0.55, -0.50), (0.40, -0.82), (0.26, -0.34), (0.12, -0.46)))


def _bases(d, r):
    """Per-row orthonormal frames (e1 = d, e2, e3) with a random roll."""
    d = d / np.linalg.norm(d, axis=1, keepdims=True)
    a = r.normal(size=d.shape)
    e2 = np.cross(d, a)
    e2 /= np.maximum(np.linalg.norm(e2, axis=1, keepdims=True), 1e-9)
    e3 = np.cross(d, e2)
    return d, e2, e3


def _leaves(centers, radii, counts, name):
    """Leaf cards in dense clumps: every leaf roots inside its clump and points outward, so the clump core is
    shadowed by the leaves around it. tint darkens towards the core."""
    r = U.rng("iso.bramble.leaves." + name)
    tmpl = _card_template()
    tv, tf = tmpl.v.astype(np.float64), tmpl.f
    cen = np.repeat(np.asarray(centers, dtype=np.float64), counts, axis=0)
    rad = np.repeat(np.asarray(radii, dtype=np.float64), counts)
    n = len(cen)
    off = r.normal(size=(n, 3)) * np.array((1.0, 1.0, 0.8))
    off /= np.maximum(np.linalg.norm(off, axis=1, keepdims=True), 1e-9)
    depth = r.random(n) ** 0.6
    pos = cen + off * (rad * depth)[:, None]
    d = off + r.normal(size=(n, 3)) * 0.45 + np.array((0, 0, 0.2))
    e1, e2, e3 = _bases(d, r)
    up = np.array((0, 0, 1.0))
    e3 = e3 + up * 0.8
    e3 -= e1 * (e3 * e1).sum(1, keepdims=True)
    e3 /= np.maximum(np.linalg.norm(e3, axis=1, keepdims=True), 1e-9)
    e2 = np.cross(e3, e1)
    L = (2.5 + 1.4 * r.random(n)) * (0.7 + 0.5 * depth)
    W = L * (0.75 + 0.3 * r.random(n))
    V = (pos[:, None, :] + e1[:, None, :] * (tv[None, :, 0:1] * L[:, None, None]) + e2[:, None, :] * (tv[None, :, 1:2] * W[:, None, None])
         + e3[:, None, :] * (tv[None, :, 2:3] * L[:, None, None]))
    nv = len(tv)
    F = (np.arange(n)[:, None, None] * nv + tf[None, :, :]).reshape(-1, 3)
    shade = np.clip(0.30 + 0.85 * depth * (0.8 + 0.4 * r.random(n)), 0.25, 1.2)
    tint = np.repeat(np.repeat(shade[:, None], nv, axis=0)[:, :1], 3, axis=1)
    return Mesh(V.reshape(-1, 3), F, None, np.tile(tmpl.uv, (n, 1)), tint)


def _card_template():
    from isoforge import card
    m = card(_LEAF_OUTLINE, bend=0.5)
    return m


def _thorns(pts, dirs, length, radius, name):
    """Flat-faceted thorn cones, one per row: base point, axis."""
    r = U.rng("iso.bramble.thorns." + name)
    n = len(pts)
    e1, e2, e3 = _bases(np.asarray(dirs, dtype=np.float64), r)
    sides = 5
    a = np.linspace(0, TAU, sides, endpoint=False)
    ring = np.stack((np.cos(a), np.sin(a)), 1)
    ln = np.asarray(length)[:, None]
    rd = np.asarray(radius)[:, None]
    base = pts[:, None, :] + (e2[:, None, :] * ring[None, :, 0:1] + e3[:, None, :] * ring[None, :, 1:2]) * rd[:, :, None]
    tip = pts + e1 * ln
    V = np.concatenate((base, tip[:, None, :], pts[:, None, :] - e1[:, None, :] * 0.2), axis=1)
    ii = np.arange(sides)
    tf = np.concatenate((np.stack((ii, (ii + 1) % sides, np.full(sides, sides)), 1)))
    F = (np.arange(n)[:, None, None] * (sides + 2) + tf[None]).reshape(-1, 3)
    return Mesh(V.reshape(-1, 3), F)


def bramble_scene() -> Scene:
    from isoforge import tube
    r = U.rng("iso.bramble.canes")
    sc = Scene("bramble", BR_SIZE[0], BR_SIZE[1], BR_PIVOT)
    canes = None
    tpts, tdirs, tlen, trad = [], [], [], []
    clump_c, clump_r, clump_n = [], [], []

    def add_cane(path, r0, r1, thorn_gap, leafy):
        nonlocal canes
        m = tube(path, np.linspace(r0, r1, 5), sides=6, seg_u=1.8, name="bram")
        canes = m if canes is None else canes.merge(m)
        # thorns along the cane, alternating sides
        from isoforge import _resample
        q = _resample(np.asarray(path, dtype=np.float64), thorn_gap)
        tg = np.gradient(q, axis=0)
        tg /= np.maximum(np.linalg.norm(tg, axis=1, keepdims=True), 1e-9)
        for k in range(1, len(q) - 1):
            rad = r0 + (r1 - r0) * k / len(q)
            side = _perp(tg[k], r)
            tpts.append(q[k] + side * rad * 0.7)
            tdirs.append(_unit(side * 0.85 + tg[k] * (0.35 + 0.3 * r.random())))
            tlen.append(1.8 + 1.4 * r.random())
            trad.append(max(0.42, rad * 0.6))
        if leafy:
            for f in leafy:
                k = int(f * (len(q) - 1))
                clump_c.append(q[k] + np.array((0, 0, 0.8)))
                clump_r.append(3.2 + 1.8 * r.random())
                clump_n.append(int(18 + 16 * r.random()))

    nmain = 19
    for i in range(nmain):
        a = TAU * (i + 0.5 * r.random()) / nmain
        c, s = math.cos(a), math.sin(a)
        R = (12 + 6 * r.random()) * (1 - 0.42 * max(0.0, math.cos(a - math.pi / 4)))
        H = 17 + 9 * r.random()
        tw = (r.random() - 0.5) * 0.9
        ct, st = math.cos(a + tw), math.sin(a + tw)
        pts = np.array(((c * 1.0, s * 1.0, -0.5), (c * 2.5, s * 2.5, H * 0.55), (ct * R * 0.5, st * R * 0.5, H), (ct * R * 0.9, st * R * 0.9, H * 0.7),
                        (ct * R * 1.15, st * R * 1.15, 2.0 + 3 * r.random())))
        pts[1:] += r.normal(size=(4, 3)) * 0.9
        add_cane(pts, 1.2, 0.3, 3.2, (0.8,) if i % 3 == 0 else ())
        # a side shoot from the arch
        k = 2
        d = _unit(pts[k + 1] - pts[k] + r.normal(size=3) * 0.8 + np.array((0, 0, 0.6)))
        sp = pts[k] + (pts[k + 1] - pts[k]) * 0.4
        sh = np.array((sp, sp + d * 6 + (0, 0, 1.5), sp + d * 12 + r.normal(size=3) * 1.2 - (0, 0, 2.5)))
        add_cane(sh, 0.6, 0.2, 3.0, (0.9,) if i % 2 == 0 else ())
    # a few low trailing canes
    for i in range(4):
        a = math.pi / 4 + math.radians((100, 150, 210, 260)[i] + 15 * r.random())
        c, s = math.cos(a), math.sin(a)
        pts = np.array(((c * 2, s * 2, 0.0), (c * 6, s * 6, 3.5), (c * 11, s * 11, 1.6), (c * 15, s * 15, 0.3)))
        add_cane(pts, 1.0, 0.28, 3.0, (0.85,))
    sc.add(canes, CANE, "bram.cane")
    sc.add(_thorns(np.array(tpts), np.array(tdirs), np.array(tlen), np.array(trad), "t"), CANE, "bram.thorn")
    sc.add(_leaves(clump_c, clump_r, clump_n, "l"), LEAF, "bram.leaf")
    return sc


def render_bramble():
    return bramble_scene().render(wear=0.1, grime=0.5, rust=0.0, dust=0.05, exposure=0.95, hard_alpha=True)


OBJECTS["bramble"] = ObjectSpec(BR_SIZE, BR_PIVOT, (render_bramble,))


# ---------------------------------------------------------------------------------------------------
# slate rocks
# ---------------------------------------------------------------------------------------------------

SL_SIZE = (192, 144)
SL_PIVOT = (76, 108)

SLATE = IsoMat((0.011, 0.013, 0.019), (0.045, 0.048, 0.056), 0.70, (0.040, 0.046, 0.058), (0.075, 0.078, 0.085), 0.9, sat_cap=0.30,
               detail=(Waves3("slate.step", 7, 2.4, 5.0, 0.07), Waves3("slate.layer", 6, 0.4, 0.9, 0.012, (1.0, 1.0, 0.3))))


def slate_scene() -> Scene:
    from isoforge import convex
    r = U.rng("iso.slate.layout")
    sc = Scene("slate_rocks", SL_SIZE[0], SL_SIZE[1], SL_PIVOT)
    mesh = None

    def put(name, rad, cuts, squash, pos, rot, layers=1):
        """A shard: `layers` thin slabs stacked along the cleavage normal, each a little smaller, shifted and
        twisted, so the silhouette and the faces show stepped cleavage planes."""
        nonlocal mesh
        rr = U.rng("iso.slate.layers." + name)
        m = None
        for k in range(layers):
            sq = (squash[0] * (0.82 + 0.3 * rr.random()), squash[1] / layers * 1.5, squash[2] * (0.78 + 0.35 * rr.random()))
            sl = convex(f"{name}{k}", rad * (0.9 + 0.15 * rr.random()), cuts, sq)
            sl = sl.xform(tuple((rr.random(3) - 0.5) * 5), ((rr.random() - 0.5) * rad * 0.25, (k - (layers - 1) / 2) * squash[1] * rad * 0.82,
                                                          (rr.random() - 0.3) * rad * 0.2))
            m = sl if m is None else m.merge(sl)
        m = m.xform(rot)
        lo = float(m.v[:, 2].min())
        m = m.xform((0, 0, 0), (pos[0], pos[1], -lo - 0.5))
        mesh = m if mesh is None else mesh.merge(m)

    shards = (
        ("a", 8.0, 5, (0.95, 0.34, 1.7), (0.0, 0.0), (8, -14, 20), 3),
        ("b", 6.6, 5, (0.9, 0.32, 1.45), (-8.5, 3.5), (-12, 22, 110), 3),
        ("c", 7.0, 5, (0.95, 0.30, 1.3), (7.5, 4.5), (14, 18, 65), 3),
        ("d", 5.4, 5, (0.95, 0.34, 1.2), (3.5, -8.0), (-24, -10, 150), 2),
        ("e", 5.0, 5, (1.0, 0.36, 1.0), (-5.0, -7.0), (30, 8, 30), 2),
        ("f", 4.2, 5, (1.0, 0.35, 0.9), (-12.0, -2.0), (-20, -28, 80), 2),
    )
    for s_ in shards:
        put(*s_)
    for i in range(9):
        a = TAU * (i + r.random() * 0.7) / 9
        d = 10 + 7 * r.random()
        put(f"r{i}", 1.8 + 1.6 * r.random(), 4, (1.0, 0.55, 0.7), (math.cos(a) * d, math.sin(a) * d),
            tuple(r.random(3) * 360))
    sc.add(mesh, SLATE, "slate.rock")
    return sc


def render_slate():
    return slate_scene().render(wear=0.5, grime=0.4, rust=0.0, dust=0.12, exposure=0.95, hard_alpha=False)


OBJECTS["slate_rocks"] = ObjectSpec(SL_SIZE, SL_PIVOT, (render_slate,))
