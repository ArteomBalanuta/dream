using System.Globalization;
using Xunit;
using Xunit.Abstractions;
using OpenTK.Mathematics;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Tests.Server.Worlds;

namespace Dream.Tests.Integration;

// Persistence wave E tester: a player and an enemy saved inside a terrain cell that reset-terrain then
// restores (wave E plan risk 9, recorded by workstream C at restore level only). Here the restored world
// is hosted and ticked: what happens to them in play. Observation first, then the invariants the
// server must keep whatever the answer is.
public sealed class RestoredCellOccupantPlayTests(ITestOutputHelper output)
{
    [Fact]
    public async Task APlayerAndAnEnemySavedInsideARestoredCell_AreHostedAndTicked_WithoutAnInvariantViolation()
    {
        using TerrainCliFixture f = new();
        GeometryCellKey cell = f.FractureTerrain(flat: 1)[0];
        string center = string.Join(", ", new[] { cell.X + 0.5f, cell.Y + 0.5f, cell.Z + 0.5f }.Select(v => v.ToString("0.0#", CultureInfo.InvariantCulture)));
        f.Exec("INSERT OR IGNORE INTO player_profiles (player_name, display_name) VALUES ('Name alice', 'Name alice');");
        f.Exec($"INSERT INTO player_world_state (world_id, player_name, position_x, position_y, position_z, facing_x, facing_y, facing_z) VALUES (1, 'Name alice', {center}, 0, 0, 1);");
        f.Exec($"INSERT INTO enemy_instances (world_id, object_id, enemy_kind, display_name, position_x, position_y, position_z, facing_x, facing_y, facing_z, current_hp, max_hp) VALUES (1, 800001, 'wolf', 'Wolf', {center}, 0, 0, 1, 10, 10);");
        Assert.Equal(0, f.Cli("reset-terrain", "1", "--yes").Exit);

        await using WireLoopbackServer server = await WireLoopbackServer.StartAsync(StoredWorldDb.CreateHost(f.Database.OpenBundle(), null));
        Assert.NotNull(server.Runtime.Map.Cubes[cell.X, cell.Y, cell.Z]);
        await using WireLoopbackClient client = await server.ConnectAndJoinAsync("alice");

        Vector3 cellCenter = new(cell.X + 0.5f, cell.Y + 0.5f, cell.Z + 0.5f);
        output.WriteLine($"cell {cell}, centre {cellCenter}");
        for (int tick = 0; tick <= 960; tick++)
        {
            if (tick % 64 == 0)
            {
                ActorSnapshot player = server.Runtime.CaptureActorSnapshots().First(a => a.SessionId == "alice");
                string wolf = server.Runtime.Enemies.ActiveActors.Count == 0
                    ? "(none active)"
                    : string.Join(" | ", server.Runtime.Enemies.ActiveActors.Select(e => $"{Describe(server, e.Position)} hp={e.Combatant.Health.CurrentHealth}"));
                output.WriteLine($"t={tick,4}  player {Describe(server, player.Position)}   enemies {wolf}");
                Assert.True(float.IsFinite(player.Position.X + player.Position.Y + player.Position.Z));
                foreach (var enemy in server.Runtime.Enemies.ActiveActors)
                {
                    Assert.True(float.IsFinite(enemy.Position.X + enemy.Position.Y + enemy.Position.Z));
                }
            }

            await server.TickAsync();
            Assert.True(await server.FlushAllAsync());
        }

        // Can the player walk out? Hold the move key (forward, then each other direction) for 3 s each.
        foreach ((string name, PlayerMoveIntent move, Vector3 facing) in new[]
        {
            ("forward", new PlayerMoveIntent(1f, 0f), Vector3.UnitZ),
            ("right", new PlayerMoveIntent(0f, 1f), Vector3.UnitZ),
            ("back", new PlayerMoveIntent(-1f, 0f), Vector3.UnitZ),
            ("left", new PlayerMoveIntent(0f, -1f), Vector3.UnitZ)
        })
        {
            for (int tick = 0; tick < 192; tick++)
            {
                await client.SendCommandAsync(PlayerInteractionMode.Combat, move, facing, attack: false);
                await server.TickAsync();
                Assert.True(await server.FlushAllAsync());
            }

            ActorSnapshot after = server.Runtime.CaptureActorSnapshots().First(a => a.SessionId == "alice");
            output.WriteLine($"walked {name} for 3 s: player {Describe(server, after.Position)}");
        }

        // Control: a second player, not saved anywhere, with the same input.
        await using WireLoopbackClient control = await server.ConnectAndJoinAsync("bob");
        ActorSnapshot start = server.Runtime.CaptureActorSnapshots().First(a => a.SessionId == "bob");
        for (int tick = 0; tick < 192; tick++)
        {
            await control.SendCommandAsync(PlayerInteractionMode.Combat, new PlayerMoveIntent(1f, 0f), Vector3.UnitZ, attack: false);
            await server.TickAsync();
            Assert.True(await server.FlushAllAsync());
        }

        ActorSnapshot end = server.Runtime.CaptureActorSnapshots().First(a => a.SessionId == "bob");
        output.WriteLine($"control player walked forward for 3 s: {Describe(server, start.Position)} -> {Describe(server, end.Position)}");
        Assert.True(Vector3.Distance(start.Position, end.Position) > 5f, "the control player never moved: the walking input in this test is not valid");
        Assert.Empty(server.Runtime.Map.FindInvariantViolations());
    }

    private static string Describe(WireLoopbackServer server, Vector3 p)
    {
        int x = (int)MathF.Floor(p.X);
        int y = (int)MathF.Floor(p.Y);
        int z = (int)MathF.Floor(p.Z);
        Map map = server.Runtime.Map;
        bool inBounds = x >= 0 && z >= 0 && y >= 0 && x < map.Width && z < map.Depth && y < map.Height;
        bool inside = inBounds && map.Cubes[x, y, z] != null;
        return $"({p.X:0.00},{p.Y:0.00},{p.Z:0.00}){(inside ? " IN-SOLID" : "")}";
    }
}
