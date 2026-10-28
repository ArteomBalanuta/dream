using Dream.Server.Persistence;
using Dream.Server.Persistence.Store;
using Dream.Simulation.Persistence;
using Dream.Simulation.Players;
using Microsoft.Data.Sqlite;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Server.Persistence.Store;

// Wave A (A2): the store helpers, the repository base and the three non-world repositories on it.
// The session shapes themselves are covered by SqliteStoreContractSmokeTests.
public sealed class SqliteStoreTests
{
    private const string ProbeTableSql = """
        CREATE TABLE store_probe (
            id INTEGER NOT NULL PRIMARY KEY,
            label TEXT NULL,
            big INTEGER NULL,
            ratio REAL NULL,
            at_utc TEXT NULL,
            px REAL NULL,
            py REAL NULL,
            pz REAL NULL,
            cx REAL NULL,
            cy REAL NULL,
            cz REAL NULL
        );
        """;

    private const string ProbeInsertSql = """
        INSERT INTO store_probe (id, label, big, ratio, at_utc, px, py, pz, cx, cy, cz)
        VALUES ($id, $label, $big, $ratio, $atUtc, $px, $py, $pz, $cx, $cy, $cz);
        """;

    private const string ProbeSelectSql = """
        SELECT label, big, ratio, at_utc, px, py, pz, cx, cy, cz
        FROM store_probe
        WHERE id = $id;
        """;

    [Fact]
    public void BindAndReadHelpers_RoundTripNullVectorUtcAndLongValues()
    {
        using PersistenceTestDatabase database = new();
        SqliteWorldStore store = new(database.Schema);
        CreateProbeTable(store);
        DateTime atUtc = new(2026, 10, 28, 13, 45, 12, DateTimeKind.Utc);
        atUtc = atUtc.AddTicks(1234567);
        Vector3 position = new(1.5f, -2.25f, 1e6f);
        Vector3 center = new(0.125f, 7f, -3.5f);

        using (SqliteSession session = store.OpenSession())
        {
            using SqliteCommand insert = session.CreateCommand(ProbeInsertSql);
            BindProbe(insert, 1, "full", long.MaxValue, 0.1, atUtc, position, center);
            Assert.Equal(1, insert.ExecuteNonQuery());
            // The same command again: Bind* reuses the existing parameters.
            BindProbe(insert, 2, null, long.MinValue, -0.5, atUtc, Vector3.Zero, null);
            Assert.Equal(1, insert.ExecuteNonQuery());
            Assert.Equal(11, insert.Parameters.Count);
        }

        using (SqliteSession session = store.OpenSession())
        {
            using SqliteCommand select = session.CreateCommand(ProbeSelectSql);
            select.BindInt64("$id", 1);
            using (SqliteDataReader reader = select.ExecuteReader())
            {
                Assert.True(reader.Read());
                Assert.Equal("full", reader.GetNullableString(0));
                Assert.Equal(long.MaxValue, reader.GetInt64(1));
                Assert.Equal(0.1, reader.GetDouble(2));
                Assert.Equal(SqliteSchemaBootstrapper.FormatUtc(atUtc), reader.GetString(3));
                DateTime readUtc = reader.GetUtc(3);
                Assert.Equal(atUtc, readUtc);
                Assert.Equal(DateTimeKind.Utc, readUtc.Kind);
                Assert.Equal(position, reader.GetVector3(4));
                Assert.Equal(center, reader.GetNullableVector3(7));
            }

            select.BindInt64("$id", 2);
            using (SqliteDataReader reader = select.ExecuteReader())
            {
                Assert.True(reader.Read());
                Assert.Null(reader.GetNullableString(0));
                Assert.Equal(long.MinValue, reader.GetInt64(1));
                Assert.Equal(Vector3.Zero, reader.GetVector3(4));
                Assert.Null(reader.GetNullableVector3(7));
                Assert.True(reader.IsDBNull(7));
                Assert.True(reader.IsDBNull(8));
                Assert.True(reader.IsDBNull(9));
            }
        }
    }

    [Fact]
    public void GetNullableVector3_IsNullWhenAnyComponentIsNull()
    {
        using PersistenceTestDatabase database = new();
        SqliteWorldStore store = new(database.Schema);
        CreateProbeTable(store);
        using SqliteSession session = store.OpenSession();
        using (SqliteCommand insert = session.CreateCommand(
            "INSERT INTO store_probe (id, cx, cy, cz) VALUES (1, 1.0, NULL, 3.0);"))
        {
            insert.ExecuteNonQuery();
        }

        using SqliteCommand select = session.CreateCommand("SELECT cx, cy, cz FROM store_probe WHERE id = 1;");
        using SqliteDataReader reader = select.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Null(reader.GetNullableVector3(0));
    }

    [Fact]
    public void RepositoryBaseHelpers_ExecuteQuerySingleQueryListAndQueryInt64()
    {
        using PersistenceTestDatabase database = new();
        ProbeRepository repository = new(new SqliteWorldStore(database.Schema));
        repository.CreateTable();

        Assert.Equal(1, repository.Insert(10, "ten"));
        Assert.Equal(1, repository.Insert(20, null));

        Assert.Equal(2L, repository.Count());
        Assert.Equal(10L, repository.FindBig("ten"));
        Assert.Null(repository.FindBig("missing"));
        Assert.Equal([10L, 20L], repository.AllBig());
    }

    [Fact]
    public void SavepointRollback_UndoesOnlyTheFailingWrite_AndTheGroupCommitsOnce()
    {
        using PersistenceTestDatabase database = new();
        SqlitePlayerProfileRepository profiles = new(database.Schema);
        using SqliteWriteBatch batch = new();
        batch.Activate();
        try
        {
            batch.BeginWrite();
            profiles.Save(Profile("kept-first"));
            batch.CompleteWrite();

            batch.BeginWrite();
            profiles.Save(Profile("rolled-back"));
            batch.RollBackWrite();

            batch.BeginWrite();
            profiles.Save(Profile("kept-second"));
            batch.CompleteWrite();

            batch.Commit();
        }
        finally
        {
            SqliteWriteBatch.Deactivate();
        }

        Assert.Equal(1L, batch.CommittedTransactions);
        Assert.Equal(1L, database.Count("SELECT COUNT(*) FROM player_profiles WHERE player_name = 'kept-first';"));
        Assert.Equal(1L, database.Count("SELECT COUNT(*) FROM player_profiles WHERE player_name = 'kept-second';"));
        Assert.Equal(0L, database.Count("SELECT COUNT(*) FROM player_profiles WHERE player_name = 'rolled-back';"));
    }

    [Fact]
    public void NonWorldRepositories_InsideABatch_EnlistAndCommitOnce()
    {
        using PersistenceTestDatabase database = new();
        SqlitePlayerProfileRepository profiles = new(database.Schema);
        SqliteCombatFormulaRepository formulas = new(database.Schema);
        SqliteWorldMetadataRepository metadata = new(database.Schema);
        long formulasBefore = database.Count("SELECT COUNT(*) FROM combat_formulas;");
        using SqliteWriteBatch batch = new();
        batch.Activate();
        try
        {
            batch.BeginWrite();
            profiles.Save(Profile("batched-a"));
            profiles.Save(Profile("batched-b"));
            formulas.Upsert(new CombatFormulaDefinition("batched_formula", "1 + 1", "test", true, default));
            WorldSimulationSeed seed = metadata.GetOrCreateSimulationSeed(static () => 42UL);
            Assert.True(seed.Created);
            // Reads inside the batch see the batch's own uncommitted writes.
            Assert.NotNull(profiles.TryGet("batched-a"));
            Assert.NotNull(formulas.TryGet("batched_formula"));
            Assert.True(metadata.TryGetSimulationSeed(out ulong read));
            Assert.Equal(42UL, read);
            batch.CompleteWrite();

            // Nothing is visible to another connection before the group commits.
            Assert.Equal(0L, database.Count("SELECT COUNT(*) FROM player_profiles WHERE player_name LIKE 'batched-%';"));
            batch.Commit();
        }
        finally
        {
            SqliteWriteBatch.Deactivate();
        }

        Assert.Equal(1L, batch.CommittedTransactions);
        Assert.Equal(2L, database.Count("SELECT COUNT(*) FROM player_profiles WHERE player_name LIKE 'batched-%';"));
        Assert.Equal(formulasBefore + 1, database.Count("SELECT COUNT(*) FROM combat_formulas;"));
        Assert.True(metadata.TryGetSimulationSeed(out ulong committed));
        Assert.Equal(42UL, committed);
    }

    [Fact]
    public void ProfileSave_StampsUpdatedUtcWithNow_AndKeepsCreatedUtc()
    {
        using PersistenceTestDatabase database = new();
        SqlitePlayerProfileRepository profiles = new(database.Schema);
        DateTime created = new(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        PersistedPlayerProfile profile = Profile("  stamped  ") with
        {
            DisplayName = " ",
            CreatedUtc = created,
            UpdatedUtc = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        DateTime before = DateTime.UtcNow;
        profiles.Save(profile);
        DateTime after = DateTime.UtcNow;

        PersistedPlayerProfile? read = profiles.TryGet(" stamped ");
        Assert.True(read.HasValue);
        Assert.Equal("stamped", read.Value.PlayerName);
        Assert.Equal("stamped", read.Value.DisplayName);
        Assert.Equal(created, read.Value.CreatedUtc);
        Assert.InRange(read.Value.UpdatedUtc, before, after);
        Assert.Equal(profile.Stats, read.Value.Stats);
        Assert.Equal(profile.InteractionMode, read.Value.InteractionMode);
    }

    [Fact]
    public void FormulaUpsert_KeepsAnExplicitUpdatedUtc_AndStampsNowForDefault()
    {
        using PersistenceTestDatabase database = new();
        SqliteCombatFormulaRepository formulas = new(database.Schema);
        DateTime explicitUtc = new(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc);

        formulas.Upsert(new CombatFormulaDefinition(" explicit ", " 2 * x ", " d ", false, explicitUtc));
        DateTime before = DateTime.UtcNow;
        formulas.Upsert(new CombatFormulaDefinition("stamped", "x", "d", true, default));
        DateTime after = DateTime.UtcNow;

        CombatFormulaDefinition? explicitRead = formulas.TryGet("explicit");
        Assert.True(explicitRead.HasValue);
        Assert.Equal("2 * x", explicitRead.Value.ExpressionText);
        Assert.Equal("d", explicitRead.Value.Description);
        Assert.False(explicitRead.Value.Enabled);
        Assert.Equal(explicitUtc, explicitRead.Value.UpdatedUtc);
        Assert.DoesNotContain(formulas.LoadAllEnabled(), f => f.FormulaKey == "explicit");

        CombatFormulaDefinition? stampedRead = formulas.TryGet("stamped");
        Assert.True(stampedRead.HasValue);
        Assert.InRange(stampedRead.Value.UpdatedUtc, before, after);
        Assert.Contains(formulas.LoadAllEnabled(), f => f.FormulaKey == "stamped");
    }

    private static PersistedPlayerProfile Profile(string name)
    {
        PersistedPlayerProfile profile = PersistedPlayerProfile.CreateDefault(name, DateTime.UtcNow);
        return profile with
        {
            InteractionMode = PlayerInteractionMode.Building,
            Stats = profile.Stats with { Strength = 17, MovementSpeed = 6.3f, Experience = 1234 },
        };
    }

    private static void CreateProbeTable(IWorldStore store)
    {
        using SqliteSession session = store.OpenSession();
        using SqliteCommand create = session.CreateCommand(ProbeTableSql);
        create.ExecuteNonQuery();
    }

    private static void BindProbe(
        SqliteCommand command,
        long id,
        string? label,
        long big,
        double ratio,
        DateTime atUtc,
        Vector3 position,
        Vector3? center)
    {
        command.BindInt64("$id", id);
        command.BindNullableText("$label", label);
        command.BindInt64("$big", big);
        command.BindReal("$ratio", ratio);
        command.BindUtc("$atUtc", atUtc);
        command.BindVector3("$px", "$py", "$pz", position);
        command.BindNullableVector3("$cx", "$cy", "$cz", center);
    }

    private sealed class ProbeRepository(IWorldStore store) : SqliteRepositoryBase(store)
    {
        public void CreateTable()
        {
            Execute(ProbeTableSql, 0, static (_, _) => { });
        }

        public int Insert(long big, string? label)
        {
            return Execute(
                "INSERT INTO store_probe (id, big, label) VALUES ($big, $big, $label);",
                (big, label),
                static (command, state) =>
                {
                    command.BindInt64("$big", state.big);
                    command.BindNullableText("$label", state.label);
                });
        }

        public long? FindBig(string label)
        {
            return QuerySingle(
                "SELECT big FROM store_probe WHERE label = $label;",
                label,
                static (command, value) => command.BindText("$label", value),
                static reader => reader.GetInt64(0));
        }

        public List<long> AllBig()
        {
            return QueryList("SELECT big FROM store_probe ORDER BY id;", static reader => reader.GetInt64(0));
        }

        public long Count()
        {
            return QueryInt64("SELECT COUNT(*) FROM store_probe;");
        }
    }
}
