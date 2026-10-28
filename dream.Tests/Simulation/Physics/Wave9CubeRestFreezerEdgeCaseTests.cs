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
// Player bodies are movers, but they are not map bodies. Before fix loop 1 (W9-1) a player body
// that left the world (RemoveSessionPlayerBody, SetPlayerBodyParked) or was teleported
// (Player.SetWorldPosition) woke nothing, so a cube frozen on top of an idle player floated where
// it stood; those paths now wake the cubes resting on the body (CubeRestFreezer.WakeRidersOf).
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
    // correction). Measured (64 ticks after the exit, the cube had rested on the head for 2.5 s):
    //   Disconnect: y 1.500 frozen, y 2.500 dynamic (pre-existing without freezing: the dynamic
    //               cube had reached WANTS_DEACTIVATION, and alone in its island Bullet put it
    //               to sleep at once).
    //   Teleport:   y 1.500 frozen, y 1.500 dynamic (the cube falls to the floor).
    // Before the W9-1 fix the frozen cube stayed at y 2.500 in both cases.
    [Theory]
    [InlineData(PlayerExit.Disconnect, true, 1.5f)]
    [InlineData(PlayerExit.Disconnect, false, 2.5f)]
    [InlineData(PlayerExit.Teleport, true, 1.5f)]
    [InlineData(PlayerExit.Teleport, false, 1.5f)]
    public void CubeOnAPlayersHead_AfterThePlayerLeaves_MeasuredHeight(PlayerExit exit, bool freeze, float expectedY)
    {
        float endY = RunCubeOnPlayersHead(exit, freeze);
        Assert.InRange(endY, expectedY - 0.05f, expectedY + 0.05f);
    }

    [Theory]
    [InlineData(PlayerExit.Disconnect)]
    [InlineData(PlayerExit.Teleport)]
    [InlineData(PlayerExit.Park)]
    [InlineData(PlayerExit.Detach)]
    public void FrozenCubeOnAPlayersHead_FallsWhenThePlayerLeaves(PlayerExit exit)
    {
        float endY = RunCubeOnPlayersHead(exit, freeze: true);
        Assert.InRange(endY, 1.45f, 1.55f);
    }

    private float RunCubeOnPlayersHead(PlayerExit exit, bool freeze)
    {
        using Scene scene = Scene.Create(freeze);
        bool primary = exit is PlayerExit.Park or PlayerExit.Detach;
        Player player = scene.AddPlayer(new TkVector3(8.5f, 1.54f, 8.5f), primary);
        scene.AddPlayer(new TkVector3(2.5f, 1.54f, 2.5f));
        scene.Run(SettleTicks, TkVector3.Zero);
        RigidBody rider = scene.AddCube(8, 2, 8);
        scene.Run(SettleTicks, TkVector3.Zero);
        Assert.InRange(rider.WorldTransform.Origin.Y, 2.45f, 2.7f);
        Assert.Equal(freeze, scene.Physics.CubeRest.IsFrozen(rider));
        switch (exit)
        {
            case PlayerExit.Disconnect:
                scene.RemovePlayer(player);
                break;
            case PlayerExit.Teleport:
                player.SetWorldPosition(new TkVector3(16.5f, 1.54f, 16.5f));
                break;
            case PlayerExit.Park:
                // WorldSessionRuntime parks the primary body while its seat is empty.
                scene.StopDriving(player);
                scene.Physics.SetPlayerBodyParked(true);
                break;
            case PlayerExit.Detach:
                scene.StopDriving(player);
                RigidBody detached = scene.Physics.DetachPlayerBody();
                Assert.Same(player.PhysicsBody, detached);
                detached.Dispose();
                break;
        }

        scene.Run(64, TkVector3.Zero);
        float endY = rider.WorldTransform.Origin.Y;
        _output.WriteLine($"{exit}, freeze {freeze}: rider at y {endY:0.000} one second later, state {rider.ActivationState}");
        return endY;
    }

    public enum PlayerExit
    {
        Disconnect,
        Teleport,
        Park,
        Detach
    }

    // Only what rests on the player's top wakes: a cube frozen beside the player's head (on a
    // pedestal next to it) and the floor cubes around the player stay frozen.
    [Fact]
    public void PlayerTeleport_WakesOnlyTheCubeOnItsHead()
    {
        using Scene scene = Scene.Create(freezeEnabled: true);
        Player player = scene.AddPlayer(new TkVector3(8.5f, 1.54f, 8.5f));
        RigidBody pedestal = scene.AddCube(10, 1, 8);
        RigidBody besideHead = scene.AddCube(10, 2, 8);
        RigidBody floorNeighbour = scene.AddCube(8, 1, 10);
        scene.Run(SettleTicks, TkVector3.Zero);
        RigidBody rider = scene.AddCube(8, 2, 8);
        scene.Run(SettleTicks, TkVector3.Zero);
        Assert.True(scene.Physics.CubeRest.IsFrozen(rider));
        Assert.True(scene.Physics.CubeRest.IsFrozen(besideHead));
        long wokenBefore = scene.Physics.CubeRest.TotalWoken;

        player.SetWorldPosition(new TkVector3(16.5f, 1.54f, 16.5f));

        Assert.False(scene.Physics.CubeRest.IsFrozen(rider));
        Assert.True(scene.Physics.CubeRest.IsFrozen(pedestal));
        Assert.True(scene.Physics.CubeRest.IsFrozen(besideHead));
        Assert.True(scene.Physics.CubeRest.IsFrozen(floorNeighbour));
        Assert.Equal(wokenBefore + 1, scene.Physics.CubeRest.TotalWoken);
    }

    // ---- Moving a frozen support in place ----

    // Map.UpdateCubePosition moves a cube's body in place (WorldPhysicsBinder
    // .TryMoveCompatibilityCubeBody) with zero velocity, so no mover rule fires; the cube frozen on
    // top of it must still fall instead of floating over the emptied cell.
    [Fact]
    public void MovingAFrozenSupportThroughTheMap_WakesItsRider_WhichFalls()
    {
        using Scene scene = Scene.Create(freezeEnabled: true);
        RigidBody support = scene.AddCube(8, 1, 8);
        RigidBody rider = scene.AddCube(8, 2, 8);
        RigidBody neighbour = scene.AddCube(9, 1, 8);
        scene.AddPlayer(new TkVector3(2.5f, 1.54f, 2.5f));
        scene.Run(SettleTicks, TkVector3.Zero);
        Assert.True(scene.Physics.CubeRest.IsFrozen(support));
        Assert.True(scene.Physics.CubeRest.IsFrozen(rider));
        float riderY = rider.WorldTransform.Origin.Y;
        Cube cube = scene.CubeAt(8, 1, 8);

        scene.Map.UpdateCubePosition(cube, cube.Position + new TkVector3(0f, 0f, 4f));

        Assert.False(scene.Physics.CubeRest.IsFrozen(rider));
        Assert.True(scene.Physics.CubeRest.IsFrozen(neighbour));
        scene.Run(SettleTicks, TkVector3.Zero);
        float fell = riderY - rider.WorldTransform.Origin.Y;
        _output.WriteLine($"rider fell {fell:0.000} m; frozen again {scene.Physics.CubeRest.IsFrozen(rider)}");
        Assert.InRange(fell, 0.9f, 1.1f);
        Assert.Empty(scene.Map.FindInvariantViolations());
    }

    // PhysicsSyncSystem.ResetBodyToCenter teleports a body back to its accepted center and
    // activates it. When the body is a frozen support, the freezer's next pass sees it activated
    // away from where it froze and wakes the cubes on its old place.
    [Fact]
    public void ResettingAFrozenSupportToAnotherCenter_WakesItsRiderBeforeTheNextStep()
    {
        using Scene scene = Scene.Create(freezeEnabled: true);
        RigidBody support = scene.AddCube(8, 1, 8);
        RigidBody rider = scene.AddCube(8, 2, 8);
        RigidBody neighbour = scene.AddCube(9, 1, 8);
        scene.AddPlayer(new TkVector3(2.5f, 1.54f, 2.5f));
        scene.Run(SettleTicks, TkVector3.Zero);
        Assert.True(scene.Physics.CubeRest.IsFrozen(support));
        Assert.True(scene.Physics.CubeRest.IsFrozen(rider));
        float riderY = rider.WorldTransform.Origin.Y;
        BulletSharp.Math.Vector3 origin = support.WorldTransform.Origin;
        TkVector3 center = new(origin.X, origin.Y, origin.Z + 4f);

        System.Reflection.MethodInfo reset = typeof(PhysicsSyncSystem).GetMethod(
            "ResetBodyToCenter",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        reset.Invoke(null, [support, center]);
        scene.Physics.StepSimulationFixed(TickSeconds);

        Assert.False(scene.Physics.CubeRest.IsFrozen(support));
        Assert.False(scene.Physics.CubeRest.IsFrozen(rider));
        Assert.True(scene.Physics.CubeRest.IsFrozen(neighbour));
        for (int tick = 0; tick < SettleTicks; tick++)
        {
            scene.Physics.StepSimulationFixed(TickSeconds);
        }

        float fell = riderY - rider.WorldTransform.Origin.Y;
        _output.WriteLine($"rider fell {fell:0.000} m");
        Assert.InRange(fell, 0.9f, 1.1f);
    }

    // Activate(true) on a frozen cube that did not move still only unfreezes that cube.
    [Fact]
    public void ActivatingAFrozenSupportInPlace_LeavesItsRiderFrozen()
    {
        using Scene scene = Scene.Create(freezeEnabled: true);
        RigidBody support = scene.AddCube(8, 1, 8);
        RigidBody rider = scene.AddCube(8, 2, 8);
        scene.AddPlayer(new TkVector3(2.5f, 1.54f, 2.5f));
        scene.Run(SettleTicks, TkVector3.Zero);
        Assert.True(scene.Physics.CubeRest.IsFrozen(rider));

        support.Activate(true);
        scene.Physics.StepSimulationFixed(TickSeconds);

        Assert.False(scene.Physics.CubeRest.IsFrozen(support));
        Assert.True(scene.Physics.CubeRest.IsFrozen(rider));
    }

    // ---- Flush seams at large coordinates, and a mover resting with a positive gap ----

    // A box resting cell-aligned on a frozen row has its front face flush with the next cube's
    // back face, which the flush-seam rule wakes (CubeRestFreezerTests.PlayerStartingCellAligned).
    // "Flush" is up to float rounding: at |x| >= 2048 one ulp exceeds the old absolute 1e-4
    // tolerance, so a face one ulp off was not flush any more and the row stayed a wall. The
    // mover here is one ulp right of cell-aligned and rests a hair into the row.
    [Theory]
    [InlineData(0f, -0.001f)]
    [InlineData(4096f, -0.001f)]
    [InlineData(-8192f, -0.001f)]
    [InlineData(30000f, -0.001f)]
    [InlineData(30000f, -0.0001f)]
    public void FlushSeamAhead_WakesTheRow_AtAnyCoordinate(float originX, float verticalGap)
    {
        using PhysicsIntegrator physics = new();
        physics.CubeRest.Enabled = true;
        RigidBody[] row = FreezeRawRow(physics, originX, count: 3);
        RigidBody mover = AddRawMover(physics, MathF.BitIncrement(originX + 0.5f), 1.5f + verticalGap, 0.5f);
        mover.GetAabb(out BulletSharp.Math.Vector3 moverMin, out BulletSharp.Math.Vector3 moverMax);
        row[1].GetAabb(out BulletSharp.Math.Vector3 aheadMin, out _);
        float seamGap = aheadMin.X - moverMax.X;
        _output.WriteLine($"x {originX}: seam gap {seamGap:E2}, mover bottom {moverMin.Y:0.0000}");
        Assert.True(MathF.Abs(seamGap) < 0.01f);
        if (MathF.Abs(originX) >= 4096f)
        {
            // The case the absolute tolerance missed.
            Assert.True(MathF.Abs(seamGap) > 1e-4f, $"seam gap {seamGap}");
        }

        mover.LinearVelocity = new BulletSharp.Math.Vector3(4f, 0f, 0f);
        physics.CubeRest.WakeTouchedBy(mover, TickSeconds);

        Assert.True(physics.CubeRest.IsFrozen(row[0]), "the cube under the mover is a support");
        Assert.False(physics.CubeRest.IsFrozen(row[1]));
        Assert.False(physics.CubeRest.IsFrozen(row[2]));
    }

    // A mover flush with the seam but a centimetre above the row has no box-box contact with it
    // (Bullet reports none for separated boxes), so the seam is no wall yet and nothing wakes;
    // once it has settled into the row, the next pass wakes the seam. Waking on the gap already
    // cascaded through cell-aligned stacks (see CubeRestFreezer.IsPushedOrCarried).
    [Theory]
    [InlineData(0f)]
    [InlineData(4096f)]
    public void FlushSeamAhead_OfAMoverWithAPositiveGap_WakesOnceItSettlesIn(float originX)
    {
        using PhysicsIntegrator physics = new();
        physics.CubeRest.Enabled = true;
        RigidBody[] row = FreezeRawRow(physics, originX, count: 3);
        float x = MathF.BitIncrement(originX + 0.5f);
        RigidBody mover = AddRawMover(physics, x, 1.51f, 0.5f);
        mover.LinearVelocity = new BulletSharp.Math.Vector3(4f, 0f, 0f);

        physics.CubeRest.WakeTouchedBy(mover, TickSeconds);
        Assert.All(row, body => Assert.True(physics.CubeRest.IsFrozen(body)));

        mover.WorldTransform = BulletSharp.Math.Matrix.Translation(x, 1.499f, 0.5f);
        physics.CubeRest.WakeTouchedBy(mover, TickSeconds);
        Assert.True(physics.CubeRest.IsFrozen(row[0]));
        Assert.False(physics.CubeRest.IsFrozen(row[1]));
        Assert.False(physics.CubeRest.IsFrozen(row[2]));
    }

    // The tolerance stays a few ulps: a mover two centimetres short of the seam does not wake the
    // row, at small or large coordinates.
    [Theory]
    [InlineData(0f)]
    [InlineData(4096f)]
    [InlineData(30000f)]
    public void SeamAheadWithARealGap_StaysFrozen_AtAnyCoordinate(float originX)
    {
        using PhysicsIntegrator physics = new();
        physics.CubeRest.Enabled = true;
        RigidBody[] row = FreezeRawRow(physics, originX, count: 3);
        RigidBody mover = AddRawMover(physics, originX + 0.48f, 1.499f, 0.5f);
        mover.LinearVelocity = new BulletSharp.Math.Vector3(0.2f, 0f, 0f);

        physics.CubeRest.WakeTouchedBy(mover, TickSeconds);

        Assert.All(row, body => Assert.True(physics.CubeRest.IsFrozen(body)));
    }

    // A row of unit cubes along +x from (originX, 0, 0), frozen where they are placed (no gravity,
    // so their boxes are exactly the placed ones).
    private static RigidBody[] FreezeRawRow(PhysicsIntegrator physics, float originX, int count)
    {
        physics.World.Gravity = BulletSharp.Math.Vector3.Zero;
        RigidBody[] row = new RigidBody[count];
        for (int i = 0; i < count; i++)
        {
            TkVector3 center = new(originX + 0.5f + i, 0.5f, 0.5f);
            BoxShape shape = new(0.5f) { Margin = 0f };
            BulletSharp.Math.Vector3 inertia = shape.CalculateLocalInertia(1f);
            using RigidBodyConstructionInfo info = new(
                1f,
                new DefaultMotionState(BulletSharp.Math.Matrix.Translation(center.X, center.Y, center.Z)),
                shape,
                inertia);
            RigidBody body = new(info)
            {
                UserObject = (new Cube(isEmpty: false, isSolid: true) { Position = center }, center),
                AngularFactor = BulletSharp.Math.Vector3.Zero
            };
            body.SetSleepingThresholds(0.05f, 0.05f);
            physics.AddMapBodyToWorld(body);
            physics.MapCubeBodies.Add(body);
            physics.DynamicMapCubeBodies.Add(body);
            row[i] = body;
        }

        for (int tick = 0; tick < 48; tick++)
        {
            physics.StepSimulationFixed(TickSeconds);
        }

        Assert.All(row, body => Assert.True(physics.CubeRest.IsFrozen(body)));
        return row;
    }

    // A player body added after the row froze and never stepped, so it is exactly where it is put.
    private static RigidBody AddRawMover(PhysicsIntegrator physics, float x, float y, float z)
    {
        RigidBody mover = new PhysicsFactory().CreatePlayerBody(new BulletSharp.Math.Vector3(x, y, z));
        physics.AddSessionPlayerBody(mover);
        return mover;
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

        public Player AddPlayer(TkVector3 position, bool primary = false)
        {
            RigidBody body = new PhysicsFactory().CreatePlayerBody(position.ToBullet());
            if (primary)
            {
                Physics.RegisterPlayerBody(body, PhysicsBodyOwnership.IntegratorOwned);
            }
            else
            {
                Physics.AddSessionPlayerBody(body);
            }

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

        // Stops updating the player (its body is parked or detached); the integrator keeps or
        // hands back the body.
        public void StopDriving(Player player)
        {
            _players.Remove(player);
            player.Dispose();
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
