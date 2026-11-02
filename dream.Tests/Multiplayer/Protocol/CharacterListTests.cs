using Xunit;
using Dream.Multiplayer.Diagnostics;
using Dream.Multiplayer.Protocol;
using Dream.Simulation.Characters;
using Dream.Simulation.Persistence;
using Dream.Tests.Server;
using Dream.Tests.Support;

namespace Dream.Tests.Multiplayer.Protocol;

// M1-C: the character picker's list. It reads only the requesting account's characters, answers a bad
// token and a host without accounts with an empty list, is throttled per connection, and never lets the
// token (or its hash) reach a log line.
public sealed class CharacterListTests
{
    private static readonly string TokenA = TestAccountTokens.For("list-a");
    private static readonly string TokenB = TestAccountTokens.For("list-b");

    [Fact]
    public void AList_NeverReturnsAnotherAccountsCharacters_OverSqlite()
    {
        using AccountHostHarness harness = AccountHostHarness.OverSqlite();
        harness.JoinAccepted("c-1", "s-1", "Alice", TokenA);
        harness.JoinAccepted("c-2", "s-2", "Alicia", TokenA);
        harness.JoinAccepted("c-3", "s-3", "Bob", TokenB);
        harness.Leave("c-1");
        harness.Leave("c-2");
        harness.Leave("c-3");

        ServerCharacterList forA = harness.ListOf("l-1", TokenA);
        ServerCharacterList forB = harness.ListOf("l-2", TokenB);
        ServerCharacterList forStranger = harness.ListOf("l-3", TestAccountTokens.For("list-stranger"));

        Assert.Equal(["Alice", "Alicia"], forA.Characters.Select(c => c.DisplayName).Order(StringComparer.Ordinal));
        Assert.Equal(["Bob"], forB.Characters.Select(c => c.DisplayName));
        Assert.All(forA.Characters, character => Assert.True(character.Level >= 1));
        Assert.Empty(forStranger.Characters);
    }

    [Fact]
    public void AnInvalidOrMissingToken_GetsAnEmptyList_AndReadsNothing()
    {
        FakeAccounts accounts = new();
        CharacterListHandler handler = new();
        handler.Attach(accounts, writes: null);
        List<(string, IServerToClientMessage)> delivered = [];
        handler.Deliver = (id, message) => delivered.Add((id, message));

        foreach (string token in new[] { "not-a-token", "", TokenA + "x" })
        {
            ServerRoutingContext context = new("c-bad");
            handler.Handle(context, new ClientCharacterListRequest(token));
            ServerCharacterList reply = Assert.IsType<ServerCharacterList>(Assert.Single(context.OutboundMessages));
            Assert.Empty(reply.Characters);
        }

        Assert.Equal(0, accounts.ListCalls);
        Assert.Empty(delivered);
    }

    [Fact]
    public void AHostWithoutAccounts_AnswersEmpty()
    {
        CharacterListHandler handler = new();
        ServerRoutingContext context = new("c-none");

        handler.Handle(context, new ClientCharacterListRequest(TokenA));

        Assert.Empty(Assert.IsType<ServerCharacterList>(Assert.Single(context.OutboundMessages)).Characters);
    }

    [Fact]
    public void ARepeatedRequest_IsThrottledPerConnection_AndTheListIsCapped()
    {
        ServerConnectionTestFixtures.ManualTimeProvider clock = new();
        FakeAccounts accounts = new() { Count = 40 };
        CharacterListHandler handler = new(clock);
        handler.Attach(accounts, writes: null);
        List<(string Id, IServerToClientMessage Message)> delivered = [];
        handler.Deliver = (id, message) => delivered.Add((id, message));
        ServerRoutingContext first = new("c-1");
        ServerRoutingContext second = new("c-2");

        handler.Handle(first, new ClientCharacterListRequest(TokenA));
        handler.Handle(first, new ClientCharacterListRequest(TokenA));
        handler.Handle(second, new ClientCharacterListRequest(TokenA));

        Assert.Equal(2, delivered.Count);
        Assert.Equal(["c-1", "c-2"], delivered.Select(d => d.Id));
        Assert.Equal(ServerCharacterList.MaxCharacters, Assert.IsType<ServerCharacterList>(delivered[0].Message).Characters.Count);

        clock.Advance(CharacterListHandler.RequestInterval + TimeSpan.FromMilliseconds(1));
        handler.Handle(first, new ClientCharacterListRequest(TokenA));

        Assert.Equal(3, delivered.Count);
        Assert.Equal(3, accounts.ListCalls);
    }

    [Fact]
    public void AJoinedConnection_IsNotAnswered()
    {
        FakeAccounts accounts = new();
        CharacterListHandler handler = new();
        handler.Attach(accounts, writes: null);
        ServerRoutingContext context = new("c-joined") { LastJoinRequest = new ClientJoinRequestMessage("s", "Name", null) };

        handler.Handle(context, new ClientCharacterListRequest(TokenA));

        Assert.Empty(context.OutboundMessages);
        Assert.Equal(0, accounts.ListCalls);
    }

    [Fact]
    public void TheToken_AppearsInNoLogLineOrToString()
    {
        List<string> lines = [];
        string hashHex = TestAccountTokens.KeyFor("list-a").HashHex;
        using AccountHostHarness harness = AccountHostHarness.OverSqlite();
        using (NetworkDiagnosticsLog.CaptureOnCurrentThread((category, message) => lines.Add($"{category} {message}")))
        {
            harness.JoinAccepted("c-1", "s-1", "Logged", TokenA);
            harness.Leave("c-1");
            harness.ListOf("l-1", TokenA);
            harness.ListOf("l-2", TokenA + "x");
        }

        foreach (string line in lines)
        {
            Assert.DoesNotContain(TokenA, line, StringComparison.Ordinal);
            Assert.DoesNotContain(hashHex, line, StringComparison.OrdinalIgnoreCase);
        }

        ClientCharacterListRequest request = new(TokenA);
        Assert.DoesNotContain(TokenA, request.ToString(), StringComparison.Ordinal);
    }

    private sealed class FakeAccounts : IAccountRepository
    {
        public int ListCalls;
        public int Count = 3;

        public CharacterLookup Lookup(string trimmedName, AccountKey? account) => default;

        public bool RecordJoin(AccountKey account, CharacterKey character, bool claim, DateTime utcNow) => true;

        public IReadOnlyList<AccountCharacterSummary> ListCharacters(AccountKey account, int max)
        {
            Interlocked.Increment(ref ListCalls);
            return Enumerable.Range(0, Count).Select(i => new AccountCharacterSummary($"Hero{i}", i + 1)).ToArray();
        }
    }
}
