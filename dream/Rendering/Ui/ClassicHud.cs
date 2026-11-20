using Dream.Rendering.Hud;
using Dream.Rendering.Ui.Panels;

namespace Dream.Rendering.Ui;

// The classic HUD's z-order. Frozen after C0: workstreams fill the panels, not this call order.
public static class ClassicHud
{
    // The bindings below are per thread, like InventoryUiState: the game binds them in the scene's update
    // and reads them in the render, both on the one window thread, and a test binds and reads on its
    // own thread. A process-wide static let a PlayScene test that opened a trade or a dialogue replace
    // the character window in an unrelated HUD test running at the same time.
    //
    // The current play scene's chat state; each PlayScene binds a fresh one when it is built.
    public static ChatHudState? Chat { get => _chat; set => _chat = value; }

    // M5 and M6. The current play scene binds these every frame (PlayScene.UpdateMultiplayerHud), because
    // the render path reaches the HUD only through ClassicHud. Clicks go to Intents, which the scene
    // drains into player actions.
    public static PartyHudState? Party { get => _party; set => _party = value; }

    public static TradeHudState? Trade { get => _trade; set => _trade = value; }

    public static QuestHudState? Quests { get => _quests; set => _quests = value; }

    public static HudIntentQueue? Intents { get => _intents; set => _intents = value; }

    [ThreadStatic]
    private static ChatHudState? _chat;

    [ThreadStatic]
    private static PartyHudState? _party;

    [ThreadStatic]
    private static TradeHudState? _trade;

    [ThreadStatic]
    private static QuestHudState? _quests;

    [ThreadStatic]
    private static HudIntentQueue? _intents;

    // Forget every binding on this thread. A thread-pool thread outlives the test that ran on it, so a HUD test
    // calls this first, like InventoryUiState.Reset, rather than inherit a trade a PlayScene test left bound.
    public static void Reset()
    {
        _chat = null;
        _party = null;
        _trade = null;
        _quests = null;
        _intents = null;
        VendorFxState.Reset();
    }

    public static void Build(IUiCanvas c, IUiInput i, HudView v, in UiLayout l, UiSpriteTable s, IHudActions a)
    {
        WorldLabelsPanel.Build(c, i, v, l, a);
        CombatTextPanel.Build(c, v, l, s);
        // The vendor window takes the character window's place while it is open (M3); so do the trade
        // window (M5) and the quest giver's dialogue (M6), in that order.
        if (v.Vendor is { IsOpen: true })
        {
            VendorWindow.Build(c, i, v, l, s, a);
        }
        else if (Trade is { IsOpen: true })
        {
            TradeWindow.Build(c, i, v, l, s, a, Trade, Intents);
        }
        else if (Quests is { DialogueOpen: true })
        {
            NpcDialogueWindow.Build(c, i, v, l, s, a, Quests, Intents);
        }
        else
        {
            CharacterWindow.Build(c, i, v, l, s, a);
        }

        InventoryWindow.Build(c, i, v, l, s, a);
        StatusBarPanel.Build(c, i, v, l, s, a);
        PartyPanel.Build(c, v, l, Party, Intents);
        QuestLogPanel.Build(c, v, l, Quests, Party);
        NoticePanel.Build(c, v, l);
        ChatPanel.Build(c, v, l, Chat);
        HoverInfoPanel.Build(c, i, v, l, s);    // the hovered world thing; item tooltips stay above it
        ItemOverlays.Build(c, i, v, l, s, a);   // tooltip, context menu, confirm, drag ghost
        CursorPanel.Build(c, i, v, l, s);
    }
}
