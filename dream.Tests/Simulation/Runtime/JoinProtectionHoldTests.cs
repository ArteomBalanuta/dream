using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Combat;
using Dream.Simulation.Determinism;
using Dream.Simulation.Enemies;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;

namespace Dream.Tests.Simulation.Runtime;

// A join may ask to keep its spawn protection until its first input (the loading screen's "press any
// key" prompt): neutral commands do not release it, the first move does (then the normal window runs),
// a cap bounds it, and attacking ends it. Without the ask a join behaves as before.
public sealed class JoinProtectionHoldTests
{
    private const int Protection = PlayerRespawnRules.SpawnProtectionTicks;
    private const int BigHealth = 100_000;
    private static readonly PlayerSessionId A = new("hold-a");
    private static readonly Vector3 WolfOffset = new(1.0f, -0.5f, 0f);

    private static WorldSessionRuntime Join(bool hold, out IAuthoritativePlayerTarget target, out EnemyActor wolf)
    {
        WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        runtime.AttachRemoteSession(A, A.Value);
        if (hold)
        {
            runtime.HoldSpawnProtectionUntilInput(A);
        }

        target = WaveIRuntimeKit.Target(runtime, A.Value);
        target.Combatant.Health.SetAuthoritativeState(BigHealth, BigHealth);
        wolf = runtime.Enemies.ActiveActors.First(e => e.IsActive && e.EnemyKind == "Wolf");
        WaveIRuntimeKit.PlaceBeside(wolf, target, WolfOffset);
        return runtime;
    }

    private static PlayerCommandFrame Frame(ulong sequence, float forward) => new(
        A,
        sequence,
        default,
        new PlayerMoveIntent(forward, 0f),
        PlayerLookIntent.None,
        PrimaryUsePressed: false);

    private static void TickWith(WorldSessionRuntime runtime, ref ulong tick, PlayerCommandFrame frame)
        => runtime.AdvanceAuthoritativeTick(
            ++tick, SimulationClock.FixedDeltaSeconds, frame, PlayerSimulationTuning.AuthoritativeDefault);

    private static bool Bitten(IAuthoritativePlayerTarget target) => target.Combatant.Health.CurrentHealth < BigHealth;

    [Fact]
    public void AHeldJoin_SendingOnlyNeutralCommands_IsNotBittenForSixtySeconds()
    {
        using WorldSessionRuntime runtime = Join(true, out IAuthoritativePlayerTarget target, out EnemyActor wolf);
        ulong tick = 0;
        for (int i = 1; i <= 60 * SimulationClock.AuthoritativeTicksPerSecond; i++)
        {
            TickWith(runtime, ref tick, Frame((ulong)i, 0f));
            Assert.Equal(EnemyBehaviorState.Idle, wolf.BehaviorState);
        }

        Assert.False(Bitten(target));
    }

    [Fact]
    public void AHeldJoin_IsBittenOnlyAfterTheFirstMovePlusTheNormalWindow()
    {
        using WorldSessionRuntime runtime = Join(true, out IAuthoritativePlayerTarget target, out EnemyActor wolf);
        ulong tick = 0;
        WaveIRuntimeKit.Tick(runtime, ref tick, 1000);
        TickWith(runtime, ref tick, Frame(1, 1f));
        TickWith(runtime, ref tick, Frame(2, 0f));
        WaveIRuntimeKit.PlaceBeside(wolf, target, WolfOffset);
        for (int i = 0; i < Protection - 2; i++)
        {
            WaveIRuntimeKit.Tick(runtime, ref tick);
            Assert.Equal(EnemyBehaviorState.Idle, wolf.BehaviorState);
        }

        WaveIRuntimeKit.Tick(runtime, ref tick, 4);
        Assert.NotEqual(EnemyBehaviorState.Idle, wolf.BehaviorState);
        for (int i = 0; i < 200 && !Bitten(target); i++)
        {
            WaveIRuntimeKit.Tick(runtime, ref tick);
        }

        Assert.True(Bitten(target));
    }

    [Fact]
    public void TheHold_ExpiresAtTheCap()
    {
        using WorldSessionRuntime runtime = Join(true, out IAuthoritativePlayerTarget target, out EnemyActor wolf);
        ulong tick = 0;
        WaveIRuntimeKit.Tick(runtime, ref tick, PlayerRespawnRules.JoinHoldMaxTicks - 1);
        Assert.Equal(EnemyBehaviorState.Idle, wolf.BehaviorState);
        Assert.False(Bitten(target));
        WaveIRuntimeKit.Tick(runtime, ref tick, 4);
        Assert.NotEqual(EnemyBehaviorState.Idle, wolf.BehaviorState);
    }

    [Fact]
    public void Attacking_EndsTheHold()
    {
        using WorldSessionRuntime runtime = Join(true, out IAuthoritativePlayerTarget target, out EnemyActor wolf);
        ulong tick = 0;
        WaveIRuntimeKit.EnterCombat(runtime, A);
        WaveIRuntimeKit.Tick(runtime, ref tick, 500);
        Assert.Equal(EnemyBehaviorState.Idle, wolf.BehaviorState);
        AuthoritativeActionResult swing = runtime.ProcessAuthoritativeAction(
            new PlayerActionMessage(A, 1, new CombatUseIntent(target.EyePosition, -Vector3.UnitX)));
        Assert.True(swing.Accepted);
        WaveIRuntimeKit.Tick(runtime, ref tick);
        Assert.NotEqual(EnemyBehaviorState.Idle, wolf.BehaviorState);
    }

    [Fact]
    public void WithoutTheHold_AnIdleJoin_IsBittenRightAfterTheNormalWindow()
    {
        using WorldSessionRuntime runtime = Join(false, out IAuthoritativePlayerTarget target, out EnemyActor wolf);
        ulong tick = 0;
        WaveIRuntimeKit.Tick(runtime, ref tick, Protection);
        Assert.Equal(EnemyBehaviorState.Idle, wolf.BehaviorState);
        WaveIRuntimeKit.Tick(runtime, ref tick);
        Assert.NotEqual(EnemyBehaviorState.Idle, wolf.BehaviorState);
    }
}
