using System.Net.Sockets;
using Xunit;
using Dream.Client.Networking;
using Dream.Client.Startup;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Transport;
using Dream.Server;

namespace Dream.Tests.Client.Startup;

// M8. The automatic reconnect on the connection screen: it retries a lost connection, and nothing else.
// The kick and ban side (a rejection is not a lost connection) is proved over real TCP in
// M8EndToEndLoopbackTests; here the coordinator's loop runs against a scripted connector, with the waits
// cut to milliseconds.
public sealed class ServerOpsReconnectTests : IDisposable
{
    private readonly IReadOnlyList<TimeSpan> _delays = NetworkSessionCoordinator.ReconnectDelays;

    public ServerOpsReconnectTests()
    {
        NetworkSessionCoordinator.ReconnectDelays = [.. Enumerable.Repeat(TimeSpan.FromMilliseconds(5), NetworkSessionCoordinator.MaxReconnectAttempts)];
    }

    public void Dispose()
    {
        NetworkSessionCoordinator.ReconnectDelays = _delays;
    }

    private static readonly ClientConnectionOptions Endpoint = new("203.0.113.9", "5057", "Wanderer");

    [Theory]
    [InlineData("Connection lost: no data from the server for 8 s", true)]
    [InlineData("Connection lost: the server closed the connection.", true)]
    [InlineData(RemoteClientSession.ServerSilenceDisconnectReason, true)]
    [InlineData("This character was opened somewhere else.", false)]
    [InlineData("You were kicked from the server. Reason: bye", false)]
    [InlineData("You are banned from this server.", false)]
    [InlineData("Server is full.", false)]
    [InlineData("Server uses protocol v1; this client uses v2.", false)]
    [InlineData("World transfer failed: x", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyALostConnection_IsRetryable(string? reason, bool expected)
    {
        Assert.Equal(expected, RemoteClientSession.IsRetryableEnd(reason));
    }

    [Fact]
    public async Task ALostConnection_IsRetried_UntilTheServerAnswers()
    {
        ScriptedConnector connector = new(failures: 2);
        NetworkSessionCoordinator coordinator = new(new FakeLocalServerHandle(), connector);

        coordinator.BeginReconnect(Endpoint);
        Assert.True(coordinator.IsReconnecting);
        await coordinator.WaitForPendingConnectionAsync();

        Assert.Equal(3, connector.Calls);
        Assert.Equal(ConnectionFlowState.Connected, coordinator.State);
        Assert.False(coordinator.IsReconnecting);
        Assert.True(coordinator.TryTakeConnectedSession(out RemoteClientSession? session));
        await session!.DisposeAsync();
    }

    [Fact]
    public async Task FiveFailures_GiveUp_AndSayWhy()
    {
        ScriptedConnector connector = new(failures: 99);
        NetworkSessionCoordinator coordinator = new(new FakeLocalServerHandle(), connector);

        coordinator.BeginReconnect(Endpoint);
        await coordinator.WaitForPendingConnectionAsync();

        Assert.Equal(NetworkSessionCoordinator.MaxReconnectAttempts, connector.Calls);
        Assert.Equal(ConnectionFlowState.Failed, coordinator.State);
        Assert.StartsWith("Could not reconnect:", coordinator.StatusMessage, StringComparison.Ordinal);
        Assert.False(coordinator.IsReconnecting);
        Assert.False(coordinator.TryTakeConnectedSession(out _));
    }

    [Fact]
    public async Task TheStatusCountsTheAttempts()
    {
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ScriptedConnector connector = new(failures: 99) { Gate = gate.Task };
        NetworkSessionCoordinator coordinator = new(new FakeLocalServerHandle(), connector);

        coordinator.BeginReconnect(Endpoint);
        await connector.FirstCall.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("Reconnecting (1/5)...", coordinator.StatusMessage);
        Assert.True(coordinator.IsBusy);
        gate.SetResult();
        await coordinator.WaitForPendingConnectionAsync();
    }

    [Fact]
    public async Task AProtocolMismatch_StopsAtOnce()
    {
        ScriptedConnector connector = new(failures: 99) { Fault = () => ProtocolVersionMismatchException.FromServer(17) };
        NetworkSessionCoordinator coordinator = new(new FakeLocalServerHandle(), connector);

        coordinator.BeginReconnect(Endpoint);
        await coordinator.WaitForPendingConnectionAsync();

        Assert.Equal(1, connector.Calls);
        Assert.Equal(ConnectionFlowState.Failed, coordinator.State);
        Assert.False(coordinator.IsReconnecting);
    }

    [Fact]
    public async Task Connect_StopsTheRetries()
    {
        ScriptedConnector connector = new(failures: 99);
        NetworkSessionCoordinator.ReconnectDelays = [.. Enumerable.Repeat(TimeSpan.FromSeconds(30), 5)];
        NetworkSessionCoordinator coordinator = new(new FakeLocalServerHandle(), connector);
        coordinator.BeginReconnect(Endpoint);
        Assert.True(coordinator.IsReconnecting);

        coordinator.BeginConnect(Endpoint);
        await coordinator.WaitForPendingConnectionAsync();

        // The player's own connect ran (one call); the long wait of the reconnect never produced another.
        Assert.False(coordinator.IsReconnecting);
        Assert.Equal(1, connector.Calls);
    }

    [Fact]
    public async Task UseLocalServer_StopsTheRetries()
    {
        NetworkSessionCoordinator.ReconnectDelays = [.. Enumerable.Repeat(TimeSpan.FromSeconds(30), 5)];
        ScriptedConnector connector = new(failures: 99);
        NetworkSessionCoordinator coordinator = new(new FakeLocalServerHandle(), connector);
        coordinator.BeginReconnect(Endpoint);

        coordinator.TryUseLocalServer("Wanderer", out _);
        await Task.Delay(50);

        Assert.False(coordinator.IsReconnecting);
        Assert.Equal(0, connector.Calls);
    }

    [Fact]
    public async Task Escape_StopsTheRetries_AndSaysSo()
    {
        NetworkSessionCoordinator.ReconnectDelays = [.. Enumerable.Repeat(TimeSpan.FromSeconds(30), 5)];
        ScriptedConnector connector = new(failures: 99);
        NetworkSessionCoordinator coordinator = new(new FakeLocalServerHandle(), connector);
        coordinator.BeginReconnect(Endpoint);

        coordinator.StopReconnect();
        await coordinator.WaitForPendingConnectionAsync();

        Assert.False(coordinator.IsReconnecting);
        Assert.Equal("Reconnect cancelled.", coordinator.StatusMessage);
        Assert.Equal(0, connector.Calls);
        coordinator.StopReconnect();
    }

    [Fact]
    public void ASecondReconnect_WhileOneRuns_IsIgnored()
    {
        NetworkSessionCoordinator.ReconnectDelays = [.. Enumerable.Repeat(TimeSpan.FromSeconds(30), 5)];
        NetworkSessionCoordinator coordinator = new(new FakeLocalServerHandle(), new ScriptedConnector(failures: 99));

        coordinator.BeginReconnect(Endpoint);
        Task first = coordinator.WaitForPendingConnectionAsync();
        coordinator.BeginReconnect(Endpoint);

        Assert.Same(first, coordinator.WaitForPendingConnectionAsync());
        coordinator.StopReconnect();
    }

    [Fact]
    public void ARelaunchConnector_IsNeverRetried()
    {
        // The session ended on the game's own integrated server: connecting there starts a new server.
        NetworkSessionCoordinator coordinator = new(new FakeLocalServerHandle(), new LocalServerRelaunchConnector());

        coordinator.BeginReconnect(Endpoint);

        Assert.False(coordinator.CanAutoReconnect);
        Assert.False(coordinator.IsReconnecting);
    }

    [Fact]
    public async Task ASessionThatLastedLongEnough_IsRetried_AShortOneIsNot_AndTheStampIsJudgedOnce()
    {
        NetworkSessionCoordinator coordinator = new(new FakeLocalServerHandle(), new ScriptedConnector(failures: 0));
        long frequency = TimeProvider.System.TimestampFrequency;
        long now = TimeProvider.System.GetTimestamp();

        // Lasted 31 s: a lost connection is retried.
        NetworkSessionCoordinator.MarkSessionStarted(now - (31 * frequency));
        Assert.True(coordinator.TakeSessionLastedLongEnoughToReconnect());
        // The stamp is consumed: the next ended session has to be taken again to count.
        Assert.False(coordinator.TakeSessionLastedLongEnoughToReconnect());

        // Dropped 5 s after the game took it: the server keeps dropping the client, so no loop.
        NetworkSessionCoordinator.MarkSessionStarted(now - (5 * frequency));
        Assert.False(coordinator.TakeSessionLastedLongEnoughToReconnect());

        // No session was ever taken (a failed join): nothing to retry on.
        Assert.False(coordinator.TakeSessionLastedLongEnoughToReconnect());

        // Taking a connected session stamps it, and the stamp is young.
        coordinator.BeginReconnect(Endpoint);
        await coordinator.WaitForPendingConnectionAsync();
        Assert.True(coordinator.TryTakeConnectedSession(out RemoteClientSession? session));
        await session!.DisposeAsync();
        Assert.False(coordinator.TakeSessionLastedLongEnoughToReconnect());
    }

    [Fact]
    public void TheScene_RetriesOnlyAfterALongSession()
    {
        // The scene needs a GL context to build, so its gate is read from source: the retry is behind the
        // coordinator's session-length check, and the comment no longer claims a one-scene limit alone.
        string source = File.ReadAllText(Path.Combine(
            Dream.Tests.Integration.RepoRoot.Find(), "dream", "Client", "Startup", "StartupConnectionScene.cs"));

        Assert.Contains("TakeSessionLastedLongEnoughToReconnect()", source, StringComparison.Ordinal);
        Assert.Contains("lastedLongEnough && RemoteClientSession.IsRetryableEnd(sessionEndReason)", source, StringComparison.Ordinal);
    }

    // Named like the production connector (Dream.Application.LocalServerRelaunchConnector) on purpose: the
    // coordinator recognises it by name because the Client layer cannot reference Application.
    private sealed class LocalServerRelaunchConnector : IRemoteSessionConnector
    {
        public ValueTask<RemoteClientSession> ConnectAsync(RemoteClientOptions options, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("never reached");
    }

    private sealed class FakeLocalServerHandle : ILocalServerHandle
    {
        public bool IsReady => true;

        public int BoundPort => 6123;

        public string? ErrorMessage => null;

        public DedicatedServerPerformanceSnapshot LatestPerformance => DedicatedServerPerformanceSnapshot.Empty;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ScriptedConnector(int failures) : IRemoteSessionConnector
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Func<Exception>? Fault { get; init; }

        public Task? Gate { get; init; }

        public TaskCompletionSource FirstCall { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<RemoteClientSession> ConnectAsync(RemoteClientOptions options, CancellationToken cancellationToken = default)
        {
            int call = Interlocked.Increment(ref _calls);
            FirstCall.TrySetResult();
            if (Gate != null)
            {
                await Gate;
            }

            if (call <= failures)
            {
                throw Fault?.Invoke() ?? new SocketException((int)SocketError.ConnectionRefused);
            }

            return new RemoteClientSession(new IdleTransportConnection(), new TransportJsonCodec(), new LengthPrefixedMessageFramer(), options);
        }
    }

    private sealed class IdleTransportConnection : ITransportConnection
    {
        public string ConnectionId => "reconnect-test";

        public ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromResult(0);

        public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
