"""Gates for the ground flora: tools/ui-assets/gen_ground_flora.py, groundkit.py and the files they ship under
dream/animations/ground/ (flora.json and flora/*.glb).

    python tools/ui-assets/tests/check_ground_flora.py [--fast] [--measure] [stem ...]

  --fast     skip the second build (determinism), the generator re-render of groundkit.family_errors and the preview sheet
  --measure  print every metric for every asset and exit, to calibrate the constants below

Exits nonzero on failure. Every gate has a synthetic negative that must fail it (self_test). Do not loosen a constant to
pass: fix the art, or add an OVERRIDES entry that says why, with the measured value.

What is gated, per asset:
  * the manifest (groundkit.validate_manifest: schema, ids, class/layer, grounds, files exist, no stale glb), the order and
    fields against the generator's ASSETS, and the recorded radius / footprint / height against the measured geometry;
  * the GLB container (groundkit.glb_errors: rigid, one node, embedded PNG, the two material names, no emissive);
  * the shipped bytes: every committed file equals a fresh build (and, without --fast, groundkit.family_errors renders the
    whole family once more), and two builds are identical;
  * budgets by class: triangles, bytes, atlas size; and the family total;
  * grounding (lowest vertex within 0.01 of y = 0), cover height (<= 0.4 wu, grass <= 0.35), trees 3-4.5 wu high with a
    footprint of at most 0.5 wu, a contact disc on trees / saplings / bushes only;
  * no baked key and the grim palette: check_props' quadrant, low-frequency and azimuth gates and its palette ceilings.
"""
from __future__ import annotations

import json
import sys
from concurrent.futures import ProcessPoolExecutor
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
TOOLS = HERE.parent
ROOT = TOOLS.parents[1]
sys.path.insert(0, str(TOOLS))
sys.path.insert(0, str(HERE))
import check_props as CP  # noqa: E402
import gen_ground_flora as F  # noqa: E402
import groundkit as K  # noqa: E402

SHIPPED = K.GROUND_ROOT
GATES = dict(CP.GATES)
GATES.update({
    "ground_tol": 0.01,          # the lowest baked vertex is on the ground
    "cover_h_max": 0.40,         # cover never stands taller than this, in world units (the player is 1.2)
    "grass_h_max": 0.35,
    "tree_h_min": 3.0,
    "tree_h_max": 4.5,
    "tree_fp_max": 0.50,         # half extent of a tree's geometry below 0.15 wu
    "bounds_tol": 0.0006,        # the manifest records 3 decimals
})
# An override is a claim that the art needs it: say why, with the measured value. Do not loosen a ceiling to pass.
# Azimuth leakage is measured over the side-facing texels of each part; thin ribbons, petals and caps in a 128 px atlas leave few
# of them, so a few cover stems read above check_props' 0.23 unkeyed. Each gets its own ceiling at the measured value rounded up,
# which a synthetic baked key (the "keyed" value) must still exceed (see blind_errors).
OVERRIDES: dict[str, dict] = {
    "grass_tuft_dry_03": {"azimuth_max": 0.25},    # measured .2403 (keyed .676)
    "grass_tuft_tall_02": {"azimuth_max": 0.26},   # measured .2589 (keyed .782)
    "grass_tuft_tall_03": {"azimuth_max": 0.29},   # measured .2812 (keyed .735)
    "grass_wiry_01": {"azimuth_max": 0.41},        # measured .4052 (keyed .448)
    "flower_pale_bell": {"azimuth_max": 0.25},     # measured .2416 (keyed .702)
    "flower_withered_rose": {"azimuth_max": 0.26}, # measured .2525 (keyed .802)
    "flower_thistle": {"azimuth_max": 0.26},       # measured .2532 (keyed .898)
    "shroom_bracket_stub": {"azimuth_max": 0.45},  # measured .4403 (keyed .826)
}
# Assets with no 200 side-facing texels in any part (flat blades, a flat ring): azimuth_leak has nothing to measure and reads 0
# unkeyed and keyed alike. The set fails if one of them ever starts measuring.
NO_SIDE_TEXELS = {"grass_tuft_dry_04", "grass_sedge_02", "shroom_ring"}
# 4 of 203 cylinder triangles (1.97%) of the umbel's single stalk straddle the unwrap seam; the stalk is 0.4 u thick
CP.OVERRIDES["flower_hemlock_umbel"] = {"seam_share_max": 0.025}   # check_props.atlas_errors reads its own table
EXPECTED_COUNTS = {"tree": 8, "sapling": 3, "bush": 7, "grass": 10, "flower": 6, "mushroom": 6}


def cfg(stem: str) -> dict:
    c = dict(GATES)
    c.update(OVERRIDES.get(stem, {}))
    return c


def blind_errors(stem: str, m: dict) -> list[str]:
    """The azimuth gate must be able to see a key on this asset (unless it has no side-facing texels to measure)."""
    if stem in NO_SIDE_TEXELS:
        if m["azimuth_keyed"] > 0 or m["azimuth"] > 0:
            return [f"{stem}: listed in NO_SIDE_TEXELS but the azimuth gate now measures it (keyed {m['azimuth_keyed']:.3f}): drop it from the set"]
        return []
    if m["azimuth_keyed"] <= cfg(stem)["azimuth_max"]:
        return [f"{stem}: the azimuth gate is blind on this asset: a synthetic key reads {m['azimuth_keyed']:.3f}, under the {cfg(stem)['azimuth_max']} ceiling"]
    return []


def measure_bounds(positions: np.ndarray) -> tuple[float, float, float]:
    p = positions.astype(np.float64)
    low = p[:, 1] < 0.15
    fp = float(np.abs(p[low][:, [0, 2]]).max()) if low.any() else 0.0
    return float(np.hypot(p[:, 0], p[:, 2]).max()), fp, float(p[:, 1].max())


def job(stem: str):
    """Worker: one build, the GLB bytes, the metrics and the atlas errors (plain data)."""
    spec = F.ASSETS[stem]
    a = K.build_ground_asset(F.FAMILY, stem, spec)
    glb = K.glb_bytes(a)
    samples = CP.texel_samples(a)
    rq, rl = CP.rect_metrics(a["atlas"], a["rects"])
    baked = a["primitives"][0].positions
    radius, fp, height = measure_bounds(baked)
    m = {"rect_quad": rq, "rect_lowfreq": rl, "azimuth": CP.azimuth_leak(samples), "azimuth_keyed": CP.azimuth_leak(CP.with_key(samples)),
         **CP.palette(a["atlas"], a["rects"]),
         "tris": sum(len(p.indices) for p in a["primitives"]), "verts": sum(len(p.positions) for p in a["primitives"]), "bytes": len(glb),
         "radius": radius, "footprint": fp, "height": height, "miny": float(baked[:, 1].min()), "has_disc": a["disc"] is not None,
         "bounds_kit": tuple(float(x) for x in a["bounds"])}
    return stem, glb, m, CP.atlas_errors(stem, a)


def run(stems, workers: int):
    if workers <= 1:
        return {r[0]: r[1:] for r in map(job, stems)}
    with ProcessPoolExecutor(max_workers=workers) as pool:
        return {r[0]: r[1:] for r in pool.map(job, stems)}


def metric_errors(stem: str, m: dict, spec) -> list[str]:
    c = cfg(stem)
    errs = []
    if m["rect_quad"] > c["rect_quad_max"]:
        errs.append(f"{stem}: part rectangles' quadrant luma spread {m['rect_quad']:.3f} > {c['rect_quad_max']} (baked direction)")
    if m["rect_lowfreq"] > c["rect_lowfreq_max"]:
        errs.append(f"{stem}: part rectangles' low-frequency luma share {m['rect_lowfreq']:.3f} > {c['rect_lowfreq_max']} (baked gradient)")
    if m["azimuth"] > c["azimuth_max"]:
        errs.append(f"{stem}: azimuth leakage {m['azimuth']:.3f} > {c['azimuth_max']} (a baked key)")
    if m["sat_p95"] > c["sat_p95_max"]:
        errs.append(f"{stem}: saturation p95 {m['sat_p95']:.3f} > {c['sat_p95_max']}")
    if m["forbidden_hue"] > c["forbidden_hue_max"]:
        errs.append(f"{stem}: {m['forbidden_hue']:.4f} of texels in forbidden hues > {c['forbidden_hue_max']}")
    if m["chrome"] > c["chrome_max"]:
        errs.append(f"{stem}: chrome share {m['chrome']:.4f} > {c['chrome_max']}")
    if m["v_p99"] > c["v_p99_max"]:
        errs.append(f"{stem}: p99 V {m['v_p99']:.3f} > {c['v_p99_max']} (too bright for the grim palette)")
    if m["v_p99"] < c["v_p99_min"]:
        errs.append(f"{stem}: p99 V {m['v_p99']:.3f} < {c['v_p99_min']} (a black hole)")
    if m["mean_v"] > c["mean_v_max"]:
        errs.append(f"{stem}: mean V {m['mean_v']:.3f} > {c['mean_v_max']}")
    b = K.budget_for(spec.cls, spec.layer)
    if m["tris"] > b["tris_max"]:
        errs.append(f"{stem}: {m['tris']} triangles over the {spec.cls} budget of {b['tris_max']}")
    if m["bytes"] > b["bytes_max"]:
        errs.append(f"{stem}: {m['bytes']} bytes over the {spec.cls} / {spec.layer} budget of {b['bytes_max']}")
    if spec.atlas != b["atlas"]:
        errs.append(f"{stem}: atlas {spec.atlas} px, the {spec.cls} / {spec.layer} budget is {b['atlas']}")
    if abs(m["miny"]) > c["ground_tol"]:
        errs.append(f"{stem}: lowest vertex y {m['miny']:+.3f} is not on the ground (tolerance {c['ground_tol']})")
    if spec.layer == "cover":
        lim = c["grass_h_max"] if spec.cls == "grass" else c["cover_h_max"]
        if m["height"] > lim:
            errs.append(f"{stem}: cover stands {m['height']:.3f} wu, over {lim}")
        if m["has_disc"]:
            errs.append(f"{stem}: a cover asset carries a contact disc (only accent / landmark assets do)")
    elif not m["has_disc"]:
        errs.append(f"{stem}: an {spec.layer} asset needs its contact disc")
    if spec.cls == "tree":
        if not c["tree_h_min"] <= m["height"] <= c["tree_h_max"]:
            errs.append(f"{stem}: tree height {m['height']:.2f} wu outside {c['tree_h_min']}-{c['tree_h_max']}")
        if m["footprint"] > c["tree_fp_max"]:
            errs.append(f"{stem}: tree footprint {m['footprint']:.2f} wu over {c['tree_fp_max']}")
    return errs


def manifest_errors(doc: dict, measured: dict[str, dict], root: Path | None = None) -> list[str]:
    errs = K.validate_manifest(doc, root)
    ents = doc.get("assets", [])
    stems = list(F.ASSETS)
    if len(ents) == len(stems) and [e.get("id") for e in ents] != [f"flora.{s}" for s in stems]:
        errs.append("manifest: entries are not in the generator's spec order")
    if len(ents) != len(stems):
        errs.append(f"manifest: {len(ents)} entries, the generator has {len(stems)}")
    if doc.get("generator") != F.GENERATOR:
        errs.append(f"manifest: generator {doc.get('generator')!r}")
    counts: dict[str, int] = {}
    for e in ents:
        counts[e.get("class")] = counts.get(e.get("class"), 0) + 1
        stem = str(e.get("id", "")).split(".", 1)[-1]
        spec = F.ASSETS.get(stem)
        if spec is None:
            errs.append(f"manifest: {e.get('id')} is not in the generator")
            continue
        if (e.get("class"), e.get("layer"), tuple(e.get("ground", ())), e.get("weight"), tuple(e.get("scale", ()))) != \
                (spec.cls, spec.layer, tuple(spec.ground), round(spec.weight, 3), tuple(round(v, 3) for v in spec.scale)):
            errs.append(f"{stem}: manifest class/layer/ground/weight/scale differ from the generator's spec")
        m = measured.get(stem)
        if m:
            for key, mk in (("radius", "radius"), ("footprint", "footprint"), ("height", "height")):
                if abs(e.get(key, -1) - m[mk]) > GATES["bounds_tol"]:
                    errs.append(f"{stem}: manifest {key} {e.get(key)} but the geometry measures {m[mk]:.4f}")
    if counts != EXPECTED_COUNTS:
        errs.append(f"manifest: class counts {counts}, expected {EXPECTED_COUNTS}")
    return errs


def total_errors(total: int) -> list[str]:
    cap = K.FAMILY_BYTES_MAX["flora"]
    return [f"family: {total} bytes of GLB over the {cap} budget"] if total > cap else []


# synthetic negatives: each must fail its gate -------------------------------------------------------------
def self_test() -> list[str]:
    errs = []
    spec = F.ASSETS["tree_dead_spruce"]
    cov = F.ASSETS["grass_tuft_dry_01"]
    base = {"rect_quad": 0.05, "rect_lowfreq": 0.2, "azimuth": 0.02, "sat_p95": 0.2, "forbidden_hue": 0.0, "chrome": 0.0, "v_p99": 0.3, "mean_v": 0.1,
            "tris": 1000, "verts": 1000, "bytes": 1000, "radius": 1.0, "footprint": 0.3, "height": 3.5, "miny": 0.0, "has_disc": True}
    cbase = {**base, "height": 0.25, "has_disc": False, "tris": 300}
    if metric_errors("selftest", base, spec) or metric_errors("selftest", cbase, cov):
        errs.append("self-test: a clean metric set fails its own gates")
    cases = {"rect_quad": 0.9, "rect_lowfreq": 0.99, "azimuth": 0.8, "sat_p95": 0.9, "forbidden_hue": 0.5, "chrome": 0.2, "v_p99": 0.95, "mean_v": 0.7,
             "tris": 10 ** 6, "bytes": 10 ** 9, "miny": 0.2, "footprint": 0.9, "height": 9.0}
    for k, v in cases.items():
        if not metric_errors("selftest", {**base, k: v}, spec):
            errs.append(f"self-test: gate {k} did not fail its negative")
    if not metric_errors("selftest", {**base, "v_p99": 0.01}, spec):
        errs.append("self-test: the black-hole gate did not fail its negative")
    if not metric_errors("selftest", {**base, "height": 2.0}, spec):
        errs.append("self-test: a short tree passed")
    if not metric_errors("selftest", {**base, "has_disc": False}, spec):
        errs.append("self-test: a tree without a disc passed")
    for k, v in (("height", 0.45), ("has_disc", True)):
        if not metric_errors("selftest", {**cbase, k: v}, cov):
            errs.append(f"self-test: cover gate {k} did not fail its negative")
    if not metric_errors("selftest", {**cbase, "height": 0.37}, cov):
        errs.append("self-test: grass over 0.35 wu passed")
    if not metric_errors("selftest", base, K.GroundSpec(spec.build, "tree", "landmark", 256, (1.0, 1.0))):
        errs.append("self-test: a wrong atlas size passed")
    # the key gate against a real baked key
    r = np.random.default_rng(3)
    th = r.uniform(0, np.pi * 2, 20000)
    nx, ny, nz = np.cos(th), np.sin(th), np.zeros_like(th)
    y = 0.2 + 0.02 * r.standard_normal(th.shape)
    if CP.azimuth_leak(CP.with_key([(y, nx, ny, nz)])) <= GATES["azimuth_max"]:
        errs.append("self-test: the azimuth gate did not catch a baked key on a cylinder")
    # the manifest gates
    good = {"schema": K.SCHEMA, "family": "flora", "generator": F.GENERATOR, "assets": []}
    meas = {s: {"radius": 1.0, "footprint": 0.3, "height": 1.0} for s in F.ASSETS}
    e = K.manifest_entry("flora", "tree_dead_spruce", spec, (1.0, 0.3, 1.0))
    good["assets"] = [e]
    if not any("entries" in x for x in manifest_errors(good, meas)):
        errs.append("self-test: a one-entry manifest passed")
    bad = {**good, "assets": [{**e, "radius": 2.0}]}
    if not any("radius" in x for x in manifest_errors(bad, meas)):
        errs.append("self-test: a manifest whose radius disagrees with the geometry passed")
    bad = {**good, "assets": [{**e, "weight": 9.0}]}
    if not any("differ" in x for x in manifest_errors(bad, meas)):
        errs.append("self-test: a manifest whose weight disagrees with the spec passed")
    if not total_errors(K.FAMILY_BYTES_MAX["flora"] + 1) or total_errors(1000):
        errs.append("self-test: the family total gate is wrong")
    return errs


def main() -> int:
    fast, meas = "--fast" in sys.argv, "--measure" in sys.argv
    errs: list[str] = [] if meas else self_test()
    errs += [f"groundkit: {x}" for x in (K.self_test() if not meas else [])]
    only = [x for x in sys.argv[1:] if not x.startswith("--")]
    stems = [s for s in F.ASSETS if not only or s in only]
    workers = 4 if len(stems) > 3 else 1
    print(f"check_ground_flora: building {len(stems)} assets ({workers} workers)")
    first = run(stems, workers)
    if meas:
        for s, (_, m, _) in first.items():
            print(s, " ".join(f"{k} {v:.4f}" if isinstance(v, float) else f"{k} {v}" for k, v in m.items() if k != "bounds_kit"))
        return 0
    measured = {s: m for s, (_, m, _) in first.items()}
    for s, (glb, m, aerrs) in first.items():
        spec = F.ASSETS[s]
        errs += K.glb_errors(s, glb, spec.layer)
        errs += metric_errors(s, m, spec)
        errs += aerrs
        errs += [f"{s}: groundkit bounds {m['bounds_kit']} disagree with the re-measured geometry"] \
            if max(abs(a - b) for a, b in zip(m["bounds_kit"], (m["radius"], m["footprint"], m["height"]))) > 1e-6 else []
        print(f"  {s:24s} {spec.cls:9s} tris {m['tris']:5d} bytes {m['bytes']:7d} h {m['height']:.2f} fp {m['footprint']:.2f} quad {m['rect_quad']:.2f} "
              f"low {m['rect_lowfreq']:.2f} az {m['azimuth']:.2f} (keyed {m['azimuth_keyed']:.2f}) sat {m['sat_p95']:.2f} vp99 {m['v_p99']:.2f}")
        errs += blind_errors(s, m)
    total = sum(m["bytes"] for m in measured.values())
    print(f"  family total {total} bytes of {K.FAMILY_BYTES_MAX['flora']}")
    if not only:
        errs += total_errors(total)
        manifest_path = SHIPPED / "flora.json"
        if not manifest_path.is_file():
            errs.append(f"{manifest_path} is not shipped")
        else:
            doc = json.loads(manifest_path.read_text(encoding="utf-8"))
            errs += manifest_errors(doc, measured)
    for s, (glb, _, _) in first.items():
        c = SHIPPED / "flora" / f"{s}.glb"
        if not c.is_file():
            errs.append(f"{s}: not shipped under {c.parent}")
        elif c.read_bytes() != glb:
            errs.append(f"{s}: the committed file differs from a fresh build (regenerate with gen_ground_flora.py and commit)")
    if not fast:
        second = run(stems, workers)
        same = sum(first[s][0] == second[s][0] for s in stems)
        for s in stems:
            if first[s][0] != second[s][0]:
                errs.append(f"{s}: two builds produced different GLB bytes")
        print(f"  deterministic: {same}/{len(stems)} identical across two builds")
        if not only:
            errs += K.family_errors(F.main, "flora")
            import uiart as U
            errs += U.shipped_image_errors("flora preview sheet", F.preview(), F.PREVIEW_PATH)
    for e in errs:
        print("FAIL", e)
    print(f"ground flora: {len(errs)} problems, {len(stems)} assets checked")
    return 1 if errs else 0


if __name__ == "__main__":
    sys.exit(main())
