using Dream.Server;
using Dream.Server.Persistence;
using Dream.Simulation.Content;
using Dream.Simulation.Inventory;
using Dream.Simulation.Persistence;
using Dream.Simulation.World;
using Dream.Tests.Server.Persistence.Migrations;
using Dream.Tests.Support;
using Xunit;

namespace Dream.Tests.BugHunt;

// BH-QUEST-2: the turn-in's XP used to be a profile write queued AFTER the turn-in's closure, so it was a
// savepoint of its own. A failed completion rolled back - quest still active, no gold, no reward items -
// while the XP committed, and a restart let the same quest be turned in again for the full reward. The XP's
// write now runs inside the turn-in's one closure, so it commits with the completion or not at all.
//
// The runtime's own XP write (WorldSessionRuntime.GrantQuestExperience) is handed to TryTurnInQuest as the
// rewardWrite factory; this exercises that seam on real SQLite through the group-commit write queue, where
// the savepoint semantics are real. A durable quest row stands in for the profile row, so the assertion is
// about rows in the database, not about a flag.
public sealed class QuestTurnInXpAtomicityTests
{
    private const string Player = "Alice";
    private const string Marker = "xp-marker";

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
            _bundle.World.SaveStarterKitGrant(Player, ToolCatalog.CurrentStarterKitVersion);
            Assert.True(Inventory.Ensure(Player));
        }

        public PlayerInventoryService Inventory { get; }

        public IWorldPersistenceRepository World => _bundle.World;

        public long FailedWrites => _writes.FailedWrites;

        // Short on purpose: a long mid-test flush starves the test classes running beside this one.
        public void Flush() => Assert.True(_writes.Flush(TimeSpan.FromSeconds(5)));

        public void Kill(string kind, int times = 1)
        {
            for (int i = 0; i < times; i++)
            {
                Inventory.RecordKill(Player, ContentCatalog.Shipped, kind);
            }
        }

        // Stands in for the XP's profile write: one durable row, written only when it is invoked.
        public Func<Action?> RewardWrite(Counter asked) => () =>
        {
            asked.Value++;
            IWorldPersistenceRepository world = World;
            return () => world.SaveQuestProgress(
                new PersistedQuest(Player, Marker, PlayerInventoryService.QuestActive, string.Empty, DateTime.UtcNow));
        };

        public bool MarkerWritten()
        {
            IReadOnlyList<PersistedQuest> rows = World.LoadPlayerInventory(Player).Quests ?? [];
            foreach (PersistedQuest row in rows)
            {
                if (string.Equals(row.QuestKey, Marker, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        public void Dispose()
        {
            _writes.Dispose();
            _bundle.Dispose();
            SqlitePools.Release(_databasePath);
        }
    }

    private sealed class Counter
    {
        public int Value { get; set; }
    }

    private static QuestDefinition Quest(string key) =>
        ContentCatalog.Shipped.TryGetQuest(key, out QuestDefinition? quest) ? quest : throw new InvalidOperationException(key);

    // The defect: the completion write fails, so nothing of the reward may be durable - the XP included.
    [Fact]
    public void AFailedCompletionWrite_DoesNotPayTheXpDurably()
    {
        using MigrationTestDatabase db = Migrated();
        QuestDefinition quest = Quest("wolf-cull");
        Counter asked = new();
        using (Run run = new(db))
        {
            Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryAcceptQuest(Player, quest));
            run.Kill("Wolf", 2);
            run.Flush();
            Assert.Equal(0, run.FailedWrites);

            db.ExecuteScript("CREATE TRIGGER fail_quest BEFORE UPDATE ON player_quests BEGIN SELECT RAISE(ABORT, 'simulated'); END;");
            Assert.Equal(
                InventoryEquipOutcome.Equipped,
                run.Inventory.TryTurnInQuest(Player, quest, out _, run.RewardWrite(asked)));
            run.Flush();

            Assert.Equal(1, run.FailedWrites);
            Assert.False(run.MarkerWritten());
        }

        db.ExecuteScript("DROP TRIGGER fail_quest;");
    }

    // Control: a legitimate turn-in pays the XP, exactly once, and it is durable.
    [Fact]
    public void AnAcceptedTurnIn_PaysTheXpOnce_AndItIsDurable()
    {
        using MigrationTestDatabase db = Migrated();
        QuestDefinition quest = Quest("wolf-cull");
        Counter asked = new();
        using Run run = new(db);
        Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryAcceptQuest(Player, quest));
        run.Kill("Wolf", 2);

        Assert.Equal(
            InventoryEquipOutcome.Equipped,
            run.Inventory.TryTurnInQuest(Player, quest, out long xp, run.RewardWrite(asked)));

        // A second turn-in of a quest already completed changes nothing and must not ask again.
        Assert.Equal(
            InventoryEquipOutcome.QuestUnavailable,
            run.Inventory.TryTurnInQuest(Player, quest, out _, run.RewardWrite(asked)));
        run.Flush();

        Assert.Equal(quest.Rewards.Xp, xp);
        Assert.Equal(1, asked.Value);
        Assert.Equal(0, run.FailedWrites);
        Assert.True(run.MarkerWritten());
        Assert.Equal(quest.Rewards.Gold, run.Inventory.GoldOf(Player));
    }

    // Control: a refused turn-in never asks for the XP at all, so nothing is granted in memory either.
    [Fact]
    public void ATurnInRefusedByItsObjectives_NeverAsksForTheXp()
    {
        using MigrationTestDatabase db = Migrated();
        QuestDefinition quest = Quest("wolf-cull");
        Counter asked = new();
        using Run run = new(db);
        Assert.Equal(InventoryEquipOutcome.Equipped, run.Inventory.TryAcceptQuest(Player, quest));

        Assert.Equal(
            InventoryEquipOutcome.QuestUnavailable,
            run.Inventory.TryTurnInQuest(Player, quest, out long xp, run.RewardWrite(asked)));
        run.Flush();

        Assert.Equal(0, xp);
        Assert.Equal(0, asked.Value);
        Assert.False(run.MarkerWritten());
        Assert.Equal(0, run.FailedWrites);
    }
}
