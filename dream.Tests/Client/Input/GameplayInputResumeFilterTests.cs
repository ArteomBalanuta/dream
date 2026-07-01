using Xunit;
using Dream.Client.Input;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Players;

namespace Dream.Tests.Client.Input;


public class GameplayInputResumeFilterTests
{
    [Fact]
    public void Filter_SuppressesOnlyFirstLookDeltaAfterGameplayInputWasBlocked()
    {
        GameplayInputResumeFilter filter = new();

        _ = filter.Filter(CreateFrame(sequence: 1, yawDelta: 0f, forwardAxis: 0f), allowGameplayInput: false);
        PlayerCommandFrame resumed = filter.Filter(CreateFrame(sequence: 2, yawDelta: 9f, forwardAxis: 1f), allowGameplayInput: true);

        Assert.Equal(PlayerLookIntent.None, resumed.LookIntent);
        Assert.Equal(1f, resumed.MoveIntent.ForwardAxis);
    }

    [Fact]
    public void Filter_PassesLookDeltaOnFollowingGameplayFrames()
    {
        GameplayInputResumeFilter filter = new();

        _ = filter.Filter(CreateFrame(sequence: 1, yawDelta: 0f, forwardAxis: 0f), allowGameplayInput: false);
        _ = filter.Filter(CreateFrame(sequence: 2, yawDelta: 9f, forwardAxis: 1f), allowGameplayInput: true);
        PlayerCommandFrame next = filter.Filter(CreateFrame(sequence: 3, yawDelta: 2.5f, forwardAxis: 1f), allowGameplayInput: true);

        Assert.Equal(2.5f, next.LookIntent.YawDelta);
        Assert.Equal(1f, next.MoveIntent.ForwardAxis);
    }

    [Fact]
    public void Filter_SuppressesOneShotInputsOnFirstGameplayFrameAfterInputWasBlocked()
    {
        GameplayInputResumeFilter filter = new();

        _ = filter.Filter(CreateFrame(sequence: 1, yawDelta: 0f, forwardAxis: 0f), allowGameplayInput: false);
        PlayerCommandFrame resumed = filter.Filter(
            CreateFrame(
                sequence: 2,
                yawDelta: 0f,
                forwardAxis: 1f,
                jumpPressed: true,
                freezePressed: true,
                toggleNoclipPressed: true,
                primaryUsePressed: true),
            allowGameplayInput: true);

        Assert.False(resumed.MoveIntent.JumpPressed);
        Assert.False(resumed.MoveIntent.FreezePressed);
        Assert.False(resumed.MoveIntent.ToggleNoclipPressed);
        Assert.False(resumed.PrimaryUsePressed);
        Assert.Equal(1f, resumed.MoveIntent.ForwardAxis);
    }

    [Fact]
    public void Filter_DoesNotSuppressInitialGameplayLookDelta()
    {
        GameplayInputResumeFilter filter = new();

        PlayerCommandFrame first = filter.Filter(CreateFrame(sequence: 1, yawDelta: 3f, forwardAxis: 1f), allowGameplayInput: true);

        Assert.Equal(3f, first.LookIntent.YawDelta);
    }

    private static PlayerCommandFrame CreateFrame(
        ulong sequence,
        float yawDelta,
        float forwardAxis,
        bool jumpPressed = false,
        bool freezePressed = false,
        bool toggleNoclipPressed = false,
        bool primaryUsePressed = false)
    {
        return new PlayerCommandFrame(
            new PlayerSessionId("session-a"),
            sequence,
            PlayerModeSwitchIntent.None,
            new PlayerMoveIntent(
                ForwardAxis: forwardAxis,
                StrafeAxis: 0f,
                VerticalAxis: 0f,
                JumpPressed: jumpPressed,
                FreezePressed: freezePressed,
                ToggleNoclipPressed: toggleNoclipPressed),
            new PlayerLookIntent(YawDelta: yawDelta, PitchDelta: 0f),
            PrimaryUsePressed: primaryUsePressed);
    }
}
