using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server;
using Dream.Server.Persistence;
using Dream.Simulation.Combat;
using Dream.Simulation.Determinism;
using Dream.Simulation.Enemies;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using static Dream.Tests.Integration.PersistentWorldSeedDeterminismIntegrationTests;

namespace Dream.Tests.Integration;

// PersistentWorldPlacedCubeDamageIntegrationTests' control case on an ephemeral world, in its own
// class so the two scripted runs go in parallel.
public class EphemeralWorldPlacedCubeDamageIntegrationTests(ITestOutputHelper output)
{
    // The same cube script on the same production host without persistence: damage accumulates
    // and the cube breaks well before the first checkpoint, so a failure of the persistent-world
    // case in PersistentWorldPlacedCubeDamageIntegrationTests is persistence's.
    [Fact]
    public void EphemeralWorld_PlacedCube_KeepsItsDamageBetweenHits_AndBreaksBeforeTheFirstCheckpoint()
    {
        using PersistentScriptedServer server = new(ServerBootstrap.CreateAuthoritativeHost(persistence: null));
        server.RunScript(CubeOnlyScript, ticks: CheckpointTicks - 10);

        output.WriteLine($"ephemeral: {DescribeHits(server)}; fractured at {server.BuiltCubeFractureTick}");
        AssertCubeLosesHealthEveryHitAndBreaks(server);
    }
}
