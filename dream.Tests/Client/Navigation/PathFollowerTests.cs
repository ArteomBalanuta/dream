using Dream.Client.Navigation;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Client.Navigation;

public class PathFollowerTests
{
    private static readonly Vector3 Start = new(2.5f, 1.5f, 2.5f);

    [Fact]
    public void HeadsForTheNextCentre_AndArrivesAtTheEnd()
    {
        GridPathfinderTests.FakeWorld world = new();
        PathFollower follower = new();
        follower.SetPath([new NavCell(2, 1, 2), new NavCell(3, 1, 2)], viaStartCentre: false);

        NavStep step = follower.Step(Start, world);
        Assert.Equal(new Vector3(1f, 0f, 0f), step.Direction);
        Assert.False(step.Jump);
        Assert.False(step.Arrived);

        step = follower.Step(new Vector3(3.5f, 1.5f, 2.5f), world);
        Assert.True(step.Arrived);
        Assert.False(follower.HasPath);
    }

    [Fact]
    public void StartCentreIsSkippedUnlessViaStartCentre()
    {
        GridPathfinderTests.FakeWorld world = new();
        NavCell[] path = [new NavCell(2, 1, 2), new NavCell(3, 1, 2)];
        PathFollower skip = new();
        skip.SetPath(path, viaStartCentre: false);
        PathFollower include = new();
        include.SetPath(path, viaStartCentre: true);
        Vector3 offCentre = new(2.8f, 1.5f, 2.5f);

        Assert.True(skip.Step(offCentre, world).Direction.X > 0f);
        Assert.True(include.Step(offCentre, world).Direction.X < 0f);
    }

    [Fact]
    public void JumpPulsesOnceThenWaitsForTheCooldown()
    {
        GridPathfinderTests.FakeWorld world = new();
        world.Set(4, 1, 2);
        PathFollower follower = new();
        follower.SetPath([new NavCell(3, 1, 2), new NavCell(4, 2, 2)], viaStartCentre: false);
        Vector3 body = new(3.6f, 1.5f, 2.5f);

        // The body keeps moving: a pinned body hops instead.
        Assert.True(follower.Step(body, world).Jump);
        for (int i = 0; i < PathFollower.JumpCooldownTicks; i++)
        {
            body.Z += 0.001f;
            body.X -= 0.05f;
            Assert.False(follower.Step(body, world).Jump);
        }

        Assert.True(follower.Step(body, world).Jump);
    }

    [Fact]
    public void RequestsReplan_WhenTheNextCellStopsBeingStandable()
    {
        GridPathfinderTests.FakeWorld world = new();
        PathFollower follower = new();
        follower.SetPath([new NavCell(2, 1, 2), new NavCell(3, 1, 2), new NavCell(4, 1, 2)], viaStartCentre: false);
        Assert.False(follower.Step(Start, world).NeedsReplan);

        world.Set(3, 1, 2);

        Assert.True(follower.Step(Start, world).NeedsReplan);
    }

    [Fact]
    public void HoppingInPlaceBelowATwoLevelTarget_StillEndsInAReplanOrGiveUp()
    {
        GridPathfinderTests.FakeWorld world = new();
        world.Set(4, 1, 2);
        world.Set(4, 2, 2);
        PathFollower follower = new();
        follower.SetPath([new NavCell(3, 1, 2), new NavCell(4, 3, 2)], viaStartCentre: false);
        Vector3 body = new(3.6f, 1.5f, 2.5f);

        bool ended = false;
        for (int tick = 0; tick < PathFollower.HopTicks * 3 + PathFollower.StuckWindowTicks * 6 && !ended; tick++)
        {
            NavStep step = follower.Step(body, world);
            ended = step.NeedsReplan || step.Arrived;
        }

        Assert.True(ended);
    }

    [Fact]
    public void Stuck_JumpsFirst_ThenReplans_AndGivesUpAfterThree()
    {
        GridPathfinderTests.FakeWorld world = new();
        PathFollower follower = new();
        NavCell[] path = [new NavCell(2, 1, 2), new NavCell(6, 1, 2)];
        follower.SetPath(path, viaStartCentre: false);

        int replans = 0;
        bool jumpedBeforeReplan = false;
        bool gaveUp = false;
        for (int tick = 0; tick < PathFollower.StuckWindowTicks * 20 && !gaveUp; tick++)
        {
            NavStep step = follower.Step(Start, world);
            if (replans == 0 && step.Jump && tick > 0)
            {
                jumpedBeforeReplan = true;
            }

            if (step.NeedsReplan)
            {
                replans++;
                Assert.True(jumpedBeforeReplan);
                follower.SetPath(path, viaStartCentre: true);
            }

            gaveUp = step.Arrived;
        }

        Assert.True(gaveUp);
        Assert.True(follower.GaveUp);
        Assert.Equal(PathFollower.MaxFailedReplans, replans);
        Assert.False(follower.HasPath);
    }

    [Fact]
    public void WalkingBackBetweenStuckReplans_KeepsTheFailures_ButANormalPathClearsThem()
    {
        GridPathfinderTests.FakeWorld world = new();
        PathFollower follower = new();
        NavCell[] path = [new NavCell(2, 1, 2), new NavCell(9, 1, 2)];
        follower.SetPath(path, viaStartCentre: false);

        // Each cycle shuffles 0.2 m, then pins for the jump and replan windows.
        int replans = 0;
        Vector3 body = Start;
        for (int tick = 0; tick < PathFollower.StuckWindowTicks * 60 && !follower.GaveUp; tick++)
        {
            if (tick % (PathFollower.StuckWindowTicks * 3) == 0)
            {
                body.X = replans % 2 == 0 ? Start.X + 0.2f : Start.X;
            }

            if (follower.Step(body, world).NeedsReplan)
            {
                replans++;
                follower.SetPath(path, viaStartCentre: true);
            }
        }

        Assert.True(follower.GaveUp);
        Assert.Equal(PathFollower.MaxFailedReplans, replans);

        follower.SetPath(path, viaStartCentre: false);
        Assert.False(follower.GaveUp);
    }

    [Fact]
    public void Progress_ResetsTheStuckCount()
    {
        GridPathfinderTests.FakeWorld world = new();
        PathFollower follower = new();
        follower.SetPath([new NavCell(2, 1, 2), new NavCell(9, 1, 2)], viaStartCentre: false);
        Vector3 body = Start;

        for (int tick = 0; tick < PathFollower.StuckWindowTicks * 10; tick++)
        {
            body.X += 0.01f;
            NavStep step = follower.Step(body, world);
            Assert.False(step.NeedsReplan);
            Assert.False(step.Arrived);
        }
    }
}
