using Dream.Rendering.Ui.Panels;

namespace Dream.Rendering.Ui;

// The classic HUD's z-order. Frozen after C0: workstreams fill the panels, not this call order.
public static class ClassicHud
{
    // The current play scene's chat state; each PlayScene binds a fresh one when it is built.
    public static ChatHudState? Chat { get; set; }

    public static void Build(IUiCanvas c, IUiInput i, HudView v, in UiLayout l, UiSpriteTable s, IHudActions a)
    {
        WorldLabelsPanel.Build(c, v, l);
        CombatTextPanel.Build(c, v, l, s);
        CharacterWindow.Build(c, i, v, l, s, a);
        InventoryWindow.Build(c, i, v, l, s, a);
        StatusBarPanel.Build(c, i, v, l, s, a);
        NoticePanel.Build(c, v, l);
        ChatPanel.Build(c, v, l, Chat);
        ItemOverlays.Build(c, i, v, l, s, a);   // tooltip, context menu, confirm, drag ghost
        CursorPanel.Build(c, i, v, l, s);
    }
}
