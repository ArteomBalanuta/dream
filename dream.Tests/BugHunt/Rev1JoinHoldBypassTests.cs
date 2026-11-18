using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Tests.Simulation.Runtime;

namespace Dream.Tests.BugHunt;

// BH-REV1. The character-keyed spawn-protection brake (BH-PVP-1) was bypassable through the join's opt-in
// protection hold: the hold gated only on ConnectedPlayer.FreshlyAttached, which is per SESSION, and the
// client picks its own session id. Attack, drop the connection, rejoin under a brand-new id asking for the
// hold: the join itself correctly resumed nothing, but the hold made the player untargetable anyway and its
// release wrote a full fresh window AND a cleared spent mark into the character record -- so attack,
// reconnect, attack again was intact with the brake's own record erased each round.
//
// The hold is now gated on the same character record the join consults: it is armed only when that join
// actually granted the character a new window, and a release can never hand back a spent one. The controls
// below keep the reason the hold exists: a genuine join, and a rejoin after a death, are still held and
// protected while the client loads.
public sealed class Rev1JoinHoldBypassTests
{
    private const int Protection = PlayerRespawnRules.SpawnProtectionTicks;
    private const int Cooldown = PlayerRespawnRules.JoinHoldReattachCooldownTicks;
    private static readonly PlayerSessionId A = new("rev1-hold-a");
    private static readonly PlayerSessionId B = new("rev1-hold-b");
    private static readonly PlayerSessionId C = new("rev1-hold-c");
    private const string Character = "Mallory";

    private static ConnectedPlayer Seat(WorldSessionRuntime runtime, PlayerSessionId session)
    {
        Assert.True(runtime.Sessions.TryGet(session, out ConnectedPlayer? player));
        return player!;
    }

    private static void Attack(WorldSessionRuntime runtime, PlayerSessionId session, ulong sequence)
    {
        WaveIRuntimeKit.EnterCombat(runtime, session);
        IAuthoritativePlayerTarget target = WaveIRuntimeKit.Target(runtime, session.Value);
        Assert.True(runtime.ProcessAuthoritativeAction(
            new PlayerActionMessage(session, sequence, new CombatUseIntent(target.EyePosition, target.Facing))).Accepted);
    }

    // The player's first input, whichever seat it is on: what releases a hold.
    private static void Input(WorldSessionRuntime runtime, PlayerSessionId session, ref ulong tick)
    {
        PlayerCommandFrame frame = new(
            session, 1, default, new PlayerMoveIntent(1f, 0f), PlayerLookIntent.None, PrimaryUsePressed: false);
        if (runtime.PrimaryRemoteSessionId == session)
        {
            runtime.AdvanceAuthoritativeTick(
                ++tick, SimulationClock.FixedDeltaSeconds, frame, PlayerSimulationTuning.AuthoritativeDefault);
            return;
        }

        runtime.AdvanceSecondaryRemoteSession(frame, SimulationClock.FixedDeltaSeconds, PlayerSimulationTuning.AuthoritativeDefault);
    }

    // The whole exploit: attack, drop, rejoin under a NEW session id asking for the hold.
    [Fact]
    public void AttackThenRejoinUnderANewSessionId_AskingForTheHold_IsNotHeld()
    {
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        runtime.AttachRemoteSession(A, Character);
        ulong tick = 0;
        WaveIRuntimeKit.Tick(runtime, ref tick, 10);
        Attack(runtime, A, 1);

        runtime.DetachRemoteSession(A);
        WaveIRuntimeKit.Tick(runtime, ref tick, 10);
        runtime.AttachRemoteSession(B, Character);
        runtime.HoldSpawnProtectionUntilInput(B);

        ConnectedPlayer seat = Seat(runtime, B);
        Assert.Equal(0UL, seat.ProtectionHeldSinceTick);
        Assert.Equal(0UL, seat.ProtectedThroughTick);
    }

    // And even granting that something armed a hold on that seat, its release may not write a fresh window
    // or erase the spent mark: the exploit's whole profit is in line 214's grant.
    [Fact]
    public void AReleasedHold_AfterTheCharacterAttacked_GrantsNoFreshWindow_AndKeepsTheSpentMark()
    {
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        runtime.AttachRemoteSession(A, Character);
        ulong tick = 0;
        WaveIRuntimeKit.Tick(runtime, ref tick, 10);
        Attack(runtime, A, 1);

        runtime.DetachRemoteSession(A);
        WaveIRuntimeKit.Tick(runtime, ref tick, 10);
        runtime.AttachRemoteSession(B, Character);
        // A modded client that got a hold any way it likes: the release itself must refuse.
        Seat(runtime, B).ProtectionHeldSinceTick = Math.Max(1UL, tick);

        Input(runtime, B, ref tick);

        Assert.Equal(0UL, Seat(runtime, B).ProtectedThroughTick);

        // The spent mark survived: a further rejoin inside the cooldown still resumes nothing.
        runtime.DetachRemoteSession(B);
        WaveIRuntimeKit.Tick(runtime, ref tick, 10);
        runtime.AttachRemoteSession(C, Character);
        runtime.HoldSpawnProtectionUntilInput(C);
        Assert.Equal(0UL, Seat(runtime, C).ProtectedThroughTick);
        Assert.Equal(0UL, Seat(runtime, C).ProtectionHeldSinceTick);
    }

    // ---- controls: the reasons the hold exists ----

    // A genuine first join is held, stays protected for as long as the client takes to load, and gets the
    // whole normal window from its first input.
    [Fact]
    public void AGenuineFirstJoin_IsStillHeld_AndProtectedWhileLoading()
    {
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        runtime.AttachRemoteSession(A, Character);
        runtime.HoldSpawnProtectionUntilInput(A);
        ConnectedPlayer seat = Seat(runtime, A);
        Assert.NotEqual(0UL, seat.ProtectionHeldSinceTick);

        ulong tick = 0;
        WaveIRuntimeKit.Tick(runtime, ref tick, 1000);
        Assert.NotEqual(0UL, seat.ProtectionHeldSinceTick);

        Input(runtime, A, ref tick);

        Assert.Equal(0UL, seat.ProtectionHeldSinceTick);
        Assert.Equal(tick + Protection, seat.ProtectedThroughTick);
    }

    // The pinned behaviour: a death is the legitimate way back, so a rejoin after one -- even under a new
    // session id, even by a player who had been attacking -- is held again and gets its own full window.
    [Fact]
    public void ARejoinAfterADeath_UnderANewSessionId_IsHeldAgain_AndGetsAFullFreshWindow()
    {
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        runtime.AttachRemoteSession(A, Character);
        ulong tick = 0;
        WaveIRuntimeKit.Tick(runtime, ref tick, 10);
        Attack(runtime, A, 1);
        WaveIRuntimeKit.Kill(runtime, A.Value);
        WaveIRuntimeKit.Tick(runtime, ref tick, PlayerRespawnRules.DelayTicks + 2);
        Assert.True(WaveIRuntimeKit.Target(runtime, A.Value).IsAlive);

        runtime.DetachRemoteSession(A);
        WaveIRuntimeKit.Tick(runtime, ref tick, 10);
        runtime.AttachRemoteSession(B, Character);
        runtime.HoldSpawnProtectionUntilInput(B);

        ConnectedPlayer seat = Seat(runtime, B);
        Assert.NotEqual(0UL, seat.ProtectionHeldSinceTick);
        Assert.Equal(tick + Protection, seat.ProtectedThroughTick);

        Input(runtime, B, ref tick);
        Assert.Equal(tick + Protection, seat.ProtectedThroughTick);
    }

    // A legitimate reconnect after the cooldown, by a player who was not fighting: still held, still plays.
    [Fact]
    public void AReconnectAfterTheCooldown_UnderANewSessionId_IsHeldAndStillPlays()
    {
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        runtime.AttachRemoteSession(A, Character);
        ulong tick = 0;
        runtime.DetachRemoteSession(A);
        WaveIRuntimeKit.Tick(runtime, ref tick, Cooldown + 10);
        runtime.AttachRemoteSession(B, Character);
        runtime.HoldSpawnProtectionUntilInput(B);

        ConnectedPlayer seat = Seat(runtime, B);
        Assert.NotEqual(0UL, seat.ProtectionHeldSinceTick);

        Input(runtime, B, ref tick);

        Assert.Equal(0UL, seat.ProtectionHeldSinceTick);
        Assert.Equal(tick + Protection, seat.ProtectedThroughTick);
        Assert.True(WaveIRuntimeKit.Target(runtime, B.Value).IsAlive);
        Attack(runtime, B, 2);
    }

    // A reconnect inside the cooldown by a player who was NOT fighting keeps the remainder of the window it
    // already had -- no hold, but no punishment either.
    [Fact]
    public void AReconnectInsideTheCooldown_WithoutFighting_KeepsTheRemainder()
    {
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        runtime.AttachRemoteSession(A, Character);
        ulong granted = Seat(runtime, A).ProtectedThroughTick;
        ulong tick = 0;
        WaveIRuntimeKit.Tick(runtime, ref tick, 20);
        runtime.DetachRemoteSession(A);
        WaveIRuntimeKit.Tick(runtime, ref tick, 5);
        runtime.AttachRemoteSession(B, Character);
        runtime.HoldSpawnProtectionUntilInput(B);

        Assert.Equal(granted, Seat(runtime, B).ProtectedThroughTick);
        Assert.Equal(0UL, Seat(runtime, B).ProtectionHeldSinceTick);
    }
}
