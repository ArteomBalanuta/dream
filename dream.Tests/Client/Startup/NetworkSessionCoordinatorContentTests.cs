using Xunit;
using Dream.Client.Networking;
using Dream.Client.Startup;
using Dream.Server;

namespace Dream.Tests.Client.Startup;

// Wave I3 (I-D25): the content error blocks only Use Local Server. A remote connect reads no content
// files, so it goes ahead; the replacement for the old "refuses to connect" expectation.
public sealed class NetworkSessionCoordinatorContentTests
{
    private const string ContentError = "The game content is invalid: x";

    [Fact]
    public async Task ARemoteConnect_StillHappens_WithInvalidContent()
    {
        int starts = 0;
        await using OnDemandLocalServerHandle local = new(() =>
        {
            starts++;
            return new StubHandle();
        });
        CountingConnector connector = new();
        NetworkSessionCoordinator coordinator = new(local, connector, contentError: ContentError);

        Assert.NotEqual(ConnectionFlowState.Failed, coordinator.State);

        coordinator.BeginConnect(new ClientConnectionOptions("play.example.com", "6000", "p"));
        await coordinator.WaitForPendingConnectionAsync();

        Assert.Equal(1, connector.Calls);
        Assert.Equal(0, starts);
        // The connector in this test fails, and the screen says so, not the content error.
        Assert.Equal(ConnectionFlowState.Failed, coordinator.State);
        Assert.DoesNotContain("content is invalid", coordinator.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UseLocalServer_ShowsTheContentError_AndStartsNoServer()
    {
        int starts = 0;
        await using OnDemandLocalServerHandle local = new(() =>
        {
            starts++;
            return new StubHandle();
        });
        NetworkSessionCoordinator coordinator = new(local, new CountingConnector(), contentError: ContentError);

        coordinator.EnsureLocalServerStarted();
        bool used = coordinator.TryUseLocalServer("p", out ClientConnectionOptions? options);

        Assert.False(used);
        Assert.Null(options);
        Assert.Equal(0, starts);
        Assert.Equal(ConnectionFlowState.Failed, coordinator.State);
        Assert.Contains("content is invalid", coordinator.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutAContentError_NothingChanges()
    {
        NetworkSessionCoordinator coordinator = new(
            new OnDemandLocalServerHandle(() => new StubHandle()), new CountingConnector());

        Assert.Equal(ConnectionFlowState.ReadyToConnect, coordinator.State);
    }

    private sealed class CountingConnector : IRemoteSessionConnector
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
