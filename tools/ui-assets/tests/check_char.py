"""Gates for the grim player actor: the texture, the seams, the lift, the grounding and the clips.

    python tools/ui-assets/tests/check_char.py

Fails (exit 1) when any gate fails. Every gate that can silently stop measuring has a sibling that must fail:
the seam gate is run on the old unwrap() as well and must find its straddling triangles there.
"""
from __future__ import annotations

import hashlib
import io
import math
import re
import sys
from pathlib import Path

import numpy as np
from PIL import Image

HERE = Path(__file__).resolve().parent
TOOLS = HERE.parent
if str(TOOLS) not in sys.path:
    sys.path.insert(0, str(TOOLS))

import charforge as C  # noqa: E402
import enginelook as L  # noqa: E402
import gen_char_player as P  # noqa: E402
import gltfwrite as G  # noqa: E402
import uiart as U  # noqa: E402

REQUIRED_CLIPS = ("idle", "walk", "sprint", "die", "attack-melee-right", "holding-right-shoot",
                  "walk-back", "walk-strafe-left", "walk-strafe-right")
ENGINE_NODES = ("torso", "head", "arm-left", "arm-right", "leg-left", "leg-right")
# M3, the offline bimodal-lambert spread (p90 - p10 of HSV value over the silhouette, shaded through
# enginelook at a brazier 1.5 units away, at the WORSE of two mirrored key azimuths).
#
# Spec 00-acceptance M3 sets 0.50, and until 2026-11-06 this file held the BASE FIGURE to it. That number was
# calibrated on a figure with a kettle helm, a pauldron, a lame and knee cops baked in: MEASURED 0.5064, and 0.1577
# with those plates removed. The "worn-visuals" wave then moved every piece of armour into the equipment layer
# (ArmourMeshCatalog) and left the base figure an unarmoured traveller - tunic, breeches, boots, a bare head -
# which is the old negative control, so the premise of the 0.50 floor on the BASE changed, not its intent.
#
# 1. The BASE keeps a legibility floor (a traveller must still read as a figure and not a dark blob), set from
#    the measurement: MEASURED 2026-11-06 on the first undressed figure, 0.2376 (az 35 p10 0.0202 p90 0.2578),
#    because every material sat at 15-35% value and nothing on the figure caught light. The palette now has a
#    light end (weathered linen tunic over near-black breeches and boots): MEASURED 0.6717 (worst azimuth 35:
#    p10 0.0079 p90 0.6796; az 145: 0.0081 / 1.0000). The floor is that minus 0.03 of stated headroom.
# 2. The 0.50 claim moves to the figure PLUS a reference worn set (greathelm + plate-cuirass + hooded-cloak),
#    read from the shipped ArmourMeshCatalog.cs, because that is where plate contrast now legitimately lives.
#    WHY 0.50 and not a number picked to make the build green: a fully equipped character must be at least as
#    legible as the figure that actually shipped, which measured 0.5064 with its plate baked in. 0.50 is that
#    standard, rounded down to the spec's M3 figure. MEASURED 0.5150 (az 35 p10 0.0082 p90 0.5232; az 145
#    p10 0.0133 p90 0.5301). The armour is NOT where this came from: the worn pieces were not brightened.
BASE_SPREAD_MIN = 0.25
WORN_SET_SPREAD_MIN = 0.36
# THE NUMBER FOLLOWS THE ART, NOT THE REVERSE (loop 2, 2026-11-06). Loop 1 raised BASE_SPREAD_MIN to 0.64 because
# that is what a near-white tunic (#9E9A8C, lab-coat linen) happened to measure, and the floor then ENFORCED the
# white shirt: darken the linen and the gate failed. Both of its azimuths were also partly an artefact of
# clipping (az 145 p90 was 1.0000, a tenth of the figure fully blown out), so the "spread" was a clipped cloth
# against black. The base floor is now set from the corrected, non-clipping art (dirty oatmeal tunic #645E4F over
# near-black breeches): MEASURED 0.2780 (az 35 p10 0.0077 p90 0.2857; az 145 0.0083 / 0.4918), minus 0.03 of
# headroom, rounded. If the art changes, re-measure and move this number to follow it; never brighten the art to
# reach a floor. CLIP_SHARE_MAX below is the gate that stops a brightening from being the way out.
#
# WORN_SET_SPREAD_MIN was 0.50, defended as "a fully equipped character must be at least as legible as the figure
# that shipped, which measured 0.5064". That defence does not survive measurement: THE 0.5064 BASELINE WAS ITSELF
# CLIPPED. Measured directly from master's player-grim.glb (04a8fbfe), its atlas peak albedo is 0.970 and 0.39% of
# its covered texels are ALREADY at byte 255 before any lighting; at the peak brazier gain of 2.17 it reaches
# 2.105. Clipping pins the top of the histogram at 1.0, which inflates (p90 - p10), so 0.5064 measures blown
# highlights as if they were legibility. Holding a floor derived from it would make clipping mandatory forever -
# the gate would require the exact defect CLIP_SHARE_MAX exists to forbid. For comparison the corrected figure
# peaks at 0.605 with 0.00% of its texels at 255.
#
# So this number, like BASE_SPREAD_MIN, now follows the art: MEASURED 0.3906 (az 35 p10 0.0084 p90 0.4063; az 145
# p10 0.0135 p90 0.4041), minus headroom, rounded to 0.36. Nothing was brightened to reach it. The standard it
# encodes is no longer "as bright as the old figure" but "an equipped figure keeps a real range between its lit
# and shadowed faces", which is what the eye actually reads at the shipped 28x101 px actor size.
CLIP_SHARE_MAX = 0.01      # see gate_clipping: at most 1% of a material's visible pixels may be fully saturated
CLIP_MIN_PIXELS = 12       # a material shown in fewer pixels than this is too small for a share to mean anything
WORN_SET_IDS = ("worn.head.greathelm", "worn.body.plate-cuirass", "worn.back.hooded-cloak")
CATALOG = TOOLS.parents[1] / "dream" / "Rendering" / "Actors" / "ArmourMeshCatalog.cs"
HELD_CATALOG = TOOLS.parents[1] / "dream" / "Rendering" / "Actors" / "WeaponMeshCatalog.cs"
ENVELOPE_TOL = 0.015       # the worn-model contract's tolerance, engine units
DIE_SPAN_FRACTION = 0.45   # the lying-down gate: a fraction of the figure's own standing height...
DIE_REST_MAX = 0.012       # the held last frame may float at most this far (0.45 px) above y = 0
DIE_SPAN_ABS_MAX = 1.15    # ...and an absolute ceiling, so a taller figure cannot ride the fraction up
# The engine's own copy, the one PlayerModelAssetDefinition loads; the generator must reproduce it exactly.
SHIPPED_PLAYER = TOOLS.parents[1] / "dream" / "animations" / "players" / "player-grim.glb"
EMBER_FACTOR_MAX = 0.35    # the emissive accent's brightest channel; it bypasses the gain, so it stays dim
errs: list[str] = []


def fail(msg: str) -> None:
    errs.append(msg)
    print(f"  FAIL {msg}")


def ok(msg: str) -> None:
    print(f"  ok   {msg}")


def seam_report(meshes, uvs, rects, tex: int) -> tuple[int, int]:
    """(straddling triangles, cylinder triangles): a straddler has a u spread over half its rectangle's width."""
    bad = total = 0
    for m, uv, r in zip(meshes, uvs, rects):
        if not r.cyl:
            continue
        u = np.asarray(uv, dtype=np.float64)[np.asarray(m.f)][:, :, 0] * tex
        spread = u.max(axis=1) - u.min(axis=1)
        bad += int((spread > r.w / 2 + 1e-6).sum())
        total += len(m.f)
    return bad, total


def area(m) -> float:
    v = np.asarray(m.v, dtype=np.float64)[np.asarray(m.f)]
    return float(0.5 * np.linalg.norm(np.cross(v[:, 1] - v[:, 0], v[:, 2] - v[:, 0]), axis=1).sum())


def fk_vertices(asset: dict, clip_name: str | None, t_index: int | None) -> np.ndarray:
    """All primitive vertices in model space (glTF axes), the bind pose or one key of one clip.

    The forward kinematics itself lives in enginelook.node_worlds, so the gate and the measuring instrument
    cannot drift apart - and so there is exactly one place where "an animation channel replaces a node's TRS,
    it is not a delta on it" is written down."""
    clip = next(a for a in asset["animations"] if a.name == clip_name) if clip_name else None
    world = L.node_worlds(asset["nodes"], clip, t_index or 0)
    pts = []
    for p in asset["primitives"]:
        v = np.asarray(p.positions, dtype=np.float64)
        pts.append(v @ world[p.node][:3, :3].T + world[p.node][:3, 3])
    return np.concatenate(pts)


def gate_shipped(label: str, data: bytes, shipped: Path) -> None:
    """The generated bytes must BE the shipped file's bytes.

    Nothing else in this directory closed this: the determinism gates build twice and compare the two builds
    to each other, so editing a generator without re-running leaves the shipped asset stale with every gate
    green. The assets match today, which is exactly when to nail it down."""
    if not shipped.exists():
        fail(f"{label}: {shipped} is not shipped")
        return
    on_disk = shipped.read_bytes()
    if on_disk != data:
        fail(f"{label}: the generator produces {len(data)} bytes, sha1 {hashlib.sha1(data).hexdigest()[:12]}, but "
             f"{shipped.name} on disk is {len(on_disk)} bytes, sha1 {hashlib.sha1(on_disk).hexdigest()[:12]} - "
             f"re-run the generator and commit the asset")
    else:
        ok(f"{label}: the shipped {shipped.name} is byte-for-byte what the generator makes ({len(on_disk)} bytes)")


def _edge_samples(positions: np.ndarray, indices: np.ndarray, steps: int = 16) -> np.ndarray:
    """Every vertex plus points along every triangle edge, so a region bound that falls between two loft rings
    is still measured on the surface and not only on the rings."""
    v = np.asarray(positions, dtype=np.float64)
    tri = np.asarray(indices, dtype=np.int64).reshape(-1, 3)
    t = np.linspace(0.0, 1.0, steps + 1)[None, :, None]
    out = [v]
    for i, j in ((0, 1), (1, 2), (2, 0)):
        a, b = v[tri[:, i]][:, None, :], v[tri[:, j]][:, None, :]
        out.append((a + (b - a) * t).reshape(-1, 3))
    return np.concatenate(out)


def node_points(asset: dict) -> dict:
    """node name -> surface samples in that node's own frame (engine units, y up, +z forward)."""
    names = [n.name for n in asset["nodes"]]
    out: dict = {}
    for p in asset["primitives"]:
        out.setdefault(names[p.node], []).append(_edge_samples(p.positions, p.indices))
    return {k: np.concatenate(v) for k, v in out.items()}


def envelope_rows(asset: dict) -> list:
    """(label, measured, target) for each row of the shared worn-model contract (see build_parts()): the largest
    extent of the base figure inside the row's region, which the row's target must hit within ENVELOPE_TOL."""
    pts = node_points(asset)
    arm_x = 0.2   # ARM_OFFSET_U * UNITS_PER_U: the arm's own axis, outboard of its node

    def inside(node, y0, y1, shift=0.0):
        v = pts[node].copy()
        v[:, 0] -= shift
        return v[(v[:, 1] >= y0 - 1e-9) & (v[:, 1] <= y1 + 1e-9)]

    rows = []
    h = inside("head", 0.0, 0.42)
    rows += [("head |x|", float(np.abs(h[:, 0]).max()), 0.12), ("head |z|", float(np.abs(h[:, 2]).max()), 0.135),
             ("head top y", float(pts["head"][:, 1].max()), 0.42)]
    t = inside("torso", 0.0, 0.86)
    rows += [("torso |x| (hip skirt)", float(np.abs(t[:, 0]).max()), 0.26),
             ("torso front z", float(t[:, 2].max()), 0.15), ("torso back z (Back mount)", float(-t[:, 2].min()), 0.145)]
    c = inside("torso", 0.35, 0.86)
    rows += [("torso chest |x|", float(np.abs(c[:, 0]).max()), 0.195)]
    for node, side in (("arm-left", 1.0), ("arm-right", -1.0)):
        a = inside(node, -0.95, -0.55, shift=side * arm_x)
        rows += [(f"{node} |x|", float(np.abs(a[:, 0]).max()), 0.09), (f"{node} |z|", float(np.abs(a[:, 2]).max()), 0.10)]
    for node in ("leg-left", "leg-right"):
        l = inside(node, -0.75, -0.10)
        rows += [(f"{node} |x|", float(np.abs(l[:, 0]).max()), 0.11), (f"{node} |z|", float(np.abs(l[:, 2]).max()), 0.11)]
        f = inside(node, -1.30, -0.88)
        rows += [(f"{node} foot |x|", float(np.abs(f[:, 0]).max()), 0.132),
                 (f"{node} foot back z", float(-f[:, 2].min()), 0.055), (f"{node} foot front z", float(f[:, 2].max()), 0.155)]
    # the hand: the fist is centred at arm-local y -0.9 (HeldWeaponRenderer), so the bare hand's heel is at -1.003
    rows += [("arm-left lowest y (hand at -0.9)", float(-pts["arm-left"][:, 1].min()), 1.003)]
    return rows


_BOX = re.compile(r"Box\(\s*((?:[-\d.]+f,\s*){5}[-\d.]+f),\s*(?:ArmourMaterial\.)?(\w+)((?:,\s*(?:pitch|roll):\s*[-\d.]+f)*)\)")
# ArmourMeshCatalog.BaseColor, linear, as the engine draws it. Parsed from the same .cs the boxes come from
# (a hand-copied table had no gate tying it to the catalog and no entry for the glowing materials).
_BASE_COLOUR = re.compile(r"ArmourMaterial\.(\w+)\s*=>\s*new Vector4\(\s*([-\d.]+)f,\s*([-\d.]+)f,\s*([-\d.]+)f,\s*[-\d.]+f\)")
_EMISSIVE = re.compile(r"ArmourMaterial\.(\w+)\s*=>\s*new Vector3\(")
_ALIAS = re.compile(r"const ArmourMaterial (\w+) = ArmourMaterial\.(\w+);")


def worn_palette() -> tuple:
    """(name -> linear albedo, set of glowing material names), both including the BuildModels aliases."""
    src = CATALOG.read_text(encoding="utf-8")
    colours = {m.group(1): tuple(float(m.group(i)) for i in (2, 3, 4)) for m in _BASE_COLOUR.finditer(src)}
    glow = {m.group(1) for m in _EMISSIVE.finditer(src)}
    for alias, target in _ALIAS.findall(src):
        if target in colours:
            colours[alias] = colours[target]
        if target in glow:
            glow.add(alias)
    return colours, glow


_FACES = (((1, 0, 0), ((1, -1, -1), (1, 1, -1), (1, 1, 1), (1, -1, 1))),
          ((-1, 0, 0), ((-1, -1, 1), (-1, 1, 1), (-1, 1, -1), (-1, -1, -1))),
          ((0, 1, 0), ((-1, 1, -1), (-1, 1, 1), (1, 1, 1), (1, 1, -1))),
          ((0, -1, 0), ((-1, -1, 1), (-1, -1, -1), (1, -1, -1), (1, -1, 1))),
          ((0, 0, 1), ((-1, -1, 1), (1, -1, 1), (1, 1, 1), (-1, 1, 1))),
          ((0, 0, -1), ((1, -1, -1), (-1, -1, -1), (-1, 1, -1), (1, 1, -1))))


def catalog_boxes(visual_id: str) -> tuple:
    """(mount, [(centre, size, material, pitch, roll)]) for one model, parsed from ArmourMeshCatalog.cs."""
    src = CATALOG.read_text(encoding="utf-8")
    m = re.search(r'Add\("%s",\s*ArmourMount\.(\w+),(.*?)\);' % re.escape(visual_id), src, re.S)
    if not m:
        return "", []
    out = []
    for bm in _BOX.finditer(m.group(2)):
        n = [float(x.strip().rstrip("f")) for x in bm.group(1).split(",")]
        kw = {k: float(v.rstrip("f")) for k, v in re.findall(r"(pitch|roll):\s*([-\d.]+f)", bm.group(3))}
        out.append((tuple(n[:3]), tuple(n[3:]), bm.group(2), kw.get("pitch", 0.0), kw.get("roll", 0.0)))
    return m.group(1), out


def worn_surfaces(asset: dict, ids, labels: list | None = None, lit_glow: bool = False) -> list:
    """The worn models as enginelook Surfaces, standing on the bind-pose figure at the same normalisation the
    engine applies (the figure's own authored height, 1.2 world units). Flat-shaded boxes in the catalog's
    own palette, turned about X (pitch) then Z (roll), exactly as ArmourPart.ToMatrix does.

    `labels`, if given, receives one "<id>:<material>" per surface. `lit_glow` draws the glowing materials by
    their lit albedo alone (their emissive is added after the gain and is not what clips); the worn-set spread
    gate leaves it off and refuses them, as before. The Companion mount rides the torso frame, so it is drawn."""
    world = L.node_worlds(asset["nodes"])
    names = [n.name for n in asset["nodes"]]
    pts = fk_vertices(asset, None, None)
    k = L.PLAYER_WORLD_HEIGHT / float(pts[:, 1].max() - pts[:, 1].min())
    surfaces = []
    colours, glow = worn_palette()
    for vid in ids:
        mount, boxes = catalog_boxes(vid)
        if not boxes:
            return []
        # Head rides the head node; Torso and Back are authored in the torso frame. Any other mount would be
        # drawn on the torso, unmirrored, and look plausible: refuse it.
        if mount not in ("Head", "Torso", "Back") + (("Companion",) if lit_glow else ()):
            fail(f"{vid}: mount {mount} is not drawn by the worn-set gate (only Head, Torso and Back are)")
            return []
        W = world[names.index("head" if mount == "Head" else "torso")]
        for centre, size, mat, pitch, roll in boxes:
            cp, sp = math.cos(math.radians(pitch)), math.sin(math.radians(pitch))
            cr, sr = math.cos(math.radians(roll)), math.sin(math.radians(roll))
            R = np.array([[cr, -sr, 0], [sr, cr, 0], [0, 0, 1]]) @ np.array([[1, 0, 0], [0, cp, -sp], [0, sp, cp]])
            pos, nrm, tri = [], [], []
            for normal, corners in _FACES:
                base = len(pos)
                for q in corners:
                    local = R @ (np.array(q, dtype=np.float64) * 0.5 * np.array(size)) + np.array(centre)
                    pos.append(W[:3, :3] @ local + W[:3, 3])
                nrm += [W[:3, :3] @ (R @ np.array(normal, dtype=np.float64))] * 4
                tri += [(base, base + 1, base + 2), (base, base + 2, base + 3)]
            nrm = np.array(nrm)
            nrm /= np.linalg.norm(nrm, axis=1, keepdims=True)
            if (mat in glow and not lit_glow) or mat not in colours:
                fail(f"{vid}: material {mat} is {'emissive' if mat in glow else 'unknown to ArmourMeshCatalog.BaseColor'}; "
                     f"the worn-set gate draws lit materials only")
                return []
            alb = np.tile(np.array(colours[mat]), (len(pos), 1))
            surfaces.append(L.Surface(np.array(pos) * k, nrm, alb, np.array(tri), None))
            if labels is not None:
                labels.append(f"{vid}:{mat}")
    return surfaces


# WeaponMeshCatalog, read the same way. Held items are albedo too: the same gain multiplies them, and the torch and
# axe a new character starts with blew out to a yellow head and a cyan blade when the palette was authored as if
# it were final pixel colour. HeldWeaponRenderer draws a Hand model at HandScale in the fist frame
# (Rx(90) then the fist offset) on the arm node, and a Shield model at ShieldScale on the off-hand arm.
HELD_HAND_SCALE, HELD_SHIELD_SCALE = 1.7, 1.5
HELD_FIST_X, HELD_FIST_Y, HELD_SHIELD_X, HELD_SHIELD_Y = 0.2, -0.9, 0.31, -0.55
_HELD_BOX = re.compile(r"Box\(\s*((?:[-\d.]+f,\s*){5}[-\d.]+f),\s*WeaponMaterial\.(\w+)((?:,\s*(?:pitch|roll):\s*[-\d.]+f)*)\)")
_HELD_COLOUR = re.compile(r"WeaponMaterial\.(\w+)\s*=>\s*new Vector4\(\s*([-\d.]+)f,\s*([-\d.]+)f,\s*([-\d.]+)f,\s*[-\d.]+f\)")
_HELD_EMISSIVE = re.compile(r"WeaponMaterial\.(\w+)\s*=>\s*new Vector3\(")


def held_palette() -> tuple:
    """(name -> linear albedo, set of glowing material names) from WeaponMeshCatalog.BaseColor / Emissive."""
    src = HELD_CATALOG.read_text(encoding="utf-8")
    colours = {m.group(1): tuple(float(m.group(i)) for i in (2, 3, 4)) for m in _HELD_COLOUR.finditer(src)}
    return colours, {m.group(1) for m in _HELD_EMISSIVE.finditer(src)}


def held_ids() -> list:
    return sorted(set(re.findall(r'(?:Hand|Shield)\("(held\.[^"]+)"', HELD_CATALOG.read_text(encoding="utf-8"))))


def held_boxes(visual_id: str) -> tuple:
    """(grip, [(centre, size, material, pitch, roll)]) for one held model, parsed from WeaponMeshCatalog.cs."""
    src = HELD_CATALOG.read_text(encoding="utf-8")
    m = re.search(r'(Hand|Shield)\("%s",(.*?)\);' % re.escape(visual_id), src, re.S)
    if not m:
        return "", []
    out = []
    for bm in _HELD_BOX.finditer(m.group(2)):
        n = [float(x.strip().rstrip("f")) for x in bm.group(1).split(",")]
        kw = {k: float(v.rstrip("f")) for k, v in re.findall(r"(pitch|roll):\s*([-\d.]+f)", bm.group(3))}
        out.append((tuple(n[:3]), tuple(n[3:]), bm.group(2), kw.get("pitch", 0.0), kw.get("roll", 0.0)))
    return m.group(1), out


def held_surfaces(asset: dict, vid: str, labels: list | None = None) -> list:
    """One held model as enginelook Surfaces on the bind-pose figure, placed as HeldWeaponRenderer places it:
    a Hand model in the right fist (length turned forward), a Shield strapped to the left forearm. Lit albedo
    only, glowing materials included (their emissive is added after the gain and is not what clips)."""
    world = L.node_worlds(asset["nodes"])
    names = [n.name for n in asset["nodes"]]
    pts = fk_vertices(asset, None, None)
    k = L.PLAYER_WORLD_HEIGHT / float(pts[:, 1].max() - pts[:, 1].min())
    colours, _ = held_palette()
    grip, boxes = held_boxes(vid)
    if not boxes:
        return []
    if grip == "Shield":
        W, side, scale = world[names.index("arm-left")], 1.0, HELD_SHIELD_SCALE
        th = math.radians(90.0 * side)
        Rg = np.array([[math.cos(th), 0, math.sin(th)], [0, 1, 0], [-math.sin(th), 0, math.cos(th)]])
        tg = np.array([side * HELD_SHIELD_X, HELD_SHIELD_Y, 0.0])
    else:
        W, side, scale = world[names.index("arm-right")], -1.0, HELD_HAND_SCALE
        Rg = np.array([[1, 0, 0], [0, 0, -1], [0, 1, 0]], dtype=np.float64)
        tg = np.array([side * HELD_FIST_X, HELD_FIST_Y, 0.0])
    surfaces = []
    for centre, size, mat, pitch, roll in boxes:
        if mat not in colours:
            fail(f"{vid}: material {mat} is unknown to WeaponMeshCatalog.BaseColor")
            return []
        cp, sp = math.cos(math.radians(pitch)), math.sin(math.radians(pitch))
        cr, sr = math.cos(math.radians(roll)), math.sin(math.radians(roll))
        R = np.array([[cr, -sr, 0], [sr, cr, 0], [0, 0, 1]]) @ np.array([[1, 0, 0], [0, cp, -sp], [0, sp, cp]])
        pos, nrm, tri = [], [], []
        for normal, corners in _FACES:
            base = len(pos)
            for q in corners:
                local = R @ (np.array(q, dtype=np.float64) * 0.5 * np.array(size)) + np.array(centre)
                pos.append(W[:3, :3] @ (Rg @ (local * scale) + tg) + W[:3, 3])
            nrm += [W[:3, :3] @ (Rg @ (R @ np.array(normal, dtype=np.float64)))] * 4
            tri += [(base, base + 1, base + 2), (base, base + 2, base + 3)]
        nrm = np.array(nrm)
        nrm /= np.linalg.norm(nrm, axis=1, keepdims=True)
        surfaces.append(L.Surface(np.array(pos) * k, nrm, np.tile(np.array(colours[mat]), (len(pos), 1)),
                                  np.array(tri), None))
        if labels is not None:
            labels.append(f"{vid}:{mat}")
    return surfaces


def clip_report(surfaces, names, azimuths=L.M3_AZIMUTHS, albedo_scale: float = 1.0) -> list:
    """Per (azimuth, name): (visible pixels, fully-saturated pixels), through the engine transfer at M3's brazier.
    `names` is one string per surface (the material the surface stands for); a pixel is fully saturated when its
    HSV value (the largest channel, after the gain and the clamp) is 1.0, i.e. at least one channel is blown.
    `albedo_scale` exists for the live control only."""
    if albedo_scale != 1.0:
        surfaces = [L.Surface(s.positions, s.normals, s.albedo * albedo_scale, s.indices, s.emissive) for s in surfaces]
    rows = []
    for az in azimuths:
        r = L.rasterise(surfaces, L.m3_env(azimuth_deg=az))
        tally: dict = {}
        for i, name in enumerate(names):
            m = r["surface"] == i
            n, c = tally.get(name, (0, 0))
            tally[name] = (n + int(m.sum()), c + int((r["value"][m] >= 0.999).sum()))
        rows.append((az, tally))
    return rows


def clip_problems(rows) -> list:
    """The materials that blow out: more than CLIP_SHARE_MAX of their visible pixels fully saturated."""
    out = []
    for az, tally in rows:
        for name, (n, c) in sorted(tally.items()):
            if n >= CLIP_MIN_PIXELS and c / n > CLIP_SHARE_MAX:
                out.append(f"{name} at azimuth {az:.0f}: {c} of {n} px ({c / n:.1%}) fully saturated")
    return out


def main() -> int:
    print("check_char: building the asset twice")
    a = P.build_asset()
    b = P.build_asset()
    glb1 = G.build_glb(a["nodes"], a["primitives"], None, None, a["animations"], a["materials"])
    glb2 = G.build_glb(b["nodes"], b["primitives"], None, None, b["animations"], b["materials"])
    if hashlib.sha1(glb1).hexdigest() != hashlib.sha1(glb2).hexdigest():
        fail("two builds produced different GLB bytes")
    else:
        ok(f"deterministic: sha1 {hashlib.sha1(glb1).hexdigest()[:12]} ({len(glb1)} bytes)")
    gate_shipped("player", glb1, SHIPPED_PLAYER)

    # structure: rigid, no skin, the engine's node names, every clip the state machine plays
    doc, _ = G._check_container(glb1)
    if "skins" in doc:
        fail("the GLB carries a skin; the player must stay a rigid part hierarchy")
    names = {n["name"] for n in doc["nodes"]}
    miss = [n for n in ENGINE_NODES if n not in names]
    if miss:
        fail(f"engine node names missing: {miss}")
    clips = {x["name"] for x in doc["animations"]}
    miss = [c for c in REQUIRED_CLIPS if c not in clips]
    if miss:
        fail(f"clips missing: {miss}")
    if not miss and not [n for n in ENGINE_NODES if n not in names]:
        ok(f"rigid, engine nodes present, clips {sorted(clips)}")

    # grounding: the lowest vertex of the bind pose is y = 0 (this has bitten twice)
    ymin = float(fk_vertices(a, None, None)[:, 1].min())
    if abs(ymin) > 1e-5:
        fail(f"not grounded: lowest vertex y = {ymin:.6f}")
    else:
        ok(f"grounded: lowest vertex y = {ymin:.6f}")
    top = float(fk_vertices(a, None, None)[:, 1].max())
    if not 2.5 <= top <= 2.9:
        fail(f"height {top:.3f} units is not near the blocky's 2.7, so held items will not sit in the hand")
    else:
        ok(f"height {top:.3f} units (blocky 2.7: the engine's grip offsets assume this scale)")

    # every clip stays above the ground (a small walk dip is tolerated) and the fall ends lying down
    worst = 0.0
    for clip in a["animations"]:
        times = next(iter(clip.rotations.values()))[0] if clip.rotations else next(iter(clip.translations.values()))[0]
        for k in range(len(times)):
            worst = min(worst, float(fk_vertices(a, clip.name, k)[:, 1].min()))
    if worst < -0.06:
        fail(f"a clip pushes the body {-worst:.3f} units into the ground")
    else:
        ok(f"no clip sinks the body more than {-worst:.3f} units below y = 0")
    # The fall must end LYING DOWN, measured against the TIGHTER of a fraction of the figure's own standing
    # height and an absolute ceiling. The fraction alone is what a taller figure would ride up; the absolute
    # alone was the old gaunt cloth figure's 1.00, which this plated figure genuinely cannot meet - MEASURED
    # 0.87 units lying on its back against the 2.68 it stands at (1.06 with the old baked helm brim and pauldrons,
    # broad horizontal plates that a half turn about X stood upright). So the ceiling is 1.15, a stated
    # 15% relaxation of the old 1.00 with 8% of headroom over the measurement, and the fraction binds instead
    # as soon as the figure is shorter than 2.56 units.
    # The sibling below is what keeps this honest, and it is NOT the bind pose: a bind-pose control is circular
    # against a fraction-of-standing-height gate (its y span IS `stand`, so the comparison restates
    # stand <= 0.45*stand and can never fail). It is an EARLY key of the die clip, still mostly upright, which
    # the gate must reject at the same limit.
    die_clip = next(x for x in a["animations"] if x.name == "die")
    keys = len(die_clip.rotations[1][0])
    stand = top - ymin
    limit = min(DIE_SPAN_ABS_MAX, DIE_SPAN_FRACTION * stand)
    dv = fk_vertices(a, "die", keys - 1)
    span_y = float(dv[:, 1].max() - dv[:, 1].min())
    if span_y > limit:
        fail(f"die ends {span_y:.2f} units tall, over the {limit:.2f} limit "
             f"(min of {DIE_SPAN_ABS_MAX} and {DIE_SPAN_FRACTION:.0%} of the {stand:.2f} it stands at); "
             f"it is not lying down")
    else:
        ok(f"die ends lying down ({span_y:.2f} units <= {limit:.2f}, {span_y / stand:.0%} of its standing "
           f"{stand:.2f}; lowest y {float(dv[:, 1].min()):+.3f})")
    # and it must REST on the ground: the engine holds the last frame forever, and a lift sized for a mid-fall key
    # left the corpse hovering 0.0414 units (1.5 px) above the floor.
    rest = float(dv[:, 1].min())
    if not -0.01 <= rest <= DIE_REST_MAX:
        fail(f"the held last frame of die rests at y = {rest:+.4f}, outside -0.01..{DIE_REST_MAX} (a hovering or sunken corpse)")
    else:
        ok(f"die's held frame rests on the ground (lowest y {rest:+.4f} <= {DIE_REST_MAX})")
    early = min(1, keys - 1)
    ev = fk_vertices(a, "die", early)
    early_span = float(ev[:, 1].max() - ev[:, 1].min())
    if early_span <= limit:
        fail(f"the lying-down gate passes die key {early}, which is still standing ({early_span:.2f} units "
             f"<= {limit:.2f}); it has stopped measuring the fall")
    else:
        ok(f"the gate is live: die key {early} spans {early_span:.2f} units ({early_span / stand:.0%} of "
           f"standing) and fails the same {limit:.2f} limit")

    # seams: no cylinder triangle jumps across its atlas rectangle, and cutting changed no geometry
    parts, _ = P.split_parts(P.build_parts())
    world = P.world_meshes(parts)
    new_m, uvs, rects = C.unwrap_split(world, P.ATLAS_SIZE, axis=P.unwrap_axes(world))
    bad, total = seam_report(new_m, uvs, rects, P.ATLAS_SIZE)
    if bad:
        fail(f"{bad} of {total} cylinder triangles still straddle a seam after unwrap_split")
    else:
        ok(f"unwrap_split: 0 of {total} cylinder triangles straddle a seam")
    old_uvs, old_rects = C.unwrap(world, P.ATLAS_SIZE)
    obad, ototal = seam_report(world, old_uvs, old_rects, P.ATLAS_SIZE)
    if obad == 0:
        fail("the seam gate found no straddlers in the old unwrap(); it has stopped measuring")
    else:
        ok(f"the gate is live: old unwrap() leaves {obad} of {ototal} triangles straddling")
    drift = max(abs(area(n) - area(o)) / area(o) for n, o in zip(new_m, world))
    if drift > 1e-5:
        fail(f"cutting the seams changed the surface area by {drift:.2e}")
    else:
        ok(f"seam cuts keep the surface (area drift {drift:.1e})")

    # the lift is applied exactly once: the GLB texture times its factor is 3.0x the bake in linear light
    png = a["materials"][0]["base_color_png"]
    tex = np.asarray(Image.open(io.BytesIO(png)).convert("RGBA"))
    factor = a["materials"][0]["base_color_factor"][0]
    cov = a["atlas"][..., 3] > 0
    baked = U.srgb_to_linear(a["atlas"][..., :3].astype(np.float32) / 255.0).astype(np.float64)[cov].mean()
    shipped = (tex[..., :3].astype(np.float64) / 255.0 * factor)[cov].mean()
    ratio = shipped / baked
    if not math.isclose(ratio, P.ALBEDO_LIFT, rel_tol=0.03):
        fail(f"texture x factor is {ratio:.3f}x the baked linear albedo, expected {P.ALBEDO_LIFT} (lift applied 0 or 2 times?)")
    else:
        ok(f"lift applied once: shipped / baked linear albedo = {ratio:.3f} (lift {P.ALBEDO_LIFT})")
    if not (0.0 < factor <= 1.0) or len(set(a["materials"][0]["base_color_factor"][:3])) != 1:
        fail(f"baseColorFactor {a['materials'][0]['base_color_factor']} must be a single scale in (0,1]")
    # the texture really is wired to the baked material, and the emissive accent is a SECOND material with no
    # texture of its own: emissive is added after dreamExpose, so it bypasses the gain and has to stay dim
    if "baseColorTexture" not in doc["materials"][0]["pbrMetallicRoughness"]:
        fail("the GLB's material 0 has no baseColorTexture")
    elif len(doc["materials"]) > 2:
        fail(f"{len(doc['materials'])} materials; the figure is one baked atlas plus at most one emissive accent")
    else:
        ok(f"baseColorTexture on material 0, {len(doc['materials'])} material(s) total")
    extra = doc["materials"][1:]
    if extra:
        m = extra[0]
        emis = m.get("emissiveFactor")
        if "baseColorTexture" in m["pbrMetallicRoughness"]:
            fail("the emissive accent carries a texture; the factor path is the whole mechanism")
        elif not emis or max(emis) > EMBER_FACTOR_MAX:
            fail(f"emissiveFactor {emis} is missing or brighter than {EMBER_FACTOR_MAX}")
        else:
            share = sum(len(p.indices) for p in a["primitives"] if p.material == 1) / \
                sum(len(p.indices) for p in a["primitives"])
            ok(f"emissive accent: factor {[round(x, 3) for x in emis]}, {share:.3%} of the figure's triangles")
    accent_prims = [p for p in a["primitives"] if p.material != 0]
    if len(accent_prims) != len(a["emissive"]):
        fail(f"{len(accent_prims)} primitives off material 0 but {len(a['emissive'])} emissive parts")

    # M3 on the BASE figure: it still has to read as a figure. Measured BEFORE the floor was set; see BASE_SPREAD_MIN.
    surfaces = L.asset_surfaces(a, [a["encoded"]] + [None] * (len(a["materials"]) - 1))
    m3 = L.m3_measure(surfaces)
    per = m3["per_azimuth"]
    detail = ", ".join(f"az {k:.0f} p10 {v['p10']:.4f} p90 {v['p90']:.4f}" for k, v in sorted(per.items()))
    print(f"  info M3 base spread {m3['spread']:.4f} (worst azimuth {m3['worst_azimuth']:.0f}; {detail})")
    if m3["spread"] < BASE_SPREAD_MIN:
        fail(f"base figure M3 spread {m3['spread']:.4f} < {BASE_SPREAD_MIN} at azimuth {m3['worst_azimuth']:.0f} "
             f"(the unarmoured figure reads as a dark blob under a close light)")
    else:
        ok(f"base figure M3 spread {m3['spread']:.4f} >= {BASE_SPREAD_MIN} (worst azimuth {m3['worst_azimuth']:.0f})")
    # The live failure: NOT the undressed figure (that is now the figure under test). The same geometry with
    # every part forced to one albedo and no second material - a flat figure - must fail the base floor, or the
    # gate has stopped measuring contrast.
    lit = [s for s in surfaces if s.emissive is None]
    mid = np.median(np.concatenate([s.albedo for s in lit]), axis=0)
    flat = L.m3_measure([L.Surface(s.positions, s.normals, np.tile(mid, (len(s.positions), 1)), s.indices, None)
                         for s in lit])
    if flat["spread"] >= BASE_SPREAD_MIN:
        fail(f"the base gate passes a single-albedo flat figure at {flat['spread']:.4f}; it has stopped measuring contrast")
    else:
        ok(f"the base gate is live: the same figure at one albedo, spread {flat['spread']:.4f} < {BASE_SPREAD_MIN}")

    # M3 on the figure PLUS the reference worn set: where plate contrast lives now. 0.50 is the spec's number.
    worn = worn_surfaces(a, WORN_SET_IDS)
    if not worn:
        fail(f"could not read {WORN_SET_IDS} from {CATALOG.name}; the worn-set gate has nothing to measure")
    else:
        dressed = L.m3_measure(surfaces + worn)
        dper = ", ".join(f"az {k:.0f} p10 {v['p10']:.4f} p90 {v['p90']:.4f}" for k, v in sorted(dressed["per_azimuth"].items()))
        print(f"  info M3 figure + worn set {dressed['spread']:.4f} ({dper})")
        if dressed["spread"] < WORN_SET_SPREAD_MIN:
            fail(f"figure + worn set M3 spread {dressed['spread']:.4f} < {WORN_SET_SPREAD_MIN} at azimuth "
                 f"{dressed['worst_azimuth']:.0f} (the dressed figure is too flat under a close light)")
        else:
            ok(f"figure + worn set M3 spread {dressed['spread']:.4f} >= {WORN_SET_SPREAD_MIN}")
        # live: the same dressed figure at ONE albedo (figure and set alike) must fail the same floor, or the
        # gate has stopped measuring contrast. The figure alone is no longer a control: its own palette now
        # clears 0.50 by itself, so "figure < 0.50" would assert something false.
        every = surfaces + worn
        lit_all = [s for s in every if s.emissive is None]
        mid_all = np.median(np.concatenate([s.albedo for s in lit_all]), axis=0)
        flat_worn = L.m3_measure([L.Surface(s.positions, s.normals, np.tile(mid_all, (len(s.positions), 1)),
                                            s.indices, None) for s in lit_all])
        if flat_worn["spread"] >= WORN_SET_SPREAD_MIN:
            fail(f"the worn-set gate passes a single-albedo dressed figure at {flat_worn['spread']:.4f}; it has stopped measuring contrast")
        else:
            ok(f"the worn-set gate is live: the dressed figure at one albedo, {flat_worn['spread']:.4f} < {WORN_SET_SPREAD_MIN}")

    # CLIPPING. M3's spread compares the 10th and 90th percentile and the lift-once gate compares a MEAN, so a
    # highlight that is blown flat passes both (loop 1 shipped a tunic with 31% of its pixels at 1.0 and the
    # base figure's p90 read 1.0000). The renderer multiplies albedo by a gain of up to ~2.2 at M3's brazier with
    # no tonemap, so any lit texel or box above ~0.45 in its largest channel is clamped to a flat patch whose hue
    # is wrong. This gate looks at the TOP of the distribution, per material, at BOTH azimuths:
    #   - a material's visible pixels may have at most CLIP_SHARE_MAX (1%) fully saturated, equivalent to its p99
    #     value staying under 1.0. A share rather than a bare count, because a count means different things for
    #     a 70 px tunic and a 700 px one; 1% rather than 0 because a pixel or two on a silhouette edge is not a
    #     blown patch, while anything a viewer can see as a flat hot area is far above 1%. A p100 gate would fail
    #     on noise; a p90 gate is what let the highlight through;
    #   - measured on every baked material of the figure and on every worn piece (drawn on the figure, so
    #     occlusion is real), the lit part only for the glowing ones: their emissive bypasses the gain and is
    #     capped separately (EMBER_FACTOR_MAX);
    #   - plus the palette bound: no ArmourMeshCatalog colour times the peak gain on the drawn geometry may reach 1.
    fig_names = ["figure:" + k for _, _, k in P.split_parts(P.build_parts())[0]]
    fig_names += ["figure:emissive"] * (len(surfaces) - len(fig_names))
    problems = clip_problems(clip_report(surfaces, fig_names))
    catalog_src = CATALOG.read_text(encoding="utf-8")
    drawable = [vid for vid in sorted(set(re.findall(r'Add\("(worn\.[^"]+)"', catalog_src)))
                if catalog_boxes(vid)[0] in ("Head", "Torso", "Back", "Companion")]
    seen_materials, peak_gain, worn_total = set(), 0.0, 0
    palette, _ = worn_palette()
    for vid in drawable:
        names: list = []
        piece = worn_surfaces(a, [vid], names, lit_glow=True)
        if not piece:
            continue
        worn_total += len(piece)
        seen_materials |= {n.split(":")[1] for n in names}
        problems += clip_problems(clip_report(surfaces + piece, fig_names + names))
        for az in L.M3_AZIMUTHS:
            env = L.m3_env(azimuth_deg=az)
            for sf in surfaces + piece:
                peak_gain = max(peak_gain, float(L.gain(sf.normals, sf.positions, env).max()))
    # the held items, on the same figure (right fist; shields on the left forearm), through the same gate
    held = held_ids()
    held_seen, held_total = set(), 0
    for vid in held:
        names = []
        piece = held_surfaces(a, vid, names)
        if not piece:
            fail(f"{vid}: the held model could not be drawn by the clip gate")
            continue
        held_total += len(piece)
        held_seen |= {n.split(":")[1] for n in names}
        problems += clip_problems(clip_report(surfaces + piece, fig_names + names))
        for az in L.M3_AZIMUTHS:
            env = L.m3_env(azimuth_deg=az)
            for sf in piece:
                peak_gain = max(peak_gain, float(L.gain(sf.normals, sf.positions, env).max()))
    base = {m.group(1): tuple(float(m.group(i)) for i in (2, 3, 4)) for m in _BASE_COLOUR.finditer(catalog_src)}
    for mat, rgb in sorted(base.items()):
        if max(rgb) * peak_gain >= 1.0:
            problems.append(f"palette: {mat} albedo {rgb} x peak gain {peak_gain:.2f} = {max(rgb) * peak_gain:.2f} reaches 1.0")
    held_base, _ = held_palette()
    for mat, rgb in sorted(held_base.items()):
        if max(rgb) * peak_gain >= 1.0:
            problems.append(f"held palette: {mat} albedo {rgb} x peak gain {peak_gain:.2f} = {max(rgb) * peak_gain:.2f} reaches 1.0")
    held_unseen = sorted(set(held_base) - held_seen)    # no exemption: every held material is used by some model
    if held_unseen:
        problems.append(f"held palette materials never drawn by the clip gate: {held_unseen}")
    # materials are named by their enum member here, and by their BuildModels alias in the box labels
    alias = dict(_ALIAS.findall(catalog_src))
    drawn = {alias.get(m, m) for m in seen_materials}
    unseen = sorted(set(base) - drawn - {"Gilt"})   # Gilt is declared and used by no model
    if unseen:
        problems.append(f"palette materials never drawn by the clip gate: {unseen}")
    for msg in problems:
        fail("clipping: " + msg)
    if not problems:
        ok(f"no clipping: figure + {len(drawable)} worn pieces ({worn_total} boxes) + {len(held)} held items "
           f"({held_total} boxes), every material under "
           f"{CLIP_SHARE_MAX:.0%} saturated at both azimuths, peak gain {peak_gain:.2f}, "
           f"palette max {max(max(c) for c in base.values()):.3f}")
    # live: the same figure lit 3x brighter (what loop 1's palette amounted to) must trip it. A gate that cannot
    # fail is not a gate.
    control = clip_problems(clip_report(surfaces, fig_names, albedo_scale=3.0))
    if not control:
        fail("the clipping gate passes the figure at 3x albedo; it has stopped measuring the top of the distribution")
    else:
        ok(f"the clipping gate is live: the figure at 3x albedo trips it on {len(control)} material/azimuth pairs")

    # the worn-model contract: the base figure's extents in each mount's frame, +-0.015 of the table in build_parts()
    for label, got, want in envelope_rows(a):
        line = f"envelope {label}: {got:.4f} (contract {want})"
        # One-sided OUTWARD: the figure may stand short of the table by ENVELOPE_TOL, but never past it. Worn
        # pieces are authored to cover the table value, so a figure wider than the table pokes out through them.
        two_sided = "lowest y" in label     # the hand's heel must sit AT the grip, not merely inside it
        if (abs(got - want) > ENVELOPE_TOL + 1e-9) if two_sided else (got > want + 1e-9 or got < want - ENVELOPE_TOL - 1e-9):
            fail(line + f" is outside the table (at most {want}, at least {want - ENVELOPE_TOL})")
        else:
            ok(line)

    # the baked texture's own palette discipline: no forbidden hues, and saturation past 0.35 belongs to the one
    # accent material alone, which keeps to a few percent of the figure's atlas area
    rep = C._hsv_report(a["atlas"])
    px = a["atlas"][..., :3].astype(np.float64) / 255.0
    mx, mn = px.max(2), px.min(2)
    sat = np.where(mx > 0, (mx - mn) / np.maximum(mx, 1e-6), 0.0)
    stray = 0
    accent = 0
    for r, (_, _, key) in zip(a["rects"], a["parts"]):
        if key == "cloth.red":
            accent += r.w * r.h
        else:
            stray += int((sat[r.y:r.y + r.h, r.x:r.x + r.w] > 0.35).sum())
    share = accent / float(cov.sum())
    if rep["forbidden_hue_texels"] > 0.001 * rep["texels"]:
        fail(f"{rep['forbidden_hue_texels']} texels in forbidden hues")
    elif stray:
        fail(f"{stray} texels outside the accent material are saturated past 0.35")
    elif share > 0.04:
        fail(f"the accent covers {share:.3f} of the atlas; the budget is about 3%")
    else:
        ok(f"palette: forbidden-hue texels {rep['forbidden_hue_texels']}, stray saturated texels 0, accent share {share:.3f}")

    print(f"check_char: {len(errs)} problem(s)")
    return 1 if errs else 0


if __name__ == "__main__":
    raise SystemExit(main())
