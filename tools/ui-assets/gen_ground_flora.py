"""Ground flora: forty original, grim, rigid GLBs (trees, saplings, bushes, grass, flowers, mushrooms) the client scatters
over the voxel floor. Nothing here blocks anything; it is dressing.

    python tools/ui-assets/gen_ground_flora.py [out_root] [--only stem,stem]

Writes <out_root>/flora.json and <out_root>/flora/<stem>.glb (default out_root: dream/animations/ground/). The bake,
the manifest and the budgets are groundkit.py's; this file is the forty mesh builders and their specs.

Authored in u (16 u = 1 m, z up, front +y), standing on z = 0 and centred, exported at gen_props.WORLD_UNITS_PER_U.
Every builder is a pure function of fixed rng names: no clock, environment or file is read.

Form bands (u): primary 8 and up (trunks, crowns, clumps), secondary 2..6 (limbs, shelves, petal groups, bark ridges),
tertiary under 1 (twigs, blades, thorns, florets). Grass is geometry blades, not alpha cards.
Palette: muted olive ash-greens, umber, bone; flowers are dull (greyed violet, bruised red, dirty ivory). The green
hues sit on the yellow side (under 85 degrees) because the palette gate forbids green-cyan at any real saturation.
"""
from __future__ import annotations

import importlib
import math
import sys
import zlib
from dataclasses import replace
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
if str(HERE) not in sys.path:
    sys.path.insert(0, str(HERE))

import isoforge as I  # noqa: E402
import uiart as U  # noqa: E402
from isoforge import Mesh, Waves3  # noqa: E402

# by name: check_sources.py lets a tool import only a fixed set of siblings
N = importlib.import_module("gen_iso_nature")
GP = importlib.import_module("gen_props")
K = importlib.import_module("groundkit")
Part, cat = GP.Part, GP.cat

TAU = math.tau
FAMILY = "flora"
GENERATOR = "tools/ui-assets/gen_ground_flora.py"


# ---------------------------------------------------------------------------------------------- materials
def _m(token, rough=0.90, cap=0.20, wear=0.7, detail=None):
    m = GP.M(token, rough, cap=cap, wear=wear)
    if detail:
        m = replace(m, detail=tuple(detail))
    return m


def _bark_detail(tag, amp=0.05):
    return (Waves3(f"gf.{tag}.bark", 6, 2.4, 5.0, amp), Waves3(f"gf.{tag}.grit", 5, 0.4, 0.9, 0.012))


BARK = _m("#2c2722", 0.90, 0.18, 0.9, _bark_detail("bark"))
BARK_ASH = _m("#403c35", 0.90, 0.18, 0.8, _bark_detail("ash"))
BARK_PALE = _m("#625e53", 0.88, 0.18, 0.7, _bark_detail("pale", 0.035))
BARK_MARK = _m("#1d1b19", 0.92, 0.16, 0.4)
DEAD = _m("#46423a", 0.90, 0.18, 0.8, _bark_detail("dead", 0.04))
CHAR = _m("#1e1c1a", 0.92, 0.14, 0.5, _bark_detail("char", 0.04))
HOLLOW = _m("#0e0d0c", 0.95, 0.10, 0.0)
ROPE = _m("#3b372e", 0.95, 0.20, 0.5)
TWIG = _m("#35302a", 0.90, 0.18, 0.5)
TWIG_PALE = _m("#3f3b34", 0.90, 0.18, 0.5)
THORN = _m("#2b2824", 0.78, 0.20, 0.9)
FOL = _m("#383b26", 0.92, 0.26, 0.2)            # ash olive
FOL_DRY = _m("#353120", 0.92, 0.24, 0.2)
FOL_DARK = _m("#2b2e1f", 0.92, 0.22, 0.2)
STRAW = _m("#3d3826", 0.94, 0.24, 0.2)
STRAW_PALE = _m("#443e2d", 0.94, 0.24, 0.2)
SEDGE = _m("#3a3f27", 0.92, 0.26, 0.2)
STEM = _m("#3e402a", 0.92, 0.24, 0.2)
IVORY = _m("#46423a", 0.88, 0.22, 0.3)
IVORY_DIRTY = _m("#524c3d", 0.90, 0.22, 0.4)
VIOLET = _m("#4a4359", 0.90, 0.26, 0.2)
VIOLET_DARK = _m("#352f40", 0.90, 0.22, 0.2)
BRUISE = _m("#4a2322", 0.88, 0.28, 0.3)
OCHRE = _m("#5a5236", 0.90, 0.26, 0.2)
CAP_GREY = _m("#403e39", 0.86, 0.20, 0.4)
CAP_INK = _m("#201f22", 0.80, 0.16, 0.2)
CAP_UMBER = _m("#392c23", 0.86, 0.22, 0.3)
CAP_TOAD = _m("#3b3830", 0.88, 0.22, 0.4)
SHELF = _m("#4a4034", 0.90, 0.22, 0.4)


# ---------------------------------------------------------------------------------------------- mesh accumulation
class Acc:
    """Triangles collected from numpy arrays into one flat-shaded Mesh (no merge cost per blade or leaf). Every piece
    carries its own brightness (vertex tint, +-jitter, hashed from the piece's own coordinates so it is deterministic and
    independent of the way it faces): the individual blades and petals differ, and the atlas luma is not a function of
    the surface direction."""

    def __init__(self, jitter: float = 0.28):
        self.v, self.f, self.t = [], [], []
        self.n = 0
        self.jitter = jitter

    def add(self, verts, faces, want=None):
        """want: a direction the face normals should lean towards; the winding of the whole piece flips if it does not."""
        v = np.asarray(verts, dtype=np.float64)
        f = np.asarray(faces, dtype=np.int64)
        if want is not None:
            nrm = np.cross(v[f[:, 1]] - v[f[:, 0]], v[f[:, 2]] - v[f[:, 0]]).sum(axis=0)
            if float(nrm @ np.asarray(want, dtype=np.float64)) < 0:
                f = f[:, ::-1]
        h = zlib.crc32(np.ascontiguousarray(np.round(v * 64.0).astype(np.int32)).tobytes()) % 1000
        self.t.append(np.full(len(v), 1.0 + self.jitter * (h / 500.0 - 1.0)))
        self.v.append(v)
        self.f.append(f + self.n)
        self.n += len(v)

    def mesh(self):
        t = np.concatenate(self.t)
        return Mesh(np.vstack(self.v), np.vstack(self.f), None, None, np.repeat(t[:, None], 3, axis=1))

    def __bool__(self):
        return bool(self.v)


def _rz(a):
    c, s = math.cos(a), math.sin(a)
    return np.array(((c, -s, 0), (s, c, 0), (0, 0, 1.0)))


def _ry(a):
    c, s = math.cos(a), math.sin(a)
    return np.array(((c, 0, s), (0, 1, 0), (-s, 0, c)))


def _rx(a):
    c, s = math.cos(a), math.sin(a)
    return np.array(((1, 0, 0), (0, c, -s), (0, s, c)), dtype=np.float64)


def blade(acc, base, h, w, yaw, lean, segs=3, want=None):
    """A tapering ribbon from `base` (x, y) up to height h, bending `lean` (fraction of h) along yaw to a point."""
    cx, cy = math.cos(yaw), math.sin(yaw)
    px, py = -cy, cx
    rows = []
    for i in range(segs):
        t = i / segs
        off = lean * h * t * t
        z = h * t * (1.0 - 0.18 * lean * t)
        wi = w * (1.0 - 0.75 * t ** 1.5) * 0.5
        c = np.array((base[0] + cx * off, base[1] + cy * off, z))
        rows.append((c + np.array((-px, -py, 0)) * wi, c + np.array((px, py, 0)) * wi))
    tip = np.array((base[0] + cx * lean * h, base[1] + cy * lean * h, h * (1.0 - 0.18 * lean)))
    verts, faces = [], []
    for l, r in rows:
        verts += [l, r]
    verts.append(tip)
    for i in range(segs - 1):
        a = 2 * i
        faces += [(a, a + 1, a + 3), (a, a + 3, a + 2)]
    a = 2 * (segs - 1)
    faces.append((a, a + 1, 2 * segs))
    if want is None:
        want = (base[0], base[1], 0.5 * max(math.hypot(base[0], base[1]), 1.0))
    acc.add(verts, faces, want)


def leaf(acc, pos, yaw, elev, roll, length, width, outline, bend=0.0, want=(0, 0, 1.0)):
    o = np.asarray(outline, dtype=np.float64)
    cen = o.mean(0)
    pts = np.vstack((cen[None, :], o))
    loc = np.column_stack((pts[:, 0] * length, pts[:, 1] * width, bend * (pts[:, 1] * width) ** 2))
    R = _rz(yaw) @ _ry(-elev) @ _rx(roll)
    v = loc @ R.T + np.asarray(pos, dtype=np.float64)
    k = len(o)
    f = [(0, 1 + i, 1 + (i + 1) % k) for i in range(k)]
    acc.add(v, f, want)


OUT_LANCE = ((0, 0), (0.30, 0.42), (0.72, 0.30), (1.0, 0.0), (0.72, -0.30), (0.30, -0.42))
OUT_SERRATE = ((0, 0), (0.10, 0.32), (0.26, 0.22), (0.38, 0.52), (0.55, 0.30), (0.70, 0.36), (1.0, 0.0), (0.70, -0.36),
               (0.55, -0.30), (0.38, -0.52), (0.26, -0.22), (0.10, -0.32))
OUT_ROUND = ((0, 0), (0.18, 0.55), (0.62, 0.60), (1.0, 0.18), (1.0, -0.18), (0.62, -0.60), (0.18, -0.55))
OUT_PETAL = ((0, 0.0), (0.35, 0.34), (0.85, 0.22), (1.0, 0.0), (0.85, -0.22), (0.35, -0.34))
OUT_CURL = ((0, 0), (0.22, 0.30), (0.62, 0.40), (1.0, 0.04), (0.62, -0.20), (0.22, -0.34))


def octa(acc, c, r, squash=1.0):
    c = np.asarray(c, dtype=np.float64)
    v = [c + (r, 0, 0), c + (-r, 0, 0), c + (0, r, 0), c + (0, -r, 0), c + (0, 0, r * squash), c + (0, 0, -r * squash)]
    f = [(0, 2, 4), (2, 1, 4), (1, 3, 4), (3, 0, 4), (2, 0, 5), (1, 2, 5), (3, 1, 5), (0, 3, 5)]
    acc.add(v, f)


def thorn_cone(acc, base, d, length, radius):
    d = np.asarray(d, dtype=np.float64)
    d = d / max(np.linalg.norm(d), 1e-9)
    a = np.cross(d, (0, 0, 1.0)) if abs(d[2]) < 0.9 else np.cross(d, (1.0, 0, 0))
    a /= np.linalg.norm(a)
    b = np.cross(d, a)
    b_ = np.asarray(base, dtype=np.float64)
    ring = [b_ + (a * math.cos(t) + b * math.sin(t)) * radius for t in (0, TAU / 3, 2 * TAU / 3)]
    verts = ring + [b_ + d * length]
    acc.add(verts, [(0, 1, 3), (1, 2, 3), (2, 0, 3)], want=a + b)


def _fix(m: Mesh) -> Mesh:
    """Outward winding for a closed solid (signed volume positive)."""
    v = m.v.astype(np.float64)
    vol = float(np.einsum("ij,ij->i", v[m.f[:, 0]], np.cross(v[m.f[:, 1]], v[m.f[:, 2]])).sum())
    if vol >= 0:
        return m
    return Mesh(m.v, m.f[:, ::-1].copy(), None if m.n is None else -m.n, m.uv, m.tint)


def stalk(pts, r0, r1, tag, sides_step=1.8):
    """A thin flat-faceted stem along pts."""
    return N.limb(np.asarray(pts, dtype=np.float64), r0, r1, f"gf.{tag}", ridges=0, step=sides_step, taper=1.0, flat=True, cap=False)


TREE_MAX_H = 114.0    # u: 4.45 wu, under the 4.5 wu ceiling
COVER_MAX_H = 9.9     # u: 0.386 wu, under the 0.4 wu ceiling
SAPLING_MAX_H = 27.0  # u: 1.05 wu
BUSH_MAX_H = 25.0     # u: 0.975 wu
GRASS_MAX_H = 8.9     # u: 0.347 wu, under the 0.35 wu ceiling
CLEAR_R, CLEAR_Z = 11.0, 4.4    # u: past this radius nothing hangs lower than this (the 0.15 wu footprint band is 3.85 u)


def grounded(fn=None, clear=False, cap_h=None):
    """Stand the parts on z = 0. Without `clear` the lowest vertex of any part is the ground; with it the first part (the
    trunk) is the ground, and drooping tips far from the trunk are held above the footprint band, so a tree's footprint is
    its trunk and not its lowest branch. cap_h (u) scales the whole asset down uniformly if it stands taller."""
    def deco(f):
        def wrap():
            parts = f()
            lo = float((parts[0].mesh.v[:, 2] if clear else np.concatenate([p.mesh.v[:, 2] for p in parts])).min())
            out = []
            for p in parts:
                v = p.mesh.v.astype(np.float64)
                v[:, 2] -= lo
                if clear:
                    far = np.hypot(v[:, 0], v[:, 1]) > CLEAR_R
                    v[far, 2] = np.maximum(v[far, 2], CLEAR_Z)
                out.append(Mesh(v, p.mesh.f, p.mesh.n, p.mesh.uv, p.mesh.tint))
            if not clear:
                floor = min(float(m.v[:, 2].min()) for m in out)
                out = [Mesh(m.v - (0, 0, floor), m.f, m.n, m.uv, m.tint) for m in out]
            top = max(float(m.v[:, 2].max()) for m in out)
            if cap_h and top > cap_h:
                k = cap_h / top
                out = [m.xform((0, 0, 0), (0, 0, 0), k) for m in out]
            return [Part(m, p.mat, p.emit) for m, p in zip(out, parts)]
        wrap.__name__ = f.__name__
        return wrap
    return deco(fn) if fn is not None else deco


def grounded_tree(fn):
    return grounded(clear=True, cap_h=TREE_MAX_H)(fn)


# ---------------------------------------------------------------------------------------------- tree growth
def _grow(acc, r, tag, start, d, length, rad, order, p):
    """One limb on a kinked path, children along it: acc['limb'] thick, acc['twig'] thin and flat shaded."""
    d = N._unit(d)
    nseg = max(2, int(length / p.get("seg", 8.0)))
    pts = N._kinked(r, start, d, length, nseg, p.get("kink", 22) + 5 * order, lift=p.get("lift", 0.4) - 0.10 * order,
                    droop=p.get("droop", 0.5) + 0.35 * order)
    broken = order >= 1 and r.random() < p.get("broken", 0.35)
    thin = rad < 1.25
    m = N.limb(pts, rad, rad * (0.38 if broken else 0.12) + 0.07, f"{tag}", ridges=0, knot=0.18 if rad > 1.2 else 0.0,
               twist=0.03, step=p.get("step", 2.4), taper=0.85 if broken else 1.1, ragged=2.0 if broken else 0.0, flat=thin)
    (acc["twig"] if (thin or order >= p.get("twig_order", 3)) else acc["limb"]).append(m)
    if order >= p.get("max_order", 3) or (broken and r.random() < 0.6):
        return
    kids = p.get("kids", (3, 2, 2, 1))[min(order, 3)]
    if kids <= 0:
        return
    fr = np.sort(0.30 + 0.62 * r.random(kids))
    ang0 = r.random() * TAU
    q = N._poly(pts, 1.6)
    for j, f in enumerate(fr):
        k = min(int(f * (len(q) - 1)), len(q) - 2)
        t = N._unit(q[k + 1] - q[k])
        side = N._rot(N._perp(t, r), t, ang0 + j * 2.4)
        spread = math.radians(40 + 36 * r.random())
        cd = N._unit(t * math.cos(spread) + side * math.sin(spread) + np.array((0, 0, 0.12)))
        crad = rad * (1 - 0.5 * f) * (0.52 + 0.2 * r.random()) + 0.08
        clen = length * (0.58 + 0.3 * r.random()) * (1 - 0.3 * f)
        if crad < 0.3 or clen < 5:
            continue
        _grow(acc, r, f"{tag}.{j}", q[k], cd, clen, crad, order + 1, p)


def _tree_acc():
    return {"limb": [], "twig": []}


def _parts(trunk, acc, trunk_mat, limb_mat, twig_mat, extra=()):
    out = [Part(cat(trunk) if isinstance(trunk, list) else trunk, trunk_mat)]
    for key, mat in (("limb", limb_mat), ("twig", twig_mat)):
        ms = acc[key]
        for i in range(0, len(ms), 8):
            out.append(Part(cat(ms[i:i + 8]), mat))
    return out + list(extra)


def _trunk(pts, r0, r1, tag, ridges=3, **kw):
    kw.setdefault("flare", 3.5)
    kw.setdefault("flare_len", 5.0)
    return N.limb(np.asarray(pts, dtype=np.float64), r0, r1, f"gf.{tag}", ridges=ridges, amp=kw.pop("amp", 0.4), knot=kw.pop("knot", 0.10),
                  twist=kw.pop("twist", 0.02), step=kw.pop("step", 2.2), taper=kw.pop("taper", 0.8), **kw)


def _az(deg, up):
    a = math.radians(deg)
    return (math.cos(a), math.sin(a), up)


# ---------------------------------------------------------------------------------------------- trees
@grounded_tree
def tree_gallows_oak():
    r = U.rng("ground.flora.tree_gallows_oak")
    acc = _tree_acc()
    tp = np.array(((0, 0, 0), (1.5, 0.8, 24), (-1.0, 2.0, 48), (0.0, 1.0, 68), (2.0, 0.0, 84)), dtype=np.float64)
    trunk = _trunk(tp, 7.2, 3.6, "oak.trunk", ridges=3)
    # the gallows arm: a long limb reaching out nearly level, with a rope and a noose hanging from it
    arm_pts = np.array(((1, 1, 62), (12, 1.5, 66), (26, 0.5, 69), (40, 2.0, 68.5), (52, 1.0, 66)), dtype=np.float64)
    arm = N.limb(arm_pts, 3.6, 1.1, "gf.oak.arm", ridges=0, knot=0.2, step=2.2, taper=0.9)
    acc["limb"].append(arm)
    for i, (st, dr, ln, rd) in enumerate(((tp[3], _az(200, 0.5), 30, 3.0), (tp[2], _az(120, 0.35), 24, 2.6), (tp[4], _az(60, 1.0), 22, 2.6),
                                          (tp[4], _az(300, 0.9), 20, 2.2), (tp[3], _az(250, 0.8), 18, 2.0))):
        _grow(acc, r, f"oak.m{i}", st, dr, ln, rd, 1, dict(max_order=2, kids=(0, 2, 1, 0), broken=0.5, twig_order=2))
    rope = I.tube([(34, 1.4, 68), (34.2, 1.4, 56), (33.8, 1.5, 48), (34, 1.4, 41)], [0.5, 0.5, 0.5, 0.5], sides=5, seg_u=3.0)
    noose = I.torus(2.4, 0.45, 5, 10).xform((90, 0, 0), (34, 1.4, 37.6))
    knot = I.torus(0.8, 0.8, 5, 8).xform((0, 0, 0), (34, 1.4, 41))
    flare = [N.limb(np.array(((c * 3.0, s * 3.0, 10), (c * 8, s * 8, 3.6), (c * 11, s * 11, 0.4))), 2.8, 0.6, f"gf.oak.root{i}", ridges=0, knot=0.1, step=2.0)
             for i, (c, s) in enumerate((math.cos(TAU * k / 5 + 0.3), math.sin(TAU * k / 5 + 0.3)) for k in range(5))]
    return _parts([trunk], acc, BARK, BARK_ASH, TWIG, extra=[Part(cat(flare), BARK), Part(cat([rope, noose, knot]), ROPE)])


@grounded_tree
def tree_hollow_elm():
    r = U.rng("ground.flora.tree_hollow_elm")
    acc = _tree_acc()
    tp = np.array(((0, 0, 0), (0.6, 0.4, 18), (-0.4, 0.8, 34), (0.2, 0.2, 48)), dtype=np.float64)
    trunk = _trunk(tp, 9.0, 6.0, "elm.trunk", ridges=4, amp=0.45, flare=4.5)
    for i, (az, up, ln, rd) in enumerate(((20, 1.1, 46, 4.4), (110, 1.0, 42, 4.0), (200, 1.2, 44, 4.2), (290, 1.0, 40, 3.8), (60, 0.5, 26, 2.8))):
        _grow(acc, r, f"elm.m{i}", tp[-1] - (0, 0, 4 if i < 4 else 14), _az(az, up), ln, rd, 1,
              dict(max_order=3, kids=(0, 2, 2, 0), broken=0.4, twig_order=2, step=3.0))
    # the hollow: a black scoop in the front of the trunk, with a splintered lip
    hole = _fix(I.convex("gf.elm.hole", 5.0, 3, (0.75, 0.28, 1.7))).xform((0, 0, 0), (0, 7.1, 21))
    lip = _fix(I.convex("gf.elm.lip", 6.2, 3, (0.95, 0.24, 1.95))).xform((0, 0, 0), (0, 6.6, 21))
    rootl = [N.limb(np.array(((c * 4, s * 4, 12), (c * 10, s * 10, 3.4), (c * 14, s * 14, 0.5))), 3.2, 0.7, f"gf.elm.root{i}", ridges=0, knot=0.1, step=2.0)
             for i, (c, s) in enumerate((math.cos(TAU * k / 5 + 1.0), math.sin(TAU * k / 5 + 1.0)) for k in range(5))]
    return _parts([trunk], acc, BARK, BARK_ASH, TWIG, extra=[Part(lip, BARK_ASH), Part(hole, HOLLOW), Part(cat(rootl), BARK)])


@grounded_tree
def tree_blasted_pine():
    r = U.rng("ground.flora.tree_blasted_pine")
    acc = _tree_acc()
    tp = np.array(((0, 0, 0), (0.5, 0.2, 28), (-0.6, 0.6, 56), (0.2, 0.0, 84), (0.6, 0.4, 104)), dtype=np.float64)
    trunk = _trunk(tp, 5.4, 1.5, "pine.trunk", ridges=3, amp=0.45, ragged=7.0, flare=2.5, taper=0.7, knot=0.05)
    for i in range(11):
        z = 24 + i * 7.2 + 3 * r.random()
        ln = (26 - 1.6 * i) * (0.7 + 0.5 * r.random())
        _grow(acc, r, f"pine.b{i}", (0.2 * math.cos(i), 0.2 * math.sin(i), z), _az(i * 137.5, 0.05 - 0.02 * (i > 5)), ln, 1.9 - 0.08 * i, 1,
              dict(max_order=1, kids=(0, 1, 0, 0), broken=0.7, droop=1.4, kink=14, seg=7.0, step=2.6))
    scar = N.limb(np.array(((3.6, 1.0, 6), (3.8, 3.8, 34), (2.2, 4.6, 70))), 0.9, 0.5, "gf.pine.scar", ridges=0, step=2.4, cap=False)
    return _parts([trunk], acc, CHAR, DEAD, TWIG, extra=[Part(scar, BARK_MARK)])


@grounded_tree
def tree_weeping_ash():
    r = U.rng("ground.flora.tree_weeping_ash")
    acc = _tree_acc()
    tp = np.array(((0, 0, 0), (1.0, 0.5, 20), (-0.6, 1.2, 40), (0.4, 0.4, 60)), dtype=np.float64)
    trunk = _trunk(tp, 6.0, 3.0, "wash.trunk", ridges=3, flare=3.2)
    strands = []
    for i, az in enumerate((10, 70, 130, 190, 250, 310)):
        st = tp[-1] - (0, 0, 6 + 9 * (i % 3))
        pts = N._kinked(r, st, _az(az, 0.9 - 0.2 * (i % 3)), 30, 4, 18, lift=0.3, droop=2.6)
        m = N.limb(pts, 2.0, 0.4, f"gf.wash.arc{i}", ridges=0, knot=0.1, step=2.4, taper=1.0)
        acc["limb"].append(m)
        q = N._poly(pts, 2.4)
        for j in range(4):
            k = min(len(q) - 1, 3 + j * max(1, (len(q) - 4) // 3))
            ln = 24 + 12 * r.random() - (3 if j == 0 else 0)
            sp = N._kinked(r, q[k], (0.25 * (r.random() - 0.5), 0.25 * (r.random() - 0.5), -1), ln, 4, 10, lift=0.0, droop=0.5)
            strands.append(N.limb(sp, 0.42, 0.12, f"gf.wash.s{i}.{j}", ridges=0, step=3.6, taper=1.0, flat=True, cap=False))
    for i in range(3):
        _grow(acc, r, f"wash.up{i}", tp[-1], _az(i * 120 + 40, 2.2), 22, 2.4, 1, dict(max_order=1, kids=(0, 2, 0, 0), broken=0.6))
    return _parts([trunk], acc, BARK_ASH, BARK, TWIG_PALE, extra=[Part(cat(strands), TWIG_PALE)])


@grounded_tree
def tree_twisted_yew():
    r = U.rng("ground.flora.tree_twisted_yew")
    acc = _tree_acc()
    strands = []
    for k in range(3):
        ph = TAU * k / 3
        pts = []
        for i in range(8):
            z = i * 9.0
            rr = 4.0 * (1.0 - 0.07 * i) + 0.5 * math.sin(i * 1.3 + k)
            a = ph + i * 0.55
            pts.append((rr * math.cos(a), rr * math.sin(a), z))
        strands.append(N.limb(np.array(pts), 4.4, 2.1, f"gf.yew.strand{k}", ridges=2, amp=0.5, knot=0.2, flare=2.0, flare_len=4.0, twist=0.03, step=2.2,
                              taper=0.8))
    top = np.array((0.0, 0.0, 62.0))
    for i, (az, up, ln, rd) in enumerate(((10, 0.5, 34, 3.0), (130, 0.3, 30, 2.8), (250, 0.6, 32, 2.8), (70, 1.4, 22, 2.2), (190, 1.2, 20, 2.0))):
        _grow(acc, r, f"yew.m{i}", top - (0, 0, 6 * (i % 3)), _az(az, up), ln, rd, 1, dict(max_order=2, kids=(0, 2, 1, 0), broken=0.45, kink=34, droop=0.9))
    knots = [_fix(I.convex(f"gf.yew.burl{i}", 3.0 + 0.5 * (i % 2), 3, (1.0, 0.8, 0.8))).xform((0, 0, 40 * i), (4.0 * math.cos(1.7 * i), 4.0 * math.sin(1.7 * i), 12 + 11 * i))
             for i in range(4)]
    return _parts([cat(strands)], acc, BARK, BARK_ASH, TWIG, extra=[Part(cat(knots), BARK_ASH)])


@grounded_tree
def tree_split_trunk():
    r = U.rng("ground.flora.tree_split_trunk")
    acc = _tree_acc()
    a = np.array(((-3.0, 0, 0), (-4.0, 0.6, 20), (-6.5, 0.2, 44), (-9.5, 1.0, 66), (-13.0, 0.4, 82)))
    b = np.array(((3.0, 0, 0), (4.5, 0.8, 22), (6.5, 0.2, 46), (9.0, 1.2, 68), (14.0, 1.0, 100)))
    ha = _trunk(a, 5.6, 2.0, "split.a", ridges=3, ragged=8.0, flare=2.0, taper=0.75)
    hb = _trunk(b, 5.6, 2.0, "split.b", ridges=3, ragged=9.0, flare=2.0, taper=0.75)
    core = [N.limb(np.array(((0.2, 0, 4), (0.4, 0.3, 24), (-0.6, 0.2, 46), (-0.4, 0.1, 62))), 2.2, 0.8, "gf.split.core", ridges=0, ragged=4.0, step=2.4, taper=0.9)]
    for i, (st, az, up, ln, rd) in enumerate(((b[3], 20, 0.5, 24, 2.2), (b[2], 160, 0.6, 20, 1.9), (a[3], 200, 0.7, 16, 1.7))):
        _grow(acc, r, f"split.m{i}", st, _az(az, up), ln, rd, 1, dict(max_order=2, kids=(0, 2, 1, 0), broken=0.6))
    chips = [_fix(I.convex(f"gf.split.chip{i}", 2.0, 2, (1.6, 0.5, 0.4))).xform((0, 0, 50 * i), (6 * math.cos(i * 2.3), 6 * math.sin(i * 2.3) + 2, 0.7)) for i in range(5)]
    return _parts([ha, hb], acc, CHAR, BARK_ASH, TWIG, extra=[Part(cat(core), DEAD), Part(cat(chips), DEAD)])


@grounded_tree
def tree_leaning_birch():
    r = U.rng("ground.flora.tree_leaning_birch")
    acc = _tree_acc()
    tp = np.array(((0, 0, 0), (1.5, 0.3, 22), (5.5, 0.6, 46), (11.0, -0.2, 68), (17.5, 0.5, 90)), dtype=np.float64)
    trunk = _trunk(tp, 3.5, 1.1, "birch.trunk", ridges=0, flare=1.6, flare_len=4.0, taper=0.85)
    # the bark's dark bands: thin rings round the trunk, following its lean
    q = N._poly(tp, 1.0)
    rings = []
    for i, z in enumerate((9, 17, 26, 33, 43, 52, 61, 74)):
        k = int(np.argmin(np.abs(q[:, 2] - z)))
        rad = 3.5 * (1 - z / 100) ** 0.8 + 0.4
        rings.append(I.torus(rad, 0.34, 5, 9).xform((0, 0, 0), q[k]))
    for i, (az, up, zf, ln, rd) in enumerate(((190, 0.8, 0.45, 34, 1.5), (20, 0.9, 0.55, 30, 1.4), (110, 0.9, 0.7, 26, 1.2), (290, 1.1, 0.8, 22, 1.1),
                                              (70, 0.7, 0.35, 22, 1.2), (240, 1.4, 0.95, 16, 0.9))):
        k = int(zf * (len(q) - 1))
        _grow(acc, r, f"birch.m{i}", q[k], _az(az, up), ln, rd, 1, dict(max_order=2, kids=(0, 3, 2, 0), twig_order=1, broken=0.3, kink=26, droop=0.9))
    return _parts([trunk], acc, BARK_PALE, BARK_ASH, TWIG, extra=[Part(cat(rings), BARK_MARK)])


@grounded_tree
def tree_dead_spruce():
    r = U.rng("ground.flora.tree_dead_spruce")
    acc = _tree_acc()
    tp = np.array(((0, 0, 0), (0.4, 0.2, 30), (-0.3, 0.5, 60), (0.2, 0.2, 90), (0.0, 0.0, 112)), dtype=np.float64)
    trunk = _trunk(tp, 4.2, 0.5, "spruce.trunk", ridges=3, amp=0.35, flare=2.6, taper=0.9)
    n = 0
    for tier in range(8):
        z = 16 + tier * 11.6
        reach = 30 * (1.0 - tier / 10.5) ** 1.1 + 5
        cnt = 5 if tier < 6 else 4
        for j in range(cnt):
            if r.random() < 0.18:
                continue
            az = j * 360 / cnt + tier * 31 + 14 * r.random()
            _grow(acc, r, f"spruce.t{tier}.{j}", (0, 0, z), _az(az, -0.25 + 0.1 * r.random()), reach * (0.8 + 0.4 * r.random()), 1.35 - 0.08 * tier, 1,
                  dict(max_order=1, kids=(0, 1, 0, 0), broken=0.35, droop=1.8, kink=12, seg=9.0, step=3.4, lift=0.2))
            n += 1
    return _parts([trunk], acc, DEAD, BARK, TWIG)


# ---------------------------------------------------------------------------------------------- saplings
def _sapling(tag, pts, r0, twigs, ragged_top=0.0, kink=20):
    r = U.rng(f"ground.flora.{tag}")
    acc = _tree_acc()
    stem = N.limb(np.asarray(pts, dtype=np.float64), r0, 0.35, f"gf.{tag}.stem", ridges=0, knot=0.2, flare=1.0, flare_len=3.0, step=2.0, taper=0.9,
                  ragged=ragged_top)
    q = N._poly(np.asarray(pts, dtype=np.float64), 2.0)
    for i, (zf, az, up, ln) in enumerate(twigs):
        k = int(zf * (len(q) - 1))
        _grow(acc, r, f"{tag}.t{i}", q[k], _az(az, up), ln, 0.7, 1, dict(max_order=2, kids=(0, 2, 1, 0), broken=0.4, kink=kink, twig_order=1, seg=6.0, step=2.4))
    return [Part(stem, BARK_ASH)] + [Part(cat(acc[k][i:i + 8]), TWIG_PALE) for k in ("limb", "twig") for i in range(0, len(acc[k]), 8)]


@grounded(cap_h=SAPLING_MAX_H)
def sapling_dead_01():
    return _sapling("sapling_dead_01", ((0, 0, 0), (0.4, 0.2, 10), (-0.4, 0.6, 22), (0.3, 0.2, 34)), 1.4,
                    ((0.35, 20, 0.9, 12), (0.5, 140, 0.8, 11), (0.65, 250, 1.0, 10), (0.8, 80, 1.1, 8), (0.9, 200, 1.2, 6)))


@grounded(cap_h=SAPLING_MAX_H)
def sapling_dead_02():
    return _sapling("sapling_dead_02", ((0, 0, 0), (0.8, 0.2, 9), (3.4, 0.4, 17), (8.0, 0.1, 22), (13.0, 0.3, 21)), 1.5,
                    ((0.4, 200, 0.7, 12), (0.6, 100, 0.5, 11), (0.8, 330, 0.3, 10)), kink=26)


@grounded(cap_h=SAPLING_MAX_H)
def sapling_dead_03():
    pts = ((0, 0, 0), (-0.3, 0.2, 8), (0.2, 0.5, 15), (0.0, 0.2, 21))
    base = _sapling("sapling_dead_03", pts, 1.5, ((0.3, 90, 0.4, 10), (0.55, 270, 0.5, 11)), ragged_top=3.0)
    r = U.rng("ground.flora.sapling_dead_03.fork")
    acc = _tree_acc()
    for i, (az, up, ln) in enumerate(((60, 1.3, 15), (230, 1.0, 9))):
        _grow(acc, r, f"sapling_dead_03.f{i}", pts[-1], _az(az, up), ln, 1.0 - 0.2 * i, 1, dict(max_order=2, kids=(0, 2, 1, 0), broken=0.5, twig_order=1, seg=6.0))
    return base + [Part(cat(acc[k][i:i + 8]), TWIG_PALE) for k in ("limb", "twig") for i in range(0, len(acc[k]), 8)]


# ---------------------------------------------------------------------------------------------- bushes
def _cane(r, tag, start, az, length, rad, up=1.2, droop=1.6, kink=14):
    pts = N._kinked(r, start, _az(az, up), length, 4, kink, lift=0.2, droop=droop)
    return pts, N.limb(pts, rad, 0.18, f"gf.{tag}", ridges=0, knot=0.1, step=2.2, taper=1.0, flat=rad < 1.0)


def _thorn_bush(tag, cane_n, leafy, seed_az=0.0, height=24.0):
    r = U.rng(f"ground.flora.{tag}")
    canes, thorns, leaves = [], Acc(), Acc()
    for i in range(cane_n):
        az = seed_az + i * 360 / cane_n + 22 * (r.random() - 0.5)
        pts, m = _cane(r, f"{tag}.c{i}", (0.6 * math.cos(math.radians(az)), 0.6 * math.sin(math.radians(az)), 0), az, height * (1.0 + 0.5 * r.random()),
                       0.95, up=1.5 + 0.8 * r.random(), droop=1.4 + 0.8 * r.random())
        canes.append(m)
        q = N._poly(pts, 4.0)
        for j in range(1, len(q) - 1):
            t = N._unit(q[j + 1] - q[j])
            for sg in (1, -1):
                side = N._unit(np.cross(t, (0, 0, 1.0)) * sg + np.array((0, 0, 0.3)) * r.random())
                thorn_cone(thorns, q[j], side * 0.8 + t * 0.3 + (0, 0, 0.25), 1.6 + 0.7 * r.random(), 0.35)
        if leafy:
            for j in range(2, len(q), 2):
                t = N._unit(q[min(j + 1, len(q) - 1)] - q[j - 1])
                leaf(leaves, q[j], math.radians(az + 80 * (r.random() - 0.5)), math.radians(10 + 40 * r.random()), math.radians(30 * (r.random() - 0.5)),
                     3.6 + 1.6 * r.random(), 2.2 + r.random(), OUT_LANCE, 0.04)
    parts = [Part(cat(canes), BARK), Part(thorns.mesh(), THORN)]
    if leafy:
        parts.append(Part(leaves.mesh(), FOL_DRY))
    return parts


@grounded(cap_h=BUSH_MAX_H)
def bush_thorn_01():
    return _thorn_bush("bush_thorn_01", 9, False, 0.0, 18.0)


@grounded(cap_h=BUSH_MAX_H)
def bush_thorn_02():
    return _thorn_bush("bush_thorn_02", 8, True, 17.0, 17.0)


def _dry_bush(tag, count, height, spread, leaf_n, mat_stem, mat_leaf, kink=30):
    r = U.rng(f"ground.flora.{tag}")
    acc = _tree_acc()
    for i in range(count):
        az = i * 360 / count + 30 * (r.random() - 0.5)
        st = (spread * 0.12 * math.cos(math.radians(az)), spread * 0.12 * math.sin(math.radians(az)), 0)
        _grow(acc, r, f"{tag}.m{i}", st, _az(az, 1.6 + 1.0 * r.random()), height * (0.8 + 0.5 * r.random()), 1.15, 1,
              dict(max_order=2, kids=(0, 2, 1, 0), broken=0.35, kink=kink, droop=1.0, seg=6.0, step=3.4, twig_order=1))
    leaves = Acc()
    if leaf_n:
        for i in range(leaf_n):
            a = TAU * r.random()
            rr = spread * (0.25 + 0.7 * r.random())
            leaf(leaves, (rr * math.cos(a), rr * math.sin(a), height * (0.35 + 0.6 * r.random())), a + 1.2 * (r.random() - 0.5),
                 math.radians(40 * r.random() - 10), math.radians(60 * (r.random() - 0.5)), 3.0 + 1.5 * r.random(), 1.8 + r.random(), OUT_LANCE, 0.05)
    parts = [Part(cat(acc[k][i:i + 8]), mat_stem) for k in ("limb", "twig") for i in range(0, len(acc[k]), 8)]
    if leaf_n:
        parts.append(Part(leaves.mesh(), mat_leaf))
    return parts


@grounded(cap_h=BUSH_MAX_H)
def bush_dry_01():
    return _dry_bush("bush_dry_01", 8, 18.0, 15.0, 36, TWIG_PALE, FOL_DRY)


@grounded(cap_h=BUSH_MAX_H)
def bush_dry_02():
    return _dry_bush("bush_dry_02", 7, 15.0, 12.0, 0, TWIG, FOL_DRY, kink=38)


@grounded(cap_h=BUSH_MAX_H)
def bush_heather():
    r = U.rng("ground.flora.bush_heather")
    stems, buds, leaves = [], Acc(), Acc()
    for i in range(20):
        a = TAU * (i + r.random()) / 20
        rr = 8.0 * math.sqrt(r.random())
        top = 8 + 5 * r.random() * (1.0 - rr / 12)
        out = 1.0 + 3.0 * r.random()
        pts = ((rr * math.cos(a), rr * math.sin(a), 0), (rr * math.cos(a) + out * 0.5 * math.cos(a), rr * math.sin(a) + out * 0.5 * math.sin(a), top * 0.5),
               ((rr + out) * math.cos(a), (rr + out) * math.sin(a), top))
        stems.append(stalk(pts, 0.42, 0.18, f"heather.s{i}", 4.4))
        tip = np.array(pts[-1])
        for j in range(2):
            octa(buds, tip - (0, 0, 1.5 * j) + (0.4 * math.cos(a + j), 0.4 * math.sin(a + j), 0), 0.66 - 0.1 * j, 1.5)
        leaf(leaves, np.array(pts[1]), a, math.radians(50), 0.0, 2.2, 1.0, OUT_LANCE)
    return [Part(cat(stems), TWIG), Part(buds.mesh(), VIOLET), Part(leaves.mesh(), FOL_DARK)]


@grounded(cap_h=BUSH_MAX_H)
def bush_juniper_dead():
    r = U.rng("ground.flora.bush_juniper_dead")
    acc = _tree_acc()
    stem = N.limb(np.array(((0, 0, 0), (1, 0.4, 8), (-1.5, 0.8, 16), (0.5, 0.2, 26))), 2.4, 0.6, "gf.juniper.stem", ridges=0, knot=0.25, flare=1.5, step=2.2,
                  taper=0.9, ragged=3.0)
    for i in range(8):
        zf = 0.12 + 0.1 * i
        _grow(acc, r, f"juniper.b{i}", (0, 0.4, 26 * zf), _az(i * 137.5, 0.6 + 0.4 * (i % 3)), (18 - 1.4 * i) * (0.8 + 0.4 * r.random()), 1.15 - 0.07 * i, 1,
              dict(max_order=2, kids=(0, 2, 2, 0), broken=0.5, kink=34, droop=0.7, seg=6.0, step=2.4, twig_order=1))
    return [Part(stem, DEAD)] + [Part(cat(acc[k][i:i + 8]), DEAD) for k in ("limb", "twig") for i in range(0, len(acc[k]), 8)]


@grounded(cap_h=BUSH_MAX_H)
def bush_nettle():
    r = U.rng("ground.flora.bush_nettle")
    stems, leaves, seeds = [], Acc(), Acc()
    for i in range(9):
        a = TAU * (i + 0.5 * r.random()) / 9
        rr = 5.0 * math.sqrt(r.random()) + 1.0
        x0, y0 = rr * math.cos(a), rr * math.sin(a)
        h = 15 + 8 * r.random()
        lean = 3.0 * (r.random() - 0.3)
        pts = ((x0, y0, 0), (x0 + 0.3 * lean * math.cos(a), y0 + 0.3 * lean * math.sin(a), h * 0.5), (x0 + lean * math.cos(a), y0 + lean * math.sin(a), h))
        stems.append(stalk(pts, 0.40, 0.22, f"nettle.s{i}", 5.0))
        for j in range(3):
            t = 0.32 + 0.24 * j
            c = (x0 + lean * t * math.cos(a), y0 + lean * t * math.sin(a), h * t)
            ln = 6.6 - 1.2 * j
            for sg in (0, math.pi):
                leaf(leaves, c, a + sg + 0.5 * j + 0.3 * (r.random() - 0.5), math.radians(24 - 8 * j + 12 * r.random()), math.radians(12 * (r.random() - 0.5)),
                     ln, ln * 0.62, OUT_SERRATE, 0.03)
        octa(seeds, (x0 + lean * math.cos(a), y0 + lean * math.sin(a), h + 0.6), 0.5, 1.2)
    return [Part(cat(stems), STEM), Part(leaves.mesh(), FOL), Part(seeds.mesh(), FOL_DRY)]


# ---------------------------------------------------------------------------------------------- grass
def _clump(tag, n, radius, height, width, lean, mats, segs=3, curl=0.0, thin_share=0.0, tall_share=0.0, hmax=8.4):
    r = U.rng(f"ground.flora.{tag}")
    accs = [Acc() for _ in mats]
    for i in range(n):
        a = TAU * r.random()
        rr = radius * math.sqrt(r.random())
        base = (rr * math.cos(a), rr * math.sin(a))
        core = 1.0 - 0.45 * rr / max(radius, 1e-6)
        h = min(hmax, height * (0.55 + 0.6 * r.random()) * core)
        w = width * (0.7 + 0.6 * r.random())
        yaw = a + 0.9 * (r.random() - 0.5) + (math.pi if r.random() < 0.12 else 0.0)
        ln = lean * (0.5 + r.random()) + curl * r.random()
        blade(accs[int(r.random() * len(mats))] if len(mats) > 1 else accs[0], base, h, w, yaw, ln, segs)
    return [Part(a.mesh(), m) for a, m in zip(accs, mats) if a]


@grounded(cap_h=GRASS_MAX_H)
def grass_tuft_dry_01():
    return _clump("grass_tuft_dry_01", 54, 5.5, 6.4, 0.95, 0.35, (STRAW, STRAW_PALE))


@grounded(cap_h=GRASS_MAX_H)
def grass_tuft_dry_02():
    return _clump("grass_tuft_dry_02", 56, 7.5, 5.4, 0.85, 0.55, (STRAW, SEDGE), curl=0.3)


@grounded(cap_h=GRASS_MAX_H)
def grass_tuft_dry_03():
    return _clump("grass_tuft_dry_03", 58, 4.4, 7.6, 0.8, 0.22, (STRAW_PALE, STRAW, FOL_DRY))


@grounded(cap_h=GRASS_MAX_H)
def grass_tuft_dry_04():
    r = _clump("grass_tuft_dry_04", 46, 8.5, 4.6, 0.9, 0.7, (STRAW, STRAW_PALE), curl=0.4)
    return r


@grounded(cap_h=GRASS_MAX_H)
def grass_tuft_tall_01():
    return _clump("grass_tuft_tall_01", 58, 4.0, 8.4, 0.75, 0.16, (SEDGE, STRAW), hmax=8.6)


@grounded(cap_h=GRASS_MAX_H)
def grass_tuft_tall_02():
    return _clump("grass_tuft_tall_02", 58, 5.0, 8.2, 0.65, 0.28, (FOL_DRY, SEDGE), hmax=8.6)


@grounded(cap_h=GRASS_MAX_H)
def grass_tuft_tall_03():
    return _clump("grass_tuft_tall_03", 52, 3.6, 8.6, 0.85, 0.12, (STRAW_PALE, SEDGE), hmax=8.8)


@grounded(cap_h=GRASS_MAX_H)
def grass_sedge_01():
    return _clump("grass_sedge_01", 54, 4.2, 7.8, 0.9, 0.85, (SEDGE, FOL_DRY), segs=3, curl=0.55, hmax=8.4)


@grounded(cap_h=GRASS_MAX_H)
def grass_sedge_02():
    return _clump("grass_sedge_02", 48, 5.4, 7.2, 1.1, 0.95, (FOL_DARK, SEDGE, STRAW), segs=3, curl=0.6, hmax=8.4)


@grounded(cap_h=GRASS_MAX_H)
def grass_wiry_01():
    r = U.rng("ground.flora.grass_wiry_01")
    stems, heads = Acc(), Acc()
    for i in range(34):
        a = TAU * r.random()
        rr = 5.0 * math.sqrt(r.random())
        h = 6.0 + 2.6 * r.random()
        lean = 0.2 + 0.5 * r.random()
        yaw = a + 0.6 * (r.random() - 0.5)
        blade(stems, (rr * math.cos(a), rr * math.sin(a)), h, 0.30, yaw, lean, 3)
        tip = (rr * math.cos(a) + math.cos(yaw) * lean * h, rr * math.sin(a) + math.sin(yaw) * lean * h, h * (1 - 0.18 * lean))
        if i % 3 == 0:
            octa(heads, tip, 0.34, 2.4)
    return [Part(stems.mesh(), STRAW_PALE), Part(heads.mesh(), STRAW)]


# ---------------------------------------------------------------------------------------------- flowers
def _plant_stem(r, tag, base, h, lean_az, lean):
    a = math.radians(lean_az)
    pts = (base, (base[0] + math.cos(a) * lean * 0.3, base[1] + math.sin(a) * lean * 0.3, base[2] + h * 0.5),
           (base[0] + math.cos(a) * lean, base[1] + math.sin(a) * lean, base[2] + h))
    return pts, stalk(pts, 0.30, 0.2, tag, 3.2)


def _basal_leaves(acc, r, n, ln, wd, outline=OUT_LANCE, spread=2.0, elev=18):
    for i in range(n):
        a = TAU * (i + r.random() * 0.6) / n
        leaf(acc, (spread * 0.4 * math.cos(a), spread * 0.4 * math.sin(a), 0.2), a, math.radians(elev + 14 * r.random()), math.radians(14 * (r.random() - 0.5)),
             ln * (0.8 + 0.4 * r.random()), wd, outline, 0.05)


@grounded(cap_h=COVER_MAX_H)
def flower_pale_bell():
    r = U.rng("ground.flora.flower_pale_bell")
    stems, bells, lv = [], [], Acc()
    for k, (x, y, h, az) in enumerate(((0.0, 0.0, 9.0, 20), (3.2, 1.4, 7.0, 150))):
        pts, s_ = _plant_stem(r, f"pbell.s{k}", (x, y, 0), h, az, 2.4)
        stems.append(s_)
        for j in range(2):
            bx_, by_, bz_ = pts[2][0], pts[2][1], pts[2][2] - 1.7 * j
            b = I.lathe([(0, 0), (1.3, 0.5), (1.8, 1.5), (1.1, 2.1), (0, 2.5)], 6).xform((180, 0, 0), (bx_ + 0.8 * j * math.cos(az), by_ + 0.8 * j * math.sin(az), bz_ + 0.6))
            bells.append(b)
    _basal_leaves(lv, r, 4, 4.2, 1.5, spread=3.0)
    return [Part(cat(stems), STEM), Part(cat(bells), IVORY), Part(lv.mesh(), FOL_DARK)]


@grounded(cap_h=COVER_MAX_H)
def flower_ash_lily():
    r = U.rng("ground.flora.flower_ash_lily")
    stems, petals, lv = [], Acc(), Acc()
    for k, (x, y, h, az) in enumerate(((0, 0, 9.4, 40), (3.4, -1.0, 6.4, 200))):
        pts, s = _plant_stem(r, f"alily.s{k}", (x, y, 0), h, az, 1.2)
        stems.append(s)
        top = np.array(pts[2])
        for j in range(6):
            a = TAU * j / 6 + 0.3 * k
            leaf(petals, top, a, math.radians(38 - 14 * (j % 2)), math.radians(22), 3.8 - 0.4 * (j % 2), 1.3, OUT_PETAL, 0.12)
        octa(petals, top + (0, 0, 0.3), 0.5, 1.0)
    _basal_leaves(lv, r, 5, 5.0, 0.9, spread=3.0, elev=34)
    return [Part(cat(stems), STEM), Part(petals.mesh(), IVORY), Part(lv.mesh(), FOL_DARK)]


@grounded(cap_h=COVER_MAX_H)
def flower_crowsfoot():
    r = U.rng("ground.flora.flower_crowsfoot")
    stems, petals, lv = [], Acc(), Acc()
    for k, (x, y, h, az) in enumerate(((0, 0, 8.4, 70), (3.0, 2.0, 6.4, 190), (-2.6, -1.6, 5.4, 300))):
        pts, s = _plant_stem(r, f"crow.s{k}", (x, y, 0), h, az, 1.4)
        stems.append(s)
        top = np.array(pts[2])
        for j in range(5):
            a = TAU * j / 5
            leaf(petals, top, a, math.radians(18), math.radians(8), 1.9, 1.0, OUT_ROUND, 0.1)
        octa(petals, top + (0, 0, 0.4), 0.42, 0.9)
    # the crow's-foot leaf: three lobes
    for i in range(5):
        a = TAU * i / 5 + 0.4
        for d in (-0.45, 0.0, 0.45):
            leaf(lv, (2.0 * math.cos(a), 2.0 * math.sin(a), 0.3), a + d, math.radians(16), 0, 3.4, 1.0, OUT_LANCE, 0.04)
    return [Part(cat(stems), STEM), Part(petals.mesh(), OCHRE), Part(lv.mesh(), FOL_DARK)]


@grounded(cap_h=COVER_MAX_H)
def flower_withered_rose():
    r = U.rng("ground.flora.flower_withered_rose")
    pts = ((0, 0, 0), (0.6, 0.4, 4.5), (2.0, 1.2, 8.0), (3.4, 1.6, 8.2))
    stem = N.limb(np.array(pts, dtype=np.float64), 0.42, 0.28, "gf.rose.stem", ridges=0, step=1.6, taper=1.0, flat=True)
    thorns = Acc()
    q = N._poly(np.array(pts, dtype=np.float64), 2.2)
    for j in range(1, len(q) - 2):
        thorn_cone(thorns, q[j], (0.8 * (-1) ** j, 0.6, 0.4), 1.1, 0.22)
    top = np.array(pts[-1]) + (0, 0, 0.4)
    cup = I.lathe([(0, 0), (0.9, 0.1), (1.7, 0.9), (2.0, 1.9), (1.5, 2.6), (1.1, 2.4), (0.9, 1.7), (0, 1.4)], 8).xform((150, 0, 20), top + (0.4, 0.6, 1.4))
    petals = Acc()
    for j in range(7):
        a = TAU * j / 7
        leaf(petals, top + (0.4, 0.5, 0.4), a, math.radians(10 + 10 * (j % 2)), math.radians(40), 1.9, 1.1, OUT_ROUND, 0.2)
    lv = Acc()
    for j, (t, a) in enumerate(((0.3, 2.4), (0.55, 5.2), (0.75, 0.6))):
        c = q[min(int(t * (len(q) - 1)), len(q) - 1)]
        leaf(lv, c, a, math.radians(-25), math.radians(15), 2.8, 1.5, OUT_SERRATE, 0.1)
    fallen = Acc()
    for j in range(3):
        leaf(fallen, (3.0 + 2.2 * j, -2.6 + 1.7 * j, 0.1), 1.0 + 2.1 * j, math.radians(2), 0, 1.5, 0.95, OUT_ROUND)
    return [Part(stem, STEM), Part(thorns.mesh(), THORN), Part(cat([_fix(cup)]), BRUISE), Part(petals.mesh(), BRUISE),
            Part(lv.mesh(), FOL_DARK), Part(fallen.mesh(), BRUISE)]


@grounded(cap_h=COVER_MAX_H)
def flower_hemlock_umbel():
    r = U.rng("ground.flora.flower_hemlock_umbel")
    pts = ((0, 0, 0), (0.3, 0.2, 4.8), (0.1, 0.0, 9.2))
    stem = N.limb(np.array(pts, dtype=np.float64), 0.44, 0.26, "gf.hemlock.stem", ridges=0, step=3.2, taper=0.8, flat=True)
    top = np.array(pts[-1])
    spokes, florets = Acc(), Acc()
    for j in range(9):
        a = TAU * j / 9
        end = top + (math.cos(a) * 3.8, math.sin(a) * 3.8, -0.9 + 0.4 * (j % 2))
        mid = (top + end) / 2 + (0, 0, 0.6)
        spokes.add([top, mid - (0.12 * math.sin(a), -0.12 * math.cos(a), 0), mid + (0.12 * math.sin(a), -0.12 * math.cos(a), 0), end],
                   [(0, 1, 2), (1, 3, 2)], want=(0, 0, 1))
        octa(florets, end + (0, 0, 0.1), 0.55, 0.6)
    octa(florets, top + (0, 0, 0.2), 0.5, 0.9)
    lv = Acc()
    for i in range(3):
        a = TAU * i / 3 + 0.5
        for d in (-0.5, 0.5):
            leaf(lv, (0.8 * math.cos(a), 0.8 * math.sin(a), 0.2), a + d, math.radians(36), 0, 3.8, 0.95, OUT_SERRATE, 0.04)
    marks = Acc()
    for z in (2.0, 4.6):
        octa(marks, (0.4, 0.0, z), 0.34, 1.1)
    return [Part(stem, STEM), Part(spokes.mesh(), STEM), Part(florets.mesh(), IVORY_DIRTY), Part(lv.mesh(), FOL_DARK), Part(marks.mesh(), VIOLET_DARK)]


@grounded(cap_h=COVER_MAX_H)
def flower_thistle():
    r = U.rng("ground.flora.flower_thistle")
    pts = ((0, 0, 0), (0.5, 0.3, 4.4), (1.0, 0.5, 8.0))
    stem = N.limb(np.array(pts, dtype=np.float64), 0.46, 0.3, "gf.thistle.stem", ridges=0, step=1.6, flat=True)
    top = np.array(pts[-1])
    bulb = _fix(I.lathe([(0, 0), (0.9, 0.1), (1.7, 0.8), (1.9, 1.7), (1.3, 2.3), (0, 2.5)], 8)).xform((0, 0, 0), top - (0, 0, 0.5))
    bloom = Acc()
    for j in range(12):
        a = TAU * j / 12
        bloom.add([top + (0.35 * math.cos(a), 0.35 * math.sin(a), 1.4), top + (0.35 * math.cos(a + 0.5), 0.35 * math.sin(a + 0.5), 1.4),
                   top + (1.5 * math.cos(a + 0.25), 1.5 * math.sin(a + 0.25), 3.1 + 0.4 * (j % 2))], [(0, 1, 2)], want=(math.cos(a), math.sin(a), 0.5))
    spikes = Acc()
    for j in range(7):
        a = TAU * j / 7
        thorn_cone(spikes, top + (1.2 * math.cos(a), 1.2 * math.sin(a), 0.5 + 0.3 * (j % 3)), (math.cos(a), math.sin(a), 0.9), 1.4, 0.2)
    lv = Acc()
    for i in range(6):
        a = TAU * i / 6 + 0.3
        leaf(lv, (1.6 * math.cos(a), 1.6 * math.sin(a), 0.3), a, math.radians(22), math.radians(10), 4.2, 1.3, OUT_SERRATE, 0.07)
        thorn_cone(spikes, (2.4 * math.cos(a), 2.4 * math.sin(a), 0.7), (math.cos(a), math.sin(a), 0.7), 1.0, 0.18)
    return [Part(stem, STEM), Part(bulb, FOL_DARK), Part(bloom.mesh(), VIOLET), Part(spikes.mesh(), THORN), Part(lv.mesh(), FOL_DARK)]


# ---------------------------------------------------------------------------------------------- mushrooms
def _shroom(base, stem_h, stem_r, cap_r, cap_h, lean=(0, 0), ss=5, cs=7, flare=1.0, drop=0.0):
    """-> (stem mesh, cap mesh) of one low-poly mushroom standing on `base`; the cap sits over the stem top."""
    st = I.lathe([(0, 0), (stem_r * 1.5 * flare, 0), (stem_r, stem_h * 0.5), (stem_r * 0.9, stem_h)], ss)
    cap_prof = [(0, 0), (cap_r * 0.9, cap_h * 0.1 - drop), (cap_r, cap_h * 0.4), (cap_r * 0.62, cap_h * 0.85), (0, cap_h)]
    cap = I.lathe(cap_prof, cs).xform((lean[0], lean[1], 0), (0, 0, stem_h - 0.15 * cap_h))
    return (_fix(st).xform((lean[0] * 0.6, lean[1] * 0.6, 0), tuple(base)), _fix(cap).xform((0, 0, 0), tuple(base)))


@grounded(cap_h=COVER_MAX_H)
def shroom_inkcap_cluster():
    r = U.rng("ground.flora.shroom_inkcap_cluster")
    stems, caps = [], []
    for i, (x, y, h, cr) in enumerate(((0, 0, 7.4, 1.7), (2.4, 1.2, 6.2, 1.5), (-2.2, 1.4, 5.0, 1.4), (0.8, -2.4, 3.6, 1.2))):
        s_, c = _shroom((x, y, 0), h, 0.42, cr, cr * 2.3, lean=(6 * (r.random() - 0.5), 6 * (r.random() - 0.5)), ss=5, cs=6)
        stems.append(s_)
        caps.append(c)
    return [Part(cat(stems), IVORY), Part(cat(caps), CAP_INK)]


@grounded(cap_h=COVER_MAX_H)
def shroom_pale_toad():
    r = U.rng("ground.flora.shroom_pale_toad")
    stems, caps = [], []
    for i, (x, y, h, cr) in enumerate(((0, 0, 4.8, 3.4), (4.6, 1.4, 3.4, 2.4), (-3.8, 3.2, 3.0, 2.0))):
        s_, c = _shroom((x, y, 0), h, 0.9 - 0.15 * i, cr, cr * 0.55, lean=(4 * (r.random() - 0.5), 4 * (r.random() - 0.5)), ss=6, cs=8, flare=1.7)
        stems.append(s_)
        caps.append(c.displace(f"gf.toad.{i}", 0.14, 1.2, 2.4))
    return [Part(cat(stems), IVORY), Part(cat(caps), CAP_TOAD)]


@grounded(cap_h=COVER_MAX_H)
def shroom_grave_cap():
    r = U.rng("ground.flora.shroom_grave_cap")
    stems, caps = [], []
    for i, (x, y, h, cr) in enumerate(((0, 0, 6.0, 2.4), (3.4, 1.6, 4.2, 1.9), (-2.6, 2.2, 5.0, 1.8), (1.2, -3.2, 3.4, 1.5))):
        s_, c = _shroom((x, y, 0), h, 0.5, cr, cr * 1.5, lean=(8 * (r.random() - 0.5), 8 * (r.random() - 0.5)), ss=5, cs=7, drop=0.3)
        stems.append(s_)
        caps.append(c)
    return [Part(cat(stems), IVORY_DIRTY), Part(cat(caps), CAP_UMBER)]


@grounded(cap_h=COVER_MAX_H)
def shroom_puffball():
    balls = []
    spec = ((0, 0, 3.4), (4.4, 1.0, 2.6), (-3.6, 2.4, 2.2), (1.4, -3.8, 1.6))
    for i, (x, y, rad) in enumerate(spec):
        ts = np.linspace(0.3, math.pi * (0.97 if i else 0.78), 5)
        prof = [(0, 0)] + [(rad * math.sin(t), rad * (1 - math.cos(t)) * 0.95) for t in ts]
        if i == 0:
            # the old one has burst: an open crown with a torn lip
            prof = prof[:-1] + [(prof[-1][0] * 0.7, prof[-1][1] - 0.5)]
        b = _fix(I.lathe(prof, 7)).xform((0, 0, 0), (x, y, 0)).displace(f"gf.puff.{i}", 0.06, 1.0, 2.2)
        balls.append(b)
    return [Part(cat(balls), IVORY)]


@grounded(cap_h=COVER_MAX_H)
def shroom_bracket_stub():
    r = U.rng("ground.flora.shroom_bracket_stub")
    stub = N.limb(np.array(((0, 0, 0), (0.3, 0.2, 3.2), (0.0, 0.0, 6.8))), 4.2, 3.2, "gf.bracket.stub", ridges=2, amp=0.4, flare=1.4, flare_len=2.5, step=1.6, taper=0.8,
                  ragged=2.4)
    shelves = []
    for i, (z, a, rad) in enumerate(((1.4, 20, 3.0), (3.0, 110, 2.5), (4.4, 235, 2.0), (2.2, 300, 1.7))):
        c = (4.0 * math.cos(math.radians(a)), 4.0 * math.sin(math.radians(a)), z)
        sh = _fix(I.convex(f"gf.bracket.shelf{i}", rad, 3, (1.0, 1.0, 0.28))).xform((0, 0, a), c)
        shelves.append(sh)
    return [Part(stub, DEAD), Part(cat(shelves), SHELF)]


@grounded(cap_h=COVER_MAX_H)
def shroom_ring():
    r = U.rng("ground.flora.shroom_ring")
    stems, caps = [], []
    for i in range(7):
        a = TAU * (i + 0.25 * (r.random() - 0.5)) / 7
        rr = 9.0 + 1.4 * (r.random() - 0.5)
        h = 2.4 + 1.4 * r.random()
        s_, c = _shroom((rr * math.cos(a), rr * math.sin(a), 0), h, 0.34, 1.2 + 0.4 * r.random(), 1.0, lean=(4 * (r.random() - 0.5), 4 * (r.random() - 0.5)), ss=4, cs=5)
        stems.append(s_)
        caps.append(c)
    return [Part(cat(stems), IVORY), Part(cat(caps), CAP_GREY)]


# ---------------------------------------------------------------------------------------------- specs
def _spec(fn, cls, layer, atlas, disc=(0.0, 0.0), ground=("grass", "dirt", "mud"), weight=1.0, scale=(0.9, 1.1), weld=True, age=(0.40, 0.60, 0.0, 0.10)):
    return K.GroundSpec(fn, cls, layer, atlas, disc, weld, age, ground, weight, scale)


def _tree(fn, disc, weight=1.0, ground=("grass", "dirt", "mud")):
    return _spec(fn, "tree", "landmark", 512, (disc, disc), ground, weight, (0.85, 1.15), age=(0.50, 0.60, 0.0, 0.06))


def _cover(fn, cls, ground, weight=1.0, scale=(0.8, 1.25), age=(0.20, 0.40, 0.0, 0.08)):
    return _spec(fn, cls, "cover", 128, ground=ground, weight=weight, scale=scale, age=age)


ASSETS: dict[str, K.GroundSpec] = {
    "tree_gallows_oak": _tree(tree_gallows_oak, 13.0, 1.0),
    "tree_hollow_elm": _tree(tree_hollow_elm, 14.0, 1.0),
    "tree_blasted_pine": _tree(tree_blasted_pine, 10.0, 1.0, ("grass", "dirt", "mud", "snow", "rock")),
    "tree_weeping_ash": _tree(tree_weeping_ash, 13.0, 1.0, ("grass", "mud")),
    "tree_twisted_yew": _tree(tree_twisted_yew, 11.0, 1.0),
    "tree_split_trunk": _tree(tree_split_trunk, 13.0, 0.8),
    "tree_leaning_birch": _tree(tree_leaning_birch, 8.0, 1.0, ("grass", "dirt")),
    "tree_dead_spruce": _tree(tree_dead_spruce, 11.0, 1.0, ("grass", "dirt", "mud", "snow")),
    "sapling_dead_01": _spec(sapling_dead_01, "sapling", "accent", 256, (6.0, 6.0), scale=(0.85, 1.2)),
    "sapling_dead_02": _spec(sapling_dead_02, "sapling", "accent", 256, (7.0, 6.0), scale=(0.85, 1.2)),
    "sapling_dead_03": _spec(sapling_dead_03, "sapling", "accent", 256, (6.0, 6.0), scale=(0.85, 1.2)),
    "bush_thorn_01": _spec(bush_thorn_01, "bush", "accent", 256, (16.0, 16.0), ("grass", "dirt"), 1.0, (0.85, 1.2)),
    "bush_thorn_02": _spec(bush_thorn_02, "bush", "accent", 256, (15.0, 15.0), ("grass", "dirt", "mud"), 1.0, (0.85, 1.2)),
    "bush_dry_01": _spec(bush_dry_01, "bush", "accent", 256, (14.0, 14.0), ("grass", "dirt", "sand"), 1.0, (0.85, 1.2)),
    "bush_dry_02": _spec(bush_dry_02, "bush", "accent", 256, (12.0, 12.0), ("dirt", "sand", "grass"), 1.0, (0.85, 1.2)),
    "bush_heather": _spec(bush_heather, "bush", "accent", 256, (11.0, 11.0), ("grass", "dirt", "rock"), 0.9, (0.85, 1.2)),
    "bush_juniper_dead": _spec(bush_juniper_dead, "bush", "accent", 256, (11.0, 11.0), ("grass", "dirt", "rock", "snow"), 0.8, (0.85, 1.2)),
    "bush_nettle": _spec(bush_nettle, "bush", "accent", 256, (9.0, 9.0), ("grass", "mud"), 0.9, (0.85, 1.2)),
    "grass_tuft_dry_01": _cover(grass_tuft_dry_01, "grass", ("grass", "dirt"), 1.2),
    "grass_tuft_dry_02": _cover(grass_tuft_dry_02, "grass", ("grass", "dirt", "sand"), 1.2),
    "grass_tuft_dry_03": _cover(grass_tuft_dry_03, "grass", ("grass", "dirt"), 1.0),
    "grass_tuft_dry_04": _cover(grass_tuft_dry_04, "grass", ("grass", "dirt", "sand", "mud"), 1.0),
    "grass_tuft_tall_01": _cover(grass_tuft_tall_01, "grass", ("grass", "mud"), 1.0),
    "grass_tuft_tall_02": _cover(grass_tuft_tall_02, "grass", ("grass", "mud"), 1.0),
    "grass_tuft_tall_03": _cover(grass_tuft_tall_03, "grass", ("grass", "dirt"), 0.8),
    "grass_sedge_01": _cover(grass_sedge_01, "grass", ("mud", "grass"), 1.0),
    "grass_sedge_02": _cover(grass_sedge_02, "grass", ("mud", "grass"), 1.0),
    "grass_wiry_01": _cover(grass_wiry_01, "grass", ("dirt", "grass", "sand", "rock"), 1.0),
    "flower_pale_bell": _cover(flower_pale_bell, "flower", ("grass",), 0.5),
    "flower_ash_lily": _cover(flower_ash_lily, "flower", ("grass", "mud"), 0.5),
    "flower_crowsfoot": _cover(flower_crowsfoot, "flower", ("grass", "dirt"), 0.5),
    "flower_withered_rose": _cover(flower_withered_rose, "flower", ("grass",), 0.4),
    "flower_hemlock_umbel": _cover(flower_hemlock_umbel, "flower", ("grass", "mud"), 0.5),
    "flower_thistle": _cover(flower_thistle, "flower", ("grass", "dirt"), 0.5),
    "shroom_inkcap_cluster": _cover(shroom_inkcap_cluster, "mushroom", ("mud", "grass"), 0.7),
    "shroom_pale_toad": _cover(shroom_pale_toad, "mushroom", ("grass", "mud"), 0.7),
    "shroom_grave_cap": _cover(shroom_grave_cap, "mushroom", ("grass", "dirt", "mud"), 0.7),
    "shroom_puffball": _cover(shroom_puffball, "mushroom", ("grass", "dirt"), 0.6),
    "shroom_bracket_stub": _cover(shroom_bracket_stub, "mushroom", ("grass", "mud", "dirt"), 0.5),
    "shroom_ring": _cover(shroom_ring, "mushroom", ("grass",), 0.3, scale=(0.9, 1.2)),
}


def render(only: list[str] | None = None):
    """-> (entries, files, assets) in spec order."""
    unknown = [s for s in (only or []) if s not in ASSETS]
    if unknown:
        raise SystemExit(f"unknown flora: {unknown}")
    entries, files, built = [], {}, {}
    for stem, spec in ASSETS.items():
        if only and stem not in only:
            continue
        a = K.build_ground_asset(FAMILY, stem, spec)
        data = K.glb_bytes(a)
        entries.append(K.manifest_entry(FAMILY, stem, spec, a["bounds"]))
        files[stem] = data
        built[stem] = a
    return entries, files, built


def main(out_root: Path = K.GROUND_ROOT, only: list[str] | None = None) -> Path:
    import hashlib
    entries, files, built = render(only)
    K.write_family(Path(out_root), FAMILY, entries, files, GENERATOR, partial=bool(only))
    for e in entries:
        stem = e["id"].split(".", 1)[1]
        a = built[stem]
        tris = sum(len(p.indices) for p in a["primitives"])
        print(f"gen_ground_flora: {stem:24s} {e['class']:9s} tris={tris:6d} size={len(files[stem]):7d} r={e['radius']:.2f} fp={e['footprint']:.2f} "
              f"h={e['height']:.2f} sha1={hashlib.sha1(files[stem]).hexdigest()[:10]}")
    return Path(out_root)

# ---------------------------------------------------------------------------------------------- preview sheet
PREVIEW_PATH = Path(__file__).resolve().parent / "previews" / "ground_flora.png"
_PX_PER_WU = {"tree": 40.0, "sapling": 100.0, "bush": 100.0}      # everything else (cover) is drawn at 190
_CELL, _COLS, _SS = 192, 8, 2


def _draw(img, zbuf, pos, nrm, uv, idx, tex, gain, yaw, pitch, scale, origin, alpha_cut=None):
    """Orthographic z-buffered triangles, nearest texel, one directional light and an ambient; the albedo is linear, as the
    atlas bytes are, so the same gain-then-gamma the engine's actor shader roughly applies is used here."""
    cy, sy = math.cos(yaw), math.sin(yaw)
    cp, sp = math.cos(pitch), math.sin(pitch)
    x = pos[:, 0] * cy - pos[:, 2] * sy
    z = pos[:, 0] * sy + pos[:, 2] * cy
    up = pos[:, 1] * cp - z * sp
    near = pos[:, 1] * sp + z * cp
    sx = origin[0] + x * scale
    sy_ = origin[1] - up * scale
    n = nrm.astype(np.float64)
    nx = n[:, 0] * cy - n[:, 2] * sy
    nz = n[:, 0] * sy + n[:, 2] * cy
    nview = np.stack((nx, n[:, 1], nz), axis=1)
    light = np.array((-0.45, 0.80, 0.40))
    light = light / np.linalg.norm(light)
    size = tex.shape[0]
    H, W = zbuf.shape
    for a, b, c in idx.astype(np.int64):
        xs, ys = sx[[a, b, c]], sy_[[a, b, c]]
        x0, x1 = int(max(math.floor(xs.min()), 0)), int(min(math.ceil(xs.max()), W - 1))
        y0, y1 = int(max(math.floor(ys.min()), 0)), int(min(math.ceil(ys.max()), H - 1))
        if x1 < x0 or y1 < y0:
            continue
        den = (ys[1] - ys[2]) * (xs[0] - xs[2]) + (xs[2] - xs[1]) * (ys[0] - ys[2])
        if abs(den) < 1e-9:
            continue
        gx, gy = np.meshgrid(np.arange(x0, x1 + 1) + 0.5, np.arange(y0, y1 + 1) + 0.5)
        l0 = ((ys[1] - ys[2]) * (gx - xs[2]) + (xs[2] - xs[1]) * (gy - ys[2])) / den
        l1 = ((ys[2] - ys[0]) * (gx - xs[2]) + (xs[0] - xs[2]) * (gy - ys[2])) / den
        l2 = 1.0 - l0 - l1
        inside = (l0 >= -0.02) & (l1 >= -0.02) & (l2 >= -0.02)
        if not inside.any():
            continue
        d = l0 * near[a] + l1 * near[b] + l2 * near[c]
        sub = zbuf[y0:y1 + 1, x0:x1 + 1]
        ok = inside & (d > sub)
        if not ok.any():
            continue
        u = l0 * uv[a, 0] + l1 * uv[b, 0] + l2 * uv[c, 0]
        v = l0 * uv[a, 1] + l1 * uv[b, 1] + l2 * uv[c, 1]
        tx = np.clip((u * size).astype(np.int64), 0, size - 1)
        ty = np.clip((v * size).astype(np.int64), 0, size - 1)
        texel = tex[ty, tx]
        if alpha_cut is not None:
            ok &= texel[..., 3] >= alpha_cut
            if not ok.any():
                continue
        nn = l0[..., None] * nview[a] + l1[..., None] * nview[b] + l2[..., None] * nview[c]
        nn = nn / np.maximum(np.linalg.norm(nn, axis=-1, keepdims=True), 1e-9)
        # double sided: the face that looks at the camera is lit
        toward = nn[..., 2] * cp + nn[..., 1] * sp
        nn = np.where((toward < 0)[..., None], -nn, nn)
        lam = np.clip(nn @ light, 0.0, 1.0)
        shade = 0.55 + 0.85 * lam
        rgb = (texel[..., :3].astype(np.float64) / 255.0 * gain) * shade[..., None]
        sub[ok] = d[ok]
        img[y0:y1 + 1, x0:x1 + 1][ok] = rgb[ok]


def preview(built: dict | None = None):
    """-> an RGBA sheet of all forty assets, in spec order, from the baked atlases (what the engine samples)."""
    if built is None:
        built = render()[2]
    rows = -(-len(ASSETS) // _COLS)
    sheet = np.zeros((rows * _CELL, _COLS * _CELL, 3), dtype=np.float64)
    sheet[:] = (0.020, 0.019, 0.017)
    for i, stem in enumerate(ASSETS):
        a = built[stem]
        scale = _PX_PER_WU.get(a["spec"].cls, 190.0) * _SS
        S = _CELL * _SS
        img = np.zeros((S, S, 3), dtype=np.float64)
        img[:] = (0.020, 0.019, 0.017)
        zbuf = np.full((S, S), -1e9)
        origin = (S / 2, S * 0.80)
        for prim in a["primitives"]:
            mat = a["materials"][prim.material]
            if mat["name"] == K.CONTACT_MATERIAL:
                t = a["disc"]
                _draw(img, zbuf, prim.positions.astype(np.float64), prim.normals, prim.uvs, prim.indices, t, GP.DISC_LIFT, math.radians(35), math.radians(50),
                      scale, origin, alpha_cut=128)
                continue
            t = a["encoded"]
            _draw(img, zbuf, prim.positions.astype(np.float64), prim.normals, prim.uvs, prim.indices, t, a["scale"], math.radians(35), math.radians(50), scale, origin)
        lit = np.clip(img, 0.0, 1.0)
        cell = lit.reshape(_CELL, _SS, _CELL, _SS, 3).mean(axis=(1, 3))
        r0, c0 = (i // _COLS) * _CELL, (i % _COLS) * _CELL
        sheet[r0:r0 + _CELL, c0:c0 + _CELL] = cell
        # a 1.2 wu bar (the player) at the cell's left edge, so the scale of every cell can be read
        bar = int(1.2 * _PX_PER_WU.get(a["spec"].cls, 190.0) * 0.64)
        sheet[r0 + int(_CELL * 0.80) - bar:r0 + int(_CELL * 0.80), c0 + 6:c0 + 8] = (0.18, 0.17, 0.15)
    out = np.zeros((*sheet.shape[:2], 4), dtype=np.uint8)
    out[..., :3] = np.round(np.clip(sheet, 0, 1) ** (1 / 2.2) * 255).astype(np.uint8)
    out[..., 3] = 255
    return out



if __name__ == "__main__":
    args = [x for x in sys.argv[1:] if not x.startswith("--")]
    only_arg = None
    if "--only" in sys.argv:
        only_arg = sys.argv[sys.argv.index("--only") + 1].split(",")
        args = [x for x in args if x != sys.argv[sys.argv.index("--only") + 1]]
    if "--preview" in sys.argv:
        U.save_png(preview(), PREVIEW_PATH)
        print(f"gen_ground_flora: wrote {PREVIEW_PATH}")
    else:
        main(Path(args[0]) if args else K.GROUND_ROOT, only_arg)
