using Xunit;
using Xunit.Abstractions;
using Dream.Multiplayer.Diagnostics;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Tests.Utilities;

namespace Dream.Tests.Simulation.Runtime;

// Wave I2: the respawn scan and the loot hook add nothing to a tick. A dead session waiting for its
// respawn, on the primary and on a secondary seat, costs 0 B per tick, and so does a tick with loot
// enabled and no kill (only a kill tick allocates).
public sealed class RespawnAllocationTests(ITestOutputHelper output)
{
    private static readonly PlayerSessionId A = new("alloc-a");
    private static readonly PlayerSessionId B = new("alloc-b");

    private static WorldSessionRuntime CreateSettled(out ulong tick)
    {
        WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        runtime.LootContent = WaveIRuntimeKit.AlwaysDropCatalog(drops: 2);
        foreach (PlayerSessionId session in new[] { A, B })
        {
            ConnectedPlayer player = runtime.AttachRemoteSession(session, session.Value);
            Assert.True(runtime.InventoryService.Ensure(player.CharacterKey.Value));
        }

        tick = 0;
        for (int i = 0; i < 240; i++)
        {
            runtime.AdvanceAuthoritativeTick(
                ++tick,
                SimulationClock.FixedDeltaSeconds,
                default(PlayerCommandFrame),
                PlayerSimulationTuning.AuthoritativeDefault);
            runtime.ExportServerSnapshotEnvelope();
        }

        return runtime;
    }

    // The seats are killed again whenever they are alive, so the measured ticks keep two dead sessions
    // waiting; a respawn every 320 ticks falls in a few rounds only, and the helper reports the cleanest.
    private static long Measure(WorldSessionRuntime runtime, ref ulong tick, bool keepSeatsDead)
    {
        IAuthoritativePlayerTarget a = WaveIRuntimeKit.Target(runtime, A.Value);
        IAuthoritativePlayerTarget b = WaveIRuntimeKit.Target(runtime, B.Value);
        ulong local = tick;
        long bytes = AllocationAssert.MeasureBytesPerIteration(
            () =>
            {
                if (keepSeatsDead)
                {
                    if (a.IsAlive)
                    {
                        a.Combatant.Health.ApplyDamage(1_000_000);
                    }

                    if (b.IsAlive)
                    {
                        b.Combatant.Health.ApplyDamage(1_000_000);
                    }
                }

                runtime.AdvanceAuthoritativeTick(
                    ++local,
                    SimulationClock.FixedDeltaSeconds,
                    default(PlayerCommandFrame),
                    PlayerSimulationTuning.AuthoritativeDefault);
            },
            iterations: 40);
        tick = local;
        return bytes;
    }

    [Fact]
    public void ATickWithDeadSessionsWaiting_OnBothSeatKinds_AllocatesNothing()
    {
        Assert.False(
            NetworkDiagnosticsLog.IsEnabled,
            "This test needs DREAM_NET_DIAGNOSTICS unset: the diagnostics collision report allocates.");
        using WorldSessionRuntime runtime = CreateSettled(out ulong tick);

        long bytes = Measure(runtime, ref tick, keepSeatsDead: true);

        output.WriteLine($"AdvanceAuthoritativeTick, primary and secondary seat dead and waiting: {bytes} B");
        Assert.True(bytes <= 0, $"Allocated {bytes} B per tick; budget is 0 B.");
    }

    [Fact]
    public void ATickWithLootEnabledAndNoKill_AllocatesNothing()
    {
        Assert.False(
            NetworkDiagnosticsLog.IsEnabled,
            "This test needs DREAM_NET_DIAGNOSTICS unset: the diagnostics collision report allocates.");
        using WorldSessionRuntime runtime = CreateSettled(out ulong tick);

        long bytes = Measure(runtime, ref tick, keepSeatsDead: false);

        output.WriteLine($"AdvanceAuthoritativeTick, loot enabled, no kill: {bytes} B");
        Assert.True(bytes <= 0, $"Allocated {bytes} B per tick; budget is 0 B.");
    }
}
