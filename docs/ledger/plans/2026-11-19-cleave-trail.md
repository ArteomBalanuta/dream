# Cleave trail: a fast cut with a blade trail

- **Purpose**: answer the feedback that Cleave "moves but has no special animation" and is "slow like drawing".
- **Status**: implemented on `trail/int`.
- **Scope**: the MeleeArc timeline, the player `cleave` clip, the particle atlas (frame 31) and the arc sweep effect.
  No protocol bump (stays 22), no persistence, shader or `FxRenderer` change.

## Overview

The swing becomes a cut: 8/6/16 ticks (0.125/0.094/0.25 s, 0.47 s; was 0.84 s). The `cleave` clip carries the
sword near level with its tip sweeping high-left to low-right: a short anticipation, a constant-speed active sweep
and an eased follow-through with overshoot. A near-white blade trail - camera-facing streak segments laid along the
blade-tip path as the active window sweeps - fades from its tail within ~0.2 s. The ground crescent and edge sparks
stay (sparks 14 -> 8, hand embers removed).

## Root cause

- **Clip.** `HeldWeaponRenderer.CreateGrip` maps the weapon's +Y onto the arm's +Z, so a raised arm carries the
  weapon pointing up. The old clip raised the arm 80-95 degrees, so the blade stood vertical through the sweep. Both
  gates measured the arm's hanging axis, not the blade. The held-arm overlay also kept holding the arm for ~0.15 s.
- **Effect.** The crescent is a ground quad at the feet reaching 3.5 m, far from the ~1 m blade; it appeared whole,
  read as a grey film by day, and voxel steps and bodies cut it. The sparks were tiny and 3.15 m out.

## Design decisions

1. **Timeline.** `MeleeArcSwing`: `WindupTicks` 8, `ActiveTicks` 6, `RecoveryTicks` 16, `TotalTicks` 30, seconds
   derived. Active ticks stay even and >= 3, wind-up > 5, total <= every MeleeArc cooldown (96).
2. **Shared blade path.** `FxBladeTrailGeometry` defines the tip path both the clip and the trail follow:
   angle -60 -> +60 degrees linear in progress (half of `PaintedArcDegrees`), radius 1.0 m, height 1.4 -> 1.0 m above
   the feet, blade reach 0.8 m, and the trail cell's plateau fraction 20/32. `FxFrame.Trail = 31`.
3. **Clip.** Keys at ticks 0,2,4,6,8,10,11,12,14,16,18,21,25,30 with linear rotation. Arm raise ~40/28/15 degrees at
   ticks 8/11/14 keeps the blade near level; torso yaw plus arm yaw carry the sweep. Wind-up eases into a coil with
   near-zero velocity at tick 8; recovery overshoots to ~+85 degrees, holds, then smoothsteps to identity at 30.
   A new swing releases the held-arm overlay at once.
4. **Atlas cell.** Frame 31 is a neutral white additive band (A = 0, peak 230-245): flat for |x| <= 20 px, gone by
   25 px, a bright core with a soft sheath. No reserved cells remain.
5. **Trail.** Each arc sweep lays 10 segments (`FxParticleKind.Trail`, with a world half-chord `Axis`) as progress
   reaches them, between the crescent and the sparks. Segments are billboards rotated to the projected chord and
   stretched so neighbours overlap on screen, depth-biased, additive, faded by `pow(1 - t, 1.5)` and thinning, life
   `2 x ActiveSeconds`. Budget 1 + 10 + 8 = 19 = `MaxCastParticles`, unchanged. An unaimed cast lays no trail.
   Segments are not followed after they are laid.

## Out of scope

Trails for enemies or other skills, a light or flash, per-weapon blade length, hit cone/range/cooldown/damage
changes, shader changes, rebalancing the crescent.
