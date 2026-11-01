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

`tools/ui-assets/uiart.py` is committed and read-only for workstreams; read it first. Request changes through the coordinator. `tokens.json` holds the spec palette, with `zen.*` renamed `currency.*`, plus the `forge.*` colours that `forge.py` reads (rust, dust, ivory, gold, bare steel).

Each `gen_*.py` exposes `main(out_root: Path) -> None`, writes only its own directories and imports only `uiart`, `forge`, `gemtrace`, numpy, Pillow and the standard library (`concurrent` and `os` for the gem tracer's process pool); never `ImageFont`. `pivot` and `slice` are px. In `slice`, values are multiples of 4. State suffixes: `.normal .hover .pressed .disabled`. Hover = face +10%, pressed = swap bevels, disabled = 50% desaturated.

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
| `hud.dragon.left/right` | 118x58 | pivot bottom outer corner; bottom 6 u over the plate | 03 s2, iteration 2 |
| `hud.talon.left/right` | 71x52 | three talons bite into the gem; draws over it | 03 s7, iteration 2 |
| `gem.setting.left/right` | 62x56 | forged collar under the stone | iteration 2 |
| `gem.hp.spill` `gem.mp.spill` | 128x64 | additive light, centred on the gem | iteration 2 |
| `hudnum.0` to `hudnum.9` `hudnum.slash` | 8x12 | bold ivory bar numerals | iteration 2 |
| `hud.base.left/right` | 199x48 | iron, rail | 03 s1 |
| `hud.base.centre` | 242x48 | chain band | 03 s1, s10 |
| `hud.gapfill` | 64x48 | tile x | 03 Responsive |
| `gem.hp.full/empty/mask/spec` | 50x50 | see below | 03 s5 |
| `gem.mp.full/empty/mask/spec` | 50x50 | mirrored cut, sapphire | 03 s6 |
| `gem.setting.left/right` | 62x56 | forged collar under the stone | iteration 2 |
| `gem.hp.spill` `gem.mp.spill` | 128x64 | additive light, centred on the gem | iteration 2 |
| `hudnum.0/1/2/3/4/5/6/7/8/9/slash` | 8x12 | bold ivory bar numerals | iteration 2 |
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

Gem layers share a pivot. Iteration 2 supersedes the old greyscale mask: see "Gem layers" in the Iteration 2 section. The cut is original and ray traced (`gemtrace.py`), never radial and never rescaled from screenshot pixels.

`dream/ui/hud/cursors.png`: `cursor.pointer .pressed .grab .attack .forbid`, 32x32 u. Own steel gauntlet design, tip upper-left, ~30 u long. Pivot = fingertip hotspot. Spec 01 s4.

### C. `dream/ui/items/` and `dream/ui/fx/`

Sprite = footprint + 6 u margin per side. `pivot` = footprint top-left (24,24 px). `meta.footprint=[cols,rows]`. Fit art to ~75% of footprint, tilt weapons top-left to bottom-right, TL light, no outline. Spec 02 s6-7. Icons should read as pre-rendered 3D snapshots: use heightfields with strong metal spec.

| Footprint | u | Sprites |
|---|---|---|
| 1x1 | 32x32 | `item.relic.disc` `item.gear.iron-band` `item.gem.amber` `item.gear.ward-pendant` |
| 1x2 | 32x52 | `item.feather.azure` `item.tool.survey-lantern` `item.gear.ward-lantern` `item.tool.torch` |
| 2x2 | 52x52 | `item.gear.leather-cap` `item.gear.leather-vest` `item.armor.bone` `item.helm.gilded` `item.armor.gilded` `item.gear.plated-leggings` `item.gear.iron-gauntlets` `item.gear.iron-boots` `item.gear.ember-wisp` |
| 2x3 | 52x72 | `item.tool.builder-hammer` `item.tool.stone-maul` `item.tool.field-blade` `item.gear.iron-blade` `item.axe.twinbit` `item.shield.kite` |
| 2x4 | 52x92 | `item.bow.verdant` `item.gear.hunting-spear` |
| 3x2 | 72x52 | `item.gear.gossamer-wings` (veined alpha) `item.gear.travel-cloak` (3 u top overflow) |

Verify the `tool.*` and `gear.*` names against `dream/config/content/items.json`; every key must have a sprite. Footprints are art-only; the grid is 1x1 today. Set `meta.pivotOverflow` only if needed.

Tiers share one layout: `items.json` with `meta.variants` listing the PNGs, `items.png` (Common), `items_uncommon.png` (light tint), `items_rare.png` (frozen orange cycle key `axe.cycle` with rims), `items_mask.png` (`kind:"mask"`; R metal, G reflect coord, B rim, A emissive), `tier_ramps.png/json` (256x16 per row: rare cycle, enhanced, chrome), and `fx.aura.blob` in `items.png` (30x30 u, soft radial).

Combat text in `dream/ui/fx/`: `dmg_digits.png/json` holds `dmg.0`-`dmg.9`, `dmg.plus`, `dmg.comma`, 12x16 texels, `scale:1`, extrude 1. Bitmaps as strings in code. R fill, G rim + 1-texel down-right shadow, B 0, A = R+G. `meta.advance=11`. `dmg_digits_x4.png/json` is the same at 48x64 by box upscale, plus `dmg.miss` (128x44) and `dmg.miss.M/i/s`: thin polygons, ~3 px-equivalent stroke, R fill, G soft shadow. Spec 05 Glyph plan.

## Checks

Plain Python exiting nonzero: `tools/ui-assets/tests/check_<name>.py`, using `uiart.validate_atlas` and `uiart.determinism_errors`.

All: exact sprite sizes; schema, bounds, extrusion, no fringe, seams, names (via `validate_atlas`); generators free of `ImageFont`, `urllib`, `requests`, `reference/`, `.webp`, `dream/fonts`; token colours within ±6 by `probe` unless noted.

Window: `cell.empty` face `#404143`, `cell.occupied` `#232425`, `cell.hover` `#1C2030`; slab mean `#444347` ±8 with blue ≥ red; well interior `#141415`; `currency.field` interior `#232323`; `tip.panel` alpha 204 ±2 RGB 0; `tip.band` opaque `#AD0D01`; `cell.overlay.hover` over `#232425` gives `#1C2030` ±4; 3x3 `cell.empty` has no seam; silhouettes near-neutral (|R-B| ≤ 12); `cell.empty` right-top bevel brighter than left-bottom.

HUD: gem `full` includes `#EC260C`, `#7C1001`, `#EEC6AD` ±10; `full`, `empty`, `mask` have identical alpha; `mask` monotonic with y; `gem.mp` silhouette is `gem.hp` mirrored (IoU ≥ .98); gauge stripe right of centre in both; `hud.dragon.right` mirrors left (IoU ≥ .98); `chat.rock` mean near `#15120B`; `hud.base.*` opaque in the band; widths sum to 640 u; cursor pivot A > 128 within 2 px, upper-left quadrant; dragon eye near `#BDA884` ±12, remainder low saturation.

Items and fx: all names exist including all `items.json` keys; footprint matches; sprite = footprint + 12 u; art bbox ≤ footprint + 6 u and ≥ 45% of footprint on the long axis; variants same size and identical alpha; enhanced has ≤ 20% of opaque px within 8 of white. Digits: exact cells; R and G never overlap; B = 0; each distinct; stem 3 texels at mid-height (except `1`, `7`). x4 equals box upscale exactly. Tinted fill px equal `dmg.*.fill` exactly.

## Previews

Each workstream writes previews to `tools/ui-assets/previews/`: Window assembles Inventory and Combine at 01's rects; HUD assembles the 640x48 bar with dragons at 1920 wide; Items shows icons on `#232425` at 2.25x and digits in every type colour. Reviewed by eye for style against the screenshots, never pixel-diffed, and committed.

## Iteration 2 (classic HUD): final names and sizes

Pinned by the C0b art core. Everything here is written by the generators through `forge.py`; `tests/check_art.py` holds the value gates. `tests/check_manifest.py` parses only the Manifest tables above, so the HUD rows below move into table B when WS4 lands the sprites (then the hud count in `check_manifest.py` goes from 80 to 95).

### Renderer

`tools/ui-assets/forge.py`: linear-light GGX with Schlick Fresnel, baked horizon AO and key shadow, edge wear driven by the convexity of the primary form, soot, rust and dust, a filmic finish with bloom, and a premultiplied linear-light Mitchell downsample. Hi-res stays 16 px/u, atlases 4 px/u. Call chain: `Relief.render` (or `bake`, `age`, `light`, `finish`), then `downsample`. `Relief.sprite_banded` renders canvases above about 8 Mpx in bands. Sprites that tile pass `tile="x"`, `"y"` or `"xy"` to `Relief` and `wrap=` to `downsample`.

### HUD sprites that change

| Sprite | Was u | Now u | Notes |
|---|---|---|---|
| `hud.dragon.left/right` | 107x46 | 118x58 | pivot stays the bottom outer corner; the bottom 6 u overlap the plate; layout rect 0,-52,118,58 |
| `hud.talon.left/right` | 59x46 | 71x52 | three talons bite into the gem; layout rect 128,-3,71,52 (right mirrored) |
| `gem.hp.*` `gem.mp.*` | 50x50 | 50x50 | names and sizes unchanged, channels redefined below |

### HUD sprites added

| Sprite | u | Notes |
|---|---|---|
| `gem.setting.left/right` | 62x56 | dark forged bezel under the gem; suggested rect 92,-5,62,56 (right mirrored) |
| `gem.hp.spill` `gem.mp.spill` | 128x64 | additive gem light on the iron and talons, alpha scales with fill |
| `hudnum.0` to `hudnum.9` `hudnum.slash` | 8x12 | bold ivory bar numerals, one advance for all |

### Gem layers

`gem.<k>.full` is the stone filled to the top, tonemapped. `gem.<k>.empty` is the same geometry as dark glass with residue. `gem.<k>.mask` (raw RGBA8, straight alpha, byte/255 is the value, not sRGB; WS3 runtime contract): R is the fill threshold measured from the bottom, 0 at the lowest point of the stone and 1 at the highest, bent by refraction (a pixel is filled when `smoothstep(R - .012, R + .012, level)` is 1, so low values fill first; R spans about 0.02 to 0.98 and rises broadly upward, with no stray islands at 25, 50 and 75 percent), G the meniscus weight 0..1, B the per-facet surface phase (one cycle), A the silhouette (the shader does not read it; coverage is mixed from empty.a and full.a, so all four layers share one silhouette). R, G and B are dilated past the rim, never 0 there. This is the reverse of the old grey mask, so the new masks and both `gem.setting.*` sprites ship in one commit: the batch reads masks as the old gradient until both settings exist. `gem.<k>.spec` carries hot facet glints only. All four share one alpha. The old rule that `mask` is grey and constant along each row no longer holds; WS4 rewrites those checks in `check_hud.py`. This section supersedes the paragraph on gem layers in table B.

### Items

Nine sprites were renamed to their content keys, art unchanged: `item.gloves.plate` to `item.gear.iron-gauntlets`, `item.boots.plate` to `item.gear.iron-boots`, `item.legs.verdant` to `item.gear.plated-leggings`, `item.pendant.ward` to `item.gear.ward-pendant`, `item.ring.ice` to `item.gear.iron-band`, `item.cape.navy` to `item.gear.travel-cloak`, `item.wings.gossamer` to `item.gear.gossamer-wings`, `item.pet.wisp` to `item.gear.ember-wisp`, `item.spear.crimson` to `item.gear.hunting-spear`. Footprints and sizes are as in table C.

### Art gates

`tools/ui-assets/tests/check_art.py` measures our own generated art at 2.25 px/u against constants (no outside image is read): bar mean .16 to .24, median .08 to .14, p95 at least .72, p99 at least .92, share over .85 between 2 and 5 percent, saturation at least .26, 4u over 0.5u band at least 1.1; dragon 4u band at least .04 and ratio at least 1.5; HP gem saturation at least .78 and p95 at least .75; MP gem mean at least .22 and saturation at least .78; window slab std at least .14 and 4u band at least .025; frame high frequency at most .11 and p99 at most .60; empty cell high frequency at most .055. The default run proves the chain on a forged sample plate; `--art` measures `dream/ui`.
