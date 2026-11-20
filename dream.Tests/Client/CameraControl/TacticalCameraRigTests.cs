using Dream.Client.CameraControl;
using Dream.Client.Presentation;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Client.CameraControl;

public class TacticalCameraRigTests
{
    private static readonly Vector3 Focus = new(10f, 1.5f, -6f);

    private static CameraInputFrame Input(
        Vector2 edge = default,
        Vector2 key = default,
        float drag = 0f,
        bool dragActive = false,
        bool dragReleased = false,
        int steps = 0,
        float notches = 0f,
        bool recentre = false,
        bool toggle = false) =>
        new(edge, key, drag, dragActive, dragReleased, steps, notches, recentre, toggle);

    private static TacticalCameraRig NewRig(TacticalCameraSettings? settings = null)
    {
        TacticalCameraRig rig = new(settings ?? TacticalCameraSettings.Default);
        rig.Reset(Focus);
        return rig;
    }

    private static void Run(TacticalCameraRig rig, Vector3 focus, CameraInputFrame input, float seconds, float dt)
    {
        int frames = (int)MathF.Round(seconds / dt);
        for (int i = 0; i < frames; i++)
        {
            rig.Advance(focus, input, dt);
        }
    }

    private static void Settle(TacticalCameraRig rig, Vector3 focus, float seconds = 3f) =>
        Run(rig, focus, CameraInputFrame.None, seconds, 1f / 60f);

    private static void AssertPosesClose(TacticalCameraPose a, TacticalCameraPose b, float tolerance)
    {
        Assert.InRange((a.Target - b.Target).Length, 0f, tolerance);
        Assert.InRange(MathF.Abs(a.YawDegrees - b.YawDegrees), 0f, tolerance);
        Assert.InRange(MathF.Abs(a.Distance - b.Distance), 0f, tolerance);
    }

    [Fact]
    public void BeforeAnyAdvanceThePoseIsTheDefaultAtTheOrigin()
    {
        TacticalCameraRig rig = new(TacticalCameraSettings.Default);
        Assert.Equal(TacticalCameraPose.Default(Vector3.Zero), rig.Pose);
    }

    [Fact]
    public void NoInputGivesExactlyTheDefaultPoseWithNoDrift()
    {
        TacticalCameraRig rig = new(TacticalCameraSettings.Default);
        Vector3 focus = new(3.3f, 0.7f, 9.1f);
        for (int i = 0; i < 600; i++)
        {
            TacticalCameraPose pose = rig.Advance(focus, CameraInputFrame.None, 1f / 60f);
            Assert.Equal(TacticalCameraPose.Default(focus), pose);
        }
    }

    [Fact]
    public void ZeroDeltaRebuildsThePoseFromTheFocusWithoutIntegrating()
    {
        TacticalCameraRig rig = NewRig();
        Vector3 moved = Focus + new Vector3(2f, 0f, 3f);
        TacticalCameraPose pose = rig.Advance(moved, Input(edge: new Vector2(1f, 0f), notches: 2f, drag: 50f), 0f);
        Assert.Equal(moved, pose.Target);
        Assert.Equal(IsometricView.YawDegrees, pose.YawDegrees);
        Assert.Equal(IsometricView.Distance, pose.Distance);
    }

    [Theory]
    [InlineData(30f)]
    [InlineData(60f)]
    [InlineData(144f)]
    [InlineData(240f)]
    public void SameInputsGiveTheSamePoseAtAnyFrameRate(float fps)
    {
        CameraInputFrame pan = Input(edge: new Vector2(0.6f, 0.8f));
        TacticalCameraRig reference = NewRig();
        TacticalCameraRig rig = NewRig();
        Run(reference, Focus, pan, 0.5f, 1f / 600f);
        Run(rig, Focus, pan, 0.5f, 1f / fps);
        Run(reference, Focus, CameraInputFrame.None, 0.5f, 1f / 600f);
        Run(rig, Focus, CameraInputFrame.None, 0.5f, 1f / fps);
        AssertPosesClose(reference.Pose, rig.Pose, 1e-3f);

        // Rotation and zoom: one impulse, then settle for the rest of the second.
        foreach (CameraInputFrame impulse in new[] { Input(drag: 100f), Input(notches: 3f) })
        {
            TacticalCameraRig a = NewRig();
            TacticalCameraRig b = NewRig();
            a.Advance(Focus, impulse, 1f / 600f);
            b.Advance(Focus, impulse, 1f / fps);
            Run(a, Focus, CameraInputFrame.None, 1f - 1f / 600f, 1f / 600f);
            Run(b, Focus, CameraInputFrame.None, 1f - 1f / fps, 1f / fps);
            AssertPosesClose(a.Pose, b.Pose, 1e-3f);
        }
    }

    [Fact]
    public void DeltaTimeIsClampedAndNonFiniteDeltaDoesNothing()
    {
        CameraInputFrame pan = Input(edge: new Vector2(1f, 0f));
        TacticalCameraRig a = NewRig();
        TacticalCameraRig b = NewRig();
        TacticalCameraRig c = NewRig();
        a.Advance(Focus, pan, 5f);
        b.Advance(Focus, pan, 0.1f);
        c.Advance(Focus, pan, float.NaN);
        Assert.Equal(b.Pose, a.Pose);
        Assert.Equal(Focus, c.Pose.Target);
        Assert.True(b.PanOffset.Length > 0f);
    }

    [Fact]
    public void LeashHoldsUnderLongEdgePan()
    {
        TacticalCameraRig rig = NewRig();
        float leash = rig.Settings.LeashMetres;
        for (int i = 0; i < 600; i++)
        {
            rig.Advance(Focus, Input(edge: new Vector2(1f, 1f)), 1f / 60f);
            Assert.InRange(rig.PanOffset.Length, 0f, leash + 1e-3f);
        }

        Assert.InRange(rig.PanOffset.Length, leash - 1e-2f, leash + 1e-3f);
        Assert.Equal(0f, rig.PanOffset.Y);
        Assert.Equal(Focus.Y, rig.Pose.Target.Y);
    }

    [Theory]
    [InlineData(45f)]
    [InlineData(135f)]
    [InlineData(-90f)]
    public void PanFollowsTheGroundBasisAtAnyYaw(float yaw)
    {
        TacticalCameraRig rig = NewRig();
        rig.SnapTo(Focus, yaw, null, null, null);
        TacticalCameraPose basis = rig.Pose;

        Run(rig, Focus, Input(edge: new Vector2(0f, 1f)), 0.5f, 1f / 60f);
        Vector3 forwardMove = rig.PanOffset;
        Assert.True(forwardMove.Length > 1f);
        Assert.InRange(Vector3.Dot(forwardMove.Normalized(), basis.GroundForward), 0.9999f, 1.0001f);

        rig.SnapTo(Focus, yaw, null, Vector2.Zero, null);
        Run(rig, Focus, Input(edge: new Vector2(1f, 0f)), 0.5f, 1f / 60f);
        Vector3 rightMove = rig.PanOffset;
        Assert.InRange(Vector3.Dot(rightMove.Normalized(), basis.GroundRight), 0.9999f, 1.0001f);
    }

    [Fact]
    public void PanSpeedScalesWithDistance()
    {
        TacticalCameraSettings settings = TacticalCameraSettings.Default with { LeashMetres = 40f };
        TacticalCameraRig near = NewRig(settings);
        TacticalCameraRig far = NewRig(settings);
        near.SnapTo(Focus, null, 12f, null, null);
        far.SnapTo(Focus, null, 24f, null, null);
        Run(near, Focus, Input(edge: new Vector2(0f, 1f)), 0.6f, 1f / 60f);
        Run(far, Focus, Input(edge: new Vector2(0f, 1f)), 0.6f, 1f / 60f);
        Assert.InRange(far.PanOffset.Length / near.PanOffset.Length, 1.999f, 2.001f);
    }

    [Fact]
    public void YawTurnsTheShortestWay()
    {
        TacticalCameraRig rig = NewRig(TacticalCameraSettings.Default with { RotateSmoothingSeconds = 0f });
        rig.SnapTo(Focus, 170f, null, null, null);
        // 20 degrees across the seam, by drag: 20 / 0.3 px per degree.
        rig.Advance(Focus, Input(drag: 20f / 0.3f), 1f / 60f);
        Assert.InRange(rig.Pose.YawDegrees, -170.01f, -169.99f);

        // A half-way frame must sit between 170 and -170 through 180, never through 0.
        TacticalCameraRig smooth = NewRig(TacticalCameraSettings.Default with { RotateSmoothingSeconds = 0.5f });
        smooth.SnapTo(Focus, 170f, null, null, null);
        smooth.Advance(Focus, Input(drag: 20f / 0.3f), 0.05f);
        float yaw = smooth.Pose.YawDegrees;
        Assert.True(yaw > 170f || yaw < -170f, $"yaw {yaw} left the short arc");
    }

    [Fact]
    public void RotateStepsStayOnTheGridThroughTheDefaultYaw()
    {
        TacticalCameraRig rig = NewRig(TacticalCameraSettings.Default with { RotateSmoothingSeconds = 0f });
        rig.Advance(Focus, Input(steps: 1), 1f / 60f);
        Assert.Equal(135f, rig.Pose.YawDegrees);
        rig.Advance(Focus, Input(steps: 1), 1f / 60f);
        Assert.Equal(-135f, rig.Pose.YawDegrees);
        rig.Advance(Focus, Input(steps: -2), 1f / 60f);
        Assert.Equal(45f, rig.Pose.YawDegrees);
    }

    [Fact]
    public void ReleasingARotateDragSnapsToTheNearestGridAngle()
    {
        TacticalCameraSettings settings = TacticalCameraSettings.Default with { SnapOnDragRelease = true, RotateSmoothingSeconds = 0f };
        TacticalCameraRig rig = NewRig(settings);
        rig.Advance(Focus, Input(drag: 70f / 0.3f, dragActive: true), 1f / 60f);
        Assert.InRange(rig.Pose.YawDegrees, 114.9f, 115.1f);
        rig.Advance(Focus, Input(dragReleased: true), 1f / 60f);
        Assert.Equal(135f, rig.Pose.YawDegrees);

        TacticalCameraRig free = NewRig(TacticalCameraSettings.Default with { RotateSmoothingSeconds = 0f });
        free.Advance(Focus, Input(drag: 70f / 0.3f), 1f / 60f);
        free.Advance(Focus, Input(dragReleased: true), 1f / 60f);
        Assert.InRange(free.Pose.YawDegrees, 114.9f, 115.1f);
    }

    [Fact]
    public void ZoomClampsAtTheLimits()
    {
        TacticalCameraRig rig = NewRig();
        Run(rig, Focus, Input(notches: 1f), 3f, 1f / 60f);
        Assert.Equal(rig.Settings.MinDistance, rig.Pose.Distance);
        Run(rig, Focus, Input(notches: -1f), 6f, 1f / 60f);
        Assert.Equal(rig.Settings.MaxDistance, rig.Pose.Distance);

        TacticalCameraRig step = NewRig(TacticalCameraSettings.Default with { ZoomSmoothingSeconds = 0f });
        step.Advance(Focus, Input(notches: 1f), 1f / 60f);
        Assert.InRange(step.Pose.Distance, 18f / 1.15f - 1e-3f, 18f / 1.15f + 1e-3f);
    }

    [Fact]
    public void ZoomGestureStaysActiveUntilSettledAndHeld()
    {
        TacticalCameraRig rig = NewRig();
        Assert.False(rig.ZoomGestureActive);
        rig.LatchZoomAnchor(new Vector3(12f, 1.5f, -8f));
        rig.Advance(Focus, Input(notches: 2f), 1f / 60f);
        Assert.True(rig.ZoomGestureActive);

        // Distance still moving at 0.1 s.
        Run(rig, Focus, CameraInputFrame.None, 0.1f, 1f / 60f);
        Assert.True(rig.ZoomGestureActive);

        // Fully settled and 0.3 s elapsed well before 3 s.
        Run(rig, Focus, CameraInputFrame.None, 3f, 1f / 60f);
        Assert.False(rig.ZoomGestureActive);

        // The latched anchor was cleared: zooming now about the centre leaves the offset alone.
        Vector3 offset = rig.PanOffset;
        rig.Advance(Focus, Input(notches: 1f), 1f / 60f);
        Settle(rig, Focus);
        Assert.Equal(offset, rig.PanOffset);
    }

    [Fact]
    public void ZoomGestureWaitsForTheHoldTimeEvenWhenSnapped()
    {
        TacticalCameraRig rig = NewRig(TacticalCameraSettings.Default with { ZoomSmoothingSeconds = 0f });
        rig.Advance(Focus, Input(notches: 1f), 1f / 60f);
        Assert.True(rig.ZoomGestureActive);
        Run(rig, Focus, CameraInputFrame.None, 0.2f, 1f / 60f);
        Assert.True(rig.ZoomGestureActive);
        Run(rig, Focus, CameraInputFrame.None, 0.2f, 1f / 60f);
        Assert.False(rig.ZoomGestureActive);
    }

    [Fact]
    public void DoubleTappingHomeResetsYawAndDistanceButASingleTapOnlyTheOffset()
    {
        TacticalCameraSettings settings = TacticalCameraSettings.Default with { RotateSmoothingSeconds = 0f, ZoomSmoothingSeconds = 0f };
        TacticalCameraRig rig = NewRig(settings);
        rig.SnapTo(Focus, 100f, 12f, new Vector2(5f, 3f), null);

        rig.Advance(Focus, Input(recentre: true), 1f / 60f);
        Settle(rig, Focus);
        Assert.Equal(Vector3.Zero, rig.PanOffset);
        Assert.Equal(100f, rig.Pose.YawDegrees);
        Assert.Equal(12f, rig.Pose.Distance);

        // Second tap well after the double-tap window: still a single tap.
        Run(rig, Focus, CameraInputFrame.None, 1f, 1f / 60f);
        rig.Advance(Focus, Input(recentre: true), 1f / 60f);
        Assert.Equal(100f, rig.Pose.YawDegrees);

        rig.SnapTo(Focus, 100f, 12f, new Vector2(5f, 3f), null);
        rig.Advance(Focus, Input(recentre: true), 1f / 60f);
        Run(rig, Focus, CameraInputFrame.None, 0.2f, 1f / 60f);
        rig.Advance(Focus, Input(recentre: true), 1f / 60f);
        Assert.Equal(IsometricView.YawDegrees, rig.Pose.YawDegrees);
        Assert.Equal(IsometricView.Distance, rig.Pose.Distance);
        Settle(rig, Focus);
        Assert.Equal(TacticalCameraPose.Default(Focus), rig.Pose);
    }

    [Fact]
    public void PanInputCancelsARecentre()
    {
        TacticalCameraRig rig = NewRig(TacticalCameraSettings.Default with { LeashMetres = 40f });
        rig.SnapTo(Focus, null, null, new Vector2(10f, 0f), null);
        rig.Advance(Focus, Input(recentre: true), 1f / 60f);
        Vector3 start = rig.PanOffset;
        Assert.True(start.Length < 10f);
        Run(rig, Focus, Input(edge: new Vector2(1f, 0f)), 0.3f, 1f / 60f);
        Settle(rig, Focus, 0.3f);
        Assert.True(rig.PanOffset.Length > 0.5f);
    }

    [Fact]
    public void FreeModeKeepsTheTargetWhileFollowMovesWithThePlayer()
    {
        TacticalCameraRig follow = NewRig();
        follow.SnapTo(Focus, null, null, new Vector2(2f, 1f), CameraAnchorMode.Follow);
        TacticalCameraRig free = NewRig();
        free.SnapTo(Focus, null, null, new Vector2(2f, 1f), CameraAnchorMode.Free);

        Vector3 walked = Focus + new Vector3(6f, 0f, -4f);
        Vector3 followTarget = Vector3.Zero;
        Vector3 freeTarget = Vector3.Zero;
        for (int i = 0; i < 10; i++)
        {
            followTarget = follow.Advance(walked, CameraInputFrame.None, 1f / 60f).Target;
            freeTarget = free.Advance(walked, CameraInputFrame.None, 1f / 60f).Target;
        }

        Assert.Equal(walked.X + 2f, followTarget.X, 3);
        Assert.Equal(walked.Z + 1f, followTarget.Z, 3);
        Assert.Equal(Focus.X + 2f, freeTarget.X, 3);
        Assert.Equal(Focus.Z + 1f, freeTarget.Z, 3);
        Assert.Equal(walked.Y, freeTarget.Y);
        Assert.Equal(CameraAnchorMode.Free, free.AnchorMode);
    }

    [Fact]
    public void TogglingTheAnchorModeKeepsTheTargetWhereItIs()
    {
        TacticalCameraRig rig = NewRig();
        rig.SnapTo(Focus, null, null, new Vector2(3f, 0f), null);
        Vector3 before = rig.Advance(Focus, CameraInputFrame.None, 1f / 60f).Target;
        Vector3 after = rig.Advance(Focus, Input(toggle: true), 1f / 60f).Target;
        Assert.Equal(CameraAnchorMode.Free, rig.AnchorMode);
        Assert.Equal(before, after);

        Vector3 walked = Focus + new Vector3(5f, 0f, 0f);
        Vector3 held = rig.Advance(walked, CameraInputFrame.None, 1f / 60f).Target;
        Assert.Equal(before.X, held.X, 3);
        rig.Advance(walked, Input(toggle: true), 1f / 60f);
        Assert.Equal(CameraAnchorMode.Follow, rig.AnchorMode);
        Assert.Equal(before.X, rig.Pose.Target.X, 3);
        Assert.Equal(walked.X - before.X, -rig.PanOffset.X, 3);
    }

    [Fact]
    public void ReturnToPlayerDecaysTheOffsetAfterTheIdleTime()
    {
        TacticalCameraSettings settings = TacticalCameraSettings.Default with { ReturnToPlayerSeconds = 2f };
        TacticalCameraRig rig = NewRig(settings);
        rig.SnapTo(Focus, null, null, new Vector2(6f, 0f), null);
        Run(rig, Focus, CameraInputFrame.None, 1.5f, 1f / 60f);
        Assert.Equal(6f, rig.PanOffset.X, 3);
        Run(rig, Focus, CameraInputFrame.None, 4f, 1f / 60f);
        Assert.Equal(0f, rig.PanOffset.X);
        Assert.Equal(TacticalCameraPose.Default(Focus), rig.Pose);

        TacticalCameraRig off = NewRig();
        off.SnapTo(Focus, null, null, new Vector2(6f, 0f), null);
        Run(off, Focus, CameraInputFrame.None, 10f, 1f / 60f);
        Assert.Equal(6f, off.PanOffset.X, 3);
    }

    [Fact]
    public void SettingsSetterReclampsDistanceAndLeash()
    {
        TacticalCameraRig rig = NewRig(TacticalCameraSettings.Default with { LeashMetres = 30f });
        rig.SnapTo(Focus, null, 25f, new Vector2(20f, 0f), null);
        rig.Settings = TacticalCameraSettings.Default with { MaxDistance = 20f, LeashMetres = 5f };
        Assert.Equal(20f, rig.Pose.Distance);
        Assert.InRange(rig.PanOffset.Length, 4.999f, 5.001f);
    }

    [Fact]
    public void ResetReturnsTheDefaultPoseOnThePlayer()
    {
        TacticalCameraRig rig = NewRig();
        Run(rig, Focus, Input(edge: new Vector2(1f, 0f), notches: 2f, drag: 90f), 1f, 1f / 60f);
        Vector3 other = new(-30f, 4f, 22f);
        rig.Reset(other);
        Assert.Equal(TacticalCameraPose.Default(other), rig.Pose);
        Assert.Equal(Vector3.Zero, rig.PanOffset);
        Assert.False(rig.ZoomGestureActive);
        Assert.Equal(TacticalCameraPose.Default(other), rig.Advance(other, CameraInputFrame.None, 1f / 60f));
    }
}
