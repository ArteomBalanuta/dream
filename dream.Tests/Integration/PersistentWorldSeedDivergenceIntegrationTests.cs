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

// PersistentWorldSeedDeterminismIntegrationTests' second-database run: a fresh database gets its own
// seed and the same script diverges at a damage roll. Its own class so it runs in parallel with
// the restart replay; the scripted server and helpers are the main class's.
public class PersistentWorldSeedDivergenceIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public void SecondDatabase_GetsADifferentSeed_AndDivergesAtADamageRoll_NotBefore()
    {
        using TempDatabase left = new();
        using TempDatabase right = new();
        using PersistentScriptedServer first = new(left.OpenBundle());
        using PersistentScriptedServer second = new(right.OpenBundle());

        Assert.NotEqual(first.Seed, second.Seed);
        Assert.Equal(first.Hash(), second.Hash());

        int firstDivergence = -1;
        for (int tick = 1; tick <= Ticks; tick++)
        {
            ScriptStep step = Script(tick, first);
            first.Advance(tick, step);
            second.Advance(tick, step);
            if (firstDivergence < 0 && first.Hash() != second.Hash())
            {
                firstDivergence = tick;
                output.WriteLine($"tick {tick}: {first.CurrentParts} vs {second.CurrentParts}");
            }

            // Every assertion below reads only the first divergence, the two first hits and the
            // hit ticks up to the divergence, none of which a later tick can change. Once all of
            // them are known the rest of the script cannot alter the outcome, so stop there.
            if (firstDivergence > 0 && first.FirstHitTick > 0 && second.FirstHitTick > 0)
            {
                break;
            }
        }

        RunRecord a = first.Record;
        RunRecord b = second.Record;
        int firstHit = Math.Min(a.FirstHitTick, b.FirstHitTick);
        output.WriteLine($"first hits {a.FirstHitTick}/{b.FirstHitTick}, first divergence {firstDivergence}");
        Assert.True(firstHit > 0, "No hit landed.");
        Assert.True(firstDivergence > 0, "Two worlds with different seeds never diverged.");
        Assert.True(firstDivergence >= FightFrom, $"Diverged at tick {firstDivergence}, before combat started.");
        Assert.True(firstDivergence >= firstHit, $"Diverged at tick {firstDivergence}, before the first hit at {firstHit}.");
        // The divergence is caused by a roll: a hit landed on one of the two servers in that tick.
        Assert.True(
            a.HitTicks.Contains(firstDivergence) || b.HitTicks.Contains(firstDivergence),
            $"Diverged at tick {firstDivergence}, where neither server rolled a hit.");
    }
}
