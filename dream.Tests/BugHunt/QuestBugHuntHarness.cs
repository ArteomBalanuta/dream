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

// Shared harness for the BH-QUEST findings that need a real PlayerInventoryService on real SQLite: the
// same shape QuestServiceTests uses, with a short flush deadline so a mid-test flush cannot starve the
// test classes running beside it.
internal static class QuestBugHunt
{
    public const string Player = "Alice";

    public static MigrationTestDatabase Migrated()
    {
        MigrationTestDatabase db = new();
        db.Schema.EnsureSchema();
        SqlitePools.Release(db.DatabasePath);
        db.ExecuteScript("INSERT INTO player_profiles (player_name, display_name) VALUES ('Alice', 'Alice');");
        return db;
    }

    public static QuestDefinition Shipped(string key) =>
        ContentCatalog.Shipped.TryGetQuest(key, out QuestDefinition? quest) ? quest : throw new InvalidOperationException(key);

    public sealed class Run : IDisposable
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

        public void Flush() => Assert.True(_writes.Flush(TimeSpan.FromSeconds(5)));

        public PersistedPlayerInventory Rows() => World.LoadPlayerInventory(Player);

        public void Fund(long gold)
        {
            Assert.True(Inventory.TryAddGold(Player, gold));
            Flush();
        }

        public void Kill(ContentCatalog content, string kind, int times = 1)
        {
            for (int i = 0; i < times; i++)
            {
                Inventory.RecordKill(Player, content, kind);
            }
        }

        public void Dispose()
        {
            _writes.Dispose();
            _bundle.Dispose();
            SqlitePools.Release(_databasePath);
        }
    }
}
