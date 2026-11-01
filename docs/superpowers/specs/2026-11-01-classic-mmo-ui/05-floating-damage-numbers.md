# 05 - Floating damage numbers

Evidence tags: MEASURED (probe, coordinates cited), OBSERVED (seen by eye in a zoomed crop), INFERRED (domain knowledge or guess). All coordinates are screenshot px in `reference/1.webp` (489x632), origin top-left.

## Summary

1. The frame holds about 29 numeric hits plus two white "Miss" labels, in two loose columns over a white-hot explosion (MEASURED). The columns rise from a target zone hidden by the blast (INFERRED).
2. Digits are one heavy, wide, blocky bitmap face in four tints: blue `#0A9AFE`, teal-green `#15E8B5`, gold `#FDB442`, and one cyan row `#1FD3EE`. Each has a dark rim of the same hue. Fills are flat and opaque, not additive. MEASURED.
3. Size is a continuum, not two classes: 40 px fill at the bottom of a column, then 29, 20 and 14 px within about 36 px of rise. Numbers spawn huge and shrink fast. MEASURED sizes, INFERRED motion.
4. The stack keeps the newest number on top, which is why it survives heavy overlap. Only about 5 of 29 numbers are fully visible, and about 3 more mostly readable. OBSERVED.
5. Value ratios suggest blue is critical, green is excellent (1.2x) and gold is normal; `DMG_IMPL.md` adds cyan as ignore defense. The image cannot prove this. INFERRED.

## Sources

- `reference/1.webp`, one frame. It is scaled, compressed and AI-upscaled, so single pixels are not native. JPEG-like ringing, halos and 1-2 px colour shifts are treated as noise.
- Crops at 5x-20x: `damage-numbers/` next to the probe script (`a1 a2 b1 b2 c c2 d d2 e f f4 f0 miss .png`).
- Reference unit: **B** = height of the big digit `44300` including rim = 50 px (y 328-377, MEASURED `vline 290`). Fill alone is 40 px (y 333-372), so 0.8 B. Every size ratio in this spec is fill height / 40. B is used only for rise distance.
- Frame-relative sizes: big fill = 6.3% of frame height, small fill = 2.2%. The frame may be cropped, so these are approximate.

## Number inventory

"Top" is the first row of the digit tops (MEASURED by `vline` 178, 236, 276, 318, 340 and row colour counts, +/-2 px). "Adv" is the digit advance. "Fill" is the top-10% brightest fill in that band. Row order within a column is top to bottom, which is oldest to newest (INFERRED) and back to front (OBSERVED).

Readings marked `?` are illegible digits. Rows marked "arcs" show only the tops of their digits.

### Right column (x about 258-345 up top, 223-411 at the bottom)

| # | Value | Family | Top y | x span | Fill height, fill/40 | Visibility |
|---|---|---|---|---|---|---|
| R1 | 106252 (5th digit could be 6) | green, dim `#2CB57C` | 94 | 258-345 | about 14 px, 0.35 | top half, 6 digits |
| R2 | illegible, 6 digits, a tall leading 1 | green/cyan | about 108 | 262-350 | about 14 px | scraps behind R3-R5 |
| R3 | 81120 | deep orange `#F38422` | 107 | 268-345 | about 14 px | top 60% |
| R4 | 84?28 (read 84428 or 84478) | gold `#FDB442` | 120 | 268-345 | about 14 px | top half |
| R5 | 8?328 | gold `#FEB440` | 130 | 268-345 | about 14 px | top half |
| R6 | 53126 | green `#41E6C0` | 138 | 266-340 | about 14 px | top 70%, covered by R7 |
| R7 | 42784 | gold `#FCB23A` | 150 | 265-345 | 15 px, 0.38 | full |
| R8 | 53978 | green `#45E8A5` | 175 | 266-339, adv 14.6 | 15 px (y 175-189), 17 with rim, 0.38 | full |
| C1 | ?1758 (21758 or 31758) | cyan `#1FD3EE`, median hue 186 vs 205-208 for blues | 214 | 266-328 | about 14 px | most, bottom overlapped |
| C2 | 44690 or 44890 | blue `#3C9DF5` | 225 | 263-332 | about 14 px | most, broken by bright gaps |
| C3 | illegible, ends ?69? | blue | 239 | 265-350, adv about 17 | about 17 px, 0.43 | arcs |
| C4 | 5?5?6 family, illegible | green `#15E8B5` | 248 | about 248-376 | about 27 px est. | arcs |
| C5 | illegible | green | 258 | 245-380, adv 27 | about 27 px, 0.68 | arcs, cut by E |
| E | **44284** | blue `#0C9AFE` | 270 | 223-411, adv 37.6 | 38-41 px fill (y 274-314), 46 with rim, 0.94 by advance | full, topmost in column |

### Left column (x about 165-245 up top, 121-321 at the bottom)

| # | Value | Family | Top y | x span | Fill height, fill/40 | Visibility |
|---|---|---|---|---|---|---|
| L1 | illegible | gold | 163 | 165-240 | about 14 px | 2-3 px arcs only |
| L2 | 52??? | green `#4EE0B8` | 168 | 165-240 | about 14 px | arcs, top third |
| L3 | ??688 | blue `#41A2F2` | 175 | 165-240 | about 14 px | arcs, top half |
| L4 | 28588 | gold `#FBB53C` | 184 | 165-240 | about 14 px | top 60% |
| L5 | 44280 (third digit could be 5) | blue `#229BF7` | 194 | 165-240 | about 14 px | top 70% |
| L6 | 44990 | blue `#2B9BF9` | 206 | 165-240 | about 14 px | most |
| L7 | 73148 | deep orange `#F28B2C` | 220 | 166-240 | about 14 px | top 80% |
| L8 | 73148 | gold `#FEAF30` | 233 | 166-241 | 15-16 px, 0.39 | full |
| D1 | 52012? | green `#47E0BB` | 263 | 164-240, adv 15 | about 14 px | arcs |
| D2 | 19666 | gold `#FBB54A` | 273 | 165-235 | about 14 px | top half |
| D3 | 2?79? | gold | 282 | 165-232 | about 14 px | top half |
| D4 | illegible, maybe a third gold row | gold | about 291 | 165-225 | about 14 px | scraps |
| D5 | illegible | green | 298 | 159 to under E, adv about 20 est. | about 20 px, 0.50 | arcs |
| D6 | 5???? starts with 5, then illegible | green `#18E7C4` | 308 | 143-288, adv 29 | about 29 px, 0.72 | top half, cut by F |
| F | **44300** | blue `#099BFF` | 328 | 121-321, adv 40 | 40 px, rim to 50, 1.00 | full, topmost in column |

### Text labels

| # | Text | Family | Glyph box | Notes |
|---|---|---|---|---|
| M1 | Miss | white `#F1EDEF` | x 190-246, y 78-97 | MEASURED height 19 px, 0.47 |
| M2 | Miss | white, a little dimmer | x 192-246, y 100-118 | pitch to M1 22 px, overlap 0 |

MEASURED `vline 195`, `hline 90`, `miss.png`.

Family tally (OBSERVED, +/-2): gold or orange 11, green 10, blue 7, cyan 1, Miss 2.

### Hidden-context elements

Two bars sit in the columns (MEASURED `hline 196`, `hline 246`, `vline` at x 225, 300, 350). Left, x about 166-241, y 243-250: dark, `#261202`-`#3F181A`; the orange runs along it are L8 digit bottoms. Right, x about 264-364, y 190-200: a dark-outlined rectangle with a red-orange interior (`#A03D18`, `#D9642C`), possibly an HP fill. Digits draw over both. INFERRED: they are monster HP or name plates, which would make the columns two targets.

## Colour families

### Tokens

| Token | Hex | Basis |
|---|---|---|
| `dmg.blue.fill` | `#0A9AFE` | MEASURED `44284`, `44300` means; runs `#0E96FE`, `#05A1FC`, `#089BF6` |
| `dmg.blue.rim` | `#001750` | MEASURED `hline 300` x 237-240 (`#001750`), x 275-279 (`#00176B`), `hline 362` x 160 (`#00367D`) |
| `dmg.blue.edge` | `#0062C9` | MEASURED ramp between fill and rim, x 253-255 on `hline 362` |
| `dmg.green.fill` | `#15E8B5` | MEASURED C5, D6; runs `#03E8A6`, `#13EBB4`, `#01E8AC` |
| `dmg.green.fillSmall` | `#45E8A5` | MEASURED R8. Paler from blur, so treat it as the same tint |
| `dmg.green.rim` | `#00331C` | MEASURED `#002D20`, `#00421F`, small row `#00380A` |
| `dmg.ignore` | `#1FD3EE` | MEASURED C1 (x 266-330, y 214-226), top-10% saturated mean, median hue 186 |
| `dmg.ignore.rim` | `#00344A` | MEASURED C1 darkest pixels `#003448`, `#003955`, `#00384A` |
| `dmg.gold.fill` | `#FDB442` | MEASURED R4, R5, R7, L4; runs `#FFA940`, `#FFAC39` |
| `dmg.orange.fill` | `#F28A28` | MEASURED R3 `#F38422`, L7 `#F28B2C` |
| `dmg.gold.rim` | `#4A1800` | MEASURED `#401100`, `#552200`, `#5B1700` |
| `dmg.miss.fill` | `#F7F3F7` | MEASURED peaks `#FFFCFF`, `#F5EFF8` |

### Findings

- **Flat fill.** MEASURED. `44284` at x=400 stays `#089BF6` from y 284 to 313. `44300` at x=290 stays `#0E98FD` from y 340 to 368. No designed top-to-bottom gradient. The fill has a 4-8 px darker ramp at its top (`#1287DA`, `#047ED9`), which is one texel of bilinear blend into the rim. There are small tone shifts around the crossbar at x=185 (`#0879D3`, `#04A4FF`, `#0872BE`); these are unconfirmed and could be compression.
- **Two-tone look on small rows is false.** OBSERVED. L1-L3 look like yellow-green-blue digits, but those are three hidden rows peeking out above each other. Do not build a gradient from them.
- **Blend mode: opaque or alpha, not additive.** MEASURED. `44300` sits on background `#FFFFFF`/`#FEFEFE` and its fill stays `#0E96FE`. Additive would have clipped to white. The same holds for green at `hline 319` (`#05E7BD` next to white x 140-141).
- **Rim hue follows the fill.** MEASURED. Each rim is a dark colour of its fill hue, but not a multiply: channel ratios run from about 0.09 to 0.33 of the fill, and differ within one family. A grey rim times a tint cannot produce the rim tokens, so each type needs its own rim colour.
- **Rim thickness scales with the digit.** MEASURED. About 3-5 px on B-size digits, 1.5-2 px on small ones, ratio about 2.9, close to the 2.7-2.9 digit ratio. INFERRED: rim is baked in the texture.
- **Rim edge is soft.** MEASURED. Outer rim over white reads grey-blue `#6B8394`, `#7E8691` (x 120-124 on `hline 362`), and neutral `#333A43` under the crossbar (x=140, y 368). That is a half-texel alpha fade, or a dark neutral shadow.
- **Shadow.** OBSERVED, weak. Rim is heavier to the right and bottom. Inside `0`, navy runs x 267-270 on the right side and only grey-blue x 256-258 on the left (`hline 362`). Under the `4` crossbar there is a 4-5 px dark band (x=140, y 367-371). Treat it as a 1-texel down-right hard shadow.
- **Against white-hot.** OBSERVED. The dark rim, not the fill, does the separating. Blue has the best contrast on white and on the orange rim of the blast. Gold is weakest on fire (`#FDB442` on `#E87030` background), and only its brown rim holds it. Green is strongest on both.
- **Fade.** INFERRED from measured peaks. R1, the oldest row, peaks at `#2CB57C` over a near-black background, while same-family R8 peaks at `#45E8A5`. That is about 0.78 of the green channel, consistent with alpha fading at end of life. R3 is not faded, so this is one data point. Compare also M2 slightly dimmer than M1, against the same background family.
- **Deep orange vs gold.** MEASURED, unexplained. R3 and L7 (`#F28A28`) differ in hue from the other gold rows (`#FDB442`). L7 and L8 have the same value but different hue, so hue does not follow value. It may be a fifth style, or alpha fade over fire. See Open questions.
- **Yellow.** No distinct lemon yellow family was found. MEASURED: highest hue readings stay near `#F5CF51`, on a single `8` in L8 where it blends with its background. "Yellow" rows in the source are the gold family.

## Size hierarchy

MEASURED, as fill height / 40 px:

| Row | Top y | Advance | Fill | fill/40 |
|---|---|---|---|---|
| F | 328 | 40 | 40 | 1.00 |
| E | 270 | 37.6 | 38-41 | 0.94 (by advance) |
| D6 | 308 | 29 | about 29 | 0.72 |
| C5 | 258 | 27 | about 27 | 0.68 |
| D5 | 298 | about 20 | about 20 | 0.50 |
| C3 | 239 | about 17 | about 17 | 0.43 |
| All others | up to 94 | 14-15 | 14-15 | 0.35-0.37 |

- Small to big ratio is 1:2.7 by advance and 1:2.9 by fill. MEASURED. This ratio is the only size evidence that carries over to other resolutions.
- **Newest is biggest.** OBSERVED. Size order matches y order inside each column, and size drops to the floor within about 36 px of the column bottom. Left: F y 328 (40), D6 y 308 (29), D5 y 298 (20), D-rows y 292 (14). Right: E y 270 (37.6), C5 y 258 (27), C3 y 239 (17), C2 y 225 (14).
- **Not type-locked.** OBSERVED. Green D6 and C5 are intermediate size, so green also spawns large. Small blues exist, so blue does not stay large.
- **Not value-driven.** MEASURED. The largest values (106252, 81120, 73148) are at the smallest size. Big size is unrelated to value.
- The two biggest being both blue may be chance, or blue may start slightly larger. One frame cannot separate these. INFERRED.
- Miss has fixed height 0.47. It does not appear to take part in the pop. OBSERVED once.

## Glyphs

- **Type:** one bitmap digit set. OBSERVED. The same shapes appear at every size. Heavy, wide, squat grotesque with a slight blocky feel.
- **Pixelation:** MEASURED stairs on the `4` diagonal: dark edge x is 140 down to y 335, then steps to 137, 134, 130, 127, 124, 120 at y 336, 340, 344, 350, 353, 358. That is a step of about 3.3 px in x and about 4 px in y. Texel at B size is about 3.5 px, so the native digit is about 11 texels of fill, 14 with rim. INFERRED: a tiny digit texture magnified about 3.5x with little filtering. Small rows look soft because they sit near 1.2x.
- **Proportions:** MEASURED. Digit box about 36 wide x 50 tall with rim. Advance 40 at B size. Advance/fill height is 1.0, which is wide. Rims nearly touch; gap between `4` and `4` is 2-3 px (x 157-160). Monospaced: advances are constant across digits, and `1` keeps a full cell (OBSERVED in 73148).
- **Stroke weight:** MEASURED. Vertical stems 10-12 px (`4` stem x 182-193, `0` leg x 246-255), or 0.25-0.30 of fill height. Horizontal strokes 6-7 px (crossbar y 360-366, `0` top y 329-335), or 0.17. Stems are noticeably heavier than bars.
- **Counters:** tiny. `0` clear counter is 6 px wide inside 40 px height (x 260-265). `4` counter is a small dark triangle about 8x9 px (`f4.png`). In small rows, counters hold only 2-3 px, so they read as dark dots.

### Stroke notes per digit (OBSERVED)

| Digit | Seen in | Shape |
|---|---|---|
| 0 | 44300 | Tall rounded rectangle, near-straight sides, narrow slot counter. |
| 1 | 73148, 81120, 106252 | Plain stem, a short flag at the top left, no foot serif, centred in a full cell. |
| 2 | 44284, 42784 | Round top bowl, straight diagonal to a wide flat base. Heavy base. |
| 3 | 44300 | Round, two bowls, the waist notch opens left. The upper-left terminal hooks down. |
| 4 | 44284, 44300 | Closed triangle, diagonal from top of stem to far left, crossbar extends a little past the stem to the right, stem drops below the bar. The most recognisable glyph. |
| 5 | 53978 | Flat top bar, short vertical left, round lower bowl. |
| 6 | 19666 (top only) | Round top with a curled terminal. Lower half illegible. |
| 7 | 42784, 53978, 73148 | Flat top bar, straight diagonal to bottom centre-left. No crossbar. |
| 8 | 44284, 42784, 53978 | Two stacked rings, lower ring slightly larger. Two round dot counters. |
| 9 | 53978 | Closed upper ring, tail curving to bottom left. |

### Miss

Proportional sans, thin stroke about 3 px, anti-aliased, Arial-like, no tinted rim. MEASURED `hline 90`, `miss.png`. It clearly comes from a different render path to the digits. INFERRED: the client's ordinary UI font. There is a faint dark halo (`#08020B` at x=200 against `#241C23` background), about 1 px, which could be a shadow or ringing.

## Stacking and motion

### Observed layout

- **Two columns.** MEASURED. Left upper rows centre x about 203, right upper rows centre x about 303, 100 px apart.
- **Bottom rows centre further right.** MEASURED. Left: 202, 215 (D6), 221 (F). Right: 303, 313 (C5), 317 (E). Drift is 15-20 px over the column. It could be jitter, or a screen-space effect of the anchor. Big numbers are centred on an anchor, so width alone does not explain the drift. INFERRED.
- **Column height:** 165 px left (y 163-328), 176 px right (y 94-270), or 3.3-3.5 B.
- **Row pitch, MEASURED top y.** Left: 163, 168, 175, 184, 194, 206, 220, 233, then a 30 px gap to 263, 273, 282, 291, 298, 308, 328. Pitches in the upper run are 5, 7, 9, 10, 12, 14, 13, growing toward the bottom. Right: 94, 107, 120, 130, 138, 150, 175, then a 39 px gap to 214, 225, 239, 248, 258, 270. Upper pitches are 13, 13, 10, 8, 12, 25, so no clear trend.
- **Inferred from that:** the left pitch series suggests rise that decelerates and stalls near the top. Pitches of 5-7 mean rows pile into each other. Hits are bursty, not uniform: runs at 8-14 px pitch are split by the 233-263 and 175-214 gaps.
- **Horizontal jitter is tiny.** MEASURED. Left edges in the same column differ by 1-4 px among rows of equal size.
- **Vertical gaps** near E and F: rows D6 and C5 tuck directly behind the big digit.
- **Miss:** at x 190-246, above the left column, pitch 22. No overlap: 19 px height on 22 px pitch. This suggests either a slower rise, or a different anchor height.
- **Draw order:** lower covers higher everywhere legible: R7 over R6, L8 over L7, E over C5, F over D6, B over G in L2/L3. MEASURED via `b1`, `a2`, `e`, `f`. C3 vs C4 is ambiguous. The blue `0` at about x 318, y 250 seems to punch through green. That could be a gap in the green glyph.
- **Numbers draw over effects and HP bars.** OBSERVED. INFERRED: 2D overlay pass after the 3D world.

### Inferred motion (all INFERRED, suggested curves)

The original probably uses a world-space gravity integrator. These numbers aim to reproduce the look.

| Parameter | Value | Reasoning |
|---|---|---|
| Lifetime | 1.4 s | 165 px height, then fade |
| Rise total | 3.4 B (about 2.1 B of it in the first 0.5 s) | Measured column height |
| Rise curve | `y(t) = H * (1 - (1 - t)^2.2)` with t in 0..1 | Suggested ease-out. No fit is claimed: bursty hits mean pitch does not map to velocity |
| Scale pop | start 1.0 B-relative, floor 0.36 | Measured continuum |
| Pop curve | `s(t) = 0.36 + 0.64 * (1 - clamp(age / 0.15))^2` | Floor reached in about 36 px of rise |
| Fade | alpha 1.0 until 75% of life, linear to 0 at 100% | R1 about 0.78 near the top |
| Miss | fixed size, lifetime 0.9 s, rise 0.8 B, no pop | M1 and M2 keep one pitch |

Pop length: at 0.15 s (t = 0.107) the suggested rise curve gives 0.22 H, or about 37 px, the rise over which size reaches the floor. Size against rise is what was measured, not time, so a slower rise with a longer pop serves equally well. Validate by screenshot against the size table.

## Colour meaning

### What the image proves

Nothing about meaning. MEASURED facts are only: four tints, tints mixed within a column, and values cluster by colour.

### Value clusters

| Tint | Read values | Note |
|---|---|---|
| Blue | 44300, 44284, 44280, 44990, 44690 | Narrow band around 44.3k |
| Cyan | ?1758 | One row, its own hue, so not part of the blue band |
| Green | 53978, 53126, 52012?, 106252 | 53k band, plus 106252 read from top halves only |
| Gold/orange | 42784, 28588, 19666, 73148, 81120, 84?28, 8?328 | Wide, 19k-84k |

Ratios, arithmetic on readings, so INFERRED by interpretation:

- 53126 / 44284 = 1.1997. 53160 would be exactly 44300 x 1.2.
- 73148 / 2 = 36574; 81120 / 2 = 40560. Both sit in the gold band. 73148 appears twice.

### Hypotheses

| # | Hypothesis | Support | Against | How to tell |
|---|---|---|---|---|
| H1 | Tint = damage type. Blue critical (fixed max hit), green excellent (max x 1.2), gold normal (variable), cyan ignore defense, doubled gold rows are "double damage" | The ratios above. Blue's tight band and gold's wide spread fit. INFERRED memory of MU rules, unverified: blue critical, green excellent, orange normal; palette near `(0,0.6,1)`, `(0,1,0.6)`, `(1,0.6,0)` as float tints, which match measured blue closely and green and gold approximately | 73148 being gold above critical is only explained by doubling | Video: does the same attacker on the same target produce every tint? |
| H2 | Tint = attacker or party member | Mixed tints in one column could be several players | Values cluster by tint at a neat 1.2x ratio; three players would not do that | Solo footage: tints should collapse to one |
| H3 | Blue = damage by another party, green/gold = own | None specific | Same as H2. The biggest numbers being blue would be odd | Same test |
| H4 | Blue = shield or SD hit | Tight band could be a shield-capped hit | Season 2 style usually predates SD. INFERRED. Targets look like monsters | Look at a PvP clip, watch `gauge.left` and `gauge.right` (SD and AG are dream design labels, not read from these frames) |
| H5 | Reflect or poison use one of the tints | MU has such types. INFERRED | No evidence here. These would not produce a 44k band every tick | Status effect visible on target |
| H6 | Server customisation of colours | Private server; high numbers show it is modified | Tints still match stock-looking palette | Compare against stock client footage |

Working assumption for dream: **H1**. Tag it as design choice, not a fact.

Miss: H1 needs no explanation. It reads as "attack did not connect", and the plain white distinguishes it from damage. INFERRED.

## Readability

Why it works:

- Each digit is wide and heavy with a dark rim, so it reads against fire, stone and white.
- Newest on top at 2.7x size means the eye locks to the latest two hits.
- Hue alone gives hit type at a glance without reading any digits.

Where it fails:

- OBSERVED. About 21 of 29 rows are partly or fully unreadable, and pitch 5-14 px is less than the 14-15 px digit height.
- Hidden rows produce false two-tone stripes.
- Gold on fire has weak separation.
- Rows look like one noisy block, so individual values are lost after about 0.3 s.

Keep: flat tint fill, hue-dark rim, big spawn pop, newest on top, monospaced wide digits, two-column separation by target.

Improve for dream:

- Enforce a minimum pitch of 0.8 x current height for settled rows.
- Cap per target, and merge only past that cap (see below).
- Use a 1-texel stronger rim on gold.
- Fade old rows sooner under pressure.

## Build recipe

Art must be self-drawn. Reproduce the proportions and behaviour, not any texture from the original game.

### Glyph atlas plan

- Draw digits `0-9` on a 12x16 texel cell in a two-channel atlas: R is the fill mask, G the rim mask. Fill 9x11, rim 1 texel, plus a 1-texel down-right shadow included in the rim mask. Masks never overlap, so R + G is at most 1.
- Stems 3 texels, bars 2 texels, to match 0.27/0.17 of fill height. Square-ish rounded corners. Counters only 1-2 texels wide.
- Monospaced advance 11 texels. `1` keeps its cell.
- Add `+`, `,`, and text labels if needed. Render `Miss` from the existing UI font rather than the digit atlas, or draw a thin set.
- Filtering: nearest at large scale for the blocky stair look; linear when scale is under 2x so small rows do not shimmer. If one sampler only, choose nearest with integer-ish scales.
- Alpha: hard fill, 1 soft edge texel on the rim.

### Per-type style

Scales are fill/40. Only the ratio 2.7-2.9:1 is evidence, because `reference/1.webp` is a crop of unknown scale. If `Miss` uses the UI font, img1 is about 1.4x img2, which puts the source at about 8 px floor and 23 px spawn at 1080p. Design choice for dream: floor 24 px, spawn 68 px of fill height at 1080p-tall output, converted with the scale rule in 03.

| Type | `fill` | `rim` | Spawn scale | Floor scale | Motion | Lifetime |
|---|---|---|---|---|---|---|
| Normal | `#FDB442` | `#4A1800` | 0.85 | 0.36 | standard | 1.4 s |
| Critical | `#0A9AFE` | `#001750` | 1.00 | 0.36 | standard | 1.4 s |
| Excellent | `#15E8B5` | `#00331C` | 1.00 | 0.36 | standard | 1.4 s |
| Ignore defense | `#1FD3EE` | `#00344A` | 1.00 | 0.36 | standard | 1.4 s |
| Double | as hit type | as hit type | 1.00 | 0.36 | second copy at +60 ms, +12 px y | 1.4 s |
| Alt orange (reserve) | `#F28A28` | `#4A1800` | 0.85 | 0.36 | standard | 1.4 s |
| Miss | `#F7F3F7` no tint | none, soft shadow | 0.47 fixed | 0.47 | short rise, no pop | 0.9 s |
| Incoming (placeholder) | `#FF0000` | `#400000` | 0.85 | 0.36 | standard | 1.4 s |
| Reflect (placeholder) | `#800080` | `#200020` | 0.85 | 0.36 | standard | 1.4 s |
| SD absorbed (placeholder, dream label) | `#8B8000` | `#232000` | 0.85 | 0.36 | standard | 1.4 s |
| Poison (placeholder) | `#556B2F` | `#151B0C` | 0.85 | 0.36 | standard | 1.4 s |

Spawn 0.85 for normal is a design choice for dream, not measured: it gives crits an edge in the first frames. Set to 1.0 if the original's look matters more. Placeholder rows are not in the frame: fills are the `DMG_IMPL.md` hues and rims are a hue-dark design choice.

### Blend and pass

- Premultiplied alpha, `glBlendFunc(GL_ONE, GL_ONE_MINUS_SRC_ALPHA)`. Never additive.
- Shader, premultiplied, with uniforms `fill` and `rim`: `rgb = (tex.r * fill + tex.g * rim) * fade`, `a = (tex.r + tex.g) * fade`. Fade scales rgb too, so a fading digit stays non-additive.
- Draw after world and effects, with depth test off, in a screen-space pass from projected world anchors.
- Sort: oldest first, so newest is drawn last.

### Spawn and stack (pseudo-code)

```
const LIFE = 1.4, POP = 0.15, RISE = 3.4 * B, MAX_PER_TARGET = 12, MAX_GLOBAL = 64

onHit(target, value, type):
    list = active[target]
    if list.count >= MAX_PER_TARGET:
        merged = newest number in list of this type spawned this tick
        if merged exists:
            merged.value += value; merged.age = 0; return
        o = oldest in list with age < 0.75 * LIFE
        if o exists: o.age = 0.75 * LIFE  // start its fade now; soft cap, list may exceed 12 until it ends
    n = new Number(value, type)
    n.anchor = project(target.head) + (rand(-3, 3), 0)  // tiny jitter, matches measured 1-4 px
    n.age = 0
    list.add(n)
    if total > MAX_GLOBAL: remove oldest with lowest priority (normal first, then excellent, then crit)

update(dt):
    for each n: n.age += dt; if n.age >= LIFE: remove
    for each target list, newest to oldest:
        t = n.age / LIFE
        n.y = RISE * (1 - (1 - t)^2.2)
        n.s = floor + (spawn - floor) * (1 - clamp(n.age / POP))^2
        n.screenY = n.anchor.y - n.y
        // readable-pitch guard, an improvement over the source
        if previous exists and n.screenY > previous.screenY - 0.8 * height(n):
            n.screenY = previous.screenY - 0.8 * height(n)
        n.alpha = t < 0.75 ? 1 : (1 - t) / 0.25

render: sort by spawn time ascending; draw digits centred on anchor x
```

### Caps

- 12 per target is a soft cap: at cap, the oldest unfaded row starts its fade early rather than vanishing, so the list can briefly exceed 12. 64 global is a hard cap.
- Merge only past the cap: a hit joins `merged`, the newest same-type number spawned in that tick on that target, and shows the sum. This never fully hides a crit.
- Keep at most 2 Miss per target, pitch 22/632 of frame height.
- Batch all digits into one quad buffer, one draw call.

## Reconciliation with DMG_IMPL.md

`G:\workspace\projects\dream\DMG_IMPL.md` is a written brief; the pixels win on look. Its hue-to-meaning mapping agrees with H1: regular gold/orange, critical blue, excellent green, ignore defense cyan. That mapping stays INFERRED.

| Topic | `DMG_IMPL.md` | Build this (from the frame) |
|---|---|---|
| Colours | `#FF6600`, `#0066FF`, `#00FF00`, `#00FFFF` | `#FDB442`, `#0A9AFE`, `#15E8B5`, `#1FD3EE` |
| Edge | Glow, aura, shimmer, 1 px black stroke | No glow. Opaque fill with a hue-dark rim |
| Scale over life | 0.2, overshoot to 1.1, settle at modifier | Starts large, shrinks to 0.36. No growth phase |
| Per-type scale | 1.0 to 1.25 modifiers | Per-type style table above |
| Draw order | Z priority by type | Newest on top, whatever the type |
| Spacing | Kerning -5% to -10% | Monospaced, advance = fill height, no negative kerning |
| Per-target cap | 4 active | 12, soft |
| Excellent value | Max x 1.10 | Max x 1.20 (53126 / 44284 = 1.1997). INFERRED from readings |
| Merging | Optional during phases 1-2 | Only past the cap |
| Lifetime | 1.0 s | No evidence in one frame. Keep 1.4 s, tunable. Existing code runs about 1.67 s, not checked here |

Incoming, Reflect, SD absorbed and Poison do not appear in the frame. Their rows in the style table are placeholders using `DMG_IMPL.md` hues with a hue-dark rim.

## Open questions

1. Is scale driven by age alone, or do crits get a larger starting scale? Needs consecutive frames.
2. Timing: real lifetime and pop duration are unknown. The curves above fit spatial measurements from one frame, so absolute seconds are guesses.
3. Is `#F28A28` a fifth type (R3, L7), or a fade or background effect? L7 and L8 share value but differ in hue.
4. Do tints mean what H1 says? The 1.2x ratio is strong, but C1 is a single row and several readings are uncertain.
5. Do the columns map to two targets? The bars imply it, but both target models are hidden by the blast.
6. Is the rim shadow really directional, or only compression bias?
7. Why does Miss sit above the left column with a wider pitch: different anchor, or a slower motion?
