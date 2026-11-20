"""
Design: Grey Hare (crouched/sitting idle pose), realistic-stylized game creature.

Units meters. Y up, +Z = nose direction (front), +X = creature's LEFT.
Size: nose-to-rump ~0.53 m, ear tips ~0.50 m above ground.
"""
import math
import numpy as np
from creaturelib import (Prim, basis, nrm, nrm_rows, rot_x, smoothstep, wave_noise, strand_field,
                         uv_sphere, strand_tube, quat_axis, quat_mul)
from PIL import Image, ImageDraw, ImageFilter

NAME = "GreyHare"
TITLE = "Grey Hare"
SETTINGS = dict(voxel=0.0015, tris=11000, tex=2048,
                preview=dict(voxel=0.0028, tris=7000, tex=1024))

# ----------------------------------------------------------------------------- landmarks
EAR_BASE = np.array([0.019, 0.326, 0.170])
EAR_DIR = nrm([0.27, 1.0, -0.17])
EAR_OPEN = nrm([0.55, 0.0, 1.0])         # concave side faces forward-outward
EAR_LEN = 0.225
EYE_C = np.array([0.0443, 0.290, 0.216])
EYE_AXIS = nrm([0.90, 0.16, 0.40])
EYE_R = 0.0123
NOSE_TIP = np.array([0.0, 0.257, 0.299])


def side_pt(p, sx):
    p = np.array(p, float)
    p[0] *= sx
    return p


def side_vec(v, sx):
    v = np.array(v, float)
    v[0] *= sx
    return v


# ----------------------------------------------------------------------------- skeleton
def _bones():
    B = [
        ("Root", None, (0, 0, 0)),
        ("Hips", "Root", (0, 0.150, -0.130)),
        ("Spine", "Hips", (0, 0.170, -0.030)),
        ("Chest", "Spine", (0, 0.165, 0.070)),
        ("Neck", "Chest", (0, 0.195, 0.120)),
        ("Head", "Neck", (0, 0.258, 0.180)),
        ("Nose", "Head", (0, 0.251, 0.272)),
        ("Tail", "Hips", (0, 0.140, -0.222)),
    ]
    for s, sx in (("L", 1), ("R", -1)):
        P = lambda p: tuple(side_pt(p, sx))
        eb = side_pt(EAR_BASE, sx)
        ed = side_vec(EAR_DIR, sx)
        B += [
            (f"Ear1.{s}", "Head", tuple(eb)),
            (f"Ear2.{s}", f"Ear1.{s}", tuple(eb + ed * 0.095)),
            (f"UpperArm.{s}", "Chest", P((0.040, 0.175, 0.100))),
            (f"Forearm.{s}", f"UpperArm.{s}", P((0.047, 0.100, 0.090))),
            (f"ForePaw.{s}", f"Forearm.{s}", P((0.044, 0.027, 0.106))),
            (f"Thigh.{s}", "Hips", P((0.060, 0.150, -0.110))),
            (f"Shin.{s}", f"Thigh.{s}", P((0.074, 0.070, -0.030))),
            (f"HindFoot.{s}", f"Shin.{s}", P((0.067, 0.030, -0.168))),
        ]
    return B


BONES = _bones()


# ----------------------------------------------------------------------------- sculpt
def prims():
    E = lambda c, r, bone, k, tag, flow, R=None, **kw: Prim("ellipsoid", bone, k, tag=tag, flow=flow, c=c, r=r,
                                                             R=np.eye(3) if R is None else R, **kw)
    C = lambda a, b, ra, rb, bone, k, tag, flow, **kw: Prim("cone", bone, k, tag=tag, flow=flow, a=a, b=b, ra=ra, rb=rb, **kw)
    S = lambda c, r, bone, k, tag, flow=(0, 0, -1), **kw: Prim("sphere", bone, k, tag=tag, flow=flow, c=c, r=r, **kw)
    down = (0, -1, 0.05)
    L = []
    # --- body masses
    L += [
        E((0, 0.180, -0.045), (0.090, 0.116, 0.160), "Spine", 0.01, "back", (0, -0.25, -1), name="torso"),
        E((0, 0.160, -0.150), (0.090, 0.112, 0.098), "Hips", 0.045, "back", (0, -0.7, -0.7), name="rump"),
        E((0, 0.172, 0.090), (0.064, 0.085, 0.080), "Chest", 0.045, "chest", (0, -1, 0.15), name="chest"),
        E((0, 0.124, -0.005), (0.070, 0.058, 0.115), "Spine", 0.04, "belly", (0, -0.3, -1), name="belly"),
        C((0, 0.182, 0.105), (0, 0.248, 0.170), 0.062, 0.044, "Neck", 0.03, "neck", (0, -0.5, -1), name="neck"),
    ]
    for sx in (1, -1):
        s = "L" if sx > 0 else "R"
        L += [
            E(side_pt((0.058, 0.122, -0.108), sx), (0.050, 0.086, 0.106), f"Thigh.{s}", 0.024, "haunch", (0, -1, -0.35),
              R=rot_x(-0.25), name="haunch"),
            C(side_pt((0.060, 0.150, -0.110), sx), side_pt((0.074, 0.070, -0.030), sx), 0.045, 0.024, f"Thigh.{s}",
              0.02, "haunch", (0, -1, 0.2), name="thigh"),
            S(side_pt((0.079, 0.074, -0.027), sx), 0.026, f"Shin.{s}", 0.016, "haunch", (0, -1, -0.2), name="stifle"),
            E(side_pt((0.046, 0.206, 0.066), sx), (0.026, 0.046, 0.036), "Chest", 0.026, "back", (0, -0.6, -1),
              R=rot_x(0.45), name="scapula"),
            C(side_pt((0.074, 0.070, -0.030), sx), side_pt((0.068, 0.032, -0.163), sx), 0.020, 0.0125, f"Shin.{s}",
              0.015, "leg", (0, -0.3, -1), name="shin"),
            S(side_pt((0.067, 0.030, -0.170), sx), 0.0135, f"HindFoot.{s}", 0.010, "paw", name="heel"),
            C(side_pt((0.066, 0.021, -0.170), sx), side_pt((0.061, 0.014, 0.012), sx), 0.0158, 0.0128, f"HindFoot.{s}",
              0.012, "paw", (0, -0.1, 1), name="hindfoot"),
        ]
        for i, off in enumerate((-0.0105, -0.0035, 0.0035, 0.0105)):
            ln = 0.026 if i in (1, 2) else 0.021
            a = side_pt((0.061 + off, 0.012, 0.008), sx)
            b = side_pt((0.061 + off * 1.15, 0.008, 0.008 + ln), sx)
            L += [C(a, b, 0.0058, 0.0045, f"HindFoot.{s}", 0.004, "paw", (0, 0, 1), name="htoe"),
                  C(b - np.array([0, 0.001, 0.003]), b + np.array([0, -0.004, 0.008]), 0.0028, 0.0011, f"HindFoot.{s}",
                    0.0015, "claw", (0, 0, 1), name="hclaw")]
        # forelegs
        L += [
            C(side_pt((0.040, 0.175, 0.100), sx), side_pt((0.047, 0.100, 0.090), sx), 0.030, 0.0165, f"UpperArm.{s}",
              0.022, "leg", down, name="upperarm"),
            C(side_pt((0.047, 0.100, 0.090), sx), side_pt((0.044, 0.029, 0.106), sx), 0.0145, 0.0105, f"Forearm.{s}",
              0.008, "leg", down, name="forearm"),
            C(side_pt((0.044, 0.027, 0.104), sx), side_pt((0.043, 0.011, 0.136), sx), 0.0115, 0.0098, f"ForePaw.{s}",
              0.007, "paw", (0, -0.2, 1), name="forepaw"),
        ]
        for i, off in enumerate((-0.0085, -0.0028, 0.0028, 0.0085)):
            ln = 0.017 if i in (1, 2) else 0.013
            a = side_pt((0.043 + off, 0.010, 0.131), sx)
            b = side_pt((0.043 + off * 1.2, 0.0065, 0.131 + ln), sx)
            L += [C(a, b, 0.0047, 0.0037, f"ForePaw.{s}", 0.003, "paw", (0, 0, 1), name="ftoe"),
                  C(b - np.array([0, 0.0005, 0.002]), b + np.array([0, -0.0035, 0.0065]), 0.0024, 0.0010, f"ForePaw.{s}",
                    0.0012, "claw", (0, 0, 1), name="fclaw")]
    # --- tail (fluffy)
    L += [E((0, 0.142, -0.243), (0.025, 0.029, 0.024), "Tail", 0.012, "tail", (0, 0.4, -1), amp=0.0022, freq=240,
            seed=5, name="tail")]
    # --- head
    L += [
        E((0, 0.268, 0.200), (0.050, 0.056, 0.086), "Head", 0.025, "head", (0, 0.2, -1), R=rot_x(0.22), name="skull"),
        E((0, 0.250, 0.264), (0.027, 0.030, 0.042), "Head", 0.016, "muzzle", (0, 0.1, -1), R=rot_x(0.25), name="muzzle"),
        E((0, 0.229, 0.256), (0.017, 0.012, 0.025), "Head", 0.010, "chin", (0, -0.4, -1), name="chin"),
        S(NOSE_TIP, 0.0105, "Nose", 0.008, "nose", name="nose"),
    ]
    for sx in (1, -1):
        L += [
            E(side_pt((0.029, 0.250, 0.224), sx), (0.031, 0.033, 0.046), "Head", 0.016, "cheek", (0, -0.3, -1), name="cheek"),
            E(side_pt((0.014, 0.246, 0.276), sx), (0.015, 0.017, 0.019), "Nose", 0.008, "muzzle", (side_vec((0.6, -0.2, -1), sx)),
              name="whiskerpad"),
            E(side_pt((0.035, 0.302, 0.204), sx), (0.019, 0.012, 0.029), "Head", 0.012, "head", (0, 0.2, -1), name="brow"),
        ]
    # nostril/lip details (subtractive)
    for sx in (1, -1):
        L += [Prim("cone", None, 0.0015, op="sub", a=NOSE_TIP + side_vec((0.0035, 0.003, 0.009), sx),
                   b=NOSE_TIP + side_vec((0.0085, -0.0035, 0.004), sx), ra=0.0017, rb=0.0012, name="nostril")]
    L += [Prim("cone", None, 0.0015, op="sub", a=NOSE_TIP + np.array([0, -0.005, 0.004]), b=NOSE_TIP + np.array([0, -0.019, -0.011]), ra=0.0012, rb=0.0010, name="lipsplit")]
    # eyes: socket, then eyelid ring
    for sx in (1, -1):
        c = side_pt(EYE_C, sx)
        ax = side_vec(EYE_AXIS, sx)
        L += [Prim("sphere", None, 0.003, op="sub", c=c + ax * 0.001, r=EYE_R + 0.0006, name="socket"),
              Prim("torus", "Head", 0.004, tag="eyelid", flow=(0, 0.2, -1), c=c + ax * 0.0035, n=ax, R=EYE_R * 0.98,
                   r=0.0027, name="eyelid")]
    # ears: two overlapping lobes + concavity carve
    for sx in (1, -1):
        s = "L" if sx > 0 else "R"
        b0 = side_pt(EAR_BASE, sx)
        d = side_vec(EAR_DIR, sx)
        o = side_vec(EAR_OPEN, sx)
        R = basis(d, o)
        L += [
            E(b0 + d * 0.054, (0.027, 0.068, 0.0088), f"Ear1.{s}", 0.014, "ear", d, R=R, name="ear_lo"),
            E(b0 + d * 0.142, (0.036, 0.086, 0.0082), f"Ear2.{s}", 0.016, "ear", d, R=R, name="ear_hi"),
            Prim("ellipsoid", None, 0.004, op="sub", c=b0 + d * 0.130 + basis(d, o)[:, 2] * 0.0055,
                 r=(0.0285, 0.102, 0.0085), R=R, name="ear_cup"),
        ]
    return L


# ----------------------------------------------------------------------------- extra parts
def eye_texture(size=512):
    """Equirect eye map; v=0 at the front pole (pupil)."""
    v = (np.arange(size) + 0.5) / size
    u = (np.arange(size) + 0.5) / size
    U, Vv = np.meshgrid(u, v)
    th = Vv * 180
    rng = np.random.default_rng(3)
    streak = np.zeros_like(U)
    for k in range(40):
        streak += np.sin(U * 2 * np.pi * rng.integers(20, 90) + rng.uniform(0, 6)) * rng.uniform(0.2, 1)
    streak /= 20
    col = np.zeros((size, size, 3))
    pupil = np.array([0.012, 0.010, 0.010])
    iris_in = np.array([0.045, 0.028, 0.018])
    iris_out = np.array([0.30, 0.17, 0.07])
    limbus = np.array([0.05, 0.035, 0.03])
    sclera = np.array([0.55, 0.47, 0.40])
    t = np.clip((th - 34) / (60 - 34), 0, 1)[..., None]
    iris = iris_in + (iris_out - iris_in) * (t ** 1.6) + streak[..., None] * 0.05
    col[:] = sclera
    col = np.where((th < 68)[..., None], limbus, col)
    col = np.where((th < 62)[..., None], iris, col)
    col = np.where((th < 34)[..., None], pupil, col)
    im = Image.fromarray((np.clip(col, 0, 1) * 255).astype(np.uint8)).filter(ImageFilter.GaussianBlur(1.2))
    return im


def extra_parts(cr):
    parts = []
    eV, eN, eUV, eI = [], [], [], []
    off = 0
    for sx in (1, -1):
        V, N, UV, F = uv_sphere(side_pt(EYE_C, sx), EYE_R, side_vec(EYE_AXIS, sx), rings=14, segs=20)
        eV.append(V); eN.append(N); eUV.append(UV); eI.append(F + off)
        off += len(V)
    V = np.concatenate(eV)
    parts.append(dict(name="Eyes", P=V, N=np.concatenate(eN), UV=np.concatenate(eUV), I=np.concatenate(eI),
                      weights=[("Head", 1.0)], texture=eye_texture(),
                      material={"name": "M_Hare_Eye", "pbrMetallicRoughness": {"metallicFactor": 0.0, "roughnessFactor": 0.04},
                                "extensions": {"KHR_materials_clearcoat": {"clearcoatFactor": 1.0, "clearcoatRoughnessFactor": 0.02}}}))
    # whiskers
    rng = np.random.default_rng(11)
    wV, wN, wI = [], [], []
    off = 0
    for sx in (1, -1):
        for i in range(13):
            root = side_pt((0.020 + rng.uniform(-0.002, 0.004), 0.239 + rng.uniform(0, 0.016), 0.274 + rng.uniform(-0.008, 0.006)), sx)
            out = side_vec(nrm((1.0, rng.uniform(-0.45, 0.35), rng.uniform(-0.55, 0.35))), sx)
            length = rng.uniform(0.045, 0.095)
            pts = [root + out * length * t + np.array([0, -0.18 * length * t * t, -0.12 * length * t * t]) for t in np.linspace(0, 1, 8)]
            V, N, F = strand_tube(pts, 0.00042, 0.00006, sides=3)
            wV.append(V); wN.append(N); wI.append(F + off); off += len(V)
        for i in range(4):  # brow whiskers
            root = side_pt((0.040 + rng.uniform(-0.004, 0.004), 0.308 + rng.uniform(0, 0.006), 0.214 + rng.uniform(-0.008, 0.008)), sx)
            out = side_vec(nrm((0.7, 0.8, rng.uniform(-0.6, 0.2))), sx)
            length = rng.uniform(0.022, 0.04)
            pts = [root + out * length * t + np.array([0, -0.1 * length * t * t, -0.2 * length * t * t]) for t in np.linspace(0, 1, 6)]
            V, N, F = strand_tube(pts, 0.00035, 0.00006, sides=3)
            wV.append(V); wN.append(N); wI.append(F + off); off += len(V)
    parts.append(dict(name="Whiskers", P=np.concatenate(wV), N=np.concatenate(wN), UV=None, I=np.concatenate(wI),
                      weights=[("Nose", 0.6), ("Head", 0.4)], texture=None,
                      material={"name": "M_Hare_Whisker", "doubleSided": True,
                                "pbrMetallicRoughness": {"baseColorFactor": [0.035, 0.03, 0.028, 1], "metallicFactor": 0.0,
                                                         "roughnessFactor": 0.35}}))
    return parts


# ----------------------------------------------------------------------------- fur shading
def srgb(*c):
    return np.array(c, float) / 255.0


FUR_MID = srgb(112, 104, 95)
FUR_DARK = srgb(66, 61, 55)
FUR_LIGHT = srgb(152, 142, 130)
UNDER = srgb(172, 165, 154)
BACK_TINT = srgb(94, 93, 92)
EAR_IN = srgb(176, 158, 152)
EAR_RIM = srgb(96, 90, 85)
NOSE = srgb(118, 96, 92)
NOSTRIL = srgb(36, 26, 26)
EYELID = srgb(40, 33, 30)
EYE_RING = srgb(196, 190, 180)
CLAW = srgb(58, 52, 48)
TAIL_TOP = srgb(78, 74, 71)
TAIL_UNDER = srgb(222, 218, 210)


def shade(ctx):
    P, N, flow, tag, ao = ctx["P"], ctx["N"], ctx["flow"], ctx["tag"], ctx["ao"]
    n = len(P)
    t = lambda k: tag.get(k, np.zeros(n))
    sx = np.where(P[:, 0] >= 0, 1.0, -1.0)

    # strand noise: fine strands + clumps
    r1, g1 = strand_field(P, flow, fp=3000, fa=280, seed=1)
    r2, g2 = strand_field(P, flow, fp=950, fa=110, seed=2)
    patch = wave_noise(P, 55, seed=9) * 0.5 + wave_noise(P, 140, seed=10) * 0.3

    furry = 1 - np.clip(t("nose") + t("claw") + t("eyelid"), 0, 1)
    # agouti speckle: strand tips light/dark
    k = np.clip(0.5 + 0.14 * r1 + 0.10 * r2 + 0.10 * patch, 0, 1)
    col = FUR_DARK + (FUR_LIGHT - FUR_DARK) * k[:, None]
    col = col * (FUR_MID / ((FUR_DARK + FUR_LIGHT) / 2))

    # darker, cooler back and top of head; lighter underside
    top = smoothstep(0.35, 0.95, N[:, 1]) * np.clip(t("back") + t("haunch") + t("neck") + 0.6 * t("head"), 0, 1)
    col = col + (col * (BACK_TINT / FUR_MID) - col) * (0.55 * top)[:, None]
    under = smoothstep(-0.05, -0.65, N[:, 1]) * np.clip(t("belly") + t("chest") + t("chin") + t("leg") + t("haunch") + t("neck"), 0, 1)
    under = np.maximum(under, t("chest") * smoothstep(0.2, 0.9, N[:, 2]) * smoothstep(0.17, 0.10, P[:, 1]))
    inner_leg = (t("leg") + t("haunch")) * smoothstep(0.2, 0.8, -N[:, 0] * sx)
    under = np.clip(np.maximum(under, inner_leg * 0.8), 0, 1)
    ucol = UNDER * (0.92 + 0.16 * k)[:, None]
    col = col + (ucol - col) * (0.85 * under)[:, None]
    # muzzle + paws slightly lighter
    col = col * (1 + 0.10 * (t("muzzle") + t("chin") + t("paw")))[:, None]

    # eye ring (pale) around eye
    eye_c = np.stack([sx * EYE_C[0], np.full(n, EYE_C[1]), np.full(n, EYE_C[2])], 1)
    de = np.linalg.norm(P - eye_c, axis=1)
    ring = smoothstep(EYE_R + 0.0095, EYE_R + 0.0035, de) * (1 - t("eyelid"))
    col = col + (EYE_RING * (0.9 + 0.2 * k)[:, None] - col) * (0.55 * ring)[:, None]

    # ears: pinkish inner cup with veins, darker rim, grey back
    ear = t("ear")
    o = np.stack([sx * EAR_OPEN[0], np.zeros(n), np.full(n, EAR_OPEN[2])], 1)
    inner = ear * smoothstep(0.05, 0.55, np.einsum("ij,ij->i", N, o))
    vein = np.clip(1 - np.abs(wave_noise(P * np.array([1, 0.25, 1]), 420, seed=21)) * 2.2, 0, 1) ** 3
    icol = EAR_IN * (0.92 + 0.12 * patch)[:, None] * (1 - 0.18 * vein)[:, None]
    col = col + (icol - col) * (0.9 * inner)[:, None]
    d_tip = np.linalg.norm(P - (np.stack([sx * EAR_BASE[0], np.full(n, EAR_BASE[1]), np.full(n, EAR_BASE[2])], 1) +
                                np.stack([sx * EAR_DIR[0], np.full(n, EAR_DIR[1]), np.full(n, EAR_DIR[2])], 1) * EAR_LEN), axis=1)
    col = col * (1 - 0.35 * ear * smoothstep(0.04, 0.0, d_tip))[:, None]
    ear_back = ear * (1 - smoothstep(-0.1, 0.3, np.einsum("ij,ij->i", N, o)))
    col = col * (1 - 0.04 * ear_back)[:, None]

    # nose leather, nostrils, lip split
    nose = t("nose")
    col = col + (NOSE * (0.9 + 0.1 * patch)[:, None] - col) * nose[:, None]
    nos = np.zeros(n)
    for s_ in (1, -1):
        a = NOSE_TIP + np.array([s_ * 0.0035, 0.003, 0.009])
        b = NOSE_TIP + np.array([s_ * 0.0085, -0.0035, 0.004])
        ab = b - a
        h = np.clip(((P - a) @ ab) / (ab @ ab), 0, 1)
        dd = np.linalg.norm(P - a - h[:, None] * ab, axis=1)
        nos = np.maximum(nos, smoothstep(0.0030, 0.0016, dd))
    lip = smoothstep(0.0022, 0.0008, np.abs(P[:, 0])) * smoothstep(NOSE_TIP[1] - 0.003, NOSE_TIP[1] - 0.007, P[:, 1]) * smoothstep(NOSE_TIP[1] - 0.022, NOSE_TIP[1] - 0.017, P[:, 1]) * smoothstep(NOSE_TIP[2] - 0.019, NOSE_TIP[2] - 0.011, P[:, 2])
    col = col + (NOSTRIL - col) * np.clip(nos + lip, 0, 1)[:, None]
    # whisker pad dots
    pad = t("muzzle") * np.clip(wave_noise(P, 900, seed=31) - 1.2, 0, 1)
    col = col * (1 - 0.35 * pad)[:, None]

    # eyelid + claws + tail
    col = col + (EYELID - col) * t("eyelid")[:, None]
    col = col + (CLAW * (0.85 + 0.3 * k)[:, None] - col) * t("claw")[:, None]
    tail = t("tail")
    tcol = TAIL_TOP + (TAIL_UNDER - TAIL_TOP) * smoothstep(0.35, -0.6, N[:, 1] - 0.4 * N[:, 2])[:, None]
    tcol = tcol * (0.85 + 0.3 * k)[:, None]
    col = col + (tcol - col) * tail[:, None]

    # bake a little cavity AO into albedo (main AO goes to the occlusion map)
    col = col * (0.55 + 0.45 * ao)[:, None]

    rough = 0.84 - 0.06 * k
    rough = rough + (0.62 - rough) * inner
    rough = rough + (0.42 - rough) * nose
    rough = rough + (0.32 - rough) * t("claw")
    rough = rough + (0.38 - rough) * t("eyelid")

    fur = 0.20 * furry * (1 - 0.7 * inner) * (1 - 0.4 * t("muzzle"))
    fur = fur * (1 + 0.6 * t("tail"))
    grad = g1 * 0.8 + g2 * 0.5
    return dict(albedo=np.clip(col, 0, 1), roughness=np.clip(rough, 0, 1), fur=fur, grad=grad,
                occlusion=np.clip(ao ** 0.8, 0, 1))


FUR_MATERIAL_EXT = {"KHR_materials_sheen": {"sheenColorFactor": [0.52, 0.50, 0.47], "sheenRoughnessFactor": 0.55}}


# ----------------------------------------------------------------------------- animation
def _ear_twitch(t, t0, dur=0.35):
    x = (t - t0) / dur
    return math.sin(math.pi * x) ** 2 if 0 <= x <= 1 else 0.0


def anim_idle(t, T=4.0):
    w = 2 * math.pi / T
    q = {}
    breathe = math.sin(w * 2 * t)
    q["Spine"] = quat_axis((1, 0, 0), 0.012 * breathe)
    q["Chest"] = quat_axis((1, 0, 0), -0.010 * breathe)
    q["Neck"] = quat_axis((0, 1, 0), 0.10 * math.sin(w * t))
    q["Head"] = quat_mul(quat_axis((0, 1, 0), 0.08 * math.sin(w * t - 0.6)), quat_axis((1, 0, 0), 0.04 * math.sin(w * 2 * t)))
    sniff = 0.05 * max(0.0, math.sin(2 * math.pi * 7 * t)) * (1 if (t % T) < 1.6 else 0)
    q["Nose"] = quat_axis((1, 0, 0), -sniff)
    for s, sx in (("L", 1), ("R", -1)):
        tw = _ear_twitch(t % T, 1.1 if s == "L" else 2.6)
        q[f"Ear1.{s}"] = quat_mul(quat_axis((0, 1, 0), sx * (0.35 * tw + 0.08 * math.sin(w * t + sx))),
                                  quat_axis((1, 0, 0), -0.10 * tw))
        q[f"Ear2.{s}"] = quat_axis((1, 0, 0), 0.05 * math.sin(w * 2 * t + sx) - 0.08 * tw)
    q["Tail"] = quat_axis((1, 0, 0), -0.25 * _ear_twitch(t % T, 3.2, 0.25))
    return q


def anim_alert(t, T=2.0):
    a = math.sin(math.pi * min(t / 0.4, 1)) if t < 0.4 else 1.0
    a = min(t / 0.35, 1.0) if t < 1.6 else max(0.0, 1 - (t - 1.6) / 0.4)
    a = a * a * (3 - 2 * a)
    q = {"Neck": quat_axis((1, 0, 0), -0.22 * a), "Head": quat_axis((1, 0, 0), 0.12 * a),
         "Spine": quat_axis((1, 0, 0), -0.05 * a), "Chest": quat_axis((1, 0, 0), -0.06 * a)}
    for s, sx in (("L", 1), ("R", -1)):
        q[f"Ear1.{s}"] = quat_mul(quat_axis((0, 0, 1), -sx * 0.14 * a), quat_axis((1, 0, 0), 0.10 * a))
        q[f"Ear2.{s}"] = quat_axis((1, 0, 0), -0.06 * a)
    return q


ANIMATIONS = [("Idle", 4.0, 30, anim_idle), ("Alert", 2.0, 30, anim_alert)]
