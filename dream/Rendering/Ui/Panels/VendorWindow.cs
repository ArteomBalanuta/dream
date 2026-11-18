using Dream.Rendering.Hud;
using OpenTK.Mathematics;

namespace Dream.Rendering.Ui.Panels;

// The vendor window (M3): the stone frame of the character window, in its place while a vendor is open.
// A buy list of what the vendor sells (a click buys one), the wallet in the second title strip, and a
// paged sell list of the bag with a Sell button. Draws nothing without an inventory open and a vendor
// open. Every pick is only an IHudActions call; the server checks the NPC, range, stock, gold and
// slot. Strings (the vendor's name, item names) are cut to their box and never format strings.
public static class VendorWindow
{
    public const int BuyRows = 8;
    public const int SellRows = 8;

    private const float RowX = 19f;
    private const float RowW = 152f;
    private const float RowH = 12f;
    private const float RowPitch = 13f;
    private const float BuyTop = 58f;
    private const float SellHeadTop = 168f;
    private const float SellTop = 182f;
    private const float PagerW = 14f;
    private const float ButtonH = 16f;

    // Text sizes in window units (1 u = Scale px); the boxes are the art's, so each is capped by its box.
    internal const float RowTextUnits = 9.5f;
    internal const float HeadTextUnits = 8f;
    internal const float ButtonTextUnits = 9.5f;

    // Widget ids, clear of the inventory (0x100..0x502) and the character window (0x310..).
    internal const int IdBuyRow = InventoryWindow.IdBase + 0x600;
    internal const int IdSellRow = InventoryWindow.IdBase + 0x620;
    internal const int IdPagePrev = InventoryWindow.IdBase + 0x640;
    internal const int IdPageNext = InventoryWindow.IdBase + 0x641;
    internal const int IdSellButton = InventoryWindow.IdBase + 0x642;
    internal const int IdClose = InventoryWindow.IdBase + 0x643;

    private static readonly UiColor GoldColor = new(0xD8, 0xB4, 0x5A, 255);
    private static readonly UiColor HoverFill = new(0xF5, 0xDD, 0xA5, 40);
    private static readonly UiColor SelectFill = new(0x8A, 0x1E, 0x1E, 110);
    private static readonly UiColor White = UiColor.White;

    public static void Build(IUiCanvas c, IUiInput i, HudView v, in UiLayout l, UiSpriteTable s, IHudActions a)
    {
        if (!v.InventoryOpen || v.Vendor is not { IsOpen: true } vendor)
        {
            return;
        }

        UiRect win = l.Character;
        bool live = !InventoryUiState.Current.ModalOpen;
        InventoryWindow.DrawFrame(c, s, l, win, inventoryKind: false);

        // Pass one: the plates, hover and selection washes, and every click. Pass two: the text.
        int stock = vendor.StockCount;
        int buyRows = Math.Min(stock, BuyRows);
        for (int n = 0; n < buyRows; n++)
        {
            UiRect r = l.FromUnits(win, RowX, BuyTop + (n * RowPitch), RowW, RowH);
            DrawRowPlate(c, s, r);
            if (live && i.Hovered(IdBuyRow + n, r))
            {
                c.Solid(r, HoverFill);
            }

            if (live && i.Button(IdBuyRow + n, r))
            {
                a.Vendor(new VendorHudAction(VendorHudActionKind.Buy, vendor.StockAt(n).ItemKind));
            }
        }

        int sellCount = CountSellable(v);
        int pages = Math.Max(1, (sellCount + SellRows - 1) / SellRows);
        vendor.SellPage = Math.Clamp(vendor.SellPage, 0, pages - 1);
        DropStaleSelection(v, vendor);

        UiRect prev = l.FromUnits(win, RowX + RowW - (2 * PagerW) - 8f, SellHeadTop, PagerW, RowH);
        UiRect next = l.FromUnits(win, RowX + RowW - PagerW, SellHeadTop, PagerW, RowH);
        DrawRowPlate(c, s, prev);
        DrawRowPlate(c, s, next);
        if (live && vendor.SellPage > 0 && i.Button(IdPagePrev, prev))
        {
            vendor.SellPage--;
        }

        if (live && vendor.SellPage < pages - 1 && i.Button(IdPageNext, next))
        {
            vendor.SellPage++;
        }

        int first = vendor.SellPage * SellRows;
        Span<int> anchors = stackalloc int[SellRows];
        int shown = CollectPage(v, first, anchors);
        for (int n = 0; n < shown; n++)
        {
            UiRect r = l.FromUnits(win, RowX, SellTop + (n * RowPitch), RowW, RowH);
            long rowItemId = v.Backpack[anchors[n]].Item.ItemInstanceId;
            // BH-INPUT-3: same shape as the trade bag row. CollectPage renumbers the sell rows when an
            // item at a lower anchor leaves the bag mid-press (a quickbar potion, a loot push), so a
            // position id would move the selection onto an item the player never clicked — and the
            // Sell step would then carry that item's own instance id, so the server would accept it.
            // The id carries the item, so a mid-press change drops the click instead.
            int rowId = HudWidgetId.Of(IdSellRow, rowItemId);
            DrawRowPlate(c, s, r);
            if (anchors[n] == vendor.SelectedAnchor)
            {
                c.Solid(r, SelectFill);
            }
            else if (live && i.Hovered(rowId, r))
            {
                c.Solid(r, HoverFill);
            }

            if (live && i.Button(rowId, r))
            {
                vendor.Select(anchors[n], rowItemId);
            }
        }

        UiRect sell = l.FromUnits(win, RowX, SellTop + (SellRows * RowPitch) + 6f, RowW, ButtonH);
        bool canSell = vendor.SelectedAnchor >= 0;
        DrawRowPlate(c, s, sell);
        if (canSell && live && i.Hovered(IdSellButton, sell))
        {
            c.Solid(sell, HoverFill);
        }

        if (canSell && live && i.Button(IdSellButton, sell))
        {
            int anchor = vendor.SelectedAnchor;
            a.Vendor(new VendorHudAction(
                VendorHudActionKind.Sell,
                SlotIndex: anchor,
                ItemInstanceId: vendor.SelectedItemId));
            vendor.ClearSelection();
        }

        DrawClose(c, i, l, s, a, win, live);

        // ---- text ----
        // One size per kind of box, each capped by the box the art fixed; the pagers are 14 u wide,
        // so their glyph is shrunk to that width and not only to the 12 u height.
        float shadow = UiText.ShadowPx(in l);
        var title = InventoryWindow.TitleTextFor(in l);
        var row = UiText.For(in l, RowTextUnits, RowH);
        var head = UiText.For(in l, HeadTextUnits, RowH);
        var button = UiText.For(in l, ButtonTextUnits, ButtonH);
        var pager = UiText.ForFitting(c, in l, "<", RowTextUnits, RowH, prev.W);
        UiText.PutCentered(c, l.FromUnits(win, 35f, 8f, 120f, 8f), TitleText(vendor.Title), in title, ItemTierStyle.Title, shadow);
        DrawGoldStrip(c, l, win, v, in title, shadow);
        UiText.PutLeft(c, HeadRect(l, win, BuyTop - 12f), 0f, "For sale", in head, ItemTierStyle.Dim, shadow);
        if (stock == 0)
        {
            UiText.PutCentered(c, l.FromUnits(win, RowX, BuyTop, RowW, RowH), "Nothing for sale", in row, ItemTierStyle.Dim, shadow);
        }

        long gold = v.Inventory?.Gold ?? 0;
        for (int n = 0; n < buyRows; n++)
        {
            VendorStockLine line = vendor.StockAt(n);
            UiRect r = l.FromUnits(win, RowX, BuyTop + (n * RowPitch), RowW, RowH);
            DrawPricedRow(c, r, line.DisplayName, line.Price, ItemTierStyle.Body, line.Price > gold ? ItemTierStyle.Danger : GoldColor, in row, shadow);
        }

        Span<char> mem = stackalloc char[24];
        UiTextBuilder pageText = new(mem);
        pageText.Append(vendor.SellPage + 1);
        pageText.Append("/");
        pageText.Append(pages);
        UiRect headRect = HeadRect(l, win, SellHeadTop + 1f);
        UiText.PutLeft(c, headRect, 0f, "Your goods", in head, ItemTierStyle.Dim, shadow);
        UiText.PutCentered(c, prev, "<", in pager, vendor.SellPage > 0 ? ItemTierStyle.Body : ItemTierStyle.Dim, shadow);
        UiText.PutCentered(c, next, ">", in pager, vendor.SellPage < pages - 1 ? ItemTierStyle.Body : ItemTierStyle.Dim, shadow);

        // "1/2" between the heading and the pagers. The 8 u gap between the two pagers is narrower than
        // the text at any size, so it goes in the room the heading leaves instead of on top of them.
        float pageLeft = headRect.X + UiText.Measure(c, "Your goods", in head) + (4f * l.Scale);
        UiRect pageBox = new(pageLeft, prev.Y, MathF.Max(0f, prev.X - pageLeft), prev.H);
        var page = UiText.ForFitting(c, in l, pageText.Text, HeadTextUnits, RowH, MathF.Max(0f, pageBox.W - (2f * l.Scale)));
        UiText.PutRight(c, pageBox, 2f * l.Scale, pageText.Text, in page, ItemTierStyle.Dim, shadow);

        if (shown == 0)
        {
            UiText.PutCentered(c, l.FromUnits(win, RowX, SellTop, RowW, RowH), "Nothing to sell", in row, ItemTierStyle.Dim, shadow);
        }

        for (int n = 0; n < shown; n++)
        {
            HudItemView item = v.Backpack[anchors[n]];
            UiRect r = l.FromUnits(win, RowX, SellTop + (n * RowPitch), RowW, RowH);
            string name = item.Item.DisplayName ?? string.Empty;
            ReadOnlySpan<char> shownName = name.AsSpan(0, Math.Min(name.Length, 40));
            UiText.PutLeft(c, r, 2f * l.Scale, shownName, in row, ItemTierStyle.ColorOf(item.Tier), shadow);
        }

        UiText.PutCentered(c, sell, "Sell", in button, canSell ? ItemTierStyle.Title : ItemTierStyle.Dim, shadow);
    }

    // The bag items the sell list offers: every item with an anchor, in anchor order.
    internal static int CountSellable(HudView v)
    {
        int count = 0;
        for (int anchor = 0; anchor < HudView.BackpackSlots && anchor < v.Backpack.Length; anchor++)
        {
            if (IsSellRow(v, anchor))
            {
                count++;
            }
        }

        return count;
    }

    // Fills anchors with the page's items (anchor order), returns how many.
    internal static int CollectPage(HudView v, int first, Span<int> anchors)
    {
        int seen = 0;
        int shown = 0;
        for (int anchor = 0; anchor < HudView.BackpackSlots && anchor < v.Backpack.Length && shown < anchors.Length; anchor++)
        {
            if (!IsSellRow(v, anchor))
            {
                continue;
            }

            if (seen++ >= first)
            {
                anchors[shown++] = anchor;
            }
        }

        return shown;
    }

    private static bool IsSellRow(HudView v, int anchor) =>
        v.Backpack[anchor].Item.ItemInstanceId != 0
        && !v.Backpack[anchor].Equipped
        && v.BagAnchorAt(anchor) == anchor;

    private static void DropStaleSelection(HudView v, VendorHudState vendor)
    {
        int anchor = vendor.SelectedAnchor;
        if (anchor >= 0 && (anchor >= HudView.BackpackSlots || anchor >= v.Backpack.Length || !IsSellRow(v, anchor)
            || v.Backpack[anchor].Item.ItemInstanceId != vendor.SelectedItemId))
        {
            vendor.ClearSelection();
        }
    }

    private static void DrawRowPlate(IUiCanvas c, UiSpriteTable s, UiRect r)
    {
        if (s.TryGet(UiSpriteNames.WinLineBox, out UiSprite box))
        {
            c.NineSlice(box, r, White);
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

    // Name on the left, price on the right; the name gives way to the price. Measured at the size it
    // draws with: PanelText.FitLength reads the canvas's current scale, which is not this one.
    private static void DrawPricedRow(
        IUiCanvas c, UiRect r, string name, long price, UiColor nameColor, UiColor priceColor,
        in UiSizedText t, float shadow)
    {
        Span<char> mem = stackalloc char[16];
        UiTextBuilder b = new(mem);
        b.Append(ClampToInt(price));
        float inset = MathF.Max(2f, r.H * 0.15f);
        float priceW = UiText.Measure(c, b.Text, in t);
        float y = UiText.TextY(r, in t);
        UiText.Put(c, b.Text, new Vector2(r.Right - inset - priceW, y), in t, priceColor, shadow);
        ReadOnlySpan<char> shown = (name ?? string.Empty).AsSpan(0, Math.Min((name ?? string.Empty).Length, 40));
        shown = shown[..UiText.FitLength(c, shown, in t, MathF.Max(0f, r.W - (3f * inset) - priceW))];
        UiText.Put(c, shown, new Vector2(r.X + inset, y), in t, nameColor, shadow);
    }

    private static UiRect HeadRect(in UiLayout l, UiRect win, float topUnits) =>
        l.FromUnits(win, RowX, topUnits, RowW, RowH);

    private static int ClampToInt(long value) => (int)Math.Clamp(value, 0L, int.MaxValue);
}
