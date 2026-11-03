"""World props: original procedural geometry exported as rigid, unskinned, unanimated glTF with a baked albedo atlas.

    python tools/ui-assets/gen_props.py [out_dir] [--only name[,name]]

Writes <out_dir>/<name>.glb (default: dream/animations/props/, where the engine's PropCatalog loads them) and
nothing else. The set is two kinds of prop:

  * the seven objects gen_iso_*.py drew as sprites that nothing in the engine ever consumed (rust_barrel,
    dead_oak, bramble, slate_rocks, tombstone, bone_pile and the rune flask of rune_potion_drop). Their MESHES are
    reused unchanged: the sprite renderer cannot be used in a perspective world, the geometry can;
  * the props a hamlet needs and the set lacked (brazier, well, cart, fence, crates, sacks, woodpile, signpost,
    scarecrow, three buildings, a ruined wall and standing stones).

Authored in u (16 u = 1 m, the isoforge scale), iso axes (x right, y forward = the prop's front, z up), standing on
z = 0 and centred on the origin, then exported at WORLD_UNITS_PER_U world units per u, Y up, +Z forward - so a
prop and the player share a scale (WORLD_UNITS_PER_U: the player is 1.2 world units, a barrel 0.8, a cottage door 1.4).

Texture. One baked albedo atlas per prop (charforge.unwrap_split + bake_albedo): albedo, occlusion, cavity, wear,
soot and dust, NO directional light. The engine applies its own ambient + N.L + the placed point lights, and the sun
moves. The atlas is encoded exactly as the player's is (gen_char_player.encode_atlas: the engine's actor shader is
linear in albedo and applies no tonemap, so the bytes ARE linear albedo and the lift is applied once, there).

Three glTF materials at most: "baked" (the atlas), "ember" (flat colour + emissive factor, the brazier's coals) and
"contact" (a flat ground disc of alpha-MASK contact occlusion; blending is off in the world pass, so it is a
ragged-edged dark patch, not a soft shadow, and it needs no framebuffer object and no new GL state).

All geometry is original. Colours are muted and dark: the palette ceilings are gated by tests/check_props.py.
"""
from __future__ import annotations

import hashlib
import importlib
import io
import math
import sys
from dataclasses import dataclass, replace
from pathlib import Path

import numpy as np
from PIL import Image

HERE = Path(__file__).resolve().parent
if str(HERE) not in sys.path:
    sys.path.insert(0, str(HERE))

import charforge as C  # noqa: E402
import gen_char_player as P  # noqa: E402
import gltfwrite as G  # noqa: E402
import isoforge as I  # noqa: E402
import uiart as U  # noqa: E402

TAU = math.tau
UNITS_PER_U = P.UNITS_PER_U        # the player glb's export scale, the unit the shared mesh plumbing converts with
# World scale. The engine normalises the player to 1.2 world units (PlayerModelAssetDefinition: twice the wolf's 0.6) and the
# figure is 30.7 u, so a prop that shares the player's scale is 1.2 / 30.72 world units per u (a cell is about 1.6 m).
WORLD_UNITS_PER_U = 0.039
ALBEDO_LIFT = P.ALBEDO_LIFT
DEFAULT_OUT = HERE.parents[1] / "dream" / "animations" / "props"

DISC_PX = 128
DISC_LIFT = 0.05            # baseColorFactor of the contact disc; its texture bytes span 0..1 of this
DISC_Y = 0.02               # world units above the terrain top face: clear of z-fighting at the iso distance
DISC_LINEAR = (0.030, 0.026, 0.022)   # linear albedo at the disc rim; the centre is half of it
EMBER_FACTOR = (0.30, 0.095, 0.030)      # linear emissive of the brazier's coals


@dataclass
class Part:
    mesh: I.Mesh
    mat: object                  # an IsoMat (baked) - unused when emit is set
    emit: tuple | None = None    # linear emissive colour: this part is a flat "ember" primitive, not in the atlas


@dataclass(frozen=True)
class PropSpec:
    build: object                # () -> list[Part], in u, z up, standing on z = 0
    atlas: int = 256
    disc: tuple = (0.0, 0.0)     # contact disc half-extents in u (x, y); (0, 0) = no disc
    weld: bool = False           # smooth the flat-shaded parts (thin twigs): shares vertices, a third of the size
    age: tuple = (0.40, 0.60, 0.0, 0.12)   # wear, grime, rust, dust
    scale: float = 1.0           # extra uniform scale on top of UNITS_PER_U
    source: str = "new"          # "new" or "sprite:<name>" for the seven reused ones
    notes: str = ""


# ---------------------------------------------------------------------------------------------- materials
def M(token: str, rough: float = 0.86, f0: float = 0.03, cap: float = 0.26, metal: bool = False, wear: float = 0.8):
    return C._mat(token, rough, f0, cap, metal, wear)


WOOD = M("#2a2723", 0.88, cap=0.20, wear=0.9)
WOOD_OLD = M("#3a342d", 0.90, wear=0.9, cap=0.18)
WOOD_DARK = M("#1d1b19", 0.90, cap=0.20, wear=0.7)
BOARD = M("#312e2a", 0.90, cap=0.20, wear=1.0)
THATCH = M("#413e35", 0.94, f0=0.025, cap=0.20, wear=0.4)
DAUB = M("#48453f", 0.95, f0=0.025, cap=0.18, wear=0.5)
STONE = M("#3c3d3e", 0.88, cap=0.12, wear=0.7)
STONE_DARK = M("#2b2c2e", 0.90, cap=0.14, wear=0.7)
IRON = C.GRIM_MATS["grim.iron.black"]
IRON_OLD = C.GRIM_MATS["grim.iron.pitted"]
ROPE = M("#37342e", 0.95, cap=0.22, wear=0.5)
BURLAP = M("#39362f", 0.96, cap=0.22, wear=0.5)
STRAW = M("#48443a", 0.95, cap=0.24, wear=0.3)
RAG = M("#27282b", 0.95, cap=0.16, wear=0.5)
SOIL = M("#262421", 0.95, cap=0.18, wear=0.2)
COAL = M("#1a1512", 0.9, cap=0.2, wear=0.0)


# ---------------------------------------------------------------------------------------------- mesh helpers
def cat(meshes):
    out = None
    for m in meshes:
        out = m if out is None else out.merge(m)
    return out


def bx(cx, cy, z0, w, d, h, rot=(0.0, 0.0, 0.0), chamfer=0.25):
    """Axis-aligned box standing on z0, then rotated about its own base centre and moved to (cx, cy, z0)."""
    return I.box(w, d, h, chamfer).xform(rot, (cx, cy, z0))


def rod(a, b, r, sides=6, r1=None):
    return I.tube([a, b], [r, r if r1 is None else r1], sides=sides, seg_u=max(2.0, float(np.linalg.norm(np.subtract(b, a))) / 3))


def prism(pts_xz, y0, y1):
    """A closed prism: polygon (x, z) pairs in the xz plane, extruded from y0 to y1. Flat shaded."""
    p = np.asarray(pts_xz, dtype=np.float64)
    n = len(p)
    v = np.vstack([np.column_stack((p[:, 0], np.full(n, y0), p[:, 1])), np.column_stack((p[:, 0], np.full(n, y1), p[:, 1]))])
    f = []
    for i in range(1, n - 1):
        f += [(0, i, i + 1), (n, n + i, n + i + 1)]
    for i in range(n):
        j = (i + 1) % n
        f += [(i, j, n + j), (i, n + j, n + i)]
    f = np.array(f, dtype=np.int32)
    return I.Mesh(v, I._orient_outward(v, f, v.mean(axis=0)))


def wobble(name, mesh, amp=0.25, lo=2.0, hi=5.0):
    return mesh.displace(name, amp, lo, hi)


# ---------------------------------------------------------------------------------------------- existing sprite props
def _scene_parts(module: str, fn: str, yaw_fix: float = 0.0):
    """The meshes and materials of a gen_iso_* scene, unchanged. yaw_fix undoes the sprite's own turn towards the
    camera, so the prop's front is +y."""
    sc = getattr(importlib.import_module(module), fn)()
    out = []
    for mesh, mid, _seed, _emit in sc.items:
        out.append(Part(mesh.xform((0, 0, yaw_fix)) if yaw_fix else mesh, sc.mats[mid]))
    return out


def barrel():
    return _scene_parts("gen_iso_props", "barrel_scene")


class _Limbs:
    """Stands in for gen_iso_nature._Tree while the oak is built, so each limb stays its own part and unwraps on
    its own cylinder (one merged 57k-triangle bark mesh has no sensible single projection)."""

    def __init__(self):
        self.bark, self.dead, self.n, self.parts = None, None, 0, []

    def add(self, m, wood=False):
        self.parts.append((m, wood))


def oak():
    mod = importlib.import_module("gen_iso_nature")
    real = mod._Tree
    mod._Tree = _Limbs
    try:
        tree = mod.oak_meshes()
    finally:
        mod._Tree = real
    out, bark, wood = [], [], []
    for m, w in tree.parts:
        (wood if w else bark).append(m.xform(scale=mod.SC))
    # thin twigs go together by thickness, the big limbs stay apart: a bundle per eight keeps the part count (and the
    # atlas rectangles) in hand without making any one cylinder projection absurd
    big = [m for m in bark if len(m.v) > 400]
    small = [m for m in bark if len(m.v) <= 400]
    out += [Part(m, mod.BARK) for m in big]
    out += [Part(cat(small[i:i + 8]), mod.BARK) for i in range(0, len(small), 8)]
    out += [Part(m, mod.DEADWOOD) for m in wood]
    return out


def _regrade(parts, index: int, albedo_scale: float, sat_cap: float | None = None, grey: float = 0.0):
    """The sprites were graded for a key light and a cold fill that the engine does not apply: the crimson leaves
    would be the loudest thing on the map and the slate would be a black hole. Rescale one material's albedo for the
    world, and optionally pull it towards its own grey (grey 0..1)."""
    part = parts[index]
    albedo = np.asarray(part.mat.albedo, dtype=np.float64) * albedo_scale
    albedo = albedo * (1.0 - grey) + albedo.mean() * grey
    mat = replace(part.mat, albedo=tuple(float(c) for c in albedo), sat_cap=part.mat.sat_cap if sat_cap is None else sat_cap)
    parts[index] = Part(part.mesh, mat, part.emit)
    return parts


def bramble():
    # the leaves (third item): a dull wine instead of crimson
    return _regrade(_scene_parts("gen_iso_nature", "bramble_scene"), 2, 0.55, 0.50)


def slate_rocks():
    # the slate: dark, but a stone and not a hole
    return _regrade(_scene_parts("gen_iso_nature", "slate_scene"), 0, 3.4, grey=0.55)


def tombstone():
    return _scene_parts("gen_iso_relics", "tomb_scene")


def bone_pile():
    return _scene_parts("gen_iso_relics", "bone_scene")


def rune_flask():
    mod = importlib.import_module("gen_iso_relics")
    glass, stop, wax = mod._bottle()
    dull_glass = M("#3a2c20", 0.55, f0=0.05, cap=0.30, wear=0.0)   # smoky amber: the sprite's glass is near-black, a world flask has to read
    return [Part(glass, dull_glass), Part(stop, mod.CORK), Part(wax, mod.WAX)]


# ---------------------------------------------------------------------------------------------- village props
def brazier():
    """An iron bowl on a splayed tripod; the coals are a flat emissive primitive. The bowl rim is at 10.6 u = 0.93
    units and the coals at about 9.7 u, so the placed light, FlameHeight 1.0 unit above the ground, sits just over them."""
    legs = []
    for i in range(3):
        a = TAU * (i + 0.15) / 3
        legs.append(rod((5.0 * math.cos(a), 5.0 * math.sin(a), 0.0), (2.4 * math.cos(a), 2.4 * math.sin(a), 7.6), 0.62, 6, 0.5))
    foot = cat([bx(5.0 * math.cos(TAU * (i + 0.15) / 3), 5.0 * math.sin(TAU * (i + 0.15) / 3), 0.0, 2.2, 2.2, 0.7, (0, 0, 40 * i), 0.2)
                for i in range(3)])
    bowl = I.lathe([(0.0, 7.2), (2.6, 7.4), (4.3, 8.5), (5.0, 10.3), (5.1, 10.7), (4.5, 10.8), (4.15, 10.4), (3.9, 8.9), (2.4, 8.0), (0.0, 7.8)], sides=14)
    band = I.torus(5.0, 0.38, sides=6, rings=14).xform((0, 0, 0), (0, 0, 9.7))
    rim = I.torus(4.8, 0.3, sides=6, rings=14).xform((0, 0, 0), (0, 0, 10.7))
    coals = I.lathe([(0.0, 9.9), (1.5, 9.8), (2.9, 9.4), (3.7, 8.6), (3.6, 8.2), (0.0, 8.2)], sides=11).displace("props.brazier.coals", 0.35, 1.5, 3.0)
    return [Part(cat(legs + [foot]), IRON_OLD), Part(cat([bowl, band, rim]), IRON), Part(coals, COAL, emit=EMBER_FACTOR)]


def well():
    stones = []
    for course, (z0, h, n, r0) in enumerate(((0.0, 6.5, 12, 14.5), (6.5, 6.0, 11, 14.2), (12.5, 5.5, 10, 14.0))):
        for i in range(n):
            a = TAU * (i + 0.5 * (course % 2) + 0.06 * math.sin(i * 2.7 + course)) / n
            wdt = TAU * r0 / n * 0.96
            stones.append(bx(r0 * math.cos(a), r0 * math.sin(a), z0, wdt, 5.2 + 0.5 * math.sin(i * 1.9), h * (0.94 + 0.06 * math.sin(i * 3.1 + course)),
                             (0, 0, math.degrees(a) + 90), 0.55))
    wall = wobble("props.well.stone", cat(stones), 0.2, 2.0, 4.0)
    water = I.lathe([(0.0, 5.0), (9.6, 5.0), (9.6, 4.6), (0.0, 4.6)], sides=18)
    posts = cat([bx(x, 0, 0.0, 2.6, 2.6, 46.0, (0, 0, 0), 0.3) for x in (-15.0, 15.0)])
    beam = rod((-17.5, 0, 42.0), (17.5, 0, 42.0), 1.5, 6)
    roller = rod((-12.0, 0, 36.5), (12.0, 0, 36.5), 1.8, 7)
    roof = cat([bx(0, 4.5 * s, 49.6, 40.0, 11.0, 1.6, (-s * 38.0, 0, 0), 0.3) for s in (1, -1)])
    rope = rod((6.0, 0, 36.5), (6.0, 0.0, 21.0), 0.38, 5)
    bucket = I.lathe([(0.0, 14.0), (2.6, 14.0), (3.2, 17.6), (3.4, 21.0), (2.9, 21.0), (2.7, 17.8), (0.0, 17.2)], sides=10).xform((0, 0, 0), (6.0, 0, 0))
    hoop = I.torus(3.0, 0.35, 5, 12).xform((0, 0, 0), (6.0, 0, 16.0))
    crank = cat([rod((12.0, 0, 36.5), (12.0, 7.0, 36.5), 0.7, 5), rod((12.0, 7.0, 36.5), (12.0, 7.0, 31.0), 0.7, 5)])
    return [Part(wall, STONE), Part(water, SOIL), Part(cat([posts, roof]), WOOD), Part(cat([beam, roller, crank]), WOOD_OLD),
            Part(cat([rope]), ROPE), Part(cat([bucket]), WOOD_DARK), Part(hoop, IRON)]


def cart():
    bed = bx(0, 0, 10.5, 26.0, 42.0, 2.0, (0, 0, 0), 0.3)
    sides = cat([bx(x, 0, 12.5, 1.6, 42.0, 7.0, (0, 0, 0), 0.3) for x in (-12.2, 12.2)]
                + [bx(0, y, 12.5, 24.0, 1.6, 7.0, (0, 0, 0), 0.3) for y in (-20.2, 20.2)])
    axle = rod((-16.0, 0, 10.0), (16.0, 0, 10.0), 1.1, 6)
    wheels, hubs = [], []
    for x in (-15.0, 15.0):
        rim = I.torus(9.6, 1.35, 6, 18).xform((0, 90, 0), (x, 0, 10.0))
        spokes = cat([rod((x, 0, 10.0), (x, 8.6 * math.cos(a), 10.0 + 8.6 * math.sin(a)), 0.55, 5) for a in [TAU * k / 7 for k in range(7)]])
        hubs.append(I.lathe([(0.0, -1.7), (1.9, -1.7), (1.9, 1.7), (0.0, 1.7)], sides=8).xform((0, 90, 0), (x, 0, 10.0)))
        wheels += [rim, spokes]
    shafts = cat([rod((x, 20.0, 12.0), (x * 1.15, 46.0, 1.4), 0.95, 6, 0.8) for x in (-8.0, 8.0)] +
                 [rod((-8.0, 46.0, 1.4), (8.0, 46.0, 1.4), 0.7, 5)])
    legs = cat([bx(x, -26.0, 0.0, 1.6, 1.6, 10.5, (0, 0, 0), 0.2) for x in (-9.0, 9.0)])
    return [Part(cat([bed, sides, legs]), BOARD), Part(cat([axle, shafts] + wheels), WOOD), Part(cat(hubs), IRON)]


def _fence(broken: bool):
    n = 3
    posts = []
    for i, x in enumerate((-16.0, 0.0, 16.0)):
        lean = (0.0, 10.0 if broken and i == 2 else 0.0, 0.0)
        posts.append(bx(x, 0, 0.0, 2.6, 2.4, 23.0, lean, 0.4))
    rails = [bx(0, -1.7, 8.0, 36.0, 1.6, 2.4, (0, 0, 0), 0.3), bx(0, -1.7, 15.5, 36.0, 1.6, 2.4, (0, 0, 0), 0.3)]
    if broken:
        rails[1] = bx(9.0, -1.7, 15.5, 18.0, 1.6, 2.4, (0, 14.0, 0), 0.3)
        rails.append(bx(-9.0, -1.7, 1.2, 16.0, 1.6, 2.4, (0, 0, 8), 0.3))   # the fallen rail
    return [Part(cat(posts), WOOD_OLD), Part(cat(rails), WOOD)]


def fence():
    return _fence(False)


def fence_broken():
    return _fence(True)


def _crate(cx, cy, z0, s, yaw, h=None):
    h = s if h is None else h
    parts = [bx(cx, cy, z0, s, s, h, (0, 0, yaw), 0.35)]
    for sx in (-1, 1):
        for sy in (-1, 1):
            parts.append(bx(cx + sx * (s / 2 - 0.5), cy + sy * (s / 2 - 0.5), z0, 1.1, 1.1, h + 0.5, (0, 0, yaw), 0.2).xform((0, 0, 0), (0, 0, 0)))
    return cat(parts)


def crates():
    a = _crate(-6.0, 0.0, 0.0, 15.0, 8.0, 13.0)
    b = _crate(9.0, 4.0, 0.0, 12.0, -14.0, 11.0)
    c = _crate(-3.0, 1.5, 13.0, 10.5, 31.0, 9.5)
    d = _crate(8.5, -9.5, 0.0, 9.0, 52.0, 8.0)
    lid = bx(8.5, -9.5, 8.0, 9.4, 9.4, 0.8, (0, 0, 52.0), 0.2)
    return [Part(cat([a, b, c, d, lid]), BOARD)]


def _sack(cx, cy, z0, yaw, s=1.0, tilt=0.0):
    prof = [(0.0, 0.0), (4.4, 0.0), (5.6, 3.2), (5.4, 6.5), (4.0, 9.0), (2.0, 10.4), (1.5, 11.0), (2.1, 12.3), (1.3, 13.2), (0.0, 13.2)]
    return I.lathe(prof, sides=9).displace(f"props.sack.{cx:.0f}.{cy:.0f}", 0.6, 3.0, 6.0).xform((tilt, 0, yaw), (cx, cy, z0), s)


def sacks():
    return [Part(cat([_sack(-6.0, 0.0, 0.0, 10.0), _sack(5.0, 2.0, 0.0, 120.0, 0.9, 8.0), _sack(0.0, -7.5, 0.0, 200.0, 0.85, -14.0),
                      _sack(-1.0, 3.5, 8.0, 70.0, 0.8, 24.0)]), BURLAP)]


def woodpile():
    rows = []
    for row, (z, n, r) in enumerate(((2.2, 6, 2.3), (6.4, 5, 2.2), (10.4, 4, 2.1), (14.3, 3, 2.0))):
        logs = []
        for i in range(n):
            x = (i - (n - 1) / 2) * 4.45
            ln = 30.0 - 2.5 * ((i * 7 + row * 3) % 4)
            logs.append(rod((-ln / 2 + 0.7 * math.sin(i + row), x + 0.3 * math.sin(row * 5 + i), z), (ln / 2, x, z + 0.25 * math.sin(i * 2.3)), r, 6))
        rows.append(cat(logs))
    pile = cat(rows)
    block = I.lathe([(0.0, 0.0), (5.2, 0.0), (5.0, 4.0), (4.6, 8.4), (0.0, 8.4)], sides=9).displace("props.block", 0.35, 2.0, 4.0).xform((0, 0, 0), (-4.0, 17.0, 0.0))
    axe = cat([rod((-4.0, 17.0, 8.2), (2.0, 17.0, 7.4), 0.55, 5), bx(-4.8, 17.0, 7.1, 2.6, 0.6, 4.2, (0, 0, 0), 0.2)])
    chips = cat([bx(8.0 * math.cos(i * 2.1) - 1.0, 19.0 + 6.0 * math.sin(i * 1.7), 0.0, 3.0, 1.1, 0.5, (0, 0, i * 53.0), 0.15) for i in range(7)])
    return [Part(pile, WOOD), Part(cat([block, chips]), WOOD_OLD), Part(axe, IRON_OLD)]


def signpost():
    post = bx(0, 0, 0.0, 2.8, 2.8, 56.0, (0, 0, 0), 0.4)
    arm = bx(11.0, 0, 50.0, 24.0, 2.0, 2.2, (0, 0, 0), 0.3)
    brace = rod((0.0, 0, 38.0), (9.0, 0, 49.0), 0.9, 5)
    chains = cat([rod((x, 0, 50.2), (x, 0, 43.2), 0.28, 4) for x in (17.5, 24.0)])
    board = cat([bx(20.75, 0, 31.0, 14.0, 1.6, 12.0, (0, 0, 0), 0.35), bx(20.75, 1.0, 33.0, 10.0, 0.6, 8.0, (0, 0, 0), 0.2)])
    base = I.convex("props.signpost.base", 3.6, 2, (1.2, 1.2, 0.5)).xform((0, 0, 0), (0, 0, 0.5))
    return [Part(cat([post, arm, brace]), WOOD_OLD), Part(chains, IRON), Part(board, BOARD), Part(base, STONE)]


def scarecrow():
    pole = rod((0, 0, 0.0), (0, 0.2, 40.0), 1.1, 6)
    cross = rod((-12.0, 0.3, 30.0), (12.0, 0.3, 29.0), 0.8, 5)
    body = I.lathe([(0.0, 16.0), (3.6, 17.0), (4.2, 22.0), (3.4, 29.0), (2.0, 32.0), (0.0, 32.4)], sides=8).displace("props.scare.body", 0.5, 2.0, 5.0).xform((0, 0, 0), (0, 0.4, 0))
    head = I.lathe([(0.0, 31.0), (2.6, 32.0), (3.4, 35.0), (2.8, 38.0), (1.2, 39.5), (0.0, 39.6)], sides=8).displace("props.scare.head", 0.4, 2.0, 4.0).xform((0, 0, 0), (0, 0.6, 0.5))
    hat = cat([I.lathe([(0.0, 38.5), (6.2, 38.5), (6.2, 39.2), (3.0, 39.6), (0.0, 39.6)], sides=10),
               I.lathe([(0.0, 39.4), (3.0, 39.4), (2.4, 45.0), (0.0, 48.0)], sides=8).xform((8.0, 0, 0), (0, 0.5, 0))])
    arms = cat([I.lathe([(0.0, -10.0), (1.6, -9.0), (1.9, 0.0), (0.0, 0.8)], sides=6).xform((0, 90 * s + 4 * s, 0), (s * 3.2, 0.3, 29.5)) for s in (1, -1)])
    straw = cat([rod((s * (11.0 + 0.8 * k), 0.5, 29.0 - 0.5 * k), (s * (13.0 + 1.2 * k), 0.5, 24.0 - 2.2 * k), 0.35, 4, 0.05) for s in (1, -1) for k in range(4)]
                + [rod((1.5 * k - 3.0, 1.0, 17.0), (1.8 * k - 3.6, 1.2, 12.0 - k), 0.35, 4, 0.05) for k in range(5)])
    cloak = cat([bx(0, -2.4, 14.0, 9.5, 0.7, 16.0, (6.0, 0, 0), 0.2)])
    return [Part(cat([pole, cross]), WOOD_OLD), Part(cat([body, head, arms]), BURLAP), Part(hat, RAG), Part(straw, STRAW), Part(cloak, RAG)]


def _house(w, d, wall_h, roof_deg, door_x, windows, chimney_x, name, lean_to=False, sign=False, door_w=12.0):
    """Wattle-and-daub on a stone footing, a timber frame, a steep thatch roof. Front is +y."""
    foot = bx(0, 0, -14.0, w + 3.0, d + 3.0, 20.0, (0, 0, 0), 0.8)   # reaches 14 u (a cell) below the ground, so a house may span one step
    foot = wobble("props.house." + name + ".foot", foot.xform(), 0.15, 2.0, 4.0)
    walls = bx(0, 0, 6.0, w, d, wall_h - 6.0, (0, 0, 0), 0.5)
    hw, hd = w / 2, d / 2
    frame = []
    for x in (-hw, hw):
        for y in (-hd, hd):
            frame.append(bx(x, y, 6.0, 3.0, 3.0, wall_h - 6.0 + 1.0, (0, 0, 0), 0.4))
    n_mid = max(1, int(w // 34))
    for k in range(1, n_mid + 1):
        x = -hw + w * k / (n_mid + 1)
        frame.append(bx(x, hd + 0.3, 6.0, 2.2, 1.0, wall_h - 6.0, (0, 0, 0), 0.2))
        frame.append(bx(x, -hd - 0.3, 6.0, 2.2, 1.0, wall_h - 6.0, (0, 0, 0), 0.2))
    for z in (wall_h - 2.0, 16.0):
        frame.append(bx(0, hd + 0.3, z, w + 3.0, 1.2, 2.2, (0, 0, 0), 0.2))
        frame.append(bx(0, -hd - 0.3, z, w + 3.0, 1.2, 2.2, (0, 0, 0), 0.2))
        frame.append(bx(hw + 0.3, 0, z, 1.2, d + 3.0, 2.2, (0, 0, 0), 0.2))
        frame.append(bx(-hw - 0.3, 0, z, 1.2, d + 3.0, 2.2, (0, 0, 0), 0.2))
    # braces: diagonals on the side walls
    frame.append(rod((hw + 0.5, -hd + 2.0, 7.0), (hw + 0.5, -hd + d * 0.45, wall_h - 3.0), 0.9, 5))
    frame.append(rod((-hw - 0.5, hd - 2.0, 7.0), (-hw - 0.5, hd - d * 0.45, wall_h - 3.0), 0.9, 5))
    # roof: two thatch panels over the ridge (along x), overhanging every side
    a = math.radians(roof_deg)
    run = hd + 6.0
    rise = run * math.tan(a)
    span_len = run / math.cos(a)
    ridge_z = wall_h + rise - 1.0
    panels = []
    for s in (1, -1):
        c = (0.0, s * run / 2, wall_h - 1.0 + rise / 2 + 0.6)
        panels.append(bx(0, 0, 0.0, w + 9.0, span_len, 4.0, (-s * roof_deg, 0, 0), 0.9).xform((0, 0, 0), c))
    roof = wobble("props.house." + name + ".thatch", cat(panels), 0.35, 3.0, 6.0)
    ridge = bx(0, 0, ridge_z - 1.6, w + 10.0, 3.0, 3.0, (0, 0, 0), 0.8)
    gable = [prism([(-hd + 0.2, wall_h - 1.0), (hd - 0.2, wall_h - 1.0), (0.0, ridge_z - 2.0)], -hw + 0.4, hw - 0.4).xform((0, 0, 90))]
    gable = [m.xform((0, 0, 0)) for m in gable]
    # door and windows on the front
    door_h = wall_h - 7.0
    wz = 6.0 + (wall_h - 6.0) * 0.42
    door = cat([bx(door_x, hd + 0.4, 6.0, door_w, 1.8, door_h, (0, 0, 0), 0.4),
                bx(door_x, hd + 0.2, 6.0, door_w + 3.6, 1.2, door_h + 2.0, (0, 0, 0), 0.3)])
    step = bx(door_x, hd + 3.4, 0.0, door_w + 4.0, 5.0, 3.0, (0, 0, 0), 0.8)
    shutters = []
    for wx in windows:
        shutters.append(bx(wx, hd + 0.4, wz, 9.0, 1.2, 9.0, (0, 0, 0), 0.3))
        shutters.append(bx(wx - 5.8, hd + 1.0, wz, 3.4, 0.9, 9.0, (0, 0, 8.0), 0.2))
        shutters.append(bx(wx + 5.8, hd + 1.0, wz, 3.4, 0.9, 9.0, (0, 0, -8.0), 0.2))
    chimney = bx(chimney_x, -4.0, wall_h - 4.0, 8.0, 8.0, rise + 12.0, (0, 0, 0), 0.5)
    chimney_cap = bx(chimney_x, -4.0, wall_h + rise + 7.8, 10.0, 10.0, 2.0, (0, 0, 0), 0.6)
    parts = [Part(cat([foot, step, chimney, chimney_cap]), STONE), Part(cat([walls] + gable), DAUB), Part(cat(frame + [ridge]), WOOD_DARK),
             Part(roof, THATCH), Part(cat([door]), WOOD), Part(cat(shutters), BOARD)]
    if lean_to:
        lt = [bx(w / 2 + 11.0, 0, -14.0, 22.0, d - 8.0, 17.0, (0, 0, 0), 0.4)]
        lt.append(bx(0, 0, 0.0, 1.0, 1.0, 0.1).xform((0, 0, 0), (w / 2 + 11.0, 0, 0.0)))
        roof2 = bx(w / 2 + 11.5, 0, 0.0, 26.0, d - 4.0, 3.0, (0, -22.0, 0), 0.8).xform((0, 0, 0), (0, 0, wall_h - 12.0))
        posts = cat([bx(w / 2 + 21.0, y, 3.0, 2.6, 2.6, wall_h - 15.0, (0, 0, 0), 0.3) for y in (-d / 2 + 5.0, d / 2 - 5.0)])
        parts += [Part(cat(lt), STONE), Part(wobble("props.house.leanto", roof2, 0.3, 3.0, 6.0), THATCH), Part(posts, WOOD_DARK)]
    if sign:
        arm = cat([bx(w / 2 - 6.0, hd + 7.0, 33.0, 2.4, 14.0, 2.2, (0, 0, 0), 0.3), rod((w / 2 - 6.0, hd + 2.0, 24.0), (w / 2 - 6.0, hd + 11.0, 33.0), 0.8, 5)])
        board = bx(w / 2 - 6.0, hd + 12.5, 22.0, 1.6, 12.0, 11.0, (0, 0, 0), 0.3)
        parts += [Part(arm, IRON), Part(board, BOARD)]
    return parts


def cottage():
    return _house(80.0, 60.0, 44.0, 42.0, -20.0, (20.0,), 28.0, "cottage")


def inn():
    return _house(128.0, 76.0, 50.0, 40.0, 0.0, (-38.0, 38.0), -46.0, "inn", lean_to=True, sign=True, door_w=16.0)


def hovel():
    return _house(56.0, 46.0, 42.0, 46.0, 6.0, (), -18.0, "hovel", door_w=12.0)


def ruined_wall():
    blocks = []
    r = U.rng("props.ruined_wall")
    x = -40.0
    for course, (z0, h) in enumerate(((0.0, 7.0), (7.0, 6.5), (13.5, 6.0), (19.5, 5.5))):
        x = -40.0
        while x < 40.0:
            wdt = float(r.uniform(8.0, 15.0))
            hmax = 9.0 + 17.0 * (0.5 + 0.5 * math.sin(x * 0.07 + 1.2))    # the wall has crumbled to a ragged top
            if z0 < hmax:
                blocks.append(bx(x + wdt / 2, float(r.uniform(-0.6, 0.6)), z0, wdt * 0.96, 6.4, h * 0.96, (0, 0, float(r.uniform(-2.5, 2.5))), 0.7))
            x += wdt
    wall = wobble("props.ruined_wall.stone", cat(blocks), 0.25, 2.0, 4.0)
    rubble = cat([I.convex(f"props.ruined_wall.r{i}", float(r.uniform(2.2, 3.8)), 4, (1.2, 1.0, 0.6)).xform((0, 0, float(r.uniform(0, 360))), (float(r.uniform(-34, 40)), float(r.uniform(5.0, 11.0)) * (1 if i % 2 else -1), 1.6)) for i in range(11)])
    return [Part(wall, STONE), Part(rubble, STONE_DARK)]


def standing_stones():
    r = U.rng("props.standing_stones")
    stones = []
    for i, (x, y, rad, tall, lean, yaw) in enumerate(((0.0, 0.0, 6.5, 2.7, 4.0, 20.0), (-17.0, 7.0, 5.0, 2.2, -10.0, 70.0), (15.0, -10.0, 5.4, 2.0, 16.0, 150.0), (13.0, 14.0, 4.2, 1.5, -7.0, 10.0))):
        s = I.convex(f"props.standing.{i}", rad, 5, (0.62, 0.50, tall))
        s = s.xform((lean * 0.5, lean, yaw))
        lo = float(s.v[:, 2].min())
        stones.append(s.xform((0, 0, 0), (x, y, -lo - 0.6)))
    rubble = cat([I.convex(f"props.standing.r{i}", float(r.uniform(1.6, 3.0)), 4, (1.3, 1.0, 0.5)).xform((0, 0, float(r.uniform(0, 360))), (float(r.uniform(-20, 20)), float(r.uniform(-14, 18)), 0.9)) for i in range(9)])
    return [Part(wobble("props.standing.surface", cat(stones), 0.15, 2.0, 5.0), STONE), Part(rubble, STONE_DARK)]


PROPS: dict[str, PropSpec] = {
    # ---- the seven reused sprite objects (meshes unchanged; materials are the sprites' own)
    "rust_barrel": PropSpec(barrel, 512, (10.5, 10.5), age=(0.40, 0.60, 0.30, 0.05), source="sprite:rust_barrel"),
    "dead_oak": PropSpec(oak, 1024, (16.0, 16.0), weld=True, age=(0.35, 0.60, 0.0, 0.08), source="sprite:dead_oak"),
    "bramble": PropSpec(bramble, 512, (22.0, 22.0), age=(0.55, 0.50, 0.0, 0.05), source="sprite:bramble"),
    "slate_rocks": PropSpec(slate_rocks, 512, (19.0, 17.0), age=(0.25, 0.40, 0.0, 0.04), source="sprite:slate_rocks"),
    "tombstone": PropSpec(tombstone, 512, (11.0, 8.0), age=(0.15, 0.60, 0.0, 0.10), source="sprite:tombstone"),
    "bone_pile": PropSpec(bone_pile, 512, (21.0, 11.0), age=(0.30, 0.65, 0.12, 0.15), source="sprite:bone_pile"),
    "rune_flask": PropSpec(rune_flask, 256, (6.0, 6.0), age=(0.2, 0.4, 0.0, 0.05), scale=0.55, source="sprite:rune_potion_drop"),
    # ---- generated for the village
    "brazier": PropSpec(brazier, 256, (7.5, 7.5), age=(0.5, 0.7, 0.35, 0.05)),
    "well": PropSpec(well, 512, (19.0, 19.0), age=(0.45, 0.65, 0.10, 0.10)),
    "cart": PropSpec(cart, 512, (21.0, 28.0), age=(0.45, 0.65, 0.2, 0.12)),
    "fence": PropSpec(fence, 256, (20.0, 3.5), age=(0.5, 0.6, 0.0, 0.10)),
    "fence_broken": PropSpec(fence_broken, 256, (20.0, 5.0), age=(0.6, 0.65, 0.0, 0.10)),
    "crates": PropSpec(crates, 256, (16.0, 17.0), age=(0.5, 0.6, 0.0, 0.12)),
    "sacks": PropSpec(sacks, 256, (12.0, 13.0), age=(0.4, 0.6, 0.0, 0.15)),
    "woodpile": PropSpec(woodpile, 512, (19.0, 24.0), age=(0.4, 0.6, 0.0, 0.10)),
    "signpost": PropSpec(signpost, 256, (7.0, 6.0), age=(0.5, 0.6, 0.15, 0.08)),
    "scarecrow": PropSpec(scarecrow, 256, (7.0, 6.0), age=(0.4, 0.6, 0.0, 0.10)),
    "cottage": PropSpec(cottage, 512, (40.0, 33.0), age=(0.45, 0.6, 0.0, 0.12)),
    "inn": PropSpec(inn, 1024, (70.0, 40.0), age=(0.45, 0.6, 0.1, 0.12)),
    "hovel": PropSpec(hovel, 512, (30.0, 27.0), age=(0.55, 0.7, 0.0, 0.12)),
    "ruined_wall": PropSpec(ruined_wall, 512, (45.0, 14.0), age=(0.55, 0.6, 0.0, 0.12)),
    "standing_stones": PropSpec(standing_stones, 512, (26.0, 24.0), age=(0.4, 0.6, 0.0, 0.10)),
}


# ---------------------------------------------------------------------------------------------- the contact disc
def contact_texture(seed: str):
    """-> (PNG bytes, RGBA array). A 128 px square: a ragged-edged disc of dithered dirt. Alpha-MASK has no partial
    coverage, so a solid disc reads as a black pit; instead the coverage thins from the centre (all covered) to the rim
    (a fifth covered) by a clumped stipple, which reads as a soft patch of trampled, darkened ground at gameplay size.
    Alpha is 255 or 0. The bytes span 0..1 of DISC_LIFT, a linear albedo like the atlas."""
    r = U.rng("props.disc." + seed)
    ph = r.random(5) * TAU
    ys, xs = np.mgrid[0:DISC_PX, 0:DISC_PX]
    c = (DISC_PX - 1) / 2.0
    dx, dy = (xs - c) / c, (ys - c) / c
    rad = np.hypot(dx, dy)
    th = np.arctan2(dy, dx)
    edge = 0.84 + 0.07 * np.sin(3 * th + ph[0]) + 0.05 * np.sin(5 * th + ph[1]) + 0.04 * np.sin(9 * th + ph[2]) + 0.03 * np.sin(13 * th + ph[3])
    rel = np.clip(rad / np.maximum(edge, 1e-6), 0, 1)
    t = np.clip((rel - 0.25) / 0.75, 0, 1)
    density = 1.0 - 0.80 * t * t * (3 - 2 * t)
    noise = r.random((DISC_PX, DISC_PX))
    noise = (noise + np.roll(noise, 1, 0) + np.roll(noise, 1, 1) + np.roll(noise, (1, 1), (0, 1))) / 4.0   # clumps of 2 px
    order = np.argsort(np.argsort(noise.ravel())).reshape(noise.shape)
    uniform = (order + 0.5) / noise.size
    inside = (rad < edge) & (uniform < density)
    k = (0.5 + 0.5 * rel ** 1.5)[..., None]
    lin = np.asarray(DISC_LINEAR)[None, None, :] * k
    mottle = 1.0 + 0.25 * np.sin(xs * 0.45 + ph[4])[..., None] * np.sin(ys * 0.55 + ph[0])[..., None]
    enc = np.zeros((DISC_PX, DISC_PX, 4), dtype=np.uint8)
    enc[..., :3] = np.round(np.clip(lin * mottle / DISC_LIFT, 0, 1) * 255).astype(np.uint8)
    enc[..., 3] = np.where(inside, 255, 0)
    buf = io.BytesIO()
    Image.fromarray(enc, "RGBA").save(buf, format="PNG", compress_level=9)
    return buf.getvalue(), enc


def disc_arrays(rx_u: float, ry_u: float, scale: float):
    """Flat fan in glTF space (Y up), DISC_Y above the ground, uv over the whole texture, normal up."""
    n = 28
    a = np.arange(n) * TAU / n
    rx, ry = rx_u * WORLD_UNITS_PER_U * scale, ry_u * WORLD_UNITS_PER_U * scale
    pos = np.vstack([[0.0, DISC_Y, 0.0], np.column_stack((rx * np.cos(a), np.full(n, DISC_Y), ry * np.sin(a)))]).astype(np.float32)
    nrm = np.tile(np.array([0.0, 1.0, 0.0], dtype=np.float32), (n + 1, 1))
    uv = np.vstack([[0.5, 0.5], np.column_stack((0.5 + 0.5 * np.cos(a), 0.5 + 0.5 * np.sin(a)))]).astype(np.float32)
    idx = np.array([(0, 1 + (i + 1) % n, 1 + i) for i in range(n)], dtype=np.uint32)   # CCW seen from +Y
    return pos, nrm, uv, idx


# ---------------------------------------------------------------------------------------------- build
def _weld(m: I.Mesh) -> I.Mesh:
    if m.n is not None:
        return m
    return I.Mesh(m.v, m.f, I._smooth_normals(m.v, m.f), m.uv, m.tint)


def _clean(pos, nrm, uv, idx):
    """What a strict glTF loader (SharpGLTF validates every accessor) needs: unit normals everywhere - a lathe pole
    has none of its own - and no zero-area triangles."""
    n = nrm.astype(np.float64)
    ln = np.linalg.norm(n, axis=1)
    ok = np.isfinite(ln) & (ln > 1e-6)
    n[~ok] = (0.0, 1.0, 0.0)
    n[ok] /= ln[ok, None]
    v = pos.astype(np.float64)[idx]
    area = np.linalg.norm(np.cross(v[:, 1] - v[:, 0], v[:, 2] - v[:, 0]), axis=1)
    return pos, n.astype(np.float32), uv, idx[area > 1e-10]


def _merge_arrays(items):
    pos, nrm, uvs, idx, off = [], [], [], [], 0
    for p, n, u, i in (_clean(*it) for it in items):
        pos.append(p), nrm.append(n), uvs.append(u), idx.append(i + off)
        off += len(p)
    return np.vstack(pos), np.vstack(nrm), np.vstack(uvs), np.vstack(idx).astype(np.uint32)


def build_asset(name: str) -> dict:
    """Pure function of the source: nothing here reads the clock, the environment or the filesystem."""
    spec = PROPS[name]
    parts = [p for p in spec.build() if p.mesh is not None]
    baked = [p for p in parts if p.emit is None]
    ember = [p for p in parts if p.emit is not None]
    meshes = [(_weld(p.mesh) if spec.weld else p.mesh) for p in baked]
    split, uvs, rects = C.unwrap_split(meshes, spec.atlas)
    wear, grime, rust, dust = spec.age
    atlas = C.bake_albedo(split, None, None, uvs, rects, spec.atlas, materials=[p.mat for p in baked], seed="prop." + name,
                          wear=wear, grime=grime, rust=rust, dust=dust)
    png, scale, enc = P.encode_atlas(atlas, ALBEDO_LIFT)

    k = spec.scale * WORLD_UNITS_PER_U / UNITS_PER_U
    arrays = []
    for m, uv in zip(split, uvs):
        pos, nrm, uvo, idx = P._render_arrays(m, uv)
        arrays.append((pos * k, nrm, uvo, idx))
    prims = []
    pos, nrm, uvo, idx = _merge_arrays(arrays)
    prims.append(G.GltfPrimitive(pos, nrm, uvo, idx, None, None, material=0, node=0))
    mats = [{"name": "prop.baked", "base_color_factor": (scale, scale, scale, 1.0), "base_color_png": png, "metallic": 0.0,
             "roughness": 0.9, "double_sided": True}]
    if ember:
        ar = []
        for p in ember:
            ar.append(P._render_arrays(p.mesh, np.zeros((len(p.mesh.v), 2), dtype=np.float32)))
        e_pos, e_nrm, e_uv, e_idx = _merge_arrays([(a * k, b, c, d) for a, b, c, d in ar])
        mats.append({"name": "prop.ember", "base_color_factor": (0.020, 0.012, 0.009, 1.0), "metallic": 0.0, "roughness": 0.9,
                     "emissive_factor": ember[0].emit})
        prims.append(G.GltfPrimitive(e_pos, e_nrm, e_uv, e_idx, None, None, material=len(mats) - 1, node=0))
    disc = None
    if spec.disc[0] > 0:
        dpng, denc = contact_texture(name)
        mats.append({"name": "prop.contact", "base_color_factor": (DISC_LIFT, DISC_LIFT, DISC_LIFT, 1.0), "base_color_png": dpng,
                     "metallic": 0.0, "roughness": 1.0, "alpha_mode": "MASK", "alpha_cutoff": 0.5})
        dp, dn, du, di = disc_arrays(spec.disc[0], spec.disc[1], spec.scale)
        prims.append(G.GltfPrimitive(dp, dn, du, di, None, None, material=len(mats) - 1, node=0))
        disc = denc
    nodes = [G.GltfNode(name=name, parent=None)]
    return dict(name=name, nodes=nodes, primitives=prims, materials=mats, atlas=atlas, encoded=enc, scale=scale, rects=rects,
                parts=baked, meshes=split, uvs=uvs, disc=disc, spec=spec)


def glb_bytes(asset: dict) -> bytes:
    return G.build_glb(asset["nodes"], asset["primitives"], None, None, [], asset["materials"])


def main(out_dir: str | None = None, only: list[str] | None = None) -> Path:
    out = Path(out_dir) if out_dir else DEFAULT_OUT
    out.mkdir(parents=True, exist_ok=True)
    names = [n for n in PROPS if not only or n in only]
    unknown = [n for n in (only or []) if n not in PROPS]
    if unknown:
        raise SystemExit(f"unknown props: {unknown}")
    for name in names:
        a = build_asset(name)
        data = glb_bytes(a)
        (out / f"{name}.glb").write_bytes(data)
        tris = sum(len(p.indices) for p in a["primitives"])
        verts = sum(len(p.positions) for p in a["primitives"])
        print(f"gen_props: {name:16s} parts={len(a['parts']):3d} verts={verts:6d} tris={tris:6d} atlas={a['spec'].atlas} "
              f"size={len(data):8d} sha1={hashlib.sha1(data).hexdigest()[:12]}")
    return out


if __name__ == "__main__":
    args = [x for x in sys.argv[1:] if not x.startswith("--")]
    only_arg = None
    if "--only" in sys.argv:
        only_arg = sys.argv[sys.argv.index("--only") + 1].split(",")
        args = [x for x in args if x != sys.argv[sys.argv.index("--only") + 1]]
    main(args[0] if args else None, only_arg)
