# 01 - Inventory window and Chaos Machine "Regular Combination" panel

Confidence tags: **MEASURED** (probe, with image and coords), **OBSERVED** (seen by eye at high zoom), **INFERRED** (domain knowledge or guess). Coordinates written `img4 (x,y)` are raw screenshot pixels. All sizes are also given in **u**, the logical 1x pixel (1 bag cell = 20 u).

---

## 1. Summary

1. A tall, narrow, fixed-size window: **190 x ~432 u, bag cell 20 u**. Top to bottom: dark ornate crown with a title strip, a silver scroll bar with a `[Set option]` tab and a down-pointing silver arrow, a grey stone slab holding a 12-slot paper-doll, an 8 x 8 bag grid, a Zen money field, and three small riveted square buttons. The bottom edge sinks behind the HUD.
2. The whole look depends on **one rule: bright bevels with light from the top right**. Raised things (bag tiles, buttons) are bright on top and right, dark on left and bottom. Sunken things (paper-doll wells, Zen field) are bright on left and bottom, dark on top and right.
3. Bag cells invert the usual logic: **an empty cell is a raised grey stone tile; an occupied cell has its tile removed**, showing near-black backing with a thin grey lattice. Hovering an item tints its whole footprint dark navy. There is no selection border.
4. The frame is a double column of dark iron: a cream-gold outer rim line, a chain/rope column, a thin bronze rail, and a column of bone beads in the pattern "three beads, long bar". Bone-claw ornaments fill the corners. The stone slab has rounded corners and an inner shadow.
5. The Regular Combination panel is the same frame without the scroll or Zen row: two title strips (title and tax), left-aligned status lines, an 8 x 4 grid, two red warning lines, and a 44 x 32 u riveted button with a gold rotating-arrows glyph on an otherwise empty stone slab. The black-silver dragon by the Inventory's lower right belongs to the **HUD**, not the window (see 3.13).

---

## 2. Sources

| Image | Size | What it shows | Cell pitch (px) | Scale to u | px to u mapping (MEASURED, fitted) |
|---|---|---|---|---|---|
| 4.webp | 535x1202 | Cleanest inventory, 3 buttons. Top of window cropped off | 56.3 | 2.816 | `xu=(x+4)/2.816`, `yu=(y+32)/2.816` |
| 11.webp | 399x900 | Whole window incl. the top crown and left rim, smaller | 39.9-40.0 | 2.00 | `xu=(x-6)/2`, `yu=(y-24)/2` |
| 5.webp | 1038x1218 | Combine (left) + Inventory (right), side by side | 56.1-56.4 | 2.8125 | Inv: `xu=(x-526)/2.8125`. Combine: `xu=(x+10.5)/2.8125`. Both: `yu=(y+2)/2.8125` |
| 10.webp | 605x920 | Combine with filled grid, hover, tooltip | 56.3-56.5 horizontal (peaks 81.5, 137.5, 193.5, 250.5); vertical peaks 286/343/400/456 give 56.7 | 2.82 | `xu=(x+13)/2.82`, `yu=(y+24)/2.82` |
| 6, 7, 8, 9.png | various | Tooltips over panels; hover tint, red glow | ~same as 5 | n/a | Used for tint colour only |
| 2.webp | 1803 wide | HUD strip; proves dragon ownership | n/a | n/a | n/a |

Images are scaled, compressed, AI-upscaled video frames. Edges are softened by 1-3 px and small text is mangled, so geometry is good to about **±1 u** and colours to about ±6 per channel. Text-peak colours are lower bounds.

**Key derivation (MEASURED):** in img4, tile bright-top lines at y=758/814/871/928 and right-edge peaks at x=89/145/203/259/315/371 give a pitch of 56.3. Img11 rim lines at x=6-8 and x=380-382 span 377 px, or 188.5 u. The original game this resembles used a 190 px wide window on a 640x480 canvas with 20 px cells (INFERRED, domain knowledge), and every measurement lands on that lattice to within 1 u.

---

## 3. Element-by-element spec

### 3.0 Composition, top to bottom (u)

| y (u) | Band |
|---|---|
| 0-12 | Crown: black with bone/cream ornaments |
| 12-20 | Title strip, "Inventory" |
| 22-32 | Silver scroll bar with `[Set option]` tab (26-32) |
| 34-41 | Silver down-arrow, between curved ornament arcs |
| ~41 | Top edge of stone slab (rounded corners) |
| 46-192 | Paper-doll, three well rows |
| 200-360 | Bag grid, 8 x 8 |
| 368-386 | Zen row |
| 395-419 | Button row |
| ~428 | Bottom edge of stone slab |
| 428-432+ | Bottom bone ornament; hidden behind HUD on the right side |

The window fills the screen height down to the HUD. Img5 shows the Inventory at the far right with Combine directly to its left, so screen layout is `[game][Combine 190][Inventory 190]` (OBSERVED).

### 3.1 Outer frame columns

OBSERVED and MEASURED at img4 y=280-480 and img11 y=300. Listed from outside in (left side; the right is a mirror). Material: dark forged iron with bone, seen through a black gap.

| Layer | Offset from outer edge (u) | Width (u) | Look |
|---|---|---|---|
| Cream rim line | 0-1.5 | 1.5 | Solid line. Peak `#F6F1DC`, sides `#C7BA9C`, darker wear gaps `#928C78`. This is the brightest thing on the frame. On img4 right side, it occurs at x=523-526 |
| Black gap | 1.5-2.5 | 1 | `#060000` |
| Chain/rope column | 2.5-7 | 4.5 | Dark twisted links, `#1A191C` to `#4C4B50` with cool blue-grey sheen. Each link has a small pale highlight at the top `#726C67`. Repeats every ~6 u (MEASURED: 17 px on img4) |
| Bronze rail | ~7-8 | 1 | Vertical dark olive-brown line `#3E3630` to `#514E44` |
| Black gutter | 8-9.5 | 1.5 | `#0E0E0C` |
| Bead column | 9.5-11 | 1.5 | Repeating vertical pattern: three bright bone beads (each ~2 x 3 u, `#C7C4B0`, ~6 u apart), then a long dim olive bar (~2 x 25 u, `#57544A`). Pattern period ~44 u (MEASURED: beads at img4 y=307/323/340, bar 353-424, beads again 430...) |
| Dark gutter | 11-12 | 1 | `#1A170F` |
| Stone slab lip | 12+ | 2 | Stone darkened by inner shadow, `#282828` at the edge rising to `#404243` at ~5 u inside |

MEASURED asymmetry: frame centre sits at img4 x=261 but the bag centre is at x=264.5. Contents are about **1 u right of frame centre**. Also, rims on img5 are 518-521 (left window) and 525-529 (right window), with ~2.5 u between them. So each window occupies a 190 u slot while its **art is only ~188 u wide**; the last 2 u on the right is black gap.

The chain has no break at the slab, and runs the full height. On img5, the two adjacent windows form a symmetrical centre column of `chain | rim | gap | rim | chain`.

### 3.2 Corners and top crown

OBSERVED on img11 y=24-110 and img5 x=0-90.

- **Top edge (y 0-4 u):** an irregular wavy line of cream-gold (`#8D8A67` to `#CDC7A5`), with two rising arcs either side of centre, like a moulding. Thin (~1 u). Black behind.
- **Top corners (~35 x 35 u each, mirrored):** black field with a sharp **bone claw** curving from the outer corner inward and down, about 15 u long, cream highlight `#D8D2B5` falling to `#3E3C33`. Scattered around it are small polished ovals, like knuckles, `#A5A5A5` with black gaps. A tiny pure-white stud at (~5,~5) (MEASURED img5 region x~23,y~5; img11 x~30,y~29).
- **Under the scroll:** at each end, 3-4 slanted bone teeth step diagonally towards the centre at y~32-36 u, then fade into two shallow **arcs** either side of the arrow. Colours `#626368` highlight, `#151515` shadow.
- **Stone slab corners:** rounded, ~6 u radius (OBSERVED img5). A soft dark halo fills between slab and frame.
- **Bottom corners (OBSERVED img5 bottom left, x=0-110,y=1130-1218):** a polished bone arc runs from the bottom of the bead column, curves outward to the bottom edge, about 12 u radius, bright `#D0D0D0`. Beside it, three teardrop bone beads run along the bottom edge, heading to centre, then a thin cream wavy line (`#6A6042`) and a small white stud. Up the side, three twisted bone beads climb the stone edge.
- **Bottom edge centre:** thin wavy cream line (mirrors the top edge), arcs close to the centre with a gold tint (`#7A6B40`). On Inventory, mostly hidden by HUD.

### 3.3 Title strip

MEASURED img4 x=92-430,y=1-23; img11 y=48-63.

- Rect (34,12,120,8) u. Flat near-black `#141414` on a blacker field `#060606`. No bevel, no border, very slight lift only.
- Text "Inventory" centred at (94.5,16) u, near-white `#FDFDFE`. See Typography.

### 3.4 Scroll bar ornament

OBSERVED and MEASURED img4 3x crop.

- Rect (16,22,158,10) u. Horizontal **polished steel cylinder**, like a scroll rod. Light comes from above: top `#626368`, a bright broken specular streak `#A5A6AB` along the upper third (y~24 u), mid `#626368`, bottom shading `#353639`, then `#151515`. Metal shows scratch-like streaking along its length.
- **End collars:** at each end, a wider ring (~11 u wide, starting ~6 u in), 1 u taller than the rod, brighter `#D0D0D0`, separated from the rod by a thin dark line `#111`. Outer face tapers to a cone with a dull point, then a dark tip into the frame. The end shape is roughly `<|==|---`.
- The bar overlaps the frame ornaments and is the widest non-frame object (spans 83% of window).

### 3.5 `[Set option]` tab

- Rect (78,26,33,6) u. MEASURED img4 x=217-310,y=42-57. Sits on the lower half of the scroll, centred at x~94.5, hanging slightly below the rod.
- Dark plate `#151515`, slightly translucent. Text in square brackets `[Set option]`, grey-white, peak `#D5D5DB`. Legibility is poor in all images; "Set option" is read from the brief and the fragment `[S..t op..on]`.
- Function INFERRED: hover shows the equipped set-bonus popup. No open state is seen in any image.

### 3.6 Down arrow

Rect (87,34,13,7) u. MEASURED img4 x=243-280,y=65-83. Solid silver triangle pointing down, soft shading: top `#BDBDBD` to tip `#6A6A6A`, with a dark drop shadow beneath. OBSERVED, static in all images. Reads as "dropdown" under `[Set option]`.

### 3.7 Stone slab

- Slab spans x 12-177 u, y 41-428 u, rounded corners ~6 u.
- Colour: cool mid-dark grey. MEASURED img4 (155-200,110-200): `#403F43`, `#454449`, `#49484C`, `#4F4F53`. MEASURED img5 combine (60-480,700-820): `#39383B`, `#3F3F42`, `#424245`, `#454449`, `#4A4A4E`. Blue channel is always ~3 above red; keep that cool bias.
- Texture: fine grain with low-contrast **cloudy blotches and a few long diagonal cracks**, like worn slate. Largest visible on the Combine slab below y~300 u: a dark crack band running upper right to lower left, roughly (70,310)-(170,390) u. Grain amplitude about ±8 luma.
- Inner shadow: ~5 u soft dark falloff on all edges (MEASURED img5 y=700: `#2A2A2C` at slab edge, `#38373A`, `#404243` ~5 u in).
- It has no specular; matte.

### 3.8 Equipment section (paper-doll)

All wells sit on a lattice of 20 u from origin (14,46), with row gaps of 3 u. The bag lattice is **1 u to the right** of the well lattice (x=15). This 1 u offset is real (MEASURED: well left at img4 x=36, bag left at x=39).

Rows MEASURED by vertical scans: row 1 y=97-209 (px), row 2 y=218-388, row 3 y=396-508, small wells 219-274 and 396-451. Columns x=36/148/205/317/372/487.

Diagram in cells (`.` is bare stone):

```
Row1 (2 tall):  [PET ] .  [HELM][  WINGS   ]
Row2 (3 tall):  [WPN1][P] [ARM ] .  [WPN2 ]
                [    ] .  [    ] .  [     ]
                [    ] .  [    ] .  [     ]
Row3 (2 tall):  [GLOV][R] [PANT][R] [BOOT ]
                [    ] .  [    ] .  [     ]
```

| Slot | Rect u (x,y,w,h) | Cells | Empty-state silhouette (OBSERVED) |
|---|---|---|---|
| Pet/helper | 14,46,40,40 | 2x2 | Small spread-winged winged figure, seen from above: thin body with four long blade-like wings forming an X, slim tail. Faint fairy or dragonfly, with a soft glow pool behind |
| Helm | 74,46,40,40 | 2x2 | Closed knight helm, three-quarter facing right: domed top, horizontal visor slit, faceted jaw |
| Wings | 114,46,60,40 | 3x2 | Pair of spread feathered wings, dark grey and very faint, fanned low. Cape item covers the centre in img4 |
| Weapon L | 14,89,40,60 | 2x3 | Crossed bow and sword: a large recurve bow arcing top left, sword diagonally from upper right to lower left, hilt with crossguard at the lower left |
| Pendant | 54,89,20,20 | 1x1 | Thin teardrop loop of chain with a small pendant lump at the lower left. Outline only, very dim |
| Armor | 74,89,40,60 | 2x3 | Full plate cuirass: rounded chest with high shoulder pauldrons, waist band, flared skirt plates. Brightest silhouette |
| Weapon R | 134,89,40,60 | 2x3 | Spiked mace with star head at upper right, handle diagonal to lower left, plus a dim kite shield behind it |
| Gloves | 14,152,40,40 | 2x2 | Pair of plate gauntlets, crossed at the cuffs, fingers pointing down-outward |
| Ring L | 54,152,20,20 | 1x1 | Plain thick ring seen at 3/4, rendered like a chrome C |
| Pants | 74,152,40,40 | 2x2 | Plate leg armour: belt at top, two thigh plates with central V seam |
| Ring R | 114,152,20,20 | 1x1 | Same ring (appears identical, no mirror) |
| Boots | 134,152,40,40 | 2x2 | Pair of plate boots, side view facing left, offset in depth |

Empty stone remains between the wells: 54-74 at row 1, the area under the pendant, 114-134 at row 2, and under each ring. Those are bare slab, with no decoration.

**Well construction (identical for all sizes, MEASURED img4):**

| Part | Width | Colour |
|---|---|---|
| Top inner wall | 2 u | Black `#020202`, softly fading into interior |
| Right inner wall | 1.5-2 u | `#020202` to `#151515` |
| Left bevel | 1.5 u | **Vertical gradient**: `#3C393A` at top, `#84888D` by the middle, `#C7C9CA` at the bottom |
| Bottom bevel | 2 u | **Horizontal gradient**: brightest at left `#D6D8D7`, plateau `#C2C2C2`, falling to `#717171` at the right end |
| Bottom-left corner | n/a | Mitred, peak brightness; reads as a hot L-shaped corner highlight |
| Below bevel | 3 u gap | Stone strip `#484A4B`, slightly brighter than slab, next well's top black starts at once |
| Interior | n/a | `#131313` to `#1C1C1C`, a touch cooler in places (`#16151A`) |

So the well is an L-shaped bright lip on left and bottom, dark on top and right. **Silhouettes are monochrome chromed steel:** rendered like embossed metal, highlights `#FDFFFF` on Armor/Pants/Boots, down to `#404042`; dim ones (Pet, Wings, Pendant) peak about `#7E7E7E`. They have a soft-light gradient, sharp specular, no outline. They read like photographed polished pewter in near darkness. Items are centred in wells.

**When equipped:** the silhouette **stays visible underneath** the item. MEASURED img4: wings silhouette is visible either side of the blue cape. There is no dark-out, no tile change. Only the wings slot is occupied in any image. INFERRED: the silhouettes are part of the background art.

**Item overflow:** items are not clipped to wells. The cape occupies img4 y=85-216 against a well of y=97-209, so it overlaps by ~4 u at the top and ~2 u at the bottom, drawing above bevel and stone.

### 3.9 Bag grid

MEASURED: **8 columns x 8 rows, 20 u cells, 160 x 160 u, origin (15,200) u**. Img4 x=39-490, y=532-982. There is no gap between cells; separation comes from bevels only.

Three visual cell states:

**A. Empty cell: raised stone tile** (MEASURED img4 y=840 and x=120)

| Part | Thickness (of a 20 u cell) | Colour |
|---|---|---|
| Top bevel | 1.5 u | `#A5A5A5` peak, falling `#797979` |
| Right bevel | 1.5 u | `#8F8F91` to `#A7A6A9` |
| Bottom edge | 2 u | Shadow `#212121` to `#2C2C2C` |
| Left edge | 1.5-2 u | Shadow `#1D1D1F` to `#262628` |
| Face | ~15 u | `#3E3F41`, `#434345`, `#3B3C3E`, `#36383A` flat with slight noise; no gradient |

The right bevel's top joins the top bevel with a bright corner; bottom-right forms a short mitre (OBSERVED). Tiles look like a pillowed stone paving block.

**B. Occupied cell: tile removed** (MEASURED img4 x=430-485,y=545-585; x=92-212,y=615)

- Fill flat `#232425` (samples `#232325`, `#222123`, `#242427`).
- Thin lattice lines ~1 u, breaking at the crossings (OBSERVED img5 4x crop: horizontal segments stop short of the verticals). Vertical lines `#444648`-`#4F4F4F`. Horizontal lines fainter, `#303234`.
- The lattice covers the **whole dark region**, including inside a multi-cell footprint. So a 2x3 item shows three interior lattice lines under it (one vertical, two horizontal). Multi-cell items do not merge visually.
- All cells are the same state regardless of item type. No rarity tint.

**C. Hovered item: navy tint** (MEASURED img10 x=262-310,y=295-445; 6.png, 7.png, 9.png)

- Fill `#1C2030` (samples `#1C1E30`, `#1E2235`, `#191E2E`); lattice lines vertical `#323848`, horizontal `#262939`. Covers **every cell of the hovered item's footprint**, and no others.
- Same colour in the bag (6.png), and in the combine grid (9.png, 10.webp). It has no border, no glow, no animation visible.
- Recreate as navy overlay at ~25% alpha: over `#232425`, `#071451 @ 0.25` gives `#1C2030`.
- Upper rows are a bit brighter than lower (`#23263C` near top edge), but this may be compression; treat as flat.

**Multi-cell items and overflow:** all items sit on footprints but are rendered as 3D models, centred in the footprint, and **free to overflow** (MEASURED or OBSERVED):
- Bone armour (white extent from img4 x=28 and img5 x=557, against grid left 39 and 568) spills 11 px, about 4 u or 0.2 cell, left of the grid into the frame.
- The bow runs from img4 y=765 to y=1000, ~6 u past the grid bottom.
- Item art draws above tiles, bevels and frame. Neighbouring tile bevels are overlapped where art crosses them.

**Observed occupancy.** `#` = occupied, `.` = empty tile.

```
img4 / img11             img5
r1  # # A A # F H H      # # A A # F . .
r2  # # A A . F H H      # . A A . F . .
r3  B B A A G G K K      B B A A . . K K
r4  B B . . G G K K      B B . . . . K K
r5  . . . . . . W W      S S . . . . W W
r6  . . . . . . W W      S S . . . . W W
r7  . . . . . . W W      S S . . . . W W
r8  . . . . . . W W      S S . . . . W W
```

Footprints by item (useful for a test fixture): small jewels 1x1; axe A 2x3; feather F 1x2; bone armour B 2x2; green pants G 2x2; gold helm H 2x2; gold armour K 2x2; bow W 2x4; red spear S 2x4. Img4's gold figure looks like one object but is two: img5 keeps only the lower half, and rows 1-2 turn back into tiles (OBSERVED). The 2x2 armour footprint is unusually small and is simply what occupancy shows.

### 3.10 Zen row

- **Label:** "Zen", cream `#FBF0D1`, on a small dark rectangle `#2D2920` (rect 19,370,12,8 u; MEASURED img4 x=50-82,y~1010-1034). Vertically in line with the digits, not field centre.
- **Field:** rect (49,368,113,18) u. MEASURED img4 x=135-452,y=1004-1055. **Sunken** like a well: interior `#232323`/`#1F2121`; top wall black 1.5 u `#000000`; right wall `#060606` 1.5 u; left bevel `#B9B9B9` 1.5 u; bottom bevel `#CED0CF` 2 u. The bottom bevel shows a **gloss hot-spot in its right third** (OBSERVED img5 from x~870 to the right end: near white, left of that `#9E9E9E`). Contrasts with wells, which are brighter at the left.
- **Number:** red with thousands commas (only the leading `745`/`744` group is reliably legible; the last three digits, written here as `705`, are a guess), left aligned at x=59 u, centred at y=374.4 u: **upper half** of the field, leaving the lower half empty (space INFERRED to be for a second currency row in other builds). Peak `#C54029`, top-2% mean `#A92818`, body after blur `#853123`. Dim, with a very dark drop shadow; deliberately low contrast.
- Value size is large here (9 digits) and consumes 34% of the field width.

### 3.11 Bottom action buttons

MEASURED img4 y=1100 and x=85. Rects: **Close (19,395,24,24), Repair (47,395,24,24), Personal store (75,395,24,24) u.** 4 u gaps, pitch 28 u.

Construction, shared:
- Raised **riveted steel plate**. Face `#515053`/`#48484A`/`#39383C`, cloudy brushed.
- Top bevel 1.5 u `#B8BAB9`; right bevel 1.5 u `#74767A`; left wall 2 u `#000001`; bottom wall 2 u `#000000`.
- Four corner **rivets** at ~4 u from corners: 3 u diameter, dark ring `#151515`, light dot centre `#A5A5A5`, faint bright crescent on the lower outer side.

Icons, centred, ~14 x 14 u. Art is OBSERVED; the Close, Repair and Personal store functions are INFERRED from the icons and from domain knowledge:
- **Close:** thick X, four rounded arms. Amber: centre `#F9AA0C`, top `#F4DC77`, edges `#BB761B`, dark orange-brown shadow `#3E2A08` at lower left. Colour MEASURED img4 x=85,y=1109-1120; `#DEB350` by palette.
- **Repair:** cream-tan bone and leather colour `#E5DAB0`, `#BEB289`, `#735C3F`. A hammer angled from upper right to lower left over a hand with spread fingers; slightly overfills the plate.
- **Personal store:** tan chest/bags, `#D8CAA1`, `#735C3F`, `#2B1C06`: a satchel on top of a small chest with a clasp. Overflows the top right corner by ~1 u.

In img5, Repair and Store **disappear**, and Close remains at the same place (MEASURED img5 x=580-648,y=1113-1180 gives (19,396) u). Nothing replaces them: bare stone.

### 3.12 Regular Combination panel

Same frame, same crown shape, same slab, same chain. Differences (OBSERVED img5 and img10):

- **No scroll bar, no arrow.** Instead the arcs under where the scroll would be ("crown arcs") rise a little.
- **Title strip** rect (35,8,120,8) u, ~4 u **higher** than Inventory's. "Regular Combination" centred at y=11.5 u. MEASURED img5 y=20-41.
- **Tax strip** rect (35,20,119,8) u, second strip, same fill. Text "Tax rate: 0% (changed in real-time)" centred, white. MEASURED img10 y=31-54.
- **Slab** top ~40 u, rounded. Bottom ~428 u.

Text lines (all left aligned at x=19.5 u, on faint translucent dark boxes; MEASURED):

| Line centre y (u) | img | Text | Colour |
|---|---|---|---|
| 54 | 10 | `Create Wings` | Yellow, peak `#F4EC98` to `#FFFEA4`, body `#C0B670` |
| 54 | 5 | `Improper items for combination` | Red, peak `#F46650`, top-10% saturated mean `#C64230` |
| 74 | 10 | `Combining Success rate: 90%` | White, `#E9EAF3` |
| 74 | 5 | `Combining Success rate: 0%` | Same |
| 94 | 10 | `Required zen: 5,000,000(5,000,000)` | White `#DEE5EF` |
| 94 | 5 | `Required zen: 0(0)` | Same |
| 215 | 5 | `When Combining, You must have a Jewel of Chaos` | Orange-red, peak `#D5380D`, mean `#C14828` |
| 230 | 5 | `in order to combine items` | Same |

- Line pitch: 20 u for the status block, 15 u for the warning block.
- Line backing: translucent black box tight to text, `#383A40`-ish on stone `#444346`, so ~15-20% black. Height ~8 u; **width follows the text**.
- First line is **state-driven**: recipe name in yellow when valid, error text in red when invalid (OBSERVED). Second number in brackets is INFERRED to be the taxed cost.
- The gold lines on img5/10 like `You can press /offattack for offline afk mode` and `For free 12 Hours and 24 for VIPs`, are **chat or server notices** drawn over the panel, with their own translucent boxes. They are **not** panel content: they overlap the tax line, spill past both frame edges, and differ between frames. Do not build them here.

**Grid:** 8 x 4 cells, 20 u, rect (15,110,160,80) u. MEASURED img5 x=31.7-482,y=306-532. Same three states as bag. Img10 occupancy (row 4 mostly hidden by tooltip):

```
r1  . # # # A A . .     wings = cols 2-4, rows 1-2 (3x2)
r2  . # # # A A # F     axe = cols 5-6, rows 1-3 (2x3), hovered, navy
r3  . . . . A A . F     jewel = col 7 row 2; feather = col 8 rows 2-3 (1x2)
r4  . ? ...hidden...
```

**Combine button:** rect (74,301,44,32) u. MEASURED img5 x=199-323,y=845-934.
- Same riveted plate language as 3.11: top bevel `#BFBEC3` 1.5 u; right bevel `#A2A1A6` 1.5 u; bottom wall `#1D1F20` 1.5 u; left wall `#212327`-`#000` 2 u; face `#504A51`/`#494A51`, four rivets.
- Glyph: **gold cycle**: a central disc (~10 u) with two curved arrows sweeping around it clockwise, one over the top pointing right, one under pointing left. Fill `#F0C532`, peak `#FFD546`, lower edges `#B4780F`. Thin dark cast shadow to the lower right of each shape (`#403628`). The glyph's cast shadow suggests light from upper left, unlike the frame's bevels; keep that as is.

Below the button, bare stone to the bottom. Combine has **no close button of its own** in any image (OBSERVED). INFERRED: closing Inventory closes it, or the NPC session.

### 3.13 Dragon head (not a window element)

Img4 x=235-528,y=1060-1200 shows a black-chrome dragon head facing left, with a tan horn or tooth `#D9BC8F` at x~235-280 and a small gold eye at x~340-370. In **2.webp the same dragon appears mirrored at both ends of the screen**, part of the HUD. It is in Inventory's lower right only because the window sits against that end, and Combine has none.

Therefore: **do not draw it in the window.** The HUD draws above the window, and overlaps the lower-right corner of the Personal Store button (OBSERVED: the horn tip touches the chest icon at img4 (~262,1140)). Layer order: window < HUD dragon. The window art beneath is presumably ordinary frame.

---

## 4. States and behaviour

| Item | Observation | Tag |
|---|---|---|
| Item hover | Navy tint of whole footprint | OBSERVED |
| Tooltip | Appears on hover; content and placement are owned by 02. Horizontally centred on the footprint. Above: panel bottom = footprint top + 0.5 cell, so the panel overlaps the item (6.png footprint y=311-423, panel bottom 340, overlap 29 px; 7.png same). Below: panel top = footprint bottom (img10 footprint bottom 454-455, panel top 450-452; panel x=110-509, centre 309.5, footprint centre 311). Above is preferred, flipping below when the panel would not fit | MEASURED positions, INFERRED rule |
| Cursor | A **steel gauntlet with pointing index**, about 1.5 cells long, tip to upper left. Hotspot INFERRED at the upper-left fingertip. Evidence: img5 tip at (~888,~905) is on an empty tile beside the bow, and the bow is **not** tinted; img10 tip at ~(335,345) is on the axe, and it is tinted | OBSERVED, hotspot INFERRED |
| Drag | **No frame shows an item attached to the cursor.** Img5 is a hover-free frame, not a proven drag. Drag rules (pick up on click, footprint-sized placement preview, tile restored while lifted) are all INFERRED. Suggested placement preview: reuse the hover tint, red tint for invalid | INFERRED |
| Item glow | Red spear (img5 x=590-660, 6.png): soft red halo over **cells and tile-free backing only near the spearhead**, `#2F2627` faint in img5, stronger `#5F2E2B`/`#6F4543` in 6.png, so intensity varies, probably a pulsing additive glow from the 3D item. Extends ~1 cell around head, not footprint-shaped | OBSERVED, pulse INFERRED |
| White bone armour | Rendered blown-out white in all frames: bright item, not a state. It also keeps the same state in 6.png while hovered | OBSERVED |
| Axe colour | Orange in img4, 10; lilac in img5, in the same bag slot, with neighbouring items stable. It is an item-specific colour effect, not a window state; see 02 section 9 | OBSERVED |
| Money change | Leading group reads 745 on img4/11 and 744 on img5, a drop of 1,000,000 if the unreadable last three digits are unchanged. Between those frames the bag loses the gold helm, green pants and one amber gem, and gains the red spear; img3 shows `You have successfully created the item`. So the spear is a combination output paid for by that Zen (INFERRED). Only the digits change; no animation evidence. Img5's `Required zen: 0(0)` is the panel after the combine | OBSERVED, cause INFERRED |
| Button set change | With Combine open, Repair and Store are hidden, Close stays | OBSERVED; rule INFERRED: action buttons are context-disabled during NPC sessions |
| Button hover/press | No frame shows it. Suggest: hover = face +10% and glyph brighter; press = swap bevels and move glyph +1,+1 u | INFERRED |
| Combine button | Looks the same at 0% with improper items (img5) and at 90% (img10): no disabled look (`#F0C532` vs `#EFC131`). So it is always active, and the server rejects bad requests | MEASURED; rule INFERRED |
| Combine grid and bag | Items can sit in both, one at a time; the img10 items were moved in. Cross-window transfer INFERRED | INFERRED |
| Window open | Fixed position. No animation evidence. Non-movable, opens at right edge | INFERRED |
| Equipped silhouette | Stays underneath item | OBSERVED |
| Black hollow cone | 1.webp x=215-270,y=440-495: a dark cone with its point upper left and an open, hollow end lower right, over a combat flash. Unidentified; its pose resembles the gauntlet, so possibly an attack cursor | OBSERVED, identity INFERRED |

---

## 5. Layout tables (1x base: 190 x 432 u, cell 20 u)

Rects are `x, y, w, h` from the window's top left. Convert with section 2. Units: 1 cell = 20 u; HUD height H = 48 u. UI scale snaps to 0.25 steps, so 1080p uses 2.25 to fill 480 u; img4 is about 2.8x. Text is not scaled with the art (section 7).

### Inventory

| Element | Rect u | Conf |
|---|---|---|
| Window slot | 0,0,190,432 | Width MEASURED; height 432 INFERRED (img2's HUD bar spans 433-480 u at 2.817 px/u) |
| Frame art extents | 0,0,188,432 | Width MEASURED; height INFERRED |
| Stone slab | 12,41,165,387 | MEASURED ±2 |
| Title strip | 34,12,120,8 | MEASURED |
| Title text centre | 94.5,16 | MEASURED |
| Scroll bar | 16,22,158,10 | MEASURED |
| Scroll collars | 22,22,11,10 and 157,22,11,10 | MEASURED ±1 |
| Set option tab | 78,26,33,6 | MEASURED |
| Down arrow | 87,34,13,7 | MEASURED |
| Wells | see 3.8 | MEASURED |
| Bag grid | 15,200,160,160 | MEASURED |
| Bag cell (c,r 0-based) | 15+20c, 200+20r, 20, 20 | MEASURED |
| Zen label backing | 19,370,12,8 | MEASURED |
| Zen field | 49,368,113,18 | MEASURED |
| Zen text origin | 59, centre y 374.4 | MEASURED |
| Close | 19,395,24,24 | MEASURED |
| Repair | 47,395,24,24 | MEASURED |
| Personal store | 75,395,24,24 | MEASURED |

### Regular Combination

| Element | Rect u | Conf |
|---|---|---|
| Window slot | 0,0,190,432 | Width MEASURED; height INFERRED |
| Stone slab | 12,40,165,388 | MEASURED ±2 |
| Title strip | 35,8,120,8 | MEASURED |
| Tax strip | 35,20,119,8 | MEASURED |
| Status lines | x=19.5, centre y=54, 74, 94 | MEASURED |
| Grid | 15,110,160,80 | MEASURED |
| Warning lines | x=19.5, centre y=215, 230 | MEASURED |
| Combine button | 74,301,44,32 | MEASURED |

Screen placement at 640x480 logical: Inventory at x=450, Combine at x=260, y=0 (INFERRED from adjacency in img5).

---

## 6. Colour tokens

| Token | Value | Use |
|---|---|---|
| `frame.void` | `#060606` | Crown field, frame gaps |
| `frame.rim` | `#E4E3BF` | Outer rim line (peak `#F6F1DC`) |
| `frame.rim.dim` | `#928C78` | Rim edges, wear |
| `frame.bronze` | `#514E44` | Bronze rail |
| `frame.chain` | `#4C4B50` | Chain link |
| `frame.chain.dark` | `#1A191C` | Chain shadow |
| `frame.chain.spec` | `#726C67` | Chain highlight |
| `frame.bone` | `#D8D2B5` | Claws, beads |
| `frame.bone.dim` | `#57544A` | Bead bars |
| `scroll.spec` | `#A5A6AB` | Scroll streak |
| `scroll.mid` | `#626368` | Scroll body |
| `scroll.shade` | `#353639` | Scroll underside |
| `strip.fill` | `#141414` | Title/tax strip |
| `stone.base` | `#444347` | Slab average |
| `stone.low` | `#39383B` | Grain low |
| `stone.high` | `#4F4F53` | Grain high |
| `stone.edge` | `#2A2A2C` | Inner shadow end |
| `well.fill` | `#141415` | Well interior |
| `well.wall` | `#020202` | Top and right wall |
| `well.bevel.hi` | `#D6D8D7` | Lip peak, lower left |
| `well.bevel.mid` | `#C2C2C2` | Lip plateau |
| `well.bevel.lo` | `#717171` | Lip end, right |
| `well.bevel.top` | `#3C393A` | Left lip top |
| `sil.hi` | `#FDFFFF` | Silhouette specular |
| `sil.mid` | `#737375` | Silhouette body |
| `sil.lo` | `#28272A` | Silhouette shade |
| `cell.tile` | `#404143` | Empty tile face |
| `cell.tile.top` | `#A5A5A5` | Tile top bevel |
| `cell.tile.right` | `#8F8F91` | Tile right bevel |
| `cell.tile.shadow` | `#232325` | Tile left and bottom |
| `cell.occupied` | `#232425` | Item backing |
| `cell.lattice.v` | `#444648`-`#4F4F4F` | Vertical lattice |
| `cell.lattice.h` | `#303234` | Horizontal lattice |
| `cell.hover` | `#1C2030` | Rendered hover fill |
| `cell.hover.overlay` | `#071451` @ 25% | Overlay form |
| `cell.hover.lattice.v` | `#323848` | Vertical lattice under hover |
| `cell.hover.lattice.h` | `#262939` | Horizontal lattice under hover |
| `zen.fill` | `#232323` | Zen field |
| `zen.bevel` | `#CED0CF` | Zen left and bottom lip |
| `zen.value` | `#A92818` | Number (peak `#C54029`) |
| `zen.label` | `#FBF0D1` | "Zen" |
| `zen.label.plate` | `#2D2920` | Label backing |
| `btn.face` | `#4C4B4E` | Button face |
| `btn.bevel.top` | `#B8BAB9` | Top bevel |
| `btn.bevel.right` | `#74767A` | Right bevel |
| `btn.wall` | `#000000` | Left and bottom wall |
| `btn.rivet.dark` | `#151515` | Rivet ring |
| `btn.rivet.dot` | `#A5A5A5` | Rivet centre |
| `icon.amber` | `#F9AA0C` | Close glyph |
| `icon.amber.light` | `#F4DC77` | Close glyph top |
| `icon.tan` | `#E5DAB0` | Repair/store highlight |
| `icon.tan.shade` | `#735C3F` | Repair/store shade |
| `icon.gold` | `#F0C532` | Combine glyph |
| `icon.gold.hi` | `#FFD546` | Combine glyph peak |
| `icon.gold.lo` | `#B4780F` | Combine glyph edge |
| `text.title` | `#FDFDFE` | Titles |
| `text.body` | `#E9EAF3` | Combine status |
| `text.setopt` | `#D5D5DB` | Set option |
| `text.recipe` | `#F4EC98` | Recipe name |
| `text.error` | `#F46650` peak (top-10% saturated mean `#C64230`) | Improper items |
| `text.warning` | `#D5380D` | Warnings |
| `text.backing` | `#000000` @ ~18% | Line boxes |

---

## 7. Typography

Table values are OBSERVED. Text is drawn at screen pixels, **not scaled with the art**: on 2.8x screens letters are about 5-6 u tall, smaller than scaling a 1x bitmap font would give. The face is INFERRED to be a system UI font.

| Use | Feel | Size (cap height) | Weight | Tracking | Shadow | Colour |
|---|---|---|---|---|---|---|
| Titles | Thin humanist sans (Tahoma/Verdana class) | 14 px on img4, 5 u | Regular | Wide, about +1 px per letter at 1x | None visible; strip is dark | `text.title` |
| Status lines | Same | ~13 px on img10, 4.6 u | Regular | Wide | Backing box | white/yellow/red |
| Zen digits | Same, tabular feel | ~15 px on img4, 5.4 u | Regular to medium | Tight | Dark 1 u shadow, or blur-darkened | `zen.value` |
| Zen label | Same | 14 px | Regular | Normal | Plate | `zen.label` |
| Set option | Same | ~12 px, 4.3 u | Regular | Wide | Plate | `text.setopt` |

Text is **crisp and unoutlined**, with subtle LCD-style softness. Recommended: an original or open-licence 11 px sans, drawn at screen pixels, with letter spacing +0.5 to +1 px. Title width guide: "Inventory" spans 80 px on img4, 28 u.

---

## 8. Recreation notes (original art only)

Do not trace or sample the source art. Recreate from the structure below.

**Procedural (shader or draw calls), cheap and scalable:**
- Bag tiles, occupied lattice, hover overlay. Build as: draw backing and lattice for all cells, then draw raised tile for every **empty** cell. Item sprites last, unclipped.
- Well bevels: L-shaped gradients; use the hot corner at lower left. Two 1D gradients are enough.
- Zen field: as wells, but put the gloss at the right third of the bottom edge.
- Title strips, text boxes: flat translucent rects.
- Button plate: filled rect with four bevel quads plus rivet sprite (a tiny ring).

**Textures to author yourself:**
- Stone slab: tiled 256x256 noise (fractal cloud + grain) on `stone.base`, with 2-3 hand-placed crack strokes, and a 5 u inner shadow on a 9-slice.
- Frame: 9-slice with tiling vertical strip of 44 u period for beads, 6 u period for chain. Draw at 4x to get the polished bone look; downsample. Top and bottom crown are unique 190 u wide images. One for Inventory, one for Combine (or share and add the scroll as a separate sprite).
- Scroll: one sprite, cylindrical gradient, streaked specular, cap sprites mirrored.
- Silhouettes: 12 greyscale sprites. Quickest original route: render your game's own low-poly equipment in chrome matcap on black, slightly blurred. Keep them dim for Pet, Wings, Pendant.
- Icons: X, repair, store, combine. Ours can be new motifs if the gold-tan palette, soft bevels and overflow are kept.
- Cursor: own gauntlet design, hotspot upper left.

**Key points that give the feel:**
1. Bright hard-edged bevels on very dark ground; do not soften them. `#D6D8D7` next to `#141415` is correct.
2. Top-right light for all bevels.
3. Cool grey stone and warm cream bone; gold only for active glyphs.
4. Items overflow their cells and sit on top of everything in the window.
5. Keep text small and thin.
6. At 3x or above, add detail only in textures, not geometry.

---

## 9. Open questions

1. Exact window height: 429 (classic) or ~432 (frame still visible at 432.5 u on img11, y=889). HUD hides the bottom, so either works.
2. `[Set option]` popup: appearance and trigger not shown.
3. Whether the 1 u well/bag offset and the 2 u right gap are intended, or artefacts of the source art. They are measured on several images. Keep them for fidelity, or drop if the 20 u lattice is simpler; nobody will see it.
4. Tooltip flip rule: only 3 sample points.
5. Drag visuals: no evidence at all.
6. Red glow: per-item effect or rarity? Only one item has it, and intensity differs across frames.
7. What the Zen field's empty lower half is for.
8. Button hover, pressed and disabled states are entirely unseen.
9. Colour of pressed Combine button, and any success or failure feedback.
10. Img11 right of the rim (x=383-399): black with a tan oval and white specks, unexplained. The window art ends at the rim, so nothing there belongs to the window.
11. Identity of the black hollow cone in 1.webp (section 4).
