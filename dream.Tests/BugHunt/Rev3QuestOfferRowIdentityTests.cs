using Dream.Multiplayer.Protocol;
using Dream.Rendering.Hud;
using Dream.Rendering.Ui;
using Dream.Rendering.Ui.Panels;
using Dream.Tests.Rendering.Ui.Panels;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.BugHunt;

// REV3-4: the NPC dialogue's offer ROWS still bound their position.
//
// BH-QUEST-1 gave the action button an identity (state + NPC + quest key) but left the rows two
// blocks up on `IdOfferRow + n`. UiInputRouter acts on release and matches only the integer id, so
// a rebuild between the press and the release that moves a different quest under the pointer made
// quests.Select(n) highlight a quest the player never pressed. Bounded harm - the action button's
// own id carries the quest, so it cannot act on the wrong one - but the same defect and the same
// cure: the row's id carries the NPC and the quest key, so a row that changed hands drops the click
// instead of moving the selection.
//
// Driven through the real UiInputRouter frame by frame, so no GL context and no window.
public sealed class Rev3QuestOfferRowIdentityTests : IDisposable
{
    private readonly InventoryScene _scene = new();
    private readonly QuestHudState _quests = new();
    private readonly HudIntentQueue _intents = new();
    private readonly UiInputRouter _router = new();

    public void Dispose() => _scene.Dispose();

    private static QuestOfferLine Offer(string key) =>
        new(key, "Title " + key, "A story about " + key + ".", "100 XP", QuestOfferState.Available);

    // Row n's rect, from the panel's own layout constants (19 u in, 127 u down, 13 u pitch).
    private UiRect Row(int n) =>
        _scene.Layout.FromUnits(_scene.Layout.Character, 19f, 127f + (n * 13f), 152f, 12f);

    private void DialogueFrame(Vector2 pointer, bool leftDown)
    {
        _router.BeginFrame(new UiPointer(pointer, leftDown, false, true));
        NpcDialogueWindow.Build(
            _scene.Canvas, _router, _scene.View, _scene.Layout, _scene.Table, _scene.Actions, _quests, _intents);
        _router.EndFrame();
    }

    private void PressAndRelease(int row, Action? betweenPressAndRelease = null)
    {
        Vector2 at = InventoryScene.Centre(Row(row));
        DialogueFrame(at, leftDown: false);
        DialogueFrame(at, leftDown: true);
        betweenPressAndRelease?.Invoke();
        DialogueFrame(at, leftDown: false);
    }

    private string SelectedKey() => _quests.Offers[_quests.SelectedOffer].Key;

    // The defect: press row 1 ("torch-supply"), and have the offers rebuilt with the quests in the
    // other order before the release. With positional ids the release landed on row 1 of the NEW
    // list and selected "wolf-cull" - a quest the player never pressed. With the quest in the id the
    // click is dropped and the selection stays where the rebuild put it.
    [Fact]
    public void AnOfferListRebuiltMidPress_SelectsNoQuestAtAll_NotTheOneThatTookTheRow()
    {
        _quests.OpenDialogue(900, "Gate Warden", "Keep your voice low.", [Offer("wolf-cull"), Offer("torch-supply")]);
        Assert.Equal("wolf-cull", SelectedKey());

        PressAndRelease(
            row: 1,
            betweenPressAndRelease: () => _quests.OpenDialogue(
                900, "Gate Warden", "Keep your voice low.", [Offer("torch-supply"), Offer("wolf-cull")]));

        // Row 1 now holds "wolf-cull". The press was on "torch-supply", so the click is dropped and
        // the rebuild's own selection (index 0) stands.
        Assert.Equal(0, _quests.SelectedOffer);
        Assert.Equal("torch-supply", SelectedKey());
    }

    // The same through the NPC: a rebuild that brings another quest giver's offers is a different
    // identity even at the same row with the same key order.
    [Fact]
    public void ARebuildOnAnotherNpc_DropsTheRowClick()
    {
        _quests.OpenDialogue(900, "Gate Warden", "Keep your voice low.", [Offer("wolf-cull"), Offer("torch-supply")]);

        PressAndRelease(
            row: 1,
            betweenPressAndRelease: () => _quests.OpenDialogue(
                901, "Other Warden", "Move along.", [Offer("wolf-cull"), Offer("torch-supply")]));

        Assert.Equal(0, _quests.SelectedOffer);
        Assert.Equal("wolf-cull", SelectedKey());
    }

    // Control: an undisturbed press on a row still selects the quest it was pressed on.
    [Fact]
    public void Control_AnUndisturbedRowClick_StillSelectsTheQuestPressed()
    {
        _quests.OpenDialogue(900, "Gate Warden", "Keep your voice low.", [Offer("wolf-cull"), Offer("torch-supply")]);

        PressAndRelease(row: 1);

        Assert.Equal(1, _quests.SelectedOffer);
        Assert.Equal("torch-supply", SelectedKey());
    }

    // Control: a rebuild that leaves the pressed quest on the same row is the same identity, so the
    // click still lands. Nothing about the fix drops a press whose identity did not change.
    [Fact]
    public void Control_ARebuildThatKeepsTheRow_StillSelectsIt()
    {
        _quests.OpenDialogue(900, "Gate Warden", "Keep your voice low.", [Offer("wolf-cull"), Offer("torch-supply")]);

        PressAndRelease(
            row: 1,
            betweenPressAndRelease: () => _quests.OpenDialogue(
                900, "Gate Warden", "Keep your voice low.", [Offer("wolf-cull"), Offer("torch-supply")]));

        Assert.Equal(1, _quests.SelectedOffer);
        Assert.Equal("torch-supply", SelectedKey());
    }

    // The ids themselves: distinct per quest and per NPC, stable within a run, and clear of the
    // action button's four state seeds - both live in the 0x4000_0000 band, so a collision there
    // would route a row click at the action button.
    [Fact]
    public void RowIds_AreDistinctPerQuestAndNpc_AndCollideWithNoActionId()
    {
        int wolf = NpcDialogueWindow.OfferRowId(900, "wolf-cull");
        int torch = NpcDialogueWindow.OfferRowId(900, "torch-supply");
        int otherNpc = NpcDialogueWindow.OfferRowId(901, "wolf-cull");

        Assert.NotEqual(wolf, torch);
        Assert.NotEqual(wolf, otherNpc);
        Assert.Equal(wolf, NpcDialogueWindow.OfferRowId(900, "wolf-cull"));

        int[] rowIds = [wolf, torch, otherNpc];
        foreach (QuestOfferState state in Enum.GetValues<QuestOfferState>())
        {
            foreach (int npc in (int[])[900, 901])
            {
                foreach (string key in (string[])["wolf-cull", "torch-supply"])
                {
                    Assert.DoesNotContain(NpcDialogueWindow.ActionId(state, npc, key), rowIds);
                }
            }
        }

        // The band is the panel's own, and clear of HudWidgetId's (0x5000_0000) and of the ordinary
        // ids (below 0x0100_0000).
        foreach (int id in rowIds)
        {
            Assert.Equal(NpcDialogueWindow.IdActionBand, id & ~NpcDialogueWindow.IdActionBandMask);
            Assert.NotEqual(HudWidgetId.IdentityBand, id & ~HudWidgetId.IdentityMask);
        }
    }
}
