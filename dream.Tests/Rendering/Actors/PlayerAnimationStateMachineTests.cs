using OpenTK.Mathematics;
using Xunit;
using Dream.Rendering.Actors;

namespace Dream.Tests.Rendering.Actors;

public class PlayerAnimationStateMachineTests
{
    private const float Dt = 1f / 60f;

    private static PlayerAnimationInputs Inputs(
        bool alive = true,
        float speed = 0f,
        bool airborne = false,
        int attack = 0,
        int hit = 0,
        bool main = false,
        bool off = false,
        bool ranged = false,
        float twist = 0f) =>
        new(alive, speed, airborne, attack, hit, main, off, ranged, twist);

    private static PlayerAnimationFrame Run(PlayerAnimationStateMachine machine, PlayerAnimationInputs inputs, float seconds)
    {
        PlayerAnimationFrame frame = default;
        for (float t = 0f; t < seconds; t += Dt)
        {
            frame = machine.Update(in inputs, Dt);
        }

        return frame;
    }

    [Fact]
    public void Standing_PlaysIdle()
    {
        PlayerAnimationFrame frame = new PlayerAnimationStateMachine().Update(Inputs(), Dt);

        Assert.Equal("idle", frame.Clip);
        Assert.Equal(PlayerClipMode.Loop, frame.Mode);
    }

    [Fact]
    public void Moving_PlaysWalk_AtASpeedFollowingTheMovement()
    {
        PlayerAnimationStateMachine machine = new();

        PlayerAnimationFrame normal = machine.Update(Inputs(speed: 1.6f), Dt);
        PlayerAnimationFrame slow = new PlayerAnimationStateMachine().Update(Inputs(speed: 0.96f), Dt);

        Assert.Equal("walk", normal.Clip);
        Assert.Equal(1f, normal.PlaybackSpeed, 3);
        Assert.Equal("walk", slow.Clip);
        Assert.Equal(0.6f, slow.PlaybackSpeed, 3);
    }

    [Fact]
    public void LegTwist_IsZeroWhenTravelEqualsFacing()
    {
        Assert.Equal(0f, PlayerAnimationStateMachine.LegTwist(0f, 1f, 0f, 1f), 5);
        Assert.Equal(0f, PlayerAnimationStateMachine.LegTwist(1f, 1f, 2f, 2f), 5);
    }

    [Fact]
    public void LegTwist_IsSigned_ForStrafes()
    {
        // Facing +Z, yaw = atan2(x, z): travelling +X is +90 degrees, -X is -90.
        Assert.Equal(MathF.PI / 2f, PlayerAnimationStateMachine.LegTwist(0f, 1f, 1f, 0f), 5);
        Assert.Equal(-MathF.PI / 2f, PlayerAnimationStateMachine.LegTwist(0f, 1f, -1f, 0f), 5);
        // A forward-left diagonal is 45 degrees.
        Assert.Equal(-MathF.PI / 4f, PlayerAnimationStateMachine.LegTwist(0f, 1f, -1f, 1f), 5);
    }

    // C2: LegTwist is the RAW delta now, unclamped, because Update needs the whole angle to pick
    // the walk clip's sector. The clamp moved to the residual the state machine reports.
    [Fact]
    public void LegTwist_IsTheRawDelta_IncludingStraightBackwards()
    {
        Assert.Equal(MathF.Atan2(1f, -0.5f), PlayerAnimationStateMachine.LegTwist(0f, 1f, 1f, -0.5f), 5);
        Assert.Equal(-MathF.Atan2(1f, -0.5f), PlayerAnimationStateMachine.LegTwist(0f, 1f, -1f, -0.5f), 5);
        Assert.Equal(MathF.PI, MathF.Abs(PlayerAnimationStateMachine.LegTwist(0f, 1f, 0f, -1f)), 4);
        Assert.True(MathF.Abs(PlayerAnimationStateMachine.LegTwist(0f, 1f, 0f, -1f)) >
            PlayerAnimationStateMachine.MaxLegTwistRadians);
    }

    // C2's table, exactly: the clip per sector and the residual the pose layers get. The residual
    // never exceeds 45 degrees, which is why MaxLegTwistRadians could drop to pi/4.
    [Theory]
    [InlineData(0f, "walk", 0f)]
    [InlineData(30f, "walk", 30f)]
    [InlineData(-44f, "walk", -44f)]
    [InlineData(45f, "walk", 45f)]
    [InlineData(60f, "walk-strafe-right", -30f)]
    [InlineData(90f, "walk-strafe-right", 0f)]
    [InlineData(135f, "walk-strafe-right", 45f)]
    [InlineData(-60f, "walk-strafe-left", 30f)]
    [InlineData(-90f, "walk-strafe-left", 0f)]
    [InlineData(-135f, "walk-strafe-left", -45f)]
    [InlineData(160f, "walk-back", -20f)]
    [InlineData(180f, "walk-back", 0f)]
    [InlineData(-160f, "walk-back", 20f)]
    public void ResolveWalkClip_FollowsTheDirectionalTable(float degrees, string expectedClip, float expectedResidualDegrees)
    {
        string clip = PlayerAnimationStateMachine.ResolveWalkClip(
            MathHelper.DegreesToRadians(degrees), out float residual);

        Assert.Equal(expectedClip, clip);
        Assert.Equal(expectedResidualDegrees, MathHelper.RadiansToDegrees(residual), 3);
        Assert.True(MathF.Abs(residual) <= PlayerAnimationStateMachine.MaxLegTwistRadians + 1e-4f);
    }

    [Fact]
    public void TheLegTwistClamp_IsTheWithinSectorResidual()
    {
        Assert.Equal(MathF.PI / 4f, PlayerAnimationStateMachine.MaxLegTwistRadians, 5);
        Assert.True(MathHelper.RadiansToDegrees(PlayerAnimationStateMachine.MaxLegTwistRadians) <= 45f + 1e-3f);
    }

    [Theory]
    [InlineData(170f, "walk-back")]
    [InlineData(-100f, "walk-strafe-left")]
    [InlineData(10f, "walk")]
    public void Moving_PicksTheDirectionalClip_AndReportsOnlyTheResidual(float degrees, string expectedClip)
    {
        float delta = MathHelper.DegreesToRadians(degrees);

        PlayerAnimationFrame frame = new PlayerAnimationStateMachine()
            .Update(Inputs(speed: 1.6f, twist: delta), Dt);

        PlayerAnimationStateMachine.ResolveWalkClip(delta, out float residual);

        Assert.Equal(expectedClip, frame.Clip);
        Assert.Equal(residual, frame.LegTwistRadians, 5);
        Assert.True(MathF.Abs(frame.LegTwistRadians) <= PlayerAnimationStateMachine.MaxLegTwistRadians + 1e-4f);
    }

    // The sprint deliberately keeps ONE clip (C2), so nothing covers its direction but the twist -
    // and the twist it gets is the RAW delta, not a sector residual. It therefore keeps the wider
    // pre-C2 clamp: at pi/4 a backwards sprint would leave the stride 135 degrees off its travel,
    // worse than the 90 degrees it was before this wave and closer to the moonwalk C2 set out to
    // kill. The same holds for an attack while moving, which also plays one clip.
    [Fact]
    public void Sprinting_KeepsOneClip_AndTheRawDeltaUnderTheWiderSprintClamp()
    {
        PlayerAnimationFrame backwards = new PlayerAnimationStateMachine()
            .Update(Inputs(speed: 10f, twist: MathF.PI), Dt);
        PlayerAnimationFrame diagonal = new PlayerAnimationStateMachine()
            .Update(Inputs(speed: 10f, twist: -MathF.PI / 3f), Dt);

        Assert.Equal("sprint", backwards.Clip);
        Assert.Equal(MathF.PI / 2f, PlayerAnimationStateMachine.MaxSprintLegTwistRadians, 5);
        // Straight backwards: the stride ends 90 degrees off travel, as it did before C2 - not the
        // 135 degrees a pi/4 clamp would leave.
        Assert.Equal(PlayerAnimationStateMachine.MaxSprintLegTwistRadians, backwards.LegTwistRadians, 5);
        Assert.True(backwards.LegTwistRadians > PlayerAnimationStateMachine.MaxLegTwistRadians);
        Assert.Equal(MathF.PI - backwards.LegTwistRadians, MathF.PI / 2f, 5);
        // Inside the sprint clamp the raw delta passes through untouched.
        Assert.Equal("sprint", diagonal.Clip);
        Assert.Equal(-MathF.PI / 3f, diagonal.LegTwistRadians, 5);
    }

    // An attack while moving also plays one clip (the swing), so it takes the same wide clamp.
    [Fact]
    public void AttackingWhileMoving_TakesTheRawDelta_UnderTheSprintClamp()
    {
        PlayerAnimationStateMachine machine = new();
        machine.Update(Inputs(speed: 6f, attack: 0), Dt);

        PlayerAnimationFrame frame = machine.Update(Inputs(speed: 6f, attack: 1, twist: MathF.PI), Dt);

        Assert.Equal("attack-melee-right", frame.Clip);
        Assert.Equal(PlayerAnimationStateMachine.MaxSprintLegTwistRadians, frame.LegTwistRadians, 5);
    }

    // The sector boundary needs the same hysteresis every other branch of this state machine has:
    // AnimationPlaybackState.Play restarts the clip at t = 0 on a name change, so a cursor hovering
    // near 45 degrees off the key direction would stutter the stride at frame 0 on mouse jitter.
    [Fact]
    public void TheSectorBoundary_IsSticky_SoJitterAtTheBoundaryDoesNotRestartTheStride()
    {
        Assert.True(PlayerAnimationStateMachine.SectorHysteresisRadians > 0f);
        float just = PlayerAnimationStateMachine.StrafeSectorStartRadians +
            (PlayerAnimationStateMachine.SectorHysteresisRadians * 0.5f);

        // Past the boundary, but inside the deadband: whatever is playing keeps playing.
        Assert.Equal("walk", PlayerAnimationStateMachine.ResolveWalkClip(just, "walk", out _));
        Assert.Equal("walk-strafe-right", PlayerAnimationStateMachine.ResolveWalkClip(just, "walk-strafe-right", out _));
        // With no clip playing it is the plain table, and the boundary still moves eventually.
        Assert.Equal("walk-strafe-right", PlayerAnimationStateMachine.ResolveWalkClip(just, null, out _));
        Assert.Equal(
            "walk-strafe-right",
            PlayerAnimationStateMachine.ResolveWalkClip(
                PlayerAnimationStateMachine.StrafeSectorStartRadians +
                (PlayerAnimationStateMachine.SectorHysteresisRadians * 2f), "walk", out _));

        // The whole point, through Update: jitter either side of the boundary keeps one clip name.
        PlayerAnimationStateMachine machine = new();
        float below = PlayerAnimationStateMachine.StrafeSectorStartRadians - 0.01f;
        string first = machine.Update(Inputs(speed: 1.6f, twist: below), Dt).Clip;
        List<string> names = new() { first };
        for (int frame = 0; frame < 8; frame++)
        {
            float jitter = (frame & 1) == 0 ? just : below;
            names.Add(machine.Update(Inputs(speed: 1.6f, twist: jitter), Dt).Clip);
        }

        Assert.Equal("walk", first);
        Assert.Single(names.Distinct());
    }

    // And the other side of the deadband: the clip does change once the cursor really moves over,
    // so the stickiness cannot silently become "never switch".
    [Fact]
    public void TheStickySector_StillGivesWay_WhenTheTravelReallyChangesSector()
    {
        PlayerAnimationStateMachine machine = new();
        machine.Update(Inputs(speed: 1.6f, twist: 0.1f), Dt);

        PlayerAnimationFrame strafe = machine.Update(Inputs(speed: 1.6f, twist: MathF.PI / 2f), Dt);
        PlayerAnimationFrame back = machine.Update(Inputs(speed: 1.6f, twist: MathF.PI), Dt);

        Assert.Equal("walk-strafe-right", strafe.Clip);
        Assert.Equal(0f, strafe.LegTwistRadians, 5);
        Assert.Equal("walk-back", back.Clip);
    }

    [Fact]
    public void FallbackClip_LetsAModelWithoutTheDirectionalClipsKeepWalking()
    {
        Assert.Equal("walk", PlayerAnimationStateMachine.FallbackClip("run"));
        Assert.Equal("walk", PlayerAnimationStateMachine.FallbackClip("walk-back"));
        Assert.Equal("walk", PlayerAnimationStateMachine.FallbackClip("walk-strafe-left"));
        Assert.Equal("walk", PlayerAnimationStateMachine.FallbackClip("walk-strafe-right"));
        Assert.Equal("idle", PlayerAnimationStateMachine.FallbackClip("idle"));
        Assert.Equal("die", PlayerAnimationStateMachine.FallbackClip("die"));
    }

    [Fact]
    public void LegTwist_WrapsAcrossTheYawSeam()
    {
        // Facing yaw just under +180, travel just past it: a small twist, not a near-full turn.
        float twist = PlayerAnimationStateMachine.LegTwist(MathF.Sin(3.0f), MathF.Cos(3.0f), MathF.Sin(-3.0f), MathF.Cos(-3.0f));

        Assert.Equal(MathF.Tau - 6.0f, twist, 4);
    }

    [Fact]
    public void LegTwist_ZeroDirections_GiveNoTwist()
    {
        Assert.Equal(0f, PlayerAnimationStateMachine.LegTwist(0f, 1f, 0f, 0f));
        Assert.Equal(0f, PlayerAnimationStateMachine.LegTwist(0f, 0f, 1f, 0f));
    }

    [Fact]
    public void Frame_CarriesTheResidualWhileMoving_WithinTheClamp()
    {
        PlayerAnimationStateMachine machine = new();

        // 1.0 rad is a strafe sector: the clip takes 90 degrees, the residual is what is left.
        PlayerAnimationFrame moving = Run(machine, Inputs(speed: 1.6f, twist: 1.0f), 0.2f);
        PlayerAnimationFrame over = new PlayerAnimationStateMachine().Update(Inputs(speed: 1.6f, twist: 3.0f), Dt);
        PlayerAnimationFrame under = new PlayerAnimationStateMachine().Update(Inputs(speed: 1.6f, twist: -3.0f), Dt);

        Assert.Equal("walk-strafe-right", moving.Clip);
        Assert.Equal(1.0f - (MathF.PI / 2f), moving.LegTwistRadians, 5);
        Assert.Equal("walk-back", over.Clip);
        Assert.Equal(3.0f - MathF.PI, over.LegTwistRadians, 5);
        Assert.Equal(MathF.PI - 3.0f, under.LegTwistRadians, 5);
        Assert.All(
            new[] { moving.LegTwistRadians, over.LegTwistRadians, under.LegTwistRadians },
            twist => Assert.True(MathF.Abs(twist) <= PlayerAnimationStateMachine.MaxLegTwistRadians + 1e-4f));
    }

    [Fact]
    public void Frame_HasNoTwist_WhenStationaryOrDead()
    {
        PlayerAnimationFrame still = new PlayerAnimationStateMachine().Update(Inputs(speed: 0f, twist: 1.0f), Dt);
        PlayerAnimationFrame dead = new PlayerAnimationStateMachine().Update(Inputs(alive: false, speed: 6f, twist: 1.0f), Dt);

        Assert.Equal(0f, still.LegTwistRadians);
        Assert.Equal(0f, dead.LegTwistRadians);
    }

    [Fact]
    public void Fast_PlaysSprint()
    {
        PlayerAnimationFrame frame = new PlayerAnimationStateMachine().Update(Inputs(speed: 10f), Dt);

        Assert.Equal("sprint", frame.Clip);
    }

    [Fact]
    public void WalkStopsAtALowerSpeedThanItStartsAt()
    {
        PlayerAnimationStateMachine machine = new();
        Assert.Equal("walk", machine.Update(Inputs(speed: 0.4f), Dt).Clip);

        // Still above the stop threshold: no flicker back to idle.
        Assert.Equal("walk", machine.Update(Inputs(speed: 0.3f), Dt).Clip);
        Assert.Equal("idle", machine.Update(Inputs(speed: 0.05f), Dt).Clip);
    }

    [Fact]
    public void AttackSerialChange_StartsAOneShot_ThatReturnsToTheLoop()
    {
        PlayerAnimationStateMachine machine = new();
        machine.Update(Inputs(attack: 5), Dt);

        PlayerAnimationFrame swing = machine.Update(Inputs(attack: 6), Dt);
        Assert.Equal("attack-melee-right", swing.Clip);
        Assert.Equal(PlayerClipMode.OneShot, swing.Mode);

        PlayerAnimationFrame after = Run(machine, Inputs(attack: 6), PlayerAnimationStateMachine.MeleeSeconds + 0.1f);
        Assert.Equal("idle", after.Clip);
        Assert.Equal(PlayerClipMode.Loop, after.Mode);
    }

    [Fact]
    public void FirstSerialsSeenOnlySetTheBaseline()
    {
        PlayerAnimationStateMachine machine = new();

        PlayerAnimationFrame frame = machine.Update(Inputs(attack: 40, hit: 12), Dt);

        Assert.Equal("idle", frame.Clip);
        Assert.Equal(0f, frame.HitFlinch);
    }

    [Fact]
    public void ABowShootsInsteadOfSwinging()
    {
        PlayerAnimationStateMachine machine = new();
        machine.Update(Inputs(attack: 0, main: true, ranged: true), Dt);

        PlayerAnimationFrame frame = machine.Update(Inputs(attack: 1, main: true, ranged: true), Dt);

        Assert.Equal("holding-right-shoot", frame.Clip);
    }

    [Fact]
    public void ASecondSwingWhileSwinging_IsIgnored_AndOneAfterwardsPlays()
    {
        PlayerAnimationStateMachine machine = new();
        machine.Update(Inputs(), Dt);
        int firstSerial = machine.Update(Inputs(attack: 1), Dt).OneShotSerial;

        PlayerAnimationFrame during = machine.Update(Inputs(attack: 2), Dt);
        Assert.Equal(firstSerial, during.OneShotSerial);

        Run(machine, Inputs(attack: 2), PlayerAnimationStateMachine.MeleeSeconds + 0.1f);
        PlayerAnimationFrame next = machine.Update(Inputs(attack: 3), Dt);
        Assert.Equal(firstSerial + 1, next.OneShotSerial);
    }

    [Fact]
    public void Hit_FlinchesAndFadesWithoutChangingTheClip()
    {
        PlayerAnimationStateMachine machine = new();
        machine.Update(Inputs(speed: 1.6f), Dt);

        PlayerAnimationFrame start = machine.Update(Inputs(speed: 1.6f, hit: 1), Dt);
        PlayerAnimationFrame mid = Run(machine, Inputs(speed: 1.6f, hit: 1), PlayerAnimationStateMachine.HitSeconds / 2f);
        PlayerAnimationFrame end = Run(machine, Inputs(speed: 1.6f, hit: 1), PlayerAnimationStateMachine.HitSeconds);

        Assert.Equal("walk", start.Clip);
        Assert.True(mid.HitFlinch > 0.8f);
        Assert.Equal(0f, end.HitFlinch);
    }

    [Fact]
    public void Airborne_TucksTheLegsAndLandsBack()
    {
        PlayerAnimationStateMachine machine = new();

        PlayerAnimationFrame up = Run(machine, Inputs(airborne: true), 0.5f);
        PlayerAnimationFrame landed = Run(machine, Inputs(), 0.5f);

        Assert.True(up.AirTuck > 0.95f);
        Assert.Equal("idle", up.Clip);
        Assert.True(landed.AirTuck < 0.05f);
    }

    [Fact]
    public void Death_HoldsTheDieClip_AndBeatsEverythingElse()
    {
        PlayerAnimationStateMachine machine = new();
        machine.Update(Inputs(), Dt);

        PlayerAnimationFrame dead = machine.Update(Inputs(alive: false, speed: 6f, attack: 9, hit: 9, airborne: true), Dt);
        PlayerAnimationFrame later = Run(machine, Inputs(alive: false, attack: 10), 2f);

        Assert.Equal("die", dead.Clip);
        Assert.Equal(PlayerClipMode.Hold, dead.Mode);
        Assert.True(dead.IsDead);
        Assert.Equal(dead.OneShotSerial, later.OneShotSerial);
        Assert.Equal(0f, later.AirTuck);
    }

    [Fact]
    public void Respawn_ReturnsToIdle_WithANewOneShotSerial()
    {
        PlayerAnimationStateMachine machine = new();
        machine.Update(Inputs(), Dt);
        int deadSerial = machine.Update(Inputs(alive: false), Dt).OneShotSerial;

        PlayerAnimationFrame alive = machine.Update(Inputs(), Dt);

        Assert.Equal("idle", alive.Clip);
        Assert.False(alive.IsDead);
        Assert.NotEqual(deadSerial, alive.OneShotSerial);
    }

    [Fact]
    public void HoldingAnItem_RaisesTheArm_ButTheSwingClipOwnsTheMainArmWhileItPlays()
    {
        PlayerAnimationStateMachine machine = new();
        machine.Update(Inputs(main: true, off: true), Dt);

        PlayerAnimationFrame held = Run(machine, Inputs(main: true, off: true), 0.5f);
        Assert.True(held.MainArmHold > 0.95f);
        Assert.True(held.OffArmHold > 0.95f);

        PlayerAnimationFrame swing = Run(machine, Inputs(attack: 1, main: true, off: true), 0.3f);
        Assert.Equal("attack-melee-right", swing.Clip);
        Assert.True(swing.MainArmHold < 0.1f);
        Assert.True(swing.OffArmHold > 0.95f);
    }

    [Fact]
    public void EmptyHands_KeepTheArmsDown()
    {
        PlayerAnimationFrame frame = Run(new PlayerAnimationStateMachine(), Inputs(), 0.5f);

        Assert.Equal(0f, frame.MainArmHold);
        Assert.Equal(0f, frame.OffArmHold);
    }

    // The three ground gaits, each at the speed its clip is authored for: the cycle plays at its
    // authored rate (playback 1.0) there. Before this wave there was one reference speed of 6 m/s
    // and one threshold of 9, so the only gait a player could ever reach was the walk cycle played
    // at rate 1.0 while sprinting.
    [Theory]
    [InlineData(0.2f, "idle")]
    [InlineData(PlayerAnimationStateMachine.ReferenceWalkSpeed, "walk")]
    [InlineData(PlayerAnimationStateMachine.ReferenceRunSpeed, "run")]
    [InlineData(PlayerAnimationStateMachine.ReferenceSprintSpeed, "sprint")]
    public void EachGaitTier_PlaysItsOwnClip_AtItsAuthoredRate(float speed, string expectedClip)
    {
        PlayerAnimationFrame frame = new PlayerAnimationStateMachine().Update(Inputs(speed: speed), Dt);

        Assert.Equal(expectedClip, frame.Clip);
        if (expectedClip != "idle")
        {
            Assert.Equal(1f, frame.PlaybackSpeed, 2);
        }
    }

    // Accelerating from a stop to the sprint and back: the gait climbs through every tier and comes
    // back down through it, and - the point of GaitHysteresisSpeed - no tier is entered and left
    // twice on the way, which is what an undamped boundary would do by restarting the stride at
    // frame 0 (AnimationPlaybackState.Play resets the clip time on a clip NAME change). Same shape
    // as the sector-jitter test above.
    [Fact]
    public void ASweepUpToTheSprintAndBack_ClimbsTheTiersOnce_WithNoFlipping()
    {
        PlayerAnimationStateMachine machine = new();
        List<string> order = new();
        List<float> upSwitches = new();
        List<float> downSwitches = new();

        void Step(float speed, List<float> switches)
        {
            string clip = machine.Update(Inputs(speed: speed), Dt).Clip;
            if (clip == "idle")
            {
                return;
            }

            if (order.Count == 0 || order[^1] != clip)
            {
                order.Add(clip);
                if (order.Count > 1)
                {
                    switches.Add(speed);
                }
            }
        }

        for (int i = 0; i <= 30; i++)
        {
            Step(i * 0.2f, upSwitches);
        }

        for (int i = 30; i >= 0; i--)
        {
            Step(i * 0.2f, downSwitches);
        }

        Assert.Equal(new[] { "walk", "run", "sprint", "run", "walk" }, order);

        // Every boundary is crossed above it going up and below it going down, by about the
        // deadband: that is what makes a speed hovering on a boundary unable to flip.
        Assert.Equal(2, upSwitches.Count);
        Assert.Equal(2, downSwitches.Count);
        Assert.True(upSwitches[0] > PlayerAnimationStateMachine.RunSpeedThreshold);
        Assert.True(upSwitches[1] > PlayerAnimationStateMachine.SprintSpeedThreshold);
        Assert.True(downSwitches[0] < PlayerAnimationStateMachine.SprintSpeedThreshold);
        Assert.True(downSwitches[1] < PlayerAnimationStateMachine.RunSpeedThreshold);
    }

    // D5: the RUN tier is the default cruise, so it runs C2's sector table exactly as the walk tier
    // does - the forward sector swaps in the run cycle and the other sectors reuse the directional
    // walk clips played faster. A run tier that played one clip would silently switch the moonwalk
    // fix off at the speed the player actually moves at, and still look right from the front. The
    // sprint tier deliberately keeps its single clip.
    [Theory]
    [InlineData(MathF.PI / 2f, "walk-strafe-right")]
    [InlineData(MathF.PI, "walk-back")]
    [InlineData(0f, "run")]
    public void TheRunTier_StillPicksTheDirectionalClip(float twist, string expectedClip)
    {
        PlayerAnimationFrame running = new PlayerAnimationStateMachine()
            .Update(Inputs(speed: PlayerAnimationStateMachine.ReferenceRunSpeed, twist: twist), Dt);
        PlayerAnimationFrame sprinting = new PlayerAnimationStateMachine()
            .Update(Inputs(speed: PlayerAnimationStateMachine.ReferenceSprintSpeed, twist: twist), Dt);

        Assert.Equal(expectedClip, running.Clip);
        Assert.True(MathF.Abs(running.LegTwistRadians) <= PlayerAnimationStateMachine.MaxLegTwistRadians + 1e-4f);
        Assert.Equal("sprint", sprinting.Clip);
    }

    // The run tier plays the directional walk clips FASTER: they are authored for the walk, so the
    // playback rate is still the run tier's, not the walk's.
    [Fact]
    public void TheRunTier_PlaysTheDirectionalWalkClipsAtTheRunRate()
    {
        PlayerAnimationFrame frame = new PlayerAnimationStateMachine().Update(
            Inputs(speed: PlayerAnimationStateMachine.ReferenceRunSpeed, twist: MathF.PI), Dt);

        Assert.Equal("walk-back", frame.Clip);
        Assert.Equal(1f, frame.PlaybackSpeed, 2);
    }
}
