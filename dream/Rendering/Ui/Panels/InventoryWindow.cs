using Dream.Rendering.Hud;
using Dream.Simulation.Inventory;
using OpenTK.Mathematics;

namespace Dream.Rendering.Ui.Panels;

// The inventory window (spec 01, rects from section 3.8 and the art generator's composition):
// stone frame, four equipment wells, and a 4x4 bag where each slot is a 2x2 block of 20 u cells.
// Left click does nothing. A right click opens the item menu (drawn by ItemOverlays) and a drag
// emits exactly one InventoryHudAction when it is dropped on a valid target. Hidden because they
// are not built: currency, repair, store, set-option tab, arrow, other wells.
public static class InventoryWindow
{
    public const int BagColumns = 4;
    public const int BagRows = 4;
    public const float SlotUnits = 40f;
    public const float CellUnits = 20f;
    public const float ArtPixelsPerUnit = 4f;
    public const float BodyTextPx = 13f;

    // The body line height on this canvas (doubled when the glyphs are).
    internal static float BodyHeight(IUiCanvas c) => BodyTextPx * c.TextScale;

    // Widget ids. The router keys on the int only, so a block far from small ids avoids clashes.
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

    // Four real wells in equipment order; the index is the HudView.Worn index. The rest of the
    // paper doll (pet, wings, gloves, rings, ...) has no slot in the game yet and stays out.
    private static readonly WellDef[] Wells =
    [
        new(EquipmentSlot.MainHand, UiSpriteNames.Well2x3, UiSpriteNames.SilWeaponLeft, 14f, 89f, 40f, 60f),
        new(EquipmentSlot.OffHand, UiSpriteNames.Well2x3, UiSpriteNames.SilWeaponRight, 134f, 89f, 40f, 60f),
        new(EquipmentSlot.Head, UiSpriteNames.Well2x2, UiSpriteNames.SilHelm, 74f, 46f, 40f, 40f),
        new(EquipmentSlot.Body, UiSpriteNames.Well2x3, UiSpriteNames.SilArmor, 74f, 89f, 40f, 60f)
    ];

    private static readonly UiColor White = UiColor.White;
    private static readonly UiColor EquippedAliasTint = new(255, 255, 255, 140);   // 55%
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
            DrawTextCentered(c, l.FromUnits(win, 34f, 12f, 120f, 8f), "Inventory", ItemTierStyle.Title);
            DrawTextCentered(c, l.FromUnits(win, 15f, 200f, 160f, 20f), "Waiting for inventory...", ItemTierStyle.Dim);
            return;
        }

        for (int w = 0; w < Wells.Length; w++)
        {
            DrawWell(c, i, v, l, s, st, win, w, live, dragging);
        }

        for (int slot = 0; slot < HudView.BackpackSlots; slot++)
        {
            DrawBagCells(c, i, v, l, s, st, win, slot, live, dragging);
        }

        DrawCloseButton(c, i, l, s, a, win, IdCloseInventory, live);

        for (ItemTier tier = ItemTier.Common; tier <= ItemTier.Rare; tier++)
        {
            for (int w = 0; w < Wells.Length; w++)
            {
                DrawWellIcon(c, v, l, st, win, w, tier);
            }

            for (int slot = 0; slot < HudView.BackpackSlots; slot++)
            {
                DrawBagIcon(c, v, l, st, win, slot, tier);
            }
        }

        DrawTextCentered(c, l.FromUnits(win, 34f, 12f, 120f, 8f), "Inventory", ItemTierStyle.Title);
        for (int w = 0; w < Wells.Length; w++)
        {
            DrawWellLabel(c, v, l, win, w);
        }

        for (int slot = 0; slot < HudView.BackpackSlots; slot++)
        {
            DrawBagText(c, v, l, win, slot);
        }

        if (i.TryEndDrag(out int payload, out Vector2 dropPosition))
        {
            if (st.DragActive && !st.DragCancelled && payload == st.DragSource)
            {
                int target = HitTest(l, win, dropPosition);
                if (TryResolveDrop(v, payload, target, out InventoryHudAction action) == DropVerdict.Valid)
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

    internal static UiRect SlotRect(in UiLayout l, UiRect win, int slot) =>
        l.FromUnits(win, 15f + (slot % BagColumns * SlotUnits), 200f + (slot / BagColumns * SlotUnits), SlotUnits, SlotUnits);

    internal static UiRect WellRect(in UiLayout l, UiRect win, int well)
    {
        WellDef d = Wells[well];
        return l.FromUnits(win, d.X, d.Y, d.W, d.H);
    }

    // The widget index under p: 0..15 a bag slot, 100..103 a well, -1 nothing.
    internal static int HitTest(in UiLayout l, UiRect win, Vector2 p)
    {
        for (int slot = 0; slot < HudView.BackpackSlots; slot++)
        {
            if (SlotRect(l, win, slot).Contains(p))
            {
                return slot;
            }
        }

        for (int w = 0; w < Wells.Length; w++)
        {
            if (WellRect(l, win, w).Contains(p))
            {
                return EquipmentSlots.FirstSlotIndex + w;
            }
        }

        return -1;
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

    // ---- drop rules -------------------------------------------------------------------------

    internal enum DropVerdict : byte
    {
        // Not a target: same slot, empty space, or nothing is dragged. No highlight.
        None,
        Valid,
        Invalid
    }

    // source and target are widget indices: 0..15 bag, 100..103 well, -1 nothing. Valid fills
    // action; None and Invalid emit nothing.
    internal static DropVerdict TryResolveDrop(HudView v, int source, int target, out InventoryHudAction action)
    {
        action = default;
        if (target < 0 || source == target)
        {
            return DropVerdict.None;
        }

        bool targetIsWell = EquipmentSlots.TryFromSlotIndex(target, out EquipmentSlot targetSlot);
        if (!targetIsWell && (target < 0 || target >= HudView.BackpackSlots))
        {
            return DropVerdict.None;
        }

        if (EquipmentSlots.TryFromSlotIndex(source, out EquipmentSlot sourceSlot))
        {
            // From a well: only a bag slot takes it, and it is unequipped.
            if (!TryItem(v.Worn, (int)sourceSlot, out _))
            {
                return DropVerdict.None;
            }

            if (targetIsWell)
            {
                return DropVerdict.Invalid;
            }

            action = new InventoryHudAction(InventoryHudActionKind.Unequip, source);
            return DropVerdict.Valid;
        }

        if (source < 0 || source >= HudView.BackpackSlots || !TryItem(v.Backpack, source, out InventoryItemSnapshot item))
        {
            return DropVerdict.None;
        }

        if (!targetIsWell)
        {
            action = new InventoryHudAction(InventoryHudActionKind.Move, source, target);
            return DropVerdict.Valid;
        }

        if (!CharacterPanelModel.CanEquipTo(item.EquipSlots, targetSlot))
        {
            return DropVerdict.Invalid;
        }

        if (TryItem(v.Worn, (int)targetSlot, out InventoryItemSnapshot worn) && worn.ItemInstanceId == item.ItemInstanceId)
        {
            return DropVerdict.None;
        }

        action = new InventoryHudAction(InventoryHudActionKind.EquipTo, source, target);
        return DropVerdict.Valid;
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

    // ---- drawing ----------------------------------------------------------------------------

    private static void DrawWell(
        IUiCanvas c, IUiInput i, HudView v, in UiLayout l, UiSpriteTable s, InventoryUiState st,
        UiRect win, int w, bool live, bool dragging)
    {
        WellDef d = Wells[w];
        UiRect r = l.FromUnits(win, d.X, d.Y, d.W, d.H);
        int index = EquipmentSlots.FirstSlotIndex + w;
        if (s.TryGet(d.Well, out UiSprite well))
        {
            c.Sprite(well, r, White);
        }

        if (s.TryGet(d.Silhouette, out UiSprite sil))
        {
            c.Sprite(sil, r, White);
        }

        bool has = TryItem(v.Worn, w, out _);
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

        DrawDropHighlight(c, i, v, s, st, index, r, dragging);
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
            DrawFallbackLabel(c, WellRect(l, win, w), v.Worn[w].Item.DisplayName);
        }
    }

    private static void DrawBagCells(
        IUiCanvas c, IUiInput i, HudView v, in UiLayout l, UiSpriteTable s, InventoryUiState st,
        UiRect win, int slot, bool live, bool dragging)
    {
        UiRect r = SlotRect(l, win, slot);
        bool has = TryItem(v.Backpack, slot, out InventoryItemSnapshot _);
        bool hovered = false;
        if (has && live && !dragging)
        {
            hovered = i.Hovered(IdBagSlot + slot, r);
            if (hovered)
            {
                st.SetHover(slot, r);
            }

            if (i.BeginDrag(IdBagSlot + slot, r, slot))
            {
                st.StartDrag(slot, v);
            }

            if (i.RightClicked(IdBagSlot + slot, r))
            {
                st.OpenMenu(v, slot, r, i.Pointer.Position);
            }
        }

        if (!has)
        {
            if (s.TryGet(UiSpriteNames.CellEmpty, out UiSprite empty))
            {
                float half = r.W * 0.5f;
                float halfH = r.H * 0.5f;
                c.Sprite(empty, new UiRect(r.X, r.Y, half, halfH), White);
                c.Sprite(empty, new UiRect(r.X + half, r.Y, half, halfH), White);
                c.Sprite(empty, new UiRect(r.X, r.Y + halfH, half, halfH), White);
                c.Sprite(empty, new UiRect(r.X + half, r.Y + halfH, half, halfH), White);
            }
        }
        else if (s.TryGet(hovered ? UiSpriteNames.CellHover : UiSpriteNames.CellOccupied, out UiSprite backing))
        {
            c.Tile(backing, r, White);
        }

        if (has && v.Backpack[slot].Equipped && s.TryGet(UiSpriteNames.CellOverlayHover, out UiSprite alias))
        {
            c.Tile(alias, r, White);
        }

        DrawDropHighlight(c, i, v, s, st, slot, r, dragging);
    }

    private static void DrawBagIcon(IUiCanvas c, HudView v, in UiLayout l, InventoryUiState st, UiRect win, int slot, ItemTier tier)
    {
        if (!TryItem(v.Backpack, slot, out _))
        {
            return;
        }

        HudItemView hv = v.Backpack[slot];
        if (!hv.HasIcon || hv.Tier != tier)
        {
            return;
        }

        UiColor tint = hv.Equipped ? EquippedAliasTint : White;
        if (st.IsDraggingSource(slot))
        {
            tint = DraggedTint;
        }

        c.Sprite(ItemTierStyle.IconFor(hv.Icon, hv.Tier), IconRect(hv.Icon, SlotRect(l, win, slot), l.Scale), tint);
    }

    // Name stub for an item without a sprite, and the stack count, both over the icon.
    private static void DrawBagText(IUiCanvas c, HudView v, in UiLayout l, UiRect win, int slot)
    {
        if (!TryItem(v.Backpack, slot, out InventoryItemSnapshot item))
        {
            return;
        }

        UiRect r = SlotRect(l, win, slot);
        if (!v.Backpack[slot].HasIcon)
        {
            DrawFallbackLabel(c, r, item.DisplayName);
        }

        if (item.StackCount > 1)
        {
            Span<char> mem = stackalloc char[12];
            UiTextBuilder b = new(mem);
            b.Append("x");
            b.Append(item.StackCount);
            ReadOnlySpan<char> text = b.Text;
            float width = c.MeasureText(text, UiTextStyle.Body);
            DrawText(c, text, new Vector2(r.Right - width - (2f * l.Scale), r.Bottom - BodyHeight(c) - l.Scale), ItemTierStyle.Body);
        }
    }

    // While a bag or well item is dragged, the slot under the pointer shows whether the drop works.
    private static void DrawDropHighlight(
        IUiCanvas c, IUiInput i, HudView v, UiSpriteTable s, InventoryUiState st, int index, UiRect r, bool dragging)
    {
        if (!dragging || !st.DragActive || st.DragCancelled || !OverRect(i, IdBase + 0x600 + index, r))
        {
            return;
        }

        DropVerdict verdict = TryResolveDrop(v, st.DragSource, index, out _);
        if (verdict == DropVerdict.None)
        {
            return;
        }

        string name = verdict == DropVerdict.Valid ? UiSpriteNames.CellOverlayHover : UiSpriteNames.CellOverlayInvalid;
        if (s.TryGet(name, out UiSprite overlay))
        {
            c.Tile(overlay, r, White);
        }
    }

    // The pointer is over r. Uses the real position when the input offers it, otherwise Hovered.
    internal static bool OverRect(IUiInput i, int id, UiRect r)
    {
        Vector2 p = i.Pointer.Position;
        return float.IsFinite(p.X) && float.IsFinite(p.Y) ? r.Contains(p) : i.Hovered(id, r);
    }

    // An item with no sprite shows the start of its name, so it is never invisible.
    private static void DrawFallbackLabel(IUiCanvas c, UiRect r, string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        ReadOnlySpan<char> text = name.AsSpan(0, Math.Min(name.Length, 3));
        DrawTextCentered(c, r, text, ItemTierStyle.Body);
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

    // ---- text helpers shared by the windows -------------------------------------------------

    internal static void DrawText(IUiCanvas c, ReadOnlySpan<char> text, Vector2 pos, UiColor color)
    {
        c.Text(text, new Vector2(pos.X + 1f, pos.Y + 1f), UiTextStyle.Body, ItemTierStyle.Shadow);
        c.Text(text, pos, UiTextStyle.Body, color);
    }

    // Centred in r. Text wider than r is cut to fit instead of clipped, so it stays in the same draw
    // command as every other line of text.
    internal static void DrawTextCentered(IUiCanvas c, UiRect r, ReadOnlySpan<char> text, UiColor color)
    {
        text = text[..PanelText.FitLength(c, text, UiTextStyle.Body, r.W)];
        float width = c.MeasureText(text, UiTextStyle.Body);
        float x = r.X + MathF.Max(0f, (r.W - width) * 0.5f);
        float y = r.Y + MathF.Floor((r.H - BodyHeight(c)) * 0.5f);
        DrawText(c, text, new Vector2(x, y), color);
    }
}
