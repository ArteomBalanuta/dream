using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Tests.Simulation.Runtime;

namespace Dream.Tests.BugHunt;

// BH-PVP-1: the plain spawn-protection window could be re-armed at will by re-joining. A player losing a
// fight dropped the connection (or had the same account take its own seat over), came back with a brand-new
// 3 s of total immunity to PvP and to enemies, landed a free hit and repeated. The window now belongs to the
// character, not the seat: a re-join resumes what is left of the one the character already had (nothing,
// once an attack spent it) and starts a new one only after the reattach cooldown, which an attack also
// restarts. The controls below keep the reasons the window exists: a first join, a respawn and a reconnect
// by someone who was not fighting are all protected, and the window still expires on its own.
public sealed class PvpSpawnProtectionRearmTests
{
    private const int Protection = PlayerRespawnRules.SpawnProtectionTicks;
    private const int Cooldown = PlayerRespawnRules.JoinHoldReattachCooldownTicks;
    private static readonly PlayerSessionId A = new("pvp-rearm-a");
    private static readonly PlayerSessionId B = new("pvp-rearm-b");
    private const string Character = "Alice";

    private static ConnectedPlayer Seat(WorldSessionRuntime runtime, PlayerSessionId session)
    {
        Assert.True(runtime.Sessions.TryGet(session, out ConnectedPlayer? player));
        return player!;
    }

    // One accepted attack by the session, which is what spends spawn protection.
    private static void Attack(WorldSessionRuntime runtime, PlayerSessionId session, ulong sequence)
    {
        WaveIRuntimeKit.EnterCombat(runtime, session);
        IAuthoritativePlayerTarget target = WaveIRuntimeKit.Target(runtime, session.Value);
        Assert.True(runtime.ProcessAuthoritativeAction(
            new PlayerActionMessage(session, sequence, new CombatUseIntent(target.EyePosition, target.Facing))).Accepted);
    }

    [Fact]
    public void ARejoinRightAfterAttacking_DoesNotReArmTheWindow()
    {
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        runtime.AttachRemoteSession(A, Character);
        ulong tick = 0;
        WaveIRuntimeKit.Tick(runtime, ref tick, 10);
        Attack(runtime, A, 1);
        Assert.Equal(0UL, Seat(runtime, A).ProtectedThroughTick);

        runtime.DetachRemoteSession(A);
        WaveIRuntimeKit.Tick(runtime, ref tick, 10);
        runtime.AttachRemoteSession(A, Character);

        Assert.Equal(0UL, Seat(runtime, A).ProtectedThroughTick);
    }

    // The same account takes its own seat over, so the join can arrive under a different session id and the
    // seat that carried the history is gone. The character's record is what refuses the new window.
    [Fact]
    public void ARejoinUnderANewSessionId_AfterAttacking_DoesNotReArmTheWindow()
    {
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        runtime.AttachRemoteSession(A, Character);
        ulong tick = 0;
        WaveIRuntimeKit.Tick(runtime, ref tick, 10);
        Attack(runtime, A, 1);

        runtime.DetachRemoteSession(A);
        WaveIRuntimeKit.Tick(runtime, ref tick, 10);
        runtime.AttachRemoteSession(B, Character);

        Assert.Equal(0UL, Seat(runtime, B).ProtectedThroughTick);
    }

    // Two live seats can share a CharacterKey (wave 16). A second seat resumes the window the character
    // already has instead of fetching its own, so it cannot be used to carry immunity into a fight.
    [Fact]
    public void ASecondLiveSeatOnTheSameCharacter_ResumesTheWindow_InsteadOfFetchingANewOne()
    {
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        runtime.AttachRemoteSession(A, Character);
        ulong first = Seat(runtime, A).ProtectedThroughTick;
        Assert.NotEqual(0UL, first);
        ulong tick = 0;
        WaveIRuntimeKit.Tick(runtime, ref tick, 100);

        runtime.AttachRemoteSession(B, Character);

        Assert.Equal(first, Seat(runtime, B).ProtectedThroughTick);
    }

    // And the second seat gets nothing at all once the character has spent the window by attacking.
    [Fact]
    public void ASecondLiveSeatOnTheSameCharacter_GetsNothingOnceTheCharacterAttacked()
    {
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        runtime.AttachRemoteSession(A, Character);
        ulong tick = 0;
        WaveIRuntimeKit.Tick(runtime, ref tick, 10);
        Attack(runtime, A, 1);

        runtime.AttachRemoteSession(B, Character);

        Assert.Equal(0UL, Seat(runtime, B).ProtectedThroughTick);
    }

    // A re-join long after the last grant still cannot stretch the window if the character has been fighting.
    [Fact]
    public void ARejoinAfterTheCooldownSinceTheJoin_ButRightAfterAnAttack_GetsNothing()
    {
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        runtime.AttachRemoteSession(A, Character);
        ulong tick = 0;
        WaveIRuntimeKit.Tick(runtime, ref tick, Cooldown + 100);
        Attack(runtime, A, 1);

        runtime.DetachRemoteSession(A);
        WaveIRuntimeKit.Tick(runtime, ref tick, 10);
        runtime.AttachRemoteSession(A, Character);

        Assert.Equal(0UL, Seat(runtime, A).ProtectedThroughTick);
    }

    // ---- controls: the reasons the window exists ----

    [Fact]
    public void AFirstJoin_IsStillProtectedForTheWholeWindow()
    {
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        runtime.AttachRemoteSession(A, Character);
        Assert.Equal((ulong)Protection, Seat(runtime, A).ProtectedThroughTick);
    }

    [Fact]
    public void TheWindow_StillExpiresOnItsOwn()
    {
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        runtime.AttachRemoteSession(A, Character);
        ConnectedPlayer seat = Seat(runtime, A);
        ulong tick = 0;
        WaveIRuntimeKit.Tick(runtime, ref tick, Protection);
        Assert.True(seat.ProtectedThroughTick >= tick, "the window ended early");
        WaveIRuntimeKit.Tick(runtime, ref tick, 1);
        Assert.True(seat.ProtectedThroughTick < tick, "the window never expired");
    }

    [Fact]
    public void ARespawnAfterDeath_IsStillProtected()
    {
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        runtime.AttachRemoteSession(A, Character);
        ulong tick = 0;
        WaveIRuntimeKit.Tick(runtime, ref tick, 10);
        Attack(runtime, A, 1);
        Assert.Equal(0UL, Seat(runtime, A).ProtectedThroughTick);

        WaveIRuntimeKit.Kill(runtime, A.Value);
        WaveIRuntimeKit.Tick(runtime, ref tick, PlayerRespawnRules.DelayTicks + 2);

        ConnectedPlayer seat = Seat(runtime, A);
        Assert.True(WaveIRuntimeKit.Target(runtime, A.Value).IsAlive);
        Assert.True(seat.ProtectedThroughTick >= tick, "a respawn left the player unprotected");
    }

    // A dropped connection by a player who was not fighting: the reconnect is protected again once the
    // reattach cooldown has run, exactly as the join hold already allows.
    [Fact]
    public void AReconnectAfterTheCooldown_WithoutFighting_IsProtectedAgain()
    {
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        runtime.AttachRemoteSession(A, Character);
        ulong tick = 0;
        runtime.DetachRemoteSession(A);
        WaveIRuntimeKit.Tick(runtime, ref tick, Cooldown + 10);
        runtime.AttachRemoteSession(A, Character);

        Assert.Equal(tick + Protection, Seat(runtime, A).ProtectedThroughTick);
    }

    // A reconnect inside the cooldown keeps whatever is left of the window it already had -- it is not
    // punished for the blip, it simply gets no more than staying connected would have given it.
    [Fact]
    public void AReconnectInsideTheCooldown_KeepsTheRemainderOfTheWindow_AndCanStillPlay()
    {
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        runtime.AttachRemoteSession(A, Character);
        ulong granted = Seat(runtime, A).ProtectedThroughTick;
        ulong tick = 0;
        WaveIRuntimeKit.Tick(runtime, ref tick, 20);
        runtime.DetachRemoteSession(A);
        WaveIRuntimeKit.Tick(runtime, ref tick, 5);
        runtime.AttachRemoteSession(A, Character);

        Assert.Equal(granted, Seat(runtime, A).ProtectedThroughTick);

        // And the seat is playable: it moves and its actions are accepted.
        IAuthoritativePlayerTarget target = WaveIRuntimeKit.Target(runtime, A.Value);
        Assert.True(target.IsAlive);
        Vector3 before = target.Position;
        runtime.AdvanceAuthoritativeTick(
            ++tick,
            SimulationClock.FixedDeltaSeconds,
            new PlayerCommandFrame(A, 1, default, new PlayerMoveIntent(1f, 0f), PlayerLookIntent.None, PrimaryUsePressed: false),
            PlayerSimulationTuning.AuthoritativeDefault);
        Assert.NotEqual(before, WaveIRuntimeKit.Target(runtime, A.Value).Position);
        Attack(runtime, A, 2);
    }

    // A different character is untouched by its neighbour's record.
    [Fact]
    public void AnotherCharactersJoin_IsProtectedEvenWhileTheFirstIsFighting()
    {
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        runtime.AttachRemoteSession(A, Character);
        ulong tick = 0;
        WaveIRuntimeKit.Tick(runtime, ref tick, 10);
        Attack(runtime, A, 1);

        runtime.AttachRemoteSession(B, "Bob");

        Assert.Equal(tick + Protection, Seat(runtime, B).ProtectedThroughTick);
    }
}
