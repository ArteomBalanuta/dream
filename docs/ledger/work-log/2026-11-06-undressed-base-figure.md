# 2026-11-06 - Undressed base figure (WS-A)

Branch `ws-a/undressed-figure`, from `c6b6fddb`. Files: `tools/ui-assets/gen_char_player.py`,
`dream/animations/players/player-grim.glb`, `tools/ui-assets/tests/check_char.py`,
`tools/ui-assets/previews/char_player.png`, `tools/ui-assets/README.md`, `docs/rendering-and-assets.md`.

## Why

The base player figure baked a kettle helm, hood, mask, one pauldron, gorget, cloak, bracers, knee cops and shin
guards. Worn pieces therefore had to oversize and enclose that geometry, an unequipped character still read as
armoured, and the enclosure rule produced the flat navy cloak plank. The base is now an unarmoured traveller
(not nude): tunic, breeches, boots, bare grey head, bare hands.

## What changed in the generator

- Deleted `_torso_plates`, `_head_plates`; dropped cloak, backplate, bracer, couter, guard, cop, sabaton, cuisse.
  Kept belt, skirt, tail, trouser, boot, sleeve, hand and the two `ember.eye` sockets.
- `cuirass` became a cloth tunic (new `cloth.tunic`, undyed wool `#5E5748`); the skirt and sleeves use it too.
- New bare head: 8-sided skull/jaw loft in `flesh.grey`, a neck, a short cowl collar.
- The head joint moved from 9.6 u to 10.97 u over the pelvis (+1.37 u). A bare 0.42-unit head tops out at
  2.56; holding the authored top at 2.68 (the engine normalises by authored height, so a lower top would draw
  the figure bigger) is done with neck length, and doing it in the node keeps the contract's head region
  (y 0.00 at the chin) literally true. Node names are unchanged.
- `build_asset` now computes the die clip's root lift with `_die_lift`: the old `back` thickness was the cloak,
  and without it the tipped head sank 0.13 units under the ground. `clip_die` itself is untouched.
- Shared helpers, constants and every `clip_*` are untouched; `check_actors.py` is green unchanged.

## Measured (engine units, node frames, check_char envelope gate)

| Row | Contract | Measured |
|---|---|---|
| Head x / z / top y | 0.12 / 0.13 / 0.42 | 0.1197 / 0.1302 / 0.4198 |
| Torso hip x | 0.26 | 0.2596 |
| Torso chest x | 0.19 | 0.1901 |
| Torso z front / back | +0.14 / -0.14 | +0.1497 / -0.1406 |
| Arm x / z (own axis) | 0.09 / 0.09 | 0.0858 / 0.0990 |
| Leg x / z | 0.11 / 0.11 | 0.1082 / 0.1082 |
| Foot x | 0.132 | 0.1232 |
| Foot z back / front | -0.05 / +0.15 | -0.0525 / +0.1505 |
| Hand | arm-local y -0.9 | heel -1.0032, unchanged |
| Authored top | 2.68 | 2.680 |

All rows are inside +-0.015. Notes: the foot row forces a stub boot (z -0.05..+0.15 is 0.2 units long against
0.25 wide at the cuff); the torso's front z sits 0.0097 over its nominal 0.14 (the red tail and the skirt);
the arm's z is 0.009 over nominal.

## The M3 gate

Measured before touching any floor: the undressed figure's M3 spread is **0.2376** (az 35 p10 0.0202 p90
0.2578; az 145 p10 0.0212 p90 0.4320). The old plated figure was 0.5064 and its loft-only negative control
0.1577, so the undressed figure is that negative control and the 0.50 gate on the base was circular.

- Base floor `BASE_SPREAD_MIN = 0.21` (measured minus ~0.025), dated and explained in the file.
- Live failure: the same geometry at one albedo (the median) measures 0.0978 and fails the base floor.
- 0.50 moved to `WORN_SET_SPREAD_MIN`: figure plus greathelm, plate-cuirass and hooded-cloak, parsed from the
  live `ArmourMeshCatalog.cs` so the gate measures what ships and cannot be tuned from this side.
  **It does not pass.** With the catalog as of `c6b6fddb` (oversize boxes) it measures 0.4259
  (az 35 p10 0.0342 p90 0.4828; az 145 p10 0.0448 p90 0.4707); a hand-fitted contract-width set measured 0.4525.
  The floor was NOT lowered. The number that decides it is WS-B's catalog; re-run `check_char.py` after the merge.
- Envelope gate added: every row of the contract table, +-0.015.

A3 (`check_look.py`, `ACTOR_SPREAD_MIN = 0.45`) needs a real capture and is not touched. The offline proxy is
the same M3 number: 0.2376 for the bare figure against 0.5064 before, so expect A3 to drop well under 0.45 when
nothing is equipped.

## Fix loop 1

Palette (the actual defect). Everything left on the figure sat at 15-35% value, so there was no light end.
Measured sweep of the tunic linen against the M3 gates, then fixed at the brightest value the 8-bit atlas keeps
honest: the engine lift is 3x and a texel cannot exceed 1.0, so the lift-once gate (mean shipped / baked within 3%
of 3.0) caps the linen at R = 0x9E (2.92x; 0xA4 gives 2.83x and fails). Only R sets HSV value, so G/B are held back.

| Material | Was | Now | Why |
| --- | --- | --- | --- |
| cloth.tunic (tunic, sleeves, collar, skirt) | #5E5748 | #9E9A8C (sat 0.10) | the light end; ceiling is the atlas, not taste |
| cloth.ash.cold (breeches) | #1E2024 | #101010 | value contrast between garments |
| leather (belt, boots) -> leather.dark | #241C16 | #0F0F0E | the boots are the largest dark area |

Near-black has a floor of its own: #0D0F12 quantised into the forbidden cyan band (1367 texels), and #0A0706 /
#0E0C0B made 369 texels past 0.35 saturation. Neutral #101010 / #0F0F0E makes none; the accent share is 0.036 (budget
0.04) and no material exceeds 0.35. The tunic skirt hangs to -5 u (was -3) for more pale area below a cuirass.

Measured: base 0.6717 (az 35 p10 0.0079 p90 0.6796, binding; az 145 p10 0.0081 p90 1.0000), up from 0.2376.
Worn set 0.5150 (az 35 p10 0.0082 p90 0.5232; az 145 p10 0.0133 p90 0.5301), up from 0.4487. Target 0.52 was NOT
reached: the worn p90 is pinned by the atlas ceiling and the armour's own lit faces, and brighter linen fails the
lift gate. The 0.50 floor stands, now with the rationale that a fully equipped character must be at least as legible
as the figure that shipped (0.5064). `BASE_SPREAD_MIN` 0.21 -> 0.64. The worn-set liveness control is now the dressed
figure at one albedo (0.107), since the figure alone clears 0.50.

Head ceiling. The three helms sat at 1.583 of a 1.6 ceiling. Now: main masses 0.32-0.34 wide (half 0.16-0.17),
nothing wider than 0.17; ceiling = base head 0.12 + a 0.06 shell allowance = 0.18 (1.5x), depth 0.135 + 0.06; floor
= body + one pixel (0.0265) = 0.1465. Margin to ceiling 0.01 (0.4 px), to floor 0.014+.

Also: fit window gets a floor and a z ceiling (leather-vest was exactly 1.000x; it gains a shoulder yoke and a wider
body); cloak panels respaced to tiers 0.03 apart at 0.06 thickness; cloak length contracts; the envelope gate is
one-sided outward and the table moved to the figure (head z 0.135, torso z -0.145/+0.15, chest 0.195, arm z 0.10,
foot z -0.055/+0.155); a cover-table-vs-mesh test; the die clip lift is per key (the held frame now rests at y 0,
was 0.0414 up); worn colours parsed from the catalog. A3 (`check_look.py`) still needs a real capture.

