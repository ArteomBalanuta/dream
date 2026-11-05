"""Gates for the ground assets of family "stone": tools/ui-assets/gen_ground_stone.py and what it ships under
dream/animations/ground/ (stone.json and stone/*.glb).

    python tools/ui-assets/tests/check_ground_stone.py [--fast] [--measure] [stem ...]

  --fast     skip the second build (determinism); the shipped-bytes comparison still runs
  --measure  print every metric for every asset and exit, to calibrate the constants below

Exits nonzero on failure. Every gate has a synthetic negative that must fail it (self_test), the style of check_props.py.
Do not loosen a constant to pass: fix the art, or add an OVERRIDES entry that says why, with the measured value.

What is gated:
  * the set: exactly the 36 stems, the class counts, the layer of each class, the ground lists (rocks anywhere but a path,
    bones on dirt, mud, sand, logs and stumps on grass, dirt, mud), rust on the four items and nowhere else;
  * the manifest: groundkit.validate_manifest on the committed stone.json (schema, ids, class and layer, materials, files,
    no stale GLB); manifest radius, footprint and height equal what the fresh build measures;
  * the shipped bytes: groundkit.family_errors renders main() into a temp directory and byte-compares it with what is committed;
  * determinism: two builds give identical bytes (unless --fast);
  * the GLB container: one node, one mesh, no skin or animation, embedded PNG images, no emissive, double-sided baked material,
    alpha-MASK contact disc only on accent assets;
  * budgets (groundkit.budget_for): triangles, bytes, atlas size, and the family total under 2.5 MB;
  * grounding: the lowest vertex of every GLB sits at y = 0; cover assets are no taller than 0.4 world units;
  * no baked key and the grim palette: the check_props gates (quadrant spread, low-frequency share, azimuth leakage, saturation,
    forbidden hues, chrome, value ceilings) on every atlas.
"""
from __future__ import annotations

import json
import math
import struct
import sys
from concurrent.futures import ProcessPoolExecutor
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
TOOLS = HERE.parent
sys.path.insert(0, str(TOOLS))
sys.path.insert(0, str(HERE))
import check_props as CP  # noqa: E402
import gen_ground_stone as GS  # noqa: E402
import groundkit as K  # noqa: E402

FAMILY = "stone"
NAMES = tuple(GS.ASSETS)
EXPECTED_COUNT = 36
EXPECTED_CLASSES = {"rock": 8, "pebbles": 3, "stump": 4, "log": 4, "root": 3, "twigs": 3, "leaves": 3, "bones": 4, "item": 4}
EXPECTED_LAYERS = {"rock": {"accent": 5, "cover": 3}, "pebbles": {"cover": 3}, "stump": {"accent": 4}, "log": {"accent": 4}, "root": {"cover": 3},
                   "twigs": {"cover": 3}, "leaves": {"cover": 3}, "bones": {"cover": 4}, "item": {"cover": 4}}

GATES = dict(CP.GATES)
GATES.update({
    "family_bytes_max": K.FAMILY_BYTES_MAX[FAMILY],
    "cover_height_max": 0.40,    # world units: ground cover stays under the knee of a 1.2 unit player
    "ground_tol": 0.01,          # the lowest vertex of a GLB, world units
    "bounds_tol": 0.0006,        # manifest vs measured, world units (the manifest rounds to 3 decimals)
})
# An override is a claim that the art needs it: say why, with the measured value, and keep it as tight as the measurement allows.
OVERRIDES: dict[str, dict] = {
    # Azimuth leakage is the correlation of a texel's luma with the way it faces, over the side-facing texels of each part. On
    # these small or sparse assets few texels face sideways, so the vertex mottle and the cavity bake correlate with the normal by
    # chance; none of them carries a key (the synthetic key reads .5 to .9 on every one, so the gate is not blind here). Each
    # ceiling is the measured value rounded up, so a real key (which adds several tenths) still fails.
    "pebbles_01": {"azimuth_max": 0.35},         # measured .332 (nine pebbles, three rows each)
    "bones_ribcage": {"azimuth_max": 0.35},      # measured .330
    "bones_skull": {"azimuth_max": 0.30},        # measured .283
    "rock_slab_02": {"azimuth_max": 0.30},       # measured .277
    "stump_axe": {"azimuth_max": 0.27},          # measured .247
    # Seam share: triangles that still straddle a cylinder seam after unwrap_split, over cylinder triangles (check_props' budget .015,
    # measured there at 1.2 percent at most). On these three a few large flat triangles of a small mesh do; they smear a few texels.
    "rock_scree_02": {"azimuth_max": 0.28, "seam_share_max": 0.05},   # measured .043 (11 of 254), azimuth .259
    "roots_surface_01": {"seam_share_max": 0.03},                      # measured .023 (6 of 264)
    "bones_antler": {"azimuth_max": 0.28, "seam_share_max": 0.025},    # measured .020 (8 of 392), azimuth .263
}
# Assets with no 200 side-facing texels in any part (flat leaves, round pebbles, a flat shield): azimuth_leak has nothing to
# correlate, and reads 0 for the keyed negative too. The quadrant and low-frequency gates still guard them.
CP.OVERRIDES.update(OVERRIDES)   # CP.atlas_errors reads its own cfg(): hand it our per-asset seam ceilings (keys are distinct from the props)
NO_SIDE_TEXELS = {"pebbles_03", "leaf_heap_01", "leaf_heap_02", "leaf_heap_03", "item_split_shield"}


def cfg(name: str) -> dict:
    c = dict(GATES)
    c.update(OVERRIDES.get(name, {}))
    return c


# measuring ---------------------------------------------------------------------------------------------


def glb_json(data: bytes) -> dict | None:
    if len(data) < 20 or data[:4] != b"glTF":
        return None
    n, kind = struct.unpack_from("<II", data, 12)
    if kind != 0x4E4F534A:
        return None
    return json.loads(data[20:20 + n].decode("utf-8"))


def glb_errors(name: str, data: bytes, layer: str, has_disc: bool) -> list[str]:
    doc = glb_json(data)
    if doc is None:
        return [f"{name}: not a GLB"]
    errs = []
    if len(doc.get("nodes", [])) != 1:
        errs.append(f"{name}: {len(doc.get('nodes', []))} nodes, expected exactly one")
    if len(doc.get("meshes", [])) != 1:
        errs.append(f"{name}: {len(doc.get('meshes', []))} meshes, expected one")
    if doc.get("skins") or doc.get("animations"):
        errs.append(f"{name}: carries a skin or an animation (must be rigid)")
    if any("uri" in i for i in doc.get("images", [])) or not doc.get("images"):
        errs.append(f"{name}: images must be embedded PNG, none external")
    mats = doc.get("materials", [])
    names = [m.get("name") for m in mats]
    if names[:1] != [K.BAKED_MATERIAL] or len(names) > 2 or (len(names) == 2 and names[1] != K.CONTACT_MATERIAL):
        errs.append(f"{name}: materials {names}")
    for m in mats:
        if any(v for v in m.get("emissiveFactor", [0, 0, 0])):
            errs.append(f"{name}: material {m.get('name')} is emissive")
    if mats and not mats[0].get("doubleSided"):
        errs.append(f"{name}: the baked material is not double sided")
    if mats and "baseColorTexture" not in mats[0].get("pbrMetallicRoughness", {}):
        errs.append(f"{name}: the baked material has no base colour texture")
    if len(mats) == 2:
        if mats[1].get("alphaMode") != "MASK":
            errs.append(f"{name}: the contact disc is not alpha-MASK")
        if layer == "cover":
            errs.append(f"{name}: a cover asset must not carry a contact disc")
    if has_disc != (len(mats) == 2):
        errs.append(f"{name}: spec.disc and the GLB's contact material disagree")
    return errs


def measured_bounds(a: dict) -> tuple[float, float, float, float]:
    """-> (radius, footprint, height, lowest y) from the exported primitives, the way groundkit measures them."""
    pos = a["primitives"][0].positions.astype(np.float64)    # the baked mesh: the contact disc is ground shading, not geometry
    horiz = np.hypot(pos[:, 0], pos[:, 2])
    low = pos[:, 1] < 0.15
    fp = float(np.abs(pos[low][:, [0, 2]]).max()) if low.any() else 0.0
    return float(horiz.max()), fp, float(pos[:, 1].max()), float(pos[:, 1].min())


class AtlasSpec:
    def __init__(self, atlas: int):
        self.atlas = atlas


def job(name: str):
    """Worker: one build, the GLB bytes, the metrics, and the asset pieces the gates need (plain data)."""
    spec = GS.ASSETS[name]
    a = K.build_ground_asset(FAMILY, name, spec)
    glb = K.glb_bytes(a)
    samples = CP.texel_samples(a)
    rq, rl = CP.rect_metrics(a["atlas"], a["rects"])
    radius, fp, height, low = measured_bounds(a)
    m = {"rect_quad": rq, "rect_lowfreq": rl, "azimuth": CP.azimuth_leak(samples), "azimuth_keyed": CP.azimuth_leak(CP.with_key(samples)),
         **CP.palette(a["atlas"], a["rects"]), **CP.detail_metrics(a),
         "tris": sum(len(p.indices) for p in a["primitives"]), "verts": sum(len(p.positions) for p in a["primitives"]), "bytes": len(glb),
         "radius": radius, "footprint": fp, "height": height, "low_y": low}
    keep = {k: a[k] for k in ("meshes", "uvs", "rects", "encoded", "atlas", "primitives", "materials", "bounds")}
    keep["spec"] = AtlasSpec(spec.atlas)       # the GroundSpec holds a lambda, which cannot cross a process boundary
    return name, glb, m, keep


def run(names, workers: int):
    if workers <= 1:
        return {r[0]: r[1:] for r in map(job, names)}
    with ProcessPoolExecutor(max_workers=workers) as pool:
        return {r[0]: r[1:] for r in pool.map(job, names)}


# gates ---------------------------------------------------------------------------------------------------


def metric_errors(name: str, m: dict, cls: str, layer: str) -> list[str]:
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
    errs += CP.detail_errors(name, m, c)
    b = K.budget_for(cls, layer)
    if m["tris"] > b["tris_max"]:
        errs.append(f"{name}: {m['tris']} triangles over the {b['tris_max']} budget for {cls}/{layer}")
    if m["bytes"] > b["bytes_max"]:
        errs.append(f"{name}: {m['bytes']} bytes over the {b['bytes_max']} budget for {cls}/{layer}")
    if m["low_y"] < -c["ground_tol"] or m["low_y"] > c["ground_tol"]:
        errs.append(f"{name}: lowest vertex at y = {m['low_y']:.4f}, must stand on 0 (tolerance {c['ground_tol']})")
    if layer == "cover" and m["height"] > c["cover_height_max"]:
        errs.append(f"{name}: cover height {m['height']:.3f} wu > {c['cover_height_max']}")
    return errs


def blind_errors(name: str, m: dict) -> list[str]:
    """The azimuth gate must be able to see a key on this asset (unless it has no side-facing texels to measure)."""
    if name in NO_SIDE_TEXELS:
        if m["azimuth_keyed"] > 0:
            return [f"{name}: listed in NO_SIDE_TEXELS but the azimuth gate now measures it (keyed {m['azimuth_keyed']:.3f}): drop it from the set"]
        return []
    if m["azimuth_keyed"] <= cfg(name)["azimuth_max"]:
        return [f"{name}: the azimuth gate is blind on this asset: a synthetic key reads {m['azimuth_keyed']:.3f}, under the {cfg(name)['azimuth_max']} ceiling"]
    return []


def atlas_budget_errors(name: str, atlas_px: int, cls: str, layer: str) -> list[str]:
    b = K.budget_for(cls, layer)
    return [f"{name}: atlas {atlas_px} px over the {b['atlas']} px budget for {cls}/{layer}"] if atlas_px > b["atlas"] else []


def spec_errors(stems, assets) -> list[str]:
    """The set as authored: counts, layers, ground lists and rust."""
    errs = []
    if len(stems) != EXPECTED_COUNT:
        errs.append(f"set: {len(stems)} assets, expected {EXPECTED_COUNT}")
    classes: dict[str, int] = {}
    layers: dict[str, dict[str, int]] = {}
    for n in stems:
        s = assets[n]
        classes[s.cls] = classes.get(s.cls, 0) + 1
        layers.setdefault(s.cls, {})
        layers[s.cls][s.layer] = layers[s.cls].get(s.layer, 0) + 1
        if s.cls not in K.CLASSES or s.layer not in K.CLASSES[s.cls]:
            errs.append(f"{n}: class {s.cls!r} does not allow layer {s.layer!r}")
        if not s.ground or any(g not in K.MATERIALS for g in s.ground):
            errs.append(f"{n}: ground {s.ground} is not a list of known materials")
        if s.cls == "rock" and "grass_path" in s.ground:
            errs.append(f"{n}: a rock must not lie on a path")
        if s.cls == "bones" and not set(s.ground) <= {"dirt", "mud", "sand"}:
            errs.append(f"{n}: bones belong on dirt, mud, sand, not {s.ground}")
        if s.cls in ("log", "stump") and not set(s.ground) <= {"grass", "dirt", "mud"}:
            errs.append(f"{n}: logs and stumps belong on grass, dirt, mud, not {s.ground}")
        rust = s.age[2]
        if (s.cls == "item") != (rust > 0):
            errs.append(f"{n}: rust {rust} - only the dropped items may rust, and every one of them does")
        if s.layer == "cover" and s.disc[0] > 0:
            errs.append(f"{n}: a cover asset carries no contact disc")
        if not n.startswith({"rock": ("boulder", "rock_"), "pebbles": ("pebbles",), "stump": ("stump",), "log": ("log",), "root": ("roots",),
                             "twigs": ("twigs",), "leaves": ("leaf",), "bones": ("bones",), "item": ("item",)}.get(s.cls, ("?",))):
            errs.append(f"{n}: the stem does not match its class {s.cls}")
    if classes != EXPECTED_CLASSES:
        errs.append(f"set: class counts {classes}, expected {EXPECTED_CLASSES}")
    if layers != EXPECTED_LAYERS:
        errs.append(f"set: layer split {layers}, expected {EXPECTED_LAYERS}")
    return errs


def manifest_errors(doc: dict, root: Path | None, measured: dict[str, dict] | None) -> list[str]:
    errs = K.validate_manifest(doc, root)
    got = [a.get("id") for a in doc.get("assets", [])]
    want = [f"{FAMILY}.{n}" for n in NAMES]
    if got != want:
        errs.append(f"manifest: ids {got} differ from the generator's {want} (or their order)")
    if measured is not None:
        tol = GATES["bounds_tol"]
        for a in doc.get("assets", []):
            stem = str(a.get("id", "")).split(".", 1)[-1]
            m = measured.get(stem)
            if m is None:
                continue
            for key, mk in (("radius", "radius"), ("footprint", "footprint"), ("height", "height")):
                if abs(float(a.get(key, -1)) - m[mk]) > tol:
                    errs.append(f"{a['id']}: manifest {key} {a.get(key)} != measured {m[mk]:.4f}")
    return errs


def family_total_errors(sizes: dict[str, int]) -> list[str]:
    total = sum(sizes.values())
    return [f"family total {total} bytes over the {GATES['family_bytes_max']} budget"] if total > GATES["family_bytes_max"] else []


# synthetic negatives: each must fail its gate --------------------------------------------------------------


def self_test() -> list[str]:
    errs = []
    base = {"rect_quad": 0.05, "rect_lowfreq": 0.2, "azimuth": 0.02, "sat_p95": 0.2, "forbidden_hue": 0.0, "chrome": 0.0, "v_p99": 0.3,
            "mean_v": 0.1, "tris": 100, "bytes": 1000, "low_y": 0.0, "height": 0.1, "hf": 0.05, "albedo_max": 0.3,
            "precap_albedo_max": 0.3, "cap_scale": 1.0, "moved_fraction": 0.0,
            "eff_mean_v": 0.15, "eff_mean_sat": 0.3, "base_mean_v": 0.15, "base_mean_sat": 0.3}
    if metric_errors("selftest", base, "rock", "cover"):
        errs.append("self-test: a clean metric set fails its own gates")
    cases = {"rect_quad": 0.9, "rect_lowfreq": 0.99, "azimuth": 0.8, "sat_p95": 0.9, "forbidden_hue": 0.5, "chrome": 0.2, "v_p99": 0.95,
             "mean_v": 0.7, "tris": 10 ** 6, "bytes": 10 ** 9, "low_y": 0.2, "height": 0.9, "hf": 0.004, "albedo_max": 0.7,
             "precap_albedo_max": 5.0, "cap_scale": 0.01, "eff_mean_sat": 0.01}
    for k, v in cases.items():
        if not metric_errors("selftest", {**base, k: v}, "rock", "cover"):
            errs.append(f"self-test: gate {k} did not fail its negative")
    if not metric_errors("selftest", {**base, "low_y": -0.2}, "rock", "cover"):
        errs.append("self-test: a buried asset passed the grounding gate")
    if not metric_errors("selftest", {**base, "v_p99": 0.01}, "rock", "cover"):
        errs.append("self-test: the black-hole gate did not fail its negative")
    if metric_errors("selftest", {**base, "height": 3.0}, "rock", "accent"):
        errs.append("self-test: the cover height gate wrongly applies to an accent asset")
    if not atlas_budget_errors("selftest", 512, "rock", "cover") or atlas_budget_errors("selftest", 128, "rock", "cover"):
        errs.append("self-test: the atlas budget gate is wrong")
    if not family_total_errors({"a": GATES["family_bytes_max"] + 1}) or family_total_errors({"a": 10}):
        errs.append("self-test: the family total gate is wrong")
    if not blind_errors("boulder_01", {"azimuth_keyed": 0.0}) or blind_errors("boulder_01", {"azimuth_keyed": 0.7}):
        errs.append("self-test: the blindness gate is wrong")
    if not blind_errors("leaf_heap_01", {"azimuth_keyed": 0.7}) or blind_errors("leaf_heap_01", {"azimuth_keyed": 0.0}):
        errs.append("self-test: the NO_SIDE_TEXELS gate is wrong")
    # the azimuth gate against a real key, on a flat-shaded cylinder wall
    r = np.random.default_rng(3)
    th = r.uniform(0, math.tau, 20000)
    clean = [(0.2 + 0.02 * r.standard_normal(th.shape), np.cos(th), np.sin(th), np.zeros_like(th))]
    if CP.azimuth_leak(clean) > GATES["azimuth_max"]:
        errs.append("self-test: azimuth_leak flags an unlit cylinder")
    if CP.azimuth_leak(CP.with_key(clean)) <= GATES["azimuth_max"]:
        errs.append("self-test: azimuth_leak did not catch a baked key on a cylinder")
    ramp = np.tile(np.linspace(0.05, 0.4, 64, dtype=np.float32), (64, 1))
    rgba = np.zeros((64, 64, 4), np.uint8)
    rgba[..., :3] = (ramp[..., None] * 255).astype(np.uint8)
    rgba[..., 3] = 255
    rq, rl = CP.rect_metrics(rgba, [CP.C.Rect(0, 0, 64, 64)])
    if rq <= GATES["rect_quad_max"] and rl <= GATES["rect_lowfreq_max"]:
        errs.append(f"self-test: a lit ramp passed the rectangle gates (quad {rq:.2f}, low {rl:.2f})")
    # the container gate
    if not glb_errors("x", b"not a glb", "cover", False):
        errs.append("self-test: garbage passed the container gate")
    # the set gates, against a mutated copy of the real set
    assets = dict(GS.ASSETS)
    if spec_errors(list(NAMES), assets):
        errs.append(f"self-test: the real set fails its own set gates: {spec_errors(list(NAMES), assets)}")
    import dataclasses
    bad = {**assets, "boulder_01": dataclasses.replace(assets["boulder_01"], ground=("grass", "grass_path"))}
    if not spec_errors(list(NAMES), bad):
        errs.append("self-test: a rock on a path passed")
    bad = {**assets, "bones_skull": dataclasses.replace(assets["bones_skull"], ground=("grass",))}
    if not spec_errors(list(NAMES), bad):
        errs.append("self-test: bones on grass passed")
    bad = {**assets, "log_mossy": dataclasses.replace(assets["log_mossy"], ground=("snow",))}
    if not spec_errors(list(NAMES), bad):
        errs.append("self-test: a log on snow passed")
    bad = {**assets, "twigs_01": dataclasses.replace(assets["twigs_01"], age=(0.4, 0.6, 0.3, 0.1))}
    if not spec_errors(list(NAMES), bad):
        errs.append("self-test: rust on twigs passed")
    bad = {**assets, "item_rusted_helm": dataclasses.replace(assets["item_rusted_helm"], age=(0.4, 0.6, 0.0, 0.1))}
    if not spec_errors(list(NAMES), bad):
        errs.append("self-test: a rustless item passed")
    bad = {**assets, "twigs_01": dataclasses.replace(assets["twigs_01"], disc=(5.0, 5.0))}
    if not spec_errors(list(NAMES), bad):
        errs.append("self-test: a cover disc passed")
    if not spec_errors(list(NAMES)[:-1], assets):
        errs.append("self-test: a short set passed")
    # the manifest gates: a good manifest passes, a wrong bound and a wrong order fail
    import tempfile
    a = K.build_ground_asset(FAMILY, "pebbles_03", assets["pebbles_03"])
    fake = {"schema": K.SCHEMA, "family": FAMILY, "assets": [K.manifest_entry(FAMILY, "pebbles_03", assets["pebbles_03"], a["bounds"])]}
    meas = {"pebbles_03": {"radius": a["bounds"][0], "footprint": a["bounds"][1], "height": a["bounds"][2]}}
    with tempfile.TemporaryDirectory(dir=K._tmp_parent()) as d:
        K.write_family(Path(d), FAMILY, fake["assets"], {"pebbles_03": K.glb_bytes(a)})
        errs_ok = [e for e in manifest_errors(fake, Path(d), meas) if "differ from the generator's" not in e]
        if errs_ok:
            errs.append(f"self-test: a valid manifest fails: {errs_ok}")
        wrong = json.loads(json.dumps(fake))
        wrong["assets"][0]["height"] += 0.05
        if not any("measured" in e for e in manifest_errors(wrong, Path(d), meas)):
            errs.append("self-test: a wrong manifest height passed")
    if not any("differ from the generator's" in e for e in manifest_errors(fake, None, None)):
        errs.append("self-test: a manifest that lists too few assets passed")
    return errs


# main ------------------------------------------------------------------------------------------------


def main() -> int:
    fast, meas = "--fast" in sys.argv, "--measure" in sys.argv
    errs: list[str] = [] if meas else self_test()
    only = [x for x in sys.argv[1:] if not x.startswith("--")]
    names = [n for n in NAMES if not only or n in only]
    workers = 4 if len(names) > 3 else 1
    print(f"check_ground_stone: building {len(names)} assets ({workers} workers)")
    first = run(names, workers)
    if meas:
        for n, (_, m, _) in first.items():
            print(n, " ".join(f"{k} {v:.4f}" if isinstance(v, float) else f"{k} {v}" for k, v in m.items()))
        return 0
    errs += spec_errors(list(NAMES), GS.ASSETS)
    sizes = {}
    for n, (glb, m, a) in first.items():
        spec = GS.ASSETS[n]
        sizes[n] = len(glb)
        errs += glb_errors(n, glb, spec.layer, spec.disc[0] > 0)
        errs += metric_errors(n, m, spec.cls, spec.layer)
        errs += atlas_budget_errors(n, spec.atlas, spec.cls, spec.layer)
        errs += CP.atlas_errors(n, a)
        print(f"  {n:18s} {spec.cls:8s} {spec.layer:6s} tris {m['tris']:5d} bytes {m['bytes']:7d} h {m['height']:.2f} quad {m['rect_quad']:.2f} "
              f"low {m['rect_lowfreq']:.2f} az {m['azimuth']:.2f} (keyed {m['azimuth_keyed']:.2f}) satp95 {m['sat_p95']:.2f} vp99 {m['v_p99']:.2f}")
        errs += blind_errors(n, m)
    if not only:
        errs += family_total_errors(sizes)
        print(f"  family total {sum(sizes.values())} bytes of {GATES['family_bytes_max']}")
    manifest_path = K.GROUND_ROOT / f"{FAMILY}.json"
    if not manifest_path.is_file():
        errs.append(f"{manifest_path} is missing: run tools/ui-assets/gen_ground_stone.py and commit")
    else:
        doc = json.loads(manifest_path.read_text(encoding="utf-8"))
        measured = {n: first[n][1] for n in names}
        errs += [e for e in manifest_errors(doc, K.GROUND_ROOT, measured) if not only or "differ from the generator's" not in e]
    if not only:
        errs += K.family_errors(GS.main, FAMILY)
    if not fast:
        second = run(names, workers)
        for n in names:
            if first[n][0] != second[n][0]:
                errs.append(f"{n}: two builds produced different GLB bytes")
        det = sum(first[n][0] == second[n][0] for n in names)
        print(f"  deterministic: {det}/{len(names)} identical across two builds")
    for e in errs:
        print("FAIL", e)
    print(f"ground stone: {len(errs)} problems, {len(names)} assets checked")
    return 1 if errs else 0


if __name__ == "__main__":
    sys.exit(main())
