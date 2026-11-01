using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Enemies;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Tests.Simulation.Runtime;

// Final fix loop of wave I: a wolf bites only a player within its vertical reach and with no cube on the
// line between them. Wolves never change Y, so before this a wolf at the foot of a pillar, under a floor or
// under a sealed room bit the player above it, through the cubes.
public sealed class WolfReachTests
{
    private const int BigHealth = 100_000;
    private static readonly PlayerSessionId A = new("reach-a");

    private sealed class Scene : IDisposable
    {
        public Scene()
        {
            Runtime = WaveIRuntimeKit.CreateRuntime();
            Runtime.AttachRemoteSession(A, A.Value);
            Target = WaveIRuntimeKit.Target(Runtime, A.Value);
            Target.Combatant.Health.SetAuthoritativeState(BigHealth, BigHealth);
            WaveIRuntimeKit.EndProtection(Runtime, A);
            Wolf = Runtime.Enemies.ActiveActors.First(e => e.IsActive && e.EnemyKind == "Wolf");
            ClearArea(Runtime.Map, 14, 28, 1, 10, 14, 28);
        }

        public WorldSessionRuntime Runtime { get; }

        public IAuthoritativePlayerTarget Target { get; }

        public EnemyActor Wolf { get; }

        public ulong Tick;

        public void Cube(int x, int y, int z) => Assert.NotNull(Runtime.Map.TryAddCube(new Vector3(x, y, z), isStatic: true));

        // Puts both bodies down (after the cubes), lets the player settle, and returns the player's position.
        public void Place(Vector3 wolf, Vector3 player)
        {
            Wolf.SetPosition(wolf);
            Target.SetWorldPosition(player);
        }

        public Result Run(int ticks)
        {
            Vector3 start = Target.Position;
            bool attacking = false;
            bool bitten = false;
            float shove = 0f;
            for (int i = 0; i < ticks; i++)
            {
                WaveIRuntimeKit.Tick(Runtime, ref Tick);
                attacking |= Wolf.BehaviorState == EnemyBehaviorState.Attacking;
                bitten |= Target.Combatant.Health.CurrentHealth < BigHealth;
                Assert.True(float.IsFinite(Wolf.Position.X + Wolf.Position.Z + Target.Position.X + Target.Position.Z));
                Assert.True(EnemyMovementCollision.CanOccupy(Runtime.Map, Wolf.Position, Wolf.CollisionRadius, Wolf.CollisionHeight),
                    $"the wolf is inside a cube at {Wolf.Position}");
                Vector3 moved = Target.Position - start;
                moved.Y = 0f;
                shove = Math.Max(shove, moved.Length);
            }

            return new Result(attacking, bitten, shove);
        }

        public void Dispose() => Runtime.Dispose();
    }

    private readonly record struct Result(bool Attacked, bool Bitten, float Shove);

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

    private static void AssertSafe(Result result, string where)
    {
        Assert.False(result.Attacked, $"{where}: the wolf went to Attacking");
        Assert.False(result.Bitten, $"{where}: the player was bitten");
        Assert.True(result.Shove < 0.05f, $"{where}: the player was shoved {result.Shove}");
    }

    [Fact]
    public void ATowerPlayer_IsNotBitten_ByAWolfAtTheFootOfThePillar()
    {
        using Scene scene = new();
        for (int y = 1; y <= 4; y++)
        {
            scene.Cube(22, y, 20);
        }

        // Standing on top, 0.8 m from where the wolf can get to.
        scene.Place(new Vector3(19.5f, 1f, 20.5f), new Vector3(22.5f, 5.5f, 20.5f));
        AssertSafe(scene.Run(400), "tower");
        Assert.True(scene.Wolf.Position.X > 21f, $"the wolf never came to the pillar: {scene.Wolf.Position}");
    }

    [Fact]
    public void APlayerAboveAFloor_IsNotBitten_ByAWolfUnderIt()
    {
        using Scene scene = new();
        // A slab at y=3 carried by two walls (static cubes, or it would fall), open on the z sides so the wolf can walk under it.
        for (int z = 18; z <= 24; z++)
        {
            scene.Cube(18, 1, z);
            scene.Cube(18, 2, z);
            scene.Cube(24, 1, z);
            scene.Cube(24, 2, z);
        }

        for (int x = 18; x <= 24; x++)
        {
            for (int z = 18; z <= 24; z++)
            {
                scene.Cube(x, 3, z);
            }
        }

        scene.Place(new Vector3(21.5f, 1f, 16.5f), new Vector3(21.5f, 4.5f, 20.5f));
        AssertSafe(scene.Run(400), "under-floor");
        Assert.True(scene.Wolf.Position.Z > 18.3f, $"the wolf never came under the floor: {scene.Wolf.Position}");
    }

    [Fact]
    public void APlayerInASealedUpperRoom_IsNotBitten_ByAWolfBelowItsOverhang()
    {
        using Scene scene = new();
        // A pillar carrying a 3 x 3 room: floor at y=4, walls at y=5 and 6, roof at y=7 (the map is 8 high). The floor overhangs the pillar.
        for (int y = 1; y <= 3; y++)
        {
            scene.Cube(22, y, 20);
        }

        for (int x = 21; x <= 23; x++)
        {
            for (int z = 19; z <= 21; z++)
            {
                scene.Cube(x, 4, z);
                scene.Cube(x, 7, z);
                if (x != 22 || z != 20)
                {
                    scene.Cube(x, 5, z);
                    scene.Cube(x, 6, z);
                }
            }
        }

        scene.Place(new Vector3(19.5f, 1f, 20.5f), new Vector3(22.5f, 5.5f, 20.5f));
        AssertSafe(scene.Run(400), "sealed room");
        Assert.True(scene.Wolf.Position.X > 21f, $"the wolf never came under the room: {scene.Wolf.Position}");
    }

    [Fact]
    public void APlayerOnAOneCubeStep_CanStillBeBitten_ByAWolfAtItsFoot()
    {
        using Scene scene = new();
        for (int z = 18; z <= 22; z++)
        {
            scene.Cube(21, 1, z);
        }

        scene.Place(new Vector3(18.5f, 1f, 20.5f), new Vector3(21.5f, 2.5f, 20.5f));
        Result result = scene.Run(300);
        Assert.True(result.Bitten, "the wolf never bit a player on a one-cube step");
        Assert.True(result.Shove < 0.05f, $"the player was shoved off the step: {result.Shove}");
    }

    [Fact]
    public void APlayerOnATwoCubeStep_IsNotBitten_AKnownLimitBecauseWolvesCannotClimb()
    {
        using Scene scene = new();
        for (int z = 18; z <= 22; z++)
        {
            scene.Cube(21, 1, z);
            scene.Cube(21, 2, z);
        }

        scene.Place(new Vector3(18.5f, 1f, 20.5f), new Vector3(21.5f, 3.5f, 20.5f));
        AssertSafe(scene.Run(400), "two-cube step");
    }

    [Fact]
    public void TheSameApproach_OnTheFloor_IsBitten_SoTheTowerAndFloorResultsComeFromTheReachRule()
    {
        using Scene scene = new();
        scene.Place(new Vector3(19.5f, 1f, 20.5f), new Vector3(22.5f, 1.5f, 20.5f));
        Assert.True(scene.Run(300).Bitten);
    }

    // ---- the actor alone ----

    private static EnemyActor Actor(Vector3 position)
        => new(new EnemyCombatant(() => position), position, aggroRange: 10f, attackRange: 1.35f, moveSpeed: 2.8f,
            preferredStandOffDistance: WolfDefinition.PreferredStandOffDistance, collisionRadius: WolfDefinition.CollisionRadius,
            collisionHeight: WolfDefinition.CollisionHeight);

    [Theory]
    [InlineData(1.55f, true)]
    [InlineData(1.65f, false)]
    [InlineData(-0.95f, true)]
    [InlineData(-1.05f, false)]
    [InlineData(0.5f, true)]
    public void TheVerticalReach_IsHalfAMetreBetweenTheBodies(float playerHeightAboveTheWolfsFeet, bool bites)
    {
        EnemyActor wolf = Actor(new Vector3(10.5f, 1f, 10.5f));
        wolf.Update(0.015625f, new Vector3(11.5f, 1f + playerHeightAboveTheWolfsFeet, 10.5f), (Map?)null);
        Assert.Equal(bites ? EnemyBehaviorState.Attacking : EnemyBehaviorState.Idle, wolf.BehaviorState);
    }

    [Fact]
    public void ACubeBetweenTheBodies_BlocksTheBite_AndTheLineIsClearWithout()
    {
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        Map map = runtime.Map;
        ClearArea(map, 18, 24, 1, 4, 18, 24);
        EnemyActor wolf = Actor(new Vector3(20.5f, 1f, 20.5f));
        Vector3 player = new(21.7f, 1.5f, 20.5f);
        wolf.Update(0.015625f, player, map);
        Assert.Equal(EnemyBehaviorState.Attacking, wolf.BehaviorState);

        Assert.NotNull(map.TryAddCube(new Vector3(21f, 1f, 20f)));
        wolf = Actor(new Vector3(20.5f, 1f, 20.5f));
        wolf.Update(0.015625f, player, map);
        Assert.NotEqual(EnemyBehaviorState.Attacking, wolf.BehaviorState);
    }

    // ---- the leash ----

    [Fact]
    public void AWolfWithNoTarget_WalksBackToItsHome_StopsThereAndIdles_TheSameWayEveryTime()
    {
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        Map map = runtime.Map;
        ClearArea(map, 8, 30, 1, 3, 8, 30);
        Vector3 home = new(10.5f, 1f, 10.5f);
        Vector3[] arrivals = new Vector3[2];
        int[] ticks = new int[2];
        for (int run = 0; run < 2; run++)
        {
            EnemyActor wolf = new(new EnemyCombatant(() => home), home, aggroRange: 10f, attackRange: 1.35f, moveSpeed: 2.8f,
                preferredStandOffDistance: WolfDefinition.PreferredStandOffDistance, collisionRadius: WolfDefinition.CollisionRadius,
                collisionHeight: WolfDefinition.CollisionHeight);
            wolf.SetPosition(new Vector3(25.5f, 1f, 20.5f));
            int n = 0;
            do
            {
                wolf.Update(0.015625f, null, map);
                n++;
                Assert.True(n < 1000, "the wolf never got home");
            }
            while (wolf.BehaviorState == EnemyBehaviorState.Chasing);

            Assert.Equal(EnemyBehaviorState.Idle, wolf.BehaviorState);
            Assert.True(Vector3.Distance(wolf.Position, home) < 0.06f, $"stopped at {wolf.Position}");
            Vector3 stopped = wolf.Position;
            for (int i = 0; i < 100; i++)
            {
                wolf.Update(0.015625f, null, map);
            }

            Assert.Equal(stopped, wolf.Position);
            Assert.Equal(EnemyBehaviorState.Idle, wolf.BehaviorState);
            arrivals[run] = stopped;
            ticks[run] = n;
        }

        Assert.Equal(arrivals[0], arrivals[1]);
        Assert.Equal(ticks[0], ticks[1]);
        // 18.4 m at 2.8 m/s and 64 ticks/s is about 420 ticks.
        Assert.InRange(ticks[0], 400, 440);
    }

    [Fact]
    public void AWolfWhosePlayerHasLeftItsAggroRange_GoesHome()
    {
        Vector3 home = new(10.5f, 1f, 10.5f);
        EnemyActor wolf = new(new EnemyCombatant(() => home), home, aggroRange: 10f, attackRange: 1.35f, moveSpeed: 2.8f,
            preferredStandOffDistance: WolfDefinition.PreferredStandOffDistance, collisionRadius: WolfDefinition.CollisionRadius,
            collisionHeight: WolfDefinition.CollisionHeight);
        wolf.SetPosition(new Vector3(20.5f, 1f, 10.5f));
        Vector3 farPlayer = new(40.5f, 1.5f, 10.5f);
        wolf.Update(0.015625f, farPlayer, (Map?)null);
        Assert.Equal(EnemyBehaviorState.Chasing, wolf.BehaviorState);
        Assert.True(wolf.Position.X < 20.5f, "the wolf did not head home");
    }

    [Fact]
    public void ABitingTick_AllocatesNothing()
    {
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        EnemyActor wolf = Actor(new Vector3(20.5f, 1f, 20.5f));
        Vector3 player = new(21.5f, 1.5f, 20.5f);
        wolf.Update(0.015625f, player, runtime.Map);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            wolf.Update(0.015625f, player, runtime.Map);
            wolf.Update(0.015625f, null, runtime.Map);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
