"""Isometric props: the rusted iron barrel, the calibration object for isoforge.

A heavy old barrel: sixteen flat staves in a bulge, each with its own height at the rim and a V seam
between neighbours, a recessed plank head, and four rolled iron hoops that shed rust down the wood.
Primary forms are the body, the hoops and the head (8 u and up), secondary the staves, seams and dents
(2..6 u), tertiary only grain and pitting (under 1 u).
"""
from __future__ import annotations

import math

import numpy as np

import uiart as U
from isoforge import Mesh, ObjectSpec, Scene, grid, lathe

HEIGHT = 20.0
R_END = 6.0
R_MID = 7.5
STAVES = 16
SEAM_DEPTH = 0.17
RIM = 0.95
HEAD_Z = HEIGHT - 2.6
SIZE = (128, 160)
PIVOT = (40, 118)
YAW = 14.0


def radius(z):
    t = 2 * np.asarray(z) / HEIGHT - 1
    return R_END + (R_MID - R_END) * (1 - t * t)


def _staves() -> Mesh:
    """One lattice around the barrel: per stave the columns are seam, chamfer, middle, chamfer; the seam column
    is shared with the next stave and sunk into a V groove. Faceted (flat normals)."""
    r = U.rng("iso.barrel.staves")
    per = 4
    ncol = STAVES * per + 1
    top = HEIGHT - 0.7 * r.random(STAVES)                  # each stave stops at its own height
    top[r.integers(0, STAVES)] -= 1.6                      # one short, broken stave
    rows = 26
    cols = np.arange(ncol)
    s = cols // per
    k = cols % per
    frac = np.array((-0.5, -0.43, 0.0, 0.43))[k]
    phi = (s + frac + 0.5) * math.tau / STAVES
    sm = s % STAVES
    seam = k == 0
    ztop = np.where(seam, np.minimum(top[(sm - 1) % STAVES], top[sm]), top[sm])
    thick = 0.05 * (r.random(STAVES) - 0.5)
    half = math.pi / STAVES
    P = np.zeros((rows, ncol, 3))
    t_row = np.linspace(0, 1, rows)
    for ri, tr in enumerate(t_row):
        z = tr * ztop
        R = radius(z)
        plank = R * math.cos(half) / np.cos(frac * 2 * half) + thick[sm]
        rad = np.where(seam, R - SEAM_DEPTH, plank)
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
    """Recessed head: five planks with sunk joints and a shallow dish, mapped square to disc."""
    n = 36
    u = np.linspace(-1, 1, n)
    uu, vv = np.meshgrid(u, u)
    rin = float(radius(HEAD_Z)) - RIM - 0.25
    x = rin * uu * np.sqrt(1 - vv * vv / 2)
    y = rin * vv * np.sqrt(1 - uu * uu / 2)
    pw = 2 * rin / 5
    d = np.abs(((x + rin) % pw) - pw / 2)
    groove = np.exp(-(((pw / 2 - d) / 0.16) ** 2))
    z = HEAD_Z - 0.22 * groove - 0.04 * (uu ** 2 + vv ** 2)
    plank_h = U.rng("iso.barrel.head").random(5) * 0.10
    z = z + plank_h[np.clip(((x + rin) // pw).astype(int), 0, 4)]
    return grid(np.stack((x, y, z), -1), None, smooth_normals=False)


def _hoop(z0: float, h: float, t: float, name: str) -> Mesh:
    def ro(z):
        return float(radius(z)) + 0.10 + t

    def ri(z):
        return float(radius(z)) + 0.05

    zb, zt = z0 - h / 2, z0 + h / 2
    prof = [(ri(zb), zb), (ro(zb) - 0.12, zb), (ro(zb), zb + 0.12), (ro(z0) + 0.06, z0), (ro(zt), zt - 0.12), (ro(zt) - 0.12, zt), (ri(zt), zt)]
    return lathe(prof, sides=112).displace(name + ".dent", 0.035, 3.0, 7.0)


def _rivets(z0: float, count: int, seed: str) -> Mesh:
    r = U.rng(seed)
    out = None
    for i in range(count):
        a = math.tau * (i + 0.5 * r.random()) / count
        rr = float(radius(z0)) + 0.10 + 0.55
        prof = [(0.40, 0.0), (0.36, 0.14), (0.20, 0.30), (0.0, 0.34)]
        m = lathe(prof, sides=10).xform((0, 90, 0), (rr, 0, z0)).xform((0, 0, math.degrees(a)))
        out = m if out is None else out.merge(m)
    return out


def barrel_scene() -> Scene:
    sc = Scene("rust_barrel", SIZE[0], SIZE[1], PIVOT)
    wood = _staves().merge(_head())
    sc.add(wood.xform((0, 0, YAW)), "wood.old", "barrel.wood")
    iron = None
    for i, (z0, h, t) in enumerate(((1.9, 2.4, 0.60), (5.8, 2.2, 0.55), (14.3, 2.2, 0.55), (18.2, 2.6, 0.60))):
        m = _hoop(z0, h, t, f"iso.barrel.hoop{i}")
        iron = m if iron is None else iron.merge(m)
    for z0 in (5.8, 14.3):
        iron = iron.merge(_rivets(z0, 7, f"iso.barrel.rivets{z0}"))
    sc.add(iron.xform((0, 0, YAW)), "iron.rust", "barrel.iron")
    return sc


def render_barrel():
    return barrel_scene().render(wear=0.32, grime=0.65, rust=0.45, dust=0.06, exposure=0.95)


OBJECTS = {"rust_barrel": ObjectSpec(SIZE, PIVOT, (render_barrel,))}
