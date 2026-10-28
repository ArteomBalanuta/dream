using System.Net;
using System.Reflection;
using Xunit;
using Xunit.Abstractions;
using Dream.Client.Networking;
using Dream.Multiplayer.Diagnostics;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Multiplayer.Transport;
using Dream.Server;
using Dream.Simulation.Runtime;

namespace Dream.Tests.Integration;

// Wave 11 tester, SCALE-7 under real churn: the real RemoteClientSession connects to a
// DedicatedServerService over loopback, joins, and disconnects, again and again.
// - the registry (active sessions + an LRU of departed ones) stays bounded;
// - the tick does not slow down as sessions come and go;
// - a session that reconnects with the same session id reclaims its registry entry and actor id
//   (the desktop client picks a fresh guid per connect, so only an explicit id exercises this);
// - WorldSessionRuntime._sessionInteractionModes follows the registry (fix loop 1: it used to keep
//   an entry per session id ever seen).
// To reach the LRU's eviction in a few hundred cycles, the churn tests swap the runtime's registry
// for one with a small departed capacity before any client connects (the runtime reads the
// registry only through its _sessions field, and the swap carries over its SessionForgotten hook).
// ARCH-1c: only the churn test that measures the tick (Wave11SessionChurnTickLoopbackTests below)
// runs alone; these assert on the registry alone and run in the parallel pool, one collection each
// (TestScheduling.SplitClasses).
public class Wave11SessionChurnLoopbackTests
{
    internal static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    private readonly ITestOutputHelper _output;

    public Wave11SessionChurnLoopbackTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // The same session id comes back: while the registry remembers it, the join goes through its
    // kept entry (same ConnectedPlayer, same actor id when nobody took the id meanwhile). Another
    // session holding the released id meanwhile gives the returning one a fresh id on the same entry.
    [Fact]
    public async Task ReconnectWithTheSameSessionId_ReclaimsItsEntryAndActorId()
    {
        await using LoopbackServer server = await LoopbackServer.StartAsync(PlayerSessionRegistry.DefaultMaxDepartedSessions);
        PlayerSessionRegistry registry = server.Runtime.Sessions;
        await using RemoteClientSession primary = await server.JoinAsync("w11-primary");
        await using RemoteClientSession anchor = await server.JoinAsync("w11-anchor");

        const string Returning = "w11-returning";
        RemoteClientSession first = await server.JoinAsync(Returning);
        ConnectedPlayer entry = server.Player(Returning);
        int actorId = entry.ActorId;
        Assert.True(entry.IsSpawned);
        await first.DisposeAsync();
        await server.WaitForDepartureAsync(Returning);

        // Churn between the visits: fresh sessions take and release ids while Returning is away.
        for (int i = 0; i < 20; i++)
        {
            string churn = Guid.NewGuid().ToString("N");
            RemoteClientSession visitor = await server.JoinAsync(churn);
            await visitor.DisposeAsync();
            await server.WaitForDepartureAsync(churn);
        }

        RemoteClientSession second = await server.JoinAsync(Returning);
        ConnectedPlayer back = server.Player(Returning);
        _output.WriteLine($"first visit actor {actorId}, second visit actor {back.ActorId}");
        Assert.Same(entry, back);
        Assert.Equal(actorId, back.ActorId);
        Assert.True(back.IsSpawned);
        await second.DisposeAsync();
        await server.WaitForDepartureAsync(Returning);

        // Now a newcomer takes the released id before Returning comes back.
        RemoteClientSession squatter = await server.JoinAsync("w11-squatter");
        Assert.Equal(actorId, server.Player("w11-squatter").ActorId);
        await using (squatter)
        {
            RemoteClientSession third = await server.JoinAsync(Returning);
            await using (third)
            {
                ConnectedPlayer reseated = server.Player(Returning);
                _output.WriteLine($"third visit (id taken) actor {reseated.ActorId}");
                Assert.NotEqual(actorId, reseated.ActorId);
                Assert.Equal(reseated.ActorId, server.Runtime.Sessions.Players.Single(player => player.SessionId.Value == Returning).ActorId);
                // Every remote session holds its own actor id (the primary shares the local player's).
                List<ConnectedPlayer> remotes = registry.Players.Where(player => !player.IsLocal).ToList();
                Assert.Equal(remotes.Count, remotes.Select(player => player.ActorId).Distinct().Count());
            }
        }
    }

    // Past MaxDepartedSessions departures the returning session is forgotten and joins as new.
    [Fact]
    public async Task ReconnectAfterEviction_JoinsAsANewSession()
    {
        const int MaxDeparted = 4;
        await using LoopbackServer server = await LoopbackServer.StartAsync(MaxDeparted);
        await using RemoteClientSession primary = await server.JoinAsync("w11-primary");

        const string Returning = "w11-evicted";
        RemoteClientSession first = await server.JoinAsync(Returning);
        ConnectedPlayer entry = server.Player(Returning);
        await first.DisposeAsync();
        await server.WaitForDepartureAsync(Returning);

        for (int i = 0; i < MaxDeparted; i++)
        {
            string churn = Guid.NewGuid().ToString("N");
            RemoteClientSession visitor = await server.JoinAsync(churn);
            await visitor.DisposeAsync();
            await server.WaitForDepartureAsync(churn);
        }

        // The registry trims right after the last departure's IsSpawned flips, on the server thread.
        await WaitUntilAsync(() => !server.Runtime.Sessions.TryGet(new PlayerSessionId(Returning), out _), "the oldest departure to be evicted");
        RemoteClientSession second = await server.JoinAsync(Returning);
        await using (second)
        {
            ConnectedPlayer back = server.Player(Returning);
            Assert.NotSame(entry, back);
            Assert.True(back.IsSpawned);
        }
    }

    // Regression (wave 11 fix loop 1): AttachRemoteSession / RegisterSecondarySession add an entry to
    // WorldSessionRuntime._sessionInteractionModes for every session id, and nothing removed it, so
    // with a fresh session id per connect the dictionary grew by one entry per connect for the life
    // of the server while the registry stayed bounded (42 entries for 10 remembered sessions after
    // 40 cycles). The registry's eviction now drops the mode too.
    [Fact]
    public async Task InteractionModes_StayBoundedWithTheRegistry_UnderChurn()
    {
        const int MaxDeparted = 8;
        const int Cycles = 40;
        await using LoopbackServer server = await LoopbackServer.StartAsync(MaxDeparted);
        await using RemoteClientSession primary = await server.JoinAsync("w11-primary");
        for (int i = 0; i < Cycles; i++)
        {
            string churn = Guid.NewGuid().ToString("N");
            RemoteClientSession visitor = await server.JoinAsync(churn);
            await visitor.DisposeAsync();
            await server.WaitForDepartureAsync(churn);
        }

        int modes = InteractionModeCount(server.Runtime);
        int registered = server.Runtime.Sessions.Count;
        _output.WriteLine($"interaction modes {modes}, registry count {registered}");
        Assert.True(modes <= registered, $"{modes} interaction-mode entries for {registered} remembered sessions after {Cycles} churn cycles");
        Assert.Equal(server.Runtime.SessionInteractionModeCount, modes);
        Assert.Equal(MaxDeparted, server.Runtime.Sessions.DepartedCount);
    }

    internal static int InteractionModeCount(WorldSessionRuntime runtime)
    {
        return ((System.Collections.ICollection)PrivateField(runtime, "_sessionInteractionModes")).Count;
    }

    internal static int ReleasedActorIdCount(WorldSessionRuntime runtime)
    {
        return ((SortedSet<int>)PrivateField(runtime, "_releasedRemoteActorIds")).Count;
    }

    internal static int NextRemoteActorId(WorldSessionRuntime runtime)
    {
        return (int)PrivateField(runtime, "_nextRemoteActorId");
    }

    internal static int RateLimiterKeyCount()
    {
        FieldInfo field = typeof(NetworkDiagnosticsLog).GetField("RateLimiter", BindingFlags.NonPublic | BindingFlags.Static)!;
        return ((NetworkDiagnosticsRateLimiter)field.GetValue(null)!).Count;
    }

    private static object PrivateField(WorldSessionRuntime runtime, string name)
    {
        FieldInfo field = typeof(WorldSessionRuntime).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingFieldException(nameof(WorldSessionRuntime), name);
        return field.GetValue(runtime)!;
    }

    internal static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        DateTime deadline = DateTime.UtcNow + Deadline;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {what}.");
            await Task.Delay(2);
        }
    }

    internal sealed class LoopbackServer : IAsyncDisposable
    {
        private readonly CancellationTokenSource _shutdown = new();
        private Task _run = Task.CompletedTask;

        private LoopbackServer(DedicatedServerHost host, DedicatedServerService service)
        {
            Host = host;
            Service = service;
        }

        public DedicatedServerHost Host { get; }

        public DedicatedServerService Service { get; }

        public WorldSessionRuntime Runtime => Host.Runtime!;

        public static Task<LoopbackServer> StartAsync(int maxDepartedSessions)
        {
            DedicatedServerHost host = DedicatedServerHost.CreateRuntimeAttached(persistence: null);
            WorldSessionRuntime runtime = host.Runtime!;
            if (maxDepartedSessions != PlayerSessionRegistry.DefaultMaxDepartedSessions)
            {
                // Before any client: the fresh registry holds only the local player, as the
                // runtime's constructor registers it.
                PlayerSessionRegistry original = runtime.Sessions;
                Assert.True(original.TryGet(runtime.LocalPlayerSessionId, out ConnectedPlayer? local));
                Assert.Equal(1, original.Count);
                PlayerSessionRegistry small = new(maxDepartedSessions)
                {
                    SessionForgotten = original.SessionForgotten
                };
                Assert.NotNull(small.SessionForgotten);
                small.Register(local!.SessionId, local.ActorId, isLocal: true, displayName: local.DisplayName);
                typeof(WorldSessionRuntime)
                    .GetField("_sessions", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .SetValue(runtime, small);
                Assert.Same(small, runtime.Sessions);
            }

            DedicatedServerService service = new(
                host,
                new TcpServerTransport(IPAddress.Loopback, 0),
                new TransportJsonCodec(),
                FrameLimitOptions.Default.CreateServerFramer());
            LoopbackServer server = new(host, service);
            service.Start();
            server._run = Task.Run(() => service.RunAsync(server._shutdown.Token));
            return Task.FromResult(server);
        }

        public async Task<RemoteClientSession> JoinAsync(string sessionId)
        {
            RemoteClientSession session = await RemoteClientSession.ConnectAsync(
                new RemoteClientOptions("127.0.0.1", Service.Port, sessionId, sessionId, "w11-test")).AsTask().WaitAsync(Deadline);
            await WaitUntilAsync(() => session.IsJoined || !session.IsConnected, $"{sessionId} to join");
            Assert.True(session.IsJoined, session.DisconnectReason);
            await WaitUntilAsync(
                () => Runtime.Sessions.TryGet(new PlayerSessionId(sessionId), out ConnectedPlayer? player) && player!.IsSpawned,
                $"{sessionId} to be seated");
            return session;
        }

        public Task WaitForDepartureAsync(string sessionId)
        {
            PlayerSessionId id = new(sessionId);
            return WaitUntilAsync(
                () => !Runtime.Sessions.TryGet(id, out ConnectedPlayer? player) || !player!.IsSpawned,
                $"{sessionId} to leave");
        }

        public ConnectedPlayer Player(string sessionId)
        {
            Assert.True(Runtime.Sessions.TryGet(new PlayerSessionId(sessionId), out ConnectedPlayer? player), $"{sessionId} is not registered");
            return player!;
        }

        public async ValueTask DisposeAsync()
        {
            _shutdown.Cancel();
            try
            {
                await _run.WaitAsync(Deadline);
            }
            catch (OperationCanceledException)
            {
            }

            await Service.DisposeAsync();
            Host.Dispose();
            _shutdown.Dispose();
        }
    }
}

// The churn test of Wave11SessionChurnLoopbackTests that measures the tick: its last batch's
// median tick cost is compared with its first, which only means something when nothing else runs,
// so it runs alone (TimingSensitive).
//
// ARCH-1c: 100 cycles instead of 200. The LRU still fills after 32 departures and then evicts on
// every one of the other 68 (the departed count is asserted to sit at its cap and the registry
// count never to pass it), and the tick is still compared across two batches of 50 cycles.
[Collection(TimingSensitiveCollection.Name)]
public class Wave11SessionChurnTickLoopbackTests
{
    private readonly ITestOutputHelper _output;

    public Wave11SessionChurnTickLoopbackTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task HundredConnectDisconnectCycles_KeepTheRegistryBounded_AndTheTickFlat()
    {
        const int Cycles = 100;
        const int BatchSize = 50;
        const int MaxDeparted = 32;
        await using Wave11SessionChurnLoopbackTests.LoopbackServer server = await Wave11SessionChurnLoopbackTests.LoopbackServer.StartAsync(MaxDeparted);
        PlayerSessionRegistry registry = server.Runtime.Sessions;

        // A primary stays connected, so every churning session takes a secondary seat and an id
        // from the released-id pool.
        await using RemoteClientSession primary = await server.JoinAsync("w11-primary");
        int activeBaseline = registry.ActiveCount;
        Assert.Equal(2, activeBaseline);

        int maxCount = 0;
        int maxActive = 0;
        List<(double TickP50, double TickP99, double AdvanceP50, double AdvanceP99)> batches = [];
        server.Service.CaptureMetrics(resetWindow: true);
        for (int cycle = 0; cycle < Cycles; cycle++)
        {
            string sessionId = Guid.NewGuid().ToString("N");
            RemoteClientSession session = await server.JoinAsync(sessionId);
            maxActive = Math.Max(maxActive, registry.ActiveCount);
            await session.DisposeAsync();
            await server.WaitForDepartureAsync(sessionId);
            await WaitUntilAsync(() => registry.ActiveCount == activeBaseline, "the active count to return to its baseline");
            maxCount = Math.Max(maxCount, registry.Count);

            if ((cycle + 1) % BatchSize == 0)
            {
                ServerMetricsSnapshot metrics = server.Service.CaptureMetrics(resetWindow: true);
                batches.Add((
                    metrics.Tick.TickWindow.P50Milliseconds,
                    metrics.Tick.TickWindow.P99Milliseconds,
                    metrics.Tick.AdvancePerTick.P50Milliseconds,
                    metrics.Tick.AdvancePerTick.P99Milliseconds));
                _output.WriteLine(
                    $"cycles {cycle + 1 - BatchSize}..{cycle}: registry count {registry.Count} (active {registry.ActiveCount}, departed {registry.DepartedCount}), " +
                    $"interaction modes {InteractionModeCount(server.Runtime)}, released ids {ReleasedActorIdCount(server.Runtime)}, next id {NextRemoteActorId(server.Runtime)}, " +
                    $"tick p50 {batches[^1].TickP50:0.000} ms p99 {batches[^1].TickP99:0.000} ms, advance/tick p50 {batches[^1].AdvanceP50:0.000} ms p99 {batches[^1].AdvanceP99:0.000} ms");
            }
        }

        _output.WriteLine($"max registry count {maxCount}, max active {maxActive}, diagnostics rate-limiter keys {RateLimiterKeyCount()}");

        // Bounded: the active sessions plus at most MaxDeparted departed ones, whatever the churn.
        Assert.Equal(MaxDeparted, registry.DepartedCount);
        Assert.True(maxCount <= MaxDeparted + activeBaseline + 1, $"registry count reached {maxCount}");
        Assert.True(maxActive <= activeBaseline + 1, $"active count reached {maxActive}");
        // Actor ids are recycled, not minted per session.
        Assert.True(NextRemoteActorId(server.Runtime) <= 4, $"next remote actor id {NextRemoteActorId(server.Runtime)}");
        Assert.True(ReleasedActorIdCount(server.Runtime) <= 2, $"{ReleasedActorIdCount(server.Runtime)} released ids pooled");
        Assert.True(RateLimiterKeyCount() <= NetworkDiagnosticsRateLimiter.MaxEntries);

        // Flat tick: the last batch's median per-tick cost is no worse than twice the first's plus
        // half a millisecond of noise (the joins' world transfers run on the tick in every batch).
        double first = batches[0].AdvanceP50;
        double last = batches[^1].AdvanceP50;
        Assert.True(last <= (first * 2d) + 0.5d, $"advance per tick p50 grew from {first:0.000} ms to {last:0.000} ms");
        Assert.True(primary.IsConnected, primary.DisconnectReason);
    }

    private static Task WaitUntilAsync(Func<bool> condition, string what) => Wave11SessionChurnLoopbackTests.WaitUntilAsync(condition, what);

    private static int InteractionModeCount(WorldSessionRuntime runtime) => Wave11SessionChurnLoopbackTests.InteractionModeCount(runtime);

    private static int ReleasedActorIdCount(WorldSessionRuntime runtime) => Wave11SessionChurnLoopbackTests.ReleasedActorIdCount(runtime);

    private static int NextRemoteActorId(WorldSessionRuntime runtime) => Wave11SessionChurnLoopbackTests.NextRemoteActorId(runtime);

    private static int RateLimiterKeyCount() => Wave11SessionChurnLoopbackTests.RateLimiterKeyCount();
}
