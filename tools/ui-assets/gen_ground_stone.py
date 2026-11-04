"""Ground assets, family "stone": rocks, scree, pebbles, stumps, fallen logs, surface roots, twigs, leaf heaps, bones and
dropped gear, as rigid glTF with a baked albedo atlas (see groundkit.py for the container, budgets and manifest).

    python tools/ui-assets/gen_ground_stone.py [out_root] [--only a,b] [--preview]

Writes <out_root>/stone/<stem>.glb and <out_root>/stone.json (default out_root: dream/animations/ground/). --preview also
writes previews/ground_stone.png, a review sheet (not a shipped asset).

Original procedural geometry only; every mesh is authored in u (16 u = 1 m, isoforge's z-up, standing on z = 0, centred
on the origin) and exported by groundkit at gen_props.WORLD_UNITS_PER_U. Palette: ash-grey and umber stone, old wood,
bone, a little dull moss; rust exists only on the four dropped items. All of it is non-blocking scenery.
"""
from __future__ import annotations

import importlib
import math
import sys
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
if str(HERE) not in sys.path:
    sys.path.insert(0, str(HERE))

GP = importlib.import_module("gen_props")   # by name: check_sources.py allows tools to import only a fixed set of siblings
K = importlib.import_module("groundkit")
import isoforge as I  # noqa: E402
import uiart as U  # noqa: E402
Part = GP.Part

TAU = math.tau
FAMILY = "stone"

# ---------------------------------------------------------------------------------------------- materials
M = GP.M
ROCK = M("#403f3d", 0.90, cap=0.12, wear=0.7)
ROCK_DARK = M("#2d2d2f", 0.90, cap=0.14, wear=0.7)
ROCK_WARM = M("#413b33", 0.92, cap=0.16, wear=0.6)
SLATE = M("#31363b", 0.86, cap=0.16, wear=0.7)
MOSS = I.IsoMat((0.026, 0.031, 0.015), (0.020, 0.020, 0.020), 0.95, sat_cap=0.30,
                detail=(I.Waves3("gstone.moss.mound", 6, 2.5, 5.0, 0.08),))
BARK = M("#2b2723", 0.92, cap=0.18, wear=0.9)
BARK_OLD = M("#38332c", 0.92, cap=0.18, wear=0.9)
HEART = M("#4a4136", 0.90, cap=0.22, wear=0.5)           # cut or split wood
ROT = M("#2a2a22", 0.95, cap=0.18, wear=0.5)             # punky, grey-brown rotten wood
CHAR = M("#16130f", 0.92, cap=0.12, wear=0.6)            # burnt wood
ROOT = M("#2e2923", 0.92, cap=0.18, wear=0.8)
TWIG = M("#3a3229", 0.92, cap=0.20, wear=0.6)
LEAF_A = M("#443620", 0.94, cap=0.28, wear=0.2)
LEAF_B = M("#37301f", 0.94, cap=0.24, wear=0.2)
LEAF_C = M("#2a2a1e", 0.94, cap=0.20, wear=0.2)
BONE = GP.C.GRIM_MATS["grim.bone.old"]
BONE_DUST = I.IsoMat((0.034, 0.030, 0.026), (0.020, 0.020, 0.020), 0.95, sat_cap=0.2)
FUNGUS = M("#4a463e", 0.90, cap=0.22, wear=0.2)
# the barrel's iron: sat_cap .41 lets the rust pass reach a deep orange-brown and nothing else saturates
IRON = I.IsoMat((0.011, 0.011, 0.011), (0.034, 0.030, 0.027), 0.84, (0.012, 0.012, 0.013), (0.050, 0.047, 0.045), 0.7, sat_cap=0.41)
WOOD = GP.WOOD_OLD


# ---------------------------------------------------------------------------------------------- mesh helpers
def smooth(m: I.Mesh) -> I.Mesh:
    """Shared vertices and welded normals (35 degree crease): a flat mesh unwelds to three vertices a triangle the moment it
    merges with a smooth one, and the cover and accent byte budgets cannot pay for that."""
    return m if m.n is not None else I.Mesh(m.v, m.f, I._smooth_normals(m.v, m.f), m.uv, m.tint)


def cat(meshes) -> I.Mesh:
    out = None
    for m in meshes:
        m = smooth(m)
        out = m if out is None else out.merge(m)
    return out


def sub(m: I.Mesh) -> I.Mesh:
    """One midpoint subdivision, welded: four triangles per triangle. Flat output."""
    v = m.v.astype(np.float64)
    verts = [tuple(p) for p in v]
    cache: dict = {}

    def mid(a: int, b: int) -> int:
        k = (a, b) if a < b else (b, a)
        if k not in cache:
            cache[k] = len(verts)
            verts.append(tuple((v[a] + v[b]) / 2))
        return cache[k]

    out = []
    for a, b, c in m.f:
        ab, bc, ca = mid(int(a), int(b)), mid(int(b), int(c)), mid(int(c), int(a))
        out += [(a, ab, ca), (ab, b, bc), (ca, bc, c), (ab, bc, ca)]
    return I.Mesh(np.array(verts), np.array(out, dtype=np.int32))


def floor_at(m: I.Mesh, z: float) -> I.Mesh:
    """Flatten everything below z onto z: a rock's bedded underside."""
    v = m.v.astype(np.float64).copy()
    v[:, 2] = np.maximum(v[:, 2], z)
    return I.Mesh(v, m.f.copy(), m.n, m.uv, m.tint)


def mottle(m: I.Mesh, name: str, depth: float = 0.35, lo: float = 4.0, hi: float = 9.0) -> I.Mesh:
    """Per-vertex darkening in patches (a tint, so the albedo stays in the palette)."""
    n = I.waves3(m.v.astype(np.float64), name, 6, lo, hi)[0] * 0.5 + 0.5
    k = 1.0 - depth * np.clip(n, 0, 1)
    return I.Mesh(m.v, m.f, m.n, m.uv, np.stack((k, k, k), -1))


def rock(name: str, r: float, cuts: int, squash=(1.0, 1.0, 1.0), subdiv: int = 1, amp: float = 0.07, base: float = 0.55,
         yaw: float = 0.0, at=(0.0, 0.0, 0.0), tilt=(0.0, 0.0)) -> I.Mesh:
    """A faceted stone: a cut box, subdivided, pushed about by two bands of waves, bedded flat underneath."""
    m = I.convex(name, r, cuts, squash)
    for _ in range(subdiv):
        m = sub(m)
    m = m.displace(name + ".s", amp * r, 2.5, 6.0).displace(name + ".p", amp * r * 0.8, 6.0, 11.0)
    lo, hi = float(m.v[:, 2].min()), float(m.v[:, 2].max())
    m = floor_at(m, lo + base * (hi - lo) * 0.35)
    m = m.xform((tilt[0], tilt[1], yaw))
    lo = float(m.v[:, 2].min())
    return m.xform((0, 0, 0), (at[0], at[1], at[2] - lo))


def blob(rx, ry, rz, name, at=(0, 0, 0), rot=(0, 0, 0), rows=5, sides=8, amp=0.12) -> I.Mesh:
    _blob = importlib.import_module("gen_iso_relics").blob
    m = _blob(rx, ry, rz, rows, sides)
    if amp:
        m = m.displace(name, amp * min(rx, ry, rz), 2.0, 4.0, along_normal=False)
    return m.xform(rot, at)


def rod(a, b, r0, r1=None, sides=5, seg=3.0) -> I.Mesh:
    return I.tube([a, b], [r0, r0 if r1 is None else r1], sides=sides, seg_u=seg)


def ground(parts: list[Part]) -> list[Part]:
    """Centre on x and y (bounding box) and stand on z = 0."""
    ms = [p.mesh for p in parts if p.mesh is not None]
    v = np.vstack([m.v.astype(np.float64) for m in ms])
    dx, dy, dz = (v[:, 0].min() + v[:, 0].max()) / 2, (v[:, 1].min() + v[:, 1].max()) / 2, v[:, 2].min()
    return [Part(p.mesh.xform((0, 0, 0), (-dx, -dy, -dz)), p.mat, p.emit) for p in parts if p.mesh is not None]


def top_points(m: I.Mesh, n: int, name: str, min_nz: float = 0.7) -> list[np.ndarray]:
    """n deterministic points on the up-facing surface of m."""
    v = m.v.astype(np.float64)
    nrm = (m.n if m.n is not None else I._smooth_normals(m.v, m.f)).astype(np.float64)
    ok = np.nonzero((nrm[:, 2] > min_nz) & (v[:, 2] > v[:, 2].min() + 1.0))[0]
    r = U.rng(name)
    return [v[int(ok[int(r.integers(len(ok)))])] for _ in range(n)] if len(ok) else []


def moss_on(m: I.Mesh, n: int, name: str, size: float = 4.5) -> I.Mesh | None:
    out = None
    r = U.rng(name + ".sz")
    for i, p in enumerate(top_points(m, n, name)):
        s = size * float(r.uniform(0.7, 1.2))
        c = blob(s, s * 0.8, s * 0.28, f"{name}.{i}", (p[0], p[1], p[2] - s * 0.05), (0, 0, float(r.uniform(0, 360))), 4, 9, 0.2)
        out = c if out is None else out.merge(c)
    return out


# ---------------------------------------------------------------------------------------------- rocks
def boulder_01():
    a = rock("gs.b1.a", 17.0, 9, (1.15, 0.92, 0.80), 2, 0.09, yaw=20)
    b = rock("gs.b1.b", 8.5, 6, (1.0, 1.0, 0.75), 1, 0.10, yaw=60, at=(20.0, -6.0, 0.0), tilt=(6, -8))
    chips = [rock(f"gs.b1.c{i}", 2.4 + 0.6 * i, 4, (1, 1, 0.7), 0, 0.1, yaw=40 * i, at=(x, y, 0.0))
             for i, (x, y) in enumerate(((-19.0, 8.0), (4.0, -17.0), (24.0, 7.0), (-6.0, 17.0)))]
    return [Part(mottle(cat([a, b] + chips), "gs.b1.m", 0.30), ROCK)]


def boulder_02():
    a = rock("gs.b2.a", 16.0, 14, (1.05, 1.0, 0.85), 2, 0.07, yaw=75)
    b = rock("gs.b2.b", 7.0, 10, (1.0, 0.9, 0.8), 1, 0.08, yaw=10, at=(-18.0, 5.0, 0.0))
    moss = moss_on(a, 4, "gs.b2.moss", 5.0).merge(moss_on(b, 1, "gs.b2.moss2", 3.2))
    return [Part(mottle(cat([a, b]), "gs.b2.m", 0.30), ROCK_WARM), Part(moss, MOSS)]


def boulder_03():
    """A boulder split down the middle by frost: two halves apart, rubble in the crack."""
    h1 = rock("gs.b3.a", 14.0, 8, (0.8, 1.15, 0.85), 2, 0.07, yaw=0, at=(-8.5, 0.0, 0.0), tilt=(0, -7))
    h2 = rock("gs.b3.b", 13.0, 8, (0.75, 1.1, 0.8), 2, 0.07, yaw=180, at=(8.0, 1.5, 0.0), tilt=(2, 9))
    rub = [rock(f"gs.b3.r{i}", 1.8 + 0.5 * (i % 3), 3, (1, 1, 0.7), 0, 0.1, yaw=60 * i, at=(0.5 * (i % 2) - 0.3, -9.0 + 4.5 * i, 0.0))
           for i in range(4)]
    return [Part(mottle(cat([h1, h2] + rub), "gs.b3.m", 0.35), ROCK_DARK)]


def rock_slab_01():
    """Stacked, stepped plates of shale, tilted a little."""
    r = U.rng("gs.slab1")
    out, z = None, 0.0
    for k in range(3):
        h = 3.0 * (0.8 + 0.4 * float(r.random()))
        pl = I.convex(f"gs.slab1.{k}", 14.0 * (1.0 - 0.14 * k), 9, (1.25, 1.0, h / 14.0 * 1.6))
        pl = sub(pl).displace(f"gs.slab1.d{k}", 0.9, 2.5, 6.0)
        pl = pl.xform((0, 0, float(r.uniform(-25, 25))), (float(r.uniform(-2, 2)), float(r.uniform(-2, 2)), z + h * 0.5))
        pl = I.Mesh(pl.v, pl.f, None, None, np.full((len(pl.v), 3), (0.8 + 0.2 * float(r.random())) * (1 - 0.08 * k)))
        z += h * 1.1
        out = pl if out is None else out.merge(pl)
    return [Part(out.xform((5, -7, 25)), SLATE)]


def rock_slab_02():
    """A slab leaning on a low boulder, a broken-off piece lying below it."""
    slab = sub(I.convex("gs.slab2.s", 13.0, 6, (0.95, 0.16, 1.15))).displace("gs.slab2.d", 0.8, 2.5, 6.0)
    slab = slab.xform((-18, 0, 30))
    slab = slab.xform((0, 0, 0), (0, 4.0, -float(slab.v[:, 2].min())))
    prop = rock("gs.slab2.p", 8.5, 7, (1.1, 1.0, 0.6), 1, 0.09, yaw=30, at=(2.0, -7.0, 0.0))
    piece = rock("gs.slab2.c", 5.0, 5, (1.3, 1.0, 0.4), 1, 0.1, yaw=70, at=(-12.0, -12.0, 0.0), tilt=(0, 6))
    return [Part(mottle(cat([slab, piece]), "gs.slab2.m", 0.3), SLATE), Part(prop, ROCK_DARK)]


def _scree(tag: str, n: int, spread: float, rmin: float, rmax: float):
    r = U.rng("gs.scree." + tag)
    ms = []
    for i in range(n):
        a = float(r.uniform(0, TAU))
        d = spread * math.sqrt(float(r.random()))
        ms.append(rock(f"gs.scree.{tag}.{i}", float(r.uniform(rmin, rmax)), 3, (1.1, 1.0, 0.55), 0, 0.10, base=0.6, yaw=float(r.uniform(0, 360)),
                       at=(d * math.cos(a), d * math.sin(a), 0.0), tilt=(float(r.uniform(-12, 12)), float(r.uniform(-12, 12)))))
    return [Part(mottle(cat(ms), "gs.scree.m." + tag, 0.35, 2.5, 5.0), ROCK)]


def rock_scree_01():
    return _scree("01", 9, 9.0, 1.4, 3.0)


def rock_scree_02():
    return _scree("02", 7, 8.0, 2.0, 4.0)


def rock_scree_03():
    return _scree("03", 11, 11.0, 1.2, 2.6)


def _pebbles(tag: str, n: int, spread: float, lo: float, hi: float, rows: int, sides: int):
    r = U.rng("gs.pebbles." + tag)
    ms = []
    for i in range(n):
        a = float(r.uniform(0, TAU))
        d = spread * math.sqrt(float(r.random()))
        rx = float(r.uniform(lo, hi))
        ms.append(blob(rx, rx * float(r.uniform(0.7, 1.0)), rx * 0.55, f"gs.pb.{tag}.{i}", (d * math.cos(a), d * math.sin(a), rx * 0.2),
                       (0, 0, float(r.uniform(0, 360))), rows, sides, 0.10))
    return [Part(cat(ms), ROCK)]


def pebbles_01():
    return _pebbles("01", 9, 7.0, 0.8, 1.4, 3, 7)


def pebbles_02():
    return _pebbles("02", 6, 6.0, 1.0, 1.9, 4, 7)


def pebbles_03():
    return _pebbles("03", 10, 9.0, 0.6, 1.2, 3, 6)


# ---------------------------------------------------------------------------------------------- stumps
def _trunk(R: float, H: float, flare: float, rings: int = 7, sides: int = 14, top: str = "flat") -> I.Mesh:
    prof = [(0.0, 0.0)]
    for z in np.linspace(0, H, rings):
        prof.append((R * (1.0 + flare * math.exp(-z / 3.0)) * (1.0 - 0.05 * z / H), float(z)))
    if top == "flat":
        prof += [(R * 0.9, H + 0.1), (R * 0.45, H - 0.15), (R * 0.1, H + 0.1), (0.0, H)]
    else:   # a rotted crater
        prof += [(R * 0.82, H - 0.5), (R * 0.55, H - 2.5), (R * 0.2, H - 4.0), (0.0, H - 4.2)]
    return I.lathe(prof, sides)


def stump_axe():
    s = _trunk(8.5, 11.0, 0.25).displace("gs.stump.axe", 0.55, 2.0, 5.0)
    s = mottle(s, "gs.stump.axe.m", 0.25)
    # the axe: an iron head in the xz plane, a haft leaning back out of the cut top
    head = GP.prism([(0.0, 0.0), (7.5, 1.0), (8.6, 4.5), (7.4, 8.3), (3.0, 6.2), (0.0, 3.0)], -0.7, 0.7)
    head = head.xform((0, 0, 0), (-1.5, 0, 0)).xform((0, 0, 0), (1.5, 0.0, 10.0))
    haft = rod((0, 0, 0), (0, 0, 15.0), 0.8, 0.65, 6).xform((0, 24, 0), (0.5, 0.0, 9.5))
    return [Part(s, BARK_OLD), Part(haft, GP.WOOD_DARK), Part(head, IRON)]


def stump_rotten():
    s = _trunk(9.5, 9.0, 0.22, 7, 14, "crater").displace("gs.stump.rot", 1.0, 2.5, 6.0)
    s = mottle(s, "gs.stump.rot.m", 0.35)
    ms = moss_on(s, 3, "gs.stump.rot.moss", 3.8)
    br = cat([blob(2.4, 1.6, 0.8, f"gs.stump.rot.br{i}", (9.0 * math.cos(math.radians(a)), 9.0 * math.sin(math.radians(a)), 2.5 + 2.2 * i),
                   (0, 0, a - 90), 4, 8, 0.1) for i, a in enumerate((20, 130, 250))])
    parts = [Part(s, ROT), Part(br, FUNGUS)]
    if ms is not None:
        parts.append(Part(ms, MOSS))
    return parts


def stump_hollow():
    """A broken shell of a trunk: ragged crown, open inside, a gap in the wall."""
    nth = 28
    th = np.linspace(0, TAU, nth + 1)
    jag = U.rng("gs.stump.hollow").random(nth)
    jag = np.append(jag, jag[0])
    H = 13.0 + 8.0 * jag + 3.0 * np.sin(2 * th + 0.7)
    H[(th > 4.1) & (th < 5.0)] = 3.5                                   # the gap
    Ro, Ri = 9.0, 5.6
    # (radius, share of the column height); the last two rows are the hollow's floor at 1.5 u
    rows = [(Ro * 1.18, 0.0), (Ro * 1.02, 0.45), (Ro, 0.8), (Ro * 0.97, 0.99), (Ro * 0.9, 1.0), (Ri * 1.04, 1.0), (Ri, 0.8), (Ri * 0.96, 0.4),
            (Ri * 0.85, -1.0), (0.0, -1.0)]
    P = np.zeros((len(rows), nth + 1, 3))
    for i, (rad, hz) in enumerate(rows):
        P[i, :, 0], P[i, :, 1] = rad * np.cos(th), rad * np.sin(th)
        P[i, :, 2] = H * hz if hz >= 0 else 1.5
    shell = I.grid(P, None, smooth_normals=True).displace("gs.stump.hollow.d", 0.8, 2.5, 6.0)
    return [Part(mottle(shell, "gs.stump.hollow.m", 0.4), BARK)]


def stump_root_flare():
    s = _trunk(6.5, 9.0, 0.55, 8, 12, "flat").displace("gs.stump.rf", 0.5, 2.0, 5.0)
    roots = []
    for i in range(5):
        a = TAU * (i + 0.2 * math.sin(i * 2.1)) / 5
        L = 11.0 + 4.0 * math.sin(i * 1.7)
        pa = [(5.0 * math.cos(a), 5.0 * math.sin(a), 4.5), (8.5 * math.cos(a + 0.12), 8.5 * math.sin(a + 0.12), 1.8),
              ((6.0 + L) * math.cos(a + 0.2), (6.0 + L) * math.sin(a + 0.2), 0.7)]
        roots.append(I.tube(pa, [2.6, 1.7, 0.55], sides=6, seg_u=2.6).displace(f"gs.stump.rf.r{i}", 0.35, 2.5, 4.5))
    return [Part(mottle(cat([s] + roots), "gs.stump.rf.m", 0.3), BARK_OLD)]


# ---------------------------------------------------------------------------------------------- logs
def _log(name: str, length: float, r: float, bow: float = 1.5, taper: float = 0.85, sides: int = 9) -> I.Mesh:
    path = [(-length / 2, 0, r), (-length / 6, bow * 0.5, r), (length / 6, bow, r), (length / 2, 0, r)]
    return I.tube(path, [r * 1.05, r, r * 0.95, r * taper], sides=sides, seg_u=5.0).displace(name, 0.65, 2.0, 5.0)


def _stub(at, direction, ln, r, name):
    a = np.asarray(at, dtype=np.float64)
    d = np.asarray(direction, dtype=np.float64)
    d = d / np.linalg.norm(d)
    return I.tube([a, a + d * ln * 0.5, a + d * ln], [r * 1.5, r, r * 0.75], sides=6, seg_u=3.0).displace(name, 0.25, 2.0, 4.0)


def log_fallen_long():
    L = _log("gs.log.long", 70.0, 5.8, 2.0)
    stubs = [_stub((x, y * 4.2, 7.8), (0.25, y, 1.0), 6.0 - i, 1.2, f"gs.log.long.s{i}") for i, (x, y) in enumerate(((-22.0, 1.0), (4.0, -1.0), (19.0, 1.0)))]
    return [Part(mottle(cat([L] + stubs), "gs.log.long.m", 0.35), BARK)]


def log_fallen_split():
    """A trunk split lengthwise at one end: splinters stand out and the pale heartwood shows."""
    L = _log("gs.log.split", 52.0, 6.4, 1.0, 0.95)
    r = U.rng("gs.log.split.sp")
    sp = []
    for i in range(7):
        y = float(r.uniform(-4.5, 4.5))
        h = 6.0 + 4.0 * float(r.random())
        ln = 14.0 + 8.0 * float(r.random())
        sp.append(I.tube([(26.0, y, h * 0.5), (26.0 + ln * 0.5, y * 1.4, h * 0.75), (26.0 + ln, y * 1.7, h)], [1.5, 1.15, 0.35], sides=5, seg_u=4.0)
                  .displace(f"gs.log.split.s{i}", 0.3, 2.0, 4.0))
    core = I.box(20.0, 6.0, 9.5, 0.6).xform((0, 0, 0), (30.0, 0.0, 0.0)).displace("gs.log.split.core", 0.5, 2.5, 5.0)
    return [Part(mottle(L, "gs.log.split.m", 0.3), BARK_OLD), Part(cat(sp + [core]), HEART)]


def log_mossy():
    L = _log("gs.log.moss", 60.0, 6.2, 1.5)
    moss = moss_on(L, 7, "gs.log.moss.mo", 5.2)
    br = cat([blob(2.8, 1.8, 0.8, f"gs.log.moss.br{i}", (x, 6.2 * s, 5.0 + 0.7 * i), (0, 0, 90 * s), 4, 8, 0.1)
              for i, (x, s) in enumerate(((-14.0, 1), (6.0, -1), (22.0, 1)))])
    return [Part(mottle(L, "gs.log.moss.m", 0.35), BARK_OLD), Part(moss, MOSS), Part(br, FUNGUS)]


def log_burnt():
    L = _log("gs.log.burnt", 58.0, 6.0, 1.0, 0.8).displace("gs.log.burnt.cr", 1.1, 2.5, 6.0)
    stubs = [_stub((x, 0.0, 6.0), (0.1, s, 1.2), 5.0, 1.0, f"gs.log.burnt.s{i}") for i, (x, s) in enumerate(((-20.0, 1.0), (12.0, -1.0)))]
    ash = blob(11.0, 7.5, 0.7, "gs.log.burnt.ash", (-23.0, 0.0, 0.2), (0, 0, 10), 3, 12, 0.3)
    return [Part(mottle(cat([L] + stubs), "gs.log.burnt.m", 0.4), CHAR), Part(ash, BONE_DUST)]


# ---------------------------------------------------------------------------------------------- roots, twigs, leaves
def _roots(tag: str, n: int, reach: float):
    r = U.rng("gs.roots." + tag)
    ms = []
    for i in range(n):
        a = TAU * (i + 0.5 * float(r.random())) / n
        L = reach * float(r.uniform(0.7, 1.15))
        wig = float(r.uniform(-0.5, 0.5))
        pts = [(0.0, 0.0, 1.8), (0.4 * L * math.cos(a + wig * 0.4), 0.4 * L * math.sin(a + wig * 0.4), 1.6),
               (0.75 * L * math.cos(a + wig), 0.75 * L * math.sin(a + wig), 0.9), (L * math.cos(a + wig * 1.3), L * math.sin(a + wig * 1.3), 0.4)]
        ms.append(I.tube(pts, [1.7, 1.3, 0.85, 0.4], sides=6, seg_u=3.6).displace(f"gs.roots.{tag}.{i}", 0.3, 2.5, 4.5))
    ms.append(blob(3.4, 3.4, 1.6, f"gs.roots.{tag}.k", (0, 0, 0.2), rows=3, sides=8, amp=0.2))   # the knuckle the roots leave
    return [Part(mottle(cat(ms), f"gs.roots.{tag}.m", 0.3, 3.0, 6.0), ROOT)]


def roots_surface_01():
    return _roots("01", 3, 14.0)


def roots_surface_02():
    return _roots("02", 4, 11.0)


def roots_surface_03():
    return _roots("03", 2, 18.0)


def _twigs(tag: str, n: int, ln: float, forks: int):
    r = U.rng("gs.twigs." + tag)
    ms = []
    for i in range(n):
        a = float(r.uniform(0, TAU))
        c = (float(r.uniform(-4, 4)), float(r.uniform(-4, 4)))
        L = ln * float(r.uniform(0.6, 1.1))
        d = (math.cos(a), math.sin(a))
        z0 = 0.5 + 0.7 * (i % 3)
        p0 = (c[0] - d[0] * L / 2, c[1] - d[1] * L / 2, z0)
        p1 = (c[0] + d[0] * L / 2, c[1] + d[1] * L / 2, z0 + float(r.uniform(-0.2, 0.5)))
        mid = ((p0[0] + p1[0]) / 2 - d[1] * float(r.uniform(-1.2, 1.2)), (p0[1] + p1[1]) / 2 + d[0] * float(r.uniform(-1.2, 1.2)), z0 + 0.5)
        ms.append(I.tube([p0, mid, p1], [0.55, 0.45, 0.2], sides=4, seg_u=4.0))
        if i < forks:
            q = (mid[0] + d[0] * 2.2 - d[1] * 3.0, mid[1] + d[1] * 2.2 + d[0] * 3.0, z0 + 1.0)
            ms.append(I.tube([mid, q], [0.35, 0.12], sides=4, seg_u=3.0))
    return [Part(cat(ms), TWIG)]


def twigs_01():
    return _twigs("01", 6, 14.0, 3)


def twigs_02():
    return _twigs("02", 8, 10.0, 4)


def twigs_03():
    return _twigs("03", 4, 18.0, 2)


_LEAF_OUT = [(-2.4, 0.0), (-1.2, 1.1), (0.4, 1.5), (1.8, 1.0), (2.9, 0.0), (1.8, -1.0), (0.4, -1.5), (-1.2, -1.1)]
_LEAF_OAK = [(-2.6, 0.0), (-1.7, 1.1), (-0.9, 0.5), (-0.2, 1.6), (0.6, 0.8), (1.5, 1.3), (2.2, 0.0), (1.5, -1.3), (0.6, -0.8), (-0.2, -1.6), (-0.9, -0.5), (-1.7, -1.1)]


def _leaves(tag: str, n: int, spread: float, hmax: float, outline, mats):
    r = U.rng("gs.leaves." + tag)
    groups: list = [None] * len(mats)
    for i in range(n):
        a = float(r.uniform(0, TAU))
        d = spread * math.sqrt(float(r.random()))
        z = 0.25 + (hmax - 0.25) * max(0.0, 1.0 - d / spread) ** 0.8 * float(r.uniform(0.5, 1.0))
        s = float(r.uniform(0.9, 1.5))
        m = I.card(outline, 0.12).xform((float(r.uniform(-25, 25)), float(r.uniform(-25, 25)), float(r.uniform(0, 360))), (d * math.cos(a), d * math.sin(a), z), s)
        k = int(r.integers(len(mats)))
        groups[k] = m if groups[k] is None else groups[k].merge(m)
    return [Part(g, mats[k]) for k, g in enumerate(groups) if g is not None]


def leaf_heap_01():
    return _leaves("01", 26, 9.0, 3.2, _LEAF_OUT, (LEAF_A, LEAF_B))


def leaf_heap_02():
    return _leaves("02", 22, 8.0, 2.8, _LEAF_OAK, (LEAF_B, LEAF_C))


def leaf_heap_03():
    return _leaves("03", 18, 12.0, 1.2, _LEAF_OUT, (LEAF_A, LEAF_B, LEAF_C))


# ---------------------------------------------------------------------------------------------- bones
def _bone(a, b, r, knob=1.5) -> I.Mesh:
    """A long bone: a six-sided shaft swollen at both ends, from a to b."""
    a, b = np.asarray(a, dtype=np.float64), np.asarray(b, dtype=np.float64)
    ln = float(np.linalg.norm(b - a))
    prof = [(0.0, 0.0), (r * knob, 0.0), (r * 0.95, ln * 0.14), (r * 0.8, ln * 0.5), (r * 0.95, ln * 0.86), (r * knob, ln), (0.0, ln)]
    d = (b - a) / ln
    ry = math.degrees(math.acos(max(-1.0, min(1.0, float(d[2])))))
    rz = math.degrees(math.atan2(float(d[1]), float(d[0])))
    return I.lathe(prof, 5).xform((0, ry, rz), a)


def _skull_lo(name: str, at=(0.0, 0.0, 0.0), yaw: float = 0.0, scale: float = 1.0) -> I.Mesh:
    """A low-poly skull: cranium, muzzle, jaw and cheek bones; sockets and the nose go near black through the tint."""
    sk = cat([blob(3.2, 3.8, 3.0, name + ".c", (0, 0.4, 3.0), rows=5, sides=9, amp=0.05),
              blob(2.0, 2.2, 1.6, name + ".m", (0, -2.8, 1.8), rows=3, sides=7, amp=0.04),
              blob(2.2, 2.4, 0.7, name + ".j", (0, -2.6, 0.5), rows=3, sides=7, amp=0.04)]
             + [blob(0.9, 1.0, 1.0, f"{name}.k{s}", (2.2 * s, -1.6, 2.4), rows=2, sides=5, amp=0.0) for s in (-1, 1)])
    tint = np.ones((len(sk.v), 3))
    for s in (-1, 1):
        for c, rad in (((1.35 * s, -2.9, 3.4), 1.25), ((0.0, -4.4, 2.2), 0.9)):
            tint[np.linalg.norm(sk.v.astype(np.float64) - np.asarray(c), axis=1) < rad] *= 0.12
    return I.Mesh(sk.v, sk.f, sk.n, None, tint).xform((0, 0, yaw), at, scale)


def bones_skull():
    sk = _skull_lo("gs.bones.skull", (0, 0, 0), -25.0, 1.05).xform((-8, 6, 0))
    jaw = cat([_bone((-2.4, 0.0, 0.5), (0.0, 4.5, 0.5), 0.55, 1.2), _bone((2.4, 0.0, 0.5), (0.0, 4.5, 0.5), 0.55, 1.2)]).xform((0, 0, 20), (6.0, -4.0, 0.0))
    bits = _bone((-6.0, -5.0, 0.6), (-2.0, -9.0, 0.6), 0.5)
    dust = blob(8.0, 7.0, 0.5, "gs.bones.skull.dust", (0, -1.0, 0.1), rows=2, sides=8, amp=0.2)
    return [Part(mottle(cat([sk, jaw, bits]), "gs.bones.skull.m", 0.25, 3.0, 6.0), BONE), Part(dust, BONE_DUST)]


def bones_ribcage():
    spine = I.tube([(-9.0, 0.0, 1.0), (-3.0, 0.8, 1.2), (3.0, -0.4, 1.0), (9.0, 0.6, 1.1)], 0.85, sides=6, seg_u=4.0)
    ribs = []
    r = U.rng("gs.bones.rib")
    for i in range(5):
        side = 1 if i % 2 == 0 else -1
        ln = 8.5 * float(r.uniform(0.6, 1.0)) * (0.55 if i == 3 else 1.0)
        rise = 4.0 + 1.5 * float(r.random())
        p = [(0.0, 0.0, 0.0), (0.2 * ln, side * 0.35 * ln, rise * 0.8), (0.3 * ln, side * 0.7 * ln, rise), (0.2 * ln, side * ln, rise * 0.25)]
        ribs.append(I.tube(p, [0.7, 0.6, 0.55, 0.35], sides=5, seg_u=4.0).xform((0, 0, float(r.uniform(-14, 14))), (-7.0 + 3.4 * i, 0.3, 0.9)))
    dust = blob(11.0, 7.0, 0.5, "gs.bones.rib.dust", (0, 0, 0.1), rows=2, sides=8, amp=0.2)
    return [Part(mottle(cat([spine] + ribs), "gs.bones.rib.m", 0.25, 3.0, 6.0), BONE), Part(dust, BONE_DUST)]


def bones_scatter():
    r = U.rng("gs.bones.scatter")
    ms = []
    for _ in range(5):
        a = float(r.uniform(0, TAU))
        c = (float(r.uniform(-8, 8)), float(r.uniform(-6, 6)))
        ln = float(r.uniform(5.0, 9.0))
        d = (math.cos(a), math.sin(a))
        ms.append(_bone((c[0] - d[0] * ln / 2, c[1] - d[1] * ln / 2, 0.8), (c[0] + d[0] * ln / 2, c[1] + d[1] * ln / 2, 0.8 + float(r.uniform(0, 0.5))),
                        float(r.uniform(0.5, 0.75)), 1.6))
    ms.append(blob(2.6, 3.0, 2.4, "gs.bones.sc.cap", (9.0, 5.0, 1.2), (0, 0, 40), rows=3, sides=8, amp=0.06))   # a broken cranium
    dust = blob(10.0, 7.0, 0.5, "gs.bones.sc.dust", (0, 0, 0.1), rows=2, sides=8, amp=0.2)
    return [Part(mottle(cat(ms), "gs.bones.sc.m", 0.25, 3.0, 6.0), BONE), Part(dust, BONE_DUST)]


def bones_antler():
    beam = I.tube([(-9.0, 0.0, 1.5), (-3.0, 0.8, 2.2), (4.0, -0.6, 4.5), (10.0, 0.4, 7.5)], [1.4, 0.95, 0.75, 0.3], sides=6, seg_u=3.2).displace("gs.bones.antler.b", 0.25, 2.5, 5.0)
    tines = []
    for t, side, ln in ((0.25, 1, 6.5), (0.5, -1, 7.5), (0.72, 1, 5.5)):
        x = -9.0 + 19.0 * t
        z = 2.5 + 6.0 * t * t
        tines.append(I.tube([(x, 0.0, z), (x + 1.2, side * ln * 0.5, z + 2.2), (x + 2.2, side * ln, z + 1.2)], [0.75, 0.55, 0.2], sides=5, seg_u=3.0))
    brow = I.tube([(-9.0, 0.0, 1.5), (-9.0, 0.5, 4.2), (-6.0, 2.5, 5.2)], [1.4, 0.8, 0.25], sides=5, seg_u=3.0)
    plate = blob(2.4, 2.0, 1.2, "gs.bones.antler.base", (-9.8, 0.0, 1.4), rows=4, sides=8, amp=0.15)
    return [Part(mottle(cat([beam] + tines + [brow, plate]), "gs.bones.antler.m", 0.3, 3.0, 6.0), BONE)]


# ---------------------------------------------------------------------------------------------- dropped gear (the only rusted things)
def item_rusted_helm():
    prof = [(6.6, 0.0), (7.0, 3.0), (6.8, 6.8), (5.6, 9.8), (3.2, 11.8), (0.0, 12.4), (0.0, 11.7), (2.9, 11.2), (5.0, 9.4), (6.1, 6.9), (6.3, 3.0), (5.9, 0.3)]
    shell = I.lathe(prof, 12).displace("gs.helm.dent", 0.7, 2.5, 5.0)
    shell = I.Mesh(shell.v * np.array([1.0, 0.88, 1.0]), shell.f, shell.n)
    brim = I.torus(6.7, 0.5, 5, 12).xform((0, 0, 0), (0, 0, 0.3))
    crest = I.box(1.0, 14.0, 1.6, 0.2).xform((0, 0, 0), (0, 0, 11.6))
    return [Part(cat([shell, brim, crest]).xform((78, 8, 35), (0, 0, 0), 0.6), IRON)]


def item_broken_blade():
    blade = GP.prism([(0.0, 0.0), (2.0, 0.0), (2.0, 14.0), (1.3, 17.5), (0.9, 16.0), (0.4, 18.4), (0.0, 15.0)], -0.28, 0.28)
    blade = blade.xform((90, 0, 0)).xform((0, 0, 0), (-1.0, -6.0, 0.0))
    guard = I.box(9.0, 1.4, 1.3, 0.3).xform((0, 0, 0), (0.0, -7.0, 0.0))
    grip = rod((0, -7.5, 0.8), (0, -14.5, 0.8), 0.65, 0.65, 6)
    pommel = blob(1.1, 1.1, 1.0, "gs.blade.pom", (0, -15.2, 0.9), rows=4, sides=7, amp=0.0)
    sword = cat([blade, guard, grip, pommel]).xform((0, 4, 32))
    tip = GP.prism([(0.0, 0.0), (1.8, 0.0), (1.1, 4.8), (0.2, 7.0)], -0.28, 0.28).xform((90, 0, 0)).xform((0, 0, 70), (9.0, 6.0, 0.0))
    return [Part(cat([sword, tip]), IRON)]


def item_split_shield():
    def half(sign: float) -> I.Mesh:
        arc = [(sign * 8.6 * math.sin(t), -8.6 * math.cos(t)) for t in np.linspace(0, math.pi, 9)]
        return GP.prism(arc, -0.7, 0.7).displace(f"gs.shield.h{sign}", 0.3, 2.5, 5.0)
    a = half(1.0).xform((90, 0, 0)).xform((0, 8, -4), (1.4, 0.0, 0.0))
    b = half(-1.0).xform((90, 0, 0)).xform((0, -14, 12), (-2.2, 1.0, 0.0))
    boss = blob(2.4, 2.4, 1.3, "gs.shield.boss", (1.4, 0.0, 1.0), rows=4, sides=8, amp=0.0)
    rim = I.tube([(8.6 * math.sin(t) + 1.4, -8.6 * math.cos(t), 0.9) for t in np.linspace(0.2, 2.9, 7)], 0.5, sides=5, seg_u=3.0)
    return [Part(cat([a, b]), WOOD), Part(cat([boss, rim]), IRON)]


def item_spent_lantern():
    cage = [rod((3.0 * math.cos(TAU * k / 6), 3.0 * math.sin(TAU * k / 6), 1.0), (2.6 * math.cos(TAU * k / 6), 2.6 * math.sin(TAU * k / 6), 9.0), 0.28, 0.28, 4)
            for k in range(6)]
    base = I.lathe([(0.0, 0.0), (3.6, 0.0), (3.6, 1.2), (3.0, 1.4), (0.0, 1.4)], 10)
    cap = I.lathe([(3.4, 9.0), (3.4, 9.8), (2.0, 11.2), (1.0, 11.6), (0.0, 11.8), (0.0, 9.0)], 10)
    ring = I.torus(1.0, 0.25, 4, 10).xform((0, 0, 0), (0, 0, 12.2))
    bail = rod((-3.0, 0.0, 9.4), (0.0, 0.0, 14.0), 0.3, 0.3, 4).merge(rod((0.0, 0.0, 14.0), (3.0, 0.0, 9.4), 0.3, 0.3, 4))
    return [Part(cat(cage + [base, cap, ring, bail]).xform((76, 0, 40)), IRON)]


# ---------------------------------------------------------------------------------------------- the set
ANY_ROCK = ("grass", "dirt", "stone", "sand", "mud", "snow", "rock")
BONE_GROUND = ("dirt", "mud", "sand")
WOOD_GROUND = ("grass", "dirt", "mud")
ITEM_GROUND = ("grass", "dirt", "mud", "sand")
BONE_AGE = (0.30, 0.60, 0.0, 0.15)
ITEM_AGE = (0.45, 0.60, 0.35, 0.08)   # the only rust in the family
LEAF_AGE = (0.10, 0.40, 0.0, 0.05)


def _spec(build, cls, layer, atlas, ground_=ANY_ROCK, weight=1.0, scale=(0.9, 1.15), disc=(0.0, 0.0), weld=False, age=(0.40, 0.60, 0.0, 0.12)):
    return K.GroundSpec(lambda: ground(build()), cls, layer, atlas, disc, weld, age, ground_, weight, scale)


def _assets() -> dict[str, K.GroundSpec]:
    s = _spec
    return {
        "boulder_01": s(boulder_01, "rock", "accent", 256, weight=0.8, scale=(0.8, 1.3), disc=(26.0, 20.0)),
        "boulder_02": s(boulder_02, "rock", "accent", 256, weight=0.8, scale=(0.8, 1.3), disc=(26.0, 20.0)),
        "boulder_03": s(boulder_03, "rock", "accent", 256, weight=0.6, scale=(0.8, 1.25), disc=(26.0, 20.0)),
        "rock_slab_01": s(rock_slab_01, "rock", "accent", 128, weight=0.9, scale=(0.85, 1.3), disc=(20.0, 18.0), age=(0.25, 0.40, 0.0, 0.04)),
        "rock_slab_02": s(rock_slab_02, "rock", "accent", 256, weight=0.8, scale=(0.85, 1.25), disc=(20.0, 20.0)),
        "rock_scree_01": s(rock_scree_01, "rock", "cover", 128, weight=1.2),
        "rock_scree_02": s(rock_scree_02, "rock", "cover", 128, weight=1.2),
        "rock_scree_03": s(rock_scree_03, "rock", "cover", 128, weight=1.2),
        "pebbles_01": s(pebbles_01, "pebbles", "cover", 128, weight=1.5, weld=True),
        "pebbles_02": s(pebbles_02, "pebbles", "cover", 128, weight=1.5, weld=True),
        "pebbles_03": s(pebbles_03, "pebbles", "cover", 128, weight=1.5, weld=True),
        "stump_axe": s(stump_axe, "stump", "accent", 256, WOOD_GROUND, 0.5, (0.9, 1.1), (11.0, 11.0)),
        "stump_rotten": s(stump_rotten, "stump", "accent", 256, WOOD_GROUND, 1.0, (0.9, 1.2), (12.0, 12.0)),
        "stump_hollow": s(stump_hollow, "stump", "accent", 256, WOOD_GROUND, 0.8, (0.9, 1.2), (13.0, 13.0)),
        "stump_root_flare": s(stump_root_flare, "stump", "accent", 256, WOOD_GROUND, 1.0, (0.9, 1.2), (16.0, 16.0)),
        "log_fallen_long": s(log_fallen_long, "log", "accent", 256, WOOD_GROUND, 1.0, (0.85, 1.15), (38.0, 9.0)),
        "log_fallen_split": s(log_fallen_split, "log", "accent", 256, WOOD_GROUND, 0.7, (0.85, 1.15), (30.0, 10.0)),
        "log_mossy": s(log_mossy, "log", "accent", 256, WOOD_GROUND, 1.0, (0.85, 1.15), (33.0, 9.0)),
        "log_burnt": s(log_burnt, "log", "accent", 256, WOOD_GROUND, 0.6, (0.85, 1.15), (32.0, 9.0)),
        "roots_surface_01": s(roots_surface_01, "root", "cover", 128, WOOD_GROUND, 1.0, (0.9, 1.25), weld=True),
        "roots_surface_02": s(roots_surface_02, "root", "cover", 128, WOOD_GROUND, 1.0, (0.9, 1.25), weld=True),
        "roots_surface_03": s(roots_surface_03, "root", "cover", 128, WOOD_GROUND, 0.8, (0.9, 1.25), weld=True),
        "twigs_01": s(twigs_01, "twigs", "cover", 128, WOOD_GROUND, 1.5, (0.9, 1.25)),
        "twigs_02": s(twigs_02, "twigs", "cover", 128, WOOD_GROUND, 1.5, (0.9, 1.25)),
        "twigs_03": s(twigs_03, "twigs", "cover", 128, WOOD_GROUND, 1.2, (0.9, 1.25)),
        "leaf_heap_01": s(leaf_heap_01, "leaves", "cover", 128, WOOD_GROUND, 1.5, (0.9, 1.3), age=LEAF_AGE),
        "leaf_heap_02": s(leaf_heap_02, "leaves", "cover", 128, WOOD_GROUND, 1.5, (0.9, 1.3), age=LEAF_AGE),
        "leaf_heap_03": s(leaf_heap_03, "leaves", "cover", 128, WOOD_GROUND, 1.5, (0.9, 1.3), age=LEAF_AGE),
        "bones_skull": s(bones_skull, "bones", "cover", 128, BONE_GROUND, 0.6, age=BONE_AGE),
        "bones_ribcage": s(bones_ribcage, "bones", "cover", 128, BONE_GROUND, 0.5, age=BONE_AGE),
        "bones_scatter": s(bones_scatter, "bones", "cover", 128, BONE_GROUND, 0.9, age=BONE_AGE),
        "bones_antler": s(bones_antler, "bones", "cover", 128, BONE_GROUND, 0.6, age=BONE_AGE),
        "item_rusted_helm": s(item_rusted_helm, "item", "cover", 128, ITEM_GROUND, 0.3, (0.95, 1.1), age=ITEM_AGE),
        "item_broken_blade": s(item_broken_blade, "item", "cover", 128, ITEM_GROUND, 0.3, (0.95, 1.1), age=ITEM_AGE),
        "item_split_shield": s(item_split_shield, "item", "cover", 128, ITEM_GROUND, 0.3, (0.95, 1.1), age=ITEM_AGE),
        "item_spent_lantern": s(item_spent_lantern, "item", "cover", 128, ITEM_GROUND, 0.3, (0.95, 1.1), age=ITEM_AGE),
    }


ASSETS: dict[str, K.GroundSpec] = _assets()
BUILDERS = {n: f for n, f in globals().items() if n in ASSETS}
assert list(BUILDERS) != [] and set(BUILDERS) == set(ASSETS), "every stem needs a builder of the same name"


# ---------------------------------------------------------------------------------------------- build and write
def main(out_root: Path = K.GROUND_ROOT, only: list[str] | None = None) -> dict:
    stems = [n for n in ASSETS if not only or n in only]
    unknown = [n for n in (only or []) if n not in ASSETS]
    if unknown:
        raise SystemExit(f"unknown stone assets: {unknown}")
    entries, files, built = [], {}, {}
    for stem in stems:
        a = K.build_ground_asset(FAMILY, stem, ASSETS[stem])
        data = K.glb_bytes(a)
        files[stem] = data
        built[stem] = a
        entries.append(K.manifest_entry(FAMILY, stem, ASSETS[stem], a["bounds"]))
        tris = sum(len(p.indices) for p in a["primitives"])
        print(f"gen_ground_stone: {stem:18s} {ASSETS[stem].cls:8s} {ASSETS[stem].layer:7s} tris={tris:5d} size={len(data):7d}")
    K.write_family(Path(out_root), FAMILY, entries, files, "tools/ui-assets/gen_ground_stone.py")
    print(f"gen_ground_stone: {len(files)} assets, {sum(map(len, files.values()))} bytes -> {out_root}")
    return built


# ---------------------------------------------------------------------------------------------- review sheet
def preview(built: dict, out: Path, cols: int = 6, tile: int = 200, ss: int = 3) -> Path:
    """A contact sheet of every asset, painter-sorted and flat-lit from the baked atlas: a review aid, not a shipped asset."""
    from PIL import Image, ImageDraw
    rows = (len(built) + cols - 1) // cols
    sheet = Image.new("RGB", (cols * tile, rows * tile), (58, 58, 56))
    yaw, pitch = math.radians(35.0), math.radians(40.0)
    cy, sy, cp, sp = math.cos(yaw), math.sin(yaw), math.cos(pitch), math.sin(pitch)
    light = np.array([-0.45, 0.8, 0.4]) / np.linalg.norm([-0.45, 0.8, 0.4])
    for n, (stem, a) in enumerate(built.items()):
        prim = a["primitives"][0]
        pos, nrm, uv, idx = prim.positions.astype(np.float64), prim.normals.astype(np.float64), prim.uvs.astype(np.float64), prim.indices.astype(np.int64)
        enc = a["encoded"]
        tex = enc.shape[0]
        # glTF Y up: rotate about Y, then tilt about X, drop the depth
        x = pos[:, 0] * cy + pos[:, 2] * sy
        z = -pos[:, 0] * sy + pos[:, 2] * cy
        sx, syv, depth = x, pos[:, 1] * cp - z * sp, pos[:, 1] * sp + z * cp
        ext = max(float(np.ptp(sx)), float(np.ptp(syv)), 0.5)
        k = (tile * ss * 0.78) / ext
        ox, oy = tile * ss / 2 - k * (sx.min() + sx.max()) / 2, tile * ss * 0.62 + k * (syv.min() + syv.max()) / 2 - k * float(np.ptp(syv)) * 0.0
        px, py = ox + sx * k, oy - syv * k
        tri = idx
        cen_uv = uv[tri].mean(1)
        tx = np.clip((cen_uv[:, 0] * tex).astype(int), 0, tex - 1)
        ty = np.clip((cen_uv[:, 1] * tex).astype(int), 0, tex - 1)
        albedo = enc[ty, tx, :3].astype(np.float64) / 255.0 * a["scale"]
        fn = nrm[tri].mean(1)
        fn /= np.maximum(np.linalg.norm(fn, axis=1, keepdims=True), 1e-9)
        lit = 0.45 + 0.55 * np.clip(fn @ light, 0, 1) + 0.15 * np.clip(fn[:, 1], 0, 1)
        col = np.clip((albedo * lit[:, None] * 3.5) ** (1 / 2.2), 0, 1)
        order = np.argsort(depth[tri].mean(1))
        img = Image.new("RGB", (tile * ss, tile * ss), (58, 58, 56))
        dr = ImageDraw.Draw(img)
        dr.ellipse([tile * ss * 0.5 - 34 * ss, tile * ss * 0.68, tile * ss * 0.5 + 34 * ss, tile * ss * 0.68 + 26 * ss], fill=(50, 50, 48))
        for t in order:
            q = tri[t]
            dr.polygon([(px[q[0]], py[q[0]]), (px[q[1]], py[q[1]]), (px[q[2]], py[q[2]])], fill=tuple(int(c * 255) for c in col[t]))
        img = img.resize((tile, tile), Image.LANCZOS)
        d2 = ImageDraw.Draw(img)
        d2.text((4, 3), stem, fill=(190, 190, 180))
        sheet.paste(img, ((n % cols) * tile, (n // cols) * tile))
    out.parent.mkdir(parents=True, exist_ok=True)
    sheet.save(out, format="PNG", optimize=True)
    return out


if __name__ == "__main__":
    argv = sys.argv[1:]
    only_arg = None
    if "--only" in argv:
        i = argv.index("--only")
        only_arg = argv[i + 1].split(",")
        del argv[i:i + 2]
    want_preview = "--preview" in argv
    pos_args = [x for x in argv if not x.startswith("--")]
    built_ = main(Path(pos_args[0]) if pos_args else K.GROUND_ROOT, only_arg)
    if want_preview:
        print("preview:", preview(built_, HERE / "previews" / "ground_stone.png"))
