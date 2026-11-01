# Classic MMO UI spec pack

Visual specification of a MU Online style interface, reverse-engineered from 11 screenshots of a Season 2 private server video, as the target look for `dream`'s UI. A reader who has not seen the screenshots should be able to rebuild the look from these files.

This is analysis only. No code changed. The implementation plan belongs to the roadmap session that receives this pack.

## Files

| File | Covers |
|---|---|
| [01-inventory-panel.md](01-inventory-panel.md) | Inventory window, equipment slots, bag grid, Zen row, buttons, combine panel |
| [02-items-and-tiers.md](02-items-and-tiers.md) | Item icons, sizes in cells, tooltips, tier table, icon recipe |
| [03-status-bar-hud.md](03-status-bar-hud.md) | Bottom bar, gems, gauges, EXP, Q/W/E, skill tile, chat, actor bars, system messages |
| [05-floating-damage-numbers.md](05-floating-damage-numbers.md) | Damage number families, glyphs, stacking, build recipe, reconciliation with `DMG_IMPL.md` |
| [04-dream-gap-map.md](04-dream-gap-map.md) | What dream's code has today, gaps with effort, suggested order |
| [06-review-findings.md](06-review-findings.md) | Pixel review: what was wrong, and how it was resolved |
| [reference/](reference/) | The 11 source screenshots |

## How to read the claims

- **MEASURED**: taken from the pixels with coordinates. **OBSERVED**: seen by eye. **INFERRED**: domain knowledge or a guess. Treat INFERRED as a design suggestion, not a fact.
- The screenshots are scaled, compressed, AI-upscaled video frames at different scales, so colours are approximate and sizes are given in units.
- The source is a single video. Motion, timing, hover and drag are never proven by these stills, so those rules are INFERRED.

## Shared rules

These override anything that seems to differ in a single spec.

**Units**

| Unit | Meaning |
|---|---|
| u | Logical pixel of a 640x480 layout |
| c | One bag cell, 20 u |
| H | Bottom bar height, 48 u |

Screenshot scale for images 2 and 3 is 2.817 px/u, so the bar spans u 433-480 and the inventory window is about 189 u wide. The black strip under the bar in image 2 is treated as video letterbox.

**Scale**: `s = screenHeight / 480`, snapped to multiples of 0.25. 1080p needs 2.25 to fill 480 u. Anchor rules for 16:9 are in 03, Responsive rule.

**Text**: drawn at screen pixels, not scaled with the art. Layout positions text in u; the font size stays a crisp native size for the current resolution. Use an original or licensed font in a small system-UI feel. Exception: damage numbers use their own digit atlas (05).

**Ownership when elements overlap**

- Dragon heads belong to the HUD bar, not the inventory window.
- Close, Repair and Store buttons belong to the inventory window.
- Combat text is owned by 05. Item and tooltip rules are owned by 02. Window chrome and grid are owned by 01.
- Combine-panel text such as `Improper items for combination` belongs to the combine panel, not to system notices.

**Damage numbers**: pixels win over `DMG_IMPL.md` on look. Hue meaning agrees between them: gold regular, blue critical, green excellent, cyan ignore-defence. All meanings remain INFERRED; the strongest evidence is the ratio 53126/44284 = 1.20.

**Gauges**: use the neutral names `gauge.left` and `gauge.right`. Nothing in the art labels them, and Season 2 predates SD.

**Art**: recreate the style with original assets. Don't extract or trace the source game's art, icons or fonts, and don't reuse its names or branding.

## Decisions the roadmap owner must make

1. **Camera fit.** The reference is an isometric, mouse-driven game. dream grabs the mouse in play, so a clickable bar needs a click mode or held key (04, section 3).
2. **Paper doll scope.** Full reference layout needs more equipment slots than the four on `integrate/engine-iteration`. Show the extra wells as empty, or add slots?
3. **Equipped items.** They alias a bag slot today. A paper doll that lifts items out of the bag needs a server rule change (04, section 4).
4. **Multi-cell bag.** Item footprints are central to the look, but the grid is 1x1 across client, server and wire. It is the largest single gap.
5. **Missing systems.** Mana, second gauge, skills, hotbar, chat, gold and combine do not exist. Hide their UI, or show it inert?
6. **Tier mapping.** dream has three rarities. 02 proposes the recolour, including rose `#F08FB0` for Uncommon, so that it doesn't collide with the blue bonus text.
7. **Base branch.** 04 recommends `integrate/engine-iteration`, which has rarity, equipment and intents that `master` lacks.

## Biggest remaining uncertainties

- Gem and gauge fill behaviour: every sample looks full.
- Orange and purple gauge meaning, and the EXP `0` counter.
- Cause and period of the axe's orange, pink and lilac shift, and the spear aura pulse.
- All damage-number timing, and whether the two columns are two targets.
- Drag visuals: no frame shows an item on the cursor.
- Tooltip overlap of 0.5 c rests on two samples; clamping is unobserved.
- `[Set option]` function; Zen's last three digits; the disc item's identity.
