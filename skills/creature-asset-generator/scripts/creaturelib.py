"""
creaturelib - SDF-sculpted, game-ready creature assets exported as skinned glTF 2.0 (.glb).

Pipeline
  1. Sculpt: a creature is an ordered list of signed-distance primitives (ellipsoids,
     round cones, spheres, tori) combined with smooth union / smooth subtraction.
  2. Polygonize: marching cubes on a fine grid -> high-res surface (the "sculpt").
  3. Retopo-lite: quadric decimation to a game budget, vertices re-projected onto the SDF.
  4. UV: xatlas automatic unwrap + packing.
  5. Bake (per texel, straight from the analytic SDF, i.e. from the "high poly"):
       - tangent-space normal map (true sculpt normal + procedural fur-strand detail)
       - albedo (design-supplied shading rules: regions, fur flow, agouti speckle ...)
       - occlusion (SDF ambient occlusion), roughness  -> packed ORM
  6. Rig: skin weights from a soft-min over primitives (each primitive belongs to a bone).
  7. Export: one skinned mesh with several primitives (fur, eyes, whiskers, ...),
     embedded textures, KHR_materials_sheen for fur, and keyframed animations.

Deps: numpy, scipy, pillow, scikit-image, fast-simplification, xatlas
"""
import io
import json
import math
import struct
import time

import numpy as np
from PIL import Image
from scipy import ndimage

# ----------------------------------------------------------------------------- math

def nrm(v):
    v = np.asarray(v, float)
    return v / max(np.linalg.norm(v), 1e-12)


def nrm_rows(a):
    return a / np.maximum(np.linalg.norm(a, axis=-1, keepdims=True), 1e-12)


def basis(primary, hint):
    """Orthonormal rotation matrix (columns = local x, y, z). local y = primary,
    local z = hint orthogonalised (e.g. thickness / opening direction)."""
    y = nrm(primary)
    z = np.asarray(hint, float)
    z = nrm(z - y * np.dot(z, y))
    x = np.cross(y, z)
    return np.stack([x, y, z], 1)


def rot_x(a):
    c, s = math.cos(a), math.sin(a)
    return np.array([[1, 0, 0], [0, c, -s], [0, s, c]], float)


def rot_y(a):
    c, s = math.cos(a), math.sin(a)
    return np.array([[c, 0, s], [0, 1, 0], [-s, 0, c]], float)


def rot_z(a):
    c, s = math.cos(a), math.sin(a)
    return np.array([[c, -s, 0], [s, c, 0], [0, 0, 1]], float)


def smin(a, b, k):
    h = np.clip(0.5 + 0.5 * (b - a) / k, 0.0, 1.0)
    return b + (a - b) * h - k * h * (1.0 - h)


def smax(a, b, k):
    return -smin(-a, -b, k)


def smoothstep(e0, e1, x):
    t = np.clip((x - e0) / (e1 - e0), 0.0, 1.0)
    return t * t * (3 - 2 * t)


def mix(a, b, t):
    t = np.asarray(t)
    if np.ndim(a) or np.ndim(b):
        t = t[..., None] if t.ndim and np.ndim(a) != t.ndim else t
    return a + (b - a) * t


_WAVES = {}


def wave_noise(P, freq, seed=0, n=16):
    """Smooth band-limited 3D noise ~N(0,1) from random plane waves."""
    key = (seed, n)
    if key not in _WAVES:
        rng = np.random.default_rng(seed)
        _WAVES[key] = (nrm_rows(rng.normal(size=(n, 3))), rng.uniform(0, 2 * np.pi, n), 0.6 + 0.8 * rng.random(n))
    d, ph, fr = _WAVES[key]
    v = np.zeros(len(P))
    for j in range(n):
        v += np.sin(P @ d[j] * (freq * fr[j]) + ph[j])
    return v / math.sqrt(n / 2)


def wave_noise_grad(P, freq, seed=0, n=16):
    """wave_noise value and its spatial gradient (for bump/normal detail)."""
    key = (seed, n)
    if key not in _WAVES:
        wave_noise(P[:1], freq, seed, n)
    d, ph, fr = _WAVES[key]
    v = np.zeros(len(P))
    g = np.zeros_like(P)
    for j in range(n):
        f = freq * fr[j]
        a = P @ d[j] * f + ph[j]
        v += np.sin(a)
        g += np.cos(a)[:, None] * (d[j] * f)
    s = math.sqrt(n / 2)
    return v / s, g / s


# ----------------------------------------------------------------------------- primitives

class Prim:
    """One SDF primitive.
    kind: 'ellipsoid' (c, r[3], R=3x3 columns=local axes), 'sphere' (c, r),
          'cone' (a, b, ra, rb)  - round cone / tapered capsule,
          'torus' (c, n=axis, R, r),
          'box' (c, b=half extents, R, rr=rounding),
          'plane' (c, n)  - half-space; keeps the +n side when used with op='sub'... see below,
          'noise' (c, r=bound radius, thresh, scale) - blobs where noise > thresh (punch holes),
          'group' (children=[Prim...]) - evaluated on its own, then unioned as ONE primitive.
            Use groups for hard-surface / cloth parts so their cuts don't carve the body.
    op:   'union' | 'sub' (applied in list order with smoothness k). A 'sub' removes the
          region where the subtracted primitive is negative (inside). For a 'plane' that is
          the side opposite n, i.e. the part on the +n side is KEPT.
    shell: >0 turns the solid into a hollow skin of thickness 2*shell (cloth, armor plates).
    bone: joint that owns this primitive (skin weights); tag: shading region;
    flow: fur/fibre direction hint (world); amp/freq: surface noise (fluff, ragged hems)."""

    def __init__(self, kind, bone=None, k=0.01, op="union", tag="body", flow=(0, 0, -1),
                 amp=0.0, freq=200.0, seed=1, name="", shell=0.0, children=None, **p):
        self.kind, self.bone, self.k, self.op, self.tag, self.name = kind, bone, k, op, tag, name
        self.flow = nrm(flow) if flow is not None else np.array([0, 0, -1.0])
        self.amp, self.freq, self.seed, self.shell = amp, freq, seed, shell
        self.children = children or []
        self.p = {key: (np.asarray(v, float) if not np.isscalar(v) else float(v)) for key, v in p.items()}
        if kind in ("ellipsoid", "box") and "R" not in self.p:
            self.p["R"] = np.eye(3)
        if kind == "plane":
            self.p["n"] = nrm(self.p["n"])
        self.bc, self.br = self._bounds()
        if kind == "group":
            us = [c for c in self.children if c.op == "union"]
            self.lo = np.min([c.lo for c in us], 0) - self.amp - self.shell
            self.hi = np.max([c.hi for c in us], 0) + self.amp + self.shell
        else:
            self.lo, self.hi = self.bc - self.br, self.bc + self.br

    def _bounds(self):
        p = self.p
        extra = self.amp + self.shell
        if self.kind == "ellipsoid":
            return p["c"], float(np.max(p["r"])) + extra
        if self.kind == "sphere":
            return p["c"], p["r"] + extra
        if self.kind == "cone":
            return (p["a"] + p["b"]) / 2, float(np.linalg.norm(p["b"] - p["a"]) / 2 + max(p["ra"], p["rb"])) + extra
        if self.kind == "torus":
            return p["c"], p["R"] + p["r"] + extra
        if self.kind == "box":
            return p["c"], float(np.linalg.norm(p["b"])) + p.get("rr", 0.0) + extra
        if self.kind in ("plane", "noise"):
            return p["c"], p.get("r", 10.0)
        if self.kind == "group":
            us = [c for c in self.children if c.op == "union"]
            lo = np.min([c.bc - c.br for c in us], 0)
            hi = np.max([c.bc + c.br for c in us], 0)
            return (lo + hi) / 2, float(np.linalg.norm(hi - lo) / 2) + extra
        raise ValueError(self.kind)

    def sdf(self, P):
        p = self.p
        if self.kind == "ellipsoid":
            q = (P - p["c"]) @ p["R"]
            r = p["r"]
            k0 = np.linalg.norm(q / r, axis=1)
            k1 = np.linalg.norm(q / (r * r), axis=1)
            d = k0 * (k0 - 1.0) / np.maximum(k1, 1e-9)
        elif self.kind == "sphere":
            d = np.linalg.norm(P - p["c"], axis=1) - p["r"]
        elif self.kind == "cone":
            d = _round_cone(P, p["a"], p["b"], p["ra"], p["rb"])
        elif self.kind == "torus":
            q = P - p["c"]
            h = q @ p["n"]
            rad = np.linalg.norm(q - h[:, None] * p["n"], axis=1)
            d = np.sqrt((rad - p["R"]) ** 2 + h * h) - p["r"]
        elif self.kind == "box":
            q = np.abs((P - p["c"]) @ p["R"]) - p["b"]
            d = np.linalg.norm(np.maximum(q, 0), axis=1) + np.minimum(q.max(1), 0) - p.get("rr", 0.0)
        elif self.kind == "plane":
            d = (P - p["c"]) @ p["n"]
        elif self.kind == "noise":
            d = (p["thresh"] - wave_noise(P, self.freq, self.seed)) * p.get("scale", 1.0 / self.freq)
        elif self.kind == "group":
            d = np.full(len(P), 1e3)
            for ch in self.children:
                di = ch.sdf(P)
                d = smin(d, di, ch.k) if ch.op == "union" else smax(d, -di, ch.k)
        if self.amp:
            d = d + self.amp * wave_noise(P, self.freq, self.seed)
        if self.shell:
            d = np.abs(d) - self.shell
        return d


def group(children, bone, tag, k=0.01, flow=(0, -1, 0), name="", **kw):
    return Prim("group", bone, k, tag=tag, flow=flow, name=name, children=children, **kw)


def _round_cone(P, a, b, r1, r2):
    """Inigo Quilez exact round cone, vectorised."""
    ba = b - a
    l2 = float(ba @ ba)
    rr = r1 - r2
    a2 = l2 - rr * rr
    il2 = 1.0 / l2
    pa = P - a
    y = pa @ ba
    z = y - l2
    w = pa * l2 - y[:, None] * ba
    x2 = (w * w).sum(1)
    y2 = y * y * l2
    z2 = z * z * l2
    k = np.sign(rr) * rr * rr * x2
    d3 = (np.sqrt(np.maximum(x2 * a2 * il2, 0)) + y * rr) * il2 - r1
    d1 = np.sqrt(x2 + z2) * il2 - r2
    d2 = np.sqrt(x2 + y2) * il2 - r1
    return np.where(np.sign(z) * a2 * z2 > k, d1, np.where(np.sign(y) * a2 * y2 < k, d2, d3))


# ----------------------------------------------------------------------------- creature

class Creature:
    """The sculpt. Field queries are evaluated in spatially sorted blocks; each block only
    touches primitives whose bounding sphere comes near the block's bounding box, so
    designs with hundreds of primitives (skeletons, armor) stay fast."""

    def __init__(self, prims, bones, margin=0.07, block=16384):
        self.prims = prims
        self.bones = bones                       # [(name, parent, world_pos)]
        self.bidx = {b[0]: i for i, b in enumerate(bones)}
        self.margin = margin
        self.block = block
        self.tags = sorted({p.tag for p in prims if p.op == "union"})
        self.tidx = {t: i for i, t in enumerate(self.tags)}
        self.BC = np.array([p.bc for p in prims])
        self.BR = np.array([p.br for p in prims])
        for p in prims:
            if p.op == "union" and p.bone not in self.bidx:
                raise ValueError(f"primitive {p.name or p.kind} uses unknown bone {p.bone}")

    def _blocks(self, P, extra):
        n = len(P)
        if n <= self.block:
            order = [np.arange(n)]
        else:
            lo = P.min(0)
            cell = np.floor((P - lo) / 0.05).astype(np.int64)
            key = (cell[:, 0] * 4096 + cell[:, 1]) * 4096 + cell[:, 2]
            srt = np.argsort(key, kind="stable")
            order = [srt[i:i + self.block] for i in range(0, n, self.block)]
        for idx in order:
            Pb = P[idx]
            lo, hi = Pb.min(0), Pb.max(0)
            dd = np.linalg.norm(np.maximum(np.maximum(lo - self.BC, self.BC - hi), 0), axis=1)
            yield idx, Pb, np.nonzero(dd < self.BR + extra)[0]

    # -- field
    def sdf(self, P):
        d = np.full(len(P), 1e3)
        for idx, Pb, cand in self._blocks(P, self.margin):
            db = np.full(len(Pb), 1e3)
            for i in cand:
                pr = self.prims[i]
                di = pr.sdf(Pb)
                db = smin(db, di, pr.k) if pr.op == "union" else smax(db, -di, pr.k)
            d[idx] = db
        return d

    def grad(self, P, eps=3e-4):
        ks = np.array([[1, -1, -1], [-1, -1, 1], [-1, 1, -1], [1, 1, 1]], float)
        g = np.zeros_like(P)
        for k in ks:
            g += k * self.sdf(P + k * eps)[:, None]
        return nrm_rows(g)

    def project(self, P, iters=3, clamp=0.004):
        P = P.copy()
        for _ in range(iters):
            d = self.sdf(P)
            g = self.grad(P)
            P -= np.clip(d, -clamp, clamp)[:, None] * g
        return P

    def ao(self, P, N, dists=(0.003, 0.007, 0.014, 0.028, 0.05), w=(0.3, 0.25, 0.2, 0.15, 0.1), strength=1.35):
        # Approximate SDFs (ellipsoids, smooth unions) under-estimate distance; calibrate
        # the field's local scale with a short reference step so open surfaces read unoccluded.
        h0 = 0.0015
        scale = np.clip(self.sdf(P + N * h0) / h0, 0.3, 1.0)
        occ = np.zeros(len(P))
        for h, wi in zip(dists, w):
            d = self.sdf(P + N * h) / scale
            occ += wi * np.clip((h - d) / h, 0, 1)
        return np.clip(1 - occ * strength, 0.08, 1)

    def blend(self, P, vec_fn, dim, sigma, cull=True):
        """Soft-min (log-sum-exp) blend of per-primitive vectors -> smooth regions, weights."""
        n = len(P)
        out = np.zeros((n, dim))
        extra = 12 * sigma + 0.01 if cull else 1e9
        for idx, Pb, cand in self._blocks(P, extra):
            nb = len(Pb)
            m = np.full(nb, -np.inf)
            s = np.zeros(nb)
            acc = np.zeros((nb, dim))
            for i in cand:
                pr = self.prims[i]
                if pr.op != "union":
                    continue
                l = -pr.sdf(Pb) / sigma
                newm = np.maximum(m, l)
                sc = np.exp(m - newm)
                e = np.exp(l - newm)
                s = s * sc + e
                acc = acc * sc[:, None] + e[:, None] * vec_fn(pr)[None, :]
                m = newm
            out[idx] = acc / np.maximum(s, 1e-30)[:, None]
        return out

    def bounds(self, pad=0.01):
        lo = np.min([p.lo for p in self.prims if p.op == "union"], 0) - pad
        hi = np.max([p.hi for p in self.prims if p.op == "union"], 0) + pad
        return lo, hi


# ----------------------------------------------------------------------------- meshing

def polygonize(cr, voxel, log=print):
    from skimage.measure import marching_cubes
    lo, hi = cr.bounds()
    n = np.ceil((hi - lo) / voxel).astype(int) + 1
    log(f"  grid {n[0]}x{n[1]}x{n[2]} = {n.prod()/1e6:.1f}M voxels @ {voxel*1000:.2f} mm")
    xs, ys, zs = (lo[i] + np.arange(n[i]) * voxel for i in range(3))
    vol = np.empty(n, np.float32)
    YY, ZZ = np.meshgrid(ys, zs, indexing="ij")
    step = max(1, int(2.5e6 // (n[1] * n[2])))
    for i0 in range(0, n[0], step):
        i1 = min(n[0], i0 + step)
        X = np.repeat(xs[i0:i1], n[1] * n[2])
        P = np.stack([X, np.tile(YY.ravel(), i1 - i0), np.tile(ZZ.ravel(), i1 - i0)], 1)
        vol[i0:i1] = cr.sdf(P).reshape(i1 - i0, n[1], n[2])
    V, F, _, _ = marching_cubes(vol, 0.0, spacing=(voxel,) * 3)
    V = V + lo
    F = F.astype(np.int64)
    # orient outward (compare with SDF gradient)
    sel = np.random.default_rng(0).choice(len(F), min(500, len(F)), replace=False)
    fn = np.cross(V[F[sel, 1]] - V[F[sel, 0]], V[F[sel, 2]] - V[F[sel, 0]])
    g = cr.grad(V[F[sel]].mean(1))
    if (np.einsum("ij,ij->i", fn, g) > 0).mean() < 0.5:
        F = F[:, ::-1]
    return V, F


def keep_large_components(V, F, min_faces=60):
    import scipy.sparse as sp
    n = len(V)
    e = np.concatenate([F[:, [0, 1]], F[:, [1, 2]]])
    A = sp.coo_matrix((np.ones(len(e)), (e[:, 0], e[:, 1])), shape=(n, n))
    nc, lab = sp.csgraph.connected_components(A, directed=False)
    fl = lab[F[:, 0]]
    cnt = np.bincount(fl, minlength=nc)
    F = F[cnt[fl] >= min_faces]
    return compact(V, F)


def compact(V, F):
    used = np.unique(F)
    remap = -np.ones(len(V), np.int64)
    remap[used] = np.arange(len(used))
    return V[used], remap[F]


def decimate(V, F, target_tris):
    import fast_simplification
    red = 1.0 - target_tris / len(F)
    if red <= 0:
        return V, F
    V2, F2 = fast_simplification.simplify(V.astype(np.float32), F.astype(np.int32), target_reduction=red, agg=7)
    return compact(V2.astype(float), F2.astype(np.int64))


def vertex_normals(V, F):
    fn = np.cross(V[F[:, 1]] - V[F[:, 0]], V[F[:, 2]] - V[F[:, 0]])
    N = np.zeros_like(V)
    for k in range(3):
        np.add.at(N, F[:, k], fn)
    return nrm_rows(N)


def unwrap(V, F, N, tex, padding=4, log=print):
    import xatlas
    atlas = xatlas.Atlas()
    atlas.add_mesh(V.astype(np.float32), F.astype(np.uint32), N.astype(np.float32))
    co = xatlas.ChartOptions()
    co.max_iterations = 4
    co.max_cost = 6.0           # larger charts -> less padding waste on detailed meshes
    po = xatlas.PackOptions()
    po.resolution = tex
    po.padding = padding
    po.bilinear = True
    po.rotate_charts = True
    po.bruteForce = False
    po.blockAlign = False
    atlas.generate(co, po)
    vmap, idx, uv = atlas[0]
    log(f"  xatlas: {atlas.chart_count} charts, utilization {atlas.get_utilization(0)*100:.0f}%")
    return vmap.astype(np.int64), idx.astype(np.int64), uv.astype(float)


def tangents(V, N, UV, F):
    p0, p1, p2 = V[F[:, 0]], V[F[:, 1]], V[F[:, 2]]
    w0, w1, w2 = UV[F[:, 0]], UV[F[:, 1]], UV[F[:, 2]]
    e1, e2 = p1 - p0, p2 - p0
    d1, d2 = w1 - w0, w2 - w0
    r = d1[:, 0] * d2[:, 1] - d2[:, 0] * d1[:, 1]
    r = np.where(np.abs(r) < 1e-12, 1e-12, r)
    sdir = (e1 * d2[:, 1:2] - e2 * d1[:, 1:2]) / r[:, None]
    tdir = (e2 * d1[:, 0:1] - e1 * d2[:, 0:1]) / r[:, None]
    T = np.zeros_like(V)
    B = np.zeros_like(V)
    for k in range(3):
        np.add.at(T, F[:, k], sdir)
        np.add.at(B, F[:, k], tdir)
    T = nrm_rows(T - N * np.einsum("ij,ij->i", N, T)[:, None])
    w = np.where(np.einsum("ij,ij->i", np.cross(N, T), B) < 0, -1.0, 1.0)
    return np.concatenate([T, w[:, None]], 1)


# ----------------------------------------------------------------------------- baking

def rasterize(UV, F, size):
    """Return (pixel_index, tri, bary) for every texel center covered by a UV triangle."""
    px = UV * size - 0.5
    pids, tris, bars = [], [], []
    for t, (a, b, c) in enumerate(F):
        A, B_, C = px[a], px[b], px[c]
        x0 = int(max(math.floor(min(A[0], B_[0], C[0])), 0))
        x1 = int(min(math.ceil(max(A[0], B_[0], C[0])), size - 1))
        y0 = int(max(math.floor(min(A[1], B_[1], C[1])), 0))
        y1 = int(min(math.ceil(max(A[1], B_[1], C[1])), size - 1))
        if x1 < x0 or y1 < y0:
            continue
        xs, ys = np.meshgrid(np.arange(x0, x1 + 1), np.arange(y0, y1 + 1))
        xs, ys = xs.ravel().astype(float), ys.ravel().astype(float)
        den = (B_[1] - C[1]) * (A[0] - C[0]) + (C[0] - B_[0]) * (A[1] - C[1])
        if abs(den) < 1e-12:
            continue
        l0 = ((B_[1] - C[1]) * (xs - C[0]) + (C[0] - B_[0]) * (ys - C[1])) / den
        l1 = ((C[1] - A[1]) * (xs - C[0]) + (A[0] - C[0]) * (ys - C[1])) / den
        l2 = 1 - l0 - l1
        ins = (l0 >= -1e-4) & (l1 >= -1e-4) & (l2 >= -1e-4)
        if not ins.any():
            continue
        pids.append((ys[ins] * size + xs[ins]).astype(np.int64))
        tris.append(np.full(ins.sum(), t))
        bars.append(np.stack([l0[ins], l1[ins], l2[ins]], 1))
    pid = np.concatenate(pids)
    tri = np.concatenate(tris)
    bar = np.concatenate(bars)
    _, first = np.unique(pid, return_index=True)  # drop duplicates on shared edges
    return pid[first], tri[first], bar[first]


def strand_field(P, flow, fp, fa, seed, n=36):
    """Fur-strand noise elongated along `flow` (anisotropic plane-wave sum with a
    spread of frequencies ~1/f amplitude so it doesn't form fingerprint swirls).
    Returns value in ~[-1,1] and its gradient (scaled by 1/fp)."""
    rng = np.random.default_rng(seed)
    K = nrm_rows(rng.normal(size=(n, 3)))
    ph = rng.uniform(0, 2 * np.pi, n)
    fr = np.exp(rng.uniform(np.log(0.55), np.log(1.9), n))
    amp = (0.6 + 0.8 * rng.random(n)) / fr
    fk = flow  # (N,3)
    h = np.zeros(len(P))
    g = np.zeros_like(P)
    for j in range(n):
        k = K[j]
        fdotk = fk @ k                                        # (N,)
        Jk = fr[j] * (fp * k[None, :] - (fp - fa) * fk * fdotk[:, None])  # J is symmetric
        arg = np.einsum("ij,ij->i", P, Jk) + ph[j]
        h += amp[j] * np.sin(arg)
        g += (amp[j] * np.cos(arg))[:, None] * Jk
    hn = math.sqrt((amp ** 2).sum() / 2)
    h /= hn
    g /= hn * fp
    r = 1 - 2 * np.clip(np.abs(h) / 2.2, 0, 1)                # soft ridges: strands
    gr = (-2 / 2.2) * np.sign(h)[:, None] * g
    return r, gr


def dilate(img, mask, iters_px=16):
    """Fill uncovered texels with nearest covered texel (seam padding)."""
    idx = ndimage.distance_transform_edt(~mask, return_distances=False, return_indices=True)
    return img[idx[0], idx[1]]


def bake(cr, V, F, Nv, Tv, UV, size, shade, chunk=250_000, log=print):
    """Bake albedo/normal/ORM from the SDF + design `shade(ctx)` callback."""
    t0 = time.time()
    pid, tri, bar = rasterize(UV, F, size)
    log(f"  rasterized {len(pid)/1e6:.2f}M texels ({len(pid)/size/size*100:.0f}% coverage) in {time.time()-t0:.1f}s")
    alb = np.zeros((size * size, 3), np.float32)
    nor = np.zeros((size * size, 3), np.float32)
    orm = np.zeros((size * size, 3), np.float32)
    ntag = len(cr.tags)
    for c0 in range(0, len(pid), chunk):
        sl = slice(c0, c0 + chunk)
        fi = F[tri[sl]]
        b = bar[sl]
        P = np.einsum("ij,ijk->ik", b, V[fi])
        Nl = nrm_rows(np.einsum("ij,ijk->ik", b, Nv[fi]))
        T4 = np.einsum("ij,ijk->ik", b, Tv[fi])
        w = np.sign(T4[:, 3] + 1e-9)
        T = nrm_rows(T4[:, :3] - Nl * np.einsum("ij,ij->i", Nl, T4[:, :3])[:, None])
        Bt = np.cross(Nl, T) * w[:, None]
        P = cr.project(P, iters=2)
        Nh = cr.grad(P)
        bad = np.einsum("ij,ij->i", Nh, Nl) < 0.15
        Nh[bad] = Nl[bad]
        attr = cr.blend(P, lambda pr: np.concatenate([np.eye(ntag)[cr.tidx[pr.tag]], pr.flow]), ntag + 3, sigma=0.0035)
        tagw = {t: attr[:, i] for t, i in cr.tidx.items()}
        flow = attr[:, ntag:]
        flow = flow - Nh * np.einsum("ij,ij->i", flow, Nh)[:, None]
        flow = nrm_rows(flow + 1e-6 * T)
        ao = cr.ao(P, Nh)
        ctx = dict(P=P, N=Nh, Nl=Nl, flow=flow, tag=tagw, ao=ao, T=T, B=Bt)
        out = shade(ctx)
        g = out.get("grad")
        Nf = Nh
        if g is not None:
            g = g - Nh * np.einsum("ij,ij->i", g, Nh)[:, None]
            Nf = nrm_rows(Nh - g * out["fur"][:, None])
        ts = np.stack([np.einsum("ij,ij->i", Nf, T), np.einsum("ij,ij->i", Nf, Bt), np.einsum("ij,ij->i", Nf, Nl)], 1)
        ids = pid[sl]
        alb[ids] = out["albedo"]
        nor[ids] = ts * 0.5 + 0.5
        orm[ids] = np.stack([out.get("occlusion", ao), out["roughness"], out.get("metallic", np.zeros(len(P)))], 1)
        log(f"    baked {min(c0+chunk, len(pid))/1e6:.2f}M / {len(pid)/1e6:.2f}M texels  ({time.time()-t0:.0f}s)")
    mask = np.zeros(size * size, bool)
    mask[pid] = True
    mask = mask.reshape(size, size)
    out = []
    for img in (alb, nor, orm):
        im = dilate(img.reshape(size, size, 3), mask)
        out.append(Image.fromarray((np.clip(im, 0, 1) * 255 + 0.5).astype(np.uint8)))
    return out


# ----------------------------------------------------------------------------- extra geometry

def uv_sphere(center, radius, axis, rings=10, segs=16):
    """Sphere whose pole points along `axis`. UV: u=longitude, v=0 at front pole."""
    R = basis(axis, [0, 1, 0] if abs(nrm(axis)[1]) < 0.9 else [1, 0, 0])
    V, N, UV, F = [], [], [], []
    for i in range(rings + 1):
        th = math.pi * i / rings
        for j in range(segs + 1):
            ph = 2 * math.pi * j / segs
            local = np.array([math.sin(th) * math.cos(ph), math.cos(th), math.sin(th) * math.sin(ph)])
            n = R @ local
            V.append(np.asarray(center) + radius * n)
            N.append(n)
            UV.append((j / segs, i / rings))
    for i in range(rings):
        for j in range(segs):
            a = i * (segs + 1) + j
            b = a + segs + 1
            if i > 0:
                F.append((a, a + 1, b))
            if i < rings - 1:
                F.append((a + 1, b + 1, b))
    V, N, UV, F = np.array(V), np.array(N), np.array(UV), np.array(F)
    fn = np.cross(V[F[:, 1]] - V[F[:, 0]], V[F[:, 2]] - V[F[:, 0]])
    if (np.einsum("ij,ij->i", fn, V[F].mean(1) - center) < 0).mean() > 0.5:
        F = F[:, ::-1]
    return V, N, UV, F


def strand_tube(points, r0, r1, sides=3):
    """Tapered tube along a polyline (whiskers, hairs)."""
    pts = np.asarray(points, float)
    n = len(pts)
    V, N, F = [], [], []
    for i in range(n):
        t = nrm(pts[min(i + 1, n - 1)] - pts[max(i - 1, 0)])
        a = nrm(np.cross(t, [0, 1, 0] if abs(t[1]) < 0.9 else [1, 0, 0]))
        b = np.cross(t, a)
        r = r0 + (r1 - r0) * i / (n - 1)
        for s in range(sides):
            ang = 2 * math.pi * s / sides
            d = math.cos(ang) * a + math.sin(ang) * b
            V.append(pts[i] + r * d)
            N.append(d)
    for i in range(n - 1):
        for s in range(sides):
            a0 = i * sides + s
            a1 = i * sides + (s + 1) % sides
            b0, b1 = a0 + sides, a1 + sides
            F += [(a0, b0, a1), (a1, b0, b1)]
    return np.array(V), np.array(N), np.array(F)


# ----------------------------------------------------------------------------- rig

def skin_weights(cr, P, sigma=0.006, max_inf=4):
    nb = len(cr.bones)
    W = cr.blend(P, lambda pr: np.eye(nb)[cr.bidx[pr.bone]], nb, sigma, cull=False)
    order = np.argsort(-W, 1)[:, :max_inf]
    Wt = np.take_along_axis(W, order, 1)
    Wt[Wt < 0.01] = 0
    Wt /= np.maximum(Wt.sum(1, keepdims=True), 1e-9)
    return order.astype(np.uint8), Wt.astype(np.float32)


def fixed_weights(n, cr, pairs):
    J = np.zeros((n, 4), np.uint8)
    W = np.zeros((n, 4), np.float32)
    for k, (b, w) in enumerate(pairs):
        J[:, k] = cr.bidx[b]
        W[:, k] = w
    return J, W


def quat_axis(axis, ang):
    a = nrm(axis)
    s = math.sin(ang / 2)
    return np.array([a[0] * s, a[1] * s, a[2] * s, math.cos(ang / 2)])


def quat_mul(q, r):
    x1, y1, z1, w1 = q
    x2, y2, z2, w2 = r
    return np.array([w1 * x2 + x1 * w2 + y1 * z2 - z1 * y2,
                     w1 * y2 - x1 * z2 + y1 * w2 + z1 * x2,
                     w1 * z2 + x1 * y2 - y1 * x2 + z1 * w2,
                     w1 * w2 - x1 * x2 - y1 * y2 - z1 * z2])


# ----------------------------------------------------------------------------- glTF export

def img_bytes(im, fmt="PNG", quality=92):
    b = io.BytesIO()
    if fmt == "JPEG":
        im.convert("RGB").save(b, "JPEG", quality=quality, optimize=True, subsampling=0)
        return b.getvalue(), "image/jpeg"
    im.save(b, "PNG", optimize=True)
    return b.getvalue(), "image/png"


def write_glb(path, bones, prims, materials, images, animations, extras, extensions_used=()):
    """prims: list of dict(P,N,UV,T(optional),J,W,I,material,name)
    images: list of (name, bytes, mime); textures map 1:1 to images.
    animations: list of (name, times, {bone_name: quats (K,4)}, {bone_name: translations (K,3)})"""
    blob = bytearray()
    views, accs = [], []

    def add_view(data, target=None):
        while len(blob) % 4:
            blob.append(0)
        off = len(blob)
        blob.extend(data)
        v = {"buffer": 0, "byteOffset": off, "byteLength": len(data)}
        if target:
            v["target"] = target
        views.append(v)
        return len(views) - 1

    def add_acc(arr, ctype, typ, target=None, minmax=False):
        arr = np.ascontiguousarray(arr)
        a = {"bufferView": add_view(arr.tobytes(), target), "componentType": ctype, "count": int(arr.shape[0]), "type": typ}
        if minmax:
            a["min"] = arr.reshape(arr.shape[0], -1).min(0).tolist()
            a["max"] = arr.reshape(arr.shape[0], -1).max(0).tolist()
        accs.append(a)
        return len(accs) - 1

    bidx = {b[0]: i for i, b in enumerate(bones)}
    world = {b[0]: np.asarray(b[2], float) for b in bones}
    nodes = []
    for name, par, pos in bones:
        loc = world[name] - (world[par] if par else 0)
        nodes.append({"name": name, "translation": [round(float(x), 6) for x in loc]})
    for i, (name, _, _) in enumerate(bones):
        ch = [bidx[c] for c, p, _ in bones if p == name]
        if ch:
            nodes[i]["children"] = ch
    ibm = np.zeros((len(bones), 16), np.float32)
    for i, (name, _, _) in enumerate(bones):
        m = np.eye(4)
        m[:3, 3] = -world[name]
        ibm[i] = m.T.reshape(-1)
    a_ibm = add_acc(ibm, 5126, "MAT4")
    roots = [bidx[b[0]] for b in bones if b[1] is None]
    arm = len(nodes)
    nodes.append({"name": "Armature", "children": roots})
    mesh_node = len(nodes)
    nodes.append({"name": extras.get("name", "Creature"), "mesh": 0, "skin": 0})

    gl_prims = []
    for p in prims:
        attrs = {"POSITION": add_acc(p["P"].astype(np.float32), 5126, "VEC3", 34962, True),
                 "NORMAL": add_acc(nrm_rows(p["N"]).astype(np.float32), 5126, "VEC3", 34962)}
        if p.get("UV") is not None:
            attrs["TEXCOORD_0"] = add_acc(p["UV"].astype(np.float32), 5126, "VEC2", 34962)
        if p.get("T") is not None:
            attrs["TANGENT"] = add_acc(p["T"].astype(np.float32), 5126, "VEC4", 34962)
        attrs["JOINTS_0"] = add_acc(p["J"].astype(np.uint8), 5121, "VEC4", 34962)
        attrs["WEIGHTS_0"] = add_acc(p["W"].astype(np.float32), 5126, "VEC4", 34962)
        I = p["I"].reshape(-1)
        it = (np.uint16, 5123) if p["P"].shape[0] < 65535 else (np.uint32, 5125)
        gl_prims.append({"attributes": attrs, "indices": add_acc(I.astype(it[0]), it[1], "SCALAR", 34963),
                         "material": p["material"], "mode": 4})

    img_entries = []
    for name, data, mime in images:
        img_entries.append({"name": name, "bufferView": add_view(data), "mimeType": mime})

    anims = []
    for name, times, rots, trans in animations:
        a_t = add_acc(np.asarray(times, np.float32), 5126, "SCALAR", minmax=True)
        samplers, channels = [], []
        for b, q in rots.items():
            samplers.append({"input": a_t, "output": add_acc(np.asarray(q, np.float32), 5126, "VEC4"), "interpolation": "LINEAR"})
            channels.append({"sampler": len(samplers) - 1, "target": {"node": bidx[b], "path": "rotation"}})
        for b, tr in (trans or {}).items():
            base = world[b] - (world[[x for x in bones if x[0] == b][0][1]] if [x for x in bones if x[0] == b][0][1] else 0)
            samplers.append({"input": a_t, "output": add_acc((np.asarray(tr) + base).astype(np.float32), 5126, "VEC3"), "interpolation": "LINEAR"})
            channels.append({"sampler": len(samplers) - 1, "target": {"node": bidx[b], "path": "translation"}})
        anims.append({"name": name, "samplers": samplers, "channels": channels})

    gltf = {
        "asset": {"version": "2.0", "generator": "creaturelib (SDF sculpt -> glTF)", "extras": extras},
        "scene": 0,
        "scenes": [{"name": "Scene", "nodes": [arm, mesh_node]}],
        "nodes": nodes,
        "meshes": [{"name": extras.get("name", "Creature"), "primitives": gl_prims}],
        "skins": [{"name": "Armature", "inverseBindMatrices": a_ibm, "joints": list(range(len(bones))), "skeleton": roots[0]}],
        "materials": materials,
        "samplers": [{"magFilter": 9729, "minFilter": 9987, "wrapS": 33071, "wrapT": 33071}],
        "images": img_entries,
        "textures": [{"sampler": 0, "source": i} for i in range(len(img_entries))],
        "animations": anims,
        "accessors": accs,
        "bufferViews": views,
        "buffers": [{"byteLength": 0}],
    }
    if extensions_used:
        gltf["extensionsUsed"] = list(extensions_used)
    while len(blob) % 4:
        blob.append(0)
    gltf["buffers"][0]["byteLength"] = len(blob)
    js = json.dumps(gltf, separators=(",", ":")).encode()
    while len(js) % 4:
        js += b" "
    total = 28 + len(js) + len(blob)
    with open(path, "wb") as f:
        f.write(struct.pack("<III", 0x46546C67, 2, total))
        f.write(struct.pack("<II", len(js), 0x4E4F534A))
        f.write(js)
        f.write(struct.pack("<II", len(blob), 0x004E4942))
        f.write(blob)
    return total
