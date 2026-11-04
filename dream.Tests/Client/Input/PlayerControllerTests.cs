using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Xunit;
using Dream.Client.Input;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Players;

namespace Dream.Tests.Client.Input;


public class PlayerControllerTests
{
    [Fact]
    public void CreateCommandFrame_BindsSessionSequenceAndGameplayIntents()
    {
        PlayerCommandFrame frame = PlayerController.CreateCommandFrame(
            sessionId: new PlayerSessionId("local-player-1"),
            sequence: 12,
            allowGameplayInput: true,
            forwardAxis: 1f,
            strafeAxis: -1f,
            verticalAxis: 0f,
            jumpPressed: true,
            freezePressed: false,
            toggleNoclipPressed: true,
            yawDelta: 4f,
            pitchDelta: -3f,
            primaryUsePressed: true,
            selectBuildingModePressed: false,
            selectCombatModePressed: true);

        Assert.Equal("local-player-1", frame.SessionId.Value);
        Assert.Equal((ulong)12, frame.Sequence);
        Assert.Equal(1f, frame.MoveIntent.ForwardAxis);
        Assert.Equal(-1f, frame.MoveIntent.StrafeAxis);
        Assert.True(frame.MoveIntent.JumpPressed);
        Assert.True(frame.MoveIntent.ToggleNoclipPressed);
        Assert.Equal(4f, frame.LookIntent.YawDelta);
        Assert.Equal(-3f, frame.LookIntent.PitchDelta);
        Assert.True(frame.PrimaryUsePressed);
        Assert.Equal(PlayerInteractionMode.Combat, frame.RequestedMode.Mode);
    }

    [Fact]
    public void CreateCommandFrame_WhenGameplayInputBlocked_ProducesNeutralIntents()
    {
        PlayerCommandFrame frame = PlayerController.CreateCommandFrame(
            sessionId: new PlayerSessionId("local-player-1"),
            sequence: 5,
            allowGameplayInput: false,
            forwardAxis: 1f,
            strafeAxis: 1f,
            verticalAxis: 1f,
            jumpPressed: true,
            freezePressed: true,
            toggleNoclipPressed: true,
            yawDelta: 8f,
            pitchDelta: 2f,
            primaryUsePressed: true,
            selectBuildingModePressed: true,
            selectCombatModePressed: true);

        Assert.Equal(0f, frame.MoveIntent.ForwardAxis);
        Assert.Equal(0f, frame.MoveIntent.StrafeAxis);
        Assert.Equal(0f, frame.MoveIntent.VerticalAxis);
        Assert.False(frame.MoveIntent.JumpPressed);
        Assert.False(frame.MoveIntent.FreezePressed);
        Assert.False(frame.MoveIntent.ToggleNoclipPressed);
        Assert.Equal(0f, frame.LookIntent.YawDelta);
        Assert.Equal(0f, frame.LookIntent.PitchDelta);
        Assert.False(frame.PrimaryUsePressed);
        Assert.Null(frame.RequestedMode.Mode);
    }

    private static PlayerCommandFrame Held(params Keys[] keys) =>
        PlayerController.CaptureCommandFrame(new PlayerSessionId("local-player-1"), 1, key => Array.IndexOf(keys, key) >= 0, allowGameplayInput: true);

    private static (PlayerCommandFrame Frame, Vector2 Axes) HeldWithAxes(bool primary, params Keys[] keys)
    {
        PlayerCommandFrame frame = PlayerController.CaptureCommandFrame(
            new PlayerSessionId("local-player-1"), 1, key => Array.IndexOf(keys, key) >= 0, true, primary, out Vector2 axes);
        return (frame, axes);
    }

    private static Vector3 Flat(float degrees) =>
        new(MathF.Cos(MathHelper.DegreesToRadians(degrees)), 0f, MathF.Sin(MathHelper.DegreesToRadians(degrees)));

    [Fact]
    public void CaptureCommandFrame_WasdStayOutOfTheFrameAxes_AndComeBackAsCameraSpace()
    {
        (PlayerCommandFrame frame, Vector2 axes) = HeldWithAxes(false, Keys.W, Keys.D);

        Assert.Equal(0f, frame.MoveIntent.ForwardAxis);
        Assert.Equal(0f, frame.MoveIntent.StrafeAxis);
        Assert.Equal(new Vector2(1f, 1f), axes);
        Assert.Equal(new Vector2(-1f, 0f), HeldWithAxes(false, Keys.A).Axes);
        Assert.Equal(new Vector2(0f, -1f), HeldWithAxes(false, Keys.S).Axes);
        Assert.Equal(Vector2.Zero, HeldWithAxes(false, Keys.W, Keys.S).Axes);
        // The legacy overload never reads them.
        Assert.Equal(0f, Held(Keys.W, Keys.A).MoveIntent.ForwardAxis);
    }

    [Fact]
    public void CaptureCommandFrame_PrimaryRidesTheRawFrame_ButNotWhenGameplayInputIsBlocked()
    {
        Assert.True(HeldWithAxes(true).Frame.PrimaryUsePressed);
        Assert.False(HeldWithAxes(false).Frame.PrimaryUsePressed);

        PlayerCommandFrame blocked = PlayerController.CaptureCommandFrame(
            new PlayerSessionId("local-player-1"), 1, _ => true, false, true, out Vector2 axes);
        Assert.False(blocked.PrimaryUsePressed);
        Assert.Equal(Vector2.Zero, axes);
    }

    [Fact]
    public void Project_WIsUpScreen_WhateverTheFacing()
    {
        // Screen-up is the camera's ground-plane forward, (-1, 0, -1) normalised.
        Vector3 screenUp = new(-0.70710677f, 0f, -0.70710677f);
        foreach (float degrees in new[] { 0f, 37f, 90f, 180f, 250f })
        {
            Vector3 facing = Flat(degrees);
            (float forward, float strafe) = PlayerController.Project(new Vector2(0f, 1f), facing);
            Vector3 right = Vector3.Cross(facing, Vector3.UnitY);
            Vector3 moved = (facing * forward) + (right * strafe);
            Assert.Equal(screenUp.X, moved.X, 4);
            Assert.Equal(screenUp.Z, moved.Z, 4);
        }
    }

    [Fact]
    public void Project_DDirectionIsScreenRight()
    {
        Vector3 screenRight = new(0.70710677f, 0f, -0.70710677f);
        (float forward, float strafe) = PlayerController.Project(new Vector2(1f, 0f), Flat(123f));
        Vector3 facing = Flat(123f);
        Vector3 moved = (facing * forward) + (Vector3.Cross(facing, Vector3.UnitY) * strafe);

        Assert.Equal(screenRight.X, moved.X, 4);
        Assert.Equal(screenRight.Z, moved.Z, 4);
    }

    [Fact]
    public void Project_Diagonal_IsUnitLength_AtEveryFacing()
    {
        foreach (float degrees in new[] { 0f, 45f, 90f, 135f, 200f, 300f })
        {
            (float forward, float strafe) = PlayerController.Project(new Vector2(-1f, 1f), Flat(degrees));
            Assert.Equal(1f, MathF.Sqrt((forward * forward) + (strafe * strafe)), 4);
        }
    }

    [Fact]
    public void Project_AWithTheFacing90DegreesOff_IsAPureStrafe()
    {
        // A is screen-left (-0.707, 0, 0.707). Facing it rotated 90 degrees puts that along the strafe axis.
        Vector3 screenLeft = new(-0.70710677f, 0f, 0.70710677f);
        Vector3 facing = Vector3.Cross(Vector3.UnitY, screenLeft); // 90 degrees from screen-left, on the ground
        (float forward, float strafe) = PlayerController.Project(new Vector2(-1f, 0f), facing);

        Assert.Equal(0f, forward, 4);
        Assert.Equal(1f, MathF.Abs(strafe), 4);
    }

    [Fact]
    public void Project_AWithTheFacingToTheRight_MovesLeftAwayFromTheFacing()
    {
        // Facing screen-right, press A: the body travels screen-left, i.e. straight backwards.
        Vector3 screenRight = new(0.70710677f, 0f, -0.70710677f);
        (float forward, float strafe) = PlayerController.Project(new Vector2(-1f, 0f), screenRight);

        Assert.Equal(-1f, forward, 4);
        Assert.Equal(0f, strafe, 4);
    }

    [Fact]
    public void Project_IgnoresThePitchOfTheFacing()
    {
        Vector3 flat = Flat(70f);
        Vector3 pitched = Vector3.Normalize(new Vector3(flat.X, -0.8f, flat.Z));

        (float f1, float s1) = PlayerController.Project(new Vector2(1f, 1f), flat);
        (float f2, float s2) = PlayerController.Project(new Vector2(1f, 1f), pitched);

        Assert.Equal(f1, f2, 4);
        Assert.Equal(s1, s2, 4);
    }

    [Fact]
    public void Project_NoInput_IsZero()
    {
        Assert.Equal((0f, 0f), PlayerController.Project(Vector2.Zero, Flat(10f)));
    }

    [Fact]
    public void CaptureCommandFrame_SpaceJumpsAndRisesInNoclip_ControlDescends()
    {
        PlayerCommandFrame jump = Held(Keys.Space);
        Assert.True(jump.MoveIntent.JumpPressed);
        Assert.Equal(1f, jump.MoveIntent.VerticalAxis);

        PlayerCommandFrame down = Held(Keys.LeftControl);
        Assert.Equal(-1f, down.MoveIntent.VerticalAxis);
        Assert.False(down.MoveIntent.JumpPressed);
    }

    [Fact]
    public void CaptureCommandFrame_ZeroTogglesNoclip_OneAndTwoPickTheMode()
    {
        Assert.True(Held(Keys.D0).MoveIntent.ToggleNoclipPressed);
        Assert.Equal(PlayerInteractionMode.Building, Held(Keys.D1).RequestedMode.Mode);
        Assert.Equal(PlayerInteractionMode.Combat, Held(Keys.D2).RequestedMode.Mode);
        Assert.Null(Held().RequestedMode.Mode);
    }

    [Fact]
    public void CaptureCommandFrame_BlockedInput_IsNeutral()
    {
        PlayerCommandFrame frame = PlayerController.CaptureCommandFrame(
            new PlayerSessionId("local-player-1"), 1, _ => true, allowGameplayInput: false);

        Assert.False(frame.MoveIntent.JumpPressed);
        Assert.False(frame.MoveIntent.ToggleNoclipPressed);
        Assert.Equal(0f, frame.MoveIntent.VerticalAxis);
        Assert.Null(frame.RequestedMode.Mode);
    }
}
