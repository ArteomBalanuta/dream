using System.Diagnostics;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Combat;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;

namespace Dream.Tests.Load;

// SCALE-7 measurement (DREAM_LOAD_TEST=1, Release): eight connected players tick while 10k
// short-lived sessions (a fresh session id each, as client relaunches produce) join and leave.
// Reports the registry's walked size, the median tick time and the bytes a tick allocates before
// and after the churn. Before SCALE-7 the registry kept every session and the tick walked all of
// them; now the walk covers the connected players only.
[Collection(TimingSensitiveCollection.Name)]
public sealed class SessionChurnMeasurementTests
{
    private const int ConnectedPlayers = 8;
    private const int ChurnCycles = 10_000;
    private const int MeasuredTicks = 512;

    private readonly ITestOutputHelper _output;

    public SessionChurnMeasurementTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [LoadFact]
    public void TenThousandJoinLeaveCycles_KeepTheTickFlat()
    {
        using WorldSessionRuntime runtime = new(32, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        using RuntimeAuthoritativeSessionHost host = new(runtime);
        PlayerSessionId[] connected = new PlayerSessionId[ConnectedPlayers];
        for (int i = 0; i < ConnectedPlayers; i++)
        {
            connected[i] = new PlayerSessionId($"player-{i}");
            host.JoinSession(connected[i].Value, $"Player {i}");
        }

        ulong tick = 0;
        ulong sequence = 0;
        (double beforeMs, long beforeBytes) = MeasureTicks(host, connected, ref tick, ref sequence);
        int walkedBefore = runtime.Sessions.Players.Count;

        Stopwatch churn = Stopwatch.StartNew();
        for (int i = 0; i < ChurnCycles; i++)
        {
            string session = $"client-{i:D5}";
            host.JoinSession(session, $"Visitor {i}");
            host.ReleaseSession(session);
        }

        churn.Stop();
        (double afterMs, long afterBytes) = MeasureTicks(host, connected, ref tick, ref sequence);
        int walkedAfter = runtime.Sessions.Players.Count;

        _output.WriteLine(
            $"churn {ChurnCycles} cycles in {churn.Elapsed.TotalSeconds:0.00} s; walked sessions {walkedBefore} -> {walkedAfter}; " +
            $"median tick {beforeMs:0.000} ms -> {afterMs:0.000} ms; median tick bytes {beforeBytes} -> {afterBytes}");

        Assert.Equal(walkedBefore, walkedAfter);
        Assert.True(afterBytes <= beforeBytes + 64, $"tick bytes {beforeBytes} -> {afterBytes}");
        Assert.True(afterMs <= (beforeMs * 2) + 0.1, $"median tick {beforeMs:0.000} ms -> {afterMs:0.000} ms");
    }

    private static (double MedianMs, long MedianBytes) MeasureTicks(
        RuntimeAuthoritativeSessionHost host,
        PlayerSessionId[] connected,
        ref ulong tick,
        ref ulong sequence)
    {
        double[] ms = new double[MeasuredTicks];
        long[] bytes = new long[MeasuredTicks];
        PlayerCommandFrame[] frames = new PlayerCommandFrame[connected.Length];
        for (int warmup = -64; warmup < MeasuredTicks; warmup++)
        {
            sequence++;
            for (int i = 0; i < connected.Length; i++)
            {
                frames[i] = new PlayerCommandFrame(
                    connected[i],
                    sequence,
                    new PlayerModeSwitchIntent(PlayerInteractionMode.Building),
                    new PlayerMoveIntent(0.1f, 0f, 0f),
                    new PlayerLookIntent(0f, 0f),
                    PrimaryUsePressed: false);
            }

            long bytesBefore = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            host.AdvanceTick(++tick, SimulationClock.FixedDeltaSeconds, frames, PlayerSimulationTuning.AuthoritativeDefault);
            _ = host.ExportSnapshot();
            double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            long allocated = GC.GetAllocatedBytesForCurrentThread() - bytesBefore;
            if (warmup >= 0)
            {
                ms[warmup] = elapsed;
                bytes[warmup] = allocated;
            }
        }

        Array.Sort(ms);
        Array.Sort(bytes);
        return (ms[MeasuredTicks / 2], bytes[MeasuredTicks / 2]);
    }
}
