using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Presentation;
using Dream.Rendering.Actors;
using Dream.Rendering.Models;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using Dream.Tests.Utilities;

namespace Dream.Tests.Rendering.Actors;


public class PlayerRenderAllocationTests
{
    [Fact]
    public void Create_Shares_One_Read_Only_Head_Override_And_Allocates_Nothing()
    {
        RemotePlayerPresentationState remotePlayer = CreatePlayer(7, new Vector3(4f, 2f, 8f), isAlive: true);

        PlayerRenderState first = PlayerRenderState.CreateRemote(remotePlayer);
        PlayerRenderState second = PlayerRenderState.CreateLocal(Vector3.One, Vector3.UnitX, isMoving: true);

        Assert.Same(first.NodeOverrides, second.NodeOverrides);
        Assert.Equal(Matrix4.Identity, first.NodeOverrides[PlayerModelAssetDefinition.HeadNodeName]);
        IDictionary<string, Matrix4> mutable = Assert.IsAssignableFrom<IDictionary<string, Matrix4>>(first.NodeOverrides);
        Assert.Throws<NotSupportedException>(() => mutable[PlayerModelAssetDefinition.HeadNodeName] = Matrix4.Zero);

        Assert.Equal(0, AllocationAssert.AtMost(0, () => _ = PlayerRenderState.CreateRemote(remotePlayer)));
        Assert.Equal(0, AllocationAssert.AtMost(0, () => _ = PlayerRenderState.CreateLocal(Vector3.One, Vector3.UnitX, isMoving: false)));
    }

    [Fact]
    public void Remote_Bookkeeping_Allocates_Nothing_On_Repeat_Calls()
    {
        Camera camera = new(Vector3.Zero);
        List<RemotePlayerPresentationState> players = [];
        for (int i = 0; i < 6; i++)
        {
            players.Add(CreatePlayer(i + 1, new Vector3(i, 2f, -4f), isAlive: i % 2 == 0));
        }

        HashSet<int> activeActorIds = [];
        List<int> staleActorIds = [];
        List<RemotePlayerPresentationState> visible = [];
        Dictionary<int, object> cachedModels = new()
        {
            [1] = new object(),
            [2] = new object(),
            [99] = new object()
        };
        PlayerAnimationClipTracker tracker = new();
        Action<string> playClip = _ => { };
        for (int i = 1; i <= 6; i++)
        {
            tracker.TryApplyClip(i, PlayerModelAssetDefinition.IdleClip, playClip);
        }

        int alive = 0;
        long bytes = AllocationAssert.AtMost(0, () =>
        {
            RemotePlayerRenderer.CollectActiveActorIds(players, activeActorIds);
            tracker.RemoveExcept(activeActorIds);
            RemotePlayerRenderer.CollectStaleActorIds(cachedModels, activeActorIds, staleActorIds);
            RemotePlayerRenderer.SelectVisiblePlayers(camera, players, visible);
            alive = RemotePlayerRenderer.CountAlive(players);
            tracker.TryApplyClip(1, PlayerModelAssetDefinition.IdleClip, playClip);
        });

        Assert.Equal(0, bytes);
        Assert.Equal([1, 2, 3, 4, 5, 6], activeActorIds.Order());
        Assert.Equal([99], staleActorIds);
        Assert.Equal(6, visible.Count);
        Assert.Equal(3, alive);
    }

    [Fact]
    public void Clip_Tracker_RemoveExcept_Drops_Only_Inactive_Actors()
    {
        PlayerAnimationClipTracker tracker = new();
        List<string> played = [];
        Action<string> playClip = played.Add;
        tracker.TryApplyClip(1, PlayerModelAssetDefinition.IdleClip, playClip);
        tracker.TryApplyClip(2, PlayerModelAssetDefinition.IdleClip, playClip);
        tracker.TryApplyClip(3, PlayerModelAssetDefinition.IdleClip, playClip);

        tracker.RemoveExcept(new HashSet<int> { 2 });

        Assert.True(tracker.TryApplyClip(1, PlayerModelAssetDefinition.IdleClip, playClip));
        Assert.False(tracker.TryApplyClip(2, PlayerModelAssetDefinition.IdleClip, playClip));
        Assert.True(tracker.TryApplyClip(3, PlayerModelAssetDefinition.IdleClip, playClip));
        Assert.Equal(5, played.Count);
    }

    [Fact]
    public void SelectVisiblePlayers_Reused_List_Matches_Allocating_Overload()
    {
        Camera camera = new(new Vector3(0f, 2f, 0f));
        List<RemotePlayerPresentationState> players =
        [
            CreatePlayer(1, new Vector3(0f, 2f, -4f), isAlive: true),
            CreatePlayer(2, new Vector3(0f, 2f, WorldVisibility.MaxRenderDistance + 10f), isAlive: true),
            CreatePlayer(3, new Vector3(0f, 2f, 4f), isAlive: false)
        ];
        List<RemotePlayerPresentationState> reused = [CreatePlayer(42, Vector3.Zero, isAlive: true)];

        RemotePlayerRenderer.SelectVisiblePlayers(camera, players, reused);

        Assert.Equal(RemotePlayerRenderer.SelectVisiblePlayers(camera, players), reused);
        Assert.Equal([1, 3], reused.Select(player => player.ActorId));
    }

    private static RemotePlayerPresentationState CreatePlayer(int actorId, Vector3 position, bool isAlive)
    {
        return new RemotePlayerPresentationState(
            actorId,
            $"session-{actorId}",
            $"Player {actorId}",
            new InterpolatedActorPose(position, -Vector3.UnitZ),
            isAlive ? 100 : 0,
            100,
            isAlive);
    }
}
