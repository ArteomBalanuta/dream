# Engine seams

Paths are relative to `G:\workspace\projects\dream`.

## Layer A — material distribution (simulation, deterministic)

| File | What it holds |
| --- | --- |
| `dream/Simulation/World/WorldGenerator.cs` | The shared geometry walk, `ResolveRaisedLandmarkHeight` (public and static: a policy reads it for slope) and the three hand-placed sculptures |
| `dream/Simulation/World/ITerrainMaterialPolicy.cs` | The material-choice seam: `ResolveFloorMaterial` and `ResolveRaisedMaterial` |
| `dream/Simulation/World/ClassicTerrainMaterialPolicy.cs` | The modulo-hash scatter, byte-identical, behind key `default` |
| `dream/Simulation/World/NaturalTerrainMaterialPolicy.cs` | The fBm + Worley distribution behind key `natural`; the ramps and gains are all named constants at the top |
| `dream/Simulation/World/TerrainNoise.cs` | `Hash`, `Hash01`, `Gradient`, `Fbm`, `FbmUnit`, `Worley`, `Smoothstep`, `Contrast` |
| `dream/Simulation/World/TerrainLandmarks.cs` | The rim ring, the spawn and central paths, the spawn island and its bridge, shared so no policy can lose one |
| `dream/Simulation/World/Geometry/GeometryMaterialId.cs` | `Grass Dirt Stone Sand Mud Snow Rock GrassPath Lamp`. `Lamp` is player-placed and must never be generated. `Snow` is currently never generated either |
| `dream/Simulation/World/MapSources/IMapGenerator.cs`, `MapGenerationRequest` | The generator contract; carries `Width Height Depth Seed`. Both generators ignore `Seed` on purpose |
| `dream/Server/Worlds/WorldCreateOptions.cs`, `WorldCli.cs`, `SqliteWorldCatalog.Create` | `GeneratorKey` on a new world, and the `--worlds create <name> --generator KEY` flag |
| `dream/Client/World/ClientWorldSession.cs` | `RebuildMapForDescriptor`: the client builds a generated world from the descriptor's generator, so server and client agree on the ground |
| `dream/Simulation/World/WorldGeneratorCatalog.cs` | `s_all`, keyed by `(Key, Version)`. Register a new generator here |
| `dream/Simulation/World/MapSources/MapSources.cs` | Resolves a world's generator from its stored `GeneratorKey` + `GeneratorVersion`, or rebuilds it from stored chunks |

### Determinism rules

- A world row stores its generator key and version. Changing the cells an existing
  `(key, version)` produces desynchronises every already-persisted generated world.
  Add a key or bump a version instead.
- `Map.WorldChecksum` folds every cell's material id, so any distribution change moves
  it. Hosts compare checksums.
- No `Random`, no `DateTime`, no `double` (x64 and ARM differ on some transcendentals),
  no dictionary or set enumeration order feeding a decision, no parallelism inside the
  per-cell loop. `float` with explicit `MathF` calls is fine.
- `CreateStartupGeometry` fills a `Dictionary<GeometryCellKey, GeometryCellRecord>` and
  returns `cells.Values.ToArray()`; the goldens pin that **insertion order**. Keep the
  `x` then `z` loop nesting and keep adding the floor cell before the raised cells.

### Golden-test procedure

`dream.Tests/Simulation/World/MapSourceGoldenTests.cs` pins cell count, cell-order hash,
world checksum and physics body order for six (width, depth, enrich) cases. Also check
`GeneratedMapSourceTests`, `MapSourceSeamTests`, `MapWorldChecksumTests` and
`dream.Tests/Simulation/Content/SpawnCampTests.cs` (spawn camp assumes walkable spawn
ground).

If a change to the shipped `default` generator is genuinely wanted, the failure message
prints the measured `new(...)` row verbatim; paste it over the old row and say in the
commit why the world changed. Prefer a new generator key, which needs no re-recording.

## Layer B — surface blending (render only)

| File | What changes |
| --- | --- |
| `tools/ui-assets/gen_terrain.py` | Ships only `<name>.albedo.png` with `s.alpha = ones` (255). The relief field `h = prim + tert` exists in `render_hires`; write it to alpha, or ship a second map, to give the blend a height |
| `dream/Rendering/World/StaticTerrainMaterialCatalog.cs` | One slot per (material, role); `ResolveForgeTexture` builds the path once per slot. A second map per material means a second path and a second cache key |
| `dream/Rendering/Core/StaticTerrainTextureCache.cs` | Keyed by `(MaterialId, Role)`; returns `-1` while decoding and caches `-1` on failure |
| `dream/Rendering/World/StaticWorldChunkMesh.cs` | `StaticWorldVertex(Position, Normal, TexCoord)`. A blend weight or neighbour-material index is a new field here and a new `VertexAttribPointer` in `StaticWorldChunkRenderer` |
| `dream/Rendering/World/StaticWorldChunkMeshBuilder.cs` | `AppendFace` / `FaceDefinition.WriteTexCoords`. The neighbour lookup for a cross-material blend belongs here, next to the existing `FaceOccluderIndex` |
| `dream/Rendering/World/StaticWorldChunkMeshKey.cs` | `(ChunkKey, MaterialId, MaterialRole)` — one mesh and one draw per material, which is why a fragment can't see its neighbour's texture today |
| `dream/shaders/cubeFragment.glsl` | Compiled twice; the `WORLD_ALPHA_MASK` variant discards on low alpha. Terrain is opaque, so alpha is free, but keep terrain out of the MASK program |
| `dream/shaders/cubeVertex.glsl` | Passes `texCoord0`, `normal`, `fragPos`; add the blend varying here |

### Shipped: cross-material height blend and relief

| File | Role |
| --- | --- |
| `dream/Rendering/Core/StaticTerrainTileArray.cs` | 8 tiles (layer = `(int)GeometryMaterialId`, grass top) as one `Texture2DArray`; background decode, one layer upload per call, box mips; `-1` until ready, `-1` for good on any failure |
| `dream/Rendering/Core/Render.Terrain.cs` | Binds the array on unit 2 per pass; sets `uTerrainBlend/Band/Depth/Relief` per pass and `uTerrainLayer` per material; `BindTerrainMaterial` forces variation, blend and layer off for moving fallback cubes. All terrain uniform name strings live here |
| `dream/Rendering/Core/Render.Shaders.cs` | `LoadTerrainShader` sets the sampler units once (`uTerrainTiles` 2, `uTerrainMaterialMap` 3) |
| `dream/Rendering/World/TerrainSurfaceSettings.cs` | `DREAM_TERRAIN_VARIATION`, `DREAM_TERRAIN_BLEND`, `DREAM_TERRAIN_RELIEF` |
| `dream/shaders/cubeFragment.glsl` | Terrain block: per-variant transform shared by all layers, neighbour weights from the map texels (own, X, Z, diagonal), height blend, bump from `dFdx/dFdy` of the height |

Map texel: 0 none, 1..8 material id + 1 (exposed full top cell), 254 covered, 255 not blendable. Unit 0 stays the per-material texture; unit 1 is the war mist.

### Render-side constraints

- `StaticWorldChunkMeshBuilderAllocationTests` and
  `StaticWorldChunkMeshBuilderPerformanceTests` fail on new allocations or a slower
  build. Grow the existing pooled spans; do not add a `List` per face.
- The world pass runs with blending disabled and no sorted transparency: resolve the mix
  in the fragment shader, never with two alpha draws.
- Lighting is applied after the albedo mix (`dreamAmbient + dreamDirect + dreamPointLights`,
  then `dreamExpose`, then `dreamApplyFog`). Mix albedo, not lit colour.
- Triplanar and stochastic sampling multiply texture fetches per fragment. Gate them
  behind a config flag in `docs/configuration.md` style and measure before shipping;
  `docs/performance-and-optimizations.md` records the terrain pass budget.

## Cheapest useful order

1. ~~Layer A noise in a new generator key~~ — done: key `natural`, version 1.
2. Height in the forge tiles' alpha — one `gen_terrain.py` change plus a re-bake.
3. Stochastic tile offset in the fragment shader — kills the repeating grid without any
   mesh or vertex change.
4. Cross-material height blend — the expensive one: neighbour lookup, a vertex
   attribute, a second sampler and a new mesh key.
