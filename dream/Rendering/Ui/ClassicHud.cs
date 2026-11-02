using Dream.Rendering.Hud;
using Dream.Rendering.Ui.Panels;

namespace Dream.Rendering.Ui;

// The classic HUD's z-order. Frozen after C0: workstreams fill the panels, not this call order.
public static class ClassicHud
{
    // The current play scene's chat state; each PlayScene binds a fresh one when it is built.
    public static ChatHudState? Chat { get; set; }

    // M5 and M6. The current play scene binds these every frame (PlayScene.UpdateMultiplayerHud), because
    // the render path reaches the HUD only through ClassicHud. Clicks go to Intents, which the scene
    // drains into player actions.
    public static PartyHudState? Party { get; set; }

    public static TradeHudState? Trade { get; set; }

    public static QuestHudState? Quests { get; set; }

    public static HudIntentQueue? Intents { get; set; }

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
        QuestLogPanel.Build(c, v, l, Quests);
        NoticePanel.Build(c, v, l);
        ChatPanel.Build(c, v, l, Chat);
        ItemOverlays.Build(c, i, v, l, s, a);   // tooltip, context menu, confirm, drag ghost
        CursorPanel.Build(c, i, v, l, s);
    }
}
