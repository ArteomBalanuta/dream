using Dream.Application;
using Dream.Client.Navigation;
using Dream.Client.World;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;

namespace Dream.Tests.Client.Navigation;

// Drives a real ClientWorldSession (Bullet player body, static cubes) with the pathfinder and
// follower, the way the click-move controller does each tick.
public class PathFollowerPhysicsTests(ITestOutputHelper output)
{
    private static readonly PlayerSimulationTuning Tuning = new(
        EyeHeightOffset: 0.35f,
        MouseSensitivity: 2.5f,
        MoveSpeed: PlayerSimulationTuning.DefaultRunSpeed,
        NoclipSpeed: 150f,
        JumpVelocity: 5f);

    private static void Block(ClientWorldSession session, int x, int z, int fromY, int toY)
    {
        for (int y = fromY; y <= toY; y++)
        {
            Assert.NotNull(session.Map.TryAddCube(new Vector3(x, y, z), isStatic: true));
        }
    }

    private (bool Reached, Vector3 End, int Ticks, int Replans) Walk(
        ClientWorldSession session, NavCell goal, int maxTicks, Action<int>? onTick = null)
    {
        MapNavWorld world = new(session.Map);
        GridPathfinder pathfinder = new();
        PathFollower follower = new();
        List<NavCell> path = [];
        int replans = 0;

        bool Plan(bool viaStartCentre)
        {
            Vector3 body = session.Player.GetWorldPosition();
            if (!pathfinder.TryFindPath(world, NavCell.FromBody(body), goal, path, out _))
            {
                return false;
            }

            follower.SetPath(path, viaStartCentre);
            return true;
        }

        Assert.True(Plan(false));
        for (int tick = 0; tick < maxTicks; tick++)
        {
            onTick?.Invoke(tick);
            Vector3 body = session.Player.GetWorldPosition();
            NavStep step = follower.Step(body, world);
            if (step.NeedsReplan)
            {
                replans++;
                Plan(true);
                step = follower.Step(body, world);
            }

            if (step.Arrived)
            {
                return (true, body, tick, replans);
            }

            PlayerCommandFrame frame = new(
                new PlayerSessionId("walker"),
                (ulong)tick + 1,
                PlayerModeSwitchIntent.None,
                new PlayerMoveIntent(step.Direction == Vector3.Zero ? 0f : 1f, 0f, JumpPressed: step.Jump),
                new PlayerLookIntent(0f, 0f, step.Direction),
                PrimaryUsePressed: false);
            session.AdvancePredictionTick((ulong)tick + 1, SimulationClock.FixedDeltaSeconds, frame, Tuning);
        }

        return (false, session.Player.GetWorldPosition(), maxTicks, replans);
    }

    // The session map starts with generated terrain; the scenarios want a flat floor.
    private static ClientWorldSession NewSession()
    {
        ClientWorldSession session = new(floorSize: 24, new Vector3(2.5f, 1.5f, 12.5f), new SceneEventHub());
        for (int x = 0; x < session.Map.Width; x++)
        {
            for (int y = 1; y < session.Map.Height; y++)
            {
                for (int z = 0; z < session.Map.Depth; z++)
                {
                    Cube? cube = session.Map.TryGetCubeAtCell(x, y, z);
                    if (cube != null)
                    {
                        session.Map.RemoveCube(cube);
                    }
                }
            }
        }

        return session;
    }

    private void Check((bool Reached, Vector3 End, int Ticks, int Replans) result, Vector3 target, string label)
    {
        output.WriteLine($"{label}: reached={result.Reached} end={result.End} ticks={result.Ticks} replans={result.Replans}");
        Assert.True(result.Reached, label);
        Assert.True(Math.Abs(result.End.X - target.X) < 0.5f && Math.Abs(result.End.Z - target.Z) < 0.5f, label);
        Assert.InRange(result.End.Y, 1.2f, 1.9f);
    }

    [Fact]
    public void PinnedStepMetres_IsAFractionOfOneCruiseTick_WithRoomToSpare()
    {
        Assert.Equal(
            PathFollower.PinnedStepCruiseFraction * PathFollower.CruiseTickMetres,
            PathFollower.PinnedStepMetres,
            5);
        // A free body at cruise must clear the line by at least twice, and a body that is genuinely
        // stopped (zero travel) must sit under it.
        Assert.True(PathFollower.CruiseTickMetres >= 2f * PathFollower.PinnedStepMetres);
        Assert.True(0f < PathFollower.PinnedStepMetres);
    }

    [Fact]
    public void WalksAroundATwoHighWall()
    {
        using ClientWorldSession session = NewSession();
        for (int z = 4; z <= 16; z++)
        {
            Block(session, 8, z, 1, 2);
        }

        Check(Walk(session, new NavCell(14, 1, 12), 1500), new Vector3(14.5f, 1.5f, 12.5f), "wall detour");
    }

    [Fact]
    public void JumpsOntoAOneHighWallAndDownTheOtherSide()
    {
        using ClientWorldSession session = NewSession();
        for (int z = 0; z < 24; z++)
        {
            Block(session, 8, z, 1, 1);
        }

        Check(Walk(session, new NavCell(14, 1, 12), 1500), new Vector3(14.5f, 1.5f, 12.5f), "one-high wall");
    }

    [Fact]
    public void WalksThroughAOneWideDoor()
    {
        using ClientWorldSession session = NewSession();
        for (int z = 0; z < 24; z++)
        {
            if (z != 12)
            {
                Block(session, 8, z, 1, 2);
            }
        }

        Check(Walk(session, new NavCell(14, 1, 12), 1500), new Vector3(14.5f, 1.5f, 12.5f), "door");
    }

    [Fact]
    public void ReplansWhenACubeAppearsOnThePath()
    {
        using ClientWorldSession session = NewSession();
        bool placed = false;

        (bool Reached, Vector3 End, int Ticks, int Replans) result = Walk(session, new NavCell(14, 1, 12), 1500, tick =>
        {
            if (!placed && session.Player.GetWorldPosition().X > 5f)
            {
                placed = true;
                for (int z = 10; z <= 14; z++)
                {
                    Block(session, 8, z, 1, 2);
                }
            }
        });

        Assert.True(placed);
        Assert.True(result.Replans >= 1);
        Check(result, new Vector3(14.5f, 1.5f, 12.5f), "mid-walk cube");
    }
}
