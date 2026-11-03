using Xunit;
using Dream.Client.Input;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Players;

namespace Dream.Tests.Client.Input;


public class PlayerCommandOneShotInputFilterTests
{
    [Fact]
    public void Filter_EmitsNoclipToggleOnlyOnceWhileKeyIsHeld()
    {
        PlayerCommandOneShotInputFilter filter = new();

        PlayerCommandFrame first = filter.Filter(CreateFrame(sequence: 1, toggleNoclipPressed: true));
        PlayerCommandFrame held = filter.Filter(CreateFrame(sequence: 2, toggleNoclipPressed: true));

        Assert.True(first.MoveIntent.ToggleNoclipPressed);
        Assert.False(held.MoveIntent.ToggleNoclipPressed);
    }

    [Fact]
    public void Filter_EmitsNoclipToggleAgainAfterRelease()
    {
        PlayerCommandOneShotInputFilter filter = new();

        _ = filter.Filter(CreateFrame(sequence: 1, toggleNoclipPressed: true));
        _ = filter.Filter(CreateFrame(sequence: 2, toggleNoclipPressed: false));
        PlayerCommandFrame pressedAgain = filter.Filter(CreateFrame(sequence: 3, toggleNoclipPressed: true));

        Assert.True(pressedAgain.MoveIntent.ToggleNoclipPressed);
    }

    [Fact]
    public void Filter_DropsNoclipAndFreezeToggles_WhenDevTogglesAreNotAllowed()
    {
        PlayerCommandOneShotInputFilter filter = new() { AllowDevToggles = false };
        PlayerCommandFrame frame = CreateFrame(sequence: 1, toggleNoclipPressed: true);
        frame = frame with { MoveIntent = frame.MoveIntent with { FreezePressed = true, JumpPressed = true } };

        PlayerCommandFrame filtered = filter.Filter(frame);

        Assert.False(filtered.MoveIntent.ToggleNoclipPressed);
        Assert.False(filtered.MoveIntent.FreezePressed);
        Assert.True(filtered.MoveIntent.JumpPressed);
    }

    [Fact]
    public void Filter_PreservesContinuousMovementLookAndPrimaryUse()
    {
        PlayerCommandOneShotInputFilter filter = new();

        PlayerCommandFrame filtered = filter.Filter(new PlayerCommandFrame(
            new PlayerSessionId("session-a"),
            Sequence: 9,
            new PlayerModeSwitchIntent(PlayerInteractionMode.Combat),
            new PlayerMoveIntent(
                ForwardAxis: 1f,
                StrafeAxis: -1f,
                VerticalAxis: 1f,
                JumpPressed: true,
                FreezePressed: true,
                ToggleNoclipPressed: true),
            new PlayerLookIntent(YawDelta: 4f, PitchDelta: -2f),
            PrimaryUsePressed: true));

        Assert.Equal(1f, filtered.MoveIntent.ForwardAxis);
        Assert.Equal(-1f, filtered.MoveIntent.StrafeAxis);
        Assert.Equal(1f, filtered.MoveIntent.VerticalAxis);
        Assert.True(filtered.MoveIntent.JumpPressed);
        Assert.True(filtered.MoveIntent.FreezePressed);
        Assert.True(filtered.MoveIntent.ToggleNoclipPressed);
        Assert.Equal(4f, filtered.LookIntent.YawDelta);
        Assert.Equal(-2f, filtered.LookIntent.PitchDelta);
        Assert.True(filtered.PrimaryUsePressed);
        Assert.Equal(PlayerInteractionMode.Combat, filtered.RequestedMode.Mode);
    }

    [Fact]
    public void Filter_DropsFreezeAndNoclipOnlyWhileDevTogglesAreDisallowed()
    {
        PlayerCommandOneShotInputFilter filter = new() { AllowDevToggles = false };
        PlayerCommandFrame frame = new(
            new PlayerSessionId("session-a"),
            1,
            PlayerModeSwitchIntent.None,
            new PlayerMoveIntent(1f, 0f, 0f, JumpPressed: true, FreezePressed: true, ToggleNoclipPressed: true),
            PlayerLookIntent.None,
            PrimaryUsePressed: false);

        PlayerCommandFrame blocked = filter.Filter(frame);

        Assert.False(blocked.MoveIntent.FreezePressed);
        Assert.False(blocked.MoveIntent.ToggleNoclipPressed);
        Assert.Equal(1f, blocked.MoveIntent.ForwardAxis);

        filter.AllowDevToggles = true;
        _ = filter.Filter(CreateFrame(sequence: 2, toggleNoclipPressed: false));
        PlayerCommandFrame allowed = filter.Filter(frame with { Sequence = 3 });

        Assert.True(allowed.MoveIntent.FreezePressed);
        Assert.True(allowed.MoveIntent.ToggleNoclipPressed);
    }

    private static PlayerCommandFrame CreateFrame(ulong sequence, bool toggleNoclipPressed)
    {
        return new PlayerCommandFrame(
            new PlayerSessionId("session-a"),
            sequence,
            PlayerModeSwitchIntent.None,
            new PlayerMoveIntent(
                ForwardAxis: 1f,
                StrafeAxis: 0f,
                VerticalAxis: 0f,
                JumpPressed: false,
                FreezePressed: false,
                ToggleNoclipPressed: toggleNoclipPressed),
            PlayerLookIntent.None,
            PrimaryUsePressed: false);
    }
}
