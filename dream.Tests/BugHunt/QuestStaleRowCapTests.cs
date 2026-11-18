using System.Text;
using Dream.Multiplayer.Protocol;
using Dream.Simulation.Content;
using Dream.Simulation.Inventory;
using Dream.Simulation.Persistence;
using Dream.Tests.Server.Persistence.Migrations;
using Xunit;

namespace Dream.Tests.BugHunt;

// BH-QUEST-4: rows for quest keys the running content no longer has (a quest renamed or removed in
// quests.json, a world moved between content versions) are kept inert - the client is never told the key,
// so it can never abandon them, and a completed row cannot be abandoned at all. They used to count against
// the same 32-row cap as the live ones, and ContentCatalog.MaxQuests is also 32, so a handful of them left a
// character permanently unable to take the quests the content does have.
//
// The decision: do not count them, rather than make them visible. Making them visible would mean inventing a
// title and objectives the content no longer has, on the server and in the HUD both, and the surrounding
// code deliberately keeps them inert (QuestServiceTests asserts "an unknown quest kept inert").
public sealed class QuestStaleRowCapTests
{
    private static void InsertStaleRows(MigrationTestDatabase db, int count, int state)
    {
        StringBuilder script = new();
        for (int i = 0; i < count; i++)
        {
            script.Append("INSERT INTO player_quests (world_id, player_name, quest_key, state, progress, updated_utc) VALUES (1, 'Alice', 'retired-")
                .Append(i)
                .Append("', ")
                .Append(state)
                .Append(", '', '2026-01-01T00:00:00.0000000Z');\n");
        }

        db.ExecuteScript(script.ToString());
    }

    // The cap the accept gate uses is the content's own, and the log message can carry that many.
    [Fact]
    public void TheLiveCapMatchesTheContent_AndTheRowCapLeavesRoomForStaleRows()
    {
        Assert.Equal(ContentCatalog.MaxQuests, PlayerInventoryService.MaxLiveQuestRecords);
        Assert.Equal(ServerQuestLog.MaxEntries, PlayerInventoryService.MaxLiveQuestRecords);
        Assert.True(PlayerInventoryService.MaxQuestRecords > PlayerInventoryService.MaxLiveQuestRecords);
    }

    // The defect: a character whose rows are all stale could accept nothing at all.
    [Theory]
    [InlineData(PlayerInventoryService.QuestActive)]
    [InlineData(PlayerInventoryService.QuestCompleted)]
    public void StaleRowsFillingTheOldCap_DoNotBlockANewQuest(int state)
    {
        using MigrationTestDatabase db = QuestBugHunt.Migrated();
        InsertStaleRows(db, ContentCatalog.MaxQuests, state);
        using QuestBugHunt.Run run = new(db);

        Assert.Equal(
            InventoryEquipOutcome.Equipped,
            run.Inventory.TryAcceptQuest(QuestBugHunt.Player, QuestBugHunt.Shipped("wolf-cull"), ContentCatalog.Shipped));
        run.Flush();

        Assert.Equal(0, run.FailedWrites);
        Assert.Contains(run.Rows().Quests!, row => row.QuestKey == "wolf-cull");
    }

    // Every shipped quest is reachable over the stale rows, and a reload keeps all of them.
    [Fact]
    public void EveryShippedQuest_IsStillReachable_OverAFullSetOfStaleRows()
    {
        using MigrationTestDatabase db = QuestBugHunt.Migrated();
        InsertStaleRows(db, ContentCatalog.MaxQuests, PlayerInventoryService.QuestCompleted);
        int shipped = ContentCatalog.Shipped.Quests.Count;
        using (QuestBugHunt.Run run = new(db))
        {
            foreach (QuestDefinition quest in ContentCatalog.Shipped.Quests)
            {
                Assert.Equal(
                    InventoryEquipOutcome.Equipped,
                    run.Inventory.TryAcceptQuest(QuestBugHunt.Player, quest, ContentCatalog.Shipped));
            }

            run.Flush();
            Assert.Equal(0, run.FailedWrites);
        }

        using QuestBugHunt.Run again = new(db);
        foreach (QuestDefinition quest in ContentCatalog.Shipped.Quests)
        {
            Assert.True(again.Inventory.TryGetQuestRecord(QuestBugHunt.Player, quest.Key, out _));
        }

        Assert.Equal(ContentCatalog.MaxQuests + shipped, again.Inventory.QuestsOf(QuestBugHunt.Player).Count);
    }

    // Control: the stale rows stay inert. They are still in the rows, still not in the quest log's
    // reachable set, and a second accept of a live quest is still refused.
    [Fact]
    public void TheStaleRowsStayInert_AndADuplicateAcceptIsStillRefused()
    {
        using MigrationTestDatabase db = QuestBugHunt.Migrated();
        InsertStaleRows(db, 3, PlayerInventoryService.QuestCompleted);
        using QuestBugHunt.Run run = new(db);
        QuestDefinition quest = QuestBugHunt.Shipped("wolf-cull");

        Assert.Equal(
            InventoryEquipOutcome.Equipped,
            run.Inventory.TryAcceptQuest(QuestBugHunt.Player, quest, ContentCatalog.Shipped));
        Assert.Equal(
            InventoryEquipOutcome.QuestUnavailable,
            run.Inventory.TryAcceptQuest(QuestBugHunt.Player, quest, ContentCatalog.Shipped));

        // A stale row cannot be abandoned - it is not a quest the content has - and it is still there.
        Assert.Equal(
            InventoryEquipOutcome.QuestUnavailable,
            run.Inventory.TryAbandonQuest(QuestBugHunt.Player, "retired-0"));
        run.Flush();

        Assert.Equal(0, run.FailedWrites);
        Assert.True(run.Inventory.TryGetQuestRecord(QuestBugHunt.Player, "retired-0", out PlayerInventoryService.QuestRecord? stale));
        Assert.Equal(PlayerInventoryService.QuestCompleted, stale.State);
        Assert.Equal(4, run.Inventory.QuestsOf(QuestBugHunt.Player).Count);
    }

    // Control: the absolute row cap still bounds what a hostile set of rows can load.
    [Fact]
    public void TheRowCap_StillBoundsALoad()
    {
        using MigrationTestDatabase db = QuestBugHunt.Migrated();
        InsertStaleRows(db, PlayerInventoryService.MaxQuestRecords + 20, PlayerInventoryService.QuestActive);
        using QuestBugHunt.Run run = new(db);

        Assert.Equal(PlayerInventoryService.MaxQuestRecords, run.Inventory.QuestsOf(QuestBugHunt.Player).Count);
    }
}
