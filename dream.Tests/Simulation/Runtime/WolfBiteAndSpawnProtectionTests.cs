using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Combat;
using Dream.Simulation.Determinism;
using Dream.Simulation.Enemies;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Integration;

namespace Dream.Tests.Simulation.Runtime;

// Wolf bite and spawn protection: a chasing wolf stops inside its bite range, so a standing player is
// bitten; a player who spawns (first spawn, join or respawn) is ignored by enemies for
// PlayerRespawnRules.SpawnProtectionTicks ticks, and attacking ends that early. A wolf with no target
// walks back to its home, so tests that need it to stand still put the player beside the home.
public sealed class WolfBiteAndSpawnProtectionTests
{
    private const int Protection = PlayerRespawnRules.SpawnProtectionTicks;
    private const int BigHealth = 100_000;

    private static readonly PlayerSessionId A = new("prot-a");
    private static readonly PlayerSessionId B = new("prot-b");
    private static readonly Vector3 WolfOffset = new(4f, -0.5f, 0f);

    private static float Horizontal(Vector3 a, Vector3 b)
    {
        Vector3 d = a - b;
        d.Y = 0f;
        return d.Length;
    }

    private static WorldSessionRuntime Create(out IAuthoritativePlayerTarget target)
    {
        WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        runtime.AttachRemoteSession(A, A.Value);
        target = WaveIRuntimeKit.Target(runtime, A.Value);
        target.Combatant.Health.SetAuthoritativeState(BigHealth, BigHealth);
        // These tests are about the bite and the respawn window; the join window has its own tests.
        WaveIRuntimeKit.EndProtection(runtime, A);
        return runtime;
    }

    private static EnemyActor Wolf(WorldSessionRuntime runtime) => runtime.Enemies.ActiveActors.First(e => e.IsActive && e.EnemyKind == "Wolf");

    private static void Place(EnemyActor enemy, IAuthoritativePlayerTarget target, Vector3 offset) => WaveIRuntimeKit.PlaceBeside(enemy, target, offset);

    // ---- the bite ----

    private static void AssertBitesAStandingPlayer(WorldSessionRuntime runtime, IAuthoritativePlayerTarget target, EnemyActor enemy, ulong tick)
    {
        Place(enemy, target, WolfOffset);
        Vector3 playerAt = target.Position;
        float minDistance = float.MaxValue;
        bool bitten = false;
        List<Vector3> late = [];
        for (int i = 0; i < 400; i++)
        {
            WaveIRuntimeKit.Tick(runtime, ref tick);
            minDistance = Math.Min(minDistance, Horizontal(enemy.Position, target.Position));
            bitten |= target.Combatant.Health.CurrentHealth < BigHealth;
            if (i >= 300)
            {
                late.Add(enemy.Position);
            }
        }

        Assert.True(bitten, "the wolf never bit the standing player");
        Assert.True(Horizontal(playerAt, target.Position) < 0.01f, "the wolf pushed the player");
        Assert.True(minDistance >= WolfDefinition.PlayerSeparationDistance, $"the wolf pushed into the player: {minDistance}");
        Assert.InRange(Horizontal(enemy.Position, target.Position), WolfDefinition.PlayerSeparationDistance, enemy.AttackRange);
        Assert.Equal(EnemyBehaviorState.Attacking, enemy.BehaviorState);
        for (int i = 1; i < late.Count; i++)
        {
            Assert.True((late[i] - late[i - 1]).Length < 0.001f, "the wolf jitters at the stop point");
        }
    }

    [Fact]
    public void AWolf_BitesAStationaryPlayer_AndStopsInsideBiteRangeWithoutJitterOrPush()
    {
        using WorldSessionRuntime runtime = Create(out IAuthoritativePlayerTarget target);
        AssertBitesAStandingPlayer(runtime, target, Wolf(runtime), 0);
    }

    [Fact]
    public void ADireWolf_BitesAStationaryPlayer()
    {
        WorldClockSettings oneMinuteDay = WorldClockSettings.FromDayLengthMinutes(1);
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        runtime.AttachRemoteSession(A, A.Value);
        WaveIRuntimeKit.EndProtection(runtime, A);
        long nightfall = WaveT2NightSpawnTests.WorldTicksAt(oneMinuteDay, dayIndex: 0, hour: 20);
        runtime.ConfigureWorldClock(oneMinuteDay, nightfall - 4);
        IAuthoritativePlayerTarget target = WaveIRuntimeKit.Target(runtime, A.Value);
        target.Combatant.Health.SetAuthoritativeState(BigHealth, BigHealth);
        ulong tick = 0;
        WaveIRuntimeKit.Tick(runtime, ref tick, 12);
        EnemyActor dire = runtime.Enemies.ActiveActors.First(e => e.IsActive && e.EnemyKind == "DireWolf");
        AssertBitesAStandingPlayer(runtime, target, dire, tick);
    }

    [Fact]
    public void TheStandOffLiesInsideTheAttackRange_ForAnyTemplate()
    {
        Assert.InRange(WolfDefinition.PreferredStandOffDistance, WolfDefinition.PlayerSeparationDistance + 0.1f, 1.35f - 0.1f);
        // Ranges the content catalog accepts: above the separation distance over the stand-off fraction.
        float minimum = WolfDefinition.PlayerSeparationDistance / EnemyActor.StandOffRangeFraction;
        foreach (float range in new[] { minimum + 0.01f, 1.35f, 3f })
        {
            EnemyActor actor = new(new EnemyCombatant(() => Vector3.Zero), Vector3.Zero, 10f, range, 2f, preferredStandOffDistance: 2f);
            Assert.True(actor.PreferredStandOffDistance < actor.AttackRange);
            Assert.True(actor.PreferredStandOffDistance >= WolfDefinition.PlayerSeparationDistance, $"range {range}");
        }
    }

    // ---- spawn protection ----

    // Kills the player and ticks until the respawn. The wolf waits at its home, out of range.
    private static void DieAndRespawn(WorldSessionRuntime runtime, IAuthoritativePlayerTarget target, ref ulong tick)
    {
        EnemyActor wolf = Wolf(runtime);
        wolf.SetPosition(wolf.HomePosition);
        WaveIRuntimeKit.Kill(runtime, A.Value);
        int guard = 0;
        while (!target.IsAlive)
        {
            WaveIRuntimeKit.Tick(runtime, ref tick);
            Assert.True(++guard < 1000);
        }
    }

    [Theory]
    [InlineData(4f)]
    [InlineData(1.0f)]
    public void ARespawnedPlayer_IsNeitherTargetedNorBitten_ForExactly192Ticks(float wolfDistance)
    {
        Assert.Equal(192, Protection);
        using WorldSessionRuntime runtime = Create(out IAuthoritativePlayerTarget target);
        ulong tick = 0;
        WaveIRuntimeKit.Tick(runtime, ref tick, 5);
        DieAndRespawn(runtime, target, ref tick);
        EnemyActor wolf = Wolf(runtime);
        int health = target.Combatant.Health.CurrentHealth;
        Place(wolf, target, new Vector3(wolfDistance, -0.5f, 0f));
        Vector3 wolfAt = wolf.Position;

        for (int i = 1; i <= Protection; i++)
        {
            WaveIRuntimeKit.Tick(runtime, ref tick);
            Assert.True(wolf.BehaviorState == EnemyBehaviorState.Idle, $"i={i} {wolf.BehaviorState}");
            Assert.Equal(health, target.Combatant.Health.CurrentHealth);
        }

        Assert.Equal(wolfAt, wolf.Position);

        // Tick 193 after the respawn: the wolf targets the player.
        WaveIRuntimeKit.Tick(runtime, ref tick);
        Assert.NotEqual(EnemyBehaviorState.Idle, wolf.BehaviorState);
        for (int i = 0; i < 200 && target.Combatant.Health.CurrentHealth == health; i++)
        {
            WaveIRuntimeKit.Tick(runtime, ref tick);
        }

        Assert.True(target.Combatant.Health.CurrentHealth < health, "the wolf never bit after the protection ended");
    }

    [Fact]
    public void TheFirstSpawn_IsProtectedFor192Ticks_ThenBitten()
    {
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        runtime.AttachRemoteSession(A, A.Value);
        IAuthoritativePlayerTarget target = WaveIRuntimeKit.Target(runtime, A.Value);
        target.Combatant.Health.SetAuthoritativeState(BigHealth, BigHealth);
        EnemyActor wolf = Wolf(runtime);
        ulong tick = 0;
        Place(wolf, target, new Vector3(1.0f, -0.5f, 0f));
        for (int i = 1; i <= Protection; i++)
        {
            WaveIRuntimeKit.Tick(runtime, ref tick);
            Assert.Equal(EnemyBehaviorState.Idle, wolf.BehaviorState);
            Assert.Equal(BigHealth, target.Combatant.Health.CurrentHealth);
        }

        WaveIRuntimeKit.Tick(runtime, ref tick);
        Assert.NotEqual(EnemyBehaviorState.Idle, wolf.BehaviorState);
        for (int i = 0; i < 200 && target.Combatant.Health.CurrentHealth == BigHealth; i++)
        {
            WaveIRuntimeKit.Tick(runtime, ref tick);
        }

        Assert.True(target.Combatant.Health.CurrentHealth < BigHealth, "the wolf never bit after the first spawn's window");
    }

    [Fact]
    public void ARepeatedJoinOfAnAttachedSeat_DoesNotStartANewWindow()
    {
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        runtime.AttachRemoteSession(A, A.Value);
        ulong tick = 0;
        WaveIRuntimeKit.Tick(runtime, ref tick, Protection + 10);
        runtime.AttachRemoteSession(A, A.Value);
        IAuthoritativePlayerTarget target = WaveIRuntimeKit.Target(runtime, A.Value);
        target.Combatant.Health.SetAuthoritativeState(BigHealth, BigHealth);
        EnemyActor wolf = Wolf(runtime);
        Place(wolf, target, new Vector3(1.0f, -0.5f, 0f));
        WaveIRuntimeKit.Tick(runtime, ref tick);
        Assert.NotEqual(EnemyBehaviorState.Idle, wolf.BehaviorState);
    }

    [Fact]
    public void AProtectedSecondarySeat_NextToTheWolf_IsSkipped_AndTheWolfBitesTheUnprotectedPlayerFartherAway()
    {
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        IAuthoritativePlayerTarget unprotected = Create(runtime, A);
        // B joins second, so it is a secondary seat, and its join window protects it.
        runtime.AttachRemoteSession(B, B.Value);
        IAuthoritativePlayerTarget protectedSeat = WaveIRuntimeKit.Target(runtime, B.Value);
        protectedSeat.Combatant.Health.SetAuthoritativeState(BigHealth, BigHealth);
        EnemyActor wolf = Wolf(runtime);
        wolf.SetPosition(wolf.HomePosition);
        protectedSeat.SetWorldPosition(wolf.Position + new Vector3(-1.2f, 0.5f, 0f));
        unprotected.SetWorldPosition(wolf.Position + new Vector3(0f, 0.5f, -3f));
        ulong tick = 0;
        int ticks = 0;
        while (unprotected.Combatant.Health.CurrentHealth == BigHealth && ticks++ < 150)
        {
            WaveIRuntimeKit.Tick(runtime, ref tick);
            Assert.Equal(BigHealth, protectedSeat.Combatant.Health.CurrentHealth);
        }

        Assert.True(unprotected.Combatant.Health.CurrentHealth < BigHealth, "the wolf never switched to the unprotected player");
        Assert.True(tick < (ulong)Protection, "the bite came after B's window ended; the test proves nothing");
        Assert.Equal(BigHealth, protectedSeat.Combatant.Health.CurrentHealth);
    }

    [Fact]
    public void AWolfOverlappingAProtectedPlayer_DoesNotBiteIt_ShovesItClear_AndStaysFinite()
    {
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        runtime.AttachRemoteSession(A, A.Value);
        IAuthoritativePlayerTarget target = WaveIRuntimeKit.Target(runtime, A.Value);
        target.Combatant.Health.SetAuthoritativeState(BigHealth, BigHealth);
        EnemyActor wolf = Wolf(runtime);
        wolf.SetPosition(wolf.HomePosition);
        target.SetWorldPosition(wolf.Position + new Vector3(0.2f, 0.5f, 0f));
        ulong tick = 0;
        for (int i = 1; i <= Protection; i++)
        {
            WaveIRuntimeKit.Tick(runtime, ref tick);
            Assert.True(float.IsFinite(target.Position.X) && float.IsFinite(target.Position.Z), $"i={i} {target.Position}");
            Assert.Equal(BigHealth, target.Combatant.Health.CurrentHealth);
            Assert.Equal(EnemyBehaviorState.Idle, wolf.BehaviorState);
            Assert.Equal(wolf.HomePosition, wolf.Position);
        }

        Assert.True(Horizontal(wolf.Position, target.Position) >= WolfDefinition.PlayerSeparationDistance - 0.01f,
            $"the protected player was left inside the wolf: {Horizontal(wolf.Position, target.Position)}");
        for (int i = 0; i < 200 && target.Combatant.Health.CurrentHealth == BigHealth; i++)
        {
            WaveIRuntimeKit.Tick(runtime, ref tick);
        }

        Assert.True(target.Combatant.Health.CurrentHealth < BigHealth, "never bitten after the window");
    }

    private static IAuthoritativePlayerTarget Create(WorldSessionRuntime runtime, PlayerSessionId id)
    {
        runtime.AttachRemoteSession(id, id.Value);
        IAuthoritativePlayerTarget seat = WaveIRuntimeKit.Target(runtime, id.Value);
        seat.Combatant.Health.SetAuthoritativeState(BigHealth, BigHealth);
        WaveIRuntimeKit.EndProtection(runtime, id);
        return seat;
    }

    [Fact]
    public void AttackingEndsTheProtection()
    {
        using WorldSessionRuntime runtime = Create(out IAuthoritativePlayerTarget target);
        ulong tick = 0;
        WaveIRuntimeKit.Tick(runtime, ref tick, 5);
        DieAndRespawn(runtime, target, ref tick);
        EnemyActor wolf = Wolf(runtime);
        Place(wolf, target, WolfOffset);
        WaveIRuntimeKit.EnterCombat(runtime, A);

        WaveIRuntimeKit.Tick(runtime, ref tick, 10);
        Assert.Equal(EnemyBehaviorState.Idle, wolf.BehaviorState);

        // A swing away from the wolf.
        AuthoritativeActionResult swing = runtime.ProcessAuthoritativeAction(
            new PlayerActionMessage(A, 1, new CombatUseIntent(target.EyePosition, -Vector3.UnitX)));
        Assert.True(swing.Accepted);
        WaveIRuntimeKit.Tick(runtime, ref tick);
        Assert.NotEqual(EnemyBehaviorState.Idle, wolf.BehaviorState);
    }

    [Fact]
    public void ASecondRespawn_StartsTheProtectionAgain()
    {
        using WorldSessionRuntime runtime = Create(out IAuthoritativePlayerTarget target);
        ulong tick = 0;
        WaveIRuntimeKit.Tick(runtime, ref tick, 5);
        DieAndRespawn(runtime, target, ref tick);
        WaveIRuntimeKit.Tick(runtime, ref tick, Protection + 20);

        DieAndRespawn(runtime, target, ref tick);
        EnemyActor wolf = Wolf(runtime);
        Place(wolf, target, WolfOffset);
        for (int i = 1; i <= Protection; i++)
        {
            WaveIRuntimeKit.Tick(runtime, ref tick);
            Assert.Equal(EnemyBehaviorState.Idle, wolf.BehaviorState);
        }

        WaveIRuntimeKit.Tick(runtime, ref tick);
        Assert.NotEqual(EnemyBehaviorState.Idle, wolf.BehaviorState);
    }

    [Fact]
    public void TwoServersInLockstep_AgreeThroughDeathRespawnProtectionAndTheFirstBite()
    {
        using WorldSessionRuntime first = Create(out IAuthoritativePlayerTarget t1);
        using WorldSessionRuntime second = Create(out IAuthoritativePlayerTarget t2);
        ulong tick1 = 0;
        ulong tick2 = 0;
        for (int i = 0; i < 1100; i++)
        {
            if (i == 20)
            {
                WaveIRuntimeKit.Kill(first, A.Value);
                WaveIRuntimeKit.Kill(second, A.Value);
            }

            if (i == 20 + PlayerRespawnRules.DelayTicks + 3)
            {
                Place(Wolf(first), t1, WolfOffset);
                Place(Wolf(second), t2, WolfOffset);
            }

            WaveIRuntimeKit.Tick(first, ref tick1);
            WaveIRuntimeKit.Tick(second, ref tick2);
            Assert.Equal(SimulationStateChecksum.Compute(first), SimulationStateChecksum.Compute(second));
            Assert.Equal(t1.Combatant.Health.CurrentHealth, t2.Combatant.Health.CurrentHealth);
        }

        Assert.True(t1.Combatant.Health.CurrentHealth < t1.Combatant.Health.MaxHealth, "the scenario never reached a bite");
    }

    [Fact]
    public void ATickWhileProtectedWithAWolfBeside_AllocatesNothing()
    {
        using WorldSessionRuntime runtime = Create(out IAuthoritativePlayerTarget target);
        ulong tick = 0;
        WaveIRuntimeKit.Tick(runtime, ref tick, 5);
        DieAndRespawn(runtime, target, ref tick);
        Place(Wolf(runtime), target, new Vector3(1f, -0.5f, 0f));
        WaveIRuntimeKit.Tick(runtime, ref tick, 10);
        ulong start = tick;
        long before = GC.GetAllocatedBytesForCurrentThread();
        WaveIRuntimeKit.Tick(runtime, ref tick, 40);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, bytes);
        Assert.True(tick - start < (ulong)Protection, "the measurement outlasted the protection");
    }
}
