using Dream.Rendering.Hud;

namespace Dream.Rendering.Ui.Panels;

// The trade window (M5 workstream A): both offers, gold entry, Lock, Confirm and Cancel. Takes the
// character window's place while a trade is open. Contract stub: draws nothing yet.
public static class TradeWindow
{
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
    }
}
