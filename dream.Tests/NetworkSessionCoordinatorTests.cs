using Xunit;

public class NetworkSessionCoordinatorTests
{
    [Fact]
    public void UseLocalServerDefaults_Returns_Loopback_And_Bound_Port()
    {
        FakeLocalServerHandle localServer = new(isReady: true, boundPort: 6123);
        NetworkSessionCoordinator coordinator = new(localServer, new FakeRemoteSessionConnector());

        ClientConnectionOptions options = coordinator.UseLocalServerDefaults("player-a");

        Assert.Equal("127.0.0.1", options.Host);
        Assert.Equal("6123", options.PortText);
        Assert.Equal("player-a", options.PlayerName);
    }

    [Fact]
    public async Task BeginConnectAsync_Sets_Failed_State_When_Options_Are_Invalid()
    {
        FakeLocalServerHandle localServer = new(isReady: true, boundPort: 6123);
        NetworkSessionCoordinator coordinator = new(localServer, new FakeRemoteSessionConnector());

        coordinator.BeginConnect(new ClientConnectionOptions("", "6123", "player-a"));
        await coordinator.WaitForPendingConnectionAsync();

        Assert.Equal(ConnectionFlowState.Failed, coordinator.State);
        Assert.Equal("Host is required.", coordinator.StatusMessage);
    }

    [Fact]
    public async Task BeginConnectAsync_Transitions_To_Connected_And_Hands_Off_Session()
    {
        FakeLocalServerHandle localServer = new(isReady: true, boundPort: 6123);
        FakeRemoteSessionConnector connector = new();
        NetworkSessionCoordinator coordinator = new(localServer, connector);

        coordinator.BeginConnect(new ClientConnectionOptions("127.0.0.1", "6123", "player-a"));
        await coordinator.WaitForPendingConnectionAsync();

        Assert.Equal(ConnectionFlowState.Connected, coordinator.State);
        Assert.True(coordinator.TryTakeConnectedSession(out RemoteClientSession? session));
        Assert.NotNull(session);
        await session.DisposeAsync();
    }

    private sealed class FakeLocalServerHandle : ILocalServerHandle
    {
        public FakeLocalServerHandle(bool isReady, int boundPort, string? errorMessage = null)
        {
            IsReady = isReady;
            BoundPort = boundPort;
            ErrorMessage = errorMessage;
        }

        public bool IsReady { get; }

        public int BoundPort { get; }

        public string? ErrorMessage { get; }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeRemoteSessionConnector : IRemoteSessionConnector
    {
        public ValueTask<RemoteClientSession> ConnectAsync(
            RemoteClientOptions options,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(new RemoteClientSession(
                new FakeTransportConnection(),
                new TransportJsonCodec(),
                new LengthPrefixedMessageFramer(),
                options));
        }
    }

    private sealed class FakeTransportConnection : ITransportConnection
    {
        public string ConnectionId => "coordinator-test";

        public ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(0);
        }

        public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
