# Work log: the gothic shop board and the reliquary HUD

- Purpose: record the vendor window becoming a grid shop that mirrors the bag, and the art pass that took the
  remaining casual UI art to the forged standard.
- Status: done, on `integrate/gothic-shop` from `master` 14f5a1d8 via the contract commit 8d2a56e5. Not pushed,
  not merged into `master`.
- Plan: `roadmap_ideas/gothic-shop-overhaul.md`.
- Workstreams: `ws/shop-panels`, `ws/shop-audio`, `ws/shop-art`, plus `fix/shop-review`.

## What landed

- `VendorWindow` is an 8x8 grid board at `InventoryWindow.BagLeftUnits/BagTopUnits` (15, 200 u) and the bag's
  20 u pitch, so the shop and the bag line up cell for cell when the windows sit side by side. Stock is placed
  by real item footprints through `BagGrid.FirstFit`. The text buy list, the paged sell list, the pagers and the
  Sell button are gone.
- New `VendorGrid` (pure geometry and drop resolution) and `VendorFxState` (ember glow and buy flash, clocked off
  `HudView.AnimationSeconds`).
- Click-to-buy from a shop cell; drag-to-sell from the bag onto the board. The board's border ignites
  (`shop.frame.ember`) on a valid sell-hover and reads cold (`shop.frame.cold`) on a refused one, and the drag
  ghost takes a warm ember tint rather than the red "drops on ground" tint.
- `UiFeedback` gained `Coin` and `Refuse`; `IUiInput.Emit` carries them, with a default no-op so existing test
  fakes keep compiling. `SoundId.VendorCoin` / `VendorRefuse` are synthesised by `tools/audio/gen_sfx.py`.
- Art: item icons, bag cells, HP/MP gems and the flat glyphs were reforged through `forge.py`; five new window
  sprites (`shop.cell.empty`, `shop.cell.occupied`, `shop.frame.ember`, `shop.frame.cold`, `cell.overlay.ember`).
- **No protocol change.** Drag-to-sell reuses `VendorSellIntent` unchanged, so the protocol stays at v22.

## Findings worth keeping

- **The vendor stock cap had to stop counting and start packing.** `ContentCatalog.MaxNpcStock` was 8 because the
  old window drew eight fixed row plates. On a grid the binding constraint is placement: five 4x4 wares pass a cap
  of 8, four of them fill the 64-cell board exactly, and the fifth is placed nowhere and silently unbuyable -
  BH-CONTENT-1 reopened. The validator now runs the window's own first-fit over each vendor's stock at load.
  Cell count is not the test; packing is. Five 3x4 wares (60 of 64 cells) also fail, because an 8-wide board fits
  two 3-wide wares per band and wastes the last two columns.
- **Two gates measure "how lit is the fluid" in different units, and they were conflated.** `check_art.py`'s
  `glass_p95` is sRGB luma; `check_hud.py`'s is the max channel. On a blood-red stone the max channel is about
  2.7x the luma (0.651 against 0.236), so a number from one read against a bound from the other looks like a pass
  when it is a fail. Both files now say which quantity they hold.
- **Neither of those is the legibility contract.** `ClassicHudGemTests.RealArt_TheLiquidRisesWithTheFill` counts
  the pixels that differ between fill 0 and fill 1 - the pixels that actually show the player the level. The first
  art pass darkened the mana stone until that count fell to 1517 against a floor of 2000: an empty orb and a full
  orb looked the same. After relighting the fluid it measures 3082 (mana) and 2810 (health). When a gauge's art
  changes, that test is the one that matters, not the value gates.
- **A whole-sprite statistic stops meaning what it meant once the sprite gains a second material.** The gems'
  saturation gates "failed" partly because the new iron cage is 25-30% of the covered pixels and is near-neutral,
  so the mean fell without the fluid changing at all. The gem gates now mask to the glass. Measure the material
  the gate is about, not the sprite.
- **Widget ids in the shop fold in the item kind** (`HudWidgetId.Of(IdShopCell, VendorGrid.KindIdentity(kind))`),
  so a stock change between press and release drops the click instead of redirecting it. Separately, a duplicate
  stock kind would have bought twice from one click, because two lines share an id and `UiInputRouter.Button`
  answers by id without consulting the rect; the buy loop breaks after the first buy. The window had been safe
  only because two distant callers happened to filter duplicates.
- **The sell path's "you sell what you grabbed" guarantee rests entirely on `InventoryUiState.BeginFrame`**
  setting `DragCancelled` when the snapshot arrays change identity. That is now covered by a test that pushes a
  changed snapshot mid-drag.
- `check_sources.py` still reports the same two pre-existing failures at `enginelook.py:525` and `:640`. Any third
  is new. Confirmed against clean `master`, not assumed.

## Verification

- `dotnet build dream/dream.csproj`: 0 errors (17 pre-existing warnings).
- `dotnet test dream.Tests/dream.Tests.csproj`: 9459 passed, 0 failed, 47 skipped.
- `check_window.py`, `check_items.py`, `check_hud.py`: 0 errors.
- `check_art.py --art` - **the FULL set, not the `--only` subset**: 0 errors. This is the first time the
  whole set has been green. `--only bar,hp,mp,dragon` was the command in the plan and in every brief,
  and it skips `slab`, `frame` and `cell`; two real regressions hid behind it (`cell.empty` hf 0.0272 ->
  0.0699 against a 0.055 ceiling, `win.side.left` hf 0.1061 -> 0.1121 against 0.11). Both were fixed in
  the art, not by moving the gate. The `slab` std floor was corrected to its long-standing measurement
  (0.1352 on master and on head against a 0.14 floor) so the full set can actually be run - a
  permanently red gate is what produced the `--only` habit in the first place. **Run the full `--art`.**
- Every long-running step ran under a wall-clock watchdog that kills stray `testhost` on a stall; no step stalled.
- The duplicate-buy guard was confirmed by mutation: removing the `break` from the buy loop fails its test.

## The gem legibility number, across four art passes

`ClassicHudGemTests` counts the pixels that differ between fill 0 and fill 1, floor 2000. Measured by
raising the floor to print the real count, never estimated:

| pass | mana | health | what changed |
| --- | --- | --- | --- |
| 1 | 1517 FAIL | 2810 | the stones were darkened to soot; mana stopped reading as a gauge |
| 2 | 3082 | 2810 | the fluid was relit |
| 3 | 2566 | 2273 | heavier cage and a deeper rim vignette ate the margin back |
| 4 | 3025 | 3050 | rim vignette eased; the cage mass kept |

The lesson is in pass 3 to 4: the count is an AREA measure, not a brightness one. Pass 3 raised the
base gain and the count still fell, because a bigger cage and a deeper vignette leave fewer pixels
free to change at all. Cage pixels never change; heavily vignetted rim pixels barely do. The empty
glass was already near-black (glass mean 0.029 / 0.033), so there was nothing to take off that side.
Easing the vignette recovered it without touching the cage.

## Not done

- Selling straight out of an equipment well. It needs a new intent and therefore a protocol bump; such a drag
  reads `Invalid` and refuses.
- Raising `MaxNpcStock` above 8. The board could hold more, but 16 wares at up to 2x3 could need 96 of 64 cells,
  so it is a packing question that needs proving, not a number to raise.
