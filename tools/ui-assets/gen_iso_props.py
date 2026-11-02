"""Isometric props: the rusted iron barrel, the calibration object for isoforge.

A heavy rusted iron drum: sixteen lap-jointed iron staves in a bulge, a riveted seam strap, an iron head with
a bung, and four thick hammered hoops with two rows of rivets each. One stave is broken and splintered at the
rim, another is dented. Rust sits in big patches (8 u and up) with vertical runs, carried on the vertex tint.
Primary forms are the body, the hoops, the rust patches and the head (8 u and up), secondary the staves,
rivets, seams and dents (2..6 u), tertiary only pitting (under 1 u).
"""
from __future__ import annotations

import math

import numpy as np

import uiart as U
from isoforge import IsoMat, Mesh, ObjectSpec, Scene, Waves3, grid, lathe, waves3

HEIGHT = 20.0
R_END = 6.0
R_MID = 7.5
STAVES = 16
SEAM_DEPTH = 0.26
RIM = 1.05
HEAD_Z = HEIGHT - 2.6
SIZE = (128, 160)
PIVOT = (40, 118)
YAW = 14.0
BROKEN = 8                      # splintered stave at the rim, front left
DENTED = 11                     # dented stave, on the shadow side
BUNG_PHI = math.radians(188.0)  # before YAW
STRAP_PHI = math.radians(282.0)
HOOPS = ((2.2, 3.0, 1.0), (7.0, 2.8, 0.95), (13.0, 2.8, 0.95), (17.4, 3.0, 1.0))   # z, height, thickness: heavy

# Albedo is a near-neutral dark; the vertex tint (see _rust_tint) makes it blue-black iron or deep orange-brown rust.
# bare_f0 stays low (.05): worn edges catch a dull highlight, never chrome. sat_cap .41 lands the rust at p99 .50 after the downsample and 8-bit rounding.
PLATE = IsoMat((0.012, 0.012, 0.012), (0.034, 0.030, 0.027), 0.84, (0.012, 0.012, 0.013), (0.050, 0.047, 0.045), 0.7, sat_cap=0.41,
               detail=(Waves3("plate.dent", 6, 7.0, 12.0, 0.18), Waves3("plate.ding", 6, 2.5, 5.5, 0.03), Waves3("plate.pit", 8, 0.35, 0.9, 0.005)))
HOOP = IsoMat((0.011, 0.011, 0.011), (0.034, 0.030, 0.027), 0.82, (0.012, 0.012, 0.013), (0.055, 0.052, 0.050), 0.8, sat_cap=0.41,
              detail=(Waves3("hoop.hammer", 6, 2.0, 4.0, 0.03), Waves3("hoop.warp", 6, 8.0, 12.0, 0.14), Waves3("hoop.pit", 8, 0.35, 0.9, 0.006)))
HEAD = IsoMat((0.010, 0.010, 0.011), (0.034, 0.030, 0.027), 0.86, (0.011, 0.011, 0.012), (0.045, 0.043, 0.042), 0.6, sat_cap=0.41,
              detail=(Waves3("head.dish", 6, 8.0, 12.0, 0.10), Waves3("head.ding", 6, 2.5, 5.5, 0.03), Waves3("head.pit", 8, 0.35, 0.9, 0.005)))


def radius(z):
    t = 2 * np.asarray(z) / HEIGHT - 1
    return R_END + (R_MID - R_END) * (1 - t * t)


def _smooth(lo, hi, x):
    t = np.clip((x - lo) / (hi - lo), 0, 1)
    return t * t * (3 - 2 * t)


def _rust_tint(m: Mesh, gain: float = 1.0, cover: float = 0.0) -> Mesh:
    """Rust on the vertex tint: patches 8 u and wider, long vertical runs, over blue-black iron. Rust is a deep
    orange-brown (linear albedo about .07 .024 .007), the iron between it a cold near-black. cover shifts the
    rust threshold: positive means more rust."""
    P = m.v.astype(np.float64)
    big = waves3(P, "barrel.rust.patch", 8, 8.0, 15.0)[0]
    mid = waves3(P, "barrel.rust.edge", 6, 3.0, 6.0)[0]
    run = waves3(P, "barrel.rust.run", 8, 2.0, 4.0, (1.0, 1.0, 0.10))[0]
    w = _smooth(0.55 - cover, 1.15 - cover, 0.85 * big + 0.30 * mid + 0.80 * run)
    w = w * (1 - 0.8 * _smooth(17.0, 19.0, P[:, 2]))
    k = 0.40 + 0.95 * _smooth(-0.7, 0.7, big + 0.2 * mid)
    rust = np.array((8.0, 2.9, 0.85))
    base = np.array((0.62, 1.0, 2.0))
    tint = (base[None, :] * (1 - w[:, None]) + rust[None, :] * w[:, None]) * (k * gain)[:, None]
    return Mesh(m.v, m.f, m.n, m.uv, tint.astype(np.float32))


def _staves() -> Mesh:
    """One lattice around the barrel: per stave the columns are seam, chamfer, middle, chamfer; the seam column
    is shared with the next stave and sunk into a lap groove. Plates differ in thickness so every seam steps.
    One stave is broken and splintered at the rim, another dented. Faceted (flat normals)."""
    r = U.rng("iso.barrel.staves")
    per = 4
    ncol = STAVES * per + 1
    top = HEIGHT - 0.6 * r.random(STAVES)                  # each stave stops at its own height
    top[BROKEN] -= 3.4
    top[(BROKEN + 1) % STAVES] -= 1.3                      # the neighbour is cracked down too
    rows = 44
    cols = np.arange(ncol)
    s = cols // per
    k = cols % per
    frac = np.array((-0.5, -0.43, 0.0, 0.43))[k]
    phi = (s + frac + 0.5) * math.tau / STAVES
    sm = s % STAVES
    seam = k == 0
    jag = np.array((0.0, 1.5, 3.0, 0.7))[k] * (sm == BROKEN)   # splintered edge across the broken stave
    ztop = np.where(seam, np.minimum(top[(sm - 1) % STAVES], top[sm]), top[sm]) - jag
    ztop = np.where(seam & ((sm == BROKEN) | (sm == (BROKEN + 1) % STAVES)), ztop - 0.8, ztop)
    thick = 0.30 * (r.random(STAVES) - 0.5)
    half = math.pi / STAVES
    P = np.zeros((rows, ncol, 3))
    t_row = np.linspace(0, 1, rows)
    dent_phi = (DENTED + 0.5) * math.tau / STAVES
    for ri, tr in enumerate(t_row):
        z = tr * ztop
        R = radius(z)
        plank = R * math.cos(half) / np.cos(frac * 2 * half) + thick[sm]
        rad = np.where(seam, R - SEAM_DEPTH, plank)
        dphi = np.angle(np.exp(1j * (phi - dent_phi))) * R
        rad = rad - 0.75 * np.exp(-(dphi ** 2 + (z - 9.5) ** 2 * 1.3) / (2 * 2.3 ** 2))   # dent, 5 u across
        P[ri, :, 0] = rad * np.cos(phi)
        P[ri, :, 1] = rad * np.sin(phi)
        P[ri, :, 2] = z
    uv = np.stack((np.broadcast_to(cols[None, :] / (ncol - 1), (rows, ncol)), np.broadcast_to(t_row[:, None], (rows, ncol))), -1)
    body = grid(P, uv, smooth_normals=False)
    # rim: top face of the staves and the inner wall down to the head
    rim_out = P[-1]
    inner = rim_out * (1 - RIM / np.maximum(np.hypot(rim_out[:, 0], rim_out[:, 1]), 1e-6))[:, None]
    inner[:, 2] = rim_out[:, 2]
    strip = grid(np.stack((rim_out, inner)), None, smooth_normals=False)
    floor = np.column_stack((inner[:, 0], inner[:, 1], np.full(ncol, HEAD_Z)))
    wall = grid(np.stack((floor, inner)), None, smooth_normals=False)
    return body.merge(strip).merge(wall)


def _head() -> Mesh:
    """Recessed iron head: a dished plate with one lap joint and a ring of rivets round the rim, mapped square to
    disc. A bolted lid, not planks."""
    n = 40
    u = np.linspace(-1, 1, n)
    uu, vv = np.meshgrid(u, u)
    rin = float(radius(HEAD_Z)) - RIM - 0.25
    x = rin * uu * np.sqrt(1 - vv * vv / 2)
    y = rin * vv * np.sqrt(1 - uu * uu / 2)
    seam = np.exp(-((((x * 0.94 + y * 0.34) - 0.9) / 0.20) ** 2))
    step = 0.20 * (1 / (1 + np.exp(-((x * 0.94 + y * 0.34) - 0.9) / 0.12)))        # the second plate lies proud
    z = HEAD_Z - 0.30 * seam - 0.16 * (uu ** 2 + vv ** 2) + step
    out = grid(np.stack((x, y, z), -1), None, smooth_normals=False)
    prof = [(0.50, 0.0), (0.46, 0.14), (0.26, 0.30), (0.0, 0.36)]
    ring = rin * 0.80
    for i in range(14):
        a = math.tau * (i + 0.5) / 14
        rv = lathe(prof, sides=10).xform((0, 0, 0), (ring * math.cos(a), ring * math.sin(a), HEAD_Z - 0.16 * 0.64 + 0.02))
        out = out.merge(rv)
    return out


def _hoop(z0: float, h: float, t: float, name: str) -> Mesh:
    def ro(z):
        return float(radius(z)) + 0.10 + t

    def ri(z):
        return float(radius(z)) + 0.05

    zb, zt = z0 - h / 2, z0 + h / 2
    b = 0.34   # hammered bevel on both edges
    prof = [(ri(zb), zb), (ro(zb) - b, zb), (ro(zb), zb + b), (ro(z0) - 0.05, z0 - h / 4), (ro(z0) + 0.08, z0),
            (ro(z0) - 0.05, z0 + h / 4), (ro(zt), zt - b), (ro(zt) - b, zt), (ri(zt), zt)]
    return lathe(prof, sides=96).displace(name + ".hammer", 0.04, 2.0, 4.0).displace(name + ".warp", 0.10, 8.0, 12.0)


def _rivets(z0: float, h: float, t: float, seed: str) -> Mesh:
    """Two staggered rows of domed rivets on a hoop."""
    r = U.rng(seed)
    out = None
    count = 11
    for row, dz in enumerate((-h / 4, h / 4)):
        for i in range(count):
            a = math.tau * (i + 0.5 * row + 0.25 * r.random()) / count
            rr = float(radius(z0 + dz)) + 0.10 + t - 0.02
            prof = [(0.46, 0.0), (0.42, 0.16), (0.24, 0.34), (0.0, 0.40)]
            m = lathe(prof, sides=10).xform((0, 90, 0), (rr, 0, z0 + dz)).xform((0, 0, math.degrees(a)))
            out = m if out is None else out.merge(m)
    return out


def _strap() -> Mesh:
    """A riveted lap strap down the shadow side, over the staves and under the hoops."""
    rows = 24
    zs = np.linspace(0.6, 19.2, rows)
    w = 0.115
    cols = np.array((-w - 0.02, -w, -w + 0.02, w - 0.02, w, w + 0.02))
    lift = np.array((0.02, 0.34, 0.46, 0.46, 0.34, 0.02))
    P = np.zeros((rows, len(cols), 3))
    for i, z in enumerate(zs):
        rad = float(radius(z)) + lift - 0.05
        ph = STRAP_PHI + cols
        P[i, :, 0] = rad * np.cos(ph)
        P[i, :, 1] = rad * np.sin(ph)
        P[i, :, 2] = z
    strap = grid(P, None, smooth_normals=False)
    for z in (4.6, 8.6, 10.6, 11.4, 15.6):
        rr = float(radius(z)) + 0.46
        prof = [(0.40, 0.0), (0.36, 0.14), (0.20, 0.30), (0.0, 0.34)]
        m = lathe(prof, sides=10).xform((0, 90, 0), (rr - 0.02, 0, z)).xform((0, 0, math.degrees(STRAP_PHI)))
        strap = strap.merge(m)
    return strap


def _bung() -> Mesh:
    """Iron collar with a sunk plug, set on the wall between the two middle hoops."""
    z = 10.0
    rr = float(radius(z)) + 0.05
    prof = [(1.50, -0.5), (1.50, 0.30), (1.25, 0.46), (0.98, 0.40), (0.98, 0.06), (0.88, 0.04), (0.82, 0.20), (0.0, 0.20)]
    m = lathe(prof, sides=24).displace("iso.barrel.bung", 0.04, 2.0, 4.0)
    return m.xform((0, 90, 0), (rr, 0, z)).xform((0, 0, math.degrees(BUNG_PHI)))


def barrel_scene() -> Scene:
    sc = Scene("rust_barrel", SIZE[0], SIZE[1], PIVOT)
    plate = _staves().merge(_strap()).merge(_bung())
    sc.add(_rust_tint(plate.xform((0, 0, YAW)), 1.1, 0.85), PLATE, "barrel.plate")
    sc.add(_rust_tint(_head().xform((0, 0, YAW)), 0.6, -3.0), HEAD, "barrel.head")
    iron = None
    for i, (z0, h, t) in enumerate(HOOPS):
        m = _hoop(z0, h, t, f"iso.barrel.hoop{i}").merge(_rivets(z0, h, t, f"iso.barrel.rivets{i}"))
        iron = m if iron is None else iron.merge(m)
    sc.add(_rust_tint(iron.xform((0, 0, YAW)), 1.15, 0.4), HOOP, "barrel.iron")
    return sc


def render_barrel():
    return barrel_scene().render(wear=0.38, grime=0.6, rust=0.3, dust=0.0, exposure=1.2)


OBJECTS = {"rust_barrel": ObjectSpec(SIZE, PIVOT, (render_barrel,))}
