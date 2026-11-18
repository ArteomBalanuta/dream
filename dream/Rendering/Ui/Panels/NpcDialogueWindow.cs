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
    private const float ButtonH = 16f;

    // Text sizes in window units (1 u = Scale px); the boxes are the art's, so each is capped by its box.
    // The prose is wrapped, so its block is capped by the room left under it as well as by its line count.
    internal const float RowTextUnits = 9.5f;
    internal const float HeadTextUnits = 8f;
    internal const float ProseTextUnits = 8.5f;
    internal const float ButtonTextUnits = 9.5f;

    // Clear of the inventory (0x100..0x502), the character window and the vendor window (0x600..0x643).
    internal const int IdOfferRow = InventoryWindow.IdBase + 0x7A0;

    // Four seeds, one per QuestOfferState (0x7B0..0x7B3): the action button's id carries the state the
    // button was drawn from. The one button stands for three mutually exclusive, non-equivalent actions
    // chosen from live server state, and a ServerQuestLog push can rewrite that state between the press
    // and the release (a collect objective counts bag contents, so Ready can fall back to InProgress).
    // With the state in the id, such a flip makes the router's press id no longer match and the click is
    // dropped, instead of sending the action the release frame computed: a press on "Turn in" can never
    // release as "Abandon".
    internal const int IdAction = InventoryWindow.IdBase + 0x7B0;
    internal const int IdClose = InventoryWindow.IdBase + 0x7B4;

    // BH-QUEST-1: the state alone is not the button's identity. A dialogue re-open (E pressed while the
    // left button is still down) rebuilds the offers and resets SelectedOffer to 0, so the release frame
    // can compute the SAME state id for a DIFFERENT quest and the click is redirected — an abandon of
    // another quest deletes its stored progress. The id therefore carries the whole identity the button
    // was drawn from: the offer state, the NPC it belongs to and the quest key. Anything that changes
    // between the press and the release makes the router's press id no longer match, so the click is
    // dropped (the established failure mode), never redirected.
    //
    // The identity does not fit in an int, so it is hashed into a band of its own, 0x4000_0000..
    // 0x4FFF_FFFF, which no other HUD id uses (every other id is below 0x0100_0000). A stable FNV-1a is
    // used, not string.GetHashCode: that is randomised per process and the id must be the same in the
    // press frame and the release frame of the same run.
    internal const int IdActionBand = 0x4000_0000;
    internal const int IdActionBandMask = 0x0FFF_FFFF;

    // The state seed of the action id; the state's own contribution, kept separate so the four states
    // stay distinguishable on their own.
    internal static int ActionId(QuestOfferState state) => IdAction + (int)state;

    // The id the action button is actually registered with: the state, the NPC and the quest key.
    internal static int ActionId(QuestOfferState state, int npcActorId, ReadOnlySpan<char> questKey) =>
        IdentityOf(ActionId(state), npcActorId, questKey);

    // The id an offer ROW is registered with. The same defect, one block up: a row bound to its
    // POSITION lets a ServerQuestLog push between the press and the release move a different quest
    // under the pointer, and quests.Select(n) then highlights the quest that took the row rather
    // than the one that was pressed. Bounded harm (the action button's own id carries the quest, so
    // it cannot act on the wrong one), but the cure is the established one: the row's id carries
    // the NPC and the quest key, so a row that changed hands drops the click instead of moving the
    // selection. The seed is IdOfferRow, which is distinct from every ActionId(state) seed, so a
    // row and the action button cannot share an id; both land in the 0x4000_0000 band, which no
    // other HUD id uses (ordinary ids are below 0x0100_0000 and HudWidgetId owns 0x5000_0000).
    internal static int OfferRowId(int npcActorId, ReadOnlySpan<char> questKey) =>
        IdentityOf(IdOfferRow, npcActorId, questKey);

    // Pure arithmetic over the seed, the NPC and the key: a per-frame caller allocates nothing.
    private static int IdentityOf(int seed, int npcActorId, ReadOnlySpan<char> questKey)
    {
        uint hash = 2166136261u;
        hash = Fold(hash, (uint)seed);
        hash = Fold(hash, (uint)npcActorId);
        hash = Fold(hash, (uint)questKey.Length);
        for (int i = 0; i < questKey.Length; i++)
        {
            hash = (hash ^ questKey[i]) * 16777619u;
            hash = (hash ^ (uint)(questKey[i] >> 8)) * 16777619u;
        }

        return IdActionBand + (int)(hash & IdActionBandMask);
    }

    private static uint Fold(uint hash, uint value)
    {
        for (int b = 0; b < 4; b++)
        {
            hash = (hash ^ ((value >> (b * 8)) & 0xFF)) * 16777619u;
        }

        return hash;
    }

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
            // The quest this row stands for, not the row's position: see OfferRowId.
            int rowId = OfferRowId(quests.NpcActorId, offers[n].Key);
            if (n == quests.SelectedOffer)
            {
                c.Solid(r, SelectFill);
            }
            else if (live && i.Hovered(rowId, r))
            {
                c.Solid(r, HoverFill);
            }

            if (live && i.Button(rowId, r))
            {
                quests.Select(n);
            }
        }

        QuestHudOffer? selected = (uint)quests.SelectedOffer < (uint)offers.Count ? offers[quests.SelectedOffer] : null;
        UiRect action = l.FromUnits(win, RowX, ActionTop, RowW, ButtonH);
        string? actionText = selected == null ? null : ActionText(selected.State);
        DrawRowPlate(c, s, action);
        if (selected != null && actionText != null)
        {
            int actionId = ActionId(selected.State, quests.NpcActorId, selected.Key);
            if (live && i.Hovered(actionId, action))
            {
                c.Solid(action, HoverFill);
            }

            if (live && i.Button(actionId, action) && intents != null)
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
        float shadow = UiText.ShadowPx(in l);
        var title = InventoryWindow.TitleTextFor(in l);
        var row = UiText.For(in l, RowTextUnits, RowH);
        var head = UiText.For(in l, HeadTextUnits, RowH);
        var prose = UiText.For(in l, ProseTextUnits, LinePitch);
        var button = UiText.For(in l, ButtonTextUnits, ButtonH);
        UiText.PutCentered(c, l.FromUnits(win, 35f, 8f, 120f, 8f), TitleText(quests.NpcName), in title, ItemTierStyle.Title, shadow);
        UiText.PutCentered(c, l.FromUnits(win, 35f, 20f, 119f, 8f), "Quest giver", in title, GoldColor, shadow);

        // The greeting block runs from 40 u and must stop short of the "Tasks" head at 114 u, so it is
        // capped by the room left as well as by its line count.
        UiRect greeting = l.FromUnits(win, RowX, GreetingTop, RowW, LinePitch);
        DrawWrapped(c, in l, quests.Greeting, greeting, l.FromUnits(win, RowX, HeadTop, RowW, RowH).Y, GreetingLines, in prose, ItemTierStyle.Body);

        UiRect headBox = l.FromUnits(win, RowX, HeadTop, RowW, RowH);
        UiText.PutLeft(c, headBox, 0f, offers.Count == 0 ? "Nothing to ask of you" : "Tasks", in head, ItemTierStyle.Dim, shadow);

        for (int n = 0; n < rows; n++)
        {
            QuestHudOffer offer = offers[n];
            UiRect r = l.FromUnits(win, RowX, OfferTop + (n * RowPitch), RowW, RowH);
            DrawOfferRow(c, r, offer, in row, shadow);
        }

        if (selected != null)
        {
            UiRect description = l.FromUnits(win, RowX, DescriptionTop, RowW, LinePitch);
            DrawWrapped(c, in l, selected.Description, description, l.FromUnits(win, RowX, RewardTop, RowW, RowH).Y, DescriptionLines, in prose, ItemTierStyle.Body);

            if (selected.RewardText.Length > 0)
            {
                UiRect reward = l.FromUnits(win, RowX, RewardTop, RowW, LinePitch);
                DrawWrapped(c, in l, "Reward: " + selected.RewardText, reward, l.FromUnits(win, RowX, ObjectiveTop, RowW, RowH).Y, RewardLines, in prose, GoldColor);
            }

            DrawObjectives(c, l, win, quests, selected, in row, shadow);
        }

        UiText.PutCentered(c, action, actionText ?? string.Empty, in button, selected?.State == QuestOfferState.ReadyToTurnIn ? ReadyColor : ItemTierStyle.Title, shadow);
    }

    private static void DrawObjectives(
        IUiCanvas c, in UiLayout l, UiRect win, QuestHudState quests, QuestHudOffer selected, in UiSizedText t, float shadow)
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
            DrawObjectiveLine(c, r, objective, objective.Have >= objective.Need ? ReadyColor : ItemTierStyle.Body, in t, shadow);
        }
    }

    // The size the canvas is already set to, for a caller whose box follows the text height (the quest
    // log plate). A panel whose boxes come from the art passes the size its box allows.
    internal static void DrawObjectiveLine(IUiCanvas c, UiRect r, QuestHudObjective objective, UiColor color) =>
        DrawObjectiveLine(c, r, objective, color, UiText.AtCanvasScale(c), 1f);

    // Text on the left, "have/need" on the right; the text gives way to the count. Measured at the size
    // it draws with: PanelText.FitLength reads the canvas's current scale, which need not be this one.
    internal static void DrawObjectiveLine(
        IUiCanvas c, UiRect r, QuestHudObjective objective, UiColor color, in UiSizedText t, float shadow = 1f)
    {
        Span<char> mem = stackalloc char[24];
        UiTextBuilder count = new(mem);
        count.Append(objective.Have);
        count.Append("/");
        count.Append(objective.Need);
        float countW = UiText.Measure(c, count.Text, in t);
        float y = UiText.TextY(r, in t);
        UiText.Put(c, count.Text, new Vector2(r.Right - countW, y), in t, color, shadow);
        ReadOnlySpan<char> text = objective.Text;
        text = text[..UiText.FitLength(c, text, in t, MathF.Max(0f, r.W - countW - 6f))];
        UiText.Put(c, text, new Vector2(r.X, y), in t, color, shadow);
    }

    private static void DrawOfferRow(IUiCanvas c, UiRect r, QuestHudOffer offer, in UiSizedText t, float shadow)
    {
        (string tag, UiColor tagColor) = offer.State switch
        {
            QuestOfferState.Available => ("New", GoldColor),
            QuestOfferState.InProgress => ("Active", ItemTierStyle.Dim),
            QuestOfferState.ReadyToTurnIn => ("Ready", ReadyColor),
            _ => ("Done", ItemTierStyle.Dim),
        };
        float inset = MathF.Max(2f, r.H * 0.15f);
        float tagW = UiText.Measure(c, tag, in t);
        float y = UiText.TextY(r, in t);
        UiText.Put(c, tag, new Vector2(r.Right - inset - tagW, y), in t, tagColor, shadow);
        ReadOnlySpan<char> title = offer.Title;
        title = title[..UiText.FitLength(c, title, in t, MathF.Max(0f, r.W - (3f * inset) - tagW))];
        UiText.Put(c, title, new Vector2(r.X + inset, y), in t, offer.State == QuestOfferState.Completed ? ItemTierStyle.Dim : ItemTierStyle.Body, shadow);
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

    // Word-wrapped plain text in box (its X, Y and W; its H is one line), at most maxLines lines and
    // never past maxBottom. The pitch comes from the drawn size, not from a unit constant: a 12 u pitch
    // held 13 px glyphs at scale 1 but would overlap 38 px ones at scale 4. The block is also capped by
    // the room above maxBottom, because maxLines of a size that follows ui_scale is not a fixed height
    // and the greeting would otherwise run into the head under it. The last line is cut, not marked.
    internal static int DrawWrapped(
        IUiCanvas c, in UiLayout l, ReadOnlySpan<char> text, UiRect box, float maxBottom, int maxLines,
        in UiSizedText t, UiColor color)
    {
        float shadow = UiText.ShadowPx(in l);
        float pitch = MathF.Round(t.Px + shadow);
        float x = box.X;
        float y = box.Y;
        float width = box.W;
        int room = pitch > 0f ? (int)MathF.Floor((maxBottom - y) / pitch) : 0;
        maxLines = Math.Min(maxLines, Math.Max(0, room));

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

            int fit = UiText.FitLength(c, rest, in t, width);
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

            UiText.Put(c, rest[..length], new Vector2(x, y + (lines * pitch)), in t, color, shadow);
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
