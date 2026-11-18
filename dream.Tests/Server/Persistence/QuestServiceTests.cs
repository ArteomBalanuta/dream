using Dream.Server;
using Dream.Server.Persistence;
using Dream.Simulation.Content;
using Dream.Simulation.Inventory;
using Dream.Simulation.Persistence;
using Dream.Simulation.World;
using Dream.Tests.Server.Persistence.Migrations;
using Dream.Tests.Support;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Dream.Tests.Server.Persistence;

// M6 workstream B: the quest state of PlayerInventoryService on real SQLite through the background write
// queue. Accept and kill progress are their own writes; a turn-in is ONE closure holding the completion, the
// consumed items, the gold delta and the reward rows, so a failed write leaves the rows as they were and the
// failed-write gate refuses everything after it.
public sealed class QuestServiceTests
{
    private const string Player = "Alice";
    private const string Torch = "tool.torch";
    private const string HealthPotion = "potion.health";

    private static MigrationTestDatabase Migrated()
    {
        MigrationTestDatabase db = new();
        db.Schema.EnsureSchema();
        SqlitePools.Release(db.DatabasePath);
        db.ExecuteScript("INSERT INTO player_profiles (player_name, display_name) VALUES ('Alice', 'Alice');");
        return db;
    }

    private sealed class Run : IDisposable
    {
        private readonly ServerPersistenceBundle _bundle;
        private readonly string _databasePath;
        private readonly PersistenceWriteQueue _writes = PersistenceWriteQueue.Background();

        public Run(MigrationTestDatabase db)
        {
            _databasePath = db.DatabasePath;
            _bundle = ServerBootstrap.CreatePersistenceBundle(db.DatabasePath, MigrationTestDatabase.InitScriptPath);
            Inventory = new PlayerInventoryService(_bundle.World, _writes, new ObjectIdAllocator(_bundle.World.LoadNextObjectId()));
            // An empty bag: the starter kit counts as received.
            _bundle.World.SaveStarterKitGrant(Player, ToolCatalog.CurrentStarterKitVersion);
            Assert.True(Inventory.Ensure(Player));
        }

        public PlayerInventoryService Inventory { get; }

        public IWorldPersistenceRepository World => _bundle.World;

        public long FailedWrites => _writes.FailedWrites;

        public void Flush() => Assert.True(_writes.Flush(TimeSpan.FromSeconds(30)));

        public PersistedPlayerInventory Rows() => World.LoadPlayerInventory(Player);

        public void Fund(long gold)
        {
            Assert.True(Inventory.TryAddGold(Player, gold));
            Flush();
            Assert.Equal(0, FailedWrites);
        }

        public void BuyTorch()
        {
            Assert.Equal(InventoryEquipOutcome.Equipped, Inventory.TryBuy(Player, Torch, 6));
        }

        public void Kill(string kind, int times = 1)
        {
            for (int i = 0; i < times; i++)
            {
                Inventory.RecordKill(Player, ContentCatalog.Shipped, kind);
            }
        }

        public void Dispose()
        {
            _writes.Dispose();
            _bundle.Dispose();
            SqlitePools.Release(_databasePath);
        }
    }

    private static QuestDefinition Quest(string key) =>
        ContentCatalog.Shipped.TryGetQuest(key, out QuestDefinition? quest) ? quest : throw new InvalidOperationException(key);

    private static int CountOf(Run run, string kind) => run.Inventory.CountBagItems(Player, kind);

    [Fact]
    public void AnAcceptedQuest_IsPersistedActive_AndCannotBeAcceptedTwice()
    {
        using MigrationTestDatabase db = Migrated();
        using Run run = new(db);
        long before = run.Inventory.QuestRevisionOf(Player);

        Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryAcceptQuest(Player, Quest("wolf-cull")));
        Assert.Equal(InventoryEquipOutcome.QuestUnavailable, run.Inventory.TryAcceptQuest(Player, Quest("wolf-cull")));
        run.Flush();

        Assert.True(run.Inventory.QuestRevisionOf(Player) > before);
        Assert.Equal(0, run.FailedWrites);
        PersistedQuest row = Assert.Single(run.Rows().Quests!);
        Assert.Equal("wolf-cull", row.QuestKey);
        Assert.Equal(PlayerInventoryService.QuestActive, row.State);
        Assert.Equal("0", row.Progress);
    }

    [Fact]
    public void KillProgress_CountsOnlyTheRightEnemy_StopsAtTheCount_AndSurvivesARestart()
    {
        using MigrationTestDatabase db = Migrated();
        using (Run run = new(db))
        {
            Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryAcceptQuest(Player, Quest("wolf-cull")));
            run.Kill("DireWolf");
            run.Kill("Wolf", 5);
            run.Flush();
            Assert.Equal("2", Assert.Single(run.Rows().Quests!).Progress);
            Assert.Equal(0, run.FailedWrites);
        }

        using Run again = new(db);
        Assert.True(again.Inventory.TryGetQuestRecord(Player, "wolf-cull", out PlayerInventoryService.QuestRecord? record));
        Assert.Equal(2, record.Kills[0]);
        Assert.Equal(PlayerInventoryService.QuestActive, record.State);
        Assert.True(again.Inventory.ObjectivesMet(Player, record, Quest("wolf-cull")));
    }

    [Fact]
    public void AKillWithoutTheQuest_ChangesNothing()
    {
        using MigrationTestDatabase db = Migrated();
        using Run run = new(db);
        long revision = run.Inventory.QuestRevisionOf(Player);

        Assert.False(run.Inventory.RecordKill(Player, ContentCatalog.Shipped, "Wolf"));
        run.Flush();

        Assert.Equal(revision, run.Inventory.QuestRevisionOf(Player));
        Assert.Empty(run.Rows().Quests!);
    }

    [Fact]
    public void ATurnIn_PaysOnce_AsOneWrite_AndSurvivesARestart()
    {
        using MigrationTestDatabase db = Migrated();
        QuestDefinition quest = Quest("wolf-cull");
        using (Run run = new(db))
        {
            Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryAcceptQuest(Player, quest));
            Assert.Equal(InventoryEquipOutcome.QuestUnavailable, run.Inventory.TryTurnInQuest(Player, quest, out long unmetXp));
            Assert.Equal(0, unmetXp);
            run.Kill("Wolf", 2);

            Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryTurnInQuest(Player, quest, out long xp));
            Assert.Equal(quest.Rewards.Xp, xp);
            Assert.Equal(InventoryEquipOutcome.QuestUnavailable, run.Inventory.TryTurnInQuest(Player, quest, out long secondXp));
            Assert.Equal(0, secondXp);
            Assert.Equal(InventoryEquipOutcome.QuestUnavailable, run.Inventory.TryAcceptQuest(Player, quest));
            Assert.Equal(InventoryEquipOutcome.QuestUnavailable, run.Inventory.TryAbandonQuest(Player, quest.Key));
            run.Flush();

            Assert.Equal(0, run.FailedWrites);
            Assert.Equal(40, run.Inventory.GoldOf(Player));
            Assert.Equal(1, CountOf(run, HealthPotion));
            PersistedPlayerInventory rows = run.Rows();
            Assert.Equal(40, rows.Gold);
            Assert.Equal(PlayerInventoryService.QuestCompleted, Assert.Single(rows.Quests!).State);
            Assert.Equal(HealthPotion, Assert.Single(rows.Items).ItemKind);
            Assert.Single(rows.Slots);
        }

        using Run again = new(db);
        Assert.Equal(40, again.Inventory.GoldOf(Player));
        Assert.Equal(1, CountOf(again, HealthPotion));
        Assert.True(again.Inventory.TryGetQuestRecord(Player, quest.Key, out PlayerInventoryService.QuestRecord? record));
        Assert.Equal(PlayerInventoryService.QuestCompleted, record.State);
        Assert.Equal(InventoryEquipOutcome.QuestUnavailable, again.Inventory.TryTurnInQuest(Player, quest, out _));
        Assert.Equal(40, again.Inventory.GoldOf(Player));
    }

    [Fact]
    public void ACollectTurnIn_ConsumesTheItems_ButNotWhatTheBagLacks()
    {
        using MigrationTestDatabase db = Migrated();
        QuestDefinition quest = Quest("torch-supply");
        using Run run = new(db);
        run.Fund(100);
        run.BuyTorch();
        Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryAcceptQuest(Player, quest));
        Assert.Equal(InventoryEquipOutcome.QuestUnavailable, run.Inventory.TryTurnInQuest(Player, quest, out _));
        Assert.Equal(1, CountOf(run, Torch));

        run.BuyTorch();
        run.Flush();
        long goldBefore = run.Inventory.GoldOf(Player);
        Assert.Equal(2, CountOf(run, Torch));
        Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryTurnInQuest(Player, quest, out long xp));
        run.Flush();

        Assert.Equal(quest.Rewards.Xp, xp);
        Assert.Equal(0, run.FailedWrites);
        Assert.Equal(0, CountOf(run, Torch));
        Assert.Equal(goldBefore + quest.Rewards.Gold, run.Inventory.GoldOf(Player));
        PersistedPlayerInventory rows = run.Rows();
        Assert.Empty(rows.Slots);
        Assert.Empty(rows.Items);
        Assert.Equal(goldBefore + quest.Rewards.Gold, rows.Gold);
    }

    [Fact]
    public void ATurnInWithNoRoomForTheReward_IsRefused_AndChangesNothing_UntilSpaceIsMade()
    {
        using MigrationTestDatabase db = Migrated();
        QuestDefinition quest = Quest("wolf-cull");
        using Run run = new(db);
        run.Fund(1000);
        while (run.Inventory.TryBuy(Player, Torch, 6) == InventoryEquipOutcome.Equipped)
        {
        }

        Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryAcceptQuest(Player, quest));
        run.Kill("Wolf", 2);
        run.Flush();
        long gold = run.Inventory.GoldOf(Player);

        Assert.Equal(InventoryEquipOutcome.BackpackFull, run.Inventory.TryTurnInQuest(Player, quest, out long xp));
        Assert.Equal(0, xp);
        Assert.Equal(gold, run.Inventory.GoldOf(Player));
        Assert.True(run.Inventory.TryGetQuestRecord(Player, quest.Key, out PlayerInventoryService.QuestRecord? record));
        Assert.Equal(PlayerInventoryService.QuestActive, record.State);

        InventorySlotSnapshot torch = run.Inventory.BuildSnapshot(Player).Slots.First(slot => slot.Item != null);
        Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryDestroy(Player, torch.SlotIndex, torch.Item!.Value.ItemInstanceId));
        Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryTurnInQuest(Player, quest, out _));
        run.Flush();

        Assert.Equal(0, run.FailedWrites);
        Assert.Equal(gold + quest.Rewards.Gold, run.Rows().Gold);
        Assert.Equal(PlayerInventoryService.QuestCompleted, Assert.Single(run.Rows().Quests!).State);
    }

    [Fact]
    public void ATurnInThatWouldOverfillTheWallet_IsRefused()
    {
        using MigrationTestDatabase db = Migrated();
        QuestDefinition quest = Quest("wolf-cull");
        using Run run = new(db);
        run.Fund(InventoryConstants.MaxGold - 10);
        Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryAcceptQuest(Player, quest));
        run.Kill("Wolf", 2);

        Assert.Equal(InventoryEquipOutcome.WalletFull, run.Inventory.TryTurnInQuest(Player, quest, out _));
        run.Flush();

        Assert.Equal(InventoryConstants.MaxGold - 10, run.Inventory.GoldOf(Player));
        Assert.Equal(PlayerInventoryService.QuestActive, Assert.Single(run.Rows().Quests!).State);
        Assert.Equal(0, run.FailedWrites);
    }

    [Fact]
    public void AnAbandonedQuest_LeavesTheLog_AndCanBeTakenAgain()
    {
        using MigrationTestDatabase db = Migrated();
        QuestDefinition quest = Quest("wolf-cull");
        using Run run = new(db);
        Assert.Equal(InventoryEquipOutcome.QuestUnavailable, run.Inventory.TryAbandonQuest(Player, quest.Key));
        Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryAcceptQuest(Player, quest));
        run.Kill("Wolf");

        Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryAbandonQuest(Player, quest.Key));
        run.Flush();
        Assert.Empty(run.Rows().Quests!);

        Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryAcceptQuest(Player, quest));
        run.Flush();
        Assert.Equal("0", Assert.Single(run.Rows().Quests!).Progress);
    }

    [Fact]
    public void AFailedCompletionWrite_PaysNothing_AndTheGateRefusesEverythingAfter()
    {
        using MigrationTestDatabase db = Migrated();
        QuestDefinition quest = Quest("wolf-cull");
        using (Run run = new(db))
        {
            Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryAcceptQuest(Player, quest));
            run.Kill("Wolf", 2);
            run.Flush();
            Assert.Equal(0, run.FailedWrites);

            db.ExecuteScript("CREATE TRIGGER fail_quest BEFORE UPDATE ON player_quests BEGIN SELECT RAISE(ABORT, 'simulated'); END;");
            Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryTurnInQuest(Player, quest, out _));
            run.Flush();

            // The closure rolled back whole: no gold, no item, the quest still active in the rows.
            Assert.Equal(1, run.FailedWrites);
            PersistedPlayerInventory rows = run.Rows();
            Assert.Equal(0, rows.Gold);
            Assert.Empty(rows.Items);
            Assert.Empty(rows.Slots);
            Assert.Equal(PlayerInventoryService.QuestActive, Assert.Single(rows.Quests!).State);

            // The gate: nothing that moves quests, gold or items goes on until a restart reloads the rows.
            Assert.Equal(InventoryEquipOutcome.WriteFailed, run.Inventory.TryTurnInQuest(Player, quest, out long xp));
            Assert.Equal(0, xp);
            Assert.Equal(InventoryEquipOutcome.WriteFailed, run.Inventory.TryAcceptQuest(Player, Quest("torch-supply")));
            Assert.Equal(InventoryEquipOutcome.WriteFailed, run.Inventory.TryAbandonQuest(Player, quest.Key));
        }

        db.ExecuteScript("DROP TRIGGER fail_quest;");
        using Run again = new(db);
        Assert.Equal(0, again.Inventory.GoldOf(Player));
        Assert.Equal(0, CountOf(again, HealthPotion));
        Assert.Equal(InventoryEquipOutcome.Equipped, again.Inventory.TryTurnInQuest(Player, quest, out _));
        again.Flush();
        Assert.Equal(0, again.FailedWrites);
        Assert.Equal(40, again.Rows().Gold);
        Assert.Equal(HealthPotion, Assert.Single(again.Rows().Items).ItemKind);
    }

    [Fact]
    public void StoredRowsAreRead_WithHostileProgressClamped_AndAnUnknownQuestKeptInert()
    {
        using MigrationTestDatabase db = Migrated();
        db.ExecuteScript("""
            INSERT INTO player_quests (world_id, player_name, quest_key, state, progress, updated_utc)
            VALUES (1, 'Alice', 'wolf-cull', 1, 'x,-5,999999,1,2,3', '2026-01-01T00:00:00.0000000Z');
            INSERT INTO player_quests (world_id, player_name, quest_key, state, progress, updated_utc)
            VALUES (1, 'Alice', 'retired-quest', 2, '', '2026-01-01T00:00:00.0000000Z');
            """);
        using Run run = new(db);

        Assert.True(run.Inventory.TryGetQuestRecord(Player, "wolf-cull", out PlayerInventoryService.QuestRecord? record));
        Assert.Equal(0, record.Kills[0]);
        Assert.Equal(0, record.Kills[1]);
        Assert.Equal(ContentCatalog.MaxQuestCount, record.Kills[2]);
        Assert.Equal(1, record.Kills[3]);
        Assert.Equal(PlayerInventoryService.QuestActive, record.State);
        Assert.True(run.Inventory.TryGetQuestRecord(Player, "retired-quest", out _));
    }
}
