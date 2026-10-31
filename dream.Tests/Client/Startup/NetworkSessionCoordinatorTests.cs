using Xunit;
using Dream.Client.Identity;
using Dream.Client.Networking;
using Dream.Client.Startup;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Transport;
using Dream.Server;

namespace Dream.Tests.Client.Startup;


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
    public void IsLocalServerReady_Reflects_Local_Server_State()
    {
        NetworkSessionCoordinator readyCoordinator = new(
            new FakeLocalServerHandle(isReady: true, boundPort: 6123),
            new FakeRemoteSessionConnector());
        NetworkSessionCoordinator waitingCoordinator = new(
            new FakeLocalServerHandle(isReady: false, boundPort: 0),
            new FakeRemoteSessionConnector());

        Assert.True(readyCoordinator.IsLocalServerReady);
        Assert.False(waitingCoordinator.IsLocalServerReady);
    }

    [Fact]
    public void ResolveInitialOptions_ReplacesConfiguredLoopbackPortWithCurrentLocalServerPort()
    {
        FakeLocalServerHandle localServer = new(isReady: true, boundPort: 6123);
        NetworkSessionCoordinator coordinator = new(localServer, new FakeRemoteSessionConnector());

        ClientConnectionOptions options = coordinator.ResolveInitialOptions(
            new ClientConnectionOptions("127.0.0.1", "5057", "player-a"));

        Assert.Equal("127.0.0.1", options.Host);
        Assert.Equal("6123", options.PortText);
        Assert.Equal("player-a", options.PlayerName);
    }

    [Fact]
    public void ResolveInitialOptions_KeepsConfiguredRemoteEndpoint()
    {
        FakeLocalServerHandle localServer = new(isReady: true, boundPort: 6123);
        NetworkSessionCoordinator coordinator = new(localServer, new FakeRemoteSessionConnector());

        ClientConnectionOptions options = coordinator.ResolveInitialOptions(
            new ClientConnectionOptions("192.168.1.25", "5057", "player-a"));

        Assert.Equal("192.168.1.25", options.Host);
        Assert.Equal("5057", options.PortText);
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

    [Fact]
    public async Task ReportSessionEnded_ShowsTheReason_AndConnectingStillWorks()
    {
        FakeLocalServerHandle localServer = new(isReady: true, boundPort: 6123);
        NetworkSessionCoordinator coordinator = new(localServer, new FakeRemoteSessionConnector());

        coordinator.ReportSessionEnded("Connection lost: no data from the server for 8 s");

        Assert.Equal(ConnectionFlowState.Failed, coordinator.State);
        Assert.False(coordinator.IsBusy);
        Assert.Equal("Session ended: Connection lost: no data from the server for 8 s", coordinator.StatusMessage);

        coordinator.BeginConnect(new ClientConnectionOptions("127.0.0.1", "6123", "player-a"));
        await coordinator.WaitForPendingConnectionAsync();

        Assert.Equal(ConnectionFlowState.Connected, coordinator.State);
        Assert.True(coordinator.TryTakeConnectedSession(out RemoteClientSession? session));
        await session!.DisposeAsync();
    }

    [Fact]
    public async Task TryUseLocalServer_WhileAConnectIsInFlight_IsRefused_AndDoesNotStartASecondConnect()
    {
        FakeLocalServerHandle localServer = new(isReady: true, boundPort: 6123);
        SlowConnector connector = new();
        NetworkSessionCoordinator coordinator = new(localServer, connector);
        ClientConnectionOptions slowHost = new("slow.example.net", "5057", "player-a");

        coordinator.BeginConnect(slowHost);
        Assert.True(coordinator.IsBusy);

        bool used = coordinator.TryUseLocalServer("player-a", out ClientConnectionOptions? options);

        Assert.False(used);
        Assert.Null(options);
        Assert.Equal(ConnectionFlowState.Connecting, coordinator.State);
        coordinator.BeginConnect(slowHost);
        Assert.Equal(1, connector.Calls);

        connector.Release();
        await coordinator.WaitForPendingConnectionAsync();
        Assert.Equal(ConnectionFlowState.Connected, coordinator.State);
        Assert.True(coordinator.TryTakeConnectedSession(out RemoteClientSession? session));
        await session!.DisposeAsync();
    }

    [Fact]
    public async Task BeginConnect_PassesTheStoredTokenOfTheServerKey_ToTheConnector()
    {
        RecordingIdentityStore store = new();
        CapturingConnector connector = new();
        NetworkSessionCoordinator coordinator = new(
            new FakeLocalServerHandle(isReady: true, boundPort: 6123), connector, identity: store);

        coordinator.BeginConnect(new ClientConnectionOptions("Play.Example.com", "5057", "player-a"));
        await coordinator.WaitForPendingConnectionAsync();

        Assert.Equal(["play.example.com:5057"], store.Keys);
        Assert.Equal(RecordingIdentityStore.TokenFor("play.example.com:5057"), connector.Options!.AccountToken);
        Assert.DoesNotContain(connector.Options.AccountToken!, connector.Options.ToString());
        Assert.DoesNotContain(connector.Options.AccountToken!, coordinator.StatusMessage);
        Assert.True(coordinator.TryTakeConnectedSession(out RemoteClientSession? session));
        await session!.DisposeAsync();
    }

    [Fact]
    public async Task BeginConnect_ToTheIntegratedServer_UsesTheLocalKey_AcrossChangingPorts()
    {
        RecordingIdentityStore store = new();
        foreach (int port in new[] { 6123, 7456 })
        {
            CapturingConnector connector = new();
            NetworkSessionCoordinator coordinator = new(
                new FakeLocalServerHandle(isReady: true, boundPort: port), connector, identity: store);

            coordinator.BeginConnect(new ClientConnectionOptions("127.0.0.1", port.ToString(), "player-a"));
            await coordinator.WaitForPendingConnectionAsync();
            Assert.True(coordinator.TryTakeConnectedSession(out RemoteClientSession? session));
            await session!.DisposeAsync();
        }

        Assert.Equal(["local", "local"], store.Keys);
    }

    [Theory]
    [InlineData("localhost", "50123")]
    [InlineData("127.0.0.1", "5057")]
    [InlineData("127.0.0.2", "6000")]
    [InlineData("::1", "5057")]
    [InlineData("[::1]", "5057")]
    public async Task BeginConnect_ToAnyLoopbackAddressAndPort_UsesTheLocalKey(string host, string port)
    {
        RecordingIdentityStore store = new();
        NetworkSessionCoordinator coordinator = new(
            new FakeLocalServerHandle(isReady: false, boundPort: 0), new CapturingConnector(), identity: store);

        coordinator.BeginConnect(new ClientConnectionOptions(host, port, "player-a"));
        await coordinator.WaitForPendingConnectionAsync();

        Assert.Equal(["local"], store.Keys);
        Assert.True(coordinator.TryTakeConnectedSession(out RemoteClientSession? session));
        await session!.DisposeAsync();
    }

    [Fact]
    public async Task BeginConnect_WithoutAStore_LeavesTheTokenNull()
    {
        CapturingConnector connector = new();
        NetworkSessionCoordinator coordinator = new(new FakeLocalServerHandle(isReady: true, boundPort: 6123), connector);

        coordinator.BeginConnect(new ClientConnectionOptions("play.example.com", "5057", "player-a"));
        await coordinator.WaitForPendingConnectionAsync();

        Assert.Null(connector.Options!.AccountToken);
        Assert.True(coordinator.TryTakeConnectedSession(out RemoteClientSession? session));
        await session!.DisposeAsync();
    }

    [Fact]
    public async Task BeginConnect_WhenTheStoreCannotBeWritten_FailsWithAReadableMessage()
    {
        CapturingConnector connector = new();
        NetworkSessionCoordinator coordinator = new(
            new FakeLocalServerHandle(isReady: true, boundPort: 6123), connector, identity: new RecordingIdentityStore { Fail = true });

        coordinator.BeginConnect(new ClientConnectionOptions("play.example.com", "5057", "player-a"));
        await coordinator.WaitForPendingConnectionAsync();

        Assert.Equal(ConnectionFlowState.Failed, coordinator.State);
        Assert.Contains("identity", coordinator.StatusMessage);
        Assert.Null(connector.Options);
    }

    private sealed class RecordingIdentityStore : IClientIdentityStore
    {
        public List<string> Keys { get; } = [];

        public bool Fail { get; init; }

        public static string TokenFor(string key) => "T" + new string((char)('0' + key.Length % 10), 42);

        public string GetOrCreateToken(string serverKey)
        {
            if (Fail)
            {
                throw new IOException("The player identity file could not be saved.");
            }

            Keys.Add(serverKey);
            return TokenFor(serverKey);
        }
    }

    private sealed class CapturingConnector : IRemoteSessionConnector
    {
        public RemoteClientOptions? Options { get; private set; }

        public ValueTask<RemoteClientSession> ConnectAsync(
            RemoteClientOptions options,
            CancellationToken cancellationToken = default)
        {
            Options = options;
            return new FakeRemoteSessionConnector().ConnectAsync(options, cancellationToken);
        }
    }

    private sealed class SlowConnector : IRemoteSessionConnector
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Calls { get; private set; }

        public void Release() => _gate.SetResult();

        public async ValueTask<RemoteClientSession> ConnectAsync(
            RemoteClientOptions options,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            await _gate.Task.ConfigureAwait(false);
            return await new FakeRemoteSessionConnector().ConnectAsync(options, cancellationToken);
        }
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

        public DedicatedServerPerformanceSnapshot LatestPerformance =>
            DedicatedServerPerformanceSnapshot.Empty;

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
