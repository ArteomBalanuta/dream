using Dream.Rendering.Hud;
using Dream.Simulation.Inventory;
using OpenTK.Mathematics;

namespace Dream.Rendering.Ui.Panels;

// The vendor window: the stone frame of the character window, in its place while a vendor is open, with
// the vendor's stock on an 8x8 board that mirrors the bag cell for cell (VendorGrid). Stock is placed by
// real item footprints in stock order. A click on an item buys one; dragging a bag item onto the board
// sells it (the release itself is resolved by InventoryWindow, which owns the drag; this window only
// draws the reaction: the frame ignites for a sale and goes cold for a refusal). Draws nothing without an
// inventory open and a vendor open. Every pick is only an IHudActions call; the server checks the NPC,
// range, stock, gold and slot. Strings are cut to their box and never format strings. Every shop sprite
// is drawn through TryGet, so the window is complete without the forged art.
public static class VendorWindow
{
    // Kept for the text-fit preview test, which still lays out the retired list rows.
    public const int BuyRows = 8;
    public const int SellRows = 8;

    // Text sizes in window units (1 u = Scale px); the boxes are the art's, so each is capped by its box.
    internal const float RowTextUnits = 9.5f;
    internal const float HeadTextUnits = 8f;
    internal const float ButtonTextUnits = 9.5f;
    internal const float CellTextUnits = 8f;

    // The lines of the free band above the board (58..190 u): the "For sale" head, then the hovered
    // item's name and price, or the hints.
    private const float TextX = 19f;
    private const float TextW = 152f;
    private const float TextH = 12f;
    private const float HeadTop = 46f;
    private const float InfoTop = 58f;
    private const float InfoPitch = 13f;

    // Widget ids, clear of the inventory (0x100..0x502) and the character window (0x310..). A shop cell's
    // id is HudWidgetId.Of(IdShopCell, VendorGrid.KindIdentity(kind)), never IdShopCell + index.
    internal const int IdShopCell = InventoryWindow.IdBase + 0x700;
    internal const int IdClose = InventoryWindow.IdBase + 0x643;

    // How far the ember and cold frame reach past the board edge.
    private const float FrameInflateUnits = 2f;

    private static readonly UiColor GoldColor = new(0xD8, 0xB4, 0x5A, 255);
    private static readonly UiColor Unaffordable = new(0xB0, 0xB0, 0xB0, 255);
    private static readonly UiColor White = UiColor.White;

    public static void Build(IUiCanvas c, IUiInput i, HudView v, in UiLayout l, UiSpriteTable s, IHudActions a)
    {
        VendorFxState fx = VendorFxState.Current;
        if (!v.InventoryOpen || v.Vendor is not { IsOpen: true } vendor)
        {
            fx.SetSellHover(InventoryWindow.DropVerdict.None, v.AnimationSeconds);
            return;
        }

        InventoryUiState st = InventoryUiState.Current;
        UiRect win = l.Character;
        bool live = !st.ModalOpen;
        bool dragging = i.ActiveDragPayload >= 0;
        long gold = v.Inventory?.Gold ?? 0;
        InventoryWindow.DrawFrame(c, s, l, win, inventoryKind: false);

        Span<int> anchors = stackalloc int[VendorHudState.MaxStock];
        Span<ItemFootprint> footprints = stackalloc ItemFootprint[VendorHudState.MaxStock];
        Span<sbyte> owner = stackalloc sbyte[BagGrid.CellCount];
        owner.Fill(-1);
        VendorGrid.Place(v, vendor, anchors, footprints);
        int stock = Math.Min(vendor.StockCount, anchors.Length);
        for (int n = 0; n < stock; n++)
        {
            if (anchors[n] >= 0 && BagGrid.TryMask(anchors[n], footprints[n], out ulong mask))
            {
                for (int cell = 0; cell < BagGrid.CellCount; cell++)
                {
                    if ((mask & (1UL << cell)) != 0)
                    {
                        owner[cell] = (sbyte)n;
                    }
                }
            }
        }

        // What releasing the drag on the board would do. The same decision InventoryWindow makes on the
        // release, so the cue never disagrees with it.
        Vector2 pointer = i.Pointer.Position;
        InventoryWindow.DropVerdict sell = InventoryWindow.DropVerdict.None;
        if (dragging && st.DragActive && !st.DragCancelled)
        {
            sell = VendorGrid.TryResolveSell(in l, v, st.DragSource, pointer, out _);
        }

        fx.SetSellHover(sell, v.AnimationSeconds);

        // The hovered stock line. No widget is registered while an item is being dragged or a menu is open.
        int hovered = -1;
        if (live && !dragging)
        {
            for (int n = 0; n < stock; n++)
            {
                if (anchors[n] >= 0 && i.Hovered(CellId(vendor, n), VendorGrid.ItemRect(in l, win, anchors[n], footprints[n])))
                {
                    hovered = n;
                }
            }
        }

        // Pass one: the board, its frame, and every click. Pass two: the icons. Pass three: the text.
        DrawBoard(c, s, l, win, owner, hovered);
        DrawSellCue(c, s, l, win, sell, fx, pointer);

        if (live && !dragging)
        {
            for (int n = 0; n < stock; n++)
            {
                if (anchors[n] < 0)
                {
                    continue;
                }

                VendorStockLine line = vendor.StockAt(n);
                UiRect r = VendorGrid.ItemRect(in l, win, anchors[n], footprints[n]);
                if (!i.Button(CellId(vendor, n), r))
                {
                    continue;
                }

                if (line.Price > gold)
                {
                    i.Emit(UiFeedback.Refuse);
                    continue;
                }

                a.Vendor(new VendorHudAction(VendorHudActionKind.Buy, line.ItemKind));
                i.Emit(UiFeedback.Coin);
                fx.NoteBuy(line.ItemKind, v);
            }
        }

        DrawClose(c, i, l, s, a, win, live);

        for (int n = 0; n < stock; n++)
        {
            if (anchors[n] < 0 || !fx.TryIcon(n, vendor.StockAt(n).ItemKind, s, out UiSprite icon))
            {
                continue;
            }

            UiRect box = VendorGrid.ItemRect(in l, win, anchors[n], footprints[n]);
            UiSprite shown = ItemTierStyle.IconFor(icon, ItemTier.Common);
            c.Sprite(shown, InventoryWindow.IconRect(icon, box, l.Scale), vendor.StockAt(n).Price > gold ? Unaffordable : White);
        }

        DrawText(c, s, l, win, v, vendor, fx, anchors, footprints, stock, hovered, gold);
    }

    // The widget id of stock line n: it folds the item kind in, so a stock that renumbers between a press
    // and its release drops the click instead of buying a different item.
    private static int CellId(VendorHudState vendor, int line) =>
        HudWidgetId.Of(IdShopCell, VendorGrid.KindIdentity(vendor.StockAt(line).ItemKind));

    private static void DrawBoard(
        IUiCanvas c, UiSpriteTable s, in UiLayout l, UiRect win, ReadOnlySpan<sbyte> owner, int hovered)
    {
        bool haveEmpty = s.TryGet(UiSpriteNames.ShopCellEmpty, out UiSprite empty)
            || s.TryGet(UiSpriteNames.CellEmpty, out empty);
        bool haveOccupied = s.TryGet(UiSpriteNames.ShopCellOccupied, out UiSprite occupied)
            || s.TryGet(UiSpriteNames.CellOccupied, out occupied);
        bool haveHover = s.TryGet(UiSpriteNames.CellHover, out UiSprite hover);
        for (int cell = 0; cell < BagGrid.CellCount; cell++)
        {
            UiRect r = VendorGrid.CellRect(in l, win, cell);
            int line = owner[cell];
            if (line < 0)
            {
                if (haveEmpty)
                {
                    c.Sprite(empty, r, White);
                }
            }
            else if (line == hovered)
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
        }
    }

    // A drag over the board: Valid ignites the frame (eased by EmberLevel) and washes the cell under the
    // pointer; Invalid goes cold. Once the drag ends the ember keeps drawing while it fades.
    private static void DrawSellCue(
        IUiCanvas c, UiSpriteTable s, in UiLayout l, UiRect win, InventoryWindow.DropVerdict sell, VendorFxState fx, Vector2 pointer)
    {
        UiRect board = VendorGrid.BoardRect(in l, win);
        float pad = FrameInflateUnits * l.Scale;
        UiRect frame = new(board.X - pad, board.Y - pad, board.W + (2f * pad), board.H + (2f * pad));
        if (sell != InventoryWindow.DropVerdict.None)
        {
            string name = sell == InventoryWindow.DropVerdict.Valid ? UiSpriteNames.CellOverlayHover : UiSpriteNames.CellOverlayInvalid;
            if (board.Contains(pointer) && s.TryGet(name, out UiSprite overlay))
            {
                float cell = VendorGrid.CellUnits * l.Scale;
                int col = Math.Clamp((int)MathF.Floor((pointer.X - board.X) / cell), 0, VendorGrid.Columns - 1);
                int row = Math.Clamp((int)MathF.Floor((pointer.Y - board.Y) / cell), 0, VendorGrid.Rows - 1);
                c.Tile(overlay, VendorGrid.CellRect(in l, win, (row * VendorGrid.Columns) + col), White);
            }
        }

        if (sell == InventoryWindow.DropVerdict.Invalid)
        {
            if (s.TryGet(UiSpriteNames.ShopFrameCold, out UiSprite cold))
            {
                c.NineSlice(cold, frame, White);
            }
        }
        else if (fx.EmberLevel > 0.001f && s.TryGet(UiSpriteNames.ShopFrameEmber, out UiSprite ember))
        {
            c.NineSlice(ember, frame, new UiColor(255, 255, 255, (byte)Math.Clamp((int)MathF.Round(255f * fx.EmberLevel), 0, 255)));
        }
    }

    private static void DrawClose(IUiCanvas c, IUiInput i, in UiLayout l, UiSpriteTable s, IHudActions a, UiRect win, bool live)
    {
        UiRect r = l.FromUnits(win, 19f, 395f, 24f, 24f);
        bool hovered = live && i.Hovered(IdClose, r);
        if (s.TryGet(hovered ? UiSpriteNames.ButtonHover : UiSpriteNames.ButtonNormal, out UiSprite plate))
        {
            c.Sprite(plate, r, White);
        }

        if (s.TryGet(hovered ? UiSpriteNames.GlyphCloseHover : UiSpriteNames.GlyphCloseNormal, out UiSprite glyph))
        {
            c.Sprite(glyph, l.FromUnits(win, 22f, 398f, 18f, 18f), White);
        }

        if (live && i.Button(IdClose, r))
        {
            a.CloseVendor();
        }
    }

    private static void DrawText(
        IUiCanvas c, UiSpriteTable s, in UiLayout l, UiRect win, HudView v, VendorHudState vendor, VendorFxState fx,
        ReadOnlySpan<int> anchors, ReadOnlySpan<ItemFootprint> footprints, int stock, int hovered, long gold)
    {
        float shadow = UiText.ShadowPx(in l);
        var title = InventoryWindow.TitleTextFor(in l);
        var row = UiText.For(in l, RowTextUnits, TextH);
        var head = UiText.For(in l, HeadTextUnits, TextH);
        UiText.PutCentered(c, l.FromUnits(win, 35f, 8f, 120f, 8f), TitleText(vendor.Title), in title, ItemTierStyle.Title, shadow);
        DrawGoldStrip(c, l, win, v, in title, shadow);
        UiText.PutLeft(c, l.FromUnits(win, TextX, HeadTop, TextW, TextH), 0f, "For sale", in head, ItemTierStyle.Dim, shadow);

        UiRect nameBox = l.FromUnits(win, TextX, InfoTop, TextW, TextH);
        UiRect priceBox = l.FromUnits(win, TextX, InfoTop + InfoPitch, TextW, TextH);
        if (hovered >= 0)
        {
            VendorStockLine line = vendor.StockAt(hovered);
            string name = line.DisplayName ?? string.Empty;
            ReadOnlySpan<char> shownName = name.AsSpan(0, Math.Min(name.Length, 40));
            shownName = shownName[..UiText.FitLength(c, shownName, in row, MathF.Max(0f, nameBox.W))];
            UiText.PutLeft(c, nameBox, 0f, shownName, in row, ItemTierStyle.Body, shadow);

            Span<char> mem = stackalloc char[24];
            UiTextBuilder b = new(mem);
            b.Append("Price ");
            b.Append(ClampToInt(line.Price));
            UiText.PutLeft(c, priceBox, 0f, b.Text, in row, line.Price > gold ? ItemTierStyle.Danger : GoldColor, shadow);
        }
        else if (stock == 0)
        {
            UiText.PutLeft(c, nameBox, 0f, "Nothing for sale", in row, ItemTierStyle.Dim, shadow);
        }
        else
        {
            UiText.PutLeft(c, nameBox, 0f, "Click an item to buy it.", in row, ItemTierStyle.Dim, shadow);
            UiText.PutLeft(c, priceBox, 0f, "Drag a bag item here to sell.", in row, ItemTierStyle.Dim, shadow);
        }

        for (int n = 0; n < stock; n++)
        {
            if (anchors[n] < 0)
            {
                continue;
            }

            VendorStockLine line = vendor.StockAt(n);
            UiRect r = VendorGrid.ItemRect(in l, win, anchors[n], footprints[n]);
            if (!fx.TryIcon(n, line.ItemKind, s, out _))
            {
                DrawFallbackLabel(c, l, r, line.DisplayName);
            }

            DrawPrice(c, l, r, line.Price, line.Price > gold ? ItemTierStyle.Danger : GoldColor);
        }
    }

    // The price small in the item's bottom-right, like a stack count: shrunk to the cell's WIDTH as well
    // as its height, since a five digit price is wider than a 20 u cell at the 13 px bake.
    private static void DrawPrice(IUiCanvas c, in UiLayout l, UiRect r, long price, UiColor color)
    {
        Span<char> mem = stackalloc char[12];
        UiTextBuilder b = new(mem);
        b.Append(ClampToInt(price));
        ReadOnlySpan<char> text = b.Text;
        float pad = 2f * l.Scale;
        var t = UiText.ForFitting(c, in l, text, CellTextUnits, VendorGrid.CellUnits, MathF.Max(0f, r.W - (2f * pad)));
        float width = UiText.Measure(c, text, in t);
        UiText.Put(c, text, new Vector2(r.Right - width - pad, r.Bottom - t.Px - l.Scale), in t, color, UiText.ShadowPx(in l));
    }

    // An item with no sprite shows the start of its name, so it is never invisible. Cut by WIDTH.
    private static void DrawFallbackLabel(IUiCanvas c, in UiLayout l, UiRect r, string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        ReadOnlySpan<char> text = name.AsSpan(0, Math.Min(name.Length, 24));
        var t = UiText.For(in l, CellTextUnits, VendorGrid.CellUnits);
        float room = MathF.Max(0f, r.W - (2f * l.Scale));
        UiText.PutCentered(c, r, text[..UiText.FitLength(c, text, in t, room)], in t, ItemTierStyle.Body, UiText.ShadowPx(in l));
    }

    private static ReadOnlySpan<char> TitleText(string? title)
    {
        if (string.IsNullOrEmpty(title))
        {
            return "Merchant";
        }

        return title.AsSpan(0, Math.Min(title.Length, VendorHudState.MaxTitleChars));
    }

    private static void DrawGoldStrip(IUiCanvas c, in UiLayout l, UiRect win, HudView v, in UiSizedText t, float shadow)
    {
        Span<char> mem = stackalloc char[32];
        UiTextBuilder b = new(mem);
        b.Append("Gold ");
        b.Append(ClampToInt(v.Inventory?.Gold ?? 0));
        UiText.PutCentered(c, l.FromUnits(win, 35f, 20f, 119f, 8f), b.Text, in t, GoldColor, shadow);
    }

    private static int ClampToInt(long value) => (int)Math.Clamp(value, 0L, int.MaxValue);
}
