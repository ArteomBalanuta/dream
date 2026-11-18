using Dream.Multiplayer.Protocol;
using Dream.Rendering.Hud;
using Dream.Rendering.Ui;
using Dream.Rendering.Ui.Panels;
using Dream.Tests.Rendering.Ui.Panels;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.BugHunt;

// BH-HUD-2: the quest dialogue's single action button stands for three mutually exclusive actions
// chosen from live server state. A ServerQuestLog push between the press and the release must never
// redirect the press to another action. Driven through the real UiInputRouter frame by frame, so no
// GL context and no window are involved.
public sealed class HudQuestDialogueActionTests : IDisposable
{
    private readonly InventoryScene _scene = new();
    private readonly QuestHudState _quests = new();
    private readonly HudIntentQueue _intents = new();
    private readonly UiInputRouter _router = new();

    public void Dispose() => _scene.Dispose();

    private static QuestOfferLine Offer(string key, QuestOfferState state) =>
        new(key, "Title " + key, "A story about " + key + ".", "100 XP", state);

    private UiRect ActionButton() => _scene.Layout.FromUnits(_scene.Layout.Character, 19f, 372f, 152f, 16f);

    // One dialogue frame through the real router: the pointer, whether the left button is down, and
    // then the panel, as ClassicHudSurface runs it.
    private void DialogueFrame(Vector2 pointer, bool leftDown)
    {
        _router.BeginFrame(new UiPointer(pointer, leftDown, false, true));
        NpcDialogueWindow.Build(
            _scene.Canvas, _router, _scene.View, _scene.Layout, _scene.Table, _scene.Actions, _quests, _intents);
        _router.EndFrame();
    }

    // Hover, press, release over the action button, with the push landing after the press frame.
    private void PressAndRelease(Action? betweenPressAndRelease = null)
    {
        Vector2 at = InventoryScene.Centre(ActionButton());
        DialogueFrame(at, leftDown: false);
        DialogueFrame(at, leftDown: true);
        betweenPressAndRelease?.Invoke();
        DialogueFrame(at, leftDown: false);
    }

    [Fact]
    public void TheActionButton_SendsWhatItShowed_WhenNothingChanges()
    {
        _quests.OpenDialogue(900, "Gate Warden", "Keep your voice low.", [Offer("a", QuestOfferState.ReadyToTurnIn)]);

        PressAndRelease();

        Assert.True(_intents.TryDequeue(out IPlayerActionPayload payload));
        Assert.Equal(new QuestTurnInIntent(900, "a"), payload);
    }

    // The destructive case: a collect objective counts bag contents, so selling or dropping one of the
    // counted items makes Ready fall back to InProgress. The release frame then draws "Abandon" over
    // the button the player pressed as "Turn in". Nothing may be sent.
    [Fact]
    public void AQuestLogPushBetweenPressAndRelease_SendsNothing_NotAnAbandon()
    {
        _quests.OpenDialogue(900, "Gate Warden", "Keep your voice low.", [Offer("a", QuestOfferState.ReadyToTurnIn)]);

        PressAndRelease(() => _quests.SetLog(
        [
            new QuestLogEntry("a", "Title a", QuestState.Active, [new QuestObjectiveLine("Bring Pelts", 1, 2)]),
        ]));

        Assert.Equal(QuestOfferState.InProgress, _quests.Offers[0].State);
        Assert.Equal(0, _intents.Count);
    }

    // The same rule in the benign direction: a press on "Abandon" must not turn into a turn-in either.
    [Fact]
    public void AnOfferThatBecomesReadyBetweenPressAndRelease_SendsNothing()
    {
        _quests.OpenDialogue(900, "Gate Warden", "Keep your voice low.", [Offer("a", QuestOfferState.InProgress)]);

        PressAndRelease(() => _quests.SetLog(
        [
            new QuestLogEntry("a", "Title a", QuestState.Active, [new QuestObjectiveLine("Bring Pelts", 2, 2)]),
        ]));

        Assert.Equal(QuestOfferState.ReadyToTurnIn, _quests.Offers[0].State);
        Assert.Equal(0, _intents.Count);
    }

    // The four action ids are distinct and clear of the buttons beside them.
    [Fact]
    public void EveryOfferState_HasItsOwnActionId_AndNoneCollides()
    {
        int[] ids =
        [
            NpcDialogueWindow.ActionId(QuestOfferState.Available),
            NpcDialogueWindow.ActionId(QuestOfferState.InProgress),
            NpcDialogueWindow.ActionId(QuestOfferState.ReadyToTurnIn),
            NpcDialogueWindow.ActionId(QuestOfferState.Completed),
        ];

        Assert.Equal(4, ids.Distinct().Count());
        Assert.DoesNotContain(NpcDialogueWindow.IdClose, ids);
        Assert.DoesNotContain(NpcDialogueWindow.IdOfferRow, ids);
    }
}
