"""Isometric nature: a gnarled dead oak, a thorn bramble with crimson leaves, a cluster of slate slabs.

Form bands (u): primary 8 and up (trunk, crown, canes, leaf clumps, slabs), secondary 2..6 (bark ridges, knots, limbs,
leaf groups, cleavage steps), tertiary under 1 (grain, grit, thorn tips). The 1..2 u gap stays empty.
"""
from __future__ import annotations

import math

import numpy as np

import uiart as U
from isoforge import IsoMat, Mesh, ObjectSpec, Scene, Waves3, grid

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


def _fit_shadow(rd, margin):
    """Fade the ground shadow to zero over `margin` px before every canvas edge, so a long cast shadow ends softly
    inside the canvas instead of being cut off at its border."""
    sh = rd.shadow.copy()
    h, w = sh.shape[:2]
    ys = np.minimum(np.arange(h), h - 1 - np.arange(h))[:, None]
    xs = np.minimum(np.arange(w), w - 1 - np.arange(w))[None, :]
    t = np.clip(np.minimum(ys, xs) / float(margin), 0.0, 1.0)
    sh[..., 3] = np.floor(sh[..., 3] * (t * t * (3 - 2 * t)) + 0.5).astype(np.uint8)
    rd.shadow = sh
    return rd


def _poly(path, step):
    """A polyline with its corners cut once (a kink about 2 u across), resampled linearly about `step` apart."""
    p = np.asarray(path, dtype=np.float64)
    if len(p) > 2:
        c = [p[0]]
        for a, b in zip(p[:-1], p[1:]):
            c += [a * 0.75 + b * 0.25, a * 0.25 + b * 0.75]
        c.append(p[-1])
        p = np.array(c)
    s = np.concatenate(([0.0], np.cumsum(np.linalg.norm(np.diff(p, axis=0), axis=1))))
    n = max(2, int(round(s[-1] / step)) + 1)
    u = np.linspace(0, s[-1], n)
    return np.stack([np.interp(u, s, p[:, k]) for k in range(3)], axis=1)


def _kinked(r, start, direction, length, nseg, kink, lift=0.0, droop=0.0):
    """A polyline of nseg straight runs; the heading breaks by `kink` degrees at every joint, about an axis that
    keeps turning, so the limb zig-zags in 3D."""
    d = _unit(direction)
    ax = _perp(d, r)
    pts = [np.asarray(start, dtype=np.float64)]
    step = length / nseg
    sg = 1.0
    for i in range(nseg):
        pts.append(pts[-1] + d * step * (0.7 + 0.6 * r.random()))
        ang = math.radians(kink * (0.5 + 0.8 * r.random())) * sg
        sg = -sg if r.random() < 0.85 else sg
        d = _unit(_rot(d, ax, ang) + np.array((0, 0, lift - droop * (i + 1) / nseg)) * 0.25)
        ax = _unit(_rot(ax, d, 0.6 * (r.random() - 0.5) + (1.1 if r.random() < 0.3 else 0.0)))
    return np.array(pts)


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


def limb(path, r0, r1, name, ridges=0, amp=0.35, knot=0.0, flare=0.0, flare_len=6.0, twist=0.035, step=1.6, taper=1.3,
         ragged=0.0, flat=False, cap=True):
    """Angular limb along a kinked path: the radius falls as (1-t)^taper from r0 to r1, swells into a knot at every kink,
    and carries deep V ridges that wander with length. ragged > 0 splinters the end (a broken limb): the last rings
    are pushed along the axis by up to `ragged` u, per side. flat shades every facet (thin twigs)."""
    r = U.rng("iso.oak." + name)
    q = _poly(path, step)
    K = len(q)
    t, Nn, Bb = _frames(q)
    s = np.concatenate(([0.0], np.cumsum(np.linalg.norm(np.diff(q, axis=0), axis=1))))
    tt = s / max(s[-1], 1e-9)
    base = r1 + (r0 - r1) * (1 - tt) ** taper
    if knot:
        pj = np.asarray(path, dtype=np.float64)
        sj = np.concatenate(([0.0], np.cumsum(np.linalg.norm(np.diff(pj, axis=0), axis=1))))
        sj = sj / max(sj[-1], 1e-9) * s[-1]
        for v in sj[1:-1]:
            base = base * (1 + knot * np.exp(-(((s - v) / (1.1 + 0.25 * r0)) ** 2)))
    if flare:
        base = base + flare * np.exp(-s / flare_len)
    ph = r.random(4) * TAU
    sides = max(6, 6 * ridges) if ridges else 5
    th = np.linspace(0, TAU, sides + 1)
    if ridges:
        wob = 1.7 * np.sin(s[:, None] * 0.23 + 2 * th[None, :] + ph[2]) + 1.2 * np.sin(s[:, None] * 0.47 - th[None, :] + ph[3])
        tw = twist * s + 0.012 * s * np.sin(s * 0.05 + ph[0])
        x = ridges * (th[None, :] + tw[:, None]) + wob
        crest = np.abs(np.cos(x / 2)) ** 0.30
        fac = 1 - amp * (1 - crest)
        tint = 0.30 + 0.70 * crest
    else:
        fac = 1 + 0.10 * np.sin(s[:, None] * 0.9 + 3 * th[None, :] + ph[2])
        tint = np.ones_like(fac)
    rad = base[:, None] * fac
    P = q[:, None, :] + (np.cos(th)[None, :, None] * Nn[:, None, :] + np.sin(th)[None, :, None] * Bb[:, None, :]) * rad[:, :, None]
    if ragged:
        jag = r.random(sides)
        jag = np.append(jag, jag[0])
        for back, f in ((0, 1.0), (1, 0.45)):
            k = K - 1 - back
            P[k] = P[k] + t[k][None, :] * (ragged * f * jag)[:, None]
            P[k] = q[k][None, :] + (P[k] - q[k][None, :]) * (0.55 + 0.45 * (1 - jag))[:, None] ** (1 if back == 0 else 0)
    uv = np.stack((np.broadcast_to(th[None, :] / TAU, (K, sides + 1)), np.broadcast_to(s[:, None], (K, sides + 1))), -1)
    m = grid(P, uv, smooth_normals=not flat)
    m.tint = np.repeat(tint.reshape(-1, 1), 3, axis=1).astype(np.float32)
    if cap:
        for end, sg in ((0, -1.0), (K - 1, 1.0)):
            if end == 0 or r1 > 0.4:
                ring = P[end, :sides]
                cv = np.vstack((ring, ring.mean(0)[None, :]))
                ii = np.arange(sides)
                cf = np.stack((ii, (ii + 1) % sides, np.full(sides, sides)), axis=1)
                fnv = np.cross(cv[cf[:, 1]] - cv[cf[:, 0]], cv[cf[:, 2]] - cv[cf[:, 0]])
                bad = (fnv * t[end] * sg).sum(1) < 0
                cf[bad] = cf[bad][:, ::-1]
                cp = Mesh(cv, cf)
                cp.tint = np.full((len(cv), 3), 0.7, dtype=np.float32)
                m = m.merge(cp)
    return m


# ---------------------------------------------------------------------------------------------------
# dead oak
# ---------------------------------------------------------------------------------------------------

OAK_SIZE = (448, 416)
OAK_PIVOT = (166, 311)
SC = 1.15

# cool-leaning dark bark: the warm key lifts the ridge crests, the blue fill still reads in the grooves
BARK = IsoMat((0.062, 0.046, 0.034),(0.020, 0.019, 0.019), 0.86, (0.085, 0.072, 0.060), (0.030, 0.028, 0.026), 0.5, sat_cap=0.30,
              detail=(Waves3("oak.crack", 8, 2.4, 5.0, 0.07), Waves3("oak.grit", 6, 0.4, 0.9, 0.018), Waves3("oak.roll", 6, 5.0, 8.0, 0.14)))
DEADWOOD = IsoMat((0.085, 0.074, 0.064), (0.034, 0.032, 0.030), 0.84, wear=0.3, sat_cap=0.28,
                  detail=(Waves3("oak.grain", 7, 0.4, 0.9, 0.016, (1.0, 1.0, 0.2)), Waves3("oak.split", 6, 2.5, 5.0, 0.07)))


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


# per order: (radius factor of a child, length factor, kinks (deg), kids)
def _grow(tree, r, start, direction, length, rad, order, tag, droop):
    """One limb: a kinked path, a ridged tube that tapers hard, and child limbs spawned along it."""
    d = _unit(direction)
    start = np.asarray(start, dtype=np.float64) - d * 1.4 * rad ** 0.5
    nseg = max(3, int(length / 7.0))
    pts = _kinked(r, start, d, length, nseg, 26 + 6 * order, lift=0.5 - 0.15 * order, droop=droop)
    broken = order >= 1 and r.random() < (0.45 if order < 3 else 0.2)
    ridges = 0 if rad < 1.5 else int(max(5, round(rad * TAU / 3.2)))
    m = limb(pts, rad, rad * (0.38 if broken else 0.14) + 0.1, tag, ridges=ridges, amp=0.45 if rad > 2.5 else 0.3, knot=0.30 if rad > 1.1 else 0.0,
             twist=0.03 * (1 if r.random() < 0.5 else -1), step=1.6, taper=0.8 if broken else 1.0, ragged=2.4 if broken else 0.0, flat=rad < 1.5)
    tree.add(m)
    tree.n += 1
    if order >= 4:
        return
    kids = {1: 3, 2: 2, 3: 2}[order] + (1 if rad > 3.5 else 0)
    if broken:
        kids -= 1
    fr = np.sort(0.30 + 0.62 * r.random(kids)) if kids > 0 else []
    ang0 = r.random() * TAU
    q = _poly(pts, 1.6)
    for j, f in enumerate(fr):
        k = min(int(f * (len(q) - 1)), len(q) - 2)
        pos = q[k]
        t = _unit(q[k + 1] - q[k])
        side = _rot(_perp(t, r), t, ang0 + j * 2.4)
        spread = math.radians(42 + 36 * r.random())
        cd = _unit(t * math.cos(spread) + side * math.sin(spread) + np.array((0, 0, 0.15)))
        crad = rad * (1 - 0.5 * f) * (0.55 + 0.2 * r.random()) + 0.1
        clen = length * (0.62 + 0.3 * r.random()) * (1 - 0.3 * f)
        if crad < 0.42 or clen < 5:
            continue
        _grow(tree, r, pos, cd, clen, crad, order + 1, f"{tag}.{j}", droop + 0.5)


def _crown(tree, top, r, r_top):
    """A jagged broken crown: a few wide splintered teeth of different heights around a ragged rim, no flat cap."""
    n = 5
    a0 = r.random() * TAU
    hs = (15.0, 7.5, 11.0, 5.0, 9.0)
    for i in range(n):
        a = a0 + TAU * (i + 0.25 * r.random()) / n
        rim = r_top * (0.55 + 0.25 * r.random())
        base = top + np.array((math.cos(a) * rim, math.sin(a) * rim, -4.0))
        lean = 0.2 + 0.4 * r.random()
        dr = _unit((math.cos(a) * lean, math.sin(a) * lean, 1.0))
        pts = _kinked(r, base, dr, hs[i] + 4.0, 3, 24)
        tree.add(limb(pts, 2.9 + 0.6 * r.random(), 0.15, f"crown{i}", ridges=3, amp=0.5, step=1.5, taper=0.75))


def oak_meshes():
    r = U.rng("iso.oak.skeleton")
    tree = _Tree()
    tp = np.array(((0, 0, -3), (2.4, 1.0, 12), (-1.8, 3.6, 28), (-4.6, 0.8, 44), (-1.6, -2.8, 60), (1.2, -3.4, 74)), dtype=np.float64)
    tree.add(limb(tp, 8.4, 4.2, "trunk", ridges=9, amp=0.45, knot=0.12, flare=5.0, flare_len=5.5, twist=0.012, step=1.5, taper=0.8, cap=False))
    top = tp[-1]
    _crown(tree, top, r, 3.9)
    def az(phi, up):
        th = math.radians(-45.0 + phi)
        return (math.cos(th), math.sin(th), up)

    mains = (
        (tp[3], az(8, 0.55), 38, 5.2),
        (tp[4], az(178, 0.6), 36, 5.0),
        (tp[2] + (0, 0, 2), az(205, 0.25), 32, 4.6),
        (tp[4] + (0, 0, 2), az(335, 0.9), 30, 4.0),
        (tp[1] + (0, 0, 3), az(30, 0.2), 28, 4.4),
        (tp[5] - (0, 0, 4), az(120, 0.8), 26, 3.6),
        (tp[3] - (0, 0, 3), az(250, 0.35), 28, 4.0),
    )
    for i, (st, dr, ln, rd) in enumerate(mains):
        _grow(tree, r, st, dr, ln, rd, 1, f"m{i}", droop=0.5)
    # root collar: buttress roots arching into the ground
    for i in range(7):
        a = TAU * (i + 0.3 * r.random()) / 7
        c, s = math.cos(a), math.sin(a)
        reach = 10 + 4 * r.random()
        pts = np.array(((c * 3.0, s * 3.0, 11), (c * 7.5, s * 7.5, 4.8), (c * reach * 0.75, s * reach * 0.75, 1.8), (c * reach, s * reach, -0.6)))
        rp = np.cross((c, s, 0), (0, 0, 1)) * (r.random() - 0.5) * 7
        pts[1:, :] += rp[None, :] * np.array((0.5, 1.0, 1.0))[:, None]
        tree.add(limb(pts, 3.6, 0.6, f"root{i}", ridges=7, amp=0.35, knot=0.15, twist=0.02, step=1.6, taper=1.0))
    return tree


def oak_scene() -> Scene:
    sc = Scene("dead_oak", OAK_SIZE[0], OAK_SIZE[1], OAK_PIVOT)
    tree = oak_meshes()
    sc.add(tree.bark.xform(scale=SC), BARK, "oak.bark")
    if tree.dead is not None:
        sc.add(tree.dead.xform(scale=SC), DEADWOOD, "oak.dead")
    return sc


def render_oak():
    return _fit_shadow(oak_scene().render(wear=0.35, grime=0.6, rust=0.0, dust=0.08, exposure=0.95, hard_alpha=False), 72)


OBJECTS = {"dead_oak": ObjectSpec(OAK_SIZE, OAK_PIVOT, (render_oak,))}


# ---------------------------------------------------------------------------------------------------
# bramble
# ---------------------------------------------------------------------------------------------------

BR_SIZE = (288, 288)
BR_PIVOT = (122, 139)

# charcoal canes (cool, a little lighter than the shadows) and thorns that wear to a pale bright edge
CANE = IsoMat((0.034, 0.031, 0.033), (0.030, 0.030, 0.032), 0.74, (0.10, 0.09, 0.09), (0.05, 0.05, 0.055), 0.6, sat_cap=0.25,
              detail=(Waves3("bram.bark", 6, 2.4, 5.0, 0.05), Waves3("bram.grit", 5, 0.4, 0.9, 0.012)))
THORN = IsoMat((0.040, 0.036, 0.037), (0.060, 0.060, 0.064), 0.68, (0.24, 0.20, 0.18), (0.10, 0.10, 0.11), 1.0, sat_cap=0.25)
LEAF = IsoMat((0.060, 0.0058, 0.0170), (0.012, 0.010, 0.012), 0.72, wear=0.0, sat_cap=0.60)

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


def _card_template():
    from isoforge import card
    return card(_LEAF_OUTLINE, bend=0.5)


def _leaves(centers, radii, counts, name):
    """Leaf cards in dense clumps: every leaf roots inside its clump and points outward, so the clump core is
    shadowed by the leaves around it. tint darkens towards the core."""
    r = U.rng("iso.bramble.leaves." + name)
    tmpl = _card_template()
    tv, tf = tmpl.v.astype(np.float64), tmpl.f
    cen = np.repeat(np.asarray(centers, dtype=np.float64), counts, axis=0)
    rad = np.repeat(np.asarray(radii, dtype=np.float64), counts)
    n = len(cen)
    off = r.normal(size=(n, 3)) * np.array((1.0, 1.0, 0.75))
    off /= np.maximum(np.linalg.norm(off, axis=1, keepdims=True), 1e-9)
    depth = r.random(n) ** 0.4
    pos = cen + off * (rad * depth)[:, None]
    d = off + r.normal(size=(n, 3)) * 0.40 + np.array((0, 0, 0.25))
    e1, e2, e3 = _bases(d, r)
    up = np.array((0, 0, 1.0))
    e3 = e3 + up * 0.8
    e3 -= e1 * (e3 * e1).sum(1, keepdims=True)
    e3 /= np.maximum(np.linalg.norm(e3, axis=1, keepdims=True), 1e-9)
    e2 = np.cross(e3, e1)
    L = (3.4 + 1.6 * r.random(n)) * (0.7 + 0.5 * depth)
    W = L * (0.75 + 0.3 * r.random(n))
    V = (pos[:, None, :] + e1[:, None, :] * (tv[None, :, 0:1] * L[:, None, None]) + e2[:, None, :] * (tv[None, :, 1:2] * W[:, None, None])
         + e3[:, None, :] * (tv[None, :, 2:3] * L[:, None, None]))
    nv = len(tv)
    F = (np.arange(n)[:, None, None] * nv + tf[None, :, :]).reshape(-1, 3)
    shade = np.clip(0.80 + 0.60 * depth * (0.8 + 0.4 * r.random(n)), 0.75, 1.4)
    tint = np.repeat(np.repeat(shade[:, None], nv, axis=0)[:, :1], 3, axis=1)
    # a share of the leaves has withered to a dull grey-mauve: it breaks the crimson up and lowers the saturated share
    dry = np.repeat(r.random(n) < 0.36, nv)
    tint = tint * np.where(dry[:, None], np.array((0.60, 3.2, 1.3)), np.array((1.0, 1.0, 1.0)))
    return Mesh(V.reshape(-1, 3), F, None, np.tile(tmpl.uv, (n, 1)), tint)


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
    tf = np.stack((ii, (ii + 1) % sides, np.full(sides, sides)), 1)
    F = (np.arange(n)[:, None, None] * (sides + 2) + tf[None]).reshape(-1, 3)
    return Mesh(V.reshape(-1, 3), F)


def bramble_scene() -> Scene:
    r = U.rng("iso.bramble.canes")
    sc = Scene("bramble", BR_SIZE[0], BR_SIZE[1], BR_PIVOT)
    canes = None
    tpts, tdirs, tlen, trad = [], [], [], []
    cand = []   # leaf clump candidates: points on canes

    def add_cane(path, r0, r1, thorn_gap, clumpy, tag):
        nonlocal canes
        m = limb(path, r0, r1, "bram" + tag, ridges=0, amp=0.0, knot=0.25 if r0 > 0.8 else 0.0, step=1.4, taper=1.0, flat=True)
        canes = m if canes is None else canes.merge(m)
        q = _poly(path, thorn_gap)
        tg = np.gradient(q, axis=0)
        tg /= np.maximum(np.linalg.norm(tg, axis=1, keepdims=True), 1e-9)
        sgn = 1.0
        for k in range(1, len(q) - 1):
            rad = r0 + (r1 - r0) * k / len(q)
            side = _perp(tg[k], r)
            side = side * sgn + 0.0
            sgn = -sgn
            tpts.append(q[k] + side * rad * 0.55)
            tdirs.append(_unit(side * 0.9 + tg[k] * (0.25 + 0.5 * r.random()) + np.array((0, 0, 0.25))))
            tlen.append(3.0 + 1.6 * r.random())
            trad.append(max(0.5, rad * 0.62))
        if clumpy:
            for f in clumpy:
                cand.append(q[int(f * (len(q) - 1))] + np.array((0, 0, 1.0)))

    ncane = 13
    for i in range(ncane):
        a = TAU * (i + 0.5 * r.random()) / ncane
        c, s = math.cos(a), math.sin(a)
        R = (13 + 9 * r.random()) * (1 - 0.30 * max(0.0, math.cos(a - math.pi / 4)))
        H = 11 + 14 * r.random()
        tw = (r.random() - 0.5) * 1.3
        ct, st = math.cos(a + tw), math.sin(a + tw)
        e = 1.0 + 0.9 * r.random()
        pts = np.array(((c * 1.5, s * 1.5, -0.5), (c * 3.0, s * 3.0, H * 0.6), (ct * R * 0.45, st * R * 0.45, H), (ct * R * 0.85, st * R * 0.85, H * (0.35 + 0.5 * r.random())),
                        (ct * R * 1.2, st * R * 1.2, (r.random() * 0.45) * H * e)))
        pts[1:] += r.normal(size=(4, 3)) * 1.6
        add_cane(pts, 1.25, 0.28, 3.4, (0.5, 0.7), f"c{i}")
        # a side shoot from the arch, reaching out past the leaves
        k = 2 + (i % 2)
        d = _unit(pts[k + 1] - pts[k] + r.normal(size=3) * 1.0 + np.array((0, 0, 0.5)))
        sp = pts[k] + (pts[k + 1] - pts[k]) * 0.4
        sh = np.array((sp, sp + d * 5 + (0, 0, 2.0), sp + d * 9 + r.normal(size=3) * 1.5 - (0, 0, 1.0), sp + d * 13 + r.normal(size=3) * 2.0 - (0, 0, 4.0)))
        add_cane(sh, 0.7, 0.2, 3.2, (0.35,) if i % 2 else (), f"s{i}")
    # low whips trailing along the ground
    for i in range(5):
        a = math.radians((20, 100, 170, 235, 305)[i] + 15 * r.random())
        c, s = math.cos(a), math.sin(a)
        pts = np.array(((c * 2, s * 2, 0.5), (c * 8, s * 8, 6.0), (c * 15, s * 15, 4.0), (c * 24, s * 24, 0.5)))
        pts[1:] += r.normal(size=(3, 3)) * 1.4
        add_cane(pts, 0.9, 0.22, 3.2, (), f"w{i}")
    # clumps: picked far enough apart that dark gaps (and the canes crossing them) stay open between
    order = r.permutation(len(cand))
    cc, cr, cn = [], [], []
    for k in order:
        p = cand[k]
        if np.hypot(p[0] - 0.0, p[1] - 0.0) < 3.0 or p[2] < 3.0:
            continue
        if all(np.linalg.norm(p - q_) > 13.0 for q_ in cc):
            cc.append(p)
            cr.append(4.4 + 1.2 * r.random())
            cn.append(int(28 + 8 * r.random()))
        if len(cc) >= 9:
            break
    sc.add(canes, CANE, "bram.cane")
    sc.add(_thorns(np.array(tpts), np.array(tdirs), np.array(tlen), np.array(trad), "t"), THORN, "bram.thorn")
    sc.add(_leaves(cc, cr, cn, "l"), LEAF, "bram.leaf")
    return sc


def render_bramble():
    return _fit_shadow(bramble_scene().render(wear=0.6, grime=0.5, rust=0.0, dust=0.05, exposure=0.95, hard_alpha=True), 48)


OBJECTS["bramble"] = ObjectSpec(BR_SIZE, BR_PIVOT, (render_bramble,))


# ---------------------------------------------------------------------------------------------------
# slate rocks
# ---------------------------------------------------------------------------------------------------

SL_SIZE = (240, 192)
SL_PIVOT = (115, 105)

# grey-blue shale: a cool body, warm bare rock where the edges wear through, so the lit tops read warm
SLATE = IsoMat((0.034, 0.040, 0.054), (0.020, 0.022, 0.026), 0.74, (0.062, 0.060, 0.060), (0.040, 0.040, 0.044), 0.8, sat_cap=0.30,
               detail=(Waves3("slate.step", 7, 2.4, 5.0, 0.035), Waves3("slate.layer", 6, 0.4, 0.9, 0.012, (1.0, 1.0, 0.3))))


def slate_scene() -> Scene:
    from isoforge import convex
    r = U.rng("iso.slate.layout")
    sc = Scene("slate_rocks", SL_SIZE[0], SL_SIZE[1], SL_PIVOT)
    mesh = None

    def put(name, rad, layers, pos, tilt, yaw, thick=1.5, flat=1.0):
        """A slab of shale: `layers` plates stacked on a common cleavage plane, each a little smaller, shifted and
        rotated about the plane normal, so the edge shows stepped layers. The group is tilted, then dropped to the ground."""
        nonlocal mesh
        rr = U.rng("iso.slate.layers." + name)
        m = None
        z = 0.0
        for k in range(layers):
            h = thick * (0.75 + 0.5 * rr.random())
            sl = convex(f"{name}{k}", rad * (1.0 - 0.17 * k) * (0.9 + 0.2 * rr.random()), 8, (1.25 * flat, 1.0, h / rad * 1.9))
            sl = sl.xform((0, 0, (rr.random() - 0.5) * 50), ((rr.random() - 0.5) * rad * 0.35, (rr.random() - 0.5) * rad * 0.3, z + h * 0.5))
            z += h * 1.15
            m = sl if m is None else m.merge(sl)
        m = m.xform((tilt[0], tilt[1], yaw))
        lo = float(m.v[:, 2].min())
        m = m.xform((0, 0, 0), (pos[0], pos[1], -lo - 0.35))
        mesh = m if mesh is None else mesh.merge(m)

    # name, radius, layers, pos, tilt (about x, y), yaw, plate thickness, width factor
    slabs = (
        ("a", 10.5, 4, (0.0, 0.0), (6, -9), 20, 1.7, 1.0),
        ("b", 8.8, 3, (-12.5, 5.5), (-13, 14), 110, 1.6, 1.0),
        ("c", 9.2, 3, (11.5, 6.0), (9, 16), 65, 1.5, 1.0),
        ("d", 7.0, 3, (4.0, -11.5), (-10, -12), 150, 1.4, 1.0),
        ("e", 6.0, 2, (-8.0, -10.0), (14, 6), 30, 1.3, 1.0),
        ("f", 11.0, 2, (-1.0, 11.5), (68, 6), 95, 1.3, 0.85),    # thin plates leaning against the heap
        ("g", 7.5, 2, (-17.0, -2.0), (52, -14), 20, 1.2, 0.8),
    )
    for s_ in slabs:
        put(*s_)
    for i in range(10):
        a = TAU * (i + r.random() * 0.7) / 10
        d = 15 + 7 * r.random()
        put(f"r{i}", 1.8 + 1.8 * r.random(), 1, (math.cos(a) * d, math.sin(a) * d * 0.9), tuple((r.random(2) - 0.5) * 40), r.random() * 360, 0.9)
    sc.add(mesh, SLATE, "slate.rock")
    return sc


def render_slate():
    return _fit_shadow(slate_scene().render(wear=0.25, grime=0.4, rust=0.0, dust=0.12, exposure=0.95, hard_alpha=False), 40)


OBJECTS["slate_rocks"] = ObjectSpec(SL_SIZE, SL_PIVOT, (render_slate,))
