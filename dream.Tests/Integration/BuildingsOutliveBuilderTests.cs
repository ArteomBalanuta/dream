using Microsoft.Data.Sqlite;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Server;
using Dream.Simulation.Players;
using static Dream.Tests.Integration.ObjectIdTestSupport;
using static Dream.Tests.Integration.PersistentWorldSeedDeterminismIntegrationTests;

namespace Dream.Tests.Integration;

// Wave C D: buildings outlive their builder. Deleting the owner's profile row sets the owner to
// NULL on the cube and rubble rows (ON DELETE SET NULL, migration 0005) instead of deleting them.
// After a restart both are restored with no owner, they are still checkpointed (a cube is captured
// when it has an ObjectId binding, whatever its owner), and the cube can still be damaged.
public sealed class BuildingsOutliveBuilderTests(ITestOutputHelper output)
{
    private const string NeedsAbc = "wave C: needs A+B+C, unskip at glue";

    [Fact(Skip = NeedsAbc)]
    public async Task DeletingTheOwnersProfile_KeepsTheCube_WhichIsRestoredOwnerless_CheckpointedAndDamageable()
    {
        using TempDatabase database = new();
        Vector3 buildCell;
        using (PersistentScriptedServer server = new(database.OpenBundle()))
        {
            buildCell = server.BuildCell;
            server.RunScript(Script, ticks: 20);
            Assert.NotNull(server.BuiltCubeId);
        }

        SqliteConnection.ClearAllPools();
        PersistedIds built = ReadIds(database.DatabasePath);
        long cubeObjectId = Assert.Single(built.Cubes);
        int maxHealth = (int)Scalar(database.DatabasePath, "SELECT max_health FROM placed_cube_instances;");
        string owner = OwnerOf(database.DatabasePath, "placed_cube_instances");
        DeleteProfile(database.DatabasePath, owner);
        Assert.Equal(1L, Scalar(database.DatabasePath, "SELECT COUNT(*) FROM placed_cube_instances WHERE owner_player_name IS NULL;"));

        // Restart: the cube is back in its cell, owned by nobody. A joining client hits it; the
        // damage reaches the database through the checkpoint on dispose.
        await using (WireLoopbackServer server = await WireLoopbackServer.StartAsync(
            ServerBootstrap.CreateAuthoritativeHost(database.OpenBundle())))
        {
            Assert.NotNull(server.Runtime.Map.TryGetCubeAtCell((int)buildCell.X, (int)buildCell.Y, (int)buildCell.Z));
            Assert.True(server.Runtime.ObjectIdentities.TryGetActorId(new Dream.Simulation.World.ObjectId(cubeObjectId), out _));
            await using WireLoopbackClient client = await server.ConnectAndJoinAsync(PrimarySession);
            for (int tick = 0; tick < 90; tick++)
            {
                await client.SendCommandAsync(PlayerInteractionMode.Combat, PlayerMoveIntent.None, Vector3.UnitX, attack: true);
                await server.TickAsync();
                Assert.True(await server.FlushAllAsync());
            }

            await server.DisconnectAsync(client);
        }

        SqliteConnection.ClearAllPools();
        PersistedIds after = ReadIds(database.DatabasePath);
        long damaged = Scalar(database.DatabasePath, $"SELECT COUNT(*) FROM placed_cube_instances WHERE object_id = {cubeObjectId} AND current_health < {maxHealth};");
        long rubble = Scalar(database.DatabasePath, "SELECT COUNT(*) FROM cube_fracture_pieces;");
        output.WriteLine($"cube {cubeObjectId}: damaged rows {damaged}, rubble pieces {rubble}, next_object_id {after.NextObjectId}");
        Assert.True(damaged == 1 || rubble > 0, "the ownerless cube took no damage after the restart");
        Assert.Equal(0L, Scalar(database.DatabasePath, "SELECT COUNT(*) FROM placed_cube_instances WHERE owner_player_name IS NOT NULL;"));
        Assert.Equal(0L, Scalar(database.DatabasePath, "SELECT COUNT(*) FROM cube_fracture_pieces WHERE owner_player_name IS NOT NULL;"));
    }

    [Fact(Skip = NeedsAbc)]
    public void DeletingTheOwnersProfile_KeepsTheRubble_WhichIsRestoredOwnerless_AndCheckpointedUnderTheSameIds()
    {
        using TempDatabase database = new();
        using (PersistentScriptedServer server = new(database.OpenBundle()))
        {
            server.RunScript(CubeOnlyScript, ticks: CheckpointTicks - 10);
            Assert.True(server.BuiltCubeFractureTick > 0);
        }

        SqliteConnection.ClearAllPools();
        PersistedIds before = ReadIds(database.DatabasePath);
        Assert.NotEmpty(before.Pieces);
        string owner = OwnerOf(database.DatabasePath, "cube_fracture_pieces");
        DeleteProfile(database.DatabasePath, owner);
        Assert.Equal(before.Pieces.Count, (int)Scalar(database.DatabasePath, "SELECT COUNT(*) FROM cube_fracture_pieces WHERE owner_player_name IS NULL;"));

        int restored;
        using (DedicatedServerHost host = ServerBootstrap.CreateAuthoritativeHost(database.OpenBundle()))
        {
            restored = host.Runtime!.Map.CaptureFracturePieces().Count();
            Assert.Equal(before.Pieces.Count, restored);
            Assert.True(host.Runtime.ObjectIds.Next >= before.NextObjectId);
        }

        SqliteConnection.ClearAllPools();
        PersistedIds after = ReadIds(database.DatabasePath);
        output.WriteLine($"{restored} ownerless pieces restored; origins {string.Join(",", before.Origins)}");
        Assert.Equal(before.Pieces, after.Pieces);
        Assert.Equal(before.Origins, after.Origins);
        Assert.Equal(0L, Scalar(database.DatabasePath, "SELECT COUNT(*) FROM cube_fracture_pieces WHERE owner_player_name IS NOT NULL;"));
    }

    private static string OwnerOf(string databasePath, string table)
    {
        using SqliteConnection connection = Open(databasePath);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT DISTINCT owner_player_name FROM {table};";
        return (string)command.ExecuteScalar()!;
    }

    // Foreign keys must be ON for the connection, or SET NULL does not fire.
    private static void DeleteProfile(string databasePath, string playerName)
    {
        using SqliteConnection connection = Open(databasePath, readOnly: false);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; DELETE FROM player_profiles WHERE player_name = $name;";
        command.Parameters.AddWithValue("$name", playerName);
        Assert.Equal(1, command.ExecuteNonQuery());
    }
}
