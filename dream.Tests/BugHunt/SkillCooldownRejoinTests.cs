using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Runtime;
using Dream.Tests.Simulation.Runtime;

namespace Dream.Tests.BugHunt;

// BH-SKILL-2: a reconnect wiped every running cooldown while mana, health and the bag all came back
// from persistence, so dropping the connection was a free reset of the one cost a cast takes besides
// mana. AttachRemoteSession cleared the seat's AbilityCooldowns on every fresh attach; nothing was
// keyed on the character. Cooldowns are now recorded against the CHARACTER as they start, exactly as
// the spawn-protection window is, and a fresh attach resumes the ones still running at the current
// server tick. The controls keep what a cooldown is for: it still runs down on its own, a legitimate
// reconnect can cast again the moment it has, and another character is untouched.
public sealed class SkillCooldownRejoinTests
{
    private const string Mend = "skill.mend";
    private const int MendCooldownTicks = 320;
    private static readonly PlayerSessionId A = new("skill-cd-a");
    private static readonly PlayerSessionId B = new("skill-cd-b");
    private static readonly PlayerSessionId C = new("skill-cd-c");
    private const string Character = "Alice";
    private const string OtherCharacter = "Bob";

    private static ConnectedPlayer Seat(WorldSessionRuntime runtime, PlayerSessionId session)
    {
        Assert.True(runtime.Sessions.TryGet(session, out ConnectedPlayer? player));
        return player!;
    }

    // A mend cast. The caster is nicked first because a SelfHeal that would restore nothing is refused
    // (BH-SKILL-1) and these tests are about the cooldown, not about the heal. The cooldown gate runs
    // before the heal gate, so a cast refused for being on cooldown is still refused for that reason.
    private static AuthoritativeActionResult Cast(WorldSessionRuntime runtime, PlayerSessionId session, ulong sequence)
    {
        WaveIRuntimeKit.Target(runtime, session.Value).Combatant.Health.ApplyDamage(1);
        return runtime.ProcessAuthoritativeAction(
            new PlayerActionMessage(session, sequence, new SkillUseIntent(Mend)));
    }

    private static bool MendReady(WorldSessionRuntime runtime, PlayerSessionId session)
        => Seat(runtime, session).Cooldowns.IsReady(Mend, runtime.CurrentServerTick);

    // The exploit: cast, drop, come back, cast again with the cooldown still owed.
    [Fact]
    public void ARejoinMidCooldown_DoesNotHandTheCooldownBack()
    {
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        runtime.AttachRemoteSession(A, Character);
        ulong tick = 0;
        WaveIRuntimeKit.Tick(runtime, ref tick, 10);
        Assert.True(Cast(runtime, A, 1).Accepted);
        Assert.False(MendReady(runtime, A));

        runtime.DetachRemoteSession(A);
        WaveIRuntimeKit.Tick(runtime, ref tick, 20);
        runtime.AttachRemoteSession(A, Character);

        Assert.False(MendReady(runtime, A));
        AuthoritativeActionResult second = Cast(runtime, A, 2);
        Assert.False(second.Accepted);
        Assert.Contains("cooldown", second.Rejection!.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // The same account coming back under a different session id (the seat that cast is gone) is the
    // shape the spawn-protection fix had to handle too, and the character's record is what refuses it.
    [Fact]
    public void ARejoinUnderANewSessionId_DoesNotHandTheCooldownBack()
    {
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        runtime.AttachRemoteSession(A, Character);
        ulong tick = 0;
        WaveIRuntimeKit.Tick(runtime, ref tick, 10);
        Assert.True(Cast(runtime, A, 1).Accepted);

        runtime.DetachRemoteSession(A);
        WaveIRuntimeKit.Tick(runtime, ref tick, 20);
        runtime.AttachRemoteSession(B, Character);

        Assert.False(MendReady(runtime, B));
    }

    // Two live seats can share a CharacterKey (wave 16), so a second seat must not be a way around it:
    // the record is written as the cooldown starts, not on detach, and the joining seat inherits it.
    [Fact]
    public void ASecondLiveSeatOnTheSameCharacter_InheritsTheRunningCooldown()
    {
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        runtime.AttachRemoteSession(A, Character);
        ulong tick = 0;
        WaveIRuntimeKit.Tick(runtime, ref tick, 10);
        Assert.True(Cast(runtime, A, 1).Accepted);

        runtime.AttachRemoteSession(B, Character);

        Assert.False(MendReady(runtime, B));
        Assert.False(MendReady(runtime, A));
    }

    // Control: a cooldown is still a time cost that runs down on its own, and a reconnect does not
    // stretch it either. After the full 320 ticks the returning player casts again.
    [Fact]
    public void AReconnectingPlayer_CanCastAgainOnceTheCooldownHasRun()
    {
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        runtime.AttachRemoteSession(A, Character);
        ulong tick = 0;
        WaveIRuntimeKit.Tick(runtime, ref tick, 10);
        Assert.True(Cast(runtime, A, 1).Accepted);

        runtime.DetachRemoteSession(A);
        WaveIRuntimeKit.Tick(runtime, ref tick, 20);
        runtime.AttachRemoteSession(A, Character);
        WaveIRuntimeKit.Tick(runtime, ref tick, MendCooldownTicks);

        Assert.True(MendReady(runtime, A));
        Assert.True(Cast(runtime, A, 2).Accepted);
    }

    // Control: a cooldown that expired while the player was away is not restored, so a long absence
    // costs nothing extra -- a bad connection is not punished beyond what was already owed.
    [Fact]
    public void ACooldownThatExpiredWhileAway_IsNotRestoredOnRejoin()
    {
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        runtime.AttachRemoteSession(A, Character);
        ulong tick = 0;
        WaveIRuntimeKit.Tick(runtime, ref tick, 10);
        Assert.True(Cast(runtime, A, 1).Accepted);

        runtime.DetachRemoteSession(A);
        WaveIRuntimeKit.Tick(runtime, ref tick, MendCooldownTicks + 10);
        runtime.AttachRemoteSession(A, Character);

        Assert.True(MendReady(runtime, A));
        Assert.True(Cast(runtime, A, 2).Accepted);
    }

    // Control: the record is the character's, so another character joining is unaffected by it.
    [Fact]
    public void ASecondCharacter_IsUnaffectedByTheFirstsCooldown()
    {
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        runtime.AttachRemoteSession(A, Character);
        ulong tick = 0;
        WaveIRuntimeKit.Tick(runtime, ref tick, 10);
        Assert.True(Cast(runtime, A, 1).Accepted);

        runtime.AttachRemoteSession(C, OtherCharacter);

        Assert.True(MendReady(runtime, C));
        Assert.True(Cast(runtime, C, 1).Accepted);
        Assert.False(MendReady(runtime, A));
    }

    // Control: a first join still starts clean -- nothing is inherited from a character that never cast.
    [Fact]
    public void AFirstJoin_StartsWithNoCooldownRunning()
    {
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        runtime.AttachRemoteSession(A, Character);

        Assert.True(MendReady(runtime, A));
        Assert.True(Cast(runtime, A, 1).Accepted);
    }
}
