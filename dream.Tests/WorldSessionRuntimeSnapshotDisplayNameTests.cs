using OpenTK.Mathematics;
using Xunit;

public class WorldSessionRuntimeSnapshotDisplayNameTests
{
    [Fact]
    public void ExportServerSnapshotEnvelope_IncludesRemotePlayerAndWolfDisplayNames()
    {
        using WorldSessionRuntime runtime = new(
            floorSize: 10,
            playerStartPosition: new Vector3(5f, 1.5f, 5f),
            new SceneEventHub(),
            loadVisualAssets: false);
        runtime.AttachRemoteSession(new PlayerSessionId("session-a"), "Player A");

        ServerSnapshotEnvelope snapshot = runtime.ExportServerSnapshotEnvelope();

        ActorSnapshot remotePlayer = Assert.Single(snapshot.Actors, actor => actor.Kind == CreatureKind.Player && actor.SessionId == "session-a");
        ActorSnapshot wolf = Assert.Single(snapshot.Actors, actor => actor.Kind == CreatureKind.Enemy);

        Assert.Equal("Player A", remotePlayer.DisplayName);
        Assert.Equal("Wolf", wolf.DisplayName);
        Assert.Equal(100, wolf.MaxHealth);
    }
}
