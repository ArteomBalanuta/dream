using BulletSharp;
using Dream.Simulation.Physics;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Tests.Utilities;
using Xunit;
using Xunit.Abstractions;
using TkVector2 = OpenTK.Mathematics.Vector2;
using TkVector3 = OpenTK.Mathematics.Vector3;

namespace Dream.Tests.Simulation.Physics;

// Wave 9 tester, PHYS-2 (CubeRestFreezer) edge cases the merged tests do not cover. Each scenario
// runs with freezing on and, where the expected outcome is "behaves like dynamic cubes", with
// freezing off as the reference.
//
// The freezer wakes a frozen cube when a mover pushes or carries it, when a map body it rests on
// leaves the world (PhysicsIntegrator.RemoveMapBodyFromWorld), or when something calls Activate.
// Player bodies are movers, but they are not map bodies: a player body that leaves the world
// (RemoveSessionPlayerBody, SetPlayerBodyParked) or is teleported (Player.SetWorldPosition) wakes
// nothing, so a cube frozen on top of an idle player stays where it is.
public class Wave9CubeRestFreezerEdgeCaseTests
{
    private const float TickSeconds = 1f / 64f;
    private const int SettleTicks = 160;

    private readonly ITestOutputHelper _output;

    public Wave9CubeRestFreezerEdgeCaseTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // ---- Client mirror ----

    // Clients build their mirror map with replicatedCubeBodies: placed cubes are kinematic and
    // never in DynamicMapCubeBodies, so the freezer never freezes or wakes anything on a client
    // and cannot make prediction differ between freeze on and off.
    [Fact]
    public void ClientMirrorMap_NeverFreezesReplicatedCubes()
    {
        using PhysicsIntegrator physics = new();
        Assert.True(physics.CubeRest.Enabled || !CubeRestFreezer.DefaultEnabled);
        physics.CubeRest.Enabled = true;
        Map map = new(physics, width: 24, depth: 24, replicatedCubeBodies: true);
        for (int x = 4; x < 12; x++)
        {
            for (int z = 4; z < 12; z++)
            {
                Assert.NotNull(map.TryAddCube(new TkVector3(x, 1f, z), withPhysics: true));
            }
        }

        RigidBody playerBody = new PhysicsFactory().CreatePlayerBody(new TkVector3(6.5f, 2.54f, 6.5f).ToBullet());
        physics.AddSessionPlayerBody(playerBody);
        using Player player = new(map, physics, playerBody, new TkVector3(6.5f, 2.54f, 6.5f));
        Run(physics, map, [player], SettleTicks, TkVector3.Zero);
        Run(physics, map, [player], 64, TkVector3.UnitX);

        Assert.Empty(physics.DynamicMapCubeBodies);
        Assert.Equal(0, physics.CubeRest.FrozenCount);
        Assert.Equal(0L, physics.CubeRest.TotalFrozen);
        Assert.Equal(0L, physics.CubeRest.TotalWoken);
    }

    // ---- A cube resting on a player ----

    // The player body is a 1 m box, so a cube built above an idle player rests on its head and,
    // like any resting cube, freezes. A player who walks away carries it off (the rider rule).
    [Fact]
    public void CubeOnAnIdlePlayersHead_Freezes_AndWakesWhenThePlayerWalksAway()
    {
        using Scene scene = Scene.Create(freezeEnabled: true);
        Player player = scene.AddPlayer(new TkVector3(8.5f, 1.54f, 8.5f));
        scene.Run(SettleTicks, TkVector3.Zero);
        RigidBody rider = scene.AddCube(8, 2, 8);
        scene.Run(SettleTicks, TkVector3.Zero);
        float restY = rider.WorldTransform.Origin.Y;
        _output.WriteLine($"rider rests at y {restY:0.000}; frozen {scene.Physics.CubeRest.IsFrozen(rider)}");
        Assert.InRange(restY, 2.45f, 2.7f);
        Assert.True(scene.Physics.CubeRest.IsFrozen(rider));

        scene.Run(48, TkVector3.UnitX);
        scene.Run(SettleTicks, TkVector3.Zero);

        float endY = rider.WorldTransform.Origin.Y;
        float riderX = rider.WorldTransform.Origin.X;
        float playerX = player.GetWorldPosition().X;
        _output.WriteLine($"after the walk the rider is at x {riderX:0.000} y {endY:0.000}, player x {playerX:0.00}");
        // Either it slid off onto the floor, or friction carried it along on the player's head;
        // never left hanging where the player stood.
        bool onFloor = endY is > 1.45f and < 1.6f;
        bool stillOnHead = endY is > 2.45f and < 2.7f && MathF.Abs(riderX - playerX) < 0.6f;
        Assert.True(onFloor || stillOnHead, $"rider at ({riderX}, {endY}), player x {playerX}");
        Assert.True(riderX > 8.6f, "the rider never woke");
    }

    // A player body that leaves the world (RemoveSessionPlayerBody on a disconnect or seat release,
    // RemoteSessionPlayerState) or is teleported (Player.SetWorldPosition: a respawn, a server
    // correction) is not a map body, so the freezer wakes nothing. Measured (64 ticks after the
    // exit, the cube had rested on the head for 2.5 s):
    //   Disconnect: y 2.500 frozen, y 2.500 dynamic (pre-existing: the dynamic cube had reached
    //               WANTS_DEACTIVATION, and alone in its island Bullet put it to sleep at once).
    //   Teleport:   y 2.500 frozen, y 1.500 dynamic (the dynamic cube falls to the floor).
    // Without freezing a cube floats only if it rested >= 2 s (gDeactivationTime) and the exit
    // left it alone in its island; with freezing it floats after 0.5 s of rest, every time.
    // BUG W9-1: PHYS-2 regression for the teleport case, wider window for the disconnect case.
    // This test pins the measured behaviour; FrozenCubeOnAPlayersHead_FallsWhenThePlayerLeaves is
    // the expected one.
    [Theory]
    [InlineData(PlayerExit.Disconnect, true, 2.5f)]
    [InlineData(PlayerExit.Disconnect, false, 2.5f)]
    [InlineData(PlayerExit.Teleport, true, 2.5f)]
    [InlineData(PlayerExit.Teleport, false, 1.5f)]
    public void CubeOnAPlayersHead_AfterThePlayerLeaves_MeasuredHeight(PlayerExit exit, bool freeze, float expectedY)
    {
        float endY = RunCubeOnPlayersHead(exit, freeze);
        Assert.InRange(endY, expectedY - 0.05f, expectedY + 0.05f);
    }

    [Theory(Skip = "W9-1: the freezer does not wake a cube frozen on a player body that is removed or teleported")]
    [InlineData(PlayerExit.Disconnect)]
    [InlineData(PlayerExit.Teleport)]
    public void FrozenCubeOnAPlayersHead_FallsWhenThePlayerLeaves(PlayerExit exit)
    {
        float endY = RunCubeOnPlayersHead(exit, freeze: true);
        Assert.InRange(endY, 1.45f, 1.55f);
    }

    private float RunCubeOnPlayersHead(PlayerExit exit, bool freeze)
    {
        using Scene scene = Scene.Create(freeze);
        Player player = scene.AddPlayer(new TkVector3(8.5f, 1.54f, 8.5f));
        scene.AddPlayer(new TkVector3(2.5f, 1.54f, 2.5f));
        scene.Run(SettleTicks, TkVector3.Zero);
        RigidBody rider = scene.AddCube(8, 2, 8);
        scene.Run(SettleTicks, TkVector3.Zero);
        Assert.InRange(rider.WorldTransform.Origin.Y, 2.45f, 2.7f);
        Assert.Equal(freeze, scene.Physics.CubeRest.IsFrozen(rider));
        if (exit == PlayerExit.Disconnect)
        {
            scene.RemovePlayer(player);
        }
        else
        {
            player.SetWorldPosition(new TkVector3(16.5f, 1.54f, 16.5f));
        }

        scene.Run(64, TkVector3.Zero);
        float endY = rider.WorldTransform.Origin.Y;
        _output.WriteLine($"{exit}, freeze {freeze}: rider at y {endY:0.000} one second later, state {rider.ActivationState}");
        return endY;
    }

    public enum PlayerExit
    {
        Disconnect,
        Teleport
    }

    // ---- Jumping ----

    // A player jumping on the same frozen cube over and over: the cube under it is a support, so
    // nothing wakes, and the jump heights match the all-dynamic run.
    [Fact]
    public void RepeatedJumpsOnAFrozenCube_WakeNothing_AndMatchDynamicCubes()
    {
        float[][] peaks = new float[2][];
        foreach (bool freeze in new[] { true, false })
        {
            using Scene scene = Scene.Create(freeze);
            RigidBody pedestal = scene.AddCube(8, 1, 8);
            Player player = scene.AddPlayer(new TkVector3(8.5f, 2.54f, 8.5f));
            scene.Run(SettleTicks, TkVector3.Zero);
            if (freeze)
            {
                Assert.True(scene.Physics.CubeRest.IsFrozen(pedestal));
            }

            long wokenBefore = scene.Physics.CubeRest.TotalWoken;
            float pedestalY = pedestal.WorldTransform.Origin.Y;
            List<float> jumpPeaks = [];
            for (int jump = 0; jump < 10; jump++)
            {
                float peak = float.MinValue;
                scene.Run(1, TkVector3.Zero, jump: true);
                for (int tick = 0; tick < 63; tick++)
                {
                    scene.Run(1, TkVector3.Zero);
                    peak = Math.Max(peak, player.GetWorldPosition().Y);
                }

                jumpPeaks.Add(peak);
            }

            peaks[freeze ? 0 : 1] = [.. jumpPeaks];
            if (freeze)
            {
                Assert.Equal(wokenBefore, scene.Physics.CubeRest.TotalWoken);
                Assert.True(scene.Physics.CubeRest.IsFrozen(pedestal));
            }

            Assert.Equal(pedestalY, pedestal.WorldTransform.Origin.Y, 3);
            Assert.InRange(player.GetWorldPosition().Y, 2.45f, 2.6f);
        }

        _output.WriteLine($"peaks frozen  {string.Join(", ", peaks[0].Select(p => p.ToString("0.000")))}");
        _output.WriteLine($"peaks dynamic {string.Join(", ", peaks[1].Select(p => p.ToString("0.000")))}");
        // (A press on the landing tick is not grounded yet and does not jump, in both runs.)
        Assert.True(peaks[0].Count(peak => peak > 2.85f) >= 6, "most presses should jump");
        for (int i = 0; i < peaks[0].Length; i++)
        {
            Assert.InRange(peaks[0][i], peaks[1][i] - 0.05f, peaks[1][i] + 0.05f);
        }
    }

    // ---- Building, fracturing ----

    // A cube built on top of a frozen carpet lands on static cubes: the carpet stays frozen, the
    // new cube settles on top at the same height as on dynamic cubes and freezes in turn.
    [Fact]
    public void BuildingOnTopOfAFrozenCube_LandsLikeOnADynamicOne_AndFreezes()
    {
        float[] landed = new float[2];
        foreach (bool freeze in new[] { true, false })
        {
            using Scene scene = Scene.Create(freeze);
            List<RigidBody> carpet = [];
            for (int x = 6; x < 10; x++)
            {
                carpet.Add(scene.AddCube(x, 1, 8));
            }

            scene.AddPlayer(new TkVector3(2.5f, 1.54f, 2.5f));
            scene.Run(SettleTicks, TkVector3.Zero);
            long wokenBefore = scene.Physics.CubeRest.TotalWoken;
            RigidBody built = scene.AddCube(7, 3, 8);
            scene.Run(SettleTicks, TkVector3.Zero);
            landed[freeze ? 0 : 1] = built.WorldTransform.Origin.Y;
            if (freeze)
            {
                Assert.Equal(wokenBefore, scene.Physics.CubeRest.TotalWoken);
                Assert.All(carpet, body => Assert.True(scene.Physics.CubeRest.IsFrozen(body)));
                Assert.True(scene.Physics.CubeRest.IsFrozen(built));
            }

            Assert.Empty(scene.Map.FindInvariantViolations());
        }

        _output.WriteLine($"built cube rests at y {landed[0]:0.0000} frozen, {landed[1]:0.0000} dynamic");
        Assert.InRange(landed[0], landed[1] - 0.01f, landed[1] + 0.01f);
        Assert.InRange(landed[0], 2.45f, 2.55f);
    }

    // A frozen cube that is fractured (FractureCubeIntoPieces removes its body) leaves the freezer,
    // its rider wakes and falls onto the rubble, and the side neighbours stay frozen.
    [Fact]
    public void FracturingAFrozenSupport_DropsItFromTheFreezer_AndWakesOnlyItsRider()
    {
        using Scene scene = Scene.Create(freezeEnabled: true);
        RigidBody support = scene.AddCube(8, 1, 8);
        RigidBody neighbour = scene.AddCube(9, 1, 8);
        RigidBody rider = scene.AddCube(8, 2, 8);
        scene.AddPlayer(new TkVector3(2.5f, 1.54f, 2.5f));
        scene.Run(SettleTicks, TkVector3.Zero);
        Assert.True(scene.Physics.CubeRest.IsFrozen(support));
        Assert.True(scene.Physics.CubeRest.IsFrozen(rider));
        int frozenBefore = scene.Physics.CubeRest.FrozenCount;
        float riderY = rider.WorldTransform.Origin.Y;

        Assert.True(scene.Map.FractureCubeIntoPieces(new GeometryCellKey(8, 1, 8)));

        Assert.False(scene.Physics.CubeRest.IsFrozen(support));
        Assert.False(scene.Physics.CubeRest.IsFrozen(rider));
        Assert.True(scene.Physics.CubeRest.IsFrozen(neighbour));
        Assert.Equal(frozenBefore - 2, scene.Physics.CubeRest.FrozenCount);

        scene.Run(SettleTicks, TkVector3.Zero);
        float fell = riderY - rider.WorldTransform.Origin.Y;
        _output.WriteLine($"rider fell {fell:0.000} m onto the rubble; frozen again {scene.Physics.CubeRest.IsFrozen(rider)}");
        Assert.True(fell > 0.3f, $"the rider fell only {fell} m");
        Assert.True(scene.Physics.CubeRest.IsFrozen(neighbour));
        Assert.Empty(scene.Map.FindInvariantViolations());
    }

    // A frozen cube removed outright (destroyed by an attack): its frozen entry goes with it, so a
    // cube later built in the same cell can freeze there (the freezer keys cells by center).
    [Fact]
    public void RemovingAFrozenCube_FreesItsCell_ForTheNextCubeToFreezeIn()
    {
        using Scene scene = Scene.Create(freezeEnabled: true);
        RigidBody first = scene.AddCube(8, 1, 8);
        scene.AddPlayer(new TkVector3(2.5f, 1.54f, 2.5f));
        scene.Run(SettleTicks, TkVector3.Zero);
        Assert.True(scene.Physics.CubeRest.IsFrozen(first));

        Assert.True(scene.Map.RemoveCube(scene.CubeAt(8, 1, 8)));
        Assert.Equal(0, scene.Physics.CubeRest.FrozenCount);
        RigidBody second = scene.AddCube(8, 1, 8);
        scene.Run(SettleTicks, TkVector3.Zero);

        Assert.True(scene.Physics.CubeRest.IsFrozen(second));
        Assert.Equal(1, scene.Physics.CubeRest.FrozenCount);
    }

    // ---- Teleporting a frozen cube (map move, reset) ----

    // Map.UpdateCubePosition with physics sync moves the body and activates it; the freezer must
    // let it go before the next step (it is dynamic again), and the map records the move for the
    // next incremental checkpoint.
    [Fact]
    public void MovingAFrozenCubeThroughTheMap_UnfreezesIt_AndMarksItForTheCheckpoint()
    {
        using Scene scene = Scene.Create(freezeEnabled: true);
        RigidBody body = scene.AddCube(8, 1, 8);
        scene.AddPlayer(new TkVector3(2.5f, 1.54f, 2.5f));
        scene.Run(SettleTicks, TkVector3.Zero);
        Assert.True(scene.Physics.CubeRest.IsFrozen(body));
        scene.Map.EnableCheckpointTracking();
        scene.Map.ClearCheckpointChanges();
        Cube cube = scene.CubeAt(8, 1, 8);

        TkVector3 target = cube.Position + new TkVector3(3f, 0f, 0f);
        scene.Map.UpdateCubePosition(cube, target);
        Assert.Contains(cube, scene.Map.CheckpointMovedCubes);
        scene.Run(1, TkVector3.Zero);

        Assert.False(body.IsStaticObject);
        Assert.Equal(1f, body.InvMass);
        Assert.Equal(target.X, body.WorldTransform.Origin.X, 3);

        scene.Run(SettleTicks, TkVector3.Zero);
        Assert.True(scene.Physics.CubeRest.IsFrozen(body));
        Assert.Equal(target.X, cube.Position.X, 3);
        Assert.Empty(scene.Map.FindInvariantViolations());
    }

    // ---- Wave 8 partial-cell support bodies ----

    // A cube resting on a partial sculpture cell's support body freezes there like on a full cube;
    // clearing the partial cell removes the support (a map body), which wakes the cube, and it
    // falls through the now empty cell.
    [Fact]
    public void CubeFrozenOnAPartialCellSupport_FallsWhenThePartialCellIsCleared()
    {
        using PhysicsIntegrator physics = new();
        physics.CubeRest.Enabled = true;
        Map map = new(physics, generateStartupTerrain: true, width: 56, depth: 56);
        GeometryCellKey partialCell = new(26, 2, 13);
        Cube partial = Assert.IsType<Cube>(map.Cubes[26, 2, 13]);
        Assert.True(map.HasCellSupportBody(partialCell));
        Cube cube = Assert.IsType<Cube>(map.TryAddCube(new TkVector3(26f, 3f, 13f), withPhysics: true));
        RigidBody body = physics.DynamicMapCubeBodies.Single(candidate =>
            candidate.UserObject is ValueTuple<Cube, TkVector3> tuple && ReferenceEquals(tuple.Item1, cube));
        PhysicsSyncSystem sync = new();
        for (int tick = 0; tick < SettleTicks * 2; tick++)
        {
            physics.StepSimulationFixed(TickSeconds);
            sync.SyncDynamicMapBodies(map, physics.DynamicMapCubeBodies);
        }

        float restY = body.WorldTransform.Origin.Y;
        _output.WriteLine($"rests at y {restY:0.000}; frozen {physics.CubeRest.IsFrozen(body)}");
        Assert.True(physics.CubeRest.IsFrozen(body));
        Assert.InRange(restY, 3.45f, 3.55f);

        Assert.True(map.RemoveCube(partial));
        Assert.False(map.HasCellSupportBody(partialCell));
        Assert.False(physics.CubeRest.IsFrozen(body));
        for (int tick = 0; tick < SettleTicks; tick++)
        {
            physics.StepSimulationFixed(TickSeconds);
            sync.SyncDynamicMapBodies(map, physics.DynamicMapCubeBodies);
        }

        float endY = body.WorldTransform.Origin.Y;
        _output.WriteLine($"after clearing the partial cell: y {endY:0.000}");
        Assert.True(endY < restY - 0.5f, $"the cube stayed at {endY}");
        Assert.Empty(map.FindInvariantViolations());
    }

    // ---- Harness ----

    private static void Run(PhysicsIntegrator physics, Map map, IReadOnlyList<Player> players, int ticks, TkVector3 direction)
    {
        PhysicsSyncSystem sync = new();
        PlayerSimulationTuning tuning = PlayerSimulationTuning.AuthoritativeDefault;
        for (int tick = 0; tick < ticks; tick++)
        {
            foreach (Player player in players)
            {
                player.Update(
                    TickSeconds,
                    new PlayerSimulationInput(new MovementCommand(direction, JumpPressed: false), TkVector2.Zero, AllowGameplayInput: true),
                    tuning);
            }

            physics.StepSimulationFixed(TickSeconds);
            foreach (Player player in players)
            {
                player.SyncCameraFromBody(tuning);
            }

            sync.SyncDynamicMapBodies(map, physics.DynamicMapCubeBodies);
        }
    }

    private sealed class Scene : IDisposable
    {
        private readonly List<Player> _players = [];
        private readonly PhysicsSyncSystem _sync = new();

        private Scene(PhysicsIntegrator physics, Map map)
        {
            Physics = physics;
            Map = map;
        }

        public PhysicsIntegrator Physics { get; }

        public Map Map { get; }

        public static Scene Create(bool freezeEnabled)
        {
            PhysicsIntegrator physics = new();
            physics.CubeRest.Enabled = freezeEnabled;
            Map map = new(physics, width: 24, depth: 24);
            return new Scene(physics, map);
        }

        public RigidBody AddCube(int x, int y, int z)
        {
            Cube cube = Assert.IsType<Cube>(Map.TryAddCube(new TkVector3(x, y, z), withPhysics: true));
            return Physics.DynamicMapCubeBodies.Single(candidate =>
                candidate.UserObject is ValueTuple<Cube, TkVector3> tuple && ReferenceEquals(tuple.Item1, cube));
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

        // As RemoteSessionPlayerState does when a session leaves: the integrator removes and
        // disposes the body.
        public void RemovePlayer(Player player)
        {
            _players.Remove(player);
            player.Dispose();
            Physics.RemoveSessionPlayerBody(player.PhysicsBody);
        }

        public void Run(int ticks, TkVector3 direction, bool jump = false)
        {
            PlayerSimulationTuning tuning = PlayerSimulationTuning.AuthoritativeDefault;
            for (int tick = 0; tick < ticks; tick++)
            {
                foreach (Player player in _players)
                {
                    player.Update(
                        TickSeconds,
                        new PlayerSimulationInput(new MovementCommand(direction, JumpPressed: jump && tick == 0), TkVector2.Zero, AllowGameplayInput: true),
                        tuning);
                }

                Physics.StepSimulationFixed(TickSeconds);
                foreach (Player player in _players)
                {
                    player.SyncCameraFromBody(tuning);
                }

                _sync.SyncDynamicMapBodies(Map, Physics.DynamicMapCubeBodies);
            }
        }

        public void Dispose()
        {
            foreach (Player player in _players)
            {
                player.Dispose();
            }

            Physics.Dispose();
        }
    }
}
