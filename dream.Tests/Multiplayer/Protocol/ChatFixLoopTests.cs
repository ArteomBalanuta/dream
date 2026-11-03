using Dream.Client.Input;
using Dream.Multiplayer.Protocol;
using Dream.Rendering.Ui.Panels;
using Dream.Simulation.Characters;
using Dream.Simulation.Persistence;
using Dream.Tests.Server;
using Xunit;

namespace Dream.Tests.Multiplayer.Protocol;

// M1 fix loop: the chat bucket outlives a connection, the character list is throttled per source address,
// and a character name with spaces can be whispered.
public sealed class ChatFixLoopTests
{
    [Fact]
    public void TheChatBucket_IsKeyedByTheLimiter_SoANewContextDoesNotRefillIt()
    {
        ChatRateLimiter limiter = new();
        long frequency = TimeProvider.System.TimestampFrequency;
        for (int i = 0; i < ServerRoutingContext.ChatBurst; i++)
        {
            Assert.True(limiter.TryConsume("a:account", 100, frequency));
        }

        // "Reconnect": the key is the same, so the same empty bucket answers.
        Assert.False(limiter.TryConsume("a:account", 100, frequency));
        Assert.True(limiter.TryConsume("a:other", 100, frequency));
        Assert.True(limiter.TryConsume("a:account", 100 + frequency, frequency));
    }

    [Fact]
    public void TheChatLimiterTable_IsBounded()
    {
        ChatRateLimiter limiter = new();
        long frequency = TimeProvider.System.TimestampFrequency;
        for (int i = 0; i < ChatRateLimiter.MaxEntries + 500; i++)
        {
            limiter.TryConsume("k" + i, i, frequency);
        }

        Assert.True(limiter.Count <= ChatRateLimiter.MaxEntries);
    }

    [Fact]
    public void CharacterListRequests_AreThrottledPerSourceAddress_NotPerConnection()
    {
        ServerConnectionTestFixtures.ManualTimeProvider clock = new();
        CharacterListHandler handler = new(clock);
        handler.Attach(new FakeAccounts(), writes: null);
        List<string> delivered = [];
        handler.Deliver = (id, _) => delivered.Add(id);

        // Many connections (new ports) from one address share its allowance.
        for (int port = 1000; port < 1020; port++)
        {
            handler.Handle(new ServerRoutingContext("203.0.113.9:" + port), new ClientCharacterListRequest(TestAccountTokenFor()));
        }

        Assert.True(delivered.Count <= CharacterListHandler.SourceBurst);
        Assert.NotEmpty(delivered);

        handler.Handle(new ServerRoutingContext("198.51.100.4:2000"), new ClientCharacterListRequest(TestAccountTokenFor()));
        Assert.Equal("198.51.100.4:2000", delivered[^1]);
    }

    [Fact]
    public void AWhisperToANameWithSpaces_NeedsQuotes_AndTheNameMayBeTheFullLength()
    {
        Assert.True(ChatInput.TryParse("/w \"two words\" hello there", out ClientChatMessage? quoted, out _));
        Assert.Equal(ChatChannel.Whisper, quoted!.Channel);
        Assert.Equal("two words", quoted.Target);
        Assert.Equal("hello there", quoted.Text);

        Assert.True(ChatInput.TryParse("/w Bob hi", out ClientChatMessage? plain, out _));
        Assert.Equal("Bob", plain!.Target);

        string full = new('n', CharacterNames.MaxNewNameLength);
        Assert.True(ChatInput.TryParse($"/w {full} hi", out ClientChatMessage? long32, out _));
        Assert.Equal(full, long32!.Target);
        Assert.Equal(CharacterNames.MaxNewNameLength, ChatHudState.MaxNameChars);
    }

    private static string TestAccountTokenFor() => Dream.Tests.Support.TestAccountTokens.For("throttle");

    private sealed class FakeAccounts : IAccountRepository
    {
        public IReadOnlyList<AccountCharacterSummary> ListCharacters(AccountKey account, int max) => [];

        public CharacterLookup Lookup(string trimmedName, AccountKey? account) => default;

        public bool RecordJoin(AccountKey account, CharacterKey character, bool claim, DateTime utcNow) => true;
    }
}
