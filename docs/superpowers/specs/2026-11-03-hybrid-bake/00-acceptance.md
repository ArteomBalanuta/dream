# Hybrid bake: acceptance criteria

Status: written 2026-11-03, before implementation. Depth: architectural.
Decision: **hybrid** - static world baked at build time, actors lit at frame time.

Claim tags: MEASURED = I measured it this session. VERIFIED = read in the code. DERIVED = computed
from a measurement. Anything untagged is a target, i.e. a choice, not a fact.

## Why this change exists

MEASURED. `roadmap_ideas/concept.gif` (600x338, 314 frames, 30 ms, 180 colours - a degraded screen
recording, so its own palette and any pixel grid are not evidence) against our isometric capture:

| metric | reference (3 frames) | ours | direction |
| --- | --- | --- | --- |
| mean value | 0.123 - 0.159 | 0.252 | must fall |
| median value | 0.094 | 0.255 | must fall |
| p99 value | 0.843 - 0.871 | 0.710 | must rise |
| share v < 0.10 | 0.592 - 0.705 | 0.043 | must rise hard |
| share v > 0.60 | 0.024 - 0.038 | 0.014 | must rise |
| mean saturation | 0.311 - 0.371 | 0.445 | must fall |

DERIVED. The reference histogram is **bimodal**: more near-black *and* more bright than ours at the
same time. A gamma sweep on our own frame proves no monotone tone curve reaches it - at gamma 1.4 the
mean matches (0.141 vs 0.123) but the black share reaches only 0.205; at gamma 1.8 the black share
overshoots to 0.847 while the bright share collapses to 0.005. Bimodality therefore requires low
ambient plus real local light sources, not grading.

VERIFIED. The look gap is bake-time vs frame-time, not 2D vs 3D assets:
- no framebuffer objects, no MRT, no depth textures anywhere in the renderer;
- no runtime shadow mapping and no runtime ambient occlusion;
- `isoforge.py` bakes a key shadow map, 20-direction dome AO, contact AO, GGX and grading, at a
  MEASURED 1.6 s per 150x180 view;
- `cubeFragment.glsl:42` already samples a base colour texture and `StaticTerrainMaterial.TexturePath`
  is loaded by `StaticTerrainTextureCache`.

CORRECTION 2026-11-03. I first recorded that no terrain textures ship. That was **wrong**, and the
architect caught it. VERIFIED: `StaticTerrainMaterialCatalog.cs:109-127` points every material at a
third-party photographic diffuse - 8 git-tracked CC0 PolyHaven JPGs under
`dream/animations/library/polyhaven/` plus the grass PNGs - and
`StaticTerrainMaterialCatalogTests` already asserts `File.Exists` on each. The surfaces are textured.
The problem is that they are textured with photographs of real daylit dirt, grass and sand, which is
precisely why the frame measures mean value 0.254 and mean saturation 0.451. A2 is therefore
**replacing naturalistic photo diffuse with forged grim material**, not filling an empty path. This
makes A2 cheaper in engine terms (the wiring exists) and more demanding in art terms (the new tiles
must be measurably darker and less saturated than what they replace, or nothing changes).

## Acceptance criteria

Measured on a representative isometric gameplay frame captured through `DREAM_HUD_CAPTURE`, over the
world region only (the HUD band excluded), with the same script and camera as the baseline capture.

**A1 - bimodal value structure.** Both tails must move, in the same frame:
- share v < 0.10 >= 0.45
- share v > 0.60 >= 0.020
- mean v <= 0.18
- mean saturation <= 0.38

A1 is the criterion that cannot be faked by grading, so it is the gate that decides whether the
lighting change actually happened. A frame that passes the mean but fails either tail fails A1.

**A2 - surfaces carry detail.** The primary ground materials report `HasTexture`, and each shipped
terrain texture shows high-frequency content: the ratio of local (3 px) to broad (16 px) luma band
energy must exceed a floor measured on a known-good forge output, so a flat or near-flat fill fails.
A gradient alone does not pass.

**A3 - the actor reads against the ground it stands on.** In the same frame, the player crop's p99
value must exceed the surrounding ground's p99 by >= 0.08. BASELINE MEASURED: 0.2625 vs 0.2647, a
margin of -0.002, i.e. no separation at all.

**A4 - nothing regresses.** `tests/check_sources.py` stays at 0 problems; every new or changed
generator is byte-deterministic across two runs; the existing `check_*.py` art gates still pass; the
solution builds with 0 errors and the affected tests pass.

## Non-goals

Explicitly out of scope. Each was considered and deferred with a reason.

1. **No low-resolution or pixel-art render pass.** MEASURED: our frame at 1440p, 480x270 and 320x180
   is visually the same image, because the content carries no detail to lose. Revisit only after A2.
2. **No sprite-actor replacement.** Actors keep the glTF path: equipment (7 held visuals), held point
   lights and multiplayer all attach there, and sprites would lock the camera and multiply frames.
3. **No camera projection change.** The isometric mode stays a narrow-FOV perspective projection.
4. **No framebuffer objects.** If a workstream needs one, that is a scope change to raise, not absorb:
   it is a new engine capability and the most likely place for this to explode.
5. **No nearest-neighbour filtering switch.** It only matters for a texel grid, which is non-goal 1.

## Open questions

- Q1. Does lowering ambient to reach A1 make the existing HUD, menus and FX read wrong? They were
  authored against the current brighter world.
- Q2. RESOLVED 2026-11-03, by measuring the baseline frame with the HUD band excluded: the world's
  bright share is **0.003**, against 0.014 for the whole frame including the HUD. Our only bright
  pixels are the HUD. The world contains no light source that produces a highlight, so the A1 bright
  tail must come from authored light placement; no amount of exposure or ambient tuning can supply it.
- Q3. RESOLVED 2026-11-03. VERIFIED: terrain UVs are `0..cellSize`, one texture repeat per voxel
  (`StaticWorldChunkMeshBuilder.cs:275-305`), and the terrain upload path sets no wrap mode, so it
  inherits GL's default `GL_REPEAT`. Tiling works today, but only by default, so the baked tiles must
  be seamless and the wrap mode should be set explicitly. A2 needs no new engine capability.
- Q4. NEW, blocking WS-B. Does the grim look own the whole day/night cycle, or does the game default
  to the night/dusk band? The target is a night scene; its measured numbers are reachable at the night
  and dusk keyframes but not at noon with `DaySunIntensity` 0.82 on a 60-degree sun. Owning the whole
  clock means re-authoring the day keyframes as overcast gloom, which is much more than lowering
  ambient. This is a product decision, not a code one.

## Baseline, measured through the gate

`python tools/ui-assets/tests/check_look.py artifacts/iso-capture/02-snap.png --hud-rows 140`

    world 1440x884      measured   required
    mean value          0.254      <= 0.18    FAIL
    near-black share    0.012      >= 0.45    FAIL
    bright share        0.003      >= 0.020   FAIL
    mean saturation     0.451      <= 0.38    FAIL

All four fail, which is the point: the gate has a real baseline to move off. A3 is unmeasured in this
frame for want of an actor crop; the earlier hand measurement put the margin at -0.002.

`check_look.py --selftest` holds four synthetic frames that must each fail A1 - flat mid grey, a
graded-dark frame with no light sources, an oversaturated frame and dark noise - plus one built to
pass. The graded-dark case is the important one: it is what a tone curve produces, and it fails on the
bright tail, which is exactly the distinction A1 exists to enforce.
