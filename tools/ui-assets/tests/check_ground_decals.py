"""Gates for the floor decals: tools/ui-assets/gen_ground_decals.py and what it ships under dream/animations/ground/.

    python tools/ui-assets/tests/check_ground_decals.py [--fast] [--measure]

  --fast     skip the second build (determinism)
  --measure  print every metric for every decal and exit, to calibrate the constants below

Exits nonzero on failure. Every gate has a synthetic negative that must fail it (self_test); do not loosen a constant
to pass, fix the art.

What is gated, per decal:
  * the manifest (groundkit.validate_manifest), the shipped bytes (groundkit.family_errors), determinism (two builds);
  * the container: rigid, one node, one mesh, one primitive, no skin or animation, one alpha-MASK material
    "ground.baked" with cutoff 0.5, nothing emissive, an embedded PNG that is a square of the budgeted size;
  * flat: every vertex at y = 0 exactly, every normal +Y, every triangle facing +Y;
  * budgets: triangles, bytes per decal, bytes for the family, the size across (0.6-1.8 wu, the path up to 2.5);
  * the manifest's radius, footprint and height are what the mesh measures;
  * the texture: alpha is 0 or 255, every outer texel is clear, coverage 25-85%;
  * brightness: the covered texels' mean luma over the mean luma of the ground tiles it may lie on, per class band
    (the tiles are read from dream/animations/library/forge, bytes sampled as linear, as the engine does), moss
    greener and puddle colder than neutral, and the generator's TILE_LUMA matching the tiles on disk;
  * no baked key: the quadrant spread and the low-frequency share of the texture's luma (check_props' gates).
"""
from __future__ import annotations

import io
import json
import struct
import sys
from pathlib import Path

import numpy as np
from PIL import Image

HERE = Path(__file__).resolve().parent
TOOLS = HERE.parent
ROOT = TOOLS.parents[1]
sys.path.insert(0, str(TOOLS))
sys.path.insert(0, str(HERE))
import check_props as CP  # noqa: E402
import gen_ground_decals as GD  # noqa: E402
import groundkit as K  # noqa: E402

FAMILY = "decals"
TILES_DIR = ROOT / "dream" / "animations" / "library" / "forge"
BANDS = {   # class -> (ratio lo, ratio hi) of covered-texel mean luma over the mean luma of the tiles it lies on
    "decal.dirt": (0.75, 1.15), "decal.path": (0.75, 1.15), "decal.moss": (0.60, 1.00), "decal.puddle": (0.35, 0.65),
    "decal.crack": (0.40, 0.70), "decal.litter": (0.60, 1.00), "decal.scorch": (0.25, 0.50), "decal.stain": (0.45, 0.75),
}
COVER_MIN, COVER_MAX = 0.25, 0.85
SIZE_RANGE = (0.6, 1.8)
SIZE_RANGE_PATH = (0.6, 2.5)
TILE_LUMA_TOL = 0.002
BOUNDS_TOL = 0.0015
COUNTS = {"decal.dirt": 4, "decal.moss": 4, "decal.puddle": 3, "decal.crack": 3, "decal.path": 3, "decal.litter": 3,
          "decal.scorch": 2, "decal.stain": 2}
GATES = {"quad_max": CP.GATES["rect_quad_max"], "lowfreq_max": CP.GATES["rect_lowfreq_max"]}


# --------------------------------------------------------------------------------------------------- GLB access
def parse_glb(data: bytes) -> dict:
    """-> dict(doc, pos, nrm, idx, png, rgba). Raises ValueError on anything that is not one rigid primitive."""
    if len(data) < 20 or data[:4] != b"glTF":
        raise ValueError("not a GLB")
    n, kind = struct.unpack_from("<II", data, 12)
    doc = json.loads(data[20:20 + n].decode("utf-8"))
    off = 20 + n
    bn, bkind = struct.unpack_from("<II", data, off)
    blob = data[off + 8: off + 8 + bn]
    comp = {5126: np.float32, 5125: np.uint32, 5123: np.uint16}
    width = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4}

    def accessor(i):
        a = doc["accessors"][i]
        v = doc["bufferViews"][a["bufferView"]]
        dt = np.dtype(comp[a["componentType"]])
        w = width[a["type"]]
        start = v.get("byteOffset", 0) + a.get("byteOffset", 0)
        arr = np.frombuffer(blob, dtype=dt, count=a["count"] * w, offset=start)
        return arr.reshape(a["count"], w) if w > 1 else arr

    if len(doc.get("meshes", [])) != 1 or len(doc["meshes"][0]["primitives"]) != 1:
        raise ValueError("expected exactly one mesh with one primitive")
    prim = doc["meshes"][0]["primitives"][0]
    at = prim["attributes"]
    png = blob[doc["bufferViews"][doc["images"][0]["bufferView"]].get("byteOffset", 0):][: doc["bufferViews"][doc["images"][0]["bufferView"]]["byteLength"]] if doc.get("images") else b""
    rgba = np.asarray(Image.open(io.BytesIO(png)).convert("RGBA")) if png else np.zeros((0, 0, 4), np.uint8)
    return dict(doc=doc, pos=accessor(at["POSITION"]).astype(np.float64), nrm=accessor(at["NORMAL"]).astype(np.float64),
                idx=accessor(prim["indices"]).reshape(-1, 3), png=png, rgba=rgba, prim=prim)


# --------------------------------------------------------------------------------------------------- gates
def container_errors(stem: str, g: dict, size: int) -> list[str]:
    doc, errs = g["doc"], []
    if doc.get("skins") or doc.get("animations"):
        errs.append(f"{stem}: skinned or animated")
    if len(doc.get("nodes", [])) != 1:
        errs.append(f"{stem}: {len(doc.get('nodes', []))} nodes, expected one")
    mats = doc.get("materials", [])
    if len(mats) != 1 or mats[0].get("name") != K.BAKED_MATERIAL:
        errs.append(f"{stem}: materials {[m.get('name') for m in mats]}, expected exactly [{K.BAKED_MATERIAL!r}]")
    else:
        m = mats[0]
        if m.get("alphaMode") != "MASK" or abs(m.get("alphaCutoff", 0.5) - 0.5) > 1e-6:
            errs.append(f"{stem}: material is not alpha-MASK with cutoff 0.5")
        if "emissiveFactor" in m and max(m["emissiveFactor"]) > 0:
            errs.append(f"{stem}: emissive")
        if "baseColorTexture" not in m.get("pbrMetallicRoughness", {}):
            errs.append(f"{stem}: no base colour texture")
    if any("uri" in i for i in doc.get("images", [])) or len(doc.get("images", [])) != 1:
        errs.append(f"{stem}: expected one embedded image")
    if g["rgba"].shape[:2] != (size, size):
        errs.append(f"{stem}: atlas is {g['rgba'].shape[1]}x{g['rgba'].shape[0]}, budget says {size}x{size}")
    return errs


def flat_errors(stem: str, g: dict) -> list[str]:
    errs = []
    if np.any(g["pos"][:, 1] != 0.0):
        errs.append(f"{stem}: a vertex is off y = 0 (max |y| {np.abs(g['pos'][:, 1]).max():.6f}); a decal is flat at exactly 0")
    if np.any(np.abs(g["nrm"] - np.array([0.0, 1.0, 0.0])) > 1e-6):
        errs.append(f"{stem}: a normal is not +Y")
    p = g["pos"][g["idx"]]
    face = np.cross(p[:, 1] - p[:, 0], p[:, 2] - p[:, 0])
    if np.any(face[:, 1] <= 0):
        errs.append(f"{stem}: {int((face[:, 1] <= 0).sum())} triangles face -Y or are degenerate")
    return errs


def budget_errors(stem: str, cls: str, g: dict, glb_len: int) -> list[str]:
    b = K.BUDGETS[cls]
    errs = []
    tris = len(g["idx"])
    if tris > b["tris_max"]:
        errs.append(f"{stem}: {tris} triangles over the {b['tris_max']} budget")
    if glb_len > b["bytes_max"]:
        errs.append(f"{stem}: {glb_len} bytes over the {b['bytes_max']} budget")
    return errs


def bounds_errors(stem: str, entry: dict, g: dict) -> list[str]:
    r = float(np.hypot(g["pos"][:, 0], g["pos"][:, 2]).max())
    fp = float(np.abs(g["pos"][:, [0, 2]]).max())
    h = float(g["pos"][:, 1].max())
    errs = []
    for key, v in (("radius", r), ("footprint", fp), ("height", h)):
        if abs(entry[key] - v) > BOUNDS_TOL:
            errs.append(f"{stem}: manifest {key} {entry[key]} but the mesh measures {v:.4f}")
    return errs


def size_errors(stem: str, entry: dict) -> list[str]:
    lo, hi = SIZE_RANGE_PATH if entry["class"] == "decal.path" else SIZE_RANGE
    a, b = 2 * entry["radius"] * entry["scale"][0], 2 * entry["radius"] * entry["scale"][1]
    if a < lo - 1e-3 or b > hi + 1e-3:
        return [f"{stem}: spans {a:.2f}-{b:.2f} wu across at its scale range, allowed {lo}-{hi}"]
    return []


def texture_errors(stem: str, rgba: np.ndarray) -> list[str]:
    errs = []
    a = rgba[..., 3]
    if np.any((a != 0) & (a != 255)):
        errs.append(f"{stem}: alpha is not 0 or 255 ({int(((a != 0) & (a != 255)).sum())} partial texels)")
    ring = np.concatenate([a[0], a[-1], a[:, 0], a[:, -1]])
    if ring.any():
        errs.append(f"{stem}: {int((ring > 0).sum())} edge texels are not clear")
    cov = float((a > 0).mean())
    if not COVER_MIN <= cov <= COVER_MAX:
        errs.append(f"{stem}: coverage {cov:.2f} outside {COVER_MIN}-{COVER_MAX}")
    return errs


def tile_luma_measured() -> dict[str, float]:
    out = {}
    for d in sorted(TILES_DIR.iterdir()):
        im = np.asarray(Image.open(d / f"{d.name}.albedo.png").convert("RGB"), np.float64) / 255.0
        out[d.name] = float((im @ np.array([0.2126, 0.7152, 0.0722])).mean())
    return out


def tile_errors(tiles: dict[str, float]) -> list[str]:
    errs = []
    for name, v in GD.TILE_LUMA.items():
        if name not in tiles or abs(tiles[name] - v) > TILE_LUMA_TOL:
            errs.append(f"gen_ground_decals.TILE_LUMA[{name!r}] = {v} but the tile on disk measures {tiles.get(name, float('nan')):.4f}")
    return errs


def ratio_of(rgba: np.ndarray, ground: list[str], tiles: dict[str, float]) -> float:
    m = rgba[..., 3] > 0
    c = rgba[..., :3][m].astype(np.float64) / 255.0
    lum = float((c @ np.array([0.2126, 0.7152, 0.0722])).mean())
    names = [GD.TILE_OF[x] for x in ground if x != "snow"]
    return lum / float(np.mean([tiles[n] for n in names]))


def brightness_errors(stem: str, entry: dict, rgba: np.ndarray, tiles: dict[str, float]) -> list[str]:
    errs = []
    lo, hi = BANDS[entry["class"]]
    ratio = ratio_of(rgba, entry["ground"], tiles)
    if not lo <= ratio <= hi:
        errs.append(f"{stem}: brightness ratio {ratio:.2f} vs its ground outside the {entry['class']} band {lo}-{hi}")
    mean = rgba[..., :3][rgba[..., 3] > 0].astype(np.float64).mean(axis=0)
    if entry["class"] == "decal.moss" and not (mean[1] > mean[0] and mean[1] > mean[2]):
        errs.append(f"{stem}: moss is not green (mean rgb {mean.round(1)})")
    if entry["class"] == "decal.puddle" and not mean[2] > mean[0]:
        errs.append(f"{stem}: puddle is not cold (mean rgb {mean.round(1)})")
    return errs


def key_metrics(rgba: np.ndarray) -> tuple[float, float]:
    y = CP.luma(rgba[..., :3])
    return CP.quad_spread(y), CP.lowfreq_share(y)


def key_errors(stem: str, rgba: np.ndarray) -> list[str]:
    q, lf = key_metrics(rgba)
    errs = []
    if q > GATES["quad_max"]:
        errs.append(f"{stem}: quadrant luma spread {q:.2f} over {GATES['quad_max']}: a baked key light")
    if lf > GATES["lowfreq_max"]:
        errs.append(f"{stem}: low-frequency luma share {lf:.2f} over {GATES['lowfreq_max']}: a baked key light")
    return errs


# --------------------------------------------------------------------------------------------------- self test
def self_test(good: dict, tiles: dict[str, float]) -> list[str]:
    """Each gate must fail a synthetic violation, so none of them can stop measuring without this saying so."""
    errs = []

    def must_fail(label, found):
        if not found:
            errs.append(f"self-test: {label} passed the gate that should reject it")

    base = good["rgba"].copy()
    entry = dict(good["entry"])
    stem = "decal_dirt_01"
    if (e := texture_errors(stem, base) + flat_errors(stem, good) + brightness_errors(stem, entry, base, tiles)
            + key_errors(stem, base) + container_errors(stem, good, 128) + bounds_errors(stem, entry, good)):
        errs.append(f"self-test: the reference decal does not pass its own gates: {e[:2]}")

    t = base.copy(); t[0, 5, 3] = 255
    must_fail("a lit edge texel", texture_errors(stem, t))
    t = base.copy(); t[64, 64, 3] = 128
    must_fail("a half-alpha texel", texture_errors(stem, t))
    t = base.copy(); t[..., 3] = 0; t[60:64, 60:64, 3] = 255
    must_fail("4% coverage", texture_errors(stem, t))
    t = base.copy(); t[2:-2, 2:-2, 3] = 255
    must_fail("full coverage", texture_errors(stem, t))
    t = base.copy(); t[..., :3] = np.clip(t[..., :3].astype(int) * 2, 0, 255).astype(np.uint8)
    must_fail("a decal twice as bright as its ground", brightness_errors(stem, entry, t, tiles))
    t = base.copy(); t[..., :3] = (t[..., :3] * 0.3).astype(np.uint8)
    must_fail("a decal far darker than its ground", brightness_errors(stem, entry, t, tiles))
    moss = dict(entry, **{"class": "decal.moss"}); grey = base.copy(); grey[..., :3] = grey[..., 0:1]
    must_fail("grey moss", [e for e in brightness_errors(stem, moss, grey, tiles) if "green" in e])
    pud = dict(entry, **{"class": "decal.puddle"}); warm = base.copy(); warm[..., 0] = 200; warm[..., 2] = 20
    must_fail("a warm puddle", [e for e in brightness_errors(stem, pud, warm, tiles) if "cold" in e])
    ramp = np.linspace(0.35, 1.0, 128, dtype=np.float32)[None, :, None]          # a baked key from the left
    t = base.copy(); t[..., :3] = np.clip(t[..., :3] * ramp, 0, 255).astype(np.uint8)
    t[..., 3] = 255
    must_fail("a luma ramp across the texture", key_errors(stem, t))
    bad = dict(good, pos=good["pos"].copy()); bad["pos"][3, 1] = 0.01
    must_fail("a vertex raised off y = 0", flat_errors(stem, bad))
    bad = dict(good, nrm=good["nrm"].copy()); bad["nrm"][2] = (0.0, 0.0, 1.0)
    must_fail("a sideways normal", flat_errors(stem, bad))
    bad = dict(good, idx=good["idx"][:, ::-1].copy())
    must_fail("a downward-facing fan", flat_errors(stem, bad))
    bad = dict(good, idx=np.tile(good["idx"], (5, 1)))
    must_fail("a fan over the triangle budget", budget_errors(stem, "decal.dirt", bad, 1000))
    must_fail("a GLB over the byte budget", budget_errors(stem, "decal.dirt", good, 60_000))
    must_fail("a lying manifest radius", bounds_errors(stem, dict(entry, radius=entry["radius"] + 0.1), good))
    must_fail("a decal that is too big", size_errors(stem, dict(entry, radius=1.2)))
    must_fail("the wrong atlas size", container_errors(stem, good, 256))
    doc = json.loads(json.dumps(good["doc"])); doc["materials"][0]["emissiveFactor"] = [1, 0, 0]
    must_fail("an emissive decal", container_errors(stem, dict(good, doc=doc), 128))
    doc = json.loads(json.dumps(good["doc"])); doc["materials"][0]["alphaMode"] = "BLEND"
    must_fail("a BLEND decal", container_errors(stem, dict(good, doc=doc), 128))
    must_fail("a drifted tile table", tile_errors({**tiles, "dirt": tiles["dirt"] + 0.05}))
    return errs


# --------------------------------------------------------------------------------------------------- main
def build_all() -> dict:
    return {stem: GD.build_decal(stem) for stem in GD.DECALS}


def main() -> int:
    fast, meas = "--fast" in sys.argv, "--measure" in sys.argv
    tiles = tile_luma_measured()
    built = build_all()
    errs: list[str] = []
    entries = {stem: K.manifest_entry(FAMILY, stem, GD.ASSETS[stem], a["bounds"]) for stem, a in built.items()}
    parsed = {}
    for stem, a in built.items():
        g = parse_glb(a["glb"])
        g["entry"] = entries[stem]
        parsed[stem] = g
    if meas:
        for stem, g in parsed.items():
            q, lf = key_metrics(g["rgba"])
            print(f"{stem:16s} cover {float((g['rgba'][..., 3] > 0).mean()):.2f} ratio {ratio_of(g['rgba'], g['entry']['ground'], tiles):.2f} "
                  f"quad {q:.2f} low {lf:.2f} bytes {len(built[stem]['glb'])}")
        return 0
    errs += self_test(parsed["decal_dirt_01"], tiles)
    errs += tile_errors(tiles)
    counts: dict[str, int] = {}
    total = 0
    for stem, g in parsed.items():
        e = g["entry"]
        glb = built[stem]["glb"]
        total += len(glb)
        counts[e["class"]] = counts.get(e["class"], 0) + 1
        size = K.BUDGETS[e["class"]]["atlas"]
        errs += container_errors(stem, g, size) + flat_errors(stem, g) + budget_errors(stem, e["class"], g, len(glb))
        errs += bounds_errors(stem, e, g) + size_errors(stem, e) + texture_errors(stem, g["rgba"])
        errs += brightness_errors(stem, e, g["rgba"], tiles) + key_errors(stem, g["rgba"])
        if e["layer"] != "decal":
            errs.append(f"{stem}: layer {e['layer']!r}, a decal is always the decal layer")
        q, lf = key_metrics(g["rgba"])
        print(f"  {stem:16s} cover {float((g['rgba'][..., 3] > 0).mean()):.2f} ratio {ratio_of(g['rgba'], e['ground'], tiles):.2f} "
              f"quad {q:.2f} low {lf:.2f} bytes {len(glb):6d}")
    if counts != COUNTS or len(parsed) != 24:
        errs.append(f"the set is {counts}, expected 24 decals as {COUNTS}")
    if total > K.FAMILY_BYTES_MAX[FAMILY]:
        errs.append(f"family is {total} bytes over the {K.FAMILY_BYTES_MAX[FAMILY]} budget")
    print(f"  family {total} bytes of {K.FAMILY_BYTES_MAX[FAMILY]}")
    doc = json.loads((K.GROUND_ROOT / f"{FAMILY}.json").read_text(encoding="utf-8")) if (K.GROUND_ROOT / f"{FAMILY}.json").is_file() else {}
    if not doc:
        errs.append(f"{FAMILY}.json is not committed under {K.GROUND_ROOT}")
    else:
        errs += [f"manifest: {m}" for m in K.validate_manifest(doc)]
        if [a["id"] for a in doc.get("assets", [])] != [f"{FAMILY}.{s}" for s in GD.DECALS]:
            errs.append("manifest entries are not the generator's stems in spec order")
        if doc.get("assets") != list(entries.values()):
            errs.append("manifest entries differ from what the generator computes")
    errs += K.family_errors(GD.main, FAMILY)
    if not fast:
        again = build_all()
        same = sum(again[s]["glb"] == built[s]["glb"] for s in built)
        for s in built:
            if again[s]["glb"] != built[s]["glb"]:
                errs.append(f"{s}: two builds produced different GLB bytes")
        print(f"  deterministic: {same}/{len(built)} identical across two builds")
    for e in errs:
        print("FAIL", e)
    print(f"ground decals: {len(errs)} problems, {len(parsed)} decals checked")
    return 1 if errs else 0


if __name__ == "__main__":
    sys.exit(main())
