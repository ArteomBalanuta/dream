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

// PersistentWorldSeedDeterminismIntegrationTests' placed-cube damage regression on a persistent
// world (the ephemeral control case is EphemeralWorldPlacedCubeDamageIntegrationTests). Its own
// class so it runs in parallel with the seed runs.
public class PersistentWorldPlacedCubeDamageIntegrationTests(ITestOutputHelper output)
{
    // Regression for a production bug: on a persistent world a player-placed cube's damage was
    // reverted by the next hit until a 5 s checkpoint refreshed the in-memory persisted copy.
    // ProcessCombatUseAction calls RepairCubeRecordFromPersistence before every hit, which treated
    // a live health that differed from the cached PersistedPlacedCube (still full health from
    // placement) as an identity mismatch and rebuilt the record with the cached health. Fixed: the
    // repair compares identity (id, owner) only, and a non-fatal hit refreshes the cached health.
    [Fact]
    public void PersistentWorld_PlacedCube_KeepsItsDamageBetweenHits_AndBreaksBeforeTheFirstCheckpoint()
    {
        using TempDatabase database = new();
        using PersistentScriptedServer server = new(database.OpenBundle());
        server.RunScript(CubeOnlyScript, ticks: CheckpointTicks - 10);

        output.WriteLine($"persistent: {DescribeHits(server)}; fractured at {server.BuiltCubeFractureTick}");
        AssertCubeLosesHealthEveryHitAndBreaks(server);
    }

}
