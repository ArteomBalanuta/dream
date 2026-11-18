using System.Collections;
using System.Reflection;
using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Server;
using Dream.Simulation.Characters;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Support;

namespace Dream.Tests.BugHunt;

// BH-REV2-2: the standing refresh queues a read of the connected accounts' standing on a cadence, and
// ApplyAccountUpdate used to drop the recent-sanction memory for every account the read came back with,
// on the grounds that "the read was queued behind every write this host made". That is false for a read
// queued EARLIER: tick N queues read R, R reads the pre-ban row, the admin's /ban at tick N queues W
// behind it, W commits and remembers the sanction, and R's update then removed the memory the ban had
// just created and put the stale, unbanned standing back - so the ban or mute was not enforced on the
// live connection and the join-refusal memory was gone, while the administrator's confirmation (which
// rides with W's own update) still said it was done. It self-corrected only at the next refresh, 960
// ticks (15 s) later by default.
//
// The memory now carries the position it was remembered at, and the read carries the position it saw
// when it STARTED, so a memory newer than the read survives it. These drive the host's private members
// by reflection, as PersistSanctionMemoryTests does.
public sealed class Rev2StandingRefreshSanctionTests
{
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

    private static readonly Type UpdateType = typeof(DedicatedServerHost)
        .GetNestedType("AccountUpdate", BindingFlags.NonPublic)!;

    // The sanction counter a read would have seen if it started now: the host's own, read the way the
    // refresh read reads it.
    // 0 when the host keeps no such counter, so this reads as "the read saw nothing" rather than
    // throwing: the assertions below are then about the behaviour, not about a field's name.
    private static long SanctionSequence(DedicatedServerHost host) =>
        typeof(DedicatedServerHost).GetField("_sanctionSequence", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(host) as long? ?? 0L;

    private static void RememberBan(DedicatedServerHost host, AccountKey key, DateTime until, string reason)
    {
        MethodInfo method = typeof(DedicatedServerHost)
            .GetMethod("RememberSanction", BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(host, [key, (ValueTuple<DateTime, string?>?)(until, reason), null, false, false]);
    }

    private static void RememberMute(DedicatedServerHost host, AccountKey key, DateTime until)
    {
        MethodInfo method = typeof(DedicatedServerHost)
            .GetMethod("RememberSanction", BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(host, [key, null, until, false, false]);
    }

    // Applies the update a standing read produces: the row as the read saw it, with the sanction
    // position that read started from.
    private static void ApplyRead(DedicatedServerHost host, AccountKey key, AccountStanding read, long readSequence)
    {
        // A host that carries no read position takes the three-argument shape, so this drives the old
        // and the new host alike and the assertions stay about the behaviour.
        object update = UpdateType.GetConstructors().Any(c => c.GetParameters().Length == 4)
            ? Activator.CreateInstance(UpdateType, [key, (AccountStanding?)read, null, readSequence])!
            : Activator.CreateInstance(UpdateType, [key, (AccountStanding?)read, null])!;
        MethodInfo apply = typeof(DedicatedServerHost)
            .GetMethod("ApplyAccountUpdate", BindingFlags.Instance | BindingFlags.NonPublic)!;
        apply.Invoke(host, [update]);
    }

    private static bool HasMemory(DedicatedServerHost host, AccountKey key)
    {
        FieldInfo field = typeof(DedicatedServerHost)
            .GetField("_sanctionMemory", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return ((IDictionary)field.GetValue(host)!).Contains(key.HashHex);
    }

    private static string? RecentBanText(DedicatedServerHost host, AccountKey key)
    {
        MethodInfo method = typeof(DedicatedServerHost)
            .GetMethod("RecentBanText", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (string?)method.Invoke(host, [(AccountKey?)key]);
    }

    private static AccountStanding Unsanctioned => new(0, null, null, null);

    // The defect, in its order: the read starts, the ban lands behind it, the read's update is applied.
    [Fact]
    public void ARefreshReadThatStartedBeforeTheBan_DoesNotUndoIt()
    {
        DedicatedServerHost host = CreateHost();
        AccountKey key = TestAccountTokens.KeyFor("rev2-ban-target");

        // Tick N: the refresh read starts and reads the pre-ban row.
        long readSequence = SanctionSequence(host);
        AccountStanding stale = Unsanctioned;

        // The administrator's /ban runs behind it and its write commits.
        RememberBan(host, key, DateTime.UtcNow.AddHours(1), "griefing");
        Assert.True(HasMemory(host, key));

        // Tick N+1: the read's update lands. Before the fix this removed the memory the ban had just
        // created, and a join in flight was judged on the stale row again.
        ApplyRead(host, key, stale, readSequence);

        Assert.True(HasMemory(host, key), "the ban's recent-sanction memory was dropped by an older read");
        Assert.NotNull(RecentBanText(host, key));
    }

    // The same for a mute: the memory is what the live connection's standing is overlaid from.
    [Fact]
    public void ARefreshReadThatStartedBeforeTheMute_DoesNotUndoIt()
    {
        DedicatedServerHost host = CreateHost();
        AccountKey key = TestAccountTokens.KeyFor("rev2-mute-target");

        long readSequence = SanctionSequence(host);
        RememberMute(host, key, DateTime.UtcNow.AddMinutes(10));

        ApplyRead(host, key, Unsanctioned, readSequence);

        Assert.True(HasMemory(host, key), "the mute's recent-sanction memory was dropped by an older read");
    }

    // Control, and the reason the drop exists at all: a read that STARTED after the sanction is the
    // newer of the two, so it drops the memory and another process's `--accounts unban` decides again
    // instead of being overridden for the whole two-minute window.
    [Fact]
    public void ARefreshReadThatStartedAfterTheSanction_StillDropsTheMemory()
    {
        DedicatedServerHost host = CreateHost();
        AccountKey key = TestAccountTokens.KeyFor("rev2-unban-elsewhere");

        RememberBan(host, key, DateTime.UtcNow.AddHours(1), "griefing");
        Assert.True(HasMemory(host, key));

        // The read starts after the ban was remembered, and comes back with the row another process
        // has since cleared.
        long readSequence = SanctionSequence(host);
        ApplyRead(host, key, Unsanctioned, readSequence);

        Assert.False(HasMemory(host, key), "a newer read must still let an external change decide");
        Assert.Null(RecentBanText(host, key));
    }

    // Control: a read older than one account's sanction leaves the OTHER accounts' memories alone, and
    // a second sanction behind the same read is kept too.
    [Fact]
    public void AnOlderRead_KeepsEverySanctionWrittenAfterIt()
    {
        DedicatedServerHost host = CreateHost();
        AccountKey first = TestAccountTokens.KeyFor("rev2-multi-a");
        AccountKey second = TestAccountTokens.KeyFor("rev2-multi-b");

        long readSequence = SanctionSequence(host);
        RememberBan(host, first, DateTime.UtcNow.AddHours(1), "one");
        RememberMute(host, second, DateTime.UtcNow.AddMinutes(5));

        ApplyRead(host, first, Unsanctioned, readSequence);
        ApplyRead(host, second, Unsanctioned, readSequence);

        Assert.True(HasMemory(host, first));
        Assert.True(HasMemory(host, second));
    }
}
