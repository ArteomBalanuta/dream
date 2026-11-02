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
        bool ranged = false) =>
        new(alive, speed, airborne, attack, hit, main, off, ranged);

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

        PlayerAnimationFrame normal = machine.Update(Inputs(speed: 6f), Dt);
        PlayerAnimationFrame slow = new PlayerAnimationStateMachine().Update(Inputs(speed: 3.6f), Dt);

        Assert.Equal("walk", normal.Clip);
        Assert.Equal(1f, normal.PlaybackSpeed, 3);
        Assert.Equal("walk", slow.Clip);
        Assert.Equal(0.6f, slow.PlaybackSpeed, 3);
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
        machine.Update(Inputs(speed: 6f), Dt);

        PlayerAnimationFrame start = machine.Update(Inputs(speed: 6f, hit: 1), Dt);
        PlayerAnimationFrame mid = Run(machine, Inputs(speed: 6f, hit: 1), PlayerAnimationStateMachine.HitSeconds / 2f);
        PlayerAnimationFrame end = Run(machine, Inputs(speed: 6f, hit: 1), PlayerAnimationStateMachine.HitSeconds);

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
}
