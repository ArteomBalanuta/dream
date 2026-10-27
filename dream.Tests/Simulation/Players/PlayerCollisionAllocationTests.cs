using BulletSharp;
using Xunit;
using Xunit.Abstractions;
using TkVector2 = OpenTK.Mathematics.Vector2;
using TkVector3 = OpenTK.Mathematics.Vector3;
using Dream.Multiplayer.Diagnostics;
using Dream.Simulation.Physics;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using Dream.Tests.Utilities;

namespace Dream.Tests.Simulation.Players;

// MEM-11: Player's collision report, the motor's grounded probe and the presentation camera copy.
// Before (bytes, Debug, measured by these tests on the original code): one 0.5 s collision period
// (30 idle Player.Update calls) 448 on the floor and 1368 on a cube (the console report included);
// a jump plus release through PlayerMotor.Update 504 (IsGrounded's callback, list, closure and
// enumerator), the same pair without a jump 0; Camera.Clone 80.
// After: 0 and 0 with the diagnostics log off (the report is diagnostics-only, so its contact
// test no longer runs); 80 for the jump pair on the floor and 0 in the air (the 80 B are
// BulletSharp's own per-contact wrappers, see below); Camera.CopyTo and
// Player.CopyPresentationCameraTo 0. With the diagnostics log on, a check is 80 B on the floor
// (wrappers only, no list or callback) and 1368 B on a cube (wrappers plus the report text).
public class PlayerCollisionAllocationTests
{
    private const float TickSeconds = 1f / 60f;
    private const int TicksPerCollisionPeriod = 30;
    private const long ReportTextBudgetBytes = 1536;

    // Floor cells are y = 0 (top at y = 1). One cube sits in cell (3, 1, 3): x and z in [3, 4], top y = 2.
    private static readonly TkVector3 CubeCell = new(3f, 1f, 3f);
    private static readonly TkVector3 OnFloor = new(5.5f, 1.5f, 5.5f);
    private static readonly TkVector3 InAir = new(5.5f, 4f, 5.5f);
    private static readonly TkVector3 OnCube = new(3.5f, 2.5f, 3.5f);
    // Player box is 1 wide: x in [3.9, 4.9] overlaps the cube's top edge by 0.1.
    private static readonly TkVector3 OnCubeEdge = new(4.4f, 2.5f, 3.5f);
    // Level with the cube top but x in [4.1, 5.1]: off the edge, nothing underneath.
    private static readonly TkVector3 PastCubeEdge = new(4.6f, 2.5f, 3.5f);

    private static readonly PlayerSimulationInput Idle = new(
        new MovementCommand(TkVector3.Zero),
        TkVector2.Zero,
        AllowGameplayInput: true,
        AllowLook: true);

    private static readonly PlayerSimulationInput Jump = Idle with
    {
        Movement = new MovementCommand(TkVector3.Zero, JumpPressed: true)
    };

    private readonly ITestOutputHelper _output;

    public PlayerCollisionAllocationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CollisionCheckPeriod_DiagnosticsOff_AllocatesNothing(bool onCube)
    {
        Assert.False(NetworkDiagnosticsLog.IsEnabled, "This test needs DREAM_NET_DIAGNOSTICS unset.");
        using PhysicsIntegrator physics = new();
        Map map = CreateMap(physics);
        using Player player = CreatePlayer(physics, map, onCube ? OnCube : OnFloor);
        PlayerSimulationTuning tuning = PlayerSimulationTuning.AuthoritativeDefault;

        long bytes = AllocationAssert.AtMost(0, () => RunCollisionPeriod(player, tuning), iterations: 4);

        _output.WriteLine($"0.5 s collision period, diagnostics off, on {(onCube ? "cube" : "floor")}: {bytes} B");
    }

    [Fact]
    public void CollisionCheck_DiagnosticsOn_NoCubeContact_ReusesCallbackAndList()
    {
        using PhysicsIntegrator physics = new();
        Map map = CreateMap(physics);
        using Player player = CreatePlayer(physics, map, OnFloor);
        player.CollisionReportCooldown = 0f; // check on every update
        long bulletBytes = MeasureBulletContactWrapperBytes(physics, player.PhysicsBody);
        PlayerSimulationTuning tuning = PlayerSimulationTuning.AuthoritativeDefault;
        int reports = 0;

        long bytes;
        using (NetworkDiagnosticsLog.CaptureOnCurrentThread((_, _) => reports++))
        {
            bytes = AllocationAssert.AtMost(bulletBytes, () => player.Update(TickSeconds, Idle, tuning));
        }

        _output.WriteLine(
            $"Collision check with diagnostics on, floor only: {bytes} B per check " +
            $"(BulletSharp's contact wrappers {bulletBytes} B)");
        Assert.Equal(0, reports);
    }

    [Fact]
    public void CollisionCheck_DiagnosticsOn_OnCube_AllocatesOnlyTheReportText()
    {
        using PhysicsIntegrator physics = new();
        Map map = CreateMap(physics);
        using Player player = CreatePlayer(physics, map, OnCube);
        player.CollisionReportCooldown = 0f;
        long bulletBytes = MeasureBulletContactWrapperBytes(physics, player.PhysicsBody);
        PlayerSimulationTuning tuning = PlayerSimulationTuning.AuthoritativeDefault;
        int reports = 0;
        string? lastReport = null;

        long bytes;
        using (NetworkDiagnosticsLog.CaptureOnCurrentThread((_, message) =>
               {
                   reports++;
                   lastReport = message;
               }))
        {
            bytes = AllocationAssert.MeasureBytesPerIteration(() => player.Update(TickSeconds, Idle, tuning));
        }

        long reportTextBytes = AllocationAssert.MeasureBytesPerIteration(() => GC.KeepAlive(new string(lastReport!.AsSpan())));
        _output.WriteLine(
            $"Collision check with diagnostics on, on a cube: {bytes} B per check " +
            $"(BulletSharp's contact wrappers {bulletBytes} B, final report string {reportTextBytes} B)");
        Assert.True(reports > 0);
        // Besides BulletSharp's wrappers, only the report text: the builder, the formatted
        // vectors and the final string. No list or callback.
        Assert.True(
            bytes <= bulletBytes + ReportTextBudgetBytes,
            $"Allocated {bytes} B per check; budget is {bulletBytes} + {ReportTextBudgetBytes} B.");
    }

    [Fact]
    public void CollisionCheck_DiagnosticsOn_OnCube_ReportsTheCubeLikeTheConsoleReportDid()
    {
        using PhysicsIntegrator physics = new();
        Map map = CreateMap(physics);
        using Player player = CreatePlayer(physics, map, OnCube);
        PlayerSimulationTuning tuning = PlayerSimulationTuning.AuthoritativeDefault;
        List<(string Category, string Message)> lines = [];

        using (NetworkDiagnosticsLog.CaptureOnCurrentThread((category, message) => lines.Add((category, message))))
        {
            // Just short of one 0.5 s period: no report yet. A few ticks more: exactly one.
            for (int i = 0; i < TicksPerCollisionPeriod - 2; i++)
            {
                player.Update(TickSeconds, Idle, tuning);
            }

            Assert.Empty(lines);
            for (int i = 0; i < 4; i++)
            {
                player.Update(TickSeconds, Idle, tuning);
            }
        }

        (string category, string message) = Assert.Single(lines);
        Assert.Equal(Player.CollisionReportCategory, category);
        TkVector3 cubeCenter = new(3.5f, 1.5f, 3.5f);
        string expected =
            "\n=== Collision Report ===\n" +
            "Colliding with cube at: [3, 1, 3]\n" +
            $"Cube center: {cubeCenter}\n" +
            $"Player position: {player.LastReportedPosition}\n" +
            "========================";
        Assert.Equal(expected, message);
    }

    [Fact]
    public void CollisionCheck_DiagnosticsOn_OnFloorOnly_ReportsNothing()
    {
        using PhysicsIntegrator physics = new();
        Map map = CreateMap(physics);
        using Player player = CreatePlayer(physics, map, OnFloor);
        int reports = 0;

        using (NetworkDiagnosticsLog.CaptureOnCurrentThread((_, _) => reports++))
        {
            RunCollisionPeriod(player, PlayerSimulationTuning.AuthoritativeDefault);
            RunCollisionPeriod(player, PlayerSimulationTuning.AuthoritativeDefault);
        }

        Assert.Equal(0, reports);
    }

    // IsGrounded keeps the Bullet contact test, so BulletSharp still wraps each contact point it
    // reports (a ManifoldPoint and two CollisionObjectWrappers, newed in
    // ContactResultCallback.AddSingleResultUnmanaged, 80 B for the floor contact). That is inside
    // the binding; what Dream allocated around it (callback, list, closure, enumerator) is gone,
    // and a probe with no contact allocates nothing.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PlayerJump_GroundedProbe_AllocatesOnlyBulletSharpContactWrappers(bool onFloor)
    {
        TkVector3 position = onFloor ? OnFloor : InAir;
        using PhysicsIntegrator physics = new();
        Map map = CreateMap(physics);
        using Player player = CreatePlayer(physics, map, position);
        player.CollisionReportCooldown = float.MaxValue;
        long bulletBytes = MeasureBulletContactWrapperBytes(physics, player.PhysicsBody);
        PlayerSimulationTuning tuning = PlayerSimulationTuning.AuthoritativeDefault;
        bool jumped = false;

        long bytes = AllocationAssert.AtMost(bulletBytes, () =>
        {
            ResetBody(player.PhysicsBody, position);
            player.Update(TickSeconds, Jump, tuning);
            jumped = player.PhysicsBody.LinearVelocity.Y > 0f;
            player.Update(TickSeconds, Idle, tuning);
        });

        _output.WriteLine(
            $"Player jump and release {(onFloor ? "on the floor" : "in the air")}: {bytes} B " +
            $"(BulletSharp's contact wrappers {bulletBytes} B)");
        Assert.Equal(onFloor, jumped);
        if (!onFloor)
        {
            Assert.Equal(0, bytes);
        }
    }

    [Fact]
    public void StandaloneMotorJump_GroundedProbe_AllocatesOnlyBulletSharpContactWrappers()
    {
        using PhysicsIntegrator physics = new();
        Map map = CreateMap(physics);
        RigidBody body = AddSessionBody(physics, OnFloor);
        PlayerMotor motor = new(physics);
        long bulletBytes = MeasureBulletContactWrapperBytes(physics, body);
        PlayerSimulationTuning tuning = PlayerSimulationTuning.AuthoritativeDefault;
        bool jumped = false;

        long bytes = AllocationAssert.AtMost(bulletBytes, () =>
        {
            body.LinearVelocity = BulletSharp.Math.Vector3.Zero;
            motor.Update(body, OnFloor, new MovementCommand(TkVector3.Zero, JumpPressed: true), tuning, TickSeconds);
            jumped = body.LinearVelocity.Y > 0f;
            motor.Update(body, OnFloor, new MovementCommand(TkVector3.Zero), tuning, TickSeconds);
        });

        _output.WriteLine($"PlayerMotor jump and release (IsGrounded): {bytes} B (BulletSharp's contact wrappers {bulletBytes} B)");
        Assert.True(jumped);
        GC.KeepAlive(map);
    }

    // Expected values were checked against the original IsGrounded (new callback + Any) before MEM-11.
    [Theory]
    [InlineData("floor", true)]
    [InlineData("air", false)]
    [InlineData("cube", true)]
    [InlineData("cube-edge", true)]
    [InlineData("past-cube-edge", false)]
    public void Jump_GroundedResult_IsUnchanged(string place, bool expectedGrounded)
    {
        TkVector3 position = place switch
        {
            "floor" => OnFloor,
            "air" => InAir,
            "cube" => OnCube,
            "cube-edge" => OnCubeEdge,
            "past-cube-edge" => PastCubeEdge,
            _ => throw new ArgumentOutOfRangeException(nameof(place))
        };

        using PhysicsIntegrator physics = new();
        Map map = CreateMap(physics);
        PlayerSimulationTuning tuning = PlayerSimulationTuning.AuthoritativeDefault;

        // Through a Player (shared callback), twice so the reused callback is probed again.
        using Player player = CreatePlayer(physics, map, position);
        player.CollisionReportCooldown = float.MaxValue;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            ResetBody(player.PhysicsBody, position);
            player.Update(TickSeconds, Jump, tuning);
            Assert.Equal(expectedGrounded, player.PhysicsBody.LinearVelocity.Y >= tuning.JumpVelocity);
            player.Update(TickSeconds, Idle, tuning);
        }

        // Through a standalone motor on a second body at the same spot.
        using PhysicsIntegrator otherPhysics = new();
        Map otherMap = CreateMap(otherPhysics);
        RigidBody body = AddSessionBody(otherPhysics, position);
        PlayerMotor motor = new(otherPhysics);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            body.LinearVelocity = BulletSharp.Math.Vector3.Zero;
            motor.Update(body, position, new MovementCommand(TkVector3.Zero, JumpPressed: true), tuning, TickSeconds);
            Assert.Equal(expectedGrounded, body.LinearVelocity.Y >= tuning.JumpVelocity);
            motor.Update(body, position, new MovementCommand(TkVector3.Zero), tuning, TickSeconds);
        }

        GC.KeepAlive(otherMap);
    }

    [Fact]
    public void CameraCopyTo_AllocatesNothing_AndMatchesClone()
    {
        Camera source = new(new TkVector3(1f, 2f, 3f)) { MouseSensitivity = 3.25f };
        source.ApplyLookDelta(new TkVector2(140f, -35f));
        Camera target = new(TkVector3.Zero);

        long cloneBytes = AllocationAssert.MeasureBytesPerIteration(() => GC.KeepAlive(source.Clone()));
        long copyBytes = AllocationAssert.AtMost(0, () => source.CopyTo(target));

        _output.WriteLine($"Camera.Clone {cloneBytes} B, Camera.CopyTo {copyBytes} B");
        AssertSameCamera(source.Clone(), target);
    }

    [Fact]
    public void CopyPresentationCameraTo_AllocatesNothing_AndMatchesCreatePresentationCamera()
    {
        using PhysicsIntegrator physics = new();
        Map map = CreateMap(physics);
        using Player player = CreatePlayer(physics, map, OnFloor);
        player.SetFacing(new TkVector3(0.3f, -0.2f, -1f));
        player.SyncCameraFromBody(PlayerSimulationTuning.AuthoritativeDefault);
        player.PhysicsBody.LinearVelocity = new BulletSharp.Math.Vector3(2f, -1f, 3f);
        Camera target = new(TkVector3.Zero);

        long bytes = AllocationAssert.AtMost(0, () => player.CopyPresentationCameraTo(target, 0.05f));

        _output.WriteLine($"Player.CopyPresentationCameraTo: {bytes} B");
        AssertSameCamera(player.CreatePresentationCamera(0.05f), target);
    }

    [Fact]
    public void Dispose_IsIdempotent_LeavesTheBodyAlone_AndBlocksFurtherUpdates()
    {
        using PhysicsIntegrator physics = new();
        Map map = CreateMap(physics);
        Player player = CreatePlayer(physics, map, OnFloor);

        player.Dispose();
        player.Dispose();

        Assert.True(physics.IsPlayerBody(player.PhysicsBody));
        Assert.Equal(OnFloor, player.GetWorldPosition());
        Assert.Throws<ObjectDisposedException>(
            () => player.Update(TickSeconds, Idle, PlayerSimulationTuning.AuthoritativeDefault));

        // The body is still owned by its registrar and is removed exactly once.
        physics.RemoveSessionPlayerBody(player.PhysicsBody);
    }

    [Fact]
    public void PlayerContactCallback_DisposeTwice_DisposesOnce_AndRefusesFurtherTests()
    {
        using PhysicsIntegrator physics = new();
        RigidBody body = AddSessionBody(physics, OnFloor);
        Player.PlayerContactCallback callback = new(body);

        callback.RunContactTest(physics.World);
        callback.Dispose();
        callback.Dispose();

        Assert.True(callback.IsDisposed);
        Assert.Empty(callback.Collisions);
        Assert.Throws<ObjectDisposedException>(() => callback.RunContactTest(physics.World));
    }

    // One bare contact test of this body with a warmed, reused callback: what BulletSharp itself
    // allocates, the managed wrappers for every contact point it reports.
    private static long MeasureBulletContactWrapperBytes(PhysicsIntegrator physics, RigidBody body)
    {
        using Player.PlayerContactCallback probe = new(body);
        return AllocationAssert.MeasureBytesPerIteration(() => probe.RunContactTest(physics.World));
    }

    private static void RunCollisionPeriod(Player player, PlayerSimulationTuning tuning)
    {
        for (int i = 0; i < TicksPerCollisionPeriod; i++)
        {
            player.Update(TickSeconds, Idle, tuning);
        }
    }

    private static void ResetBody(RigidBody body, TkVector3 position)
    {
        BulletSharp.Math.Matrix transform = BulletSharp.Math.Matrix.Translation(position.ToBullet());
        body.WorldTransform = transform;
        body.MotionState.SetWorldTransform(ref transform);
        body.LinearVelocity = BulletSharp.Math.Vector3.Zero;
    }

    private static void AssertSameCamera(Camera expected, Camera actual)
    {
        Assert.Equal(expected.Position, actual.Position);
        Assert.Equal(expected.Front, actual.Front);
        Assert.Equal(expected.Up, actual.Up);
        Assert.Equal(expected.Right, actual.Right);
        Assert.Equal(expected.HorizontalForward, actual.HorizontalForward);
        Assert.Equal(expected.MouseSensitivity, actual.MouseSensitivity);
        Assert.Equal(expected.GetViewMatrix(), actual.GetViewMatrix());
    }

    private static Map CreateMap(PhysicsIntegrator physics)
    {
        Map map = new(physics, width: 10, depth: 10);
        Assert.True(map.AddCube(CubeCell));
        return map;
    }

    private static Player CreatePlayer(PhysicsIntegrator physics, Map map, TkVector3 position)
    {
        return new Player(map, physics, AddSessionBody(physics, position), position);
    }

    private static RigidBody AddSessionBody(PhysicsIntegrator physics, TkVector3 position)
    {
        RigidBody body = new PhysicsFactory().CreatePlayerBody(position.ToBullet());
        physics.AddSessionPlayerBody(body);
        return body;
    }
}
