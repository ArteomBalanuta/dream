using Xunit;
using Dream.Client.Startup;
using Dream.Server;
using Dream.Server.Persistence;
using Dream.Server.Worlds;
using Dream.Simulation.Persistence;
using Dream.Simulation.Runtime;
using Dream.Simulation.World.Terrain;
using Dream.Tests.Server.Worlds;

namespace Dream.Tests.Server;

// Persistence wave E, fix loop 1: startup failure wording, resource release on a failed stored-world
// start, and reset-terrain's lost-override rubble and inside-cell report.
public sealed class StoredTerrainFixLoopServerTests
{
    private const string HealedCategory = "server.persistence.terrain-override-healed";

    [Fact]
    public void AnInvalidDataExceptionThatIsNotTheStoredTerrain_GetsANeutralExit5Message()
    {
        List<string> log = [];

        using DedicatedServerHost? host = ServerBootstrap.TryCreateServerModeHost(
            () => throw new InvalidDataException("some other file is bad"),
            log.Add,
            out int exitCode,
            "stone");

        Assert.Null(host);
        Assert.Equal(ServerBootstrap.WorldCannotBeOpenedExitCode, exitCode);
        string line = Assert.Single(log);
        Assert.Contains("invalid data: some other file is bad", line, StringComparison.Ordinal);
        Assert.DoesNotContain("stored terrain", line, StringComparison.Ordinal);
    }

    [Fact]
    public void ACorruptStoredBody_RaisesStoredTerrainInvalid_AndSaysSo()
    {
        using StoredWorldFixture fixture = new();
        fixture.Damage(StoredWorldHostingTests.Damage.CorruptBytes);
        using SpyProfiles spy = new();
        ServerPersistenceBundle bundle = WithSpyProfiles(fixture.OpenBundle(), spy);

        Assert.Throws<StoredTerrainInvalidException>(() => ServerBootstrap.CreateRuntimeHost(bundle, null));

        List<string> log = [];
        using DedicatedServerHost? host = ServerBootstrap.TryCreateServerModeHost(
            () => ServerBootstrap.CreateAuthoritativeHost(WithSpyProfiles(fixture.OpenBundle(), new SpyProfiles())),
            log.Add,
            out int exitCode,
            "stone");
        Assert.Null(host);
        Assert.Equal(ServerBootstrap.WorldCannotBeOpenedExitCode, exitCode);
        Assert.Contains(log, l => l.Contains("the stored terrain is invalid", StringComparison.Ordinal));
    }

    [Fact]
    public void AFailedStoredWorldHostStart_DisposesThePersistenceBundleOnce()
    {
        using StoredWorldFixture fixture = new();
        fixture.Damage(StoredWorldHostingTests.Damage.CorruptBytes);
        SpyProfiles spy = new();

        Assert.Throws<StoredTerrainInvalidException>(
            () => ServerBootstrap.CreateAuthoritativeHost(WithSpyProfiles(fixture.OpenBundle(), spy)));

        Assert.Equal(1, spy.DisposeCount);
    }

    [Fact]
    public async Task AFailedIntegratedStoredStart_ReleasesTheBundle_AndReportsNotReady()
    {
        using StoredWorldFixture fixture = new();
        fixture.Damage(StoredWorldHostingTests.Damage.CorruptBytes);
        SpyProfiles spy = new();

        await using IntegratedLocalServerHandle handle = await IntegratedLocalServerHandle.StartAsync(
            hostFactory: () => ServerBootstrap.CreateAuthoritativeHost(WithSpyProfiles(fixture.OpenBundle(), spy)));

        Assert.False(handle.IsReady);
        Assert.NotNull(handle.ErrorMessage);
        Assert.Equal(1, spy.DisposeCount);
        // The database is free: nothing of the failed start still holds it.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _ = fixture.OpenBundle();
    }

    [Fact]
    public void ALostOverrideWithRubble_IsReset_ThenRestartsWithoutAHeal()
    {
        using TerrainCliFixture f = new();
        f.FractureTerrain(flat: 2);
        long rubbleBefore = f.Scalar("SELECT COUNT(*) FROM cube_fracture_pieces WHERE world_id = 1;");
        Assert.True(rubbleBefore > 0);
        f.Exec("DELETE FROM terrain_cell_overrides WHERE world_id = 1;");

        TerrainResetResult result = f.Catalog.ResetTerrain(Dream.Simulation.World.WorldId.Default);

        Assert.Equal(0, result.OverridesCleared);
        Assert.Equal(rubbleBefore, result.RubbleRowsDeleted);
        Assert.Equal(0, f.Scalar("SELECT COUNT(*) FROM cube_fracture_pieces WHERE world_id = 1;"));
        using TerrainRig rig = f.OpenRig(Dream.Simulation.World.WorldId.Default);
        List<(string Category, string Message)> lines = rig.Restore();
        Assert.DoesNotContain(lines, line => line.Category == HealedCategory);
    }

    [Fact]
    public void DebrisOfADestroyedPlayerCubeInAnAirCell_SurvivesReset()
    {
        using TerrainCliFixture f = new();
        f.FractureTerrain(flat: 1);
        // Origin 999999 is a long-gone player cube; the cell (2, 6, 2) is air in the base terrain.
        f.Exec(
            "INSERT INTO cube_fracture_pieces (world_id, object_id, origin_object_id, piece_index, owner_player_name, parent_cell_x, parent_cell_y, parent_cell_z, center_x, center_y, center_z, side_length, material_id) " +
            "VALUES (1, 900100, 999999, 0, NULL, 2, 6, 2, 2.1, 6.1, 2.1, 0.5, 'stone');");

        _ = f.Catalog.ResetTerrain(Dream.Simulation.World.WorldId.Default);

        Assert.Equal(1, f.Scalar("SELECT COUNT(*) FROM cube_fracture_pieces WHERE object_id = 900100;"));
    }

    [Fact]
    public void RubbleOfALivePlacedCubesCell_SurvivesTheLostOverrideRule()
    {
        using TerrainCliFixture f = new();
        List<Dream.Simulation.World.Geometry.GeometryCellKey> removed = f.FractureTerrain(flat: 2);
        using (TerrainRig rig = f.OpenRig(Dream.Simulation.World.WorldId.Default))
        {
            rig.Restore();
            rig.PlaceCube(removed[0]);
        }

        f.Exec("DELETE FROM terrain_cell_overrides WHERE world_id = 1;");
        long underCube = f.Scalar(
            $"SELECT COUNT(*) FROM cube_fracture_pieces WHERE world_id = 1 AND parent_cell_x = {removed[0].X} AND parent_cell_y = {removed[0].Y} AND parent_cell_z = {removed[0].Z};");
        Assert.True(underCube > 0);

        _ = f.Catalog.ResetTerrain(Dream.Simulation.World.WorldId.Default);

        Assert.Equal(
            underCube,
            f.Scalar($"SELECT COUNT(*) FROM cube_fracture_pieces WHERE world_id = 1 AND parent_cell_x = {removed[0].X} AND parent_cell_y = {removed[0].Y} AND parent_cell_z = {removed[0].Z};"));
    }

    [Fact]
    public void ResetTerrain_ReportsPlayersAndEnemiesInsideRestoredCells_WithoutMovingThem()
    {
        using TerrainCliFixture f = new();
        Dream.Simulation.World.Geometry.GeometryCellKey cell = f.FractureTerrain(flat: 1)[0];
        string center = string.Join(", ", new[] { cell.X + 0.5f, cell.Y + 0.5f, cell.Z + 0.5f }
            .Select(v => v.ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture)));
        f.Exec("INSERT OR IGNORE INTO player_profiles (player_name, display_name) VALUES ('alice', 'Alice');");
        f.Exec("INSERT OR IGNORE INTO player_profiles (player_name, display_name) VALUES ('bob', 'Bob');");
        f.Exec($"INSERT INTO player_world_state (world_id, player_name, position_x, position_y, position_z, facing_x, facing_y, facing_z) VALUES (1, 'alice', {center}, 0, 0, 1);");
        f.Exec("INSERT INTO player_world_state (world_id, player_name, position_x, position_y, position_z, facing_x, facing_y, facing_z) VALUES (1, 'bob', 0.5, 5.5, 0.5, 0, 0, 1);");
        f.Exec($"INSERT INTO enemy_instances (world_id, object_id, enemy_kind, display_name, position_x, position_y, position_z, facing_x, facing_y, facing_z, current_hp, max_hp) VALUES (1, 800001, 'wolf', 'Wolf', {center}, 0, 0, 1, 10, 10);");

        (int exit, string line, _) = f.Cli("reset-terrain", "1", "--yes");

        Assert.Equal(0, exit);
        string[] fields = line.TrimEnd().Split('\t');
        Assert.Contains("inside=2", fields);
        Assert.Contains("inside_players=alice", fields);
        Assert.Equal(1, f.Scalar("SELECT COUNT(*) FROM player_world_state WHERE world_id = 1 AND player_name = 'alice';"));
        Assert.Equal(1, f.Scalar("SELECT COUNT(*) FROM enemy_instances WHERE world_id = 1 AND object_id = 800001;"));
    }

    [Fact]
    public void ResetTerrain_WithNobodyInside_ReportsInsideZero()
    {
        using TerrainCliFixture f = new();
        _ = f.FractureTerrain(flat: 1);

        TerrainResetResult result = f.Catalog.ResetTerrain(Dream.Simulation.World.WorldId.Default);

        Assert.Equal(0, result.Inside);
        Assert.Equal(0, result.PlayersInside);
        Assert.Null(result.PlayerNames);
    }

    private static ServerPersistenceBundle WithSpyProfiles(ServerPersistenceBundle b, SpyProfiles spy) =>
        new(b.Schema, spy.Wrap(b.PlayerProfiles), b.World, b.CombatFormulas, b.FormulaService, b.SimulationSeed,
            b.WorldTimeTicks, b.WorldId, b.Descriptor, b.TerrainChunks, b.StoredTerrain, b.TerrainPayload, b.HostLease);

    private sealed class SpyProfiles : IDisposable
    {
        private IPlayerProfileRepository? _inner;

        public int DisposeCount { get; private set; }

        public IPlayerProfileRepository Wrap(IPlayerProfileRepository inner)
        {
            _inner = inner;
            return new Forwarding(this);
        }

        public void Dispose() => DisposeCount++;

        private sealed class Forwarding(SpyProfiles owner) : IPlayerProfileRepository, IDisposable
        {
            public PersistedPlayerProfile GetOrCreate(string playerName) => owner._inner!.GetOrCreate(playerName);

            public PersistedPlayerProfile? TryGet(string playerName) => owner._inner!.TryGet(playerName);

            public void Save(PersistedPlayerProfile profile) => owner._inner!.Save(profile);

            public void Dispose() => owner.Dispose();
        }
    }
}
