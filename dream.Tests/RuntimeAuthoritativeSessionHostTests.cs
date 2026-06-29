using OpenTK.Mathematics;
using Xunit;

public class RuntimeAuthoritativeSessionHostTests
{
    [Fact]
    public void JoinSession_StoresRequestedDisplayName_OnConnectedPlayer()
    {
        SceneEventHub events = new();
        using WorldSessionRuntime runtime = new(10, new Vector3(5f, 1.5f, 5f), events, loadVisualAssets: false);
        using RuntimeAuthoritativeSessionHost host = new(runtime);

        JoinAcceptedMessage accepted = host.JoinSession("session-a", "Player A");

        Assert.Equal("session-a", accepted.SessionId);
        Assert.True(runtime.Sessions.TryGet(new PlayerSessionId("session-a"), out ConnectedPlayer? player));
        Assert.NotNull(player);
        Assert.Equal("Player A", player.DisplayName);
    }

    [Fact]
    public void JoinSession_ReturnsCurrentJoinedPlayerRoster()
    {
        SceneEventHub events = new();
        using WorldSessionRuntime runtime = new(10, new Vector3(5f, 1.5f, 5f), events, loadVisualAssets: false);
        using RuntimeAuthoritativeSessionHost host = new(runtime);

        host.JoinSession("session-a", "Player A");
        JoinAcceptedMessage accepted = host.JoinSession("session-b", "Player B");

        Assert.Collection(
            accepted.EffectiveRoster.OrderBy(entry => entry.SessionId),
            first =>
            {
                Assert.Equal("session-a", first.SessionId);
                Assert.Equal("Player A", first.DisplayName);
            },
            second =>
            {
                Assert.Equal("session-b", second.SessionId);
                Assert.Equal("Player B", second.DisplayName);
            });
    }
}
