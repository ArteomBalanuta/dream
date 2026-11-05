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
