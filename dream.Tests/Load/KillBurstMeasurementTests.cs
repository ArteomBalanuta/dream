using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Characters;
using Dream.Simulation.Runtime;
using Dream.Tests.Server.Persistence.Migrations;
using Dream.Tests.Simulation.Runtime;

namespace Dream.Tests.Load;

// Wave I: 100 kills with a table that always drops, on the production repository and write queue, at
// the 5 kills per second plan risk 2 names, in real time. Each kill is a real combat action through
// ResolveEnemyHit, so it runs the delete, the grant (4 writes) and a push built on the tick, as the host
// does. Ten characters take turns because a backpack holds 16 items and one character can attack about
// once a second. Release, DREAM_LOAD_TEST=1. The tick gate applies to Release only; Debug prints it.
[Collection(TimingSensitiveCollection.Name)]
public sealed class KillBurstMeasurementTests(ITestOutputHelper output)
{
    private const int Kills = 100;
    private const int Characters = 10;
    private const int TicksPerKill = SimulationClock.AuthoritativeTicksPerSecond / 5;
    private const double TickMilliseconds = 1000d / SimulationClock.AuthoritativeTicksPerSecond;

    [LoadFact]
    public void Kills100_WithASureDrop_DrainTheQueueAtOnce_AndLoseNoItem()
    {
        using MigrationTestDatabase db = new();
        db.Schema.EnsureSchema();
        long itemsBefore;
        long itemsAfter;
        List<double> tickMilliseconds = [];
        List<double> killTickMilliseconds = [];
        int peakPending = 0;
        int pushes = 0;
        double drainMilliseconds;
        long failedWrites;
        using (WaveIRuntimeKit.RealRun run = new(db))
        {
            WorldSessionRuntime runtime = run.Runtime;
            runtime.LootContent = WaveIRuntimeKit.AlwaysDropCatalog(drops: 1);
            PlayerSessionId[] sessions = [.. Enumerable.Range(0, Characters).Select(i => new PlayerSessionId($"burst-{i}"))];
            foreach (PlayerSessionId session in sessions)
            {
                // Seated the way a join does: the profile row first, which the inventory rows need.
                ConnectedPlayer player = runtime.AttachRemoteSession(session, session.Value, new CharacterKey(session.Value));
                runtime.RestorePersistedPlayerState(session, session.Value, session.Value, account: null);
                Assert.True(runtime.InventoryService.IsLoaded(player.CharacterKey.Value));
            }

            run.Advance(3);
            foreach (PlayerSessionId session in sessions)
            {
                WaveIRuntimeKit.EnterCombat(runtime, session);
            }

            run.Advance(TicksPerKill);
            Assert.True(runtime.PersistenceWrites!.Flush(TimeSpan.FromSeconds(30)));
            itemsBefore = ItemCount(db);

            List<SessionInventoryPush> drained = [];
            ulong sequence = 1UL << 40;
            int kills = 0;
            long lastKillTimestamp = 0;
            long nextTick = Stopwatch.GetTimestamp();
            long tickPeriod = (long)(Stopwatch.Frequency * TickMilliseconds / 1000d);
            for (int tick = 0; kills < Kills; tick++)
            {
                // Paced like the service: the queue gets the wall time a real server would give it.
                while (Stopwatch.GetTimestamp() < nextTick)
                {
                    Thread.SpinWait(64);
                }

                nextTick += tickPeriod;
                long start = Stopwatch.GetTimestamp();
                bool killTick = tick % TicksPerKill == 0 && runtime.Enemies.ActiveActors.Count > 0;
                if (killTick)
                {
                    PlayerSessionId killer = sessions[kills % Characters];
                    IAuthoritativePlayerTarget target = WaveIRuntimeKit.Target(runtime, killer.Value);
                    target.Combatant.Health.SetAuthoritativeState(target.Combatant.Health.MaxHealth, target.Combatant.Health.MaxHealth);
                    AuthoritativeActionResult result = WaveIRuntimeKit.HitWolf(runtime, killer, ++sequence);
                    Assert.True(
                        result.Payload is CombatResolutionResult { TargetDied: true },
                        $"kill {kills} by {killer}: {result.Rejection?.Reason ?? result.Payload?.ToString() ?? "no payload"}");
                    kills++;
                    lastKillTimestamp = Stopwatch.GetTimestamp();
                }

                run.Advance(1);
                if (runtime.HasPendingInventoryPushes)
                {
                    runtime.DrainInventoryPushesInto(drained);
                    pushes += drained.Count;
                    drained.Clear();
                }

                double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                tickMilliseconds.Add(elapsed);
                if (killTick)
                {
                    killTickMilliseconds.Add(elapsed);
                }

                peakPending = Math.Max(peakPending, runtime.PersistenceWrites!.PendingCount);
            }

            while (runtime.PersistenceWrites!.PendingCount > 0)
            {
                Assert.True(Stopwatch.GetElapsedTime(lastKillTimestamp).TotalSeconds < 30, "the queue never drained");
                Thread.Yield();
            }

            drainMilliseconds = Stopwatch.GetElapsedTime(lastKillTimestamp).TotalMilliseconds;
            failedWrites = runtime.PersistenceWrites.FailedWrites;
        }

        itemsAfter = ItemCount(db);
        double p99 = Percentile(tickMilliseconds, 0.99);
        double killP99 = Percentile(killTickMilliseconds, 0.99);
        output.WriteLine(
            $"{Kills} kills, {pushes} pushes, {itemsAfter - itemsBefore} items, peak pending {peakPending}, " +
            $"drained {drainMilliseconds:0.0} ms after the last kill, {failedWrites} failed");
        output.WriteLine(
            $"tick p50 {Percentile(tickMilliseconds, 0.5):0.00} ms, p99 {p99:0.00} ms, max {tickMilliseconds.Max():0.00} ms; " +
            $"kill tick p50 {Percentile(killTickMilliseconds, 0.5):0.00} ms, p99 {killP99:0.00} ms, max {killTickMilliseconds.Max():0.00} ms");

        Assert.Equal(0, failedWrites);
        Assert.Equal(Kills, pushes);
        Assert.Equal(Kills, itemsAfter - itemsBefore);
        Assert.True(drainMilliseconds <= 100d, $"the queue took {drainMilliseconds:0.0} ms to drain after the last kill");
#if !DEBUG
        Assert.True(p99 <= TickMilliseconds, $"tick p99 {p99:0.00} ms");
#endif
    }

    private static long ItemCount(MigrationTestDatabase db)
    {
        SqlitePools.Release(db.DatabasePath);
        return Convert.ToInt64(
            db.Scalar("SELECT COUNT(*) FROM item_instances WHERE world_id = 1 AND owner_player_name IS NOT NULL;"),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static double Percentile(List<double> values, double percentile)
    {
        double[] sorted = [.. values.Order()];
        return sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(sorted.Length * percentile) - 1)];
    }
}
