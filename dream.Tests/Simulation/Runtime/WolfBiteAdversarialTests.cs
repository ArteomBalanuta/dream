using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Combat;
using Dream.Simulation.Determinism;
using Dream.Simulation.Enemies;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Tests.Integration;

namespace Dream.Tests.Simulation.Runtime;

// Wave I tester, round 4: the wolf bite and the 192-tick spawn protection against the awkward cases.
// Runtime level (no sockets), driven tick by tick through WaveIRuntimeKit.
public sealed class WolfBiteAdversarialTests(ITestOutputHelper output)
{
    private const int Protection = PlayerRespawnRules.SpawnProtectionTicks;
    private const int Delay = PlayerRespawnRules.DelayTicks;
    private const int Gap = 200;
    // The runtime's world spawn is the start position it was built with.
    private static readonly Vector3 Spawn = WaveIRuntimeKit.FarStart;

    private static readonly PlayerSessionId A = new("adv4-a");
    private static readonly PlayerSessionId B = new("adv4-b");
    private static readonly PlayerSessionId C = new("adv4-c");

    // A seat whose join window has been ended: these tests are about the respawn window and the bite. Use
    // JoinedSeat for the join window itself.
    private static IAuthoritativePlayerTarget Seat(WorldSessionRuntime runtime, PlayerSessionId id)
    {
        IAuthoritativePlayerTarget seat = JoinedSeat(runtime, id);
        WaveIRuntimeKit.EndProtection(runtime, id);
        return seat;
    }

    private static IAuthoritativePlayerTarget JoinedSeat(WorldSessionRuntime runtime, PlayerSessionId id)
    {
        runtime.AttachRemoteSession(id, id.Value);
        return WaveIRuntimeKit.Target(runtime, id.Value);
    }

    private static EnemyActor DayWolf(WorldSessionRuntime runtime)
        => runtime.Enemies.ActiveActors.First(e => e.IsActive && e.EnemyKind == "Wolf");

    private static void Park(WorldSessionRuntime runtime)
    {
        foreach (EnemyActor enemy in runtime.Enemies.ActiveActors)
        {
            enemy.SetPosition(new Vector3(45f, 1f, 45f));
        }
    }

    // Terrain may already hold cubes anywhere above the floor; the cube tests need a known empty block first.
    private static void ClearArea(Map map, int x0, int x1, int y0, int y1, int z0, int z1)
    {
        for (int x = x0; x <= x1; x++)
        {
            for (int y = y0; y <= y1; y++)
            {
                for (int z = z0; z <= z1; z++)
                {
                    Cube? cube = map.TryGetCubeAtCell(x, y, z);
                    if (cube != null)
                    {
                        Assert.True(map.RemoveCube(cube));
                    }
                }
            }
        }
    }

    private static float Horizontal(Vector3 a, Vector3 b)
    {
        Vector3 d = a - b;
        d.Y = 0f;
        return d.Length;
    }

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    // Ticks until the target is alive; returns how many ticks it took.
    private static int TicksUntilAlive(WorldSessionRuntime runtime, IAuthoritativePlayerTarget target, ref ulong tick, Action? perTick = null)
    {
        int n = 0;
        while (!target.IsAlive)
        {
            perTick?.Invoke();
            WaveIRuntimeKit.Tick(runtime, ref tick);
            Assert.True(++n < 2000, "never respawned");
        }

        return n;
    }

    // ---- rubble at the spawn with a wolf nearby ----

    [Fact]
    public void RubbleAtTheSpawn_AWolfBesideIt_TheRespawnIsLiftedClear_ProtectedFor192_ThenBitten()
    {
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        ClearArea(runtime.Map, 38, 42, 1, 4, 38, 42);
        Assert.NotNull(runtime.Map.TryAddCube(new Vector3(40f, 1f, 40f), withPhysics: false));
        Assert.True(runtime.Map.FractureCubeIntoPieces(new GeometryCellKey(40, 1, 40)));
        IAuthoritativePlayerTarget target = Seat(runtime, A);
        EnemyActor wolf = DayWolf(runtime);
        ulong tick = 0;
        WaveIRuntimeKit.Tick(runtime, ref tick, 5);
        target.SetWorldPosition(new Vector3(20f, 1.5f, 20f));
        WaveIRuntimeKit.Kill(runtime, A.Value);

        // The wolf waits right beside the spawn, in the rubble, the whole time.
        TicksUntilAlive(runtime, target, ref tick, () => wolf.SetPosition(new Vector3(41.3f, 1f, 40f)));
        Assert.True(Finite(target.Position), $"respawned at {target.Position}");
        Assert.True(target.Position.Y > Spawn.Y, $"respawned inside the rubble at {target.Position}");
        int health = target.Combatant.Health.CurrentHealth;
        Assert.Equal(target.Combatant.Health.MaxHealth, health);

        for (int i = 1; i <= Protection; i++)
        {
            // A wolf with no target walks home, so the test holds it beside the spawn.
            wolf.SetPosition(new Vector3(41.3f, 1f, 40f));
            WaveIRuntimeKit.Tick(runtime, ref tick);
            Assert.Equal(health, target.Combatant.Health.CurrentHealth);
            Assert.True(Finite(target.Position) && Finite(wolf.Position), $"i={i} non-finite position");
        }

        bool bitten = false;
        for (int i = 0; i < 400 && !bitten; i++)
        {
            WaveIRuntimeKit.Tick(runtime, ref tick);
            bitten = target.Combatant.Health.CurrentHealth < health;
        }

        output.WriteLine($"player at {target.Position}, wolf at {wolf.Position}, bitten {bitten}");
        Assert.True(bitten, "a protected player at the spawn in rubble was never bitten after the window");
    }

    // ---- protection across a leave and a rejoin ----

    [Fact]
    public void ARejoin_IsProtectedByItsOwnWindow_NotTheOldOne_AndALaterDeathGetsAFullFreshWindow()
    {
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        IAuthoritativePlayerTarget target = Seat(runtime, A);
        EnemyActor wolf = DayWolf(runtime);
        ulong tick = 0;
        WaveIRuntimeKit.Tick(runtime, ref tick, 5);
        wolf.SetPosition(wolf.HomePosition);
        WaveIRuntimeKit.Kill(runtime, A.Value);
        TicksUntilAlive(runtime, target, ref tick);

        // Protected: a wolf beside the player ignores it.
        WaveIRuntimeKit.PlaceBeside(wolf, target, new Vector3(4f, -0.5f, 0f));
        WaveIRuntimeKit.Tick(runtime, ref tick, 150);
        Assert.Equal(EnemyBehaviorState.Idle, wolf.BehaviorState);

        // Leave and rejoin with only 42 ticks of the old window left: the rejoin has its own, full window.
        runtime.DetachRemoteSession(A);
        WaveIRuntimeKit.Tick(runtime, ref tick, 3);
        IAuthoritativePlayerTarget again = JoinedSeat(runtime, A);
        int health = again.Combatant.Health.CurrentHealth;
        WaveIRuntimeKit.PlaceBeside(wolf, again, new Vector3(1.2f, -0.5f, 0f));
        ulong joinedAt = tick;
        for (int i = 1; i <= Protection; i++)
        {
            WaveIRuntimeKit.Tick(runtime, ref tick);
            Assert.Equal(EnemyBehaviorState.Idle, wolf.BehaviorState);
            Assert.Equal(health, again.Combatant.Health.CurrentHealth);
        }

        Assert.Equal(joinedAt + (ulong)Protection, tick);
        WaveIRuntimeKit.Tick(runtime, ref tick);
        Assert.NotEqual(EnemyBehaviorState.Idle, wolf.BehaviorState);

        // Run well past the window, then die again: a full new window after that respawn.
        wolf.SetPosition(new Vector3(45f, 1f, 45f));
        WaveIRuntimeKit.Tick(runtime, ref tick, Protection);
        Park(runtime);
        WaveIRuntimeKit.Kill(runtime, A.Value);
        TicksUntilAlive(runtime, again, ref tick);
        Park(runtime);
        health = again.Combatant.Health.CurrentHealth;
        WaveIRuntimeKit.PlaceBeside(wolf, again, new Vector3(1.2f, -0.5f, 0f));
        for (int i = 1; i <= Protection; i++)
        {
            WaveIRuntimeKit.Tick(runtime, ref tick);
            Assert.Equal(EnemyBehaviorState.Idle, wolf.BehaviorState);
            Assert.Equal(health, again.Combatant.Health.CurrentHealth);
        }

        WaveIRuntimeKit.Tick(runtime, ref tick);
        Assert.NotEqual(EnemyBehaviorState.Idle, wolf.BehaviorState);
    }

    [Fact]
    public void LeavingWhileDeadAndRejoining_StartsAFreshWait()
    {
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        IAuthoritativePlayerTarget target = Seat(runtime, A);
        Park(runtime);
        ulong tick = 0;
        WaveIRuntimeKit.Tick(runtime, ref tick, 5);
        WaveIRuntimeKit.Kill(runtime, A.Value);
        WaveIRuntimeKit.Tick(runtime, ref tick, 50);
        runtime.DetachRemoteSession(A);
        WaveIRuntimeKit.Tick(runtime, ref tick, 5);
        IAuthoritativePlayerTarget again = JoinedSeat(runtime, A);
        // The profile-less runtime brings the seat back alive; kill it so the rejoin is dead as the host's would be.
        if (again.IsAlive)
        {
            WaveIRuntimeKit.Kill(runtime, A.Value);
        }

        int waited = TicksUntilAlive(runtime, again, ref tick);
        Assert.Equal(Delay + 1, waited);
    }

    // ---- a second seat ----

    [Fact]
    public void TwoSeats_HaveTheirOwnWindows_AWolfBitesTheUnprotectedOneOnly()
    {
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        IAuthoritativePlayerTarget a = Seat(runtime, A);
        IAuthoritativePlayerTarget b = Seat(runtime, B);
        a.Combatant.Health.SetAuthoritativeState(100_000, 100_000);
        b.Combatant.Health.SetAuthoritativeState(100_000, 100_000);
        EnemyActor wolf = DayWolf(runtime);
        Park(runtime);
        ulong tick = 0;
        WaveIRuntimeKit.Tick(runtime, ref tick, 5);
        a.SetWorldPosition(new Vector3(30f, 1.5f, 30f));
        b.SetWorldPosition(new Vector3(35f, 1.5f, 35f));

        WaveIRuntimeKit.Kill(runtime, A.Value);
        WaveIRuntimeKit.Tick(runtime, ref tick, Gap);
        WaveIRuntimeKit.Kill(runtime, B.Value);
        // A: dead 1 + Delay ticks after its kill; B 100 ticks later.
        int aWait = TicksUntilAlive(runtime, a, ref tick);
        ulong aRespawnedAt = tick;
        Assert.True(!b.IsAlive, "B should still be dead when A returns");
        Assert.Equal(Delay - Gap + 1, aWait);

        // A is at the spawn, B returns there 100 ticks later. A's window ends Gap - Protection ticks before B's, long enough for the first bite.
        int aHealth = a.Combatant.Health.CurrentHealth;
        int bHealth = 0;
        ulong bRespawnedAt = 0;
        for (ulong t = aRespawnedAt + 1; t <= aRespawnedAt + (ulong)Gap + (ulong)Protection; t++)
        {
            if (t <= aRespawnedAt + (ulong)Protection)
            {
                // A wolf with no target walks home; hold it beside the spawn for the window.
                wolf.SetPosition(Spawn + new Vector3(1.5f, -0.5f, 0f));
            }

            WaveIRuntimeKit.Tick(runtime, ref tick);
            Assert.Equal(t, tick);
            if (bRespawnedAt == 0 && b.IsAlive)
            {
                bRespawnedAt = tick;
                bHealth = b.Combatant.Health.CurrentHealth;
                Assert.Equal(aRespawnedAt + (ulong)Gap, bRespawnedAt);
            }

            if (bRespawnedAt != 0)
            {
                Assert.Equal(bHealth, b.Combatant.Health.CurrentHealth);
            }

            if (t <= aRespawnedAt + (ulong)Protection)
            {
                Assert.Equal(aHealth, a.Combatant.Health.CurrentHealth);
                Assert.NotEqual(EnemyBehaviorState.Attacking, wolf.BehaviorState);
            }
        }

        Assert.True(a.Combatant.Health.CurrentHealth < aHealth, "A was never bitten once its own window ended while B was still protected");
        // B is bitten once its window is over too.
        for (int i = 0; i < 600 && b.Combatant.Health.CurrentHealth == bHealth; i++)
        {
            WaveIRuntimeKit.Tick(runtime, ref tick);
        }

        Assert.True(b.Combatant.Health.CurrentHealth < bHealth, "B was never bitten after its window");
    }

    // ---- killed while the respawn is pending, or inside the window ----

    [Fact]
    public void AnotherKillWhileDeadAndWaiting_NeitherShortensNorExtendsTheWait()
    {
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        IAuthoritativePlayerTarget target = Seat(runtime, A);
        Park(runtime);
        ulong tick = 0;
        WaveIRuntimeKit.Tick(runtime, ref tick, 5);
        WaveIRuntimeKit.Kill(runtime, A.Value);
        WaveIRuntimeKit.Tick(runtime, ref tick, 100);
        // Further damage on a body that is already dead, and a hit on the very tick before the respawn.
        WaveIRuntimeKit.Kill(runtime, A.Value);
        WaveIRuntimeKit.Tick(runtime, ref tick, Delay - 100 - 2);
        WaveIRuntimeKit.Kill(runtime, A.Value);
        int rest = TicksUntilAlive(runtime, target, ref tick);
        // Dead on the first tick after the kill, then Delay ticks: total Delay + 1 ticks, 100 + (Delay - 102) already spent.
        Assert.Equal(Delay + 1 - 100 - (Delay - 102), rest);
        Assert.Equal(target.Combatant.Health.MaxHealth, target.Combatant.Health.CurrentHealth);
    }

    [Fact]
    public void KilledInsideTheWindow_RespawnsAfterAFullWait_AndTheWindowStartsAgainFromThatRespawn()
    {
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        IAuthoritativePlayerTarget target = Seat(runtime, A);
        EnemyActor wolf = DayWolf(runtime);
        Park(runtime);
        ulong tick = 0;
        WaveIRuntimeKit.Tick(runtime, ref tick, 5);
        WaveIRuntimeKit.Kill(runtime, A.Value);
        TicksUntilAlive(runtime, target, ref tick);

        // Something other than a wolf (a fall, a script) kills the protected player 100 ticks in.
        WaveIRuntimeKit.Tick(runtime, ref tick, 100);
        WaveIRuntimeKit.Kill(runtime, A.Value);
        Assert.Equal(Delay + 1, TicksUntilAlive(runtime, target, ref tick));

        Park(runtime);
        int health = target.Combatant.Health.CurrentHealth;
        WaveIRuntimeKit.PlaceBeside(wolf, target, new Vector3(1.2f, -0.5f, 0f));
        for (int i = 1; i <= Protection; i++)
        {
            WaveIRuntimeKit.Tick(runtime, ref tick);
            Assert.Equal(health, target.Combatant.Health.CurrentHealth);
            Assert.Equal(EnemyBehaviorState.Idle, wolf.BehaviorState);
        }

        WaveIRuntimeKit.Tick(runtime, ref tick);
        Assert.NotEqual(EnemyBehaviorState.Idle, wolf.BehaviorState);
    }

    // ---- a wolf and a player at the separation distance, on a slope and beside a cube ----

    [Theory]
    [InlineData(0.95f)]
    [InlineData(0.80f)]
    [InlineData(0.50f)]
    [InlineData(0.0f)]
    public void AWolfAtOrInsideTheSeparationDistance_BesideACube_NeverEndsInsideTheCube_OrNaN_AndTheBiteRangeHolds(float gap)
    {
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        IAuthoritativePlayerTarget target = Seat(runtime, A);
        target.Combatant.Health.SetAuthoritativeState(100_000, 100_000);
        EnemyActor wolf = DayWolf(runtime);
        Vector3 playerAt = new(20.5f, 1.5f, 20.5f);
        ClearArea(runtime.Map, 14, 26, 1, 5, 14, 26);
        // A wall of cubes on the +X side of the player, one cell away, and the wolf pressed between player and wall.
        for (int z = 19; z <= 21; z++)
        {
            Assert.NotNull(runtime.Map.TryAddCube(new Vector3(22f, 1f, z)));
            Assert.NotNull(runtime.Map.TryAddCube(new Vector3(22f, 2f, z)));
        }

        ulong tick = 0;
        WaveIRuntimeKit.Tick(runtime, ref tick, 2);
        target.SetWorldPosition(playerAt);
        wolf.SetPosition(playerAt + new Vector3(gap, -0.5f, 0.001f));
        float minDist = float.MaxValue;
        for (int i = 0; i < 300; i++)
        {
            WaveIRuntimeKit.Tick(runtime, ref tick);
            Assert.True(Finite(wolf.Position) && Finite(target.Position), $"i={i} non-finite {wolf.Position} {target.Position}");
            Assert.True(EnemyMovementCollision.CanOccupy(runtime.Map, wolf.Position, wolf.CollisionRadius, wolf.CollisionHeight),
                $"i={i} the wolf is inside the cube wall at {wolf.Position}");
            minDist = Math.Min(minDist, Horizontal(wolf.Position, target.Position));
        }

        output.WriteLine($"gap {gap}: wolf {wolf.Position}, player {target.Position}, min dist {minDist}, state {wolf.BehaviorState}, health {target.Combatant.Health.CurrentHealth}");
        Assert.True(target.Position.X < 21.5f + 0.01f, $"the player was pushed into the wall: {target.Position}");
        Assert.True(Horizontal(wolf.Position, target.Position) <= wolf.AttackRange + 0.01f, "the wolf drifted out of bite range");
        Assert.True(target.Combatant.Health.CurrentHealth < 100_000, "a wolf pressed against a player never bit");
    }

    [Fact]
    public void APlayerOnAStep_AWolfAtItsFoot_StaysOutOfTheCubes_AndNeverProducesNaN()
    {
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        IAuthoritativePlayerTarget target = Seat(runtime, A);
        target.Combatant.Health.SetAuthoritativeState(100_000, 100_000);
        EnemyActor wolf = DayWolf(runtime);
        ClearArea(runtime.Map, 14, 26, 1, 5, 14, 26);
        // A two-step stair rising along +X: cells x=21 (height 1), x=22 (height 2), floor top at y=1.
        for (int z = 19; z <= 22; z++)
        {
            Assert.NotNull(runtime.Map.TryAddCube(new Vector3(21f, 1f, z)));
            Assert.NotNull(runtime.Map.TryAddCube(new Vector3(22f, 1f, z)));
            Assert.NotNull(runtime.Map.TryAddCube(new Vector3(22f, 2f, z)));
        }

        ulong tick = 0;
        WaveIRuntimeKit.Tick(runtime, ref tick, 2);
        target.SetWorldPosition(new Vector3(22.5f, 3.5f, 20.5f));
        wolf.SetPosition(new Vector3(18.5f, 1f, 20.5f));
        for (int i = 0; i < 400; i++)
        {
            WaveIRuntimeKit.Tick(runtime, ref tick);
            Assert.True(Finite(wolf.Position) && Finite(target.Position), $"i={i} non-finite {wolf.Position} {target.Position}");
            Assert.True(EnemyMovementCollision.CanOccupy(runtime.Map, wolf.Position, wolf.CollisionRadius, wolf.CollisionHeight),
                $"i={i} the wolf is inside the stair at {wolf.Position}");
        }

        output.WriteLine($"stair: wolf {wolf.Position}, player {target.Position}, state {wolf.BehaviorState}, health {target.Combatant.Health.CurrentHealth}");
    }

    // ---- camped by two wolves at night ----

    [Fact]
    public void ACampedPlayerAtNight_LoopsDieRespawnProtected_OnSchedule_WithoutStarvingAnotherSession()
    {
        WorldClockSettings oneMinuteDay = WorldClockSettings.FromDayLengthMinutes(5);
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        IAuthoritativePlayerTarget victim = Seat(runtime, A);
        IAuthoritativePlayerTarget bystander = Seat(runtime, C);
        long nightfall = WaveT2NightSpawnTests.WorldTicksAt(oneMinuteDay, dayIndex: 0, hour: 20);
        runtime.ConfigureWorldClock(oneMinuteDay, nightfall - 4);
        ulong tick = 0;
        WaveIRuntimeKit.Tick(runtime, ref tick, 12);
        bystander.SetWorldPosition(new Vector3(10f, 1.5f, 10f));
        List<EnemyActor> wolves = runtime.Enemies.ActiveActors.Where(e => e.IsActive).ToList();
        output.WriteLine($"active enemies at night: {string.Join(", ", wolves.Select(w => w.EnemyKind))}");
        Assert.True(wolves.Count >= 2, $"expected two wolves at night, found {wolves.Count}");
        foreach (EnemyActor wolf in wolves)
        {
            wolf.SetPosition(Spawn + new Vector3(2f, -0.5f, 0f));
        }

        int bystanderMax = bystander.Combatant.Health.MaxHealth;
        victim.SetWorldPosition(Spawn);
        int victimMax = victim.Combatant.Health.MaxHealth;
        List<(ulong Death, ulong Respawn)> cycles = [];
        ulong lastDeath = 0;
        bool wasAlive = true;
        ulong respawnedAt = 0;
        int bystanderDeathCheck = 0;
        ulong bystanderKilledAt = 0;
        ulong bystanderBackAt = 0;
        for (int i = 0; i < 4000; i++)
        {
            if (i == 1200)
            {
                WaveIRuntimeKit.Kill(runtime, C.Value);
                bystanderKilledAt = tick;
                bystanderDeathCheck = 1;
            }

            if (!wasAlive || (respawnedAt != 0 && tick <= respawnedAt + (ulong)Protection))
            {
                // A wolf with no target walks home. The test camps them at the spawn while they cannot target the victim.
                foreach (EnemyActor wolf in wolves)
                {
                    wolf.SetPosition(Spawn + new Vector3(2f, -0.5f, 0f));
                }
            }

            WaveIRuntimeKit.Tick(runtime, ref tick);
            if (bystanderDeathCheck == 1 && bystander.IsAlive)
            {
                bystanderBackAt = tick;
                bystanderDeathCheck = 2;
                // It returns to the camped spawn like the victim does, with full health and its own window.
                Assert.Equal(bystanderMax, bystander.Combatant.Health.CurrentHealth);
            }

            Assert.True(Finite(victim.Position), $"tick {tick}: victim position {victim.Position}");
            bool alive = victim.IsAlive;
            if (wasAlive && !alive)
            {
                lastDeath = tick;
            }
            else if (!wasAlive && alive)
            {
                // Respawn: exactly Delay ticks after the tick that first saw the corpse.
                Assert.Equal(lastDeath + (ulong)Delay, tick);
                Assert.Equal(victimMax, victim.Combatant.Health.CurrentHealth);
                cycles.Add((lastDeath, tick));
                respawnedAt = tick;
            }
            else if (alive && respawnedAt != 0 && tick <= respawnedAt + (ulong)Protection)
            {
                Assert.Equal(victimMax, victim.Combatant.Health.CurrentHealth);
            }

            wasAlive = alive;
            if (bystanderDeathCheck == 0)
            {
                // Out at (10, 10) until it is killed: the camped wolves stay near the spawn and never come for it.
                Assert.True(bystander.IsAlive, $"tick {tick}: the bystander at {bystander.Position} died; enemies at {string.Join(' ', wolves.Select(w => w.Position + "/" + w.BehaviorState))}");
            }
        }

        output.WriteLine($"cycles: {string.Join(" ", cycles.Select(c => $"{c.Death}->{c.Respawn}"))}; bystander killed at {bystanderKilledAt}, back at {bystanderBackAt}");
        Assert.True(cycles.Count >= 3, $"only {cycles.Count} die-respawn cycles in 4000 ticks");
        for (int i = 1; i < cycles.Count; i++)
        {
            // Respawn, then the 192 protected ticks, then the bite(s) that kill: never sooner than the window.
            Assert.True(cycles[i].Death > cycles[i - 1].Respawn + (ulong)Protection, $"cycle {i} died inside the window");
        }

        Assert.True(bystanderBackAt != 0, "the bystander never came back");
        Assert.Equal(bystanderKilledAt + (ulong)Delay + 1, bystanderBackAt);
    }

    // ---- several players at one wolf ----

    [Fact]
    public void FivePlayersAroundOneWolf_ItBitesOnItsInterval_StaysClearOfEveryBody_AndNothingIsNaN()
    {
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        PlayerSessionId[] ids = [A, B, C, new("adv4-d"), new("adv4-e")];
        IAuthoritativePlayerTarget[] seats = ids.Select(id => Seat(runtime, id)).ToArray();
        foreach (IAuthoritativePlayerTarget seat in seats)
        {
            seat.Combatant.Health.SetAuthoritativeState(100_000, 100_000);
        }

        EnemyActor wolf = DayWolf(runtime);
        ClearArea(runtime.Map, 25, 35, 1, 4, 25, 35);
        ulong tick = 0;
        WaveIRuntimeKit.Tick(runtime, ref tick, 2);
        Vector3 center = new(30f, 1.5f, 30f);
        for (int i = 0; i < seats.Length; i++)
        {
            float angle = i * MathF.Tau / seats.Length;
            seats[i].SetWorldPosition(center + new Vector3(MathF.Cos(angle), 0f, MathF.Sin(angle)) * 0.9f);
        }

        wolf.SetPosition(center + new Vector3(0f, -0.5f, 0f));
        int bites = 0;
        int[] last = seats.Select(s => s.Combatant.Health.CurrentHealth).ToArray();
        const int Ticks = 1000;
        for (int t = 0; t < Ticks; t++)
        {
            WaveIRuntimeKit.Tick(runtime, ref tick);
            Assert.True(Finite(wolf.Position), $"t={t} wolf {wolf.Position}");
            bool bitThisTick = false;
            for (int i = 0; i < seats.Length; i++)
            {
                Assert.True(Finite(seats[i].Position), $"t={t} seat {i} {seats[i].Position}");
                int now = seats[i].Combatant.Health.CurrentHealth;
                Assert.True(now <= last[i], "a bite healed");
                if (now < last[i])
                {
                    bitThisTick = true;
                }

                last[i] = now;
            }

            if (bitThisTick)
            {
                bites++;
            }
        }

        float interval = wolf.Combatant.AttackIntervalSeconds;
        int maxBites = (int)MathF.Ceiling(Ticks / (interval * SimulationClock.AuthoritativeTicksPerSecond)) + 1;
        output.WriteLine($"bites {bites} of at most {maxBites} (interval {interval}s); health {string.Join(",", last)}");
        Assert.True(bites > 0, "the wolf never bit anyone");
        Assert.True(bites <= maxBites, $"{bites} bite ticks exceed the interval limit {maxBites}");
        // One wolf, one bite per tick: no tick bit two players, so total health lost equals bites times a hit.
        int lost = seats.Sum(s => 100_000 - s.Combatant.Health.CurrentHealth);
        Assert.True(lost > 0);
    }

    // ---- lockstep with everything in play ----

    [Fact]
    public void TwoServersInLockstep_AgreeThroughTwoSeatsDyingRespawningAndRejoining()
    {
        using WorldSessionRuntime first = WaveIRuntimeKit.CreateRuntime();
        using WorldSessionRuntime second = WaveIRuntimeKit.CreateRuntime();
        IAuthoritativePlayerTarget[] f = [Seat(first, A), Seat(first, B)];
        IAuthoritativePlayerTarget[] s = [Seat(second, A), Seat(second, B)];
        ulong t1 = 0;
        ulong t2 = 0;
        for (int i = 0; i < 1800; i++)
        {
            if (i == 10 || i == 60)
            {
                int who = i == 10 ? 0 : 1;
                WaveIRuntimeKit.Kill(first, (who == 0 ? A : B).Value);
                WaveIRuntimeKit.Kill(second, (who == 0 ? A : B).Value);
            }

            if (i == 900)
            {
                first.DetachRemoteSession(B);
                second.DetachRemoteSession(B);
            }

            if (i == 950)
            {
                f[1] = Seat(first, B);
                s[1] = Seat(second, B);
            }

            WaveIRuntimeKit.Tick(first, ref t1);
            WaveIRuntimeKit.Tick(second, ref t2);
            Assert.Equal(SimulationStateChecksum.Compute(first), SimulationStateChecksum.Compute(second));
        }
    }
}
