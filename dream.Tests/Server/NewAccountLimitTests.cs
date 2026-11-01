using Dream.Multiplayer.Protocol;
using Dream.Server;
using Dream.Tests.Support;
using Xunit;

namespace Dream.Tests.Server;

// Wave G fix loop 2: a per-source limit on NEW accounts (a token that owns no character yet). Minting
// tokens bypassed the 5-characters-per-token cap; one address now gets a small burst, then a few a minute.
public sealed class NewAccountLimitTests
{
    private const long Frequency = 1000;

    [Fact]
    public void Defaults_AreABurstOfFive_ThenThreePerMinute()
    {
        NewAccountLimiter limiter = new();
        Assert.Equal(5, limiter.Burst);
        Assert.Equal(3, limiter.PerMinute);
    }

    [Fact]
    public void Limiter_AllowsTheBurst_ThenRefusesWithTheSecondsToWait_ThenRefills()
    {
        NewAccountLimiter limiter = new(burst: 2, perMinute: 3);
        const string source = "10.0.0.5";
        for (int i = 0; i < 2; i++)
        {
            Assert.Equal(0, limiter.SecondsUntilAllowed(source, now: 0, Frequency));
            limiter.Record(source, now: 0, Frequency);
        }

        Assert.Equal(20, limiter.SecondsUntilAllowed(source, now: 0, Frequency));
        Assert.Equal(0, limiter.SecondsUntilAllowed("10.0.0.6", now: 0, Frequency));
        // 3 a minute: one token back after 20 s.
        Assert.Equal(0, limiter.SecondsUntilAllowed(source, now: 20 * Frequency, Frequency));
        limiter.Record(source, now: 20 * Frequency, Frequency);
        Assert.True(limiter.SecondsUntilAllowed(source, now: 20 * Frequency, Frequency) > 0);
    }

    [Fact]
    public void Limiter_ExemptsLoopback_AndCanBeSwitchedOff()
    {
        NewAccountLimiter limiter = new(burst: 1, perMinute: 1);
        foreach (string loopback in new[] { "127.0.0.1", "::1" })
        {
            for (int i = 0; i < 20; i++)
            {
                limiter.Record(loopback, now: 0, Frequency);
            }

            Assert.Equal(0, limiter.SecondsUntilAllowed(loopback, now: 0, Frequency));
        }

        Assert.Equal(0, limiter.Count);

        NewAccountLimiter off = new(burst: 0, perMinute: 3);
        off.Record("10.0.0.5", now: 0, Frequency);
        Assert.Equal(0, off.SecondsUntilAllowed("10.0.0.5", now: 0, Frequency));
    }

    [Fact]
    public void Limiter_TableIsBounded()
    {
        NewAccountLimiter limiter = new(burst: 2, perMinute: 3);
        for (int i = 0; i < NewAccountLimiter.MaxEntries * 3; i++)
        {
            limiter.Record($"10.{i / 65536 % 256}.{i / 256 % 256}.{i % 256}", now: i, Frequency);
        }

        Assert.True(limiter.Count <= NewAccountLimiter.MaxEntries, $"{limiter.Count} entries");
    }

    [Fact]
    public void Host_RefusesANewAccountPastTheLimit_ButAnExistingAccountStillJoins()
    {
        using AccountHostHarness harness = new();
        harness.Host.NewAccountLimits = new NewAccountLimiter(burst: 2, perMinute: 1);

        harness.JoinAccepted("10.0.0.9:4001", "s-1", "One");
        harness.JoinAccepted("10.0.0.9:4002", "s-2", "Two");
        string reason = harness.JoinRejectionOf("10.0.0.9:4003", "s-3", "Three", TestAccountTokens.For("Three"));

        Assert.StartsWith(DedicatedServerHost.NewAccountRejectionReason, reason);
        Assert.Contains("Try again in", reason);
        Assert.Equal(0, harness.Database.Count("SELECT COUNT(*) FROM player_profiles WHERE player_name = 'Three';"));

        // An account that already owns a character is never limited, from the same address.
        Assert.IsType<JoinAcceptedMessage>(harness.Join("10.0.0.9:4004", "s-4", "One", TestAccountTokens.For("One")));
        // Another address has its own allowance.
        harness.JoinAccepted("10.0.0.10:4005", "s-5", "Three");

        // The wait is real: a minute later the source may create one again.
        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        harness.JoinAccepted("10.0.0.9:4006", "s-6", "Four");
    }

    [Fact]
    public void Host_NeverLimitsLoopbackSources()
    {
        using AccountHostHarness harness = new();
        harness.Host.NewAccountLimits = new NewAccountLimiter(burst: 1, perMinute: 1);

        for (int i = 0; i < 6; i++)
        {
            harness.JoinAccepted($"127.0.0.1:{5000 + i}", $"s-{i}", $"Loop{i}");
        }

        Assert.Equal(0, harness.Host.NewAccountLimits.Count);
    }
}
