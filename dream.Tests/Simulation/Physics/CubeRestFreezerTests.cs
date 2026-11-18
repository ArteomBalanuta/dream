using BulletSharp;
using BulletSharp.Math;
using Dream.Simulation.Physics;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Utilities;
using Xunit;
using Xunit.Abstractions;
using TkVector2 = OpenTK.Mathematics.Vector2;
using TkVector3 = OpenTK.Mathematics.Vector3;

namespace Dream.Tests.Simulation.Physics;

// PHYS-2: player bodies never deactivate, and Bullet unites every pair of overlapping dynamic
// proxies into one island, so a player on a carpet of touching cubes kept the whole carpet
// solving every tick. CubeRestFreezer turns resting cubes static (they leave islands) and wakes
// only the cubes something pushes, carries or un-supports.
public class CubeRestFreezerTests
{
    private const float TickSeconds = 1f / 64f;
    private const int CarpetSide = 21;
    private const int CarpetOrigin = 4;
    private const int SettleTicks = 320;

    private readonly ITestOutputHelper _output;

    public CubeRestFreezerTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void PlayerStandingOnCarpet_AfterSettling_NoCubeIsActive()
    {
        using Scene scene = Scene.Create(freezeEnabled: true);
        TkVector3 start = scene.CarpetTop(10, 10);
        Player player = scene.AddPlayer(start);

        scene.Run(player, SettleTicks, TkVector3.Zero);

        int active = scene.CountActiveCarpetBodies();
        _output.WriteLine($"carpet {scene.Carpet.Count}, active {active}, frozen {scene.Physics.CubeRest.FrozenCount}, synced {scene.Sync.LastSyncedBodyCount}");
        Assert.True(scene.Carpet.Count >= 400);
        Assert.Equal(0, active);
        Assert.Equal(0, scene.Sync.LastSyncedBodyCount);
        Assert.Equal(scene.Carpet.Count, scene.Physics.CubeRest.FrozenCount);
        Assert.True(player.PhysicsBody.IsActive);

        // The player still stands on the carpet (static cubes collide like the resting ones did).
        TkVector3 position = player.GetWorldPosition();
        Assert.InRange(position.Y, start.Y - 0.05f, start.Y + 0.05f);
        Assert.Empty(scene.Map.FindInvariantViolations());
    }

    // The measured problem, kept as a regression reference: with freezing off, the player's
    // never-sleeping body keeps the whole carpet's island awake.
    [Fact]
    public void PlayerStandingOnCarpet_WithFreezingOff_KeepsTheWholeCarpetAwake()
    {
        using Scene scene = Scene.Create(freezeEnabled: false);
        Player player = scene.AddPlayer(scene.CarpetTop(10, 10));

        scene.Run(player, SettleTicks, TkVector3.Zero);

        int active = scene.CountActiveCarpetBodies();
        _output.WriteLine($"carpet {scene.Carpet.Count}, active {active}");
        Assert.Equal(scene.Carpet.Count, active);
        Assert.Equal(0, scene.Physics.CubeRest.FrozenCount);
    }

    // Ticks to cover a bit over 5 m at the default cruise, including the ramp-up.
    private static readonly int WalkTicks =
        (int)(6f / PlayerSimulationTuning.DefaultRunSpeed * SimulationClock.AuthoritativeTicksPerSecond);

    [Fact]
    public void PlayerWalkingAcrossCarpet_WakesNothingUnderfoot()
    {
        using Scene scene = Scene.Create(freezeEnabled: true);
        Player player = scene.AddPlayer(scene.CarpetTop(2, 10) + new TkVector3(0.23f, 0f, -0.13f));
        scene.Run(player, SettleTicks, TkVector3.Zero);
        Assert.Equal(0, scene.CountActiveCarpetBodies());
        long wokenBefore = scene.Physics.CubeRest.TotalWoken;

        int maxActive = 0;
        TkVector3 before = player.GetWorldPosition();
        TkVector3 diagonal = new TkVector3(1f, 0f, 0.35f).Normalized();
        for (int tick = 0; tick < WalkTicks; tick++)
        {
            scene.Run(player, 1, tick < WalkTicks / 2 ? TkVector3.UnitX : diagonal);
            maxActive = Math.Max(maxActive, scene.CountActiveCarpetBodies());
        }

        TkVector3 after = player.GetWorldPosition();
        _output.WriteLine($"walked {after.X - before.X:0.00} m, max active {maxActive}");
        Assert.True(after.X - before.X > 5f, $"walked only {after.X - before.X} m");
        Assert.InRange(after.Y, before.Y - 0.01f, before.Y + 0.01f);
        Assert.Equal(0, maxActive);
        Assert.Equal(wokenBefore, scene.Physics.CubeRest.TotalWoken);
    }

    // A player standing exactly cell-aligned (a cell-centered spawn) has its front face flush
    // with the next seam of the carpet. Bullet's box-box contact then pushes against the next
    // cube's side; dynamic cubes yielded, a static one would hold the player forever. The flush
    // seam rule wakes that row, and the walk matches the all-dynamic run.
    //
    // The walk has to cover more than 5 m for the seam crossings to be what is measured, so the
    // tick count is derived from the cruise speed (WalkTicks) rather than a fixed second count: a
    // ramping body at the default cruise needs WalkTicks to get there. The frozen/dynamic
    // comparison that this test exists for is untouched: both runs ramp the same way.
    [Fact]
    public void PlayerStartingCellAligned_WalksAcrossTheSeamsLikeWithDynamicCubes()
    {
        float[] walked = new float[2];
        foreach (bool freeze in new[] { true, false })
        {
            using Scene scene = Scene.Create(freezeEnabled: freeze);
            Player player = scene.AddPlayer(scene.CarpetTop(2, 10));
            scene.Run(player, SettleTicks, TkVector3.Zero);
            float startX = player.GetWorldPosition().X;
            scene.Run(player, WalkTicks, TkVector3.UnitX);
            walked[freeze ? 0 : 1] = player.GetWorldPosition().X - startX;
        }

        _output.WriteLine($"walked frozen {walked[0]:0.0000} m, dynamic {walked[1]:0.0000} m");
        Assert.True(walked[0] > 5f, $"walked only {walked[0]} m");
        Assert.InRange(walked[0], walked[1] - 0.05f, walked[1] + 0.05f);
    }

    // Character-gaits wave: the run-up is 3 m long and sprinted, and the push runs 96 ticks.
    //
    // Shoving a 3-cube chain is a momentum transfer, and the ramp changed how much momentum
    // arrives. The old motor assigned the commanded velocity every tick, so a player standing
    // flush against the row pressed with the full 6 m/s on every tick regardless of the contact.
    // PlayerMotor now ramps from the body's own velocity, which the solver zeroes on contact, so a
    // player with no run-up can only ever add GroundAcceleration * dt = 0.28 m/s against three
    // cube masses: measured, the row moved 0.011 m in 48 ticks and 0.396 m in 144 (it saturates;
    // the distance is set by the arrival energy, not by how long the key is held).
    //
    // So the walk-up is given the distance to arrive at full speed, which is what the test always
    // measured. The run-up sprints, so the push arrives at the sprint tier's energy, which scales
    // with the square of the speed; the floor below is the movement the row shows at the shipped
    // sprint speed, a fraction of what the faster sprint it replaced gave.
    [Fact]
    public void PlayerPushingACube_MovesItAndWakesOnlyWhatItTouches()
    {
        using Scene scene = Scene.Create(freezeEnabled: true);
        // A lone row of 3 cubes beside the carpet, one cell apart from it, and a player on the
        // floor 3 m west of the row sprinting east along it.
        int z = CarpetOrigin + CarpetSide + 1;
        List<RigidBody> row = [];
        for (int x = 10; x < 13; x++)
        {
            row.Add(scene.AddCube(x, 1, z));
        }

        // A cube touching the row's side (north of the first cube) that the push slides past.
        RigidBody side = scene.AddCube(10, 1, z + 1);
        Player player = scene.AddPlayer(new TkVector3(6.48f, 1.52f, z + 0.5f));
        scene.Run(player, SettleTicks, TkVector3.Zero);
        Assert.Equal(0, scene.CountActiveCarpetBodies());
        Assert.All(row, body => Assert.True(scene.Physics.CubeRest.IsFrozen(body)));

        float startX = row[2].WorldTransform.Origin.X;
        bool sideWoke = false;
        bool carpetWoke = false;
        for (int tick = 0; tick < 96; tick++)
        {
            scene.Run(player, 1, TkVector3.UnitX, sprint: true);
            sideWoke |= side.IsActive;
            carpetWoke |= scene.Carpet.Any(entry => entry.Body.IsActive);
        }

        float moved = row[2].WorldTransform.Origin.X - startX;
        _output.WriteLine(
            $"row end moved {moved:0.000} m; active {scene.CountActive()}; "
            + $"player at {player.GetWorldPosition().X:0.000} doing {new TkVector2(player.PhysicsBody.LinearVelocity.X, player.PhysicsBody.LinearVelocity.Z).Length:0.000} m/s");
        Assert.True(moved > 0.25f, $"the pushed row moved {moved} m");
        Assert.All(row, body => Assert.True(body.IsActive));
        Assert.False(carpetWoke);
        Assert.False(sideWoke);

        // Once the player stops, everything refreezes.
        scene.Run(player, SettleTicks, TkVector3.Zero);
        Assert.Equal(0, scene.CountActive());
        Assert.Empty(scene.Map.FindInvariantViolations());
    }

    // Pushing feels the same: a lone cube on the floor, and a cube resting on top of the carpet
    // (which the player pushes while standing on the carpet), travel as far as with every cube
    // dynamic.
    //
    // Character-gaits wave: 64 push ticks, not 48. A single cube does yield to a ramping player
    // leaning on it, so this one only lost the ramp's travel: the first 4.3 / 18 = 0.239 s
    // (15.3 ticks) go into reaching cruise and cover 0.514 m instead of 0.65 m, and the cube
    // measured 0.923 m against the 1 m the test asks for. 16 more ticks is the ramp window back
    // (0.0662 m per cruise tick of push); measured 1.646 m on the carpet and 2.061 m on the floor.
    // The frozen/dynamic comparison, which is what this test is for, is unaffected: both runs
    // ramp identically (measured 1.646 vs 1.671 m).
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PushedCube_TravelsAsFarAsWithDynamicCubes(bool onTopOfCarpet)
    {
        float[] moved = new float[2];
        int[] wokenCarpet = new int[2];
        foreach (bool freeze in new[] { true, false })
        {
            using Scene scene = Scene.Create(freezeEnabled: freeze);
            RigidBody pushed;
            Player player;
            if (onTopOfCarpet)
            {
                pushed = scene.AddCube(CarpetOrigin + 8, 2, CarpetOrigin + 10);
                player = scene.AddPlayer(scene.CarpetTop(6, 10) + new TkVector3(0.98f, 0f, 0f));
            }
            else
            {
                int z = CarpetOrigin + CarpetSide + 2;
                pushed = scene.AddCube(10, 1, z);
                player = scene.AddPlayer(new TkVector3(9.48f, 1.52f, z + 0.5f));
            }

            scene.Run(player, SettleTicks, TkVector3.Zero);
            float startX = pushed.WorldTransform.Origin.X;
            scene.Run(player, 64, TkVector3.UnitX);
            moved[freeze ? 0 : 1] = pushed.WorldTransform.Origin.X - startX;
            wokenCarpet[freeze ? 0 : 1] = scene.CountActiveCarpetBodies();
            Assert.Empty(scene.Map.FindInvariantViolations());
        }

        _output.WriteLine($"moved frozen {moved[0]:0.000} m (carpet awake {wokenCarpet[0]}), dynamic {moved[1]:0.000} m (carpet awake {wokenCarpet[1]})");
        Assert.True(moved[0] > 1f, $"the pushed cube moved {moved[0]} m");
        Assert.InRange(moved[0], moved[1] - 0.15f, moved[1] + 0.15f);
        // At most the carpet rows the push slides along (the cube rests cell-aligned, so the seam
        // ahead of it and the two corner seams beside it are flush), never the whole carpet.
        Assert.True(wokenCarpet[0] <= 3 * CarpetSide, $"{wokenCarpet[0]} carpet cubes woke");
    }

    [Fact]
    public void PushingTheBottomOfAStack_CarriesTheCubesAboveIt()
    {
        using Scene scene = Scene.Create(freezeEnabled: true);
        int z = CarpetOrigin + CarpetSide + 2;
        RigidBody bottom = scene.AddCube(10, 1, z);
        RigidBody top = scene.AddCube(10, 2, z);
        Player player = scene.AddPlayer(new TkVector3(9.48f, 1.52f, z + 0.5f));
        scene.Run(player, SettleTicks, TkVector3.Zero);
        Assert.True(scene.Physics.CubeRest.IsFrozen(bottom));
        Assert.True(scene.Physics.CubeRest.IsFrozen(top));

        scene.Run(player, 1, TkVector3.UnitX);

        Assert.True(bottom.IsActive);
        Assert.True(top.IsActive);
    }

    [Fact]
    public void RemovingASupportingCube_WakesTheStackAboveItOnly()
    {
        using Scene scene = Scene.Create(freezeEnabled: true);
        Player player = scene.AddPlayer(scene.CarpetTop(2, 2));
        Cube supportCube = scene.CubeAt(10, 1, 10);
        RigidBody sideNeighbour = scene.Carpet.Single(entry => ReferenceEquals(entry.Cube, scene.CubeAt(11, 1, 10))).Body;
        RigidBody rider = scene.AddCube(10, 2, 10);
        RigidBody topRider = scene.AddCube(10, 3, 10);
        scene.Run(player, SettleTicks, TkVector3.Zero);
        Assert.True(scene.Physics.CubeRest.IsFrozen(rider));
        Assert.True(scene.Physics.CubeRest.IsFrozen(topRider));
        float restY = rider.WorldTransform.Origin.Y;
        float topRestY = topRider.WorldTransform.Origin.Y;

        Assert.True(scene.Map.RemoveCube(supportCube));
        Assert.False(scene.Physics.CubeRest.IsFrozen(rider));
        Assert.False(scene.Physics.CubeRest.IsFrozen(topRider));
        Assert.True(scene.Physics.CubeRest.IsFrozen(sideNeighbour));
        scene.Run(player, 64, TkVector3.Zero);

        float fallen = restY - rider.WorldTransform.Origin.Y;
        float topFallen = topRestY - topRider.WorldTransform.Origin.Y;
        _output.WriteLine($"rider fell {fallen:0.000} m, top rider {topFallen:0.000} m");
        Assert.InRange(fallen, 0.9f, 1.1f);
        Assert.InRange(topFallen, 0.9f, 1.1f);
        Assert.Empty(scene.Map.FindInvariantViolations());
    }

    [Fact]
    public void ActivatingAFrozenCube_UnfreezesItBeforeTheNextStep()
    {
        using Scene scene = Scene.Create(freezeEnabled: true);
        Player player = scene.AddPlayer(scene.CarpetTop(2, 2));
        scene.Run(player, SettleTicks, TkVector3.Zero);
        RigidBody body = scene.Carpet[0].Body;
        Assert.True(scene.Physics.CubeRest.IsFrozen(body));
        Assert.True(body.IsStaticObject);

        body.Activate(true);
        scene.Run(player, 1, TkVector3.Zero);

        Assert.False(scene.Physics.CubeRest.IsFrozen(body));
        Assert.False(body.IsStaticObject);
        Assert.Equal(1f, body.InvMass);
    }

    [Fact]
    public void Grounded_JumpWorksOnFrozenCubesAndOnTerrain()
    {
        using Scene scene = Scene.Create(freezeEnabled: true);
        Player onCarpet = scene.AddPlayer(scene.CarpetTop(10, 10));
        Player onFloor = scene.AddPlayer(new TkVector3(1.5f, 1.52f, 1.5f));
        scene.Run([onCarpet, onFloor], SettleTicks, TkVector3.Zero);
        Assert.Equal(0, scene.CountActiveCarpetBodies());
        float carpetY = onCarpet.GetWorldPosition().Y;
        float floorY = onFloor.GetWorldPosition().Y;

        scene.Run([onCarpet, onFloor], 1, TkVector3.Zero, jump: true);
        scene.Run([onCarpet, onFloor], 8, TkVector3.Zero);

        _output.WriteLine($"carpet rise {onCarpet.GetWorldPosition().Y - carpetY:0.000}, floor rise {onFloor.GetWorldPosition().Y - floorY:0.000}");
        Assert.True(onCarpet.GetWorldPosition().Y - carpetY > 0.3f);
        Assert.True(onFloor.GetWorldPosition().Y - floorY > 0.3f);

        // Landing on the frozen carpet wakes nothing.
        scene.Run([onCarpet, onFloor], 128, TkVector3.Zero);
        Assert.InRange(onCarpet.GetWorldPosition().Y, carpetY - 0.05f, carpetY + 0.05f);
        Assert.Equal(0, scene.CountActiveCarpetBodies());
    }

    // The freeze/wake pass runs every tick on the server: walking over a frozen carpet allocates
    // nothing.
    //
    // The measured body is one whole walk cycle, and the player is turned around by its own
    // position rather than by a tick counter. AllocationAssert measures for a minimum time span
    // and keeps the best round, so how many iterations run depends on the machine: a tick
    // counter therefore measured a different, machine-dependent slice of a body that never
    // repeats. Worse, the walk drifted (the gait ramp is not symmetric in the two directions):
    // after about 4,200 ticks the player reached the carpet's east edge and fell to the floor,
    // and from there on every tick woke cubes and genuinely allocated (~0.5 kB a tick, boxing
    // each moved body's (Cube, position) user object in PhysicsSyncSystem). At roughly 25 us a
    // tick the helper's 300 ms minimum span alone is some 12,000 ticks, so most of the
    // measurement was spent outside the regime this test is named for.
    //
    // Turning at +/- TurnAroundMetres keeps the player on the frozen carpet for any number of
    // iterations (measured: 60,000 ticks, x within [13.6, 15.9] of a 4..25 carpet, nothing ever
    // woken, 0 B), and WalkCycleTicks is long enough that every iteration always contains
    // several turns and several seam crossings. What the measurement covers no longer depends on
    // how fast the machine is.
    [Fact]
    public void Tick_WithAFrozenCarpetAndAWalkingPlayer_AllocatesNothing()
    {
        const float TurnAroundMetres = 0.75f;
        const int WalkCycleTicks = 48;

        using Scene scene = Scene.Create(freezeEnabled: true);
        Player player = scene.AddPlayer(scene.CarpetTop(10, 10) + new TkVector3(0.23f, 0f, -0.13f));
        scene.Run(player, SettleTicks, TkVector3.Zero);
        Assert.Equal(scene.Carpet.Count, scene.Physics.CubeRest.FrozenCount);
        Player[] players = [player];
        float anchorX = player.GetWorldPosition().X;
        float startY = player.GetWorldPosition().Y;
        bool eastbound = true;
        float minX = anchorX;
        float maxX = anchorX;
        int turns = 0;

        AllocationAssert.AtMost(0, () =>
        {
            for (int i = 0; i < WalkCycleTicks; i++)
            {
                float x = player.GetWorldPosition().X;
                if (eastbound ? x > anchorX + TurnAroundMetres : x < anchorX - TurnAroundMetres)
                {
                    eastbound = !eastbound;
                    turns++;
                }

                scene.Run(players, 1, eastbound ? TkVector3.UnitX : -TkVector3.UnitX);
                float walked = player.GetWorldPosition().X;
                minX = Math.Min(minX, walked);
                maxX = Math.Max(maxX, walked);
            }
        });

        // The measurement really did walk a player to and fro over a wholly frozen carpet, rather
        // than one that had wandered off it: the allocation figure above is about this regime.
        _output.WriteLine($"turned {turns} times, x in [{minX:0.00}, {maxX:0.00}] around {anchorX:0.00}");
        Assert.True(turns > 1, $"the walk turned only {turns} times");
        Assert.InRange(minX, anchorX - TurnAroundMetres - 0.5f, anchorX);
        Assert.InRange(maxX, anchorX, anchorX + TurnAroundMetres + 0.5f);
        Assert.InRange(player.GetWorldPosition().Y, startY - 0.01f, startY + 0.01f);
        Assert.Equal(0, scene.CountActiveCarpetBodies());
        Assert.Equal(scene.Carpet.Count, scene.Physics.CubeRest.FrozenCount);
        Assert.Equal(0, scene.Physics.CubeRest.TotalWoken);
    }

    // Two identical worlds given identical input stay bit-identical through freezing, a push
    // that wakes and moves cubes, and refreezing (the two-server determinism requirement).
    [Fact]
    public void TwoIdenticalWorlds_StayBitIdenticalThroughFreezeAndWake()
    {
        using Scene first = Scene.Create(freezeEnabled: true);
        using Scene second = Scene.Create(freezeEnabled: true);
        foreach (Scene scene in new[] { first, second })
        {
            scene.AddCube(CarpetOrigin + 8, 2, CarpetOrigin + 10);
            scene.AddPlayer(scene.CarpetTop(6, 10) + new TkVector3(0.98f, 0f, 0f));
            scene.AddPlayer(new TkVector3(1.5f, 1.52f, 1.5f));
        }

        TkVector3[] script = [TkVector3.Zero, TkVector3.UnitX, new TkVector3(0.6f, 0f, 0.8f), TkVector3.Zero];
        for (int phase = 0; phase < script.Length; phase++)
        {
            first.Run(first.Players, 120, script[phase]);
            second.Run(second.Players, 120, script[phase]);
            Assert.Equal(first.Physics.CubeRest.FrozenCount, second.Physics.CubeRest.FrozenCount);
            Assert.Equal(first.Physics.CubeRest.TotalWoken, second.Physics.CubeRest.TotalWoken);
            RigidBody[] firstBodies = first.Physics.DynamicMapCubeBodies.ToArray();
            RigidBody[] secondBodies = second.Physics.DynamicMapCubeBodies.ToArray();
            Assert.Equal(firstBodies.Length, secondBodies.Length);
            for (int i = 0; i < firstBodies.Length; i++)
            {
                Assert.Equal(firstBodies[i].WorldTransform.Origin, secondBodies[i].WorldTransform.Origin);
                Assert.Equal(first.Physics.CubeRest.IsFrozen(firstBodies[i]), second.Physics.CubeRest.IsFrozen(secondBodies[i]));
            }

            for (int i = 0; i < first.Players.Count; i++)
            {
                Assert.Equal(first.Players[i].GetWorldPosition(), second.Players[i].GetWorldPosition());
            }
        }

        Assert.True(first.Physics.CubeRest.TotalWoken > 0);
    }

    [Fact]
    public void TurningFreezingOff_RestoresEveryBody()
    {
        using Scene scene = Scene.Create(freezeEnabled: true);
        Player player = scene.AddPlayer(scene.CarpetTop(10, 10));
        scene.Run(player, SettleTicks, TkVector3.Zero);
        Assert.Equal(scene.Carpet.Count, scene.Physics.CubeRest.FrozenCount);

        scene.Physics.CubeRest.Enabled = false;

        Assert.Equal(0, scene.Physics.CubeRest.FrozenCount);
        Assert.All(scene.Carpet, entry =>
        {
            Assert.False(entry.Body.IsStaticObject);
            Assert.Equal(1f, entry.Body.InvMass);
            Assert.True(entry.Body.IsActive);
        });
    }

    private sealed class Scene : IDisposable
    {
        private readonly List<Player> _players = [];

        public IReadOnlyList<Player> Players => _players;

        private Scene(PhysicsIntegrator physics, Map map)
        {
            Physics = physics;
            Map = map;
        }

        public PhysicsIntegrator Physics { get; }

        public Map Map { get; }

        public PhysicsSyncSystem Sync { get; } = new();

        public List<(Cube Cube, RigidBody Body)> Carpet { get; } = [];

        public static Scene Create(bool freezeEnabled)
        {
            PhysicsIntegrator physics = new();
            physics.CubeRest.Enabled = freezeEnabled;
            Map map = new(physics, width: 32, depth: 32);
            Scene scene = new(physics, map);
            for (int x = CarpetOrigin; x < CarpetOrigin + CarpetSide; x++)
            {
                for (int z = CarpetOrigin; z < CarpetOrigin + CarpetSide; z++)
                {
                    Cube cube = Assert.IsType<Cube>(map.TryAddCube(new TkVector3(x, 1f, z), withPhysics: true));
                    scene.Carpet.Add((cube, scene.BodyOf(cube)));
                }
            }

            return scene;
        }

        // Center of a player standing on the carpet cell at (carpet-relative) column i, j.
        public TkVector3 CarpetTop(int i, int j)
        {
            return new TkVector3(CarpetOrigin + i + 0.5f, 2.52f, CarpetOrigin + j + 0.5f);
        }

        public RigidBody AddCube(int x, int y, int z)
        {
            Cube cube = Assert.IsType<Cube>(Map.TryAddCube(new TkVector3(x, y, z), withPhysics: true));
            return BodyOf(cube);
        }

        public Cube CubeAt(int x, int y, int z)
        {
            return Assert.IsType<Cube>(Map.Cubes[x, y, z]);
        }

        public Player AddPlayer(TkVector3 position)
        {
            RigidBody body = new PhysicsFactory().CreatePlayerBody(position.ToBullet());
            Physics.AddSessionPlayerBody(body);
            Player player = new(Map, Physics, body, position);
            _players.Add(player);
            return player;
        }

        public void Run(Player player, int ticks, TkVector3 direction, bool jump = false, bool sprint = false)
        {
            Run([player], ticks, direction, jump, sprint);
        }

        public void Run(IReadOnlyList<Player> players, int ticks, TkVector3 direction, bool jump = false, bool sprint = false)
        {
            PlayerSimulationTuning tuning = PlayerSimulationTuning.AuthoritativeDefault;
            for (int tick = 0; tick < ticks; tick++)
            {
                for (int i = 0; i < players.Count; i++)
                {
                    players[i].Update(
                        TickSeconds,
                        new PlayerSimulationInput(new MovementCommand(direction, JumpPressed: jump && tick == 0, Sprint: sprint), TkVector2.Zero, AllowGameplayInput: true),
                        tuning);
                }

                Physics.StepSimulationFixed(TickSeconds);
                for (int i = 0; i < players.Count; i++)
                {
                    players[i].SyncCameraFromBody(tuning);
                }

                Sync.SyncDynamicMapBodies(Map, Physics.DynamicMapCubeBodies);
            }
        }

        public int CountActiveCarpetBodies()
        {
            return Carpet.Count(entry => entry.Body.IsActive);
        }

        public int CountActive()
        {
            int active = 0;
            foreach (RigidBody body in Physics.DynamicMapCubeBodies)
            {
                if (body.IsActive)
                {
                    active++;
                }
            }

            return active;
        }

        public void Dispose()
        {
            foreach (Player player in _players)
            {
                player.Dispose();
            }

            Physics.Dispose();
        }

        private RigidBody BodyOf(Cube cube)
        {
            return Physics.DynamicMapCubeBodies.Single(candidate =>
                candidate.UserObject is ValueTuple<Cube, TkVector3> tuple && ReferenceEquals(tuple.Item1, cube));
        }
    }
}
