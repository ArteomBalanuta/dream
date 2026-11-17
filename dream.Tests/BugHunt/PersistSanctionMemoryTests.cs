using System.Collections;
using System.Reflection;
using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server;
using Dream.Simulation.Characters;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Integration;
using Dream.Tests.Support;

namespace Dream.Tests.BugHunt;

// Two host-side bookkeeping bugs behind the moderation state. Both are reached through members the host
// keeps private, so these tests drive them by reflection rather than by changing the host's surface:
//  - BH-PERSIST-3: the recent-sanction memory's cap existed as a constant but was not enforced, because
//    the only eviction pass drops entries older than the window and a sanction sweep makes none.
//  - BH-PERSIST-1: a close reason that was never delivered stayed under the connection id, which is the
//    remote ip:port, so a later socket on the same endpoint was handed another session's kick or ban text.
public sealed class PersistSanctionMemoryTests
{
    private const int MaxSanctionMemory = 256;

    private static DedicatedServerHost CreateHost()
    {
        WorldSessionRuntime runtime = new(12, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        ProtocolRouter router = new(
            [
                new ClientHelloHandler(),
                new JoinRequestHandler(),
                new PlayerCommandHandler(),
                new PlayerActionHandler(),
                new WorldResyncRequestHandler()
            ],
            Array.Empty<IClientMessageHandler>());
        return new DedicatedServerHost(new RuntimeAuthoritativeSessionHost(runtime), router, deferJoinsToTick: true);
    }

    private static int SanctionMemoryCount(DedicatedServerHost host)
    {
        FieldInfo field = typeof(DedicatedServerHost)
            .GetField("_sanctionMemory", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return ((ICollection)field.GetValue(host)!).Count;
    }

    // RememberSanction(AccountKey, (DateTime, string?)?, DateTime?, bool, bool)
    private static void RememberMute(DedicatedServerHost host, AccountKey key, DateTime until)
    {
        MethodInfo method = typeof(DedicatedServerHost)
            .GetMethod("RememberSanction", BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(host, [key, null, until, false, false]);
    }

    private static void CloseConnectionWithReason(DedicatedServerHost host, string connectionId, string reason)
    {
        MethodInfo method = typeof(DedicatedServerHost)
            .GetMethod("CloseConnectionWithReason", BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(host, [connectionId, reason]);
    }

    // BH-PERSIST-3: a ban sweep against many accounts inside the two-minute window. Every entry is young,
    // so the age sweep frees nothing; the cap has to hold anyway.
    [Fact]
    public void SanctionMemory_StaysWithinItsCap_WhenEveryEntryIsYoungerThanTheWindow()
    {
        DedicatedServerHost host = CreateHost();
        DateTime until = DateTime.UtcNow.AddMinutes(30);
        for (int i = 0; i < MaxSanctionMemory * 4; i++)
        {
            RememberMute(host, TestAccountTokens.KeyFor($"bhp-sweep-{i}"), until);
            Assert.True(
                SanctionMemoryCount(host) <= MaxSanctionMemory,
                $"the recent-sanction memory grew to {SanctionMemoryCount(host)} after {i + 1} sanctions");
        }

        Assert.Equal(MaxSanctionMemory, SanctionMemoryCount(host));
    }

    // The newest sanction must survive the eviction: it is the one a join in flight is judged on.
    [Fact]
    public void SanctionMemory_UnderPressure_KeepsTheNewestSanction()
    {
        DedicatedServerHost host = CreateHost();
        DateTime until = DateTime.UtcNow.AddMinutes(30);
        for (int i = 0; i < MaxSanctionMemory * 2; i++)
        {
            RememberMute(host, TestAccountTokens.KeyFor($"bhp-pressure-{i}"), until);
        }

        AccountKey last = TestAccountTokens.KeyFor($"bhp-pressure-{(MaxSanctionMemory * 2) - 1}");
        FieldInfo field = typeof(DedicatedServerHost)
            .GetField("_sanctionMemory", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.True(((IDictionary)field.GetValue(host)!).Contains(last.HashHex));
    }

    // BH-PERSIST-1: the kick's reason is written under the connection id and enqueued for the service to
    // deliver. When the service has already removed that connection (its read loop ended, or a liveness
    // fault) nobody is told — and the reason used to stay behind. The id is the remote ip:port, which a
    // later socket can present, and that connection would then be told it was kicked.
    [Fact]
    public async Task ACloseReasonNobodyWasToldIsNotHandedToTheNextConnectionOnThatEndpoint()
    {
        await using WireLoopbackServer server = await WireLoopbackServer.StartAsync(CreateHost());
        // An endpoint the service knows nothing about: exactly the state the service's early return covers.
        const string Endpoint = "198.51.100.7:40001";
        const string Reason = "You were kicked from the server. Reason: naming another player";
        CloseConnectionWithReason(server.Host, Endpoint, Reason);

        // One service tick drains the taken-over queue and finds no connection for the id.
        await server.TickAsync();
        await server.TickAsync();

        string next = server.Host.TakeCloseReason(Endpoint);
        Assert.DoesNotContain("kicked", next, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(DedicatedServerHost.OpenedElsewhereRejectionReason, next);
    }
}
