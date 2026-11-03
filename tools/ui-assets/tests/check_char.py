"""Gates for the grim player actor: the texture, the seams, the lift, the grounding and the clips.

    python tools/ui-assets/tests/check_char.py

Fails (exit 1) when any gate fails. Every gate that can silently stop measuring has a sibling that must fail:
the seam gate is run on the old unwrap() as well and must find its straddling triangles there.
"""
from __future__ import annotations

import hashlib
import io
import math
import sys
from pathlib import Path

import numpy as np
from PIL import Image

HERE = Path(__file__).resolve().parent
TOOLS = HERE.parent
if str(TOOLS) not in sys.path:
    sys.path.insert(0, str(TOOLS))

import charforge as C  # noqa: E402
import gen_char_player as P  # noqa: E402
import gltfwrite as G  # noqa: E402
import uiart as U  # noqa: E402

REQUIRED_CLIPS = ("idle", "walk", "sprint", "die", "attack-melee-right")
ENGINE_NODES = ("torso", "head", "arm-left", "arm-right", "leg-left", "leg-right")
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
    """All primitive vertices in model space (glTF axes), the bind pose or one key of one clip."""
    nodes = asset["nodes"]
    rot = {i: np.array([0.0, 0.0, 0.0, 1.0]) for i in range(len(nodes))}
    trs = {i: np.array(n.translation, dtype=np.float64) for i, n in enumerate(nodes)}
    if clip_name:
        clip = next(a for a in asset["animations"] if a.name == clip_name)
        for n, (_, q) in clip.rotations.items():
            rot[n] = np.asarray(q[t_index], dtype=np.float64)
        for n, (_, v) in clip.translations.items():
            trs[n] = trs[n] + np.asarray(v[t_index], dtype=np.float64)

    def mat(i):
        x, y, z, w = rot[i]
        R = np.array([[1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
                      [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
                      [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]])
        M = np.eye(4)
        M[:3, :3] = R
        M[:3, 3] = trs[i]
        return M

    world = {}
    for i, n in enumerate(nodes):
        world[i] = mat(i) if n.parent is None else world[n.parent] @ mat(i)
    pts = []
    for p in asset["primitives"]:
        v = np.asarray(p.positions, dtype=np.float64)
        pts.append(v @ world[p.node][:3, :3].T + world[p.node][:3, 3])
    return np.concatenate(pts)


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
    last = len(next(x for x in a["animations"] if x.name == "die").rotations[1][0]) - 1
    dv = fk_vertices(a, "die", last)
    span_y = float(dv[:, 1].max() - dv[:, 1].min())
    if span_y > 1.0:
        fail(f"die ends {span_y:.2f} units tall; the body should be lying down")
    else:
        ok(f"die ends lying down ({span_y:.2f} units tall, lowest y {float(dv[:, 1].min()):+.3f})")

    # seams: no cylinder triangle jumps across its atlas rectangle, and cutting changed no geometry
    parts = P.build_parts()
    world = P.world_meshes(parts)
    new_m, uvs, rects = C.unwrap_split(world, P.ATLAS_SIZE, axis=P.UNWRAP_AXIS)
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
    # the texture really is wired to every primitive
    if len(doc["materials"]) != 1 or "baseColorTexture" not in doc["materials"][0]["pbrMetallicRoughness"]:
        fail("the GLB material has no baseColorTexture")
    else:
        ok("baseColorTexture present on the single material")

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
