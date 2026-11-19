# Work log: Cleave as a fast cut with a blade trail

- Purpose: record Cleave becoming a 0.47 s cut with a level blade and a white blade trail.
- Status: done, on branch `trail/int` from `0da2e098`.
- Plan: `docs/ledger/plans/2026-11-19-cleave-trail.md`.

## What landed

- **Timeline.** `MeleeArcSwing` is 8/6/16 ticks (0.47 s, was 0.84 s). `MeleeArcSwingContentTests` now states why
  the active window is even and >= 3 ticks and the wind-up is > 5.
- **Animation.** `clip_cleave` in `tools/ui-assets/gen_char_player.py` is rewritten: keys at
  0,2,4,6,8,10,11,12,14,16,18,21,25,30 with linear rotation, a coiled wind-up, a constant-speed sweep with the blade
  near level and an overshooting follow-through. `check_char.py` now gates the blade (arm world +Z): left ->
  forward -> right, monotonic, elevation in [-15, +50] degrees, with a live mirrored-clip negative.
  `PlayerCleaveClipTests` walks the real chain (root dip -> torso -> arm -> fist -> blade tip) and pins the tip to
  `FxBladeTrailGeometry`. The state machine drops the main-arm hold to 0 on a new swing. The other ten clips are
  byte-identical.
- **Atlas.** Frame 31 is the blade trail band (`gen_particles.py`, `check_particles.py` with a 14 px negative,
  `FxBladeTrailCellTests`). Cells 0-30 are unchanged; the atlas has no reserved cells.
- **Effect.** `FxSimulation` lays 10 `Trail` segments along the tip path during the active window; sparks drop to
  8 and the hand embers are gone. `FxBladeTrailTests` cover timing, placement, ordering, fade, gap-free coverage
  at isometric and top-down views, unaimed casts, the pool reserve and zero steady-state allocation.

## Measured blade tip (shipped glb, same chain as the C# helper)

| tick | angle (deg) | radius (m) | height (m) |
|---|---|---|---|
| 8  | -59.0 | 1.04 | 1.41 |
| 10 | -19.6 | 1.08 | 1.25 |
| 11 | -0.1  | 1.09 | 1.18 |
| 12 | +19.4 | 1.08 | 1.10 |
| 14 | +59.3 | 1.01 | 0.97 |

Overshoot +85.7 at tick 16, hold +89.6 at tick 18. Blade elevation +39 / +26 / +12 degrees at ticks 8 / 11 / 14.

## Findings worth keeping

- **The blade stood vertical.** `HeldWeaponRenderer.CreateGrip` maps the weapon's +Y onto the arm's +Z, so a
  raised arm points the weapon up. The old clip raised the arm 80-95 degrees; both gates measured the arm's
  hanging axis, so they passed while the sword pointed at the sky. Gate the blade, not the arm.
- **The slash lived on the ground.** The crescent is a ground decal at the feet reaching 3.5 m, about 1 m away
  from where the blade actually is, appearing whole and greyish by day, and cut by voxel steps and bodies (the
  ground branch of `fxVertex.glsl` has no depth bias). The trail is drawn where the blade is, as billboards.
- **The held-arm overlay fought the swing** for ~0.15 s (blended at 12/s); a new swing now releases it at once.
- Effects are unlit and additive at A = 0; the albedo <= 0.40 rule does not apply to them, only the < 255 byte pack.
- Mid-window tip radius runs ~1.08 m against the 1.0 m path (inside the 0.15 m tolerance).
