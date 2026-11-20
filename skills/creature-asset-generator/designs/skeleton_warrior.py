"""
Design: Skeleton Warrior - a hunched undead soldier in rusted pauldrons, mismatched steel
leg armour and a tattered blue-grey tunic. Original design in a realistic dark-fantasy style.

Units meters. Y up, +Z = facing direction, +X = character's LEFT. Height ~1.82 m (hunched).
The rest pose IS the idle stance (hunched, knees soft, right leg forward, arms hanging).
"""
import math
import numpy as np
from creaturelib import (Prim, group, basis, nrm, nrm_rows, rot_x, rot_z, smoothstep, wave_noise,
                         wave_noise_grad, strand_field, quat_axis, quat_mul)

NAME = "SkeletonWarrior"
TITLE = "Skeleton Warrior"
SETTINGS = dict(voxel=0.0024, tris=26000, tex=2048,
                preview=dict(voxel=0.0042, tris=14000, tex=1024))

V3 = lambda *a: np.array(a, float)

# ----------------------------------------------------------------------------- landmarks (pose)
HEAD = V3(0, 1.630, 0.170)                 # cranium centre
JAW_PIVOT = HEAD + V3(0, -0.045, -0.005)
JAW_OPEN = 0.30
SPINE_CTRL = [V3(0, 1.040, -0.060), V3(0, 1.220, -0.075), V3(0, 1.400, -0.115), V3(0, 1.495, 0.035)]
HIP = {1: V3(0.100, 0.950, -0.020), -1: V3(-0.100, 0.950, -0.020)}
KNEE = {1: V3(0.110, 0.515, -0.010), -1: V3(-0.120, 0.520, 0.110)}
ANKLE = {1: V3(0.130, 0.100, -0.120), -1: V3(-0.120, 0.100, 0.030)}
SHOULDER = {1: V3(0.198, 1.425, 0.040), -1: V3(-0.198, 1.425, 0.040)}
ELBOW = {1: V3(0.245, 1.160, 0.085), -1: V3(-0.245, 1.160, 0.035)}
WRIST = {1: V3(0.170, 0.930, 0.170), -1: V3(-0.220, 0.920, 0.080)}
HAND_DIR = {1: nrm([-0.35, -1.0, 0.25]), -1: nrm([0.05, -1.0, 0.12])}
SIDE = {1: "L", -1: "R"}


def bez(t):
    p0, p1, p2, p3 = SPINE_CTRL
    u = 1 - t
    return u ** 3 * p0 + 3 * u * u * t * p1 + 3 * u * t * t * p2 + t ** 3 * p3


def bez_tan(t, e=1e-3):
    return nrm(bez(min(t + e, 1)) - bez(max(t - e, 0)))


def rot_about_x(p, pivot, a):
    q = np.asarray(p, float) - pivot
    c, s = math.cos(a), math.sin(a)
    return pivot + np.array([q[0], q[1] * c - q[2] * s, q[1] * s + q[2] * c])


# ----------------------------------------------------------------------------- skeleton (rig)
def _bones():
    B = [
        ("Root", None, (0, 0, 0)),
        ("Hips", "Root", (0, 0.980, -0.030)),
        ("Spine1", "Hips", tuple(bez(0.25))),
        ("Spine2", "Spine1", tuple(bez(0.55))),
        ("Chest", "Spine2", tuple(bez(0.80))),
        ("Neck", "Chest", tuple(bez(1.0))),
        ("Head", "Neck", (0, 1.578, 0.118)),
        ("Jaw", "Head", tuple(JAW_PIVOT)),
    ]
    for sx, s in SIDE.items():
        B += [
            (f"Clavicle.{s}", "Chest", (sx * 0.030, 1.440, 0.050)),
            (f"Pauldron.{s}", f"Clavicle.{s}", tuple(SHOULDER[sx] + V3(0, 0.03, 0))),
            (f"UpperArm.{s}", f"Clavicle.{s}", tuple(SHOULDER[sx])),
            (f"Forearm.{s}", f"UpperArm.{s}", tuple(ELBOW[sx])),
            (f"Hand.{s}", f"Forearm.{s}", tuple(WRIST[sx])),
            (f"Thigh.{s}", "Hips", tuple(HIP[sx])),
            (f"Shin.{s}", f"Thigh.{s}", tuple(KNEE[sx])),
            (f"Foot.{s}", f"Shin.{s}", tuple(ANKLE[sx])),
        ]
    return B


BONES = _bones()

# ----------------------------------------------------------------------------- primitive helpers
def E(c, r, bone, k=0.004, tag="bone", R=None, flow=(0, -1, 0), **kw):
    return Prim("ellipsoid", bone, k, tag=tag, flow=flow, c=c, r=r, R=np.eye(3) if R is None else R, **kw)


def C(a, b, ra, rb, bone, k=0.003, tag="bone", flow=None, **kw):
    f = nrm(np.asarray(b, float) - np.asarray(a, float)) if flow is None else flow
    return Prim("cone", bone, k, tag=tag, flow=f, a=a, b=b, ra=ra, rb=rb, **kw)


def S(c, r, bone, k=0.003, tag="bone", **kw):
    return Prim("sphere", bone, k, tag=tag, flow=(0, -1, 0), c=c, r=r, **kw)


def SUB(kind, k=0.002, **p):
    return Prim(kind, None, k, op="sub", **p)


def long_bone(a, b, r_shaft, r_a, r_b, bone, inset=0.25):
    a, b = np.asarray(a, float), np.asarray(b, float)
    d = b - a
    return [C(a + d * 0.06, b - d * 0.06, r_shaft * 1.08, r_shaft, bone, k=0.012),
            S(a, r_a, bone, k=0.012), S(b, r_b, bone, k=0.012)]


# ----------------------------------------------------------------------------- anatomy
def skull():
    H = HEAD
    L = [
        E(H + V3(0, 0.020, -0.015), (0.071, 0.081, 0.094), "Head", k=0.01, R=rot_x(0.15), name="cranium"),
        E(H + V3(0, -0.044, 0.058), (0.044, 0.034, 0.040), "Head", k=0.018, name="maxilla"),
        E(H + V3(0, 0.002, 0.070), (0.058, 0.015, 0.030), "Head", k=0.012, name="brow"),
        E(H + V3(0, -0.020, -0.075), (0.040, 0.030, 0.025), "Head", k=0.02, name="occiput"),
    ]
    for sx in (1, -1):
        L += [E(H + V3(sx * 0.046, -0.030, 0.062), (0.016, 0.017, 0.018), "Head", k=0.008, name="zygoma"),
              C(H + V3(sx * 0.052, -0.030, 0.050), H + V3(sx * 0.064, -0.026, -0.002), 0.0075, 0.0065, "Head", k=0.006),
              E(H + V3(sx * 0.060, -0.010, 0.000), (0.012, 0.030, 0.035), "Head", k=0.015, name="temporal")]
    for sx in (1, -1):
        L += [SUB("sphere", 0.004, c=H + V3(sx * 0.030, -0.012, 0.088), r=0.0205, name="orbit")]
    L += [SUB("ellipsoid", 0.003, c=H + V3(0, -0.046, 0.104), r=V3(0.010, 0.016, 0.020), R=rot_x(0.35), name="nasal")]
    # upper teeth along an arc
    for i, a in enumerate(np.linspace(-1.25, 1.25, 12)):
        base = H + V3(0.031 * math.sin(a), -0.070, 0.058 + 0.031 * math.cos(a))
        L += [C(base, base + V3(0, -0.013, 0.001), 0.0044, 0.0036, "Head", k=0.0015, tag="teeth")]
    # mandible (built closed, rotated open about the jaw hinge)
    J = lambda p: rot_about_x(p, JAW_PIVOT, JAW_OPEN)
    chin = H + V3(0, -0.108, 0.072)
    L += [E(J(chin), (0.019, 0.012, 0.013), "Jaw", k=0.008, name="chin")]
    for sx in (1, -1):
        ang = H + V3(sx * 0.046, -0.100, 0.002)
        hinge = H + V3(sx * 0.050, -0.046, -0.006)
        L += [C(J(chin + V3(sx * 0.010, 0, 0)), J(ang), 0.0085, 0.0078, "Jaw", k=0.006),
              C(J(ang), J(hinge), 0.0078, 0.0068, "Jaw", k=0.006),
              E(J(ang + V3(0, 0.020, 0.004)), (0.006, 0.022, 0.012), "Jaw", k=0.006)]
    for i, a in enumerate(np.linspace(-1.2, 1.2, 12)):
        base = H + V3(0.029 * math.sin(a), -0.095, 0.054 + 0.029 * math.cos(a))
        L += [C(J(base), J(base + V3(0, 0.012, 0.0)), 0.0042, 0.0034, "Jaw", k=0.0015, tag="teeth")]
    return L


def spine_and_ribs():
    L = []
    # 5 lumbar + 12 thoracic vertebrae
    for i, t in enumerate(np.linspace(0.02, 0.97, 17)):
        p = bez(t)
        tan = bez_tan(t)
        back = nrm(np.cross(tan, [1, 0, 0]))  # points backward (-z) for an upward spine
        if back[2] > 0:
            back = -back
        bone = "Spine1" if t < 0.35 else ("Spine2" if t < 0.65 else "Chest")
        size = 1.25 - 0.45 * t
        R = basis(tan, back)
        L += [E(p - back * 0.012, (0.020 * size, 0.011 * size + 0.002, 0.016 * size), bone, k=0.004, R=R, name="vertebra"),
              C(p + back * 0.004, p + back * 0.034 * size - tan * 0.012, 0.0062, 0.004, bone, k=0.005),
              C(p - V3(0.022 * size, 0, 0) + back * 0.008, p + V3(0.022 * size, 0, 0) + back * 0.008, 0.0045, 0.0045, bone, k=0.005)]
    # ribs: 10 pairs, ellipse arcs from the spine around to the sternum
    rx = [0.075, 0.098, 0.116, 0.130, 0.138, 0.141, 0.140, 0.134, 0.124, 0.112]
    rz = [0.050, 0.062, 0.072, 0.081, 0.086, 0.088, 0.088, 0.085, 0.080, 0.074]
    ts = np.linspace(0.93, 0.47, 10)
    fronts = []
    for i in range(10):
        sp = bez(ts[i])
        zc = sp[2] + rz[i] - 0.004
        th_end = 18 if i < 7 else 18 + (i - 6) * 22
        bone = "Chest" if i < 6 else "Spine2"
        for sx in (1, -1):
            pts = []
            for th in np.linspace(172, th_end, 7):
                a = math.radians(th)
                drop = 0.065 * (172 - th) / 154
                pts.append(V3(sx * rx[i] * math.sin(a), sp[1] - drop, zc + rz[i] * math.cos(a)))
            for a_, b_ in zip(pts, pts[1:]):
                L += [C(a_, b_, 0.0062, 0.0058, bone, k=0.003, name="rib")]
            if sx == 1 and i < 7:
                fronts.append(V3(0, pts[-1][1], pts[-1][2]))
    top, bot = fronts[0], fronts[6]
    st = (top + bot) / 2
    L += [E(st + V3(0, 0, 0.006), (0.016, np.linalg.norm(top - bot) / 2 + 0.01, 0.007), "Chest", k=0.008,
            R=basis(top - bot, [0, 0, 1]), name="sternum")]
    for sx in (1, -1):  # costal cartilage joins
        for f in fronts[1:6]:
            L += [C(f + V3(sx * 0.03, 0, -0.004), f + V3(sx * 0.012, 0.004, 0.004), 0.004, 0.0045, "Chest", k=0.004)]
    # neck vertebrae
    a, b = bez(1.0), HEAD + V3(0, -0.058, -0.045)
    for t in np.linspace(0.1, 0.9, 5):
        p = a + (b - a) * t
        L += [E(p, (0.017, 0.008, 0.015), "Neck", k=0.004, R=basis(b - a, [0, 0, -1])),
              C(p + V3(0, 0, -0.010), p + V3(0, -0.006, -0.026), 0.004, 0.003, "Neck", k=0.004)]
    return L


def pelvis():
    L = [E(V3(0, 1.000, -0.078), (0.040, 0.055, 0.020), "Hips", k=0.01, R=rot_x(-0.45), name="sacrum"),
         Prim("torus", "Hips", 0.012, tag="bone", flow=(0, -1, 0), c=V3(0, 0.955, -0.005), n=nrm([0, 1, 0.35]),
              R=0.064, r=0.012, name="pelvic_ring")]
    for sx in (1, -1):
        L += [E(V3(sx * 0.085, 1.030, -0.035), (0.062, 0.056, 0.011), "Hips", k=0.012,
                R=basis([0, 1, 0.1], [sx * 0.8, 0, 0.6]), name="ilium"),
              S(HIP[sx] + V3(-sx * 0.005, 0.004, 0), 0.026, "Hips", k=0.012, name="acetabulum")]
    return L


def arm(sx):
    s = SIDE[sx]
    sh, el, wr = SHOULDER[sx], ELBOW[sx], WRIST[sx]
    L = [C(V3(sx * 0.025, 1.455, 0.060), sh + V3(-sx * 0.012, 0.012, 0), 0.0075, 0.0085, f"Clavicle.{s}", k=0.004, name="clavicle"),
         E(V3(sx * 0.095, 1.360, -0.070), (0.052, 0.068, 0.008), f"Clavicle.{s}", k=0.006,
           R=basis([sx * 0.25, 1, 0], [sx * 0.35, 0, -1]), name="scapula")]
    L += long_bone(sh, el, 0.0125, 0.024, 0.016, f"UpperArm.{s}")
    lat = nrm(np.cross(el - sh, [0, 0, 1])) * sx
    L += [S(el + lat * 0.018, 0.012, f"UpperArm.{s}", k=0.008), S(el - lat * 0.016, 0.011, f"UpperArm.{s}", k=0.008)]
    fl = nrm(np.cross(wr - el, [0, 0, 1]))
    L += [C(el + fl * 0.010 + (wr - el) * 0.02, wr + fl * 0.012, 0.0085, 0.010, f"Forearm.{s}", k=0.006, name="radius"),
          C(el - fl * 0.006 + V3(0, 0, -0.008), wr - fl * 0.011, 0.0105, 0.0072, f"Forearm.{s}", k=0.006, name="ulna"),
          S(el + V3(0, 0.004, -0.014), 0.012, f"Forearm.{s}", k=0.008, name="olecranon")]
    L += hand(sx, wr, HAND_DIR[sx], f"Hand.{s}")
    return L


def hand(sx, wr, d, bone):
    n = nrm(V3(-sx, 0, 0) - d * np.dot(V3(-sx, 0, 0), d))   # palm faces the body
    e = nrm(np.cross(d, n))
    if e[2] < 0:
        e = -e                                               # e points toward the thumb (front)
    L = [E(wr + d * 0.016, (0.020, 0.015, 0.009), bone, k=0.006, R=basis(d, n), name="carpals")]
    for j, (o, sc) in enumerate(zip((0.016, 0.005, -0.006, -0.017), (1.0, 1.08, 1.0, 0.84))):
        p = wr + d * 0.028 + e * o * 0.7
        q = wr + d * 0.088 * sc + e * o
        L += [C(p, q, 0.0052, 0.0047, bone, k=0.002)]
        dirv = d
        for seg, (ln, curl, r0, r1) in enumerate(((0.040, 0.30, 0.0047, 0.0043), (0.026, 0.65, 0.0042, 0.0038),
                                                   (0.020, 0.95, 0.0037, 0.0033))):
            dirv = nrm(d * math.cos(curl) + n * math.sin(curl) * 0.9 + e * o * 0.6)
            nq = q + dirv * ln * sc
            L += [C(q, nq, r0, r1, bone, k=0.0018)]
            q = nq
    t0 = wr + d * 0.012 + e * 0.020
    t1 = t0 + nrm(d * 0.6 + e * 0.6 + n * 0.3) * 0.040
    t2 = t1 + nrm(d * 0.7 + e * 0.3 + n * 0.5) * 0.030
    t3 = t2 + nrm(d * 0.5 + e * 0.1 + n * 0.85) * 0.022
    L += [C(t0, t1, 0.0055, 0.0048, bone, k=0.002), C(t1, t2, 0.0046, 0.0041, bone, k=0.002),
          C(t2, t3, 0.0039, 0.0034, bone, k=0.002)]
    return L


def leg(sx):
    s = SIDE[sx]
    hp, kn, an = HIP[sx], KNEE[sx], ANKLE[sx]
    L = [S(hp, 0.024, f"Thigh.{s}", k=0.006, name="femur_head"),
         C(hp, hp + V3(sx * 0.042, -0.035, 0), 0.016, 0.017, f"Thigh.{s}", k=0.006, name="femur_neck"),
         S(hp + V3(sx * 0.046, -0.030, -0.004), 0.019, f"Thigh.{s}", k=0.008, name="trochanter"),
         C(hp + V3(sx * 0.040, -0.050, 0), kn + V3(0, 0.035, 0), 0.0155, 0.0150, f"Thigh.{s}", k=0.012, name="femur")]
    lat = V3(sx, 0, 0)
    L += [S(kn + lat * 0.018 + V3(0, 0.012, -0.004), 0.021, f"Thigh.{s}", k=0.01),
          S(kn - lat * 0.018 + V3(0, 0.012, -0.004), 0.021, f"Thigh.{s}", k=0.01),
          E(kn + V3(0, -0.010, 0), (0.036, 0.012, 0.030), f"Shin.{s}", k=0.01, name="tibial_plateau"),
          C(kn + V3(0, -0.02, 0.004), an + V3(-sx * 0.004, 0.02, 0.004), 0.017, 0.0125, f"Shin.{s}", k=0.012, name="tibia"),
          C(kn + lat * 0.026 + V3(0, -0.03, -0.012), an + lat * 0.022 + V3(0, 0.01, -0.006), 0.0075, 0.0068, f"Shin.{s}", k=0.006, name="fibula"),
          S(an - lat * 0.012 + V3(0, 0.005, 0.004), 0.012, f"Shin.{s}", k=0.006),
          S(an + lat * 0.022 + V3(0, 0.0, -0.004), 0.011, f"Shin.{s}", k=0.006)]
    if sx == 1:
        L += bare_foot(sx)
    return L


def bare_foot(sx):
    s = SIDE[sx]
    an = ANKLE[sx]
    fwd = nrm([sx * 0.12, 0, 1])
    lat = nrm(np.cross([0, 1, 0], fwd)) * -1 * sx * -1
    lat = V3(sx, 0, 0)
    bone = f"Foot.{s}"
    L = [S(an + V3(0, -0.012, 0), 0.020, bone, k=0.006, name="talus"),
         E(an + V3(0, -0.058, -0.030), (0.020, 0.026, 0.034), bone, k=0.01, R=rot_x(0.3), name="calcaneus"),
         E(an + V3(0, -0.050, 0.035), (0.026, 0.016, 0.030), bone, k=0.01, name="tarsals")]
    for j, (o, sc) in enumerate(zip((-0.020, -0.010, 0.000, 0.011, 0.021), (1.0, 0.97, 0.93, 0.88, 0.82))):
        base = an + V3(0, -0.055, 0.050) + lat * o * 0.8
        ball = an + V3(0, -0.085, 0.050 + 0.075 * sc) + lat * o * 1.15
        r = 0.0062 if j else 0.0078
        L += [C(base, ball, r, r * 0.85, bone, k=0.003)]
        p1 = ball + fwd * 0.026 * sc + V3(0, -0.004, 0)
        p2 = p1 + fwd * 0.016 * sc + V3(0, -0.006, 0)
        L += [C(ball, p1, r * 0.8, r * 0.7, bone, k=0.002), C(p1, p2, r * 0.66, r * 0.55, bone, k=0.002)]
    return L


# ----------------------------------------------------------------------------- armour & cloth
def pauldron(sx):
    """Broad, outward-sloping rusted shell with a lower lame and a serrated crest."""
    s = SIDE[sx]
    sh = SHOULDER[sx]
    tilt = rot_z(-sx * 0.55)
    c1 = sh + V3(sx * 0.045, 0.004, 0.0)
    keep_up = nrm([sx * 0.55, 1, 0])
    kids = [
        E(c1, (0.135, 0.100, 0.128), None, k=0.003, R=tilt, shell=0.0042, tag="rust", amp=0.0015, freq=60, seed=3),
        SUB("plane", 0.004, c=c1 - keep_up * 0.018, n=keep_up, amp=0.004, freq=70, seed=4),
        SUB("plane", 0.004, c=V3(sx * 0.105, sh[1], 0), n=nrm([sx, -0.35, 0])),
    ]
    c2 = c1 + V3(sx * 0.050, -0.055, 0.0)
    kids += [
        E(c2, (0.112, 0.082, 0.112), None, k=0.003, R=rot_z(-sx * 0.75), shell=0.0038, tag="rust", amp=0.0015, freq=60, seed=5),
        SUB("plane", 0.004, c=c2 - nrm([sx * 0.8, 1, 0]) * 0.012, n=nrm([sx * 0.8, 1, 0]), amp=0.004, freq=70, seed=6),
        SUB("ellipsoid", 0.003, c=c1, r=V3(0.131, 0.096, 0.124), R=tilt),
        SUB("plane", 0.004, c=V3(sx * 0.150, sh[1], 0), n=nrm([sx, -0.2, 0])),
    ]
    top = c1 + tilt @ V3(0, 0.099, 0)
    kids += [E(top, (0.006, 0.026, 0.085), None, k=0.005, R=tilt, amp=0.0022, freq=420, seed=7, tag="rust")]
    return [group(kids, f"Pauldron.{s}", "rust", k=0.004, flow=(0, -1, 0), name="pauldron")]


def knee_guard(sx):
    s = SIDE[sx]
    kn = KNEE[sx]
    thigh = nrm(HIP[sx] - kn)
    shin = nrm(ANKLE[sx] - kn)
    fwd = nrm(-(thigh + shin))
    if fwd[2] < 0:
        fwd = nrm(fwd + V3(0, 0, 0.8))
    c = kn + fwd * 0.018
    kids = [E(c, (0.054, 0.066, 0.050), None, k=0.003, R=basis(thigh - shin, fwd), shell=0.0034, tag="steel"),
            SUB("plane", 0.004, c=kn - fwd * 0.004, n=fwd),
            E(c + fwd * 0.040 + V3(0, 0.002, 0), (0.016, 0.016, 0.010), None, k=0.008, R=basis(thigh - shin, fwd), tag="steel"),
            E(kn - shin * 0.062 + fwd * 0.026, (0.046, 0.024, 0.030), None, k=0.003, R=basis(shin, fwd), shell=0.003, tag="steel"),
            SUB("plane", 0.004, c=kn - fwd * 0.0, n=fwd)]
    return [group(kids, f"Shin.{s}", "steel", k=0.004, name="knee_guard")]


def greave_and_boot(sx):
    s = SIDE[sx]
    kn, an = KNEE[sx], ANKLE[sx]
    shin = nrm(an - kn)
    a = kn + shin * 0.085
    b = an + V3(0, 0.035, 0)
    greave = [C(a, b, 0.050, 0.044, None, k=0.003, shell=0.0032, tag="steel"),
              SUB("plane", 0.003, c=a, n=-shin, amp=0.004, freq=60),
              SUB("plane", 0.003, c=b, n=shin)]
    boot_c = an + V3(0, -0.048, 0.060)
    boot = [E(boot_c, (0.050, 0.052, 0.128), None, k=0.003, tag="steel"),
            C(an + V3(0, -0.04, -0.002), an + V3(0, 0.07, 0.0), 0.054, 0.049, None, k=0.012, tag="steel"),
            E(an + V3(0, -0.040, 0.165), (0.040, 0.034, 0.040), None, k=0.02, tag="steel"),
            SUB("plane", 0.003, c=V3(0, 0.004, 0), n=V3(0, 1, 0))]
    straps = [Prim("torus", f"Shin.{s}", 0.004, tag="leather", flow=shin, c=a + (b - a) * t, n=shin,
                   R=0.050 - 0.006 * t, r=0.0055) for t in (0.15, 0.62)]
    straps += [Prim("torus", f"Foot.{s}", 0.004, tag="leather", flow=(1, 0, 0), c=an + V3(0, -0.010, 0.050),
                    n=nrm([0, 1, -0.5]), R=0.052, r=0.006)]
    return [group(greave, f"Shin.{s}", "steel", k=0.004, name="greave"),
            group(boot, f"Foot.{s}", "steel", k=0.004, name="boot")] + straps


def wraps(a, b, r, bone, seed, holes=0.9):
    kids = [C(a, b, r, r * 0.92, None, k=0.003, shell=0.0026, tag="cloth", amp=0.0015, freq=120, seed=seed),
            SUB("plane", 0.003, c=a, n=nrm(b - a), amp=0.006, freq=90, seed=seed + 1),
            SUB("plane", 0.003, c=b, n=nrm(a - b), amp=0.006, freq=90, seed=seed + 2),
            SUB("noise", 0.002, c=(a + b) / 2, r=0.3, thresh=holes, freq=60, seed=seed + 3)]
    return group(kids, bone, "cloth", k=0.004, flow=nrm(b - a), name="wraps")


def clothing():
    tunic = [E(V3(0, 1.225, -0.008), (0.178, 0.262, 0.128), None, k=0.003, shell=0.0034, tag="cloth",
               amp=0.004, freq=38, seed=11),
             SUB("plane", 0.006, c=V3(0, 1.445, 0), n=V3(0, -1, 0.15), amp=0.018, freq=28, seed=12),
             SUB("box", 0.01, c=V3(0, 1.31, 0.14), b=V3(0.040, 0.15, 0.08), amp=0.02, freq=35, seed=15),
             SUB("noise", 0.004, c=V3(0, 1.25, 0), r=0.5, thresh=1.05, freq=16, seed=13),
             SUB("noise", 0.003, c=V3(0, 1.25, 0), r=0.5, thresh=1.35, freq=42, seed=14)]
    skirt = [C(V3(0, 1.075, -0.018), V3(0, 0.600, 0.020), 0.172, 0.250, None, k=0.003, shell=0.0034, tag="cloth",
               amp=0.006, freq=30, seed=21),
             SUB("plane", 0.004, c=V3(0, 0.665, 0), n=V3(0, 1, 0), amp=0.050, freq=19, seed=22),
             SUB("plane", 0.004, c=V3(0, 1.075, 0), n=V3(0, -1, 0)),
             SUB("box", 0.006, c=V3(0.255, 0.79, 0.02), b=V3(0.075, 0.20, 0.30), amp=0.025, freq=25, seed=23),
             SUB("box", 0.006, c=V3(-0.255, 0.79, 0.02), b=V3(0.075, 0.20, 0.30), amp=0.025, freq=25, seed=24),
             SUB("noise", 0.004, c=V3(0, 0.85, 0), r=0.5, thresh=1.25, freq=24, seed=25)]
    L = [group(tunic, "Spine2", "cloth", k=0.004, flow=(0, -1, 0), name="tunic"),
         group(skirt, "Hips", "cloth", k=0.004, flow=(0, -1, 0), name="skirt"),
         Prim("torus", "Hips", 0.004, tag="leather", flow=(1, 0, 0), c=V3(0, 1.062, -0.016), n=V3(0, 1, 0),
              R=0.176, r=0.012, name="belt"),
         Prim("box", "Hips", 0.003, tag="steel", flow=(1, 0, 0), c=V3(0, 1.062, 0.165), b=V3(0.020, 0.016, 0.006),
              rr=0.003, name="buckle")]
    # cross strap over the chest (flattened torus following the tunic)
    L += [wraps(ELBOW[-1] + (WRIST[-1] - ELBOW[-1]) * 0.18, WRIST[-1] + V3(0, 0.01, 0), 0.026, "Forearm.R", 31),
          wraps(KNEE[1] + (ANKLE[1] - KNEE[1]) * 0.30, KNEE[1] + (ANKLE[1] - KNEE[1]) * 0.78, 0.030, "Shin.L", 41, 0.7)]
    return L


def prims():
    L = skull() + spine_and_ribs() + pelvis()
    for sx in (1, -1):
        L += arm(sx) + leg(sx) + pauldron(sx) + knee_guard(sx)
    L += greave_and_boot(-1)
    L += clothing()
    return L


# ----------------------------------------------------------------------------- shading
def srgb(*c):
    return np.array(c, float) / 255.0


BONE_A, BONE_B, BONE_DIRT = srgb(226, 212, 168), srgb(186, 166, 118), srgb(92, 74, 50)
TEETH = srgb(206, 190, 140)
CLOTH_A, CLOTH_B, CLOTH_DIRT = srgb(84, 96, 120), srgb(128, 138, 154), srgb(80, 70, 58)
RUST_A, RUST_B, RUST_C = srgb(112, 58, 40), srgb(66, 36, 26), srgb(146, 82, 48)
STEEL, GRIME = srgb(200, 199, 202), srgb(70, 64, 56)
LEATHER, LEATHER_W = srgb(64, 44, 30), srgb(104, 78, 54)


def shade(ctx):
    P, N, tag, ao, flow = ctx["P"], ctx["N"], ctx["tag"], ctx["ao"], ctx["flow"]
    n = len(P)
    t = lambda k: tag.get(k, np.zeros(n))
    bone_w = np.clip(t("bone") + t("teeth"), 0, 1)
    cloth, rust, steel, leather = t("cloth"), t("rust"), t("steel"), t("leather")
    col = np.zeros((n, 3))
    rough = np.zeros(n)
    metal = np.zeros(n)
    grad = np.zeros((n, 3))

    # ---- bone: yellowed ivory, stains, dirty crevices, pitting
    st = smoothstep(-0.6, 1.4, wave_noise(P, 35, seed=1) + 0.6 * wave_noise(P, 140, seed=2))
    bcol = BONE_A + (BONE_B - BONE_A) * st[:, None]
    bcol = bcol + (TEETH - bcol) * t("teeth")[:, None] * 0.6
    cav = np.clip(ao, 0, 1) ** 0.8
    bcol = BONE_DIRT + (bcol - BONE_DIRT) * cav[:, None]
    _, gp1 = wave_noise_grad(P, 650, seed=3)
    _, gp2 = wave_noise_grad(P, 160, seed=4)
    col += bcol * bone_w[:, None]
    rough += (0.58 + 0.12 * st) * bone_w
    grad += (gp1 / 650 * 0.22 + gp2 / 160 * 0.18) * bone_w[:, None]

    # ---- cloth: faded blue-grey linen, weave, vertical folds, grime toward hems
    up = V3(0, 1, 0)
    fv = nrm_rows(up - N * (N @ up)[:, None] + 1e-6)
    fh = nrm_rows(np.cross(N, fv))
    rv, gv = strand_field(P, fv, fp=4200, fa=4200 / 9, seed=5, n=24)
    rh, gh = strand_field(P, fh, fp=4200, fa=4200 / 9, seed=6, n=24)
    rf, gf = strand_field(P, fv, fp=70, fa=12, seed=7, n=16)
    fade = smoothstep(-0.8, 1.2, wave_noise(P, 14, seed=8) + 0.5 * rf)
    ccol = CLOTH_A + (CLOTH_B - CLOTH_A) * fade[:, None]
    dirt = np.clip(smoothstep(0.95, 0.62, P[:, 1]) * 0.7 + 0.35 * smoothstep(0.4, 1.4, wave_noise(P, 30, seed=9)), 0, 0.85)
    ccol = ccol + (CLOTH_DIRT - ccol) * dirt[:, None]
    ccol = ccol * (0.88 + 0.07 * (rv + rh))[:, None] * (0.5 + 0.5 * ao)[:, None]
    col += ccol * cloth[:, None]
    rough += 0.93 * cloth
    grad += (gv * 0.10 + gh * 0.10 + gf * 0.45) * cloth[:, None]

    # ---- rusted plate: layered rust, flaking, patches of bare dark metal
    r1 = wave_noise(P, 45, seed=10) + 0.5 * wave_noise(P, 160, seed=11)
    r2 = wave_noise(P, 260, seed=12)
    rc = RUST_A + (RUST_B - RUST_A) * smoothstep(-0.2, 1.2, r1)[:, None]
    rc = rc + (RUST_C - rc) * smoothstep(0.6, 1.8, r2 - 0.3 * r1)[:, None]
    bare = smoothstep(1.0, 1.5, wave_noise(P, 30, seed=13) + 0.4 * r2) * smoothstep(0.5, 0.9, ao)
    rc = rc + (srgb(96, 92, 90) - rc) * bare[:, None]
    rc = rc * (0.45 + 0.55 * ao)[:, None]
    _, gr1 = wave_noise_grad(P, 520, seed=14)
    _, gr2 = wave_noise_grad(P, 85, seed=15)
    col += rc * rust[:, None]
    rough += (0.78 - 0.35 * bare) * rust
    metal += (0.15 + 0.6 * bare) * rust
    grad += (gr1 / 520 * 0.35 + gr2 / 85 * 0.25) * rust[:, None]

    # ---- steel: scratched, dented, grime in cavities, rust bloom
    g1 = smoothstep(0.2, 1.4, wave_noise(P, 40, seed=16) + 0.5 * wave_noise(P, 200, seed=17))
    grime = np.clip(g1 * 0.6 + (1 - ao) * 1.4, 0, 1)
    sc = STEEL + (GRIME - STEEL) * grime[:, None]
    rb = smoothstep(1.1, 1.7, wave_noise(P, 70, seed=18)) * 0.8
    sc = sc + (RUST_A - sc) * rb[:, None]
    rs, gs = strand_field(P, flow, fp=3500, fa=200, seed=19, n=16)
    sc = sc * (0.94 + 0.06 * rs)[:, None]
    _, gd = wave_noise_grad(P, 75, seed=20)
    col += sc * steel[:, None]
    rough += (0.42 + 0.30 * grime + 0.3 * rb) * steel
    metal += (0.80 - 0.40 * grime - 0.6 * rb) * steel
    grad += (gd / 75 * 0.22 + gs * 0.05) * steel[:, None]

    # ---- leather straps
    lw = smoothstep(0.4, 1.6, wave_noise(P, 90, seed=21))
    lc = LEATHER + (LEATHER_W - LEATHER) * lw[:, None]
    _, gl = wave_noise_grad(P, 900, seed=22)
    col += lc * (0.5 + 0.5 * ao)[:, None] * leather[:, None]
    rough += 0.66 * leather
    grad += gl / 900 * 0.18 * leather[:, None]

    wsum = np.maximum(bone_w + cloth + rust + steel + leather, 1e-6)
    col /= wsum[:, None]
    rough /= wsum
    metal /= wsum
    grad /= wsum[:, None]
    return dict(albedo=np.clip(col, 0, 1), roughness=np.clip(rough, 0.05, 1), metallic=np.clip(metal, 0, 1),
                fur=np.ones(n), grad=grad, occlusion=np.clip(ao, 0, 1) ** 0.9)


FUR_MATERIAL_EXT = None


def extra_parts(cr):
    return []


# ----------------------------------------------------------------------------- animation
def anim_idle(t, T=4.0):
    w = 2 * math.pi / T
    q = {}
    sway = math.sin(w * t)
    q["Spine1"] = quat_axis((0, 0, 1), 0.020 * sway)
    q["Spine2"] = quat_axis((1, 0, 0), 0.015 * math.sin(w * t * 2))
    q["Chest"] = quat_mul(quat_axis((0, 0, 1), -0.015 * sway), quat_axis((1, 0, 0), 0.012 * math.sin(w * t * 2 + 1)))
    q["Neck"] = quat_axis((0, 1, 0), 0.12 * math.sin(w * t * 0.5 * 2 - 0.5))
    q["Head"] = quat_mul(quat_axis((0, 0, 1), 0.10 * math.sin(w * t + 0.8)), quat_axis((1, 0, 0), 0.05 * math.sin(w * t * 2)))
    burst = 1.0 if 1.4 < (t % T) < 2.3 else 0.0
    q["Jaw"] = quat_axis((1, 0, 0), -0.18 * burst * max(0.0, math.sin(2 * math.pi * 9 * t)))
    for s, sx in (("L", 1), ("R", -1)):
        q[f"UpperArm.{s}"] = quat_axis((1, 0, 0), 0.05 * math.sin(w * t + sx))
        q[f"Forearm.{s}"] = quat_axis((1, 0, 0), -0.04 * math.sin(w * t + sx + 0.6))
        q[f"Pauldron.{s}"] = quat_axis((0, 0, 1), sx * 0.015 * sway)
    return q


def anim_rattle(t, T=1.5):
    q = {}
    e = math.sin(math.pi * t / T) ** 2
    q["Jaw"] = quat_axis((1, 0, 0), -0.25 * e * max(0.0, math.sin(2 * math.pi * 11 * t)))
    q["Head"] = quat_mul(quat_axis((0, 1, 0), 0.08 * e * math.sin(2 * math.pi * 6 * t)), quat_axis((1, 0, 0), -0.18 * e))
    q["Neck"] = quat_axis((1, 0, 0), -0.12 * e)
    q["Chest"] = quat_axis((1, 0, 0), -0.06 * e)
    for s, sx in (("L", 1), ("R", -1)):
        q[f"UpperArm.{s}"] = quat_axis((0, 0, 1), sx * 0.10 * e * math.sin(2 * math.pi * 5 * t + sx))
        q[f"Hand.{s}"] = quat_axis((1, 0, 0), 0.3 * e * math.sin(2 * math.pi * 7 * t))
    return q


ANIMATIONS = [("Idle", 4.0, 30, anim_idle), ("Rattle", 1.5, 30, anim_rattle)]
