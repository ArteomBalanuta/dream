using Dream.Simulation.Players;
using Xunit;

namespace Dream.Tests.Simulation.Players;

// The player speeds were slowed by fixed factors. The old speeds live here and nowhere else, so
// the relation is what is pinned rather than a number restated in a comment.
public sealed class PlayerSpeedRatioTests
{
    private const float OldWalk = 1.6f;
    private const float OldRun = 4.3f;
    private const float OldSprint = 6.0f;

    [Fact]
    public void TheAnalogWalkFloor_IsTwoAndAHalfTimesSlower()
    {
        Assert.InRange(OldWalk / PlayerSimulationTuning.DefaultWalkSpeed, 2.5f - 1e-4f, 2.5f + 1e-4f);
    }

    [Fact]
    public void TheDefaultCruise_IsTwoAndAHalfTimesSlower()
    {
        Assert.InRange(OldRun / PlayerSimulationTuning.DefaultRunSpeed, 2.5f - 1e-4f, 2.5f + 1e-4f);
    }

    [Fact]
    public void TheSprint_IsOneAndAHalfTimesSlower()
    {
        Assert.InRange(OldSprint / PlayerSimulationTuning.DefaultSprintSpeed, 1.5f - 1e-4f, 1.5f + 1e-4f);
    }

    [Fact]
    public void TheTiersStayInOrder_AndTheAuthoritativeCruiseIsTheDefault()
    {
        Assert.True(PlayerSimulationTuning.DefaultWalkSpeed < PlayerSimulationTuning.DefaultRunSpeed);
        Assert.True(PlayerSimulationTuning.DefaultRunSpeed < PlayerSimulationTuning.DefaultSprintSpeed);
        Assert.Equal(PlayerSimulationTuning.DefaultRunSpeed, PlayerSimulationTuning.AuthoritativeDefault.MoveSpeed);
        Assert.Equal(PlayerSimulationTuning.DefaultSprintSpeed, PlayerSimulationTuning.AuthoritativeDefault.SprintSpeed);
    }
}
