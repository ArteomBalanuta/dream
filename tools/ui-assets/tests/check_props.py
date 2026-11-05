"""Gates for the world props: tools/ui-assets/gen_props.py and the GLBs it ships in dream/animations/props/.

    python tools/ui-assets/tests/check_props.py [--fast] [--measure]

  --fast     skip the second build (determinism) and the comparison with the committed files
  --measure  print every metric for every prop and exit, to calibrate the constants below

Exits nonzero on failure. Every gate that can silently stop measuring has a synthetic negative that must fail it
(self_test), in the style of check_terrain.py and check_char.py. Do not loosen a constant to pass: fix the art, or
add an OVERRIDES entry that says why, with the measured value.

What is gated, per prop:
  * the GLB container: rigid (no skin), no animation, one node, every image embedded as PNG, no external URI, the
    baked material textured, the contact disc an alpha-MASK material;
  * determinism: two builds give identical bytes, and both equal the committed file;
  * no baked key: the baked albedo carries no directional light. Two measures, because the atlas is a packing of
    parts and not a tile: the quadrant luma spread and the low-frequency share of each part's rectangle (the
    check_terrain gates), and the azimuth leakage - the correlation between a texel's luma and the horizontal
    direction its surface faces, over every direction, which a baked key from any side would raise;
  * the grim palette: value ceilings, saturation ceilings, forbidden hues, chrome;
  * the atlas: gutter, rectangles inside the atlas and apart, every uv inside its own rectangle, no triangle
    straddling a cylinder seam after unwrap_split;
  * the scale and footprint: grounded, no larger than the C# footprint PropCatalog uses for placement;
  * budgets: triangles, vertices, bytes; emissive only on the brazier's coals.
"""
from __future__ import annotations

import hashlib
import io
import json
import math
import re
import sys
from concurrent.futures import ProcessPoolExecutor
from pathlib import Path

import numpy as np
from PIL import Image

HERE = Path(__file__).resolve().parent
TOOLS = HERE.parent
ROOT = TOOLS.parents[1]
sys.path.insert(0, str(TOOLS))
import charforge as C  # noqa: E402
import gen_props as GP  # noqa: E402
import gltfwrite as G  # noqa: E402
import isoforge as I  # noqa: E402
import uiart as U  # noqa: E402

SHIPPED = ROOT / "dream" / "animations" / "props"
CATALOG_CS = ROOT / "dream" / "Rendering" / "Props" / "PropCatalog.cs"
MANIFEST = TOOLS / "iso" / "iso_manifest.json"
NAMES = tuple(GP.PROPS)

GATES = {
    "rect_quad_max": 0.45,       # area-weighted mean over the part rectangles of (max - min quadrant mean luma) / mean
    "rect_lowfreq_max": 0.80,    # area-weighted mean share of rectangle luma energy at <= 2 cycles per rectangle
    "azimuth_max": 0.23,         # max over 16 azimuths of the area-weighted mean |corr(luma, side-facing direction)| per part
    "sat_p95_max": 0.35,         # HSV S on atlas texels with V >= .10
    "forbidden_hue_max": 0.001,  # share of texels in magenta/pink or green-cyan hues at S > .12 (charforge._hsv_report)
    "chrome_max": 0.002,         # share of texels over V .85
    "v_p99_max": 0.55,           # nothing is bright: bone and stone are the lightest materials
    "v_p99_min": 0.10,           # and nothing is a black hole
    "mean_v_max": 0.28,
    "tris_max": 70000,
    "verts_max": 60000,
    "bytes_max": 4_000_000,
    "ground_tol": 0.06,          # the lowest vertex of a prop may not float higher than this above the ground, in world units ...
    "buried_max": 0.30,          # ... nor sit deeper than this (roots and a pile's bed go under the surface a little)
    "footprint_low_y": 0.40,     # the footprint is measured on vertices up to this height: the ground a prop covers
    "seam_share_max": 0.015,     # triangles that still straddle a cylinder seam after unwrap_split, over cylinder triangles
    "footprint_slack": 1.25,     # half extents may exceed the placement footprint by this factor ...
    "footprint_pad": 0.15,       # ... plus this much (world units)
    "footprint_floor": 0.35,     # and must fill at least this share of it
    "ember_max": 0.35,           # emissive factor ceiling (the night look budget)
    "hf_min": 7.0 / 255.0,       # FLOOR on the mean |4-neighbour luma gradient| of the shipped texture over the used uv area (7.0 / 255)
    "albedo_clip_max": 0.42,     # CEILING on byte / 255 * baseColorFactor over the used texels: above it the lit pixel clips flat
    # The cap (groundkit.cap_peak) keeps the shipped albedo under that ceiling whatever the bake does, so albedo_clip_max alone could never
    # fire. These three gate the bake BEFORE the cap, so that an over-bright bake is a failure and not a silent darkening / flattening:
    "precap_albedo_max": 0.75,   # CEILING on the brightest effective albedo the bake produced: measured max .718 (tree_leaning_birch), rounded up
    "cap_scale_min": 0.55,       # FLOOR on the uniform darkening the cap applied (1 = it did nothing): measured min .59 (props/bramble)
    # moved_fraction (the share of texels the cap changed) is reported in atlas_detail_table.md but not gated: a uniform darkening moves every
    # texel, so today's assets sit anywhere from 0 to 100 % and no threshold could pass them and still mean something; cap_scale_min bounds it.
}
# An override is a claim that the art needs it: say why, with the measured value, and keep it as tight as the
# measurement allows.
OVERRIDES: dict[str, dict] = {
    # The sprite objects keep the materials they were drawn with. The bramble's crimson leaves and the tombstone's moss
    # are the two deliberate colours of the set (sat_cap .60 and .35 in their IsoMats); both stay a small share.
    "bramble": {"sat_p95_max": 0.50, "forbidden_hue_max": 0.26},   # the leaves, regraded to a dull wine: measured sat p95 .463, forbidden .218
    "tombstone": {"forbidden_hue_max": 0.25},   # moss: measured forbidden share .203
    "rust_barrel": {"sat_p95_max": 0.46, "forbidden_hue_max": 0.10},   # rust is the one accent a prop may saturate (isoforge.RUST_CAP); measured sat .386, forbidden .068 (cold blue-black iron)
    "rune_flask": {"sat_p95_max": 0.56},   # the wax seal and the cork are the saturated parts; measured .510
}
SKIRTED = {"cottage": 14.0, "inn": 14.0, "hovel": 14.0}   # a house's footing reaches this many u below the ground


def cfg(name: str) -> dict:
    c = dict(GATES)
    c.update(OVERRIDES.get(name, {}))
    return c


# metrics ---------------------------------------------------------------------------------------------


def luma(rgb: np.ndarray) -> np.ndarray:
    a = rgb[..., :3].astype(np.float32) / 255.0
    return 0.2126 * a[..., 0] + 0.7152 * a[..., 1] + 0.0722 * a[..., 2]


def quad_spread(y: np.ndarray) -> float:
    h, w = y.shape[0] // 2, y.shape[1] // 2
    q = [y[:h, :w].mean(), y[:h, w:].mean(), y[h:, :w].mean(), y[h:, w:].mean()]
    return float((max(q) - min(q)) / max(float(y.mean()), 1e-6))


def lowfreq_share(y: np.ndarray) -> float:
    f = np.abs(np.fft.fft2(y - y.mean())) ** 2
    ky = np.abs(np.fft.fftfreq(y.shape[0]) * y.shape[0])[:, None]
    kx = np.abs(np.fft.fftfreq(y.shape[1]) * y.shape[1])[None, :]
    low = np.hypot(kx, ky) <= 2.0
    return float(f[low].sum() / max(f.sum(), 1e-9))


def rect_metrics(atlas: np.ndarray, rects) -> tuple[float, float]:
    quads, lows, wts = [], [], []
    for r in rects:
        if r.w < 24 or r.h < 24:
            continue
        y = luma(atlas[r.y:r.y + r.h, r.x:r.x + r.w])
        quads.append(quad_spread(y))
        lows.append(lowfreq_share(y))
        wts.append(r.w * r.h)
    if not wts:
        return 0.0, 0.0
    w = np.array(wts, dtype=np.float64)
    return float(np.average(quads, weights=w)), float(np.average(lows, weights=w))


def texel_samples(a: dict):
    """Per part: (luma, nx, ny, nz) for every covered texel of its rectangle (bary-interpolated surface normal)."""
    tex = a["spec"].atlas
    out = []
    for m, uv, rect in zip(a["meshes"], a["uvs"], a["rects"]):
        uvpx = np.asarray(uv, dtype=np.float64) * tex
        ty, tx, tri, bary = C._raster(uvpx, m.f, rect)
        if len(ty) == 0:
            continue
        v = m.v.astype(np.float64)
        if m.n is not None:
            nv = m.n.astype(np.float64)
            N = np.einsum("nk,nkc->nc", bary, nv[m.f[tri]])
        else:
            fn = np.cross(v[m.f[:, 1]] - v[m.f[:, 0]], v[m.f[:, 2]] - v[m.f[:, 0]])
            N = fn[tri]
        N /= np.maximum(np.linalg.norm(N, axis=1, keepdims=True), 1e-9)
        y = luma(a["atlas"][ty, tx])
        out.append((y, N[:, 0], N[:, 1], N[:, 2]))
    return out


AZIMUTHS = [math.tau * k / 16 for k in range(16)]


def used_mask(a: dict) -> np.ndarray:
    """The used uv area: the interior of every part rectangle (the gutter and the unallocated atlas are not part of it). Not only the
    texels a triangle lands on: the rest of a rectangle is filled by the bake and is sampled by mip levels and filtering, and it is the
    area the 3.3 - 4.1 / 255 flora measurement in the brief was taken over (a triangle-only mask reads 6 - 9 on the same atlases)."""
    tex = a["spec"].atlas
    mask = np.zeros((tex, tex), dtype=bool)
    for r in a["rects"]:
        mask[r.y:r.y + r.h, r.x:r.x + r.w] = True
    return mask


def hf_gradient(enc: np.ndarray, mask: np.ndarray) -> float:
    """Mean absolute 4-neighbour luma gradient (|d/dx| and |d/dy| over every pair of used texels) of the SHIPPED texture bytes. The
    check_art.high_frequency laplacian reads about three times this; the floor is calibrated on the gradient (props/cottage 7.0/255)."""
    y = luma(enc)
    gx = np.abs(y[:, 1:] - y[:, :-1])[mask[:, 1:] & mask[:, :-1]]
    gy = np.abs(y[1:] - y[:-1])[mask[1:] & mask[:-1]]
    both = np.concatenate([gx, gy])
    return float(both.mean()) if len(both) else 0.0


def albedo_max(enc: np.ndarray, scale: float, mask: np.ndarray) -> float:
    """Largest EFFECTIVE albedo over the used texels: the texture byte times the material's baseColorFactor, not the byte alone."""
    return float(enc[..., :3][mask].max()) / 255.0 * float(scale) if mask.any() else 0.0


def detail_metrics(a: dict) -> dict:
    mask = used_mask(a)
    cap = a.get("cap") or {"precap_albedo_max": 0.0, "cap_scale": 1.0, "moved_fraction": 0.0}
    return {"hf": hf_gradient(a["encoded"], mask), "albedo_max": albedo_max(a["encoded"], a["scale"], mask), **cap}


def detail_errors(name: str, m: dict, c: dict) -> list[str]:
    errs = []
    if m["hf"] < c["hf_min"]:
        errs.append(f"{name}: texture gradient {m['hf']:.4f} < {c['hf_min']} (a near-flat atlas: no grain, striation or breakup)")
    if m["albedo_max"] > c["albedo_clip_max"]:
        errs.append(f"{name}: effective albedo {m['albedo_max']:.3f} > {c['albedo_clip_max']} (byte * baseColorFactor clips under the brazier)")
    if m["precap_albedo_max"] > c["precap_albedo_max"]:
        errs.append(f"{name}: the bake reaches effective albedo {m['precap_albedo_max']:.3f} before the cap, over {c['precap_albedo_max']}: "
                    "fix the art; the cap would only darken and flatten it")
    if m["cap_scale"] < c["cap_scale_min"]:
        errs.append(f"{name}: the cap had to darken the whole atlas to {m['cap_scale']:.2f} of the bake (floor {c['cap_scale_min']})")
    return errs


def azimuth_leak(samples) -> float:
    """Max over azimuths of the texel-weighted mean |corr(luma, N . d)| per part, over side-facing texels. A part that
    faces one way only (a door) or is uniform has nothing to correlate with and is skipped."""
    best = 0.0
    for phi in AZIMUTHS:
        c, s = math.cos(phi), math.sin(phi)
        num = den = 0.0
        for y, nx, ny, nz in samples:
            side = np.abs(nz) < 0.6
            if side.sum() < 200:
                continue
            ys, d = y[side].astype(np.float64), (nx[side] * c + ny[side] * s)
            if ys.std() < 1e-4 or d.std() < 0.2:
                continue
            r = float(np.corrcoef(ys, d)[0, 1])
            num += abs(r) * side.sum()
            den += side.sum()
        if den:
            best = max(best, num / den)
    return best


def with_key(samples, strength: float = 0.5):
    """The synthetic negative: the same texels lit by a baked key from the isometric key direction."""
    k = I.KEY_WORLD
    return [(y * (1 - strength + strength * np.clip(nx * k[0] + ny * k[1] + nz * k[2], 0, 1)), nx, ny, nz) for y, nx, ny, nz in samples]


def palette(atlas: np.ndarray, rects) -> dict:
    mask = np.zeros(atlas.shape[:2], dtype=bool)
    for r in rects:
        mask[r.y:r.y + r.h, r.x:r.x + r.w] = True
    px = atlas[mask][:, :3].astype(np.float64) / 255.0
    mx, mn = px.max(1), px.min(1)
    s = (mx - mn) / np.maximum(mx, 1e-6)
    # Hue and saturation only mean something above the 8-bit floor: at V < .12 one level of rounding swings S by .1.
    lit = mx >= 0.12
    d = np.maximum(mx - mn, 1e-9)
    r, g, b = px[:, 0], px[:, 1], px[:, 2]
    h = np.where(mx == r, ((g - b) / d) % 6, np.where(mx == g, (b - r) / d + 2, (r - g) / d + 4)) * 60
    bad = lit & (s > 0.12) & (((h > 270) & (h < 345)) | ((h > 85) & (h < 200)))   # magenta/pink, and green through cyan
    return {"sat_p95": float(np.percentile(s[lit], 95)) if lit.any() else 0.0,
            "forbidden_hue": float(bad.sum()) / max(int(lit.sum()), 1), "chrome": float((mx > 0.85).mean()),
            "v_p99": float(np.percentile(mx, 99)), "mean_v": float(mx.mean())}


def seam_report(meshes, uvs, rects, tex: int) -> tuple[int, int]:
    bad = total = 0
    for m, uv, r in zip(meshes, uvs, rects):
        if not r.cyl:
            continue
        u = np.asarray(uv, dtype=np.float64)[np.asarray(m.f)][:, :, 0] * tex
        spread = u.max(axis=1) - u.min(axis=1)
        bad += int((spread > r.w / 2 + 1e-6).sum())
        total += len(m.f)
    return bad, total


def atlas_errors(name: str, a: dict) -> list[str]:
    errs = []
    tex = a["spec"].atlas
    rects, enc = a["rects"], a["encoded"]
    g = C.GUTTER
    for i, r in enumerate(rects):
        if r.x - g < 0 or r.y - g < 0 or r.x + r.w + g > tex or r.y + r.h + g > tex:
            errs.append(f"{name}: rect {i} {tuple(r)} with its gutter leaves the {tex} px atlas")
    for i in range(len(rects)):
        for j in range(i + 1, len(rects)):
            ra, rb = rects[i], rects[j]
            if not (ra.x + ra.w + g <= rb.x - g or rb.x + rb.w + g <= ra.x - g or ra.y + ra.h + g <= rb.y - g or rb.y + rb.h + g <= ra.y - g):
                errs.append(f"{name}: padded rects {i} and {j} overlap")
                return errs
    # gutter: every texel of the padded ring is filled (alpha 255) and carries the colour of the edge it extends
    # (dilated), never black bleed: a mipmapped or filtered sample at the rectangle's edge must not darken.
    worst = 0.0
    for r in rects:
        pad = enc[r.y - g:r.y + r.h + g, r.x - g:r.x + r.w + g].astype(np.int32)
        if (pad[..., 3] != 255).any():
            errs.append(f"{name}: a gutter texel is not covered")
            break
        inner = enc[r.y:r.y + r.h, r.x:r.x + r.w, :3].astype(np.int32)
        lo, hi = inner.reshape(-1, 3).min(0), inner.reshape(-1, 3).max(0)
        ring = np.ones(pad.shape[:2], dtype=bool)
        ring[g:-g, g:-g] = False
        out = pad[ring][:, :3]
        over = np.maximum(np.maximum(lo - out, out - hi), 0).max()
        worst = max(worst, float(over))
    if worst > 24:
        errs.append(f"{name}: gutter colour strays {worst:.0f}/255 outside the rectangle's own range (not dilated from it)")
    # every uv stays inside its own rectangle: a vertex outside would sample the neighbour or the gutter's far side
    for i, (uv, r) in enumerate(zip(a["uvs"], rects)):
        px = np.asarray(uv, dtype=np.float64) * tex
        slack = 0.51
        if r.cyl:
            x_ok = (px[:, 0] >= r.x - slack) & (px[:, 0] <= r.x + r.w + slack)
        else:
            x_ok = (px[:, 0] >= r.x - slack) & (px[:, 0] <= r.x + r.w + slack)
        y_ok = (px[:, 1] >= r.y - slack) & (px[:, 1] <= r.y + r.h + slack)
        if not (x_ok & y_ok).all():
            errs.append(f"{name}: part {i} has {int((~(x_ok & y_ok)).sum())} uvs outside its rectangle")
            break
    bad, total = seam_report(a["meshes"], a["uvs"], rects, tex)
    # Not zero, unlike the player: the player's parts are lofts and unwrap on their own axis, while a prop's merged parts
    # take the PCA axis, where a few big flat triangles span more than half the way round. A budget, measured at <= 1.2%.
    if total and bad / total > cfg(name)["seam_share_max"]:
        errs.append(f"{name}: {bad} of {total} cylinder triangles ({bad / total:.2%}) straddle a seam after unwrap_split, over the budget")
    return errs


def footprints() -> dict[str, tuple[float, float, int]]:
    """The half extents PropCatalog.cs places with, by file stem, parsed from the C# source so the two cannot drift."""
    src = CATALOG_CS.read_text(encoding="utf-8")
    stems = re.search(r"FileStems =\s*\[(.*?)\];", src, re.S).group(1)
    names = re.findall(r'"([a-z_]+)"', stems)
    block = re.search(r"Footprints =\s*\[(.*?)\];", src, re.S).group(1)
    vals = re.findall(r"new\(([\d.]+)f,\s*([\d.]+)f,\s*(\d)\)", block)
    if len(names) != len(vals):
        raise SystemExit(f"PropCatalog.cs: {len(names)} stems but {len(vals)} footprints")
    return {n: (float(w), float(d), int(s)) for n, (w, d, s) in zip(names, vals)}


def glb_errors(name: str, data: bytes, a_meta: dict) -> list[str]:
    errs = []
    try:
        doc, boff = G._check_container(data)
    except (AssertionError, KeyError, ValueError, OSError, __import__("struct").error) as e:
        return [f"{name}: GLB container invalid ({e!r})"]
    if "skins" in doc:
        errs.append(f"{name}: the GLB carries a skin; props are rigid")
    if doc.get("animations"):
        errs.append(f"{name}: the GLB carries animations; props are static")
    if len(doc["nodes"]) != 1 or doc["nodes"][0].get("name") != name:
        errs.append(f"{name}: expected one node named {name}, got {[n.get('name') for n in doc['nodes']]}")
    for key in ("buffers", "images"):
        for item in doc.get(key, []):
            if "uri" in item:
                errs.append(f"{name}: {key} entry uses an external or data URI")
    for img in doc.get("images", []):
        bv = doc["bufferViews"][img["bufferView"]]
        head = data[boff + 8 + bv["byteOffset"]:][:8]
        if img.get("mimeType") != "image/png" or head != b"\x89PNG\r\n\x1a\n":
            errs.append(f"{name}: an image is not an embedded PNG")
    mats = {m["name"]: m for m in doc["materials"]}
    baked = mats.get("prop.baked")
    if baked is None or "baseColorTexture" not in baked["pbrMetallicRoughness"]:
        errs.append(f"{name}: no textured prop.baked material")
    contact = mats.get("prop.contact")
    if contact is None or contact.get("alphaMode") != "MASK" or "baseColorTexture" not in contact["pbrMetallicRoughness"]:
        errs.append(f"{name}: the contact disc must be a textured alpha-MASK material")
    for mname, m in mats.items():
        if m.get("alphaMode") == "BLEND":
            errs.append(f"{name}: {mname} is BLEND; the world pass has blending off")
    ember = [m for m in doc["materials"] if "emissiveFactor" in m]
    if name != "brazier" and ember:
        errs.append(f"{name}: only the brazier's coals may be emissive")
    for m in ember:
        if max(m["emissiveFactor"]) > cfg(name)["ember_max"]:
            errs.append(f"{name}: emissive {m['emissiveFactor']} over the {cfg(name)['ember_max']} night budget")
    return errs


def model_points(a: dict) -> np.ndarray:
    """Vertices of the baked and ember primitives (not the contact disc), world units, glTF axes."""
    return np.vstack([p.positions for p in a["primitives"] if a["materials"][p.material]["name"] != "prop.contact"])


def shape_errors(name: str, a: dict, fps: dict) -> list[str]:
    errs = []
    c = cfg(name)
    pts = model_points(a)
    lo, hi = pts.min(0), pts.max(0)
    sink = SKIRTED.get(name, 0.0) * GP.WORLD_UNITS_PER_U
    if name in SKIRTED:
        if abs(lo[1] + sink) > 0.04:
            errs.append(f"{name}: footing bottom at y {lo[1]:.3f}, expected {-sink:.3f}")
    elif lo[1] > c["ground_tol"]:
        errs.append(f"{name}: lowest vertex y {lo[1]:+.3f} floats above the ground (tolerance {c['ground_tol']})")
    elif lo[1] < -c["buried_max"]:
        errs.append(f"{name}: lowest vertex y {lo[1]:+.3f} is buried deeper than {c['buried_max']}")
    if name in fps:
        hw, hd, _ = fps[name]
        # What the footprint claims is the ground the prop covers: the vertices up to LOW_Y, so a tree's canopy, a
        # signpost's arm and a scarecrow's crossbar (all in the air) do not count against it.
        low = pts[pts[:, 1] <= c["footprint_low_y"]]
        ex, ez = (max(abs(low[:, 0]).max(), 0.0), max(abs(low[:, 2]).max(), 0.0)) if len(low) else (0.0, 0.0)
        if ex > hw * c["footprint_slack"] + c["footprint_pad"] or ez > hd * c["footprint_slack"] + c["footprint_pad"]:
            errs.append(f"{name}: at ground level the model reaches x {ex:.2f} z {ez:.2f} but PropCatalog's footprint is {hw} x {hd}")
        if (hi[0] - lo[0]) / 2 < hw * c["footprint_floor"] or (hi[2] - lo[2]) / 2 < hd * c["footprint_floor"]:
            errs.append(f"{name}: model is {(hi[0]-lo[0])/2:.2f} x {(hi[2]-lo[2])/2:.2f} half extents, far inside PropCatalog's {hw} x {hd}")
    else:
        errs.append(f"{name}: no footprint in PropCatalog.cs")
    return errs


# the work ---------------------------------------------------------------------------------------------


def job(name: str):
    """Worker: one build, the GLB bytes, the metrics, and the asset pieces the gates need (plain data)."""
    a = GP.build_asset(name)
    glb = GP.glb_bytes(a)
    samples = texel_samples(a)
    rq, rl = rect_metrics(a["atlas"], a["rects"])
    m = {"rect_quad": rq, "rect_lowfreq": rl, "azimuth": azimuth_leak(samples), "azimuth_keyed": azimuth_leak(with_key(samples)),
         **palette(a["atlas"], a["rects"]), **detail_metrics(a),
         "tris": sum(len(p.indices) for p in a["primitives"]), "verts": sum(len(p.positions) for p in a["primitives"]), "bytes": len(glb)}
    keep = {k: a[k] for k in ("meshes", "uvs", "rects", "encoded", "atlas", "primitives", "materials", "spec")}
    return name, glb, m, keep


def run(names, workers: int):
    if workers <= 1:
        return {r[0]: r[1:] for r in map(job, names)}
    with ProcessPoolExecutor(max_workers=workers) as pool:
        return {r[0]: r[1:] for r in pool.map(job, names)}


def metric_errors(name: str, m: dict) -> list[str]:
    c = cfg(name)
    errs = []
    if m["rect_quad"] > c["rect_quad_max"]:
        errs.append(f"{name}: part rectangles' quadrant luma spread {m['rect_quad']:.3f} > {c['rect_quad_max']} (baked direction)")
    if m["rect_lowfreq"] > c["rect_lowfreq_max"]:
        errs.append(f"{name}: part rectangles' low-frequency luma share {m['rect_lowfreq']:.3f} > {c['rect_lowfreq_max']} (baked gradient)")
    if m["azimuth"] > c["azimuth_max"]:
        errs.append(f"{name}: azimuth leakage {m['azimuth']:.3f} > {c['azimuth_max']} (a baked key: luma follows the way a surface faces)")
    if m["sat_p95"] > c["sat_p95_max"]:
        errs.append(f"{name}: saturation p95 {m['sat_p95']:.3f} > {c['sat_p95_max']}")
    if m["forbidden_hue"] > c["forbidden_hue_max"]:
        errs.append(f"{name}: {m['forbidden_hue']:.4f} of texels in forbidden hues > {c['forbidden_hue_max']}")
    if m["chrome"] > c["chrome_max"]:
        errs.append(f"{name}: chrome share {m['chrome']:.4f} > {c['chrome_max']}")
    if m["v_p99"] > c["v_p99_max"]:
        errs.append(f"{name}: p99 V {m['v_p99']:.3f} > {c['v_p99_max']} (too bright for the grim palette)")
    if m["v_p99"] < c["v_p99_min"]:
        errs.append(f"{name}: p99 V {m['v_p99']:.3f} < {c['v_p99_min']} (a black hole)")
    if m["mean_v"] > c["mean_v_max"]:
        errs.append(f"{name}: mean V {m['mean_v']:.3f} > {c['mean_v_max']}")
    # One branch per ceiling, not one or-ed condition: with the three sharing a condition the self-test only
    # mutated tris, so the verts and bytes ceilings could have stopped measuring without anything going red.
    errs += detail_errors(name, m, c)
    if m["tris"] > c["tris_max"]:
        errs.append(f"{name}: {m['tris']} triangles over the {c['tris_max']} budget")
    if m["verts"] > c["verts_max"]:
        errs.append(f"{name}: {m['verts']} vertices over the {c['verts_max']} budget")
    if m["bytes"] > c["bytes_max"]:
        errs.append(f"{name}: {m['bytes']} bytes over the {c['bytes_max']} budget")
    return errs


# synthetic negatives: each must fail its gate -------------------------------------------------------------


def self_test() -> list[str]:
    errs = []
    base = {"rect_quad": 0.05, "rect_lowfreq": 0.2, "azimuth": 0.02, "sat_p95": 0.2, "forbidden_hue": 0.0, "chrome": 0.0,
            "v_p99": 0.3, "mean_v": 0.1, "tris": 1000, "verts": 1000, "bytes": 1000, "hf": 0.05, "albedo_max": 0.3,
            "precap_albedo_max": 0.3, "cap_scale": 1.0, "moved_fraction": 0.0}
    if metric_errors("selftest", base):
        errs.append("self-test: a clean metric set fails its own gates")
    cases = {"rect_quad": 0.9, "rect_lowfreq": 0.99, "azimuth": 0.8, "sat_p95": 0.9, "forbidden_hue": 0.5, "chrome": 0.2, "v_p99": 0.95,
             "mean_v": 0.7, "tris": 10 ** 6, "verts": 10 ** 6, "bytes": 10 ** 9, "hf": 0.004, "albedo_max": 0.7,
             "precap_albedo_max": 5.0, "cap_scale": 0.01}
    for k, v in cases.items():
        if not metric_errors("selftest", {**base, k: v}):
            errs.append(f"self-test: gate {k} did not fail its negative")
    if not metric_errors("selftest", {**base, "v_p99": 0.01}):
        errs.append("self-test: the black-hole gate did not fail its negative")
    # the azimuth gate against a real key: a flat-shaded cylinder wall baked with a key from the iso direction
    r = np.random.default_rng(3)
    th = r.uniform(0, math.tau, 20000)
    nx, ny, nz = np.cos(th), np.sin(th), np.zeros_like(th)
    y = 0.2 + 0.02 * r.standard_normal(th.shape)
    clean = [(y, nx, ny, nz)]
    if azimuth_leak(clean) > GATES["azimuth_max"]:
        errs.append("self-test: azimuth_leak flags an unlit cylinder")
    if azimuth_leak(with_key(clean)) <= GATES["azimuth_max"]:
        errs.append("self-test: azimuth_leak did not catch a baked key on a cylinder")
    # and the rectangle gates against a ramp
    ramp = np.tile(np.linspace(0.05, 0.4, 64, dtype=np.float32), (64, 1))
    rgba = np.zeros((64, 64, 4), np.uint8)
    rgba[..., :3] = (ramp[..., None] * 255).astype(np.uint8)
    rgba[..., 3] = 255
    rq, rl = rect_metrics(rgba, [C.Rect(0, 0, 64, 64)])
    if rq <= GATES["rect_quad_max"] and rl <= GATES["rect_lowfreq_max"]:
        errs.append(f"self-test: a lit ramp passed the rectangle gates (quad {rq:.2f}, low {rl:.2f})")
    # the detail gates against real textures: a smooth ramp has no gradient to speak of, and a bright one clips
    ramp8 = np.zeros((64, 64, 4), np.uint8)
    ramp8[..., :3] = np.tile(np.linspace(40, 90, 64, dtype=np.uint8), (64, 1))[..., None]
    full = np.ones((64, 64), bool)
    if hf_gradient(ramp8, full) >= GATES["hf_min"]:
        errs.append("self-test: a smooth ramp reads as detailed; hf_gradient has stopped measuring")
    noise8 = ramp8.copy()
    noise8[..., :3] = np.random.default_rng(5).integers(40, 90, (64, 64, 1), dtype=np.uint8)
    if hf_gradient(noise8, full) < GATES["hf_min"]:
        errs.append("self-test: noise reads as flat; hf_gradient has stopped measuring")
    if hf_gradient(noise8, np.zeros((64, 64), bool)) != 0.0:
        errs.append("self-test: hf_gradient counts texels outside the used mask")
    bright = np.full((8, 8, 4), 255, np.uint8)
    if not detail_errors("x", {"hf": 1.0, "precap_albedo_max": 0.3, "cap_scale": 1.0, "albedo_max": albedo_max(bright, 0.6, np.ones((8, 8), bool))}, GATES):
        errs.append("self-test: byte * baseColorFactor over the ceiling passed albedo_clip_max")
    if detail_errors("x", {"hf": 1.0, "precap_albedo_max": 0.3, "cap_scale": 1.0, "albedo_max": albedo_max(bright, 0.3, np.ones((8, 8), bool))}, GATES):
        errs.append("self-test: byte * baseColorFactor under the ceiling failed albedo_clip_max")
    if albedo_max(bright, 0.6, np.zeros((8, 8), bool)) != 0.0:
        errs.append("self-test: albedo_max counts texels outside the used mask")
    # the pre-cap gates THROUGH the real pipeline: a bake far over the ceiling is capped (albedo_clip_max alone would pass it) and
    # must still fail on what the cap had to do
    bright = np.zeros((64, 64, 4), np.uint8)
    bright[..., :3] = 235
    bright[..., 3] = 255
    atlas, rep = GP._detail_and_cap("selftest", bright, [C.Rect(8, 8, 48, 48)])
    capped_eff = float(U.srgb_to_linear(atlas[..., :3].astype(np.float32) / 255.0).max()) * GP.ALBEDO_LIFT
    if capped_eff > GATES["albedo_clip_max"]:
        errs.append(f"self-test: the real cap left effective albedo {capped_eff:.3f} over the ceiling")
    if not detail_errors("selftest", {"hf": 1.0, "albedo_max": capped_eff, **rep}, GATES):
        errs.append(f"self-test: an over-bright bake ({rep}) passed through the real cap without failing a pre-cap gate")
    # the seam gate against the old unwrap
    spec = GP.PROPS["rust_barrel"]
    parts = [p for p in spec.build() if p.mesh is not None]
    meshes = [p.mesh for p in parts]
    old_uvs, old_rects = C.unwrap(meshes, spec.atlas)
    bad, _ = seam_report(meshes, old_uvs, old_rects, spec.atlas)
    if bad == 0:
        errs.append("self-test: the seam gate found no straddlers in the old unwrap(); it has stopped measuring")
    # the footprint gate
    fake = {"primitives": [G.GltfPrimitive(np.array([[-5, 0, -5], [5, 3, 5], [0, 1, 0]], np.float32), np.zeros((3, 3), np.float32),
                                           np.zeros((3, 2), np.float32), np.array([[0, 1, 2]], np.uint32), None, None, 0)],
            "materials": [{"name": "prop.baked"}]}
    if not shape_errors("brazier", fake, {"brazier": (0.3, 0.3, 0)}):
        errs.append("self-test: an oversized model passed the footprint gate")
    # the container gates
    if not glb_errors("x", b"not a glb", {}):
        errs.append("self-test: garbage passed the container gate")
    return errs


# main ------------------------------------------------------------------------------------------------


def table_job(args):
    """Worker for --table: one asset, built with the detail on (baseline False) or exactly as master built it (baseline True)."""
    import importlib
    family, name, baseline = args
    K = importlib.import_module("groundkit")
    saved = (K.detail_kind, GP._detail_and_cap)
    try:
        if baseline:
            K.detail_kind = lambda *a: None
            GP._detail_and_cap = lambda n, atlas, rects: (atlas, None)
        if family == "props":
            a = GP.build_asset(name)
        else:
            mod = importlib.import_module("gen_ground_flora" if family == "flora" else "gen_ground_stone")
            a = K.build_ground_asset(family, name, mod.ASSETS[name])
        return detail_metrics(a)
    finally:
        K.detail_kind, GP._detail_and_cap = saved


def write_table(path: Path) -> None:
    """--table PATH: the per-asset evidence for hf_min and the pre-cap gates, for every flora, stone and prop atlas. 'before' is the bake
    exactly as master produced it (no detail, no cap); 'after' is what ships."""
    import importlib
    jobs = [("props", n) for n in NAMES]
    for fam, mod in (("flora", "gen_ground_flora"), ("stone", "gen_ground_stone")):
        jobs += [(fam, n) for n in importlib.import_module(mod).ASSETS]
    with ProcessPoolExecutor(max_workers=4) as pool:
        before = list(pool.map(table_job, [(f, n, True) for f, n in jobs]))
        after = list(pool.map(table_job, [(f, n, False) for f, n in jobs]))
    rows = ["# Atlas detail: per-asset measurements", "",
            "Generated by `python tools/ui-assets/tests/check_props.py --table tools/ui-assets/atlas_detail_table.md`; do not edit by hand.", "",
            "- hf: mean absolute 4-neighbour luma gradient of the shipped texture bytes over the used uv rectangles, in /255 (floor `hf_min` = 7.0).",
            "- albedo: largest effective albedo, texture byte / 255 * baseColorFactor (ceiling `albedo_clip_max` = 0.42).",
            "- before: the bake as master built it (no detail field, no cap). after: what ships.",
            "- precap: the brightest effective albedo the bake produced before the cap; cap scale: the uniform darkening the cap applied; moved: the share of",
            "  texels the cap changed by more than 0.02. `precap_albedo_max` and `cap_scale_min` gate the first two; moved is reported only.", "",
            "| family | asset | hf before | hf after | albedo before | albedo after | precap | cap scale | moved |",
            "|---|---|---|---|---|---|---|---|---|"]
    for (f, n), b, a in zip(jobs, before, after):
        rows.append(f"| {f} | {n} | {b['hf'] * 255:.2f} | {a['hf'] * 255:.2f} | {b['albedo_max']:.3f} | {a['albedo_max']:.3f} | "
                    f"{a['precap_albedo_max']:.3f} | {a['cap_scale']:.2f} | {a['moved_fraction']:.1%} |")
    rows += ["", f"{len(jobs)} assets; min hf after {min(a['hf'] for a in after) * 255:.2f}, max albedo after {max(a['albedo_max'] for a in after):.3f}, "
             f"max precap {max(a['precap_albedo_max'] for a in after):.3f}, min cap scale {min(a['cap_scale'] for a in after):.2f}, "
             f"max moved {max(a['moved_fraction'] for a in after):.1%}.", ""]
    path.write_text("\n".join(rows), encoding="utf-8", newline="\n")
    print(f"check_props: wrote {path}")


def main() -> int:
    if "--table" in sys.argv:
        write_table(Path(sys.argv[sys.argv.index("--table") + 1]))
        return 0
    fast, meas = "--fast" in sys.argv, "--measure" in sys.argv
    errs: list[str] = [] if meas else self_test()
    only = [x for x in sys.argv[1:] if not x.startswith("--")]
    names = [n for n in NAMES if not only or n in only]
    workers = 4 if len(names) > 3 else 1
    print(f"check_props: building {len(names)} props ({workers} workers)")
    first = run(names, workers)
    if meas:
        for n, (_, m, _) in first.items():
            print(n, " ".join(f"{k} {v:.4f}" if isinstance(v, float) else f"{k} {v}" for k, v in m.items()))
        return 0
    fps = footprints()
    manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
    sprite_names = {e["name"] for e in manifest["objects"]}
    for n, (glb, m, a) in first.items():
        errs += glb_errors(n, glb, m)
        errs += metric_errors(n, m)
        errs += atlas_errors(n, a)
        errs += shape_errors(n, a, fps)
        src = a["spec"].source
        if src.startswith("sprite:") and src[7:] not in sprite_names:
            errs.append(f"{n}: claims the unused sprite {src[7:]!r}, which iso_manifest.json does not list")
        print(f"  {n:16s} tris {m['tris']:6d} bytes {m['bytes']:8d} quad {m['rect_quad']:.2f} low {m['rect_lowfreq']:.2f} "
              f"az {m['azimuth']:.2f} (keyed {m['azimuth_keyed']:.2f}) satp95 {m['sat_p95']:.2f} vp99 {m['v_p99']:.2f} hf {m['hf'] * 255:.2f} albedo {m['albedo_max']:.3f}")
        if m["azimuth_keyed"] <= cfg(n)["azimuth_max"]:
            errs.append(f"{n}: the azimuth gate is blind on this prop: a synthetic key reads {m['azimuth_keyed']:.3f}, under the {cfg(n)['azimuth_max']} ceiling")
    # every shipped file belongs to a generated prop, and the catalog names exactly the generated set
    shipped = {p.stem for p in SHIPPED.glob("*.glb")} if SHIPPED.exists() else set()
    for extra in sorted(shipped - set(NAMES)):
        errs.append(f"stale shipped file {extra}.glb: no generator prop produces it")
    for stem in sorted(set(fps) ^ set(NAMES)):
        errs.append(f"{stem}: in only one of PropCatalog.cs and gen_props.PROPS")
    if not fast:
        second = run(names, workers)
        for n in names:
            if first[n][0] != second[n][0]:
                errs.append(f"{n}: two builds produced different GLB bytes")
            committed = SHIPPED / f"{n}.glb"
            if not committed.exists():
                errs.append(f"{n}: not shipped under {SHIPPED}")
            elif committed.read_bytes() != first[n][0]:
                errs.append(f"{n}: the committed file differs from a fresh build (regenerate with gen_props.py and commit)")
        det = sum(first[n][0] == second[n][0] for n in names)
        print(f"  deterministic: {det}/{len(names)} identical across two builds")
    for e in errs:
        print("FAIL", e)
    print(f"props: {len(errs)} problems, {len(names)} props checked")
    return 1 if errs else 0


if __name__ == "__main__":
    sys.exit(main())
