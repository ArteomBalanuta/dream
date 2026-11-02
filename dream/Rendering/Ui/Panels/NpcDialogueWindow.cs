using Dream.Multiplayer.Protocol;
using Dream.Rendering.Hud;
using OpenTK.Mathematics;

namespace Dream.Rendering.Ui.Panels;

// The quest giver's dialogue window (M6): the stone frame of the character window, in its place while the
// dialogue is open. The giver's name in the title, what it says, the quests it offers (a click selects one),
// the selected quest's story, its reward and its objectives, and one button for what the quest allows:
// Accept, Abandon (in progress) or Turn in (ready). Every click only enqueues an intent for the scene to
// send; the server checks the NPC, the range and the quest. Server text arrives cleaned and clamped by
// QuestHudState and is drawn as plain spans (a '%' or '{' stays literal).
public static class NpcDialogueWindow
{
    private const float RowX = 19f;
    private const float RowW = 152f;
    private const float RowH = 12f;
    private const float RowPitch = 13f;
    private const float LinePitch = 12f;

    private const float GreetingTop = 40f;
    private const int GreetingLines = 6;
    private const float HeadTop = 114f;
    private const float OfferTop = 127f;
    private const float DescriptionTop = 212f;
    private const int DescriptionLines = 6;
    private const float RewardTop = 288f;
    private const int RewardLines = 2;
    private const float ObjectiveTop = 316f;
    private const float ActionTop = 372f;

    // Clear of the inventory (0x100..0x502), the character window and the vendor window (0x600..0x643).
    internal const int IdOfferRow = InventoryWindow.IdBase + 0x7A0;
    internal const int IdAction = InventoryWindow.IdBase + 0x7B0;
    internal const int IdClose = InventoryWindow.IdBase + 0x7B1;

    private static readonly UiColor GoldColor = new(0xD8, 0xB4, 0x5A, 255);
    private static readonly UiColor ReadyColor = new(0x5F, 0xD6, 0xA0, 255);
    private static readonly UiColor HoverFill = new(0xF5, 0xDD, 0xA5, 40);
    private static readonly UiColor SelectFill = new(0x8A, 0x1E, 0x1E, 110);
    private static readonly UiColor White = UiColor.White;

    public static void Build(
        IUiCanvas c,
        IUiInput i,
        HudView v,
        in UiLayout l,
        UiSpriteTable s,
        IHudActions a,
        QuestHudState? quests,
        HudIntentQueue? intents)
    {
        if (!v.InventoryOpen || quests is not { DialogueOpen: true })
        {
            return;
        }

        UiRect win = l.Character;
        bool live = !InventoryUiState.Current.ModalOpen;
        InventoryWindow.DrawFrame(c, s, l, win, inventoryKind: false);

        // Pass one: the plates, the washes and every click. Pass two: the text.
        IReadOnlyList<QuestHudOffer> offers = quests.Offers;
        int rows = Math.Min(offers.Count, QuestHudState.MaxOffers);
        for (int n = 0; n < rows; n++)
        {
            UiRect r = l.FromUnits(win, RowX, OfferTop + (n * RowPitch), RowW, RowH);
            DrawRowPlate(c, s, r);
            if (n == quests.SelectedOffer)
            {
                c.Solid(r, SelectFill);
            }
            else if (live && i.Hovered(IdOfferRow + n, r))
            {
                c.Solid(r, HoverFill);
            }

            if (live && i.Button(IdOfferRow + n, r))
            {
                quests.Select(n);
            }
        }

        QuestHudOffer? selected = (uint)quests.SelectedOffer < (uint)offers.Count ? offers[quests.SelectedOffer] : null;
        UiRect action = l.FromUnits(win, RowX, ActionTop, RowW, 16f);
        string? actionText = selected == null ? null : ActionText(selected.State);
        DrawRowPlate(c, s, action);
        if (selected != null && actionText != null)
        {
            if (live && i.Hovered(IdAction, action))
            {
                c.Solid(action, HoverFill);
            }

            if (live && i.Button(IdAction, action) && intents != null)
            {
                intents.Enqueue(IntentFor(quests.NpcActorId, selected));
            }
        }

        if (DrawClose(c, i, l, s, win, live, quests))
        {
            // The click closed the dialogue: the state it drew from is gone, so the frame ends here.
            return;
        }

        // ---- text ----
        InventoryWindow.DrawTextCentered(c, l.FromUnits(win, 35f, 8f, 120f, 8f), TitleText(quests.NpcName), ItemTierStyle.Title);
        InventoryWindow.DrawTextCentered(c, l.FromUnits(win, 35f, 20f, 119f, 8f), "Quest giver", GoldColor);

        UiRect greeting = l.FromUnits(win, RowX, GreetingTop, RowW, LinePitch);
        DrawWrapped(c, quests.Greeting, greeting.X, greeting.Y, greeting.W, LinePitch * l.Scale, GreetingLines, ItemTierStyle.Body);

        UiRect head = l.FromUnits(win, RowX, HeadTop, RowW, RowH);
        InventoryWindow.DrawText(c, offers.Count == 0 ? "Nothing to ask of you" : "Tasks", new Vector2(head.X, head.Y), ItemTierStyle.Dim);

        for (int n = 0; n < rows; n++)
        {
            QuestHudOffer offer = offers[n];
            UiRect r = l.FromUnits(win, RowX, OfferTop + (n * RowPitch), RowW, RowH);
            DrawOfferRow(c, r, offer);
        }

        if (selected != null)
        {
            UiRect description = l.FromUnits(win, RowX, DescriptionTop, RowW, LinePitch);
            DrawWrapped(c, selected.Description, description.X, description.Y, description.W, LinePitch * l.Scale, DescriptionLines, ItemTierStyle.Body);

            if (selected.RewardText.Length > 0)
            {
                UiRect reward = l.FromUnits(win, RowX, RewardTop, RowW, LinePitch);
                DrawWrapped(c, "Reward: " + selected.RewardText, reward.X, reward.Y, reward.W, LinePitch * l.Scale, RewardLines, GoldColor);
            }

            DrawObjectives(c, l, win, quests, selected);
        }

        InventoryWindow.DrawTextCentered(c, action, actionText ?? string.Empty, selected?.State == QuestOfferState.ReadyToTurnIn ? ReadyColor : ItemTierStyle.Title);
    }

    private static void DrawObjectives(IUiCanvas c, in UiLayout l, UiRect win, QuestHudState quests, QuestHudOffer selected)
    {
        QuestHudEntry? entry = null;
        foreach (QuestHudEntry candidate in quests.Entries)
        {
            if (string.Equals(candidate.Key, selected.Key, StringComparison.Ordinal))
            {
                entry = candidate;
                break;
            }
        }

        if (entry == null)
        {
            return;
        }

        for (int n = 0; n < entry.Objectives.Length; n++)
        {
            QuestHudObjective objective = entry.Objectives[n];
            UiRect r = l.FromUnits(win, RowX, ObjectiveTop + (n * LinePitch), RowW, LinePitch);
            DrawObjectiveLine(c, r, objective, objective.Have >= objective.Need ? ReadyColor : ItemTierStyle.Body);
        }
    }

    // Text on the left, "have/need" on the right; the text gives way to the count.
    internal static void DrawObjectiveLine(IUiCanvas c, UiRect r, QuestHudObjective objective, UiColor color)
    {
        Span<char> mem = stackalloc char[24];
        UiTextBuilder count = new(mem);
        count.Append(objective.Have);
        count.Append("/");
        count.Append(objective.Need);
        float countW = c.MeasureText(count.Text, UiTextStyle.Body);
        float y = r.Y + MathF.Floor((r.H - InventoryWindow.BodyHeight(c)) * 0.5f);
        InventoryWindow.DrawText(c, count.Text, new Vector2(r.Right - countW, y), color);
        ReadOnlySpan<char> text = objective.Text;
        text = text[..PanelText.FitLength(c, text, UiTextStyle.Body, MathF.Max(0f, r.W - countW - 6f))];
        InventoryWindow.DrawText(c, text, new Vector2(r.X, y), color);
    }

    private static void DrawOfferRow(IUiCanvas c, UiRect r, QuestHudOffer offer)
    {
        (string tag, UiColor tagColor) = offer.State switch
        {
            QuestOfferState.Available => ("New", GoldColor),
            QuestOfferState.InProgress => ("Active", ItemTierStyle.Dim),
            QuestOfferState.ReadyToTurnIn => ("Ready", ReadyColor),
            _ => ("Done", ItemTierStyle.Dim),
        };
        float inset = MathF.Max(2f, r.H * 0.15f);
        float tagW = c.MeasureText(tag, UiTextStyle.Body);
        float y = r.Y + MathF.Floor((r.H - InventoryWindow.BodyHeight(c)) * 0.5f);
        InventoryWindow.DrawText(c, tag, new Vector2(r.Right - inset - tagW, y), tagColor);
        ReadOnlySpan<char> title = offer.Title;
        title = title[..PanelText.FitLength(c, title, UiTextStyle.Body, MathF.Max(0f, r.W - (3f * inset) - tagW))];
        InventoryWindow.DrawText(c, title, new Vector2(r.X + inset, y), offer.State == QuestOfferState.Completed ? ItemTierStyle.Dim : ItemTierStyle.Body);
    }

    private static string? ActionText(QuestOfferState state) => state switch
    {
        QuestOfferState.Available => "Accept",
        QuestOfferState.InProgress => "Abandon",
        QuestOfferState.ReadyToTurnIn => "Turn in",
        _ => null,
    };

    private static IPlayerActionPayload IntentFor(int npcActorId, QuestHudOffer offer) => offer.State switch
    {
        QuestOfferState.Available => new QuestAcceptIntent(npcActorId, offer.Key),
        QuestOfferState.ReadyToTurnIn => new QuestTurnInIntent(npcActorId, offer.Key),
        _ => new QuestAbandonIntent(offer.Key),
    };

    // Word-wrapped plain text, at most maxLines lines of lineHeight px. The last line is cut, not marked.
    internal static int DrawWrapped(IUiCanvas c, ReadOnlySpan<char> text, float x, float y, float width, float lineHeight, int maxLines, UiColor color)
    {
        int lines = 0;
        int start = 0;
        while (start < text.Length && lines < maxLines)
        {
            while (start < text.Length && text[start] == ' ')
            {
                start++;
            }

            ReadOnlySpan<char> rest = text[start..];
            if (rest.Length == 0)
            {
                break;
            }

            int fit = PanelText.FitLength(c, rest, UiTextStyle.Body, width);
            if (fit <= 0)
            {
                break;
            }

            int length = fit;
            if (fit < rest.Length)
            {
                int space = rest[..fit].LastIndexOf(' ');
                if (space > 0)
                {
                    length = space;
                }
            }

            float top = y + (lines * lineHeight) + MathF.Floor((lineHeight - InventoryWindow.BodyHeight(c)) * 0.5f);
            InventoryWindow.DrawText(c, rest[..length], new Vector2(x, top), color);
            start += length;
            lines++;
        }

        return lines;
    }

    private static void DrawRowPlate(IUiCanvas c, UiSpriteTable s, UiRect r)
    {
        if (s.TryGet(UiSpriteNames.WinLineBox, out UiSprite box))
        {
            c.NineSlice(box, r, White);
        }
    }

    private static bool DrawClose(IUiCanvas c, IUiInput i, in UiLayout l, UiSpriteTable s, UiRect win, bool live, QuestHudState quests)
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
            quests.CloseDialogue();
            return true;
        }

        return false;
    }

    private static ReadOnlySpan<char> TitleText(string? title)
        => string.IsNullOrEmpty(title) ? "Warden" : title.AsSpan(0, Math.Min(title.Length, QuestHudState.MaxTitleChars));
}
