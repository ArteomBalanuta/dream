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
            DrawRowPlate(c, s, r);
            if (anchors[n] == vendor.SelectedAnchor)
            {
                c.Solid(r, SelectFill);
            }
            else if (live && i.Hovered(IdSellRow + n, r))
            {
                c.Solid(r, HoverFill);
            }

            if (live && i.Button(IdSellRow + n, r))
            {
                vendor.Select(anchors[n], v.Backpack[anchors[n]].Item.ItemInstanceId);
            }
        }

        UiRect sell = l.FromUnits(win, RowX, SellTop + (SellRows * RowPitch) + 6f, RowW, 16f);
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
        InventoryWindow.DrawTextCentered(c, l.FromUnits(win, 35f, 8f, 120f, 8f), TitleText(vendor.Title), ItemTierStyle.Title);
        DrawGoldStrip(c, l, win, v);
        InventoryWindow.DrawText(c, "For sale", HeadPos(l, win, BuyTop - 12f), ItemTierStyle.Dim);
        if (stock == 0)
        {
            InventoryWindow.DrawTextCentered(c, l.FromUnits(win, RowX, BuyTop, RowW, RowH), "Nothing for sale", ItemTierStyle.Dim);
        }

        long gold = v.Inventory?.Gold ?? 0;
        for (int n = 0; n < buyRows; n++)
        {
            VendorStockLine line = vendor.StockAt(n);
            UiRect r = l.FromUnits(win, RowX, BuyTop + (n * RowPitch), RowW, RowH);
            DrawPricedRow(c, r, line.DisplayName, line.Price, ItemTierStyle.Body, line.Price > gold ? ItemTierStyle.Danger : GoldColor);
        }

        Span<char> mem = stackalloc char[24];
        UiTextBuilder pageText = new(mem);
        pageText.Append(vendor.SellPage + 1);
        pageText.Append("/");
        pageText.Append(pages);
        InventoryWindow.DrawText(c, "Your goods", HeadPos(l, win, SellHeadTop + 1f), ItemTierStyle.Dim);
        InventoryWindow.DrawTextCentered(c, prev, "<", vendor.SellPage > 0 ? ItemTierStyle.Body : ItemTierStyle.Dim);
        InventoryWindow.DrawTextCentered(c, next, ">", vendor.SellPage < pages - 1 ? ItemTierStyle.Body : ItemTierStyle.Dim);
        float pageX = prev.Right + (((next.X - prev.Right) - c.MeasureText(pageText.Text, UiTextStyle.Body)) * 0.5f);
        InventoryWindow.DrawText(c, pageText.Text, new Vector2(pageX, prev.Y + MathF.Floor((prev.H - InventoryWindow.BodyHeight(c)) * 0.5f)), ItemTierStyle.Dim);

        if (shown == 0)
        {
            InventoryWindow.DrawTextCentered(c, l.FromUnits(win, RowX, SellTop, RowW, RowH), "Nothing to sell", ItemTierStyle.Dim);
        }

        for (int n = 0; n < shown; n++)
        {
            HudItemView item = v.Backpack[anchors[n]];
            UiRect r = l.FromUnits(win, RowX, SellTop + (n * RowPitch), RowW, RowH);
            string name = item.Item.DisplayName ?? string.Empty;
            float inset = 2f * l.Scale;
            ReadOnlySpan<char> shownName = name.AsSpan(0, Math.Min(name.Length, 40));
            shownName = shownName[..PanelText.FitLength(c, shownName, UiTextStyle.Body, r.W - (2f * inset))];
            InventoryWindow.DrawText(c, shownName, new Vector2(r.X + inset, TextY(c, r)), ItemTierStyle.ColorOf(item.Tier));
        }

        InventoryWindow.DrawTextCentered(c, sell, "Sell", canSell ? ItemTierStyle.Title : ItemTierStyle.Dim);
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

    private static void DrawGoldStrip(IUiCanvas c, in UiLayout l, UiRect win, HudView v)
    {
        Span<char> mem = stackalloc char[32];
        UiTextBuilder b = new(mem);
        b.Append("Gold ");
        b.Append(ClampToInt(v.Inventory?.Gold ?? 0));
        InventoryWindow.DrawTextCentered(c, l.FromUnits(win, 35f, 20f, 119f, 8f), b.Text, GoldColor);
    }

    // Name on the left, price on the right; the name gives way to the price.
    private static void DrawPricedRow(IUiCanvas c, UiRect r, string name, long price, UiColor nameColor, UiColor priceColor)
    {
        Span<char> mem = stackalloc char[16];
        UiTextBuilder b = new(mem);
        b.Append(ClampToInt(price));
        float inset = MathF.Max(2f, r.H * 0.15f);
        float priceW = c.MeasureText(b.Text, UiTextStyle.Body);
        float y = TextY(c, r);
        InventoryWindow.DrawText(c, b.Text, new Vector2(r.Right - inset - priceW, y), priceColor);
        ReadOnlySpan<char> shown = (name ?? string.Empty).AsSpan(0, Math.Min((name ?? string.Empty).Length, 40));
        shown = shown[..PanelText.FitLength(c, shown, UiTextStyle.Body, MathF.Max(0f, r.W - (3f * inset) - priceW))];
        InventoryWindow.DrawText(c, shown, new Vector2(r.X + inset, y), nameColor);
    }

    private static float TextY(IUiCanvas c, UiRect r) => r.Y + MathF.Floor((r.H - InventoryWindow.BodyHeight(c)) * 0.5f);

    private static Vector2 HeadPos(in UiLayout l, UiRect win, float topUnits)
    {
        UiRect r = l.FromUnits(win, RowX, topUnits, RowW, RowH);
        return new Vector2(r.X, r.Y);
    }

    private static int ClampToInt(long value) => (int)Math.Clamp(value, 0L, int.MaxValue);
}
