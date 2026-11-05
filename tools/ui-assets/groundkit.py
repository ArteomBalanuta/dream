"""Shared kit for the ground assets (flora, stone, decals): specs, the bake, the manifest and its gates.

    python tools/ui-assets/groundkit.py        # self-tests

A family generator (gen_ground_<family>.py) declares ASSETS: dict[stem, GroundSpec] and a main(out_root, only) that
calls build_ground_asset / glb_bytes / manifest_entry per stem and write_family once. Output:

    <out_root>/<family>.json            the manifest (schema dream.ground/1), LF, indent 2, floats to 3 decimals
    <out_root>/<family>/<stem>.glb      one rigid, one-node GLB per asset

A GLB is authored in u (16 u = 1 m, z up, front +y) like gen_props, exported at WORLD_UNITS_PER_U, Y up, +Z front,
centred on the origin and standing on y = 0. Materials: "ground.baked" (the atlas, double sided) and, for accent and
landmark assets with a disc, "ground.contact" (alpha-MASK disc). Nothing is emissive. Generators read no files.
"""
from __future__ import annotations

import importlib
import json
import re
import struct
import sys
import tempfile
from dataclasses import dataclass
from pathlib import Path
from typing import Callable

import numpy as np

HERE = Path(__file__).resolve().parent
if str(HERE) not in sys.path:
    sys.path.insert(0, str(HERE))

import charforge as C  # noqa: E402
import gen_char_player as P  # noqa: E402
import gltfwrite as G  # noqa: E402

GP = importlib.import_module("gen_props")   # by name: check_sources.py allows tools to import only a fixed set of siblings

GROUND_ROOT = HERE.parents[1] / "dream" / "animations" / "ground"
SCHEMA = "dream.ground/1"
LAYERS = ("decal", "cover", "accent", "landmark")
_CLASS_LAYERS: dict[str, tuple[str, ...]] = {
    "tree": ("landmark",), "sapling": ("accent",), "bush": ("accent",), "grass": ("cover",), "flower": ("cover",),
    "mushroom": ("cover",), "root": ("cover",), "rock": ("accent", "cover"), "pebbles": ("cover",), "stump": ("accent",),
    "log": ("accent",), "twigs": ("cover",), "leaves": ("cover",), "bones": ("cover",), "item": ("cover",),
}
CLASSES: dict[str, set[str]] = {k: set(v) for k, v in _CLASS_LAYERS.items()}
for _d in ("dirt", "moss", "puddle", "crack", "path", "litter", "scorch", "stain"):
    CLASSES["decal." + _d] = {"decal"}
# lower-case GeometryMaterialId names (Lamp is player-placed and never a ground)
MATERIALS: tuple[str, ...] = ("grass", "dirt", "stone", "sand", "mud", "snow", "rock", "grass_path")
FAMILIES = ("flora", "stone", "decals")
BAKED_MATERIAL = "ground.baked"
CONTACT_MATERIAL = "ground.contact"
PNG_MAGIC = bytes((0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A))
FAMILY_BYTES_MAX = {"flora": 5_000_000, "stone": 2_500_000, "decals": 1_200_000}


def _b(tris: int, nbytes: int, atlas: int) -> dict:
    return dict(tris_max=tris, bytes_max=nbytes, atlas=atlas)


def _cover(t: int) -> dict:
    return _b(t, 40_000, 128)


def _accent(t: int) -> dict:
    return _b(t, 140_000, 256)


BUDGETS: dict[str, dict] = {
    "grass": _b(400, 44_000, 128),              # was _cover(400) = 40_000: blade grain is PNG entropy; grass_tuft_tall_01 measured 41_460
    "pebbles": _cover(400), "twigs": _cover(400), "bones": _cover(400),
    "leaves": _b(400, 44_000, 128),             # was _cover(400) = 40_000: leaf_heap_02 measured 40_568 with the clump breakup
    "flower": _cover(600), "item": _cover(600), "mushroom": _cover(1200),
    "root": _cover(3000),                       # a cover-layer asset with the 3000-triangle root budget
    "bush": _accent(4000), "sapling": _accent(4000), "stump": _accent(3000), "log": _accent(3000),
    "rock": _b(2500, 160_000, 256),             # was _accent(2500) = 140_000: facet and grain detail is PNG entropy; boulder_03 measured 147_076
    "tree": _b(12000, 560_000, 512),   # was 450_000: the bark and leaf breakup is PNG entropy; the largest tree (elm) measured 487_336
}
for _k in CLASSES:
    if _k.startswith("decal."):
        BUDGETS[_k] = _b(64, 96_000 if _k == "decal.path" else 48_000, 256 if _k == "decal.path" else 128)
# a rock may be cover (scree): the cover bytes and atlas apply, the triangle budget stays the rock's
LAYER_BUDGET_OVERRIDES = {("rock", "cover"): _b(2500, 40_000, 128)}


def budget_for(cls: str, layer: str) -> dict:
    return LAYER_BUDGET_OVERRIDES.get((cls, layer), BUDGETS[cls])


@dataclass(frozen=True)
class GroundSpec:
    build: Callable[[], list]            # () -> list[gen_props.Part], in u, z up, standing on z = 0, centred
    cls: str
    layer: str
    atlas: int = 128
    disc: tuple = (0.0, 0.0)             # contact disc half-extents in u; (0, 0) = none (accent / landmark only)
    weld: bool = False
    age: tuple = (0.40, 0.60, 0.0, 0.12)  # wear, grime, rust, dust
    ground: tuple = ("grass", "dirt", "mud")
    weight: float = 1.0
    scale: tuple = (0.9, 1.1)


# ---------------------------------------------------------------------------------------------- albedo
# The engine samples texel bytes as linear (no sRGB internal format) and its terrain tiles are authored in that same
# "sRGB-looking bytes read as linear" convention (check_ground_decals measures them so). A ground asset must sit beside
# that terrain, so its baked sRGB atlas is used AS IS (times GROUND_ALBEDO_GAIN), not decoded to true linear and lifted
# like an actor's (gen_char_player.encode_atlas): that path lands at about 0.06, a sixth of the floor, and every bush,
# twig and slab rendered as a black cutout in the captures.
GROUND_ALBEDO_GAIN = 1.5
GROUND_DISC_FACTOR = 0.11   # the contact disc (a dirt shadow, texture bytes span 0..1 of this); props use GP.DISC_LIFT = 0.05 in true linear


def encode_ground_atlas(atlas_rgba: np.ndarray, gain: float = GROUND_ALBEDO_GAIN):
    """Baked sRGB atlas -> (PNG bytes, baseColorFactor scale, encoded RGBA). Same container as P.encode_atlas: the bytes
    span 0..1 of `scale`, which goes in baseColorFactor, so the shader sees atlas / 255 * gain."""
    import io

    from PIL import Image

    cov = atlas_rgba[..., 3] > 0
    eff = atlas_rgba[..., :3].astype(np.float64) / 255.0 * gain
    top = float(eff[cov].max()) if cov.any() else 1.0
    scale = min(1.0, max(0.005, np.ceil(top * 200.0) / 200.0))
    enc = np.zeros(atlas_rgba.shape, dtype=np.uint8)
    enc[..., :3] = np.round(np.clip(eff / scale, 0.0, 1.0) * 255.0).astype(np.uint8)
    enc[..., 3] = 255
    buf = io.BytesIO()
    Image.fromarray(enc, "RGBA").save(buf, format="PNG", compress_level=9)
    return buf.getvalue(), float(scale), enc


# Thin, double-sided geometry (blades, leaf cards, twig and branch sheets) has no meaningful "outside": about half of its
# vertex normals point at the ground, where the hemisphere ambient is darkest and the sun contributes nothing, so a heap of
# leaves or a bush rendered black from above. The world shaders have no gl_FrontFacing handling, so the fix is in the
# data: a normal that points down is flipped, and every normal leans toward +Y, which is how these read from the camera.
SHEET_CLASSES = frozenset({"grass", "flower", "bush", "sapling", "leaves", "twigs", "root", "tree"})
SHEET_UP_LEAN = 0.5


def sheet_normals(nrm: np.ndarray, lean: float = SHEET_UP_LEAN) -> np.ndarray:
    n = np.asarray(nrm, dtype=np.float64).copy()
    n[n[:, 1] < 0.0] *= -1.0
    n[:, 1] += lean
    n /= np.maximum(np.linalg.norm(n, axis=1, keepdims=True), 1e-9)
    return n.astype(np.asarray(nrm).dtype)


# ---------------------------------------------------------------------------------------------- detail
# The baked atlases are ALBEDO: the final pixel is (ambient + lights) * albedo with no tonemap and a clamp, and the gain
# reaches about 2.17 beside a brazier, so a channel over ~0.40 clips to a flat patch (WeaponMeshCatalog.cs:101-106). Detail
# is therefore added by MODULATING DOWNWARD ONLY: what the bake produced is the per-class ceiling and the field is a
# multiplier in [1 - d, 1]. And a texel's EFFECTIVE albedo is byte / 255 * baseColorFactor, not byte / 255.
DETAIL_LEVELS = 3   # the field takes this many values: every extra level of noise is bytes of PNG the family budgets pay for
ALBEDO_CEILING = 0.41   # what the bake is capped to, before encoding; the gate (check_props albedo_clip_max) is 0.42
DETAIL_D = {"bark": 0.26, "leafmass": 0.22, "blades": 0.30, "stone": 0.24, "thatch": 0.18, "mud": 0.16, "grain": 0.20, "cap": 0.20}
# kind per ground class (GroundAsset.cs enum); a cylindrical rectangle (a trunk, a stem, a branch) is bark / blades instead
_CLASS_KIND = {"tree": "leafmass", "sapling": "bark", "bush": "leafmass", "grass": "blades", "flower": "blades", "mushroom": "cap",
               "root": "bark", "rock": "stone", "pebbles": "stone", "stump": "bark", "log": "bark", "twigs": "bark",
               "leaves": "leafmass", "bones": "stone", "item": "grain"}
_CYL_KIND = {"tree": "bark", "bush": "bark", "flower": "blades", "mushroom": "cap"}
# kind per world prop; "cottage" is the reference of what good looks like and is left alone
PROP_KIND = {"rust_barrel": "bark", "dead_oak": "bark", "bramble": "bark", "slate_rocks": "stone", "tombstone": "stone",
             "bone_pile": "stone", "rune_flask": "stone", "brazier": "stone", "well": "stone", "cart": "grain", "fence": "grain",
             "fence_broken": "grain", "crates": "grain", "sacks": "thatch", "woodpile": "bark", "signpost": "grain",
             "scarecrow": "thatch", "inn": "mud", "hovel": "mud", "ruined_wall": "stone", "standing_stones": "stone"}


# props whose atlas is mostly one flat, saturated or glazed material need a deeper field to read (measured: 5.6 - 5.8 / 255 at 1.0)
PROP_STRENGTH = {"rust_barrel": 1.6, "bramble": 1.6, "rune_flask": 1.6}




CLASS_STRENGTH: dict[str, float] = {"grass": 0.8}   # per class multiplier on the detail depth: grass sits at its 40_000 byte budget
ASSET_STRENGTH: dict[str, float] = {"grass_wiry_01": 0.5, "flower_hemlock_umbel": 0.6}   # wiry: azimuth ceiling .41, a synthetic key must still read above it (grain dilutes the key); umbel: forbidden-hue share .0018 at 1.0, .0007 at .6


def detail_kind(cls: str, cyl: bool) -> str | None:
    if cls.startswith("decal."):
        return None
    return _CYL_KIND.get(cls, _CLASS_KIND[cls]) if cyl else _CLASS_KIND[cls]


def _seed_rng(*parts) -> np.random.Generator:
    import hashlib
    h = hashlib.sha256(".".join(str(p) for p in parts).encode("utf-8")).digest()
    return np.random.default_rng(int.from_bytes(h[:8], "little"))


def _smooth(t):
    return t * t * (3.0 - 2.0 * t)


def _vnoise(h: int, w: int, cy: float, cx: float, rng, wrap: bool = False) -> np.ndarray:
    """Smooth value noise in [0, 1] with feature size cy x cx pixels; wrap: periodic across the width (a cylinder)."""
    ny = int(np.ceil(h / cy)) + 2
    nx = max(2, int(round(w / cx))) if wrap else int(np.ceil(w / cx)) + 2
    g = rng.random((ny, nx))
    ys = np.arange(h) / cy
    xs = np.arange(w) / ((w / nx) if wrap else cx)
    iy, ix = np.floor(ys).astype(int), np.floor(xs).astype(int)
    fy, fx = _smooth(ys - iy)[:, None], _smooth(xs - ix)[None, :]
    ix1 = (ix + 1) % nx if wrap else ix + 1
    ix = ix % nx if wrap else ix
    a, b = g[iy][:, ix], g[iy][:, ix1]
    c, d = g[iy + 1][:, ix], g[iy + 1][:, ix1]
    return ((a * (1 - fx) + b * fx) * (1 - fy) + (c * (1 - fx) + d * fx) * fy).astype(np.float32)


def _worley(h: int, w: int, cell: float, rng, wrap: bool = False):
    """Jittered-grid cellular noise: (F1, F2 in pixels, a random value per nearest cell in [0, 1])."""
    ny = int(np.ceil(h / cell)) + 2
    nx = max(2, int(round(w / cell))) if wrap else int(np.ceil(w / cell)) + 2
    cw = (w / nx) if wrap else cell
    jy, jx, val = rng.random((ny, nx)), rng.random((ny, nx)), rng.random((ny, nx))
    ys, xs = np.arange(h, dtype=np.float32) + 0.5, np.arange(w, dtype=np.float32) + 0.5
    cy_i = np.floor(ys / cell).astype(int)[:, None]
    cx_i = np.floor(xs / cw).astype(int)[None, :]
    f1 = np.full((h, w), 1e9, np.float32)
    f2 = np.full((h, w), 1e9, np.float32)
    v = np.zeros((h, w), np.float32)
    for oy in (-1, 0, 1):
        for ox in (-1, 0, 1):
            gy = np.broadcast_to(np.clip(cy_i + oy + 1, 0, ny - 1), (h, w))
            gx = np.broadcast_to((cx_i + ox) % nx if wrap else np.clip(cx_i + ox + 1, 0, nx - 1), (h, w))
            py = (cy_i + oy + jy[gy, gx]) * cell
            px = (cx_i + ox + jx[gy, gx]) * cw
            dist = np.hypot(ys[:, None] - py, xs[None, :] - px).astype(np.float32)
            closer = dist < f1
            f2 = np.where(closer, f1, np.minimum(f2, dist))
            v = np.where(closer, val[gy, gx], v)
            f1 = np.where(closer, dist, f1)
    return f1, f2, v


def _norm01(x: np.ndarray) -> np.ndarray:
    lo, hi = np.percentile(x, 2), np.percentile(x, 98)
    return np.clip((x - lo) / max(float(hi - lo), 1e-6), 0.0, 1.0).astype(np.float32)


def _speckle(h: int, w: int, rng) -> np.ndarray:
    n = rng.random((h, w)).astype(np.float32)
    return (n + 0.5 * (np.roll(n, 1, 0) + np.roll(n, 1, 1))) / 2.0   # 1-2 px grain


def detail(kind: str, shape: tuple, rng, cyl: bool = False, strength: float = 1.0) -> np.ndarray:
    """A multiplicative field in [1 - d, 1] (d = DETAIL_D[kind] * strength, at most 0.5) of shape (h, w), a pure function of the rng.
    Rows run along the part (v), columns around it (u; wraps when cyl)."""
    h, w = shape
    d = min(DETAIL_D[kind] * strength, 0.5)
    if kind == "bark":
        # vertical ridges about 2 px apart whose lines wander with a low-frequency warp, and sparse furrows
        warp = (_vnoise(h, w, 14.0, 9.0, rng, cyl) - 0.5) * 5.0
        cols = np.arange(w, dtype=np.float32)[None, :] + warp
        ridge = 0.5 + 0.5 * np.sin(cols * (np.pi * 2.0 / 2.3) + _vnoise(h, w, 18.0, 3.0, rng, cyl) * 6.0)
        furrow = _vnoise(h, w, 22.0, 2.0, rng, cyl)
        raw = 0.50 * ridge + 0.35 * (furrow > 0.5) * furrow + 0.15 * _speckle(h, w, rng)
    elif kind == "leafmass":
        f1, f2, v = _worley(h, w, 6.0, rng, cyl)
        seam = np.clip((f2 - f1) / 2.2, 0.0, 1.0)          # dark seams between clumps
        raw = 0.45 * seam + 0.40 * v + 0.15 * _speckle(h, w, rng)
    elif kind == "blades":
        # per-blade value jitter: narrow columns, long along the blade, each with its own level
        jit = _vnoise(h, w, 5.0, 1.6, rng, cyl)
        lvl = rng.random(max(2, int(np.ceil(w / 2.0))))[np.arange(w) // 2][None, :]
        # mostly per-texel grain: smooth fields on a thin stem or blade correlate with the way each side faces by chance (azimuth gate)
        raw = 0.25 * jit + 0.20 * lvl + 0.55 * _speckle(h, w, rng)
        if cyl:   # a stem: only structure that is constant around it (along its length) or independent per texel, never per column
            raw = 0.15 * _vnoise(h, w, 3.0, 1e6, rng) + 0.85 * _speckle(h, w, rng)
    elif kind == "stone":
        f1, f2, v = _worley(h, w, 11.0, rng, cyl)
        crack = np.clip((f2 - f1) / 1.6, 0.0, 1.0)         # fractured planar facets, each a flat tone
        raw = 0.25 * v + 0.20 * crack + 0.55 * _speckle(h, w, rng)
    elif kind == "thatch":
        streak = _vnoise(h, w, 20.0, 1.3, rng, cyl)
        raw = 0.60 * streak + 0.25 * _vnoise(h, w, 4.0, 9.0, rng, cyl) + 0.15 * _speckle(h, w, rng)
    elif kind == "mud":
        raw = 0.50 * _vnoise(h, w, 6.0, 6.0, rng, cyl) + 0.25 * _vnoise(h, w, 2.5, 2.5, rng, cyl) + 0.25 * _speckle(h, w, rng)
    elif kind == "grain":
        seam = (np.arange(w)[None, :] % 11 != 0) * 1.0     # plank joints every 11 px
        raw = 0.45 * _vnoise(h, w, 26.0, 1.8, rng, cyl) + 0.30 * seam + 0.25 * _speckle(h, w, rng)
    elif kind == "cap":
        f1, f2, v = _worley(h, w, 4.0, rng, cyl)
        raw = 0.35 * np.clip((f2 - f1) / 1.4, 0.0, 1.0) + 0.30 * v + 0.35 * _speckle(h, w, rng)
    else:
        raise KeyError(kind)
    raw = np.broadcast_to(raw, (h, w))
    return (1.0 - d * (1.0 - np.round(_norm01(raw) * (DETAIL_LEVELS - 1)) / (DETAIL_LEVELS - 1))).astype(np.float32)


def _lichen(shape: tuple, rng, cyl: bool) -> np.ndarray:
    """Sparse blotches 6-10 px wide, in [0, 1]: where bark greys out."""
    h, w = shape
    return np.clip((_vnoise(h, w, 8.0, 8.0, rng, cyl) - 0.62) * 6.0, 0.0, 1.0).astype(np.float32)


def apply_detail(atlas: np.ndarray, rects, kinds, seed: str, strength: float = 1.0) -> np.ndarray:
    """atlas: uint8 RGBA from the bake. kinds: one detail kind (or None) per rectangle. Returns a copy with every rectangle multiplied
    by its field (never brighter) and its gutter re-extended from the new edge. Deterministic in `seed`."""
    out = atlas.copy()
    g = C.GUTTER
    for i, (r, kind) in enumerate(zip(rects, kinds)):
        if kind is None:
            continue
        rng = _seed_rng(seed, i, kind)
        f = detail(kind, (r.h, r.w), rng, r.cyl, strength)
        rgb = out[r.y:r.y + r.h, r.x:r.x + r.w, :3].astype(np.float32) * f[..., None]
        # the pits of a surface collect grey: dark detail pulls the chroma in a little, so the grain does not raise the saturation the
        # gates cap (8-bit rounding of dark texels does it too)
        pit = np.clip((1.0 - f) / max(DETAIL_D[kind] * strength, 1e-6), 0.0, 1.0)[..., None] * 0.35
        luma_ = (rgb @ np.array([0.2126, 0.7152, 0.0722], np.float32))[..., None]
        rgb = rgb * (1 - pit) + luma_ * pit   # a mix with grey keeps the hue; luma <= max, so the brightest channel never rises
        if kind == "bark":   # lichen: desaturate toward the luma, still downward only
            lich = _lichen((r.h, r.w), _seed_rng(seed, i, "lichen"), r.cyl)[..., None] * 0.6
            grey = np.minimum(rgb, (rgb @ np.array([0.2126, 0.7152, 0.0722], np.float32))[..., None])
            rgb = rgb * (1 - lich) + grey * lich
        out[r.y:r.y + r.h, r.x:r.x + r.w, :3] = np.round(rgb).astype(np.uint8)
        pad = np.pad(out[r.y:r.y + r.h, r.x:r.x + r.w, :3], ((g, g), (g, g), (0, 0)), mode="edge")
        out[r.y - g:r.y + r.h + g, r.x - g:r.x + r.w + g, :3] = pad
    return out


CAP_MOVED_EPS = 0.02   # a texel counts as moved by the cap when its brightest channel changes by more than this (effective albedo)


def cap_peak(eff: np.ndarray, covered: np.ndarray, ceiling: float = ALBEDO_CEILING):
    """eff: effective albedo (float, HxWx3). Scale so the 99.5th percentile of the covered texels sits at the ceiling, then clip the
    rare outlier at it. A no-op when the atlas is already under it. Returns (capped, report): the report says how much the cap had to
    do, because a cap that quietly flattens the brightest texels would undo the detail there. precap_albedo_max is the brightest
    effective albedo the bake produced, cap_scale the uniform darkening (1 = untouched) and moved_fraction the share of covered
    texels the cap changed by more than CAP_MOVED_EPS; check_props gates all three on the pre-cap bake."""
    top = float(np.percentile(eff[covered].max(axis=1), 99.5))
    s = min(1.0, ceiling / max(top, 1e-9))
    out = np.minimum(eff * s, ceiling)
    moved = (eff.max(axis=2) - out.max(axis=2))[covered] > CAP_MOVED_EPS
    return out, {"precap_albedo_max": float(eff[covered].max()), "cap_scale": float(s), "moved_fraction": float(moved.mean())}


def cap_ground_atlas(atlas: np.ndarray, gain: float = GROUND_ALBEDO_GAIN):
    """Keep the effective albedo (byte / 255 * gain) of a baked atlas at or under ALBEDO_CEILING. -> (atlas, cap report)."""
    cov = atlas[..., 3] > 0
    eff = atlas[..., :3].astype(np.float64) / 255.0 * gain
    if float(eff[cov].max()) <= ALBEDO_CEILING:
        return atlas, {"precap_albedo_max": float(eff[cov].max()), "cap_scale": 1.0, "moved_fraction": 0.0}
    out = atlas.copy()
    capped, report = cap_peak(eff, cov)
    out[..., :3] = np.round(np.clip(capped / gain, 0.0, 1.0) * 255.0).astype(np.uint8)
    return out, report


# ---------------------------------------------------------------------------------------------- build
def build_ground_asset(family: str, stem: str, spec: GroundSpec) -> dict:
    """gen_props.build_asset for a GroundSpec. Pure: no clock, environment or filesystem."""
    parts = [p for p in spec.build() if p.mesh is not None]
    meshes = [(GP._weld(p.mesh) if spec.weld else p.mesh) for p in parts]
    split, uvs, rects = C.unwrap_split(meshes, spec.atlas)
    wear, grime, rust, dust = spec.age
    seed = f"ground.{family}.{stem}"
    atlas = C.bake_albedo(split, None, None, uvs, rects, spec.atlas, materials=[p.mat for p in parts], seed=seed,
                          wear=wear, grime=grime, rust=rust, dust=dust)
    kinds = [detail_kind(spec.cls, r.cyl) for r in rects]
    cap = None
    if any(kinds):
        atlas, cap = cap_ground_atlas(apply_detail(atlas, rects, kinds, seed, ASSET_STRENGTH.get(stem, CLASS_STRENGTH.get(spec.cls, 1.0))))
    png, scale, enc = encode_ground_atlas(atlas)
    k = GP.WORLD_UNITS_PER_U / GP.UNITS_PER_U
    arrays = []
    for m, uv in zip(split, uvs):
        pos, nrm, uvo, idx = P._render_arrays(m, uv)
        arrays.append((pos * k, nrm, uvo, idx))
    pos, nrm, uvo, idx = GP._merge_arrays(arrays)
    if spec.cls in SHEET_CLASSES:
        nrm = sheet_normals(nrm)
    prims = [G.GltfPrimitive(pos, nrm, uvo, idx, None, None, material=0, node=0)]
    mats = [{"name": BAKED_MATERIAL, "base_color_factor": (scale, scale, scale, 1.0), "base_color_png": png, "metallic": 0.0,
             "roughness": 0.9, "double_sided": True}]
    disc = None
    if spec.disc[0] > 0:
        dpng, disc = GP.contact_texture(seed)
        mats.append({"name": CONTACT_MATERIAL, "base_color_factor": (GROUND_DISC_FACTOR,) * 3 + (1.0,), "base_color_png": dpng,
                     "metallic": 0.0, "roughness": 1.0, "alpha_mode": "MASK", "alpha_cutoff": 0.5})
        dp, dn, du, di = GP.disc_arrays(spec.disc[0], spec.disc[1], 1.0)
        prims.append(G.GltfPrimitive(dp, dn, du, di, None, None, material=1, node=0))
    p64 = pos.astype(np.float64)
    horiz = np.hypot(p64[:, 0], p64[:, 2])
    low = p64[:, 1] < 0.15
    footprint = float(np.abs(p64[low][:, [0, 2]]).max()) if low.any() else 0.0
    bounds = (float(horiz.max()), footprint, float(p64[:, 1].max()))
    nodes = [G.GltfNode(name=stem, parent=None)]
    return dict(name=stem, family=family, nodes=nodes, primitives=prims, materials=mats, atlas=atlas, encoded=enc, scale=scale,
                rects=rects, parts=parts, meshes=split, uvs=uvs, disc=disc, spec=spec, bounds=bounds, cap=cap)


def glb_bytes(asset: dict) -> bytes:
    return G.build_glb(asset["nodes"], asset["primitives"], None, None, [], asset["materials"])


def _r3(x) -> float:
    return round(float(x), 3)


def manifest_entry(family: str, stem: str, spec_or_fields, bounds) -> dict:
    """spec_or_fields: a GroundSpec, or a dict with cls, layer and optionally ground, weight, scale."""
    f = spec_or_fields if isinstance(spec_or_fields, dict) else dict(
        cls=spec_or_fields.cls, layer=spec_or_fields.layer, ground=spec_or_fields.ground, weight=spec_or_fields.weight,
        scale=spec_or_fields.scale)
    radius, footprint, height = bounds
    return {"id": f"{family}.{stem}", "file": f"{family}/{stem}.glb", "class": f["cls"], "layer": f["layer"],
            "radius": _r3(radius), "footprint": _r3(footprint), "height": _r3(height),
            "ground": list(f.get("ground", ("grass", "dirt", "mud"))), "weight": _r3(f.get("weight", 1.0)),
            "scale": [_r3(v) for v in f.get("scale", (0.9, 1.1))]}


def manifest_text(family: str, generator: str, entries: list[dict]) -> str:
    doc = {"schema": SCHEMA, "family": family, "generator": generator, "assets": entries}
    return json.dumps(doc, indent=2, ensure_ascii=False) + "\n"


def write_family(out_root: Path, family: str, entries: list[dict], files: dict[str, bytes],
                 generator: str | None = None, partial: bool = False) -> None:
    """files: stem -> GLB bytes. Writes <out_root>/<family>/<stem>.glb and <out_root>/<family>.json (LF).
    partial=True (a --only run) writes just the given GLBs: the manifest is left untouched and nothing is deleted."""
    out_root = Path(out_root)
    d = out_root / family
    d.mkdir(parents=True, exist_ok=True)
    for stem, data in files.items():
        (d / f"{stem}.glb").write_bytes(data)
    if partial:
        print(f"groundkit: partial run, {family}.json is untouched (a full run rewrites it)")
        return
    gen = generator or f"tools/ui-assets/gen_ground_{family}.py"
    (out_root / f"{family}.json").write_bytes(manifest_text(family, gen, entries).encode("utf-8"))


# ---------------------------------------------------------------------------------------------- validation
def glb_material_names(data: bytes) -> list[str] | None:
    if len(data) < 20 or data[:4] != b"glTF":
        return None
    n, kind = struct.unpack_from("<II", data, 12)
    if kind != 0x4E4F534A:
        return None
    doc = json.loads(data[20:20 + n].decode("utf-8"))
    return [m.get("name", "") for m in doc.get("materials", [])]


def glb_errors(stem: str, data: bytes, layer: str) -> list[str]:
    """The container gates of a ground GLB (pure on the bytes): rigid, one node, embedded PNG, the two material names,
    no BLEND, no emissive; a decal's materials are alpha-MASK, a disc is MASK and only accent / landmark assets carry one."""
    try:
        doc, boff = G._check_container(data)
    except (AssertionError, KeyError, ValueError, struct.error) as e:
        return [f"{stem}: GLB container invalid ({e!r})"]
    errs = []
    if "skins" in doc or doc.get("animations"):
        errs.append(f"{stem}: a ground asset is rigid: no skin, no animation")
    if len(doc.get("nodes", [])) != 1 or doc["nodes"][0].get("name") != stem:
        errs.append(f"{stem}: expected one node named {stem}, got {[n.get('name') for n in doc.get('nodes', [])]}")
    for key in ("buffers", "images"):
        for item in doc.get(key, []):
            if "uri" in item:
                errs.append(f"{stem}: {key} entry uses a URI; everything is embedded")
    for img in doc.get("images", []):
        bv = doc["bufferViews"][img["bufferView"]]
        if img.get("mimeType") != "image/png" or data[boff + 8 + bv["byteOffset"]:][:8] != PNG_MAGIC:
            errs.append(f"{stem}: an image is not an embedded PNG")
    mats = doc.get("materials", [])
    names = [m.get("name", "") for m in mats]
    if any(n not in (BAKED_MATERIAL, CONTACT_MATERIAL) for n in names) or len(set(names)) != len(names):
        errs.append(f"{stem}: materials {names} must be {BAKED_MATERIAL} and optionally {CONTACT_MATERIAL}")
    for m in mats:
        if "baseColorTexture" not in m.get("pbrMetallicRoughness", {}):
            errs.append(f"{stem}: {m.get('name')} has no base colour texture")
        if m.get("alphaMode") == "BLEND":
            errs.append(f"{stem}: {m.get('name')} is BLEND; the world pass has blending off")
        if "emissiveFactor" in m:
            errs.append(f"{stem}: {m.get('name')} is emissive; ground assets are not")
        masked = m.get("alphaMode") == "MASK"
        is_disc = m.get("name") == CONTACT_MATERIAL
        if layer == "decal" and not masked:
            errs.append(f"{stem}: a decal material must be alpha-MASK")
        if is_disc and (not masked or layer in ("decal", "cover")):
            errs.append(f"{stem}: the contact disc must be an alpha-MASK material and only accent / landmark assets carry one")
        if not is_disc and layer != "decal" and masked:
            errs.append(f"{stem}: the baked material of a {layer} asset must be opaque")
    if layer != "decal" and BAKED_MATERIAL not in names:
        errs.append(f"{stem}: no {BAKED_MATERIAL} material")
    return errs


def validate_manifest(doc: dict, root: Path | None = None) -> list[str]:
    root = Path(root) if root else GROUND_ROOT
    errs: list[str] = []
    if doc.get("schema") != SCHEMA:
        errs.append(f"schema is {doc.get('schema')!r}, expected {SCHEMA!r}")
    family = doc.get("family")
    if family not in FAMILIES:
        errs.append(f"family {family!r} is not one of {FAMILIES}")
    assets = doc.get("assets")
    if not isinstance(assets, list) or not assets:
        return errs + ["assets is missing or empty"]
    seen, files = set(), set()
    for a in assets:
        aid = str(a.get("id", "?"))
        stem = aid.split(".", 1)[1] if "." in aid else aid
        if not aid.startswith(f"{family}.") or not re.fullmatch(r"[a-z0-9_]+", stem):
            errs.append(f"{aid}: id must be '{family}.<[a-z0-9_]+>'")
        if aid in seen:
            errs.append(f"{aid}: duplicate id")
        seen.add(aid)
        if a.get("file") != f"{family}/{stem}.glb":
            errs.append(f"{aid}: file {a.get('file')!r} is not '{family}/{stem}.glb'")
        cls, layer = a.get("class"), a.get("layer")
        if cls not in CLASSES:
            errs.append(f"{aid}: unknown class {cls!r}")
        elif layer not in CLASSES[cls]:
            errs.append(f"{aid}: layer {layer!r} not allowed for class {cls!r} (allowed {sorted(CLASSES[cls])})")
        if layer not in LAYERS:
            errs.append(f"{aid}: unknown layer {layer!r}")
        if (layer == "decal") != str(cls).startswith("decal."):
            errs.append(f"{aid}: only decal.* classes may use the decal layer, and they always do")
        for key in ("radius", "footprint", "height", "weight"):
            v = a.get(key)
            if not isinstance(v, (int, float)) or isinstance(v, bool) or not np.isfinite(v) or v < 0 or (key in ("radius", "weight") and v <= 0):
                errs.append(f"{aid}: {key} {v!r} is not a valid number")
        gl = a.get("ground")
        if not gl or any(g not in MATERIALS for g in gl):
            errs.append(f"{aid}: ground {gl!r} must be a non-empty list of {MATERIALS}")
        sc = a.get("scale")
        if not (isinstance(sc, list) and len(sc) == 2 and 0 < sc[0] <= sc[1]):
            errs.append(f"{aid}: scale {sc!r} must be [min, max] with 0 < min <= max")
        p = root / str(a.get("file", ""))
        files.add(p.resolve())
        if not p.is_file():
            errs.append(f"{aid}: {a.get('file')} does not exist under {root}")
            continue
    fam_dir = root / str(family)
    if fam_dir.is_dir():
        for g in sorted(fam_dir.glob("*.glb")):
            if g.resolve() not in files:
                errs.append(f"stale: {family}/{g.name} is not in the manifest")
    return errs


def _tmp_parent() -> str | None:
    """Temp files stay on the repo's drive (the system drive may be full)."""
    d = HERE.parents[1] / "artifacts" / "tmp"
    try:
        d.mkdir(parents=True, exist_ok=True)
        return str(d)
    except OSError:
        return None


def family_errors(main, family: str) -> list[str]:
    """Shipped-bytes gate: main(tmp) must write exactly what is committed under GROUND_ROOT (json LF-normalised, as
    uiart.shipped_errors compares), and the committed family may hold nothing the generator no longer makes."""
    import uiart as U
    old = tempfile.tempdir
    tempfile.tempdir = _tmp_parent()
    try:
        errs: list[str] = []
        seen: set[str] = set()

        def run(out_root):
            main(Path(out_root))
            seen.update(p.name for p in (Path(out_root) / family).glob("*.glb"))
        errs += U.shipped_errors(run, ".", GROUND_ROOT)
    finally:
        tempfile.tempdir = old
    if (GROUND_ROOT / family).is_dir():
        for g in sorted((GROUND_ROOT / family).glob("*.glb")):
            if seen and g.name not in seen:
                errs.append(f"{family}/{g.name} is committed but the generator no longer makes it")
    return errs


# ---------------------------------------------------------------------------------------------- self-test
def _demo_spec(layer="cover", cls="pebbles", disc=(0.0, 0.0), atlas=128) -> GroundSpec:
    import isoforge as I

    def build():
        return [GP.Part(I.box(8.0, 6.0, 4.0, 0.3), GP.STONE)]
    return GroundSpec(build, cls, layer, atlas, disc)


def self_test() -> list[str]:
    fails: list[str] = []

    def expect(cond, msg):
        if not cond:
            fails.append(msg)

    spec = _demo_spec()
    a = build_ground_asset("flora", "demo_stone", spec)
    data = glb_bytes(a)
    expect(a["bounds"][2] > 0 and abs(min(float(p.positions[:, 1].min()) for p in a["primitives"])) < 0.01, "demo asset not grounded")
    expect(glb_bytes(build_ground_asset("flora", "demo_stone", spec)) == data, "build is not deterministic")
    disc = build_ground_asset("flora", "demo_disc", _demo_spec("accent", "rock", (6.0, 6.0), 256))
    expect(glb_material_names(glb_bytes(disc)) == [BAKED_MATERIAL, CONTACT_MATERIAL], "disc materials wrong")
    expect(budget_for("rock", "cover")["atlas"] == 128 and budget_for("rock", "accent")["atlas"] == 256, "rock budgets")

    with tempfile.TemporaryDirectory(dir=_tmp_parent()) as d:
        root = Path(d)
        entry = manifest_entry("flora", "demo_stone", spec, a["bounds"])
        write_family(root, "flora", [entry], {"demo_stone": data})
        before = (root / "flora.json").stat()
        write_family(root, "flora", [], {"demo_other": data}, partial=True)
        after = (root / "flora.json").stat()
        expect((after.st_size, after.st_mtime_ns) == (before.st_size, before.st_mtime_ns), "a partial write touched the manifest")
        expect((root / "flora" / "demo_stone.glb").is_file() and (root / "flora" / "demo_other.glb").is_file(), "a partial write lost a GLB")
        (root / "flora" / "demo_other.glb").unlink()
        text = manifest_text("flora", "tools/ui-assets/gen_ground_flora.py", [entry])
        doc = json.loads(text)
        expect(validate_manifest(doc, root) == [], f"valid manifest rejected: {validate_manifest(doc, root)}")
        expect(text.endswith("\n") and "\r" not in text and (root / "flora.json").is_file(), "manifest must be LF with a trailing newline")
        expect(glb_errors("demo_stone", data, "cover") == [], f"valid glb rejected: {glb_errors('demo_stone', data, 'cover')}")
        expect(glb_errors("demo_stone", data, "decal"), "an opaque material passed as a decal")
        expect(glb_errors("demo_stone", b"junk", "cover"), "garbage passed the container gate")
        expect(glb_errors("other", data, "cover"), "a wrong node name passed")
        expect(glb_errors("demo_disc", glb_bytes(disc), "cover"), "a cover asset with a contact disc passed")
        expect(glb_errors("demo_disc", glb_bytes(disc), "accent") == [], "a disc on an accent asset was rejected")

        def bad(mut, needle):
            dd = json.loads(json.dumps(doc))
            mut(dd)
            e = validate_manifest(dd, root)
            expect(any(needle in x for x in e), f"negative '{needle}' not caught: {e}")
        bad(lambda x: x.__setitem__("schema", "x"), "schema")
        bad(lambda x: x["assets"][0].__setitem__("class", "dragon"), "unknown class")
        bad(lambda x: x["assets"][0].__setitem__("layer", "landmark"), "not allowed")
        bad(lambda x: x["assets"][0].__setitem__("layer", "decal"), "decal")
        bad(lambda x: x["assets"][0].__setitem__("ground", ["lava"]), "ground")
        bad(lambda x: x["assets"][0].__setitem__("id", "flora.Bad Name"), "id must be")
        bad(lambda x: x["assets"].append(json.loads(json.dumps(x["assets"][0]))), "duplicate")
        bad(lambda x: x["assets"][0].__setitem__("file", "flora/missing.glb"), "file")
        (root / "flora" / "stale.glb").write_bytes(data)
        expect(any("stale" in x for x in validate_manifest(doc, root)), "stale glb not caught")
        (root / "flora" / "stale.glb").unlink()
        e2 = manifest_entry("flora", "x", {"cls": "grass", "layer": "cover", "weight": 1.23456, "scale": (0.91234, 1.1)}, (1.23456, 0.5, 2.0))
        expect(e2["radius"] == 1.235 and e2["weight"] == 1.235 and e2["scale"] == [0.912, 1.1], "rounding")
    return fails


if __name__ == "__main__":
    f = self_test()
    for m in f:
        print("FAIL", m)
    print("groundkit self-test:", "FAILED" if f else "ok")
    sys.exit(1 if f else 0)
