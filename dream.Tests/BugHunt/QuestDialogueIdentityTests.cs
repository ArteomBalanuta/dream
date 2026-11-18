using Dream.Multiplayer.Protocol;
using Dream.Rendering.Hud;
using Dream.Rendering.Ui;
using Dream.Rendering.Ui.Panels;
using Dream.Tests.Rendering.Ui.Panels;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.BugHunt;

// BH-QUEST-1: the dialogue's single action button must send the action it was drawn from, FOR THE QUEST it
// was drawn for. A dialogue re-open between the press and the release (E is not gated on the dialogue being
// open) rebuilds the offers and resets the selection to index 0, so a same-state offer at index 0 used to
// take the press of another quest's button — an abandon that deletes the wrong quest's stored progress.
// Driven through the real UiInputRouter frame by frame, so no GL context and no window are involved.
public sealed class QuestDialogueIdentityTests : IDisposable
{
    private readonly InventoryScene _scene = new();
    private readonly QuestHudState _quests = new();
    private readonly HudIntentQueue _intents = new();
    private readonly UiInputRouter _router = new();

    public void Dispose() => _scene.Dispose();

    private static QuestOfferLine Offer(string key, QuestOfferState state) =>
        new(key, "Title " + key, "A story about " + key + ".", "100 XP", state);

    private UiRect ActionButton() => _scene.Layout.FromUnits(_scene.Layout.Character, 19f, 372f, 152f, 16f);

    private void DialogueFrame(Vector2 pointer, bool leftDown)
    {
        _router.BeginFrame(new UiPointer(pointer, leftDown, false, true));
        NpcDialogueWindow.Build(
            _scene.Canvas, _router, _scene.View, _scene.Layout, _scene.Table, _scene.Actions, _quests, _intents);
        _router.EndFrame();
    }

    private void PressAndRelease(Action? betweenPressAndRelease = null)
    {
        Vector2 at = InventoryScene.Centre(ActionButton());
        DialogueFrame(at, leftDown: false);
        DialogueFrame(at, leftDown: true);
        betweenPressAndRelease?.Invoke();
        DialogueFrame(at, leftDown: false);
    }

    // Two offers in the same state, as the shipped content allows (every quest has giver gate-warden).
    private static QuestOfferLine[] TwoInProgress() =>
    [
        Offer("wolf-cull", QuestOfferState.InProgress),
        Offer("marked-for-the-dark", QuestOfferState.InProgress),
    ];

    // The defect: pressing "Abandon" on offer 1, then re-opening the dialogue (which resets the selection
    // to offer 0, in the SAME state), used to release as an abandon of offer 0.
    [Fact]
    public void ADialogueReopenBetweenPressAndRelease_SendsNothing_NotAnActionAgainstAnotherQuest()
    {
        _quests.OpenDialogue(900, "Gate Warden", "Keep your voice low.", TwoInProgress());
        _quests.Select(1);

        PressAndRelease(() => _quests.OpenDialogue(900, "Gate Warden", "Keep your voice low.", TwoInProgress()));

        Assert.Equal(0, _quests.SelectedOffer);
        Assert.Equal("wolf-cull", _quests.Offers[0].Key);
        Assert.Equal(0, _intents.Count);
    }

    // The same route with Available offers: an unintended accept is dropped too.
    [Fact]
    public void ADialogueReopenBetweenPressAndRelease_SendsNoAcceptForTheOtherQuest()
    {
        QuestOfferLine[] offers =
        [
            Offer("wolf-cull", QuestOfferState.Available),
            Offer("torch-supply", QuestOfferState.Available),
        ];
        _quests.OpenDialogue(900, "Gate Warden", "Keep your voice low.", offers);
        _quests.Select(1);

        PressAndRelease(() => _quests.OpenDialogue(900, "Gate Warden", "Keep your voice low.", offers));

        Assert.Equal(0, _intents.Count);
    }

    // A re-open that brings a different NPC with a quest of the same key and state at index 0 is also a
    // different identity: the NPC actor id is part of the button's id.
    [Fact]
    public void AReopenOnAnotherNpc_SendsNothing()
    {
        _quests.OpenDialogue(900, "Gate Warden", "Keep your voice low.", [Offer("wolf-cull", QuestOfferState.InProgress)]);

        PressAndRelease(() => _quests.OpenDialogue(
            901, "Other Warden", "Move along.", [Offer("wolf-cull", QuestOfferState.InProgress)]));

        Assert.Equal(0, _intents.Count);
    }

    // Control: a re-open that leaves the same quest of the same NPC selected is the same identity, so the
    // click still lands. Nothing about the fix drops a press whose identity did not change.
    [Fact]
    public void AReopenThatKeepsTheSameSelection_StillSendsTheAction()
    {
        _quests.OpenDialogue(900, "Gate Warden", "Keep your voice low.", [Offer("wolf-cull", QuestOfferState.InProgress)]);

        PressAndRelease(() => _quests.OpenDialogue(
            900, "Gate Warden", "Keep your voice low.", [Offer("wolf-cull", QuestOfferState.InProgress)]));

        Assert.True(_intents.TryDequeue(out IPlayerActionPayload payload));
        Assert.Equal(new QuestAbandonIntent("wolf-cull"), payload);
    }

    // Control: the ordinary accept, abandon and turn-in all still reach the queue unchanged.
    [Theory]
    [InlineData(QuestOfferState.Available)]
    [InlineData(QuestOfferState.InProgress)]
    [InlineData(QuestOfferState.ReadyToTurnIn)]
    public void AnUndisturbedClick_SendsTheActionItShowed(QuestOfferState state)
    {
        _quests.OpenDialogue(900, "Gate Warden", "Keep your voice low.", [Offer("a", QuestOfferState.Available), Offer("b", state)]);
        _quests.Select(1);

        PressAndRelease();

        Assert.True(_intents.TryDequeue(out IPlayerActionPayload payload));
        IPlayerActionPayload expected = state switch
        {
            QuestOfferState.Available => new QuestAcceptIntent(900, "b"),
            QuestOfferState.ReadyToTurnIn => new QuestTurnInIntent(900, "b"),
            _ => new QuestAbandonIntent("b"),
        };
        Assert.Equal(expected, payload);
    }

    // The id binds all three parts of the identity, and its band is clear of every other HUD id.
    [Fact]
    public void TheActionId_BindsStateNpcAndQuestKey_InABandOfItsOwn()
    {
        int baseline = NpcDialogueWindow.ActionId(QuestOfferState.InProgress, 900, "wolf-cull");

        Assert.NotEqual(baseline, NpcDialogueWindow.ActionId(QuestOfferState.ReadyToTurnIn, 900, "wolf-cull"));
        Assert.NotEqual(baseline, NpcDialogueWindow.ActionId(QuestOfferState.InProgress, 901, "wolf-cull"));
        Assert.NotEqual(baseline, NpcDialogueWindow.ActionId(QuestOfferState.InProgress, 900, "marked-for-the-dark"));
        Assert.Equal(baseline, NpcDialogueWindow.ActionId(QuestOfferState.InProgress, 900, "wolf-cull"));

        Assert.InRange(
            baseline,
            NpcDialogueWindow.IdActionBand,
            NpcDialogueWindow.IdActionBand + NpcDialogueWindow.IdActionBandMask);
        Assert.True(NpcDialogueWindow.IdActionBand > NpcDialogueWindow.IdClose);
        Assert.True(NpcDialogueWindow.IdActionBand > InventoryWindow.IdBase + 0x1000);
    }
}
