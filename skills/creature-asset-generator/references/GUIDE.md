# Creature Asset Generator: Guide

## 1. How it works (the sculpt → game asset pipeline in one process)

| Stage | Real-world equivalent | Implementation |
|---|---|---|
| Sculpt | ZBrush / Blender sculpt | ordered SDF primitives, `smin` / `smax` smooth booleans (`Creature.sdf`) |
| High poly | dynamesh / remesh | marching cubes on a 1.5 mm grid (≈40M voxels, ≈0.6M triangles) |
| Retopo | manual retopology / ZRemesher | quadric decimation (`fast_simplification`) to `tris`, then vertices re-projected onto the SDF |
| UV | Blender unwrap / RizomUV | `xatlas` (charts, padding 8, 2048 packing) |
| Bake | Marmoset / Substance bake | per texel: position → project to SDF → gradient = high-poly normal; SDF AO; region and flow blend |
| Texture paint | Substance Painter | design `shade(ctx)` returns albedo, roughness and fur strength from regions, flow and noise |
| Rig + skin | Blender auto-weights | soft-min over primitive distances → bone weights (top 4) |
| Animate | keyframes | design functions `t -> {bone: quat}`, sampled to LINEAR glTF channels |

Because the "high poly" is an analytic field, baking needs no ray-casting cage: every texel simply asks the field for its true surface point and normal.

## 2. Design-file contract (`designs/<name>.py`)

| Symbol | Type | Purpose |
|---|---|---|
| `NAME`, `TITLE` | str | file names / viewer title |
| `SETTINGS` | dict | `voxel` (m), `tris`, `tex`, `preview` overrides |
| `BONES` | list of (name, parent or None, world pos) | rest pose, identity rotations; the root comes first |
| `prims()` | list of `Prim` | the sculpt, in order |
| `shade(ctx)` | fn | returns dict `albedo (N,3 sRGB 0–1)`, `roughness (N)`, `fur (N)`, `grad (N,3)`, optional `occlusion`, `metallic` |
| `extra_parts(cr)` | fn | list of dicts: `name, P, N, UV or None, I, weights=[(bone,w)], texture (PIL or None), material (glTF dict)` |
| `FUR_MATERIAL_EXT` | dict | glTF extensions on the baked material (sheen) |
| `ANIMATIONS` | list of (name, duration_s, fps, fn(t) -> {bone: quat xyzw}) | animation clips |

### `ctx` passed to `shade`
`P` surface points, `N` high-poly normals, `Nl` low-poly normals, `flow` (tangent fur direction), `tag` (dict tag → weight 0–1, smooth across region borders), `ao`, `T`/`B` tangent frame. Helpers in creaturelib: `strand_field(P, flow, fp, fa, seed)` returns (value in [-1,1], gradient), `wave_noise(P, freq, seed)`, `smoothstep`.

### Primitive kinds (`Prim(kind, bone, k, op, tag, flow, amp, freq, **params)`)
- `ellipsoid`: `c`, `r=(rx,ry,rz)`, `R` (3×3, columns = local axes; use `basis(primary, hint)` or `rot_x/y/z`)
- `sphere`: `c`, `r`
- `cone`: `a`, `b`, `ra`, `rb` (exact round cone; limbs, necks, toes, claws, horns)
- `torus`: `c`, `n` (axis), `R`, `r` (eyelids, rims, collars)
- `amp`/`freq`: surface noise (fluffy tails, shaggy manes)
- `box`: `c`, `b` (half extents), `R`, `rr` (rounding): buckles, plates, slots
- `plane`: `c`, `n`. As `op='sub'` it removes the -n side, so it keeps the +n side. Add `amp`/`freq` for ragged hems.
- `noise`: `c`, `r` (bound), `thresh`, `freq`. As `op='sub'` it punches organic holes (tattered cloth, rot).
- `group`: `group(children, bone, tag)` evaluates its children on their own, then unions the result as ONE primitive. Use it for every armor plate and garment.
- `shell=t` on any primitive makes a hollow skin of thickness 2t.
- `k`: blend radius in meters. Body masses use 0.03–0.045, limbs into the body 0.015–0.025, facial features 0.008–0.016, toes and claws 0.001–0.004.

## 3. Anatomy recipe (how the hare was built; reuse it for other mammals)
1. **Masses first**: torso, rump, chest, belly ellipsoids, plus a neck cone. Check the silhouette in the side view before adding anything else.
2. **Limbs** as cone chains joint to joint. The joint positions are the bone positions, so the rig lines up automatically.
3. **Definition**: scapula, stifle (knee) and haunch ellipsoids with *smaller* k than the masses, so they read through.
4. **Head**: skull ellipsoid (pitched with `rot_x`), muzzle, cheeks, chin, brow, nose sphere. Then subtractions (nostrils, lip split, eye sockets), then additions that must survive the carve (eyelid torus).
5. **Ears**: two overlapping flat ellipsoids along the ear axis (lower and upper lobes on Ear1/Ear2 bones), then a slightly smaller flat ellipsoid **subtracted**, offset toward the opening side, which makes the cup.
6. **Feet**: a long cone for the foot, 4 toe cones, and a claw cone on each toe.
7. **Tail**: ellipsoid with `amp` noise.

## 3b. Hard-surface and cloth recipe (from the skeleton warrior)
- **Plate armor (pauldron)**: `group([ellipsoid shell, SUB plane (keep top, tilted outward), SUB plane (trim toward the neck), second lame ellipsoid shell, SUB plane, SUB ellipsoid (the first dome's inside, so the lame tucks under), crest ellipsoid with amp])`.
- **Knee guard**: ellipsoid shell around the knee, trimmed by a plane facing the knee's forward direction, plus a small boss and a lower lame.
- **Greave**: a round-cone shell trimmed top (ragged) and bottom. Boot: solid ellipsoid + ankle cone + toe cap, with a ground plane cut.
- **Tunic or skirt**: an ellipsoid or round-cone shell with wave `amp` for drape, a ragged `plane` hem (amp 0.02–0.05), `box` slits, `noise` holes at two scales, and a front `box` opening to reveal anatomy.
- **Wraps and bandages**: a thin cone shell around a limb with noise holes and ragged ends (`wraps()` helper).
- **Shading by material tag**: return `metallic` from `shade` (it goes in the ORM blue channel). Bone is ivory with AO-driven grime; rust is a layered noise palette with bare-metal patches; steel is bright albedo (around 200) with grime and rust bloom (dark steel albedo renders black); cloth uses a two-axis weave from `strand_field`, `fp=70` folds, and grime toward the hem; leather is dark brown with worn patches.
- Detail normals for non-fur materials come from `wave_noise_grad(P, freq)` scaled by 1/freq, plus `strand_field` for scratches and weave.

## 4. Shading recipe (fur)
- Two strand octaves: fine (`fp≈3000, fa≈280`) and clump (`fp≈950, fa≈110`). Albedo modulation ±0.14 and ±0.10; normal `fur` strength about 0.2.
- Regions: a darker, cooler top (`N.y` high on back/neck/head), a lighter underside (`N.y` low on belly, chest, chin, legs, plus the inner legs), a pale eye ring, a pink inner ear with veins (normal · ear-opening direction), nose leather, nostrils, claws, a tail with a dark top and white underside.
- Multiply albedo by `0.55 + 0.45·AO` for contact darkening, and keep the full AO in the ORM red channel.
- Roughness: fur 0.78–0.84, inner ear 0.62, nose 0.42, claws 0.32. Sheen on the fur material.

## 5. Settings and cost (8-core laptop)
| Mode | voxel | tris | tex | time | GLB |
|---|---|---|---|---|---|
| `--preview` | 2.8 mm | 7k | 1024 | ~30 s | ~2 MB |
| final | 1.5 mm | 11k | 2048 | ~2.5 min | ~4 MB (JPEG 92) |
Use `--jpeg 0` for PNG maps (much larger files). Halving the voxel size is about 8× the memory and time for the sculpt stage.

## 6. Lessons learned (all hit while building the hare)
1. **Speckle instead of fur**: strand wavelength below about 4 texels aliases into salt-and-pepper noise. Keep `fp` ≤ 3000 at 2K and ≤ 1500 at 1K.
2. **Fingerprint swirls**: a few equal-frequency plane waves interfere into regular patterns. `strand_field` uses 36 waves, frequencies spread 0.55–1.9× and 1/f amplitude, with soft ridges.
3. **Sandpaper look**: fur normal strength above about 0.3. Real fur reads soft at game distance.
4. **Dark ear backs from fake AO**: IQ ellipsoid distance underestimates off-axis on flat shapes. `Creature.ao` calibrates against a 1.5 mm reference step. Diagnose by sampling the maps at the mesh vertices from `<NAME>_mesh.npz`.
5. **Too pale**: lit renders brighten a lot. Pick albedo from the reference's mid-tones and expect the viewer to show it lighter.
6. **Proportions drift**: measure ratios off the reference side view (back height / body length ≈ 0.55–0.6 for a crouched hare) and compare in the four-view sheet, not in orbit.
7. **Chest too low hides the forelegs**: raise the chest and belly so the legs read as legs.
8. **Order bugs**: an eyelid added before the socket subtraction gets carved away.
9. **Eye marbles**: a big, bright iris looks toy-like. Use a large pupil (`th<34°`), a dark iris with a thin amber rim, and push the eye slightly into the socket.
10. **Pauldron vanished**: a trimming plane with a tilted normal anchored at y=0 removed the whole part. Anchor planes at the part and test the field along a line (`group.sdf(P)` step by step).
11. **Cube-shaped sculpt grid**: group bounds must be boxes (AABB), not spheres. Fixed in `Prim.lo/hi`.
12. **Texture space waste on detailed meshes**: hundreds of tiny charts × padding. Use `max_cost=6`, padding 4 and no blockAlign (coverage 25% → ~40%).
13. **Slow fields with hundreds of primitives**: `Creature` evaluates in spatial blocks and only touches nearby primitives (a 358-primitive skeleton sculpts in under a minute at preview).
14. **xatlas API**: options are `PackOptions.bruteForce` and `blockAlign`, and `atlas.get_utilization(0)`.

## 7. Debugging recipe
```python
d = np.load("out/<NAME>_mesh.npz")          # V,N,UV,F,T,J,W of the game mesh
# sample baked maps at vertices of a region and compare albedo / AO / decoded normal
```
The viewer exposes `window.__viewer = {cams, orbitCam, controls, scene}`. Script camera positions for repeatable close-ups. Toggle Normal map and Albedo to isolate a problem.

## 8. Engines
- **three.js / Babylon / web**: works as is (tangents provided, sheen and clearcoat supported).
- **Godot 4**: native .glb import. Sheen is ignored. Clearcoat is supported.
- **Unity**: glTFast. Map the skeleton to a Generic rig (quadruped). Sheen needs HDRP or a custom shader.
- **Unreal**: Interchange glTF import, or convert to FBX in Blender.
- **Blender**: File → Import → glTF. Export FBX with `add_leaf_bones=False`.
- The normal map uses the exported TANGENT attribute (B = cross(N,T)·w). If an engine regenerates tangents and the bumps look inverted, flip the green channel.

## 9. Making a new creature quickly
- **Fox or dog**: copy the hare, shorten the ears and make them triangular (one ellipsoid plus a cone tip), lengthen the muzzle, use digitigrade hind legs (shin→foot cone shorter, angled), a bushy tail (`amp` 0.006), and an orange/white/black palette by region.
- **Cat**: rounder skull, short muzzle, triangular ears, long tail cone chain (Tail1–4 bones).
- **Rodent**: smaller scale (bump voxel down to 1 mm and re-check memory), big incisors as cones.
- **Bird**: a body ellipsoid, wings as flat ellipsoid chains, a beak cone with `tag="beak"` and low roughness, and feather strands with `fa` very low.
- **Reptile**: swap the strand noise for scale noise (cellular via `wave_noise` thresholds) and use a low `fur` value with a high-frequency bump.
