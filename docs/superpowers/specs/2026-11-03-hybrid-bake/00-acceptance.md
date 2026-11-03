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

## Surface tiles: two passes, and what the first one taught

MEASURED in-engine, by building the terrain branch and capturing at the default 08:00 start.

| | mean v | near-black | bright | mean sat |
| --- | --- | --- | --- | --- |
| master baseline | 0.254 | 0.012 | 0.003 | 0.451 |
| pass 1 | 0.107 | 0.639 | 0.004 | 0.182 |
| pass 2 | 0.189 | 0.085 | 0.004 | 0.146 |

Pass 1 passed every tile-level gate and was wrong. It hit the A1 night numbers **at eight in the
morning**, because A1's bands were derived from a night scene while albedo is time-independent: a
darker tile darkens every hour of the clock equally. The user's decision was that the grim look owns
night and dusk only, so pass 1 broke the half of the day it was supposed to leave alone.

**The lesson, worth keeping: a gate measured on an asset is not a gate measured on a frame.** The
tile gates (value ceiling, saturation ceiling, seam, no baked key) are all still correct and all
still pass; none of them could have caught this, because none of them looks at a rendered frame. A1
belongs on a capture, and an asset-level proxy for it will mislead.

A second instance of the same mistake, mine: when sending the rework I asked for a *tile* mean value
of 0.18-0.22 as a proxy for the frame. The tiles came back at 0.296 and the frame landed at 0.189,
exactly where it should be. The proxy was wrong and the frame measurement was right. Ask for the
frame number.

## Deferred, with the reason

- **Per-block texture repetition.** Terrain UVs are `0..cellSize`, one repeat per voxel
  (`StaticWorldChunkMeshBuilder.cs:275-305`), so every stone block carries an identical cobble
  arrangement and the ground reads as a regular lattice at the isometric camera distance. The user
  saw it and accepted it for now (2026-11-03). A real fix is per-cell UV variation - rotate or offset
  by a hash of the cell index - which is engine work in the chunk mesh builder, not an art change,
  and would want its own gate measuring that neighbouring cells differ.

## A3 was wrong twice, and measuring the target art is what found it

**First version** compared the actor's p99 against the p99 of a ring of ground as wide as the actor.
Fine on evenly lit ground; useless once local light pools exist. MEASURED on the integrated night
frame, the same figure scored -0.10, -0.24 and -0.60 as the ring grew from 20 to 60 to 140 px,
because the ring reaches into a brazier's core. A number that depends that strongly on how far away
you look is measuring the lighting, not the figure.

**Second version** used a thin collar and medians, and counted separation in either direction, since
the user has chosen a dark silhouette over a lit figure. Better behaved - our frame scored 0.063
instead of swinging - but still wrong in premise. MEASURED on the target art with the same
statistic, its own figures score 0.000 to 0.086, median 0.000: most of them do not differ from the
ground they touch at all. That is not quantisation; the frame carries 82 distinct value levels with
a median gap of 0.008, so a zero is a real zero. **A gate the target art fails is a bad gate.**

**What the target art actually does.** Its figures read through internal contrast. MEASURED p90-p10
within a figure: 0.50, 0.50, 0.58, 0.71, 0.77 - bright bone and steel against near-black recesses,
inside the silhouette. Our character spans 0.278, about half. That is why ours reads as a flat shape
and theirs reads as an armoured figure, and it is the thing worth gating.

A3 is now **internal spread >= 0.45**, with the collar separation kept as a reported number. Our
integrated night frame measures 0.278 and fails it, correctly: the figure needs deeper recesses and
brighter highlights, not a different average brightness.

The general lesson, which cost two attempts: **derive a gate from the target, not from intuition.**
Both wrong versions were plausible and neither survived contact with a measurement of the thing we
are trying to match.

## Accepted: 0.23% of the night frame clips

MEASURED. The brazier pool originally clipped 1.52% of the night frame to pure white - a third of the
whole bright tail - and 2.50% of the morning frame. Lowering brazier intensity 3.0x -> 1.68x and
broadening the falloff core (1/(1+6s) -> 1/(1+0.75s)) brought night clipping to **0.23%** and morning
to 0.04%, with A1 still passing at mean 0.119 / near-black 0.595 / bright 0.021 / saturation 0.269.

The remaining clip is **the `FxSimulation` fire halo, not the light**. Confirmed by shifting
`BrazierRadius` off the lamp radius so the FX tag misses: clipping fell to 0.03% with bright 0.022,
but the brazier then has no halo or embers. Reaching under 0.1% while holding bright >= 0.020 is not
possible with the halo as it is - the measured curve is 1.9x -> 0.42%, 1.75x -> 0.28%, 1.68x -> 0.23%,
1.6x -> 0.11%, 1.5x -> 0.03%, and bright falls through the floor by 1.6x.

The user accepted 0.23% on 2026-11-03. Reasons: it is a 6.6x improvement, what clips is fire, and the
alternative is weakening the fire FX to satisfy a number the target art's own compressed capture
cannot be held to precisely.

**Known thinness, recorded rather than hidden.** The bright tail is now 0.021 - on the gate floor and
below the target art's 0.024-0.038 band - and **most of it is the particle halo, not lit geometry**:
the lights alone contribute about 0.004. In the target, bright pixels are fire *and* the surfaces the
fire lights. A1 is therefore being satisfied more thinly than intended. If the baked terrain (darker
than the photo terrain this was tuned against) pulls it below 0.020, the answer is more or better
placed light, not a lower gate.

## MSAA, and one unexplained capture

4x MSAA is on the default framebuffer: no FBO, no alpha-to-coverage, no sample shading. MEASURED:
the driver grants 4 samples; 0x vs 4x captures differ on 0.32% of pixels against a run-to-run noise
floor of 0.155%, **98.9% of the differing pixels lie within 2 px of an edge, and 100% of those
differing by more than 32 levels do**. Interior pixels move at most 15 levels. That spatial
distribution, not the raw percentage, is the proof the change is edges-only. Cost is about 0.085 ms
per frame, roughly 0.055 ms of it in swap where the resolve happens - about 1.2% of a 144 FPS budget,
though the test scene was too light to measure the fill-rate cost properly.

**Unresolved, recorded rather than closed.** The implementer's first 4x run wrote 91-byte 1x1 PNGs,
i.e. the window reported a 1x1 `ClientSize`. It did not reproduce for them, and I then ran three more
4x captures which all produced clean 1440x1024 frames with 4 samples granted. So it is not
reproducible at roughly 1 in 8 runs and is more likely a transient window-creation race than an
MSAA defect - but it was never explained. If capture runs ever start producing tiny PNGs, start here.

Cutout (alpha-mask) edges are deliberately NOT anti-aliased: `discard` kills all samples. Alpha-to-
coverage would fix it and was rejected for this change - it needs fragment alpha to carry the mask
value and dithers coverage noisily on small hard-edged textures. Anisotropic filtering is a separate
follow-up: it would sharpen the over-blurred distant ground, which is an art-direction call, whereas
MSAA is strictly "same image, smoother edges".
