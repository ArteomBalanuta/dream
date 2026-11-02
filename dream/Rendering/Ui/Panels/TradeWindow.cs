using Dream.Multiplayer.Protocol;
using Dream.Rendering.Hud;
using Dream.Simulation.Inventory;
using OpenTK.Mathematics;

namespace Dream.Rendering.Ui.Panels;

// The trade window (M5 workstream A): both offers, gold entry, Lock, Confirm and Cancel. Takes the
// character window's place while a trade is open, in the stone frame of the vendor window. The three stages
// it draws: waiting for the answer (Cancel), an incoming request (Accept, Decline) and the open trade. The
// open trade lists what the other player offers (their text in the tier colour), the gold of both sides,
// six gold steps for your own offer, a paged bag list where a click puts an item in or takes it out of your
// offer, and the Lock and Confirm buttons. Every pick is only a TradeXxxIntent in the queue; the server
// checks everything and the window is redrawn from its next state. Once either side locked, the offer
// controls go dark, because an edit would end the trade. Strings are the scene's cleaned text, cut to their
// box and never format strings. Draws nothing without an inventory open and a trade open.
public static class TradeWindow
{
    public const int BagRows = 8;
    public const int OfferRows = TradeHudState.MaxLines;

    private const float RowX = 19f;
    private const float RowW = 152f;
    private const float RowH = 12f;
    private const float RowPitch = 13f;
    private const float StatusTop = 34f;
    private const float TheirHeadTop = 46f;
    private const float TheirTop = 58f;
    private const float MineHeadTop = 166f;
    private const float GoldTop = 178f;
    private const float GoodsHeadTop = 194f;
    private const float BagTop = 206f;
    private const float ButtonTop = 316f;
    private const float PagerW = 14f;

    // Widget ids, clear of the inventory, the character window and the vendor window (..0x643).
    internal const int IdBagRow = InventoryWindow.IdBase + 0x660;
    internal const int IdPagePrev = InventoryWindow.IdBase + 0x670;
    internal const int IdPageNext = InventoryWindow.IdBase + 0x671;
    internal const int IdGoldStep = InventoryWindow.IdBase + 0x672;
    internal const int IdLock = InventoryWindow.IdBase + 0x680;
    internal const int IdConfirm = InventoryWindow.IdBase + 0x681;
    internal const int IdCancel = InventoryWindow.IdBase + 0x682;
    internal const int IdAccept = InventoryWindow.IdBase + 0x683;
    internal const int IdDecline = InventoryWindow.IdBase + 0x684;
    internal const int IdClose = InventoryWindow.IdBase + 0x685;

    private static readonly long[] GoldSteps = [-100, -10, -1, 1, 10, 100];
    private static readonly string[] GoldStepLabels = ["-100", "-10", "-1", "+1", "+10", "+100"];

    private static readonly UiColor GoldColor = new(0xD8, 0xB4, 0x5A, 255);
    private static readonly UiColor HoverFill = new(0xF5, 0xDD, 0xA5, 40);
    private static readonly UiColor OfferFill = new(0x8A, 0x1E, 0x1E, 110);
    private static readonly UiColor LockedFill = new(0x5A, 0x44, 0x2A, 90);
    private static readonly UiColor White = UiColor.White;

    public static void Build(
        IUiCanvas c,
        IUiInput i,
        HudView v,
        in UiLayout l,
        UiSpriteTable s,
        IHudActions a,
        TradeHudState? trade,
        HudIntentQueue? intents)
    {
        _ = a;
        if (!v.InventoryOpen || trade is not { IsOpen: true })
        {
            return;
        }

        UiRect win = l.Character;
        bool live = !InventoryUiState.Current.ModalOpen && intents != null;
        InventoryWindow.DrawFrame(c, s, l, win, inventoryKind: false);
        InventoryWindow.DrawTextCentered(c, l.FromUnits(win, 35f, 8f, 120f, 8f), TitleText(trade), ItemTierStyle.Title);
        DrawGoldStrip(c, l, win, v);

        switch (trade.Stage)
        {
            case TradeHudStage.Requested:
                InventoryWindow.DrawTextCentered(c, l.FromUnits(win, RowX, 70f, RowW, RowH), "Waiting for an answer", ItemTierStyle.Dim);
                if (Button(c, i, s, l, win, IdCancel, RowX, 90f, RowW, 16f, "Cancel", true, live) && live)
                {
                    intents!.Enqueue(new TradeCancelIntent());
                }

                return;
            case TradeHudStage.Incoming:
                InventoryWindow.DrawTextCentered(c, l.FromUnits(win, RowX, 70f, RowW, RowH), "asks to trade with you", ItemTierStyle.Body);
                if (Button(c, i, s, l, win, IdAccept, RowX, 90f, RowW, 16f, "Accept", true, live) && live)
                {
                    intents!.Enqueue(new TradeRespondIntent(true));
                }

                if (Button(c, i, s, l, win, IdDecline, RowX, 110f, RowW, 16f, "Decline", true, live) && live)
                {
                    intents!.Enqueue(new TradeRespondIntent(false));
                }

                return;
        }

        BuildOpen(c, i, v, l, s, win, trade, intents, live);
        DrawClose(c, i, l, s, win, live, intents);
    }

    private static void BuildOpen(
        IUiCanvas c,
        IUiInput i,
        HudView v,
        in UiLayout l,
        UiSpriteTable s,
        UiRect win,
        TradeHudState trade,
        HudIntentQueue? intents,
        bool live)
    {
        bool editable = trade.OfferEditable && live;

        InventoryWindow.DrawTextCentered(c, l.FromUnits(win, RowX, StatusTop, RowW, RowH), StatusText(trade), StatusColor(trade));

        // ---- their offer ----
        InventoryWindow.DrawText(c, "They offer", HeadPos(l, win, TheirHeadTop), ItemTierStyle.Dim);
        DrawRightGold(c, l, win, TheirHeadTop, trade.TheirGold, trade.TheirLocked);
        for (int n = 0; n < OfferRows; n++)
        {
            UiRect r = l.FromUnits(win, RowX, TheirTop + (n * RowPitch), RowW, RowH);
            DrawRowPlate(c, s, r);
            if (trade.TheirLocked)
            {
                c.Solid(r, LockedFill);
            }

            if (n < trade.TheirCount)
            {
                TradeHudLine line = trade.TheirsAt(n);
                DrawFitted(c, l, r, line.Text, ItemTierStyle.ColorOf(ItemTierStyle.TierOf(line.Rarity)));
            }
        }

        // ---- your offer: gold steps and a summary ----
        InventoryWindow.DrawText(c, "You offer", HeadPos(l, win, MineHeadTop), ItemTierStyle.Dim);
        DrawRightGold(c, l, win, MineHeadTop, trade.MyGold, trade.MyLocked);
        float stepW = (RowW - (5f * 2f)) / 6f;
        long wallet = v.Inventory?.Gold ?? 0;
        for (int n = 0; n < GoldSteps.Length; n++)
        {
            UiRect r = l.FromUnits(win, RowX + (n * (stepW + 2f)), GoldTop, stepW, RowH);
            bool enabled = editable && StepAllowed(trade.MyGold, GoldSteps[n], wallet);
            if (Button(c, i, s, l, win, IdGoldStep + n, RowX + (n * (stepW + 2f)), GoldTop, stepW, RowH, GoldStepLabels[n], enabled, live) && enabled)
            {
                long gold = Math.Clamp(trade.MyGold + GoldSteps[n], 0L, Math.Min(wallet, InventoryConstants.MaxGold));
                intents!.Enqueue(new TradeOfferIntent(OfferedIds(trade, excluding: 0), gold));
            }
        }

        // ---- your goods: the paged bag list ----
        int bagCount = CountOfferable(v);
        int pages = Math.Max(1, (bagCount + BagRows - 1) / BagRows);
        trade.BagPage = Math.Clamp(trade.BagPage, 0, pages - 1);
        InventoryWindow.DrawText(c, "Your goods", HeadPos(l, win, GoodsHeadTop + 1f), ItemTierStyle.Dim);
        UiRect prev = l.FromUnits(win, RowX + RowW - (2 * PagerW) - 8f, GoodsHeadTop, PagerW, RowH);
        UiRect next = l.FromUnits(win, RowX + RowW - PagerW, GoodsHeadTop, PagerW, RowH);
        DrawRowPlate(c, s, prev);
        DrawRowPlate(c, s, next);
        if (live && trade.BagPage > 0 && i.Button(IdPagePrev, prev))
        {
            trade.BagPage--;
        }

        if (live && trade.BagPage < pages - 1 && i.Button(IdPageNext, next))
        {
            trade.BagPage++;
        }

        InventoryWindow.DrawTextCentered(c, prev, "<", trade.BagPage > 0 ? ItemTierStyle.Body : ItemTierStyle.Dim);
        InventoryWindow.DrawTextCentered(c, next, ">", trade.BagPage < pages - 1 ? ItemTierStyle.Body : ItemTierStyle.Dim);

        Span<int> anchors = stackalloc int[BagRows];
        int shown = CollectPage(v, trade.BagPage * BagRows, anchors);
        for (int n = 0; n < shown; n++)
        {
            UiRect r = l.FromUnits(win, RowX, BagTop + (n * RowPitch), RowW, RowH);
            HudItemView item = v.Backpack[anchors[n]];
            bool offered = trade.OffersItem(item.Item.ItemInstanceId);
            DrawRowPlate(c, s, r);
            if (offered)
            {
                c.Solid(r, OfferFill);
            }
            else if (editable && i.Hovered(IdBagRow + n, r))
            {
                c.Solid(r, HoverFill);
            }

            if (editable && i.Button(IdBagRow + n, r))
            {
                // Put in or taken out: the whole new offer replaces the old one.
                bool full = !offered && trade.MyCount >= OfferRows;
                if (!full)
                {
                    intents!.Enqueue(new TradeOfferIntent(
                        offered ? OfferedIds(trade, item.Item.ItemInstanceId) : OfferedIdsPlus(trade, item.Item.ItemInstanceId),
                        trade.MyGold));
                }
            }

            DrawFitted(c, l, r, item.Item.DisplayName ?? string.Empty, ItemTierStyle.ColorOf(item.Tier));
        }

        if (shown == 0)
        {
            InventoryWindow.DrawTextCentered(c, l.FromUnits(win, RowX, BagTop, RowW, RowH), "Nothing to offer", ItemTierStyle.Dim);
        }

        // ---- the three buttons ----
        float third = (RowW - 4f) / 3f;
        bool canLock = trade.Stage == TradeHudStage.Open && !trade.MyLocked && live;
        bool canConfirm = trade.MyLocked && trade.TheirLocked && !trade.MyConfirmed && live;
        if (Button(c, i, s, l, win, IdLock, RowX, ButtonTop, third, 16f, trade.MyLocked ? "Locked" : "Lock", canLock, live) && canLock)
        {
            intents!.Enqueue(new TradeLockIntent());
        }

        if (Button(c, i, s, l, win, IdConfirm, RowX + third + 2f, ButtonTop, third, 16f, trade.MyConfirmed ? "Done" : "Confirm", canConfirm, live) && canConfirm)
        {
            intents!.Enqueue(new TradeConfirmIntent());
        }

        if (Button(c, i, s, l, win, IdCancel, RowX + ((third + 2f) * 2f), ButtonTop, third, 16f, "Cancel", live, live) && live)
        {
            intents!.Enqueue(new TradeCancelIntent());
        }
    }

    // A step keeps the gold in 0..wallet; a step that changes nothing is dark.
    private static bool StepAllowed(long current, long step, long wallet)
    {
        long target = Math.Clamp(current + step, 0L, Math.Min(wallet, InventoryConstants.MaxGold));
        return target != current;
    }

    private static long[] OfferedIds(TradeHudState trade, long excluding)
    {
        List<long> ids = new(TradeHudState.MaxLines);
        for (int n = 0; n < trade.MyCount; n++)
        {
            long id = trade.MineAt(n).ItemInstanceId;
            if (id != excluding)
            {
                ids.Add(id);
            }
        }

        return [.. ids];
    }

    private static long[] OfferedIdsPlus(TradeHudState trade, long added)
    {
        long[] ids = new long[trade.MyCount + 1];
        for (int n = 0; n < trade.MyCount; n++)
        {
            ids[n] = trade.MineAt(n).ItemInstanceId;
        }

        ids[trade.MyCount] = added;
        return ids;
    }

    // The bag items the list offers: every item with an anchor that is not worn.
    internal static int CountOfferable(HudView v)
    {
        int count = 0;
        for (int anchor = 0; anchor < HudView.BackpackSlots && anchor < v.Backpack.Length; anchor++)
        {
            if (IsOfferRow(v, anchor))
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
            if (!IsOfferRow(v, anchor))
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

    private static bool IsOfferRow(HudView v, int anchor) =>
        v.Backpack[anchor].Item.ItemInstanceId != 0
        && !v.Backpack[anchor].Equipped
        && v.BagAnchorAt(anchor) == anchor;

    private static string StatusText(TradeHudState trade)
    {
        if (trade.MyConfirmed)
        {
            return "Confirmed. Waiting for them";
        }

        if (trade.MyLocked && trade.TheirLocked)
        {
            return trade.TheirConfirmed ? "They confirmed. Your move" : "Both locked. Confirm to trade";
        }

        if (trade.MyLocked)
        {
            return "You locked. Waiting for them";
        }

        return trade.TheirLocked ? "They locked their offer" : "Choose what to offer";
    }

    private static UiColor StatusColor(TradeHudState trade)
        => trade.MyLocked || trade.TheirLocked ? GoldColor : ItemTierStyle.Dim;

    private static ReadOnlySpan<char> TitleText(TradeHudState trade)
    {
        string partner = trade.Partner;
        if (string.IsNullOrEmpty(partner))
        {
            return "Trade";
        }

        return partner.AsSpan(0, Math.Min(partner.Length, TradeHudState.MaxPartnerChars));
    }

    private static void DrawGoldStrip(IUiCanvas c, in UiLayout l, UiRect win, HudView v)
    {
        Span<char> mem = stackalloc char[32];
        UiTextBuilder b = new(mem);
        b.Append("Gold ");
        b.Append(ClampToInt(v.Inventory?.Gold ?? 0));
        InventoryWindow.DrawTextCentered(c, l.FromUnits(win, 35f, 20f, 119f, 8f), b.Text, GoldColor);
    }

    // "120 gold" at the right end of a head line, with a bar of metal under it once that side locked.
    private static void DrawRightGold(IUiCanvas c, in UiLayout l, UiRect win, float headTop, long gold, bool locked)
    {
        Span<char> mem = stackalloc char[40];
        UiTextBuilder b = new(mem);
        b.Append(ClampToInt(gold));
        b.Append(locked ? " gold [locked]" : " gold");
        UiRect r = l.FromUnits(win, RowX, headTop, RowW, RowH);
        float w = c.MeasureText(b.Text, UiTextStyle.Body);
        InventoryWindow.DrawText(c, b.Text, new Vector2(r.Right - w, r.Y), gold > 0 ? GoldColor : ItemTierStyle.Dim);
    }

    private static void DrawFitted(IUiCanvas c, in UiLayout l, UiRect r, string text, UiColor color)
    {
        float inset = 2f * l.Scale;
        ReadOnlySpan<char> shown = text.AsSpan(0, Math.Min(text.Length, 128));
        shown = shown[..PanelText.FitLength(c, shown, UiTextStyle.Body, r.W - (2f * inset))];
        InventoryWindow.DrawText(c, shown, new Vector2(r.X + inset, r.Y + MathF.Floor((r.H - InventoryWindow.BodyHeight(c)) * 0.5f)), color);
    }

    // A plate button with a centred label. Returns true on a click (the caller checks enabled and live).
    private static bool Button(
        IUiCanvas c,
        IUiInput i,
        UiSpriteTable s,
        in UiLayout l,
        UiRect win,
        int id,
        float x,
        float y,
        float w,
        float h,
        string label,
        bool enabled,
        bool live)
    {
        UiRect r = l.FromUnits(win, x, y, w, h);
        DrawRowPlate(c, s, r);
        bool hovered = enabled && live && i.Hovered(id, r);
        if (hovered)
        {
            c.Solid(r, HoverFill);
        }

        InventoryWindow.DrawTextCentered(c, r, label, enabled ? ItemTierStyle.Title : ItemTierStyle.Dim);
        return enabled && live && i.Button(id, r);
    }

    private static void DrawRowPlate(IUiCanvas c, UiSpriteTable s, UiRect r)
    {
        if (s.TryGet(UiSpriteNames.WinLineBox, out UiSprite box))
        {
            c.NineSlice(box, r, White);
        }
    }

    private static void DrawClose(IUiCanvas c, IUiInput i, in UiLayout l, UiSpriteTable s, UiRect win, bool live, HudIntentQueue? intents)
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
            intents!.Enqueue(new TradeCancelIntent());
        }
    }

    private static Vector2 HeadPos(in UiLayout l, UiRect win, float topUnits)
    {
        UiRect r = l.FromUnits(win, RowX, topUnits, RowW, RowH);
        return new Vector2(r.X, r.Y);
    }

    private static int ClampToInt(long value) => (int)Math.Clamp(value, 0L, int.MaxValue);
}
