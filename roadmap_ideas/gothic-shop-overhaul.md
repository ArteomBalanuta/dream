# Gothic shop overhaul - implementation plan

**Goal.** Turn the vendor window into a grid shop that mirrors the bag exactly (drag-to-sell, click-to-buy, forged iron/stone art, tactile audio and ember feedback), and raise the remaining casual art (item icons, bag cells, HP/MP gems, flat glyphs) to the forged standard.

Base branch: `master` (protocol v22). Three workstreams with disjoint files, plus a lead-owned contract commit *before* the fork and a wiring commit *after* the merges.

---

## 0. Frozen contracts

### 0.1 Protocol: no change, no version bump

`VendorSellIntent(NpcActorId, SlotIndex, ItemInstanceId)` and `VendorBuyIntent(NpcActorId, ItemKind)` are reused **unchanged**. Drag-to-sell produces the existing `VendorHudAction(VendorHudActionKind.Sell, SlotIndex: bagAnchor, ItemInstanceId: id)`, which `PlayScene.Economy` already turns into a `VendorSellIntent`. **Nothing is added to the protocol, so it stays v22 and no spelling chase is needed.** Any design needing a new intent (selling straight out of an equipment well) is out of scope - see 0.5.

### 0.2 Lead's pre-fork contract commit (on `master`, before any workstream starts)

Small and mechanical, ~15 lines. It exists so all three branches compile independently.

1. `dream/Rendering/Ui/UiInputRouter.cs` - `public enum UiFeedback { Hover, Click, Coin, Refuse }` and on the router `public void Emit(UiFeedback feedback) => Notify(feedback);`
2. `dream/Rendering/Ui/IUiInput.cs` - `void Emit(UiFeedback feedback) { }` as a default interface member, so existing test fakes keep compiling (the `IHudActions` pattern).
3. `dream/Client/Audio/SoundId.cs` - add `VendorCoin`, `VendorRefuse` at the END of the enum, update `SoundIds.Count` to the new last member, and map both to `AudioBus.Ui` in `BusOf`. File stems become `vendor_coin.wav` and `vendor_refuse.wav`.
4. `dream/Client/Audio/GameAudio.cs` - `PlayUiFeedback` maps `Coin -> SoundId.VendorCoin`, `Refuse -> SoundId.VendorRefuse`, else the existing click/hover.
5. `dream/Rendering/Hud/VendorHudState.cs` - `public readonly record struct VendorStockLine(string ItemKind, string DisplayName, long Price, ItemTier Tier = ItemTier.Common);` (add `using Dream.Rendering.Ui;`).
6. `dream/Client/Scenes/PlayScene.Economy.cs` - fill `Tier` in `ApplyVendorOpened` from the resolved catalog rarity through `HudItemCatalog.TierOf`; `ItemTier.Common` when unknown.

After this commit those six files are **frozen**: no workstream may touch them.

### 0.3 New public C# surface (owned by WS-B)

New file `dream/Rendering/Ui/Panels/VendorGrid.cs`:

    public static class VendorGrid
    {
        public const float LeftUnits = 15f;    // same x as the bag
        public const float TopUnits  = 60f;    // under the title and gold strips
        public const int   Columns   = BagGrid.Columns;              // 8
        public const int   Rows      = BagGrid.Rows;                 // 8
        public const float CellUnits = InventoryWindow.CellUnits;    // 20

        public static UiRect BoardRect(in UiLayout l, UiRect win);
        public static UiRect CellRect(in UiLayout l, UiRect win, int cell);
        public static UiRect ItemRect(in UiLayout l, UiRect win, int anchor, ItemFootprint f);

        // Deterministic stock placement: BagGrid.FirstFit in stock order, so the shop obeys the same
        // footprint rules as the bag. anchors[i] is stock line i's anchor, or -1 when it does not fit.
        // Allocation-free; the caller passes stackalloc spans of length VendorHudState.MaxStock.
        public static int Place(HudView v, VendorHudState vendor, Span<int> anchors, Span<ItemFootprint> footprints);

        // Stable FNV-1a identity of an item kind, for HudWidgetId.Of (BH-INPUT-3).
        public static long KindIdentity(string? itemKind);

        // Vendor open AND p inside BoardRect(l, l.Character).
        public static bool OverShop(in UiLayout l, HudView v, Vector2 p);

        // What releasing the dragged source over the shop does. Valid fills action (Kind is always Sell).
        public static InventoryWindow.DropVerdict TryResolveSell(
            in UiLayout l, HudView v, int source, Vector2 p, out VendorHudAction action);
    }

`TryResolveSell` rules, frozen:

- vendor not open, or p not over the board, or p non-finite -> `None`
- source is an equipment well (100..111) -> `Invalid` (reads as a cold refusal; the protocol takes a bag slot only)
- source is a placed bag anchor whose item is ALSO worn -> `Invalid` (the rule the ground-drop path already uses)
- source is a placed bag anchor -> `Valid`, `action = new VendorHudAction(VendorHudActionKind.Sell, null, source, itemInstanceId)`
- anything else -> `None`

New file `dream/Rendering/Ui/Panels/VendorFxState.cs`, thread-static like `InventoryUiState`:

    public sealed class VendorFxState
    {
        public static VendorFxState Current { get; }
        public static void Reset();
        public const float FlashSeconds = 0.45f;
        public const float EmberRiseSeconds = 0.12f;

        internal void BeginFrame(HudView v);                   // resolves a pending buy into FlashAnchor
        internal void NoteBuy(string? itemKind, HudView v);    // records the kind and BagOccupancy()
        internal void SetSellHover(InventoryWindow.DropVerdict verdict, float animationSeconds);
        public int FlashAnchor { get; }                        // -1 when nothing flashes
        public float FlashLevel(float animationSeconds);       // 1 -> 0 over FlashSeconds
        public float EmberLevel { get; }                       // 0..1, eased glow of the shop frame
    }

Buy-flash resolution: `NoteBuy` stores the kind and `v.BagOccupancy()`. On the first later frame where occupancy gains cells, `FlashAnchor = v.BagAnchorAt(lowest newly occupied cell)`; the pending buy is dropped after 3 s if nothing arrives. The clock is `HudView.AnimationSeconds`, already set once a frame by `ClassicHudSurface`. No new time source and no allocation.

### 0.4 New sprite names (frozen strings; WS-A makes them, WS-B consumes them)

Added to `UiSpriteNames.cs` as consts by WS-B, and **NOT added to `Required`** by any workstream (see Risks):

| const | string | page | what it is |
|---|---|---|---|
| `ShopCellEmpty` | `shop.cell.empty` | window | 20x20 recessed iron grate / carved stone niche, visibly distinct from `cell.empty` |
| `ShopCellOccupied` | `shop.cell.occupied` | window | 20x20, tile `xy` |
| `ShopFrameEmber` | `shop.frame.ember` | window | 9-slice border glow over the board edge while a valid sell hovers |
| `ShopFrameCold` | `shop.frame.cold` | window | 9-slice, the cold refused state |
| `CellOverlayEmber` | `cell.overlay.ember` | window | 20x20 tile `xy`, the buy flash on the receiving bag cell |

Panels draw each through `s.TryGet(...)` and skip it when absent, so WS-B ships green before WS-A lands.

### 0.5 Out of scope

- Selling from an equipment well (needs a protocol change; such a drag reads `Invalid`, cold frame, `UiFeedback.Refuse`).
- Buying more than one unit at a time, stock quantities, and any server-side change.

### 0.6 Widget id bands (hand-partitioned, do not collide)

Existing: inventory `IdBase+0x100..0x502`, character window `0x310..`, vendor `0x600..0x643`.
Removed with the text lists: `IdSellRow (0x620)`, `IdPagePrev/Next (0x640/0x641)`, `IdSellButton (0x642)`.
New: `IdShopCell = InventoryWindow.IdBase + 0x700`, but a shop cell's id is **`HudWidgetId.Of(IdShopCell, VendorGrid.KindIdentity(line.ItemKind))`**, never `IdShopCell + index` - BH-INPUT-3: a stock renumbering mid-press must drop the click, not redirect it. `IdBuyRow (0x600)` and `IdClose (0x643)` stay as they are.

---

## 1. Workstreams

### WS-A `ws/shop-art` - forged art and atlases

Owns, and only these:

- `tools/ui-assets/gen_window.py`
- `tools/ui-assets/gen_items.py`
- `tools/ui-assets/gen_hud.py`
- `tools/ui-assets/tests/check_window.py`
- `tools/ui-assets/tests/check_items.py`
- `tools/ui-assets/tests/check_hud.py`
- `dream/ui/window/*`, `dream/ui/items/*`, `dream/ui/hud/*`
- `tools/ui-assets/previews/*`

**Read `tools/ui-assets/README.md` first.** Every new sprite goes through the `forge.py` chain (Relief -> bake -> age -> light with GOTHIC_RIG -> finish -> downsample) with the existing material vocabulary (`iron`, `iron.warm`, `iron.cold`, `iron.black`, `iron.rivet`, `steel`, `stone`, `rust`, `void`). No flat fills, no smooth gradients, no painted sRGB.

Steps, in priority order. This is the art verdict from the coordinator's own review of `previews/window_inventory_1080.png` and `previews/hud_bar_1920.png` - do not substitute another list.

1. **Item icons** (`gen_items.py`, `dream/ui/items/*`) - the single biggest violation: bright saturated gold/blue/red/green, smooth gradients, clean edges, zero grime; mobile-game clipart inside a gothic frame. Desaturate and age them into the forge vocabulary - broken edge wear, cavity rust, soot, dust on up-facing slopes, hard silhouettes. Target a channel ceiling around 0.40 (repo rule: render colours are albedo and get multiplied up to ~2.17x with no tonemap). Keep the hue *directions* `check_items.check_palette` encodes (gilded leans gold, azure and travel-cloak lean blue, hunting-spear leans red, bone stays the palest of the set) but re-tune the absolute thresholds, especially `item.armor.bone`'s `c.min() > 120`, and ADD a saturation/value ceiling gate so the icons cannot drift bright again. Write the measured number beside every changed constant.
2. **Bag grid cells** (`gen_window.py`, `cell_relief` / `build_cells`) - `cell.empty` and `cell.occupied` are flat near-black squares with a thin bevel, far below the wells directly above them. Re-forge as recessed iron grates or carved stone niches with rivets and cavity rust, still quiet enough for icons to read over. `check_window.check_colours` gates face luma 0.05..0.24, the upper-left key and the 3x3 seam: satisfy those or re-tune them with measurements.
3. **HP/MP gems** (`gen_hud.py`) - they read as glossy candy: pure red and electric blue, a smooth plastic highlight, no visible cage. Rebuild as blood-filled glass in a sculpted iron cage: a forged claw ring over the orb, viscous non-uniform fluid, soot on the glass, the existing spill light kept. **Reuse the existing names** (`gem.hp.*`, `gem.mp.*`, `gem.setting.*`, `gem.*.spill`) so there is no C# change and no collision with WS-B.
4. **Flat glyphs** (`gen_window.py` glyph builders, `gen_hud.py` skill tiles) - the bottom button-row glyphs (the X) and the skill-tile glyphs (the magnifier) are flat saturated icons; same treatment at smaller scale, same names.
5. **New shop sprites** - the five names of 0.4 in the window atlas: `shop.cell.empty` and `shop.cell.occupied` (iron grate or stone niche, clearly a SHOP cell and clearly not the player's bag cell), `shop.frame.ember` and `shop.frame.cold` as 9-slice border overlays (ember = wrought iron heated to a glow in its cavities; cold = blue-grey refusal), and `cell.overlay.ember` as the buy flash. Extend `check_window.py` with a gate per sprite: size, slice, alpha, ember leaning warm, cold leaning blue, and the shop cell measurably different from `cell.empty`.
6. Regenerate the atlases and the previews, commit the bytes, and confirm `uiart.shipped_errors` is green for every touched gate.

**Leave alone** - already at the bar: the window chrome (slab, crown, sides), the equipment wells, the engraved `sil.*` reliefs, the dragon-head bar terminals, the base plates.

Acceptance and gates, from the venv:

    .venv/Scripts/python tools/ui-assets/build_all.py
    .venv/Scripts/python tools/ui-assets/tests/check_window.py
    .venv/Scripts/python tools/ui-assets/tests/check_items.py
    .venv/Scripts/python tools/ui-assets/tests/check_hud.py
    .venv/Scripts/python tools/ui-assets/tests/check_sources.py
    .venv/Scripts/python tools/ui-assets/tests/check_art.py --art --only bar,hp,mp,dragon

plus: committed bytes equal a fresh render (the shipped-bytes gate green for window, items and hud), previews regenerated, and the five new sprites present in `dream/ui/window/window.json`.

### WS-B `ws/shop-panels` - the grid shop, drag-to-sell, click-to-buy

Owns, and only these:

- `dream/Rendering/Ui/Panels/VendorWindow.cs` (rewritten)
- `dream/Rendering/Ui/Panels/InventoryWindow.cs`
- `dream/Rendering/Ui/Panels/ItemOverlays.cs`
- `dream/Rendering/Ui/Panels/VendorGrid.cs` (new)
- `dream/Rendering/Ui/Panels/VendorFxState.cs` (new)
- `dream/Rendering/Ui/UiSpriteNames.cs`
- `dream.Tests/Rendering/Ui/Panels/VendorWindowTests.cs`
- `dream.Tests/Rendering/Ui/Panels/InventoryWindowTests.cs`
- `dream.Tests/Rendering/Ui/Panels/ItemOverlaysTests.cs`
- `dream.Tests/Rendering/Ui/Panels/InventoryDrawBudgetTests.cs`
- `dream.Tests/Rendering/Ui/Panels/VendorGridTests.cs` (new)

Steps:

1. `VendorGrid.cs`: geometry, `Place` (stock order through `BagGrid.FirstFit`, footprints from `v.FootprintOf(line.ItemKind)`), `KindIdentity`, `OverShop`, `TryResolveSell`, exactly as in 0.3. Pure and allocation-free, so it is fully unit-testable; write `VendorGridTests.cs` beside it.
2. `VendorWindow.cs`: delete the buy list, the paged sell list, the pagers and the Sell button; keep `DrawFrame`, the title, the gold strip and the close button. Draw the 8x8 shop board at `VendorGrid.LeftUnits/TopUnits` with the bag's 20 u pitch: `shop.cell.empty` per cell, `shop.cell.occupied` tiled under each placed item, icons through `InventoryWindow.IconRect` and `ItemTierStyle.IconFor(icon, line.Tier)` (the icon from `s.TryGet(UiSpriteNames.Item(line.ItemKind), ...)`), the price small in the item's bottom-right like a stack count, and the name only as the no-icon fallback. Keep the two-pass order - window art, then icons grouped by tier, then text - so the batch stays merged.
3. Buy: `i.Button(HudWidgetId.Of(IdShopCell, VendorGrid.KindIdentity(kind)), itemRect)` -> `a.Vendor(new VendorHudAction(VendorHudActionKind.Buy, kind))`, plus `i.Emit(UiFeedback.Coin)` and `VendorFxState.Current.NoteBuy(kind, v)`. A price above the player's gold still draws in `ItemTierStyle.Danger` and must not buy.
4. Sell reaction, drawn by `VendorWindow` (it builds before `InventoryWindow`, so it reads the drag itself): while `i.ActiveDragPayload >= 0 && InventoryUiState.Current.DragActive && !DragCancelled`, call `VendorGrid.TryResolveSell` with `InventoryUiState.Current.DragSource` and the pointer. `Valid` draws `shop.frame.ember` over the board edge at `VendorFxState.EmberLevel` plus `cell.overlay.hover` on the hovered cells; `Invalid` draws `shop.frame.cold` plus `cell.overlay.invalid`. Feed `VendorFxState.SetSellHover` either way.
5. `InventoryWindow.Build`, in the `TryEndDrag` block, resolves the shop FIRST and emits exactly one action:

        DropVerdict sv = VendorGrid.TryResolveSell(in l, v, payload, dropPosition, out VendorHudAction sell);
        if (sv == DropVerdict.Valid) { a.Vendor(sell); i.Emit(UiFeedback.Coin); }
        else if (sv == DropVerdict.Invalid) { i.Emit(UiFeedback.Refuse); }
        else if (TryResolveRelease(l, win, v, payload, dropPosition, out InventoryHudAction action) == DropVerdict.Valid) { a.Inventory(action); }

   One release must emit exactly one action - never both a sell and a move or drop.
6. `InventoryWindow` draws the buy flash: when `VendorFxState.Current.FlashAnchor == anchor`, tile `cell.overlay.ember` over that anchor's cells at `FlashLevel(v.AnimationSeconds)` alpha. Call `VendorFxState.Current.BeginFrame(v)` next to `InventoryUiState.Current.BeginFrame(v)`.
7. `ItemOverlays.DrawGhost`: a release that would sell must not take the red "drops on ground" tint - give it its own warm ember tint, decided from `VendorGrid.TryResolveSell` before the existing `TryResolveRelease` call.
8. `UiSpriteNames.cs`: add the five consts of 0.4. **Do not touch `Required` or `PendingRequired`.**
9. Tests. Rewrite `VendorWindowTests` for the grid: stock placed by footprint, a click buys the right kind, a click on an empty cell buys nothing, a price above the wallet is refused, a stock change mid-press drops the click. Extend `InventoryWindowTests` with drag-to-sell valid / invalid / well-source / exactly-one-action. Add a draw-budget case **with the shop open**: the vendor's item pages sit between two window-page groups, so measure `batch.CommandCount`, record the number and add a `VendorBudget` constant rather than silently raising `WindowBudget`. Keep per-frame allocation at zero: no LINQ, no closures, `stackalloc` spans.

Acceptance and gates (from Git Bash prefix `MSYS_NO_PATHCONV=1`; inside a worktree build and test with the DEFAULT output path - do not redirect `BaseOutputPath` there):

    dotnet build dream/dream.csproj
    dotnet test dream.Tests/dream.Tests.csproj --filter "FullyQualifiedName~Rendering.Ui"
    dotnet test dream.Tests/dream.Tests.csproj          # full suite before handing back

Tee the suite output to a file and arm a stall watchdog beside it.

### WS-C `ws/shop-audio` - the coin clank and the refusal

Owns, and only these:

- `tools/audio/gen_sfx.py`
- `tools/audio/build_all.py`
- `dream/audio/sfx/vendor_coin.wav` (new)
- `dream/audio/sfx/vendor_refuse.wav` (new)

Steps:

1. In `gen_sfx.py` add `vendor_coin()` - a heavy struck-metal clank with a short coin-on-stone tail - and `vendor_refuse()` - a dull dead thud with no bright tail. Original synthesis only, through `synth.py` (`sine`, `sweep`, `bell`, `bandpass(noise)`, `decay`, `finalize`); no samples, no external audio. Both must be returned by `build()`, because `build_all.py` deletes any wav no `build()` returned.
2. Stay inside the existing guards: 16-bit PCM, peak <= 0.97, start and end near silence, fizz above 8 kHz at least 30 dB under the 40 Hz to 2 kHz body (that is the crackle that bit the pack before), folder total under the 12 MB budget. Do NOT add them to `PACK` - that tuple is the 30-sound gothic pack's own inventory.
3. Audition both at the HUD's UI bus level: the coin must sit under a hit, not over it.

Acceptance and gates:

    python -I tools/audio/build_all.py
    python -I tools/audio/build_all.py --check-only
    dotnet test dream.Tests/dream.Tests.csproj --filter "FullyQualifiedName~Client.Audio"

(The clip bank loads by file stem; a missing file is silent, not a crash.)

---

## 2. Merge order

`ws/shop-panels` -> `ws/shop-audio` -> `ws/shop-art`, into `integrate/gothic-shop`.

- **Panels first**: it carries the behavioural contract and the largest test delta, and it is written to degrade gracefully with no new sprites and no new wavs, so it is green on its own.
- **Audio second**: two binary files and one Python module, zero C# risk.
- **Art last**: the atlases are large binaries and the slowest to rebuild. Merging them last means the shipped-bytes gates run once against the final tree, and a red gate cannot be mistaken for a merge artefact.

No file is shared, so conflicts should be nil; a conflict means a workstream strayed outside its list.

## 3. Lead-owned wiring (not any workstream)

1. The pre-fork contract commit of 0.2.
2. After all three merges: add the five names of 0.4 to `UiSpriteNames.Required`, **only after confirming they exist in the merged `dream/ui/window/window.json`**, then run `ClassicHudIteration2ContractTests` and `UiSpriteTableTests`.
3. Run the full suite once on the integration branch (at most two full runs in a row; isolate flakes instead of re-running).
4. Launch the game and look: open a vendor, drag a bag item onto the shop (ember), drag a worn item onto it (cold), buy an item (clank plus a flash on the arriving cell), and judge the gems and icons against the references.
5. Do not push or merge into `master`.

## 4. Risks

- **`UiSpriteTable.Validate` and `Required`**: a name in `Required` that the shipped atlas lacks throws, and the whole classic HUD silently falls back to the ImGui HUD. Hence the rule above - panels use `TryGet`, and only the lead promotes names to `Required`, after the atlas is merged.
- **Shipped-bytes art gates**: a gate reads, a generator writes. Never "fix" a red gate by regenerating inside the gate. One gate red means stale art (re-run its generator and commit). Several red at once after a Pillow or numpy bump means the encoder moved, not the art: compare decoded pixels and do not regenerate.
- **Re-tuning art gates**: desaturating the item icons WILL trip `check_items.check_palette`, notably `item.armor.bone`'s `min > 120` floor. Re-tune with the measured value written beside the constant, keep the hue-direction assertions, and never delete a gate.
- **Widget-id collisions**: ids are hand-partitioned (0.6). The shop cell id must fold the item kind in, or a stock change between press and release redirects the click onto another item - the exact bug the old sell list was hardened against (BH-INPUT-3).
- **Double action on one release**: the shop resolve and the inventory resolve must be mutually exclusive. A bag-to-bag move that happens to land under the shop board must never also sell.
- **Draw-command budget**: the vendor's item pages between two window-page groups can add draw commands. Measure with the shop open, record the budget, and do not silently raise `WindowBudget`.
- **Allocation**: HUD panels are allocation-gated. `Place` and every per-frame path take `stackalloc` spans; no LINQ, no lambdas.
- **Worktree hygiene**: `git stash` is shared across all worktrees - banned in every workstream. Worktrees start from `master`, so reset them before starting, and clear `bin/obj` in inactive ones (stale worktrees have filled G: before).

## 5. Open questions

1. The board origin `TopUnits = 60` is read off the art's title and gold strips. If the second strip collides, move it to 70 u and keep the 8 rows - the close button sits at 395 u, so there is room.
2. Stock bigger than the board: the cap is `VendorHudState.MaxStock = 16`, which always fits, so `Place` returns -1 for an overflow line and the panel just does not draw it. No pager. Confirm that is acceptable.
3. Should selling a Rare item ask for confirmation? Currently no confirm, unlike Destroy - a sell is irreversible but the server pays for it. Flagged, not assumed.
