using Xunit;

public class PlayerSessionRegistryTests
{
    [Fact]
    public void RegisterLocalPlayer_CreatesSpawnedSessionWithOwnedActor()
    {
        PlayerSessionRegistry registry = new();
        PlayerSessionId sessionId = new("local-player-1");

        ConnectedPlayer player = registry.Register(sessionId, actorId: 7, isLocal: true, displayName: "Local Hero");

        Assert.Equal(sessionId, player.SessionId);
        Assert.Equal(7, player.ActorId);
        Assert.True(player.IsLocal);
        Assert.Equal("Local Hero", player.DisplayName);
        Assert.True(player.IsSpawned);
        Assert.Null(player.LastAcceptedCommandSequence);
    }

    [Fact]
    public void TryAcceptCommand_RequiresStrictlyIncreasingSequence()
    {
        PlayerSessionRegistry registry = new();
        PlayerSessionId sessionId = new("local-player-1");
        registry.Register(sessionId, actorId: 7, isLocal: true);

        Assert.True(registry.TryAcceptCommand(sessionId, 1, out ConnectedPlayer? firstAccepted));
        Assert.NotNull(firstAccepted);
        Assert.Equal((ulong)1, firstAccepted.LastAcceptedCommandSequence);

        Assert.False(registry.TryAcceptCommand(sessionId, 1, out _));
        Assert.False(registry.TryAcceptCommand(sessionId, 0, out _));
        Assert.True(registry.TryAcceptCommand(sessionId, 2, out ConnectedPlayer? secondAccepted));
        Assert.NotNull(secondAccepted);
        Assert.Equal((ulong)2, secondAccepted.LastAcceptedCommandSequence);
    }

    [Fact]
    public void Register_BlankDisplayName_FallsBackToSessionId()
    {
        PlayerSessionRegistry registry = new();
        PlayerSessionId sessionId = new("session-a");

        ConnectedPlayer player = registry.Register(sessionId, actorId: 11, isLocal: false, displayName: " ");

        Assert.Equal("session-a", player.DisplayName);
    }
}
