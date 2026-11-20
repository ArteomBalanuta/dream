---
name: creature-asset-generator
description: Generate realistic or stylized game-ready creature and character 3D models (skinned, textured, animated .glb) in pure Python, with no Blender or sculpting app. Use when the user wants a 3D animal, monster, undead or skeleton, armored or clothed NPC, mount or critter model, or a "make a model like this reference image" request for an organic or armored character. It sculpts with signed-distance primitives, polygonizes, decimates to a game budget, auto-unwraps, bakes normal, albedo and AO maps from the sculpt with procedural fur, rigs with automatic skin weights, adds eyes and whiskers, and exports glTF with animations plus a four-view turnaround viewer for checking against references.
---

# Creature asset generator

Turns a **design file** (anatomy as SDF primitives + shading rules + rig + animations)
into one `.glb`:

- a game mesh (default 11k triangles) decimated from a ~0.6M-triangle sculpt
- 2048² **albedo**, tangent-space **normal** and **ORM** maps baked from the sculpt (procedural fur strands, ambient occlusion, region-based coloring)
- **eyes** (glossy clearcoat, painted iris) and **whiskers**, as extra primitives
- a skeleton with automatic skin weights and keyframed **animations**
- `KHR_materials_sheen` on the fur

Two reference designs show the range:
- `designs/hare.py`: Grey Hare. Organic anatomy, fur strands, eyes, whiskers.
- `designs/skeleton_warrior.py`: about 360 primitives. A full skeleton, rusted plate, steel greave and boot, tattered cloth with holes and ragged hems, leather straps, and per-region metal/roughness.

Copy the closer one as your starting point.

## Setup
Run from the repo root. The skill keeps its own Python environment in `skills/creature-asset-generator/.venv` (git-ignored, create it once per machine):
```bash
SK=skills/creature-asset-generator
python3 -m venv $SK/.venv && $SK/.venv/bin/pip install numpy scipy pillow scikit-image fast-simplification xatlas
$SK/.venv/bin/python $SK/scripts/build_creature.py $SK/designs/hare.py --out out/creatures --preview
```
On Windows use `$SK\.venv\Scripts\python.exe` instead of `$SK/.venv/bin/python`.
New designs go in `$SK/designs/<creature>.py`, copied from the closer of the two existing designs.

**Engine note (Dream):** the output is a skinned, tonemapped-PBR glTF. Dream draws equipment as unskinned parts on a rigid hierarchy with no tonemap, so albedo above ~0.42 clips (see `docs/ledger/work-log/2026-11-19-royal-sorcerer-set.md`). Treat these assets as source or reference art until an import path exists.

## Workflow
1. **Study the reference.** Read the images and write a short design sheet (references/DESIGN_SHEET.md): size in meters, pose, the proportion ratios measured off the image (body length : back height : ear or horn length : head length), palette sampled from lit mid-tones, special features (whiskers, horns, scales, fluffy tail).
2. **Copy `designs/hare.py`** to `designs/<creature>.py`. Rewrite the landmarks, `BONES`, `prims()`, `shade()`, `extra_parts()` and `ANIMATIONS`. Keep the conventions below.
3. **Preview build** (about 30 s):
   `$SK/.venv/bin/python $SK/scripts/build_creature.py $SK/designs/<c>.py --out out/creatures/<c> --preview`
4. **Look at it.** `$SK/scripts/make_viewer.py out_preview/<NAME>.glb out_preview/viewer.html`, serve with `python3 -m http.server` (file:// may be blocked), and open it. The default **Four views** layout (front, three-quarter, side, back) matches a standard model-sheet reference. Compare the silhouette first, then proportions, then tone. Orbit mode plus `window.__viewer` lets you script close-ups.
5. **Iterate** on the design file only. Fix silhouette before color, and color before fur detail. When a map looks wrong, sample the baked maps numerically (`<NAME>_mesh.npz` is dumped for this; see references/GUIDE.md §7).
6. **Final build** (about 2.5 min): drop `--preview`.
7. **Check animations** in the viewer (the dropdown) for tearing, then turn on wireframe and bones.
8. **Report** triangles per part, sculpt triangles, map size, bones, clips, file size, and what was or wasn't verified.

## Conventions (hard rules)
- Meters, **Y up, +Z = facing direction (nose), +X = creature's left**. Ground at y = 0.
- Primitives are applied **in list order**. A subtraction (`op="sub"`) only carves what came before it, so add an eyelid ring *after* its eye socket and an ear cup *after* its ear lobes.
- Build **hard-surface and cloth parts as `group([...])`**: a solid with `shell=` (a hollow skin), trimmed by `SUB('plane')` (which keeps the +n side), `SUB('box')` and `SUB('noise')` (which punches holes). A group's cuts never carve the body. Anchor every trimming plane's `c` at the part's own height, because a tilted plane anchored at the origin removes everything.
- Shells need at least 1.5 voxels of half-thickness (`shell >= 0.75*voxel`), or they break up in marching cubes.
- Every union primitive has a `bone` (skin weights come from it), a `tag` (shading region) and a `flow` (fur direction).
- Mirror by building each side with an `sx = ±1` helper (`side_pt` / `side_vec`). Never mirror rotations by hand.
- Thin parts (ears, wings, fins) must be at least 3 voxels thick at the final voxel size (1.5 mm by default), or they tear.
- Keep shading positions (eye ring, nostrils) **derived from the same constants** as the sculpt.
- Fur relief: normal strength 0.15–0.3, strands 2–3 mm wide and about 10× longer than wide. Stronger values look like sandpaper or fingerprints.
- Write albedo at a lit-mid-tone value: real fur albedo is darker than it looks in a render. Judge it under the viewer's neutral studio light.
- Never claim validation you didn't run.

## Files
- `scripts/creaturelib.py`: the SDF primitives, field, marching cubes, decimation, xatlas UVs, tangents, texel baker, strand noise, eyes and strands, skinning, quaternions and the glTF writer
- `scripts/build_creature.py`: CLI pipeline (`--preview`, `--voxel`, `--tris`, `--tex`, `--jpeg`)
- `scripts/make_viewer.py` + `scripts/viewer_template.html`: turnaround / orbit viewer
- `designs/hare.py`: organic reference (77 primitives, 24 bones, Idle + Alert clips)
- `designs/skeleton_warrior.py`: armored and clothed humanoid reference (~360 primitives, 24 bones, Idle + Rattle clips)
- `references/GUIDE.md`: the API, the design-file contract, the lessons learned, troubleshooting and engine notes
