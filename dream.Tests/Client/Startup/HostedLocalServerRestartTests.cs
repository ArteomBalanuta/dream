using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Xunit;
using Dream.Client.Identity;
using Dream.Client.Menu;
using Dream.Client.Networking;
using Dream.Client.Startup;
using Dream.Server;
using Dream.Tests.Server.Persistence.Migrations;

namespace Dream.Tests.Client.Startup;

// A hosted world must not outlive the Host attempt that started it: after a failed host, or a switch
// to Single Player, the port is released and the next start is loopback-only.
public sealed class HostedLocalServerRestartTests
{
    private static int FreePort()
    {
        TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static IPEndPoint[] Listeners() => IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();

    private static async Task<RemoteClientSession> TakeSessionAsync(SinglePlayerLauncher launcher)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            launcher.Update();
            if (launcher.TryTakeConnectedSession(out RemoteClientSession? session))
            {
                return session!;
            }

            Assert.False(launcher.Failed, launcher.StatusMessage);
            await Task.Delay(20);
        }

        throw new TimeoutException($"Did not connect: {launcher.Phase} {launcher.StatusMessage}");
    }

    private sealed class SwitchableConnector : IRemoteSessionConnector
    {
        private readonly RemoteSessionConnector _real = new();

        public bool Fail { get; set; }

        public ValueTask<RemoteClientSession> ConnectAsync(RemoteClientOptions options, CancellationToken cancellationToken = default) =>
            Fail
                ? ValueTask.FromException<RemoteClientSession>(new InvalidOperationException("connection refused"))
                : _real.ConnectAsync(options, cancellationToken);
    }

    [Fact]
    public async Task HostConnectFails_ReleasesThePublicPort_AndSinglePlayerThenStartsLoopbackOnly()
    {
        using MigrationTestDatabase db = new();
        int port = FreePort();
        OnDemandLocalServerHandle handle = new(listen =>
            IntegratedLocalServerHandle.StartAsync(
                hostFactory: () => ServerBootstrap.CreateAuthoritativeHost(
                    ServerBootstrap.CreatePersistenceBundle(db.DatabasePath, MigrationTestDatabase.InitScriptPath)),
                listen: listen).AsTask().GetAwaiter().GetResult());
        SwitchableConnector connector = new() { Fail = true };
        NetworkSessionCoordinator coordinator = new(
            handle, connector, identity: new ClientIdentityStore(Path.Combine(db.RootDirectory, "tokens.json")));
        SinglePlayerLauncher launcher = new(coordinator, handle, null, () => "single");
        await using (handle)
        {
            launcher.RequestHost(port, "host-a");
            launcher.NotifyFramePresented();
            launcher.NotifyFramePresented();
            DateTime deadline = DateTime.UtcNow.AddSeconds(30);
            while (!launcher.Failed && DateTime.UtcNow < deadline)
            {
                launcher.Update();
                await Task.Delay(20);
            }

            Assert.True(launcher.Failed, launcher.StatusMessage);
            Assert.Equal(LocalServerListenOptions.Loopback, handle.ListenOptions);

            // The stop runs in the background; the next start queues behind it.
            connector.Fail = false;
            launcher.RequestStart();
            launcher.NotifyFramePresented();
            launcher.NotifyFramePresented();
            await using RemoteClientSession session = await TakeSessionAsync(launcher);

            Assert.Equal(LocalServerListenOptions.Loopback, handle.ListenOptions);
            Assert.DoesNotContain(Listeners(), e => e.Port == port);
            Assert.DoesNotContain(Listeners(), e => e.Port == handle.BoundPort && e.Address.Equals(IPAddress.Any));
            Assert.Contains(Listeners(), e => e.Port == handle.BoundPort && e.Address.Equals(IPAddress.Loopback));
        }
    }
}
