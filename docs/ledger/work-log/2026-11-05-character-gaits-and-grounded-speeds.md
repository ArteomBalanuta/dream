# Work log: character gaits and grounded speeds

Branch `integrate/character-gaits`, from master `04a8fbfe`. Three parallel workstreams, merged WS-1 -> WS-3 -> WS-2.

## The defect this wave fixes

The player had ONE flat speed of 6 m/s and a sprint clip authored for 9 m/s that the simulation could never produce, so "walk vs run" could not express anything: there was nothing to express it with. The wolf had a single `Move` clip, so a chase and a patrol looked identical. And every enemy moved at 2.8-3.6 m/s against a 6 m/s player, which means no enemy in the game could ever close on a player who simply walked away. The wave gives the player three reachable tiers, the wolf two gaits, and re-bases every character speed against each other.

## The workstreams

- **WS-1 `feat/grounded-movement`** (`83fdb1e6`) - the tuning tiers, the motor acceleration ramp, the `sprintHeld` wire field, LeftShift/Z bindings, the `PathFollower` recalibration, `GameSettings`, and `HudCapturePointerPlan` + `PlayScene.Capture` (`Shift = 2048`, so `keysnap:w+shift:N` works). Merged first because it publishes the tuning constants the other two compile against.
- **WS-3 `feat/enemy-gaits`** (`28f1fcd5`) - `EnemyClipSet` with a Sprint/gallop clip and a speed-driven `Resolve`, observed-speed measurement in `EnemyVisualState`, the `patrolSpeed` content field, the wolf's real `AnimalArmature|Death` clip (wired and correct, but LATENT - see fix loop 2, F7), a forged `gallop` for the hound, `enemies.json`. Merged second because it is independent of the other two.
- **WS-2 `feat/player-gaits`** (`66ad572b`, `65c30f81`) - a new authored `run` clip, a retuned `sprint`, a regenerated `player-grim.glb`, three-tier selection in `PlayerAnimationStateMachine`, three-tier `FootstepCadence`. Merged last because it carries a byte-exact regenerated GLB, the worst thing to resolve in a conflict.

The three file sets were fully disjoint and all three merges were conflict-free.

## The speed tables

Player, in `PlayerSimulationTuning` (`dream/Simulation/Players/PlayerCommands.cs`):

| Tier | m/s | How it is reached |
| --- | --- | --- |
| Walk | 1.6 | `DefaultWalkSpeed`, the floor a feathered throttle cannot crawl below |
| Run (cruise) | 4.3 | `DefaultRunSpeed`, `MoveSpeed`, the new default at full throttle |
| Sprint | 6.0 | `DefaultSprintSpeed`, `SprintHeld` |

`DefaultGroundAcceleration` is 18 m/s^2; the motor ramps toward the target instead of snapping to it.

Enemies, in `dream/config/content/enemies.json`:

| Enemy | Patrol | Chase |
| --- | --- | --- |
| Wolf | 1.4 | 5.0 |
| Dire Wolf | 1.6 | 5.6 |
| Ashen Prowler | 1.7 | 5.8 |
| Grave Howler | 1.8 | 6.0 |

Every chase speed now exceeds the player's 4.3 cruise, so walking away is no longer an escape; none exceeds the 6.0 sprint.

## Decisions, and why

### 1. Cruise dropped 6.0 -> 4.3, a 28% slower ordinary traversal

Grounding traversal was the explicit ask: at 6 m/s the character skated, and no animation tier could fix a number that was simply too fast for the figure's stride. Everything else in this wave is calibrated against it. It is deliberately ONE constant (`DefaultRunSpeed`) so that if the slower world turns out to feel like a chore it can be reverted by editing a single number, without unpicking the tier structure, the ramp, or the enemy table.

### 2. The Grave Howler is 6.0, not the 6.4 first proposed

6.4 would have made it strictly faster than a sprinting player on open ground. With no stamina system in the game, that is not difficulty, it is an unescapable trap: the player has no resource to spend and no mechanic to break line of sight with, so the outcome is decided the moment it aggros. At exactly 6.0 it matches sprint, which means it holds its distance forever without ever closing - the same dread, sustained indefinitely, but still beatable with terrain and cubes. The ceiling for any future enemy is therefore the sprint speed, and breaking it needs a stamina system first.

### 3. The run tier must also run C2's directional sector table (extended in fix loop 2, F2/F3)

C2's directional clips (`walk-back`, `walk-strafe-left`, `walk-strafe-right`) fixed the strafe moonwalk, and they were selected in the walk tier because at the time the walk tier was the only tier. Now that the default cruise is the RUN tier, a run that skipped the sector table would silently retire that fix at exactly the speed the player actually moves at - the bug would have come back everywhere except while deliberately walking.

So `PlayerAnimationStateMachine.Update` runs `ResolveWalkClip` first in every tier and substitutes `RunClip` only when the resolved sector is the forward one:

    string sector = ResolveWalkClip(rawTwist, _walkClip, out float residual);
    _walkClip = sector;
    clip = _gait == PlayerGaitTier.Run && string.Equals(sector, WalkClip, StringComparison.Ordinal)
        ? RunClip
        : sector;

The strafe and back sectors keep their directional clips in the run tier and are played faster, and the sector hysteresis keeps holding the sector's own name.

Fix loop 2 found this decision only half-delivered and extended it: the SPRINT tier bypassed the table entirely, and the three directional sectors divided their playback by the tier's reference instead of the walk's, so the stated goal was met in one sector out of four. See F2 and F3 below; the code in this section is now superseded by the shape recorded there.

### 4. `PinnedTicksBeforeNudge` in `PathFollower` was recalibrated off `DefaultGroundAcceleration`

The old flat 3-tick window was tuned to the instant motor, where a body was at full speed on tick one. A ramping body genuinely covers less than `PinnedStepMetres` for its first ~5 ticks, so with the ramp *every single start* looked pinned and fired the nudge, and the click soak lost 14 of 70 clicks to timeouts fighting it.

WS-1 isolated the cause experimentally rather than guessing: instant ramp at the 4.3 cruise gave 0 timeouts; acceleration 18 with an instant brake gave 24 timeouts. So it is the acceleration, not the braking and not the lower speed. The window is now derived from the acceleration instead of being a literal:

    private const int RampTicksToClearPinnedStep = (int)(PinnedStepSpeed
        / (PlayerSimulationTuning.DefaultGroundAcceleration / SimulationClock.AuthoritativeTicksPerSecond)) + 1;
    public const int PinnedTicksBeforeNudge = RampTicksToClearPinnedStep + 3;

Retuning the acceleration now moves the nudge window with it.

### 5. `EnemyTeleportDistance` 4 -> 6.5 m

`ClientScenePresenter.EnemyTeleportDistance` is a DERIVED number, not a taste one: it has to exceed what the fastest enemy can legally cover across the longest span the interpolation buffer still interpolates, or an ordinary chase reads as a teleport and pops. The fastest shipped chase is the Grave Howler's 6.0 m/s and the span is `TickInterpolationBuffer.DefaultMaxTickSpan` (64 ticks = 1 s), so 6.0 m of travel is legal and the threshold sits just above it. 4 m was correct only while every enemy moved under 3 m/s; after the re-basing an ordinary 5 m/s wolf chase read as a teleport and popped. `EnemyInterpolationReplicationIntegrationTests` measures the relation rather than the number, so the next speed change fails the test instead of shipping a pop.

## Known gameplay consequence, open for a product decision

After a server-side send stall the player lands **about 0.3 m ahead of truth and smooths back**. This is a real behaviour, not a test number.

A replay cannot model a velocity gap between a client body that never stopped cruising and a server seat that idled through the stall and so resumes from rest and ramps back up. The reconciler translates the client's unacknowledged path onto the acknowledged server position; translation carries the client's distance but not its velocity. WS-1 derived the size independently before measuring it: over 6 ticks it is the client's cruise travel (6/64 x 4.3 = 0.403 m) less the server's ramp-up from rest (0.092 m), about 0.311 m, against 0.3046 m actually measured.

Before the ramp both sides were at cruise instantly and the translation was exact, which is what the old 0.05 m tolerance in `ClientReplayReconciliationTests` measured. That assert was loosened to 0.35 m with the derivation in the comment. Because a loosened bound alone would no longer discriminate, the now-toothless assert was replaced with one that still does - `replayGain > 0.3 m`, that the replay really does carry the unacknowledged travel rather than snapping back to the ack.

**Open question for a future product decision:** whether to close the gap properly by replaying velocity as well as position (the server would have to ship its seat velocity, or the client would have to re-simulate the motor instead of translating), or to accept a 0.3 m smooth-back after a stall. It is only visible after a stall, and it smooths rather than snapping, so it was not worth blocking this wave.

## Integration note

One cross-workstream seam needed resolving; it was not a conflict in any file. `EnemyInterpolationReplicationIntegrationTests` drove the player 3 ticks in 10 to fake an average 1.8 m/s, because the player's single flat 6.0 m/s speed outran the wolf's 5.0 m/s chase and the wolf lost aggro. Both halves of that premise died in this wave: the cruise is now 4.3 and already below the wolf, and the motor ramps, so a 3-tick burst never escapes the ramp. Merged unchanged, the player moved centimetres and the wolf died 1.87 m from its spawn against an assert wanting it past 7.5 m. The player now moves every tick and is catchable by construction rather than by duty cycle; every assert in the test is unchanged. The test was verified green on `feat/enemy-gaits` alone first, so this was a merge interaction and not a defect in either branch.

## Fix loop 2: gait legibility

A reviewer read the wave's animation, audio and enemy-gait code against what it claims and found six places where the GAITS ARE WRONG ON SCREEN even though every speed number is right. Fixed on `fix/gaits-loop2`.

### F1 - the footstep strides disagreed with the authored clip cadences

`FootstepCadence` said its strides were "grounded on real cadence". They were grounded on a *cadence figure someone picked*, and against the generator every tier's audio was slower than its legs:

| Tier | clip cycle | visible footfalls/s at playback 1.0 | audio footfalls/s, as shipped | drift |
| --- | --- | --- | --- | --- |
| Walk | 0.80 s | 2.50 | 1.6 / 0.85 = 1.88 | -25% |
| Run | 0.62 s | 3.23 | 4.3 / 1.50 = 2.87 | -11% |
| Sprint | 0.44 s | 4.55 | 6.0 / 2.00 = 3.00 | **-52%** |

A stride is not a free number. Each gait is a five-key cycle with two footfalls, so at the authored rate the body covers `referenceSpeed * cycle / 2` metres between footfalls and that distance IS the stride: **0.64 / 1.333 / 1.32**. The clips were kept and the strides re-derived, not the other way round - the clip cadences were authored and inspected on screen, the stride numbers never were.

Walk and sprint landing a centimetre apart is the arithmetic, not a mistake: the sprint cycle is 1.8x faster but the sprint speed is 3.75x the walk's, so the ground per footfall is nearly identical and only the RATE differs (2.5 steps/s walking against 4.5 sprinting).

**Why it shipped, and the guard that now stops it.** `TheAnimationTiersMatchTheAuthoritativeTuning` ties the tier SPEEDS to `PlayerSimulationTuning`, and it passed. Nothing tied the strides to the CLIPS, and that is a different axis entirely - the speed-parity guard structurally cannot see this class of drift. `FootstepCadenceTests.TheStridesMatchTheAuthoredClipCadence` now reads each cycle length back off the SHIPPED `player-grim.glb` (not out of `gen_char_player.py`) and asserts `stride == referenceSpeed * cycle / FootfallsPerCycle`, so re-timing a clip without re-deriving its stride fails.

### F2 - the directional sectors played walk clips at playback 1.0 in the run tier

In the run tier, a resolved sector of `walk-strafe-left`, `walk-strafe-right` or `walk-back` set `clip = sector` and then `speed = HorizontalSpeed / ReferenceSpeed(Run)` = 4.3 / 4.3 = **1.0**. Those three clips are walk-cadence and this wave did not re-time them, so they skated about 2.7x at the ordinary cruise: decision 3's stated goal - the moonwalk fix applying at the speed the player actually moves at - was met for **one of four sectors**. Not a regression (it was 3.75x before the wave) but not the fix either.

A directional clip's playback now divides by `ReferenceWalkSpeed`, which runs into `MaxPlaybackSpeed` 1.6 and gets the legs 1.6x faster. The forward sector still divides by the tier's own reference, because its clip is authored for that tier's speed and playback 1.0 is correct there and only there.

The twist bookkeeping is untouched and must stay that way: walk and run pass the within-sector RESIDUAL under `MaxLegTwistRadians`, only the forward sprint passes the RAW delta under `MaxSprintLegTwistRadians`. A tier passing the wrong one makes the legs double-count the turn, which looks fine from the front and wrong from the side.

### F3 - decision: YOU CANNOT SPRINT SIDEWAYS

The sprint tier bypassed the sector table altogether and played the forward sprint cycle with the legs yawed up to 90 degrees. Before this wave `SprintSpeedThreshold` was 9 m/s against a 6 m/s cap, so that branch was **dead code**; the wave made it reachable, and nobody has ever looked at sprinting sideways or backwards.

Rather than ship a never-inspected pose for a capture to judge later, the sprint is now gated on the forward sector: when the table resolves to anything other than the forward `WalkClip`, the sprint falls through to the run tier's handling and keeps the directional clip (with F2's walk-referenced, clamped playback). The sprint keeps its single clip and its raw-delta / `MaxSprintLegTwistRadians` treatment ONLY in the forward sector. The yawed-leg state is gone by construction.

This also fixes the nit in the old branch: it never updated `_walkClip`, so the sector hysteresis resumed from a stale sector after a long sprint. Running the table on every tier keeps the sector current on every path, and `ALongSprint_LeavesTheSectorUpToDate_NotStale` is the guard.

### F4 - the enemy snap guard was a distance, not a speed

`EnemyVisualState.ObserveMotion` discarded steps over `MaxStepDistance = 3 m`. A distance is not a speed: at 60 fps a 2.9 m step is a **174 m/s** sample and at 144 fps a 0.5 m step is 72 m/s. With `SpeedSmoothing 0.5` one 174 m/s sample stays above the 3.0 m/s gait threshold for about six frames (174, 87, 43, 22, 11, 5.4, 2.7), and `ResolveSprinting`'s latch then holds the gallop until it falls below 2.6: **a visible gallop flash on a walking wolf**.

It is reachable. When `ClientScenePresenter.ApplyEnemySample` has just called `entry.Buffer.Clear()` (teleport, respawn, coalesced death), `TryGetEnemyRenderPose` fails and `EnemyRenderer.ResolveRenderPose` falls back to `actor.Position`, which is snapshot-quantised rather than interpolated - so for those frames the measurement is a train of spikes and zeros, and a chasing enemy can oscillate across the 3.0 boundary even with the 0.4 m/s hysteresis band.

The gate is now the implied SPEED: `MaxObservedSpeed = 12 m/s`, twice the fastest `SprintReferenceSpeed` in the catalog, so nothing an enemy actually does is discarded and no snap can reach the gait threshold through the EMA. The EMA weight is deliberately left alone - it is frame-rate dependent, but it matches the presenter's on purpose.

`ObserveMotion_DiscardsASpikeSampleUnderTheOldDistanceGate_SoTheGallopDoesNotFlash` proves it, and it discriminates: against the old distance-only guard it fails with `Expected "AnimalArmature|Walk", Actual "AnimalArmature|Gallop"`.

### F5 - the two command coalescers disagreed about `SprintHeld`

`LatestPlayerCommandInbox.Merge` ORed `SprintHeld`, copying `JumpPressed`'s treatment; `ClientOutboundCommandQueue.Merge` takes the newer frame's value. Sprint is HELD STATE, not an edge, so the newer value is correct and is what the client predicted - OR-ing it made a coalesced pair in which sprint was *released* simulate one extra sprint tick server-side. Small (about 0.027 m, inside the smooth band), but two coalescers disagreeing about one field is the kind of thing that grows. The inbox now takes the newer value; the one-shot presses are still ORed, which is right for them. `BufferedPlayerCommandInbox.HasSameMovement` already treated sprint as movement correctly and is untouched.

### F6 - two small ones

- **The walk pose was unphotographable.** `PlayScene.Update`'s `HandleWalkToggle` has always asked `CaptureKeyHeld(Keys.Z)`, but `PlayScene.Capture`'s key switch had no `Keys.Z` case, so it could only ever answer false: a capture could photograph the sprint pose and not the walk one. `HudCaptureKeys.Z = 4096`, the names `z`/`walk`, and the switch case are wired exactly as `Shift`/`sprint` was, so `keysnap:w+walk:N` works.
- **M7, the walk throttle fought the path follower.** `ApplyWalkThrottle` runs after `_clickMove.SteerTick`, so it scaled click-move axes too, putting the follower at 1.6 m/s = 0.025 m/tick against `PathFollower.PinnedStepMetres` 0.02 - a 25% margin, so any mild slowdown while walk-toggled read as pinned and fired the nudge, the exact fight decision 4 was meant to end. **Decision: the walk toggle does not apply while a click goal is active.** The toggle is a keyboard-movement affordance and click-move carries its own speed intent, so the two never need to compose. `PathFollower` is untouched.

### F7 - the wolf's death clip is latent, not a visible fix

The WS-3 bullet above reads as though the wave made wolves die properly. It did not, and the record now says so: the `AnimalArmature|Death` clip is **wired and correct, latent until something draws corpses**. `EnemyActor.IsActive => Combatant.Health.IsAlive`, `EnemyRenderer.DrawAtRenderTick` skips `!actor.IsActive`, `ClientScenePresenter.ApplyEnemySample` drops dead enemies and `RemoteEnemyStateApplier` removes them - so nothing draws a corpse today and the clip cannot be seen.

Worth knowing before something does: `EnemyVisualState.ApplyClip` calls `PlayLoop` unconditionally, so the first code that draws a corpse will **loop the death animation** - a wolf dying over and over - unless it plays the clip in a hold mode the way `PlayerAnimationStateMachine` does with `PlayerClipMode.Hold`.

### What fix loop 2 did NOT do

- `FootstepCadence.MaxStepDistance` has the same distance-not-speed shape as F4, but the cadence accumulator only gains one extra step from a spike rather than switching gait, so it was left alone rather than widened into a second behaviour change.
- M7 has no test: `ApplyWalkThrottle` is a private method on `PlayScene`, which has no test harness, so the change is covered by reading alone.
- `LatestPlayerCommandInboxTests.Enqueue_AccumulatesLookDeltaAndPreservesOneShotInputsUntilDrain` still asserts the OR-ed sprint and therefore fails against F5. That file belonged to a parallel workstream in this loop and was deliberately not edited; the new contract is covered by `LatestInboxSprintCoalescingTests` and the stale expectation needs flipping during integration.

## Verification

Build, both projects, Debug: 0 errors. 18 warnings in `dream` and 83 in `dream.Tests`, identical to master at `04a8fbfe` - the merges added none.

Gates, all exit 0:

- `check_char.py`: 0 problems. `player-grim.glb` is byte-for-byte what the generator makes (428500 bytes, sha1 2374d15992b8); clips `['attack-melee-right', 'die', 'holding-right-shoot', 'idle', 'run', 'sprint', 'walk', 'walk-back', 'walk-strafe-left', 'walk-strafe-right']`.
- `check_actors.py`: 0 problems. `hound.glb` is byte-for-byte what the generator makes (381796 bytes, sha1 1616091d1f3c); clips `['attack', 'die', 'gallop', 'idle', 'walk']`.
- `check_sources.py`: 0 problems, 428 names scanned.

The three workstreams' own suites together: **407 passed, 0 failed, 1 skipped**. That includes `TheAnimationTiersMatchTheAuthoritativeTuning`, the WS-1/WS-2 contract guard, passing unchanged: the authored tiers equal the authoritative tuning at 1.6 / 4.3 / 6.0 and the selection thresholds lie strictly between them.

### Open: 18 failing tests the ramp invalidates, inherited from WS-1

Not a merge artefact. The same 19 failures appear on `feat/grounded-movement` (`83fdb1e6`) alone, of which 18 are new against master and 1 (`StoredWorldJoinLoopbackTests`) already fails on master. The acceleration ramp changes how far the body travels in the first few ticks and how it stops, and these suites assert those magnitudes; WS-1 updated `PathFollower` and `ClientReplayReconciliationTests` for the ramp but not this set. Root causes:

- **A shared probe helper.** `ProbeOpenFacing` in `Wave9StarvedTickAckTests` walks a short burst from the spawn to find an unobstructed direction and throws `No open walk from the spawn` when none qualifies. A ramping body no longer covers the probe's distance in the burst, so the probe fails before the test under it runs (4 tests).
- **A stopping body now coasts.** `SeatUseCadenceAndStarvationTests` asserts a starved seat stands still after 16 repeated ticks; it now moves 0.0526 m, because the ramp decelerates instead of zeroing the velocity. This one deserves a product look rather than a new number: a seat whose commands stop glides about 5 cm (2 tests, plus 2 in `Wave7SanitizedSeatCadenceTests`).
- **Stall reconciliation magnitudes**, the same family as the 0.3 m consequence recorded above but in classes WS-1 did not touch: errors of 2.886-2.991 m and a replay error of 0.3159 m in `BufferedInboxStallReplayTests`, `Wave7StarvationReconciliationTests` and `Wave9StarveInputChangeTests` (6 tests).
- **Cube pushing and seam walking.** The player accelerates into a cube instead of hitting it at full speed, so push distance and walked distance change: pushed cube 0.923 m, pushed row 0.0104 m, walked only 3.568 m (3 tests in `CubeRestFreezerTests`).
- **A golden tick capture.** `WorldSessionRuntimeTickEquivalenceTests.ScriptedRun_...AsBeforeMem3` compares recorded snapshots and now differs (1 test).

These were left for an implementer rather than resolved here: picking the new expected magnitudes is authoring the contract, not resolving a conflict, and the coasting seat may be a behaviour to change rather than a number to update.

### Baseline, unchanged by this wave

Master at `04a8fbfe` is not green. The attribute-ceiling commit `1f422512` changed `PlayerCombatStats.cs` / `PlayerStatRules.cs` / `StatRules.cs` and updated no tests. Measured on master's tree in this worktree and again on the integration branch, the same **26 tests** fail in both, identically: `RuntimeAuthoritativeSessionHostTests` (3), `PlayerStatPointsTests` (4), `ArmorMitigationTests` (6), `PlayerCombatAttributeTests` (2), `AuthoritativeActionFlowTests` (2), `CharacterWindowTests` (1), `Wave7HeldUseAndStatsValidationTests` (3), `ObjectIdRestartLoopbackTests` (1), `PersistentWorldPlacedCubeDamageIntegrationTests` (1), `PersistentWorldSeedDivergenceIntegrationTests` (1), `WaveCTesterBuildingsOutliveBuilderWireTests` (1), `EnemyLootEndToEndTests` (1). This is wider than the 5 tests the baseline was previously thought to cover.

`StoredWorldJoinLoopbackTests.GeneratedWorldJoin_FrameSequenceAndBytes_EqualTheC0Capture` also fails on master and is baseline.

`PersistenceWriteQueueLockRetryTests` (2) and `WorldTransferLoopbackTests` (1) failed during a full-suite run that overlapped other sessions' test processes and passed in isolation on both master and this branch; treat them as contention flakes, with the two 0-byte HUD allocation tests already known to be intermittent.

## Fix loop 1: the ramp's two production defects

Merged onto `integrate/character-gaits-v2` as `fix/gaits-loop1` (`75fb6148`). Besides re-expressing the 18 ramp-invalidated tests, it fixed two places where the ramp was actively wrong rather than merely re-measured.

### M1 - a snap restarted the ramp from rest

`ClientCorrectionApplier`, on a `Snap`, called `SetWorldPosition`, which teleports the body AND zeroes its velocity. Under the old motor that was free: the motor set the commanded velocity outright, so the tick after a snap was back at full speed. Under the ramp the motor accelerates from the body's own horizontal velocity, so a zeroed body needs v/a to recover - 0.24 s at the 4.3 cruise, 0.33 s at the 6.0 sprint - and falls v^2/2a behind a server that never stopped: **0.51 m at cruise, 1.00 m at sprint, on every snap**. It also read `HorizontalSpeed ~ 0` for those frames, so the local figure replayed idle -> walk -> run, exactly the stride restart `GaitHysteresisSpeed` exists to prevent.

The fix restores the pre-snap **horizontal** velocity and leaves the vertical zeroed: the replayed snap position already contains the unacknowledged travel, so the pre-snap horizontal velocity is the state that belongs with it, while a snap may be a respawn or a teleport and the fall it was in must not survive it. A snap whose replay was **discarded** (a teleport, or a path into a solid cell) lands on the server position alone, carries no unacknowledged travel, and so restores nothing.

Measured, in `SnapVelocityHandoffTests`: with the fix, speed right after a snap is **4.300 m/s** and the body covers **0.993 m** over the next 15 ticks, against a cruising ideal of **1.008 m**. Without it, **0.000 m/s** and **0.413 m** - the restart-from-rest curve, short by **0.494 m**.

The test `ClientReplayReconciliationTests.Snap_ZeroesThePredictedVelocity` asserted the very behaviour M1 fixes, and is now `Snap_KeepsTheHorizontalVelocity_AndZeroesTheVertical`.

### M6 - a zero-length tick bypassed the ramp

`PlayerMotor`'s ramp returned the full target speed when `deltaTime <= 0f`, folded into the same branch as "already at the commanded velocity". That made the motor's output depend on whether one side happened to see a zero-length tick, in the one method whose contract is that the server and a predicting client stay bit-identical. It now returns the current horizontal velocity for a zero-length tick - no time passed, so no ramping happened. Every production caller passes the fixed 1/64 s, so this was unreachable; it was still the wrong answer to give. The same branch's `float.Epsilon` (1.4e-45, which made a tolerance test an exact comparison) became a named `RampVelocityEpsilon = 1e-6f`.

### Cluster 3's replay-residual bound is cruise-only

Loop 1's replay-residual bound of **0.514 m** is cruise-only *by construction*: the harnesses in that cluster never sprint. If a future test sprints through that path the bound has to become 6.0^2/36 = **1.00 m**. The number is not a measured ceiling; it is v^2/2a at one tier.

## Fix loop 2 merge note

Merged as `fix/gaits-loop2` (`38eaa4dd`). Both loops contain an identical one-line change to `LatestPlayerCommandInbox.Merge` (sprint takes the newer value instead of OR-ing); loop 1 had taken loop 2's hunk verbatim so its own branch would not be red, so the second merge did not touch the file at all and there was no conflict. Loop 1 also flipped the matching stale `Assert.True(drained.MoveIntent.SprintHeld)` to `Assert.False` in `LatestPlayerCommandInboxTests`, the expectation loop 2 recorded as needing flipping during integration.

## TOP FOLLOW-UP, recorded and deliberately NOT fixed in this wave

`dream/Client/Replication/ClientPredictionReconciler.cs`'s class comment still ends:

> The motor stops horizontal movement at once on an idle tick, so the idle part needs no deceleration.

**That claim is FALSE under the ramp** - the motor now decelerates at `DefaultGroundDeceleration` (24 m/s^2) instead of zeroing the velocity. It matters more than a stale comment usually would, because that sentence is the stated *rationale* for how the starved-tick extrapolation builds its idle step. The comment is certainly wrong. Whether the extrapolation logic underneath it is also wrong is **unresolved**: no test in loop 1's set exposes it, because the errors it now produces are all sub-deadband (below the 0.05 m `DefaultDeadbandDistance`), so nothing fails and nothing proves it right either.

Somebody must derive whether the extrapolated idle step should decelerate. This was left alone on purpose: it is a design question about what the extrapolation should model, not a merge resolution, and guessing it inside an integration would bury the decision.

## Other deliberate non-fixes

- `FootstepCadence.MaxStepDistance` still has the same distance-not-speed shape as the enemy snap guard that fix loop 2's F4 corrected. Left as is: a spike there only adds one extra footstep, rather than switching gait the way the enemy guard's spike flashed the gallop.
- `dream.Tests/Server/LatestInboxSprintCoalescingTests.cs` sits under `Server/` only because of parallel-ownership boundaries in loop 2. It tests `LatestPlayerCommandInbox` and **should be moved beside `LatestPlayerCommandInboxTests.cs`**.
- F1 is an **audible** change: walk footfalls go 1.9/s -> 2.5/s and sprint 3.0/s -> 4.5/s, because the strides were re-derived from the authored clip cycles (0.85/1.5/2.0 -> 0.64/1.333/1.32; sprint audio had been 52% slower than the legs). It wants one human listen before it ships - no test can judge whether it sounds right.
