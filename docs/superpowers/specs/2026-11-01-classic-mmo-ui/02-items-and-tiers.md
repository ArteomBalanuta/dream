# 02 - Items, tooltips and item tiers

Confidence tags: **MEASURED** (probe result, image and coordinates cited), **OBSERVED** (seen by eye in a zoomed crop), **INFERRED** (domain knowledge or guess). Coordinates are screenshot px as `x,y` in the named file.

Base unit: **1 cell (`c`) = one inventory grid cell = 56.3 px** in images 4, 5, 6, 7, 8, 9 and 10, range 56.2..56.5 (MEASURED: 5.webp grid x 568..1018 over 8 columns; 10.webp lattice peaks at x 81.5, 137.5, 193.5, 250.5). Image 11 is a smaller frame at 2.00 px per unit: `c` = 39.9..40.0 px (MEASURED: 11.webp grid x 36..356).

Units and rendering: 1 c = 20 u, and art scale snaps to 0.25 steps. Text is drawn at screen px and is not scaled with the art, so every text size below is a ratio measured from the screenshots, not a scaled asset size. The shared units table and font rule live in 00-README.

## Summary

1. Items are full-colour, unframed 3D model pictures drawn straight onto a very dark cell, sometimes spilling out of their footprint. Occupied cells turn near black; the hovered item's cells turn dark navy.
2. The tooltip is a borderless, centred text block on 80 % black, sized to its longest line, placed above the item when it fits and below otherwise. Only item text and solid red class bands appear in it.
3. Name colour is the main tier signal: white (plain or wings), gold (high +N), green (Excellent). Blue is for every bonus line, white for base stats.
4. Icons also carry a tier signal. The +7 Excellent axe's colour moves through orange, pink and lilac while neighbouring icons stay constant, so it is an item-specific animated effect, not lighting. A red spear has a soft aura whose strength differs between frames. The +10 bone armour is blown out to near white.
5. For dream: bake a lit base icon plus masks offline, then add a runtime icon shader with tint, scrolling reflection and an additive aura. Ancient, socket and set tiers are not in the evidence.

## Sources

| Image | Scale | Content used here |
|---|---|---|
| 6.png | c=56.3 | Bone Armor +10 tooltip (above item), red spear with strong aura, hovered white armour on navy cells |
| 7.png | c=56.3 | Wings of Elf tooltip (above item), crossed by system notices |
| 8.png, 9.png, 10.webp | c=56.3 | Excellent Larkan Axe +7 tooltip (below item), combination window grid with wings, axe, gem, feather |
| 4.webp | c=56.3 | Inventory, axe orange, gold helm and armour, pants, bow |
| 11.webp | c=40 | Same inventory, axe peach and lilac |
| 5.webp | c=56.3 | Later inventory, axe lilac, spear with dim aura. Gold helm, green pants and one amber gem are gone |
| 3.webp | n/a | Shows the top of the inventory, a dark ghost silhouette slot, and the notice `You have successfully created the item`. **It does not show any wings icon or glowing items**, contrary to the brief; wings evidence comes from 7, 8, 9, 10 |
| 1, 2 | n/a | No item content. 1 has combat numbers, 2 has HUD |

All images are compressed, AI-upscaled video frames. Glyph edges are smeared and some letters are unreliable, as listed under Typography.

## Element-by-element spec

### 1. Tooltip panel

- **Fill:** flat black at about 80 % opacity, MEASURED. Stone #3F4042 outside reads #100D0E inside (10.webp, 60..105,600 vs 120..160,600), 21 % transmission. A bright slot edge of #C3C3C5 reads #242425 inside (6.png x=340, y 46 vs 75..77), 18 % transmission. No gradient, no texture, OBSERVED. Background shapes such as cell grid and slot edges remain faintly legible through it. The line at the bottom of the name in 6.png looks like a separator but is a slot edge showing through; there is no title rule.
- **Border:** none, OBSERVED. 2 to 3 px of darker rim on the edge is compression ringing (9.png y=590: #000005 at x 12..13, then #0A0C10). Corners are square.
- **Shadow or glow:** none, OBSERVED.
- **Width rule:** fits the longest line, MEASURED:

| Image | Longest text width | Band width | Panel width | Left inset of band | Right inset of band |
|---|---|---|---|---|---|
| 6.png | 340 | 347 | 358 (x 2..359) | 4 | 7 |
| 7.png | 284 | 289 | 300 (x 7..306) | 2 | 9 |
| 9.png, 10.webp | 379 | 386 | 400 (10.webp x 110..509) | 4 | 10 |

  So panel width is about longest line + 0.3 to 0.4 c (16 to 21 px). The asymmetry looks real: text centre is 2 to 4 px left of panel centre. For dream, use longest line + 0.33 c, centred, and ignore the asymmetry.
- **Vertical padding:** top to the name's cap top is 17 to 21 px, and last line's bottom to the panel bottom is 24 to 28 px (0.45 c), MEASURED (10.webp panel y 450..848, name top 471, last line bottom 824). The top pad stays a range rather than a flat 21 px because only 10.webp gives 21: 6.png gives 18 (y 45 to 63) and 9.png gives 17 (y 218 to 235). Use 0.34 c.
- **Height:** content driven. Axe tooltip height is 398 px, or 7.1 c. Bone Armor is about 295 px, 5.2 c. Text height follows screen px, so these c values hold only at this screenshot scale.
- **Alignment:** every line is centred, MEASURED. Line centre x varies by at most 2 px in 6.png (178..179), 9.png (207..209) and 10.webp (306..308).

### 2. Tooltip line order

Sections are separated by half-line gaps. Line pitch is 25.3 px or 0.45 c, MEASURED (9.png tops 272, 297, 323, 348, then 447, 472, 498, 522, 549, 574). Name to first stat is 37 px or 1.5 pitch.

```
[name, tier colour]                       Excellent Larkan Axe +7
<half-line gap>
[damage or defence, white or blue]        Two-Handed Damage: 109 ~ 122
[attack speed, white, weapons only]       Attack speed: 25
[durability, white]                       Durability: 80
[requirements, white when met]            Strength Requirement: 416
<half-line gap>
[class band, one per allowed class]       Can be equipped by Dark Knight
                                          Can be equipped by Magic Gladiator
<half-line gap>
[skill, blue]                             Falling Slash skill (Mana:9)
[Luck 1, blue]                            Luck (success rate of Jewel of Soul +25%)
[Luck 2, blue]                            Luck (critical damage rate +5%)
[additional option, blue]                 Additional Dmg +8
[excellent options, blue]                 Increase Attacking(Wizardry)speed +7
                                          Increases life acquisition rate after hunt +life/8
```

Exact visible text per tooltip. Letter confusions are noted under Typography.

**Bone Armor +10 (6.png):** name in gold. White: `Defense: 44`, `Durability: 47`, `Strength Requirement: 92`. Red bands: `Can be equipped by Dark Wizard`, `Can be equipped by Magic Gladiator` (renders like "Magio"). Blue: `Luck (success rate of Jewel of Soul +25%)`, `Luck (critical damage rate +5%)`, `Additional defense +8`.

**Wings of Elf (7.png):** name in white. Next line is illegible: a white line at about y 72..83 is crossed by an orange notice and reads like `Defense: 10` at best (INFERRED). White: `Increase 12% of Damage`, `Absorb 12% of Damage`, `Increase speed`, `Durability: 200`, `Minimum Level Requirement: 180`. Red band: `Can be equipped by Elf`. No blue lines. On wings the stat block is one uninterrupted white group.

**Excellent Larkan Axe +7 (8, 9, 10):** the lines in the block above. The damage line is blue while the other base stats are white, MEASURED #9DAED0 vs #FBFBFE (10.webp y 507 vs 532). In the screenshot, `Increase Attacking(Wizardry)speed +7` has no space before `speed`. The last line says `after hunt +life/8`. These appear to be the game's real strings; for dream, write proper text rather than copying the typo.

### 3. Class bands

- Solid opaque red strip behind the centred text, MEASURED core #AD0D01 (9.png 150..300,380..400), slightly brighter #BC0F00 in 6.png. Nothing shows through.
- Width equals the longest line + about 6 px, not the row's own text. All bands in a tooltip have the same width and sit nearly edge to edge inside the panel.
- Height 21 to 23 px or 0.39 c, pitch 25.3 to 26 px, so a 2 to 4 px gap (blurred to #5C0C03 to #690000 by compression). MEASURED 9.png x=40: band 380..400, gap 401..405, band 406..423.
- Text white, reading #FCC5BB after chroma bleed, so OBSERVED as white, not pink.
- No bevel or border. Faint brighter top edge is likely compression.
- **Meaning is INFERRED.** In MU's client, red marks a requirement the current character does not meet. All three tooltips show red for DW, MG, DK and Elf. The worn cape looks like a Dark Lord cape, which fits a character who can use none of these. We have no evidence of the met-class look. Design proposal: class met = plain white text without band; not met = red band.
- Requirement lines appear white here. A red unmet state is INFERRED.

### 4. Placement

- **Horizontal:** centred on the item's footprint, not the cursor, MEASURED within 6 px: 7.png footprint centre 156 vs panel 156.5; 10.webp footprint 311 vs panel 309.5 (x 110..509); 6.png 186 vs 180.5.
- **Above (preferred, INFERRED rule):** bone armour and wings. The panel bottom edge overlaps the item's top by about 0.5 c, MEASURED: 6.png panel bottom 340 vs footprint top about 311 (derived from bottom 423 minus 2 c); 7.png 274 vs about 250. This hides the top of the icon. Rule: panel bottom = footprint top + 0.5 c. Only these two samples support the overlap.
- **Below (flip):** axe. Panel top is flush with footprint bottom with no gap, MEASURED: 10.webp panel top 450 to 452, footprint bottom 454 to 455; 9.png 218 vs 217. Rule: panel top = footprint bottom. The axe tooltip is 7.1 c tall and the item sits at about y 286, so it could not fit above.
- **Inferred rule:** try above, flip below if the panel would cross the screen top. Clamping at screen edges is unobserved but needed.
- **Stacking:** the panel draws over all windows and crosses into neighbouring windows; in 6.png it crosses the frame between combination and inventory windows. It draws under the mouse cursor, INFERRED: no frame shows the cursor overlapping a tooltip. In 7.png system notices draw **on top of** the tooltip, and their text collides with stats. This is a flaw to avoid. dream should draw tooltips above chat and notices.
- **Pointer:** none.

### 5. Cell states behind items

| State | Colour | Evidence | Confidence |
|---|---|---|---|
| Empty | raised stone #404143, bevelled | 5.webp 700..720,800..830; 4.webp 60..80,770..800 | MEASURED |
| Occupied, not hovered | flat #232425, lattice vertical #444648 to #4F4F4F, horizontal #303234 | 4.webp, 5.webp and 11.webp, axe and gold armour palettes; 10.webp wings | MEASURED |
| Occupied, hovered | dark navy #1C2030 to #1B2032 | 9.png 160..215,110..210; 10.webp 258..278,296..330; 7.png 75..120,285..320; 6.png 130..145,345..415 | MEASURED |

**The navy is hover-only, not occupation-based.** The same chaos-box slots are near black with wings unhovered (10.webp) and navy with wings hovered (7.png). In 6.png the armour is navy, and in 4 and 5 the same armour is near black. The navy fills the item's full footprint, including corners the icon does not touch. Grid lines inside stay visible: vertical #323848, horizontal #262939 (MEASURED 7.png, 10.webp; compressed samples range up to #3B4155 and #313343). It is a rectangular block with square edges. There is no hover border, no brightening of the icon, and no change for the tooltip.

Stone cells lose their raised look completely when occupied, so the grid reads as sunken wells around items. Footprints form clean rectangles. Bevel details belong to the inventory panel analyst.

### 6. Item icon style

OBSERVED across all crops:

- Pictures of 3D models, seen in three-quarter view, with weapons tilted diagonally from top left to bottom right.
- Low-poly silhouette with sharp edges, no outline, no background, no drop shadow, no frame or rarity border.
- Light from upper left or camera front, with very high contrast: near-black crevices next to bright speculars. Metals have broad smooth highlights (the grey placeholder slots show this best, though those are the equipment ghosts, not items).
- Saturated, painterly low-resolution textures.
- Drawn with no clipping. They overflow the footprint when the model's bounding box is wider, MEASURED: white armour's spike reaches 11 px, or 0.2 c, left of the grid (extent x=28 in 4.webp vs grid left 39; x=557 in 5.webp vs grid left 568). Bow reaches about 18 px, or 0.3 c, below the grid. Cape overflows its equipment slot by about 13 px top and 5 px bottom (5.webp cape y 117..245).
- Icons are scaled to fit the footprint and centred, leaving 15 to 30 % margin. Icon size is not locked to cell multiples. Small items sit small in a full cell: a gem is only 0.23 x 0.44 c.

### 7. Item catalogue

Column `Cells` gives footprint in columns x rows, MEASURED from dark footprint. `Drawn` is the icon's bounding box in px and c from 4.webp unless noted. Names in parentheses are INFERRED; only Bone Armor, Wings of Elf and Larkan Axe have visible tooltips.

| Item | Where | Cells | Drawn | Palette | Material and treatment |
|---|---|---|---|---|---|
| Round disc, unidentified | col 0, row 0 | 1x1 | 37x37, 0.66c | #BAA167 ring, #624F28, #342A14, #311A08 | Gold beaded rim around a dark cross ornament. Flat coin seen face-on. No glow |
| Ring with blue stone | col 1, row 0 | 1x1 | 29x33, 0.5x0.6c | gem #94B8D5, band #857560 to #4E3A2C | Polished gold band, pale ice-blue stone on top. Looks different between frames (open hoop in 4, more droplet in 5), which may be compression or a moving reflection |
| Small amber gem (Jewel of Chaos, INFERRED, strong: 5.webp's hint requires a Jewel of Chaos, and 10.webp's valid 90 % recipe shows wings, axe, feather and this gem with no disc; combine row 4 is hidden) | 4: col 4 row 0, cols 0 and 1 row 1. 10: chaos col 6 row 1 | 1x1 | 13x25, 0.23x0.44c | #E3A644 core, #B37C3E, #805629, dark #372B1D edge | Vertical oval crystal, soft internal glow painted into the model. No external aura |
| Blue feather (Loch's Feather, INFERRED) | col 5, rows 0..1. Chaos col 7, rows 1..2 | 1x2 | 21x91, 0.38x1.62c | #95B9DE, #5077A0, #223C5A, near white rachis | Stripey blue-white, stiff, vertical. Colour constant in all frames |
| Excellent Larkan Axe +7 | cols 2..3, rows 0..2. Chaos cols 4..5, rows 0..2 | 2x3 | 80x138, 1.42x2.45c | Orange frame: #DA8939, #F1C385, shaft #573D29. Lilac frame: #B19FC9, #8A6A7A | Double-bladed axe, wooden shaft, gold central boss, pale blade rim. Time-varying tint, section 9 |
| Bone Armor +10, white armour | cols 0..1, rows 2..3 | 2x2 | 112x99, 2.0x1.76c | #FCFEFE, #F1F1F2, #999A9A | Nearly clipped to pure white with a few dark creases. Left shoulder spike overflows. Not 2x3 |
| Gold helm | cols 6..7, rows 0..1 (4 and 11 only; gone in 5) | 2x2 | 63x75, 1.1x1.3c | #FAB81A, #EF9F1C, #945A18, #FCCE3E | Saturated gold, nearly unlit, flat like an emissive material. Tall thin crest |
| Gold armour | cols 6..7, rows 2..3 | 2x2 | 82x98, 1.45x1.74c | as helm | Same material, with an orange-brown lower part. Colour stable across frames |
| Green pants | cols 4..5, rows 2..3 (4 and 11 only; gone in 5) | 2x2 | 51x85, 0.9x1.5c | #0E1A0C, #263622, #50634A, #9DAB8D, #D7DFC5 | Dark green, with cloudy pale patches and blown-out pale boots. Reads as chrome or reflective wash over dark cloth |
| Green and red bow | cols 6..7, rows 4..7 | 2x4 | 118x235, 2.1x4.17c | #4B4137, #7F7456, green #689253 with brighter core (region 415..440,860..900), rust red | Ornate limbs with metal studs, rust red tips, thin pale string, saturated green core glowing at the grip. Overflows bottom |
| Red spear | cols 0..1, rows 4..7 (5 and 6 only) | 2x4 | 78x148, 1.39x2.63c | #391310, #39201F, blade highlights muted pink | Dark crimson leaf blade, gold-red filigree, thin red shaft. Soft red aura and combination origin (INFERRED), section 10 |
| Wings of Elf | chaos cols 1..3, rows 0..1 | 3x2 | 137x63, 2.43x1.12c, 10.webp | #6C909F, #3E4F58, #252C32, bright cyan near the roots | Translucent butterfly wings with pale veins, additive look, brighter near the centre. Grid shows through |
| Cape | equipment wing slot | Slot 3x2 | 96x128, 1.7x2.3c, 5.webp | #0A233F navy, #736A5C and gold trim | Stiff blue cape with gold edges and small gold feet. Only worn item. Colour is the only one in the equipment area, so an equipped item stands out strongly against the grey ghost art |

Sizes seen: 1x1, 1x2, 2x2, 2x3, 2x4, 3x2. Brief assumptions corrected: armour is 2x2, not 2x3. Bow is 2x4 as guessed. Axe is 2x3 and wings 3x2 as guessed. Gold helm and armour are separate items, not one set piece.

Grey equipment ghost art (helm, armour, pants, gloves, boots, weapons, rings, pendant, wing silhouettes) belongs to the inventory panel analyst. It uses the same pre-rendered 3D look in monochrome.

### 8. Tier taxonomy

Evidence comes from only three names and icon-only items.

| Tier | Evidence | Name colour | Stat colours | Icon treatment | Cell highlight | dream extension |
|---|---|---|---|---|---|---|
| Normal | MEASURED name style only via wings. No plain +0 item tooltip seen | white #F9F8FA (wings); INFERRED same for normal gear | White | Lit model only | None tier-specific | `Common` |
| Wings | Wings of Elf | White | White, one group | Translucent, additive-looking | None | Item type, not a tier. Could use one dedicated category colour if wanted |
| High +N, Enhanced | Bone Armor +10 | Gold, peak #E2C17C, body #B79A5B | White base, blue extras | Whitened to near clip (4, 5, 11) | None | Threshold level tier: gold name at or above some +N |
| Excellent | Larkan Axe +7 | Green, peak #8CBFAB, edges #154E32; OBSERVED as a mint green, about #5FD6A0 once chroma loss is undone | Damage line blue | Slow orange, pink, lilac cycle, pale rims | None | `Rare`. Prefix `Excellent` in the name |
| Uncommon, dream only | No counterpart in the evidence | Rose #F08FB0, design choice. Blue would collide with `tip.text.bonus` | White base, blue extras | Base with light tint | None | `Uncommon` |
| Item with aura | Red spear, no tooltip seen | Unknown | Unknown | Pulsing red additive aura | None | Add-on layer for legendary, unique or high +N. See section 10 |
| Low to mid +N | **INFERRED gap** | MU: white, or light blue from about +7 for non-gold; gold from about +7 or higher | - | MU: stepped tints | - | Reserve steps |
| Ancient, set | **INFERRED gap** | MU: green-teal name with a set name prefix; a set list in the tooltip with yellow-green lines; item carries a blue-green shimmer | - | - | - | Pick a distinct colour from Excellent. The inventory panel has a `[Set option]` button (4, 5, 11), so sets exist on this server even though no set item is shown |
| Socket | **INFERRED gap** | Season 2 predates sockets; would be purple in later MU | - | - | - | Do not copy, design new |
| Jewels | Amber gem seen, no tooltip | INFERRED white name | - | No aura | - | Consumable class |
| Quest or event | Not seen | Unknown | - | - | - | Open |

Name composition, from evidence: `[Excellent ]<Base name>[ +N]`. The `+N` sits in the name in the same colour with a space before it. The word Excellent is only seen with green. Bone Armor +10 has no prefix.

Line additions by property, from evidence:
- **Skill** on item: one blue line at the top of the option block. `Falling Slash skill (Mana:9)`.
- **Luck**: always two blue lines, jewel success then critical rate. Present on armour and weapon.
- **Option**: one line, `Additional defense +8` on armour, `Additional Dmg +8` on weapon.
- **Excellent**: extra lines after the option, one per excellent bonus, same blue, no different colour. The blue block alone does not distinguish them.
- **Excellent also changes** the damage line to blue. Defense on +10 armour stays white, so level alone does not cause this. It may be excellent-only, INFERRED.
- Option blocks are absent if the item has none (wings).

Consider blue #A0B3D5 on the tooltip as one single "bonus" colour.

### 9. Why the same axe is orange, pink and lilac

MEASURED, dominant blade colours:

| Frame | Blade mid | Blade light | Shaft or dark |
|---|---|---|---|
| 4.webp, 180..250,540..610 | #DA8939 | #F1C385 | #573D29 |
| 10.webp, hovered, in chaos box | #CC8657 | #EBBD9B | #6B453A |
| 11.webp | #C68F79 | #E4C2BC | #684C43 |
| 5.webp | #8A6A7A | #B19FC9 | #574145 |

Controls in the same frames are stable, MEASURED:

- Feather #95B9DE / #95BBDF / #98BADD in 4, 5, 11.
- Gold armour hue stable, #FAB81A / #FACA34 / #F6BE2F, small variation only.

Conclusions:

- **Not global lighting, colour grading or video effect:** the feather barely moves between frames. MEASURED.
- **Not hover:** 4, 5 and 11 are unhovered, and all three colours occur. MEASURED.
- **Not location:** all three colours appear in the same inventory slot. MEASURED.
- **Not a different item:** silhouette and position match, and 4 and 11 show the same zen, 745 million (last three digits unreadable). OBSERVED.
- **Not two states:** the values form a continuum: orange, peach, pink, lilac. That suggests a smooth cycle. OBSERVED.
- **Probable cause, INFERRED:** the icon is the live 3D model, drawn with a reflection or colour effect driven by time. MU renders inventory items as 3D models at runtime, with chrome-style passes for higher +level and for Excellent items. The silhouette does not rotate; only surface colour changes. Pale rims turn lilac first while the shaft also shifts, so the effect affects the whole object, stronger on bright edges.
- **Period unknown:** a video may help. The spear's aura also differs between frames (section 10), which reinforces that icons are animated.
- The other four tinted-looking items (gold, white, pants, bow) were checked only for stability or by eye. Gold and feather are stable. Their white and cloudy washes look like static or slower chrome.

### 10. Red spear aura

- MEASURED aura colour on black cell: strong in 6.png, #652924 to #71342E (133..158,450..490; pixel 150,470 #682D29). Weak in 5.webp, #392526 to #372223 (585..610,830..860). Cell base is #232425, so in 6 it adds about +66,+5,+0 and in 5 about +22,+1,+1.
- The difference suggests pulsing, or two frames of a slow pulse, INFERRED. Spear is unhovered in both.
- Origin, INFERRED: from 4.webp to 5.webp the bag loses the gold helm, green pants and one amber gem, gains the spear, and Zen drops from 745 to 744 million, consistent with 1,000,000 (last three digits unreadable). 3.webp shows `You have successfully created the item`. So the spear is a combination output paid for by that Zen.
- Shape: soft blob around the blade head, about 1.5 x 1.5 c, fading to nothing, tinting grid lines too. Shaft has none. The icon stays sharp over it. Blend is additive, or a soft red sprite under the model, OBSERVED.
- It paints over cell background, within the footprint, and is not a rectangle cell highlight.
- No particles are visible in any still frame, OBSERVED. A shimmer may exist in motion.

## States and behaviour

| Trigger | Result | Confidence |
|---|---|---|
| Hover item | Navy fill across its footprint plus tooltip | MEASURED |
| Hover empty cell | No evidence | Open |
| Move away | Footprint returns to #232425 | MEASURED by comparison |
| Time | Axe colour cycle, spear aura pulse | INFERRED cause |
| Item in combination box | Same icon, same hover, same tooltip | OBSERVED |
| Equipped item | Icon drawn over ghost art, overflow allowed | OBSERVED |
| Drag or carry | No evidence. MU carries icon on the cursor | INFERRED |
| Cursor | Metal gauntlet cursor drawn above icons (OBSERVED) and above tooltips (no overlap seen) | INFERRED for tooltips |

## Layout table

| Element | Screenshot px at c=56.3 | In c |
|---|---|---|
| Cell pitch | 56.3 | 1 |
| Panel width | longest text + 16..21 | + 0.33 |
| Panel top pad, to cap top | 17..21 | 0.34 |
| Panel bottom pad | 24..28 | 0.45 |
| Line pitch | 25.3 | 0.45 |
| Name to first stat | 37 | 0.66 |
| Section gap, last stat to first band or band to option | 35..39 baseline to baseline | 0.66 |
| Cap height | 12..13 | 0.22 |
| Height with descender | 15..16 | 0.28 |
| Band height | 21..23 | 0.39 |
| Band gap | 2..4 | 0.05 |
| Band side inset | 2..4 left, 7..12 right | use 0.1 both |
| Overlap when above, two samples | 25..29 | 0.5 |
| Offset when below | 0 | 0 |
| Aura blob | ~85 | 1.5 |

## Colour tokens

| Token | Value | Use |
|---|---|---|
| `tip.bg` | #000000 at 0.80 | Tooltip fill |
| `tip.band` | #AD0D01 | Class band |
| `tip.text.base` | #FFFFFF, measured #FBFBFE | Base stats, wings name, band text |
| `tip.text.bonus` | #A0B3D5, measured #99B0D6..#A7B7D2 | Option, luck, skill, excellent, damage on excellent |
| `tip.text.gold` | #E2C17C | High +N name |
| `tip.text.green` | #5FD6A0, proposed. Measured peak #8CBFAB, body #4B987B | Excellent and `Rare` name |
| `tip.text.uncommon` | #F08FB0, design choice, not evidence. CIE76 distance 44 from bonus blue, 56 from gold, 89 from green | `Uncommon` name |
| `cell.tile` | #404143 | Raised empty stone |
| `cell.occupied` | #232425 | Unhovered footprint |
| `cell.occupied.line.v` | #444648 to #4F4F4F | Vertical lattice in footprint |
| `cell.occupied.line.h` | #303234 | Horizontal lattice in footprint |
| `cell.hover` | #1C2030 | Hover footprint |
| `cell.hover.line.v` | #323848 | Vertical lattice in hover |
| `cell.hover.line.h` | #262939 | Horizontal lattice in hover |
| `aura.red.peak` | #652924 over cell, or additive rgb(66,5,0) | Spear aura strong |
| `aura.red.low` | additive rgb(22,1,1) | Spear aura weak |
| `axe.cycle` | #DA8939 > #C68F79 > #8A6A7A, rim #F1C385 > #E4C2BC > #B19FC9 | Excellent cycle keys |
| `gold.material` | #FAB81A, #EF9F1C, #945A18 | Gold item |

## Typography

- **Font feel:** a small proportional humanist sans, like Tahoma or Arial rendered by GDI at small size, then upscaled. Regular weight. Not a chunky pixel font. Fully smooth edges here result from upscaling, so a native look would be crisp 1-bit or lightly antialiased. INFERRED.
- **Size:** drawn at screen px, not scaled with art. In the screenshots cap height is 12..13 px, 0.22 c, line pitch 25.3 px, average advance 7.6 to 8.8 px per character (MEASURED: 379 px for about 50 characters; 97 px for 11 characters), so tracking is wide.
- **Kerning:** irregular bitmap spacing, with tight runs and gaps (`Attack speed` nearly touches at `ck s`, `skill`, `Dmg`). Do not reproduce deliberately.
- **Outline and shadow:** none, on any line, OBSERVED. Text colour sits directly on dark. No weight change on names; only colour distinguishes the name.
- **Case:** Title Case for names and classes, sentence-like for options.
- **Unreliable letters from upscaling, do not treat as text:** `Magic` reads `Magio`. `Increases` reads `Inoreases`. `critical` reads `oritical`. `Wizardry` smears to `Wizardn/`. Digits `109` could be `100`; `416` could be `418`; `44`, `92` and `8` are less certain. `Mana:9` is probable.

## Recreation notes

### Draw procedurally
- Tooltip fill, class bands, and all three cell states are flat quads. Keep the cell states as a palette lookup.
- Text with one bitmap font and per-line colour. Layout model: list of `{text, colour, band?, gapBefore}`.
- Aura as a soft radial gradient mask, or a blurred silhouette, drawn additively.

### Icon pipeline recommendation

**Use a hybrid: offline bake, runtime effects.**

| Option | Benefit | Cost |
|---|---|---|
| Runtime render-to-texture of each model | Exact authentic behaviour, live chrome | Needs FBO code in the client. Dozens of icons per frame, or cache invalidation, in an inventory-heavy UI |
| Pure offline PNG | Cheap and simple | No live tier effects; per-tier variants multiply |
| **Bake offline, shader at runtime** | Cheap, animated, one asset per item | Needs bake tool and masks |

Dependencies, all rows in the gap table in [04-dream-gap-map.md section 8](04-dream-gap-map.md#8-gap-table):

- `FBO icon bake tool`: dream has no FBO code, so the bake tool below must add it.
- `Custom UI shader path`: `UserCallback` is skipped at `Render.ImGui.cs:295`, so the icon shader and aura pass cannot run through ImGui draw callbacks. Until this row lands, ship static baked PNGs as plain UI images with no tier effects.
- `Nearest-filter upload`: needed only for icons shipped as native-size pixel art. The 4x bakes below can use linear filtering.
- `Item icons`, `Rich tooltip layout`, `Rarity recolour`, `Item durability and requirements` and `Tool tiers / item level` cover the `VisualId` mapping, tooltip rows, colours and the data the tables below need.

Bake tool, a dev-only command that renders models through new FBO code, writes to an atlas per item:

1. **Camera:** orthographic, three-quarter yaw about -35 degrees, pitch about 25 degrees. Weapons rolled about 30 degrees to lie top left to bottom right. Fit the bounding box to about 75 % of the footprint. Allow up to 0.3 c overflow, so give the atlas tile a margin.
2. **Lighting:** a key directional from upper left and slightly front at 1.4 intensity, cool weak fill from lower right at 0.2, no ambient occlusion beyond a small amount, hard specular. Slight overexposure, then contrast curve, gets the punchy look.
3. **Output:** RGBA base at 4x native (`c` = 96 px minimum, sample down to UI scale). Plus a mask tile: R = metal or reflective, G = view-space normal x or a bent-reflection coordinate, B = edge-rim mask, A = emissive.
4. **No outline, no background.** Pre-multiplied alpha.

Icon shader, per item instance, uniforms `levelTint`, `levelBoost`, `chromeAmount`, `cycleSpeed`, `auraColour`:

```
base = sample(iconAtlas, uv)
m = sample(maskAtlas, uv)
// +level brightness and whitening
col = base.rgb * levelTint * (1 + levelBoost)
// moving reflection: ramp lookup scrolled over time
ramp = sample(gradientRamp, vec2(m.g * 0.5 + m.b * 0.5 + time * cycleSpeed, tierRow))
col = mix(col, col * ramp * 2, chromeAmount * m.r)
// rim and emissive
col += m.b * rimColour * chromeAmount
col += base.rgb * m.a * emissivePulse(time)
```

Aura pass: draw under the icon, additive, clipped to the footprint plus 0.2 c. Texture is a blurred silhouette of the alpha, or a radial blob. Pulse `0.33 + 0.67 * (0.5 + 0.5 * sin(2 pi t / P))`, with P of 2 to 3 seconds as a design choice.

Suggested presets for dream (design proposal, not evidence):

| dream state | Name | Icon |
|---|---|---|
| `ItemRarity.Common` (dream/Simulation/Inventory/ItemRarity.cs on branch I) | white | Base |
| `Uncommon` | `tip.text.uncommon` #F08FB0 | Base with light tint |
| `Rare` | `tip.text.green` #5FD6A0 | Cycle ramp, rims |
| Level below +3 | unchanged | none |
| +3..+6 | unchanged | Warm or cool `levelTint`, `levelBoost` 0.1..0.3 |
| +7..+9 | gold | `chromeAmount` 0.5 |
| +10 and up | gold | Whitened, `levelBoost` 0.8 plus optional aura |

Today's code has Uncommon green and Rare blue; this table replaces both for the reskin. The +level rows need item level data that dream does not have yet.

Avoid the bone armour extreme: at near white, the item's form is lost. Cap at about 80 % whitening.

Use only original models, palettes and names. Do not copy MU's model shapes, class names or item names into dream. Red bands and class lists translate into dream's own class or requirement rules.

## Open questions

1. Period and exact shape of the axe colour cycle. Needs video, and cause remains inferred.
2. Whether the spear aura pulses, or is a mid-transition of something else, and which tier gives it the aura.
3. Meaning of the red band: unmet class is inferred; the met-class look is unseen.
4. Colour of Excellent green is reconstructed; video chroma loss makes it uncertain.
5. Tiers of ring, gold helm and armour, pants and bow are unknown, as no tooltip is visible.
6. Why tooltip overlaps by 0.5 c when above and 0 when below. A cursor-based or item-centre-based anchor may be the real rule. Only two above samples and one below item exist.
7. Is a tooltip clamped at screen edges? Unseen.
8. Is the green tooltip for Ancient different from Excellent? Unseen.
9. Line 2 of Wings of Elf is illegible, covered by a notice.
10. Does hover also change the icon (rotation, brightening)? The ring's changing shape hints at motion, but nothing confirms it.
