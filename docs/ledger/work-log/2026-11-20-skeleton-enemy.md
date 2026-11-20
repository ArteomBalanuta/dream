# Work log: the risen skeleton enemy

- Purpose: a fifth enemy kind, forged end to end — model, animation, content, persistence, client look.
- Status: on `integrate/skeleton-enemy`, not merged into master.
- Plan: `docs/ledger/plans/2026-11-20-skeleton-enemy-plan.md`.
- Method: the swarm workflow in `CLAUDE.md` with `parallel-agent-delivery` — contracts frozen first,
  three implementers on disjoint files, then integration, a domain-split review and measurement here.

## What shipped

**The model (`ws/skeleton-forge`).** `tools/ui-assets/gen_char_skeleton.py` forges
`dream/animations/enemies/skeleton.glb`: one skin, **18 joints** against contract C3's cap of 32,
3,702 triangles, 554,624 bytes, a single 256 px baked atlas, and four bare clips — `idle`, `walk`,
`attack`, `die`. It is **the first forged BIPED enemy**: it takes its structure from the hound
(skinned, `_quat_from_iso`, the grounding drop, the root channel built from the grounded skeleton)
and its humanoid proportions from the player and the villagers, so it is now the worked reference
for any further bipedal creature. Unlike the beasts, which share the wolf's 0.60 world height, it
normalises to the **player's 1.20**, because it is player-shaped. The blade is part of the mesh
rather than a held item: a skinned actor can carry no pose-layer contract, since node overrides
resolve by renderable node name — the same reason the hound's collar is geometry.

**The kind (`ws/skeleton-content`).** `RisenSkeleton`, level 6, appended **last** in `enemies.json`,
with two night frontier camps, a `risen-skeleton` loot table and migration `0018`. No enum, no
protocol bump, no change to `EnemyFactory` or the spawn policy.

**The look (`ws/skeleton-client`).** An `enemy.skeleton` entry in `EnemyVisualCatalog` at scale 1 and
no tint, its own `EnemyClipSet` with one gait, the `ModelAssets` path and the `PlayScene` preload.

## The three defects that mattered, and what each one teaches

### 1. A tier's numbers are a sequence, not a set

The frozen stat row was wrong twice, and **both times the track building against it caught the
error** rather than implementing it. First, `attackSpeed` was authored as an unrelated number when
`EnemyKindsMigrationTests:54` pins `attackSpeed == 1 / attackIntervalSeconds` to five decimals, which
all four shipped kinds satisfy exactly. Second — and more interesting — the row chased at 3.4 with a
1.45 s interval, both of which sit on the **wrong side of the level-5 Ashen Prowler**, contradicting
the rule `EnemyTemplate.cs` states in the very comment the change was editing: chase speeds rise with
the tier and intervals fall with it.

Every individual number was legal. `patrol <= 1.6`, `chase >= 2.0`, `chase` inside the player's
cruise-to-sprint window, `patrol <= chase`, `attackRange` above the shove threshold — all five gates
passed. The row was individually legal and **collectively wrong**, because inserting a tier into an
ordered sequence is only correct if every column still reads monotonically. The shipped row
interpolates between the Prowler (5) and the Grave Howler (8) in all of: level, strength, vitality,
attack speed, aggro, chase, patrol, interval, health and gold. `attackRange` 1.5 is the deliberate
exception — this is the first enemy that swings a blade instead of biting, and reach is the one thing
that should differ.

### 2. The gates measure everything about the model except whether it looks like one

The first forge passed all seventeen gates — determinism, the shipped bytes, the byte ceiling, the
joint cap, weight sums, a 62% blend share, CPU skinning agreeing with glTF forward kinematics to
3.9e-07 with a live negative at 23 units, grounding, the palette, the M3 spread with a
plates-removed control — and **it had no skull**. The head was a stack of flat octagonal plates with
no cranium, no sockets and no jaw.

One absent feature produced three symptoms, and the third is the one worth remembering:

- at gameplay size it read as a straw hat, so the ribcage was carrying the whole identity;
- the pauldron sat close enough to twin it, and one attack frame read as a two-headed figure;
- **in `die` the head vanished entirely**, because a flat disc seen edge-on is a line — and the held
  final frame of `die` is what the player looks at for the whole death linger.

The corpse gate passed throughout, reading a lowest vertex of -0.01 u. A gate on the lowest vertex
cannot notice that the corpse has no head. This is the house rule in its purest form: *a green suite
does not show a sword pointing at the sky.* The fix was form, not brightness — a cranium volume, a
brow, dark recessed sockets, an open jaw, and the pauldron moved outboard and down.

The re-forge added two gates that would have caught it: the head's bind-pose bounding extent against
a floor (a flat-plate head reads 0.30 u against the 2.5 floor), and the head's on-screen coverage in
the **held `die` pose** specifically (1.79 u against a 2.8 floor). Neither measures beauty. Both make
it impossible to flatten the skull back into discs without the gate saying so.

### 3. One clip cannot have two reference speeds

`EnemyClipSet` carries a `MoveReferenceSpeed` and a `SprintReferenceSpeed`, and the skeleton has one
gait, so its Sprint **is** its Move. The integration brief told the client track to set the sprint
reference to the template's chase speed of 3.6 — substituting a *simulation* speed for a
*clip-authoring* speed. They are not the same quantity, and the file's own header says so: a
reference speed is "the ground speeds the wolf's walk and gallop clips were authored for".

The failure is concrete. `EnemyVisualState` snaps the speed it forwards — `min(speed, 1.6)` on the
walk gait, `max(speed, 2.0)` on the sprint gait — so a skeleton accelerating into a chase crosses
2.0, the latch flips, and the forwarded speed steps 1.6 to 2.0. With two references that is a rate of
`1.6/1.4 = 1.143` followed by `2.0/3.6 = 0.556`, clamped to 0.600. Because Sprint and Move are the
same string there is **no clip rename**, so `ApplyClip` never resets clip time and nothing hides it:
the legs simply halve their rate mid-stride, at the start of every chase, which is the most-watched
moment an enemy has. For the wolf and the hound the identical step is masked by the walk-to-gallop
swap.

The fix needed a number nobody had measured. The forge supplied it: a stride of
`2 * 2 * (6.61 + 6.41) * sin(20 deg) = 17.812 u` over a 0.88 s cycle, on a figure 29.136 u tall
normalising to 1.2, which is **0.8337 world units per second**. At the shipped patrol speed of 1.25
that resolves to a rate of 1.50 and the feet match the ground exactly, where the inherited 1.4 skated
by 40%. It lives in C# as `SkeletonAssetDefinition.WalkAuthoredGroundSpeed` with its derivation.

Note for whoever touches this next: the hound's `SprintReferenceSpeed` of 6.0 was itself set from a
chase speed, and its comment names "the Grave Howler's 6.0" when that kind actually chases at 3.8.
The existing values conflate the two quantities. Only the skeleton's is measured.

## A gate of my own that could not fail

Writing the test for defect 3, the obvious assertion was that `reference * rate == patrolSpeed` —
"the feet match the ground". It passed. It also passed with the wrong reference, and with the wolf's,
because `ResolvePlaybackSpeed` *divides* by the reference: the product is an identity for any
reference at all whenever the rate is unclamped. The liveness check written beside it is what
exposed that, by failing where it was supposed to pass.

What replaced it is something that can be false: the patrol speed must resolve **strictly inside**
the clamp band, since only an unclamped rate can put the feet on the ground, with a negative that
pushes the same rate onto a bound. The lesson is the repo's own and I relearned it the hard way: a
gate's negative control is not paperwork, it is the only thing standing between a measurement and a
tautology.

The review found the same shape in the forge's new clip-set gate — `want | {"gallop"}` compared
against `want`, which is unreachable for a strict superset, so the real comparison was never run on
bad input and the `ok(...)` printed unconditionally. Both are fixed; both were written by someone who
knew the rule.

## Measured

| | before | after |
| --- | --- | --- |
| enemy kinds | 4 | 5 |
| skeleton M3 spread (floor 0.50) | 0.575 | **0.688** |
| skeleton palette `v_p99` | 0.502 | 0.502 |
| held `die` lowest vertex | -0.01 u | **+0.00 u** |
| `attack` worst floor dip | -0.979 u | **-0.20 u** |
| `die` per-key lowest vertex | 0, -3.0, -3.7, -4.9, -0.01 | **0.0 at every key** |
| playback rate across the gait latch | 1.143 then 0.600 | **monotonic, no drop** |
| full suite | 10,226 / 0 | **10,456 / 0** (47 skipped) |

The palette number is worth stating plainly because it was raised as a concern: `v_p99` 0.502 is the
*baked atlas* percentile, which is a different measurement from an authored channel value. Two
shipped villagers read 0.549 and the hound 0.439, so the skeleton sits inside the band four actors
already occupy. Every authored `MATS` colour is inside the 0.40 bound — the brightest is `bone.hi`
at 0.201 linear, against `bone` at 0.147 which it shares verbatim with the hound.

## Looked at, in the real game

`check_actors.py` measures spread, palette, bytes, skinning and grounding. None of that is a picture,
so the wave was also captured in the engine with `capture.ps1`:

- **Night, staged**: `dev:clear;spawn:RisenSkeleton:1:3` answers `[dev] spawned 1 RisenSkeleton near
  5.0,5.0`, the nameplate reads **Risen Skeleton / Lv 6 520/520**, and the figure stands and holds
  its blade.
- **Noon, beside a Grave Howler**: the biped-against-quadruped scale relationship reads correctly —
  a standing figure at roughly player height next to a low hound, which is the point of the 1.20
  world height against the beasts' 0.60.
- **Offline, every clip key** rendered through the same chain `gen_char_preview.py` uses: the skull
  reads at gameplay size, the pauldron is a separate mass at all four yaws, the attack arc is legible
  (neutral, blade up, cut across, follow-through, recover), and the corpse lies flat with its skull at
  the leading end.

One trap worth repeating: **`capture.ps1` without `-FromSource` runs a prebuilt
`artifacts/capture-bin` whose content JSON can predate your change.** It answered
`[dev] error: Unknown enemy 'risenskeleton'. Known: Wolf, DireWolf, AshenProwler, GraveHowler.` — a
message indistinguishable from a real content bug. The same script with `-FromSource` spawned it.

## Deliberately not done

- **The enemy body stays global.** `WolfDefinition` hard-wires collision radius 0.30, height 0.60,
  anchor 0.75 and stand-off 1.20 for *every* enemy, and `EnemyFactory` has no per-kind branch. A
  skeleton is player-shaped and a wolf's capsule is a fair fit, so this wave did not open it. A kind
  that genuinely needed a different body would have to turn that constant set into per-template
  fields — which is a simulation refactor, not a content addition.
- No second gait. One walk, aliased as the sprint, which `EnemyClipSet` explicitly sanctions.
- No undead damage type, resistance, or combat skill.
- No new golden scene. The staged scenes cover the wolf; a skeleton scene would need its own
  baseline and belongs with whatever wave next re-records them.

## Open items

- `tools/ui-assets/tests/check_sources.py` **exits 1 on master**, with three unclassified
  `enginelook.py` shader reads. `enginelook.py` is byte-identical to `16c17f01`, so this wave did not
  cause it — but it means this wave's `ALLOWED_MODULES` addition has never been seen by a green run
  of that gate, and a permanently red gate is one nobody reads. Worth its own small fix.
- The skeleton's `walk` is the only clip in the game with a measured authored ground speed. The
  wolf's 1.4 and the hound's 6.0 are not measured, and the hound's comment misstates where its number
  came from.
- `EnemyKindsMigrationTests.RunningTheScriptAgain_KeepsAHandEditedRow` re-runs `0017` only, so
  `0018`'s `ON CONFLICT DO NOTHING` — the behaviour its own header advertises — is never exercised.
- `SkeletonAssetDefinition.TargetWorldHeight` reaches through `PlayerModelAssetDefinition`, whose
  bounds measurement has no try/catch, so a missing `player-grim.glb` would throw out of the
  skeleton's scale and the `Lazy` would cache the exception. This copies `NpcAssetDefinition` exactly
  and is pre-existing precedent, not a new mistake.
