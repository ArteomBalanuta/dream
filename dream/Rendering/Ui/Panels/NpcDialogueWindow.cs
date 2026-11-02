using Dream.Rendering.Hud;

namespace Dream.Rendering.Ui.Panels;

// The quest giver's dialogue window (M6 workstream B): the greeting and the quests to accept or turn in.
// Takes the character window's place while it is open. Contract stub: draws nothing yet.
public static class NpcDialogueWindow
{
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
    }
}
