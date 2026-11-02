using Dream.Multiplayer.Protocol;
using Dream.Rendering.Hud;
using Dream.Rendering.Ui;
using Dream.Rendering.Ui.Panels;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Ui.Panels;

// M6: the quest state the scene fills, the quest giver's dialogue window and the always-on tracker. The
// window draws only with the inventory and a dialogue open, selects a quest on a click, and turns the one
// button into the intent the quest allows (Accept, Abandon, Turn in) in the HUD queue, never anywhere else.
public sealed class QuestPanelsTests : IDisposable
{
    private readonly InventoryScene _scene = new();
    private readonly QuestHudState _quests = new();
    private readonly HudIntentQueue _intents = new();

    public void Dispose() => _scene.Dispose();

    private static QuestOfferLine Line(string key, QuestOfferState state) =>
        new(key, "Title " + key, "A story about " + key + ".", "100 XP, 40 gold", state);

    private void OpenDialogue(params QuestOfferLine[] lines) => _quests.OpenDialogue(900, "Gate Warden", "Keep your voice low.", lines);

    private void WindowFrame()
    {
        _scene.Canvas.Calls.Clear();
        NpcDialogueWindow.Build(_scene.Canvas, _scene.Input, _scene.View, _scene.Layout, _scene.Table, _scene.Actions, _quests, _intents);
        _scene.Input.EndFrame();
    }

    private void Click(UiRect rect)
    {
        _scene.Input.Pointer = InventoryScene.Centre(rect);
        _scene.Input.LeftClick = true;
        WindowFrame();
    }

    private UiRect Units(float x, float y, float w, float h) =>
        _scene.Layout.FromUnits(_scene.Layout.Character, x, y, w, h);

    private UiRect OfferRow(int n) => Units(19f, 127f + (n * 13f), 152f, 12f);

    private UiRect ActionButton() => Units(19f, 372f, 152f, 16f);

    private static ServerQuestLog Log(params QuestLogEntry[] entries) => new(entries);

    private static QuestLogEntry Entry(string key, QuestState state, params QuestObjectiveLine[] objectives) =>
        new(key, "Title " + key, state, objectives);

    [Fact]
    public void TheLog_IsCleanedAndCapped_AndOnlyDefinedStatesAreKept()
    {
        QuestLogEntry[] many = [.. Enumerable.Range(0, ServerQuestLog.MaxEntries + 10).Select(n => Entry("q" + n, QuestState.Active, new QuestObjectiveLine("Slay Wolf", 0, 1)))];
        _quests.SetLog(many);
        Assert.Equal(QuestHudState.MaxEntries, _quests.Entries.Count);

        _quests.SetLog(
        [
            Entry("a\u0007b", QuestState.Active, new QuestObjectiveLine("Slay‮ Wolf", 99, 3), new QuestObjectiveLine("x", -4, 0)),
            Entry("bad-state", (QuestState)9),
            Entry("dup", QuestState.Active),
            Entry("dup", QuestState.Completed),
            Entry("", QuestState.Active),
        ]);

        Assert.Equal(["ab", "dup"], _quests.Entries.Select(e => e.Key));
        QuestHudEntry first = _quests.Entries[0];
        Assert.Equal("Slay Wolf", first.Objectives[0].Text);
        Assert.Equal(3, first.Objectives[0].Have);
        Assert.Equal(3, first.Objectives[0].Need);
        Assert.Equal(0, first.Objectives[1].Have);
        Assert.Equal(1, first.Objectives[1].Need);
        Assert.False(first.Ready);
    }

    [Fact]
    public void TheLog_Objectives_AreCappedPerQuest()
    {
        QuestObjectiveLine[] six = [.. Enumerable.Range(0, 6).Select(n => new QuestObjectiveLine("o" + n, 0, 1))];
        _quests.SetLog([Entry("q", QuestState.Active, six)]);

        Assert.Equal(QuestHudState.MaxObjectives, Assert.Single(_quests.Entries).Objectives.Length);
        Assert.False(_quests.Entries[0].Ready);
    }

    [Fact]
    public void TheDialogue_KeepsAtMostEightOffers_DropsBadLines_AndFollowsTheLog()
    {
        QuestOfferLine[] lines =
        [
            Line("a", QuestOfferState.Available),
            Line("a", QuestOfferState.Available),
            Line("", QuestOfferState.Available),
            Line("bad", (QuestOfferState)9),
            Line("b", QuestOfferState.Available),
            Line("c", QuestOfferState.Completed),
            .. Enumerable.Range(0, 20).Select(n => Line("x" + n, QuestOfferState.Available)),
        ];
        OpenDialogue(lines);

        Assert.True(_quests.DialogueOpen);
        Assert.Equal(QuestHudState.MaxOffers, _quests.Offers.Count);
        Assert.Equal(["a", "b", "c"], _quests.Offers.Take(3).Select(o => o.Key));
        Assert.Equal(0, _quests.SelectedOffer);

        _quests.SetLog([Entry("a", QuestState.Active, new QuestObjectiveLine("Slay Wolf", 0, 1))]);
        Assert.Equal(QuestOfferState.InProgress, _quests.Offers[0].State);

        _quests.SetLog([Entry("a", QuestState.Active, new QuestObjectiveLine("Slay Wolf", 1, 1))]);
        Assert.Equal(QuestOfferState.ReadyToTurnIn, _quests.Offers[0].State);

        _quests.SetLog([Entry("a", QuestState.Completed, new QuestObjectiveLine("Slay Wolf", 1, 1))]);
        Assert.Equal(QuestOfferState.Completed, _quests.Offers[0].State);

        // A dropped quest is open again; a finished one stays finished.
        _quests.SetLog([]);
        Assert.Equal(QuestOfferState.Completed, _quests.Offers[0].State);
        Assert.Equal(QuestOfferState.Completed, _quests.Offers[2].State);
    }

    [Fact]
    public void ADroppedQuest_IsOfferedAgain()
    {
        OpenDialogue(Line("a", QuestOfferState.InProgress));

        _quests.SetLog([]);

        Assert.Equal(QuestOfferState.Available, _quests.Offers[0].State);
    }

    [Fact]
    public void Reset_ForgetsTheLogAndTheDialogue()
    {
        OpenDialogue(Line("a", QuestOfferState.Available));
        _quests.SetLog([Entry("a", QuestState.Active)]);

        _quests.Reset();

        Assert.False(_quests.DialogueOpen);
        Assert.Empty(_quests.Entries);
        Assert.Empty(_quests.Offers);
        Assert.Equal(-1, _quests.SelectedOffer);
    }

    [Fact]
    public void TheWindowDrawsNothing_WithoutADialogueOrWithoutTheInventory()
    {
        WindowFrame();
        Assert.Empty(_scene.Canvas.Calls);

        OpenDialogue(Line("a", QuestOfferState.Available));
        _scene.View.InventoryOpen = false;
        WindowFrame();
        Assert.Empty(_scene.Canvas.Calls);

        _scene.View.InventoryOpen = true;
        WindowFrame();
        Assert.True(_scene.Canvas.HasText("Gate Warden"));
        Assert.True(_scene.Canvas.HasText("Title a"));
    }

    [Fact]
    public void AvailableOffer_AcceptSendsAnAcceptIntent_ForTheSelectedQuest()
    {
        OpenDialogue(Line("a", QuestOfferState.InProgress), Line("b", QuestOfferState.Available));
        WindowFrame();

        Click(OfferRow(1));
        Assert.Equal(1, _quests.SelectedOffer);
        Assert.True(_scene.Canvas.HasText("Accept"));

        Click(ActionButton());

        Assert.True(_intents.TryDequeue(out IPlayerActionPayload payload));
        Assert.Equal(new QuestAcceptIntent(900, "b"), payload);
        Assert.Equal(0, _intents.Count);
    }

    [Fact]
    public void InProgressOffer_OffersAbandon_AndReadyOfferOffersTurnIn()
    {
        OpenDialogue(Line("a", QuestOfferState.InProgress), Line("b", QuestOfferState.ReadyToTurnIn), Line("c", QuestOfferState.Completed));

        WindowFrame();
        Assert.True(_scene.Canvas.HasText("Abandon"));
        Click(ActionButton());
        Assert.True(_intents.TryDequeue(out IPlayerActionPayload abandon));
        Assert.Equal(new QuestAbandonIntent("a"), abandon);

        Click(OfferRow(1));
        Assert.True(_scene.Canvas.HasText("Turn in"));
        Click(ActionButton());
        Assert.True(_intents.TryDequeue(out IPlayerActionPayload turnIn));
        Assert.Equal(new QuestTurnInIntent(900, "b"), turnIn);

        Click(OfferRow(2));
        Click(ActionButton());
        Assert.Equal(0, _intents.Count);
    }

    [Fact]
    public void TheCloseButton_ClosesTheDialogue_AndSendsNothing()
    {
        OpenDialogue(Line("a", QuestOfferState.Available));

        Click(Units(19f, 395f, 24f, 24f));

        Assert.False(_quests.DialogueOpen);
        Assert.Equal(0, _intents.Count);
    }

    [Fact]
    public void ALongStoryAndHostileText_AreDrawnAsPlainSpans_WithinTheLineBudget()
    {
        string story = string.Join(' ', Enumerable.Repeat("%s {0} grey as ash", 60));
        _quests.OpenDialogue(900, "Warden {0}", story, [new QuestOfferLine("a", "T %d", story, "R {1}", QuestOfferState.Available)]);

        WindowFrame();

        Assert.True(_scene.Canvas.HasText("Warden {0}"));
        Assert.True(_scene.Canvas.Texts.Count(t => t.Text.Contains("grey as ash", StringComparison.Ordinal)) > 0);
    }

    [Fact]
    public void TheTracker_ShowsEveryActiveQuest_WithItsObjectives_AndSaysWhenItIsReady()
    {
        _quests.SetLog(
        [
            Entry("a", QuestState.Active, new QuestObjectiveLine("Slay Wolf", 1, 2)),
            Entry("b", QuestState.Active, new QuestObjectiveLine("Bring Torch", 1, 1)),
            Entry("c", QuestState.Completed, new QuestObjectiveLine("Slay Wolf", 2, 2)),
        ]);

        _scene.Canvas.Calls.Clear();
        QuestLogPanel.Build(_scene.Canvas, _scene.View, _scene.Layout, _quests);

        Assert.True(_scene.Canvas.HasText("Quests"));
        Assert.True(_scene.Canvas.HasText("Title a"));
        Assert.True(_scene.Canvas.HasText("Title b"));
        Assert.False(_scene.Canvas.HasText("Title c"));
        Assert.True(_scene.Canvas.HasText("1/2"));
        Assert.True(_scene.Canvas.HasText("Return to the quest giver"));
    }

    [Fact]
    public void TheTracker_DrawsNothing_WithoutAnActiveQuest()
    {
        QuestLogPanel.Build(_scene.Canvas, _scene.View, _scene.Layout, null);
        QuestLogPanel.Build(_scene.Canvas, _scene.View, _scene.Layout, _quests);
        _quests.SetLog([Entry("c", QuestState.Completed)]);
        QuestLogPanel.Build(_scene.Canvas, _scene.View, _scene.Layout, _quests);

        Assert.Empty(_scene.Canvas.Calls);
    }
}
