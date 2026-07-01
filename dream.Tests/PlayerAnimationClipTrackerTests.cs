using Xunit;

public class PlayerAnimationClipTrackerTests
{
    [Fact]
    public void TryApplyClip_TracksActiveClipPerActor()
    {
        PlayerAnimationClipTracker tracker = new();
        List<string> playedClips = [];

        bool firstActorStartedWalking = tracker.TryApplyClip(
            actorId: 1,
            PlayerModelAssetDefinition.WalkClip,
            clipName => playedClips.Add($"1:{clipName}"));
        bool secondActorStayedIdle = tracker.TryApplyClip(
            actorId: 2,
            PlayerModelAssetDefinition.IdleClip,
            clipName => playedClips.Add($"2:{clipName}"));
        bool firstActorRepeatedWalk = tracker.TryApplyClip(
            actorId: 1,
            PlayerModelAssetDefinition.WalkClip,
            clipName => playedClips.Add($"1:{clipName}"));
        bool secondActorStartedWalking = tracker.TryApplyClip(
            actorId: 2,
            PlayerModelAssetDefinition.WalkClip,
            clipName => playedClips.Add($"2:{clipName}"));

        Assert.True(firstActorStartedWalking);
        Assert.True(secondActorStayedIdle);
        Assert.False(firstActorRepeatedWalk);
        Assert.True(secondActorStartedWalking);
        Assert.Equal(
            [
                "1:walk",
                "2:idle",
                "2:walk"
            ],
            playedClips);
    }
}
