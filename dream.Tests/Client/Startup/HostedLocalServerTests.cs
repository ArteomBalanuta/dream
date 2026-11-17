using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Xunit;
using Dream.Client.Identity;
using Dream.Client.Menu;
using Dream.Client.Networking;
using Dream.Client.Startup;
using Dream.Server;
using Dream.Server.Hosting;
using Dream.Tests.Server.Persistence.Migrations;

namespace Dream.Tests.Client.Startup;

// Multiplayer menu: the integrated server can bind every interface on a chosen port, and the host
// joins it over loopback like Single Player.
public sealed class HostedLocalServerTests
{
    private static int FreePort()
    {
        TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static Func<DedicatedServerHost> HostFactory(MigrationTestDatabase db, Action<DedicatedServerHost>? capture = null) =>
        () =>
        {
            DedicatedServerHost host = ServerBootstrap.CreateAuthoritativeHost(
                ServerBootstrap.CreatePersistenceBundle(db.DatabasePath, MigrationTestDatabase.InitScriptPath));
            capture?.Invoke(host);
            return host;
        };

    private static bool EnvDevCommands() =>
        string.Equals(Environment.GetEnvironmentVariable(ServerBootstrap.DevCommandsEnvironmentVariable), "1", StringComparison.Ordinal);

    private static async Task<RemoteClientSession> TakeSessionAsync(SinglePlayerLauncher launcher)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(20);
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

        throw new TimeoutException($"Host did not connect: {launcher.Phase} {launcher.StatusMessage}");
    }

    private static (OnDemandLocalServerHandle Handle, SinglePlayerLauncher Launcher) Create(MigrationTestDatabase db)
    {
        OnDemandLocalServerHandle handle = new(listen =>
            IntegratedLocalServerHandle.StartAsync(hostFactory: HostFactory(db), listen: listen).AsTask().GetAwaiter().GetResult());
        NetworkSessionCoordinator coordinator = new(handle, new RemoteSessionConnector(), identity: new ClientIdentityStore(Path.Combine(db.RootDirectory, "tokens.json")));
        return (handle, new SinglePlayerLauncher(coordinator, handle, null, () => "single"));
    }

    [Fact]
    public async Task Hosted_BindsAllInterfacesOnThePort_WithDedicatedConnectionRules()
    {
        using MigrationTestDatabase db = new();
        int port = FreePort();
        DedicatedServerHost? captured = null;

        await using IntegratedLocalServerHandle handle = await IntegratedLocalServerHandle.StartAsync(
            hostFactory: HostFactory(db, h => captured = h),
            listen: LocalServerListenOptions.Hosted(port));

        Assert.True(handle.IsReady, handle.ErrorMessage);
        Assert.Equal(port, handle.BoundPort);
        Assert.Contains(
            IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners(),
            e => e.Port == port && e.Address.Equals(IPAddress.Any));
        Assert.True(handle.ConnectionOptions.IdleTimeout > TimeSpan.Zero);
        Assert.Equal(EnvDevCommands(), captured!.DevCommandsAllowed);
        Assert.Equal(IPAddress.Any, handle.ListenOptions.BindAddress);
    }

    [Fact]
    public async Task HostRequest_StartsHostedServer_AndTheHostJoinsOverLoopback()
    {
        using MigrationTestDatabase db = new();
        int port = FreePort();
        var (handle, launcher) = Create(db);
        await using (handle)
        {
            launcher.RequestHost(port, "host-a");
            launcher.NotifyFramePresented();
            launcher.NotifyFramePresented();

            await using RemoteClientSession session = await TakeSessionAsync(launcher);

            Assert.Equal(IPAddress.Any, handle.ListenOptions.BindAddress);
            Assert.Equal(port, handle.BoundPort);
            Assert.Equal("host-a", session.Options.DisplayName);
        }
    }

    [Fact]
    public async Task HostRequest_OnATakenPort_FailsClearly_AndSucceedsOnceThePortIsFree()
    {
        using MigrationTestDatabase db = new();
        int port = FreePort();
        var (handle, launcher) = Create(db);
        await using (handle)
        {
            TcpListener holder = new(IPAddress.Any, port);
            holder.Start();
            try
            {
                launcher.RequestHost(port, "host-a");
                launcher.NotifyFramePresented();
                launcher.NotifyFramePresented();
                launcher.Update();

                Assert.Equal(SinglePlayerPhase.Failed, launcher.Phase);
                Assert.Contains("already in use", launcher.StatusMessage, StringComparison.Ordinal);
            }
            finally
            {
                holder.Stop();
            }

            launcher.RequestHost(port, "host-a");
            launcher.NotifyFramePresented();
            launcher.NotifyFramePresented();

            await using RemoteClientSession session = await TakeSessionAsync(launcher);
            Assert.NotNull(session);
        }
    }

    [Fact]
    public async Task DefaultStart_StillBindsLoopbackWithLivenessChecksOff()
    {
        using MigrationTestDatabase db = new();
        await using IntegratedLocalServerHandle handle = await IntegratedLocalServerHandle.StartAsync(hostFactory: HostFactory(db));

        Assert.True(handle.IsReady, handle.ErrorMessage);
        Assert.Equal(IPAddress.Loopback, handle.ListenOptions.BindAddress);
        Assert.Equal(TimeSpan.Zero, handle.ConnectionOptions.IdleTimeout);
        Assert.Contains(
            IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners(),
            e => e.Port == handle.BoundPort && e.Address.Equals(IPAddress.Loopback));
    }
}
