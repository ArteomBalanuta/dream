# Queued: the models wave (graphics polish, assets, skeletons and animation wiring)

Requested 2026-11-03 by the user, to start **only after the current work is merged into master**.
Method: the swarm workflow in `CLAUDE.md` (architect -> parallel implementers -> integrator ->
tester/reviewer), with the engineering discipline in the software-engineering skill.

Scope in the user's words: "polish graphics, generate assets, focus should be on graphics and
model's skeletons/animations wiring, I want models."

## What is already true, so the wave starts from evidence rather than a survey

**The glTF path is proven end to end.** `gltfwrite.py` + `charforge.py` + `gen_char_player.py`
produce a deterministic GLB that the real loader accepts, and it ships as the player. Two props
generators now exist alongside it. Nothing about the export path needs inventing.

**Skinning is built but unused.** `gltfwrite.write_glb` takes both paths - a skin with `JOINTS_0`,
`WEIGHTS_0` and `inverseBindMatrices`, or rigid node-hierarchy animation - and `charforge` has
`bind`/`skin` for two-bone linear blend skinning with recomputed normals. **Every shipped actor is
rigid, not skinned.** The player is rigid on purpose: `PlayerModelAssetDefinition` measures a model's
own authored height and normalises to it, so a rigid part hierarchy drops in with no C# change. The
engine caps skins at 64 joints (`AnimatedModelData.MaxSupportedJoints`). So "skeletons" is a
capability that exists and has never been exercised in-game - that is the single biggest unknown.

**The wolf is the blocker for a second actor.** Its normalisation is a hard-coded `3.191f -> 0.60f`
constant, and its clip is `"AnimalArmature|Idle"`. Replacing it is not the drop-in the player was.

**Clip coverage is thin.** The player carries `idle`, `walk`, `sprint`, `die`,
`attack-melee-right`, `holding-right-shoot`. There is **no strafe or back-walk clip**, which is why
the cursor-aim work counter-rotates the lower body and clamps at +/-90 degrees, leaving an honest
artifact for straight-backwards walking. `PlayerAnimationInputs` carries only a scalar
`HorizontalSpeed` with no direction, so directional clips need plumbing as well as art.

**The measured art gap, already gated.** A3: the figure's internal contrast is about **0.21 against
a 0.45 floor** derived from the target art's own figures (0.50-0.78). It reads as a flat silhouette
rather than an armoured figure. The fix is deeper recesses and brighter highlights inside the
silhouette, not a different average brightness - a frame at 0.137 rose to 0.208 purely from brighter
braziers, so lighting moves it but cannot close it.

**Held items are in blocky units, not metres.** `HeldWeaponRenderer` puts the fist at (+/-0.2, -0.9)
in the old blocky model's frame, so `gen_char_player.py` exports at 0.088 per authored unit. Any new
actor that holds anything must match that convention or its weapon floats.

## Candidate workstreams, for the architect to cut and challenge

1. **Close A3 on the player** - internal contrast to 0.45+, measured by `check_look.py --actor`.
   Pure art-and-bake work on a proven pipeline; the gate already exists.
2. **Exercise skinning for the first time** - take one actor through `charforge.bind`/`skin` and the
   skinned `gltfwrite` path, and prove it loads, deforms and animates in-engine. This is where the
   unknowns are: inverse bind matrices, joint limits, and whether the engine's normalisation story
   works for a skinned mesh.
3. **A second actor** - the wolf replacement, which needs the hard-coded normalisation addressed,
   or a new enemy that avoids it.
4. **Directional clips** - strafe and back-walk, plus the `PlayerAnimationInputs` direction
   plumbing, retiring the leg-twist clamp. Clips are generated, so the art may be cheap; the
   plumbing is the cost.

## Standing constraints the wave must not breach

- Original art only. Two provenance errors have already been fixed this session; a third would be
  carelessness. The one deliberate exception is the vendored OFL HUD font, accepted by the user.
- numpy + pillow only in `tools/ui-assets/`, pinned, and `check_sources.py` must stay at 0 problems
  without being weakened.
- Byte-determinism for every generator, and a negative self-test for every new gate.
- No framebuffer objects; runtime shadows and bloom remain out of scope.
- Bake **view-independent response only** - albedo, AO, cavity, wear. No directional key, ever: the
  engine lights actors itself and the sun moves.
- Dark, grim, gothic; the user dislikes bright, saturated colour.
