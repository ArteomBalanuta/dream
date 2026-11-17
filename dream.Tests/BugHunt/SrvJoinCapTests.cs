using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Server;
using Dream.Tests.Support;

namespace Dream.Tests.BugHunt;

// Bug hunt, dedicated server: the MaxPlayers gate. A seat belongs to a stable identity, is counted
// once, and the join that the cap reads is untrusted client input.
public sealed class SrvJoinCapTests
{
    // BH-SRV-3: HoldsSeat matched a seated player by display name with no account check, so any
    // account could walk past a full server by naming a seated character. Character names ride in
    // every snapshot, so the name is attacker-chosen; the seat check must key on the account.
    [Fact]
    public void FullServer_AnotherAccountNamingASeatedCharacter_IsStillRefusedAsFull()
    {
        using AccountHostHarness harness = new();
        harness.Host.MaxPlayers = 1;
        harness.JoinAccepted("seated", "s-seated", "Alice", TestAccountTokens.For("alice"));
        Assert.True(harness.Host.IsConnectionJoined("seated"));

        // A different account, borrowing the seated character's public name.
        string reason = harness.JoinRejectionOf("hijacker", "s-hijacker", "Alice", TestAccountTokens.For("mallory"));

        Assert.Equal(ServerConnectionOptions.ServerFullRejectionReason, reason);
        Assert.False(harness.Host.IsConnectionJoined("hijacker"));
        Assert.True(harness.Host.IsConnectionJoined("seated"), "the seated player lost their seat");
    }

    // BH-SRV-3, the other half of the contract: the owner of the seat is still let back in, so the
    // fix did not lock a reconnecting player out of their own seat on a full server.
    [Fact]
    public void FullServer_TheSeatsOwnAccount_StillGetsItsSeatBack()
    {
        using AccountHostHarness harness = new();
        harness.Host.MaxPlayers = 1;
        string token = TestAccountTokens.For("alice");
        harness.JoinAccepted("seated", "s-seated", "Alice", token);

        // The same account from a new connection: a reconnect, not a new player.
        Assert.IsNotType<JoinRejectedMessage>(harness.Join("reconnect", "s-again", "Alice", token));
    }

    // BH-SRV-2: a seated player's queued repeat join was counted as a second seat, so the cap
    // refused a legitimate joiner one seat early until the next tick applied the repeat.
    [Fact]
    public void ASeatedPlayersRepeatJoin_DoesNotCostTheServerASecondSeat()
    {
        using AccountHostHarness harness = new();
        harness.Host.MaxPlayers = 2;
        harness.JoinAccepted("seated", "s-seated", "Alice", TestAccountTokens.For("alice"));

        // A repeat join from the seated connection, still waiting for the next tick.
        harness.SendJoin("seated", "s-seated", "Alice", TestAccountTokens.For("alice"));

        // One of the two seats is taken; the second is free.
        Assert.IsNotType<JoinRejectedMessage>(harness.Join("fresh", "s-fresh", "Bob", TestAccountTokens.For("bob")));
    }

    // BH-SRV-4 (also reported as BH-NET-3): a join with no displayName decodes as null, and a host
    // without accounts validates nothing before the cap, so HoldsSeat used to throw on Trim().
    [Fact]
    public void FullAccountlessServer_AJoinWithNoDisplayName_IsRefusedInsteadOfThrowing()
    {
        using DedicatedServerHost host = DedicatedServerHost.CreateRuntimeAttached();
        host.MaxPlayers = 1;
        host.HandleClientMessage("seated", new ClientJoinRequestMessage("s-seated", "Alice"));
        Assert.True(host.IsConnectionJoined("seated"));

        IReadOnlyList<IServerToClientMessage> replies =
            host.HandleClientMessage("nameless", new ClientJoinRequestMessage("s-nameless", null!));

        Assert.Equal(
            ServerConnectionOptions.ServerFullRejectionReason,
            Assert.IsType<JoinRejectedMessage>(Assert.Single(replies)).Reason);
        Assert.True(host.IsConnectionJoined("seated"));
    }
}
