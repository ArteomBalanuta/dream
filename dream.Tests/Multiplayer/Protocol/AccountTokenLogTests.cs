using System.Collections;
using System.Reflection;
using Xunit;
using Dream.Multiplayer.Diagnostics;
using Dream.Multiplayer.Protocol;
using Dream.Simulation.Characters;
using Dream.Tests.Support;

namespace Dream.Tests.Multiplayer.Protocol;

// Wave G (G2): the account token is a secret. A sentinel token goes through join, rejection and takeover with
// diagnostics on; it must appear in no log line, no ToString, no routing context and no queued join.
public sealed class AccountTokenLogTests
{
    private static readonly string SentinelToken = TestAccountTokens.For("sentinel-owner");
    private static readonly string OtherToken = TestAccountTokens.For("sentinel-stranger");

    [Fact]
    public void ASentinelToken_AppearsInNoLogLine_ThroughJoinRejectionBadTokenAndTakeover()
    {
        List<string> lines = [];
        string hashHex = TestAccountTokens.KeyFor("sentinel-owner").HashHex;
        string otherHashHex = TestAccountTokens.KeyFor("sentinel-stranger").HashHex;
        using AccountHostHarness harness = new();
        using (NetworkDiagnosticsLog.CaptureOnCurrentThread((category, message) => lines.Add($"{category} {message}")))
        {
            harness.JoinAccepted("c-1", "s-1", "Sentinel", SentinelToken);
            Assert.Equal(JoinRejectionReasons.CharacterOwned, harness.JoinRejectionOf("c-2", "s-2", "Sentinel", OtherToken));
            // A malformed token echoing the sentinel: still no echo.
            Assert.Equal(JoinRejectionReasons.TokenInvalid, harness.JoinRejectionOf("c-3", "s-3", "Sentinel", SentinelToken + "x"));
            Assert.Equal(JoinRejectionReasons.TokenRequired, harness.JoinRejectionOf("c-4", "s-4", "Sentinel", token: null));
            // Takeover by the same account, with its own session id.
            harness.JoinAccepted("c-5", "s-5", "Sentinel", SentinelToken);
        }

        Assert.NotEmpty(lines);
        foreach (string line in lines)
        {
            Assert.DoesNotContain(SentinelToken, line, StringComparison.Ordinal);
            Assert.DoesNotContain(OtherToken, line, StringComparison.Ordinal);
            Assert.DoesNotContain(hashHex, line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(otherHashHex, line, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void TheRoutingContextAndTheQueuedJoin_NeverHoldTheToken()
    {
        using AccountHostHarness harness = new();

        // Queued but not applied: the pending join holds the stripped message.
        harness.SendJoin("c-1", "s-1", "Held", SentinelToken);
        IList pending = (IList)typeof(Dream.Server.DedicatedServerHost)
            .GetField("_pendingJoins", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(harness.Host)!;
        Assert.Single(pending);
        object pendingJoin = pending[0]!;
        ClientJoinRequestMessage queued = (ClientJoinRequestMessage)pendingJoin.GetType().GetProperty("Request")!.GetValue(pendingJoin)!;
        Assert.Null(queued.AccountToken);
        Assert.Equal(TestAccountTokens.KeyFor("sentinel-owner"), queued.ResolvedAccount);
        Assert.DoesNotContain(SentinelToken, pendingJoin.ToString(), StringComparison.Ordinal);

        // Applied: the context's LastJoinRequest is the stripped message too.
        harness.JoinAccepted("c-1", "s-1", "Held", SentinelToken);
        Assert.True(harness.Host.TryGetConnectionContext("c-1", out ServerRoutingContext? context));
        ClientJoinRequestMessage last = Assert.IsType<ClientJoinRequestMessage>(context!.LastJoinRequest);
        Assert.Null(last.AccountToken);
        Assert.DoesNotContain(SentinelToken, last.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(SentinelToken, context.JoinedIdentity.ToString()!, StringComparison.Ordinal);
    }

    [Fact]
    public void ToString_OnTheMessage_AndTheAccountKey_HoldsNoToken()
    {
        ClientJoinRequestMessage message = new("s-1", "Name", SentinelToken)
        {
            ResolvedAccount = TestAccountTokens.KeyFor("sentinel-owner")
        };

        Assert.DoesNotContain(SentinelToken, message.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(message.ResolvedAccount!.Value.HashHex, message.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("account", message.ResolvedAccount.Value.ToString());
    }
}
