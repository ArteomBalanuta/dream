using Xunit;
using Dream.Application;
using Dream.Client.Identity;
using Dream.Client.Networking;
using Dream.Client.Startup;
using Dream.Server;
using Dream.Tests.Server.Persistence.Migrations;

namespace Dream.Tests.Client.Startup;

// Wave G glue: local play over the real pieces (integrated server on a real database, the real token file,
// real TCP). The player does nothing about identity, and the same character is theirs after the integrated
// server came back on another port.
public sealed class IntegratedServerIdentityTests
{
    private const string Hero = "Local Hero";

    private static OnDemandLocalServerHandle LocalServerOn(MigrationTestDatabase db) =>
        new(() => IntegratedLocalServerHandle.StartAsync(
            hostFactory: () => ServerBootstrap.CreateAuthoritativeHost(
                ServerBootstrap.CreatePersistenceBundle(db.DatabasePath, MigrationTestDatabase.InitScriptPath)))
            .AsTask().GetAwaiter().GetResult());

    private static async Task<RemoteClientSession> ConnectAsync(NetworkSessionCoordinator coordinator, ClientConnectionOptions options)
    {
        coordinator.BeginConnect(options);
        await coordinator.WaitForPendingConnectionAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(coordinator.TryTakeConnectedSession(out RemoteClientSession? session), coordinator.StatusMessage);
        return session!;
    }

    private static async Task WaitUntilSettledAsync(RemoteClientSession session)
    {
        for (int i = 0; i < 600 && session.IsConnected && !session.IsJoined; i++)
        {
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task ThePlayer_JoinsTheIntegratedServerWithNoAction_AndAgainAfterItRestartedOnANewPort()
    {
        using MigrationTestDatabase db = new();
        string file = Path.Combine(db.RootDirectory, "tokens.json");
        ClientIdentityStore store = new(file);
        int endedPort;

        await using (OnDemandLocalServerHandle local = LocalServerOn(db))
        {
            NetworkSessionCoordinator coordinator = new(local, new RemoteSessionConnector(), identity: store);
            Assert.True(coordinator.TryUseLocalServer(Hero, out ClientConnectionOptions? options), coordinator.LocalServerStatus);
            await using RemoteClientSession session = await ConnectAsync(coordinator, options!);
            await WaitUntilSettledAsync(session);
            Assert.True(session.IsJoined, session.DisconnectReason);
            endedPort = local.BoundPort;
        }

        await using (OnDemandLocalServerHandle local = LocalServerOn(db))
        {
            // What Game builds after a session on its own server ended: the screen keeps the ended endpoint.
            NetworkSessionCoordinator coordinator = new(
                local,
                new LocalServerRelaunchConnector(new RemoteSessionConnector(), local, "127.0.0.1", endedPort),
                identity: store);
            await using RemoteClientSession session = await ConnectAsync(
                coordinator,
                new ClientConnectionOptions("127.0.0.1", endedPort.ToString(), Hero));
            await WaitUntilSettledAsync(session);
            Assert.True(session.IsJoined, session.DisconnectReason);
            Assert.NotEqual(endedPort, local.BoundPort);
        }

        Assert.Equal(1L, db.Scalar("SELECT COUNT(*) FROM accounts;"));
        Assert.Equal(1L, db.Scalar("SELECT COUNT(*) FROM player_profiles WHERE account_id IS NOT NULL;"));
        string text = File.ReadAllText(file);
        Assert.Contains("\"local\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("127.0.0.1", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCharacter_IsKeptWhenAnIntegratedServersDatabaseIsLaterServedByADedicatedServerOnLoopback()
    {
        using MigrationTestDatabase db = new();
        ClientIdentityStore store = new(Path.Combine(db.RootDirectory, "tokens.json"));

        await using (OnDemandLocalServerHandle local = LocalServerOn(db))
        {
            NetworkSessionCoordinator coordinator = new(local, new RemoteSessionConnector(), identity: store);
            Assert.True(coordinator.TryUseLocalServer(Hero, out ClientConnectionOptions? options), coordinator.LocalServerStatus);
            await using RemoteClientSession session = await ConnectAsync(coordinator, options!);
            await WaitUntilSettledAsync(session);
            Assert.True(session.IsJoined, session.DisconnectReason);
        }

        // A server this window did not start, reached by host and port like any dedicated server.
        await using (OnDemandLocalServerHandle dedicated = LocalServerOn(db))
        {
            dedicated.EnsureStarted();
            await dedicated.WaitForStartAsync();
            await using OnDemandLocalServerHandle idle = LocalServerOn(db);
            NetworkSessionCoordinator coordinator = new(idle, new RemoteSessionConnector(), identity: store);
            await using RemoteClientSession session = await ConnectAsync(
                coordinator,
                new ClientConnectionOptions("localhost", dedicated.BoundPort.ToString(), Hero));
            await WaitUntilSettledAsync(session);
            Assert.True(session.IsJoined, session.DisconnectReason);
        }

        Assert.Equal(1L, db.Scalar("SELECT COUNT(*) FROM accounts;"));
        Assert.Equal(1L, db.Scalar("SELECT COUNT(*) FROM player_profiles WHERE account_id IS NOT NULL;"));
    }

    [Fact]
    public async Task ASecondWindow_JoiningTheFirstWindowsEphemeralPort_KeepsItsCharacter()
    {
        using MigrationTestDatabase db = new();
        string file = Path.Combine(db.RootDirectory, "tokens.json");
        await using OnDemandLocalServerHandle first = LocalServerOn(db);
        NetworkSessionCoordinator firstWindow = new(first, new RemoteSessionConnector(), identity: new ClientIdentityStore(file));
        Assert.True(firstWindow.TryUseLocalServer(Hero, out ClientConnectionOptions? options), firstWindow.LocalServerStatus);
        await using (RemoteClientSession joined = await ConnectAsync(firstWindow, options!))
        {
            await WaitUntilSettledAsync(joined);
            Assert.True(joined.IsJoined, joined.DisconnectReason);
        }

        // The second window has its own coordinator and token store over the same file, and no server of its own.
        await using OnDemandLocalServerHandle none = LocalServerOn(db);
        NetworkSessionCoordinator secondWindow = new(none, new RemoteSessionConnector(), identity: new ClientIdentityStore(file));
        await using RemoteClientSession session = await ConnectAsync(secondWindow, options!);
        await WaitUntilSettledAsync(session);

        Assert.True(session.IsJoined, session.DisconnectReason);
        Assert.Equal(1L, db.Scalar("SELECT COUNT(*) FROM accounts;"));
        Assert.DoesNotContain("127.0.0.1", File.ReadAllText(file), StringComparison.Ordinal);
    }
}
