using Dream.Rendering.Hud;
using Dream.Simulation.Inventory;
using OpenTK.Mathematics;

namespace Dream.Rendering.Ui.Panels;

// The inventory window (spec 01, rects from sections 3.8 and 3.9 and the art generator's composition):
// stone frame, twelve equipment wells above an 8x8 bag of 20 u cells. A bag item covers a block of
// cells (its footprint) from its top-left anchor cell and is hovered, dragged and menu'd from any of
// them. Left click does nothing. A right click opens the item menu (drawn by ItemOverlays) and a drag
// emits exactly one InventoryHudAction when it is dropped on a valid target. Hidden because they are
// not built: currency, repair, store, set-option tab, arrow, other wells.
public static class InventoryWindow
{
    public const int BagColumns = BagGrid.Columns;
    public const int BagRows = BagGrid.Rows;
    public const float CellUnits = 20f;
    public const float BagLeftUnits = 15f;
    public const float BagTopUnits = 200f;
    public const float ArtPixelsPerUnit = 4f;
    public const float BodyTextPx = 13f;

    // The free band under the bag where the held-back line goes.
    public const float HeldBackLeftUnits = 15f;
    public const float HeldBackTopUnits = 364f;

    // The wallet line under it (M3).
    public const float GoldTopUnits = 376f;
    private static readonly UiColor GoldColor = new(0xD8, 0xB4, 0x5A, 255);

    // Text sizes in window units (1 u = Scale px), in the style of CharacterWindow.AttributeTextUnits.
    // Every box in this window comes from the ART, so each size is capped by the box it goes in and the
    // ones over a cell are cut or shrunk by its WIDTH as well: a fixed 13 px shrank relative to its own
    // box as ui_scale rose, and a size taken from the scale alone overflows the box the art fixed.
    internal const float TitleTextUnits = 7.5f;
    internal const float TitleStripUnits = 8f;
    internal const float RowTextUnits = 9.5f;
    internal const float RowUnits = 12f;
    internal const float CellTextUnits = 8f;
    internal const float BandTextUnits = 8f;
    internal const float BandUnits = 8f;

    // The 8 u title strip's engraved capitals.
    internal static UiSizedText TitleTextFor(in UiLayout l) =>
        UiText.For(in l, TitleTextUnits, TitleStripUnits, UiTextFace.Title);

    // A 12 u line box's text: the size every stone window gives a row.
    internal static UiSizedText RowTextFor(in UiLayout l) => UiText.For(in l, RowTextUnits, RowUnits);

    // The body line height on this canvas (doubled when the glyphs are). Only the overlays still lay
    // out from it: their boxes follow the text height, so UiLayout.TextScale already sizes them.
    internal static float BodyHeight(IUiCanvas c) => BodyTextPx * c.TextScale;

    // Widget ids. The router keys on the int only, so a block far from small ids avoids clashes.
    // A bag item is IdBagSlot + its anchor (0..63); a well is IdWell + its slot (0..11).
    internal const int IdBase = 0x00B0_0000;
    internal const int IdBagSlot = IdBase + 0x100;
    internal const int IdWell = IdBase + 0x200;
    internal const int IdCloseInventory = IdBase + 0x300;
    internal const int IdCloseCharacter = IdBase + 0x301;
    internal const int IdMenuBackdrop = IdBase + 0x400;
    internal const int IdMenuEntry = IdBase + 0x410;
    internal const int IdConfirmBackdrop = IdBase + 0x500;
    internal const int IdConfirmDestroy = IdBase + 0x501;
    internal const int IdConfirmCancel = IdBase + 0x502;

    private readonly record struct WellDef(
        EquipmentSlot Slot,
        string Well,
        string Silhouette,
        float X,
        float Y,
        float W,
        float H);

    // The twelve wells in equipment order; the index is the HudView.Worn index (spec 01 section 3.8).
    private static readonly WellDef[] Wells =
    [
        new(EquipmentSlot.MainHand, UiSpriteNames.Well2x3, UiSpriteNames.SilWeaponLeft, 14f, 89f, 40f, 60f),
        new(EquipmentSlot.OffHand, UiSpriteNames.Well2x3, UiSpriteNames.SilWeaponRight, 134f, 89f, 40f, 60f),
        new(EquipmentSlot.Head, UiSpriteNames.Well2x2, UiSpriteNames.SilHelm, 74f, 46f, 40f, 40f),
        new(EquipmentSlot.Body, UiSpriteNames.Well2x3, UiSpriteNames.SilArmor, 74f, 89f, 40f, 60f),
        new(EquipmentSlot.Hands, UiSpriteNames.Well2x2, UiSpriteNames.SilGloves, 14f, 152f, 40f, 40f),
        new(EquipmentSlot.Feet, UiSpriteNames.Well2x2, UiSpriteNames.SilBoots, 134f, 152f, 40f, 40f),
        new(EquipmentSlot.Legs, UiSpriteNames.Well2x2, UiSpriteNames.SilPants, 74f, 152f, 40f, 40f),
        new(EquipmentSlot.Neck, UiSpriteNames.Well1x1, UiSpriteNames.SilPendant, 54f, 89f, 20f, 20f),
        new(EquipmentSlot.RingLeft, UiSpriteNames.Well1x1, UiSpriteNames.SilRing, 54f, 152f, 20f, 20f),
        new(EquipmentSlot.RingRight, UiSpriteNames.Well1x1, UiSpriteNames.SilRing, 114f, 152f, 20f, 20f),
        new(EquipmentSlot.Back, UiSpriteNames.Well3x2, UiSpriteNames.SilWings, 114f, 46f, 60f, 40f),
        new(EquipmentSlot.Companion, UiSpriteNames.Well2x2, UiSpriteNames.SilPet, 14f, 46f, 40f, 40f)
    ];

    private static readonly UiColor White = UiColor.White;
    private static readonly UiColor DraggedTint = new(255, 255, 255, 90);

    public static void Build(IUiCanvas c, IUiInput i, HudView v, in UiLayout l, UiSpriteTable s, IHudActions a)
    {
        InventoryUiState st = InventoryUiState.Current;
        if (!v.InventoryOpen)
        {
            // Closed: touch the input only to finish a drag this window started.
            bool wasDragging = st.DragActive;
            st.ResetAll();
            if (wasDragging && i.ActiveDragPayload >= 0)
            {
                i.TryEndDrag(out _, out _);
            }

            return;
        }

        st.BeginFrame(v);
        VendorFxState.Current.BeginFrame(v);
        bool live = !st.ModalOpen;
        bool dragging = i.ActiveDragPayload >= 0;
        UiRect win = l.Inventory;

        // Draw order is by texture page, not by widget: all window art (frame, wells, cells, buttons),
        // then the item icons one page per tier, then all text. Nothing overlaps across those groups
        // except as listed (icons over cells, text over both), so z-order is kept and the batch
        // merges each group into one draw command.
        DrawFrame(c, s, l, win, inventoryKind: true);

        if (v.Inventory is null)
        {
            DrawCloseButton(c, i, l, s, a, win, IdCloseInventory, live);
            DrawTitle(c, l, win);
            UiText.PutCentered(
                c, l.FromUnits(win, 15f, 200f, 160f, 20f), "Waiting for inventory...",
                RowTextFor(in l), ItemTierStyle.Dim, UiText.ShadowPx(in l));
            return;
        }

        // Where a drag in progress would land and which cells it would cover, decided once a frame.
        DragPreview drag = PreviewDrag(i, v, l, win, st, dragging);

        for (int w = 0; w < Wells.Length; w++)
        {
            DrawWell(c, i, v, l, s, st, win, w, live, dragging, drag);
        }

        int hoveredAnchor = ReadBagInput(i, v, l, win, st, live, dragging);
        DrawBagCells(c, v, l, s, win, hoveredAnchor, drag);

        DrawCloseButton(c, i, l, s, a, win, IdCloseInventory, live);

        for (ItemTier tier = ItemTier.Common; tier <= ItemTier.Rare; tier++)
        {
            for (int w = 0; w < Wells.Length; w++)
            {
                DrawWellIcon(c, v, l, st, win, w, tier);
            }

            for (int anchor = 0; anchor < HudView.BackpackSlots; anchor++)
            {
                DrawBagIcon(c, v, l, st, win, anchor, tier);
            }
        }

        DrawTitle(c, l, win);
        for (int w = 0; w < Wells.Length; w++)
        {
            DrawWellLabel(c, v, l, win, w);
        }

        for (int anchor = 0; anchor < HudView.BackpackSlots; anchor++)
        {
            DrawBagText(c, v, l, win, anchor);
        }

        DrawHeldBackLine(c, v, l, win);
        DrawGoldLine(c, v, l, win);

        if (i.TryEndDrag(out int payload, out Vector2 dropPosition))
        {
            if (st.DragActive && !st.DragCancelled && payload == st.DragSource)
            {
                // One release, exactly one action: the shop is resolved first, and when it answers (Valid or
                // Invalid) the bag and the ground are not asked, so a drop never both sells and moves.
                DropVerdict shop = VendorGrid.TryResolveSell(in l, v, payload, dropPosition, out VendorHudAction sell);
                if (shop == DropVerdict.Valid)
                {
                    a.Vendor(sell);
                    i.Emit(UiFeedback.Coin);
                }
                else if (shop == DropVerdict.Invalid)
                {
                    i.Emit(UiFeedback.Refuse);
                }
                else if (TryResolveRelease(l, win, v, payload, dropPosition, out InventoryHudAction action) == DropVerdict.Valid)
                {
                    a.Inventory(action);
                }
            }

            st.ClearDrag();
        }
        else if (i.ActiveDragPayload < 0)
        {
            st.ClearDrag();
        }
    }

    // ---- geometry ---------------------------------------------------------------------------

    // The rect of bag cell n (0..63, row * 8 + col): also the rect of a 1x1 item anchored there.
    internal static UiRect SlotRect(in UiLayout l, UiRect win, int slot) =>
        l.FromUnits(win, BagLeftUnits + (slot % BagColumns * CellUnits), BagTopUnits + (slot / BagColumns * CellUnits), CellUnits, CellUnits);

    // The rect of an item anchored at anchor: its whole footprint.
    internal static UiRect ItemRect(in UiLayout l, UiRect win, int anchor, ItemFootprint footprint) =>
        l.FromUnits(
            win,
            BagLeftUnits + (anchor % BagColumns * CellUnits),
            BagTopUnits + (anchor / BagColumns * CellUnits),
            footprint.Cols * CellUnits,
            footprint.Rows * CellUnits);

    // The whole 8x8 board.
    internal static UiRect BagRect(in UiLayout l, UiRect win) =>
        l.FromUnits(win, BagLeftUnits, BagTopUnits, BagColumns * CellUnits, BagRows * CellUnits);

    internal static UiRect WellRect(in UiLayout l, UiRect win, int well)
    {
        WellDef d = Wells[well];
        return l.FromUnits(win, d.X, d.Y, d.W, d.H);
    }

    // The bag cell under p (0..63), or -1 off the board (or for a non-finite p).
    internal static int CellAt(in UiLayout l, UiRect win, Vector2 p)
    {
        UiRect board = BagRect(l, win);
        if (!board.Contains(p))
        {
            return -1;
        }

        float cell = CellUnits * l.Scale;
        int col = Math.Clamp((int)MathF.Floor((p.X - board.X) / cell), 0, BagColumns - 1);
        int row = Math.Clamp((int)MathF.Floor((p.Y - board.Y) / cell), 0, BagRows - 1);
        return (row * BagColumns) + col;
    }

    // The well under p (0..11, the HudView.Worn index), or -1.
    internal static int WellAt(in UiLayout l, UiRect win, Vector2 p)
    {
        for (int w = 0; w < Wells.Length; w++)
        {
            if (WellRect(l, win, w).Contains(p))
            {
                return w;
            }
        }

        return -1;
    }

    // The widget index under p: 0..63 a bag cell, 100..111 a well, -1 nothing. A cell is not an
    // anchor: the item that covers it is HudView.BagCells[cell].
    internal static int HitTest(in UiLayout l, UiRect win, Vector2 p)
    {
        int cell = CellAt(l, win, p);
        if (cell >= 0)
        {
            return cell;
        }

        int well = WellAt(l, win, p);
        return well >= 0 ? EquipmentSlots.FirstSlotIndex + well : -1;
    }

    // Where a dropped footprint lands (decision D12). The ghost is centred on the pointer, so its
    // top-left cell is the cell nearest the ghost's top-left corner, clamped so the whole footprint
    // stays on the board. False when the pointer is off the board (or the footprint is malformed).
    internal static bool TryDropAnchor(in UiLayout l, UiRect win, Vector2 p, ItemFootprint footprint, out int anchor)
    {
        anchor = -1;
        UiRect board = BagRect(l, win);
        if (!board.Contains(p) || !BagGrid.TryMask(0, footprint, out _))
        {
            return false;
        }

        float cell = CellUnits * l.Scale;
        int col = (int)MathF.Floor(((p.X - board.X) / cell) - (footprint.Cols * 0.5f) + 0.5f);
        int row = (int)MathF.Floor(((p.Y - board.Y) / cell) - (footprint.Rows * 0.5f) + 0.5f);
        col = Math.Clamp(col, 0, BagColumns - footprint.Cols);
        row = Math.Clamp(row, 0, BagRows - footprint.Rows);
        anchor = (row * BagColumns) + col;
        return true;
    }

    // The target widget index for a drop of the dragged source at p: a well (100..111) when p is over
    // one, otherwise the anchor the dragged footprint lands on, otherwise -1.
    internal static int ResolveTarget(in UiLayout l, UiRect win, HudView v, int source, Vector2 p)
    {
        int well = WellAt(l, win, p);
        if (well >= 0)
        {
            return EquipmentSlots.FirstSlotIndex + well;
        }

        return TryDragFootprint(v, source, out ItemFootprint footprint) && TryDropAnchor(l, win, p, footprint, out int anchor)
            ? anchor
            : -1;
    }

    // What a release at p does: a well or bag anchor first (TryResolveDrop); with no in-window target,
    // a bag item released over the world is dropped on the ground (no confirm: it stays recoverable).
    // A worn item released into the world is Invalid (the server refuses it, as with Destroy and Sell).
    // Anything else, including a non-finite pointer, is None. The window is open here.
    internal static DropVerdict TryResolveRelease(
        in UiLayout l, UiRect win, HudView v, int source, Vector2 p, out InventoryHudAction action)
    {
        action = default;
        if (!float.IsFinite(p.X) || !float.IsFinite(p.Y))
        {
            return DropVerdict.None;
        }

        int target = ResolveTarget(l, win, v, source, p);
        if (target >= 0)
        {
            return TryResolveDrop(v, source, target, out action);
        }

        if (!ClassicHudHitTest.OverWorld(in l, inventoryOpen: true, p))
        {
            return DropVerdict.None;
        }

        if (EquipmentSlots.TryFromSlotIndex(source, out EquipmentSlot sourceSlot))
        {
            return TryItem(v.Worn, (int)sourceSlot, out _) ? DropVerdict.Invalid : DropVerdict.None;
        }

        if (!IsPlaced(v, source))
        {
            return DropVerdict.None;
        }

        // An item can hold a bag anchor and a worn row at once; the server refuses to drop it (ItemEquipped),
        // so no Drop is offered, as in CharacterPanelModel.BackpackMenu. The bag copy's Equipped flag is
        // false (it means "this row is an equipment slot"), so the worn rows are scanned for the id.
        long itemId = v.Backpack[source].Item.ItemInstanceId;
        if (v.Worn is { } worn)
        {
            for (int slot = 0; slot < worn.Length; slot++)
            {
                if (TryItem(worn, slot, out InventoryItemSnapshot held) && held.ItemInstanceId == itemId)
                {
                    return DropVerdict.Invalid;
                }
            }
        }

        action = new InventoryHudAction(InventoryHudActionKind.Drop, source, 0, itemId);
        return DropVerdict.Valid;
    }

    // Scale that fits an icon with the given footprint into a box of boxW x boxH units: the
    // footprint (20 u per cell) must fit, and small items are not enlarged.
    public static float FitScale(int footprintCols, int footprintRows, float boxWUnits, float boxHUnits)
    {
        float fw = Math.Max(1, footprintCols) * CellUnits;
        float fh = Math.Max(1, footprintRows) * CellUnits;
        return MathF.Min(1f, MathF.Min(boxWUnits / fw, boxHUnits / fh));
    }

    // The screen rect of an icon fitted into box and centred in it.
    public static UiRect IconRect(in UiSprite icon, UiRect box, float uiScale)
    {
        float scale = uiScale <= 0f ? 1f : uiScale;
        float fit = FitScale(icon.FootprintCols, icon.FootprintRows, box.W / scale, box.H / scale);
        float w = icon.W / ArtPixelsPerUnit * fit * scale;
        float h = icon.H / ArtPixelsPerUnit * fit * scale;
        return new UiRect(box.X + ((box.W - w) * 0.5f), box.Y + ((box.H - h) * 0.5f), w, h);
    }

    // ---- bag items --------------------------------------------------------------------------

    // True when an item is anchored at anchor and the board really gives it that spot (a hand-built
    // view could hold an item the cell map does not know).
    internal static bool IsPlaced(HudView v, int anchor) =>
        (uint)anchor < BagGrid.CellCount
        && anchor < v.BagCells.Length
        && anchor < v.BagFootprints.Length
        && v.BagCells[anchor] == anchor
        && TryItem(v.Backpack, anchor, out _);

    // The footprint of the dragged item: a bag item's from the board, a worn item's from its kind.
    internal static bool TryDragFootprint(HudView v, int source, out ItemFootprint footprint)
    {
        footprint = ItemFootprint.One;
        if (EquipmentSlots.TryFromSlotIndex(source, out EquipmentSlot slot))
        {
            if (!TryItem(v.Worn, (int)slot, out InventoryItemSnapshot worn))
            {
                return false;
            }

            footprint = v.FootprintOf(worn.ItemKind);
            return true;
        }

        if (!IsPlaced(v, source))
        {
            return false;
        }

        footprint = v.BagFootprints[source];
        return true;
    }

    // ---- drop rules -------------------------------------------------------------------------

    internal enum DropVerdict : byte
    {
        // Not a target: same spot, empty space, or nothing is dragged. No highlight.
        None,
        Valid,
        Invalid
    }

    // source and target are widget indices: 0..63 a bag anchor, 100..111 a well, -1 nothing. Valid fills
    // action; None and Invalid emit nothing.
    //
    // A bag to bag drop is BagGrid.PlanMove, the rule the server uses, so a free spot and a swap with
    // exactly one other item are Valid and everything else is Invalid. A bag item dropped on an
    // occupied well swaps, and shows Invalid when the displaced item has nowhere to go. A worn item
    // dropped on a bag anchor is unequipped there, which needs its whole footprint free; dropped on
    // another well it moves there if its slot mask allows it. When that well is taken the two swap,
    // which also needs the other item's mask to allow the source well (the server's rule).
    internal static DropVerdict TryResolveDrop(HudView v, int source, int target, out InventoryHudAction action)
    {
        action = default;
        if (target < 0 || source == target)
        {
            return DropVerdict.None;
        }

        bool targetIsWell = EquipmentSlots.TryFromSlotIndex(target, out EquipmentSlot targetSlot);
        if (!targetIsWell && target >= BagGrid.CellCount)
        {
            return DropVerdict.None;
        }

        if (EquipmentSlots.TryFromSlotIndex(source, out EquipmentSlot sourceSlot))
        {
            if (!TryItem(v.Worn, (int)sourceSlot, out InventoryItemSnapshot worn))
            {
                return DropVerdict.None;
            }

            if (targetIsWell)
            {
                if (!CharacterPanelModel.CanEquipTo(worn.EquipSlots, targetSlot))
                {
                    return DropVerdict.Invalid;
                }

                if (TryItem(v.Worn, (int)targetSlot, out InventoryItemSnapshot other))
                {
                    if (other.ItemInstanceId == worn.ItemInstanceId)
                    {
                        return DropVerdict.None;
                    }

                    if (!CharacterPanelModel.CanEquipTo(other.EquipSlots, sourceSlot))
                    {
                        return DropVerdict.Invalid;
                    }
                }

                action = new InventoryHudAction(InventoryHudActionKind.EquipTo, source, target);
                return DropVerdict.Valid;
            }

            if (!BagGrid.TryMask(target, v.FootprintOf(worn.ItemKind), out ulong landing) || (v.BagOccupancy() & landing) != 0)
            {
                return DropVerdict.Invalid;
            }

            action = new InventoryHudAction(InventoryHudActionKind.Unequip, source, target);
            return DropVerdict.Valid;
        }

        if (!IsPlaced(v, source))
        {
            return DropVerdict.None;
        }

        InventoryItemSnapshot item = v.Backpack[source].Item;
        if (!targetIsWell)
        {
            BagMovePlan plan = BagGrid.PlanMove(v.BagCells, v.BagFootprints, source, target);
            if (plan.Verdict == BagMoveVerdict.Same)
            {
                return DropVerdict.None;
            }

            if (plan.Verdict == BagMoveVerdict.Blocked)
            {
                return DropVerdict.Invalid;
            }

            action = new InventoryHudAction(InventoryHudActionKind.Move, source, target);
            return DropVerdict.Valid;
        }

        if (!CharacterPanelModel.CanEquipTo(item.EquipSlots, targetSlot))
        {
            return DropVerdict.Invalid;
        }

        if (TryItem(v.Worn, (int)targetSlot, out InventoryItemSnapshot occupant))
        {
            if (occupant.ItemInstanceId == item.ItemInstanceId)
            {
                return DropVerdict.None;
            }

            if (!CanTakeDisplaced(v, source, occupant))
            {
                return DropVerdict.Invalid;
            }
        }

        action = new InventoryHudAction(InventoryHudActionKind.EquipTo, source, target);
        return DropVerdict.Valid;
    }

    // Equipping over a worn item sends that item back to the bag: to the equipped item's old anchor if
    // it fits there, otherwise the first fit, with the equipped item's cells already free.
    private static bool CanTakeDisplaced(HudView v, int sourceAnchor, in InventoryItemSnapshot displaced)
    {
        ItemFootprint footprint = v.FootprintOf(displaced.ItemKind);
        BagGrid.TryMask(sourceAnchor, v.BagFootprints[sourceAnchor], out ulong sourceCells);
        ulong occupied = v.BagOccupancy() & ~sourceCells;
        if (BagGrid.TryMask(sourceAnchor, footprint, out ulong atSource) && (occupied & atSource) == 0)
        {
            return true;
        }

        return BagGrid.FirstFit(occupied, footprint) >= 0;
    }

    internal static bool TryItem(HudItemView[]? items, int index, out InventoryItemSnapshot item)
    {
        if (items is not null && (uint)index < (uint)items.Length && items[index].Item.ItemInstanceId != 0)
        {
            item = items[index].Item;
            return true;
        }

        item = default;
        return false;
    }

    // What dropping the dragged item where the pointer is would do, for the highlight. Cells is the
    // bag cells it would cover (bit = cell) when the target is a bag anchor.
    private readonly record struct DragPreview(int Target, DropVerdict Verdict, ulong Cells);

    private static DragPreview PreviewDrag(
        IUiInput i, HudView v, in UiLayout l, UiRect win, InventoryUiState st, bool dragging)
    {
        DragPreview none = new(-1, DropVerdict.None, 0);
        if (!dragging || !st.DragActive || st.DragCancelled)
        {
            return none;
        }

        // The same decision a release makes, so the cue never disagrees with it. Over the world (no
        // in-window target) a valid release drops the item on the ground; that is NOT a cell cue, because
        // the hover overlay means "lands in this cell". It is shown on the ghost instead (ItemOverlays
        // draws it in DropGhostTint). Only a refused world release (a worn item) keeps the invalid
        // overlay on the item's own well.
        int target = ResolveTarget(l, win, v, st.DragSource, i.Pointer.Position);
        DropVerdict verdict = TryResolveRelease(l, win, v, st.DragSource, i.Pointer.Position, out _);
        if (verdict == DropVerdict.None || (target < 0 && verdict == DropVerdict.Valid))
        {
            return none;
        }

        if (target < 0)
        {
            target = st.DragSource;
        }

        ulong cells = 0;
        if ((uint)target < BagGrid.CellCount && TryDragFootprint(v, st.DragSource, out ItemFootprint footprint))
        {
            BagGrid.TryMask(target, footprint, out cells);
        }

        return new DragPreview(target, verdict, cells);
    }

    // ---- drawing ----------------------------------------------------------------------------

    private static void DrawWell(
        IUiCanvas c, IUiInput i, HudView v, in UiLayout l, UiSpriteTable s, InventoryUiState st,
        UiRect win, int w, bool live, bool dragging, DragPreview drag)
    {
        WellDef d = Wells[w];
        UiRect r = l.FromUnits(win, d.X, d.Y, d.W, d.H);
        int index = EquipmentSlots.FirstSlotIndex + w;
        if (s.TryGet(d.Well, out UiSprite well))
        {
            c.Sprite(well, r, White);
        }

        bool has = TryItem(v.Worn, w, out _);
        if (!has && s.TryGet(d.Silhouette, out UiSprite sil))
        {
            c.Sprite(sil, r, White);
        }

        if (has && live)
        {
            if (!dragging && i.Hovered(IdWell + w, r))
            {
                st.SetHover(index, r);
            }

            if (!dragging && i.BeginDrag(IdWell + w, r, index))
            {
                st.StartDrag(index, v);
            }

            if (!dragging && i.RightClicked(IdWell + w, r))
            {
                st.OpenMenu(v, index, r, i.Pointer.Position);
            }
        }

        if (drag.Target == index && drag.Verdict != DropVerdict.None)
        {
            string name = drag.Verdict == DropVerdict.Valid ? UiSpriteNames.CellOverlayHover : UiSpriteNames.CellOverlayInvalid;
            if (s.TryGet(name, out UiSprite overlay))
            {
                c.Tile(overlay, r, White);
            }
        }
    }

    private static void DrawWellIcon(IUiCanvas c, HudView v, in UiLayout l, InventoryUiState st, UiRect win, int w, ItemTier tier)
    {
        if (!TryItem(v.Worn, w, out _))
        {
            return;
        }

        HudItemView hv = v.Worn[w];
        if (!hv.HasIcon || hv.Tier != tier)
        {
            return;
        }

        UiRect r = WellRect(l, win, w);
        UiColor tint = st.IsDraggingSource(EquipmentSlots.FirstSlotIndex + w) ? DraggedTint : White;
        c.Sprite(ItemTierStyle.IconFor(hv.Icon, hv.Tier), IconRect(hv.Icon, r, l.Scale), tint);
    }

    // An item with no sprite shows the start of its name, so it is never invisible.
    private static void DrawWellLabel(IUiCanvas c, HudView v, in UiLayout l, UiRect win, int w)
    {
        if (TryItem(v.Worn, w, out _) && !v.Worn[w].HasIcon)
        {
            DrawFallbackLabel(c, l, WellRect(l, win, w), v.Worn[w].Item.DisplayName);
        }
    }

    // One widget per placed item, over the item's whole footprint, so hover, drag and right click work
    // from any cell it covers. Returns the anchor of the hovered item (the router names at most one), or
    // -1. Nothing is registered while a menu is open or an item is being dragged.
    private static int ReadBagInput(
        IUiInput i, HudView v, in UiLayout l, UiRect win, InventoryUiState st, bool live, bool dragging)
    {
        if (!live || dragging)
        {
            return -1;
        }

        int hovered = -1;
        for (int anchor = 0; anchor < HudView.BackpackSlots; anchor++)
        {
            if (!IsPlaced(v, anchor))
            {
                continue;
            }

            UiRect r = ItemRect(l, win, anchor, v.BagFootprints[anchor]);
            if (i.Hovered(IdBagSlot + anchor, r))
            {
                hovered = anchor;
                st.SetHover(anchor, r);
            }

            if (i.BeginDrag(IdBagSlot + anchor, r, anchor))
            {
                st.StartDrag(anchor, v);
            }

            if (i.RightClicked(IdBagSlot + anchor, r))
            {
                st.OpenMenu(v, anchor, r, i.Pointer.Position);
            }
        }

        return hovered;
    }

    // The 64 cells: an empty tile, or the occupied backing of the item covering it (the hover backing on
    // every cell of the hovered item). While an item is dragged, the cells it would cover get the valid
    // or invalid overlay.
    private static void DrawBagCells(
        IUiCanvas c, HudView v, in UiLayout l, UiSpriteTable s, UiRect win, int hoveredAnchor, DragPreview drag)
    {
        string overlayName = drag.Verdict == DropVerdict.Valid ? UiSpriteNames.CellOverlayHover : UiSpriteNames.CellOverlayInvalid;
        bool haveEmpty = s.TryGet(UiSpriteNames.CellEmpty, out UiSprite empty);
        bool haveOccupied = s.TryGet(UiSpriteNames.CellOccupied, out UiSprite occupied);
        bool haveHover = s.TryGet(UiSpriteNames.CellHover, out UiSprite hover);
        UiSprite overlay = default;
        bool haveOverlay = drag.Cells != 0 && s.TryGet(overlayName, out overlay);

        // The buy flash: the cells of the item that just arrived from the shop glow ember and fade.
        VendorFxState fx = VendorFxState.Current;
        float flash = fx.FlashLevel(v.AnimationSeconds);
        ulong flashCells = 0;
        UiSprite emberOverlay = default;
        bool haveFlash = flash > 0f
            && fx.FlashAnchor >= 0
            && IsPlaced(v, fx.FlashAnchor)
            && BagGrid.TryMask(fx.FlashAnchor, v.BagFootprints[fx.FlashAnchor], out flashCells)
            && s.TryGet(UiSpriteNames.CellOverlayEmber, out emberOverlay);
        UiColor flashTint = new(255, 255, 255, (byte)Math.Clamp((int)MathF.Round(255f * flash), 0, 255));

        for (int cell = 0; cell < BagGrid.CellCount; cell++)
        {
            UiRect r = SlotRect(l, win, cell);
            int owner = v.BagAnchorAt(cell);
            if (owner < 0 || !IsPlaced(v, owner))
            {
                if (haveEmpty)
                {
                    c.Sprite(empty, r, White);
                }
            }
            else if (owner == hoveredAnchor)
            {
                if (haveHover)
                {
                    c.Tile(hover, r, White);
                }
            }
            else if (haveOccupied)
            {
                c.Tile(occupied, r, White);
            }

            if (haveOverlay && (drag.Cells & (1UL << cell)) != 0)
            {
                c.Tile(overlay, r, White);
            }

            if (haveFlash && (flashCells & (1UL << cell)) != 0)
            {
                c.Tile(emberOverlay, r, flashTint);
            }
        }
    }

    private static void DrawBagIcon(IUiCanvas c, HudView v, in UiLayout l, InventoryUiState st, UiRect win, int anchor, ItemTier tier)
    {
        if (!IsPlaced(v, anchor))
        {
            return;
        }

        HudItemView hv = v.Backpack[anchor];
        if (!hv.HasIcon || hv.Tier != tier)
        {
            return;
        }

        UiColor tint = st.IsDraggingSource(anchor) ? DraggedTint : White;
        UiRect box = ItemRect(l, win, anchor, v.BagFootprints[anchor]);
        c.Sprite(ItemTierStyle.IconFor(hv.Icon, hv.Tier), IconRect(hv.Icon, box, l.Scale), tint);
    }

    // Name stub for an item without a sprite, and the stack count, both over the icon.
    private static void DrawBagText(IUiCanvas c, HudView v, in UiLayout l, UiRect win, int anchor)
    {
        if (!IsPlaced(v, anchor))
        {
            return;
        }

        InventoryItemSnapshot item = v.Backpack[anchor].Item;
        UiRect r = ItemRect(l, win, anchor, v.BagFootprints[anchor]);
        if (!v.Backpack[anchor].HasIcon)
        {
            DrawFallbackLabel(c, l, r, item.DisplayName);
        }

        if (item.StackCount > 1)
        {
            Span<char> mem = stackalloc char[12];
            UiTextBuilder b = new(mem);
            b.Append("x");
            b.Append(item.StackCount);
            ReadOnlySpan<char> text = b.Text;
            // Over a cell, so the count is shrunk to the cell's WIDTH as well as its height: "x12" at
            // the 13 px bake is already wider than a 20 u cell at scale 1.
            float pad = 2f * l.Scale;
            var t = UiText.ForFitting(c, in l, text, CellTextUnits, CellUnits, MathF.Max(0f, r.W - (2f * pad)));
            float width = UiText.Measure(c, text, in t);
            UiText.Put(
                c, text, new Vector2(r.Right - width - pad, r.Bottom - t.Px - l.Scale), in t,
                ItemTierStyle.Body, UiText.ShadowPx(in l));
        }
    }

    // "1 item held back: make room" or "N items held back: make room" in the free band under the bag;
    // nothing at 0. The server holds back items it has no room for and places them as space appears.
    private static void DrawHeldBackLine(IUiCanvas c, HudView v, in UiLayout l, UiRect win)
    {
        if (v.HeldBackCount <= 0)
        {
            return;
        }

        Span<char> mem = stackalloc char[48];
        UiTextBuilder b = new(mem);
        b.Append(v.HeldBackCount);
        b.Append(CharacterPanelModel.HeldBackSuffix(v.HeldBackCount));
        UiRect band = l.FromUnits(win, HeldBackLeftUnits, HeldBackTopUnits, BagColumns * CellUnits, BandUnits);
        UiText.PutLeft(c, band, 0f, b.Text, BandTextFor(in l), ItemTierStyle.Danger, UiText.ShadowPx(in l));
    }

    // "Gold 120" in the band under the held-back line: the wallet of the snapshot (M3).
    private static void DrawGoldLine(IUiCanvas c, HudView v, in UiLayout l, UiRect win)
    {
        long gold = v.Inventory?.Gold ?? 0;
        Span<char> mem = stackalloc char[32];
        UiTextBuilder b = new(mem);
        b.Append("Gold ");
        b.Append((int)Math.Clamp(gold, 0L, int.MaxValue));
        UiRect band = l.FromUnits(win, HeldBackLeftUnits, GoldTopUnits, BagColumns * CellUnits, BandUnits);
        UiText.PutLeft(c, band, 0f, b.Text, BandTextFor(in l), GoldColor, UiText.ShadowPx(in l));
    }

    // An item with no sprite shows the start of its name, so it is never invisible. Cut by WIDTH, not
    // to three characters: three glyphs at 8 u need not fit a 20 u cell, and four often do.
    private static void DrawFallbackLabel(IUiCanvas c, in UiLayout l, UiRect r, string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        ReadOnlySpan<char> text = name.AsSpan(0, Math.Min(name.Length, 24));
        var t = UiText.For(in l, CellTextUnits, CellUnits);
        float room = MathF.Max(0f, r.W - (2f * l.Scale));
        UiText.PutCentered(c, r, text[..UiText.FitLength(c, text, in t, room)], in t, ItemTierStyle.Body, UiText.ShadowPx(in l));
    }

    internal static void DrawCloseButton(
        IUiCanvas c, IUiInput i, in UiLayout l, UiSpriteTable s, IHudActions a, UiRect win, int id, bool live)
    {
        UiRect r = l.FromUnits(win, 19f, 395f, 24f, 24f);
        bool hovered = live && i.Hovered(id, r);
        if (s.TryGet(hovered ? UiSpriteNames.ButtonHover : UiSpriteNames.ButtonNormal, out UiSprite plate))
        {
            c.Sprite(plate, r, White);
        }

        if (s.TryGet(hovered ? UiSpriteNames.GlyphCloseHover : UiSpriteNames.GlyphCloseNormal, out UiSprite glyph))
        {
            c.Sprite(glyph, l.FromUnits(win, 22f, 398f, 18f, 18f), White);
        }

        if (live && i.Button(id, r))
        {
            a.CloseInventory();
        }
    }

    // The stone frame shared by the two windows: slab, tiled sides, crown, bottom ornament and
    // the title strips. The inventory has a scroll rod under the title; the character window
    // (the combine art) has a second strip.
    internal static void DrawFrame(IUiCanvas c, UiSpriteTable s, in UiLayout l, UiRect win, bool inventoryKind)
    {
        float crownH = inventoryKind ? 41f : 40f;
        if (s.TryGet(inventoryKind ? UiSpriteNames.WinSlabInventory : UiSpriteNames.WinSlabCombine, out UiSprite slab))
        {
            c.Sprite(slab, l.FromUnits(win, 12f, crownH, 165f, inventoryKind ? 387f : 388f), White);
        }

        if (s.TryGet(UiSpriteNames.WinSideLeft, out UiSprite left))
        {
            c.Tile(left, l.FromUnits(win, 0f, crownH, 12f, 416f - crownH), White);
        }

        if (s.TryGet(UiSpriteNames.WinSideRight, out UiSprite right))
        {
            c.Tile(right, l.FromUnits(win, 177f, crownH, 13f, 416f - crownH), White);
        }

        if (s.TryGet(inventoryKind ? UiSpriteNames.WinCrownInventory : UiSpriteNames.WinCrownCombine, out UiSprite crown))
        {
            c.Sprite(crown, l.FromUnits(win, 0f, 0f, 190f, crownH), White);
        }

        if (s.TryGet(UiSpriteNames.WinBottom, out UiSprite bottom))
        {
            c.Sprite(bottom, l.FromUnits(win, 0f, 416f, 190f, 16f), White);
        }

        if (!s.TryGet(UiSpriteNames.WinStrip, out UiSprite strip))
        {
            return;
        }

        if (inventoryKind)
        {
            c.NineSlice(strip, l.FromUnits(win, 34f, 12f, 120f, 8f), White);
            if (s.TryGet(UiSpriteNames.WinScrollRod, out UiSprite rod))
            {
                c.Sprite(rod, l.FromUnits(win, 16f, 22f, 158f, 10f), White);
            }
        }
        else
        {
            c.NineSlice(strip, l.FromUnits(win, 35f, 8f, 120f, 8f), White);
            c.NineSlice(strip, l.FromUnits(win, 35f, 20f, 119f, 8f), White);
        }
    }

    // "Inventory" in the 8 u title strip, in the engraved face at the size the strip allows.
    private static void DrawTitle(IUiCanvas c, in UiLayout l, UiRect win) =>
        UiText.PutCentered(
            c, l.FromUnits(win, 34f, 12f, 120f, TitleStripUnits), "Inventory",
            TitleTextFor(in l), ItemTierStyle.Title, UiText.ShadowPx(in l));

    // The 8 u bands under the bag: the held-back warning and the wallet.
    private static UiSizedText BandTextFor(in UiLayout l) => UiText.For(in l, BandTextUnits, BandUnits);

    // ---- text helpers shared by the windows -------------------------------------------------
    //
    // These draw at whatever size the canvas is already set to (UiLayout.TextScale), so they are for
    // the overlays, whose boxes follow the text height. A panel whose boxes come from the art asks
    // UiText for a size capped by the box instead.

    internal static void DrawText(IUiCanvas c, ReadOnlySpan<char> text, Vector2 pos, UiColor color) =>
        UiText.Put(c, text, pos, UiText.AtCanvasScale(c), color);

    // Centred in r. Text wider than r is cut to fit instead of clipped, so it stays in the same draw
    // command as every other line of text.
    internal static void DrawTextCentered(IUiCanvas c, UiRect r, ReadOnlySpan<char> text, UiColor color) =>
        UiText.PutCentered(c, r, text, UiText.AtCanvasScale(c), color);
}
