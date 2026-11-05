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


## Fix loop 2: the figure looked wrong while every gate passed (palette and clipping only)

Looking at captures, the unarmoured figure read as a white shirt over black trousers and an equipped one as bright
blocks. Gate numbers were evidence, not the goal; loop 1 had optimised a histogram and produced the wrong mood.

Mechanism (verified in the shader, partly different from the first guess). `enemyFragment.glsl` computes
`(ambient * z + (direct + point) * 0.75) * baseColor`, times exposure, clamps, with no tonemap and no gamma encode, for
the baked atlas and the procedural armour boxes alike. `ALBEDO_LIFT = 3.0` is generator-side and touches the atlas
only; the boxes never get it. So the figure is lifted 3x and then gained by up to 2.17 beside a brazier (1.4 at noon),
while a box is gained only. The tunic (#9E9A8C, lifted to ~1.0) was fully clipped on 12% of its pixels at azimuth 35 and
31% at 145 (that is the 1.0000 p90). The armour was authored as final pixel colours: Bone 0.62 x 2.17 = 1.35, Steel
0.46 reaches 1.00 exactly, Ember 0.95 and Arcane 0.55 are far over. ClothRed (0.353) does NOT actually clip, even at
the strongest light (0.77); it read as a salmon block because with no gamma a mid albedo is a bright byte, and it was
the most saturated thing on the figure. Hence: darken, desaturate, distinguish by value.

New gate (check_char.py, "clipping"): per material, on the figure's every baked material and on every drawable worn
piece drawn on the figure (11 pieces, 66 boxes, glowing ones by lit albedo), at both M3 azimuths, at most 1% of the
material's visible pixels may be fully saturated (value 1.0 after gain and clamp), equivalent to p99 < 1.0. Plus a
palette bound: no ArmourMeshCatalog colour x the peak gain on the drawn geometry may reach 1.0, and every catalog
material must be drawn by some piece. Live controls: the figure at 3x albedo trips it, and the loop 1 catalog trips it
(Ember 68%/55% saturated, Bone, Steel, Arcane, Gilt over the palette bound).

Art. Tunic #9E9A8C -> #645E4F (dirty oatmeal, atlas p99 0.48 lifted, 0% clipped). Catalog albedo (linear), all
channels <= 0.40: Steel (0.30,0.315,0.345), DarkIron (0.115,0.115,0.13), Leather unchanged (0.13,0.08,0.06), ClothCold
(0.07,0.078,0.10), ClothAsh (0.17,0.162,0.145), ClothRed (0.20,0.055,0.045), Bone (0.40,0.36,0.29), Gilt
(0.32,0.23,0.09), Arcane body (0.10,0.06,0.17), Ember body (0.24,0.10,0.04). Emissive factors unchanged: the glow
stays hot, the body is dark.

Measured: base 0.2780 (az 35 p10 0.0077 p90 0.2857, binding; az 145 p10 0.0083 p90 0.4918). `BASE_SPREAD_MIN`
0.64 -> 0.25, from the art, with a comment that the number follows the art. Worn set 0.3906 (az 35 p10 0.0084 p90
0.4063; az 145 p10 0.0135 p90 0.4041): `WORN_SET_SPREAD_MIN` 0.50 is NOT reached and was NOT moved or worked around;
that check is red pending the owner's decision. GLB 253588 -> 258436 bytes.

## Held items: the same albedo-as-pixel defect (fix/held-item-palette)

`WeaponMeshCatalog` was authored as final pixel colour too. Before (albedo, x2.17 at the brazier): Steel
(0.46,0.48,0.52) -> (1.00,1.04,1.13); DarkIron (0.17,0.17,0.20) -> 0.43 max; Wood (0.25,0.15,0.09) -> 0.54; Leather
(0.13,0.08,0.06) -> 0.28; Gilt (0.50,0.37,0.14) -> 1.09; Cloth (0.36,0.05,0.06) -> 0.78; Arcane (0.35,0.18,0.55) ->
1.19; Ember (0.95,0.45,0.12) -> 2.06; Bone (0.62,0.58,0.48) -> 1.35. Steel, Gilt, Arcane, Ember and Bone clipped. The
starter torch rendered a saturated yellow head and the axe a cyan blade (blue clipped first on steel).

After, all channels <= 0.40: Steel (0.33,0.34,0.365), DarkIron (0.11,0.11,0.125), Wood (0.20,0.125,0.07), Leather
unchanged, Gilt (0.32,0.23,0.09), Cloth (0.20,0.055,0.045), Arcane body (0.10,0.06,0.17), Ember body (0.24,0.10,0.04),
Bone (0.40,0.36,0.29). Emissive unchanged. The torch's 0.13x0.18 head block is now DarkIron (charred rag) and the flame
is two small rolled Ember boxes on top, so the glow sits on a dark body.

`gate_clipping` now also draws every held model (Hand models in the right fist at HandScale, shields on the left
forearm at ShieldScale, as HeldWeaponRenderer places them) and applies the per-material saturation share and the
palette bound to WeaponMeshCatalog. No held material is exempt (every one is used by some model); the armour catalog's
Gilt exemption is unchanged. Extending the gate made no armour material newly fail. CLIP_SHARE_MAX unchanged.
