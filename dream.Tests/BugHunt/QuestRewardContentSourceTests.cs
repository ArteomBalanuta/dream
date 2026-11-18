using System.Text.Json.Nodes;
using Dream.Simulation.Content;
using Dream.Simulation.Inventory;
using Dream.Simulation.Persistence;
using Dream.Tests.Server.Persistence.Migrations;
using Xunit;

namespace Dream.Tests.BugHunt;

// BH-QUEST-5: the quest offer side reads its definitions through the runtime's own EnemyContent
// ("so a runtime with its own content has its own quests"), but the turn-in used to resolve the reward
// item through ContentCatalog.Shipped. A reward item only the runtime's items.json has therefore never
// resolved, every turn-in answered QuestUnavailable with no diagnostic, and the player was stuck holding
// a finished quest for good. The reward now resolves through the catalog the quest came from.
public sealed class QuestRewardContentSourceTests
{
    private const string CustomReward = "potion.custom-reward";

    private static string ShippedFile(string file) => File.ReadAllText(Path.Combine(ContentCatalog.ShippedDirectory, file));

    // The shipped items plus one more: a copy of potion.health under a key the shipped catalog lacks
    // (the same visual, so nothing else has to be authored for it).
    private static string ItemsWithACustomReward()
    {
        JsonNode root = JsonNode.Parse(ShippedFile(ContentCatalog.ItemsFileName))!;
        JsonArray items = root["items"]!.AsArray();
        JsonNode template = items.First(item => (string?)item!["key"] == "potion.health")!;
        JsonNode copy = JsonNode.Parse(template.ToJsonString())!;
        copy["key"] = CustomReward;
        copy["displayName"] = "Warden's Draught";
        items.Add(copy);
        return root.ToJsonString();
    }

    // One quest, on the shipped gate warden, whose only reward item is the custom one.
    private static ContentCatalog CatalogWithACustomReward() =>
        ContentCatalog.Load(
            ItemsWithACustomReward(),
            ShippedFile(ContentCatalog.EnemiesFileName),
            ShippedFile(ContentCatalog.LootFileName),
            ShippedFile(ContentCatalog.SpawnsFileName),
            ShippedFile(ContentCatalog.NpcsFileName),
            ShippedFile(ContentCatalog.SkillsFileName),
            "{\"version\":1,\"quests\":[{\"key\":\"warden-errand\",\"title\":\"Errand\",\"description\":\"D\","
                + "\"giver\":\"gate-warden\",\"objectives\":[{\"kind\":\"Kill\",\"target\":\"Wolf\",\"count\":1}],"
                + "\"rewards\":{\"xp\":10,\"gold\":5,\"items\":[{\"item\":\"" + CustomReward + "\",\"count\":1}]}}]}");

    // The load-time check already validates the reward against that file's own items, so the mismatch
    // only ever showed up at turn-in.
    [Fact]
    public void TheAlternateCatalog_Loads_AndTheShippedOneDoesNotKnowTheRewardItem()
    {
        ContentCatalog alternate = CatalogWithACustomReward();

        Assert.True(alternate.TryGetItem(CustomReward, out _));
        Assert.False(ContentCatalog.Shipped.TryGetItem(CustomReward, out _));
        Assert.True(alternate.TryGetQuest("warden-errand", out _));
    }

    [Fact]
    public void AQuestFromAnAlternateCatalog_PaysItsOwnRewardItem()
    {
        ContentCatalog alternate = CatalogWithACustomReward();
        Assert.True(alternate.TryGetQuest("warden-errand", out QuestDefinition? quest));
        using MigrationTestDatabase db = QuestBugHunt.Migrated();
        using QuestBugHunt.Run run = new(db);

        Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryAcceptQuest(QuestBugHunt.Player, quest));
        run.Kill(alternate, "Wolf");
        Assert.Equal(
            InventoryEquipOutcome.Equipped,
            run.Inventory.TryTurnInQuest(QuestBugHunt.Player, quest, out long xp, content: alternate));
        run.Flush();

        Assert.Equal(10, xp);
        Assert.Equal(0, run.FailedWrites);
        Assert.Equal(1, run.Inventory.CountBagItems(QuestBugHunt.Player, CustomReward));
        PersistedItemInstance row = Assert.Single(run.Rows().Items);
        Assert.Equal(CustomReward, row.ItemKind);
        Assert.Equal("Warden's Draught", row.DisplayName);
        Assert.Equal(5, run.Inventory.GoldOf(QuestBugHunt.Player));
    }

    // Control: the shipped path is unchanged - a shipped quest still pays its shipped reward item, both
    // when the catalog is passed and when it is left out.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AShippedQuest_StillPaysItsShippedRewardItem(bool passTheCatalog)
    {
        QuestDefinition quest = QuestBugHunt.Shipped("wolf-cull");
        using MigrationTestDatabase db = QuestBugHunt.Migrated();
        using QuestBugHunt.Run run = new(db);

        Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryAcceptQuest(QuestBugHunt.Player, quest));
        run.Kill(ContentCatalog.Shipped, "Wolf", 2);
        Assert.Equal(
            InventoryEquipOutcome.Equipped,
            run.Inventory.TryTurnInQuest(
                QuestBugHunt.Player, quest, out _, content: passTheCatalog ? ContentCatalog.Shipped : null));
        run.Flush();

        Assert.Equal(0, run.FailedWrites);
        Assert.Equal("potion.health", Assert.Single(run.Rows().Items).ItemKind);
        Assert.Equal(quest.Rewards.Gold, run.Inventory.GoldOf(QuestBugHunt.Player));
    }

    // A reward the acting catalog does not know is still refused, not crashed through on.
    [Fact]
    public void ARewardTheActingCatalogLacks_IsRefused()
    {
        ContentCatalog alternate = CatalogWithACustomReward();
        Assert.True(alternate.TryGetQuest("warden-errand", out QuestDefinition? quest));
        using MigrationTestDatabase db = QuestBugHunt.Migrated();
        using QuestBugHunt.Run run = new(db);

        Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryAcceptQuest(QuestBugHunt.Player, quest));
        run.Kill(alternate, "Wolf");

        Assert.Equal(
            InventoryEquipOutcome.QuestUnavailable,
            run.Inventory.TryTurnInQuest(QuestBugHunt.Player, quest, out _, content: ContentCatalog.Shipped));
        run.Flush();
        Assert.Equal(0, run.FailedWrites);
        Assert.Empty(run.Rows().Items);
    }
}
