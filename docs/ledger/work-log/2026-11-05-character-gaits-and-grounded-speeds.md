# Work log: character gaits and grounded speeds

Branch `integrate/character-gaits`, from master `04a8fbfe`. Three parallel workstreams, merged WS-1 -> WS-3 -> WS-2.

## The defect this wave fixes

The player had ONE flat speed of 6 m/s and a sprint clip authored for 9 m/s that the simulation could never produce, so "walk vs run" could not express anything: there was nothing to express it with. The wolf had a single `Move` clip, so a chase and a patrol looked identical. And every enemy moved at 2.8-3.6 m/s against a 6 m/s player, which means no enemy in the game could ever close on a player who simply walked away. The wave gives the player three reachable tiers, the wolf two gaits, and re-bases every character speed against each other.

## The workstreams

- **WS-1 `feat/grounded-movement`** (`83fdb1e6`) - the tuning tiers, the motor acceleration ramp, the `sprintHeld` wire field, LeftShift/Z bindings, the `PathFollower` recalibration, `GameSettings`, and `HudCapturePointerPlan` + `PlayScene.Capture` (`Shift = 2048`, so `keysnap:w+shift:N` works). Merged first because it publishes the tuning constants the other two compile against.
- **WS-3 `feat/enemy-gaits`** (`28f1fcd5`) - `EnemyClipSet` with a Sprint/gallop clip and a speed-driven `Resolve`, observed-speed measurement in `EnemyVisualState`, the `patrolSpeed` content field, the wolf's real `AnimalArmature|Death` clip, a forged `gallop` for the hound, `enemies.json`. Merged second because it is independent of the other two.
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

### 3. The run tier must also run C2's directional sector table

C2's directional clips (`walk-back`, `walk-strafe-left`, `walk-strafe-right`) fixed the strafe moonwalk, and they were selected in the walk tier because at the time the walk tier was the only tier. Now that the default cruise is the RUN tier, a run that skipped the sector table would silently retire that fix at exactly the speed the player actually moves at - the bug would have come back everywhere except while deliberately walking.

So `PlayerAnimationStateMachine.Update` runs `ResolveWalkClip` first in every tier and substitutes `RunClip` only when the resolved sector is the forward one:

    string sector = ResolveWalkClip(rawTwist, _walkClip, out float residual);
    _walkClip = sector;
    clip = _gait == PlayerGaitTier.Run && string.Equals(sector, WalkClip, StringComparison.Ordinal)
        ? RunClip
        : sector;

The strafe and back sectors keep their directional clips in the run tier and are played faster, and the sector hysteresis keeps holding the sector's own name.

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
