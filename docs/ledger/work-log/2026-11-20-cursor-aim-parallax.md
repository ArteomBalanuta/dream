# Work log: the cursor aim and the enemy on higher ground

- Purpose: finish the user's melee bug. "Hitting it with normal attack doesn't land — nothing happens!
  using skill it works." The mode half of that was fixed earlier today (Combat is now a new player's
  default, `27c96b56`); this is the other half, which only shows on sloped ground.
- Status: on `master`, uncommitted at the time of writing; see the commit that carries this file.
- Method: measured the geometry first, then one change, then a domain review of the diff and a second
  pass on its findings.

## The bug

`CursorAimController` turns the cursor into a yaw-only facing by cutting the cursor ray with the
horizontal plane through the player's body. That cut is exact only for a target standing at the
player's own height.

Under the isometric camera — pitch 50°, yaw 45°, `IsometricView` — a target raised by `h` metres draws
`h / tan(50°) = 0.84h` metres further along the camera's ground direction. The ray through its sprite
therefore meets the player's plane that far **past** it, and the facing points beside the enemy.

What decides a hit is not the yaw error but the lateral miss: `AuthoritativeCombatResolver`'s flat
branch allows `EnemyRegistry.DefaultHitRadiusMetres` = 0.9 m of it. The part of the 0.84h displacement
lying across the line of attack is what eats into that, foreshortened by range.

**The h that matters is not the ground rise.** What the player points at is the enemy's *combat
anchor*, so h is the height of that anchor above the player's body plane. Both cases are now measured
in a running client at the skeleton's 1.5 m stand-off, not derived:

| case | anchor above body plane | plane cut (old) | anchor aim (new) |
| --- | --- | --- | --- |
| level ground | 0.250 m | miss 0.160 m — hit | 0.000 m |
| one-block step | 1.250 m | **miss 1.022 m — MISS** | 0.000 m |

The radius is 0.9 m, so a single block of rise puts the swing 14% past it and misses every time, while
level ground spends about a sixth of the tolerance and is safe. That closes a loop: the commit that
added `MaxMeleeHeightDifferenceMetres` says in its own comment that "the 3D distance made a one-block
step an unhittable target" and fixed the distance half. This was the aim half of the same symptom, still
live. A cleave's 120° arc swallows all of it, which is why skills kept landing on an enemy a normal
attack could not touch.

**I got this wrong twice before measuring it, in opposite directions.** First I treated h as the ground
rise and said "a rise of about 1.34 m misses" — too pessimistic about what a slope does. Then I found
`WolfDefinition.CombatAnchorHeight` = 0.75 m, assumed that was the anchor's height above the *player's*
plane, and said level ground already spent 64% of the tolerance — too alarmist, and wrong by a factor of
3.6. The 0.75 m is measured from the *enemy's own position*, and an enemy's body centre sits about half a
metre below the player's, so the real figure is 0.250 m. Both errors were a wrong zero point, and both
survived a review and a green suite because nothing in either measured the actual game. The capture did,
in one run.

The general form of this trap is in memory as `parallax-zero-is-the-anchor`: when you quantify a
projection error, do not derive the zero point from the constants you happen to find — print it from a
running frame.

## The fix

An enemy the cursor is *on* needs no plane at all: the pick that found it is a ray/actor intersection,
so its combat anchor is where the enemy actually is. The hover probe already makes that pick once a
frame for the cursor art, so the anchor was already in hand — it was simply thrown away.

- `IClickMoveHost.IsEnemyAt` became `TryGetEnemyAnchorAt`, returning the anchor. `IsEnemyAt` is gone;
  nothing asked the yes/no question any more.
- `EnemyHoverProbe.Evaluate` gained an out-anchor overload. One pick per frame, as before.
- `CursorAimController.OnFrame` gained a fifth parameter, `Vector3? aimPoint = null`, and prefers it to
  the plane cut. `PlayScene.Update` hands it the hover's anchor.
- Combat mode only, so Building keeps the facing it always had.
- An anchor nearer than `MinAimMetres` falls **through** to the plane cut rather than being taken and
  then rejected by `TryGetFacing`, which would leave `OwnsFacing` true with no facing to give and stop
  the character turning at all.

`GroundPoint` is now `AimPoint`, because it is no longer always a ground point.

## What the review caught, and what it teaches

Three findings worth keeping.

**1. A rename can kill an allocation-free override silently.** `PlayScene`'s `ClickMoveHost` overrode
`IsEnemyAt` with `WorldCursorPicker.PickHoveredEnemy` precisely because the hover runs every frame: no
closure, no delegate, no target object. When the probe started calling `TryGetEnemyAnchorAt` instead,
the override stopped being called and the per-frame hover fell to the interface default, which goes
through `TryPick` and allocates. Nothing failed: the allocation gate
(`WorldCursorPickerHoverTests.HoverEvaluation_AllocatesNothing…`) measured `PickHoveredEnemy`
**directly**, so it could not fail for the reason it was written. My own comment on the new method had
predicted this and I had not acted on it.

The lesson is about where an allocation gate is pointed. A gate on the primitive proves the primitive;
only a gate on the path the shipped code takes proves the path. `EnemyHoverAnchorTests` now measures
through `EnemyHoverProbe.Evaluate` with a host shaped like the scene's, and pins in source that the
scene's override reaches `PickHoveredEnemy`.

**2. A constant restated in a test cannot pin anything.** The first version of the test declared its own
`PitchDegrees = 50f` and said it was "pinned so a pitch change cannot quietly make this test vacuous".
With the angle restated, changing `IsometricView.PitchDegrees` would change the real parallax and leave
the test passing on its private copy — the comment asserted exactly the protection it had given up. It
now reads `IsometricView.PitchDegrees`, `IsometricView.YawDegrees` and `IsometricView.Distance`. This is
the repo's "derive, do not restate" rule, and the giveaway was that the comment claimed a tripwire.

**3. A deferred snapshot is better passed than stored.** The first version put the anchor in a
`PlayScene` field and served it through a host method that ignored its `screenPosition` argument. It was
correct — every path through the pointer block wrote the field — but the ordering was guarded only by a
test that compared `IndexOf` positions in the source. Passing the anchor as a parameter removed the
field, removed the ignored argument, made the ordering a compile-time fact, and let the source-text test
be replaced by real unit tests.

## Verification

- Full suite green on the shipping code: **10541 passed, 0 failed, 47 skipped**, 7m08s. The Client area
  green after the last test-side edits: 1134 passed, 0 failed. The synthetic negative is real — delete
  the branch and `WithNoAimPoint_ARaisedEnemyIsMissedByMoreThanTheSwingRadius` is the case that was
  shipping.
- **Confirmed in a running client**, `artifacts/captures/aim-parallax`:
  `play,wait:60,dev:clear;spawn:RisenSkeleton:2:3,wait:40,aimenemy,status,holdleft:300,status,snap`.
  The status line reads `aimOwnsFacing True aimedAtTarget True`, so the hover's anchor really does reach
  the aim in the shipped client, and the swing landed three times: `520 -> 482 -> 450 -> 399`. The run
  also re-confirms the Combat default — no mode key was pressed and the log says `mode Combat`.
- **The raised case is confirmed too**, `artifacts/captures/aim-step`. `dev tp` takes an XZ pair and no
  Y, and this world's terrain is flat (every probed column 1.48–1.50 m), so the step is *built*: the
  capture switches to Building, right-clicks the ground one cell out with `buildat`, switches back to
  Combat, and spawns with `spawn RisenSkeleton 1 2` — `DevSpawn` puts the first enemy at exactly +X and
  `TryFindStandable` has only the block's top to choose in that column, so the skeleton lands on it.
  Result: anchor 1.250 m up, plane cut 1.022 m MISS, shipped aim 0.000 m HIT, and the swing landing
  520 → 433 → 393 → 352. That is the user's bug reproduced and the fix verified in the engine.
- The A/B comes from a new capture-only `aimcheck` line (`PlayScene.Capture.cs`), which prints the anchor
  height, the range, and the lateral miss for both aims the way the resolver measures it. It recomputes
  the plane cut from its own copy of the removed formula rather than calling the controller, so the
  comparison cannot quietly collapse to zero if the aim changes again.
- Unexamined by eye: W/A/S/D is projected against the aim facing, so while an enemy is hovered the walk
  direction now follows the enemy's true position rather than the cursor's ground point — up to ~30° of
  shift at a 3 m rise. It matches what click-to-move attack already does, but it is a feel change.
