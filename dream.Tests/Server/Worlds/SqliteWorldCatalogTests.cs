using Xunit;
using Dream.Tests.Server.Persistence.Migrations;
using Dream.Server.Worlds;
using Dream.Simulation.World;

namespace Dream.Tests.Server.Worlds;

public sealed class SqliteWorldCatalogTests
{
    [Fact]
    public void Create_TrimsNameStoresSeedAndListsInIdOrder()
    {
        using WorldCatalogTestDatabase db = new();

        WorldRecord a = db.Catalog.Create(new WorldCreateOptions("  Alpha  ", 20));
        WorldRecord b = db.Catalog.Create(new WorldCreateOptions("Beta", 30, Seed: 42UL));

        Assert.Equal("Alpha", a.Descriptor.Name);
        Assert.Equal(20, a.Descriptor.Width);
        Assert.Equal(20, a.Descriptor.Depth);
        Assert.Equal(8, a.Descriptor.Height);
        Assert.NotNull(a.Seed);
        Assert.Equal(42UL, b.Seed);
        Assert.Equal(0, a.WorldTimeTicks);
        Assert.Equal(MigrationTestDatabase.ShippedLatestVersion, a.CreatedSchemaVersion);
        Assert.Equal([a.Descriptor.Id, b.Descriptor.Id], db.Catalog.List().Select(r => r.Descriptor.Id).ToArray());
        Assert.Equal(b, db.Catalog.TryGet(b.Descriptor.Id));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_RejectsBlankName(string name)
    {
        using WorldCatalogTestDatabase db = new();
        Assert.Throws<ArgumentException>(() => db.Catalog.Create(new WorldCreateOptions(name, 20)));
    }

    [Fact]
    public void Create_RejectsTooLongNameAndBadSizes()
    {
        using WorldCatalogTestDatabase db = new();
        Assert.Throws<ArgumentException>(() => db.Catalog.Create(new WorldCreateOptions(new string('x', 65), 20)));
        Assert.Equal(64, db.Catalog.Create(new WorldCreateOptions(new string('x', 64), 20)).Descriptor.Name.Length);
        Assert.Throws<ArgumentException>(() => db.Catalog.Create(new WorldCreateOptions("small", 4)));
        Assert.Throws<ArgumentException>(() => db.Catalog.Create(new WorldCreateOptions("big", 101)));
    }

    [Fact]
    public void Create_NameConflictIgnoresCase()
    {
        using WorldCatalogTestDatabase db = new();
        db.Catalog.Create(new WorldCreateOptions("Alpha", 20));

        Assert.Throws<WorldNameTakenException>(() => db.Catalog.Create(new WorldCreateOptions(" ALPHA ", 20)));
        Assert.Equal("Alpha", db.Catalog.TryGetByName("alpha")!.Descriptor.Name);
        Assert.Single(db.Catalog.List());
    }

    [Fact]
    public void Copy_DeepCopiesEveryPerWorldTableWithSameIdsAndStaysIndependent()
    {
        using WorldCatalogTestDatabase db = new();
        db.SeedWorldOne();

        WorldRecord source = db.Catalog.TryGet(WorldId.Default)!;
        WorldRecord copy = db.Catalog.Copy(WorldId.Default, "Copy");
        long copyId = copy.Descriptor.Id.Value;

        Assert.NotEqual(WorldId.Default, copy.Descriptor.Id);
        Assert.Equal(source.Seed, copy.Seed);
        Assert.Equal(source.WorldTimeTicks, copy.WorldTimeTicks);
        Assert.Equal(source.Descriptor with { Id = copy.Descriptor.Id, Name = "Copy" }, copy.Descriptor);
        foreach (string table in WorldCatalogTestDatabase.PerWorldTables)
        {
            Assert.Equal(db.Count(table, 1), db.Count(table, copyId));
            Assert.True(db.Count(table, copyId) > 0, table);
        }

        Assert.Equal(
            Convert.ToInt64(db.Scalar("SELECT SUM(object_id) FROM placed_cube_instances WHERE world_id = 1")),
            Convert.ToInt64(db.Scalar($"SELECT SUM(object_id) FROM placed_cube_instances WHERE world_id = {copyId}")));
        Assert.Equal(
            1L,
            Convert.ToInt64(db.Scalar(
                $"SELECT COUNT(*) FROM player_inventory_slots WHERE world_id = {copyId} AND item_instance_id = 30")));

        // Independent afterwards: edits to the copy do not reach the source.
        db.Execute($"DELETE FROM placed_cube_instances WHERE world_id = {copyId} AND object_id = 10;");
        Assert.Equal(2L, db.Count("placed_cube_instances", 1));
        Assert.Equal(1L, db.Count("placed_cube_instances", copyId));
    }

    [Fact]
    public void Copy_UnknownSourceOrTakenNameChangesNothing()
    {
        using WorldCatalogTestDatabase db = new();
        db.SeedWorldOne();

        Assert.Throws<WorldNotFoundException>(() => db.Catalog.Copy(new WorldId(99), "x"));
        Assert.Throws<WorldNameTakenException>(() => db.Catalog.Copy(WorldId.Default, "DEFAULT"));
        Assert.Throws<ArgumentException>(() => db.Catalog.Copy(WorldId.Default, "  "));
        Assert.Single(db.Catalog.List());
    }

    [Fact]
    public void Delete_RemovesOnlyThatWorld()
    {
        using WorldCatalogTestDatabase db = new();
        db.SeedWorldOne();
        WorldRecord copy = db.Catalog.Copy(WorldId.Default, "Copy");

        Assert.True(db.Catalog.Delete(copy.Descriptor.Id));

        Assert.Null(db.Catalog.TryGet(copy.Descriptor.Id));
        foreach (string table in WorldCatalogTestDatabase.PerWorldTables)
        {
            Assert.Equal(0L, db.Count(table, copy.Descriptor.Id.Value));
            Assert.True(db.Count(table, 1) > 0, table);
        }

        Assert.Equal(1L, Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM player_profiles")));
        Assert.False(db.Catalog.Delete(copy.Descriptor.Id));
    }

    [Fact]
    public void CopyAndDelete_LeaveTheMigratedDatabaseForeignKeyClean()
    {
        using WorldCatalogTestDatabase db = new();
        db.SeedWorldOne();
        Assert.Equal(0L, Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM pragma_foreign_key_check")));

        WorldRecord copy = db.Catalog.Copy(WorldId.Default, "Copy");
        Assert.Equal(0L, Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM pragma_foreign_key_check")));
        Assert.Equal("ok", db.Scalar("PRAGMA integrity_check"));

        Assert.True(db.Catalog.Delete(copy.Descriptor.Id));
        Assert.Equal(0L, Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM pragma_foreign_key_check")));
        Assert.Equal("ok", db.Scalar("PRAGMA integrity_check"));
    }

    [Fact]
    public void Resolve_DefaultCreatesDefaultOnEmptyDatabaseThenReusesIt()
    {
        using WorldCatalogTestDatabase db = new();

        WorldRecord first = db.Catalog.Resolve(WorldSelector.Default, 24, out bool created);
        Assert.True(created);
        Assert.Equal("default", first.Descriptor.Name);
        Assert.Equal(24, first.Descriptor.Width);

        WorldRecord again = db.Catalog.Resolve(WorldSelector.Default, 40, out created);
        Assert.False(created);
        Assert.Equal(first, again);
    }

    [Fact]
    public void Resolve_DefaultPrefersWorldOneElseLowestId()
    {
        using WorldCatalogTestDatabase db = new();
        WorldRecord one = db.Catalog.Resolve(WorldSelector.Default, 20, out _);
        WorldRecord two = db.Catalog.Create(new WorldCreateOptions("Two", 20));
        WorldRecord three = db.Catalog.Create(new WorldCreateOptions("Three", 20));

        Assert.Equal(one.Descriptor.Id, db.Catalog.Resolve(WorldSelector.Default, 20, out _).Descriptor.Id);

        db.Catalog.Delete(one.Descriptor.Id);
        Assert.Equal(two.Descriptor.Id, db.Catalog.Resolve(WorldSelector.Default, 20, out bool created).Descriptor.Id);
        Assert.False(created);
        Assert.NotEqual(three.Descriptor.Id, two.Descriptor.Id);
    }

    [Fact]
    public void Resolve_IdMustExist_NameIsOpenedOrCreated()
    {
        using WorldCatalogTestDatabase db = new();
        WorldRecord alpha = db.Catalog.Create(new WorldCreateOptions("Alpha", 20));

        Assert.Throws<WorldNotFoundException>(
            () => db.Catalog.Resolve(WorldSelector.Parse("999"), 20, out _));
        Assert.Equal(
            alpha.Descriptor.Id,
            db.Catalog.Resolve(WorldSelector.Parse(alpha.Descriptor.Id.ToString()), 20, out bool created).Descriptor.Id);
        Assert.False(created);

        Assert.Equal(alpha.Descriptor.Id, db.Catalog.Resolve(WorldSelector.Parse("alpha"), 20, out created).Descriptor.Id);
        Assert.False(created);

        WorldRecord made = db.Catalog.Resolve(WorldSelector.Parse("Gamma"), 33, out created);
        Assert.True(created);
        Assert.Equal(33, made.Descriptor.Width);
        Assert.Equal(2, db.Catalog.List().Count);
    }

    [Fact]
    public void Read_RejectsCorruptSeed()
    {
        using WorldCatalogTestDatabase db = new();
        WorldRecord a = db.Catalog.Create(new WorldCreateOptions("Alpha", 20));
        db.Execute($"UPDATE worlds SET seed = 'not a number' WHERE world_id = {a.Descriptor.Id.Value};");

        Assert.Throws<InvalidDataException>(() => db.Catalog.TryGet(a.Descriptor.Id));
    }

    [Fact]
    public void Read_RejectsCorruptWorldTimeTicks()
    {
        using WorldCatalogTestDatabase db = new();
        WorldRecord a = db.Catalog.Create(new WorldCreateOptions("Alpha", 20));
        db.Execute($"UPDATE worlds SET world_time_ticks = 'soon' WHERE world_id = {a.Descriptor.Id.Value};");
        Assert.Throws<InvalidDataException>(() => db.Catalog.TryGet(a.Descriptor.Id));

        db.Execute($"UPDATE worlds SET world_time_ticks = -5 WHERE world_id = {a.Descriptor.Id.Value};");
        Assert.Throws<InvalidDataException>(() => db.Catalog.TryGet(a.Descriptor.Id));
    }

    [Theory]
    [InlineData("2024")]
    [InlineData(" 7 ")]
    [InlineData("a\tb")]
    [InlineData("a\nb")]
    public void CreateAndCopy_RejectAllDigitAndControlCharacterNames(string name)
    {
        using WorldCatalogTestDatabase db = new();
        WorldRecord a = db.Catalog.Create(new WorldCreateOptions("Alpha", 20));

        Assert.Throws<ArgumentException>(() => db.Catalog.Create(new WorldCreateOptions(name, 20)));
        Assert.Throws<ArgumentException>(() => db.Catalog.Copy(a.Descriptor.Id, name));
        Assert.Single(db.Catalog.List());
    }

    [Fact]
    public void Create_AcceptsNamesThatMerelyContainDigits()
    {
        using WorldCatalogTestDatabase db = new();
        Assert.Equal("w2024", db.Catalog.Create(new WorldCreateOptions("w2024", 20)).Descriptor.Name);
    }

    [Fact]
    public void Copy_OfASourceWithoutASeed_StoresOneSeedSharedByBoth()
    {
        using WorldCatalogTestDatabase db = new();
        WorldRecord a = db.Catalog.Create(new WorldCreateOptions("Alpha", 20));
        db.Execute($"UPDATE worlds SET seed = NULL WHERE world_id = {a.Descriptor.Id.Value};");

        WorldRecord copy = db.Catalog.Copy(a.Descriptor.Id, "Copy");

        WorldRecord source = db.Catalog.TryGet(a.Descriptor.Id)!;
        Assert.NotNull(source.Seed);
        Assert.Equal(source.Seed, copy.Seed);
    }
}
