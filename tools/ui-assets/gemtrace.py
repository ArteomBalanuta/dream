"""Ray-traced faceted stone for the HUD gems.

An original seven-fold cut (crown, upper girdle, girdle, lower girdle and pavilion, about 57 half-space
planes) is tilted so the table sits off centre, then traced with orthographic primary rays: exact Fresnel
at every interface, total internal reflection, Beer-Lambert absorption per channel, glow along the paths
under the liquid, up to 18 bounces. Facets are very slightly convex, so a reflection grades across a
facet instead of flooding it. The light is a dark room with a few soft boxes, a low candle strip, a cold
rim and a scatter of small lights that become the glints.

Everything is deterministic: the room and the cut are drawn once from a fixed generator, shading has no
random terms, and all intersection maths is elementwise float32 (no BLAS), so tile order and process
count cannot change a byte. Tiles of rows go through a ProcessPoolExecutor in fixed order.

render_set(kind) returns the four sprites the runtime bakes into one gem, uint8 RGBA at 200x200:
  full   stone filled to the top, tonemapped
  empty  the same geometry as dark glass with a little residue at the bottom
  mask   R refraction-warped fill threshold (0 at the bottom of the stone, 1 at the top: a pixel is filled
         when the level is above its R), G meniscus weight, B facet id, A silhouette
  spec   hot facet glints only, in RGB, with the shared silhouette in A
"""
from __future__ import annotations

import math
import os
from concurrent.futures import ProcessPoolExecutor

import numpy as np

import forge
import uiart as U

F32 = np.float32
SIZE_PX = 200          # sprite size at atlas density
IOR = 1.76
BOUNCES = 18


def _smooth(a, b, x):
    t = np.clip((x - a) / (b - a), 0, 1)
    return t * t * (3 - 2 * t)


def _rot(ax: str, a: float) -> np.ndarray:
    c, s = math.cos(a), math.sin(a)
    return {
        "x": np.array([[1, 0, 0], [0, c, -s], [0, s, c]]),
        "y": np.array([[c, 0, s], [0, 1, 0], [-s, 0, c]]),
        "z": np.array([[c, -s, 0], [s, c, 0], [0, 0, 1]]),
    }[ax]


# the cut --------------------------------------------------------------------------------

CONVEX = 0.11


def cut_planes(mirror: bool = False, convex: float = CONVEX) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    """Unit normals (P,3), offsets (P,) and per-facet convexity (P,). mirror flips x for the second stone."""
    rs = np.random.default_rng(20261101 ^ 0x6E3F)
    n, d, kc = [], [], []

    def add(tilt, az, dist, k=1.0):
        t = math.radians(tilt + rs.normal(0, 1.4))
        a = math.radians(az + rs.normal(0, 2.4))
        n.append((math.sin(t) * math.cos(a), math.sin(t) * math.sin(a), math.cos(t)))
        d.append(dist * (1 + rs.normal(0, 0.014)))
        kc.append(convex * k * (0.6 + 0.8 * rs.random()))

    add(0, 0, 0.38, 2.6)                                    # table
    for i in range(7):
        add(32, i * 360 / 7 + 8, 0.64)                      # crown mains, seven-fold so it is no pinwheel
    for i in range(14):
        add(48, i * 360 / 14 + 3, 0.80)                     # upper girdle
    for i in range(14):
        add(88, i * 360 / 14, 0.97, 0.6)                    # girdle
    for i in range(7):
        add(139, i * 360 / 7 + 30, 0.70)                    # pavilion mains
    for i in range(14):
        add(127, i * 360 / 14 + 12, 0.80)                   # lower girdle
    N = np.array(n, dtype=np.float64)
    D = np.array(d, dtype=np.float64)
    R = _rot("x", math.radians(-21)) @ _rot("y", math.radians(18)) @ _rot("z", math.radians(9))
    N = N @ R.T
    sc = np.array([1.0, 0.95, 0.92])                         # a little squashed: cushion, not a disc
    N = N / sc
    ln = np.linalg.norm(N, axis=1)
    N, D = N / ln[:, None], D / ln
    if mirror:
        N = N * np.array([-1.0, 1.0, 1.0])
    return N.astype(F32), D.astype(F32), np.array(kc, dtype=F32)


# the room -------------------------------------------------------------------------------


def _basis(c, rot_deg=0.0):
    c = np.array(c, dtype=np.float64)
    c /= np.linalg.norm(c)
    a = np.cross(c, (0, 1, 0))
    a /= np.linalg.norm(a)
    b = np.cross(a, c)
    r = math.radians(rot_deg)
    a, b = a * math.cos(r) + b * math.sin(r), b * math.cos(r) - a * math.sin(r)
    return c.astype(F32), a.astype(F32), b.astype(F32)


# soft boxes: centre direction, half extents (on the unit sphere), softness, colour, power, vertical
# gradient inside the box (0 flat .. 1 dark to bright, so a reflection of it never floods a facet flat),
# and a turn in degrees so a strip can run along the stone's upper-left rim
PANELS = (
    ((-0.46, 0.64, 0.62), 0.34, 0.045, 0.05, (1.00, 0.92, 0.84), 3.2, 0.5, 40),   # the strip light, upper left
    ((0.48, 0.58, 0.66), 0.30, 0.03, 0.04, (1.00, 0.95, 0.90), 1.9, 0.4, 25),     # a second strip, found by the table
    ((-0.52, 0.58, 0.62), 0.15, 0.10, 0.10, (1.00, 0.90, 0.80), 1.1, 0.85, 0),    # the window, upper left
    ((0.10, 0.93, 0.36), 0.55, 0.10, 0.20, (0.92, 0.96, 1.00), 0.55, 0.0, 0),     # overhead strip
    ((0.80, -0.12, 0.58), 0.10, 0.34, 0.22, (1.00, 0.62, 0.30), 1.2, 0.3, 0),     # candle, low right
    ((-0.72, -0.40, 0.56), 0.26, 0.20, 0.20, (0.70, 0.80, 1.00), 0.35, 0.0, 0),   # cold fill, lower left
    ((0.45, -0.78, 0.43), 0.30, 0.12, 0.20, (1.00, 0.75, 0.55), 0.50, 0.0, 0),    # warm bounce, below
)


def make_room(mirror: bool = False) -> dict:
    """Lamps and glints in direction space, +z towards the viewer, y up. Fixed draw.

    Lamps are mid-size lights that only feed light seen through the stone, so they build the pattern of
    bright and dark facets without flooding a surface reflection white. Glints are small hot lights; they
    show on the surface, inside, and in the spec layer. mirror flips the lights to the other side (the
    right-hand stone), with a different scatter of lamps and glints so the two stones are not twins."""
    rl = np.random.default_rng(20261101 ^ (0x51C7 if not mirror else 0x7A93))
    nl, ng = 30, 80
    k = nl + ng
    dirs = rl.normal(size=(k, 3))
    dirs[:, 2] = np.abs(dirs[:, 2]) + 0.1             # most of them in front, so reflections can find them
    dirs /= np.linalg.norm(dirs, axis=1, keepdims=True)
    rad = np.concatenate([np.radians(2.5 + 3.5 * rl.random(nl)), np.radians(0.7 + 1.6 * rl.random(ng) ** 1.5)])
    power = np.concatenate([2.0 + 7.0 * rl.random(nl) ** 1.5, 4.0 + 22.0 * rl.random(ng) ** 2.2])
    tint = np.ones((k, 3))
    warm = rl.random(k) < 0.28
    tint[warm] = (1.0, 0.80, 0.55)
    cold = rl.random(k) < 0.15
    tint[cold] = (0.78, 0.88, 1.0)
    panels = []
    flip = np.array([-1.0 if mirror else 1.0, 1.0, 1.0])
    for c, hx, hy, soft, col, power_p, grad, rot in PANELS:
        cc, ax, ay = _basis(np.array(c) * flip, -rot if mirror else rot)
        panels.append({"c": cc, "ax": ax, "ay": ay, "hx": hx, "hy": hy, "soft": soft, "col": np.array(col, F32), "power": power_p, "grad": grad})
    dirs = dirs * flip
    lamp = np.zeros(k, F32)
    lamp[:nl] = 1
    return {
        "dirs": dirs.astype(F32), "cos": np.cos(rad).astype(F32), "power": power.astype(F32), "tint": tint.astype(F32),
        "lamp": lamp, "panels": panels,
    }


def _panel(v, p):
    x = v[:, 0] * p["ax"][0] + v[:, 1] * p["ax"][1] + v[:, 2] * p["ax"][2]
    y = v[:, 0] * p["ay"][0] + v[:, 1] * p["ay"][1] + v[:, 2] * p["ay"][2]
    z = v[:, 0] * p["c"][0] + v[:, 1] * p["c"][1] + v[:, 2] * p["c"][2]
    m = (1 - _smooth(p["hx"], p["hx"] + p["soft"], np.abs(x))) * (1 - _smooth(p["hy"], p["hy"] + p["soft"], np.abs(y))) * (z > 0)
    if p["grad"]:
        m = m * (1 - p["grad"] + p["grad"] * _smooth(-p["hy"], p["hy"], y))
    return m


def env_parts(v: np.ndarray, room: dict, env: dict) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    """(broad, lamp, glint) radiance for unit directions v (m,3)."""
    broad = np.zeros((len(v), 3), F32) + F32(0.012)
    # a dim sky, brighter overhead, with a dark hole round the viewer so the table shows the pavilion's pattern
    ang = np.arccos(np.clip(v[:, 2], -1, 1))
    hole = 1 - 0.6 * np.exp(-((ang / 0.30) ** 2))
    sky = (0.10 + 0.9 * _smooth(-0.25, 0.9, v[:, 1])) * hole * np.where(v[:, 2] > -0.2, 1.0, 0.25)
    broad += (sky * env["sky"])[:, None] * np.array([0.96, 0.97, 1.0], F32)[None]
    for p in room["panels"]:
        broad += (_panel(v, p) * p["power"] * env["panel"])[:, None] * p["col"][None]
    r = _smooth(0.2, 0.85, -v[:, 2]) * (1 - _smooth(0.25, 0.7, np.abs(v[:, 1] - 0.05)))
    broad += r[:, None] * np.array([0.10, 0.16, 0.34], F32)[None] * env["rim"]
    d0 = v[:, 0:1] * room["dirs"][:, 0][None] + v[:, 1:2] * room["dirs"][:, 1][None] + v[:, 2:3] * room["dirs"][:, 2][None]
    lobe = np.clip((d0 - room["cos"][None]) / (1 - room["cos"][None]), 0, 1) ** 0.7 * room["power"][None]
    lamps = lobe * room["lamp"][None]
    glints = lobe * (1 - room["lamp"])[None]
    lamp = np.stack([(lamps * room["tint"][:, c_][None]).sum(1) for c_ in range(3)], axis=-1) * F32(env["lamp"])
    glint = np.stack([(glints * room["tint"][:, c_][None]).sum(1) for c_ in range(3)], axis=-1) * F32(env["glint"])
    return broad.astype(F32), lamp.astype(F32), glint.astype(F32)


# tracing --------------------------------------------------------------------------------


def _dots(v: np.ndarray, N3: tuple) -> np.ndarray:
    """(m,3) . planes -> (m,P), elementwise, no BLAS."""
    out = v[:, 0:1] * N3[0][None]
    out += v[:, 1:2] * N3[1][None]
    out += v[:, 2:3] * N3[2][None]
    return out


def _shade_normal(p, j, N, D, K):
    """Slightly convex facet: normal leans away from the facet's foot point."""
    nj = N[j]
    q = p - nj * D[j][:, None]
    n = nj + K[j][:, None] * q
    return n / np.linalg.norm(n, axis=1, keepdims=True)


def _tile(job: dict) -> dict:
    cfg = job["cfg"]
    N, D, K = cfg["N"], cfg["D"], cfg["K"]
    N3 = (N[:, 0].copy(), N[:, 1].copy(), N[:, 2].copy())
    W = SIZE_PX * cfg["S"]
    y0, y1 = job["rows"]
    ys, xs = np.mgrid[y0:y1, 0:W].astype(F32)
    cx, cy, sc = cfg["win"]["cx"], cfg["win"]["cy"], cfg["win"]["scale"]
    px = (xs.ravel() + 0.5 - W / 2) / (W / 2) * sc + cx
    py = -(ys.ravel() + 0.5 - W / 2) / (W / 2) * sc + cy
    m = px.size
    o = np.stack([px, py, np.full(m, 4.0, F32)], -1).astype(F32)
    v = np.tile(np.array([0, 0, -1], F32), (m, 1))
    dn = _dots(v, N3)
    on = _dots(o, N3)
    tn = (D[None] - on) / np.where(np.abs(dn) < 1e-6, 1e-6, dn)
    tin = np.where(dn < 0, tn, -1e9).max(1)
    ida = np.where(dn < 0, tn, -1e9).argmax(1)
    tout = np.where(dn > 0, tn, 1e9).min(1)
    hit = tin < tout
    del dn, on, tn
    rgb = np.zeros((m, 3), F32)
    spark = np.zeros((m, 3), F32)
    depth = np.zeros(m, F32)
    ydepth = np.zeros(m, F32)
    fid = np.zeros(m, np.uint16)
    idx = np.nonzero(hit)[0]
    res = {"rows": (y0, y1), "rgb": rgb, "spark": spark, "alpha": hit.astype(F32), "depth": depth, "ydepth": ydepth, "fid": fid}
    if not len(idx):
        return res
    room = cfg["room"]
    env = cfg["env"]
    sig, emit, residue = cfg["sigma"], cfg["emit"], cfg["residue"]
    level = cfg["level"]
    p = o[idx] + v[idx] * tin[idx, None]
    j0 = ida[idx]
    fid[idx] = j0.astype(np.uint16)
    nh = _shade_normal(p, j0, N, D, K)
    d = v[idx]
    eta = 1.0 / IOR
    c = -(d * nh).sum(1, keepdims=True)
    k = 1 - eta * eta * (1 - c * c)
    t = eta * d + (eta * c - np.sqrt(k)) * nh
    t /= np.linalg.norm(t, axis=1, keepdims=True)
    F0 = ((IOR - 1) / (IOR + 1)) ** 2
    Fr = F0 + (1 - F0) * (1 - c) ** 5
    rd = d + 2 * c * nh
    b0, l0, g0 = env_parts(rd, room, env)
    g0 = np.minimum(g0, cfg["surf_glint_cap"])      # a surface glint is a point of light, never a lit facet
    rgb[idx] += Fr * (b0 * cfg["surf_broad"] + g0)
    spark[idx] += Fr * g0
    w = np.broadcast_to((1 - Fr), (len(idx), 3)).astype(F32).copy() * np.exp(-sig["full"] * cfg["min_path"])[None]
    q, dirn, ids = p, t, idx
    dcount = np.zeros(m, F32)
    ydepth[idx] = p[:, 1]
    for bounce in range(BOUNCES):
        dn = _dots(dirn, N3)
        on = _dots(q, N3)
        tn = (D[None] - on) / np.where(np.abs(dn) < 1e-6, 1e-6, dn)
        tn = np.where(dn > 1e-6, tn, 1e9)
        j = tn.argmin(1)
        L = np.maximum(tn[np.arange(len(j)), j], 0)
        q2 = q + dirn * L[:, None]
        if bounce < 2:  # level map: world height where the first two segments end
            depth[ids] += q2[:, 1]
            dcount[ids] += 1
        y0_, y1_ = q[:, 1], q2[:, 1]
        lo, hi = np.minimum(y0_, y1_), np.maximum(y0_, y1_)
        fl = np.clip((level - lo) / np.maximum(hi - lo, 1e-5), 0, 1)
        Lf = L * fl
        Le = L - Lf
        tr = np.exp(-(sig["full"][None] * Lf[:, None] + sig["empty"][None] * Le[:, None]))
        gl = emit[None] * Lf[:, None] + residue[None] * (Le * _smooth(0.25, -0.9, q2[:, 1]))[:, None]
        rgb[ids] += w * gl * (tr ** 0.5)
        w = w * tr
        nh = -_shade_normal(q2, j, N, D, K)
        c = np.clip(-(dirn * nh).sum(1, keepdims=True), 0, 1)
        e2 = IOR
        k = 1 - e2 * e2 * (1 - c * c)
        tir = k[:, 0] < 0
        ks = np.sqrt(np.maximum(k, 0))
        rs_ = (e2 * c - ks) / (e2 * c + ks + 1e-6)
        rp = (c - e2 * ks) / (c + e2 * ks + 1e-6)
        Fi = np.where(tir[:, None], 1.0, 0.5 * (rs_ * rs_ + rp * rp))
        tdir = e2 * dirn + (e2 * c - ks) * nh
        ex = ~tir
        if ex.any():
            tdn = tdir[ex] / np.maximum(np.linalg.norm(tdir[ex], axis=1, keepdims=True), 1e-6)
            bb, ll, gg = env_parts(tdn, room, env)
            rgb[ids[ex]] += w[ex] * (1 - Fi[ex]) * (bb + ll + gg)
            spark[ids[ex]] += w[ex] * (1 - Fi[ex]) * gg
        w = w * Fi
        dirn = dirn + 2 * c * nh
        q = q2 + nh * 1e-4
        keep = w.max(1) > 0.003
        q, dirn, w, ids = q[keep], dirn[keep], w[keep], ids[keep]
        if not len(ids):
            break
    depth[idx] = depth[idx] / np.maximum(dcount[idx], 1.0)
    return res


def _run(cfg: dict, workers: int | None = None) -> dict:
    H = SIZE_PX * cfg["S"]
    step = max(8, H // 24)
    jobs = [{"cfg": cfg, "rows": (a, min(a + step, H))} for a in range(0, H, step)]
    workers = workers if workers is not None else min(os.cpu_count() or 1, 12)
    if workers <= 1:
        parts = [_tile(j) for j in jobs]
    else:
        with ProcessPoolExecutor(max_workers=workers) as ex:
            parts = list(ex.map(_tile, jobs))
    out = {}
    for key in ("rgb", "spark"):
        out[key] = np.concatenate([p[key].reshape(p["rows"][1] - p["rows"][0], H, 3) for p in parts], axis=0)
    for key in ("alpha", "depth", "ydepth", "fid"):
        out[key] = np.concatenate([p[key].reshape(p["rows"][1] - p["rows"][0], H) for p in parts], axis=0)
    return out


# stones ---------------------------------------------------------------------------------

# Absorption per channel (per world unit; the stone is about 2 across), glow of the liquid, and the
# residue left in the empty glass. exposure scales the traced light before the filmic curve.
STONES = {
    "hp": dict(
        sigma_full=(0.50, 4.6, 5.6), sigma_empty=(2.6, 3.4, 3.6), emit=(0.15, 0.004, 0.001), residue=(0.04, 0.0, 0.0),
        surf=0.4, sky=0.30, panel=1.0, rim=1.0, lamp=1.6, glint=1.0, exposure=1.35, exposure_empty=1.0,
        glint_cap=0.35, facet_trim={4: 0.62}, edge=0.12, form=0.8, form_empty=0.5, bloom=0.7, hot=(1.0, 0.90, 0.82), hot_amount=0.85, facet_lo=0.14, facet_gain=1.75,
        flares=((0.27, 0.27, 0.14, 1.6), (0.70, 0.66, 0.09, 0.9), (0.36, 0.78, 0.07, 0.7)), flare_tint=(1.0, 0.82, 0.74),
    ),
    "mp": dict(
        sigma_full=(6.4, 2.1, 0.30), sigma_empty=(4.2, 3.2, 1.6), emit=(0.002, 0.034, 0.20), residue=(0.0, 0.004, 0.05),
        surf=0.4, sky=0.30, panel=1.0, rim=1.0, lamp=1.6, glint=1.0, exposure=1.7, exposure_empty=1.0,
        glint_cap=0.35, facet_trim={4: 0.62}, edge=0.12, form=0.8, form_empty=0.5, bloom=0.7, hot=(0.86, 0.95, 1.0), hot_amount=0.85, hot_start=0.20, facet_lo=0.18, facet_gain=1.7,
        flares=((0.27, 0.27, 0.14, 1.6), (0.70, 0.66, 0.09, 0.9), (0.36, 0.78, 0.07, 0.7)), flare_tint=(0.80, 0.90, 1.0),
    ),
}


def _fit_window(N, D) -> tuple[float, float, float]:
    """Centre and half extent of the silhouette, from a coarse alpha pass."""
    n = 200
    xs = (np.arange(n, dtype=F32) + 0.5) / n * 3.0 - 1.5
    X, Y = np.meshgrid(xs, -xs)
    o = np.stack([X.ravel(), Y.ravel(), np.full(n * n, 4.0, F32)], -1)
    v = np.tile(np.array([0, 0, -1], F32), (n * n, 1))
    N3 = (N[:, 0].copy(), N[:, 1].copy(), N[:, 2].copy())
    dn, on = _dots(v, N3), _dots(o, N3)
    tn = (D[None] - on) / np.where(np.abs(dn) < 1e-6, 1e-6, dn)
    hit = np.where(dn < 0, tn, -1e9).max(1) < np.where(dn > 0, tn, 1e9).min(1)
    px, py = o[hit, 0], o[hit, 1]
    cx, cy = (px.min() + px.max()) / 2, (py.min() + py.max()) / 2
    half = max(px.max() - px.min(), py.max() - py.min()) / 2
    return float(cx), float(cy), float(half * SIZE_PX / (SIZE_PX - 2 * 4))


def make_cfg(kind: str, state: str, S: int, tune: dict | None = None) -> dict:
    """state is "full" (liquid to the top) or "empty" (dark glass)."""
    st = {**STONES[kind], **(tune or {})}
    mirror = kind == "mp"
    N0, D0, _ = cut_planes(False)
    cx, cy, sc = _fit_window(N0, D0)
    N, D, K = cut_planes(mirror, st.get("convex", CONVEX))
    if mirror:
        cx = -cx
    full = state == "full"
    return {
        "S": S, "N": N, "D": D, "K": K, "room": make_room(mirror), "win": {"cx": cx, "cy": cy, "scale": sc},
        "sigma": {"full": np.array(st["sigma_full"], F32), "empty": np.array(st["sigma_full"] if full else st["sigma_empty"], F32)},
        "emit": np.array(st["emit"], F32), "residue": np.array((0, 0, 0) if full else st["residue"], F32),
        "min_path": 0.0 if not full else float(st.get("min_path", 0.3)), "level": 9.0 if full else -9.0, "surf_broad": F32(st["surf"]), "surf_glint_cap": F32(st["glint_cap"]),
        "env": {"sky": st["sky"], "panel": st["panel"], "rim": st["rim"], "lamp": st["lamp"], "glint": st["glint"]},
    }


def trace(kind: str, state: str, S: int = 4, workers: int | None = None, tune: dict | None = None) -> dict:
    return _run(make_cfg(kind, state, S, tune), workers)


# bake -----------------------------------------------------------------------------------


def _luma(x: np.ndarray) -> np.ndarray:
    return 0.2126 * x[..., 0] + 0.7152 * x[..., 1] + 0.0722 * x[..., 2]


def _value_map(alpha: np.ndarray, S: int, strength: float) -> np.ndarray:
    """Large form: the key from the upper left lights the stone, the lower right and the rim fall away."""
    H = alpha.shape[0]
    ys, xs = np.mgrid[0:H, 0:H].astype(F32)
    x, y = (xs / H - 0.5) * 2, (ys / H - 0.5) * 2
    key = np.clip(0.5 - 0.5 * (x * 0.62 + y * 0.78), 0, 1)           # 1 at the upper left, 0 at the lower right
    inner = forge.blur(alpha, 5.0 * S)                                  # 1 inside, falling to the rim
    rim = 1 - _smooth(0.55, 0.98, inner)
    vm = (1 - strength) + strength * (0.55 + 0.9 * key)
    return (vm * (1 - 0.45 * rim)).astype(F32)


def _grade(rgba: np.ndarray, hot: tuple, amount: float, start: float = 0.34) -> np.ndarray:
    """Hot highlights drift towards a warm or cold white, like light burning through the stone."""
    y = _luma(rgba[..., :3])
    t = _smooth(start, 0.85, y) * amount
    rgba = rgba.copy()
    rgba[..., :3] = rgba[..., :3] * (1 - t[..., None]) + np.array(hot, F32) * t[..., None]
    return rgba


def _facet_shade(t: dict, kind: str, st: dict) -> np.ndarray:
    """Per-facet light, so the big planes alternate bright and dark the way a lit cut does: facets that face the
    key are open, the ones that turn away fall dark, and each facet wanders a little from its neighbours."""
    N, _, _ = cut_planes(kind == "mp", st.get("convex", CONVEX))
    key = np.array((-0.50, 0.58, 0.64), np.float64)
    if kind == "mp":
        key[0] = -key[0]
    key /= np.linalg.norm(key)
    lam = np.clip(N.astype(np.float64) @ key, 0, 1)
    jit = 1 + 0.50 * (np.random.default_rng(20261101 ^ 0x2B6D).random(len(N)) - 0.5)
    f = (st["facet_lo"] + st["facet_gain"] * lam ** 1.15) * jit
    for k_, v_ in st.get("facet_trim", {}).items():   # the facet that squares up to the key would otherwise burn flat
        f[k_] *= v_
    return f.astype(F32)[t["fid"]]


def _flares(shape: tuple, S: int, spots: tuple, mirror: bool) -> np.ndarray:
    """Four-point star flares on a few chosen facet corners, in stone-relative coordinates (0..1), as a 0..1
    pattern: a hot core, a long thin cross and a shorter diagonal cross."""
    H = shape[0]
    ys, xs = np.mgrid[0:H, 0:H].astype(F32)
    out = np.zeros((H, H), F32)
    for (u, v, size, power) in spots:
        cx, cy = (1 - u if mirror else u) * H, v * H
        dx, dy = xs - cx, ys - cy
        r = size * H
        core = np.exp(-(dx * dx + dy * dy) / (2 * (0.012 * H) ** 2))
        cross = np.exp(-(dy * dy) / (2 * (0.0045 * H) ** 2)) * np.exp(-np.abs(dx) / (0.22 * r))
        cross += np.exp(-(dx * dx) / (2 * (0.0045 * H) ** 2)) * np.exp(-np.abs(dy) / (0.22 * r))
        a, b = (dx + dy) * 0.7071, (dx - dy) * 0.7071
        diag = np.exp(-(b * b) / (2 * (0.004 * H) ** 2)) * np.exp(-np.abs(a) / (0.10 * r))
        diag += np.exp(-(a * a) / (2 * (0.004 * H) ** 2)) * np.exp(-np.abs(b) / (0.10 * r))
        out += power * (1.6 * core + 0.55 * cross + 0.22 * diag)
    return out


def _tone(t: dict, kind: str, state: str, S: int) -> np.ndarray:
    st = STONES[kind]
    vm = _value_map(t["alpha"], S, st["form"] if state == "full" else st["form_empty"])
    hdr = t["rgb"] * (st["exposure"] if state == "full" else st["exposure_empty"])
    shade = _facet_shade(t, kind, st)
    hdr = hdr * shade[..., None]
    fid = t["fid"]
    edge = np.zeros(fid.shape, np.float32)
    edge[1:] += fid[1:] != fid[:-1]
    edge[:, 1:] += fid[:, 1:] != fid[:, :-1]
    edge = forge.blur(np.minimum(edge, 1.0), 0.55 * S) * 2.2
    H_ = fid.shape[0]
    yy_, xx_ = np.mgrid[0:H_, 0:H_].astype(F32)
    lit = np.clip(0.5 - 0.5 * ((xx_ / H_ - 0.5) * 2 * (0.62 if kind == "hp" else -0.62) + (yy_ / H_ - 0.5) * 2 * 0.78), 0, 1)
    hdr = hdr + (np.clip(edge, 0, 1) * shade * (0.15 + 1.1 * lit ** 1.5) * st["edge"] * (1.0 if state == "full" else 0.45))[..., None] * np.array(st["flare_tint"], F32)[None, None]
    if state == "full" and st.get("flares"):
        fl = _flares(t["alpha"].shape, S, st["flares"], kind == "mp")
        hdr = hdr + fl[..., None] * np.array(st["flare_tint"], F32)[None, None]
    rgba = forge.finish(hdr, t["alpha"], 1.0, vm, bloom=st["bloom"], grade=False)
    return _grade(rgba, st["hot"], st["hot_amount"], st.get("hot_start", 0.34)) if state == "full" else rgba


def _down_data(a: np.ndarray, alpha: np.ndarray, S: int) -> np.ndarray:
    """Alpha-weighted box average of a data channel, 0..1."""
    H = alpha.shape[0] // S
    aw = alpha.reshape(H, S, H, S).sum(axis=(1, 3))
    v = (a * alpha).reshape(H, S, H, S).sum(axis=(1, 3))
    return v / np.maximum(aw, 1e-6)


def level_map(t: dict) -> np.ndarray:
    """R: 0 at the lowest point of the stone, 1 at the highest, bent by what the light does inside."""
    hit = t["alpha"] > 0.5
    lo, hi = np.percentile(t["ydepth"][hit], 0.3), np.percentile(t["ydepth"][hit], 99.7)
    yn = np.clip((t["ydepth"] - lo) / (hi - lo), 0, 1)
    dn = np.clip((t["depth"] - lo) / (hi - lo), 0, 1)
    r = np.clip(yn + 0.30 * np.clip(dn - yn, -0.25, 0.25), 0, 1)
    r = (r - r[hit].min()) / max(float(r[hit].max() - r[hit].min()), 1e-6)
    return (0.02 + 0.96 * r).astype(F32)


def render_set(kind: str, S: int = 4, workers: int | None = None) -> dict[str, np.ndarray]:
    """The four gem sprites as uint8 RGBA at 200x200 (see the module docstring)."""
    full = trace(kind, "full", S, workers)
    empty = trace(kind, "empty", S, workers)
    alpha = full["alpha"]
    out = {}
    c_full = _tone(full, kind, "full", S)
    c_empty = _tone(empty, kind, "empty", S)
    a8 = np.clip(alpha.reshape(SIZE_PX, S, SIZE_PX, S).mean(axis=(1, 3)) * 255 + 0.5, 0, 255).astype(np.uint8)
    for name, img in (("full", c_full), ("empty", c_empty)):
        px = forge.downsample(img, factor=S)
        px[..., 3] = a8
        px[..., :3][a8 == 0] = 0
        out[name] = px
    # mask: R level threshold, G meniscus weight, B facet phase, A silhouette
    lum = _luma(c_full[..., :3])
    ref = np.percentile(lum[alpha > 0.5], 92)
    rim = 1 - _smooth(0.55, 0.98, forge.blur(alpha, 5.0 * S))
    g = np.clip((0.25 + 0.95 * lum / max(float(ref), 1e-3)) * (1 - 0.6 * rim), 0, 1)
    b = (full["fid"].astype(np.float64) * 0.6180339887 % 1.0).astype(F32)
    mk = np.zeros((SIZE_PX, SIZE_PX, 4), np.uint8)
    mk[..., 0] = np.clip(_down_data(level_map(full), alpha, S) * 255 + 0.5, 0, 255)
    mk[..., 1] = np.clip(_down_data(g.astype(F32), alpha, S) * 255 + 0.5, 0, 255)
    c = S // 2
    mk[..., 2] = np.clip(b[c::S, c::S] * 255 + 0.5, 0, 255)
    mk[..., 3] = a8
    out["mask"] = mk
    # spec: hot glints only
    st = STONES[kind]
    sp = np.maximum(full["spark"] - 0.22, 0)
    glint = 1 - np.exp(-sp * 1.6)
    peak = glint.max(axis=2, keepdims=True)
    hot = glint * 0.4 + np.array(st["flare_tint"], F32)[None, None] * peak * 0.6   # glints burn white, whatever the stone
    if st.get("flares"):
        hot = hot + _flares(alpha.shape, S, st["flares"], kind == "mp")[..., None] * np.array(st["flare_tint"], F32)[None, None] * 0.5
    rgb = U.linear_to_srgb(np.clip(hot, 0, 1))
    sp_img = np.dstack((rgb, alpha)).astype(F32)
    px = forge.downsample(sp_img, factor=S, sharpen=0.0, dither=False)
    px[..., 3] = a8
    px[..., :3][a8 == 0] = 0
    out["spec"] = px
    return out
