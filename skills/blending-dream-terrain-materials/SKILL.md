---
name: blending-dream-terrain-materials
description: Replaces Dream's modulo-hash terrain material scatter with organic noise-driven distribution and height-aware surface blending. Use when changing how grass, dirt, stone, sand, mud, rock or snow are chosen per world cell, when adding fBm or Worley noise to world generation, when blending or triplanar-mapping the baked forge terrain tiles, or when grid artifacts and repeating tiles are visible on the ground.
---

# Blending Dream Terrain Materials

Dream's ground is voxel: one `GeometryMaterialId` per world cell, drawn from one baked
256 px forge tile per material and role. There is no splat map and no per-fragment
material mixing today. "Blending" therefore has two independent layers, and they are
changed in different places with different risks:

| Layer | Where | Risk |
| --- | --- | --- |
| A. Distribution — which material a cell gets | `dream/Simulation/World/*TerrainMaterialPolicy.cs`, `TerrainNoise.cs` | Simulation-deterministic: breaks goldens and already-persisted worlds |
| B. Surface — how two materials meet on screen | `dream/shaders/cubeFragment.glsl`, `StaticWorldChunkMeshBuilder`, `tools/ui-assets/gen_terrain.py` | Render-only: no determinism risk, but costs a vertex attribute and a sampler |

Decide which layer the complaint is about before writing code. Blocky patches of sand
in the middle of grass are layer A. Hard straight seams where sand meets grass, or an
obvious repeating tile grid, are layer B.

## Current state (read before proposing anything)

- **Layer A is implemented.** `ITerrainMaterialPolicy` is the seam: the geometry walk, the
  cell insertion order and the landmark heights stay in `WorldGenerator`, and only the
  material choice is swappable. `ClassicTerrainMaterialPolicy` holds the old scatter,
  byte-identical, behind generator key `default`; `NaturalTerrainMaterialPolicy` is the
  fBm-and-Worley distribution behind key `natural`, registered in `WorldGeneratorCatalog`
  and reachable as `--worlds create <name> --generator natural`. `TerrainNoise` holds the
  primitives (hash, gradient noise, fBm, Worley, smoothstep, contrast). Extend those rather
  than starting again, and read `NaturalMapGeneratorTests` before retuning a ramp.
- `ClassicTerrainMaterialPolicy.ResolveFloorMaterial` still picks the material from
  `Hash(x / 3, z / 3)` tested against `% 29`, `% 23`, `% 11`, `% 17`, and it stays that way:
  it quantises to 3x3 cell blocks, has no spatial coherence, and the modulo families
  interact so coverage is neither tunable nor smooth. `ResolveRaisedMaterial` does the
  same with `Hash(x / 2, z / 2) % 5`.
- Both generators ignore `MapGenerationRequest.Seed`, and that is load-bearing: the client
  rebuilds a generated world from its descriptor
  (`ClientWorldSession.RebuildMapForDescriptor`) and never receives the simulation seed,
  which is server-only. A seed-dependent generator would hand the client different ground
  from the server's. Sending the seed in the join-accepted descriptor is the prerequisite
  for per-seed worlds.
- Normalised fBm clusters near 0.5, so a ramp placed on the raw field catches almost
  nothing and the world comes out one material. `TerrainNoise.Contrast` spreads the field's
  middle half across most of 0..1 first; the measured raw range on a 56x56 world is about
  0.25..0.73 with the middle half inside 0.47..0.59.
- Each terrain material ships exactly one file:
  `dream/animations/library/forge/<name>/<name>.albedo.png`, RGBA with **alpha = 255
  everywhere**. No height, normal or roughness map is shipped. The alpha channel is a
  free slot for a height map.
- One chunk mesh per (chunk, material id, role) — see `StaticWorldChunkMeshKey`. A draw
  binds one terrain texture, so a cell knows nothing about its neighbour's material at
  fragment time.
- `StaticWorldVertex` is `(Position, Normal, TexCoord)` only: no blend weight, no second
  UV, no material index.

## Workflow

1. Read [references/engine-seams.md](references/engine-seams.md) for the exact files,
   the determinism rules and the golden-test procedure.
2. Read [references/algorithms.md](references/algorithms.md) for the fBm, Worley and
   height-blend specifications and their zero-allocation shapes.
3. For layer A, do **not** edit `default`, and do not edit `natural` either once a world has
   been created with it. Add an `ITerrainMaterialPolicy` plus an `IMapGenerator` with its own
   key and version, and register it in `WorldGeneratorCatalog.s_all`. `WorldDescriptor` stores the generator key and version
   per world, so an existing key must keep producing byte-identical cells forever or
   already-persisted worlds stop reproducing. A new key leaves
   `dream.Tests/Simulation/World/MapSourceGoldenTests.cs` untouched and gives the new
   terrain its own goldens.
4. Extend `TerrainNoise` with `static` methods over `int`/`float` and a seed argument: no
   `Random`, no `double`, no transcendental call (a gradient table beats `MathF.Cos`), no
   LINQ and no allocation in the per-cell loop. Terrain is
   generated on the server and shipped to clients as chunks, but the world checksum is
   compared across hosts, so the same seed and size must give the same bytes on every
   machine and every run.
5. For layer B, pack the forge tile's existing height field into the albedo alpha in
   `gen_terrain.py` before touching the shader — the blend has nothing to work with
   until that channel carries data. Layer B also needs a neighbour material per face
   and a second sampler; `references/engine-seams.md` lists everything it touches.
6. Verify with the relevant tests only, then report what changed per layer.

## Dream conventions that bite here

- Albedo is held near V 0.2 and desaturated on purpose; the look is dark, grim and
  gothic. A blend that brightens the ground, or that reveals a saturated tile under a
  dark one, is a regression even when the math is right.
- The world pass has blending off and no sorted transparency. A blend must be computed
  and resolved inside the fragment shader; it cannot be two draws with alpha.
- `cubeFragment.glsl` is compiled twice, and the `WORLD_ALPHA_MASK` variant discards on
  `baseColor.a < alphaCutoff`. Terrain draws opaque, so height-in-alpha is safe there,
  but never let a terrain material reach the MASK program once alpha means height.
- Keep the per-frame draw path reading the material table by slot. Do not build strings
  or look up paths per mesh per frame (`StaticTerrainMaterialCatalog`).

## Validation

```powershell
dotnet test dream.Tests --filter "FullyQualifiedName~TerrainNoiseTests|FullyQualifiedName~NaturalMapGeneratorTests|FullyQualifiedName~MapSourceGoldenTests|FullyQualifiedName~WorldGeneratorCatalogTests"
```

Then, for render-side work:

```powershell
dotnet test dream.Tests --filter "FullyQualifiedName~Rendering.World"
```

Full-suite runs are expensive; run at most two in a row and isolate a failure instead of
re-running. Regenerate tiles only for the materials that changed:

```powershell
python tools/ui-assets/gen_terrain.py --only sand,grass_top
```

Record a `docs/ledger/work-log/` entry when the distribution of a shipped generator
changes, so a later session can tell which worlds were built with which terrain.
