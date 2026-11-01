# 07: Procedural UI asset plan

Asset files only. No C#, csproj or shader changes. Output under `dream/ui/`, generators under `tools/ui-assets/`. All art is original and procedural; never trace, sample or read the screenshots from a generator.

`06-review-findings.md` overrides the specs where they differ.

## Decisions

| # | Topic | Decision |
|---|---|---|
| 1 | Native density | 4 px/u, c = 80 px. Generators work at 16 px/u and downsample 4x |
| 2 | Light | `LIGHT_TL` by default. Window tiles, wells, currency field and riveted plates use `LIGHT_TR` or 01's per-edge colours. Glyphs, icons, HUD and items use TL. Don't "fix" this |
| 3 | Alpha | Straight alpha, RGB dilated under A=0 |
| 4 | Window size | 190 u wide slot, art ends at x=188, last 2 u transparent; 432 tall. Wells x=14, bag x=15 |
| 5 | Chain | Period 5.5 u, so one 44 u tile loops chain and beads |
| 6 | Tiles, wells, buttons | Ship sprites even where 01 says procedural |
| 7 | Naming | `currency.*` and `combine.*`. No names from the source game in sprites or files |
| 8 | Invalid cell | `cell.invalid` `#2F1D1E`, lattice v `#4A3236`, h `#39262A`, overlay `#51070A`@25%. Empty cells get overlay sprites only |
| 9 | Tile bevels | Top 1.5, right 1.5, bottom 2, left 2, face 16.5 u |
| 10 | Gold digit rim | Stays 1 texel; darker uniform at runtime |
| 11 | Digits | 12x16 masks plus a 4x box upscale |
| 12 | Dragon heads | ORIGINAL design from a polygon heightfield: two crescent back-swept horns, a serrated dorsal fin, angular plated snout, ivory lower-jaw fang. No bead rows. Keep corner-rooted low inward wedge, gloss black, amber slit eye |
| 13 | EXP fill | Pale amber `#F0C870`, tile plus 10-section tick |
| 14 | Chat | 9-slice log, default 66 u tall (5 lines at 13 u), 268 u wide |
| 15 | Right gauge | Same 35 u height. Stripe right of centre on both; caps mirror |
| 16 | Icons | Keep role, change motif: repair = hammer on anvil; store = satchel on chest; banner, lens and satchel are our own drawings |
| 17 | Bar split | Left 0-199, centre 199-441, right 441-640 u, plus tiled `hud.gapfill`. Chat sits above the band |
| 18 | Extrusion | 4 px per side |
| 19 | Text | No bitmap UI font; no `ImageFont`, no rasterising `dream/fonts/Arial.ttf`. Sprites for Q/W/E letters and damage digits/Miss only |
| 20 | Out of scope | Actor bars, notices, log and name bands (flat runtime quads) |

## Toolkit contract

`tools/ui-assets/uiart.py` is committed and read-only for workstreams; read it first. Request changes through the coordinator. `tokens.json` holds the spec palette, with `zen.*` renamed `currency.*`.

Each `gen_*.py` exposes `main(out_root: Path) -> None`, writes only its own directories and imports only `uiart`, numpy and Pillow (not `ImageFont`). `pivot` and `slice` are px. In `slice`, values are multiples of 4. State suffixes: `.normal .hover .pressed .disabled`. Hover = face +10%, pressed = swap bevels, disabled = 50% desaturated.

`sdf` costs seconds on big canvases: compute per element, not per full window.

## Manifest

px = u x 4. `{n,h,p,d}` = state suffixes.

### A. `dream/ui/window/window.png`

| Sprite | u | Notes | Spec |
|---|---|---|---|
| `win.crown.inventory` | 190x41 | claws, title field, wavy edge | 01 3.2 |
| `win.crown.combine` | 190x40 | no scroll, raised arcs | 01 3.12 |
| `win.side.left` | 12x44 | tile y; rim, chain, rail, beads | 01 3.1 |
| `win.side.right` | 13x44 | tile y; final 2 u transparent | 01 3.1 |
| `win.bottom` | 190x16 | bone arcs; no dragon | 01 3.2, 3.13 |
| `win.slab.inventory` | 165x387 | radius 6, inner shadow 5 u | 01 3.7 |
| `win.slab.combine` | 165x388 | crack band | 01 3.7 |
| `win.strip` | 8x8 | slice 2 u; `strip.fill` | 01 3.3 |
| `win.linebox` | 8x8 | black @18% | 01 3.12 |
| `win.scroll.rod` | 158x10 | collars | 01 3.4 |
| `win.setoption.tab` `{n,h}` | 33x6 | no text | 01 3.5 |
| `win.arrow.down` `{n,h}` | 13x7 | | 01 3.6 |
| `well.1x1` | 20x20 | L lip, hot lower-left | 01 3.8 |
| `well.2x2` / `well.3x2` / `well.2x3` | 40x40 / 60x40 / 40x60 | slice 3 u | 01 3.8 |
| `sil.pet` `sil.helm` `sil.gloves` `sil.pants` `sil.boots` | 40x40 | chrome; pet dim peak `#7E7E7E` | 01 3.8 |
| `sil.wings` | 60x40 | dim | 01 3.8 |
| `sil.weapon.left` `sil.armor` `sil.weapon.right` | 40x60 | armour brightest | 01 3.8 |
| `sil.pendant` `sil.ring` | 20x20 | | 01 3.8 |
| `cell.empty` `cell.occupied` `cell.hover` `cell.invalid` | 20x20 | lattice half-lines on edges, tiles seamlessly | 01 3.9 |
| `cell.overlay.hover` `cell.overlay.invalid` | 20x20 | flat alpha 25% | 01 3.9 |
| `btn.plate24` `{n,h,p,d}` | 24x24 | 4 rivets | 01 3.11 |
| `btn.plate44x32` `{n,h,p}` | 44x32 | combine button | 01 3.12 |
| `glyph.close` `glyph.repair` `glyph.store` `{n,h}` | 18x18 | ~14 u art | 01 3.11 |
| `glyph.combine` `{n,h}` | 28x24 | TL cast shadow | 01 3.12 |
| `currency.field` | 113x18 | slice 3 u; gloss in right third | 01 3.10 |
| `currency.plate` | 12x8 | | 01 3.10 |
| `tip.panel` | 12x12 | slice 4 u; black @0.80, no border | 02 s1 |
| `tip.band` | 12x8 | horizontal slice 4 u; opaque `#AD0D01` | 02 s3 |

### B. `dream/ui/hud/hud.png`

| Sprite | u | Notes | Spec |
|---|---|---|---|
| `hud.dragon.left/right` | 107x46 | pivot bottom outer corner | 03 s2 |
| `hud.talon.left/right` | 59x46 | gold rings; draws over gem | 03 s7 |
| `hud.base.left/right` | 199x48 | iron, rail | 03 s1 |
| `hud.base.centre` | 242x48 | chain band | 03 s1, s10 |
| `hud.gapfill` | 64x48 | tile x | 03 Responsive |
| `gem.hp.full/empty/mask/spec` | 50x50 | see below | 03 s5 |
| `gem.mp.full/empty/mask/spec` | 50x50 | mirrored, cyan rim | 03 s6 |
| `gauge.left.fill` `gauge.right.fill` | 15x35 | 3-face prism | 03 s4, s6 |
| `gauge.empty` | 15x35 | near black | 03 s4 |
| `gauge.cap.left/right` | 33x46 | hole over the fill | 03 s4 |
| `hud.slab` `{n,h,p}` | 56x22 | | 03 s3, s6 |
| `hud.coordsplate` | 56x22 | sunken gradient | 03 s3 |
| `icon.scroll` `icon.party` `icon.pvp` | 26x16 | black halo | 03 s3, s6 |
| `hud.ministat` `.off .on` | 16x11 | | 03 s6 |
| `exp.frame` | 207x10 | 3-slice | 03 s9 |
| `exp.trough` | 199x6 | 3-slice | 03 s9 |
| `exp.fill` | 8x5 | tile x | 03 s9 |
| `exp.tick` / `exp.cup` | 1x6 / 12x12 | | 03 s9 |
| `qwe.slot.q` / `.w` / `.e` `{n,h,p}` | 33x28 / 29x28 / 28x28 | | 03 s10 |
| `qwe.notch` | 18x14 | | 03 s10 |
| `qwe.letter.q/w/e` | 7x10 | outlined gold | 03 s10 |
| `skill.bracket` | 32x36 | | 03 s11 |
| `skill.tile` `{n,h,d}` | 19x28 | | 03 s11 |
| `skill.icon.default` | 17x26 | engraved | 03 s11 |
| `iconbtn.plinth` | 101x30 | | 03 s12 |
| `iconbtn.frame` `{n,h,p}` | 28x28 | pressed: black L top/left | 03 s12 |
| `icon.banner` `icon.lens` `icon.satchel` | 22x22 | | 03 s12 |
| `chat.rock` | 64x64 | tile xy; opaque | 03 s8 |
| `chat.post.left/right` | 5x44 | tile y | 03 s8 |
| `chat.rail` | 64x4 | tile x | 03 s8 |
| `chat.input.frame` | 253x17 | 3-slice | 03 s8 |
| `chat.field.main` `chat.field.whisper` | 8x12 | 3-slice | 03 s8 |
| `chat.btn` `{n,h,p}` | 14x14 | gold curl glyph | 03 s8 |
| `chat.scroll.up/down` `{n,h,p}` | 14x9 | hollow arrows | 03 s8 |
| `chat.scroll.thumb` / `.track` | 14x10 / 14x8 | track tile y | 03 s8 |
| `chat.bracket` | 3x3 | | 03 s8 |
| `chat.postbar` | 8x8 | 3-slice, `#9D7A2B` | 03 s8 |

Gem layers share a pivot. `full` and `empty` (dimmed 30%, desaturated) are colour. `mask` is greyscale in RGB: value = fill threshold, 1 at bottom to 0 at top, inside the silhouette. `spec` is the rim arc drawn above the cut. Facets are newly authored polygons, flat-shaded, never radial. Use 03 s5 for the idea (cut gem, specular table rim, palette) but not its coordinates: no shape may be rescaled from screenshot pixels.

`dream/ui/hud/cursors.png`: `cursor.pointer .pressed .grab .attack .forbid`, 32x32 u. Own steel gauntlet design, tip upper-left, ~30 u long. Pivot = fingertip hotspot. Spec 01 s4.

### C. `dream/ui/items/` and `dream/ui/fx/`

Sprite = footprint + 6 u margin per side. `pivot` = footprint top-left (24,24 px). `meta.footprint=[cols,rows]`. Fit art to ~75% of footprint, tilt weapons top-left to bottom-right, TL light, no outline. Spec 02 s6-7. Icons should read as pre-rendered 3D snapshots: use heightfields with strong metal spec.

| Footprint | u | Sprites |
|---|---|---|
| 1x1 | 32x32 | `item.relic.disc` `item.ring.ice` `item.gem.amber` `item.pendant.ward` |
| 1x2 | 32x52 | `item.feather.azure` `item.tool.survey-lantern` `item.gear.ward-lantern` `item.tool.torch` |
| 2x2 | 52x52 | `item.gear.leather-cap` `item.gear.leather-vest` `item.armor.bone` `item.helm.gilded` `item.armor.gilded` `item.legs.verdant` `item.gloves.plate` `item.boots.plate` `item.pet.wisp` |
| 2x3 | 52x72 | `item.tool.builder-hammer` `item.tool.stone-maul` `item.tool.field-blade` `item.gear.iron-blade` `item.axe.twinbit` `item.shield.kite` |
| 2x4 | 52x92 | `item.bow.verdant` `item.spear.crimson` |
| 3x2 | 72x52 | `item.wings.gossamer` (veined alpha) `item.cape.navy` (3 u top overflow) |

Verify the `tool.*` and `gear.*` names against `dream/config/content/items.json`; every key must have a sprite. Footprints are art-only; the grid is 1x1 today. Set `meta.pivotOverflow` only if needed.

Tiers share one layout: `items.json` with `meta.variants` listing the PNGs, `items.png` (Common), `items_uncommon.png` (light tint), `items_rare.png` (frozen orange cycle key `axe.cycle` with rims), `items_enhanced.png` (gold, whitened, capped at 80%), `items_mask.png` (`kind:"mask"`; R metal, G reflect coord, B rim, A emissive), `tier_ramps.png/json` (256x16 per row: rare cycle, enhanced, chrome), and `fx.aura.blob` in `items.png` (30x30 u, soft radial).

Combat text in `dream/ui/fx/`: `dmg_digits.png/json` holds `dmg.0`-`dmg.9`, `dmg.plus`, `dmg.comma`, 12x16 texels, `scale:1`, extrude 1. Bitmaps as strings in code. R fill, G rim + 1-texel down-right shadow, B 0, A = R+G. `meta.advance=11`. `dmg_digits_x4.png/json` is the same at 48x64 by box upscale, plus `dmg.miss` (128x44) and `dmg.miss.M/i/s`: thin polygons, ~3 px-equivalent stroke, R fill, G soft shadow. Spec 05 Glyph plan.

## Checks

Plain Python exiting nonzero: `tools/ui-assets/tests/check_<name>.py`, using `uiart.validate_atlas` and `uiart.determinism_errors`.

All: exact sprite sizes; schema, bounds, extrusion, no fringe, seams, names (via `validate_atlas`); generators free of `ImageFont`, `urllib`, `requests`, `reference/`, `.webp`, `dream/fonts`; token colours within ±6 by `probe` unless noted.

Window: `cell.empty` face `#404143`, `cell.occupied` `#232425`, `cell.hover` `#1C2030`; slab mean `#444347` ±8 with blue ≥ red; well interior `#141415`; `currency.field` interior `#232323`; `tip.panel` alpha 204 ±2 RGB 0; `tip.band` opaque `#AD0D01`; `cell.overlay.hover` over `#232425` gives `#1C2030` ±4; 3x3 `cell.empty` has no seam; silhouettes near-neutral (|R-B| ≤ 12); `cell.empty` right-top bevel brighter than left-bottom.

HUD: gem `full` includes `#EC260C`, `#7C1001`, `#EEC6AD` ±10; `full`, `empty`, `mask` have identical alpha; `mask` monotonic with y; `gem.mp` silhouette is `gem.hp` mirrored (IoU ≥ .98); gauge stripe right of centre in both; `hud.dragon.right` mirrors left (IoU ≥ .98); `chat.rock` mean near `#15120B`; `hud.base.*` opaque in the band; widths sum to 640 u; cursor pivot A > 128 within 2 px, upper-left quadrant; dragon eye near `#BDA884` ±12, remainder low saturation.

Items and fx: all names exist including all `items.json` keys; footprint matches; sprite = footprint + 12 u; art bbox ≤ footprint + 6 u and ≥ 45% of footprint on the long axis; variants same size and identical alpha; enhanced has ≤ 20% of opaque px within 8 of white. Digits: exact cells; R and G never overlap; B = 0; each distinct; stem 3 texels at mid-height (except `1`, `7`). x4 equals box upscale exactly. Tinted fill px equal `dmg.*.fill` exactly.

## Previews

Each workstream writes previews to `tools/ui-assets/previews/`: Window assembles Inventory and Combine at 01's rects; HUD assembles the 640x48 bar with dragons at 1920 wide; Items shows icons on `#232425` at 2.25x and digits in every type colour. Reviewed by eye for style against the screenshots, never pixel-diffed, and committed.
