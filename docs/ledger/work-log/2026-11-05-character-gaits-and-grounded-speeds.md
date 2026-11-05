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

## Fix loop 3: the client's starved-tick extrapolation still modelled the old motor

One production bug, on `fix/gaits-loop3`. `ClientPredictionReconciler` was never touched by this wave, and its class comment still claimed the rationale the ramp destroyed:

> "The motor stops horizontal movement at once on an idle tick, so the idle part needs no deceleration."

### What the code actually did

`PredictAfterStarvedTicks` derives ONE displacement from the two samples around the newest exactly-matching sample and then fills every remaining starved tick with it inside `BufferedPlayerCommandInbox.ServerRepeatTicks` (16) and with nothing but gravity past it. That is **constant velocity for 16 ticks, then an instantaneous stop**. Both halves were exactly right against the instant-velocity motor and both are wrong against the ramp:

- inside the window the server is still accelerating at `GroundAcceleration` 18 m/s^2 (or braking at 24 after a tier drop) whenever the ack tick was mid-ramp, so the repeated step is short by `18 dt^2 n(n+1)/2 = 0.0043945 n(n+1)/2` m;
- past the window the server brakes at `GroundDeceleration` 24 m/s^2 and coasts `dt sum(max(v - 0.375k, 0))` = **0.3523 m** from the 4.3 m/s cruise (12 ticks) and **0.7031 m** from the 6.0 m/s sprint (16 ticks), where the model predicted zero. (The continuous `v^2/2a` is 0.385 / 0.750; the discrete sum is lower by `v dt / 2`.)

The 0.0043945 m per-tick quantum is the same number fix loop 1 measured independently as a 0.0044 m one-tick divergence, which corroborates the mechanism.

**Why no test caught it.** The extrapolation only runs when the client's own sample at the target tick *cannot* be used, i.e. when the commands diverge mid-starve - an ordinary >78 ms uplink loss burst while the player keeps playing. In the designed send-stall path the client deliberately goes idle, matches, and its own sample carries the real ramp, which is why fix loop 1's scenarios all measured sub-deadband. It never reaches `DefaultSnapDistance`, so every occurrence is a `Smooth`: a sub-metre yank drained over `SmoothingSeconds` 0.1, visible rather than catastrophic. It also does not compound, because `_lastComparedSequence` returns `None` for a repeated ack.

### The fix: model the ramp, motor-only

Sample positions are post-tick positions, so the step's horizontal length over one tick **is** the body's horizontal velocity for that tick, and its direction is fixed (the server repeats one command). The extrapolation now carries a scalar speed and ramps it per tick exactly as `PlayerMotor.RampGroundVelocity` does - toward the ack command's tier speed while `repeat <= RepeatTicks`, toward zero past it, accelerating at `GroundAcceleration` and braking at `GroundDeceleration` - and steps `direction * speed * dt`. The `stepY` / gravity handling is untouched.

The tier speed comes from `Sample.Move`, the `PlayerMoveIntent` the tick recorded: sprint -> `SprintSpeed`, else `max(WalkSpeed, MoveSpeed * clamp(|(ForwardAxis, StrafeAxis)|, 0, 1))`. That mirrors `PlayerMotor.ResolveTargetSpeed` in a private helper rather than calling it, because the motor's method takes a `MovementCommand`, which needs the camera the reconciler does not have. The tuning constants are read statically from `PlayerSimulationTuning.AuthoritativeDefault`, the way `Client/Navigation/PathFollower` already reads them, so no constructor or call site changes. A zero-length step still leaves no direction to ramp along and still predicts no horizontal movement.

### The accepted residual, measured

The motor is not the only thing braking. Bullet's floor friction takes about **0.061 m/s per tick** off the body on top of the motor: measured from rest, one acceleration tick moves the body 0.00344 m (0.220 m/s) where the motor alone gives 0.28125 m/s, and the steady tiers sit at 4.239 and 5.939 m/s rather than 4.3 and 6.0. The reconciler cannot derive that from tuning constants, so a faithful motor-only model overshoots. Measured against real physics (`PhysicsIntegrator` + `PlayerMotor`, 28 starved ticks = 16 repeats + 12 idle, horizontal distance from the ack sample):

| Case | server travels | ramp model | error | old fixed-step model's error |
| --- | --- | --- | --- | --- |
| steady cruise | 1.3490 m | 1.4274 m | **0.0783 m** | 0.2893 m |
| steady sprint | 2.0665 m | 2.1680 m | **0.1015 m** | 0.5818 m |
| cruise, stall from rest | 0.7446 m | 0.9963 m | **0.2517 m** | 0.6896 m |
| sprint, stall from rest | 0.7446 m | 1.0806 m | **0.3360 m** | 0.6896 m |

Strictly better in every case - a 3.7x to 5.7x reduction - but **not within a few centimetres**. The architect's residual estimate (0.054 m cruise / 0.104 m sprint) only counted friction in the braking tail and holds for the steady cases; the stall-from-rest case is worse than predicted because friction eats ~22% of the acceleration and that deficit compounds over all 16 repeat ticks. The worst case is therefore **0.336 m, not the ~0.1 m the brief expected**, and it stays above the 0.05 m deadband in all four cases, so each is still a `Smooth`.

**Decision: accept the residual and name it in the class comment.** An empirical coast-deceleration constant and shipping the server's velocity in `ActorSnapshot` were both considered and rejected as disproportionate to a sub-metre smooth; that judgement stands at 0.34 m as it did at 0.1 m, but the number is recorded here so the next loop can weigh the protocol change against the real figure rather than the estimate. If the stall-from-rest smooth is ever judged visible, shipping the velocity is the only fix that removes it, because the error is friction the client cannot predict, not a modelling mistake.

### Tests

Both new tests are in `dream.Tests/Client/Replication/ClientPredictionReconcilerTests.cs`, pure reconciler unit tests with diverged commands so the extrapolation is the path under test, and both fail against the restored fixed-step loop:

- `Reconcile_StarvedPastTheRepeatWindow_WithDivergedCommands_ExtrapolatesTheIdleCoast` - steady cruise, 28 starved ticks: the prediction must land 0.3523 m past the end of the repeat window. Un-fixed: `Expected: 0.35 (rounded from 0.352299988) Actual: 0 (rounded from 4.76837158E-07)`.
- `Reconcile_StarvedInsideTheRepeatWindow_FromAMidRampAck_KeepsAcceleratingTowardTheTier` - acknowledged four ticks after a standing start (1.125 m/s), 5 starved ticks, no idle part at all: the server covers 0.1538 m against the repeated step's 0.0879 m, and that 0.066 m gap is the first starve that crosses the deadband. Un-fixed: `Expected: 0.154 (rounded from 0.153808594) Actual: 0.088 (rounded from 0.087890625)`.

Two tests in `Wave9StarveInputChangeTests` encoded the old model and were adjusted, not relaxed:

- `StarvedSnapshot_UsesTheClientSampleOnlyWhenItsCommandsMatch` built its fake server path from a hand-picked `Step = 0.07 m/tick`, which is 4.48 m/s - a speed the motor brakes back to the cruise. The step is now the cruise step; the scenario is about which sample is compared, not about the tier, and the `error < 1e-4` assertion is unchanged.
- `InputChangeDuringAStarve_IsNotCorrectedBackAndForth` asserted the fixed comparison was within a tenth of the legacy one. With an idle ack the tier speed is zero, so the model now brakes to a stop instead of repeating the ack tick's displacement - and that displacement was the body's residual drift (settling or a slow slide on the spawn's slope), which no motor model can derive. The error went from 0.00023 m to 0.00092 m on the secondary seat's spawn. The bound is now absolute (under 0.002 m, a twenty-fifth of the deadband) and still under half the legacy error, with the reason written down.

### Verification

`dream` and `dream.Tests` build with 0 errors and no new warnings. Blast radius `ClientPredictionReconciler|ClientReplayReconciliation|SnapVelocityHandoff|Correction|ClickSoak|PlayerMotor|CommandInbox`: **114 passed, 0 failed, 2 skipped**. A wider replication sweep (`Replication|Reconcil|Starv|Wave12Delta|Snapshot|Prediction`): **576 passed, 0 failed, 4 skipped**. `check_sources.py`: 0 problems, 428 names scanned. The three Wave9 failures above were confirmed as this loop's by re-running them against `84eb1c34`'s reconciler, where they pass.
