using System.Reflection;
using BulletSharp;
using Xunit;
using Xunit.Abstractions;
using TkVector2 = OpenTK.Mathematics.Vector2;
using TkVector3 = OpenTK.Mathematics.Vector3;
using Dream.Multiplayer.Diagnostics;
using Dream.Simulation.Physics;
using Dream.Simulation.Players;
using Dream.Simulation.World;

namespace Dream.Tests.Integration;

// MEM-11 gives every Player one native-backed contact callback, shared by its collision report
// and its motor's grounded probe, and disposed once in Player.Dispose. Several players share one
// physics world here and, every tick with the diagnostics log captured, each runs a collision
// check and (on alternating ticks) a grounded probe through its reused callback. Before every
// update the test computes what a fresh per-call callback (the pre-MEM-11 behaviour) reports for
// that player, and the update must produce exactly that: the same jump decision and the same
// collision report text. Players are then disposed in a random order while the rest keep
// playing, and each callback is disposed once with no exception.
public class PlayerSharedContactCallbackIntegrationTests(ITestOutputHelper output)
{
    private const float TickSeconds = 1f / 60f;
    private const int Ticks = 360;
    private const float PlayerHalfHeight = 0.5f;
    private const float GroundEpsilon = 0.08f;

    private static readonly FieldInfo PlayerCallbackField = typeof(Player).GetField("_contactCallback", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo PlayerMotorField = typeof(Player).GetField("_motor", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo MotorCallbackField = typeof(PlayerMotor).GetField("_contactCallback", BindingFlags.Instance | BindingFlags.NonPublic)!;

    // Floor top is y = 1. Cubes occupy whole cells (cell (3,1,3) spans x,z in [3,4], y in [1,2]).
    private static readonly TkVector3[] CubeCells =
    [
        new(3f, 1f, 3f), new(4f, 1f, 3f), new(3f, 1f, 4f), new(7f, 1f, 7f), new(7f, 2f, 7f), new(1f, 1f, 8f)
    ];

    private static readonly (TkVector3 Start, TkVector3 Walk)[] Players =
    [
        (new(3.5f, 2.5f, 3.5f), TkVector3.Zero),          // on a cube
        (new(5.6f, 1.5f, 3.5f), new(-1f, 0f, 0f)),        // walks into the side of a cube
        (new(7.5f, 4.5f, 7.5f), TkVector3.Zero),          // on a two-cube stack
        (new(8.5f, 1.5f, 2.5f), new(0f, 0f, 1f)),         // walks on the floor
        (new(1.5f, 5f, 8.5f), TkVector3.Zero),            // falls onto a cube
        (new(4.45f, 2.5f, 4.45f), new(0.3f, 0f, 0.3f))    // near a cube's corner, walks off it
    ];

    [Theory]
    [InlineData(11)]
    [InlineData(29)]
    public void PlayersInOneWorld_ReusedCallbacksMatchFreshCallbacks_AndDisposeOnceInAnyOrder(int seed)
    {
        using PhysicsIntegrator physics = new();
        Map map = new(physics, width: 12, depth: 12);
        foreach (TkVector3 cell in CubeCells)
        {
            Assert.True(map.AddCube(cell, isStatic: cell.X == 7f));
        }

        PlayerSimulationTuning tuning = PlayerSimulationTuning.AuthoritativeDefault;
        List<Player> players = [];
        List<bool> wasJumpPressed = [];
        foreach ((TkVector3 start, _) in Players)
        {
            RigidBody body = new PhysicsFactory().CreatePlayerBody(start.ToBullet());
            physics.AddSessionPlayerBody(body);
            Player player = new(map, physics, body, start) { CollisionReportCooldown = 0f };
            players.Add(player);
            wasJumpPressed.Add(false);
        }

        // Each player's motor probes with the player's own callback, and no two players share one.
        HashSet<Player.PlayerContactCallback> callbacks = new(ReferenceEqualityComparer.Instance);
        foreach (Player player in players)
        {
            Player.PlayerContactCallback callback = CallbackOf(player);
            Assert.Same(callback, MotorCallbackField.GetValue(PlayerMotorField.GetValue(player)));
            Assert.Same(player.PhysicsBody, callback.PlayerBody);
            Assert.True(callbacks.Add(callback));
        }

        Random random = new(seed);
        Stats stats = default;
        int tick = 0;
        for (; tick < Ticks; tick++)
        {
            RunTick(tick, physics, players, wasJumpPressed, Enumerable.Range(0, players.Count).ToList(), tuning, ref stats);
        }

        // Dispose in a random order; the others keep going for a while after each disposal.
        List<int> alive = Enumerable.Range(0, players.Count).ToList();
        foreach (int victim in Enumerable.Range(0, players.Count).OrderBy(_ => random.Next()).ToList())
        {
            Player player = players[victim];
            Player.PlayerContactCallback callback = CallbackOf(player);
            Assert.False(callback.IsDisposed);
            player.Dispose();
            Assert.True(callback.IsDisposed);
            player.Dispose();
            Assert.True(callback.IsDisposed);
            Assert.Empty(callback.Collisions);
            Assert.Throws<ObjectDisposedException>(() => player.Update(TickSeconds, Idle(), tuning));
            Assert.Throws<ObjectDisposedException>(() => callback.RunContactTest(physics.World));
            alive.Remove(victim);
            foreach (int other in alive)
            {
                Assert.False(CallbackOf(players[other]).IsDisposed, $"disposing player {victim} disposed player {other}'s callback.");
            }

            for (int i = 0; i < 20; i++, tick++)
            {
                RunTick(tick, physics, players, wasJumpPressed, alive, tuning, ref stats);
            }
        }

        foreach (Player player in players)
        {
            physics.RemoveSessionPlayerBody(player.PhysicsBody);
        }

        output.WriteLine(
            $"seed {seed}: probes {stats.Probes} (grounded {stats.Grounded}), collision checks {stats.Checks}, " +
            $"reports {stats.Reports}");
        Assert.True(stats.Grounded > 0 && stats.Grounded < stats.Probes, "The probes never disagreed: the scenario is too static.");
        Assert.True(stats.Reports > 0, "No collision report was written.");
    }

    private static void RunTick(
        int tick,
        PhysicsIntegrator physics,
        List<Player> players,
        List<bool> wasJumpPressed,
        List<int> alive,
        PlayerSimulationTuning tuning,
        ref Stats stats)
    {
        foreach (int index in alive)
        {
            Player player = players[index];
            RigidBody body = player.PhysicsBody;

            // Grounded probes on alternating ticks (offset per player): a jump press is an edge.
            bool jump = (tick + index) % 2 == 0;
            PlayerSimulationInput input = new(
                new MovementCommand(Players[index].Walk * ((tick / 90) % 2 == 0 ? 1f : -1f), JumpPressed: jump),
                TkVector2.Zero,
                AllowGameplayInput: true,
                AllowLook: true);

            // What fresh callbacks report right now (Update does not move the body before it probes).
            TkVector3 position = player.GetWorldPosition();
            List<Player.CollisionData> contacts = FreshContacts(physics, body);
            bool expectedGrounded = contacts.Any(contact => contact.ContactPoint.Y <= position.Y - PlayerHalfHeight + GroundEpsilon);
            string? expectedReport = ExpectedReport(contacts, player.LastReportedPosition);
            bool probes = jump && !wasJumpPressed[index];
            float velocityYBefore = body.LinearVelocity.Y;

            List<(string Category, string Message)> lines = [];
            using (NetworkDiagnosticsLog.CaptureOnCurrentThread((category, message) => lines.Add((category, message))))
            {
                player.Update(TickSeconds, input, tuning);
            }

            wasJumpPressed[index] = jump;
            stats.Checks++;
            if (probes)
            {
                stats.Probes++;
                stats.Grounded += expectedGrounded ? 1 : 0;
                float expectedY = expectedGrounded ? tuning.JumpVelocity : velocityYBefore;
                Assert.True(
                    body.LinearVelocity.Y == expectedY,
                    $"tick {tick} player {index}: velocity.y {body.LinearVelocity.Y}, a fresh probe says grounded={expectedGrounded} (expected {expectedY}).");
            }

            List<string> reports = lines.Where(line => line.Category == Player.CollisionReportCategory).Select(line => line.Message).ToList();
            if (expectedReport == null)
            {
                Assert.True(reports.Count == 0, $"tick {tick} player {index}: unexpected report {string.Join("|", reports)}");
            }
            else
            {
                stats.Reports++;
                Assert.True(reports.Count == 1, $"tick {tick} player {index}: {reports.Count} reports, expected one.");
                Assert.Equal(expectedReport, reports[0]);
            }

            // The reused callback keeps no contacts between uses.
            Assert.Empty(CallbackOf(player).Collisions);
        }

        physics.StepSimulationFixed(TickSeconds);
    }

    private static List<Player.CollisionData> FreshContacts(PhysicsIntegrator physics, RigidBody body)
    {
        using Player.PlayerContactCallback fresh = new(body);
        physics.World.ContactTest(body, fresh);
        return [.. fresh.Collisions];
    }

    // The report Player writes, built from a fresh callback's contacts.
    private static string? ExpectedReport(List<Player.CollisionData> contacts, TkVector3 playerPosition)
    {
        List<TkVector3> cubeCenters = [];
        foreach (Player.CollisionData contact in contacts)
        {
            if (contact.CollisionObject.UserObject is ValueTuple<Cube, TkVector3> cubeBody)
            {
                cubeCenters.Add(cubeBody.Item2);
            }
        }

        if (cubeCenters.Count == 0)
        {
            return null;
        }

        System.Text.StringBuilder report = new();
        report.Append("\n=== Collision Report ===\n");
        foreach (TkVector3 center in cubeCenters)
        {
            TkVector3 grid = new(
                (int)Math.Floor(center.X + 1e-6f),
                (int)Math.Floor(center.Y + 1e-6f),
                (int)Math.Floor(center.Z + 1e-6f));
            report.Append($"Colliding with cube at: [{grid.X}, {grid.Y}, {grid.Z}]\n");
            report.Append($"Cube center: {center}\n");
            report.Append($"Player position: {playerPosition}\n");
        }

        report.Append("========================");
        return report.ToString();
    }

    private static Player.PlayerContactCallback CallbackOf(Player player)
    {
        return (Player.PlayerContactCallback)PlayerCallbackField.GetValue(player)!;
    }

    private static PlayerSimulationInput Idle()
    {
        return new PlayerSimulationInput(new MovementCommand(TkVector3.Zero), TkVector2.Zero, AllowGameplayInput: true, AllowLook: true);
    }

    private record struct Stats(int Probes, int Grounded, int Checks, int Reports);
}
