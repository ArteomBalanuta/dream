# Models and rigs: original actors, real rigs, and an honest A3

Committed before the first implementer launched, deliberately. Agents work in worktrees, where an
untracked file does not exist; last round an untracked acceptance file meant every agent verified
against a paraphrase in a task brief instead of against the criteria. Never again.

Claim tags: **MEASURED** = run and observed. **VERIFIED** = read in the code. **DERIVED** = computed
from one of those.

## What this wave is for

Put real, rigged, animated original characters on screen — a model-backed villager, the first
original skinned creature, directional player clips — and move actor legibility by the mechanism
that measurably controls it, with A3 restated so it measures something the engine can reach.

## Four findings that changed the plan

### F1. "Skinned animation has never been exercised" is FALSE

MEASURED, by parsing the shipped GLB's JSON chunk:

```
dream/animations/enemies/wolf.glb
  skins 1, joints 49, nodes 68, animations 24
  5 primitives, every one carrying JOINTS_0 + WEIGHTS_0
```

VERIFIED: `AnimatedModelDataTests.cs:122` asserts the wolf's `SkinJoints` is non-null, and
`GltfSkinningAnalysisTests` analyses the wolf specifically. The engine's skinned path —
`LoadSkinning`, `ComposeSkinJointMatrix`, the 64-slot `jointMatrices[64]` uniform in
`cubeVertex.glsl` — is shipped, proven and under test.

The real unknown is narrower: whether **our Python exporter** emits a skin the engine accepts.
`gltfwrite.py` already has a two-bone skinned self-test. An implementer told this is "the biggest
unknown" will over-engineer it.

### F2. The player must NOT become skinned

VERIFIED. `AnimatedModel.RenderPose` (`AnimatedModel.cs:332`) looks up node overrides by
**renderable** node name — a node that owns mesh primitives — and `PlayerPoseLayers.Resolve`
(`PlayerPoseLayers.cs:149`) resolves `torso`/`head`/`arm-left`/`arm-right`/`leg-left`/`leg-right`
the same way. In a skinned glTF the mesh hangs off one node and the joints are not renderable, so
on a skinned player every one of these silently degrades to identity:

- the hit flinch and its red flash
- the airborne leg tuck
- the raised held-item arm
- the leg twist that stops strafe moonwalking
- **held weapons and shields entirely** — `TryGetArmMatrix` returns false when `_rightArm < 0`, so
  `HeldWeaponRenderer` draws nothing

Nothing throws. It is an enforced decision already: `check_char.py:107` fails with *"the GLB carries
a skin; the player must stay a rigid part hierarchy"*. **The player stays rigid and that gate stays.
Skinning goes on actors with no node-name contract.**

### F3. A3 at 0.45 is a LIGHTING number, not an art number

VERIFIED, the whole actor transfer function, and there is **no tonemap and no gamma encode** in any
shader (grepped: zero hits):

```glsl
vec3 lit = (dreamAmbient(n)*uLightingParams.z + (dreamDirect(n) + dreamPointLights(n,fragPos))*0.75) * baseColor.rgb;
lit = max(lit, baseColor.rgb * uLightingParams.y);
FragColor = vec4(dreamApplyFog(dreamExpose(lit) + emissive, fragPos), baseColor.a);
```

`dreamExpose(c) = c * uLightingParams.x`, exposure is 1.0 at every keyframe. So **output = albedo x
gain**, strictly linear, and therefore `p90-p10` of the output is the albedo's spread **times the
gain**.

MEASURED, the shipped player's linear albedo over 192,913 covered atlas texels: p10 0.0266, p50
0.1369, p90 0.5059, **spread 0.4793**. The albedo already clears the 0.45 gate. The art is not the
bottleneck.

DERIVED, applying the real formula with the real night constants:

| lighting situation | spread |
| --- | --- |
| night, open ground (ambient + moon) | **0.059** |
| night, held torch 0.4 m, lambert 1.0 | **0.351** |
| night, held torch 0.4 m, lambert 0.0 | **0.093** |
| night, brazier 1 m, lambert 1.0 | **0.635** |
| night, brazier 2 m, lambert 1.0 | **0.573** |
| night, brazier 4 m, lambert 1.0 | **0.377** |
| night, brazier 1 m, half lit / half shadowed | **0.191** |
| day noon, full sun, lambert 1.0 | **0.682** |

**Correction 2026-11-04, MEASURED against the shaders:** the spread column above is **not reproducible
from C4's own formula and constants** and should not be used as a source of absolute numbers. C4's
constants were re-derived independently and match (moon intensity 0.0660, actor floor 0.0245), and
the table's *ratios* between distances match the window function to three decimals — but its
point-light term is uniformly **0.603x** what C4 gives (a brazier at 1.5 u with lambert 1.0 yields a
red-channel gain of 2.08, where the table implies 1.325 at 1 m). `enginelook.py` is built to the
shaders, not to this table. Nothing downstream depends on the absolute values **except M1's "0.45
allows partial coverage" headroom claim**, which must be re-checked against a real capture.

Also: the shader clamps where this prose does not. C4 writes the point window as `(1-s)^2/(1+1.5s)`;
`pointLights.glsl:18` writes `clamp(1-s,0,1)^2/(1+1.5s)`. They differ only past the radius, where the
shader is 0 and the prose is positive.

DERIVED, the albedo spread you would need to output 0.45 at a uniform gain:

| gain | needed albedo spread |
| --- | --- |
| 0.072 (night ambient) | 6.25 — impossible |
| 0.300 | 1.50 — impossible |
| 0.845 (full day ambient) | 0.533 |

So:

1. **In open ground at night no figure of any albedo can score above about 0.06.** "Move 0.2314 to
   0.45" is not achievable as a property of the figure. The measured 0.2314 was low because the
   figure stood in a dim patch — the wasd verification record says exactly that.
2. What moves A3, in measured order of effect: **(a) a strong local light close to the figure**;
   **(b) the distribution of N·L** — the same brazier gives 0.635 facing it and 0.191 half-shadowed,
   so bimodal normals (broad plates that catch light, deep undercuts that do not) are worth 3.3x;
   **(c) emissive**, which is added after `dreamExpose` and bypasses the gain entirely; **(d)
   albedo, already done.**
3. The figure is built from 6–12-sided elliptical lofts, which give a smooth lambert ramp and a
   mid-heavy histogram. That is the art-side half of the fix.

### F4. Other verified facts

- **NPCs are two coloured boxes** (`WorldEntityRenderer.CollectBoxes`): a `(0.7,1.3,0.5)` robe cube
  plus a `0.34` head cube, standing in the new spawn village. `ClientNpc.VisualKey` is already
  replicated, so a model-backed NPC needs **zero server change**.
- **`EnemyVisual.ModelPath` is dead.** `EnemyRenderer.Draw` calls `EnemyRenderState.CreateWolf`,
  which hard-codes `WolfAssetDefinition.ModelPath`. A second enemy model needs that one indirection
  opened.
- **Four enemy templates, one model.** `Wolf`, `DireWolf`, `AshenProwler`, `GraveHowler` all resolve
  to the wolf mesh. A new model is pure client art plus catalog.
- **`HeldLightCatalog.TorchIntensity = 1.6`** against the brazier's `3.0`. Raising it is a
  one-constant change worth 0.351 -> ~0.6 DERIVED.
- **The held-pose capture gap is real**: `PlayScene.Capture.cs` sets `_captureWritesPng = false` for
  `HudCaptureVerb.Keys`, so no PNG is ever written while a movement key is held. That is why B3's
  moonwalk criterion was unverifiable last round.
- **`ComposeSkinJointMatrix = inverseBind * jointWorld * inverse(meshWorld)`** and `RenderPose` then
  uses `meshWorld` as the model matrix, so a skinned mesh node's own transform cancels out.
- Prop budget: `dream/animations/props` is 13 MB, `dead_oak.glb` 3.3 MB, the player 312 KB. New
  actors get a byte ceiling so this cannot drift the way props did.

## Acceptance criteria

**M1 — A3 at a defined light. MEASURED.** One capture at 22:00 with the player within 2.0 m of the
spawn-village brazier, cursor latched by `aim`, actor crop taken from the renderer's own logged
screen bbox and not typed by hand: `check_look.py --actor` reports **internal spread >= 0.45**.
DERIVED headroom: the same brazier at 1–2 m scores 0.635/0.573, so 0.45 allows partial coverage.

**M2 — A3 in open ground, reported with NO floor. MEASURED.** Same figure, 22:00, open ground, same
protocol. Expect 0.06–0.20. **Recorded, not gated.** DERIVED: the night actor gain is ~0.072, so no
albedo takes this above ~0.06 without a local light. Stating the number is the deliverable; a floor
here would be a gate the engine cannot pass.

**M3 — bimodal lambert. MEASURED, offline.** A generator-side gate shades the figure's own geometry
with the engine's exact actor formula (C4) at a brazier 1.5 m away and measures `p90-p10` over the
silhouette. Floor **>= 0.50** — above the current figure, below the 0.635 full-coverage ideal.
Negative self-test required: the pre-change geometry must fail it.

**M4 — the first original skinned actor loads, deforms and animates. MEASURED.** A forged skinned
creature with `<= 32` joints, one skin, every primitive skinned, weights summing to 1.
`GltfSkinningAnalysis.Analyze` reports `HasSkins` and `HasSkinnedPrimitives` with the right joint
count; `AnimatedModelData.Load` returns non-null `SkinJoints`; a C# test asserts a posed vertex
actually moves (joint matrices at t>0 differ from bind pose). Visible in a capture.

**M5 — villagers are figures, not boxes. MEASURED.** `WorldEntityRenderer` emits no NPC boxes; spawn
NPCs draw a forged animated model keyed off `ClientNpc.VisualKey` with an idle clip advancing.
Verified in a capture. No server or protocol change.

**M6 — directional clips retire the leg-twist clamp. MEASURED.** The player carries `walk-back`,
`walk-strafe-left`, `walk-strafe-right`; the state machine selects by travel-minus-facing yaw per C2;
`MaxLegTwistRadians` drops to a within-sector residual of `<= 45 degrees`. Verified from a
**held-key** PNG, which needs M7.

**M7 — the harness can see a held pose. MEASURED.** A new capture step writes a PNG while movement
keys are still held, and the capture logs the player's projected screen bbox. Prerequisite for M1,
M2 and M6.

**M8 — nothing regresses.** `check_sources.py` 0 problems without weakening the whitelist; every new
and changed generator byte-deterministic across two runs; `check_char.py` passes **including its skin
ban**; A1 still passes at the M1 capture (mean v <= 0.18, near-black >= 0.45, bright >= 0.020,
saturation <= 0.38) at a **declared** `--hud-rows`; build 0 errors; no new test failures against
master's 32 distinct failing names.

## Non-goals, each considered

- **No tonemap or gamma encode.** DERIVED: an sRGB encode would take night open ground from 0.059 to
  ~0.197 and the torch case from 0.351 to 0.478 — it would nearly close A3 by itself, **and it would
  destroy A1**, whose 0.66 near-black share exists only because the frame is linear. This is a
  separate product decision; do not absorb it into this wave.
- No framebuffer objects, no runtime shadows, no AO, no nearest filtering (hybrid-bake non-goals).
- No skinned player (F2).
- No prop decimation.
- No protocol change. Everything rides existing replicated fields.

## Decisions taken for this wave, so nobody has to guess

- **M2 is reported, not gated.** See Q1 below; the tonemap question goes to the user separately.
- **Villagers match the player's height** (1.20 m) rather than the 1.64 m boxes they replace.
- **The default loadout is unchanged** — no torch added. It would change the opening minute.
- **`enemy.hound` goes on `GraveHowler` only.** The wolf stays for the other three as a fallback
  until the hound is proven in play.

## Shared contracts

**C1 — rigid actor node names.** Exactly these, in this parent order, resolved by **renderable** node
name (a node carrying mesh primitives; one without is invisible to the lookup):

```
root -> torso -> head, arm-left, arm-right
root -> leg-left, leg-right
```

**C2 — player clip names and selection.** Existing: `idle`, `walk`, `sprint`, `die`,
`attack-melee-right`, `holding-right-shoot`. New, exact: `walk-back`, `walk-strafe-left`,
`walk-strafe-right`. Selection by `delta = atan2(travelX,travelZ) - atan2(facingX,facingZ)` wrapped
to `(-pi,pi]` — the quantity `LegTwist` already computes before clamping:

| `abs(delta)` | clip | residual twist to the pose layers |
| --- | --- | --- |
| `<= 45 deg` | `walk` | `delta` |
| `45..135`, delta > 0 | `walk-strafe-right` | `delta - 90 deg` |
| `45..135`, delta < 0 | `walk-strafe-left` | `delta + 90 deg` |
| `> 135` | `walk-back` | `delta -+ 180 deg` |

`MaxLegTwistRadians` becomes `pi/4`. `sprint` keeps one clip and the existing clamp.
**`PlayerAnimationFrame.LegTwistRadians` carries the RESIDUAL, not the raw delta** — the clips and
the state machine must agree here or the legs double-count.

**C3 — skinned export contract.** One skin; `joints <= 32` (engine limit is 64, and above it
`LoadSkinning` silently returns null and the mesh draws in bind pose — a silent failure, so stay well
under); every primitive carries `JOINTS_0` (uint16 VEC4) and `WEIGHTS_0` (float32 VEC4, rows summing
to 1); `inverseBindMatrices` in **exported space** — iso `(x,y,z)` -> glTF `(x,z,y)`, scaled by
`UNITS_PER_U`, consistently with vertex positions and node translations. The bind pose has no
rotation, so the inverse bind is a pure negated translation; keep that true. Per-joint node overrides
are **impossible** on a skinned model (F2) — never give a skinned actor a pose-layer contract.

**C4 — the engine's actor transfer function.** Stated once, identically, in `enginelook.py` and here:

```
G    = ambient(n)*ambientScale + (direct(n) + points(n,p)) * 0.75
out  = clamp(max(albedo*G, albedo*floor) * exposure + emissive, 0, 1)
ambient(n) = mix(ambientGround, ambientSky, n.y*0.5 + 0.5)
points: s = d^2/r^2 ; f = (1-s)^2 / (1 + 1.5*s) ; contribution = colour * f * (0.25 + 0.75*max(dot(n,l),0))
albedo = texel/255 * baseColorFactor        # the texture bytes ARE linear albedo
```

Night constants, VERIFIED: `ambientSky (0.016,0.024,0.048)`, `ambientGround (0.006,0.007,0.013)`,
`ambientScale 1.5`, `floor = 0.35*0.07 = 0.0245`, `exposure 1.0`, moon tint `(0.60,0.70,1.00)` x
intensity `0.0660`. Brazier `(1.0,0.72,0.46)*3.0` radius `10.0`. Torch `(1.0,0.62,0.32)*TorchIntensity`
radius `8.0`. **No tonemap, no gamma encode** — the captured byte is the clamped linear value.

**C5 — asset paths and visual keys.**

```
dream/animations/players/player-grim.glb      existing, rigid, nodes per C1
dream/animations/enemies/wolf.glb             existing, 49-joint skin, UNTOUCHED this wave
dream/animations/enemies/hound.glb            new, skinned, clips: idle walk attack die
dream/animations/npcs/villager-vendor.glb     new, rigid, clips: idle walk
dream/animations/npcs/villager-smith.glb      new, rigid, clips: idle walk
dream/animations/npcs/villager-warden.glb     new, rigid, clips: idle walk
```

Keys: `enemy.hound` (new, `GraveHowler` only), `npc.vendor` (already in `npcs.json`), `npc.smith`,
`npc.warden`. **Unknown, null and oversized keys must resolve to the default** — follow
`EnemyVisualCatalog.Resolve`'s length-first pattern; these come off the wire. The hound's clip names
are bare (`idle`, not `AnimalArmature|Idle`).

**C6 — new capture vocabulary.**

```
keysnap:<keys>:<frames>   hold keys through the real mapping for N frames, write the PNG on the LAST
                          HELD FRAME, then release
status                    additionally logs:  actorbox <x0>,<y0>,<x1>,<y1>
check_look.py --actor-from-log <out.log>      reads that line instead of a hand-typed crop
check_look.py --hud-rows                      now defaults to 96
```

## Risks

- **R1.** The queued plan's central premise is false (F1). Skinning is shipped and tested.
- **R2.** The original headline target was unachievable as stated (F3). Chasing "better albedo
  contrast" would produce a better figure and move A3 by almost nothing — the same shape of error as
  the two previously rejected A3 definitions.
- **R3.** A skinned player would silently break five shipped features (F2), held weapons included.
- **R4.** The inverse-bind space conversion is the single most error-prone step: `bind_world()` is in
  iso-axis `u`, the export is glTF-axis `UNITS_PER_U`, and nothing currently exercises the
  conversion. A wrong one explodes or collapses the mesh on the first non-rest frame. Mitigation:
  assert CPU skinning agrees with a glTF-space forward-kinematic evaluation, as `check_char.py`'s
  `fk_vertices` already does for the rigid path.
- **R5.** Villager height: the boxes are 1.64 m, the player is 1.20 m. Decided above: match the
  player. See Q2.
- **R6, and its premise was wrong in both directions.** A1's bright share is thin (0.022 measured
  against the 0.020 floor) and moves with the HUD crop, which is why `--hud-rows` now defaults to 96.
  But MEASURED: **an emissive accent does not help it at any sane size** — the player's eye sockets
  own 67 of 15,388 silhouette pixels at value 0.226-0.241, and `BRIGHT_V` is 0.60, so they contribute
  exactly zero. (The brazier's coals at 0.30 contribute nothing either; its bright pixels come from
  the fire FX.) And **the plate geometry moves the bright share UP, not down**: the reworked figure
  owns ~1000 pixels above `BRIGHT_V` where the old one owned essentially none, because deep undercuts
  only dim pixels that were already far below 0.60 while plates create pixels above it. Size the
  emissive accent on art grounds, not for A1. If the bright share does drop below the floor the
  answer remains more or better-placed light, not a lower gate.
- **R7.** Raising `TorchIntensity` is world-wide — every remote player's torch brightens. Changing
  the **intensity** is safe; changing the **radius** is not, because `BrazierRadius == LampRadius ==
  10f` is what `FxSimulation.TryClassify` matches for the fire halo and the torch's `8f` deliberately
  misses it.
- **R8.** Captures must be strictly serial (one `dream.exe`; the first locks the world). Reuse one
  `DREAM_IDENTITY_FILE`; a fresh one gets `Join rejected` in **stdout**. Server startup failures go
  to **stderr**. No colons in filenames. Builds need an absolute Windows `BaseOutputPath` inside the
  worktree, set from PowerShell.
- **R9.** Asset size: three NPCs plus a hound at the player's budget add ~1.5 MB. `check_actors.py`
  carries a per-asset byte ceiling.

## Decisions, all four taken

Resolved 2026-11-04 rather than referred upward. Reverse any of them if the measured result argues
against it; the reasoning is recorded so that argument can actually be had.

- **Q1 — NO tonemap. The figure reads when lit, and the game puts light where the player goes.**
  A tonemap would nearly close A3 by itself (DERIVED: open ground 0.059 -> ~0.197, torchlit 0.351 ->
  0.478) but it would destroy A1, whose 0.66 near-black share exists **only** because the frame is
  linear. That near-black *is* the grim look this project chose deliberately and tuned twice. Trading
  it for actor legibility would be trading the thing that works for the thing that is missing. The
  linear frame stays; legibility comes from light placement, which is cheaper, local and reversible.
  **A1 is load-bearing; A3 is a deficit. Do not pay for the deficit with the asset.**

- **Q2 — villagers and the player stay at 1.20 m.** Changing the height touches
  `WolfAssetDefinition.Normalization`, which is also the player's basis, so it would move the player,
  every enemy and the camera framing to fix a complaint nobody has made. The 1.64 m NPC boxes were
  never authored as a height decision; they were placeholder cubes.

- **Q3 — YES, the default loadout carries a torch.** This follows directly from Q1 and is the half of
  that decision that costs something. Having chosen a world where a figure reads only when lit, it is
  incoherent to start the player in the dark with no light source: at 22:00 in open ground with the
  default loadout the figure measures a 0.059 spread, which is a near-invisible smudge, and the
  nearest fix would be the tonemap just rejected. A hooded figure carrying fire also suits the art
  direction. It does change the opening minute of the game, which is exactly why it is written down
  here.
  Ownership: `ViewModelAssets.DefaultLoadout` is **assigned to WS-2** for this wave; no other
  workstream may touch it.

- **Q4 — keep the wolf this wave; `enemy.hound` goes on `GraveHowler` only.** Retiring the
  third-party Quaternius mesh is the right direction of travel, but doing it in the same wave that
  first exercises our own skinned exporter would leave no fallback if the forged skin is wrong — and
  R4 says a bad inverse-bind conversion fails silently rather than loudly. Prove the hound in play,
  then retire the wolf in a follow-up whose only job is that swap.
