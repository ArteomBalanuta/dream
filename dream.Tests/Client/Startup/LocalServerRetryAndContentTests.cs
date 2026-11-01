using Xunit;
using Dream.Client.Networking;
using Dream.Client.Startup;
using Dream.Server;
using Dream.Server.Hosting;
using Dream.Simulation.Content;
using Dream.Simulation.World;
using Dream.Tests.Server.Persistence.Migrations;

namespace Dream.Tests.Client.Startup;

// Wave F fix loop: Use Local Server leaves the entered endpoint alone while the server is not
// ready and can start again after a failed start; a client with unusable content says so on the
// connection screen.
public sealed class LocalServerRetryAndContentTests
{
    private static OnDemandLocalServerHandle LocalServerOn(MigrationTestDatabase db) =>
        new(() => IntegratedLocalServerHandle.StartAsync(
            hostFactory: () => ServerBootstrap.CreateAuthoritativeHost(
                ServerBootstrap.CreatePersistenceBundle(db.DatabasePath, MigrationTestDatabase.InitScriptPath)))
            .AsTask().GetAwaiter().GetResult());

    [Fact]
    public async Task UseLocalServer_WhileTheWorldIsHeld_ReturnsFalse_AndWhenItIsFree_StartsAgain()
    {
        using MigrationTestDatabase db = new();
        WorldHostLease holder = WorldHostLease.Acquire(db.DatabasePath, WorldId.Default);
        await using OnDemandLocalServerHandle local = LocalServerOn(db);
        NetworkSessionCoordinator coordinator = new(local, new NoopConnector());

        bool first = coordinator.TryUseLocalServer("player-a", out ClientConnectionOptions? options);

        Assert.False(first);
        Assert.Null(options);
        Assert.Contains("already in use by another Dream window", coordinator.LocalServerStatus, StringComparison.Ordinal);
        Assert.Equal(ConnectionFlowState.Failed, coordinator.State);

        // The first window closes; the second client presses Use Local Server again.
        holder.Dispose();
        bool second = coordinator.TryUseLocalServer("player-a", out options);

        Assert.True(second, coordinator.LocalServerStatus);
        Assert.NotNull(options);
        Assert.Equal("127.0.0.1", options!.Host);
        Assert.Equal(local.BoundPort.ToString(), options.PortText);
        Assert.True(local.IsReady);
        Assert.Equal(ConnectionFlowState.ReadyToConnect, coordinator.State);
    }

    [Fact]
    public async Task ResetFailure_LeavesARunningServerAlone()
    {
        using MigrationTestDatabase db = new();
        await using OnDemandLocalServerHandle local = LocalServerOn(db);
        local.EnsureStarted();
        Assert.True(local.IsReady, local.ErrorMessage);
        int port = local.BoundPort;

        local.ResetFailure();

        Assert.True(local.IsReady);
        Assert.Equal(port, local.BoundPort);
    }

    [Fact]
    public void ClientContentCheck_ReportsAFailingRead_AndPassesTheShippedContent()
    {
        string? message = ClientContentCheck.TryRead(
            static () => throw new ContentValidationException("items.json: bad starter kit"));

        Assert.NotNull(message);
        Assert.Contains("bad starter kit", message, StringComparison.Ordinal);
        Assert.Null(ClientContentCheck.TryReadShipped());
    }

    [Fact]
    // Wave I3 (I-D25) changed this on purpose: invalid content blocks only Use Local Server, and a
    // remote connect goes ahead (NetworkSessionCoordinatorContentTests covers the same from scratch).
    public async Task ACoordinatorWithInvalidContent_ShowsTheMessageOnUseLocalServer_StartsNoServer_AndStillConnects()
    {
        int starts = 0;
        await using OnDemandLocalServerHandle local = new(() =>
        {
            starts++;
            return new StubHandle();
        });
        NoopConnector connector = new();
        NetworkSessionCoordinator coordinator = new(local, connector, contentError: "The game content is invalid: x");

        coordinator.EnsureLocalServerStarted();
        Assert.False(coordinator.TryUseLocalServer("p", out _));
        Assert.Equal(ConnectionFlowState.Failed, coordinator.State);
        Assert.Contains("content is invalid", coordinator.StatusMessage, StringComparison.Ordinal);

        coordinator.BeginConnect(new ClientConnectionOptions("play.example.com", "6000", "p"));
        await coordinator.WaitForPendingConnectionAsync();

        Assert.Equal(0, starts);
        Assert.Equal(1, connector.Calls);
    }

    private sealed class NoopConnector : IRemoteSessionConnector
    {
        public int Calls { get; private set; }

        public ValueTask<RemoteClientSession> ConnectAsync(RemoteClientOptions options, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new IOException("no server in this test");
        }
    }

    private sealed class StubHandle : ILocalServerHandle
    {
        public bool IsReady => true;

        public int BoundPort => 1;

        public string? ErrorMessage => null;

        public DedicatedServerPerformanceSnapshot LatestPerformance => DedicatedServerPerformanceSnapshot.Empty;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
