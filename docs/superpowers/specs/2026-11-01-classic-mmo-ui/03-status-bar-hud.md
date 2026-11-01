# 03 — Status bar and HUD

## Summary

1. A bottom-anchored, mirror-symmetric (axis ≈ x 902 of 1803) black-iron bar, 133 px tall in image 2 (height `H`), 7.4% of frame width. All sizes below are also given in `H`. In the shared units (`00-README`), 1 u = 2.817 px of image 2, so `H` = 48 u and the bar spans u 433–480 of a 480 u tall frame.
2. Outside to in, on each side: a two-button stone block, a vertical crystal prism gauge between steel-and-gold caps, a large cut-gem resource orb gripped by a dragon talon. Black chrome dragon heads rise 1×`H` above the bar at both outer corners, snouts pointing inward.
3. Centre: chat cluster on top (log, input with whisper field), a long EXP trough with a digit in a cup, then Q/W/E slots, the selected-skill tile, and three square icon buttons.
4. Numbers are bold white/cream sans at 0.19`H`, sitting across the gem and gauge bottoms. There is no visible thick outline: they rely on a thin dark halo.
5. World text: centred gold notices on tight translucent bands that step upward over time and draw over all panels; blue log line at top left; thin framed bars under actors. Combat numbers are specified in `05-floating-damage-numbers.md`.

## Sources

Confidence tags: **MEASURED** (probe, coordinates cited), **OBSERVED** (seen by eye in a zoom), **INFERRED** (domain knowledge or guess).

| Image | Size | Used for |
|---|---|---|
| `reference/2.webp` | 1803×303 | Full bar (main source). All bar coordinates are in this image. |
| `reference/1.webp` | 489×632 | Actor bars. Crop of unknown scale, different from image 2. Combat text is covered by spec 05. |
| `reference/3.webp` | 1511×332 | Notices, blue log line, name plate. The inventory's cream rim is at x 1271–1272 in both image 2 (y 60, `#F8F0DA`) and image 3 (y 200, `#B9B2A2`), so images 2 and 3 share x origin and scale 1.00; image 3 is cropped at the right (MEASURED). |
| `reference/5.webp`, `7.png` | | Secondary: show the notices drawing on top of panels and tooltips, and more notice lines. |

Excluded as video overlay: white bold title "Arcana Mu Online Season2 - Chaos Weapon, Wings lvl1 and lvl2" in the top black band of image 3 (y 0–41, pure black, MEASURED).
Inventory panel content is another group's scope; here only its bottom button cluster is identified, so it is not confused with the bar.

Compression caveat: upscaled video. Lines 1–2 px wide, bright "ringing" along edges and mottling inside the prisms may be AI-upscale artefacts. Coordinates are ±2 px.

## Element-by-element spec

Coordinates are `x0-x1, y0-y1` in image 2 px.

### 1. Bar envelope

- Bar body `y 150–283`, so `H = 133`, spanning the full 1803 width (MEASURED: row population collapses to zero at y 284; gold rails at y≈153 and y≈280 on gauges). Ratio W/H = 13.6.
- Everything above y 150 is world, chat or inventory panel, except dragons and gem tops.
- Base material between elements: near-black iron `#0F100D` to `#050505`, with low-contrast relief; not a flat fill (OBSERVED). Dark charcoal slabs behind buttons `#262626`–`#383838`.
- **Black baseline strip**: `y 284–302`, 19 px = 0.14`H`, exactly `#000000` for all 1803 columns, with a hard edge (MEASURED: hline y 290, y 298 are a single run). At 2.817 px/u the bar already ends at u 480, the bottom of the frame, and a black band also exists at the top of image 3 with the video title, so this strip is **video letterbox, not art** (INFERRED, strong). Do not build it.
- **Top rail**: a thin ivory-gold line, y 152–154, `#C1B2A0`–`#EDECD6`, marks the bar's top edge between the big elements. Visible at x 466–523 and x 1280–1337 (MEASURED hline y 153, vline x 480, x 1330; the two segments mirror about 901.5). Outside those it is covered: the talons and gems sit in front of it, and it reappears as the gauge rails (x 180–273 and mirror). Brightness varies along its length, `#665D52` at x 505, so it is worn, not uniform.
- Symmetry: orange gauge centre 228 ↔ purple 1574; left buttons 14–170 ↔ right 1634–1790; red gem centre 345 ↔ blue 1455 (MEASURED). Axis about x 902. Only the numbers and the centre cluster break the mirror.

### 2. Dragon-head corner ornaments

- Left: `x 0–300, y ≈20–150` (MEASURED by silhouette tops: y 23 at x 0, 38 at x 90, 69 at x 150, 116 at x 270, 132 at x 300). So 300 px = 16.6% W = 2.26`H` long, about 130 px = 1.0`H` tall above the bar. Right is the mirror (`x 1503–1803`).
- Silhouette: a long low wedge, with the neck cut by the screen edge at far left, rising to a mass of swept-back horn spines at the outer side; the top contour falls almost linearly to a pointed snout that rests on the top rail just above the gem (OBSERVED).
- Features (OBSERVED):
  - 3–4 long sweeping horn/spine ridges from the brow backwards, each with a soft silver highlight.
  - Heavy brow ridge from about (60,60) to (195,75).
  - Amber slit eye at about (168,96), 25×8 px, `#BDA884` at that point (MEASURED), brightening to `#DCC49F` toward the snout.
  - Row of small bead-like scales along the snout top, x 190–250.
  - Lower jaw ridge with similar beads, y≈115–125.
  - A pale ivory tusk/nasal horn, about 45 px long, angled from (248,95) to (290,120), `#C8B890`.
- Material: glossy black lacquered metal or wet obsidian. Base `#0A0A0A`–`#1C1C1C`; highlights hard and elongated, running along the ridges, up to `#EEF1F0`–`#FEFEFE` (MEASURED: vline x 75 y 63–67 `#EEF1F0`; hline y 110 x 124–134 `#FEFEFE`). Light comes from above and slightly toward the viewer, with a chrome-style contrast: very little mid-grey.
- Z-order: drawn **in front of the inventory panel**; the right dragon's tusk overlaps the store button (OBSERVED, `x ≈1510–1545, y≈95–115`). The dragon's silhouette is alpha-cut against the world on the left.

### 3. Left block: two buttons

Both sit on a darker slab, `x 14–170`, with an inner face `x 23–165`.

- **Scroll button** `x 14–170, y 152–214` (156×62 = 1.17×0.47`H`). Face `y 159–203`, stone-grey mottled `#2D2D2D`–`#444442`.
  - Bevel (OBSERVED, MEASURED at vline x 100): bright top lip y 159–165 `#B7B2AF`, thin left lip `#8D8E89`, black drop area y 194–207 under the face. The face reads as a raised slab lit from top-left, sunk in a black recess.
  - Icon: closed horizontal scroll, `x 58–124, y 165–200`, parchment `#AE9371`, two brass-coloured vertical rollers with bright knobs `#F1E3BF` at each end. Parchment has a small illegible inscription. Purpose INFERRED: menu/commands window.
- **Coordinates plate** `x 14–170, y 218–281`. Same size but **inverted**: a sunken display whose face is a vertical gradient from `#000000` at the top (y 226) to `#282B2A` (y≈240–259), then a dull steel lower lip y≈266–272 peaking `#878883` and a left lip `#61615F` (MEASURED vline x 95).
  - Text: "187 104", two groups, map X and Y. `187` x 40–85, `104` x 108–152, y 236–259, height 23 px = 0.17`H`. Flat grey `#A9A9A9` (peak, MEASURED), no outline. Reads as an engraved or dim LCD. Same typeface as the bar numbers.

### 4. Orange vertical gauge

- Mapping: value **104003** sits at its foot, and the art gives no label. Keep this as a neutral `gauge.left`. The right one, with 16058, is `gauge.right`. MU's later clients use two such gauges for SD and AG, but SD may not exist on a Season 2 client, so **SD and AG are design labels for dream to assign, not a reading of these frames** (INFERRED).
- Fill rectangle: `x 205–248, y 164–~263` (MEASURED: orange pixels x 208–247, y 165–263; vline x 228 is orange y 165–247, then digits overlay). 43×99 px = 0.32×0.74`H`.
- Shape: a square crystal prism seen at a slight angle (3 faces), not a tube:
  - Left broad face x 205–231: orange `#F27B00`–`#FA8B08` with faint cloud mottling.
  - Bright edge stripe x 231–237: `#FFDC61` peak, `#FAC23C` around it, sharp-sided (MEASURED: hline y 200 x 230–237).
  - Right narrow face x 238–247: `#E18008`, `#E56E00`.
  - Top end y 163–165 darkened to `#924C0F`, like a thin dark rim where the prism enters the cap. Outer edges fall off through `#A84D0C` to near black within 3–4 px: slightly rounded sides.
- Palette: `#F37C07`, `#FB890A`, `#C65F0B`, `#FBCC4D`, `#F89B1A` (MEASURED, pal 205,165–248,245).
- Frame (OBSERVED, crop `gaugeO`):
  - Top: thin gold rail `x 180–273, y≈152–154`; below it, a central silver half-dome `x 216–241, y 153–162` (`#ADAEAB` peak) sitting on the prism, and two gold collars `x 187–206` and `x 249–268`, each ending in a downward steel spike reaching y≈186. The prism looks held between them, like a capsule in a brass-and-steel clamp.
  - Bottom: a smaller silver dome and matching gold rail `x 180–272, y≈279–281`, mostly hidden by the digits.
  - Sides: black, no frame; the prism floats on black.
- Fill behaviour not visible: both gauges appear full. INFERRED: fill clips from the top down with the bottom anchored, showing black behind. Height = value/max × 99 px; a hard horizontal cut, no meniscus.

### 5. HP red gem

- Bounds: `x 276–414, y 146–281` (MEASURED red-pixel extents and rows), about 139×136 = 1.05×1.02`H`. **Overshoots the bar's top edge by about 4 px**. Not a sphere; an irregular faceted brilliant-cut stone (OBSERVED): flat planes with hard edges, no smooth radial gradient.
- Outline, approx clockwise polygon: (302,151) (372,149) (410,194) (417,234) (392,278) (286,278) (275,200) (277,186). Row spans (MEASURED): y 150 → 308–374, y 170 → 287–393, y 190 → 278–413, y 200 → 276–408, y 240 → 281–408. The right edge is partly occluded by the talons; the indent at x≈400–414 is the talon, not the gem.
- Facet zones (OBSERVED, crop `gemR`):
  - **Table (top face):** large rounded area, x ≈310–400, y ≈150–232, saturated `#EC260C`–`#CD1401`.
  - **Dark core:** soft blot at about (337,195), `#A61302`–`#7C1001`. The only soft shape, like depth inside the stone.
  - **Secondary glint:** soft pink-red patch at about (362,188), lighter.
  - **Specular rim:** the signature feature. A thin, near-white arc `#EEC6AD`, 3–5 px wide, follows the table edge: from the top (321,150), down the left (309,180), around the bottom (329,224), to (383,234), ending at (397,219). Top edge has a flat white segment x 321–370, y 148–153, where the gem meets the rail. It looks like a ring of light along the crown, not a window glare. Open toward top-right.
  - **Left pavilion facet:** polygon (277,190)–(310,185)–(325,225)–(306,250)–(287,278), mid red `#CD1401`–`#98210B`, gradient darker to the bottom.
  - **Upper-left crown facet:** (302,151)–(320,152)–(310,180)–(278,187), bright `#EC260C`, with a small pale glint on its lower edge at about (290,185).
  - **Bottom facets:** a dark triangular plane under the table, about (325,232)–(384,240)–(345,275)–(297,275), `#7C1001`–`#420D03`; right lower facet (384,240)–(417,234)–(390,262), bright `#D45E45`-to-`#CD1401`.
- Palette: `#CD1401`, `#EC260C`, `#7C1001`, `#420D03`, `#EEC6AD`, `#A61302`, `#D45E45` (MEASURED).
- Light: from upper left in the facet values; the specular rim is a fake crown highlight.
- No frame: the gem sits directly on black, gripped by the talon. The number overlays it.
- **Fill level not visible** (in every frame available the gem is full, or the current/max is unknown). INFERRED as in MU: a dark, desaturated "empty" gem or black is shown, and the bright gem is clipped from the top so that the level drops. A horizontal straight cut through facets fits the look and reads well.

### 6. Right mirror side

- **Blue gem** `x 1385–1525, y 148–281`, same polygon mirrored (specular ring opens top-left; dark core at about (1475,195), MEASURED/OBSERVED). Row spans: y 190 → 1386–1524, y 200 → 1392–1525. Colours `#0051ED`, `#1385E7`, `#0222DF`, `#0109A5`, `#05063D`, `#0F1771`, `#6FC2E3` (rim). The rim is cyan rather than white, and the top flat is cyan `#40C8F0` (OBSERVED). Upper-left facets are brighter cyan-azure than the red gem's counterparts, so the blue gem looks a bit more luminous. Value 22918 = mana (INFERRED).
- **Purple gauge** `x 1553–1594, y 164–~255` (MEASURED: 1554–1594). Same 3-face prism **not mirrored**: stripe x 1578–1583 `#A677A7`–`#B183B0`, on the right of centre like the orange one. Broad left face `#5D2E69`, `#552464`; right narrow face `#572461`. Mottled. Much duller than orange: low saturation, low brightness. Caps as orange, mirrored; right side gets a faint blue-grey sliver x 1593–1595 `#535765`. Value 16058 (`gauge.right`).
- **Party button** `x 1634–1790, y 152–214`, same slab as scroll button. Icon: two bare-chested tan figures, side by side, the left with an arm on the right's shoulder, `x 1654–1722, y 163–204`, flesh `#C8A878`, black halo; faces blurred to a pale blob. Right side has an inset mini-slot `x 1731–1776, y 168–199`, black with a thin white lip and a dim grey bar: probably a party/toggle status light, off (INFERRED).
- **PvP button** `x 1634–1790, y 218–281`. Crossed swords, cream-gold `#D8C490`, hilts at the bottom, `x 1688–1742, y 232–270`. Purpose INFERRED: PK/attack-mode toggle.
- Note: this button is a raised face, unlike the left coordinates plate.

### 7. Dragon talons

Each side, a scaled black forearm comes out of the centre cluster and grips the gem (OBSERVED, crop `clawL`).

- Left: forearm mass `x 400–560, y 150–280`, a twist of segmented cords, ropelike, with bright hard highlights on each ridge.
- Three talons:
  - top, thin spike along the rail, x≈395–430, y≈150, gold ring at (436,156);
  - middle, largest, tip at about (397,207) **overlapping the gem**, gold ring at (462,189);
  - lower, smaller, x 410–465, y≈242, gold ring (471,241).
- Each talon is a black cone with one elongated silver highlight along its upper side. Rings: dull gold `#C8A860`.
- Right: mirror; middle tip at about (1405,207).

### 8. Chat cluster (centre top)

Outer posts at x 524–526 (MEASURED hline y 80) and x 1277–1279 (MEASURED hlines y 110–140; mirror of the left about 901.5): 755 px = 41.9% W = 5.68`H`. Bottom edge meets the EXP frame at y≈164. Top is cropped by the image (log area extends above y 0). Above y≈100 the inventory rim (x 1270–1273) covers the right side, so the log row is visible only to x≈1268; the right outer post is visible only at input height, y 100–165, where the centre frame draws over the panel edge, so x 1277–1279 is measured there rather than only predicted by mirroring (OBSERVED, seam crop).

- **Log pane** `x ≈538–1222, y <0–100`. Background: dark mottled boulder-like stone, `#1C1810`, `#15120B`, `#0D0C07`, with `#493C1D` warm highlights (MEASURED). It is **not** a plain translucent rectangle: it reads as opaque dark rock. Translucency unverifiable.
- **Side posts**: left gold-and-iron column x 524–538; on the right the inner post is x 1265–1267 `#DCC9A6`, and the strip x 1271–1273 beside it is the inventory rim, not chat. Narrow strips of pale gold `#948468` beside black ropework.
- **Chat lines**, pitch 37 px = 0.28`H` (first at y≈7, second ≈43, third ≈80; MEASURED). Left at x≈552. Three visible:
  - `JUIZ: [POST] adm on??` — same gold bar, `x 552–748, y 0–17`, top cropped by the image (MEASURED: bar ends x 748, y 17).
  - `BayE: [POST] selam kardesúm bk kanatmú var sende` — dark brown text `#3C2303` on a gold bar `#9D7A2B`/`#A28134`, bar `x 552–988, y 34–54`, 20 px = 0.15`H` tall, **flush tight to the text width**, not full width.
  - `Admin: Arcana Mu Online guides for` — off-white `#F8F4EB`, no bar, thin dark shadow.
  - INFERRED rule: `[POST]` is a server-wide broadcast channel, highlighted; default chat is white; the highlight bar is per-line background sized to the text.
- **Scrollbar column** `x 1222–1262, y <0–100`. Stone `#3A3430`–`#4F493B`, divided into three cells: up-arrow (y≈2–28), thumb/track (y≈32–60 with bright bar at y≈62–66), down-arrow (y≈75–98). Arrows are hollow outlined block arrows in tan `#897B60`, chiselled, 28 px wide.
- **Chain rail** under the log: white line y 100–103 `#CDCDC4`, twisted rope/chain y 106–112 (`#545454`), repeated dash pattern, spanning x≈540–1270.
- **Input row** `x 549–1262, y 114–161`, 47 px = 0.35`H`, with tiny L-shaped white corner brackets at x≈549, x≈1262:
  - Main field `x 552–1057`, pure black `#030303`. Typed text "Chaos" x 549–600, y 125–139, `#FDFDFE`, left-aligned, vertically high.
  - Second field `x 1058–1216`, flat dark grey `#1A1A18`. INFERRED: whisper-target name field.
  - **Golden button** `x 1222–1262, y 120–158`, 40×38: tan stone tile `#937953`, `#AA9068` with bright top-left lip `#F3E8C7`, carrying a gold curled glyph, like an ear or a scroll hook, `#D69836`, `#F9EAAE`. Purpose INFERRED: chat mode / whisper lock.
- Bottom line: white rail y 163–164 `#ECE8E5`, top of the EXP frame.

### 9. EXP bar and counter

- Trough `x 622–1181, y 165–181`: 560×17 px = 31% W = 4.2×0.13`H`. Pure black `#070300`, **empty in this frame** (MEASURED hline y 172).
- Frame: white-cream top rail y 163–164, gold-cream lower rail y 182–184 `#D0BEA0`, broken into bright dashes (irregular wear). End posts x 618–621, x 1182–1185 `#C0B9A7`.
- Fill never seen. INFERRED: horizontal, left to right, flat bright colour (MU uses a pale yellow-orange), with 10 sections.
- **Counter "0"**: glyph `x 1191–1207, y 158–183`, 17×26, cream `#F6F0D9`, sitting in a gold U-shaped cup that curls from the bottom of the right post. The brief calls this the level counter. Given a character with 100k stats, a level of 0 is implausible, so it is **more likely the count of filled EXP sections, 0–9, or a master-EXP digit** (INFERRED). Treat it as "EXP section counter".
- Immediately right: small steel bead at x≈1215–1225 (end of forearm), left mirror x≈600–607.

### 10. Q/W/E slots

Sitting on a chain band `y 186–190`, spanning x≈600–1220 (OBSERVED).

| Slot | Outer x | Outer y | Arch notch x | Letter |
|---|---|---|---|---|
| Q | 561–654 | 199–279 | 609–659 | x 564–584, y 254–280 |
| W | 661–742 | 199–279 | 697–746 | x 656–681, y 257–281 |
| E | 750–829 | 199–279 | 785–834 | x 744–759, y 258–282 |

- Slot about 80×80 (0.6×0.6`H`), Q's extends left by a stone cheek x 561–577. Frame is a white-cream polished line, 3–4 px wide (`#E3DDD8`, `#FFFFFC` with gold flecks), top y 199–204, bottom y 272–278, drawn from the slot's lower-left to top with its right side interrupted.
- Interior: recessed, black-grey; `#1B1B19` fading to `#222220` at centre and `#4C4C4A` near the bottom (MEASURED vline x 590). A shallow floor gradient: lighter along the bottom, as if the light falls into the recess.
- **Arch notches**: rounded cartouche (superellipse), 50×39 px (0.38×0.29`H`), pure black `#030000`–`#040000`, with a 2–3 px gold rim `#766D4E`, `#8D7B5A`. It overlaps the top-right corner of each slot, biting into it, and rises above the top by about 10 px, up to the chain. All empty. INFERRED: holds quantity/consumable count, or is a socket design. Not the same as the slot's own content.
- **Letters**: hollow-looking, thin gold-beige outline capitals, `#D6C69C`, 25 px tall = 0.19`H`, stroke 2–3 px, sitting at the slot's lower-left corner, overlapping the frame. Q has a small tail. Slightly serif/engraved feel.

### 11. Selected-skill tile

- Ornamental bracket `x 856–947, y 182–283`: 91×101 = 0.68×0.76`H`. Top and bottom gold bars (dashed highlights) and left and right vertical gold columns, x 856–864 and x 938–947, that bulge inward-outward at mid height (y≈215–250) in a beaded, wavy waist. `#5D4831`–`#786149`, bright `#D0BEA0`.
- Black recess around the tile, `#060100`.
- Tile `x 875–929, y 191–270`: 54×79, ratio ≈ 2:3. Matte grey-tan stone `#4B453C`–`#5A534A`, light top lip y 191–193 `#A59D89`, thin right lip `#7D776E`, no sheen.
- Icon (engraved dark-on-grey, OBSERVED): an arrow or spear with tip pointing right, a wide barbed head with fletching at the left, shaft at y≈228. Around it is a vertical oval "wheel" formed by two arcs, top arc at y≈200–215 and bottom y≈247–262. Reads as an arrow through a ring. Monochrome: looks like the default "normal attack" placeholder (INFERRED), which would be replaced by a skill's coloured icon. If the skill wheel/cycle is what the ring represents, unknown.
- INFERRED MU behaviour: selected skill; hover opens the skill list above it; mouse wheel cycles.

### 12. Three icon buttons

Sitting on a stone plinth `x 960–1245`, cheeks visible at x 960–975 and x 1229–1245 (`#808080`–`#3C3C3C` gradients).

| Button | Outer x | y | Icon |
|---|---|---|---|
| 1 | 975–1054 | 199–278 | Cream heraldic banner/tabard with notched bottom, on a little crossbar with a ring on top, x≈990–1040. |
| 2 | 1062–1141 | 199–278 | Magnifying glass, gold ring, cream handle pointing lower right, on a dark vignette. |
| 3 | 1150–1228 | 199–278 | Cream-to-gold folded parchment/bag shape. |

- Frame: same 3–4 px white-cream line as Q/W/E, then black 5–6 px, then an inner grey stone face with an inner vignette, `#646155`→`#0D0500`.
- **Button 3 appears pressed**: inner face shifted down-right with a black L along top and left (MEASURED hline y 215: black x 1156–1162 on the left, vs face directly after the frame on buttons 1–2; OBSERVED). The inventory panel is open, so this is likely the Inventory toggle in active state (INFERRED). Button meanings otherwise unknown: INFERRED banner = character or guild, magnifier = info/search.
- Icon colour: `#CFB68F`, `#95846F`.

### 13. NOT part of the bar: inventory bottom cluster

`X` / repair / store buttons at `x 1325–1391`, `1405–1472`, `1484–1550`, `y 44–110`: 66×66 px (0.5`H`), belong to the **inventory panel**, which spans `x 1270–1803` = 533 px = 189 u, 29.6% W (MEASURED; x 1280–1281 is its chain column). They are grey `#464444` riveted plates with 4 corner rivets, orange X `#F7AC08`, gold hammer-and-hand and chest icons. They appear and disappear with the panel, and repair and store also vanish while the Combine panel is open, leaving only `X` (OBSERVED, image 5). Do not build them in the HUD. They are listed here because they sit visually above the right gem.

### 14. Bar numbers

| Value | x | y | Colour (peak) | Anchor |
|---|---|---|---|---|
| 104003 | 178–283 | 251–276 | `#FFFFFF` | Gauge foot, right edge = gauge centre + 55 |
| 40650 | 322–410 | 251–276 | `#FFF8DB` | Gem low, right edge = gem centre + 65 |
| 22918 | 1436–1522 | 251–276 | `#F3EFF9` | Gem low, right edge = gem centre + 67 |
| 16058 | 1546–1630 | 251–276 | `#FCF0D3` | Gauge foot, right edge = gauge centre + 56 |

- Coordinates MEASURED from zoom crops ±2 px.
- The right-edge offsets match on both sides, for 5 and 6 digit numbers, which suggests **right-aligned numbers** (INFERRED), so the numbers do not mirror: they all shift rightward of their element. Centre alignment cannot be excluded for the gems (only 5-digit samples there).
- Note: 104003 and 16058 are **not** the same as the current/max readouts of MU's original UI. Each element shows a single number here; maximum is not displayed.
- Sharpening ringing made digits look metallic on 104003; differences between white and cream are probably noise. Use one token.

### 15. Floating combat text

`05-floating-damage-numbers.md` owns this: colours, sizes, glyphs, stacking, motion, meaning. The shared facts are: four colour families, blue `#0A9AFE`, green `#15E8B5`, gold `#FDB442` and one cyan row `#1FD3EE` (`x 266–330, y 214–226` in image 1, hue 186 against 205–208 for blue, so `dmg.ignore`); two columns at x centres 203 and 303; `Miss` pitch 21–22 px.

### 16. Actor bars and name plates

Evidence is thin: both monster bars are half hidden by the numbers. No name text is legible on either.

- **Right actor bar** (image 1, MEASURED): frame `x 264–364, y 190–201`, 100×11 px, ratio ≈ 9:1. Dark outline, brown-black `#581D00` along the top (y 190–191), olive-brown along the bottom (y 198–200) and sides, 2–3 px. Thin, plain, no ornament.
- Its interior has a **hard vertical step at x≈323–327**: left of it bright red-orange `#EE8240`–`#D9642C`, right of it dark `#672404`–`#723410` (hline y 194). INFERRED: an HP fill left to right at about 60% of the 96 px interior. Caution: the backdrop here is fire, so the true fill colour is unknown; the interior may be translucent, and the lower interior rows y 197–199 are duller, `#A16F27`–`#AC4B28`.
- **Left actor bar** (image 1): strip around `x 166–241`, covered above by the `73148` digits, dark `#261202`–`#3F181A` at y 243–250, grey `#56402C` at y 251–253 (MEASURED vline x 200). No bright interior shows, so it is either empty or hidden; its width and height cannot be read.
- Position: directly under the number stack; each bar sits over its target, with numbers rising from just above it (OBSERVED). Whether the name text normally sits in the same plate is unknown, the digits cover that area.
- **Player name plate** (image 3): see section 17. No HP bar is visible in that crop, which ends at y 331.
- Build: world-anchored billboard at constant screen size. Image 1's scale is unknown, so treat the size as a design choice and keep the 9:1 ratio.

### 17. System messages (image 3 coordinates)

- **Centred gold notices** (image 3):
  - `You can press /attack 1 1 7 for afk mode`: text x 735–1075, y 213–226.
  - `SM/MG Evil Spirit /attack 1 1 7 9`: x 765–1039, y 250–267.
  - `BK/MG Twisting Slash /attack 1 1 7 41`: x 740–1063, y 286–300.
  - Pitch 37 px = 0.28`H`, same as chat. Cap height ≈13 px = 0.10`H`.
  - Colour: pale gold, peak `#F5DDA5` (MEASURED). Typically reads `#D9C08A`; orange tint at edges.
  - Band: per line, tight, about 5 px horizontal and 5 px vertical padding, 20 px tall. Black at about 50% (45–52%): transmission 0.48 in image 3 at x 1000 (`#554618` to `#29200C`) and 0.55 in image 5 at x 200 (`#45474D` to `#26262A`) (MEASURED). No border. Sharp rectangle. See-through: a bright world sprite shows through (OBSERVED).
  - Centring: line centres x 905, 902, 901 against a screen centre of 901.5, so centred on **the whole screen**, not the visible play area (MEASURED). Images 5 and 7 agree: notices run over the inventory or combination panels and tooltips.
  - Z-order: on top of everything, including tooltips; in image 7 it collides with tooltip text. Do not copy the collision: draw notices below tooltips.
  - Further lines seen in images 5 and 7: `You can press /offattack for offline afk mode`, `For free 12 Hours and 24 for VIPs`, same gold. Up to 5 gold lines at once.
  - **Flow** (MEASURED across frames, converted to image 5 y): lines sit on a 37 px lattice, y 64, 101, 138, 175, 212, 249, with blank rows possible (175 is empty in image 5). `BK/MG` is at 251, then 138, then 64, then gone; `/offattack` 212, 137, 100; `For free` 249, 173, 137. Lines enter near y 250 in image 5 px (about 89 u from the top), step up one pitch at a time, and leave at the top. The list moves with no new line pushing it, so it is time-stepped. Frame order is assumed; the step rate is unknown, so make it tunable.
- **Top-left blue line**: `You have successfully created the item`, x 1–320, y 82–97. Blue-grey-lilac, peak `#9BB6DD`, with typical `#6C8BAD`; dark navy-black band `#0C111D`–`#161F29`, x 0–322, y≈78–100. Left-flush to screen. It is a result log; INFERRED it stacks downward with fade.
- **[Admin] name plate**: `[Admin] Master`, x≈552–720, y 318–332+ (cropped at the image's bottom). Text lime-yellow `#E1F170` peak, on an olive-green translucent band `#3C4D04`/`#344502`, x≈548–722. Centred above a character head. INFERRED: green marks GM; ordinary players likely use a different tint. World-anchored billboard, constant screen size.

## States and behaviour

All INFERRED unless stated, since only stills exist.

| Element | States |
|---|---|
| Gem | Fill clips with value; maybe a low-HP flash. |
| Gauges | Fill clips with value, bottom-anchored. |
| Buttons | Normal; hover (lighter face); pressed (face shifts down-right, black L on top and left: OBSERVED on icon button 3). |
| Q/W/E | Empty as in reference; item icon; digit count in notch; key press flash. |
| Skill tile | Default placeholder; skill icon; greyed if not enough resources. |
| Chat | Input focus; `[POST]` line highlight; scrollbar. |
| EXP | Fill; counter increments per section. |
| Notices | Enter at the lower row, step up one 37 px row per tick, leave at the top row. |
| Actor bars | Fill follows target HP. |

## Layout table

`H`=133. Ratios from image 2; `%W` based on 1803.

| Element | x | y | w×h px | ×`H` | %W left |
|---|---|---|---|---|---|
| Bar body | 0–1803 | 150–283 | 1803×133 | 13.6×1 | 0 |
| Dragon L | 0–300 | 20–150 | 300×130 | 2.26×0.98 | 0 |
| Scroll button | 14–170 | 152–214 | 156×62 | 1.17×0.47 | 0.8 |
| Coords plate | 14–170 | 218–281 | 156×63 | 1.17×0.47 | 0.8 |
| Orange gauge fill | 205–248 | 164–263 | 43×99 | 0.32×0.74 | 11.4 |
| Gauge cap assembly | 180–273 | 152–281 | 93×129 | 0.70×0.97 | 10.0 |
| HP gem | 276–414 | 146–281 | 139×136 | 1.05×1.02 | 15.3 |
| Talon L | 395–560 | 150–280 | 165×130 | 1.24×0.98 | 21.9 |
| Chat cluster | 524–1279 | ..–164 | 755×.. | 5.68×.. | 29.1 |
| Inventory (panel, not bar) | 1270–1803 | | 533×.. | 4.01×.. | 70.4 |
| Input row | 549–1262 | 114–161 | 713×47 | 5.36×0.35 | 30.4 |
| EXP trough | 622–1181 | 165–181 | 560×17 | 4.21×0.13 | 34.5 |
| EXP counter | 1191–1207 | 158–183 | 17×26 | 0.13×0.19 | 66.1 |
| Q | 561–654 | 199–279 | 93×80 | 0.70×0.60 | 31.1 |
| W | 661–742 | 199–279 | 81×80 | 0.61×0.60 | 36.7 |
| E | 750–829 | 199–279 | 79×80 | 0.59×0.60 | 41.6 |
| Skill bracket | 856–947 | 182–283 | 91×101 | 0.68×0.76 | 47.5 |
| Skill tile | 875–929 | 191–270 | 54×79 | 0.41×0.59 | 48.5 |
| Icon btn 1–3 | 975–1228 | 199–278 | 79×79 each, gap 8 | 0.59 each | 54.1 |
| MP gem | 1385–1525 | 148–281 | 140×133 | 1.05×1.00 | 76.8 |
| Purple gauge | 1553–1594 | 164–255 | 41×91+ | 0.31×0.74 | 86.1 |
| Party/PvP | 1634–1790 | 152–281 | 156×62 each | 1.17×0.47 | 90.6 |
| Dragon R | 1503–1803 | 20–150 | 300×130 | 2.26×0.98 | 83.4 |

Native-grid hint: 1803/640 = 2.817 px/u. `H` = 47.2, nominal 48 u; gem ≈ 49 u; icon buttons ≈ 28 u; digits ≈ 9 u. Gems are nearly square, so there is no sign of anisotropic stretch here despite the brief's "stretched" label (MEASURED). The bar spanning u 433–480 corroborates the 432 u play height and the letterbox reading. Units are defined in `00-README`.

### Responsive rule for 16:9

- Scale: `s = screenHeight / 480`, snapped to multiples of 0.25 (project rule); bar height `H = 48·s`. 720p: s=1.5, `H`=72. 1080p: s=2.25, `H`=108. 1440p: s=3, `H`=144. 2160p: s=4.5, `H`=216. Author art at 4× native (2560×192) to avoid mush; filter linearly. Text is drawn at screen pixels and does not scale with the art: the `H` ratios in Typography give the reference look, and the font rule lives in `00-README`.
- The native bar is 640·s wide: 1440 at 1080p, leaving 480 extra. Split the bar in anchored groups; do not stretch art:
  - **Left group** anchored bottom-left: dragon L, buttons, orange gauge, HP gem, talon.
  - **Right group** anchored bottom-right: mirror.
  - **Centre group** anchored bottom-centre: chat cluster, EXP, Q/W/E, skill, buttons.
  - **Gap filler** between the groups: horizontally tiled forearm/rope iron strip with chain band. Each gap ≈240 px at 1080p. Above gap: world.
  - Option: use the extra width for the chat: main input and EXP trough are 3-slice stretchable up to 1.25×. Keep Q/W/E and icon buttons centred.
  - Ultrawide: cap centre at 1.4×; let the gap filler absorb the rest. Above 21:9, consider moving groups inward so eyes need not travel; cap total bar at 2560·(s/3).
- Open panels anchor top-right and sit under the dragon: keep bar Z above panels, and clip nothing.
- Notices centre on the full screen, entry row about 89 u from the top; the blue log anchors left at 0–8·s from the edge. No space is reserved for the black strip.

## Colour tokens

| Token | Value | Use |
|---|---|---|
| `hud.iron.base` | `#0F100D` | Bar background |
| `hud.iron.black` | `#050505` | Recess |
| `hud.rail` | `#C1B2A0` | Top rail |
| `hud.slab` | `#262626` | Button surround |
| `hud.stone.face` | `#383838` | Button face |
| `hud.stone.tile` | `#4B453C` | Skill tile |
| `hud.steel.lip` | `#8D8E89` | Left/low bevel |
| `hud.steel.high` | `#B7B2AF` | Top bevel |
| `hud.ivory.line` | `#E3DDD8` | Slot frame, EXP rail |
| `hud.gold.dim` | `#766D4E` | Notch rim |
| `hud.gold.mid` | `#C8A860` | Rings, collars |
| `hud.gold.bright` | `#F1E3BF` | Knobs, highlights |
| `hud.parchment` | `#AE9371` | Icon fill |
| `hud.dragon.black` | `#0A0A0A` | Dragon base |
| `hud.dragon.spec` | `#EEF1F0` | Dragon highlight |
| `hud.dragon.eye` | `#BDA884` | Dragon eye |
| `hud.hp.bright` | `#EC260C` | Table |
| `hud.hp.mid` | `#CD1401` | Facet |
| `hud.hp.deep` | `#7C1001` | Facet |
| `hud.hp.void` | `#420D03` | Lowest facet |
| `hud.hp.rim` | `#EEC6AD` | Specular |
| `hud.mp.bright` | `#1385E7` | Table |
| `hud.mp.mid` | `#0051ED` | Facet |
| `hud.mp.deep` | `#0109A5` | Facet |
| `hud.mp.void` | `#05063D` | Lowest facet |
| `hud.mp.rim` | `#6FC2E3` | Specular |
| `gauge.left.face` | `#FA8B08` | Prism |
| `gauge.left.side` | `#E18008` | Prism |
| `gauge.left.edge` | `#FFDC61` | Stripe |
| `gauge.right.face` | `#5D2E69` | Prism |
| `gauge.right.side` | `#572461` | Prism |
| `gauge.right.edge` | `#B183B0` | Stripe |
| `hud.number` | `#F8F4E8` | Bar numbers |
| `hud.coords` | `#A9A9A9` | Coordinates |
| `chat.bg` | `#15120B` | Log |
| `chat.bg.high` | `#493C1D` | Log rock light |
| `chat.input` | `#030303` | Main field |
| `chat.whisper` | `#1A1A18` | Second field |
| `chat.default` | `#F8F4EB` | Text |
| `chat.post.bg` | `#9D7A2B` | Broadcast bar |
| `chat.post.text` | `#3C2303` | Broadcast text |
| `dmg.*` | see spec 05 | Includes cyan `dmg.ignore` `#1FD3EE` |
| `notice.gold` | `#F5DDA5` | Band black 50% |
| `log.blue` | `#9BB6DD` | Band `#0C111D` 70% |
| `name.gm` | `#E1F170` | Band `#3C4D04` 60% |
| `actor.bar.rim` | `#581D00` | Actor bar outline |
| `actor.bar.empty` | `#672404` | Over fire, low confidence |

## Typography

| Use | Feel | Size | Weight | Outline/shadow | Colour |
|---|---|---|---|---|---|
| Bar numbers | Bold humanist sans, Tahoma/Verdana Bold feel, tabular | 0.19`H`, digit adv 0.13`H` | Bold | 1 px dark halo, `#0A0101`-like, soft | `hud.number` |
| Coordinates | Same, digits with a wide gap | 0.17`H` | Bold | None | `hud.coords` |
| Q/W/E | Thin gold capitals, engraved | 0.19`H` | Light, outlined | Black fill inside | `#D6C69C` |
| Chat | Small Tahoma-like, hinted, wide tracking | x-height 0.07`H`, line pitch 0.28`H` | Regular | 1 px shadow | per channel |
| Input | Same as chat | | | None | White |
| Notices | Same, slightly larger | cap 0.10`H` | Regular | None, band | `notice.gold` |
| Damage, Miss | See spec 05 | | | | |

The very wide tracking of chat and notices reads as a Windows bitmap font (GDI Tahoma 11/12) scaled up (INFERRED). For original art use an open bold sans such as a hinted Noto Sans/Open Sans with +5% tracking at native 2× pixel-like size.

## Recreation notes

Use original art. MU's look is reproducible by the *recipe*, without copying any of its textures.

**Procedural or vector:**
- Button slabs, bevel lips, slot frames, EXP frame, chat bars: nine-slice with rules from this spec.
- Prism gauges: three vertical quads with a gradient; add noise overlay; clip with scissor. Stripe is a 14% wide, right-of-centre band.
- Gems: authored low-poly mesh. Build a 2D polygon fan with 9–10 flat-shaded facets in the listed values. Add a bent polyline stroke for the rim, 3% of gem size, with a gaussian falloff, plus a soft dark core and a soft glint. Render it once into a texture, mirror for blue with hue shift, then scissor for fill. Add dimmed version for the empty part.
- Chain and rope bands: a 16-px tile.
- Text bands: rect sized to text bounds + 5·(s/2.8).
- Combat digits: see spec 05.
- Actor bars: outlined rect with a clipped fill.

**Textures, must be hand-made or sculpted:**
- Dragons, forearms and talons. Recommended: sculpt or kitbash an original creature, render with a black, high-gloss material and a single top light in Blender, bake to a sprite. Change the anatomy so that it is not a trace: for example different horn layout, spine rhythm. Keep the composition: corner-rooted, inward-facing, long low wedge.
- Rock background for chat: dark noise-based tile.
- Icons: draw in cream/gold over grey with black halo; the halo gives the 2000s look.

**Avoid:** gradients on gems that look like smooth spheres; rounded modern shadows; colour glows.

**Draw order:** world → name plates → damage numbers → panels → bar (with dragons) → notices → tooltips → cursor.

## Open questions

1. What dream assigns to `gauge.left` and `gauge.right`, and whether both should exist at all.
2. Fill mechanism for gems/gauges: all samples are full. Need a frame with damaged HP. The right actor bar's step is the only fill evidence in the set.
3. EXP fill colour and the true meaning of the "0" counter; also of the arch notches over Q/W/E.
4. Is the black strip below the bar art or letterbox? Assumed letterbox.
5. Actor bar size, true fill colour, and whether names share the plate: the frames show bars only under numbers and over fire.
6. Purpose of each of the three icon buttons, the scroll button, the party's mini slot and the golden input button.
7. Native frame aspect ratio: "stretched" not confirmed; gems are round in image 2.
8. Notice step rate and hold time; only stills exist.
9. The upper chat area is outside image 2: panel height is unknown. Any full-frame view of it is required for exact proportions.
