using Dream.Multiplayer.Protocol;
using Dream.Rendering.Hud;
using Dream.Rendering.Ui;

namespace Dream.Client.Scenes;

// M5, M6 and M8 on the client: the seam between the scene and the classic HUD. The render path reaches the
// HUD only through ClassicHud's statics, so the scene binds its states there each frame and drains the
// clicks the HUD queued. The fields are filled by their parameterless constructors in the headless play
// scene, so every one is a state class (a bare List or Dictionary field would stay null there).
public partial class PlayScene
{
    private readonly HudIntentQueue _hudIntents = new();
    private readonly PartyHudState _partyHud = new();
    private readonly TradeHudState _tradeHud = new();
    private readonly QuestHudState _questHud = new();

    // First thing in the frame's global input, typing or not.
    private void UpdateMultiplayerHud()
    {
        ClassicHud.Party = _partyHud;
        ClassicHud.Trade = _tradeHud;
        ClassicHud.Quests = _questHud;
        ClassicHud.Intents = _hudIntents;

        while (_hudIntents.TryDequeue(out IPlayerActionPayload payload))
        {
            SendEconomyIntent(payload);
        }

        UpdatePartyTradeHud();
        UpdateQuestHud();
    }
}
