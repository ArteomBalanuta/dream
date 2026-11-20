#!/usr/bin/env python3
"""
Build a creature design into a skinned, textured, animated .glb.

  python build_creature.py ../designs/hare.py --out ../out            # final quality
  python build_creature.py ../designs/hare.py --out ../out --preview  # fast iteration

Outputs <out>/<NAME>.glb plus the baked textures and the high-res sculpt stats.
"""
import argparse
import importlib.util
import json
import math
import os
import sys
import time

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import creaturelib as cl  # noqa: E402


def load_design(path):
    sys.path.insert(0, os.path.dirname(os.path.abspath(path)))
    spec = importlib.util.spec_from_file_location("design", path)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("design")
    ap.add_argument("--out", default="out")
    ap.add_argument("--preview", action="store_true", help="coarse voxels, fewer tris, 1K textures")
    ap.add_argument("--voxel", type=float)
    ap.add_argument("--tris", type=int)
    ap.add_argument("--tex", type=int)
    ap.add_argument("--jpeg", type=int, default=92, help="JPEG quality for albedo/normal/ORM (0 = PNG)")
    a = ap.parse_args()

    D = load_design(a.design)
    S = dict(D.SETTINGS)
    if a.preview:
        S.update(D.SETTINGS.get("preview", {}))
    for k in ("voxel", "tris", "tex"):
        if getattr(a, k):
            S[k] = getattr(a, k)
    os.makedirs(a.out, exist_ok=True)
    T0 = time.time()
    log = lambda *x: print(*x, flush=True)

    cr = cl.Creature(D.prims(), D.BONES)
    log(f"[{D.NAME}] {len(cr.prims)} primitives, {len(cr.bones)} bones, tags: {', '.join(cr.tags)}")

    log("1/6 sculpt -> marching cubes")
    Vh, Fh = cl.polygonize(cr, S["voxel"], log)
    Vh, Fh = cl.keep_large_components(Vh, Fh)
    log(f"  high-res sculpt: {len(Fh):,} tris ({time.time()-T0:.0f}s)")

    log("2/6 decimate + reproject")
    V, F = cl.decimate(Vh, Fh, S["tris"])
    V, F = cl.keep_large_components(V, F, 20)
    V = cr.project(V, iters=3)
    N = cl.vertex_normals(V, F)
    log(f"  game mesh: {len(F):,} tris, {len(V):,} verts ({time.time()-T0:.0f}s)")

    log("3/6 skin weights")
    J, W = cl.skin_weights(cr, V)

    log("4/6 UV unwrap")
    vmap, F2, UV = cl.unwrap(V, F, N, S["tex"], log=log)
    V2, N2, J2, W2 = V[vmap], N[vmap], J[vmap], W[vmap]
    T2 = cl.tangents(V2, N2, UV, F2)

    np.savez_compressed(os.path.join(a.out, f"{D.NAME}_mesh.npz"), V=V2, N=N2, UV=UV, F=F2, T=T2, J=J2, W=W2)
    log("5/6 bake textures from the SDF sculpt")
    alb, nor, orm = cl.bake(cr, V2, F2, N2, T2, UV, S["tex"], D.shade, log=log)
    for name, im in (("Albedo", alb), ("Normal", nor), ("ORM", orm)):
        im.save(os.path.join(a.out, f"T_{D.NAME}_{name}.png"))

    log("6/6 extra parts, animation, export")
    fmt = "JPEG" if a.jpeg else "PNG"
    images = []
    for name, im in (("Albedo", alb), ("Normal", nor), ("ORM", orm)):
        data, mime = cl.img_bytes(im, fmt, a.jpeg)
        images.append((f"T_{D.NAME}_{name}", data, mime))
    fur_mat = {"name": f"M_{D.NAME}_Fur",
               "pbrMetallicRoughness": {"baseColorTexture": {"index": 0}, "metallicRoughnessTexture": {"index": 2},
                                        "metallicFactor": 1.0, "roughnessFactor": 1.0},
               "normalTexture": {"index": 1, "scale": 1.0},
               "occlusionTexture": {"index": 2, "strength": 1.0}}
    ext_used = set()
    if getattr(D, "FUR_MATERIAL_EXT", None):
        fur_mat["extensions"] = D.FUR_MATERIAL_EXT
        ext_used |= set(D.FUR_MATERIAL_EXT)
    materials = [fur_mat]
    prims = [dict(name="Body", P=V2, N=N2, UV=UV, T=T2, J=J2, W=W2, I=F2, material=0)]
    tri_counts = {"Body": int(len(F2))}
    for part in (D.extra_parts(cr) if hasattr(D, "extra_parts") else []):
        mat = dict(part["material"])
        if part.get("texture") is not None:
            data, mime = cl.img_bytes(part["texture"], "PNG")
            images.append((f"T_{D.NAME}_{part['name']}", data, mime))
            mat.setdefault("pbrMetallicRoughness", {})["baseColorTexture"] = {"index": len(images) - 1}
            part["texture"].save(os.path.join(a.out, f"T_{D.NAME}_{part['name']}.png"))
        ext_used |= set(mat.get("extensions", {}))
        materials.append(mat)
        Jp, Wp = cl.fixed_weights(len(part["P"]), cr, part["weights"])
        prims.append(dict(name=part["name"], P=part["P"], N=part["N"], UV=part.get("UV"), T=None, J=Jp, W=Wp,
                          I=part["I"], material=len(materials) - 1))
        tri_counts[part["name"]] = int(len(part["I"]))

    anims = []
    for name, dur, fps, fn in getattr(D, "ANIMATIONS", []):
        times = np.linspace(0, dur, int(dur * fps) + 1)
        frames = [fn(t) for t in times]
        bones = sorted({b for f in frames for b in f})
        ident = np.array([0, 0, 0, 1.0])
        rots = {b: np.array([cl.nrm(f.get(b, ident)) for f in frames]) for b in bones}
        anims.append((name, times, rots, None))

    total_tris = sum(tri_counts.values())
    extras = {"name": D.NAME, "title": getattr(D, "TITLE", D.NAME), "triangles": total_tris, "parts": tri_counts,
              "sculpt_triangles": int(len(Fh)), "texture_size": S["tex"], "voxel_mm": S["voxel"] * 1000}
    out = os.path.join(a.out, f"{D.NAME}.glb")
    size = cl.write_glb(out, D.BONES, prims, materials, images, anims, extras, sorted(ext_used))
    json.dump(extras, open(os.path.join(a.out, f"{D.NAME}_stats.json"), "w"), indent=1)
    lo, hi = V.min(0), V.max(0)
    log(f"done: {out}  {size/1e6:.2f} MB, {total_tris:,} tris {tri_counts}, bbox {lo.round(3)}..{hi.round(3)}, {time.time()-T0:.0f}s")


if __name__ == "__main__":
    main()
