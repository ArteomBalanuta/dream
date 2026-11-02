using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Multiplayer;
using Dream.Simulation.Runtime;

namespace Dream.Tests.Simulation;

// M5/M8 fix loop, below the network: the per-session cap of the session message queue, the party invite
// cooldown and the silent refresh of the registry, and the request phase of a trade. The loopback proof
// (muted senders, the client's notice) is in M5EndToEndLoopbackTests and AdminAccountSanctionLoopbackTests.
public sealed class PartyFixLoopTests
{
    [Fact]
    public void TheSessionQueueCap_IsPerSession_SoOneBackedUpSessionNeverPushesAnothersMessagesOut()
    {
        using RuntimeAuthoritativeSessionHost host = RuntimeAuthoritativeSessionHost.CreateDefault();
        WorldSessionRuntime runtime = host.Runtime;
        PlayerSessionId quiet = new("quiet");
        PlayerSessionId noisy = new("noisy");
        runtime.QueueSessionMessage(quiet, new ServerChatMessage(ChatChannel.System, "", "first for quiet", 1));

        int cap = WorldSessionRuntime.MaxQueuedSessionMessagesPerSession;
        for (int i = 0; i < cap + 50; i++)
        {
            runtime.QueueSessionMessage(noisy, new ServerChatMessage(ChatChannel.System, "", $"noisy {i}", 1));
        }

        List<SessionServerMessage> drained = [];
        runtime.DrainSessionMessagesInto(drained);

        Assert.Single(drained, m => m.SessionId.Equals(quiet));
        List<string> noisyTexts = [.. drained.Where(m => m.SessionId.Equals(noisy)).Select(m => ((ServerChatMessage)m.Message).Text)];
        Assert.Equal(cap, noisyTexts.Count);
        // The oldest of the noisy session went, in order.
        Assert.Equal("noisy 50", noisyTexts[0]);
        Assert.Equal($"noisy {cap + 49}", noisyTexts[^1]);
    }

    [Fact]
    public void TheSessionQueueCounts_FollowAPartialDrain()
    {
        using RuntimeAuthoritativeSessionHost host = RuntimeAuthoritativeSessionHost.CreateDefault();
        WorldSessionRuntime runtime = host.Runtime;
        PlayerSessionId claimed = new("claimed");
        PlayerSessionId unclaimed = new("unclaimed");
        int cap = WorldSessionRuntime.MaxQueuedSessionMessagesPerSession;
        for (int i = 0; i < cap; i++)
        {
            runtime.QueueSessionMessage(claimed, new ServerChatMessage(ChatChannel.System, "", $"c{i}", 1));
        }

        runtime.QueueSessionMessage(unclaimed, new ServerChatMessage(ChatChannel.System, "", "u0", 1));
        List<SessionServerMessage> drained = [];
        runtime.DrainSessionMessagesInto(drained, id => id.Equals(claimed));
        Assert.Equal(cap, drained.Count);

        // The drained session starts from zero again: a full cap fits without dropping anything.
        for (int i = 0; i < cap; i++)
        {
            runtime.QueueSessionMessage(claimed, new ServerChatMessage(ChatChannel.System, "", $"d{i}", 1));
        }

        drained.Clear();
        runtime.DrainSessionMessagesInto(drained);
        Assert.Equal(cap + 1, drained.Count);
        Assert.Contains(drained, m => m.SessionId.Equals(unclaimed));
        Assert.Equal("d0", ((ServerChatMessage)drained.First(m => m.SessionId.Equals(claimed)).Message).Text);
    }

    [Fact]
    public void ARefreshedInvite_ChangesNothingForTheInvitee_AndIsReported()
    {
        PartyRegistry registry = new();
        Assert.Equal(PartyOutcome.Ok, registry.Invite("alice", "bob", 1, out bool refreshed));
        Assert.False(refreshed);
        List<string> changed = [];
        registry.DrainChanged(changed);
        Assert.Equal(["bob"], changed);

        Assert.Equal(PartyOutcome.Ok, registry.Invite("alice", "bob", 5, out refreshed));

        Assert.True(refreshed);
        Assert.False(registry.HasChanges);
        // The time was renewed all the same.
        registry.Expire(1 + (ulong)PartyRules.InviteTimeoutTicks + 1);
        Assert.Equal("alice", registry.PendingInviteFrom("bob"));
    }

    [Fact]
    public void AnInviter_IsLimitedToOneInvitePerCooldown_AndTheCooldownIsPerInviter()
    {
        PartyRegistry registry = new() { InviteCooldownTicks = 100 };
        Assert.Equal(PartyOutcome.Ok, registry.Invite("alice", "bob", 1000));
        Assert.Equal(PartyOutcome.Cooldown, registry.Invite("alice", "carol", 1050));
        Assert.Null(registry.PendingInviteFrom("carol"));
        // Another inviter is not held back by it.
        Assert.Equal(PartyOutcome.Ok, registry.Invite("dave", "carol", 1050));
        // A refused attempt did not restart the clock.
        Assert.Equal(PartyOutcome.Ok, registry.Invite("alice", "erin", 1100));

        registry.Remove("alice");
        Assert.Equal(PartyOutcome.Ok, registry.Invite("alice", "frank", 1101));
    }

    [Fact]
    public void WithoutACooldown_ARegistryInvitesFreely()
    {
        PartyRegistry registry = new();
        for (int i = 0; i < 10; i++)
        {
            Assert.Equal(PartyOutcome.Ok, registry.Invite("alice", "target" + i, 1));
        }
    }

    [Fact]
    public void ARequestIsAnsweredWithin20Seconds_ThePhaseTimeoutIsShorterThanTheTradeLimit()
    {
        Assert.Equal(20 * SimulationClock.AuthoritativeTicksPerSecond, TradeRules.RequestTimeoutTicks);
        Assert.True(TradeRules.RequestTimeoutTicks < TradeRules.TimeoutTicks);
    }
}
